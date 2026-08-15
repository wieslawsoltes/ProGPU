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
        kind == PROGPU_NATIVE_SCENE_RESOURCE_STROKE_BATCH ||
        kind == PROGPU_NATIVE_SCENE_RESOURCE_LINE_3D_BATCH ||
        kind == PROGPU_NATIVE_SCENE_RESOURCE_MESH_3D_BATCH;
}

} // namespace

semantic_content_hashes compute_content_hashes(
    const std::byte* bytes,
    const progpu_native_scene_header& header) noexcept {
    semantic_content_hashes result{};
    if (bytes == nullptr) {
        return result;
    }

    // Command placement, state selection, brush/style maps, and layer scopes
    // are shared inputs to compiled pages. Hash them once without the header's
    // scene generation, then combine them with typed resource identities.
    std::uint64_t commands = fnv_offset;
    for (std::uint32_t index = 0U; index < header.command_count; ++index) {
        const std::size_t offset = header.command_offset +
            static_cast<std::size_t>(index) * header.command_stride;
        const auto command =
            read_record<progpu_native_scene_command>(bytes, offset);
        commands = append_fnv1a64(commands, &command, sizeof(command));
        if (command.payload_size != 0U) {
            commands = append_fnv1a64(
                commands,
                bytes + command.payload_offset,
                command.payload_size);
        }
    }

    std::uint64_t common = fnv_offset;
    std::uint64_t brushes = fnv_offset;
    std::uint64_t styles = fnv_offset;
    std::uint64_t analytics = fnv_offset;
    std::uint64_t paths = fnv_offset;
    std::uint64_t glyphs = fnv_offset;
    std::uint64_t images = fnv_offset;
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
        }
    }

    const auto combine = [commands, common](
        std::uint64_t seed,
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
    result.brush = combine(fnv_offset ^ 0x01U, brushes);
    result.text_style = combine(fnv_offset ^ 0x02U, styles);
    result.analytic = combine(fnv_offset ^ 0x03U, analytics, brushes);
    result.path = combine(fnv_offset ^ 0x04U, paths, brushes);
    result.glyph = combine(fnv_offset ^ 0x05U, glyphs, brushes);
    result.glyph = finish(append_fnv1a64(
        result.glyph, &styles, sizeof(styles)));
    result.image = combine(fnv_offset ^ 0x06U, images);
    return result;
}

} // namespace progpu::native::semantic
