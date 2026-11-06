// SwitchDesk installation signature policy. GPL-3.0.
#pragma once
#include "switchdesk/install_hash_policy.hpp"
#include <array>

namespace switchdesk {
enum class NcaSignaturePolicy { Strict, Warn };
enum class NcaSignatureDecision { Valid, Warning, Reject };
inline constexpr std::uint32_t NcaHeaderSignatureMismatchResult = 0x81F9;
inline constexpr std::size_t NcaSignedHeaderSize = 0x200;

inline auto ParseNcaSignaturePolicy(std::optional<std::string_view> text = {}) -> std::optional<NcaSignaturePolicy> {
    if (!text || *text == "strict") return NcaSignaturePolicy::Strict;
    if (*text == "warn") return NcaSignaturePolicy::Warn;
    return std::nullopt;
}

inline auto EvaluateNcaSignature(std::uint32_t result, NcaSignaturePolicy policy) -> NcaSignatureDecision {
    if (result == 0) return NcaSignatureDecision::Valid;
    if (result == NcaHeaderSignatureMismatchResult && policy == NcaSignaturePolicy::Warn)
        return NcaSignatureDecision::Warning;
    return NcaSignatureDecision::Reject;
}

struct NcaSignatureWarning {
    std::string entry, header_sha256;
    std::uint32_t verification_result{};
};

// Verification may retry a converted header. Give it a copy so neither the
// install header nor the bytes identified by the warning can change.
template <typename Header, typename Verify, typename Digest, typename Warning>
auto VerifyNcaHeaderSignature(const Header& original_header,
    std::span<const std::uint8_t, NcaSignedHeaderSize> signed_header, NcaSignaturePolicy policy,
    std::string_view entry, Verify&& verify, Digest&& digest, Warning&& warning) -> std::uint32_t {
    auto verification_header = original_header;
    const auto result = static_cast<std::uint32_t>(verify(verification_header));
    if (EvaluateNcaSignature(result, policy) != NcaSignatureDecision::Warning) return result;
    const auto header_sha256 = digest(signed_header);
    return warning(NcaSignatureWarning{std::string(entry), HashHex(header_sha256), result});
}
}
