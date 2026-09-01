#include "progpu_native_direct2d_compat.hpp"
#include "progpu_native_direct2d_scene_submission.hpp"
#include "progpu_native.h"

#if defined(_WIN32)
#  include <d2d1.h>
#endif

#include <cmath>
#include <cstdio>
#include <cstdint>
#include <limits>
#include <vector>

namespace compat = progpu::native::direct2d::compat;
namespace core = progpu::native::direct2d::core;
namespace com = progpu::native::com;

namespace {

[[nodiscard]] bool approximately_equal(float left, float right) noexcept
{
    return std::abs(left - right) <= 0.0001F;
}

class simplified_sink final : public compat::geometry_sink {
public:
    com::result PROGPU_NATIVE_COM_CALL QueryInterface(
        com::guid_ref interface_id,
        void** value) noexcept override
    {
        if (value == nullptr) {
            return com::pointer_error;
        }
        *value = nullptr;
        if (com::guid_equal(interface_id, com::unknown_interface_id()) ||
            com::guid_equal(
                interface_id,
                compat::simplified_geometry_sink_interface_id) ||
            com::guid_equal(
                interface_id, compat::geometry_sink_interface_id)) {
            *value = static_cast<compat::geometry_sink*>(this);
            AddRef();
            return com::ok;
        }
        return com::no_interface;
    }

    com::reference_count_value PROGPU_NATIVE_COM_CALL AddRef()
        noexcept override
    {
        return reference_count_.add_ref();
    }

    com::reference_count_value PROGPU_NATIVE_COM_CALL Release()
        noexcept override
    {
        return reference_count_.release(this);
    }

    void PROGPU_NATIVE_COM_CALL SetFillMode(compat::fill_mode value)
        noexcept override
    {
        fill_mode = value;
    }

    void PROGPU_NATIVE_COM_CALL SetSegmentFlags(compat::path_segment value)
        noexcept override
    {
        segment_flags = value;
    }

    void PROGPU_NATIVE_COM_CALL BeginFigure(
        compat::point_2f start,
        compat::figure_begin begin) noexcept override
    {
        first = start;
        figure_begin = begin;
        ++begin_count;
    }

    void PROGPU_NATIVE_COM_CALL AddLines(
        const compat::point_2f* points,
        std::uint32_t point_count) noexcept override
    {
        line_count += point_count;
        if (points != nullptr && point_count != 0U) {
            last = points[point_count - 1U];
        }
    }

    void PROGPU_NATIVE_COM_CALL AddBeziers(
        const compat::bezier_segment* beziers,
        std::uint32_t value_count) noexcept override
    {
        bezier_count += value_count;
        if (beziers != nullptr && value_count != 0U) {
            last = beziers[value_count - 1U].point3;
        }
    }

    void PROGPU_NATIVE_COM_CALL EndFigure(compat::figure_end end)
        noexcept override
    {
        figure_end = end;
        ++end_count;
    }

    com::result PROGPU_NATIVE_COM_CALL Close() noexcept override
    {
        ++close_count;
        return com::ok;
    }

    void PROGPU_NATIVE_COM_CALL AddLine(compat::point_2f point)
        noexcept override
    {
        ++line_count;
        last = point;
    }

    void PROGPU_NATIVE_COM_CALL AddBezier(
        const compat::bezier_segment* bezier) noexcept override
    {
        ++bezier_count;
        if (bezier != nullptr) {
            last = bezier->point3;
        }
    }

    void PROGPU_NATIVE_COM_CALL AddQuadraticBezier(
        const compat::quadratic_bezier_segment* bezier) noexcept override
    {
        ++quadratic_count;
        if (bezier != nullptr) {
            last = bezier->point2;
        }
    }

    void PROGPU_NATIVE_COM_CALL AddQuadraticBeziers(
        const compat::quadratic_bezier_segment* beziers,
        std::uint32_t value_count) noexcept override
    {
        quadratic_count += value_count;
        if (beziers != nullptr && value_count != 0U) {
            last = beziers[value_count - 1U].point2;
        }
    }

    void PROGPU_NATIVE_COM_CALL AddArc(
        const compat::arc_segment* arc) noexcept override
    {
        ++arc_count;
        if (arc != nullptr) {
            last = arc->point;
        }
    }

    compat::fill_mode fill_mode = compat::fill_mode::alternate;
    compat::path_segment segment_flags =
        compat::path_segment::force_unstroked;
    compat::figure_begin figure_begin = compat::figure_begin::hollow;
    compat::figure_end figure_end = compat::figure_end::open;
    compat::point_2f first{};
    compat::point_2f last{};
    std::uint32_t begin_count = 0U;
    std::uint32_t end_count = 0U;
    std::uint32_t line_count = 0U;
    std::uint32_t bezier_count = 0U;
    std::uint32_t quadratic_count = 0U;
    std::uint32_t arc_count = 0U;
    std::uint32_t close_count = 0U;

private:
    friend class com::atomic_reference_count<simplified_sink>;
    ~simplified_sink() = default;
    com::atomic_reference_count<simplified_sink> reference_count_;
};

class triangle_sink final : public compat::tessellation_sink {
public:
    com::result PROGPU_NATIVE_COM_CALL QueryInterface(
        com::guid_ref interface_id,
        void** value) noexcept override
    {
        if (value == nullptr) {
            return com::pointer_error;
        }
        *value = nullptr;
        if (com::guid_equal(interface_id, com::unknown_interface_id())) {
            *value = static_cast<compat::tessellation_sink*>(this);
            AddRef();
            return com::ok;
        }
        return com::no_interface;
    }

    com::reference_count_value PROGPU_NATIVE_COM_CALL AddRef()
        noexcept override
    {
        return reference_count_.add_ref();
    }

    com::reference_count_value PROGPU_NATIVE_COM_CALL Release()
        noexcept override
    {
        return reference_count_.release(this);
    }

    void PROGPU_NATIVE_COM_CALL AddTriangles(
        const compat::triangle* values,
        std::uint32_t triangle_count) noexcept override
    {
        count += triangle_count;
        if (values != nullptr && triangle_count != 0U) {
            first = values[0U];
        }
    }

    com::result PROGPU_NATIVE_COM_CALL Close() noexcept override
    {
        return com::ok;
    }

    compat::triangle first{};
    std::uint32_t count = 0U;

private:
    friend class com::atomic_reference_count<triangle_sink>;
    ~triangle_sink() = default;
    com::atomic_reference_count<triangle_sink> reference_count_;
};

} // namespace

static_assert(sizeof(compat::rectangle_f) == 16U);
static_assert(sizeof(compat::matrix_3x2_f) == 24U);
static_assert(sizeof(compat::geometry_relation) == 4U);
static_assert(sizeof(compat::quadratic_bezier_segment) == 16U);
static_assert(sizeof(compat::arc_segment) == 28U);
static_assert(sizeof(compat::ellipse) == 16U);
static_assert(sizeof(compat::rounded_rectangle) == 24U);
static_assert(sizeof(compat::stroke_style_properties) == 28U);
static_assert(sizeof(compat::drawing_state_description) == 48U);
static_assert(sizeof(compat::color_f) == 16U);
static_assert(sizeof(compat::brush_properties) == 28U);
static_assert(sizeof(compat::gradient_stop) == 20U);
static_assert(sizeof(compat::linear_gradient_brush_properties) == 16U);
static_assert(sizeof(compat::radial_gradient_brush_properties) == 24U);
static_assert(sizeof(compat::pixel_format) == 8U);
static_assert(sizeof(compat::size_u) == 8U);
static_assert(sizeof(compat::point_2u) == 8U);
static_assert(sizeof(compat::rectangle_u) == 16U);
static_assert(sizeof(compat::bitmap_properties) == 16U);
static_assert(sizeof(compat::bitmap_brush_properties) == 12U);
static_assert(sizeof(compat::scene_render_target_properties) == 32U);
static_assert(sizeof(compat::scene_render_target_summary) == 40U);
static_assert(sizeof(compat::scene_submission_diagnostics) == 32U);
static_assert(sizeof(compat::scene_render_options) == 16U);

int main()
{
    if (compat::create_factory(nullptr) != com::pointer_error) {
        return 1;
    }

    com::pointer<compat::factory> factory;
    compat::factory* raw_factory = nullptr;
    if (compat::create_factory(&raw_factory) != com::ok ||
        raw_factory == nullptr) {
        return 2;
    }
    factory.attach(raw_factory);

    com::pointer<com::unknown> identity;
    if (factory.as(com::unknown_interface_id(), identity) != com::ok ||
        identity.get() != static_cast<com::unknown*>(raw_factory)) {
        return 3;
    }
    float dpi_x = 0.0F;
    float dpi_y = 0.0F;
    factory->GetDesktopDpi(&dpi_x, &dpi_y);
    if (!approximately_equal(dpi_x, 96.0F) ||
        !approximately_equal(dpi_y, 96.0F)) {
        return 4;
    }

    const compat::rectangle_f rectangle{1.0F, 2.0F, 5.0F, 8.0F};
    com::pointer<compat::rectangle_geometry> geometry;
    compat::rectangle_geometry* raw_geometry = nullptr;
    if (factory->CreateRectangleGeometry(&rectangle, &raw_geometry) !=
            com::ok ||
        raw_geometry == nullptr) {
        return 5;
    }
    geometry.attach(raw_geometry);

    compat::factory* original_factory = factory.get();
    identity.Reset();
    factory.Reset();

    com::pointer<compat::factory> parent;
    compat::factory* raw_parent = nullptr;
    geometry->GetFactory(&raw_parent);
    parent.attach(raw_parent);
    if (!parent || parent.get() != original_factory) {
        return 6;
    }
    factory = parent;

    com::pointer<compat::resource> resource;
    com::pointer<compat::geometry> geometry_base;
    if (geometry.as(compat::resource_interface_id, resource) != com::ok ||
        geometry.as(compat::geometry_interface_id, geometry_base) != com::ok ||
        resource.get() != static_cast<compat::resource*>(geometry.get()) ||
        geometry_base.get() != static_cast<compat::geometry*>(geometry.get())) {
        return 7;
    }

    compat::rectangle_f returned{};
    geometry->GetRect(&returned);
    if (!approximately_equal(returned.left, 1.0F) ||
        !approximately_equal(returned.bottom, 8.0F)) {
        return 8;
    }
    const compat::matrix_3x2_f transform{
        2.0F, 0.0F, 0.0F, 3.0F, 10.0F, -4.0F};
    if (geometry->GetBounds(&transform, &returned) != com::ok ||
        !approximately_equal(returned.left, 12.0F) ||
        !approximately_equal(returned.top, 2.0F) ||
        !approximately_equal(returned.right, 20.0F) ||
        !approximately_equal(returned.bottom, 20.0F)) {
        return 9;
    }

    std::int32_t contains = 0;
    float area = 0.0F;
    float length = 0.0F;
    if (geometry->FillContainsPoint(
            {16.0F, 10.0F},
            &transform,
            core::default_flattening_tolerance,
            &contains) != com::ok ||
        contains != 1 ||
        geometry->ComputeArea(
            &transform, core::default_flattening_tolerance, &area) !=
            com::ok ||
        geometry->ComputeLength(
            &transform, core::default_flattening_tolerance, &length) !=
            com::ok ||
        !approximately_equal(area, 144.0F) ||
        !approximately_equal(length, 52.0F)) {
        return 10;
    }

    auto* raw_simplified_sink = new simplified_sink();
    com::pointer<compat::simplified_geometry_sink> simplified;
    simplified.attach(raw_simplified_sink);
    if (geometry->Simplify(
            compat::geometry_simplification_option::lines,
            &transform,
            core::default_flattening_tolerance,
            simplified.get()) != com::ok ||
        raw_simplified_sink->fill_mode != compat::fill_mode::winding ||
        raw_simplified_sink->segment_flags != compat::path_segment::none ||
        raw_simplified_sink->begin_count != 1U ||
        raw_simplified_sink->end_count != 1U ||
        raw_simplified_sink->line_count != 3U ||
        raw_simplified_sink->bezier_count != 0U ||
        geometry->Simplify(
            compat::geometry_simplification_option::lines,
            &transform,
            std::numeric_limits<float>::infinity(),
            simplified.get()) != com::invalid_argument ||
        raw_simplified_sink->begin_count != 1U) {
        return 11;
    }

    auto* raw_triangle_sink = new triangle_sink();
    com::pointer<compat::tessellation_sink> triangles;
    triangles.attach(raw_triangle_sink);
    if (geometry->Tessellate(
            &transform,
            core::default_flattening_tolerance,
            triangles.get()) != com::ok ||
        raw_triangle_sink->count != 2U ||
        !approximately_equal(raw_triangle_sink->first.point1.x, 12.0F)) {
        return 12;
    }

    const compat::matrix_3x2_f local_transform{
        1.0F, 0.0F, 0.0F, 1.0F, 5.0F, 7.0F};
    compat::transformed_geometry* raw_transformed = nullptr;
    if (factory->CreateTransformedGeometry(
            geometry_base.get(), &local_transform, &raw_transformed) !=
            com::ok ||
        raw_transformed == nullptr) {
        return 13;
    }
    com::pointer<compat::transformed_geometry> transformed;
    transformed.attach(raw_transformed);
    com::pointer<compat::geometry> transformed_base;
    if (transformed.as(
            compat::geometry_interface_id, transformed_base) != com::ok ||
        !transformed_base) {
        return 14;
    }
    compat::geometry* raw_source = nullptr;
    transformed->GetSourceGeometry(&raw_source);
    com::pointer<compat::geometry> returned_source;
    returned_source.attach(raw_source);
    compat::matrix_3x2_f returned_transform{};
    transformed->GetTransform(&returned_transform);
    if (returned_source.get() != geometry_base.get() ||
        !approximately_equal(returned_transform.m31, 5.0F) ||
        transformed->GetBounds(&transform, &returned) != com::ok ||
        !approximately_equal(returned.left, 22.0F) ||
        !approximately_equal(returned.top, 23.0F) ||
        !approximately_equal(returned.right, 30.0F) ||
        !approximately_equal(returned.bottom, 41.0F)) {
        return 15;
    }

    compat::factory* second_raw_factory = nullptr;
    if (compat::create_factory(&second_raw_factory) != com::ok) {
        return 16;
    }
    com::pointer<compat::factory> second_factory;
    second_factory.attach(second_raw_factory);
    compat::transformed_geometry* wrong_factory_geometry = nullptr;
    if (second_factory->CreateTransformedGeometry(
            geometry_base.get(),
            &local_transform,
            &wrong_factory_geometry) != compat::wrong_factory ||
        wrong_factory_geometry != nullptr) {
        return 17;
    }

    compat::path_geometry* raw_path = nullptr;
    if (factory->CreatePathGeometry(&raw_path) != com::ok ||
        raw_path == nullptr) {
        return 24;
    }
    com::pointer<compat::path_geometry> path;
    path.attach(raw_path);
    com::pointer<compat::resource> path_resource;
    com::pointer<compat::geometry> path_base;
    if (path.as(compat::resource_interface_id, path_resource) != com::ok ||
        path.as(compat::geometry_interface_id, path_base) != com::ok ||
        !path_resource || !path_base) {
        return 25;
    }
    compat::factory* raw_path_factory = nullptr;
    path->GetFactory(&raw_path_factory);
    com::pointer<compat::factory> path_factory;
    path_factory.attach(raw_path_factory);
    if (path_factory.get() != factory.get()) {
        return 26;
    }

    std::uint32_t path_segment_count = 99U;
    std::uint32_t path_figure_count = 99U;
    if (path->GetSegmentCount(&path_segment_count) != compat::wrong_state ||
        path_segment_count != 0U ||
        path->GetFigureCount(&path_figure_count) != compat::wrong_state ||
        path_figure_count != 0U) {
        return 27;
    }
    compat::geometry_sink* raw_path_sink = nullptr;
    if (path->Open(&raw_path_sink) != com::ok || raw_path_sink == nullptr) {
        return 28;
    }
    com::pointer<compat::geometry_sink> path_sink;
    path_sink.attach(raw_path_sink);
    compat::geometry_sink* duplicate_sink =
        reinterpret_cast<compat::geometry_sink*>(
            static_cast<std::uintptr_t>(1U));
    if (path->Open(&duplicate_sink) != compat::wrong_state ||
        duplicate_sink != nullptr) {
        return 29;
    }
    com::pointer<compat::simplified_geometry_sink> path_sink_base;
    if (path_sink.as(
            compat::simplified_geometry_sink_interface_id,
            path_sink_base) != com::ok ||
        !path_sink_base) {
        return 30;
    }

    path_sink->SetFillMode(compat::fill_mode::winding);
    path_sink->SetSegmentFlags(compat::path_segment::none);
    path_sink->BeginFigure({0.0F, 0.0F}, compat::figure_begin::filled);
    path_sink->AddLine({2.0F, 0.0F});
    const compat::bezier_segment cubic{
        {2.0F, 2.0F}, {4.0F, 2.0F}, {4.0F, 0.0F}};
    path_sink->AddBezier(&cubic);
    path_sink->SetSegmentFlags(
        compat::path_segment::force_round_line_join);
    const compat::quadratic_bezier_segment quadratic{
        {5.0F, -2.0F}, {6.0F, 0.0F}};
    path_sink->AddQuadraticBezier(&quadratic);
    path_sink->EndFigure(compat::figure_end::closed);
    if (path_sink->Close() != com::ok ||
        path_sink->Close() != compat::wrong_state) {
        return 31;
    }
    path_sink_base.Reset();
    path_sink.Reset();

    if (path->GetSegmentCount(&path_segment_count) != com::ok ||
        path_segment_count != 4U ||
        path->GetFigureCount(&path_figure_count) != com::ok ||
        path_figure_count != 1U) {
        return 32;
    }
    compat::rectangle_f path_bounds{};
    if (path->GetBounds(&transform, &path_bounds) != com::ok ||
        !approximately_equal(path_bounds.left, 10.0F) ||
        !approximately_equal(path_bounds.top, -7.0F) ||
        !approximately_equal(path_bounds.right, 22.0F) ||
        !approximately_equal(path_bounds.bottom, 0.5F)) {
        return 33;
    }

    auto* raw_path_stream = new simplified_sink();
    com::pointer<compat::geometry_sink> path_stream;
    path_stream.attach(raw_path_stream);
    if (path->Stream(path_stream.get()) != com::ok ||
        raw_path_stream->fill_mode != compat::fill_mode::winding ||
        raw_path_stream->begin_count != 1U ||
        raw_path_stream->end_count != 1U ||
        raw_path_stream->line_count != 1U ||
        raw_path_stream->bezier_count != 1U ||
        raw_path_stream->quadratic_count != 1U ||
        raw_path_stream->arc_count != 0U ||
        !approximately_equal(raw_path_stream->last.x, 6.0F)) {
        return 34;
    }

    auto* raw_path_simplified = new simplified_sink();
    com::pointer<compat::simplified_geometry_sink> path_simplified;
    path_simplified.attach(raw_path_simplified);
    if (path->Simplify(
            compat::geometry_simplification_option::cubics_and_lines,
            &transform,
            core::default_flattening_tolerance,
            path_simplified.get()) != com::ok ||
        raw_path_simplified->begin_count != 1U ||
        raw_path_simplified->line_count != 1U ||
        raw_path_simplified->bezier_count != 2U ||
        raw_path_simplified->quadratic_count != 0U ||
        !approximately_equal(raw_path_simplified->first.x, 10.0F) ||
        !approximately_equal(raw_path_simplified->last.x, 22.0F) ||
        !approximately_equal(raw_path_simplified->last.y, -4.0F)) {
        return 35;
    }

    compat::path_geometry* raw_arc_path = nullptr;
    if (factory->CreatePathGeometry(&raw_arc_path) != com::ok ||
        raw_arc_path == nullptr) {
        return 36;
    }
    com::pointer<compat::path_geometry> arc_path;
    arc_path.attach(raw_arc_path);
    compat::geometry_sink* raw_arc_sink = nullptr;
    if (arc_path->Open(&raw_arc_sink) != com::ok ||
        raw_arc_sink == nullptr) {
        return 37;
    }
    com::pointer<compat::geometry_sink> arc_sink;
    arc_sink.attach(raw_arc_sink);
    arc_sink->BeginFigure({0.0F, 0.0F}, compat::figure_begin::filled);
    const compat::arc_segment arc{
        {2.0F, 0.0F},
        {1.0F, 1.0F},
        0.0F,
        compat::sweep_direction::clockwise,
        compat::arc_size::small_value};
    arc_sink->AddArc(&arc);
    arc_sink->EndFigure(compat::figure_end::open);
    if (arc_sink->Close() != com::ok) {
        return 38;
    }
    arc_sink.Reset();
    path_bounds = {1.0F, 1.0F, 1.0F, 1.0F};
    auto* raw_arc_simplified = new simplified_sink();
    com::pointer<compat::simplified_geometry_sink> arc_simplified;
    arc_simplified.attach(raw_arc_simplified);
    if (arc_path->GetBounds(nullptr, &path_bounds) != com::ok ||
        !approximately_equal(path_bounds.left, 0.0F) ||
        !approximately_equal(path_bounds.top, -1.0F) ||
        !approximately_equal(path_bounds.right, 2.0F) ||
        !approximately_equal(path_bounds.bottom, 0.0F) ||
        arc_path->Simplify(
            compat::geometry_simplification_option::cubics_and_lines,
            nullptr,
            core::default_flattening_tolerance,
            arc_simplified.get()) != com::ok ||
        raw_arc_simplified->begin_count != 1U ||
        raw_arc_simplified->end_count != 1U ||
        raw_arc_simplified->bezier_count != 2U ||
        !approximately_equal(raw_arc_simplified->last.x, 2.0F)) {
        return 39;
    }
    auto* raw_arc_stream = new simplified_sink();
    com::pointer<compat::geometry_sink> arc_stream;
    arc_stream.attach(raw_arc_stream);
    if (arc_path->Stream(arc_stream.get()) != com::ok ||
        raw_arc_stream->arc_count != 1U ||
        !approximately_equal(raw_arc_stream->last.x, 2.0F)) {
        return 40;
    }

    const compat::ellipse ellipse_value{{2.0F, 3.0F}, 4.0F, 2.0F};
    compat::ellipse_geometry* raw_ellipse = nullptr;
    if (factory->CreateEllipseGeometry(&ellipse_value, &raw_ellipse) !=
            com::ok ||
        raw_ellipse == nullptr) {
        return 53;
    }
    com::pointer<compat::ellipse_geometry> ellipse;
    ellipse.attach(raw_ellipse);
    com::pointer<compat::geometry> ellipse_base;
    if (ellipse.as(compat::geometry_interface_id, ellipse_base) != com::ok ||
        !ellipse_base) {
        return 54;
    }
    compat::ellipse returned_ellipse{};
    ellipse->GetEllipse(&returned_ellipse);
    if (!approximately_equal(returned_ellipse.point.x, 2.0F) ||
        !approximately_equal(returned_ellipse.radius_y, 2.0F) ||
        ellipse->GetBounds(&transform, &returned) != com::ok ||
        !approximately_equal(returned.left, 6.0F) ||
        !approximately_equal(returned.top, -1.0F) ||
        !approximately_equal(returned.right, 22.0F) ||
        !approximately_equal(returned.bottom, 11.0F)) {
        return 55;
    }
    if (ellipse->FillContainsPoint(
            {14.0F, 5.0F},
            &transform,
            core::default_flattening_tolerance,
            &contains) != com::ok ||
        contains != 1 ||
        ellipse->FillContainsPoint(
            {23.0F, 5.0F},
            &transform,
            core::default_flattening_tolerance,
            &contains) != com::ok ||
        contains != 0) {
        return 56;
    }
    auto* raw_ellipse_simplified = new simplified_sink();
    com::pointer<compat::simplified_geometry_sink> ellipse_simplified;
    ellipse_simplified.attach(raw_ellipse_simplified);
    if (ellipse->Simplify(
            compat::geometry_simplification_option::cubics_and_lines,
            &transform,
            core::default_flattening_tolerance,
            ellipse_simplified.get()) != com::ok ||
        raw_ellipse_simplified->begin_count != 1U ||
        raw_ellipse_simplified->end_count != 1U ||
        raw_ellipse_simplified->bezier_count != 4U ||
        raw_ellipse_simplified->figure_end != compat::figure_end::closed) {
        return 57;
    }

    const compat::rounded_rectangle rounded_rectangle_value{
        {0.0F, 0.0F, 10.0F, 8.0F}, 3.0F, 2.0F};
    compat::rounded_rectangle_geometry* raw_rounded_rectangle = nullptr;
    if (factory->CreateRoundedRectangleGeometry(
            &rounded_rectangle_value, &raw_rounded_rectangle) != com::ok ||
        raw_rounded_rectangle == nullptr) {
        return 67;
    }
    com::pointer<compat::rounded_rectangle_geometry> rounded_rectangle;
    rounded_rectangle.attach(raw_rounded_rectangle);
    com::pointer<compat::geometry> rounded_rectangle_base;
    if (rounded_rectangle.as(
            compat::geometry_interface_id, rounded_rectangle_base) !=
            com::ok ||
        !rounded_rectangle_base) {
        return 68;
    }
    compat::rounded_rectangle returned_rounded_rectangle{};
    rounded_rectangle->GetRoundedRect(&returned_rounded_rectangle);
    if (!approximately_equal(returned_rounded_rectangle.radius_x, 3.0F) ||
        !approximately_equal(
            returned_rounded_rectangle.rectangle.bottom, 8.0F) ||
        rounded_rectangle->GetBounds(&transform, &returned) != com::ok ||
        !approximately_equal(returned.left, 10.0F) ||
        !approximately_equal(returned.top, -4.0F) ||
        !approximately_equal(returned.right, 30.0F) ||
        !approximately_equal(returned.bottom, 20.0F)) {
        return 69;
    }
    if (rounded_rectangle->FillContainsPoint(
            {20.0F, 8.0F},
            &transform,
            core::default_flattening_tolerance,
            &contains) != com::ok ||
        contains != 1 ||
        rounded_rectangle->FillContainsPoint(
            {10.2F, -3.7F},
            &transform,
            core::default_flattening_tolerance,
            &contains) != com::ok ||
        contains != 0) {
        return 70;
    }
    auto* raw_rounded_simplified = new simplified_sink();
    com::pointer<compat::simplified_geometry_sink> rounded_simplified;
    rounded_simplified.attach(raw_rounded_simplified);
    if (rounded_rectangle->Simplify(
            compat::geometry_simplification_option::cubics_and_lines,
            &transform,
            core::default_flattening_tolerance,
            rounded_simplified.get()) != com::ok ||
        raw_rounded_simplified->begin_count != 1U ||
        raw_rounded_simplified->end_count != 1U ||
        raw_rounded_simplified->line_count != 4U ||
        raw_rounded_simplified->bezier_count != 4U ||
        raw_rounded_simplified->figure_end != compat::figure_end::closed) {
        return 71;
    }
    compat::rounded_rectangle invalid_rounded_rectangle =
        rounded_rectangle_value;
    invalid_rounded_rectangle.radius_x = -1.0F;
    raw_rounded_rectangle = reinterpret_cast<
        compat::rounded_rectangle_geometry*>(static_cast<std::uintptr_t>(1U));
    if (factory->CreateRoundedRectangleGeometry(
            &invalid_rounded_rectangle, &raw_rounded_rectangle) !=
            com::invalid_argument ||
        raw_rounded_rectangle != nullptr ||
        factory->CreateRoundedRectangleGeometry(nullptr, nullptr) !=
            com::pointer_error) {
        return 72;
    }

    std::array<compat::geometry*, 2U> group_sources{
        geometry_base.get(), ellipse_base.get()};
    compat::geometry_group* raw_group = nullptr;
    if (factory->CreateGeometryGroup(
            compat::fill_mode::alternate,
            group_sources.data(),
            static_cast<std::uint32_t>(group_sources.size()),
            &raw_group) != com::ok ||
        raw_group == nullptr) {
        return 77;
    }
    com::pointer<compat::geometry_group> group;
    group.attach(raw_group);
    com::pointer<compat::geometry> group_base;
    if (group.as(compat::geometry_interface_id, group_base) != com::ok ||
        !group_base ||
        group->GetFillMode() != compat::fill_mode::alternate ||
        group->GetSourceGeometryCount() != group_sources.size() ||
        group->GetBounds(&transform, &returned) != com::ok ||
        !approximately_equal(returned.left, 6.0F) ||
        !approximately_equal(returned.top, -1.0F) ||
        !approximately_equal(returned.right, 22.0F) ||
        !approximately_equal(returned.bottom, 20.0F)) {
        return 78;
    }
    std::array<compat::geometry*, 2U> returned_group_sources{};
    group->GetSourceGeometries(
        returned_group_sources.data(),
        static_cast<std::uint32_t>(returned_group_sources.size()));
    com::pointer<compat::geometry> returned_group_rectangle;
    com::pointer<compat::geometry> returned_group_ellipse;
    returned_group_rectangle.attach(returned_group_sources[0U]);
    returned_group_ellipse.attach(returned_group_sources[1U]);
    if (returned_group_rectangle.get() != geometry_base.get() ||
        returned_group_ellipse.get() != ellipse_base.get()) {
        return 79;
    }
    auto* raw_group_simplified = new simplified_sink();
    com::pointer<compat::simplified_geometry_sink> group_simplified;
    group_simplified.attach(raw_group_simplified);
    if (group->Simplify(
            compat::geometry_simplification_option::cubics_and_lines,
            &transform,
            core::default_flattening_tolerance,
            group_simplified.get()) != com::ok ||
        raw_group_simplified->fill_mode != compat::fill_mode::alternate ||
        raw_group_simplified->begin_count != 2U ||
        raw_group_simplified->end_count != 2U ||
        raw_group_simplified->line_count != 3U ||
        raw_group_simplified->bezier_count != 4U) {
        return 80;
    }
    contains = 1;
    if (group->FillContainsPoint(
            {16.0F, 10.0F},
            &transform,
            core::default_flattening_tolerance,
            &contains) != compat::not_implemented ||
        contains != 0) {
        return 87;
    }
    std::array<compat::geometry*, 1U> nested_group_source{group_base.get()};
    raw_group = reinterpret_cast<compat::geometry_group*>(
        static_cast<std::uintptr_t>(1U));
    if (factory->CreateGeometryGroup(
            compat::fill_mode::winding,
            nested_group_source.data(),
            1U,
            &raw_group) != compat::not_implemented ||
        raw_group != nullptr) {
        return 81;
    }
    raw_group = reinterpret_cast<compat::geometry_group*>(
        static_cast<std::uintptr_t>(1U));
    if (factory->CreateGeometryGroup(
            static_cast<compat::fill_mode>(99U),
            nullptr,
            0U,
            &raw_group) != com::invalid_argument ||
        raw_group != nullptr) {
        return 88;
    }
    raw_group = reinterpret_cast<compat::geometry_group*>(
        static_cast<std::uintptr_t>(1U));
    if (factory->CreateGeometryGroup(
            compat::fill_mode::winding,
            nullptr,
            1U,
            &raw_group) != com::invalid_argument ||
        raw_group != nullptr) {
        return 89;
    }
    raw_group = reinterpret_cast<compat::geometry_group*>(
        static_cast<std::uintptr_t>(1U));
    if (second_factory->CreateGeometryGroup(
            compat::fill_mode::winding,
            group_sources.data(),
            static_cast<std::uint32_t>(group_sources.size()),
            &raw_group) != compat::wrong_factory ||
        raw_group != nullptr) {
        return 90;
    }

    const compat::stroke_style_properties stroke_properties{
        compat::cap_style::round,
        compat::cap_style::square,
        compat::cap_style::triangle,
        compat::line_join::bevel,
        4.0F,
        compat::dash_style::custom,
        0.5F};
    const std::array<float, 4U> stroke_dashes{2.0F, 1.0F, 0.5F, 1.0F};
    compat::stroke_style* raw_stroke_style = nullptr;
    if (factory->CreateStrokeStyle(
            &stroke_properties,
            stroke_dashes.data(),
            static_cast<std::uint32_t>(stroke_dashes.size()),
            &raw_stroke_style) != com::ok ||
        raw_stroke_style == nullptr) {
        return 91;
    }
    com::pointer<compat::stroke_style> stroke_style;
    stroke_style.attach(raw_stroke_style);
    com::pointer<compat::resource> stroke_resource;
    if (stroke_style.as(
            compat::resource_interface_id, stroke_resource) != com::ok ||
        !stroke_resource ||
        stroke_style->GetStartCap() != compat::cap_style::round ||
        stroke_style->GetEndCap() != compat::cap_style::square ||
        stroke_style->GetDashCap() != compat::cap_style::triangle ||
        stroke_style->GetLineJoin() != compat::line_join::bevel ||
        !approximately_equal(stroke_style->GetMiterLimit(), 4.0F) ||
        !approximately_equal(stroke_style->GetDashOffset(), 0.5F) ||
        stroke_style->GetDashStyle() != compat::dash_style::custom ||
        stroke_style->GetDashesCount() !=
            static_cast<std::uint32_t>(stroke_dashes.size())) {
        return 92;
    }
    std::array<float, 4U> returned_stroke_dashes{};
    stroke_style->GetDashes(
        returned_stroke_dashes.data(),
        static_cast<std::uint32_t>(returned_stroke_dashes.size()));
    if (!approximately_equal(returned_stroke_dashes[0U], 2.0F) ||
        !approximately_equal(returned_stroke_dashes[1U], 1.0F) ||
        !approximately_equal(returned_stroke_dashes[2U], 0.5F) ||
        !approximately_equal(returned_stroke_dashes[3U], 1.0F)) {
        return 93;
    }
    raw_stroke_style = reinterpret_cast<compat::stroke_style*>(
        static_cast<std::uintptr_t>(1U));
    if (factory->CreateStrokeStyle(
            &stroke_properties, nullptr, 0U, &raw_stroke_style) !=
            com::invalid_argument ||
        raw_stroke_style != nullptr) {
        return 94;
    }
    compat::stroke_style_properties solid_stroke_properties =
        stroke_properties;
    solid_stroke_properties.dash = compat::dash_style::solid;
    raw_stroke_style = reinterpret_cast<compat::stroke_style*>(
        static_cast<std::uintptr_t>(1U));
    if (factory->CreateStrokeStyle(
            &solid_stroke_properties,
            stroke_dashes.data(),
            static_cast<std::uint32_t>(stroke_dashes.size()),
            &raw_stroke_style) != com::invalid_argument ||
        raw_stroke_style != nullptr) {
        return 95;
    }

    const compat::drawing_state_description drawing_state_description{
        compat::antialias_mode::aliased,
        compat::text_antialias_mode::grayscale,
        17U,
        23U,
        {1.0F, 0.25F, -0.5F, 2.0F, 3.0F, -4.0F}};
    compat::drawing_state_block* raw_drawing_state = nullptr;
    if (factory->CreateDrawingStateBlock(
            &drawing_state_description,
            static_cast<com::unknown*>(factory.get()),
            &raw_drawing_state) != com::ok ||
        raw_drawing_state == nullptr) {
        return 100;
    }
    com::pointer<compat::drawing_state_block> drawing_state;
    drawing_state.attach(raw_drawing_state);
    com::pointer<compat::resource> drawing_state_resource;
    if (drawing_state.as(
            compat::resource_interface_id, drawing_state_resource) !=
            com::ok ||
        !drawing_state_resource) {
        return 101;
    }
    compat::drawing_state_description returned_drawing_state{};
    drawing_state->GetDescription(&returned_drawing_state);
    com::unknown* raw_text_parameters = nullptr;
    drawing_state->GetTextRenderingParams(&raw_text_parameters);
    com::pointer<com::unknown> returned_text_parameters;
    returned_text_parameters.attach(raw_text_parameters);
    if (returned_drawing_state.antialias !=
            compat::antialias_mode::aliased ||
        returned_drawing_state.text_antialias !=
            compat::text_antialias_mode::grayscale ||
        returned_drawing_state.tag1 != 17U ||
        returned_drawing_state.tag2 != 23U ||
        !approximately_equal(returned_drawing_state.transform.m12, 0.25F) ||
        returned_text_parameters.get() !=
            static_cast<com::unknown*>(factory.get())) {
        return 102;
    }
    compat::drawing_state_description changed_drawing_state =
        drawing_state_description;
    changed_drawing_state.tag1 = 31U;
    changed_drawing_state.transform.m31 = 9.0F;
    drawing_state->SetDescription(&changed_drawing_state);
    drawing_state->SetTextRenderingParams(nullptr);
    returned_drawing_state = {};
    raw_text_parameters = reinterpret_cast<com::unknown*>(
        static_cast<std::uintptr_t>(1U));
    drawing_state->GetDescription(&returned_drawing_state);
    drawing_state->GetTextRenderingParams(&raw_text_parameters);
    if (returned_drawing_state.tag1 != 31U ||
        !approximately_equal(returned_drawing_state.transform.m31, 9.0F) ||
        raw_text_parameters != nullptr) {
        return 103;
    }
    compat::drawing_state_description invalid_drawing_state =
        drawing_state_description;
    invalid_drawing_state.transform.m11 =
        std::numeric_limits<float>::infinity();
    raw_drawing_state = reinterpret_cast<compat::drawing_state_block*>(
        static_cast<std::uintptr_t>(1U));
    if (factory->CreateDrawingStateBlock(
            &invalid_drawing_state, nullptr, &raw_drawing_state) !=
            com::invalid_argument ||
        raw_drawing_state != nullptr) {
        return 104;
    }
    raw_drawing_state = nullptr;
    if (factory->CreateDrawingStateBlock(
            nullptr, nullptr, &raw_drawing_state) != com::ok ||
        raw_drawing_state == nullptr) {
        return 105;
    }
    com::pointer<compat::drawing_state_block> default_drawing_state;
    default_drawing_state.attach(raw_drawing_state);
    returned_drawing_state = {};
    default_drawing_state->GetDescription(&returned_drawing_state);
    if (returned_drawing_state.antialias !=
            compat::antialias_mode::per_primitive ||
        returned_drawing_state.text_antialias !=
            compat::text_antialias_mode::default_value ||
        returned_drawing_state.tag1 != 0U ||
        returned_drawing_state.tag2 != 0U ||
        !approximately_equal(returned_drawing_state.transform.m11, 1.0F) ||
        !approximately_equal(returned_drawing_state.transform.m22, 1.0F)) {
        return 106;
    }

    com::pointer<compat::factory_native> resource_factory;
    if (factory.as(
            compat::factory_native_interface_id, resource_factory) !=
            com::ok ||
        !resource_factory) {
        return 109;
    }
    const compat::color_f brush_color{0.25F, 0.5F, 0.75F, 1.0F};
    const compat::brush_properties brush_properties{
        0.625F,
        {1.0F, 0.25F, -0.5F, 2.0F, 3.0F, -4.0F}};
    compat::solid_color_brush* raw_brush = nullptr;
    if (resource_factory->CreateSolidColorBrush(
            &brush_color, &brush_properties, &raw_brush) != com::ok ||
        raw_brush == nullptr) {
        return 110;
    }
    com::pointer<compat::solid_color_brush> solid_brush;
    solid_brush.attach(raw_brush);
    com::pointer<compat::resource> brush_resource;
    com::pointer<compat::brush> brush_base;
    if (solid_brush.as(
            compat::resource_interface_id, brush_resource) != com::ok ||
        solid_brush.as(compat::brush_interface_id, brush_base) != com::ok ||
        !brush_resource || !brush_base) {
        return 111;
    }
    compat::factory* raw_brush_factory = nullptr;
    solid_brush->GetFactory(&raw_brush_factory);
    com::pointer<compat::factory> brush_factory;
    brush_factory.attach(raw_brush_factory);
    compat::matrix_3x2_f returned_brush_transform{};
    solid_brush->GetTransform(&returned_brush_transform);
    const compat::color_f returned_brush_color = solid_brush->GetColor();
    if (brush_factory.get() != factory.get() ||
        !approximately_equal(solid_brush->GetOpacity(), 0.625F) ||
        !approximately_equal(returned_brush_transform.m12, 0.25F) ||
        !approximately_equal(returned_brush_transform.m31, 3.0F) ||
        !approximately_equal(returned_brush_color.red, 0.25F) ||
        !approximately_equal(returned_brush_color.blue, 0.75F)) {
        return 112;
    }
    const compat::color_f changed_brush_color{1.0F, 0.0F, 0.5F, 0.75F};
    const compat::matrix_3x2_f changed_brush_transform{
        2.0F, 0.0F, 0.0F, 3.0F, -1.0F, 5.0F};
    solid_brush->SetColor(&changed_brush_color);
    solid_brush->SetOpacity(0.5F);
    solid_brush->SetTransform(&changed_brush_transform);
    const compat::color_f changed_returned_brush_color =
        solid_brush->GetColor();
    returned_brush_transform = {};
    solid_brush->GetTransform(&returned_brush_transform);
    if (!approximately_equal(changed_returned_brush_color.red, 1.0F) ||
        !approximately_equal(changed_returned_brush_color.alpha, 0.75F) ||
        !approximately_equal(solid_brush->GetOpacity(), 0.5F) ||
        !approximately_equal(returned_brush_transform.m22, 3.0F) ||
        !approximately_equal(returned_brush_transform.m32, 5.0F)) {
        return 113;
    }
    compat::color_f invalid_brush_color = changed_brush_color;
    invalid_brush_color.green = std::numeric_limits<float>::infinity();
    compat::matrix_3x2_f invalid_brush_transform = changed_brush_transform;
    invalid_brush_transform.m11 =
        std::numeric_limits<float>::quiet_NaN();
    solid_brush->SetColor(&invalid_brush_color);
    solid_brush->SetOpacity(-1.0F);
    solid_brush->SetTransform(&invalid_brush_transform);
    returned_brush_transform = {};
    solid_brush->GetTransform(&returned_brush_transform);
    if (!approximately_equal(solid_brush->GetColor().green, 0.0F) ||
        !approximately_equal(solid_brush->GetOpacity(), 0.5F) ||
        !approximately_equal(returned_brush_transform.m11, 2.0F)) {
        return 114;
    }
    raw_brush = reinterpret_cast<compat::solid_color_brush*>(
        static_cast<std::uintptr_t>(1U));
    if (resource_factory->CreateSolidColorBrush(
            &invalid_brush_color, nullptr, &raw_brush) !=
            com::invalid_argument ||
        raw_brush != nullptr ||
        resource_factory->CreateSolidColorBrush(
            &brush_color, nullptr, nullptr) != com::pointer_error) {
        return 115;
    }

    com::pointer<compat::scene_factory_native> scene_factory;
    if (factory.as(
            compat::scene_factory_native_interface_id, scene_factory) !=
            com::ok ||
        !scene_factory) {
        return 118;
    }
    const compat::scene_render_target_properties target_properties{
        640U, 480U, 96.0F, 96.0F, 7001U, 11U};
    compat::render_target* raw_target = nullptr;
    if (scene_factory->CreateSceneRenderTarget(
            &target_properties, &raw_target) != com::ok ||
        raw_target == nullptr) {
        return 119;
    }
    com::pointer<compat::render_target> target;
    target.attach(raw_target);
    com::pointer<compat::resource> target_resource;
    com::pointer<compat::scene_render_target_native> scene_target;
    if (target.as(compat::resource_interface_id, target_resource) != com::ok ||
        target.as(
            compat::scene_render_target_native_interface_id,
            scene_target) != com::ok ||
        !target_resource || !scene_target) {
        return 120;
    }
    const compat::size_u target_pixel_size = target->GetPixelSize();
    const compat::size_f target_size = target->GetSize();
    if (target_pixel_size.width != 640U ||
        target_pixel_size.height != 480U ||
        !approximately_equal(target_size.width, 640.0F) ||
        !approximately_equal(target_size.height, 480.0F)) {
        return 121;
    }
    compat::solid_color_brush* raw_target_brush = nullptr;
    if (target->CreateSolidColorBrush(
            &brush_color, nullptr, &raw_target_brush) != com::ok ||
        raw_target_brush == nullptr) {
        return 122;
    }
    com::pointer<compat::solid_color_brush> target_brush;
    target_brush.attach(raw_target_brush);
    const compat::gradient_stop gradient_stops[]{
        {0.0F, {1.0F, 0.0F, 0.0F, 1.0F}},
        {0.5F, {0.0F, 1.0F, 0.0F, 0.75F}},
        {1.0F, {0.0F, 0.0F, 1.0F, 1.0F}}};
    compat::gradient_stop_collection* raw_gradient_stops = nullptr;
    if (target->CreateGradientStopCollection(
            gradient_stops,
            3U,
            compat::gamma::gamma_2_2,
            compat::extend_mode::mirror,
            &raw_gradient_stops) != com::ok ||
        raw_gradient_stops == nullptr) {
        return 131;
    }
    com::pointer<compat::gradient_stop_collection> gradient_collection;
    gradient_collection.attach(raw_gradient_stops);
    compat::gradient_stop copied_gradient_stops[3]{};
    gradient_collection->GetGradientStops(copied_gradient_stops, 3U);
    if (gradient_collection->GetGradientStopCount() != 3U ||
        gradient_collection->GetColorInterpolationGamma() !=
            compat::gamma::gamma_2_2 ||
        gradient_collection->GetExtendMode() !=
            compat::extend_mode::mirror ||
        !approximately_equal(copied_gradient_stops[1].position, 0.5F) ||
        !approximately_equal(copied_gradient_stops[1].color.alpha, 0.75F)) {
        return 132;
    }
    const compat::linear_gradient_brush_properties linear_properties{
        {2.0F, 3.0F}, {30.0F, 21.0F}};
    compat::linear_gradient_brush* raw_linear_brush = nullptr;
    if (target->CreateLinearGradientBrush(
            &linear_properties,
            nullptr,
            gradient_collection.get(),
            &raw_linear_brush) != com::ok ||
        raw_linear_brush == nullptr) {
        return 133;
    }
    com::pointer<compat::linear_gradient_brush> linear_brush;
    linear_brush.attach(raw_linear_brush);
    linear_brush->SetStartPoint({4.0F, 5.0F});
    linear_brush->SetEndPoint({32.0F, 23.0F});
    linear_brush->SetStartPoint(
        {std::numeric_limits<float>::infinity(), 0.0F});
    if (!approximately_equal(linear_brush->GetStartPoint().x, 4.0F) ||
        !approximately_equal(linear_brush->GetEndPoint().y, 23.0F)) {
        return 134;
    }
    const compat::radial_gradient_brush_properties radial_properties{
        {17.0F, 14.0F}, {-2.0F, 1.0F}, 12.0F, 8.0F};
    const compat::brush_properties radial_brush_properties{
        0.875F, {1.0F, 0.0F, 0.0F, 1.0F, 1.0F, -1.0F}};
    compat::radial_gradient_brush* raw_radial_brush = nullptr;
    if (target->CreateRadialGradientBrush(
            &radial_properties,
            &radial_brush_properties,
            gradient_collection.get(),
            &raw_radial_brush) != com::ok ||
        raw_radial_brush == nullptr) {
        return 135;
    }
    com::pointer<compat::radial_gradient_brush> radial_brush;
    radial_brush.attach(raw_radial_brush);
    radial_brush->SetRadiusX(10.0F);
    radial_brush->SetRadiusY(-1.0F);
    if (!approximately_equal(radial_brush->GetRadiusX(), 10.0F) ||
        !approximately_equal(radial_brush->GetRadiusY(), 8.0F) ||
        !approximately_equal(radial_brush->GetOpacity(), 0.875F)) {
        return 136;
    }
    compat::gradient_stop invalid_gradient_stops[]{
        {0.75F, {1.0F, 0.0F, 0.0F, 1.0F}},
        {0.25F, {0.0F, 0.0F, 1.0F, 1.0F}}};
    raw_gradient_stops = reinterpret_cast<compat::gradient_stop_collection*>(
        static_cast<std::uintptr_t>(1U));
    if (target->CreateGradientStopCollection(
            invalid_gradient_stops,
            2U,
            compat::gamma::gamma_2_2,
            compat::extend_mode::clamp,
            &raw_gradient_stops) != com::invalid_argument ||
        raw_gradient_stops != nullptr) {
        return 137;
    }
    compat::factory* raw_other_factory = nullptr;
    if (compat::create_factory(&raw_other_factory) != com::ok ||
        raw_other_factory == nullptr) {
        return 140;
    }
    com::pointer<compat::factory> other_factory;
    other_factory.attach(raw_other_factory);
    com::pointer<compat::scene_factory_native> other_scene_factory;
    if (other_factory.as(
            compat::scene_factory_native_interface_id,
            other_scene_factory) != com::ok ||
        !other_scene_factory) {
        return 141;
    }
    compat::render_target* raw_other_target = nullptr;
    if (other_scene_factory->CreateSceneRenderTarget(
            &target_properties, &raw_other_target) != com::ok ||
        raw_other_target == nullptr) {
        return 142;
    }
    com::pointer<compat::render_target> other_target;
    other_target.attach(raw_other_target);
    compat::gradient_stop_collection* raw_foreign_stops = nullptr;
    if (other_target->CreateGradientStopCollection(
            gradient_stops,
            3U,
            compat::gamma::gamma_2_2,
            compat::extend_mode::clamp,
            &raw_foreign_stops) != com::ok ||
        raw_foreign_stops == nullptr) {
        return 143;
    }
    com::pointer<compat::gradient_stop_collection> foreign_stops;
    foreign_stops.attach(raw_foreign_stops);
    raw_linear_brush = reinterpret_cast<compat::linear_gradient_brush*>(
        static_cast<std::uintptr_t>(1U));
    if (target->CreateLinearGradientBrush(
            &linear_properties,
            nullptr,
            foreign_stops.get(),
            &raw_linear_brush) != compat::wrong_factory ||
        raw_linear_brush != nullptr) {
        return 144;
    }
    target->BeginDraw();
    const compat::color_f clear_color{0.05F, 0.1F, 0.15F, 1.0F};
    target->Clear(&clear_color);
    target->FillRectangle(
        &rectangle, static_cast<compat::brush*>(target_brush.get()));
    target->DrawLine(
        {0.0F, 0.0F},
        {20.0F, 10.0F},
        static_cast<compat::brush*>(target_brush.get()),
        2.0F,
        nullptr);
    const compat::rounded_rectangle target_rounded_rectangle{
        rounded_rectangle_value.rectangle, 2.0F, 2.0F};
    target->DrawRoundedRectangle(
        &target_rounded_rectangle,
        static_cast<compat::brush*>(target_brush.get()),
        1.5F,
        nullptr);
    target->FillEllipse(
        &ellipse_value, static_cast<compat::brush*>(target_brush.get()));
    target->FillRectangle(
        &rectangle, static_cast<compat::brush*>(linear_brush.get()));
    target->FillEllipse(
        &ellipse_value, static_cast<compat::brush*>(radial_brush.get()));
    if (target->EndDraw(nullptr, nullptr) != com::ok) {
        return 123;
    }
    compat::scene_render_target_summary target_summary{};
    scene_target->GetSummary(&target_summary);
    const std::uint64_t required_scene_size =
        scene_target->GetRequiredSceneSize();
    if (target_summary.scene_id != 7001U ||
        target_summary.generation != 11U ||
        target_summary.draw_count != 6U || target_summary.has_clear != 1 ||
        !approximately_equal(target_summary.clear_color.green, 0.1F) ||
        required_scene_size < sizeof(progpu_native_scene_header)) {
        return 124;
    }
    std::vector<std::byte> scene_bytes(
        static_cast<std::size_t>(required_scene_size));
    std::uint64_t written_scene_size = 0U;
    if (scene_target->BuildScene(
            scene_bytes.data(),
            scene_bytes.size(),
            &written_scene_size) != com::ok ||
        written_scene_size != required_scene_size) {
        return 125;
    }
    const auto* scene_header = reinterpret_cast<
        const progpu_native_scene_header*>(scene_bytes.data());
    if (scene_header->scene_id != 7001U ||
        scene_header->generation != 11U ||
        scene_header->command_count != 6U ||
        scene_header->total_size != written_scene_size) {
        return 126;
    }
    const auto* scene_resources = reinterpret_cast<
        const progpu_native_scene_resource*>(
        scene_bytes.data() + scene_header->resource_offset);
    const progpu_native_scene_resource* brush_table = nullptr;
    for (std::uint32_t index = 0U;
         index < scene_header->resource_count;
         ++index) {
        const auto* scene_resource = reinterpret_cast<
            const progpu_native_scene_resource*>(
            reinterpret_cast<const std::byte*>(scene_resources) +
            index * scene_header->resource_stride);
        if (scene_resource->kind ==
            PROGPU_NATIVE_SCENE_RESOURCE_BRUSH_TABLE) {
            brush_table = scene_resource;
            break;
        }
    }
    if (brush_table == nullptr ||
        brush_table->payload_size % sizeof(progpu_native_scene_brush) != 0U ||
        brush_table->auxiliary_size !=
            6U * sizeof(progpu_native_scene_gradient_stop)) {
        return 138;
    }
    const auto* scene_brushes = reinterpret_cast<
        const progpu_native_scene_brush*>(
        scene_bytes.data() + brush_table->payload_offset);
    const std::size_t scene_brush_count = brush_table->payload_size /
        sizeof(progpu_native_scene_brush);
    bool found_linear = false;
    bool found_radial = false;
    for (std::size_t index = 0U; index < scene_brush_count; ++index) {
        found_linear = found_linear ||
            (scene_brushes[index].type ==
                    PROGPU_NATIVE_SCENE_BRUSH_LINEAR_GRADIENT &&
                scene_brushes[index].spread_method ==
                    PROGPU_NATIVE_SCENE_GRADIENT_REFLECT &&
                scene_brushes[index].stop_count == 3U);
        found_radial = found_radial ||
            (scene_brushes[index].type ==
                    PROGPU_NATIVE_SCENE_BRUSH_RADIAL_GRADIENT &&
                approximately_equal(scene_brushes[index].radius, 10.0F) &&
                approximately_equal(scene_brushes[index].radius_y, 8.0F));
    }
    if (!found_linear || !found_radial) {
        return 139;
    }
    target->BeginDraw();
    target->DrawRectangle(
        &rectangle,
        static_cast<compat::brush*>(target_brush.get()),
        0.0F,
        nullptr);
    if (target->EndDraw(nullptr, nullptr) != com::invalid_argument ||
        scene_target->GetRequiredSceneSize() != 0U) {
        return 127;
    }
    target->BeginDraw();
    target->DrawBitmap(
        nullptr,
        nullptr,
        1.0F,
        compat::bitmap_interpolation_mode::linear,
        nullptr);
    if (target->EndDraw(nullptr, nullptr) != com::invalid_argument ||
        scene_target->GetRequiredSceneSize() != 0U) {
        return 130;
    }

    const compat::bitmap_properties bitmap_properties{
        {87U, compat::alpha_mode::premultiplied}, 96.0F, 96.0F};
    const std::byte bitmap_pixels[]{
        std::byte{0x00}, std::byte{0x00}, std::byte{0xff}, std::byte{0xff},
        std::byte{0x00}, std::byte{0xff}, std::byte{0x00}, std::byte{0xff},
        std::byte{0xff}, std::byte{0x00}, std::byte{0x00}, std::byte{0xff},
        std::byte{0xff}, std::byte{0xff}, std::byte{0xff}, std::byte{0xff}};
    compat::bitmap* raw_bitmap = nullptr;
    if (target->CreateBitmap(
            {2U, 2U}, bitmap_pixels, 8U, &bitmap_properties, &raw_bitmap) !=
            com::ok ||
        raw_bitmap == nullptr) {
        return 146;
    }
    com::pointer<compat::bitmap> portable_bitmap;
    portable_bitmap.attach(raw_bitmap);
    const compat::size_u bitmap_pixel_size = portable_bitmap->GetPixelSize();
    const compat::size_f bitmap_size = portable_bitmap->GetSize();
    float bitmap_dpi_x = 0.0F;
    float bitmap_dpi_y = 0.0F;
    portable_bitmap->GetDpi(&bitmap_dpi_x, &bitmap_dpi_y);
    if (bitmap_pixel_size.width != 2U || bitmap_pixel_size.height != 2U ||
        !approximately_equal(bitmap_size.width, 2.0F) ||
        !approximately_equal(bitmap_size.height, 2.0F) ||
        !approximately_equal(bitmap_dpi_x, 96.0F) ||
        !approximately_equal(bitmap_dpi_y, 96.0F) ||
        portable_bitmap->GetPixelFormat().format != 87U) {
        return 147;
    }
    const std::byte replacement[]{
        std::byte{0x30}, std::byte{0x20}, std::byte{0x10}, std::byte{0xff}};
    const compat::rectangle_u replacement_rectangle{1U, 0U, 2U, 1U};
    if (portable_bitmap->CopyFromMemory(
            &replacement_rectangle, replacement, 4U) != com::ok ||
        portable_bitmap->CopyFromRenderTarget(
            nullptr, target.get(), nullptr) != compat::not_implemented) {
        return 148;
    }
    compat::bitmap* raw_bitmap_copy = nullptr;
    if (target->CreateBitmap(
            {2U, 2U}, nullptr, 0U, &bitmap_properties, &raw_bitmap_copy) !=
            com::ok ||
        raw_bitmap_copy == nullptr) {
        return 154;
    }
    com::pointer<compat::bitmap> bitmap_copy;
    bitmap_copy.attach(raw_bitmap_copy);
    if (bitmap_copy->CopyFromBitmap(
            nullptr, portable_bitmap.get(), nullptr) != com::ok) {
        return 155;
    }
    target->BeginDraw();
    const compat::rectangle_f first_bitmap_destination{
        2.0F, 3.0F, 18.0F, 19.0F};
    target->DrawBitmap(
        bitmap_copy.get(),
        &first_bitmap_destination,
        0.75F,
        compat::bitmap_interpolation_mode::nearest_neighbor,
        nullptr);
    const compat::rectangle_f second_bitmap_destination{
        20.0F, 3.0F, 36.0F, 19.0F};
    target->DrawBitmap(
        bitmap_copy.get(),
        &second_bitmap_destination,
        1.0F,
        compat::bitmap_interpolation_mode::linear,
        nullptr);
    if (target->EndDraw(nullptr, nullptr) != com::ok ||
        scene_target->GetRequiredSceneSize() == 0U) {
        return 149;
    }
    const std::uint64_t bitmap_scene_size =
        scene_target->GetRequiredSceneSize();
    std::vector<std::byte> bitmap_scene(
        static_cast<std::size_t>(bitmap_scene_size));
    std::uint64_t bitmap_scene_written = 0U;
    if (scene_target->BuildScene(
            bitmap_scene.data(),
            bitmap_scene.size(),
            &bitmap_scene_written) != com::ok ||
        bitmap_scene_written != bitmap_scene_size) {
        return 150;
    }
    const auto* bitmap_header = reinterpret_cast<
        const progpu_native_scene_header*>(bitmap_scene.data());
    std::uint32_t bitmap_resource_count = 0U;
    const progpu_native_scene_resource* image_resource = nullptr;
    for (std::uint32_t index = 0U;
         index < bitmap_header->resource_count;
         ++index) {
        const auto* candidate_resource = reinterpret_cast<
            const progpu_native_scene_resource*>(
            bitmap_scene.data() + bitmap_header->resource_offset +
            index * bitmap_header->resource_stride);
        if (candidate_resource->kind == PROGPU_NATIVE_SCENE_RESOURCE_IMAGE) {
            image_resource = candidate_resource;
            ++bitmap_resource_count;
        }
    }
    if (bitmap_header->command_count != 2U || bitmap_resource_count != 1U ||
        image_resource == nullptr ||
        (image_resource->flags & PROGPU_NATIVE_SCENE_IMAGE_BGRA8) == 0U ||
        image_resource->payload_size != sizeof(bitmap_pixels)) {
        return 151;
    }
    const auto* serialized_pixels =
        bitmap_scene.data() + image_resource->payload_offset;
    if (serialized_pixels[4] != replacement[0] ||
        serialized_pixels[5] != replacement[1] ||
        serialized_pixels[6] != replacement[2] ||
        serialized_pixels[7] != replacement[3]) {
        return 152;
    }

    const compat::bitmap_brush_properties bitmap_brush_properties{
        compat::extend_mode::wrap,
        compat::extend_mode::mirror,
        compat::bitmap_interpolation_mode::nearest_neighbor};
    const compat::brush_properties bitmap_brush_base_properties{
        0.625F,
        {1.0F, 0.0F, 0.0F, 1.0F, 1.0F, 2.0F}};
    compat::bitmap_brush* raw_bitmap_brush = nullptr;
    if (target->CreateBitmapBrush(
            portable_bitmap.get(),
            &bitmap_brush_properties,
            &bitmap_brush_base_properties,
            &raw_bitmap_brush) != com::ok ||
        raw_bitmap_brush == nullptr) {
        return 156;
    }
    com::pointer<compat::bitmap_brush> bitmap_brush;
    bitmap_brush.attach(raw_bitmap_brush);
    com::pointer<compat::brush> bitmap_brush_base;
    compat::bitmap* returned_bitmap = nullptr;
    compat::matrix_3x2_f returned_bitmap_brush_transform{};
    bitmap_brush->GetBitmap(&returned_bitmap);
    bitmap_brush->GetTransform(&returned_bitmap_brush_transform);
    const bool bitmap_brush_identity_matches = returned_bitmap ==
        portable_bitmap.get();
    if (returned_bitmap != nullptr) {
        returned_bitmap->Release();
    }
    if (bitmap_brush.as(
            compat::brush_interface_id, bitmap_brush_base) != com::ok ||
        !bitmap_brush_base || !bitmap_brush_identity_matches ||
        bitmap_brush->GetExtendModeX() != compat::extend_mode::wrap ||
        bitmap_brush->GetExtendModeY() != compat::extend_mode::mirror ||
        bitmap_brush->GetInterpolationMode() !=
            compat::bitmap_interpolation_mode::nearest_neighbor ||
        !approximately_equal(bitmap_brush->GetOpacity(), 0.625F) ||
        !approximately_equal(returned_bitmap_brush_transform.m31, 1.0F) ||
        !approximately_equal(returned_bitmap_brush_transform.m32, 2.0F)) {
        return 157;
    }
    bitmap_brush->SetExtendModeX(compat::extend_mode::clamp);
    bitmap_brush->SetExtendModeY(compat::extend_mode::wrap);
    bitmap_brush->SetInterpolationMode(
        compat::bitmap_interpolation_mode::linear);
    bitmap_brush->SetOpacity(0.75F);
    bitmap_brush->SetExtendModeX(static_cast<compat::extend_mode>(99U));
    bitmap_brush->SetInterpolationMode(
        static_cast<compat::bitmap_interpolation_mode>(99U));
    if (bitmap_brush->GetExtendModeX() != compat::extend_mode::clamp ||
        bitmap_brush->GetExtendModeY() != compat::extend_mode::wrap ||
        bitmap_brush->GetInterpolationMode() !=
            compat::bitmap_interpolation_mode::linear ||
        !approximately_equal(bitmap_brush->GetOpacity(), 0.75F)) {
        return 158;
    }
    compat::bitmap* raw_foreign_bitmap = nullptr;
    if (other_target->CreateBitmap(
            {2U, 2U}, bitmap_pixels, 8U, &bitmap_properties,
            &raw_foreign_bitmap) != com::ok ||
        raw_foreign_bitmap == nullptr) {
        return 159;
    }
    com::pointer<compat::bitmap> foreign_bitmap;
    foreign_bitmap.attach(raw_foreign_bitmap);
    raw_bitmap_brush = reinterpret_cast<compat::bitmap_brush*>(
        static_cast<std::uintptr_t>(1U));
    if (target->CreateBitmapBrush(
            foreign_bitmap.get(), nullptr, nullptr, &raw_bitmap_brush) !=
            compat::wrong_factory ||
        raw_bitmap_brush != nullptr) {
        return 160;
    }
    bitmap_brush->SetBitmap(foreign_bitmap.get());
    returned_bitmap = nullptr;
    bitmap_brush->GetBitmap(&returned_bitmap);
    const bool rejected_foreign_bitmap = returned_bitmap ==
        portable_bitmap.get();
    if (returned_bitmap != nullptr) {
        returned_bitmap->Release();
    }
    if (!rejected_foreign_bitmap) {
        return 161;
    }

    target->BeginDraw();
    const compat::rectangle_f bitmap_brush_rectangle{
        4.0F, 5.0F, 20.0F, 17.0F};
    target->FillRectangle(
        &bitmap_brush_rectangle,
        static_cast<compat::brush*>(bitmap_brush.get()));
    if (target->EndDraw(nullptr, nullptr) != com::ok ||
        scene_target->GetRequiredSceneSize() == 0U) {
        return 162;
    }
    const std::uint64_t bitmap_brush_scene_size =
        scene_target->GetRequiredSceneSize();
    std::vector<std::byte> bitmap_brush_scene(
        static_cast<std::size_t>(bitmap_brush_scene_size));
    std::uint64_t bitmap_brush_scene_written = 0U;
    if (scene_target->BuildScene(
            bitmap_brush_scene.data(),
            bitmap_brush_scene.size(),
            &bitmap_brush_scene_written) != com::ok ||
        bitmap_brush_scene_written != bitmap_brush_scene_size) {
        return 163;
    }
    const auto* bitmap_brush_header = reinterpret_cast<
        const progpu_native_scene_header*>(bitmap_brush_scene.data());
    const progpu_native_scene_resource* bitmap_brush_image = nullptr;
    const progpu_native_scene_resource* bitmap_brush_mask = nullptr;
    const progpu_native_scene_resource* bitmap_brush_state = nullptr;
    for (std::uint32_t index = 0U;
         index < bitmap_brush_header->resource_count;
         ++index) {
        const auto* brush_scene_resource = reinterpret_cast<
            const progpu_native_scene_resource*>(
            bitmap_brush_scene.data() +
            bitmap_brush_header->resource_offset +
            index * bitmap_brush_header->resource_stride);
        if (brush_scene_resource->kind ==
            PROGPU_NATIVE_SCENE_RESOURCE_IMAGE) {
            bitmap_brush_image = brush_scene_resource;
        } else if (brush_scene_resource->kind ==
            PROGPU_NATIVE_SCENE_RESOURCE_LAYER_MASK) {
            bitmap_brush_mask = brush_scene_resource;
        } else if (brush_scene_resource->kind ==
            PROGPU_NATIVE_SCENE_RESOURCE_STATE) {
            bitmap_brush_state = brush_scene_resource;
        }
    }
    const auto* bitmap_brush_command = reinterpret_cast<
        const progpu_native_scene_command*>(
        bitmap_brush_scene.data() + bitmap_brush_header->command_offset);
    const auto* bitmap_brush_draw = reinterpret_cast<
        const progpu_native_scene_image_draw*>(
        bitmap_brush_scene.data() + bitmap_brush_command->payload_offset);
    const auto* bitmap_brush_mask_value = bitmap_brush_mask == nullptr
        ? nullptr
        : reinterpret_cast<const progpu_native_scene_layer_geometry_mask*>(
            bitmap_brush_scene.data() + bitmap_brush_mask->payload_offset);
    if (bitmap_brush_header->command_count != 1U ||
        bitmap_brush_image == nullptr || bitmap_brush_mask == nullptr ||
        bitmap_brush_state == nullptr || bitmap_brush_command->kind !=
            PROGPU_NATIVE_SCENE_COMMAND_DRAW_IMAGE ||
        bitmap_brush_mask_value == nullptr ||
        bitmap_brush_mask_value->kind !=
            PROGPU_NATIVE_SCENE_LAYER_MASK_GEOMETRY ||
        bitmap_brush_mask_value->primitive_count != 1U ||
        bitmap_brush_draw->sampling !=
            PROGPU_NATIVE_IMAGE_SAMPLING_LINEAR ||
        (bitmap_brush_draw->flags &
            PROGPU_NATIVE_SCENE_IMAGE_EXTENDED_SOURCE_RECT) == 0U ||
        ((bitmap_brush_draw->flags &
                PROGPU_NATIVE_SCENE_IMAGE_ADDRESS_U_MASK) >>
            PROGPU_NATIVE_SCENE_IMAGE_ADDRESS_U_SHIFT) !=
            PROGPU_NATIVE_IMAGE_ADDRESS_CLAMP ||
        ((bitmap_brush_draw->flags &
                PROGPU_NATIVE_SCENE_IMAGE_ADDRESS_V_MASK) >>
            PROGPU_NATIVE_SCENE_IMAGE_ADDRESS_V_SHIFT) !=
            PROGPU_NATIVE_IMAGE_ADDRESS_REPEAT ||
        !approximately_equal(bitmap_brush_draw->opacity, 0.75F) ||
        !approximately_equal(bitmap_brush_draw->transform.m31, 1.0F) ||
        !approximately_equal(bitmap_brush_draw->transform.m32, 2.0F)) {
        return 164;
    }

    compat::render_target* unsupported =
        reinterpret_cast<compat::render_target*>(
        static_cast<std::uintptr_t>(1U));
    if (factory->CreateWicBitmapRenderTarget(
            nullptr, nullptr, &unsupported) !=
            compat::not_implemented ||
        unsupported != nullptr ||
        factory->CreateWicBitmapRenderTarget(
            nullptr, nullptr, nullptr) !=
            com::pointer_error) {
        return 18;
    }

#if defined(_WIN32)
    if (!com::guid_equal(
            compat::factory_interface_id, __uuidof(ID2D1Factory)) ||
        !com::guid_equal(
            compat::rectangle_geometry_interface_id,
            __uuidof(ID2D1RectangleGeometry)) ||
        !com::guid_equal(
            compat::ellipse_geometry_interface_id,
            __uuidof(ID2D1EllipseGeometry)) ||
        !com::guid_equal(
            compat::rounded_rectangle_geometry_interface_id,
            __uuidof(ID2D1RoundedRectangleGeometry)) ||
        !com::guid_equal(
            compat::geometry_group_interface_id,
            __uuidof(ID2D1GeometryGroup)) ||
        !com::guid_equal(
            compat::stroke_style_interface_id,
            __uuidof(ID2D1StrokeStyle)) ||
        !com::guid_equal(
            compat::drawing_state_block_interface_id,
            __uuidof(ID2D1DrawingStateBlock)) ||
        !com::guid_equal(
            compat::brush_interface_id, __uuidof(ID2D1Brush)) ||
        !com::guid_equal(
            compat::solid_color_brush_interface_id,
            __uuidof(ID2D1SolidColorBrush)) ||
        !com::guid_equal(
            compat::bitmap_interface_id, __uuidof(ID2D1Bitmap)) ||
        !com::guid_equal(
            compat::bitmap_brush_interface_id, __uuidof(ID2D1BitmapBrush)) ||
        !com::guid_equal(
            compat::gradient_stop_collection_interface_id,
            __uuidof(ID2D1GradientStopCollection)) ||
        !com::guid_equal(
            compat::linear_gradient_brush_interface_id,
            __uuidof(ID2D1LinearGradientBrush)) ||
        !com::guid_equal(
            compat::radial_gradient_brush_interface_id,
            __uuidof(ID2D1RadialGradientBrush)) ||
        !com::guid_equal(
            compat::render_target_interface_id,
            __uuidof(ID2D1RenderTarget)) ||
        !com::guid_equal(
            compat::transformed_geometry_interface_id,
            __uuidof(ID2D1TransformedGeometry)) ||
        !com::guid_equal(
            compat::path_geometry_interface_id,
            __uuidof(ID2D1PathGeometry)) ||
        !com::guid_equal(
            compat::simplified_geometry_sink_interface_id,
            __uuidof(ID2D1SimplifiedGeometrySink)) ||
        !com::guid_equal(
            compat::geometry_sink_interface_id,
            __uuidof(ID2D1GeometrySink)) ||
        sizeof(compat::rectangle_f) != sizeof(D2D1_RECT_F) ||
        sizeof(compat::ellipse) != sizeof(D2D1_ELLIPSE) ||
        sizeof(compat::rounded_rectangle) != sizeof(D2D1_ROUNDED_RECT) ||
        sizeof(compat::stroke_style_properties) !=
            sizeof(D2D1_STROKE_STYLE_PROPERTIES) ||
        sizeof(compat::drawing_state_description) !=
            sizeof(D2D1_DRAWING_STATE_DESCRIPTION) ||
        sizeof(compat::color_f) != sizeof(D2D1_COLOR_F) ||
        sizeof(compat::brush_properties) != sizeof(D2D1_BRUSH_PROPERTIES) ||
        sizeof(compat::gradient_stop) != sizeof(D2D1_GRADIENT_STOP) ||
        sizeof(compat::linear_gradient_brush_properties) !=
            sizeof(D2D1_LINEAR_GRADIENT_BRUSH_PROPERTIES) ||
        sizeof(compat::radial_gradient_brush_properties) !=
            sizeof(D2D1_RADIAL_GRADIENT_BRUSH_PROPERTIES) ||
        sizeof(compat::pixel_format) != sizeof(D2D1_PIXEL_FORMAT) ||
        sizeof(compat::size_u) != sizeof(D2D1_SIZE_U) ||
        sizeof(compat::point_2u) != sizeof(D2D1_POINT_2U) ||
        sizeof(compat::rectangle_u) != sizeof(D2D1_RECT_U) ||
        sizeof(compat::bitmap_properties) != sizeof(D2D1_BITMAP_PROPERTIES) ||
        sizeof(compat::bitmap_brush_properties) !=
            sizeof(D2D1_BITMAP_BRUSH_PROPERTIES) ||
        sizeof(compat::triangle) != sizeof(D2D1_TRIANGLE) ||
        sizeof(compat::quadratic_bezier_segment) !=
            sizeof(D2D1_QUADRATIC_BEZIER_SEGMENT) ||
        sizeof(compat::arc_segment) != sizeof(D2D1_ARC_SEGMENT)) {
        return 19;
    }
    auto* native_solid_brush =
        reinterpret_cast<ID2D1SolidColorBrush*>(solid_brush.get());
    ID2D1Brush* native_brush_base = nullptr;
    if (FAILED(native_solid_brush->QueryInterface(
            __uuidof(ID2D1Brush),
            reinterpret_cast<void**>(&native_brush_base))) ||
        native_brush_base == nullptr) {
        return 116;
    }
    const D2D1_COLOR_F native_portable_brush_color =
        native_solid_brush->GetColor();
    const FLOAT native_portable_brush_opacity =
        native_solid_brush->GetOpacity();
    D2D1_MATRIX_3X2_F native_portable_brush_transform{};
    native_solid_brush->GetTransform(&native_portable_brush_transform);
    native_brush_base->Release();
    if (!approximately_equal(native_portable_brush_color.r, 1.0F) ||
        !approximately_equal(native_portable_brush_color.a, 0.75F) ||
        !approximately_equal(native_portable_brush_opacity, 0.5F) ||
        !approximately_equal(native_portable_brush_transform._22, 3.0F)) {
        return 117;
    }
    auto* native_gradient_collection = reinterpret_cast<
        ID2D1GradientStopCollection*>(gradient_collection.get());
    D2D1_GRADIENT_STOP native_gradient_stops[3]{};
    native_gradient_collection->GetGradientStops(native_gradient_stops, 3U);
    auto* native_linear_brush = reinterpret_cast<
        ID2D1LinearGradientBrush*>(linear_brush.get());
    auto* native_radial_brush = reinterpret_cast<
        ID2D1RadialGradientBrush*>(radial_brush.get());
    ID2D1GradientStopCollection* returned_native_collection = nullptr;
    native_linear_brush->GetGradientStopCollection(
        &returned_native_collection);
    const D2D1_POINT_2F native_linear_start =
        native_linear_brush->GetStartPoint();
    const D2D1_POINT_2F native_radial_center =
        native_radial_brush->GetCenter();
    const float native_radial_radius_x = native_radial_brush->GetRadiusX();
    if (returned_native_collection != nullptr) {
        returned_native_collection->Release();
    }
    if (native_gradient_collection->GetGradientStopCount() != 3U ||
        native_gradient_collection->GetColorInterpolationGamma() !=
            D2D1_GAMMA_2_2 ||
        native_gradient_collection->GetExtendMode() !=
            D2D1_EXTEND_MODE_MIRROR ||
        !approximately_equal(native_gradient_stops[1].position, 0.5F) ||
        !approximately_equal(native_linear_start.x, 4.0F) ||
        !approximately_equal(native_radial_center.x, 17.0F) ||
        !approximately_equal(native_radial_radius_x, 10.0F) ||
        returned_native_collection == nullptr) {
        return 145;
    }
    auto* native_target = reinterpret_cast<ID2D1RenderTarget*>(target.get());
    auto* native_bitmap = reinterpret_cast<ID2D1Bitmap*>(
        portable_bitmap.get());
    ID2D1Bitmap* queried_native_bitmap = nullptr;
    const D2D1_SIZE_U native_bitmap_pixel_size =
        native_bitmap->GetPixelSize();
    const D2D1_PIXEL_FORMAT native_bitmap_format =
        native_bitmap->GetPixelFormat();
    const D2D1_RECT_U native_bitmap_update_rectangle{0U, 1U, 1U, 2U};
    const std::uint8_t native_bitmap_update[]{0x44U, 0x33U, 0x22U, 0xffU};
    if (FAILED(native_bitmap->QueryInterface(
            __uuidof(ID2D1Bitmap),
            reinterpret_cast<void**>(&queried_native_bitmap))) ||
        queried_native_bitmap == nullptr ||
        native_bitmap_pixel_size.width != 2U ||
        native_bitmap_pixel_size.height != 2U ||
        native_bitmap_format.format != DXGI_FORMAT_B8G8R8A8_UNORM ||
        native_bitmap_format.alphaMode != D2D1_ALPHA_MODE_PREMULTIPLIED ||
        FAILED(native_bitmap->CopyFromMemory(
            &native_bitmap_update_rectangle,
            native_bitmap_update,
            4U))) {
        if (queried_native_bitmap != nullptr) {
            queried_native_bitmap->Release();
        }
        return 153;
    }
    queried_native_bitmap->Release();
    auto* native_bitmap_brush = reinterpret_cast<ID2D1BitmapBrush*>(
        bitmap_brush.get());
    ID2D1BitmapBrush* queried_native_bitmap_brush = nullptr;
    ID2D1Bitmap* returned_native_brush_bitmap = nullptr;
    if (FAILED(native_bitmap_brush->QueryInterface(
            __uuidof(ID2D1BitmapBrush),
            reinterpret_cast<void**>(&queried_native_bitmap_brush))) ||
        queried_native_bitmap_brush == nullptr) {
        return 165;
    }
    native_bitmap_brush->GetBitmap(&returned_native_brush_bitmap);
    const bool native_bitmap_brush_matches =
        returned_native_brush_bitmap == native_bitmap &&
        native_bitmap_brush->GetExtendModeX() == D2D1_EXTEND_MODE_CLAMP &&
        native_bitmap_brush->GetExtendModeY() == D2D1_EXTEND_MODE_WRAP &&
        native_bitmap_brush->GetInterpolationMode() ==
            D2D1_BITMAP_INTERPOLATION_MODE_LINEAR;
    if (returned_native_brush_bitmap != nullptr) {
        returned_native_brush_bitmap->Release();
    }
    queried_native_bitmap_brush->Release();
    if (!native_bitmap_brush_matches) {
        return 166;
    }
    const D2D1_SIZE_U native_target_pixel_size = native_target->GetPixelSize();
    const D2D1_SIZE_F native_target_size = native_target->GetSize();
    ID2D1SolidColorBrush* native_target_brush = nullptr;
    const D2D1_COLOR_F native_target_color{0.2F, 0.4F, 0.6F, 0.8F};
    if (native_target_pixel_size.width != 640U ||
        native_target_pixel_size.height != 480U ||
        !approximately_equal(native_target_size.width, 640.0F) ||
        FAILED(native_target->CreateSolidColorBrush(
            &native_target_color, nullptr, &native_target_brush)) ||
        native_target_brush == nullptr) {
        return 128;
    }
    native_target->BeginDraw();
    const D2D1_RECT_F native_target_rectangle{8.0F, 9.0F, 30.0F, 40.0F};
    native_target->FillRectangle(
        &native_target_rectangle, native_target_brush);
    const D2D1_RECT_F native_bitmap_brush_rectangle{
        32.0F, 9.0F, 39.0F, 25.0F};
    native_target->FillRectangle(
        &native_bitmap_brush_rectangle, native_bitmap_brush);
    const D2D1_RECT_F native_bitmap_destination{40.0F, 9.0F, 56.0F, 25.0F};
    native_target->DrawBitmap(
        native_bitmap,
        &native_bitmap_destination,
        1.0F,
        D2D1_BITMAP_INTERPOLATION_MODE_NEAREST_NEIGHBOR,
        nullptr);
    const HRESULT native_target_end_status = native_target->EndDraw();
    native_target_brush->Release();
    scene_target->GetSummary(&target_summary);
    if (FAILED(native_target_end_status) ||
        target_summary.generation != 16U ||
        target_summary.draw_count != 3U ||
        scene_target->GetRequiredSceneSize() == 0U) {
        return 129;
    }
    auto* native_factory = reinterpret_cast<ID2D1Factory*>(factory.get());
    const D2D1_RECT_F native_rectangle{2.0F, 3.0F, 6.0F, 8.0F};
    ID2D1RectangleGeometry* native_geometry = nullptr;
    if (FAILED(native_factory->CreateRectangleGeometry(
            &native_rectangle, &native_geometry)) ||
        native_geometry == nullptr) {
        return 20;
    }
    float native_area = 0.0F;
    const HRESULT native_status = native_geometry->ComputeArea(
        nullptr, D2D1_DEFAULT_FLATTENING_TOLERANCE, &native_area);
    if (FAILED(native_status) || !approximately_equal(native_area, 20.0F)) {
        native_geometry->Release();
        return 21;
    }
    const D2D1_MATRIX_3X2_F native_transform{
        1.0F, 0.0F, 0.0F, 1.0F, 4.0F, -2.0F};
    ID2D1TransformedGeometry* native_transformed = nullptr;
    const HRESULT native_transformed_status =
        native_factory->CreateTransformedGeometry(
            native_geometry, &native_transform, &native_transformed);
    native_geometry->Release();
    if (FAILED(native_transformed_status) || native_transformed == nullptr) {
        return 22;
    }
    D2D1_RECT_F native_bounds{};
    const HRESULT native_bounds_status =
        native_transformed->GetBounds(nullptr, &native_bounds);
    native_transformed->Release();
    if (FAILED(native_bounds_status) ||
        !approximately_equal(native_bounds.left, 6.0F) ||
        !approximately_equal(native_bounds.top, 1.0F) ||
        !approximately_equal(native_bounds.right, 10.0F) ||
        !approximately_equal(native_bounds.bottom, 6.0F)) {
        return 23;
    }

    const D2D1_ELLIPSE native_ellipse_value{
        D2D1_POINT_2F{2.0F, 3.0F}, 4.0F, 2.0F};
    const D2D1_MATRIX_3X2_F native_ellipse_transform{
        0.5F, 1.25F, -0.75F, 0.25F, 4.0F, -3.0F};
    ID2D1EllipseGeometry* native_ellipse = nullptr;
    if (FAILED(native_factory->CreateEllipseGeometry(
            &native_ellipse_value, &native_ellipse)) ||
        native_ellipse == nullptr) {
        return 58;
    }
    D2D1_ELLIPSE returned_native_ellipse{};
    D2D1_RECT_F portable_ellipse_bounds{};
    BOOL portable_ellipse_contains = FALSE;
    native_ellipse->GetEllipse(&returned_native_ellipse);
    const HRESULT portable_ellipse_bounds_status =
        native_ellipse->GetBounds(
            &native_ellipse_transform, &portable_ellipse_bounds);
    const HRESULT portable_ellipse_contains_status =
        native_ellipse->FillContainsPoint(
            D2D1_POINT_2F{2.75F, 0.25F},
            &native_ellipse_transform,
            D2D1_DEFAULT_FLATTENING_TOLERANCE,
            &portable_ellipse_contains);
    native_ellipse->Release();
    if (FAILED(portable_ellipse_bounds_status) ||
        FAILED(portable_ellipse_contains_status) ||
        portable_ellipse_contains != TRUE ||
        !approximately_equal(returned_native_ellipse.point.x, 2.0F) ||
        !approximately_equal(returned_native_ellipse.radiusY, 2.0F)) {
        return 59;
    }

    const D2D1_ROUNDED_RECT native_rounded_rectangle_value{
        D2D1_RECT_F{0.0F, 0.0F, 10.0F, 8.0F}, 3.0F, 2.0F};
    const D2D1_MATRIX_3X2_F native_rounded_rectangle_transform{
        0.5F, 1.25F, -0.75F, 0.25F, 4.0F, -3.0F};
    ID2D1RoundedRectangleGeometry* native_rounded_rectangle = nullptr;
    if (FAILED(native_factory->CreateRoundedRectangleGeometry(
            &native_rounded_rectangle_value, &native_rounded_rectangle)) ||
        native_rounded_rectangle == nullptr) {
        return 73;
    }
    D2D1_ROUNDED_RECT returned_native_rounded_rectangle{};
    D2D1_RECT_F portable_rounded_rectangle_bounds{};
    BOOL portable_rounded_rectangle_center_contains = FALSE;
    BOOL portable_rounded_rectangle_corner_contains = TRUE;
    native_rounded_rectangle->GetRoundedRect(
        &returned_native_rounded_rectangle);
    const HRESULT portable_rounded_rectangle_bounds_status =
        native_rounded_rectangle->GetBounds(
            &native_rounded_rectangle_transform,
            &portable_rounded_rectangle_bounds);
    const HRESULT portable_rounded_rectangle_center_status =
        native_rounded_rectangle->FillContainsPoint(
            D2D1_POINT_2F{3.5F, 4.25F},
            &native_rounded_rectangle_transform,
            D2D1_DEFAULT_FLATTENING_TOLERANCE,
            &portable_rounded_rectangle_center_contains);
    const HRESULT portable_rounded_rectangle_corner_status =
        native_rounded_rectangle->FillContainsPoint(
            D2D1_POINT_2F{3.975F, -2.85F},
            &native_rounded_rectangle_transform,
            D2D1_DEFAULT_FLATTENING_TOLERANCE,
            &portable_rounded_rectangle_corner_contains);
    native_rounded_rectangle->Release();
    if (FAILED(portable_rounded_rectangle_bounds_status) ||
        FAILED(portable_rounded_rectangle_center_status) ||
        FAILED(portable_rounded_rectangle_corner_status) ||
        portable_rounded_rectangle_center_contains != TRUE ||
        portable_rounded_rectangle_corner_contains != FALSE ||
        !approximately_equal(
            returned_native_rounded_rectangle.radiusX, 3.0F) ||
        !approximately_equal(
            returned_native_rounded_rectangle.rect.bottom, 8.0F)) {
        return 74;
    }

    std::array<ID2D1Geometry*, 2U> native_group_sources{
        reinterpret_cast<ID2D1Geometry*>(geometry_base.get()),
        reinterpret_cast<ID2D1Geometry*>(ellipse_base.get())};
    ID2D1GeometryGroup* native_group = nullptr;
    if (FAILED(native_factory->CreateGeometryGroup(
            D2D1_FILL_MODE_ALTERNATE,
            native_group_sources.data(),
            static_cast<UINT32>(native_group_sources.size()),
            &native_group)) ||
        native_group == nullptr) {
        return 82;
    }
    D2D1_RECT_F portable_group_bounds{};
    const HRESULT portable_group_bounds_status = native_group->GetBounds(
        &native_ellipse_transform, &portable_group_bounds);
    std::array<ID2D1Geometry*, 2U> returned_native_group_sources{};
    native_group->GetSourceGeometries(
        returned_native_group_sources.data(),
        static_cast<UINT32>(returned_native_group_sources.size()));
    const bool native_group_metadata_matches =
        native_group->GetFillMode() == D2D1_FILL_MODE_ALTERNATE &&
        native_group->GetSourceGeometryCount() ==
            static_cast<UINT32>(native_group_sources.size()) &&
        returned_native_group_sources[0U] == native_group_sources[0U] &&
        returned_native_group_sources[1U] == native_group_sources[1U];
    for (auto* returned_native_source : returned_native_group_sources) {
        if (returned_native_source != nullptr) {
            returned_native_source->Release();
        }
    }
    native_group->Release();
    if (FAILED(portable_group_bounds_status) ||
        !native_group_metadata_matches) {
        return 83;
    }

    const D2D1_STROKE_STYLE_PROPERTIES native_stroke_properties{
        D2D1_CAP_STYLE_ROUND,
        D2D1_CAP_STYLE_SQUARE,
        D2D1_CAP_STYLE_TRIANGLE,
        D2D1_LINE_JOIN_BEVEL,
        4.0F,
        D2D1_DASH_STYLE_CUSTOM,
        0.5F};
    const std::array<float, 4U> native_stroke_dashes{
        2.0F, 1.0F, 0.5F, 1.0F};
    ID2D1StrokeStyle* native_stroke_style = nullptr;
    if (FAILED(native_factory->CreateStrokeStyle(
            &native_stroke_properties,
            native_stroke_dashes.data(),
            static_cast<UINT32>(native_stroke_dashes.size()),
            &native_stroke_style)) ||
        native_stroke_style == nullptr) {
        return 96;
    }
    std::array<float, 4U> portable_native_stroke_dashes{};
    native_stroke_style->GetDashes(
        portable_native_stroke_dashes.data(),
        static_cast<UINT32>(portable_native_stroke_dashes.size()));
    const bool portable_native_stroke_matches =
        native_stroke_style->GetStartCap() == D2D1_CAP_STYLE_ROUND &&
        native_stroke_style->GetEndCap() == D2D1_CAP_STYLE_SQUARE &&
        native_stroke_style->GetDashCap() == D2D1_CAP_STYLE_TRIANGLE &&
        native_stroke_style->GetLineJoin() == D2D1_LINE_JOIN_BEVEL &&
        approximately_equal(native_stroke_style->GetMiterLimit(), 4.0F) &&
        approximately_equal(native_stroke_style->GetDashOffset(), 0.5F) &&
        native_stroke_style->GetDashStyle() == D2D1_DASH_STYLE_CUSTOM &&
        native_stroke_style->GetDashesCount() ==
            static_cast<UINT32>(native_stroke_dashes.size());
    native_stroke_style->Release();
    if (!portable_native_stroke_matches) {
        return 97;
    }

    const D2D1_DRAWING_STATE_DESCRIPTION native_drawing_state_description{
        D2D1_ANTIALIAS_MODE_ALIASED,
        D2D1_TEXT_ANTIALIAS_MODE_GRAYSCALE,
        17U,
        23U,
        D2D1_MATRIX_3X2_F{1.0F, 0.25F, -0.5F, 2.0F, 3.0F, -4.0F}};
    ID2D1DrawingStateBlock* native_drawing_state = nullptr;
    if (FAILED(native_factory->CreateDrawingStateBlock(
            &native_drawing_state_description,
            nullptr,
            &native_drawing_state)) ||
        native_drawing_state == nullptr) {
        return 107;
    }
    D2D1_DRAWING_STATE_DESCRIPTION portable_native_drawing_state{};
    native_drawing_state->GetDescription(&portable_native_drawing_state);
    D2D1_DRAWING_STATE_DESCRIPTION changed_native_drawing_state =
        native_drawing_state_description;
    changed_native_drawing_state.tag1 = 31U;
    changed_native_drawing_state.transform._31 = 9.0F;
    native_drawing_state->SetDescription(&changed_native_drawing_state);
    D2D1_DRAWING_STATE_DESCRIPTION returned_changed_native_drawing_state{};
    native_drawing_state->GetDescription(
        &returned_changed_native_drawing_state);
    IDWriteRenderingParams* portable_native_text_parameters =
        reinterpret_cast<IDWriteRenderingParams*>(
            static_cast<std::uintptr_t>(1U));
    native_drawing_state->GetTextRenderingParams(
        &portable_native_text_parameters);
    native_drawing_state->Release();
    if (portable_native_drawing_state.antialiasMode !=
            D2D1_ANTIALIAS_MODE_ALIASED ||
        portable_native_drawing_state.textAntialiasMode !=
            D2D1_TEXT_ANTIALIAS_MODE_GRAYSCALE ||
        portable_native_drawing_state.tag1 != 17U ||
        portable_native_drawing_state.tag2 != 23U ||
        returned_changed_native_drawing_state.tag1 != 31U ||
        !approximately_equal(
            returned_changed_native_drawing_state.transform._31, 9.0F) ||
        portable_native_text_parameters != nullptr) {
        return 108;
    }

    ID2D1PathGeometry* native_path = nullptr;
    if (FAILED(native_factory->CreatePathGeometry(&native_path)) ||
        native_path == nullptr) {
        return 41;
    }
    ID2D1GeometrySink* native_path_sink = nullptr;
    if (FAILED(native_path->Open(&native_path_sink)) ||
        native_path_sink == nullptr) {
        native_path->Release();
        return 42;
    }
    ID2D1SimplifiedGeometrySink* native_path_sink_base = nullptr;
    if (FAILED(native_path_sink->QueryInterface(
            __uuidof(ID2D1SimplifiedGeometrySink),
            reinterpret_cast<void**>(&native_path_sink_base))) ||
        native_path_sink_base == nullptr) {
        native_path_sink->Release();
        native_path->Release();
        return 43;
    }
    native_path_sink_base->Release();
    native_path_sink->SetFillMode(D2D1_FILL_MODE_WINDING);
    native_path_sink->BeginFigure(
        D2D1_POINT_2F{0.0F, 0.0F}, D2D1_FIGURE_BEGIN_FILLED);
    native_path_sink->AddLine(D2D1_POINT_2F{2.0F, 0.0F});
    const D2D1_QUADRATIC_BEZIER_SEGMENT native_quadratic{
        D2D1_POINT_2F{3.0F, 2.0F}, D2D1_POINT_2F{4.0F, 0.0F}};
    native_path_sink->SetSegmentFlags(
        D2D1_PATH_SEGMENT_FORCE_ROUND_LINE_JOIN);
    native_path_sink->AddQuadraticBezier(&native_quadratic);
    native_path_sink->EndFigure(D2D1_FIGURE_END_CLOSED);
    const HRESULT native_path_close_status = native_path_sink->Close();
    native_path_sink->Release();
    if (FAILED(native_path_close_status)) {
        native_path->Release();
        return 44;
    }
    UINT32 native_path_segments = 0U;
    UINT32 native_path_figures = 0U;
    D2D1_RECT_F native_path_bounds{};
    if (FAILED(native_path->GetSegmentCount(&native_path_segments)) ||
        native_path_segments != 3U ||
        FAILED(native_path->GetFigureCount(&native_path_figures)) ||
        native_path_figures != 1U ||
        FAILED(native_path->GetBounds(nullptr, &native_path_bounds)) ||
        !approximately_equal(native_path_bounds.left, 0.0F) ||
        !approximately_equal(native_path_bounds.top, 0.0F) ||
        !approximately_equal(native_path_bounds.right, 4.0F) ||
        !approximately_equal(native_path_bounds.bottom, 1.0F)) {
        native_path->Release();
        return 45;
    }

    ID2D1PathGeometry* native_streamed_path = nullptr;
    ID2D1GeometrySink* native_streamed_sink = nullptr;
    if (FAILED(native_factory->CreatePathGeometry(&native_streamed_path)) ||
        native_streamed_path == nullptr ||
        FAILED(native_streamed_path->Open(&native_streamed_sink)) ||
        native_streamed_sink == nullptr) {
        if (native_streamed_path != nullptr) {
            native_streamed_path->Release();
        }
        native_path->Release();
        return 46;
    }
    const HRESULT native_stream_status =
        native_path->Stream(native_streamed_sink);
    const HRESULT native_stream_close_status = native_streamed_sink->Close();
    native_streamed_sink->Release();
    native_path->Release();
    native_path_segments = 0U;
    const HRESULT native_stream_count_status =
        native_streamed_path->GetSegmentCount(&native_path_segments);
    native_streamed_path->Release();
    if (FAILED(native_stream_status) || FAILED(native_stream_close_status) ||
        FAILED(native_stream_count_status) || native_path_segments != 3U) {
        return 47;
    }

    ID2D1Factory* system_factory = nullptr;
    if (FAILED(D2D1CreateFactory(
            D2D1_FACTORY_TYPE_SINGLE_THREADED,
            &system_factory)) ||
        system_factory == nullptr) {
        return 48;
    }
    ID2D1PathGeometry* system_path = nullptr;
    ID2D1GeometrySink* system_sink = nullptr;
    if (FAILED(system_factory->CreatePathGeometry(&system_path)) ||
        system_path == nullptr || FAILED(system_path->Open(&system_sink)) ||
        system_sink == nullptr) {
        if (system_path != nullptr) {
            system_path->Release();
        }
        system_factory->Release();
        return 49;
    }
    system_sink->SetFillMode(D2D1_FILL_MODE_WINDING);
    system_sink->BeginFigure(
        D2D1_POINT_2F{0.0F, 0.0F}, D2D1_FIGURE_BEGIN_FILLED);
    system_sink->AddLine(D2D1_POINT_2F{2.0F, 0.0F});
    system_sink->SetSegmentFlags(
        D2D1_PATH_SEGMENT_FORCE_ROUND_LINE_JOIN);
    system_sink->AddQuadraticBezier(&native_quadratic);
    system_sink->EndFigure(D2D1_FIGURE_END_CLOSED);
    const HRESULT system_close_status = system_sink->Close();
    system_sink->Release();
    UINT32 system_segments = 0U;
    UINT32 system_figures = 0U;
    D2D1_RECT_F system_bounds{};
    const HRESULT system_segment_status =
        system_path->GetSegmentCount(&system_segments);
    const HRESULT system_figure_status =
        system_path->GetFigureCount(&system_figures);
    const HRESULT system_bounds_status =
        system_path->GetBounds(nullptr, &system_bounds);
    system_path->Release();
    if (FAILED(system_close_status) || FAILED(system_segment_status) ||
        FAILED(system_figure_status) || FAILED(system_bounds_status) ||
        system_segments != native_path_segments || system_figures != 1U ||
        !approximately_equal(system_bounds.left, native_path_bounds.left) ||
        !approximately_equal(system_bounds.top, native_path_bounds.top) ||
        !approximately_equal(system_bounds.right, native_path_bounds.right) ||
        !approximately_equal(
            system_bounds.bottom, native_path_bounds.bottom)) {
        system_factory->Release();
        return 50;
    }

    ID2D1PathGeometry* system_arc_path = nullptr;
    ID2D1GeometrySink* system_arc_sink = nullptr;
    if (FAILED(system_factory->CreatePathGeometry(&system_arc_path)) ||
        system_arc_path == nullptr ||
        FAILED(system_arc_path->Open(&system_arc_sink)) ||
        system_arc_sink == nullptr) {
        if (system_arc_path != nullptr) {
            system_arc_path->Release();
        }
        system_factory->Release();
        return 51;
    }
    const D2D1_ARC_SEGMENT system_arc{
        D2D1_POINT_2F{2.0F, 0.0F},
        D2D1_SIZE_F{1.0F, 1.0F},
        0.0F,
        D2D1_SWEEP_DIRECTION_CLOCKWISE,
        D2D1_ARC_SIZE_SMALL};
    system_arc_sink->BeginFigure(
        D2D1_POINT_2F{0.0F, 0.0F}, D2D1_FIGURE_BEGIN_FILLED);
    system_arc_sink->AddArc(&system_arc);
    system_arc_sink->EndFigure(D2D1_FIGURE_END_OPEN);
    const HRESULT system_arc_close_status = system_arc_sink->Close();
    system_arc_sink->Release();
    D2D1_RECT_F system_arc_bounds{};
    const HRESULT system_arc_bounds_status =
        system_arc_path->GetBounds(nullptr, &system_arc_bounds);
    system_arc_path->Release();
    auto* portable_arc_path =
        reinterpret_cast<ID2D1PathGeometry*>(arc_path.get());
    D2D1_RECT_F portable_arc_bounds{};
    const HRESULT portable_arc_bounds_status =
        portable_arc_path->GetBounds(nullptr, &portable_arc_bounds);
    if (FAILED(system_arc_close_status) ||
        FAILED(system_arc_bounds_status) ||
        FAILED(portable_arc_bounds_status) ||
        !approximately_equal(
            system_arc_bounds.left, portable_arc_bounds.left) ||
        !approximately_equal(
            system_arc_bounds.top, portable_arc_bounds.top) ||
        !approximately_equal(
            system_arc_bounds.right, portable_arc_bounds.right) ||
        !approximately_equal(
            system_arc_bounds.bottom, portable_arc_bounds.bottom)) {
        system_factory->Release();
        return 52;
    }

    ID2D1EllipseGeometry* system_ellipse = nullptr;
    if (FAILED(system_factory->CreateEllipseGeometry(
            &native_ellipse_value, &system_ellipse)) ||
        system_ellipse == nullptr) {
        system_factory->Release();
        return 60;
    }
    D2D1_RECT_F system_ellipse_bounds{};
    BOOL system_ellipse_contains = FALSE;
    const HRESULT system_ellipse_bounds_status =
        system_ellipse->GetBounds(
            &native_ellipse_transform, &system_ellipse_bounds);
    const HRESULT system_ellipse_contains_status =
        system_ellipse->FillContainsPoint(
            D2D1_POINT_2F{2.75F, 0.25F},
            &native_ellipse_transform,
            D2D1_DEFAULT_FLATTENING_TOLERANCE,
            &system_ellipse_contains);
    system_ellipse->Release();
    if (FAILED(system_ellipse_bounds_status) ||
        FAILED(system_ellipse_contains_status)) {
        system_factory->Release();
        return 61;
    }
    if (system_ellipse_contains != portable_ellipse_contains) {
        system_factory->Release();
        return 62;
    }
    if (!approximately_equal(
            system_ellipse_bounds.left, portable_ellipse_bounds.left)) {
        std::fprintf(
            stderr,
            "ellipse bounds system=(%.9g,%.9g,%.9g,%.9g) portable=(%.9g,%.9g,%.9g,%.9g)\n",
            system_ellipse_bounds.left,
            system_ellipse_bounds.top,
            system_ellipse_bounds.right,
            system_ellipse_bounds.bottom,
            portable_ellipse_bounds.left,
            portable_ellipse_bounds.top,
            portable_ellipse_bounds.right,
            portable_ellipse_bounds.bottom);
        system_factory->Release();
        return 63;
    }
    if (!approximately_equal(
            system_ellipse_bounds.top, portable_ellipse_bounds.top)) {
        system_factory->Release();
        return 64;
    }
    if (!approximately_equal(
            system_ellipse_bounds.right, portable_ellipse_bounds.right)) {
        system_factory->Release();
        return 65;
    }
    if (!approximately_equal(
            system_ellipse_bounds.bottom, portable_ellipse_bounds.bottom)) {
        system_factory->Release();
        return 66;
    }

    ID2D1RoundedRectangleGeometry* system_rounded_rectangle = nullptr;
    if (FAILED(system_factory->CreateRoundedRectangleGeometry(
            &native_rounded_rectangle_value, &system_rounded_rectangle)) ||
        system_rounded_rectangle == nullptr) {
        system_factory->Release();
        return 75;
    }
    D2D1_RECT_F system_rounded_rectangle_bounds{};
    BOOL system_rounded_rectangle_center_contains = FALSE;
    BOOL system_rounded_rectangle_corner_contains = TRUE;
    const HRESULT system_rounded_rectangle_bounds_status =
        system_rounded_rectangle->GetBounds(
            &native_rounded_rectangle_transform,
            &system_rounded_rectangle_bounds);
    const HRESULT system_rounded_rectangle_center_status =
        system_rounded_rectangle->FillContainsPoint(
            D2D1_POINT_2F{3.5F, 4.25F},
            &native_rounded_rectangle_transform,
            D2D1_DEFAULT_FLATTENING_TOLERANCE,
            &system_rounded_rectangle_center_contains);
    const HRESULT system_rounded_rectangle_corner_status =
        system_rounded_rectangle->FillContainsPoint(
            D2D1_POINT_2F{3.975F, -2.85F},
            &native_rounded_rectangle_transform,
            D2D1_DEFAULT_FLATTENING_TOLERANCE,
            &system_rounded_rectangle_corner_contains);
    system_rounded_rectangle->Release();
    if (FAILED(system_rounded_rectangle_bounds_status) ||
        FAILED(system_rounded_rectangle_center_status) ||
        FAILED(system_rounded_rectangle_corner_status) ||
        system_rounded_rectangle_center_contains !=
            portable_rounded_rectangle_center_contains ||
        system_rounded_rectangle_corner_contains !=
            portable_rounded_rectangle_corner_contains ||
        !approximately_equal(
            system_rounded_rectangle_bounds.left,
            portable_rounded_rectangle_bounds.left) ||
        !approximately_equal(
            system_rounded_rectangle_bounds.top,
            portable_rounded_rectangle_bounds.top) ||
        !approximately_equal(
            system_rounded_rectangle_bounds.right,
            portable_rounded_rectangle_bounds.right) ||
        !approximately_equal(
            system_rounded_rectangle_bounds.bottom,
            portable_rounded_rectangle_bounds.bottom)) {
        system_factory->Release();
        return 76;
    }

    const D2D1_RECT_F system_group_rectangle_value{
        1.0F, 2.0F, 5.0F, 8.0F};
    ID2D1RectangleGeometry* system_group_rectangle = nullptr;
    ID2D1EllipseGeometry* system_group_ellipse = nullptr;
    if (FAILED(system_factory->CreateRectangleGeometry(
            &system_group_rectangle_value, &system_group_rectangle)) ||
        system_group_rectangle == nullptr ||
        FAILED(system_factory->CreateEllipseGeometry(
            &native_ellipse_value, &system_group_ellipse)) ||
        system_group_ellipse == nullptr) {
        if (system_group_rectangle != nullptr) {
            system_group_rectangle->Release();
        }
        if (system_group_ellipse != nullptr) {
            system_group_ellipse->Release();
        }
        system_factory->Release();
        return 84;
    }
    std::array<ID2D1Geometry*, 2U> system_group_sources{
        system_group_rectangle, system_group_ellipse};
    ID2D1GeometryGroup* system_group = nullptr;
    const HRESULT system_group_create_status =
        system_factory->CreateGeometryGroup(
            D2D1_FILL_MODE_ALTERNATE,
            system_group_sources.data(),
            static_cast<UINT32>(system_group_sources.size()),
            &system_group);
    system_group_rectangle->Release();
    system_group_ellipse->Release();
    if (FAILED(system_group_create_status) || system_group == nullptr) {
        system_factory->Release();
        return 85;
    }
    D2D1_RECT_F system_group_bounds{};
    const HRESULT system_group_bounds_status = system_group->GetBounds(
        &native_ellipse_transform, &system_group_bounds);
    const bool system_group_metadata_matches =
        system_group->GetFillMode() == D2D1_FILL_MODE_ALTERNATE &&
        system_group->GetSourceGeometryCount() ==
            static_cast<UINT32>(system_group_sources.size());
    system_group->Release();
    if (FAILED(system_group_bounds_status) ||
        !system_group_metadata_matches ||
        !approximately_equal(
            system_group_bounds.left, portable_group_bounds.left) ||
        !approximately_equal(
            system_group_bounds.top, portable_group_bounds.top) ||
        !approximately_equal(
            system_group_bounds.right, portable_group_bounds.right) ||
        !approximately_equal(
            system_group_bounds.bottom, portable_group_bounds.bottom)) {
        system_factory->Release();
        return 86;
    }

    ID2D1StrokeStyle* system_stroke_style = nullptr;
    if (FAILED(system_factory->CreateStrokeStyle(
            &native_stroke_properties,
            native_stroke_dashes.data(),
            static_cast<UINT32>(native_stroke_dashes.size()),
            &system_stroke_style)) ||
        system_stroke_style == nullptr) {
        system_factory->Release();
        return 98;
    }
    std::array<float, 4U> system_stroke_dashes{};
    system_stroke_style->GetDashes(
        system_stroke_dashes.data(),
        static_cast<UINT32>(system_stroke_dashes.size()));
    const bool system_stroke_matches =
        system_stroke_style->GetStartCap() == D2D1_CAP_STYLE_ROUND &&
        system_stroke_style->GetEndCap() == D2D1_CAP_STYLE_SQUARE &&
        system_stroke_style->GetDashCap() == D2D1_CAP_STYLE_TRIANGLE &&
        system_stroke_style->GetLineJoin() == D2D1_LINE_JOIN_BEVEL &&
        approximately_equal(system_stroke_style->GetMiterLimit(), 4.0F) &&
        approximately_equal(system_stroke_style->GetDashOffset(), 0.5F) &&
        system_stroke_style->GetDashStyle() == D2D1_DASH_STYLE_CUSTOM &&
        system_stroke_style->GetDashesCount() ==
            static_cast<UINT32>(native_stroke_dashes.size()) &&
        system_stroke_dashes == portable_native_stroke_dashes;
    system_stroke_style->Release();
    if (!system_stroke_matches) {
        system_factory->Release();
        return 99;
    }

    ID2D1DrawingStateBlock* system_drawing_state = nullptr;
    if (FAILED(system_factory->CreateDrawingStateBlock(
            &native_drawing_state_description,
            nullptr,
            &system_drawing_state)) ||
        system_drawing_state == nullptr) {
        system_factory->Release();
        return 109;
    }
    D2D1_DRAWING_STATE_DESCRIPTION system_drawing_state_description{};
    system_drawing_state->GetDescription(
        &system_drawing_state_description);
    IDWriteRenderingParams* system_text_parameters =
        reinterpret_cast<IDWriteRenderingParams*>(
            static_cast<std::uintptr_t>(1U));
    system_drawing_state->GetTextRenderingParams(&system_text_parameters);
    system_drawing_state->Release();
    system_factory->Release();
    if (system_drawing_state_description.antialiasMode !=
            portable_native_drawing_state.antialiasMode ||
        system_drawing_state_description.textAntialiasMode !=
            portable_native_drawing_state.textAntialiasMode ||
        system_drawing_state_description.tag1 !=
            portable_native_drawing_state.tag1 ||
        system_drawing_state_description.tag2 !=
            portable_native_drawing_state.tag2 ||
        !approximately_equal(
            system_drawing_state_description.transform._11,
            portable_native_drawing_state.transform._11) ||
        !approximately_equal(
            system_drawing_state_description.transform._12,
            portable_native_drawing_state.transform._12) ||
        !approximately_equal(
            system_drawing_state_description.transform._21,
            portable_native_drawing_state.transform._21) ||
        !approximately_equal(
            system_drawing_state_description.transform._22,
            portable_native_drawing_state.transform._22) ||
        !approximately_equal(
            system_drawing_state_description.transform._31,
            portable_native_drawing_state.transform._31) ||
        !approximately_equal(
            system_drawing_state_description.transform._32,
            portable_native_drawing_state.transform._32) ||
        system_text_parameters != nullptr) {
        return 110;
    }
#endif
    return 0;
}
