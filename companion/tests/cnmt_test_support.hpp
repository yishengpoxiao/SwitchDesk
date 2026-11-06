#pragma once

// Host-only platform doubles for compiling the production CNMT parser. The
// parser itself is not copied here. AES uses the host crypto provider, while
// SPL/key retrieval and libnx NcaReader are replaced by deterministic fixtures.
#include <algorithm>
#include <array>
#include <cstdint>
#include <cstring>
#include <functional>
#include <memory>
#include <string>
#include <vector>

using u8 = std::uint8_t;
using u16 = std::uint16_t;
using u32 = std::uint32_t;
using u64 = std::uint64_t;
using s64 = std::int64_t;
using Result = u32;

#define R_SUCCEEDED(rc) ((rc) == 0)
#define R_FAILED(rc) ((rc) != 0)
#define R_TRY(expr) do { const Result test_result = (expr); if (test_result) return test_result; } while (0)
#define R_UNLESS(expr, rc) do { if (!(expr)) return (rc); } while (0)
#define R_SUCCEED() return 0
#define NCA_SECTION_TOTAL 4
#define NCA3_MAGIC 0x3341434E

inline constexpr Result Result_NcaBadMagic = 0xA3F9;
inline constexpr Result Result_NspBadMagic = 0xA5F9;
inline constexpr Result Result_NspInvalidHeader = 0x151F9;
inline constexpr Result Result_TestRead = 0x456789;

struct FsRightsId { u8 c[16]; };
struct NcmContentMetaHeader {
    u16 extended_header_size, content_count, content_meta_count;
    u8 attributes, storage_id;
};
struct NcmContentInfo {
    u8 content_id[16];
    u32 size_low;
    u8 size_high, attr, content_type, id_offset;
};
struct NcmPackagedContentInfo { u8 hash[32]; NcmContentInfo info; };
struct NcmContentMetaInfo { u64 id; u32 version; u8 type, attr, padding[2]; };
static_assert(sizeof(NcmContentMetaHeader) == 8);
static_assert(sizeof(NcmPackagedContentInfo) == 0x38);

namespace sphaira {
namespace yati::source {
struct Base {
    virtual ~Base() = default;
    virtual Result Read(void*, s64, s64, u64*) = 0;
};
}
namespace keys {
struct Keys { std::array<u8, 16> wrapping_key{}; };
}
namespace ncm {
struct PackagedContentMeta {
    u64 title_id;
    u32 title_version;
    u8 meta_type, content_meta_platform;
    NcmContentMetaHeader meta_header;
    u8 install_type, reserved;
    u32 required_sys_version;
    u8 padding[4];
};
static_assert(sizeof(PackagedContentMeta) == 0x20);
}
namespace nca {
enum FileSystemType { FileSystemType_RomFS = 0, FileSystemType_PFS0 = 1 };
enum ContentType { ContentType_Program = 0, ContentType_Meta = 1 };
enum HashType { HashType_Auto = 0, HashType_HierarchicalSha256 = 2, HashType_HierarchicalIntegrity = 3 };
enum EncryptionType {
    EncryptionType_Auto = 0, EncryptionType_None = 1, EncryptionType_AesXts = 2,
    EncryptionType_AesCtr = 3, EncryptionType_AesCtrEx = 4,
    EncryptionType_AesCtrSkipLayerHash = 5, EncryptionType_AesCtrExSkipLayerHash = 6
};
struct SectionTableEntry {
    u32 media_start_offset, media_end_offset;
    u8 padding[8];
    bool IsValid() const { return media_start_offset && media_end_offset; }
    u64 GetOffset() const { return u64(media_start_offset) * 0x200; }
    u64 GetOffsetEnd() const { return u64(media_end_offset) * 0x200; }
    u64 GetSize() const { return GetOffsetEnd() - GetOffset(); }
};
struct LayerRegion { u64 offset, size; };
struct HierarchicalSha256Data {
    u8 master_hash[32];
    u32 block_size, layer_count;
    LayerRegion hash_layer, pfs0_layer, unused_layers[3];
    u8 reserved[0x80];
};
static_assert(sizeof(HierarchicalSha256Data) == 0xF8);
struct FsHeader {
    u16 version;
    u8 fs_type, hash_type, encryption_type, metadata_hash_type;
    u8 pad[2];
    union { HierarchicalSha256Data hierarchical_sha256_data; u8 integrity[0xF8]; } hash_data;
    u8 patch_info[0x40];
    u64 section_ctr;
    u8 rest[0xB8];
    bool IsValid() const { return version == 2; }
};
static_assert(sizeof(FsHeader) == 0x200);
struct KeyArea { u8 area[16]; };
struct Header {
    u8 signatures[0x200];
    u32 magic;
    u8 distribution_type, content_type, old_key_gen, kaek_index;
    u64 size, program_id;
    u32 context_id, sdk_version;
    u8 key_gen, sig_key_gen, padding[14];
    FsRightsId rights_id;
    SectionTableEntry fs_table[4];
    u8 fs_header_hash[4][32];
    KeyArea key_area[4];
    u8 reserved[0xC0];
    FsHeader fs_header[4];
    bool IsValid() const { return magic == NCA3_MAGIC; }
    u8 GetKeyGeneration() const { return std::max(old_key_gen, key_gen); }
    u8 GetSectionCount() const {
        u8 count{};
        for (unsigned i = 0; i < 4 && fs_header[i].IsValid() && fs_table[i].IsValid(); ++i) ++count;
        return count;
    }
};
static_assert(sizeof(Header) == 0xC00);

Result DecryptKeak(const keys::Keys&, Header&);
struct NcaReader final : yati::source::Base {
    NcaReader(const Header&, const void*, u64, const std::shared_ptr<yati::source::Base>&);
    Result Read(void*, s64, s64, u64*) override;
private:
    Header header;
    std::array<u8, 16> key;
    u64 capacity;
    std::shared_ptr<yati::source::Base> source;
};

Result ParseCnmtContents(u64, const std::function<Result(void*, u64, u64, u64*)>&,
    ncm::PackagedContentMeta&, std::vector<u8>&, std::vector<NcmPackagedContentInfo>&, std::string&);
Result ParseCnmtFromSource(yati::source::Base&, u64, const Header&, const keys::Keys&,
    ncm::PackagedContentMeta&, std::vector<u8>&, std::vector<NcmPackagedContentInfo>&, std::string&, std::string* = nullptr);
}
}
