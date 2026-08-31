#include "wildpinkler/vfs_core.h"

namespace wildpinkler::vfs {

Engine::Engine() = default;
Engine::~Engine() = default;

void Engine::PushLayer(std::wstring_view rootPath) {
    layers_.emplace_back(rootPath);
}

std::wstring Engine::Resolve(std::wstring_view /*virtualPath*/) const {
    // TODO: implement union/merge resolution over layers_.
    return {};
}

const char* VersionString() noexcept {
    return "0.1.0";
}

} // namespace wildpinkler::vfs
