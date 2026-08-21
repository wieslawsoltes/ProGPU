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
            PROGPU_NATIVE_PATH_SEGMENT_LINE, 0U, 0U, 0U},
        progpu_native_path_segment{
            {27.0F, 25.0F}, {33.0F, 25.0F}, {}, {},
            PROGPU_NATIVE_PATH_SEGMENT_LINE, 0U, 0U, 0U},
        progpu_native_path_segment{
            {33.0F, 25.0F}, {30.0F, 34.0F}, {}, {},
            PROGPU_NATIVE_PATH_SEGMENT_LINE, 0U, 0U, 0U},
        progpu_native_path_segment{
            {30.0F, 34.0F}, {27.0F, 25.0F}, {}, {},
            PROGPU_NATIVE_PATH_SEGMENT_LINE, 0U, 0U, 0U}};
    const std::array path_boolean_nodes{
        progpu_native_scene_path_boolean_node{
            0U, 3U, 20.0F, 20.0F, 40.0F, 42.0F,
            PROGPU_NATIVE_FILL_RULE_NON_ZERO,
            PROGPU_NATIVE_PATH_BOOLEAN_LEAF, 0U, 0U},
        progpu_native_scene_path_boolean_node{
            3U, 3U, 27.0F, 25.0F, 33.0F, 34.0F,
            PROGPU_NATIVE_FILL_RULE_NON_ZERO,
            PROGPU_NATIVE_PATH_BOOLEAN_LEAF, 0U, 0U},
        progpu_native_scene_path_boolean_node{
            0U, 0U, 0.0F, 0.0F, 0.0F, 0.0F,
            PROGPU_NATIVE_FILL_RULE_NON_ZERO,
            PROGPU_NATIVE_PATH_BOOLEAN_DIFFERENCE, 0U, 0U}};
    const progpu_native_scene_path_fill path{
        0U,
        path_segments.size(),
        0U,
        path_boolean_nodes.size(),
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
            {18.0F, 18.0F, 24.0F, 26.0F},
            PROGPU_NATIVE_SCENE_NO_INDEX,
            path_boolean_nodes) ||
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
    const std::size_t required_size = builder.required_stream_size();
    std::vector<std::byte> caller_owned(
        required_size,
        std::byte{0xA5U});
    std::size_t bytes_written = 0U;
    scene_build_metrics caller_metrics{};
    if (required_size != first.size() ||
        !builder.build_into(caller_owned, bytes_written, &caller_metrics) ||
        bytes_written != required_size || caller_owned != first ||
        caller_metrics.stream_bytes != metrics.stream_bytes ||
        caller_metrics.arena_bytes != metrics.arena_bytes) {
        return false;
    }
    std::size_t rejected_bytes = 1U;
    if (builder.build_into(
            std::span<std::byte>{
            caller_owned.data(), caller_owned.size() - 1U},
            rejected_bytes) ||
        rejected_bytes != 0U ||
        caller_owned != first ||
        builder.last_error() != scene_build_error::capacity_exceeded) {
        return false;
    }

    const auto validated = scene::validate(
        caller_owned.data(),
        bytes_written);
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
        path_resource.auxiliary_size ==
            sizeof(path_segments) + sizeof(path_boolean_nodes);
}

bool semantic_scene_builder_preserves_shared_path_segments() {
    const std::array segments{
        progpu_native_path_segment{
            {0.0F, 0.0F}, {12.0F, 0.0F}, {}, {},
            PROGPU_NATIVE_PATH_SEGMENT_LINE, 0U, 0U, 0U},
        progpu_native_path_segment{
            {12.0F, 0.0F}, {6.0F, 12.0F}, {}, {},
            PROGPU_NATIVE_PATH_SEGMENT_LINE, 0U, 0U, 0U},
        progpu_native_path_segment{
            {6.0F, 12.0F}, {0.0F, 0.0F}, {}, {},
            PROGPU_NATIVE_PATH_SEGMENT_LINE, 0U, 0U, 0U}};
    auto first = progpu_native_scene_path_fill{};
    first.segment_count = segments.size();
    first.min_x = 0.0F;
    first.min_y = 0.0F;
    first.max_x = 12.0F;
    first.max_y = 12.0F;
    first.color = {1.0F, 1.0F, 1.0F, 1.0F};
    first.transform = semantic_scene_builder::identity_transform();
    first.fill_rule = PROGPU_NATIVE_FILL_RULE_NON_ZERO;
    first.sample_grid = 4U;
    auto second = first;
    second.transform.m31 = 18.0F;
    const std::array paths{first, second};

    semantic_scene_builder builder(702U, 1U);
    if (!builder.reserve(1U, 1U, 1024U) ||
        !builder.draw_paths(
            paths,
            segments,
            {},
            {0.0F, 0.0F, 30.0F, 12.0F})) {
        return false;
    }
    std::vector<std::byte> stream;
    if (!builder.build(stream)) {
        return false;
    }
    const auto validated = scene::validate(stream.data(), stream.size());
    if (validated.status != PROGPU_NATIVE_STATUS_SUCCESS ||
        validated.header.resource_count != 1U || validated.draw_count != 1U) {
        return false;
    }
    const auto resource = read<progpu_native_scene_resource>(
        stream, validated.header.resource_offset);
    const auto stored_first = read<progpu_native_scene_path_fill>(
        stream, resource.payload_offset);
    const auto stored_second = read<progpu_native_scene_path_fill>(
        stream, resource.payload_offset + sizeof(stored_first));
    return resource.kind == PROGPU_NATIVE_SCENE_RESOURCE_PATH_BATCH &&
        resource.payload_size == sizeof(paths) &&
        resource.auxiliary_size == sizeof(segments) &&
        stored_first.segment_offset == 0U &&
        stored_second.segment_offset == 0U &&
        stored_first.segment_count == segments.size() &&
        stored_second.segment_count == segments.size();
}

bool semantic_scene_builder_records_general_brushes() {
    semantic_scene_builder builder(710U, 2U);
    progpu_native_scene_brush linear{};
    linear.type = PROGPU_NATIVE_SCENE_BRUSH_LINEAR_GRADIENT;
    linear.opacity = 0.8F;
    linear.start_point = {0.0F, 0.0F};
    linear.end_point = {100.0F, 0.0F};
    linear.stop_count = 3U;
    linear.coordinate_transform0[0] = 1.0F;
    linear.coordinate_transform1[1] = 1.0F;
    const std::array stops{
        progpu_native_scene_gradient_stop{
            {1.0F, 0.0F, 0.0F, 1.0F}, 0.0F, 0U, 0U, 0U},
        progpu_native_scene_gradient_stop{
            {0.0F, 1.0F, 0.0F, 0.75F}, 0.4F, 0U, 0U, 0U},
        progpu_native_scene_gradient_stop{
            {0.0F, 0.0F, 1.0F, 1.0F}, 1.0F, 0U, 0U, 0U}};
    std::uint32_t brush = PROGPU_NATIVE_SCENE_NO_INDEX;
    if (!builder.add_brush(linear, stops, brush) || brush != 0U) {
        return false;
    }
    std::uint32_t solid = PROGPU_NATIVE_SCENE_NO_INDEX;
    if (!builder.add_solid_brush(
            {1.0F, 0.0F, 0.0F, 1.0F}, 0.8F, solid) ||
        solid == brush) {
        return false;
    }
    const progpu_native_analytic_primitive primitive{
        PROGPU_NATIVE_PRIMITIVE_RECTANGLE,
        0U,
        0.0F,
        0.0F,
        100.0F,
        20.0F,
        0.0F,
        0.0F,
        {1.0F, 1.0F, 1.0F, 1.0F},
        semantic_scene_builder::identity_transform()};
    if (!builder.draw_analytic(
            std::span<const progpu_native_analytic_primitive>(&primitive, 1U),
            std::span<const std::uint32_t>(&brush, 1U),
            {0.0F, 0.0F, 100.0F, 20.0F})) {
        return false;
    }
    std::vector<std::byte> stream;
    if (!builder.build(stream)) {
        return false;
    }
    const auto validated = scene::validate(stream.data(), stream.size());
    if (validated.status != PROGPU_NATIVE_STATUS_SUCCESS ||
        validated.header.resource_count != 2U) {
        return false;
    }
    const auto resource = read<progpu_native_scene_resource>(
        stream, validated.header.resource_offset);
    const auto stored_linear = read<progpu_native_scene_brush>(
        stream, resource.payload_offset);
    const auto stored_stop = read<progpu_native_scene_gradient_stop>(
        stream, resource.auxiliary_offset + sizeof(stops[0U]));
    if (resource.kind != PROGPU_NATIVE_SCENE_RESOURCE_BRUSH_TABLE ||
        resource.payload_size != 2U * sizeof(progpu_native_scene_brush) ||
        resource.auxiliary_size != stops.size() * sizeof(stops[0U]) ||
        stored_linear.stop_offset != 0U ||
        stored_stop.offset != 0.4F || stored_stop.color.g != 1.0F) {
        return false;
    }

    semantic_scene_builder invalid(711U, 1U);
    auto unsorted = stops;
    unsorted[1U].offset = -1.0F;
    unsorted[2U].offset = -2.0F;
    return !invalid.add_brush(linear, unsorted, brush) &&
        invalid.last_error() == scene_build_error::invalid_argument;
}

bool semantic_scene_builder_records_native_svg_layers() {
    constexpr std::string_view xml =
        "<svg><defs><linearGradient id='g'><stop offset='0' "
        "stop-color='red'/><stop offset='1' stop-color='blue'/>"
        "</linearGradient></defs><g id='glyph3' transform='translate(2 3)'>"
        "<path d='M0 0L8 0L0 8Z' fill='url(#g)'/>"
        "<rect x='10' y='0' width='5' height='6' fill='#0f0'/></g></svg>";
    text::svg_glyph_requirements requirements{};
    if (!text::try_get_svg_glyph_requirements(
            xml, 3U, 1000U, requirements) ||
        requirements.layer_count != 2U ||
        requirements.brush_count != 2U) {
        return false;
    }
    std::vector<text::svg_glyph_layer> layers(requirements.layer_count);
    std::vector<progpu_native_path_segment> segments(
        requirements.segment_count);
    std::vector<progpu_native_scene_brush> brushes(
        requirements.brush_count);
    std::vector<progpu_native_scene_gradient_stop> stops(
        requirements.gradient_stop_count);
    if (!text::try_decode_svg_glyph(
            xml, 3U, 1000U, layers, segments, brushes, stops,
            requirements)) {
        return false;
    }

    semantic_scene_builder builder(712U, 1U);
    std::vector<std::uint32_t> registered;
    registered.reserve(brushes.size());
    for (const auto& brush : brushes) {
        if (brush.stop_offset > stops.size() ||
            brush.stop_count > stops.size() - brush.stop_offset) {
            return false;
        }
        std::uint32_t index = PROGPU_NATIVE_SCENE_NO_INDEX;
        if (!builder.add_brush(
                brush,
                std::span<const progpu_native_scene_gradient_stop>{stops}
                    .subspan(brush.stop_offset, brush.stop_count),
                index)) {
            return false;
        }
        registered.push_back(index);
    }

    std::vector<progpu_native_scene_path_fill> paths;
    std::vector<std::uint32_t> path_brushes;
    paths.reserve(layers.size());
    path_brushes.reserve(layers.size());
    for (const auto& layer : layers) {
        if (layer.brush_index >= registered.size()) {
            return false;
        }
        progpu_native_scene_path_fill path{};
        path.segment_offset = layer.segment_offset;
        path.segment_count = layer.segment_count;
        path.min_x = layer.minimum_x;
        path.min_y = layer.minimum_y;
        path.max_x = layer.maximum_x;
        path.max_y = layer.maximum_y;
        path.color = {1.0F, 1.0F, 1.0F, 1.0F};
        path.transform = layer.transform;
        path.fill_rule = layer.fill_rule;
        path.sample_grid = 8U;
        paths.push_back(path);
        path_brushes.push_back(registered[layer.brush_index]);
    }
    if (!builder.draw_paths(
            paths, segments, path_brushes, {0.0F, 0.0F, 20.0F, 12.0F})) {
        return false;
    }
    std::vector<std::byte> stream;
    if (!builder.build(stream)) {
        return false;
    }
    const auto validated = scene::validate(stream.data(), stream.size());
    return validated.status == PROGPU_NATIVE_STATUS_SUCCESS &&
        validated.header.command_count == 1U &&
        validated.header.resource_count == 2U;
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
    const progpu_native_scene_image_effect effect{
        {}, {}, {}, {}, {},
        {0.0F, 1.0F, 1.0F, 0.0F},
        {0.0F, 0.0F, 0.0F, 1.0F},
        {2.0F, 2.0F, 0.0F, 0.0F},
        {}, {}, {}, {}, {}, {}, {}, {}, {}, {},
        sizeof(progpu_native_scene_image_effect), 0U, 0U, 0U};
    image.flags = PROGPU_NATIVE_SCENE_IMAGE_EFFECT;
    if (!builder.draw_image(
            image_index,
            image,
            {8.0F, 10.0F, 32.0F, 32.0F},
            PROGPU_NATIVE_SCENE_NO_INDEX,
            nullptr,
            nullptr,
            &effect)) {
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
        first_command.payload_size != sizeof(progpu_native_scene_image_draw) +
            sizeof(progpu_native_scene_image_effect) ||
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

bool semantic_scene_builder_records_image_patch_batches() {
    semantic_scene_builder builder(714U, 1U);
    std::uint32_t image_index = PROGPU_NATIVE_SCENE_NO_INDEX;
    if (!builder.add_external_image(64U, 32U, image_index)) {
        return false;
    }
    progpu_native_scene_image_draw image{};
    image.flags = PROGPU_NATIVE_SCENE_IMAGE_SOURCE_PREMULTIPLIED |
        PROGPU_NATIVE_SCENE_IMAGE_SNAP_TO_PIXELS;
    image.image_width = 64U;
    image.image_height = 32U;
    image.row_bytes = 256U;
    image.sampling = PROGPU_NATIVE_IMAGE_SAMPLING_LINEAR;
    image.source_rect = {0.0F, 0.0F, 64.0F, 32.0F};
    image.destination_rect = {1.0F, 1.0F, 1.0F, 1.0F};
    image.transform = semantic_scene_builder::identity_transform();
    image.opacity = 0.75F;
    std::array<progpu_native_scene_image_patch, 3U> patches{};
    for (auto& patch : patches) {
        patch.struct_size = sizeof(patch);
        patch.source_rect = {0.0F, 0.0F, 16.0F, 16.0F};
        patch.destination_rect = {4.0F, 6.0F, 32.0F, 24.0F};
        patch.transform = semantic_scene_builder::identity_transform();
    }
    patches[0].kind = PROGPU_NATIVE_SCENE_IMAGE_PATCH_TEXTURE;
    patches[1].kind = PROGPU_NATIVE_SCENE_IMAGE_PATCH_FIXED_COLOR;
    patches[1].source_rect = {};
    patches[1].color[0] = 1.0F;
    patches[1].color[3] = 0.5F;
    patches[2].kind = PROGPU_NATIVE_SCENE_IMAGE_PATCH_ATLAS_COLOR;
    patches[2].color_blend_mode = 24U;
    patches[2].transform = {1.0F, 0.0F, 0.0F, 1.0F, 8.0F, 3.0F};
    patches[2].color[0] = 0.25F;
    patches[2].color[1] = 0.5F;
    patches[2].color[2] = 0.75F;
    patches[2].color[3] = 1.0F;
    if (!builder.draw_image_patches(
            image_index,
            image,
            patches,
            {4.0F, 6.0F, 72.0F, 48.0F})) {
        return false;
    }

    std::vector<std::byte> stream;
    scene_build_metrics metrics{};
    if (!builder.build(stream, &metrics) || metrics.command_count != 1U ||
        metrics.resource_count != 1U) {
        return false;
    }
    const auto validation = scene::validate(stream.data(), stream.size());
    if (validation.status != PROGPU_NATIVE_STATUS_SUCCESS ||
        validation.draw_count != 1U) {
        return false;
    }
    const auto command = read<progpu_native_scene_command>(
        stream, validation.header.command_offset);
    const auto retained_image = read<progpu_native_scene_image_draw>(
        stream, command.payload_offset);
    const auto batch = read<progpu_native_scene_image_patch_batch>(
        stream, command.payload_offset + sizeof(retained_image));
    const auto atlas_patch = read<progpu_native_scene_image_patch>(
        stream,
        command.payload_offset + sizeof(retained_image) + sizeof(batch) +
            2U * sizeof(progpu_native_scene_image_patch));
    if (command.payload_size != sizeof(retained_image) + sizeof(batch) +
            sizeof(patches) ||
        (retained_image.flags & PROGPU_NATIVE_SCENE_IMAGE_PATCH_BATCH) == 0U ||
        batch.struct_size != sizeof(batch) || batch.patch_count != 3U ||
        atlas_patch.kind != PROGPU_NATIVE_SCENE_IMAGE_PATCH_ATLAS_COLOR ||
        atlas_patch.color_blend_mode != 24U ||
        atlas_patch.transform.m31 != 8.0F) {
        return false;
    }

    semantic_scene_builder invalid(715U, 1U);
    if (!invalid.add_external_image(64U, 32U, image_index)) {
        return false;
    }
    image.flags |= PROGPU_NATIVE_SCENE_IMAGE_PATCH_BATCH;
    return !invalid.draw_image(
            image_index, image, {4.0F, 6.0F, 72.0F, 48.0F}) &&
        invalid.last_error() == scene_build_error::invalid_argument;
}

bool semantic_scene_builder_serializes_external_images_pointer_free() {
    semantic_scene_builder builder(713U, 4U);
    std::uint32_t image_index = PROGPU_NATIVE_SCENE_NO_INDEX;
    progpu_native_scene_image_draw image{};
    image.image_width = 64U;
    image.image_height = 32U;
    image.row_bytes = 256U;
    image.sampling = PROGPU_NATIVE_IMAGE_SAMPLING_LINEAR;
    image.source_rect = {0.0F, 0.0F, 64.0F, 32.0F};
    image.destination_rect = {4.0F, 6.0F, 128.0F, 64.0F};
    image.transform = semantic_scene_builder::identity_transform();
    image.opacity = 1.0F;
    std::vector<std::byte> stream;
    if (!builder.add_external_image(64U, 32U, image_index) ||
        !builder.draw_image(
            image_index, image, {4.0F, 6.0F, 128.0F, 64.0F}) ||
        !builder.build(stream)) {
        return false;
    }
    const auto validation = scene::validate(stream.data(), stream.size());
    if (validation.status != PROGPU_NATIVE_STATUS_SUCCESS) {
        return false;
    }
    const auto resource = read<progpu_native_scene_resource>(
        stream, validation.header.resource_offset);
    return resource.kind == PROGPU_NATIVE_SCENE_RESOURCE_IMAGE &&
        resource.flags == (PROGPU_NATIVE_SCENE_RECORD_REQUIRED |
            PROGPU_NATIVE_SCENE_EXTERNAL_IMAGE) &&
        resource.payload_size == 0U && resource.auxiliary_size == 0U;
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
    if (!(glyph_record.kind == PROGPU_NATIVE_SCENE_RESOURCE_GLYPH_RUN &&
        glyph_record.payload_size == sizeof(outline) &&
        glyph_record.auxiliary_size == sizeof(segments) &&
        style_record.kind ==
            PROGPU_NATIVE_SCENE_RESOURCE_TEXT_STYLE_TABLE &&
        style_record.payload_size == sizeof(style) &&
        (command.flags & PROGPU_NATIVE_SCENE_GLYPH_STYLED) != 0U &&
        command.payload_size == sizeof(progpu_native_scene_glyph_draw) +
            sizeof(glyphs))) {
        return false;
    }

    const auto original_hashes = semantic::compute_content_hashes(
        stream.data(), validated.header);
    auto style_changed = stream;
    auto changed_style = style_record;
    ++changed_style.generation;
    std::memcpy(
        style_changed.data() + validated.header.resource_offset +
            validated.header.resource_stride,
        &changed_style,
        sizeof(changed_style));
    const auto changed_hashes = semantic::compute_content_hashes(
        style_changed.data(), validated.header);
    return changed_hashes.text_style != original_hashes.text_style &&
        changed_hashes.glyph != original_hashes.glyph;
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
    if (!builder.reserve(3U, 7U, 4096U)) {
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
    std::uint32_t vector_mask = PROGPU_NATIVE_SCENE_NO_INDEX;
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
    const std::array vector_segments{
        progpu_native_path_segment{
            {4.0F, 4.0F}, {52.0F, 8.0F}, {}, {},
            PROGPU_NATIVE_PATH_SEGMENT_LINE, 0U, 0U, 0U},
        progpu_native_path_segment{
            {52.0F, 8.0F}, {28.0F, 48.0F}, {}, {},
            PROGPU_NATIVE_PATH_SEGMENT_LINE, 0U, 0U, 0U},
        progpu_native_path_segment{
            {28.0F, 48.0F}, {4.0F, 4.0F}, {}, {},
            PROGPU_NATIVE_PATH_SEGMENT_LINE, 0U, 0U, 0U}};
    const progpu_native_scene_clip_path vector_path{
        0U,
        vector_segments.size(),
        0U,
        0U,
        4.0F,
        4.0F,
        52.0F,
        48.0F,
        identity,
        PROGPU_NATIVE_FILL_RULE_NON_ZERO,
        4U,
        PROGPU_NATIVE_CLIP_INTERSECT,
        0U};
    if (!builder.add_coverage_mask(
            coverage,
            coverage_bytes,
            coverage_mask) ||
        !builder.add_analytic_mask_chain(analytic_masks, chain_mask) ||
        !builder.add_vector_clip_mask(
            std::span<const progpu_native_scene_clip_path>(&vector_path, 1U),
            vector_segments,
            1.0F,
            vector_mask) ||
        rounded_mask == coverage_mask || coverage_mask == chain_mask ||
        chain_mask == vector_mask) {
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
        metrics.resource_count != 7U ||
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
    const auto vector_record = resource_at(vector_mask);
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
        vector_record.kind == PROGPU_NATIVE_SCENE_RESOURCE_LAYER_MASK &&
        vector_record.payload_size ==
            sizeof(progpu_native_scene_layer_vector_mask) &&
        vector_record.auxiliary_size ==
            sizeof(vector_path) + sizeof(vector_segments) &&
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
    mesh.vertex_count = static_cast<std::uint32_t>(vertices.size());
    mesh.index_count = static_cast<std::uint32_t>(indices.size());
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
    if (!(first_hashes.brush == second_hashes.brush &&
        first_hashes.text_style == second_hashes.text_style &&
        first_hashes.analytic == second_hashes.analytic &&
        first_hashes.path == second_hashes.path &&
        first_hashes.glyph == second_hashes.glyph &&
        first_hashes.image != second_hashes.image)) {
        return false;
    }

    auto brush_changed = first;
    const auto brush_header = read<progpu_native_scene_header>(
        brush_changed, 0U);
    for (std::uint32_t index = 0U;
         index < brush_header.resource_count;
         ++index) {
        const auto offset = static_cast<std::uint32_t>(
            brush_header.resource_offset +
            index * brush_header.resource_stride);
        auto resource = read<progpu_native_scene_resource>(
            brush_changed, offset);
        if (resource.kind != PROGPU_NATIVE_SCENE_RESOURCE_BRUSH_TABLE) {
            continue;
        }
        ++resource.generation;
        std::memcpy(
            brush_changed.data() + offset,
            &resource,
            sizeof(resource));
    }
    const auto brush_hashes = semantic::compute_content_hashes(
        brush_changed.data(), brush_header);
    return brush_hashes.brush != first_hashes.brush &&
        brush_hashes.analytic != first_hashes.analytic &&
        brush_hashes.glyph == first_hashes.glyph;
}

bool semantic_scene_builder_records_retained_hit_test_index() {
    semantic_scene_builder builder(711U, 1U);
    progpu_native_hit_test_primitive primitive{};
    primitive.bounds_min = {0.0F, 0.0F};
    primitive.bounds_max = {20.0F, 10.0F};
    primitive.data0 = {0.0F, 0.0F, 20.0F, 10.0F};
    primitive.inverse_transform0 = {1.0F, 0.0F, 0.0F, 0.0F};
    primitive.inverse_transform1 = {0.0F, 1.0F, 0.0F, 0.0F};
    primitive.kind = PROGPU_NATIVE_HIT_TEST_RECTANGLE_FILL;
    primitive.flags = PROGPU_NATIVE_HIT_TEST_VISIBLE |
        PROGPU_NATIVE_HIT_TEST_VISIBLE_TO_INPUT;
    primitive.id = 42;
    const progpu_native_hit_test_node node{
        {0.0F, 0.0F},
        {20.0F, 10.0F},
        0U,
        0U,
        0U,
        1U};
    constexpr std::uint32_t primitive_index = 0U;
    std::uint32_t resource_index = PROGPU_NATIVE_SCENE_NO_INDEX;
    if (!builder.add_hit_test_index(
            std::span<const progpu_native_hit_test_primitive>(
                &primitive, 1U),
            std::span<const progpu_native_hit_test_node>(&node, 1U),
            std::span<const std::uint32_t>(&primitive_index, 1U),
            {},
            resource_index) ||
        resource_index != 0U) {
        return false;
    }
    std::vector<std::byte> stream;
    if (!builder.build(stream)) {
        return false;
    }
    const auto validated = scene::validate(stream.data(), stream.size());
    if (validated.status != PROGPU_NATIVE_STATUS_SUCCESS) {
        return false;
    }
    const auto resource = read<progpu_native_scene_resource>(
        stream, validated.header.resource_offset);
    auto page = read<progpu_native_scene_hit_test_index>(
        stream, resource.payload_offset);
    const auto hashes = semantic::compute_content_hashes(
        stream.data(), validated.header);
    if (resource.kind != PROGPU_NATIVE_SCENE_RESOURCE_HIT_TEST_INDEX ||
        page.struct_size != sizeof(page) || page.primitive_count != 1U ||
        page.node_count != 1U || page.primitive_index_count != 1U ||
        hashes.hit_test == 0U) {
        return false;
    }

    auto malformed = stream;
    page.node_count = 0U;
    std::memcpy(
        malformed.data() + resource.payload_offset,
        &page,
        sizeof(page));
    return scene::validate(malformed.data(), malformed.size()).status ==
        PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
}

} // namespace progpu::native::tests
