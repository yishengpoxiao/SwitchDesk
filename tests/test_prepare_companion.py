"""Offline regressions for rebuilding the companion overlay from pinned source."""
from pathlib import Path
from unittest import mock
import hashlib
import importlib.util
import io
import json
import re
import shutil
import subprocess
import sys
import tempfile
import unittest
import zipfile


REPOSITORY = Path(__file__).resolve().parents[1]
SPEC = importlib.util.spec_from_file_location("prepare_companion", REPOSITORY / "scripts/prepare-companion.py")
preparation = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(preparation)


class CompanionDownloadTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory(prefix="SwitchDesk-download-tests-")
        self.addCleanup(self.temporary.cleanup)
        self.directory = Path(self.temporary.name)
        self.target = self.directory / "dependency.zip"
        self.payload = b"complete synthetic source archive"
        self.digest = hashlib.sha256(self.payload).hexdigest()

    def download(self):
        with mock.patch("builtins.print"):
            preparation.download("https://example.invalid/source.zip", self.target, self.digest)

    def test_interrupted_download_leaves_no_cache_and_retry_downloads_complete_source(self):
        class Interrupted(io.BytesIO):
            def read(self, size=-1):
                if self.tell():
                    raise OSError("synthetic connection interruption")
                return super().read(size)

        with mock.patch.object(preparation.urllib.request, "urlopen", return_value=Interrupted(b"partial")):
            with self.assertRaises(OSError):
                self.download()
        self.assertEqual(list(self.directory.iterdir()), [])
        with mock.patch.object(preparation.urllib.request, "urlopen", return_value=io.BytesIO(self.payload)) as retry:
            self.download()
            retry.assert_called_once()
        self.assertEqual(self.target.read_bytes(), self.payload)
        self.assertEqual(list(self.directory.iterdir()), [self.target])

    def test_checksum_failure_is_not_published_or_left_for_future_downloads(self):
        with mock.patch.object(preparation.urllib.request, "urlopen", return_value=io.BytesIO(b"wrong archive")):
            with self.assertRaisesRegex(RuntimeError, "checksum mismatch"):
                self.download()
        self.assertEqual(list(self.directory.iterdir()), [])
        with mock.patch.object(preparation.urllib.request, "urlopen", return_value=io.BytesIO(self.payload)):
            self.download()
        self.assertEqual(self.target.read_bytes(), self.payload)

    def test_existing_cache_is_not_modified_or_downloaded_again(self):
        self.target.write_bytes(self.payload)
        with mock.patch.object(preparation.urllib.request, "urlopen") as connection:
            self.download()
            connection.assert_not_called()
        self.assertEqual(self.target.read_bytes(), self.payload)


class CompanionPreparationTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory(prefix="SwitchDesk-prepare-tests-")
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name) / "source"
        for directory in ("scripts", "third_party", "src/SwitchDesk.Core"):
            (self.root / directory).mkdir(parents=True)
        for name in ("version.json", "build-dependencies.lock.json", "Directory.Build.props", "global.json"):
            shutil.copyfile(REPOSITORY / name, self.root / name)
        for name in ("prepare-companion.py", "build-info.py", "source_inputs.py"):
            shutil.copyfile(REPOSITORY / "scripts" / name, self.root / "scripts" / name)
        shutil.copytree(REPOSITORY / "companion", self.root / "companion")
        source = "Sphaira-1.0.8-source.zip"
        for name in (source, source + ".sha256"):
            shutil.copyfile(REPOSITORY / "third_party" / name, self.root / "third_party" / name)
        # This test exercises the overlay, not dependency downloading or compiling.
        # Existing empty dependency fixtures keep preparation completely offline.
        with zipfile.ZipFile(self.root / "third_party" / source) as archive:
            cmake = next(name for name in archive.namelist() if name.endswith("/sphaira/CMakeLists.txt"))
            for name in re.findall(r"FetchContent_Declare\((\w+)", archive.read(cmake).decode()):
                (self.root / "vendor/deps" / name).mkdir(parents=True, exist_ok=True)
        self.destination = self.root / "prepared"

    def prepare(self, destination=None, cwd=None):
        completed = subprocess.run(
            [sys.executable, str(self.root / "scripts/prepare-companion.py"),
             "--destination", str(destination or self.destination), "--offline"],
            cwd=cwd or self.root, capture_output=True, text=True, encoding="utf-8",
        )
        self.assertEqual(completed.returncode, 0, completed.stdout + completed.stderr)

    def test_clean_source_has_cnmt_definition_build_input_and_both_callers(self):
        self.prepare()
        prepared = self.destination / "sphaira"
        for relative in ("source/yati/nx/cnmt.cpp", "include/switchdesk/cnmt.hpp"):
            self.assertEqual((prepared / relative).read_bytes(),
                             (self.root / "companion" / relative).read_bytes())
        cmake = (prepared / "CMakeLists.txt").read_text(encoding="utf-8")
        self.assertEqual(cmake.count("    source/yati/nx/cnmt.cpp\n"), 1)
        installer = (prepared / "source/yati/yati.cpp").read_text(encoding="utf-8")
        self.assertIn('#include "switchdesk/cnmt.hpp"', installer)
        self.assertEqual(installer.count("yati->InstallCnmtNca(tickets, cnmt, collections, source)"), 2)
        self.assertIn("source && !source->IsStream()", installer)
        self.assertIn('cnmt.name.ends_with(".cnmt.nca")', installer)
        self.assertIn("nca::ParseCnmtFromSource(*source", installer)
        self.assertIn("nca::ParseCnmt(path", installer)
        nca = (prepared / "source/yati/nx/nca.cpp").read_text(encoding="utf-8")
        self.assertNotIn("Result ParseCnmtFromSource(", nca)
        self.assertIn("i < NCA_SECTION_TOTAL", nca)
        server = (prepared / "source/server.cpp").read_text(encoding="utf-8")
        self.assertIn('#include "switchdesk/cnmt.hpp"', server)
        self.assertIn("nca::ParseCnmtFromSource(source", server)
        identity = json.loads((self.root / "build-info.json").read_text(encoding="utf-8-sig"))
        self.assertIn(identity["sourceId"],
                      (prepared / "include/switchdesk/build_info.hpp").read_text(encoding="utf-8"))

    def test_second_prepare_preserves_every_prepared_file(self):
        self.prepare()

        def snapshot():
            return {file.relative_to(self.destination).as_posix():
                    hashlib.sha256(file.read_bytes()).hexdigest()
                    for file in self.destination.rglob("*") if file.is_file()}

        first = snapshot()
        self.prepare()
        self.assertEqual(first, snapshot())

    def test_fsp_fallback_uses_checked_plaintext_parser(self):
        self.prepare()
        nca = (self.destination / "sphaira/source/yati/nx/nca.cpp").read_text(encoding="utf-8")
        fallback = nca.split("Result ParseCnmt(", 1)[1].split("Result ParseControl(", 1)[0]
        self.assertIn("total_entries == 1 && visited++ < 0x100", fallback)
        self.assertIn("total_entries == 0", fallback)
        self.assertIn('ends_with(".cnmt")', fallback)
        self.assertIn('if (!name.ends_with(".cnmt")) continue;', fallback)
        self.assertIn("R_UNLESS(cnmt_name.empty()", fallback)
        self.assertIn("file.GetSize(&size)", fallback)
        self.assertIn("ParseCnmtContents(static_cast<u64>(size)", fallback)
        self.assertNotIn("extended_header.resize(", fallback)
        self.assertIn('#include "switchdesk/cnmt.hpp"', nca)

    def test_relative_destination_uses_project_root_from_another_directory(self):
        self.prepare("work/prepared", self.root.parent)
        target = self.root / "work/prepared/sphaira/source/yati/nx/cnmt.cpp"
        self.assertTrue(target.is_file())
        self.assertFalse((self.root.parent / "work").exists())

    def test_hash_warning_policy_keeps_the_actual_digest_and_explicit_configuration(self):
        self.prepare()
        prepared = self.destination / "sphaira"
        header = (prepared / "include/yati/yati.hpp").read_text(encoding="utf-8")
        installer = (prepared / "source/yati/yati.cpp").read_text(encoding="utf-8")
        server = (prepared / "source/server.cpp").read_text(encoding="utf-8")
        self.assertIn("std::optional<bool> allow_nca_hash_mismatch{};", header)
        self.assertIn("config.allow_nca_hash_mismatch = override.allow_nca_hash_mismatch.value_or(false);", installer)
        self.assertIn("config.nca_hash_warning = override.nca_hash_warning;", installer)
        self.assertIn("switchdesk::EvaluateNcaHash(expected, actual, policy)", installer)
        self.assertIn("R_TRY(config.nca_hash_warning(switchdesk::MakeNcaHashWarning", installer)
        self.assertIn("config.skip_nca_hash_verify = false;", server)
        self.assertIn('"install.hash-warning.v1"', server)
        self.assertIn('"expectedContentId"', server)
        self.assertIn('"actualSha256"', server)
        self.assertIn('"管理安装计划必须使用 strict 摘要策略"', server)

    def test_signature_warning_verifies_copies_and_remains_independent_of_hash_policy(self):
        self.prepare()
        prepared = self.destination / "sphaira"
        header = (prepared / "include/yati/yati.hpp").read_text(encoding="utf-8")
        installer = (prepared / "source/yati/yati.cpp").read_text(encoding="utf-8")
        server = (prepared / "source/server.cpp").read_text(encoding="utf-8")
        policy = (prepared / "include/switchdesk/install_signature_policy.hpp").read_text(encoding="utf-8")
        self.assertEqual(header.count("bool allow_nca_signature_mismatch{};"), 1)
        self.assertEqual(header.count("std::optional<bool> allow_nca_signature_mismatch{};"), 1)
        self.assertEqual(header.count("nca_signature_warning{};"), 2)
        self.assertIn("config.allow_nca_signature_mismatch = override.allow_nca_signature_mismatch.value_or(false);", installer)
        self.assertIn("!config.skip_rsa_header_fixed_key_verify && config.nca_signature_warning", installer)
        self.assertIn("VerifyNcaHeaderSignature(header, signed_header, signature_policy, t->nca->name,", installer)
        self.assertEqual(installer.count("return nca::VerifyFixedKey(candidate);"), 1)
        self.assertIn("reinterpret_cast<const std::uint8_t*>(&header.magic)", installer)
        self.assertIn("sha256CalculateHash(digest.data(), bytes.data(), bytes.size());", installer)
        self.assertIn("auto verification_header = original_header;", policy)
        self.assertIn("verify(verification_header)", policy)
        self.assertIn("NcaHeaderSignatureMismatchResult = 0x81F9", policy)
        self.assertIn("result == NcaHeaderSignatureMismatchResult && policy == NcaSignaturePolicy::Warn", policy)
        self.assertIn('yyjson_obj_get(args, "ncaSignaturePolicy")', server)
        self.assertIn('"管理安装计划必须使用 strict 签名策略"', server)
        self.assertIn('"install.signature-warning.v1"', server)
        self.assertIn('"NcaHeaderSignatureMismatch"', server)
        self.assertIn('"headerSha256"', server)
        self.assertIn('"verificationResult", Hex(warning.verification_result)', server)
        self.assertIn('"NcaHeaderSignatureInvalid"', server)
        self.assertIn("error_code.empty() && error_message.empty()", server)
        self.assertEqual(server.count("hash_warnings.size() + signature_warnings.size() >= 256"), 2)
        self.assertEqual(server.count("mutexLock(&warning_mutex); ON_SCOPE_EXIT(mutexUnlock(&warning_mutex));"), 2)
        self.assertNotIn("skip_rsa_header_fixed_key_verify = true", server)
        self.assertIn("config.skip_rsa_header_fixed_key_verify = false;", server)
        # The pinned cryptographic verifier, including its distribution-bit
        # retry, must remain unchanged by the compatibility overlay.
        with zipfile.ZipFile(self.root / "third_party/Sphaira-1.0.8-source.zip") as archive:
            name = next(name for name in archive.namelist() if name.endswith("/source/yati/nx/nca.cpp"))
            original_nca = archive.read(name).decode()
        nca = (prepared / "source/yati/nx/nca.cpp").read_text(encoding="utf-8")
        def verifier(text):
            return text.split("Result VerifyFixedKey(", 1)[1].split("Result ParseCnmt(", 1)[0]
        self.assertEqual(verifier(original_nca), verifier(nca))


if __name__ == "__main__":
    unittest.main()
