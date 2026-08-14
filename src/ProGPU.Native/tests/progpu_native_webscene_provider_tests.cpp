#include "progpu_native_dawn.h"
#include "progpu_native_semantic_backdrop_scene.hpp"
#include "progpu_native_semantic_color_glyph_scene.hpp"
#include "progpu_native_semantic_coverage_mask_scene.hpp"
#include "progpu_native_webscene_advanced_blend_fixture.hpp"
#include "progpu_native_webscene_semantic_effect_fixture.hpp"
#include "webscene_gpu_provider.h"

#include <webgpu.h>

#include <IOSurface/IOSurface.h>
#include <dlfcn.h>

#include <chrono>
#include <array>
#include <condition_variable>
#include <cstddef>
#include <cstdint>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <iterator>
#include <limits>
#include <mutex>
#include <type_traits>
#include <vector>

namespace {

using progpu::native::tests::
    create_semantic_advanced_blend_scene_stream;
using progpu::native::tests::create_semantic_backdrop_scene_stream;
using progpu::native::tests::create_semantic_color_glyph_scene_stream;
using progpu::native::tests::create_semantic_coverage_mask_scene_stream;
using progpu::native::tests::
    create_semantic_masked_effect_layer_scene_stream;
using progpu::native::tests::create_semantic_root_effect_layer_scene_stream;
using progpu::native::tests::verify_semantic_advanced_blend_scene;
using progpu::native::tests::verify_semantic_masked_effect_layer_scene;

[[noreturn]] void fail(const char* message) {
    std::fprintf(stderr, "ProGPU WebScene provider integration failed: %s\n",
        message);
    std::abort();
}

void require(bool condition, const char* message) {
    if (!condition) {
        fail(message);
    }
}

using semantic_scene_stream = std::array<std::byte, 200U>;

semantic_scene_stream create_semantic_scene_stream(
    std::uint64_t scene_generation,
    std::uint64_t resource_generation) {
    semantic_scene_stream stream{};
    progpu_native_scene_header header{};
    header.struct_size = sizeof(header);
    header.magic = PROGPU_NATIVE_SCENE_STREAM_MAGIC;
    header.stream_version = PROGPU_NATIVE_SCENE_STREAM_VERSION;
    header.endian_marker = PROGPU_NATIVE_SCENE_STREAM_ENDIAN_MARKER;
    header.total_size = static_cast<std::uint32_t>(stream.size());
    header.scene_id = 91U;
    header.generation = scene_generation;
    header.command_offset = 80U;
    header.command_count = 1U;
    header.command_stride = sizeof(progpu_native_scene_command);
    header.resource_offset = 144U;
    header.resource_count = 1U;
    header.resource_stride = sizeof(progpu_native_scene_resource);
    header.arena_offset = 192U;
    header.arena_size = 8U;
    std::memcpy(stream.data(), &header, sizeof(header));

    progpu_native_scene_command command{};
    command.struct_size = sizeof(command);
    command.kind = PROGPU_NATIVE_SCENE_COMMAND_DRAW_ANALYTIC;
    command.flags = PROGPU_NATIVE_SCENE_RECORD_REQUIRED;
    command.command_id = 11U;
    command.state_index = PROGPU_NATIVE_SCENE_NO_INDEX;
    command.resource_index = 0U;
    command.bounds_width = 64.0F;
    command.bounds_height = 48.0F;
    std::memcpy(stream.data() + header.command_offset,
        &command, sizeof(command));

    progpu_native_scene_resource resource{};
    resource.struct_size = sizeof(resource);
    resource.kind = PROGPU_NATIVE_SCENE_RESOURCE_ANALYTIC_BATCH;
    resource.flags = PROGPU_NATIVE_SCENE_RECORD_REQUIRED;
    resource.resource_id = 21U;
    resource.generation = resource_generation;
    resource.payload_offset = header.arena_offset;
    resource.payload_size = header.arena_size;
    std::memcpy(stream.data() + header.resource_offset,
        &resource, sizeof(resource));
    stream[header.arena_offset] = std::byte{0x5a};
    return stream;
}

template<typename T>
std::uint32_t append_scene_payload(
    std::vector<std::byte>& stream,
    const T* values,
    std::size_t count) {
    const std::size_t aligned_size = (stream.size() + 7U) & ~7U;
    stream.resize(aligned_size);
    const std::uint32_t offset = static_cast<std::uint32_t>(stream.size());
    const std::size_t byte_count = sizeof(T) * count;
    const auto* source = reinterpret_cast<const std::byte*>(values);
    stream.insert(stream.end(), source, source + byte_count);
    return offset;
}

std::vector<std::byte> create_renderable_semantic_scene_stream(
    std::uint64_t generation) {
    constexpr std::uint32_t command_count = 12U;
    constexpr std::uint32_t resource_count = 12U;
    constexpr std::uint32_t command_offset =
        sizeof(progpu_native_scene_header);
    constexpr std::uint32_t resource_offset = command_offset +
        command_count * sizeof(progpu_native_scene_command);
    constexpr std::uint32_t arena_offset = resource_offset +
        resource_count * sizeof(progpu_native_scene_resource);
    std::vector<std::byte> stream(arena_offset);
    const progpu_native_affine_2d identity{
        1.0F, 0.0F, 0.0F, 1.0F, 0.0F, 0.0F};

    const progpu_native_analytic_primitive analytic{
        PROGPU_NATIVE_PRIMITIVE_RECTANGLE,
        0U,
        4.0F,
        4.0F,
        12.0F,
        12.0F,
        0.0F,
        0.0F,
        {1.0F, 0.0F, 0.0F, 1.0F},
        identity};
    const std::uint32_t analytic_offset = append_scene_payload(
        stream, &analytic, 1U);
    const progpu_native_analytic_primitive second_analytic{
        PROGPU_NATIVE_PRIMITIVE_RECTANGLE,
        0U,
        4.0F,
        4.0F,
        12.0F,
        12.0F,
        0.0F,
        0.0F,
        {0.0F, 1.0F, 1.0F, 1.0F},
        identity};
    const std::uint32_t second_analytic_offset = append_scene_payload(
        stream, &second_analytic, 1U);
    const progpu_native_geometry_primitive geometry{
        PROGPU_NATIVE_GEOMETRY_LINE,
        0U,
        {4.0F, 42.0F},
        {60.0F, 42.0F},
        {},
        {},
        3.0F,
        0.0F,
        {0.15F, 0.45F, 1.0F, 1.0F},
        identity};
    const std::uint32_t geometry_offset = append_scene_payload(
        stream, &geometry, 1U);

    const progpu_native_path_segment path_segments[]{
        {{0.0F, 0.0F}, {12.0F, 0.0F}, {}, {},
            PROGPU_NATIVE_PATH_SEGMENT_LINE, 0U, 0U, 0U},
        {{12.0F, 0.0F}, {12.0F, 12.0F}, {}, {},
            PROGPU_NATIVE_PATH_SEGMENT_LINE, 0U, 0U, 0U},
        {{12.0F, 12.0F}, {0.0F, 12.0F}, {}, {},
            PROGPU_NATIVE_PATH_SEGMENT_LINE, 0U, 0U, 0U},
        {{0.0F, 12.0F}, {0.0F, 0.0F}, {}, {},
            PROGPU_NATIVE_PATH_SEGMENT_LINE, 0U, 0U, 0U}
    };
    const progpu_native_scene_path_fill path{
        0U,
        std::size(path_segments),
        0.0F,
        0.0F,
        12.0F,
        12.0F,
        {0.0F, 1.0F, 0.0F, 1.0F},
        {1.0F, 0.0F, 0.0F, 1.0F, 20.0F, 4.0F},
        PROGPU_NATIVE_FILL_RULE_NON_ZERO,
        4U};
    const std::uint32_t path_offset = append_scene_payload(
        stream, &path, 1U);
    const std::uint32_t path_segment_offset = append_scene_payload(
        stream, path_segments, std::size(path_segments));
    progpu_native_scene_path_fill second_path = path;
    second_path.color = {1.0F, 0.0F, 1.0F, 1.0F};
    second_path.transform.m31 = 20.0F;
    second_path.transform.m32 = 4.0F;
    const std::uint32_t second_path_offset = append_scene_payload(
        stream, &second_path, 1U);
    const std::uint32_t second_path_segment_offset = append_scene_payload(
        stream, path_segments, std::size(path_segments));

    const progpu_native_scene_glyph_outline outline{
        0U,
        std::size(path_segments),
        0.0F,
        0.0F,
        12.0F,
        12.0F,
        1.0F,
        0.0F};
    const std::uint32_t outline_offset = append_scene_payload(
        stream, &outline, 1U);
    const std::uint32_t outline_segment_offset = append_scene_payload(
        stream, path_segments, std::size(path_segments));
    const progpu_native_positioned_glyph glyph{
        0U,
        0U,
        {36.0F, 18.0F},
        {1.0F, 0.0F},
        {0.0F, 1.0F},
        {0.0F, 0.0F, 1.0F, 1.0F},
        1.0F,
        0.0F,
        0.0F,
        0.0F};
    const progpu_native_scene_glyph_draw glyph_draw{
        sizeof(progpu_native_scene_glyph_draw), 10U, 0U, 1U, 0U, 0U};
    const std::uint32_t glyph_offset = append_scene_payload(
        stream, &glyph_draw, 1U);
    append_scene_payload(stream, &glyph, 1U);
    const std::uint32_t second_outline_offset = append_scene_payload(
        stream, &outline, 1U);
    const std::uint32_t second_outline_segment_offset = append_scene_payload(
        stream, path_segments, std::size(path_segments));
    progpu_native_positioned_glyph second_glyph = glyph;
    second_glyph.position.y = 18.0F;
    second_glyph.color = {1.0F, 0.5F, 0.0F, 1.0F};
    const std::uint32_t second_glyph_offset = append_scene_payload(
        stream, &glyph_draw, 1U);
    append_scene_payload(stream, &second_glyph, 1U);
    const progpu_native_scene_text_style text_style{
        {1.0F, 0.15F, 0.05F, 0.9F},
        PROGPU_NATIVE_SCENE_TEXT_GRAYSCALE,
        0U,
        0U,
        0U};
    const std::uint32_t text_style_offset = append_scene_payload(
        stream, &text_style, 1U);

    const std::array<std::uint8_t, 16U> image_pixels{
        255U, 255U, 0U, 255U, 255U, 255U, 0U, 255U,
        255U, 255U, 0U, 255U, 255U, 255U, 0U, 255U};
    const std::uint32_t image_offset = append_scene_payload(
        stream, image_pixels.data(), image_pixels.size());
    const progpu_native_scene_image_draw image{
        sizeof(progpu_native_scene_image_draw),
        0U,
        2U,
        2U,
        8U,
        PROGPU_NATIVE_IMAGE_SAMPLING_NEAREST,
        {0.0F, 0.0F, 2.0F, 2.0F},
        {50.0F, 4.0F, 10.0F, 12.0F},
        identity,
        1.0F,
        0U};
    const std::uint32_t image_draw_offset = append_scene_payload(
        stream, &image, 1U);
    const std::array<std::uint8_t, 16U> second_image_pixels{
        255U, 0U, 0U, 255U, 0U, 255U, 0U, 255U,
        0U, 0U, 255U, 255U, 255U, 255U, 255U, 255U};
    const std::uint32_t second_image_offset = append_scene_payload(
        stream, second_image_pixels.data(), second_image_pixels.size());
    progpu_native_scene_image_draw second_image = image;
    second_image.flags = PROGPU_NATIVE_SCENE_IMAGE_COLOR_MATRIX;
    second_image.sampling = PROGPU_NATIVE_IMAGE_SAMPLING_CUBIC;
    second_image.destination_rect.y = 4.0F;
    const std::uint32_t second_image_draw_offset = append_scene_payload(
        stream, &second_image, 1U);
    const progpu_native_scene_image_sampling_options second_sampling{
        sizeof(progpu_native_scene_image_sampling_options),
        0U,
        1.0F / 3.0F,
        1.0F / 3.0F};
    append_scene_payload(stream, &second_sampling, 1U);
    const progpu_native_scene_image_color_matrix second_color_matrix{
        sizeof(progpu_native_scene_image_color_matrix),
        0U,
        {0.2126F, 0.7152F, 0.0722F, 0.0F},
        {0.2126F, 0.7152F, 0.0722F, 0.0F},
        {0.2126F, 0.7152F, 0.0722F, 0.0F},
        {0.0F, 0.0F, 0.0F, 1.0F},
        {0.0F, 0.0F, 0.0F, 0.0F},
        {0U, 0U}};
    append_scene_payload(stream, &second_color_matrix, 1U);
    const progpu_native_scene_state second_row_state{
        sizeof(progpu_native_scene_state),
        PROGPU_NATIVE_SCENE_STATE_CLIP_RECT,
        {1.0F, 0.0F, 0.0F, 1.0F, 0.0F, 20.0F},
        0.5F,
        0U,
        {8.0F, 20.0F, 48.0F, 16.0F},
        0U,
        0U};
    const std::uint32_t second_row_state_offset = append_scene_payload(
        stream, &second_row_state, 1U);
    const progpu_native_scene_state empty_clip_state{
        sizeof(progpu_native_scene_state),
        PROGPU_NATIVE_SCENE_STATE_CLIP_RECT,
        identity,
        1.0F,
        0U,
        {},
        0U,
        0U};
    const std::uint32_t empty_clip_state_offset = append_scene_payload(
        stream, &empty_clip_state, 1U);

    progpu_native_scene_header header{};
    header.struct_size = sizeof(header);
    header.magic = PROGPU_NATIVE_SCENE_STREAM_MAGIC;
    header.stream_version = PROGPU_NATIVE_SCENE_STREAM_VERSION;
    header.endian_marker = PROGPU_NATIVE_SCENE_STREAM_ENDIAN_MARKER;
    header.total_size = static_cast<std::uint32_t>(stream.size());
    header.scene_id = 91U;
    header.generation = generation;
    header.command_offset = command_offset;
    header.command_count = command_count;
    header.command_stride = sizeof(progpu_native_scene_command);
    header.resource_offset = resource_offset;
    header.resource_count = resource_count;
    header.resource_stride = sizeof(progpu_native_scene_resource);
    header.arena_offset = arena_offset;
    header.arena_size = header.total_size - arena_offset;
    std::memcpy(stream.data(), &header, sizeof(header));

    const progpu_native_scene_resource resources[]{
        {sizeof(progpu_native_scene_resource),
            PROGPU_NATIVE_SCENE_RESOURCE_ANALYTIC_BATCH,
            PROGPU_NATIVE_SCENE_RECORD_REQUIRED, 0U, 101U, 1U,
            analytic_offset, sizeof(analytic), 0U, 0U},
        {sizeof(progpu_native_scene_resource),
            PROGPU_NATIVE_SCENE_RESOURCE_PATH_BATCH,
            PROGPU_NATIVE_SCENE_RECORD_REQUIRED, 0U, 102U, 1U,
            path_offset, sizeof(path), path_segment_offset,
            sizeof(path_segments)},
        {sizeof(progpu_native_scene_resource),
            PROGPU_NATIVE_SCENE_RESOURCE_GLYPH_RUN,
            PROGPU_NATIVE_SCENE_RECORD_REQUIRED, 0U, 103U, 1U,
            outline_offset, sizeof(outline), outline_segment_offset,
            sizeof(path_segments)},
        {sizeof(progpu_native_scene_resource),
            PROGPU_NATIVE_SCENE_RESOURCE_IMAGE,
            PROGPU_NATIVE_SCENE_RECORD_REQUIRED, 0U, 104U, 1U,
            image_offset, image_pixels.size(), 0U, 0U},
        {sizeof(progpu_native_scene_resource),
            PROGPU_NATIVE_SCENE_RESOURCE_ANALYTIC_BATCH,
            PROGPU_NATIVE_SCENE_RECORD_REQUIRED, 0U, 105U, 1U,
            second_analytic_offset, sizeof(second_analytic), 0U, 0U},
        {sizeof(progpu_native_scene_resource),
            PROGPU_NATIVE_SCENE_RESOURCE_PATH_BATCH,
            PROGPU_NATIVE_SCENE_RECORD_REQUIRED, 0U, 106U, 1U,
            second_path_offset, sizeof(second_path),
            second_path_segment_offset, sizeof(path_segments)},
        {sizeof(progpu_native_scene_resource),
            PROGPU_NATIVE_SCENE_RESOURCE_GLYPH_RUN,
            PROGPU_NATIVE_SCENE_RECORD_REQUIRED, 0U, 107U, 1U,
            second_outline_offset, sizeof(outline),
            second_outline_segment_offset, sizeof(path_segments)},
        {sizeof(progpu_native_scene_resource),
            PROGPU_NATIVE_SCENE_RESOURCE_IMAGE,
            PROGPU_NATIVE_SCENE_RECORD_REQUIRED, 0U, 108U, 1U,
            second_image_offset, second_image_pixels.size(), 0U, 0U},
        {sizeof(progpu_native_scene_resource),
            PROGPU_NATIVE_SCENE_RESOURCE_STATE,
            PROGPU_NATIVE_SCENE_RECORD_REQUIRED, 0U, 109U, 1U,
            second_row_state_offset, sizeof(second_row_state), 0U, 0U},
        {sizeof(progpu_native_scene_resource),
            PROGPU_NATIVE_SCENE_RESOURCE_STATE,
            PROGPU_NATIVE_SCENE_RECORD_REQUIRED, 0U, 110U, 1U,
            empty_clip_state_offset, sizeof(empty_clip_state), 0U, 0U},
        {sizeof(progpu_native_scene_resource),
            PROGPU_NATIVE_SCENE_RESOURCE_TEXT_STYLE_TABLE,
            PROGPU_NATIVE_SCENE_RECORD_REQUIRED, 0U, 111U, 1U,
            text_style_offset, sizeof(text_style), 0U, 0U},
        {sizeof(progpu_native_scene_resource),
            PROGPU_NATIVE_SCENE_RESOURCE_GEOMETRY_BATCH,
            PROGPU_NATIVE_SCENE_RECORD_REQUIRED, 0U, 112U, 1U,
            geometry_offset, sizeof(geometry), 0U, 0U}
    };
    std::memcpy(
        stream.data() + resource_offset,
        resources,
        sizeof(resources));

    const progpu_native_scene_command commands[]{
        {sizeof(progpu_native_scene_command),
            PROGPU_NATIVE_SCENE_COMMAND_DRAW_ANALYTIC,
            PROGPU_NATIVE_SCENE_RECORD_REQUIRED, 0U, 201U,
            PROGPU_NATIVE_SCENE_NO_INDEX, 0U, 0U, 0U,
            4.0F, 4.0F, 12.0F, 12.0F, 0U, 0U},
        {sizeof(progpu_native_scene_command),
            PROGPU_NATIVE_SCENE_COMMAND_DRAW_PATH,
            PROGPU_NATIVE_SCENE_RECORD_REQUIRED, 0U, 202U,
            PROGPU_NATIVE_SCENE_NO_INDEX, 1U, 0U, 0U,
            20.0F, 4.0F, 12.0F, 12.0F, 0U, 0U},
        {sizeof(progpu_native_scene_command),
            PROGPU_NATIVE_SCENE_COMMAND_DRAW_GLYPH_RUN,
            PROGPU_NATIVE_SCENE_RECORD_REQUIRED |
                PROGPU_NATIVE_SCENE_GLYPH_STYLED, 0U, 203U,
            PROGPU_NATIVE_SCENE_NO_INDEX, 2U,
            glyph_offset, sizeof(glyph_draw) + sizeof(glyph),
            36.0F, 4.0F, 12.0F, 12.0F, 0U, 0U},
        {sizeof(progpu_native_scene_command),
            PROGPU_NATIVE_SCENE_COMMAND_DRAW_IMAGE,
            PROGPU_NATIVE_SCENE_RECORD_REQUIRED, 0U, 204U,
            PROGPU_NATIVE_SCENE_NO_INDEX, 3U,
            image_draw_offset, sizeof(image),
            50.0F, 4.0F, 10.0F, 12.0F, 0U, 0U},
        {sizeof(progpu_native_scene_command),
            PROGPU_NATIVE_SCENE_COMMAND_SAVE,
            PROGPU_NATIVE_SCENE_RECORD_REQUIRED, 0U, 205U,
            8U, PROGPU_NATIVE_SCENE_NO_INDEX, 0U, 0U,
            0.0F, 0.0F, 0.0F, 0.0F, 0U, 0U},
        {sizeof(progpu_native_scene_command),
            PROGPU_NATIVE_SCENE_COMMAND_DRAW_PATH,
            PROGPU_NATIVE_SCENE_RECORD_REQUIRED, 0U, 206U,
            PROGPU_NATIVE_SCENE_NO_INDEX, 5U, 0U, 0U,
            20.0F, 24.0F, 12.0F, 12.0F, 0U, 0U},
        {sizeof(progpu_native_scene_command),
            PROGPU_NATIVE_SCENE_COMMAND_DRAW_GLYPH_RUN,
            PROGPU_NATIVE_SCENE_RECORD_REQUIRED |
                PROGPU_NATIVE_SCENE_GLYPH_STYLED, 0U, 207U,
            PROGPU_NATIVE_SCENE_NO_INDEX, 6U,
            second_glyph_offset, sizeof(glyph_draw) + sizeof(second_glyph),
            36.0F, 24.0F, 12.0F, 12.0F, 0U, 0U},
        {sizeof(progpu_native_scene_command),
            PROGPU_NATIVE_SCENE_COMMAND_DRAW_IMAGE,
            PROGPU_NATIVE_SCENE_RECORD_REQUIRED, 0U, 208U,
            PROGPU_NATIVE_SCENE_NO_INDEX, 7U,
            second_image_draw_offset,
            sizeof(second_image) + sizeof(second_sampling) +
                sizeof(second_color_matrix),
            50.0F, 24.0F, 10.0F, 12.0F, 0U, 0U},
        {sizeof(progpu_native_scene_command),
            PROGPU_NATIVE_SCENE_COMMAND_DRAW_ANALYTIC,
            PROGPU_NATIVE_SCENE_RECORD_REQUIRED, 0U, 209U,
            PROGPU_NATIVE_SCENE_NO_INDEX, 4U, 0U, 0U,
            4.0F, 24.0F, 12.0F, 12.0F, 0U, 0U},
        {sizeof(progpu_native_scene_command),
            PROGPU_NATIVE_SCENE_COMMAND_DRAW_ANALYTIC,
            PROGPU_NATIVE_SCENE_RECORD_REQUIRED, 0U, 210U,
            9U, 0U, 0U, 0U,
            4.0F, 4.0F, 12.0F, 12.0F, 0U, 0U},
        {sizeof(progpu_native_scene_command),
            PROGPU_NATIVE_SCENE_COMMAND_RESTORE,
            PROGPU_NATIVE_SCENE_RECORD_REQUIRED, 0U, 211U,
            PROGPU_NATIVE_SCENE_NO_INDEX, PROGPU_NATIVE_SCENE_NO_INDEX,
            0U, 0U, 0.0F, 0.0F, 0.0F, 0.0F, 0U, 0U},
        {sizeof(progpu_native_scene_command),
            PROGPU_NATIVE_SCENE_COMMAND_DRAW_GEOMETRY,
            PROGPU_NATIVE_SCENE_RECORD_REQUIRED, 0U, 212U,
            PROGPU_NATIVE_SCENE_NO_INDEX, 11U, 0U, 0U,
            4.0F, 40.0F, 56.0F, 4.0F, 0U, 0U}
    };
    std::memcpy(
        stream.data() + command_offset,
        commands,
        sizeof(commands));
    return stream;
}

std::vector<std::byte> create_over_budget_semantic_scene_stream() {
    constexpr std::uint32_t command_count = 16U * 1024U + 1U;
    constexpr std::uint32_t command_offset =
        sizeof(progpu_native_scene_header);
    constexpr std::uint32_t resource_offset = command_offset +
        command_count * sizeof(progpu_native_scene_command);
    constexpr std::uint32_t arena_offset = resource_offset +
        sizeof(progpu_native_scene_resource);
    std::vector<std::byte> stream(arena_offset);
    const progpu_native_analytic_primitive analytic{
        PROGPU_NATIVE_PRIMITIVE_RECTANGLE,
        0U,
        4.0F,
        4.0F,
        12.0F,
        12.0F,
        0.0F,
        0.0F,
        {1.0F, 0.0F, 0.0F, 1.0F},
        {1.0F, 0.0F, 0.0F, 1.0F, 0.0F, 0.0F}};
    const std::uint32_t analytic_offset = append_scene_payload(
        stream, &analytic, 1U);

    progpu_native_scene_header header{};
    header.struct_size = sizeof(header);
    header.magic = PROGPU_NATIVE_SCENE_STREAM_MAGIC;
    header.stream_version = PROGPU_NATIVE_SCENE_STREAM_VERSION;
    header.endian_marker = PROGPU_NATIVE_SCENE_STREAM_ENDIAN_MARKER;
    header.total_size = static_cast<std::uint32_t>(stream.size());
    header.scene_id = 92U;
    header.generation = 1U;
    header.command_offset = command_offset;
    header.command_count = command_count;
    header.command_stride = sizeof(progpu_native_scene_command);
    header.resource_offset = resource_offset;
    header.resource_count = 1U;
    header.resource_stride = sizeof(progpu_native_scene_resource);
    header.arena_offset = arena_offset;
    header.arena_size = header.total_size - arena_offset;
    std::memcpy(stream.data(), &header, sizeof(header));

    const progpu_native_scene_resource resource{
        sizeof(progpu_native_scene_resource),
        PROGPU_NATIVE_SCENE_RESOURCE_ANALYTIC_BATCH,
        PROGPU_NATIVE_SCENE_RECORD_REQUIRED,
        0U,
        301U,
        1U,
        analytic_offset,
        sizeof(analytic),
        0U,
        0U};
    std::memcpy(
        stream.data() + resource_offset,
        &resource,
        sizeof(resource));

    for (std::uint32_t index = 0U; index < command_count; ++index) {
        const progpu_native_scene_command command{
            sizeof(progpu_native_scene_command),
            PROGPU_NATIVE_SCENE_COMMAND_DRAW_ANALYTIC,
            PROGPU_NATIVE_SCENE_RECORD_REQUIRED,
            0U,
            401U + index,
            PROGPU_NATIVE_SCENE_NO_INDEX,
            0U,
            0U,
            0U,
            4.0F,
            4.0F,
            12.0F,
            12.0F,
            0U,
            0U};
        std::memcpy(
            stream.data() + command_offset +
                index * sizeof(progpu_native_scene_command),
            &command,
            sizeof(command));
    }
    return stream;
}

std::vector<std::byte> create_semantic_layer_scene_stream() {
    constexpr std::uint32_t command_count = 2U;
    constexpr std::uint32_t command_offset =
        sizeof(progpu_native_scene_header);
    constexpr std::uint32_t resource_offset = command_offset +
        command_count * sizeof(progpu_native_scene_command);
    constexpr std::uint32_t arena_offset = resource_offset;
    std::vector<std::byte> stream(arena_offset);

    const progpu_native_scene_layer layer{
        sizeof(progpu_native_scene_layer),
        PROGPU_NATIVE_SCENE_LAYER_BACKDROP |
            PROGPU_NATIVE_SCENE_LAYER_FORCE_ISOLATION,
        {},
        0.5F,
        PROGPU_NATIVE_BLEND_OVERLAY,
        PROGPU_NATIVE_SCENE_NO_INDEX,
        PROGPU_NATIVE_SCENE_NO_INDEX,
        11U,
        13U,
        0U,
        0U};
    const std::uint32_t layer_offset = append_scene_payload(
        stream, &layer, 1U);

    progpu_native_scene_header header{};
    header.struct_size = sizeof(header);
    header.magic = PROGPU_NATIVE_SCENE_STREAM_MAGIC;
    header.stream_version = PROGPU_NATIVE_SCENE_STREAM_VERSION;
    header.endian_marker = PROGPU_NATIVE_SCENE_STREAM_ENDIAN_MARKER;
    header.total_size = static_cast<std::uint32_t>(stream.size());
    header.scene_id = 93U;
    header.generation = 1U;
    header.command_offset = command_offset;
    header.command_count = command_count;
    header.command_stride = sizeof(progpu_native_scene_command);
    header.resource_offset = resource_offset;
    header.resource_stride = sizeof(progpu_native_scene_resource);
    header.arena_offset = arena_offset;
    header.arena_size = header.total_size - arena_offset;
    std::memcpy(stream.data(), &header, sizeof(header));

    const progpu_native_scene_command commands[]{
        {sizeof(progpu_native_scene_command),
            PROGPU_NATIVE_SCENE_COMMAND_PUSH_LAYER,
            PROGPU_NATIVE_SCENE_RECORD_REQUIRED, 0U, 1U,
            PROGPU_NATIVE_SCENE_NO_INDEX,
            PROGPU_NATIVE_SCENE_NO_INDEX,
            layer_offset, sizeof(layer),
            0.0F, 0.0F, 0.0F, 0.0F, 0U, 0U},
        {sizeof(progpu_native_scene_command),
            PROGPU_NATIVE_SCENE_COMMAND_POP_LAYER,
            PROGPU_NATIVE_SCENE_RECORD_REQUIRED, 0U, 2U,
            PROGPU_NATIVE_SCENE_NO_INDEX,
            PROGPU_NATIVE_SCENE_NO_INDEX,
            0U, 0U, 0.0F, 0.0F, 0.0F, 0.0F, 0U, 0U}
    };
    std::memcpy(
        stream.data() + command_offset,
        commands,
        sizeof(commands));
    return stream;
}

std::vector<std::byte> create_semantic_opacity_layer_scene_stream(
    std::uint64_t generation = 1U,
    float glyph_raster_scale = 1.0F) {
    constexpr std::uint32_t command_count = 13U;
    constexpr std::uint32_t resource_count = 6U;
    constexpr std::uint32_t command_offset =
        sizeof(progpu_native_scene_header);
    constexpr std::uint32_t resource_offset = command_offset +
        command_count * sizeof(progpu_native_scene_command);
    constexpr std::uint32_t arena_offset = resource_offset +
        resource_count * sizeof(progpu_native_scene_resource);
    std::vector<std::byte> stream(arena_offset);

    constexpr progpu_native_affine_2d identity{
        1.0F, 0.0F, 0.0F, 1.0F, 0.0F, 0.0F};
    const progpu_native_analytic_primitive root_analytic{
        PROGPU_NATIVE_PRIMITIVE_RECTANGLE, 0U,
        4.0F, 4.0F, 12.0F, 12.0F, 0.0F, 0.0F,
        {1.0F, 0.0F, 0.0F, 1.0F}, identity};
    const progpu_native_analytic_primitive direct_analytic{
        PROGPU_NATIVE_PRIMITIVE_RECTANGLE, 0U,
            52.0F, 4.0F, 8.0F, 12.0F, 0.0F, 0.0F,
            {1.0F, 1.0F, 0.0F, 1.0F}, identity};
    const std::uint32_t root_analytic_offset = append_scene_payload(
        stream,
        &root_analytic,
        1U);
    const std::uint32_t direct_analytic_offset = append_scene_payload(
        stream,
        &direct_analytic,
        1U);

    const progpu_native_path_segment square_segments[]{
        {{0.0F, 0.0F}, {12.0F, 0.0F}, {}, {},
            PROGPU_NATIVE_PATH_SEGMENT_LINE, 0U, 0U, 0U},
        {{12.0F, 0.0F}, {12.0F, 12.0F}, {}, {},
            PROGPU_NATIVE_PATH_SEGMENT_LINE, 0U, 0U, 0U},
        {{12.0F, 12.0F}, {0.0F, 12.0F}, {}, {},
            PROGPU_NATIVE_PATH_SEGMENT_LINE, 0U, 0U, 0U},
        {{0.0F, 12.0F}, {0.0F, 0.0F}, {}, {},
            PROGPU_NATIVE_PATH_SEGMENT_LINE, 0U, 0U, 0U}
    };
    const progpu_native_scene_path_fill outer_path{
        0U,
        std::size(square_segments),
        0.0F,
        0.0F,
        12.0F,
        12.0F,
        {0.0F, 1.0F, 0.0F, 1.0F},
        {1.0F, 0.0F, 0.0F, 1.0F, 20.0F, 4.0F},
        PROGPU_NATIVE_FILL_RULE_NON_ZERO,
        4U};
    const std::uint32_t outer_path_offset = append_scene_payload(
        stream,
        &outer_path,
        1U);
    const std::uint32_t outer_path_segment_offset = append_scene_payload(
        stream,
        square_segments,
        std::size(square_segments));

    const progpu_native_scene_glyph_outline inner_outline{
        0U,
        std::size(square_segments),
        0.0F,
        0.0F,
        12.0F,
        12.0F,
        glyph_raster_scale,
        0.0F};
    const std::uint32_t inner_outline_offset = append_scene_payload(
        stream,
        &inner_outline,
        1U);
    const std::uint32_t inner_outline_segment_offset = append_scene_payload(
        stream,
        square_segments,
        std::size(square_segments));
    const progpu_native_positioned_glyph inner_glyph{
        0U,
        0U,
        {36.0F, 18.0F},
        {1.0F, 0.0F},
        {0.0F, 1.0F},
        {0.0F, 0.0F, 1.0F, 1.0F},
        1.0F,
        0.0F,
        0.0F,
        0.0F};
    const std::uint32_t inner_glyph_offset = append_scene_payload(
        stream,
        &inner_glyph,
        1U);

    const std::array<std::uint8_t, 16U> sequential_image_pixels{
        255U, 0U, 255U, 255U, 255U, 0U, 255U, 255U,
        255U, 0U, 255U, 255U, 255U, 0U, 255U, 255U};
    const std::uint32_t sequential_image_offset = append_scene_payload(
        stream,
        sequential_image_pixels.data(),
        sequential_image_pixels.size());
    const progpu_native_scene_image_draw sequential_image{
        sizeof(progpu_native_scene_image_draw),
        0U,
        2U,
        2U,
        8U,
        PROGPU_NATIVE_IMAGE_SAMPLING_NEAREST,
        {0.0F, 0.0F, 2.0F, 2.0F},
        {4.0F, 24.0F, 12.0F, 12.0F},
        identity,
        1.0F,
        0U};
    const std::uint32_t sequential_image_draw_offset = append_scene_payload(
        stream,
        &sequential_image,
        1U);
    const progpu_native_scene_state layer_state{
        sizeof(progpu_native_scene_state),
        0U,
        {1.0F, 0.0F, 0.0F, 1.0F, 0.0F, 20.0F},
        1.0F,
        0U,
        {},
        0U,
        0U};
    const std::uint32_t state_offset = append_scene_payload(
        stream,
        &layer_state,
        1U);
    const progpu_native_scene_layer outer_layer{
        sizeof(progpu_native_scene_layer),
        PROGPU_NATIVE_SCENE_LAYER_BOUNDS |
            PROGPU_NATIVE_SCENE_LAYER_FORCE_ISOLATION,
        {22.0F, 22.0F, 28.0F, 16.0F},
        0.5F,
        PROGPU_NATIVE_BLEND_SRC_OVER,
        PROGPU_NATIVE_SCENE_NO_INDEX,
        PROGPU_NATIVE_SCENE_NO_INDEX,
        21U,
        31U,
        0U,
        0U};
    const progpu_native_scene_layer inner_layer{
        sizeof(progpu_native_scene_layer),
        PROGPU_NATIVE_SCENE_LAYER_BOUNDS |
            PROGPU_NATIVE_SCENE_LAYER_FORCE_ISOLATION,
        {34.0F, 22.0F, 16.0F, 16.0F},
        0.5F,
        PROGPU_NATIVE_BLEND_SRC_OVER,
        PROGPU_NATIVE_SCENE_NO_INDEX,
        PROGPU_NATIVE_SCENE_NO_INDEX,
        22U,
        32U,
        0U,
        0U};
    const progpu_native_scene_layer sequential_layer{
        sizeof(progpu_native_scene_layer),
        PROGPU_NATIVE_SCENE_LAYER_BOUNDS |
            PROGPU_NATIVE_SCENE_LAYER_FORCE_ISOLATION,
        {2.0F, 22.0F, 16.0F, 16.0F},
        0.25F,
        PROGPU_NATIVE_BLEND_PLUS,
        PROGPU_NATIVE_SCENE_NO_INDEX,
        PROGPU_NATIVE_SCENE_NO_INDEX,
        23U,
        33U,
        0U,
        0U};
    const progpu_native_scene_layer direct_layer{
        sizeof(progpu_native_scene_layer),
        0U,
        {},
        1.0F,
        PROGPU_NATIVE_BLEND_SRC_OVER,
        PROGPU_NATIVE_SCENE_NO_INDEX,
        PROGPU_NATIVE_SCENE_NO_INDEX,
        24U,
        34U,
        0U,
        0U};
    const std::uint32_t outer_layer_offset = append_scene_payload(
        stream,
        &outer_layer,
        1U);
    const std::uint32_t inner_layer_offset = append_scene_payload(
        stream,
        &inner_layer,
        1U);
    const std::uint32_t sequential_layer_offset = append_scene_payload(
        stream,
        &sequential_layer,
        1U);
    const std::uint32_t direct_layer_offset = append_scene_payload(
        stream,
        &direct_layer,
        1U);

    progpu_native_scene_header header{};
    header.struct_size = sizeof(header);
    header.magic = PROGPU_NATIVE_SCENE_STREAM_MAGIC;
    header.stream_version = PROGPU_NATIVE_SCENE_STREAM_VERSION;
    header.endian_marker = PROGPU_NATIVE_SCENE_STREAM_ENDIAN_MARKER;
    header.total_size = static_cast<std::uint32_t>(stream.size());
    header.scene_id = 94U;
    header.generation = generation;
    header.command_offset = command_offset;
    header.command_count = command_count;
    header.command_stride = sizeof(progpu_native_scene_command);
    header.resource_offset = resource_offset;
    header.resource_count = resource_count;
    header.resource_stride = sizeof(progpu_native_scene_resource);
    header.arena_offset = arena_offset;
    header.arena_size = header.total_size - arena_offset;
    std::memcpy(stream.data(), &header, sizeof(header));

    std::array<progpu_native_scene_resource, resource_count> resources{};
    resources[0] = {
        sizeof(progpu_native_scene_resource),
        PROGPU_NATIVE_SCENE_RESOURCE_ANALYTIC_BATCH,
        PROGPU_NATIVE_SCENE_RECORD_REQUIRED,
        0U,
        501U,
        generation,
        root_analytic_offset,
        sizeof(root_analytic),
        0U,
        0U};
    resources[1] = {
        sizeof(progpu_native_scene_resource),
        PROGPU_NATIVE_SCENE_RESOURCE_PATH_BATCH,
        PROGPU_NATIVE_SCENE_RECORD_REQUIRED,
        0U,
        502U,
        generation,
        outer_path_offset,
        sizeof(outer_path),
        outer_path_segment_offset,
        sizeof(square_segments)};
    resources[2] = {
        sizeof(progpu_native_scene_resource),
        PROGPU_NATIVE_SCENE_RESOURCE_GLYPH_RUN,
        PROGPU_NATIVE_SCENE_RECORD_REQUIRED,
        0U,
        503U,
        generation,
        inner_outline_offset,
        sizeof(inner_outline),
        inner_outline_segment_offset,
        sizeof(square_segments)};
    resources[3] = {
        sizeof(progpu_native_scene_resource),
        PROGPU_NATIVE_SCENE_RESOURCE_ANALYTIC_BATCH,
        PROGPU_NATIVE_SCENE_RECORD_REQUIRED,
        0U,
        504U,
        generation,
        direct_analytic_offset,
        sizeof(direct_analytic),
        0U,
        0U};
    resources[4] = {
        sizeof(progpu_native_scene_resource),
        PROGPU_NATIVE_SCENE_RESOURCE_IMAGE,
        PROGPU_NATIVE_SCENE_RECORD_REQUIRED,
        0U,
        505U,
        generation,
        sequential_image_offset,
        sequential_image_pixels.size(),
        0U,
        0U};
    resources[5] = {
        sizeof(progpu_native_scene_resource),
        PROGPU_NATIVE_SCENE_RESOURCE_STATE,
        PROGPU_NATIVE_SCENE_RECORD_REQUIRED,
        0U,
        506U,
        generation,
        state_offset,
        sizeof(layer_state),
        0U,
        0U};
    std::memcpy(
        stream.data() + resource_offset,
        resources.data(),
        sizeof(resources));

    const progpu_native_scene_command commands[]{
        {sizeof(progpu_native_scene_command),
            PROGPU_NATIVE_SCENE_COMMAND_DRAW_ANALYTIC,
            PROGPU_NATIVE_SCENE_RECORD_REQUIRED, 0U, 601U,
            PROGPU_NATIVE_SCENE_NO_INDEX, 0U, 0U, 0U,
            4.0F, 4.0F, 12.0F, 12.0F, 0U, 0U},
        {sizeof(progpu_native_scene_command),
            PROGPU_NATIVE_SCENE_COMMAND_PUSH_LAYER,
            PROGPU_NATIVE_SCENE_RECORD_REQUIRED, 0U, 602U,
            5U, PROGPU_NATIVE_SCENE_NO_INDEX,
            outer_layer_offset, sizeof(outer_layer),
            0.0F, 0.0F, 0.0F, 0.0F, 0U, 0U},
        {sizeof(progpu_native_scene_command),
            PROGPU_NATIVE_SCENE_COMMAND_DRAW_PATH,
            PROGPU_NATIVE_SCENE_RECORD_REQUIRED, 0U, 603U,
            PROGPU_NATIVE_SCENE_NO_INDEX, 1U, 0U, 0U,
            20.0F, 4.0F, 12.0F, 12.0F, 0U, 0U},
        {sizeof(progpu_native_scene_command),
            PROGPU_NATIVE_SCENE_COMMAND_PUSH_LAYER,
            PROGPU_NATIVE_SCENE_RECORD_REQUIRED, 0U, 604U,
            PROGPU_NATIVE_SCENE_NO_INDEX, PROGPU_NATIVE_SCENE_NO_INDEX,
            inner_layer_offset, sizeof(inner_layer),
            0.0F, 0.0F, 0.0F, 0.0F, 0U, 0U},
        {sizeof(progpu_native_scene_command),
            PROGPU_NATIVE_SCENE_COMMAND_DRAW_GLYPH_RUN,
            PROGPU_NATIVE_SCENE_RECORD_REQUIRED, 0U, 605U,
            PROGPU_NATIVE_SCENE_NO_INDEX, 2U,
            inner_glyph_offset, sizeof(inner_glyph),
            36.0F, 4.0F, 12.0F, 12.0F, 0U, 0U},
        {sizeof(progpu_native_scene_command),
            PROGPU_NATIVE_SCENE_COMMAND_POP_LAYER,
            PROGPU_NATIVE_SCENE_RECORD_REQUIRED, 0U, 606U,
            PROGPU_NATIVE_SCENE_NO_INDEX, PROGPU_NATIVE_SCENE_NO_INDEX,
            0U, 0U, 0.0F, 0.0F, 0.0F, 0.0F, 0U, 0U},
        {sizeof(progpu_native_scene_command),
            PROGPU_NATIVE_SCENE_COMMAND_POP_LAYER,
            PROGPU_NATIVE_SCENE_RECORD_REQUIRED, 0U, 607U,
            PROGPU_NATIVE_SCENE_NO_INDEX, PROGPU_NATIVE_SCENE_NO_INDEX,
            0U, 0U, 0.0F, 0.0F, 0.0F, 0.0F, 0U, 0U},
        {sizeof(progpu_native_scene_command),
            PROGPU_NATIVE_SCENE_COMMAND_PUSH_LAYER,
            PROGPU_NATIVE_SCENE_RECORD_REQUIRED, 0U, 608U,
            PROGPU_NATIVE_SCENE_NO_INDEX, PROGPU_NATIVE_SCENE_NO_INDEX,
            sequential_layer_offset, sizeof(sequential_layer),
            0.0F, 0.0F, 0.0F, 0.0F, 0U, 0U},
        {sizeof(progpu_native_scene_command),
            PROGPU_NATIVE_SCENE_COMMAND_DRAW_IMAGE,
            PROGPU_NATIVE_SCENE_RECORD_REQUIRED, 0U, 609U,
            PROGPU_NATIVE_SCENE_NO_INDEX, 4U,
            sequential_image_draw_offset, sizeof(sequential_image),
            4.0F, 24.0F, 12.0F, 12.0F, 0U, 0U},
        {sizeof(progpu_native_scene_command),
            PROGPU_NATIVE_SCENE_COMMAND_POP_LAYER,
            PROGPU_NATIVE_SCENE_RECORD_REQUIRED, 0U, 610U,
            PROGPU_NATIVE_SCENE_NO_INDEX, PROGPU_NATIVE_SCENE_NO_INDEX,
            0U, 0U, 0.0F, 0.0F, 0.0F, 0.0F, 0U, 0U},
        {sizeof(progpu_native_scene_command),
            PROGPU_NATIVE_SCENE_COMMAND_PUSH_LAYER,
            PROGPU_NATIVE_SCENE_RECORD_REQUIRED, 0U, 611U,
            PROGPU_NATIVE_SCENE_NO_INDEX, PROGPU_NATIVE_SCENE_NO_INDEX,
            direct_layer_offset, sizeof(direct_layer),
            0.0F, 0.0F, 0.0F, 0.0F, 0U, 0U},
        {sizeof(progpu_native_scene_command),
            PROGPU_NATIVE_SCENE_COMMAND_DRAW_ANALYTIC,
            PROGPU_NATIVE_SCENE_RECORD_REQUIRED, 0U, 612U,
            PROGPU_NATIVE_SCENE_NO_INDEX, 3U, 0U, 0U,
            52.0F, 4.0F, 8.0F, 12.0F, 0U, 0U},
        {sizeof(progpu_native_scene_command),
            PROGPU_NATIVE_SCENE_COMMAND_POP_LAYER,
            PROGPU_NATIVE_SCENE_RECORD_REQUIRED, 0U, 613U,
            PROGPU_NATIVE_SCENE_NO_INDEX, PROGPU_NATIVE_SCENE_NO_INDEX,
            0U, 0U, 0.0F, 0.0F, 0.0F, 0.0F, 0U, 0U}
    };
    std::memcpy(
        stream.data() + command_offset,
        commands,
        sizeof(commands));
    return stream;
}

std::vector<std::byte> create_semantic_masked_layer_scene_stream() {
    constexpr std::uint32_t command_count = 5U;
    constexpr std::uint32_t resource_count = 2U;
    constexpr std::uint32_t command_offset =
        sizeof(progpu_native_scene_header);
    constexpr std::uint32_t resource_offset = command_offset +
        command_count * sizeof(progpu_native_scene_command);
    constexpr std::uint32_t arena_offset = resource_offset +
        resource_count * sizeof(progpu_native_scene_resource);
    std::vector<std::byte> stream(arena_offset);

    constexpr progpu_native_affine_2d identity{
        1.0F, 0.0F, 0.0F, 1.0F, 0.0F, 0.0F};
    const progpu_native_analytic_primitive analytic{
        PROGPU_NATIVE_PRIMITIVE_RECTANGLE, 0U,
        12.0F, 12.0F, 32.0F, 24.0F, 0.0F, 0.0F,
        {1.0F, 0.0F, 0.0F, 1.0F}, identity};
    const std::uint32_t analytic_offset = append_scene_payload(
        stream,
        &analytic,
        1U);

    progpu_native_scene_layer_mask mask{};
    mask.struct_size = sizeof(mask);
    mask.kind = PROGPU_NATIVE_SCENE_LAYER_MASK_ROUNDED_RECTANGLE;
    mask.bounds = {0.0F, 0.0F, 32.0F, 24.0F};
    mask.transform = {1.0F, 0.0F, 0.0F, 1.0F, 12.0F, 12.0F};
    std::fill_n(mask.corner_radii_x, 4U, 8.0F);
    std::fill_n(mask.corner_radii_y, 4U, 8.0F);
    mask.opacity = 1.0F;
    const std::uint32_t mask_offset = append_scene_payload(
        stream,
        &mask,
        1U);

    const progpu_native_scene_layer outer_layer{
        sizeof(progpu_native_scene_layer),
        PROGPU_NATIVE_SCENE_LAYER_BOUNDS |
            PROGPU_NATIVE_SCENE_LAYER_FORCE_ISOLATION,
        {8.0F, 8.0F, 40.0F, 32.0F},
        1.0F,
        PROGPU_NATIVE_BLEND_SRC_OVER,
        PROGPU_NATIVE_SCENE_NO_INDEX,
        PROGPU_NATIVE_SCENE_NO_INDEX,
        41U,
        51U,
        0U,
        0U};
    const progpu_native_scene_layer masked_layer{
        sizeof(progpu_native_scene_layer),
        PROGPU_NATIVE_SCENE_LAYER_BOUNDS |
            PROGPU_NATIVE_SCENE_LAYER_FORCE_ISOLATION,
        {12.0F, 12.0F, 32.0F, 24.0F},
        1.0F,
        PROGPU_NATIVE_BLEND_SRC_OVER,
        1U,
        PROGPU_NATIVE_SCENE_NO_INDEX,
        42U,
        52U,
        0U,
        0U};
    const std::uint32_t outer_layer_offset = append_scene_payload(
        stream,
        &outer_layer,
        1U);
    const std::uint32_t masked_layer_offset = append_scene_payload(
        stream,
        &masked_layer,
        1U);

    progpu_native_scene_header header{};
    header.struct_size = sizeof(header);
    header.magic = PROGPU_NATIVE_SCENE_STREAM_MAGIC;
    header.stream_version = PROGPU_NATIVE_SCENE_STREAM_VERSION;
    header.endian_marker = PROGPU_NATIVE_SCENE_STREAM_ENDIAN_MARKER;
    header.total_size = static_cast<std::uint32_t>(stream.size());
    header.scene_id = 95U;
    header.generation = 1U;
    header.command_offset = command_offset;
    header.command_count = command_count;
    header.command_stride = sizeof(progpu_native_scene_command);
    header.resource_offset = resource_offset;
    header.resource_count = resource_count;
    header.resource_stride = sizeof(progpu_native_scene_resource);
    header.arena_offset = arena_offset;
    header.arena_size = header.total_size - arena_offset;
    std::memcpy(stream.data(), &header, sizeof(header));

    const std::array<progpu_native_scene_resource, resource_count> resources{{
        {sizeof(progpu_native_scene_resource),
            PROGPU_NATIVE_SCENE_RESOURCE_ANALYTIC_BATCH,
            PROGPU_NATIVE_SCENE_RECORD_REQUIRED, 0U, 701U, 1U,
            analytic_offset, sizeof(analytic), 0U, 0U},
        {sizeof(progpu_native_scene_resource),
            PROGPU_NATIVE_SCENE_RESOURCE_LAYER_MASK,
            PROGPU_NATIVE_SCENE_RECORD_REQUIRED, 0U, 702U, 1U,
            mask_offset, sizeof(mask), 0U, 0U}
    }};
    std::memcpy(
        stream.data() + resource_offset,
        resources.data(),
        sizeof(resources));

    const progpu_native_scene_command commands[]{
        {sizeof(progpu_native_scene_command),
            PROGPU_NATIVE_SCENE_COMMAND_PUSH_LAYER,
            PROGPU_NATIVE_SCENE_RECORD_REQUIRED, 0U, 801U,
            PROGPU_NATIVE_SCENE_NO_INDEX, PROGPU_NATIVE_SCENE_NO_INDEX,
            outer_layer_offset, sizeof(outer_layer),
            0.0F, 0.0F, 0.0F, 0.0F, 0U, 0U},
        {sizeof(progpu_native_scene_command),
            PROGPU_NATIVE_SCENE_COMMAND_PUSH_LAYER,
            PROGPU_NATIVE_SCENE_RECORD_REQUIRED, 0U, 802U,
            PROGPU_NATIVE_SCENE_NO_INDEX, PROGPU_NATIVE_SCENE_NO_INDEX,
            masked_layer_offset, sizeof(masked_layer),
            0.0F, 0.0F, 0.0F, 0.0F, 0U, 0U},
        {sizeof(progpu_native_scene_command),
            PROGPU_NATIVE_SCENE_COMMAND_DRAW_ANALYTIC,
            PROGPU_NATIVE_SCENE_RECORD_REQUIRED, 0U, 803U,
            PROGPU_NATIVE_SCENE_NO_INDEX, 0U, 0U, 0U,
            12.0F, 12.0F, 32.0F, 24.0F, 0U, 0U},
        {sizeof(progpu_native_scene_command),
            PROGPU_NATIVE_SCENE_COMMAND_POP_LAYER,
            PROGPU_NATIVE_SCENE_RECORD_REQUIRED, 0U, 804U,
            PROGPU_NATIVE_SCENE_NO_INDEX, PROGPU_NATIVE_SCENE_NO_INDEX,
            0U, 0U, 0.0F, 0.0F, 0.0F, 0.0F, 0U, 0U},
        {sizeof(progpu_native_scene_command),
            PROGPU_NATIVE_SCENE_COMMAND_POP_LAYER,
            PROGPU_NATIVE_SCENE_RECORD_REQUIRED, 0U, 805U,
            PROGPU_NATIVE_SCENE_NO_INDEX, PROGPU_NATIVE_SCENE_NO_INDEX,
            0U, 0U, 0.0F, 0.0F, 0.0F, 0.0F, 0U, 0U}
    };
    std::memcpy(
        stream.data() + command_offset,
        commands,
        sizeof(commands));
    return stream;
}

template<typename T>
T load_symbol(void* module, const char* name) {
    static_assert(std::is_pointer_v<T>);
    void* symbol = dlsym(module, name);
    require(symbol != nullptr, name);
    T result{};
    static_assert(sizeof(result) == sizeof(symbol));
    std::memcpy(&result, &symbol, sizeof(result));
    return result;
}

struct provider_api final {
    decltype(&webscene_gpu_provider_get_abi_version) get_abi_version{};
    decltype(&webscene_gpu_provider_get_info) get_info{};
    decltype(&webscene_gpu_provider_create) create{};
    decltype(&webscene_gpu_provider_destroy) destroy{};
    decltype(&webscene_gpu_provider_get_wgpu_instance) get_instance{};
    decltype(&webscene_gpu_provider_get_wgpu_proc_address) get_proc{};
    decltype(&webscene_gpu_provider_create_canvas) create_canvas{};
    decltype(&webscene_gpu_provider_acquire_canvas_texture) acquire{};
    decltype(&webscene_gpu_provider_present_canvas) present{};
    decltype(&webscene_gpu_provider_destroy_canvas) destroy_canvas{};
    decltype(&webscene_gpu_provider_retain_external_texture) retain_external{};
    decltype(&webscene_gpu_provider_release_external_texture) release_external{};

    explicit provider_api(void* module)
        : get_abi_version(load_symbol<decltype(get_abi_version)>(
            module, "webscene_gpu_provider_get_abi_version")),
          get_info(load_symbol<decltype(get_info)>(
            module, "webscene_gpu_provider_get_info")),
          create(load_symbol<decltype(create)>(
            module, "webscene_gpu_provider_create")),
          destroy(load_symbol<decltype(destroy)>(
            module, "webscene_gpu_provider_destroy")),
          get_instance(load_symbol<decltype(get_instance)>(
            module, "webscene_gpu_provider_get_wgpu_instance")),
          get_proc(load_symbol<decltype(get_proc)>(
            module, "webscene_gpu_provider_get_wgpu_proc_address")),
          create_canvas(load_symbol<decltype(create_canvas)>(
            module, "webscene_gpu_provider_create_canvas")),
          acquire(load_symbol<decltype(acquire)>(
            module, "webscene_gpu_provider_acquire_canvas_texture")),
          present(load_symbol<decltype(present)>(
            module, "webscene_gpu_provider_present_canvas")),
          destroy_canvas(load_symbol<decltype(destroy_canvas)>(
            module, "webscene_gpu_provider_destroy_canvas")),
          retain_external(load_symbol<decltype(retain_external)>(
            module, "webscene_gpu_provider_retain_external_texture")),
          release_external(load_symbol<decltype(release_external)>(
            module, "webscene_gpu_provider_release_external_texture")) {}
};

template<typename T>
T resolve(const provider_api& api, webscene_gpu_provider* provider,
    const char* name) {
    static_assert(std::is_pointer_v<T>);
    void* symbol = api.get_proc(provider, name);
    require(symbol != nullptr, name);
    T result{};
    static_assert(sizeof(result) == sizeof(symbol));
    std::memcpy(&result, &symbol, sizeof(result));
    return result;
}

struct adapter_request final {
    std::mutex mutex;
    std::condition_variable changed;
    WGPUAdapter adapter{};
    bool complete{};
};

struct device_request final {
    std::mutex mutex;
    std::condition_variable changed;
    WGPUDevice device{};
    bool complete{};
};

template<typename T>
void wait_for_request(T& request, const char* message) {
    std::unique_lock lock(request.mutex);
    require(request.changed.wait_for(lock, std::chrono::seconds(15),
        [&request] { return request.complete; }), message);
}

WGPUDevice create_device(const provider_api& api,
    webscene_gpu_provider* provider) {
    adapter_request adapter_state;
    WGPURequestAdapterOptions adapter_options =
        WGPU_REQUEST_ADAPTER_OPTIONS_INIT;
    adapter_options.backendType = WGPUBackendType_Metal;
    adapter_options.featureLevel = WGPUFeatureLevel_Core;
    WGPURequestAdapterCallbackInfo adapter_callback =
        WGPU_REQUEST_ADAPTER_CALLBACK_INFO_INIT;
    adapter_callback.mode = WGPUCallbackMode_AllowSpontaneous;
    adapter_callback.userdata1 = &adapter_state;
    adapter_callback.callback = [](
        WGPURequestAdapterStatus status,
        WGPUAdapter adapter,
        WGPUStringView,
        void* userdata1,
        void*) {
        auto* state = static_cast<adapter_request*>(userdata1);
        {
            std::lock_guard lock(state->mutex);
            state->adapter = status == WGPURequestAdapterStatus_Success
                ? adapter
                : nullptr;
            state->complete = true;
        }
        state->changed.notify_one();
    };
    resolve<WGPUProcInstanceRequestAdapter>(
        api, provider, "wgpuInstanceRequestAdapter")(
        static_cast<WGPUInstance>(api.get_instance(provider)),
        &adapter_options,
        adapter_callback);
    wait_for_request(adapter_state, "adapter request timed out");
    require(adapter_state.adapter != nullptr, "Metal adapter unavailable");

    const WGPUFeatureName features[] = {
        WGPUFeatureName_SharedTextureMemoryIOSurface,
        WGPUFeatureName_SharedFenceMTLSharedEvent
    };
    device_request device_state;
    WGPUDeviceDescriptor descriptor = WGPU_DEVICE_DESCRIPTOR_INIT;
    descriptor.requiredFeatureCount = std::size(features);
    descriptor.requiredFeatures = features;
    descriptor.uncapturedErrorCallbackInfo.callback = [](
        WGPUDevice const*,
        WGPUErrorType type,
        WGPUStringView message,
        void*,
        void*) {
        std::fprintf(stderr, "Dawn validation error %u: %.*s\n",
            static_cast<unsigned>(type),
            static_cast<int>(message.length),
            message.data == nullptr ? "" : message.data);
        std::abort();
    };
    descriptor.deviceLostCallbackInfo.mode =
        WGPUCallbackMode_AllowSpontaneous;
    descriptor.deviceLostCallbackInfo.callback = [](
        WGPUDevice const*,
        WGPUDeviceLostReason reason,
        WGPUStringView message,
        void*,
        void*) {
        if (reason == WGPUDeviceLostReason_Destroyed) {
            return;
        }
        std::fprintf(stderr, "Dawn device lost %u: %.*s\n",
            static_cast<unsigned>(reason),
            static_cast<int>(message.length),
            message.data == nullptr ? "" : message.data);
        std::abort();
    };
    WGPURequestDeviceCallbackInfo device_callback =
        WGPU_REQUEST_DEVICE_CALLBACK_INFO_INIT;
    device_callback.mode = WGPUCallbackMode_AllowSpontaneous;
    device_callback.userdata1 = &device_state;
    device_callback.callback = [](
        WGPURequestDeviceStatus status,
        WGPUDevice device,
        WGPUStringView,
        void* userdata1,
        void*) {
        auto* state = static_cast<device_request*>(userdata1);
        {
            std::lock_guard lock(state->mutex);
            state->device = status == WGPURequestDeviceStatus_Success
                ? device
                : nullptr;
            state->complete = true;
        }
        state->changed.notify_one();
    };
    resolve<WGPUProcAdapterRequestDevice>(
        api, provider, "wgpuAdapterRequestDevice")(
        adapter_state.adapter,
        &descriptor,
        device_callback);
    wait_for_request(device_state, "device request timed out");
    resolve<WGPUProcAdapterRelease>(api, provider, "wgpuAdapterRelease")(
        adapter_state.adapter);
    require(device_state.device != nullptr, "Dawn device unavailable");
    return device_state.device;
}

struct resolver_context final {
    const provider_api* api{};
    webscene_gpu_provider* provider{};
};

void* resolve_for_progpu(void* context, const char* name) {
    auto* state = static_cast<resolver_context*>(context);
    return state->api->get_proc(state->provider, name);
}

void verify_semantic_scene(
    IOSurfaceRef surface,
    const char* output_path) {
    require(surface != nullptr, "semantic scene has no IOSurface");
    require(IOSurfaceLock(surface, kIOSurfaceLockReadOnly, nullptr) ==
        kIOReturnSuccess, "could not lock semantic scene IOSurface");
    const auto* bytes = static_cast<const std::uint8_t*>(
        IOSurfaceGetBaseAddress(surface));
    const std::size_t width = IOSurfaceGetWidth(surface);
    const std::size_t height = IOSurfaceGetHeight(surface);
    const std::size_t row_bytes = IOSurfaceGetBytesPerRow(surface);
    require(bytes != nullptr && width == 64U && height == 48U &&
        row_bytes >= width * 4U, "unexpected semantic IOSurface storage");
    const auto pixel = [bytes, row_bytes](std::size_t x, std::size_t y) {
        return bytes + y * row_bytes + x * 4U;
    };
    const auto is_bgra = [](const std::uint8_t* value,
                            std::uint8_t b,
                            std::uint8_t g,
                            std::uint8_t r) {
        constexpr int tolerance = 48;
        return std::abs(static_cast<int>(value[0]) - b) <= tolerance &&
            std::abs(static_cast<int>(value[1]) - g) <= tolerance &&
            std::abs(static_cast<int>(value[2]) - r) <= tolerance &&
            value[3] >= 240U;
    };
    const auto* semantic_clear = pixel(2U, 2U);
    const auto* semantic_analytic = pixel(8U, 8U);
    const auto* semantic_path = pixel(24U, 8U);
    const auto* semantic_glyph = pixel(40U, 8U);
    const auto* semantic_image = pixel(54U, 8U);
    const auto* semantic_second_analytic = pixel(8U, 28U);
    const auto* semantic_second_path = pixel(24U, 28U);
    const auto* semantic_second_glyph = pixel(40U, 28U);
    const auto* semantic_second_image = pixel(54U, 28U);
    std::fprintf(stderr,
        "semantic clear=%u,%u,%u,%u analytic=%u,%u,%u,%u "
        "path=%u,%u,%u,%u glyph=%u,%u,%u,%u image=%u,%u,%u,%u "
        "second-path=%u,%u,%u,%u second-glyph=%u,%u,%u,%u "
        "second-image=%u,%u,%u,%u second-analytic=%u,%u,%u,%u\n",
        semantic_clear[0], semantic_clear[1], semantic_clear[2], semantic_clear[3],
        semantic_analytic[0], semantic_analytic[1], semantic_analytic[2], semantic_analytic[3],
        semantic_path[0], semantic_path[1], semantic_path[2], semantic_path[3],
        semantic_glyph[0], semantic_glyph[1], semantic_glyph[2], semantic_glyph[3],
        semantic_image[0], semantic_image[1], semantic_image[2], semantic_image[3],
        semantic_second_path[0], semantic_second_path[1],
        semantic_second_path[2], semantic_second_path[3],
        semantic_second_glyph[0], semantic_second_glyph[1],
        semantic_second_glyph[2], semantic_second_glyph[3],
        semantic_second_image[0], semantic_second_image[1],
        semantic_second_image[2], semantic_second_image[3],
        semantic_second_analytic[0], semantic_second_analytic[1],
        semantic_second_analytic[2], semantic_second_analytic[3]);
    require(is_bgra(pixel(2U, 2U), 10U, 8U, 5U),
        "semantic clear color is missing");
    require(is_bgra(pixel(8U, 8U), 0U, 0U, 255U),
        "semantic analytic draw is missing");
    require(is_bgra(pixel(24U, 8U), 0U, 255U, 0U),
        "semantic path draw is missing");
    require(is_bgra(pixel(40U, 8U), 12U, 35U, 230U),
        "semantic styled positioned-glyph draw is missing");
    require(is_bgra(pixel(54U, 8U), 0U, 255U, 255U),
        "semantic image draw is missing");
    require(is_bgra(pixel(24U, 28U), 132U, 4U, 130U),
        "second distinct semantic path draw is missing");
    require(is_bgra(pixel(40U, 28U), 11U, 22U, 118U),
        "second state-opacity semantic styled glyph draw is missing");
    require(semantic_second_image[0] > 20U &&
            semantic_second_image[0] < 180U &&
            semantic_second_image[1] > 20U &&
            semantic_second_image[1] < 180U &&
            semantic_second_image[2] > 20U &&
            semantic_second_image[2] < 180U &&
            std::abs(static_cast<int>(semantic_second_image[0]) -
                static_cast<int>(semantic_second_image[1])) <= 8 &&
            std::abs(static_cast<int>(semantic_second_image[1]) -
                static_cast<int>(semantic_second_image[2])) <= 8 &&
            semantic_second_image[3] >= 240U,
        "fused cubic/color-matrix semantic image processing is missing");
    require(is_bgra(pixel(8U, 28U), 132U, 132U, 3U),
        "second distinct semantic analytic draw is missing");
    require(is_bgra(pixel(30U, 42U), 255U, 115U, 38U),
        "semantic retained geometry draw is missing");
    require(is_bgra(pixel(6U, 28U), 10U, 8U, 5U),
        "semantic state clip did not trim the analytic left edge");
    require(is_bgra(pixel(58U, 28U), 10U, 8U, 5U),
        "semantic state clip did not trim the image right edge");

    if (output_path != nullptr && output_path[0] != '\0') {
        std::FILE* output = std::fopen(output_path, "wb");
        require(output != nullptr, "could not create semantic capture");
        std::fprintf(output, "P6\n%zu %zu\n255\n", width, height);
        for (std::size_t y = 0; y < height; ++y) {
            for (std::size_t x = 0; x < width; ++x) {
                const std::uint8_t* source = pixel(x, y);
                const std::uint8_t rgb[]{
                    source[2], source[1], source[0]};
                require(std::fwrite(rgb, sizeof(rgb), 1U, output) == 1U,
                    "semantic capture write failed");
            }
        }
        require(std::fclose(output) == 0,
            "semantic capture close failed");
    }
    require(IOSurfaceUnlock(surface, kIOSurfaceLockReadOnly, nullptr) ==
        kIOReturnSuccess, "could not unlock semantic scene IOSurface");
}

void verify_semantic_color_glyph_scene(
    IOSurfaceRef surface,
    const char* output_path) {
    require(surface != nullptr, "color-glyph scene has no IOSurface");
    require(IOSurfaceLock(surface, kIOSurfaceLockReadOnly, nullptr) ==
        kIOReturnSuccess, "could not lock color-glyph IOSurface");
    const auto* bytes = static_cast<const std::uint8_t*>(
        IOSurfaceGetBaseAddress(surface));
    const std::size_t width = IOSurfaceGetWidth(surface);
    const std::size_t height = IOSurfaceGetHeight(surface);
    const std::size_t row_bytes = IOSurfaceGetBytesPerRow(surface);
    require(bytes != nullptr && width == 64U && height == 48U &&
        row_bytes >= width * 4U,
        "unexpected color-glyph IOSurface storage");
    const auto pixel = [bytes, row_bytes](std::size_t x, std::size_t y) {
        return bytes + y * row_bytes + x * 4U;
    };
    const auto near_bgra = [](const std::uint8_t* value,
                              int b,
                              int g,
                              int r) {
        constexpr int tolerance = 64;
        return std::abs(static_cast<int>(value[0]) - b) <= tolerance &&
            std::abs(static_cast<int>(value[1]) - g) <= tolerance &&
            std::abs(static_cast<int>(value[2]) - r) <= tolerance &&
            value[3] >= 240U;
    };
    require(near_bgra(pixel(22U, 18U), 16, 34, 194),
        "color-glyph red quadrant is missing");
    require(near_bgra(pixel(34U, 18U), 46, 166, 15),
        "color-glyph green quadrant is missing");
    require(near_bgra(pixel(22U, 30U), 194, 64, 15),
        "color-glyph blue quadrant is missing");
    require(near_bgra(pixel(34U, 30U), 11, 85, 97),
        "color-glyph translucent quadrant is missing");
    require(near_bgra(pixel(46U, 14U), 26, 0, 230),
        "vector color-glyph transformed-gradient start is missing");
    require(near_bgra(pixel(54U, 14U), 230, 0, 26),
        "vector color-glyph transformed-gradient end is missing");
    require(near_bgra(pixel(50U, 18U), 255, 255, 0),
        "vector color-glyph inner layer is missing");
    require(near_bgra(pixel(10U, 26U), 218, 218, 218),
        "color-glyph strikethrough lowering is missing");
    require(near_bgra(pixel(10U, 40U), 218, 218, 218),
        "color-glyph underline lowering is missing");
    const auto* noise_a = pixel(3U, 3U);
    const auto* noise_b = pixel(10U, 6U);
    const int noise_delta =
        std::abs(static_cast<int>(noise_a[0]) - noise_b[0]) +
        std::abs(static_cast<int>(noise_a[1]) - noise_b[1]) +
        std::abs(static_cast<int>(noise_a[2]) - noise_b[2]);
    require(noise_a[3] >= 240U && noise_b[3] >= 240U &&
            noise_delta >= 20,
        "transformed retained Perlin-noise brush is missing");

    if (output_path != nullptr && output_path[0] != '\0') {
        std::FILE* output = std::fopen(output_path, "wb");
        require(output != nullptr, "could not create color-glyph capture");
        std::fprintf(output, "P6\n%zu %zu\n255\n", width, height);
        for (std::size_t y = 0U; y < height; ++y) {
            for (std::size_t x = 0U; x < width; ++x) {
                const auto* source = pixel(x, y);
                const std::uint8_t rgb[]{
                    source[2], source[1], source[0]};
                require(std::fwrite(rgb, sizeof(rgb), 1U, output) == 1U,
                    "color-glyph capture write failed");
            }
        }
        require(std::fclose(output) == 0,
            "color-glyph capture close failed");
    }
    require(IOSurfaceUnlock(surface, kIOSurfaceLockReadOnly, nullptr) ==
        kIOReturnSuccess, "could not unlock color-glyph IOSurface");
}

void verify_semantic_layer_scene(
    IOSurfaceRef surface,
    const char* output_path,
    std::size_t scale = 1U) {
    require(surface != nullptr, "semantic layer scene has no IOSurface");
    require(IOSurfaceLock(surface, kIOSurfaceLockReadOnly, nullptr) ==
        kIOReturnSuccess, "could not lock semantic layer IOSurface");
    const auto* bytes = static_cast<const std::uint8_t*>(
        IOSurfaceGetBaseAddress(surface));
    const std::size_t width = IOSurfaceGetWidth(surface);
    const std::size_t height = IOSurfaceGetHeight(surface);
    const std::size_t row_bytes = IOSurfaceGetBytesPerRow(surface);
    require(bytes != nullptr && width == 64U * scale &&
        height == 48U * scale &&
        row_bytes >= width * 4U,
        "unexpected semantic layer IOSurface storage");
    const auto pixel = [bytes, row_bytes](std::size_t x, std::size_t y) {
        return bytes + y * row_bytes + x * 4U;
    };
    const auto is_bgra = [](const std::uint8_t* value,
                            std::uint8_t b,
                            std::uint8_t g,
                            std::uint8_t r,
                            int tolerance = 24) {
        return std::abs(static_cast<int>(value[0]) - b) <= tolerance &&
            std::abs(static_cast<int>(value[1]) - g) <= tolerance &&
            std::abs(static_cast<int>(value[2]) - r) <= tolerance &&
            value[3] >= 240U;
    };
    const auto* clear = pixel(2U * scale, 2U * scale);
    const auto* red = pixel(8U * scale, 8U * scale);
    const auto* unshifted_green = pixel(24U * scale, 8U * scale);
    const auto* bounded_green = pixel(20U * scale, 28U * scale);
    const auto* green = pixel(24U * scale, 28U * scale);
    const auto* blue = pixel(40U * scale, 28U * scale);
    const auto* magenta = pixel(8U * scale, 28U * scale);
    const auto* yellow = pixel(56U * scale, 8U * scale);
    std::fprintf(stderr,
        "semantic-layer clear=%u,%u,%u,%u red=%u,%u,%u,%u "
        "green=%u,%u,%u,%u blue=%u,%u,%u,%u "
        "magenta=%u,%u,%u,%u "
        "yellow=%u,%u,%u,%u\n",
        clear[0], clear[1], clear[2], clear[3],
        red[0], red[1], red[2], red[3],
        green[0], green[1], green[2], green[3],
        blue[0], blue[1], blue[2], blue[3],
        magenta[0], magenta[1], magenta[2], magenta[3],
        yellow[0], yellow[1], yellow[2], yellow[3]);
    require(is_bgra(clear, 10U, 8U, 5U),
        "semantic layer clear color is missing");
    require(is_bgra(red, 0U, 0U, 255U),
        "draw before semantic layer is missing");
    require(is_bgra(unshifted_green, 10U, 8U, 5U),
        "semantic layer state transform was not scoped");
    require(is_bgra(bounded_green, 10U, 8U, 5U),
        "bounded semantic layer did not crop its left edge");
    require(is_bgra(green, 5U, 131U, 3U),
        "outer semantic group opacity is incorrect");
    require(is_bgra(blue, 71U, 6U, 4U),
        "nested semantic group opacity is incorrect");
    require(is_bgra(magenta, 74U, 8U, 69U, 4),
        "sequential same-depth semantic plus blend is incorrect");
    require(is_bgra(yellow, 0U, 255U, 255U),
        "semantic layer state was not restored after pop");

    if (output_path != nullptr && output_path[0] != '\0') {
        std::FILE* output = std::fopen(output_path, "wb");
        require(output != nullptr,
            "could not create semantic layer capture");
        std::fprintf(output, "P6\n%zu %zu\n255\n", width, height);
        for (std::size_t y = 0; y < height; ++y) {
            for (std::size_t x = 0; x < width; ++x) {
                const std::uint8_t* source = pixel(x, y);
                const std::uint8_t rgb[]{
                    source[2], source[1], source[0]};
                require(std::fwrite(rgb, sizeof(rgb), 1U, output) == 1U,
                    "semantic layer capture write failed");
            }
        }
        require(std::fclose(output) == 0,
            "semantic layer capture close failed");
    }
    require(IOSurfaceUnlock(surface, kIOSurfaceLockReadOnly, nullptr) ==
        kIOReturnSuccess, "could not unlock semantic layer IOSurface");
}

void verify_semantic_backdrop_scene(
    IOSurfaceRef surface,
    const char* output_path) {
    require(surface != nullptr, "semantic backdrop scene has no IOSurface");
    require(IOSurfaceLock(surface, kIOSurfaceLockReadOnly, nullptr) ==
        kIOReturnSuccess, "could not lock semantic backdrop IOSurface");
    const auto* bytes = static_cast<const std::uint8_t*>(
        IOSurfaceGetBaseAddress(surface));
    const std::size_t width = IOSurfaceGetWidth(surface);
    const std::size_t height = IOSurfaceGetHeight(surface);
    const std::size_t row_bytes = IOSurfaceGetBytesPerRow(surface);
    require(bytes != nullptr && width == 64U && height == 48U &&
        row_bytes >= width * 4U,
        "unexpected semantic backdrop IOSurface storage");
    const auto pixel = [bytes, row_bytes](std::size_t x, std::size_t y) {
        return bytes + y * row_bytes + x * 4U;
    };
    const auto* outside_left = pixel(4U, 4U);
    const auto* outside_right = pixel(60U, 4U);
    const auto* bounded_left = pixel(14U, 24U);
    const auto* filtered_left = pixel(20U, 24U);
    const auto* transition = pixel(31U, 24U);
    const auto* marker = pixel(26U, 24U);
    const auto* filtered_right = pixel(36U, 24U);
    const auto* bounded_right = pixel(50U, 24U);
    const auto* initialized_previous = pixel(12U, 40U);
    const auto opaque = [](const std::uint8_t* value) {
        return value[3] >= 240U;
    };
    std::fprintf(stderr,
        "semantic-backdrop outside-left=%u,%u,%u,%u "
        "filtered-left=%u,%u,%u,%u transition=%u,%u,%u,%u "
        "marker=%u,%u,%u,%u filtered-right=%u,%u,%u,%u\n",
        outside_left[0], outside_left[1], outside_left[2], outside_left[3],
        filtered_left[0], filtered_left[1], filtered_left[2],
        filtered_left[3], transition[0], transition[1], transition[2],
        transition[3], marker[0], marker[1], marker[2], marker[3],
        filtered_right[0], filtered_right[1], filtered_right[2],
        filtered_right[3]);
    require(outside_left[2] >= 240U && outside_left[0] <= 16U &&
        opaque(outside_left),
        "left parent content outside the backdrop was lost");
    require(outside_right[0] >= 240U && outside_right[2] <= 16U &&
        opaque(outside_right),
        "right parent content outside the backdrop was lost");
    require(bounded_left[2] >= 240U && bounded_left[0] <= 16U &&
        opaque(bounded_left),
        "backdrop capture escaped its left bound");
    require(bounded_right[0] >= 240U && bounded_right[2] <= 16U &&
        opaque(bounded_right),
        "backdrop capture escaped its right bound");
    require(filtered_left[2] >= 160U && filtered_left[0] <= 96U &&
        opaque(filtered_left),
        "filtered backdrop lost its left source");
    require(transition[0] >= 40U && transition[2] >= 40U &&
        transition[0] <= 220U && transition[2] <= 220U &&
        opaque(transition),
        "backdrop Gaussian effect did not filter the parent transition");
    require(marker[1] >= 240U && marker[0] <= 16U &&
        marker[2] >= 80U && marker[2] <= 180U && opaque(marker),
        "retained linear gradient was not drawn over the filtered backdrop");
    require(filtered_right[0] >= 160U && filtered_right[2] <= 96U &&
        opaque(filtered_right),
        "filtered backdrop lost its right source");
    require(initialized_previous[2] >= 240U &&
        initialized_previous[0] <= 16U && opaque(initialized_previous),
        "unfiltered backdrop did not initialize from previous parent pixels");

    if (output_path != nullptr && output_path[0] != '\0') {
        std::FILE* output = std::fopen(output_path, "wb");
        require(output != nullptr,
            "could not create semantic backdrop capture");
        std::fprintf(output, "P6\n%zu %zu\n255\n", width, height);
        for (std::size_t y = 0U; y < height; ++y) {
            for (std::size_t x = 0U; x < width; ++x) {
                const std::uint8_t* source = pixel(x, y);
                const std::uint8_t rgb[]{source[2], source[1], source[0]};
                require(std::fwrite(rgb, sizeof(rgb), 1U, output) == 1U,
                    "semantic backdrop capture write failed");
            }
        }
        require(std::fclose(output) == 0,
            "semantic backdrop capture close failed");
    }
    require(IOSurfaceUnlock(surface, kIOSurfaceLockReadOnly, nullptr) ==
        kIOReturnSuccess, "could not unlock semantic backdrop IOSurface");
}

void verify_semantic_masked_layer_scene(
    IOSurfaceRef surface,
    const char* output_path) {
    require(surface != nullptr,
        "semantic masked layer scene has no IOSurface");
    require(IOSurfaceLock(surface, kIOSurfaceLockReadOnly, nullptr) ==
        kIOReturnSuccess,
        "could not lock semantic masked layer IOSurface");
    const auto* bytes = static_cast<const std::uint8_t*>(
        IOSurfaceGetBaseAddress(surface));
    const std::size_t width = IOSurfaceGetWidth(surface);
    const std::size_t height = IOSurfaceGetHeight(surface);
    const std::size_t row_bytes = IOSurfaceGetBytesPerRow(surface);
    require(bytes != nullptr && width == 64U && height == 48U &&
        row_bytes >= width * 4U,
        "unexpected semantic masked layer IOSurface storage");
    const auto pixel = [bytes, row_bytes](std::size_t x, std::size_t y) {
        return bytes + y * row_bytes + x * 4U;
    };
    const auto is_bgra = [](const std::uint8_t* value,
                            std::uint8_t b,
                            std::uint8_t g,
                            std::uint8_t r,
                            int tolerance = 24) {
        return std::abs(static_cast<int>(value[0]) - b) <= tolerance &&
            std::abs(static_cast<int>(value[1]) - g) <= tolerance &&
            std::abs(static_cast<int>(value[2]) - r) <= tolerance &&
            value[3] >= 240U;
    };
    const auto* clear = pixel(2U, 2U);
    const auto* rounded_corner = pixel(13U, 13U);
    const auto* top_center = pixel(28U, 13U);
    const auto* center = pixel(28U, 24U);
    require(is_bgra(clear, 10U, 8U, 5U),
        "semantic masked layer clear color is missing");
    require(is_bgra(rounded_corner, 10U, 8U, 5U),
        "semantic rounded mask did not remove its corner");
    require(is_bgra(top_center, 0U, 0U, 255U),
        "semantic rounded mask removed its top-center interior");
    require(is_bgra(center, 0U, 0U, 255U),
        "semantic rounded mask removed its center interior");

    if (output_path != nullptr && output_path[0] != '\0') {
        std::FILE* output = std::fopen(output_path, "wb");
        require(output != nullptr,
            "could not create semantic masked layer capture");
        std::fprintf(output, "P6\n%zu %zu\n255\n", width, height);
        for (std::size_t y = 0; y < height; ++y) {
            for (std::size_t x = 0; x < width; ++x) {
                const std::uint8_t* source = pixel(x, y);
                const std::uint8_t rgb[]{
                    source[2], source[1], source[0]};
                require(std::fwrite(rgb, sizeof(rgb), 1U, output) == 1U,
                    "semantic masked layer capture write failed");
            }
        }
        require(std::fclose(output) == 0,
            "semantic masked layer capture close failed");
    }
    require(IOSurfaceUnlock(surface, kIOSurfaceLockReadOnly, nullptr) ==
        kIOReturnSuccess,
        "could not unlock semantic masked layer IOSurface");
}

void verify_semantic_coverage_mask_scene(IOSurfaceRef surface) {
    require(surface != nullptr,
        "semantic coverage-mask scene has no IOSurface");
    require(IOSurfaceLock(surface, kIOSurfaceLockReadOnly, nullptr) ==
            kIOReturnSuccess,
        "could not lock semantic coverage-mask IOSurface");
    const auto* bytes = static_cast<const std::uint8_t*>(
        IOSurfaceGetBaseAddress(surface));
    const std::size_t width = IOSurfaceGetWidth(surface);
    const std::size_t height = IOSurfaceGetHeight(surface);
    const std::size_t row_bytes = IOSurfaceGetBytesPerRow(surface);
    require(bytes != nullptr && width == 64U && height == 48U &&
        row_bytes >= width * 4U,
        "unexpected semantic coverage-mask IOSurface storage");
    const auto pixel = [bytes, row_bytes](std::size_t x, std::size_t y) {
        return bytes + y * row_bytes + x * 4U;
    };
    const auto cyan = [](const std::uint8_t* value) {
        return value[0] >= 230U && value[1] >= 190U &&
            value[2] <= 24U && value[3] >= 240U;
    };
    const auto clear = [](const std::uint8_t* value) {
        return value[0] <= 16U && value[1] <= 16U &&
            value[2] <= 16U && value[3] >= 240U;
    };
    require(cyan(pixel(23U, 15U)),
        "retained coverage mask lost the left H stem");
    require(clear(pixel(33U, 16U)),
        "retained coverage mask did not remove the H counter");
    require(cyan(pixel(34U, 24U)),
        "retained coverage mask lost the H bridge");
    require(clear(pixel(14U, 22U)),
        "retained coverage mask escaped its transformed bounds");
    require(IOSurfaceUnlock(surface, kIOSurfaceLockReadOnly, nullptr) ==
            kIOReturnSuccess,
        "could not unlock semantic coverage-mask IOSurface");
}

void verify_and_capture(IOSurfaceRef surface, const char* output_path) {
    require(surface != nullptr, "provider did not expose an IOSurface");
    require(IOSurfaceLock(surface, kIOSurfaceLockReadOnly, nullptr) ==
        kIOReturnSuccess, "could not lock IOSurface");

    const auto* bytes = static_cast<const std::uint8_t*>(
        IOSurfaceGetBaseAddress(surface));
    const std::size_t width = IOSurfaceGetWidth(surface);
    const std::size_t height = IOSurfaceGetHeight(surface);
    const std::size_t row_bytes = IOSurfaceGetBytesPerRow(surface);
    require(bytes != nullptr && width == 64U && height == 48U &&
        row_bytes >= width * 4U, "unexpected IOSurface storage");

    const auto pixel = [bytes, row_bytes](std::size_t x, std::size_t y) {
        return bytes + y * row_bytes + x * 4U;
    };
    const std::uint8_t* outside = pixel(2U, 2U);
    const std::uint8_t* clipped = pixel(12U, 20U);
    const std::uint8_t* inside = pixel(20U, 20U);
    std::fprintf(stderr,
        "IOSurface outside=%u,%u,%u,%u clipped=%u,%u,%u,%u "
        "inside=%u,%u,%u,%u row=%zu\n",
        outside[0], outside[1], outside[2], outside[3],
        clipped[0], clipped[1], clipped[2], clipped[3],
        inside[0], inside[1], inside[2], inside[3], row_bytes);

    if (output_path != nullptr && output_path[0] != '\0') {
        std::FILE* output = std::fopen(output_path, "wb");
        require(output != nullptr, "could not create capture");
        std::fprintf(output, "P6\n%zu %zu\n255\n", width, height);
        for (std::size_t y = 0; y < height; ++y) {
            for (std::size_t x = 0; x < width; ++x) {
                const std::uint8_t* source = pixel(x, y);
                const std::uint8_t rgb[] = {
                    source[2], source[1], source[0]
                };
                require(std::fwrite(rgb, sizeof(rgb), 1U, output) == 1U,
                    "capture write failed");
            }
        }
        require(std::fclose(output) == 0, "capture close failed");
    }

    require(outside[2] < 80U && outside[0] > outside[2],
        "clear-color pixel did not reach the IOSurface");
    require(std::memcmp(outside, clipped, 4U) == 0,
        "physical draw-state scissor did not preserve the clear color");
    require(inside[2] > 100U && inside[2] < 160U &&
        inside[2] > inside[1] * 2U &&
        inside[2] > inside[0] * 2U,
        "native primitive opacity did not reach the IOSurface");
    require(IOSurfaceUnlock(surface, kIOSurfaceLockReadOnly, nullptr) ==
        kIOReturnSuccess, "could not unlock IOSurface");
}

} // namespace

int main(int argc, char** argv) {
    require(argc == 2 || argc == 3,
        "usage: test PROVIDER_DYLIB [CAPTURE_PPM]");
    void* module = dlopen(argv[1], RTLD_NOW | RTLD_LOCAL);
    require(module != nullptr, dlerror());
    provider_api api(module);
    require(api.get_abi_version() == WEBSCENE_GPU_PROVIDER_ABI_VERSION,
        "provider ABI mismatch");

    webscene_gpu_provider_info provider_info{};
    provider_info.struct_size = sizeof(provider_info);
    require(api.get_info(&provider_info) != 0U &&
        provider_info.abi_version == WEBSCENE_GPU_PROVIDER_ABI_VERSION &&
        (provider_info.capabilities & WEBSCENE_GPU_CAPABILITY_WEBGPU) != 0U,
        "provider does not report WebGPU support");

    webscene_gpu_provider_options provider_options{};
    provider_options.struct_size = sizeof(provider_options);
    provider_options.required_capabilities =
        WEBSCENE_GPU_CAPABILITY_WEBGPU;
    webscene_gpu_provider* provider = api.create(&provider_options);
    require(provider != nullptr, "provider creation failed");
    WGPUDevice device = create_device(api, provider);
    WGPUQueue queue = resolve<WGPUProcDeviceGetQueue>(
        api, provider, "wgpuDeviceGetQueue")(device);
    require(queue != nullptr, "device queue unavailable");

    resolver_context resolver{&api, provider};
    progpu_native_dawn_engine_options engine_options{};
    engine_options.struct_size = sizeof(engine_options);
    engine_options.native_abi_version = PROGPU_NATIVE_ABI_VERSION;
    engine_options.adapter_abi_version =
        PROGPU_NATIVE_DAWN_ADAPTER_ABI_VERSION;
    engine_options.provider_abi_version =
        WEBSCENE_GPU_PROVIDER_ABI_VERSION;
    engine_options.target_format = PROGPU_NATIVE_TEXTURE_FORMAT_BGRA8_UNORM;
    engine_options.resolver_context = &resolver;
    engine_options.resolve_proc = resolve_for_progpu;
    engine_options.instance = reinterpret_cast<std::uintptr_t>(
        api.get_instance(provider));
    engine_options.device = reinterpret_cast<std::uintptr_t>(device);
    engine_options.queue = reinterpret_cast<std::uintptr_t>(queue);
    progpu_native_engine* engine{};
    require(progpu_native_dawn_engine_create(&engine_options, &engine) ==
        PROGPU_NATIVE_STATUS_SUCCESS && engine != nullptr,
        "ProGPU Dawn engine creation failed");

    auto semantic_scene = create_semantic_scene_stream(1U, 2U);
    progpu_native_scene_metrics scene_metrics{};
    scene_metrics.struct_size = sizeof(scene_metrics);
    require(progpu_native_engine_update_scene(
        engine,
        semantic_scene.data(),
        semantic_scene.size(),
        &scene_metrics) == PROGPU_NATIVE_STATUS_SUCCESS &&
        scene_metrics.draw_count == 1U && scene_metrics.flags == 0U,
        "semantic scene snapshot update failed");
    scene_metrics.struct_size = sizeof(scene_metrics);
    require(progpu_native_engine_update_scene(
        engine,
        semantic_scene.data(),
        semantic_scene.size(),
        &scene_metrics) == PROGPU_NATIVE_STATUS_SUCCESS &&
        (scene_metrics.flags &
            PROGPU_NATIVE_SCENE_METRICS_SNAPSHOT_REUSED) != 0U,
        "unchanged semantic scene snapshot was not retained");

    auto mutated_same_generation = semantic_scene;
    mutated_same_generation.back() = std::byte{0x33};
    scene_metrics.struct_size = sizeof(scene_metrics);
    require(progpu_native_engine_update_scene(
        engine,
        mutated_same_generation.data(),
        mutated_same_generation.size(),
        &scene_metrics) == PROGPU_NATIVE_STATUS_INVALID_ARGUMENT &&
        scene_metrics.validation_error ==
            PROGPU_NATIVE_SCENE_VALIDATION_GENERATION,
        "mutable same-generation semantic scene did not fail closed");

    auto regressing_resource = create_semantic_scene_stream(2U, 1U);
    scene_metrics.struct_size = sizeof(scene_metrics);
    require(progpu_native_engine_update_scene(
        engine,
        regressing_resource.data(),
        regressing_resource.size(),
        &scene_metrics) == PROGPU_NATIVE_STATUS_INVALID_ARGUMENT &&
        scene_metrics.validation_error ==
            PROGPU_NATIVE_SCENE_VALIDATION_GENERATION,
        "regressing semantic resource generation did not fail closed");

    auto next_semantic_scene = create_semantic_scene_stream(2U, 3U);
    scene_metrics.struct_size = sizeof(scene_metrics);
    require(progpu_native_engine_update_scene(
        engine,
        next_semantic_scene.data(),
        next_semantic_scene.size(),
        &scene_metrics) == PROGPU_NATIVE_STATUS_SUCCESS,
        "next semantic scene generation failed");
    auto malformed_scene = next_semantic_scene;
    progpu_native_scene_command malformed_command{};
    std::memcpy(&malformed_command, malformed_scene.data() + 80U,
        sizeof(malformed_command));
    malformed_command.kind = PROGPU_NATIVE_SCENE_COMMAND_RESTORE;
    malformed_command.resource_index = PROGPU_NATIVE_SCENE_NO_INDEX;
    std::memcpy(malformed_scene.data() + 80U, &malformed_command,
        sizeof(malformed_command));
    scene_metrics.struct_size = sizeof(scene_metrics);
    require(progpu_native_engine_update_scene(
        engine,
        malformed_scene.data(),
        malformed_scene.size(),
        &scene_metrics) == PROGPU_NATIVE_STATUS_INVALID_ARGUMENT &&
        scene_metrics.validation_error == PROGPU_NATIVE_SCENE_VALIDATION_STACK,
        "malformed semantic scene stack did not fail transactionally");
    scene_metrics.struct_size = sizeof(scene_metrics);
    require(progpu_native_engine_update_scene(
        engine,
        next_semantic_scene.data(),
        next_semantic_scene.size(),
        &scene_metrics) == PROGPU_NATIVE_STATUS_SUCCESS &&
        (scene_metrics.flags &
            PROGPU_NATIVE_SCENE_METRICS_SNAPSHOT_REUSED) != 0U,
        "failed semantic update mutated the retained snapshot");

    webscene_gpu_canvas_configuration canvas_configuration{};
    canvas_configuration.struct_size = sizeof(canvas_configuration);
    canvas_configuration.device = reinterpret_cast<std::uintptr_t>(device);
    canvas_configuration.usage = WGPUTextureUsage_RenderAttachment |
        WGPUTextureUsage_CopySrc;
    canvas_configuration.pixel_format =
        WEBSCENE_GPU_PIXEL_FORMAT_BGRA8_UNORM;
    canvas_configuration.alpha_mode =
        WEBSCENE_GPU_ALPHA_MODE_PREMULTIPLIED;
    canvas_configuration.buffer_count = 3U;
    webscene_gpu_canvas* canvas = api.create_canvas(
        provider, &canvas_configuration, 64U, 48U);
    require(canvas != nullptr, "canvas creation failed");

    std::uintptr_t texture_handle{};
    require(api.acquire(provider, canvas, &texture_handle) ==
        WEBSCENE_GPU_STATUS_SUCCESS && texture_handle != 0U,
        "canvas texture acquisition failed");
    auto texture = reinterpret_cast<WGPUTexture>(texture_handle);
    WGPUTextureViewDescriptor view_descriptor =
        WGPU_TEXTURE_VIEW_DESCRIPTOR_INIT;
    WGPUTextureView view = resolve<WGPUProcTextureCreateView>(
        api, provider, "wgpuTextureCreateView")(
        texture, &view_descriptor);
    require(view != nullptr, "target view creation failed");

    auto renderable_scene = create_renderable_semantic_scene_stream(3U);
    scene_metrics.struct_size = sizeof(scene_metrics);
    require(progpu_native_engine_update_scene(
        engine,
        renderable_scene.data(),
        renderable_scene.size(),
        &scene_metrics) == PROGPU_NATIVE_STATUS_SUCCESS &&
        scene_metrics.draw_count == 10U &&
        scene_metrics.resource_count == 12U,
        "renderable semantic scene update failed");
    scene_metrics.struct_size = sizeof(scene_metrics);
    require(progpu_native_engine_update_scene(
        engine,
        renderable_scene.data(),
        renderable_scene.size(),
        &scene_metrics) == PROGPU_NATIVE_STATUS_SUCCESS &&
        (scene_metrics.flags &
            PROGPU_NATIVE_SCENE_METRICS_SNAPSHOT_REUSED) != 0U,
        "renderable semantic snapshot was not retained");
    progpu_native_scene_frame semantic_frame{};
    semantic_frame.struct_size = sizeof(semantic_frame);
    semantic_frame.width = 64U;
    semantic_frame.height = 48U;
    semantic_frame.dpi_scale = 1.0F;
    semantic_frame.target_view = reinterpret_cast<std::uintptr_t>(view);
    semantic_frame.clear_color = {0.02F, 0.03F, 0.04F, 1.0F};
    semantic_frame.scene_id = 91U;
    semantic_frame.generation = 3U;
    progpu_native_scene_frame_metrics semantic_metrics{};
    semantic_metrics.struct_size = sizeof(semantic_metrics);
    const auto semantic_status = progpu_native_engine_render_scene(
        engine,
        &semantic_frame,
        &semantic_metrics);
    if (semantic_status != PROGPU_NATIVE_STATUS_SUCCESS ||
        semantic_metrics.command_count != 12U ||
        semantic_metrics.draw_call_count != 9U ||
        semantic_metrics.family_switch_count != 8U ||
        semantic_metrics.submission_count != 1U ||
        semantic_metrics.payload_hash == 0U) {
        std::array<char, 512U> semantic_error{};
        progpu_native_engine_get_last_error(
            engine, semantic_error.data(), semantic_error.size());
        std::fprintf(stderr,
            "semantic status=%u commands=%u draws=%u families=%u "
            "submissions=%llu hash=%llu error=%s\n",
            static_cast<unsigned>(semantic_status),
            semantic_metrics.command_count,
            semantic_metrics.draw_call_count,
            semantic_metrics.family_switch_count,
            static_cast<unsigned long long>(
                semantic_metrics.submission_count),
            static_cast<unsigned long long>(semantic_metrics.payload_hash),
            semantic_error.data());
    }
    require(semantic_status == PROGPU_NATIVE_STATUS_SUCCESS &&
        semantic_metrics.command_count == 12U &&
        semantic_metrics.draw_call_count == 9U &&
        semantic_metrics.family_switch_count == 8U &&
        semantic_metrics.submission_count == 1U &&
        semantic_metrics.text_style_upload_bytes ==
            3U * sizeof(progpu_native_scene_text_style) &&
        semantic_metrics.payload_hash != 0U,
        "mixed semantic scene rendering failed");
    const std::uint64_t semantic_payload_hash =
        semantic_metrics.payload_hash;
    semantic_metrics = {};
    semantic_metrics.struct_size = sizeof(semantic_metrics);
    require(progpu_native_engine_render_scene(
        engine,
        &semantic_frame,
        &semantic_metrics) == PROGPU_NATIVE_STATUS_SUCCESS &&
        semantic_metrics.command_count == 12U &&
        semantic_metrics.draw_call_count == 9U &&
        semantic_metrics.family_switch_count == 8U &&
        semantic_metrics.submission_count == 1U &&
        semantic_metrics.vertex_upload_bytes == 0U &&
        semantic_metrics.index_upload_bytes == 0U &&
        semantic_metrics.texture_upload_bytes == 0U &&
        semantic_metrics.coverage_staging_bytes == 0U &&
        semantic_metrics.text_style_upload_bytes == 0U &&
        semantic_metrics.payload_hash == semantic_payload_hash,
        "stable mixed semantic scene replay rebuilt retained resources");
    require(progpu_native_engine_mark_device_lost(engine) ==
        PROGPU_NATIVE_STATUS_SUCCESS,
        "native device-loss notification failed");
    require(progpu_native_engine_mark_device_lost(engine) ==
        PROGPU_NATIVE_STATUS_SUCCESS,
        "native device-loss notification was not idempotent");
    require(progpu_native_engine_render_scene(
        engine,
        &semantic_frame,
        &semantic_metrics) == PROGPU_NATIVE_STATUS_DEVICE_LOST,
        "lost native engine did not fail closed");
    auto loss_updated_scene = renderable_scene;
    progpu_native_scene_header loss_updated_header{};
    std::memcpy(
        &loss_updated_header,
        loss_updated_scene.data(),
        sizeof(loss_updated_header));
    loss_updated_header.scene_id = 191U;
    loss_updated_header.generation = 1U;
    std::memcpy(
        loss_updated_scene.data(),
        &loss_updated_header,
        sizeof(loss_updated_header));
    scene_metrics = {};
    scene_metrics.struct_size = sizeof(scene_metrics);
    require(progpu_native_engine_update_scene(
        engine,
        loss_updated_scene.data(),
        loss_updated_scene.size(),
        &scene_metrics) == PROGPU_NATIVE_STATUS_SUCCESS,
        "CPU-only scene replacement after device loss failed");
    semantic_frame.scene_id = 191U;
    semantic_frame.generation = 1U;
    progpu_native_engine* replacement_engine{};
    require(progpu_native_dawn_engine_recreate(
        engine,
        &engine_options,
        &replacement_engine) == PROGPU_NATIVE_STATUS_SUCCESS &&
        replacement_engine != nullptr,
        "native Dawn engine recreation failed");
    progpu_native_engine_destroy(engine);
    engine = replacement_engine;
    semantic_metrics = {};
    semantic_metrics.struct_size = sizeof(semantic_metrics);
    require(progpu_native_engine_render_scene(
        engine,
        &semantic_frame,
        &semantic_metrics) == PROGPU_NATIVE_STATUS_SUCCESS &&
        semantic_metrics.payload_hash != 0U &&
        semantic_metrics.payload_hash != semantic_payload_hash &&
        semantic_metrics.vertex_upload_bytes != 0U &&
        semantic_metrics.text_style_upload_bytes ==
            3U * sizeof(progpu_native_scene_text_style),
        "replacement Dawn engine did not rebuild retained GPU state");
    const std::uint64_t replacement_payload_hash =
        semantic_metrics.payload_hash;
    semantic_metrics = {};
    semantic_metrics.struct_size = sizeof(semantic_metrics);
    require(progpu_native_engine_render_scene(
        engine,
        &semantic_frame,
        &semantic_metrics) == PROGPU_NATIVE_STATUS_SUCCESS &&
        semantic_metrics.payload_hash == replacement_payload_hash &&
        semantic_metrics.vertex_upload_bytes == 0U &&
        semantic_metrics.index_upload_bytes == 0U &&
        semantic_metrics.texture_upload_bytes == 0U &&
        semantic_metrics.uniform_upload_bytes == 0U &&
        semantic_metrics.text_style_upload_bytes == 0U,
        "stable Dawn replay after device recovery rebuilt resources");
    std::uint64_t semantic_submission{};
    require(progpu_native_engine_get_last_submission(
        engine,
        &semantic_submission) == PROGPU_NATIVE_STATUS_SUCCESS &&
        semantic_submission != 0U,
        "semantic scene submission token unavailable");

    auto color_glyph_scene = create_semantic_color_glyph_scene_stream(
        semantic_frame.width,
        semantic_frame.height);
    scene_metrics = {};
    scene_metrics.struct_size = sizeof(scene_metrics);
    require(progpu_native_engine_update_scene(
        engine,
        color_glyph_scene.data(),
        color_glyph_scene.size(),
        &scene_metrics) == PROGPU_NATIVE_STATUS_SUCCESS,
        "retained color-glyph scene update failed");
    semantic_frame.scene_id = 97U;
    semantic_frame.generation = 1U;
    semantic_metrics = {};
    semantic_metrics.struct_size = sizeof(semantic_metrics);
    require(progpu_native_engine_render_scene(
        engine,
        &semantic_frame,
        &semantic_metrics) == PROGPU_NATIVE_STATUS_SUCCESS &&
        semantic_metrics.draw_call_count == 3U &&
        semantic_metrics.color_glyph_upload_bytes == 16U,
        "retained color-glyph WebGPU render failed");
    semantic_metrics = {};
    semantic_metrics.struct_size = sizeof(semantic_metrics);
    require(progpu_native_engine_render_scene(
        engine,
        &semantic_frame,
        &semantic_metrics) == PROGPU_NATIVE_STATUS_SUCCESS &&
        semantic_metrics.color_glyph_upload_bytes == 0U &&
        semantic_metrics.vertex_upload_bytes == 0U &&
        semantic_metrics.coverage_staging_bytes == 0U,
        "stable retained color-glyph replay rebuilt GPU resources");
    resolve<WGPUProcTextureViewRelease>(
        api, provider, "wgpuTextureViewRelease")(view);
    resolve<WGPUProcTextureRelease>(
        api, provider, "wgpuTextureRelease")(texture);
    webscene_gpu_external_texture color_external{};
    color_external.struct_size = sizeof(color_external);
    require(api.present(provider, canvas, &color_external) ==
            WEBSCENE_GPU_STATUS_SUCCESS &&
        color_external.handle_kind == WEBSCENE_GPU_HANDLE_IOSURFACE &&
        (color_external.flags &
            WEBSCENE_GPU_EXTERNAL_TEXTURE_GPU_COMPLETE) != 0U,
        "color-glyph scene presentation failed");
    verify_semantic_color_glyph_scene(
        reinterpret_cast<IOSurfaceRef>(color_external.shared_handle),
        "progpu-native-semantic-color-glyph.ppm");
    api.release_external(provider, &color_external);
    api.destroy_canvas(provider, canvas);

    canvas = api.create_canvas(
        provider, &canvas_configuration, 64U, 48U);
    require(canvas != nullptr,
        "semantic restore canvas creation failed");
    texture_handle = 0U;
    require(api.acquire(provider, canvas, &texture_handle) ==
            WEBSCENE_GPU_STATUS_SUCCESS && texture_handle != 0U,
        "semantic restore texture acquisition failed");
    texture = reinterpret_cast<WGPUTexture>(texture_handle);
    view = resolve<WGPUProcTextureCreateView>(
        api, provider, "wgpuTextureCreateView")(
        texture, &view_descriptor);
    require(view != nullptr, "semantic restore target view creation failed");
    semantic_frame.target_view = reinterpret_cast<std::uintptr_t>(view);

    scene_metrics = {};
    scene_metrics.struct_size = sizeof(scene_metrics);
    require(progpu_native_engine_update_scene(
        engine,
        renderable_scene.data(),
        renderable_scene.size(),
        &scene_metrics) == PROGPU_NATIVE_STATUS_SUCCESS,
        "mixed semantic scene restore after color-glyph test failed");
    semantic_frame.scene_id = 91U;
    semantic_frame.generation = 3U;
    semantic_metrics = {};
    semantic_metrics.struct_size = sizeof(semantic_metrics);
    require(progpu_native_engine_render_scene(
        engine,
        &semantic_frame,
        &semantic_metrics) == PROGPU_NATIVE_STATUS_SUCCESS,
        "mixed semantic scene rerender after color-glyph test failed");
    require(progpu_native_engine_get_last_submission(
        engine,
        &semantic_submission) == PROGPU_NATIVE_STATUS_SUCCESS &&
        semantic_submission != 0U,
        "semantic restore submission token unavailable");

    auto invalid_style_scene = create_renderable_semantic_scene_stream(4U);
    progpu_native_scene_header invalid_style_header{};
    std::memcpy(
        &invalid_style_header,
        invalid_style_scene.data(),
        sizeof(invalid_style_header));
    progpu_native_scene_resource invalid_style_resource{};
    std::memcpy(
        &invalid_style_resource,
        invalid_style_scene.data() + invalid_style_header.resource_offset +
            10U * invalid_style_header.resource_stride,
        sizeof(invalid_style_resource));
    progpu_native_scene_text_style invalid_style{};
    std::memcpy(
        &invalid_style,
        invalid_style_scene.data() + invalid_style_resource.payload_offset,
        sizeof(invalid_style));
    invalid_style.color.a = std::numeric_limits<float>::quiet_NaN();
    std::memcpy(
        invalid_style_scene.data() + invalid_style_resource.payload_offset,
        &invalid_style,
        sizeof(invalid_style));
    scene_metrics = {};
    scene_metrics.struct_size = sizeof(scene_metrics);
    require(progpu_native_engine_update_scene(
        engine,
        invalid_style_scene.data(),
        invalid_style_scene.size(),
        &scene_metrics) == PROGPU_NATIVE_STATUS_INVALID_ARGUMENT &&
        scene_metrics.validation_error ==
            PROGPU_NATIVE_SCENE_VALIDATION_VALUE,
        "non-finite retained text style was accepted");
    auto invalid_value_scene = create_renderable_semantic_scene_stream(4U);
    progpu_native_scene_header invalid_header{};
    std::memcpy(
        &invalid_header,
        invalid_value_scene.data(),
        sizeof(invalid_header));
    progpu_native_scene_command invalid_image_command{};
    std::memcpy(
        &invalid_image_command,
        invalid_value_scene.data() + invalid_header.command_offset +
            3U * invalid_header.command_stride,
        sizeof(invalid_image_command));
    progpu_native_scene_image_draw invalid_image{};
    std::memcpy(
        &invalid_image,
        invalid_value_scene.data() + invalid_image_command.payload_offset,
        sizeof(invalid_image));
    invalid_image.opacity = std::numeric_limits<float>::quiet_NaN();
    std::memcpy(
        invalid_value_scene.data() + invalid_image_command.payload_offset,
        &invalid_image,
        sizeof(invalid_image));
    scene_metrics = {};
    scene_metrics.struct_size = sizeof(scene_metrics);
    require(progpu_native_engine_update_scene(
        engine,
        invalid_value_scene.data(),
        invalid_value_scene.size(),
        &scene_metrics) == PROGPU_NATIVE_STATUS_SUCCESS,
        "structurally valid semantic value-preflight scene was rejected");
    progpu_native_scene_frame invalid_frame = semantic_frame;
    invalid_frame.generation = 4U;
    semantic_metrics = {};
    semantic_metrics.struct_size = sizeof(semantic_metrics);
    require(progpu_native_engine_render_scene(
        engine,
        &invalid_frame,
        &semantic_metrics) == PROGPU_NATIVE_STATUS_INVALID_ARGUMENT &&
        semantic_metrics.submission_count == 0U,
        "semantic value preflight did not fail before submission");
    std::uint64_t submission_after_invalid{};
    require(progpu_native_engine_get_last_submission(
        engine,
        &submission_after_invalid) == PROGPU_NATIVE_STATUS_SUCCESS &&
        submission_after_invalid == semantic_submission,
        "failed semantic value preflight mutated the submission timeline");
    std::uint8_t semantic_complete{};
    require(progpu_native_engine_poll_submission(
        engine,
        semantic_submission,
        1U,
        &semantic_complete) == PROGPU_NATIVE_STATUS_SUCCESS &&
        semantic_complete != 0U,
        "mixed semantic scene did not reach GPU completion");

    auto over_budget_scene = create_over_budget_semantic_scene_stream();
    scene_metrics = {};
    scene_metrics.struct_size = sizeof(scene_metrics);
    require(progpu_native_engine_update_scene(
        engine,
        over_budget_scene.data(),
        over_budget_scene.size(),
        &scene_metrics) == PROGPU_NATIVE_STATUS_SUCCESS,
        "structurally valid over-budget semantic scene was rejected early");
    progpu_native_scene_frame over_budget_frame = semantic_frame;
    over_budget_frame.scene_id = 92U;
    over_budget_frame.generation = 1U;
    semantic_metrics = {};
    semantic_metrics.struct_size = sizeof(semantic_metrics);
    require(progpu_native_engine_render_scene(
        engine,
        &over_budget_frame,
        &semantic_metrics) == PROGPU_NATIVE_STATUS_OUT_OF_MEMORY &&
        semantic_metrics.submission_count == 0U,
        "semantic compilation budget did not fail before submission");
    std::uint64_t submission_after_budget_failure{};
    require(progpu_native_engine_get_last_submission(
        engine,
        &submission_after_budget_failure) == PROGPU_NATIVE_STATUS_SUCCESS &&
        submission_after_budget_failure == semantic_submission,
        "semantic compilation budget failure mutated the submission timeline");

    auto layer_scene = create_semantic_layer_scene_stream();
    scene_metrics = {};
    scene_metrics.struct_size = sizeof(scene_metrics);
    require(progpu_native_engine_update_scene(
        engine,
        layer_scene.data(),
        layer_scene.size(),
        &scene_metrics) == PROGPU_NATIVE_STATUS_SUCCESS &&
        scene_metrics.command_count == 2U &&
        scene_metrics.draw_count == 0U &&
        scene_metrics.maximum_stack_depth == 1U &&
        scene_metrics.payload_bytes == sizeof(progpu_native_scene_layer),
        "typed semantic layer descriptor update failed");
    progpu_native_scene_frame layer_frame = semantic_frame;
    layer_frame.scene_id = 93U;
    layer_frame.generation = 1U;
    layer_frame.width = 65536U;
    layer_frame.height = 65536U;
    semantic_metrics = {};
    semantic_metrics.struct_size = sizeof(semantic_metrics);
    require(progpu_native_engine_render_scene(
        engine,
        &layer_frame,
        &semantic_metrics) == PROGPU_NATIVE_STATUS_OUT_OF_MEMORY &&
        semantic_metrics.submission_count == 0U,
        "semantic layer pixel budget did not fail before submission");
    layer_frame.width = std::numeric_limits<std::uint32_t>::max();
    layer_frame.height = std::numeric_limits<std::uint32_t>::max();
    semantic_metrics = {};
    semantic_metrics.struct_size = sizeof(semantic_metrics);
    require(progpu_native_engine_render_scene(
        engine,
        &layer_frame,
        &semantic_metrics) == PROGPU_NATIVE_STATUS_OUT_OF_MEMORY &&
        semantic_metrics.submission_count == 0U,
        "semantic layer pixel-budget arithmetic did not fail closed");
    std::uint64_t submission_after_layer_failure{};
    require(progpu_native_engine_get_last_submission(
        engine,
        &submission_after_layer_failure) == PROGPU_NATIVE_STATUS_SUCCESS &&
        submission_after_layer_failure == semantic_submission,
        "semantic layer preflight mutated the submission timeline");

    resolve<WGPUProcTextureViewRelease>(
        api, provider, "wgpuTextureViewRelease")(view);
    resolve<WGPUProcTextureRelease>(
        api, provider, "wgpuTextureRelease")(texture);
    webscene_gpu_external_texture semantic_external{};
    semantic_external.struct_size = sizeof(semantic_external);
    require(api.present(provider, canvas, &semantic_external) ==
            WEBSCENE_GPU_STATUS_SUCCESS &&
        semantic_external.handle_kind == WEBSCENE_GPU_HANDLE_IOSURFACE &&
        (semantic_external.flags &
            WEBSCENE_GPU_EXTERNAL_TEXTURE_GPU_COMPLETE) != 0U,
        "semantic scene presentation failed");
    verify_semantic_scene(
        reinterpret_cast<IOSurfaceRef>(semantic_external.shared_handle),
        "progpu-native-semantic-scene.ppm");
    api.release_external(provider, &semantic_external);
    api.destroy_canvas(provider, canvas);

    canvas = api.create_canvas(
        provider, &canvas_configuration, 64U, 48U);
    require(canvas != nullptr, "semantic layer canvas creation failed");
    texture_handle = 0U;
    require(api.acquire(provider, canvas, &texture_handle) ==
            WEBSCENE_GPU_STATUS_SUCCESS && texture_handle != 0U,
        "semantic layer canvas texture acquisition failed");
    texture = reinterpret_cast<WGPUTexture>(texture_handle);
    view = resolve<WGPUProcTextureCreateView>(
        api, provider, "wgpuTextureCreateView")(
        texture, &view_descriptor);
    require(view != nullptr, "semantic layer target view creation failed");

    auto opacity_layer_scene =
        create_semantic_opacity_layer_scene_stream();
    scene_metrics = {};
    scene_metrics.struct_size = sizeof(scene_metrics);
    require(progpu_native_engine_update_scene(
        engine,
        opacity_layer_scene.data(),
        opacity_layer_scene.size(),
        &scene_metrics) == PROGPU_NATIVE_STATUS_SUCCESS &&
        scene_metrics.command_count == 13U &&
        scene_metrics.draw_count == 5U &&
        scene_metrics.maximum_stack_depth == 2U,
        "nested semantic opacity-layer update failed");
    progpu_native_scene_frame opacity_layer_frame = semantic_frame;
    opacity_layer_frame.target_view =
        reinterpret_cast<std::uintptr_t>(view);
    opacity_layer_frame.scene_id = 94U;
    opacity_layer_frame.generation = 1U;
    semantic_metrics = {};
    semantic_metrics.struct_size = sizeof(semantic_metrics);
    require(progpu_native_engine_render_scene(
        engine,
        &opacity_layer_frame,
        &semantic_metrics) == PROGPU_NATIVE_STATUS_SUCCESS &&
        semantic_metrics.command_count == 13U &&
        semantic_metrics.draw_call_count == 8U &&
        semantic_metrics.submission_count == 1U &&
        semantic_metrics.vertex_upload_bytes != 0U,
        "nested semantic opacity-layer rendering failed");
    progpu_native_layer_metrics semantic_layer_metrics{};
    semantic_layer_metrics.struct_size = sizeof(semantic_layer_metrics);
    require(progpu_native_engine_get_layer_metrics(
        engine,
        &semantic_layer_metrics) == PROGPU_NATIVE_STATUS_SUCCESS &&
        semantic_layer_metrics.texture_width == 28U &&
        semantic_layer_metrics.texture_height == 16U &&
        semantic_layer_metrics.allocation_count >= 2U &&
        semantic_layer_metrics.content_pass_count == 3U &&
        semantic_layer_metrics.composite_pass_count == 3U &&
        semantic_layer_metrics.cache_hit == 0U &&
        semantic_layer_metrics.texture_bytes == 2816U &&
        semantic_layer_metrics.vertex_upload_bytes != 0U,
        "semantic opacity-layer allocation metrics are incorrect");
    const std::uint32_t semantic_layer_allocation_count =
        semantic_layer_metrics.allocation_count;
    const std::uint64_t opacity_layer_payload_hash =
        semantic_metrics.payload_hash;
    {
        const progpu_native_rect interleaved_rectangle{
            2.0F,
            2.0F,
            8.0F,
            8.0F,
            {0.2F, 0.4F, 0.8F, 1.0F}};
        progpu_native_draw_state interleaved_state{};
        interleaved_state.struct_size = sizeof(interleaved_state);
        interleaved_state.opacity = 1.0F;
        interleaved_state.group_opacity = 0.5F;
        interleaved_state.group_revision = 77U;
        interleaved_state.group_blend_mode =
            PROGPU_NATIVE_BLEND_SRC_OVER;
        progpu_native_frame interleaved_frame{};
        interleaved_frame.struct_size = sizeof(interleaved_frame);
        interleaved_frame.width = 32U;
        interleaved_frame.height = 24U;
        interleaved_frame.dpi_scale = 1.0F;
        interleaved_frame.target_view =
            reinterpret_cast<std::uintptr_t>(view);
        interleaved_frame.clear_color = semantic_frame.clear_color;
        interleaved_frame.rects = &interleaved_rectangle;
        interleaved_frame.rect_count = 1U;
        interleaved_frame.draw_state = &interleaved_state;
        progpu_native_frame_metrics interleaved_metrics{};
        interleaved_metrics.struct_size = sizeof(interleaved_metrics);
        require(progpu_native_engine_render(
            engine,
            &interleaved_frame,
            &interleaved_metrics) == PROGPU_NATIVE_STATUS_SUCCESS &&
            interleaved_metrics.draw_call_count == 1U,
            "interleaved frame-group render failed");
    }
    semantic_metrics = {};
    semantic_metrics.struct_size = sizeof(semantic_metrics);
    require(progpu_native_engine_render_scene(
        engine,
        &opacity_layer_frame,
        &semantic_metrics) == PROGPU_NATIVE_STATUS_SUCCESS &&
        semantic_metrics.command_count == 13U &&
        semantic_metrics.draw_call_count == 8U &&
        semantic_metrics.submission_count == 1U &&
        semantic_metrics.vertex_upload_bytes != 0U &&
        semantic_metrics.index_upload_bytes == 0U &&
        semantic_metrics.texture_upload_bytes == 0U &&
        semantic_metrics.uniform_upload_bytes != 0U &&
        semantic_metrics.coverage_staging_bytes == 0U &&
        semantic_metrics.payload_hash == opacity_layer_payload_hash,
        "interleaved semantic opacity-layer rebuild did not restore state");
    semantic_layer_metrics = {};
    semantic_layer_metrics.struct_size = sizeof(semantic_layer_metrics);
    require(progpu_native_engine_get_layer_metrics(
        engine,
        &semantic_layer_metrics) == PROGPU_NATIVE_STATUS_SUCCESS &&
        semantic_layer_metrics.allocation_count ==
            semantic_layer_allocation_count &&
        semantic_layer_metrics.content_pass_count == 3U &&
        semantic_layer_metrics.composite_pass_count == 3U &&
        semantic_layer_metrics.cache_hit == 0U &&
        semantic_layer_metrics.texture_bytes == 2816U &&
        semantic_layer_metrics.vertex_upload_bytes != 0U &&
        semantic_layer_metrics.uniform_upload_bytes != 0U,
        "interleaved semantic opacity-layer metrics are incorrect");
    semantic_metrics = {};
    semantic_metrics.struct_size = sizeof(semantic_metrics);
    require(progpu_native_engine_render_scene(
        engine,
        &opacity_layer_frame,
        &semantic_metrics) == PROGPU_NATIVE_STATUS_SUCCESS &&
        semantic_metrics.command_count == 13U &&
        semantic_metrics.draw_call_count == 8U &&
        semantic_metrics.submission_count == 1U &&
        semantic_metrics.vertex_upload_bytes == 0U &&
        semantic_metrics.index_upload_bytes == 0U &&
        semantic_metrics.texture_upload_bytes == 0U &&
        semantic_metrics.uniform_upload_bytes == 0U &&
        semantic_metrics.coverage_staging_bytes == 0U &&
        semantic_metrics.payload_hash == opacity_layer_payload_hash,
        "stable semantic opacity-layer replay rebuilt retained resources");
    semantic_layer_metrics = {};
    semantic_layer_metrics.struct_size = sizeof(semantic_layer_metrics);
    require(progpu_native_engine_get_layer_metrics(
        engine,
        &semantic_layer_metrics) == PROGPU_NATIVE_STATUS_SUCCESS &&
        semantic_layer_metrics.allocation_count ==
            semantic_layer_allocation_count &&
        semantic_layer_metrics.content_pass_count == 3U &&
        semantic_layer_metrics.composite_pass_count == 3U &&
        semantic_layer_metrics.cache_hit == 1U &&
        semantic_layer_metrics.texture_bytes == 2816U &&
        semantic_layer_metrics.vertex_upload_bytes == 0U &&
        semantic_layer_metrics.uniform_upload_bytes == 0U,
        "stable semantic opacity-layer metrics did not report retained reuse");
    std::uint64_t opacity_layer_submission{};
    require(progpu_native_engine_get_last_submission(
        engine,
        &opacity_layer_submission) == PROGPU_NATIVE_STATUS_SUCCESS &&
        opacity_layer_submission > semantic_submission,
        "semantic opacity-layer submission token unavailable");
    std::uint8_t opacity_layer_complete{};
    require(progpu_native_engine_poll_submission(
        engine,
        opacity_layer_submission,
        1U,
        &opacity_layer_complete) == PROGPU_NATIVE_STATUS_SUCCESS &&
        opacity_layer_complete != 0U,
        "semantic opacity-layer scene did not reach GPU completion");
    resolve<WGPUProcTextureViewRelease>(
        api, provider, "wgpuTextureViewRelease")(view);
    resolve<WGPUProcTextureRelease>(
        api, provider, "wgpuTextureRelease")(texture);
    webscene_gpu_external_texture opacity_layer_external{};
    opacity_layer_external.struct_size = sizeof(opacity_layer_external);
    require(api.present(provider, canvas, &opacity_layer_external) ==
            WEBSCENE_GPU_STATUS_SUCCESS &&
        opacity_layer_external.handle_kind ==
            WEBSCENE_GPU_HANDLE_IOSURFACE &&
        (opacity_layer_external.flags &
            WEBSCENE_GPU_EXTERNAL_TEXTURE_GPU_COMPLETE) != 0U,
        "semantic opacity-layer presentation failed");
    verify_semantic_layer_scene(
        reinterpret_cast<IOSurfaceRef>(
            opacity_layer_external.shared_handle),
        "progpu-native-semantic-layers.ppm");
    api.release_external(provider, &opacity_layer_external);
    api.destroy_canvas(provider, canvas);

    canvas = api.create_canvas(
        provider, &canvas_configuration, 64U, 48U);
    require(canvas != nullptr,
        "semantic advanced-blend canvas creation failed");
    texture_handle = 0U;
    require(api.acquire(provider, canvas, &texture_handle) ==
            WEBSCENE_GPU_STATUS_SUCCESS && texture_handle != 0U,
        "semantic advanced-blend canvas texture acquisition failed");
    texture = reinterpret_cast<WGPUTexture>(texture_handle);
    view = resolve<WGPUProcTextureCreateView>(
        api, provider, "wgpuTextureCreateView")(
        texture, &view_descriptor);
    require(view != nullptr,
        "semantic advanced-blend target view creation failed");

    auto advanced_blend_scene =
        create_semantic_advanced_blend_scene_stream();
    scene_metrics = {};
    scene_metrics.struct_size = sizeof(scene_metrics);
    require(progpu_native_engine_update_scene(
        engine,
        advanced_blend_scene.data(),
        advanced_blend_scene.size(),
        &scene_metrics) == PROGPU_NATIVE_STATUS_SUCCESS &&
        scene_metrics.command_count == 4U &&
        scene_metrics.resource_count == 2U &&
        scene_metrics.draw_count == 2U &&
        scene_metrics.maximum_stack_depth == 1U,
        "semantic advanced-blend scene update failed");
    progpu_native_scene_frame advanced_blend_frame = semantic_frame;
    advanced_blend_frame.target_view =
        reinterpret_cast<std::uintptr_t>(view);
    advanced_blend_frame.scene_id = 97U;
    advanced_blend_frame.generation = 1U;
    semantic_metrics = {};
    semantic_metrics.struct_size = sizeof(semantic_metrics);
    require(progpu_native_engine_render_scene(
        engine,
        &advanced_blend_frame,
        &semantic_metrics) == PROGPU_NATIVE_STATUS_SUCCESS &&
        semantic_metrics.draw_call_count == 6U &&
        semantic_metrics.submission_count == 1U &&
        semantic_metrics.vertex_upload_bytes != 0U,
        "semantic destination-aware blend rendering failed");
    semantic_layer_metrics = {};
    semantic_layer_metrics.struct_size = sizeof(semantic_layer_metrics);
    require(progpu_native_engine_get_layer_metrics(
        engine,
        &semantic_layer_metrics) == PROGPU_NATIVE_STATUS_SUCCESS &&
        semantic_layer_metrics.content_pass_count == 1U &&
        semantic_layer_metrics.composite_pass_count == 1U &&
        semantic_layer_metrics.cache_hit == 0U,
        "semantic destination-aware blend metrics are incorrect");
    std::uint64_t advanced_blend_submission{};
    require(progpu_native_engine_get_last_submission(
        engine,
        &advanced_blend_submission) == PROGPU_NATIVE_STATUS_SUCCESS,
        "semantic advanced-blend submission token unavailable");
    std::uint8_t advanced_blend_complete{};
    require(progpu_native_engine_poll_submission(
        engine,
        advanced_blend_submission,
        1U,
        &advanced_blend_complete) == PROGPU_NATIVE_STATUS_SUCCESS &&
        advanced_blend_complete != 0U,
        "semantic advanced-blend scene did not reach GPU completion");
    resolve<WGPUProcTextureViewRelease>(
        api, provider, "wgpuTextureViewRelease")(view);
    resolve<WGPUProcTextureRelease>(
        api, provider, "wgpuTextureRelease")(texture);
    webscene_gpu_external_texture advanced_blend_external{};
    advanced_blend_external.struct_size =
        sizeof(advanced_blend_external);
    require(api.present(provider, canvas, &advanced_blend_external) ==
            WEBSCENE_GPU_STATUS_SUCCESS &&
        advanced_blend_external.handle_kind ==
            WEBSCENE_GPU_HANDLE_IOSURFACE &&
        (advanced_blend_external.flags &
            WEBSCENE_GPU_EXTERNAL_TEXTURE_GPU_COMPLETE) != 0U,
        "semantic advanced-blend presentation failed");
    verify_semantic_advanced_blend_scene(
        reinterpret_cast<IOSurfaceRef>(
            advanced_blend_external.shared_handle),
        "progpu-native-semantic-advanced-blend.ppm");
    api.release_external(provider, &advanced_blend_external);
    api.destroy_canvas(provider, canvas);

    canvas = api.create_canvas(
        provider, &canvas_configuration, 64U, 48U);
    require(canvas != nullptr,
        "semantic backdrop canvas creation failed");
    texture_handle = 0U;
    require(api.acquire(provider, canvas, &texture_handle) ==
            WEBSCENE_GPU_STATUS_SUCCESS && texture_handle != 0U,
        "semantic backdrop canvas texture acquisition failed");
    texture = reinterpret_cast<WGPUTexture>(texture_handle);
    view = resolve<WGPUProcTextureCreateView>(
        api, provider, "wgpuTextureCreateView")(
        texture, &view_descriptor);
    require(view != nullptr,
        "semantic backdrop target view creation failed");

    auto backdrop_scene = create_semantic_backdrop_scene_stream();
    scene_metrics = {};
    scene_metrics.struct_size = sizeof(scene_metrics);
    require(progpu_native_engine_update_scene(
        engine,
        backdrop_scene.data(),
        backdrop_scene.size(),
        &scene_metrics) == PROGPU_NATIVE_STATUS_SUCCESS &&
        scene_metrics.command_count == 6U &&
        scene_metrics.resource_count == 4U &&
        scene_metrics.draw_count == 2U &&
        scene_metrics.maximum_stack_depth == 1U,
        "semantic backdrop scene update failed");
    progpu_native_scene_frame backdrop_frame = semantic_frame;
    backdrop_frame.target_view = reinterpret_cast<std::uintptr_t>(view);
    backdrop_frame.scene_id = 98U;
    backdrop_frame.generation = 1U;
    semantic_metrics = {};
    semantic_metrics.struct_size = sizeof(semantic_metrics);
    require(progpu_native_engine_render_scene(
        engine,
        &backdrop_frame,
        &semantic_metrics) == PROGPU_NATIVE_STATUS_SUCCESS &&
        semantic_metrics.command_count == 6U &&
        semantic_metrics.draw_call_count == 6U &&
        semantic_metrics.submission_count == 1U &&
        semantic_metrics.vertex_upload_bytes != 0U &&
        semantic_metrics.uniform_upload_bytes != 0U &&
        semantic_metrics.brush_upload_bytes ==
            4U * sizeof(progpu_native_scene_brush) &&
        semantic_metrics.gradient_stop_upload_bytes ==
            3U * sizeof(progpu_native_scene_gradient_stop),
        "semantic backdrop rendering failed");
    semantic_layer_metrics = {};
    semantic_layer_metrics.struct_size = sizeof(semantic_layer_metrics);
    require(progpu_native_engine_get_layer_metrics(
        engine,
        &semantic_layer_metrics) == PROGPU_NATIVE_STATUS_SUCCESS &&
        semantic_layer_metrics.texture_width == 32U &&
        semantic_layer_metrics.texture_height == 32U &&
        semantic_layer_metrics.content_pass_count == 2U &&
        semantic_layer_metrics.composite_pass_count == 2U &&
        semantic_layer_metrics.effect_count == 1U &&
        semantic_layer_metrics.effect_pass_count == 2U &&
        semantic_layer_metrics.effect_cache_hit == 0U &&
        semantic_layer_metrics.texture_bytes == 16384U &&
        semantic_layer_metrics.effect_texture_bytes == 12288U,
        "semantic backdrop layer metrics are incorrect");
    semantic_metrics = {};
    semantic_metrics.struct_size = sizeof(semantic_metrics);
    require(progpu_native_engine_render_scene(
        engine,
        &backdrop_frame,
        &semantic_metrics) == PROGPU_NATIVE_STATUS_SUCCESS &&
        semantic_metrics.draw_call_count == 6U &&
        semantic_metrics.submission_count == 1U &&
        semantic_metrics.vertex_upload_bytes == 0U &&
        semantic_metrics.index_upload_bytes == 0U &&
        semantic_metrics.texture_upload_bytes == 0U &&
        semantic_metrics.uniform_upload_bytes == 0U &&
        semantic_metrics.coverage_staging_bytes == 0U &&
        semantic_metrics.brush_upload_bytes == 0U &&
        semantic_metrics.gradient_stop_upload_bytes == 0U,
        "stable semantic backdrop replay rebuilt retained resources");
    struct legacy_scene_frame_metrics_v3 {
        std::uint32_t struct_size;
        std::uint32_t command_count;
        std::uint32_t draw_call_count;
        std::uint32_t family_switch_count;
        std::uint64_t submission_count;
        std::uint64_t vertex_upload_bytes;
        std::uint64_t index_upload_bytes;
        std::uint64_t texture_upload_bytes;
        std::uint64_t uniform_upload_bytes;
        std::uint64_t coverage_staging_bytes;
        std::uint64_t payload_hash;
    };
    static_assert(sizeof(legacy_scene_frame_metrics_v3) == 72U);
    struct legacy_scene_frame_metrics_guard {
        legacy_scene_frame_metrics_v3 metrics{};
        std::uint64_t canary{0XABCD'0123'4567'89EFULL};
    } legacy_metrics{};
    legacy_metrics.metrics.struct_size =
        sizeof(legacy_scene_frame_metrics_v3);
    require(progpu_native_engine_render_scene(
        engine,
        &backdrop_frame,
        reinterpret_cast<progpu_native_scene_frame_metrics*>(
            &legacy_metrics.metrics)) == PROGPU_NATIVE_STATUS_SUCCESS &&
        legacy_metrics.metrics.command_count == 6U &&
        legacy_metrics.metrics.draw_call_count == 6U &&
        legacy_metrics.metrics.submission_count == 1U &&
        legacy_metrics.metrics.vertex_upload_bytes == 0U &&
        legacy_metrics.metrics.index_upload_bytes == 0U &&
        legacy_metrics.metrics.texture_upload_bytes == 0U &&
        legacy_metrics.metrics.uniform_upload_bytes == 0U &&
        legacy_metrics.metrics.coverage_staging_bytes == 0U &&
        legacy_metrics.metrics.payload_hash != 0U &&
        legacy_metrics.canary == 0XABCD'0123'4567'89EFULL,
        "legacy semantic frame metrics ABI was overwritten");
    struct legacy_scene_frame_metrics_v4 {
        legacy_scene_frame_metrics_v3 base{};
        std::uint64_t brush_upload_bytes{};
        std::uint64_t gradient_stop_upload_bytes{};
    };
    static_assert(sizeof(legacy_scene_frame_metrics_v4) == 88U);
    struct legacy_scene_frame_metrics_v4_guard {
        legacy_scene_frame_metrics_v4 metrics{};
        std::uint64_t canary{0X1020'3040'5060'7080ULL};
    } legacy_v4_metrics{};
    legacy_v4_metrics.metrics.base.struct_size =
        sizeof(legacy_scene_frame_metrics_v4);
    require(progpu_native_engine_render_scene(
        engine,
        &backdrop_frame,
        reinterpret_cast<progpu_native_scene_frame_metrics*>(
            &legacy_v4_metrics.metrics)) == PROGPU_NATIVE_STATUS_SUCCESS &&
        legacy_v4_metrics.metrics.base.command_count == 6U &&
        legacy_v4_metrics.metrics.base.draw_call_count == 6U &&
        legacy_v4_metrics.metrics.base.submission_count == 1U &&
        legacy_v4_metrics.metrics.brush_upload_bytes == 0U &&
        legacy_v4_metrics.metrics.gradient_stop_upload_bytes == 0U &&
        legacy_v4_metrics.canary == 0X1020'3040'5060'7080ULL,
        "pre-text-style semantic frame metrics ABI was overwritten");
    semantic_layer_metrics = {};
    semantic_layer_metrics.struct_size = sizeof(semantic_layer_metrics);
    require(progpu_native_engine_get_layer_metrics(
        engine,
        &semantic_layer_metrics) == PROGPU_NATIVE_STATUS_SUCCESS &&
        semantic_layer_metrics.cache_hit == 1U &&
        semantic_layer_metrics.effect_pass_count == 2U &&
        semantic_layer_metrics.effect_cache_hit == 0U,
        "stable semantic backdrop replay cached parent-dependent pixels");
    std::uint64_t backdrop_submission{};
    require(progpu_native_engine_get_last_submission(
        engine,
        &backdrop_submission) == PROGPU_NATIVE_STATUS_SUCCESS,
        "semantic backdrop submission token unavailable");
    std::uint8_t backdrop_complete{};
    require(progpu_native_engine_poll_submission(
        engine,
        backdrop_submission,
        1U,
        &backdrop_complete) == PROGPU_NATIVE_STATUS_SUCCESS &&
        backdrop_complete != 0U,
        "semantic backdrop scene did not reach GPU completion");
    resolve<WGPUProcTextureViewRelease>(
        api, provider, "wgpuTextureViewRelease")(view);
    resolve<WGPUProcTextureRelease>(
        api, provider, "wgpuTextureRelease")(texture);
    webscene_gpu_external_texture backdrop_external{};
    backdrop_external.struct_size = sizeof(backdrop_external);
    require(api.present(provider, canvas, &backdrop_external) ==
            WEBSCENE_GPU_STATUS_SUCCESS &&
        backdrop_external.handle_kind == WEBSCENE_GPU_HANDLE_IOSURFACE &&
        (backdrop_external.flags &
            WEBSCENE_GPU_EXTERNAL_TEXTURE_GPU_COMPLETE) != 0U,
        "semantic backdrop presentation failed");
    verify_semantic_backdrop_scene(
        reinterpret_cast<IOSurfaceRef>(backdrop_external.shared_handle),
        "progpu-native-semantic-backdrop.ppm");
    api.release_external(provider, &backdrop_external);
    api.destroy_canvas(provider, canvas);

    canvas = api.create_canvas(
        provider, &canvas_configuration, 64U, 48U);
    require(canvas != nullptr,
        "semantic masked layer canvas creation failed");
    texture_handle = 0U;
    require(api.acquire(provider, canvas, &texture_handle) ==
            WEBSCENE_GPU_STATUS_SUCCESS && texture_handle != 0U,
        "semantic masked layer canvas texture acquisition failed");
    texture = reinterpret_cast<WGPUTexture>(texture_handle);
    view = resolve<WGPUProcTextureCreateView>(
        api, provider, "wgpuTextureCreateView")(
        texture, &view_descriptor);
    require(view != nullptr,
        "semantic masked layer target view creation failed");

    auto masked_layer_scene =
        create_semantic_masked_layer_scene_stream();
    scene_metrics = {};
    scene_metrics.struct_size = sizeof(scene_metrics);
    require(progpu_native_engine_update_scene(
        engine,
        masked_layer_scene.data(),
        masked_layer_scene.size(),
        &scene_metrics) == PROGPU_NATIVE_STATUS_SUCCESS &&
        scene_metrics.command_count == 5U &&
        scene_metrics.resource_count == 2U &&
        scene_metrics.draw_count == 1U &&
        scene_metrics.maximum_stack_depth == 2U,
        "semantic masked layer update failed");
    progpu_native_scene_frame masked_layer_frame = semantic_frame;
    masked_layer_frame.target_view = reinterpret_cast<std::uintptr_t>(view);
    masked_layer_frame.scene_id = 95U;
    masked_layer_frame.generation = 1U;
    semantic_metrics = {};
    semantic_metrics.struct_size = sizeof(semantic_metrics);
    require(progpu_native_engine_render_scene(
        engine,
        &masked_layer_frame,
        &semantic_metrics) == PROGPU_NATIVE_STATUS_SUCCESS &&
        semantic_metrics.command_count == 5U &&
        semantic_metrics.draw_call_count == 3U &&
        semantic_metrics.submission_count == 1U &&
        semantic_metrics.uniform_upload_bytes != 0U,
        "semantic masked layer rendering failed");
    semantic_layer_metrics = {};
    semantic_layer_metrics.struct_size = sizeof(semantic_layer_metrics);
    require(progpu_native_engine_get_layer_metrics(
        engine,
        &semantic_layer_metrics) == PROGPU_NATIVE_STATUS_SUCCESS &&
        semantic_layer_metrics.texture_width == 40U &&
        semantic_layer_metrics.texture_height == 32U &&
        semantic_layer_metrics.content_pass_count == 2U &&
        semantic_layer_metrics.composite_pass_count == 2U &&
        semantic_layer_metrics.cache_hit == 0U &&
        semantic_layer_metrics.texture_bytes == 8192U &&
        semantic_layer_metrics.mask_kind ==
            PROGPU_NATIVE_GROUP_MASK_ROUNDED_RECTANGLE &&
        semantic_layer_metrics.mask_uniform_upload_bytes ==
            24U * sizeof(float),
        "semantic masked layer metrics are incorrect");
    const std::uint32_t masked_layer_allocation_count =
        semantic_layer_metrics.allocation_count;
    const std::uint32_t masked_bind_group_generation =
        semantic_layer_metrics.mask_bind_group_generation;
    semantic_metrics = {};
    semantic_metrics.struct_size = sizeof(semantic_metrics);
    require(progpu_native_engine_render_scene(
        engine,
        &masked_layer_frame,
        &semantic_metrics) == PROGPU_NATIVE_STATUS_SUCCESS &&
        semantic_metrics.submission_count == 1U &&
        semantic_metrics.vertex_upload_bytes == 0U &&
        semantic_metrics.index_upload_bytes == 0U &&
        semantic_metrics.texture_upload_bytes == 0U &&
        semantic_metrics.uniform_upload_bytes == 0U &&
        semantic_metrics.coverage_staging_bytes == 0U,
        "stable semantic masked layer replay rebuilt retained resources");
    semantic_layer_metrics = {};
    semantic_layer_metrics.struct_size = sizeof(semantic_layer_metrics);
    require(progpu_native_engine_get_layer_metrics(
        engine,
        &semantic_layer_metrics) == PROGPU_NATIVE_STATUS_SUCCESS &&
        semantic_layer_metrics.allocation_count ==
            masked_layer_allocation_count &&
        semantic_layer_metrics.cache_hit == 1U &&
        semantic_layer_metrics.mask_bind_group_generation ==
            masked_bind_group_generation &&
        semantic_layer_metrics.mask_uniform_upload_bytes == 0U &&
        semantic_layer_metrics.uniform_upload_bytes == 0U,
        "stable semantic masked layer metrics did not retain resources");
    std::uint64_t masked_layer_submission{};
    require(progpu_native_engine_get_last_submission(
        engine,
        &masked_layer_submission) == PROGPU_NATIVE_STATUS_SUCCESS,
        "semantic masked layer submission token unavailable");
    std::uint8_t masked_layer_complete{};
    require(progpu_native_engine_poll_submission(
        engine,
        masked_layer_submission,
        1U,
        &masked_layer_complete) == PROGPU_NATIVE_STATUS_SUCCESS &&
        masked_layer_complete != 0U,
        "semantic masked layer scene did not reach GPU completion");
    resolve<WGPUProcTextureViewRelease>(
        api, provider, "wgpuTextureViewRelease")(view);
    resolve<WGPUProcTextureRelease>(
        api, provider, "wgpuTextureRelease")(texture);
    webscene_gpu_external_texture masked_layer_external{};
    masked_layer_external.struct_size = sizeof(masked_layer_external);
    require(api.present(provider, canvas, &masked_layer_external) ==
            WEBSCENE_GPU_STATUS_SUCCESS &&
        masked_layer_external.handle_kind ==
            WEBSCENE_GPU_HANDLE_IOSURFACE &&
        (masked_layer_external.flags &
            WEBSCENE_GPU_EXTERNAL_TEXTURE_GPU_COMPLETE) != 0U,
        "semantic masked layer presentation failed");
    verify_semantic_masked_layer_scene(
        reinterpret_cast<IOSurfaceRef>(
            masked_layer_external.shared_handle),
        "progpu-native-semantic-masked-layer.ppm");
    api.release_external(provider, &masked_layer_external);
    api.destroy_canvas(provider, canvas);

    canvas = api.create_canvas(
        provider, &canvas_configuration, 64U, 48U);
    require(canvas != nullptr,
        "semantic coverage-mask canvas creation failed");
    texture_handle = 0U;
    require(api.acquire(provider, canvas, &texture_handle) ==
            WEBSCENE_GPU_STATUS_SUCCESS && texture_handle != 0U,
        "semantic coverage-mask texture acquisition failed");
    texture = reinterpret_cast<WGPUTexture>(texture_handle);
    view = resolve<WGPUProcTextureCreateView>(
        api, provider, "wgpuTextureCreateView")(
        texture, &view_descriptor);
    require(view != nullptr,
        "semantic coverage-mask target view creation failed");
    auto coverage_mask_scene =
        create_semantic_coverage_mask_scene_stream(64U, 48U);
    scene_metrics = {};
    scene_metrics.struct_size = sizeof(scene_metrics);
    require(progpu_native_engine_update_scene(
        engine,
        coverage_mask_scene.data(),
        coverage_mask_scene.size(),
        &scene_metrics) == PROGPU_NATIVE_STATUS_SUCCESS &&
        scene_metrics.command_count == 3U &&
        scene_metrics.resource_count == 2U &&
        scene_metrics.draw_count == 1U,
        "semantic coverage-mask scene update failed");
    progpu_native_scene_frame coverage_mask_frame = semantic_frame;
    coverage_mask_frame.target_view =
        reinterpret_cast<std::uintptr_t>(view);
    coverage_mask_frame.scene_id = 100U;
    coverage_mask_frame.generation = 1U;
    semantic_metrics = {};
    semantic_metrics.struct_size = sizeof(semantic_metrics);
    require(progpu_native_engine_render_scene(
        engine,
        &coverage_mask_frame,
        &semantic_metrics) == PROGPU_NATIVE_STATUS_SUCCESS &&
        semantic_metrics.command_count == 3U &&
        semantic_metrics.draw_call_count == 2U &&
        semantic_metrics.submission_count == 1U &&
        semantic_metrics.texture_upload_bytes == 64U &&
        semantic_metrics.uniform_upload_bytes >= 24U * sizeof(float),
        "semantic coverage-mask rendering failed");
    semantic_layer_metrics = {};
    semantic_layer_metrics.struct_size = sizeof(semantic_layer_metrics);
    require(progpu_native_engine_get_layer_metrics(
        engine,
        &semantic_layer_metrics) == PROGPU_NATIVE_STATUS_SUCCESS &&
        semantic_layer_metrics.mask_kind ==
            PROGPU_NATIVE_GROUP_MASK_TEXTURE &&
        semantic_layer_metrics.mask_uniform_upload_bytes ==
            24U * sizeof(float),
        "semantic coverage-mask metrics are incorrect");
    semantic_metrics = {};
    semantic_metrics.struct_size = sizeof(semantic_metrics);
    require(progpu_native_engine_render_scene(
        engine,
        &coverage_mask_frame,
        &semantic_metrics) == PROGPU_NATIVE_STATUS_SUCCESS &&
        semantic_metrics.texture_upload_bytes == 0U &&
        semantic_metrics.vertex_upload_bytes == 0U &&
        semantic_metrics.uniform_upload_bytes == 0U,
        "stable semantic coverage-mask replay rebuilt resources");
    std::uint64_t coverage_mask_submission{};
    require(progpu_native_engine_get_last_submission(
        engine,
        &coverage_mask_submission) == PROGPU_NATIVE_STATUS_SUCCESS,
        "semantic coverage-mask submission token unavailable");
    std::uint8_t coverage_mask_complete{};
    require(progpu_native_engine_poll_submission(
        engine,
        coverage_mask_submission,
        1U,
        &coverage_mask_complete) == PROGPU_NATIVE_STATUS_SUCCESS &&
        coverage_mask_complete != 0U,
        "semantic coverage-mask scene did not reach GPU completion");
    resolve<WGPUProcTextureViewRelease>(
        api, provider, "wgpuTextureViewRelease")(view);
    resolve<WGPUProcTextureRelease>(
        api, provider, "wgpuTextureRelease")(texture);
    webscene_gpu_external_texture coverage_mask_external{};
    coverage_mask_external.struct_size = sizeof(coverage_mask_external);
    require(api.present(provider, canvas, &coverage_mask_external) ==
            WEBSCENE_GPU_STATUS_SUCCESS &&
        coverage_mask_external.handle_kind ==
            WEBSCENE_GPU_HANDLE_IOSURFACE &&
        (coverage_mask_external.flags &
            WEBSCENE_GPU_EXTERNAL_TEXTURE_GPU_COMPLETE) != 0U,
        "semantic coverage-mask presentation failed");
    verify_semantic_coverage_mask_scene(
        reinterpret_cast<IOSurfaceRef>(
            coverage_mask_external.shared_handle));
    api.release_external(provider, &coverage_mask_external);
    api.destroy_canvas(provider, canvas);

    canvas = api.create_canvas(
        provider, &canvas_configuration, 64U, 48U);
    require(canvas != nullptr,
        "semantic mask/effect canvas creation failed");
    texture_handle = 0U;
    require(api.acquire(provider, canvas, &texture_handle) ==
            WEBSCENE_GPU_STATUS_SUCCESS && texture_handle != 0U,
        "semantic mask/effect canvas texture acquisition failed");
    texture = reinterpret_cast<WGPUTexture>(texture_handle);
    view = resolve<WGPUProcTextureCreateView>(
        api, provider, "wgpuTextureCreateView")(
        texture, &view_descriptor);
    require(view != nullptr,
        "semantic mask/effect target view creation failed");

    auto mask_effect_scene =
        create_semantic_masked_effect_layer_scene_stream();
    auto oversized_effect_scene = mask_effect_scene;
    progpu_native_scene_header oversized_header{};
    std::memcpy(
        &oversized_header,
        oversized_effect_scene.data(),
        sizeof(oversized_header));
    oversized_header.scene_id = 97U;
    std::memcpy(
        oversized_effect_scene.data(),
        &oversized_header,
        sizeof(oversized_header));
    progpu_native_scene_resource oversized_effect_resource{};
    std::memcpy(
        &oversized_effect_resource,
        oversized_effect_scene.data() + oversized_header.resource_offset +
            3U * oversized_header.resource_stride,
        sizeof(oversized_effect_resource));
    progpu_native_group_effect oversized_effect{};
    std::memcpy(
        &oversized_effect,
        oversized_effect_scene.data() +
            oversized_effect_resource.auxiliary_offset,
        sizeof(oversized_effect));
    oversized_effect.sigma_x = 50.0F;
    std::memcpy(
        oversized_effect_scene.data() +
            oversized_effect_resource.auxiliary_offset,
        &oversized_effect,
        sizeof(oversized_effect));
    scene_metrics = {};
    scene_metrics.struct_size = sizeof(scene_metrics);
    require(progpu_native_engine_update_scene(
        engine,
        oversized_effect_scene.data(),
        oversized_effect_scene.size(),
        &scene_metrics) == PROGPU_NATIVE_STATUS_SUCCESS,
        "semantic oversized physical effect update failed");
    std::uint64_t submission_before_oversized_effect{};
    require(progpu_native_engine_get_last_submission(
        engine,
        &submission_before_oversized_effect) ==
            PROGPU_NATIVE_STATUS_SUCCESS,
        "semantic oversized effect preflight token unavailable");
    progpu_native_scene_frame oversized_effect_frame = semantic_frame;
    oversized_effect_frame.target_view =
        reinterpret_cast<std::uintptr_t>(view);
    oversized_effect_frame.scene_id = 97U;
    semantic_metrics = {};
    semantic_metrics.struct_size = sizeof(semantic_metrics);
    require(progpu_native_engine_render_scene(
        engine,
        &oversized_effect_frame,
        &semantic_metrics) == PROGPU_NATIVE_STATUS_INVALID_ARGUMENT &&
        semantic_metrics.submission_count == 0U,
        "semantic oversized physical effect was not rejected before submission");
    std::uint64_t submission_after_oversized_effect{};
    require(progpu_native_engine_get_last_submission(
        engine,
        &submission_after_oversized_effect) ==
            PROGPU_NATIVE_STATUS_SUCCESS &&
        submission_after_oversized_effect ==
            submission_before_oversized_effect,
        "semantic oversized physical effect changed the submission timeline");

    auto root_effect_scene =
        create_semantic_root_effect_layer_scene_stream();
    scene_metrics = {};
    scene_metrics.struct_size = sizeof(scene_metrics);
    require(progpu_native_engine_update_scene(
        engine,
        root_effect_scene.data(),
        root_effect_scene.size(),
        &scene_metrics) == PROGPU_NATIVE_STATUS_SUCCESS &&
        scene_metrics.command_count == 3U &&
        scene_metrics.draw_count == 1U &&
        scene_metrics.maximum_stack_depth == 1U,
        "semantic root effect update failed");
    progpu_native_scene_frame root_effect_frame = semantic_frame;
    root_effect_frame.target_view = reinterpret_cast<std::uintptr_t>(view);
    root_effect_frame.scene_id = 98U;
    root_effect_frame.generation = 1U;
    semantic_metrics = {};
    semantic_metrics.struct_size = sizeof(semantic_metrics);
    require(progpu_native_engine_render_scene(
        engine,
        &root_effect_frame,
        &semantic_metrics) == PROGPU_NATIVE_STATUS_SUCCESS &&
        semantic_metrics.draw_call_count == 2U &&
        semantic_metrics.submission_count == 1U,
        "semantic root effect first render failed");
    semantic_metrics = {};
    semantic_metrics.struct_size = sizeof(semantic_metrics);
    require(progpu_native_engine_render_scene(
        engine,
        &root_effect_frame,
        &semantic_metrics) == PROGPU_NATIVE_STATUS_SUCCESS &&
        semantic_metrics.draw_call_count == 1U &&
        semantic_metrics.submission_count == 1U &&
        semantic_metrics.vertex_upload_bytes == 0U &&
        semantic_metrics.index_upload_bytes == 0U &&
        semantic_metrics.texture_upload_bytes == 0U &&
        semantic_metrics.uniform_upload_bytes == 0U &&
        semantic_metrics.coverage_staging_bytes == 0U,
        "semantic root effect stable cache replay failed");
    semantic_layer_metrics = {};
    semantic_layer_metrics.struct_size = sizeof(semantic_layer_metrics);
    require(progpu_native_engine_get_layer_metrics(
        engine,
        &semantic_layer_metrics) == PROGPU_NATIVE_STATUS_SUCCESS &&
        semantic_layer_metrics.cache_hit == 1U &&
        semantic_layer_metrics.content_pass_count == 0U &&
        semantic_layer_metrics.composite_pass_count == 1U &&
        semantic_layer_metrics.effect_cache_hit == 1U &&
        semantic_layer_metrics.effect_pass_count == 0U,
        "semantic root effect stable metrics are incorrect");

    scene_metrics = {};
    scene_metrics.struct_size = sizeof(scene_metrics);
    require(progpu_native_engine_update_scene(
        engine,
        mask_effect_scene.data(),
        mask_effect_scene.size(),
        &scene_metrics) == PROGPU_NATIVE_STATUS_SUCCESS &&
        scene_metrics.command_count == 6U &&
        scene_metrics.resource_count == 4U &&
        scene_metrics.draw_count == 2U &&
        scene_metrics.maximum_stack_depth == 2U,
        "semantic mask/effect update failed");
    progpu_native_scene_frame mask_effect_frame = semantic_frame;
    mask_effect_frame.target_view =
        reinterpret_cast<std::uintptr_t>(view);
    mask_effect_frame.scene_id = 96U;
    mask_effect_frame.generation = 1U;
    semantic_metrics = {};
    semantic_metrics.struct_size = sizeof(semantic_metrics);
    require(progpu_native_engine_render_scene(
        engine,
        &mask_effect_frame,
        &semantic_metrics) == PROGPU_NATIVE_STATUS_SUCCESS &&
        semantic_metrics.command_count == 6U &&
        semantic_metrics.draw_call_count == 4U &&
        semantic_metrics.submission_count == 1U &&
        semantic_metrics.uniform_upload_bytes >= 1280U,
        "semantic mask/effect rendering failed");
    semantic_layer_metrics = {};
    semantic_layer_metrics.struct_size = sizeof(semantic_layer_metrics);
    require(progpu_native_engine_get_layer_metrics(
        engine,
        &semantic_layer_metrics) == PROGPU_NATIVE_STATUS_SUCCESS &&
        semantic_layer_metrics.texture_width == 48U &&
        semantic_layer_metrics.texture_height == 40U &&
        semantic_layer_metrics.content_pass_count == 2U &&
        semantic_layer_metrics.composite_pass_count == 2U &&
        semantic_layer_metrics.cache_hit == 0U &&
        semantic_layer_metrics.texture_bytes == 10752U &&
        semantic_layer_metrics.mask_kind ==
            PROGPU_NATIVE_GROUP_MASK_ROUNDED_RECTANGLE &&
        semantic_layer_metrics.effect_kind ==
            PROGPU_NATIVE_GROUP_EFFECT_DROP_SHADOW &&
        semantic_layer_metrics.effect_revision == 91U &&
        semantic_layer_metrics.effect_chain_revision == 91U &&
        semantic_layer_metrics.effect_count == 2U &&
        semantic_layer_metrics.effect_pass_count == 5U &&
        semantic_layer_metrics.effect_texture_bytes == 9216U &&
        semantic_layer_metrics.effect_uniform_upload_bytes == 1280U,
        "semantic mask/effect metrics are incorrect");
    const std::uint32_t mask_effect_allocation_count =
        semantic_layer_metrics.effect_allocation_count;
    const std::uint32_t mask_effect_texture_generation =
        semantic_layer_metrics.effect_texture_generation;
    const std::uint32_t mask_effect_bind_group_generation =
        semantic_layer_metrics.mask_bind_group_generation;
    semantic_metrics = {};
    semantic_metrics.struct_size = sizeof(semantic_metrics);
    const auto stable_mask_effect_status = progpu_native_engine_render_scene(
        engine,
        &mask_effect_frame,
        &semantic_metrics);
    if (stable_mask_effect_status != PROGPU_NATIVE_STATUS_SUCCESS ||
        semantic_metrics.draw_call_count != 3U ||
        semantic_metrics.submission_count != 1U ||
        semantic_metrics.vertex_upload_bytes != 0U ||
        semantic_metrics.index_upload_bytes != 0U ||
        semantic_metrics.texture_upload_bytes != 0U ||
        semantic_metrics.uniform_upload_bytes != 0U ||
        semantic_metrics.coverage_staging_bytes != 0U) {
        std::fprintf(
            stderr,
            "stable mask/effect status=%u draws=%u submissions=%llu "
            "vertex=%llu index=%llu texture=%llu uniform=%llu coverage=%llu\n",
            static_cast<unsigned>(stable_mask_effect_status),
            semantic_metrics.draw_call_count,
            static_cast<unsigned long long>(semantic_metrics.submission_count),
            static_cast<unsigned long long>(semantic_metrics.vertex_upload_bytes),
            static_cast<unsigned long long>(semantic_metrics.index_upload_bytes),
            static_cast<unsigned long long>(semantic_metrics.texture_upload_bytes),
            static_cast<unsigned long long>(semantic_metrics.uniform_upload_bytes),
            static_cast<unsigned long long>(semantic_metrics.coverage_staging_bytes));
    }
    require(stable_mask_effect_status == PROGPU_NATIVE_STATUS_SUCCESS &&
        semantic_metrics.draw_call_count == 3U &&
        semantic_metrics.submission_count == 1U &&
        semantic_metrics.vertex_upload_bytes == 0U &&
        semantic_metrics.index_upload_bytes == 0U &&
        semantic_metrics.texture_upload_bytes == 0U &&
        semantic_metrics.uniform_upload_bytes == 0U &&
        semantic_metrics.coverage_staging_bytes == 0U,
        "stable semantic mask/effect replay rebuilt retained resources");
    semantic_layer_metrics = {};
    semantic_layer_metrics.struct_size = sizeof(semantic_layer_metrics);
    require(progpu_native_engine_get_layer_metrics(
        engine,
        &semantic_layer_metrics) == PROGPU_NATIVE_STATUS_SUCCESS &&
        semantic_layer_metrics.cache_hit == 1U &&
        semantic_layer_metrics.content_pass_count == 1U &&
        semantic_layer_metrics.composite_pass_count == 2U &&
        semantic_layer_metrics.effect_cache_hit == 1U &&
        semantic_layer_metrics.effect_pass_count == 0U &&
        semantic_layer_metrics.effect_allocation_count ==
            mask_effect_allocation_count &&
        semantic_layer_metrics.effect_texture_generation ==
            mask_effect_texture_generation &&
        semantic_layer_metrics.mask_bind_group_generation ==
            mask_effect_bind_group_generation &&
        semantic_layer_metrics.mask_uniform_upload_bytes == 0U &&
        semantic_layer_metrics.effect_uniform_upload_bytes == 0U &&
        semantic_layer_metrics.uniform_upload_bytes == 0U,
        "stable semantic mask/effect output was not retained");
    std::uint64_t mask_effect_submission{};
    require(progpu_native_engine_get_last_submission(
        engine,
        &mask_effect_submission) == PROGPU_NATIVE_STATUS_SUCCESS,
        "semantic mask/effect submission token unavailable");
    std::uint8_t mask_effect_complete{};
    require(progpu_native_engine_poll_submission(
        engine,
        mask_effect_submission,
        1U,
        &mask_effect_complete) == PROGPU_NATIVE_STATUS_SUCCESS &&
        mask_effect_complete != 0U,
        "semantic mask/effect scene did not reach GPU completion");
    resolve<WGPUProcTextureViewRelease>(
        api, provider, "wgpuTextureViewRelease")(view);
    resolve<WGPUProcTextureRelease>(
        api, provider, "wgpuTextureRelease")(texture);
    webscene_gpu_external_texture mask_effect_external{};
    mask_effect_external.struct_size = sizeof(mask_effect_external);
    require(api.present(provider, canvas, &mask_effect_external) ==
            WEBSCENE_GPU_STATUS_SUCCESS &&
        mask_effect_external.handle_kind ==
            WEBSCENE_GPU_HANDLE_IOSURFACE &&
        (mask_effect_external.flags &
            WEBSCENE_GPU_EXTERNAL_TEXTURE_GPU_COMPLETE) != 0U,
        "semantic mask/effect presentation failed");
    verify_semantic_masked_effect_layer_scene(
        reinterpret_cast<IOSurfaceRef>(
            mask_effect_external.shared_handle),
        "progpu-native-semantic-mask-effects.ppm");
    api.release_external(provider, &mask_effect_external);
    api.destroy_canvas(provider, canvas);

    canvas = api.create_canvas(
        provider, &canvas_configuration, 128U, 96U);
    require(canvas != nullptr,
        "DPI-2 semantic layer canvas creation failed");
    texture_handle = 0U;
    require(api.acquire(provider, canvas, &texture_handle) ==
            WEBSCENE_GPU_STATUS_SUCCESS && texture_handle != 0U,
        "DPI-2 semantic layer canvas texture acquisition failed");
    texture = reinterpret_cast<WGPUTexture>(texture_handle);
    view = resolve<WGPUProcTextureCreateView>(
        api, provider, "wgpuTextureCreateView")(
        texture, &view_descriptor);
    require(view != nullptr,
        "DPI-2 semantic layer target view creation failed");

    auto dpi2_opacity_layer_scene =
        create_semantic_opacity_layer_scene_stream(2U, 2.0F);
    scene_metrics = {};
    scene_metrics.struct_size = sizeof(scene_metrics);
    require(progpu_native_engine_update_scene(
        engine,
        dpi2_opacity_layer_scene.data(),
        dpi2_opacity_layer_scene.size(),
        &scene_metrics) == PROGPU_NATIVE_STATUS_SUCCESS &&
        scene_metrics.command_count == 13U &&
        scene_metrics.draw_count == 5U &&
        scene_metrics.maximum_stack_depth == 2U,
        "DPI-2 bounded semantic layer update failed");
    progpu_native_scene_frame dpi2_layer_frame = opacity_layer_frame;
    dpi2_layer_frame.width = 128U;
    dpi2_layer_frame.height = 96U;
    dpi2_layer_frame.dpi_scale = 2.0F;
    dpi2_layer_frame.target_view =
        reinterpret_cast<std::uintptr_t>(view);
    dpi2_layer_frame.generation = 2U;
    semantic_metrics = {};
    semantic_metrics.struct_size = sizeof(semantic_metrics);
    require(progpu_native_engine_render_scene(
        engine,
        &dpi2_layer_frame,
        &semantic_metrics) == PROGPU_NATIVE_STATUS_SUCCESS &&
        semantic_metrics.command_count == 13U &&
        semantic_metrics.draw_call_count == 8U &&
        semantic_metrics.submission_count == 1U &&
        semantic_metrics.vertex_upload_bytes != 0U &&
        semantic_metrics.uniform_upload_bytes != 0U,
        "DPI-2 bounded semantic layer rendering failed");
    semantic_layer_metrics = {};
    semantic_layer_metrics.struct_size = sizeof(semantic_layer_metrics);
    require(progpu_native_engine_get_layer_metrics(
        engine,
        &semantic_layer_metrics) == PROGPU_NATIVE_STATUS_SUCCESS &&
        semantic_layer_metrics.texture_width == 56U &&
        semantic_layer_metrics.texture_height == 32U &&
        semantic_layer_metrics.texture_bytes == 11264U &&
        semantic_layer_metrics.content_pass_count == 3U &&
        semantic_layer_metrics.composite_pass_count == 3U &&
        semantic_layer_metrics.cache_hit == 0U,
        "DPI-2 bounded semantic layer metrics are incorrect");
    semantic_metrics = {};
    semantic_metrics.struct_size = sizeof(semantic_metrics);
    require(progpu_native_engine_render_scene(
        engine,
        &dpi2_layer_frame,
        &semantic_metrics) == PROGPU_NATIVE_STATUS_SUCCESS &&
        semantic_metrics.command_count == 13U &&
        semantic_metrics.draw_call_count == 8U &&
        semantic_metrics.submission_count == 1U &&
        semantic_metrics.vertex_upload_bytes == 0U &&
        semantic_metrics.index_upload_bytes == 0U &&
        semantic_metrics.texture_upload_bytes == 0U &&
        semantic_metrics.uniform_upload_bytes == 0U &&
        semantic_metrics.coverage_staging_bytes == 0U,
        "stable DPI-2 bounded semantic layer replay rebuilt resources");
    resolve<WGPUProcTextureViewRelease>(
        api, provider, "wgpuTextureViewRelease")(view);
    resolve<WGPUProcTextureRelease>(
        api, provider, "wgpuTextureRelease")(texture);
    webscene_gpu_external_texture dpi2_layer_external{};
    dpi2_layer_external.struct_size = sizeof(dpi2_layer_external);
    require(api.present(provider, canvas, &dpi2_layer_external) ==
            WEBSCENE_GPU_STATUS_SUCCESS &&
        dpi2_layer_external.handle_kind ==
            WEBSCENE_GPU_HANDLE_IOSURFACE &&
        (dpi2_layer_external.flags &
            WEBSCENE_GPU_EXTERNAL_TEXTURE_GPU_COMPLETE) != 0U,
        "DPI-2 bounded semantic layer presentation failed");
    verify_semantic_layer_scene(
        reinterpret_cast<IOSurfaceRef>(
            dpi2_layer_external.shared_handle),
        "progpu-native-semantic-layers-2x.ppm",
        2U);
    api.release_external(provider, &dpi2_layer_external);
    api.destroy_canvas(provider, canvas);

    canvas = api.create_canvas(
        provider, &canvas_configuration, 64U, 48U);
    require(canvas != nullptr, "baseline canvas recreation failed");
    texture_handle = 0U;
    require(api.acquire(provider, canvas, &texture_handle) ==
            WEBSCENE_GPU_STATUS_SUCCESS && texture_handle != 0U,
        "baseline canvas texture acquisition failed");
    texture = reinterpret_cast<WGPUTexture>(texture_handle);
    view = resolve<WGPUProcTextureCreateView>(
        api, provider, "wgpuTextureCreateView")(
        texture, &view_descriptor);
    require(view != nullptr, "baseline target view creation failed");

    WGPUTextureDescriptor mask_texture_descriptor =
        WGPU_TEXTURE_DESCRIPTOR_INIT;
    mask_texture_descriptor.label = {
        "ProGPU common mask provider test",
        WGPU_STRLEN};
    mask_texture_descriptor.usage = WGPUTextureUsage_TextureBinding |
        WGPUTextureUsage_CopyDst;
    mask_texture_descriptor.dimension = WGPUTextureDimension_2D;
    mask_texture_descriptor.size = {1U, 1U, 1U};
    mask_texture_descriptor.format = WGPUTextureFormat_R8Unorm;
    mask_texture_descriptor.mipLevelCount = 1U;
    mask_texture_descriptor.sampleCount = 1U;
    WGPUTexture mask_texture = resolve<WGPUProcDeviceCreateTexture>(
        api, provider, "wgpuDeviceCreateTexture")(
        device, &mask_texture_descriptor);
    require(mask_texture != nullptr, "mask texture creation failed");
    WGPUTextureView mask_view = resolve<WGPUProcTextureCreateView>(
        api, provider, "wgpuTextureCreateView")(
        mask_texture, &view_descriptor);
    require(mask_view != nullptr, "mask view creation failed");
    const std::uint8_t opaque_mask = 255U;
    WGPUTexelCopyTextureInfo mask_destination =
        WGPU_TEXEL_COPY_TEXTURE_INFO_INIT;
    mask_destination.texture = mask_texture;
    mask_destination.aspect = WGPUTextureAspect_All;
    WGPUTexelCopyBufferLayout mask_layout =
        WGPU_TEXEL_COPY_BUFFER_LAYOUT_INIT;
    mask_layout.bytesPerRow = 1U;
    mask_layout.rowsPerImage = 1U;
    const WGPUExtent3D mask_extent{1U, 1U, 1U};
    resolve<WGPUProcQueueWriteTexture>(
        api, provider, "wgpuQueueWriteTexture")(
        queue,
        &mask_destination,
        &opaque_mask,
        sizeof(opaque_mask),
        &mask_layout,
        &mask_extent);

    const progpu_native_rect rectangles[]{
        {4.0F, 4.0F, 32.0F, 24.0F,
            {0.92F, 0.18F, 0.08F, 1.0F}},
        {4.0F, 4.0F, 32.0F, 24.0F,
            {0.92F, 0.18F, 0.08F, 1.0F}}
    };
    progpu_native_draw_state draw_state{};
    draw_state.struct_size = sizeof(draw_state);
    draw_state.flags = PROGPU_NATIVE_DRAW_STATE_CLIP_RECT;
    draw_state.opacity = 0.5F;
    draw_state.clip_rect = {10.25F, 8.25F, 10.5F, 10.5F};
    draw_state.group_opacity = 1.0F;
    draw_state.group_blend_mode = PROGPU_NATIVE_BLEND_SRC_OVER;
    progpu_native_frame frame{
        sizeof(progpu_native_frame),
        64U,
        48U,
        1.5F,
        reinterpret_cast<std::uintptr_t>(view),
        {0.05F, 0.10F, 0.22F, 1.0F},
        rectangles,
        1U,
        &draw_state
    };
    progpu_native_frame_metrics metrics{};
    metrics.struct_size = sizeof(metrics);
    draw_state.flags = 1U << 31U;
    require(progpu_native_engine_render(engine, &frame, &metrics) ==
        PROGPU_NATIVE_STATUS_INVALID_ARGUMENT,
        "unknown draw-state feature did not fail closed");
    draw_state.flags = PROGPU_NATIVE_DRAW_STATE_CLIP_RECT;
    draw_state.struct_size = offsetof(
        progpu_native_draw_state,
        group_opacity);
    require(progpu_native_engine_render(engine, &frame, &metrics) ==
        PROGPU_NATIVE_STATUS_SUCCESS && metrics.draw_call_count == 1U,
        "legacy ABI-v3 draw-state prefix failed");
    draw_state.struct_size = offsetof(
        progpu_native_draw_state,
        group_mask);
    draw_state.group_opacity = 0.75F;
    draw_state.group_revision = 3U;
    require(progpu_native_engine_render(engine, &frame, &metrics) ==
        PROGPU_NATIVE_STATUS_SUCCESS,
        "40-byte ABI-v3 group draw-state prefix failed");
    draw_state.struct_size = offsetof(
        progpu_native_draw_state,
        group_effect);
    require(progpu_native_engine_render(engine, &frame, &metrics) ==
        PROGPU_NATIVE_STATUS_SUCCESS,
        "48-byte ABI-v3 mask draw-state prefix failed");
    draw_state.struct_size = sizeof(draw_state);
    draw_state.group_revision = 0U;
    draw_state.group_opacity = 1.1F;
    require(progpu_native_engine_render(engine, &frame, &metrics) ==
        PROGPU_NATIVE_STATUS_INVALID_ARGUMENT,
        "out-of-range group opacity did not fail closed");
    draw_state.group_opacity = 1.0F;
    draw_state.group_blend_mode = PROGPU_NATIVE_BLEND_MODULATE + 1U;
    require(progpu_native_engine_render(engine, &frame, &metrics) ==
        PROGPU_NATIVE_STATUS_INVALID_ARGUMENT,
        "unknown group blend mode did not fail closed");
    draw_state.group_blend_mode = PROGPU_NATIVE_BLEND_SRC_OVER;
    draw_state.clip_rect = {10.0F, 8.0F, 0.0F, 10.0F};
    require(progpu_native_engine_render(engine, &frame, &metrics) ==
        PROGPU_NATIVE_STATUS_SUCCESS && metrics.draw_call_count == 0U,
        "empty draw-state clip did not skip the draw");
    draw_state.clip_rect = {10.25F, 8.25F, 10.5F, 10.5F};
    frame.struct_size = offsetof(progpu_native_frame, draw_state);
    require(progpu_native_engine_render(engine, &frame, &metrics) ==
        PROGPU_NATIVE_STATUS_SUCCESS && metrics.draw_call_count == 1U,
        "legacy ABI v3 frame prefix failed");
    frame.struct_size = sizeof(frame);
    require(progpu_native_engine_render(engine, &frame, &metrics) ==
        PROGPU_NATIVE_STATUS_SUCCESS && metrics.draw_call_count == 1U,
        "ProGPU clipped-opacity render failed");
    draw_state.opacity = 1.0F;
    draw_state.group_opacity = 0.25F;
    draw_state.group_revision = 7U;
    frame.rect_count = 2U;
    require(progpu_native_engine_render(engine, &frame, &metrics) ==
        PROGPU_NATIVE_STATUS_SUCCESS && metrics.draw_call_count == 1U,
        "ProGPU group-opacity content render failed");
    progpu_native_layer_metrics layer_metrics{};
    layer_metrics.struct_size = sizeof(layer_metrics);
    require(progpu_native_engine_get_layer_metrics(
        engine, &layer_metrics) == PROGPU_NATIVE_STATUS_SUCCESS &&
        layer_metrics.content_pass_count == 1U &&
        layer_metrics.composite_pass_count == 1U &&
        layer_metrics.cache_hit == 0U &&
        layer_metrics.allocation_count == 2U,
        "group layer content metrics are invalid");
    alignas(progpu_native_layer_metrics)
        std::array<std::byte, 56U> legacy_layer_metrics_bytes{};
    auto* legacy_layer_metrics =
        reinterpret_cast<progpu_native_layer_metrics*>(
            legacy_layer_metrics_bytes.data());
    legacy_layer_metrics->struct_size = legacy_layer_metrics_bytes.size();
    require(progpu_native_engine_get_layer_metrics(
        engine, legacy_layer_metrics) == PROGPU_NATIVE_STATUS_SUCCESS &&
        legacy_layer_metrics->struct_size ==
            sizeof(progpu_native_layer_metrics) &&
        legacy_layer_metrics->content_pass_count == 1U,
        "legacy layer-metrics prefix failed");
    draw_state.group_opacity = 0.5F;
    require(progpu_native_engine_render(engine, &frame, &metrics) ==
        PROGPU_NATIVE_STATUS_SUCCESS && metrics.draw_call_count == 0U &&
        metrics.vertex_upload_bytes == 0U,
        "retained group replay did not skip family compilation and upload");
    require(progpu_native_engine_get_layer_metrics(
        engine, &layer_metrics) == PROGPU_NATIVE_STATUS_SUCCESS &&
        layer_metrics.content_pass_count == 0U &&
        layer_metrics.composite_pass_count == 1U &&
        layer_metrics.cache_hit == 1U &&
        layer_metrics.allocation_count == 2U &&
        layer_metrics.vertex_upload_bytes == 224U,
        "retained group replay metrics are invalid");

    progpu_native_group_mask group_mask{};
    group_mask.struct_size = sizeof(group_mask);
    group_mask.kind = 0xFFFFFFFFU;
    draw_state.group_mask = &group_mask;
    require(progpu_native_engine_render(engine, &frame, &metrics) ==
        PROGPU_NATIVE_STATUS_INVALID_ARGUMENT,
        "unknown group-mask kind did not fail closed");
    group_mask = {};
    group_mask.struct_size = sizeof(group_mask);
    group_mask.kind = PROGPU_NATIVE_GROUP_MASK_TEXTURE;
    group_mask.external_view = frame.target_view;
    group_mask.width = 1U;
    group_mask.height = 1U;
    group_mask.texture_format = PROGPU_NATIVE_MASK_TEXTURE_R8_UNORM;
    group_mask.revision = 1U;
    group_mask.destination_rect = {10.0F, 8.0F, 11.0F, 11.0F};
    require(progpu_native_engine_render(engine, &frame, &metrics) ==
        PROGPU_NATIVE_STATUS_INVALID_ARGUMENT,
        "target/group-mask alias did not fail closed");
    group_mask = {};
    group_mask.struct_size = sizeof(group_mask);
    group_mask.kind = PROGPU_NATIVE_GROUP_MASK_TEXTURE;
    group_mask.external_view = reinterpret_cast<std::uintptr_t>(mask_view);
    group_mask.width = 1U;
    group_mask.height = 1U;
    group_mask.sampling = PROGPU_NATIVE_IMAGE_SAMPLING_NEAREST;
    group_mask.texture_format = PROGPU_NATIVE_MASK_TEXTURE_R8_UNORM;
    group_mask.revision = 1U;
    group_mask.destination_rect = {10.0F, 8.0F, 11.0F, 11.0F};
    draw_state.group_mask = &group_mask;
    require(progpu_native_engine_render(engine, &frame, &metrics) ==
        PROGPU_NATIVE_STATUS_SUCCESS && metrics.draw_call_count == 0U,
        "retained texture group-mask replay failed");
    require(progpu_native_engine_get_layer_metrics(
        engine, &layer_metrics) == PROGPU_NATIVE_STATUS_SUCCESS &&
        layer_metrics.content_pass_count == 0U &&
        layer_metrics.composite_pass_count == 1U &&
        layer_metrics.cache_hit == 1U &&
        layer_metrics.uniform_upload_bytes == 96U &&
        layer_metrics.mask_kind == PROGPU_NATIVE_GROUP_MASK_TEXTURE &&
        layer_metrics.mask_revision == 1U &&
        layer_metrics.mask_bind_group_cache_hit == 0U &&
        layer_metrics.mask_uniform_upload_bytes == 96U,
        "texture group-mask metrics are invalid");

    group_mask = {};
    group_mask.struct_size = sizeof(group_mask);
    group_mask.kind = PROGPU_NATIVE_GROUP_MASK_ROUNDED_RECTANGLE;
    group_mask.bounds = {10.0F, 8.0F, 11.0F, 11.0F};
    group_mask.transform = {1.0F, 0.0F, 0.0F, 1.0F, 0.0F, 0.0F};
    group_mask.corner_radii_x[0] = 2.0F;
    group_mask.corner_radii_x[1] = 2.0F;
    group_mask.corner_radii_x[2] = 2.0F;
    group_mask.corner_radii_x[3] = 2.0F;
    group_mask.corner_radii_y[0] = 2.0F;
    group_mask.corner_radii_y[1] = 2.0F;
    group_mask.corner_radii_y[2] = 2.0F;
    group_mask.corner_radii_y[3] = 2.0F;
    group_mask.opacity = 1.0F;
    require(progpu_native_engine_render(engine, &frame, &metrics) ==
        PROGPU_NATIVE_STATUS_SUCCESS && metrics.draw_call_count == 0U,
        "retained analytic group-mask replay failed");
    require(progpu_native_engine_get_layer_metrics(
        engine, &layer_metrics) == PROGPU_NATIVE_STATUS_SUCCESS &&
        layer_metrics.content_pass_count == 0U &&
        layer_metrics.composite_pass_count == 1U &&
        layer_metrics.cache_hit == 1U &&
        layer_metrics.uniform_upload_bytes == 96U &&
        layer_metrics.mask_kind ==
            PROGPU_NATIVE_GROUP_MASK_ROUNDED_RECTANGLE &&
        layer_metrics.mask_bind_group_cache_hit == 1U &&
        layer_metrics.mask_uniform_upload_bytes == 96U,
        "analytic group-mask metrics are invalid");
    require(progpu_native_engine_render(engine, &frame, &metrics) ==
        PROGPU_NATIVE_STATUS_SUCCESS && metrics.draw_call_count == 0U,
        "unchanged analytic group-mask replay failed");
    require(progpu_native_engine_get_layer_metrics(
        engine, &layer_metrics) == PROGPU_NATIVE_STATUS_SUCCESS &&
        layer_metrics.content_pass_count == 0U &&
        layer_metrics.composite_pass_count == 1U &&
        layer_metrics.cache_hit == 1U &&
        layer_metrics.mask_bind_group_cache_hit == 1U &&
        layer_metrics.mask_uniform_upload_bytes == 0U &&
        layer_metrics.uniform_upload_bytes == 0U,
        "unchanged analytic group-mask replay uploaded state");

    alignas(progpu_native_group_mask)
        std::array<std::byte, 144U> legacy_group_mask_bytes{};
    std::memcpy(
        legacy_group_mask_bytes.data(),
        &group_mask,
        legacy_group_mask_bytes.size());
    auto* legacy_group_mask =
        reinterpret_cast<progpu_native_group_mask*>(
            legacy_group_mask_bytes.data());
    legacy_group_mask->struct_size = legacy_group_mask_bytes.size();
    draw_state.group_mask = legacy_group_mask;
    require(progpu_native_engine_render(engine, &frame, &metrics) ==
        PROGPU_NATIVE_STATUS_SUCCESS,
        "legacy common-mask descriptor prefix failed");

    const progpu_native_path_segment clip_segments[]{
        {{0.0F, 0.0F}, {20.0F, 0.0F}, {}, {},
            PROGPU_NATIVE_PATH_SEGMENT_LINE, 0U, 0U, 0U},
        {{20.0F, 0.0F}, {20.0F, 20.0F}, {}, {},
            PROGPU_NATIVE_PATH_SEGMENT_LINE, 0U, 0U, 0U},
        {{20.0F, 20.0F}, {0.0F, 20.0F}, {}, {},
            PROGPU_NATIVE_PATH_SEGMENT_LINE, 0U, 0U, 0U},
        {{0.0F, 20.0F}, {0.0F, 0.0F}, {}, {},
            PROGPU_NATIVE_PATH_SEGMENT_LINE, 0U, 0U, 0U}
    };
    const progpu_native_clip_path clip_paths[]{
        {0U, 4U, 0.0F, 0.0F, 20.0F, 20.0F,
            {1.0F, 0.15F, -0.1F, 1.0F, 10.0F, 8.0F},
            PROGPU_NATIVE_FILL_RULE_NON_ZERO, 8U,
            PROGPU_NATIVE_CLIP_INTERSECT, 0U},
        {0U, 4U, 0.0F, 0.0F, 20.0F, 20.0F,
            {0.4F, -0.1F, 0.15F, 0.35F, 16.0F, 12.0F},
            PROGPU_NATIVE_FILL_RULE_EVEN_ODD, 8U,
            PROGPU_NATIVE_CLIP_DIFFERENCE, 0U}
    };
    const progpu_native_clip_chain clip_chain{
        sizeof(progpu_native_clip_chain),
        0U,
        clip_paths,
        2U,
        clip_segments,
        4U
    };
    group_mask = {};
    group_mask.struct_size = sizeof(group_mask);
    group_mask.kind = PROGPU_NATIVE_GROUP_MASK_VECTOR_CLIP_CHAIN;
    group_mask.revision = 1U;
    group_mask.clip_chain = &clip_chain;
    draw_state.group_mask = &group_mask;
    require(progpu_native_engine_render(engine, &frame, &metrics) ==
        PROGPU_NATIVE_STATUS_SUCCESS,
        "retained vector clip-chain render failed");
    require(progpu_native_engine_get_layer_metrics(
        engine, &layer_metrics) == PROGPU_NATIVE_STATUS_SUCCESS &&
        layer_metrics.content_pass_count == 0U &&
        layer_metrics.composite_pass_count == 1U &&
        layer_metrics.cache_hit == 1U &&
        layer_metrics.mask_kind ==
            PROGPU_NATIVE_GROUP_MASK_VECTOR_CLIP_CHAIN &&
        layer_metrics.clip_path_count == 2U &&
        layer_metrics.clip_rasterized_path_count > 0U &&
        layer_metrics.clip_pass_count == 5U &&
        layer_metrics.clip_cache_hit == 0U &&
        layer_metrics.clip_path_upload_bytes > 0U &&
        layer_metrics.clip_coverage_staging_bytes > 0U,
        "changed vector clip-chain metrics are invalid");
    require(progpu_native_engine_render(engine, &frame, &metrics) ==
        PROGPU_NATIVE_STATUS_SUCCESS,
        "unchanged vector clip-chain replay failed");
    require(progpu_native_engine_get_layer_metrics(
        engine, &layer_metrics) == PROGPU_NATIVE_STATUS_SUCCESS &&
        layer_metrics.content_pass_count == 0U &&
        layer_metrics.composite_pass_count == 1U &&
        layer_metrics.cache_hit == 1U &&
        layer_metrics.clip_path_count == 2U &&
        layer_metrics.clip_rasterized_path_count == 0U &&
        layer_metrics.clip_pass_count == 0U &&
        layer_metrics.clip_cache_hit == 1U &&
        layer_metrics.clip_path_upload_bytes == 0U &&
        layer_metrics.clip_coverage_staging_bytes == 0U,
        "unchanged vector clip-chain replay rebuilt coverage");

    progpu_native_group_effect group_effect{};
    group_effect.struct_size = sizeof(group_effect);
    group_effect.kind = 0xFFFFFFFFU;
    draw_state.group_effect = &group_effect;
    require(progpu_native_engine_render(engine, &frame, &metrics) ==
        PROGPU_NATIVE_STATUS_INVALID_ARGUMENT,
        "unknown group-effect kind did not fail closed");
    group_effect = {};
    group_effect.struct_size = sizeof(group_effect);
    group_effect.kind = PROGPU_NATIVE_GROUP_EFFECT_GAUSSIAN_BLUR;
    group_effect.revision = 1U;
    group_effect.sigma_x = 32.0F;
    group_effect.sigma_y = 2.0F;
    require(progpu_native_engine_render(engine, &frame, &metrics) ==
        PROGPU_NATIVE_STATUS_INVALID_ARGUMENT,
        "out-of-range physical Gaussian sigma did not fail closed");
    group_effect.sigma_x = 2.0F;
    require(progpu_native_engine_render(engine, &frame, &metrics) ==
        PROGPU_NATIVE_STATUS_SUCCESS && metrics.draw_call_count == 0U,
        "retained Gaussian group-effect replay failed");
    require(progpu_native_engine_get_layer_metrics(
        engine, &layer_metrics) == PROGPU_NATIVE_STATUS_SUCCESS &&
        layer_metrics.content_pass_count == 0U &&
        layer_metrics.composite_pass_count == 1U &&
        layer_metrics.cache_hit == 1U &&
        layer_metrics.effect_kind ==
            PROGPU_NATIVE_GROUP_EFFECT_GAUSSIAN_BLUR &&
        layer_metrics.effect_revision == 1U &&
        layer_metrics.effect_pass_count == 2U &&
        layer_metrics.effect_cache_hit == 0U &&
        layer_metrics.effect_uniform_upload_bytes == 32U &&
        layer_metrics.effect_texture_bytes == 64U * 48U * 8U,
        "changed Gaussian group-effect metrics are invalid");
    require(progpu_native_engine_render(engine, &frame, &metrics) ==
        PROGPU_NATIVE_STATUS_SUCCESS && metrics.draw_call_count == 0U,
        "unchanged Gaussian group-effect replay failed");
    require(progpu_native_engine_get_layer_metrics(
        engine, &layer_metrics) == PROGPU_NATIVE_STATUS_SUCCESS &&
        layer_metrics.content_pass_count == 0U &&
        layer_metrics.composite_pass_count == 1U &&
        layer_metrics.cache_hit == 1U &&
        layer_metrics.effect_pass_count == 0U &&
        layer_metrics.effect_cache_hit == 1U &&
        layer_metrics.effect_uniform_upload_bytes == 0U &&
        layer_metrics.effect_texture_bytes == 64U * 48U * 8U,
        "unchanged Gaussian group-effect replay dispatched work");
    group_effect.revision = 2U;
    group_effect.sigma_x = 3.0F;
    require(progpu_native_engine_render(engine, &frame, &metrics) ==
        PROGPU_NATIVE_STATUS_SUCCESS && metrics.draw_call_count == 0U,
        "changed Gaussian group-effect replay failed");
    require(progpu_native_engine_get_layer_metrics(
        engine, &layer_metrics) == PROGPU_NATIVE_STATUS_SUCCESS &&
        layer_metrics.content_pass_count == 0U &&
        layer_metrics.effect_pass_count == 2U &&
        layer_metrics.effect_cache_hit == 0U &&
        layer_metrics.effect_uniform_upload_bytes == 16U,
        "changed Gaussian group-effect replay did not reuse content");
    group_effect.kind = PROGPU_NATIVE_GROUP_EFFECT_DROP_SHADOW;
    group_effect.revision = 3U;
    group_effect.sigma_x = 2.0F;
    group_effect.sigma_y = 2.0F;
    group_effect.offset_x = 3.5F;
    group_effect.offset_y = -1.25F;
    group_effect.color_r = 0.1F;
    group_effect.color_g = 0.2F;
    group_effect.color_b = 0.3F;
    group_effect.color_a = 1.5F;
    require(progpu_native_engine_render(engine, &frame, &metrics) ==
        PROGPU_NATIVE_STATUS_INVALID_ARGUMENT,
        "out-of-range drop-shadow color did not fail closed");
    group_effect.color_a = 0.75F;
    require(progpu_native_engine_render(engine, &frame, &metrics) ==
        PROGPU_NATIVE_STATUS_SUCCESS && metrics.draw_call_count == 0U,
        "retained drop-shadow group-effect replay failed");
    require(progpu_native_engine_get_layer_metrics(
        engine, &layer_metrics) == PROGPU_NATIVE_STATUS_SUCCESS &&
        layer_metrics.content_pass_count == 0U &&
        layer_metrics.composite_pass_count == 1U &&
        layer_metrics.cache_hit == 1U &&
        layer_metrics.effect_kind ==
            PROGPU_NATIVE_GROUP_EFFECT_DROP_SHADOW &&
        layer_metrics.effect_revision == 3U &&
        layer_metrics.effect_pass_count == 3U &&
        layer_metrics.effect_cache_hit == 0U &&
        layer_metrics.effect_uniform_upload_bytes == 48U &&
        layer_metrics.effect_texture_bytes == 64U * 48U * 8U,
        "changed drop-shadow group-effect metrics are invalid");
    require(progpu_native_engine_render(engine, &frame, &metrics) ==
        PROGPU_NATIVE_STATUS_SUCCESS && metrics.draw_call_count == 0U,
        "unchanged drop-shadow group-effect replay failed");
    require(progpu_native_engine_get_layer_metrics(
        engine, &layer_metrics) == PROGPU_NATIVE_STATUS_SUCCESS &&
        layer_metrics.content_pass_count == 0U &&
        layer_metrics.composite_pass_count == 1U &&
        layer_metrics.effect_pass_count == 0U &&
        layer_metrics.effect_cache_hit == 1U &&
        layer_metrics.effect_uniform_upload_bytes == 0U,
        "unchanged drop-shadow group-effect replay dispatched work");
    group_effect.kind = PROGPU_NATIVE_GROUP_EFFECT_GAUSSIAN_BLUR;
    require(progpu_native_engine_render(engine, &frame, &metrics) ==
        PROGPU_NATIVE_STATUS_SUCCESS,
        "same-revision group-effect kind transition failed");
    require(progpu_native_engine_get_layer_metrics(
        engine, &layer_metrics) == PROGPU_NATIVE_STATUS_SUCCESS &&
        layer_metrics.content_pass_count == 0U &&
        layer_metrics.effect_kind ==
            PROGPU_NATIVE_GROUP_EFFECT_GAUSSIAN_BLUR &&
        layer_metrics.effect_pass_count == 2U &&
        layer_metrics.effect_cache_hit == 0U,
        "same-revision group-effect kind transition reused stale output");
    draw_state.group_effect = nullptr;

    std::array<progpu_native_group_effect, 2U> effect_nodes{};
    effect_nodes[0].struct_size = sizeof(progpu_native_group_effect);
    effect_nodes[0].kind = PROGPU_NATIVE_GROUP_EFFECT_GAUSSIAN_BLUR;
    effect_nodes[0].revision = 4U;
    effect_nodes[0].sigma_x = 1.5F;
    effect_nodes[0].sigma_y = 1.5F;
    effect_nodes[1].struct_size = sizeof(progpu_native_group_effect);
    effect_nodes[1].kind = PROGPU_NATIVE_GROUP_EFFECT_DROP_SHADOW;
    effect_nodes[1].revision = 5U;
    effect_nodes[1].sigma_x = 2.0F;
    effect_nodes[1].sigma_y = 2.0F;
    effect_nodes[1].offset_x = 2.5F;
    effect_nodes[1].offset_y = 1.25F;
    effect_nodes[1].color_r = 0.2F;
    effect_nodes[1].color_g = 0.1F;
    effect_nodes[1].color_b = 0.4F;
    effect_nodes[1].color_a = 0.6F;
    progpu_native_group_effect_chain effect_chain{};
    effect_chain.struct_size = sizeof(effect_chain);
    effect_chain.revision = 9U;
    effect_chain.effects = effect_nodes.data();
    draw_state.group_effect_chain = &effect_chain;
    require(progpu_native_engine_render(engine, &frame, &metrics) ==
        PROGPU_NATIVE_STATUS_INVALID_ARGUMENT,
        "empty group-effect chain did not fail closed");
    effect_chain.effect_count = effect_nodes.size();
    effect_chain.reserved = 1U;
    require(progpu_native_engine_render(engine, &frame, &metrics) ==
        PROGPU_NATIVE_STATUS_INVALID_ARGUMENT,
        "reserved group-effect chain state did not fail closed");
    effect_chain.reserved = 0U;
    draw_state.group_effect = &group_effect;
    require(progpu_native_engine_render(engine, &frame, &metrics) ==
        PROGPU_NATIVE_STATUS_INVALID_ARGUMENT,
        "simultaneous single and chained effects did not fail closed");
    draw_state.group_effect = nullptr;
    require(progpu_native_engine_render(engine, &frame, &metrics) ==
        PROGPU_NATIVE_STATUS_SUCCESS && metrics.draw_call_count == 0U,
        "bounded retained group-effect chain replay failed");
    require(progpu_native_engine_get_layer_metrics(
        engine, &layer_metrics) == PROGPU_NATIVE_STATUS_SUCCESS &&
        layer_metrics.content_pass_count == 0U &&
        layer_metrics.composite_pass_count == 1U &&
        layer_metrics.cache_hit == 1U &&
        layer_metrics.effect_kind ==
            PROGPU_NATIVE_GROUP_EFFECT_DROP_SHADOW &&
        layer_metrics.effect_revision == 9U &&
        layer_metrics.effect_count == 2U &&
        layer_metrics.effect_chain_revision == 9U &&
        layer_metrics.effect_pass_count == 5U &&
        layer_metrics.effect_cache_hit == 0U &&
        layer_metrics.effect_uniform_upload_bytes == 96U &&
        layer_metrics.effect_texture_bytes == 64U * 48U * 12U &&
        layer_metrics.effect_texture_generation != 0U &&
        layer_metrics.effect_allocation_count != 0U,
        "changed group-effect chain metrics are invalid");
    require(progpu_native_engine_render(engine, &frame, &metrics) ==
        PROGPU_NATIVE_STATUS_SUCCESS,
        "unchanged group-effect chain replay failed");
    require(progpu_native_engine_get_layer_metrics(
        engine, &layer_metrics) == PROGPU_NATIVE_STATUS_SUCCESS &&
        layer_metrics.effect_count == 2U &&
        layer_metrics.effect_pass_count == 0U &&
        layer_metrics.effect_cache_hit == 1U &&
        layer_metrics.effect_uniform_upload_bytes == 0U,
        "unchanged group-effect chain dispatched work");
    effect_chain.revision = 10U;
    effect_nodes[1].revision = 6U;
    effect_nodes[1].offset_x = 4.0F;
    require(progpu_native_engine_render(engine, &frame, &metrics) ==
        PROGPU_NATIVE_STATUS_SUCCESS,
        "changed group-effect chain replay failed");
    require(progpu_native_engine_get_layer_metrics(
        engine, &layer_metrics) == PROGPU_NATIVE_STATUS_SUCCESS &&
        layer_metrics.content_pass_count == 0U &&
        layer_metrics.effect_pass_count == 5U &&
        layer_metrics.effect_cache_hit == 0U &&
        layer_metrics.effect_uniform_upload_bytes == 32U,
        "changed group-effect chain did not reuse content and uniforms");
    draw_state.group_effect_chain = nullptr;
    require(progpu_native_engine_render(engine, &frame, &metrics) ==
        PROGPU_NATIVE_STATUS_SUCCESS,
        "post-effect retained group replay failed");
    draw_state.group_mask = nullptr;
    draw_state.flags = 0U;
    draw_state.clip_rect = {};
    for (std::uint32_t blend_mode = PROGPU_NATIVE_BLEND_SRC_OVER;
         blend_mode <= PROGPU_NATIVE_BLEND_MODULATE;
         ++blend_mode) {
        draw_state.group_blend_mode = blend_mode;
        require(progpu_native_engine_render(engine, &frame, &metrics) ==
            PROGPU_NATIVE_STATUS_SUCCESS,
            "group blend-mode replay failed");
        require(progpu_native_engine_get_layer_metrics(
            engine, &layer_metrics) == PROGPU_NATIVE_STATUS_SUCCESS &&
            layer_metrics.content_pass_count == 0U &&
            layer_metrics.composite_pass_count == 1U &&
            layer_metrics.cache_hit == 1U &&
            layer_metrics.blend_mode == blend_mode &&
            layer_metrics.blend_source_pass_count <= 1U,
            "group blend-mode metrics are invalid");
        require(progpu_native_engine_render(engine, &frame, &metrics) ==
            PROGPU_NATIVE_STATUS_SUCCESS,
            "stable group blend-mode replay failed");
        require(progpu_native_engine_get_layer_metrics(
            engine, &layer_metrics) == PROGPU_NATIVE_STATUS_SUCCESS &&
            layer_metrics.content_pass_count == 0U &&
            layer_metrics.composite_pass_count == 1U &&
            layer_metrics.cache_hit == 1U &&
            layer_metrics.blend_mode == blend_mode &&
            layer_metrics.blend_source_pass_count == 0U &&
            layer_metrics.blend_pipeline_cache_hit == 1U,
            "stable group blend-mode replay rebuilt retained state");
        if (blend_mode == PROGPU_NATIVE_BLEND_MULTIPLY) {
            require(
                layer_metrics.blend_source_texture_generation != 0U &&
                layer_metrics.blend_source_allocation_count != 0U &&
                layer_metrics.blend_source_texture_bytes == 64U * 48U * 4U,
                "advanced group blend did not expose bounded scratch metrics");
        }
    }
    draw_state.group_blend_mode = PROGPU_NATIVE_BLEND_SRC_OVER;
    draw_state.group_mask = &group_mask;
    draw_state.flags = PROGPU_NATIVE_DRAW_STATE_CLIP_RECT;
    draw_state.clip_rect = {10.25F, 8.25F, 10.5F, 10.5F};
    require(progpu_native_engine_render(engine, &frame, &metrics) ==
        PROGPU_NATIVE_STATUS_SUCCESS,
        "post-blend baseline replay failed");
    std::uint64_t submission{};
    require(progpu_native_engine_get_last_submission(engine, &submission) ==
        PROGPU_NATIVE_STATUS_SUCCESS && submission != 0U,
        "submission token unavailable");
    std::uint8_t complete{};
    require(progpu_native_engine_poll_submission(
        engine, submission, 1U, &complete) == PROGPU_NATIVE_STATUS_SUCCESS &&
        complete != 0U, "ProGPU submission did not complete");

    resolve<WGPUProcTextureViewRelease>(
        api, provider, "wgpuTextureViewRelease")(view);
    resolve<WGPUProcTextureRelease>(
        api, provider, "wgpuTextureRelease")(texture);
    webscene_gpu_external_texture external{};
    external.struct_size = sizeof(external);
    require(api.present(provider, canvas, &external) ==
        WEBSCENE_GPU_STATUS_SUCCESS &&
        external.handle_kind == WEBSCENE_GPU_HANDLE_IOSURFACE &&
        (external.flags & WEBSCENE_GPU_EXTERNAL_TEXTURE_GPU_COMPLETE) != 0U,
        "provider presentation failed");

    verify_and_capture(
        reinterpret_cast<IOSurfaceRef>(external.shared_handle),
        argc == 3 ? argv[2] : nullptr);
    require(api.retain_external(provider, &external) ==
        WEBSCENE_GPU_STATUS_SUCCESS,
        "external texture retain failed");
    api.release_external(provider, &external);
    api.release_external(provider, &external);

    progpu_native_engine_destroy(engine);
    resolve<WGPUProcTextureViewRelease>(
        api, provider, "wgpuTextureViewRelease")(mask_view);
    resolve<WGPUProcTextureRelease>(
        api, provider, "wgpuTextureRelease")(mask_texture);
    api.destroy_canvas(provider, canvas);
    resolve<WGPUProcQueueRelease>(api, provider, "wgpuQueueRelease")(queue);
    resolve<WGPUProcDeviceRelease>(api, provider, "wgpuDeviceRelease")(device);
    api.destroy(provider);
    require(dlclose(module) == 0, "provider unload failed");
    std::printf(
        "ProGPU rendered through WebScene provider '%s' on '%s': "
        "draws=%u submission=%llu capture=%s\n",
        provider_info.name,
        provider_info.adapter,
        metrics.draw_call_count,
        static_cast<unsigned long long>(submission),
        argc == 3 ? argv[2] : "disabled");
    return 0;
}
