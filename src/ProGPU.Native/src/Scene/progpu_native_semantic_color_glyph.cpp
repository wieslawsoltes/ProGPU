#include "progpu_native_semantic_color_glyph.hpp"
#include "progpu_native_semantic_validation.hpp"

#include <cstring>

namespace progpu::native::semantic {
namespace {

template<typename T>
T read_record(const std::byte* bytes, std::size_t offset) noexcept {
    T value{};
    std::memcpy(&value, bytes + offset, sizeof(value));
    return value;
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
        if (!is_valid_semantic_color_glyph_bitmap(
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
