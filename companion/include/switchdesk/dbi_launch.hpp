#pragma once
#include <array>
#include <cstdint>

namespace switchdesk {
inline constexpr std::array DbiPaths{"/switch/DBI/DBI.nro", "/switch/DBI.nro"};

// hbloader reads the image identified by header.size and maps its three
// segments. The optional icon, NACP and RomFS may follow that image.
template<class Header>
constexpr bool ValidDbiImage(const Header& header, std::uint64_t file_size) {
    if (header.magic != 0x304f524e || header.size < 0x80 || header.size > file_size ||
        header.segments[0].file_off != 0 || header.segments[0].size < 0x80) return false;
    std::uint64_t previous_end{};
    for (const auto& segment : header.segments) {
        if (segment.file_off < previous_end || segment.file_off >= header.size ||
            segment.size > header.size - segment.file_off || (segment.file_off & 0xfff) != 0) return false;
        previous_end = static_cast<std::uint64_t>(segment.file_off) + segment.size;
    }
    return true;
}
}
