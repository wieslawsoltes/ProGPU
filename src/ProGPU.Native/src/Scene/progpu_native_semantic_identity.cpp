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

std::uint64_t append_command(
    std::uint64_t hash,
    const std::byte* bytes,
    const progpu_native_scene_command& command) noexcept {
    // payload_offset is an arena serialization detail. It moves whenever the
    // resource/command tables change size and is not part of draw semantics.
    auto semantic_command = command;
    semantic_command.payload_offset = 0U;
    hash = append_fnv1a64(
        hash, &semantic_command, sizeof(semantic_command));
    if (command.payload_size != 0U) {
        hash = append_fnv1a64(
            hash,
            bytes + command.payload_offset,
            command.payload_size);
    }
    return hash;
}

std::uint64_t append_family_command(
    std::uint64_t hash,
    const std::byte* bytes,
    const progpu_native_scene_command& command) noexcept {
    return append_command(hash, bytes, command);
}

std::uint64_t append_brush_mapping(
    std::uint64_t hash,
    const std::byte* bytes,
    const progpu_native_scene_command& command) noexcept {
    hash = append_fnv1a64(hash, &command.kind, sizeof(command.kind));
    hash = append_fnv1a64(hash, &command.flags, sizeof(command.flags));
    hash = append_fnv1a64(
        hash, &command.state_index, sizeof(command.state_index));
    if (command.payload_size < sizeof(progpu_native_scene_draw_brushes)) {
        return hash;
    }
    const auto draw = read_record<progpu_native_scene_draw_brushes>(
        bytes, command.payload_offset);
    hash = append_fnv1a64(hash, &draw, sizeof(draw));
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
            brush_commands = append_family_command(
                brush_commands, bytes, command);
            style_commands = append_family_command(
                style_commands, bytes, command);
            analytic_commands = append_family_command(
                analytic_commands, bytes, command);
            path_commands = append_family_command(
                path_commands, bytes, command);
            glyph_commands = append_family_command(
                glyph_commands, bytes, command);
            image_commands = append_family_command(
                image_commands, bytes, command);
            three_d_commands = append_family_command(
                three_d_commands, bytes, command);
            continue;
        }
        if (is_analytic_command(command.kind)) {
            analytic_commands = append_command(
                analytic_commands, bytes, command);
            brush_commands = append_brush_mapping(
                brush_commands, bytes, command);
        } else if (command.kind == PROGPU_NATIVE_SCENE_COMMAND_DRAW_PATH) {
            path_commands = append_command(path_commands, bytes, command);
            brush_commands = append_brush_mapping(
                brush_commands, bytes, command);
        } else if (command.kind ==
            PROGPU_NATIVE_SCENE_COMMAND_DRAW_GLYPH_RUN) {
            glyph_commands = append_command(glyph_commands, bytes, command);
            if ((command.flags & PROGPU_NATIVE_SCENE_GLYPH_STYLED) != 0U) {
                style_commands = append_family_command(
                    style_commands, bytes, command);
            }
        } else if (command.kind == PROGPU_NATIVE_SCENE_COMMAND_DRAW_IMAGE) {
            image_commands = append_command(image_commands, bytes, command);
        } else if (is_3d_command(command.kind)) {
            three_d_commands = append_command(
                three_d_commands, bytes, command);
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
