#pragma once

#include "progpu_native_mil_visual_clip_fixture.hpp"

#include <algorithm>
#include <cmath>
#include <cstring>

namespace progpu::native::tests {

struct mil_path_join_case {
    bool tiled{};
    bool reversal{};
    bool dashed{};
    bool smooth{};
};

namespace mil_path_join_detail {
using mil_clip_fixture_detail::append;
using mil_clip_fixture_detail::packet;
using mil::command;

template<class T>
T read(std::span<const std::byte> bytes, std::size_t offset) {
    T result{};
    if (offset <= bytes.size() && sizeof(T) <= bytes.size() - offset)
        std::memcpy(&result, bytes.data() + offset, sizeof(T));
    return result;
}

// Literal source stream: one open, unfilled figure. The cubic is collinear,
// but remains an actual cubic segment, selecting MIL's curved-stroke route.
inline std::vector<std::byte> figures(const mil_path_join_case& input) {
    std::vector<std::byte> bytes;
    append(bytes, 184U); append(bytes, 2U);
    append(bytes, 12.25); append(bytes, input.reversal ? 40.25 : 20.25);
    append(bytes, 32.25); append(bytes, 40.25);
    append(bytes, 1U); append(bytes, 0U);
    append(bytes, 0U); append(bytes, 2U); // curves; neither filled nor closed
    append(bytes, 2U); append(bytes, 136U);
    append(bytes, 12.25); append(bytes, 40.25);
    append(bytes, 104U); append(bytes, 0U);
    append(bytes, 2U); append(bytes, 0x20U | (input.smooth ? 8U : 0U));
    append(bytes, 0U); append(bytes, 0U);
    for (const auto point : {std::array{16.25, 40.25},
            std::array{28.25, 40.25}, std::array{32.25, 40.25}}) {
        append(bytes, point[0]); append(bytes, point[1]);
    }
    append(bytes, 1U); append(bytes, 0U); append(bytes, 64U); append(bytes, 0U);
    append(bytes, input.reversal ? 12.25 : 32.25);
    append(bytes, input.reversal ? 40.25 : 20.25);
    return bytes;
}

inline void path(std::vector<std::byte>& batch, const mil_path_join_case& input) {
    const auto data = figures(input);
    append(batch, static_cast<std::uint32_t>(24U + data.size()));
    append(batch, static_cast<std::uint32_t>(command::path_geometry));
    append(batch, 10U); append(batch, 0U); append(batch, 0U);
    append(batch, static_cast<std::uint32_t>(data.size()));
    batch.insert(batch.end(), data.begin(), data.end());
}

inline bool paint(progpu_native_mil_channel* channel, bool tiled, bool green) {
    if (tiled) {
        std::array<std::uint8_t, 16U> pixels{};
        for (std::size_t offset = 0U; offset < pixels.size(); offset += 4U) {
            pixels[offset + (green ? 1U : 0U)] = 255U;
            pixels[offset + 3U] = 255U;
        }
        return progpu_native_mil_channel_set_bitmap_source_rgba8_with_dpi(channel,
            12U, 2U, 2U, 8U, pixels.data(), pixels.size(), 96.0, 96.0) ==
                PROGPU_NATIVE_MIL_STATUS_SUCCESS;
    }
    std::vector<std::byte> batch;
    packet(batch, command::solid_color_brush, 11U, 1.0,
        green ? progpu_native_color{0, 1, 0, 1} : progpu_native_color{1, 0, 0, 1},
        0U, 0U, 0U, 0U);
    return progpu_native_mil_channel_apply(channel, batch.data(), batch.size(), nullptr) ==
        PROGPU_NATIVE_MIL_STATUS_SUCCESS;
}

inline mil_clip_channel create(const mil_path_join_case& input) {
    std::vector<std::byte> batch, content;
    packet(batch, command::channel_create_resource, 7U, 39U);
    packet(batch, command::visual_create, 7U);
    packet(batch, command::visual_set_render_options, 7U, 3U, 1U, 0U, 3U, 0U, 0U, 0U);
    packet(batch, command::channel_create_resource, 8U, 43U);
    packet(batch, command::channel_create_resource, 4U, 47U);
    packet(batch, command::generic_target_create, 4U, std::uint64_t{}, std::uint64_t{}, 64U, 64U, 0U);
    packet(batch, command::target_set_root, 4U, 7U);
    packet(batch, command::channel_create_resource, 10U, 73U);
    path(batch, input);
    packet(batch, command::channel_create_resource, 11U, input.tiled ? 80U : 75U);
    if (input.tiled) {
        packet(batch, command::channel_create_resource, 12U, 95U);
        packet(batch, command::image_brush, 11U, 1.0,
            std::array{0.0, 0.0, 8.0, 8.0}, std::array{0.0, 0.0, 2.0, 2.0},
            0.707, 1.414, 0U, 0U, 0U, 0U, 0U, 0U, 0U, 1U, 4U, 1U, 1U, 0U, 12U);
    } else packet(batch, command::solid_color_brush, 11U, 1.0,
        progpu_native_color{1, 0, 0, 1}, 0U, 0U, 0U, 0U);
    packet(batch, command::channel_create_resource, 13U, 85U);
    if (input.dashed) {
        packet(batch, command::channel_create_resource, 14U, 84U);
        packet(batch, command::dash_style, 14U, 0.0, 0U, 16U, 3.75, 1.25);
    }
    packet(batch, command::pen, 13U, 8.0, 1.0, 11U, 0U, 0U, 0U, 0U, 0U,
        input.dashed ? 14U : 0U);
    packet(content, command::draw_geometry, 0U, 13U, 10U, 0U);
    append(batch, static_cast<std::uint32_t>(16U + content.size()));
    append(batch, static_cast<std::uint32_t>(command::render_data));
    append(batch, 8U); append(batch, static_cast<std::uint32_t>(content.size()));
    batch.insert(batch.end(), content.begin(), content.end());
    packet(batch, command::visual_set_content, 7U, 8U);
    progpu_native_mil_channel* raw{};
    if (progpu_native_mil_channel_create(&raw) != PROGPU_NATIVE_MIL_STATUS_SUCCESS) return {};
    mil_clip_channel channel(raw);
    if (progpu_native_mil_channel_apply(raw, batch.data(), batch.size(), nullptr) !=
            PROGPU_NATIVE_MIL_STATUS_SUCCESS || !paint(raw, input.tiled, false)) return {};
    return channel;
}

inline bool snapshot(progpu_native_mil_channel* channel, std::vector<std::byte>& scene,
    std::uint64_t identity, std::uint64_t generation, bool with_input = true) {
    const progpu_native_mil_scene_build_request request{
        sizeof(progpu_native_mil_scene_build_request),
        with_input ? static_cast<std::uint32_t>(PROGPU_NATIVE_MIL_SCENE_BUILD_REQUEST_HIT_TEST_INDEX) : 0U,
        4U, 0U, identity, generation, 1.0, 1.0, 0U, generation};
    progpu_native_mil_scene_build_result result{}; result.struct_size = sizeof(result);
    std::size_t size{};
    const auto measured = progpu_native_mil_channel_build_scene_with_request(channel, &request,
        nullptr, 0U, &size, nullptr, &result);
    if (measured != PROGPU_NATIVE_MIL_STATUS_SUCCESS) {
        std::fprintf(stderr, "MIL PathJoin scene=%llu generation=%llu input=%u measurement status=%u\n",
            static_cast<unsigned long long>(identity), static_cast<unsigned long long>(generation),
            unsigned(with_input), unsigned(measured));
        return false;
    }
    std::vector<std::byte> candidate(size);
    if (progpu_native_mil_channel_build_scene_with_request(channel, &request,
            candidate.data(), candidate.size(), &size, nullptr, &result) != PROGPU_NATIVE_MIL_STATUS_SUCCESS ||
        size != candidate.size()) return false;
    scene = std::move(candidate);
    return true;
}

inline bool expected_ink(const mil_path_join_case& input, double x, double y) {
    if (input.reversal) return x >= 12.25 && x < 36.25 && y >= 36.25 && y < 44.25;
    const bool horizontal = x >= 12.25 && x < 32.25 && y >= 36.25 && y < 44.25;
    const bool vertical = x >= 28.25 && x < 36.25 &&
        y >= (input.dashed ? 30.25 : 20.25) && y < 40.25;
    const bool clipped_corner = x >= 32.25 && x < 36.25 && y >= 40.25 && y < 44.25 &&
        (x - 32.25) + (y - 40.25) <= 4.0 * std::sqrt(2.0);
    return horizontal || vertical || clipped_corner;
}

template<class Require>
void pixels(std::span<const std::uint8_t> rgba, const mil_path_join_case& input,
    bool green, Require require) {
    require(rgba.size() == 64U * 64U * 4U, "MIL PathJoin capture size changed");
    std::size_t ink{};
    for (unsigned y = 0U; y < 64U; ++y) for (unsigned x = 0U; x < 64U; ++x) {
        const bool inside = expected_ink(input, x + 0.5, y + 0.5);
        ink += inside ? 1U : 0U;
        const auto offset = (y * 64U + x) * 4U;
        for (unsigned component = 0U; component < 4U; ++component) {
            const auto expected = component == 3U || (inside && component == (green ? 1U : 0U)) ? 255U : 0U;
            if (rgba[offset + component] != expected)
                std::fprintf(stderr, "MIL PathJoin %u/%u/%u pixel %u,%u,%u actual=%u expected=%u\n",
                    unsigned(input.tiled), unsigned(input.reversal), unsigned(input.dashed), x, y, component,
                    unsigned(rgba[offset + component]), expected);
            require(rgba[offset + component] == expected, "MIL PathJoin independent full-pixel oracle failed");
        }
    }
    require(ink != 0U, "MIL PathJoin oracle unexpectedly empty");
}

// This inspects the real captured source-input triangles, not raster envelopes.
// All triangles must retain the actual Visual owner and exact original frame.
inline bool input_contains_join(std::span<const std::byte> scene, progpu_native_point point) {
    const auto header = read<progpu_native_scene_header>(scene, 0U);
    bool found = false;
    for (std::uint32_t index = 0U; index < header.resource_count; ++index) {
        const auto resource = read<progpu_native_scene_resource>(scene,
            header.resource_offset + index * sizeof(progpu_native_scene_resource));
        if (resource.kind != PROGPU_NATIVE_SCENE_RESOURCE_HIT_TEST_INDEX) continue;
        const auto page = read<progpu_native_scene_hit_test_index>(scene, resource.payload_offset);
        for (std::uint32_t i = 0U; i < page.primitive_count; ++i) {
            const auto hit = read<progpu_native_hit_test_primitive>(scene,
                resource.auxiliary_offset + page.primitive_offset + i * sizeof(progpu_native_hit_test_primitive));
            if (hit.id != 7 || hit.flags != (PROGPU_NATIVE_HIT_TEST_VISIBLE | PROGPU_NATIVE_HIT_TEST_VISIBLE_TO_INPUT))
                return false;
            if (hit.kind != PROGPU_NATIVE_HIT_TEST_PATH_FILL || hit.data1.y != 3.0F) continue;
            std::array<progpu_native_point, 3U> vertices{};
            for (std::size_t j = 0U; j < vertices.size(); ++j) vertices[j] = read<progpu_native_path_segment>(scene,
                resource.auxiliary_offset + page.path_segment_offset +
                (static_cast<std::size_t>(hit.data1.x) + j) * sizeof(progpu_native_path_segment)).p0;
            bool positive = false, negative = false;
            for (std::size_t j = 0U; j < vertices.size(); ++j) {
                const auto a = vertices[j], b = vertices[(j + 1U) % vertices.size()];
                const double side = double(b.x - a.x) * (point.y - a.y) - double(b.y - a.y) * (point.x - a.x);
                positive |= side > 0.0; negative |= side < 0.0;
            }
            found |= !(positive && negative);
        }
    }
    return found;
}

template<class Require>
void inspect(std::span<const std::byte> scene, const mil_path_join_case& input, Require require) {
    const auto header = read<progpu_native_scene_header>(scene, 0U);
    std::uint32_t joins{}, cubics{};
    for (std::uint32_t index = 0U; index < header.resource_count; ++index) {
        const auto resource = read<progpu_native_scene_resource>(scene,
            header.resource_offset + index * sizeof(progpu_native_scene_resource));
        std::uint64_t offset{}; std::uint32_t count{};
        if (resource.kind == PROGPU_NATIVE_SCENE_RESOURCE_GEOMETRY_BATCH) {
            offset = resource.payload_offset;
            count = resource.payload_size / sizeof(progpu_native_geometry_primitive);
        } else if (resource.kind == PROGPU_NATIVE_SCENE_RESOURCE_LAYER_MASK &&
            read<std::uint32_t>(scene, resource.payload_offset + 4U) == PROGPU_NATIVE_SCENE_LAYER_MASK_GEOMETRY) {
            const auto mask = read<progpu_native_scene_layer_geometry_mask>(scene, resource.payload_offset);
            offset = resource.auxiliary_offset + mask.primitive_offset; count = mask.primitive_count;
        }
        for (std::uint32_t i = 0U; i < count; ++i) {
            const auto primitive = read<progpu_native_geometry_primitive>(scene, offset + i * sizeof(progpu_native_geometry_primitive));
            cubics += primitive.kind == PROGPU_NATIVE_GEOMETRY_CUBIC_BEZIER ? 1U : 0U;
            if (primitive.kind != PROGPU_NATIVE_GEOMETRY_PATH_JOIN) {
                require((primitive.flags & PROGPU_NATIVE_PRIMITIVE_FLAG_WPF_JOIN_SEMANTICS) == 0U,
                    "MIL WPF join flag leaked into body/cap");
                continue;
            }
            ++joins;
            require((primitive.flags & PROGPU_NATIVE_PRIMITIVE_FLAG_WPF_JOIN_SEMANTICS) != 0U &&
                (primitive.flags & PROGPU_NATIVE_PRIMITIVE_FLAG_CLIP_MITER_AT_LIMIT) == 0U,
                "MIL curved/tile join lost its distinct WPF policy");
            require(((primitive.flags & PROGPU_NATIVE_PRIMITIVE_START_CAP_MASK) >> PROGPU_NATIVE_PRIMITIVE_START_CAP_SHIFT) ==
                static_cast<std::uint32_t>(input.smooth ? PROGPU_NATIVE_STROKE_JOIN_ROUND : PROGPU_NATIVE_STROKE_JOIN_MITER),
                "MIL source smooth flag did not select Round");
        }
    }
    require(joins != 0U && cubics != 0U, "MIL fixture bypassed actual curved PathJoin lowering");
    require(input_contains_join(scene, {34.65F, input.reversal ? 40.25F : 42.65F}),
        "MIL source-input join lost coverage beyond the bevel or its exact Visual owner");
    if (!input.reversal) require(!input_contains_join(scene, {35.45F, 43.45F}),
        "MIL source-input join retained an unclipped miter tip");
}
} // namespace mil_path_join_detail

template<class Require>
void verify_mil_path_join_ownership(Require require) {
    using namespace mil_path_join_detail;
    for (bool tiled : {false, true}) for (bool reversal : {false, true}) for (bool dashed : {false, true}) {
        const mil_path_join_case input{tiled, reversal, dashed};
        auto channel = create(input); require(channel != nullptr, "MIL PathJoin source creation failed");
        std::vector<std::byte> scene;
        require(snapshot(channel.get(), scene, 0xC17FU, 1U), "MIL PathJoin source compilation failed");
        inspect(scene, input, require);
        const auto original = scene;
        std::vector<std::byte> batch;
        packet(batch, command::pen, 13U, 8.0, 1.0, 11U, 0U, 0U, 0U, 0U, 3U, dashed ? 14U : 0U);
        require(progpu_native_mil_channel_apply(channel.get(), batch.data(), batch.size(), nullptr) !=
            PROGPU_NATIVE_MIL_STATUS_SUCCESS, "MIL enum3 was silently admitted as WPF source");
        require(snapshot(channel.get(), scene, 0xC17FU, 1U) && scene == original,
            "Rejected MIL pen changed retained source bytes/input");
        require(paint(channel.get(), tiled, true) && snapshot(channel.get(), scene, 0xC17FU, 2U) && scene != original,
            "MIL retained brush dependency failed to invalidate");
        inspect(scene, input, require);
        const auto retained = scene;
        for (const auto dependency : {std::array{10U, 73U}, std::array{13U, 85U},
                std::array{11U, tiled ? 80U : 75U}}) {
            batch.clear(); packet(batch, command::channel_delete_resource, dependency[0], dependency[1]);
            require(progpu_native_mil_channel_apply(channel.get(), batch.data(), batch.size(), nullptr) ==
                PROGPU_NATIVE_MIL_STATUS_INVALID_GRAPH, "Referenced original geometry, pen or brush was deleted");
        }
        require(snapshot(channel.get(), scene, 0xC17FU, 2U) && scene == retained,
            "Failed source deletion changed its exact retained generation");
        auto smooth = input; smooth.smooth = true;
        batch.clear(); path(batch, smooth);
        require(progpu_native_mil_channel_apply(channel.get(), batch.data(), batch.size(), nullptr) ==
            PROGPU_NATIVE_MIL_STATUS_SUCCESS && snapshot(channel.get(), scene, 0xC17FU, 3U),
            "MIL original smooth-join update failed");
        inspect(scene, smooth, require);
        batch.clear(); path(batch, input);
        require(progpu_native_mil_channel_apply(channel.get(), batch.data(), batch.size(), nullptr) ==
            PROGPU_NATIVE_MIL_STATUS_SUCCESS && snapshot(channel.get(), scene, 0xC17FU, 4U),
            "MIL original path reset failed");
        inspect(scene, input, require);
        // Detaching the Visual does not release the separately retained drawing.
        batch.clear(); packet(batch, command::visual_set_content, 7U, 0U);
        require(progpu_native_mil_channel_apply(channel.get(), batch.data(), batch.size(), nullptr) ==
            PROGPU_NATIVE_MIL_STATUS_SUCCESS, "MIL original content detachment failed");
        batch.clear(); packet(batch, command::channel_delete_resource, 10U, 73U);
        require(progpu_native_mil_channel_apply(channel.get(), batch.data(), batch.size(), nullptr) ==
            PROGPU_NATIVE_MIL_STATUS_INVALID_GRAPH, "Detached retained drawing lost its geometry dependency");
        batch.clear(); packet(batch, command::channel_delete_resource, 8U, 43U);
        packet(batch, command::channel_delete_resource, 10U, 73U);
        require(progpu_native_mil_channel_apply(channel.get(), batch.data(), batch.size(), nullptr) ==
            PROGPU_NATIVE_MIL_STATUS_SUCCESS, "Retired drawing did not release its geometry dependency");
    }
}

template<class Render, class Require>
void verify_mil_path_join_pixels(Render render, Require require) {
    using namespace mil_path_join_detail;
    std::uint64_t identity = 0xC180U;
    for (bool tiled : {false, true}) for (bool reversal : {false, true}) for (bool dashed : {false, true}) {
        const mil_path_join_case input{tiled, reversal, dashed};
        std::fprintf(stderr, "MIL PathJoin pixels tiled=%u reversal=%u dashed=%u\n",
            unsigned(tiled), unsigned(reversal), unsigned(dashed));
        auto channel = create(input); require(channel != nullptr, "MIL PathJoin source creation failed");
        std::vector<std::byte> scene;
        require(snapshot(channel.get(), scene, ++identity, 1U), "MIL PathJoin source compilation failed");
        inspect(scene, input, require);
        const auto first = render(false, scene, read<progpu_native_scene_header>(scene, 0U), tiled);
        pixels(first, input, false, require);
        require(first == render(false, scene, read<progpu_native_scene_header>(scene, 0U), tiled),
            "MIL PathJoin cold/warm pixels changed");
        auto independent = create(input); std::vector<std::byte> recreated;
        require(independent != nullptr && snapshot(independent.get(), recreated, ++identity, 1U),
            "Independent MIL PathJoin source failed");
        require(first == render(true, recreated, read<progpu_native_scene_header>(recreated, 0U), tiled),
            "Independent MIL PathJoin source bytes produced different pixels");
        require(paint(channel.get(), tiled, true) && snapshot(channel.get(), scene, identity - 1U, 2U),
            "MIL PathJoin paint generation update failed");
        pixels(render(false, scene, read<progpu_native_scene_header>(scene, 0U), tiled), input, true, require);
    }
}

} // namespace progpu::native::tests
