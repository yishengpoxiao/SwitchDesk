#pragma once
#include <cerrno>
#include <cstdio>
#include <memory>
#include <string>
#include <string_view>
#include <unistd.h>

namespace switchdesk {
struct OperationFileCloser {
    void operator()(FILE* file) const noexcept { if (file) std::fclose(file); }
};
using OperationFile = std::unique_ptr<FILE, OperationFileCloser>;

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
    OperationFile file(std::fopen(temporary.c_str(), "wb"));
    if (!file || std::fwrite(json.data(), 1, json.size(), file.get()) != json.size() ||
        std::fflush(file.get()) || fsync(fileno(file.get()))) return false;
    if (std::fclose(file.release())) return false;
    return std::rename(temporary.c_str(), path.c_str()) == 0;
}

inline auto OpenOperationRecord(const std::string& base) -> OperationFile {
    for (const auto suffix : {".final.json", ".writing.json", ".json"}) {
        OperationFile file(std::fopen((base + suffix).c_str(), "rb"));
        if (file || errno != ENOENT) return file;
    }
    return {};
}
}
