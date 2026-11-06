"""Prepare the corresponding GPL source tree and a reproducible Sphaira overlay."""
from pathlib import Path
import argparse, hashlib, re, shutil, urllib.request, zipfile, subprocess, sys, os, tempfile

ROOT = Path(__file__).resolve().parents[1]
def download(url: str, destination: Path, expected_sha256: str | None = None):
    destination.parent.mkdir(parents=True, exist_ok=True)
    if not destination.exists():
        print(f"Downloading {url}", flush=True)
        # Keep interrupted or mismatched downloads out of the reusable cache.
        # The sibling staging directory keeps publication on the same volume.
        with tempfile.TemporaryDirectory(prefix=".SwitchDesk-download-", dir=destination.parent) as temporary:
            staged = Path(temporary) / destination.name
            with urllib.request.urlopen(urllib.request.Request(url, headers={"User-Agent": "SwitchDesk-build/0.1"}), timeout=120) as source, staged.open("wb") as out:
                shutil.copyfileobj(source, out)
            if expected_sha256 and hashlib.sha256(staged.read_bytes()).hexdigest() != expected_sha256.lower():
                raise RuntimeError("Downloaded source archive checksum mismatch: " + destination.name)
            staged.replace(destination)

def unpack(archive: Path, destination: Path):
    with zipfile.ZipFile(archive) as z:
        prefix = z.namelist()[0].split('/')[0] + '/'
        for entry in z.infolist():
            relative = entry.filename.removeprefix(prefix)
            if not relative or entry.is_dir(): continue
            target = (destination / relative).resolve()
            if not target.is_relative_to(destination.resolve()): raise RuntimeError("Archive path escapes target")
            target.parent.mkdir(parents=True, exist_ok=True)
            with z.open(entry) as source, target.open("wb") as out: shutil.copyfileobj(source, out)

def replace(path: Path, before: str, after: str):
    text = path.read_text(encoding="utf-8")
    if after in text: return
    if text.count(before) != 1: raise RuntimeError(f"Pinned upstream anchor changed: {path}: {before[:60]}")
    path.write_text(text.replace(before, after), encoding="utf-8")

def main():
    subprocess.run([sys.executable, str(ROOT / "scripts/build-info.py")], check=True)
    p = argparse.ArgumentParser(); p.add_argument("--destination", type=Path, default=ROOT / "vendor/sphaira"); p.add_argument("--offline", action="store_true"); args = p.parse_args()
    destination = (ROOT / args.destination).resolve(); archive = ROOT / "third_party/Sphaira-1.0.8-source.zip"
    digest_file = archive.with_suffix(archive.suffix + ".sha256")
    if not archive.exists():
        if args.offline: raise RuntimeError("Missing pinned source archive")
        download("https://codeload.github.com/NaGaa95/sphaira/zip/338348e74b6d278bc570a9be5373d25197bf6c8d", archive,
                 digest_file.read_text().strip() if digest_file.exists() else None)
    if digest_file.exists() and hashlib.sha256(archive.read_bytes()).hexdigest() != digest_file.read_text().strip(): raise RuntimeError("Upstream archive checksum mismatch")
    if not (destination / "sphaira/source/main.cpp").exists(): unpack(archive, destination)
    for folder in ("include", "source"):
        for source in (ROOT / "companion" / folder).rglob("*"):
            if source.is_file():
                target = destination / "sphaira" / folder / source.relative_to(ROOT / "companion" / folder)
                target.parent.mkdir(parents=True, exist_ok=True); shutil.copyfile(source, target)
    replace(destination / "sphaira/CMakeLists.txt", "add_executable(sphaira\n", "add_executable(sphaira\n    source/server.cpp\n")
    replace(destination / "sphaira/CMakeLists.txt", "    source/server.cpp\n", "    source/server.cpp\n    source/management_view.cpp\n")
    replace(destination / "sphaira/CMakeLists.txt", "    source/management_view.cpp\n", "    source/management_view.cpp\n    source/yati/nx/cnmt.cpp\n")
    replace(destination / "sphaira/source/main.cpp", '#include "app.hpp"', '#include "app.hpp"\n#include "switchdesk/server.hpp"')
    replace(destination / "sphaira/source/main.cpp", "    app->Loop();", "    switchdesk::Launch();\n    app->Loop();")
    replace(destination / "sphaira/source/usb/usbds.cpp", 'usbDsAddUsbStringDescriptor(&iProduct, "Nintendo Switch")', 'usbDsAddUsbStringDescriptor(&iProduct, "SwitchDesk PC")')
    replace(destination / "sphaira/source/ui/progress_box.cpp", '#include "app.hpp"', '#include "app.hpp"\n#include "switchdesk/server.hpp"')
    replace(destination / "sphaira/source/ui/progress_box.cpp", '#include "switchdesk/server.hpp"', '#include "switchdesk/server.hpp"\n#include "switchdesk/management_view.hpp"')
    replace(destination / "sphaira/include/ui/progress_box.hpp", '    bool m_screen_toggle_enabled{};', '    bool m_screen_toggle_enabled{};\n    bool m_switchdesk_management{};')
    replace(destination / "sphaira/source/ui/progress_box.cpp", '    m_pos.w = 770.f;', '    m_switchdesk_management = m_action == "SwitchDesk PC";\n    if (m_switchdesk_management) switchdesk::ConfigureManagement(this);\n    m_pos.w = 770.f;')
    replace(destination / "sphaira/source/ui/progress_box.cpp", 'auto ProgressBox::Draw(NVGcontext* vg, Theme* theme) -> void {', 'auto ProgressBox::Draw(NVGcontext* vg, Theme* theme) -> void {\n    if (m_switchdesk_management) { switchdesk::DrawManagement(vg); return; }')
    replace(destination / "sphaira/source/ui/progress_box.cpp", "    m_size = size;", "    switchdesk::SetProgress(offset, size);\n    m_size = size;")
    replace(destination / "sphaira/include/usb/base.hpp", "#include <memory>", "#include <memory>\n#include <cstdlib>")
    replace(destination / "sphaira/include/usb/base.hpp", "    std::unique_ptr<u8*> m_aligned{};", "    struct AlignedFree { void operator()(u8* ptr) const { std::free(ptr); } };\n    std::unique_ptr<u8, AlignedFree> m_aligned{};")
    replace(destination / "sphaira/source/usb/base.cpp", "#include <cstring>", "#include <cstring>\n#include <malloc.h>")
    replace(destination / "sphaira/source/usb/base.cpp", "    m_aligned = std::make_unique<u8*>(new(std::align_val_t{TRANSFER_ALIGN}) u8[TRANSFER_MAX]);", "    m_aligned.reset(static_cast<u8*>(memalign(TRANSFER_ALIGN, TRANSFER_MAX)));")
    replace(destination / "sphaira/source/usb/base.cpp", "    auto transfer_buf = *m_aligned;", "    auto transfer_buf = m_aligned.get();\n    R_UNLESS(transfer_buf != nullptr, Result_UsbBadBufferAlign);")
    # Preserve previous registered content. Never run upstream's cross-storage
    # deletion before publishing a newly installed content-meta record.
    yati = destination / "sphaira/source/yati/yati.cpp"
    text = yati.read_text(encoding="utf-8")
    text = text.replace("R_TRY(yati->RemoveInstalledNcas(cnmt));", "/* SwitchDesk: retain previously registered content. */")
    text = text.replace("[[maybe_unused]] Result Yati::RemoveInstalledNcas(", "Result Yati::RemoveInstalledNcas(").replace("Result Yati::RemoveInstalledNcas(", "[[maybe_unused]] Result Yati::RemoveInstalledNcas(")
    yati.write_text(text, encoding="utf-8")
    # CNMT parsing belongs to the reproducible overlay, not the ignored vendor tree.
    # Migrate the former local-only implementation when reusing an existing tree.
    nca_source = destination / "sphaira/source/yati/nx/nca.cpp"
    nca_header = destination / "sphaira/include/yati/nx/nca.hpp"
    legacy_marker = "// 用户态 CNMT 解析（DBI 同思路）："
    text = nca_source.read_text(encoding="utf-8")
    if legacy_marker in text:
        text = text[:text.index(legacy_marker)] + "} // namespace sphaira::nca\n"
        nca_source.write_text(text, encoding="utf-8")
    text = nca_header.read_text(encoding="utf-8")
    legacy_start = text.find("// userspace CNMT parse (DBI approach):")
    if legacy_start >= 0:
        legacy_end = text.index("Result ParseControl(", legacy_start)
        nca_header.write_text(text[:legacy_start] + text[legacy_end:], encoding="utf-8")
    legacy_cnmt = '''    // 用户态解析（DBI 同思路）：直接从源读取，不走 fsp-srv。
    std::string cnmt_stage;
    if (source) {
        R_TRY(nca::ParseCnmtFromSource(*source, static_cast<u64>(cnmt.offset), cnmt.header, keys, header, cnmt.extended_header, infos, cnmt_stage));
    } else {
        R_TRY(nca::ParseCnmt(path, cnmt.header.program_id, header, cnmt.extended_header, infos));
    }'''
    text = yati.read_text(encoding="utf-8")
    if legacy_cnmt in text:
        yati.write_text(text.replace(legacy_cnmt, "    R_TRY(nca::ParseCnmt(path, cnmt.header.program_id, header, cnmt.extended_header, infos));"), encoding="utf-8")
    replace(yati, '#include "yati/yati.hpp"', '#include "yati/yati.hpp"\n#include "switchdesk/cnmt.hpp"')
    old_cnmt_signature = "InstallCnmtNca(std::span<TikCollection> tickets, CnmtCollection& cnmt, const container::Collections& collections)"
    new_cnmt_signature = old_cnmt_signature[:-1] + ", source::Base* source)"
    # The declaration and definition deliberately share the same signature.
    text = yati.read_text(encoding="utf-8")
    if old_cnmt_signature in text:
        if text.count(old_cnmt_signature) != 2:
            raise RuntimeError("Pinned upstream CNMT signatures changed")
        yati.write_text(text.replace(old_cnmt_signature, new_cnmt_signature), encoding="utf-8")
    text = yati.read_text(encoding="utf-8")
    if "yati->InstallCnmtNca(tickets, cnmt, collections)" in text:
        if text.count("yati->InstallCnmtNca(tickets, cnmt, collections)") != 2:
            raise RuntimeError("Pinned upstream CNMT callers changed")
        yati.write_text(text.replace("yati->InstallCnmtNca(tickets, cnmt, collections)", "yati->InstallCnmtNca(tickets, cnmt, collections, source)"), encoding="utf-8")
    replace(yati, "    R_TRY(nca::ParseCnmt(path, cnmt.header.program_id, header, cnmt.extended_header, infos));", '''    Result cnmt_result = Result_NspInvalidHeader;
    // A consumed stream and compressed NCZ bytes cannot be randomly read as NCA.
    if (source && !source->IsStream() && cnmt.name.ends_with(".cnmt.nca")) {
        R_UNLESS(cnmt.offset >= 0 && cnmt.size >= static_cast<s64>(sizeof(nca::Header)), Result_NspInvalidHeader);
        std::array<u8, sizeof(nca::Header)> raw_header{};
        u64 header_read{};
        R_TRY(source->Read(raw_header.data(), cnmt.offset, raw_header.size(), &header_read));
        R_UNLESS(header_read == raw_header.size(), Result_NspInvalidHeader);
        nca::Header source_header{};
        R_TRY(nca::DecryptHeader(raw_header.data(), keys, source_header));
        R_UNLESS(source_header.size == static_cast<u64>(cnmt.size), Result_NspInvalidHeader);
        std::string cnmt_stage;
        cnmt_result = nca::ParseCnmtFromSource(*source, static_cast<u64>(cnmt.offset), source_header, keys,
            header, cnmt.extended_header, infos, cnmt_stage);
    }
    if (R_FAILED(cnmt_result)) {
        R_TRY(nca::ParseCnmt(path, cnmt.header.program_id, header, cnmt.extended_header, infos));
    }''')
    # Section tables may contain gaps. CTR still uses key slot 2 for every section.
    replace(nca_source, "for (u32 i = 0; i < m_header.GetSectionCount(); i++) {", "for (u32 i = 0; i < NCA_SECTION_TOTAL; i++) {")
    replace(nca_source, "                const auto& fs_table = m_header.fs_table[i];", "                const auto& fs_table = m_header.fs_table[i];\n                if (!fs_header.IsValid() || !fs_table.IsValid()) continue;")
    # The fsp-srv fallback must not accept an empty directory or a truncated
    # plaintext CNMT when its reads return success with fewer bytes than asked.
    replace(nca_source, '#include "yati/nx/nca.hpp"', '#include "yati/nx/nca.hpp"\n#include "switchdesk/cnmt.hpp"')
    replace(nca_source, "#include <cstdlib>", "#include <cstdlib>\n#include <string_view>")
    text = nca_source.read_text(encoding="utf-8")
    begin = text.index("Result ParseCnmt(")
    end = text.index("Result ParseControl(", begin)
    checked_cnmt = '''Result ParseCnmt(const fs::FsPath& path, u64 program_id, ncm::PackagedContentMeta& header, std::vector<u8>& extended_header, std::vector<NcmPackagedContentInfo>& infos) {
    fs::FsNativeId fs{program_id, FsFileSystemType_ContentMeta, path, FsContentAttributes_All};
    R_TRY(fs.GetFsOpenResult());
    fs::Dir dir;
    R_TRY(fs.OpenDirectory("/", FsDirOpenMode_ReadFiles, &dir));
    std::string cnmt_name;
    u32 visited{};
    for (;;) {
        s64 total_entries{};
        FsDirectoryEntry entry{};
        R_TRY(dir.Read(&total_entries, 1, &entry));
        if (total_entries == 0) break;
        // Match the userspace PFS0 entry bound and allow accompanying files.
        R_UNLESS(total_entries == 1 && visited++ < 0x100, Result_NspInvalidHeader);
        const auto name_end = static_cast<const char*>(std::memchr(entry.name, '\\0', sizeof(entry.name)));
        R_UNLESS(name_end && name_end != entry.name, Result_NspInvalidHeader);
        const auto name = std::string_view(entry.name, name_end - entry.name);
        if (!name.ends_with(".cnmt")) continue;
        R_UNLESS(cnmt_name.empty(), Result_NspInvalidHeader);
        cnmt_name.assign(name);
    }
    R_UNLESS(!cnmt_name.empty(), Result_NspInvalidHeader);
    fs::File file;
    R_TRY(fs.OpenFile(fs::AppendPath("/", cnmt_name.c_str()), FsOpenMode_Read, &file));
    s64 size{};
    R_TRY(file.GetSize(&size));
    R_UNLESS(size >= 0, Result_NspInvalidHeader);
    std::string stage;
    return ParseCnmtContents(static_cast<u64>(size), [&](void* buffer, u64 offset, u64 count, u64* actual) -> Result {
        return file.Read(static_cast<s64>(offset), buffer, static_cast<s64>(count), 0, actual);
    }, header, extended_header, infos, stage);
}

'''
    nca_source.write_text(text[:begin] + checked_cnmt + text[end:], encoding="utf-8")
    # Managed installs must not inherit another installer's skip/downgrade settings.
    override_flags = ("allow_downgrade", "skip_if_already_installed", "ticket_only", "skip_base", "skip_patch", "skip_addon", "skip_data_patch", "skip_ticket")
    override_header = destination / "sphaira/include/yati/yati.hpp"
    declarations = "".join(f"    std::optional<bool> {flag}{{}};\n" for flag in override_flags)
    replace(override_header, "struct ConfigOverride {\n", "struct ConfigOverride {\n" + declarations)
    for flag in override_flags:
        replace(yati, f"config.{flag} = App::GetApp()->m_{flag}.Get();", f"config.{flag} = override.{flag}.value_or(App::GetApp()->m_{flag}.Get());")
    # Follow DBI's documented warning behavior for explicitly selected repacked
    # content, while continuing to calculate every NCA's actual stream digest.
    replace(override_header, "#include <optional>", "#include <optional>\n#include <functional>\n#include \"switchdesk/install_hash_policy.hpp\"")
    replace(override_header, "struct Config {\n", "struct Config {\n    bool allow_nca_hash_mismatch{};\n    std::function<Result(const switchdesk::NcaHashWarning&)> nca_hash_warning{};\n")
    replace(override_header, "    std::optional<bool> sd_card_install{};", "    std::optional<bool> sd_card_install{};\n    std::optional<bool> allow_nca_hash_mismatch{};\n    std::function<Result(const switchdesk::NcaHashWarning&)> nca_hash_warning{};")
    replace(yati, "Result Yati::Setup(const ConfigOverride& override) {", "Result Yati::Setup(const ConfigOverride& override) {\n    config.allow_nca_hash_mismatch = override.allow_nca_hash_mismatch.value_or(false);\n    config.nca_hash_warning = override.nca_hash_warning;")
    replace(yati, "    config.title_ids = override.title_ids;", "    config.title_ids = override.title_ids;\n    R_UNLESS(!config.allow_nca_hash_mismatch || (!config.skip_nca_hash_verify && config.nca_hash_warning), Result_YatiInvalidNcaSha256);")
    replace(yati, '''            R_UNLESS(!std::memcmp(&nca.content_id, nca.hash, sizeof(nca.content_id)), Result_YatiInvalidNcaSha256);''', '''            const auto expected = std::span<const std::uint8_t, 16>(reinterpret_cast<const std::uint8_t*>(&nca.content_id), 16);
            const auto actual = std::span<const std::uint8_t, 32>(nca.hash, 32);
            const auto policy = config.allow_nca_hash_mismatch ? switchdesk::NcaHashPolicy::Warn : switchdesk::NcaHashPolicy::Strict;
            R_UNLESS(switchdesk::EvaluateNcaHash(expected, actual, policy) == switchdesk::NcaHashDecision::Warning, Result_YatiInvalidNcaSha256);
            R_TRY(config.nca_hash_warning(switchdesk::MakeNcaHashWarning(nca.name, expected, actual)));''')
    # Signature compatibility is independent of filename/content-ID digest
    # compatibility. Verify the original header even when warnings are enabled.
    replace(override_header, '#include "switchdesk/install_hash_policy.hpp"', '#include "switchdesk/install_hash_policy.hpp"\n#include "switchdesk/install_signature_policy.hpp"')
    hash_config = "struct Config {\n    bool allow_nca_hash_mismatch{};\n    std::function<Result(const switchdesk::NcaHashWarning&)> nca_hash_warning{};"
    replace(override_header, hash_config, hash_config + "\n    bool allow_nca_signature_mismatch{};\n    std::function<Result(const switchdesk::NcaSignatureWarning&)> nca_signature_warning{};")
    hash_override = "    std::optional<bool> sd_card_install{};\n    std::optional<bool> allow_nca_hash_mismatch{};\n    std::function<Result(const switchdesk::NcaHashWarning&)> nca_hash_warning{};"
    replace(override_header, hash_override, hash_override + "\n    std::optional<bool> allow_nca_signature_mismatch{};\n    std::function<Result(const switchdesk::NcaSignatureWarning&)> nca_signature_warning{};")
    replace(yati, "    config.nca_hash_warning = override.nca_hash_warning;", "    config.nca_hash_warning = override.nca_hash_warning;\n    config.allow_nca_signature_mismatch = override.allow_nca_signature_mismatch.value_or(false);\n    config.nca_signature_warning = override.nca_signature_warning;")
    replace(yati, "    R_UNLESS(!config.allow_nca_hash_mismatch || (!config.skip_nca_hash_verify && config.nca_hash_warning), Result_YatiInvalidNcaSha256);", "    R_UNLESS(!config.allow_nca_hash_mismatch || (!config.skip_nca_hash_verify && config.nca_hash_warning), Result_YatiInvalidNcaSha256);\n    R_UNLESS(!config.allow_nca_signature_mismatch || (!config.skip_rsa_header_fixed_key_verify && config.nca_signature_warning), Result_NcaFailedNcaHeaderHashVerify);")
    replace(yati, "                    R_TRY(nca::VerifyFixedKey(header));", '''                    const auto signature_policy = config.allow_nca_signature_mismatch ? switchdesk::NcaSignaturePolicy::Warn : switchdesk::NcaSignaturePolicy::Strict;
                    const auto signed_header = std::span<const std::uint8_t, switchdesk::NcaSignedHeaderSize>(reinterpret_cast<const std::uint8_t*>(&header.magic), switchdesk::NcaSignedHeaderSize);
                    R_TRY(switchdesk::VerifyNcaHeaderSignature(header, signed_header, signature_policy, t->nca->name,
                        [](auto& candidate) -> Result { return nca::VerifyFixedKey(candidate); },
                        [](auto bytes) {
                            std::array<std::uint8_t, 32> digest{};
                            sha256CalculateHash(digest.data(), bytes.data(), bytes.size());
                            return digest;
                        }, config.nca_signature_warning));''')
    replace(destination / "sphaira/source/yati/container/xci.cpp", "    R_UNLESS(out.header.magic == HFS0_MAGIC, Result_XciBadMagic);", "    R_UNLESS(out.header.magic == HFS0_MAGIC, Result_XciBadMagic);\n    R_UNLESS(out.header.total_files <= 0x10000 && out.header.string_table_size <= 16 * 1024 * 1024, Result_XciBadMagic);")
    replace(destination / "sphaira/source/yati/container/xci.cpp", "    if (R_SUCCEEDED(rc)) {", "    if (R_SUCCEEDED(rc)) {\n        R_UNLESS(out.hfs0.header.total_files <= 16, Result_XciBadMagic);")
    replace(destination / "sphaira/source/yati/container/xci.cpp", "        out.string_table.emplace_back(string_table.data() + out.file_table[i].name_offset);", "        const auto index = out.file_table[i].name_offset;\n        R_UNLESS(index < string_table.size(), Result_XciBadMagic);\n        const auto end = static_cast<const char*>(std::memchr(string_table.data() + index, '\\0', std::min<std::size_t>(256, string_table.size() - index)));\n        R_UNLESS(end, Result_XciBadMagic);\n        out.string_table.emplace_back(string_table.data() + index, end - string_table.data() - index);")
    replace(destination / "sphaira/source/yati/container/nsp.cpp", "R_UNLESS(std::memchr(name, '\\0', name_size), Result_NspInvalidHeader);", "R_UNLESS(std::memchr(name, '\\0', std::min<std::size_t>(256, name_size)), Result_NspInvalidHeader);")
    replace(destination / "sphaira/source/yati/container/xci.cpp", "#include <cstring>", "#include <cstring>\n#include <algorithm>")
    replace(destination / "sphaira/CMakeLists.txt", "    NAME ${CMAKE_PROJECT_NAME}", "    NAME SwitchDesk")
    cmake = destination / "sphaira/CMakeLists.txt"
    code = cmake.read_text(encoding="utf-8")
    begin = code.index('execute_process(')
    end = code.index('if (DEFINED sphaira_VERSION_OVERRIDE)', begin)
    if 'if (EXISTS "${CMAKE_SOURCE_DIR}/.git")' not in code:
        git_calls = code[begin:end]
        code = code[:begin] + 'if (EXISTS "${CMAKE_SOURCE_DIR}/.git")\n' + git_calls + 'else()\nset(GIT_COMMIT "${SWITCHDESK_SOURCE_ID}")\nset(GIT_BRANCH "source")\nset(GIT_DIRTY "")\nendif()\n\n' + code[end:]
        cmake.write_text(code, encoding="utf-8")
    # Keep this fork's settings/cache separate from an existing Sphaira install.
    for source in (destination / "sphaira").rglob("*"):
        if source.suffix in (".cpp", ".hpp", ".c", ".h"):
            text = source.read_text(encoding="utf-8")
            updated = text.replace("/config/sphaira", "/config/SwitchDesk").replace("/switch/sphaira", "/switch/SwitchDesk")
            if updated != text: source.write_text(updated, encoding="utf-8")
    # Pre-download exact dependency revisions via HTTPS archives, so a git proxy
    # does not prevent FetchContent from preparing the corresponding source.
    pending = [destination / "sphaira/CMakeLists.txt"]
    seen = set(); cache = []
    lock = __import__("json").loads((ROOT / "build-dependencies.lock.json").read_text())
    expected = {item["file"]: item["sha256"] for item in lock.get("sources", [])}
    while pending:
        cmake_file = pending.pop()
        if not cmake_file.exists(): continue
        cmake_text = cmake_file.read_text(encoding="utf-8")
        declarations = re.findall(r"FetchContent_Declare\((\w+)\s+GIT_REPOSITORY https://github.com/([^\s]+?)\.git\s+GIT_TAG ([^\s)]+)", cmake_text)
        for name, repo, ref in declarations:
            if name in seen: continue
            seen.add(name)
            dep = ROOT / "vendor/deps" / name; bundle = ROOT / "vendor/cache" / (name + "-" + ref + ".zip")
            if not bundle.exists() and not args.offline: download(f"https://codeload.github.com/{repo}/zip/{ref}", bundle, expected.get(bundle.name))
            if bundle.exists() and bundle.name in expected and hashlib.sha256(bundle.read_bytes()).hexdigest() != expected[bundle.name]: raise RuntimeError("Dependency hash mismatch: " + name)
            if not dep.exists():
                if not bundle.exists():
                    if args.offline: raise RuntimeError("Missing pinned dependency archive: " + name)
                    raise RuntimeError("Download did not publish dependency archive: " + name)
                unpack(bundle, dep)
            cmake_path = dep.as_posix()
            if sys.platform == "win32" and os.environ.get("MSYSTEM"):
                cmake_path = subprocess.check_output(["cygpath", "-u", str(dep.resolve())], text=True).strip()
            cache.append(f'set(FETCHCONTENT_SOURCE_DIR_{name.upper()} "{cmake_path}" CACHE PATH "" FORCE)')
            pending.extend([dep / "CMakeLists.txt", dep / "build/cmake/CMakeLists.txt"])
    config = ROOT / "vendor/dependencies.cmake"; config.parent.mkdir(parents=True, exist_ok=True); config.write_text('\n'.join(cache), encoding="utf-8")
    print(f"Prepared {destination}\nCMake preload: {config}")

if __name__ == "__main__": main()
