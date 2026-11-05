#include "progpu_native_direct2d_path.hpp"
#include "../Geometry/progpu_native_path_core.hpp"

#include <algorithm>
#include <array>
#include <atomic>
#include <bit>
#include <cmath>
#include <cstddef>
#include <cstring>
#include <limits>
#include <memory>
#include <mutex>
#include <new>
#include <span>
#include <utility>
#include <vector>

// The Direct2D path geometry: COM objects and their plumbing over the neutral
// Geometry module (src/Geometry/progpu_native_path_core.hpp), which owns the
// recorded path data and every path, Boolean and stroke algorithm. This file
// keeps reference counting, factory identity, the state and argument checks
// that precede the algorithms, Direct2D/neutral type conversion and streaming
// of results into the caller's COM sinks.

namespace progpu::native::direct2d::compat::detail {
namespace {

namespace geom = ::progpu::native::geometry;

static_assert(sizeof(quadratic_bezier_segment) ==
    sizeof(geom::quadratic_bezier_segment_f));
static_assert(offsetof(quadratic_bezier_segment, point2) ==
    offsetof(geom::quadratic_bezier_segment_f, point2));
static_assert(sizeof(triangle) == sizeof(geom::triangle_f));

[[nodiscard]] bool valid_tolerance(float tolerance) noexcept
{
    return std::isfinite(tolerance) && tolerance > 0.0F;
}

// Forwards a Direct2D array to a neutral receiver in bounded converted
// pieces. Every neutral receiver here processes array elements sequentially,
// so piecewise delivery is equivalent to one call.
template<typename Target, typename Source, typename Forward>
void forward_pieces(
    const Source* values, std::uint32_t count, Forward&& forward) noexcept
{
    static_assert(sizeof(Target) == sizeof(Source));
    if (values == nullptr || count == 0U) {
        forward(static_cast<const Target*>(nullptr), count);
        return;
    }
    constexpr std::uint32_t piece_size = 64U;
    std::array<Target, piece_size> piece{};
    for (std::uint32_t offset = 0U; offset < count;) {
        const std::uint32_t piece_count =
            (std::min)(piece_size, count - offset);
        for (std::uint32_t index = 0U; index < piece_count; ++index) {
            piece[index] = std::bit_cast<Target>(values[offset + index]);
        }
        forward(piece.data(), piece_count);
        offset += piece_count;
    }
}

// Forwards a neutral array to a caller's COM sink as one converted array,
// preserving the call shape of the original implementation.
template<typename Target, typename Source, typename Forward>
void forward_whole(
    const Source* values, std::uint32_t count, Forward&& forward) noexcept
{
    static_assert(sizeof(Target) == sizeof(Source));
    if (values == nullptr) {
        forward(static_cast<const Target*>(nullptr), count);
        return;
    }
    constexpr std::uint32_t inline_count = 16U;
    std::array<Target, inline_count> local{};
    if (count <= inline_count) {
        for (std::uint32_t index = 0U; index < count; ++index) {
            local[index] = std::bit_cast<Target>(values[index]);
        }
        forward(local.data(), count);
        return;
    }
    std::vector<Target> converted;
    try {
        converted.resize(count);
    } catch (...) {
        converted.clear();
    }
    if (converted.size() == count) {
        for (std::uint32_t index = 0U; index < count; ++index) {
            converted[index] = std::bit_cast<Target>(values[index]);
        }
        forward(converted.data(), count);
        return;
    }
    // No conversion storage: deliver bounded pieces instead.
    for (std::uint32_t offset = 0U; offset < count;) {
        const std::uint32_t piece_count =
            (std::min)(inline_count, count - offset);
        for (std::uint32_t index = 0U; index < piece_count; ++index) {
            local[index] = std::bit_cast<Target>(values[offset + index]);
        }
        forward(local.data(), piece_count);
        offset += piece_count;
    }
}

// A caller's COM simplified geometry sink as a neutral sink.
class com_simplified_sink final : public geom::simplified_sink {
public:
    explicit com_simplified_sink(simplified_geometry_sink& sink) noexcept
        : sink_(&sink)
    {
    }

    void set_fill_mode(fill_mode value) noexcept override
    {
        sink_->SetFillMode(value);
    }

    void set_segment_flags(path_segment value) noexcept override
    {
        sink_->SetSegmentFlags(value);
    }

    void begin_figure(geom::point_2f start, figure_begin begin)
        noexcept override
    {
        sink_->BeginFigure(core::from_geometry(start), begin);
    }

    void add_lines(const geom::point_2f* points, std::uint32_t point_count)
        noexcept override
    {
        forward_whole<point_2f>(points, point_count,
            [this](const point_2f* converted, std::uint32_t count) {
                sink_->AddLines(converted, count);
            });
    }

    void add_beziers(
        const geom::cubic_bezier_segment_f* beziers,
        std::uint32_t bezier_count) noexcept override
    {
        forward_whole<bezier_segment>(beziers, bezier_count,
            [this](const bezier_segment* converted, std::uint32_t count) {
                sink_->AddBeziers(converted, count);
            });
    }

    void end_figure(figure_end end) noexcept override
    {
        sink_->EndFigure(end);
    }

    [[nodiscard]] geom::status close() noexcept override
    {
        return sink_->Close();
    }

private:
    simplified_geometry_sink* sink_;
};

// A caller's COM geometry sink as a neutral path sink.
class com_path_sink final : public geom::path_sink {
public:
    explicit com_path_sink(geometry_sink& sink) noexcept : sink_(&sink)
    {
    }

    void set_fill_mode(fill_mode value) noexcept override
    {
        sink_->SetFillMode(value);
    }

    void set_segment_flags(path_segment value) noexcept override
    {
        sink_->SetSegmentFlags(value);
    }

    void begin_figure(geom::point_2f start, figure_begin begin)
        noexcept override
    {
        sink_->BeginFigure(core::from_geometry(start), begin);
    }

    void add_lines(const geom::point_2f* points, std::uint32_t point_count)
        noexcept override
    {
        forward_whole<point_2f>(points, point_count,
            [this](const point_2f* converted, std::uint32_t count) {
                sink_->AddLines(converted, count);
            });
    }

    void add_beziers(
        const geom::cubic_bezier_segment_f* beziers,
        std::uint32_t bezier_count) noexcept override
    {
        forward_whole<bezier_segment>(beziers, bezier_count,
            [this](const bezier_segment* converted, std::uint32_t count) {
                sink_->AddBeziers(converted, count);
            });
    }

    void end_figure(figure_end end) noexcept override
    {
        sink_->EndFigure(end);
    }

    [[nodiscard]] geom::status close() noexcept override
    {
        return sink_->Close();
    }

    void add_line(geom::point_2f point) noexcept override
    {
        sink_->AddLine(core::from_geometry(point));
    }

    void add_bezier(const geom::cubic_bezier_segment_f* bezier)
        noexcept override
    {
        if (bezier == nullptr) {
            sink_->AddBezier(nullptr);
            return;
        }
        const bezier_segment value = core::from_geometry(*bezier);
        sink_->AddBezier(&value);
    }

    void add_quadratic_bezier(const geom::quadratic_bezier_segment_f* bezier)
        noexcept override
    {
        if (bezier == nullptr) {
            sink_->AddQuadraticBezier(nullptr);
            return;
        }
        const auto value = std::bit_cast<quadratic_bezier_segment>(*bezier);
        sink_->AddQuadraticBezier(&value);
    }

    void add_quadratic_beziers(
        const geom::quadratic_bezier_segment_f* beziers,
        std::uint32_t bezier_count) noexcept override
    {
        forward_whole<quadratic_bezier_segment>(beziers, bezier_count,
            [this](const quadratic_bezier_segment* converted,
                std::uint32_t count) {
                sink_->AddQuadraticBeziers(converted, count);
            });
    }

    void add_arc(const geom::arc_segment_f* arc) noexcept override
    {
        if (arc == nullptr) {
            sink_->AddArc(nullptr);
            return;
        }
        const arc_segment value = core::from_geometry(*arc);
        sink_->AddArc(&value);
    }

private:
    geometry_sink* sink_;
};

// A neutral collector (or recorder) as a reference-counted COM simplified
// geometry sink, for the output of a COM geometry's Outline or Widen.
template<typename Collector>
class collector_sink final : public simplified_geometry_sink {
public:
    template<typename... Arguments>
    explicit collector_sink(Arguments&&... arguments)
        : collector_(std::forward<Arguments>(arguments)...)
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
        return references_.add_ref();
    }

    com::reference_count_value PROGPU_NATIVE_COM_CALL Release()
        noexcept override
    {
        return references_.release(this);
    }

    void PROGPU_NATIVE_COM_CALL SetFillMode(fill_mode value)
        noexcept override
    {
        collector_.set_fill_mode(value);
    }

    void PROGPU_NATIVE_COM_CALL SetSegmentFlags(path_segment value)
        noexcept override
    {
        collector_.set_segment_flags(value);
    }

    void PROGPU_NATIVE_COM_CALL BeginFigure(
        point_2f start,
        figure_begin begin) noexcept override
    {
        collector_.begin_figure(core::to_geometry(start), begin);
    }

    void PROGPU_NATIVE_COM_CALL AddLines(
        const point_2f* points,
        std::uint32_t point_count) noexcept override
    {
        forward_pieces<geom::point_2f>(points, point_count,
            [this](const geom::point_2f* converted, std::uint32_t count) {
                collector_.add_lines(converted, count);
            });
    }

    void PROGPU_NATIVE_COM_CALL AddBeziers(
        const bezier_segment* beziers,
        std::uint32_t bezier_count) noexcept override
    {
        forward_pieces<geom::cubic_bezier_segment_f>(beziers, bezier_count,
            [this](const geom::cubic_bezier_segment_f* converted,
                std::uint32_t count) {
                collector_.add_beziers(converted, count);
            });
    }

    void PROGPU_NATIVE_COM_CALL EndFigure(figure_end end) noexcept override
    {
        collector_.end_figure(end);
    }

    com::result PROGPU_NATIVE_COM_CALL Close() noexcept override
    {
        return collector_.close();
    }

    [[nodiscard]] Collector& collector() noexcept
    {
        return collector_;
    }

private:
    friend class com::atomic_reference_count<collector_sink>;
    ~collector_sink() = default;

    com::atomic_reference_count<collector_sink> references_;
    Collector collector_;
};

// The values of a stroke style object for the neutral algorithms. Dashes are
// read where the algorithms read them: a custom style with 1 to 2^20 dashes.
struct stroke_values final {
    geom::stroke_style style{};
    std::vector<float> dashes;
};

[[nodiscard]] com::result read_stroke_style(
    stroke_style& source,
    stroke_values& values) noexcept
{
    values.style.properties = {
        source.GetStartCap(),
        source.GetEndCap(),
        source.GetDashCap(),
        source.GetLineJoin(),
        source.GetMiterLimit(),
        source.GetDashStyle(),
        source.GetDashOffset()};
    values.style.dashes = nullptr;
    values.style.dash_count = source.GetDashesCount();
    constexpr std::uint32_t maximum_dash_count = 1U << 20U;
    if (values.style.properties.dash != dash_style::custom ||
        values.style.dash_count == 0U ||
        values.style.dash_count > maximum_dash_count) {
        return com::ok;
    }
    try {
        values.dashes.assign(values.style.dash_count, 0.0F);
    } catch (const std::bad_alloc&) {
        return com::out_of_memory;
    } catch (...) {
        return failure;
    }
    source.GetDashes(values.dashes.data(), values.style.dash_count);
    values.style.dashes = values.dashes.data();
    return com::ok;
}

[[nodiscard]] com::result to_compat_contours(
    std::vector<std::vector<geom::point_2f>>&& source,
    std::vector<std::vector<point_2f>>& contours) noexcept
{
    try {
        std::vector<std::vector<point_2f>> converted;
        converted.reserve(source.size());
        for (const std::vector<geom::point_2f>& contour : source) {
            std::vector<point_2f>& target =
                converted.emplace_back(contour.size());
            if (!contour.empty()) {
                std::memcpy(target.data(), contour.data(),
                    contour.size() * sizeof(point_2f));
            }
        }
        contours = std::move(converted);
        return com::ok;
    } catch (const std::bad_alloc&) {
        return com::out_of_memory;
    } catch (...) {
        return failure;
    }
}

// The recording sink of a path geometry: a COM shell over the neutral path
// recorder, which writes the path data.
class portable_geometry_sink final : public geometry_sink {
public:
    explicit portable_geometry_sink(std::shared_ptr<geom::path_data> data)
        noexcept
        : recorder_(std::move(data))
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
                interface_id, simplified_geometry_sink_interface_id) ||
            com::guid_equal(interface_id, geometry_sink_interface_id)) {
            *value = static_cast<geometry_sink*>(this);
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
        recorder_.set_fill_mode(value);
    }

    void PROGPU_NATIVE_COM_CALL SetSegmentFlags(path_segment value)
        noexcept override
    {
        recorder_.set_segment_flags(value);
    }

    void PROGPU_NATIVE_COM_CALL BeginFigure(
        point_2f start,
        figure_begin begin) noexcept override
    {
        recorder_.begin_figure(core::to_geometry(start), begin);
    }

    void PROGPU_NATIVE_COM_CALL AddLines(
        const point_2f* points,
        std::uint32_t point_count) noexcept override
    {
        forward_pieces<geom::point_2f>(points, point_count,
            [this](const geom::point_2f* converted, std::uint32_t count) {
                recorder_.add_lines(converted, count);
            });
    }

    void PROGPU_NATIVE_COM_CALL AddBeziers(
        const bezier_segment* beziers,
        std::uint32_t bezier_count) noexcept override
    {
        forward_pieces<geom::cubic_bezier_segment_f>(beziers, bezier_count,
            [this](const geom::cubic_bezier_segment_f* converted,
                std::uint32_t count) {
                recorder_.add_beziers(converted, count);
            });
    }

    void PROGPU_NATIVE_COM_CALL EndFigure(figure_end end) noexcept override
    {
        recorder_.end_figure(end);
    }

    com::result PROGPU_NATIVE_COM_CALL Close() noexcept override
    {
        return recorder_.close();
    }

    void PROGPU_NATIVE_COM_CALL AddLine(point_2f point) noexcept override
    {
        recorder_.add_line(core::to_geometry(point));
    }

    void PROGPU_NATIVE_COM_CALL AddBezier(
        const bezier_segment* bezier) noexcept override
    {
        if (bezier == nullptr) {
            recorder_.add_bezier(nullptr);
            return;
        }
        const geom::cubic_bezier_segment_f value = core::to_geometry(*bezier);
        recorder_.add_bezier(&value);
    }

    void PROGPU_NATIVE_COM_CALL AddQuadraticBezier(
        const quadratic_bezier_segment* bezier) noexcept override
    {
        if (bezier == nullptr) {
            recorder_.add_quadratic_bezier(nullptr);
            return;
        }
        const auto value =
            std::bit_cast<geom::quadratic_bezier_segment_f>(*bezier);
        recorder_.add_quadratic_bezier(&value);
    }

    void PROGPU_NATIVE_COM_CALL AddQuadraticBeziers(
        const quadratic_bezier_segment* beziers,
        std::uint32_t bezier_count) noexcept override
    {
        forward_pieces<geom::quadratic_bezier_segment_f>(beziers, bezier_count,
            [this](const geom::quadratic_bezier_segment_f* converted,
                std::uint32_t count) {
                recorder_.add_quadratic_beziers(converted, count);
            });
    }

    void PROGPU_NATIVE_COM_CALL AddArc(const arc_segment* arc)
        noexcept override
    {
        if (arc == nullptr) {
            recorder_.add_arc(nullptr);
            return;
        }
        const geom::arc_segment_f value = core::to_geometry(*arc);
        recorder_.add_arc(&value);
    }

private:
    friend class com::atomic_reference_count<portable_geometry_sink>;
    ~portable_geometry_sink() = default;

    com::atomic_reference_count<portable_geometry_sink> reference_count_;
    geom::path_recorder recorder_;
};

class portable_path_geometry final : public path_geometry {
public:
    explicit portable_path_geometry(factory* owner)
        : owner_(owner), data_(std::make_shared<geom::path_data>())
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
            com::guid_equal(interface_id, path_geometry_interface_id)) {
            *value = static_cast<path_geometry*>(this);
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
        if (bounds == nullptr) {
            return com::pointer_error;
        }
        *bounds = {};
        if (!closed()) {
            return wrong_state;
        }
        geom::matrix_3x2_f transform{};
        return geom::path_bounds(*data_,
            core::to_geometry(world_transform, transform), bounds);
    }

    com::result PROGPU_NATIVE_COM_CALL GetWidenedBounds(
        float stroke_width,
        stroke_style* style,
        const matrix_3x2_f* world_transform,
        float flattening_tolerance,
        rectangle_f* bounds) const noexcept override
    {
        if (bounds == nullptr) {
            return com::pointer_error;
        }
        *bounds = {};
        if (!closed()) {
            return wrong_state;
        }
        if (!std::isfinite(stroke_width) || stroke_width < 0.0F ||
            !valid_tolerance(flattening_tolerance) ||
            !core::valid_transform(world_transform)) {
            return com::invalid_argument;
        }
        stroke_values values;
        if (style != nullptr) {
            if (!same_factory(*style)) {
                return wrong_factory;
            }
            const com::result result = read_stroke_style(*style, values);
            if (com::failed(result)) {
                return result;
            }
        }
        geom::matrix_3x2_f transform{};
        return geom::path_widened_bounds(*data_, stroke_width,
            style != nullptr ? &values.style : nullptr,
            core::to_geometry(world_transform, transform),
            flattening_tolerance, bounds);
    }

    com::result PROGPU_NATIVE_COM_CALL StrokeContainsPoint(
        point_2f point,
        float stroke_width,
        stroke_style* style,
        const matrix_3x2_f* world_transform,
        float flattening_tolerance,
        std::int32_t* contains) const noexcept override
    {
        if (contains == nullptr) {
            return com::pointer_error;
        }
        *contains = 0;
        if (!closed()) {
            return wrong_state;
        }
        if (!std::isfinite(point.x) || !std::isfinite(point.y) ||
            !std::isfinite(stroke_width) || stroke_width < 0.0F ||
            !valid_tolerance(flattening_tolerance) ||
            !core::valid_transform(world_transform)) {
            return com::invalid_argument;
        }
        stroke_values values;
        if (style != nullptr) {
            if (!same_factory(*style)) {
                return wrong_factory;
            }
            const com::result result = read_stroke_style(*style, values);
            if (com::failed(result)) {
                return result;
            }
        }
        geom::matrix_3x2_f transform{};
        return geom::path_stroke_contains_point(*data_,
            core::to_geometry(point), stroke_width,
            style != nullptr ? &values.style : nullptr,
            core::to_geometry(world_transform, transform),
            flattening_tolerance, contains);
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
        if (!closed()) {
            return wrong_state;
        }
        geom::matrix_3x2_f transform{};
        return geom::path_fill_contains_point(*data_,
            core::to_geometry(point),
            core::to_geometry(world_transform, transform),
            flattening_tolerance, contains);
    }

    com::result PROGPU_NATIVE_COM_CALL CompareWithGeometry(
        geometry* input,
        const matrix_3x2_f* input_transform,
        float flattening_tolerance,
        geometry_relation* relation) const noexcept override
    {
        if (relation == nullptr) {
            return com::pointer_error;
        }
        *relation = geometry_relation::unknown;
        if (!closed()) {
            return wrong_state;
        }
        if (input == nullptr ||
            !valid_tolerance(flattening_tolerance) ||
            !core::valid_transform(input_transform)) {
            return com::invalid_argument;
        }
        if (!same_factory(*input)) {
            return wrong_factory;
        }

        try {
            std::vector<std::vector<geom::point_2f>> first;
            com::result result = geom::path_outline_contours(
                *data_, nullptr, flattening_tolerance, first);
            if (com::failed(result)) {
                return result;
            }
            std::vector<std::vector<geom::point_2f>> second;
            result = outline_contours(
                *input, input_transform, flattening_tolerance, second);
            if (com::failed(result)) {
                return result;
            }
            if (first.empty() || second.empty()) {
                return not_implemented;
            }

            return geom::compare_normalized_fill_contours(
                first, second, relation);
        } catch (const std::bad_alloc&) {
            return com::out_of_memory;
        } catch (...) {
            return failure;
        }
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
        if (!closed()) {
            return wrong_state;
        }
        if ((option != geometry_simplification_option::cubics_and_lines &&
                option != geometry_simplification_option::lines) ||
            !valid_tolerance(flattening_tolerance) ||
            !core::valid_transform(world_transform)) {
            return com::invalid_argument;
        }
        com_simplified_sink output(*sink);
        geom::matrix_3x2_f transform{};
        return geom::path_simplify(*data_,
            option == geometry_simplification_option::lines,
            core::to_geometry(world_transform, transform),
            flattening_tolerance, &output);
    }

    com::result PROGPU_NATIVE_COM_CALL Tessellate(
        const matrix_3x2_f* world_transform,
        float flattening_tolerance,
        tessellation_sink* sink) const noexcept override
    {
        if (sink == nullptr) {
            return com::pointer_error;
        }
        if (!closed()) {
            return wrong_state;
        }
        try {
            std::vector<geom::triangle_f> triangles;
            geom::matrix_3x2_f transform{};
            const com::result result = geom::path_tessellate(*data_,
                core::to_geometry(world_transform, transform),
                flattening_tolerance, triangles);
            if (com::failed(result)) {
                return result;
            }
            if (!triangles.empty()) {
                std::vector<triangle> converted(triangles.size());
                std::memcpy(converted.data(), triangles.data(),
                    triangles.size() * sizeof(triangle));
                sink->AddTriangles(
                    converted.data(),
                    static_cast<std::uint32_t>(converted.size()));
            }
            return com::ok;
        } catch (const std::bad_alloc&) {
            return com::out_of_memory;
        } catch (...) {
            return failure;
        }
    }

    com::result PROGPU_NATIVE_COM_CALL CombineWithGeometry(
        geometry* input,
        combine_mode mode,
        const matrix_3x2_f* input_transform,
        float flattening_tolerance,
        simplified_geometry_sink* sink) const noexcept override
    {
        if (sink == nullptr) {
            return com::pointer_error;
        }
        if (!closed()) {
            return wrong_state;
        }
        if (input == nullptr ||
            (mode != combine_mode::union_value &&
                mode != combine_mode::intersect &&
                mode != combine_mode::xor_value &&
                mode != combine_mode::exclude) ||
            !valid_tolerance(flattening_tolerance) ||
            !core::valid_transform(input_transform)) {
            return com::invalid_argument;
        }
        if (!same_factory(*input)) {
            return wrong_factory;
        }

        try {
            std::vector<std::vector<geom::point_2f>> first;
            com::result result = geom::path_outline_contours(
                *data_, nullptr, flattening_tolerance, first);
            if (com::failed(result)) {
                return result;
            }
            std::vector<std::vector<geom::point_2f>> second;
            result = outline_contours(
                *input, input_transform, flattening_tolerance, second);
            if (com::failed(result)) {
                return result;
            }
            com_simplified_sink output(*sink);
            return geom::combine_outline_contours(
                std::move(first), std::move(second), mode, output);
        } catch (const std::bad_alloc&) {
            return com::out_of_memory;
        } catch (...) {
            return failure;
        }
    }

    com::result PROGPU_NATIVE_COM_CALL Outline(
        const matrix_3x2_f* world_transform,
        float flattening_tolerance,
        simplified_geometry_sink* sink) const noexcept override
    {
        if (sink == nullptr) {
            return com::pointer_error;
        }
        if (!closed()) {
            return wrong_state;
        }
        com_simplified_sink output(*sink);
        geom::matrix_3x2_f transform{};
        return geom::path_outline(*data_,
            core::to_geometry(world_transform, transform),
            flattening_tolerance, &output);
    }

    com::result PROGPU_NATIVE_COM_CALL ComputeArea(
        const matrix_3x2_f* world_transform,
        float flattening_tolerance,
        float* area) const noexcept override
    {
        if (area == nullptr) {
            return com::pointer_error;
        }
        *area = 0.0F;
        if (!closed()) {
            return wrong_state;
        }
        geom::matrix_3x2_f transform{};
        return geom::path_compute_area(*data_,
            core::to_geometry(world_transform, transform),
            flattening_tolerance, area);
    }

    com::result PROGPU_NATIVE_COM_CALL ComputeLength(
        const matrix_3x2_f* world_transform,
        float flattening_tolerance,
        float* length) const noexcept override
    {
        if (length == nullptr) {
            return com::pointer_error;
        }
        *length = 0.0F;
        if (!closed()) {
            return wrong_state;
        }
        geom::matrix_3x2_f transform{};
        return geom::path_compute_length(*data_,
            core::to_geometry(world_transform, transform),
            flattening_tolerance, length);
    }

    com::result PROGPU_NATIVE_COM_CALL ComputePointAtLength(
        float length,
        const matrix_3x2_f* world_transform,
        float flattening_tolerance,
        point_2f* point,
        point_2f* tangent) const noexcept override
    {
        if (point == nullptr && tangent == nullptr) {
            return com::pointer_error;
        }
        if (point != nullptr) {
            *point = {};
        }
        if (tangent != nullptr) {
            *tangent = {};
        }
        if (!closed()) {
            return wrong_state;
        }
        geom::point_2f neutral_point{};
        geom::point_2f neutral_tangent{};
        geom::matrix_3x2_f transform{};
        const com::result result = geom::path_compute_point_at_length(
            *data_,
            length,
            core::to_geometry(world_transform, transform),
            flattening_tolerance,
            point != nullptr ? &neutral_point : nullptr,
            tangent != nullptr ? &neutral_tangent : nullptr);
        if (point != nullptr) {
            *point = core::from_geometry(neutral_point);
        }
        if (tangent != nullptr) {
            *tangent = core::from_geometry(neutral_tangent);
        }
        return result;
    }

    com::result PROGPU_NATIVE_COM_CALL Widen(
        float stroke_width,
        stroke_style* style,
        const matrix_3x2_f* world_transform,
        float flattening_tolerance,
        simplified_geometry_sink* sink) const noexcept override
    {
        if (sink == nullptr) {
            return com::pointer_error;
        }
        if (!closed()) {
            return wrong_state;
        }
        if (!std::isfinite(stroke_width) || stroke_width < 0.0F ||
            !valid_tolerance(flattening_tolerance) ||
            !core::valid_transform(world_transform)) {
            return com::invalid_argument;
        }
        if (stroke_width == 0.0F) {
            sink->SetFillMode(fill_mode::winding);
            return com::ok;
        }
        stroke_values values;
        if (style != nullptr) {
            if (!same_factory(*style)) {
                return wrong_factory;
            }
            const com::result result = read_stroke_style(*style, values);
            if (com::failed(result)) {
                return result;
            }
        }
        com_simplified_sink output(*sink);
        geom::matrix_3x2_f transform{};
        return geom::path_widen(*data_, stroke_width,
            style != nullptr ? &values.style : nullptr,
            core::to_geometry(world_transform, transform),
            flattening_tolerance, &output);
    }

    com::result PROGPU_NATIVE_COM_CALL Open(
        geometry_sink** sink) noexcept override
    {
        if (sink == nullptr) {
            return com::pointer_error;
        }
        *sink = nullptr;
        const std::lock_guard lock(data_->mutex);
        if (data_->state.load(std::memory_order_relaxed) !=
            geom::path_state::fresh) {
            return wrong_state;
        }
        auto* created = new (std::nothrow) portable_geometry_sink(data_);
        if (created == nullptr) {
            return com::out_of_memory;
        }
        data_->state.store(geom::path_state::open, std::memory_order_release);
        *sink = created;
        return com::ok;
    }

    com::result PROGPU_NATIVE_COM_CALL Stream(
        geometry_sink* sink) const noexcept override
    {
        if (sink == nullptr) {
            return com::pointer_error;
        }
        if (!closed()) {
            return wrong_state;
        }
        sink->SetFillMode(data_->mode);
        path_segment current_flags = static_cast<path_segment>(
            (std::numeric_limits<std::uint32_t>::max)());
        for (const auto& figure : data_->figures) {
            sink->BeginFigure(core::from_geometry(figure.start), figure.begin);
            for (std::uint32_t offset = 0U;
                 offset < figure.segment_count;
                 ++offset) {
                const auto& segment =
                    data_->segments[figure.first_segment + offset];
                if (segment.flags != current_flags) {
                    sink->SetSegmentFlags(segment.flags);
                    current_flags = segment.flags;
                }
                switch (segment.kind) {
                case geom::segment_kind::line:
                    sink->AddLine(core::from_geometry(segment.end));
                    break;
                case geom::segment_kind::cubic: {
                    const bezier_segment value{
                        core::from_geometry(segment.control1),
                        core::from_geometry(segment.control2),
                        core::from_geometry(segment.end)};
                    sink->AddBezier(&value);
                    break;
                }
                case geom::segment_kind::quadratic: {
                    const quadratic_bezier_segment value{
                        core::from_geometry(segment.control1),
                        core::from_geometry(segment.end)};
                    sink->AddQuadraticBezier(&value);
                    break;
                }
                case geom::segment_kind::arc: {
                    const arc_segment value =
                        core::from_geometry(segment.arc);
                    sink->AddArc(&value);
                    break;
                }
                }
            }
            sink->EndFigure(figure.end);
        }
        return com::ok;
    }

    com::result PROGPU_NATIVE_COM_CALL GetSegmentCount(
        std::uint32_t* count) const noexcept override
    {
        if (count == nullptr) {
            return com::pointer_error;
        }
        *count = 0U;
        if (!closed()) {
            return wrong_state;
        }
        *count = data_->public_segment_count;
        return com::ok;
    }

    com::result PROGPU_NATIVE_COM_CALL GetFigureCount(
        std::uint32_t* count) const noexcept override
    {
        if (count == nullptr) {
            return com::pointer_error;
        }
        *count = 0U;
        if (!closed()) {
            return wrong_state;
        }
        *count = static_cast<std::uint32_t>(data_->figures.size());
        return com::ok;
    }

private:
    [[nodiscard]] bool closed() const noexcept
    {
        return geom::path_closed(*data_);
    }

    [[nodiscard]] bool same_factory(resource& value) const noexcept
    {
        factory* raw_factory = nullptr;
        value.GetFactory(&raw_factory);
        com::pointer<factory> value_factory;
        value_factory.attach(raw_factory);
        return value_factory.get() == owner_.get();
    }

    // The input operand's Outline into the bounded polygon collector.
    [[nodiscard]] static com::result outline_contours(
        geometry& input,
        const matrix_3x2_f* input_transform,
        float flattening_tolerance,
        std::vector<std::vector<geom::point_2f>>& contours) noexcept
    {
        auto* raw_sink = new (std::nothrow)
            collector_sink<geom::polygon_contours_collector>();
        if (raw_sink == nullptr) {
            return com::out_of_memory;
        }
        com::pointer<collector_sink<geom::polygon_contours_collector>> sink;
        sink.attach(raw_sink);
        com::result result = input.Outline(
            input_transform, flattening_tolerance, raw_sink);
        if (com::failed(result)) {
            return result;
        }
        result = raw_sink->collector().result();
        if (com::failed(result)) {
            return result;
        }
        contours = raw_sink->collector().take_contours();
        return com::ok;
    }

    friend class com::atomic_reference_count<portable_path_geometry>;
    ~portable_path_geometry() = default;

    com::atomic_reference_count<portable_path_geometry> reference_count_;
    com::pointer<factory> owner_;
    std::shared_ptr<geom::path_data> data_;
};

// The native fill/stroke figures of canonical segments, recorded through the
// owner's path geometry and its geometry sink.
com::result create_native_geometry(factory* owner,
    std::span<const progpu_native_path_segment> segments, fill_mode mode,
    std::span<const std::uint8_t> smooth_joins, bool filled, bool closed,
    path_geometry** value) noexcept
{
    if (value == nullptr) return com::pointer_error;
    *value = nullptr;
    if (owner == nullptr ||
        !geom::valid_native_path_arguments(segments, mode, smooth_joins, filled))
        return com::invalid_argument;
    com::pointer<path_geometry> path;
    com::result result = owner->CreatePathGeometry(path.put());
    if (com::failed(result)) return result;
    com::pointer<geometry_sink> sink;
    result = path->Open(sink.put());
    if (com::failed(result)) return result;
    com_path_sink output(*sink.get());
    result = geom::write_native_path(
        segments, mode, smooth_joins, filled, closed, output);
    if (com::failed(result)) return result;
    result = sink->Close();
    if (com::failed(result)) return result;
    *value = path.detach();
    return com::ok;
}

} // namespace

com::result create_native_fill_geometry(factory* owner,
    std::span<const progpu_native_path_segment> segments, fill_mode mode,
    path_geometry** value) noexcept
{
    return create_native_geometry(owner, segments, mode, {}, true, true, value);
}

com::result create_native_query_geometry(factory* owner,
    std::span<const progpu_native_geometry_query_figure> figures,
    std::span<const progpu_native_path_segment> segments,
    std::span<const std::uint8_t> flags, path_geometry** value) noexcept
{
    if (value == nullptr) return com::pointer_error;
    *value = nullptr;
    if (owner == nullptr ||
        !geom::valid_native_query_arguments(figures, segments, flags))
        return com::invalid_argument;
    com::pointer<path_geometry> path;
    com::result result = owner->CreatePathGeometry(path.put());
    if (com::failed(result)) return result;
    com::pointer<geometry_sink> sink;
    result = path->Open(sink.put());
    if (com::failed(result)) return result;
    com_path_sink output(*sink.get());
    result = geom::write_native_query_path(figures, segments, flags, output);
    if (com::failed(result)) return result;
    result = sink->Close();
    if (com::failed(result)) return result;
    *value = path.detach();
    return com::ok;
}

com::result combine_native_fill_contours(
    std::span<const progpu_native_path_segment> first, fill_mode first_fill,
    std::span<const progpu_native_path_segment> second, fill_mode second_fill,
    combine_mode mode, float tolerance,
    std::vector<std::vector<point_2f>>& contours) noexcept
{
    std::vector<std::vector<geom::point_2f>> combined;
    const com::result result = geom::combine_native_fill_contours(
        first, first_fill, second, second_fill, mode, tolerance, combined);
    if (com::failed(result)) return result;
    return to_compat_contours(std::move(combined), contours);
}

com::result compare_native_fill_contours(
    std::span<const progpu_native_path_segment> first, fill_mode first_fill,
    std::span<const progpu_native_path_segment> second, fill_mode second_fill,
    float tolerance, geometry_relation& relation) noexcept
{
    return geom::compare_native_fill_contours(
        first, first_fill, second, second_fill, tolerance, relation);
}

com::result get_widened_outline_bounds(geometry* source,
    float width, stroke_style* style, const matrix_3x2_f* transform,
    float tolerance, rectangle_f& bounds, bool& has_outline) noexcept
{
    if (source == nullptr) return com::invalid_argument;
    auto* raw = new (std::nothrow)
        collector_sink<geom::widened_bounds_collector>();
    if (raw == nullptr) return com::out_of_memory;
    com::pointer<collector_sink<geom::widened_bounds_collector>> sink;
    sink.attach(raw);
    com::result result = source->Widen(width, style, transform, tolerance, raw);
    if (com::failed(result)) return result;
    result = raw->Close();
    if (com::failed(result)) return result;
    bounds = raw->collector().bounds();
    has_outline = raw->collector().has_outline();
    return com::ok;
}

com::result create_native_stroke_geometry(factory* owner,
    std::span<const progpu_native_path_segment> segments,
    std::span<const std::uint8_t> smooth_joins, bool closed,
    path_geometry** value) noexcept
{
    return create_native_geometry(owner, segments, fill_mode::winding,
        smooth_joins, false, closed, value);
}

com::result get_widened_outline_segments(geometry* source,
    float width, stroke_style* style, const matrix_3x2_f* transform,
    float tolerance, std::vector<progpu_native_path_segment>& segments,
    fill_mode& mode) noexcept
{
    if (source == nullptr || !valid_tolerance(tolerance)) return com::invalid_argument;
    try {
        // Borrow the production recording sink and widening implementation.
        // Export is sequential contour topology; shared widening retains its
        // SIMD geometry kernels. No new stroke composer or CPU raster path.
        auto data = std::make_shared<geom::path_data>();
        data->state.store(geom::path_state::open, std::memory_order_relaxed);
        com::pointer<collector_sink<geom::path_recorder>> sink;
        sink.attach(new collector_sink<geom::path_recorder>(data));
        auto result = source->Widen(width, style, transform, tolerance, sink.get());
        if (com::failed(result)) return result;
        result = sink->Close();
        if (com::failed(result)) return result;
        return geom::export_outline_segments(*data, tolerance, segments, mode);
    } catch (const std::bad_alloc&) {
        return com::out_of_memory;
    } catch (...) {
        return failure;
    }
}

// Algorithm: Reuse production Outline and its bounded polygon collector.
// Time/space: existing curve flattening/arrangement cost plus O(B) output for
// B boundary points. All intersection/SIMD kernels stay in the shared core.
com::result extract_outline_contours(geometry* source, float tolerance,
    std::vector<std::vector<point_2f>>& contours) noexcept
{
    if (source == nullptr || !valid_tolerance(tolerance)) return com::invalid_argument;
    auto* raw = new (std::nothrow)
        collector_sink<geom::polygon_contours_collector>();
    if (raw == nullptr) return com::out_of_memory;
    com::pointer<collector_sink<geom::polygon_contours_collector>> sink;
    sink.attach(raw);
    com::result result = source->Outline(nullptr, tolerance, raw);
    if (com::failed(result)) return result;
    result = raw->collector().result();
    if (com::failed(result)) return result;
    return to_compat_contours(raw->collector().take_contours(), contours);
}

com::result create_path_geometry(
    factory* owner,
    path_geometry** value) noexcept
{
    if (value == nullptr) {
        return com::pointer_error;
    }
    *value = nullptr;
    if (owner == nullptr) {
        return com::invalid_argument;
    }
    try {
        auto* created = new (std::nothrow) portable_path_geometry(owner);
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
