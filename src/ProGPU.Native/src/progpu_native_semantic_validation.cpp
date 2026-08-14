#include "progpu_native_semantic_validation.hpp"

#include "progpu_native_geometry.hpp"

#include <bit>
#include <cmath>
#include <cstdint>

namespace progpu::native::semantic {

namespace {

constexpr float antialias_padding_pixels = 1.5F;
constexpr std::uint32_t maximum_atlas_size = 4096U;
constexpr std::uint32_t path_padding = 4U;
constexpr std::uint32_t copy_row_alignment = 256U;

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
    return image.struct_size >= sizeof(image) &&
        (image.flags & ~PROGPU_NATIVE_SCENE_IMAGE_COLOR_MATRIX) == 0U &&
        image.reserved == 0U && image.image_width != 0U &&
        image.image_height != 0U && image.image_width <= 16384U &&
        image.image_height <= 16384U &&
        image.row_bytes >= minimum_row_bytes &&
        required_pixels <= pixel_bytes &&
        image.sampling <= PROGPU_NATIVE_IMAGE_SAMPLING_CUBIC &&
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
    return matrix.struct_size == sizeof(matrix) && matrix.flags == 0U &&
        matrix.reserved[0] == 0U && matrix.reserved[1] == 0U &&
        valid_row(matrix.red) && valid_row(matrix.green) &&
        valid_row(matrix.blue) && valid_row(matrix.alpha) &&
        valid_row(matrix.offset);
}

bool is_valid_semantic_layer(
    const progpu_native_scene_layer& layer) noexcept {
    constexpr std::uint32_t known_flags =
        PROGPU_NATIVE_SCENE_LAYER_BOUNDS |
        PROGPU_NATIVE_SCENE_LAYER_BACKDROP |
        PROGPU_NATIVE_SCENE_LAYER_FORCE_ISOLATION;
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
        layer.reserved0 == 0U && layer.reserved1 == 0U;
}

bool is_valid_semantic_effect(
    const progpu_native_group_effect& effect) noexcept {
    if (effect.struct_size != sizeof(effect) ||
        (effect.kind != PROGPU_NATIVE_GROUP_EFFECT_GAUSSIAN_BLUR &&
            effect.kind != PROGPU_NATIVE_GROUP_EFFECT_DROP_SHADOW) ||
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
    if (effect.kind == PROGPU_NATIVE_GROUP_EFFECT_GAUSSIAN_BLUR) {
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

} // namespace progpu::native::semantic
