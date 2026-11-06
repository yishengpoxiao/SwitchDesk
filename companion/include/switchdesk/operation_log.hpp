#pragma once
#include <cerrno>
#include <cstdio>
#include <memory>
#include <string>
#include <string_view>
#include <unistd.h>

namespace switchdesk {
// On the SD filesystem a rename to an existing destination can fail. Publish each
// transition under a fresh name, keeping the preceding durable record intact.
inline auto PublishOperationRecord(const std::string& base, std::string_view phase, std::string_view json) -> bool {
    const char* suffix;
    if (phase == "staging" || phase == "installing") suffix = ".json";
    else if (phase == "writing") suffix = ".writing.json";
    else if (phase == "complete" || phase == "verify-required" || phase == "needs-inspection") suffix = ".final.json";
    else return false;
    if (json.empty()) return false;
    const auto path = base + suffix;
    const auto temporary = path + ".new";
    std::unique_ptr<FILE, decltype(&std::fclose)> file(std::fopen(temporary.c_str(), "wb"), &std::fclose);
    if (!file || std::fwrite(json.data(), 1, json.size(), file.get()) != json.size() ||
        std::fflush(file.get()) || fsync(fileno(file.get()))) return false;
    if (std::fclose(file.release())) return false;
    return std::rename(temporary.c_str(), path.c_str()) == 0;
}

inline auto OpenOperationRecord(const std::string& base) -> std::unique_ptr<FILE, decltype(&std::fclose)> {
    for (const auto suffix : {".final.json", ".writing.json", ".json"}) {
        auto file = std::unique_ptr<FILE, decltype(&std::fclose)>(std::fopen((base + suffix).c_str(), "rb"), &std::fclose);
        if (file || errno != ENOENT) return file;
    }
    return {nullptr, &std::fclose};
}
}
