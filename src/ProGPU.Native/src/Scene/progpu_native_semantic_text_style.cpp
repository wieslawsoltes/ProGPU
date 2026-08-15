#include "progpu_native_semantic_text_style.hpp"

#include "progpu_native_semantic_state.hpp"
#include "progpu_native_semantic_validation.hpp"

#include <bit>
#include <cmath>
#include <cstring>
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

struct text_style_key final {
    std::uint32_t resource_index = 0U;
    std::uint32_t style_index = 0U;
    std::uint32_t opacity_bits = 0U;

    bool operator==(const text_style_key&) const = default;
};

struct text_style_hash final {
    std::size_t operator()(const text_style_key& key) const noexcept {
        std::uint64_t value = key.resource_index;
        value = (value * 0x9e3779b185ebca87ULL) ^ key.style_index;
        value = (value * 0x9e3779b185ebca87ULL) ^ key.opacity_bits;
        return static_cast<std::size_t>(value ^ (value >> 32U));
    }
};

} // namespace

static_assert(sizeof(progpu_native_scene_text_style) == 32U);
static_assert(offsetof(progpu_native_scene_text_style, color) == 0U);
static_assert(offsetof(
    progpu_native_scene_text_style, text_rendering_mode) == 16U);
static_assert(sizeof(progpu_native_scene_glyph_draw) == 24U);

bool validate_text_style_table(
    const std::byte* bytes,
    const progpu_native_scene_resource& resource,
    std::uint32_t& error_offset) noexcept {
    error_offset = resource.payload_offset;
    if (bytes == nullptr || resource.payload_size == 0U ||
        resource.auxiliary_size != 0U ||
        resource.payload_size % sizeof(progpu_native_scene_text_style) != 0U) {
        return false;
    }
    const std::uint32_t count = resource.payload_size /
        sizeof(progpu_native_scene_text_style);
    if (count == 0U || count > PROGPU_NATIVE_SCENE_MAX_TEXT_STYLES) {
        return false;
    }
    for (std::uint32_t index = 0U; index < count; ++index) {
        const std::uint32_t offset = resource.payload_offset +
            index * sizeof(progpu_native_scene_text_style);
        if (!is_valid_semantic_text_style(
                read_record<progpu_native_scene_text_style>(
                bytes,
                offset))) {
            error_offset = offset;
            return false;
        }
    }
    return true;
}

bool validate_styled_glyph_draw(
    const std::byte* bytes,
    const progpu_native_scene_header& header,
    const progpu_native_scene_command& command,
    std::uint32_t& error_offset) noexcept {
    error_offset = command.payload_offset;
    if (bytes == nullptr ||
        (command.flags & PROGPU_NATIVE_SCENE_GLYPH_STYLED) == 0U ||
        command.payload_size < sizeof(progpu_native_scene_glyph_draw)) {
        return false;
    }
    const auto draw = read_record<progpu_native_scene_glyph_draw>(
        bytes,
        command.payload_offset);
    const std::uint64_t expected_size = sizeof(draw) +
        static_cast<std::uint64_t>(draw.glyph_count) *
            sizeof(progpu_native_positioned_glyph);
    if (draw.struct_size != sizeof(draw) || draw.glyph_count == 0U ||
        draw.glyph_count > (1U << 24U) || draw.reserved0 != 0U ||
        draw.reserved1 != 0U || expected_size != command.payload_size ||
        draw.style_resource_index >= header.resource_count) {
        return false;
    }
    const auto resource = read_resource(
        bytes,
        header,
        draw.style_resource_index);
    if (resource.kind != PROGPU_NATIVE_SCENE_RESOURCE_TEXT_STYLE_TABLE ||
        resource.payload_size % sizeof(progpu_native_scene_text_style) != 0U ||
        draw.style_index >= resource.payload_size /
            sizeof(progpu_native_scene_text_style)) {
        return false;
    }
    return true;
}

bool try_get_glyph_payload(
    const std::byte* bytes,
    const progpu_native_scene_command& command,
    std::uint32_t& payload_offset,
    std::uint32_t& glyph_count) noexcept {
    payload_offset = command.payload_offset;
    glyph_count = 0U;
    if ((command.flags & PROGPU_NATIVE_SCENE_GLYPH_STYLED) == 0U) {
        if (command.payload_size == 0U ||
            command.payload_size % sizeof(progpu_native_positioned_glyph) !=
                0U) {
            return false;
        }
        glyph_count = command.payload_size /
            sizeof(progpu_native_positioned_glyph);
        return true;
    }
    if (bytes == nullptr ||
        command.payload_size < sizeof(progpu_native_scene_glyph_draw)) {
        return false;
    }
    const auto draw = read_record<progpu_native_scene_glyph_draw>(
        bytes,
        command.payload_offset);
    const std::uint64_t expected_size = sizeof(draw) +
        static_cast<std::uint64_t>(draw.glyph_count) *
            sizeof(progpu_native_positioned_glyph);
    if (draw.struct_size != sizeof(draw) || draw.glyph_count == 0U ||
        expected_size != command.payload_size) {
        return false;
    }
    payload_offset += sizeof(draw);
    glyph_count = draw.glyph_count;
    return true;
}

bool compile_text_style_page(
    const std::byte* bytes,
    const progpu_native_scene_header& header,
    std::uint64_t scene_hash,
    semantic_text_style_page& page) noexcept {
    try {
        semantic_text_style_page compiled{};
        compiled.styles.reserve(32U);
        compiled.command_style_indices.assign(
            header.command_count,
            PROGPU_NATIVE_SCENE_NO_INDEX);
        compiled.styles.push_back({});
        std::unordered_map<text_style_key, std::uint32_t, text_style_hash>
            variants;
        variants.reserve(32U);
        semantic_state_cursor state_cursor(bytes, header);
        for (std::uint32_t command_index = 0U;
             command_index < header.command_count;
             ++command_index) {
            const auto command = read_command(bytes, header, command_index);
            const auto state = state_cursor.advance(command);
            if (command.kind != PROGPU_NATIVE_SCENE_COMMAND_DRAW_GLYPH_RUN ||
                (command.flags & PROGPU_NATIVE_SCENE_GLYPH_STYLED) == 0U) {
                continue;
            }
            const auto draw = read_record<progpu_native_scene_glyph_draw>(
                bytes,
                command.payload_offset);
            const text_style_key key{
                draw.style_resource_index,
                draw.style_index,
                std::bit_cast<std::uint32_t>(state.opacity)};
            const auto found = variants.find(key);
            if (found != variants.end()) {
                compiled.command_style_indices[command_index] = found->second;
                continue;
            }
            if (compiled.styles.size() >
                    PROGPU_NATIVE_SCENE_MAX_TEXT_STYLES ||
                compiled.styles.size() >= (1U << 24U)) {
                return false;
            }
            const auto resource = read_resource(
                bytes,
                header,
                draw.style_resource_index);
            auto style = read_record<progpu_native_scene_text_style>(
                bytes,
                resource.payload_offset +
                    static_cast<std::size_t>(draw.style_index) *
                        sizeof(progpu_native_scene_text_style));
            style.color.a *= state.opacity;
            const auto packed_index = static_cast<std::uint32_t>(
                compiled.styles.size());
            compiled.styles.push_back(style);
            variants.emplace(key, packed_index);
            compiled.command_style_indices[command_index] = packed_index;
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

bool try_get_command_text_style_index(
    const semantic_text_style_page& page,
    std::uint32_t command_index,
    std::uint32_t& style_index) noexcept {
    style_index = PROGPU_NATIVE_SCENE_NO_INDEX;
    if (command_index >= page.command_style_indices.size()) {
        return false;
    }
    style_index = page.command_style_indices[command_index];
    return style_index == PROGPU_NATIVE_SCENE_NO_INDEX ||
        style_index < page.styles.size();
}

} // namespace progpu::native::semantic
