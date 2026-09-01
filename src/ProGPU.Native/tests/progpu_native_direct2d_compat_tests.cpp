#include "progpu_native_direct2d_compat.hpp"

#if defined(_WIN32)
#  include <d2d1.h>
#endif

#include <cmath>
#include <cstdint>
#include <limits>

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

    compat::geometry* unsupported = reinterpret_cast<compat::geometry*>(
        static_cast<std::uintptr_t>(1U));
    if (factory->CreateEllipseGeometry(nullptr, &unsupported) !=
            compat::not_implemented ||
        unsupported != nullptr ||
        factory->CreateEllipseGeometry(nullptr, nullptr) !=
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
        sizeof(compat::triangle) != sizeof(D2D1_TRIANGLE) ||
        sizeof(compat::quadratic_bezier_segment) !=
            sizeof(D2D1_QUADRATIC_BEZIER_SEGMENT) ||
        sizeof(compat::arc_segment) != sizeof(D2D1_ARC_SEGMENT)) {
        return 19;
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
    system_factory->Release();
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
        return 52;
    }
#endif
    return 0;
}
