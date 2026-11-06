"""Offline Mac delivery regressions; synthetic headers are not native execution."""
from contextlib import chdir
from pathlib import Path
from unittest import mock
import importlib.util
import json
import stat
import struct
import tempfile
import unittest
import zipfile

REPOSITORY = Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location("macos_build", REPOSITORY / "scripts/build-macos.py")
macos = importlib.util.module_from_spec(spec)
spec.loader.exec_module(macos)


def executable(cpu=0x0100000C):
    return struct.pack("<8I", 0xFEEDFACF, cpu, 0, 2, 1, 8, 0, 0) + bytes(8)


class MacBuildTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory(prefix="SwitchDesk-macos-tests-")
        self.addCleanup(self.temporary.cleanup)
        # tempfile may return /var while publish() resolves /private/var on macOS.
        self.root = Path(self.temporary.name).resolve() / "project"
        (self.root / "scripts").mkdir(parents=True)
        (self.root / "docs").mkdir()
        (self.root / "src/SwitchDesk.Cli").mkdir(parents=True)
        (self.root / "caller").mkdir()
        self.identity = {"version": "fixture", "sourceId": "a" * 12, "sourceSha256": "a" * 64}
        (self.root / "build-info.json").write_text(json.dumps(self.identity))
        for name in ("docs/BUILD.md", "docs/STATUS.md", "LICENSE", "THIRD-PARTY-NOTICES.md"):
            (self.root / name).write_text("synthetic " + name)
        self.calls = []

    def command(self, arguments, root):
        arguments = [str(value) for value in arguments]
        self.calls.append(arguments)
        self.assertEqual(root, self.root)
        if "publish" not in arguments:
            return
        output = Path(arguments[arguments.index("-o") + 1])
        output.mkdir(parents=True)
        if "-r" not in arguments:
            (output / "SwitchDesk.Cli.dll").write_bytes(b"host fixture")
            return
        rid = arguments[arguments.index("-r") + 1]
        (output / "SwitchDesk.Cli").write_bytes(executable(macos.RIDS[rid]))

    def licenses(self, root, rid, destination):
        destination.mkdir()
        (destination / "dotnet-LICENSE.TXT").write_text("fixture license")
        (destination / "dotnet-THIRD-PARTY-NOTICES.TXT").write_text("fixture notices")
        return "10.0.fixture"

    def publish(self, command=None):
        with mock.patch.object(macos, "run", side_effect=command or self.command), \
                mock.patch.object(macos, "runtime_licenses", side_effect=self.licenses), \
                mock.patch.object(macos.platform, "system", return_value="Windows"), \
                mock.patch("builtins.print"), chdir(self.root / "caller"):
            return macos.publish(self.root, "tools/dotnet", Path("work/macos"))

    def test_headers_reject_wrong_architecture_or_truncated_load_table(self):
        path = self.root / "fixture"
        path.write_bytes(executable())
        macos.check_macho(path, "osx-arm64")
        for data, rid in ((executable(), "osx-x64"), (executable()[:31], "osx-arm64"),
                          (executable()[:32], "osx-arm64"), (b"MZ" + bytes(40), "osx-arm64")):
            path.write_bytes(data)
            with self.assertRaises(RuntimeError):
                macos.check_macho(path, rid)

    def test_project_relative_delivery_has_both_architectures_permissions_and_licenses(self):
        result = self.publish()
        self.assertTrue(result["portableCliTestsPassed"])
        self.assertEqual(len(result["assets"]), 2)
        self.assertFalse(any(row["nativeVersionTested"] for row in result["assets"]))
        self.assertFalse((self.root / "caller/work").exists())
        self.assertFalse(list((self.root / "work").glob(".SwitchDesk-macos-*")))
        commands = [row for row in self.calls if "publish" in row]
        self.assertEqual(len(commands), 3)
        self.assertTrue(all(row[0] == str(self.root / "tools/dotnet") for row in commands))
        self.assertTrue(any("--cli" in row and "--dotnet" in row for row in self.calls))
        for row in result["assets"]:
            path = self.root / "work/macos" / row["file"]
            self.assertEqual(macos.sha256(path), row["sha256"])
            with zipfile.ZipFile(path) as archive:
                member = archive.getinfo("SwitchDesk/SwitchDesk.Cli")
                self.assertEqual(member.create_system, 3)
                self.assertEqual(stat.S_IMODE(member.external_attr >> 16), 0o755)
                self.assertEqual(json.loads(archive.read("SwitchDesk/build-info.json")), self.identity)
                self.assertIn("SwitchDesk/licenses/dotnet-LICENSE.TXT", archive.namelist())
                for document in ("BUILD.md", "STATUS.md"):
                    self.assertEqual(archive.read("SwitchDesk/docs/" + document).decode(), "synthetic docs/" + document)
                readme = archive.read("SwitchDesk/README.txt").decode()
                self.assertIn("docs/BUILD.md", readme)
                self.assertIn("docs/STATUS.md", readme)
                self.assertNotIn("docs/MAC.md", readme)
                self.assertIsNone(archive.testzip())

    def test_second_architecture_failure_never_publishes_partial_delivery(self):
        def failure(arguments, root):
            if "osx-x64" in arguments:
                raise RuntimeError("fixture second publish failed")
            self.command(arguments, root)
        with self.assertRaisesRegex(RuntimeError, "second publish"):
            self.publish(failure)
        self.assertFalse((self.root / "work/macos").exists())
        self.assertFalse(list((self.root / "work").glob(".SwitchDesk-macos-*")))

    def test_existing_delivery_and_source_output_are_rejected_without_running_tools(self):
        output = self.root / "work/macos"
        output.mkdir(parents=True)
        old = output / "keep.txt"
        old.write_bytes(b"old delivery")
        with self.assertRaises(FileExistsError):
            self.publish()
        self.assertEqual(old.read_bytes(), b"old delivery")
        self.assertEqual(self.calls, [])
        for target in (self.root, self.root.parent / "outside", self.root / "src/new"):
            with self.assertRaises(ValueError):
                macos.publish(self.root, "dotnet", target)

    def test_changed_source_aborts_before_publishing_mismatched_builds(self):
        def mutate(arguments, root):
            self.command(arguments, root)
            if "osx-x64" in arguments:
                (self.root / "build-info.json").write_text(json.dumps({**self.identity, "sourceId": "b" * 12}))
        with self.assertRaisesRegex(RuntimeError, "Source changed"):
            self.publish(mutate)
        self.assertFalse((self.root / "work/macos").exists())

    def test_runtime_licenses_match_the_actual_runtime_not_the_latest_cache(self):
        project = self.root / "src/SwitchDesk.Cli"
        runtime = project / "bin/Release/net10.0/osx-arm64"
        runtime.mkdir(parents=True)
        (runtime / "SwitchDesk.Cli.runtimeconfig.json").write_text(json.dumps({"runtimeOptions": {
            "includedFrameworks": [{"name": "Microsoft.NETCore.App", "version": "10.0.fixture"}]}}))
        packages = self.root / "packages"
        for version in ("10.0.fixture", "99.0.fixture"):
            folder = packages / "microsoft.netcore.app.runtime.osx-arm64" / version
            folder.mkdir(parents=True)
            for name in ("LICENSE.TXT", "THIRD-PARTY-NOTICES.TXT"):
                (folder / name).write_text(version)
        (project / "obj").mkdir()
        (project / "obj/project.assets.json").write_text(json.dumps({"project": {"restore": {"packagesPath": str(packages)}}}))
        output = self.root / "licenses"
        self.assertEqual(macos.runtime_licenses(self.root, "osx-arm64", output), "10.0.fixture")
        self.assertEqual((output / "dotnet-LICENSE.TXT").read_text(), "10.0.fixture")


if __name__ == "__main__":
    unittest.main()
