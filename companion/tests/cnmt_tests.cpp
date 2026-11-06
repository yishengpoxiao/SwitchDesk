#include "cnmt_test_support.hpp"
#include <cassert>
#include <iostream>
#include <limits>
#include <span>
#include <stdexcept>

#if defined(_WIN32) || defined(__CYGWIN__) || defined(__MSYS__)
#include <windows.h>
#include <bcrypt.h>
#else
#include <openssl/evp.h>
#endif

namespace {
using Block = std::array<u8, 16>;
unsigned decrypt_calls{};
Result decrypt_error{};
unsigned checks{};

auto AesBlock(const Block& key, const Block& input, bool encrypt) -> Block {
    Block output{};
#if defined(_WIN32) || defined(__CYGWIN__) || defined(__MSYS__)
    BCRYPT_ALG_HANDLE algorithm{};
    BCRYPT_KEY_HANDLE handle{};
    if (BCryptOpenAlgorithmProvider(&algorithm, BCRYPT_AES_ALGORITHM, nullptr, 0) != 0)
        throw std::runtime_error("BCrypt AES provider failed");
    const auto mode = const_cast<wchar_t*>(BCRYPT_CHAIN_MODE_ECB);
    if (BCryptSetProperty(algorithm, BCRYPT_CHAINING_MODE, reinterpret_cast<PUCHAR>(mode),
        sizeof(BCRYPT_CHAIN_MODE_ECB), 0) != 0) throw std::runtime_error("BCrypt ECB mode failed");
    DWORD object_size{}, result_size{};
    if (BCryptGetProperty(algorithm, BCRYPT_OBJECT_LENGTH, reinterpret_cast<PUCHAR>(&object_size),
        sizeof(object_size), &result_size, 0) != 0) throw std::runtime_error("BCrypt key object size failed");
    std::vector<u8> object(object_size);
    if (BCryptGenerateSymmetricKey(algorithm, &handle, object.data(), object_size,
        const_cast<u8*>(key.data()), key.size(), 0) != 0) throw std::runtime_error("BCrypt key failed");
    ULONG written{};
    const auto operation = encrypt ? BCryptEncrypt : BCryptDecrypt;
    const auto status = operation(handle, const_cast<u8*>(input.data()), input.size(), nullptr,
        nullptr, 0, output.data(), output.size(), &written, 0);
    BCryptDestroyKey(handle);
    BCryptCloseAlgorithmProvider(algorithm, 0);
    if (status != 0 || written != output.size()) throw std::runtime_error("BCrypt AES block failed");
#else
    auto context = std::unique_ptr<EVP_CIPHER_CTX, decltype(&EVP_CIPHER_CTX_free)>(
        EVP_CIPHER_CTX_new(), EVP_CIPHER_CTX_free);
    if (!context || EVP_CipherInit_ex(context.get(), EVP_aes_128_ecb(), nullptr, key.data(), nullptr,
        encrypt ? 1 : 0) != 1 || EVP_CIPHER_CTX_set_padding(context.get(), 0) != 1)
        throw std::runtime_error("OpenSSL AES provider failed");
    int written{}, final{};
    if (EVP_CipherUpdate(context.get(), output.data(), &written, input.data(), input.size()) != 1 ||
        EVP_CipherFinal_ex(context.get(), output.data() + written, &final) != 1 || written + final != 16)
        throw std::runtime_error("OpenSSL AES block failed");
#endif
    return output;
}

void AesCtr(const Block& key, u64 section_ctr, u64 offset, std::span<u8> bytes) {
    while (!bytes.empty()) {
        Block counter{};
        const auto block = offset / 16;
        for (unsigned i = 0; i < 8; ++i) {
            counter[7 - i] = static_cast<u8>(section_ctr >> (i * 8));
            counter[15 - i] = static_cast<u8>(block >> (i * 8));
        }
        const auto stream = AesBlock(key, counter, true);
        const auto skip = static_cast<std::size_t>(offset % 16);
        const auto count = std::min(bytes.size(), 16 - skip);
        for (std::size_t i = 0; i < count; ++i) bytes[i] ^= stream[skip + i];
        bytes = bytes.subspan(count);
        offset += count;
    }
}

struct MemorySource final : sphaira::yati::source::Base {
    std::vector<u8> bytes;
    std::vector<std::pair<u64, u64>> reads;
    Result error{};
    std::size_t error_after{};
    bool short_read{};
    Result Read(void* buffer, s64 offset, s64 size, u64* got) override {
        *got = 0;
        if (offset < 0 || size < 0 || u64(offset) > bytes.size() || u64(size) > bytes.size() - u64(offset))
            return Result_TestRead;
        reads.emplace_back(offset, size);
        if (error && reads.size() > error_after) return error;
        *got = static_cast<u64>(size);
        if (short_read && *got) --*got;
        if (*got) std::memcpy(buffer, bytes.data() + offset, *got);
        return 0;
    }
};

template<class T> void Put(std::vector<u8>& data, std::size_t at, const T& value) {
    assert(at + sizeof(value) <= data.size());
    std::memcpy(data.data() + at, &value, sizeof(value));
}

struct Fixture {
    static constexpr u64 PackageOffset = 0x123; // Deliberately unaligned package start.
    static constexpr std::size_t PfsSize = 0x200, Strings = 32, Data = 16 + 24 + Strings;
    static constexpr std::size_t FileOffset = 13, CnmtOffset = Data + FileOffset;
    static constexpr std::size_t CnmtSize = 32 + 16 + 56 + 16 + 32;
    sphaira::nca::Header header{};
    sphaira::keys::Keys keys{};
    std::array<Block, 4> area{};
    std::vector<u8> pfs = std::vector<u8>(PfsSize);
    MemorySource source;
    unsigned section;

    explicit Fixture(unsigned selected = 0, u8 encryption = sphaira::nca::EncryptionType_AesCtr)
        : section(selected) {
        using namespace sphaira::nca;
        header.magic = NCA3_MAGIC;
        header.content_type = ContentType_Meta;
        header.size = 0x2C00;
        header.program_id = 0x0100000000010000ULL;
        header.kaek_index = 0;
        for (unsigned i = 0; i < 16; ++i) keys.wrapping_key[i] = static_cast<u8>(0x21 + i);
        for (unsigned i = 0; i < 4; ++i) {
            header.fs_table[i].media_start_offset = 6 + 4 * i;
            header.fs_table[i].media_end_offset = 10 + 4 * i;
            auto& fs = header.fs_header[i];
            fs.version = 2;
            fs.fs_type = i == section ? FileSystemType_PFS0 : FileSystemType_RomFS;
            fs.hash_type = HashType_HierarchicalSha256;
            fs.encryption_type = encryption;
            fs.section_ctr = 0x1020304050607080ULL + i;
            auto& hash = fs.hash_data.hierarchical_sha256_data;
            hash.block_size = 0x200;
            hash.layer_count = 2;
            hash.hash_layer = {0, 0x20};
            hash.pfs0_layer = {0x100, PfsSize};
            for (unsigned j = 0; j < 16; ++j) area[i][j] = static_cast<u8>(0x31 + i * 0x20 + j);
            const auto wrapped = AesBlock(keys.wrapping_key, area[i], true);
            std::memcpy(header.key_area[i].area, wrapped.data(), wrapped.size());
        }
        Put(pfs, 0, u32{0x30534650});
        Put(pfs, 4, u32{1});
        Put(pfs, 8, u32{Strings});
        Put(pfs, 16, u64{FileOffset});
        Put(pfs, 24, u64{CnmtSize});
        Put(pfs, 32, u32{0});
        constexpr char name[] = "Application.cnmt";
        std::memcpy(pfs.data() + 40, name, sizeof(name));
        sphaira::ncm::PackagedContentMeta meta{};
        meta.title_id = header.program_id;
        meta.title_version = 0x30000;
        meta.meta_type = 0x80;
        meta.meta_header.extended_header_size = 16;
        meta.meta_header.content_count = 1;
        meta.meta_header.content_meta_count = 1;
        meta.required_sys_version = 0x14000000;
        Put(pfs, CnmtOffset, meta);
        for (std::size_t i = 32; i < CnmtSize; ++i) pfs[CnmtOffset + i] = static_cast<u8>(i);
        Seal();
    }
    void Seal() {
        source.bytes.assign(PackageOffset + header.size, 0xA7);
        const auto pfs_at = header.fs_table[section].GetOffset() + 0x100;
        std::copy(pfs.begin(), pfs.end(), source.bytes.begin() + PackageOffset + pfs_at);
        const auto& fs = header.fs_header[section];
        if (fs.encryption_type != sphaira::nca::EncryptionType_None)
            AesCtr(area[2], fs.section_ctr, pfs_at, std::span(source.bytes).subspan(PackageOffset + pfs_at, pfs.size()));
        source.reads.clear();
    }
};

auto Parse(Fixture& fixture, sphaira::ncm::PackagedContentMeta& meta, std::vector<u8>& extended,
    std::vector<NcmPackagedContentInfo>& infos, std::string& stage, std::string* diagnostic = nullptr) -> Result {
    return sphaira::nca::ParseCnmtFromSource(fixture.source, Fixture::PackageOffset, fixture.header,
        fixture.keys, meta, extended, infos, stage, diagnostic);
}

void Accepted(Fixture& fixture) {
    sphaira::ncm::PackagedContentMeta meta{};
    std::vector<u8> extended;
    std::vector<NcmPackagedContentInfo> infos;
    std::string stage;
    const auto original = fixture.header;
    const auto result = Parse(fixture, meta, extended, infos, stage);
    if (result) std::cerr << "Expected successful parse, stage=" << stage << " result=" << std::hex << result << '\n';
    assert(result == 0);
    assert(std::memcmp(&fixture.header, &original, sizeof(original)) == 0);
    assert(meta.title_id == fixture.header.program_id && meta.title_version == 0x30000);
    assert(extended.size() == 16 && infos.size() == 1);
    assert(std::memcmp(&meta, fixture.pfs.data() + Fixture::CnmtOffset, sizeof(meta)) == 0);
    assert(std::equal(extended.begin(), extended.end(), fixture.pfs.begin() + Fixture::CnmtOffset + 32));
    assert(std::memcmp(infos.data(), fixture.pfs.data() + Fixture::CnmtOffset + 48, sizeof(NcmPackagedContentInfo)) == 0);
    for (const auto& [offset, count] : fixture.source.reads) {
        const auto section_start = Fixture::PackageOffset + fixture.header.fs_table[fixture.section].GetOffset();
        const auto section_end = Fixture::PackageOffset + fixture.header.fs_table[fixture.section].GetOffsetEnd();
        assert(offset >= section_start && offset <= section_end && count <= section_end - offset);
    }
    ++checks;
}

void Rejected(Fixture& fixture, Result expected = 0) {
    sphaira::ncm::PackagedContentMeta meta{};
    std::memset(&meta, 0x6D, sizeof(meta));
    const auto original_meta = meta;
    std::vector<u8> extended{0xCC};
    std::vector<NcmPackagedContentInfo> infos(1);
    infos[0].hash[0] = 0xEE;
    const auto original_info = infos[0];
    std::string stage;
    const auto original = fixture.header;
    const auto result = Parse(fixture, meta, extended, infos, stage);
    if (!result || (expected && result != expected))
        std::cerr << "Expected rejection, stage=" << stage << " result=" << std::hex << result << " expected=" << expected << '\n';
    assert(result != 0 && (!expected || result == expected));
    assert(!stage.empty());
    assert(std::memcmp(&fixture.header, &original, sizeof(original)) == 0);
    assert(std::memcmp(&meta, &original_meta, sizeof(meta)) == 0);
    assert(extended == std::vector<u8>{0xCC});
    assert(infos.size() == 1 && std::memcmp(infos.data(), &original_info, sizeof(original_info)) == 0);
    ++checks;
}

void PlaintextContentsTests() {
    Fixture fixture;
    const std::vector<u8> contents(fixture.pfs.begin() + Fixture::CnmtOffset,
        fixture.pfs.begin() + Fixture::CnmtOffset + Fixture::CnmtSize);
    const auto exercise = [&](const std::vector<u8>& bytes, u64 declared_size, int short_at, int error_at, bool valid) {
        sphaira::ncm::PackagedContentMeta meta{}; meta.title_id = 0x12345678;
        const auto original_meta = meta;
        std::vector<u8> extended{0xCC};
        std::vector<NcmPackagedContentInfo> infos(1); infos[0].hash[0] = 0xEE;
        const auto original_info = infos[0];
        std::string stage;
        int calls{};
        const auto rc = sphaira::nca::ParseCnmtContents(declared_size,
            [&](void* buffer, u64 offset, u64 count, u64* actual) -> Result {
                ++calls;
                assert(offset <= declared_size && count <= declared_size - offset);
                if (calls == error_at) return Result_TestRead;
                *actual = offset < bytes.size() ? std::min<u64>(count, bytes.size() - offset) : 0;
                if (calls == short_at && *actual) --*actual;
                if (*actual) std::memcpy(buffer, bytes.data() + offset, *actual);
                return 0;
            }, meta, extended, infos, stage);
        if (valid) {
            assert(rc == 0 && stage == "Complete");
            assert(std::memcmp(&meta, contents.data(), sizeof(meta)) == 0);
            assert(extended.size() == 16 && infos.size() == 1);
            assert(std::equal(extended.begin(), extended.end(), contents.begin() + 32));
            assert(std::memcmp(infos.data(), contents.data() + 48, sizeof(NcmPackagedContentInfo)) == 0);
        } else {
            assert(rc == (error_at ? Result_TestRead : Result_NspInvalidHeader));
            assert(std::memcmp(&meta, &original_meta, sizeof(meta)) == 0);
            assert(extended == std::vector<u8>{0xCC});
            assert(infos.size() == 1 && std::memcmp(infos.data(), &original_info, sizeof(original_info)) == 0);
        }
        ++checks;
    };
    exercise(contents, contents.size(), -1, -1, true);
    for (const auto size : {u64{0}, u64{31}, u64{63}, u64{Fixture::CnmtSize - 1}, UINT64_MAX})
        exercise(contents, size, -1, 0, false);
    for (int step = 1; step <= 3; ++step) {
        exercise(contents, contents.size(), step, 0, false);
        exercise(contents, contents.size(), -1, step, false);
    }
    for (const auto size : {std::size_t{1}, std::size_t{40}, std::size_t{80}})
        exercise(std::vector<u8>(contents.begin(), contents.begin() + size), contents.size(), -1, 0, false);
    for (const auto offset : {14, 16, 18}) {
        auto invalid = contents; Put(invalid, offset, u16{UINT16_MAX});
        exercise(invalid, invalid.size(), -1, 0, false);
    }
}
}

namespace sphaira::nca {
Result DecryptKeak(const keys::Keys& keys, Header& header) {
    ++decrypt_calls;
    if (decrypt_error) return decrypt_error;
    for (auto& entry : header.key_area) {
        Block wrapped{};
        std::memcpy(wrapped.data(), entry.area, 16);
        const auto decrypted = AesBlock(keys.wrapping_key, wrapped, false);
        std::memcpy(entry.area, decrypted.data(), 16);
    }
    return 0;
}
NcaReader::NcaReader(const Header& source_header, const void* source_key, u64 size,
    const std::shared_ptr<yati::source::Base>& input)
    : header(source_header), capacity(size), source(input) {
    std::memcpy(key.data(), source_key, key.size());
}
Result NcaReader::Read(void* buffer, s64 offset, s64 size, u64* got) {
    *got = 0;
    if (offset < 0 || size < 0 || u64(offset) > capacity || u64(size) > capacity - u64(offset))
        return Result_TestRead;
    R_TRY(source->Read(buffer, offset, size, got));
    for (unsigned i = 0; i < 4; ++i) {
        const auto& table = header.fs_table[i];
        if (table.GetOffset() <= u64(offset) && u64(offset) < table.GetOffsetEnd()) {
            if (header.fs_header[i].encryption_type != EncryptionType_None)
                AesCtr(key, header.fs_header[i].section_ctr, offset,
                    std::span(static_cast<u8*>(buffer), static_cast<std::size_t>(*got)));
            break;
        }
    }
    return 0;
}
}

int main() {
    PlaintextContentsTests();
    // NIST SP 800-38A ECB-AES128 vector verifies the host provider used below.
    const Block vector_key{0x2b,0x7e,0x15,0x16,0x28,0xae,0xd2,0xa6,0xab,0xf7,0x15,0x88,0x09,0xcf,0x4f,0x3c};
    const Block vector_input{0x6b,0xc1,0xbe,0xe2,0x2e,0x40,0x9f,0x96,0xe9,0x3d,0x7e,0x11,0x73,0x93,0x17,0x2a};
    const Block vector_expected{0x3a,0xd7,0x7b,0xb4,0x0d,0x7a,0x36,0x60,0xa8,0x9e,0xca,0xf3,0x24,0x66,0xef,0x97};
    assert(AesBlock(vector_key, vector_input, true) == vector_expected);
    assert(AesBlock(vector_key, vector_expected, false) == vector_input);
    ++checks;
    for (unsigned section = 0; section < 4; ++section) {
        Fixture fixture(section);
        const auto before = decrypt_calls;
        Accepted(fixture);
        Accepted(fixture); // Input key area must still be encrypted for a repeat inspection.
        assert(decrypt_calls == before + 2);
    }
    for (const auto encryption : {sphaira::nca::EncryptionType_None, sphaira::nca::EncryptionType_AesCtrSkipLayerHash}) {
        Fixture fixture(0, encryption);
        Accepted(fixture);
    }
    for (const u8 generation : {u8{0}, u8{1}, u8{0x14}, u8{0x20}}) {
        Fixture fixture;
        fixture.header.old_key_gen = generation <= 2 ? generation : 2;
        fixture.header.key_gen = generation <= 2 ? 0 : generation;
        const auto before = decrypt_calls;
        Accepted(fixture);
        assert(decrypt_calls == before + 1);
    }
    for (const auto old_field : {false, true}) {
        for (const u8 generation : {u8{0x21}, u8{0xFF}}) {
            Fixture fixture;
            fixture.header.old_key_gen = old_field ? generation : 2;
            fixture.header.key_gen = old_field ? 1 : generation;
            const auto before = decrypt_calls;
            Rejected(fixture, Result_NspInvalidHeader);
            assert(decrypt_calls == before && fixture.source.reads.empty());
        }
    }
    {
        Fixture fixture(3);
        fixture.header.old_key_gen = 2;
        fixture.header.key_gen = 0x14;
        sphaira::ncm::PackagedContentMeta meta{};
        std::vector<u8> extended;
        std::vector<NcmPackagedContentInfo> infos;
        std::string stage, diagnostic = "previous response";
        assert(Parse(fixture, meta, extended, infos, stage, &diagnostic) == 0 && stage == "Complete");
        assert(diagnostic.starts_with("cnmt-v3 "));
        assert(diagnostic.find("nca_offset=0x0000000000000123") != std::string::npos);
        assert(diagnostic.find("section=3 encryption=3 hash=2 keygen=20") != std::string::npos);
        assert(diagnostic.find("pfs_nca_offset=0x0000000000002500") != std::string::npos);
        assert(diagnostic.find("pfs_source_offset=0x0000000000002623") != std::string::npos);
        assert(diagnostic.ends_with("pfs_magic=0x30534650"));
        assert(diagnostic.size() < 256);
        ++checks;
    }
    {
        Fixture fixture;
        fixture.pfs[0] ^= 0xFF;
        fixture.Seal();
        sphaira::ncm::PackagedContentMeta meta{};
        std::vector<u8> extended;
        std::vector<NcmPackagedContentInfo> infos;
        std::string stage, diagnostic;
        assert(Parse(fixture, meta, extended, infos, stage, &diagnostic) == Result_NspBadMagic);
        assert(stage == "Pfs0Magic" && diagnostic.starts_with("cnmt-v3 "));
        assert(diagnostic.ends_with("pfs_magic=0x305346AF"));
        ++checks;
    }
    {
        Fixture fixture;
        fixture.source.error = Result_TestRead;
        sphaira::ncm::PackagedContentMeta meta{};
        std::vector<u8> extended;
        std::vector<NcmPackagedContentInfo> infos;
        std::string stage, diagnostic;
        assert(Parse(fixture, meta, extended, infos, stage, &diagnostic) == Result_TestRead);
        assert(stage == "ReadPfs0Header" && diagnostic.starts_with("cnmt-v3 "));
        assert(diagnostic.ends_with("pfs_magic=unread"));
        ++checks;
    }
    {
        Fixture fixture;
        Block wrong{}; wrong.fill(0xEF);
        const auto wrapped = AesBlock(fixture.keys.wrapping_key, wrong, true);
        std::memcpy(fixture.header.key_area[2].area, wrapped.data(), 16);
        Rejected(fixture, Result_NspBadMagic);
    }
    {
        Fixture fixture;
        fixture.header.rights_id.c[0] = 1;
        Rejected(fixture);
    }
    {
        Fixture fixture;
        fixture.header.kaek_index = 3;
        Rejected(fixture);
    }
    {
        Fixture fixture;
        fixture.header.magic = 0;
        Rejected(fixture);
    }
    {
        Fixture fixture;
        fixture.header.fs_table[0].media_end_offset = fixture.header.fs_table[0].media_start_offset - 1;
        Rejected(fixture);
    }
    {
        Fixture fixture;
        fixture.header.size = 0xC00;
        Rejected(fixture);
    }
    {
        Fixture fixture;
        fixture.header.size = (64ULL << 20) + 1;
        Rejected(fixture);
    }
    {
        Fixture fixture;
        fixture.header.fs_header[0].hash_data.hierarchical_sha256_data.pfs0_layer.offset = UINT64_MAX;
        Rejected(fixture);
    }
    {
        Fixture fixture;
        fixture.header.fs_header[0].hash_data.hierarchical_sha256_data.pfs0_layer.size = 15;
        Rejected(fixture);
    }
    {
        Fixture fixture;
        fixture.header.fs_header[0].hash_data.hierarchical_sha256_data.layer_count = 1;
        Rejected(fixture);
    }
    {
        Fixture fixture;
        fixture.header.fs_header[0].encryption_type = sphaira::nca::EncryptionType_AesXts;
        Rejected(fixture);
    }
    for (const auto count : {u32{0}, u32{257}}) {
        Fixture fixture;
        Put(fixture.pfs, 4, count); fixture.Seal(); Rejected(fixture);
    }
    for (const auto strings : {u32{0}, u32{UINT32_MAX}}) {
        Fixture fixture;
        Put(fixture.pfs, 8, strings); fixture.Seal(); Rejected(fixture);
    }
    {
        Fixture fixture;
        Put(fixture.pfs, 32, u32{Fixture::Strings}); fixture.Seal(); Rejected(fixture);
    }
    {
        Fixture fixture;
        std::fill_n(fixture.pfs.begin() + 40, Fixture::Strings, u8{'x'});
        fixture.Seal(); Rejected(fixture);
    }
    {
        Fixture fixture;
        std::fill_n(fixture.pfs.begin() + 40, Fixture::Strings, u8{0});
        const char name[] = "unrelated.bin";
        std::memcpy(fixture.pfs.data() + 40, name, sizeof(name));
        fixture.Seal(); Rejected(fixture);
    }
    {
        Fixture fixture;
        const std::vector<u8> original_cnmt(fixture.pfs.begin() + Fixture::CnmtOffset,
            fixture.pfs.begin() + Fixture::CnmtOffset + Fixture::CnmtSize);
        fixture.pfs.assign(Fixture::PfsSize, 0);
        Put(fixture.pfs, 0, u32{0x30534650});
        Put(fixture.pfs, 4, u32{2});
        Put(fixture.pfs, 8, u32{Fixture::Strings});
        for (const auto entry : {std::size_t{16}, std::size_t{40}}) {
            Put(fixture.pfs, entry, u64{Fixture::FileOffset});
            Put(fixture.pfs, entry + 8, u64{Fixture::CnmtSize});
        }
        Put(fixture.pfs, 56, u32{17});
        constexpr char names[] = "Application.cnmt\0Other.cnmt\0";
        std::memcpy(fixture.pfs.data() + 64, names, sizeof(names));
        std::copy(original_cnmt.begin(), original_cnmt.end(), fixture.pfs.begin() + 96 + Fixture::FileOffset);
        fixture.Seal(); Rejected(fixture);
    }
    for (const auto location : {std::size_t{16}, std::size_t{24}}) {
        Fixture fixture;
        Put(fixture.pfs, location, u64{UINT64_MAX}); fixture.Seal(); Rejected(fixture);
    }
    {
        Fixture fixture;
        Put(fixture.pfs, 24, u64{31}); fixture.Seal(); Rejected(fixture);
    }
    for (const auto location : {std::size_t{14}, std::size_t{16}, std::size_t{18}}) {
        Fixture fixture;
        Put(fixture.pfs, Fixture::CnmtOffset + location, u16{UINT16_MAX}); fixture.Seal(); Rejected(fixture);
    }
    for (const auto read_number : {std::size_t{0}, std::size_t{1}, std::size_t{2},
        std::size_t{3}, std::size_t{4}, std::size_t{5}}) {
        Fixture fixture;
        fixture.source.error = Result_TestRead;
        fixture.source.error_after = read_number;
        Rejected(fixture, Result_TestRead);
    }
    {
        Fixture fixture;
        fixture.source.short_read = true;
        Rejected(fixture);
    }
    {
        Fixture fixture;
        decrypt_error = 0x123456;
        Rejected(fixture, decrypt_error);
        decrypt_error = 0;
    }
    {
        Fixture fixture;
        for (const auto at : {std::size_t{14}, std::size_t{16}, std::size_t{18}})
            Put(fixture.pfs, Fixture::CnmtOffset + at, u16{0});
        Put(fixture.pfs, 24, u64{64}); // Just the fixed header plus CNMT digest.
        fixture.Seal();
        sphaira::ncm::PackagedContentMeta meta{};
        std::vector<u8> extended{1};
        std::vector<NcmPackagedContentInfo> infos(1);
        std::string stage;
        assert(Parse(fixture, meta, extended, infos, stage) == 0);
        assert(extended.empty() && infos.empty());
        ++checks;
    }
    {
        Fixture fixture;
        sphaira::ncm::PackagedContentMeta meta{};
        std::vector<u8> extended;
        std::vector<NcmPackagedContentInfo> infos;
        std::string stage;
        assert(sphaira::nca::ParseCnmtFromSource(fixture.source, UINT64_MAX, fixture.header,
            fixture.keys, meta, extended, infos, stage) != 0);
        assert(fixture.source.reads.empty());
        ++checks;
    }
    std::cout << "CNMT host checks passed: " << checks
        << " (production parser; synthetic AES fixtures, platform I/O/SPL doubles; no console access)\n";
}
