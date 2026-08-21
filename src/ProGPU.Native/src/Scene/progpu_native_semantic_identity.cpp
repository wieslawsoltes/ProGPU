#include "progpu_native_semantic_identity.hpp"

#include "progpu_native_draw_state.hpp"

#include <cstring>

namespace progpu::native::semantic {
namespace {

constexpr std::uint64_t fnv_offset = 14695981039346656037ULL;

template<typename T>
T read_record(const std::byte* bytes, std::size_t offset) noexcept {
    T value{};
    std::memcpy(&value, bytes + offset, sizeof(value));
    return value;
}

std::uint64_t finish(std::uint64_t hash) noexcept {
    return hash == 0U ? 1U : hash;
}

std::uint64_t append_identity(
    std::uint64_t hash,
    const progpu_native_scene_resource& resource) noexcept {
    hash = append_fnv1a64(hash, &resource.kind, sizeof(resource.kind));
    hash = append_fnv1a64(hash, &resource.flags, sizeof(resource.flags));
    hash = append_fnv1a64(
        hash, &resource.resource_id, sizeof(resource.resource_id));
    hash = append_fnv1a64(
        hash, &resource.generation, sizeof(resource.generation));
    hash = append_fnv1a64(
        hash, &resource.payload_size, sizeof(resource.payload_size));
    return append_fnv1a64(
        hash, &resource.auxiliary_size, sizeof(resource.auxiliary_size));
}

bool is_common_resource(std::uint32_t kind) noexcept {
    return kind == PROGPU_NATIVE_SCENE_RESOURCE_STATE ||
        kind == PROGPU_NATIVE_SCENE_RESOURCE_LAYER_MASK ||
        kind == PROGPU_NATIVE_SCENE_RESOURCE_EFFECT_CHAIN;
}

bool is_analytic_resource(std::uint32_t kind) noexcept {
    return kind == PROGPU_NATIVE_SCENE_RESOURCE_ANALYTIC_BATCH ||
        kind == PROGPU_NATIVE_SCENE_RESOURCE_GEOMETRY_BATCH ||
        kind == PROGPU_NATIVE_SCENE_RESOURCE_POINT_BATCH ||
        kind == PROGPU_NATIVE_SCENE_RESOURCE_VERTEX_MESH ||
        kind == PROGPU_NATIVE_SCENE_RESOURCE_STROKE_BATCH;
}

bool is_3d_resource(std::uint32_t kind) noexcept {
    return
        kind == PROGPU_NATIVE_SCENE_RESOURCE_LINE_3D_BATCH ||
        kind == PROGPU_NATIVE_SCENE_RESOURCE_MESH_3D_BATCH;
}

std::uint64_t append_resource_reference(
    std::uint64_t hash,
    const std::byte* bytes,
    const progpu_native_scene_header& header,
    std::uint32_t resource_index) noexcept {
    const std::uint32_t marker = resource_index ==
            PROGPU_NATIVE_SCENE_NO_INDEX
        ? 0U
        : resource_index < header.resource_count ? 1U : 2U;
    hash = append_fnv1a64(hash, &marker, sizeof(marker));
    if (marker != 1U) {
        return hash;
    }
    const auto resource = read_record<progpu_native_scene_resource>(
        bytes,
        header.resource_offset +
            static_cast<std::size_t>(resource_index) *
                header.resource_stride);
    return append_identity(hash, resource);
}

std::uint64_t append_command_record(
    std::uint64_t hash,
    const std::byte* bytes,
    const progpu_native_scene_header& header,
    const progpu_native_scene_command& command) noexcept {
    // command_id and payload_offset are stream serialization identities. They
    // move when an unrelated family inserts a command or changes arena layout.
    // Resource table ordinals likewise move when a lower stable resource id is
    // inserted. Compiled pages consume the referenced stable identities, not
    // those serialization positions. The full scene hash independently keeps
    // the exact byte-stream identity for bundle ordering and effect caches.
    auto semantic_command = command;
    semantic_command.command_id = 0U;
    semantic_command.state_index = 0U;
    semantic_command.resource_index = 0U;
    semantic_command.payload_offset = 0U;
    hash = append_fnv1a64(
        hash, &semantic_command, sizeof(semantic_command));
    hash = append_resource_reference(
        hash, bytes, header, command.state_index);
    return append_resource_reference(
        hash, bytes, header, command.resource_index);
}

std::uint64_t append_command(
    std::uint64_t hash,
    const std::byte* bytes,
    const progpu_native_scene_header& header,
    const progpu_native_scene_command& command,
    bool include_payload) noexcept {
    hash = append_command_record(hash, bytes, header, command);
    if (include_payload && command.payload_size != 0U) {
        hash = append_fnv1a64(
            hash,
            bytes + command.payload_offset,
            command.payload_size);
    }
    return hash;
}

std::uint64_t append_scope_command(
    std::uint64_t hash,
    const std::byte* bytes,
    const progpu_native_scene_header& header,
    const progpu_native_scene_command& command) noexcept {
    hash = append_command_record(hash, bytes, header, command);
    if (command.kind != PROGPU_NATIVE_SCENE_COMMAND_PUSH_LAYER ||
        command.payload_size < sizeof(progpu_native_scene_layer)) {
        if (command.payload_size != 0U) {
            hash = append_fnv1a64(
                hash,
                bytes + command.payload_offset,
                command.payload_size);
        }
        return hash;
    }
    auto layer = read_record<progpu_native_scene_layer>(
        bytes, command.payload_offset);
    const std::uint32_t mask_resource_index = layer.mask_resource_index;
    const std::uint32_t effect_resource_index = layer.effect_resource_index;
    layer.mask_resource_index = 0U;
    layer.effect_resource_index = 0U;
    hash = append_fnv1a64(hash, &layer, sizeof(layer));
    hash = append_resource_reference(
        hash, bytes, header, mask_resource_index);
    return append_resource_reference(
        hash, bytes, header, effect_resource_index);
}

std::uint64_t append_state_scope(
    std::uint64_t hash,
    const std::byte* bytes,
    const progpu_native_scene_header& header,
    std::uint32_t command_index,
    const progpu_native_scene_command& command) noexcept {
    hash = append_fnv1a64(hash, &command_index, sizeof(command_index));
    hash = append_fnv1a64(hash, &command.kind, sizeof(command.kind));
    return append_resource_reference(
        hash, bytes, header, command.state_index);
}

std::uint64_t append_brush_mapping(
    std::uint64_t hash,
    const std::byte* bytes,
    const progpu_native_scene_header& header,
    std::uint32_t command_index,
    const progpu_native_scene_command& command) noexcept {
    hash = append_fnv1a64(hash, &command_index, sizeof(command_index));
    hash = append_fnv1a64(hash, &command.kind, sizeof(command.kind));
    hash = append_fnv1a64(hash, &command.flags, sizeof(command.flags));
    hash = append_resource_reference(
        hash, bytes, header, command.state_index);
    if (command.payload_size < sizeof(progpu_native_scene_draw_brushes)) {
        return hash;
    }
    auto draw = read_record<progpu_native_scene_draw_brushes>(
        bytes, command.payload_offset);
    const std::uint32_t brush_resource_index =
        draw.brush_resource_index;
    draw.brush_resource_index = 0U;
    hash = append_fnv1a64(hash, &draw, sizeof(draw));
    hash = append_resource_reference(
        hash, bytes, header, brush_resource_index);
    const auto remaining = command.payload_size - sizeof(draw);
    if (draw.brush_count <= remaining / sizeof(std::uint32_t)) {
        const auto index_bytes = static_cast<std::size_t>(draw.brush_count) *
            sizeof(std::uint32_t);
        hash = append_fnv1a64(
            hash,
            bytes + command.payload_offset + sizeof(draw),
            index_bytes);
    }
    return hash;
}

std::uint64_t append_style_mapping(
    std::uint64_t hash,
    const std::byte* bytes,
    const progpu_native_scene_header& header,
    std::uint32_t command_index,
    const progpu_native_scene_command& command) noexcept {
    hash = append_fnv1a64(hash, &command_index, sizeof(command_index));
    hash = append_resource_reference(
        hash, bytes, header, command.state_index);
    if (command.payload_size < sizeof(progpu_native_scene_glyph_draw)) {
        return hash;
    }
    const auto draw = read_record<progpu_native_scene_glyph_draw>(
        bytes, command.payload_offset);
    hash = append_fnv1a64(
        hash, &draw.style_index, sizeof(draw.style_index));
    return append_resource_reference(
        hash, bytes, header, draw.style_resource_index);
}

std::uint64_t append_glyph_command(
    std::uint64_t hash,
    const std::byte* bytes,
    const progpu_native_scene_header& header,
    const progpu_native_scene_command& command) noexcept {
    hash = append_command_record(hash, bytes, header, command);
    if (command.payload_size == 0U) {
        return hash;
    }
    if ((command.flags & PROGPU_NATIVE_SCENE_GLYPH_STYLED) == 0U ||
        command.payload_size < sizeof(progpu_native_scene_glyph_draw)) {
        return append_fnv1a64(
            hash,
            bytes + command.payload_offset,
            command.payload_size);
    }
    auto draw = read_record<progpu_native_scene_glyph_draw>(
        bytes, command.payload_offset);
    const std::uint32_t style_resource_index =
        draw.style_resource_index;
    draw.style_resource_index = 0U;
    hash = append_fnv1a64(hash, &draw, sizeof(draw));
    hash = append_resource_reference(
        hash, bytes, header, style_resource_index);
    return append_fnv1a64(
        hash,
        bytes + command.payload_offset + sizeof(draw),
        command.payload_size - sizeof(draw));
}

bool is_scope_command(std::uint32_t kind) noexcept {
    return kind == PROGPU_NATIVE_SCENE_COMMAND_SAVE ||
        kind == PROGPU_NATIVE_SCENE_COMMAND_RESTORE ||
        kind == PROGPU_NATIVE_SCENE_COMMAND_PUSH_LAYER ||
        kind == PROGPU_NATIVE_SCENE_COMMAND_POP_LAYER;
}

bool is_analytic_command(std::uint32_t kind) noexcept {
    return kind == PROGPU_NATIVE_SCENE_COMMAND_DRAW_ANALYTIC ||
        kind == PROGPU_NATIVE_SCENE_COMMAND_DRAW_GEOMETRY ||
        kind == PROGPU_NATIVE_SCENE_COMMAND_DRAW_POINT_BATCH ||
        kind == PROGPU_NATIVE_SCENE_COMMAND_DRAW_VERTEX_MESH ||
        kind == PROGPU_NATIVE_SCENE_COMMAND_DRAW_STROKE_BATCH;
}

bool is_3d_command(std::uint32_t kind) noexcept {
    return kind == PROGPU_NATIVE_SCENE_COMMAND_DRAW_LINE_3D_BATCH ||
        kind == PROGPU_NATIVE_SCENE_COMMAND_DRAW_MESH_3D_BATCH;
}

} // namespace

semantic_content_hashes compute_content_hashes(
    const std::byte* bytes,
    const progpu_native_scene_header& header) noexcept {
    semantic_content_hashes result{};
    if (bytes == nullptr) {
        return result;
    }

    // One draw family cannot affect another family's compiled GPU page, but
    // every family consumes the shared save/restore and layer cursors. Preserve
    // complete draw identity within each family and shared scope identity
    // across them; the full scene hash independently owns bundle ordering.
    std::uint64_t brush_commands = fnv_offset;
    std::uint64_t style_commands = fnv_offset;
    std::uint64_t analytic_commands = fnv_offset;
    std::uint64_t path_commands = fnv_offset;
    std::uint64_t glyph_commands = fnv_offset;
    std::uint64_t image_commands = fnv_offset;
    std::uint64_t three_d_commands = fnv_offset;
    bool glyph_uses_text_styles = false;
    for (std::uint32_t index = 0U; index < header.command_count; ++index) {
        const std::size_t offset = header.command_offset +
            static_cast<std::size_t>(index) * header.command_stride;
        const auto command =
            read_record<progpu_native_scene_command>(bytes, offset);
        if (is_scope_command(command.kind)) {
            brush_commands = append_state_scope(
                brush_commands, bytes, header, index, command);
            style_commands = append_state_scope(
                style_commands, bytes, header, index, command);
            analytic_commands = append_scope_command(
                analytic_commands, bytes, header, command);
            path_commands = append_scope_command(
                path_commands, bytes, header, command);
            glyph_commands = append_scope_command(
                glyph_commands, bytes, header, command);
            image_commands = append_scope_command(
                image_commands, bytes, header, command);
            three_d_commands = append_scope_command(
                three_d_commands, bytes, header, command);
            continue;
        }
        if (is_analytic_command(command.kind)) {
            analytic_commands = append_command(
                analytic_commands, bytes, header, command, false);
            brush_commands = append_brush_mapping(
                brush_commands, bytes, header, index, command);
        } else if (command.kind == PROGPU_NATIVE_SCENE_COMMAND_DRAW_PATH) {
            path_commands = append_command(
                path_commands, bytes, header, command, false);
            brush_commands = append_brush_mapping(
                brush_commands, bytes, header, index, command);
        } else if (command.kind ==
            PROGPU_NATIVE_SCENE_COMMAND_DRAW_GLYPH_RUN) {
            glyph_commands = append_glyph_command(
                glyph_commands, bytes, header, command);
            if ((command.flags & PROGPU_NATIVE_SCENE_GLYPH_STYLED) != 0U) {
                style_commands = append_style_mapping(
                    style_commands, bytes, header, index, command);
            }
        } else if (command.kind == PROGPU_NATIVE_SCENE_COMMAND_DRAW_IMAGE) {
            image_commands = append_command(
                image_commands, bytes, header, command, true);
        } else if (is_3d_command(command.kind)) {
            three_d_commands = append_command(
                three_d_commands, bytes, header, command, true);
        }
        glyph_uses_text_styles = glyph_uses_text_styles ||
            (command.kind == PROGPU_NATIVE_SCENE_COMMAND_DRAW_GLYPH_RUN &&
                (command.flags & PROGPU_NATIVE_SCENE_GLYPH_STYLED) != 0U);
    }

    std::uint64_t common = fnv_offset;
    std::uint64_t brushes = fnv_offset;
    std::uint64_t styles = fnv_offset;
    std::uint64_t analytics = fnv_offset;
    std::uint64_t paths = fnv_offset;
    std::uint64_t glyphs = fnv_offset;
    std::uint64_t images = fnv_offset;
    std::uint64_t three_d = fnv_offset;
    std::uint64_t hit_tests = fnv_offset;
    for (std::uint32_t index = 0U; index < header.resource_count; ++index) {
        const std::size_t offset = header.resource_offset +
            static_cast<std::size_t>(index) * header.resource_stride;
        const auto resource =
            read_record<progpu_native_scene_resource>(bytes, offset);
        if (is_common_resource(resource.kind)) {
            common = append_identity(common, resource);
        }
        if (resource.kind == PROGPU_NATIVE_SCENE_RESOURCE_BRUSH_TABLE) {
            brushes = append_identity(brushes, resource);
        } else if (resource.kind ==
            PROGPU_NATIVE_SCENE_RESOURCE_TEXT_STYLE_TABLE) {
            styles = append_identity(styles, resource);
        } else if (is_analytic_resource(resource.kind)) {
            analytics = append_identity(analytics, resource);
        } else if (resource.kind == PROGPU_NATIVE_SCENE_RESOURCE_PATH_BATCH) {
            paths = append_identity(paths, resource);
        } else if (resource.kind == PROGPU_NATIVE_SCENE_RESOURCE_GLYPH_RUN) {
            glyphs = append_identity(glyphs, resource);
        } else if (resource.kind == PROGPU_NATIVE_SCENE_RESOURCE_IMAGE) {
            images = append_identity(images, resource);
        } else if (is_3d_resource(resource.kind)) {
            three_d = append_identity(three_d, resource);
        } else if (resource.kind ==
            PROGPU_NATIVE_SCENE_RESOURCE_HIT_TEST_INDEX) {
            hit_tests = append_identity(hit_tests, resource);
        }
    }

    const auto combine = [common](
        std::uint64_t seed,
        std::uint64_t commands,
        std::uint64_t primary,
        std::uint64_t secondary = 0U) noexcept {
        auto hash = append_fnv1a64(seed, &commands, sizeof(commands));
        hash = append_fnv1a64(hash, &common, sizeof(common));
        hash = append_fnv1a64(hash, &primary, sizeof(primary));
        if (secondary != 0U) {
            hash = append_fnv1a64(hash, &secondary, sizeof(secondary));
        }
        return finish(hash);
    };
    const auto resource_only = [](std::uint64_t seed,
                                  std::uint64_t commands,
                                  std::uint64_t resources) noexcept {
        auto hash = append_fnv1a64(seed, &commands, sizeof(commands));
        return finish(append_fnv1a64(
            hash, &resources, sizeof(resources)));
    };
    result.brush = combine(
        fnv_offset ^ 0x01U, brush_commands, brushes);
    result.text_style = combine(
        fnv_offset ^ 0x02U, style_commands, styles);
    result.analytic = combine(
        fnv_offset ^ 0x03U, analytic_commands, analytics, result.brush);
    result.path = combine(
        fnv_offset ^ 0x04U, path_commands, paths, result.brush);
    // Positioned glyphs carry their color directly. Only the optional styled
    // command form reads the text-style page; neither glyph form reads the
    // analytic brush table. Keep unrelated material updates out of the glyph
    // page identity so its retained coverage survives analytic-only changes.
    result.glyph = combine(
        fnv_offset ^ 0x05U, glyph_commands, glyphs);
    if (glyph_uses_text_styles) {
        result.glyph = finish(append_fnv1a64(
            result.glyph, &result.text_style, sizeof(result.text_style)));
    }
    result.image = combine(
        fnv_offset ^ 0x06U, image_commands, images);
    result.three_d = combine(
        fnv_offset ^ 0x07U, three_d_commands, three_d);
    result.hit_test = resource_only(
        fnv_offset ^ 0x08U, fnv_offset, hit_tests);
    return result;
}

} // namespace progpu::native::semantic
