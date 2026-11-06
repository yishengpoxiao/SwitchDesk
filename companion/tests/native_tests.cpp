#ifndef _POSIX_C_SOURCE
#define _POSIX_C_SOURCE 200809L
#endif
#include "switchdesk/wire.hpp"
#include "switchdesk/safety.hpp"
#include "switchdesk/restore_policy.hpp"
#include "switchdesk/operation_log.hpp"
#include "switchdesk/dbi_launch.hpp"
#include "switchdesk/export_reads.hpp"
#include "switchdesk/install_hash_policy.hpp"
#include "switchdesk/install_signature_policy.hpp"
#include <algorithm>
#include <cassert>
#include <cstdlib>
#include <iostream>
#include <random>
#include <vector>

namespace {
auto DbiImageTests() -> void {
    using namespace switchdesk;
    struct Segment { std::uint32_t file_off, size; };
    struct Header { std::uint32_t magic, size; std::array<Segment, 3> segments; };
    const Header valid{0x304f524e, 0x3000, {{{0, 0x1000}, {0x1000, 0x1000}, {0x2000, 0x1000}}}};
    assert(ValidDbiImage(valid, 0x3000) && ValidDbiImage(valid, 0x4000));
    assert(!ValidDbiImage(valid, 0x2fff));
    auto bad = valid; bad.magic = 0; assert(!ValidDbiImage(bad, 0x4000));
    bad = valid; bad.size = 0x7f; assert(!ValidDbiImage(bad, 0x4000));
    bad = valid; bad.segments[1].file_off = 0; assert(!ValidDbiImage(bad, 0x4000));
    bad = valid; bad.segments[0].size = 0; assert(!ValidDbiImage(bad, 0x4000));
    bad = valid; bad.segments[1].file_off++; assert(!ValidDbiImage(bad, 0x4000));
    bad = valid; bad.segments[2].size = UINT32_MAX; assert(!ValidDbiImage(bad, 0x4000));
    std::cout << "DBI NRO image checks passed: header, file length, aligned segments and bounds\n";
}
auto OperationLogTests() -> void {
    using namespace switchdesk;
    char pattern[] = "/tmp/switchdesk-operation-XXXXXX";
    const auto directory = mkdtemp(pattern); assert(directory);
    const auto base = std::string(directory) + "/record";
    assert(!OpenOperationRecord(base) && errno == ENOENT);
    const auto latest = [&] {
        auto file = OpenOperationRecord(base); assert(file);
        char text[64]{};
        const auto length = std::fread(text, 1, sizeof(text), file.get());
        assert(!std::ferror(file.get()));
        return std::string(text, length);
    };
    assert(PublishOperationRecord(base, "staging", "staging") && latest() == "staging");
    // A partial next record must never mask a complete earlier record.
    auto pending = std::fopen((base + ".writing.json.new").c_str(), "wb");
    assert(pending && std::fclose(pending) == 0 && latest() == "staging");
    assert(PublishOperationRecord(base, "writing", "writing") && latest() == "writing");
    assert(PublishOperationRecord(base, "complete", "complete") && latest() == "complete");
    assert(!PublishOperationRecord(base, "unknown-phase", "invalid"));
    assert(std::remove((base + ".final.json").c_str()) == 0 && latest() == "writing");
    assert(std::remove((base + ".writing.json").c_str()) == 0 && latest() == "staging");
    assert(std::remove((base + ".json").c_str()) == 0);
    for (const auto phase : {"verify-required", "needs-inspection"}) {
        assert(PublishOperationRecord(base, "installing", "installing") && latest() == "installing");
        assert(PublishOperationRecord(base, phase, phase) && latest() == phase);
        assert(std::remove((base + ".final.json").c_str()) == 0 && latest() == "installing");
        assert(std::remove((base + ".json").c_str()) == 0);
    }
    assert(rmdir(directory) == 0);
    std::cout << "Operation journal passed: durable restore/install phases, legacy records and interrupted publish\n";
}
auto InstallSignaturePolicyTests() -> void {
    using namespace switchdesk;
    assert(ParseNcaSignaturePolicy() == NcaSignaturePolicy::Strict);
    assert(ParseNcaSignaturePolicy("strict") == NcaSignaturePolicy::Strict);
    assert(ParseNcaSignaturePolicy("warn") == NcaSignaturePolicy::Warn);
    for (const auto invalid : {"", "WARN", "off", "skip", "strict ", "unknown"}) assert(!ParseNcaSignaturePolicy(invalid));
    assert(NcaHeaderSignatureMismatchResult == ((64U << 9) | 505U));
    std::array<std::uint8_t, 0xC00> header{};
    for (std::size_t i = 0; i < header.size(); ++i) header[i] = static_cast<std::uint8_t>(i / 0x100 + i);
    const auto original = header;
    const auto signed_header = std::span<const std::uint8_t>(header).subspan<0x200, NcaSignedHeaderSize>();
    std::array<std::uint8_t, 32> digest{};
    for (std::size_t i = 0; i < digest.size(); ++i) digest[i] = static_cast<std::uint8_t>(i);
    for (const auto policy : {NcaSignaturePolicy::Strict, NcaSignaturePolicy::Warn}) {
        for (const std::uint32_t result : {0U, 0x81F9U, 0x83F9U, 0xA5F9U, 0x81FAU}) {
            unsigned verified{}, hashed{}, warned{};
            const bool expected_warning = policy == NcaSignaturePolicy::Warn && result == NcaHeaderSignatureMismatchResult;
            const auto verify = [&](auto& candidate) {
                ++verified; assert(candidate == original); candidate[0x200] ^= 1; return result;
            };
            const auto hash = [&](auto bytes) {
                ++hashed; assert(bytes.data() == header.data() + 0x200 && bytes.size() == 0x200);
                assert(std::equal(bytes.begin(), bytes.end(), original.begin() + 0x200)); return digest;
            };
            const auto warning = [&](const NcaSignatureWarning& value) -> std::uint32_t {
                ++warned; assert(value.entry == "synthetic.nca");
                assert(value.header_sha256 == "000102030405060708090A0B0C0D0E0F101112131415161718191A1B1C1D1E1F");
                assert(value.verification_result == 0x81F9); return 0;
            };
            const auto actual = VerifyNcaHeaderSignature(header, signed_header, policy, "synthetic.nca", verify, hash, warning);
            assert(actual == (expected_warning ? 0 : result));
            assert(verified == 1 && hashed == unsigned(expected_warning) && warned == unsigned(expected_warning));
            assert(header == original);
        }
    }
    unsigned callbacks{};
    const auto failed_callback = VerifyNcaHeaderSignature(header, signed_header, NcaSignaturePolicy::Warn, "synthetic.nca",
        [](auto&) { return NcaHeaderSignatureMismatchResult; }, [&](auto) { return digest; },
        [&](const auto&) -> std::uint32_t { ++callbacks; return 0x1234; });
    assert(failed_callback == 0x1234 && callbacks == 1 && header == original);
    std::cout << "Install NCA signature policy passed: real verification, strict default, exact 81F9 warnings, immutable headers and callback errors\n";
}
auto InstallHashPolicyTests() -> void {
    using namespace switchdesk;
    assert(ParseNcaHashPolicy() == NcaHashPolicy::Strict);
    assert(ParseNcaHashPolicy("strict") == NcaHashPolicy::Strict);
    assert(ParseNcaHashPolicy("warn") == NcaHashPolicy::Warn);
    for (const auto invalid : {"", "WARN", "off", "skip", "strict ", "unknown"}) assert(!ParseNcaHashPolicy(invalid));
    std::array<std::uint8_t, 16> expected{};
    std::array<std::uint8_t, 32> actual{};
    for (std::size_t i = 0; i < actual.size(); ++i) actual[i] = static_cast<std::uint8_t>(i);
    std::copy_n(actual.begin(), expected.size(), expected.begin());
    assert(EvaluateNcaHash(expected, actual, NcaHashPolicy::Strict) == NcaHashDecision::Match);
    assert(EvaluateNcaHash(expected, actual, NcaHashPolicy::Warn) == NcaHashDecision::Match);
    const auto original_expected = expected;
    const auto original_actual = actual;
    expected.back() ^= 0x80;
    assert(EvaluateNcaHash(expected, actual, NcaHashPolicy::Strict) == NcaHashDecision::Reject);
    assert(EvaluateNcaHash(expected, actual, NcaHashPolicy::Warn) == NcaHashDecision::Warning);
    const auto warning = MakeNcaHashWarning("synthetic.cnmt.nca", expected, actual);
    assert(warning.entry == "synthetic.cnmt.nca");
    assert(warning.expected_content_id == "000102030405060708090A0B0C0D0E8F");
    assert(warning.actual_sha256 == "000102030405060708090A0B0C0D0E0F101112131415161718191A1B1C1D1E1F");
    assert(actual == original_actual);
    expected.back() ^= 0x80; assert(expected == original_expected);
    std::cout << "Install NCA hash policy passed: strict default, explicit warnings, exact digest reports and immutable inputs\n";
}
auto ExportReadTests() -> void {
    using namespace switchdesk;
    // A short header, two NCA-like regions, and small ticket/certificate regions.
    const std::array<std::uint64_t, 6> ends{320, Chunk + 123, Chunk + 127, 2 * Chunk + 19, 2 * Chunk + 27, 2 * Chunk + 43};
    const auto size = ends.back();
    std::vector<std::uint8_t> buffer(Chunk);
    std::uint64_t calls{};
    const auto segmented = [&](std::span<std::uint8_t> target, std::uint64_t position) -> ExportSegmentRead {
        ++calls;
        const auto end = std::find_if(ends.begin(), ends.end(), [position](auto value) { return value > position; });
        assert(end != ends.end());
        const auto count = std::min<std::uint64_t>(target.size(), *end - position);
        for (std::uint64_t i = 0; i < count; ++i) target[static_cast<std::size_t>(i)] = static_cast<std::uint8_t>(position + i);
        return {0, count};
    };
    for (std::uint64_t offset = 0; offset < size;) {
        const auto count = std::min<std::uint64_t>(buffer.size(), size - offset);
        const auto read = ReadExportChunk(std::span(buffer).first(static_cast<std::size_t>(count)), offset, size, segmented);
        assert(read.state == ExportReadState::Complete && read.completed == count);
        for (std::uint64_t i = 0; i < count; ++i) assert(buffer[static_cast<std::size_t>(i)] == static_cast<std::uint8_t>(offset + i));
        offset += count;
    }
    assert(calls > 3); // Three protocol chunks require additional segment reads.
    for (const auto offset : std::array<std::uint64_t, 4>{0, 320, Chunk + 123, size - 1}) {
        const auto count = std::min<std::uint64_t>(19, size - offset);
        const auto read = ReadExportChunk(std::span(buffer).first(static_cast<std::size_t>(count)), offset, size, segmented);
        assert(read.state == ExportReadState::Complete && read.completed == count);
        for (std::uint64_t i = 0; i < count; ++i) assert(buffer[static_cast<std::size_t>(i)] == static_cast<std::uint8_t>(offset + i));
    }
    const auto before_empty = calls;
    assert(ReadExportChunk(std::span(buffer).first(0), size, size, segmented).state == ExportReadState::Complete);
    assert(calls == before_empty);
    assert(ReadExportChunk(std::span(buffer).first(1), size, size, segmented).state == ExportReadState::OutOfRange);
    assert(ReadExportChunk(std::span(buffer).first(0), size + 1, size, segmented).state == ExportReadState::OutOfRange);
    assert(ReadExportChunk(std::span(buffer).first(2), UINT64_MAX - 1, UINT64_MAX, segmented).state == ExportReadState::OutOfRange);
    assert(calls == before_empty);

    // Exercise crossing 4 GiB with a tiny buffer and nonzero destination offset.
    std::array<std::uint8_t, 32> guarded{}; guarded.fill(0xEE);
    constexpr auto large_offset = (1ULL << 32) - 8;
    auto target = std::span(guarded).subspan(3, 21);
    std::uint64_t high_calls{};
    std::uint64_t high_completed{};
    const auto high = ReadExportChunk(target, large_offset, large_offset + target.size(),
        [&](std::span<std::uint8_t> part, std::uint64_t position) -> ExportSegmentRead {
            assert(position == large_offset + high_completed);
            const auto count = std::min<std::size_t>(5, part.size()); ++high_calls;
            for (std::size_t i = 0; i < count; ++i) part[i] = static_cast<std::uint8_t>(position + i);
            high_completed += count;
            return {0, count};
        });
    assert(high.state == ExportReadState::Complete && high.completed == target.size() && high_calls == 5);
    for (std::size_t i = 0; i < target.size(); ++i) assert(target[i] == static_cast<std::uint8_t>(large_offset + i));
    for (std::size_t i = 0; i < guarded.size(); ++i) if (i < 3 || i >= 24) assert(guarded[i] == 0xEE);

    auto stalled = ReadExportChunk(target, 0, target.size(), [](auto, auto) -> ExportSegmentRead { return {}; });
    assert(stalled.state == ExportReadState::NoProgress && stalled.completed == 0);
    std::uint64_t attempts{};
    const auto partial_stall = ReadExportChunk(target, 0, target.size(), [&](auto, auto) -> ExportSegmentRead { return {0, ++attempts == 1 ? 2ULL : 0ULL}; });
    assert(partial_stall.state == ExportReadState::NoProgress && partial_stall.completed == 2 && attempts == 2);
    attempts = 0;
    const auto failed = ReadExportChunk(target, 0, target.size(), [&](auto, auto) -> ExportSegmentRead { return ++attempts == 1 ? ExportSegmentRead{0, 2} : ExportSegmentRead{0x1234, 5}; });
    assert(failed.state == ExportReadState::ReadError && failed.error == 0x1234 && failed.completed == 2 && failed.reported == 5 && attempts == 2);
    const auto oversized = ReadExportChunk(target, 0, target.size(), [](auto part, auto) -> ExportSegmentRead { return {0, part.size() + 1}; });
    assert(oversized.state == ExportReadState::InvalidCount && oversized.completed == 0 && oversized.reported == target.size() + 1);
    std::cout << "Export reads passed: segmented NSP chunks, boundaries/tail, 64-bit offsets, bounds, no-progress, source errors and invalid counts\n";
}
}

int main() {
    DbiImageTests();
    OperationLogTests();
    InstallSignaturePolicyTests();
    using namespace switchdesk;
    InstallHashPolicyTests();
    ExportReadTests();
    assert(RestoreFits(64ULL << 20, 64ULL << 20, 1ULL << 20, 1ULL << 20, 10));
    assert(!RestoreFits(64ULL << 20, 1ULL << 20, 1ULL << 20, 1ULL << 20, 10));
    assert(!RestoreFits(1024, 64ULL << 20, 10, 10, 1));
    assert(!RestoreFits(UINT64_MAX, UINT64_MAX, UINT64_MAX, 1, 1));
    assert(!RestoreFits(UINT64_MAX, UINT64_MAX, 1, UINT64_MAX, 1));
    assert(!RestoreFits(UINT64_MAX, UINT64_MAX, 1, 1, UINT64_MAX));
    assert(ValidOperationId("0123456789abcdef0123456789ABCDEF"));
    assert(!ValidOperationId("../operations") && !ValidOperationId(std::string(32, 'x')));
    assert(SameDigest(std::string(64, 'a'), std::string(64, 'A')));
    assert(!SameDigest(std::string(64, 'G'), std::string(64, 'g')));
    assert(!SameDigest("", "") && !SameDigest(std::string(64, 'A'), std::string(64, 'B')));
    assert(ValidRemote("/")); assert(ValidRelative("nested/save.dat")); assert(!ValidRelative("../x")); assert(!ValidRemote("/x/../Nintendo"));
    assert(!Writable("/EMUmmc/raw1")); assert(!Writable("/switch/SwitchDesk/staging/x")); assert(Writable("/switch/tools/config.ini"));
    assert(Writable("/switch") && !MovableOrRemovable("/Switch"));
    assert(Writable("/config") && !MovableOrRemovable("/CONFIG"));
    assert(MovableOrRemovable("/switch/homebrew") && MovableOrRemovable("/config/homebrew"));
    assert(!MovableOrRemovable("/config/SwitchDesk") && !MovableOrRemovable("/switch/SwitchDesk/staging/x"));
    // Cheat exception: exact title/build IDs, standard Atmosphere text files and legacy .cheat files only.
    const std::string cheat_root = "/atmosphere/contents/0100000000010000/cheats/";
    assert(IsCheatFile(cheat_root + "0d784a915f5b4ac2.txt"));
    assert(IsCheatFile(cheat_root + "0d784a915f5b4ac2.txt.off"));
    assert(IsCheatFile("/Atmosphere/Contents/010000000001ABCD/Cheats/0D784A915F5B4AC2.TXT.OFF"));
    assert(!Writable(cheat_root + "0d784a915f5b4ac2.txt"));
    assert(!Writable(cheat_root + "0d784a915f5b4ac2.txt.off"));
    for (const auto name : {"0d784a915f5b4ac.txt", "0d784a915f5b4ac20.txt", "0d784a915f5b4acz.txt", "0d784a915f5b4ac2.txt.exe",
             "0d784a915f5b4ac2.txt.off.off", "0d784a915f5b4ac2.off", "0d784a915f5b4ac2.txt/extra", "../0d784a915f5b4ac2.txt"})
        assert(!IsCheatFile(cheat_root + name));
    assert(!IsCheatFile("/atmosphere/contents/010000000001000/cheats/0d784a915f5b4ac2.txt"));
    assert(!IsCheatFile("/atmosphere/contents/01000000000100000/cheats/0d784a915f5b4ac2.txt"));
    assert(!IsCheatFile("/atmosphere/contents/010000000001000z/cheats/0d784a915f5b4ac2.txt"));
    assert(!IsCheatFile("/atmosphere/contents/0100000000010000/exefs/0d784a915f5b4ac2.txt"));
    assert(!IsCheatFile("/atmosphere/contents/0100000000010000/cheats/../0d784a915f5b4ac2.txt"));
    assert(IsCheatFile("/atmosphere/contents/0100000000010000/cheats/0d784a915f5b4ac2.cheat"));
    assert(IsCheatFile("/atmosphere/contents/0100000000010000/cheats/0d784a915f5b4ac2.cheat.off"));
    assert(IsCheatDirectory("/atmosphere/contents/0100000000010000"));
    assert(IsCheatDirectory("/atmosphere/contents/0100000000010000/cheats"));
    assert(!IsCheatFile("/atmosphere/package3"));
    assert(!IsCheatFile("/atmosphere/contents/010000000001000/cheats/0d784a915f5b4ac2.cheat"));
    assert(!IsCheatFile("/atmosphere/contents/zzzzzzzzzzzzzzzz/cheats/0d784a915f5b4ac2.cheat"));
    assert(!IsCheatFile("/atmosphere/contents/0100000000010000/exefs/code.ips"));
    assert(!IsCheatDirectory("/atmosphere/contents/0100000000010000/exefs"));
    assert(!Writable("/atmosphere/contents/0100000000010000/cheats/0d784a915f5b4ac2.cheat"));
    const auto packet = Encode({Kind::Request, 123}); assert(packet[0] == 0x53 && packet[1] == 0x44 && packet[8] == 123);
    Header header{}; assert(Decode(packet, header) && header.size == 123 && header.kind == Kind::Request);
    auto invalid = packet; invalid[4] = 2; assert(!Decode(invalid, header));
    auto cancel = Encode({Kind::Cancel, 0}); assert(Decode(cancel, header) && header.kind == Kind::Cancel && header.size == 0);
    auto too_large = Encode({Kind::Binary, 5ULL << 30}); assert(!Decode(too_large, header));
    std::mt19937_64 rng(873);
    for (int i = 0; i < 100000; ++i) { const auto size = rng() % (MaxFrame + 1); auto encoded = Encode({Kind::Binary, size}); assert(Decode(encoded, header) && header.size == size); }
    std::cout << "Native wire/path checks passed (100000 deterministic roundtrips)\n";
}
