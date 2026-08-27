#include "progpu_native_scene.hpp"
#include "progpu_native_hit_testing_validation.hpp"
#include "progpu_native_semantic_brush.hpp"
#include "progpu_native_semantic_color_glyph.hpp"
#include "progpu_native_semantic_layer_mask.hpp"
#include "progpu_native_semantic_text_style.hpp"
#include "progpu_native_semantic_validation.hpp"

#include <algorithm>
#include <array>
#include <bit>
#include <cmath>
#include <cstring>
#include <limits>
#include <new>
#include <vector>

namespace progpu::native::scene {
namespace {

constexpr std::uint32_t known_resource_flags =
    PROGPU_NATIVE_SCENE_RECORD_REQUIRED |
    PROGPU_NATIVE_SCENE_COLOR_GLYPH_BITMAPS |
    PROGPU_NATIVE_SCENE_EXTERNAL_IMAGE;
constexpr std::uint32_t known_command_flags =
    PROGPU_NATIVE_SCENE_RECORD_REQUIRED |
    PROGPU_NATIVE_SCENE_GLYPH_STYLED;

template<typename T>
T read_record(const std::byte* bytes, std::size_t offset) noexcept {
    T value{};
    std::memcpy(&value, bytes + offset, sizeof(T));
    return value;
}

bool add_fits(
    std::uint32_t offset,
    std::uint32_t size,
    std::uint32_t total) noexcept {
    return offset <= total && size <= total - offset;
}

bool table_fits(
    std::uint32_t offset,
    std::uint32_t count,
    std::uint32_t stride,
    std::uint32_t total) noexcept {
    if (offset > total || stride == 0U) {
        return false;
    }
    const std::uint64_t bytes =
        static_cast<std::uint64_t>(count) * stride;
    return bytes <= static_cast<std::uint64_t>(total - offset);
}

bool ranges_overlap(
    std::uint32_t first_offset,
    std::uint32_t first_size,
    std::uint32_t second_offset,
    std::uint32_t second_size) noexcept {
    if (first_size == 0U || second_size == 0U) {
        return false;
    }
    const std::uint64_t first_end =
        static_cast<std::uint64_t>(first_offset) + first_size;
    const std::uint64_t second_end =
        static_cast<std::uint64_t>(second_offset) + second_size;
    return first_offset < second_end && second_offset < first_end;
}

bool span_lives_in_arena(
    std::uint32_t offset,
    std::uint32_t size,
    const progpu_native_scene_header& header) noexcept {
    if (size == 0U) {
        return offset == 0U;
    }
    return offset >= header.arena_offset &&
        add_fits(offset, size, header.total_size) &&
        static_cast<std::uint64_t>(offset) + size <=
            static_cast<std::uint64_t>(header.arena_offset) +
                header.arena_size;
}

bool is_known_resource(std::uint32_t kind) noexcept {
    return kind >= PROGPU_NATIVE_SCENE_RESOURCE_ANALYTIC_BATCH &&
        kind <= PROGPU_NATIVE_SCENE_RESOURCE_GUIDELINE_SET;
}

bool is_known_command(std::uint32_t kind) noexcept {
    return kind == PROGPU_NATIVE_SCENE_COMMAND_SAVE ||
        kind == PROGPU_NATIVE_SCENE_COMMAND_RESTORE ||
        kind == PROGPU_NATIVE_SCENE_COMMAND_PUSH_LAYER ||
        kind == PROGPU_NATIVE_SCENE_COMMAND_POP_LAYER ||
        (kind >= PROGPU_NATIVE_SCENE_COMMAND_DRAW_ANALYTIC &&
            kind <= PROGPU_NATIVE_SCENE_COMMAND_DRAW_MESH_3D_BATCH);
}

bool is_draw_command(std::uint32_t kind) noexcept {
    return kind >= PROGPU_NATIVE_SCENE_COMMAND_DRAW_ANALYTIC &&
        kind <= PROGPU_NATIVE_SCENE_COMMAND_DRAW_MESH_3D_BATCH;
}

std::uint32_t expected_resource_kind(std::uint32_t command_kind) noexcept {
    switch (command_kind) {
        case PROGPU_NATIVE_SCENE_COMMAND_DRAW_ANALYTIC:
            return PROGPU_NATIVE_SCENE_RESOURCE_ANALYTIC_BATCH;
        case PROGPU_NATIVE_SCENE_COMMAND_DRAW_PATH:
            return PROGPU_NATIVE_SCENE_RESOURCE_PATH_BATCH;
        case PROGPU_NATIVE_SCENE_COMMAND_DRAW_GLYPH_RUN:
            return PROGPU_NATIVE_SCENE_RESOURCE_GLYPH_RUN;
        case PROGPU_NATIVE_SCENE_COMMAND_DRAW_IMAGE:
            return PROGPU_NATIVE_SCENE_RESOURCE_IMAGE;
        case PROGPU_NATIVE_SCENE_COMMAND_DRAW_GEOMETRY:
            return PROGPU_NATIVE_SCENE_RESOURCE_GEOMETRY_BATCH;
        case PROGPU_NATIVE_SCENE_COMMAND_DRAW_POINT_BATCH:
            return PROGPU_NATIVE_SCENE_RESOURCE_POINT_BATCH;
        case PROGPU_NATIVE_SCENE_COMMAND_DRAW_VERTEX_MESH:
            return PROGPU_NATIVE_SCENE_RESOURCE_VERTEX_MESH;
        case PROGPU_NATIVE_SCENE_COMMAND_DRAW_STROKE_BATCH:
            return PROGPU_NATIVE_SCENE_RESOURCE_STROKE_BATCH;
        case PROGPU_NATIVE_SCENE_COMMAND_DRAW_LINE_3D_BATCH:
            return PROGPU_NATIVE_SCENE_RESOURCE_LINE_3D_BATCH;
        case PROGPU_NATIVE_SCENE_COMMAND_DRAW_MESH_3D_BATCH:
            return PROGPU_NATIVE_SCENE_RESOURCE_MESH_3D_BATCH;
        default:
            return 0U;
    }
}

bool finite_bounds(const progpu_native_scene_command& command) noexcept {
    return std::isfinite(command.bounds_x) &&
        std::isfinite(command.bounds_y) &&
        std::isfinite(command.bounds_width) &&
        std::isfinite(command.bounds_height) &&
        command.bounds_width >= 0.0F && command.bounds_height >= 0.0F;
}

enum class hit_test_resource_validation {
    valid,
    invalid,
    out_of_memory
};

hit_test_resource_validation validate_hit_test_resource(
    const std::byte* bytes,
    const progpu_native_scene_resource& resource,
    std::uint32_t& error_offset) noexcept {
    error_offset = resource.payload_offset;
    if (resource.payload_size !=
            sizeof(progpu_native_scene_hit_test_index) ||
        resource.auxiliary_size == 0U) {
        return hit_test_resource_validation::invalid;
    }
    const auto page = read_record<progpu_native_scene_hit_test_index>(
        bytes, resource.payload_offset);
    hit_testing::hit_test_page_layout layout{};
    if (page.struct_size != sizeof(page) || page.flags != 0U ||
        page.node_count == 0U ||
        page.primitive_index_count != page.primitive_count ||
        !hit_testing::try_get_hit_test_page_layout(
            page.primitive_count,
            page.node_count,
            page.primitive_index_count,
            page.path_segment_count,
            layout) ||
        page.primitive_offset != layout.primitive_offset ||
        page.node_offset != layout.node_offset ||
        page.primitive_index_offset != layout.primitive_index_offset ||
        page.path_segment_offset != layout.path_segment_offset ||
        resource.auxiliary_size != layout.auxiliary_size) {
        return hit_test_resource_validation::invalid;
    }

    try {
        std::vector<std::uint8_t> primitive_seen(
            page.primitive_count, 0U);
        std::vector<std::uint8_t> node_parent_count(
            page.node_count, 0U);
        for (std::uint32_t index = 0U;
             index < page.primitive_count;
             ++index) {
            error_offset = resource.auxiliary_offset +
                page.primitive_offset +
                index * sizeof(progpu_native_hit_test_primitive);
            const auto primitive =
                read_record<progpu_native_hit_test_primitive>(
                    bytes, error_offset);
            if (!hit_testing::valid_hit_test_primitive(
                    primitive, page.path_segment_count)) {
                return hit_test_resource_validation::invalid;
            }
        }
        for (std::uint32_t index = 0U; index < page.node_count; ++index) {
            error_offset = resource.auxiliary_offset + page.node_offset +
                index * sizeof(progpu_native_hit_test_node);
            const auto node = read_record<progpu_native_hit_test_node>(
                bytes, error_offset);
            if (!hit_testing::valid_hit_test_node(
                    node,
                    page.node_count,
                    page.primitive_index_count) ||
                (node.child_count != 0U && node.first_child <= index)) {
                return hit_test_resource_validation::invalid;
            }
            for (std::uint32_t child = 0U;
                 child < node.child_count;
                 ++child) {
                const auto child_index = node.first_child + child;
                if (++node_parent_count[child_index] != 1U) {
                    return hit_test_resource_validation::invalid;
                }
                const auto child_node =
                    read_record<progpu_native_hit_test_node>(
                        bytes,
                        resource.auxiliary_offset + page.node_offset +
                            child_index *
                                sizeof(progpu_native_hit_test_node));
                if (child_node.bounds_min.x < node.bounds_min.x ||
                    child_node.bounds_min.y < node.bounds_min.y ||
                    child_node.bounds_max.x > node.bounds_max.x ||
                    child_node.bounds_max.y > node.bounds_max.y) {
                    return hit_test_resource_validation::invalid;
                }
            }
            for (std::uint32_t primitive_slot = 0U;
                 primitive_slot < node.primitive_count;
                 ++primitive_slot) {
                error_offset = resource.auxiliary_offset +
                    page.primitive_index_offset +
                    (node.first_primitive + primitive_slot) *
                        sizeof(std::uint32_t);
                const auto primitive_index = read_record<std::uint32_t>(
                    bytes, error_offset);
                if (primitive_index >= page.primitive_count ||
                    ++primitive_seen[primitive_index] != 1U) {
                    return hit_test_resource_validation::invalid;
                }
                const auto primitive =
                    read_record<progpu_native_hit_test_primitive>(
                        bytes,
                        resource.auxiliary_offset + page.primitive_offset +
                            primitive_index *
                                sizeof(progpu_native_hit_test_primitive));
                if (primitive.bounds_min.x < node.bounds_min.x ||
                    primitive.bounds_min.y < node.bounds_min.y ||
                    primitive.bounds_max.x > node.bounds_max.x ||
                    primitive.bounds_max.y > node.bounds_max.y) {
                    return hit_test_resource_validation::invalid;
                }
            }
        }
        for (std::uint32_t index = 1U; index < page.node_count; ++index) {
            if (node_parent_count[index] != 1U) {
                return hit_test_resource_validation::invalid;
            }
        }
        for (const auto seen : primitive_seen) {
            if (seen != 1U) {
                return hit_test_resource_validation::invalid;
            }
        }
        for (std::uint32_t index = 0U;
             index < page.path_segment_count;
             ++index) {
            error_offset = resource.auxiliary_offset +
                page.path_segment_offset +
                index * sizeof(progpu_native_path_segment);
            const auto segment = read_record<progpu_native_path_segment>(
                bytes, error_offset);
            if (!semantic::is_valid_semantic_segment(segment, true)) {
                return hit_test_resource_validation::invalid;
            }
        }
        return hit_test_resource_validation::valid;
    } catch (const std::bad_alloc&) {
        return hit_test_resource_validation::out_of_memory;
    } catch (...) {
        return hit_test_resource_validation::invalid;
    }
}

bool valid_scene_state(const progpu_native_scene_state& state) noexcept {
    constexpr std::uint32_t known_flags =
        PROGPU_NATIVE_SCENE_STATE_CLIP_RECT |
        PROGPU_NATIVE_SCENE_STATE_MASK |
        PROGPU_NATIVE_SCENE_STATE_GUIDELINE_SET;
    const bool clip_is_canonical =
        (state.flags & PROGPU_NATIVE_SCENE_STATE_CLIP_RECT) != 0U ||
        (state.clip_rect.x == 0.0F && state.clip_rect.y == 0.0F &&
            state.clip_rect.width == 0.0F &&
            state.clip_rect.height == 0.0F);
    const bool mask_is_canonical =
        (state.flags & PROGPU_NATIVE_SCENE_STATE_MASK) != 0U ||
        state.mask_resource_index == 0U;
    const bool guidelines_are_canonical =
        (state.flags & PROGPU_NATIVE_SCENE_STATE_GUIDELINE_SET) != 0U ||
        state.guideline_resource_index == 0U;
    return state.struct_size == sizeof(progpu_native_scene_state) &&
        (state.flags & ~known_flags) == 0U &&
        state.reserved == 0U &&
        std::isfinite(state.transform.m11) &&
        std::isfinite(state.transform.m12) &&
        std::isfinite(state.transform.m21) &&
        std::isfinite(state.transform.m22) &&
        std::isfinite(state.transform.m31) &&
        std::isfinite(state.transform.m32) &&
        std::isfinite(state.opacity) && state.opacity >= 0.0F &&
        state.opacity <= 1.0F &&
        std::isfinite(state.clip_rect.x) &&
        std::isfinite(state.clip_rect.y) &&
        std::isfinite(state.clip_rect.width) &&
        std::isfinite(state.clip_rect.height) &&
        state.clip_rect.width >= 0.0F &&
        state.clip_rect.height >= 0.0F && clip_is_canonical &&
        mask_is_canonical && guidelines_are_canonical;
}

bool valid_scene_layer(const progpu_native_scene_layer& layer) noexcept {
    return semantic::is_valid_semantic_layer(layer);
}

bool valid_scene_effect(
    const progpu_native_group_effect& effect) noexcept {
    return semantic::is_valid_semantic_effect(effect);
}

bool command_ids_are_unique(
    const std::byte* bytes,
    const progpu_native_scene_header& header) {
    if (header.command_count < 2U) {
        return true;
    }
    std::vector<std::uint64_t> current(header.command_count);
    std::vector<std::uint64_t> scratch(header.command_count);
    for (std::uint32_t index = 0U; index < header.command_count; ++index) {
        const std::size_t offset = header.command_offset +
            static_cast<std::size_t>(index) * header.command_stride;
        current[index] =
            read_record<progpu_native_scene_command>(bytes, offset).command_id;
    }

    for (std::uint32_t pass = 0U; pass < 8U; ++pass) {
        std::array<std::size_t, 256U> counts{};
        const std::uint32_t shift = pass * 8U;
        for (const std::uint64_t id : current) {
            ++counts[static_cast<std::uint8_t>(id >> shift)];
        }
        std::size_t position = 0U;
        for (auto& count : counts) {
            const std::size_t next = position + count;
            count = position;
            position = next;
        }
        for (const std::uint64_t id : current) {
            scratch[counts[static_cast<std::uint8_t>(id >> shift)]++] = id;
        }
        current.swap(scratch);
    }
    for (std::size_t index = 1U; index < current.size(); ++index) {
        if (current[index - 1U] == current[index]) {
            return false;
        }
    }
    return true;
}

validation_result fail(
    const progpu_native_scene_header& header,
    progpu_native_scene_validation_error error,
    std::uint32_t error_offset,
    progpu_native_status status = PROGPU_NATIVE_STATUS_INVALID_ARGUMENT) noexcept {
    validation_result result{};
    result.status = status;
    result.error = error;
    result.error_offset = error_offset;
    result.header = header;
    return result;
}

thread_local std::uint32_t validation_depth = 0U;

struct validation_depth_scope final {
    validation_depth_scope() noexcept { ++validation_depth; }
    ~validation_depth_scope() { --validation_depth; }
};

} // namespace

validation_result validate(
    const void* stream,
    std::size_t stream_size) noexcept {
    progpu_native_scene_header header{};
    if (validation_depth >= PROGPU_NATIVE_SCENE_MAX_PICTURE_MASK_DEPTH) {
        return fail(
            header,
            PROGPU_NATIVE_SCENE_VALIDATION_RANGE,
            0U,
            PROGPU_NATIVE_STATUS_OUT_OF_MEMORY);
    }
    const validation_depth_scope depth_scope{};
    if (stream == nullptr ||
        stream_size < sizeof(progpu_native_scene_header) ||
        stream_size > PROGPU_NATIVE_SCENE_MAX_STREAM_BYTES ||
        std::endian::native != std::endian::little) {
        return fail(header, PROGPU_NATIVE_SCENE_VALIDATION_HEADER, 0U);
    }
    const auto* bytes = static_cast<const std::byte*>(stream);
    header = read_record<progpu_native_scene_header>(bytes, 0U);
    if (header.struct_size < sizeof(progpu_native_scene_header) ||
        header.struct_size > stream_size ||
        header.magic != PROGPU_NATIVE_SCENE_STREAM_MAGIC ||
        header.stream_version != PROGPU_NATIVE_SCENE_STREAM_VERSION ||
        header.endian_marker != PROGPU_NATIVE_SCENE_STREAM_ENDIAN_MARKER ||
        header.flags != 0U || header.scene_id == 0U ||
        header.generation == 0U ||
        header.total_size != stream_size ||
        header.total_size > PROGPU_NATIVE_SCENE_MAX_STREAM_BYTES ||
        header.reserved0 != 0U || header.reserved1 != 0U) {
        return fail(header, PROGPU_NATIVE_SCENE_VALIDATION_HEADER, 0U);
    }
    if (header.command_count > PROGPU_NATIVE_SCENE_MAX_COMMANDS ||
        header.resource_count > PROGPU_NATIVE_SCENE_MAX_RESOURCES ||
        header.command_stride < sizeof(progpu_native_scene_command) ||
        header.resource_stride < sizeof(progpu_native_scene_resource) ||
        (header.command_stride & 7U) != 0U ||
        (header.resource_stride & 7U) != 0U ||
        (header.command_offset & 7U) != 0U ||
        (header.resource_offset & 7U) != 0U ||
        (header.arena_offset & 7U) != 0U ||
        header.command_offset < header.struct_size ||
        header.resource_offset < header.struct_size ||
        header.arena_offset < header.struct_size ||
        !table_fits(header.command_offset, header.command_count,
            header.command_stride, header.total_size) ||
        !table_fits(header.resource_offset, header.resource_count,
            header.resource_stride, header.total_size) ||
        !add_fits(header.arena_offset, header.arena_size, header.total_size)) {
        return fail(header, PROGPU_NATIVE_SCENE_VALIDATION_RANGE, 0U);
    }

    const std::uint32_t command_bytes = static_cast<std::uint32_t>(
        static_cast<std::uint64_t>(header.command_count) *
            header.command_stride);
    const std::uint32_t resource_bytes = static_cast<std::uint32_t>(
        static_cast<std::uint64_t>(header.resource_count) *
            header.resource_stride);
    if (ranges_overlap(header.command_offset, command_bytes,
            header.resource_offset, resource_bytes) ||
        ranges_overlap(header.command_offset, command_bytes,
            header.arena_offset, header.arena_size) ||
        ranges_overlap(header.resource_offset, resource_bytes,
            header.arena_offset, header.arena_size)) {
        return fail(header, PROGPU_NATIVE_SCENE_VALIDATION_RANGE, 0U);
    }

    std::uint64_t payload_bytes = 0U;
    std::uint64_t aggregate_brush_count = 0U;
    std::uint64_t aggregate_gradient_stop_count = 0U;
    std::uint64_t aggregate_text_style_count = 0U;
    std::uint64_t previous_resource_id = 0U;
    for (std::uint32_t index = 0U; index < header.resource_count; ++index) {
        const std::uint32_t offset = header.resource_offset +
            index * header.resource_stride;
        const auto resource =
            read_record<progpu_native_scene_resource>(bytes, offset);
        if (resource.struct_size < sizeof(progpu_native_scene_resource) ||
            resource.struct_size > header.resource_stride ||
            (resource.flags & ~known_resource_flags) != 0U ||
            resource.reserved != 0U || resource.resource_id == 0U ||
            !span_lives_in_arena(
                resource.payload_offset, resource.payload_size, header) ||
            !span_lives_in_arena(
                resource.auxiliary_offset,
                resource.auxiliary_size,
                header)) {
            return fail(header, PROGPU_NATIVE_SCENE_VALIDATION_RECORD, offset);
        }
        if ((resource.flags & PROGPU_NATIVE_SCENE_COLOR_GLYPH_BITMAPS) != 0U &&
            resource.kind != PROGPU_NATIVE_SCENE_RESOURCE_GLYPH_RUN) {
            return fail(header, PROGPU_NATIVE_SCENE_VALIDATION_RECORD, offset);
        }
        const bool external_image =
            (resource.flags & PROGPU_NATIVE_SCENE_EXTERNAL_IMAGE) != 0U;
        if (external_image &&
            (resource.kind != PROGPU_NATIVE_SCENE_RESOURCE_IMAGE ||
                resource.payload_size != 0U ||
                resource.auxiliary_size != 0U)) {
            return fail(header, PROGPU_NATIVE_SCENE_VALIDATION_RECORD, offset);
        }
        if (resource.resource_id <= previous_resource_id) {
            return fail(header, PROGPU_NATIVE_SCENE_VALIDATION_ID, offset);
        }
        if (resource.generation == 0U) {
            return fail(
                header,
                PROGPU_NATIVE_SCENE_VALIDATION_GENERATION,
                offset);
        }
        if (!is_known_resource(resource.kind) &&
            (resource.flags & PROGPU_NATIVE_SCENE_RECORD_REQUIRED) != 0U) {
            return fail(
                header,
                PROGPU_NATIVE_SCENE_VALIDATION_UNSUPPORTED,
                offset,
                PROGPU_NATIVE_STATUS_UNSUPPORTED);
        }
        if (is_known_resource(resource.kind) && resource.payload_size == 0U &&
            !external_image) {
            return fail(header, PROGPU_NATIVE_SCENE_VALIDATION_RECORD, offset);
        }
        if (resource.kind == PROGPU_NATIVE_SCENE_RESOURCE_STATE) {
            if (resource.payload_size != sizeof(progpu_native_scene_state) ||
                resource.auxiliary_size != 0U) {
                return fail(
                    header,
                    PROGPU_NATIVE_SCENE_VALIDATION_RECORD,
                    offset);
            }
            const auto state = read_record<progpu_native_scene_state>(
                bytes,
                resource.payload_offset);
            if (!valid_scene_state(state)) {
                return fail(
                    header,
                    PROGPU_NATIVE_SCENE_VALIDATION_VALUE,
                    resource.payload_offset);
            }
            if ((state.flags & PROGPU_NATIVE_SCENE_STATE_MASK) != 0U) {
                if (state.mask_resource_index >= index) {
                    return fail(
                        header,
                        PROGPU_NATIVE_SCENE_VALIDATION_RECORD,
                        resource.payload_offset);
                }
                const auto mask_resource =
                    read_record<progpu_native_scene_resource>(
                        bytes,
                        header.resource_offset +
                            static_cast<std::size_t>(
                                state.mask_resource_index) *
                                header.resource_stride);
                if (mask_resource.kind !=
                    PROGPU_NATIVE_SCENE_RESOURCE_LAYER_MASK) {
                    return fail(
                        header,
                        PROGPU_NATIVE_SCENE_VALIDATION_RECORD,
                        resource.payload_offset);
                }
            }
            if ((state.flags &
                    PROGPU_NATIVE_SCENE_STATE_GUIDELINE_SET) != 0U) {
                if (state.guideline_resource_index >= index) {
                    return fail(
                        header,
                        PROGPU_NATIVE_SCENE_VALIDATION_RECORD,
                        resource.payload_offset);
                }
                const auto guideline_resource =
                    read_record<progpu_native_scene_resource>(
                        bytes,
                        header.resource_offset +
                            static_cast<std::size_t>(
                                state.guideline_resource_index) *
                                header.resource_stride);
                if (guideline_resource.kind !=
                    PROGPU_NATIVE_SCENE_RESOURCE_GUIDELINE_SET) {
                    return fail(
                        header,
                        PROGPU_NATIVE_SCENE_VALIDATION_RECORD,
                        resource.payload_offset);
                }
            }
        }
        if (resource.kind == PROGPU_NATIVE_SCENE_RESOURCE_GUIDELINE_SET) {
            if (resource.payload_size <
                    sizeof(progpu_native_scene_guideline_set) ||
                resource.auxiliary_size != 0U) {
                return fail(
                    header,
                    PROGPU_NATIVE_SCENE_VALIDATION_RECORD,
                    offset);
            }
            const auto guidelines =
                read_record<progpu_native_scene_guideline_set>(
                    bytes, resource.payload_offset);
            const std::uint64_t coordinate_count =
                static_cast<std::uint64_t>(guidelines.guideline_x_count) +
                guidelines.guideline_y_count;
            const bool explicit_offsets = (guidelines.flags &
                PROGPU_NATIVE_SCENE_GUIDELINE_EXPLICIT_OFFSETS) != 0U;
            const std::uint64_t expected_size = sizeof(guidelines) +
                coordinate_count * sizeof(double) *
                    (explicit_offsets ? 2U : 1U);
            const bool multiple = guidelines.guideline_x_count > 1U ||
                guidelines.guideline_y_count > 1U;
            const std::uint32_t multi_flags = guidelines.flags &
                (PROGPU_NATIVE_SCENE_GUIDELINE_COMPOSITE_ONLY |
                 PROGPU_NATIVE_SCENE_GUIDELINE_PER_POINT);
            if (guidelines.struct_size != sizeof(guidelines) ||
                (guidelines.flags &
                    ~(PROGPU_NATIVE_SCENE_GUIDELINE_COMPOSITE_ONLY |
                      PROGPU_NATIVE_SCENE_GUIDELINE_PER_POINT |
                      PROGPU_NATIVE_SCENE_GUIDELINE_EXPLICIT_OFFSETS)) != 0U ||
                multiple != (multi_flags != 0U) ||
                (multi_flags & (multi_flags - 1U)) != 0U ||
                guidelines.guideline_x_count >
                    PROGPU_NATIVE_SCENE_MAX_GUIDELINES_PER_AXIS ||
                guidelines.guideline_y_count >
                    PROGPU_NATIVE_SCENE_MAX_GUIDELINES_PER_AXIS ||
                expected_size != resource.payload_size) {
                return fail(
                    header,
                    PROGPU_NATIVE_SCENE_VALIDATION_VALUE,
                    resource.payload_offset);
            }
            std::uint64_t coordinate_index = 0U;
            for (std::uint32_t axis = 0U; axis < 2U; ++axis) {
                const std::uint32_t count = axis == 0U
                    ? guidelines.guideline_x_count
                    : guidelines.guideline_y_count;
                double previous = -std::numeric_limits<double>::infinity();
                for (std::uint32_t axis_index = 0U;
                     axis_index < count;
                     ++axis_index, ++coordinate_index) {
                    const auto coordinate = read_record<double>(
                        bytes,
                        resource.payload_offset + sizeof(guidelines) +
                            coordinate_index * sizeof(double));
                    if (!std::isfinite(coordinate) ||
                        coordinate < previous) {
                        return fail(
                            header,
                            PROGPU_NATIVE_SCENE_VALIDATION_VALUE,
                            resource.payload_offset + sizeof(guidelines) +
                                static_cast<std::uint32_t>(
                                    coordinate_index * sizeof(double)));
                    }
                    previous = coordinate;
                }
            }
            if (explicit_offsets) {
                for (std::uint64_t offset_index = 0U;
                     offset_index < coordinate_count;
                     ++offset_index) {
                    const auto explicit_offset = read_record<double>(
                        bytes,
                        resource.payload_offset + sizeof(guidelines) +
                            (coordinate_count + offset_index) *
                                sizeof(double));
                    if (!std::isfinite(explicit_offset) ||
                        std::abs(explicit_offset) > 1.0) {
                        return fail(
                            header,
                            PROGPU_NATIVE_SCENE_VALIDATION_VALUE,
                            resource.payload_offset + sizeof(guidelines) +
                                static_cast<std::uint32_t>(
                                    (coordinate_count + offset_index) *
                                        sizeof(double)));
                    }
                }
            }
        }
        if (semantic::is_color_glyph_resource(resource)) {
            std::uint32_t bitmap_error_offset = resource.payload_offset;
            if (!semantic::validate_color_glyph_resource(
                    bytes,
                    resource,
                    bitmap_error_offset)) {
                return fail(
                    header,
                    PROGPU_NATIVE_SCENE_VALIDATION_VALUE,
                    bitmap_error_offset);
            }
        }
        if (resource.kind == PROGPU_NATIVE_SCENE_RESOURCE_LAYER_MASK) {
            std::uint32_t mask_error_offset = resource.payload_offset;
            semantic::semantic_layer_mask parsed_mask{};
            if (!semantic::validate_layer_mask_resource(
                    bytes, resource, mask_error_offset, &parsed_mask)) {
                return fail(
                    header,
                    PROGPU_NATIVE_SCENE_VALIDATION_VALUE,
                    mask_error_offset);
            }
            const auto validate_nested = [&](const std::byte* nested,
                                             std::uint32_t size) noexcept {
                const validation_result nested_result = validate(nested, size);
                return nested_result.status == PROGPU_NATIVE_STATUS_SUCCESS;
            };
            if (parsed_mask.kind == PROGPU_NATIVE_SCENE_LAYER_MASK_PICTURE &&
                !validate_nested(
                    parsed_mask.composite_picture_streams,
                    parsed_mask.picture.stream_size)) {
                return fail(
                    header,
                    PROGPU_NATIVE_SCENE_VALIDATION_VALUE,
                    resource.auxiliary_offset);
            }
            if (parsed_mask.kind == PROGPU_NATIVE_SCENE_LAYER_MASK_COMPOSITE) {
                for (std::uint32_t picture_index = 0U;
                     picture_index < parsed_mask.composite.picture_mask_count;
                     ++picture_index) {
                    const auto& picture =
                        parsed_mask.composite_picture_masks[picture_index];
                    if (!validate_nested(
                            parsed_mask.composite_picture_streams +
                                picture.stream_offset,
                            picture.stream_size)) {
                        return fail(
                            header,
                            PROGPU_NATIVE_SCENE_VALIDATION_VALUE,
                            resource.auxiliary_offset);
                    }
                }
            }
        }
        if (resource.kind == PROGPU_NATIVE_SCENE_RESOURCE_EFFECT_CHAIN) {
            if (resource.payload_size !=
                    sizeof(progpu_native_scene_effect_chain) ||
                resource.auxiliary_size == 0U) {
                return fail(
                    header,
                    PROGPU_NATIVE_SCENE_VALIDATION_RECORD,
                    offset);
            }
            const auto chain = read_record<progpu_native_scene_effect_chain>(
                bytes,
                resource.payload_offset);
            if (chain.struct_size != sizeof(chain) ||
                chain.effect_count == 0U ||
                chain.effect_count > PROGPU_NATIVE_MAX_GROUP_EFFECTS ||
                chain.revision == 0U || chain.reserved != 0U ||
                resource.auxiliary_size !=
                    static_cast<std::uint64_t>(chain.effect_count) *
                        sizeof(progpu_native_group_effect)) {
                return fail(
                    header,
                    PROGPU_NATIVE_SCENE_VALIDATION_VALUE,
                    resource.payload_offset);
            }
            for (std::uint32_t effect_index = 0U;
                 effect_index < chain.effect_count;
                 ++effect_index) {
                const auto effect = read_record<progpu_native_group_effect>(
                    bytes,
                    resource.auxiliary_offset +
                        static_cast<std::size_t>(effect_index) *
                            sizeof(progpu_native_group_effect));
                if (!valid_scene_effect(effect)) {
                    return fail(
                        header,
                        PROGPU_NATIVE_SCENE_VALIDATION_VALUE,
                        resource.auxiliary_offset +
                            effect_index *
                                sizeof(progpu_native_group_effect));
                }
            }
        }
        if (resource.kind == PROGPU_NATIVE_SCENE_RESOURCE_BRUSH_TABLE) {
            std::uint32_t brush_error_offset = resource.payload_offset;
            if (!semantic::validate_brush_table(
                    bytes,
                    resource,
                    brush_error_offset)) {
                return fail(
                    header,
                    PROGPU_NATIVE_SCENE_VALIDATION_VALUE,
                    brush_error_offset);
            }
            aggregate_brush_count += resource.payload_size /
                sizeof(progpu_native_scene_brush);
            aggregate_gradient_stop_count += resource.auxiliary_size /
                sizeof(progpu_native_scene_gradient_stop);
            if (aggregate_brush_count >
                    PROGPU_NATIVE_SCENE_MAX_BRUSHES ||
                aggregate_gradient_stop_count >
                    PROGPU_NATIVE_SCENE_MAX_GRADIENT_STOPS) {
                return fail(
                    header,
                    PROGPU_NATIVE_SCENE_VALIDATION_RANGE,
                    offset,
                    PROGPU_NATIVE_STATUS_OUT_OF_MEMORY);
            }
        }
        if (resource.kind ==
            PROGPU_NATIVE_SCENE_RESOURCE_TEXT_STYLE_TABLE) {
            std::uint32_t style_error_offset = resource.payload_offset;
            if (!semantic::validate_text_style_table(
                    bytes,
                    resource,
                    style_error_offset)) {
                return fail(
                    header,
                    PROGPU_NATIVE_SCENE_VALIDATION_VALUE,
                    style_error_offset);
            }
            aggregate_text_style_count += resource.payload_size /
                sizeof(progpu_native_scene_text_style);
            if (aggregate_text_style_count >
                PROGPU_NATIVE_SCENE_MAX_TEXT_STYLES) {
                return fail(
                    header,
                    PROGPU_NATIVE_SCENE_VALIDATION_RANGE,
                    offset,
                    PROGPU_NATIVE_STATUS_OUT_OF_MEMORY);
            }
        }
        if (resource.kind ==
            PROGPU_NATIVE_SCENE_RESOURCE_HIT_TEST_INDEX) {
            std::uint32_t hit_test_error_offset = resource.payload_offset;
            const auto hit_test_validation = validate_hit_test_resource(
                bytes, resource, hit_test_error_offset);
            if (hit_test_validation ==
                hit_test_resource_validation::out_of_memory) {
                return fail(
                    header,
                    PROGPU_NATIVE_SCENE_VALIDATION_RANGE,
                    hit_test_error_offset,
                    PROGPU_NATIVE_STATUS_OUT_OF_MEMORY);
            }
            if (hit_test_validation != hit_test_resource_validation::valid) {
                return fail(
                    header,
                    PROGPU_NATIVE_SCENE_VALIDATION_VALUE,
                    hit_test_error_offset);
            }
        }
        if (resource.kind == PROGPU_NATIVE_SCENE_RESOURCE_LINE_3D_BATCH) {
            if (resource.payload_size == 0U ||
                resource.payload_size % sizeof(progpu_native_scene_line_3d) !=
                    0U ||
                resource.auxiliary_size != 0U) {
                return fail(
                    header,
                    PROGPU_NATIVE_SCENE_VALIDATION_RECORD,
                    offset);
            }
            const std::uint32_t line_count = resource.payload_size /
                sizeof(progpu_native_scene_line_3d);
            for (std::uint32_t line_index = 0U;
                 line_index < line_count;
                 ++line_index) {
                const std::uint32_t line_offset = resource.payload_offset +
                    line_index * sizeof(progpu_native_scene_line_3d);
                const auto line = read_record<progpu_native_scene_line_3d>(
                    bytes, line_offset);
                if (!semantic::is_valid_semantic_line_3d(line)) {
                    return fail(
                        header,
                        PROGPU_NATIVE_SCENE_VALIDATION_VALUE,
                        line_offset);
                }
            }
        }
        if (resource.kind == PROGPU_NATIVE_SCENE_RESOURCE_MESH_3D_BATCH) {
            if (resource.payload_size == 0U ||
                resource.payload_size % sizeof(progpu_native_scene_mesh_3d) !=
                    0U ||
                resource.auxiliary_size == 0U) {
                return fail(
                    header,
                    PROGPU_NATIVE_SCENE_VALIDATION_RECORD,
                    offset);
            }
            const std::uint32_t mesh_count = resource.payload_size /
                sizeof(progpu_native_scene_mesh_3d);
            std::size_t vertex_count = 0U;
            std::size_t index_count = 0U;
            std::size_t light_count = 0U;
            for (std::uint32_t mesh_index = 0U;
                 mesh_index < mesh_count;
                 ++mesh_index) {
                const auto mesh = read_record<progpu_native_scene_mesh_3d>(
                    bytes,
                    resource.payload_offset + mesh_index *
                        sizeof(progpu_native_scene_mesh_3d));
                vertex_count = std::max(
                    vertex_count,
                    static_cast<std::size_t>(mesh.vertex_offset) +
                        mesh.vertex_count);
                index_count = std::max(
                    index_count,
                    static_cast<std::size_t>(mesh.index_offset) +
                        mesh.index_count);
                light_count = std::max(
                    light_count,
                    static_cast<std::size_t>(mesh.light_offset) +
                        mesh.light_count);
            }
            const std::uint64_t vertex_bytes =
                static_cast<std::uint64_t>(vertex_count) *
                    sizeof(progpu_native_scene_mesh_3d_vertex);
            const std::uint64_t index_bytes =
                static_cast<std::uint64_t>(index_count) * sizeof(std::uint32_t);
            const std::uint64_t light_bytes =
                static_cast<std::uint64_t>(light_count) *
                    sizeof(progpu_native_scene_light_3d);
            if (vertex_bytes + index_bytes + light_bytes !=
                resource.auxiliary_size) {
                return fail(
                    header,
                    PROGPU_NATIVE_SCENE_VALIDATION_RECORD,
                    resource.auxiliary_offset);
            }
            for (std::size_t vertex_index = 0U;
                 vertex_index < vertex_count;
                 ++vertex_index) {
                const auto vertex = read_record<
                    progpu_native_scene_mesh_3d_vertex>(
                        bytes,
                        resource.auxiliary_offset + vertex_index *
                            sizeof(progpu_native_scene_mesh_3d_vertex));
                if (!semantic::is_valid_semantic_mesh_3d_vertex(vertex)) {
                    const auto error_offset = static_cast<std::uint32_t>(
                        resource.auxiliary_offset + vertex_index *
                            sizeof(progpu_native_scene_mesh_3d_vertex));
                    return fail(
                        header,
                        PROGPU_NATIVE_SCENE_VALIDATION_VALUE,
                        error_offset);
                }
            }
            const std::size_t indices_offset = resource.auxiliary_offset +
                static_cast<std::size_t>(vertex_bytes);
            const std::size_t lights_offset = indices_offset +
                static_cast<std::size_t>(index_bytes);
            for (std::size_t light_index = 0U;
                 light_index < light_count;
                 ++light_index) {
                const auto light = read_record<progpu_native_scene_light_3d>(
                    bytes,
                    lights_offset + light_index *
                        sizeof(progpu_native_scene_light_3d));
                if (!semantic::is_valid_semantic_light_3d(light)) {
                    return fail(
                        header,
                        PROGPU_NATIVE_SCENE_VALIDATION_VALUE,
                        static_cast<std::uint32_t>(
                            lights_offset + light_index *
                                sizeof(progpu_native_scene_light_3d)));
                }
            }
            for (std::uint32_t mesh_index = 0U;
                 mesh_index < mesh_count;
                 ++mesh_index) {
                const auto mesh = read_record<progpu_native_scene_mesh_3d>(
                    bytes,
                    resource.payload_offset + mesh_index *
                        sizeof(progpu_native_scene_mesh_3d));
                if (!semantic::is_valid_semantic_mesh_3d(
                        mesh, vertex_count, index_count, light_count)) {
                    return fail(
                        header,
                        PROGPU_NATIVE_SCENE_VALIDATION_VALUE,
                        resource.payload_offset + mesh_index *
                            sizeof(progpu_native_scene_mesh_3d));
                }
                for (std::size_t mesh_index_offset = mesh.index_offset;
                     mesh_index_offset <
                        static_cast<std::size_t>(mesh.index_offset) +
                            mesh.index_count;
                     ++mesh_index_offset) {
                    const auto value = read_record<std::uint32_t>(
                        bytes,
                        indices_offset + mesh_index_offset *
                            sizeof(std::uint32_t));
                    if (value >= mesh.vertex_count) {
                        return fail(
                            header,
                            PROGPU_NATIVE_SCENE_VALIDATION_VALUE,
                            static_cast<std::uint32_t>(
                                indices_offset + mesh_index_offset *
                                    sizeof(std::uint32_t)));
                    }
                }
            }
        }
        previous_resource_id = resource.resource_id;
        payload_bytes += resource.payload_size;
        payload_bytes += resource.auxiliary_size;
    }

    std::array<std::uint8_t, PROGPU_NATIVE_SCENE_MAX_STACK_DEPTH> stack{};
    std::uint32_t depth = 0U;
    std::uint32_t materialized_layer_depth = 0U;
    std::uint32_t maximum_depth = 0U;
    std::uint32_t draw_count = 0U;
    try {
        if (!command_ids_are_unique(bytes, header)) {
            return fail(
                header,
                PROGPU_NATIVE_SCENE_VALIDATION_ID,
                header.command_offset);
        }
    } catch (const std::bad_alloc&) {
        return fail(
            header,
            PROGPU_NATIVE_SCENE_VALIDATION_RECORD,
            header.command_offset,
            PROGPU_NATIVE_STATUS_OUT_OF_MEMORY);
    } catch (...) {
        return fail(
            header,
            PROGPU_NATIVE_SCENE_VALIDATION_RECORD,
            header.command_offset,
            PROGPU_NATIVE_STATUS_INTERNAL_ERROR);
    }

    for (std::uint32_t index = 0U; index < header.command_count; ++index) {
        const std::uint32_t offset = header.command_offset +
            index * header.command_stride;
        const auto command =
            read_record<progpu_native_scene_command>(bytes, offset);
        if (command.struct_size < sizeof(progpu_native_scene_command) ||
            command.struct_size > header.command_stride ||
            (command.flags & ~known_command_flags) != 0U ||
            command.reserved != 0U || command.command_id == 0U ||
            command.reserved0 != 0U || command.reserved1 != 0U ||
            !span_lives_in_arena(
                command.payload_offset, command.payload_size, header) ||
            !finite_bounds(command)) {
            return fail(header, PROGPU_NATIVE_SCENE_VALIDATION_RECORD, offset);
        }
        if (!is_known_command(command.kind)) {
            if ((command.flags & PROGPU_NATIVE_SCENE_RECORD_REQUIRED) != 0U) {
                return fail(
                    header,
                    PROGPU_NATIVE_SCENE_VALIDATION_UNSUPPORTED,
                    offset,
                    PROGPU_NATIVE_STATUS_UNSUPPORTED);
            }
            continue;
        }
        if ((command.flags & PROGPU_NATIVE_SCENE_GLYPH_STYLED) != 0U &&
            command.kind != PROGPU_NATIVE_SCENE_COMMAND_DRAW_GLYPH_RUN) {
            return fail(
                header,
                PROGPU_NATIVE_SCENE_VALIDATION_RECORD,
                offset);
        }
        if (command.state_index != PROGPU_NATIVE_SCENE_NO_INDEX) {
            if (command.kind == PROGPU_NATIVE_SCENE_COMMAND_RESTORE ||
                command.kind == PROGPU_NATIVE_SCENE_COMMAND_POP_LAYER) {
                return fail(
                    header,
                    PROGPU_NATIVE_SCENE_VALIDATION_RECORD,
                    offset);
            }
            if (command.state_index >= header.resource_count) {
                return fail(
                    header,
                    PROGPU_NATIVE_SCENE_VALIDATION_RANGE,
                    offset);
            }
            const std::size_t state_offset = header.resource_offset +
                static_cast<std::size_t>(command.state_index) *
                    header.resource_stride;
            const auto state_resource =
                read_record<progpu_native_scene_resource>(
                    bytes,
                    state_offset);
            if (state_resource.kind != PROGPU_NATIVE_SCENE_RESOURCE_STATE) {
                return fail(
                    header,
                    PROGPU_NATIVE_SCENE_VALIDATION_RECORD,
                    offset);
            }
            const auto state = read_record<progpu_native_scene_state>(
                bytes, state_resource.payload_offset);
            if ((state.flags &
                    PROGPU_NATIVE_SCENE_STATE_GUIDELINE_SET) != 0U) {
                const auto guideline_resource = read_record<
                    progpu_native_scene_resource>(
                        bytes,
                        header.resource_offset +
                            static_cast<std::size_t>(
                                state.guideline_resource_index) *
                                header.resource_stride);
                const auto guidelines = read_record<
                    progpu_native_scene_guideline_set>(
                        bytes, guideline_resource.payload_offset);
                if ((guidelines.flags &
                        PROGPU_NATIVE_SCENE_GUIDELINE_COMPOSITE_ONLY) != 0U) {
                    return fail(
                        header,
                        PROGPU_NATIVE_SCENE_VALIDATION_VALUE,
                        offset);
                }
            }
        }
        if (is_draw_command(command.kind)) {
            if (command.resource_index >= header.resource_count) {
                return fail(
                    header,
                    PROGPU_NATIVE_SCENE_VALIDATION_RANGE,
                    offset);
            }
            const std::size_t resource_offset = header.resource_offset +
                static_cast<std::size_t>(command.resource_index) *
                    header.resource_stride;
            const auto resource = read_record<progpu_native_scene_resource>(
                bytes,
                resource_offset);
            if (resource.kind != expected_resource_kind(command.kind)) {
                return fail(
                    header,
                    PROGPU_NATIVE_SCENE_VALIDATION_RECORD,
                    offset);
            }
            if (command.kind ==
                    PROGPU_NATIVE_SCENE_COMMAND_DRAW_LINE_3D_BATCH ||
                command.kind ==
                    PROGPU_NATIVE_SCENE_COMMAND_DRAW_MESH_3D_BATCH) {
                const bool mesh_material_payload = command.kind ==
                        PROGPU_NATIVE_SCENE_COMMAND_DRAW_MESH_3D_BATCH &&
                    command.payload_size >
                        sizeof(progpu_native_scene_camera_3d);
                if (command.payload_size <
                        sizeof(progpu_native_scene_camera_3d) ||
                    (!mesh_material_payload && command.payload_size !=
                        sizeof(progpu_native_scene_camera_3d))) {
                    return fail(
                        header,
                        PROGPU_NATIVE_SCENE_VALIDATION_RECORD,
                        offset);
                }
                const auto camera = read_record<
                    progpu_native_scene_camera_3d>(
                        bytes, command.payload_offset);
                if (!semantic::is_valid_semantic_camera_3d(camera)) {
                    return fail(
                        header,
                        PROGPU_NATIVE_SCENE_VALIDATION_VALUE,
                        command.payload_offset);
                }
                if (mesh_material_payload) {
                    const std::uint32_t materials_offset =
                        command.payload_offset +
                            sizeof(progpu_native_scene_camera_3d);
                    if (command.payload_size <
                        sizeof(progpu_native_scene_camera_3d) +
                            sizeof(progpu_native_scene_mesh_3d_materials)) {
                        return fail(
                            header,
                            PROGPU_NATIVE_SCENE_VALIDATION_RECORD,
                            offset);
                    }
                    const auto materials = read_record<
                        progpu_native_scene_mesh_3d_materials>(
                            bytes, materials_offset);
                    const std::uint32_t mesh_count =
                        resource.payload_size /
                            sizeof(progpu_native_scene_mesh_3d);
                    const std::uint64_t expected_payload_size =
                        sizeof(progpu_native_scene_camera_3d) +
                        sizeof(materials) +
                        static_cast<std::uint64_t>(materials.brush_count) *
                            sizeof(std::uint32_t);
                    if (materials.struct_size != sizeof(materials) ||
                        materials.reserved0 != 0U ||
                        materials.brush_count != mesh_count ||
                        expected_payload_size != command.payload_size ||
                        materials.brush_resource_index >=
                            header.resource_count) {
                        return fail(
                            header,
                            PROGPU_NATIVE_SCENE_VALIDATION_VALUE,
                            materials_offset);
                    }
                    const auto brush_resource = read_record<
                        progpu_native_scene_resource>(
                            bytes,
                            header.resource_offset +
                                static_cast<std::size_t>(
                                    materials.brush_resource_index) *
                                    header.resource_stride);
                    if (brush_resource.kind !=
                            PROGPU_NATIVE_SCENE_RESOURCE_BRUSH_TABLE ||
                        brush_resource.payload_size == 0U ||
                        brush_resource.payload_size %
                            sizeof(progpu_native_scene_brush) != 0U) {
                        return fail(
                            header,
                            PROGPU_NATIVE_SCENE_VALIDATION_RECORD,
                            materials_offset);
                    }
                    const std::uint32_t brush_count =
                        brush_resource.payload_size /
                            sizeof(progpu_native_scene_brush);
                    const std::uint32_t indices_offset = materials_offset +
                        sizeof(materials);
                    for (std::uint32_t material_index = 0U;
                         material_index < materials.brush_count;
                         ++material_index) {
                        const auto brush_index = read_record<std::uint32_t>(
                            bytes,
                            indices_offset +
                                static_cast<std::size_t>(material_index) *
                                    sizeof(std::uint32_t));
                        if (brush_index >= brush_count) {
                            return fail(
                                header,
                                PROGPU_NATIVE_SCENE_VALIDATION_RANGE,
                                indices_offset + material_index *
                                    sizeof(std::uint32_t));
                        }
                        const auto brush = read_record<
                            progpu_native_scene_brush>(
                                bytes,
                                brush_resource.payload_offset +
                                    static_cast<std::size_t>(brush_index) *
                                        sizeof(progpu_native_scene_brush));
                        if (brush.type !=
                                PROGPU_NATIVE_SCENE_BRUSH_SOLID &&
                            brush.type !=
                                PROGPU_NATIVE_SCENE_BRUSH_LINEAR_GRADIENT &&
                            brush.type !=
                                PROGPU_NATIVE_SCENE_BRUSH_RADIAL_GRADIENT) {
                            return fail(
                                header,
                                PROGPU_NATIVE_SCENE_VALIDATION_VALUE,
                                brush_resource.payload_offset + brush_index *
                                    sizeof(progpu_native_scene_brush));
                        }
                    }
                }
            }
            if (command.kind == PROGPU_NATIVE_SCENE_COMMAND_DRAW_ANALYTIC ||
                    command.kind == PROGPU_NATIVE_SCENE_COMMAND_DRAW_PATH ||
                    command.kind == PROGPU_NATIVE_SCENE_COMMAND_DRAW_GEOMETRY ||
                    command.kind ==
                        PROGPU_NATIVE_SCENE_COMMAND_DRAW_POINT_BATCH ||
                    command.kind ==
                        PROGPU_NATIVE_SCENE_COMMAND_DRAW_VERTEX_MESH ||
                    command.kind ==
                        PROGPU_NATIVE_SCENE_COMMAND_DRAW_STROKE_BATCH) {
                if (command.payload_size == 0U) {
                    ++draw_count;
                    payload_bytes += command.payload_size;
                    continue;
                }
                const std::uint32_t record_size = command.kind ==
                        PROGPU_NATIVE_SCENE_COMMAND_DRAW_ANALYTIC
                    ? sizeof(progpu_native_analytic_primitive)
                    : command.kind == PROGPU_NATIVE_SCENE_COMMAND_DRAW_GEOMETRY
                        ? sizeof(progpu_native_geometry_primitive)
                        : command.kind ==
                                PROGPU_NATIVE_SCENE_COMMAND_DRAW_POINT_BATCH
                            ? sizeof(progpu_native_scene_point_batch)
                        : command.kind ==
                                PROGPU_NATIVE_SCENE_COMMAND_DRAW_VERTEX_MESH
                            ? sizeof(progpu_native_scene_vertex_mesh)
                        : command.kind ==
                                PROGPU_NATIVE_SCENE_COMMAND_DRAW_STROKE_BATCH
                            ? sizeof(progpu_native_scene_stroke)
                        : sizeof(progpu_native_scene_path_fill);
                if (resource.payload_size % record_size != 0U ||
                    resource.payload_size / record_size >
                        PROGPU_NATIVE_SCENE_MAX_DRAW_BRUSH_INDICES) {
                    return fail(
                        header,
                        PROGPU_NATIVE_SCENE_VALIDATION_RECORD,
                        resource.payload_offset);
                }
                std::uint32_t brush_error_offset =
                    command.payload_offset;
                if (!semantic::validate_draw_brushes(
                        bytes,
                        header,
                        command,
                        resource.payload_size / record_size,
                        brush_error_offset)) {
                    return fail(
                        header,
                        PROGPU_NATIVE_SCENE_VALIDATION_VALUE,
                        brush_error_offset);
                }
            }
            if (command.kind ==
                    PROGPU_NATIVE_SCENE_COMMAND_DRAW_GLYPH_RUN &&
                (command.flags & PROGPU_NATIVE_SCENE_GLYPH_STYLED) != 0U) {
                std::uint32_t style_error_offset = command.payload_offset;
                if (!semantic::validate_styled_glyph_draw(
                        bytes,
                        header,
                        command,
                        style_error_offset)) {
                    return fail(
                        header,
                        PROGPU_NATIVE_SCENE_VALIDATION_VALUE,
                        style_error_offset);
                }
            }
            ++draw_count;
            payload_bytes += command.payload_size;
            continue;
        }
        if (command.resource_index != PROGPU_NATIVE_SCENE_NO_INDEX ||
            (command.kind != PROGPU_NATIVE_SCENE_COMMAND_PUSH_LAYER &&
                (command.payload_size != 0U ||
                    command.payload_offset != 0U))) {
            return fail(header, PROGPU_NATIVE_SCENE_VALIDATION_RECORD, offset);
        }
        bool layer_is_materialized = false;
        if (command.kind == PROGPU_NATIVE_SCENE_COMMAND_PUSH_LAYER &&
            command.payload_size != 0U) {
            if (command.payload_size != sizeof(progpu_native_scene_layer)) {
                return fail(
                    header,
                    PROGPU_NATIVE_SCENE_VALIDATION_VALUE,
                    offset);
            }
            const auto layer = read_record<progpu_native_scene_layer>(
                bytes,
                command.payload_offset);
            if (!valid_scene_layer(layer)) {
                return fail(
                    header,
                    PROGPU_NATIVE_SCENE_VALIDATION_VALUE,
                    offset);
            }
            const auto valid_layer_resource = [&](std::uint32_t index,
                                                   std::uint32_t kind) {
                if (index == PROGPU_NATIVE_SCENE_NO_INDEX) {
                    return true;
                }
                if (index >= header.resource_count) {
                    return false;
                }
                const auto resource = read_record<
                    progpu_native_scene_resource>(
                        bytes,
                        header.resource_offset +
                            static_cast<std::size_t>(index) *
                                header.resource_stride);
                return resource.kind == kind;
            };
            if (!valid_layer_resource(
                    layer.mask_resource_index,
                    PROGPU_NATIVE_SCENE_RESOURCE_LAYER_MASK) ||
                !valid_layer_resource(
                    layer.effect_resource_index,
                    PROGPU_NATIVE_SCENE_RESOURCE_EFFECT_CHAIN) ||
                (((layer.flags &
                        (PROGPU_NATIVE_SCENE_LAYER_CACHE_LOCAL_SPACE |
                            PROGPU_NATIVE_SCENE_LAYER_COMPOSITE_STATE)) !=
                    0U) &&
                    (layer.reserved0 == PROGPU_NATIVE_SCENE_NO_INDEX ||
                        !valid_layer_resource(
                            layer.reserved0,
                            PROGPU_NATIVE_SCENE_RESOURCE_STATE)))) {
                return fail(
                    header,
                    PROGPU_NATIVE_SCENE_VALIDATION_RECORD,
                    offset);
            }
            const bool local_cache = (layer.flags &
                PROGPU_NATIVE_SCENE_LAYER_CACHE_LOCAL_SPACE) != 0U;
            const bool has_composite_state = local_cache ||
                (layer.flags &
                    PROGPU_NATIVE_SCENE_LAYER_COMPOSITE_STATE) != 0U;
            if (has_composite_state) {
                const auto composite_resource = read_record<
                    progpu_native_scene_resource>(
                        bytes,
                        header.resource_offset +
                            static_cast<std::size_t>(layer.reserved0) *
                                header.resource_stride);
                const auto composite_state = read_record<
                    progpu_native_scene_state>(
                        bytes, composite_resource.payload_offset);
                const std::uint32_t composite_flags = local_cache
                    ? PROGPU_NATIVE_SCENE_STATE_CLIP_RECT |
                        PROGPU_NATIVE_SCENE_STATE_GUIDELINE_SET
                    : PROGPU_NATIVE_SCENE_STATE_CLIP_RECT;
                const bool canonical_transform = local_cache ||
                    (composite_state.transform.m11 == 1.0F &&
                        composite_state.transform.m12 == 0.0F &&
                        composite_state.transform.m21 == 0.0F &&
                        composite_state.transform.m22 == 1.0F &&
                        composite_state.transform.m31 == 0.0F &&
                        composite_state.transform.m32 == 0.0F);
                bool composite_guideline_is_valid = true;
                if ((composite_state.flags &
                        PROGPU_NATIVE_SCENE_STATE_GUIDELINE_SET) != 0U) {
                    const auto guideline_resource = read_record<
                        progpu_native_scene_resource>(
                            bytes,
                            header.resource_offset +
                                static_cast<std::size_t>(
                                    composite_state.guideline_resource_index) *
                                    header.resource_stride);
                    const auto guidelines = read_record<
                        progpu_native_scene_guideline_set>(
                            bytes, guideline_resource.payload_offset);
                    composite_guideline_is_valid = (guidelines.flags &
                        PROGPU_NATIVE_SCENE_GUIDELINE_PER_POINT) == 0U;
                }
                if ((composite_state.flags & ~composite_flags) != 0U ||
                    !canonical_transform ||
                    !composite_guideline_is_valid ||
                    composite_state.opacity != 1.0F ||
                    composite_state.mask_resource_index != 0U) {
                    return fail(
                        header,
                        PROGPU_NATIVE_SCENE_VALIDATION_VALUE,
                        offset);
                }
            }
            layer_is_materialized = layer_requires_materialization(layer);
        }
        payload_bytes += command.payload_size;
        if (command.kind == PROGPU_NATIVE_SCENE_COMMAND_SAVE ||
            command.kind == PROGPU_NATIVE_SCENE_COMMAND_PUSH_LAYER) {
            const bool is_layer = command.kind ==
                PROGPU_NATIVE_SCENE_COMMAND_PUSH_LAYER;
            if (depth == PROGPU_NATIVE_SCENE_MAX_STACK_DEPTH ||
                (layer_is_materialized && materialized_layer_depth ==
                    PROGPU_NATIVE_SCENE_MAX_MATERIALIZED_LAYERS)) {
                return fail(
                    header,
                    PROGPU_NATIVE_SCENE_VALIDATION_STACK,
                    offset);
            }
            stack[depth++] = !is_layer
                ? 1U
                : (layer_is_materialized ? 3U : 2U);
            materialized_layer_depth += layer_is_materialized ? 1U : 0U;
            maximum_depth = std::max(maximum_depth, depth);
        } else {
            const bool expects_layer = command.kind ==
                PROGPU_NATIVE_SCENE_COMMAND_POP_LAYER;
            const std::uint8_t actual = depth == 0U
                ? 0U
                : stack[depth - 1U];
            if (depth == 0U || (expects_layer
                    ? (actual != 2U && actual != 3U)
                    : actual != 1U)) {
                return fail(
                    header,
                    PROGPU_NATIVE_SCENE_VALIDATION_STACK,
                    offset);
            }
            --depth;
            materialized_layer_depth -= actual == 3U ? 1U : 0U;
        }
    }
    if (depth != 0U) {
        return fail(
            header,
            PROGPU_NATIVE_SCENE_VALIDATION_STACK,
            header.command_offset + command_bytes);
    }

    validation_result result{};
    result.status = PROGPU_NATIVE_STATUS_SUCCESS;
    result.error = PROGPU_NATIVE_SCENE_VALIDATION_NONE;
    result.header = header;
    result.draw_count = draw_count;
    result.maximum_stack_depth = maximum_depth;
    result.payload_bytes = payload_bytes;
    return result;
}

bool generations_do_not_regress(
    const void* previous_stream,
    const progpu_native_scene_header& previous_header,
    const void* next_stream,
    const progpu_native_scene_header& next_header,
    std::uint32_t& error_offset) noexcept {
    error_offset = 0U;
    const auto* previous_bytes =
        static_cast<const std::byte*>(previous_stream);
    const auto* next_bytes = static_cast<const std::byte*>(next_stream);
    std::uint32_t previous_index = 0U;
    std::uint32_t next_index = 0U;
    while (previous_index < previous_header.resource_count &&
        next_index < next_header.resource_count) {
        const auto old_resource = read_record<progpu_native_scene_resource>(
            previous_bytes,
            previous_header.resource_offset +
                static_cast<std::size_t>(previous_index) *
                    previous_header.resource_stride);
        const std::uint32_t next_offset = next_header.resource_offset +
            next_index * next_header.resource_stride;
        const auto new_resource = read_record<progpu_native_scene_resource>(
            next_bytes,
            next_offset);
        if (old_resource.resource_id < new_resource.resource_id) {
            ++previous_index;
        } else if (new_resource.resource_id < old_resource.resource_id) {
            ++next_index;
        } else {
            if (new_resource.generation < old_resource.generation) {
                error_offset = next_offset;
                return false;
            }
            if (new_resource.generation == old_resource.generation) {
                const bool metadata_matches =
                    new_resource.kind == old_resource.kind &&
                    new_resource.flags == old_resource.flags &&
                    new_resource.payload_size == old_resource.payload_size &&
                    new_resource.auxiliary_size ==
                        old_resource.auxiliary_size;
                const bool payload_matches = metadata_matches &&
                    (new_resource.payload_size == 0U ||
                        std::memcmp(
                            next_bytes + new_resource.payload_offset,
                            previous_bytes + old_resource.payload_offset,
                            new_resource.payload_size) == 0);
                const bool auxiliary_matches = payload_matches &&
                    (new_resource.auxiliary_size == 0U ||
                        std::memcmp(
                            next_bytes + new_resource.auxiliary_offset,
                            previous_bytes + old_resource.auxiliary_offset,
                            new_resource.auxiliary_size) == 0);
                if (!auxiliary_matches) {
                    error_offset = next_offset;
                    return false;
                }
            }
            ++previous_index;
            ++next_index;
        }
    }
    return true;
}

void write_metrics(
    const validation_result& result,
    progpu_native_scene_metrics* metrics) noexcept {
    if (metrics == nullptr ||
        metrics->struct_size < sizeof(progpu_native_scene_metrics)) {
        return;
    }
    const std::uint32_t struct_size = metrics->struct_size;
    *metrics = {};
    metrics->struct_size = struct_size;
    metrics->command_count = result.header.command_count;
    metrics->resource_count = result.header.resource_count;
    metrics->draw_count = result.draw_count;
    metrics->maximum_stack_depth = result.maximum_stack_depth;
    metrics->validation_error = result.error;
    metrics->error_offset = result.error_offset;
    metrics->scene_id = result.header.scene_id;
    metrics->generation = result.header.generation;
    metrics->snapshot_bytes = result.header.total_size;
    metrics->payload_bytes = result.payload_bytes;
}

} // namespace progpu::native::scene
