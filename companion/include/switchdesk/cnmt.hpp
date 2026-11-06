// SwitchDesk userspace CNMT reader. GPL-3.0.
#pragma once
#include "yati/nx/nca.hpp"
#include <functional>
#include <string>

namespace sphaira::nca {
// Parse one plaintext CNMT file, either from userspace decryption or fsp-srv.
// Both paths must enforce identical bounds and reject successful short reads.
Result ParseCnmtContents(u64 size, const std::function<Result(void*, u64, u64, u64*)>& read,
    ncm::PackagedContentMeta& meta, std::vector<u8>& extended,
    std::vector<NcmPackagedContentInfo>& infos, std::string& stage);
// Header has its XTS layer removed but still contains the encrypted key area.
// Parsing does not change it. All reads stay inside this NCA and its PFS0 layer.
// Optional diagnostics contain parser revision and format/offset fields only.
Result ParseCnmtFromSource(yati::source::Base& source, u64 nca_offset, const Header& header,
    const keys::Keys& keys, ncm::PackagedContentMeta& meta, std::vector<u8>& extended,
    std::vector<NcmPackagedContentInfo>& infos, std::string& stage, std::string* diagnostic = nullptr);
}
