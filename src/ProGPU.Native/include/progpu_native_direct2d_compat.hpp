#pragma once

#include "progpu_native_com.hpp"
#include "progpu_native_direct2d_core.hpp"

#include <cstdint>

namespace progpu::native::direct2d::compat {

using point_2f = progpu_native_direct2d_point_2f;
using matrix_3x2_f = progpu_native_direct2d_matrix_3x2_f;
using triangle = progpu_native_direct2d_triangle;
using rectangle_f = core::rectangle_edges_f;
using bezier_segment = core::cubic_bezier_segment_f;
using size_f = core::size_f;
using sweep_direction = core::arc_sweep_direction;
using arc_size = core::arc_size_kind;
using arc_segment = core::arc_segment_f;
using ellipse = core::ellipse_f;
using rounded_rectangle = core::rounded_rectangle_f;
using cap_style = core::cap_style;
using line_join = core::line_join;
using dash_style = core::dash_style;
using stroke_style_properties = core::stroke_style_properties_f;

struct color_f final {
    float red;
    float green;
    float blue;
    float alpha;
};

struct brush_properties final {
    float opacity;
    matrix_3x2_f transform;
};

struct gradient_stop final {
    float position;
    color_f color;
};

struct linear_gradient_brush_properties final {
    point_2f start_point;
    point_2f end_point;
};

struct radial_gradient_brush_properties final {
    point_2f center;
    point_2f gradient_origin_offset;
    float radius_x;
    float radius_y;
};

struct point_2u final {
    std::uint32_t x;
    std::uint32_t y;
};

struct rectangle_u final {
    std::uint32_t left;
    std::uint32_t top;
    std::uint32_t right;
    std::uint32_t bottom;
};

inline constexpr com::result not_implemented = -2147467263;
inline constexpr com::result failure = -2147467259;
inline constexpr com::result wrong_factory = -2003238894;
inline constexpr com::result wrong_state = -2003238911;

inline constexpr com::guid resource_interface_id{
    0x2CD90691U,
    0x12E2U,
    0x11DCU,
    {0x9FU, 0xEDU, 0x00U, 0x11U, 0x43U, 0xA0U, 0x55U, 0xF9U}};
inline constexpr com::guid geometry_interface_id{
    0x2CD906A1U,
    0x12E2U,
    0x11DCU,
    {0x9FU, 0xEDU, 0x00U, 0x11U, 0x43U, 0xA0U, 0x55U, 0xF9U}};
inline constexpr com::guid rectangle_geometry_interface_id{
    0x2CD906A2U,
    0x12E2U,
    0x11DCU,
    {0x9FU, 0xEDU, 0x00U, 0x11U, 0x43U, 0xA0U, 0x55U, 0xF9U}};
inline constexpr com::guid ellipse_geometry_interface_id{
    0x2CD906A4U,
    0x12E2U,
    0x11DCU,
    {0x9FU, 0xEDU, 0x00U, 0x11U, 0x43U, 0xA0U, 0x55U, 0xF9U}};
inline constexpr com::guid rounded_rectangle_geometry_interface_id{
    0x2CD906A3U,
    0x12E2U,
    0x11DCU,
    {0x9FU, 0xEDU, 0x00U, 0x11U, 0x43U, 0xA0U, 0x55U, 0xF9U}};
inline constexpr com::guid geometry_group_interface_id{
    0x2CD906A6U,
    0x12E2U,
    0x11DCU,
    {0x9FU, 0xEDU, 0x00U, 0x11U, 0x43U, 0xA0U, 0x55U, 0xF9U}};
inline constexpr com::guid transformed_geometry_interface_id{
    0x2CD906BBU,
    0x12E2U,
    0x11DCU,
    {0x9FU, 0xEDU, 0x00U, 0x11U, 0x43U, 0xA0U, 0x55U, 0xF9U}};
inline constexpr com::guid path_geometry_interface_id{
    0x2CD906A5U,
    0x12E2U,
    0x11DCU,
    {0x9FU, 0xEDU, 0x00U, 0x11U, 0x43U, 0xA0U, 0x55U, 0xF9U}};
inline constexpr com::guid simplified_geometry_sink_interface_id{
    0x2CD9069EU,
    0x12E2U,
    0x11DCU,
    {0x9FU, 0xEDU, 0x00U, 0x11U, 0x43U, 0xA0U, 0x55U, 0xF9U}};
inline constexpr com::guid geometry_sink_interface_id{
    0x2CD9069FU,
    0x12E2U,
    0x11DCU,
    {0x9FU, 0xEDU, 0x00U, 0x11U, 0x43U, 0xA0U, 0x55U, 0xF9U}};
inline constexpr com::guid stroke_style_interface_id{
    0x2CD9069DU,
    0x12E2U,
    0x11DCU,
    {0x9FU, 0xEDU, 0x00U, 0x11U, 0x43U, 0xA0U, 0x55U, 0xF9U}};
inline constexpr com::guid drawing_state_block_interface_id{
    0x28506E39U,
    0xEBF6U,
    0x46A1U,
    {0xBBU, 0x47U, 0xFDU, 0x85U, 0x56U, 0x5AU, 0xB9U, 0x57U}};
inline constexpr com::guid layer_interface_id{
    0x2CD9069BU,
    0x12E2U,
    0x11DCU,
    {0x9FU, 0xEDU, 0x00U, 0x11U, 0x43U, 0xA0U, 0x55U, 0xF9U}};
inline constexpr com::guid brush_interface_id{
    0x2CD906A8U,
    0x12E2U,
    0x11DCU,
    {0x9FU, 0xEDU, 0x00U, 0x11U, 0x43U, 0xA0U, 0x55U, 0xF9U}};
inline constexpr com::guid solid_color_brush_interface_id{
    0x2CD906A9U,
    0x12E2U,
    0x11DCU,
    {0x9FU, 0xEDU, 0x00U, 0x11U, 0x43U, 0xA0U, 0x55U, 0xF9U}};
inline constexpr com::guid bitmap_interface_id{
    0xA2296057U,
    0xEA42U,
    0x4099U,
    {0x98U, 0x3BU, 0x53U, 0x9FU, 0xB6U, 0x50U, 0x54U, 0x26U}};
inline constexpr com::guid bitmap_brush_interface_id{
    0x2CD906AAU,
    0x12E2U,
    0x11DCU,
    {0x9FU, 0xEDU, 0x00U, 0x11U, 0x43U, 0xA0U, 0x55U, 0xF9U}};
inline constexpr com::guid gradient_stop_collection_interface_id{
    0x2CD906A7U,
    0x12E2U,
    0x11DCU,
    {0x9FU, 0xEDU, 0x00U, 0x11U, 0x43U, 0xA0U, 0x55U, 0xF9U}};
inline constexpr com::guid linear_gradient_brush_interface_id{
    0x2CD906ABU,
    0x12E2U,
    0x11DCU,
    {0x9FU, 0xEDU, 0x00U, 0x11U, 0x43U, 0xA0U, 0x55U, 0xF9U}};
inline constexpr com::guid radial_gradient_brush_interface_id{
    0x2CD906ACU,
    0x12E2U,
    0x11DCU,
    {0x9FU, 0xEDU, 0x00U, 0x11U, 0x43U, 0xA0U, 0x55U, 0xF9U}};
inline constexpr com::guid render_target_interface_id{
    0x2CD90694U,
    0x12E2U,
    0x11DCU,
    {0x9FU, 0xEDU, 0x00U, 0x11U, 0x43U, 0xA0U, 0x55U, 0xF9U}};
inline constexpr com::guid factory_interface_id{
    0x06152247U,
    0x6F50U,
    0x465AU,
    {0x92U, 0x45U, 0x11U, 0x8BU, 0xFDU, 0x3BU, 0x60U, 0x07U}};
inline constexpr com::guid factory_native_interface_id{
    0x19967CEEU,
    0xEA52U,
    0x45DDU,
    {0x9FU, 0xDAU, 0xD9U, 0x70U, 0x3AU, 0x9FU, 0xD1U, 0x50U}};
inline constexpr com::guid scene_factory_native_interface_id{
    0x46B5C76BU,
    0xC27CU,
    0x4364U,
    {0x94U, 0x6BU, 0x89U, 0x84U, 0x41U, 0x48U, 0x98U, 0x65U}};
inline constexpr com::guid scene_render_target_native_interface_id{
    0x170588C0U,
    0x12A5U,
    0x4200U,
    {0x93U, 0x4CU, 0x34U, 0x56U, 0x8FU, 0xF9U, 0xD8U, 0xE8U}};

enum class fill_mode : std::uint32_t {
    alternate = 0U,
    winding = 1U
};

enum class path_segment : std::uint32_t {
    none = 0U,
    force_unstroked = 1U,
    force_round_line_join = 2U
};

enum class figure_begin : std::uint32_t {
    filled = 0U,
    hollow = 1U
};

enum class figure_end : std::uint32_t {
    open = 0U,
    closed = 1U
};

enum class geometry_relation : std::uint32_t {
    unknown = 0U,
    disjoint = 1U,
    is_contained = 2U,
    contains = 3U,
    overlap = 4U
};

enum class geometry_simplification_option : std::uint32_t {
    cubics_and_lines = 0U,
    lines = 1U
};

enum class combine_mode : std::uint32_t {
    union_value = 0U,
    intersect = 1U,
    xor_value = 2U,
    exclude = 3U
};

enum class antialias_mode : std::uint32_t {
    per_primitive = 0U,
    aliased = 1U
};

enum class text_antialias_mode : std::uint32_t {
    default_value = 0U,
    cleartype = 1U,
    grayscale = 2U,
    aliased = 3U
};

enum class alpha_mode : std::uint32_t {
    unknown = 0U,
    premultiplied = 1U,
    straight = 2U,
    ignore = 3U
};

enum class bitmap_interpolation_mode : std::uint32_t {
    nearest_neighbor = 0U,
    linear = 1U
};

enum class opacity_mask_content : std::uint32_t {
    graphics = 0U,
    text_natural = 1U,
    text_gdi_compatible = 2U
};

enum class measuring_mode : std::uint32_t {
    natural = 0U,
    gdi_classic = 1U,
    gdi_natural = 2U
};

enum class draw_text_options : std::uint32_t {
    none = 0U,
    no_snap = 1U,
    clip = 2U,
    enable_color_font = 4U,
    disable_color_bitmap_snapping = 8U
};

enum class compatible_render_target_options : std::uint32_t {
    none = 0U,
    gdi_compatible = 1U
};

enum class layer_options : std::uint32_t {
    none = 0U,
    initialize_for_cleartype = 1U
};

enum class gamma : std::uint32_t {
    gamma_2_2 = 0U,
    gamma_1_0 = 1U
};

enum class extend_mode : std::uint32_t {
    clamp = 0U,
    wrap = 1U,
    mirror = 2U
};

struct pixel_format final {
    std::uint32_t format;
    alpha_mode alpha;
};

struct bitmap_properties final {
    pixel_format pixel_format_value;
    float dpi_x;
    float dpi_y;
};

struct bitmap_brush_properties final {
    extend_mode extend_mode_x;
    extend_mode extend_mode_y;
    bitmap_interpolation_mode interpolation_mode;
};

struct size_u final {
    std::uint32_t width;
    std::uint32_t height;
};

struct scene_render_target_properties final {
    std::uint32_t pixel_width;
    std::uint32_t pixel_height;
    float dpi_x;
    float dpi_y;
    std::uint64_t scene_id;
    std::uint64_t generation;
};

struct scene_render_target_summary final {
    std::uint64_t scene_id;
    std::uint64_t generation;
    std::uint32_t draw_count;
    std::int32_t has_clear;
    color_f clear_color;
};

struct drawing_state_description final {
    antialias_mode antialias;
    text_antialias_mode text_antialias;
    std::uint64_t tag1;
    std::uint64_t tag2;
    matrix_3x2_f transform;
};

struct quadratic_bezier_segment final {
    point_2f point1;
    point_2f point2;
};

struct render_target_properties;
struct hwnd_render_target_properties;

struct factory;
struct geometry;
struct geometry_group;
struct path_geometry;
struct ellipse_geometry;
struct rounded_rectangle_geometry;
struct geometry_sink;
struct stroke_style;
struct drawing_state_block;
struct brush;
struct solid_color_brush;
struct render_target;
struct hwnd_render_target;
struct dc_render_target;
struct bitmap;
struct bitmap_brush;
struct gradient_stop_collection;
struct linear_gradient_brush;
struct radial_gradient_brush;
struct bitmap_render_target;
struct layer;
struct mesh;
struct text_format;
struct text_layout;
struct rendering_parameters;
struct glyph_run;

struct layer_parameters final {
    rectangle_f content_bounds;
    geometry* geometric_mask;
    antialias_mode mask_antialias_mode;
    matrix_3x2_f mask_transform;
    float opacity;
    brush* opacity_brush;
    layer_options options;
};

struct simplified_geometry_sink : com::unknown {
    virtual void PROGPU_NATIVE_COM_CALL SetFillMode(fill_mode value)
        noexcept = 0;
    virtual void PROGPU_NATIVE_COM_CALL SetSegmentFlags(path_segment value)
        noexcept = 0;
    virtual void PROGPU_NATIVE_COM_CALL BeginFigure(
        point_2f start,
        figure_begin begin) noexcept = 0;
    virtual void PROGPU_NATIVE_COM_CALL AddLines(
        const point_2f* points,
        std::uint32_t point_count) noexcept = 0;
    virtual void PROGPU_NATIVE_COM_CALL AddBeziers(
        const bezier_segment* beziers,
        std::uint32_t bezier_count) noexcept = 0;
    virtual void PROGPU_NATIVE_COM_CALL EndFigure(figure_end end)
        noexcept = 0;
    virtual com::result PROGPU_NATIVE_COM_CALL Close() noexcept = 0;
};

struct geometry_sink : simplified_geometry_sink {
    virtual void PROGPU_NATIVE_COM_CALL AddLine(point_2f point) noexcept = 0;
    virtual void PROGPU_NATIVE_COM_CALL AddBezier(
        const bezier_segment* bezier) noexcept = 0;
    virtual void PROGPU_NATIVE_COM_CALL AddQuadraticBezier(
        const quadratic_bezier_segment* bezier) noexcept = 0;
    virtual void PROGPU_NATIVE_COM_CALL AddQuadraticBeziers(
        const quadratic_bezier_segment* beziers,
        std::uint32_t bezier_count) noexcept = 0;
    virtual void PROGPU_NATIVE_COM_CALL AddArc(
        const arc_segment* arc) noexcept = 0;
};

struct tessellation_sink : com::unknown {
    virtual void PROGPU_NATIVE_COM_CALL AddTriangles(
        const triangle* triangles,
        std::uint32_t triangle_count) noexcept = 0;
    virtual com::result PROGPU_NATIVE_COM_CALL Close() noexcept = 0;
};

struct resource : com::unknown {
    virtual void PROGPU_NATIVE_COM_CALL GetFactory(factory** value) const
        noexcept = 0;
};

struct geometry : resource {
    virtual com::result PROGPU_NATIVE_COM_CALL GetBounds(
        const matrix_3x2_f* world_transform,
        rectangle_f* bounds) const noexcept = 0;
    virtual com::result PROGPU_NATIVE_COM_CALL GetWidenedBounds(
        float stroke_width,
        stroke_style* style,
        const matrix_3x2_f* world_transform,
        float flattening_tolerance,
        rectangle_f* bounds) const noexcept = 0;
    virtual com::result PROGPU_NATIVE_COM_CALL StrokeContainsPoint(
        point_2f point,
        float stroke_width,
        stroke_style* style,
        const matrix_3x2_f* world_transform,
        float flattening_tolerance,
        std::int32_t* contains) const noexcept = 0;
    virtual com::result PROGPU_NATIVE_COM_CALL FillContainsPoint(
        point_2f point,
        const matrix_3x2_f* world_transform,
        float flattening_tolerance,
        std::int32_t* contains) const noexcept = 0;
    virtual com::result PROGPU_NATIVE_COM_CALL CompareWithGeometry(
        geometry* input_geometry,
        const matrix_3x2_f* input_geometry_transform,
        float flattening_tolerance,
        geometry_relation* relation) const noexcept = 0;
    virtual com::result PROGPU_NATIVE_COM_CALL Simplify(
        geometry_simplification_option simplification_option,
        const matrix_3x2_f* world_transform,
        float flattening_tolerance,
        simplified_geometry_sink* geometry_sink) const noexcept = 0;
    virtual com::result PROGPU_NATIVE_COM_CALL Tessellate(
        const matrix_3x2_f* world_transform,
        float flattening_tolerance,
        tessellation_sink* sink) const noexcept = 0;
    virtual com::result PROGPU_NATIVE_COM_CALL CombineWithGeometry(
        geometry* input_geometry,
        combine_mode mode,
        const matrix_3x2_f* input_geometry_transform,
        float flattening_tolerance,
        simplified_geometry_sink* geometry_sink) const noexcept = 0;
    virtual com::result PROGPU_NATIVE_COM_CALL Outline(
        const matrix_3x2_f* world_transform,
        float flattening_tolerance,
        simplified_geometry_sink* geometry_sink) const noexcept = 0;
    virtual com::result PROGPU_NATIVE_COM_CALL ComputeArea(
        const matrix_3x2_f* world_transform,
        float flattening_tolerance,
        float* area) const noexcept = 0;
    virtual com::result PROGPU_NATIVE_COM_CALL ComputeLength(
        const matrix_3x2_f* world_transform,
        float flattening_tolerance,
        float* length) const noexcept = 0;
    virtual com::result PROGPU_NATIVE_COM_CALL ComputePointAtLength(
        float length,
        const matrix_3x2_f* world_transform,
        float flattening_tolerance,
        point_2f* point,
        point_2f* unit_tangent) const noexcept = 0;
    virtual com::result PROGPU_NATIVE_COM_CALL Widen(
        float stroke_width,
        stroke_style* style,
        const matrix_3x2_f* world_transform,
        float flattening_tolerance,
        simplified_geometry_sink* geometry_sink) const noexcept = 0;
};

struct rectangle_geometry : geometry {
    virtual void PROGPU_NATIVE_COM_CALL GetRect(rectangle_f* rectangle) const
        noexcept = 0;
};

struct transformed_geometry : geometry {
    virtual void PROGPU_NATIVE_COM_CALL GetSourceGeometry(
        geometry** source) const noexcept = 0;
    virtual void PROGPU_NATIVE_COM_CALL GetTransform(
        matrix_3x2_f* transform) const noexcept = 0;
};

struct ellipse_geometry : geometry {
    virtual void PROGPU_NATIVE_COM_CALL GetEllipse(ellipse* value) const
        noexcept = 0;
};

struct rounded_rectangle_geometry : geometry {
    virtual void PROGPU_NATIVE_COM_CALL GetRoundedRect(
        rounded_rectangle* value) const noexcept = 0;
};

struct geometry_group : geometry {
    virtual fill_mode PROGPU_NATIVE_COM_CALL GetFillMode() const
        noexcept = 0;
    virtual std::uint32_t PROGPU_NATIVE_COM_CALL GetSourceGeometryCount()
        const noexcept = 0;
    virtual void PROGPU_NATIVE_COM_CALL GetSourceGeometries(
        geometry** geometries,
        std::uint32_t geometry_count) const noexcept = 0;
};

struct stroke_style : resource {
    virtual cap_style PROGPU_NATIVE_COM_CALL GetStartCap() const
        noexcept = 0;
    virtual cap_style PROGPU_NATIVE_COM_CALL GetEndCap() const
        noexcept = 0;
    virtual cap_style PROGPU_NATIVE_COM_CALL GetDashCap() const
        noexcept = 0;
    virtual float PROGPU_NATIVE_COM_CALL GetMiterLimit() const
        noexcept = 0;
    virtual line_join PROGPU_NATIVE_COM_CALL GetLineJoin() const
        noexcept = 0;
    virtual float PROGPU_NATIVE_COM_CALL GetDashOffset() const
        noexcept = 0;
    virtual dash_style PROGPU_NATIVE_COM_CALL GetDashStyle() const
        noexcept = 0;
    virtual std::uint32_t PROGPU_NATIVE_COM_CALL GetDashesCount() const
        noexcept = 0;
    virtual void PROGPU_NATIVE_COM_CALL GetDashes(
        float* dashes,
        std::uint32_t dash_count) const noexcept = 0;
};

struct drawing_state_block : resource {
    virtual void PROGPU_NATIVE_COM_CALL GetDescription(
        drawing_state_description* description) const noexcept = 0;
    virtual void PROGPU_NATIVE_COM_CALL SetDescription(
        const drawing_state_description* description) noexcept = 0;
    virtual void PROGPU_NATIVE_COM_CALL SetTextRenderingParams(
        com::unknown* parameters) noexcept = 0;
    virtual void PROGPU_NATIVE_COM_CALL GetTextRenderingParams(
        com::unknown** parameters) const noexcept = 0;
};

struct layer : resource {
    virtual size_f PROGPU_NATIVE_COM_CALL GetSize() const noexcept = 0;
};

struct brush : resource {
    virtual void PROGPU_NATIVE_COM_CALL SetOpacity(float opacity)
        noexcept = 0;
    virtual void PROGPU_NATIVE_COM_CALL SetTransform(
        const matrix_3x2_f* transform) noexcept = 0;
    virtual float PROGPU_NATIVE_COM_CALL GetOpacity() const noexcept = 0;
    virtual void PROGPU_NATIVE_COM_CALL GetTransform(
        matrix_3x2_f* transform) const noexcept = 0;
};

struct solid_color_brush : brush {
    virtual void PROGPU_NATIVE_COM_CALL SetColor(
        const color_f* color) noexcept = 0;
    virtual color_f PROGPU_NATIVE_COM_CALL GetColor() const noexcept = 0;
};

struct bitmap : resource {
    virtual size_f PROGPU_NATIVE_COM_CALL GetSize() const noexcept = 0;
    virtual size_u PROGPU_NATIVE_COM_CALL GetPixelSize() const noexcept = 0;
    virtual pixel_format PROGPU_NATIVE_COM_CALL GetPixelFormat()
        const noexcept = 0;
    virtual void PROGPU_NATIVE_COM_CALL GetDpi(
        float* dpi_x,
        float* dpi_y) const noexcept = 0;
    virtual com::result PROGPU_NATIVE_COM_CALL CopyFromBitmap(
        const point_2u* destination_point,
        bitmap* source,
        const rectangle_u* source_rectangle) noexcept = 0;
    virtual com::result PROGPU_NATIVE_COM_CALL CopyFromRenderTarget(
        const point_2u* destination_point,
        render_target* source,
        const rectangle_u* source_rectangle) noexcept = 0;
    virtual com::result PROGPU_NATIVE_COM_CALL CopyFromMemory(
        const rectangle_u* destination_rectangle,
        const void* source_data,
        std::uint32_t pitch) noexcept = 0;
};

struct bitmap_brush : brush {
    virtual void PROGPU_NATIVE_COM_CALL SetExtendModeX(
        extend_mode extend) noexcept = 0;
    virtual void PROGPU_NATIVE_COM_CALL SetExtendModeY(
        extend_mode extend) noexcept = 0;
    virtual void PROGPU_NATIVE_COM_CALL SetInterpolationMode(
        bitmap_interpolation_mode interpolation) noexcept = 0;
    virtual void PROGPU_NATIVE_COM_CALL SetBitmap(bitmap* value) noexcept = 0;
    virtual extend_mode PROGPU_NATIVE_COM_CALL GetExtendModeX()
        const noexcept = 0;
    virtual extend_mode PROGPU_NATIVE_COM_CALL GetExtendModeY()
        const noexcept = 0;
    virtual bitmap_interpolation_mode PROGPU_NATIVE_COM_CALL
        GetInterpolationMode() const noexcept = 0;
    virtual void PROGPU_NATIVE_COM_CALL GetBitmap(bitmap** value)
        const noexcept = 0;
};

struct gradient_stop_collection : resource {
    virtual std::uint32_t PROGPU_NATIVE_COM_CALL GetGradientStopCount()
        const noexcept = 0;
    virtual void PROGPU_NATIVE_COM_CALL GetGradientStops(
        gradient_stop* gradient_stops,
        std::uint32_t gradient_stop_count) const noexcept = 0;
    virtual gamma PROGPU_NATIVE_COM_CALL GetColorInterpolationGamma()
        const noexcept = 0;
    virtual extend_mode PROGPU_NATIVE_COM_CALL GetExtendMode()
        const noexcept = 0;
};

struct linear_gradient_brush : brush {
    virtual void PROGPU_NATIVE_COM_CALL SetStartPoint(point_2f start_point)
        noexcept = 0;
    virtual void PROGPU_NATIVE_COM_CALL SetEndPoint(point_2f end_point)
        noexcept = 0;
    virtual point_2f PROGPU_NATIVE_COM_CALL GetStartPoint()
        const noexcept = 0;
    virtual point_2f PROGPU_NATIVE_COM_CALL GetEndPoint()
        const noexcept = 0;
    virtual void PROGPU_NATIVE_COM_CALL GetGradientStopCollection(
        gradient_stop_collection** collection) const noexcept = 0;
};

struct radial_gradient_brush : brush {
    virtual void PROGPU_NATIVE_COM_CALL SetCenter(point_2f center)
        noexcept = 0;
    virtual void PROGPU_NATIVE_COM_CALL SetGradientOriginOffset(
        point_2f gradient_origin_offset) noexcept = 0;
    virtual void PROGPU_NATIVE_COM_CALL SetRadiusX(float radius_x)
        noexcept = 0;
    virtual void PROGPU_NATIVE_COM_CALL SetRadiusY(float radius_y)
        noexcept = 0;
    virtual point_2f PROGPU_NATIVE_COM_CALL GetCenter() const noexcept = 0;
    virtual point_2f PROGPU_NATIVE_COM_CALL GetGradientOriginOffset()
        const noexcept = 0;
    virtual float PROGPU_NATIVE_COM_CALL GetRadiusX() const noexcept = 0;
    virtual float PROGPU_NATIVE_COM_CALL GetRadiusY() const noexcept = 0;
    virtual void PROGPU_NATIVE_COM_CALL GetGradientStopCollection(
        gradient_stop_collection** collection) const noexcept = 0;
};

/* ProGPU's stable activation seam for resources that the original factory
 * cannot create. Its IID and method order match the Windows provider's
 * IProGpuD2DCompatFactoryNative contract. */
struct factory_native : com::unknown {
    virtual com::result PROGPU_NATIVE_COM_CALL CreateSolidColorBrush(
        const color_f* color,
        const brush_properties* properties,
        solid_color_brush** value) noexcept = 0;
};

/* A separate IID keeps the established Windows factory extension immutable. */
struct scene_factory_native : com::unknown {
    virtual com::result PROGPU_NATIVE_COM_CALL CreateSceneRenderTarget(
        const scene_render_target_properties* properties,
        render_target** value) noexcept = 0;
};

struct scene_render_target_native : com::unknown {
    virtual std::uint64_t PROGPU_NATIVE_COM_CALL GetRequiredSceneSize()
        const noexcept = 0;
    virtual com::result PROGPU_NATIVE_COM_CALL BuildScene(
        void* destination,
        std::uint64_t destination_size,
        std::uint64_t* bytes_written) const noexcept = 0;
    virtual void PROGPU_NATIVE_COM_CALL GetSummary(
        scene_render_target_summary* summary) const noexcept = 0;
};

struct path_geometry : geometry {
    virtual com::result PROGPU_NATIVE_COM_CALL Open(
        geometry_sink** sink) noexcept = 0;
    virtual com::result PROGPU_NATIVE_COM_CALL Stream(
        geometry_sink* sink) const noexcept = 0;
    virtual com::result PROGPU_NATIVE_COM_CALL GetSegmentCount(
        std::uint32_t* count) const noexcept = 0;
    virtual com::result PROGPU_NATIVE_COM_CALL GetFigureCount(
        std::uint32_t* count) const noexcept = 0;
};

/* The method order is the original ID2D1RenderTarget vtable. Resource families
 * not yet represented by portable descriptors retain their slots and fail
 * closed in the scene target implementation. */
struct render_target : resource {
    virtual com::result PROGPU_NATIVE_COM_CALL CreateBitmap(
        size_u size,
        const void* source_data,
        std::uint32_t pitch,
        const bitmap_properties* properties,
        bitmap** value) noexcept = 0;
    virtual com::result PROGPU_NATIVE_COM_CALL CreateBitmapFromWicBitmap(
        com::unknown* source,
        const bitmap_properties* properties,
        bitmap** value) noexcept = 0;
    virtual com::result PROGPU_NATIVE_COM_CALL CreateSharedBitmap(
        com::guid_ref interface_id,
        void* data,
        const bitmap_properties* properties,
        bitmap** value) noexcept = 0;
    virtual com::result PROGPU_NATIVE_COM_CALL CreateBitmapBrush(
        bitmap* source,
        const bitmap_brush_properties* bitmap_properties_value,
        const brush_properties* properties,
        bitmap_brush** value) noexcept = 0;
    virtual com::result PROGPU_NATIVE_COM_CALL CreateSolidColorBrush(
        const color_f* color,
        const brush_properties* properties,
        solid_color_brush** value) noexcept = 0;
    virtual com::result PROGPU_NATIVE_COM_CALL CreateGradientStopCollection(
        const gradient_stop* gradient_stops,
        std::uint32_t gradient_stop_count,
        gamma color_interpolation_gamma,
        extend_mode extend_mode_value,
        gradient_stop_collection** value) noexcept = 0;
    virtual com::result PROGPU_NATIVE_COM_CALL CreateLinearGradientBrush(
        const linear_gradient_brush_properties* gradient_properties,
        const brush_properties* properties,
        gradient_stop_collection* stops,
        linear_gradient_brush** value) noexcept = 0;
    virtual com::result PROGPU_NATIVE_COM_CALL CreateRadialGradientBrush(
        const radial_gradient_brush_properties* gradient_properties,
        const brush_properties* properties,
        gradient_stop_collection* stops,
        radial_gradient_brush** value) noexcept = 0;
    virtual com::result PROGPU_NATIVE_COM_CALL CreateCompatibleRenderTarget(
        const size_f* desired_size,
        const size_u* desired_pixel_size,
        const pixel_format* desired_format,
        compatible_render_target_options options,
        bitmap_render_target** value) noexcept = 0;
    virtual com::result PROGPU_NATIVE_COM_CALL CreateLayer(
        const size_f* size,
        layer** value) noexcept = 0;
    virtual com::result PROGPU_NATIVE_COM_CALL CreateMesh(
        mesh** value) noexcept = 0;
    virtual void PROGPU_NATIVE_COM_CALL DrawLine(
        point_2f point0,
        point_2f point1,
        brush* brush_value,
        float stroke_width,
        stroke_style* style) noexcept = 0;
    virtual void PROGPU_NATIVE_COM_CALL DrawRectangle(
        const rectangle_f* rectangle,
        brush* brush_value,
        float stroke_width,
        stroke_style* style) noexcept = 0;
    virtual void PROGPU_NATIVE_COM_CALL FillRectangle(
        const rectangle_f* rectangle,
        brush* brush_value) noexcept = 0;
    virtual void PROGPU_NATIVE_COM_CALL DrawRoundedRectangle(
        const rounded_rectangle* rectangle,
        brush* brush_value,
        float stroke_width,
        stroke_style* style) noexcept = 0;
    virtual void PROGPU_NATIVE_COM_CALL FillRoundedRectangle(
        const rounded_rectangle* rectangle,
        brush* brush_value) noexcept = 0;
    virtual void PROGPU_NATIVE_COM_CALL DrawEllipse(
        const ellipse* ellipse_value,
        brush* brush_value,
        float stroke_width,
        stroke_style* style) noexcept = 0;
    virtual void PROGPU_NATIVE_COM_CALL FillEllipse(
        const ellipse* ellipse_value,
        brush* brush_value) noexcept = 0;
    virtual void PROGPU_NATIVE_COM_CALL DrawGeometry(
        geometry* geometry_value,
        brush* brush_value,
        float stroke_width,
        stroke_style* style) noexcept = 0;
    virtual void PROGPU_NATIVE_COM_CALL FillGeometry(
        geometry* geometry_value,
        brush* brush_value,
        brush* opacity_brush) noexcept = 0;
    virtual void PROGPU_NATIVE_COM_CALL FillMesh(
        mesh* mesh_value,
        brush* brush_value) noexcept = 0;
    virtual void PROGPU_NATIVE_COM_CALL FillOpacityMask(
        bitmap* mask,
        brush* brush_value,
        opacity_mask_content content,
        const rectangle_f* destination,
        const rectangle_f* source) noexcept = 0;
    virtual void PROGPU_NATIVE_COM_CALL DrawBitmap(
        bitmap* bitmap_value,
        const rectangle_f* destination,
        float opacity,
        bitmap_interpolation_mode interpolation,
        const rectangle_f* source) noexcept = 0;
    virtual void PROGPU_NATIVE_COM_CALL DrawText(
        const wchar_t* text,
        std::uint32_t text_length,
        text_format* format,
        const rectangle_f* layout_rectangle,
        brush* default_brush,
        draw_text_options options,
        measuring_mode measuring) noexcept = 0;
    virtual void PROGPU_NATIVE_COM_CALL DrawTextLayout(
        point_2f origin,
        text_layout* layout,
        brush* default_brush,
        draw_text_options options) noexcept = 0;
    virtual void PROGPU_NATIVE_COM_CALL DrawGlyphRun(
        point_2f baseline_origin,
        const glyph_run* glyphs,
        brush* foreground,
        measuring_mode measuring) noexcept = 0;
    virtual void PROGPU_NATIVE_COM_CALL SetTransform(
        const matrix_3x2_f* transform) noexcept = 0;
    virtual void PROGPU_NATIVE_COM_CALL GetTransform(
        matrix_3x2_f* transform) const noexcept = 0;
    virtual void PROGPU_NATIVE_COM_CALL SetAntialiasMode(
        antialias_mode mode) noexcept = 0;
    virtual antialias_mode PROGPU_NATIVE_COM_CALL GetAntialiasMode()
        const noexcept = 0;
    virtual void PROGPU_NATIVE_COM_CALL SetTextAntialiasMode(
        text_antialias_mode mode) noexcept = 0;
    virtual text_antialias_mode PROGPU_NATIVE_COM_CALL GetTextAntialiasMode()
        const noexcept = 0;
    virtual void PROGPU_NATIVE_COM_CALL SetTextRenderingParams(
        rendering_parameters* parameters) noexcept = 0;
    virtual void PROGPU_NATIVE_COM_CALL GetTextRenderingParams(
        rendering_parameters** parameters) const noexcept = 0;
    virtual void PROGPU_NATIVE_COM_CALL SetTags(
        std::uint64_t tag1,
        std::uint64_t tag2) noexcept = 0;
    virtual void PROGPU_NATIVE_COM_CALL GetTags(
        std::uint64_t* tag1,
        std::uint64_t* tag2) const noexcept = 0;
    virtual void PROGPU_NATIVE_COM_CALL PushLayer(
        const layer_parameters* parameters,
        layer* layer_value) noexcept = 0;
    virtual void PROGPU_NATIVE_COM_CALL PopLayer() noexcept = 0;
    virtual com::result PROGPU_NATIVE_COM_CALL Flush(
        std::uint64_t* tag1,
        std::uint64_t* tag2) noexcept = 0;
    virtual void PROGPU_NATIVE_COM_CALL SaveDrawingState(
        drawing_state_block* state) const noexcept = 0;
    virtual void PROGPU_NATIVE_COM_CALL RestoreDrawingState(
        drawing_state_block* state) noexcept = 0;
    virtual void PROGPU_NATIVE_COM_CALL PushAxisAlignedClip(
        const rectangle_f* rectangle,
        antialias_mode mode) noexcept = 0;
    virtual void PROGPU_NATIVE_COM_CALL PopAxisAlignedClip() noexcept = 0;
    virtual void PROGPU_NATIVE_COM_CALL Clear(
        const color_f* clear_color) noexcept = 0;
    virtual void PROGPU_NATIVE_COM_CALL BeginDraw() noexcept = 0;
    virtual com::result PROGPU_NATIVE_COM_CALL EndDraw(
        std::uint64_t* tag1,
        std::uint64_t* tag2) noexcept = 0;
    virtual pixel_format PROGPU_NATIVE_COM_CALL GetPixelFormat()
        const noexcept = 0;
    virtual void PROGPU_NATIVE_COM_CALL SetDpi(
        float dpi_x,
        float dpi_y) noexcept = 0;
    virtual void PROGPU_NATIVE_COM_CALL GetDpi(
        float* dpi_x,
        float* dpi_y) const noexcept = 0;
    virtual size_f PROGPU_NATIVE_COM_CALL GetSize() const noexcept = 0;
    virtual size_u PROGPU_NATIVE_COM_CALL GetPixelSize() const noexcept = 0;
    virtual std::uint32_t PROGPU_NATIVE_COM_CALL GetMaximumBitmapSize()
        const noexcept = 0;
    virtual std::int32_t PROGPU_NATIVE_COM_CALL IsSupported(
        const render_target_properties* properties) const noexcept = 0;
};

/* The method order matches the original ID2D1Factory vtable. Unsupported
 * resource families are deliberately present and return not_implemented so
 * adding resource support cannot shift later ABI slots. Opaque descriptor
 * declarations are expanded only as their resource families become portable. */
struct factory : com::unknown {
    virtual com::result PROGPU_NATIVE_COM_CALL ReloadSystemMetrics()
        noexcept = 0;
    virtual void PROGPU_NATIVE_COM_CALL GetDesktopDpi(
        float* dpi_x,
        float* dpi_y) noexcept = 0;
    virtual com::result PROGPU_NATIVE_COM_CALL CreateRectangleGeometry(
        const rectangle_f* rectangle,
        rectangle_geometry** value) noexcept = 0;
    virtual com::result PROGPU_NATIVE_COM_CALL CreateRoundedRectangleGeometry(
        const rounded_rectangle* rectangle,
        rounded_rectangle_geometry** value) noexcept = 0;
    virtual com::result PROGPU_NATIVE_COM_CALL CreateEllipseGeometry(
        const ellipse* ellipse_value,
        ellipse_geometry** value) noexcept = 0;
    virtual com::result PROGPU_NATIVE_COM_CALL CreateGeometryGroup(
        fill_mode mode,
        geometry** geometries,
        std::uint32_t geometry_count,
        geometry_group** value) noexcept = 0;
    virtual com::result PROGPU_NATIVE_COM_CALL CreateTransformedGeometry(
        geometry* source,
        const matrix_3x2_f* transform,
        transformed_geometry** value) noexcept = 0;
    virtual com::result PROGPU_NATIVE_COM_CALL CreatePathGeometry(
        path_geometry** value) noexcept = 0;
    virtual com::result PROGPU_NATIVE_COM_CALL CreateStrokeStyle(
        const stroke_style_properties* properties,
        const float* dashes,
        std::uint32_t dash_count,
        stroke_style** value) noexcept = 0;
    virtual com::result PROGPU_NATIVE_COM_CALL CreateDrawingStateBlock(
        const drawing_state_description* description,
        com::unknown* text_rendering_parameters,
        drawing_state_block** value) noexcept = 0;
    virtual com::result PROGPU_NATIVE_COM_CALL CreateWicBitmapRenderTarget(
        com::unknown* target,
        const render_target_properties* properties,
        render_target** value) noexcept = 0;
    virtual com::result PROGPU_NATIVE_COM_CALL CreateHwndRenderTarget(
        const render_target_properties* properties,
        const hwnd_render_target_properties* hwnd_properties,
        hwnd_render_target** value) noexcept = 0;
    virtual com::result PROGPU_NATIVE_COM_CALL CreateDxgiSurfaceRenderTarget(
        com::unknown* surface,
        const render_target_properties* properties,
        render_target** value) noexcept = 0;
    virtual com::result PROGPU_NATIVE_COM_CALL CreateDCRenderTarget(
        const render_target_properties* properties,
        dc_render_target** value) noexcept = 0;
};

[[nodiscard]] com::result create_factory(factory** value) noexcept;

} // namespace progpu::native::direct2d::compat
