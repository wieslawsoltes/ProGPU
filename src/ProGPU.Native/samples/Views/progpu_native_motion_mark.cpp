#include "progpu_native_motion_mark.hpp"

#include <algorithm>
#include <array>
#include <bit>
#include <cmath>
#include <limits>
#include <unordered_map>

namespace progpu::native::samples {
namespace {

constexpr std::array<std::array<std::int32_t, 2U>, 4U> offsets{{
    {-4, 0}, {2, 0}, {1, -2}, {1, 2}}};

constexpr std::array<progpu_native_color, 7U> vello_colors{{
    {0.06F, 0.06F, 0.06F, 1.0F},
    {0.50F, 0.50F, 0.50F, 1.0F},
    {0.75F, 0.75F, 0.75F, 1.0F},
    {0.06F, 0.06F, 0.06F, 1.0F},
    {0.50F, 0.50F, 0.50F, 1.0F},
    {0.75F, 0.75F, 0.75F, 1.0F},
    {0.88F, 0.06F, 0.25F, 1.0F}}};

constexpr std::array<progpu_native_color, 5U> fluent_colors{{
    {0.0F, 0.47F, 0.83F, 1.0F},
    {0.52F, 0.15F, 0.79F, 1.0F},
    {0.91F, 0.11F, 0.38F, 1.0F},
    {1.0F, 0.73F, 0.0F, 1.0F},
    {0.06F, 0.69F, 0.32F, 1.0F}}};

constexpr std::array<progpu_native_color, 4U> monochrome_colors{{
    {0.12F, 0.12F, 0.12F, 1.0F},
    {0.24F, 0.24F, 0.24F, 1.0F},
    {0.60F, 0.60F, 0.60F, 1.0F},
    {0.90F, 0.90F, 0.90F, 1.0F}}};

constexpr progpu_native_affine_2d identity{
    1.0F, 0.0F, 0.0F, 1.0F, 0.0F, 0.0F};

struct color_key final {
    std::array<std::uint32_t, 4U> bits{};

    bool operator==(const color_key&) const noexcept = default;
};

struct color_key_hash final {
    std::size_t operator()(const color_key& value) const noexcept {
        std::size_t hash = 0U;
        for (const std::uint32_t bits : value.bits) {
            hash ^= static_cast<std::size_t>(bits) + 0x9E3779B9U +
                (hash << 6U) + (hash >> 2U);
        }
        return hash;
    }
};

color_key key_for(progpu_native_color color) noexcept {
    return {{
        std::bit_cast<std::uint32_t>(color.r),
        std::bit_cast<std::uint32_t>(color.g),
        std::bit_cast<std::uint32_t>(color.b),
        std::bit_cast<std::uint32_t>(color.a)}};
}

progpu_native_color hsv_to_rgb(float h, float s, float v) noexcept {
    const float c = v * s;
    const float x = c * (1.0F - std::abs(
        std::fmod(h / 60.0F, 2.0F) - 1.0F));
    const float m = v - c;
    float r = 0.0F;
    float g = 0.0F;
    float b = 0.0F;
    if (h < 60.0F) {
        r = c;
        g = x;
    } else if (h < 120.0F) {
        r = x;
        g = c;
    } else if (h < 180.0F) {
        g = c;
        b = x;
    } else if (h < 240.0F) {
        g = x;
        b = c;
    } else if (h < 300.0F) {
        r = x;
        b = c;
    } else {
        r = c;
        b = x;
    }
    return {r + m, g + m, b + m, 1.0F};
}

} // namespace

motion_mark_scene::motion_mark_scene(
    std::uint32_t element_count,
    std::uint32_t seed)
    : random_state_(seed == 0U ? 0x50A7C0DEU : seed) {
    elements_.reserve(5000U);
    primitives_.reserve(10000U);
    brush_indices_.reserve(10000U);
    builder_.reserve(1U, 2U, 10000U *
        (sizeof(progpu_native_geometry_primitive) +
            sizeof(std::uint32_t)));
    resize(width_, height_);
    rebuild_elements(std::clamp(element_count, 1U, 5000U));
}

bool motion_mark_scene::resize(float width, float height) noexcept {
    if (!std::isfinite(width) || !std::isfinite(height) ||
        width <= 0.0F || height <= 0.0F) {
        return false;
    }
    if (width_ == width && height_ == height && !elements_.empty()) {
        return false;
    }
    width_ = width;
    height_ = height;
    grid_scale_ = std::max(0.0F, std::min(width / 81.0F, height / 41.0F));
    grid_offset_x_ = (width - grid_scale_ * 81.0F) * 0.5F;
    grid_offset_y_ = (height - grid_scale_ * 41.0F) * 0.5F;
    rebuild_primitives();
    mark_dirty();
    return true;
}

bool motion_mark_scene::set_element_count(std::uint32_t count) noexcept {
    count = std::clamp(count, 1U, 5000U);
    if (count == elements_.size()) {
        return false;
    }
    if (count < elements_.size()) {
        elements_.resize(count);
    } else {
        grid_point current = elements_.empty()
            ? grid_point{40, 20}
            : elements_.back().end;
        while (elements_.size() < count) {
            elements_.push_back(create_element(current));
        }
    }
    rebuild_primitives();
    mark_dirty();
    return true;
}

bool motion_mark_scene::set_color_mode(std::uint32_t mode) noexcept {
    mode = std::min(mode, 3U);
    if (mode == color_mode_) {
        return false;
    }
    color_mode_ = mode;
    for (auto& value : elements_) {
        value.color = random_color();
    }
    rebuild_primitives();
    mark_dirty();
    return true;
}

bool motion_mark_scene::regenerate(std::uint32_t seed) noexcept {
    random_state_ = seed == 0U ? 0x50A7C0DEU : seed;
    animation_budget_ = 0.0F;
    split_toggle_budget_ = 0.0F;
    rebuild_elements(static_cast<std::uint32_t>(elements_.size()));
    return true;
}

bool motion_mark_scene::advance(float delta_seconds) noexcept {
    if (elements_.empty() || !std::isfinite(delta_seconds) ||
        delta_seconds <= 0.0F) {
        return false;
    }
    constexpr float animation_step = 1.0F / 60.0F;
    animation_budget_ += std::min(delta_seconds, 0.1F);
    const auto step_count = static_cast<std::uint32_t>(
        animation_budget_ / animation_step);
    if (step_count == 0U) {
        return false;
    }
    animation_budget_ -= static_cast<float>(step_count) * animation_step;
    split_toggle_budget_ += static_cast<float>(elements_.size()) *
        0.005F * static_cast<float>(step_count);
    const auto toggle_count = std::min(
        static_cast<std::uint32_t>(elements_.size()),
        static_cast<std::uint32_t>(split_toggle_budget_));
    if (toggle_count == 0U) {
        return false;
    }
    split_toggle_budget_ -= static_cast<float>(toggle_count);
    for (std::uint32_t index = 0U; index < toggle_count; ++index) {
        auto& value = elements_[next_random() % elements_.size()];
        value.split = !value.split;
    }
    rebuild_primitives();
    mark_dirty();
    return true;
}

void motion_mark_scene::invalidate() noexcept {
    mark_dirty();
}

bool motion_mark_scene::compile(
    std::vector<std::byte>& stream,
    motion_mark_scene_metrics& metrics) noexcept {
    if (!dirty_ || primitives_.empty() ||
        !builder_.reset(scene_id_, generation_) ||
        !builder_.reserve(
            1U,
            2U,
            primitives_.size() *
                (sizeof(progpu_native_geometry_primitive) +
                    sizeof(std::uint32_t)))) {
        return false;
    }
    brush_indices_.clear();
    std::unordered_map<color_key, std::uint32_t, color_key_hash> brushes{};
    try {
        brushes.reserve(std::min<std::size_t>(
            primitives_.size(),
            static_cast<std::size_t>(group_count_)));
        for (const auto& primitive : primitives_) {
            const color_key key = key_for(primitive.color);
            auto found = brushes.find(key);
            if (found == brushes.end()) {
                std::uint32_t brush_index = PROGPU_NATIVE_SCENE_NO_INDEX;
                if (!builder_.add_solid_brush(
                        primitive.color,
                        1.0F,
                        brush_index)) {
                    return false;
                }
                found = brushes.emplace(key, brush_index).first;
            }
            brush_indices_.push_back(found->second);
        }
    } catch (...) {
        return false;
    }
    if (!builder_.draw_geometry(
            primitives_,
            brush_indices_,
            {0.0F, 0.0F, width_, height_})) {
        return false;
    }
    const std::size_t required = builder_.required_stream_size();
    if (required == 0U) {
        return false;
    }
    try {
        if (stream.capacity() < required) {
            stream.reserve(required);
        }
        stream.resize(required);
    } catch (...) {
        return false;
    }
    std::size_t written = 0U;
    scene_build_metrics build_metrics{};
    if (!builder_.build_into(stream, written, &build_metrics) ||
        written != required) {
        return false;
    }
    metrics.element_count = static_cast<std::uint32_t>(elements_.size());
    metrics.group_count = group_count_;
    metrics.primitive_count = static_cast<std::uint32_t>(primitives_.size());
    metrics.brush_count = build_metrics.brush_count;
    metrics.command_count = build_metrics.command_count;
    metrics.resource_count = build_metrics.resource_count;
    metrics.stream_bytes = build_metrics.stream_bytes;
    metrics.generation = generation_;
    dirty_ = false;
    return true;
}

bool motion_mark_scene::dirty() const noexcept {
    return dirty_;
}

std::uint64_t motion_mark_scene::generation() const noexcept {
    return generation_;
}

std::uint32_t motion_mark_scene::element_count() const noexcept {
    return static_cast<std::uint32_t>(elements_.size());
}

std::uint32_t motion_mark_scene::group_count() const noexcept {
    return group_count_;
}

std::span<const progpu_native_geometry_primitive>
motion_mark_scene::primitives() const noexcept {
    return primitives_;
}

std::uint32_t motion_mark_scene::next_random() noexcept {
    std::uint32_t value = random_state_;
    value ^= value << 13U;
    value ^= value >> 17U;
    value ^= value << 5U;
    random_state_ = value == 0U ? 0x50A7C0DEU : value;
    return random_state_;
}

float motion_mark_scene::next_unit() noexcept {
    return static_cast<float>(next_random() >> 8U) *
        (1.0F / 16777216.0F);
}

motion_mark_scene::grid_point motion_mark_scene::random_point(
    grid_point point) noexcept {
    const auto& offset = offsets[next_random() % offsets.size()];
    std::int32_t x = point.x + offset[0];
    if (x < 0 || x > 80) {
        x -= offset[0] * 2;
    }
    std::int32_t y = point.y + offset[1];
    if (y < 0 || y > 40) {
        y -= offset[1] * 2;
    }
    return {std::clamp(x, 0, 80), std::clamp(y, 0, 40)};
}

progpu_native_color motion_mark_scene::random_color() noexcept {
    if (color_mode_ == 1U) {
        return fluent_colors[next_random() % fluent_colors.size()];
    }
    if (color_mode_ == 2U) {
        return hsv_to_rgb(next_unit() * 360.0F, 0.85F, 0.95F);
    }
    if (color_mode_ == 3U) {
        return monochrome_colors[next_random() % monochrome_colors.size()];
    }
    return vello_colors[next_random() % vello_colors.size()];
}

motion_mark_scene::element motion_mark_scene::create_element(
    grid_point& current) noexcept {
    element value{};
    value.start = current;
    const std::uint32_t kind = next_random() % 3U;
    if (kind == 0U) {
        value.kind = motion_mark_segment_kind::line;
        value.end = random_point(current);
    } else if (kind == 1U) {
        value.kind = motion_mark_segment_kind::quadratic;
        value.control1 = random_point(current);
        value.end = random_point(value.control1);
    } else {
        value.kind = motion_mark_segment_kind::cubic;
        value.control1 = random_point(current);
        value.control2 = random_point(value.control1);
        value.end = random_point(value.control1);
    }
    current = value.end;
    value.color = random_color();
    value.width = std::pow(next_unit(), 5.0F) * 20.0F + 1.0F;
    value.split = next_unit() < 0.5F;
    return value;
}

progpu_native_point motion_mark_scene::map(grid_point point) const noexcept {
    return {
        grid_offset_x_ + (static_cast<float>(point.x) + 0.5F) * grid_scale_,
        grid_offset_y_ + (static_cast<float>(point.y) + 0.5F) * grid_scale_};
}

progpu_native_point motion_mark_scene::incoming_tangent(
    const element& value) const noexcept {
    const auto end = map(value.end);
    const auto previous = value.kind == motion_mark_segment_kind::line
        ? map(value.start)
        : value.kind == motion_mark_segment_kind::quadratic
            ? map(value.control1)
            : map(value.control2);
    return {end.x - previous.x, end.y - previous.y};
}

progpu_native_point motion_mark_scene::outgoing_tangent(
    const element& value) const noexcept {
    const auto start = map(value.start);
    const auto next = value.kind == motion_mark_segment_kind::line
        ? map(value.end)
        : map(value.control1);
    return {next.x - start.x, next.y - start.y};
}

void motion_mark_scene::rebuild_elements(std::uint32_t count) noexcept {
    elements_.clear();
    grid_point current{40, 20};
    for (std::uint32_t index = 0U; index < count; ++index) {
        elements_.push_back(create_element(current));
    }
    animation_budget_ = 0.0F;
    split_toggle_budget_ = 0.0F;
    rebuild_primitives();
    mark_dirty();
}

void motion_mark_scene::rebuild_primitives() noexcept {
    primitives_.clear();
    group_count_ = 0U;
    std::size_t begin = 0U;
    while (begin < elements_.size()) {
        std::size_t end = begin;
        while (end + 1U < elements_.size() && !elements_[end].split) {
            ++end;
        }
        ++group_count_;
        const auto& style = elements_[end];
        for (std::size_t index = begin; index <= end; ++index) {
            const auto& source = elements_[index];
            progpu_native_geometry_primitive primitive{};
            primitive.kind = source.kind == motion_mark_segment_kind::line
                ? PROGPU_NATIVE_GEOMETRY_LINE
                : source.kind == motion_mark_segment_kind::quadratic
                    ? PROGPU_NATIVE_GEOMETRY_QUADRATIC_BEZIER
                    : PROGPU_NATIVE_GEOMETRY_CUBIC_BEZIER;
            primitive.p0 = map(source.start);
            primitive.p1 = source.kind == motion_mark_segment_kind::line
                ? map(source.end)
                : map(source.control1);
            if (source.kind == motion_mark_segment_kind::quadratic) {
                primitive.p2 = map(source.end);
            } else if (source.kind == motion_mark_segment_kind::cubic) {
                primitive.p2 = map(source.control2);
                primitive.p3 = map(source.end);
            }
            primitive.stroke_thickness = style.width;
            primitive.color = style.color;
            primitive.transform = identity;
            primitives_.push_back(primitive);

            if (index < end) {
                progpu_native_geometry_primitive join{};
                join.kind = PROGPU_NATIVE_GEOMETRY_PATH_JOIN;
                join.flags = PROGPU_NATIVE_STROKE_JOIN_MITER <<
                    PROGPU_NATIVE_PRIMITIVE_START_CAP_SHIFT;
                join.p0 = map(source.end);
                join.p1 = incoming_tangent(source);
                join.p2 = outgoing_tangent(elements_[index + 1U]);
                join.p3 = {10.0F, 0.0F};
                join.stroke_thickness = style.width;
                join.color = style.color;
                join.transform = identity;
                primitives_.push_back(join);
            }
        }
        begin = end + 1U;
    }
}

void motion_mark_scene::mark_dirty() noexcept {
    if (!dirty_ && generation_ < std::numeric_limits<std::uint64_t>::max()) {
        ++generation_;
    } else if (dirty_ && generation_ == 0U) {
        generation_ = 1U;
    }
    dirty_ = true;
}

} // namespace progpu::native::samples
