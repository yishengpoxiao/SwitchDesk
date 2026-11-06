// SwitchDesk userspace CNMT reader. GPL-3.0.
#include "switchdesk/cnmt.hpp"
#include <algorithm>
#include <cstdio>
#include <cstring>
#include <functional>
#include <limits>
#include <string_view>

namespace sphaira::nca {
namespace {
auto InRange(u64 offset, u64 length, u64 capacity) -> bool {
    return offset <= capacity && length <= capacity - offset;
}

struct NcaOffsetSource final : yati::source::Base {
    yati::source::Base& base;
    const u64 offset, length;
    NcaOffsetSource(yati::source::Base& source, u64 start, u64 size)
        : base(source), offset(start), length(size) { }
    Result Read(void* buffer, s64 position, s64 count, u64* read) override {
        *read = 0;
        R_UNLESS(position >= 0 && count >= 0 && InRange(position, count, length), Result_NspInvalidHeader);
        return base.Read(buffer, static_cast<s64>(offset + position), count, read);
    }
};
struct PfsHeader { u32 magic, count, strings, reserved; };
struct PfsEntry { u64 offset, size; u32 name, reserved; };
static_assert(sizeof(PfsHeader) == 0x10 && sizeof(PfsEntry) == 0x18);
}

Result ParseCnmtContents(u64 size, const std::function<Result(void*, u64, u64, u64*)>& read,
    ncm::PackagedContentMeta& meta, std::vector<u8>& extended,
    std::vector<NcmPackagedContentInfo>& infos, std::string& stage) {
    stage = "CnmtBounds";
    R_UNLESS(size >= sizeof(ncm::PackagedContentMeta) + 0x20 && size <= 64ULL * 1024 * 1024,
        Result_NspInvalidHeader);
    const auto read_exact = [&](void* buffer, u64 position, u64 count) -> Result {
        R_UNLESS(InRange(position, count, size), Result_NspInvalidHeader);
        if (!count) return 0;
        u64 actual{};
        R_TRY(read(buffer, position, count, &actual));
        R_UNLESS(actual == count, Result_NspInvalidHeader);
        return 0;
    };
    stage = "ReadMeta";
    ncm::PackagedContentMeta parsed{};
    R_TRY(read_exact(&parsed, 0, sizeof(parsed)));
    stage = "CnmtBounds";
    const u64 info_size = static_cast<u64>(parsed.meta_header.content_count) * sizeof(NcmPackagedContentInfo);
    const u64 meta_info_size = static_cast<u64>(parsed.meta_header.content_meta_count) * sizeof(NcmContentMetaInfo);
    const u64 extended_size = parsed.meta_header.extended_header_size;
    R_UNLESS(InRange(sizeof(parsed), extended_size, size)
        && InRange(sizeof(parsed) + extended_size, info_size, size)
        && InRange(sizeof(parsed) + extended_size + info_size, meta_info_size + 0x20, size), Result_NspInvalidHeader);
    std::vector<u8> parsed_extended(extended_size);
    std::vector<NcmPackagedContentInfo> parsed_infos(parsed.meta_header.content_count);
    stage = "ReadExtended";
    R_TRY(read_exact(parsed_extended.data(), sizeof(parsed), extended_size));
    stage = "ReadInfos";
    R_TRY(read_exact(parsed_infos.data(), sizeof(parsed) + extended_size, info_size));
    meta = parsed;
    extended = std::move(parsed_extended);
    infos = std::move(parsed_infos);
    stage = "Complete";
    return 0;
}

Result ParseCnmtFromSource(yati::source::Base& source, u64 nca_offset, const Header& header,
    const keys::Keys& keys, ncm::PackagedContentMeta& meta, std::vector<u8>& extended,
    std::vector<NcmPackagedContentInfo>& infos, std::string& stage, std::string* diagnostic) {
    u32 section_index = NCA_SECTION_TOTAL;
    u64 pfs_offset{};
    u32 observed_magic{};
    bool pfs_offset_known{}, magic_read{};
    const auto describe = [&] {
        if (!diagnostic) return;
        const auto section_known = section_index < NCA_SECTION_TOTAL;
        char text[256]{};
        std::snprintf(text, sizeof(text),
            "cnmt-v3 nca_offset=0x%016llX section=%d encryption=%d hash=%d keygen=%u",
            static_cast<unsigned long long>(nca_offset), section_known ? static_cast<int>(section_index) : -1,
            section_known ? static_cast<int>(header.fs_header[section_index].encryption_type) : -1,
            section_known ? static_cast<int>(header.fs_header[section_index].hash_type) : -1,
            static_cast<unsigned>(header.GetKeyGeneration()));
        *diagnostic = text;
        if (pfs_offset_known) {
            std::snprintf(text, sizeof(text), " pfs_nca_offset=0x%016llX pfs_source_offset=0x%016llX",
                static_cast<unsigned long long>(pfs_offset), static_cast<unsigned long long>(nca_offset + pfs_offset));
            *diagnostic += text;
        } else {
            *diagnostic += " pfs_offset=unresolved";
        }
        if (magic_read) {
            std::snprintf(text, sizeof(text), " pfs_magic=0x%08X", static_cast<unsigned>(observed_magic));
            *diagnostic += text;
        } else {
            *diagnostic += " pfs_magic=unread";
        }
    };
    stage = "NcaBounds";
    describe();
    constexpr auto max_offset = static_cast<u64>(std::numeric_limits<s64>::max());
    R_UNLESS(header.IsValid() && header.size >= sizeof(Header) && header.size <= 64ULL * 1024 * 1024,
        Result_NspInvalidHeader);
    R_UNLESS(InRange(nca_offset, header.size, max_offset), Result_NspInvalidHeader);

    stage = "FindSection";
    for (u32 i = 0; i < NCA_SECTION_TOTAL; ++i) {
        if (header.fs_header[i].IsValid() && header.fs_table[i].IsValid()
            && header.fs_header[i].fs_type == FileSystemType_PFS0) {
            R_UNLESS(section_index == NCA_SECTION_TOTAL, Result_NspInvalidHeader);
            section_index = i;
            describe();
        }
    }
    R_UNLESS(section_index < NCA_SECTION_TOTAL, Result_NcaBadMagic);
    const auto& table = header.fs_table[section_index];
    const auto& fs = header.fs_header[section_index];
    stage = "SectionBounds";
    R_UNLESS(table.GetOffset() >= sizeof(Header) && table.GetOffsetEnd() > table.GetOffset()
        && table.GetOffsetEnd() <= header.size, Result_NspInvalidHeader);
    stage = "SectionFormat";
    R_UNLESS(fs.hash_type == HashType_HierarchicalSha256
        && fs.hash_data.hierarchical_sha256_data.layer_count == 2, Result_NspInvalidHeader);
    const auto& layer = fs.hash_data.hierarchical_sha256_data.pfs0_layer;
    R_UNLESS(layer.size >= sizeof(PfsHeader) && InRange(layer.offset, layer.size, table.GetSize()), Result_NspInvalidHeader);
    pfs_offset = table.GetOffset() + layer.offset;
    pfs_offset_known = true;
    describe();

    // NCA CTR keys are selected by encryption type, never by section number.
    // Decrypt a copy: callers may inspect the same header again or install it later.
    auto decrypted_header = header;
    // The pinned key table has 0x20 entries. FixKey maps generation 0 to 0,
    // and positive generations to generation - 1 before the software fallback.
    stage = "KeyGeneration";
    R_UNLESS(header.GetKeyGeneration() <= 0x20, Result_NspInvalidHeader);
    stage = "SectionEncryption";
    R_UNLESS(fs.encryption_type == EncryptionType_None || fs.encryption_type == EncryptionType_AesCtr
        || fs.encryption_type == EncryptionType_AesCtrSkipLayerHash, Result_NspInvalidHeader);
    if (fs.encryption_type != EncryptionType_None) {
        stage = "RightsId";
        const auto* rights = reinterpret_cast<const u8*>(&header.rights_id);
        R_UNLESS(std::all_of(rights, rights + sizeof(header.rights_id), [](u8 byte) { return byte == 0; }), Result_NspInvalidHeader);
        stage = "KeyAreaIndex";
        R_UNLESS(header.kaek_index < 3, Result_NspInvalidHeader);
        stage = "DecryptKeak";
        R_TRY(DecryptKeak(keys, decrypted_header));
    }
    auto nca_source = std::make_shared<NcaOffsetSource>(source, nca_offset, header.size);
    NcaReader reader(decrypted_header, decrypted_header.key_area[2].area, header.size, nca_source);
    const auto read_exact = [&](void* buffer, u64 position, u64 count) -> Result {
        R_UNLESS(InRange(position, count, layer.size), Result_NspInvalidHeader);
        if (!count) return 0;
        u64 read{};
        R_TRY(reader.Read(buffer, static_cast<s64>(pfs_offset + position), static_cast<s64>(count), &read));
        R_UNLESS(read == count, Result_NspInvalidHeader);
        return 0;
    };

    stage = "ReadPfs0Header";
    PfsHeader pfs{};
    R_TRY(read_exact(&pfs, 0, sizeof(pfs)));
    observed_magic = pfs.magic;
    magic_read = true;
    describe();
    stage = "Pfs0Magic";
    R_UNLESS(pfs.magic == 0x30534650, Result_NspBadMagic);
    stage = "Pfs0Bounds";
    R_UNLESS(pfs.count > 0 && pfs.count <= 0x100 && pfs.strings > 0 && pfs.strings <= 1024 * 1024,
        Result_NspInvalidHeader);
    const u64 table_size = static_cast<u64>(pfs.count) * sizeof(PfsEntry);
    const u64 strings_offset = sizeof(pfs) + table_size;
    R_UNLESS(InRange(strings_offset, pfs.strings, layer.size), Result_NspInvalidHeader);
    const u64 data_offset = strings_offset + pfs.strings;
    stage = "ReadFileTable";
    std::vector<PfsEntry> files(pfs.count);
    R_TRY(read_exact(files.data(), sizeof(pfs), table_size));
    stage = "ReadStringTable";
    std::vector<char> names(pfs.strings);
    R_TRY(read_exact(names.data(), strings_offset, names.size()));

    const PfsEntry* cnmt = nullptr;
    stage = "FindCnmt";
    for (const auto& entry : files) {
        R_UNLESS(entry.name < names.size() && InRange(entry.offset, entry.size, layer.size - data_offset), Result_NspInvalidHeader);
        const auto* name = names.data() + entry.name;
        const auto* end = static_cast<const char*>(std::memchr(name, '\0', std::min<std::size_t>(256, names.size() - entry.name)));
        R_UNLESS(end && end != name, Result_NspInvalidHeader);
        if (!std::string_view(name, end - name).ends_with(".cnmt")) continue;
        R_UNLESS(!cnmt, Result_NspInvalidHeader);
        cnmt = &entry;
    }
    R_UNLESS(cnmt && cnmt->size >= sizeof(ncm::PackagedContentMeta), Result_NspInvalidHeader);
    const auto cnmt_offset = data_offset + cnmt->offset;
    return ParseCnmtContents(cnmt->size, [&](void* buffer, u64 position, u64 count, u64* actual) -> Result {
        // The file range was checked against the PFS0 layer above.
        return reader.Read(buffer, static_cast<s64>(pfs_offset + cnmt_offset + position), static_cast<s64>(count), actual);
    }, meta, extended, infos, stage);
}
}
