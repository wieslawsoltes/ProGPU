#include "progpu_native_scene_builder_internal.hpp"

#include "progpu_native_semantic_validation.hpp"

#include <algorithm>
#include <cstring>
#include <limits>
#include <new>
#include <utility>

namespace progpu::native {
using scene_builder_detail::copy_bytes;
using scene_builder_detail::finite_rect;

bool semantic_scene_builder::add_text_style(
    const progpu_native_scene_text_style& source,
    std::uint32_t& style_index) noexcept {
    style_index = PROGPU_NATIVE_SCENE_NO_INDEX;
    progpu_native_scene_text_style style = source;
    style.reserved0 = 0U;
    style.reserved1 = 0U;
    style.reserved2 = 0U;
    if (!semantic::is_valid_semantic_text_style(style)) {
        return implementation_->fail(scene_build_error::invalid_argument);
    }
    for (std::uint32_t index = 0U;
         index < implementation_->text_styles.size();
         ++index) {
        if (std::memcmp(
                &implementation_->text_styles[index],
                &style,
                sizeof(style)) == 0) {
            style_index = index;
            implementation_->error = scene_build_error::none;
            return true;
        }
    }
    if (implementation_->text_styles.size() >=
        PROGPU_NATIVE_SCENE_MAX_TEXT_STYLES) {
        return implementation_->fail(scene_build_error::capacity_exceeded);
    }
    try {
        implementation_->text_styles.reserve(
            implementation_->text_styles.size() + 1U);
        if (implementation_->text_style_resource_index ==
            PROGPU_NATIVE_SCENE_NO_INDEX) {
            if (implementation_->resources.size() >=
                PROGPU_NATIVE_SCENE_MAX_RESOURCES) {
                return implementation_->fail(
                    scene_build_error::capacity_exceeded);
            }
            implementation_->resources.reserve(
                implementation_->resources.size() + 1U);
            implementation::resource_entry resource{};
            resource.record.struct_size = sizeof(resource.record);
            resource.record.kind =
                PROGPU_NATIVE_SCENE_RESOURCE_TEXT_STYLE_TABLE;
            resource.record.flags = PROGPU_NATIVE_SCENE_RECORD_REQUIRED;
            resource.record.resource_id = implementation_->resources.size() + 1U;
            resource.record.generation = implementation_->generation;
            resource.text_style_table = true;
            implementation_->text_style_resource_index =
                static_cast<std::uint32_t>(implementation_->resources.size());
            implementation_->resources.push_back(std::move(resource));
        }
        style_index = static_cast<std::uint32_t>(
            implementation_->text_styles.size());
        implementation_->text_styles.push_back(style);
        implementation_->error = scene_build_error::none;
        return true;
    } catch (const std::bad_alloc&) {
        return implementation_->fail(scene_build_error::out_of_memory);
    } catch (...) {
        return implementation_->fail(scene_build_error::invalid_state);
    }
}

bool semantic_scene_builder::add_glyph_outlines(
    std::span<const progpu_native_scene_glyph_outline> outlines,
    std::span<const progpu_native_path_segment> segments,
    std::uint32_t& resource_index) noexcept {
    resource_index = PROGPU_NATIVE_SCENE_NO_INDEX;
    if (outlines.empty() || segments.empty() ||
        implementation_->resources.size() >=
            PROGPU_NATIVE_SCENE_MAX_RESOURCES) {
        return implementation_->fail(scene_build_error::invalid_argument);
    }
    // Outlines may deliberately share immutable segment ranges when the same
    // glyph geometry is rasterized at multiple physical sizes or subpixel
    // phases. Keep the packed auxiliary stream gap-free while permitting
    // overlapping and repeated references into the already validated data.
    std::uint64_t covered_segment_end = 0U;
    for (const auto& outline : outlines) {
        if (outline.segment_offset > covered_segment_end ||
            !semantic::is_valid_semantic_glyph_outline(
                outline,
                segments.size())) {
            return implementation_->fail(scene_build_error::invalid_argument);
        }
        covered_segment_end = std::max(
            covered_segment_end,
            outline.segment_offset + outline.segment_count);
    }
    if (covered_segment_end != segments.size()) {
        return implementation_->fail(scene_build_error::invalid_argument);
    }
    for (const auto& segment : segments) {
        if (!semantic::is_valid_semantic_segment(segment, false)) {
            return implementation_->fail(scene_build_error::invalid_argument);
        }
    }
    try {
        implementation_->resources.reserve(
            implementation_->resources.size() + 1U);
        implementation::resource_entry resource{};
        resource.record.struct_size = sizeof(resource.record);
        resource.record.kind = PROGPU_NATIVE_SCENE_RESOURCE_GLYPH_RUN;
        resource.record.flags = PROGPU_NATIVE_SCENE_RECORD_REQUIRED;
        resource.record.resource_id = implementation_->resources.size() + 1U;
        resource.record.generation = implementation_->generation;
        resource.payload = copy_bytes(outlines);
        resource.auxiliary = copy_bytes(segments);
        resource.glyph_outline_count = static_cast<std::uint32_t>(
            outlines.size());
        resource_index = static_cast<std::uint32_t>(
            implementation_->resources.size());
        implementation_->resources.push_back(std::move(resource));
        implementation_->error = scene_build_error::none;
        return true;
    } catch (const std::bad_alloc&) {
        return implementation_->fail(scene_build_error::out_of_memory);
    } catch (...) {
        return implementation_->fail(scene_build_error::invalid_state);
    }
}

bool semantic_scene_builder::add_color_glyph_bitmaps(
    std::span<const progpu_native_scene_color_glyph_bitmap> bitmaps,
    std::span<const std::byte> rgba_pixels,
    std::uint32_t& resource_index) noexcept {
    resource_index = PROGPU_NATIVE_SCENE_NO_INDEX;
    if (bitmaps.empty() || rgba_pixels.empty() ||
        implementation_->resources.size() >=
            PROGPU_NATIVE_SCENE_MAX_RESOURCES ||
        bitmaps.size() > (1U << 20U)) {
        return implementation_->fail(scene_build_error::invalid_argument);
    }
    for (const auto& bitmap : bitmaps) {
        if (!semantic::is_valid_semantic_color_glyph_bitmap(
                bitmap,
                rgba_pixels.size())) {
            return implementation_->fail(scene_build_error::invalid_argument);
        }
    }
    try {
        implementation_->resources.reserve(
            implementation_->resources.size() + 1U);
        implementation::resource_entry resource{};
        resource.record.struct_size = sizeof(resource.record);
        resource.record.kind = PROGPU_NATIVE_SCENE_RESOURCE_GLYPH_RUN;
        resource.record.flags = PROGPU_NATIVE_SCENE_RECORD_REQUIRED |
            PROGPU_NATIVE_SCENE_COLOR_GLYPH_BITMAPS;
        resource.record.resource_id = implementation_->resources.size() + 1U;
        resource.record.generation = implementation_->generation;
        resource.payload = copy_bytes(bitmaps);
        resource.auxiliary.assign(rgba_pixels.begin(), rgba_pixels.end());
        resource.glyph_outline_count = static_cast<std::uint32_t>(
            bitmaps.size());
        resource_index = static_cast<std::uint32_t>(
            implementation_->resources.size());
        implementation_->resources.push_back(std::move(resource));
        implementation_->error = scene_build_error::none;
        return true;
    } catch (const std::bad_alloc&) {
        return implementation_->fail(scene_build_error::out_of_memory);
    } catch (...) {
        return implementation_->fail(scene_build_error::invalid_state);
    }
}

bool semantic_scene_builder::draw_glyph_run(
    std::uint32_t glyph_resource_index,
    std::span<const progpu_native_positioned_glyph> glyphs,
    progpu_native_image_rect bounds,
    std::uint32_t state_resource_index,
    std::uint32_t text_style_index) noexcept {
    if (glyphs.empty() || !finite_rect(bounds) ||
        glyph_resource_index >= implementation_->resources.size() ||
        !implementation_->valid_state_index(state_resource_index) ||
        implementation_->commands.size() >=
            PROGPU_NATIVE_SCENE_MAX_COMMANDS) {
        return implementation_->fail(scene_build_error::invalid_argument);
    }
    const auto& resource = implementation_->resources[glyph_resource_index];
    const bool styled = text_style_index != PROGPU_NATIVE_SCENE_NO_INDEX;
    if (resource.record.kind != PROGPU_NATIVE_SCENE_RESOURCE_GLYPH_RUN ||
        resource.glyph_outline_count == 0U ||
        (styled && (implementation_->text_style_resource_index ==
                PROGPU_NATIVE_SCENE_NO_INDEX ||
            text_style_index >= implementation_->text_styles.size()))) {
        return implementation_->fail(scene_build_error::invalid_argument);
    }
    for (const auto& glyph : glyphs) {
        if (!semantic::is_valid_semantic_positioned_glyph(
                glyph,
                resource.glyph_outline_count)) {
            return implementation_->fail(scene_build_error::invalid_argument);
        }
    }
    const std::uint64_t payload_size = glyphs.size_bytes() +
        (styled ? sizeof(progpu_native_scene_glyph_draw) : 0U);
    if (payload_size > std::numeric_limits<std::uint32_t>::max()) {
        return implementation_->fail(scene_build_error::capacity_exceeded);
    }
    try {
        implementation_->commands.reserve(
            implementation_->commands.size() + 1U);
        implementation::command_entry command{};
        command.record.struct_size = sizeof(command.record);
        command.record.kind = PROGPU_NATIVE_SCENE_COMMAND_DRAW_GLYPH_RUN;
        command.record.flags = PROGPU_NATIVE_SCENE_RECORD_REQUIRED |
            (styled ? PROGPU_NATIVE_SCENE_GLYPH_STYLED : 0U);
        command.record.command_id = implementation_->commands.size() + 1U;
        command.record.state_index = state_resource_index;
        command.record.resource_index = glyph_resource_index;
        command.record.bounds_x = bounds.x;
        command.record.bounds_y = bounds.y;
        command.record.bounds_width = bounds.width;
        command.record.bounds_height = bounds.height;
        command.payload.resize(static_cast<std::size_t>(payload_size));
        std::size_t glyph_offset = 0U;
        if (styled) {
            const progpu_native_scene_glyph_draw draw{
                sizeof(progpu_native_scene_glyph_draw),
                implementation_->text_style_resource_index,
                text_style_index,
                static_cast<std::uint32_t>(glyphs.size()),
                0U,
                0U};
            std::memcpy(command.payload.data(), &draw, sizeof(draw));
            glyph_offset = sizeof(draw);
        }
        std::memcpy(
            command.payload.data() + glyph_offset,
            glyphs.data(),
            glyphs.size_bytes());
        implementation_->commands.push_back(std::move(command));
        implementation_->error = scene_build_error::none;
        return true;
    } catch (const std::bad_alloc&) {
        return implementation_->fail(scene_build_error::out_of_memory);
    } catch (...) {
        return implementation_->fail(scene_build_error::invalid_state);
    }
}

} // namespace progpu::native
