#include "scene_packet.hpp"
#include "progpu_native_scene_builder.hpp"

#include <array>
#include <bit>
#include <cstdint>
#include <iostream>
#include <stdexcept>
#include <vector>

namespace {
using progpu::native::semantic_scene_builder;
using progpu::native::browser::compile_scene_packet;

void require(bool condition) { if (!condition) throw std::runtime_error("Browser scene packet regression failed"); }

struct writer {
    std::vector<std::byte> bytes;
    void u32(std::uint32_t value) { for (unsigned i = 0; i < 4; ++i) bytes.push_back(std::byte((value >> (8U * i)) & 255U)); }
    void u64(std::uint64_t value) { u32(static_cast<std::uint32_t>(value)); u32(static_cast<std::uint32_t>(value >> 32U)); }
    void f32(float value) { u32(std::bit_cast<std::uint32_t>(value)); }
    void f64(double value) { u64(std::bit_cast<std::uint64_t>(value)); }
    void floats(std::initializer_list<float> values) { for (const auto value : values) f32(value); }
    void matrix() { floats({1, 0, 0, 1, 0, 0}); }
    void solid() { u32(0); floats({1, 0, 0, 1}); }
    template<class F> void record(std::uint32_t kind, F body) {
        writer payload; body(payload); u32(kind); u32(static_cast<std::uint32_t>(payload.bytes.size()));
        bytes.insert(bytes.end(), payload.bytes.begin(), payload.bytes.end());
    }
    void header(std::uint32_t count) { u32(0x50534750); u32(1); u64(0xfedcba9876543210ULL); u64(5); u32(count); u32(0); }
};

void overwrite(std::vector<std::byte>& bytes, std::size_t offset, std::uint32_t value) {
    for (unsigned i = 0; i < 4; ++i) bytes[offset + i] = std::byte((value >> (8U * i)) & 255U);
}

void rejected(const std::vector<std::byte>& packet) {
    const std::vector<std::byte> previous{std::byte{7}, std::byte{9}};
    auto output = previous; const char* error = nullptr;
    require(!compile_scene_packet(packet, output, error));
    require(error != nullptr && output == previous);
}

void full_builder_differential() {
    // Original in-repository provenance: scene_builder_tests.cpp typed path,
    // brush, stroke and state fixtures. Expected bytes are compiled by a
    // separately authored direct call sequence, not by decoding our packet.
    writer packet; packet.header(7);
    packet.record(4, [](writer& p) { p.matrix(); p.f32(0.75F); p.u32(1); p.floats({0, 0, 200, 100}); });
    packet.record(6, [](writer& p) { p.f32(0.5F); p.u32(1); p.floats({0, 0, 200, 100}); });
    packet.record(1, [](writer& p) { p.floats({2, 3, 20, 30}); p.matrix(); p.solid(); });
    packet.record(2, [](writer& p) {
        p.matrix(); p.u32(1); p.u32(3);
        p.u32(1); p.floats({0, 0, 10, 30, 20, 0, 0, 0});
        p.u32(2); p.floats({20, 0, 30, 10, 40, 20, 50, 0});
        p.u32(0); p.floats({50, 0, 0, 0, 0, 0, 0, 0});
        p.u32(1); p.f32(0.8F); p.floats({0, 0, 50, 0}); p.u32(1); p.u32(2);
        p.f32(0); p.floats({0, 1, 0, 1}); p.f32(1); p.floats({0, 0, 1, 1});
    });
    packet.record(3, [](writer& p) {
        p.matrix(); p.f32(2); p.f32(4); p.u32(1);
        p.u32(2); p.u32(1); p.u32(2); p.u32(3); p.f64(0.5);
        p.u32(3); p.u32(3); p.floats({0, 0, 20, 10, 40, 0});
        p.f64(2); p.f64(1); p.f64(3); p.solid();
    });
    packet.record(7, [](writer&) {}); packet.record(5, [](writer&) {});

    semantic_scene_builder builder(0xfedcba9876543210ULL, 5U);
    auto state = semantic_scene_builder::identity_state(); state.opacity = 0.75F;
    state.flags = PROGPU_NATIVE_SCENE_STATE_CLIP_RECT; state.clip_rect = {0, 0, 200, 100};
    std::uint32_t state_index = 0; require(builder.add_state(state, state_index) && builder.save(state_index));
    progpu_native_scene_layer layer{}; layer.struct_size = sizeof(layer);
    layer.flags = PROGPU_NATIVE_SCENE_LAYER_BOUNDS; layer.bounds = {0, 0, 200, 100}; layer.opacity = 0.5F;
    layer.mask_resource_index = PROGPU_NATIVE_SCENE_NO_INDEX; layer.effect_resource_index = PROGPU_NATIVE_SCENE_NO_INDEX;
    require(builder.push_layer(layer));
    std::uint32_t red = 0; require(builder.add_solid_brush({1, 0, 0, 1}, 1, red));
    progpu_native_analytic_primitive rect{}; rect.kind = PROGPU_NATIVE_PRIMITIVE_RECTANGLE;
    rect.x = 2; rect.y = 3; rect.width = 20; rect.height = 30; rect.color = {1, 1, 1, 1};
    rect.transform = semantic_scene_builder::identity_transform();
    require(builder.draw_analytic({&rect, 1}, {&red, 1}, {2, 3, 20, 30}));
    progpu_native_scene_brush gradient{}; gradient.type = PROGPU_NATIVE_SCENE_BRUSH_LINEAR_GRADIENT;
    gradient.opacity = 0.8F; gradient.end_point = {50, 0}; gradient.spread_method = PROGPU_NATIVE_SCENE_GRADIENT_REFLECT;
    gradient.stop_count = 2; gradient.coordinate_transform0[0] = 1; gradient.coordinate_transform1[1] = 1;
    const std::array stops{progpu_native_scene_gradient_stop{{0, 1, 0, 1}, 0, 0, 0, 0},
        progpu_native_scene_gradient_stop{{0, 0, 1, 1}, 1, 0, 0, 0}};
    std::uint32_t gradient_index = 0; require(builder.add_brush(gradient, stops, gradient_index));
    const std::array segments{
        progpu_native_path_segment{{0, 0}, {10, 30}, {20, 0}, {}, PROGPU_NATIVE_PATH_SEGMENT_QUADRATIC, 0, 0, 0},
        progpu_native_path_segment{{20, 0}, {30, 10}, {40, 20}, {50, 0}, PROGPU_NATIVE_PATH_SEGMENT_CUBIC, 0, 0, 0},
        progpu_native_path_segment{{50, 0}, {0, 0}, {}, {}, PROGPU_NATIVE_PATH_SEGMENT_LINE, 0, 0, 0}};
    progpu_native_scene_path_fill path{}; path.segment_count = 3; path.max_x = 50; path.max_y = 30;
    path.color = {1, 1, 1, 1}; path.transform = semantic_scene_builder::identity_transform();
    path.fill_rule = PROGPU_NATIVE_FILL_RULE_EVEN_ODD; path.sample_grid = 4;
    require(builder.draw_paths({&path, 1}, segments, {&gradient_index, 1}, {0, 0, 50, 30}));
    // The native builder's own exact brush deduplication remains authoritative.
    require(builder.add_solid_brush({1, 0, 0, 1}, 1, red));
    const std::array points{progpu_native_point{0, 0}, progpu_native_point{20, 10}, progpu_native_point{40, 0}};
    const std::array dashes{2.0, 1.0, 3.0};
    progpu_native_scene_stroke stroke{}; stroke.struct_size = sizeof(stroke);
    stroke.flags = PROGPU_NATIVE_POLYLINE_FLAG_CLOSED; stroke.point_count = 3; stroke.dash_interval_count = 3;
    stroke.color = {1, 1, 1, 1}; stroke.transform = semantic_scene_builder::identity_transform();
    stroke.stroke_thickness = 2; stroke.miter_limit = 4; stroke.dash_offset = 0.5;
    stroke.start_cap = 2; stroke.end_cap = 1; stroke.line_join = 2; stroke.dash_cap = 3;
    require(builder.draw_strokes({&stroke, 1}, points, dashes, {&red, 1}, {-8, -8, 56, 26}));
    require(builder.pop_layer() && builder.restore());
    std::vector<std::byte> expected, actual; const char* error = nullptr;
    require(builder.build(expected)); require(compile_scene_packet(packet.bytes, actual, error));
    require(error == nullptr && actual == expected);

    // Every truncation is rejected atomically, including a partial variable
    // array or final scope. These are real byte-length boundaries, not null-only probes.
    for (std::size_t size = 0; size < packet.bytes.size(); ++size)
        rejected({packet.bytes.begin(), packet.bytes.begin() + static_cast<std::ptrdiff_t>(size)});
    auto invalid = packet.bytes; overwrite(invalid, 32, 0xffffffffU); rejected(invalid);
    invalid = packet.bytes; overwrite(invalid, 36, 0xffffffffU); rejected(invalid);
    invalid = packet.bytes; overwrite(invalid, 40, 0x7fc00000U); rejected(invalid);
    invalid = packet.bytes; overwrite(invalid, 4, 2U); rejected(invalid);
    invalid = packet.bytes; overwrite(invalid, 28, 1U); rejected(invalid);
    invalid = packet.bytes; invalid.push_back(std::byte{0}); rejected(invalid);
    // Unaligned input uses exactly the same transport and native stream.
    auto unaligned = packet.bytes; unaligned.insert(unaligned.begin(), std::byte{17});
    require(compile_scene_packet(std::span(unaligned).subspan(1), actual, error) && actual == expected);
}

void malformed_scopes_and_identity() {
    writer packet; packet.header(1); packet.record(5, [](writer&) {}); rejected(packet.bytes);
    packet = {}; packet.header(1); packet.record(6, [](writer& p) { p.f32(1); p.u32(0); }); rejected(packet.bytes);
    packet = {}; packet.header(0);
    std::vector<std::byte> empty; const char* error = nullptr;
    require(compile_scene_packet(packet.bytes, empty, error) && !empty.empty());
    overwrite(packet.bytes, 8, 0); overwrite(packet.bytes, 12, 0); rejected(packet.bytes);
}
} // namespace

int main() {
    try { full_builder_differential(); malformed_scopes_and_identity(); }
    catch (const std::exception& error) { std::cerr << error.what() << '\n'; return 1; }
    std::cout << "Browser authoring transport: complete native builder differential and rejection regressions passed\n";
    return 0;
}
