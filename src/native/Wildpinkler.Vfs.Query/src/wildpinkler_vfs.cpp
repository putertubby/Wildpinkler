#include "wildpinkler_vfs.h"

#include <exception>
#include <new>
#include <string>

#include "wildpinkler/vfs_core.h"

namespace {

thread_local std::string t_lastError;

void SetLastError(const char* message) {
    t_lastError = message ? message : "";
}

} // namespace

extern "C" const char* WP_VFS_CALL wp_vfs_get_version(void) {
    return wildpinkler::vfs::VersionString();
}

extern "C" wp_vfs_status WP_VFS_CALL wp_vfs_create(wp_vfs_handle* out_handle) {
    if (out_handle == nullptr) {
        SetLastError("out_handle must not be null");
        return WP_VFS_E_INVALID_ARG;
    }

    *out_handle = nullptr;
    try {
        auto* engine = new wildpinkler::vfs::Engine();
        *out_handle = reinterpret_cast<wp_vfs_handle>(engine);
        SetLastError("");
        return WP_VFS_OK;
    } catch (const std::exception& ex) {
        SetLastError(ex.what());
        return WP_VFS_E_UNEXPECTED;
    } catch (...) {
        SetLastError("unknown failure creating VFS engine");
        return WP_VFS_E_UNEXPECTED;
    }
}

extern "C" void WP_VFS_CALL wp_vfs_destroy(wp_vfs_handle handle) {
    delete reinterpret_cast<wildpinkler::vfs::Engine*>(handle);
}

extern "C" const char* WP_VFS_CALL wp_vfs_last_error(void) {
    return t_lastError.c_str();
}
