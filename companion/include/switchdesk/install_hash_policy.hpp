// SwitchDesk installation integrity policy. GPL-3.0.
#pragma once
#include <algorithm>
#include <cstdint>
#include <optional>
#include <span>
#include <string>
#include <string_view>

namespace switchdesk {
enum class NcaHashPolicy { Strict, Warn };
enum class NcaHashDecision { Match, Warning, Reject };

inline auto ParseNcaHashPolicy(std::optional<std::string_view> text = {}) -> std::optional<NcaHashPolicy> {
    if (!text || *text == "strict") return NcaHashPolicy::Strict;
    if (*text == "warn") return NcaHashPolicy::Warn;
    return std::nullopt;
}

inline auto EvaluateNcaHash(std::span<const std::uint8_t, 16> expected_content_id,
    std::span<const std::uint8_t, 32> actual_sha256, NcaHashPolicy policy) -> NcaHashDecision {
    if (std::equal(expected_content_id.begin(), expected_content_id.end(), actual_sha256.begin()))
        return NcaHashDecision::Match;
    return policy == NcaHashPolicy::Warn ? NcaHashDecision::Warning : NcaHashDecision::Reject;
}

struct NcaHashWarning {
    std::string entry, expected_content_id, actual_sha256;
};

inline auto HashHex(std::span<const std::uint8_t> bytes) -> std::string {
    constexpr char alphabet[] = "0123456789ABCDEF";
    std::string text(bytes.size() * 2, '0');
    for (std::size_t i = 0; i < bytes.size(); ++i) {
        text[i * 2] = alphabet[bytes[i] >> 4];
        text[i * 2 + 1] = alphabet[bytes[i] & 15];
    }
    return text;
}

inline auto MakeNcaHashWarning(std::string_view entry, std::span<const std::uint8_t, 16> expected_content_id,
    std::span<const std::uint8_t, 32> actual_sha256) -> NcaHashWarning {
    return {std::string(entry), HashHex(expected_content_id), HashHex(actual_sha256)};
}
}
