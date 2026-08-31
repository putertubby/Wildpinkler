#pragma once

#include <string>
#include <string_view>
#include <vector>

namespace wildpinkler::vfs {

inline constexpr int kVersionMajor = 0;
inline constexpr int kVersionMinor = 1;
inline constexpr int kVersionPatch = 0;

/// Union/merge semantics live here only; the hook shim and the query DLL both delegate to this.
class Engine {
public:
    Engine();
    ~Engine();

    Engine(const Engine&) = delete;
    Engine& operator=(const Engine&) = delete;

    /// Appends a mod layer; later layers override earlier ones.
    void PushLayer(std::wstring_view rootPath);

    /// Returns the real path a virtual path currently resolves to, or empty when unmapped.
    [[nodiscard]] std::wstring Resolve(std::wstring_view virtualPath) const;

private:
    std::vector<std::wstring> layers_;
};

[[nodiscard]] const char* VersionString() noexcept;

} // namespace wildpinkler::vfs
