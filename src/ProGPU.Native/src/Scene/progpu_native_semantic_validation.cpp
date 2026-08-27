#include "progpu_native_semantic_validation.hpp"

#include "progpu_native_geometry.hpp"
#include "progpu_native_path_boolean_validation.hpp"

#include <bit>
#include <cmath>
#include <cstdint>
#include <limits>

namespace progpu::native::semantic {

bool resolve_semantic_image_sampler_options(
    std::uint32_t sampling,
    std::uint32_t max_anisotropy,
    semantic_image_sampler_options& options) noexcept {
    options = {};
    const std::uint32_t canonical_anisotropy = max_anisotropy == 0U
        ? 1U
        : max_anisotropy;
    if (canonical_anisotropy > 16U ||
        (sampling != PROGPU_NATIVE_IMAGE_SAMPLING_LINEAR_MIPMAP &&
            canonical_anisotropy != 1U)) {
        return false;
    }
    options.max_anisotropy = static_cast<std::uint16_t>(
        canonical_anisotropy);
    switch (sampling) {
        case PROGPU_NATIVE_IMAGE_SAMPLING_NEAREST:
            return true;
        case PROGPU_NATIVE_IMAGE_SAMPLING_LINEAR:
        case PROGPU_NATIVE_IMAGE_SAMPLING_CUBIC:
        case PROGPU_NATIVE_IMAGE_SAMPLING_FANT:
            options.mag_linear = true;
            options.min_linear = true;
            return true;
        case PROGPU_NATIVE_IMAGE_SAMPLING_LINEAR_MIPMAP:
            options.mag_linear = true;
            options.min_linear = true;
            options.mip_linear = true;
            return true;
        case PROGPU_NATIVE_IMAGE_SAMPLING_MAG_LINEAR_MIN_LINEAR_MIP_NEAREST:
            options.mag_linear = true;
            options.min_linear = true;
            return true;
        case PROGPU_NATIVE_IMAGE_SAMPLING_MAG_LINEAR_MIN_NEAREST_MIP_LINEAR:
            options.mag_linear = true;
            options.mip_linear = true;
            return true;
        case PROGPU_NATIVE_IMAGE_SAMPLING_MAG_LINEAR_MIN_NEAREST_MIP_NEAREST:
            options.mag_linear = true;
            return true;
        case PROGPU_NATIVE_IMAGE_SAMPLING_MAG_NEAREST_MIN_LINEAR_MIP_LINEAR:
            options.min_linear = true;
            options.mip_linear = true;
            return true;
        case PROGPU_NATIVE_IMAGE_SAMPLING_MAG_NEAREST_MIN_LINEAR_MIP_NEAREST:
            options.min_linear = true;
            return true;
        case PROGPU_NATIVE_IMAGE_SAMPLING_MAG_NEAREST_MIN_NEAREST_MIP_LINEAR:
            options.mip_linear = true;
            return true;
        default:
            return false;
    }
}

namespace {

constexpr float antialias_padding_pixels = 1.5F;
constexpr std::uint32_t maximum_atlas_size = 4096U;
constexpr std::uint32_t path_padding = 4U;
constexpr std::uint32_t copy_row_alignment = 256U;

constexpr bool binary_flag(float value) noexcept {
    return value == 0.0F || value == 1.0F;
}

std::uint32_t align_up(
    std::uint32_t value,
    std::uint32_t alignment) noexcept {
    return (value + alignment - 1U) / alignment * alignment;
}

} // namespace

bool is_valid_semantic_analytic(
    const progpu_native_analytic_primitive& primitive) noexcept {
    float minimum_scale = 0.0F;
    if (!try_get_minimum_scale(primitive.transform, minimum_scale)) {
        return false;
    }
    return is_valid_analytic_primitive(
        primitive,
        antialias_padding_pixels / minimum_scale);
}

bool is_valid_semantic_segment(
    const progpu_native_path_segment& segment,
    bool allow_arc) noexcept {
    const bool is_arc = segment.kind == PROGPU_NATIVE_PATH_SEGMENT_ARC;
    const std::uint32_t maximum_kind = static_cast<std::uint32_t>(allow_arc
        ? PROGPU_NATIVE_PATH_SEGMENT_ARC
        : PROGPU_NATIVE_PATH_SEGMENT_CUBIC);
    return segment.kind <= maximum_kind &&
        is_finite(segment.p0) &&
        is_finite(segment.p1) &&
        is_finite(segment.p2) &&
        is_finite(segment.p3) &&
        ((!is_arc && segment.pad0 == 0U && segment.pad1 == 0U &&
             segment.pad2 == 0U) ||
         (allow_arc && is_arc && segment.p3.x > 0.0F &&
             segment.p3.y > 0.0F &&
             std::isfinite(std::bit_cast<float>(segment.pad0)) &&
             std::isfinite(std::bit_cast<float>(segment.pad1)) &&
             std::isfinite(std::bit_cast<float>(segment.pad2))));
}

bool is_valid_semantic_path(
    const progpu_native_scene_path_fill& path,
    std::uint64_t segment_count,
    const progpu_native_scene_path_boolean_node* boolean_nodes,
    std::uint64_t boolean_node_count,
    std::uint64_t* coverage_bytes) noexcept {
    if (path.segment_count == 0U ||
        path.segment_offset > segment_count ||
        path.segment_count > segment_count - path.segment_offset ||
        !std::isfinite(path.min_x) || !std::isfinite(path.min_y) ||
        !std::isfinite(path.max_x) || !std::isfinite(path.max_y) ||
        path.max_x <= path.min_x || path.max_y <= path.min_y ||
        !is_finite(path.color) || !is_finite(path.transform) ||
        path.fill_rule > PROGPU_NATIVE_FILL_RULE_EVEN_ODD ||
        (path.sample_grid != 4U && path.sample_grid != 8U)) {
        return false;
    }
    if (!path_boolean::validate(
            path,
            boolean_nodes,
            static_cast<std::size_t>(boolean_node_count))) {
        return false;
    }
    float maximum_scale = 0.0F;
    float minimum_scale = 0.0F;
    if (!try_get_stroke_scales(
            path.transform,
            maximum_scale,
            minimum_scale)) {
        return false;
    }
    (void)minimum_scale;
    const double raster_width =
        std::ceil(path.max_x * maximum_scale) + path_padding -
        (std::floor(path.min_x * maximum_scale) - path_padding);
    const double raster_height =
        std::ceil(path.max_y * maximum_scale) + path_padding -
        (std::floor(path.min_y * maximum_scale) - path_padding);
    const bool valid =
        std::isfinite(raster_width) && std::isfinite(raster_height) &&
        raster_width > 0.0 && raster_height > 0.0 &&
        raster_width <= maximum_atlas_size - 4U &&
        raster_height <= maximum_atlas_size - 4U;
    if (valid && coverage_bytes != nullptr) {
        const auto width = static_cast<std::uint32_t>(raster_width);
        const auto height = static_cast<std::uint32_t>(raster_height);
        *coverage_bytes = static_cast<std::uint64_t>(
            align_up(width, copy_row_alignment)) * height;
    }
    return valid;
}

bool is_valid_semantic_path(
    const progpu_native_scene_path_fill& path,
    std::uint64_t segment_count,
    std::uint64_t* coverage_bytes) noexcept {
    return is_valid_semantic_path(
        path,
        segment_count,
        nullptr,
        0U,
        coverage_bytes);
}

bool is_valid_semantic_glyph_outline(
    const progpu_native_scene_glyph_outline& outline,
    std::uint64_t segment_count,
    std::uint64_t* coverage_bytes) noexcept {
    if (outline.segment_count == 0U ||
        outline.segment_offset > segment_count ||
        outline.segment_count > segment_count - outline.segment_offset ||
        !std::isfinite(outline.min_x) || !std::isfinite(outline.min_y) ||
        !std::isfinite(outline.max_x) || !std::isfinite(outline.max_y) ||
        outline.max_x <= outline.min_x || outline.max_y <= outline.min_y ||
        !std::isfinite(outline.raster_scale) ||
        outline.raster_scale <= 0.0F ||
        !std::isfinite(outline.subpixel_x) || outline.subpixel_x < 0.0F ||
        outline.subpixel_x > 0.75F ||
        std::abs(outline.subpixel_x * 4.0F -
            std::round(outline.subpixel_x * 4.0F)) > 0.0001F) {
        return false;
    }
    const float scaled_min_x = outline.min_x * outline.raster_scale;
    const float scaled_min_y = -outline.max_y * outline.raster_scale;
    const float scaled_max_x = outline.max_x * outline.raster_scale;
    const float scaled_max_y = -outline.min_y * outline.raster_scale;
    const double width = std::ceil(scaled_max_x) + path_padding -
        (std::floor(scaled_min_x) - path_padding);
    const double height = std::ceil(scaled_max_y) + path_padding -
        (std::floor(scaled_min_y) - path_padding);
    const bool valid = std::isfinite(width) && std::isfinite(height) &&
        width > 0.0 && height > 0.0 &&
        width <= maximum_atlas_size - 4U &&
        height <= maximum_atlas_size - 4U;
    if (valid && coverage_bytes != nullptr) {
        const auto pixel_width = static_cast<std::uint32_t>(width);
        const auto pixel_height = static_cast<std::uint32_t>(height);
        *coverage_bytes = static_cast<std::uint64_t>(
            align_up(pixel_width, copy_row_alignment)) * pixel_height;
    }
    return valid;
}

bool is_valid_semantic_positioned_glyph(
    const progpu_native_positioned_glyph& glyph,
    std::uint64_t outline_count) noexcept {
    return glyph.outline_index < outline_count && glyph.reserved == 0U &&
        glyph.reserved2 == 0.0F &&
        is_finite(glyph.position) &&
        is_finite(glyph.basis_x) &&
        is_finite(glyph.basis_y) &&
        is_finite(glyph.color) &&
        std::isfinite(glyph.atlas_to_logical_scale) &&
        glyph.atlas_to_logical_scale > 0.0F &&
        std::isfinite(glyph.bold_offset) &&
        std::isfinite(glyph.italic_skew);
}

bool is_valid_semantic_color_glyph_bitmap(
    const progpu_native_scene_color_glyph_bitmap& bitmap,
    std::size_t pixel_bytes) noexcept {
    if (bitmap.width == 0U || bitmap.height == 0U ||
        bitmap.width > 16384U || bitmap.height > 16384U ||
        bitmap.width > std::numeric_limits<std::uint32_t>::max() / 4U ||
        bitmap.row_bytes < bitmap.width * 4U || bitmap.reserved0 != 0U ||
        bitmap.reserved1 != 0U || bitmap.reserved2 != 0U ||
        !std::isfinite(bitmap.bear_x) || !std::isfinite(bitmap.bear_y) ||
        !std::isfinite(bitmap.render_width) ||
        !std::isfinite(bitmap.render_height) || bitmap.render_width < 0.0F ||
        bitmap.render_height < 0.0F) {
        return false;
    }
    const std::uint64_t required =
        static_cast<std::uint64_t>(bitmap.row_bytes) *
            (bitmap.height - 1U) +
        static_cast<std::uint64_t>(bitmap.width) * 4U;
    return bitmap.pixel_offset <= pixel_bytes &&
        required <= static_cast<std::uint64_t>(pixel_bytes) -
            bitmap.pixel_offset;
}

bool is_valid_semantic_text_style(
    const progpu_native_scene_text_style& style) noexcept {
    return is_finite(style.color) &&
        style.text_rendering_mode <= PROGPU_NATIVE_SCENE_TEXT_CLEARTYPE &&
        style.reserved0 == 0U && style.reserved1 == 0U &&
        style.reserved2 == 0U;
}

bool is_valid_semantic_image(
    const progpu_native_scene_image_draw& image,
    std::uint64_t pixel_bytes) noexcept {
    const auto valid_rect = [](const progpu_native_image_rect& rect) noexcept {
        return std::isfinite(rect.x) && std::isfinite(rect.y) &&
            std::isfinite(rect.width) && std::isfinite(rect.height) &&
            rect.width > 0.0F && rect.height > 0.0F;
    };
    const std::uint64_t minimum_row_bytes =
        static_cast<std::uint64_t>(image.image_width) * 4U;
    const std::uint64_t required_pixels = image.image_height == 0U
        ? 0U
        : static_cast<std::uint64_t>(image.row_bytes) *
                (image.image_height - 1U) + minimum_row_bytes;
    const std::uint32_t known_flags =
        PROGPU_NATIVE_SCENE_IMAGE_COLOR_MATRIX |
        PROGPU_NATIVE_SCENE_IMAGE_EFFECT |
        PROGPU_NATIVE_SCENE_IMAGE_SNAP_TO_PIXELS |
        PROGPU_NATIVE_SCENE_IMAGE_SOURCE_PREMULTIPLIED |
        PROGPU_NATIVE_SCENE_IMAGE_PATCH_BATCH;
    semantic_image_sampler_options sampler{};
    return image.struct_size >= sizeof(image) &&
        (image.flags & ~known_flags) == 0U &&
        (image.flags & known_flags) != known_flags &&
        image.image_width != 0U &&
        image.image_height != 0U && image.image_width <= 16384U &&
        image.image_height <= 16384U &&
        image.row_bytes >= minimum_row_bytes &&
        required_pixels <= pixel_bytes &&
        resolve_semantic_image_sampler_options(
            image.sampling, image.max_anisotropy, sampler) &&
        !((image.flags & PROGPU_NATIVE_SCENE_IMAGE_EFFECT) != 0U &&
            image.sampling == PROGPU_NATIVE_IMAGE_SAMPLING_CUBIC) &&
        valid_rect(image.source_rect) && valid_rect(image.destination_rect) &&
        image.source_rect.x >= 0.0F && image.source_rect.y >= 0.0F &&
        image.source_rect.x + image.source_rect.width <=
            static_cast<float>(image.image_width) &&
        image.source_rect.y + image.source_rect.height <=
            static_cast<float>(image.image_height) &&
        is_finite(image.transform) &&
        std::isfinite(image.opacity) && image.opacity >= 0.0F &&
        image.opacity <= 1.0F;
}

bool is_valid_semantic_image_sampling_options(
    const progpu_native_scene_image_sampling_options& options) noexcept {
    return options.struct_size == sizeof(options) && options.flags == 0U &&
        std::isfinite(options.cubic_b) && std::isfinite(options.cubic_c) &&
        std::abs(options.cubic_b) <= 16.0F &&
        std::abs(options.cubic_c) <= 16.0F;
}

bool is_valid_semantic_image_color_matrix(
    const progpu_native_scene_image_color_matrix& matrix) noexcept {
    const auto valid_component = [](float value) noexcept {
        return std::isfinite(value) && std::abs(value) <= 1024.0F;
    };
    const auto valid_row = [&valid_component](const float (&row)[4]) noexcept {
        for (float value : row) {
            if (!valid_component(value)) {
                return false;
            }
        }
        return true;
    };
    return matrix.struct_size == sizeof(matrix) &&
        (matrix.flags &
            ~PROGPU_NATIVE_SCENE_IMAGE_COLOR_MATRIX_LUMINANCE_TO_ALPHA) ==
            0U &&
        matrix.reserved[0] == 0U && matrix.reserved[1] == 0U &&
        valid_row(matrix.red) && valid_row(matrix.green) &&
        valid_row(matrix.blue) && valid_row(matrix.alpha) &&
        valid_row(matrix.offset);
}

bool is_valid_semantic_image_effect(
    const progpu_native_scene_image_effect& effect) noexcept {
    const auto finite_row = [](const float (&row)[4]) noexcept {
        return std::isfinite(row[0]) && std::isfinite(row[1]) &&
            std::isfinite(row[2]) && std::isfinite(row[3]);
    };
    constexpr std::uint32_t known_flags =
        PROGPU_NATIVE_SCENE_IMAGE_EFFECT_UNFILTERABLE_PLANAR;
    return effect.struct_size == sizeof(effect) &&
        (effect.flags & ~known_flags) == 0U &&
        ((effect.flags &
                PROGPU_NATIVE_SCENE_IMAGE_EFFECT_UNFILTERABLE_PLANAR) == 0U ||
            effect.flags0[0] == 1.0F) &&
        effect.reserved0 == 0U && effect.reserved1 == 0U &&
        finite_row(effect.color_matrix_red) &&
        finite_row(effect.color_matrix_green) &&
        finite_row(effect.color_matrix_blue) &&
        finite_row(effect.color_matrix_alpha) &&
        finite_row(effect.color_matrix_offset) &&
        finite_row(effect.effects0) && finite_row(effect.effects1) &&
        finite_row(effect.texture0) && finite_row(effect.flags0) &&
        finite_row(effect.yuv_range) && finite_row(effect.yuv_red) &&
        finite_row(effect.yuv_green) && finite_row(effect.yuv_blue) &&
        finite_row(effect.spherical0) &&
        finite_row(effect.spherical_uv_rect) &&
        finite_row(effect.spherical_rotation0) &&
        finite_row(effect.spherical_rotation1) &&
        finite_row(effect.spherical_rotation2) &&
        effect.effects1[2] >= 0.0F && effect.effects1[2] <= 32.0F &&
        binary_flag(effect.effects1[3]) &&
        binary_flag(effect.flags0[0]) && binary_flag(effect.flags0[1]) &&
        (effect.flags0[2] == 0.0F || effect.flags0[2] == 1.0F) &&
        (effect.flags0[3] == 0.0F || effect.flags0[3] == 1.0F) &&
        effect.texture0[0] > 0.0F && effect.texture0[1] > 0.0F &&
        effect.texture0[2] == 0.0F && effect.texture0[3] == 0.0F &&
        (effect.spherical0[0] == 0.0F || effect.spherical0[0] == 1.0F);
}

bool is_valid_semantic_layer(
    const progpu_native_scene_layer& layer) noexcept {
    constexpr std::uint32_t known_flags =
        PROGPU_NATIVE_SCENE_LAYER_BOUNDS |
        PROGPU_NATIVE_SCENE_LAYER_BACKDROP |
        PROGPU_NATIVE_SCENE_LAYER_FORCE_ISOLATION |
        PROGPU_NATIVE_SCENE_LAYER_CACHE_CONTENT |
        PROGPU_NATIVE_SCENE_LAYER_CACHE_LOCAL_SPACE |
        PROGPU_NATIVE_SCENE_LAYER_CACHE_NEAREST |
        PROGPU_NATIVE_SCENE_LAYER_CACHE_FANT |
        PROGPU_NATIVE_SCENE_LAYER_COMPOSITE_STATE;
    const bool local_cache =
        (layer.flags & PROGPU_NATIVE_SCENE_LAYER_CACHE_LOCAL_SPACE) != 0U;
    const bool explicit_composite_state =
        (layer.flags & PROGPU_NATIVE_SCENE_LAYER_COMPOSITE_STATE) != 0U;
    const bool has_composite_state = local_cache || explicit_composite_state;
    const bool materialized =
        (layer.flags & (PROGPU_NATIVE_SCENE_LAYER_BACKDROP |
                PROGPU_NATIVE_SCENE_LAYER_FORCE_ISOLATION |
                PROGPU_NATIVE_SCENE_LAYER_CACHE_CONTENT)) != 0U ||
        layer.opacity != 1.0F ||
        layer.blend_mode != PROGPU_NATIVE_BLEND_SRC_OVER ||
        layer.mask_resource_index != PROGPU_NATIVE_SCENE_NO_INDEX ||
        layer.effect_resource_index != PROGPU_NATIVE_SCENE_NO_INDEX;
    const bool bounds_are_canonical =
        (layer.flags & PROGPU_NATIVE_SCENE_LAYER_BOUNDS) != 0U ||
        (layer.bounds.x == 0.0F && layer.bounds.y == 0.0F &&
            layer.bounds.width == 0.0F && layer.bounds.height == 0.0F);
    return layer.struct_size == sizeof(layer) &&
        (layer.flags & ~known_flags) == 0U &&
        std::isfinite(layer.bounds.x) &&
        std::isfinite(layer.bounds.y) &&
        std::isfinite(layer.bounds.width) &&
        std::isfinite(layer.bounds.height) &&
        layer.bounds.width >= 0.0F && layer.bounds.height >= 0.0F &&
        bounds_are_canonical && std::isfinite(layer.opacity) &&
        layer.opacity >= 0.0F && layer.opacity <= 1.0F &&
        layer.blend_mode <= PROGPU_NATIVE_BLEND_MODULATE &&
        (!explicit_composite_state || (!local_cache && materialized)) &&
        ((layer.flags & PROGPU_NATIVE_SCENE_LAYER_CACHE_CONTENT) == 0U ||
            ((layer.flags & PROGPU_NATIVE_SCENE_LAYER_BACKDROP) == 0U &&
                layer.content_revision != 0U &&
                layer.composite_revision != 0U)) &&
        (!local_cache ||
            ((layer.flags & (PROGPU_NATIVE_SCENE_LAYER_CACHE_CONTENT |
                    PROGPU_NATIVE_SCENE_LAYER_BOUNDS)) ==
                (PROGPU_NATIVE_SCENE_LAYER_CACHE_CONTENT |
                    PROGPU_NATIVE_SCENE_LAYER_BOUNDS) &&
                layer.bounds.x == 0.0F && layer.bounds.y == 0.0F &&
                layer.bounds.width > 0.0F && layer.bounds.height > 0.0F &&
                layer.blend_mode == PROGPU_NATIVE_BLEND_SRC_OVER &&
                layer.effect_resource_index ==
                    PROGPU_NATIVE_SCENE_NO_INDEX)) &&
        (((layer.flags & (PROGPU_NATIVE_SCENE_LAYER_CACHE_NEAREST |
                PROGPU_NATIVE_SCENE_LAYER_CACHE_FANT)) == 0U) ||
            local_cache) &&
        ((layer.flags & (PROGPU_NATIVE_SCENE_LAYER_CACHE_NEAREST |
                PROGPU_NATIVE_SCENE_LAYER_CACHE_FANT)) !=
            (PROGPU_NATIVE_SCENE_LAYER_CACHE_NEAREST |
                PROGPU_NATIVE_SCENE_LAYER_CACHE_FANT)) &&
        (has_composite_state || layer.reserved0 == 0U) &&
        layer.reserved1 == 0U;
}

bool is_valid_semantic_effect(
    const progpu_native_group_effect& effect) noexcept {
    if (effect.struct_size != sizeof(effect) ||
        (effect.kind != PROGPU_NATIVE_GROUP_EFFECT_GAUSSIAN_BLUR &&
            effect.kind != PROGPU_NATIVE_GROUP_EFFECT_DROP_SHADOW &&
            effect.kind != PROGPU_NATIVE_GROUP_EFFECT_BOX_BLUR) ||
        effect.flags != 0U || effect.revision == 0U ||
        effect.reserved != 0U || effect.reserved2 != 0U ||
        !std::isfinite(effect.sigma_x) ||
        !std::isfinite(effect.sigma_y) || effect.sigma_x < 0.0F ||
        effect.sigma_y < 0.0F || !std::isfinite(effect.offset_x) ||
        !std::isfinite(effect.offset_y) ||
        !std::isfinite(effect.color_r) ||
        !std::isfinite(effect.color_g) ||
        !std::isfinite(effect.color_b) ||
        !std::isfinite(effect.color_a)) {
        return false;
    }
    if (effect.kind == PROGPU_NATIVE_GROUP_EFFECT_GAUSSIAN_BLUR ||
        effect.kind == PROGPU_NATIVE_GROUP_EFFECT_BOX_BLUR) {
        return effect.sigma_x > 0.01F && effect.sigma_y > 0.01F &&
            effect.offset_x == 0.0F && effect.offset_y == 0.0F &&
            effect.color_r == 0.0F && effect.color_g == 0.0F &&
            effect.color_b == 0.0F && effect.color_a == 0.0F;
    }
    return effect.color_r >= 0.0F && effect.color_r <= 1.0F &&
        effect.color_g >= 0.0F && effect.color_g <= 1.0F &&
        effect.color_b >= 0.0F && effect.color_b <= 1.0F &&
        effect.color_a >= 0.0F && effect.color_a <= 1.0F;
}

namespace {

bool finite_matrix(const progpu_native_matrix_4x4& matrix) noexcept {
    const float* values = &matrix.m11;
    for (std::size_t index = 0U; index < 16U; ++index) {
        if (!std::isfinite(values[index])) {
            return false;
        }
    }
    return true;
}

bool finite_point_3d(const progpu_native_point_3d& point) noexcept {
    return std::isfinite(point.x) && std::isfinite(point.y) &&
        std::isfinite(point.z) && point.reserved == 0.0F;
}

bool finite_float_4(const progpu_native_float_4& value) noexcept {
    return std::isfinite(value.x) && std::isfinite(value.y) &&
        std::isfinite(value.z) && std::isfinite(value.w);
}

} // namespace

bool is_valid_semantic_camera_3d(
    const progpu_native_scene_camera_3d& camera) noexcept {
    return camera.struct_size == sizeof(camera) && camera.flags == 0U &&
        camera.reserved0 == 0U && camera.reserved1 == 0U &&
        finite_matrix(camera.projection) && finite_matrix(camera.view) &&
        finite_point_3d(camera.camera_position);
}

bool is_valid_semantic_line_3d(
    const progpu_native_scene_line_3d& line) noexcept {
    return line.struct_size == sizeof(line) && line.flags == 0U &&
        line.reserved0 == 0U && line.reserved1 == 0U &&
        line.reserved2 == 0U && line.reserved3 == 0U &&
        finite_point_3d(line.start) && finite_point_3d(line.end) &&
        is_finite(line.color) && std::isfinite(line.thickness) &&
        line.thickness > 0.0F && line.thickness <= 16384.0F &&
        std::isfinite(line.opacity) && line.opacity >= 0.0F &&
        line.opacity <= 1.0F && finite_matrix(line.transform);
}

bool is_valid_semantic_mesh_3d_vertex(
    const progpu_native_scene_mesh_3d_vertex& vertex) noexcept {
    return finite_point_3d(vertex.position) &&
        finite_point_3d(vertex.normal) &&
        is_finite(vertex.texture_coordinate) &&
        vertex.reserved0 == 0U && vertex.reserved1 == 0U;
}

bool is_valid_semantic_mesh_3d(
    const progpu_native_scene_mesh_3d& mesh,
    std::size_t vertex_count,
    std::size_t index_count) noexcept {
    const std::size_t mesh_vertex_offset = mesh.vertex_offset;
    const std::size_t mesh_index_offset = mesh.index_offset;
    constexpr std::uint32_t known_flags =
        PROGPU_NATIVE_MESH_3D_FRONT_FACE |
        PROGPU_NATIVE_MESH_3D_BACK_FACE;
    const auto face_flags = mesh.flags & known_flags;
    return mesh.struct_size == sizeof(mesh) &&
        (mesh.flags & ~known_flags) == 0U && face_flags != known_flags &&
        mesh.topology <= PROGPU_NATIVE_MESH_3D_TRIANGLE_STRIP &&
        mesh.render_mode <= PROGPU_NATIVE_MESH_3D_SOLID_WIREFRAME &&
        mesh.vertex_count >= 3U && mesh.index_count >= 3U &&
        mesh_vertex_offset <= vertex_count &&
        mesh.vertex_count <= vertex_count - mesh_vertex_offset &&
        mesh_index_offset <= index_count &&
        mesh.index_count <= index_count - mesh_index_offset &&
        finite_matrix(mesh.model_transform) &&
        finite_matrix(mesh.normal_transform) && is_finite(mesh.color) &&
        finite_float_4(mesh.light_direction) &&
        mesh.light_direction.w >= 0.0F &&
        finite_float_4(mesh.ambient_color) &&
        mesh.ambient_color.w >= 0.0F &&
        finite_float_4(mesh.specular_color) &&
        mesh.specular_color.w > 0.0F &&
        finite_float_4(mesh.material_ambient) &&
        std::isfinite(mesh.opacity) && mesh.opacity >= 0.0F &&
        mesh.opacity <= 1.0F && mesh.shading_mode <= 6U &&
        mesh.reserved0 == 0U && mesh.reserved1 == 0U;
}

} // namespace progpu::native::semantic
