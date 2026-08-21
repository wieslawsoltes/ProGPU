#pragma once

#include "progpu_native.h"

#ifdef __cplusplus
extern "C" {
#endif

enum {
    PROGPU_NATIVE_DAWN_ADAPTER_ABI_VERSION = 1U,
    PROGPU_NATIVE_DAWN_REQUIRED_PROVIDER_ABI_VERSION = 2U
};

/*
 * The host callback adapts its provider-specific resolver to this neutral
 * signature. It is invoked only while an engine is created. The provider,
 * instance, device, queue, and module containing the resolved procedures must
 * remain alive until the engine is destroyed.
 */
typedef void* (*progpu_native_dawn_resolve_proc)(
    void* context,
    const char* name);

typedef struct progpu_native_dawn_engine_options {
    uint32_t struct_size;
    uint32_t native_abi_version;
    uint32_t adapter_abi_version;
    uint32_t provider_abi_version;
    uint32_t target_format;
    uint32_t reserved;
    void* resolver_context;
    progpu_native_dawn_resolve_proc resolve_proc;
    uintptr_t instance;
    uintptr_t device;
    uintptr_t queue;
    uint64_t flags;
} progpu_native_dawn_engine_options;

PROGPU_NATIVE_API uint32_t progpu_native_dawn_get_adapter_abi_version(void);
PROGPU_NATIVE_API progpu_native_status progpu_native_dawn_engine_create(
    const progpu_native_dawn_engine_options* options,
    progpu_native_engine** engine);
PROGPU_NATIVE_API progpu_native_status progpu_native_dawn_engine_recreate(
    const progpu_native_engine* source,
    const progpu_native_dawn_engine_options* options,
    progpu_native_engine** replacement);

#ifdef __cplusplus
}
#endif
