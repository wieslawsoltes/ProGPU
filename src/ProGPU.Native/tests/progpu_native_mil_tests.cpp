#include "progpu_native_mil.hpp"
#include "progpu_native_mil.h"
#include "progpu_native_text.hpp"
#include "../src/Geometry/progpu_native_arc.hpp"

#include <array>
#include <bit>
#include <cmath>
#include <cstddef>
#include <cstdint>
#include <cstring>
#include <filesystem>
#include <fstream>
#include <iostream>
#include <vector>

namespace {

using progpu::native::mil::batch_metrics;
using progpu::native::mil::channel;
using progpu::native::mil::command;
using progpu::native::mil::status;

void require(bool condition, const char* expression, int line) {
    if (condition) {
        return;
    }
    std::cerr << "line " << line << ": requirement failed: "
              << expression << '\n';
    std::abort();
}

#define PROGPU_REQUIRE(condition) require((condition), #condition, __LINE__)

template<typename T>
void append_value(std::vector<std::byte>& bytes, const T& value) {
    const auto previous = bytes.size();
    bytes.resize(previous + sizeof(T));
    std::memcpy(bytes.data() + previous, &value, sizeof(T));
}

template<typename T>
T read_value(const std::vector<std::byte>& bytes, std::size_t offset) {
    T value{};
    PROGPU_REQUIRE(offset <= bytes.size());
    PROGPU_REQUIRE(sizeof(T) <= bytes.size() - offset);
    std::memcpy(&value, bytes.data() + offset, sizeof(T));
    return value;
}

bool scene_contains_text_style_mode(
    const std::vector<std::byte>& stream,
    std::uint32_t expected_mode) {
    const auto header = read_value<progpu_native_scene_header>(stream, 0U);
    for (std::uint32_t index = 0U; index < header.resource_count; ++index) {
        const auto resource = read_value<progpu_native_scene_resource>(
            stream,
            header.resource_offset +
                index * sizeof(progpu_native_scene_resource));
        if (resource.kind !=
                PROGPU_NATIVE_SCENE_RESOURCE_TEXT_STYLE_TABLE ||
            resource.payload_size %
                sizeof(progpu_native_scene_text_style) != 0U) {
            continue;
        }
        const std::uint32_t style_count = resource.payload_size /
            sizeof(progpu_native_scene_text_style);
        for (std::uint32_t style_index = 0U;
             style_index < style_count;
             ++style_index) {
            const auto style = read_value<progpu_native_scene_text_style>(
                stream,
                resource.payload_offset +
                    style_index * sizeof(progpu_native_scene_text_style));
            if (style.text_rendering_mode == expected_mode) {
                return true;
            }
        }
    }
    return false;
}

template<typename T>
void write_value(
    std::vector<std::byte>& bytes,
    std::size_t offset,
    const T& value) {
    PROGPU_REQUIRE(offset <= bytes.size());
    PROGPU_REQUIRE(sizeof(T) <= bytes.size() - offset);
    std::memcpy(bytes.data() + offset, &value, sizeof(T));
}

template<typename... T>
void append_command(
    std::vector<std::byte>& batch,
    command kind,
    const T&... fields) {
    std::vector<std::byte> packet;
    append_value(packet, static_cast<std::uint32_t>(kind));
    (append_value(packet, fields), ...);
    const auto item_size = static_cast<std::uint32_t>(
        (packet.size() + sizeof(std::uint32_t) + 3U) & ~std::size_t{3U});
    append_value(batch, item_size);
    batch.insert(batch.end(), packet.begin(), packet.end());
    batch.resize(batch.size() + item_size - sizeof(std::uint32_t) - packet.size());
}

void append_create(
    std::vector<std::byte>& batch,
    std::uint32_t handle,
    std::uint32_t type) {
    append_command(batch, command::channel_create_resource, handle, type);
}

void append_render_data(
    std::vector<std::byte>& batch,
    std::uint32_t handle,
    const std::vector<std::byte>& render_data) {
    std::vector<std::byte> packet;
    append_value(packet, static_cast<std::uint32_t>(command::render_data));
    append_value(packet, handle);
    append_value(packet, static_cast<std::uint32_t>(render_data.size()));
    packet.insert(packet.end(), render_data.begin(), render_data.end());
    const auto item_size = static_cast<std::uint32_t>(
        (packet.size() + sizeof(std::uint32_t) + 3U) & ~std::size_t{3U});
    append_value(batch, item_size);
    batch.insert(batch.end(), packet.begin(), packet.end());
    batch.resize(batch.size() + item_size - sizeof(std::uint32_t) - packet.size());
}

void append_glyph_run_create(
    std::vector<std::byte>& batch,
    std::uint32_t handle,
    float origin_x,
    float origin_y,
    float em_size,
    std::span<const std::uint16_t> glyph_indices,
    std::span<const float> advances,
    std::span<const progpu_native_point> offsets,
    double bounds_x,
    double bounds_y,
    double bounds_width,
    double bounds_height) {
    PROGPU_REQUIRE(!glyph_indices.empty());
    PROGPU_REQUIRE(glyph_indices.size() == advances.size());
    PROGPU_REQUIRE(offsets.empty() || offsets.size() == glyph_indices.size());
    constexpr std::size_t fixed_size = 76U;
    const std::size_t payload_size = glyph_indices.size_bytes() +
        advances.size_bytes() + offsets.size_bytes();
    std::vector<std::byte> packet(fixed_size + payload_size);
    write_value(packet, 0U, static_cast<std::uint32_t>(
        command::glyph_run_create));
    write_value(packet, 4U, handle);
    write_value(packet, 16U, static_cast<std::uint16_t>(
        offsets.empty() ? 0U : 0x10U));
    write_value(packet, 20U, origin_x);
    write_value(packet, 24U, origin_y);
    write_value(packet, 28U, em_size);
    write_value(packet, 32U, bounds_x);
    write_value(packet, 40U, bounds_y);
    write_value(packet, 48U, bounds_width);
    write_value(packet, 56U, bounds_height);
    write_value(packet, 64U, static_cast<std::uint16_t>(
        glyph_indices.size()));
    write_value(packet, 68U, std::uint16_t{0U});
    write_value(packet, 72U, std::uint16_t{0U});
    std::size_t payload_offset = fixed_size;
    std::memcpy(
        packet.data() + payload_offset,
        glyph_indices.data(),
        glyph_indices.size_bytes());
    payload_offset += glyph_indices.size_bytes();
    std::memcpy(
        packet.data() + payload_offset,
        advances.data(),
        advances.size_bytes());
    payload_offset += advances.size_bytes();
    if (!offsets.empty()) {
        std::memcpy(
            packet.data() + payload_offset,
            offsets.data(),
            offsets.size_bytes());
    }
    const auto item_size = static_cast<std::uint32_t>(
        (packet.size() + sizeof(std::uint32_t) + 3U) & ~std::size_t{3U});
    append_value(batch, item_size);
    batch.insert(batch.end(), packet.begin(), packet.end());
    batch.resize(
        batch.size() + item_size - sizeof(std::uint32_t) - packet.size());
}

std::vector<std::byte> load_inter_test_font() {
    const auto source = std::filesystem::absolute(
        std::filesystem::path(__FILE__));
    const auto font_path = source.parent_path().parent_path().parent_path() /
        "ProGPU.Fonts.Inter" / "Fonts" / "Inter-Regular.ttf";
    std::ifstream stream(font_path, std::ios::binary | std::ios::ate);
    PROGPU_REQUIRE(stream.good());
    const auto length = stream.tellg();
    PROGPU_REQUIRE(length > 0);
    std::vector<std::byte> bytes(static_cast<std::size_t>(length));
    stream.seekg(0, std::ios::beg);
    stream.read(
        reinterpret_cast<char*>(bytes.data()),
        static_cast<std::streamsize>(bytes.size()));
    PROGPU_REQUIRE(stream.good());
    return bytes;
}

struct mil_gradient_stop {
    double position{};
    progpu_native_color color{};
};

void append_linear_gradient_brush(
    std::vector<std::byte>& batch,
    std::uint32_t handle,
    double opacity,
    double start_x,
    double start_y,
    double end_x,
    double end_y,
    std::uint32_t opacity_animation,
    std::uint32_t transform,
    std::uint32_t relative_transform,
    std::uint32_t interpolation,
    std::uint32_t mapping,
    std::uint32_t spread,
    std::uint32_t start_animation,
    std::uint32_t end_animation,
    std::span<const mil_gradient_stop> stops) {
    std::vector<std::byte> packet;
    append_value(packet, static_cast<std::uint32_t>(
        command::linear_gradient_brush));
    append_value(packet, handle);
    append_value(packet, opacity);
    append_value(packet, start_x);
    append_value(packet, start_y);
    append_value(packet, end_x);
    append_value(packet, end_y);
    append_value(packet, opacity_animation);
    append_value(packet, transform);
    append_value(packet, relative_transform);
    append_value(packet, interpolation);
    append_value(packet, mapping);
    append_value(packet, spread);
    append_value(packet, static_cast<std::uint32_t>(
        stops.size_bytes()));
    append_value(packet, start_animation);
    append_value(packet, end_animation);
    for (const auto& stop : stops) {
        append_value(packet, stop.position);
        append_value(packet, stop.color);
    }
    PROGPU_REQUIRE(packet.size() == 84U + stops.size_bytes());
    append_value(batch, static_cast<std::uint32_t>(
        packet.size() + sizeof(std::uint32_t)));
    batch.insert(batch.end(), packet.begin(), packet.end());
}

void append_radial_gradient_brush(
    std::vector<std::byte>& batch,
    std::uint32_t handle,
    double opacity,
    double center_x,
    double center_y,
    double radius_x,
    double radius_y,
    double origin_x,
    double origin_y,
    std::uint32_t interpolation,
    std::uint32_t mapping,
    std::uint32_t spread,
    std::uint32_t radius_x_animation,
    std::uint32_t radius_y_animation,
    std::span<const mil_gradient_stop> stops) {
    std::vector<std::byte> packet;
    append_value(packet, static_cast<std::uint32_t>(
        command::radial_gradient_brush));
    append_value(packet, handle);
    append_value(packet, opacity);
    append_value(packet, center_x);
    append_value(packet, center_y);
    append_value(packet, radius_x);
    append_value(packet, radius_y);
    append_value(packet, origin_x);
    append_value(packet, origin_y);
    append_value(packet, 0U);
    append_value(packet, 0U);
    append_value(packet, 0U);
    append_value(packet, interpolation);
    append_value(packet, mapping);
    append_value(packet, spread);
    append_value(packet, static_cast<std::uint32_t>(
        stops.size_bytes()));
    append_value(packet, 0U);
    append_value(packet, radius_x_animation);
    append_value(packet, radius_y_animation);
    append_value(packet, 0U);
    for (const auto& stop : stops) {
        append_value(packet, stop.position);
        append_value(packet, stop.color);
    }
    PROGPU_REQUIRE(packet.size() == 108U + stops.size_bytes());
    append_value(batch, static_cast<std::uint32_t>(
        packet.size() + sizeof(std::uint32_t)));
    batch.insert(batch.end(), packet.begin(), packet.end());
}

void append_path_geometry(
    std::vector<std::byte>& batch,
    std::uint32_t handle,
    std::uint32_t transform_handle,
    std::uint32_t fill_rule,
    const std::vector<std::byte>& figures) {
    std::vector<std::byte> packet;
    append_value(packet, static_cast<std::uint32_t>(command::path_geometry));
    append_value(packet, handle);
    append_value(packet, transform_handle);
    append_value(packet, fill_rule);
    append_value(packet, static_cast<std::uint32_t>(figures.size()));
    packet.insert(packet.end(), figures.begin(), figures.end());
    const auto item_size = static_cast<std::uint32_t>(
        (packet.size() + sizeof(std::uint32_t) + 3U) & ~std::size_t{3U});
    append_value(batch, item_size);
    batch.insert(batch.end(), packet.begin(), packet.end());
    batch.resize(batch.size() + item_size - sizeof(std::uint32_t) - packet.size());
}

void append_geometry_group(
    std::vector<std::byte>& batch,
    std::uint32_t handle,
    std::uint32_t transform_handle,
    std::uint32_t fill_rule,
    std::span<const std::uint32_t> children) {
    std::vector<std::byte> packet;
    append_value(packet, static_cast<std::uint32_t>(command::geometry_group));
    append_value(packet, handle);
    append_value(packet, transform_handle);
    append_value(packet, fill_rule);
    append_value(
        packet,
        static_cast<std::uint32_t>(children.size_bytes()));
    for (const std::uint32_t child : children) {
        append_value(packet, child);
    }
    append_value(
        batch,
        static_cast<std::uint32_t>(packet.size() + sizeof(std::uint32_t)));
    batch.insert(batch.end(), packet.begin(), packet.end());
}

void append_transform_group(
    std::vector<std::byte>& batch,
    std::uint32_t handle,
    std::span<const std::uint32_t> children) {
    std::vector<std::byte> packet;
    append_value(packet, static_cast<std::uint32_t>(command::transform_group));
    append_value(packet, handle);
    append_value(
        packet,
        static_cast<std::uint32_t>(children.size_bytes()));
    for (const std::uint32_t child : children) {
        append_value(packet, child);
    }
    append_value(
        batch,
        static_cast<std::uint32_t>(packet.size() + sizeof(std::uint32_t)));
    batch.insert(batch.end(), packet.begin(), packet.end());
}

std::vector<std::byte> make_rectangle_path_figures(
    double left,
    double top,
    double right,
    double bottom) {
    constexpr std::uint32_t line_size = 32U;
    constexpr std::uint32_t figure_size = 40U + 3U * line_size;
    constexpr std::uint32_t figures_size = 48U + figure_size;
    std::vector<std::byte> figures;
    append_value(figures, figures_size);
    append_value(figures, 0x02U);
    append_value(figures, left);
    append_value(figures, top);
    append_value(figures, right);
    append_value(figures, bottom);
    append_value(figures, 1U);
    append_value(figures, 0U);

    append_value(figures, 0U);
    append_value(figures, 0x0cU);
    append_value(figures, 3U);
    append_value(figures, figure_size);
    append_value(figures, left);
    append_value(figures, top);
    append_value(figures, 40U + 2U * line_size);
    append_value(figures, 0U);

    const std::array endpoints{
        std::array{right, top},
        std::array{right, bottom},
        std::array{left, bottom}};
    std::uint32_t previous_size = 0U;
    for (const auto& endpoint : endpoints) {
        append_value(figures, 1U);
        append_value(figures, 0U);
        append_value(figures, previous_size);
        append_value(figures, 0U);
        append_value(figures, endpoint[0]);
        append_value(figures, endpoint[1]);
        previous_size = line_size;
    }
    PROGPU_REQUIRE(figures.size() == figures_size);
    return figures;
}

std::vector<std::byte> make_curve_path_figures() {
    constexpr std::uint32_t line_size = 32U;
    constexpr std::uint32_t quadratic_size = 48U;
    constexpr std::uint32_t cubic_size = 64U;
    constexpr std::uint32_t figure_size =
        40U + line_size + quadratic_size + cubic_size;
    constexpr std::uint32_t figures_size = 48U + figure_size;
    std::vector<std::byte> figures;
    append_value(figures, figures_size);
    append_value(figures, 0x02U);
    append_value(figures, 6.0);
    append_value(figures, 2.0);
    append_value(figures, 15.0);
    append_value(figures, 8.0);
    append_value(figures, 1U);
    append_value(figures, 0U);

    append_value(figures, 0U);
    append_value(figures, 0x0eU);
    append_value(figures, 3U);
    append_value(figures, figure_size);
    append_value(figures, 6.0);
    append_value(figures, 4.0);
    append_value(figures, 40U + line_size + quadratic_size);
    append_value(figures, 0U);

    append_value(figures, 1U);
    append_value(figures, 0U);
    append_value(figures, 0U);
    append_value(figures, 0U);
    append_value(figures, 8.0);
    append_value(figures, 4.0);

    append_value(figures, 3U);
    append_value(figures, 0x20U);
    append_value(figures, line_size);
    append_value(figures, 0U);
    append_value(figures, 10.0);
    append_value(figures, 2.0);
    append_value(figures, 12.0);
    append_value(figures, 6.0);

    append_value(figures, 2U);
    append_value(figures, 0x20U);
    append_value(figures, quadratic_size);
    append_value(figures, 0U);
    append_value(figures, 13.0);
    append_value(figures, 8.0);
    append_value(figures, 14.0);
    append_value(figures, 3.0);
    append_value(figures, 15.0);
    append_value(figures, 7.0);
    PROGPU_REQUIRE(figures.size() == figures_size);
    return figures;
}

std::vector<std::byte> make_arc_path_figures() {
    constexpr std::uint32_t arc_size = 64U;
    constexpr std::uint32_t figure_size = 40U + arc_size;
    constexpr std::uint32_t figures_size = 48U + figure_size;
    std::vector<std::byte> figures;
    append_value(figures, figures_size);
    append_value(figures, 0x02U);
    append_value(figures, 1.0);
    append_value(figures, 2.0);
    append_value(figures, 9.0);
    append_value(figures, 8.0);
    append_value(figures, 1U);
    append_value(figures, 0U);

    append_value(figures, 0U);
    append_value(figures, 0x0eU);
    append_value(figures, 1U);
    append_value(figures, figure_size);
    append_value(figures, 1.0);
    append_value(figures, 2.0);
    append_value(figures, 40U);
    append_value(figures, 0U);

    append_value(figures, 4U);
    append_value(figures, 0x20U);
    append_value(figures, 0U);
    append_value(figures, 0U);
    append_value(figures, 9.0);
    append_value(figures, 8.0);
    append_value(figures, 8.0);
    append_value(figures, 6.0);
    append_value(figures, 30.0);
    append_value(figures, 1U);
    append_value(figures, 0U);
    PROGPU_REQUIRE(figures.size() == figures_size);
    return figures;
}

std::vector<std::byte> make_single_bezier_path_figures(
    std::uint32_t segment_type,
    std::span<const std::array<double, 2U>> points) {
    const std::size_t expected_point_count = segment_type == 3U ? 2U : 3U;
    PROGPU_REQUIRE(
        (segment_type == 2U || segment_type == 3U) &&
        points.size() == expected_point_count);
    const auto segment_size = static_cast<std::uint32_t>(
        16U + points.size() * 16U);
    const std::uint32_t figure_size = 40U + segment_size;
    const std::uint32_t figures_size = 48U + figure_size;
    std::vector<std::byte> figures;
    append_value(figures, figures_size);
    append_value(figures, 0x02U);
    append_value(figures, 1.0);
    append_value(figures, 1.0);
    append_value(figures, 12.0);
    append_value(figures, 10.0);
    append_value(figures, 1U);
    append_value(figures, 0U);

    append_value(figures, 0U);
    append_value(figures, 0x0aU);
    append_value(figures, 1U);
    append_value(figures, figure_size);
    append_value(figures, 1.0);
    append_value(figures, 2.0);
    append_value(figures, 40U);
    append_value(figures, 0U);

    append_value(figures, segment_type);
    append_value(figures, 0x20U);
    append_value(figures, 0U);
    append_value(figures, 0U);
    for (const auto& point : points) {
        append_value(figures, point[0]);
        append_value(figures, point[1]);
    }
    PROGPU_REQUIRE(figures.size() == figures_size);
    return figures;
}

void append_dash_style(
    std::vector<std::byte>& batch,
    std::uint32_t handle,
    double offset,
    std::uint32_t offset_animations,
    std::span<const double> intervals) {
    std::vector<std::byte> packet;
    append_value(packet, static_cast<std::uint32_t>(command::dash_style));
    append_value(packet, handle);
    append_value(packet, offset);
    append_value(packet, offset_animations);
    append_value(
        packet,
        static_cast<std::uint32_t>(intervals.size_bytes()));
    for (const double interval : intervals) {
        append_value(packet, interval);
    }
    append_value(
        batch,
        static_cast<std::uint32_t>(packet.size() + sizeof(std::uint32_t)));
    batch.insert(batch.end(), packet.begin(), packet.end());
}

bool channel_retains_visual_target_graph() {
    constexpr std::uint32_t visual_type = 39U;
    constexpr std::uint32_t render_data_type = 43U;
    constexpr std::uint32_t target_type = 47U;
    std::vector<std::byte> batch;
    append_create(batch, 1U, visual_type);
    append_create(batch, 2U, visual_type);
    append_create(batch, 3U, render_data_type);
    append_create(batch, 4U, target_type);
    append_command(batch, command::visual_create, 1U);
    append_command(batch, command::visual_create, 2U);
    append_command(batch, command::visual_set_offset, 1U, 12.5, -3.0);
    append_command(batch, command::visual_set_alpha, 1U, 0.625);
    append_command(batch, command::visual_set_content, 1U, 3U);
    append_command(batch, command::visual_insert_child_at, 1U, 2U, 0U);

    const std::array<std::byte, 8> render_data{
        std::byte{8}, std::byte{0}, std::byte{0}, std::byte{0},
        std::byte{0x40}, std::byte{0}, std::byte{0}, std::byte{0}};
    std::vector<std::byte> render_packet;
    append_value(render_packet, static_cast<std::uint32_t>(command::render_data));
    append_value(render_packet, 3U);
    append_value(render_packet, static_cast<std::uint32_t>(render_data.size()));
    render_packet.insert(
        render_packet.end(), render_data.begin(), render_data.end());
    append_value(batch, static_cast<std::uint32_t>(render_packet.size() + 4U));
    batch.insert(batch.end(), render_packet.begin(), render_packet.end());

    append_command(
        batch,
        command::generic_target_create,
        4U,
        std::uint64_t{0U},
        std::uint64_t{0U},
        640U,
        480U,
        0U);
    append_command(batch, command::target_set_root, 4U, 1U);
    append_command(
        batch,
        command::target_set_clear_color,
        4U,
        0.1F,
        0.2F,
        0.3F,
        1.0F);
    append_command(batch, command::target_set_flags, 4U, 7U);

    channel state;
    batch_metrics metrics{};
    PROGPU_REQUIRE(state.apply(batch, &metrics) == status::success);
    PROGPU_REQUIRE(metrics.command_count == 15U);
    PROGPU_REQUIRE(metrics.supported_command_count == 15U);
    PROGPU_REQUIRE(metrics.created_resource_count == 4U);
    PROGPU_REQUIRE(state.resource_count() == 4U);
    PROGPU_REQUIRE(state.resource_generation(1U) == 6U);

    progpu::native::mil::visual_snapshot visual{};
    PROGPU_REQUIRE(state.try_get_visual(1U, visual));
    PROGPU_REQUIRE(visual.offset_x == 12.5);
    PROGPU_REQUIRE(visual.offset_y == -3.0);
    PROGPU_REQUIRE(visual.opacity == 0.625);
    PROGPU_REQUIRE(visual.content_handle == 3U);
    PROGPU_REQUIRE(visual.child_count == 1U);
    std::uint32_t child = 0U;
    PROGPU_REQUIRE(state.try_get_visual_child(1U, 0U, child));
    PROGPU_REQUIRE(child == 2U);

    progpu::native::mil::target_snapshot target{};
    PROGPU_REQUIRE(state.try_get_target(4U, target));
    PROGPU_REQUIRE(target.root_handle == 1U);
    PROGPU_REQUIRE(target.clear_red == 0.1F);
    PROGPU_REQUIRE(target.clear_green == 0.2F);
    PROGPU_REQUIRE(target.clear_blue == 0.3F);
    PROGPU_REQUIRE(target.clear_alpha == 1.0F);
    PROGPU_REQUIRE(target.flags == 7U);
    return true;
}

bool failed_batches_roll_back() {
    channel state;
    std::vector<std::byte> seed;
    append_create(seed, 1U, 39U);
    append_command(seed, command::visual_create, 1U);
    PROGPU_REQUIRE(state.apply(seed) == status::success);
    const auto generation = state.resource_generation(1U);

    std::vector<std::byte> invalid;
    append_command(invalid, command::visual_set_alpha, 1U, 0.25);
    append_command(invalid, command::visual_insert_child_at, 1U, 99U, 0U);
    PROGPU_REQUIRE(state.apply(invalid) == status::invalid_handle);
    progpu::native::mil::visual_snapshot snapshot{};
    PROGPU_REQUIRE(state.try_get_visual(1U, snapshot));
    PROGPU_REQUIRE(snapshot.opacity == 1.0);
    PROGPU_REQUIRE(state.resource_generation(1U) == generation);
    return true;
}

bool invalid_visual_graphs_fail_closed() {
    channel state;
    std::vector<std::byte> seed;
    for (std::uint32_t handle = 1U; handle <= 3U; ++handle) {
        append_create(seed, handle, 39U);
        append_command(seed, command::visual_create, handle);
    }
    append_command(seed, command::visual_insert_child_at, 1U, 2U, 0U);
    PROGPU_REQUIRE(state.apply(seed) == status::success);

    std::vector<std::byte> cycle;
    append_command(cycle, command::visual_insert_child_at, 2U, 1U, 0U);
    PROGPU_REQUIRE(state.apply(cycle) == status::invalid_graph);

    std::vector<std::byte> second_parent;
    append_command(
        second_parent, command::visual_insert_child_at, 3U, 2U, 0U);
    PROGPU_REQUIRE(state.apply(second_parent) == status::invalid_graph);
    progpu::native::mil::visual_snapshot root{};
    progpu::native::mil::visual_snapshot second{};
    PROGPU_REQUIRE(state.try_get_visual(1U, root));
    PROGPU_REQUIRE(state.try_get_visual(3U, second));
    PROGPU_REQUIRE(root.child_count == 1U);
    PROGPU_REQUIRE(second.child_count == 0U);
    return true;
}

bool solid_rectangle_compiles_to_semantic_scene() {
    constexpr std::uint32_t visual_type = 39U;
    constexpr std::uint32_t render_data_type = 43U;
    constexpr std::uint32_t target_type = 47U;
    constexpr std::uint32_t solid_brush_type = 75U;
    constexpr std::uint32_t root = 1U;
    constexpr std::uint32_t child = 2U;
    constexpr std::uint32_t content = 3U;
    constexpr std::uint32_t target = 4U;
    constexpr std::uint32_t brush = 5U;

    std::vector<std::byte> batch;
    append_create(batch, root, visual_type);
    append_create(batch, child, visual_type);
    append_create(batch, content, render_data_type);
    append_create(batch, target, target_type);
    append_create(batch, brush, solid_brush_type);
    append_command(batch, command::visual_create, root);
    append_command(batch, command::visual_create, child);
    append_command(batch, command::visual_set_offset, root, 10.0, 20.0);
    append_command(batch, command::visual_set_alpha, root, 0.8);
    append_command(
        batch,
        command::visual_set_render_options,
        root,
        0x3bU,
        1U,
        0U,
        3U,
        1U,
        3U,
        1U);
    append_command(batch, command::visual_set_offset, child, 3.0, 4.0);
    append_command(batch, command::visual_set_alpha, child, 0.5);
    append_command(batch, command::visual_set_content, child, content);
    append_command(batch, command::visual_insert_child_at, root, child, 0U);

    const progpu_native_color color{0.25F, 0.5F, 0.75F, 0.9F};
    append_command(
        batch,
        command::solid_color_brush,
        brush,
        0.75,
        color,
        0U,
        0U,
        0U,
        0U);
    std::vector<std::byte> nested;
    append_command(nested, command::push_opacity, 0.5);
    append_command(
        nested,
        command::draw_rectangle,
        2.0,
        6.0,
        30.0,
        40.0,
        brush,
        0U);
    append_command(
        nested,
        command::draw_ellipse,
        5.0,
        9.0,
        7.0,
        11.0,
        brush,
        0U);
    append_command(
        nested,
        command::draw_rounded_rectangle,
        1.0,
        3.0,
        20.0,
        30.0,
        4.0,
        4.0,
        brush,
        0U);
    append_command(nested, command::pop);
    append_render_data(batch, content, nested);
    append_command(
        batch,
        command::generic_target_create,
        target,
        std::uint64_t{0U},
        std::uint64_t{0U},
        640U,
        480U,
        0U);
    append_command(batch, command::target_set_root, target, root);

    channel state;
    PROGPU_REQUIRE(state.apply(batch) == status::success);
    std::vector<std::byte> stream;
    progpu::native::mil::scene_metrics metrics{};
    PROGPU_REQUIRE(
        state.build_scene(target, 9001U, 7U, stream, &metrics) ==
        status::success);
    PROGPU_REQUIRE(metrics.visual_count == 2U);
    PROGPU_REQUIRE(metrics.rectangle_count == 1U);
    PROGPU_REQUIRE(metrics.ellipse_count == 1U);
    PROGPU_REQUIRE(metrics.rounded_rectangle_count == 1U);
    PROGPU_REQUIRE(metrics.brush_count == 1U);
    PROGPU_REQUIRE(metrics.maximum_visual_depth == 2U);
    PROGPU_REQUIRE(metrics.stream_bytes == stream.size());

    const auto header = read_value<progpu_native_scene_header>(stream, 0U);
    PROGPU_REQUIRE(header.scene_id == 9001U);
    PROGPU_REQUIRE(header.generation == 7U);
    PROGPU_REQUIRE(header.command_count == 9U);
    PROGPU_REQUIRE(header.resource_count == 7U);

    bool found_child_state = false;
    bool found_nested_opacity_state = false;
    bool found_rectangle = false;
    bool found_ellipse = false;
    bool found_rounded_rectangle = false;
    bool found_brush = false;
    for (std::uint32_t index = 0U; index < header.resource_count; ++index) {
        const auto record = read_value<progpu_native_scene_resource>(
            stream,
            header.resource_offset +
                index * sizeof(progpu_native_scene_resource));
        if (record.kind == PROGPU_NATIVE_SCENE_RESOURCE_STATE) {
            const auto scene_state = read_value<progpu_native_scene_state>(
                stream, record.payload_offset);
            if (scene_state.transform.m31 == 13.0F &&
                scene_state.transform.m32 == 24.0F) {
                if (scene_state.opacity == 0.4F) {
                    found_child_state = true;
                } else if (scene_state.opacity == 0.2F) {
                    found_nested_opacity_state = true;
                }
            }
        } else if (
            record.kind == PROGPU_NATIVE_SCENE_RESOURCE_ANALYTIC_BATCH) {
            const auto primitive =
                read_value<progpu_native_analytic_primitive>(
                    stream, record.payload_offset);
            PROGPU_REQUIRE(
                (primitive.flags &
                    PROGPU_NATIVE_PRIMITIVE_FLAG_EDGE_ALIASED) != 0U);
            if (primitive.kind == PROGPU_NATIVE_PRIMITIVE_RECTANGLE) {
                PROGPU_REQUIRE(primitive.x == 2.0F);
                PROGPU_REQUIRE(primitive.y == 6.0F);
                PROGPU_REQUIRE(primitive.width == 30.0F);
                PROGPU_REQUIRE(primitive.height == 40.0F);
                found_rectangle = true;
            } else if (primitive.kind == PROGPU_NATIVE_PRIMITIVE_ELLIPSE) {
                PROGPU_REQUIRE(primitive.x == -2.0F);
                PROGPU_REQUIRE(primitive.y == -2.0F);
                PROGPU_REQUIRE(primitive.width == 14.0F);
                PROGPU_REQUIRE(primitive.height == 22.0F);
                found_ellipse = true;
            } else if (
                primitive.kind ==
                PROGPU_NATIVE_PRIMITIVE_ROUNDED_RECTANGLE) {
                PROGPU_REQUIRE(primitive.x == 1.0F);
                PROGPU_REQUIRE(primitive.y == 3.0F);
                PROGPU_REQUIRE(primitive.width == 20.0F);
                PROGPU_REQUIRE(primitive.height == 30.0F);
                PROGPU_REQUIRE(primitive.corner_radius == 4.0F);
                found_rounded_rectangle = true;
            } else {
                PROGPU_REQUIRE(false);
            }
        } else if (record.kind == PROGPU_NATIVE_SCENE_RESOURCE_BRUSH_TABLE) {
            const auto scene_brush = read_value<progpu_native_scene_brush>(
                stream, record.payload_offset);
            PROGPU_REQUIRE(scene_brush.opacity == 0.75F);
            PROGPU_REQUIRE(scene_brush.colors[0].r == color.r);
            PROGPU_REQUIRE(scene_brush.colors[0].g == color.g);
            PROGPU_REQUIRE(scene_brush.colors[0].b == color.b);
            PROGPU_REQUIRE(scene_brush.colors[0].a == color.a);
            found_brush = true;
        }
    }
    PROGPU_REQUIRE(found_child_state);
    PROGPU_REQUIRE(found_nested_opacity_state);
    PROGPU_REQUIRE(found_rectangle);
    PROGPU_REQUIRE(found_ellipse);
    PROGPU_REQUIRE(found_rounded_rectangle);
    PROGPU_REQUIRE(found_brush);

    progpu_native_mil_channel* native_channel = nullptr;
    PROGPU_REQUIRE(
        progpu_native_mil_channel_create(&native_channel) ==
        PROGPU_NATIVE_MIL_STATUS_SUCCESS);
    PROGPU_REQUIRE(
        progpu_native_mil_channel_apply(
            native_channel, batch.data(), batch.size(), nullptr) ==
        PROGPU_NATIVE_MIL_STATUS_SUCCESS);
    progpu_native_mil_scene_metrics abi_metrics{};
    abi_metrics.struct_size = sizeof(abi_metrics);
    std::size_t required_bytes = 0U;
    PROGPU_REQUIRE(
        progpu_native_mil_channel_build_scene(
            native_channel,
            target,
            9001U,
            7U,
            nullptr,
            0U,
            &required_bytes,
            &abi_metrics) == PROGPU_NATIVE_MIL_STATUS_SUCCESS);
    PROGPU_REQUIRE(required_bytes == stream.size());
    PROGPU_REQUIRE(abi_metrics.visual_count == 2U);
    PROGPU_REQUIRE(abi_metrics.rectangle_count == 1U);
    PROGPU_REQUIRE(abi_metrics.ellipse_count == 1U);
    PROGPU_REQUIRE(abi_metrics.rounded_rectangle_count == 1U);
    alignas(progpu_native_mil_scene_metrics)
        std::array<std::byte, sizeof(progpu_native_mil_scene_metrics)>
            legacy_metrics_storage{};
    legacy_metrics_storage.fill(std::byte{0x5a});
    constexpr std::uint32_t legacy_metrics_size = 32U;
    std::memcpy(
        legacy_metrics_storage.data(),
        &legacy_metrics_size,
        sizeof(legacy_metrics_size));
    std::size_t legacy_required_bytes = 0U;
    PROGPU_REQUIRE(
        progpu_native_mil_channel_build_scene(
            native_channel,
            target,
            9001U,
            7U,
            nullptr,
            0U,
            &legacy_required_bytes,
            reinterpret_cast<progpu_native_mil_scene_metrics*>(
                legacy_metrics_storage.data())) ==
        PROGPU_NATIVE_MIL_STATUS_SUCCESS);
    PROGPU_REQUIRE(legacy_required_bytes == stream.size());
    for (std::size_t index = legacy_metrics_size;
         index < legacy_metrics_storage.size();
         ++index) {
        PROGPU_REQUIRE(legacy_metrics_storage[index] == std::byte{0x5a});
    }
    std::vector<std::byte> abi_stream(required_bytes);
    std::size_t written_bytes = 0U;
    PROGPU_REQUIRE(
        progpu_native_mil_channel_build_scene(
            native_channel,
            target,
            9001U,
            7U,
            abi_stream.data(),
            abi_stream.size() - 1U,
            &written_bytes,
            nullptr) == PROGPU_NATIVE_MIL_STATUS_CAPACITY_EXCEEDED);
    PROGPU_REQUIRE(written_bytes == required_bytes);
    PROGPU_REQUIRE(
        progpu_native_mil_channel_build_scene(
            native_channel,
            target,
            9001U,
            7U,
            abi_stream.data(),
            abi_stream.size(),
            &written_bytes,
            nullptr) == PROGPU_NATIVE_MIL_STATUS_SUCCESS);
    PROGPU_REQUIRE(abi_stream == stream);
    progpu_native_mil_channel_destroy(native_channel);

    std::vector<std::byte> unsupported_options;
    append_command(
        unsupported_options,
        command::visual_set_render_options,
        root,
        0x04U,
        0U,
        0U,
        0U,
        0U,
        0U,
        0U);
    PROGPU_REQUIRE(
        state.apply(unsupported_options) == status::unsupported_command);
    std::vector<std::byte> malformed_options;
    append_command(
        malformed_options,
        command::visual_set_render_options,
        root,
        0x40U,
        0U,
        0U,
        0U,
        0U,
        0U,
        0U);
    PROGPU_REQUIRE(
        state.apply(malformed_options) == status::malformed_batch);
    malformed_options.clear();
    append_command(
        malformed_options,
        command::visual_set_render_options,
        root,
        0x10U,
        0U,
        0U,
        0U,
        0U,
        4U,
        0U);
    PROGPU_REQUIRE(
        state.apply(malformed_options) == status::malformed_batch);
    malformed_options.clear();
    append_command(
        malformed_options,
        command::visual_set_render_options,
        root,
        0U,
        0U,
        0U,
        0U,
        0U,
        1U,
        0U);
    PROGPU_REQUIRE(
        state.apply(malformed_options) == status::malformed_batch);
    return true;
}

bool visual_clips_compile_to_exact_semantic_state() {
    constexpr std::uint32_t root = 1U;
    constexpr std::uint32_t child = 2U;
    constexpr std::uint32_t content = 3U;
    constexpr std::uint32_t target = 4U;
    constexpr std::uint32_t brush = 5U;
    constexpr std::uint32_t clip = 6U;
    constexpr std::uint32_t transform = 7U;
    constexpr std::uint32_t ellipse = 8U;

    std::vector<std::byte> batch;
    append_create(batch, root, 39U);
    append_create(batch, child, 39U);
    append_create(batch, content, 43U);
    append_create(batch, target, 47U);
    append_create(batch, brush, 75U);
    append_create(batch, clip, 69U);
    append_create(batch, transform, 66U);
    append_create(batch, ellipse, 70U);
    append_command(batch, command::visual_create, root);
    append_command(batch, command::visual_create, child);
    append_command(
        batch,
        command::matrix_transform,
        transform,
        2.0,
        0.0,
        0.0,
        2.0,
        0.0,
        0.0,
        0U);
    append_command(
        batch, command::visual_set_transform, root, transform);
    append_command(
        batch, command::visual_set_offset, child, 3.4, 4.7);
    append_command(batch, command::visual_set_clip, child, clip);
    append_command(
        batch,
        command::visual_set_scrollable_area_clip,
        child,
        2.2,
        3.2,
        20.8,
        15.8,
        1U);
    append_command(batch, command::visual_set_content, child, content);
    append_command(
        batch, command::visual_insert_child_at, root, child, 0U);
    append_command(
        batch,
        command::solid_color_brush,
        brush,
        1.0,
        progpu_native_color{0.2F, 0.7F, 1.0F, 1.0F},
        0U,
        0U,
        0U,
        0U);
    append_command(
        batch,
        command::rectangle_geometry,
        clip,
        0.0,
        0.0,
        0.0,
        0.0,
        40.0,
        40.0,
        0U,
        0U,
        0U,
        0U);
    append_command(
        batch,
        command::ellipse_geometry,
        ellipse,
        10.0,
        10.0,
        20.0,
        20.0,
        0U,
        0U,
        0U,
        0U);
    std::vector<std::byte> nested;
    append_command(
        nested,
        command::draw_rectangle,
        -10.0,
        -10.0,
        100.0,
        100.0,
        brush,
        0U);
    append_render_data(batch, content, nested);
    append_command(
        batch,
        command::generic_target_create,
        target,
        std::uint64_t{0U},
        std::uint64_t{0U},
        64U,
        64U,
        0U);
    append_command(batch, command::target_set_root, target, root);

    channel state;
    PROGPU_REQUIRE(state.apply(batch) == status::success);
    std::vector<std::byte> stream;
    progpu::native::mil::scene_metrics metrics{};
    PROGPU_REQUIRE(
        state.build_scene(target, 9010U, 1U, stream, &metrics) ==
        status::success);
    const auto header = read_value<progpu_native_scene_header>(stream, 0U);
    bool found_clip = false;
    for (std::uint32_t index = 0U; index < header.resource_count; ++index) {
        const auto resource = read_value<progpu_native_scene_resource>(
            stream,
            header.resource_offset +
                index * sizeof(progpu_native_scene_resource));
        if (resource.kind != PROGPU_NATIVE_SCENE_RESOURCE_STATE) {
            continue;
        }
        const auto scene_state = read_value<progpu_native_scene_state>(
            stream, resource.payload_offset);
        if (scene_state.transform.m11 == 2.0F &&
            scene_state.transform.m22 == 2.0F &&
            scene_state.transform.m31 == 6.0F &&
            scene_state.transform.m32 == 9.0F &&
            (scene_state.flags & PROGPU_NATIVE_SCENE_STATE_CLIP_RECT) != 0U &&
            scene_state.clip_rect.x == 6.0F &&
            scene_state.clip_rect.y == 9.0F &&
            scene_state.clip_rect.width == 40.0F &&
            scene_state.clip_rect.height == 29.0F) {
            found_clip = true;
        }
    }
    PROGPU_REQUIRE(found_clip);

    std::vector<std::byte> unsupported_clip;
    append_command(
        unsupported_clip, command::visual_set_clip, child, ellipse);
    PROGPU_REQUIRE(state.apply(unsupported_clip) == status::success);
    PROGPU_REQUIRE(
        state.build_scene(target, 9010U, 2U, stream, &metrics) ==
        status::unsupported_command);

    std::vector<std::byte> clear_clip;
    append_command(clear_clip, command::visual_set_clip, child, 0U);
    append_command(
        clear_clip,
        command::visual_set_scrollable_area_clip,
        child,
        0.0,
        0.0,
        0.0,
        0.0,
        0U);
    PROGPU_REQUIRE(state.apply(clear_clip) == status::success);
    std::vector<std::byte> delete_clip;
    append_command(
        delete_clip,
        command::channel_delete_resource,
        clip,
        69U);
    PROGPU_REQUIRE(state.apply(delete_clip) == status::success);

    std::vector<std::byte> malformed_clip;
    append_command(
        malformed_clip,
        command::visual_set_scrollable_area_clip,
        child,
        0.0,
        0.0,
        -1.0,
        1.0,
        1U);
    PROGPU_REQUIRE(
        state.apply(malformed_clip) == status::malformed_batch);
    return true;
}

bool visual_solid_opacity_mask_composes_and_updates() {
    constexpr std::uint32_t visual = 1U;
    constexpr std::uint32_t content = 2U;
    constexpr std::uint32_t target = 3U;
    constexpr std::uint32_t brush = 4U;
    constexpr std::uint32_t opacity_mask = 5U;

    std::vector<std::byte> batch;
    append_create(batch, visual, 39U);
    append_create(batch, content, 43U);
    append_create(batch, target, 47U);
    append_create(batch, brush, 75U);
    append_create(batch, opacity_mask, 75U);
    append_command(batch, command::visual_create, visual);
    append_command(batch, command::visual_set_alpha, visual, 0.5);
    append_command(
        batch, command::visual_set_alpha_mask, visual, opacity_mask);
    append_command(batch, command::visual_set_content, visual, content);
    append_command(
        batch,
        command::solid_color_brush,
        brush,
        1.0,
        progpu_native_color{0.2F, 0.7F, 1.0F, 1.0F},
        0U,
        0U,
        0U,
        0U);
    append_command(
        batch,
        command::solid_color_brush,
        opacity_mask,
        0.5,
        progpu_native_color{1.0F, 1.0F, 1.0F, 0.5F},
        0U,
        0U,
        0U,
        0U);
    std::vector<std::byte> nested;
    append_command(
        nested,
        command::draw_rectangle,
        4.0,
        6.0,
        40.0,
        32.0,
        brush,
        0U);
    append_render_data(batch, content, nested);
    append_command(
        batch,
        command::generic_target_create,
        target,
        std::uint64_t{0U},
        std::uint64_t{0U},
        64U,
        64U,
        0U);
    append_command(batch, command::target_set_root, target, visual);

    channel state;
    PROGPU_REQUIRE(state.apply(batch) == status::success);
    std::vector<std::byte> stream;
    progpu::native::mil::scene_metrics metrics{};
    PROGPU_REQUIRE(
        state.build_scene(target, 9011U, 1U, stream, &metrics) ==
        status::success);
    auto contains_opacity = [&](float opacity) {
        const auto header = read_value<progpu_native_scene_header>(stream, 0U);
        for (std::uint32_t index = 0U;
             index < header.resource_count;
             ++index) {
            const auto resource = read_value<progpu_native_scene_resource>(
                stream,
                header.resource_offset +
                    index * sizeof(progpu_native_scene_resource));
            if (resource.kind == PROGPU_NATIVE_SCENE_RESOURCE_STATE &&
                read_value<progpu_native_scene_state>(
                    stream, resource.payload_offset).opacity == opacity) {
                return true;
            }
        }
        return false;
    };
    PROGPU_REQUIRE(contains_opacity(0.125F));

    std::vector<std::byte> update_mask;
    append_command(
        update_mask,
        command::solid_color_brush,
        opacity_mask,
        0.25,
        progpu_native_color{1.0F, 1.0F, 1.0F, 0.5F},
        0U,
        0U,
        0U,
        0U);
    PROGPU_REQUIRE(state.apply(update_mask) == status::success);
    PROGPU_REQUIRE(
        state.build_scene(target, 9011U, 2U, stream, &metrics) ==
        status::success);
    PROGPU_REQUIRE(contains_opacity(0.0625F));

    std::vector<std::byte> delete_referenced_mask;
    append_command(
        delete_referenced_mask,
        command::channel_delete_resource,
        opacity_mask,
        75U);
    PROGPU_REQUIRE(
        state.apply(delete_referenced_mask) == status::invalid_graph);

    std::vector<std::byte> clear_mask;
    append_command(
        clear_mask, command::visual_set_alpha_mask, visual, 0U);
    PROGPU_REQUIRE(state.apply(clear_mask) == status::success);
    PROGPU_REQUIRE(state.apply(delete_referenced_mask) == status::success);

    std::vector<std::byte> invalid_mask;
    append_command(
        invalid_mask, command::visual_set_alpha_mask, visual, target);
    PROGPU_REQUIRE(state.apply(invalid_mask) == status::invalid_handle);
    return true;
}

bool matrix_transform_scopes_compile_to_semantic_state() {
    constexpr std::uint32_t visual = 1U;
    constexpr std::uint32_t content = 2U;
    constexpr std::uint32_t target = 3U;
    constexpr std::uint32_t brush = 4U;
    constexpr std::uint32_t visual_transform = 5U;
    constexpr std::uint32_t scope_transform = 6U;
    constexpr std::uint32_t clip_geometry = 7U;
    constexpr std::uint32_t nested_clip_geometry = 8U;
    std::vector<std::byte> batch;
    append_create(batch, visual, 39U);
    append_create(batch, content, 43U);
    append_create(batch, target, 47U);
    append_create(batch, brush, 75U);
    append_create(batch, visual_transform, 66U);
    append_create(batch, scope_transform, 66U);
    append_create(batch, clip_geometry, 69U);
    append_create(batch, nested_clip_geometry, 69U);
    append_command(batch, command::visual_create, visual);
    append_command(batch, command::visual_set_offset, visual, 10.0, 20.0);
    append_command(
        batch,
        command::matrix_transform,
        visual_transform,
        2.0,
        0.0,
        0.0,
        2.0,
        1.0,
        2.0,
        0U);
    append_command(
        batch,
        command::matrix_transform,
        scope_transform,
        1.0,
        0.0,
        0.0,
        1.0,
        3.0,
        4.0,
        0U);
    append_command(
        batch,
        command::visual_set_transform,
        visual,
        visual_transform);
    append_command(
        batch,
        command::rectangle_geometry,
        clip_geometry,
        0.0,
        0.0,
        0.0,
        0.0,
        5.0,
        6.0,
        0U,
        0U,
        0U,
        0U);
    append_command(
        batch,
        command::rectangle_geometry,
        nested_clip_geometry,
        0.0,
        0.0,
        4.0,
        5.0,
        5.0,
        5.0,
        0U,
        0U,
        0U,
        0U);
    append_command(batch, command::visual_set_content, visual, content);
    append_command(
        batch,
        command::solid_color_brush,
        brush,
        1.0,
        progpu_native_color{1.0F, 0.5F, 0.25F, 1.0F},
        0U,
        0U,
        0U,
        0U);
    std::vector<std::byte> nested;
    append_command(
        nested,
        command::push_transform,
        scope_transform,
        0U);
    append_command(nested, command::push_clip, clip_geometry, 0U);
    append_command(nested, command::push_clip, nested_clip_geometry, 0U);
    append_command(nested, command::push_opacity, 0.5);
    append_command(
        nested,
        command::draw_rectangle,
        1.0,
        2.0,
        3.0,
        4.0,
        brush,
        0U);
    append_command(nested, command::pop);
    append_command(nested, command::pop);
    append_command(nested, command::pop);
    append_command(nested, command::pop);
    append_render_data(batch, content, nested);
    append_command(
        batch,
        command::generic_target_create,
        target,
        std::uint64_t{0U},
        std::uint64_t{0U},
        64U,
        64U,
        0U);
    append_command(batch, command::target_set_root, target, visual);

    channel state;
    PROGPU_REQUIRE(state.apply(batch) == status::success);
    std::vector<std::byte> stream;
    PROGPU_REQUIRE(
        state.build_scene(target, 7001U, 3U, stream) == status::success);
    const auto header = read_value<progpu_native_scene_header>(stream, 0U);
    PROGPU_REQUIRE(header.command_count == 11U);
    PROGPU_REQUIRE(header.resource_count == 7U);

    bool found_visual_state = false;
    bool found_transform_state = false;
    bool found_clip_state = false;
    bool found_nested_clip_state = false;
    bool found_opacity_state = false;
    for (std::uint32_t index = 0U; index < header.resource_count; ++index) {
        const auto record = read_value<progpu_native_scene_resource>(
            stream,
            header.resource_offset +
                index * sizeof(progpu_native_scene_resource));
        if (record.kind == PROGPU_NATIVE_SCENE_RESOURCE_STATE) {
            const auto scene_state = read_value<progpu_native_scene_state>(
                stream,
                record.payload_offset);
            if (scene_state.transform.m11 == 2.0F &&
                scene_state.transform.m22 == 2.0F &&
                scene_state.transform.m31 == 11.0F &&
                scene_state.transform.m32 == 22.0F &&
                scene_state.opacity == 1.0F) {
                found_visual_state = true;
            }
            if (scene_state.transform.m11 == 2.0F &&
                scene_state.transform.m22 == 2.0F &&
                scene_state.transform.m31 == 17.0F &&
                scene_state.transform.m32 == 30.0F) {
                const bool has_clip = (scene_state.flags &
                    PROGPU_NATIVE_SCENE_STATE_CLIP_RECT) != 0U;
                if (scene_state.opacity == 1.0F && !has_clip) {
                    found_transform_state = true;
                } else if (scene_state.opacity == 1.0F && has_clip &&
                    scene_state.clip_rect.x == 17.0F) {
                    PROGPU_REQUIRE(scene_state.clip_rect.y == 30.0F);
                    PROGPU_REQUIRE(scene_state.clip_rect.width == 10.0F);
                    PROGPU_REQUIRE(scene_state.clip_rect.height == 12.0F);
                    found_clip_state = true;
                } else if (scene_state.opacity == 1.0F && has_clip) {
                    PROGPU_REQUIRE(scene_state.clip_rect.x == 25.0F);
                    PROGPU_REQUIRE(scene_state.clip_rect.y == 40.0F);
                    PROGPU_REQUIRE(scene_state.clip_rect.width == 2.0F);
                    PROGPU_REQUIRE(scene_state.clip_rect.height == 2.0F);
                    found_nested_clip_state = true;
                } else if (scene_state.opacity == 0.5F && has_clip) {
                    PROGPU_REQUIRE(scene_state.clip_rect.x == 25.0F);
                    PROGPU_REQUIRE(scene_state.clip_rect.y == 40.0F);
                    PROGPU_REQUIRE(scene_state.clip_rect.width == 2.0F);
                    PROGPU_REQUIRE(scene_state.clip_rect.height == 2.0F);
                    found_opacity_state = true;
                }
            }
        }
    }
    bool found_transformed_bounds = false;
    for (std::uint32_t index = 0U; index < header.command_count; ++index) {
        const auto record = read_value<progpu_native_scene_command>(
            stream,
            header.command_offset +
                index * sizeof(progpu_native_scene_command));
        if (record.kind == PROGPU_NATIVE_SCENE_COMMAND_DRAW_ANALYTIC) {
            PROGPU_REQUIRE(record.bounds_x == 19.0F);
            PROGPU_REQUIRE(record.bounds_y == 34.0F);
            PROGPU_REQUIRE(record.bounds_width == 6.0F);
            PROGPU_REQUIRE(record.bounds_height == 8.0F);
            found_transformed_bounds = true;
        }
    }
    PROGPU_REQUIRE(found_visual_state);
    PROGPU_REQUIRE(found_transform_state);
    PROGPU_REQUIRE(found_clip_state);
    PROGPU_REQUIRE(found_nested_clip_state);
    PROGPU_REQUIRE(found_opacity_state);
    PROGPU_REQUIRE(found_transformed_bounds);

    const auto transform_generation =
        state.resource_generation(scope_transform);
    std::vector<std::byte> animated_update;
    append_command(
        animated_update,
        command::matrix_transform,
        scope_transform,
        1.0,
        0.0,
        0.0,
        1.0,
        99.0,
        99.0,
        1U);
    PROGPU_REQUIRE(
        state.apply(animated_update) == status::invalid_handle);
    PROGPU_REQUIRE(
        state.resource_generation(scope_transform) == transform_generation);

    std::vector<std::byte> wrong_type;
    append_command(
        wrong_type,
        command::visual_set_transform,
        visual,
        brush);
    PROGPU_REQUIRE(state.apply(wrong_type) == status::invalid_handle);

    std::vector<std::byte> rounded_clip_update;
    append_command(
        rounded_clip_update,
        command::rectangle_geometry,
        clip_geometry,
        1.0,
        1.0,
        0.0,
        0.0,
        5.0,
        6.0,
        0U,
        0U,
        0U,
        0U);
    PROGPU_REQUIRE(state.apply(rounded_clip_update) == status::success);
    PROGPU_REQUIRE(
        state.build_scene(target, 7001U, 4U, stream) == status::success);
    const auto rounded_header =
        read_value<progpu_native_scene_header>(stream, 0U);
    bool found_vector_clip = false;
    bool found_masked_state = false;
    for (std::uint32_t index = 0U;
         index < rounded_header.resource_count;
         ++index) {
        const auto resource = read_value<progpu_native_scene_resource>(
            stream,
            rounded_header.resource_offset +
                index * sizeof(progpu_native_scene_resource));
        if (resource.kind == PROGPU_NATIVE_SCENE_RESOURCE_LAYER_MASK) {
            const auto mask =
                read_value<progpu_native_scene_layer_vector_mask>(
                    stream,
                    resource.payload_offset);
            if (mask.kind ==
                PROGPU_NATIVE_SCENE_LAYER_MASK_VECTOR_CLIP_CHAIN) {
                PROGPU_REQUIRE(mask.path_count == 1U);
                PROGPU_REQUIRE(mask.segment_count == 8U);
                PROGPU_REQUIRE(mask.boolean_node_count == 0U);
                found_vector_clip = true;
            }
        } else if (resource.kind == PROGPU_NATIVE_SCENE_RESOURCE_STATE) {
            const auto scene_state = read_value<progpu_native_scene_state>(
                stream,
                resource.payload_offset);
            if ((scene_state.flags & PROGPU_NATIVE_SCENE_STATE_MASK) != 0U) {
                PROGPU_REQUIRE(
                    scene_state.mask_resource_index <
                    rounded_header.resource_count);
                found_masked_state = true;
            }
        }
    }
    PROGPU_REQUIRE(found_vector_clip);
    PROGPU_REQUIRE(found_masked_state);
    return true;
}

bool static_transform_resources_compose_and_retain_dependencies() {
    constexpr std::uint32_t visual = 1U;
    constexpr std::uint32_t content = 2U;
    constexpr std::uint32_t target = 3U;
    constexpr std::uint32_t brush = 4U;
    constexpr std::uint32_t translate = 5U;
    constexpr std::uint32_t scale = 6U;
    constexpr std::uint32_t skew = 7U;
    constexpr std::uint32_t rotate = 8U;
    constexpr std::uint32_t group = 9U;
    constexpr std::uint32_t nested_group = 10U;
    constexpr std::uint32_t double_animation = 11U;
    constexpr std::uint32_t matrix_animation = 12U;
    constexpr std::uint32_t matrix = 13U;
    std::vector<std::byte> batch;
    append_create(batch, visual, 39U);
    append_create(batch, content, 43U);
    append_create(batch, target, 47U);
    append_create(batch, brush, 75U);
    append_create(batch, translate, 62U);
    append_create(batch, scale, 63U);
    append_create(batch, skew, 64U);
    append_create(batch, rotate, 65U);
    append_create(batch, group, 61U);
    append_create(batch, nested_group, 61U);
    append_create(batch, double_animation, 49U);
    append_create(batch, matrix_animation, 54U);
    append_create(batch, matrix, 66U);
    append_command(batch, command::visual_create, visual);
    append_command(
        batch,
        command::double_resource,
        double_animation,
        3.0);
    append_command(
        batch,
        command::matrix_resource,
        matrix_animation,
        1.0,
        0.0,
        0.0,
        1.0,
        0.0,
        0.0);
    append_command(
        batch,
        command::matrix_transform,
        matrix,
        1.0,
        0.0,
        0.0,
        1.0,
        99.0,
        99.0,
        matrix_animation);
    append_command(
        batch,
        command::translate_transform,
        translate,
        99.0,
        4.0,
        double_animation,
        0U);
    append_command(
        batch,
        command::scale_transform,
        scale,
        2.0,
        3.0,
        0.0,
        0.0,
        0U,
        0U,
        0U,
        0U);
    append_command(
        batch,
        command::skew_transform,
        skew,
        45.0,
        0.0,
        0.0,
        0.0,
        0U,
        0U,
        0U,
        0U);
    append_command(
        batch,
        command::rotate_transform,
        rotate,
        90.0,
        0.0,
        0.0,
        0U,
        0U,
        0U);
    const std::array transform_children{
        matrix, translate, scale, skew, rotate};
    append_transform_group(batch, group, transform_children);
    const std::array nested_children{group};
    append_transform_group(batch, nested_group, nested_children);
    append_command(batch, command::visual_set_transform, visual, nested_group);
    append_command(batch, command::visual_set_content, visual, content);
    append_command(
        batch,
        command::solid_color_brush,
        brush,
        1.0,
        progpu_native_color{0.2F, 0.4F, 0.6F, 1.0F},
        0U,
        0U,
        0U,
        0U);
    std::vector<std::byte> nested;
    append_command(
        nested,
        command::draw_rectangle,
        0.0,
        0.0,
        2.0,
        3.0,
        brush,
        0U);
    append_render_data(batch, content, nested);
    append_command(
        batch,
        command::generic_target_create,
        target,
        std::uint64_t{0U},
        std::uint64_t{0U},
        64U,
        64U,
        0U);
    append_command(batch, command::target_set_root, target, visual);

    channel state;
    PROGPU_REQUIRE(state.apply(batch) == status::success);
    std::vector<std::byte> stream;
    PROGPU_REQUIRE(
        state.build_scene(target, 7002U, 1U, stream) == status::success);
    const auto has_transform = [&stream](
        float offset_x,
        float offset_y) {
        const auto header = read_value<progpu_native_scene_header>(stream, 0U);
        for (std::uint32_t index = 0U; index < header.resource_count; ++index) {
            const auto resource = read_value<progpu_native_scene_resource>(
                stream,
                header.resource_offset +
                    index * sizeof(progpu_native_scene_resource));
            if (resource.kind != PROGPU_NATIVE_SCENE_RESOURCE_STATE) {
                continue;
            }
            const auto scene_state = read_value<progpu_native_scene_state>(
                stream,
                resource.payload_offset);
            if (std::abs(scene_state.transform.m11) < 0.0001F &&
                std::abs(scene_state.transform.m12 - 2.0F) < 0.0001F &&
                std::abs(scene_state.transform.m21 + 3.0F) < 0.0001F &&
                std::abs(scene_state.transform.m22 - 3.0F) < 0.0001F &&
                std::abs(scene_state.transform.m31 - offset_x) < 0.0001F &&
                std::abs(scene_state.transform.m32 - offset_y) < 0.0001F) {
                return true;
            }
        }
        return false;
    };
    PROGPU_REQUIRE(has_transform(-12.0F, 18.0F));

    std::vector<std::byte> child_update;
    append_command(
        child_update,
        command::double_resource,
        double_animation,
        5.0);
    PROGPU_REQUIRE(state.apply(child_update) == status::success);
    PROGPU_REQUIRE(
        state.build_scene(target, 7002U, 2U, stream) == status::success);
    PROGPU_REQUIRE(has_transform(-12.0F, 22.0F));

    std::vector<std::byte> matrix_update;
    append_command(
        matrix_update,
        command::matrix_resource,
        matrix_animation,
        1.0,
        0.0,
        0.0,
        1.0,
        1.0,
        0.0);
    PROGPU_REQUIRE(state.apply(matrix_update) == status::success);
    PROGPU_REQUIRE(
        state.build_scene(target, 7002U, 3U, stream) == status::success);
    PROGPU_REQUIRE(has_transform(-12.0F, 24.0F));

    const auto rotate_generation = state.resource_generation(rotate);
    std::vector<std::byte> animated_update;
    append_command(
        animated_update,
        command::rotate_transform,
        rotate,
        180.0,
        0.0,
        0.0,
        brush,
        0U,
        0U);
    PROGPU_REQUIRE(
        state.apply(animated_update) == status::invalid_handle);
    PROGPU_REQUIRE(state.resource_generation(rotate) == rotate_generation);

    std::vector<std::byte> cycle;
    const std::array cycle_children{nested_group};
    append_transform_group(cycle, group, cycle_children);
    PROGPU_REQUIRE(state.apply(cycle) == status::invalid_graph);

    std::vector<std::byte> delete_dependency;
    append_command(
        delete_dependency,
        command::channel_delete_resource,
        translate,
        62U);
    PROGPU_REQUIRE(state.apply(delete_dependency) == status::invalid_graph);

    std::vector<std::byte> delete_animation;
    append_command(
        delete_animation,
        command::channel_delete_resource,
        double_animation,
        49U);
    PROGPU_REQUIRE(state.apply(delete_animation) == status::invalid_graph);
    return true;
}

bool solid_pen_line_compiles_to_geometry_scene() {
    constexpr std::uint32_t visual = 1U;
    constexpr std::uint32_t content = 2U;
    constexpr std::uint32_t target = 3U;
    constexpr std::uint32_t brush = 4U;
    constexpr std::uint32_t pen = 5U;
    constexpr std::uint32_t transform = 6U;
    constexpr std::uint32_t dash = 7U;
    std::vector<std::byte> batch;
    append_create(batch, visual, 39U);
    append_create(batch, content, 43U);
    append_create(batch, target, 47U);
    append_create(batch, brush, 75U);
    append_create(batch, pen, 85U);
    append_create(batch, transform, 66U);
    append_command(batch, command::visual_create, visual);
    append_command(batch, command::visual_set_offset, visual, 10.0, 20.0);
    append_command(
        batch,
        command::matrix_transform,
        transform,
        2.0,
        0.0,
        0.0,
        2.0,
        0.0,
        0.0,
        0U);
    append_command(
        batch,
        command::visual_set_transform,
        visual,
        transform);
    append_command(batch, command::visual_set_content, visual, content);
    append_command(
        batch,
        command::solid_color_brush,
        brush,
        1.0,
        progpu_native_color{0.25F, 0.5F, 0.75F, 1.0F},
        0U,
        0U,
        0U,
        0U);
    append_command(
        batch,
        command::pen,
        pen,
        2.0,
        10.0,
        brush,
        0U,
        1U,
        2U,
        1U,
        0U,
        0U);
    std::vector<std::byte> nested;
    append_command(
        nested,
        command::draw_line,
        1.0,
        2.0,
        5.0,
        8.0,
        pen,
        0U);
    append_render_data(batch, content, nested);
    append_command(
        batch,
        command::generic_target_create,
        target,
        std::uint64_t{0U},
        std::uint64_t{0U},
        64U,
        64U,
        0U);
    append_command(batch, command::target_set_root, target, visual);

    channel state;
    PROGPU_REQUIRE(state.apply(batch) == status::success);
    std::vector<std::byte> stream;
    progpu::native::mil::scene_metrics metrics{};
    PROGPU_REQUIRE(
        state.build_scene(target, 7002U, 1U, stream, &metrics) ==
        status::success);
    PROGPU_REQUIRE(metrics.line_count == 1U);
    PROGPU_REQUIRE(metrics.brush_count == 1U);
    const auto header = read_value<progpu_native_scene_header>(stream, 0U);
    PROGPU_REQUIRE(header.command_count == 3U);
    PROGPU_REQUIRE(header.resource_count == 3U);

    bool found_line = false;
    for (std::uint32_t index = 0U; index < header.resource_count; ++index) {
        const auto record = read_value<progpu_native_scene_resource>(
            stream,
            header.resource_offset +
                index * sizeof(progpu_native_scene_resource));
        if (record.kind != PROGPU_NATIVE_SCENE_RESOURCE_GEOMETRY_BATCH) {
            continue;
        }
        const auto primitive =
            read_value<progpu_native_geometry_primitive>(
                stream,
                record.payload_offset);
        PROGPU_REQUIRE(primitive.kind == PROGPU_NATIVE_GEOMETRY_LINE);
        PROGPU_REQUIRE(primitive.p0.x == 1.0F);
        PROGPU_REQUIRE(primitive.p0.y == 2.0F);
        PROGPU_REQUIRE(primitive.p1.x == 5.0F);
        PROGPU_REQUIRE(primitive.p1.y == 8.0F);
        PROGPU_REQUIRE(primitive.stroke_thickness == 2.0F);
        PROGPU_REQUIRE(
            (primitive.flags & PROGPU_NATIVE_PRIMITIVE_START_CAP_MASK) ==
            (1U << PROGPU_NATIVE_PRIMITIVE_START_CAP_SHIFT));
        PROGPU_REQUIRE(
            (primitive.flags & PROGPU_NATIVE_PRIMITIVE_END_CAP_MASK) ==
            (2U << PROGPU_NATIVE_PRIMITIVE_END_CAP_SHIFT));
        found_line = true;
    }
    PROGPU_REQUIRE(found_line);

    bool found_bounds = false;
    for (std::uint32_t index = 0U; index < header.command_count; ++index) {
        const auto record = read_value<progpu_native_scene_command>(
            stream,
            header.command_offset +
                index * sizeof(progpu_native_scene_command));
        if (record.kind == PROGPU_NATIVE_SCENE_COMMAND_DRAW_GEOMETRY) {
            PROGPU_REQUIRE(std::abs(record.bounds_x - 9.226499F) < 0.0001F);
            PROGPU_REQUIRE(std::abs(record.bounds_y - 21.2265F) < 0.0001F);
            PROGPU_REQUIRE(
                std::abs(record.bounds_width - 12.773501F) < 0.0001F);
            PROGPU_REQUIRE(
                std::abs(record.bounds_height - 16.7735F) < 0.0001F);
            found_bounds = true;
        }
    }
    PROGPU_REQUIRE(found_bounds);

    std::vector<std::byte> degenerate_line_batch;
    std::vector<std::byte> degenerate_line;
    append_command(
        degenerate_line,
        command::draw_line,
        3.0,
        4.0,
        3.0,
        4.0,
        pen,
        0U);
    append_render_data(
        degenerate_line_batch,
        content,
        degenerate_line);
    PROGPU_REQUIRE(state.apply(degenerate_line_batch) == status::success);
    PROGPU_REQUIRE(
        state.build_scene(target, 7002U, 2U, stream, &metrics) ==
        status::success);
    PROGPU_REQUIRE(metrics.line_count == 1U);
    const auto degenerate_line_header =
        read_value<progpu_native_scene_header>(stream, 0U);
    std::uint32_t degenerate_start_cap_count = 0U;
    std::uint32_t degenerate_end_cap_count = 0U;
    for (std::uint32_t index = 0U;
         index < degenerate_line_header.resource_count;
         ++index) {
        const auto record = read_value<progpu_native_scene_resource>(
            stream,
            degenerate_line_header.resource_offset +
                index * sizeof(progpu_native_scene_resource));
        if (record.kind != PROGPU_NATIVE_SCENE_RESOURCE_GEOMETRY_BATCH) {
            continue;
        }
        PROGPU_REQUIRE(
            record.payload_size ==
            2U * sizeof(progpu_native_geometry_primitive));
        for (std::size_t primitive_index = 0U;
             primitive_index < 2U;
             ++primitive_index) {
            const auto primitive =
                read_value<progpu_native_geometry_primitive>(
                    stream,
                    record.payload_offset +
                        primitive_index *
                            sizeof(progpu_native_geometry_primitive));
            PROGPU_REQUIRE(
                primitive.kind == PROGPU_NATIVE_GEOMETRY_PATH_CAP);
            PROGPU_REQUIRE(primitive.p0.x == 3.0F);
            PROGPU_REQUIRE(primitive.p0.y == 4.0F);
            PROGPU_REQUIRE(primitive.p1.x == 1.0F);
            PROGPU_REQUIRE(primitive.p1.y == 0.0F);
            const std::uint32_t cap =
                (primitive.flags & PROGPU_NATIVE_PRIMITIVE_START_CAP_MASK) >>
                PROGPU_NATIVE_PRIMITIVE_START_CAP_SHIFT;
            if (primitive.p2.x == 1.0F) {
                PROGPU_REQUIRE(cap == PROGPU_NATIVE_STROKE_CAP_SQUARE);
                ++degenerate_start_cap_count;
            } else {
                PROGPU_REQUIRE(primitive.p2.x == 0.0F);
                PROGPU_REQUIRE(cap == PROGPU_NATIVE_STROKE_CAP_ROUND);
                ++degenerate_end_cap_count;
            }
        }
    }
    PROGPU_REQUIRE(degenerate_start_cap_count == 1U);
    PROGPU_REQUIRE(degenerate_end_cap_count == 1U);
    bool found_degenerate_line_bounds = false;
    for (std::uint32_t index = 0U;
         index < degenerate_line_header.command_count;
         ++index) {
        const auto record = read_value<progpu_native_scene_command>(
            stream,
            degenerate_line_header.command_offset +
                index * sizeof(progpu_native_scene_command));
        if (record.kind != PROGPU_NATIVE_SCENE_COMMAND_DRAW_GEOMETRY) {
            continue;
        }
        PROGPU_REQUIRE(record.bounds_x == 14.0F);
        PROGPU_REQUIRE(record.bounds_y == 26.0F);
        PROGPU_REQUIRE(record.bounds_width == 4.0F);
        PROGPU_REQUIRE(record.bounds_height == 4.0F);
        found_degenerate_line_bounds = true;
    }
    PROGPU_REQUIRE(found_degenerate_line_bounds);
    std::vector<std::byte> restore_line_batch;
    append_render_data(restore_line_batch, content, nested);
    PROGPU_REQUIRE(state.apply(restore_line_batch) == status::success);

    const auto pen_generation = state.resource_generation(pen);
    std::vector<std::byte> animated_pen;
    append_command(
        animated_pen,
        command::pen,
        pen,
        3.0,
        10.0,
        brush,
        1U,
        0U,
        0U,
        1U,
        0U,
        0U);
    PROGPU_REQUIRE(state.apply(animated_pen) == status::unsupported_command);
    PROGPU_REQUIRE(state.resource_generation(pen) == pen_generation);

    std::vector<std::byte> dashed_pen;
    append_create(dashed_pen, dash, 84U);
    const std::array dash_intervals{2.0, 1.0};
    append_dash_style(dashed_pen, dash, 0.5, 0U, dash_intervals);
    append_command(
        dashed_pen,
        command::pen,
        pen,
        2.0,
        10.0,
        brush,
        0U,
        0U,
        0U,
        1U,
        0U,
        dash);
    PROGPU_REQUIRE(state.apply(dashed_pen) == status::success);
    PROGPU_REQUIRE(state.resource_generation(pen) == pen_generation + 1U);
    PROGPU_REQUIRE(
        state.build_scene(target, 7002U, 2U, stream, &metrics) ==
        status::success);
    const auto dashed_header =
        read_value<progpu_native_scene_header>(stream, 0U);
    bool found_dashed_line = false;
    for (std::uint32_t index = 0U;
        index < dashed_header.resource_count;
        ++index) {
        const auto record = read_value<progpu_native_scene_resource>(
            stream,
            dashed_header.resource_offset +
                index * sizeof(progpu_native_scene_resource));
        if (record.kind != PROGPU_NATIVE_SCENE_RESOURCE_STROKE_BATCH) {
            continue;
        }
        const auto stroke = read_value<progpu_native_scene_stroke>(
            stream,
            record.payload_offset);
        PROGPU_REQUIRE(stroke.kind == PROGPU_NATIVE_SCENE_STROKE_POLYLINE);
        PROGPU_REQUIRE(stroke.point_count == 2U);
        PROGPU_REQUIRE(stroke.dash_interval_count == 2U);
        PROGPU_REQUIRE(stroke.dash_offset == 0.5);
        PROGPU_REQUIRE(stroke.dash_cap == 1U);
        PROGPU_REQUIRE(
            read_value<double>(stream, record.auxiliary_offset + 16U) ==
            2.0);
        PROGPU_REQUIRE(
            read_value<double>(stream, record.auxiliary_offset + 24U) ==
            1.0);
        found_dashed_line = true;
    }
    PROGPU_REQUIRE(found_dashed_line);

    const auto dash_generation = state.resource_generation(dash);
    std::vector<std::byte> animated_dash;
    append_dash_style(animated_dash, dash, 0.0, 99U, dash_intervals);
    PROGPU_REQUIRE(
        state.apply(animated_dash) == status::unsupported_command);
    PROGPU_REQUIRE(state.resource_generation(dash) == dash_generation);

    std::vector<std::byte> invalid_dash;
    const std::array invalid_intervals{2.0, -1.0};
    append_dash_style(invalid_dash, dash, 0.0, 0U, invalid_intervals);
    PROGPU_REQUIRE(state.apply(invalid_dash) == status::malformed_batch);
    PROGPU_REQUIRE(state.resource_generation(dash) == dash_generation);

    std::vector<std::byte> delete_referenced_dash;
    append_command(
        delete_referenced_dash,
        command::channel_delete_resource,
        dash,
        84U);
    PROGPU_REQUIRE(
        state.apply(delete_referenced_dash) == status::invalid_graph);

    std::vector<std::byte> rectangle_batch;
    std::vector<std::byte> dashed_rectangle;
    append_command(
        dashed_rectangle,
        command::draw_rectangle,
        1.0,
        2.0,
        4.0,
        6.0,
        brush,
        pen);
    append_render_data(rectangle_batch, content, dashed_rectangle);
    PROGPU_REQUIRE(state.apply(rectangle_batch) == status::success);
    PROGPU_REQUIRE(
        state.build_scene(target, 7002U, 3U, stream, &metrics) ==
        status::success);
    PROGPU_REQUIRE(metrics.rectangle_count == 1U);
    PROGPU_REQUIRE(metrics.line_count == 0U);
    const auto rectangle_header =
        read_value<progpu_native_scene_header>(stream, 0U);
    bool found_closed_rectangle = false;
    for (std::uint32_t index = 0U;
        index < rectangle_header.resource_count;
        ++index) {
        const auto record = read_value<progpu_native_scene_resource>(
            stream,
            rectangle_header.resource_offset +
                index * sizeof(progpu_native_scene_resource));
        if (record.kind != PROGPU_NATIVE_SCENE_RESOURCE_STROKE_BATCH) {
            continue;
        }
        const auto stroke = read_value<progpu_native_scene_stroke>(
            stream,
            record.payload_offset);
        PROGPU_REQUIRE(
            (stroke.flags & PROGPU_NATIVE_POLYLINE_FLAG_CLOSED) != 0U);
        PROGPU_REQUIRE(stroke.point_count == 4U);
        PROGPU_REQUIRE(stroke.dash_interval_count == 2U);
        found_closed_rectangle = true;
    }
    PROGPU_REQUIRE(found_closed_rectangle);
    bool found_rectangle_stroke_bounds = false;
    for (std::uint32_t index = 0U;
        index < rectangle_header.command_count;
        ++index) {
        const auto record = read_value<progpu_native_scene_command>(
            stream,
            rectangle_header.command_offset +
                index * sizeof(progpu_native_scene_command));
        if (record.kind != PROGPU_NATIVE_SCENE_COMMAND_DRAW_STROKE_BATCH) {
            continue;
        }
        PROGPU_REQUIRE(record.bounds_x == 10.0F);
        PROGPU_REQUIRE(record.bounds_y == 22.0F);
        PROGPU_REQUIRE(record.bounds_width == 12.0F);
        PROGPU_REQUIRE(record.bounds_height == 16.0F);
        found_rectangle_stroke_bounds = true;
    }
    PROGPU_REQUIRE(found_rectangle_stroke_bounds);

    constexpr std::uint32_t solid_pen = 8U;
    std::vector<std::byte> solid_pen_batch;
    append_create(solid_pen_batch, solid_pen, 85U);
    append_command(
        solid_pen_batch,
        command::pen,
        solid_pen,
        2.0,
        10.0,
        brush,
        0U,
        0U,
        0U,
        1U,
        0U,
        0U);
    PROGPU_REQUIRE(state.apply(solid_pen_batch) == status::success);
    std::vector<std::byte> ellipse_batch;
    std::vector<std::byte> ellipse;
    append_command(
        ellipse,
        command::draw_ellipse,
        3.0,
        4.0,
        2.0,
        1.0,
        brush,
        solid_pen);
    append_render_data(ellipse_batch, content, ellipse);
    PROGPU_REQUIRE(state.apply(ellipse_batch) == status::success);
    PROGPU_REQUIRE(
        state.build_scene(target, 7002U, 4U, stream, &metrics) ==
        status::success);
    PROGPU_REQUIRE(metrics.ellipse_count == 1U);
    const auto ellipse_header =
        read_value<progpu_native_scene_header>(stream, 0U);
    bool found_ellipse_arc = false;
    for (std::uint32_t index = 0U;
        index < ellipse_header.resource_count;
        ++index) {
        const auto record = read_value<progpu_native_scene_resource>(
            stream,
            ellipse_header.resource_offset +
                index * sizeof(progpu_native_scene_resource));
        if (record.kind != PROGPU_NATIVE_SCENE_RESOURCE_GEOMETRY_BATCH) {
            continue;
        }
        const auto primitive =
            read_value<progpu_native_geometry_primitive>(
                stream,
                record.payload_offset);
        if (primitive.kind != PROGPU_NATIVE_GEOMETRY_ARC) {
            continue;
        }
        PROGPU_REQUIRE(primitive.p0.x == 3.0F);
        PROGPU_REQUIRE(primitive.p0.y == 4.0F);
        PROGPU_REQUIRE(primitive.p1.x == 2.0F);
        PROGPU_REQUIRE(primitive.p2.y == 1.0F);
        PROGPU_REQUIRE(primitive.stroke_thickness == 2.0F);
        found_ellipse_arc = true;
    }
    PROGPU_REQUIRE(found_ellipse_arc);
    bool found_ellipse_stroke_bounds = false;
    for (std::uint32_t index = 0U;
        index < ellipse_header.command_count;
        ++index) {
        const auto record = read_value<progpu_native_scene_command>(
            stream,
            ellipse_header.command_offset +
                index * sizeof(progpu_native_scene_command));
        if (record.kind != PROGPU_NATIVE_SCENE_COMMAND_DRAW_GEOMETRY) {
            continue;
        }
        PROGPU_REQUIRE(record.bounds_x == 10.0F);
        PROGPU_REQUIRE(record.bounds_y == 24.0F);
        PROGPU_REQUIRE(record.bounds_width == 12.0F);
        PROGPU_REQUIRE(record.bounds_height == 8.0F);
        found_ellipse_stroke_bounds = true;
    }
    PROGPU_REQUIRE(found_ellipse_stroke_bounds);

    std::vector<std::byte> degenerate_ellipse_batch;
    std::vector<std::byte> degenerate_ellipses;
    append_command(
        degenerate_ellipses,
        command::draw_ellipse,
        3.0,
        4.0,
        2.0,
        0.0,
        brush,
        solid_pen);
    append_command(
        degenerate_ellipses,
        command::draw_ellipse,
        8.0,
        4.0,
        0.0,
        2.0,
        brush,
        solid_pen);
    append_command(
        degenerate_ellipses,
        command::draw_ellipse,
        12.0,
        4.0,
        0.0,
        0.0,
        brush,
        solid_pen);
    append_render_data(
        degenerate_ellipse_batch,
        content,
        degenerate_ellipses);
    PROGPU_REQUIRE(
        state.apply(degenerate_ellipse_batch) == status::success);
    PROGPU_REQUIRE(
        state.build_scene(target, 7002U, 5U, stream, &metrics) ==
        status::success);
    PROGPU_REQUIRE(metrics.ellipse_count == 3U);
    const auto degenerate_ellipse_header =
        read_value<progpu_native_scene_header>(stream, 0U);
    std::uint32_t degenerate_ellipse_line_count = 0U;
    std::uint32_t degenerate_ellipse_cap_count = 0U;
    std::uint32_t degenerate_ellipse_draw_count = 0U;
    for (std::uint32_t index = 0U;
         index < degenerate_ellipse_header.resource_count;
         ++index) {
        const auto record = read_value<progpu_native_scene_resource>(
            stream,
            degenerate_ellipse_header.resource_offset +
                index * sizeof(progpu_native_scene_resource));
        PROGPU_REQUIRE(
            record.kind != PROGPU_NATIVE_SCENE_RESOURCE_ANALYTIC_BATCH);
        if (record.kind != PROGPU_NATIVE_SCENE_RESOURCE_GEOMETRY_BATCH) {
            continue;
        }
        const std::size_t primitive_count =
            record.payload_size / sizeof(progpu_native_geometry_primitive);
        for (std::size_t primitive_index = 0U;
             primitive_index < primitive_count;
             ++primitive_index) {
            const auto primitive =
                read_value<progpu_native_geometry_primitive>(
                    stream,
                    record.payload_offset +
                        primitive_index *
                            sizeof(progpu_native_geometry_primitive));
            if (primitive.kind == PROGPU_NATIVE_GEOMETRY_LINE) {
                PROGPU_REQUIRE(
                    (primitive.flags &
                        PROGPU_NATIVE_PRIMITIVE_START_CAP_MASK) ==
                    (PROGPU_NATIVE_STROKE_CAP_ROUND <<
                        PROGPU_NATIVE_PRIMITIVE_START_CAP_SHIFT));
                PROGPU_REQUIRE(
                    (primitive.flags &
                        PROGPU_NATIVE_PRIMITIVE_END_CAP_MASK) ==
                    (PROGPU_NATIVE_STROKE_CAP_ROUND <<
                        PROGPU_NATIVE_PRIMITIVE_END_CAP_SHIFT));
                ++degenerate_ellipse_line_count;
            } else if (primitive.kind ==
                PROGPU_NATIVE_GEOMETRY_PATH_CAP) {
                ++degenerate_ellipse_cap_count;
            }
        }
    }
    for (std::uint32_t index = 0U;
         index < degenerate_ellipse_header.command_count;
         ++index) {
        const auto record = read_value<progpu_native_scene_command>(
            stream,
            degenerate_ellipse_header.command_offset +
                index * sizeof(progpu_native_scene_command));
        if (record.kind != PROGPU_NATIVE_SCENE_COMMAND_DRAW_GEOMETRY) {
            continue;
        }
        if (degenerate_ellipse_draw_count == 0U) {
            PROGPU_REQUIRE(record.bounds_x == 10.0F);
            PROGPU_REQUIRE(record.bounds_y == 26.0F);
            PROGPU_REQUIRE(record.bounds_width == 12.0F);
            PROGPU_REQUIRE(record.bounds_height == 4.0F);
        } else if (degenerate_ellipse_draw_count == 1U) {
            PROGPU_REQUIRE(record.bounds_x == 24.0F);
            PROGPU_REQUIRE(record.bounds_y == 22.0F);
            PROGPU_REQUIRE(record.bounds_width == 4.0F);
            PROGPU_REQUIRE(record.bounds_height == 12.0F);
        } else {
            PROGPU_REQUIRE(record.bounds_x == 32.0F);
            PROGPU_REQUIRE(record.bounds_y == 26.0F);
            PROGPU_REQUIRE(record.bounds_width == 4.0F);
            PROGPU_REQUIRE(record.bounds_height == 4.0F);
        }
        ++degenerate_ellipse_draw_count;
    }
    PROGPU_REQUIRE(degenerate_ellipse_line_count == 2U);
    PROGPU_REQUIRE(degenerate_ellipse_cap_count == 2U);
    PROGPU_REQUIRE(degenerate_ellipse_draw_count == 3U);

    constexpr std::uint32_t round_pen = 12U;
    constexpr std::uint32_t bevel_pen = 13U;
    std::vector<std::byte> degenerate_rectangle_pen_batch;
    append_create(degenerate_rectangle_pen_batch, round_pen, 85U);
    append_create(degenerate_rectangle_pen_batch, bevel_pen, 85U);
    append_command(
        degenerate_rectangle_pen_batch,
        command::pen,
        round_pen,
        2.0,
        10.0,
        brush,
        0U,
        0U,
        0U,
        0U,
        2U,
        0U);
    append_command(
        degenerate_rectangle_pen_batch,
        command::pen,
        bevel_pen,
        2.0,
        10.0,
        brush,
        0U,
        0U,
        0U,
        0U,
        1U,
        0U);
    PROGPU_REQUIRE(
        state.apply(degenerate_rectangle_pen_batch) == status::success);
    std::vector<std::byte> degenerate_rectangle_batch;
    std::vector<std::byte> degenerate_rectangles;
    append_command(
        degenerate_rectangles,
        command::draw_rectangle,
        3.0,
        4.0,
        0.0,
        4.0,
        brush,
        solid_pen);
    append_command(
        degenerate_rectangles,
        command::draw_rectangle,
        8.0,
        4.0,
        0.0,
        4.0,
        0U,
        round_pen);
    append_command(
        degenerate_rectangles,
        command::draw_rounded_rectangle,
        12.0,
        4.0,
        0.0,
        4.0,
        2.0,
        2.0,
        brush,
        bevel_pen);
    append_command(
        degenerate_rectangles,
        command::draw_rectangle,
        16.0,
        4.0,
        0.0,
        0.0,
        0U,
        bevel_pen);
    append_render_data(
        degenerate_rectangle_batch,
        content,
        degenerate_rectangles);
    PROGPU_REQUIRE(
        state.apply(degenerate_rectangle_batch) == status::success);
    PROGPU_REQUIRE(
        state.build_scene(target, 7002U, 50U, stream, &metrics) ==
        status::success);
    PROGPU_REQUIRE(metrics.rectangle_count == 3U);
    PROGPU_REQUIRE(metrics.rounded_rectangle_count == 1U);
    const auto degenerate_rectangle_header =
        read_value<progpu_native_scene_header>(stream, 0U);
    std::uint32_t degenerate_rectangle_path_count = 0U;
    for (std::uint32_t index = 0U;
         index < degenerate_rectangle_header.resource_count;
         ++index) {
        const auto record = read_value<progpu_native_scene_resource>(
            stream,
            degenerate_rectangle_header.resource_offset +
                index * sizeof(progpu_native_scene_resource));
        PROGPU_REQUIRE(
            record.kind != PROGPU_NATIVE_SCENE_RESOURCE_ANALYTIC_BATCH);
        if (record.kind != PROGPU_NATIVE_SCENE_RESOURCE_PATH_BATCH) {
            continue;
        }
        const auto path = read_value<progpu_native_scene_path_fill>(
            stream,
            record.payload_offset);
        PROGPU_REQUIRE(path.fill_rule == PROGPU_NATIVE_FILL_RULE_EVEN_ODD);
        PROGPU_REQUIRE(path.transform.m11 == 1.0F);
        PROGPU_REQUIRE(path.transform.m22 == 1.0F);
        std::uint32_t arc_count = 0U;
#if defined(__clang__)
#pragma clang loop vectorize(disable) interleave(disable)
#endif
        for (std::size_t segment_index = 0U;
             segment_index < path.segment_count;
             ++segment_index) {
            const auto segment = read_value<progpu_native_path_segment>(
                stream,
                record.auxiliary_offset +
                    segment_index * sizeof(progpu_native_path_segment));
            arc_count += segment.kind == PROGPU_NATIVE_PATH_SEGMENT_ARC
                ? 1U
                : 0U;
            if (degenerate_rectangle_path_count == 2U &&
                segment.kind == PROGPU_NATIVE_PATH_SEGMENT_ARC) {
                PROGPU_REQUIRE(segment.p3.x == 1.0F);
                PROGPU_REQUIRE(segment.p3.y == 3.0F);
            }
        }
        if (degenerate_rectangle_path_count == 0U) {
            PROGPU_REQUIRE(path.segment_count == 4U);
            PROGPU_REQUIRE(arc_count == 0U);
        } else if (degenerate_rectangle_path_count == 1U ||
            degenerate_rectangle_path_count == 2U) {
            PROGPU_REQUIRE(path.segment_count == 8U);
            PROGPU_REQUIRE(arc_count == 4U);
        } else {
            PROGPU_REQUIRE(path.segment_count == 8U);
            PROGPU_REQUIRE(arc_count == 0U);
        }
        ++degenerate_rectangle_path_count;
    }
    PROGPU_REQUIRE(degenerate_rectangle_path_count == 4U);
    const std::array expected_degenerate_rectangle_bounds{
        progpu_native_image_rect{14.0F, 26.0F, 4.0F, 12.0F},
        progpu_native_image_rect{24.0F, 26.0F, 4.0F, 12.0F},
        progpu_native_image_rect{32.0F, 26.0F, 4.0F, 12.0F},
        progpu_native_image_rect{40.0F, 26.0F, 4.0F, 4.0F}};
    std::uint32_t degenerate_rectangle_draw_count = 0U;
    for (std::uint32_t index = 0U;
         index < degenerate_rectangle_header.command_count;
         ++index) {
        const auto record = read_value<progpu_native_scene_command>(
            stream,
            degenerate_rectangle_header.command_offset +
                index * sizeof(progpu_native_scene_command));
        if (record.kind != PROGPU_NATIVE_SCENE_COMMAND_DRAW_PATH) {
            continue;
        }
        const auto& expected = expected_degenerate_rectangle_bounds[
            degenerate_rectangle_draw_count];
        PROGPU_REQUIRE(record.bounds_x == expected.x);
        PROGPU_REQUIRE(record.bounds_y == expected.y);
        PROGPU_REQUIRE(record.bounds_width == expected.width);
        PROGPU_REQUIRE(record.bounds_height == expected.height);
        ++degenerate_rectangle_draw_count;
    }
    PROGPU_REQUIRE(degenerate_rectangle_draw_count == 4U);

    std::vector<std::byte> dashed_degenerate_rectangle_batch;
    std::vector<std::byte> dashed_degenerate_rectangle;
    append_command(
        dashed_degenerate_rectangle,
        command::draw_rectangle,
        3.0,
        4.0,
        0.0,
        4.0,
        0U,
        pen);
    append_render_data(
        dashed_degenerate_rectangle_batch,
        content,
        dashed_degenerate_rectangle);
    PROGPU_REQUIRE(
        state.apply(dashed_degenerate_rectangle_batch) == status::success);
    PROGPU_REQUIRE(
        state.build_scene(target, 7002U, 51U, stream, &metrics) ==
        status::unsupported_command);

    std::vector<std::byte> rounded_batch;
    std::vector<std::byte> rounded;
    append_command(
        rounded,
        command::draw_rounded_rectangle,
        2.0,
        3.0,
        8.0,
        6.0,
        2.0,
        2.0,
        0U,
        solid_pen);
    append_render_data(rounded_batch, content, rounded);
    PROGPU_REQUIRE(state.apply(rounded_batch) == status::success);
    PROGPU_REQUIRE(
        state.build_scene(target, 7002U, 6U, stream, &metrics) ==
        status::success);
    PROGPU_REQUIRE(metrics.rounded_rectangle_count == 1U);
    const auto rounded_header =
        read_value<progpu_native_scene_header>(stream, 0U);
    bool found_rounded_stroke = false;
    for (std::uint32_t index = 0U;
        index < rounded_header.resource_count;
        ++index) {
        const auto record = read_value<progpu_native_scene_resource>(
            stream,
            rounded_header.resource_offset +
                index * sizeof(progpu_native_scene_resource));
        if (record.kind != PROGPU_NATIVE_SCENE_RESOURCE_ANALYTIC_BATCH) {
            continue;
        }
        const auto primitive =
            read_value<progpu_native_analytic_primitive>(
                stream,
                record.payload_offset);
        if (primitive.kind !=
            PROGPU_NATIVE_PRIMITIVE_ROUNDED_RECTANGLE) {
            continue;
        }
        PROGPU_REQUIRE(primitive.x == 2.0F);
        PROGPU_REQUIRE(primitive.y == 3.0F);
        PROGPU_REQUIRE(primitive.width == 8.0F);
        PROGPU_REQUIRE(primitive.height == 6.0F);
        PROGPU_REQUIRE(primitive.corner_radius == 2.0F);
        PROGPU_REQUIRE(primitive.stroke_thickness == 2.0F);
        found_rounded_stroke = true;
    }
    PROGPU_REQUIRE(found_rounded_stroke);
    bool found_rounded_stroke_bounds = false;
    for (std::uint32_t index = 0U;
        index < rounded_header.command_count;
        ++index) {
        const auto record = read_value<progpu_native_scene_command>(
            stream,
            rounded_header.command_offset +
                index * sizeof(progpu_native_scene_command));
        if (record.kind != PROGPU_NATIVE_SCENE_COMMAND_DRAW_ANALYTIC) {
            continue;
        }
        PROGPU_REQUIRE(record.bounds_x == 12.0F);
        PROGPU_REQUIRE(record.bounds_y == 24.0F);
        PROGPU_REQUIRE(record.bounds_width == 20.0F);
        PROGPU_REQUIRE(record.bounds_height == 16.0F);
        found_rounded_stroke_bounds = true;
    }
    PROGPU_REQUIRE(found_rounded_stroke_bounds);

    std::vector<std::byte> nonuniform_rounded_batch;
    std::vector<std::byte> nonuniform_rounded;
    append_command(
        nonuniform_rounded,
        command::draw_rounded_rectangle,
        2.0,
        3.0,
        8.0,
        6.0,
        2.0,
        1.0,
        brush,
        solid_pen);
    append_render_data(
        nonuniform_rounded_batch,
        content,
        nonuniform_rounded);
    PROGPU_REQUIRE(
        state.apply(nonuniform_rounded_batch) == status::success);
    PROGPU_REQUIRE(
        state.build_scene(target, 7002U, 61U, stream, &metrics) ==
        status::success);
    PROGPU_REQUIRE(metrics.rounded_rectangle_count == 1U);
    const auto nonuniform_rounded_header =
        read_value<progpu_native_scene_header>(stream, 0U);
    std::uint32_t nonuniform_fill_arc_count = 0U;
    std::uint32_t nonuniform_stroke_arc_count = 0U;
    std::uint32_t nonuniform_round_join_count = 0U;
    for (std::uint32_t index = 0U;
         index < nonuniform_rounded_header.resource_count;
         ++index) {
        const auto record = read_value<progpu_native_scene_resource>(
            stream,
            nonuniform_rounded_header.resource_offset +
                index * sizeof(progpu_native_scene_resource));
        PROGPU_REQUIRE(
            record.kind != PROGPU_NATIVE_SCENE_RESOURCE_ANALYTIC_BATCH);
        if (record.kind == PROGPU_NATIVE_SCENE_RESOURCE_PATH_BATCH) {
            const auto path = read_value<progpu_native_scene_path_fill>(
                stream,
                record.payload_offset);
            PROGPU_REQUIRE(path.segment_count == 8U);
            for (std::size_t segment_index = 0U;
                 segment_index < path.segment_count;
                 ++segment_index) {
                const auto segment = read_value<progpu_native_path_segment>(
                    stream,
                    record.auxiliary_offset + segment_index *
                        sizeof(progpu_native_path_segment));
                if (segment.kind != PROGPU_NATIVE_PATH_SEGMENT_ARC) {
                    continue;
                }
                PROGPU_REQUIRE(segment.p3.x == 2.0F);
                PROGPU_REQUIRE(segment.p3.y == 1.0F);
                ++nonuniform_fill_arc_count;
            }
        } else if (record.kind ==
            PROGPU_NATIVE_SCENE_RESOURCE_GEOMETRY_BATCH) {
            const std::size_t primitive_count = record.payload_size /
                sizeof(progpu_native_geometry_primitive);
            for (std::size_t primitive_index = 0U;
                 primitive_index < primitive_count;
                 ++primitive_index) {
                const auto primitive =
                    read_value<progpu_native_geometry_primitive>(
                        stream,
                        record.payload_offset + primitive_index *
                            sizeof(progpu_native_geometry_primitive));
                if (primitive.kind == PROGPU_NATIVE_GEOMETRY_ARC) {
                    PROGPU_REQUIRE(primitive.p1.x == 2.0F);
                    PROGPU_REQUIRE(primitive.p2.y == 1.0F);
                    ++nonuniform_stroke_arc_count;
                } else if (primitive.kind ==
                    PROGPU_NATIVE_GEOMETRY_PATH_JOIN) {
                    PROGPU_REQUIRE(
                        (primitive.flags &
                            PROGPU_NATIVE_PRIMITIVE_START_CAP_MASK) ==
                        (PROGPU_NATIVE_STROKE_JOIN_ROUND <<
                            PROGPU_NATIVE_PRIMITIVE_START_CAP_SHIFT));
                    ++nonuniform_round_join_count;
                }
            }
        }
    }
    PROGPU_REQUIRE(nonuniform_fill_arc_count == 4U);
    PROGPU_REQUIRE(nonuniform_stroke_arc_count == 4U);
    PROGPU_REQUIRE(nonuniform_round_join_count == 8U);

    std::vector<std::byte> zero_axis_rounded_batch;
    std::vector<std::byte> zero_axis_rounded;
    append_command(
        zero_axis_rounded,
        command::draw_rounded_rectangle,
        2.0,
        3.0,
        8.0,
        6.0,
        0.0,
        3.0,
        brush,
        solid_pen);
    append_render_data(
        zero_axis_rounded_batch,
        content,
        zero_axis_rounded);
    PROGPU_REQUIRE(
        state.apply(zero_axis_rounded_batch) == status::success);
    PROGPU_REQUIRE(
        state.build_scene(target, 7002U, 63U, stream, &metrics) ==
        status::success);
    PROGPU_REQUIRE(metrics.rounded_rectangle_count == 1U);
    const auto zero_axis_rounded_header =
        read_value<progpu_native_scene_header>(stream, 0U);
    std::uint32_t zero_axis_rectangle_fill_count = 0U;
    std::uint32_t zero_axis_rectangle_stroke_count = 0U;
    for (std::uint32_t index = 0U;
         index < zero_axis_rounded_header.resource_count;
         ++index) {
        const auto record = read_value<progpu_native_scene_resource>(
            stream,
            zero_axis_rounded_header.resource_offset +
                index * sizeof(progpu_native_scene_resource));
        PROGPU_REQUIRE(record.kind != PROGPU_NATIVE_SCENE_RESOURCE_PATH_BATCH);
        if (record.kind == PROGPU_NATIVE_SCENE_RESOURCE_ANALYTIC_BATCH) {
            const auto primitive =
                read_value<progpu_native_analytic_primitive>(
                    stream,
                    record.payload_offset);
            PROGPU_REQUIRE(
                primitive.kind == PROGPU_NATIVE_PRIMITIVE_RECTANGLE);
            ++zero_axis_rectangle_fill_count;
        } else if (record.kind ==
            PROGPU_NATIVE_SCENE_RESOURCE_STROKE_BATCH) {
            const auto stroke = read_value<progpu_native_scene_stroke>(
                stream,
                record.payload_offset);
            PROGPU_REQUIRE(
                stroke.kind == PROGPU_NATIVE_SCENE_STROKE_POLYLINE);
            PROGPU_REQUIRE(
                (stroke.flags & PROGPU_NATIVE_POLYLINE_FLAG_CLOSED) != 0U);
            PROGPU_REQUIRE(stroke.point_count == 4U);
            ++zero_axis_rectangle_stroke_count;
        }
    }
    PROGPU_REQUIRE(zero_axis_rectangle_fill_count == 1U);
    PROGPU_REQUIRE(zero_axis_rectangle_stroke_count == 1U);

    std::vector<std::byte> dashed_ellipse_batch;
    std::vector<std::byte> dashed_ellipse;
    append_command(
        dashed_ellipse,
        command::draw_ellipse,
        3.0,
        4.0,
        2.0,
        1.0,
        brush,
        pen);
    append_render_data(dashed_ellipse_batch, content, dashed_ellipse);
    PROGPU_REQUIRE(state.apply(dashed_ellipse_batch) == status::success);
    PROGPU_REQUIRE(
        state.build_scene(target, 7002U, 7U, stream, &metrics) ==
        status::unsupported_command);

    std::vector<std::byte> dashed_rounded_batch;
    std::vector<std::byte> dashed_rounded;
    append_command(
        dashed_rounded,
        command::draw_rounded_rectangle,
        2.0,
        3.0,
        8.0,
        6.0,
        2.0,
        2.0,
        0U,
        pen);
    append_render_data(dashed_rounded_batch, content, dashed_rounded);
    PROGPU_REQUIRE(state.apply(dashed_rounded_batch) == status::success);
    PROGPU_REQUIRE(
        state.build_scene(target, 7002U, 8U, stream, &metrics) ==
        status::unsupported_command);

    constexpr std::uint32_t line_geometry = 9U;
    std::vector<std::byte> geometry_batch;
    append_create(geometry_batch, line_geometry, 68U);
    append_command(
        geometry_batch,
        command::line_geometry,
        line_geometry,
        1.0,
        2.0,
        5.0,
        8.0,
        transform,
        0U,
        0U);
    std::vector<std::byte> geometry_draw;
    append_command(
        geometry_draw,
        command::draw_geometry,
        brush,
        solid_pen,
        line_geometry,
        0U);
    append_render_data(geometry_batch, content, geometry_draw);
    PROGPU_REQUIRE(state.apply(geometry_batch) == status::success);
    PROGPU_REQUIRE(
        state.build_scene(target, 7002U, 9U, stream, &metrics) ==
        status::success);
    PROGPU_REQUIRE(metrics.line_count == 1U);
    const auto geometry_header =
        read_value<progpu_native_scene_header>(stream, 0U);
    bool found_transformed_line_geometry = false;
    for (std::uint32_t index = 0U;
        index < geometry_header.resource_count;
        ++index) {
        const auto record = read_value<progpu_native_scene_resource>(
            stream,
            geometry_header.resource_offset +
                index * sizeof(progpu_native_scene_resource));
        if (record.kind != PROGPU_NATIVE_SCENE_RESOURCE_GEOMETRY_BATCH) {
            continue;
        }
        const auto primitive = read_value<progpu_native_geometry_primitive>(
            stream,
            record.payload_offset);
        PROGPU_REQUIRE(primitive.kind == PROGPU_NATIVE_GEOMETRY_LINE);
        PROGPU_REQUIRE(primitive.transform.m11 == 2.0F);
        PROGPU_REQUIRE(primitive.transform.m22 == 2.0F);
        found_transformed_line_geometry = true;
    }
    PROGPU_REQUIRE(found_transformed_line_geometry);

    const auto geometry_generation =
        state.resource_generation(line_geometry);
    std::vector<std::byte> animated_geometry;
    append_command(
        animated_geometry,
        command::line_geometry,
        line_geometry,
        1.0,
        2.0,
        5.0,
        8.0,
        transform,
        1U,
        0U);
    PROGPU_REQUIRE(
        state.apply(animated_geometry) == status::unsupported_command);
    PROGPU_REQUIRE(
        state.resource_generation(line_geometry) == geometry_generation);

    constexpr std::uint32_t rectangle_geometry = 10U;
    constexpr std::uint32_t ellipse_geometry = 11U;
    std::vector<std::byte> primitive_geometry_batch;
    append_create(primitive_geometry_batch, rectangle_geometry, 69U);
    append_create(primitive_geometry_batch, ellipse_geometry, 70U);
    append_command(
        primitive_geometry_batch,
        command::rectangle_geometry,
        rectangle_geometry,
        2.0,
        2.0,
        3.0,
        4.0,
        12.0,
        8.0,
        transform,
        0U,
        0U,
        0U);
    append_command(
        primitive_geometry_batch,
        command::ellipse_geometry,
        ellipse_geometry,
        4.0,
        3.0,
        9.0,
        8.0,
        transform,
        0U,
        0U,
        0U);
    std::vector<std::byte> primitive_geometry_draw;
    append_command(
        primitive_geometry_draw,
        command::draw_geometry,
        brush,
        solid_pen,
        rectangle_geometry,
        0U);
    append_command(
        primitive_geometry_draw,
        command::draw_geometry,
        brush,
        solid_pen,
        ellipse_geometry,
        0U);
    append_render_data(
        primitive_geometry_batch,
        content,
        primitive_geometry_draw);
    PROGPU_REQUIRE(
        state.apply(primitive_geometry_batch) == status::success);
    PROGPU_REQUIRE(
        state.build_scene(target, 7002U, 10U, stream, &metrics) ==
        status::success);
    PROGPU_REQUIRE(metrics.rounded_rectangle_count == 1U);
    PROGPU_REQUIRE(metrics.ellipse_count == 1U);
    const auto primitive_geometry_header =
        read_value<progpu_native_scene_header>(stream, 0U);
    std::uint32_t transformed_analytic_count = 0U;
    for (std::uint32_t index = 0U;
        index < primitive_geometry_header.resource_count;
        ++index) {
        const auto record = read_value<progpu_native_scene_resource>(
            stream,
            primitive_geometry_header.resource_offset +
                index * sizeof(progpu_native_scene_resource));
        if (record.kind != PROGPU_NATIVE_SCENE_RESOURCE_ANALYTIC_BATCH) {
            continue;
        }
        const auto primitive = read_value<progpu_native_analytic_primitive>(
            stream,
            record.payload_offset);
        if (primitive.kind != PROGPU_NATIVE_PRIMITIVE_ROUNDED_RECTANGLE &&
            primitive.kind != PROGPU_NATIVE_PRIMITIVE_ELLIPSE) {
            continue;
        }
        PROGPU_REQUIRE(primitive.transform.m11 == 2.0F);
        PROGPU_REQUIRE(primitive.transform.m22 == 2.0F);
        ++transformed_analytic_count;
    }
    PROGPU_REQUIRE(transformed_analytic_count >= 3U);

    std::vector<std::byte> nonuniform_rectangle_geometry_update;
    append_command(
        nonuniform_rectangle_geometry_update,
        command::rectangle_geometry,
        rectangle_geometry,
        3.0,
        1.0,
        4.0,
        4.0,
        12.0,
        8.0,
        transform,
        0U,
        0U,
        0U);
    std::vector<std::byte> nonuniform_rectangle_geometry_draw;
    append_command(
        nonuniform_rectangle_geometry_draw,
        command::draw_geometry,
        brush,
        solid_pen,
        rectangle_geometry,
        0U);
    append_render_data(
        nonuniform_rectangle_geometry_update,
        content,
        nonuniform_rectangle_geometry_draw);
    PROGPU_REQUIRE(
        state.apply(nonuniform_rectangle_geometry_update) ==
        status::success);
    PROGPU_REQUIRE(
        state.build_scene(target, 7002U, 62U, stream, &metrics) ==
        status::success);
    PROGPU_REQUIRE(metrics.rounded_rectangle_count == 1U);
    const auto nonuniform_rectangle_geometry_header =
        read_value<progpu_native_scene_header>(stream, 0U);
    std::uint32_t retained_nonuniform_path_count = 0U;
    std::uint32_t retained_nonuniform_arc_count = 0U;
    for (std::uint32_t index = 0U;
         index < nonuniform_rectangle_geometry_header.resource_count;
         ++index) {
        const auto record = read_value<progpu_native_scene_resource>(
            stream,
            nonuniform_rectangle_geometry_header.resource_offset +
                index * sizeof(progpu_native_scene_resource));
        if (record.kind == PROGPU_NATIVE_SCENE_RESOURCE_PATH_BATCH) {
            const auto path = read_value<progpu_native_scene_path_fill>(
                stream,
                record.payload_offset);
            PROGPU_REQUIRE(path.segment_count == 8U);
            PROGPU_REQUIRE(path.transform.m11 == 2.0F);
            PROGPU_REQUIRE(path.transform.m22 == 2.0F);
            ++retained_nonuniform_path_count;
        } else if (record.kind ==
            PROGPU_NATIVE_SCENE_RESOURCE_GEOMETRY_BATCH) {
            const std::size_t primitive_count = record.payload_size /
                sizeof(progpu_native_geometry_primitive);
            for (std::size_t primitive_index = 0U;
                 primitive_index < primitive_count;
                 ++primitive_index) {
                const auto primitive =
                    read_value<progpu_native_geometry_primitive>(
                        stream,
                        record.payload_offset + primitive_index *
                            sizeof(progpu_native_geometry_primitive));
                if (primitive.kind != PROGPU_NATIVE_GEOMETRY_ARC) {
                    continue;
                }
                PROGPU_REQUIRE(primitive.p1.x == 3.0F);
                PROGPU_REQUIRE(primitive.p2.y == 1.0F);
                PROGPU_REQUIRE(primitive.transform.m11 == 2.0F);
                PROGPU_REQUIRE(primitive.transform.m22 == 2.0F);
                ++retained_nonuniform_arc_count;
            }
        }
    }
    PROGPU_REQUIRE(retained_nonuniform_path_count == 1U);
    PROGPU_REQUIRE(retained_nonuniform_arc_count == 4U);

    std::vector<std::byte> zero_axis_rectangle_geometry_update;
    append_command(
        zero_axis_rectangle_geometry_update,
        command::rectangle_geometry,
        rectangle_geometry,
        0.0,
        3.0,
        4.0,
        4.0,
        12.0,
        8.0,
        transform,
        0U,
        0U,
        0U);
    std::vector<std::byte> zero_axis_rectangle_geometry_draw;
    append_command(
        zero_axis_rectangle_geometry_draw,
        command::draw_geometry,
        brush,
        solid_pen,
        rectangle_geometry,
        0U);
    append_render_data(
        zero_axis_rectangle_geometry_update,
        content,
        zero_axis_rectangle_geometry_draw);
    PROGPU_REQUIRE(
        state.apply(zero_axis_rectangle_geometry_update) == status::success);
    PROGPU_REQUIRE(
        state.build_scene(target, 7002U, 64U, stream, &metrics) ==
        status::success);
    PROGPU_REQUIRE(metrics.rounded_rectangle_count == 1U);
    const auto zero_axis_rectangle_geometry_header =
        read_value<progpu_native_scene_header>(stream, 0U);
    std::uint32_t retained_zero_axis_fill_count = 0U;
    std::uint32_t retained_zero_axis_stroke_count = 0U;
    for (std::uint32_t index = 0U;
         index < zero_axis_rectangle_geometry_header.resource_count;
         ++index) {
        const auto record = read_value<progpu_native_scene_resource>(
            stream,
            zero_axis_rectangle_geometry_header.resource_offset +
                index * sizeof(progpu_native_scene_resource));
        if (record.kind == PROGPU_NATIVE_SCENE_RESOURCE_ANALYTIC_BATCH) {
            const auto primitive =
                read_value<progpu_native_analytic_primitive>(
                    stream,
                    record.payload_offset);
            PROGPU_REQUIRE(
                primitive.kind == PROGPU_NATIVE_PRIMITIVE_RECTANGLE);
            PROGPU_REQUIRE(primitive.transform.m11 == 2.0F);
            PROGPU_REQUIRE(primitive.transform.m22 == 2.0F);
            ++retained_zero_axis_fill_count;
        } else if (record.kind ==
            PROGPU_NATIVE_SCENE_RESOURCE_STROKE_BATCH) {
            const auto stroke = read_value<progpu_native_scene_stroke>(
                stream,
                record.payload_offset);
            PROGPU_REQUIRE(stroke.transform.m11 == 2.0F);
            PROGPU_REQUIRE(stroke.transform.m22 == 2.0F);
            ++retained_zero_axis_stroke_count;
        }
    }
    PROGPU_REQUIRE(retained_zero_axis_fill_count == 1U);
    PROGPU_REQUIRE(retained_zero_axis_stroke_count == 1U);

    std::vector<std::byte> degenerate_ellipse_geometry_update;
    append_command(
        degenerate_ellipse_geometry_update,
        command::ellipse_geometry,
        ellipse_geometry,
        0.0,
        3.0,
        9.0,
        8.0,
        transform,
        0U,
        0U,
        0U);
    std::vector<std::byte> degenerate_ellipse_geometry_draw;
    append_command(
        degenerate_ellipse_geometry_draw,
        command::draw_geometry,
        brush,
        solid_pen,
        ellipse_geometry,
        0U);
    append_render_data(
        degenerate_ellipse_geometry_update,
        content,
        degenerate_ellipse_geometry_draw);
    PROGPU_REQUIRE(
        state.apply(degenerate_ellipse_geometry_update) == status::success);
    PROGPU_REQUIRE(
        state.build_scene(target, 7002U, 11U, stream, &metrics) ==
        status::success);
    PROGPU_REQUIRE(metrics.ellipse_count == 1U);
    const auto degenerate_ellipse_geometry_header =
        read_value<progpu_native_scene_header>(stream, 0U);
    std::uint32_t retained_degenerate_ellipse_line_count = 0U;
    for (std::uint32_t index = 0U;
         index < degenerate_ellipse_geometry_header.resource_count;
         ++index) {
        const auto record = read_value<progpu_native_scene_resource>(
            stream,
            degenerate_ellipse_geometry_header.resource_offset +
                index * sizeof(progpu_native_scene_resource));
        PROGPU_REQUIRE(
            record.kind != PROGPU_NATIVE_SCENE_RESOURCE_ANALYTIC_BATCH);
        if (record.kind != PROGPU_NATIVE_SCENE_RESOURCE_GEOMETRY_BATCH) {
            continue;
        }
        const auto primitive =
            read_value<progpu_native_geometry_primitive>(
                stream,
                record.payload_offset);
        PROGPU_REQUIRE(primitive.kind == PROGPU_NATIVE_GEOMETRY_LINE);
        PROGPU_REQUIRE(primitive.p0.x == 9.0F);
        PROGPU_REQUIRE(primitive.p0.y == 5.0F);
        PROGPU_REQUIRE(primitive.p1.x == 9.0F);
        PROGPU_REQUIRE(primitive.p1.y == 11.0F);
        PROGPU_REQUIRE(primitive.transform.m11 == 2.0F);
        PROGPU_REQUIRE(primitive.transform.m22 == 2.0F);
        ++retained_degenerate_ellipse_line_count;
    }
    PROGPU_REQUIRE(retained_degenerate_ellipse_line_count == 1U);

    std::vector<std::byte> degenerate_rectangle_geometry_update;
    append_command(
        degenerate_rectangle_geometry_update,
        command::rectangle_geometry,
        rectangle_geometry,
        0.0,
        0.0,
        3.0,
        4.0,
        0.0,
        4.0,
        transform,
        0U,
        0U,
        0U);
    std::vector<std::byte> degenerate_rectangle_geometry_draw;
    append_command(
        degenerate_rectangle_geometry_draw,
        command::draw_geometry,
        0U,
        bevel_pen,
        rectangle_geometry,
        0U);
    append_render_data(
        degenerate_rectangle_geometry_update,
        content,
        degenerate_rectangle_geometry_draw);
    PROGPU_REQUIRE(
        state.apply(degenerate_rectangle_geometry_update) ==
        status::success);
    PROGPU_REQUIRE(
        state.build_scene(target, 7002U, 12U, stream, &metrics) ==
        status::success);
    PROGPU_REQUIRE(metrics.rectangle_count == 1U);
    const auto degenerate_rectangle_geometry_header =
        read_value<progpu_native_scene_header>(stream, 0U);
    std::uint32_t retained_degenerate_rectangle_path_count = 0U;
    for (std::uint32_t index = 0U;
         index < degenerate_rectangle_geometry_header.resource_count;
         ++index) {
        const auto record = read_value<progpu_native_scene_resource>(
            stream,
            degenerate_rectangle_geometry_header.resource_offset +
                index * sizeof(progpu_native_scene_resource));
        if (record.kind != PROGPU_NATIVE_SCENE_RESOURCE_PATH_BATCH) {
            continue;
        }
        const auto path = read_value<progpu_native_scene_path_fill>(
            stream,
            record.payload_offset);
        PROGPU_REQUIRE(path.segment_count == 8U);
        PROGPU_REQUIRE(path.transform.m11 == 2.0F);
        PROGPU_REQUIRE(path.transform.m22 == 2.0F);
        ++retained_degenerate_rectangle_path_count;
    }
    PROGPU_REQUIRE(retained_degenerate_rectangle_path_count == 1U);
    std::uint32_t retained_degenerate_rectangle_draw_count = 0U;
    for (std::uint32_t index = 0U;
         index < degenerate_rectangle_geometry_header.command_count;
         ++index) {
        const auto record = read_value<progpu_native_scene_command>(
            stream,
            degenerate_rectangle_geometry_header.command_offset +
                index * sizeof(progpu_native_scene_command));
        if (record.kind != PROGPU_NATIVE_SCENE_COMMAND_DRAW_PATH) {
            continue;
        }
        PROGPU_REQUIRE(record.bounds_x == 18.0F);
        PROGPU_REQUIRE(record.bounds_y == 32.0F);
        PROGPU_REQUIRE(record.bounds_width == 8.0F);
        PROGPU_REQUIRE(record.bounds_height == 24.0F);
        ++retained_degenerate_rectangle_draw_count;
    }
    PROGPU_REQUIRE(retained_degenerate_rectangle_draw_count == 1U);

    const auto rectangle_generation =
        state.resource_generation(rectangle_geometry);
    std::vector<std::byte> animated_rectangle_geometry;
    append_command(
        animated_rectangle_geometry,
        command::rectangle_geometry,
        rectangle_geometry,
        2.0,
        2.0,
        3.0,
        4.0,
        12.0,
        8.0,
        transform,
        1U,
        0U,
        0U);
    PROGPU_REQUIRE(
        state.apply(animated_rectangle_geometry) ==
        status::unsupported_command);
    PROGPU_REQUIRE(
        state.resource_generation(rectangle_geometry) ==
        rectangle_generation);

    std::vector<std::byte> invalid_cap;
    append_command(
        invalid_cap,
        command::pen,
        pen,
        2.0,
        10.0,
        brush,
        0U,
        4U,
        0U,
        1U,
        0U,
        0U);
    PROGPU_REQUIRE(state.apply(invalid_cap) == status::malformed_batch);
    PROGPU_REQUIRE(state.resource_generation(pen) == pen_generation + 1U);

    std::vector<std::byte> null_pen_batch;
    std::vector<std::byte> null_pen;
    append_command(
        null_pen,
        command::draw_line,
        1.0,
        2.0,
        5.0,
        8.0,
        0U,
        0U);
    append_render_data(null_pen_batch, content, null_pen);
    PROGPU_REQUIRE(state.apply(null_pen_batch) == status::success);
    PROGPU_REQUIRE(
        state.build_scene(target, 7002U, 2U, stream, &metrics) ==
        status::success);
    PROGPU_REQUIRE(metrics.line_count == 0U);

    std::vector<std::byte> missing_pen_batch;
    std::vector<std::byte> missing_pen;
    append_command(
        missing_pen,
        command::draw_line,
        1.0,
        2.0,
        5.0,
        8.0,
        99U,
        0U);
    append_render_data(missing_pen_batch, content, missing_pen);
    PROGPU_REQUIRE(state.apply(missing_pen_batch) == status::success);
    PROGPU_REQUIRE(
        state.build_scene(target, 7002U, 3U, stream, &metrics) ==
        status::invalid_handle);
    return true;
}

bool retained_path_geometry_compiles_to_semantic_scene() {
    constexpr std::uint32_t visual = 1U;
    constexpr std::uint32_t content = 2U;
    constexpr std::uint32_t target = 3U;
    constexpr std::uint32_t brush = 4U;
    constexpr std::uint32_t transform = 5U;
    constexpr std::uint32_t geometry = 6U;

    std::vector<std::byte> figures;
    append_value(figures, 296U);
    append_value(figures, 0x03U);
    append_value(figures, 1.0);
    append_value(figures, 2.0);
    append_value(figures, 21.0);
    append_value(figures, 32.0);
    append_value(figures, 1U);
    append_value(figures, 0U);

    append_value(figures, 0U);
    append_value(figures, 0x0eU);
    append_value(figures, 4U);
    append_value(figures, 248U);
    append_value(figures, 1.0);
    append_value(figures, 2.0);
    append_value(figures, 184U);
    append_value(figures, 0U);

    append_value(figures, 1U);
    append_value(figures, 0U);
    append_value(figures, 0U);
    append_value(figures, 0U);
    append_value(figures, 5.0);
    append_value(figures, 8.0);

    append_value(figures, 3U);
    append_value(figures, 0x20U);
    append_value(figures, 32U);
    append_value(figures, 0U);
    append_value(figures, 7.0);
    append_value(figures, 3.0);
    append_value(figures, 9.0);
    append_value(figures, 10.0);

    append_value(figures, 2U);
    append_value(figures, 0x20U);
    append_value(figures, 48U);
    append_value(figures, 0U);
    append_value(figures, 11.0);
    append_value(figures, 4.0);
    append_value(figures, 13.0);
    append_value(figures, 12.0);
    append_value(figures, 15.0);
    append_value(figures, 6.0);

    append_value(figures, 4U);
    append_value(figures, 0x20U);
    append_value(figures, 64U);
    append_value(figures, 0U);
    append_value(figures, 1.0);
    append_value(figures, 2.0);
    append_value(figures, 8.0);
    append_value(figures, 6.0);
    append_value(figures, 30.0);
    append_value(figures, 1U);
    append_value(figures, 0U);
    PROGPU_REQUIRE(figures.size() == 296U);

    std::vector<std::byte> batch;
    append_create(batch, visual, 39U);
    append_create(batch, content, 43U);
    append_create(batch, target, 47U);
    append_create(batch, brush, 75U);
    append_create(batch, transform, 66U);
    append_create(batch, geometry, 73U);
    append_command(batch, command::visual_create, visual);
    append_command(batch, command::visual_set_content, visual, content);
    append_command(
        batch,
        command::solid_color_brush,
        brush,
        1.0,
        progpu_native_color{0.25F, 0.5F, 0.75F, 1.0F},
        0U,
        0U,
        0U,
        0U);
    append_command(
        batch,
        command::matrix_transform,
        transform,
        2.0,
        0.0,
        0.0,
        2.0,
        0.0,
        0.0,
        0U);
    append_path_geometry(batch, geometry, transform, 1U, figures);
    std::vector<std::byte> nested;
    append_command(
        nested,
        command::draw_geometry,
        brush,
        0U,
        geometry,
        0U);
    append_render_data(batch, content, nested);
    append_command(
        batch,
        command::generic_target_create,
        target,
        std::uint64_t{0U},
        std::uint64_t{0U},
        64U,
        64U,
        0U);
    append_command(batch, command::target_set_root, target, visual);

    channel state;
    PROGPU_REQUIRE(state.apply(batch) == status::success);
    std::vector<std::byte> stream;
    progpu::native::mil::scene_metrics metrics{};
    PROGPU_REQUIRE(
        state.build_scene(target, 7002U, 1U, stream, &metrics) ==
        status::success);
    PROGPU_REQUIRE(metrics.brush_count == 1U);
    const auto header = read_value<progpu_native_scene_header>(stream, 0U);
    bool found_path = false;
    for (std::uint32_t index = 0U; index < header.resource_count; ++index) {
        const auto resource = read_value<progpu_native_scene_resource>(
            stream,
            header.resource_offset +
                index * sizeof(progpu_native_scene_resource));
        if (resource.kind != PROGPU_NATIVE_SCENE_RESOURCE_PATH_BATCH) {
            continue;
        }
        const auto path = read_value<progpu_native_scene_path_fill>(
            stream,
            resource.payload_offset);
        PROGPU_REQUIRE(path.segment_count == 4U);
        PROGPU_REQUIRE(path.fill_rule == PROGPU_NATIVE_FILL_RULE_NON_ZERO);
        PROGPU_REQUIRE(path.transform.m11 == 2.0F);
        PROGPU_REQUIRE(path.transform.m22 == 2.0F);
        const auto line = read_value<progpu_native_path_segment>(
            stream,
            resource.auxiliary_offset);
        const auto quadratic = read_value<progpu_native_path_segment>(
            stream,
            resource.auxiliary_offset + sizeof(line));
        const auto cubic = read_value<progpu_native_path_segment>(
            stream,
            resource.auxiliary_offset + 2U * sizeof(line));
        const auto arc = read_value<progpu_native_path_segment>(
            stream,
            resource.auxiliary_offset + 3U * sizeof(line));
        PROGPU_REQUIRE(line.kind == PROGPU_NATIVE_PATH_SEGMENT_LINE);
        PROGPU_REQUIRE(line.p0.x == 1.0F && line.p0.y == 2.0F);
        PROGPU_REQUIRE(line.p1.x == 5.0F && line.p1.y == 8.0F);
        PROGPU_REQUIRE(
            quadratic.kind == PROGPU_NATIVE_PATH_SEGMENT_QUADRATIC);
        PROGPU_REQUIRE(
            quadratic.p1.x == 7.0F && quadratic.p2.x == 9.0F);
        PROGPU_REQUIRE(cubic.kind == PROGPU_NATIVE_PATH_SEGMENT_CUBIC);
        PROGPU_REQUIRE(cubic.p1.x == 11.0F && cubic.p3.x == 15.0F);
        PROGPU_REQUIRE(arc.kind == PROGPU_NATIVE_PATH_SEGMENT_ARC);
        PROGPU_REQUIRE(arc.p0.x == 15.0F && arc.p0.y == 6.0F);
        PROGPU_REQUIRE(arc.p1.x == 1.0F && arc.p1.y == 2.0F);
        PROGPU_REQUIRE(std::isfinite(arc.p2.x) && std::isfinite(arc.p2.y));
        PROGPU_REQUIRE(arc.p3.x >= 8.0F && arc.p3.y >= 6.0F);
        PROGPU_REQUIRE(std::bit_cast<float>(arc.pad1) > 0.0F);
        found_path = true;
    }
    PROGPU_REQUIRE(found_path);

    auto uncached_bounds_figures = figures;
    const std::uint32_t uncached_path_flags = 0x01U;
    const double uncached_bound = 0.0;
    std::memcpy(
        uncached_bounds_figures.data() + 4U,
        &uncached_path_flags,
        sizeof(uncached_path_flags));
    for (std::size_t bounds_offset = 8U;
        bounds_offset <= 32U;
        bounds_offset += sizeof(double)) {
        std::memcpy(
            uncached_bounds_figures.data() + bounds_offset,
            &uncached_bound,
            sizeof(uncached_bound));
    }
    std::vector<std::byte> uncached_bounds_update;
    append_path_geometry(
        uncached_bounds_update,
        geometry,
        transform,
        1U,
        uncached_bounds_figures);
    PROGPU_REQUIRE(state.apply(uncached_bounds_update) == status::success);
    PROGPU_REQUIRE(
        state.build_scene(target, 7002U, 2U, stream, &metrics) ==
        status::success);
    const auto uncached_header =
        read_value<progpu_native_scene_header>(stream, 0U);
    bool found_computed_bounds = false;
    for (std::uint32_t index = 0U;
        index < uncached_header.resource_count;
        ++index) {
        const auto resource = read_value<progpu_native_scene_resource>(
            stream,
            uncached_header.resource_offset +
                index * sizeof(progpu_native_scene_resource));
        if (resource.kind != PROGPU_NATIVE_SCENE_RESOURCE_PATH_BATCH) {
            continue;
        }
        const auto path = read_value<progpu_native_scene_path_fill>(
            stream,
            resource.payload_offset);
        PROGPU_REQUIRE(path.min_x < path.max_x);
        PROGPU_REQUIRE(path.min_y < path.max_y);
        PROGPU_REQUIRE(path.min_x <= 1.0F);
        PROGPU_REQUIRE(path.max_x >= 15.0F);
        found_computed_bounds = true;
    }
    PROGPU_REQUIRE(found_computed_bounds);

    const auto generation = state.resource_generation(geometry);
    auto malformed_figures = figures;
    const std::uint32_t malformed_figure_size = 183U;
    std::memcpy(
        malformed_figures.data() + 60U,
        &malformed_figure_size,
        sizeof(malformed_figure_size));
    std::vector<std::byte> malformed_update;
    append_path_geometry(
        malformed_update,
        geometry,
        transform,
        1U,
        malformed_figures);
    PROGPU_REQUIRE(
        state.apply(malformed_update) == status::malformed_batch);
    PROGPU_REQUIRE(state.resource_generation(geometry) == generation);

    auto invalid_sweep_figures = figures;
    const std::uint32_t invalid_sweep = 2U;
    std::memcpy(
        invalid_sweep_figures.data() + 288U,
        &invalid_sweep,
        sizeof(invalid_sweep));
    std::vector<std::byte> invalid_sweep_update;
    append_path_geometry(
        invalid_sweep_update,
        geometry,
        transform,
        1U,
        invalid_sweep_figures);
    PROGPU_REQUIRE(
        state.apply(invalid_sweep_update) == status::malformed_batch);
    PROGPU_REQUIRE(state.resource_generation(geometry) == generation);

    auto degenerate_arc_figures = figures;
    const double zero_radius = 0.0;
    std::memcpy(
        degenerate_arc_figures.data() + 264U,
        &zero_radius,
        sizeof(zero_radius));
    std::vector<std::byte> degenerate_arc_update;
    append_path_geometry(
        degenerate_arc_update,
        geometry,
        transform,
        1U,
        degenerate_arc_figures);
    PROGPU_REQUIRE(state.apply(degenerate_arc_update) == status::success);
    PROGPU_REQUIRE(state.resource_generation(geometry) == generation + 1U);
    PROGPU_REQUIRE(
        state.build_scene(target, 7002U, 3U, stream, &metrics) ==
        status::success);
    const auto degenerate_header =
        read_value<progpu_native_scene_header>(stream, 0U);
    bool found_degenerate_path = false;
    for (std::uint32_t index = 0U;
        index < degenerate_header.resource_count;
        ++index) {
        const auto resource = read_value<progpu_native_scene_resource>(
            stream,
            degenerate_header.resource_offset +
                index * sizeof(progpu_native_scene_resource));
        if (resource.kind != PROGPU_NATIVE_SCENE_RESOURCE_PATH_BATCH) {
            continue;
        }
        const auto last_segment = read_value<progpu_native_path_segment>(
            stream,
            resource.auxiliary_offset +
                3U * sizeof(progpu_native_path_segment));
        PROGPU_REQUIRE(last_segment.kind == PROGPU_NATIVE_PATH_SEGMENT_LINE);
        PROGPU_REQUIRE(
            last_segment.p0.x == 15.0F && last_segment.p1.x == 1.0F);
        found_degenerate_path = true;
    }
    PROGPU_REQUIRE(found_degenerate_path);

    std::vector<std::byte> delete_transform;
    append_command(
        delete_transform,
        command::channel_delete_resource,
        transform,
        66U);
    PROGPU_REQUIRE(state.apply(delete_transform) == status::invalid_graph);
    PROGPU_REQUIRE(state.resource_generation(geometry) == generation + 1U);
    return true;
}

bool retained_line_path_stroke_preserves_closure_gaps_and_pen_state() {
    constexpr std::uint32_t primitive_stride =
        sizeof(progpu_native_geometry_primitive);
    constexpr std::uint32_t visual = 1U;
    constexpr std::uint32_t content = 2U;
    constexpr std::uint32_t target = 3U;
    constexpr std::uint32_t brush = 4U;
    constexpr std::uint32_t pen = 5U;
    constexpr std::uint32_t dash = 6U;
    constexpr std::uint32_t transform = 7U;
    constexpr std::uint32_t geometry = 8U;
    constexpr std::uint32_t line_size = 32U;
    constexpr std::uint32_t figure_size = 40U + 3U * line_size;
    constexpr std::uint32_t figures_size = 48U + 2U * figure_size;

    std::vector<std::byte> figures;
    append_value(figures, figures_size);
    append_value(figures, 0x02U);
    append_value(figures, 0.0);
    append_value(figures, 0.0);
    append_value(figures, 32.0);
    append_value(figures, 10.0);
    append_value(figures, 2U);
    append_value(figures, 0U);

    const auto append_figure = [&figures, figure_size](
        std::uint32_t back_size,
        std::uint32_t flags,
        double start_x,
        double start_y,
        const std::array<std::array<double, 2U>, 3U>& endpoints,
        const std::array<std::uint32_t, 3U>& segment_flags) {
        append_value(figures, back_size);
        append_value(figures, flags);
        append_value(figures, 3U);
        append_value(figures, figure_size);
        append_value(figures, start_x);
        append_value(figures, start_y);
        append_value(figures, 40U + 2U * line_size);
        append_value(figures, 0U);
        std::uint32_t previous_size = 0U;
        for (std::size_t index = 0U; index < endpoints.size(); ++index) {
            append_value(figures, 1U);
            append_value(figures, segment_flags[index]);
            append_value(figures, previous_size);
            append_value(figures, 0U);
            append_value(figures, endpoints[index][0]);
            append_value(figures, endpoints[index][1]);
            previous_size = line_size;
        }
    };
    append_figure(
        0U,
        0x04U,
        0.0,
        0.0,
        {{{10.0, 0.0}, {10.0, 10.0}, {0.0, 10.0}}},
        {0U, 0U, 0U});
    append_figure(
        figure_size,
        0x05U,
        20.0,
        0.0,
        {{{24.0, 0.0}, {28.0, 0.0}, {32.0, 0.0}}},
        {0x04U, 0U, 0U});
    PROGPU_REQUIRE(figures.size() == figures_size);

    std::vector<std::byte> batch;
    append_create(batch, visual, 39U);
    append_create(batch, content, 43U);
    append_create(batch, target, 47U);
    append_create(batch, brush, 75U);
    append_create(batch, pen, 85U);
    append_create(batch, dash, 84U);
    append_create(batch, transform, 66U);
    append_create(batch, geometry, 73U);
    append_command(batch, command::visual_create, visual);
    append_command(batch, command::visual_set_content, visual, content);
    append_command(
        batch,
        command::solid_color_brush,
        brush,
        1.0,
        progpu_native_color{0.2F, 0.4F, 0.8F, 1.0F},
        0U,
        0U,
        0U,
        0U);
    const std::array dash_intervals{3.0, 1.0};
    append_dash_style(batch, dash, 0.75, 0U, dash_intervals);
    append_command(
        batch,
        command::pen,
        pen,
        2.0,
        4.0,
        brush,
        0U,
        1U,
        2U,
        3U,
        1U,
        dash);
    append_command(
        batch,
        command::matrix_transform,
        transform,
        1.5,
        0.0,
        0.0,
        1.5,
        2.0,
        3.0,
        0U);
    append_path_geometry(batch, geometry, transform, 0U, figures);
    std::vector<std::byte> nested;
    append_command(
        nested,
        command::draw_geometry,
        0U,
        pen,
        geometry,
        0U);
    append_render_data(batch, content, nested);
    append_command(
        batch,
        command::generic_target_create,
        target,
        std::uint64_t{0U},
        std::uint64_t{0U},
        64U,
        64U,
        0U);
    append_command(batch, command::target_set_root, target, visual);

    channel state;
    PROGPU_REQUIRE(state.apply(batch) == status::success);
    std::vector<std::byte> stream;
    progpu::native::mil::scene_metrics metrics{};
    PROGPU_REQUIRE(
        state.build_scene(target, 7002U, 1U, stream, &metrics) ==
        status::success);
    PROGPU_REQUIRE(metrics.brush_count == 1U);
    const auto header = read_value<progpu_native_scene_header>(stream, 0U);
    std::uint32_t stroke_batch_count = 0U;
    std::uint32_t closed_count = 0U;
    std::uint32_t open_count = 0U;
    for (std::uint32_t index = 0U; index < header.resource_count; ++index) {
        const auto record = read_value<progpu_native_scene_resource>(
            stream,
            header.resource_offset +
                index * sizeof(progpu_native_scene_resource));
        if (record.kind != PROGPU_NATIVE_SCENE_RESOURCE_STROKE_BATCH) {
            continue;
        }
        const auto stroke = read_value<progpu_native_scene_stroke>(
            stream,
            record.payload_offset);
        PROGPU_REQUIRE(stroke.kind == PROGPU_NATIVE_SCENE_STROKE_POLYLINE);
        PROGPU_REQUIRE(stroke.stroke_thickness == 2.0F);
        PROGPU_REQUIRE(stroke.miter_limit == 4.0F);
        PROGPU_REQUIRE(stroke.dash_cap == 3U);
        PROGPU_REQUIRE(stroke.line_join == 1U);
        PROGPU_REQUIRE(stroke.dash_interval_count == 2U);
        PROGPU_REQUIRE(stroke.dash_offset == 0.75);
        PROGPU_REQUIRE(stroke.transform.m11 == 1.5F);
        PROGPU_REQUIRE(stroke.transform.m22 == 1.5F);
        PROGPU_REQUIRE(stroke.transform.m31 == 2.0F);
        PROGPU_REQUIRE(stroke.transform.m32 == 3.0F);
        if ((stroke.flags & PROGPU_NATIVE_POLYLINE_FLAG_CLOSED) != 0U) {
            PROGPU_REQUIRE(stroke.point_count == 4U);
            PROGPU_REQUIRE(stroke.start_cap == 1U);
            PROGPU_REQUIRE(stroke.end_cap == 2U);
            ++closed_count;
        } else {
            PROGPU_REQUIRE(stroke.point_count == 4U);
            PROGPU_REQUIRE(stroke.start_cap == 3U);
            PROGPU_REQUIRE(stroke.end_cap == 3U);
            ++open_count;
        }
        ++stroke_batch_count;
    }
    PROGPU_REQUIRE(stroke_batch_count == 2U);
    PROGPU_REQUIRE(closed_count == 1U);
    PROGPU_REQUIRE(open_count == 1U);

    auto seam_dashed_figures = figures;
    const std::uint32_t stroked = 0U;
    const std::uint32_t gap = 0x04U;
    std::memcpy(
        seam_dashed_figures.data() + 228U,
        &stroked,
        sizeof(stroked));
    std::memcpy(
        seam_dashed_figures.data() + 260U,
        &gap,
        sizeof(gap));
    std::vector<std::byte> seam_dashed_update;
    append_path_geometry(
        seam_dashed_update,
        geometry,
        transform,
        0U,
        seam_dashed_figures);
    PROGPU_REQUIRE(state.apply(seam_dashed_update) == status::success);
    PROGPU_REQUIRE(
        state.build_scene(target, 7002U, 2U, stream, &metrics) ==
        status::success);
    const auto seam_header =
        read_value<progpu_native_scene_header>(stream, 0U);
    bool found_wrapped_dashed_run = false;
    for (std::uint32_t index = 0U;
         index < seam_header.resource_count;
         ++index) {
        const auto record = read_value<progpu_native_scene_resource>(
            stream,
            seam_header.resource_offset +
                index * sizeof(progpu_native_scene_resource));
        if (record.kind != PROGPU_NATIVE_SCENE_RESOURCE_STROKE_BATCH) {
            continue;
        }
        const auto stroke = read_value<progpu_native_scene_stroke>(
            stream,
            record.payload_offset);
        if ((stroke.flags & PROGPU_NATIVE_POLYLINE_FLAG_CLOSED) != 0U ||
            stroke.point_count != 4U) {
            continue;
        }
        const auto first = read_value<progpu_native_point>(
            stream,
            record.auxiliary_offset);
        const auto second = read_value<progpu_native_point>(
            stream,
            record.auxiliary_offset + sizeof(progpu_native_point));
        const auto third = read_value<progpu_native_point>(
            stream,
            record.auxiliary_offset + 2U * sizeof(progpu_native_point));
        const auto fourth = read_value<progpu_native_point>(
            stream,
            record.auxiliary_offset + 3U * sizeof(progpu_native_point));
        if (first.x != 28.0F || first.y != 0.0F) {
            continue;
        }
        PROGPU_REQUIRE(second.x == 32.0F && second.y == 0.0F);
        PROGPU_REQUIRE(third.x == 20.0F && third.y == 0.0F);
        PROGPU_REQUIRE(fourth.x == 24.0F && fourth.y == 0.0F);
        PROGPU_REQUIRE(stroke.start_cap == 3U && stroke.end_cap == 3U);
        PROGPU_REQUIRE(stroke.dash_interval_count == 2U);
        PROGPU_REQUIRE(stroke.dash_offset == 0.75);
        found_wrapped_dashed_run = true;
    }
    PROGPU_REQUIRE(found_wrapped_dashed_run);

    auto smooth_figures = figures;
    const std::uint32_t smooth_join = 0x08U;
    std::memcpy(
        smooth_figures.data() + 92U,
        &smooth_join,
        sizeof(smooth_join));
    std::vector<std::byte> smooth_update;
    append_path_geometry(
        smooth_update,
        geometry,
        transform,
        0U,
        smooth_figures);
    PROGPU_REQUIRE(state.apply(smooth_update) == status::success);
    PROGPU_REQUIRE(
        state.build_scene(target, 7002U, 3U, stream, &metrics) ==
        status::unsupported_command);

    auto open_arc_figures = make_arc_path_figures();
    const std::uint32_t open_curve_figure = 0x0aU;
    std::memcpy(
        open_arc_figures.data() + 52U,
        &open_curve_figure,
        sizeof(open_curve_figure));
    std::vector<std::byte> solid_arc_update;
    append_command(
        solid_arc_update,
        command::pen,
        pen,
        2.0,
        4.0,
        brush,
        0U,
        0U,
        0U,
        0U,
        1U,
        0U);
    append_path_geometry(
        solid_arc_update,
        geometry,
        transform,
        0U,
        open_arc_figures);
    PROGPU_REQUIRE(state.apply(solid_arc_update) == status::success);
    PROGPU_REQUIRE(
        state.build_scene(target, 7002U, 4U, stream, &metrics) ==
        status::success);
    const auto arc_header = read_value<progpu_native_scene_header>(stream, 0U);
    bool found_arc_stroke = false;
    for (std::uint32_t index = 0U;
         index < arc_header.resource_count;
         ++index) {
        const auto record = read_value<progpu_native_scene_resource>(
            stream,
            arc_header.resource_offset +
                index * sizeof(progpu_native_scene_resource));
        if (record.kind != PROGPU_NATIVE_SCENE_RESOURCE_GEOMETRY_BATCH) {
            continue;
        }
        const auto primitive = read_value<progpu_native_geometry_primitive>(
            stream,
            record.payload_offset);
        if (primitive.kind != PROGPU_NATIVE_GEOMETRY_ARC) {
            continue;
        }
        PROGPU_REQUIRE(primitive.stroke_thickness == 2.0F);
        PROGPU_REQUIRE(primitive.transform.m11 == 1.5F);
        PROGPU_REQUIRE(primitive.transform.m22 == 1.5F);
        PROGPU_REQUIRE(primitive.transform.m31 == 2.0F);
        PROGPU_REQUIRE(primitive.transform.m32 == 3.0F);
        PROGPU_REQUIRE(primitive.p3.y > 0.0F);
        found_arc_stroke = true;
    }
    PROGPU_REQUIRE(found_arc_stroke);

    const auto contains_geometry_kind = [](const std::vector<std::byte>& scene,
                                            std::uint32_t kind) {
        const auto scene_header =
            read_value<progpu_native_scene_header>(scene, 0U);
        for (std::uint32_t index = 0U;
             index < scene_header.resource_count;
             ++index) {
            const auto record = read_value<progpu_native_scene_resource>(
                scene,
                scene_header.resource_offset +
                    index * sizeof(progpu_native_scene_resource));
            if (record.kind != PROGPU_NATIVE_SCENE_RESOURCE_GEOMETRY_BATCH) {
                continue;
            }
            const auto primitive =
                read_value<progpu_native_geometry_primitive>(
                    scene,
                    record.payload_offset);
            if (primitive.kind == kind) {
                return true;
            }
        }
        return false;
    };
    const std::array quadratic_points{
        std::array{5.0, 9.0},
        std::array{11.0, 3.0}};
    std::vector<std::byte> quadratic_update;
    append_path_geometry(
        quadratic_update,
        geometry,
        transform,
        0U,
        make_single_bezier_path_figures(3U, quadratic_points));
    PROGPU_REQUIRE(state.apply(quadratic_update) == status::success);
    PROGPU_REQUIRE(
        state.build_scene(target, 7002U, 5U, stream, &metrics) ==
        status::success);
    PROGPU_REQUIRE(contains_geometry_kind(
        stream,
        PROGPU_NATIVE_GEOMETRY_QUADRATIC_BEZIER));

    const std::array cubic_points{
        std::array{4.0, 10.0},
        std::array{8.0, -2.0},
        std::array{12.0, 6.0}};
    std::vector<std::byte> cubic_update;
    append_path_geometry(
        cubic_update,
        geometry,
        transform,
        0U,
        make_single_bezier_path_figures(2U, cubic_points));
    PROGPU_REQUIRE(state.apply(cubic_update) == status::success);
    PROGPU_REQUIRE(
        state.build_scene(target, 7002U, 6U, stream, &metrics) ==
        status::success);
    PROGPU_REQUIRE(contains_geometry_kind(
        stream,
        PROGPU_NATIVE_GEOMETRY_CUBIC_BEZIER));

    std::vector<std::byte> joined_curve_update;
    append_path_geometry(
        joined_curve_update,
        geometry,
        transform,
        0U,
        make_curve_path_figures());
    PROGPU_REQUIRE(state.apply(joined_curve_update) == status::success);
    PROGPU_REQUIRE(
        state.build_scene(target, 7002U, 7U, stream, &metrics) ==
        status::success);
    const auto joined_header =
        read_value<progpu_native_scene_header>(stream, 0U);
    std::uint32_t joined_line_count = 0U;
    std::uint32_t joined_quadratic_count = 0U;
    std::uint32_t joined_cubic_count = 0U;
    std::uint32_t joined_join_count = 0U;
    for (std::uint32_t resource_index = 0U;
         resource_index < joined_header.resource_count;
         ++resource_index) {
        const auto record = read_value<progpu_native_scene_resource>(
            stream,
            joined_header.resource_offset +
                resource_index * sizeof(progpu_native_scene_resource));
        if (record.kind != PROGPU_NATIVE_SCENE_RESOURCE_GEOMETRY_BATCH) {
            continue;
        }
        PROGPU_REQUIRE(record.payload_size >= primitive_stride);
        std::uint32_t primitive_offset = 0U;
#if defined(__clang__)
#pragma clang loop vectorize(disable) interleave(disable)
#endif
        for (;
             primitive_offset + primitive_stride <= record.payload_size;
             primitive_offset += primitive_stride) {
            const auto primitive =
                read_value<progpu_native_geometry_primitive>(
                    stream,
                    record.payload_offset + primitive_offset);
            joined_line_count +=
                primitive.kind == PROGPU_NATIVE_GEOMETRY_LINE ? 1U : 0U;
            joined_quadratic_count +=
                primitive.kind == PROGPU_NATIVE_GEOMETRY_QUADRATIC_BEZIER
                ? 1U
                : 0U;
            joined_cubic_count +=
                primitive.kind == PROGPU_NATIVE_GEOMETRY_CUBIC_BEZIER
                ? 1U
                : 0U;
            joined_join_count +=
                primitive.kind == PROGPU_NATIVE_GEOMETRY_PATH_JOIN ? 1U : 0U;
        }
        PROGPU_REQUIRE(primitive_offset == record.payload_size);
    }
    PROGPU_REQUIRE(joined_line_count == 2U);
    PROGPU_REQUIRE(joined_quadratic_count == 1U);
    PROGPU_REQUIRE(joined_cubic_count == 1U);
    PROGPU_REQUIRE(joined_join_count == 4U);

    auto smooth_curve_figures = make_curve_path_figures();
    const std::uint32_t smooth_curve_join = 0x08U;
    std::memcpy(
        smooth_curve_figures.data() + 92U,
        &smooth_curve_join,
        sizeof(smooth_curve_join));
    std::vector<std::byte> smooth_curve_update;
    append_path_geometry(
        smooth_curve_update,
        geometry,
        transform,
        0U,
        smooth_curve_figures);
    PROGPU_REQUIRE(state.apply(smooth_curve_update) == status::success);
    PROGPU_REQUIRE(
        state.build_scene(target, 7002U, 8U, stream, &metrics) ==
        status::success);
    const auto smooth_curve_header =
        read_value<progpu_native_scene_header>(stream, 0U);
    std::uint32_t bevel_join_count = 0U;
    std::uint32_t round_join_count = 0U;
    for (std::uint32_t resource_index = 0U;
         resource_index < smooth_curve_header.resource_count;
         ++resource_index) {
        const auto record = read_value<progpu_native_scene_resource>(
            stream,
            smooth_curve_header.resource_offset +
                resource_index * sizeof(progpu_native_scene_resource));
        if (record.kind != PROGPU_NATIVE_SCENE_RESOURCE_GEOMETRY_BATCH) {
            continue;
        }
        PROGPU_REQUIRE(record.payload_size >= primitive_stride);
        std::uint32_t primitive_offset = 0U;
#if defined(__clang__)
#pragma clang loop vectorize(disable) interleave(disable)
#endif
        for (;
             primitive_offset + primitive_stride <= record.payload_size;
             primitive_offset += primitive_stride) {
            const auto primitive =
                read_value<progpu_native_geometry_primitive>(
                    stream,
                    record.payload_offset + primitive_offset);
            if (primitive.kind != PROGPU_NATIVE_GEOMETRY_PATH_JOIN) {
                continue;
            }
            const std::uint32_t join =
                (primitive.flags & PROGPU_NATIVE_PRIMITIVE_START_CAP_MASK) >>
                    PROGPU_NATIVE_PRIMITIVE_START_CAP_SHIFT;
            bevel_join_count +=
                join == PROGPU_NATIVE_STROKE_JOIN_BEVEL ? 1U : 0U;
            round_join_count +=
                join == PROGPU_NATIVE_STROKE_JOIN_ROUND ? 1U : 0U;
        }
        PROGPU_REQUIRE(primitive_offset == record.payload_size);
    }
    PROGPU_REQUIRE(bevel_join_count == 3U);
    PROGPU_REQUIRE(round_join_count == 1U);

    PROGPU_REQUIRE(state.apply(solid_arc_update) == status::success);

    std::vector<std::byte> dashed_arc_update;
    append_command(
        dashed_arc_update,
        command::pen,
        pen,
        2.0,
        4.0,
        brush,
        0U,
        0U,
        0U,
        0U,
        1U,
        dash);
    PROGPU_REQUIRE(state.apply(dashed_arc_update) == status::success);
    PROGPU_REQUIRE(
        state.build_scene(target, 7002U, 9U, stream, &metrics) ==
        status::unsupported_command);

    std::vector<std::byte> capped_arc_update;
    append_command(
        capped_arc_update,
        command::pen,
        pen,
        2.0,
        4.0,
        brush,
        0U,
        2U,
        3U,
        0U,
        1U,
        0U);
    PROGPU_REQUIRE(state.apply(capped_arc_update) == status::success);
    PROGPU_REQUIRE(
        state.build_scene(target, 7002U, 10U, stream, &metrics) ==
        status::success);
    const auto capped_header =
        read_value<progpu_native_scene_header>(stream, 0U);
    std::uint32_t capped_arc_count = 0U;
    std::uint32_t start_cap_count = 0U;
    std::uint32_t end_cap_count = 0U;
    for (std::uint32_t resource_index = 0U;
         resource_index < capped_header.resource_count;
         ++resource_index) {
        const auto record = read_value<progpu_native_scene_resource>(
            stream,
            capped_header.resource_offset +
                resource_index * sizeof(progpu_native_scene_resource));
        if (record.kind != PROGPU_NATIVE_SCENE_RESOURCE_GEOMETRY_BATCH) {
            continue;
        }
        PROGPU_REQUIRE(record.payload_size >= primitive_stride);
        std::uint32_t primitive_offset = 0U;
#if defined(__clang__)
#pragma clang loop vectorize(disable) interleave(disable)
#endif
        for (;
             primitive_offset + primitive_stride <= record.payload_size;
             primitive_offset += primitive_stride) {
            const auto primitive =
                read_value<progpu_native_geometry_primitive>(
                    stream,
                    record.payload_offset + primitive_offset);
            if (primitive.kind == PROGPU_NATIVE_GEOMETRY_ARC) {
                ++capped_arc_count;
                continue;
            }
            if (primitive.kind != PROGPU_NATIVE_GEOMETRY_PATH_CAP) {
                continue;
            }
            const std::uint32_t cap =
                (primitive.flags & PROGPU_NATIVE_PRIMITIVE_START_CAP_MASK) >>
                    PROGPU_NATIVE_PRIMITIVE_START_CAP_SHIFT;
            PROGPU_REQUIRE(primitive.stroke_thickness == 2.0F);
            PROGPU_REQUIRE(primitive.transform.m11 == 1.5F);
            PROGPU_REQUIRE(primitive.transform.m22 == 1.5F);
            if (primitive.p2.x == 1.0F) {
                PROGPU_REQUIRE(cap == PROGPU_NATIVE_STROKE_CAP_ROUND);
                PROGPU_REQUIRE(primitive.p0.x == 1.0F);
                PROGPU_REQUIRE(primitive.p0.y == 2.0F);
                ++start_cap_count;
            } else {
                PROGPU_REQUIRE(primitive.p2.x == 0.0F);
                PROGPU_REQUIRE(cap == PROGPU_NATIVE_STROKE_CAP_TRIANGLE);
                PROGPU_REQUIRE(primitive.p0.x == 9.0F);
                PROGPU_REQUIRE(primitive.p0.y == 8.0F);
                ++end_cap_count;
            }
        }
        PROGPU_REQUIRE(primitive_offset == record.payload_size);
    }
    PROGPU_REQUIRE(capped_arc_count == 1U);
    PROGPU_REQUIRE(start_cap_count == 1U);
    PROGPU_REQUIRE(end_cap_count == 1U);

    constexpr std::uint32_t zero_line_size = 32U;
    constexpr std::uint32_t zero_figure_size = 40U + zero_line_size;
    constexpr std::uint32_t zero_figures_size =
        48U + 2U * zero_figure_size;
    std::vector<std::byte> zero_figures;
    append_value(zero_figures, zero_figures_size);
    append_value(zero_figures, 0x02U);
    append_value(zero_figures, 5.0);
    append_value(zero_figures, 6.0);
    append_value(zero_figures, 10.0);
    append_value(zero_figures, 12.0);
    append_value(zero_figures, 2U);
    append_value(zero_figures, 0U);
    const auto append_zero_figure = [
        &zero_figures,
        zero_figure_size](
        std::uint32_t back_size,
        std::uint32_t flags,
        double x,
        double y) {
        append_value(zero_figures, back_size);
        append_value(zero_figures, flags);
        append_value(zero_figures, 1U);
        append_value(zero_figures, zero_figure_size);
        append_value(zero_figures, x);
        append_value(zero_figures, y);
        append_value(zero_figures, 40U);
        append_value(zero_figures, 0U);
        append_value(zero_figures, 1U);
        append_value(zero_figures, 0U);
        append_value(zero_figures, 0U);
        append_value(zero_figures, 0U);
        append_value(zero_figures, x);
        append_value(zero_figures, y);
    };
    append_zero_figure(0U, 0U, 5.0, 6.0);
    append_zero_figure(zero_figure_size, 0x04U, 10.0, 12.0);
    PROGPU_REQUIRE(zero_figures.size() == zero_figures_size);
    std::vector<std::byte> zero_path_update;
    append_path_geometry(
        zero_path_update,
        geometry,
        transform,
        0U,
        zero_figures);
    PROGPU_REQUIRE(state.apply(zero_path_update) == status::success);
    PROGPU_REQUIRE(
        state.build_scene(target, 7002U, 11U, stream, &metrics) ==
        status::success);
    const auto zero_path_header =
        read_value<progpu_native_scene_header>(stream, 0U);
    std::uint32_t zero_round_cap_count = 0U;
    std::uint32_t zero_triangle_cap_count = 0U;
    std::uint32_t zero_start_cap_count = 0U;
    std::uint32_t zero_end_cap_count = 0U;
    for (std::uint32_t resource_index = 0U;
         resource_index < zero_path_header.resource_count;
         ++resource_index) {
        const auto record = read_value<progpu_native_scene_resource>(
            stream,
            zero_path_header.resource_offset +
                resource_index * sizeof(progpu_native_scene_resource));
        if (record.kind != PROGPU_NATIVE_SCENE_RESOURCE_GEOMETRY_BATCH) {
            continue;
        }
        PROGPU_REQUIRE(record.payload_size >= primitive_stride);
        std::uint32_t primitive_offset = 0U;
#if defined(__clang__)
#pragma clang loop vectorize(disable) interleave(disable)
#endif
        for (;
             primitive_offset + primitive_stride <= record.payload_size;
             primitive_offset += primitive_stride) {
            const auto primitive =
                read_value<progpu_native_geometry_primitive>(
                    stream,
                    record.payload_offset + primitive_offset);
            PROGPU_REQUIRE(
                primitive.kind == PROGPU_NATIVE_GEOMETRY_PATH_CAP);
            PROGPU_REQUIRE(primitive.p1.x == 1.0F);
            PROGPU_REQUIRE(primitive.p1.y == 0.0F);
            const std::uint32_t cap =
                (primitive.flags & PROGPU_NATIVE_PRIMITIVE_START_CAP_MASK) >>
                PROGPU_NATIVE_PRIMITIVE_START_CAP_SHIFT;
            zero_round_cap_count +=
                cap == PROGPU_NATIVE_STROKE_CAP_ROUND ? 1U : 0U;
            zero_triangle_cap_count +=
                cap == PROGPU_NATIVE_STROKE_CAP_TRIANGLE ? 1U : 0U;
            zero_start_cap_count += primitive.p2.x == 1.0F ? 1U : 0U;
            zero_end_cap_count += primitive.p2.x == 0.0F ? 1U : 0U;
        }
        PROGPU_REQUIRE(primitive_offset == record.payload_size);
    }
    PROGPU_REQUIRE(zero_round_cap_count == 3U);
    PROGPU_REQUIRE(zero_triangle_cap_count == 1U);
    PROGPU_REQUIRE(zero_start_cap_count == 2U);
    PROGPU_REQUIRE(zero_end_cap_count == 2U);

    std::vector<std::byte> boundary_dash_update;
    append_dash_style(
        boundary_dash_update,
        dash,
        3.0,
        0U,
        dash_intervals);
    append_command(
        boundary_dash_update,
        command::pen,
        pen,
        2.0,
        4.0,
        brush,
        0U,
        0U,
        0U,
        0U,
        1U,
        dash);
    PROGPU_REQUIRE(state.apply(boundary_dash_update) == status::success);
    PROGPU_REQUIRE(
        state.build_scene(target, 7002U, 12U, stream, &metrics) ==
        status::success);
    const auto boundary_dash_header =
        read_value<progpu_native_scene_header>(stream, 0U);
    std::uint32_t boundary_dash_cap_count = 0U;
    for (std::uint32_t resource_index = 0U;
         resource_index < boundary_dash_header.resource_count;
         ++resource_index) {
        const auto record = read_value<progpu_native_scene_resource>(
            stream,
            boundary_dash_header.resource_offset +
                resource_index * sizeof(progpu_native_scene_resource));
        if (record.kind != PROGPU_NATIVE_SCENE_RESOURCE_GEOMETRY_BATCH) {
            continue;
        }
        PROGPU_REQUIRE(record.payload_size >= primitive_stride);
        std::uint32_t primitive_offset = 0U;
#if defined(__clang__)
#pragma clang loop vectorize(disable) interleave(disable)
#endif
        for (;
             primitive_offset + primitive_stride <= record.payload_size;
             primitive_offset += primitive_stride) {
            const auto primitive =
                read_value<progpu_native_geometry_primitive>(
                    stream,
                    record.payload_offset + primitive_offset);
            if (primitive.kind == PROGPU_NATIVE_GEOMETRY_PATH_CAP) {
                ++boundary_dash_cap_count;
            }
        }
        PROGPU_REQUIRE(primitive_offset == record.payload_size);
    }
    PROGPU_REQUIRE(boundary_dash_cap_count == 2U);

    std::vector<std::byte> gap_dash_update;
    append_dash_style(
        gap_dash_update,
        dash,
        3.5,
        0U,
        dash_intervals);
    PROGPU_REQUIRE(state.apply(gap_dash_update) == status::success);
    PROGPU_REQUIRE(
        state.build_scene(target, 7002U, 13U, stream, &metrics) ==
        status::success);
    const auto gap_dash_header =
        read_value<progpu_native_scene_header>(stream, 0U);
    for (std::uint32_t resource_index = 0U;
         resource_index < gap_dash_header.resource_count;
         ++resource_index) {
        const auto record = read_value<progpu_native_scene_resource>(
            stream,
            gap_dash_header.resource_offset +
                resource_index * sizeof(progpu_native_scene_resource));
        PROGPU_REQUIRE(
            record.kind != PROGPU_NATIVE_SCENE_RESOURCE_GEOMETRY_BATCH);
    }
    return true;
}

bool retained_geometry_drawing_reuses_native_geometry_lowering() {
    constexpr std::uint32_t visual = 1U;
    constexpr std::uint32_t content = 2U;
    constexpr std::uint32_t target = 3U;
    constexpr std::uint32_t brush = 4U;
    constexpr std::uint32_t geometry = 5U;
    constexpr std::uint32_t drawing = 6U;

    std::vector<std::byte> batch;
    append_create(batch, visual, 39U);
    append_create(batch, content, 43U);
    append_create(batch, target, 47U);
    append_create(batch, brush, 75U);
    append_create(batch, geometry, 69U);
    append_create(batch, drawing, 87U);
    append_command(batch, command::visual_create, visual);
    append_command(batch, command::visual_set_content, visual, content);
    append_command(
        batch,
        command::solid_color_brush,
        brush,
        1.0,
        progpu_native_color{0.25F, 0.5F, 0.75F, 1.0F},
        0U,
        0U,
        0U,
        0U);
    append_command(
        batch,
        command::rectangle_geometry,
        geometry,
        0.0,
        0.0,
        2.0,
        3.0,
        20.0,
        10.0,
        0U,
        0U,
        0U,
        0U);
    append_command(
        batch,
        command::geometry_drawing,
        drawing,
        brush,
        0U,
        geometry);
    std::vector<std::byte> nested;
    append_command(nested, command::draw_drawing, drawing, 0U);
    append_render_data(batch, content, nested);
    append_command(
        batch,
        command::generic_target_create,
        target,
        std::uint64_t{0U},
        std::uint64_t{0U},
        64U,
        64U,
        0U);
    append_command(batch, command::target_set_root, target, visual);

    channel state;
    PROGPU_REQUIRE(state.apply(batch) == status::success);
    std::vector<std::byte> stream;
    progpu::native::mil::scene_metrics metrics{};
    PROGPU_REQUIRE(
        state.build_scene(target, 7003U, 1U, stream, &metrics) ==
        status::success);
    PROGPU_REQUIRE(metrics.rectangle_count == 1U);
    PROGPU_REQUIRE(metrics.brush_count == 1U);

    std::vector<std::byte> delete_geometry;
    append_command(
        delete_geometry,
        command::channel_delete_resource,
        geometry,
        69U);
    PROGPU_REQUIRE(state.apply(delete_geometry) == status::invalid_graph);

    std::vector<std::byte> clear_drawing;
    append_command(
        clear_drawing,
        command::geometry_drawing,
        drawing,
        brush,
        0U,
        0U);
    PROGPU_REQUIRE(state.apply(clear_drawing) == status::success);
    PROGPU_REQUIRE(
        state.build_scene(target, 7003U, 2U, stream, &metrics) ==
        status::success);
    PROGPU_REQUIRE(metrics.rectangle_count == 0U);
    PROGPU_REQUIRE(metrics.brush_count == 0U);

    std::vector<std::byte> invalid_drawing;
    append_command(
        invalid_drawing,
        command::geometry_drawing,
        drawing,
        target,
        0U,
        geometry);
    PROGPU_REQUIRE(state.apply(invalid_drawing) == status::invalid_handle);
    PROGPU_REQUIRE(
        state.build_scene(target, 7003U, 3U, stream, &metrics) ==
        status::success);
    PROGPU_REQUIRE(metrics.rectangle_count == 0U);
    return true;
}

bool retained_drawing_group_composes_children_transform_and_opacity() {
    constexpr std::uint32_t visual = 1U;
    constexpr std::uint32_t content = 2U;
    constexpr std::uint32_t target = 3U;
    constexpr std::uint32_t brush = 4U;
    constexpr std::uint32_t geometry = 5U;
    constexpr std::uint32_t drawing = 6U;
    constexpr std::uint32_t group = 7U;
    constexpr std::uint32_t transform = 8U;
    constexpr std::uint32_t opacity = 9U;
    constexpr std::uint32_t clip = 10U;
    constexpr std::uint32_t opacity_mask = 11U;

    std::vector<std::byte> batch;
    append_create(batch, visual, 39U);
    append_create(batch, content, 43U);
    append_create(batch, target, 47U);
    append_create(batch, brush, 75U);
    append_create(batch, geometry, 69U);
    append_create(batch, drawing, 87U);
    append_create(batch, group, 91U);
    append_create(batch, transform, 66U);
    append_create(batch, opacity, 49U);
    append_create(batch, clip, 69U);
    append_create(batch, opacity_mask, 75U);
    append_command(batch, command::visual_create, visual);
    append_command(batch, command::visual_set_content, visual, content);
    append_command(
        batch,
        command::solid_color_brush,
        brush,
        1.0,
        progpu_native_color{0.25F, 0.5F, 0.75F, 1.0F},
        0U,
        0U,
        0U,
        0U);
    append_command(
        batch,
        command::rectangle_geometry,
        geometry,
        0.0,
        0.0,
        2.0,
        3.0,
        20.0,
        10.0,
        0U,
        0U,
        0U,
        0U);
    append_command(
        batch,
        command::geometry_drawing,
        drawing,
        brush,
        0U,
        geometry);
    append_command(
        batch,
        command::matrix_transform,
        transform,
        1.0,
        0.0,
        0.0,
        1.0,
        10.0,
        20.0,
        0U);
    append_command(batch, command::double_resource, opacity, 0.5);
    append_command(
        batch,
        command::solid_color_brush,
        opacity_mask,
        0.5,
        progpu_native_color{1.0F, 1.0F, 1.0F, 0.5F},
        0U,
        0U,
        0U,
        0U);
    append_command(
        batch,
        command::rectangle_geometry,
        clip,
        0.0,
        0.0,
        0.0,
        0.0,
        10.0,
        10.0,
        0U,
        0U,
        0U,
        0U);
    append_command(
        batch,
        command::drawing_group,
        group,
        1.0,
        4U,
        clip,
        opacity,
        opacity_mask,
        transform,
        0U,
        1U,
        0U,
        1U,
        drawing);
    std::vector<std::byte> nested;
    append_command(nested, command::draw_drawing, group, 0U);
    append_render_data(batch, content, nested);
    append_command(
        batch,
        command::generic_target_create,
        target,
        std::uint64_t{0U},
        std::uint64_t{0U},
        64U,
        64U,
        0U);
    append_command(batch, command::target_set_root, target, visual);

    channel state;
    PROGPU_REQUIRE(state.apply(batch) == status::success);
    std::vector<std::byte> stream;
    progpu::native::mil::scene_metrics metrics{};
    PROGPU_REQUIRE(
        state.build_scene(target, 7004U, 1U, stream, &metrics) ==
        status::success);
    PROGPU_REQUIRE(metrics.rectangle_count == 1U);
    PROGPU_REQUIRE(metrics.brush_count == 1U);
    const auto header = read_value<progpu_native_scene_header>(stream, 0U);
    bool found_group_state = false;
    bool found_bounds = false;
    bool found_aliased_edge = false;
    for (std::uint32_t index = 0U; index < header.resource_count; ++index) {
        const auto resource = read_value<progpu_native_scene_resource>(
            stream,
            header.resource_offset +
                index * sizeof(progpu_native_scene_resource));
        if (resource.kind == PROGPU_NATIVE_SCENE_RESOURCE_STATE) {
            const auto scene_state = read_value<progpu_native_scene_state>(
                stream,
                resource.payload_offset);
            if (scene_state.opacity == 0.125F &&
                scene_state.transform.m31 == 10.0F &&
                scene_state.transform.m32 == 20.0F &&
                (scene_state.flags &
                    PROGPU_NATIVE_SCENE_STATE_CLIP_RECT) != 0U &&
                scene_state.clip_rect.x == 10.0F &&
                scene_state.clip_rect.y == 20.0F &&
                scene_state.clip_rect.width == 10.0F &&
                scene_state.clip_rect.height == 10.0F) {
                found_group_state = true;
            }
        } else if (
            resource.kind == PROGPU_NATIVE_SCENE_RESOURCE_ANALYTIC_BATCH) {
            const auto primitive =
                read_value<progpu_native_analytic_primitive>(
                    stream,
                    resource.payload_offset);
            found_aliased_edge |=
                (primitive.flags &
                    PROGPU_NATIVE_PRIMITIVE_FLAG_EDGE_ALIASED) != 0U;
        }
    }
    for (std::uint32_t index = 0U; index < header.command_count; ++index) {
        const auto record = read_value<progpu_native_scene_command>(
            stream,
            header.command_offset +
                index * sizeof(progpu_native_scene_command));
        if (record.kind == PROGPU_NATIVE_SCENE_COMMAND_DRAW_ANALYTIC &&
            record.bounds_x == 12.0F && record.bounds_y == 23.0F &&
            record.bounds_width == 20.0F &&
            record.bounds_height == 10.0F) {
            found_bounds = true;
        }
    }
    PROGPU_REQUIRE(found_group_state);
    PROGPU_REQUIRE(found_bounds);
    PROGPU_REQUIRE(found_aliased_edge);

    std::vector<std::byte> opacity_update;
    append_command(opacity_update, command::double_resource, opacity, 0.25);
    PROGPU_REQUIRE(state.apply(opacity_update) == status::success);
    PROGPU_REQUIRE(
        state.build_scene(target, 7004U, 2U, stream, &metrics) ==
        status::success);
    const auto updated_header =
        read_value<progpu_native_scene_header>(stream, 0U);
    bool found_updated_opacity = false;
    for (std::uint32_t index = 0U;
         index < updated_header.resource_count;
         ++index) {
        const auto resource = read_value<progpu_native_scene_resource>(
            stream,
            updated_header.resource_offset +
                index * sizeof(progpu_native_scene_resource));
        if (resource.kind == PROGPU_NATIVE_SCENE_RESOURCE_STATE) {
            const auto scene_state = read_value<progpu_native_scene_state>(
                stream,
                resource.payload_offset);
            found_updated_opacity |= scene_state.opacity == 0.0625F &&
                scene_state.transform.m31 == 10.0F &&
                scene_state.transform.m32 == 20.0F;
        }
    }
    PROGPU_REQUIRE(found_updated_opacity);

    std::vector<std::byte> delete_child;
    append_command(
        delete_child,
        command::channel_delete_resource,
        drawing,
        87U);
    PROGPU_REQUIRE(state.apply(delete_child) == status::invalid_graph);

    std::vector<std::byte> invalid_child;
    append_command(
        invalid_child,
        command::drawing_group,
        group,
        1.0,
        4U,
        clip,
        opacity,
        0U,
        transform,
        0U,
        0U,
        0U,
        0U,
        target);
    PROGPU_REQUIRE(state.apply(invalid_child) == status::invalid_handle);
    PROGPU_REQUIRE(
        state.build_scene(target, 7004U, 3U, stream, &metrics) ==
        status::success);
    PROGPU_REQUIRE(metrics.rectangle_count == 1U);
    return true;
}

bool retained_static_guideline_set_snaps_one_guide_per_axis() {
    constexpr std::uint32_t visual = 1U;
    constexpr std::uint32_t content = 2U;
    constexpr std::uint32_t target = 3U;
    constexpr std::uint32_t brush = 4U;
    constexpr std::uint32_t geometry = 5U;
    constexpr std::uint32_t drawing = 6U;
    constexpr std::uint32_t group = 7U;
    constexpr std::uint32_t transform = 8U;
    constexpr std::uint32_t guidelines = 9U;

    std::vector<std::byte> batch;
    append_create(batch, visual, 39U);
    append_create(batch, content, 43U);
    append_create(batch, target, 47U);
    append_create(batch, brush, 75U);
    append_create(batch, geometry, 69U);
    append_create(batch, drawing, 87U);
    append_create(batch, group, 91U);
    append_create(batch, transform, 66U);
    append_create(batch, guidelines, 92U);
    append_command(batch, command::visual_create, visual);
    append_command(batch, command::visual_set_content, visual, content);
    append_command(
        batch,
        command::solid_color_brush,
        brush,
        1.0,
        progpu_native_color{0.25F, 0.5F, 0.75F, 1.0F},
        0U,
        0U,
        0U,
        0U);
    append_command(
        batch,
        command::rectangle_geometry,
        geometry,
        0.0,
        0.0,
        2.0,
        3.0,
        20.0,
        10.0,
        0U,
        0U,
        0U,
        0U);
    append_command(
        batch,
        command::geometry_drawing,
        drawing,
        brush,
        0U,
        geometry);
    append_command(
        batch,
        command::matrix_transform,
        transform,
        1.0,
        0.0,
        0.0,
        1.0,
        10.0,
        20.0,
        0U);
    append_command(
        batch,
        command::guideline_set,
        guidelines,
        8U,
        8U,
        0U,
        2.25,
        3.5);
    append_command(
        batch,
        command::drawing_group,
        group,
        1.0,
        4U,
        0U,
        0U,
        0U,
        transform,
        guidelines,
        0U,
        0U,
        0U,
        drawing);
    std::vector<std::byte> nested;
    append_command(nested, command::draw_drawing, group, 0U);
    append_render_data(batch, content, nested);
    append_command(
        batch,
        command::generic_target_create,
        target,
        std::uint64_t{0U},
        std::uint64_t{0U},
        64U,
        64U,
        0U);
    append_command(batch, command::target_set_root, target, visual);

    channel state;
    PROGPU_REQUIRE(state.apply(batch) == status::success);
    std::vector<std::byte> stream;
    progpu::native::mil::scene_metrics metrics{};
    PROGPU_REQUIRE(
        state.build_scene(target, 7007U, 1U, stream, &metrics) ==
        status::success);
    const auto header = read_value<progpu_native_scene_header>(stream, 0U);
    bool found_guidelines = false;
    for (std::uint32_t index = 0U; index < header.resource_count; ++index) {
        const auto resource = read_value<progpu_native_scene_resource>(
            stream,
            header.resource_offset +
                index * sizeof(progpu_native_scene_resource));
        if (resource.kind != PROGPU_NATIVE_SCENE_RESOURCE_GUIDELINE_SET) {
            continue;
        }
        const auto value = read_value<progpu_native_scene_guideline_set>(
            stream, resource.payload_offset);
        PROGPU_REQUIRE(value.guideline_x_count == 1U);
        PROGPU_REQUIRE(value.guideline_y_count == 1U);
        PROGPU_REQUIRE(read_value<double>(
            stream, resource.payload_offset + sizeof(value)) == 12.25);
        PROGPU_REQUIRE(read_value<double>(
            stream,
            resource.payload_offset + sizeof(value) + sizeof(double)) ==
            23.5);
        found_guidelines = true;
    }
    PROGPU_REQUIRE(found_guidelines);

    bool found_guideline_state = false;
    for (std::uint32_t index = 0U; index < header.resource_count; ++index) {
        const auto resource = read_value<progpu_native_scene_resource>(
            stream,
            header.resource_offset +
                index * sizeof(progpu_native_scene_resource));
        if (resource.kind != PROGPU_NATIVE_SCENE_RESOURCE_STATE) {
            continue;
        }
        const auto scene_state = read_value<progpu_native_scene_state>(
            stream, resource.payload_offset);
        found_guideline_state |= (scene_state.flags &
            PROGPU_NATIVE_SCENE_STATE_GUIDELINE_SET) != 0U &&
            scene_state.transform.m31 == 10.0F &&
            scene_state.transform.m32 == 20.0F;
    }
    PROGPU_REQUIRE(found_guideline_state);

    std::vector<std::byte> multiple_update;
    append_command(
        multiple_update,
        command::guideline_set,
        guidelines,
        16U,
        0U,
        0U,
        1.0,
        2.0);
    PROGPU_REQUIRE(state.apply(multiple_update) == status::success);
    PROGPU_REQUIRE(
        state.build_scene(target, 7007U, 2U, stream, &metrics) ==
        status::unsupported_command);
    return true;
}

bool retained_image_drawing_uses_pointer_free_bitmap_sideband() {
    constexpr std::uint32_t visual = 1U;
    constexpr std::uint32_t content = 2U;
    constexpr std::uint32_t target = 3U;
    constexpr std::uint32_t bitmap = 4U;
    constexpr std::uint32_t drawing = 5U;
    constexpr std::uint32_t group = 6U;

    std::vector<std::byte> batch;
    append_create(batch, visual, 39U);
    append_create(batch, content, 43U);
    append_create(batch, target, 47U);
    append_create(batch, bitmap, 95U);
    append_create(batch, drawing, 89U);
    append_create(batch, group, 91U);
    append_command(batch, command::visual_create, visual);
    append_command(
        batch,
        command::visual_set_render_options,
        visual,
        0x09U,
        0U,
        0U,
        3U,
        1U,
        0U,
        0U);
    append_command(batch, command::visual_set_content, visual, content);
    append_command(
        batch,
        command::image_drawing,
        drawing,
        3.0,
        5.0,
        20.0,
        10.0,
        bitmap,
        0U);
    append_command(
        batch,
        command::drawing_group,
        group,
        1.0,
        4U,
        0U,
        0U,
        0U,
        0U,
        0U,
        0U,
        0U,
        0U,
        drawing);
    std::vector<std::byte> nested;
    append_command(nested, command::draw_drawing, group, 0U);
    append_render_data(batch, content, nested);
    append_command(
        batch,
        command::generic_target_create,
        target,
        std::uint64_t{0U},
        std::uint64_t{0U},
        64U,
        64U,
        0U);
    append_command(batch, command::target_set_root, target, visual);

    channel state;
    PROGPU_REQUIRE(state.apply(batch) == status::success);
    std::vector<std::byte> stream;
    progpu::native::mil::scene_metrics metrics{};
    PROGPU_REQUIRE(
        state.build_scene(target, 7005U, 1U, stream, &metrics) ==
        status::invalid_handle);

    constexpr std::array<std::byte, 16U> pixels{
        std::byte{255}, std::byte{0}, std::byte{0}, std::byte{255},
        std::byte{0}, std::byte{255}, std::byte{0}, std::byte{255},
        std::byte{0}, std::byte{0}, std::byte{255}, std::byte{255},
        std::byte{255}, std::byte{255}, std::byte{255}, std::byte{255}};
    PROGPU_REQUIRE(
        state.set_bitmap_source_rgba8(bitmap, 2U, 2U, 8U, pixels) ==
        status::success);
    PROGPU_REQUIRE(state.resource_generation(bitmap) == 2U);
    PROGPU_REQUIRE(
        state.build_scene(target, 7005U, 2U, stream, &metrics) ==
        status::success);

    const auto header = read_value<progpu_native_scene_header>(stream, 0U);
    bool found_image = false;
    for (std::uint32_t index = 0U; index < header.command_count; ++index) {
        const auto record = read_value<progpu_native_scene_command>(
            stream,
            header.command_offset +
                index * sizeof(progpu_native_scene_command));
        if (record.kind != PROGPU_NATIVE_SCENE_COMMAND_DRAW_IMAGE) {
            continue;
        }
        const auto resource = read_value<progpu_native_scene_resource>(
            stream,
            header.resource_offset +
                record.resource_index * sizeof(progpu_native_scene_resource));
        const auto image = read_value<progpu_native_scene_image_draw>(
            stream, record.payload_offset);
        PROGPU_REQUIRE(resource.kind == PROGPU_NATIVE_SCENE_RESOURCE_IMAGE);
        PROGPU_REQUIRE(resource.payload_size == pixels.size());
        PROGPU_REQUIRE(image.image_width == 2U);
        PROGPU_REQUIRE(image.image_height == 2U);
        PROGPU_REQUIRE(image.row_bytes == 8U);
        PROGPU_REQUIRE(image.sampling == PROGPU_NATIVE_IMAGE_SAMPLING_NEAREST);
        PROGPU_REQUIRE(image.source_rect.width == 2.0F);
        PROGPU_REQUIRE(image.source_rect.height == 2.0F);
        PROGPU_REQUIRE(image.destination_rect.x == 3.0F);
        PROGPU_REQUIRE(image.destination_rect.y == 5.0F);
        PROGPU_REQUIRE(image.destination_rect.width == 20.0F);
        PROGPU_REQUIRE(image.destination_rect.height == 10.0F);
        PROGPU_REQUIRE(record.bounds_x == 3.0F);
        PROGPU_REQUIRE(record.bounds_y == 5.0F);
        PROGPU_REQUIRE(record.bounds_width == 20.0F);
        PROGPU_REQUIRE(record.bounds_height == 10.0F);
        found_image = true;
    }
    PROGPU_REQUIRE(found_image);

    std::vector<std::byte> high_quality_update;
    append_command(
        high_quality_update,
        command::drawing_group,
        group,
        1.0,
        4U,
        0U,
        0U,
        0U,
        0U,
        0U,
        0U,
        2U,
        0U,
        drawing);
    PROGPU_REQUIRE(state.apply(high_quality_update) == status::success);
    PROGPU_REQUIRE(
        state.build_scene(target, 7005U, 3U, stream, &metrics) ==
        status::success);
    const auto cubic_header = read_value<progpu_native_scene_header>(
        stream, 0U);
    bool found_cubic = false;
    for (std::uint32_t index = 0U;
         index < cubic_header.command_count;
         ++index) {
        const auto record = read_value<progpu_native_scene_command>(
            stream,
            cubic_header.command_offset +
                index * sizeof(progpu_native_scene_command));
        if (record.kind == PROGPU_NATIVE_SCENE_COMMAND_DRAW_IMAGE) {
            const auto image = read_value<progpu_native_scene_image_draw>(
                stream, record.payload_offset);
            found_cubic = image.sampling ==
                PROGPU_NATIVE_IMAGE_SAMPLING_CUBIC;
        }
    }
    PROGPU_REQUIRE(found_cubic);

    std::vector<std::byte> delete_bitmap;
    append_command(
        delete_bitmap,
        command::channel_delete_resource,
        bitmap,
        95U);
    PROGPU_REQUIRE(state.apply(delete_bitmap) == status::invalid_graph);
    PROGPU_REQUIRE(
        state.set_bitmap_source_rgba8(bitmap, 2U, 2U, 7U, pixels) ==
        status::invalid_argument);
    PROGPU_REQUIRE(
        state.set_bitmap_source_rgba8(target, 2U, 2U, 8U, pixels) ==
        status::invalid_handle);
    return true;
}

bool retained_drawing_image_maps_vector_content_into_destination() {
    constexpr std::uint32_t visual = 1U;
    constexpr std::uint32_t content = 2U;
    constexpr std::uint32_t target = 3U;
    constexpr std::uint32_t brush = 4U;
    constexpr std::uint32_t geometry = 5U;
    constexpr std::uint32_t geometry_drawing = 6U;
    constexpr std::uint32_t drawing_image = 7U;
    constexpr std::uint32_t image_drawing = 8U;

    std::vector<std::byte> batch;
    append_create(batch, visual, 39U);
    append_create(batch, content, 43U);
    append_create(batch, target, 47U);
    append_create(batch, brush, 75U);
    append_create(batch, geometry, 69U);
    append_create(batch, geometry_drawing, 87U);
    append_create(batch, drawing_image, 59U);
    append_create(batch, image_drawing, 89U);
    append_command(batch, command::visual_create, visual);
    append_command(batch, command::visual_set_content, visual, content);
    append_command(
        batch,
        command::solid_color_brush,
        brush,
        1.0,
        progpu_native_color{0.1F, 0.3F, 0.8F, 1.0F},
        0U,
        0U,
        0U,
        0U);
    append_command(
        batch,
        command::rectangle_geometry,
        geometry,
        0.0,
        0.0,
        10.0,
        20.0,
        20.0,
        10.0,
        0U,
        0U,
        0U,
        0U);
    append_command(
        batch,
        command::geometry_drawing,
        geometry_drawing,
        brush,
        0U,
        geometry);
    append_command(
        batch,
        command::drawing_image,
        drawing_image,
        geometry_drawing);
    append_command(
        batch,
        command::image_drawing,
        image_drawing,
        2.0,
        4.0,
        40.0,
        20.0,
        drawing_image,
        0U);
    std::vector<std::byte> nested;
    append_command(nested, command::draw_drawing, image_drawing, 0U);
    append_render_data(batch, content, nested);
    append_command(
        batch,
        command::generic_target_create,
        target,
        std::uint64_t{0U},
        std::uint64_t{0U},
        64U,
        64U,
        0U);
    append_command(batch, command::target_set_root, target, visual);

    channel state;
    PROGPU_REQUIRE(state.apply(batch) == status::success);
    std::vector<std::byte> stream;
    progpu::native::mil::scene_metrics metrics{};
    PROGPU_REQUIRE(
        state.build_scene(target, 7006U, 1U, stream, &metrics) ==
        status::unsupported_command);
    PROGPU_REQUIRE(
        state.set_drawing_image_bounds(
            drawing_image, 10.0, 20.0, 20.0, 10.0) ==
        status::success);
    PROGPU_REQUIRE(state.resource_generation(drawing_image) == 3U);
    PROGPU_REQUIRE(
        state.build_scene(target, 7006U, 2U, stream, &metrics) ==
        status::success);
    PROGPU_REQUIRE(metrics.rectangle_count == 1U);
    PROGPU_REQUIRE(metrics.brush_count == 1U);

    const auto header = read_value<progpu_native_scene_header>(stream, 0U);
    bool found_mapping = false;
    for (std::uint32_t index = 0U; index < header.resource_count; ++index) {
        const auto resource = read_value<progpu_native_scene_resource>(
            stream,
            header.resource_offset +
                index * sizeof(progpu_native_scene_resource));
        if (resource.kind != PROGPU_NATIVE_SCENE_RESOURCE_STATE) {
            continue;
        }
        const auto scene_state = read_value<progpu_native_scene_state>(
            stream, resource.payload_offset);
        if (scene_state.transform.m11 == 2.0F &&
            scene_state.transform.m22 == 2.0F &&
            scene_state.transform.m31 == -18.0F &&
            scene_state.transform.m32 == -36.0F &&
            (scene_state.flags & PROGPU_NATIVE_SCENE_STATE_CLIP_RECT) != 0U &&
            scene_state.clip_rect.x == 2.0F &&
            scene_state.clip_rect.y == 4.0F &&
            scene_state.clip_rect.width == 40.0F &&
            scene_state.clip_rect.height == 20.0F) {
            found_mapping = true;
        }
    }
    PROGPU_REQUIRE(found_mapping);

    constexpr std::uint32_t transform = 9U;
    std::vector<std::byte> affine_update;
    append_create(affine_update, transform, 66U);
    append_command(
        affine_update,
        command::matrix_transform,
        transform,
        1.0,
        0.25,
        0.5,
        1.0,
        3.0,
        2.0,
        0U);
    append_command(
        affine_update,
        command::visual_set_transform,
        visual,
        transform);
    PROGPU_REQUIRE(state.apply(affine_update) == status::success);
    PROGPU_REQUIRE(
        state.build_scene(target, 7006U, 3U, stream, &metrics) ==
        status::success);
    const auto affine_header = read_value<progpu_native_scene_header>(
        stream, 0U);
    bool found_vector_clip = false;
    for (std::uint32_t index = 0U;
         index < affine_header.resource_count;
         ++index) {
        const auto resource = read_value<progpu_native_scene_resource>(
            stream,
            affine_header.resource_offset +
                index * sizeof(progpu_native_scene_resource));
        if (resource.kind != PROGPU_NATIVE_SCENE_RESOURCE_STATE) {
            continue;
        }
        const auto scene_state = read_value<progpu_native_scene_state>(
            stream, resource.payload_offset);
        if ((scene_state.flags & PROGPU_NATIVE_SCENE_STATE_MASK) != 0U &&
            scene_state.mask_resource_index !=
                PROGPU_NATIVE_SCENE_NO_INDEX) {
            found_vector_clip = true;
        }
    }
    PROGPU_REQUIRE(found_vector_clip);

    std::vector<std::byte> delete_dependency;
    append_command(
        delete_dependency,
        command::channel_delete_resource,
        geometry_drawing,
        87U);
    PROGPU_REQUIRE(state.apply(delete_dependency) == status::invalid_graph);
    delete_dependency.clear();
    append_command(
        delete_dependency,
        command::channel_delete_resource,
        drawing_image,
        59U);
    PROGPU_REQUIRE(state.apply(delete_dependency) == status::invalid_graph);
    PROGPU_REQUIRE(
        state.set_drawing_image_bounds(
            drawing_image, 0.0, 0.0, 0.0, 1.0) ==
        status::invalid_argument);
    PROGPU_REQUIRE(
        state.set_drawing_image_bounds(
            target, 0.0, 0.0, 1.0, 1.0) ==
        status::invalid_handle);
    return true;
}

bool retained_glyph_run_drawing_uses_pointer_free_sfnt_sideband() {
    constexpr std::uint32_t visual = 1U;
    constexpr std::uint32_t content = 2U;
    constexpr std::uint32_t target = 3U;
    constexpr std::uint32_t brush = 4U;
    constexpr std::uint32_t glyph_run = 5U;
    constexpr std::uint32_t drawing = 6U;

    const std::vector<std::byte> font_bytes = load_inter_test_font();
    progpu::native::text::sfnt_font_view font{};
    progpu::native::text::font_error font_error =
        progpu::native::text::font_error::none;
    PROGPU_REQUIRE(progpu::native::text::sfnt_font_view::try_create(
        font_bytes, 0U, font, &font_error));
    std::uint16_t glyph_index = 0U;
    PROGPU_REQUIRE(font.try_get_glyph_index('A', glyph_index));
    PROGPU_REQUIRE(glyph_index != 0U);

    std::vector<std::byte> batch;
    append_create(batch, visual, 39U);
    append_create(batch, content, 43U);
    append_create(batch, target, 47U);
    append_create(batch, brush, 75U);
    append_create(batch, drawing, 88U);
    append_command(batch, command::visual_create, visual);
    append_command(batch, command::visual_set_content, visual, content);
    append_command(
        batch,
        command::solid_color_brush,
        brush,
        0.75,
        progpu_native_color{0.2F, 0.4F, 0.8F, 1.0F},
        0U,
        0U,
        0U,
        0U);
    const std::array glyph_indices{glyph_index};
    const std::array advances{28.0F};
    const std::array offsets{progpu_native_point{2.0F, -1.0F}};
    append_glyph_run_create(
        batch,
        glyph_run,
        10.0F,
        38.0F,
        24.0F,
        glyph_indices,
        advances,
        offsets,
        10.0,
        10.0,
        36.0,
        36.0);
    append_command(
        batch,
        command::glyph_run_drawing,
        drawing,
        glyph_run,
        brush);
    std::vector<std::byte> nested;
    append_command(nested, command::draw_drawing, drawing, 0U);
    append_command(nested, command::draw_glyph_run, brush, glyph_run);
    append_render_data(batch, content, nested);
    append_command(
        batch,
        command::generic_target_create,
        target,
        std::uint64_t{0U},
        std::uint64_t{0U},
        64U,
        64U,
        0U);
    append_command(batch, command::target_set_root, target, visual);

    channel state;
    batch_metrics applied{};
    PROGPU_REQUIRE(state.apply(batch, &applied) == status::success);
    PROGPU_REQUIRE(state.resource_type(glyph_run) == 42U);
    PROGPU_REQUIRE(applied.created_resource_count == 6U);
    std::vector<std::byte> stream;
    progpu::native::mil::scene_metrics metrics{};
    PROGPU_REQUIRE(
        state.build_scene(target, 7006U, 1U, stream, &metrics) ==
        status::invalid_handle);
    PROGPU_REQUIRE(
        state.set_glyph_run_font_sfnt(
            glyph_run, 0U, 0x03U, font_bytes) == status::success);
    PROGPU_REQUIRE(state.resource_generation(glyph_run) == 2U);
    PROGPU_REQUIRE(
        state.build_scene(target, 7006U, 2U, stream, &metrics) ==
        status::success);

    const auto header = read_value<progpu_native_scene_header>(stream, 0U);
    std::uint32_t glyph_draw_count = 0U;
    for (std::uint32_t index = 0U; index < header.command_count; ++index) {
        const auto record = read_value<progpu_native_scene_command>(
            stream,
            header.command_offset +
                index * sizeof(progpu_native_scene_command));
        if (record.kind != PROGPU_NATIVE_SCENE_COMMAND_DRAW_GLYPH_RUN) {
            continue;
        }
        const auto resource = read_value<progpu_native_scene_resource>(
            stream,
            header.resource_offset +
                record.resource_index * sizeof(progpu_native_scene_resource));
        const auto draw = read_value<progpu_native_scene_glyph_draw>(
            stream, record.payload_offset);
        const auto positioned = read_value<progpu_native_positioned_glyph>(
            stream,
            record.payload_offset + sizeof(progpu_native_scene_glyph_draw));
        PROGPU_REQUIRE(
            resource.kind == PROGPU_NATIVE_SCENE_RESOURCE_GLYPH_RUN);
        PROGPU_REQUIRE(resource.payload_size ==
            4U * sizeof(progpu_native_scene_glyph_outline));
        PROGPU_REQUIRE(draw.glyph_count == 2U);
        PROGPU_REQUIRE(positioned.position.x == 12.0F);
        PROGPU_REQUIRE(positioned.position.y == 37.0F);
        PROGPU_REQUIRE(positioned.italic_skew == 0.22F);
        PROGPU_REQUIRE(record.bounds_x == 10.0F);
        PROGPU_REQUIRE(record.bounds_y == 10.0F);
        PROGPU_REQUIRE(record.bounds_width == 36.0F);
        PROGPU_REQUIRE(record.bounds_height == 36.0F);
        ++glyph_draw_count;
    }
    PROGPU_REQUIRE(glyph_draw_count == 2U);
    PROGPU_REQUIRE(scene_contains_text_style_mode(
        stream, PROGPU_NATIVE_SCENE_TEXT_GRAYSCALE));

    constexpr std::uint32_t clear_type_group = 7U;
    std::vector<std::byte> clear_type_batch;
    append_create(clear_type_batch, clear_type_group, 91U);
    append_command(
        clear_type_batch,
        command::drawing_group,
        clear_type_group,
        1.0,
        4U,
        0U,
        0U,
        0U,
        0U,
        0U,
        0U,
        0U,
        1U,
        drawing);
    std::vector<std::byte> clear_type_nested;
    append_command(
        clear_type_nested,
        command::draw_drawing,
        clear_type_group,
        0U);
    append_render_data(clear_type_batch, content, clear_type_nested);
    PROGPU_REQUIRE(state.apply(clear_type_batch) == status::success);
    PROGPU_REQUIRE(
        state.build_scene(target, 7006U, 3U, stream, &metrics) ==
        status::success);
    PROGPU_REQUIRE(scene_contains_text_style_mode(
        stream, PROGPU_NATIVE_SCENE_TEXT_CLEARTYPE));

    std::vector<std::byte> visual_clear_type_batch;
    append_command(
        visual_clear_type_batch,
        command::drawing_group,
        clear_type_group,
        1.0,
        4U,
        0U,
        0U,
        0U,
        0U,
        0U,
        0U,
        0U,
        0U,
        drawing);
    append_command(
        visual_clear_type_batch,
        command::visual_set_render_options,
        visual,
        0x08U,
        0U,
        0U,
        0U,
        1U,
        0U,
        0U);
    PROGPU_REQUIRE(state.apply(visual_clear_type_batch) == status::success);
    PROGPU_REQUIRE(
        state.build_scene(target, 7006U, 4U, stream, &metrics) ==
        status::success);
    PROGPU_REQUIRE(scene_contains_text_style_mode(
        stream, PROGPU_NATIVE_SCENE_TEXT_CLEARTYPE));

    std::vector<std::byte> fixed_aliased_batch;
    append_command(
        fixed_aliased_batch,
        command::visual_set_offset,
        visual,
        0.375,
        0.4);
    append_command(
        fixed_aliased_batch,
        command::visual_set_render_options,
        visual,
        0x30U,
        0U,
        0U,
        0U,
        0U,
        1U,
        1U);
    PROGPU_REQUIRE(state.apply(fixed_aliased_batch) == status::success);
    PROGPU_REQUIRE(
        state.build_scene(target, 7006U, 5U, stream, &metrics) ==
        status::success);
    PROGPU_REQUIRE(scene_contains_text_style_mode(
        stream, PROGPU_NATIVE_SCENE_TEXT_ALIASED));
    bool found_fixed_position = false;
    const auto fixed_header = read_value<progpu_native_scene_header>(
        stream, 0U);
    for (std::uint32_t index = 0U;
         index < fixed_header.command_count;
         ++index) {
        const auto record = read_value<progpu_native_scene_command>(
            stream,
            fixed_header.command_offset +
                index * sizeof(progpu_native_scene_command));
        if (record.kind != PROGPU_NATIVE_SCENE_COMMAND_DRAW_GLYPH_RUN) {
            continue;
        }
        const auto positioned = read_value<progpu_native_positioned_glyph>(
            stream,
            record.payload_offset + sizeof(progpu_native_scene_glyph_draw));
        PROGPU_REQUIRE(positioned.position.x == 11.625F);
        PROGPU_REQUIRE(positioned.position.y == 36.6F);
        PROGPU_REQUIRE(positioned.outline_index % 4U == 2U);
        found_fixed_position = true;
    }
    PROGPU_REQUIRE(found_fixed_position);

    std::vector<std::byte> animated_batch;
    append_command(
        animated_batch,
        command::visual_set_render_options,
        visual,
        0x30U,
        0U,
        0U,
        0U,
        0U,
        2U,
        2U);
    PROGPU_REQUIRE(state.apply(animated_batch) == status::success);
    PROGPU_REQUIRE(
        state.build_scene(target, 7006U, 6U, stream, &metrics) ==
        status::success);
    PROGPU_REQUIRE(scene_contains_text_style_mode(
        stream, PROGPU_NATIVE_SCENE_TEXT_GRAYSCALE));
    bool found_animated_position = false;
    const auto animated_header = read_value<progpu_native_scene_header>(
        stream, 0U);
    for (std::uint32_t index = 0U;
         index < animated_header.command_count;
         ++index) {
        const auto record = read_value<progpu_native_scene_command>(
            stream,
            animated_header.command_offset +
                index * sizeof(progpu_native_scene_command));
        if (record.kind != PROGPU_NATIVE_SCENE_COMMAND_DRAW_GLYPH_RUN) {
            continue;
        }
        const auto positioned = read_value<progpu_native_positioned_glyph>(
            stream,
            record.payload_offset + sizeof(progpu_native_scene_glyph_draw));
        PROGPU_REQUIRE(positioned.position.x == 12.0F);
        PROGPU_REQUIRE(positioned.position.y == 37.0F);
        PROGPU_REQUIRE(positioned.outline_index % 4U == 0U);
        found_animated_position = true;
    }
    PROGPU_REQUIRE(found_animated_position);

    std::vector<std::byte> delete_glyph;
    append_command(
        delete_glyph,
        command::channel_delete_resource,
        glyph_run,
        42U);
    PROGPU_REQUIRE(state.apply(delete_glyph) == status::invalid_graph);
    PROGPU_REQUIRE(
        state.set_glyph_run_font_sfnt(target, 0U, 0U, font_bytes) ==
        status::invalid_handle);
    PROGPU_REQUIRE(
        state.set_glyph_run_font_sfnt(glyph_run, 0U, 0x04U, font_bytes) ==
        status::invalid_argument);
    return true;
}

bool retained_geometry_group_compiles_to_one_semantic_path() {
    constexpr std::uint32_t visual = 1U;
    constexpr std::uint32_t content = 2U;
    constexpr std::uint32_t target = 3U;
    constexpr std::uint32_t brush = 4U;
    constexpr std::uint32_t transform = 5U;
    constexpr std::uint32_t path_a = 6U;
    constexpr std::uint32_t path_b = 7U;
    constexpr std::uint32_t group = 8U;
    constexpr std::uint32_t nested_group = 9U;
    constexpr std::uint32_t combined = 10U;
    constexpr std::uint32_t child_transform = 11U;
    constexpr std::uint32_t rectangle = 12U;
    constexpr std::uint32_t ellipse = 13U;
    constexpr std::uint32_t line = 14U;
    constexpr std::uint32_t rounded_rectangle = 15U;
    constexpr std::uint32_t same_fill_group = 16U;
    constexpr std::uint32_t different_fill_group = 17U;
    constexpr std::uint32_t nested_combined = 18U;
    constexpr std::uint32_t arc_transform = 19U;
    constexpr std::uint32_t singular_transform = 20U;
    constexpr std::uint32_t pen = 21U;

    std::vector<std::byte> batch;
    append_create(batch, visual, 39U);
    append_create(batch, content, 43U);
    append_create(batch, target, 47U);
    append_create(batch, brush, 75U);
    append_create(batch, transform, 66U);
    append_create(batch, path_a, 73U);
    append_create(batch, path_b, 73U);
    append_create(batch, group, 71U);
    append_create(batch, nested_group, 71U);
    append_create(batch, combined, 72U);
    append_create(batch, child_transform, 66U);
    append_create(batch, rectangle, 69U);
    append_create(batch, ellipse, 70U);
    append_create(batch, line, 68U);
    append_create(batch, rounded_rectangle, 69U);
    append_create(batch, same_fill_group, 71U);
    append_create(batch, different_fill_group, 71U);
    append_create(batch, nested_combined, 72U);
    append_create(batch, arc_transform, 66U);
    append_create(batch, singular_transform, 66U);
    append_create(batch, pen, 85U);
    append_command(batch, command::visual_create, visual);
    append_command(batch, command::visual_set_content, visual, content);
    append_command(
        batch,
        command::solid_color_brush,
        brush,
        1.0,
        progpu_native_color{0.75F, 0.25F, 0.5F, 1.0F},
        0U,
        0U,
        0U,
        0U);
    append_command(
        batch,
        command::pen,
        pen,
        2.0,
        4.0,
        brush,
        0U,
        0U,
        0U,
        0U,
        0U,
        0U);
    append_command(
        batch,
        command::matrix_transform,
        transform,
        1.5,
        0.0,
        0.0,
        1.5,
        2.0,
        3.0,
        0U);
    append_command(
        batch,
        command::matrix_transform,
        child_transform,
        1.0,
        0.0,
        0.0,
        1.0,
        20.0,
        5.0,
        0U);
    append_command(
        batch,
        command::matrix_transform,
        arc_transform,
        -1.25,
        0.5,
        0.25,
        0.75,
        3.0,
        -2.0,
        0U);
    append_command(
        batch,
        command::matrix_transform,
        singular_transform,
        1.0,
        0.0,
        0.0,
        0.0,
        0.0,
        0.0,
        0U);
    append_command(
        batch,
        command::rectangle_geometry,
        rectangle,
        0.0,
        0.0,
        0.0,
        0.0,
        4.0,
        3.0,
        child_transform,
        0U,
        0U,
        0U);
    append_command(
        batch,
        command::ellipse_geometry,
        ellipse,
        2.0,
        1.0,
        30.0,
        6.0,
        0U,
        0U,
        0U,
        0U);
    append_command(
        batch,
        command::line_geometry,
        line,
        40.0,
        4.0,
        44.0,
        8.0,
        child_transform,
        0U,
        0U);
    append_command(
        batch,
        command::rectangle_geometry,
        rounded_rectangle,
        3.0,
        2.0,
        16.0,
        0.0,
        10.0,
        8.0,
        0U,
        0U,
        0U,
        0U);
    const auto figures_a = make_rectangle_path_figures(1.0, 2.0, 9.0, 10.0);
    append_path_geometry(batch, path_a, 0U, 1U, figures_a);
    append_path_geometry(
        batch,
        path_b,
        child_transform,
        1U,
        make_curve_path_figures());
    const std::array same_fill_children{path_a};
    append_geometry_group(
        batch,
        same_fill_group,
        child_transform,
        0U,
        same_fill_children);
    const std::array different_fill_children{path_a};
    append_geometry_group(
        batch,
        different_fill_group,
        0U,
        1U,
        different_fill_children);
    const std::array children{
        path_a,
        path_b,
        rectangle,
        ellipse,
        line,
        rounded_rectangle,
        same_fill_group};
    append_geometry_group(batch, group, transform, 0U, children);
    append_command(
        batch,
        command::combined_geometry,
        combined,
        transform,
        3U,
        rectangle,
        rounded_rectangle);
    std::vector<std::byte> nested;
    append_command(
        nested,
        command::draw_geometry,
        brush,
        0U,
        group,
        0U);
    append_command(
        nested,
        command::draw_geometry,
        brush,
        0U,
        combined,
        0U);
    append_render_data(batch, content, nested);
    append_command(
        batch,
        command::generic_target_create,
        target,
        std::uint64_t{0U},
        std::uint64_t{0U},
        64U,
        64U,
        0U);
    append_command(batch, command::target_set_root, target, visual);

    channel state;
    PROGPU_REQUIRE(state.apply(batch) == status::success);
    std::vector<std::byte> stream;
    PROGPU_REQUIRE(
        state.build_scene(target, 7003U, 1U, stream) == status::success);
    const auto header = read_value<progpu_native_scene_header>(stream, 0U);
    bool found_group_path = false;
    bool found_combined_path = false;
    for (std::uint32_t index = 0U; index < header.resource_count; ++index) {
        const auto resource = read_value<progpu_native_scene_resource>(
            stream,
            header.resource_offset +
                index * sizeof(progpu_native_scene_resource));
        if (resource.kind != PROGPU_NATIVE_SCENE_RESOURCE_PATH_BATCH) {
            continue;
        }
        const auto path = read_value<progpu_native_scene_path_fill>(
            stream,
            resource.payload_offset);
        PROGPU_REQUIRE(path.transform.m11 == 1.5F);
        PROGPU_REQUIRE(path.transform.m22 == 1.5F);
        PROGPU_REQUIRE(path.transform.m31 == 2.0F);
        PROGPU_REQUIRE(path.transform.m32 == 3.0F);
        if (path.segment_count == 28U) {
            PROGPU_REQUIRE(path.boolean_node_count == 11U);
            PROGPU_REQUIRE(path.sample_grid == 8U);
            PROGPU_REQUIRE(path.segment_count == 28U);
            PROGPU_REQUIRE(path.min_x == 1.0F && path.min_y == 0.0F);
            PROGPU_REQUIRE(path.max_x == 35.0F && path.max_y == 15.0F);
            PROGPU_REQUIRE(
                path.fill_rule == PROGPU_NATIVE_FILL_RULE_EVEN_ODD);
            const std::size_t group_boolean_offset =
                resource.auxiliary_offset +
                28U * sizeof(progpu_native_path_segment);
            const auto first_group_leaf =
                read_value<progpu_native_scene_path_boolean_node>(
                    stream,
                    group_boolean_offset);
            const auto second_group_leaf =
                read_value<progpu_native_scene_path_boolean_node>(
                    stream,
                    group_boolean_offset + sizeof(first_group_leaf));
            const auto first_group_xor =
                read_value<progpu_native_scene_path_boolean_node>(
                    stream,
                    group_boolean_offset + 2U * sizeof(first_group_leaf));
            PROGPU_REQUIRE(
                first_group_leaf.kind == PROGPU_NATIVE_PATH_BOOLEAN_LEAF &&
                first_group_leaf.segment_offset == 0U &&
                first_group_leaf.segment_count == 4U &&
                first_group_leaf.fill_rule ==
                    PROGPU_NATIVE_FILL_RULE_EVEN_ODD);
            PROGPU_REQUIRE(
                second_group_leaf.kind == PROGPU_NATIVE_PATH_BOOLEAN_LEAF &&
                second_group_leaf.segment_offset == 4U &&
                second_group_leaf.segment_count == 4U &&
                second_group_leaf.fill_rule ==
                    PROGPU_NATIVE_FILL_RULE_EVEN_ODD);
            PROGPU_REQUIRE(
                first_group_xor.kind == PROGPU_NATIVE_PATH_BOOLEAN_XOR);
            const auto rectangle_line =
                read_value<progpu_native_path_segment>(
                    stream,
                    resource.auxiliary_offset +
                        8U * sizeof(progpu_native_path_segment));
            const auto ellipse_arc =
                read_value<progpu_native_path_segment>(
                    stream,
                    resource.auxiliary_offset +
                        12U * sizeof(progpu_native_path_segment));
            const auto rounded_arc =
                read_value<progpu_native_path_segment>(
                    stream,
                    resource.auxiliary_offset +
                        16U * sizeof(progpu_native_path_segment));
            const auto nested_line =
                read_value<progpu_native_path_segment>(
                    stream,
                    resource.auxiliary_offset +
                        24U * sizeof(progpu_native_path_segment));
            PROGPU_REQUIRE(
                rectangle_line.kind == PROGPU_NATIVE_PATH_SEGMENT_LINE &&
                rectangle_line.p0.x == 20.0F &&
                rectangle_line.p0.y == 5.0F &&
                rectangle_line.p1.x == 24.0F &&
                rectangle_line.p1.y == 5.0F);
            PROGPU_REQUIRE(
                ellipse_arc.kind == PROGPU_NATIVE_PATH_SEGMENT_ARC &&
                ellipse_arc.p0.x == 32.0F &&
                ellipse_arc.p0.y == 6.0F &&
                ellipse_arc.p1.x == 30.0F &&
                ellipse_arc.p1.y == 7.0F &&
                ellipse_arc.p2.x == 30.0F &&
                ellipse_arc.p2.y == 6.0F &&
                ellipse_arc.p3.x == 2.0F &&
                ellipse_arc.p3.y == 1.0F);
            PROGPU_REQUIRE(
                rounded_arc.kind == PROGPU_NATIVE_PATH_SEGMENT_ARC &&
                rounded_arc.p0.x == 16.0F &&
                rounded_arc.p0.y == 2.0F &&
                rounded_arc.p1.x == 19.0F &&
                rounded_arc.p1.y == 0.0F &&
                rounded_arc.p2.x == 19.0F &&
                rounded_arc.p2.y == 2.0F &&
                rounded_arc.p3.x == 3.0F &&
                rounded_arc.p3.y == 2.0F);
            PROGPU_REQUIRE(
                nested_line.kind == PROGPU_NATIVE_PATH_SEGMENT_LINE &&
                nested_line.p0.x == 21.0F && nested_line.p0.y == 7.0F &&
                nested_line.p1.x == 29.0F && nested_line.p1.y == 7.0F);
            found_group_path = true;
            continue;
        }
        PROGPU_REQUIRE(path.segment_count == 12U);
        PROGPU_REQUIRE(path.min_x == 16.0F && path.min_y == 0.0F);
        PROGPU_REQUIRE(path.max_x == 26.0F && path.max_y == 8.0F);
        PROGPU_REQUIRE(path.boolean_node_count == 3U);
        const std::size_t boolean_offset =
            resource.auxiliary_offset +
            12U * sizeof(progpu_native_path_segment);
        const auto leaf_a =
            read_value<progpu_native_scene_path_boolean_node>(
                stream,
                boolean_offset);
        const auto leaf_b =
            read_value<progpu_native_scene_path_boolean_node>(
                stream,
                boolean_offset + sizeof(leaf_a));
        const auto operation =
            read_value<progpu_native_scene_path_boolean_node>(
                stream,
                boolean_offset + 2U * sizeof(leaf_a));
        PROGPU_REQUIRE(
            leaf_a.kind == PROGPU_NATIVE_PATH_BOOLEAN_LEAF &&
            leaf_a.segment_offset == 0U && leaf_a.segment_count == 4U);
        PROGPU_REQUIRE(
            leaf_b.kind == PROGPU_NATIVE_PATH_BOOLEAN_LEAF &&
            leaf_b.segment_offset == 4U && leaf_b.segment_count == 8U);
        PROGPU_REQUIRE(
            leaf_a.fill_rule == PROGPU_NATIVE_FILL_RULE_NON_ZERO &&
            leaf_a.min_x == 20.0F && leaf_a.min_y == 5.0F &&
            leaf_a.max_x == 24.0F && leaf_a.max_y == 8.0F);
        PROGPU_REQUIRE(
            leaf_b.fill_rule == PROGPU_NATIVE_FILL_RULE_NON_ZERO &&
            leaf_b.min_x == 16.0F && leaf_b.min_y == 0.0F &&
            leaf_b.max_x == 26.0F && leaf_b.max_y == 8.0F);
        PROGPU_REQUIRE(
            operation.kind == PROGPU_NATIVE_PATH_BOOLEAN_DIFFERENCE);
        found_combined_path = true;
    }
    PROGPU_REQUIRE(found_group_path);
    PROGPU_REQUIRE(found_combined_path);

    std::vector<std::byte> path_operand_update;
    append_command(
        path_operand_update,
        command::combined_geometry,
        combined,
        transform,
        3U,
        path_a,
        path_b);
    PROGPU_REQUIRE(state.apply(path_operand_update) == status::success);
    PROGPU_REQUIRE(
        state.build_scene(target, 7003U, 2U, stream) == status::success);
    const auto path_operand_header =
        read_value<progpu_native_scene_header>(stream, 0U);
    bool found_path_operands = false;
    for (std::uint32_t index = 0U;
         index < path_operand_header.resource_count;
         ++index) {
        const auto resource = read_value<progpu_native_scene_resource>(
            stream,
            path_operand_header.resource_offset +
                index * sizeof(progpu_native_scene_resource));
        if (resource.kind != PROGPU_NATIVE_SCENE_RESOURCE_PATH_BATCH) {
            continue;
        }
        const auto path = read_value<progpu_native_scene_path_fill>(
            stream,
            resource.payload_offset);
        if (path.boolean_node_count == 3U) {
            PROGPU_REQUIRE(path.segment_count == 8U);
            PROGPU_REQUIRE(path.min_x == 1.0F && path.min_y == 2.0F);
            PROGPU_REQUIRE(path.max_x == 35.0F && path.max_y == 13.0F);
            const auto transformed_line =
                read_value<progpu_native_path_segment>(
                    stream,
                    resource.auxiliary_offset +
                        4U * sizeof(progpu_native_path_segment));
            const auto transformed_quadratic =
                read_value<progpu_native_path_segment>(
                    stream,
                    resource.auxiliary_offset +
                        5U * sizeof(progpu_native_path_segment));
            const auto transformed_cubic =
                read_value<progpu_native_path_segment>(
                    stream,
                    resource.auxiliary_offset +
                        6U * sizeof(progpu_native_path_segment));
            PROGPU_REQUIRE(
                transformed_line.kind == PROGPU_NATIVE_PATH_SEGMENT_LINE &&
                transformed_line.p0.x == 26.0F &&
                transformed_line.p0.y == 9.0F &&
                transformed_line.p1.x == 28.0F &&
                transformed_line.p1.y == 9.0F);
            PROGPU_REQUIRE(
                transformed_quadratic.kind ==
                    PROGPU_NATIVE_PATH_SEGMENT_QUADRATIC &&
                transformed_quadratic.p1.x == 30.0F &&
                transformed_quadratic.p1.y == 7.0F &&
                transformed_quadratic.p2.x == 32.0F &&
                transformed_quadratic.p2.y == 11.0F);
            PROGPU_REQUIRE(
                transformed_cubic.kind ==
                    PROGPU_NATIVE_PATH_SEGMENT_CUBIC &&
                transformed_cubic.p1.x == 33.0F &&
                transformed_cubic.p1.y == 13.0F &&
                transformed_cubic.p2.x == 34.0F &&
                transformed_cubic.p2.y == 8.0F &&
                transformed_cubic.p3.x == 35.0F &&
                transformed_cubic.p3.y == 12.0F);
            found_path_operands = true;
        }
    }
    PROGPU_REQUIRE(found_path_operands);

    std::vector<std::byte> group_operand_update;
    append_command(
        group_operand_update,
        command::combined_geometry,
        combined,
        transform,
        3U,
        same_fill_group,
        rounded_rectangle);
    PROGPU_REQUIRE(state.apply(group_operand_update) == status::success);
    PROGPU_REQUIRE(
        state.build_scene(target, 7003U, 3U, stream) == status::success);
    const auto group_operand_header =
        read_value<progpu_native_scene_header>(stream, 0U);
    bool found_group_operand = false;
    for (std::uint32_t index = 0U;
         index < group_operand_header.resource_count;
         ++index) {
        const auto resource = read_value<progpu_native_scene_resource>(
            stream,
            group_operand_header.resource_offset +
                index * sizeof(progpu_native_scene_resource));
        if (resource.kind != PROGPU_NATIVE_SCENE_RESOURCE_PATH_BATCH) {
            continue;
        }
        const auto path = read_value<progpu_native_scene_path_fill>(
            stream,
            resource.payload_offset);
        if (path.boolean_node_count != 3U) {
            continue;
        }
        PROGPU_REQUIRE(path.segment_count == 12U);
        const std::size_t boolean_offset =
            resource.auxiliary_offset +
            12U * sizeof(progpu_native_path_segment);
        const auto group_leaf =
            read_value<progpu_native_scene_path_boolean_node>(
                stream,
                boolean_offset);
        PROGPU_REQUIRE(
            group_leaf.kind == PROGPU_NATIVE_PATH_BOOLEAN_LEAF &&
            group_leaf.segment_offset == 0U &&
            group_leaf.segment_count == 4U &&
            group_leaf.fill_rule == PROGPU_NATIVE_FILL_RULE_EVEN_ODD &&
            group_leaf.min_x == 21.0F && group_leaf.min_y == 7.0F &&
            group_leaf.max_x == 29.0F && group_leaf.max_y == 15.0F);
        found_group_operand = true;
    }
    PROGPU_REQUIRE(found_group_operand);

    std::vector<std::byte> recursive_combined_update;
    append_command(
        recursive_combined_update,
        command::combined_geometry,
        nested_combined,
        child_transform,
        1U,
        same_fill_group,
        rectangle);
    append_command(
        recursive_combined_update,
        command::combined_geometry,
        combined,
        transform,
        3U,
        nested_combined,
        rounded_rectangle);
    PROGPU_REQUIRE(
        state.apply(recursive_combined_update) == status::success);
    PROGPU_REQUIRE(
        state.build_scene(target, 7003U, 4U, stream) == status::success);
    const auto recursive_combined_header =
        read_value<progpu_native_scene_header>(stream, 0U);
    bool found_recursive_combined = false;
    for (std::uint32_t index = 0U;
         index < recursive_combined_header.resource_count;
         ++index) {
        const auto resource = read_value<progpu_native_scene_resource>(
            stream,
            recursive_combined_header.resource_offset +
                index * sizeof(progpu_native_scene_resource));
        if (resource.kind != PROGPU_NATIVE_SCENE_RESOURCE_PATH_BATCH) {
            continue;
        }
        const auto path = read_value<progpu_native_scene_path_fill>(
            stream,
            resource.payload_offset);
        if (path.boolean_node_count != 5U) {
            continue;
        }
        PROGPU_REQUIRE(path.segment_count == 16U);
        PROGPU_REQUIRE(path.min_x == 16.0F && path.min_y == 0.0F);
        PROGPU_REQUIRE(path.max_x == 49.0F && path.max_y == 20.0F);
        const std::size_t boolean_offset =
            resource.auxiliary_offset +
            16U * sizeof(progpu_native_path_segment);
        const auto nested_group_leaf =
            read_value<progpu_native_scene_path_boolean_node>(
                stream,
                boolean_offset);
        const auto nested_rectangle_leaf =
            read_value<progpu_native_scene_path_boolean_node>(
                stream,
                boolean_offset + sizeof(nested_group_leaf));
        const auto nested_operation =
            read_value<progpu_native_scene_path_boolean_node>(
                stream,
                boolean_offset + 2U * sizeof(nested_group_leaf));
        const auto outer_leaf =
            read_value<progpu_native_scene_path_boolean_node>(
                stream,
                boolean_offset + 3U * sizeof(nested_group_leaf));
        const auto outer_operation =
            read_value<progpu_native_scene_path_boolean_node>(
                stream,
                boolean_offset + 4U * sizeof(nested_group_leaf));
        PROGPU_REQUIRE(
            nested_group_leaf.kind == PROGPU_NATIVE_PATH_BOOLEAN_LEAF &&
            nested_group_leaf.segment_offset == 0U &&
            nested_group_leaf.segment_count == 4U &&
            nested_group_leaf.fill_rule ==
                PROGPU_NATIVE_FILL_RULE_EVEN_ODD &&
            nested_group_leaf.min_x == 41.0F &&
            nested_group_leaf.min_y == 12.0F &&
            nested_group_leaf.max_x == 49.0F &&
            nested_group_leaf.max_y == 20.0F);
        PROGPU_REQUIRE(
            nested_rectangle_leaf.kind ==
                PROGPU_NATIVE_PATH_BOOLEAN_LEAF &&
            nested_rectangle_leaf.segment_offset == 4U &&
            nested_rectangle_leaf.segment_count == 4U &&
            nested_rectangle_leaf.fill_rule ==
                PROGPU_NATIVE_FILL_RULE_NON_ZERO &&
            nested_rectangle_leaf.min_x == 40.0F &&
            nested_rectangle_leaf.min_y == 10.0F &&
            nested_rectangle_leaf.max_x == 44.0F &&
            nested_rectangle_leaf.max_y == 13.0F);
        PROGPU_REQUIRE(
            nested_operation.kind ==
                PROGPU_NATIVE_PATH_BOOLEAN_INTERSECT);
        PROGPU_REQUIRE(
            outer_leaf.kind == PROGPU_NATIVE_PATH_BOOLEAN_LEAF &&
            outer_leaf.segment_offset == 8U &&
            outer_leaf.segment_count == 8U &&
            outer_leaf.min_x == 16.0F && outer_leaf.min_y == 0.0F &&
            outer_leaf.max_x == 26.0F && outer_leaf.max_y == 8.0F);
        PROGPU_REQUIRE(
            outer_operation.kind ==
                PROGPU_NATIVE_PATH_BOOLEAN_DIFFERENCE);
        found_recursive_combined = true;
    }
    PROGPU_REQUIRE(found_recursive_combined);

    const auto generation = state.resource_generation(group);
    std::vector<std::byte> malformed;
    append_command(
        malformed,
        command::geometry_group,
        group,
        transform,
        0U,
        8U,
        path_a);
    PROGPU_REQUIRE(state.apply(malformed) == status::malformed_batch);
    PROGPU_REQUIRE(state.resource_generation(group) == generation);

    std::vector<std::byte> null_operand_update;
    append_command(
        null_operand_update,
        command::combined_geometry,
        combined,
        transform,
        3U,
        path_a,
        0U);
    PROGPU_REQUIRE(state.apply(null_operand_update) == status::success);
    PROGPU_REQUIRE(
        state.build_scene(target, 7003U, 5U, stream) == status::success);
    const auto combined_generation = state.resource_generation(combined);
    std::vector<std::byte> invalid_combine;
    append_command(
        invalid_combine,
        command::combined_geometry,
        combined,
        transform,
        4U,
        path_a,
        path_b);
    PROGPU_REQUIRE(state.apply(invalid_combine) == status::malformed_batch);
    PROGPU_REQUIRE(
        state.resource_generation(combined) == combined_generation);

    std::vector<std::byte> delete_child;
    append_command(
        delete_child,
        command::channel_delete_resource,
        path_a,
        73U);
    PROGPU_REQUIRE(state.apply(delete_child) == status::invalid_graph);

    std::vector<std::byte> nested_update;
    const std::array group_child{group};
    append_geometry_group(
        nested_update,
        nested_group,
        0U,
        1U,
        group_child);
    PROGPU_REQUIRE(state.apply(nested_update) == status::success);
    std::vector<std::byte> cyclic_update;
    const std::array nested_child{nested_group};
    append_geometry_group(
        cyclic_update,
        group,
        transform,
        0U,
        nested_child);
    PROGPU_REQUIRE(state.apply(cyclic_update) == status::invalid_graph);
    PROGPU_REQUIRE(state.resource_generation(group) == generation);

    std::vector<std::byte> combined_group_update;
    append_command(
        combined_group_update,
        command::combined_geometry,
        combined,
        transform,
        0U,
        group,
        0U);
    PROGPU_REQUIRE(state.apply(combined_group_update) == status::success);
    std::vector<std::byte> cross_kind_cycle;
    const std::array combined_child{combined};
    append_geometry_group(
        cross_kind_cycle,
        group,
        transform,
        0U,
        combined_child);
    PROGPU_REQUIRE(state.apply(cross_kind_cycle) == status::invalid_graph);
    PROGPU_REQUIRE(state.resource_generation(group) == generation);

    std::vector<std::byte> transformed_arc_update;
    append_path_geometry(
        transformed_arc_update,
        path_b,
        arc_transform,
        1U,
        make_arc_path_figures());
    PROGPU_REQUIRE(state.apply(transformed_arc_update) == status::success);
    PROGPU_REQUIRE(
        state.build_scene(target, 7003U, 6U, stream) == status::success);
    const auto transformed_arc_header =
        read_value<progpu_native_scene_header>(stream, 0U);
    bool found_transformed_arc = false;
    bool found_transformed_boolean_arc = false;
    for (std::uint32_t index = 0U;
         index < transformed_arc_header.resource_count;
         ++index) {
        const auto resource = read_value<progpu_native_scene_resource>(
            stream,
            transformed_arc_header.resource_offset +
                index * sizeof(progpu_native_scene_resource));
        if (resource.kind != PROGPU_NATIVE_SCENE_RESOURCE_PATH_BATCH) {
            continue;
        }
        const auto path = read_value<progpu_native_scene_path_fill>(
            stream,
            resource.payload_offset);
        if (path.boolean_node_count == 3U && path.segment_count == 26U) {
            const auto boolean_arc = read_value<
                progpu_native_path_segment>(
                    stream,
                    resource.auxiliary_offset +
                        4U * sizeof(progpu_native_path_segment));
            PROGPU_REQUIRE(
                boolean_arc.kind == PROGPU_NATIVE_PATH_SEGMENT_ARC);
            PROGPU_REQUIRE(
                boolean_arc.p0.x == 5.375F && boolean_arc.p0.y == 3.0F);
            PROGPU_REQUIRE(
                boolean_arc.p1.x == -7.375F &&
                boolean_arc.p1.y == 15.75F);
            PROGPU_REQUIRE(std::bit_cast<float>(boolean_arc.pad1) < 0.0F);
            found_transformed_boolean_arc = true;
            continue;
        }
        if (path.boolean_node_count != 11U || path.segment_count != 26U) {
            continue;
        }
        PROGPU_REQUIRE(path.sample_grid == 8U);
        const auto arc = read_value<progpu_native_path_segment>(
            stream,
            resource.auxiliary_offset +
                4U * sizeof(progpu_native_path_segment));
        PROGPU_REQUIRE(arc.kind == PROGPU_NATIVE_PATH_SEGMENT_ARC);
        PROGPU_REQUIRE(arc.p0.x == 2.25F && arc.p0.y == 0.0F);
        PROGPU_REQUIRE(arc.p1.x == -6.25F && arc.p1.y == 8.5F);
        PROGPU_REQUIRE(arc.p3.x > 0.0F && arc.p3.y > 0.0F);
        PROGPU_REQUIRE(std::bit_cast<float>(arc.pad1) < 0.0F);

        progpu::native::geometry::arc_point source_center{};
        float source_theta1 = 0.0F;
        float source_delta = 0.0F;
        float source_radius_x = 0.0F;
        float source_radius_y = 0.0F;
        PROGPU_REQUIRE(progpu::native::geometry::resolve_arc(
            {1.0F, 2.0F},
            {9.0F, 8.0F},
            {8.0F, 6.0F},
            30.0F,
            false,
            true,
            source_center,
            source_theta1,
            source_delta,
            source_radius_x,
            source_radius_y));
        const float output_theta1 = std::bit_cast<float>(arc.pad0);
        const float output_delta = std::bit_cast<float>(arc.pad1);
        const float output_rotation = std::bit_cast<float>(arc.pad2);
        for (const float fraction :
             std::array{0.0F, 0.25F, 0.5F, 0.75F, 1.0F}) {
            const auto source_point =
                progpu::native::geometry::evaluate_arc(
                    source_center,
                    source_radius_x,
                    source_radius_y,
                    30.0F,
                    source_theta1 + fraction * source_delta);
            const float expected_x =
                source_point.x * -1.25F + source_point.y * 0.25F + 3.0F;
            const float expected_y =
                source_point.x * 0.5F + source_point.y * 0.75F - 2.0F;
            const float theta = output_theta1 + fraction * output_delta;
            const float cosine_theta = std::cos(theta);
            const float sine_theta = std::sin(theta);
            const float cosine_rotation = std::cos(output_rotation);
            const float sine_rotation = std::sin(output_rotation);
            const float actual_x =
                arc.p3.x * cosine_theta * cosine_rotation -
                arc.p3.y * sine_theta * sine_rotation + arc.p2.x;
            const float actual_y =
                arc.p3.x * cosine_theta * sine_rotation +
                arc.p3.y * sine_theta * cosine_rotation + arc.p2.y;
            PROGPU_REQUIRE(std::abs(actual_x - expected_x) < 0.0001F);
            PROGPU_REQUIRE(std::abs(actual_y - expected_y) < 0.0001F);
        }
        found_transformed_arc = true;
    }
    PROGPU_REQUIRE(found_transformed_arc);
    PROGPU_REQUIRE(found_transformed_boolean_arc);

    std::vector<std::byte> translated_arc_update;
    append_path_geometry(
        translated_arc_update,
        path_b,
        child_transform,
        1U,
        make_arc_path_figures());
    PROGPU_REQUIRE(state.apply(translated_arc_update) == status::success);
    PROGPU_REQUIRE(
        state.build_scene(target, 7003U, 70U, stream) == status::success);
    const auto translated_arc_header =
        read_value<progpu_native_scene_header>(stream, 0U);
    bool found_translated_arc = false;
    for (std::uint32_t resource_index = 0U;
         resource_index < translated_arc_header.resource_count &&
             !found_translated_arc;
         ++resource_index) {
        const auto resource = read_value<progpu_native_scene_resource>(
            stream,
            translated_arc_header.resource_offset +
                resource_index * sizeof(progpu_native_scene_resource));
        if (resource.kind != PROGPU_NATIVE_SCENE_RESOURCE_PATH_BATCH) {
            continue;
        }
        const auto path = read_value<progpu_native_scene_path_fill>(
            stream,
            resource.payload_offset);
        if (path.boolean_node_count != 11U) {
            continue;
        }
        for (std::size_t segment_index = 0U;
             segment_index < path.segment_count;
             ++segment_index) {
            const auto arc = read_value<progpu_native_path_segment>(
                stream,
                resource.auxiliary_offset +
                    segment_index * sizeof(progpu_native_path_segment));
            if (arc.kind != PROGPU_NATIVE_PATH_SEGMENT_ARC) {
                continue;
            }
            progpu::native::geometry::arc_point source_center{};
            float source_theta1 = 0.0F;
            float source_delta = 0.0F;
            float source_radius_x = 0.0F;
            float source_radius_y = 0.0F;
            PROGPU_REQUIRE(progpu::native::geometry::resolve_arc(
                {1.0F, 2.0F},
                {9.0F, 8.0F},
                {8.0F, 6.0F},
                30.0F,
                false,
                true,
                source_center,
                source_theta1,
                source_delta,
                source_radius_x,
                source_radius_y));
            PROGPU_REQUIRE(
                arc.p0.x == 21.0F && arc.p0.y == 7.0F &&
                arc.p1.x == 29.0F && arc.p1.y == 13.0F &&
                arc.p2.x == source_center.x + 20.0F &&
                arc.p2.y == source_center.y + 5.0F &&
                arc.p3.x == source_radius_x &&
                arc.p3.y == source_radius_y);
            PROGPU_REQUIRE(
                arc.pad0 == std::bit_cast<std::uint32_t>(source_theta1) &&
                arc.pad1 == std::bit_cast<std::uint32_t>(source_delta) &&
                arc.pad2 == std::bit_cast<std::uint32_t>(
                    30.0F * std::numbers::pi_v<float> / 180.0F));
            found_translated_arc = true;
            break;
        }
    }
    PROGPU_REQUIRE(found_translated_arc);

    std::vector<std::byte> second_group_arc_update;
    append_path_geometry(
        second_group_arc_update,
        path_a,
        0U,
        1U,
        make_arc_path_figures());
    PROGPU_REQUIRE(state.apply(second_group_arc_update) == status::success);
    PROGPU_REQUIRE(
        state.build_scene(target, 7003U, 71U, stream) == status::success);
    const auto multi_arc_header =
        read_value<progpu_native_scene_header>(stream, 0U);
    bool found_preserved_group_arcs = false;
    for (std::uint32_t resource_index = 0U;
         resource_index < multi_arc_header.resource_count;
         ++resource_index) {
        const auto resource = read_value<progpu_native_scene_resource>(
            stream,
            multi_arc_header.resource_offset +
                resource_index * sizeof(progpu_native_scene_resource));
        if (resource.kind != PROGPU_NATIVE_SCENE_RESOURCE_PATH_BATCH) {
            continue;
        }
        const auto path = read_value<progpu_native_scene_path_fill>(
            stream,
            resource.payload_offset);
        if (path.boolean_node_count != 11U) {
            continue;
        }
        std::size_t arc_count = 0U;
#if defined(__clang__)
#pragma clang loop vectorize(disable) interleave(disable)
#endif
        for (std::size_t segment_index = 0U;
             segment_index < path.segment_count;
             ++segment_index) {
            const auto segment = read_value<progpu_native_path_segment>(
                stream,
                resource.auxiliary_offset +
                    segment_index * sizeof(progpu_native_path_segment));
            arc_count += segment.kind == PROGPU_NATIVE_PATH_SEGMENT_ARC
                ? 1U
                : 0U;
        }
        PROGPU_REQUIRE(arc_count == 11U);
        found_preserved_group_arcs = true;
    }
    PROGPU_REQUIRE(found_preserved_group_arcs);

    std::vector<std::byte> restore_path_a;
    append_path_geometry(restore_path_a, path_a, 0U, 1U, figures_a);
    PROGPU_REQUIRE(state.apply(restore_path_a) == status::success);

    std::vector<std::byte> singular_arc_update;
    append_path_geometry(
        singular_arc_update,
        path_b,
        singular_transform,
        1U,
        make_arc_path_figures());
    const std::array singular_group_children{path_b};
    append_geometry_group(
        singular_arc_update,
        group,
        0U,
        1U,
        singular_group_children);
    append_command(
        singular_arc_update,
        command::combined_geometry,
        combined,
        0U,
        0U,
        path_b,
        0U);
    std::vector<std::byte> singular_render_data;
    append_command(
        singular_render_data,
        command::draw_geometry,
        brush,
        0U,
        group,
        0U);
    append_command(
        singular_render_data,
        command::draw_geometry,
        brush,
        0U,
        combined,
        0U);
    append_command(
        singular_render_data,
        command::draw_geometry,
        brush,
        0U,
        path_b,
        0U);
    append_command(
        singular_render_data,
        command::push_clip,
        path_b,
        0U);
    append_command(
        singular_render_data,
        command::draw_rectangle,
        0.0,
        0.0,
        64.0,
        64.0,
        brush,
        0U);
    append_command(singular_render_data, command::pop);
    append_command(
        singular_render_data,
        command::push_transform,
        singular_transform,
        0U);
    append_command(
        singular_render_data,
        command::draw_line,
        0.0,
        0.0,
        32.0,
        32.0,
        pen,
        0U);
    append_command(
        singular_render_data,
        command::draw_geometry,
        brush,
        pen,
        group,
        0U);
    append_command(
        singular_render_data,
        command::draw_geometry,
        brush,
        pen,
        combined,
        0U);
    append_command(
        singular_render_data,
        command::draw_geometry,
        brush,
        pen,
        path_a,
        0U);
    append_command(
        singular_render_data,
        command::draw_geometry,
        brush,
        pen,
        rounded_rectangle,
        0U);
    append_command(singular_render_data, command::pop);
    append_render_data(
        singular_arc_update,
        content,
        singular_render_data);
    PROGPU_REQUIRE(state.apply(singular_arc_update) == status::success);
    PROGPU_REQUIRE(
        state.build_scene(target, 7003U, 7U, stream) == status::success);
    const auto singular_header =
        read_value<progpu_native_scene_header>(stream, 0U);
    bool found_empty_singular_clip = false;
    for (std::uint32_t index = 0U;
         index < singular_header.resource_count;
         ++index) {
        const auto resource = read_value<progpu_native_scene_resource>(
            stream,
            singular_header.resource_offset +
                index * sizeof(progpu_native_scene_resource));
        PROGPU_REQUIRE(
            resource.kind != PROGPU_NATIVE_SCENE_RESOURCE_PATH_BATCH);
        PROGPU_REQUIRE(
            resource.kind != PROGPU_NATIVE_SCENE_RESOURCE_LAYER_MASK);
        if (resource.kind != PROGPU_NATIVE_SCENE_RESOURCE_STATE) {
            continue;
        }
        const auto scene_state = read_value<progpu_native_scene_state>(
            stream,
            resource.payload_offset);
        if ((scene_state.flags & PROGPU_NATIVE_SCENE_STATE_CLIP_RECT) != 0U &&
            scene_state.clip_rect.width == 0.0F &&
            scene_state.clip_rect.height == 0.0F) {
            found_empty_singular_clip = true;
        }
    }
    PROGPU_REQUIRE(found_empty_singular_clip);
    std::uint32_t singular_draw_count = 0U;
    for (std::uint32_t index = 0U;
         index < singular_header.command_count;
         ++index) {
        const auto scene_command = read_value<progpu_native_scene_command>(
            stream,
            singular_header.command_offset +
                index * sizeof(progpu_native_scene_command));
        if (scene_command.kind == PROGPU_NATIVE_SCENE_COMMAND_DRAW_ANALYTIC) {
            ++singular_draw_count;
            continue;
        }
        PROGPU_REQUIRE(
            scene_command.kind != PROGPU_NATIVE_SCENE_COMMAND_DRAW_GEOMETRY &&
            scene_command.kind != PROGPU_NATIVE_SCENE_COMMAND_DRAW_PATH &&
            scene_command.kind !=
                PROGPU_NATIVE_SCENE_COMMAND_DRAW_STROKE_BATCH);
    }
    PROGPU_REQUIRE(singular_draw_count == 1U);

    std::vector<std::byte> different_nested_fill;
    const std::array different_fill_child{different_fill_group};
    append_geometry_group(
        different_nested_fill,
        group,
        transform,
        0U,
        different_fill_child);
    append_render_data(different_nested_fill, content, nested);
    PROGPU_REQUIRE(state.apply(different_nested_fill) == status::success);
    PROGPU_REQUIRE(
        state.build_scene(target, 7003U, 8U, stream) == status::success);
    const auto different_fill_header =
        read_value<progpu_native_scene_header>(stream, 0U);
    bool found_outer_fill_override = false;
    for (std::uint32_t index = 0U;
         index < different_fill_header.resource_count;
         ++index) {
        const auto resource = read_value<progpu_native_scene_resource>(
            stream,
            different_fill_header.resource_offset +
                index * sizeof(progpu_native_scene_resource));
        if (resource.kind != PROGPU_NATIVE_SCENE_RESOURCE_PATH_BATCH) {
            continue;
        }
        const auto path = read_value<progpu_native_scene_path_fill>(
            stream,
            resource.payload_offset);
        if (path.boolean_node_count == 0U && path.segment_count == 4U) {
            PROGPU_REQUIRE(
                path.fill_rule == PROGPU_NATIVE_FILL_RULE_EVEN_ODD);
            PROGPU_REQUIRE(path.sample_grid == 8U);
            found_outer_fill_override = true;
        }
    }
    PROGPU_REQUIRE(found_outer_fill_override);

    std::vector<std::byte> overlapping_translation_update;
    append_command(
        overlapping_translation_update,
        command::matrix_transform,
        child_transform,
        1.0,
        0.0,
        0.0,
        1.0,
        1.0,
        1.0,
        0U);
    const std::array overlapping_translation_children{
        path_a,
        same_fill_group};
    append_geometry_group(
        overlapping_translation_update,
        group,
        transform,
        0U,
        overlapping_translation_children);
    PROGPU_REQUIRE(
        state.apply(overlapping_translation_update) == status::success);
    PROGPU_REQUIRE(
        state.build_scene(target, 7003U, 9U, stream) ==
        status::unsupported_command);

    std::vector<std::byte> clip_update;
    append_path_geometry(
        clip_update,
        path_b,
        child_transform,
        1U,
        make_curve_path_figures());
    const std::array clip_group_children{path_a, path_b};
    append_geometry_group(
        clip_update,
        group,
        transform,
        0U,
        clip_group_children);
    append_command(
        clip_update,
        command::combined_geometry,
        combined,
        transform,
        3U,
        path_a,
        rounded_rectangle);
    std::vector<std::byte> clipped_render_data;
    append_command(clipped_render_data, command::push_clip, path_a, 0U);
    append_command(clipped_render_data, command::push_clip, group, 0U);
    append_command(clipped_render_data, command::push_clip, combined, 0U);
    append_command(
        clipped_render_data,
        command::draw_rectangle,
        0.0,
        0.0,
        64.0,
        64.0,
        brush,
        0U);
    append_command(clipped_render_data, command::pop);
    append_command(clipped_render_data, command::pop);
    append_command(clipped_render_data, command::pop);
    append_command(
        clipped_render_data,
        command::push_clip,
        rounded_rectangle,
        0U);
    append_command(
        clipped_render_data,
        command::draw_rectangle,
        0.0,
        0.0,
        8.0,
        8.0,
        brush,
        0U);
    append_command(clipped_render_data, command::pop);
    append_render_data(clip_update, content, clipped_render_data);
    PROGPU_REQUIRE(state.apply(clip_update) == status::success);
    PROGPU_REQUIRE(
        state.build_scene(target, 7003U, 10U, stream) == status::success);
    const auto clip_header =
        read_value<progpu_native_scene_header>(stream, 0U);
    bool found_complete_clip_chain = false;
    bool found_complete_clip_state = false;
    bool found_restored_clip_chain = false;
    for (std::uint32_t index = 0U;
         index < clip_header.resource_count;
         ++index) {
        const auto resource = read_value<progpu_native_scene_resource>(
            stream,
            clip_header.resource_offset +
                index * sizeof(progpu_native_scene_resource));
        if (resource.kind == PROGPU_NATIVE_SCENE_RESOURCE_LAYER_MASK) {
            const auto mask =
                read_value<progpu_native_scene_layer_vector_mask>(
                    stream,
                    resource.payload_offset);
            if (mask.kind !=
                    PROGPU_NATIVE_SCENE_LAYER_MASK_VECTOR_CLIP_CHAIN ||
                mask.path_count != 3U) {
                if (mask.kind ==
                        PROGPU_NATIVE_SCENE_LAYER_MASK_VECTOR_CLIP_CHAIN &&
                    mask.path_count == 1U &&
                    mask.segment_count == 8U) {
                    const auto segment =
                        read_value<progpu_native_path_segment>(
                            stream,
                            resource.auxiliary_offset +
                                sizeof(progpu_native_scene_clip_path));
                    if (segment.kind == PROGPU_NATIVE_PATH_SEGMENT_ARC) {
                        found_restored_clip_chain = true;
                    }
                }
                continue;
            }
            PROGPU_REQUIRE(mask.segment_count == 24U);
            PROGPU_REQUIRE(mask.boolean_node_count == 6U);
            const auto first_path =
                read_value<progpu_native_scene_clip_path>(
                    stream,
                    resource.auxiliary_offset);
            const auto group_path =
                read_value<progpu_native_scene_clip_path>(
                    stream,
                    resource.auxiliary_offset +
                        sizeof(progpu_native_scene_clip_path));
            const auto combined_path =
                read_value<progpu_native_scene_clip_path>(
                    stream,
                    resource.auxiliary_offset +
                        2U * sizeof(progpu_native_scene_clip_path));
            PROGPU_REQUIRE(
                first_path.segment_count == 4U &&
                first_path.boolean_node_count == 0U &&
                first_path.operation == PROGPU_NATIVE_CLIP_INTERSECT);
            PROGPU_REQUIRE(
                group_path.segment_count == 8U &&
                group_path.boolean_node_count == 3U &&
                group_path.operation == PROGPU_NATIVE_CLIP_INTERSECT);
            PROGPU_REQUIRE(
                combined_path.segment_count == 12U &&
                combined_path.boolean_node_count == 3U &&
                combined_path.operation == PROGPU_NATIVE_CLIP_INTERSECT);
            found_complete_clip_chain = true;
        } else if (resource.kind == PROGPU_NATIVE_SCENE_RESOURCE_STATE) {
            const auto scene_state = read_value<progpu_native_scene_state>(
                stream,
                resource.payload_offset);
            if ((scene_state.flags & PROGPU_NATIVE_SCENE_STATE_MASK) != 0U) {
                const auto mask_resource =
                    read_value<progpu_native_scene_resource>(
                        stream,
                        clip_header.resource_offset +
                            scene_state.mask_resource_index *
                                sizeof(progpu_native_scene_resource));
                const auto mask =
                    read_value<progpu_native_scene_layer_vector_mask>(
                        stream,
                        mask_resource.payload_offset);
                if (mask.path_count == 3U) {
                    found_complete_clip_state = true;
                }
            }
        }
    }
    PROGPU_REQUIRE(found_complete_clip_chain);
    PROGPU_REQUIRE(found_complete_clip_state);
    PROGPU_REQUIRE(found_restored_clip_chain);
    return true;
}

bool render_data_scope_errors_fail_closed() {
    constexpr std::uint32_t visual = 1U;
    constexpr std::uint32_t content = 2U;
    constexpr std::uint32_t target = 3U;
    std::vector<std::byte> batch;
    append_create(batch, visual, 39U);
    append_create(batch, content, 43U);
    append_create(batch, target, 47U);
    append_command(batch, command::visual_create, visual);
    append_command(batch, command::visual_set_content, visual, content);
    std::vector<std::byte> nested;
    append_command(nested, command::push_opacity, 0.5);
    append_render_data(batch, content, nested);
    append_command(
        batch,
        command::generic_target_create,
        target,
        std::uint64_t{0U},
        std::uint64_t{0U},
        16U,
        16U,
        0U);
    append_command(batch, command::target_set_root, target, visual);

    channel state;
    PROGPU_REQUIRE(state.apply(batch) == status::success);
    std::vector<std::byte> stream;
    PROGPU_REQUIRE(
        state.build_scene(target, 1U, 1U, stream) ==
        status::invalid_graph);

    std::vector<std::byte> pop_batch;
    std::vector<std::byte> unmatched_pop;
    append_command(unmatched_pop, command::pop);
    append_render_data(pop_batch, content, unmatched_pop);
    PROGPU_REQUIRE(state.apply(pop_batch) == status::success);
    PROGPU_REQUIRE(
        state.build_scene(target, 1U, 2U, stream) ==
        status::invalid_graph);

    std::vector<std::byte> unequal_batch;
    std::vector<std::byte> unequal_radius;
    append_command(
        unequal_radius,
        command::draw_rounded_rectangle,
        0.0,
        0.0,
        0.0,
        10.0,
        0.0,
        3.0,
        visual,
        0U);
    append_render_data(unequal_batch, content, unequal_radius);
    PROGPU_REQUIRE(state.apply(unequal_batch) == status::success);
    PROGPU_REQUIRE(
        state.build_scene(target, 1U, 3U, stream) ==
        status::unsupported_command);

    std::vector<std::byte> null_transform_batch;
    std::vector<std::byte> null_transform;
    append_command(
        null_transform,
        command::push_transform,
        0U,
        0U);
    append_command(null_transform, command::pop);
    append_render_data(null_transform_batch, content, null_transform);
    PROGPU_REQUIRE(state.apply(null_transform_batch) == status::success);
    PROGPU_REQUIRE(
        state.build_scene(target, 1U, 4U, stream) == status::success);

    std::vector<std::byte> missing_transform_batch;
    std::vector<std::byte> missing_transform;
    append_command(
        missing_transform,
        command::push_transform,
        99U,
        0U);
    append_command(missing_transform, command::pop);
    append_render_data(
        missing_transform_batch,
        content,
        missing_transform);
    PROGPU_REQUIRE(state.apply(missing_transform_batch) == status::success);
    PROGPU_REQUIRE(
        state.build_scene(target, 1U, 5U, stream) ==
        status::invalid_handle);

    std::vector<std::byte> nonzero_padding_batch;
    std::vector<std::byte> nonzero_padding;
    append_command(
        nonzero_padding,
        command::push_transform,
        1U,
        1U);
    append_command(nonzero_padding, command::pop);
    append_render_data(
        nonzero_padding_batch,
        content,
        nonzero_padding);
    PROGPU_REQUIRE(state.apply(nonzero_padding_batch) == status::success);
    PROGPU_REQUIRE(
        state.build_scene(target, 1U, 6U, stream) ==
        status::malformed_batch);
    return true;
}

bool retained_gradient_brushes_compile_with_wpf_mapping_and_animation() {
    constexpr std::uint32_t visual = 1U;
    constexpr std::uint32_t content = 2U;
    constexpr std::uint32_t target = 3U;
    constexpr std::uint32_t linear = 4U;
    constexpr std::uint32_t start_point = 5U;
    constexpr std::uint32_t end_point = 6U;
    constexpr std::uint32_t opacity = 7U;
    constexpr std::uint32_t relative = 8U;
    constexpr std::uint32_t absolute = 9U;
    constexpr std::uint32_t radial = 10U;
    constexpr std::uint32_t radius_x = 11U;
    constexpr std::uint32_t radius_y = 12U;

    std::vector<std::byte> batch;
    append_create(batch, visual, 39U);
    append_create(batch, content, 43U);
    append_create(batch, target, 47U);
    append_create(batch, linear, 77U);
    append_create(batch, start_point, 51U);
    append_create(batch, end_point, 51U);
    append_create(batch, opacity, 49U);
    append_create(batch, relative, 62U);
    append_create(batch, absolute, 62U);
    append_create(batch, radial, 78U);
    append_create(batch, radius_x, 49U);
    append_create(batch, radius_y, 49U);
    append_command(batch, command::visual_create, visual);
    append_command(batch, command::visual_set_content, visual, content);
    append_command(batch, command::point_resource, start_point, 0.25, 0.5);
    append_command(batch, command::point_resource, end_point, 0.75, 0.5);
    append_command(batch, command::double_resource, opacity, 0.6);
    append_command(batch, command::double_resource, radius_x, 12.0);
    append_command(batch, command::double_resource, radius_y, 6.0);
    append_command(
        batch,
        command::translate_transform,
        relative,
        0.1,
        0.2,
        0U,
        0U);
    append_command(
        batch,
        command::translate_transform,
        absolute,
        3.0,
        4.0,
        0U,
        0U);
    const std::array linear_stops{
        mil_gradient_stop{1.0, {0.0F, 0.0F, 1.0F, 1.0F}},
        mil_gradient_stop{-1.0, {1.0F, 0.0F, 0.0F, 1.0F}},
        mil_gradient_stop{0.5, {0.0F, 1.0F, 0.0F, 0.5F}}};
    append_linear_gradient_brush(
        batch,
        linear,
        1.0,
        0.0,
        0.0,
        1.0,
        0.0,
        opacity,
        absolute,
        relative,
        1U,
        1U,
        1U,
        start_point,
        end_point,
        linear_stops);
    const std::array radial_stops{
        mil_gradient_stop{0.0, {1.0F, 1.0F, 1.0F, 1.0F}},
        mil_gradient_stop{1.0, {0.0F, 0.0F, 0.0F, 0.0F}}};
    append_radial_gradient_brush(
        batch,
        radial,
        0.8,
        20.0,
        20.0,
        1.0,
        1.0,
        18.0,
        19.0,
        0U,
        0U,
        2U,
        radius_x,
        radius_y,
        radial_stops);
    std::vector<std::byte> nested;
    append_command(
        nested,
        command::draw_rectangle,
        10.0,
        20.0,
        100.0,
        50.0,
        linear,
        0U);
    append_command(
        nested,
        command::draw_ellipse,
        20.0,
        20.0,
        15.0,
        10.0,
        radial,
        0U);
    append_render_data(batch, content, nested);
    append_command(
        batch,
        command::generic_target_create,
        target,
        std::uint64_t{0U},
        std::uint64_t{0U},
        160U,
        100U,
        0U);
    append_command(batch, command::target_set_root, target, visual);

    channel state;
    PROGPU_REQUIRE(state.apply(batch) == status::success);
    std::vector<std::byte> stream;
    progpu::native::mil::scene_metrics metrics{};
    PROGPU_REQUIRE(
        state.build_scene(target, 8100U, 1U, stream, &metrics) ==
        status::success);
    PROGPU_REQUIRE(metrics.brush_count == 2U);
    const auto header = read_value<progpu_native_scene_header>(stream, 0U);
    bool found_gradients = false;
    for (std::uint32_t index = 0U; index < header.resource_count; ++index) {
        const auto resource = read_value<progpu_native_scene_resource>(
            stream,
            header.resource_offset +
                index * sizeof(progpu_native_scene_resource));
        if (resource.kind != PROGPU_NATIVE_SCENE_RESOURCE_BRUSH_TABLE) {
            continue;
        }
        PROGPU_REQUIRE(resource.payload_size ==
            2U * sizeof(progpu_native_scene_brush));
        const auto linear_brush = read_value<progpu_native_scene_brush>(
            stream, resource.payload_offset);
        const auto radial_brush = read_value<progpu_native_scene_brush>(
            stream,
            resource.payload_offset + sizeof(progpu_native_scene_brush));
        PROGPU_REQUIRE(
            linear_brush.type == PROGPU_NATIVE_SCENE_BRUSH_LINEAR_GRADIENT);
        PROGPU_REQUIRE(linear_brush.opacity == 0.6F);
        PROGPU_REQUIRE(linear_brush.start_point.x == 35.0F);
        PROGPU_REQUIRE(linear_brush.start_point.y == 45.0F);
        PROGPU_REQUIRE(linear_brush.end_point.x == 85.0F);
        PROGPU_REQUIRE(linear_brush.end_point.y == 45.0F);
        PROGPU_REQUIRE(linear_brush.spread_method ==
            PROGPU_NATIVE_SCENE_GRADIENT_REFLECT);
        PROGPU_REQUIRE(linear_brush.color_interpolation_mode ==
            PROGPU_NATIVE_SCENE_GRADIENT_INTERPOLATE_SRGB);
        PROGPU_REQUIRE(linear_brush.stop_count == 3U);
        PROGPU_REQUIRE(linear_brush.coordinate_transform0[2] == -13.0F);
        PROGPU_REQUIRE(linear_brush.coordinate_transform1[2] == -14.0F);
        const auto first_stop = read_value<
            progpu_native_scene_gradient_stop>(
            stream,
            resource.auxiliary_offset +
                linear_brush.stop_offset *
                    sizeof(progpu_native_scene_gradient_stop));
        const auto middle_stop = read_value<
            progpu_native_scene_gradient_stop>(
            stream,
            resource.auxiliary_offset +
                (linear_brush.stop_offset + 1U) *
                    sizeof(progpu_native_scene_gradient_stop));
        PROGPU_REQUIRE(first_stop.offset == 0.0F);
        PROGPU_REQUIRE(std::abs(first_stop.color.r - (1.0F / 3.0F)) < 1e-6F);
        PROGPU_REQUIRE(std::abs(first_stop.color.g - (2.0F / 3.0F)) < 1e-6F);
        PROGPU_REQUIRE(middle_stop.offset == 0.5F);
        PROGPU_REQUIRE(
            radial_brush.type == PROGPU_NATIVE_SCENE_BRUSH_RADIAL_GRADIENT);
        PROGPU_REQUIRE(radial_brush.opacity == 0.8F);
        PROGPU_REQUIRE(radial_brush.center.x == 20.0F);
        PROGPU_REQUIRE(radial_brush.center.y == 20.0F);
        PROGPU_REQUIRE(radial_brush.start_point.x == 18.0F);
        PROGPU_REQUIRE(radial_brush.start_point.y == 19.0F);
        PROGPU_REQUIRE(radial_brush.radius == 12.0F);
        PROGPU_REQUIRE(radial_brush.radius_y == 6.0F);
        PROGPU_REQUIRE(radial_brush.spread_method ==
            PROGPU_NATIVE_SCENE_GRADIENT_REPEAT);
        PROGPU_REQUIRE(radial_brush.color_interpolation_mode ==
            PROGPU_NATIVE_SCENE_GRADIENT_INTERPOLATE_SCRGB);
        found_gradients = true;
    }
    PROGPU_REQUIRE(found_gradients);

    std::vector<std::byte> update;
    append_command(update, command::point_resource, start_point, 0.0, 0.0);
    append_command(update, command::double_resource, opacity, 0.25);
    PROGPU_REQUIRE(state.apply(update) == status::success);
    PROGPU_REQUIRE(
        state.build_scene(target, 8100U, 2U, stream, &metrics) ==
        status::success);
    std::vector<std::byte> delete_dependency;
    append_command(
        delete_dependency,
        command::channel_delete_resource,
        start_point,
        51U);
    PROGPU_REQUIRE(state.apply(delete_dependency) == status::invalid_graph);
    return true;
}

bool malformed_and_unsupported_packets_fail_closed() {
    channel state;
    const std::array malformed{
        std::byte{7}, std::byte{0}, std::byte{0}, std::byte{0},
        std::byte{1}, std::byte{0}, std::byte{0}, std::byte{0}};
    PROGPU_REQUIRE(state.apply(malformed) == status::malformed_batch);

    std::vector<std::byte> unknown;
    append_command(unknown, static_cast<command>(0x8eU));
    PROGPU_REQUIRE(state.apply(unknown) == status::unknown_command);

    std::vector<std::byte> unsupported;
    append_command(unsupported, command::draw_rectangle);
    batch_metrics metrics{};
    PROGPU_REQUIRE(
        state.apply(unsupported, &metrics) == status::unsupported_command);
    PROGPU_REQUIRE(metrics.unsupported_command_count == 1U);
    PROGPU_REQUIRE(state.resource_count() == 0U);
    return true;
}

bool c_abi_is_typed_and_size_versioned() {
    progpu_native_mil_channel* native_channel = nullptr;
    PROGPU_REQUIRE(
        progpu_native_mil_channel_create(&native_channel) ==
        PROGPU_NATIVE_MIL_STATUS_SUCCESS);
    PROGPU_REQUIRE(native_channel != nullptr);

    std::vector<std::byte> batch;
    append_create(batch, 17U, 39U);
    append_command(batch, command::visual_create, 17U);
    append_command(batch, command::visual_set_offset, 17U, 2.0, 4.0);
    progpu_native_mil_batch_metrics metrics{};
    metrics.struct_size = sizeof(metrics);
    PROGPU_REQUIRE(
        progpu_native_mil_channel_apply(
            native_channel,
            batch.data(),
            batch.size(),
            &metrics) == PROGPU_NATIVE_MIL_STATUS_SUCCESS);
    PROGPU_REQUIRE(metrics.command_count == 3U);
    PROGPU_REQUIRE(
        progpu_native_mil_channel_get_resource_count(native_channel) == 1U);
    PROGPU_REQUIRE(
        progpu_native_mil_channel_get_resource_type(native_channel, 17U) ==
        39U);
    progpu_native_mil_visual_snapshot snapshot{};
    snapshot.struct_size = sizeof(snapshot);
    PROGPU_REQUIRE(
        progpu_native_mil_channel_get_visual(
            native_channel, 17U, &snapshot) == 1U);
    PROGPU_REQUIRE(snapshot.offset_x == 2.0);
    PROGPU_REQUIRE(snapshot.offset_y == 4.0);
    progpu_native_mil_channel_destroy(native_channel);
    return true;
}

} // namespace

int main() {
    PROGPU_REQUIRE(channel_retains_visual_target_graph());
    PROGPU_REQUIRE(failed_batches_roll_back());
    PROGPU_REQUIRE(invalid_visual_graphs_fail_closed());
    PROGPU_REQUIRE(solid_rectangle_compiles_to_semantic_scene());
    PROGPU_REQUIRE(visual_clips_compile_to_exact_semantic_state());
    PROGPU_REQUIRE(visual_solid_opacity_mask_composes_and_updates());
    PROGPU_REQUIRE(matrix_transform_scopes_compile_to_semantic_state());
    PROGPU_REQUIRE(
        static_transform_resources_compose_and_retain_dependencies());
    PROGPU_REQUIRE(solid_pen_line_compiles_to_geometry_scene());
    PROGPU_REQUIRE(retained_path_geometry_compiles_to_semantic_scene());
    PROGPU_REQUIRE(
        retained_line_path_stroke_preserves_closure_gaps_and_pen_state());
    PROGPU_REQUIRE(
        retained_geometry_drawing_reuses_native_geometry_lowering());
    PROGPU_REQUIRE(
        retained_drawing_group_composes_children_transform_and_opacity());
    PROGPU_REQUIRE(
        retained_static_guideline_set_snaps_one_guide_per_axis());
    PROGPU_REQUIRE(
        retained_image_drawing_uses_pointer_free_bitmap_sideband());
    PROGPU_REQUIRE(
        retained_drawing_image_maps_vector_content_into_destination());
    PROGPU_REQUIRE(
        retained_glyph_run_drawing_uses_pointer_free_sfnt_sideband());
    PROGPU_REQUIRE(retained_geometry_group_compiles_to_one_semantic_path());
    PROGPU_REQUIRE(
        retained_gradient_brushes_compile_with_wpf_mapping_and_animation());
    PROGPU_REQUIRE(render_data_scope_errors_fail_closed());
    PROGPU_REQUIRE(malformed_and_unsupported_packets_fail_closed());
    PROGPU_REQUIRE(c_abi_is_typed_and_size_versioned());
    return 0;
}
