#pragma once

#include <stddef.h>
#include <stdint.h>

#if defined(_WIN32)
#  if defined(WEBSCENE_GPU_PROVIDER_BUILD)
#    define WEBSCENE_GPU_API __declspec(dllexport)
#  else
#    define WEBSCENE_GPU_API __declspec(dllimport)
#  endif
#else
#  define WEBSCENE_GPU_API __attribute__((visibility("default")))
#endif

#ifdef __cplusplus
extern "C" {
#endif

typedef struct webscene_gpu_provider webscene_gpu_provider;
typedef struct webscene_gpu_canvas webscene_gpu_canvas;

enum {
    WEBSCENE_GPU_PROVIDER_ABI_VERSION = 2U,
    WEBSCENE_GPU_CAPABILITY_WEBGL1 = 1ULL << 13U,
    WEBSCENE_GPU_CAPABILITY_WEBGPU = 1ULL << 14U,
    WEBSCENE_GPU_CAPABILITY_WEBGL2 = 1ULL << 15U
};

enum {
    WEBSCENE_GPU_PROVIDER_FLAG_ZERO_COPY = 1ULL << 0U,
    WEBSCENE_GPU_PROVIDER_FLAG_NO_SOFTWARE_FALLBACK = 1ULL << 1U,
    WEBSCENE_GPU_PROVIDER_FLAG_PRESENT_WAITS_FOR_GPU = 1ULL << 2U
};

typedef enum webscene_gpu_status {
    WEBSCENE_GPU_STATUS_SUCCESS = 0,
    WEBSCENE_GPU_STATUS_UNAVAILABLE = 1,
    WEBSCENE_GPU_STATUS_INVALID_ARGUMENT = 2,
    WEBSCENE_GPU_STATUS_BUSY = 3,
    WEBSCENE_GPU_STATUS_DEVICE_LOST = 4,
    WEBSCENE_GPU_STATUS_INTERNAL_ERROR = 5
} webscene_gpu_status;

typedef enum webscene_gpu_handle_kind {
    WEBSCENE_GPU_HANDLE_NONE = 0,
    WEBSCENE_GPU_HANDLE_IOSURFACE = 1,
    WEBSCENE_GPU_HANDLE_METAL_TEXTURE = 2,
    WEBSCENE_GPU_HANDLE_D3D11_TEXTURE = 3,
    WEBSCENE_GPU_HANDLE_DMABUF = 4
} webscene_gpu_handle_kind;

typedef enum webscene_gpu_pixel_format {
    WEBSCENE_GPU_PIXEL_FORMAT_BGRA8_UNORM = 1,
    WEBSCENE_GPU_PIXEL_FORMAT_BGRA8_UNORM_SRGB = 2,
    WEBSCENE_GPU_PIXEL_FORMAT_RGBA8_UNORM = 3,
    WEBSCENE_GPU_PIXEL_FORMAT_RGBA8_UNORM_SRGB = 4
} webscene_gpu_pixel_format;

typedef enum webscene_gpu_alpha_mode {
    WEBSCENE_GPU_ALPHA_MODE_OPAQUE = 1,
    WEBSCENE_GPU_ALPHA_MODE_PREMULTIPLIED = 2
} webscene_gpu_alpha_mode;

enum {
    WEBSCENE_GPU_EXTERNAL_TEXTURE_PREMULTIPLIED_ALPHA = 1U << 0U,
    WEBSCENE_GPU_EXTERNAL_TEXTURE_GPU_COMPLETE = 1U << 1U
};

typedef struct webscene_gpu_provider_info {
    uint32_t struct_size;
    uint32_t abi_version;
    uint64_t capabilities;
    uint64_t flags;
    char name[64];
    char adapter[128];
} webscene_gpu_provider_info;

typedef struct webscene_gpu_provider_options {
    uint32_t struct_size;
    uint32_t flags;
    uint64_t required_capabilities;
} webscene_gpu_provider_options;

/*
 * The device is a retained WebGPU device created from this provider's instance.
 * A canvas configuration retains it until the canvas is destroyed or
 * reconfigured. Usage is the WebGPU texture-usage bit mask.
 */
typedef struct webscene_gpu_canvas_configuration {
    uint32_t struct_size;
    uint32_t flags;
    uintptr_t device;
    uint64_t usage;
    uint32_t pixel_format;
    uint32_t alpha_mode;
    uint32_t buffer_count;
    uint32_t reserved;
} webscene_gpu_canvas_configuration;

/*
 * A provider owns every handle in this record. The consumer may retain it only
 * through webscene_gpu_provider_retain_external_texture and must release that
 * retain after the compositor has submitted its read. IOSurface is the first
 * zero-copy contract; providers must not advertise a capability if they would
 * have to read pixels back to the CPU to satisfy this interface.
 */
typedef struct webscene_gpu_external_texture {
    uint32_t struct_size;
    uint32_t handle_kind;
    uint32_t pixel_format;
    uint32_t flags;
    uint32_t width;
    uint32_t height;
    uint64_t generation;
    uintptr_t shared_handle;
    uintptr_t texture_handle;
    uintptr_t synchronization_handle;
    uint64_t ready_value;
} webscene_gpu_external_texture;

WEBSCENE_GPU_API uint32_t webscene_gpu_provider_get_abi_version(void);
WEBSCENE_GPU_API uint8_t webscene_gpu_provider_get_info(
    webscene_gpu_provider_info* info);
WEBSCENE_GPU_API webscene_gpu_provider* webscene_gpu_provider_create(
    const webscene_gpu_provider_options* options);
WEBSCENE_GPU_API void webscene_gpu_provider_destroy(
    webscene_gpu_provider* provider);

/* Dawn's proc table and instance are deliberately opaque to the core loader. */
WEBSCENE_GPU_API void* webscene_gpu_provider_get_wgpu_instance(
    webscene_gpu_provider* provider);
WEBSCENE_GPU_API void* webscene_gpu_provider_get_wgpu_proc_address(
    webscene_gpu_provider* provider,
    const char* name);

WEBSCENE_GPU_API webscene_gpu_canvas* webscene_gpu_provider_create_canvas(
    webscene_gpu_provider* provider,
    const webscene_gpu_canvas_configuration* configuration,
    uint32_t width,
    uint32_t height);
WEBSCENE_GPU_API webscene_gpu_status webscene_gpu_provider_resize_canvas(
    webscene_gpu_provider* provider,
    webscene_gpu_canvas* canvas,
    uint32_t width,
    uint32_t height);
/* The returned WGPUTexture has one reference owned by the caller. */
WEBSCENE_GPU_API webscene_gpu_status webscene_gpu_provider_acquire_canvas_texture(
    webscene_gpu_provider* provider,
    webscene_gpu_canvas* canvas,
    uintptr_t* texture);
WEBSCENE_GPU_API webscene_gpu_status webscene_gpu_provider_present_canvas(
    webscene_gpu_provider* provider,
    webscene_gpu_canvas* canvas,
    webscene_gpu_external_texture* texture);
WEBSCENE_GPU_API void webscene_gpu_provider_destroy_canvas(
    webscene_gpu_provider* provider,
    webscene_gpu_canvas* canvas);
WEBSCENE_GPU_API webscene_gpu_status webscene_gpu_provider_retain_external_texture(
    webscene_gpu_provider* provider,
    const webscene_gpu_external_texture* texture);
WEBSCENE_GPU_API void webscene_gpu_provider_release_external_texture(
    webscene_gpu_provider* provider,
    const webscene_gpu_external_texture* texture);

#ifdef __cplusplus
}
#endif
