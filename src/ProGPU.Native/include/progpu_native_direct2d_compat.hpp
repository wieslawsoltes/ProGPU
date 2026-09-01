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
inline constexpr com::guid factory_interface_id{
    0x06152247U,
    0x6F50U,
    0x465AU,
    {0x92U, 0x45U, 0x11U, 0x8BU, 0xFDU, 0x3BU, 0x60U, 0x07U}};

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
struct render_target;
struct hwnd_render_target;
struct dc_render_target;

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
