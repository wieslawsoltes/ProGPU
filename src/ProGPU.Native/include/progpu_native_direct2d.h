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
    PROGPU_NATIVE_DIRECT2D_INTERFACE_WIN2D_CANVAS_RENDER_TARGET = 18,
    PROGPU_NATIVE_DIRECT2D_INTERFACE_D2D1_SOLID_COLOR_BRUSH = 19,
    PROGPU_NATIVE_DIRECT2D_INTERFACE_WIN2D_CANVAS_SOLID_COLOR_BRUSH = 20,
    PROGPU_NATIVE_DIRECT2D_INTERFACE_D2D1_GRADIENT_STOP_COLLECTION1 = 21,
    PROGPU_NATIVE_DIRECT2D_INTERFACE_D2D1_LINEAR_GRADIENT_BRUSH = 22,
    PROGPU_NATIVE_DIRECT2D_INTERFACE_WIN2D_CANVAS_LINEAR_GRADIENT_BRUSH = 23,
    PROGPU_NATIVE_DIRECT2D_INTERFACE_D2D1_RADIAL_GRADIENT_BRUSH = 24,
    PROGPU_NATIVE_DIRECT2D_INTERFACE_WIN2D_CANVAS_RADIAL_GRADIENT_BRUSH = 25,
    PROGPU_NATIVE_DIRECT2D_INTERFACE_D2D1_GEOMETRY = 26,
    PROGPU_NATIVE_DIRECT2D_INTERFACE_D2D1_RECTANGLE_GEOMETRY = 27,
    PROGPU_NATIVE_DIRECT2D_INTERFACE_D2D1_ROUNDED_RECTANGLE_GEOMETRY = 28,
    PROGPU_NATIVE_DIRECT2D_INTERFACE_D2D1_ELLIPSE_GEOMETRY = 29,
    PROGPU_NATIVE_DIRECT2D_INTERFACE_D2D1_PATH_GEOMETRY1 = 30,
    PROGPU_NATIVE_DIRECT2D_INTERFACE_D2D1_TRANSFORMED_GEOMETRY = 31,
    PROGPU_NATIVE_DIRECT2D_INTERFACE_WIN2D_CANVAS_GEOMETRY = 32,
    PROGPU_NATIVE_DIRECT2D_INTERFACE_D2D1_STROKE_STYLE1 = 33,
    PROGPU_NATIVE_DIRECT2D_INTERFACE_WIN2D_CANVAS_STROKE_STYLE = 34
} progpu_native_direct2d_interface_kind;

typedef enum progpu_native_direct2d_fill_mode {
    PROGPU_NATIVE_DIRECT2D_FILL_MODE_ALTERNATE = 0,
    PROGPU_NATIVE_DIRECT2D_FILL_MODE_WINDING = 1
} progpu_native_direct2d_fill_mode;

typedef enum progpu_native_direct2d_path_segment_kind {
    PROGPU_NATIVE_DIRECT2D_PATH_SEGMENT_LINE = 0,
    PROGPU_NATIVE_DIRECT2D_PATH_SEGMENT_QUADRATIC = 1,
    PROGPU_NATIVE_DIRECT2D_PATH_SEGMENT_CUBIC = 2,
    PROGPU_NATIVE_DIRECT2D_PATH_SEGMENT_ARC = 3
} progpu_native_direct2d_path_segment_kind;

typedef enum progpu_native_direct2d_combine_mode {
    PROGPU_NATIVE_DIRECT2D_COMBINE_MODE_UNION = 0,
    PROGPU_NATIVE_DIRECT2D_COMBINE_MODE_INTERSECT = 1,
    PROGPU_NATIVE_DIRECT2D_COMBINE_MODE_XOR = 2,
    PROGPU_NATIVE_DIRECT2D_COMBINE_MODE_EXCLUDE = 3
} progpu_native_direct2d_combine_mode;

typedef enum progpu_native_direct2d_cap_style {
    PROGPU_NATIVE_DIRECT2D_CAP_STYLE_FLAT = 0,
    PROGPU_NATIVE_DIRECT2D_CAP_STYLE_SQUARE = 1,
    PROGPU_NATIVE_DIRECT2D_CAP_STYLE_ROUND = 2,
    PROGPU_NATIVE_DIRECT2D_CAP_STYLE_TRIANGLE = 3
} progpu_native_direct2d_cap_style;

typedef enum progpu_native_direct2d_line_join {
    PROGPU_NATIVE_DIRECT2D_LINE_JOIN_MITER = 0,
    PROGPU_NATIVE_DIRECT2D_LINE_JOIN_BEVEL = 1,
    PROGPU_NATIVE_DIRECT2D_LINE_JOIN_ROUND = 2,
    PROGPU_NATIVE_DIRECT2D_LINE_JOIN_MITER_OR_BEVEL = 3
} progpu_native_direct2d_line_join;

typedef enum progpu_native_direct2d_dash_style {
    PROGPU_NATIVE_DIRECT2D_DASH_STYLE_SOLID = 0,
    PROGPU_NATIVE_DIRECT2D_DASH_STYLE_DASH = 1,
    PROGPU_NATIVE_DIRECT2D_DASH_STYLE_DOT = 2,
    PROGPU_NATIVE_DIRECT2D_DASH_STYLE_DASH_DOT = 3,
    PROGPU_NATIVE_DIRECT2D_DASH_STYLE_DASH_DOT_DOT = 4,
    PROGPU_NATIVE_DIRECT2D_DASH_STYLE_CUSTOM = 5
} progpu_native_direct2d_dash_style;

typedef enum progpu_native_direct2d_stroke_transform_type {
    PROGPU_NATIVE_DIRECT2D_STROKE_TRANSFORM_NORMAL = 0,
    PROGPU_NATIVE_DIRECT2D_STROKE_TRANSFORM_FIXED = 1,
    PROGPU_NATIVE_DIRECT2D_STROKE_TRANSFORM_HAIRLINE = 2
} progpu_native_direct2d_stroke_transform_type;

enum {
    PROGPU_NATIVE_DIRECT2D_PATH_SEGMENT_FLAG_NONE = 0U,
    PROGPU_NATIVE_DIRECT2D_PATH_SEGMENT_FLAG_FORCE_UNSTROKED = 1U << 0U,
    PROGPU_NATIVE_DIRECT2D_PATH_SEGMENT_FLAG_FORCE_ROUND_LINE_JOIN = 1U << 1U,
    PROGPU_NATIVE_DIRECT2D_PATH_FIGURE_FLAG_FILLED = 1U << 0U,
    PROGPU_NATIVE_DIRECT2D_PATH_FIGURE_FLAG_CLOSED = 1U << 1U,
    PROGPU_NATIVE_DIRECT2D_ARC_FLAG_CLOCKWISE = 1U << 0U,
    PROGPU_NATIVE_DIRECT2D_ARC_FLAG_LARGE = 1U << 1U
};

typedef enum progpu_native_direct2d_color_space {
    PROGPU_NATIVE_DIRECT2D_COLOR_SPACE_CUSTOM = 0,
    PROGPU_NATIVE_DIRECT2D_COLOR_SPACE_SRGB = 1,
    PROGPU_NATIVE_DIRECT2D_COLOR_SPACE_SCRGB = 2
} progpu_native_direct2d_color_space;

typedef enum progpu_native_direct2d_buffer_precision {
    PROGPU_NATIVE_DIRECT2D_BUFFER_PRECISION_UNKNOWN = 0,
    PROGPU_NATIVE_DIRECT2D_BUFFER_PRECISION_8BPC_UNORM = 1,
    PROGPU_NATIVE_DIRECT2D_BUFFER_PRECISION_8BPC_UNORM_SRGB = 2,
    PROGPU_NATIVE_DIRECT2D_BUFFER_PRECISION_16BPC_UNORM = 3,
    PROGPU_NATIVE_DIRECT2D_BUFFER_PRECISION_16BPC_FLOAT = 4,
    PROGPU_NATIVE_DIRECT2D_BUFFER_PRECISION_32BPC_FLOAT = 5
} progpu_native_direct2d_buffer_precision;

typedef enum progpu_native_direct2d_extend_mode {
    PROGPU_NATIVE_DIRECT2D_EXTEND_MODE_CLAMP = 0,
    PROGPU_NATIVE_DIRECT2D_EXTEND_MODE_WRAP = 1,
    PROGPU_NATIVE_DIRECT2D_EXTEND_MODE_MIRROR = 2
} progpu_native_direct2d_extend_mode;

typedef enum progpu_native_direct2d_color_interpolation_mode {
    PROGPU_NATIVE_DIRECT2D_COLOR_INTERPOLATION_MODE_STRAIGHT = 0,
    PROGPU_NATIVE_DIRECT2D_COLOR_INTERPOLATION_MODE_PREMULTIPLIED = 1
} progpu_native_direct2d_color_interpolation_mode;

/* Selects a surface-owned Win2D wrapper for reverse native-resource
 * interop. The provider supplies the exact CanvasDevice and DPI required by
 * each wrapper; callers supply only the requested native interface IID. */
typedef enum progpu_native_direct2d_win2d_resource_kind {
    PROGPU_NATIVE_DIRECT2D_WIN2D_RESOURCE_CANVAS_DEVICE = 1,
    PROGPU_NATIVE_DIRECT2D_WIN2D_RESOURCE_CANVAS_RENDER_TARGET = 2
} progpu_native_direct2d_win2d_resource_kind;

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

/* Linear floating-point color passed directly to D2D1_COLOR_F. Values must be
 * finite; HDR values outside [0, 1] remain valid and are not clamped. */
typedef struct progpu_native_direct2d_color_f {
    float red;
    float green;
    float blue;
    float alpha;
} progpu_native_direct2d_color_f;

typedef struct progpu_native_direct2d_gradient_stop {
    float position;
    progpu_native_direct2d_color_f color;
} progpu_native_direct2d_gradient_stop;

typedef struct progpu_native_direct2d_point_2f {
    float x;
    float y;
} progpu_native_direct2d_point_2f;

typedef struct progpu_native_direct2d_matrix_3x2_f {
    float m11;
    float m12;
    float m21;
    float m22;
    float m31;
    float m32;
} progpu_native_direct2d_matrix_3x2_f;

typedef struct progpu_native_direct2d_brush_properties {
    float opacity;
    progpu_native_direct2d_matrix_3x2_f transform;
} progpu_native_direct2d_brush_properties;

typedef struct progpu_native_direct2d_linear_gradient_brush_properties {
    progpu_native_direct2d_point_2f start_point;
    progpu_native_direct2d_point_2f end_point;
} progpu_native_direct2d_linear_gradient_brush_properties;

typedef struct progpu_native_direct2d_radial_gradient_brush_properties {
    progpu_native_direct2d_point_2f center;
    progpu_native_direct2d_point_2f gradient_origin_offset;
    float radius_x;
    float radius_y;
} progpu_native_direct2d_radial_gradient_brush_properties;

typedef struct progpu_native_direct2d_rect_f {
    float x;
    float y;
    float width;
    float height;
} progpu_native_direct2d_rect_f;

typedef struct progpu_native_direct2d_path_figure {
    progpu_native_direct2d_point_2f start_point;
    uint32_t first_segment;
    uint32_t segment_count;
    uint32_t flags;
    uint32_t reserved;
} progpu_native_direct2d_path_figure;

typedef struct progpu_native_direct2d_path_segment {
    progpu_native_direct2d_point_2f point1;
    progpu_native_direct2d_point_2f point2;
    progpu_native_direct2d_point_2f point3;
    progpu_native_direct2d_point_2f size;
    float rotation_angle;
    uint32_t kind;
    uint32_t flags;
    uint32_t arc_flags;
} progpu_native_direct2d_path_segment;

typedef struct progpu_native_direct2d_stroke_style_properties {
    uint32_t start_cap;
    uint32_t end_cap;
    uint32_t dash_cap;
    uint32_t line_join;
    float miter_limit;
    uint32_t dash_style;
    float dash_offset;
    uint32_t transform_type;
} progpu_native_direct2d_stroke_style_properties;

enum {
    PROGPU_NATIVE_DIRECT2D_ABI_VERSION = 10U
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

/* Queries the selected genuine Win2D object for its official
 * ICanvasResourceWrapperNative interface and returns the requested native
 * resource with one caller-owned COM reference. CanvasDevice unwraps without
 * a device/DPI argument. CanvasRenderTarget unwraps against this surface's
 * exact CanvasDevice and scalar target DPI. */
PROGPU_NATIVE_DIRECT2D_API progpu_native_direct2d_status
progpu_native_direct2d_surface_try_get_win2d_native_resource(
    progpu_native_direct2d_surface* surface,
    progpu_native_direct2d_win2d_resource_kind resource_kind,
    const progpu_native_direct2d_guid* interface_id,
    void** value,
    int32_t* native_hresult);

/* Creates a device-context-domain ID2D1SolidColorBrush. The returned genuine
 * COM interface owns one caller reference. Resource creation does not begin a
 * draw or acquire the shared target. */
PROGPU_NATIVE_DIRECT2D_API progpu_native_direct2d_status
progpu_native_direct2d_surface_create_solid_color_brush(
    progpu_native_direct2d_surface* surface,
    const progpu_native_direct2d_color_f* color,
    void** value,
    int32_t* native_hresult);

PROGPU_NATIVE_DIRECT2D_API progpu_native_direct2d_status
progpu_native_direct2d_surface_create_gradient_stop_collection(
    progpu_native_direct2d_surface* surface,
    const progpu_native_direct2d_gradient_stop* stops,
    uint32_t stop_count,
    progpu_native_direct2d_color_space pre_interpolation_space,
    progpu_native_direct2d_color_space post_interpolation_space,
    progpu_native_direct2d_buffer_precision buffer_precision,
    progpu_native_direct2d_extend_mode extend_mode,
    progpu_native_direct2d_color_interpolation_mode interpolation_mode,
    void** value,
    int32_t* native_hresult);

PROGPU_NATIVE_DIRECT2D_API progpu_native_direct2d_status
progpu_native_direct2d_surface_create_linear_gradient_brush(
    progpu_native_direct2d_surface* surface,
    const progpu_native_direct2d_linear_gradient_brush_properties* properties,
    const progpu_native_direct2d_brush_properties* brush_properties,
    void* gradient_stop_collection,
    void** value,
    int32_t* native_hresult);

PROGPU_NATIVE_DIRECT2D_API progpu_native_direct2d_status
progpu_native_direct2d_surface_create_radial_gradient_brush(
    progpu_native_direct2d_surface* surface,
    const progpu_native_direct2d_radial_gradient_brush_properties* properties,
    const progpu_native_direct2d_brush_properties* brush_properties,
    void* gradient_stop_collection,
    void** value,
    int32_t* native_hresult);

PROGPU_NATIVE_DIRECT2D_API progpu_native_direct2d_status
progpu_native_direct2d_surface_create_rectangle_geometry(
    progpu_native_direct2d_surface* surface,
    const progpu_native_direct2d_rect_f* rectangle,
    void** value,
    int32_t* native_hresult);

PROGPU_NATIVE_DIRECT2D_API progpu_native_direct2d_status
progpu_native_direct2d_surface_create_rounded_rectangle_geometry(
    progpu_native_direct2d_surface* surface,
    const progpu_native_direct2d_rect_f* rectangle,
    float radius_x,
    float radius_y,
    void** value,
    int32_t* native_hresult);

PROGPU_NATIVE_DIRECT2D_API progpu_native_direct2d_status
progpu_native_direct2d_surface_create_ellipse_geometry(
    progpu_native_direct2d_surface* surface,
    const progpu_native_direct2d_point_2f* center,
    float radius_x,
    float radius_y,
    void** value,
    int32_t* native_hresult);

PROGPU_NATIVE_DIRECT2D_API progpu_native_direct2d_status
progpu_native_direct2d_surface_create_path_geometry(
    progpu_native_direct2d_surface* surface,
    progpu_native_direct2d_fill_mode fill_mode,
    const progpu_native_direct2d_path_figure* figures,
    uint32_t figure_count,
    const progpu_native_direct2d_path_segment* segments,
    uint32_t segment_count,
    void** value,
    int32_t* native_hresult);

PROGPU_NATIVE_DIRECT2D_API progpu_native_direct2d_status
progpu_native_direct2d_surface_create_transformed_geometry(
    progpu_native_direct2d_surface* surface,
    void* geometry,
    const progpu_native_direct2d_matrix_3x2_f* transform,
    void** value,
    int32_t* native_hresult);

PROGPU_NATIVE_DIRECT2D_API progpu_native_direct2d_status
progpu_native_direct2d_surface_combine_geometry(
    progpu_native_direct2d_surface* surface,
    void* geometry_a,
    void* geometry_b,
    progpu_native_direct2d_combine_mode combine_mode,
    const progpu_native_direct2d_matrix_3x2_f* geometry_b_transform,
    float flattening_tolerance,
    void** value,
    int32_t* native_hresult);

/* Creates a genuine factory-domain ID2D1StrokeStyle1. Custom dash lengths are
 * passed as one contiguous caller-owned span and Direct2D copies them during
 * creation; there is no per-dash COM submission. */
PROGPU_NATIVE_DIRECT2D_API progpu_native_direct2d_status
progpu_native_direct2d_surface_create_stroke_style(
    progpu_native_direct2d_surface* surface,
    const progpu_native_direct2d_stroke_style_properties* properties,
    const float* dashes,
    uint32_t dash_count,
    void** value,
    int32_t* native_hresult);

/* Wraps a caller-owned native Direct2D resource through Win2D's official
 * ICanvasFactoryNative::GetOrCreate contract using this surface's exact
 * CanvasDevice. The native resource must remain alive for the duration of the
 * call. The returned IInspectable owns one caller reference. */
PROGPU_NATIVE_DIRECT2D_API progpu_native_direct2d_status
progpu_native_direct2d_surface_try_get_or_create_win2d_wrapper(
    progpu_native_direct2d_surface* surface,
    void* native_resource,
    float dpi,
    void** value,
    int32_t* native_hresult);

/* Reverse-unwraps any genuine Win2D resource wrapper in this surface's device
 * domain through ICanvasResourceWrapperNative. The returned native interface
 * owns one caller reference. */
PROGPU_NATIVE_DIRECT2D_API progpu_native_direct2d_status
progpu_native_direct2d_surface_try_get_win2d_wrapper_native_resource(
    progpu_native_direct2d_surface* surface,
    void* wrapper,
    float dpi,
    const progpu_native_direct2d_guid* interface_id,
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
