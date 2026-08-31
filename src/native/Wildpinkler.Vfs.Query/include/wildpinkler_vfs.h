#pragma once

#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

#ifdef WP_VFS_EXPORTS
#define WP_VFS_API __declspec(dllexport)
#else
#define WP_VFS_API __declspec(dllimport)
#endif

#define WP_VFS_CALL __cdecl

typedef struct wp_vfs_engine_s* wp_vfs_handle;
typedef int32_t wp_vfs_status;

#define WP_VFS_OK 0
#define WP_VFS_E_INVALID_ARG (-1)
#define WP_VFS_E_UNEXPECTED (-2)

/// UTF-8, static storage, never null.
WP_VFS_API const char* WP_VFS_CALL wp_vfs_get_version(void);

WP_VFS_API wp_vfs_status WP_VFS_CALL wp_vfs_create(wp_vfs_handle* out_handle);

WP_VFS_API void WP_VFS_CALL wp_vfs_destroy(wp_vfs_handle handle);

/// UTF-8 description of the last failure on the calling thread, or an empty string.
WP_VFS_API const char* WP_VFS_CALL wp_vfs_last_error(void);

#ifdef __cplusplus
}
#endif
