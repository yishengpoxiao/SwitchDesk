"""Offline release-gate regressions with synthetic binaries and corresponding sources."""
from pathlib import Path
import hashlib
import importlib.util
import json
import stat
import tempfile
import unittest
import zipfile

REPOSITORY = Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location("release_assembly", REPOSITORY / "scripts/assemble-release.py")
release = importlib.util.module_from_spec(spec)
spec.loader.exec_module(release)


def encoded(value):
    return json.dumps(value).encode("utf-8")


def checksum(data):
    return hashlib.sha256(data).hexdigest()


def write_zip(path, files):
    path.parent.mkdir(parents=True, exist_ok=True)
    with zipfile.ZipFile(path, "w", zipfile.ZIP_DEFLATED) as archive:
        for name, data in files.items():
            payload, mode = data if isinstance(data, tuple) else (data, 0o644)
            entry = zipfile.ZipInfo("SwitchDesk/" + name)
            entry.create_system = 3
            entry.external_attr = (stat.S_IFREG | mode) << 16
            archive.writestr(entry, payload)


def replace_member(path, name, payload, mode=None):
    with zipfile.ZipFile(path) as archive:
        files = {entry.filename.removeprefix("SwitchDesk/"):
                 (archive.read(entry), stat.S_IMODE(entry.external_attr >> 16))
                 for entry in archive.infolist()}
    if payload is None:
        del files[name]
    else:
        files[name] = (payload, mode if mode is not None else files[name][1])
    write_zip(path, files)


class ReleaseTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory(prefix="SwitchDesk-release-tests-")
        self.addCleanup(self.temporary.cleanup)
        # Match production canonicalization on macOS (/var -> /private/var).
        self.root = Path(self.temporary.name).resolve() / "project"
        self.root.mkdir()
        self.inputs = self.root / "artifacts/inputs"
        self.output = self.root / "artifacts/release"
        self.commit = "b" * 40
        self.identity = {"version": "0.3.0-dev.3", "protocol": 1, "snapshotFormat": 1,
                         "sourceDateEpoch": 1792656000, "sourceId": "a" * 12,
                         "sourceSha256": "a" * 64}
        self.source_payloads = {
            "vendor/cache/dependency.zip": b"synthetic transitive dependency source",
            "third_party/libnx.tar.gz": b"synthetic libnx source",
            "third_party/newlib.tar.gz": b"synthetic newlib source",
            "third_party/Sphaira-source.zip": b"synthetic Sphaira source",
        }
        self.lock = {"sources": [{"file": "dependency.zip", "sha256": checksum(
            self.source_payloads["vendor/cache/dependency.zip"])}]}
        for name in ("libnx", "newlib"):
            archive = "third_party/" + name + ".tar.gz"
            self.lock[name] = {"archive": archive, "sha256": checksum(self.source_payloads[archive])}
        for name, data in {**self.source_payloads, "build-info.json": encoded(self.identity),
                           "build-dependencies.lock.json": encoded(self.lock),
                           "LICENSE": b"fixture GPL license", "THIRD-PARTY-NOTICES.md": b"fixture credits",
                           "third_party/licenses/fixture.txt": b"fixture component license",
                           "docs/BUILD.md": b"fixture build instructions", "docs/STATUS.md": b"fixture limits"}.items():
            path = self.root / name
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_bytes(data)
        self.write_inputs()

    def write_inputs(self):
        common = {"build-info.json": encoded(self.identity), "LICENSE": b"fixture GPL license",
                  "THIRD-PARTY-NOTICES.md": b"fixture credits"}
        self.windows = self.inputs / "release-windows"
        self.winzip = self.windows / "SwitchDesk-Windows-x64.zip"
        self.sourcezip = self.windows / "SwitchDesk-Source.zip"
        write_zip(self.winzip, {**common, "SwitchDesk.exe": b"synthetic Windows executable"})
        write_zip(self.sourcezip, {**common, **self.source_payloads,
                                  "version.json": encoded({"version": self.identity["version"]}),
                                  "build-dependencies.lock.json": encoded(self.lock),
                                  "scripts/build-info.py": b"# synthetic source fixture\n",
                                  ".github/workflows/release.yml": b"# synthetic workflow fixture\n"})
        self.refresh_windows_sums()
        for arch in ("arm64", "x64"):
            directory = self.inputs / ("release-macos-" + arch)
            name = "SwitchDesk-macOS-" + arch + ".zip"
            executable = b"synthetic CLI for " + arch.encode("ascii")
            package = directory / name
            write_zip(package, {**common, "SwitchDesk.Cli": (executable, 0o755),
                                "licenses/dotnet-LICENSE.TXT": b"fixture runtime license",
                                "licenses/dotnet-THIRD-PARTY-NOTICES.TXT": b"fixture runtime credits"})
            manifest = {"build": self.identity, "host": "Darwin", "portableCliTestsPassed": True,
                        "portableCoreTestsPassed": True, "hardwareTested": False,
                        "assets": [{"rid": "osx-" + arch, "file": name, "size": package.stat().st_size,
                                    "sha256": checksum(package.read_bytes()), "runtimeVersion": "10.0.fixture",
                                    "executableSha256": checksum(executable), "nativeVersionTested": True,
                                    "machOHeaderChecked": True}]}
            (directory / "macos-manifest.json").write_bytes(encoded(manifest))
        companion = self.inputs / "release-companion"
        self.nro = companion / "switch/SwitchDesk/SwitchDesk.nro"
        self.nro.parent.mkdir(parents=True, exist_ok=True)
        self.nro.write_bytes(bytes(0x10) + b"NRO0" + bytes(0x6c))
        (companion / "build-info.json").write_bytes(encoded(self.identity))

    def refresh_windows_sums(self):
        (self.windows / "SHA256SUMS.txt").write_text("".join(
            f"{checksum(path.read_bytes())}  {path.name}\n" for path in (self.winzip, self.sourcezip)), encoding="utf-8")

    def mac_manifest(self, arch="arm64"):
        path = self.inputs / ("release-macos-" + arch) / "macos-manifest.json"
        return path, json.loads(path.read_text(encoding="utf-8"))

    def assemble(self):
        return release.assemble(self.root, self.inputs, self.output, self.commit)

    def assert_rejected(self, error=ValueError):
        with self.assertRaises(error):
            self.assemble()
        self.assertFalse(self.output.exists())
        self.assertFalse(list(self.output.parent.glob(".SwitchDesk-release-*")))

    def test_complete_delivery_has_five_archives_matching_sources_and_verifiable_sums(self):
        manifest = self.assemble()
        self.assertEqual(manifest["build"], self.identity)
        self.assertEqual(manifest["commit"], self.commit)
        self.assertFalse(manifest["hardwareTested"])
        self.assertEqual({row["file"] for row in manifest["assets"]}, {
            "SwitchDesk-Windows-x64.zip", "SwitchDesk-macOS-arm64.zip",
            "SwitchDesk-macOS-x64.zip", "SwitchDesk-Switch.zip", "SwitchDesk-Source.zip"})
        self.assertEqual(len(manifest["macOS"]), 2)
        for row in manifest["assets"]:
            path = self.output / row["file"]
            self.assertEqual((path.stat().st_size, checksum(path.read_bytes())), (row["size"], row["sha256"]))
            with zipfile.ZipFile(path) as archive:
                self.assertIsNone(archive.testzip())
                self.assertEqual(json.loads(archive.read("SwitchDesk/build-info.json")), self.identity)
        with zipfile.ZipFile(self.output / "SwitchDesk-Source.zip") as archive:
            for name, data in self.source_payloads.items():
                self.assertEqual(archive.read("SwitchDesk/" + name), data)
        with zipfile.ZipFile(self.output / "SwitchDesk-Switch.zip") as archive:
            self.assertEqual(archive.read("SwitchDesk/switch/SwitchDesk/SwitchDesk.nro"), self.nro.read_bytes())
            self.assertEqual(archive.read("SwitchDesk/licenses/fixture.txt"), b"fixture component license")
        sums = (self.output / "SHA256SUMS.txt").read_text(encoding="utf-8").splitlines()
        self.assertEqual(len(sums), 7)
        for line in sums:
            digest, name = line.split("  ")
            self.assertEqual(digest, checksum((self.output / name).read_bytes()))

    def test_mixed_identity_is_rejected_even_with_the_same_short_source_id(self):
        altered = {**self.identity, "sourceSha256": "a" * 12 + "c" * 52}
        for target in ("windows", "source", "mac-zip", "mac-manifest", "companion"):
            with self.subTest(target=target):
                self.write_inputs()
                if target in ("windows", "source"):
                    replace_member(self.winzip if target == "windows" else self.sourcezip,
                                   "build-info.json", encoded(altered))
                    self.refresh_windows_sums()
                elif target == "mac-zip":
                    path, manifest = self.mac_manifest()
                    package = path.parent / manifest["assets"][0]["file"]
                    replace_member(package, "build-info.json", encoded(altered))
                    manifest["assets"][0].update(size=package.stat().st_size, sha256=checksum(package.read_bytes()))
                    path.write_bytes(encoded(manifest))
                elif target == "mac-manifest":
                    path, manifest = self.mac_manifest()
                    manifest["build"] = altered
                    path.write_bytes(encoded(manifest))
                else:
                    (self.inputs / "release-companion/build-info.json").write_bytes(encoded(altered))
                self.assert_rejected()

    def test_missing_or_corrupt_locked_corresponding_source_is_rejected(self):
        for name in self.source_payloads:
            for payload in (None, b"different source archive"):
                with self.subTest(name=name, missing=payload is None):
                    self.write_inputs()
                    replace_member(self.sourcezip, name, payload)
                    self.refresh_windows_sums()
                    self.assert_rejected((ValueError, KeyError))

    def test_unverified_mac_host_or_architecture_is_rejected(self):
        for section, field, value in (("root", "host", "Windows"), ("root", "portableCliTestsPassed", False),
                                      ("root", "portableCoreTestsPassed", False), ("asset", "nativeVersionTested", False),
                                      ("asset", "machOHeaderChecked", False)):
            with self.subTest(field=field):
                self.write_inputs()
                path, manifest = self.mac_manifest("x64")
                (manifest if section == "root" else manifest["assets"][0])[field] = value
                path.write_bytes(encoded(manifest))
                self.assert_rejected()

    def test_mac_permissions_package_digest_and_executable_digest_are_enforced(self):
        for fault in ("mode", "package-digest", "executable-digest"):
            with self.subTest(fault=fault):
                self.write_inputs()
                path, manifest = self.mac_manifest()
                row = manifest["assets"][0]
                package = path.parent / row["file"]
                if fault == "mode":
                    with zipfile.ZipFile(package) as archive:
                        payload = archive.read("SwitchDesk/SwitchDesk.Cli")
                    replace_member(package, "SwitchDesk.Cli", payload, 0o644)
                    row.update(size=package.stat().st_size, sha256=checksum(package.read_bytes()))
                else:
                    row["sha256" if fault == "package-digest" else "executableSha256"] = "0" * 64
                path.write_bytes(encoded(manifest))
                self.assert_rejected()

    def test_corrupt_or_truncated_nro_is_rejected_before_output(self):
        for payload in (b"NRO0", bytes(0x80)):
            with self.subTest(length=len(payload)):
                self.nro.write_bytes(payload)
                self.assert_rejected()

    def test_existing_release_is_never_overwritten(self):
        self.output.mkdir(parents=True)
        marker = self.output / "SwitchDesk-Source.zip"
        marker.write_bytes(b"previous release")
        with self.assertRaises(FileExistsError):
            self.assemble()
        self.assertEqual(marker.read_bytes(), b"previous release")
        self.assertEqual(list(self.output.iterdir()), [marker])

    def test_windows_or_source_checksum_mismatch_is_rejected(self):
        for archive in (self.winzip, self.sourcezip):
            with self.subTest(archive=archive.name):
                self.write_inputs()
                sums = self.windows / "SHA256SUMS.txt"
                sums.write_text(sums.read_text(encoding="utf-8").replace(
                    checksum(archive.read_bytes()), "0" * 64), encoding="utf-8")
                self.assert_rejected()

    def test_source_lock_and_release_commit_must_match(self):
        replace_member(self.sourcezip, "build-dependencies.lock.json", encoded({**self.lock, "sources": []}))
        self.refresh_windows_sums()
        self.assert_rejected()
        self.write_inputs()
        self.commit = "short-sha"
        self.assert_rejected()


if __name__ == "__main__":
    unittest.main()
