#pragma once

#include <cstdint>

#ifdef __cplusplus
extern "C" {
#endif

/// Installs the VFS hooks in the current process. Returns 0 on success.
__declspec(dllexport) int32_t uufs64_install_hooks(void);

/// Removes previously installed hooks. Returns 0 on success.
__declspec(dllexport) int32_t uufs64_remove_hooks(void);

#ifdef __cplusplus
}
#endif
