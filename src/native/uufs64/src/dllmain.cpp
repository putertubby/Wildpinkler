#include "uufs64.h"

#include <windows.h>

#include <memory>

#include "wildpinkler/vfs_core.h"

namespace {

std::unique_ptr<wildpinkler::vfs::Engine> g_engine;

} // namespace

extern "C" int32_t uufs64_install_hooks(void) {
    // TODO: install CreateProcess/file API hooks that route through g_engine.
    if (!g_engine) {
        g_engine = std::make_unique<wildpinkler::vfs::Engine>();
    }
    return 0;
}

extern "C" int32_t uufs64_remove_hooks(void) {
    // TODO: uninstall hooks.
    g_engine.reset();
    return 0;
}

BOOL APIENTRY DllMain(HMODULE module, DWORD reason, LPVOID /*reserved*/) {
    switch (reason) {
    case DLL_PROCESS_ATTACH:
        DisableThreadLibraryCalls(module);
        break;
    case DLL_PROCESS_DETACH:
        g_engine.reset();
        break;
    default:
        break;
    }
    return TRUE;
}
