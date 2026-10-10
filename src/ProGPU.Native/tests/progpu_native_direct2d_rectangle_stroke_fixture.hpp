#pragma once

#include "progpu_native_direct2d_compat.hpp"

#include <array>
#include <algorithm>
#include <cmath>
#include <cstddef>
#include <cstdint>
#include <initializer_list>
#include <limits>

namespace progpu::native::direct2d::tests {

// Solid strokes do not depend on a rectangle's undocumented dash origin.
// The independent path is authored in source order; intrinsic transforms are
// materialized before widening, while the caller world matrix follows widening.
inline constexpr compat::rectangle_f solid_stroke_rectangle{2, 3, 12, 11};
inline constexpr float solid_stroke_width = 4.0F;
inline constexpr float solid_stroke_tolerance = 0.001F;
inline constexpr compat::matrix_3x2_f solid_stroke_world{1, 0, 1, 1, 3, -1};
inline constexpr std::array<compat::matrix_3x2_f, 3U> solid_stroke_intrinsics{{
    {2, 0, 0, 3, 5, -2}, {-1, 0, 0, 1, 18, 0}, {1, 0, 0.5F, 1, 0, 0}}};
inline constexpr std::array<std::array<compat::point_2f, 4U>, 4U> solid_stroke_vertices{{
    {{{2, 3}, {12, 3}, {12, 11}, {2, 11}}},
    {{{9, 7}, {29, 7}, {29, 31}, {9, 31}}},
    {{{16, 3}, {6, 3}, {6, 11}, {16, 11}}},
    {{{3.5F, 3}, {13.5F, 3}, {17.5F, 11}, {7.5F, 11}}}}};
inline constexpr std::array<compat::line_join, 5U> solid_stroke_joins{
    compat::line_join::miter, compat::line_join::bevel, compat::line_join::round,
    compat::line_join::miter_or_bevel, compat::line_join::miter};
inline constexpr std::array<float, 5U> solid_stroke_miter_limits{10, 10, 10, 1, 1};
inline constexpr std::array<compat::point_2f, 20U> solid_stroke_points{{
    {0.25F, 1.25F}, {0.75F, 1.75F}, {0.5F, 2.75F}, {7, 1.5F}, {7, 7}, {14.5F, 7},
    {10.375F, 5.375F}, {20.375F, 5.375F}, {30.375F, 5.375F},
    {7.375F, 15.375F}, {9.375F, 20.375F}, {10.375F, 29.375F},
    {15.375F, 2.375F}, {16.375F, 4.375F}, {5.375F, 4.375F}, {6.375F, 10.375F},
    {2.375F, 5.375F}, {5.375F, 5.375F}, {14.375F, 5.375F}, {17.375F, 10.375F}}};

struct rectangle_solid_stroke_observation final {
    compat::rectangle_f bounds{};
    compat::rectangle_f path_bounds{};
    std::array<std::int32_t, solid_stroke_points.size()> contains{};
    std::array<std::int32_t, solid_stroke_points.size()> widened_contains{};
};

// Five frames (plain, world-only, and three intrinsic+world frames) × five
// joins. The same inventory can be captured through real Windows factory ABI
// pointers for an independent original-versus-portable comparison.
using rectangle_solid_stroke_observations =
    std::array<rectangle_solid_stroke_observation, 25U>;

inline compat::stroke_style_properties rectangle_solid_stroke_style(std::size_t index)
{
    // Deliberately distinct source caps cannot affect a solid closed contour.
    return {compat::cap_style::triangle, compat::cap_style::square,
        compat::cap_style::round, solid_stroke_joins[index],
        solid_stroke_miter_limits[index], compat::dash_style::solid, -0.75F};
}

inline bool same_solid_stroke_bounds(const compat::rectangle_f& a,
    const compat::rectangle_f& b) noexcept
{
    return a.left == b.left && a.top == b.top && a.right == b.right && a.bottom == b.bottom;
}

// A sheared round stroke has curved X extrema. GetWidenedBounds explicitly
// accepts a flattening error; even the original rectangle and original path
// APIs need not return bit-identical approximations. Check each against the
// independent disk support of the four authored source vertices. All straight
// extrema and all other joins retain exact equality.
inline bool round_solid_stroke_bounds(std::size_t frame, const compat::rectangle_f& bounds) noexcept
{
    const auto& vertices = solid_stroke_vertices[frame < 2U ? 0U : frame - 1U];
    double left = std::numeric_limits<double>::infinity(), top = left;
    double right = -left, bottom = -left;
    for (const auto& point : vertices) {
        const double x = frame == 0U ? point.x : static_cast<double>(point.x) + point.y + 3.0;
        const double y = frame == 0U ? point.y : static_cast<double>(point.y) - 1.0;
        left = std::min(left, x); right = std::max(right, x);
        top = std::min(top, y); bottom = std::max(bottom, y);
    }
    const double radius = solid_stroke_width / 2.0;
    if (frame == 0U)
        return same_solid_stroke_bounds(bounds, {static_cast<float>(left - radius),
            static_cast<float>(top - radius), static_cast<float>(right + radius),
            static_cast<float>(bottom + radius)});
    const double support = radius * std::sqrt(2.0);
    return bounds.top == top - radius && bounds.bottom == bottom + radius &&
        std::abs(static_cast<double>(bounds.left) - (left - support)) <= solid_stroke_tolerance &&
        std::abs(static_cast<double>(bounds.right) - (right + support)) <= solid_stroke_tolerance;
}

inline bool matching_solid_stroke_bounds(std::size_t frame, std::size_t join,
    const compat::rectangle_f& a, const compat::rectangle_f& b) noexcept
{
    return join == 2U ? round_solid_stroke_bounds(frame, a) && round_solid_stroke_bounds(frame, b)
        : same_solid_stroke_bounds(a, b);
}

template<class Require>
com::pointer<compat::path_geometry> rectangle_solid_stroke_path(compat::factory* factory,
    const std::array<compat::point_2f, 4U>& vertices, Require require)
{
    com::pointer<compat::path_geometry> path;
    com::pointer<compat::geometry_sink> sink;
    require(factory->CreatePathGeometry(path.put()) == com::ok &&
        path->Open(sink.put()) == com::ok, "solid rectangle independent path creation failed");
    sink->SetFillMode(compat::fill_mode::winding);
    sink->BeginFigure(vertices[0], compat::figure_begin::filled);
    sink->AddLines(vertices.data() + 1U, 3U);
    sink->EndFigure(compat::figure_end::closed);
    require(sink->Close() == com::ok, "solid rectangle independent path close failed");
    return path;
}

template<class Require>
com::pointer<compat::path_geometry> rectangle_solid_stroke_widen(compat::factory* factory,
    compat::geometry* geometry, compat::stroke_style* style,
    const compat::matrix_3x2_f* world, Require require)
{
    com::pointer<compat::path_geometry> result;
    com::pointer<compat::geometry_sink> sink;
    require(factory->CreatePathGeometry(result.put()) == com::ok &&
        result->Open(sink.put()) == com::ok, "solid rectangle widened destination creation failed");
    require(geometry->Widen(solid_stroke_width, style, world, solid_stroke_tolerance,
        sink.get()) == com::ok && sink->Close() == com::ok,
        "solid rectangle widening failed");
    return result;
}

template<class Require>
rectangle_solid_stroke_observations verify_rectangle_solid_strokes(
    compat::factory* factory, Require require)
{
    com::pointer<compat::rectangle_geometry> rectangle;
    require(factory->CreateRectangleGeometry(&solid_stroke_rectangle, rectangle.put()) == com::ok,
        "solid rectangle creation failed");
    rectangle_solid_stroke_observations observations{};
    for (std::size_t frame = 0U; frame < 5U; ++frame) {
        const std::size_t vertices_index = frame < 2U ? 0U : frame - 1U;
        const compat::matrix_3x2_f* world = frame == 0U ? nullptr : &solid_stroke_world;
        com::pointer<compat::transformed_geometry> transformed;
        compat::geometry* current = rectangle.get();
        if (frame >= 2U) {
            require(factory->CreateTransformedGeometry(rectangle.get(),
                &solid_stroke_intrinsics[frame - 2U], transformed.put()) == com::ok,
                "solid rectangle intrinsic transform creation failed");
            current = transformed.get();
        }
        auto independent = rectangle_solid_stroke_path(factory,
            solid_stroke_vertices[vertices_index], require);
        for (std::size_t join = 0U; join < solid_stroke_joins.size(); ++join) {
            const auto properties = rectangle_solid_stroke_style(join);
            com::pointer<compat::stroke_style> style;
            require(factory->CreateStrokeStyle(&properties, nullptr, 0U, style.put()) == com::ok,
                "solid rectangle stroke style creation failed");
            auto& output = observations[frame * solid_stroke_joins.size() + join];
            require(current->GetWidenedBounds(solid_stroke_width, style.get(), world,
                solid_stroke_tolerance, &output.bounds) == com::ok &&
                independent->GetWidenedBounds(solid_stroke_width, style.get(), world,
                    solid_stroke_tolerance, &output.path_bounds) == com::ok,
                "solid rectangle/path bounds query failed");
            require(matching_solid_stroke_bounds(frame, join, output.bounds, output.path_bounds),
                "solid rectangle bounds differ from independently materialized source path");
            auto widened = rectangle_solid_stroke_widen(factory, current, style.get(), world, require);
            auto path_widened = rectangle_solid_stroke_widen(factory, independent.get(), style.get(), world, require);
            for (std::size_t i = 0U; i < solid_stroke_points.size(); ++i) {
                compat::point_2f point = solid_stroke_points[i];
                if (world != nullptr) point = {point.x + point.y + 3.0F, point.y - 1.0F};
                std::int32_t path_contains = 0, path_widened_contains = 0;
                require(current->StrokeContainsPoint(point, solid_stroke_width, style.get(), world,
                    solid_stroke_tolerance, &output.contains[i]) == com::ok &&
                    independent->StrokeContainsPoint(point, solid_stroke_width, style.get(), world,
                        solid_stroke_tolerance, &path_contains) == com::ok &&
                    widened->FillContainsPoint(point, nullptr, solid_stroke_tolerance,
                        &output.widened_contains[i]) == com::ok &&
                    path_widened->FillContainsPoint(point, nullptr, solid_stroke_tolerance,
                        &path_widened_contains) == com::ok,
                    "solid rectangle/path containment query failed");
                require(output.contains[i] == path_contains && output.contains[i] == output.widened_contains[i] &&
                    output.contains[i] == path_widened_contains,
                    "solid rectangle stroke and widened filled coverage disagree");
            }
            if (frame == 0U) {
                constexpr std::array<std::array<std::int32_t, 6U>, 5U> corner_expectations{{
                    {{1, 1, 1, 1, 0, 0}}, {{0, 0, 1, 1, 0, 0}}, {{0, 1, 1, 1, 0, 0}},
                    {{0, 0, 1, 1, 0, 0}}, {{0, 1, 1, 1, 0, 0}}}};
                for (std::size_t i = 0U; i < corner_expectations[join].size(); ++i)
                    require(output.contains[i] == corner_expectations[join][i],
                        "solid rectangle literal corner/body/empty-center coverage differs");
                require(same_solid_stroke_bounds(output.bounds, {0, 1, 14, 13}),
                    "solid rectangle identity bounds differ from the literal edge extrema");
            }
            if (frame == 1U && (join == 0U || join == 1U || join == 3U)) {
                const compat::rectangle_f literal = join == 0U
                    ? compat::rectangle_f{4, 0, 30, 12} : compat::rectangle_f{6, 0, 28, 12};
                require(same_solid_stroke_bounds(output.bounds, literal),
                    "solid rectangle shear bounds ignored its actual join support");
            }
        }
    }
    return observations;
}

// Rejecting calls must not touch even sink state, not merely emit no figures.
class rectangle_stroke_sink_probe final : public compat::simplified_geometry_sink {
public:
    com::result PROGPU_NATIVE_COM_CALL QueryInterface(com::guid_ref id, void** value) noexcept override
    {
        if (value == nullptr) return com::pointer_error;
        *value = nullptr;
        if (!com::guid_equal(id, com::unknown_interface_id()) &&
            !com::guid_equal(id, compat::simplified_geometry_sink_interface_id)) return com::no_interface;
        *value = static_cast<compat::simplified_geometry_sink*>(this);
        AddRef();
        return com::ok;
    }
    com::reference_count_value PROGPU_NATIVE_COM_CALL AddRef() noexcept override { return references_.add_ref(); }
    com::reference_count_value PROGPU_NATIVE_COM_CALL Release() noexcept override { return references_.release(this); }
    void PROGPU_NATIVE_COM_CALL SetFillMode(compat::fill_mode) noexcept override { ++calls; }
    void PROGPU_NATIVE_COM_CALL SetSegmentFlags(compat::path_segment) noexcept override { ++calls; }
    void PROGPU_NATIVE_COM_CALL BeginFigure(compat::point_2f, compat::figure_begin) noexcept override { ++calls; }
    void PROGPU_NATIVE_COM_CALL AddLines(const compat::point_2f*, std::uint32_t) noexcept override { ++calls; }
    void PROGPU_NATIVE_COM_CALL AddBeziers(const compat::bezier_segment*, std::uint32_t) noexcept override { ++calls; }
    void PROGPU_NATIVE_COM_CALL EndFigure(compat::figure_end) noexcept override { ++calls; }
    com::result PROGPU_NATIVE_COM_CALL Close() noexcept override { ++calls; return com::ok; }
    std::uint32_t calls = 0U;
private:
    friend class com::atomic_reference_count<rectangle_stroke_sink_probe>;
    ~rectangle_stroke_sink_probe() = default;
    com::atomic_reference_count<rectangle_stroke_sink_probe> references_;
};

template<class Require>
void verify_rectangle_solid_stroke_rejections(compat::factory* factory,
    compat::factory* foreign_factory, Require require)
{
    com::pointer<compat::rectangle_geometry> rectangle, degenerate;
    const compat::rectangle_f flat{2, 3, 2, 11};
    require(factory != foreign_factory &&
        factory->CreateRectangleGeometry(&solid_stroke_rectangle, rectangle.put()) == com::ok &&
        factory->CreateRectangleGeometry(&flat, degenerate.put()) == com::ok,
        "rectangle stroke rejection fixture setup failed");
    auto properties = rectangle_solid_stroke_style(0U);
    com::pointer<compat::stroke_style> solid, foreign, dashed;
    require(factory->CreateStrokeStyle(&properties, nullptr, 0U, solid.put()) == com::ok &&
        foreign_factory->CreateStrokeStyle(&properties, nullptr, 0U, foreign.put()) == com::ok,
        "rectangle stroke rejection solid styles failed");
    properties.dash = compat::dash_style::dash;
    require(factory->CreateStrokeStyle(&properties, nullptr, 0U, dashed.put()) == com::ok,
        "rectangle stroke rejection dash style failed");
    com::pointer<rectangle_stroke_sink_probe> sink;
    sink.attach(new rectangle_stroke_sink_probe());
    const auto rejects = [&](compat::geometry* geometry, float width, compat::stroke_style* style,
        const compat::matrix_3x2_f* world, float tolerance, com::result expected) {
        compat::rectangle_f bounds{99, 98, 97, 96};
        std::int32_t contains = 77;
        require(geometry->GetWidenedBounds(width, style, world, tolerance, &bounds) == expected &&
            same_solid_stroke_bounds(bounds, {}), "rejected rectangle bounds published stale/partial output");
        require(geometry->StrokeContainsPoint({7, 3}, width, style, world, tolerance, &contains) == expected &&
            contains == 0, "rejected rectangle hit published stale/partial output");
        require(geometry->Widen(width, style, world, tolerance, sink.get()) == expected && sink->calls == 0U,
            "rejected rectangle widening touched the destination sink");
    };
    for (const float width : {4.0F, 0.0F})
        rejects(rectangle.get(), width, foreign.get(), nullptr, solid_stroke_tolerance, compat::wrong_factory);
    for (const float width : {-1.0F, std::numeric_limits<float>::infinity(),
            std::numeric_limits<float>::quiet_NaN()})
        rejects(rectangle.get(), width, solid.get(), nullptr, solid_stroke_tolerance, com::invalid_argument);
    for (const float tolerance : {0.0F, std::numeric_limits<float>::infinity(),
            std::numeric_limits<float>::quiet_NaN()})
        rejects(rectangle.get(), 4.0F, solid.get(), nullptr, tolerance, com::invalid_argument);
    const compat::matrix_3x2_f invalid{1, 0, 0, 1, std::numeric_limits<float>::infinity(), 0};
    rejects(rectangle.get(), 4.0F, solid.get(), &invalid, solid_stroke_tolerance, com::invalid_argument);
    rejects(rectangle.get(), 4.0F, dashed.get(), nullptr, solid_stroke_tolerance, compat::not_implemented);
    rejects(degenerate.get(), 4.0F, solid.get(), nullptr, solid_stroke_tolerance, compat::not_implemented);
    const compat::matrix_3x2_f collapsed{1, 0, 0, 0, 0, 0};
    com::pointer<compat::transformed_geometry> singular;
    require(factory->CreateTransformedGeometry(rectangle.get(), &collapsed, singular.put()) == com::ok,
        "rectangle collapsed intrinsic fixture creation failed");
    rejects(singular.get(), 4.0F, solid.get(), nullptr, solid_stroke_tolerance, compat::not_implemented);

    // The pre-existing explicit-solid zero-width bounds operation remains
    // available, but neither containment nor Widen acquires a new zero contract.
    compat::rectangle_f zero_bounds{};
    std::int32_t contains = 77;
    require(rectangle->GetWidenedBounds(0, solid.get(), nullptr, solid_stroke_tolerance,
        &zero_bounds) == com::ok && same_solid_stroke_bounds(zero_bounds, solid_stroke_rectangle),
        "existing zero-width explicit-solid rectangle bounds changed");
    require(rectangle->StrokeContainsPoint({7, 3}, 0, solid.get(), nullptr, solid_stroke_tolerance,
        &contains) == compat::not_implemented && contains == 0 &&
        rectangle->Widen(0, solid.get(), nullptr, solid_stroke_tolerance, sink.get()) == compat::not_implemented &&
        sink->calls == 0U, "explicit-style zero-width rectangle admission changed");
    require(rectangle->GetWidenedBounds(4, solid.get(), nullptr, solid_stroke_tolerance, nullptr) == com::pointer_error &&
        rectangle->StrokeContainsPoint({7, 3}, 4, solid.get(), nullptr, solid_stroke_tolerance, nullptr) == com::pointer_error &&
        rectangle->Widen(4, solid.get(), nullptr, solid_stroke_tolerance, nullptr) == com::pointer_error,
        "rectangle stroke null-output error contract changed");

    // Device-dependent style1 modes must not enter the new normal-style path.
    // Their old base bounds remain available; this is not original DPI parity.
    const auto normal_properties = rectangle_solid_stroke_style(0U);
    for (const auto mode : {compat::stroke_transform_type::fixed,
            compat::stroke_transform_type::hairline}) {
        com::pointer<compat::stroke_style1> extended;
        require(compat::create_stroke_style1(factory, &normal_properties, mode,
            nullptr, 0U, extended.put()) == com::ok,
            "rectangle fixed/hairline retained-policy style creation failed");
        compat::rectangle_f bounds{};
        contains = 77;
        require(rectangle->GetWidenedBounds(4, extended.get(), nullptr, solid_stroke_tolerance,
                &bounds) == com::ok && same_solid_stroke_bounds(bounds, {0, 1, 14, 13}) &&
            rectangle->StrokeContainsPoint({7, 3}, 4, extended.get(), nullptr,
                solid_stroke_tolerance, &contains) == compat::not_implemented && contains == 0 &&
            rectangle->Widen(4, extended.get(), nullptr, solid_stroke_tolerance,
                sink.get()) == compat::not_implemented && sink->calls == 0U,
            "rectangle solid path widened fixed/hairline admission");
    }
}

} // namespace progpu::native::direct2d::tests
