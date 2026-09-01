#include "progpu_native_direct2d_compat.hpp"

#if defined(_WIN32)
#  include <d2d1.h>
#endif

#include <cmath>
#include <cstdint>

namespace compat = progpu::native::direct2d::compat;
namespace core = progpu::native::direct2d::core;
namespace com = progpu::native::com;

namespace {

[[nodiscard]] bool approximately_equal(float left, float right) noexcept
{
    return std::abs(left - right) <= 0.0001F;
}

class simplified_sink final : public compat::simplified_geometry_sink {
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
            *value = static_cast<compat::simplified_geometry_sink*>(this);
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
        const compat::point_2f*,
        std::uint32_t point_count) noexcept override
    {
        line_count += point_count;
    }

    void PROGPU_NATIVE_COM_CALL AddBeziers(
        const compat::bezier_segment*,
        std::uint32_t value_count) noexcept override
    {
        bezier_count += value_count;
    }

    void PROGPU_NATIVE_COM_CALL EndFigure(compat::figure_end end)
        noexcept override
    {
        figure_end = end;
        ++end_count;
    }

    com::result PROGPU_NATIVE_COM_CALL Close() noexcept override
    {
        return com::ok;
    }

    compat::fill_mode fill_mode = compat::fill_mode::alternate;
    compat::path_segment segment_flags =
        compat::path_segment::force_unstroked;
    compat::figure_begin figure_begin = compat::figure_begin::hollow;
    compat::figure_end figure_end = compat::figure_end::open;
    compat::point_2f first{};
    std::uint32_t begin_count = 0U;
    std::uint32_t end_count = 0U;
    std::uint32_t line_count = 0U;
    std::uint32_t bezier_count = 0U;

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
        raw_simplified_sink->bezier_count != 0U) {
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

    compat::geometry* unsupported = reinterpret_cast<compat::geometry*>(
        static_cast<std::uintptr_t>(1U));
    if (factory->CreateEllipseGeometry(nullptr, &unsupported) !=
            compat::not_implemented ||
        unsupported != nullptr ||
        factory->CreateEllipseGeometry(nullptr, nullptr) !=
            com::pointer_error) {
        return 13;
    }

#if defined(_WIN32)
    if (!com::guid_equal(
            compat::factory_interface_id, __uuidof(ID2D1Factory)) ||
        !com::guid_equal(
            compat::rectangle_geometry_interface_id,
            __uuidof(ID2D1RectangleGeometry)) ||
        sizeof(compat::rectangle_f) != sizeof(D2D1_RECT_F) ||
        sizeof(compat::triangle) != sizeof(D2D1_TRIANGLE)) {
        return 14;
    }
    auto* native_factory = reinterpret_cast<ID2D1Factory*>(factory.get());
    const D2D1_RECT_F native_rectangle{2.0F, 3.0F, 6.0F, 8.0F};
    ID2D1RectangleGeometry* native_geometry = nullptr;
    if (FAILED(native_factory->CreateRectangleGeometry(
            &native_rectangle, &native_geometry)) ||
        native_geometry == nullptr) {
        return 15;
    }
    float native_area = 0.0F;
    const HRESULT native_status = native_geometry->ComputeArea(
        nullptr, D2D1_DEFAULT_FLATTENING_TOLERANCE, &native_area);
    native_geometry->Release();
    if (FAILED(native_status) || !approximately_equal(native_area, 20.0F)) {
        return 16;
    }
#endif
    return 0;
}
