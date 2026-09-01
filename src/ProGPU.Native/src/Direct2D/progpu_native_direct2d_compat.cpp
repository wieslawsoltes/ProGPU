#include "progpu_native_direct2d_compat.hpp"
#include "progpu_native_direct2d_ellipse.hpp"
#include "progpu_native_direct2d_geometry_group.hpp"
#include "progpu_native_direct2d_path.hpp"
#include "progpu_native_direct2d_rounded_rectangle.hpp"

#include <array>
#include <cmath>
#include <new>

namespace progpu::native::direct2d::compat {
namespace {

class portable_factory;

class portable_rectangle_geometry final : public rectangle_geometry {
public:
    portable_rectangle_geometry(
        factory* owner,
        rectangle_f rectangle) noexcept
        : owner_(owner), geometry_(rectangle)
    {
    }

    com::result PROGPU_NATIVE_COM_CALL QueryInterface(
        com::guid_ref interface_id,
        void** value) noexcept override
    {
        if (value == nullptr) {
            return com::pointer_error;
        }
        *value = nullptr;
        if (com::guid_equal(interface_id, com::unknown_interface_id()) ||
            com::guid_equal(interface_id, resource_interface_id) ||
            com::guid_equal(interface_id, geometry_interface_id) ||
            com::guid_equal(interface_id, rectangle_geometry_interface_id)) {
            *value = static_cast<rectangle_geometry*>(this);
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

    void PROGPU_NATIVE_COM_CALL GetFactory(factory** value) const
        noexcept override
    {
        if (value == nullptr) {
            return;
        }
        *value = owner_.get();
        if (*value != nullptr) {
            (*value)->AddRef();
        }
    }

    com::result PROGPU_NATIVE_COM_CALL GetBounds(
        const matrix_3x2_f* world_transform,
        rectangle_f* bounds) const noexcept override
    {
        return geometry_.bounds(world_transform, bounds);
    }

    com::result PROGPU_NATIVE_COM_CALL GetWidenedBounds(
        float,
        stroke_style*,
        const matrix_3x2_f*,
        float,
        rectangle_f* bounds) const noexcept override
    {
        if (bounds == nullptr) {
            return com::pointer_error;
        }
        *bounds = {};
        return not_implemented;
    }

    com::result PROGPU_NATIVE_COM_CALL StrokeContainsPoint(
        point_2f,
        float,
        stroke_style*,
        const matrix_3x2_f*,
        float,
        std::int32_t* contains) const noexcept override
    {
        if (contains == nullptr) {
            return com::pointer_error;
        }
        *contains = 0;
        return not_implemented;
    }

    com::result PROGPU_NATIVE_COM_CALL FillContainsPoint(
        point_2f point,
        const matrix_3x2_f* world_transform,
        float flattening_tolerance,
        std::int32_t* contains) const noexcept override
    {
        if (contains == nullptr) {
            return com::pointer_error;
        }
        std::uint32_t result = 0U;
        const com::result status = geometry_.fill_contains_point(
            point, world_transform, flattening_tolerance, &result);
        *contains = result == 0U ? 0 : 1;
        return status;
    }

    com::result PROGPU_NATIVE_COM_CALL CompareWithGeometry(
        geometry*,
        const matrix_3x2_f*,
        float,
        geometry_relation* relation) const noexcept override
    {
        if (relation == nullptr) {
            return com::pointer_error;
        }
        *relation = geometry_relation::unknown;
        return not_implemented;
    }

    com::result PROGPU_NATIVE_COM_CALL Simplify(
        geometry_simplification_option option,
        const matrix_3x2_f* world_transform,
        float flattening_tolerance,
        simplified_geometry_sink* sink) const noexcept override
    {
        if (sink == nullptr) {
            return com::pointer_error;
        }
        if ((option != geometry_simplification_option::cubics_and_lines &&
                option != geometry_simplification_option::lines) ||
            !std::isfinite(flattening_tolerance) ||
            flattening_tolerance <= 0.0F) {
            return com::invalid_argument;
        }
        std::array<point_2f, 4U> points{};
        const com::result status = geometry_.vertices(
            world_transform, points);
        if (com::failed(status)) {
            return status;
        }
        sink->SetFillMode(fill_mode::winding);
        sink->SetSegmentFlags(path_segment::none);
        sink->BeginFigure(points[0U], figure_begin::filled);
        sink->AddLines(points.data() + 1U, 3U);
        sink->EndFigure(figure_end::closed);
        return com::ok;
    }

    com::result PROGPU_NATIVE_COM_CALL Tessellate(
        const matrix_3x2_f* world_transform,
        float flattening_tolerance,
        tessellation_sink* sink) const noexcept override
    {
        if (sink == nullptr) {
            return com::pointer_error;
        }
        std::array<triangle, 2U> triangles{};
        const com::result status = geometry_.tessellate(
            world_transform, flattening_tolerance, &triangles);
        if (com::failed(status)) {
            return status;
        }
        sink->AddTriangles(
            triangles.data(), static_cast<std::uint32_t>(triangles.size()));
        return com::ok;
    }

    com::result PROGPU_NATIVE_COM_CALL CombineWithGeometry(
        geometry*,
        combine_mode,
        const matrix_3x2_f*,
        float,
        simplified_geometry_sink*) const noexcept override
    {
        return not_implemented;
    }

    com::result PROGPU_NATIVE_COM_CALL Outline(
        const matrix_3x2_f*,
        float,
        simplified_geometry_sink*) const noexcept override
    {
        return not_implemented;
    }

    com::result PROGPU_NATIVE_COM_CALL ComputeArea(
        const matrix_3x2_f* world_transform,
        float flattening_tolerance,
        float* area) const noexcept override
    {
        return geometry_.area(world_transform, flattening_tolerance, area);
    }

    com::result PROGPU_NATIVE_COM_CALL ComputeLength(
        const matrix_3x2_f* world_transform,
        float flattening_tolerance,
        float* length) const noexcept override
    {
        return geometry_.length(
            world_transform, flattening_tolerance, length);
    }

    com::result PROGPU_NATIVE_COM_CALL ComputePointAtLength(
        float length,
        const matrix_3x2_f* world_transform,
        float flattening_tolerance,
        point_2f* point,
        point_2f* unit_tangent) const noexcept override
    {
        return geometry_.point_at_length(
            length,
            world_transform,
            flattening_tolerance,
            point,
            unit_tangent);
    }

    com::result PROGPU_NATIVE_COM_CALL Widen(
        float,
        stroke_style*,
        const matrix_3x2_f*,
        float,
        simplified_geometry_sink*) const noexcept override
    {
        return not_implemented;
    }

    void PROGPU_NATIVE_COM_CALL GetRect(rectangle_f* rectangle) const
        noexcept override
    {
        if (rectangle != nullptr) {
            *rectangle = geometry_.rectangle();
        }
    }

private:
    friend class com::atomic_reference_count<portable_rectangle_geometry>;
    ~portable_rectangle_geometry() = default;

    com::atomic_reference_count<portable_rectangle_geometry> reference_count_;
    com::pointer<factory> owner_;
    core::rectangle_geometry geometry_;
};

class portable_transformed_geometry final : public transformed_geometry {
public:
    portable_transformed_geometry(
        factory* owner,
        geometry* source,
        matrix_3x2_f transform) noexcept
        : owner_(owner), source_(source), transform_(transform)
    {
    }

    com::result PROGPU_NATIVE_COM_CALL QueryInterface(
        com::guid_ref interface_id,
        void** value) noexcept override
    {
        if (value == nullptr) {
            return com::pointer_error;
        }
        *value = nullptr;
        if (com::guid_equal(interface_id, com::unknown_interface_id()) ||
            com::guid_equal(interface_id, resource_interface_id) ||
            com::guid_equal(interface_id, geometry_interface_id) ||
            com::guid_equal(interface_id, transformed_geometry_interface_id)) {
            *value = static_cast<transformed_geometry*>(this);
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

    void PROGPU_NATIVE_COM_CALL GetFactory(factory** value) const
        noexcept override
    {
        if (value == nullptr) {
            return;
        }
        *value = owner_.get();
        if (*value != nullptr) {
            (*value)->AddRef();
        }
    }

    com::result PROGPU_NATIVE_COM_CALL GetBounds(
        const matrix_3x2_f* world_transform,
        rectangle_f* bounds) const noexcept override
    {
        matrix_3x2_f composed{};
        const com::result status = compose(world_transform, &composed);
        return com::failed(status)
            ? status
            : source_->GetBounds(&composed, bounds);
    }

    com::result PROGPU_NATIVE_COM_CALL GetWidenedBounds(
        float stroke_width,
        stroke_style* style,
        const matrix_3x2_f* world_transform,
        float flattening_tolerance,
        rectangle_f* bounds) const noexcept override
    {
        matrix_3x2_f composed{};
        const com::result status = compose(world_transform, &composed);
        return com::failed(status)
            ? status
            : source_->GetWidenedBounds(
                  stroke_width,
                  style,
                  &composed,
                  flattening_tolerance,
                  bounds);
    }

    com::result PROGPU_NATIVE_COM_CALL StrokeContainsPoint(
        point_2f point,
        float stroke_width,
        stroke_style* style,
        const matrix_3x2_f* world_transform,
        float flattening_tolerance,
        std::int32_t* contains) const noexcept override
    {
        matrix_3x2_f composed{};
        const com::result status = compose(world_transform, &composed);
        return com::failed(status)
            ? status
            : source_->StrokeContainsPoint(
                  point,
                  stroke_width,
                  style,
                  &composed,
                  flattening_tolerance,
                  contains);
    }

    com::result PROGPU_NATIVE_COM_CALL FillContainsPoint(
        point_2f point,
        const matrix_3x2_f* world_transform,
        float flattening_tolerance,
        std::int32_t* contains) const noexcept override
    {
        matrix_3x2_f composed{};
        const com::result status = compose(world_transform, &composed);
        return com::failed(status)
            ? status
            : source_->FillContainsPoint(
                  point, &composed, flattening_tolerance, contains);
    }

    com::result PROGPU_NATIVE_COM_CALL CompareWithGeometry(
        geometry*,
        const matrix_3x2_f*,
        float,
        geometry_relation* relation) const noexcept override
    {
        if (relation == nullptr) {
            return com::pointer_error;
        }
        *relation = geometry_relation::unknown;
        return not_implemented;
    }

    com::result PROGPU_NATIVE_COM_CALL Simplify(
        geometry_simplification_option option,
        const matrix_3x2_f* world_transform,
        float flattening_tolerance,
        simplified_geometry_sink* sink) const noexcept override
    {
        matrix_3x2_f composed{};
        const com::result status = compose(world_transform, &composed);
        return com::failed(status)
            ? status
            : source_->Simplify(
                  option, &composed, flattening_tolerance, sink);
    }

    com::result PROGPU_NATIVE_COM_CALL Tessellate(
        const matrix_3x2_f* world_transform,
        float flattening_tolerance,
        tessellation_sink* sink) const noexcept override
    {
        matrix_3x2_f composed{};
        const com::result status = compose(world_transform, &composed);
        return com::failed(status)
            ? status
            : source_->Tessellate(&composed, flattening_tolerance, sink);
    }

    com::result PROGPU_NATIVE_COM_CALL CombineWithGeometry(
        geometry*,
        combine_mode,
        const matrix_3x2_f*,
        float,
        simplified_geometry_sink*) const noexcept override
    {
        return not_implemented;
    }

    com::result PROGPU_NATIVE_COM_CALL Outline(
        const matrix_3x2_f* world_transform,
        float flattening_tolerance,
        simplified_geometry_sink* sink) const noexcept override
    {
        matrix_3x2_f composed{};
        const com::result status = compose(world_transform, &composed);
        return com::failed(status)
            ? status
            : source_->Outline(&composed, flattening_tolerance, sink);
    }

    com::result PROGPU_NATIVE_COM_CALL ComputeArea(
        const matrix_3x2_f* world_transform,
        float flattening_tolerance,
        float* area) const noexcept override
    {
        matrix_3x2_f composed{};
        const com::result status = compose(world_transform, &composed);
        return com::failed(status)
            ? status
            : source_->ComputeArea(
                  &composed, flattening_tolerance, area);
    }

    com::result PROGPU_NATIVE_COM_CALL ComputeLength(
        const matrix_3x2_f* world_transform,
        float flattening_tolerance,
        float* length) const noexcept override
    {
        matrix_3x2_f composed{};
        const com::result status = compose(world_transform, &composed);
        return com::failed(status)
            ? status
            : source_->ComputeLength(
                  &composed, flattening_tolerance, length);
    }

    com::result PROGPU_NATIVE_COM_CALL ComputePointAtLength(
        float length,
        const matrix_3x2_f* world_transform,
        float flattening_tolerance,
        point_2f* point,
        point_2f* unit_tangent) const noexcept override
    {
        matrix_3x2_f composed{};
        const com::result status = compose(world_transform, &composed);
        return com::failed(status)
            ? status
            : source_->ComputePointAtLength(
                  length,
                  &composed,
                  flattening_tolerance,
                  point,
                  unit_tangent);
    }

    com::result PROGPU_NATIVE_COM_CALL Widen(
        float stroke_width,
        stroke_style* style,
        const matrix_3x2_f* world_transform,
        float flattening_tolerance,
        simplified_geometry_sink* sink) const noexcept override
    {
        matrix_3x2_f composed{};
        const com::result status = compose(world_transform, &composed);
        return com::failed(status)
            ? status
            : source_->Widen(
                  stroke_width,
                  style,
                  &composed,
                  flattening_tolerance,
                  sink);
    }

    void PROGPU_NATIVE_COM_CALL GetSourceGeometry(geometry** source) const
        noexcept override
    {
        if (source == nullptr) {
            return;
        }
        *source = source_.get();
        if (*source != nullptr) {
            (*source)->AddRef();
        }
    }

    void PROGPU_NATIVE_COM_CALL GetTransform(
        matrix_3x2_f* transform) const noexcept override
    {
        if (transform != nullptr) {
            *transform = transform_;
        }
    }

private:
    [[nodiscard]] com::result compose(
        const matrix_3x2_f* world_transform,
        matrix_3x2_f* composed) const noexcept
    {
        return core::compose_transform(
            transform_, world_transform, composed);
    }

    friend class com::atomic_reference_count<portable_transformed_geometry>;
    ~portable_transformed_geometry() = default;

    com::atomic_reference_count<portable_transformed_geometry>
        reference_count_;
    com::pointer<factory> owner_;
    com::pointer<geometry> source_;
    matrix_3x2_f transform_{};
};

class portable_factory final : public factory {
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
            com::guid_equal(interface_id, factory_interface_id)) {
            *value = static_cast<factory*>(this);
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

    com::result PROGPU_NATIVE_COM_CALL ReloadSystemMetrics()
        noexcept override
    {
        return com::ok;
    }

    void PROGPU_NATIVE_COM_CALL GetDesktopDpi(
        float* dpi_x,
        float* dpi_y) noexcept override
    {
        if (dpi_x != nullptr) {
            *dpi_x = 96.0F;
        }
        if (dpi_y != nullptr) {
            *dpi_y = 96.0F;
        }
    }

    com::result PROGPU_NATIVE_COM_CALL CreateRectangleGeometry(
        const rectangle_f* rectangle,
        rectangle_geometry** value) noexcept override
    {
        if (value == nullptr) {
            return com::pointer_error;
        }
        *value = nullptr;
        if (rectangle == nullptr ||
            !core::rectangle_geometry::valid_rectangle(*rectangle)) {
            return com::invalid_argument;
        }
        auto* created = new (std::nothrow) portable_rectangle_geometry(
            this, *rectangle);
        if (created == nullptr) {
            return com::out_of_memory;
        }
        *value = created;
        return com::ok;
    }

    com::result PROGPU_NATIVE_COM_CALL CreateRoundedRectangleGeometry(
        const rounded_rectangle* rectangle,
        rounded_rectangle_geometry** value) noexcept override
    {
        return detail::create_rounded_rectangle_geometry(
            this, rectangle, value);
    }

    com::result PROGPU_NATIVE_COM_CALL CreateEllipseGeometry(
        const ellipse* ellipse_value,
        ellipse_geometry** value) noexcept override
    {
        return detail::create_ellipse_geometry(this, ellipse_value, value);
    }

    com::result PROGPU_NATIVE_COM_CALL CreateGeometryGroup(
        fill_mode mode,
        geometry** geometries,
        std::uint32_t geometry_count,
        geometry_group** value) noexcept override
    {
        return detail::create_geometry_group(
            this, mode, geometries, geometry_count, value);
    }

    com::result PROGPU_NATIVE_COM_CALL CreateTransformedGeometry(
        geometry* source,
        const matrix_3x2_f* transform,
        transformed_geometry** value) noexcept override
    {
        if (value == nullptr) {
            return com::pointer_error;
        }
        *value = nullptr;
        if (source == nullptr || transform == nullptr ||
            !core::valid_transform(transform)) {
            return com::invalid_argument;
        }
        com::pointer<factory> source_factory;
        factory* raw_source_factory = nullptr;
        source->GetFactory(&raw_source_factory);
        source_factory.attach(raw_source_factory);
        if (!source_factory || source_factory.get() != this) {
            return wrong_factory;
        }
        auto* created = new (std::nothrow) portable_transformed_geometry(
            this, source, *transform);
        if (created == nullptr) {
            return com::out_of_memory;
        }
        *value = created;
        return com::ok;
    }

    com::result PROGPU_NATIVE_COM_CALL CreatePathGeometry(
        path_geometry** value) noexcept override
    {
        return detail::create_path_geometry(this, value);
    }

    com::result PROGPU_NATIVE_COM_CALL CreateStrokeStyle(
        const stroke_style_properties*,
        const float*,
        std::uint32_t,
        stroke_style** value) noexcept override
    {
        return unsupported_output(value);
    }

    com::result PROGPU_NATIVE_COM_CALL CreateDrawingStateBlock(
        const drawing_state_description*,
        com::unknown*,
        drawing_state_block** value) noexcept override
    {
        return unsupported_output(value);
    }

    com::result PROGPU_NATIVE_COM_CALL CreateWicBitmapRenderTarget(
        com::unknown*,
        const render_target_properties*,
        render_target** value) noexcept override
    {
        return unsupported_output(value);
    }

    com::result PROGPU_NATIVE_COM_CALL CreateHwndRenderTarget(
        const render_target_properties*,
        const hwnd_render_target_properties*,
        hwnd_render_target** value) noexcept override
    {
        return unsupported_output(value);
    }

    com::result PROGPU_NATIVE_COM_CALL CreateDxgiSurfaceRenderTarget(
        com::unknown*,
        const render_target_properties*,
        render_target** value) noexcept override
    {
        return unsupported_output(value);
    }

    com::result PROGPU_NATIVE_COM_CALL CreateDCRenderTarget(
        const render_target_properties*,
        dc_render_target** value) noexcept override
    {
        return unsupported_output(value);
    }

private:
    template<typename Interface>
    [[nodiscard]] static com::result unsupported_output(
        Interface** value) noexcept
    {
        if (value == nullptr) {
            return com::pointer_error;
        }
        *value = nullptr;
        return not_implemented;
    }

    friend class com::atomic_reference_count<portable_factory>;
    ~portable_factory() = default;

    com::atomic_reference_count<portable_factory> reference_count_;
};

} // namespace

com::result create_factory(factory** value) noexcept
{
    if (value == nullptr) {
        return com::pointer_error;
    }
    *value = new (std::nothrow) portable_factory();
    return *value == nullptr ? com::out_of_memory : com::ok;
}

} // namespace progpu::native::direct2d::compat
