#pragma once

#include "progpu_native_direct2d_copy_fixture.hpp"
#include "progpu_native_scene_builder.hpp"

#include <algorithm>
#include <array>
#include <cmath>
#include <limits>

namespace progpu::native::direct2d::tests {

// Explicit source order and explicit independent stable order. The two stops
// at 0.5 own opposite sides of the hard edge; sorting must never exchange them.
inline constexpr std::array<compat::gradient_stop, 6U> unordered_gradient_stops{{
    {2, {0, 0, 1, 1}}, {0.5F, {1, 0, 0, 1}}, {-1, {1, 0, 0, 1}},
    {0.5F, {0, 0, 1, 1}}, {0, {1, 0, 0, 1}}, {1, {0, 0, 1, 1}}}};
inline constexpr std::array<std::size_t, 6U> ordered_gradient_indices{2, 4, 1, 3, 5, 0};

inline bool same_gradient_stop(const compat::gradient_stop& left, const compat::gradient_stop& right) {
    return left.position == right.position && left.color.red == right.color.red &&
        left.color.green == right.color.green && left.color.blue == right.color.blue &&
        left.color.alpha == right.color.alpha;
}

template<class Require>
void record_gradient_stop_order(compat::render_target* target, unsigned variant, bool ordered, Require require) {
    namespace d2d = compat;
    require(variant < 12U, "gradient order variant");
    auto source = unordered_gradient_stops;
    if (ordered) for (std::size_t i = 0; i < source.size(); ++i)
        source[i] = unordered_gradient_stops[ordered_gradient_indices[i]];
    const auto original = source;
    const auto gamma = variant < 6U ? d2d::gamma::gamma_2_2 : d2d::gamma::gamma_1_0;
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
    const d2d::brush_properties properties{variant < 6U ? 1.0F : 0.5F, {1, 0, 0, 1, 4, 0}};
    com::pointer<d2d::linear_gradient_brush> linear;
    com::pointer<d2d::radial_gradient_brush> radial;
    d2d::brush* brush{};
    if ((variant & 1U) == 0U) {
        const d2d::linear_gradient_brush_properties axis{{4, 0}, {20, 0}};
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
    switch ((variant / 2U) % 3U) {
    case 0U: t = std::clamp(t, 0.0, 1.0); break;
    case 1U: t -= std::floor(t); break;
    default: t = std::abs(t); t = std::fmod(t, 2.0); if (t > 1.0) t = 2.0 - t; break;
    }
    const auto level = static_cast<std::uint8_t>(variant < 6U ? 255 : 128);
    return t < 0.5 ? std::array<std::uint8_t, 4U>{level, 0, 0, 255}
        : std::array<std::uint8_t, 4U>{0, 0, level, 255};
}

inline bool gradient_stop_snapshot(std::span<const std::byte> bytes) {
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
        brush.stop_count != unordered_gradient_stops.size()) return false;
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
    for (unsigned variant = 0U; variant < 12U; ++variant) {
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
            require(export_copy_scene(scene.get(), stream) && gradient_stop_snapshot(stream), "stable snapshot stops");
            scene.reset(); target.reset();
            const auto pixels = render(false, stream, properties.generation);
            require(pixels.size() == 64U * 64U * 4U && pixels == render(false, stream, properties.generation) &&
                pixels == render(true, stream, properties.generation), "gradient cold/warm/independent pixels");
            for (unsigned y = 0; y < 64U; ++y) for (unsigned x = 0; x < 64U; ++x) {
                const auto expected = gradient_stop_expected(variant, x, y);
                require(std::equal(expected.begin(), expected.end(), pixels.data() + (y * 64U + x) * 4U),
                    "gradient absolute hard-edge pixels");
            }
            if (!ordered) unordered = pixels;
            else require(unordered == pixels, "unordered versus explicit stable input pixels");
        }
    }
}

inline bool gradient_stop_contract(compat::scene_factory_native* factory) {
    if (!gradient_spread_admission_contract()) return false;
    const compat::scene_render_target_properties properties{64, 64, 96, 96, 0x95C6U, 1};
    com::pointer<compat::render_target> target;
    com::pointer<compat::scene_render_target_native> scene;
    if (factory->CreateSceneRenderTarget(&properties, target.put()) != com::ok ||
        target.as(compat::scene_render_target_native_interface_id, scene) != com::ok) return false;
    bool valid = true;
    for (unsigned variant = 0; variant < 12U; ++variant) {
        record_gradient_stop_order(target.get(), variant, false, [&](bool result, const char*) { valid &= result; });
        std::vector<std::byte> before, after;
        if (!valid || !export_copy_scene(scene.get(), before) || !gradient_stop_snapshot(before)) return false;
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
