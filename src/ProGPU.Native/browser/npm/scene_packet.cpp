#include "scene_packet.hpp"
#include "progpu_native_scene_builder.hpp"

#include <algorithm>
#include <bit>
#include <cmath>
#include <cstdint>
#include <limits>
#include <stdexcept>

namespace progpu::native::browser {
namespace {

constexpr std::size_t maximum_packet_bytes = 64U * 1024U * 1024U;

// Byte-wise little-endian reads deliberately do not alias packed/unaligned JS
// payloads as C++ structs. The serializer below remains owned by the existing
// native builder and its fixed uint64 scene-stream ABI.
class reader final {
public:
    explicit reader(std::span<const std::byte> bytes) : bytes_(bytes) {}
    std::uint32_t u32() {
        const auto bytes = take(4U);
        std::uint32_t value = 0U;
        for (std::uint32_t i = 0U; i < 4U; ++i)
            value |= std::to_integer<std::uint32_t>(bytes[i]) << (8U * i);
        return value;
    }
    std::uint64_t u64() { const auto low = u32(); return low | (std::uint64_t{u32()} << 32U); }
    float number() {
        const float value = std::bit_cast<float>(u32());
        require(std::isfinite(value)); return value;
    }
    double real() {
        const double value = std::bit_cast<double>(u64());
        require(std::isfinite(value)); return value;
    }
    progpu_native_point point() { const float x = number(); return {x, number()}; }
    progpu_native_affine_2d transform() {
        progpu_native_affine_2d value{};
        value.m11 = number(); value.m12 = number(); value.m21 = number();
        value.m22 = number(); value.m31 = number(); value.m32 = number(); return value;
    }
    progpu_native_image_rect rectangle() {
        const float x = number(), y = number(), width = number(), height = number();
        require(width >= 0.0F && height >= 0.0F); return {x, y, width, height};
    }
    progpu_native_color color() {
        const float r = unit(), g = unit(), b = unit(), a = unit(); return {r, g, b, a};
    }
    float unit() { const float value = number(); require(value >= 0.0F && value <= 1.0F); return value; }
    std::span<const std::byte> take(std::size_t count) {
        require(count <= bytes_.size()); const auto result = bytes_.first(count);
        bytes_ = bytes_.subspan(count); return result;
    }
    std::size_t remaining() const { return bytes_.size(); }
    static void require(bool valid) { if (!valid) throw std::invalid_argument("Invalid typed scene packet"); }
private:
    std::span<const std::byte> bytes_;
};

struct extent final {
    float min_x = std::numeric_limits<float>::infinity();
    float min_y = std::numeric_limits<float>::infinity();
    float max_x = -std::numeric_limits<float>::infinity();
    float max_y = -std::numeric_limits<float>::infinity();
    void add(progpu_native_point p) {
        min_x = std::min(min_x, p.x); min_y = std::min(min_y, p.y);
        max_x = std::max(max_x, p.x); max_y = std::max(max_y, p.y);
    }
    progpu_native_image_rect bounds(float padding = 0.0F) const {
        return {min_x - padding, min_y - padding, max_x - min_x + 2.0F * padding, max_y - min_y + 2.0F * padding};
    }
};

std::uint32_t read_brush(reader& input, semantic_scene_builder& builder) {
    const auto kind = input.u32();
    std::uint32_t index = PROGPU_NATIVE_SCENE_NO_INDEX;
    if (kind == 0U) {
        reader::require(builder.add_solid_brush(input.color(), 1.0F, index)); return index;
    }
    reader::require(kind == 1U);
    progpu_native_scene_brush brush{};
    brush.type = PROGPU_NATIVE_SCENE_BRUSH_LINEAR_GRADIENT;
    brush.opacity = input.unit(); brush.start_point = input.point(); brush.end_point = input.point();
    brush.spread_method = input.u32(); brush.stop_count = input.u32();
    reader::require(brush.spread_method <= PROGPU_NATIVE_SCENE_GRADIENT_REPEAT &&
        brush.stop_count >= 2U && brush.stop_count <= PROGPU_NATIVE_SCENE_MAX_GRADIENT_STOPS &&
        brush.stop_count <= input.remaining() / 20U);
    brush.coordinate_transform0[0] = 1.0F; brush.coordinate_transform1[1] = 1.0F;
    std::vector<progpu_native_scene_gradient_stop> stops(brush.stop_count);
    float previous = -1.0F;
    for (auto& stop : stops) {
        stop.offset = input.unit(); stop.color = input.color();
        reader::require(stop.offset >= previous); previous = stop.offset;
    }
    reader::require(builder.add_brush(brush, stops, index)); return index;
}

void command(std::uint32_t kind, reader& input, semantic_scene_builder& builder) {
    if (kind == 1U) {
        const auto bounds = input.rectangle();
        progpu_native_analytic_primitive rectangle{};
        rectangle.kind = PROGPU_NATIVE_PRIMITIVE_RECTANGLE;
        rectangle.x = bounds.x; rectangle.y = bounds.y;
        rectangle.width = bounds.width; rectangle.height = bounds.height;
        rectangle.transform = input.transform(); rectangle.color = {1, 1, 1, 1};
        const auto brush = read_brush(input, builder);
        reader::require(builder.draw_analytic({&rectangle, 1U}, {&brush, 1U}, bounds));
    } else if (kind == 2U) {
        progpu_native_scene_path_fill path{};
        path.transform = input.transform(); path.fill_rule = input.u32();
        const auto count = input.u32();
        reader::require(path.fill_rule <= PROGPU_NATIVE_FILL_RULE_EVEN_ODD && count > 0U && count <= input.remaining() / 36U);
        std::vector<progpu_native_path_segment> segments(count);
        extent bounds{};
        for (auto& segment : segments) {
            segment.kind = input.u32(); reader::require(segment.kind <= PROGPU_NATIVE_PATH_SEGMENT_CUBIC);
            segment.p0 = input.point(); segment.p1 = input.point(); segment.p2 = input.point(); segment.p3 = input.point();
            bounds.add(segment.p0); bounds.add(segment.p1);
            if (segment.kind >= PROGPU_NATIVE_PATH_SEGMENT_QUADRATIC) bounds.add(segment.p2);
            if (segment.kind == PROGPU_NATIVE_PATH_SEGMENT_CUBIC) bounds.add(segment.p3);
        }
        path.segment_count = count; path.min_x = bounds.min_x; path.min_y = bounds.min_y;
        path.max_x = bounds.max_x; path.max_y = bounds.max_y;
        path.color = {1, 1, 1, 1}; path.sample_grid = 4U;
        const auto brush = read_brush(input, builder);
        reader::require(builder.draw_paths({&path, 1U}, segments, {&brush, 1U}, bounds.bounds()));
    } else if (kind == 3U) {
        progpu_native_scene_stroke stroke{};
        stroke.struct_size = sizeof(stroke); stroke.kind = PROGPU_NATIVE_SCENE_STROKE_POLYLINE;
        stroke.transform = input.transform(); stroke.stroke_thickness = input.number(); stroke.miter_limit = input.number();
        const auto closed = input.u32();
        reader::require(closed <= 1U && stroke.stroke_thickness > 0.0F && stroke.miter_limit >= 1.0F);
        stroke.flags = closed != 0U ? static_cast<std::uint32_t>(PROGPU_NATIVE_POLYLINE_FLAG_CLOSED) : 0U;
        stroke.start_cap = input.u32(); stroke.end_cap = input.u32(); stroke.line_join = input.u32(); stroke.dash_cap = input.u32();
        reader::require(stroke.start_cap <= PROGPU_NATIVE_STROKE_CAP_TRIANGLE && stroke.end_cap <= PROGPU_NATIVE_STROKE_CAP_TRIANGLE &&
            stroke.dash_cap <= PROGPU_NATIVE_STROKE_CAP_TRIANGLE && stroke.line_join <= PROGPU_NATIVE_STROKE_JOIN_ROUND);
        stroke.dash_offset = input.real(); stroke.point_count = input.u32(); stroke.dash_interval_count = input.u32();
        reader::require(stroke.point_count >= 2U && stroke.point_count <= input.remaining() / sizeof(progpu_native_point));
        std::vector<progpu_native_point> points(static_cast<std::size_t>(stroke.point_count));
        extent bounds{};
        for (auto& point : points) { point = input.point(); bounds.add(point); }
        reader::require(stroke.dash_interval_count <= input.remaining() / sizeof(double));
        std::vector<double> dashes(static_cast<std::size_t>(stroke.dash_interval_count));
        for (auto& dash : dashes) { dash = input.real(); reader::require(dash >= 0.0); }
        reader::require(dashes.empty() || std::any_of(dashes.begin(), dashes.end(), [](double v) { return v > 0.0; }));
        stroke.color = {1, 1, 1, 1};
        const auto brush = read_brush(input, builder);
        // Conservative source bounds only; native connected-stroke preparation
        // still owns exact cap/join/dash geometry, transforms and culling.
        const float padding = stroke.stroke_thickness * std::max(1.0F, stroke.miter_limit);
        reader::require(builder.draw_strokes({&stroke, 1U}, points, dashes, {&brush, 1U}, bounds.bounds(padding)));
    } else if (kind == 4U) {
        auto state = semantic_scene_builder::identity_state();
        state.transform = input.transform(); state.opacity = input.unit();
        const auto clip = input.u32(); reader::require(clip <= 1U);
        if (clip != 0U) { state.flags |= PROGPU_NATIVE_SCENE_STATE_CLIP_RECT; state.clip_rect = input.rectangle(); }
        std::uint32_t index = PROGPU_NATIVE_SCENE_NO_INDEX;
        reader::require(builder.add_state(state, index) && builder.save(index));
    } else if (kind == 5U) {
        reader::require(builder.restore());
    } else if (kind == 6U) {
        progpu_native_scene_layer layer{};
        layer.struct_size = sizeof(layer); layer.opacity = input.unit();
        layer.mask_resource_index = PROGPU_NATIVE_SCENE_NO_INDEX; layer.effect_resource_index = PROGPU_NATIVE_SCENE_NO_INDEX;
        const auto bounded = input.u32(); reader::require(bounded <= 1U);
        if (bounded != 0U) { layer.flags = PROGPU_NATIVE_SCENE_LAYER_BOUNDS; layer.bounds = input.rectangle(); }
        reader::require(builder.push_layer(layer));
    } else if (kind == 7U) {
        reader::require(builder.pop_layer());
    } else {
        reader::require(false);
    }
    reader::require(input.remaining() == 0U);
}

} // namespace

bool compile_scene_packet(std::span<const std::byte> packet,
    std::vector<std::byte>& stream, const char*& error) noexcept {
    error = nullptr;
    try {
        reader::require(packet.size() <= maximum_packet_bytes);
        reader input(packet);
        reader::require(input.u32() == 0x50534750U && input.u32() == 1U);
        const auto scene_id = input.u64(), generation = input.u64();
        const auto count = input.u32();
        reader::require(scene_id != 0U && generation != 0U && input.u32() == 0U &&
            count <= PROGPU_NATIVE_SCENE_MAX_COMMANDS && count <= input.remaining() / 8U);
        semantic_scene_builder builder(scene_id, generation);
        for (std::uint32_t i = 0U; i < count; ++i) {
            const auto kind = input.u32(), size = input.u32();
            reader payload(input.take(size)); command(kind, payload, builder);
        }
        reader::require(input.remaining() == 0U);
        std::vector<std::byte> candidate;
        reader::require(builder.build(candidate));
        stream.swap(candidate); return true;
    } catch (const std::bad_alloc&) {
        error = "Typed scene allocation failed";
    } catch (...) {
        error = "Invalid or unsupported typed scene packet";
    }
    return false;
}

} // namespace progpu::native::browser
