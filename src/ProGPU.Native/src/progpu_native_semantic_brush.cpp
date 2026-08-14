#include "progpu_native_semantic_brush.hpp"

#include "progpu_native_semantic_state.hpp"

#include <algorithm>
#include <bit>
#include <cmath>
#include <cstring>
#include <limits>
#include <new>
#include <unordered_map>

namespace progpu::native::semantic {
namespace {

template<typename T>
T read_record(const std::byte* bytes, std::size_t offset) noexcept {
    T value{};
    std::memcpy(&value, bytes + offset, sizeof(value));
    return value;
}

bool finite_color(const progpu_native_color& color) noexcept {
    return std::isfinite(color.r) && std::isfinite(color.g) &&
        std::isfinite(color.b) && std::isfinite(color.a);
}

bool finite_point(const progpu_native_point& point) noexcept {
    return std::isfinite(point.x) && std::isfinite(point.y);
}

bool finite_array(const float* values, std::size_t count) noexcept {
    for (std::size_t index = 0U; index < count; ++index) {
        if (!std::isfinite(values[index])) {
            return false;
        }
    }
    return true;
}

bool supported_brush_kind(std::uint32_t kind) noexcept {
    return kind == PROGPU_NATIVE_SCENE_BRUSH_SOLID ||
        kind == PROGPU_NATIVE_SCENE_BRUSH_LINEAR_GRADIENT ||
        kind == PROGPU_NATIVE_SCENE_BRUSH_RADIAL_GRADIENT ||
        kind == PROGPU_NATIVE_SCENE_BRUSH_TWO_POINT_CONICAL_GRADIENT ||
        kind == PROGPU_NATIVE_SCENE_BRUSH_SWEEP_GRADIENT ||
        kind == PROGPU_NATIVE_SCENE_BRUSH_PERLIN_NOISE;
}

bool gradient_brush_kind(std::uint32_t kind) noexcept {
    return kind == PROGPU_NATIVE_SCENE_BRUSH_LINEAR_GRADIENT ||
        kind == PROGPU_NATIVE_SCENE_BRUSH_RADIAL_GRADIENT ||
        kind == PROGPU_NATIVE_SCENE_BRUSH_TWO_POINT_CONICAL_GRADIENT ||
        kind == PROGPU_NATIVE_SCENE_BRUSH_SWEEP_GRADIENT;
}

std::uint32_t stored_stop_count(
    const progpu_native_scene_brush& brush) noexcept {
    if (brush.type == PROGPU_NATIVE_SCENE_BRUSH_PERLIN_NOISE) {
        return brush.stop_count == 0U ||
                brush.color_interpolation_mode ==
                    PROGPU_NATIVE_SCENE_GRADIENT_INTERPOLATE_SRGB
            ? 0U
            : static_cast<std::uint32_t>(
                PROGPU_NATIVE_SCENE_PERLIN_TABLE_RECORDS);
    }
    return gradient_brush_kind(brush.type) ? brush.stop_count : 0U;
}

bool valid_brush(
    const progpu_native_scene_brush& brush,
    const std::byte* stop_bytes,
    std::uint32_t stop_count) noexcept {
    const std::uint32_t spread = brush.spread_method & 0x7fffffffU;
    const bool outside_color =
        (brush.spread_method & 0x80000000U) != 0U;
    if (!supported_brush_kind(brush.type) ||
        !std::isfinite(brush.opacity) || brush.opacity < 0.0F ||
        brush.opacity > 1.0F || !finite_point(brush.start_point) ||
        !finite_point(brush.end_point) || !finite_point(brush.center) ||
        !std::isfinite(brush.radius) || !std::isfinite(brush.radius_y) ||
        spread > PROGPU_NATIVE_SCENE_GRADIENT_DECAL ||
        brush.color_interpolation_mode >
            PROGPU_NATIVE_SCENE_GRADIENT_INTERPOLATE_SCRGB ||
        (outside_color && brush.type !=
            PROGPU_NATIVE_SCENE_BRUSH_TWO_POINT_CONICAL_GRADIENT) ||
        brush.reserved0 != 0U || brush.reserved1 != 0U ||
        !finite_array(brush.offsets0, 4U) ||
        !finite_array(brush.offsets1, 4U) ||
        !finite_array(brush.coordinate_transform0, 4U) ||
        !finite_array(brush.coordinate_transform1, 4U) ||
        brush.coordinate_transform0[3] != 0.0F ||
        brush.coordinate_transform1[3] != 0.0F) {
        return false;
    }
    for (const auto& color : brush.colors) {
        if (!finite_color(color)) {
            return false;
        }
    }

    if (brush.type == PROGPU_NATIVE_SCENE_BRUSH_PERLIN_NOISE) {
        const std::uint32_t table_count = stored_stop_count(brush);
        if (outside_color || spread > 1U ||
            brush.stop_count > PROGPU_NATIVE_SCENE_MAX_PERLIN_OCTAVES ||
            (table_count == 0U && brush.stop_offset != 0U) ||
            brush.stop_offset > stop_count ||
            table_count > stop_count - brush.stop_offset) {
            return false;
        }
        for (std::uint32_t index = 0U; index < table_count; ++index) {
            const auto record =
                read_record<progpu_native_scene_gradient_stop>(
                    stop_bytes,
                    static_cast<std::size_t>(brush.stop_offset + index) *
                        sizeof(progpu_native_scene_gradient_stop));
            if (!finite_color(record.color) ||
                !std::isfinite(record.offset) ||
                record.reserved0 != 0U || record.reserved1 != 0U ||
                record.reserved2 != 0U) {
                return false;
            }
        }
        return true;
    }

    if (!gradient_brush_kind(brush.type)) {
        return brush.stop_count == 0U && brush.stop_offset == 0U &&
            brush.spread_method == 0U &&
            brush.color_interpolation_mode ==
                PROGPU_NATIVE_SCENE_GRADIENT_INTERPOLATE_SRGB;
    }
    if (brush.stop_count == 0U || brush.stop_offset > stop_count ||
        brush.stop_count > stop_count - brush.stop_offset) {
        return false;
    }
    if (brush.type == PROGPU_NATIVE_SCENE_BRUSH_RADIAL_GRADIENT &&
        (brush.radius < 0.0F || brush.radius_y < 0.0F ||
            (brush.radius == 0.0F && brush.radius_y == 0.0F))) {
        return false;
    }
    if (brush.type ==
            PROGPU_NATIVE_SCENE_BRUSH_TWO_POINT_CONICAL_GRADIENT &&
        (brush.radius < 0.0F || brush.radius_y < 0.0F)) {
        return false;
    }

    float previous_offset = -std::numeric_limits<float>::infinity();
    for (std::uint32_t index = 0U; index < brush.stop_count; ++index) {
        const auto stop = read_record<progpu_native_scene_gradient_stop>(
            stop_bytes,
            static_cast<std::size_t>(brush.stop_offset + index) *
                sizeof(progpu_native_scene_gradient_stop));
        if (!finite_color(stop.color) || !std::isfinite(stop.offset) ||
            stop.offset < previous_offset || stop.reserved0 != 0U ||
            stop.reserved1 != 0U || stop.reserved2 != 0U) {
            return false;
        }
        previous_offset = stop.offset;
    }
    return true;
}

progpu_native_scene_resource read_resource(
    const std::byte* bytes,
    const progpu_native_scene_header& header,
    std::uint32_t index) noexcept {
    return read_record<progpu_native_scene_resource>(
        bytes,
        header.resource_offset +
            static_cast<std::size_t>(index) * header.resource_stride);
}

progpu_native_scene_command read_command(
    const std::byte* bytes,
    const progpu_native_scene_header& header,
    std::uint32_t index) noexcept {
    return read_record<progpu_native_scene_command>(
        bytes,
        header.command_offset +
            static_cast<std::size_t>(index) * header.command_stride);
}

struct brush_variant_key final {
    std::uint32_t resource_index = 0U;
    std::uint32_t local_index = 0U;
    std::uint32_t opacity_bits = 0U;

    bool operator==(const brush_variant_key&) const = default;
};

struct brush_variant_hash final {
    std::size_t operator()(const brush_variant_key& key) const noexcept {
        std::uint64_t value = key.resource_index;
        value = (value * 0x9e3779b185ebca87ULL) ^ key.local_index;
        value = (value * 0x9e3779b185ebca87ULL) ^ key.opacity_bits;
        return static_cast<std::size_t>(value ^ (value >> 32U));
    }
};

struct brush_stop_key final {
    std::uint32_t resource_index = 0U;
    std::uint32_t stop_offset = 0U;
    std::uint32_t stop_count = 0U;

    bool operator==(const brush_stop_key&) const = default;
};

struct brush_stop_hash final {
    std::size_t operator()(const brush_stop_key& key) const noexcept {
        std::uint64_t value = key.resource_index;
        value = (value * 0x9e3779b185ebca87ULL) ^ key.stop_offset;
        value = (value * 0x9e3779b185ebca87ULL) ^ key.stop_count;
        return static_cast<std::size_t>(value ^ (value >> 32U));
    }
};

progpu_native_scene_brush solid_sentinel() noexcept {
    progpu_native_scene_brush brush{};
    brush.type = PROGPU_NATIVE_SCENE_BRUSH_SOLID;
    brush.opacity = 1.0F;
    brush.coordinate_transform0[0] = 1.0F;
    brush.coordinate_transform1[1] = 1.0F;
    return brush;
}

} // namespace

static_assert(sizeof(progpu_native_scene_brush) == 256U);
static_assert(offsetof(progpu_native_scene_brush, opacity) == 4U);
static_assert(offsetof(progpu_native_scene_brush, stop_offset) == 52U);
static_assert(offsetof(progpu_native_scene_brush, colors) == 64U);
static_assert(offsetof(progpu_native_scene_brush, offsets0) == 192U);
static_assert(offsetof(
    progpu_native_scene_brush, coordinate_transform0) == 224U);
static_assert(sizeof(progpu_native_scene_gradient_stop) == 32U);
static_assert(offsetof(progpu_native_scene_gradient_stop, offset) == 16U);
static_assert(sizeof(progpu_native_scene_draw_brushes) == 16U);

bool validate_brush_table(
    const std::byte* bytes,
    const progpu_native_scene_resource& resource,
    std::uint32_t& error_offset) noexcept {
    error_offset = resource.payload_offset;
    if (bytes == nullptr || resource.payload_size == 0U ||
        resource.payload_size % sizeof(progpu_native_scene_brush) != 0U ||
        resource.auxiliary_size %
            sizeof(progpu_native_scene_gradient_stop) != 0U) {
        return false;
    }
    const std::uint32_t brush_count = resource.payload_size /
        sizeof(progpu_native_scene_brush);
    const std::uint32_t stop_count = resource.auxiliary_size /
        sizeof(progpu_native_scene_gradient_stop);
    if (brush_count == 0U ||
        brush_count > PROGPU_NATIVE_SCENE_MAX_BRUSHES ||
        stop_count > PROGPU_NATIVE_SCENE_MAX_GRADIENT_STOPS) {
        return false;
    }
    for (std::uint32_t index = 0U; index < brush_count; ++index) {
        const std::uint32_t offset = resource.payload_offset +
            index * sizeof(progpu_native_scene_brush);
        const auto brush = read_record<progpu_native_scene_brush>(
            bytes,
            offset);
        if (!valid_brush(
                brush,
                bytes + resource.auxiliary_offset,
                stop_count)) {
            error_offset = offset;
            return false;
        }
    }
    return true;
}

bool validate_draw_brushes(
    const std::byte* bytes,
    const progpu_native_scene_header& header,
    const progpu_native_scene_command& command,
    std::uint32_t expected_count,
    std::uint32_t& error_offset) noexcept {
    error_offset = command.payload_offset;
    if (command.payload_size == 0U) {
        return true;
    }
    if (bytes == nullptr ||
        command.payload_size < sizeof(progpu_native_scene_draw_brushes)) {
        return false;
    }
    const auto draw = read_record<progpu_native_scene_draw_brushes>(
        bytes,
        command.payload_offset);
    const std::uint64_t expected_size = sizeof(draw) +
        static_cast<std::uint64_t>(draw.brush_count) * sizeof(std::uint32_t);
    if (draw.struct_size != sizeof(draw) || draw.reserved != 0U ||
        draw.brush_count == 0U || draw.brush_count != expected_count ||
        draw.brush_count > PROGPU_NATIVE_SCENE_MAX_DRAW_BRUSH_INDICES ||
        expected_size != command.payload_size ||
        draw.brush_resource_index >= header.resource_count) {
        return false;
    }
    const auto resource = read_resource(
        bytes,
        header,
        draw.brush_resource_index);
    if (resource.kind != PROGPU_NATIVE_SCENE_RESOURCE_BRUSH_TABLE ||
        resource.payload_size % sizeof(progpu_native_scene_brush) != 0U) {
        return false;
    }
    const std::uint32_t brush_count = resource.payload_size /
        sizeof(progpu_native_scene_brush);
    for (std::uint32_t index = 0U; index < draw.brush_count; ++index) {
        const std::uint32_t offset = command.payload_offset + sizeof(draw) +
            index * sizeof(std::uint32_t);
        const auto brush_index = read_record<std::uint32_t>(bytes, offset);
        if (brush_index >= brush_count) {
            error_offset = offset;
            return false;
        }
    }
    return true;
}

bool compile_brush_page(
    const std::byte* bytes,
    const progpu_native_scene_header& header,
    std::uint64_t scene_hash,
    semantic_brush_page& page) noexcept {
    try {
        semantic_brush_page compiled{};
        compiled.brushes.reserve(64U);
        compiled.gradient_stops.reserve(64U);
        compiled.command_draws.resize(header.command_count);
        compiled.brushes.push_back(solid_sentinel());
        compiled.gradient_stops.push_back({});

        std::unordered_map<brush_variant_key, std::uint32_t,
            brush_variant_hash> variants;
        std::unordered_map<brush_stop_key, std::uint32_t,
            brush_stop_hash> stop_ranges;
        variants.reserve(64U);
        stop_ranges.reserve(32U);
        semantic_state_cursor state_cursor(bytes, header);
        for (std::uint32_t command_index = 0U;
             command_index < header.command_count;
             ++command_index) {
            const auto command = read_command(bytes, header, command_index);
            const auto state = state_cursor.advance(command);
            if ((command.kind != PROGPU_NATIVE_SCENE_COMMAND_DRAW_ANALYTIC &&
                    command.kind != PROGPU_NATIVE_SCENE_COMMAND_DRAW_PATH &&
                    command.kind != PROGPU_NATIVE_SCENE_COMMAND_DRAW_GEOMETRY) ||
                command.payload_size == 0U) {
                continue;
            }
            const auto draw = read_record<progpu_native_scene_draw_brushes>(
                bytes,
                command.payload_offset);
            const auto resource = read_resource(
                bytes,
                header,
                draw.brush_resource_index);
            auto& draw_record = compiled.command_draws[command_index];
            draw_record.first_index = static_cast<std::uint32_t>(
                compiled.remapped_indices.size());
            draw_record.index_count = draw.brush_count;
            compiled.remapped_indices.reserve(
                compiled.remapped_indices.size() + draw.brush_count);
            for (std::uint32_t index = 0U;
                 index < draw.brush_count;
                 ++index) {
                const auto local_index = read_record<std::uint32_t>(
                    bytes,
                    command.payload_offset + sizeof(draw) +
                        static_cast<std::size_t>(index) * sizeof(std::uint32_t));
                const float opacity = state.opacity;
                const brush_variant_key key{
                    draw.brush_resource_index,
                    local_index,
                    std::bit_cast<std::uint32_t>(opacity)};
                const auto found = variants.find(key);
                if (found != variants.end()) {
                    compiled.remapped_indices.push_back(found->second);
                    continue;
                }
                if (compiled.brushes.size() >
                        PROGPU_NATIVE_SCENE_MAX_BRUSHES ||
                    compiled.brushes.size() >= (1U << 24U)) {
                    return false;
                }
                auto brush = read_record<progpu_native_scene_brush>(
                    bytes,
                    resource.payload_offset +
                        static_cast<std::size_t>(local_index) *
                            sizeof(progpu_native_scene_brush));
                brush.opacity *= opacity;
                const std::uint32_t physical_stop_count =
                    stored_stop_count(brush);
                if (physical_stop_count != 0U) {
                    const brush_stop_key stop_key{
                        draw.brush_resource_index,
                        brush.stop_offset,
                        physical_stop_count};
                    const auto retained_range = stop_ranges.find(stop_key);
                    if (retained_range != stop_ranges.end()) {
                        brush.stop_offset = retained_range->second;
                    } else {
                        const std::size_t retained_stop_count =
                            compiled.gradient_stops.size() - 1U;
                        if (physical_stop_count >
                            PROGPU_NATIVE_SCENE_MAX_GRADIENT_STOPS -
                                retained_stop_count) {
                            return false;
                        }
                        const auto packed_stop_offset =
                            static_cast<std::uint32_t>(
                                compiled.gradient_stops.size());
                        for (std::uint32_t stop_index = 0U;
                             stop_index < physical_stop_count;
                             ++stop_index) {
                            compiled.gradient_stops.push_back(
                                read_record<
                                    progpu_native_scene_gradient_stop>(
                                    bytes,
                                    resource.auxiliary_offset +
                                        static_cast<std::size_t>(
                                            brush.stop_offset + stop_index) *
                                            sizeof(
                                                progpu_native_scene_gradient_stop)));
                        }
                        stop_ranges.emplace(stop_key, packed_stop_offset);
                        brush.stop_offset = packed_stop_offset;
                    }
                    if (brush.stop_offset == 0U) {
                        return false;
                    }
                }
                const auto packed_index = static_cast<std::uint32_t>(
                    compiled.brushes.size());
                compiled.brushes.push_back(brush);
                variants.emplace(key, packed_index);
                compiled.remapped_indices.push_back(packed_index);
            }
        }
        compiled.scene_hash = scene_hash;
        compiled.cache_valid = true;
        page = std::move(compiled);
        return true;
    } catch (const std::bad_alloc&) {
        return false;
    } catch (...) {
        return false;
    }
}

bool try_get_draw_brush_index(
    const semantic_brush_page& page,
    std::uint32_t command_index,
    std::uint32_t record_index,
    std::uint32_t& brush_index) noexcept {
    brush_index = 0U;
    if (command_index >= page.command_draws.size()) {
        return false;
    }
    const auto draw = page.command_draws[command_index];
    if (draw.index_count == 0U) {
        return true;
    }
    if (record_index >= draw.index_count ||
        draw.first_index > page.remapped_indices.size() ||
        draw.index_count >
            page.remapped_indices.size() - draw.first_index) {
        return false;
    }
    const std::size_t index =
        static_cast<std::size_t>(draw.first_index) + record_index;
    if (index >= page.remapped_indices.size()) {
        return false;
    }
    brush_index = page.remapped_indices[index];
    return brush_index < page.brushes.size();
}

const progpu_native_scene_brush* try_get_packed_brush(
    const semantic_brush_page& page,
    std::uint32_t brush_index) noexcept {
    return brush_index < page.brushes.size()
        ? &page.brushes[brush_index]
        : nullptr;
}

} // namespace progpu::native::semantic
