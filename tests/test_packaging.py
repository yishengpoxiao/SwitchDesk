"""Offline packaging regressions using generated, non-product build/source fixtures."""
from pathlib import Path
import hashlib
import importlib.util
import json
import os
import shutil
import subprocess
import sys
import tempfile
import unittest
from unittest import mock
import zipfile

REPOSITORY = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(REPOSITORY / "scripts"))
spec = importlib.util.spec_from_file_location("switchdesk_package", REPOSITORY / "scripts/package.py")
packaging = importlib.util.module_from_spec(spec)
spec.loader.exec_module(packaging)
import source_inputs as source_filters


class PackagingTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory(prefix="SwitchDesk-package-tests-")
        self.addCleanup(self.temporary.cleanup)
        # Match production path canonicalization (Windows short TEMP aliases and
        # macOS /var -> /private/var); fault injection must reach the actual file.
        self.scratch = Path(self.temporary.name).resolve()
        self.root = self.scratch / "source"
        for directory in ("src/SwitchDesk.Core", "companion/include/switchdesk", "scripts", "docs/images", "tests", "third_party", ".github", "vendor/cache"):
            (self.root / directory).mkdir(parents=True, exist_ok=True)
        for name in ("README.md", "README.en.md", "LICENSE", "THIRD-PARTY-NOTICES.md", "Directory.Build.props", "global.json", "SwitchDesk.slnx", ".gitignore"):
            (self.root / name).write_text("fixture", encoding="utf-8")
        shutil.copyfile(REPOSITORY / "scripts/build-info.py", self.root / "scripts/build-info.py")
        shutil.copyfile(REPOSITORY / "scripts/source_inputs.py", self.root / "scripts/source_inputs.py")
        shutil.copyfile(REPOSITORY / "scripts/package.py", self.root / "scripts/package.py")
        (self.root / "version.json").write_text(json.dumps({"version": "fixture"}), encoding="utf-8")
        self.source_file = self.root / "src/SwitchDesk.Core/Fixture.cs"
        self.source_file.write_text("// fixture\n", encoding="utf-8")
        self.dependency = self.root / "vendor/cache/dependency.zip"
        self.dependency.write_bytes(b"locked corresponding source fixture")
        self.lock = {"sources": [{"file": self.dependency.name, "sha256": hashlib.sha256(self.dependency.read_bytes()).hexdigest()}]}
        (self.root / "build-dependencies.lock.json").write_text(json.dumps(self.lock), encoding="utf-8")
        (self.root / "vendor/cache/unlisted-private.bin").write_bytes(b"must never be packaged")
        subprocess.run([sys.executable, str(self.root / "scripts/build-info.py")], check=True, capture_output=True)
        self.windows = self.root / "artifacts/Windows"
        (self.windows / "docs/images").mkdir(parents=True)
        (self.windows / "SwitchDesk.exe").write_bytes(b"non-executable build fixture")
        (self.windows / "docs/images/fixture.png").write_bytes(b"image fixture")
        (self.windows / "debug.PDB").write_bytes(b"debug fixture")
        shutil.copyfile(self.root / "build-info.json", self.windows / "build-info.json")
        self.output = self.scratch / "packages"

    def public_setup_fixture(self):
        relative = "StreamingSetup/SDCard/atmosphere/contents/00FF0000A53BB665/exefs.nsp"
        path = self.windows / relative
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_bytes(b"synthetic public sysmodule fixture; not a game")
        metadata = {"size": path.stat().st_size, "sha256": hashlib.sha256(path.read_bytes()).hexdigest()}
        entry = {"path": relative.removeprefix("StreamingSetup/"), **metadata}
        self.write_setup_manifest([entry])
        return path, {relative: metadata}, entry

    def write_setup_manifest(self, entries):
        (self.windows / "StreamingSetup/setup-manifest.json").write_text(
            json.dumps({"version": "fixture", "files": entries}), encoding="utf-8")

    def test_sources_include_only_locked_vendor_archives_and_windows_nested_images(self):
        manifest = packaging.package(self.root, self.output)
        self.assertEqual(len(manifest), 2)
        with zipfile.ZipFile(self.output / "SwitchDesk-Source.zip") as archive:
            self.assertEqual(archive.read("SwitchDesk/vendor/cache/dependency.zip"), self.dependency.read_bytes())
            self.assertNotIn("SwitchDesk/vendor/cache/unlisted-private.bin", archive.namelist())
            self.assertIn("SwitchDesk/README.en.md", archive.namelist())
        with zipfile.ZipFile(self.output / "SwitchDesk-Windows-x64.zip") as archive:
            self.assertIn("SwitchDesk/docs/images/fixture.png", archive.namelist())
            self.assertNotIn("SwitchDesk/debug.PDB", archive.namelist())
        for row in manifest:
            self.assertEqual(row["sha256"], hashlib.sha256((self.output / row["file"]).read_bytes()).hexdigest())

    def test_stale_windows_build_is_rejected_without_outputs(self):
        self.source_file.write_text("// changed since build\n", encoding="utf-8")
        with self.assertRaisesRegex(RuntimeError, "does not match"):
            packaging.package(self.root, self.output)
        self.assertFalse(self.output.exists())

    def test_source_change_during_archiving_never_publishes_a_delivery(self):
        write = zipfile.ZipFile.write

        def mutate(archive, filename, *arguments, **keywords):
            result = write(archive, filename, *arguments, **keywords)
            if Path(filename) == self.source_file:
                self.source_file.write_text("// concurrent edit\n", encoding="utf-8")
            return result

        with mock.patch.object(zipfile.ZipFile, "write", mutate):
            with self.assertRaisesRegex(RuntimeError, "Source changed during packaging"):
                packaging.package(self.root, self.output)
        self.assertEqual(list(self.output.iterdir()), [])

    @unittest.skipUnless(os.name == "nt", "Windows directory junction regression")
    def test_directory_junctions_are_rejected_without_publishing_target_data(self):
        outside = Path(self.temporary.name) / "private"
        outside.mkdir()
        secret = outside / "session.txt"
        secret.write_bytes(b"synthetic private fixture")
        # Python symlink creation needs an elevated Windows privilege. A local
        # junction does not; PowerShell receives each path as a separate argument.
        pwsh = shutil.which("pwsh") or shutil.which("powershell")
        self.assertIsNotNone(pwsh)
        create_link = Path(self.temporary.name) / "junction.ps1"
        create_link.write_text("param([string]$Link, [string]$Target)\n"
                               "$ErrorActionPreference = 'Stop'\n"
                               "$null = New-Item -ItemType Junction -Path $Link -Target $Target\n")
        for parent in (self.root / "docs", self.windows):
            linked = parent / "linked"
            subprocess.run([pwsh, "-NoProfile", "-File", str(create_link),
                            "-Link", str(linked), "-Target", str(outside)], check=True, capture_output=True)
            try:
                self.assertTrue(linked.is_junction())
                with self.assertRaisesRegex(RuntimeError, "Linked package input"):
                    packaging.package(self.root, self.output)
                self.assertFalse(any((self.output / name).exists() for name in packaging.ARCHIVES))
                self.assertEqual(secret.read_bytes(), b"synthetic private fixture")
            finally:
                # rmdir removes only the junction, never its target tree.
                linked.rmdir()

    def test_missing_or_corrupt_dependency_does_not_publish_partial_packages(self):
        for value in (b"corrupt source", None):
            if value is None:
                self.dependency.unlink()
            else:
                self.dependency.write_bytes(value)
            with self.assertRaisesRegex(RuntimeError, "dependency source"):
                packaging.package(self.root, self.output)
            self.assertFalse(self.output.exists())

    def test_existing_delivery_is_preserved(self):
        self.output.mkdir()
        for name in packaging.ARCHIVES:
            (self.output / name).write_bytes(b"previous delivery")
        with self.assertRaises(FileExistsError):
            packaging.package(self.root, self.output)
        for name in packaging.ARCHIVES:
            self.assertEqual((self.output / name).read_bytes(), b"previous delivery")

    def test_failed_final_copy_removes_only_our_outputs_and_allows_retry(self):
        second = self.output / packaging.ARCHIVES[1]
        copy = shutil.copyfileobj

        def fail_on_second(source, target, *args, **kwargs):
            if getattr(target, "name", None) == str(second):
                target.write(b"incomplete")
                raise OSError("fixture write failure")
            return copy(source, target, *args, **kwargs)

        with mock.patch.object(shutil, "copyfileobj", fail_on_second):
            with self.assertRaisesRegex(OSError, "fixture write failure"):
                packaging.package(self.root, self.output)
        self.assertEqual(list(self.output.iterdir()), [])
        self.assertEqual(len(packaging.package(self.root, self.output)), 2)

    def test_publish_collision_preserves_external_file(self):
        first, second = [self.output / name for name in packaging.ARCHIVES[:2]]
        copy = shutil.copyfileobj

        def publish_competing_file(source, target, *args, **kwargs):
            if getattr(target, "name", None) == str(first):
                second.write_bytes(b"another delivery")
            return copy(source, target, *args, **kwargs)

        with mock.patch.object(shutil, "copyfileobj", publish_competing_file):
            with self.assertRaises(FileExistsError):
                packaging.package(self.root, self.output)
        self.assertFalse(first.exists())
        self.assertEqual(second.read_bytes(), b"another delivery")

    def test_archive_output_cannot_be_inside_its_windows_input(self):
        with self.assertRaisesRegex(ValueError, "outside"):
            packaging.package(self.root, self.windows / "packages")
        self.assertFalse((self.windows / "packages").exists())

    def test_cli_paths_and_default_delivery_stay_in_the_project_from_another_directory(self):
        elsewhere = Path(self.temporary.name) / "caller"
        elsewhere.mkdir()
        for arguments, relative in (([], "artifacts/Packages"), (["--output", "work/packages"], "work/packages")):
            completed = subprocess.run(
                [sys.executable, str(self.root / "scripts/package.py"), "--windows", "artifacts/Windows", *arguments],
                cwd=elsewhere, check=True, capture_output=True, text=True,
            )
            self.assertIn("SwitchDesk-Source.zip", completed.stdout)
            with zipfile.ZipFile(self.root / relative / "SwitchDesk-Windows-x64.zip") as archive:
                self.assertEqual(archive.read("SwitchDesk/SwitchDesk.exe"), b"non-executable build fixture")
        self.assertFalse((elsewhere / "artifacts").exists())
        self.assertFalse((elsewhere / "work").exists())

    def test_ignored_private_inputs_neither_ship_nor_change_the_source_identity(self):
        original = json.loads((self.root / "build-info.json").read_text(encoding="utf-8"))
        private = ("src/.env", "src/.env.production", "scripts/local.paths.json", "src/settings.json",
                   "tests/private.sav", "src/private.keys", "tests/Backups/private.zip", "src/ConsoleCache/private.json")
        for name in private:
            path = self.root / name
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_text("synthetic private-data fixture; not a credential", encoding="utf-8")
        manifest = packaging.package(self.root, self.output)
        self.assertEqual(len(manifest), 2)
        self.assertEqual(json.loads((self.root / "build-info.json").read_text(encoding="utf-8")), original)
        with zipfile.ZipFile(self.output / "SwitchDesk-Source.zip") as archive:
            for name in private:
                self.assertNotIn("SwitchDesk/" + name, archive.namelist())

    def test_public_environment_example_is_retained(self):
        example = self.root / "src/.env.example"
        example.write_text("PUBLIC_EXAMPLE=placeholder", encoding="utf-8")
        subprocess.run([sys.executable, str(self.root / "scripts/build-info.py")], check=True, capture_output=True)
        shutil.copyfile(self.root / "build-info.json", self.windows / "build-info.json")
        packaging.package(self.root, self.output)
        with zipfile.ZipFile(self.output / "SwitchDesk-Source.zip") as archive:
            self.assertEqual(archive.read("SwitchDesk/src/.env.example"), b"PUBLIC_EXAMPLE=placeholder")

    def test_windows_package_excludes_machine_state_but_keeps_bundled_nro(self):
        private = ("settings.json", ".env", "local.paths.json", "Logs/session.txt", "Backups/private.zip",
                   "Library/catalog.json", "Downloads/private.json", "state.db", "private.sav")
        for name in private:
            file = self.windows / name
            file.parent.mkdir(parents=True, exist_ok=True)
            file.write_bytes(b"synthetic machine-state fixture")
        setup = self.windows / "StreamingSetup/SDCard/switch/SysDVR-settings.nro"
        setup.parent.mkdir(parents=True)
        setup.write_bytes(b"pinned setup resource fixture")
        packaging.package(self.root, self.output)
        with zipfile.ZipFile(self.output / "SwitchDesk-Windows-x64.zip") as archive:
            for name in private:
                self.assertNotIn("SwitchDesk/" + name, archive.namelist())
            self.assertEqual(archive.read("SwitchDesk/StreamingSetup/SDCard/switch/SysDVR-settings.nro"), setup.read_bytes())
        for name in private:
            self.assertTrue((self.windows / name).exists(), "packaging must preserve the original file")

    def test_locked_public_setup_nsp_is_preserved_only_at_its_exact_path(self):
        payload, expected, _ = self.public_setup_fixture()
        other = ("private.nsp", "StreamingSetup/SDCard/atmosphere/contents/OTHER/exefs.nsp",
                 "StreamingSetup/SDCard/atmosphere/contents/00FF0000A53BB665/private.nsp")
        for name in other:
            target = self.windows / name
            target.parent.mkdir(parents=True, exist_ok=True)
            target.write_bytes(payload.read_bytes())
        with mock.patch.dict(source_filters.PUBLIC_SETUP_PAYLOADS, expected, clear=True):
            self.assertFalse(source_filters.include_source(payload, self.windows))
            packaging.package(self.root, self.output)
        with zipfile.ZipFile(self.output / "SwitchDesk-Windows-x64.zip") as archive:
            relative = payload.relative_to(self.windows).as_posix()
            self.assertEqual(archive.read("SwitchDesk/" + relative), payload.read_bytes())
            for name in other:
                self.assertNotIn("SwitchDesk/" + name, archive.namelist())
        with zipfile.ZipFile(self.output / "SwitchDesk-Source.zip") as archive:
            self.assertFalse(any(name.endswith((".nsp", ".nro")) for name in archive.namelist()))

    def test_locked_setup_payload_cannot_be_replaced_even_with_updated_manifest(self):
        payload, expected, entry = self.public_setup_fixture()
        payload.write_bytes(b"x" * payload.stat().st_size)
        self.write_setup_manifest([{**entry, "sha256": hashlib.sha256(payload.read_bytes()).hexdigest()}])
        with mock.patch.dict(source_filters.PUBLIC_SETUP_PAYLOADS, expected, clear=True):
            with self.assertRaisesRegex(RuntimeError, "public setup payload SHA-256"):
                packaging.package(self.root, self.output)
        self.assertFalse(self.output.exists())
        self.assertEqual(payload.read_bytes(), b"x" * expected[next(iter(expected))]["size"])

    def test_setup_manifest_requires_existing_payload_and_required_entry(self):
        for missing in ("file", "entry", "manifest"):
            with self.subTest(missing=missing):
                payload, expected, _ = self.public_setup_fixture()
                if missing == "file":
                    payload.unlink()
                elif missing == "entry":
                    self.write_setup_manifest([])
                else:
                    (self.windows / "StreamingSetup/setup-manifest.json").unlink()
                with mock.patch.dict(source_filters.PUBLIC_SETUP_PAYLOADS, expected, clear=True):
                    with self.assertRaisesRegex(RuntimeError, "setup manifest"):
                        packaging.package(self.root, self.output)
                self.assertFalse(self.output.exists())

    def test_setup_manifest_cannot_claim_a_file_that_packaging_filters_out(self):
        _, expected, entry = self.public_setup_fixture()
        private = self.windows / "StreamingSetup/SDCard/private.nsp"
        private.write_bytes(b"synthetic private package fixture")
        declared = {"path": "SDCard/private.nsp", "size": private.stat().st_size,
                    "sha256": hashlib.sha256(private.read_bytes()).hexdigest()}
        self.write_setup_manifest([entry, declared])
        with mock.patch.dict(source_filters.PUBLIC_SETUP_PAYLOADS, expected, clear=True):
            with self.assertRaisesRegex(RuntimeError, "would be excluded"):
                packaging.package(self.root, self.output)
        self.assertFalse(self.output.exists())
        self.assertTrue(private.exists())

    def test_setup_manifest_checks_lengths_and_hashes_before_packaging(self):
        for field, value in (("size", 1), ("sha256", "0" * 64)):
            with self.subTest(field=field):
                _, expected, entry = self.public_setup_fixture()
                self.write_setup_manifest([{**entry, field: value}])
                with mock.patch.dict(source_filters.PUBLIC_SETUP_PAYLOADS, expected, clear=True):
                    with self.assertRaisesRegex(RuntimeError, "setup manifest"):
                        packaging.package(self.root, self.output)
                self.assertFalse(self.output.exists())

    def test_setup_manifest_rejects_duplicate_and_unsafe_paths(self):
        for path in ("../private.nsp", "/SDCard/exefs.nsp", ".", "SDCard/../private.nsp", "SDCard\\private.nsp", None):
            with self.subTest(path=path):
                _, expected, entry = self.public_setup_fixture()
                entries = [entry, entry] if path is None else [{**entry, "path": path}]
                self.write_setup_manifest(entries)
                with mock.patch.dict(source_filters.PUBLIC_SETUP_PAYLOADS, expected, clear=True):
                    with self.assertRaisesRegex(RuntimeError, "setup manifest"):
                        packaging.package(self.root, self.output)
                self.assertFalse(self.output.exists())


if __name__ == "__main__":
    unittest.main()
