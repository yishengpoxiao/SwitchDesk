"""Check project-relative build paths from an unrelated working directory."""
from contextlib import chdir
from pathlib import Path
from unittest import mock
import hashlib
import importlib.util
import io
import json
import shutil
import subprocess
import sys
import tarfile
import tempfile
import unittest
import zipfile


REPOSITORY = Path(__file__).resolve().parents[1]


class ScriptPathTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory(prefix="SwitchDesk-script-path-tests-")
        self.addCleanup(self.temporary.cleanup)
        temporary = Path(self.temporary.name).resolve()
        self.root = temporary / "project"
        self.caller = temporary / "caller"
        (self.root / "scripts").mkdir(parents=True)
        self.caller.mkdir()

    def copy_script(self, name):
        target = self.root / "scripts" / name
        shutil.copyfile(REPOSITORY / "scripts" / name, target)
        return target

    def load_script(self, name):
        script = self.copy_script(name)
        spec = importlib.util.spec_from_file_location("script_path_fixture", script)
        module = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(module)
        return module

    def run_script(self, name, *arguments):
        result = subprocess.run([sys.executable, str(self.copy_script(name)), *arguments],
                                cwd=self.caller, capture_output=True, text=True)
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        return result.stdout

    def test_bootstrap_sdk_prefix_and_cache_belong_to_the_project(self):
        # No packages means no network, extraction, signature process or SDK install.
        (self.root / "scripts/zstandard.py").write_text("# Unused in this empty-package fixture.\n")
        keyring = self.root / "fixture-keyring"
        keyring.write_bytes(b"synthetic public-keyring fixture")
        lock = {"keyring": {"archive": keyring.name, "sha256": hashlib.sha256(keyring.read_bytes()).hexdigest()},
                "sdkPackages": []}
        (self.root / "build-dependencies.lock.json").write_text(json.dumps(lock))
        self.run_script("bootstrap-windows-sdk.py", "--prefix", "work/sdk", "--cache", "work/cache")
        self.assertTrue((self.root / "work/sdk").is_dir())
        self.assertTrue((self.root / "work/cache").is_dir())
        self.assertFalse((self.caller / "work").exists())

    def test_sdk_sources_and_overlay_belong_to_the_project(self):
        lock = {"newlibOverlay": "companion/compat/build_argv.c"}
        (self.root / "third_party").mkdir()
        for name, files in (("libnx", {"Makefile": b"# fixture"}),
                            ("newlib", {"build-libgloss-local.sh": b"# fixture",
                                        "libgloss/libsysbase/build_argv.c": b"old fixture"})):
            archive = self.root / "third_party" / (name + ".tar")
            with tarfile.open(archive, "w") as target:
                for relative, content in files.items():
                    member = tarfile.TarInfo("upstream/" + relative)
                    member.size = len(content)
                    target.addfile(member, io.BytesIO(content))
            lock[name] = {"archive": "third_party/" + archive.name, "commit": "a" * 40,
                          "sha256": hashlib.sha256(archive.read_bytes()).hexdigest()}
        overlay = self.root / lock["newlibOverlay"]
        overlay.parent.mkdir(parents=True)
        overlay.write_bytes(b"synthetic overlay fixture")
        (self.root / "build-dependencies.lock.json").write_text(json.dumps(lock))
        output = json.loads(self.run_script("prepare-sdk.py", "--source-root", "work/sdk-source"))
        destination = self.root / "work/sdk-source/newlib-aaaaaaaaaaaa"
        self.assertEqual(Path(output["newlib"]), destination)
        self.assertEqual((destination / "libgloss/libsysbase/build_argv.c").read_bytes(), overlay.read_bytes())
        self.assertFalse((self.caller / "work").exists())

    def test_streaming_cache_runtime_and_setup_belong_to_the_project(self):
        module = self.load_script("prepare-streaming.py")
        cache = self.root / "work/cache"
        cache.mkdir(parents=True)
        windows = cache / "windows.zip"
        with zipfile.ZipFile(windows, "w") as archive:
            archive.writestr("SysDVR-Client.runtimeconfig.json", json.dumps({
                "runtimeOptions": {"includedFrameworks": [{"name": "Microsoft.NETCore.App", "version": "fixture"}]}}))
        switch = cache / "switch.zip"
        with zipfile.ZipFile(switch, "w") as archive:
            archive.writestr("config/sysdvr/usb", "synthetic boot marker")
        source = cache / "source.zip"
        source.write_bytes(b"synthetic source archive fixture")
        assets = [{"id": name, "file": path.name, "sha256": hashlib.sha256(path.read_bytes()).hexdigest(),
                   **({"purpose": "source"} if name == "sysdvr-source" else {})}
                  for name, path in (("windows", windows), ("switch", switch), ("sysdvr-source", source))]
        lock = {"assets": assets, "version": "fixture", "commit": "fixture",
                "runtime": {"requiredFiles": ["SysDVR-Client.runtimeconfig.json"], "dotnetRuntime": "fixture"}}
        module.LOCK.write_text(json.dumps(lock))
        licenses = self.root / "third_party/licenses/SysDVR"
        licenses.mkdir(parents=True)
        (licenses / "LICENSE").write_text("fixture")
        (self.root / "docs").mkdir()
        (self.root / "docs/BUILD.md").write_text("streaming setup fixture")
        seen_extractors = []

        def extract_fixture(path, destination, extractor):
            seen_extractors.append(extractor)
            with zipfile.ZipFile(path) as archive:
                archive.extractall(destination)

        arguments = ["prepare-streaming.py", "--offline", "--cache-dir", "work/cache",
                     "--windows-output", "work/windows", "--setup-output", "work/setup",
                     "--extractor", "tools/tar"]
        with chdir(self.caller), mock.patch.object(sys, "argv", arguments), \
                mock.patch.object(module, "extract_sevenzip", side_effect=extract_fixture), \
                mock.patch("builtins.print"):
            module.main()
        self.assertEqual(seen_extractors, [str(self.root / "tools/tar")])
        self.assertTrue((self.root / "work/windows/Streaming/SysDVR/bundle-manifest.json").is_file())
        self.assertTrue((self.root / "work/setup/setup-manifest.json").is_file())
        self.assertEqual((self.root / "work/setup/BUILD.md").read_text(), "streaming setup fixture")
        self.assertFalse((self.caller / "work").exists())

    def test_cnmt_output_and_compiler_paths_belong_to_the_project(self):
        module = self.load_script("test-cnmt.py")
        arguments = ["test-cnmt.py", "--output", "work/cnmt", "--cxx", "tools/g++"]
        with chdir(self.caller), mock.patch.object(sys, "argv", arguments), \
                mock.patch.object(module.shutil, "which", return_value=None), \
                mock.patch.object(module.subprocess, "run") as run:
            module.main()
        compile_command = run.call_args_list[0].args[0]
        self.assertEqual(compile_command[0], str(self.root / "tools/g++"))
        executable = Path(compile_command[compile_command.index("-o") + 1])
        self.assertEqual(executable.parent, self.root / "work/cnmt")
        self.assertEqual(run.call_args_list[0].kwargs["cwd"], self.root)
        self.assertEqual(run.call_args_list[1].kwargs["cwd"], executable.parent)
        self.assertTrue((executable.parent / "stub/switchdesk/cnmt.hpp").is_file())
        self.assertFalse((self.caller / "work").exists())


if __name__ == "__main__":
    unittest.main()
