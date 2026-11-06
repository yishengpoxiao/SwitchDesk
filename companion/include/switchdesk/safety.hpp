#pragma once
#include <algorithm>
#include <array>
#include <cctype>
#include <string>
#include <string_view>

namespace switchdesk {
inline auto SameDigest(std::string_view left, std::string_view right) -> bool {
    return left.size() == 64 && right.size() == 64 && std::equal(left.begin(), left.end(), right.begin(), [](unsigned char a, unsigned char b) {
        return std::isxdigit(a) && std::isxdigit(b) && std::toupper(a) == std::toupper(b);
    });
}
inline auto ValidRelative(std::string_view path) -> bool {
    if (path.empty() || path.front() == '/' || path.size() > 700 || path.find_first_of("\\:") != path.npos || path.find('\0') != path.npos) return false;
    for (std::size_t start = 0; start <= path.size();) {
        auto end = path.find('/', start); if (end == path.npos) end = path.size();
        const auto part = path.substr(start, end - start);
        if (part.empty() || part == "." || part == "..") return false;
        if (end == path.size()) return true;
        start = end + 1;
    }
    return false;
}
inline auto ValidRemote(std::string_view path) -> bool { return path == "/" || (path.starts_with('/') && ValidRelative(path.substr(1))); }
inline auto EqualFold(std::string_view value, std::string_view expected) -> bool {
    return value.size() == expected.size() && std::equal(value.begin(), value.end(), expected.begin(), [](unsigned char a, unsigned char b) {
        return std::tolower(a) == std::tolower(b);
    });
}
inline auto HexId16(std::string_view value) -> bool {
    return value.size() == 16 && std::all_of(value.begin(), value.end(), [](unsigned char c) { return std::isxdigit(c) != 0; });
}
struct RemoteParts { std::array<std::string_view, 8> value{}; std::size_t count{}; };
inline auto SplitRemote(std::string_view path) -> RemoteParts {
    RemoteParts parts{};
    if (path.size() < 2 || path.front() != '/') return parts;
    std::size_t start = 1;
    while (start <= path.size() && parts.count < parts.value.size()) {
        auto end = path.find('/', start); if (end == path.npos) end = path.size();
        parts.value[parts.count++] = path.substr(start, end - start);
        if (end == path.size()) break;
        start = end + 1;
    }
    return parts;
}
/// Only standard Atmosphere .txt and legacy .cheat paths with exact IDs are allowed.
inline auto IsCheatFile(std::string_view path) -> bool {
    const auto parts = SplitRemote(path);
    if (parts.count != 5 || !EqualFold(parts.value[0], "atmosphere") || !EqualFold(parts.value[1], "contents") ||
        !HexId16(parts.value[2]) || !EqualFold(parts.value[3], "cheats")) return false;
    auto name = parts.value[4];
    if (name.size() >= 4 && EqualFold(name.substr(name.size() - 4), ".off")) name.remove_suffix(4);
    return ((name.size() == 20 && EqualFold(name.substr(16), ".txt")) ||
            (name.size() == 22 && EqualFold(name.substr(16), ".cheat"))) && HexId16(name.substr(0, 16));
}
/// Directory shapes cheats need for creation: /atmosphere/contents/<title hex>[/cheats].
inline auto IsCheatDirectory(std::string_view path) -> bool {
    const auto parts = SplitRemote(path);
    if (parts.count < 3 || !EqualFold(parts.value[0], "atmosphere") || !EqualFold(parts.value[1], "contents") || !HexId16(parts.value[2])) return false;
    return parts.count == 3 || (parts.count == 4 && EqualFold(parts.value[3], "cheats"));
}
inline auto Writable(std::string_view path) -> bool {
    if (!ValidRemote(path) || path == "/") return false;
    auto lower = std::string(path); std::transform(lower.begin(), lower.end(), lower.begin(), [](unsigned char c) { return static_cast<char>(std::tolower(c)); });
    for (const auto root : {"/nintendo", "/emummc", "/atmosphere", "/bootloader", "/switch/switchdesk", "/config/switchdesk"})
        if (lower == root || lower.starts_with(std::string(root) + '/')) return false;
    for (const auto file : {"/exosphere.ini", "/hekate_ipl.ini", "/hbmenu.nro", "/boot.dat", "/boot.ini"}) if (lower == file) return false;
    return true;
}
inline auto MovableOrRemovable(std::string_view path) -> bool {
    return Writable(path) && !EqualFold(path, "/switch") && !EqualFold(path, "/config");
}
}
