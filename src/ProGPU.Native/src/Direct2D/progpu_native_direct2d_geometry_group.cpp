#include "progpu_native_direct2d_geometry_group.hpp"
#include "progpu_native_direct2d_path.hpp"

#include <algorithm>
#include <atomic>
#include <new>
#include <utility>
#include <vector>

namespace progpu::native::direct2d::compat::detail {
namespace {

[[nodiscard]] bool valid_fill_mode(fill_mode value) noexcept
{
    return value == fill_mode::alternate || value == fill_mode::winding;
}

[[nodiscard]] bool geometry_contains_group(
    geometry* value,
    std::uint32_t depth = 0U) noexcept
{
    if (value == nullptr || depth == 64U) {
        return true;
    }
    void* queried = nullptr;
    if (com::succeeded(value->QueryInterface(
            geometry_group_interface_id, &queried))) {
        static_cast<geometry_group*>(queried)->Release();
        return true;
    }
    queried = nullptr;
    if (com::succeeded(value->QueryInterface(
            transformed_geometry_interface_id, &queried))) {
        auto* transformed = static_cast<transformed_geometry*>(queried);
        geometry* source = nullptr;
        transformed->GetSourceGeometry(&source);
        transformed->Release();
        com::pointer<geometry> retained_source;
        retained_source.attach(source);
        return geometry_contains_group(retained_source.get(), depth + 1U);
    }
    return false;
}

class group_geometry_sink final : public simplified_geometry_sink {
public:
    explicit group_geometry_sink(geometry_sink* target) noexcept
        : target_(target)
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
            com::guid_equal(
                interface_id, simplified_geometry_sink_interface_id)) {
            *value = static_cast<simplified_geometry_sink*>(this);
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

    void PROGPU_NATIVE_COM_CALL SetFillMode(fill_mode value)
        noexcept override
    {
        if (!valid_fill_mode(value)) {
            fail(com::invalid_argument);
        }
    }

    void PROGPU_NATIVE_COM_CALL SetSegmentFlags(path_segment value)
        noexcept override
    {
        if (com::succeeded(failure_)) {
            target_->SetSegmentFlags(value);
        }
    }

    void PROGPU_NATIVE_COM_CALL BeginFigure(
        point_2f start,
        figure_begin begin) noexcept override
    {
        if (com::succeeded(failure_)) {
            target_->BeginFigure(start, begin);
        }
    }

    void PROGPU_NATIVE_COM_CALL AddLines(
        const point_2f* points,
        std::uint32_t point_count) noexcept override
    {
        if (com::succeeded(failure_)) {
            target_->AddLines(points, point_count);
        }
    }

    void PROGPU_NATIVE_COM_CALL AddBeziers(
        const bezier_segment* beziers,
        std::uint32_t bezier_count) noexcept override
    {
        if (com::succeeded(failure_)) {
            target_->AddBeziers(beziers, bezier_count);
        }
    }

    void PROGPU_NATIVE_COM_CALL EndFigure(figure_end end)
        noexcept override
    {
        if (com::succeeded(failure_)) {
            target_->EndFigure(end);
        }
    }

    com::result PROGPU_NATIVE_COM_CALL Close() noexcept override
    {
        fail(wrong_state);
        return failure_;
    }

    [[nodiscard]] com::result failure_value() const noexcept
    {
        return failure_;
    }

private:
    friend class com::atomic_reference_count<group_geometry_sink>;
    ~group_geometry_sink() = default;

    void fail(com::result value) noexcept
    {
        if (com::succeeded(failure_)) {
            failure_ = value;
        }
    }

    com::atomic_reference_count<group_geometry_sink> reference_count_;
    com::pointer<geometry_sink> target_;
    com::result failure_ = com::ok;
};

class portable_geometry_group final : public geometry_group {
public:
    portable_geometry_group(
        factory* owner,
        fill_mode mode,
        std::vector<com::pointer<geometry>>&& sources,
        path_geometry* path) noexcept
        : owner_(owner),
          mode_(mode),
          sources_(std::move(sources)),
          path_(path)
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
            com::guid_equal(interface_id, geometry_group_interface_id)) {
            *value = static_cast<geometry_group*>(this);
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
        const matrix_3x2_f* transform,
        rectangle_f* bounds) const noexcept override
    {
        return path_->GetBounds(transform, bounds);
    }

    com::result PROGPU_NATIVE_COM_CALL GetWidenedBounds(
        float stroke_width,
        stroke_style* style,
        const matrix_3x2_f* transform,
        float tolerance,
        rectangle_f* bounds) const noexcept override
    {
        return path_->GetWidenedBounds(
            stroke_width, style, transform, tolerance, bounds);
    }

    com::result PROGPU_NATIVE_COM_CALL StrokeContainsPoint(
        point_2f point,
        float stroke_width,
        stroke_style* style,
        const matrix_3x2_f* transform,
        float tolerance,
        std::int32_t* contains) const noexcept override
    {
        return path_->StrokeContainsPoint(
            point, stroke_width, style, transform, tolerance, contains);
    }

    com::result PROGPU_NATIVE_COM_CALL FillContainsPoint(
        point_2f point,
        const matrix_3x2_f* transform,
        float tolerance,
        std::int32_t* contains) const noexcept override
    {
        return path_->FillContainsPoint(
            point, transform, tolerance, contains);
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

    fill_mode PROGPU_NATIVE_COM_CALL GetFillMode() const
        noexcept override
    {
        return mode_;
    }

    std::uint32_t PROGPU_NATIVE_COM_CALL GetSourceGeometryCount() const
        noexcept override
    {
        return static_cast<std::uint32_t>(sources_.size());
    }

    void PROGPU_NATIVE_COM_CALL GetSourceGeometries(
        geometry** geometries,
        std::uint32_t geometry_count) const noexcept override
    {
        if (geometries == nullptr) {
            return;
        }
        const std::size_t count = std::min(
            static_cast<std::size_t>(geometry_count), sources_.size());
        for (std::size_t index = 0U; index < count; ++index) {
            geometries[index] = sources_[index].get();
            geometries[index]->AddRef();
        }
    }

private:
    friend class com::atomic_reference_count<portable_geometry_group>;
    ~portable_geometry_group() = default;

    com::atomic_reference_count<portable_geometry_group> reference_count_;
    com::pointer<factory> owner_;
    fill_mode mode_ = fill_mode::alternate;
    std::vector<com::pointer<geometry>> sources_;
    com::pointer<path_geometry> path_;
};

} // namespace

com::result create_geometry_group(
    factory* owner,
    fill_mode mode,
    geometry** geometries,
    std::uint32_t geometry_count,
    geometry_group** value) noexcept
{
    if (value == nullptr) {
        return com::pointer_error;
    }
    *value = nullptr;
    if (owner == nullptr || !valid_fill_mode(mode) ||
        (geometry_count != 0U && geometries == nullptr)) {
        return com::invalid_argument;
    }

    try {
        std::vector<com::pointer<geometry>> sources;
        sources.reserve(geometry_count);
        for (std::uint32_t index = 0U; index < geometry_count; ++index) {
            geometry* source = geometries[index];
            if (source == nullptr) {
                return com::invalid_argument;
            }
            factory* raw_source_factory = nullptr;
            source->GetFactory(&raw_source_factory);
            com::pointer<factory> source_factory;
            source_factory.attach(raw_source_factory);
            if (!source_factory || source_factory.get() != owner) {
                return wrong_factory;
            }
            if (geometry_contains_group(source)) {
                return not_implemented;
            }
            sources.emplace_back(source);
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
        sink->SetFillMode(mode);

        auto* raw_group_sink = new (std::nothrow)
            group_geometry_sink(sink.get());
        if (raw_group_sink == nullptr) {
            static_cast<void>(sink->Close());
            return com::out_of_memory;
        }
        com::pointer<simplified_geometry_sink> group_sink;
        group_sink.attach(raw_group_sink);
        for (const auto& source : sources) {
            result = source->Simplify(
                geometry_simplification_option::cubics_and_lines,
                nullptr,
                core::default_flattening_tolerance,
                group_sink.get());
            if (com::succeeded(result)) {
                result = raw_group_sink->failure_value();
            }
            if (com::failed(result)) {
                static_cast<void>(sink->Close());
                return result;
            }
        }
        result = sink->Close();
        if (com::failed(result)) {
            return result;
        }

        auto* created = new (std::nothrow) portable_geometry_group(
            owner, mode, std::move(sources), path.get());
        if (created == nullptr) {
            return com::out_of_memory;
        }
        *value = created;
        return com::ok;
    } catch (const std::bad_alloc&) {
        return com::out_of_memory;
    } catch (...) {
        return failure;
    }
}

} // namespace progpu::native::direct2d::compat::detail
