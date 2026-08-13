#include "progpu_native_semantic_color_glyph.hpp"

#include <cmath>
#include <cstring>
#include <limits>

namespace progpu::native::semantic {
namespace {

template<typename T>
T read_record(const std::byte* bytes, std::size_t offset) noexcept {
    T value{};
    std::memcpy(&value, bytes + offset, sizeof(value));
    return value;
}

bool valid_bitmap(
    const progpu_native_scene_color_glyph_bitmap& bitmap,
    std::uint32_t pixel_bytes) noexcept {
    if (bitmap.width == 0U || bitmap.height == 0U ||
        bitmap.width > 16384U || bitmap.height > 16384U ||
        bitmap.width > std::numeric_limits<std::uint32_t>::max() / 4U ||
        bitmap.row_bytes < bitmap.width * 4U ||
        bitmap.reserved0 != 0U || bitmap.reserved1 != 0U ||
        bitmap.reserved2 != 0U || !std::isfinite(bitmap.bear_x) ||
        !std::isfinite(bitmap.bear_y) ||
        !std::isfinite(bitmap.render_width) ||
        !std::isfinite(bitmap.render_height) ||
        bitmap.render_width < 0.0F || bitmap.render_height < 0.0F) {
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

} // namespace

static_assert(sizeof(progpu_native_scene_color_glyph_bitmap) == 48U);
static_assert(offsetof(
    progpu_native_scene_color_glyph_bitmap,
    pixel_offset) == 0U);
static_assert(offsetof(
    progpu_native_scene_color_glyph_bitmap,
    bear_x) == 24U);

bool is_color_glyph_resource(
    const progpu_native_scene_resource& resource) noexcept {
    return resource.kind == PROGPU_NATIVE_SCENE_RESOURCE_GLYPH_RUN &&
        (resource.flags & PROGPU_NATIVE_SCENE_COLOR_GLYPH_BITMAPS) != 0U;
}

bool validate_color_glyph_resource(
    const std::byte* bytes,
    const progpu_native_scene_resource& resource,
    std::uint32_t& error_offset) noexcept {
    error_offset = resource.payload_offset;
    if (bytes == nullptr || !is_color_glyph_resource(resource) ||
        resource.payload_size == 0U || resource.auxiliary_size == 0U ||
        resource.payload_size %
            sizeof(progpu_native_scene_color_glyph_bitmap) != 0U) {
        return false;
    }
    const std::uint32_t count = resource.payload_size /
        sizeof(progpu_native_scene_color_glyph_bitmap);
    if (count == 0U || count > (1U << 20U)) {
        return false;
    }
    for (std::uint32_t index = 0U; index < count; ++index) {
        const std::uint32_t offset = resource.payload_offset +
            index * sizeof(progpu_native_scene_color_glyph_bitmap);
        if (!valid_bitmap(
                read_record<progpu_native_scene_color_glyph_bitmap>(
                    bytes,
                    offset),
                resource.auxiliary_size)) {
            error_offset = offset;
            return false;
        }
    }
    return true;
}

} // namespace progpu::native::semantic
