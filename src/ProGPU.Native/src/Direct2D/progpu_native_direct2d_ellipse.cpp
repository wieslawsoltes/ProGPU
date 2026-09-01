#include "progpu_native_direct2d_ellipse.hpp"
#include "progpu_native_direct2d_path.hpp"

#include <array>
#include <atomic>
#include <new>

namespace progpu::native::direct2d::compat::detail {
namespace {

class portable_ellipse_geometry final : public ellipse_geometry {
public:
    portable_ellipse_geometry(
        factory* owner,
        const ellipse& value,
        path_geometry* path) noexcept
        : owner_(owner), value_(value), path_(path)
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
            com::guid_equal(interface_id, ellipse_geometry_interface_id)) {
            *value = static_cast<ellipse_geometry*>(this);
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
        return path_->GetBounds(world_transform, bounds);
    }

    com::result PROGPU_NATIVE_COM_CALL GetWidenedBounds(
        float stroke_width,
        stroke_style* style,
        const matrix_3x2_f* world_transform,
        float flattening_tolerance,
        rectangle_f* bounds) const noexcept override
    {
        return path_->GetWidenedBounds(
            stroke_width,
            style,
            world_transform,
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
        return path_->StrokeContainsPoint(
            point,
            stroke_width,
            style,
            world_transform,
            flattening_tolerance,
            contains);
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
        *contains = 0;
        std::uint32_t core_contains = 0U;
        const com::result result = core::ellipse_fill_contains_point(
            value_,
            point,
            world_transform,
            flattening_tolerance,
            &core_contains);
        if (com::succeeded(result)) {
            *contains = core_contains == 0U ? 0 : 1;
        }
        return result;
    }

    com::result PROGPU_NATIVE_COM_CALL CompareWithGeometry(
        geometry* input,
        const matrix_3x2_f* transform,
        float tolerance,
        geometry_relation* relation) const noexcept override
    {
        return path_->CompareWithGeometry(
            input, transform, tolerance, relation);
    }

    com::result PROGPU_NATIVE_COM_CALL Simplify(
        geometry_simplification_option option,
        const matrix_3x2_f* transform,
        float tolerance,
        simplified_geometry_sink* sink) const noexcept override
    {
        return path_->Simplify(option, transform, tolerance, sink);
    }

    com::result PROGPU_NATIVE_COM_CALL Tessellate(
        const matrix_3x2_f* transform,
        float tolerance,
        tessellation_sink* sink) const noexcept override
    {
        return path_->Tessellate(transform, tolerance, sink);
    }

    com::result PROGPU_NATIVE_COM_CALL CombineWithGeometry(
        geometry* input,
        combine_mode mode,
        const matrix_3x2_f* transform,
        float tolerance,
        simplified_geometry_sink* sink) const noexcept override
    {
        return path_->CombineWithGeometry(
            input, mode, transform, tolerance, sink);
    }

    com::result PROGPU_NATIVE_COM_CALL Outline(
        const matrix_3x2_f* transform,
        float tolerance,
        simplified_geometry_sink* sink) const noexcept override
    {
        return path_->Outline(transform, tolerance, sink);
    }

    com::result PROGPU_NATIVE_COM_CALL ComputeArea(
        const matrix_3x2_f* transform,
        float tolerance,
        float* area) const noexcept override
    {
        return path_->ComputeArea(transform, tolerance, area);
    }

    com::result PROGPU_NATIVE_COM_CALL ComputeLength(
        const matrix_3x2_f* transform,
        float tolerance,
        float* length) const noexcept override
    {
        return path_->ComputeLength(transform, tolerance, length);
    }

    com::result PROGPU_NATIVE_COM_CALL ComputePointAtLength(
        float length,
        const matrix_3x2_f* transform,
        float tolerance,
        point_2f* point,
        point_2f* tangent) const noexcept override
    {
        return path_->ComputePointAtLength(
            length, transform, tolerance, point, tangent);
    }

    com::result PROGPU_NATIVE_COM_CALL Widen(
        float stroke_width,
        stroke_style* style,
        const matrix_3x2_f* transform,
        float tolerance,
        simplified_geometry_sink* sink) const noexcept override
    {
        return path_->Widen(
            stroke_width, style, transform, tolerance, sink);
    }

    void PROGPU_NATIVE_COM_CALL GetEllipse(ellipse* value) const
        noexcept override
    {
        if (value != nullptr) {
            *value = value_;
        }
    }

private:
    friend class com::atomic_reference_count<portable_ellipse_geometry>;
    ~portable_ellipse_geometry() = default;

    com::atomic_reference_count<portable_ellipse_geometry> reference_count_;
    com::pointer<factory> owner_;
    ellipse value_{};
    com::pointer<path_geometry> path_;
};

} // namespace

com::result create_ellipse_geometry(
    factory* owner,
    const ellipse* value,
    ellipse_geometry** geometry_value) noexcept
{
    if (geometry_value == nullptr) {
        return com::pointer_error;
    }
    *geometry_value = nullptr;
    if (owner == nullptr || value == nullptr || !core::valid_ellipse(*value)) {
        return com::invalid_argument;
    }

    path_geometry* raw_path = nullptr;
    com::result result = create_path_geometry(owner, &raw_path);
    if (com::failed(result)) {
        return result;
    }
    com::pointer<path_geometry> path;
    path.attach(raw_path);
    geometry_sink* raw_sink = nullptr;
    result = path->Open(&raw_sink);
    if (com::failed(result)) {
        return result;
    }
    com::pointer<geometry_sink> sink;
    sink.attach(raw_sink);

    point_2f start{};
    std::array<bezier_segment, 4U> cubics{};
    result = core::ellipse_to_cubics(*value, &start, &cubics);
    if (com::failed(result)) {
        static_cast<void>(sink->Close());
        return result;
    }
    sink->SetFillMode(fill_mode::winding);
    sink->SetSegmentFlags(path_segment::none);
    sink->BeginFigure(start, figure_begin::filled);
    sink->AddBeziers(cubics.data(), static_cast<std::uint32_t>(cubics.size()));
    sink->EndFigure(figure_end::closed);
    result = sink->Close();
    if (com::failed(result)) {
        return result;
    }

    auto* created = new (std::nothrow) portable_ellipse_geometry(
        owner, *value, path.get());
    if (created == nullptr) {
        return com::out_of_memory;
    }
    *geometry_value = created;
    return com::ok;
}

} // namespace progpu::native::direct2d::compat::detail
