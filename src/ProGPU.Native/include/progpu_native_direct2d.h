#pragma once

#include <stdint.h>

#if !defined(_WIN32)
#  error "progpu_native_direct2d.h is a Windows-only native interop contract"
#endif

#if defined(PROGPU_NATIVE_DIRECT2D_BUILD)
#  define PROGPU_NATIVE_DIRECT2D_API __declspec(dllexport)
#else
#  define PROGPU_NATIVE_DIRECT2D_API __declspec(dllimport)
#endif

#ifdef __cplusplus
extern "C" {
#endif

typedef struct progpu_native_direct2d_surface
    progpu_native_direct2d_surface;

typedef enum progpu_native_direct2d_status {
    PROGPU_NATIVE_DIRECT2D_STATUS_SUCCESS = 0,
    PROGPU_NATIVE_DIRECT2D_STATUS_INVALID_ARGUMENT = 1,
    PROGPU_NATIVE_DIRECT2D_STATUS_OUT_OF_MEMORY = 2,
    PROGPU_NATIVE_DIRECT2D_STATUS_ADAPTER_NOT_FOUND = 3,
    PROGPU_NATIVE_DIRECT2D_STATUS_DEVICE_CREATION_FAILED = 4,
    PROGPU_NATIVE_DIRECT2D_STATUS_RESOURCE_CREATION_FAILED = 5,
    PROGPU_NATIVE_DIRECT2D_STATUS_SYNCHRONIZATION_FAILED = 6,
    PROGPU_NATIVE_DIRECT2D_STATUS_ACCESS_ALREADY_ACQUIRED = 7,
    PROGPU_NATIVE_DIRECT2D_STATUS_ACCESS_NOT_ACQUIRED = 8,
    PROGPU_NATIVE_DIRECT2D_STATUS_DEVICE_LOST = 9,
    PROGPU_NATIVE_DIRECT2D_STATUS_DRAW_ALREADY_ACTIVE = 10,
    PROGPU_NATIVE_DIRECT2D_STATUS_DRAW_NOT_ACTIVE = 11,
    PROGPU_NATIVE_DIRECT2D_STATUS_DRAW_FAILED = 12,
    PROGPU_NATIVE_DIRECT2D_STATUS_INTERFACE_NOT_SUPPORTED = 13,
    PROGPU_NATIVE_DIRECT2D_STATUS_WIN2D_RUNTIME_UNAVAILABLE = 14,
    PROGPU_NATIVE_DIRECT2D_STATUS_WINDOWS_RUNTIME_NOT_INITIALIZED = 15
} progpu_native_direct2d_status;

typedef enum progpu_native_direct2d_surface_flags {
    PROGPU_NATIVE_DIRECT2D_SURFACE_FLAG_NONE = 0,
    PROGPU_NATIVE_DIRECT2D_SURFACE_FLAG_ENABLE_DEBUG = 1U << 0U,
    PROGPU_NATIVE_DIRECT2D_SURFACE_FLAG_ALLOW_WARP_FALLBACK = 1U << 1U,
    PROGPU_NATIVE_DIRECT2D_SURFACE_FLAG_FORCE_WARP = 1U << 2U
} progpu_native_direct2d_surface_flags;

typedef enum progpu_native_direct2d_descriptor_flags {
    PROGPU_NATIVE_DIRECT2D_DESCRIPTOR_FLAG_NONE = 0,
    PROGPU_NATIVE_DIRECT2D_DESCRIPTOR_FLAG_KEYED_MUTEX = 1U << 0U,
    PROGPU_NATIVE_DIRECT2D_DESCRIPTOR_FLAG_NT_HANDLE = 1U << 1U,
    PROGPU_NATIVE_DIRECT2D_DESCRIPTOR_FLAG_SOFTWARE_ADAPTER = 1U << 2U
} progpu_native_direct2d_descriptor_flags;

/* Every returned pointer is a genuine Windows COM interface with one caller-
 * owned reference. Release it through IUnknown::Release. These pointers are
 * process-local Windows interop state and must never enter ProGPU's portable
 * scene, MIL, WebGPU, or package-neutral ABI. */
typedef enum progpu_native_direct2d_interface_kind {
    PROGPU_NATIVE_DIRECT2D_INTERFACE_D3D11_DEVICE = 1,
    PROGPU_NATIVE_DIRECT2D_INTERFACE_D3D11_DEVICE_CONTEXT = 2,
    PROGPU_NATIVE_DIRECT2D_INTERFACE_DXGI_ADAPTER1 = 3,
    PROGPU_NATIVE_DIRECT2D_INTERFACE_DXGI_DEVICE = 4,
    PROGPU_NATIVE_DIRECT2D_INTERFACE_DXGI_SURFACE = 5,
    PROGPU_NATIVE_DIRECT2D_INTERFACE_DXGI_KEYED_MUTEX = 6,
    PROGPU_NATIVE_DIRECT2D_INTERFACE_D3D11_TEXTURE2D = 7,
    PROGPU_NATIVE_DIRECT2D_INTERFACE_D2D1_FACTORY1 = 8,
    PROGPU_NATIVE_DIRECT2D_INTERFACE_D2D1_FACTORY2 = 9,
    PROGPU_NATIVE_DIRECT2D_INTERFACE_D2D1_DEVICE = 10,
    PROGPU_NATIVE_DIRECT2D_INTERFACE_D2D1_DEVICE1 = 11,
    PROGPU_NATIVE_DIRECT2D_INTERFACE_D2D1_DEVICE_CONTEXT = 12,
    PROGPU_NATIVE_DIRECT2D_INTERFACE_D2D1_DEVICE_CONTEXT1 = 13,
    PROGPU_NATIVE_DIRECT2D_INTERFACE_D2D1_BITMAP = 14,
    PROGPU_NATIVE_DIRECT2D_INTERFACE_D2D1_BITMAP1 = 15,
    PROGPU_NATIVE_DIRECT2D_INTERFACE_WINRT_DIRECT3D11_DEVICE = 16,
    PROGPU_NATIVE_DIRECT2D_INTERFACE_WIN2D_CANVAS_DEVICE = 17,
    PROGPU_NATIVE_DIRECT2D_INTERFACE_WIN2D_CANVAS_RENDER_TARGET = 18
} progpu_native_direct2d_interface_kind;

typedef struct progpu_native_direct2d_surface_options {
    uint32_t struct_size;
    uint32_t flags;
    uint32_t width;
    uint32_t height;
    float dpi_x;
    float dpi_y;
    uint32_t adapter_luid_low;
    int32_t adapter_luid_high;
} progpu_native_direct2d_surface_options;

typedef struct progpu_native_direct2d_surface_descriptor {
    uint32_t struct_size;
    uint32_t flags;
    uint32_t width;
    uint32_t height;
    float dpi_x;
    float dpi_y;
    uint32_t dxgi_format;
    uint32_t alpha_mode;
    uint32_t adapter_luid_low;
    int32_t adapter_luid_high;
    uintptr_t shared_nt_handle;
    uint64_t initial_acquire_key;
    uint64_t initial_release_key;
    uint64_t content_version;
} progpu_native_direct2d_surface_descriptor;

/* Binary-compatible Windows GUID layout. Keeping this definition in the C ABI
 * lets AOT callers request later Direct2D interface generations without COM
 * reflection or a new enum/ABI revision for every Windows SDK interface. */
typedef struct progpu_native_direct2d_guid {
    uint32_t data1;
    uint16_t data2;
    uint16_t data3;
    uint8_t data4[8];
} progpu_native_direct2d_guid;

enum {
    PROGPU_NATIVE_DIRECT2D_ABI_VERSION = 5U
};

PROGPU_NATIVE_DIRECT2D_API uint32_t
progpu_native_direct2d_get_abi_version(void);

PROGPU_NATIVE_DIRECT2D_API progpu_native_direct2d_status
progpu_native_direct2d_surface_create(
    const progpu_native_direct2d_surface_options* options,
    progpu_native_direct2d_surface** surface,
    int32_t* native_hresult);

PROGPU_NATIVE_DIRECT2D_API void
progpu_native_direct2d_surface_destroy(
    progpu_native_direct2d_surface* surface);

PROGPU_NATIVE_DIRECT2D_API progpu_native_direct2d_status
progpu_native_direct2d_surface_get_descriptor(
    const progpu_native_direct2d_surface* surface,
    progpu_native_direct2d_surface_descriptor* descriptor);

PROGPU_NATIVE_DIRECT2D_API progpu_native_direct2d_status
progpu_native_direct2d_surface_get_interface(
    progpu_native_direct2d_surface* surface,
    progpu_native_direct2d_interface_kind kind,
    void** value);

PROGPU_NATIVE_DIRECT2D_API uint32_t
progpu_native_direct2d_com_release(void* value);

/* Queries any caller-owned COM reference for a concrete Windows interface.
 * A successful result owns one reference and must be released through
 * progpu_native_direct2d_com_release. */
PROGPU_NATIVE_DIRECT2D_API progpu_native_direct2d_status
progpu_native_direct2d_com_query_interface(
    void* value,
    const progpu_native_direct2d_guid* interface_id,
    void** result,
    int32_t* native_hresult);

/* Uses the registered CanvasDevice activation factory's official
 * ICanvasFactoryNative contract to wrap this surface's exact ID2D1Device1.
 * The returned pointer is a genuine Win2D CanvasDevice with one caller-owned
 * reference. This function does not initialize the caller's Windows Runtime
 * apartment and does not load or impersonate Microsoft.Graphics.Canvas.dll. */
PROGPU_NATIVE_DIRECT2D_API progpu_native_direct2d_status
progpu_native_direct2d_surface_try_get_win2d_canvas_device(
    progpu_native_direct2d_surface* surface,
    void** value,
    int32_t* native_hresult);

/* Wraps the exact target ID2D1Bitmap1 as a genuine Win2D CanvasRenderTarget in
 * the CanvasDevice resource domain returned above. */
PROGPU_NATIVE_DIRECT2D_API progpu_native_direct2d_status
progpu_native_direct2d_surface_try_get_win2d_canvas_render_target(
    progpu_native_direct2d_surface* surface,
    void** value,
    int32_t* native_hresult);

PROGPU_NATIVE_DIRECT2D_API progpu_native_direct2d_status
progpu_native_direct2d_surface_acquire(
    progpu_native_direct2d_surface* surface,
    uint64_t acquire_key,
    uint32_t timeout_milliseconds);

PROGPU_NATIVE_DIRECT2D_API progpu_native_direct2d_status
progpu_native_direct2d_surface_release(
    progpu_native_direct2d_surface* surface,
    uint64_t release_key);

/* Transactional Direct2D producer scope. begin_draw acquires the keyed mutex
 * before ID2D1DeviceContext::BeginDraw. end_draw always calls EndDraw and then
 * releases the keyed mutex; content_version advances only if both operations
 * succeed. Direct2D commands are issued through the genuine device-context
 * interface returned by surface_get_interface while the scope is active. */
PROGPU_NATIVE_DIRECT2D_API progpu_native_direct2d_status
progpu_native_direct2d_surface_begin_draw(
    progpu_native_direct2d_surface* surface,
    uint64_t acquire_key,
    uint32_t timeout_milliseconds);

PROGPU_NATIVE_DIRECT2D_API progpu_native_direct2d_status
progpu_native_direct2d_surface_end_draw(
    progpu_native_direct2d_surface* surface,
    uint64_t release_key,
    uint64_t* tag1,
    uint64_t* tag2,
    int32_t* native_hresult);

PROGPU_NATIVE_DIRECT2D_API int32_t
progpu_native_direct2d_surface_get_last_hresult(
    const progpu_native_direct2d_surface* surface);

#ifdef __cplusplus
}
#endif
