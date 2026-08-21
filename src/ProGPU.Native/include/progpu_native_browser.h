#pragma once

#include "progpu_native.h"

#ifdef __cplusplus
extern "C" {
#endif

enum {
    PROGPU_NATIVE_BROWSER_ADAPTER_ABI_VERSION = 1U
};

typedef struct progpu_native_browser_engine_options {
    uint32_t struct_size;
    uint32_t native_abi_version;
    uint32_t adapter_abi_version;
    uint32_t target_format;
    uint32_t reserved0;
    uint32_t reserved1;
    uintptr_t device;
    uintptr_t queue;
    uint64_t flags;
} progpu_native_browser_engine_options;

PROGPU_NATIVE_API uint32_t progpu_native_browser_get_adapter_abi_version(void);
PROGPU_NATIVE_API progpu_native_status progpu_native_browser_engine_create(
    const progpu_native_browser_engine_options* options,
    progpu_native_engine** engine);
PROGPU_NATIVE_API progpu_native_status progpu_native_browser_engine_recreate(
    const progpu_native_engine* source,
    const progpu_native_browser_engine_options* options,
    progpu_native_engine** replacement);

#ifdef __cplusplus
}
#endif
