#pragma once

#include "progpu_native_direct2d_copy_fixture.hpp"
#include "progpu_native_scene_builder.hpp"

#include <algorithm>
#include <array>
#include <cmath>
#include <cstdio>
#include <limits>

namespace progpu::native::direct2d::tests {

// Explicit source order and explicit independent stable order. The two stops
// at 0.5 own opposite sides of the hard edge; sorting must never exchange them.
inline constexpr std::array<compat::gradient_stop, 6U> unordered_gradient_stops{{
    {2, {0, 0, 1, 1}}, {0.5F, {1, 0, 0, 1}}, {-1, {1, 0, 0, 1}},
    {0.5F, {0, 0, 1, 1}}, {0, {1, 0, 0, 1}}, {1, {0, 0, 1, 1}}}};
inline constexpr std::array<std::size_t, 6U> ordered_gradient_indices{2, 4, 1, 3, 5, 0};
inline constexpr unsigned gradient_stop_variant_count = 13U;

inline bool same_gradient_stop(const compat::gradient_stop& left, const compat::gradient_stop& right) {
    return left.position == right.position && left.color.red == right.color.red &&
        left.color.green == right.color.green && left.color.blue == right.color.blue &&
        left.color.alpha == right.color.alpha;
}

template<class Require>
void record_gradient_stop_order(compat::render_target* target, unsigned variant, bool ordered, Require require) {
    namespace d2d = compat;
    require(variant < gradient_stop_variant_count, "gradient order variant");
    auto source = unordered_gradient_stops;
    if (ordered) for (std::size_t i = 0; i < source.size(); ++i)
        source[i] = unordered_gradient_stops[ordered_gradient_indices[i]];
    const auto original = source;
    const bool full_opacity_srgb = variant < 6U || variant == 12U;
    const auto gamma = full_opacity_srgb ? d2d::gamma::gamma_2_2 : d2d::gamma::gamma_1_0;
    const auto spread = static_cast<d2d::extend_mode>((variant / 2U) % 3U);
    com::pointer<d2d::gradient_stop_collection> collection;
    require(target->CreateGradientStopCollection(source.data(), static_cast<std::uint32_t>(source.size()),
        gamma, spread, collection.put()) == com::ok, "gradient order collection creation");
    source.fill({0.25F, {0, 1, 0, 1}}); // Collection owns the original caller bytes.
    std::array<d2d::gradient_stop, 6U> returned{};
    collection->GetGradientStops(returned.data(), static_cast<std::uint32_t>(returned.size()));
    require(collection->GetGradientStopCount() == original.size() &&
        collection->GetColorInterpolationGamma() == gamma && collection->GetExtendMode() == spread &&
        std::equal(original.begin(), original.end(), returned.begin(), same_gradient_stop),
        "original collection readback/order/ownership changed");
    const d2d::brush_properties properties{full_opacity_srgb ? 1.0F : 0.5F, {1, 0, 0, 1, 4, 0}};
    com::pointer<d2d::linear_gradient_brush> linear;
    com::pointer<d2d::radial_gradient_brush> radial;
    d2d::brush* brush{};
    if ((variant & 1U) == 0U) {
        const d2d::linear_gradient_brush_properties axis = variant == 12U
            ? d2d::linear_gradient_brush_properties{{0, 4}, {0, 20}}
            : d2d::linear_gradient_brush_properties{{4, 0}, {20, 0}};
        require(target->CreateLinearGradientBrush(&axis, &properties, collection.get(), linear.put()) == com::ok,
            "unordered linear brush");
        brush = linear.get();
    } else {
        const d2d::radial_gradient_brush_properties ellipse{{28, 22}, {0, 0}, 16, 16};
        require(target->CreateRadialGradientBrush(&ellipse, &properties, collection.get(), radial.put()) == com::ok,
            "unordered radial brush");
        brush = radial.get();
    }
    target->BeginDraw();
    const d2d::color_f black{0, 0, 0, 1};
    const d2d::matrix_3x2_f translated{1, 0, 0, 1, 0, 2};
    target->Clear(&black); target->SetTransform(&translated);
    target->SetAntialiasMode(d2d::antialias_mode::aliased);
    const d2d::rectangle_f rectangle{4, 2, 60, 42};
    target->FillRectangle(&rectangle, brush);
    require(target->EndDraw(nullptr, nullptr) == com::ok, "unordered gradient draw failed");
    collection->GetGradientStops(returned.data(), static_cast<std::uint32_t>(returned.size()));
    require(std::equal(original.begin(), original.end(), returned.begin(), same_gradient_stop),
        "drawing sorted the public collection in place");
}

// Absolute hard-edge oracle. All geometry is pixel-center separated from an
// edge, so no tolerance or border exclusion hides sorting/range/duplicate bugs.
inline std::array<std::uint8_t, 4U> gradient_stop_expected(unsigned variant, unsigned x, unsigned y) {
    if (x < 4U || x >= 60U || y < 4U || y >= 44U) return {0, 0, 0, 255};
    const double dx = x + 0.5 - 32.0, dy = y + 0.5 - 24.0;
    double t = (variant & 1U) == 0U ? (x + 0.5 - 8.0) / 16.0 : std::sqrt(dx * dx + dy * dy) / 16.0;
    if (variant == 12U) t = (y + 0.5 - 6.0) / 16.0;
    switch ((variant / 2U) % 3U) {
    case 0U: t = std::clamp(t, 0.0, 1.0); break;
    case 1U: t -= std::floor(t); break;
    default: t = std::abs(t); t = std::fmod(t, 2.0); if (t > 1.0) t = 2.0 - t; break;
    }
    const auto level = static_cast<std::uint8_t>(variant < 6U || variant == 12U ? 255 : 128);
    return t < 0.5 ? std::array<std::uint8_t, 4U>{level, 0, 0, 255}
        : std::array<std::uint8_t, 4U>{0, 0, level, 255};
}

// One real gamma-1.0 gradient with five constant eight-pixel bands. Inputs are
// exact dyadic sRGB values, independent of either renderer's conversion helper.
// Duplicate offsets keep every center away from a transition. The near-endpoint
// bands distinguish exact endpoint preservation from a broader snap/clamp.
template<class Require>
void record_gradient_endpoint_bands(compat::render_target* target, Require require) {
    namespace d2d = compat;
    constexpr std::array<float, 5U> channels{0, 1.0F / 128.0F, 0.5F, 127.0F / 128.0F, 1};
    std::array<d2d::gradient_stop, 10U> stops{};
    for (std::size_t i = 0; i < channels.size(); ++i) {
        const auto value = channels[i];
        stops[i * 2U] = {static_cast<float>(i) / 8.0F, {value, value, value, 1}};
        stops[i * 2U + 1U] = {static_cast<float>(i + 1U) / 8.0F, {value, value, value, 1}};
    }
    com::pointer<d2d::gradient_stop_collection> collection;
    com::pointer<d2d::linear_gradient_brush> brush;
    const d2d::linear_gradient_brush_properties axis{{0, 0}, {64, 0}};
    const d2d::brush_properties properties{0.5F, {1, 0, 0, 1, 0, 0}};
    require(target->CreateGradientStopCollection(stops.data(), static_cast<std::uint32_t>(stops.size()),
        d2d::gamma::gamma_1_0, d2d::extend_mode::clamp, collection.put()) == com::ok &&
        target->CreateLinearGradientBrush(&axis, &properties, collection.get(), brush.put()) == com::ok,
        "gradient endpoint-band brush");
    target->BeginDraw();
    const d2d::matrix_3x2_f identity{1, 0, 0, 1, 0, 0};
    const d2d::color_f black{0, 0, 0, 1};
    const d2d::rectangle_f rectangle{0, 0, 40, 64};
    target->SetTransform(&identity);
    target->SetAntialiasMode(d2d::antialias_mode::aliased);
    target->Clear(&black);
    target->FillRectangle(&rectangle, brush.get());
    require(target->EndDraw(nullptr, nullptr) == com::ok, "gradient endpoint-band draw");
}

inline std::array<std::uint8_t, 4U> gradient_endpoint_expected(unsigned x) {
    // Original straight channel * one-half opacity over opaque black, then
    // nearest UNORM8: 0, 255/256, 255/4, 32385/256, 255/2.
    constexpr std::array<std::uint8_t, 5U> levels{0, 1, 64, 127, 128};
    const std::uint8_t value = x < 40U ? levels[x / 8U] : 0U;
    return {value, value, value, 255};
}

inline void report_gradient_pixel_mismatch(const char* comparison, unsigned variant, bool ordered,
    unsigned x, unsigned y, const std::uint8_t* expected, const std::uint8_t* actual) {
    std::fprintf(stderr,
        "gradient pixel mismatch: %s variant=%u ordered=%u xy=(%u,%u) "
        "expected=(%u,%u,%u,%u) actual=(%u,%u,%u,%u)\n",
        comparison, variant, ordered ? 1U : 0U, x, y,
        static_cast<unsigned>(expected[0]), static_cast<unsigned>(expected[1]),
        static_cast<unsigned>(expected[2]), static_cast<unsigned>(expected[3]),
        static_cast<unsigned>(actual[0]), static_cast<unsigned>(actual[1]),
        static_cast<unsigned>(actual[2]), static_cast<unsigned>(actual[3]));
    std::fflush(stderr);
}

inline std::uint32_t gradient_stop_spread(unsigned variant) {
    switch ((variant / 2U) % 3U) {
    case 0U: return PROGPU_NATIVE_SCENE_GRADIENT_PAD_UNIT_INTERVAL;
    case 1U: return PROGPU_NATIVE_SCENE_GRADIENT_REPEAT;
    default: return PROGPU_NATIVE_SCENE_GRADIENT_REFLECT;
    }
}

inline bool gradient_stop_snapshot(std::span<const std::byte> bytes, std::uint32_t expected_spread) {
    progpu_native_scene_header header{};
    progpu_native_scene_command command{};
    progpu_native_scene_draw_brushes draw{};
    progpu_native_scene_resource table{};
    progpu_native_scene_brush brush{};
    std::uint32_t index{};
    if (!read_scene_value(bytes, 0U, header) || header.command_count != 1U ||
        !read_scene_value(bytes, header.command_offset, command) ||
        command.kind != PROGPU_NATIVE_SCENE_COMMAND_DRAW_ANALYTIC ||
        !read_scene_value(bytes, command.payload_offset, draw) || draw.brush_count != 1U ||
        !read_scene_value(bytes, command.payload_offset + sizeof(draw), index) ||
        !read_scene_value(bytes, header.resource_offset +
            std::uint64_t{draw.brush_resource_index} * header.resource_stride, table) ||
        !read_scene_value(bytes, table.payload_offset + std::uint64_t{index} * sizeof(brush), brush) ||
        brush.stop_count != unordered_gradient_stops.size() || brush.spread_method != expected_spread) return false;
    for (std::size_t i = 0; i < unordered_gradient_stops.size(); ++i) {
        progpu_native_scene_gradient_stop actual{};
        const auto& expected = unordered_gradient_stops[ordered_gradient_indices[i]];
        if (!read_scene_value(bytes, table.auxiliary_offset +
            (brush.stop_offset + i) * sizeof(actual), actual) || actual.offset != expected.position ||
            actual.color.r != expected.color.red || actual.color.g != expected.color.green ||
            actual.color.b != expected.color.blue || actual.color.a != expected.color.alpha ||
            actual.reserved0 != 0U || actual.reserved1 != 0U || actual.reserved2 != 0U ||
            (i < 4U ? brush.offsets0[i] : brush.offsets1[i - 4U]) != actual.offset ||
            brush.colors[i].r != actual.color.r || brush.colors[i].b != actual.color.b) return false;
    }
    return true;
}

struct gradient_coordinate_case {
    compat::matrix_3x2_f draw;
    std::array<float, 4U> target_row0;
    std::array<float, 4U> target_row1;
};

// Brush translation is always (4,0). These are independently written inverse
// target-to-brush rows, not computed by the production composition helper.
inline constexpr std::array<gradient_coordinate_case, 3U> gradient_coordinate_cases{{
    {{1, 0, 0, 1, 0, 2}, {1, 0, -4, 0}, {0, 1, -2, 0}},
    {{2, 0, 0, 4, 6, 8}, {0.5F, 0, -7, 0}, {0, 0.25F, -2, 0}},
    {{1, 0, 1, 1, 0, 2}, {1, -1, -2, 0}, {0, 1, -2, 0}}}};

inline bool gradient_coordinate_snapshot(std::span<const std::byte> bytes,
    std::span<const bool> local_coordinates, const gradient_coordinate_case& test, bool require_reuse = false) {
    progpu_native_scene_header header{};
    if (!read_scene_value(bytes, 0U, header) || header.command_count != local_coordinates.size()) return false;
    std::uint32_t first_index{};
    for (std::size_t i = 0; i < local_coordinates.size(); ++i) {
        progpu_native_scene_command command{};
        progpu_native_scene_draw_brushes draw{};
        progpu_native_scene_resource table{};
        progpu_native_scene_brush brush{};
        std::uint32_t index{};
        if (!read_scene_value(bytes, header.command_offset + i * header.command_stride, command) ||
            (command.kind == PROGPU_NATIVE_SCENE_COMMAND_DRAW_ANALYTIC) != local_coordinates[i] ||
            !read_scene_value(bytes, command.payload_offset, draw) || draw.brush_count == 0U ||
            !read_scene_value(bytes, command.payload_offset + sizeof(draw), index) ||
            !read_scene_value(bytes, header.resource_offset +
                std::uint64_t{draw.brush_resource_index} * header.resource_stride, table) ||
            !read_scene_value(bytes, table.payload_offset + std::uint64_t{index} * sizeof(brush), brush)) return false;
        const std::array<float, 4U> local_row0{1, 0, -4, 0}, local_row1{0, 1, 0, 0};
        const auto& row0 = local_coordinates[i] ? local_row0 : test.target_row0;
        const auto& row1 = local_coordinates[i] ? local_row1 : test.target_row1;
        if (!std::equal(row0.begin(), row0.end(), std::begin(brush.coordinate_transform0)) ||
            !std::equal(row1.begin(), row1.end(), std::begin(brush.coordinate_transform1))) return false;
        if (i == 0U) first_index = index;
        if (require_reuse && i + 1U == local_coordinates.size() && index != first_index) return false;
    }
    return true;
}

inline bool gradient_coordinate_contract(compat::scene_factory_native* factory) {
    namespace d2d = compat;
    const d2d::scene_render_target_properties properties{64, 64, 96, 96, 0x95CCU, 1};
    com::pointer<d2d::render_target> target;
    com::pointer<d2d::scene_render_target_native> scene;
    if (factory->CreateSceneRenderTarget(&properties, target.put()) != com::ok ||
        target.as(d2d::scene_render_target_native_interface_id, scene) != com::ok) return false;
    com::pointer<d2d::gradient_stop_collection> collection;
    com::pointer<d2d::linear_gradient_brush> linear;
    com::pointer<d2d::radial_gradient_brush> radial;
    const d2d::brush_properties brush_properties{1, {1, 0, 0, 1, 4, 0}};
    const d2d::linear_gradient_brush_properties axis{{0, 4}, {0, 20}};
    const d2d::radial_gradient_brush_properties ellipse{{28, 22}, {0, 0}, 16, 16};
    if (target->CreateGradientStopCollection(unordered_gradient_stops.data(), 6U, d2d::gamma::gamma_2_2,
            d2d::extend_mode::clamp, collection.put()) != com::ok ||
        target->CreateLinearGradientBrush(&axis, &brush_properties, collection.get(), linear.put()) != com::ok ||
        target->CreateRadialGradientBrush(&ellipse, &brush_properties, collection.get(), radial.put()) != com::ok) return false;
    const d2d::rectangle_f rectangle{4, 2, 60, 42};
    const d2d::rounded_rectangle rounded{rectangle, 4, 4}, unequal{rectangle, 4, 8};
    const d2d::ellipse oval{{32, 22}, 20, 12};
    const std::array<bool, 6U> local{true, true, true, false, false, true};
    for (d2d::brush* brush : {static_cast<d2d::brush*>(linear.get()), static_cast<d2d::brush*>(radial.get())}) {
        for (const auto& test : gradient_coordinate_cases) {
            target->BeginDraw(); target->Clear(nullptr); target->SetTransform(&test.draw);
            target->FillRectangle(&rectangle, brush);
            target->FillRoundedRectangle(&rounded, brush);
            target->FillEllipse(&oval, brush);
            target->DrawLine({4, 2}, {60, 42}, brush, 2, nullptr);
            target->FillRoundedRectangle(&unequal, brush);
            target->FillRectangle(&rectangle, brush);
            std::vector<std::byte> bytes;
            if (target->EndDraw(nullptr, nullptr) != com::ok || !export_copy_scene(scene.get(), bytes) ||
                !gradient_coordinate_snapshot(bytes, local, test)) return false;
        }
    }
    return true;
}

template<class Require>
void record_gradient_interval_pad(compat::render_target* target, Require require) {
    namespace d2d = compat;
    const d2d::gradient_stop stops[]{{1, {1, 1, 1, 1}}, {-1, {0, 0, 0, 1}}};
    com::pointer<d2d::gradient_stop_collection> collection;
    com::pointer<d2d::linear_gradient_brush> brush;
    require(target->CreateGradientStopCollection(stops, 2U, d2d::gamma::gamma_2_2, d2d::extend_mode::clamp,
        collection.put()) == com::ok, "outside-range interpolation collection");
    const d2d::linear_gradient_brush_properties axis{{24.5F, 0}, {32.5F, 0}};
    require(target->CreateLinearGradientBrush(&axis, nullptr, collection.get(), brush.put()) == com::ok,
        "outside-range interpolation brush");
    const d2d::color_f black{0, 0, 0, 1};
    const d2d::matrix_3x2_f identity{1, 0, 0, 1, 0, 0};
    const d2d::rectangle_f rectangle{0, 0, 64, 64};
    target->BeginDraw(); target->Clear(&black); target->SetTransform(&identity);
    target->SetAntialiasMode(d2d::antialias_mode::aliased); target->FillRectangle(&rectangle, brush.get());
    require(target->EndDraw(nullptr, nullptr) == com::ok, "outside-range interpolation draw");
}

inline std::uint8_t gradient_interval_expected(unsigned x, bool legacy = false) {
    double t = (static_cast<double>(x) - 24.0) / 8.0;
    if (!legacy) t = std::clamp(t, 0.0, 1.0);
    return static_cast<std::uint8_t>(std::floor(std::clamp((t + 1.0) / 2.0, 0.0, 1.0) * 255.0 + 0.5));
}

inline bool gradient_spread_admission_contract() {
    const std::array<progpu_native_scene_gradient_stop, 2U> stops{{{{0, 0, 0, 1}, -1, 0, 0, 0},
        {{1, 1, 1, 1}, 1, 0, 0, 0}}};
    progpu_native_scene_brush brush{};
    brush.type = PROGPU_NATIVE_SCENE_BRUSH_LINEAR_GRADIENT;
    brush.opacity = 1; brush.stop_count = 2; brush.end_point = {1, 0};
    brush.coordinate_transform0[0] = brush.coordinate_transform1[1] = 1;
    brush.spread_method = PROGPU_NATIVE_SCENE_GRADIENT_PAD_UNIT_INTERVAL;
    // Each negative starts from a successful owned resource; failure cannot
    // append partial brush/stop storage or publish the caller's result index.
    for (unsigned invalid = 0; invalid < 6U; ++invalid) {
        semantic_scene_builder builder(0x95C8U);
        std::uint32_t index{};
        if (!builder.add_brush(brush, stops, index)) return false;
        const auto before = builder.required_stream_size();
        auto candidate = brush;
        switch (invalid) {
        case 0: candidate.spread_method = 5U; break;
        case 1: candidate.spread_method |= PROGPU_NATIVE_SCENE_GRADIENT_PAD_OUTSIDE_COLORS; break;
        case 2: candidate.spread_method |= PROGPU_NATIVE_SCENE_GRADIENT_CONICAL_OUTSIDE_COLOR; break;
        case 3: candidate.type = PROGPU_NATIVE_SCENE_BRUSH_SOLID; break;
        case 4: candidate.type = PROGPU_NATIVE_SCENE_BRUSH_SWEEP_GRADIENT; break;
        default: candidate.spread_method |= 0x20000000U; break;
        }
        index = 123U;
        if (builder.add_brush(candidate, stops, index) || index != PROGPU_NATIVE_SCENE_NO_INDEX ||
            builder.required_stream_size() != before) return false;
    }
    semantic_scene_builder radial(0x95C9U);
    brush.type = PROGPU_NATIVE_SCENE_BRUSH_RADIAL_GRADIENT;
    brush.radius = brush.radius_y = 1;
    std::uint32_t index{};
    return radial.add_brush(brush, stops, index);
}

template<class Render, class Require>
void verify_gradient_interval_pixels(Render render, Require require) {
    com::pointer<compat::factory> owner;
    com::pointer<compat::scene_factory_native> factory;
    com::pointer<compat::render_target> target;
    com::pointer<compat::scene_render_target_native> scene;
    const compat::scene_render_target_properties properties{64, 64, 96, 96, 0x95C7U, 1};
    require(compat::create_factory(owner.put()) == com::ok &&
        owner.as(compat::scene_factory_native_interface_id, factory) == com::ok &&
        factory->CreateSceneRenderTarget(&properties, target.put()) == com::ok &&
        target.as(compat::scene_render_target_native_interface_id, scene) == com::ok, "gradient interval target");
    record_gradient_interval_pad(target.get(), require);
    std::vector<std::byte> bytes;
    require(export_copy_scene(scene.get(), bytes), "gradient interval scene export");
    scene.reset(); target.reset();
    progpu_native_scene_header header{};
    progpu_native_scene_command command{};
    progpu_native_scene_draw_brushes draw{};
    progpu_native_scene_resource table{};
    progpu_native_scene_brush brush{};
    std::uint32_t index{};
    require(read_scene_value(bytes, 0U, header) && header.command_count == 1U &&
        read_scene_value(bytes, header.command_offset, command) && read_scene_value(bytes, command.payload_offset, draw) &&
        read_scene_value(bytes, command.payload_offset + sizeof(draw), index) &&
        read_scene_value(bytes, header.resource_offset + std::uint64_t{draw.brush_resource_index} * header.resource_stride, table) &&
        read_scene_value(bytes, table.payload_offset + std::uint64_t{index} * sizeof(brush), brush) &&
        brush.spread_method == PROGPU_NATIVE_SCENE_GRADIENT_PAD_UNIT_INTERVAL,
        "Direct2D interval clamp mode not retained");
    std::vector<std::uint8_t> interval;
    for (const bool legacy : {false, true}) {
        if (legacy) {
            // Deliberate old raw-wire control, not another source conversion.
            brush.spread_method = PROGPU_NATIVE_SCENE_GRADIENT_PAD;
            std::memcpy(bytes.data() + table.payload_offset + std::uint64_t{index} * sizeof(brush), &brush, sizeof(brush));
            header.generation = 2U;
            std::memcpy(bytes.data(), &header, sizeof(header));
            for (std::uint32_t resource = 0; resource < header.resource_count; ++resource) {
                const auto offset = header.resource_offset + std::uint64_t{resource} * header.resource_stride;
                progpu_native_scene_resource record{};
                require(read_scene_value(bytes, offset, record), "legacy pad resource generation");
                record.generation = 2U; std::memcpy(bytes.data() + offset, &record, sizeof(record));
            }
        }
        const auto pixels = render(false, bytes, header.generation);
        require(pixels.size() == 64U * 64U * 4U && pixels == render(false, bytes, header.generation) &&
            pixels == render(true, bytes, header.generation), "gradient interval cold/warm/independent");
        for (unsigned y = 0; y < 64U; ++y) for (unsigned x = 0; x < 64U; ++x) {
            const auto grey = gradient_interval_expected(x, legacy);
            const std::array<std::uint8_t, 4U> expected{grey, grey, grey, 255};
            require(std::equal(expected.begin(), expected.end(), pixels.data() + (y * 64U + x) * 4U),
                "gradient exact inside/edge/outside interpolation pixels");
        }
        if (!legacy) interval = pixels;
        else require(interval != pixels && interval[0] == 128U && pixels[0] == 0U,
            "legacy raw PAD accidentally acquired unit-interval semantics");
    }
}

template<class Render, class Require>
void verify_gradient_stop_pixels(Render render, Require require) {
    com::pointer<compat::factory> owner;
    com::pointer<compat::scene_factory_native> factory;
    require(compat::create_factory(owner.put()) == com::ok &&
        owner.as(compat::scene_factory_native_interface_id, factory) == com::ok, "gradient order factory");
    for (unsigned variant = 0U; variant < gradient_stop_variant_count; ++variant) {
        std::vector<std::uint8_t> unordered;
        for (const bool ordered : {false, true}) {
            const compat::scene_render_target_properties properties{64, 64, 96, 96, 0x95C5U,
                static_cast<std::uint64_t>(variant) * 2U + (ordered ? 2U : 1U)};
            com::pointer<compat::render_target> target;
            com::pointer<compat::scene_render_target_native> scene;
            require(factory->CreateSceneRenderTarget(&properties, target.put()) == com::ok &&
                target.as(compat::scene_render_target_native_interface_id, scene) == com::ok, "gradient order target");
            record_gradient_stop_order(target.get(), variant, ordered, require);
            std::vector<std::byte> stream;
            require(export_copy_scene(scene.get(), stream) && gradient_stop_snapshot(stream, gradient_stop_spread(variant)),
                "stable snapshot stops");
            scene.reset(); target.reset();
            const auto pixels = render(false, stream, properties.generation);
            require(pixels.size() == 64U * 64U * 4U && pixels == render(false, stream, properties.generation) &&
                pixels == render(true, stream, properties.generation), "gradient cold/warm/independent pixels");
            for (unsigned y = 0; y < 64U; ++y) for (unsigned x = 0; x < 64U; ++x) {
                const auto expected = gradient_stop_expected(variant, x, y);
                const auto* actual = pixels.data() + (y * 64U + x) * 4U;
                const bool equal = std::equal(expected.begin(), expected.end(), actual);
                if (!equal) report_gradient_pixel_mismatch("absolute RGBA", variant, ordered,
                    x, y, expected.data(), actual);
                require(equal, "gradient absolute hard-edge pixels");
            }
            if (!ordered) unordered = pixels;
            else require(unordered == pixels, "unordered versus explicit stable input pixels");
        }
    }
    const compat::scene_render_target_properties properties{64, 64, 96, 96, 0x95C5U,
        static_cast<std::uint64_t>(gradient_stop_variant_count) * 2U + 1U};
    com::pointer<compat::render_target> target;
    com::pointer<compat::scene_render_target_native> scene;
    require(factory->CreateSceneRenderTarget(&properties, target.put()) == com::ok &&
        target.as(compat::scene_render_target_native_interface_id, scene) == com::ok, "gradient endpoint-band target");
    record_gradient_endpoint_bands(target.get(), require);
    std::vector<std::byte> stream;
    require(export_copy_scene(scene.get(), stream), "gradient endpoint-band snapshot");
    scene.reset(); target.reset();
    const auto pixels = render(false, stream, properties.generation);
    require(pixels.size() == 64U * 64U * 4U && pixels == render(false, stream, properties.generation) &&
        pixels == render(true, stream, properties.generation), "gradient endpoint cold/warm/independent pixels");
    for (unsigned y = 0; y < 64U; ++y) for (unsigned x = 0; x < 64U; ++x) {
        const auto expected = gradient_endpoint_expected(x);
        const auto* actual = pixels.data() + (y * 64U + x) * 4U;
        const bool equal = std::equal(expected.begin(), expected.end(), actual);
        if (!equal) report_gradient_pixel_mismatch("endpoint bands RGBA", gradient_stop_variant_count + 1U,
            false, x, y, expected.data(), actual);
        require(equal, "gradient exact endpoint and near-endpoint pixels");
    }
}

inline bool gradient_stop_contract(compat::scene_factory_native* factory) {
    if (!gradient_spread_admission_contract() || !gradient_coordinate_contract(factory)) return false;
    const compat::scene_render_target_properties properties{64, 64, 96, 96, 0x95C6U, 1};
    com::pointer<compat::render_target> target;
    com::pointer<compat::scene_render_target_native> scene;
    if (factory->CreateSceneRenderTarget(&properties, target.put()) != com::ok ||
        target.as(compat::scene_render_target_native_interface_id, scene) != com::ok) return false;
    bool valid = true;
    for (unsigned variant = 0; variant < gradient_stop_variant_count; ++variant) {
        record_gradient_stop_order(target.get(), variant, false, [&](bool result, const char*) { valid &= result; });
        std::vector<std::byte> before, after;
        if (!valid || !export_copy_scene(scene.get(), before) ||
            !gradient_stop_snapshot(before, gradient_stop_spread(variant))) return false;
        for (unsigned invalid = 0; invalid < 6U; ++invalid) {
            auto stops = unordered_gradient_stops;
            if (invalid < 2U) stops.back().position = invalid == 0U ? std::numeric_limits<float>::quiet_NaN()
                : std::numeric_limits<float>::infinity();
            if (invalid == 2U) stops.back().color.red = std::numeric_limits<float>::quiet_NaN();
            std::vector<compat::gradient_stop> oversized(PROGPU_NATIVE_SCENE_MAX_GRADIENT_STOPS + 1U, stops.front());
            compat::gradient_stop_collection* result = reinterpret_cast<compat::gradient_stop_collection*>(std::uintptr_t{1});
            const auto status = target->CreateGradientStopCollection(invalid == 5U ? nullptr :
                invalid == 4U ? oversized.data() : stops.data(), invalid == 3U ? 0U : invalid == 4U
                    ? static_cast<std::uint32_t>(oversized.size()) : static_cast<std::uint32_t>(stops.size()),
                compat::gamma::gamma_2_2, compat::extend_mode::clamp, &result);
            if (status != com::invalid_argument || result != nullptr ||
                !export_copy_scene(scene.get(), after) || after != before) return false;
        }
    }
    return valid;
}
} // namespace progpu::native::direct2d::tests
