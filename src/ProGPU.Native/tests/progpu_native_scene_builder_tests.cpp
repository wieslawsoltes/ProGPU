#include "progpu_native_scene_builder_tests.hpp"

#include "progpu_native_scene.hpp"
#include "progpu_native_scene_builder.hpp"
#include "progpu_native_semantic_identity.hpp"

#include <array>
#include <cstring>
#include <vector>

namespace progpu::native::tests {
namespace {

template<typename T>
T read(const std::vector<std::byte>& bytes, std::uint32_t offset) noexcept {
    T value{};
    std::memcpy(&value, bytes.data() + offset, sizeof(value));
    return value;
}

} // namespace

bool semantic_scene_builder_is_deterministic_and_valid() {
    semantic_scene_builder builder(701U, 4U);
    if (!builder.reserve(6U, 6U, 3072U)) {
        return false;
    }
    std::uint32_t blue = PROGPU_NATIVE_SCENE_NO_INDEX;
    std::uint32_t amber = PROGPU_NATIVE_SCENE_NO_INDEX;
    std::uint32_t duplicate_blue = PROGPU_NATIVE_SCENE_NO_INDEX;
    if (!builder.add_solid_brush(
            {0.1F, 0.3F, 0.9F, 1.0F}, 1.0F, blue) ||
        !builder.add_solid_brush(
            {1.0F, 0.5F, 0.1F, 1.0F}, 0.75F, amber) ||
        !builder.add_solid_brush(
            {0.1F, 0.3F, 0.9F, 1.0F}, 1.0F, duplicate_blue) ||
        blue != duplicate_blue || blue == amber) {
        return false;
    }

    auto state = semantic_scene_builder::identity_state();
    state.flags = PROGPU_NATIVE_SCENE_STATE_CLIP_RECT;
    state.transform = {1.0F, 0.0F, 0.0F, 1.0F, 4.0F, 6.0F};
    state.opacity = 0.8F;
    state.clip_rect = {8.0F, 10.0F, 80.0F, 60.0F};
    std::uint32_t state_index = PROGPU_NATIVE_SCENE_NO_INDEX;
    if (!builder.add_state(state, state_index) || !builder.save(state_index)) {
        return false;
    }

    const auto identity = semantic_scene_builder::identity_transform();
    const std::array primitives{
        progpu_native_analytic_primitive{
            PROGPU_NATIVE_PRIMITIVE_RECTANGLE,
            0U,
            10.0F,
            12.0F,
            32.0F,
            24.0F,
            0.0F,
            0.0F,
            {1.0F, 1.0F, 1.0F, 1.0F},
            identity},
        progpu_native_analytic_primitive{
            PROGPU_NATIVE_PRIMITIVE_ROUNDED_RECTANGLE,
            0U,
            48.0F,
            18.0F,
            40.0F,
            30.0F,
            6.0F,
            0.0F,
            {1.0F, 1.0F, 1.0F, 1.0F},
            identity}};
    const std::array brush_indices{blue, amber};
    if (!builder.draw_analytic(
            primitives,
            brush_indices,
            {10.0F, 12.0F, 78.0F, 36.0F})) {
        return false;
    }
    progpu_native_geometry_primitive line{};
    line.kind = PROGPU_NATIVE_GEOMETRY_LINE;
    line.p0 = {12.0F, 54.0F};
    line.p1 = {86.0F, 54.0F};
    line.stroke_thickness = 3.0F;
    line.color = {1.0F, 1.0F, 1.0F, 1.0F};
    line.transform = identity;
    if (!builder.draw_geometry(
            std::span<const progpu_native_geometry_primitive>(&line, 1U),
            std::span<const std::uint32_t>(&amber, 1U),
            {10.0F, 50.0F, 80.0F, 8.0F})) {
        return false;
    }
    const std::array stroke_points{
        progpu_native_point{12.0F, 64.0F},
        progpu_native_point{48.0F, 76.0F},
        progpu_native_point{86.0F, 62.0F}};
    const std::array stroke_doubles{2.0, 1.0};
    progpu_native_scene_stroke stroke{};
    stroke.struct_size = sizeof(stroke);
    stroke.kind = PROGPU_NATIVE_SCENE_STROKE_POLYLINE;
    stroke.point_count = stroke_points.size();
    stroke.dash_interval_count = stroke_doubles.size();
    stroke.color = {1.0F, 1.0F, 1.0F, 1.0F};
    stroke.transform = identity;
    stroke.stroke_thickness = 3.0F;
    stroke.miter_limit = 10.0F;
    stroke.start_cap = PROGPU_NATIVE_STROKE_CAP_ROUND;
    stroke.end_cap = PROGPU_NATIVE_STROKE_CAP_ROUND;
    stroke.line_join = PROGPU_NATIVE_STROKE_JOIN_ROUND;
    stroke.dash_cap = PROGPU_NATIVE_STROKE_CAP_SQUARE;
    if (!builder.draw_strokes(
            std::span<const progpu_native_scene_stroke>(&stroke, 1U),
            stroke_points,
            stroke_doubles,
            std::span<const std::uint32_t>(&blue, 1U),
            {10.0F, 60.0F, 80.0F, 20.0F})) {
        return false;
    }
    const std::array path_segments{
        progpu_native_path_segment{
            {20.0F, 20.0F}, {40.0F, 20.0F}, {}, {},
            PROGPU_NATIVE_PATH_SEGMENT_LINE, 0U, 0U, 0U},
        progpu_native_path_segment{
            {40.0F, 20.0F}, {30.0F, 42.0F}, {}, {},
            PROGPU_NATIVE_PATH_SEGMENT_LINE, 0U, 0U, 0U},
        progpu_native_path_segment{
            {30.0F, 42.0F}, {20.0F, 20.0F}, {}, {},
            PROGPU_NATIVE_PATH_SEGMENT_LINE, 0U, 0U, 0U}};
    const progpu_native_scene_path_fill path{
        0U,
        path_segments.size(),
        20.0F,
        20.0F,
        40.0F,
        42.0F,
        {1.0F, 1.0F, 1.0F, 1.0F},
        identity,
        PROGPU_NATIVE_FILL_RULE_NON_ZERO,
        4U};
    if (!builder.draw_paths(
            std::span<const progpu_native_scene_path_fill>(&path, 1U),
            path_segments,
            std::span<const std::uint32_t>(&amber, 1U),
            {18.0F, 18.0F, 24.0F, 26.0F}) ||
        !builder.restore()) {
        return false;
    }

    std::vector<std::byte> first;
    std::vector<std::byte> second;
    scene_build_metrics metrics{};
    if (!builder.build(first, &metrics) || !builder.build(second) ||
        first != second || metrics.command_count != 6U ||
        metrics.resource_count != 6U || metrics.brush_count != 2U ||
        metrics.maximum_stack_depth != 1U || metrics.arena_bytes == 0U) {
        return false;
    }
    const auto validated = scene::validate(first.data(), first.size());
    if (validated.status != PROGPU_NATIVE_STATUS_SUCCESS ||
        validated.header.scene_id != 701U ||
        validated.header.generation != 4U ||
        validated.draw_count != 4U ||
        validated.maximum_stack_depth != 1U) {
        return false;
    }

    const auto brush_resource = read<progpu_native_scene_resource>(
        first,
        validated.header.resource_offset);
    const auto state_resource = read<progpu_native_scene_resource>(
        first,
        validated.header.resource_offset +
            sizeof(progpu_native_scene_resource));
    const auto analytic_resource = read<progpu_native_scene_resource>(
        first,
        validated.header.resource_offset +
            2U * sizeof(progpu_native_scene_resource));
    const auto geometry_resource = read<progpu_native_scene_resource>(
        first,
        validated.header.resource_offset +
            3U * sizeof(progpu_native_scene_resource));
    const auto stroke_resource = read<progpu_native_scene_resource>(
        first,
        validated.header.resource_offset +
            4U * sizeof(progpu_native_scene_resource));
    const auto path_resource = read<progpu_native_scene_resource>(
        first,
        validated.header.resource_offset +
            5U * sizeof(progpu_native_scene_resource));
    return brush_resource.kind == PROGPU_NATIVE_SCENE_RESOURCE_BRUSH_TABLE &&
        brush_resource.payload_size ==
            2U * sizeof(progpu_native_scene_brush) &&
        state_resource.kind == PROGPU_NATIVE_SCENE_RESOURCE_STATE &&
        analytic_resource.kind ==
            PROGPU_NATIVE_SCENE_RESOURCE_ANALYTIC_BATCH &&
        analytic_resource.payload_size == sizeof(primitives) &&
        geometry_resource.kind ==
            PROGPU_NATIVE_SCENE_RESOURCE_GEOMETRY_BATCH &&
        geometry_resource.payload_size == sizeof(line) &&
        stroke_resource.kind ==
            PROGPU_NATIVE_SCENE_RESOURCE_STROKE_BATCH &&
        stroke_resource.payload_size == sizeof(stroke) &&
        stroke_resource.auxiliary_size ==
            sizeof(stroke_points) + sizeof(stroke_doubles) &&
        path_resource.kind == PROGPU_NATIVE_SCENE_RESOURCE_PATH_BATCH &&
        path_resource.payload_size == sizeof(path) &&
        path_resource.auxiliary_size == sizeof(path_segments);
}

bool semantic_scene_builder_rejects_invalid_state() {
    semantic_scene_builder builder(702U, 1U);
    auto state = semantic_scene_builder::identity_state();
    state.opacity = 2.0F;
    std::uint32_t index = 0U;
    if (builder.add_state(state, index) ||
        builder.last_error() != scene_build_error::invalid_argument ||
        builder.restore() ||
        builder.last_error() != scene_build_error::unbalanced_stack) {
        return false;
    }
    state.opacity = 1.0F;
    if (!builder.add_state(state, index) || !builder.save(index)) {
        return false;
    }
    std::vector<std::byte> stream{std::byte{0x5a}};
    return !builder.build(stream) &&
        builder.last_error() == scene_build_error::unbalanced_stack &&
        stream == std::vector<std::byte>{std::byte{0x5a}};
}

bool semantic_scene_builder_reuses_retained_images() {
    semantic_scene_builder builder(703U, 2U);
    constexpr std::array<std::byte, 16U> pixels{
        std::byte{0xff}, std::byte{0x00}, std::byte{0x00}, std::byte{0xff},
        std::byte{0x00}, std::byte{0xff}, std::byte{0x00}, std::byte{0xff},
        std::byte{0x00}, std::byte{0x00}, std::byte{0xff}, std::byte{0xff},
        std::byte{0xff}, std::byte{0xff}, std::byte{0xff}, std::byte{0xff}};
    std::uint32_t image_index = PROGPU_NATIVE_SCENE_NO_INDEX;
    if (!builder.add_rgba8_image(2U, 2U, 8U, pixels, image_index)) {
        return false;
    }
    progpu_native_scene_image_draw image{};
    image.image_width = 2U;
    image.image_height = 2U;
    image.row_bytes = 8U;
    image.sampling = PROGPU_NATIVE_IMAGE_SAMPLING_NEAREST;
    image.source_rect = {0.0F, 0.0F, 2.0F, 2.0F};
    image.destination_rect = {8.0F, 10.0F, 32.0F, 32.0F};
    image.transform = semantic_scene_builder::identity_transform();
    image.opacity = 1.0F;
    if (!builder.draw_image(
            image_index,
            image,
            {8.0F, 10.0F, 32.0F, 32.0F})) {
        return false;
    }
    image.flags = PROGPU_NATIVE_SCENE_IMAGE_COLOR_MATRIX;
    image.sampling = PROGPU_NATIVE_IMAGE_SAMPLING_CUBIC;
    image.destination_rect = {48.0F, 10.0F, 32.0F, 32.0F};
    const progpu_native_scene_image_sampling_options sampling{
        sizeof(progpu_native_scene_image_sampling_options),
        0U,
        1.0F / 3.0F,
        1.0F / 3.0F};
    const progpu_native_scene_image_color_matrix matrix{
        sizeof(progpu_native_scene_image_color_matrix),
        0U,
        {1.0F, 0.0F, 0.0F, 0.0F},
        {0.0F, 1.0F, 0.0F, 0.0F},
        {0.0F, 0.0F, 1.0F, 0.0F},
        {0.0F, 0.0F, 0.0F, 1.0F},
        {0.0F, 0.0F, 0.0F, 0.0F},
        {0U, 0U}};
    if (!builder.draw_image(
            image_index,
            image,
            {48.0F, 10.0F, 32.0F, 32.0F},
            PROGPU_NATIVE_SCENE_NO_INDEX,
            &sampling,
            &matrix)) {
        return false;
    }

    std::vector<std::byte> stream;
    scene_build_metrics metrics{};
    if (!builder.build(stream, &metrics) || metrics.command_count != 2U ||
        metrics.resource_count != 1U) {
        return false;
    }
    const auto validated = scene::validate(stream.data(), stream.size());
    if (validated.status != PROGPU_NATIVE_STATUS_SUCCESS ||
        validated.draw_count != 2U) {
        return false;
    }
    const auto resource = read<progpu_native_scene_resource>(
        stream,
        validated.header.resource_offset);
    const auto first_command = read<progpu_native_scene_command>(
        stream,
        validated.header.command_offset);
    const auto second_command = read<progpu_native_scene_command>(
        stream,
        validated.header.command_offset +
            sizeof(progpu_native_scene_command));
    if (resource.kind != PROGPU_NATIVE_SCENE_RESOURCE_IMAGE ||
        resource.payload_size != pixels.size() ||
        first_command.resource_index != 0U ||
        second_command.resource_index != 0U ||
        first_command.payload_size != sizeof(progpu_native_scene_image_draw) ||
        second_command.payload_size != sizeof(progpu_native_scene_image_draw) +
            sizeof(progpu_native_scene_image_sampling_options) +
            sizeof(progpu_native_scene_image_color_matrix)) {
        return false;
    }

    semantic_scene_builder invalid(704U, 1U);
    std::uint32_t invalid_index = PROGPU_NATIVE_SCENE_NO_INDEX;
    if (invalid.add_rgba8_image(
            2U,
            2U,
            8U,
            std::span<const std::byte>(pixels).first(15U),
            invalid_index) ||
        invalid.last_error() != scene_build_error::invalid_argument) {
        return false;
    }
    if (!invalid.add_rgba8_image(2U, 2U, 8U, pixels, invalid_index)) {
        return false;
    }
    image.image_width = 3U;
    return !invalid.draw_image(
            invalid_index,
            image,
            {0.0F, 0.0F, 10.0F, 10.0F},
            PROGPU_NATIVE_SCENE_NO_INDEX,
            &sampling,
            &matrix) &&
        invalid.last_error() == scene_build_error::invalid_argument;
}

bool semantic_scene_builder_updates_retained_images_transactionally() {
    semantic_scene_builder builder(704U, 1U);
    constexpr std::array<std::byte, 16U> first_pixels{
        std::byte{0xff}, std::byte{0x00}, std::byte{0x00}, std::byte{0xff},
        std::byte{0x00}, std::byte{0xff}, std::byte{0x00}, std::byte{0xff},
        std::byte{0x00}, std::byte{0x00}, std::byte{0xff}, std::byte{0xff},
        std::byte{0xff}, std::byte{0xff}, std::byte{0xff}, std::byte{0xff}};
    constexpr std::array<std::byte, 16U> second_pixels{
        std::byte{0xff}, std::byte{0xff}, std::byte{0x00}, std::byte{0xff},
        std::byte{0x00}, std::byte{0xff}, std::byte{0xff}, std::byte{0xff},
        std::byte{0xff}, std::byte{0x00}, std::byte{0xff}, std::byte{0xff},
        std::byte{0x20}, std::byte{0x40}, std::byte{0x80}, std::byte{0xff}};
    std::uint32_t image_index = PROGPU_NATIVE_SCENE_NO_INDEX;
    if (!builder.add_rgba8_image(
            2U, 2U, 8U, first_pixels, image_index) ||
        !builder.set_resource_identity(image_index, 100U, 1U)) {
        return false;
    }
    progpu_native_scene_image_draw image{};
    image.image_width = 2U;
    image.image_height = 2U;
    image.row_bytes = 8U;
    image.sampling = PROGPU_NATIVE_IMAGE_SAMPLING_NEAREST;
    image.source_rect = {0.0F, 0.0F, 2.0F, 2.0F};
    image.destination_rect = {0.0F, 0.0F, 20.0F, 20.0F};
    image.transform = semantic_scene_builder::identity_transform();
    image.opacity = 1.0F;
    std::vector<std::byte> first;
    if (!builder.draw_image(
            image_index, image, {0.0F, 0.0F, 20.0F, 20.0F}) ||
        !builder.build(first) || !builder.advance_generation(2U) ||
        builder.advance_generation(2U) ||
        builder.last_error() != scene_build_error::invalid_argument ||
        builder.update_rgba8_image(
            image_index, 3U, 2U, 8U, second_pixels, 2U) ||
        builder.last_error() != scene_build_error::invalid_argument ||
        !builder.update_rgba8_image(
            image_index, 2U, 2U, 8U, second_pixels, 2U)) {
        return false;
    }
    std::vector<std::byte> second;
    if (!builder.build(second)) {
        return false;
    }
    const auto first_validation = scene::validate(first.data(), first.size());
    const auto second_validation =
        scene::validate(second.data(), second.size());
    if (first_validation.status != PROGPU_NATIVE_STATUS_SUCCESS ||
        second_validation.status != PROGPU_NATIVE_STATUS_SUCCESS ||
        first_validation.header.generation != 1U ||
        second_validation.header.generation != 2U) {
        return false;
    }
    const auto first_resource = read<progpu_native_scene_resource>(
        first,
        first_validation.header.resource_offset);
    const auto second_resource = read<progpu_native_scene_resource>(
        second,
        second_validation.header.resource_offset);
    return first_resource.resource_id == second_resource.resource_id &&
        first_resource.generation == 1U &&
        second_resource.generation == 2U &&
        std::memcmp(
            second.data() + second_resource.payload_offset,
            second_pixels.data(),
            second_pixels.size()) == 0;
}

bool semantic_scene_builder_records_styled_glyph_runs() {
    semantic_scene_builder builder(705U, 6U);
    const std::array segments{
        progpu_native_path_segment{
            {0.0F, 0.0F}, {16.0F, 0.0F}, {}, {},
            PROGPU_NATIVE_PATH_SEGMENT_LINE, 0U, 0U, 0U},
        progpu_native_path_segment{
            {16.0F, 0.0F}, {16.0F, 20.0F}, {}, {},
            PROGPU_NATIVE_PATH_SEGMENT_LINE, 0U, 0U, 0U},
        progpu_native_path_segment{
            {16.0F, 20.0F}, {0.0F, 20.0F}, {}, {},
            PROGPU_NATIVE_PATH_SEGMENT_LINE, 0U, 0U, 0U},
        progpu_native_path_segment{
            {0.0F, 20.0F}, {0.0F, 0.0F}, {}, {},
            PROGPU_NATIVE_PATH_SEGMENT_LINE, 0U, 0U, 0U}};
    const progpu_native_scene_glyph_outline outline{
        0U,
        segments.size(),
        0.0F,
        0.0F,
        16.0F,
        20.0F,
        1.0F,
        0.25F};
    std::uint32_t glyph_resource = PROGPU_NATIVE_SCENE_NO_INDEX;
    if (!builder.add_glyph_outlines(
            std::span<const progpu_native_scene_glyph_outline>(&outline, 1U),
            segments,
            glyph_resource)) {
        return false;
    }
    const progpu_native_scene_text_style style{
        {0.9F, 0.8F, 0.2F, 1.0F},
        PROGPU_NATIVE_SCENE_TEXT_GRAYSCALE,
        0U,
        0U,
        0U};
    std::uint32_t style_index = PROGPU_NATIVE_SCENE_NO_INDEX;
    std::uint32_t duplicate_style = PROGPU_NATIVE_SCENE_NO_INDEX;
    if (!builder.add_text_style(style, style_index) ||
        !builder.add_text_style(style, duplicate_style) ||
        style_index != duplicate_style) {
        return false;
    }
    const std::array glyphs{
        progpu_native_positioned_glyph{
            0U,
            0U,
            {24.0F, 32.0F},
            {1.0F, 0.0F},
            {0.0F, 1.0F},
            {1.0F, 1.0F, 1.0F, 1.0F},
            1.0F,
            0.0F,
            0.0F,
            0.0F},
        progpu_native_positioned_glyph{
            0U,
            0U,
            {48.0F, 32.0F},
            {1.0F, 0.0F},
            {0.0F, 1.0F},
            {1.0F, 1.0F, 1.0F, 1.0F},
            1.0F,
            0.5F,
            0.1F,
            0.0F}};
    if (!builder.draw_glyph_run(
            glyph_resource,
            glyphs,
            {24.0F, 32.0F, 40.0F, 20.0F},
            PROGPU_NATIVE_SCENE_NO_INDEX,
            style_index)) {
        return false;
    }
    std::vector<std::byte> stream;
    scene_build_metrics metrics{};
    if (!builder.build(stream, &metrics) || metrics.command_count != 1U ||
        metrics.resource_count != 2U || metrics.text_style_count != 1U) {
        return false;
    }
    const auto validated = scene::validate(stream.data(), stream.size());
    if (validated.status != PROGPU_NATIVE_STATUS_SUCCESS ||
        validated.draw_count != 1U) {
        return false;
    }
    const auto glyph_record = read<progpu_native_scene_resource>(
        stream,
        validated.header.resource_offset);
    const auto style_record = read<progpu_native_scene_resource>(
        stream,
        validated.header.resource_offset +
            sizeof(progpu_native_scene_resource));
    const auto command = read<progpu_native_scene_command>(
        stream,
        validated.header.command_offset);
    return glyph_record.kind == PROGPU_NATIVE_SCENE_RESOURCE_GLYPH_RUN &&
        glyph_record.payload_size == sizeof(outline) &&
        glyph_record.auxiliary_size == sizeof(segments) &&
        style_record.kind ==
            PROGPU_NATIVE_SCENE_RESOURCE_TEXT_STYLE_TABLE &&
        style_record.payload_size == sizeof(style) &&
        (command.flags & PROGPU_NATIVE_SCENE_GLYPH_STYLED) != 0U &&
        command.payload_size == sizeof(progpu_native_scene_glyph_draw) +
            sizeof(glyphs);
}

bool semantic_scene_builder_records_native_shaped_runs() {
    semantic_scene_builder builder(711U, 2U);
    const std::array segments{
        progpu_native_path_segment{
            {0.0F, 0.0F}, {8.0F, 0.0F}, {}, {},
            PROGPU_NATIVE_PATH_SEGMENT_LINE, 0U, 0U, 0U},
        progpu_native_path_segment{
            {8.0F, 0.0F}, {8.0F, 12.0F}, {}, {},
            PROGPU_NATIVE_PATH_SEGMENT_LINE, 0U, 0U, 0U},
        progpu_native_path_segment{
            {8.0F, 12.0F}, {0.0F, 12.0F}, {}, {},
            PROGPU_NATIVE_PATH_SEGMENT_LINE, 0U, 0U, 0U},
        progpu_native_path_segment{
            {0.0F, 12.0F}, {0.0F, 0.0F}, {}, {},
            PROGPU_NATIVE_PATH_SEGMENT_LINE, 0U, 0U, 0U}};
    const progpu_native_scene_glyph_outline outline{
        0U, segments.size(), 0.0F, 0.0F, 8.0F, 12.0F, 1.0F, 0.0F};
    std::uint32_t glyph_resource = PROGPU_NATIVE_SCENE_NO_INDEX;
    if (!builder.add_glyph_outlines(
            std::span<const progpu_native_scene_glyph_outline>(&outline, 1U),
            segments,
            glyph_resource)) {
        return false;
    }

    const std::array shaped{
        text::shaping_glyph{5U, 0x41U, 0, text::shaping_glyph_flags::none,
            10, 0, 0, 0},
        text::shaping_glyph{7U, 0x42U, 1, text::shaping_glyph_flags::none,
            10, 0, 0, 0}};
    const std::array positioned{
        text::positioned_text_glyph{
            0U, 5U, 0, 12.0F, 20.0F, 10.0F, 0.0F},
        text::positioned_text_glyph{
            1U, 7U, 1, 22.0F, 20.0F, 10.0F, 0.0F}};
    std::array<progpu_native_positioned_glyph, 2U> conversion{};
    std::array<std::uint32_t, 8U> glyph_to_outline{};
    const shaped_text_scene_options options{
        {1.25F, 0.0F},
        {0.0F, 1.25F},
        {0.2F, 0.7F, 1.0F, 0.9F},
        0.8F,
        0.25F,
        0.1F};
    if (!builder.draw_shaped_text_run(
            glyph_resource,
            shaped,
            positioned,
            conversion,
            options,
            {10.0F, 8.0F, 24.0F, 16.0F},
            glyph_to_outline)) {
        return false;
    }
    if (conversion[0U].outline_index != 0U ||
        conversion[1U].outline_index != 0U ||
        conversion[0U].position.x != 12.0F ||
        conversion[1U].position.x != 22.0F ||
        conversion[0U].basis_x.x != 1.25F ||
        conversion[0U].color.a != 0.9F ||
        conversion[0U].atlas_to_logical_scale != 0.8F) {
        return false;
    }

    std::vector<std::byte> stream;
    if (!builder.build(stream)) {
        return false;
    }
    const auto validated = scene::validate(stream.data(), stream.size());
    if (validated.status != PROGPU_NATIVE_STATUS_SUCCESS ||
        validated.draw_count != 1U) {
        return false;
    }
    const auto command = read<progpu_native_scene_command>(
        stream, validated.header.command_offset);
    const auto first = read<progpu_native_positioned_glyph>(
        stream, command.payload_offset);
    return command.kind == PROGPU_NATIVE_SCENE_COMMAND_DRAW_GLYPH_RUN &&
        command.payload_size == sizeof(conversion) &&
        first.position.x == 12.0F && first.bold_offset == 0.25F;
}

bool semantic_scene_builder_records_color_bitmap_glyphs() {
    semantic_scene_builder builder(707U, 8U);
    constexpr std::array<std::byte, 32U> pixels{
        std::byte{0xff}, std::byte{0x20}, std::byte{0x40}, std::byte{0xff},
        std::byte{0x20}, std::byte{0xe0}, std::byte{0x80}, std::byte{0xff},
        std::byte{0x20}, std::byte{0xe0}, std::byte{0x80}, std::byte{0xff},
        std::byte{0xff}, std::byte{0x20}, std::byte{0x40}, std::byte{0xff},
        std::byte{0x20}, std::byte{0x40}, std::byte{0xff}, std::byte{0xff},
        std::byte{0xf0}, std::byte{0xc0}, std::byte{0x20}, std::byte{0xff},
        std::byte{0xf0}, std::byte{0xc0}, std::byte{0x20}, std::byte{0xff},
        std::byte{0x20}, std::byte{0x40}, std::byte{0xff}, std::byte{0xff}};
    const std::array bitmaps{
        progpu_native_scene_color_glyph_bitmap{
            0U, 2U, 2U, 8U, 0U,
            -1.0F, 2.0F, 18.0F, 20.0F, 0U, 0U},
        progpu_native_scene_color_glyph_bitmap{
            16U, 2U, 2U, 8U, 0U,
            1.0F, 1.0F, 16.0F, 18.0F, 0U, 0U}};
    std::uint32_t resource_index = PROGPU_NATIVE_SCENE_NO_INDEX;
    if (!builder.add_color_glyph_bitmaps(
            bitmaps,
            pixels,
            resource_index)) {
        return false;
    }
    const std::array glyphs{
        progpu_native_positioned_glyph{
            0U, 0U, {20.0F, 30.0F}, {1.0F, 0.0F}, {0.0F, 1.0F},
            {1.0F, 1.0F, 1.0F, 1.0F}, 1.0F, 0.0F, 0.0F, 0.0F},
        progpu_native_positioned_glyph{
            1U, 0U, {44.0F, 30.0F}, {1.0F, 0.0F}, {0.0F, 1.0F},
            {1.0F, 1.0F, 1.0F, 0.75F}, 1.0F, 0.0F, 0.0F, 0.0F}};
    if (!builder.draw_glyph_run(
            resource_index,
            glyphs,
            {18.0F, 10.0F, 44.0F, 22.0F})) {
        return false;
    }
    std::vector<std::byte> stream;
    if (!builder.build(stream)) {
        return false;
    }
    const auto validated = scene::validate(stream.data(), stream.size());
    if (validated.status != PROGPU_NATIVE_STATUS_SUCCESS ||
        validated.draw_count != 1U) {
        return false;
    }
    const auto resource = read<progpu_native_scene_resource>(
        stream,
        validated.header.resource_offset);
    const auto command = read<progpu_native_scene_command>(
        stream,
        validated.header.command_offset);
    auto invalid_bitmap = bitmaps[1U];
    invalid_bitmap.pixel_offset = pixels.size();
    semantic_scene_builder invalid(708U, 1U);
    std::uint32_t invalid_index = PROGPU_NATIVE_SCENE_NO_INDEX;
    return resource.kind == PROGPU_NATIVE_SCENE_RESOURCE_GLYPH_RUN &&
        (resource.flags & PROGPU_NATIVE_SCENE_COLOR_GLYPH_BITMAPS) != 0U &&
        resource.payload_size == sizeof(bitmaps) &&
        resource.auxiliary_size == pixels.size() &&
        command.kind == PROGPU_NATIVE_SCENE_COMMAND_DRAW_GLYPH_RUN &&
        command.payload_size == sizeof(glyphs) &&
        !invalid.add_color_glyph_bitmaps(
            std::span<const progpu_native_scene_color_glyph_bitmap>(
                &invalid_bitmap,
                1U),
            pixels,
            invalid_index) &&
        invalid.last_error() == scene_build_error::invalid_argument;
}

bool semantic_scene_builder_records_layers_masks_and_effects() {
    semantic_scene_builder builder(706U, 7U);
    if (!builder.reserve(3U, 6U, 2048U)) {
        return false;
    }
    const auto identity = semantic_scene_builder::identity_transform();
    progpu_native_scene_layer_mask left{};
    left.bounds = {4.0F, 4.0F, 56.0F, 48.0F};
    left.transform = identity;
    left.corner_radii_x[0] = 6.0F;
    left.corner_radii_x[1] = 6.0F;
    left.corner_radii_x[2] = 6.0F;
    left.corner_radii_x[3] = 6.0F;
    left.corner_radii_y[0] = 6.0F;
    left.corner_radii_y[1] = 6.0F;
    left.corner_radii_y[2] = 6.0F;
    left.corner_radii_y[3] = 6.0F;
    left.opacity = 1.0F;
    auto right = left;
    right.bounds = {20.0F, 8.0F, 56.0F, 48.0F};
    right.transform.m31 = 2.0F;

    std::uint32_t rounded_mask = PROGPU_NATIVE_SCENE_NO_INDEX;
    std::uint32_t coverage_mask = PROGPU_NATIVE_SCENE_NO_INDEX;
    std::uint32_t chain_mask = PROGPU_NATIVE_SCENE_NO_INDEX;
    if (!builder.add_rounded_rectangle_mask(left, rounded_mask)) {
        return false;
    }
    progpu_native_scene_layer_coverage_mask coverage{};
    coverage.width = 4U;
    coverage.height = 4U;
    coverage.row_bytes = 4U;
    coverage.sampling = PROGPU_NATIVE_IMAGE_SAMPLING_LINEAR;
    coverage.bounds = {0.0F, 0.0F, 4.0F, 4.0F};
    coverage.transform = {16.0F, 0.0F, 0.0F, 12.0F, 4.0F, 4.0F};
    coverage.opacity = 0.75F;
    const std::array<std::byte, 16U> coverage_bytes{
        std::byte{0x00}, std::byte{0x40}, std::byte{0x80}, std::byte{0xff},
        std::byte{0x40}, std::byte{0x80}, std::byte{0xff}, std::byte{0x80},
        std::byte{0x80}, std::byte{0xff}, std::byte{0x80}, std::byte{0x40},
        std::byte{0xff}, std::byte{0x80}, std::byte{0x40}, std::byte{0x00}};
    const std::array analytic_masks{left, right};
    if (!builder.add_coverage_mask(
            coverage,
            coverage_bytes,
            coverage_mask) ||
        !builder.add_analytic_mask_chain(analytic_masks, chain_mask) ||
        rounded_mask == coverage_mask || coverage_mask == chain_mask) {
        return false;
    }

    progpu_native_group_effect blur{};
    blur.kind = PROGPU_NATIVE_GROUP_EFFECT_GAUSSIAN_BLUR;
    blur.revision = 31U;
    blur.sigma_x = 1.5F;
    blur.sigma_y = 2.0F;
    std::uint32_t effect_chain = PROGPU_NATIVE_SCENE_NO_INDEX;
    if (!builder.add_effect_chain(
            std::span<const progpu_native_group_effect>(&blur, 1U),
            41U,
            effect_chain)) {
        return false;
    }

    auto state = semantic_scene_builder::identity_state();
    state.flags = PROGPU_NATIVE_SCENE_STATE_MASK;
    state.mask_resource_index = rounded_mask;
    std::uint32_t state_index = PROGPU_NATIVE_SCENE_NO_INDEX;
    if (!builder.add_state(state, state_index)) {
        return false;
    }
    progpu_native_scene_layer layer{};
    layer.flags = PROGPU_NATIVE_SCENE_LAYER_BOUNDS |
        PROGPU_NATIVE_SCENE_LAYER_FORCE_ISOLATION;
    layer.bounds = {0.0F, 0.0F, 96.0F, 64.0F};
    layer.opacity = 0.85F;
    layer.blend_mode = PROGPU_NATIVE_BLEND_OVERLAY;
    layer.mask_resource_index = chain_mask;
    layer.effect_resource_index = effect_chain;
    layer.content_revision = 51U;
    layer.composite_revision = 52U;
    if (!builder.push_layer(layer) || builder.restore() ||
        builder.last_error() != scene_build_error::unbalanced_stack) {
        return false;
    }
    const progpu_native_analytic_primitive primitive{
        PROGPU_NATIVE_PRIMITIVE_ROUNDED_RECTANGLE,
        0U,
        8.0F,
        8.0F,
        72.0F,
        44.0F,
        8.0F,
        0.0F,
        {0.2F, 0.7F, 1.0F, 0.9F},
        identity};
    if (!builder.draw_analytic(
            std::span<const progpu_native_analytic_primitive>(
                &primitive,
                1U),
            {},
            {8.0F, 8.0F, 72.0F, 44.0F},
            state_index) ||
        !builder.pop_layer()) {
        return false;
    }

    std::vector<std::byte> first;
    std::vector<std::byte> second;
    scene_build_metrics metrics{};
    if (!builder.build(first, &metrics) || !builder.build(second) ||
        first != second || metrics.command_count != 3U ||
        metrics.resource_count != 6U ||
        metrics.maximum_stack_depth != 1U) {
        return false;
    }
    const auto validated = scene::validate(first.data(), first.size());
    if (validated.status != PROGPU_NATIVE_STATUS_SUCCESS ||
        validated.draw_count != 1U || validated.maximum_stack_depth != 1U) {
        return false;
    }
    const auto resource_at = [&](std::uint32_t index) {
        return read<progpu_native_scene_resource>(
            first,
            validated.header.resource_offset +
                index * sizeof(progpu_native_scene_resource));
    };
    const auto rounded_record = resource_at(rounded_mask);
    const auto coverage_record = resource_at(coverage_mask);
    const auto chain_record = resource_at(chain_mask);
    const auto effect_record = resource_at(effect_chain);
    const auto state_record = resource_at(state_index);
    const auto push = read<progpu_native_scene_command>(
        first,
        validated.header.command_offset);
    const auto draw = read<progpu_native_scene_command>(
        first,
        validated.header.command_offset +
            sizeof(progpu_native_scene_command));
    const auto pop = read<progpu_native_scene_command>(
        first,
        validated.header.command_offset +
            2U * sizeof(progpu_native_scene_command));
    return rounded_record.kind == PROGPU_NATIVE_SCENE_RESOURCE_LAYER_MASK &&
        rounded_record.payload_size == sizeof(left) &&
        coverage_record.kind == PROGPU_NATIVE_SCENE_RESOURCE_LAYER_MASK &&
        coverage_record.payload_size == sizeof(coverage) &&
        coverage_record.auxiliary_size == coverage_bytes.size() &&
        chain_record.kind == PROGPU_NATIVE_SCENE_RESOURCE_LAYER_MASK &&
        chain_record.payload_size ==
            sizeof(progpu_native_scene_layer_mask_chain) &&
        effect_record.kind == PROGPU_NATIVE_SCENE_RESOURCE_EFFECT_CHAIN &&
        effect_record.payload_size ==
            sizeof(progpu_native_scene_effect_chain) &&
        effect_record.auxiliary_size == sizeof(blur) &&
        state_record.kind == PROGPU_NATIVE_SCENE_RESOURCE_STATE &&
        push.kind == PROGPU_NATIVE_SCENE_COMMAND_PUSH_LAYER &&
        push.payload_size == sizeof(layer) &&
        draw.kind == PROGPU_NATIVE_SCENE_COMMAND_DRAW_ANALYTIC &&
        draw.state_index == state_index &&
        pop.kind == PROGPU_NATIVE_SCENE_COMMAND_POP_LAYER;
}

bool semantic_scene_builder_preserves_stable_resource_identities() {
    semantic_scene_builder builder(709U, 12U);
    std::uint32_t brush = PROGPU_NATIVE_SCENE_NO_INDEX;
    std::uint32_t state_index = PROGPU_NATIVE_SCENE_NO_INDEX;
    if (!builder.add_solid_brush(
            {0.2F, 0.5F, 0.9F, 1.0F},
            1.0F,
            brush)) {
        return false;
    }
    auto state = semantic_scene_builder::identity_state();
    state.opacity = 0.8F;
    if (!builder.add_state(state, state_index)) {
        return false;
    }
    const progpu_native_analytic_primitive primitive{
        PROGPU_NATIVE_PRIMITIVE_RECTANGLE,
        0U,
        4.0F,
        6.0F,
        32.0F,
        20.0F,
        0.0F,
        0.0F,
        {1.0F, 1.0F, 1.0F, 1.0F},
        semantic_scene_builder::identity_transform()};
    if (!builder.draw_analytic(
            std::span<const progpu_native_analytic_primitive>(
                &primitive,
                1U),
            std::span<const std::uint32_t>(&brush, 1U),
            {4.0F, 6.0F, 32.0F, 20.0F},
            state_index) ||
        !builder.set_resource_identity(0U, 100U, 7U) ||
        !builder.set_resource_identity(state_index, 200U, 4U) ||
        !builder.set_resource_identity(2U, 300U, 9U)) {
        return false;
    }
    std::vector<std::byte> stream;
    if (!builder.build(stream)) {
        return false;
    }
    const auto validated = scene::validate(stream.data(), stream.size());
    if (validated.status != PROGPU_NATIVE_STATUS_SUCCESS ||
        validated.header.resource_count != 3U) {
        return false;
    }
    const auto first = read<progpu_native_scene_resource>(
        stream,
        validated.header.resource_offset);
    const auto second = read<progpu_native_scene_resource>(
        stream,
        validated.header.resource_offset + validated.header.resource_stride);
    const auto third = read<progpu_native_scene_resource>(
        stream,
        validated.header.resource_offset +
            2U * validated.header.resource_stride);
    std::vector<std::byte> unchanged{std::byte{0x5a}};
    return first.resource_id == 100U && first.generation == 7U &&
        second.resource_id == 200U && second.generation == 4U &&
        third.resource_id == 300U && third.generation == 9U &&
        builder.set_resource_identity(2U, 150U, 9U) &&
        !builder.build(unchanged) &&
        builder.last_error() == scene_build_error::invalid_state &&
        unchanged == std::vector<std::byte>{std::byte{0x5a}};
}

bool semantic_scene_builder_records_retained_3d_families() {
    progpu_native_matrix_4x4 identity{};
    identity.m11 = 1.0F;
    identity.m22 = 1.0F;
    identity.m33 = 1.0F;
    identity.m44 = 1.0F;
    progpu_native_scene_camera_3d camera{};
    camera.struct_size = sizeof(camera);
    camera.projection = identity;
    camera.view = identity;

    progpu_native_scene_line_3d line{};
    line.struct_size = sizeof(line);
    line.start = {-0.75F, -0.5F, 0.0F, 0.0F};
    line.end = {0.75F, 0.5F, 0.0F, 0.0F};
    line.color = {0.1F, 0.7F, 1.0F, 1.0F};
    line.thickness = 2.0F;
    line.opacity = 1.0F;
    line.transform = identity;

    const std::array vertices{
        progpu_native_scene_mesh_3d_vertex{
            {-0.5F, -0.5F, 0.0F, 0.0F},
            {0.0F, 0.0F, 1.0F, 0.0F},
            {0.0F, 1.0F},
            0U,
            0U},
        progpu_native_scene_mesh_3d_vertex{
            {0.5F, -0.5F, 0.0F, 0.0F},
            {0.0F, 0.0F, 1.0F, 0.0F},
            {1.0F, 1.0F},
            0U,
            0U},
        progpu_native_scene_mesh_3d_vertex{
            {0.0F, 0.5F, 0.0F, 0.0F},
            {0.0F, 0.0F, 1.0F, 0.0F},
            {0.5F, 0.0F},
            0U,
            0U}};
    constexpr std::array<std::uint32_t, 3U> indices{0U, 1U, 2U};
    progpu_native_scene_mesh_3d mesh{};
    mesh.struct_size = sizeof(mesh);
    mesh.topology = PROGPU_NATIVE_MESH_3D_TRIANGLES;
    mesh.render_mode = PROGPU_NATIVE_MESH_3D_SOLID;
    mesh.vertex_count = vertices.size();
    mesh.index_count = indices.size();
    mesh.model_transform = identity;
    mesh.normal_transform = identity;
    mesh.color = {0.9F, 0.4F, 0.1F, 1.0F};
    mesh.light_direction = {0.0F, 0.0F, -1.0F, 1.0F};
    mesh.ambient_color = {1.0F, 1.0F, 1.0F, 0.2F};
    mesh.specular_color = {1.0F, 1.0F, 1.0F, 16.0F};
    mesh.material_ambient = {0.1F, 0.1F, 0.1F, 0.0F};
    mesh.opacity = 1.0F;

    semantic_scene_builder builder(712U, 3U);
    if (!builder.draw_lines_3d(
            std::span<const progpu_native_scene_line_3d>(&line, 1U),
            camera,
            {0.0F, 0.0F, 256.0F, 256.0F}) ||
        !builder.draw_meshes_3d(
            std::span<const progpu_native_scene_mesh_3d>(&mesh, 1U),
            vertices,
            indices,
            camera,
            {0.0F, 0.0F, 256.0F, 256.0F})) {
        return false;
    }
    std::vector<std::byte> stream;
    scene_build_metrics metrics{};
    if (!builder.build(stream, &metrics)) {
        return false;
    }
    const auto validated = scene::validate(stream.data(), stream.size());
    if (validated.status != PROGPU_NATIVE_STATUS_SUCCESS ||
        validated.header.resource_count != 2U ||
        validated.header.command_count != 2U ||
        metrics.command_count != 2U || metrics.resource_count != 2U) {
        return false;
    }
    const auto line_resource = read<progpu_native_scene_resource>(
        stream, validated.header.resource_offset);
    const auto mesh_resource = read<progpu_native_scene_resource>(
        stream,
        validated.header.resource_offset + validated.header.resource_stride);
    const auto line_command = read<progpu_native_scene_command>(
        stream, validated.header.command_offset);
    const auto mesh_command = read<progpu_native_scene_command>(
        stream,
        validated.header.command_offset + validated.header.command_stride);
    return line_resource.kind ==
            PROGPU_NATIVE_SCENE_RESOURCE_LINE_3D_BATCH &&
        line_resource.payload_size == sizeof(line) &&
        mesh_resource.kind ==
            PROGPU_NATIVE_SCENE_RESOURCE_MESH_3D_BATCH &&
        mesh_resource.payload_size == sizeof(mesh) &&
        mesh_resource.auxiliary_size == sizeof(vertices) +
            sizeof(indices) &&
        line_command.kind ==
            PROGPU_NATIVE_SCENE_COMMAND_DRAW_LINE_3D_BATCH &&
        mesh_command.kind ==
            PROGPU_NATIVE_SCENE_COMMAND_DRAW_MESH_3D_BATCH &&
        line_command.payload_size == sizeof(camera) &&
        mesh_command.payload_size == sizeof(camera);
}

bool semantic_scene_content_hashes_isolate_image_updates() {
    semantic_scene_builder builder(710U, 1U);
    std::uint32_t brush = PROGPU_NATIVE_SCENE_NO_INDEX;
    if (!builder.add_solid_brush(
            {0.2F, 0.5F, 0.9F, 1.0F}, 1.0F, brush)) {
        return false;
    }
    const progpu_native_analytic_primitive primitive{
        PROGPU_NATIVE_PRIMITIVE_RECTANGLE,
        0U,
        0.0F,
        0.0F,
        20.0F,
        20.0F,
        0.0F,
        0.0F,
        {1.0F, 1.0F, 1.0F, 1.0F},
        semantic_scene_builder::identity_transform()};
    constexpr std::array<std::byte, 16U> first_pixels{
        std::byte{0xff}, std::byte{0x00}, std::byte{0x00}, std::byte{0xff},
        std::byte{0x00}, std::byte{0xff}, std::byte{0x00}, std::byte{0xff},
        std::byte{0x00}, std::byte{0x00}, std::byte{0xff}, std::byte{0xff},
        std::byte{0xff}, std::byte{0xff}, std::byte{0xff}, std::byte{0xff}};
    constexpr std::array<std::byte, 16U> second_pixels{
        std::byte{0xff}, std::byte{0xff}, std::byte{0x00}, std::byte{0xff},
        std::byte{0x00}, std::byte{0xff}, std::byte{0xff}, std::byte{0xff},
        std::byte{0xff}, std::byte{0x00}, std::byte{0xff}, std::byte{0xff},
        std::byte{0x20}, std::byte{0x40}, std::byte{0x80}, std::byte{0xff}};
    std::uint32_t image_index = PROGPU_NATIVE_SCENE_NO_INDEX;
    progpu_native_scene_image_draw image{};
    image.image_width = 2U;
    image.image_height = 2U;
    image.row_bytes = 8U;
    image.sampling = PROGPU_NATIVE_IMAGE_SAMPLING_NEAREST;
    image.source_rect = {0.0F, 0.0F, 2.0F, 2.0F};
    image.destination_rect = {24.0F, 0.0F, 20.0F, 20.0F};
    image.transform = semantic_scene_builder::identity_transform();
    image.opacity = 1.0F;
    if (!builder.draw_analytic(
            std::span<const progpu_native_analytic_primitive>(
                &primitive,
                1U),
            std::span<const std::uint32_t>(&brush, 1U),
            {0.0F, 0.0F, 20.0F, 20.0F}) ||
        !builder.add_rgba8_image(
            2U, 2U, 8U, first_pixels, image_index) ||
        !builder.draw_image(
            image_index, image, {24.0F, 0.0F, 20.0F, 20.0F})) {
        return false;
    }
    std::vector<std::byte> first;
    std::vector<std::byte> second;
    if (!builder.build(first) || !builder.advance_generation(2U) ||
        !builder.update_rgba8_image(
            image_index, 2U, 2U, 8U, second_pixels, 2U) ||
        !builder.build(second)) {
        return false;
    }
    const auto first_header = read<progpu_native_scene_header>(first, 0U);
    const auto second_header = read<progpu_native_scene_header>(second, 0U);
    const auto first_hashes = semantic::compute_content_hashes(
        first.data(), first_header);
    const auto second_hashes = semantic::compute_content_hashes(
        second.data(), second_header);
    return first_hashes.brush == second_hashes.brush &&
        first_hashes.text_style == second_hashes.text_style &&
        first_hashes.analytic == second_hashes.analytic &&
        first_hashes.path == second_hashes.path &&
        first_hashes.glyph == second_hashes.glyph &&
        first_hashes.image != second_hashes.image;
}

} // namespace progpu::native::tests
