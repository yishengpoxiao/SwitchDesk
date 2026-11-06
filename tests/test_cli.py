"""Exercise the built portable CLI through real processes and isolated local fixtures.

Usage: python tests/test_cli.py --dotnet dotnet --cli path/SwitchDesk.Cli.dll
These checks do not contact a Switch or any public service.
"""
import argparse
import contextlib
import ctypes
import hashlib
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
import json
import os
from pathlib import Path
import re
import signal
import shlex
import shutil
import sqlite3
import subprocess
import sys
import tempfile
import threading
import unittest
import zipfile


def encoded(value):
    return json.dumps(value, separators=(",", ":"), ensure_ascii=True).encode()


@contextlib.contextmanager
def http_fixture(status=200, body=None, extra=None, declared=None, wait=None):
    body = body if body is not None else encoded([{"name": "Fixture", "url": "https://fixture.invalid/tool.nro"}])
    received = threading.Event()

    class Handler(BaseHTTPRequestHandler):
        def log_message(self, *_):
            pass

        def do_GET(self):
            received.set()
            if wait is not None:
                wait.wait(10)
            try:
                self.send_response(status)
                self.send_header("Content-Length", str(len(body) if declared is None else declared))
                self.send_header("Connection", "close")
                for key, value in (extra or {}).items():
                    self.send_header(key, value)
                self.end_headers()
                self.wfile.write(body)
            except (BrokenPipeError, ConnectionResetError):
                pass

    server = ThreadingHTTPServer(("127.0.0.1", 0), Handler)
    thread = threading.Thread(target=server.serve_forever, daemon=True)
    thread.start()
    try:
        yield f"http://127.0.0.1:{server.server_port}/index.json", received
    finally:
        if wait is not None:
            wait.set()
        server.shutdown()
        server.server_close()
        thread.join(5)


class CliTests(unittest.TestCase):
    command = []
    fixtures = None

    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix=self._testMethodName + "-", dir=self.fixtures)
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)

    def run_cli(self, *arguments, code=0):
        result = subprocess.run([*self.command, *map(str, arguments)], cwd=self.root,
                                capture_output=True, encoding="utf-8-sig", timeout=20)
        self.assertEqual(result.returncode, code, result.stdout + result.stderr)
        return result

    def snapshot(self, name, data=b"fixture"):
        files = [{"path": "main.dat", "size": len(data), "sha256": hashlib.sha256(data).hexdigest().upper()}]
        manifest = {"formatVersion": 1, "deviceId": "fixture", "environment": "test",
                    "titleId": "0100000000010000", "gameName": "Fixture", "userId": "fixture-user",
                    "userName": "一生破晓", "saveType": "Account", "createdAt": "2026-11-05T00:00:00Z",
                    "files": files, "directories": [],
                    "contentHash": hashlib.sha256(encoded({"files": files, "directories": []})).hexdigest().upper()}
        path = self.root / name
        with zipfile.ZipFile(path, "w") as archive:
            archive.writestr("manifest.json", encoded(manifest))
            archive.writestr("data/main.dat", data)
        return path

    def test_help_version_and_invalid_arguments(self):
        self.assertIn("--version", self.run_cli("--help").stdout)
        self.assertRegex(self.run_cli("--version").stdout.strip(), r"^SwitchDesk/[^\s]+\+[a-f0-9]{12}$")
        self.run_cli("missing-command", code=2)
        for args in (("--version", "extra"), ("updates-check", "one", "two"),
                     ("homebrew-index", "http://127.0.0.1:1/", "cache", "extra"),
                     ("ftp-ls", "127.0.0.1", "0"), ("snapshot-export",),
                     ("stream-args", "--mode")):
            with self.subTest(arguments=args):
                self.run_cli(*args, code=3)

    def test_stream_arguments_validate_modes_and_addresses(self):
        self.assertEqual(self.run_cli("stream-args", "--mode", "bridge", "--address", "192.168.1.5").stdout.strip(), "bridge 192.168.1.5")
        self.assertEqual(self.run_cli("stream-args").stdout.strip(), "usb")
        self.run_cli("stream-args", "--mode", "invalid", code=3)
        self.run_cli("stream-args", "--mode", "bridge", "--address", "invalid address", code=3)

    def test_stream_title_survives_copying_into_the_host_shell(self):
        title = "Fixture's \"title\" & $value; (literal)"
        result = self.run_cli("stream-args", "--title", title)
        if os.name == "nt":
            pwsh = shutil.which("pwsh") or shutil.which("powershell")
            self.assertIsNotNone(pwsh)
            # Only parse/echo arguments; never start SysDVR or a device session.
            parsed = subprocess.run([pwsh, "-NoProfile", "-Command",
                                     "& { ConvertTo-Json -InputObject @($args) -Compress } " + result.stdout.strip()],
                                    capture_output=True, encoding="utf-8-sig", timeout=10)
            self.assertEqual(parsed.returncode, 0, parsed.stderr)
            actual = json.loads(parsed.stdout)
        else:
            actual = shlex.split(result.stdout.strip())
        self.assertEqual(actual, ["usb", "--title", title])

    def test_library_verification_failure_has_nonzero_exit(self):
        source = self.root / "games"; source.mkdir()
        package = source / "fixture.nsp"; package.write_bytes(b"fixture")
        database = self.root / "library.db"
        self.run_cli("library-scan", database, source)
        with contextlib.closing(sqlite3.connect(database)) as db:
            with db:
                db.execute("UPDATE library SET sha256=?", (hashlib.sha256(b"fixture").hexdigest(),))
        self.run_cli("library-verify", database)
        package.write_bytes(b"changed")
        self.run_cli("library-verify", database, code=4)
        self.run_cli("library-verify", database, code=4)
        package.unlink()
        self.run_cli("library-verify", database, code=4)

    def test_library_scan_keeps_processing_after_invalid_manifest(self):
        source = self.root / "games"; source.mkdir()
        for name in ("invalid.nsp", "ordinary.nsp"):
            (source / name).write_bytes(b"fixture")
        (source / "invalid.nsp.switchdesk.json").write_text('{"formatVersion":1,"component":null}')
        database = self.root / "library.db"
        self.run_cli("library-scan", database, source)
        result = self.run_cli("library-list", database)
        self.assertIn("invalid.nsp", result.stdout)
        self.assertIn("ordinary.nsp", result.stdout)

    def test_snapshot_export_and_corruption_checks(self):
        snapshot = self.snapshot("valid.zip")
        self.assertIn("一生破晓", self.run_cli("snapshot-verify", snapshot).stdout)
        self.run_cli("snapshot-compare", snapshot, snapshot)
        target = self.root / "exports"
        self.run_cli("snapshot-export", snapshot, target)
        self.run_cli("snapshot-export", snapshot, target)
        copies = list(target.glob("*.zip"))
        self.assertEqual(len(copies), 2)
        self.assertTrue(all(p.read_bytes() == snapshot.read_bytes() for p in copies))
        self.assertFalse(list(target.glob("*.partial")))
        bad = self.root / "bad.zip"; bad.write_bytes(b"not a zip")
        self.run_cli("snapshot-export", bad, target, code=3)
        self.assertEqual(len(list(target.glob("*.zip"))), 2)

    def test_homebrew_cache_creates_nested_directory(self):
        cache = self.root / "nested" / "cache" / "homebrew.json"
        with http_fixture() as (url, _):
            self.run_cli("homebrew-index", url, cache)
        self.assertEqual(json.loads(cache.read_text())[0]["name"], "Fixture")
        self.assertFalse(list(cache.parent.glob("*.partial")))

    def test_partial_http_responses_preserve_previous_cache(self):
        cache = self.root / "cache.json"; original = b"previous cache"
        responses = ({"status": 206}, {"extra": {"Content-Range": "bytes 0-5/20"}},
                     {"declared": 5000})
        for response in responses:
            with self.subTest(response=response):
                cache.write_bytes(original)
                with http_fixture(**response) as (url, _):
                    self.run_cli("homebrew-index", url, cache, code=3)
                self.assertEqual(cache.read_bytes(), original)
                self.assertFalse(list(self.root.glob("*.partial")))

    def test_cache_publish_failure_cleans_temporary_file(self):
        cache = self.root / "cache.json"; cache.mkdir()
        keep = cache / "keep"; keep.write_bytes(b"original directory")
        with http_fixture() as (url, _):
            self.run_cli("homebrew-index", url, cache, code=3)
        self.assertEqual(keep.read_bytes(), b"original directory")
        self.assertFalse(list(self.root.glob("*.partial")))

    @unittest.skipUnless(os.name == "nt", "Windows sharing lock fixture")
    def test_locked_cache_is_not_truncated(self):
        cache = self.root / "cache.json"; cache.write_bytes(b"original cache")
        create = ctypes.windll.kernel32.CreateFileW
        create.argtypes = [ctypes.c_wchar_p, ctypes.c_uint32, ctypes.c_uint32, ctypes.c_void_p,
                           ctypes.c_uint32, ctypes.c_uint32, ctypes.c_void_p]
        create.restype = ctypes.c_void_p
        handle = create(str(cache), 0x80000000, 3, None, 3, 0, None)
        self.assertNotEqual(handle, ctypes.c_void_p(-1).value)
        try:
            with http_fixture() as (url, _):
                self.run_cli("homebrew-index", url, cache, code=3)
            self.assertEqual(cache.read_bytes(), b"original cache")
            self.assertFalse(list(self.root.glob("*.partial")))
        finally:
            close = ctypes.windll.kernel32.CloseHandle
            close.argtypes = [ctypes.c_void_p]
            close(handle)

    @unittest.skipIf(os.name == "nt", "POSIX SIGINT delivery; run on macOS/Linux")
    def test_sigint_cancels_network_and_preserves_cache(self):
        cache = self.root / "cache.json"; cache.write_bytes(b"original cache")
        release = threading.Event()
        with http_fixture(wait=release) as (url, received):
            process = subprocess.Popen([*self.command, "homebrew-index", url, str(cache)], cwd=self.root,
                                       stdout=subprocess.PIPE, stderr=subprocess.PIPE)
            try:
                self.assertTrue(received.wait(5), "CLI did not start request")
                process.send_signal(signal.SIGINT)
                stdout, stderr = process.communicate(timeout=5)
                self.assertEqual(process.returncode, 130, stdout + stderr)
                self.assertEqual(cache.read_bytes(), b"original cache")
            finally:
                if process.poll() is None:
                    process.kill(); process.communicate()


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--dotnet", default="dotnet")
    parser.add_argument("--cli", type=Path, required=True)
    parser.add_argument("--fixtures", type=Path)
    args, remaining = parser.parse_known_args()
    CliTests.command = [args.dotnet, str(args.cli.resolve())]
    if args.fixtures:
        args.fixtures.mkdir(parents=True, exist_ok=True)
        CliTests.fixtures = str(args.fixtures.resolve())
    unittest.main(argv=[sys.argv[0], *remaining], verbosity=2)
