#include "progpu_native_mil.hpp"
#include "progpu_native_mil.h"
#include "progpu_native_scene_builder.hpp"
#include "progpu_native_mil_visual_clip_fixture.hpp"
#include "progpu_native_mil_image_brush_fixture.hpp"
#include "../src/Mil/progpu_native_mil_curve_dash.hpp"
#include "../src/Scene/progpu_native_semantic_path_stroke.hpp"
#include "progpu_native_text.hpp"
#include "../src/Geometry/progpu_native_arc.hpp"
#include "../src/Backend/progpu_native_geometry_base.hpp"

#include <array>
#include <bit>
#include <cmath>
#include <cstddef>
#include <cstdint>
#include <cstring>
#include <filesystem>
#include <fstream>
#include <iostream>
#include <limits>
#include <vector>

namespace {

using progpu::native::mil::batch_metrics;
using progpu::native::mil::channel;
using progpu::native::mil::command;
using progpu::native::mil::scene_build_request;
using progpu::native::mil::scene_build_request_flags;
using progpu::native::mil::scene_build_result;
using progpu::native::mil::scene_build_result_flags;
using progpu::native::mil::status;

namespace command_layouts = progpu::native::mil::command_layouts;

// Compile-time encoding regression; GPU pixel differential cases remain in
// the final image/cache Fant gate under both explicit and native policies.
static_assert([] {
    for (std::uint32_t sampling = 0U; sampling <= PROGPU_NATIVE_IMAGE_SAMPLING_FANT; ++sampling) {
        const float native_coefficient = sampling == PROGPU_NATIVE_IMAGE_SAMPLING_FANT ? -32.0F : 0.0F;
        const float explicit_coefficient = sampling == PROGPU_NATIVE_IMAGE_SAMPLING_FANT ? -256.0F :
            sampling == PROGPU_NATIVE_IMAGE_SAMPLING_NEAREST ? -128.0F :
            sampling == PROGPU_NATIVE_IMAGE_SAMPLING_LINEAR ? -64.0F : 0.0F;
        if (progpu::native::base_image_sampling_coefficient(0U, sampling) != native_coefficient ||
            progpu::native::base_image_sampling_coefficient(PROGPU_NATIVE_ENGINE_IMAGE_REQUIRE_NATIVE_SAMPLING,
                sampling) != native_coefficient ||
            progpu::native::base_image_sampling_coefficient(PROGPU_NATIVE_ENGINE_IMAGE_EXPLICIT_SHADER_SAMPLING,
                sampling) != explicit_coefficient)
            return false;
    }
    return true;
}());

static_assert(static_cast<std::uint32_t>(command::invalid) == 0x00U);
static_assert(static_cast<std::uint32_t>(command::bitmap_cache) == 0x8dU);
static_assert(
    static_cast<std::uint32_t>(command::validate_structure_order) == 0x8eU);
static_assert(command_layouts::count == 141U);
static_assert(command_layouts::visual_set_offset::fixed_size == 24U);
static_assert(command_layouts::visual_set_offset::handle_offset == 4U);
static_assert(command_layouts::visual_set_offset::offset_x_offset == 8U);
static_assert(command_layouts::visual_set_offset::offset_y_offset == 16U);
static_assert(
    command_layouts::visual_set_guideline_collection::fixed_size == 16U);
static_assert(command_layouts::matrix_resource::fixed_size == 56U);
static_assert(command_layouts::matrix_resource::value_offset == 8U);
static_assert(command_layouts::transform_group::fixed_size == 12U);
static_assert(command_layouts::transform_group::children_size_offset == 8U);
static_assert(command_layouts::translate_transform::fixed_size == 32U);
static_assert(command_layouts::translate_transform::x_offset == 8U);
static_assert(
    command_layouts::translate_transform::h_y_animations_offset == 28U);
static_assert(command_layouts::scale_transform::fixed_size == 56U);
static_assert(command_layouts::scale_transform::center_x_offset == 24U);
static_assert(command_layouts::skew_transform::fixed_size == 56U);
static_assert(
    command_layouts::skew_transform::h_angle_x_animations_offset == 40U);
static_assert(command_layouts::rotate_transform::fixed_size == 44U);
static_assert(
    command_layouts::rotate_transform::h_angle_animations_offset == 32U);
static_assert(command_layouts::matrix_transform::fixed_size == 60U);
static_assert(command_layouts::matrix_transform::matrix_offset == 8U);
static_assert(
    command_layouts::matrix_transform::h_matrix_animations_offset == 56U);
static_assert(command_layouts::axis_angle_rotation3d::fixed_size == 36U);
static_assert(command_layouts::quaternion_rotation3d::fixed_size == 28U);
static_assert(command_layouts::perspective_camera::fixed_size == 96U);
static_assert(command_layouts::orthographic_camera::fixed_size == 96U);
static_assert(command_layouts::matrix_camera::fixed_size == 140U);
static_assert(command_layouts::transform3d_group::fixed_size == 12U);
static_assert(command_layouts::translate_transform3d::fixed_size == 44U);
static_assert(command_layouts::scale_transform3d::fixed_size == 80U);
static_assert(command_layouts::rotate_transform3d::fixed_size == 48U);
static_assert(command_layouts::matrix_transform3d::fixed_size == 72U);
static_assert(
    command_layouts::viewport3d_visual_set_camera::fixed_size == 12U);
static_assert(
    command_layouts::viewport3d_visual_set_viewport::fixed_size == 40U);
static_assert(
    command_layouts::viewport3d_visual_set_3d_child::fixed_size == 12U);
static_assert(command_layouts::visual3d_set_content::fixed_size == 12U);
static_assert(command_layouts::visual3d_set_transform::fixed_size == 12U);
static_assert(command_layouts::visual3d_remove_all_children::fixed_size == 8U);
static_assert(command_layouts::visual3d_remove_child::fixed_size == 12U);
static_assert(command_layouts::visual3d_insert_child_at::fixed_size == 16U);
static_assert(command_layouts::model3d_group::fixed_size == 16U);
static_assert(command_layouts::ambient_light::fixed_size == 32U);
static_assert(command_layouts::directional_light::fixed_size == 48U);
static_assert(command_layouts::point_light::fixed_size == 96U);
static_assert(command_layouts::spot_light::fixed_size == 136U);
static_assert(command_layouts::geometry_model3d::fixed_size == 24U);
static_assert(command_layouts::mesh_geometry3d::fixed_size == 24U);
static_assert(command_layouts::material_group::fixed_size == 12U);
static_assert(command_layouts::diffuse_material::fixed_size == 44U);
static_assert(command_layouts::specular_material::fixed_size == 36U);
static_assert(command_layouts::emissive_material::fixed_size == 28U);
static_assert(command_layouts::line_geometry::fixed_size == 52U);
static_assert(command_layouts::line_geometry::end_point_offset == 24U);
static_assert(command_layouts::rectangle_geometry::fixed_size == 72U);
static_assert(command_layouts::rectangle_geometry::rect_offset == 24U);
static_assert(command_layouts::ellipse_geometry::fixed_size == 56U);
static_assert(command_layouts::ellipse_geometry::center_offset == 24U);
static_assert(command_layouts::geometry_group::fixed_size == 20U);
static_assert(command_layouts::geometry_group::children_size_offset == 16U);
static_assert(command_layouts::combined_geometry::fixed_size == 24U);
static_assert(command_layouts::combined_geometry::h_geometry2_offset == 20U);
static_assert(command_layouts::path_geometry::fixed_size == 20U);
static_assert(command_layouts::path_geometry::figures_size_offset == 16U);
static_assert(command_layouts::solid_color_brush::fixed_size == 48U);
static_assert(command_layouts::solid_color_brush::color_offset == 16U);
static_assert(command_layouts::linear_gradient_brush::fixed_size == 84U);
static_assert(
    command_layouts::linear_gradient_brush::gradient_stops_size_offset == 72U);
static_assert(command_layouts::radial_gradient_brush::fixed_size == 108U);
static_assert(
    command_layouts::radial_gradient_brush::gradient_stops_size_offset == 88U);
static_assert(command_layouts::dash_style::fixed_size == 24U);
static_assert(command_layouts::dash_style::dashes_size_offset == 20U);
static_assert(command_layouts::pen::fixed_size == 52U);
static_assert(command_layouts::pen::h_dash_style_offset == 48U);
static_assert(command_layouts::geometry_drawing::fixed_size == 20U);
static_assert(command_layouts::geometry_drawing::h_geometry_offset == 16U);
static_assert(command_layouts::glyph_run_drawing::fixed_size == 16U);
static_assert(
    command_layouts::glyph_run_drawing::h_foreground_brush_offset == 12U);
static_assert(command_layouts::image_drawing::fixed_size == 48U);
static_assert(command_layouts::image_drawing::rect_offset == 8U);
static_assert(command_layouts::drawing_image::fixed_size == 12U);
static_assert(command_layouts::guideline_set::fixed_size == 20U);
static_assert(command_layouts::guideline_set::is_dynamic_offset == 16U);
static_assert(command_layouts::drawing_group::fixed_size == 52U);
static_assert(command_layouts::drawing_group::children_size_offset == 16U);
static_assert(command_layouts::bitmap_cache::fixed_size == 28U);
static_assert(command_layouts::bitmap_cache::enable_clear_type_offset == 24U);
static_assert(command_layouts::blur_effect::fixed_size == 28U);
static_assert(command_layouts::blur_effect::rendering_bias_offset == 24U);
static_assert(command_layouts::drop_shadow_effect::fixed_size == 80U);
static_assert(
    command_layouts::drop_shadow_effect::h_blur_radius_animations_offset ==
    72U);
static_assert(command_layouts::generic_target_create::fixed_size == 36U);
static_assert(command_layouts::hwnd_target_create::fixed_size == 92U);
static_assert(command_layouts::hwnd_target_create::dpi_x_offset == 76U);
static_assert(
    command_layouts::target_update_window_settings::fixed_size == 72U);
static_assert(
    command_layouts::target_update_window_settings::gdi_blt_offset == 68U);
static_assert(command_layouts::hwnd_target_dpi_changed::fixed_size == 28U);
static_assert(command_layouts::target_set_root::h_root_offset == 8U);
static_assert(command_layouts::target_set_clear_color::fixed_size == 24U);
static_assert(
    command_layouts::target_set_clear_color::clear_color_offset == 8U);
static_assert(command_layouts::target_invalidate::fixed_size == 24U);
static_assert(command_layouts::target_set_flags::flags_offset == 8U);
static_assert(command_layouts::render_data::fixed_size == 12U);
static_assert(command_layouts::render_data::cb_data_offset == 8U);
static_assert(
    command_layouts::fixed_header_size(command::visual_set_offset) == 24U);
static_assert(
    command_layouts::fixed_header_size(command::transport_sync_flush) == 4U);
static_assert(command_layouts::channel_create_resource::fixed_size == 12U);
static_assert(command_layouts::bitmap_source::fixed_size == 16U);
static_assert(command_layouts::bitmap_source::p_i_bitmap_offset == 8U);
static_assert(command_layouts::bitmap_invalidate::fixed_size == 28U);
static_assert(command_layouts::bitmap_invalidate::dirty_rect_offset == 12U);
static_assert(command_layouts::media_player::fixed_size == 20U);
static_assert(command_layouts::media_player::p_media_offset == 8U);
static_assert(command_layouts::video_drawing::fixed_size == 48U);
static_assert(command_layouts::video_drawing::h_player_offset == 40U);
static_assert(command_layouts::double_buffered_bitmap::fixed_size == 20U);
static_assert(
    command_layouts::double_buffered_bitmap::use_back_buffer_offset == 16U);
static_assert(
    command_layouts::double_buffered_bitmap_copy_forward::fixed_size == 16U);
static_assert(command_layouts::visual_create::fixed_size == 8U);
static_assert(command_layouts::glyph_run_create::fixed_size == 76U);
static_assert(command_layouts::draw_rectangle::fixed_size == 44U);
static_assert(command_layouts::draw_line_animate::fixed_size == 52U);
static_assert(command_layouts::draw_rectangle_animate::fixed_size == 52U);
static_assert(
    command_layouts::draw_rounded_rectangle_animate::fixed_size == 76U);
static_assert(command_layouts::draw_ellipse_animate::fixed_size == 60U);
static_assert(command_layouts::draw_rounded_rectangle::fixed_size == 60U);
static_assert(command_layouts::push_effect::fixed_size == 12U);
static_assert(command_layouts::push_effect::h_effect_offset == 4U);
static_assert(command_layouts::push_effect::h_effect_input_offset == 8U);
static_assert(command_layouts::pop::fixed_size == 4U);

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

struct explicit_guideline_snapshot {
    std::uint32_t count_x{};
    std::uint32_t count_y{};
    double coordinate{};
    double offset{};
};

bool try_get_single_explicit_guideline(
    const std::vector<std::byte>& stream,
    explicit_guideline_snapshot& snapshot) {
    const auto header = read_value<progpu_native_scene_header>(stream, 0U);
    for (std::uint32_t index = 0U; index < header.resource_count; ++index) {
        const auto resource = read_value<progpu_native_scene_resource>(
            stream,
            header.resource_offset +
                index * sizeof(progpu_native_scene_resource));
        if (resource.kind != PROGPU_NATIVE_SCENE_RESOURCE_GUIDELINE_SET) {
            continue;
        }
        const auto set = read_value<progpu_native_scene_guideline_set>(
            stream, resource.payload_offset);
        if ((set.flags & PROGPU_NATIVE_SCENE_GUIDELINE_EXPLICIT_OFFSETS) ==
                0U ||
            set.guideline_x_count + set.guideline_y_count != 1U) {
            return false;
        }
        snapshot.count_x = set.guideline_x_count;
        snapshot.count_y = set.guideline_y_count;
        snapshot.coordinate = read_value<double>(
            stream, resource.payload_offset + sizeof(set));
        snapshot.offset = read_value<double>(
            stream,
            resource.payload_offset + sizeof(set) + sizeof(double));
        return true;
    }
    return false;
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

bool try_get_cached_layer(
    const std::vector<std::byte>& stream,
    progpu_native_scene_layer& layer) {
    const auto header = read_value<progpu_native_scene_header>(stream, 0U);
    for (std::uint32_t index = 0U; index < header.command_count; ++index) {
        const auto command_record = read_value<progpu_native_scene_command>(
            stream,
            header.command_offset +
                index * sizeof(progpu_native_scene_command));
        if (command_record.kind != PROGPU_NATIVE_SCENE_COMMAND_PUSH_LAYER ||
            command_record.payload_size != sizeof(progpu_native_scene_layer)) {
            continue;
        }
        const auto candidate = read_value<progpu_native_scene_layer>(
            stream, command_record.payload_offset);
        if ((candidate.flags &
                PROGPU_NATIVE_SCENE_LAYER_CACHE_CONTENT) != 0U) {
            layer = candidate;
            return true;
        }
    }
    return false;
}

std::vector<progpu_native_scene_layer> get_scene_layers(
    const std::vector<std::byte>& stream) {
    std::vector<progpu_native_scene_layer> layers;
    const auto header = read_value<progpu_native_scene_header>(stream, 0U);
    for (std::uint32_t index = 0U; index < header.command_count; ++index) {
        const auto command_record = read_value<progpu_native_scene_command>(
            stream,
            header.command_offset +
                index * sizeof(progpu_native_scene_command));
        if (command_record.kind == PROGPU_NATIVE_SCENE_COMMAND_PUSH_LAYER &&
            command_record.payload_size == sizeof(progpu_native_scene_layer)) {
            layers.push_back(read_value<progpu_native_scene_layer>(
                stream, command_record.payload_offset));
        }
    }
    return layers;
}

bool try_get_state_resource(
    const std::vector<std::byte>& stream,
    std::uint32_t resource_index,
    progpu_native_scene_state& state) {
    const auto header = read_value<progpu_native_scene_header>(stream, 0U);
    if (resource_index >= header.resource_count) {
        return false;
    }
    const auto resource = read_value<progpu_native_scene_resource>(
        stream,
        header.resource_offset +
            resource_index * sizeof(progpu_native_scene_resource));
    if (resource.kind != PROGPU_NATIVE_SCENE_RESOURCE_STATE ||
        resource.payload_size != sizeof(progpu_native_scene_state)) {
        return false;
    }
    state = read_value<progpu_native_scene_state>(
        stream, resource.payload_offset);
    return true;
}

bool try_get_brush_mask_resource(
    const std::vector<std::byte>& stream,
    std::uint32_t resource_index,
    progpu_native_scene_layer_brush_mask& mask,
    std::vector<progpu_native_scene_gradient_stop>& stops) {
    const auto header = read_value<progpu_native_scene_header>(stream, 0U);
    if (resource_index >= header.resource_count) {
        return false;
    }
    const auto resource = read_value<progpu_native_scene_resource>(
        stream,
        header.resource_offset +
            resource_index * sizeof(progpu_native_scene_resource));
    if (resource.kind != PROGPU_NATIVE_SCENE_RESOURCE_LAYER_MASK ||
        resource.payload_size !=
            sizeof(progpu_native_scene_layer_brush_mask) ||
        resource.auxiliary_size %
            sizeof(progpu_native_scene_gradient_stop) != 0U) {
        return false;
    }
    mask = read_value<progpu_native_scene_layer_brush_mask>(
        stream, resource.payload_offset);
    const std::size_t stop_count = resource.auxiliary_size /
        sizeof(progpu_native_scene_gradient_stop);
    stops.resize(stop_count);
    for (std::size_t index = 0U; index < stop_count; ++index) {
        stops[index] = read_value<progpu_native_scene_gradient_stop>(
            stream,
            resource.auxiliary_offset +
                index * sizeof(progpu_native_scene_gradient_stop));
    }
    return true;
}

bool try_get_cached_raster_state(
    const std::vector<std::byte>& stream,
    progpu_native_scene_state& state) {
    const auto header = read_value<progpu_native_scene_header>(stream, 0U);
    bool cached_layer_pending = false;
    for (std::uint32_t index = 0U; index < header.command_count; ++index) {
        const auto command_record = read_value<progpu_native_scene_command>(
            stream,
            header.command_offset +
                index * sizeof(progpu_native_scene_command));
        if (command_record.kind == PROGPU_NATIVE_SCENE_COMMAND_PUSH_LAYER &&
            command_record.payload_size == sizeof(progpu_native_scene_layer)) {
            const auto layer = read_value<progpu_native_scene_layer>(
                stream, command_record.payload_offset);
            cached_layer_pending =
                (layer.flags &
                    PROGPU_NATIVE_SCENE_LAYER_CACHE_LOCAL_SPACE) != 0U;
            continue;
        }
        if (cached_layer_pending &&
            command_record.kind == PROGPU_NATIVE_SCENE_COMMAND_SAVE) {
            return try_get_state_resource(
                stream, command_record.state_index, state);
        }
        cached_layer_pending = false;
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

void append_model3d_group(
    std::vector<std::byte>& batch,
    std::uint32_t handle,
    std::uint32_t transform,
    std::span<const std::uint32_t> children) {
    std::vector<std::byte> packet;
    append_value(packet, static_cast<std::uint32_t>(command::model3d_group));
    append_value(packet, handle);
    append_value(packet, transform);
    append_value(packet, static_cast<std::uint32_t>(children.size_bytes()));
    for (const std::uint32_t child : children) {
        append_value(packet, child);
    }
    append_value(
        batch,
        static_cast<std::uint32_t>(packet.size() + sizeof(std::uint32_t)));
    batch.insert(batch.end(), packet.begin(), packet.end());
}

void append_material_group(
    std::vector<std::byte>& batch,
    std::uint32_t handle,
    std::span<const std::uint32_t> children) {
    std::vector<std::byte> packet;
    append_value(packet, static_cast<std::uint32_t>(command::material_group));
    append_value(packet, handle);
    append_value(packet, static_cast<std::uint32_t>(children.size_bytes()));
    for (const std::uint32_t child : children) {
        append_value(packet, child);
    }
    append_value(
        batch,
        static_cast<std::uint32_t>(packet.size() + sizeof(std::uint32_t)));
    batch.insert(batch.end(), packet.begin(), packet.end());
}

void append_mesh_geometry3d(
    std::vector<std::byte>& batch,
    std::uint32_t handle,
    std::span<const std::array<float, 3U>> positions,
    std::span<const std::array<float, 3U>> normals,
    std::span<const std::array<double, 2U>> texture_coordinates,
    std::span<const std::uint32_t> indices) {
    std::vector<std::byte> packet;
    append_value(
        packet,
        static_cast<std::uint32_t>(command::mesh_geometry3d));
    append_value(packet, handle);
    append_value(packet, static_cast<std::uint32_t>(positions.size_bytes()));
    append_value(packet, static_cast<std::uint32_t>(normals.size_bytes()));
    append_value(
        packet,
        static_cast<std::uint32_t>(texture_coordinates.size_bytes()));
    append_value(packet, static_cast<std::uint32_t>(indices.size_bytes()));
    for (const auto& position : positions) {
        append_value(packet, position);
    }
    for (const auto& normal : normals) {
        append_value(packet, normal);
    }
    for (const auto& coordinate : texture_coordinates) {
        append_value(packet, coordinate);
    }
    for (const std::uint32_t index : indices) {
        append_value(packet, index);
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

bool curve_dashes_match_managed_reference_contracts() {
    namespace curve_dash = progpu::native::mil::curve_dash;
    const std::array<std::uint8_t, 1U> one_join{};
    curve_dash::run_buffer runs;

    progpu_native_path_segment quadratic{};
    quadratic.kind = PROGPU_NATIVE_PATH_SEGMENT_QUADRATIC;
    quadratic.p0 = {0.0F, 0.0F};
    quadratic.p1 = {10.0F, 10.0F};
    quadratic.p2 = {20.0F, 0.0F};
    const std::array quadratic_pattern{8.0, 4.0};
    PROGPU_REQUIRE(curve_dash::try_create_runs(
        std::span<const progpu_native_path_segment>(&quadratic, 1U),
        one_join,
        false,
        quadratic_pattern,
        0.0,
        1.0F,
        runs) == curve_dash::result::success);
    PROGPU_REQUIRE(runs.runs.size() == 2U);
    PROGPU_REQUIRE(runs.runs.front().starts_at_source_start);
    const auto quadratic_segments = runs.segments_for(runs.runs.front());
    PROGPU_REQUIRE(
        quadratic_segments.front().kind ==
        PROGPU_NATIVE_PATH_SEGMENT_QUADRATIC);
    PROGPU_REQUIRE(quadratic_segments.front().p0.x == 0.0F);
    PROGPU_REQUIRE(quadratic_segments.front().p0.y == 0.0F);

    progpu_native_path_segment cubic{};
    cubic.kind = PROGPU_NATIVE_PATH_SEGMENT_CUBIC;
    cubic.p0 = {0.0F, 0.0F};
    cubic.p1 = {10.0F, 20.0F};
    cubic.p2 = {20.0F, 20.0F};
    cubic.p3 = {30.0F, 0.0F};
    const std::array cubic_pattern{12.0, 6.0};
    PROGPU_REQUIRE(curve_dash::try_create_runs(
        std::span<const progpu_native_path_segment>(&cubic, 1U),
        one_join,
        false,
        cubic_pattern,
        0.0,
        1.0F,
        runs) == curve_dash::result::success);
    PROGPU_REQUIRE(runs.runs.size() == 3U);
    PROGPU_REQUIRE(runs.runs.front().starts_at_source_start);
    PROGPU_REQUIRE(
        runs.segments_for(runs.runs.front()).front().kind ==
        PROGPU_NATIVE_PATH_SEGMENT_CUBIC);

    progpu_native_path_segment arc{};
    arc.kind = PROGPU_NATIVE_PATH_SEGMENT_ARC;
    arc.p0 = {10.0F, 0.0F};
    arc.p1 = {-10.0F, 0.0F};
    arc.p2 = {0.0F, 0.0F};
    arc.p3 = {10.0F, 10.0F};
    arc.pad0 = std::bit_cast<std::uint32_t>(0.0F);
    arc.pad1 = std::bit_cast<std::uint32_t>(
        std::numbers::pi_v<float>);
    arc.pad2 = std::bit_cast<std::uint32_t>(0.0F);
    const std::array arc_pattern{10.0, 10.0};
    PROGPU_REQUIRE(curve_dash::try_create_runs(
        std::span<const progpu_native_path_segment>(&arc, 1U),
        one_join,
        false,
        arc_pattern,
        0.0,
        1.0F,
        runs) == curve_dash::result::success);
    PROGPU_REQUIRE(runs.runs.size() == 2U);
    PROGPU_REQUIRE(runs.runs.front().starts_at_source_start);
    const auto arc_segments = runs.segments_for(runs.runs.front());
    PROGPU_REQUIRE(
        arc_segments.front().kind ==
        PROGPU_NATIVE_PATH_SEGMENT_ARC);
    PROGPU_REQUIRE(std::bit_cast<float>(
        arc_segments.front().pad1) > 0.9F);
    PROGPU_REQUIRE(std::bit_cast<float>(
        arc_segments.front().pad1) < 1.1F);

    progpu_native_path_segment phased_line{};
    phased_line.kind = PROGPU_NATIVE_PATH_SEGMENT_LINE;
    phased_line.p0 = {0.0F, 0.0F};
    phased_line.p1 = {20.0F, 0.0F};
    const std::array odd_pattern{2.0, 1.0, 3.0};
    PROGPU_REQUIRE(curve_dash::try_create_runs(
        std::span<const progpu_native_path_segment>(&phased_line, 1U),
        one_join,
        false,
        odd_pattern,
        -1.5,
        2.0F,
        runs) == curve_dash::result::success);
    PROGPU_REQUIRE(runs.runs.size() == 3U);
    PROGPU_REQUIRE(!runs.runs.front().starts_at_source_start);
    PROGPU_REQUIRE(runs.runs.back().ends_at_source_end);
    const auto first_phased_segments =
        runs.segments_for(runs.runs.front());
    const auto last_phased_segments =
        runs.segments_for(runs.runs.back());
    PROGPU_REQUIRE(std::abs(
        first_phased_segments.front().p0.x - 3.0F) < 0.001F);
    PROGPU_REQUIRE(std::abs(
        last_phased_segments.back().p1.x - 20.0F) < 0.001F);
    PROGPU_REQUIRE(!runs.terminal_visible_point);

    progpu_native_path_segment terminal_dash_line{};
    terminal_dash_line.kind = PROGPU_NATIVE_PATH_SEGMENT_LINE;
    terminal_dash_line.p0 = {0.0F, 0.0F};
    terminal_dash_line.p1 = {4.0F, 0.0F};
    const std::array terminal_dash_pattern{2.0, 2.0};
    PROGPU_REQUIRE(curve_dash::try_create_runs(
        std::span<const progpu_native_path_segment>(
            &terminal_dash_line, 1U),
        one_join,
        false,
        terminal_dash_pattern,
        0.0,
        1.0F,
        runs) == curve_dash::result::success);
    PROGPU_REQUIRE(runs.runs.size() == 1U);
    PROGPU_REQUIRE(runs.terminal_visible_point);

    std::array<progpu_native_path_segment, 4U> square{};
    const std::array square_points{
        progpu_native_point{0.0F, 0.0F},
        progpu_native_point{10.0F, 0.0F},
        progpu_native_point{10.0F, 10.0F},
        progpu_native_point{0.0F, 10.0F}};
    for (std::size_t index = 0U; index < square.size(); ++index) {
        square[index].kind = PROGPU_NATIVE_PATH_SEGMENT_LINE;
        square[index].p0 = square_points[index];
        square[index].p1 = square_points[(index + 1U) % square.size()];
    }
    const std::array<std::uint8_t, 4U> square_joins{0U, 0U, 0U, 1U};
    const std::array seam_pattern{5.0, 2.0};
    PROGPU_REQUIRE(curve_dash::try_create_runs(
        square,
        square_joins,
        true,
        seam_pattern,
        0.0,
        1.0F,
        runs) == curve_dash::result::success);
    PROGPU_REQUIRE(runs.runs.size() == 5U);
    PROGPU_REQUIRE(!runs.runs.back().closed);
    const auto seam_segments = runs.segments_for(runs.runs.back());
    const auto seam_joins = runs.smooth_joins_for(runs.runs.back());
    PROGPU_REQUIRE(seam_segments.size() == 2U);
    PROGPU_REQUIRE(seam_joins.size() == 1U);
    PROGPU_REQUIRE(seam_joins.front() == 1U);
    PROGPU_REQUIRE(
        seam_segments.front().p1.x == 0.0F &&
        seam_segments.front().p1.y == 0.0F);
    PROGPU_REQUIRE(
        seam_segments.back().p0.x == 0.0F &&
        seam_segments.back().p0.y == 0.0F);

    std::array<progpu_native_path_segment, 256U> dense_lines{};
    std::array<std::uint8_t, 256U> dense_joins{};
    for (std::size_t index = 0U; index < dense_lines.size(); ++index) {
        dense_lines[index].kind = PROGPU_NATIVE_PATH_SEGMENT_LINE;
        dense_lines[index].p0 = {static_cast<float>(index), 0.0F};
        dense_lines[index].p1 = {static_cast<float>(index + 1U), 0.0F};
        dense_joins[index] = 1U;
    }
    const std::array dense_pattern{3.0, 1.0};
    PROGPU_REQUIRE(curve_dash::try_create_runs(
        dense_lines,
        dense_joins,
        false,
        dense_pattern,
        0.0,
        1.0F,
        runs) == curve_dash::result::success);
    PROGPU_REQUIRE(runs.runs.size() == 64U);
    PROGPU_REQUIRE(runs.segments.size() == 192U);
    PROGPU_REQUIRE(runs.smooth_joins.size() == 128U);
    const auto* const run_storage = runs.runs.data();
    const auto* const segment_storage = runs.segments.data();
    const auto* const join_storage = runs.smooth_joins.data();
    const std::size_t run_capacity = runs.runs.capacity();
    const std::size_t segment_capacity = runs.segments.capacity();
    const std::size_t join_capacity = runs.smooth_joins.capacity();
    for (std::size_t iteration = 0U; iteration < 32U; ++iteration) {
        PROGPU_REQUIRE(curve_dash::try_create_runs(
            dense_lines,
            dense_joins,
            false,
            dense_pattern,
            0.0,
            1.0F,
            runs) == curve_dash::result::success);
        PROGPU_REQUIRE(runs.runs.size() == 64U);
        PROGPU_REQUIRE(runs.segments.size() == 192U);
        PROGPU_REQUIRE(runs.smooth_joins.size() == 128U);
        PROGPU_REQUIRE(runs.runs.data() == run_storage);
        PROGPU_REQUIRE(runs.segments.data() == segment_storage);
        PROGPU_REQUIRE(runs.smooth_joins.data() == join_storage);
        PROGPU_REQUIRE(runs.runs.capacity() == run_capacity);
        PROGPU_REQUIRE(runs.segments.capacity() == segment_capacity);
        PROGPU_REQUIRE(runs.smooth_joins.capacity() == join_capacity);
    }
    return true;
}

bool semantic_path_strokes_preserve_curves_and_forced_joins() {
    namespace semantic_path_stroke =
        progpu::native::semantic_path_stroke;
    const std::array<progpu_native_path_segment, 2U> segments = {{
        {
            {0.0F, 0.0F},
            {8.0F, 0.0F},
            {},
            {},
            PROGPU_NATIVE_PATH_SEGMENT_LINE,
            0U,
            0U,
            0U},
        {
            {8.0F, 0.0F},
            {12.0F, 0.0F},
            {12.0F, 8.0F},
            {8.0F, 8.0F},
            PROGPU_NATIVE_PATH_SEGMENT_CUBIC,
            0U,
            0U,
            0U}}};
    const std::array<std::uint8_t, 2U> smooth_joins = {1U, 0U};
    semantic_path_stroke::style style{};
    style.transform = {1.0F, 0.0F, 0.0F, 1.0F, 0.0F, 0.0F};
    style.thickness = 3.0F;
    style.miter_limit = 4.0F;
    style.line_join = PROGPU_NATIVE_STROKE_JOIN_BEVEL;
    progpu::native::mil::curve_dash::run_buffer dash_scratch;
    std::vector<progpu_native_geometry_primitive> primitives;
    std::vector<std::uint32_t> brushes;
    PROGPU_REQUIRE(
        semantic_path_stroke::compile(
            segments,
            smooth_joins,
            false,
            {},
            style,
            7U,
            dash_scratch,
            primitives,
            brushes) == semantic_path_stroke::result::success);
    PROGPU_REQUIRE(primitives.size() == 3U && brushes.size() == 3U);
    PROGPU_REQUIRE(
        primitives[0].kind == PROGPU_NATIVE_GEOMETRY_LINE &&
        primitives[1].kind == PROGPU_NATIVE_GEOMETRY_PATH_JOIN &&
        primitives[2].kind == PROGPU_NATIVE_GEOMETRY_CUBIC_BEZIER);
    PROGPU_REQUIRE(
        ((primitives[1].flags >>
            PROGPU_NATIVE_PRIMITIVE_START_CAP_SHIFT) & 0x3U) ==
            PROGPU_NATIVE_STROKE_JOIN_ROUND &&
        primitives[2].stroke_thickness == 3.0F &&
        std::ranges::all_of(
            brushes,
            [](std::uint32_t brush) { return brush == 7U; }));

    constexpr float circle_control = 5.5228477F;
    const std::array<progpu_native_path_segment, 4U> closed_cubics = {{
        {{10.0F, 0.0F},
            {10.0F, circle_control},
            {circle_control, 10.0F},
            {0.0F, 10.0F},
            PROGPU_NATIVE_PATH_SEGMENT_CUBIC,
            0U,
            0U,
            0U},
        {{0.0F, 10.0F},
            {-circle_control, 10.0F},
            {-10.0F, circle_control},
            {-10.0F, 0.0F},
            PROGPU_NATIVE_PATH_SEGMENT_CUBIC,
            0U,
            0U,
            0U},
        {{-10.0F, 0.0F},
            {-10.0F, -circle_control},
            {-circle_control, -10.0F},
            {0.0F, -10.0F},
            PROGPU_NATIVE_PATH_SEGMENT_CUBIC,
            0U,
            0U,
            0U},
        {{0.0F, -10.0F},
            {circle_control, -10.0F},
            {10.0F, -circle_control},
            {10.0F, 0.0F},
            PROGPU_NATIVE_PATH_SEGMENT_CUBIC,
            0U,
            0U,
            0U}}};
    const std::array<std::uint8_t, 4U> closed_cubic_joins{};
    const std::array closed_cubic_dashes{2.0, 1.0, 0.5, 1.0};
    primitives.clear();
    brushes.clear();
    style.start_cap = PROGPU_NATIVE_STROKE_CAP_ROUND;
    style.end_cap = PROGPU_NATIVE_STROKE_CAP_SQUARE;
    style.dash_cap = PROGPU_NATIVE_STROKE_CAP_TRIANGLE;
    PROGPU_REQUIRE(
        semantic_path_stroke::compile(
            closed_cubics,
            closed_cubic_joins,
            true,
            closed_cubic_dashes,
            style,
            9U,
            dash_scratch,
            primitives,
            brushes) == semantic_path_stroke::result::success);
    PROGPU_REQUIRE(!primitives.empty() &&
        primitives.size() == brushes.size());

    progpu_native_path_segment device_curve{};
    device_curve.kind = PROGPU_NATIVE_PATH_SEGMENT_CUBIC;
    device_curve.p0 = {0.0F, 0.0F};
    device_curve.p1 = {10.0F / 3.0F, 0.0F};
    device_curve.p2 = {20.0F / 3.0F, 0.0F};
    device_curve.p3 = {10.0F, 0.0F};
    const std::array<std::uint8_t, 1U> device_join{};
    const std::array device_pattern{2.0, 100.0};
    style = {};
    style.transform = {2.0F, 0.0F, 0.0F, 0.5F, 3.0F, -2.0F};
    style.thickness = 2.0F;
    style.miter_limit = 4.0F;

    primitives.clear();
    brushes.clear();
    PROGPU_REQUIRE(
        semantic_path_stroke::compile(
            std::span<const progpu_native_path_segment>(&device_curve, 1U),
            device_join,
            false,
            device_pattern,
            style,
            11U,
            dash_scratch,
            primitives,
            brushes) == semantic_path_stroke::result::success);
    PROGPU_REQUIRE(primitives.size() == 1U && brushes.size() == 1U);
    PROGPU_REQUIRE(
        primitives.front().kind == PROGPU_NATIVE_GEOMETRY_CUBIC_BEZIER &&
        std::abs(primitives.front().p3.x - 4.0F) < 0.001F &&
        primitives.front().stroke_thickness == 2.0F &&
        primitives.front().flags == 0U);

    style.primitive_flags =
        PROGPU_NATIVE_PRIMITIVE_FLAG_FIXED_DEVICE_STROKE;
    primitives.clear();
    brushes.clear();
    PROGPU_REQUIRE(
        semantic_path_stroke::compile(
            std::span<const progpu_native_path_segment>(&device_curve, 1U),
            device_join,
            false,
            device_pattern,
            style,
            12U,
            dash_scratch,
            primitives,
            brushes) == semantic_path_stroke::result::success);
    PROGPU_REQUIRE(primitives.size() == 1U && brushes.size() == 1U);
    PROGPU_REQUIRE(
        primitives.front().kind == PROGPU_NATIVE_GEOMETRY_CUBIC_BEZIER &&
        std::abs(primitives.front().p3.x - 2.0F) < 0.001F &&
        primitives.front().stroke_thickness == 2.0F &&
        (primitives.front().flags &
            PROGPU_NATIVE_PRIMITIVE_FLAG_FIXED_DEVICE_STROKE) != 0U);

    style.thickness = 0.0F;
    style.primitive_flags = PROGPU_NATIVE_PRIMITIVE_FLAG_HAIRLINE;
    primitives.clear();
    brushes.clear();
    PROGPU_REQUIRE(
        semantic_path_stroke::compile(
            std::span<const progpu_native_path_segment>(&device_curve, 1U),
            device_join,
            false,
            device_pattern,
            style,
            13U,
            dash_scratch,
            primitives,
            brushes) == semantic_path_stroke::result::success);
    PROGPU_REQUIRE(primitives.size() == 1U && brushes.size() == 1U);
    PROGPU_REQUIRE(
        primitives.front().kind == PROGPU_NATIVE_GEOMETRY_CUBIC_BEZIER &&
        std::abs(primitives.front().p3.x - 1.0F) < 0.001F &&
        primitives.front().stroke_thickness == 0.0F &&
        (primitives.front().flags &
            PROGPU_NATIVE_PRIMITIVE_FLAG_HAIRLINE) != 0U);

    progpu_native_path_segment terminal_line{};
    terminal_line.kind = PROGPU_NATIVE_PATH_SEGMENT_LINE;
    terminal_line.p0 = {0.0F, 0.0F};
    terminal_line.p1 = {4.0F, 0.0F};
    const std::array terminal_pattern{2.0, 2.0};
    style = {};
    style.transform = {1.0F, 0.0F, 0.0F, 1.0F, 0.0F, 0.0F};
    style.thickness = 1.0F;
    style.miter_limit = 4.0F;
    style.dash_cap = PROGPU_NATIVE_STROKE_CAP_SQUARE;
    primitives.clear();
    brushes.clear();
    PROGPU_REQUIRE(
        semantic_path_stroke::compile(
            std::span<const progpu_native_path_segment>(&terminal_line, 1U),
            device_join,
            false,
            terminal_pattern,
            style,
            15U,
            dash_scratch,
            primitives,
            brushes) == semantic_path_stroke::result::success);
    PROGPU_REQUIRE(primitives.size() == 3U && brushes.size() == 3U);
    PROGPU_REQUIRE(
        primitives.back().kind == PROGPU_NATIVE_GEOMETRY_PATH_CAP &&
        primitives.back().p0.x == 4.0F &&
        primitives.back().p2.x == 1.0F &&
        ((primitives.back().flags >>
            PROGPU_NATIVE_PRIMITIVE_START_CAP_SHIFT) & 0x3U) ==
            PROGPU_NATIVE_STROKE_CAP_SQUARE);

    style.thickness = 2.0F;
    style.primitive_flags =
        PROGPU_NATIVE_PRIMITIVE_FLAG_HAIRLINE |
        PROGPU_NATIVE_PRIMITIVE_FLAG_FIXED_DEVICE_STROKE;
    PROGPU_REQUIRE(
        semantic_path_stroke::compile(
            segments,
            smooth_joins,
            false,
            {},
            style,
            14U,
            dash_scratch,
            primitives,
            brushes) == semantic_path_stroke::result::invalid &&
        primitives.size() == 3U && brushes.size() == 3U);
    return true;
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

bool canonical_hwnd_target_uses_portable_surface_state() {
    constexpr std::uint32_t visual = 1U;
    constexpr std::uint32_t content = 2U;
    constexpr std::uint32_t target = 3U;
    constexpr std::uint32_t brush = 4U;

    std::vector<std::byte> batch;
    append_create(batch, visual, 39U);
    append_create(batch, content, 43U);
    append_create(batch, target, 46U);
    append_create(batch, brush, 75U);
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
    std::vector<std::byte> nested;
    append_command(
        nested,
        command::draw_rectangle,
        0.0,
        0.0,
        32.0,
        24.0,
        brush,
        0U);
    append_render_data(batch, content, nested);
    append_command(
        batch,
        command::hwnd_target_create,
        target,
        std::uint64_t{0U},
        std::uint64_t{0U},
        std::uint64_t{0U},
        640U,
        480U,
        std::array<float, 4U>{0.1F, 0.2F, 0.3F, 1.0F},
        0x21U,
        0U,
        0U,
        0U,
        std::int32_t{-4},
        1.25,
        1.5);
    append_command(batch, command::target_set_root, target, visual);

    channel state;
    PROGPU_REQUIRE(state.apply(batch) == status::success);
    progpu::native::mil::target_snapshot snapshot{};
    PROGPU_REQUIRE(state.try_get_target(target, snapshot));
    PROGPU_REQUIRE(snapshot.root_handle == visual);
    PROGPU_REQUIRE(snapshot.clear_red == 0.1F);
    PROGPU_REQUIRE(snapshot.clear_green == 0.2F);
    PROGPU_REQUIRE(snapshot.clear_blue == 0.3F);
    PROGPU_REQUIRE(snapshot.clear_alpha == 1.0F);
    PROGPU_REQUIRE(snapshot.flags == 0x21U);

    std::vector<std::byte> stream;
    progpu::native::mil::scene_metrics metrics{};
    PROGPU_REQUIRE(
        state.build_scene(target, 7018U, 1U, stream, &metrics) ==
        status::success);
    PROGPU_REQUIRE(metrics.visual_count == 1U);
    PROGPU_REQUIRE(metrics.rectangle_count == 1U);

    std::vector<std::byte> host_state;
    append_command(
        host_state,
        command::hwnd_target_suppress_layered,
        target,
        1U);
    append_command(
        host_state,
        command::hwnd_target_dpi_changed,
        target,
        2.0,
        2.25,
        1U);
    append_command(
        host_state,
        command::target_update_window_settings,
        target,
        std::array<std::int32_t, 4U>{-10, 20, 630, 500},
        2U,
        0x7U,
        0.75F,
        0U,
        1U,
        0U,
        progpu_native_color{0.0F, 0.0F, 0.0F, 0.0F},
        7U,
        0U);
    PROGPU_REQUIRE(state.apply(host_state) == status::success);
    const std::uint64_t disabled_generation =
        state.resource_generation(target);
    PROGPU_REQUIRE(
        state.build_scene(target, 7018U, 2U, stream, &metrics) ==
        status::success);
    PROGPU_REQUIRE(metrics.visual_count == 0U);
    PROGPU_REQUIRE(metrics.rectangle_count == 0U);

    std::vector<std::byte> stale_enable;
    append_command(
        stale_enable,
        command::target_update_window_settings,
        target,
        std::array<std::int32_t, 4U>{0, 0, 640, 480},
        0U,
        0U,
        1.0F,
        0U,
        0U,
        1U,
        progpu_native_color{},
        6U,
        0U);
    PROGPU_REQUIRE(state.apply(stale_enable) == status::success);
    PROGPU_REQUIRE(
        state.resource_generation(target) == disabled_generation);
    PROGPU_REQUIRE(
        state.build_scene(target, 7018U, 3U, stream, &metrics) ==
        status::success);
    PROGPU_REQUIRE(metrics.rectangle_count == 0U);

    std::vector<std::byte> current_enable;
    append_command(
        current_enable,
        command::target_update_window_settings,
        target,
        std::array<std::int32_t, 4U>{0, 0, 640, 480},
        0U,
        0U,
        1.0F,
        0U,
        0U,
        1U,
        progpu_native_color{},
        7U,
        0U);
    PROGPU_REQUIRE(state.apply(current_enable) == status::success);
    PROGPU_REQUIRE(
        state.build_scene(target, 7018U, 4U, stream, &metrics) ==
        status::success);
    PROGPU_REQUIRE(metrics.rectangle_count == 1U);

    const std::uint64_t valid_generation =
        state.resource_generation(target);
    std::vector<std::byte> raw_hwnd;
    append_command(
        raw_hwnd,
        command::hwnd_target_create,
        target,
        std::uint64_t{1U},
        std::uint64_t{0U},
        std::uint64_t{0U},
        640U,
        480U,
        std::array<float, 4U>{0.0F, 0.0F, 0.0F, 1.0F},
        0U,
        0U,
        0U,
        0U,
        std::int32_t{0},
        1.0,
        1.0);
    PROGPU_REQUIRE(state.apply(raw_hwnd) == status::invalid_argument);
    PROGPU_REQUIRE(state.resource_generation(target) == valid_generation);

    std::vector<std::byte> invalid_settings;
    append_command(
        invalid_settings,
        command::target_update_window_settings,
        target,
        std::array<std::int32_t, 4U>{0, 0, 640, 480},
        3U,
        0U,
        1.0F,
        0U,
        0U,
        1U,
        progpu_native_color{},
        7U,
        0U);
    PROGPU_REQUIRE(
        state.apply(invalid_settings) == status::malformed_batch);
    PROGPU_REQUIRE(state.resource_generation(target) == valid_generation);
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
    constexpr std::uint32_t double_resource_type = 49U;
    constexpr std::uint32_t solid_brush_type = 75U;
    constexpr std::uint32_t root = 1U;
    constexpr std::uint32_t child = 2U;
    constexpr std::uint32_t content = 3U;
    constexpr std::uint32_t target = 4U;
    constexpr std::uint32_t brush = 5U;
    constexpr std::uint32_t opacity_animation = 6U;
    constexpr std::uint32_t transform = 7U;
    constexpr std::uint32_t relative_transform = 8U;

    std::vector<std::byte> batch;
    append_create(batch, root, visual_type);
    append_create(batch, child, visual_type);
    append_create(batch, content, render_data_type);
    append_create(batch, target, target_type);
    append_create(batch, brush, solid_brush_type);
    append_create(batch, opacity_animation, double_resource_type);
    append_create(batch, transform, 66U);
    append_create(batch, relative_transform, 62U);
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
    append_command(
        batch, command::double_resource, opacity_animation, 0.5);
    append_command(
        batch,
        command::matrix_transform,
        transform,
        2.0,
        0.0,
        0.0,
        3.0,
        17.0,
        19.0,
        0U);
    append_command(
        batch,
        command::translate_transform,
        relative_transform,
        0.25,
        0.5,
        0U,
        0U);

    const progpu_native_color color{0.25F, 0.5F, 0.75F, 0.9F};
    append_command(
        batch,
        command::solid_color_brush,
        brush,
        0.75,
        color,
        0U,
        transform,
        relative_transform,
        0U);
    std::vector<std::byte> nested;
    append_command(
        nested,
        command::push_opacity_animate,
        0.75,
        opacity_animation,
        0U);
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
    PROGPU_REQUIRE(header.resource_count == 6U);

    bool found_child_state = false;
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
    const auto opacity_layers = get_scene_layers(stream);
    PROGPU_REQUIRE(opacity_layers.size() == 1U);
    PROGPU_REQUIRE(
        (opacity_layers[0].flags &
            PROGPU_NATIVE_SCENE_LAYER_FORCE_ISOLATION) != 0U);
    PROGPU_REQUIRE(opacity_layers[0].opacity == 0.5F);
    PROGPU_REQUIRE(
        opacity_layers[0].mask_resource_index ==
        PROGPU_NATIVE_SCENE_NO_INDEX);
    PROGPU_REQUIRE(
        opacity_layers[0].effect_resource_index ==
        PROGPU_NATIVE_SCENE_NO_INDEX);
    PROGPU_REQUIRE(found_rectangle);
    PROGPU_REQUIRE(found_ellipse);
    PROGPU_REQUIRE(found_rounded_rectangle);
    PROGPU_REQUIRE(found_brush);

    std::vector<std::byte> invalid_brush_transform;
    append_command(
        invalid_brush_transform,
        command::solid_color_brush,
        brush,
        0.75,
        color,
        0U,
        opacity_animation,
        relative_transform,
        0U);
    PROGPU_REQUIRE(
        state.apply(invalid_brush_transform) == status::invalid_handle);
    std::vector<std::byte> delete_brush_transform;
    append_command(
        delete_brush_transform,
        command::channel_delete_resource,
        transform,
        66U);
    PROGPU_REQUIRE(
        state.apply(delete_brush_transform) == status::invalid_graph);

    std::vector<std::byte> opacity_update;
    append_command(
        opacity_update,
        command::double_resource,
        opacity_animation,
        0.25);
    PROGPU_REQUIRE(state.apply(opacity_update) == status::success);
    std::vector<std::byte> updated_stream;
    PROGPU_REQUIRE(
        state.build_scene(
            target, 9001U, 8U, updated_stream, &metrics) ==
        status::success);
    const auto updated_opacity_layers = get_scene_layers(updated_stream);
    PROGPU_REQUIRE(updated_opacity_layers.size() == 1U);
    PROGPU_REQUIRE(updated_opacity_layers[0].opacity == 0.25F);

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

bool animated_value_resources_drive_render_data_primitives() {
    constexpr std::uint32_t visual = 101U;
    constexpr std::uint32_t content = 102U;
    constexpr std::uint32_t target = 103U;
    constexpr std::uint32_t brush = 104U;
    constexpr std::uint32_t rectangle_animation = 105U;
    constexpr std::uint32_t center_animation = 106U;
    constexpr std::uint32_t first_point_animation = 107U;
    constexpr std::uint32_t second_point_animation = 108U;
    constexpr std::uint32_t radius_animation = 109U;
    constexpr std::uint32_t color_animation = 110U;
    constexpr std::uint32_t size_animation = 111U;
    constexpr std::uint32_t point3d_animation = 112U;
    constexpr std::uint32_t vector3d_animation = 113U;
    constexpr std::uint32_t quaternion_animation = 114U;
    constexpr std::uint32_t brush_opacity_animation = 115U;

    std::vector<std::byte> batch;
    append_create(batch, visual, 39U);
    append_create(batch, content, 43U);
    append_create(batch, target, 47U);
    append_create(batch, brush, 75U);
    append_create(batch, rectangle_animation, 52U);
    append_create(batch, center_animation, 51U);
    append_create(batch, first_point_animation, 51U);
    append_create(batch, second_point_animation, 51U);
    append_create(batch, radius_animation, 49U);
    append_create(batch, color_animation, 50U);
    append_create(batch, size_animation, 53U);
    append_create(batch, point3d_animation, 55U);
    append_create(batch, vector3d_animation, 56U);
    append_create(batch, quaternion_animation, 57U);
    append_create(batch, brush_opacity_animation, 49U);
    append_command(batch, command::visual_create, visual);
    const progpu_native_color color{0.2F, 0.4F, 0.6F, 1.0F};
    append_command(
        batch,
        command::solid_color_brush,
        brush,
        1.0,
        color,
        brush_opacity_animation,
        0U,
        0U,
        color_animation);
    append_command(
        batch,
        command::rect_resource,
        rectangle_animation,
        10.0,
        20.0,
        30.0,
        40.0);
    append_command(batch, command::point_resource, center_animation, 50.0, 60.0);
    append_command(
        batch, command::point_resource, first_point_animation, 3.0, 4.0);
    append_command(
        batch, command::point_resource, second_point_animation, 13.0, 14.0);
    append_command(batch, command::double_resource, radius_animation, 6.0);
    append_command(
        batch,
        command::double_resource,
        brush_opacity_animation,
        0.5);
    append_command(
        batch,
        command::color_resource,
        color_animation,
        std::array<float, 4U>{0.1F, 0.2F, 0.3F, 0.4F});
    append_command(
        batch,
        command::size_resource,
        size_animation,
        std::array<double, 2U>{7.0, 8.0});
    append_command(
        batch,
        command::point3d_resource,
        point3d_animation,
        std::array<float, 3U>{1.0F, 2.0F, 3.0F});
    append_command(
        batch,
        command::vector3d_resource,
        vector3d_animation,
        std::array<float, 3U>{4.0F, 5.0F, 6.0F});
    append_command(
        batch,
        command::quaternion_resource,
        quaternion_animation,
        std::array<float, 4U>{0.0F, 0.0F, 0.0F, 1.0F});

    std::vector<std::byte> nested;
    append_command(
        nested,
        command::draw_line_animate,
        0.0,
        0.0,
        1.0,
        1.0,
        0U,
        first_point_animation,
        second_point_animation,
        0U);
    append_command(
        nested,
        command::draw_rectangle_animate,
        1.0,
        2.0,
        3.0,
        4.0,
        brush,
        0U,
        rectangle_animation,
        0U);
    append_command(
        nested,
        command::draw_rounded_rectangle_animate,
        1.0,
        2.0,
        3.0,
        4.0,
        1.0,
        1.0,
        brush,
        0U,
        rectangle_animation,
        radius_animation,
        radius_animation,
        0U);
    append_command(
        nested,
        command::draw_ellipse_animate,
        1.0,
        2.0,
        3.0,
        4.0,
        brush,
        0U,
        center_animation,
        radius_animation,
        radius_animation,
        0U);
    append_render_data(batch, content, nested);
    append_command(batch, command::visual_set_content, visual, content);
    append_command(
        batch,
        command::generic_target_create,
        target,
        std::uint64_t{0U},
        std::uint64_t{0U},
        640U,
        480U,
        0U);
    append_command(batch, command::target_set_root, target, visual);

    channel state;
    PROGPU_REQUIRE(state.apply(batch) == status::success);
    std::vector<std::byte> stream;
    progpu::native::mil::scene_metrics metrics{};
    PROGPU_REQUIRE(
        state.build_scene(target, 9050U, 1U, stream, &metrics) ==
        status::success);
    PROGPU_REQUIRE(metrics.rectangle_count == 1U);
    PROGPU_REQUIRE(metrics.rounded_rectangle_count == 1U);
    PROGPU_REQUIRE(metrics.ellipse_count == 1U);

    bool found_rectangle = false;
    bool found_rounded = false;
    bool found_ellipse = false;
    bool found_animated_brush = false;
    const auto header = read_value<progpu_native_scene_header>(stream, 0U);
    for (std::uint32_t index = 0U; index < header.resource_count; ++index) {
        const auto resource = read_value<progpu_native_scene_resource>(
            stream,
            header.resource_offset +
                index * sizeof(progpu_native_scene_resource));
        if (resource.kind == PROGPU_NATIVE_SCENE_RESOURCE_BRUSH_TABLE) {
            const auto scene_brush = read_value<progpu_native_scene_brush>(
                stream,
                resource.payload_offset);
            if (scene_brush.opacity == 0.5F &&
                scene_brush.colors[0].r == 0.1F &&
                scene_brush.colors[0].g == 0.2F &&
                scene_brush.colors[0].b == 0.3F &&
                scene_brush.colors[0].a == 0.4F) {
                found_animated_brush = true;
            }
            continue;
        }
        if (resource.kind != PROGPU_NATIVE_SCENE_RESOURCE_ANALYTIC_BATCH) {
            continue;
        }
        PROGPU_REQUIRE(
            resource.payload_size % sizeof(progpu_native_analytic_primitive) ==
            0U);
        const std::uint32_t primitive_count = resource.payload_size /
            sizeof(progpu_native_analytic_primitive);
        for (std::uint32_t primitive_index = 0U;
             primitive_index < primitive_count;
             ++primitive_index) {
            const auto primitive = read_value<progpu_native_analytic_primitive>(
                stream,
                resource.payload_offset +
                    primitive_index *
                        sizeof(progpu_native_analytic_primitive));
            if (primitive.kind == PROGPU_NATIVE_PRIMITIVE_RECTANGLE &&
                primitive.x == 10.0F && primitive.y == 20.0F &&
                primitive.width == 30.0F && primitive.height == 40.0F) {
                found_rectangle = true;
            } else if (
                primitive.kind == PROGPU_NATIVE_PRIMITIVE_ROUNDED_RECTANGLE &&
                primitive.x == 10.0F && primitive.y == 20.0F &&
                primitive.width == 30.0F && primitive.height == 40.0F &&
                primitive.corner_radius == 6.0F) {
                found_rounded = true;
            } else if (primitive.kind == PROGPU_NATIVE_PRIMITIVE_ELLIPSE &&
                primitive.x == 44.0F && primitive.y == 54.0F &&
                primitive.width == 12.0F && primitive.height == 12.0F) {
                found_ellipse = true;
            }
        }
    }
    PROGPU_REQUIRE(found_rectangle);
    PROGPU_REQUIRE(found_rounded);
    PROGPU_REQUIRE(found_ellipse);
    PROGPU_REQUIRE(found_animated_brush);

    std::vector<std::byte> brush_animation_update;
    append_command(
        brush_animation_update,
        command::double_resource,
        brush_opacity_animation,
        0.75);
    append_command(
        brush_animation_update,
        command::color_resource,
        color_animation,
        std::array<float, 4U>{0.8F, 0.6F, 0.4F, 0.2F});
    PROGPU_REQUIRE(
        state.apply(brush_animation_update) == status::success);
    PROGPU_REQUIRE(
        state.build_scene(target, 9050U, 2U, stream, &metrics) ==
        status::success);
    bool found_updated_brush = false;
    const auto updated_header = read_value<progpu_native_scene_header>(
        stream,
        0U);
    for (std::uint32_t index = 0U;
         index < updated_header.resource_count;
         ++index) {
        const auto resource = read_value<progpu_native_scene_resource>(
            stream,
            updated_header.resource_offset +
                index * sizeof(progpu_native_scene_resource));
        if (resource.kind != PROGPU_NATIVE_SCENE_RESOURCE_BRUSH_TABLE) {
            continue;
        }
        const auto scene_brush = read_value<progpu_native_scene_brush>(
            stream,
            resource.payload_offset);
        found_updated_brush = scene_brush.opacity == 0.75F &&
            scene_brush.colors[0].r == 0.8F &&
            scene_brush.colors[0].g == 0.6F &&
            scene_brush.colors[0].b == 0.4F &&
            scene_brush.colors[0].a == 0.2F;
        break;
    }
    PROGPU_REQUIRE(found_updated_brush);

    std::vector<std::byte> delete_animation;
    append_command(
        delete_animation,
        command::channel_delete_resource,
        color_animation,
        50U);
    PROGPU_REQUIRE(
        state.apply(delete_animation) == status::invalid_graph);

    const auto generation = state.resource_generation(rectangle_animation);
    std::vector<std::byte> invalid;
    append_command(
        invalid,
        command::rect_resource,
        rectangle_animation,
        0.0,
        0.0,
        std::numeric_limits<double>::quiet_NaN(),
        1.0);
    PROGPU_REQUIRE(state.apply(invalid) == status::malformed_batch);
    PROGPU_REQUIRE(
        state.resource_generation(rectangle_animation) == generation);
    return true;
}

bool animated_fixed_geometry_resources_drive_retained_geometry() {
    constexpr std::uint32_t visual = 1U;
    constexpr std::uint32_t content = 2U;
    constexpr std::uint32_t target = 3U;
    constexpr std::uint32_t brush = 4U;
    constexpr std::uint32_t pen = 5U;
    constexpr std::uint32_t cache = 6U;
    constexpr std::uint32_t line = 7U;
    constexpr std::uint32_t rectangle = 8U;
    constexpr std::uint32_t ellipse = 9U;
    constexpr std::uint32_t start = 10U;
    constexpr std::uint32_t end = 11U;
    constexpr std::uint32_t rect = 12U;
    constexpr std::uint32_t center = 13U;
    constexpr std::uint32_t radius_x = 14U;
    constexpr std::uint32_t radius_y = 15U;

    std::vector<std::byte> batch;
    append_create(batch, visual, 39U);
    append_create(batch, content, 43U);
    append_create(batch, target, 47U);
    append_create(batch, brush, 75U);
    append_create(batch, pen, 85U);
    append_create(batch, cache, 94U);
    append_create(batch, line, 68U);
    append_create(batch, rectangle, 69U);
    append_create(batch, ellipse, 70U);
    append_create(batch, start, 51U);
    append_create(batch, end, 51U);
    append_create(batch, rect, 52U);
    append_create(batch, center, 51U);
    append_create(batch, radius_x, 49U);
    append_create(batch, radius_y, 49U);
    append_command(batch, command::visual_create, visual);
    append_command(batch, command::visual_set_content, visual, content);
    append_command(
        batch,
        command::solid_color_brush,
        brush,
        1.0,
        progpu_native_color{0.2F, 0.6F, 0.9F, 1.0F},
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
        0U,
        0U,
        1U,
        0U,
        0U);
    append_command(batch, command::point_resource, start, 2.0, 3.0);
    append_command(batch, command::point_resource, end, 8.0, 9.0);
    append_command(
        batch, command::rect_resource, rect, 10.0, 20.0, 30.0, 40.0);
    append_command(batch, command::point_resource, center, 50.0, 60.0);
    append_command(batch, command::double_resource, radius_x, 4.0);
    append_command(batch, command::double_resource, radius_y, 4.0);
    append_command(
        batch,
        command::line_geometry,
        line,
        0.0,
        0.0,
        1.0,
        1.0,
        0U,
        start,
        end);
    append_command(
        batch,
        command::rectangle_geometry,
        rectangle,
        1.0,
        1.0,
        0.0,
        0.0,
        1.0,
        1.0,
        0U,
        radius_x,
        radius_y,
        rect);
    append_command(
        batch,
        command::ellipse_geometry,
        ellipse,
        1.0,
        1.0,
        0.0,
        0.0,
        0U,
        radius_x,
        radius_y,
        center);
    std::vector<std::byte> nested;
    append_command(nested, command::draw_geometry, 0U, pen, line, 0U);
    append_command(
        nested, command::draw_geometry, brush, pen, rectangle, 0U);
    append_command(
        nested, command::draw_geometry, brush, pen, ellipse, 0U);
    append_render_data(batch, content, nested);
    append_command(batch, command::bitmap_cache, cache, 1.0, 0U, 0U, 0U);
    append_command(batch, command::visual_set_cache_mode, visual, cache);
    append_command(
        batch,
        command::generic_target_create,
        target,
        std::uint64_t{0U},
        std::uint64_t{0U},
        128U,
        128U,
        0U);
    append_command(batch, command::target_set_root, target, visual);

    channel state;
    PROGPU_REQUIRE(state.apply(batch) == status::success);
    PROGPU_REQUIRE(
        state.set_visual_cache_bounds(
            visual, 0.0, 0.0, 100.0, 100.0) == status::success);
    std::vector<std::byte> stream;
    progpu::native::mil::scene_metrics metrics{};
    PROGPU_REQUIRE(
        state.build_scene(target, 9051U, 1U, stream, &metrics) ==
        status::success);
    PROGPU_REQUIRE(metrics.line_count == 1U);
    PROGPU_REQUIRE(metrics.rounded_rectangle_count == 1U);
    PROGPU_REQUIRE(metrics.ellipse_count == 1U);
    progpu_native_scene_layer initial_cache{};
    PROGPU_REQUIRE(try_get_cached_layer(stream, initial_cache));

    bool found_line = false;
    bool found_rectangle = false;
    bool found_ellipse = false;
    const auto verify_scene = [&](bool updated) {
        const auto header = read_value<progpu_native_scene_header>(stream, 0U);
        for (std::uint32_t index = 0U; index < header.resource_count; ++index) {
            const auto resource = read_value<progpu_native_scene_resource>(
                stream,
                header.resource_offset +
                    index * sizeof(progpu_native_scene_resource));
            if (resource.kind == PROGPU_NATIVE_SCENE_RESOURCE_GEOMETRY_BATCH) {
                const std::size_t count = resource.payload_size /
                    sizeof(progpu_native_geometry_primitive);
                for (std::size_t primitive_index = 0U;
                     primitive_index < count;
                     ++primitive_index) {
                    const auto primitive =
                        read_value<progpu_native_geometry_primitive>(
                            stream,
                            resource.payload_offset + primitive_index *
                                sizeof(progpu_native_geometry_primitive));
                    if (primitive.kind == PROGPU_NATIVE_GEOMETRY_LINE &&
                        primitive.p0.x == (updated ? 4.0F : 2.0F) &&
                        primitive.p0.y == (updated ? 5.0F : 3.0F) &&
                        primitive.p1.x == (updated ? 12.0F : 8.0F) &&
                        primitive.p1.y == (updated ? 13.0F : 9.0F)) {
                        found_line = true;
                    }
                }
            } else if (
                resource.kind == PROGPU_NATIVE_SCENE_RESOURCE_ANALYTIC_BATCH) {
                const std::size_t count = resource.payload_size /
                    sizeof(progpu_native_analytic_primitive);
                for (std::size_t primitive_index = 0U;
                     primitive_index < count;
                     ++primitive_index) {
                    const auto primitive =
                        read_value<progpu_native_analytic_primitive>(
                            stream,
                            resource.payload_offset + primitive_index *
                                sizeof(progpu_native_analytic_primitive));
                    if (primitive.kind ==
                            PROGPU_NATIVE_PRIMITIVE_ROUNDED_RECTANGLE &&
                        primitive.x == (updated ? 20.0F : 10.0F) &&
                        primitive.y == (updated ? 30.0F : 20.0F) &&
                        primitive.width == (updated ? 40.0F : 30.0F) &&
                        primitive.height == (updated ? 50.0F : 40.0F) &&
                        primitive.corner_radius == (updated ? 6.0F : 4.0F)) {
                        found_rectangle = true;
                    } else if (
                        primitive.kind == PROGPU_NATIVE_PRIMITIVE_ELLIPSE &&
                        primitive.x == (updated ? 64.0F : 46.0F) &&
                        primitive.y == (updated ? 64.0F : 56.0F) &&
                        primitive.width == (updated ? 12.0F : 8.0F) &&
                        primitive.height == (updated ? 12.0F : 8.0F)) {
                        found_ellipse = true;
                    }
                }
            }
        }
        return found_line && found_rectangle && found_ellipse;
    };
    PROGPU_REQUIRE(verify_scene(false));

    std::vector<std::byte> update;
    append_command(update, command::point_resource, start, 4.0, 5.0);
    append_command(update, command::point_resource, end, 12.0, 13.0);
    append_command(
        update, command::rect_resource, rect, 20.0, 30.0, 40.0, 50.0);
    append_command(update, command::point_resource, center, 70.0, 70.0);
    append_command(update, command::double_resource, radius_x, 6.0);
    append_command(update, command::double_resource, radius_y, 6.0);
    PROGPU_REQUIRE(state.apply(update) == status::success);
    PROGPU_REQUIRE(
        state.build_scene(target, 9051U, 2U, stream, &metrics) ==
        status::success);
    progpu_native_scene_layer updated_cache{};
    PROGPU_REQUIRE(try_get_cached_layer(stream, updated_cache));
    PROGPU_REQUIRE(
        updated_cache.content_revision != initial_cache.content_revision);
    found_line = false;
    found_rectangle = false;
    found_ellipse = false;
    PROGPU_REQUIRE(verify_scene(true));

    std::vector<std::byte> delete_animation;
    append_command(
        delete_animation,
        command::channel_delete_resource,
        center,
        51U);
    PROGPU_REQUIRE(state.apply(delete_animation) == status::invalid_graph);

    std::vector<std::byte> invalid_radius;
    append_command(
        invalid_radius, command::double_resource, radius_x, -1.0);
    PROGPU_REQUIRE(state.apply(invalid_radius) == status::success);
    PROGPU_REQUIRE(
        state.build_scene(target, 9051U, 3U, stream, &metrics) ==
        status::invalid_graph);
    return true;
}

bool animated_pen_and_dash_resources_drive_strokes() {
    constexpr std::uint32_t visual = 1U;
    constexpr std::uint32_t content = 2U;
    constexpr std::uint32_t target = 3U;
    constexpr std::uint32_t brush = 4U;
    constexpr std::uint32_t pen = 5U;
    constexpr std::uint32_t dash = 6U;
    constexpr std::uint32_t cache = 7U;
    constexpr std::uint32_t thickness = 8U;
    constexpr std::uint32_t offset = 9U;

    std::vector<std::byte> batch;
    append_create(batch, visual, 39U);
    append_create(batch, content, 43U);
    append_create(batch, target, 47U);
    append_create(batch, brush, 75U);
    append_create(batch, pen, 85U);
    append_create(batch, dash, 84U);
    append_create(batch, cache, 94U);
    append_create(batch, thickness, 49U);
    append_create(batch, offset, 49U);
    append_command(batch, command::visual_create, visual);
    append_command(batch, command::visual_set_content, visual, content);
    append_command(
        batch,
        command::solid_color_brush,
        brush,
        1.0,
        progpu_native_color{0.8F, 0.3F, 0.1F, 1.0F},
        0U,
        0U,
        0U,
        0U);
    append_command(batch, command::double_resource, thickness, 2.5);
    append_command(batch, command::double_resource, offset, 0.75);
    const std::array intervals{2.0, 1.0};
    append_dash_style(batch, dash, 0.25, offset, intervals);
    append_command(
        batch,
        command::pen,
        pen,
        1.0,
        10.0,
        brush,
        thickness,
        0U,
        0U,
        1U,
        0U,
        dash);
    std::vector<std::byte> nested;
    append_command(
        nested,
        command::draw_line,
        4.0,
        8.0,
        40.0,
        8.0,
        pen,
        0U);
    append_render_data(batch, content, nested);
    append_command(batch, command::bitmap_cache, cache, 1.0, 0U, 0U, 0U);
    append_command(batch, command::visual_set_cache_mode, visual, cache);
    append_command(
        batch,
        command::generic_target_create,
        target,
        std::uint64_t{0U},
        std::uint64_t{0U},
        64U,
        32U,
        0U);
    append_command(batch, command::target_set_root, target, visual);

    channel state;
    PROGPU_REQUIRE(state.apply(batch) == status::success);
    PROGPU_REQUIRE(
        state.set_visual_cache_bounds(
            visual, 0.0, 0.0, 64.0, 32.0) == status::success);
    std::vector<std::byte> stream;
    const auto verify_stroke = [&](float expected_thickness,
                                   double expected_offset) {
        const auto header = read_value<progpu_native_scene_header>(stream, 0U);
        for (std::uint32_t index = 0U; index < header.resource_count; ++index) {
            const auto resource = read_value<progpu_native_scene_resource>(
                stream,
                header.resource_offset +
                    index * sizeof(progpu_native_scene_resource));
            if (resource.kind != PROGPU_NATIVE_SCENE_RESOURCE_STROKE_BATCH) {
                continue;
            }
            const auto stroke = read_value<progpu_native_scene_stroke>(
                stream,
                resource.payload_offset);
            return stroke.stroke_thickness == expected_thickness &&
                stroke.dash_offset == expected_offset &&
                stroke.dash_interval_count == 2U;
        }
        return false;
    };
    PROGPU_REQUIRE(
        state.build_scene(target, 9052U, 1U, stream) == status::success);
    PROGPU_REQUIRE(verify_stroke(2.5F, 0.75));
    progpu_native_scene_layer initial_cache{};
    PROGPU_REQUIRE(try_get_cached_layer(stream, initial_cache));

    std::vector<std::byte> update;
    append_command(update, command::double_resource, thickness, 5.0);
    append_command(update, command::double_resource, offset, -1.25);
    PROGPU_REQUIRE(state.apply(update) == status::success);
    PROGPU_REQUIRE(
        state.build_scene(target, 9052U, 2U, stream) == status::success);
    PROGPU_REQUIRE(verify_stroke(5.0F, -1.25));
    progpu_native_scene_layer updated_cache{};
    PROGPU_REQUIRE(try_get_cached_layer(stream, updated_cache));
    PROGPU_REQUIRE(
        updated_cache.content_revision != initial_cache.content_revision);

    const auto pen_generation = state.resource_generation(pen);
    std::vector<std::byte> wrong_pen_type;
    append_command(
        wrong_pen_type,
        command::pen,
        pen,
        1.0,
        10.0,
        brush,
        brush,
        0U,
        0U,
        1U,
        0U,
        dash);
    PROGPU_REQUIRE(state.apply(wrong_pen_type) == status::invalid_handle);
    PROGPU_REQUIRE(state.resource_generation(pen) == pen_generation);

    const auto dash_generation = state.resource_generation(dash);
    std::vector<std::byte> wrong_dash_type;
    append_dash_style(wrong_dash_type, dash, 0.25, brush, intervals);
    PROGPU_REQUIRE(state.apply(wrong_dash_type) == status::invalid_handle);
    PROGPU_REQUIRE(state.resource_generation(dash) == dash_generation);

    std::vector<std::byte> delete_animation;
    append_command(
        delete_animation,
        command::channel_delete_resource,
        offset,
        49U);
    PROGPU_REQUIRE(state.apply(delete_animation) == status::invalid_graph);

    std::vector<std::byte> invalid_thickness;
    append_command(
        invalid_thickness, command::double_resource, thickness, -1.0);
    PROGPU_REQUIRE(state.apply(invalid_thickness) == status::success);
    PROGPU_REQUIRE(
        state.build_scene(target, 9052U, 3U, stream) ==
        status::invalid_graph);
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

    const auto has_vector_clip = [&stream](std::uint32_t path_count) {
        const auto scene = read_value<progpu_native_scene_header>(stream, 0U);
        for (std::uint32_t index = 0U; index < scene.resource_count; ++index) {
            const auto resource = read_value<progpu_native_scene_resource>(
                stream, scene.resource_offset +
                    index * sizeof(progpu_native_scene_resource));
            if (resource.kind != PROGPU_NATIVE_SCENE_RESOURCE_STATE) {
                continue;
            }
            const auto value = read_value<progpu_native_scene_state>(
                stream, resource.payload_offset);
            if ((value.flags & PROGPU_NATIVE_SCENE_STATE_MASK) == 0U) {
                continue;
            }
            const auto mask_resource =
                read_value<progpu_native_scene_resource>(
                    stream, scene.resource_offset +
                        value.mask_resource_index *
                            sizeof(progpu_native_scene_resource));
            const auto mask =
                read_value<progpu_native_scene_layer_vector_mask>(
                    stream, mask_resource.payload_offset);
            if (mask.path_count == path_count && mask.segment_count != 0U) {
                return true;
            }
        }
        return false;
    };
    std::vector<std::byte> ellipse_clip;
    append_command(
        ellipse_clip, command::visual_set_clip, child, ellipse);
    PROGPU_REQUIRE(state.apply(ellipse_clip) == status::success);
    PROGPU_REQUIRE(
        state.build_scene(target, 9010U, 2U, stream, &metrics) ==
        status::success);
    PROGPU_REQUIRE(has_vector_clip(1U));

    std::vector<std::byte> rounded_clip;
    append_command(
        rounded_clip,
        command::rectangle_geometry,
        clip,
        3.0,
        3.0,
        0.0,
        0.0,
        40.0,
        40.0,
        0U,
        0U,
        0U,
        0U);
    append_command(rounded_clip, command::visual_set_clip, child, clip);
    PROGPU_REQUIRE(state.apply(rounded_clip) == status::success);
    PROGPU_REQUIRE(
        state.build_scene(target, 9010U, 3U, stream, &metrics) ==
        status::success);
    PROGPU_REQUIRE(has_vector_clip(1U));

    // A visual mask must survive both a nested render-data clip and the
    // return to its sibling. Each sibling starts at the parent's prefix.
    constexpr std::uint32_t sibling = 9U;
    std::vector<std::byte> inherited_clip;
    append_create(inherited_clip, sibling, 39U);
    append_command(inherited_clip, command::visual_create, sibling);
    append_command(inherited_clip, command::visual_set_clip, root, ellipse);
    append_command(inherited_clip, command::visual_set_clip, sibling, ellipse);
    append_command(inherited_clip, command::visual_set_content, sibling, content);
    append_command(
        inherited_clip, command::visual_insert_child_at, root, sibling, 1U);
    std::vector<std::byte> clipped_nested;
    append_command(clipped_nested, command::push_clip, ellipse, 0U);
    clipped_nested.insert(clipped_nested.end(), nested.begin(), nested.end());
    append_command(clipped_nested, command::pop);
    clipped_nested.insert(clipped_nested.end(), nested.begin(), nested.end());
    append_render_data(inherited_clip, content, clipped_nested);
    PROGPU_REQUIRE(state.apply(inherited_clip) == status::success);
    PROGPU_REQUIRE(
        state.build_scene(target, 9010U, 4U, stream, &metrics) ==
        status::success);
    PROGPU_REQUIRE(has_vector_clip(1U));
    PROGPU_REQUIRE(has_vector_clip(2U));
    PROGPU_REQUIRE(has_vector_clip(3U));
    PROGPU_REQUIRE(!has_vector_clip(4U));

    std::vector<std::byte> clear_inherited_clip;
    append_command(clear_inherited_clip, command::visual_set_clip, root, 0U);
    append_command(clear_inherited_clip, command::visual_set_clip, sibling, 0U);
    append_render_data(clear_inherited_clip, content, nested);
    PROGPU_REQUIRE(state.apply(clear_inherited_clip) == status::success);

    std::vector<std::byte> transformed_scroll_clip;
    append_command(
        transformed_scroll_clip, command::visual_set_clip, child, 0U);
    append_command(
        transformed_scroll_clip,
        command::matrix_transform,
        transform,
        1.0,
        0.5,
        0.0,
        1.0,
        0.0,
        0.0,
        0U);
    PROGPU_REQUIRE(state.apply(transformed_scroll_clip) == status::success);
    PROGPU_REQUIRE(
        state.build_scene(target, 9010U, 4U, stream, &metrics) ==
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
    std::vector<std::byte> sheared_clip;
    append_command(sheared_clip, command::visual_set_clip, child, ellipse);
    PROGPU_REQUIRE(state.apply(sheared_clip) == status::success);
    PROGPU_REQUIRE(
        state.build_scene(target, 9010U, 5U, stream, &metrics) ==
        status::success);
    PROGPU_REQUIRE(has_vector_clip(1U));
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

bool viewport3d_geometry_clips_apply_to_isolated_outputs() {
    using progpu::native::tests::mil_clip_cache_options;
    using progpu::native::tests::mil_clip_effect;
    for (const auto effect : {mil_clip_effect::none, mil_clip_effect::blur,
                             mil_clip_effect::cached_blur}) {
        for (const bool cached : {false, true}) {
            const mil_clip_cache_options options{
                .enabled = cached, .viewport3d = true};
            std::vector<std::byte> stream;
            PROGPU_REQUIRE(progpu::native::tests::build_mil_visual_clip_fixture(
                stream, effect, 9300U, options));
            const auto header = read_value<progpu_native_scene_header>(stream, 0U);
            const auto layers = get_scene_layers(stream);
            std::uint32_t masked_outputs = 0U;
            for (const auto& layer : layers) {
                if (layer.mask_resource_index == PROGPU_NATIVE_SCENE_NO_INDEX) continue;
                const auto resource = read_value<progpu_native_scene_resource>(stream,
                    header.resource_offset + layer.mask_resource_index *
                        sizeof(progpu_native_scene_resource));
                const auto mask = read_value<progpu_native_scene_layer_vector_mask>(
                    stream, resource.payload_offset);
                PROGPU_REQUIRE(mask.kind == PROGPU_NATIVE_SCENE_LAYER_MASK_VECTOR_CLIP_CHAIN);
                PROGPU_REQUIRE(mask.path_count == 2U);
                ++masked_outputs;
            }
            PROGPU_REQUIRE(masked_outputs == 2U);
            std::uint32_t mesh_draws = 0U;
            for (std::uint32_t index = 0U; index < header.command_count; ++index) {
                const auto draw = read_value<progpu_native_scene_command>(stream,
                    header.command_offset + index * sizeof(progpu_native_scene_command));
                if (draw.kind != PROGPU_NATIVE_SCENE_COMMAND_DRAW_MESH_3D_BATCH) continue;
                const auto state = read_value<progpu_native_scene_state>(stream,
                    read_value<progpu_native_scene_resource>(stream,
                        header.resource_offset + draw.state_index *
                            sizeof(progpu_native_scene_resource)).payload_offset);
                PROGPU_REQUIRE((state.flags & PROGPU_NATIVE_SCENE_STATE_MASK) == 0U);
                ++mesh_draws;
            }
            PROGPU_REQUIRE(mesh_draws == 2U);
        }
    }
    return true;
}

bool visual_geometry_clips_apply_after_effects() {
    using progpu::native::tests::mil_clip_effect;
    for (const auto effect : {mil_clip_effect::zero_blur,
                             mil_clip_effect::blur,
                             mil_clip_effect::cached_blur,
                             mil_clip_effect::box_blur,
                             mil_clip_effect::shadow}) {
        std::vector<std::byte> stream;
        PROGPU_REQUIRE(progpu::native::tests::build_mil_visual_clip_fixture(
            stream, effect));
        const auto layers = get_scene_layers(stream);
        PROGPU_REQUIRE(layers.size() ==
            (effect == mil_clip_effect::cached_blur ? 4U : 2U));
        const auto header = read_value<progpu_native_scene_header>(stream, 0U);
        const auto mask_paths = [&](std::uint32_t mask_index) {
            const auto resource = read_value<progpu_native_scene_resource>(
                stream, header.resource_offset +
                    mask_index * sizeof(progpu_native_scene_resource));
            return read_value<progpu_native_scene_layer_vector_mask>(
                stream, resource.payload_offset).path_count;
        };
        std::uint32_t masked_outputs = 0U;
        for (const auto& layer : layers) {
            if (layer.mask_resource_index == PROGPU_NATIVE_SCENE_NO_INDEX) {
                PROGPU_REQUIRE((layer.flags &
                    PROGPU_NATIVE_SCENE_LAYER_CACHE_LOCAL_SPACE) != 0U);
                continue;
            }
            PROGPU_REQUIRE(mask_paths(layer.mask_resource_index) == 2U);
            ++masked_outputs;
        }
        PROGPU_REQUIRE(masked_outputs == 2U);
        for (std::uint32_t index = 0U; index < header.resource_count; ++index) {
            const auto resource = read_value<progpu_native_scene_resource>(
                stream, header.resource_offset +
                    index * sizeof(progpu_native_scene_resource));
            if (resource.kind != PROGPU_NATIVE_SCENE_RESOURCE_STATE) {
                continue;
            }
            const auto value = read_value<progpu_native_scene_state>(
                stream, resource.payload_offset);
            if ((value.flags & PROGPU_NATIVE_SCENE_STATE_MASK) != 0U) {
                // Root has one clip; effect content has its independent
                // one-clip frame. No draw state may pre-apply the two-clip
                // final output mask or prepend it to nested content clips.
                PROGPU_REQUIRE(mask_paths(value.mask_resource_index) == 1U);
            }
        }
    }
    return true;
}

bool visual_geometry_clips_apply_after_local_caches() {
    using progpu::native::tests::mil_clip_cache_options;
    using progpu::native::tests::mil_clip_effect;
    for (const bool gradient : {false, true}) {
        for (const bool nested : {false, true}) {
            const mil_clip_cache_options cache{
                .enabled = true, .gradient = gradient, .scale = 2.0,
                .offset_x = 0.25, .offset_y = 0.25,
                .snaps = true, .guidelines = true, .nested = nested};
            std::vector<std::byte> stream;
            PROGPU_REQUIRE(progpu::native::tests::build_mil_visual_clip_fixture(
                stream, mil_clip_effect::none, 9120U, cache));
            const auto header = read_value<progpu_native_scene_header>(stream, 0U);
            const auto resource_at = [&](std::uint32_t index) {
                return read_value<progpu_native_scene_resource>(stream,
                    header.resource_offset + index * sizeof(progpu_native_scene_resource));
            };
            const auto layers = get_scene_layers(stream);
            PROGPU_REQUIRE(layers.size() == (nested ? 3U : 2U));
            for (std::size_t index = 0U; index < layers.size(); ++index) {
                const auto& layer = layers[index];
                const bool parent = nested && index == 0U;
                PROGPU_REQUIRE((layer.flags &
                    PROGPU_NATIVE_SCENE_LAYER_CACHE_LOCAL_SPACE) != 0U);
                PROGPU_REQUIRE(layer.bounds.width == (parent ? 64.0F : 128.0F));
                PROGPU_REQUIRE(layer.mask_resource_index != PROGPU_NATIVE_SCENE_NO_INDEX);
                const auto resource = resource_at(layer.mask_resource_index);
                if (gradient && !parent) {
                    const auto mask = read_value<progpu_native_scene_layer_composite_mask>(
                        stream, resource.payload_offset);
                    PROGPU_REQUIRE(mask.kind == PROGPU_NATIVE_SCENE_LAYER_MASK_COMPOSITE);
                    PROGPU_REQUIRE(mask.component_count == 2U);
                    PROGPU_REQUIRE(mask.brush_mask_count == 1U);
                    PROGPU_REQUIRE(mask.gradient_stop_count == 2U);
                    PROGPU_REQUIRE(mask.path_count == (nested ? 1U : 2U));
                } else {
                    const auto mask = read_value<progpu_native_scene_layer_vector_mask>(
                        stream, resource.payload_offset);
                    PROGPU_REQUIRE(mask.kind == PROGPU_NATIVE_SCENE_LAYER_MASK_VECTOR_CLIP_CHAIN);
                    PROGPU_REQUIRE(mask.path_count == (nested ? 1U : 2U));
                }
            }
            for (std::uint32_t index = 0U; index < header.resource_count; ++index) {
                const auto resource = resource_at(index);
                if (resource.kind != PROGPU_NATIVE_SCENE_RESOURCE_STATE) continue;
                const auto value = read_value<progpu_native_scene_state>(
                    stream, resource.payload_offset);
                if ((value.flags & PROGPU_NATIVE_SCENE_STATE_MASK) == 0U) continue;
                const auto mask_resource = resource_at(value.mask_resource_index);
                const auto mask = read_value<progpu_native_scene_layer_vector_mask>(
                    stream, mask_resource.payload_offset);
                // Only a cache's own render-data clip applies to its pixels.
                // Neither ancestor nor cache-root clips belong in this frame.
                PROGPU_REQUIRE(mask.path_count == 1U);
            }
        }
    }
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

bool visual_gaussian_effects_compile_to_isolated_layers() {
    constexpr std::uint32_t visual = 1U;
    constexpr std::uint32_t content = 2U;
    constexpr std::uint32_t target = 3U;
    constexpr std::uint32_t brush = 4U;
    constexpr std::uint32_t blur = 5U;
    constexpr std::uint32_t shadow = 6U;
    constexpr std::uint32_t clip = 7U;
    constexpr std::uint32_t parent = 8U;
    constexpr std::uint32_t parent_mask = 9U;
    constexpr std::uint32_t child_mask = 10U;
    constexpr std::uint32_t blur_radius_animation = 11U;
    constexpr std::uint32_t shadow_depth_animation = 12U;
    constexpr std::uint32_t shadow_color_animation = 13U;
    constexpr std::uint32_t shadow_direction_animation = 14U;
    constexpr std::uint32_t shadow_opacity_animation = 15U;
    constexpr std::uint32_t shadow_radius_animation = 16U;

    std::vector<std::byte> batch;
    append_create(batch, visual, 39U);
    append_create(batch, content, 43U);
    append_create(batch, target, 47U);
    append_create(batch, brush, 75U);
    append_create(batch, blur, 36U);
    append_create(batch, blur_radius_animation, 49U);
    append_create(batch, shadow_depth_animation, 49U);
    append_create(batch, shadow_color_animation, 50U);
    append_create(batch, shadow_direction_animation, 49U);
    append_create(batch, shadow_opacity_animation, 49U);
    append_create(batch, shadow_radius_animation, 49U);
    append_command(batch, command::visual_create, visual);
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
    std::vector<std::byte> nested;
    append_command(
        nested,
        command::draw_rectangle,
        8.0,
        10.0,
        32.0,
        24.0,
        brush,
        0U);
    append_render_data(batch, content, nested);
    append_command(
        batch, command::double_resource, blur_radius_animation, 9.75);
    append_command(
        batch, command::double_resource, shadow_depth_animation, 5.0);
    append_command(
        batch,
        command::color_resource,
        shadow_color_animation,
        std::array<float, 4U>{0.1F, 0.2F, 0.3F, 0.5F});
    append_command(
        batch, command::double_resource, shadow_direction_animation, 315.0);
    append_command(
        batch, command::double_resource, shadow_opacity_animation, 0.4);
    append_command(
        batch, command::double_resource, shadow_radius_animation, 6.9);
    append_command(
        batch,
        command::blur_effect,
        blur,
        1.0,
        blur_radius_animation,
        0U,
        1U);
    append_command(batch, command::visual_set_effect, visual, blur);
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
    std::vector<std::byte> unbounded_stream;
    progpu::native::mil::scene_metrics unbounded_metrics{};
    PROGPU_REQUIRE(
        state.build_scene(
            target,
            9014U,
            1U,
            unbounded_stream,
            &unbounded_metrics) == status::success);
    const auto unbounded_layers = get_scene_layers(unbounded_stream);
    PROGPU_REQUIRE(unbounded_layers.size() == 1U);
    PROGPU_REQUIRE(
        (unbounded_layers[0].flags & PROGPU_NATIVE_SCENE_LAYER_BOUNDS) == 0U);
    PROGPU_REQUIRE(
        state.set_visual_cache_bounds(
            visual, 8.0, 10.0, 32.0, 24.0) == status::success);
    std::vector<std::byte> stream;
    progpu::native::mil::scene_metrics metrics{};
    const auto read_effect = [&]() {
        const auto header = read_value<progpu_native_scene_header>(stream, 0U);
        for (std::uint32_t index = 0U; index < header.resource_count; ++index) {
            const auto resource = read_value<progpu_native_scene_resource>(
                stream,
                header.resource_offset +
                    index * sizeof(progpu_native_scene_resource));
            if (resource.kind ==
                    PROGPU_NATIVE_SCENE_RESOURCE_EFFECT_CHAIN) {
                return read_value<progpu_native_group_effect>(
                    stream, resource.auxiliary_offset);
            }
        }
        return progpu_native_group_effect{};
    };
    PROGPU_REQUIRE(
        state.build_scene(target, 9014U, 2U, stream, &metrics) ==
        status::success);
    auto effect = read_effect();
    PROGPU_REQUIRE(
        effect.kind == PROGPU_NATIVE_GROUP_EFFECT_GAUSSIAN_BLUR);
    PROGPU_REQUIRE(effect.sigma_x == 3.0F && effect.sigma_y == 3.0F);
    auto bounded_layers = get_scene_layers(stream);
    PROGPU_REQUIRE(bounded_layers.size() == 1U);
    PROGPU_REQUIRE(
        (bounded_layers[0].flags & PROGPU_NATIVE_SCENE_LAYER_BOUNDS) != 0U);
    PROGPU_REQUIRE(bounded_layers[0].bounds.x == -1.0F);
    PROGPU_REQUIRE(bounded_layers[0].bounds.y == 1.0F);
    PROGPU_REQUIRE(bounded_layers[0].bounds.width == 50.0F);
    PROGPU_REQUIRE(bounded_layers[0].bounds.height == 42.0F);

    const auto initial_blur_revision = effect.revision;
    std::vector<std::byte> blur_animation_update;
    append_command(
        blur_animation_update,
        command::double_resource,
        blur_radius_animation,
        12.2);
    PROGPU_REQUIRE(state.apply(blur_animation_update) == status::success);
    PROGPU_REQUIRE(
        state.build_scene(target, 9014U, 3U, stream, &metrics) ==
        status::success);
    effect = read_effect();
    PROGPU_REQUIRE(effect.sigma_x == 4.0F && effect.sigma_y == 4.0F);
    PROGPU_REQUIRE(effect.revision != initial_blur_revision);
    bounded_layers = get_scene_layers(stream);
    PROGPU_REQUIRE(bounded_layers[0].bounds.x == -4.0F);
    PROGPU_REQUIRE(bounded_layers[0].bounds.y == -2.0F);
    PROGPU_REQUIRE(bounded_layers[0].bounds.width == 56.0F);
    PROGPU_REQUIRE(bounded_layers[0].bounds.height == 48.0F);

    std::vector<std::byte> box_blur;
    append_command(
        box_blur,
        command::blur_effect,
        blur,
        9.0,
        0U,
        1U,
        0U);
    PROGPU_REQUIRE(state.apply(box_blur) == status::success);
    PROGPU_REQUIRE(
        state.build_scene(target, 9014U, 4U, stream, &metrics) ==
        status::success);
    effect = read_effect();
    PROGPU_REQUIRE(effect.kind == PROGPU_NATIVE_GROUP_EFFECT_BOX_BLUR);
    PROGPU_REQUIRE(effect.sigma_x == 9.0F && effect.sigma_y == 9.0F);
    bounded_layers = get_scene_layers(stream);
    PROGPU_REQUIRE(bounded_layers[0].bounds.x == -1.0F);
    PROGPU_REQUIRE(bounded_layers[0].bounds.y == 1.0F);
    PROGPU_REQUIRE(bounded_layers[0].bounds.width == 50.0F);
    PROGPU_REQUIRE(bounded_layers[0].bounds.height == 42.0F);

    std::vector<std::byte> unsupported_kernel;
    append_command(
        unsupported_kernel,
        command::blur_effect,
        blur,
        9.0,
        0U,
        2U,
        0U);
    PROGPU_REQUIRE(
        state.apply(unsupported_kernel) == status::unsupported_command);

    std::vector<std::byte> wrong_blur_animation_type;
    append_command(
        wrong_blur_animation_type,
        command::blur_effect,
        blur,
        9.0,
        shadow_color_animation,
        0U,
        0U);
    PROGPU_REQUIRE(
        state.apply(wrong_blur_animation_type) == status::invalid_handle);

    std::vector<std::byte> replace;
    append_create(replace, shadow, 37U);
    append_command(
        replace,
        command::drop_shadow_effect,
        shadow,
        1.0,
        progpu_native_color{1.0F, 1.0F, 1.0F, 1.0F},
        0.0,
        1.0,
        1.0,
        shadow_depth_animation,
        shadow_color_animation,
        shadow_direction_animation,
        shadow_opacity_animation,
        shadow_radius_animation,
        0U);
    append_command(replace, command::visual_set_effect, visual, shadow);
    PROGPU_REQUIRE(state.apply(replace) == status::success);
    PROGPU_REQUIRE(
        state.build_scene(target, 9014U, 4U, stream, &metrics) ==
        status::success);
    effect = read_effect();
    PROGPU_REQUIRE(effect.kind == PROGPU_NATIVE_GROUP_EFFECT_DROP_SHADOW);
    PROGPU_REQUIRE(effect.sigma_x == 2.0F && effect.sigma_y == 2.0F);
    PROGPU_REQUIRE(std::abs(effect.offset_x - 3.535534F) < 0.00001F);
    PROGPU_REQUIRE(std::abs(effect.offset_y - 3.535534F) < 0.00001F);
    PROGPU_REQUIRE(effect.color_r == 0.1F && effect.color_g == 0.2F &&
        effect.color_b == 0.3F &&
        std::abs(effect.color_a - 0.2F) < 0.00001F);
    bounded_layers = get_scene_layers(stream);
    PROGPU_REQUIRE(bounded_layers.size() == 1U);
    PROGPU_REQUIRE(
        std::abs(bounded_layers[0].bounds.x - 5.535534F) < 0.00001F);
    PROGPU_REQUIRE(
        std::abs(bounded_layers[0].bounds.y - 7.535534F) < 0.00001F);
    PROGPU_REQUIRE(bounded_layers[0].bounds.width == 44.0F);
    PROGPU_REQUIRE(bounded_layers[0].bounds.height == 36.0F);

    const auto initial_shadow_revision = effect.revision;
    std::vector<std::byte> shadow_animation_update;
    append_command(
        shadow_animation_update,
        command::double_resource,
        shadow_depth_animation,
        8.0);
    append_command(
        shadow_animation_update,
        command::color_resource,
        shadow_color_animation,
        std::array<float, 4U>{0.6F, 0.5F, 0.4F, 0.75F});
    append_command(
        shadow_animation_update,
        command::double_resource,
        shadow_direction_animation,
        180.0);
    append_command(
        shadow_animation_update,
        command::double_resource,
        shadow_opacity_animation,
        0.5);
    append_command(
        shadow_animation_update,
        command::double_resource,
        shadow_radius_animation,
        9.5);
    PROGPU_REQUIRE(state.apply(shadow_animation_update) == status::success);
    PROGPU_REQUIRE(
        state.build_scene(target, 9014U, 5U, stream, &metrics) ==
        status::success);
    effect = read_effect();
    PROGPU_REQUIRE(effect.kind == PROGPU_NATIVE_GROUP_EFFECT_DROP_SHADOW);
    PROGPU_REQUIRE(effect.sigma_x == 3.0F && effect.sigma_y == 3.0F);
    PROGPU_REQUIRE(std::abs(effect.offset_x + 8.0F) < 0.00001F);
    PROGPU_REQUIRE(std::abs(effect.offset_y) < 0.00001F);
    PROGPU_REQUIRE(effect.color_r == 0.6F && effect.color_g == 0.5F &&
        effect.color_b == 0.4F &&
        std::abs(effect.color_a - 0.375F) < 0.00001F);
    PROGPU_REQUIRE(effect.revision != initial_shadow_revision);
    bounded_layers = get_scene_layers(stream);
    PROGPU_REQUIRE(std::abs(bounded_layers[0].bounds.x + 9.0F) < 0.00001F);
    PROGPU_REQUIRE(std::abs(bounded_layers[0].bounds.y - 1.0F) < 0.00001F);
    PROGPU_REQUIRE(std::abs(bounded_layers[0].bounds.width - 50.0F) < 0.00001F);
    PROGPU_REQUIRE(std::abs(bounded_layers[0].bounds.height - 42.0F) < 0.00001F);

    std::vector<std::byte> delete_effect_animation;
    append_command(
        delete_effect_animation,
        command::channel_delete_resource,
        shadow_color_animation,
        50U);
    PROGPU_REQUIRE(
        state.apply(delete_effect_animation) == status::invalid_graph);

    std::vector<std::byte> delete_referenced;
    append_command(
        delete_referenced,
        command::channel_delete_resource,
        shadow,
        37U);
    PROGPU_REQUIRE(state.apply(delete_referenced) == status::invalid_graph);
    std::vector<std::byte> clear;
    append_command(clear, command::visual_set_effect, visual, 0U);
    PROGPU_REQUIRE(state.apply(clear) == status::success);
    PROGPU_REQUIRE(state.apply(delete_referenced) == status::success);
    PROGPU_REQUIRE(state.apply(delete_effect_animation) == status::success);

    std::vector<std::byte> clipped_effect;
    append_create(clipped_effect, clip, 69U);
    append_command(
        clipped_effect,
        command::rectangle_geometry,
        clip,
        0.0,
        0.0,
        12.0,
        12.0,
        20.0,
        14.0,
        0U,
        0U,
        0U,
        0U);
    append_command(clipped_effect, command::visual_set_clip, visual, clip);
    append_command(clipped_effect, command::visual_set_effect, visual, blur);
    PROGPU_REQUIRE(state.apply(clipped_effect) == status::success);
    PROGPU_REQUIRE(
        state.build_scene(target, 9014U, 4U, stream, &metrics) ==
        status::success);
    const auto clipped_layers = get_scene_layers(stream);
    PROGPU_REQUIRE(clipped_layers.size() == 1U);
    PROGPU_REQUIRE(
        (clipped_layers[0].flags &
            PROGPU_NATIVE_SCENE_LAYER_COMPOSITE_STATE) != 0U);
    progpu_native_scene_state final_clip{};
    PROGPU_REQUIRE(try_get_state_resource(
        stream, clipped_layers[0].reserved0, final_clip));
    PROGPU_REQUIRE(
        final_clip.flags == PROGPU_NATIVE_SCENE_STATE_CLIP_RECT);
    PROGPU_REQUIRE(final_clip.transform.m11 == 1.0F);
    PROGPU_REQUIRE(final_clip.transform.m22 == 1.0F);
    PROGPU_REQUIRE(final_clip.clip_rect.x == 12.0F);
    PROGPU_REQUIRE(final_clip.clip_rect.y == 12.0F);
    PROGPU_REQUIRE(final_clip.clip_rect.width == 20.0F);
    PROGPU_REQUIRE(final_clip.clip_rect.height == 14.0F);

    std::vector<std::byte> scrolled_effect;
    append_command(
        scrolled_effect,
        command::visual_set_scrollable_area_clip,
        visual,
        14.0,
        13.0,
        8.0,
        6.0,
        1U);
    PROGPU_REQUIRE(state.apply(scrolled_effect) == status::success);
    PROGPU_REQUIRE(
        state.build_scene(target, 9014U, 41U, stream, &metrics) ==
        status::success);
    const auto scrolled_layers = get_scene_layers(stream);
    PROGPU_REQUIRE(scrolled_layers.size() == 1U);
    progpu_native_scene_state scrolled_final_clip{};
    PROGPU_REQUIRE(try_get_state_resource(
        stream, scrolled_layers[0].reserved0, scrolled_final_clip));
    PROGPU_REQUIRE(scrolled_final_clip.clip_rect.x == 14.0F);
    PROGPU_REQUIRE(scrolled_final_clip.clip_rect.y == 13.0F);
    PROGPU_REQUIRE(scrolled_final_clip.clip_rect.width == 8.0F);
    PROGPU_REQUIRE(scrolled_final_clip.clip_rect.height == 6.0F);

    std::vector<std::byte> zero_blur;
    append_command(
        zero_blur, command::blur_effect, blur, 0.0, 0U, 0U, 1U);
    append_command(
        zero_blur,
        command::visual_set_scrollable_area_clip,
        visual,
        0.0,
        0.0,
        0.0,
        0.0,
        0U);
    PROGPU_REQUIRE(state.apply(zero_blur) == status::success);
    PROGPU_REQUIRE(
        state.build_scene(target, 9014U, 5U, stream, &metrics) ==
        status::success);
    const auto zero_blur_layers = get_scene_layers(stream);
    PROGPU_REQUIRE(zero_blur_layers.size() == 1U);
    PROGPU_REQUIRE(
        zero_blur_layers[0].effect_resource_index ==
        PROGPU_NATIVE_SCENE_NO_INDEX);
    PROGPU_REQUIRE(
        (zero_blur_layers[0].flags &
            (PROGPU_NATIVE_SCENE_LAYER_FORCE_ISOLATION |
                PROGPU_NATIVE_SCENE_LAYER_COMPOSITE_STATE)) ==
        (PROGPU_NATIVE_SCENE_LAYER_FORCE_ISOLATION |
            PROGPU_NATIVE_SCENE_LAYER_COMPOSITE_STATE));
    PROGPU_REQUIRE(zero_blur_layers[0].bounds.x == 8.0F);
    PROGPU_REQUIRE(zero_blur_layers[0].bounds.y == 10.0F);
    PROGPU_REQUIRE(zero_blur_layers[0].bounds.width == 32.0F);
    PROGPU_REQUIRE(zero_blur_layers[0].bounds.height == 24.0F);

    std::vector<std::byte> opacity_effect;
    append_command(
        opacity_effect, command::blur_effect, blur, 9.0, 0U, 0U, 1U);
    append_command(opacity_effect, command::visual_set_alpha, visual, 0.5);
    PROGPU_REQUIRE(state.apply(opacity_effect) == status::success);
    PROGPU_REQUIRE(
        state.build_scene(target, 9014U, 6U, stream, &metrics) ==
        status::success);
    const auto opacity_effect_layers = get_scene_layers(stream);
    PROGPU_REQUIRE(opacity_effect_layers.size() == 2U);
    PROGPU_REQUIRE(
        opacity_effect_layers[0].effect_resource_index !=
            PROGPU_NATIVE_SCENE_NO_INDEX);
    PROGPU_REQUIRE(
        (opacity_effect_layers[0].flags &
            PROGPU_NATIVE_SCENE_LAYER_COMPOSITE_STATE) != 0U);
    PROGPU_REQUIRE(opacity_effect_layers[0].bounds.x == -1.0F);
    PROGPU_REQUIRE(opacity_effect_layers[0].bounds.y == 1.0F);
    PROGPU_REQUIRE(opacity_effect_layers[0].bounds.width == 50.0F);
    PROGPU_REQUIRE(opacity_effect_layers[0].bounds.height == 42.0F);
    PROGPU_REQUIRE(
        opacity_effect_layers[1].effect_resource_index ==
            PROGPU_NATIVE_SCENE_NO_INDEX);
    PROGPU_REQUIRE(
        (opacity_effect_layers[1].flags &
            PROGPU_NATIVE_SCENE_LAYER_FORCE_ISOLATION) != 0U);
    PROGPU_REQUIRE(opacity_effect_layers[1].opacity == 0.5F);
    PROGPU_REQUIRE(opacity_effect_layers[1].bounds.x == 8.0F);
    PROGPU_REQUIRE(opacity_effect_layers[1].bounds.y == 10.0F);
    PROGPU_REQUIRE(opacity_effect_layers[1].bounds.width == 32.0F);
    PROGPU_REQUIRE(opacity_effect_layers[1].bounds.height == 24.0F);

    std::vector<std::byte> zero_blur_opacity;
    append_command(
        zero_blur_opacity, command::blur_effect, blur, 0.0, 0U, 0U, 1U);
    append_command(
        zero_blur_opacity, command::visual_set_clip, visual, 0U);
    PROGPU_REQUIRE(state.apply(zero_blur_opacity) == status::success);
    PROGPU_REQUIRE(
        state.build_scene(target, 9014U, 7U, stream, &metrics) ==
        status::success);
    const auto zero_opacity_layers = get_scene_layers(stream);
    PROGPU_REQUIRE(zero_opacity_layers.size() == 1U);
    PROGPU_REQUIRE(
        zero_opacity_layers[0].effect_resource_index ==
            PROGPU_NATIVE_SCENE_NO_INDEX);
    PROGPU_REQUIRE(zero_opacity_layers[0].opacity == 0.5F);
    PROGPU_REQUIRE(zero_opacity_layers[0].bounds.x == 8.0F);
    PROGPU_REQUIRE(zero_opacity_layers[0].bounds.y == 10.0F);
    PROGPU_REQUIRE(zero_opacity_layers[0].bounds.width == 32.0F);
    PROGPU_REQUIRE(zero_opacity_layers[0].bounds.height == 24.0F);

    std::vector<std::byte> inherited_opacity;
    append_command(
        inherited_opacity, command::blur_effect, blur, 9.0, 0U, 0U, 1U);
    append_create(inherited_opacity, parent, 39U);
    append_command(inherited_opacity, command::visual_create, parent);
    append_command(
        inherited_opacity, command::visual_set_alpha, parent, 0.5);
    append_command(
        inherited_opacity,
        command::visual_insert_child_at,
        parent,
        visual,
        0U);
    append_command(
        inherited_opacity, command::target_set_root, target, parent);
    PROGPU_REQUIRE(state.apply(inherited_opacity) == status::success);
    PROGPU_REQUIRE(
        state.build_scene(target, 9014U, 8U, stream, &metrics) ==
        status::unsupported_command);
    PROGPU_REQUIRE(
        state.set_visual_cache_bounds(
            parent, 4.0, 5.0, 48.0, 30.0) == status::success);
    PROGPU_REQUIRE(
        state.build_scene(target, 9014U, 9U, stream, &metrics) ==
        status::success);
    const auto inherited_opacity_layers = get_scene_layers(stream);
    PROGPU_REQUIRE(inherited_opacity_layers.size() == 3U);
    PROGPU_REQUIRE(
        (inherited_opacity_layers[0].flags &
            PROGPU_NATIVE_SCENE_LAYER_FORCE_ISOLATION) != 0U);
    PROGPU_REQUIRE(
        (inherited_opacity_layers[0].flags &
            PROGPU_NATIVE_SCENE_LAYER_CACHE_CONTENT) == 0U);
    PROGPU_REQUIRE(inherited_opacity_layers[0].opacity == 0.5F);
    PROGPU_REQUIRE(
        inherited_opacity_layers[0].effect_resource_index ==
            PROGPU_NATIVE_SCENE_NO_INDEX);
    PROGPU_REQUIRE(inherited_opacity_layers[0].bounds.x == 4.0F);
    PROGPU_REQUIRE(inherited_opacity_layers[0].bounds.y == 5.0F);
    PROGPU_REQUIRE(inherited_opacity_layers[0].bounds.width == 48.0F);
    PROGPU_REQUIRE(inherited_opacity_layers[0].bounds.height == 30.0F);
    PROGPU_REQUIRE(
        inherited_opacity_layers[1].effect_resource_index !=
            PROGPU_NATIVE_SCENE_NO_INDEX);
    PROGPU_REQUIRE(inherited_opacity_layers[1].opacity == 1.0F);
    PROGPU_REQUIRE(
        (inherited_opacity_layers[2].flags &
            PROGPU_NATIVE_SCENE_LAYER_FORCE_ISOLATION) != 0U);
    PROGPU_REQUIRE(inherited_opacity_layers[2].opacity == 0.5F);
    const auto inherited_header =
        read_value<progpu_native_scene_header>(stream, 0U);
    for (std::uint32_t index = 0U;
         index < inherited_header.resource_count;
         ++index) {
        const auto resource = read_value<progpu_native_scene_resource>(
            stream,
            inherited_header.resource_offset +
                index * sizeof(progpu_native_scene_resource));
        if (resource.kind == PROGPU_NATIVE_SCENE_RESOURCE_STATE) {
            PROGPU_REQUIRE(
                read_value<progpu_native_scene_state>(
                    stream, resource.payload_offset).opacity == 1.0F);
        }
    }

    const std::array parent_mask_stops{
        mil_gradient_stop{0.0, {1.0F, 1.0F, 1.0F, 0.0F}},
        mil_gradient_stop{1.0, {1.0F, 1.0F, 1.0F, 1.0F}}};
    std::vector<std::byte> inherited_mask;
    append_create(inherited_mask, parent_mask, 77U);
    append_create(inherited_mask, child_mask, 77U);
    append_linear_gradient_brush(
        inherited_mask,
        parent_mask,
        1.0,
        0.0,
        0.0,
        1.0,
        0.0,
        0U,
        0U,
        0U,
        1U,
        1U,
        0U,
        0U,
        0U,
        parent_mask_stops);
    append_linear_gradient_brush(
        inherited_mask,
        child_mask,
        1.0,
        0.0,
        0.0,
        0.0,
        1.0,
        0U,
        0U,
        0U,
        1U,
        1U,
        0U,
        0U,
        0U,
        parent_mask_stops);
    append_command(
        inherited_mask, command::visual_set_alpha, parent, 1.0);
    append_command(
        inherited_mask,
        command::visual_set_alpha_mask,
        parent,
        parent_mask);
    append_command(
        inherited_mask,
        command::visual_set_alpha_mask,
        visual,
        child_mask);
    PROGPU_REQUIRE(state.apply(inherited_mask) == status::success);
    PROGPU_REQUIRE(
        state.build_scene(target, 9014U, 10U, stream, &metrics) ==
        status::success);
    const auto inherited_mask_layers = get_scene_layers(stream);
    PROGPU_REQUIRE(inherited_mask_layers.size() == 3U);
    PROGPU_REQUIRE(
        (inherited_mask_layers[0].flags &
            PROGPU_NATIVE_SCENE_LAYER_FORCE_ISOLATION) != 0U);
    PROGPU_REQUIRE(inherited_mask_layers[0].opacity == 1.0F);
    PROGPU_REQUIRE(
        inherited_mask_layers[0].mask_resource_index !=
            PROGPU_NATIVE_SCENE_NO_INDEX);
    PROGPU_REQUIRE(
        inherited_mask_layers[0].effect_resource_index ==
            PROGPU_NATIVE_SCENE_NO_INDEX);
    PROGPU_REQUIRE(inherited_mask_layers[0].bounds.x == 4.0F);
    PROGPU_REQUIRE(inherited_mask_layers[0].bounds.y == 5.0F);
    PROGPU_REQUIRE(inherited_mask_layers[0].bounds.width == 48.0F);
    PROGPU_REQUIRE(inherited_mask_layers[0].bounds.height == 30.0F);
    PROGPU_REQUIRE(
        inherited_mask_layers[1].effect_resource_index !=
            PROGPU_NATIVE_SCENE_NO_INDEX);
    PROGPU_REQUIRE(inherited_mask_layers[1].opacity == 1.0F);
    PROGPU_REQUIRE(
        (inherited_mask_layers[2].flags &
            PROGPU_NATIVE_SCENE_LAYER_FORCE_ISOLATION) != 0U);
    PROGPU_REQUIRE(inherited_mask_layers[2].opacity == 0.5F);
    PROGPU_REQUIRE(
        inherited_mask_layers[2].mask_resource_index !=
            PROGPU_NATIVE_SCENE_NO_INDEX);
    progpu_native_scene_layer_brush_mask inherited_mask_resource{};
    std::vector<progpu_native_scene_gradient_stop> inherited_mask_stops;
    PROGPU_REQUIRE(try_get_brush_mask_resource(
        stream,
        inherited_mask_layers[0].mask_resource_index,
        inherited_mask_resource,
        inherited_mask_stops));
    PROGPU_REQUIRE(
        inherited_mask_resource.brush.type ==
            PROGPU_NATIVE_SCENE_BRUSH_LINEAR_GRADIENT);
    PROGPU_REQUIRE(inherited_mask_resource.bounds.x == 4.0F);
    PROGPU_REQUIRE(inherited_mask_resource.bounds.y == 5.0F);
    PROGPU_REQUIRE(inherited_mask_resource.bounds.width == 48.0F);
    PROGPU_REQUIRE(inherited_mask_resource.bounds.height == 30.0F);
    PROGPU_REQUIRE(inherited_mask_stops.size() == 2U);
    PROGPU_REQUIRE(inherited_mask_stops.front().color.a == 0.0F);
    PROGPU_REQUIRE(inherited_mask_stops.back().color.a == 1.0F);
    progpu_native_scene_layer_brush_mask child_mask_resource{};
    std::vector<progpu_native_scene_gradient_stop> child_mask_stops;
    PROGPU_REQUIRE(try_get_brush_mask_resource(
        stream,
        inherited_mask_layers[2].mask_resource_index,
        child_mask_resource,
        child_mask_stops));
    PROGPU_REQUIRE(child_mask_resource.bounds.x == 8.0F);
    PROGPU_REQUIRE(child_mask_resource.bounds.y == 10.0F);
    PROGPU_REQUIRE(child_mask_resource.bounds.width == 32.0F);
    PROGPU_REQUIRE(child_mask_resource.bounds.height == 24.0F);
    PROGPU_REQUIRE(child_mask_resource.brush.start_point.x == 8.0F);
    PROGPU_REQUIRE(child_mask_resource.brush.start_point.y == 10.0F);
    PROGPU_REQUIRE(child_mask_resource.brush.end_point.x == 8.0F);
    PROGPU_REQUIRE(child_mask_resource.brush.end_point.y == 34.0F);
    PROGPU_REQUIRE(child_mask_stops.size() == 2U);
    return true;
}

bool visual_bitmap_cache_uses_canonical_typed_retention() {
    constexpr std::uint32_t root = 1U;
    constexpr std::uint32_t cached_visual = 2U;
    constexpr std::uint32_t sibling = 3U;
    constexpr std::uint32_t cached_content = 4U;
    constexpr std::uint32_t sibling_content = 5U;
    constexpr std::uint32_t target = 6U;
    constexpr std::uint32_t brush = 7U;
    constexpr std::uint32_t cache = 8U;
    constexpr std::uint32_t scale_animation = 9U;

    std::vector<std::byte> batch;
    append_create(batch, root, 39U);
    append_create(batch, cached_visual, 39U);
    append_create(batch, sibling, 39U);
    append_create(batch, cached_content, 43U);
    append_create(batch, sibling_content, 43U);
    append_create(batch, target, 47U);
    append_create(batch, brush, 75U);
    append_create(batch, cache, 94U);
    append_create(batch, scale_animation, 49U);
    append_command(batch, command::visual_create, root);
    append_command(batch, command::visual_create, cached_visual);
    append_command(batch, command::visual_create, sibling);
    append_command(
        batch, command::visual_set_content, cached_visual, cached_content);
    append_command(
        batch, command::visual_set_content, sibling, sibling_content);
    append_command(
        batch, command::visual_insert_child_at, root, cached_visual, 0U);
    append_command(
        batch, command::visual_insert_child_at, root, sibling, 1U);
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
    std::vector<std::byte> cached_draw;
    append_command(
        cached_draw, command::push_effect, 0xfeedU, 0xbeefU);
    append_command(
        cached_draw,
        command::draw_rectangle,
        4.0,
        6.0,
        24.0,
        18.0,
        brush,
        0U);
    append_command(cached_draw, command::pop);
    append_render_data(batch, cached_content, cached_draw);
    std::vector<std::byte> sibling_draw;
    append_command(
        sibling_draw,
        command::draw_rectangle,
        40.0,
        2.0,
        12.0,
        10.0,
        brush,
        0U);
    append_render_data(batch, sibling_content, sibling_draw);
    append_command(batch, command::double_resource, scale_animation, 1.0);
    append_command(
        batch,
        command::bitmap_cache,
        cache,
        7.0,
        scale_animation,
        0U,
        0U);
    append_command(
        batch,
        command::visual_set_cache_mode,
        cached_visual,
        cache);
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
        state.build_scene(target, 9015U, 1U, stream, &metrics) ==
        status::unsupported_command);
    PROGPU_REQUIRE(
        state.set_visual_cache_bounds(
            cached_visual, 4.0, 6.0, 24.0, 18.0) == status::success);
    PROGPU_REQUIRE(state.resource_generation(cache) == 2U);
    PROGPU_REQUIRE(
        state.build_scene(target, 9015U, 1U, stream, &metrics) ==
        status::success);
    progpu_native_scene_layer first{};
    PROGPU_REQUIRE(try_get_cached_layer(stream, first));
    PROGPU_REQUIRE(
        first.flags == (PROGPU_NATIVE_SCENE_LAYER_CACHE_CONTENT |
            PROGPU_NATIVE_SCENE_LAYER_CACHE_LOCAL_SPACE |
            PROGPU_NATIVE_SCENE_LAYER_BOUNDS));
    PROGPU_REQUIRE(first.bounds.x == 0.0F);
    PROGPU_REQUIRE(first.bounds.y == 0.0F);
    PROGPU_REQUIRE(first.bounds.width == 24.0F);
    PROGPU_REQUIRE(first.bounds.height == 18.0F);
    PROGPU_REQUIRE(first.content_revision != 0U);
    PROGPU_REQUIRE(first.composite_revision != 0U);
    progpu_native_scene_state first_composite{};
    PROGPU_REQUIRE(try_get_state_resource(
        stream, first.reserved0, first_composite));
    PROGPU_REQUIRE(first_composite.transform.m11 == 1.0F);
    PROGPU_REQUIRE(first_composite.transform.m22 == 1.0F);
    PROGPU_REQUIRE(first_composite.transform.m31 == 4.0F);
    PROGPU_REQUIRE(first_composite.transform.m32 == 6.0F);
    progpu_native_scene_state first_raster{};
    PROGPU_REQUIRE(try_get_cached_raster_state(stream, first_raster));
    PROGPU_REQUIRE(first_raster.transform.m11 == 1.0F);
    PROGPU_REQUIRE(first_raster.transform.m22 == 1.0F);
    PROGPU_REQUIRE(first_raster.transform.m31 == -4.0F);
    PROGPU_REQUIRE(first_raster.transform.m32 == -6.0F);

    std::vector<std::byte> sibling_update;
    append_command(
        sibling_update, command::visual_set_offset, sibling, 3.0, 1.0);
    PROGPU_REQUIRE(state.apply(sibling_update) == status::success);
    PROGPU_REQUIRE(
        state.build_scene(target, 9015U, 2U, stream, &metrics) ==
        status::success);
    progpu_native_scene_layer sibling_changed{};
    PROGPU_REQUIRE(try_get_cached_layer(stream, sibling_changed));
    PROGPU_REQUIRE(
        sibling_changed.content_revision == first.content_revision);
    PROGPU_REQUIRE(
        sibling_changed.composite_revision == first.composite_revision);

    std::vector<std::byte> brush_update;
    append_command(
        brush_update,
        command::solid_color_brush,
        brush,
        1.0,
        progpu_native_color{0.75F, 0.25F, 0.5F, 1.0F},
        0U,
        0U,
        0U,
        0U);
    PROGPU_REQUIRE(state.apply(brush_update) == status::success);
    PROGPU_REQUIRE(
        state.build_scene(target, 9015U, 3U, stream, &metrics) ==
        status::success);
    progpu_native_scene_layer brush_changed{};
    PROGPU_REQUIRE(try_get_cached_layer(stream, brush_changed));
    PROGPU_REQUIRE(
        brush_changed.content_revision != first.content_revision);
    PROGPU_REQUIRE(
        brush_changed.composite_revision == first.composite_revision);

    std::vector<std::byte> cached_update;
    append_command(
        cached_update,
        command::visual_set_offset,
        cached_visual,
        2.0,
        0.0);
    PROGPU_REQUIRE(state.apply(cached_update) == status::success);
    PROGPU_REQUIRE(
        state.build_scene(target, 9015U, 4U, stream, &metrics) ==
        status::success);
    progpu_native_scene_layer cached_changed{};
    PROGPU_REQUIRE(try_get_cached_layer(stream, cached_changed));
    PROGPU_REQUIRE(
        cached_changed.content_revision == brush_changed.content_revision);
    PROGPU_REQUIRE(
        cached_changed.composite_revision == first.composite_revision);
    PROGPU_REQUIRE(cached_changed.bounds.x == 0.0F);
    PROGPU_REQUIRE(cached_changed.bounds.y == 0.0F);
    PROGPU_REQUIRE(cached_changed.bounds.width == 24.0F);
    PROGPU_REQUIRE(cached_changed.bounds.height == 18.0F);
    progpu_native_scene_state moved_composite{};
    PROGPU_REQUIRE(try_get_state_resource(
        stream, cached_changed.reserved0, moved_composite));
    PROGPU_REQUIRE(moved_composite.transform.m31 == 6.0F);
    PROGPU_REQUIRE(moved_composite.transform.m32 == 6.0F);

    const auto cache_generation = state.resource_generation(cache);
    std::vector<std::byte> malformed_boolean;
    append_command(
        malformed_boolean,
        command::bitmap_cache,
        cache,
        1.0,
        scale_animation,
        2U,
        0U);
    PROGPU_REQUIRE(
        state.apply(malformed_boolean) == status::malformed_batch);
    PROGPU_REQUIRE(state.resource_generation(cache) == cache_generation);

    std::vector<std::byte> scaled;
    append_command(
        scaled, command::double_resource, scale_animation, 0.5);
    PROGPU_REQUIRE(state.apply(scaled) == status::success);
    PROGPU_REQUIRE(
        state.build_scene(target, 9015U, 5U, stream, &metrics) ==
        status::success);
    progpu_native_scene_layer scaled_layer{};
    PROGPU_REQUIRE(try_get_cached_layer(stream, scaled_layer));
    PROGPU_REQUIRE(scaled_layer.bounds.width == 12.0F);
    PROGPU_REQUIRE(scaled_layer.bounds.height == 9.0F);
    PROGPU_REQUIRE(
        scaled_layer.content_revision != cached_changed.content_revision);
    progpu_native_scene_state scaled_composite{};
    PROGPU_REQUIRE(try_get_state_resource(
        stream, scaled_layer.reserved0, scaled_composite));
    PROGPU_REQUIRE(scaled_composite.transform.m11 == 2.0F);
    PROGPU_REQUIRE(scaled_composite.transform.m22 == 2.0F);
    PROGPU_REQUIRE(scaled_composite.transform.m31 == 6.0F);
    PROGPU_REQUIRE(scaled_composite.transform.m32 == 6.0F);
    progpu_native_scene_state scaled_raster{};
    PROGPU_REQUIRE(try_get_cached_raster_state(stream, scaled_raster));
    PROGPU_REQUIRE(scaled_raster.transform.m11 == 0.5F);
    PROGPU_REQUIRE(scaled_raster.transform.m22 == 0.5F);
    PROGPU_REQUIRE(scaled_raster.transform.m31 == -2.0F);
    PROGPU_REQUIRE(scaled_raster.transform.m32 == -3.0F);
    std::vector<std::byte> unit_scale;
    append_command(
        unit_scale, command::double_resource, scale_animation, 1.0);
    PROGPU_REQUIRE(state.apply(unit_scale) == status::success);
    PROGPU_REQUIRE(
        state.build_scene(target, 9015U, 6U, stream, &metrics) ==
        status::success);
    progpu_native_scene_layer animated{};
    PROGPU_REQUIRE(try_get_cached_layer(stream, animated));
    PROGPU_REQUIRE(
        animated.content_revision != scaled_layer.content_revision);
    PROGPU_REQUIRE(
        animated.composite_revision == first.composite_revision);

    std::vector<std::byte> fractional_offset;
    append_command(
        fractional_offset,
        command::visual_set_offset,
        cached_visual,
        2.25,
        0.75);
    append_command(
        fractional_offset,
        command::bitmap_cache,
        cache,
        1.0,
        scale_animation,
        1U,
        0U);
    PROGPU_REQUIRE(state.apply(fractional_offset) == status::success);
    PROGPU_REQUIRE(
        state.build_scene(target, 9015U, 7U, stream, &metrics) ==
        status::success);
    progpu_native_scene_layer snapped{};
    PROGPU_REQUIRE(try_get_cached_layer(stream, snapped));
    PROGPU_REQUIRE(
        snapped.content_revision == animated.content_revision);
    progpu_native_scene_state snapped_composite{};
    PROGPU_REQUIRE(try_get_state_resource(
        stream, snapped.reserved0, snapped_composite));
    PROGPU_REQUIRE(snapped_composite.transform.m31 == 6.0F);
    PROGPU_REQUIRE(snapped_composite.transform.m32 == 6.0F);

    std::vector<std::byte> clear_type_cache;
    append_command(
        clear_type_cache,
        command::bitmap_cache,
        cache,
        1.0,
        scale_animation,
        0U,
        1U);
    PROGPU_REQUIRE(state.apply(clear_type_cache) == status::success);
    PROGPU_REQUIRE(
        state.build_scene(target, 9015U, 8U, stream, &metrics) ==
        status::success);
    progpu_native_scene_layer clear_type_changed{};
    PROGPU_REQUIRE(try_get_cached_layer(stream, clear_type_changed));
    PROGPU_REQUIRE(
        clear_type_changed.content_revision != snapped.content_revision);

    std::vector<std::byte> delete_animation;
    append_command(
        delete_animation,
        command::channel_delete_resource,
        scale_animation,
        49U);
    PROGPU_REQUIRE(state.apply(delete_animation) == status::invalid_graph);
    std::vector<std::byte> delete_cache;
    append_command(
        delete_cache,
        command::channel_delete_resource,
        cache,
        94U);
    PROGPU_REQUIRE(state.apply(delete_cache) == status::invalid_graph);
    std::vector<std::byte> clear_cache;
    append_command(
        clear_cache,
        command::visual_set_cache_mode,
        cached_visual,
        0U);
    PROGPU_REQUIRE(state.apply(clear_cache) == status::success);
    PROGPU_REQUIRE(state.apply(delete_cache) == status::success);
    PROGPU_REQUIRE(state.apply(delete_animation) == status::success);

    std::vector<std::byte> wrong_type;
    append_command(
        wrong_type,
        command::visual_set_cache_mode,
        cached_visual,
        brush);
    PROGPU_REQUIRE(state.apply(wrong_type) == status::invalid_handle);
    PROGPU_REQUIRE(
        state.set_visual_cache_bounds(
            cached_visual, 0.0, 0.0, 0.0, 1.0) ==
        status::invalid_argument);
    PROGPU_REQUIRE(
        state.set_visual_cache_bounds(
            brush, 0.0, 0.0, 1.0, 1.0) == status::invalid_handle);
    return true;
}

bool visual_bitmap_cache_controls_clear_type_rasterization() {
    constexpr std::uint32_t visual = 1U;
    constexpr std::uint32_t content = 2U;
    constexpr std::uint32_t target = 3U;
    constexpr std::uint32_t brush = 4U;
    constexpr std::uint32_t cache = 5U;
    constexpr std::uint32_t glyph_run = 6U;
    constexpr std::uint32_t child = 7U;

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
    append_create(batch, cache, 94U);
    append_create(batch, child, 39U);
    append_command(batch, command::visual_create, visual);
    append_command(batch, command::visual_create, child);
    append_command(batch, command::visual_set_content, child, content);
    append_command(
        batch, command::visual_insert_child_at, visual, child, 0U);
    append_command(
        batch,
        command::visual_set_render_options,
        visual,
        0x10U,
        0U,
        0U,
        0U,
        0U,
        3U,
        0U);
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
    const std::array glyph_indices{glyph_index};
    const std::array advances{28.0F};
    const std::array offsets{progpu_native_point{}};
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
    std::vector<std::byte> nested;
    append_command(nested, command::draw_glyph_run, brush, glyph_run);
    append_render_data(batch, content, nested);
    append_command(
        batch,
        command::bitmap_cache,
        cache,
        1.0,
        0U,
        0U,
        0U);
    append_command(
        batch,
        command::visual_set_cache_mode,
        visual,
        cache);
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
    PROGPU_REQUIRE(
        state.set_glyph_run_font_sfnt(
            glyph_run, 0U, 0x03U, font_bytes) == status::success);
    PROGPU_REQUIRE(
        state.set_visual_cache_bounds(
            visual, 10.0, 10.0, 36.0, 36.0) == status::success);
    std::vector<std::byte> stream;
    progpu::native::mil::scene_metrics metrics{};
    PROGPU_REQUIRE(
        state.build_scene(target, 9016U, 1U, stream, &metrics) ==
        status::success);
    PROGPU_REQUIRE(scene_contains_text_style_mode(
        stream, PROGPU_NATIVE_SCENE_TEXT_GRAYSCALE));
    PROGPU_REQUIRE(!scene_contains_text_style_mode(
        stream, PROGPU_NATIVE_SCENE_TEXT_CLEARTYPE));

    std::vector<std::byte> enable_clear_type;
    append_command(
        enable_clear_type,
        command::bitmap_cache,
        cache,
        1.0,
        0U,
        0U,
        1U);
    PROGPU_REQUIRE(state.apply(enable_clear_type) == status::success);
    PROGPU_REQUIRE(
        state.build_scene(target, 9016U, 2U, stream, &metrics) ==
        status::success);
    // DrawCacheVisualTree bypasses the cache-root Visual properties. The root
    // text mode is applied to the cached bitmap composite, not its glyphs.
    PROGPU_REQUIRE(scene_contains_text_style_mode(
        stream, PROGPU_NATIVE_SCENE_TEXT_GRAYSCALE));
    PROGPU_REQUIRE(!scene_contains_text_style_mode(
        stream, PROGPU_NATIVE_SCENE_TEXT_CLEARTYPE));

    std::vector<std::byte> child_clear_type;
    append_command(
        child_clear_type,
        command::visual_set_render_options,
        child,
        0x10U,
        0U,
        0U,
        0U,
        0U,
        3U,
        0U);
    PROGPU_REQUIRE(state.apply(child_clear_type) == status::success);
    PROGPU_REQUIRE(
        state.build_scene(target, 9016U, 3U, stream, &metrics) ==
        status::success);
    PROGPU_REQUIRE(scene_contains_text_style_mode(
        stream, PROGPU_NATIVE_SCENE_TEXT_CLEARTYPE));
    return true;
}

bool visual_bitmap_cache_applies_root_state_at_composite() {
    constexpr std::uint32_t visual = 1U;
    constexpr std::uint32_t content = 2U;
    constexpr std::uint32_t target = 3U;
    constexpr std::uint32_t brush = 4U;
    constexpr std::uint32_t cache = 5U;
    constexpr std::uint32_t clip = 6U;
    constexpr std::uint32_t transform = 7U;

    std::vector<std::byte> batch;
    append_create(batch, visual, 39U);
    append_create(batch, content, 43U);
    append_create(batch, target, 47U);
    append_create(batch, brush, 75U);
    append_create(batch, cache, 94U);
    append_create(batch, clip, 69U);
    append_create(batch, transform, 66U);
    append_command(batch, command::visual_create, visual);
    append_command(batch, command::visual_set_content, visual, content);
    append_command(batch, command::visual_set_offset, visual, 3.0, 4.0);
    append_command(
        batch,
        command::matrix_transform,
        transform,
        1.0,
        0.0,
        0.0,
        1.0,
        0.0,
        0.0,
        0U);
    append_command(
        batch, command::visual_set_transform, visual, transform);
    append_command(batch, command::visual_set_clip, visual, clip);
    append_command(
        batch,
        command::visual_set_guideline_collection,
        visual,
        std::uint16_t{1U},
        std::uint16_t{0U},
        std::uint16_t{1U},
        std::uint16_t{0U},
        2.25F,
        3.5F);
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
        4.0,
        6.0,
        10.0,
        8.0,
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
        24.0,
        18.0,
        brush,
        0U);
    append_render_data(batch, content, nested);
    append_command(
        batch,
        command::bitmap_cache,
        cache,
        1.0,
        0U,
        0U,
        0U);
    append_command(
        batch, command::visual_set_cache_mode, visual, cache);
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
    PROGPU_REQUIRE(
        state.set_visual_cache_bounds(
            visual, 0.0, 0.0, 24.0, 18.0) == status::success);
    std::vector<std::byte> stream;
    progpu::native::mil::scene_metrics metrics{};
    PROGPU_REQUIRE(
        state.build_scene(target, 9017U, 1U, stream, &metrics) ==
        status::success);
    progpu_native_scene_layer first{};
    PROGPU_REQUIRE(try_get_cached_layer(stream, first));
    progpu_native_scene_state first_composite{};
    PROGPU_REQUIRE(try_get_state_resource(
        stream, first.reserved0, first_composite));
    PROGPU_REQUIRE(
        first_composite.flags ==
            (PROGPU_NATIVE_SCENE_STATE_CLIP_RECT |
                PROGPU_NATIVE_SCENE_STATE_GUIDELINE_SET));
    PROGPU_REQUIRE(first_composite.clip_rect.x == 7.0F);
    PROGPU_REQUIRE(first_composite.clip_rect.y == 10.0F);
    PROGPU_REQUIRE(first_composite.clip_rect.width == 10.0F);
    PROGPU_REQUIRE(first_composite.clip_rect.height == 8.0F);
    const auto header = read_value<progpu_native_scene_header>(stream, 0U);
    const auto guideline_resource =
        read_value<progpu_native_scene_resource>(
            stream,
            header.resource_offset +
                first_composite.guideline_resource_index *
                    sizeof(progpu_native_scene_resource));
    PROGPU_REQUIRE(
        guideline_resource.kind ==
        PROGPU_NATIVE_SCENE_RESOURCE_GUIDELINE_SET);
    progpu_native_scene_state first_raster{};
    PROGPU_REQUIRE(try_get_cached_raster_state(stream, first_raster));
    PROGPU_REQUIRE(first_raster.flags == 0U);

    std::vector<std::byte> outer_update;
    append_command(
        outer_update,
        command::visual_set_guideline_collection,
        visual,
        std::uint16_t{1U},
        std::uint16_t{0U},
        std::uint16_t{1U},
        std::uint16_t{0U},
        2.75F,
        3.25F);
    append_command(
        outer_update,
        command::rectangle_geometry,
        clip,
        0.0,
        0.0,
        5.0,
        7.0,
        8.0,
        6.0,
        0U,
        0U,
        0U,
        0U);
    PROGPU_REQUIRE(state.apply(outer_update) == status::success);
    PROGPU_REQUIRE(
        state.build_scene(target, 9017U, 2U, stream, &metrics) ==
        status::success);
    progpu_native_scene_layer changed{};
    PROGPU_REQUIRE(try_get_cached_layer(stream, changed));
    PROGPU_REQUIRE(changed.content_revision == first.content_revision);
    progpu_native_scene_state changed_composite{};
    PROGPU_REQUIRE(try_get_state_resource(
        stream, changed.reserved0, changed_composite));
    PROGPU_REQUIRE(changed_composite.clip_rect.x == 8.0F);
    PROGPU_REQUIRE(changed_composite.clip_rect.y == 11.0F);
    PROGPU_REQUIRE(changed_composite.clip_rect.width == 8.0F);
    PROGPU_REQUIRE(changed_composite.clip_rect.height == 6.0F);

    std::vector<std::byte> multi_guideline_update;
    append_command(
        multi_guideline_update,
        command::visual_set_guideline_collection,
        visual,
        std::uint16_t{2U},
        std::uint16_t{0U},
        std::uint16_t{1U},
        std::uint16_t{0U},
        1.25F,
        20.75F,
        3.25F);
    PROGPU_REQUIRE(state.apply(multi_guideline_update) == status::success);
    PROGPU_REQUIRE(
        state.build_scene(target, 9017U, 3U, stream, &metrics) ==
        status::success);
    progpu_native_scene_layer multi_guideline{};
    PROGPU_REQUIRE(try_get_cached_layer(stream, multi_guideline));
    PROGPU_REQUIRE(
        multi_guideline.content_revision == changed.content_revision);
    progpu_native_scene_state multi_composite{};
    PROGPU_REQUIRE(try_get_state_resource(
        stream, multi_guideline.reserved0, multi_composite));
    PROGPU_REQUIRE((multi_composite.flags &
        PROGPU_NATIVE_SCENE_STATE_GUIDELINE_SET) != 0U);
    const auto multi_header =
        read_value<progpu_native_scene_header>(stream, 0U);
    const auto multi_resource =
        read_value<progpu_native_scene_resource>(
            stream,
            multi_header.resource_offset +
                multi_composite.guideline_resource_index *
                    sizeof(progpu_native_scene_resource));
    const auto multi_set =
        read_value<progpu_native_scene_guideline_set>(
            stream, multi_resource.payload_offset);
    PROGPU_REQUIRE(
        multi_set.flags ==
            PROGPU_NATIVE_SCENE_GUIDELINE_COMPOSITE_ONLY);
    PROGPU_REQUIRE(multi_set.guideline_x_count == 2U);
    PROGPU_REQUIRE(multi_set.guideline_y_count == 1U);
    PROGPU_REQUIRE(read_value<double>(
        stream,
        multi_resource.payload_offset + sizeof(multi_set)) == 4.25);
    PROGPU_REQUIRE(read_value<double>(
        stream,
        multi_resource.payload_offset + sizeof(multi_set) +
            sizeof(double)) == 23.75);
    PROGPU_REQUIRE(read_value<double>(
        stream,
        multi_resource.payload_offset + sizeof(multi_set) +
            2U * sizeof(double)) == 7.25);

    std::vector<std::byte> negative_scale_update;
    append_command(
        negative_scale_update,
        command::matrix_transform,
        transform,
        -1.0,
        0.0,
        0.0,
        1.0,
        30.0,
        0.0,
        0U);
    PROGPU_REQUIRE(state.apply(negative_scale_update) == status::success);
    PROGPU_REQUIRE(
        state.build_scene(target, 9017U, 4U, stream, &metrics) ==
        status::success);
    progpu_native_scene_layer negative_scale{};
    PROGPU_REQUIRE(try_get_cached_layer(stream, negative_scale));
    PROGPU_REQUIRE(
        negative_scale.content_revision == multi_guideline.content_revision);
    progpu_native_scene_state negative_composite{};
    PROGPU_REQUIRE(try_get_state_resource(
        stream, negative_scale.reserved0, negative_composite));
    const auto negative_header =
        read_value<progpu_native_scene_header>(stream, 0U);
    const auto negative_resource =
        read_value<progpu_native_scene_resource>(
            stream,
            negative_header.resource_offset +
                negative_composite.guideline_resource_index *
                    sizeof(progpu_native_scene_resource));
    const auto negative_set =
        read_value<progpu_native_scene_guideline_set>(
            stream, negative_resource.payload_offset);
    PROGPU_REQUIRE(read_value<double>(
        stream,
        negative_resource.payload_offset + sizeof(negative_set)) == 12.25);
    PROGPU_REQUIRE(read_value<double>(
        stream,
        negative_resource.payload_offset + sizeof(negative_set) +
            sizeof(double)) == 31.75);

    std::vector<std::byte> nearest_sampling;
    append_command(
        nearest_sampling,
        command::visual_set_render_options,
        visual,
        0x01U,
        0U,
        0U,
        3U,
        0U,
        0U,
        0U);
    PROGPU_REQUIRE(state.apply(nearest_sampling) == status::success);
    PROGPU_REQUIRE(
        state.build_scene(target, 9017U, 5U, stream, &metrics) ==
        status::success);
    progpu_native_scene_layer nearest{};
    PROGPU_REQUIRE(try_get_cached_layer(stream, nearest));
    PROGPU_REQUIRE(
        (nearest.flags & PROGPU_NATIVE_SCENE_LAYER_CACHE_NEAREST) != 0U);
    PROGPU_REQUIRE(nearest.content_revision == changed.content_revision);

    std::vector<std::byte> fant_sampling;
    append_command(
        fant_sampling,
        command::visual_set_render_options,
        visual,
        0x01U,
        0U,
        0U,
        2U,
        0U,
        0U,
        0U);
    PROGPU_REQUIRE(state.apply(fant_sampling) == status::success);
    PROGPU_REQUIRE(
        state.build_scene(target, 9017U, 6U, stream, &metrics) ==
        status::success);
    progpu_native_scene_layer fant{};
    PROGPU_REQUIRE(try_get_cached_layer(stream, fant));
    PROGPU_REQUIRE(
        (fant.flags & PROGPU_NATIVE_SCENE_LAYER_CACHE_FANT) != 0U);
    PROGPU_REQUIRE(
        (fant.flags & PROGPU_NATIVE_SCENE_LAYER_CACHE_NEAREST) == 0U);
    PROGPU_REQUIRE(fant.content_revision == changed.content_revision);
    return true;
}

bool visual_bitmap_cache_applies_gradient_mask_at_composite() {
    constexpr std::uint32_t visual = 1U;
    constexpr std::uint32_t content = 2U;
    constexpr std::uint32_t target = 3U;
    constexpr std::uint32_t content_brush = 4U;
    constexpr std::uint32_t cache = 5U;
    constexpr std::uint32_t mask_brush = 6U;
    constexpr std::uint32_t radial_mask_brush = 7U;
    constexpr std::uint32_t blur = 8U;

    std::vector<std::byte> batch;
    append_create(batch, visual, 39U);
    append_create(batch, content, 43U);
    append_create(batch, target, 47U);
    append_create(batch, content_brush, 75U);
    append_create(batch, cache, 94U);
    append_create(batch, mask_brush, 77U);
    append_command(batch, command::visual_create, visual);
    append_command(batch, command::visual_set_content, visual, content);
    append_command(batch, command::visual_set_offset, visual, 3.0, 4.0);
    append_command(
        batch,
        command::solid_color_brush,
        content_brush,
        1.0,
        progpu_native_color{0.2F, 0.7F, 1.0F, 1.0F},
        0U,
        0U,
        0U,
        0U);
    const std::array mask_stops{
        mil_gradient_stop{0.0, {1.0F, 1.0F, 1.0F, 0.0F}},
        mil_gradient_stop{1.0, {1.0F, 1.0F, 1.0F, 1.0F}}};
    append_linear_gradient_brush(
        batch,
        mask_brush,
        1.0,
        0.0,
        0.0,
        1.0,
        0.0,
        0U,
        0U,
        0U,
        1U,
        1U,
        0U,
        0U,
        0U,
        mask_stops);
    append_command(
        batch, command::visual_set_alpha_mask, visual, mask_brush);
    std::vector<std::byte> nested;
    append_command(
        nested,
        command::draw_rectangle,
        0.0,
        0.0,
        24.0,
        18.0,
        content_brush,
        0U);
    append_render_data(batch, content, nested);
    append_command(
        batch,
        command::bitmap_cache,
        cache,
        1.0,
        0U,
        0U,
        0U);
    append_command(batch, command::visual_set_cache_mode, visual, cache);
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
    PROGPU_REQUIRE(
        state.set_visual_cache_bounds(
            visual, 0.0, 0.0, 24.0, 18.0) == status::success);
    std::vector<std::byte> stream;
    progpu::native::mil::scene_metrics metrics{};
    PROGPU_REQUIRE(
        state.build_scene(target, 9018U, 1U, stream, &metrics) ==
        status::success);
    progpu_native_scene_layer first{};
    PROGPU_REQUIRE(try_get_cached_layer(stream, first));
    PROGPU_REQUIRE(
        first.mask_resource_index != PROGPU_NATIVE_SCENE_NO_INDEX);
    progpu_native_scene_layer_brush_mask first_mask{};
    std::vector<progpu_native_scene_gradient_stop> first_stops;
    PROGPU_REQUIRE(try_get_brush_mask_resource(
        stream,
        first.mask_resource_index,
        first_mask,
        first_stops));
    PROGPU_REQUIRE(
        first_mask.kind == PROGPU_NATIVE_SCENE_LAYER_MASK_BRUSH);
    PROGPU_REQUIRE(first_mask.bounds.x == 0.0F);
    PROGPU_REQUIRE(first_mask.bounds.y == 0.0F);
    PROGPU_REQUIRE(first_mask.bounds.width == 24.0F);
    PROGPU_REQUIRE(first_mask.bounds.height == 18.0F);
    PROGPU_REQUIRE(first_mask.transform.m31 == 3.0F);
    PROGPU_REQUIRE(first_mask.transform.m32 == 4.0F);
    PROGPU_REQUIRE(
        first_mask.brush.type ==
            PROGPU_NATIVE_SCENE_BRUSH_LINEAR_GRADIENT);
    PROGPU_REQUIRE(first_mask.brush.start_point.x == 0.0F);
    PROGPU_REQUIRE(first_mask.brush.end_point.x == 24.0F);
    PROGPU_REQUIRE(first_mask.brush.coordinate_transform0[2] == -3.0F);
    PROGPU_REQUIRE(first_mask.brush.coordinate_transform1[2] == -4.0F);
    PROGPU_REQUIRE(first_stops.size() == 2U);
    PROGPU_REQUIRE(first_stops.front().color.a == 0.0F);
    PROGPU_REQUIRE(first_stops.back().color.a == 1.0F);

    std::vector<std::byte> mask_update;
    append_create(mask_update, radial_mask_brush, 78U);
    append_radial_gradient_brush(
        mask_update,
        radial_mask_brush,
        0.5,
        0.5,
        0.5,
        0.5,
        0.5,
        0.5,
        0.5,
        1U,
        1U,
        0U,
        0U,
        0U,
        mask_stops);
    append_command(
        mask_update,
        command::visual_set_alpha_mask,
        visual,
        radial_mask_brush);
    PROGPU_REQUIRE(state.apply(mask_update) == status::success);
    PROGPU_REQUIRE(
        state.build_scene(target, 9018U, 2U, stream, &metrics) ==
        status::success);
    progpu_native_scene_layer changed{};
    PROGPU_REQUIRE(try_get_cached_layer(stream, changed));
    PROGPU_REQUIRE(changed.content_revision == first.content_revision);
    progpu_native_scene_layer_brush_mask changed_mask{};
    std::vector<progpu_native_scene_gradient_stop> changed_stops;
    PROGPU_REQUIRE(try_get_brush_mask_resource(
        stream,
        changed.mask_resource_index,
        changed_mask,
        changed_stops));
    PROGPU_REQUIRE(changed_mask.brush.opacity == 0.5F);
    PROGPU_REQUIRE(
        changed_mask.brush.type ==
            PROGPU_NATIVE_SCENE_BRUSH_RADIAL_GRADIENT);
    PROGPU_REQUIRE(changed_mask.brush.center.x == 12.0F);
    PROGPU_REQUIRE(changed_mask.brush.center.y == 9.0F);
    PROGPU_REQUIRE(changed_mask.brush.radius == 12.0F);
    PROGPU_REQUIRE(changed_mask.brush.radius_y == 9.0F);

    std::vector<std::byte> combined_ordering;
    append_command(
        combined_ordering,
        command::visual_set_guideline_collection,
        visual,
        std::uint16_t{1U},
        std::uint16_t{0U},
        std::uint16_t{1U},
        std::uint16_t{0U},
        2.25F,
        3.5F);
    PROGPU_REQUIRE(state.apply(combined_ordering) == status::success);
    PROGPU_REQUIRE(
        state.build_scene(target, 9018U, 3U, stream, &metrics) ==
        status::success);
    progpu_native_scene_layer guided{};
    PROGPU_REQUIRE(try_get_cached_layer(stream, guided));
    PROGPU_REQUIRE(guided.content_revision == changed.content_revision);
    PROGPU_REQUIRE(
        guided.mask_resource_index != PROGPU_NATIVE_SCENE_NO_INDEX);
    progpu_native_scene_state guided_composite{};
    PROGPU_REQUIRE(try_get_state_resource(
        stream, guided.reserved0, guided_composite));
    PROGPU_REQUIRE((guided_composite.flags &
        PROGPU_NATIVE_SCENE_STATE_GUIDELINE_SET) != 0U);
    const auto guided_header = read_value<progpu_native_scene_header>(
        stream, 0U);
    const auto guideline_resource = read_value<progpu_native_scene_resource>(
        stream,
        guided_header.resource_offset +
            guided_composite.guideline_resource_index *
                sizeof(progpu_native_scene_resource));
    PROGPU_REQUIRE(
        guideline_resource.kind ==
            PROGPU_NATIVE_SCENE_RESOURCE_GUIDELINE_SET);
    const auto guideline_set = read_value<progpu_native_scene_guideline_set>(
        stream, guideline_resource.payload_offset);
    PROGPU_REQUIRE(guideline_set.guideline_x_count == 1U);
    PROGPU_REQUIRE(guideline_set.guideline_y_count == 1U);

    constexpr std::uint32_t cache_clip = 9U;
    std::vector<std::byte> clipped_cache;
    append_create(clipped_cache, cache_clip, 69U);
    append_command(clipped_cache, command::rectangle_geometry, cache_clip,
        3.0, 3.0, 0.0, 0.0, 24.0, 18.0, 0U, 0U, 0U, 0U);
    append_command(clipped_cache, command::visual_set_clip, visual, cache_clip);
    PROGPU_REQUIRE(state.apply(clipped_cache) == status::success);
    PROGPU_REQUIRE(state.build_scene(target, 9018U, 4U, stream, &metrics) == status::success);
    progpu_native_scene_layer clipped{};
    PROGPU_REQUIRE(try_get_cached_layer(stream, clipped));
    PROGPU_REQUIRE(clipped.content_revision == guided.content_revision);
    const auto composite_header = read_value<progpu_native_scene_header>(stream, 0U);
    const auto composite_resource = read_value<progpu_native_scene_resource>(stream,
        composite_header.resource_offset + clipped.mask_resource_index *
            sizeof(progpu_native_scene_resource));
    const auto composite_mask = read_value<progpu_native_scene_layer_composite_mask>(
        stream, composite_resource.payload_offset);
    PROGPU_REQUIRE(composite_mask.kind == PROGPU_NATIVE_SCENE_LAYER_MASK_COMPOSITE);
    PROGPU_REQUIRE(composite_mask.path_count == 1U);
    PROGPU_REQUIRE(composite_mask.brush_mask_count == 1U);
    std::vector<std::byte> changed_clip;
    append_command(changed_clip, command::rectangle_geometry, cache_clip,
        5.0, 5.0, 1.0, 1.0, 22.0, 16.0, 0U, 0U, 0U, 0U);
    PROGPU_REQUIRE(state.apply(changed_clip) == status::success);
    PROGPU_REQUIRE(state.build_scene(target, 9018U, 5U, stream, &metrics) == status::success);
    progpu_native_scene_layer reclipped{};
    PROGPU_REQUIRE(try_get_cached_layer(stream, reclipped));
    PROGPU_REQUIRE(reclipped.content_revision == guided.content_revision);
    PROGPU_REQUIRE(reclipped.composite_revision == guided.composite_revision);

    std::vector<std::byte> masked_effect;
    append_create(masked_effect, blur, 36U);
    append_command(
        masked_effect,
        command::blur_effect,
        blur,
        6.0,
        0U,
        0U,
        1U);
    append_command(masked_effect, command::visual_set_effect, visual, blur);
    append_command(masked_effect, command::visual_set_alpha, visual, 0.5);
    PROGPU_REQUIRE(state.apply(masked_effect) == status::success);
    PROGPU_REQUIRE(
        state.build_scene(target, 9018U, 6U, stream, &metrics) ==
        status::success);
    const auto layers = get_scene_layers(stream);
    PROGPU_REQUIRE(layers.size() == 2U);
    PROGPU_REQUIRE(
        layers[0].effect_resource_index !=
            PROGPU_NATIVE_SCENE_NO_INDEX);
    PROGPU_REQUIRE(
        (layers[0].flags & PROGPU_NATIVE_SCENE_LAYER_CACHE_CONTENT) == 0U);
    PROGPU_REQUIRE(
        (layers[1].flags & PROGPU_NATIVE_SCENE_LAYER_CACHE_CONTENT) != 0U);
    PROGPU_REQUIRE(
        layers[1].mask_resource_index !=
            PROGPU_NATIVE_SCENE_NO_INDEX);
    PROGPU_REQUIRE(layers[1].opacity == 0.5F);
    PROGPU_REQUIRE(
        layers[1].content_revision == changed.content_revision);
    progpu_native_scene_state effect_cache_composite{};
    PROGPU_REQUIRE(try_get_state_resource(
        stream, layers[1].reserved0, effect_cache_composite));
    PROGPU_REQUIRE((effect_cache_composite.flags &
        PROGPU_NATIVE_SCENE_STATE_GUIDELINE_SET) != 0U);
    const auto effect_guided_header = read_value<progpu_native_scene_header>(
        stream, 0U);
    const auto effect_guideline_resource =
        read_value<progpu_native_scene_resource>(
            stream,
            effect_guided_header.resource_offset +
                effect_cache_composite.guideline_resource_index *
                    sizeof(progpu_native_scene_resource));
    PROGPU_REQUIRE(
        effect_guideline_resource.kind ==
            PROGPU_NATIVE_SCENE_RESOURCE_GUIDELINE_SET);

    std::vector<std::byte> uncached_masked_effect;
    append_command(
        uncached_masked_effect,
        command::visual_set_cache_mode,
        visual,
        0U);
    PROGPU_REQUIRE(state.apply(uncached_masked_effect) == status::success);
    PROGPU_REQUIRE(
        state.build_scene(target, 9018U, 7U, stream, &metrics) ==
        status::success);
    const auto uncached_layers = get_scene_layers(stream);
    PROGPU_REQUIRE(uncached_layers.size() == 2U);
    PROGPU_REQUIRE(
        uncached_layers[0].effect_resource_index !=
            PROGPU_NATIVE_SCENE_NO_INDEX);
    PROGPU_REQUIRE(
        (uncached_layers[1].flags &
            PROGPU_NATIVE_SCENE_LAYER_FORCE_ISOLATION) != 0U);
    PROGPU_REQUIRE(
        (uncached_layers[1].flags &
            PROGPU_NATIVE_SCENE_LAYER_CACHE_CONTENT) == 0U);
    PROGPU_REQUIRE(
        uncached_layers[1].mask_resource_index !=
            PROGPU_NATIVE_SCENE_NO_INDEX);
    PROGPU_REQUIRE(uncached_layers[1].opacity == 0.5F);
    PROGPU_REQUIRE(uncached_layers[1].bounds.x == 3.0F);
    PROGPU_REQUIRE(uncached_layers[1].bounds.y == 4.0F);
    PROGPU_REQUIRE(uncached_layers[1].bounds.width == 24.0F);
    PROGPU_REQUIRE(uncached_layers[1].bounds.height == 18.0F);
    return true;
}

bool visual_bitmap_cache_preserves_nested_effect_ordering() {
    constexpr std::uint32_t root = 1U;
    constexpr std::uint32_t child = 2U;
    constexpr std::uint32_t child_content = 3U;
    constexpr std::uint32_t target = 4U;
    constexpr std::uint32_t brush = 5U;
    constexpr std::uint32_t root_cache = 6U;
    constexpr std::uint32_t child_cache = 7U;
    constexpr std::uint32_t blur = 8U;
    constexpr std::uint32_t clip = 9U;
    constexpr std::uint32_t root_mask = 10U;
    constexpr std::uint32_t child_mask = 11U;
    constexpr std::uint32_t changed_root_mask = 12U;
    constexpr std::uint32_t changed_child_mask = 13U;

    std::vector<std::byte> batch;
    append_create(batch, root, 39U);
    append_create(batch, child, 39U);
    append_create(batch, child_content, 43U);
    append_create(batch, target, 47U);
    append_create(batch, brush, 75U);
    append_create(batch, root_cache, 94U);
    append_create(batch, child_cache, 94U);
    append_create(batch, blur, 36U);
    append_create(batch, clip, 69U);
    append_create(batch, root_mask, 77U);
    append_create(batch, child_mask, 77U);
    append_create(batch, changed_root_mask, 77U);
    append_create(batch, changed_child_mask, 77U);
    append_command(batch, command::visual_create, root);
    append_command(batch, command::visual_create, child);
    append_command(batch, command::visual_set_content, child, child_content);
    append_command(batch, command::visual_set_offset, child, 5.0, 6.0);
    append_command(batch, command::visual_set_alpha, child, 0.5);
    append_command(batch, command::visual_set_clip, child, clip);
    const std::array mask_stops{
        mil_gradient_stop{0.0, {1.0F, 1.0F, 1.0F, 0.0F}},
        mil_gradient_stop{1.0, {1.0F, 1.0F, 1.0F, 1.0F}}};
    append_linear_gradient_brush(
        batch,
        root_mask,
        1.0,
        0.0,
        0.0,
        1.0,
        0.0,
        0U,
        0U,
        0U,
        1U,
        1U,
        0U,
        0U,
        0U,
        mask_stops);
    append_linear_gradient_brush(
        batch,
        child_mask,
        1.0,
        0.0,
        0.0,
        0.0,
        1.0,
        0U,
        0U,
        0U,
        1U,
        1U,
        0U,
        0U,
        0U,
        mask_stops);
    append_linear_gradient_brush(
        batch,
        changed_root_mask,
        0.5,
        0.0,
        0.0,
        1.0,
        0.0,
        0U,
        0U,
        0U,
        1U,
        1U,
        0U,
        0U,
        0U,
        mask_stops);
    append_linear_gradient_brush(
        batch,
        changed_child_mask,
        0.5,
        0.0,
        0.0,
        0.0,
        1.0,
        0U,
        0U,
        0U,
        1U,
        1U,
        0U,
        0U,
        0U,
        mask_stops);
    append_command(
        batch, command::visual_set_alpha_mask, root, root_mask);
    append_command(
        batch, command::visual_set_alpha_mask, child, child_mask);
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
    std::vector<std::byte> nested;
    append_command(
        nested,
        command::draw_rectangle,
        0.0,
        0.0,
        16.0,
        12.0,
        brush,
        0U);
    append_render_data(batch, child_content, nested);
    append_command(
        batch, command::blur_effect, blur, 6.0, 0U, 0U, 1U);
    append_command(
        batch,
        command::rectangle_geometry,
        clip,
        0.0,
        0.0,
        2.0,
        1.0,
        12.0,
        10.0,
        0U,
        0U,
        0U,
        0U);
    append_command(batch, command::visual_set_effect, child, blur);
    append_command(
        batch, command::bitmap_cache, root_cache, 1.0, 0U, 0U, 0U);
    append_command(
        batch, command::bitmap_cache, child_cache, 1.0, 0U, 0U, 0U);
    append_command(batch, command::visual_set_cache_mode, root, root_cache);
    append_command(
        batch, command::visual_set_cache_mode, child, child_cache);
    append_command(
        batch, command::visual_insert_child_at, root, child, 0U);
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
    PROGPU_REQUIRE(
        state.set_visual_cache_bounds(
            root, 0.0, 0.0, 48.0, 40.0) == status::success);
    PROGPU_REQUIRE(
        state.set_visual_cache_bounds(
            child, 0.0, 0.0, 16.0, 12.0) == status::success);
    std::vector<std::byte> stream;
    progpu::native::mil::scene_metrics metrics{};
    PROGPU_REQUIRE(
        state.build_scene(target, 9023U, 1U, stream, &metrics) ==
        status::success);
    auto layers = get_scene_layers(stream);
    PROGPU_REQUIRE(layers.size() == 3U);
    PROGPU_REQUIRE(
        (layers[0].flags & PROGPU_NATIVE_SCENE_LAYER_CACHE_CONTENT) != 0U);
    PROGPU_REQUIRE(
        layers[0].mask_resource_index != PROGPU_NATIVE_SCENE_NO_INDEX);
    PROGPU_REQUIRE(
        layers[1].effect_resource_index !=
            PROGPU_NATIVE_SCENE_NO_INDEX);
    PROGPU_REQUIRE(
        (layers[1].flags & PROGPU_NATIVE_SCENE_LAYER_CACHE_CONTENT) == 0U);
    PROGPU_REQUIRE(
        (layers[1].flags & PROGPU_NATIVE_SCENE_LAYER_COMPOSITE_STATE) !=
        0U);
    PROGPU_REQUIRE(
        (layers[2].flags & PROGPU_NATIVE_SCENE_LAYER_CACHE_CONTENT) != 0U);
    PROGPU_REQUIRE(
        layers[2].mask_resource_index != PROGPU_NATIVE_SCENE_NO_INDEX);
    PROGPU_REQUIRE(layers[1].opacity == 1.0F);
    PROGPU_REQUIRE(layers[2].opacity == 0.5F);
    progpu_native_scene_state effect_composite{};
    PROGPU_REQUIRE(try_get_state_resource(
        stream, layers[1].reserved0, effect_composite));
    PROGPU_REQUIRE(
        effect_composite.flags == PROGPU_NATIVE_SCENE_STATE_CLIP_RECT);
    PROGPU_REQUIRE(effect_composite.clip_rect.x == 7.0F);
    PROGPU_REQUIRE(effect_composite.clip_rect.y == 7.0F);
    PROGPU_REQUIRE(effect_composite.clip_rect.width == 12.0F);
    PROGPU_REQUIRE(effect_composite.clip_rect.height == 10.0F);
    progpu_native_scene_state cache_composite{};
    PROGPU_REQUIRE(try_get_state_resource(
        stream, layers[2].reserved0, cache_composite));
    PROGPU_REQUIRE(
        (cache_composite.flags & PROGPU_NATIVE_SCENE_STATE_CLIP_RECT) ==
        0U);
    const auto first_root = layers[0];
    const auto first_child = layers[2];

    std::vector<std::byte> root_mask_update;
    append_command(
        root_mask_update,
        command::visual_set_alpha_mask,
        root,
        changed_root_mask);
    PROGPU_REQUIRE(state.apply(root_mask_update) == status::success);
    PROGPU_REQUIRE(
        state.build_scene(target, 9023U, 2U, stream, &metrics) ==
        status::success);
    layers = get_scene_layers(stream);
    PROGPU_REQUIRE(layers.size() == 3U);
    PROGPU_REQUIRE(
        layers[0].content_revision == first_root.content_revision);
    PROGPU_REQUIRE(
        layers[2].content_revision == first_child.content_revision);
    progpu_native_scene_layer_brush_mask root_mask_resource{};
    std::vector<progpu_native_scene_gradient_stop> root_mask_stops;
    PROGPU_REQUIRE(try_get_brush_mask_resource(
        stream,
        layers[0].mask_resource_index,
        root_mask_resource,
        root_mask_stops));
    PROGPU_REQUIRE(root_mask_resource.brush.opacity == 0.5F);
    const auto root_masked_root = layers[0];
    const auto root_masked_child = layers[2];

    std::vector<std::byte> child_mask_update;
    append_command(
        child_mask_update,
        command::visual_set_alpha_mask,
        child,
        changed_child_mask);
    PROGPU_REQUIRE(state.apply(child_mask_update) == status::success);
    PROGPU_REQUIRE(
        state.build_scene(target, 9023U, 3U, stream, &metrics) ==
        status::success);
    layers = get_scene_layers(stream);
    PROGPU_REQUIRE(layers.size() == 3U);
    PROGPU_REQUIRE(
        layers[0].content_revision !=
            root_masked_root.content_revision);
    PROGPU_REQUIRE(
        layers[2].content_revision ==
            root_masked_child.content_revision);
    progpu_native_scene_layer_brush_mask child_mask_resource{};
    std::vector<progpu_native_scene_gradient_stop> child_mask_stops;
    PROGPU_REQUIRE(try_get_brush_mask_resource(
        stream,
        layers[2].mask_resource_index,
        child_mask_resource,
        child_mask_stops));
    PROGPU_REQUIRE(child_mask_resource.brush.opacity == 0.5F);
    const auto combined_root = layers[0];
    const auto combined_child = layers[2];

    std::vector<std::byte> child_composite_update;
    append_command(
        child_composite_update,
        command::visual_set_offset,
        child,
        7.0,
        6.0);
    PROGPU_REQUIRE(
        state.apply(child_composite_update) == status::success);
    PROGPU_REQUIRE(
        state.build_scene(target, 9023U, 4U, stream, &metrics) ==
        status::success);
    layers = get_scene_layers(stream);
    PROGPU_REQUIRE(layers.size() == 3U);
    PROGPU_REQUIRE(
        layers[0].content_revision != combined_root.content_revision);
    PROGPU_REQUIRE(
        layers[0].composite_revision == combined_root.composite_revision);
    PROGPU_REQUIRE(
        layers[2].content_revision == combined_child.content_revision);
    PROGPU_REQUIRE(
        layers[2].composite_revision == combined_child.composite_revision);
    const auto moved_root = layers[0];
    const auto moved_child = layers[2];

    std::vector<std::byte> root_composite_update;
    append_command(
        root_composite_update,
        command::visual_set_offset,
        root,
        3.0,
        4.0);
    PROGPU_REQUIRE(state.apply(root_composite_update) == status::success);
    PROGPU_REQUIRE(
        state.build_scene(target, 9023U, 5U, stream, &metrics) ==
        status::success);
    layers = get_scene_layers(stream);
    PROGPU_REQUIRE(layers.size() == 3U);
    PROGPU_REQUIRE(
        layers[0].content_revision == moved_root.content_revision);
    PROGPU_REQUIRE(
        layers[2].content_revision == moved_child.content_revision);

    std::vector<std::byte> effect_update;
    append_command(
        effect_update, command::blur_effect, blur, 9.0, 0U, 0U, 1U);
    PROGPU_REQUIRE(state.apply(effect_update) == status::success);
    PROGPU_REQUIRE(
        state.build_scene(target, 9023U, 6U, stream, &metrics) ==
        status::success);
    layers = get_scene_layers(stream);
    PROGPU_REQUIRE(layers.size() == 3U);
    PROGPU_REQUIRE(
        layers[0].content_revision != moved_root.content_revision);
    PROGPU_REQUIRE(
        layers[2].content_revision == moved_child.content_revision);
    PROGPU_REQUIRE(
        layers[2].composite_revision == moved_child.composite_revision);
    return true;
}

bool visual_static_guidelines_reset_at_child_boundaries() {
    constexpr std::uint32_t root = 1U;
    constexpr std::uint32_t child = 2U;
    constexpr std::uint32_t root_content = 3U;
    constexpr std::uint32_t child_content = 4U;
    constexpr std::uint32_t target = 5U;
    constexpr std::uint32_t brush = 6U;

    std::vector<std::byte> batch;
    append_create(batch, root, 39U);
    append_create(batch, child, 39U);
    append_create(batch, root_content, 43U);
    append_create(batch, child_content, 43U);
    append_create(batch, target, 47U);
    append_create(batch, brush, 75U);
    append_command(batch, command::visual_create, root);
    append_command(batch, command::visual_create, child);
    append_command(batch, command::visual_set_offset, root, 10.0, 20.0);
    append_command(batch, command::visual_set_offset, child, 5.0, 0.0);
    append_command(
        batch,
        command::visual_set_guideline_collection,
        root,
        std::uint16_t{1U},
        std::uint16_t{0U},
        std::uint16_t{1U},
        std::uint16_t{0U},
        2.25F,
        3.5F);
    append_command(
        batch, command::visual_set_content, root, root_content);
    append_command(
        batch, command::visual_set_content, child, child_content);
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
    std::vector<std::byte> root_records;
    append_command(
        root_records,
        command::draw_rectangle,
        0.0,
        0.0,
        20.0,
        20.0,
        brush,
        0U);
    append_render_data(batch, root_content, root_records);
    std::vector<std::byte> child_records;
    append_command(
        child_records,
        command::draw_rectangle,
        0.0,
        0.0,
        20.0,
        20.0,
        brush,
        0U);
    append_render_data(batch, child_content, child_records);
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
        state.build_scene(target, 9012U, 1U, stream, &metrics) ==
        status::success);
    const auto header = read_value<progpu_native_scene_header>(stream, 0U);
    bool found_guidelines = false;
    bool found_root_state = false;
    bool found_child_reset = false;
    for (std::uint32_t index = 0U; index < header.resource_count; ++index) {
        const auto resource = read_value<progpu_native_scene_resource>(
            stream,
            header.resource_offset +
                index * sizeof(progpu_native_scene_resource));
        if (resource.kind == PROGPU_NATIVE_SCENE_RESOURCE_GUIDELINE_SET) {
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
        } else if (resource.kind == PROGPU_NATIVE_SCENE_RESOURCE_STATE) {
            const auto scene_state = read_value<progpu_native_scene_state>(
                stream, resource.payload_offset);
            const bool has_guidelines = (scene_state.flags &
                PROGPU_NATIVE_SCENE_STATE_GUIDELINE_SET) != 0U;
            found_root_state |= has_guidelines &&
                scene_state.transform.m31 == 10.0F &&
                scene_state.transform.m32 == 20.0F;
            found_child_reset |= !has_guidelines &&
                scene_state.transform.m31 == 15.0F &&
                scene_state.transform.m32 == 20.0F;
        }
    }
    PROGPU_REQUIRE(found_guidelines);
    PROGPU_REQUIRE(found_root_state);
    PROGPU_REQUIRE(found_child_reset);

    std::vector<std::byte> multiple;
    append_command(
        multiple,
        command::visual_set_guideline_collection,
        root,
        std::uint16_t{2U},
        std::uint16_t{0U},
        std::uint16_t{0U},
        std::uint16_t{0U},
        1.0F,
        2.0F);
    PROGPU_REQUIRE(state.apply(multiple) == status::success);
    PROGPU_REQUIRE(
        state.build_scene(target, 9012U, 2U, stream, &metrics) ==
        status::success);
    const auto multiple_header = read_value<progpu_native_scene_header>(
        stream, 0U);
    bool found_per_point = false;
    for (std::uint32_t index = 0U;
         index < multiple_header.resource_count;
         ++index) {
        const auto resource = read_value<progpu_native_scene_resource>(
            stream,
            multiple_header.resource_offset +
                index * sizeof(progpu_native_scene_resource));
        if (resource.kind != PROGPU_NATIVE_SCENE_RESOURCE_GUIDELINE_SET) {
            continue;
        }
        const auto value = read_value<progpu_native_scene_guideline_set>(
            stream, resource.payload_offset);
        found_per_point |= value.flags ==
                PROGPU_NATIVE_SCENE_GUIDELINE_PER_POINT &&
            value.guideline_x_count == 2U;
    }
    PROGPU_REQUIRE(found_per_point);

    std::vector<std::byte> clear;
    append_command(
        clear,
        command::visual_set_guideline_collection,
        root,
        std::uint16_t{0U},
        std::uint16_t{0U},
        std::uint16_t{0U},
        std::uint16_t{0U});
    PROGPU_REQUIRE(state.apply(clear) == status::success);
    PROGPU_REQUIRE(
        state.build_scene(target, 9012U, 3U, stream, &metrics) ==
        status::success);

    std::vector<std::byte> malformed;
    append_command(
        malformed,
        command::visual_set_guideline_collection,
        root,
        std::uint16_t{0U},
        std::uint16_t{1U},
        std::uint16_t{0U},
        std::uint16_t{0U});
    PROGPU_REQUIRE(state.apply(malformed) == status::malformed_batch);
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
    PROGPU_REQUIRE(header.resource_count == 6U);

    bool found_visual_state = false;
    bool found_transform_state = false;
    bool found_clip_state = false;
    bool found_nested_clip_state = false;
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
    const auto layers = get_scene_layers(stream);
    PROGPU_REQUIRE(layers.size() == 1U);
    PROGPU_REQUIRE(layers[0].opacity == 0.5F);
    PROGPU_REQUIRE(
        (layers[0].flags & PROGPU_NATIVE_SCENE_LAYER_FORCE_ISOLATION) !=
        0U);
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
    PROGPU_REQUIRE(state.apply(animated_pen) == status::invalid_handle);
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
    PROGPU_REQUIRE(state.apply(animated_dash) == status::invalid_handle);
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
        PROGPU_REQUIRE(
            (stroke.flags &
                PROGPU_NATIVE_POLYLINE_FLAG_WPF_JOIN_SEMANTICS) != 0U);
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
        status::success);
    const auto dashed_degenerate_rectangle_header =
        read_value<progpu_native_scene_header>(stream, 0U);
    bool found_dashed_degenerate_rectangle = false;
    for (std::uint32_t index = 0U;
         index < dashed_degenerate_rectangle_header.resource_count;
         ++index) {
        const auto record = read_value<progpu_native_scene_resource>(
            stream,
            dashed_degenerate_rectangle_header.resource_offset +
                index * sizeof(progpu_native_scene_resource));
        if (record.kind != PROGPU_NATIVE_SCENE_RESOURCE_STROKE_BATCH) {
            continue;
        }
        const auto stroke = read_value<progpu_native_scene_stroke>(
            stream,
            record.payload_offset);
        PROGPU_REQUIRE(stroke.point_count == 4U);
        PROGPU_REQUIRE(stroke.dash_interval_count == 2U);
        PROGPU_REQUIRE(
            (stroke.flags & PROGPU_NATIVE_POLYLINE_FLAG_CLOSED) != 0U);
        PROGPU_REQUIRE(
            (stroke.flags &
                PROGPU_NATIVE_POLYLINE_FLAG_WPF_JOIN_SEMANTICS) != 0U);
        const std::array expected_points{
            progpu_native_point{3.0F, 4.0F},
            progpu_native_point{3.0F, 4.0F},
            progpu_native_point{3.0F, 8.0F},
            progpu_native_point{3.0F, 8.0F}};
        for (std::size_t point_index = 0U;
             point_index < expected_points.size();
             ++point_index) {
            const auto point = read_value<progpu_native_point>(
                stream,
                record.auxiliary_offset +
                    point_index * sizeof(progpu_native_point));
            PROGPU_REQUIRE(point.x == expected_points[point_index].x);
            PROGPU_REQUIRE(point.y == expected_points[point_index].y);
        }
        found_dashed_degenerate_rectangle = true;
    }
    PROGPU_REQUIRE(found_dashed_degenerate_rectangle);

    std::vector<std::byte> dashed_point_rectangle_batch;
    std::vector<std::byte> dashed_point_rectangle;
    append_command(
        dashed_point_rectangle,
        command::draw_rectangle,
        3.0,
        4.0,
        0.0,
        0.0,
        0U,
        pen);
    append_render_data(
        dashed_point_rectangle_batch,
        content,
        dashed_point_rectangle);
    PROGPU_REQUIRE(
        state.apply(dashed_point_rectangle_batch) == status::success);
    PROGPU_REQUIRE(
        state.build_scene(target, 7002U, 53U, stream, &metrics) ==
        status::success);
    PROGPU_REQUIRE(metrics.rectangle_count == 1U);
    const auto dashed_point_rectangle_header =
        read_value<progpu_native_scene_header>(stream, 0U);
    std::uint32_t dashed_point_rectangle_cap_count = 0U;
    for (std::uint32_t index = 0U;
         index < dashed_point_rectangle_header.resource_count;
         ++index) {
        const auto record = read_value<progpu_native_scene_resource>(
            stream,
            dashed_point_rectangle_header.resource_offset +
                index * sizeof(progpu_native_scene_resource));
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
            if (primitive.kind != PROGPU_NATIVE_GEOMETRY_PATH_CAP) {
                continue;
            }
            PROGPU_REQUIRE(primitive.p0.x == 3.0F);
            PROGPU_REQUIRE(primitive.p0.y == 4.0F);
            PROGPU_REQUIRE(
                (primitive.flags &
                    PROGPU_NATIVE_PRIMITIVE_START_CAP_MASK) ==
                (PROGPU_NATIVE_STROKE_CAP_ROUND <<
                    PROGPU_NATIVE_PRIMITIVE_START_CAP_SHIFT));
            ++dashed_point_rectangle_cap_count;
        }
    }
    PROGPU_REQUIRE(dashed_point_rectangle_cap_count == 2U);
    bool found_dashed_point_rectangle_bounds = false;
    for (std::uint32_t index = 0U;
         index < dashed_point_rectangle_header.command_count;
         ++index) {
        const auto record = read_value<progpu_native_scene_command>(
            stream,
            dashed_point_rectangle_header.command_offset +
                index * sizeof(progpu_native_scene_command));
        if (record.kind != PROGPU_NATIVE_SCENE_COMMAND_DRAW_GEOMETRY) {
            continue;
        }
        PROGPU_REQUIRE(record.bounds_x == 14.0F);
        PROGPU_REQUIRE(record.bounds_y == 26.0F);
        PROGPU_REQUIRE(record.bounds_width == 4.0F);
        PROGPU_REQUIRE(record.bounds_height == 4.0F);
        found_dashed_point_rectangle_bounds = true;
    }
    PROGPU_REQUIRE(found_dashed_point_rectangle_bounds);

    constexpr std::uint32_t point_gap_dash = 14U;
    constexpr std::uint32_t point_gap_pen = 15U;
    std::vector<std::byte> point_gap_pen_batch;
    append_create(point_gap_pen_batch, point_gap_dash, 84U);
    append_create(point_gap_pen_batch, point_gap_pen, 85U);
    const std::array point_gap_intervals{1.0, 1.0};
    append_dash_style(
        point_gap_pen_batch,
        point_gap_dash,
        1.01,
        0U,
        point_gap_intervals);
    append_command(
        point_gap_pen_batch,
        command::pen,
        point_gap_pen,
        2.0,
        10.0,
        brush,
        0U,
        0U,
        0U,
        3U,
        1U,
        point_gap_dash);
    PROGPU_REQUIRE(state.apply(point_gap_pen_batch) == status::success);
    std::vector<std::byte> point_gap_rectangle_batch;
    std::vector<std::byte> point_gap_rectangle;
    append_command(
        point_gap_rectangle,
        command::draw_rectangle,
        3.0,
        4.0,
        0.0,
        0.0,
        0U,
        point_gap_pen);
    append_render_data(
        point_gap_rectangle_batch,
        content,
        point_gap_rectangle);
    PROGPU_REQUIRE(
        state.apply(point_gap_rectangle_batch) == status::success);
    PROGPU_REQUIRE(
        state.build_scene(target, 7002U, 54U, stream, &metrics) ==
        status::success);
    PROGPU_REQUIRE(metrics.rectangle_count == 1U);
    const auto point_gap_rectangle_header =
        read_value<progpu_native_scene_header>(stream, 0U);
    for (std::uint32_t index = 0U;
         index < point_gap_rectangle_header.command_count;
         ++index) {
        const auto record = read_value<progpu_native_scene_command>(
            stream,
            point_gap_rectangle_header.command_offset +
                index * sizeof(progpu_native_scene_command));
        PROGPU_REQUIRE(
            record.kind != PROGPU_NATIVE_SCENE_COMMAND_DRAW_GEOMETRY);
        PROGPU_REQUIRE(
            record.kind != PROGPU_NATIVE_SCENE_COMMAND_DRAW_STROKE_BATCH);
    }

    std::vector<std::byte> dashed_degenerate_ellipse_batch;
    std::vector<std::byte> dashed_degenerate_ellipse;
    append_command(
        dashed_degenerate_ellipse,
        command::draw_ellipse,
        8.0,
        4.0,
        0.0,
        2.0,
        0U,
        pen);
    append_render_data(
        dashed_degenerate_ellipse_batch,
        content,
        dashed_degenerate_ellipse);
    PROGPU_REQUIRE(
        state.apply(dashed_degenerate_ellipse_batch) == status::success);
    PROGPU_REQUIRE(
        state.build_scene(target, 7002U, 52U, stream, &metrics) ==
        status::success);
    PROGPU_REQUIRE(metrics.ellipse_count == 1U);
    const auto dashed_degenerate_ellipse_header =
        read_value<progpu_native_scene_header>(stream, 0U);
    bool found_dashed_degenerate_ellipse = false;
    for (std::uint32_t index = 0U;
         index < dashed_degenerate_ellipse_header.resource_count;
         ++index) {
        const auto record = read_value<progpu_native_scene_resource>(
            stream,
            dashed_degenerate_ellipse_header.resource_offset +
                index * sizeof(progpu_native_scene_resource));
        if (record.kind != PROGPU_NATIVE_SCENE_RESOURCE_STROKE_BATCH) {
            continue;
        }
        const auto stroke = read_value<progpu_native_scene_stroke>(
            stream,
            record.payload_offset);
        PROGPU_REQUIRE(stroke.point_count == 4U);
        PROGPU_REQUIRE(stroke.dash_interval_count == 2U);
        PROGPU_REQUIRE(
            stroke.line_join == PROGPU_NATIVE_STROKE_JOIN_ROUND);
        PROGPU_REQUIRE(
            (stroke.flags & PROGPU_NATIVE_POLYLINE_FLAG_CLOSED) != 0U);
        PROGPU_REQUIRE(
            (stroke.flags &
                PROGPU_NATIVE_POLYLINE_FLAG_WPF_JOIN_SEMANTICS) != 0U);
        const std::array expected_points{
            progpu_native_point{8.0F, 4.0F},
            progpu_native_point{8.0F, 6.0F},
            progpu_native_point{8.0F, 4.0F},
            progpu_native_point{8.0F, 2.0F}};
        for (std::size_t point_index = 0U;
             point_index < expected_points.size();
             ++point_index) {
            const auto point = read_value<progpu_native_point>(
                stream,
                record.auxiliary_offset +
                    point_index * sizeof(progpu_native_point));
            PROGPU_REQUIRE(point.x == expected_points[point_index].x);
            PROGPU_REQUIRE(point.y == expected_points[point_index].y);
        }
        found_dashed_degenerate_ellipse = true;
    }
    PROGPU_REQUIRE(found_dashed_degenerate_ellipse);

    std::vector<std::byte> dashed_degenerate_rounded_batch;
    std::vector<std::byte> dashed_degenerate_rounded;
    append_command(
        dashed_degenerate_rounded,
        command::draw_rounded_rectangle,
        20.0,
        4.0,
        0.0,
        8.0,
        2.0,
        2.0,
        0U,
        pen);
    append_command(
        dashed_degenerate_rounded,
        command::draw_rounded_rectangle,
        24.0,
        4.0,
        8.0,
        0.0,
        2.0,
        1.0,
        0U,
        pen);
    append_command(
        dashed_degenerate_rounded,
        command::draw_rounded_rectangle,
        36.0,
        4.0,
        0.0,
        0.0,
        2.0,
        1.0,
        0U,
        pen);
    append_command(
        dashed_degenerate_rounded,
        command::draw_rounded_rectangle,
        40.0,
        4.0,
        0.0,
        0.0,
        2.0,
        1.0,
        0U,
        point_gap_pen);
    append_command(
        dashed_degenerate_rounded,
        command::draw_rounded_rectangle,
        44.0,
        4.0,
        0.0,
        8.0,
        0.0,
        2.0,
        0U,
        pen);
    append_command(
        dashed_degenerate_rounded,
        command::draw_rounded_rectangle,
        48.0,
        4.0,
        8.0,
        0.0,
        2.0,
        0.0,
        0U,
        pen);
    append_command(
        dashed_degenerate_rounded,
        command::draw_rounded_rectangle,
        60.0,
        4.0,
        0.0,
        0.0,
        0.0,
        2.0,
        0U,
        pen);
    append_command(
        dashed_degenerate_rounded,
        command::draw_rounded_rectangle,
        64.0,
        4.0,
        0.0,
        0.0,
        2.0,
        0.0,
        0U,
        point_gap_pen);
    append_render_data(
        dashed_degenerate_rounded_batch,
        content,
        dashed_degenerate_rounded);
    PROGPU_REQUIRE(
        state.apply(dashed_degenerate_rounded_batch) == status::success);
    PROGPU_REQUIRE(
        state.build_scene(target, 7002U, 55U, stream, &metrics) ==
        status::success);
    PROGPU_REQUIRE(metrics.rounded_rectangle_count == 8U);
    const auto dashed_degenerate_rounded_header =
        read_value<progpu_native_scene_header>(stream, 0U);
    std::uint32_t dashed_degenerate_rounded_cubic_count = 0U;
    std::uint32_t dashed_degenerate_rounded_line_count = 0U;
    std::uint32_t dashed_degenerate_rounded_point_cap_count = 0U;
    std::uint32_t dashed_zero_radius_stroke_count = 0U;
    for (std::uint32_t index = 0U;
         index < dashed_degenerate_rounded_header.resource_count;
         ++index) {
        const auto record = read_value<progpu_native_scene_resource>(
            stream,
            dashed_degenerate_rounded_header.resource_offset +
                index * sizeof(progpu_native_scene_resource));
        if (record.kind == PROGPU_NATIVE_SCENE_RESOURCE_STROKE_BATCH) {
            const std::size_t stroke_count =
                record.payload_size / sizeof(progpu_native_scene_stroke);
            for (std::size_t stroke_index = 0U;
                 stroke_index < stroke_count;
                 ++stroke_index) {
                const auto stroke = read_value<progpu_native_scene_stroke>(
                    stream,
                    record.payload_offset +
                        stroke_index * sizeof(progpu_native_scene_stroke));
                PROGPU_REQUIRE(stroke.point_count == 4U);
                PROGPU_REQUIRE(
                    (stroke.flags &
                        PROGPU_NATIVE_POLYLINE_FLAG_WPF_JOIN_SEMANTICS) !=
                    0U);
                ++dashed_zero_radius_stroke_count;
            }
            continue;
        }
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
            if (primitive.kind ==
                PROGPU_NATIVE_GEOMETRY_CUBIC_BEZIER) {
                ++dashed_degenerate_rounded_cubic_count;
            } else if (primitive.kind == PROGPU_NATIVE_GEOMETRY_LINE) {
                ++dashed_degenerate_rounded_line_count;
            } else if (primitive.kind == PROGPU_NATIVE_GEOMETRY_PATH_CAP &&
                (primitive.p0.x == 36.0F ||
                    primitive.p0.x == 60.0F) &&
                primitive.p0.y == 4.0F) {
                PROGPU_REQUIRE(
                    (primitive.flags &
                        PROGPU_NATIVE_PRIMITIVE_START_CAP_MASK) ==
                    (PROGPU_NATIVE_STROKE_CAP_ROUND <<
                        PROGPU_NATIVE_PRIMITIVE_START_CAP_SHIFT));
                ++dashed_degenerate_rounded_point_cap_count;
            }
            PROGPU_REQUIRE(
                primitive.p0.x != 40.0F || primitive.p0.y != 4.0F);
            PROGPU_REQUIRE(
                primitive.p0.x != 64.0F || primitive.p0.y != 4.0F);
        }
    }
    PROGPU_REQUIRE(dashed_degenerate_rounded_cubic_count > 0U);
    PROGPU_REQUIRE(dashed_degenerate_rounded_line_count > 0U);
    PROGPU_REQUIRE(dashed_degenerate_rounded_point_cap_count == 4U);
    PROGPU_REQUIRE(dashed_zero_radius_stroke_count == 2U);
    bool found_dashed_degenerate_rounded_point_bounds = false;
    for (std::uint32_t index = 0U;
         index < dashed_degenerate_rounded_header.command_count;
         ++index) {
        const auto record = read_value<progpu_native_scene_command>(
            stream,
            dashed_degenerate_rounded_header.command_offset +
                index * sizeof(progpu_native_scene_command));
        if (record.kind != PROGPU_NATIVE_SCENE_COMMAND_DRAW_GEOMETRY ||
            record.bounds_x != 80.0F) {
            continue;
        }
        PROGPU_REQUIRE(record.bounds_y == 26.0F);
        PROGPU_REQUIRE(record.bounds_width == 4.0F);
        PROGPU_REQUIRE(record.bounds_height == 4.0F);
        found_dashed_degenerate_rounded_point_bounds = true;
    }
    PROGPU_REQUIRE(found_dashed_degenerate_rounded_point_bounds);

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
        status::success);
    const auto dashed_ellipse_header =
        read_value<progpu_native_scene_header>(stream, 0U);
    std::uint32_t dashed_ellipse_arc_count = 0U;
    std::uint32_t dashed_ellipse_cap_count = 0U;
    for (std::uint32_t index = 0U;
         index < dashed_ellipse_header.resource_count;
         ++index) {
        const auto record = read_value<progpu_native_scene_resource>(
            stream,
            dashed_ellipse_header.resource_offset +
                index * sizeof(progpu_native_scene_resource));
        if (record.kind != PROGPU_NATIVE_SCENE_RESOURCE_GEOMETRY_BATCH) {
            continue;
        }
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
            dashed_ellipse_arc_count +=
                primitive.kind == PROGPU_NATIVE_GEOMETRY_ARC ? 1U : 0U;
            dashed_ellipse_cap_count +=
                primitive.kind == PROGPU_NATIVE_GEOMETRY_PATH_CAP ? 1U : 0U;
        }
    }
    PROGPU_REQUIRE(dashed_ellipse_arc_count >= 2U);
    PROGPU_REQUIRE(dashed_ellipse_cap_count >= 2U);

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
        status::success);
    const auto dashed_rounded_header =
        read_value<progpu_native_scene_header>(stream, 0U);
    std::uint32_t dashed_rounded_body_count = 0U;
    std::uint32_t dashed_rounded_arc_count = 0U;
    std::uint32_t dashed_rounded_cap_count = 0U;
    for (std::uint32_t index = 0U;
         index < dashed_rounded_header.resource_count;
         ++index) {
        const auto record = read_value<progpu_native_scene_resource>(
            stream,
            dashed_rounded_header.resource_offset +
                index * sizeof(progpu_native_scene_resource));
        if (record.kind != PROGPU_NATIVE_SCENE_RESOURCE_GEOMETRY_BATCH) {
            continue;
        }
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
            dashed_rounded_body_count +=
                primitive.kind == PROGPU_NATIVE_GEOMETRY_LINE ||
                    primitive.kind == PROGPU_NATIVE_GEOMETRY_ARC
                ? 1U
                : 0U;
            dashed_rounded_arc_count +=
                primitive.kind == PROGPU_NATIVE_GEOMETRY_ARC ? 1U : 0U;
            dashed_rounded_cap_count +=
                primitive.kind == PROGPU_NATIVE_GEOMETRY_PATH_CAP ? 1U : 0U;
        }
    }
    PROGPU_REQUIRE(dashed_rounded_body_count >= 4U);
    PROGPU_REQUIRE(dashed_rounded_arc_count >= 1U);
    PROGPU_REQUIRE(dashed_rounded_cap_count >= 2U);

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
        state.apply(animated_geometry) == status::invalid_handle);
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
        status::invalid_handle);
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

bool wpf_arc_lowering_matches_core_piece_policy() {
    using progpu::native::geometry::lower_wpf_arc_to_cubics;
    using progpu::native::geometry::wpf_cubic_arc_piece;
    std::array<wpf_cubic_arc_piece, 4U> pieces{};
    int piece_count = -1;
    PROGPU_REQUIRE(lower_wpf_arc_to_cubics(
        {1.0F, 0.0F},
        {0.0F, 1.0F},
        {1.0F, 1.0F},
        0.0F,
        false,
        true,
        pieces,
        piece_count));
    PROGPU_REQUIRE(piece_count == 1);
    constexpr float quarter_control = 0.55228475F;
    PROGPU_REQUIRE(
        std::abs(pieces[0U].control1.x - 1.0F) < 0.000001F);
    PROGPU_REQUIRE(
        std::abs(pieces[0U].control1.y - quarter_control) < 0.000001F);
    PROGPU_REQUIRE(
        std::abs(pieces[0U].control2.x - quarter_control) < 0.000001F);
    PROGPU_REQUIRE(
        std::abs(pieces[0U].control2.y - 1.0F) < 0.000001F);
    PROGPU_REQUIRE(
        pieces[0U].end.x == 0.0F && pieces[0U].end.y == 1.0F);

    PROGPU_REQUIRE(lower_wpf_arc_to_cubics(
        {1.0F, 0.0F},
        {-1.0F, 0.0F},
        {1.0F, 1.0F},
        0.0F,
        false,
        true,
        pieces,
        piece_count));
    PROGPU_REQUIRE(piece_count == 2);
    PROGPU_REQUIRE(lower_wpf_arc_to_cubics(
        {1.0F, 0.0F},
        {-1.0F, 0.0F},
        {1.0F, 1.0F},
        0.0F,
        true,
        true,
        pieces,
        piece_count));
    PROGPU_REQUIRE(piece_count == 3);

    PROGPU_REQUIRE(lower_wpf_arc_to_cubics(
        {1.0F, 2.0F},
        {3.0F, 4.0F},
        {0.0F, 1.0F},
        0.0F,
        false,
        true,
        pieces,
        piece_count));
    PROGPU_REQUIRE(piece_count == 0);
    PROGPU_REQUIRE(lower_wpf_arc_to_cubics(
        {1.0F, 2.0F},
        {1.0F, 2.0F},
        {1.0F, 1.0F},
        0.0F,
        false,
        true,
        pieces,
        piece_count));
    PROGPU_REQUIRE(piece_count == -1);
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
        PROGPU_REQUIRE(
            std::isfinite(path.min_x) && std::isfinite(path.min_y) &&
            std::isfinite(path.max_x) && std::isfinite(path.max_y));
        PROGPU_REQUIRE(
            path.min_x != 1.0F || path.min_y != 2.0F ||
            path.max_x != 21.0F || path.max_y != 32.0F);
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

    std::vector<std::byte> per_point_guideline_update;
    append_command(
        per_point_guideline_update,
        command::visual_set_guideline_collection,
        visual,
        std::uint16_t{2U},
        std::uint16_t{0U},
        std::uint16_t{2U},
        std::uint16_t{0U},
        2.25F,
        30.75F,
        4.25F,
        24.75F);
    PROGPU_REQUIRE(
        state.apply(per_point_guideline_update) == status::success);
    PROGPU_REQUIRE(
        state.build_scene(target, 7002U, 2U, stream, &metrics) ==
        status::success);
    const auto per_point_header =
        read_value<progpu_native_scene_header>(stream, 0U);
    bool found_lowered_arc = false;
    for (std::uint32_t index = 0U;
         index < per_point_header.resource_count;
         ++index) {
        const auto resource = read_value<progpu_native_scene_resource>(
            stream,
            per_point_header.resource_offset +
                index * sizeof(progpu_native_scene_resource));
        if (resource.kind != PROGPU_NATIVE_SCENE_RESOURCE_PATH_BATCH) {
            continue;
        }
        const auto path = read_value<progpu_native_scene_path_fill>(
            stream,
            resource.payload_offset);
        std::array<
            progpu::native::geometry::wpf_cubic_arc_piece,
            4U> expected{};
        int expected_count = -1;
        PROGPU_REQUIRE(
            progpu::native::geometry::lower_wpf_arc_to_cubics(
                {15.0F, 6.0F},
                {1.0F, 2.0F},
                {8.0F, 6.0F},
                30.0F,
                false,
                true,
                expected,
                expected_count));
        PROGPU_REQUIRE(expected_count > 0);
        PROGPU_REQUIRE(path.segment_count ==
            3U + static_cast<std::uint32_t>(expected_count));
        progpu_native_point expected_start{15.0F, 6.0F};
        for (int piece_index = 0;
             piece_index < expected_count;
             ++piece_index) {
            const auto segment = read_value<progpu_native_path_segment>(
                stream,
                resource.auxiliary_offset +
                    (3U + static_cast<std::size_t>(piece_index)) *
                        sizeof(progpu_native_path_segment));
            const auto& piece = expected[
                static_cast<std::size_t>(piece_index)];
            PROGPU_REQUIRE(
                segment.kind == PROGPU_NATIVE_PATH_SEGMENT_CUBIC);
            PROGPU_REQUIRE(
                segment.p0.x == expected_start.x &&
                segment.p0.y == expected_start.y);
            PROGPU_REQUIRE(
                segment.p1.x == piece.control1.x &&
                segment.p1.y == piece.control1.y);
            PROGPU_REQUIRE(
                segment.p2.x == piece.control2.x &&
                segment.p2.y == piece.control2.y);
            PROGPU_REQUIRE(
                segment.p3.x == piece.end.x &&
                segment.p3.y == piece.end.y);
            expected_start = segment.p3;
        }
        found_lowered_arc = true;
    }
    PROGPU_REQUIRE(found_lowered_arc);
    std::vector<std::byte> clear_per_point_guidelines;
    append_command(
        clear_per_point_guidelines,
        command::visual_set_guideline_collection,
        visual,
        std::uint16_t{0U},
        std::uint16_t{0U},
        std::uint16_t{0U},
        std::uint16_t{0U});
    PROGPU_REQUIRE(
        state.apply(clear_per_point_guidelines) == status::success);

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
    constexpr std::uint32_t grouped_geometry = 9U;
    constexpr std::uint32_t group = 10U;
    constexpr std::uint32_t child_transform = 11U;
    constexpr std::uint32_t grouped_line = 12U;
    constexpr std::uint32_t line_transform = 13U;
    constexpr std::uint32_t grouped_rectangle = 14U;
    constexpr std::uint32_t grouped_ellipse = 15U;
    constexpr std::uint32_t grouped_rounded_rectangle = 16U;
    constexpr std::uint32_t ellipse_transform = 17U;
    constexpr std::uint32_t rounded_transform = 18U;
    constexpr std::uint32_t nested_group = 19U;
    constexpr std::uint32_t nested_transform = 20U;
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
    append_create(batch, grouped_geometry, 73U);
    append_create(batch, group, 71U);
    append_create(batch, child_transform, 66U);
    append_create(batch, grouped_line, 68U);
    append_create(batch, line_transform, 66U);
    append_create(batch, grouped_rectangle, 69U);
    append_create(batch, grouped_ellipse, 70U);
    append_create(batch, grouped_rounded_rectangle, 69U);
    append_create(batch, ellipse_transform, 66U);
    append_create(batch, rounded_transform, 66U);
    append_create(batch, nested_group, 71U);
    append_create(batch, nested_transform, 66U);
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
    append_command(
        batch,
        command::matrix_transform,
        child_transform,
        1.0,
        0.0,
        0.0,
        1.0,
        30.0,
        0.0,
        0U);
    append_command(
        batch,
        command::matrix_transform,
        line_transform,
        1.0,
        0.0,
        0.0,
        1.0,
        60.0,
        10.0,
        0U);
    append_command(
        batch,
        command::line_geometry,
        grouped_line,
        0.0,
        20.0,
        10.0,
        25.0,
        line_transform,
        0U,
        0U);
    append_command(
        batch,
        command::matrix_transform,
        ellipse_transform,
        1.0,
        0.0,
        0.0,
        1.0,
        90.0,
        5.0,
        0U);
    append_command(
        batch,
        command::matrix_transform,
        nested_transform,
        1.0,
        0.0,
        0.0,
        1.0,
        200.0,
        15.0,
        0U);
    append_command(
        batch,
        command::matrix_transform,
        rounded_transform,
        1.0,
        0.0,
        0.0,
        1.0,
        120.0,
        5.0,
        0U);
    append_command(
        batch,
        command::rectangle_geometry,
        grouped_rectangle,
        0.0,
        0.0,
        0.0,
        30.0,
        12.0,
        8.0,
        line_transform,
        0U,
        0U,
        0U);
    append_command(
        batch,
        command::ellipse_geometry,
        grouped_ellipse,
        6.0,
        4.0,
        20.0,
        20.0,
        ellipse_transform,
        0U,
        0U,
        0U);
    append_command(
        batch,
        command::rectangle_geometry,
        grouped_rounded_rectangle,
        3.0,
        2.0,
        0.0,
        0.0,
        16.0,
        10.0,
        rounded_transform,
        0U,
        0U,
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
        status::success);

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
        3U,
        1U,
        dash);
    PROGPU_REQUIRE(state.apply(dashed_arc_update) == status::success);
    PROGPU_REQUIRE(
        state.build_scene(target, 7002U, 9U, stream, &metrics) ==
        status::success);
    const auto dashed_arc_header =
        read_value<progpu_native_scene_header>(stream, 0U);
    std::uint32_t dashed_arc_count = 0U;
    std::uint32_t dashed_arc_cap_count = 0U;
    for (std::uint32_t resource_index = 0U;
         resource_index < dashed_arc_header.resource_count;
         ++resource_index) {
        const auto record = read_value<progpu_native_scene_resource>(
            stream,
            dashed_arc_header.resource_offset +
                resource_index * sizeof(progpu_native_scene_resource));
        if (record.kind != PROGPU_NATIVE_SCENE_RESOURCE_GEOMETRY_BATCH) {
            continue;
        }
        std::uint32_t primitive_offset = 0U;
        for (;
             primitive_offset + primitive_stride <= record.payload_size;
             primitive_offset += primitive_stride) {
            const auto primitive =
                read_value<progpu_native_geometry_primitive>(
                    stream,
                    record.payload_offset + primitive_offset);
            if (primitive.kind == PROGPU_NATIVE_GEOMETRY_ARC) {
                PROGPU_REQUIRE(primitive.p3.y > 0.0F);
                ++dashed_arc_count;
            } else if (
                primitive.kind == PROGPU_NATIVE_GEOMETRY_PATH_CAP) {
                const std::uint32_t cap =
                    (primitive.flags &
                        PROGPU_NATIVE_PRIMITIVE_START_CAP_MASK) >>
                    PROGPU_NATIVE_PRIMITIVE_START_CAP_SHIFT;
                PROGPU_REQUIRE(cap == PROGPU_NATIVE_STROKE_CAP_TRIANGLE);
                ++dashed_arc_cap_count;
            }
        }
        PROGPU_REQUIRE(primitive_offset == record.payload_size);
    }
    PROGPU_REQUIRE(dashed_arc_count >= 2U);
    PROGPU_REQUIRE(dashed_arc_cap_count >= 2U);

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

    const std::array short_dash_intervals{1.0, 1.0};
    std::vector<std::byte> dashed_curve_update;
    append_dash_style(
        dashed_curve_update,
        dash,
        0.0,
        0U,
        short_dash_intervals);
    append_command(
        dashed_curve_update,
        command::pen,
        pen,
        2.0,
        4.0,
        brush,
        0U,
        0U,
        0U,
        2U,
        1U,
        dash);
    append_path_geometry(
        dashed_curve_update,
        geometry,
        transform,
        0U,
        make_curve_path_figures());
    PROGPU_REQUIRE(state.apply(dashed_curve_update) == status::success);
    PROGPU_REQUIRE(
        state.build_scene(target, 7002U, 101U, stream, &metrics) ==
        status::success);
    const auto dashed_curve_header =
        read_value<progpu_native_scene_header>(stream, 0U);
    std::uint32_t dashed_line_count = 0U;
    std::uint32_t dashed_quadratic_count = 0U;
    std::uint32_t dashed_cubic_count = 0U;
    std::uint32_t dashed_curve_cap_count = 0U;
    for (std::uint32_t resource_index = 0U;
         resource_index < dashed_curve_header.resource_count;
         ++resource_index) {
        const auto record = read_value<progpu_native_scene_resource>(
            stream,
            dashed_curve_header.resource_offset +
                resource_index * sizeof(progpu_native_scene_resource));
        if (record.kind != PROGPU_NATIVE_SCENE_RESOURCE_GEOMETRY_BATCH) {
            continue;
        }
        std::uint32_t primitive_offset = 0U;
        for (;
             primitive_offset + primitive_stride <= record.payload_size;
             primitive_offset += primitive_stride) {
            const auto primitive =
                read_value<progpu_native_geometry_primitive>(
                    stream,
                    record.payload_offset + primitive_offset);
            dashed_line_count +=
                primitive.kind == PROGPU_NATIVE_GEOMETRY_LINE ? 1U : 0U;
            dashed_quadratic_count +=
                primitive.kind == PROGPU_NATIVE_GEOMETRY_QUADRATIC_BEZIER
                ? 1U
                : 0U;
            dashed_cubic_count +=
                primitive.kind == PROGPU_NATIVE_GEOMETRY_CUBIC_BEZIER
                ? 1U
                : 0U;
            dashed_curve_cap_count +=
                primitive.kind == PROGPU_NATIVE_GEOMETRY_PATH_CAP ? 1U : 0U;
        }
        PROGPU_REQUIRE(primitive_offset == record.payload_size);
    }
    PROGPU_REQUIRE(dashed_line_count >= 2U);
    PROGPU_REQUIRE(dashed_quadratic_count >= 1U);
    PROGPU_REQUIRE(dashed_cubic_count >= 1U);
    PROGPU_REQUIRE(dashed_curve_cap_count >= 2U);

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
    append_command(
        zero_path_update,
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

    std::vector<std::byte> group_stroke_update;
    append_command(
        group_stroke_update,
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
        0U);
    const auto grouped_figures = make_curve_path_figures();
    auto unfilled_grouped_figures = grouped_figures;
    const std::uint32_t unfilled_closed_curve_figure = 0x06U;
    std::memcpy(
        unfilled_grouped_figures.data() + 52U,
        &unfilled_closed_curve_figure,
        sizeof(unfilled_closed_curve_figure));
    append_path_geometry(
        group_stroke_update,
        geometry,
        transform,
        0U,
        grouped_figures);
    append_path_geometry(
        group_stroke_update,
        grouped_geometry,
        child_transform,
        0U,
        unfilled_grouped_figures);
    const std::array nested_group_children{grouped_line};
    append_geometry_group(
        group_stroke_update,
        nested_group,
        nested_transform,
        1U,
        nested_group_children);
    const std::array group_children{
        geometry,
        grouped_geometry,
        grouped_line,
        grouped_rectangle,
        grouped_ellipse,
        grouped_rounded_rectangle,
        nested_group};
    append_geometry_group(
        group_stroke_update,
        group,
        0U,
        0U,
        group_children);
    std::vector<std::byte> group_stroke_commands;
    append_command(
        group_stroke_commands,
        command::draw_geometry,
        brush,
        pen,
        group,
        0U);
    append_render_data(
        group_stroke_update,
        content,
        group_stroke_commands);
    PROGPU_REQUIRE(state.apply(group_stroke_update) == status::success);
    PROGPU_REQUIRE(
        state.build_scene(target, 7002U, 14U, stream, &metrics) ==
        status::success);
    PROGPU_REQUIRE(metrics.brush_count == 1U);
    const auto group_stroke_header =
        read_value<progpu_native_scene_header>(stream, 0U);
    std::uint32_t group_fill_count = 0U;
    std::uint32_t first_child_body_count = 0U;
    std::uint32_t second_child_body_count = 0U;
    std::uint32_t line_child_body_count = 0U;
    std::uint32_t rectangle_child_count = 0U;
    std::uint32_t ellipse_child_count = 0U;
    std::uint32_t rounded_child_body_count = 0U;
    std::uint32_t nested_line_body_count = 0U;
    for (std::uint32_t resource_index = 0U;
         resource_index < group_stroke_header.resource_count;
         ++resource_index) {
        const auto record = read_value<progpu_native_scene_resource>(
            stream,
            group_stroke_header.resource_offset +
                resource_index * sizeof(progpu_native_scene_resource));
        if (record.kind == PROGPU_NATIVE_SCENE_RESOURCE_PATH_BATCH) {
            const auto path = read_value<progpu_native_scene_path_fill>(
                stream,
                record.payload_offset);
            if (path.segment_count == 20U) {
                PROGPU_REQUIRE(path.transform.m11 == 1.0F);
                PROGPU_REQUIRE(path.transform.m22 == 1.0F);
                ++group_fill_count;
            }
            continue;
        }
        if (record.kind == PROGPU_NATIVE_SCENE_RESOURCE_STROKE_BATCH) {
            const auto stroke = read_value<progpu_native_scene_stroke>(
                stream,
                record.payload_offset);
            if (stroke.transform.m31 == 60.0F &&
                stroke.transform.m32 == 10.0F &&
                stroke.point_count == 4U) {
                PROGPU_REQUIRE(
                    (stroke.flags & PROGPU_NATIVE_POLYLINE_FLAG_CLOSED) !=
                    0U);
                PROGPU_REQUIRE(stroke.dash_interval_count == 0U);
                ++rectangle_child_count;
            }
            continue;
        }
        if (record.kind != PROGPU_NATIVE_SCENE_RESOURCE_GEOMETRY_BATCH) {
            continue;
        }
        std::uint32_t primitive_offset = 0U;
        for (;
             primitive_offset + primitive_stride <= record.payload_size;
             primitive_offset += primitive_stride) {
            const auto primitive =
                read_value<progpu_native_geometry_primitive>(
                    stream,
                    record.payload_offset + primitive_offset);
            if (primitive.kind == PROGPU_NATIVE_GEOMETRY_PATH_JOIN ||
                primitive.kind == PROGPU_NATIVE_GEOMETRY_PATH_CAP) {
                continue;
            }
            if (primitive.transform.m11 == 1.5F &&
                primitive.transform.m22 == 1.5F &&
                primitive.transform.m31 == 2.0F &&
                primitive.transform.m32 == 3.0F) {
                ++first_child_body_count;
            } else if (
                primitive.transform.m11 == 1.0F &&
                primitive.transform.m22 == 1.0F &&
                primitive.transform.m31 == 30.0F &&
                primitive.transform.m32 == 0.0F) {
                ++second_child_body_count;
            } else if (
                primitive.kind == PROGPU_NATIVE_GEOMETRY_LINE &&
                primitive.transform.m11 == 1.0F &&
                primitive.transform.m22 == 1.0F &&
                primitive.transform.m31 == 60.0F &&
                primitive.transform.m32 == 10.0F) {
                PROGPU_REQUIRE(
                    primitive.p0.x == 0.0F && primitive.p0.y == 20.0F &&
                    primitive.p1.x == 10.0F && primitive.p1.y == 25.0F);
                ++line_child_body_count;
            } else if (
                primitive.kind == PROGPU_NATIVE_GEOMETRY_ARC &&
                primitive.transform.m31 == 90.0F &&
                primitive.transform.m32 == 5.0F) {
                PROGPU_REQUIRE(primitive.p1.x == 6.0F);
                PROGPU_REQUIRE(primitive.p2.y == 4.0F);
                ++ellipse_child_count;
            } else if (
                primitive.transform.m31 == 120.0F &&
                primitive.transform.m32 == 5.0F) {
                PROGPU_REQUIRE(
                    primitive.kind == PROGPU_NATIVE_GEOMETRY_LINE ||
                    primitive.kind == PROGPU_NATIVE_GEOMETRY_ARC);
                ++rounded_child_body_count;
            } else if (
                primitive.kind == PROGPU_NATIVE_GEOMETRY_LINE &&
                primitive.transform.m31 == 260.0F &&
                primitive.transform.m32 == 25.0F) {
                ++nested_line_body_count;
            }
        }
        PROGPU_REQUIRE(primitive_offset == record.payload_size);
    }
    PROGPU_REQUIRE(group_fill_count == 1U);
    PROGPU_REQUIRE(first_child_body_count == 4U);
    PROGPU_REQUIRE(second_child_body_count == 4U);
    PROGPU_REQUIRE(line_child_body_count == 1U);
    PROGPU_REQUIRE(rectangle_child_count == 1U);
    PROGPU_REQUIRE(ellipse_child_count == 1U);
    PROGPU_REQUIRE(rounded_child_body_count == 8U);
    PROGPU_REQUIRE(nested_line_body_count == 1U);

    std::vector<std::byte> dashed_group_update;
    append_dash_style(
        dashed_group_update,
        dash,
        0.0,
        0U,
        short_dash_intervals);
    append_command(
        dashed_group_update,
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
    PROGPU_REQUIRE(state.apply(dashed_group_update) == status::success);
    PROGPU_REQUIRE(
        state.build_scene(target, 7002U, 15U, stream, &metrics) ==
        status::success);
    const auto dashed_group_header =
        read_value<progpu_native_scene_header>(stream, 0U);
    std::uint32_t dashed_group_cap_count = 0U;
    bool found_first_dashed_child = false;
    bool found_second_dashed_child = false;
    bool found_dashed_line_child = false;
    bool found_dashed_rectangle_child = false;
    bool found_dashed_ellipse_child = false;
    bool found_dashed_rounded_child = false;
    bool found_dashed_nested_line = false;
    for (std::uint32_t resource_index = 0U;
         resource_index < dashed_group_header.resource_count;
         ++resource_index) {
        const auto record = read_value<progpu_native_scene_resource>(
            stream,
            dashed_group_header.resource_offset +
                resource_index * sizeof(progpu_native_scene_resource));
        if (record.kind == PROGPU_NATIVE_SCENE_RESOURCE_STROKE_BATCH) {
            const auto stroke = read_value<progpu_native_scene_stroke>(
                stream,
                record.payload_offset);
            if (stroke.transform.m31 == 60.0F &&
                stroke.transform.m32 == 10.0F) {
                PROGPU_REQUIRE(stroke.dash_interval_count == 2U);
                PROGPU_REQUIRE(stroke.dash_offset == 0.0);
                if (stroke.point_count == 2U) {
                    PROGPU_REQUIRE(stroke.start_cap == 1U);
                    PROGPU_REQUIRE(stroke.end_cap == 2U);
                    found_dashed_line_child = true;
                } else if (stroke.point_count == 4U) {
                    PROGPU_REQUIRE(
                        (stroke.flags &
                            PROGPU_NATIVE_POLYLINE_FLAG_CLOSED) != 0U);
                    found_dashed_rectangle_child = true;
                }
            } else if (
                stroke.transform.m31 == 260.0F &&
                stroke.transform.m32 == 25.0F) {
                PROGPU_REQUIRE(stroke.point_count == 2U);
                PROGPU_REQUIRE(stroke.dash_interval_count == 2U);
                found_dashed_nested_line = true;
            }
            continue;
        }
        if (record.kind != PROGPU_NATIVE_SCENE_RESOURCE_GEOMETRY_BATCH) {
            continue;
        }
        std::uint32_t primitive_offset = 0U;
        for (;
             primitive_offset + primitive_stride <= record.payload_size;
             primitive_offset += primitive_stride) {
            const auto primitive =
                read_value<progpu_native_geometry_primitive>(
                    stream,
                    record.payload_offset + primitive_offset);
            if (primitive.kind == PROGPU_NATIVE_GEOMETRY_PATH_CAP) {
                const std::uint32_t cap =
                    (primitive.flags &
                        PROGPU_NATIVE_PRIMITIVE_START_CAP_MASK) >>
                    PROGPU_NATIVE_PRIMITIVE_START_CAP_SHIFT;
                PROGPU_REQUIRE(cap == PROGPU_NATIVE_STROKE_CAP_TRIANGLE);
                ++dashed_group_cap_count;
            }
            found_first_dashed_child = found_first_dashed_child ||
                (primitive.transform.m11 == 1.5F &&
                 primitive.transform.m22 == 1.5F &&
                 primitive.transform.m31 == 2.0F &&
                 primitive.transform.m32 == 3.0F);
            found_second_dashed_child = found_second_dashed_child ||
                (primitive.transform.m11 == 1.0F &&
                 primitive.transform.m22 == 1.0F &&
                 primitive.transform.m31 == 30.0F &&
                 primitive.transform.m32 == 0.0F);
            found_dashed_ellipse_child = found_dashed_ellipse_child ||
                (primitive.transform.m31 == 90.0F &&
                 primitive.transform.m32 == 5.0F);
            found_dashed_rounded_child = found_dashed_rounded_child ||
                (primitive.transform.m31 == 120.0F &&
                 primitive.transform.m32 == 5.0F);
        }
        PROGPU_REQUIRE(primitive_offset == record.payload_size);
    }
    PROGPU_REQUIRE(dashed_group_cap_count >= 4U);
    PROGPU_REQUIRE(found_first_dashed_child);
    PROGPU_REQUIRE(found_second_dashed_child);
    PROGPU_REQUIRE(found_dashed_line_child);
    PROGPU_REQUIRE(found_dashed_rectangle_child);
    PROGPU_REQUIRE(found_dashed_ellipse_child);
    PROGPU_REQUIRE(found_dashed_rounded_child);
    PROGPU_REQUIRE(found_dashed_nested_line);
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
    constexpr std::uint32_t gradient_mask = 12U;

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
    append_create(batch, gradient_mask, 77U);
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
    const std::array gradient_mask_stops{
        mil_gradient_stop{0.0, {1.0F, 1.0F, 1.0F, 0.0F}},
        mil_gradient_stop{1.0, {1.0F, 1.0F, 1.0F, 1.0F}}};
    append_linear_gradient_brush(
        batch,
        gradient_mask,
        1.0,
        0.0,
        0.0,
        1.0,
        0.0,
        0U,
        0U,
        0U,
        1U,
        1U,
        0U,
        0U,
        0U,
        gradient_mask_stops);
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
        8U,
        clip,
        opacity,
        opacity_mask,
        transform,
        0U,
        1U,
        0U,
        1U,
        drawing,
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
    PROGPU_REQUIRE(metrics.rectangle_count == 2U);
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
            if (scene_state.opacity == 1.0F &&
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
    const auto group_layers = get_scene_layers(stream);
    PROGPU_REQUIRE(group_layers.size() == 1U);
    PROGPU_REQUIRE(
        (group_layers[0].flags &
            PROGPU_NATIVE_SCENE_LAYER_FORCE_ISOLATION) != 0U);
    PROGPU_REQUIRE(group_layers[0].opacity == 0.125F);

    std::vector<std::byte> opacity_update;
    append_command(opacity_update, command::double_resource, opacity, 0.25);
    PROGPU_REQUIRE(state.apply(opacity_update) == status::success);
    PROGPU_REQUIRE(
        state.build_scene(target, 7004U, 2U, stream, &metrics) ==
        status::success);
    const auto updated_layers = get_scene_layers(stream);
    PROGPU_REQUIRE(updated_layers.size() == 1U);
    PROGPU_REQUIRE(updated_layers[0].opacity == 0.0625F);

    std::vector<std::byte> spatial_mask_update;
    append_command(
        spatial_mask_update,
        command::drawing_group,
        group,
        1.0,
        8U,
        clip,
        opacity,
        gradient_mask,
        transform,
        0U,
        1U,
        0U,
        1U,
        drawing,
        drawing);
    PROGPU_REQUIRE(state.apply(spatial_mask_update) == status::success);
    PROGPU_REQUIRE(
        state.build_scene(target, 7004U, 3U, stream, &metrics) ==
        status::unsupported_command);
    PROGPU_REQUIRE(
        state.set_drawing_group_bounds(group, 2.0, 3.0, 20.0, 10.0) ==
        status::success);
    PROGPU_REQUIRE(
        state.set_drawing_group_bounds(target, 2.0, 3.0, 20.0, 10.0) ==
        status::invalid_handle);
    PROGPU_REQUIRE(state.apply(spatial_mask_update) == status::success);
    PROGPU_REQUIRE(
        state.build_scene(target, 7004U, 4U, stream, &metrics) ==
        status::success);
    const auto spatial_layers = get_scene_layers(stream);
    PROGPU_REQUIRE(spatial_layers.size() == 1U);
    PROGPU_REQUIRE(spatial_layers[0].opacity == 0.25F);
    PROGPU_REQUIRE(spatial_layers[0].bounds.x == 12.0F);
    PROGPU_REQUIRE(spatial_layers[0].bounds.y == 23.0F);
    PROGPU_REQUIRE(spatial_layers[0].bounds.width == 20.0F);
    PROGPU_REQUIRE(spatial_layers[0].bounds.height == 10.0F);
    progpu_native_scene_layer_brush_mask spatial_mask{};
    std::vector<progpu_native_scene_gradient_stop> spatial_stops;
    PROGPU_REQUIRE(try_get_brush_mask_resource(
        stream,
        spatial_layers[0].mask_resource_index,
        spatial_mask,
        spatial_stops));
    PROGPU_REQUIRE(spatial_mask.bounds.x == 2.0F);
    PROGPU_REQUIRE(spatial_mask.bounds.y == 3.0F);
    PROGPU_REQUIRE(spatial_mask.bounds.width == 20.0F);
    PROGPU_REQUIRE(spatial_mask.bounds.height == 10.0F);
    PROGPU_REQUIRE(spatial_mask.transform.m31 == 10.0F);
    PROGPU_REQUIRE(spatial_mask.transform.m32 == 20.0F);
    PROGPU_REQUIRE(spatial_stops.size() == 2U);

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
        state.build_scene(target, 7004U, 5U, stream, &metrics) ==
        status::success);
    PROGPU_REQUIRE(metrics.rectangle_count == 2U);
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
        status::success);
    const auto updated_header = read_value<progpu_native_scene_header>(
        stream, 0U);
    bool found_per_point_guidelines = false;
    for (std::uint32_t index = 0U;
         index < updated_header.resource_count;
         ++index) {
        const auto resource = read_value<progpu_native_scene_resource>(
            stream,
            updated_header.resource_offset +
                index * sizeof(progpu_native_scene_resource));
        if (resource.kind != PROGPU_NATIVE_SCENE_RESOURCE_GUIDELINE_SET) {
            continue;
        }
        const auto value = read_value<progpu_native_scene_guideline_set>(
            stream, resource.payload_offset);
        found_per_point_guidelines |= value.flags ==
                PROGPU_NATIVE_SCENE_GUIDELINE_PER_POINT &&
            value.guideline_x_count == 2U &&
            value.guideline_y_count == 0U;
    }
    PROGPU_REQUIRE(found_per_point_guidelines);
    return true;
}

bool render_data_static_guideline_scope_uses_active_transform() {
    constexpr std::uint32_t visual = 1U;
    constexpr std::uint32_t content = 2U;
    constexpr std::uint32_t target = 3U;
    constexpr std::uint32_t brush = 4U;
    constexpr std::uint32_t geometry = 5U;
    constexpr std::uint32_t drawing = 6U;
    constexpr std::uint32_t guidelines = 7U;
    constexpr std::uint32_t transform = 8U;

    std::vector<std::byte> batch;
    append_create(batch, visual, 39U);
    append_create(batch, content, 43U);
    append_create(batch, target, 47U);
    append_create(batch, brush, 75U);
    append_create(batch, geometry, 69U);
    append_create(batch, drawing, 87U);
    append_create(batch, guidelines, 92U);
    append_create(batch, transform, 66U);
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
    std::vector<std::byte> nested;
    append_command(nested, command::push_transform, transform, 0U);
    append_command(nested, command::push_guideline_set, guidelines, 0U);
    append_command(nested, command::draw_drawing, drawing, 0U);
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
    progpu::native::mil::scene_metrics metrics{};
    PROGPU_REQUIRE(
        state.build_scene(target, 7008U, 1U, stream, &metrics) ==
        status::success);
    const auto header = read_value<progpu_native_scene_header>(stream, 0U);
    bool found_guidelines = false;
    bool found_guided_state = false;
    for (std::uint32_t index = 0U; index < header.resource_count; ++index) {
        const auto resource = read_value<progpu_native_scene_resource>(
            stream,
            header.resource_offset +
                index * sizeof(progpu_native_scene_resource));
        if (resource.kind == PROGPU_NATIVE_SCENE_RESOURCE_GUIDELINE_SET) {
            const auto value = read_value<progpu_native_scene_guideline_set>(
                stream, resource.payload_offset);
            PROGPU_REQUIRE(value.flags == 0U);
            PROGPU_REQUIRE(value.guideline_x_count == 1U);
            PROGPU_REQUIRE(value.guideline_y_count == 1U);
            PROGPU_REQUIRE(read_value<double>(
                stream,
                resource.payload_offset + sizeof(value)) == 12.25);
            PROGPU_REQUIRE(read_value<double>(
                stream,
                resource.payload_offset + sizeof(value) + sizeof(double)) ==
                23.5);
            found_guidelines = true;
        } else if (resource.kind == PROGPU_NATIVE_SCENE_RESOURCE_STATE) {
            const auto value = read_value<progpu_native_scene_state>(
                stream, resource.payload_offset);
            found_guided_state |=
                (value.flags & PROGPU_NATIVE_SCENE_STATE_GUIDELINE_SET) !=
                    0U &&
                value.transform.m31 == 10.0F &&
                value.transform.m32 == 20.0F;
        }
    }
    PROGPU_REQUIRE(found_guidelines);
    PROGPU_REQUIRE(found_guided_state);

    std::vector<std::byte> dynamic_update;
    append_command(
        dynamic_update,
        command::guideline_set,
        guidelines,
        0U,
        16U,
        1U,
        3.5,
        0.0);
    PROGPU_REQUIRE(state.apply(dynamic_update) == status::success);
    PROGPU_REQUIRE(
        state.build_scene(target, 7008U, 2U, stream, &metrics) ==
        status::unsupported_command);
    return true;
}

bool dynamic_guidelines_follow_wpf_phase_state() {
    constexpr std::uint32_t visual = 1U;
    constexpr std::uint32_t content = 2U;
    constexpr std::uint32_t target = 3U;
    constexpr std::uint32_t brush = 4U;
    constexpr std::uint32_t guidelines = 5U;
    constexpr std::uint32_t transform = 6U;
    std::vector<std::byte> batch;
    append_create(batch, visual, 39U);
    append_create(batch, content, 43U);
    append_create(batch, target, 47U);
    append_create(batch, brush, 75U);
    append_create(batch, guidelines, 92U);
    append_create(batch, transform, 66U);
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
        1.0,
        0.0,
        0.0,
        1.0,
        0.0,
        0.0,
        0U);
    append_command(
        batch,
        command::guideline_set,
        guidelines,
        16U,
        0U,
        1U,
        0.25,
        0.5);
    std::vector<std::byte> nested;
    append_command(nested, command::push_transform, transform, 0U);
    append_command(nested, command::push_guideline_set, guidelines, 0U);
    append_command(
        nested,
        command::draw_rectangle,
        0.0,
        0.0,
        8.0,
        8.0,
        brush,
        0U);
    append_command(nested, command::pop);
    append_command(nested, command::pop);
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
    std::vector<std::byte> legacy;
    PROGPU_REQUIRE(
        state.build_scene(target, 9'100U, 1U, legacy) ==
        status::unsupported_command);

    const auto build = [&state](
        std::uint64_t serial,
        std::uint64_t milliseconds,
        scene_build_request_flags flags,
        std::vector<std::byte>& copy,
        scene_build_result& result,
        double dpi_scale_x = 1.0,
        double dpi_scale_y = 1.0) {
        scene_build_request request{};
        request.flags = flags;
        request.target_handle = target;
        request.scene_id = 9'100U;
        request.generation = serial;
        request.dpi_scale_x = dpi_scale_x;
        request.dpi_scale_y = dpi_scale_y;
        request.monotonic_time_nanoseconds = milliseconds * 1'000'000U;
        request.request_serial = serial;
        std::span<const std::byte> stream;
        const status build_status = state.build_scene(
            request, stream, nullptr, &result);
        copy.assign(stream.begin(), stream.end());
        return build_status;
    };
    const auto read_offset = [](const std::vector<std::byte>& stream) {
        const auto header = read_value<progpu_native_scene_header>(stream, 0U);
        for (std::uint32_t index = 0U;
             index < header.resource_count;
             ++index) {
            const auto resource = read_value<progpu_native_scene_resource>(
                stream,
                header.resource_offset +
                    index * sizeof(progpu_native_scene_resource));
            if (resource.kind !=
                PROGPU_NATIVE_SCENE_RESOURCE_GUIDELINE_SET) {
                continue;
            }
            const auto set = read_value<progpu_native_scene_guideline_set>(
                stream, resource.payload_offset);
            PROGPU_REQUIRE((set.flags &
                PROGPU_NATIVE_SCENE_GUIDELINE_EXPLICIT_OFFSETS) != 0U);
            const std::size_t count =
                static_cast<std::size_t>(set.guideline_x_count) +
                set.guideline_y_count;
            return read_value<double>(
                stream,
                resource.payload_offset + sizeof(set) +
                    count * sizeof(double));
        }
        PROGPU_REQUIRE(false);
        return 0.0;
    };
    const auto has_guideline_resource = [](const std::vector<std::byte>& stream) {
        const auto header = read_value<progpu_native_scene_header>(stream, 0U);
        for (std::uint32_t index = 0U;
             index < header.resource_count;
             ++index) {
            const auto resource = read_value<progpu_native_scene_resource>(
                stream,
                header.resource_offset +
                    index * sizeof(progpu_native_scene_resource));
            if (resource.kind ==
                PROGPU_NATIVE_SCENE_RESOURCE_GUIDELINE_SET) {
                return true;
            }
        }
        return false;
    };

    scene_build_result result{};
    std::vector<std::byte> start;
    PROGPU_REQUIRE(build(
        1U, 0U, scene_build_request_flags::none, start, result) ==
        status::success);
    PROGPU_REQUIRE(std::abs(read_offset(start) - 0.25) < 0.0001);
    PROGPU_REQUIRE(result.flags == scene_build_result_flags::none);
    std::span<const std::byte> repeated;
    scene_build_request repeated_request{
        scene_build_request_flags::none,
        target,
        9'100U,
        1U,
        1.0,
        1.0,
        0U,
        1U};
    PROGPU_REQUIRE(state.build_scene(
        repeated_request, repeated, nullptr, &result) == status::success);
    PROGPU_REQUIRE(std::ranges::equal(repeated, start));

    std::vector<std::byte> moved_transform;
    append_command(
        moved_transform,
        command::matrix_transform,
        transform,
        1.0,
        0.0,
        0.0,
        1.0,
        0.1,
        0.0,
        0U);
    PROGPU_REQUIRE(state.apply(moved_transform) == status::success);
    std::vector<std::byte> animation;
    PROGPU_REQUIRE(build(
        2U, 100U, scene_build_request_flags::none, animation, result) ==
        status::success);
    PROGPU_REQUIRE(std::abs(read_offset(animation) - 0.5) < 0.0001);
    PROGPU_REQUIRE(result.flags ==
        scene_build_result_flags::needs_more_cycles);
    PROGPU_REQUIRE(result.next_due_time_nanoseconds == 150'000'000U);

    std::vector<std::byte> landing_start;
    PROGPU_REQUIRE(build(
        3U, 350U, scene_build_request_flags::none, landing_start, result) ==
        status::success);
    PROGPU_REQUIRE(std::abs(read_offset(landing_start) - 0.5) < 0.0001);
    std::vector<std::byte> landing_step;
    PROGPU_REQUIRE(build(
        4U, 400U, scene_build_request_flags::none, landing_step, result) ==
        status::success);
    PROGPU_REQUIRE(std::abs(read_offset(landing_step) - 0.45) < 0.0001);

    std::vector<std::byte> visual_brush;
    PROGPU_REQUIRE(build(
        5U,
        450U,
        scene_build_request_flags::visual_brush,
        visual_brush,
        result) == status::success);
    PROGPU_REQUIRE(std::abs(read_offset(visual_brush) - 0.15) < 0.0001);
    PROGPU_REQUIRE(result.flags == scene_build_result_flags::none);

    std::vector<std::byte> invalid_nested;
    append_command(invalid_nested, command::push_transform, transform, 0U);
    append_command(
        invalid_nested,
        command::push_guideline_set,
        guidelines,
        0U);
    append_command(
        invalid_nested,
        command::draw_rectangle,
        0.0,
        0.0,
        8.0,
        8.0,
        brush,
        0U);
    append_command(
        invalid_nested,
        command::draw_video,
        0.0,
        0.0,
        1.0,
        1.0,
        0U,
        0U);
    std::vector<std::byte> invalid_content;
    append_render_data(invalid_content, content, invalid_nested);
    PROGPU_REQUIRE(state.apply(invalid_content) == status::success);
    std::vector<std::byte> rejected;
    PROGPU_REQUIRE(build(
        6U, 500U, scene_build_request_flags::none, rejected, result) ==
        status::invalid_handle);

    std::vector<std::byte> restored_content;
    append_render_data(restored_content, content, nested);
    PROGPU_REQUIRE(state.apply(restored_content) == status::success);
    std::vector<std::byte> after_rejection;
    PROGPU_REQUIRE(build(
        7U,
        500U,
        scene_build_request_flags::none,
        after_rejection,
        result) == status::success);
    PROGPU_REQUIRE(std::abs(read_offset(after_rejection) - 0.4) < 0.0001);
    PROGPU_REQUIRE(result.flags ==
        scene_build_result_flags::needs_more_cycles);

    std::vector<std::byte> sheared_transform;
    append_command(
        sheared_transform,
        command::matrix_transform,
        transform,
        1.0,
        0.2,
        0.0,
        1.0,
        0.1,
        0.0,
        0U);
    PROGPU_REQUIRE(state.apply(sheared_transform) == status::success);
    std::vector<std::byte> flight;
    PROGPU_REQUIRE(build(
        8U, 550U, scene_build_request_flags::none, flight, result) ==
        status::success);
    PROGPU_REQUIRE(!has_guideline_resource(flight));
    PROGPU_REQUIRE(result.flags == scene_build_result_flags::none);

    PROGPU_REQUIRE(state.apply(moved_transform) == status::success);
    std::vector<std::byte> returned_from_flight;
    PROGPU_REQUIRE(build(
        9U,
        600U,
        scene_build_request_flags::none,
        returned_from_flight,
        result) == status::success);
    PROGPU_REQUIRE(
        std::abs(read_offset(returned_from_flight) - 0.15) < 0.0001);
    PROGPU_REQUIRE(result.flags == scene_build_result_flags::none);

    std::vector<std::byte> big_jump_transform;
    append_command(
        big_jump_transform,
        command::matrix_transform,
        transform,
        1.0,
        0.0,
        0.0,
        1.0,
        4.1,
        0.0,
        0U);
    PROGPU_REQUIRE(state.apply(big_jump_transform) == status::success);
    std::vector<std::byte> big_jump;
    PROGPU_REQUIRE(build(
        10U, 650U, scene_build_request_flags::none, big_jump, result) ==
        status::success);
    PROGPU_REQUIRE(std::abs(read_offset(big_jump) - 0.15) < 0.0001);
    PROGPU_REQUIRE(result.flags == scene_build_result_flags::none);

    std::vector<std::byte> reset_guidelines;
    append_command(
        reset_guidelines,
        command::guideline_set,
        guidelines,
        16U,
        0U,
        1U,
        0.25,
        0.5);
    append_command(
        reset_guidelines,
        command::matrix_transform,
        transform,
        1.0,
        0.0,
        0.0,
        1.0,
        0.0,
        0.0,
        0U);
    PROGPU_REQUIRE(state.apply(reset_guidelines) == status::success);
    std::vector<std::byte> nonuniform_dpi;
    PROGPU_REQUIRE(build(
        11U,
        700U,
        scene_build_request_flags::none,
        nonuniform_dpi,
        result,
        1.25,
        1.5) == status::success);
    PROGPU_REQUIRE(
        std::abs(read_offset(nonuniform_dpi) - 0.0625) < 0.0001);
    PROGPU_REQUIRE(result.flags == scene_build_result_flags::none);
    return true;
}

bool compact_dynamic_guidelines_retain_and_reset_phase_state() {
    constexpr std::uint32_t visual = 1U;
    constexpr std::uint32_t content = 2U;
    constexpr std::uint32_t target = 3U;
    constexpr std::uint32_t brush = 4U;
    constexpr std::uint32_t transform = 5U;
    std::vector<std::byte> batch;
    append_create(batch, visual, 39U);
    append_create(batch, content, 43U);
    append_create(batch, target, 47U);
    append_create(batch, brush, 75U);
    append_create(batch, transform, 66U);
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
        1.0,
        0.0,
        0.0,
        1.0,
        0.0,
        0.0,
        0U);
    const auto make_content = [brush, transform](bool pair) {
        std::vector<std::byte> nested;
        append_command(nested, command::push_transform, transform, 0U);
        if (pair) {
            append_command(
                nested, command::push_guideline_y2, 0.25, 0.5);
        } else {
            append_command(nested, command::push_guideline_y1, 0.25);
        }
        append_command(
            nested,
            command::draw_rectangle,
            0.0,
            0.0,
            8.0,
            8.0,
            brush,
            0U);
        append_command(nested, command::pop);
        append_command(nested, command::pop);
        std::vector<std::byte> update;
        append_render_data(update, content, nested);
        return update;
    };
    const std::vector<std::byte> initial_content = make_content(false);
    batch.insert(batch.end(), initial_content.begin(), initial_content.end());
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
    std::vector<std::byte> legacy;
    PROGPU_REQUIRE(
        state.build_scene(target, 9'200U, 1U, legacy) ==
        status::unsupported_command);
    const auto build = [&state](
        std::uint64_t serial,
        std::uint64_t milliseconds,
        std::vector<std::byte>& copy,
        scene_build_result& result) {
        const scene_build_request request{
            scene_build_request_flags::none,
            target,
            9'200U,
            serial,
            1.25,
            2.0,
            milliseconds * 1'000'000U,
            serial};
        std::span<const std::byte> stream;
        const status build_status = state.build_scene(
            request, stream, nullptr, &result);
        copy.assign(stream.begin(), stream.end());
        return build_status;
    };

    scene_build_result result{};
    explicit_guideline_snapshot guideline{};
    std::vector<std::byte> initial;
    PROGPU_REQUIRE(build(1U, 0U, initial, result) == status::success);
    PROGPU_REQUIRE(try_get_single_explicit_guideline(initial, guideline));
    PROGPU_REQUIRE(guideline.count_x == 0U);
    PROGPU_REQUIRE(guideline.count_y == 1U);
    PROGPU_REQUIRE(std::abs(guideline.coordinate - 0.25) < 0.0001);
    PROGPU_REQUIRE(std::abs(guideline.offset - 0.5) < 0.0001);
    PROGPU_REQUIRE(result.flags == scene_build_result_flags::none);

    std::vector<std::byte> moved_transform;
    append_command(
        moved_transform,
        command::matrix_transform,
        transform,
        1.0,
        0.0,
        0.0,
        1.0,
        0.0,
        0.1,
        0U);
    PROGPU_REQUIRE(state.apply(moved_transform) == status::success);
    std::vector<std::byte> animated;
    PROGPU_REQUIRE(build(2U, 100U, animated, result) == status::success);
    PROGPU_REQUIRE(try_get_single_explicit_guideline(animated, guideline));
    PROGPU_REQUIRE(std::abs(guideline.coordinate - 0.35) < 0.0001);
    PROGPU_REQUIRE(std::abs(guideline.offset) < 0.0001);
    PROGPU_REQUIRE(result.flags ==
        scene_build_result_flags::needs_more_cycles);

    const std::vector<std::byte> replacement_content = make_content(true);
    PROGPU_REQUIRE(state.apply(replacement_content) == status::success);
    std::vector<std::byte> replacement;
    PROGPU_REQUIRE(build(3U, 150U, replacement, result) == status::success);
    PROGPU_REQUIRE(try_get_single_explicit_guideline(
        replacement, guideline));
    PROGPU_REQUIRE(std::abs(guideline.coordinate - 0.85) < 0.0001);
    PROGPU_REQUIRE(std::abs(guideline.offset - 0.3) < 0.0001);
    PROGPU_REQUIRE(result.flags == scene_build_result_flags::none);
    return true;
}

bool render_data_opacity_mask_scope_uses_gpu_brush_layer() {
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
    append_create(batch, opacity_mask, 77U);
    append_command(batch, command::visual_create, visual);
    append_command(batch, command::visual_set_offset, visual, 5.0, 6.0);
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
    const std::array mask_stops{
        mil_gradient_stop{0.0, {1.0F, 1.0F, 1.0F, 0.0F}},
        mil_gradient_stop{1.0, {1.0F, 1.0F, 1.0F, 1.0F}}};
    append_linear_gradient_brush(
        batch,
        opacity_mask,
        1.0,
        0.0,
        0.0,
        1.0,
        0.0,
        0U,
        0U,
        0U,
        1U,
        1U,
        0U,
        0U,
        0U,
        mask_stops);
    std::vector<std::byte> nested;
    append_command(
        nested,
        command::push_opacity_mask,
        2.0F,
        3.0F,
        12.0F,
        23.0F,
        opacity_mask,
        0U);
    append_command(
        nested,
        command::draw_rectangle,
        2.0,
        3.0,
        10.0,
        20.0,
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
        64U,
        64U,
        0U);
    append_command(batch, command::target_set_root, target, visual);

    channel state;
    PROGPU_REQUIRE(state.apply(batch) == status::success);
    std::vector<std::byte> stream;
    PROGPU_REQUIRE(
        state.build_scene(target, 7009U, 1U, stream) == status::success);
    const auto layers = get_scene_layers(stream);
    PROGPU_REQUIRE(layers.size() == 1U);
    PROGPU_REQUIRE(
        (layers[0].flags & PROGPU_NATIVE_SCENE_LAYER_FORCE_ISOLATION) != 0U);
    PROGPU_REQUIRE(
        (layers[0].flags & PROGPU_NATIVE_SCENE_LAYER_BOUNDS) != 0U);
    PROGPU_REQUIRE(layers[0].bounds.x == 7.0F);
    PROGPU_REQUIRE(layers[0].bounds.y == 9.0F);
    PROGPU_REQUIRE(layers[0].bounds.width == 10.0F);
    PROGPU_REQUIRE(layers[0].bounds.height == 20.0F);
    PROGPU_REQUIRE(layers[0].opacity == 1.0F);
    PROGPU_REQUIRE(
        layers[0].mask_resource_index != PROGPU_NATIVE_SCENE_NO_INDEX);
    progpu_native_scene_layer_brush_mask mask{};
    std::vector<progpu_native_scene_gradient_stop> stops;
    PROGPU_REQUIRE(try_get_brush_mask_resource(
        stream, layers[0].mask_resource_index, mask, stops));
    PROGPU_REQUIRE(mask.kind == PROGPU_NATIVE_SCENE_LAYER_MASK_BRUSH);
    PROGPU_REQUIRE(mask.bounds.x == 2.0F);
    PROGPU_REQUIRE(mask.bounds.y == 3.0F);
    PROGPU_REQUIRE(mask.bounds.width == 10.0F);
    PROGPU_REQUIRE(mask.bounds.height == 20.0F);
    PROGPU_REQUIRE(mask.transform.m31 == 5.0F);
    PROGPU_REQUIRE(mask.transform.m32 == 6.0F);
    PROGPU_REQUIRE(stops.size() == 2U);

    const std::array changed_stops{
        mil_gradient_stop{0.0, {1.0F, 1.0F, 1.0F, 0.25F}},
        mil_gradient_stop{1.0, {1.0F, 1.0F, 1.0F, 0.75F}}};
    std::vector<std::byte> mask_update;
    append_linear_gradient_brush(
        mask_update,
        opacity_mask,
        1.0,
        0.0,
        0.0,
        1.0,
        0.0,
        0U,
        0U,
        0U,
        1U,
        1U,
        0U,
        0U,
        0U,
        changed_stops);
    PROGPU_REQUIRE(state.apply(mask_update) == status::success);
    PROGPU_REQUIRE(
        state.build_scene(target, 7009U, 2U, stream) == status::success);
    const auto updated_layers = get_scene_layers(stream);
    PROGPU_REQUIRE(updated_layers.size() == 1U);
    PROGPU_REQUIRE(try_get_brush_mask_resource(
        stream,
        updated_layers[0].mask_resource_index,
        mask,
        stops));
    PROGPU_REQUIRE(stops.size() == 2U);
    PROGPU_REQUIRE(stops[0].color.a == 0.25F);
    PROGPU_REQUIRE(stops[1].color.a == 0.75F);
    return true;
}

bool retained_image_drawing_uses_pointer_free_bitmap_sideband() {
    constexpr std::uint32_t visual = 1U;
    constexpr std::uint32_t content = 2U;
    constexpr std::uint32_t target = 3U;
    constexpr std::uint32_t bitmap = 4U;
    constexpr std::uint32_t drawing = 5U;
    constexpr std::uint32_t group = 6U;
    constexpr std::uint32_t rectangle_animation = 7U;

    std::vector<std::byte> batch;
    append_create(batch, visual, 39U);
    append_create(batch, content, 43U);
    append_create(batch, target, 47U);
    append_create(batch, bitmap, 95U);
    append_create(batch, drawing, 89U);
    append_create(batch, group, 91U);
    append_create(batch, rectangle_animation, 52U);
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
        command::rect_resource,
        rectangle_animation,
        3.0,
        5.0,
        20.0,
        10.0);
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
    append_command(
        nested,
        command::draw_image,
        3.0,
        5.0,
        20.0,
        10.0,
        bitmap,
        0U);
    append_command(
        nested,
        command::draw_image_animate,
        1.0,
        2.0,
        3.0,
        4.0,
        bitmap,
        rectangle_animation);
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

    std::vector<std::byte> animated_destination;
    append_command(
        animated_destination,
        command::rect_resource,
        rectangle_animation,
        7.0,
        9.0,
        12.0,
        14.0);
    append_command(
        animated_destination,
        command::image_drawing,
        drawing,
        3.0,
        5.0,
        20.0,
        10.0,
        bitmap,
        rectangle_animation);
    PROGPU_REQUIRE(state.apply(animated_destination) == status::success);
    PROGPU_REQUIRE(
        state.build_scene(target, 7005U, 21U, stream, &metrics) ==
        status::success);
    const auto animated_header = read_value<progpu_native_scene_header>(
        stream, 0U);
    bool found_animated_destination = false;
    for (std::uint32_t index = 0U;
         index < animated_header.command_count;
         ++index) {
        const auto record = read_value<progpu_native_scene_command>(
            stream,
            animated_header.command_offset +
                index * sizeof(progpu_native_scene_command));
        if (record.kind == PROGPU_NATIVE_SCENE_COMMAND_DRAW_IMAGE) {
            const auto image = read_value<progpu_native_scene_image_draw>(
                stream, record.payload_offset);
            if (image.destination_rect.x == 7.0F &&
                image.destination_rect.y == 9.0F &&
                image.destination_rect.width == 12.0F &&
                image.destination_rect.height == 14.0F) {
                found_animated_destination = true;
            }
        }
    }
    PROGPU_REQUIRE(found_animated_destination);

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
    const auto fant_header = read_value<progpu_native_scene_header>(
        stream, 0U);
    bool found_fant = false;
    for (std::uint32_t index = 0U;
         index < fant_header.command_count;
         ++index) {
        const auto record = read_value<progpu_native_scene_command>(
            stream,
            fant_header.command_offset +
                index * sizeof(progpu_native_scene_command));
        if (record.kind == PROGPU_NATIVE_SCENE_COMMAND_DRAW_IMAGE) {
            const auto image = read_value<progpu_native_scene_image_draw>(
                stream, record.payload_offset);
            found_fant |= image.sampling ==
                PROGPU_NATIVE_IMAGE_SAMPLING_FANT;
        }
    }
    PROGPU_REQUIRE(found_fant);

    PROGPU_REQUIRE(
        state.set_bitmap_source_external_image(bitmap, 0U, 2U) ==
        status::invalid_argument);
    PROGPU_REQUIRE(
        state.set_bitmap_source_external_image(target, 2U, 2U) ==
        status::invalid_handle);
    PROGPU_REQUIRE(
        state.set_bitmap_source_external_image(bitmap, 32U, 16U) ==
        status::success);
    PROGPU_REQUIRE(state.resource_generation(bitmap) == 3U);
    PROGPU_REQUIRE(
        state.build_scene(target, 7005U, 4U, stream, &metrics) ==
        status::success);
    const auto external_header = read_value<progpu_native_scene_header>(
        stream, 0U);
    PROGPU_REQUIRE(external_header.resource_count >= 1U);
    const auto external_resource = read_value<progpu_native_scene_resource>(
        stream, external_header.resource_offset);
    PROGPU_REQUIRE(
        external_resource.kind == PROGPU_NATIVE_SCENE_RESOURCE_IMAGE);
    PROGPU_REQUIRE(
        (external_resource.flags & PROGPU_NATIVE_SCENE_EXTERNAL_IMAGE) != 0U);
    PROGPU_REQUIRE(external_resource.resource_id == 1U);
    PROGPU_REQUIRE(external_resource.generation == 4U);
    PROGPU_REQUIRE(external_resource.payload_size == 0U);
    bool found_external = false;
    for (std::uint32_t index = 0U;
         index < external_header.command_count;
         ++index) {
        const auto record = read_value<progpu_native_scene_command>(
            stream,
            external_header.command_offset +
                index * sizeof(progpu_native_scene_command));
        if (record.kind != PROGPU_NATIVE_SCENE_COMMAND_DRAW_IMAGE ||
            record.resource_index != 0U) {
            continue;
        }
        const auto image = read_value<progpu_native_scene_image_draw>(
            stream, record.payload_offset);
        PROGPU_REQUIRE(image.image_width == 32U);
        PROGPU_REQUIRE(image.image_height == 16U);
        PROGPU_REQUIRE(image.row_bytes == 128U);
        found_external = true;
    }
    PROGPU_REQUIRE(found_external);

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

bool canonical_bitmap_packets_preserve_pointer_free_sideband() {
    constexpr std::uint32_t bitmap = 1U;
    std::vector<std::byte> create;
    append_create(create, bitmap, 95U);
    channel state;
    PROGPU_REQUIRE(state.apply(create) == status::success);
    PROGPU_REQUIRE(state.resource_generation(bitmap) == 1U);

    std::vector<std::byte> source;
    append_command(
        source,
        command::bitmap_source,
        bitmap,
        std::uint64_t{0U});
    batch_metrics metrics{};
    PROGPU_REQUIRE(state.apply(source, &metrics) == status::success);
    PROGPU_REQUIRE(metrics.command_count == 1U);
    PROGPU_REQUIRE(metrics.supported_command_count == 1U);
    PROGPU_REQUIRE(metrics.updated_resource_count == 1U);
    PROGPU_REQUIRE(state.resource_generation(bitmap) == 2U);

    constexpr std::array<std::byte, 64U> pixels{};
    PROGPU_REQUIRE(
        state.set_bitmap_source_rgba8(bitmap, 4U, 4U, 16U, pixels) ==
        status::success);
    PROGPU_REQUIRE(state.resource_generation(bitmap) == 3U);

    std::vector<std::byte> dirty;
    append_command(
        dirty,
        command::bitmap_invalidate,
        bitmap,
        1U,
        std::int32_t{1},
        std::int32_t{1},
        std::int32_t{4},
        std::int32_t{3});
    PROGPU_REQUIRE(state.apply(dirty) == status::success);
    PROGPU_REQUIRE(state.resource_generation(bitmap) == 4U);

    // WPF leaves DirtyRect uninitialized when UseDirtyRect is false. The
    // portable decoder must ignore those bytes instead of validating them.
    std::vector<std::byte> full;
    append_command(
        full,
        command::bitmap_invalidate,
        bitmap,
        0U,
        std::numeric_limits<std::int32_t>::min(),
        std::numeric_limits<std::int32_t>::max(),
        std::int32_t{-1},
        std::int32_t{0});
    PROGPU_REQUIRE(state.apply(full) == status::success);
    PROGPU_REQUIRE(state.resource_generation(bitmap) == 5U);

    std::vector<std::byte> invalid_pointer;
    append_command(
        invalid_pointer,
        command::bitmap_source,
        bitmap,
        std::uint64_t{0x1234U});
    PROGPU_REQUIRE(
        state.apply(invalid_pointer) == status::invalid_argument);
    PROGPU_REQUIRE(state.resource_generation(bitmap) == 5U);

    std::vector<std::byte> invalid_flag;
    append_command(
        invalid_flag,
        command::bitmap_invalidate,
        bitmap,
        2U,
        std::int32_t{0},
        std::int32_t{0},
        std::int32_t{1},
        std::int32_t{1});
    PROGPU_REQUIRE(state.apply(invalid_flag) == status::malformed_batch);
    PROGPU_REQUIRE(state.resource_generation(bitmap) == 5U);

    std::vector<std::byte> invalid_dirty_rect;
    append_command(
        invalid_dirty_rect,
        command::bitmap_invalidate,
        bitmap,
        1U,
        std::int32_t{0},
        std::int32_t{0},
        std::int32_t{5},
        std::int32_t{4});
    PROGPU_REQUIRE(
        state.apply(invalid_dirty_rect) == status::malformed_batch);
    PROGPU_REQUIRE(state.resource_generation(bitmap) == 5U);
    return true;
}

bool writeable_bitmap_uses_pointer_free_front_buffer_sideband() {
    constexpr std::uint32_t visual = 1U;
    constexpr std::uint32_t content = 2U;
    constexpr std::uint32_t target = 3U;
    constexpr std::uint32_t bitmap = 4U;

    std::vector<std::byte> batch;
    append_create(batch, visual, 39U);
    append_create(batch, content, 43U);
    append_create(batch, target, 47U);
    append_create(batch, bitmap, 96U);
    append_command(
        batch,
        command::double_buffered_bitmap,
        bitmap,
        std::uint64_t{0U},
        0U);
    append_command(batch, command::visual_create, visual);
    append_command(batch, command::visual_set_content, visual, content);
    std::vector<std::byte> nested;
    append_command(
        nested,
        command::draw_image,
        2.0,
        3.0,
        20.0,
        10.0,
        bitmap,
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
    PROGPU_REQUIRE(state.resource_generation(bitmap) == 2U);
    std::vector<std::byte> stream;
    PROGPU_REQUIRE(
        state.build_scene(target, 7017U, 1U, stream) ==
        status::invalid_handle);

    constexpr std::array<std::byte, 16U> pixels{
        std::byte{255}, std::byte{0}, std::byte{0}, std::byte{255},
        std::byte{0}, std::byte{255}, std::byte{0}, std::byte{255},
        std::byte{0}, std::byte{0}, std::byte{255}, std::byte{255},
        std::byte{255}, std::byte{255}, std::byte{255}, std::byte{255}};
    PROGPU_REQUIRE(
        state.set_double_buffered_bitmap_rgba8(
            bitmap, 2U, 2U, 8U, pixels) == status::success);
    PROGPU_REQUIRE(state.resource_generation(bitmap) == 3U);
    PROGPU_REQUIRE(
        state.build_scene(target, 7017U, 2U, stream) == status::success);
    const auto copied_header = read_value<progpu_native_scene_header>(
        stream, 0U);
    PROGPU_REQUIRE(copied_header.resource_count >= 1U);
    bool found_copied = false;
    for (std::uint32_t index = 0U;
         index < copied_header.resource_count;
         ++index) {
        const auto resource = read_value<progpu_native_scene_resource>(
            stream,
            copied_header.resource_offset +
                index * sizeof(progpu_native_scene_resource));
        if (resource.kind != PROGPU_NATIVE_SCENE_RESOURCE_IMAGE) {
            continue;
        }
        PROGPU_REQUIRE(resource.payload_size == pixels.size());
        PROGPU_REQUIRE(
            (resource.flags & PROGPU_NATIVE_SCENE_EXTERNAL_IMAGE) == 0U);
        found_copied = true;
    }
    PROGPU_REQUIRE(found_copied);

    std::vector<std::byte> copy_forward;
    append_command(
        copy_forward,
        command::double_buffered_bitmap_copy_forward,
        bitmap,
        std::uint64_t{0U});
    PROGPU_REQUIRE(state.apply(copy_forward) == status::success);
    PROGPU_REQUIRE(state.resource_generation(bitmap) == 4U);

    PROGPU_REQUIRE(
        state.set_double_buffered_bitmap_external_image(
            bitmap, 32U, 16U) == status::success);
    PROGPU_REQUIRE(state.resource_generation(bitmap) == 5U);
    PROGPU_REQUIRE(
        state.build_scene(target, 7017U, 3U, stream) == status::success);
    const auto external_header = read_value<progpu_native_scene_header>(
        stream, 0U);
    bool found_external = false;
    for (std::uint32_t index = 0U;
         index < external_header.resource_count;
         ++index) {
        const auto resource = read_value<progpu_native_scene_resource>(
            stream,
            external_header.resource_offset +
                index * sizeof(progpu_native_scene_resource));
        if (resource.kind != PROGPU_NATIVE_SCENE_RESOURCE_IMAGE) {
            continue;
        }
        PROGPU_REQUIRE(
            (resource.flags & PROGPU_NATIVE_SCENE_EXTERNAL_IMAGE) != 0U);
        PROGPU_REQUIRE(resource.payload_size == 0U);
        found_external = true;
    }
    PROGPU_REQUIRE(found_external);

    std::vector<std::byte> raw_pointer;
    append_command(
        raw_pointer,
        command::double_buffered_bitmap,
        bitmap,
        std::uint64_t{1U},
        0U);
    PROGPU_REQUIRE(state.apply(raw_pointer) == status::invalid_argument);
    std::vector<std::byte> raw_event;
    append_command(
        raw_event,
        command::double_buffered_bitmap_copy_forward,
        bitmap,
        std::uint64_t{1U});
    PROGPU_REQUIRE(state.apply(raw_event) == status::invalid_argument);
    std::vector<std::byte> invalid_flag;
    append_command(
        invalid_flag,
        command::double_buffered_bitmap,
        bitmap,
        std::uint64_t{0U},
        2U);
    PROGPU_REQUIRE(state.apply(invalid_flag) == status::malformed_batch);
    PROGPU_REQUIRE(state.resource_generation(bitmap) == 5U);
    PROGPU_REQUIRE(
        state.set_bitmap_source_external_image(bitmap, 1U, 1U) ==
        status::invalid_handle);
    PROGPU_REQUIRE(
        state.set_double_buffered_bitmap_external_image(
            target, 1U, 1U) == status::invalid_handle);
    return true;
}

bool canonical_d3d_image_uses_synchronized_external_image_sideband() {
    constexpr std::uint32_t visual = 1U;
    constexpr std::uint32_t content = 2U;
    constexpr std::uint32_t target = 3U;
    constexpr std::uint32_t image = 4U;

    std::vector<std::byte> batch;
    append_create(batch, visual, 39U);
    append_create(batch, content, 43U);
    append_create(batch, target, 47U);
    append_create(batch, image, 97U);
    append_command(batch, command::d3d_image, image,
        std::uint64_t{0U}, std::uint64_t{0U});
    append_command(batch, command::d3d_image_present, image,
        std::uint64_t{0U});
    append_command(batch, command::visual_create, visual);
    append_command(batch, command::visual_set_content, visual, content);
    std::vector<std::byte> nested;
    append_command(
        nested,
        command::draw_image,
        3.0,
        5.0,
        20.0,
        10.0,
        image,
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
    PROGPU_REQUIRE(state.resource_generation(image) == 3U);
    std::vector<std::byte> stream;
    PROGPU_REQUIRE(
        state.build_scene(target, 7014U, 1U, stream) ==
        status::invalid_handle);
    PROGPU_REQUIRE(
        state.set_d3d_image_external_image(image, 0U, 32U, 1U) ==
        status::invalid_argument);
    PROGPU_REQUIRE(
        state.set_d3d_image_external_image(target, 64U, 32U, 1U) ==
        status::invalid_handle);
    PROGPU_REQUIRE(
        state.set_d3d_image_external_image(image, 64U, 32U, 0U) ==
        status::invalid_argument);
    PROGPU_REQUIRE(
        state.set_d3d_image_external_image(image, 64U, 32U, 7U) ==
        status::success);
    PROGPU_REQUIRE(state.resource_generation(image) == 4U);
    PROGPU_REQUIRE(
        state.build_scene(target, 7014U, 2U, stream) == status::success);

    const auto header = read_value<progpu_native_scene_header>(stream, 0U);
    const auto resource = read_value<progpu_native_scene_resource>(
        stream, header.resource_offset);
    PROGPU_REQUIRE(resource.kind == PROGPU_NATIVE_SCENE_RESOURCE_IMAGE);
    PROGPU_REQUIRE(
        (resource.flags & PROGPU_NATIVE_SCENE_EXTERNAL_IMAGE) != 0U);
    PROGPU_REQUIRE(resource.resource_id == 1U);
    PROGPU_REQUIRE(resource.payload_size == 0U);
    bool found_image = false;
    for (std::uint32_t index = 0U; index < header.command_count; ++index) {
        const auto record = read_value<progpu_native_scene_command>(
            stream,
            header.command_offset +
                index * sizeof(progpu_native_scene_command));
        if (record.kind != PROGPU_NATIVE_SCENE_COMMAND_DRAW_IMAGE) {
            continue;
        }
        const auto draw = read_value<progpu_native_scene_image_draw>(
            stream, record.payload_offset);
        PROGPU_REQUIRE(draw.image_width == 64U);
        PROGPU_REQUIRE(draw.image_height == 32U);
        PROGPU_REQUIRE(draw.row_bytes == 256U);
        PROGPU_REQUIRE(record.resource_index == 0U);
        found_image = true;
    }
    PROGPU_REQUIRE(found_image);

    std::vector<std::byte> present;
    append_command(present, command::d3d_image_present, image,
        std::uint64_t{0U});
    PROGPU_REQUIRE(state.apply(present) == status::success);
    PROGPU_REQUIRE(state.resource_generation(image) == 5U);

    std::vector<std::byte> raw_pointer;
    append_command(raw_pointer, command::d3d_image, image,
        std::uint64_t{1U}, std::uint64_t{0U});
    PROGPU_REQUIRE(state.apply(raw_pointer) == status::invalid_argument);
    std::vector<std::byte> raw_event;
    append_command(raw_event, command::d3d_image_present, image,
        std::uint64_t{1U});
    PROGPU_REQUIRE(state.apply(raw_event) == status::invalid_argument);
    return true;
}

bool render_data_video_uses_live_external_image_sideband() {
    constexpr std::uint32_t visual = 1U;
    constexpr std::uint32_t content = 2U;
    constexpr std::uint32_t target = 3U;
    constexpr std::uint32_t player = 4U;
    constexpr std::uint32_t rectangle_animation = 5U;

    std::vector<std::byte> batch;
    append_create(batch, visual, 39U);
    append_create(batch, content, 43U);
    append_create(batch, target, 47U);
    append_create(batch, player, 1U);
    append_create(batch, rectangle_animation, 52U);
    append_command(batch, command::visual_create, visual);
    append_command(batch, command::visual_set_content, visual, content);
    append_command(
        batch,
        command::rect_resource,
        rectangle_animation,
        7.0,
        9.0,
        12.0,
        14.0);
    std::vector<std::byte> nested;
    append_command(
        nested,
        command::draw_video,
        3.0,
        5.0,
        20.0,
        10.0,
        player,
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
        state.build_scene(target, 7015U, 1U, stream) ==
        status::invalid_handle);
    PROGPU_REQUIRE(
        state.set_media_player_external_image(player, 0U, 32U) ==
        status::invalid_argument);
    PROGPU_REQUIRE(
        state.set_media_player_external_image(target, 64U, 32U) ==
        status::invalid_handle);
    PROGPU_REQUIRE(
        state.set_media_player_external_image(player, 64U, 32U) ==
        status::success);
    PROGPU_REQUIRE(state.resource_generation(player) == 2U);
    PROGPU_REQUIRE(
        state.build_scene(target, 7015U, 2U, stream) == status::success);

    const auto header = read_value<progpu_native_scene_header>(stream, 0U);
    PROGPU_REQUIRE(header.resource_count >= 1U);
    const auto resource = read_value<progpu_native_scene_resource>(
        stream, header.resource_offset);
    PROGPU_REQUIRE(resource.kind == PROGPU_NATIVE_SCENE_RESOURCE_IMAGE);
    PROGPU_REQUIRE(
        (resource.flags & PROGPU_NATIVE_SCENE_EXTERNAL_IMAGE) != 0U);
    PROGPU_REQUIRE(resource.resource_id == 1U);
    PROGPU_REQUIRE(resource.generation == 2U);
    PROGPU_REQUIRE(resource.payload_size == 0U);

    std::uint32_t image_count = 0U;
    bool found_static = false;
    for (std::uint32_t index = 0U; index < header.command_count; ++index) {
        const auto command_record = read_value<progpu_native_scene_command>(
            stream,
            header.command_offset +
                index * sizeof(progpu_native_scene_command));
        if (command_record.kind != PROGPU_NATIVE_SCENE_COMMAND_DRAW_IMAGE) {
            continue;
        }
        ++image_count;
        PROGPU_REQUIRE(command_record.resource_index == 0U);
        const auto image = read_value<progpu_native_scene_image_draw>(
            stream, command_record.payload_offset);
        PROGPU_REQUIRE(image.image_width == 64U);
        PROGPU_REQUIRE(image.image_height == 32U);
        PROGPU_REQUIRE(image.row_bytes == 256U);
        PROGPU_REQUIRE(image.source_rect.width == 64.0F);
        PROGPU_REQUIRE(image.source_rect.height == 32.0F);
        found_static |= image.destination_rect.x == 3.0F &&
            image.destination_rect.y == 5.0F &&
            image.destination_rect.width == 20.0F &&
            image.destination_rect.height == 10.0F;
    }
    PROGPU_REQUIRE(image_count == 1U);
    PROGPU_REQUIRE(found_static);

    std::vector<std::byte> animated_nested;
    append_command(
        animated_nested,
        command::draw_video_animate,
        1.0,
        2.0,
        3.0,
        4.0,
        player,
        rectangle_animation);
    std::vector<std::byte> animated_update;
    append_render_data(animated_update, content, animated_nested);
    PROGPU_REQUIRE(state.apply(animated_update) == status::success);
    PROGPU_REQUIRE(
        state.build_scene(target, 7015U, 3U, stream) == status::success);
    const auto animated_header = read_value<progpu_native_scene_header>(
        stream, 0U);
    bool found_animated = false;
    for (std::uint32_t index = 0U;
         index < animated_header.command_count;
         ++index) {
        const auto command_record = read_value<progpu_native_scene_command>(
            stream,
            animated_header.command_offset +
                index * sizeof(progpu_native_scene_command));
        if (command_record.kind != PROGPU_NATIVE_SCENE_COMMAND_DRAW_IMAGE) {
            continue;
        }
        const auto image = read_value<progpu_native_scene_image_draw>(
            stream, command_record.payload_offset);
        found_animated |= image.destination_rect.x == 7.0F &&
            image.destination_rect.y == 9.0F &&
            image.destination_rect.width == 12.0F &&
            image.destination_rect.height == 14.0F;
    }
    PROGPU_REQUIRE(found_animated);
    return true;
}

bool retained_video_drawing_uses_pointer_free_media_packet() {
    constexpr std::uint32_t visual = 1U;
    constexpr std::uint32_t content = 2U;
    constexpr std::uint32_t target = 3U;
    constexpr std::uint32_t player = 4U;
    constexpr std::uint32_t rectangle_animation = 5U;
    constexpr std::uint32_t video_drawing = 6U;

    std::vector<std::byte> batch;
    append_create(batch, visual, 39U);
    append_create(batch, content, 43U);
    append_create(batch, target, 47U);
    append_create(batch, player, 1U);
    append_create(batch, rectangle_animation, 52U);
    append_create(batch, video_drawing, 90U);
    append_command(
        batch,
        command::media_player,
        player,
        std::uint64_t{0U},
        1U);
    append_command(batch, command::visual_create, visual);
    append_command(batch, command::visual_set_content, visual, content);
    append_command(
        batch,
        command::rect_resource,
        rectangle_animation,
        7.0,
        9.0,
        12.0,
        14.0);
    append_command(
        batch,
        command::video_drawing,
        video_drawing,
        3.0,
        5.0,
        20.0,
        10.0,
        player,
        0U);
    std::vector<std::byte> nested;
    append_command(nested, command::draw_drawing, video_drawing, 0U);
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
    PROGPU_REQUIRE(state.resource_generation(player) == 2U);
    std::vector<std::byte> stream;
    PROGPU_REQUIRE(
        state.build_scene(target, 7016U, 1U, stream) ==
        status::invalid_handle);
    PROGPU_REQUIRE(
        state.set_media_player_external_image(player, 64U, 32U) ==
        status::success);
    PROGPU_REQUIRE(state.resource_generation(player) == 3U);
    PROGPU_REQUIRE(
        state.build_scene(target, 7016U, 2U, stream) == status::success);

    const auto header = read_value<progpu_native_scene_header>(stream, 0U);
    std::uint32_t image_count = 0U;
    bool found_static = false;
    for (std::uint32_t index = 0U; index < header.command_count; ++index) {
        const auto record = read_value<progpu_native_scene_command>(
            stream,
            header.command_offset +
                index * sizeof(progpu_native_scene_command));
        if (record.kind != PROGPU_NATIVE_SCENE_COMMAND_DRAW_IMAGE) {
            continue;
        }
        ++image_count;
        const auto image = read_value<progpu_native_scene_image_draw>(
            stream, record.payload_offset);
        found_static |= image.destination_rect.x == 3.0F &&
            image.destination_rect.y == 5.0F &&
            image.destination_rect.width == 20.0F &&
            image.destination_rect.height == 10.0F;
    }
    PROGPU_REQUIRE(image_count == 1U);
    PROGPU_REQUIRE(found_static);

    std::vector<std::byte> animated;
    append_command(
        animated,
        command::video_drawing,
        video_drawing,
        3.0,
        5.0,
        20.0,
        10.0,
        player,
        rectangle_animation);
    PROGPU_REQUIRE(state.apply(animated) == status::success);
    PROGPU_REQUIRE(
        state.build_scene(target, 7016U, 3U, stream) == status::success);
    const auto animated_header = read_value<progpu_native_scene_header>(
        stream, 0U);
    bool found_animated = false;
    for (std::uint32_t index = 0U;
         index < animated_header.command_count;
         ++index) {
        const auto record = read_value<progpu_native_scene_command>(
            stream,
            animated_header.command_offset +
                index * sizeof(progpu_native_scene_command));
        if (record.kind != PROGPU_NATIVE_SCENE_COMMAND_DRAW_IMAGE) {
            continue;
        }
        const auto image = read_value<progpu_native_scene_image_draw>(
            stream, record.payload_offset);
        found_animated |= image.destination_rect.x == 7.0F &&
            image.destination_rect.y == 9.0F &&
            image.destination_rect.width == 12.0F &&
            image.destination_rect.height == 14.0F;
    }
    PROGPU_REQUIRE(found_animated);

    std::vector<std::byte> delete_player;
    append_command(
        delete_player,
        command::channel_delete_resource,
        player,
        1U);
    PROGPU_REQUIRE(state.apply(delete_player) == status::invalid_graph);

    std::vector<std::byte> raw_pointer;
    append_command(
        raw_pointer,
        command::media_player,
        player,
        std::uint64_t{1U},
        0U);
    PROGPU_REQUIRE(state.apply(raw_pointer) == status::invalid_argument);
    PROGPU_REQUIRE(state.resource_generation(player) == 3U);

    std::vector<std::byte> invalid_notify;
    append_command(
        invalid_notify,
        command::media_player,
        player,
        std::uint64_t{0U},
        2U);
    PROGPU_REQUIRE(state.apply(invalid_notify) == status::malformed_batch);
    PROGPU_REQUIRE(state.resource_generation(player) == 3U);
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
    constexpr std::uint32_t rectangle_animation = 10U;
    constexpr std::uint32_t geometry_transform = 11U;
    constexpr std::uint32_t nested_drawing_image = 12U;
    constexpr std::uint32_t nested_group = 13U;
    constexpr std::uint32_t nested_transform = 14U;

    std::vector<std::byte> batch;
    append_create(batch, visual, 39U);
    append_create(batch, content, 43U);
    append_create(batch, target, 47U);
    append_create(batch, brush, 75U);
    append_create(batch, geometry, 69U);
    append_create(batch, geometry_drawing, 87U);
    append_create(batch, drawing_image, 59U);
    append_create(batch, image_drawing, 89U);
    append_create(batch, rectangle_animation, 52U);
    append_create(batch, geometry_transform, 66U);
    append_create(batch, nested_drawing_image, 59U);
    append_create(batch, nested_group, 91U);
    append_create(batch, nested_transform, 66U);
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
        command::matrix_transform,
        geometry_transform,
        1.0,
        0.0,
        0.0,
        1.0,
        5.0,
        7.0,
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
        geometry_transform,
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
        command::rect_resource,
        rectangle_animation,
        44.0,
        8.0,
        8.0,
        12.0);
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
    append_command(
        batch,
        command::matrix_transform,
        nested_transform,
        1.0,
        0.25,
        0.5,
        1.0,
        3.0,
        2.0,
        0U);
    append_command(
        batch,
        command::drawing_group,
        nested_group,
        1.0,
        4U,
        0U,
        0U,
        0U,
        nested_transform,
        0U,
        0U,
        0U,
        0U,
        image_drawing);
    append_command(
        batch,
        command::drawing_image,
        nested_drawing_image,
        nested_group);
    std::vector<std::byte> nested;
    append_command(nested, command::draw_drawing, image_drawing, 0U);
    append_command(
        nested,
        command::draw_image,
        30.0,
        6.0,
        10.0,
        15.0,
        drawing_image,
        0U);
    append_command(
        nested,
        command::draw_image_animate,
        0.0,
        0.0,
        1.0,
        1.0,
        drawing_image,
        rectangle_animation);
    append_command(
        nested,
        command::draw_image,
        5.0,
        7.0,
        80.0,
        40.0,
        nested_drawing_image,
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
        state.build_scene(target, 7006U, 1U, stream, &metrics) ==
        status::success);
    PROGPU_REQUIRE(metrics.rectangle_count == 4U);
    PROGPU_REQUIRE(metrics.brush_count == 1U);

    const auto header = read_value<progpu_native_scene_header>(stream, 0U);
    bool found_mapping = false;
    bool found_direct_mapping = false;
    bool found_animated_mapping = false;
    bool found_nested_mapping = false;
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
            scene_state.transform.m31 == -28.0F &&
            scene_state.transform.m32 == -50.0F &&
            (scene_state.flags & PROGPU_NATIVE_SCENE_STATE_CLIP_RECT) != 0U &&
            scene_state.clip_rect.x == 2.0F &&
            scene_state.clip_rect.y == 4.0F &&
            scene_state.clip_rect.width == 40.0F &&
            scene_state.clip_rect.height == 20.0F) {
            found_mapping = true;
        } else if (scene_state.transform.m11 == 0.5F &&
            scene_state.transform.m22 == 1.5F &&
            scene_state.transform.m31 == 22.5F &&
            scene_state.transform.m32 == -34.5F &&
            (scene_state.flags & PROGPU_NATIVE_SCENE_STATE_CLIP_RECT) != 0U &&
            scene_state.clip_rect.x == 30.0F &&
            scene_state.clip_rect.y == 6.0F &&
            scene_state.clip_rect.width == 10.0F &&
            scene_state.clip_rect.height == 15.0F) {
            found_direct_mapping = true;
        } else if (scene_state.transform.m11 == 0.4F &&
            scene_state.transform.m22 == 1.2F &&
            scene_state.transform.m31 == 38.0F &&
            scene_state.transform.m32 == -24.4F &&
            (scene_state.flags & PROGPU_NATIVE_SCENE_STATE_CLIP_RECT) != 0U &&
            scene_state.clip_rect.x == 44.0F &&
            scene_state.clip_rect.y == 8.0F &&
            scene_state.clip_rect.width == 8.0F &&
            scene_state.clip_rect.height == 12.0F) {
            found_animated_mapping = true;
        } else if (std::abs(scene_state.transform.m11 - 3.2F) < 0.0001F &&
            std::abs(
                scene_state.transform.m12 - 2.0F / 3.0F) < 0.0001F &&
            std::abs(scene_state.transform.m21 - 1.6F) < 0.0001F &&
            std::abs(
                scene_state.transform.m22 - 8.0F / 3.0F) < 0.0001F &&
            std::abs(scene_state.transform.m31 + 86.2F) < 0.0001F &&
            std::abs(scene_state.transform.m32 + 75.0F) < 0.0001F &&
            (scene_state.flags & PROGPU_NATIVE_SCENE_STATE_CLIP_RECT) != 0U &&
            scene_state.clip_rect.x == 5.0F &&
            scene_state.clip_rect.y == 7.0F &&
            scene_state.clip_rect.width == 80.0F &&
            scene_state.clip_rect.height == 40.0F) {
            found_nested_mapping = true;
        }
    }
    PROGPU_REQUIRE(found_mapping);
    PROGPU_REQUIRE(found_direct_mapping);
    PROGPU_REQUIRE(found_animated_mapping);
    PROGPU_REQUIRE(found_nested_mapping);

    std::vector<std::byte> animated_image_drawing;
    append_command(
        animated_image_drawing,
        command::image_drawing,
        image_drawing,
        2.0,
        4.0,
        40.0,
        20.0,
        drawing_image,
        rectangle_animation);
    PROGPU_REQUIRE(state.apply(animated_image_drawing) == status::success);
    PROGPU_REQUIRE(
        state.build_scene(target, 7006U, 2U, stream, &metrics) ==
        status::success);
    PROGPU_REQUIRE(metrics.rectangle_count == 4U);
    const auto animated_image_header =
        read_value<progpu_native_scene_header>(stream, 0U);
    bool found_live_nested_mapping = false;
    for (std::uint32_t index = 0U;
         index < animated_image_header.resource_count;
         ++index) {
        const auto resource = read_value<progpu_native_scene_resource>(
            stream,
            animated_image_header.resource_offset +
                index * sizeof(progpu_native_scene_resource));
        if (resource.kind != PROGPU_NATIVE_SCENE_RESOURCE_STATE) {
            continue;
        }
        const auto scene_state = read_value<progpu_native_scene_state>(
            stream, resource.payload_offset);
        if (std::abs(
                scene_state.transform.m11 - 16.0F / 7.0F) < 0.0001F &&
            std::abs(
                scene_state.transform.m12 - 2.0F / 7.0F) < 0.0001F &&
            std::abs(
                scene_state.transform.m21 - 24.0F / 7.0F) < 0.0001F &&
            std::abs(
                scene_state.transform.m22 - 24.0F / 7.0F) < 0.0001F &&
            std::abs(
                scene_state.transform.m31 + 853.0F / 7.0F) < 0.0001F &&
            std::abs(
                scene_state.transform.m32 + 629.0F / 7.0F) < 0.0001F &&
            (scene_state.flags & PROGPU_NATIVE_SCENE_STATE_CLIP_RECT) != 0U &&
            scene_state.clip_rect.x == 5.0F &&
            scene_state.clip_rect.y == 7.0F &&
            scene_state.clip_rect.width == 80.0F &&
            scene_state.clip_rect.height == 40.0F) {
            found_live_nested_mapping = true;
        }
    }
    PROGPU_REQUIRE(found_live_nested_mapping);
    PROGPU_REQUIRE(
        state.set_drawing_image_bounds(
            drawing_image, 15.0, 27.0, 20.0, 10.0) ==
        status::success);
    PROGPU_REQUIRE(state.resource_generation(drawing_image) == 3U);
    PROGPU_REQUIRE(
        state.build_scene(target, 7006U, 3U, stream, &metrics) ==
        status::success);
    PROGPU_REQUIRE(metrics.rectangle_count == 4U);
    PROGPU_REQUIRE(metrics.brush_count == 1U);

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
        state.build_scene(target, 7006U, 4U, stream, &metrics) ==
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

bool retained_drawing_image_infers_line_path_bounds() {
    constexpr std::uint32_t visual = 1U;
    constexpr std::uint32_t content = 2U;
    constexpr std::uint32_t target = 3U;
    constexpr std::uint32_t brush = 4U;
    constexpr std::uint32_t geometry = 5U;
    constexpr std::uint32_t geometry_drawing = 6U;
    constexpr std::uint32_t drawing_image = 7U;

    std::vector<std::byte> batch;
    append_create(batch, visual, 39U);
    append_create(batch, content, 43U);
    append_create(batch, target, 47U);
    append_create(batch, brush, 75U);
    append_create(batch, geometry, 73U);
    append_create(batch, geometry_drawing, 87U);
    append_create(batch, drawing_image, 59U);
    append_command(batch, command::visual_create, visual);
    append_command(batch, command::visual_set_content, visual, content);
    append_command(
        batch,
        command::solid_color_brush,
        brush,
        1.0,
        progpu_native_color{0.2F, 0.6F, 0.4F, 1.0F},
        0U,
        0U,
        0U,
        0U);
    append_path_geometry(
        batch,
        geometry,
        0U,
        1U,
        make_rectangle_path_figures(10.0, 20.0, 30.0, 30.0));
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
    std::vector<std::byte> nested;
    append_command(
        nested,
        command::draw_image,
        2.0,
        4.0,
        40.0,
        20.0,
        drawing_image,
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
        state.build_scene(target, 7007U, 1U, stream, nullptr) ==
        status::success);
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
            (scene_state.flags & PROGPU_NATIVE_SCENE_STATE_CLIP_RECT) != 0U) {
            found_mapping = true;
        }
    }
    PROGPU_REQUIRE(found_mapping);

    const std::array quadratic_points{
        std::array{5.0, 9.0},
        std::array{11.0, 3.0}};
    std::vector<std::byte> curved_update;
    append_path_geometry(
        curved_update,
        geometry,
        0U,
        1U,
        make_single_bezier_path_figures(3U, quadratic_points));
    PROGPU_REQUIRE(state.apply(curved_update) == status::success);
    PROGPU_REQUIRE(
        state.build_scene(target, 7007U, 2U, stream, nullptr) ==
        status::success);
    const auto curved_header = read_value<progpu_native_scene_header>(
        stream, 0U);
    bool found_curve_mapping = false;
    for (std::uint32_t index = 0U;
         index < curved_header.resource_count;
         ++index) {
        const auto resource = read_value<progpu_native_scene_resource>(
            stream,
            curved_header.resource_offset +
                index * sizeof(progpu_native_scene_resource));
        if (resource.kind != PROGPU_NATIVE_SCENE_RESOURCE_STATE) {
            continue;
        }
        const auto scene_state = read_value<progpu_native_scene_state>(
            stream, resource.payload_offset);
        if (std::abs(scene_state.transform.m11 - 4.0F) < 0.0001F &&
            std::abs(
                scene_state.transform.m22 - 260.0F / 49.0F) < 0.0001F &&
            std::abs(scene_state.transform.m31 + 2.0F) < 0.0001F &&
            std::abs(
                scene_state.transform.m32 + 324.0F / 49.0F) < 0.0001F) {
            found_curve_mapping = true;
        }
    }
    PROGPU_REQUIRE(found_curve_mapping);

    std::vector<std::byte> arc_update;
    append_path_geometry(
        arc_update,
        geometry,
        0U,
        1U,
        make_arc_path_figures());
    PROGPU_REQUIRE(state.apply(arc_update) == status::success);
    PROGPU_REQUIRE(
        state.build_scene(target, 7007U, 3U, stream, nullptr) ==
        status::success);
    progpu::native::geometry::arc_point arc_center{};
    float arc_theta = 0.0F;
    float arc_delta = 0.0F;
    float arc_radius_x = 0.0F;
    float arc_radius_y = 0.0F;
    PROGPU_REQUIRE(progpu::native::geometry::resolve_arc(
        {1.0F, 2.0F},
        {9.0F, 8.0F},
        {8.0F, 6.0F},
        30.0F,
        false,
        true,
        arc_center,
        arc_theta,
        arc_delta,
        arc_radius_x,
        arc_radius_y));
    float arc_left = 1.0F;
    float arc_top = 2.0F;
    float arc_right = 9.0F;
    float arc_bottom = 8.0F;
    const float arc_rotation = 30.0F *
        std::numbers::pi_v<float> / 180.0F;
    const float arc_x_extrema = std::atan2(
        -arc_radius_y * std::sin(arc_rotation),
        arc_radius_x * std::cos(arc_rotation));
    const float arc_y_extrema = std::atan2(
        arc_radius_y * std::cos(arc_rotation),
        arc_radius_x * std::sin(arc_rotation));
    const float arc_extrema[4U]{
        arc_x_extrema,
        arc_x_extrema + std::numbers::pi_v<float>,
        arc_y_extrema,
        arc_y_extrema + std::numbers::pi_v<float>};
    for (const float theta : arc_extrema) {
        if (!progpu::native::geometry::angle_within_sweep(
                theta, arc_theta, arc_delta)) {
            continue;
        }
        const auto point = progpu::native::geometry::evaluate_arc(
            arc_center,
            arc_radius_x,
            arc_radius_y,
            30.0F,
            theta);
        arc_left = std::min(arc_left, point.x);
        arc_top = std::min(arc_top, point.y);
        arc_right = std::max(arc_right, point.x);
        arc_bottom = std::max(arc_bottom, point.y);
    }
    const float arc_scale_x = 40.0F / (arc_right - arc_left);
    const float arc_scale_y = 20.0F / (arc_bottom - arc_top);
    const float arc_offset_x = 2.0F - arc_left * arc_scale_x;
    const float arc_offset_y = 4.0F - arc_top * arc_scale_y;
    const auto arc_header = read_value<progpu_native_scene_header>(
        stream, 0U);
    bool found_arc_mapping = false;
    for (std::uint32_t index = 0U; index < arc_header.resource_count; ++index) {
        const auto resource = read_value<progpu_native_scene_resource>(
            stream,
            arc_header.resource_offset +
                index * sizeof(progpu_native_scene_resource));
        if (resource.kind != PROGPU_NATIVE_SCENE_RESOURCE_STATE) {
            continue;
        }
        const auto scene_state = read_value<progpu_native_scene_state>(
            stream, resource.payload_offset);
        if (std::abs(scene_state.transform.m11 - arc_scale_x) < 0.0001F &&
            std::abs(scene_state.transform.m22 - arc_scale_y) < 0.0001F &&
            std::abs(scene_state.transform.m31 - arc_offset_x) < 0.0001F &&
            std::abs(scene_state.transform.m32 - arc_offset_y) < 0.0001F) {
            found_arc_mapping = true;
        }
    }
    PROGPU_REQUIRE(found_arc_mapping);

    constexpr std::uint32_t transform = 8U;
    std::vector<std::byte> transformed_update;
    append_create(transformed_update, transform, 66U);
    append_command(
        transformed_update,
        command::matrix_transform,
        transform,
        1.0,
        0.0,
        0.0,
        1.0,
        1.0,
        2.0,
        0U);
    append_path_geometry(
        transformed_update,
        geometry,
        transform,
        1U,
        make_single_bezier_path_figures(3U, quadratic_points));
    PROGPU_REQUIRE(state.apply(transformed_update) == status::success);
    PROGPU_REQUIRE(
        state.build_scene(target, 7007U, 4U, stream, nullptr) ==
        status::success);
    const auto transformed_header = read_value<progpu_native_scene_header>(
        stream, 0U);
    bool found_transformed_mapping = false;
    for (std::uint32_t index = 0U;
         index < transformed_header.resource_count;
         ++index) {
        const auto resource = read_value<progpu_native_scene_resource>(
            stream,
            transformed_header.resource_offset +
                index * sizeof(progpu_native_scene_resource));
        if (resource.kind != PROGPU_NATIVE_SCENE_RESOURCE_STATE) {
            continue;
        }
        const auto scene_state = read_value<progpu_native_scene_state>(
            stream, resource.payload_offset);
        if (std::abs(scene_state.transform.m11 - 4.0F) < 0.0001F &&
            std::abs(
                scene_state.transform.m22 - 260.0F / 49.0F) < 0.0001F &&
            std::abs(scene_state.transform.m31 + 6.0F) < 0.0001F &&
            std::abs(
                scene_state.transform.m32 + 844.0F / 49.0F) < 0.0001F) {
            found_transformed_mapping = true;
        }
    }
    PROGPU_REQUIRE(found_transformed_mapping);

    constexpr std::uint32_t group = 9U;
    constexpr std::uint32_t group_transform = 10U;
    std::vector<std::byte> group_update;
    append_create(group_update, group, 71U);
    append_create(group_update, group_transform, 66U);
    append_command(
        group_update,
        command::matrix_transform,
        group_transform,
        1.0,
        0.0,
        0.0,
        1.0,
        3.0,
        4.0,
        0U);
    const std::array group_children{geometry};
    append_geometry_group(
        group_update,
        group,
        group_transform,
        0U,
        group_children);
    append_command(
        group_update,
        command::geometry_drawing,
        geometry_drawing,
        brush,
        0U,
        group);
    PROGPU_REQUIRE(state.apply(group_update) == status::success);
    PROGPU_REQUIRE(
        state.build_scene(target, 7007U, 5U, stream, nullptr) ==
        status::success);
    const auto group_header = read_value<progpu_native_scene_header>(
        stream, 0U);
    bool found_group_mapping = false;
    for (std::uint32_t index = 0U;
         index < group_header.resource_count;
         ++index) {
        const auto resource = read_value<progpu_native_scene_resource>(
            stream,
            group_header.resource_offset +
                index * sizeof(progpu_native_scene_resource));
        if (resource.kind != PROGPU_NATIVE_SCENE_RESOURCE_STATE) {
            continue;
        }
        const auto scene_state = read_value<progpu_native_scene_state>(
            stream, resource.payload_offset);
        if (std::abs(scene_state.transform.m11 - 4.0F) < 0.0001F &&
            std::abs(
                scene_state.transform.m22 - 260.0F / 49.0F) < 0.0001F &&
            std::abs(scene_state.transform.m31 + 18.0F) < 0.0001F &&
            std::abs(
                scene_state.transform.m32 + 1'884.0F / 49.0F) < 0.0001F) {
            found_group_mapping = true;
        }
    }
    PROGPU_REQUIRE(found_group_mapping);

    std::vector<std::byte> multi_child_update;
    const std::array multi_children{geometry, geometry};
    append_geometry_group(
        multi_child_update,
        group,
        group_transform,
        0U,
        multi_children);
    PROGPU_REQUIRE(state.apply(multi_child_update) == status::success);
    PROGPU_REQUIRE(
        state.build_scene(target, 7007U, 6U, stream, nullptr) ==
        status::unsupported_command);
    return true;
}

bool retained_drawing_image_infers_fixed_stroke_bounds() {
    constexpr std::uint32_t visual = 1U;
    constexpr std::uint32_t content = 2U;
    constexpr std::uint32_t target = 3U;
    constexpr std::uint32_t brush = 4U;
    constexpr std::uint32_t pen = 5U;
    constexpr std::uint32_t thickness_animation = 6U;
    constexpr std::uint32_t geometry = 7U;
    constexpr std::uint32_t geometry_drawing = 8U;
    constexpr std::uint32_t drawing_image = 9U;
    constexpr std::uint32_t shear_transform = 10U;
    constexpr std::uint32_t rectangle_geometry = 11U;
    constexpr std::uint32_t ellipse_geometry = 12U;
    constexpr std::uint32_t dash_style = 13U;
    constexpr std::uint32_t transformed_group = 14U;

    std::vector<std::byte> batch;
    append_create(batch, visual, 39U);
    append_create(batch, content, 43U);
    append_create(batch, target, 47U);
    append_create(batch, brush, 75U);
    append_create(batch, pen, 85U);
    append_create(batch, thickness_animation, 49U);
    append_create(batch, geometry, 68U);
    append_create(batch, geometry_drawing, 87U);
    append_create(batch, drawing_image, 59U);
    append_create(batch, rectangle_geometry, 69U);
    append_create(batch, ellipse_geometry, 70U);
    append_create(batch, dash_style, 84U);
    append_command(batch, command::visual_create, visual);
    append_command(batch, command::visual_set_content, visual, content);
    append_command(
        batch,
        command::solid_color_brush,
        brush,
        1.0,
        progpu_native_color{0.7F, 0.3F, 0.2F, 1.0F},
        0U,
        0U,
        0U,
        0U);
    append_command(
        batch,
        command::double_resource,
        thickness_animation,
        4.0);
    append_command(
        batch,
        command::pen,
        pen,
        2.0,
        10.0,
        brush,
        thickness_animation,
        PROGPU_NATIVE_STROKE_CAP_SQUARE,
        PROGPU_NATIVE_STROKE_CAP_SQUARE,
        PROGPU_NATIVE_STROKE_CAP_FLAT,
        PROGPU_NATIVE_STROKE_JOIN_MITER,
        0U);
    append_command(
        batch,
        command::line_geometry,
        geometry,
        10.0,
        20.0,
        30.0,
        20.0,
        0U,
        0U,
        0U);
    append_command(
        batch,
        command::rectangle_geometry,
        rectangle_geometry,
        3.0,
        4.0,
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
        command::ellipse_geometry,
        ellipse_geometry,
        10.0,
        5.0,
        20.0,
        30.0,
        0U,
        0U,
        0U,
        0U);
    const std::array dash_intervals{1.0, 1.0};
    append_dash_style(batch, dash_style, 0.0, 0U, dash_intervals);
    append_command(
        batch,
        command::geometry_drawing,
        geometry_drawing,
        0U,
        pen,
        geometry);
    append_command(
        batch,
        command::drawing_image,
        drawing_image,
        geometry_drawing);
    std::vector<std::byte> nested;
    append_command(
        nested,
        command::draw_image,
        2.0,
        4.0,
        40.0,
        20.0,
        drawing_image,
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
        state.build_scene(target, 7008U, 1U, stream, &metrics) ==
        status::success);
    PROGPU_REQUIRE(metrics.line_count == 1U);
    const auto contains_mapping = [&stream](
        float m11,
        float m22,
        float m31,
        float m32) {
        const auto header = read_value<progpu_native_scene_header>(stream, 0U);
        for (std::uint32_t index = 0U;
             index < header.resource_count;
             ++index) {
            const auto resource = read_value<progpu_native_scene_resource>(
                stream,
                header.resource_offset +
                    index * sizeof(progpu_native_scene_resource));
            if (resource.kind != PROGPU_NATIVE_SCENE_RESOURCE_STATE) {
                continue;
            }
            const auto scene_state = read_value<progpu_native_scene_state>(
                stream, resource.payload_offset);
            if (std::abs(scene_state.transform.m11 - m11) < 0.0001F &&
                std::abs(scene_state.transform.m22 - m22) < 0.0001F &&
                std::abs(scene_state.transform.m31 - m31) < 0.0001F &&
                std::abs(scene_state.transform.m32 - m32) < 0.0001F) {
                return true;
            }
        }
        return false;
    };
    PROGPU_REQUIRE(contains_mapping(
        5.0F / 3.0F, 5.0F, -34.0F / 3.0F, -86.0F));

    std::vector<std::byte> thickness_update;
    append_command(
        thickness_update,
        command::double_resource,
        thickness_animation,
        8.0);
    PROGPU_REQUIRE(state.apply(thickness_update) == status::success);
    PROGPU_REQUIRE(
        state.build_scene(target, 7008U, 2U, stream, &metrics) ==
        status::success);
    PROGPU_REQUIRE(contains_mapping(
        10.0F / 7.0F, 2.5F, -46.0F / 7.0F, -36.0F));

    std::vector<std::byte> rectangle_update;
    append_command(
        rectangle_update,
        command::geometry_drawing,
        geometry_drawing,
        0U,
        pen,
        rectangle_geometry);
    PROGPU_REQUIRE(state.apply(rectangle_update) == status::success);
    PROGPU_REQUIRE(
        state.build_scene(target, 7008U, 3U, stream, &metrics) ==
        status::success);
    PROGPU_REQUIRE(contains_mapping(
        10.0F / 7.0F,
        10.0F / 9.0F,
        -46.0F / 7.0F,
        -124.0F / 9.0F));

    std::vector<std::byte> ellipse_update;
    append_command(
        ellipse_update,
        command::geometry_drawing,
        geometry_drawing,
        0U,
        pen,
        ellipse_geometry);
    PROGPU_REQUIRE(state.apply(ellipse_update) == status::success);
    PROGPU_REQUIRE(
        state.build_scene(target, 7008U, 4U, stream, &metrics) ==
        status::success);
    PROGPU_REQUIRE(contains_mapping(
        10.0F / 7.0F,
        10.0F / 9.0F,
        -46.0F / 7.0F,
        -58.0F / 3.0F));

    std::vector<std::byte> degenerate_rectangle_update;
    append_command(
        degenerate_rectangle_update,
        command::rectangle_geometry,
        rectangle_geometry,
        0.0,
        0.0,
        10.0,
        20.0,
        0.0,
        10.0,
        0U,
        0U,
        0U,
        0U);
    append_command(
        degenerate_rectangle_update,
        command::geometry_drawing,
        geometry_drawing,
        0U,
        pen,
        rectangle_geometry);
    PROGPU_REQUIRE(
        state.apply(degenerate_rectangle_update) == status::success);
    PROGPU_REQUIRE(
        state.build_scene(target, 7008U, 5U, stream, &metrics) ==
        status::success);
    PROGPU_REQUIRE(contains_mapping(
        5.0F, 10.0F / 9.0F, -28.0F, -124.0F / 9.0F));

    std::vector<std::byte> degenerate_ellipse_update;
    append_command(
        degenerate_ellipse_update,
        command::ellipse_geometry,
        ellipse_geometry,
        0.0,
        5.0,
        20.0,
        30.0,
        0U,
        0U,
        0U,
        0U);
    append_command(
        degenerate_ellipse_update,
        command::geometry_drawing,
        geometry_drawing,
        0U,
        pen,
        ellipse_geometry);
    PROGPU_REQUIRE(
        state.apply(degenerate_ellipse_update) == status::success);
    PROGPU_REQUIRE(
        state.build_scene(target, 7008U, 6U, stream, &metrics) ==
        status::success);
    PROGPU_REQUIRE(contains_mapping(
        5.0F, 10.0F / 9.0F, -78.0F, -58.0F / 3.0F));

    std::vector<std::byte> point_ellipse_update;
    append_command(
        point_ellipse_update,
        command::ellipse_geometry,
        ellipse_geometry,
        0.0,
        0.0,
        20.0,
        30.0,
        0U,
        0U,
        0U,
        0U);
    PROGPU_REQUIRE(state.apply(point_ellipse_update) == status::success);
    PROGPU_REQUIRE(
        state.build_scene(target, 7008U, 7U, stream, &metrics) ==
        status::success);
    PROGPU_REQUIRE(contains_mapping(5.0F, 2.5F, -78.0F, -61.0F));

    std::vector<std::byte> shear_update;
    append_create(shear_update, shear_transform, 66U);
    append_command(
        shear_update,
        command::matrix_transform,
        shear_transform,
        1.0,
        0.25,
        0.5,
        1.0,
        0.0,
        0.0,
        0U);
    append_command(
        shear_update,
        command::line_geometry,
        geometry,
        10.0,
        20.0,
        30.0,
        20.0,
        shear_transform,
        0U,
        0U);
    append_command(
        shear_update,
        command::geometry_drawing,
        geometry_drawing,
        0U,
        pen,
        geometry);
    PROGPU_REQUIRE(state.apply(shear_update) == status::success);
    PROGPU_REQUIRE(
        state.build_scene(target, 7008U, 8U, stream, &metrics) ==
        status::success);
    PROGPU_REQUIRE(contains_mapping(
        1.3467368F,
        1.3604125F,
        -18.402102F,
        -20.010311F));

    std::vector<std::byte> affine_ellipse_update;
    append_command(
        affine_ellipse_update,
        command::ellipse_geometry,
        ellipse_geometry,
        10.0,
        5.0,
        20.0,
        30.0,
        shear_transform,
        0U,
        0U,
        0U);
    append_command(
        affine_ellipse_update,
        command::geometry_drawing,
        geometry_drawing,
        0U,
        pen,
        ellipse_geometry);
    PROGPU_REQUIRE(state.apply(affine_ellipse_update) == status::success);
    PROGPU_REQUIRE(
        state.build_scene(target, 7008U, 105U, stream, &metrics) ==
        status::success);
    // Live Windows WPF EllipseGeometry.GetRenderBounds oracle:
    // 20.719608306884766,25.423517227172852,
    // 28.560783386230469,19.152963638305664.
    PROGPU_REQUIRE(contains_mapping(
        1.4005218F,
        1.0442249F,
        -27.018263F,
        -22.547869F));

    std::vector<std::byte> refined_ellipse_update;
    append_command(
        refined_ellipse_update,
        command::double_resource,
        thickness_animation,
        64.0);
    PROGPU_REQUIRE(state.apply(refined_ellipse_update) == status::success);
    PROGPU_REQUIRE(
        state.build_scene(target, 7008U, 106U, stream, &metrics) ==
        status::success);
    // Live WPF thick-stroke RoundTo refinement oracle:
    // -7.13843107223511,-2.55054593086243,
    // 84.2768588066101,75.101090669632.
    PROGPU_REQUIRE(contains_mapping(
        0.47462615F,
        0.2663077F,
        5.388086F,
        4.67923F));

    std::vector<std::byte> triangle_cap_update;
    append_command(
        triangle_cap_update,
        command::double_resource,
        thickness_animation,
        8.0);
    append_command(
        triangle_cap_update,
        command::pen,
        pen,
        2.0,
        10.0,
        brush,
        thickness_animation,
        PROGPU_NATIVE_STROKE_CAP_TRIANGLE,
        PROGPU_NATIVE_STROKE_CAP_TRIANGLE,
        PROGPU_NATIVE_STROKE_CAP_FLAT,
        PROGPU_NATIVE_STROKE_JOIN_MITER,
        0U);
    append_command(
        triangle_cap_update,
        command::geometry_drawing,
        geometry_drawing,
        0U,
        pen,
        geometry);
    PROGPU_REQUIRE(state.apply(triangle_cap_update) == status::success);
    PROGPU_REQUIRE(
        state.build_scene(target, 7008U, 9U, stream, &metrics) ==
        status::success);
    PROGPU_REQUIRE(contains_mapping(
        1.4408631F,
        1.5672582F,
        -21.225891F,
        -25.181456F));

    std::vector<std::byte> round_cap_update;
    append_command(
        round_cap_update,
        command::pen,
        pen,
        2.0,
        10.0,
        brush,
        thickness_animation,
        PROGPU_NATIVE_STROKE_CAP_ROUND,
        PROGPU_NATIVE_STROKE_CAP_ROUND,
        PROGPU_NATIVE_STROKE_CAP_FLAT,
        PROGPU_NATIVE_STROKE_JOIN_MITER,
        0U);
    PROGPU_REQUIRE(state.apply(round_cap_update) == status::success);
    PROGPU_REQUIRE(
        state.build_scene(target, 7008U, 10U, stream, &metrics) ==
        status::success);
    PROGPU_REQUIRE(contains_mapping(
        1.4284749F,
        1.5382377F,
        -20.854248F,
        -24.455942F));

    std::vector<std::byte> affine_rectangle_update;
    append_command(
        affine_rectangle_update,
        command::rectangle_geometry,
        rectangle_geometry,
        0.0,
        0.0,
        20.0,
        10.0,
        30.0,
        15.0,
        shear_transform,
        0U,
        0U,
        0U);
    append_command(
        affine_rectangle_update,
        command::geometry_drawing,
        geometry_drawing,
        0U,
        pen,
        rectangle_geometry);
    PROGPU_REQUIRE(
        state.apply(affine_rectangle_update) == status::success);
    PROGPU_REQUIRE(
        state.build_scene(target, 7008U, 11U, stream, &metrics) ==
        status::success);
    PROGPU_REQUIRE(contains_mapping(
        0.7628617F,
        0.5800507F,
        -11.375198F,
        -1.2263298F));

    std::vector<std::byte> bevel_rectangle_update;
    append_command(
        bevel_rectangle_update,
        command::pen,
        pen,
        2.0,
        10.0,
        brush,
        thickness_animation,
        PROGPU_NATIVE_STROKE_CAP_ROUND,
        PROGPU_NATIVE_STROKE_CAP_ROUND,
        PROGPU_NATIVE_STROKE_CAP_FLAT,
        PROGPU_NATIVE_STROKE_JOIN_BEVEL,
        0U);
    PROGPU_REQUIRE(
        state.apply(bevel_rectangle_update) == status::success);
    PROGPU_REQUIRE(
        state.build_scene(target, 7008U, 12U, stream, &metrics) ==
        status::success);
    PROGPU_REQUIRE(contains_mapping(
        0.8957480F,
        0.6609136F,
        -17.188974F,
        -3.3489826F));

    std::vector<std::byte> clipped_miter_update;
    append_command(
        clipped_miter_update,
        command::pen,
        pen,
        2.0,
        1.0,
        brush,
        thickness_animation,
        PROGPU_NATIVE_STROKE_CAP_ROUND,
        PROGPU_NATIVE_STROKE_CAP_ROUND,
        PROGPU_NATIVE_STROKE_CAP_FLAT,
        PROGPU_NATIVE_STROKE_JOIN_MITER,
        0U);
    PROGPU_REQUIRE(state.apply(clipped_miter_update) == status::success);
    PROGPU_REQUIRE(
        state.build_scene(target, 7008U, 13U, stream, &metrics) ==
        status::success);
    PROGPU_REQUIRE(contains_mapping(
        0.8520085F,
        0.6348318F,
        -15.275372F,
        -2.6643355F));

    std::vector<std::byte> round_rectangle_update;
    append_command(
        round_rectangle_update,
        command::pen,
        pen,
        2.0,
        10.0,
        brush,
        thickness_animation,
        PROGPU_NATIVE_STROKE_CAP_ROUND,
        PROGPU_NATIVE_STROKE_CAP_ROUND,
        PROGPU_NATIVE_STROKE_CAP_FLAT,
        PROGPU_NATIVE_STROKE_JOIN_ROUND,
        0U);
    PROGPU_REQUIRE(state.apply(round_rectangle_update) == status::success);
    PROGPU_REQUIRE(
        state.build_scene(target, 7008U, 130U, stream, &metrics) ==
        status::success);
    // Live Windows WPF RectangleGeometry.GetRenderBounds oracle:
    // 20.999963760376,10.9998416900635,
    // 45.5000743865967,30.5003185272217.
    PROGPU_REQUIRE(contains_mapping(
        0.87911946F,
        0.65573084F,
        -16.461477F,
        -3.2129357F));

    std::vector<std::byte> affine_rounded_rectangle_update;
    append_command(
        affine_rounded_rectangle_update,
        command::pen,
        pen,
        2.0,
        10.0,
        brush,
        thickness_animation,
        PROGPU_NATIVE_STROKE_CAP_ROUND,
        PROGPU_NATIVE_STROKE_CAP_ROUND,
        PROGPU_NATIVE_STROKE_CAP_FLAT,
        PROGPU_NATIVE_STROKE_JOIN_MITER,
        0U);
    append_command(
        affine_rounded_rectangle_update,
        command::rectangle_geometry,
        rectangle_geometry,
        5.0,
        3.0,
        20.0,
        10.0,
        30.0,
        15.0,
        shear_transform,
        0U,
        0U,
        0U);
    append_command(
        affine_rounded_rectangle_update,
        command::geometry_drawing,
        geometry_drawing,
        0U,
        pen,
        rectangle_geometry);
    PROGPU_REQUIRE(
        state.apply(affine_rounded_rectangle_update) == status::success);
    PROGPU_REQUIRE(
        state.build_scene(target, 7008U, 131U, stream, &metrics) ==
        status::success);
    // Live Windows WPF rounded RectangleGeometry.GetRenderBounds oracle:
    // 22.42738151550293,11.999236106872559,
    // 42.645235061645508,28.501526832580566.
    PROGPU_REQUIRE(contains_mapping(
        0.93797115F,
        0.7017168F,
        -19.036236F,
        -4.4200654F));

    std::vector<std::byte> group_transform_update;
    append_create(group_transform_update, transformed_group, 91U);
    append_command(
        group_transform_update,
        command::pen,
        pen,
        2.0,
        10.0,
        brush,
        thickness_animation,
        PROGPU_NATIVE_STROKE_CAP_ROUND,
        PROGPU_NATIVE_STROKE_CAP_ROUND,
        PROGPU_NATIVE_STROKE_CAP_FLAT,
        PROGPU_NATIVE_STROKE_JOIN_MITER,
        0U);
    append_command(
        group_transform_update,
        command::rectangle_geometry,
        rectangle_geometry,
        0.0,
        0.0,
        20.0,
        10.0,
        30.0,
        15.0,
        0U,
        0U,
        0U,
        0U);
    append_command(
        group_transform_update,
        command::drawing_group,
        transformed_group,
        1.0,
        4U,
        0U,
        0U,
        0U,
        shear_transform,
        0U,
        0U,
        0U,
        0U,
        geometry_drawing);
    append_command(
        group_transform_update,
        command::drawing_image,
        drawing_image,
        transformed_group);
    PROGPU_REQUIRE(state.apply(group_transform_update) == status::success);
    PROGPU_REQUIRE(
        state.build_scene(target, 7008U, 14U, stream, &metrics) ==
        status::success);
    PROGPU_REQUIRE(contains_mapping(
        0.8080808F,
        0.6153846F,
        -13.353535F,
        -2.1538463F));

    std::vector<std::byte> round_group_update;
    append_command(
        round_group_update,
        command::pen,
        pen,
        2.0,
        10.0,
        brush,
        thickness_animation,
        PROGPU_NATIVE_STROKE_CAP_ROUND,
        PROGPU_NATIVE_STROKE_CAP_ROUND,
        PROGPU_NATIVE_STROKE_CAP_FLAT,
        PROGPU_NATIVE_STROKE_JOIN_ROUND,
        0U);
    PROGPU_REQUIRE(state.apply(round_group_update) == status::success);
    PROGPU_REQUIRE(
        state.build_scene(target, 7008U, 140U, stream, &metrics) ==
        status::success);
    // Live Windows WPF DrawingGroup.Bounds oracle:
    // 20.5268840789795,10.875919342041,
    // 46.4462299346924,30.748161315918.
    PROGPU_REQUIRE(contains_mapping(
        0.8612109F,
        0.6504454F,
        -15.677977F,
        -3.0741916F));

    std::vector<std::byte> rounded_group_update;
    append_command(
        rounded_group_update,
        command::pen,
        pen,
        2.0,
        10.0,
        brush,
        thickness_animation,
        PROGPU_NATIVE_STROKE_CAP_ROUND,
        PROGPU_NATIVE_STROKE_CAP_ROUND,
        PROGPU_NATIVE_STROKE_CAP_FLAT,
        PROGPU_NATIVE_STROKE_JOIN_MITER,
        0U);
    append_command(
        rounded_group_update,
        command::rectangle_geometry,
        rectangle_geometry,
        5.0,
        3.0,
        20.0,
        10.0,
        30.0,
        15.0,
        0U,
        0U,
        0U,
        0U);
    append_command(
        rounded_group_update,
        command::geometry_drawing,
        geometry_drawing,
        0U,
        pen,
        rectangle_geometry);
    PROGPU_REQUIRE(state.apply(rounded_group_update) == status::success);
    PROGPU_REQUIRE(
        state.build_scene(target, 7008U, 141U, stream, &metrics) ==
        status::success);
    // Live Windows WPF rounded-rectangle DrawingGroup.Bounds oracle:
    // 21.880094528198242,11.876118659973145,
    // 43.739809036254883,28.747763633728027.
    PROGPU_REQUIRE(contains_mapping(
        0.91449875F,
        0.6957063F,
        -18.00932F,
        -4.2622905F));

    std::vector<std::byte> group_ellipse_update;
    append_command(
        group_ellipse_update,
        command::ellipse_geometry,
        ellipse_geometry,
        10.0,
        5.0,
        20.0,
        30.0,
        0U,
        0U,
        0U,
        0U);
    append_command(
        group_ellipse_update,
        command::geometry_drawing,
        geometry_drawing,
        0U,
        pen,
        ellipse_geometry);
    PROGPU_REQUIRE(state.apply(group_ellipse_update) == status::success);
    PROGPU_REQUIRE(
        state.build_scene(target, 7008U, 145U, stream, &metrics) ==
        status::success);
    // Live Windows WPF DrawingGroup.Bounds ellipse oracle:
    // 20.239826202392578,25.299463272094727,
    // 29.520347595214844,19.40107536315918.
    PROGPU_REQUIRE(contains_mapping(
        1.3549976F,
        1.0308707F,
        -25.424915F,
        -22.080475F));

    std::vector<std::byte> refined_group_ellipse_update;
    append_command(
        refined_group_ellipse_update,
        command::double_resource,
        thickness_animation,
        64.0);
    PROGPU_REQUIRE(
        state.apply(refined_group_ellipse_update) == status::success);
    PROGPU_REQUIRE(
        state.build_scene(target, 7008U, 146U, stream, &metrics) ==
        status::success);
    // Live WPF DrawingGroup thick-stroke refinement oracle:
    // -10.9766893386841,-3.54298257827759,
    // 91.9533739089966,77.0859665870667.
    PROGPU_REQUIRE(contains_mapping(
        0.43500307F,
        0.2594506F,
        6.7748938F,
        4.919229F));

    std::vector<std::byte> group_line_update;
    append_command(
        group_line_update,
        command::double_resource,
        thickness_animation,
        8.0);
    append_command(
        group_line_update,
        command::line_geometry,
        geometry,
        10.0,
        20.0,
        30.0,
        20.0,
        0U,
        0U,
        0U);
    append_command(
        group_line_update,
        command::geometry_drawing,
        geometry_drawing,
        0U,
        pen,
        geometry);
    PROGPU_REQUIRE(state.apply(group_line_update) == status::success);
    PROGPU_REQUIRE(
        state.build_scene(target, 7008U, 15U, stream, &metrics) ==
        status::success);
    PROGPU_REQUIRE(contains_mapping(
        1.3818725F,
        1.5096434F,
        -19.456175F,
        -23.741085F));

    std::vector<std::byte> restore_drawing;
    append_command(
        restore_drawing,
        command::drawing_image,
        drawing_image,
        geometry_drawing);
    PROGPU_REQUIRE(state.apply(restore_drawing) == status::success);

    std::vector<std::byte> dashed_update;
    append_command(
        dashed_update,
        command::line_geometry,
        geometry,
        10.0,
        20.0,
        30.0,
        20.0,
        0U,
        0U,
        0U);
    append_command(
        dashed_update,
        command::pen,
        pen,
        2.0,
        10.0,
        brush,
        thickness_animation,
        PROGPU_NATIVE_STROKE_CAP_SQUARE,
        PROGPU_NATIVE_STROKE_CAP_SQUARE,
        PROGPU_NATIVE_STROKE_CAP_FLAT,
        PROGPU_NATIVE_STROKE_JOIN_MITER,
        dash_style);
    PROGPU_REQUIRE(state.apply(dashed_update) == status::success);
    PROGPU_REQUIRE(
        state.build_scene(target, 7008U, 16U, stream, nullptr) ==
        status::unsupported_command);

    std::vector<std::byte> solid_dash_style_update;
    append_dash_style(
        solid_dash_style_update,
        dash_style,
        0.0,
        0U,
        std::span<const double>{});
    PROGPU_REQUIRE(
        state.apply(solid_dash_style_update) == status::success);
    PROGPU_REQUIRE(
        state.build_scene(target, 7008U, 17U, stream, &metrics) ==
        status::success);
    PROGPU_REQUIRE(contains_mapping(
        10.0F / 7.0F, 2.5F, -46.0F / 7.0F, -36.0F));
    return true;
}

bool retained_drawing_image_infers_drawing_group_bounds() {
    constexpr std::uint32_t visual = 1U;
    constexpr std::uint32_t content = 2U;
    constexpr std::uint32_t target = 3U;
    constexpr std::uint32_t brush = 4U;
    constexpr std::uint32_t first_geometry = 5U;
    constexpr std::uint32_t first_drawing = 6U;
    constexpr std::uint32_t second_geometry = 7U;
    constexpr std::uint32_t second_drawing = 8U;
    constexpr std::uint32_t inner_group = 9U;
    constexpr std::uint32_t inner_transform = 10U;
    constexpr std::uint32_t outer_group = 11U;
    constexpr std::uint32_t outer_transform = 12U;
    constexpr std::uint32_t drawing_image = 13U;
    constexpr std::uint32_t clip_geometry = 14U;
    constexpr std::uint32_t opacity_animation = 15U;
    constexpr std::uint32_t guidelines = 16U;

    std::vector<std::byte> batch;
    append_create(batch, visual, 39U);
    append_create(batch, content, 43U);
    append_create(batch, target, 47U);
    append_create(batch, brush, 75U);
    append_create(batch, first_geometry, 69U);
    append_create(batch, first_drawing, 87U);
    append_create(batch, second_geometry, 69U);
    append_create(batch, second_drawing, 87U);
    append_create(batch, inner_group, 91U);
    append_create(batch, inner_transform, 66U);
    append_create(batch, outer_group, 91U);
    append_create(batch, outer_transform, 66U);
    append_create(batch, drawing_image, 59U);
    append_create(batch, clip_geometry, 69U);
    append_create(batch, opacity_animation, 49U);
    append_create(batch, guidelines, 92U);
    append_command(batch, command::visual_create, visual);
    append_command(batch, command::visual_set_content, visual, content);
    append_command(
        batch,
        command::solid_color_brush,
        brush,
        1.0,
        progpu_native_color{0.3F, 0.5F, 0.8F, 1.0F},
        0U,
        0U,
        0U,
        0U);
    append_command(
        batch,
        command::rectangle_geometry,
        first_geometry,
        0.0,
        0.0,
        10.0,
        20.0,
        10.0,
        10.0,
        0U,
        0U,
        0U,
        0U);
    append_command(
        batch,
        command::geometry_drawing,
        first_drawing,
        brush,
        0U,
        first_geometry);
    append_command(
        batch,
        command::rectangle_geometry,
        second_geometry,
        0.0,
        0.0,
        30.0,
        5.0,
        5.0,
        15.0,
        0U,
        0U,
        0U,
        0U);
    append_command(
        batch,
        command::geometry_drawing,
        second_drawing,
        brush,
        0U,
        second_geometry);
    append_command(
        batch,
        command::matrix_transform,
        inner_transform,
        2.0,
        0.0,
        0.0,
        3.0,
        4.0,
        7.0,
        0U);
    append_command(
        batch,
        command::rectangle_geometry,
        clip_geometry,
        0.0,
        0.0,
        18.0,
        22.0,
        14.0,
        3.0,
        0U,
        0U,
        0U,
        0U);
    append_command(
        batch,
        command::double_resource,
        opacity_animation,
        0.75);
    append_command(
        batch,
        command::guideline_set,
        guidelines,
        0U,
        0U,
        0U);
    append_command(
        batch,
        command::drawing_group,
        inner_group,
        0.25,
        8U,
        clip_geometry,
        opacity_animation,
        brush,
        inner_transform,
        guidelines,
        1U,
        3U,
        1U,
        first_drawing,
        second_drawing);
    append_command(
        batch,
        command::matrix_transform,
        outer_transform,
        0.5,
        0.0,
        0.0,
        2.0,
        1.0,
        3.0,
        0U);
    append_command(
        batch,
        command::drawing_group,
        outer_group,
        1.0,
        4U,
        0U,
        0U,
        0U,
        outer_transform,
        0U,
        0U,
        0U,
        0U,
        inner_group);
    append_command(
        batch,
        command::drawing_image,
        drawing_image,
        outer_group);
    std::vector<std::byte> nested;
    append_command(
        nested,
        command::draw_image,
        2.0,
        4.0,
        100.0,
        150.0,
        drawing_image,
        0U);
    append_render_data(batch, content, nested);
    append_command(
        batch,
        command::generic_target_create,
        target,
        std::uint64_t{0U},
        std::uint64_t{0U},
        128U,
        160U,
        0U);
    append_command(batch, command::target_set_root, target, visual);

    channel state;
    PROGPU_REQUIRE(state.apply(batch) == status::success);
    std::vector<std::byte> stream;
    progpu::native::mil::scene_metrics metrics{};
    PROGPU_REQUIRE(
        state.build_scene(target, 7008U, 1U, stream, &metrics) ==
        status::success);
    PROGPU_REQUIRE(metrics.rectangle_count == 2U);
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
        if (std::abs(scene_state.transform.m11 - 50.0F) < 0.0001F &&
            std::abs(
                scene_state.transform.m22 - 25.0F / 3.0F) < 0.0001F &&
            std::abs(scene_state.transform.m31 + 1'048.0F) < 0.0001F &&
            std::abs(
                scene_state.transform.m32 + 3'713.0F / 3.0F) <
                0.0001F &&
            (scene_state.flags & PROGPU_NATIVE_SCENE_STATE_CLIP_RECT) != 0U &&
            scene_state.clip_rect.x == 2.0F &&
            scene_state.clip_rect.y == 4.0F &&
            scene_state.clip_rect.width == 100.0F &&
            scene_state.clip_rect.height == 150.0F) {
            found_mapping = true;
        }
    }
    PROGPU_REQUIRE(found_mapping);

    std::vector<std::byte> sheared_update;
    append_command(
        sheared_update,
        command::matrix_transform,
        inner_transform,
        1.0,
        0.25,
        0.5,
        1.0,
        0.0,
        0.0,
        0U);
    PROGPU_REQUIRE(state.apply(sheared_update) == status::success);
    PROGPU_REQUIRE(
        state.build_scene(target, 7008U, 2U, stream, &metrics) ==
        status::success);
    PROGPU_REQUIRE(metrics.rectangle_count == 2U);
    const auto sheared_header = read_value<progpu_native_scene_header>(
        stream, 0U);
    bool found_sheared_mapping = false;
    for (std::uint32_t index = 0U;
         index < sheared_header.resource_count;
         ++index) {
        const auto resource = read_value<progpu_native_scene_resource>(
            stream,
            sheared_header.resource_offset +
                index * sizeof(progpu_native_scene_resource));
        if (resource.kind != PROGPU_NATIVE_SCENE_RESOURCE_STATE) {
            continue;
        }
        const auto scene_state = read_value<progpu_native_scene_state>(
            stream, resource.payload_offset);
        if (std::abs(
                scene_state.transform.m11 - 400.0F / 31.0F) < 0.0001F &&
            std::abs(
                scene_state.transform.m22 - 150.0F / 13.0F) < 0.0001F &&
            std::abs(scene_state.transform.m31 + 198.0F) < 0.0001F &&
            std::abs(
                scene_state.transform.m32 + 8'348.0F / 13.0F) <
                0.0001F &&
            (scene_state.flags & PROGPU_NATIVE_SCENE_STATE_CLIP_RECT) != 0U &&
            scene_state.clip_rect.x == 2.0F &&
            scene_state.clip_rect.y == 4.0F &&
            scene_state.clip_rect.width == 100.0F &&
            scene_state.clip_rect.height == 150.0F) {
            found_sheared_mapping = true;
        }
    }
    PROGPU_REQUIRE(found_sheared_mapping);

    std::vector<std::byte> singular_update;
    append_command(
        singular_update,
        command::matrix_transform,
        inner_transform,
        1.0,
        0.25,
        4.0,
        1.0,
        0.0,
        0.0,
        0U);
    PROGPU_REQUIRE(state.apply(singular_update) == status::success);
    PROGPU_REQUIRE(
        state.build_scene(target, 7008U, 3U, stream, &metrics) ==
        status::success);
    PROGPU_REQUIRE(metrics.rectangle_count == 0U);

    std::vector<std::byte> empty_group_update;
    append_command(
        empty_group_update,
        command::drawing_group,
        inner_group,
        1.0,
        0U,
        0U,
        0U,
        0U,
        0U,
        0U,
        0U,
        0U,
        0U);
    PROGPU_REQUIRE(state.apply(empty_group_update) == status::success);
    PROGPU_REQUIRE(
        state.build_scene(target, 7008U, 4U, stream, &metrics) ==
        status::success);
    PROGPU_REQUIRE(metrics.rectangle_count == 0U);
    return true;
}

bool retained_glyph_run_drawing_uses_pointer_free_sfnt_sideband() {
    constexpr std::uint32_t visual = 1U;
    constexpr std::uint32_t content = 2U;
    constexpr std::uint32_t target = 3U;
    constexpr std::uint32_t brush = 4U;
    constexpr std::uint32_t glyph_run = 5U;
    constexpr std::uint32_t drawing = 6U;
    constexpr std::uint32_t drawing_image = 8U;
    constexpr std::uint32_t bounds_group = 9U;
    constexpr std::uint32_t bounds_transform = 10U;

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
    append_create(batch, drawing_image, 59U);
    append_create(batch, bounds_group, 91U);
    append_create(batch, bounds_transform, 66U);
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
    append_command(
        batch,
        command::matrix_transform,
        bounds_transform,
        1.0,
        0.25,
        0.5,
        1.0,
        3.0,
        2.0,
        0U);
    append_command(
        batch,
        command::drawing_group,
        bounds_group,
        1.0,
        4U,
        0U,
        0U,
        0U,
        bounds_transform,
        0U,
        0U,
        0U,
        0U,
        drawing);
    append_command(
        batch,
        command::drawing_image,
        drawing_image,
        bounds_group);
    std::vector<std::byte> nested;
    append_command(nested, command::draw_drawing, drawing, 0U);
    append_command(nested, command::draw_glyph_run, brush, glyph_run);
    append_command(
        nested,
        command::draw_image,
        2.0,
        4.0,
        108.0,
        90.0,
        drawing_image,
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
    batch_metrics applied{};
    PROGPU_REQUIRE(state.apply(batch, &applied) == status::success);
    PROGPU_REQUIRE(state.resource_type(glyph_run) == 42U);
    PROGPU_REQUIRE(applied.created_resource_count == 9U);
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
    bool found_drawing_image_mapping = false;
    bool found_transformed_glyph_bounds = false;
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
        if (record.bounds_x == 2.0F && record.bounds_y == 4.0F &&
            record.bounds_width == 108.0F &&
            record.bounds_height == 90.0F) {
            found_transformed_glyph_bounds = true;
        } else {
            PROGPU_REQUIRE(record.bounds_x == 10.0F);
            PROGPU_REQUIRE(record.bounds_y == 10.0F);
            PROGPU_REQUIRE(record.bounds_width == 36.0F);
            PROGPU_REQUIRE(record.bounds_height == 36.0F);
        }
        ++glyph_draw_count;
    }
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
            scene_state.transform.m12 == 0.5F &&
            scene_state.transform.m21 == 1.0F &&
            scene_state.transform.m22 == 2.0F &&
            scene_state.transform.m31 == -28.0F &&
            scene_state.transform.m32 == -21.0F &&
            (scene_state.flags & PROGPU_NATIVE_SCENE_STATE_CLIP_RECT) != 0U &&
            scene_state.clip_rect.x == 2.0F &&
            scene_state.clip_rect.y == 4.0F &&
            scene_state.clip_rect.width == 108.0F &&
            scene_state.clip_rect.height == 90.0F) {
            found_drawing_image_mapping = true;
        }
    }
    PROGPU_REQUIRE(glyph_draw_count == 3U);
    PROGPU_REQUIRE(found_drawing_image_mapping);
    PROGPU_REQUIRE(found_transformed_glyph_bounds);
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
    constexpr std::uint32_t second_child_transform = 22U;
    constexpr std::uint32_t second_same_fill_group = 23U;
    constexpr std::uint32_t third_child_transform = 24U;
    constexpr std::uint32_t third_same_fill_group = 25U;

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
                first_group_leaf.kind == PROGPU_NATIVE_PATH_BOOLEAN_LEAF);
            PROGPU_REQUIRE(first_group_leaf.segment_offset == 0U);
            PROGPU_REQUIRE(first_group_leaf.segment_count == 4U);
            PROGPU_REQUIRE(
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
            PROGPU_REQUIRE(path.max_x == 35.0F && path.max_y == 12.0F);
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
    append_create(
        overlapping_translation_update,
        second_child_transform,
        66U);
    append_create(
        overlapping_translation_update,
        second_same_fill_group,
        71U);
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
    append_command(
        overlapping_translation_update,
        command::matrix_transform,
        second_child_transform,
        1.0,
        0.0,
        0.0,
        1.0,
        2.0,
        2.0,
        0U);
    const std::array second_same_fill_children{path_a};
    append_geometry_group(
        overlapping_translation_update,
        second_same_fill_group,
        second_child_transform,
        0U,
        second_same_fill_children);
    const std::array overlapping_translation_children{
        path_a,
        same_fill_group,
        second_same_fill_group};
    append_geometry_group(
        overlapping_translation_update,
        group,
        transform,
        0U,
        overlapping_translation_children);
    PROGPU_REQUIRE(
        state.apply(overlapping_translation_update) == status::success);
    PROGPU_REQUIRE(
        state.build_scene(target, 7003U, 9U, stream) == status::success);

    std::vector<std::byte> four_leaf_update;
    append_create(
        four_leaf_update,
        third_child_transform,
        66U);
    append_create(
        four_leaf_update,
        third_same_fill_group,
        71U);
    append_command(
        four_leaf_update,
        command::matrix_transform,
        third_child_transform,
        1.0,
        0.0,
        0.0,
        1.0,
        3.0,
        3.0,
        0U);
    const std::array third_same_fill_children{path_a};
    append_geometry_group(
        four_leaf_update,
        third_same_fill_group,
        third_child_transform,
        0U,
        third_same_fill_children);
    const std::array four_leaf_children{
        path_a,
        same_fill_group,
        second_same_fill_group,
        third_same_fill_group};
    append_geometry_group(
        four_leaf_update,
        group,
        transform,
        0U,
        four_leaf_children);
    PROGPU_REQUIRE(
        state.apply(four_leaf_update) == status::success);
    PROGPU_REQUIRE(
        state.build_scene(target, 7003U, 10U, stream) == status::success);

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

    // The visual property and the render-data opcode must use the same
    // path/group/boolean lowering, including the exact postfix mask program.
    for (const auto geometry_handle : std::array{path_a, group, combined}) {
        std::vector<std::byte> visual_clip_update;
        append_command(visual_clip_update,
            command::visual_set_clip, visual, geometry_handle);
        std::vector<std::byte> visual_content;
        append_command(visual_content, command::draw_rectangle,
            0.0, 0.0, 64.0, 64.0, brush, 0U);
        append_render_data(visual_clip_update, content, visual_content);
        PROGPU_REQUIRE(state.apply(visual_clip_update) == status::success);
        PROGPU_REQUIRE(
            state.build_scene(target, 7003U, 11U, stream) == status::success);
        const auto visual_header =
            read_value<progpu_native_scene_header>(stream, 0U);
        bool found_visual_clip = false;
        for (std::uint32_t index = 0U;
             index < visual_header.resource_count; ++index) {
            const auto resource = read_value<progpu_native_scene_resource>(
                stream, visual_header.resource_offset +
                    index * sizeof(progpu_native_scene_resource));
            if (resource.kind != PROGPU_NATIVE_SCENE_RESOURCE_LAYER_MASK) {
                continue;
            }
            const auto mask =
                read_value<progpu_native_scene_layer_vector_mask>(
                    stream, resource.payload_offset);
            PROGPU_REQUIRE(mask.kind ==
                PROGPU_NATIVE_SCENE_LAYER_MASK_VECTOR_CLIP_CHAIN);
            PROGPU_REQUIRE(mask.path_count == 1U);
            PROGPU_REQUIRE(mask.segment_count != 0U);
            PROGPU_REQUIRE(geometry_handle == path_a
                ? mask.boolean_node_count == 0U
                : mask.boolean_node_count == 3U);
            found_visual_clip = true;
        }
        PROGPU_REQUIRE(found_visual_clip);
    }
    return true;
}

bool retained_geometry_group_accepts_combined_fill_and_clip_children() {
    constexpr std::uint32_t visual = 30U;
    constexpr std::uint32_t content = 31U;
    constexpr std::uint32_t target = 32U;
    constexpr std::uint32_t brush = 33U;
    constexpr std::uint32_t rectangle_a = 34U;
    constexpr std::uint32_t rectangle_b = 35U;
    constexpr std::uint32_t rectangle_c = 36U;
    constexpr std::uint32_t combined = 37U;
    constexpr std::uint32_t group = 38U;
    constexpr std::uint32_t reflection = 39U;
    constexpr std::uint32_t rectangle_d = 40U;
    constexpr std::uint32_t reflected_group = 41U;

    std::vector<std::byte> batch;
    append_create(batch, visual, 39U);
    append_create(batch, content, 43U);
    append_create(batch, target, 47U);
    append_create(batch, brush, 75U);
    append_create(batch, rectangle_a, 69U);
    append_create(batch, rectangle_b, 69U);
    append_create(batch, rectangle_c, 69U);
    append_create(batch, rectangle_d, 69U);
    append_create(batch, combined, 72U);
    append_create(batch, group, 71U);
    append_create(batch, reflection, 66U);
    append_create(batch, reflected_group, 71U);
    append_command(batch, command::visual_create, visual);
    append_command(batch, command::visual_set_content, visual, content);
    append_command(
        batch,
        command::solid_color_brush,
        brush,
        1.0,
        progpu_native_color{0.25F, 0.75F, 1.0F, 1.0F},
        0U,
        0U,
        0U,
        0U);
    const auto append_rectangle = [&batch](
        std::uint32_t handle,
        double x,
        double y,
        double width,
        double height) {
        append_command(
            batch,
            command::rectangle_geometry,
            handle,
            0.0,
            0.0,
            x,
            y,
            width,
            height,
            0U,
            0U,
            0U,
            0U);
    };
    append_rectangle(rectangle_a, 10.0, 10.0, 20.0, 20.0);
    append_rectangle(rectangle_b, 20.0, 10.0, 20.0, 20.0);
    append_rectangle(rectangle_c, 0.0, 0.0, 8.0, 8.0);
    append_rectangle(rectangle_d, 2.0, 0.0, 8.0, 8.0);
    append_command(
        batch,
        command::matrix_transform,
        reflection,
        -1.0,
        0.0,
        0.0,
        1.0,
        40.0,
        0.0,
        0U);
    append_command(
        batch,
        command::combined_geometry,
        combined,
        0U,
        3U,
        rectangle_a,
        rectangle_b);
    const std::array children{combined, rectangle_c, rectangle_d};
    append_geometry_group(batch, group, 0U, 0U, children);
    std::vector<std::byte> nested;
    append_command(
        nested,
        command::draw_geometry,
        brush,
        0U,
        group,
        0U);
    append_command(nested, command::push_clip, group, 0U);
    append_command(
        nested,
        command::draw_rectangle,
        0.0,
        0.0,
        48.0,
        40.0,
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
        48U,
        40U,
        0U);
    append_command(batch, command::target_set_root, target, visual);

    channel state;
    PROGPU_REQUIRE(state.apply(batch) == status::success);
    std::vector<std::byte> stream;
    PROGPU_REQUIRE(
        state.build_scene(target, 7004U, 1U, stream) == status::success);
    const auto header = read_value<progpu_native_scene_header>(stream, 0U);
    bool found_fill = false;
    bool found_clip = false;
    const auto require_program = [&](std::size_t node_offset) {
        const auto first = read_value<
            progpu_native_scene_path_boolean_node>(stream, node_offset);
        const auto second = read_value<
            progpu_native_scene_path_boolean_node>(
                stream,
                node_offset + sizeof(first));
        const auto difference = read_value<
            progpu_native_scene_path_boolean_node>(
                stream,
                node_offset + 2U * sizeof(first));
        const auto third = read_value<
            progpu_native_scene_path_boolean_node>(
                stream,
                node_offset + 3U * sizeof(first));
        const auto group_xor = read_value<
            progpu_native_scene_path_boolean_node>(
                stream,
                node_offset + 4U * sizeof(first));
        const auto fourth = read_value<
            progpu_native_scene_path_boolean_node>(
                stream,
                node_offset + 5U * sizeof(first));
        const auto final_xor = read_value<
            progpu_native_scene_path_boolean_node>(
                stream,
                node_offset + 6U * sizeof(first));
        PROGPU_REQUIRE(first.kind == PROGPU_NATIVE_PATH_BOOLEAN_LEAF);
        PROGPU_REQUIRE(second.kind == PROGPU_NATIVE_PATH_BOOLEAN_LEAF);
        PROGPU_REQUIRE(
            difference.kind == PROGPU_NATIVE_PATH_BOOLEAN_DIFFERENCE);
        PROGPU_REQUIRE(third.kind == PROGPU_NATIVE_PATH_BOOLEAN_LEAF);
        PROGPU_REQUIRE(group_xor.kind == PROGPU_NATIVE_PATH_BOOLEAN_XOR);
        PROGPU_REQUIRE(fourth.kind == PROGPU_NATIVE_PATH_BOOLEAN_LEAF);
        PROGPU_REQUIRE(final_xor.kind == PROGPU_NATIVE_PATH_BOOLEAN_XOR);
        return true;
    };
    for (std::uint32_t index = 0U; index < header.resource_count; ++index) {
        const auto resource = read_value<progpu_native_scene_resource>(
            stream,
            header.resource_offset +
                index * sizeof(progpu_native_scene_resource));
        if (resource.kind == PROGPU_NATIVE_SCENE_RESOURCE_PATH_BATCH) {
            const auto path = read_value<progpu_native_scene_path_fill>(
                stream,
                resource.payload_offset);
            if (path.segment_count != 16U ||
                path.boolean_node_count != 7U) {
                continue;
            }
            PROGPU_REQUIRE(path.min_x == 0.0F && path.min_y == 0.0F);
            PROGPU_REQUIRE(path.max_x == 40.0F && path.max_y == 30.0F);
            PROGPU_REQUIRE(require_program(
                resource.auxiliary_offset +
                16U * sizeof(progpu_native_path_segment)));
            found_fill = true;
        } else if (resource.kind ==
            PROGPU_NATIVE_SCENE_RESOURCE_LAYER_MASK) {
            const auto mask = read_value<
                progpu_native_scene_layer_vector_mask>(
                    stream,
                    resource.payload_offset);
            if (mask.kind !=
                    PROGPU_NATIVE_SCENE_LAYER_MASK_VECTOR_CLIP_CHAIN ||
                mask.path_count != 1U || mask.segment_count != 16U ||
                mask.boolean_node_count != 7U) {
                continue;
            }
            const auto path = read_value<progpu_native_scene_clip_path>(
                stream,
                resource.auxiliary_offset);
            PROGPU_REQUIRE(path.segment_count == 16U);
            PROGPU_REQUIRE(path.boolean_node_count == 7U);
            PROGPU_REQUIRE(require_program(
                resource.auxiliary_offset +
                sizeof(progpu_native_scene_clip_path) +
                16U * sizeof(progpu_native_path_segment)));
            found_clip = true;
        }
    }
    PROGPU_REQUIRE(found_fill);
    PROGPU_REQUIRE(found_clip);

    std::vector<std::byte> nonzero_update;
    append_geometry_group(nonzero_update, group, 0U, 1U, children);
    PROGPU_REQUIRE(state.apply(nonzero_update) == status::success);
    PROGPU_REQUIRE(
        state.build_scene(target, 7004U, 2U, stream) == status::success);
    const auto nonzero_header =
        read_value<progpu_native_scene_header>(stream, 0U);
    bool found_nonzero_fill = false;
    bool found_nonzero_clip = false;
    const auto require_nonzero_program = [&](std::size_t node_offset) {
        std::array<progpu_native_scene_path_boolean_node, 7U> program{};
        for (std::size_t index = 0U; index < program.size(); ++index) {
            program[index] = read_value<
                progpu_native_scene_path_boolean_node>(
                    stream,
                    node_offset + index * sizeof(program[index]));
        }
        PROGPU_REQUIRE(
            program[0].kind == PROGPU_NATIVE_PATH_BOOLEAN_LEAF);
        PROGPU_REQUIRE(
            program[1].kind == PROGPU_NATIVE_PATH_BOOLEAN_LEAF);
        PROGPU_REQUIRE(
            program[2].kind == PROGPU_NATIVE_PATH_BOOLEAN_DIFFERENCE);
        PROGPU_REQUIRE(
            program[3].kind == PROGPU_NATIVE_PATH_BOOLEAN_WINDING_LEAF);
        PROGPU_REQUIRE(
            program[4].kind == PROGPU_NATIVE_PATH_BOOLEAN_WINDING_ADD);
        PROGPU_REQUIRE(
            program[5].kind == PROGPU_NATIVE_PATH_BOOLEAN_WINDING_LEAF);
        PROGPU_REQUIRE(
            program[6].kind == PROGPU_NATIVE_PATH_BOOLEAN_WINDING_ADD);
        return true;
    };
    for (std::uint32_t index = 0U;
         index < nonzero_header.resource_count;
         ++index) {
        const auto resource = read_value<progpu_native_scene_resource>(
            stream,
            nonzero_header.resource_offset +
                index * sizeof(progpu_native_scene_resource));
        if (resource.kind == PROGPU_NATIVE_SCENE_RESOURCE_PATH_BATCH) {
            const auto path = read_value<progpu_native_scene_path_fill>(
                stream,
                resource.payload_offset);
            if (path.segment_count == 16U &&
                path.boolean_node_count == 7U &&
                path.fill_rule == PROGPU_NATIVE_FILL_RULE_NON_ZERO) {
                PROGPU_REQUIRE(require_nonzero_program(
                    resource.auxiliary_offset +
                    16U * sizeof(progpu_native_path_segment)));
                found_nonzero_fill = true;
            }
        } else if (resource.kind ==
            PROGPU_NATIVE_SCENE_RESOURCE_LAYER_MASK) {
            const auto mask = read_value<
                progpu_native_scene_layer_vector_mask>(
                    stream,
                    resource.payload_offset);
            if (mask.kind !=
                    PROGPU_NATIVE_SCENE_LAYER_MASK_VECTOR_CLIP_CHAIN ||
                mask.path_count != 1U || mask.segment_count != 16U ||
                mask.boolean_node_count != 7U) {
                continue;
            }
            const auto path = read_value<progpu_native_scene_clip_path>(
                stream,
                resource.auxiliary_offset);
            PROGPU_REQUIRE(
                path.fill_rule == PROGPU_NATIVE_FILL_RULE_NON_ZERO);
            PROGPU_REQUIRE(require_nonzero_program(
                resource.auxiliary_offset +
                sizeof(progpu_native_scene_clip_path) +
                16U * sizeof(progpu_native_path_segment)));
            found_nonzero_clip = true;
        }
    }
    PROGPU_REQUIRE(found_nonzero_fill);
    PROGPU_REQUIRE(found_nonzero_clip);

    std::vector<std::byte> reflected_update;
    const std::array reflected_children{combined};
    append_geometry_group(
        reflected_update,
        reflected_group,
        reflection,
        0U,
        reflected_children);
    const std::array reflected_root_children{
        reflected_group,
        rectangle_c};
    append_geometry_group(
        reflected_update,
        group,
        0U,
        1U,
        reflected_root_children);
    PROGPU_REQUIRE(state.apply(reflected_update) == status::success);
    PROGPU_REQUIRE(
        state.build_scene(target, 7004U, 3U, stream) == status::success);
    const auto reflected_header =
        read_value<progpu_native_scene_header>(stream, 0U);
    bool found_reflected_fill = false;
    bool found_reflected_clip = false;
    const auto require_reflected_program = [&](std::size_t node_offset) {
        std::array<progpu_native_scene_path_boolean_node, 6U> program{};
        for (std::size_t index = 0U; index < program.size(); ++index) {
            program[index] = read_value<
                progpu_native_scene_path_boolean_node>(
                    stream,
                    node_offset + index * sizeof(program[index]));
        }
        PROGPU_REQUIRE(
            program[0].kind == PROGPU_NATIVE_PATH_BOOLEAN_LEAF);
        PROGPU_REQUIRE(
            program[1].kind == PROGPU_NATIVE_PATH_BOOLEAN_LEAF);
        PROGPU_REQUIRE(
            program[2].kind == PROGPU_NATIVE_PATH_BOOLEAN_DIFFERENCE);
        PROGPU_REQUIRE(
            program[3].kind == PROGPU_NATIVE_PATH_BOOLEAN_WINDING_NEGATE);
        PROGPU_REQUIRE(
            program[4].kind == PROGPU_NATIVE_PATH_BOOLEAN_WINDING_LEAF);
        PROGPU_REQUIRE(
            program[5].kind == PROGPU_NATIVE_PATH_BOOLEAN_WINDING_ADD);
        return true;
    };
    for (std::uint32_t index = 0U;
         index < reflected_header.resource_count;
         ++index) {
        const auto resource = read_value<progpu_native_scene_resource>(
            stream,
            reflected_header.resource_offset +
                index * sizeof(progpu_native_scene_resource));
        if (resource.kind == PROGPU_NATIVE_SCENE_RESOURCE_PATH_BATCH) {
            const auto path = read_value<progpu_native_scene_path_fill>(
                stream,
                resource.payload_offset);
            if (path.segment_count == 12U &&
                path.boolean_node_count == 6U) {
                PROGPU_REQUIRE(require_reflected_program(
                    resource.auxiliary_offset +
                    12U * sizeof(progpu_native_path_segment)));
                found_reflected_fill = true;
            }
        } else if (resource.kind ==
            PROGPU_NATIVE_SCENE_RESOURCE_LAYER_MASK) {
            const auto mask = read_value<
                progpu_native_scene_layer_vector_mask>(
                    stream,
                    resource.payload_offset);
            if (mask.kind !=
                    PROGPU_NATIVE_SCENE_LAYER_MASK_VECTOR_CLIP_CHAIN ||
                mask.path_count != 1U || mask.segment_count != 12U ||
                mask.boolean_node_count != 6U) {
                continue;
            }
            PROGPU_REQUIRE(require_reflected_program(
                resource.auxiliary_offset +
                sizeof(progpu_native_scene_clip_path) +
                12U * sizeof(progpu_native_path_segment)));
            found_reflected_clip = true;
        }
    }
    PROGPU_REQUIRE(found_reflected_fill);
    PROGPU_REQUIRE(found_reflected_clip);
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
        status::invalid_handle);

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

    std::vector<std::byte> effect_batch;
    std::vector<std::byte> effect;
    append_command(effect, command::push_effect, 0xfeedU, 0xbeefU);
    append_command(effect, command::pop);
    append_render_data(effect_batch, content, effect);
    PROGPU_REQUIRE(state.apply(effect_batch) == status::success);
    PROGPU_REQUIRE(
        state.build_scene(target, 1U, 7U, stream) == status::success);

    std::vector<std::byte> unbalanced_effect_batch;
    std::vector<std::byte> unbalanced_effect;
    append_command(
        unbalanced_effect,
        command::push_effect,
        0xfeedU,
        0xbeefU);
    append_render_data(
        unbalanced_effect_batch,
        content,
        unbalanced_effect);
    PROGPU_REQUIRE(state.apply(unbalanced_effect_batch) == status::success);
    PROGPU_REQUIRE(
        state.build_scene(target, 1U, 8U, stream) == status::invalid_graph);

    std::vector<std::byte> obsolete_effect_batch;
    std::vector<std::byte> obsolete_effect;
    append_command(obsolete_effect, command::push_effect);
    append_render_data(
        obsolete_effect_batch,
        content,
        obsolete_effect);
    PROGPU_REQUIRE(state.apply(obsolete_effect_batch) == status::success);
    PROGPU_REQUIRE(
        state.build_scene(target, 1U, 9U, stream) ==
        status::malformed_batch);

    std::uint64_t frame_id = 10U;
    const auto expect_nested_status = [&state,
                                       &stream,
                                       &frame_id](
        const std::vector<std::byte>& commands,
        status expected) {
        std::vector<std::byte> update;
        append_render_data(update, content, commands);
        if (state.apply(update) != status::success) {
            return false;
        }
        return state.build_scene(
            target,
            1U,
            frame_id++,
            stream) == expected;
    };

    std::vector<std::byte> guideline_y1;
    append_command(guideline_y1, command::push_guideline_y1, 1.0);
    PROGPU_REQUIRE(expect_nested_status(
        guideline_y1,
        status::unsupported_command));
    std::vector<std::byte> short_guideline_y1;
    append_command(short_guideline_y1, command::push_guideline_y1);
    PROGPU_REQUIRE(expect_nested_status(
        short_guideline_y1,
        status::malformed_batch));

    std::vector<std::byte> guideline_y2;
    append_command(guideline_y2, command::push_guideline_y2, 1.0, 2.0);
    PROGPU_REQUIRE(expect_nested_status(
        guideline_y2,
        status::unsupported_command));
    std::vector<std::byte> short_guideline_y2;
    append_command(short_guideline_y2, command::push_guideline_y2, 1.0);
    PROGPU_REQUIRE(expect_nested_status(
        short_guideline_y2,
        status::malformed_batch));

    std::vector<std::byte> video;
    append_command(
        video,
        command::draw_video,
        0.0,
        0.0,
        1.0,
        1.0,
        0U,
        0U);
    PROGPU_REQUIRE(expect_nested_status(
        video,
        status::invalid_handle));
    std::vector<std::byte> short_video;
    append_command(short_video, command::draw_video, 0.0);
    PROGPU_REQUIRE(expect_nested_status(
        short_video,
        status::malformed_batch));

    std::vector<std::byte> animated_video;
    append_command(
        animated_video,
        command::draw_video_animate,
        0.0,
        0.0,
        1.0,
        1.0,
        0U,
        0U);
    PROGPU_REQUIRE(expect_nested_status(
        animated_video,
        status::invalid_handle));
    std::vector<std::byte> short_animated_video;
    append_command(
        short_animated_video,
        command::draw_video_animate,
        0.0);
    PROGPU_REQUIRE(expect_nested_status(
        short_animated_video,
        status::malformed_batch));
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

bool degenerate_gradient_pen_caps_use_wpf_stroke_bounds() {
    constexpr std::uint32_t visual = 800U;
    constexpr std::uint32_t content = 801U;
    constexpr std::uint32_t target = 802U;
    constexpr std::uint32_t linear = 803U;
    constexpr std::uint32_t pen = 804U;
    const std::array stops{
        mil_gradient_stop{0.0, {1.0F, 0.0F, 0.0F, 1.0F}},
        mil_gradient_stop{1.0, {0.0F, 0.0F, 1.0F, 1.0F}}};

    std::vector<std::byte> batch;
    append_create(batch, visual, 39U);
    append_create(batch, content, 43U);
    append_create(batch, target, 47U);
    append_create(batch, linear, 77U);
    append_create(batch, pen, 85U);
    append_command(batch, command::visual_create, visual);
    append_command(batch, command::visual_set_content, visual, content);
    append_linear_gradient_brush(
        batch,
        linear,
        1.0,
        0.0,
        0.0,
        1.0,
        0.0,
        0U,
        0U,
        0U,
        1U,
        1U,
        0U,
        0U,
        0U,
        stops);
    append_command(
        batch,
        command::pen,
        pen,
        4.0,
        10.0,
        linear,
        0U,
        PROGPU_NATIVE_STROKE_CAP_FLAT,
        PROGPU_NATIVE_STROKE_CAP_TRIANGLE,
        PROGPU_NATIVE_STROKE_CAP_FLAT,
        0U,
        0U);
    std::vector<std::byte> nested;
    append_command(
        nested,
        command::draw_line,
        10.0,
        20.0,
        10.0,
        20.0,
        pen,
        0U);
    append_command(
        nested,
        command::draw_ellipse,
        30.0,
        20.0,
        0.0,
        0.0,
        0U,
        pen);
    append_render_data(batch, content, nested);
    append_command(
        batch,
        command::generic_target_create,
        target,
        std::uint64_t{0U},
        std::uint64_t{0U},
        64U,
        48U,
        0U);
    append_command(batch, command::target_set_root, target, visual);

    channel state;
    PROGPU_REQUIRE(state.apply(batch) == status::success);
    std::vector<std::byte> stream;
    progpu::native::mil::scene_metrics metrics{};
    PROGPU_REQUIRE(
        state.build_scene(target, 8102U, 1U, stream, &metrics) ==
        status::success);
    PROGPU_REQUIRE(metrics.line_count == 1U);
    PROGPU_REQUIRE(metrics.ellipse_count == 1U);
    PROGPU_REQUIRE(metrics.brush_count == 2U);

    const auto header = read_value<progpu_native_scene_header>(stream, 0U);
    bool found_brushes = false;
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
        const auto line_brush = read_value<progpu_native_scene_brush>(
            stream, resource.payload_offset);
        const auto ellipse_brush = read_value<progpu_native_scene_brush>(
            stream,
            resource.payload_offset + sizeof(progpu_native_scene_brush));
        PROGPU_REQUIRE(line_brush.type ==
            PROGPU_NATIVE_SCENE_BRUSH_LINEAR_GRADIENT);
        PROGPU_REQUIRE(line_brush.start_point.x == 10.0F);
        PROGPU_REQUIRE(line_brush.start_point.y == 18.0F);
        PROGPU_REQUIRE(line_brush.end_point.x == 12.0F);
        PROGPU_REQUIRE(line_brush.end_point.y == 18.0F);
        PROGPU_REQUIRE(ellipse_brush.type ==
            PROGPU_NATIVE_SCENE_BRUSH_LINEAR_GRADIENT);
        PROGPU_REQUIRE(ellipse_brush.start_point.x == 28.0F);
        PROGPU_REQUIRE(ellipse_brush.start_point.y == 18.0F);
        PROGPU_REQUIRE(ellipse_brush.end_point.x == 32.0F);
        PROGPU_REQUIRE(ellipse_brush.end_point.y == 18.0F);
        found_brushes = true;
    }
    PROGPU_REQUIRE(found_brushes);

    std::array<progpu_native_image_rect, 2U> draw_bounds{};
    std::size_t draw_count = 0U;
    for (std::uint32_t index = 0U; index < header.command_count; ++index) {
        const auto record = read_value<progpu_native_scene_command>(
            stream,
            header.command_offset +
                index * sizeof(progpu_native_scene_command));
        if (record.kind != PROGPU_NATIVE_SCENE_COMMAND_DRAW_GEOMETRY) {
            continue;
        }
        PROGPU_REQUIRE(draw_count < draw_bounds.size());
        draw_bounds[draw_count++] = {
            record.bounds_x,
            record.bounds_y,
            record.bounds_width,
            record.bounds_height};
    }
    PROGPU_REQUIRE(draw_count == 2U);
    PROGPU_REQUIRE(draw_bounds[0].x == 10.0F);
    PROGPU_REQUIRE(draw_bounds[0].y == 18.0F);
    PROGPU_REQUIRE(draw_bounds[0].width == 2.0F);
    PROGPU_REQUIRE(draw_bounds[0].height == 4.0F);
    PROGPU_REQUIRE(draw_bounds[1].x == 28.0F);
    PROGPU_REQUIRE(draw_bounds[1].y == 18.0F);
    PROGPU_REQUIRE(draw_bounds[1].width == 4.0F);
    PROGPU_REQUIRE(draw_bounds[1].height == 4.0F);
    return true;
}

bool retained_gradient_stops_match_wpf_coincidence_and_pad_edges() {
    constexpr std::uint32_t visual = 820U;
    constexpr std::uint32_t content = 821U;
    constexpr std::uint32_t target = 822U;
    constexpr std::uint32_t linear = 823U;
    constexpr float epsilon = std::numeric_limits<float>::epsilon();
    constexpr float coincident_base = 0.5F;
    constexpr float separate_base = 0.75F;
    constexpr float coincident_one = coincident_base + 2.0F * epsilon;
    constexpr float coincident_two = coincident_base + 4.0F * epsilon;
    constexpr float separate = separate_base + 10.0F * epsilon;

    const progpu_native_color red{1.0F, 0.0F, 0.0F, 1.0F};
    const progpu_native_color green{0.0F, 1.0F, 0.0F, 1.0F};
    const progpu_native_color blue{0.0F, 0.0F, 1.0F, 1.0F};
    const progpu_native_color yellow{1.0F, 1.0F, 0.0F, 1.0F};
    const progpu_native_color magenta{1.0F, 0.0F, 1.0F, 1.0F};
    const progpu_native_color orange{1.0F, 0.5F, 0.0F, 1.0F};
    const progpu_native_color purple{0.5F, 0.0F, 1.0F, 1.0F};
    const progpu_native_color cyan{0.0F, 1.0F, 1.0F, 1.0F};
    const progpu_native_color white{1.0F, 1.0F, 1.0F, 1.0F};
    const std::array source_stops{
        // Deliberately unsorted. The stable WPF sort must retain the declared
        // order inside each endpoint/coincident group.
        mil_gradient_stop{1.0, cyan},
        mil_gradient_stop{coincident_one, yellow},
        mil_gradient_stop{0.0, red},
        mil_gradient_stop{separate, purple},
        mil_gradient_stop{coincident_base, blue},
        mil_gradient_stop{1.0, white},
        mil_gradient_stop{coincident_two, magenta},
        mil_gradient_stop{separate_base, orange},
        mil_gradient_stop{0.0, green}};

    std::vector<std::byte> batch;
    append_create(batch, visual, 39U);
    append_create(batch, content, 43U);
    append_create(batch, target, 47U);
    append_create(batch, linear, 77U);
    append_command(batch, command::visual_create, visual);
    append_command(batch, command::visual_set_content, visual, content);
    append_linear_gradient_brush(
        batch,
        linear,
        1.0,
        0.0,
        0.0,
        1.0,
        0.0,
        0U,
        0U,
        0U,
        1U,
        0U,
        0U,
        0U,
        0U,
        source_stops);
    std::vector<std::byte> nested;
    append_command(
        nested,
        command::draw_rectangle,
        0.0,
        0.0,
        100.0,
        20.0,
        linear,
        0U);
    append_render_data(batch, content, nested);
    append_command(
        batch,
        command::generic_target_create,
        target,
        std::uint64_t{0U},
        std::uint64_t{0U},
        100U,
        20U,
        0U);
    append_command(batch, command::target_set_root, target, visual);

    channel state;
    PROGPU_REQUIRE(state.apply(batch) == status::success);
    std::vector<std::byte> stream;
    PROGPU_REQUIRE(
        state.build_scene(target, 8101U, 1U, stream) == status::success);
    const auto header = read_value<progpu_native_scene_header>(stream, 0U);
    bool found = false;
    for (std::uint32_t index = 0U; index < header.resource_count; ++index) {
        const auto resource = read_value<progpu_native_scene_resource>(
            stream,
            header.resource_offset +
                index * sizeof(progpu_native_scene_resource));
        if (resource.kind != PROGPU_NATIVE_SCENE_RESOURCE_BRUSH_TABLE) {
            continue;
        }
        const auto brush = read_value<progpu_native_scene_brush>(
            stream, resource.payload_offset);
        PROGPU_REQUIRE(brush.stop_count == 6U);
        PROGPU_REQUIRE((brush.spread_method &
            PROGPU_NATIVE_SCENE_GRADIENT_SPREAD_MASK) ==
            PROGPU_NATIVE_SCENE_GRADIENT_PAD);
        PROGPU_REQUIRE((brush.spread_method &
            PROGPU_NATIVE_SCENE_GRADIENT_PAD_OUTSIDE_COLORS) != 0U);
        PROGPU_REQUIRE(brush.colors[0].r > 0.99F &&
            brush.colors[0].g == 0.0F && brush.colors[0].b == 0.0F);
        PROGPU_REQUIRE(brush.colors[1].r > 0.99F &&
            brush.colors[1].g > 0.99F &&
            brush.colors[1].b > 0.99F);
        std::array<progpu_native_scene_gradient_stop, 6U> stops{};
        for (std::size_t stop_index = 0U;
             stop_index < stops.size();
             ++stop_index) {
            stops[stop_index] = read_value<
                progpu_native_scene_gradient_stop>(
                stream,
                resource.auxiliary_offset +
                    (brush.stop_offset + stop_index) *
                        sizeof(progpu_native_scene_gradient_stop));
        }
        PROGPU_REQUIRE(stops[0].offset == 0.0F &&
            stops[0].color.g > 0.99F);
        PROGPU_REQUIRE(stops[1].offset == coincident_base &&
            stops[1].color.b > 0.99F);
        PROGPU_REQUIRE(stops[2].offset == coincident_base &&
            stops[2].color.r > 0.99F &&
            stops[2].color.b > 0.99F);
        PROGPU_REQUIRE(stops[3].offset == separate_base &&
            stops[3].color.g > 0.7F && stops[3].color.g < 0.8F);
        PROGPU_REQUIRE(stops[4].offset == separate &&
            stops[4].offset > stops[3].offset &&
            stops[4].color.r > 0.7F && stops[4].color.r < 0.8F);
        PROGPU_REQUIRE(stops[5].offset == 1.0F &&
            stops[5].color.g > 0.99F &&
            stops[5].color.b > 0.99F);
        found = true;
    }
    PROGPU_REQUIRE(found);
    return true;
}

bool retained_viewport3d_uses_pointer_free_mesh_sideband() {
    constexpr std::uint32_t viewport_handle = 920U;
    constexpr std::uint32_t target = 921U;
    std::vector<std::byte> batch;
    append_create(batch, viewport_handle, 40U);
    append_create(batch, target, 47U);
    append_command(batch, command::visual_create, viewport_handle);
    append_command(
        batch,
        command::generic_target_create,
        target,
        std::uint64_t{0U},
        std::uint64_t{0U},
        160U,
        120U,
        0U);
    append_command(batch, command::target_set_root, target, viewport_handle);

    channel state;
    PROGPU_REQUIRE(state.apply(batch) == status::success);
    std::vector<std::byte> stream;
    PROGPU_REQUIRE(
        state.build_scene(target, 8200U, 1U, stream) ==
        status::unsupported_command);

    const progpu_native_matrix_4x4 identity{
        1.0F, 0.0F, 0.0F, 0.0F,
        0.0F, 1.0F, 0.0F, 0.0F,
        0.0F, 0.0F, 1.0F, 0.0F,
        0.0F, 0.0F, 0.0F, 1.0F};
    progpu_native_scene_camera_3d camera{};
    camera.struct_size = sizeof(camera);
    camera.projection = identity;
    camera.view = identity;
    camera.camera_position = {0.0F, 0.0F, 2.0F, 0.0F};
    std::array<progpu_native_scene_mesh_3d_vertex, 3U> vertices{};
    vertices[0].position = {-0.8F, -0.8F, 0.0F, 0.0F};
    vertices[1].position = {0.8F, -0.8F, 0.0F, 0.0F};
    vertices[2].position = {0.0F, 0.8F, 0.0F, 0.0F};
    vertices[0].texture_coordinate = {0.0F, 1.0F};
    vertices[1].texture_coordinate = {1.0F, 1.0F};
    vertices[2].texture_coordinate = {0.5F, 0.0F};
    for (auto& vertex : vertices) {
        vertex.normal = {0.0F, 0.0F, 1.0F, 0.0F};
    }
    const std::array<std::uint32_t, 3U> indices{0U, 1U, 2U};
    progpu_native_scene_mesh_3d mesh{};
    mesh.struct_size = sizeof(mesh);
    mesh.topology = PROGPU_NATIVE_MESH_3D_TRIANGLES;
    mesh.render_mode = PROGPU_NATIVE_MESH_3D_SOLID;
    mesh.vertex_count = static_cast<std::uint32_t>(vertices.size());
    mesh.index_count = static_cast<std::uint32_t>(indices.size());
    mesh.model_transform = identity;
    mesh.normal_transform = identity;
    mesh.color = {0.25F, 0.5F, 0.75F, 1.0F};
    mesh.light_direction = {0.0F, 0.0F, -1.0F, 1.0F};
    mesh.ambient_color = {0.2F, 0.2F, 0.2F, 1.0F};
    mesh.specular_color = {0.1F, 0.1F, 0.1F, 1.0F};
    mesh.material_ambient = {1.0F, 1.0F, 1.0F, 1.0F};
    mesh.opacity = 1.0F;
    std::array<progpu_native_scene_light_3d, 2U> lights{};
    lights[0].struct_size = sizeof(lights[0]);
    lights[0].kind = PROGPU_NATIVE_LIGHT_3D_AMBIENT;
    lights[0].color = {0.1F, 0.2F, 0.3F, 1.0F};
    lights[1].struct_size = sizeof(lights[1]);
    lights[1].kind = PROGPU_NATIVE_LIGHT_3D_POINT;
    lights[1].color = {1.0F, 0.7F, 0.4F, 1.0F};
    lights[1].position_range = {0.0F, 0.0F, 2.0F, 20.0F};
    lights[1].attenuation_outer_cos = {1.0F, 0.1F, 0.01F, 0.0F};
    mesh.light_count = static_cast<std::uint32_t>(lights.size());
    const progpu_native_image_rect viewport{12.0F, 18.0F, 80.0F, 60.0F};
    PROGPU_REQUIRE(
        state.set_viewport3d_scene(
            viewport_handle,
            camera,
            viewport,
            std::span<const progpu_native_scene_mesh_3d>{&mesh, 1U},
            vertices,
            indices,
            lights) == status::success);
    PROGPU_REQUIRE(
        state.set_viewport3d_scene(
            target,
            camera,
            viewport,
            std::span<const progpu_native_scene_mesh_3d>{&mesh, 1U},
            vertices,
            indices,
            lights) == status::invalid_handle);
    PROGPU_REQUIRE(
        state.build_scene(target, 8200U, 2U, stream) == status::success);

    const auto header = read_value<progpu_native_scene_header>(stream, 0U);
    bool found_mesh = false;
    bool found_draw = false;
    for (std::uint32_t index = 0U; index < header.resource_count; ++index) {
        const auto resource = read_value<progpu_native_scene_resource>(
            stream,
            header.resource_offset +
                index * sizeof(progpu_native_scene_resource));
        if (resource.kind == PROGPU_NATIVE_SCENE_RESOURCE_MESH_3D_BATCH) {
            PROGPU_REQUIRE(resource.payload_size == sizeof(mesh));
            PROGPU_REQUIRE(
                resource.auxiliary_size ==
                sizeof(vertices) + sizeof(indices) + sizeof(lights));
            found_mesh = true;
        }
    }
    for (std::uint32_t index = 0U; index < header.command_count; ++index) {
        const auto scene_command = read_value<progpu_native_scene_command>(
            stream,
            header.command_offset +
                index * sizeof(progpu_native_scene_command));
        if (scene_command.kind ==
            PROGPU_NATIVE_SCENE_COMMAND_DRAW_MESH_3D_BATCH) {
            PROGPU_REQUIRE(scene_command.bounds_x == viewport.x);
            PROGPU_REQUIRE(scene_command.bounds_y == viewport.y);
            PROGPU_REQUIRE(scene_command.bounds_width == viewport.width);
            PROGPU_REQUIRE(scene_command.bounds_height == viewport.height);
            const auto retained_camera =
                read_value<progpu_native_scene_camera_3d>(
                    stream, scene_command.payload_offset);
            PROGPU_REQUIRE(retained_camera.camera_position.z == 2.0F);
            found_draw = true;
        }
    }
    PROGPU_REQUIRE(found_mesh);
    PROGPU_REQUIRE(found_draw);

    progpu_native_scene_brush material{};
    material.type = PROGPU_NATIVE_SCENE_BRUSH_LINEAR_GRADIENT;
    material.opacity = 1.0F;
    material.start_point = {0.0F, 0.0F};
    material.end_point = {1.0F, 0.0F};
    material.stop_count = 2U;
    material.coordinate_transform0[0] = 1.0F;
    material.coordinate_transform1[1] = 1.0F;
    const std::array<progpu_native_scene_gradient_stop, 2U> stops{{
        {{1.0F, 0.0F, 0.0F, 1.0F}, 0.0F, 0U, 0U, 0U},
        {{0.0F, 0.0F, 1.0F, 1.0F}, 1.0F, 0U, 0U, 0U}}};
    auto invalid_material = material;
    invalid_material.stop_count = 3U;
    PROGPU_REQUIRE(
        state.set_viewport3d_scene(
            viewport_handle,
            camera,
            viewport,
            std::span<const progpu_native_scene_mesh_3d>{&mesh, 1U},
            vertices,
            indices,
            lights,
            std::span<const progpu_native_scene_brush>{
                &invalid_material, 1U},
            stops) == status::invalid_argument);
    PROGPU_REQUIRE(
        state.set_viewport3d_scene(
            viewport_handle,
            camera,
            viewport,
            std::span<const progpu_native_scene_mesh_3d>{&mesh, 1U},
            vertices,
            indices,
            lights,
            std::span<const progpu_native_scene_brush>{&material, 1U},
            stops) == status::success);
    PROGPU_REQUIRE(
        state.build_scene(target, 8200U, 3U, stream) == status::success);
    const auto material_header =
        read_value<progpu_native_scene_header>(stream, 0U);
    bool found_material_table = false;
    bool found_material_draw = false;
    for (std::uint32_t index = 0U;
         index < material_header.resource_count;
         ++index) {
        const auto resource = read_value<progpu_native_scene_resource>(
            stream,
            material_header.resource_offset +
                index * sizeof(progpu_native_scene_resource));
        if (resource.kind == PROGPU_NATIVE_SCENE_RESOURCE_BRUSH_TABLE) {
            PROGPU_REQUIRE(resource.payload_size == sizeof(material));
            PROGPU_REQUIRE(resource.auxiliary_size == sizeof(stops));
            const auto retained_material =
                read_value<progpu_native_scene_brush>(
                    stream, resource.payload_offset);
            PROGPU_REQUIRE(retained_material.type ==
                PROGPU_NATIVE_SCENE_BRUSH_LINEAR_GRADIENT);
            found_material_table = true;
        }
    }
    for (std::uint32_t index = 0U;
         index < material_header.command_count;
         ++index) {
        const auto scene_command = read_value<progpu_native_scene_command>(
            stream,
            material_header.command_offset +
                index * sizeof(progpu_native_scene_command));
        if (scene_command.kind !=
            PROGPU_NATIVE_SCENE_COMMAND_DRAW_MESH_3D_BATCH) {
            continue;
        }
        PROGPU_REQUIRE(scene_command.payload_size == sizeof(camera) +
            sizeof(progpu_native_scene_mesh_3d_materials) +
            sizeof(std::uint32_t));
        const auto mapping =
            read_value<progpu_native_scene_mesh_3d_materials>(
                stream, scene_command.payload_offset + sizeof(camera));
        PROGPU_REQUIRE(mapping.struct_size == sizeof(mapping));
        PROGPU_REQUIRE(mapping.brush_count == 1U);
        const auto brush_index = read_value<std::uint32_t>(
            stream,
            scene_command.payload_offset + sizeof(camera) + sizeof(mapping));
        PROGPU_REQUIRE(brush_index == 0U);
        found_material_draw = true;
    }
    PROGPU_REQUIRE(found_material_table);
    PROGPU_REQUIRE(found_material_draw);
    // Rebinding equal wire values must preserve identity and the compiled scene.
    // Use distinct arrays, then mutate each family (including aliased producers).
    auto current_camera = camera;
    auto current_viewport = viewport;
    auto current_mesh = mesh;
    auto current_vertices = vertices;
    auto current_indices = indices;
    auto current_lights = lights;
    auto current_material = material;
    auto current_stops = stops;
    const auto bind = [&]() {
        return state.set_viewport3d_scene(
            viewport_handle, current_camera, current_viewport,
            std::span<const progpu_native_scene_mesh_3d>{&current_mesh, 1U},
            current_vertices, current_indices, current_lights,
            std::span<const progpu_native_scene_brush>{&current_material, 1U},
            current_stops);
    };
    auto generation = state.resource_generation(viewport_handle);
    PROGPU_REQUIRE(bind() == status::success);
    PROGPU_REQUIRE(state.resource_generation(viewport_handle) == generation);
    std::vector<std::byte> unchanged_stream;
    PROGPU_REQUIRE(state.build_scene(target, 8200U, 3U, unchanged_stream) ==
        status::success);
    PROGPU_REQUIRE(unchanged_stream == stream);
    const auto changed_once = [&]() {
        if (bind() != status::success ||
            state.resource_generation(viewport_handle) != ++generation) {
            return false;
        }
        return bind() == status::success &&
            state.resource_generation(viewport_handle) == generation;
    };
    current_camera.camera_position.z += 1.0F;
    PROGPU_REQUIRE(changed_once());
    current_viewport.x += 1.0F;
    PROGPU_REQUIRE(changed_once());
    current_mesh.opacity = 0.75F;
    PROGPU_REQUIRE(changed_once());
    current_vertices[0].position.x += 0.1F;
    PROGPU_REQUIRE(changed_once());
    std::swap(current_indices[0], current_indices[1]);
    PROGPU_REQUIRE(changed_once());
    current_lights[0].color.r = 0.4F;
    PROGPU_REQUIRE(changed_once());
    current_material.opacity = 0.5F;
    PROGPU_REQUIRE(changed_once());
    current_stops[0].color.r = 0.5F;
    PROGPU_REQUIRE(changed_once());
    current_vertices[0].reserved0 = 1U;
    PROGPU_REQUIRE(bind() == status::invalid_argument);
    PROGPU_REQUIRE(state.resource_generation(viewport_handle) == generation);
    current_vertices[0].reserved0 = 0U;
    current_indices[0] = 99U;
    PROGPU_REQUIRE(bind() == status::invalid_argument);
    PROGPU_REQUIRE(state.resource_generation(viewport_handle) == generation);
    current_indices = indices;
    std::swap(current_indices[0], current_indices[1]);
    PROGPU_REQUIRE(bind() == status::success);
    PROGPU_REQUIRE(state.resource_generation(viewport_handle) == generation);
    return true;
}

bool canonical_viewport3d_camera_uses_wpf_transform_resources() {
    constexpr std::uint32_t viewport_handle = 940U;
    constexpr std::uint32_t target = 941U;
    constexpr std::uint32_t angle_animation = 942U;
    constexpr std::uint32_t x_animation = 943U;
    constexpr std::uint32_t rotation = 944U;
    constexpr std::uint32_t rotate = 945U;
    constexpr std::uint32_t translate = 946U;
    constexpr std::uint32_t group = 947U;
    constexpr std::uint32_t camera_handle = 948U;
    constexpr std::uint32_t quaternion_rotation = 949U;
    constexpr std::uint32_t quaternion_rotate = 950U;
    constexpr std::uint32_t scale = 951U;
    constexpr std::uint32_t matrix_transform = 952U;
    constexpr std::uint32_t orthographic_camera = 953U;
    constexpr std::uint32_t matrix_camera = 954U;
    const std::array<float, 3U> z_axis{0.0F, 0.0F, 1.0F};
    const std::array<float, 3U> position{0.0F, 0.0F, 2.0F};
    const std::array<float, 3U> look{0.0F, 0.0F, -1.0F};
    const std::array<float, 3U> up{0.0F, 1.0F, 0.0F};
    const std::array<std::uint32_t, 2U> children{rotate, translate};

    std::vector<std::byte> batch;
    append_create(batch, viewport_handle, 40U);
    append_create(batch, target, 47U);
    append_create(batch, angle_animation, 49U);
    append_create(batch, x_animation, 49U);
    append_create(batch, rotation, 3U);
    append_create(batch, rotate, 31U);
    append_create(batch, translate, 29U);
    append_create(batch, group, 27U);
    append_create(batch, camera_handle, 7U);
    append_create(batch, quaternion_rotation, 4U);
    append_create(batch, quaternion_rotate, 31U);
    append_create(batch, scale, 30U);
    append_create(batch, matrix_transform, 32U);
    append_create(batch, orthographic_camera, 8U);
    append_create(batch, matrix_camera, 9U);
    append_command(batch, command::visual_create, viewport_handle);
    append_command(batch, command::double_resource, angle_animation, 90.0);
    append_command(batch, command::double_resource, x_animation, 1.0);
    append_command(
        batch,
        command::axis_angle_rotation3d,
        rotation,
        std::numeric_limits<double>::quiet_NaN(),
        z_axis,
        0U,
        angle_animation);
    append_command(
        batch,
        command::rotate_transform3d,
        rotate,
        0.0,
        0.0,
        0.0,
        0U,
        0U,
        0U,
        rotation);
    append_command(
        batch,
        command::translate_transform3d,
        translate,
        std::numeric_limits<double>::quiet_NaN(),
        2.0,
        3.0,
        x_animation,
        0U,
        0U);
    append_command(
        batch,
        command::transform3d_group,
        group,
        static_cast<std::uint32_t>(sizeof(children)),
        children);
    append_command(
        batch,
        command::perspective_camera,
        camera_handle,
        1.0,
        11.0,
        90.0,
        position,
        group,
        look,
        0U,
        up,
        0U,
        0U,
        0U,
        0U,
        0U);
    constexpr float half_root_two = 0.7071067811865475244F;
    append_command(
        batch,
        command::quaternion_rotation3d,
        quaternion_rotation,
        std::array<float, 4U>{
            0.0F, 0.0F, half_root_two, half_root_two},
        0U);
    append_command(
        batch,
        command::rotate_transform3d,
        quaternion_rotate,
        0.0,
        0.0,
        0.0,
        0U,
        0U,
        0U,
        quaternion_rotation);
    append_command(
        batch,
        command::scale_transform3d,
        scale,
        2.0,
        3.0,
        4.0,
        0.0,
        0.0,
        0.0,
        0U,
        0U,
        0U,
        0U,
        0U,
        0U);
    const progpu_native_matrix_4x4 translated{
        1.0F, 0.0F, 0.0F, 0.0F,
        0.0F, 1.0F, 0.0F, 0.0F,
        0.0F, 0.0F, 1.0F, 0.0F,
        10.0F, 20.0F, 30.0F, 1.0F};
    append_command(
        batch,
        command::matrix_transform3d,
        matrix_transform,
        translated);
    append_command(
        batch,
        command::orthographic_camera,
        orthographic_camera,
        -1.0,
        9.0,
        20.0,
        std::array<float, 3U>{1.0F, 2.0F, 3.0F},
        group,
        look,
        0U,
        up,
        0U,
        0U,
        0U,
        0U,
        0U);
    const progpu_native_matrix_4x4 direct_view{
        1.0F, 0.0F, 0.0F, 0.0F,
        0.0F, 1.0F, 0.0F, 0.0F,
        0.0F, 0.0F, 1.0F, 0.0F,
        -7.0F, -8.0F, -9.0F, 1.0F};
    const progpu_native_matrix_4x4 direct_projection{
        2.0F, 0.0F, 0.0F, 0.0F,
        0.0F, 3.0F, 0.0F, 0.0F,
        0.0F, 0.0F, -1.0F, -1.0F,
        0.0F, 0.0F, -1.0F, 0.0F};
    append_command(
        batch,
        command::matrix_camera,
        matrix_camera,
        direct_view,
        direct_projection,
        0U);
    append_command(
        batch,
        command::viewport3d_visual_set_camera,
        viewport_handle,
        camera_handle);
    append_command(
        batch,
        command::viewport3d_visual_set_viewport,
        viewport_handle,
        20.0,
        30.0,
        100.0,
        50.0);
    append_command(
        batch,
        command::generic_target_create,
        target,
        std::uint64_t{0U},
        std::uint64_t{0U},
        160U,
        120U,
        0U);
    append_command(batch, command::target_set_root, target, viewport_handle);

    channel state;
    PROGPU_REQUIRE(state.apply(batch) == status::success);
    const progpu_native_matrix_4x4 identity{
        1.0F, 0.0F, 0.0F, 0.0F,
        0.0F, 1.0F, 0.0F, 0.0F,
        0.0F, 0.0F, 1.0F, 0.0F,
        0.0F, 0.0F, 0.0F, 1.0F};
    progpu_native_scene_camera_3d sideband_camera{};
    sideband_camera.struct_size = sizeof(sideband_camera);
    sideband_camera.projection = identity;
    sideband_camera.view = identity;
    std::array<progpu_native_scene_mesh_3d_vertex, 3U> vertices{};
    vertices[0].position = {-0.5F, -0.5F, 0.0F, 0.0F};
    vertices[1].position = {0.5F, -0.5F, 0.0F, 0.0F};
    vertices[2].position = {0.0F, 0.5F, 0.0F, 0.0F};
    for (auto& vertex : vertices) {
        vertex.normal = {0.0F, 0.0F, 1.0F, 0.0F};
    }
    const std::array<std::uint32_t, 3U> indices{0U, 1U, 2U};
    progpu_native_scene_mesh_3d mesh{};
    mesh.struct_size = sizeof(mesh);
    mesh.topology = PROGPU_NATIVE_MESH_3D_TRIANGLES;
    mesh.render_mode = PROGPU_NATIVE_MESH_3D_SOLID;
    mesh.vertex_count = static_cast<std::uint32_t>(vertices.size());
    mesh.index_count = static_cast<std::uint32_t>(indices.size());
    mesh.model_transform = identity;
    mesh.normal_transform = identity;
    mesh.color = {1.0F, 1.0F, 1.0F, 1.0F};
    mesh.light_direction = {0.0F, 0.0F, -1.0F, 1.0F};
    mesh.ambient_color = {1.0F, 1.0F, 1.0F, 1.0F};
    mesh.specular_color = {0.0F, 0.0F, 0.0F, 1.0F};
    mesh.material_ambient = {1.0F, 1.0F, 1.0F, 1.0F};
    mesh.opacity = 1.0F;
    PROGPU_REQUIRE(
        state.set_viewport3d_scene(
            viewport_handle,
            sideband_camera,
            {0.0F, 0.0F, 1.0F, 1.0F},
            std::span<const progpu_native_scene_mesh_3d>{&mesh, 1U},
            vertices,
            indices) == status::success);

    const auto read_draw = [](
        const std::vector<std::byte>& stream,
        progpu_native_scene_command& draw,
        progpu_native_scene_camera_3d& camera) {
        const auto header = read_value<progpu_native_scene_header>(
            stream, 0U);
        for (std::uint32_t index = 0U;
             index < header.command_count;
             ++index) {
            const auto candidate = read_value<progpu_native_scene_command>(
                stream,
                header.command_offset +
                    index * sizeof(progpu_native_scene_command));
            if (candidate.kind !=
                PROGPU_NATIVE_SCENE_COMMAND_DRAW_MESH_3D_BATCH) {
                continue;
            }
            draw = candidate;
            camera = read_value<progpu_native_scene_camera_3d>(
                stream, candidate.payload_offset);
            return true;
        }
        return false;
    };
    std::vector<std::byte> stream;
    PROGPU_REQUIRE(
        state.build_scene(target, 8'300U, 1U, stream) == status::success);
    progpu_native_scene_command draw{};
    progpu_native_scene_camera_3d camera{};
    PROGPU_REQUIRE(read_draw(stream, draw, camera));
    PROGPU_REQUIRE(draw.bounds_x == 20.0F);
    PROGPU_REQUIRE(draw.bounds_y == 30.0F);
    PROGPU_REQUIRE(draw.bounds_width == 100.0F);
    PROGPU_REQUIRE(draw.bounds_height == 50.0F);
    PROGPU_REQUIRE(std::abs(camera.camera_position.x - 1.0F) < 0.0001F);
    PROGPU_REQUIRE(std::abs(camera.camera_position.y - 2.0F) < 0.0001F);
    PROGPU_REQUIRE(std::abs(camera.camera_position.z - 5.0F) < 0.0001F);
    PROGPU_REQUIRE(std::abs(camera.projection.m11 - 1.0F) < 0.0001F);
    PROGPU_REQUIRE(std::abs(camera.projection.m22 - 2.0F) < 0.0001F);
    PROGPU_REQUIRE(std::abs(camera.projection.m33 + 1.1F) < 0.0001F);

    std::vector<std::byte> animation_update;
    append_command(
        animation_update,
        command::double_resource,
        angle_animation,
        0.0);
    append_command(
        animation_update,
        command::double_resource,
        x_animation,
        4.0);
    PROGPU_REQUIRE(state.apply(animation_update) == status::success);
    PROGPU_REQUIRE(
        state.build_scene(target, 8'300U, 2U, stream) == status::success);
    PROGPU_REQUIRE(read_draw(stream, draw, camera));
    PROGPU_REQUIRE(std::abs(camera.camera_position.x - 4.0F) < 0.0001F);
    PROGPU_REQUIRE(std::abs(camera.camera_position.y - 2.0F) < 0.0001F);
    PROGPU_REQUIRE(std::abs(camera.camera_position.z - 5.0F) < 0.0001F);
    PROGPU_REQUIRE(std::abs(camera.view.m41 + 4.0F) < 0.0001F);
    PROGPU_REQUIRE(std::abs(camera.view.m42 + 2.0F) < 0.0001F);
    PROGPU_REQUIRE(std::abs(camera.view.m43 + 5.0F) < 0.0001F);

    std::vector<std::byte> invalid_group;
    const std::array<std::uint32_t, 1U> invalid_child{camera_handle};
    append_command(
        invalid_group,
        command::transform3d_group,
        group,
        static_cast<std::uint32_t>(sizeof(invalid_child)),
        invalid_child);
    PROGPU_REQUIRE(state.apply(invalid_group) == status::invalid_handle);
    PROGPU_REQUIRE(
        state.build_scene(target, 8'300U, 3U, stream) == status::success);
    PROGPU_REQUIRE(read_draw(stream, draw, camera));
    PROGPU_REQUIRE(std::abs(camera.camera_position.x - 4.0F) < 0.0001F);

    std::vector<std::byte> orthographic_update;
    const std::array<std::uint32_t, 3U> orthographic_children{
        quaternion_rotate, scale, matrix_transform};
    append_command(
        orthographic_update,
        command::transform3d_group,
        group,
        static_cast<std::uint32_t>(sizeof(orthographic_children)),
        orthographic_children);
    append_command(
        orthographic_update,
        command::viewport3d_visual_set_camera,
        viewport_handle,
        orthographic_camera);
    PROGPU_REQUIRE(state.apply(orthographic_update) == status::success);
    PROGPU_REQUIRE(
        state.build_scene(target, 8'300U, 4U, stream) == status::success);
    PROGPU_REQUIRE(read_draw(stream, draw, camera));
    PROGPU_REQUIRE(std::abs(camera.camera_position.x - 6.0F) < 0.0001F);
    PROGPU_REQUIRE(std::abs(camera.camera_position.y - 23.0F) < 0.0001F);
    PROGPU_REQUIRE(std::abs(camera.camera_position.z - 42.0F) < 0.0001F);
    PROGPU_REQUIRE(std::abs(camera.projection.m11 - 0.1F) < 0.0001F);
    PROGPU_REQUIRE(std::abs(camera.projection.m22 - 0.2F) < 0.0001F);
    PROGPU_REQUIRE(std::abs(camera.projection.m33 + 0.1F) < 0.0001F);
    PROGPU_REQUIRE(std::abs(camera.projection.m43 - 0.1F) < 0.0001F);

    std::vector<std::byte> matrix_camera_update;
    append_command(
        matrix_camera_update,
        command::viewport3d_visual_set_camera,
        viewport_handle,
        matrix_camera);
    PROGPU_REQUIRE(state.apply(matrix_camera_update) == status::success);
    PROGPU_REQUIRE(
        state.build_scene(target, 8'300U, 5U, stream) == status::success);
    PROGPU_REQUIRE(read_draw(stream, draw, camera));
    PROGPU_REQUIRE(std::abs(camera.camera_position.x - 7.0F) < 0.0001F);
    PROGPU_REQUIRE(std::abs(camera.camera_position.y - 8.0F) < 0.0001F);
    PROGPU_REQUIRE(std::abs(camera.camera_position.z - 9.0F) < 0.0001F);
    PROGPU_REQUIRE(camera.projection.m11 == 2.0F);
    PROGPU_REQUIRE(camera.projection.m22 == 3.0F);

    std::vector<std::byte> empty_viewport;
    append_command(
        empty_viewport,
        command::viewport3d_visual_set_viewport,
        viewport_handle,
        std::numeric_limits<double>::infinity(),
        std::numeric_limits<double>::infinity(),
        -std::numeric_limits<double>::infinity(),
        -std::numeric_limits<double>::infinity());
    PROGPU_REQUIRE(state.apply(empty_viewport) == status::success);
    PROGPU_REQUIRE(
        state.build_scene(target, 8'300U, 6U, stream) == status::success);
    PROGPU_REQUIRE(!read_draw(stream, draw, camera));
    return true;
}

bool canonical_viewport3d_scene_uses_wpf_resources() {
    constexpr std::uint32_t viewport = 960U;
    constexpr std::uint32_t root_visual3d = 961U;
    constexpr std::uint32_t child_visual3d = 962U;
    constexpr std::uint32_t target = 963U;
    constexpr std::uint32_t camera = 964U;
    constexpr std::uint32_t visual_transform = 965U;
    constexpr std::uint32_t model_group = 966U;
    constexpr std::uint32_t ambient = 967U;
    constexpr std::uint32_t directional = 968U;
    constexpr std::uint32_t point = 969U;
    constexpr std::uint32_t spot = 970U;
    constexpr std::uint32_t geometry_model = 971U;
    constexpr std::uint32_t mesh_geometry = 972U;
    constexpr std::uint32_t material_group = 973U;
    constexpr std::uint32_t diffuse = 974U;
    constexpr std::uint32_t specular = 975U;
    constexpr std::uint32_t emissive = 976U;
    constexpr std::uint32_t brush = 977U;

    const std::array<std::array<float, 3U>, 6U> positions{{
        {-0.5F, -0.5F, 0.0F},
        {0.5F, -0.5F, 0.0F},
        {0.0F, 0.5F, 0.0F},
        {0.5F, -0.5F, 0.0F},
        {1.5F, -0.5F, 0.0F},
        {1.0F, 0.5F, 0.0F}}};
    const std::array<std::array<float, 3U>, 4U> normals{{
        {1.0F, 2.0F, 3.0F},
        {0.0F, 3.0F, 4.0F},
        {5.0F, 0.0F, 12.0F},
        {8.0F, 15.0F, 0.0F}}};
    const std::array<std::array<double, 2U>, 6U> texture_coordinates{{
        {0.0, 1.0},
        {1.0, 1.0},
        {0.5, 0.0},
        {0.0, 1.0},
        {1.0, 1.0},
        {0.5, 0.0}}};
    const std::array<std::uint32_t, 6U> indices{
        0U, 1U, 2U, 3U, 4U, 5U};
    const std::array<std::uint32_t, 3U> material_children{
        diffuse, specular, emissive};
    const std::array<std::uint32_t, 5U> model_children{
        ambient, directional, point, spot, geometry_model};
    const std::array<float, 3U> camera_position{0.0F, 0.0F, 5.0F};
    const std::array<float, 3U> look_direction{0.0F, 0.0F, -1.0F};
    const std::array<float, 3U> up_direction{0.0F, 1.0F, 0.0F};
    const progpu_native_color white{1.0F, 1.0F, 1.0F, 1.0F};

    std::vector<std::byte> batch;
    append_create(batch, viewport, 40U);
    append_create(batch, root_visual3d, 41U);
    append_create(batch, child_visual3d, 41U);
    append_create(batch, target, 47U);
    append_create(batch, camera, 7U);
    append_create(batch, visual_transform, 29U);
    append_create(batch, model_group, 11U);
    append_create(batch, ambient, 13U);
    append_create(batch, directional, 14U);
    append_create(batch, point, 16U);
    append_create(batch, spot, 17U);
    append_create(batch, geometry_model, 18U);
    append_create(batch, mesh_geometry, 20U);
    append_create(batch, material_group, 22U);
    append_create(batch, diffuse, 23U);
    append_create(batch, specular, 24U);
    append_create(batch, emissive, 25U);
    append_create(batch, brush, 75U);
    append_command(batch, command::visual_create, viewport);
    append_command(
        batch,
        command::solid_color_brush,
        brush,
        0.8,
        progpu_native_color{0.5F, 0.25F, 1.0F, 0.75F},
        0U,
        0U,
        0U,
        0U);
    append_command(
        batch,
        command::perspective_camera,
        camera,
        0.1,
        100.0,
        45.0,
        camera_position,
        0U,
        look_direction,
        0U,
        up_direction,
        0U,
        0U,
        0U,
        0U,
        0U);
    append_command(
        batch,
        command::translate_transform3d,
        visual_transform,
        1.0,
        2.0,
        3.0,
        0U,
        0U,
        0U);
    append_mesh_geometry3d(
        batch,
        mesh_geometry,
        positions,
        normals,
        texture_coordinates,
        indices);
    append_command(
        batch,
        command::diffuse_material,
        diffuse,
        progpu_native_color{0.5F, 1.0F, 0.5F, 0.5F},
        progpu_native_color{0.2F, 0.3F, 0.4F, 1.0F},
        brush);
    append_command(
        batch,
        command::specular_material,
        specular,
        progpu_native_color{0.25F, 0.5F, 1.0F, 1.0F},
        32.0,
        brush);
    append_command(
        batch,
        command::emissive_material,
        emissive,
        progpu_native_color{1.0F, 0.5F, 0.25F, 1.0F},
        brush);
    append_material_group(batch, material_group, material_children);
    append_command(
        batch,
        command::geometry_model3d,
        geometry_model,
        0U,
        mesh_geometry,
        material_group,
        diffuse);
    append_command(
        batch,
        command::ambient_light,
        ambient,
        progpu_native_color{0.1F, 0.2F, 0.3F, 1.0F},
        0U,
        0U);
    append_command(
        batch,
        command::directional_light,
        directional,
        white,
        std::array<float, 3U>{0.0F, 0.0F, -1.0F},
        0U,
        0U,
        0U);
    append_command(
        batch,
        command::point_light,
        point,
        white,
        20.0,
        1.0,
        0.1,
        0.01,
        std::array<float, 3U>{0.0F, 0.0F, 2.0F},
        0U,
        0U,
        0U,
        0U,
        0U,
        0U,
        0U);
    append_command(
        batch,
        command::spot_light,
        spot,
        white,
        30.0,
        1.0,
        0.05,
        0.005,
        60.0,
        30.0,
        std::array<float, 3U>{0.0F, 1.0F, 3.0F},
        0U,
        std::array<float, 3U>{0.0F, 0.0F, -1.0F},
        0U,
        0U,
        0U,
        0U,
        0U,
        0U,
        0U,
        0U,
        0U);
    append_model3d_group(batch, model_group, 0U, model_children);
    append_command(
        batch,
        command::visual3d_set_content,
        child_visual3d,
        model_group);
    append_command(
        batch,
        command::visual3d_set_transform,
        child_visual3d,
        visual_transform);
    append_command(
        batch,
        command::visual3d_insert_child_at,
        root_visual3d,
        child_visual3d,
        0U);
    append_command(
        batch,
        command::viewport3d_visual_set_3d_child,
        viewport,
        root_visual3d);
    append_command(
        batch,
        command::viewport3d_visual_set_camera,
        viewport,
        camera);
    append_command(
        batch,
        command::viewport3d_visual_set_viewport,
        viewport,
        10.0,
        20.0,
        120.0,
        80.0);
    append_command(
        batch,
        command::generic_target_create,
        target,
        std::uint64_t{0U},
        std::uint64_t{0U},
        160U,
        120U,
        0U);
    append_command(batch, command::target_set_root, target, viewport);

    channel state;
    PROGPU_REQUIRE(state.apply(batch) == status::success);
    std::vector<std::byte> stream;
    PROGPU_REQUIRE(
        state.build_scene(target, 8'400U, 1U, stream) == status::success);
    const auto header = read_value<progpu_native_scene_header>(stream, 0U);
    bool found_mesh_batch = false;
    bool found_draw = false;
    for (std::uint32_t index = 0U; index < header.resource_count; ++index) {
        const auto resource = read_value<progpu_native_scene_resource>(
            stream,
            header.resource_offset +
                index * sizeof(progpu_native_scene_resource));
        if (resource.kind != PROGPU_NATIVE_SCENE_RESOURCE_MESH_3D_BATCH) {
            continue;
        }
        PROGPU_REQUIRE(
            resource.payload_size ==
            4U * sizeof(progpu_native_scene_mesh_3d));
        PROGPU_REQUIRE(
            resource.auxiliary_size ==
            6U * sizeof(progpu_native_scene_mesh_3d_vertex) +
            6U * sizeof(std::uint32_t) +
            4U * sizeof(progpu_native_scene_light_3d));
        const auto front_diffuse = read_value<progpu_native_scene_mesh_3d>(
            stream, resource.payload_offset);
        const auto front_specular = read_value<progpu_native_scene_mesh_3d>(
            stream,
            resource.payload_offset + sizeof(progpu_native_scene_mesh_3d));
        const auto front_emissive = read_value<progpu_native_scene_mesh_3d>(
            stream,
            resource.payload_offset +
                2U * sizeof(progpu_native_scene_mesh_3d));
        const auto back_diffuse = read_value<progpu_native_scene_mesh_3d>(
            stream,
            resource.payload_offset +
                3U * sizeof(progpu_native_scene_mesh_3d));
        PROGPU_REQUIRE(
            front_diffuse.flags == PROGPU_NATIVE_MESH_3D_FRONT_FACE);
        PROGPU_REQUIRE(
            (front_specular.flags &
                PROGPU_NATIVE_MESH_3D_SPECULAR_MATERIAL) != 0U);
        PROGPU_REQUIRE(front_emissive.shading_mode == 0U);
        PROGPU_REQUIRE(
            back_diffuse.flags == PROGPU_NATIVE_MESH_3D_BACK_FACE);
        PROGPU_REQUIRE(front_diffuse.light_count == 4U);
        PROGPU_REQUIRE(front_diffuse.model_transform.m41 == 1.0F);
        PROGPU_REQUIRE(front_diffuse.model_transform.m42 == 2.0F);
        PROGPU_REQUIRE(front_diffuse.model_transform.m43 == 3.0F);
        PROGPU_REQUIRE(std::abs(front_diffuse.opacity - 0.3F) < 0.0001F);
        const auto first_vertex =
            read_value<progpu_native_scene_mesh_3d_vertex>(
                stream, resource.auxiliary_offset);
        const float inverse_root_fourteen = 1.0F / std::sqrt(14.0F);
        PROGPU_REQUIRE(
            std::abs(first_vertex.normal.x - inverse_root_fourteen) <
            0.000001F);
        PROGPU_REQUIRE(
            std::abs(first_vertex.normal.y -
                2.0F * inverse_root_fourteen) < 0.000001F);
        PROGPU_REQUIRE(
            std::abs(first_vertex.normal.z -
                3.0F * inverse_root_fourteen) < 0.000001F);
        const auto fifth_vertex =
            read_value<progpu_native_scene_mesh_3d_vertex>(
                stream,
                resource.auxiliary_offset +
                    4U * sizeof(progpu_native_scene_mesh_3d_vertex));
        PROGPU_REQUIRE(fifth_vertex.normal.z > 0.999F);
        const std::size_t light_offset = resource.auxiliary_offset +
            6U * sizeof(progpu_native_scene_mesh_3d_vertex) +
            6U * sizeof(std::uint32_t);
        for (std::uint32_t light_index = 0U;
             light_index < 4U;
             ++light_index) {
            const auto native_light =
                read_value<progpu_native_scene_light_3d>(
                    stream,
                    light_offset + light_index *
                        sizeof(progpu_native_scene_light_3d));
            PROGPU_REQUIRE(native_light.kind == light_index);
        }
        found_mesh_batch = true;
    }
    for (std::uint32_t index = 0U; index < header.command_count; ++index) {
        const auto scene_command = read_value<progpu_native_scene_command>(
            stream,
            header.command_offset +
                index * sizeof(progpu_native_scene_command));
        if (scene_command.kind !=
            PROGPU_NATIVE_SCENE_COMMAND_DRAW_MESH_3D_BATCH) {
            continue;
        }
        PROGPU_REQUIRE(scene_command.bounds_x == 10.0F);
        PROGPU_REQUIRE(scene_command.bounds_y == 20.0F);
        PROGPU_REQUIRE(scene_command.bounds_width == 120.0F);
        PROGPU_REQUIRE(scene_command.bounds_height == 80.0F);
        const auto mapping =
            read_value<progpu_native_scene_mesh_3d_materials>(
                stream,
                scene_command.payload_offset +
                    sizeof(progpu_native_scene_camera_3d));
        PROGPU_REQUIRE(mapping.brush_count == 4U);
        found_draw = true;
    }
    PROGPU_REQUIRE(found_mesh_batch);
    PROGPU_REQUIRE(found_draw);

    std::vector<std::byte> invalid_material_update;
    const std::array<std::uint32_t, 1U> invalid_material_child{camera};
    append_material_group(
        invalid_material_update,
        material_group,
        invalid_material_child);
    PROGPU_REQUIRE(
        state.apply(invalid_material_update) == status::invalid_handle);
    PROGPU_REQUIRE(
        state.build_scene(target, 8'400U, 2U, stream) == status::success);

    std::vector<std::byte> invalid_cycle;
    append_command(
        invalid_cycle,
        command::visual3d_insert_child_at,
        child_visual3d,
        root_visual3d,
        0U);
    PROGPU_REQUIRE(state.apply(invalid_cycle) == status::invalid_graph);

    std::vector<std::byte> delete_dependency;
    append_command(
        delete_dependency,
        command::channel_delete_resource,
        brush,
        75U);
    PROGPU_REQUIRE(state.apply(delete_dependency) == status::invalid_graph);

    std::vector<std::byte> remove_child;
    append_command(
        remove_child,
        command::visual3d_remove_child,
        root_visual3d,
        child_visual3d);
    PROGPU_REQUIRE(state.apply(remove_child) == status::success);
    PROGPU_REQUIRE(
        state.build_scene(target, 8'400U, 3U, stream) == status::success);
    const auto empty_header = read_value<progpu_native_scene_header>(
        stream, 0U);
    for (std::uint32_t index = 0U;
         index < empty_header.command_count;
         ++index) {
        const auto scene_command = read_value<progpu_native_scene_command>(
            stream,
            empty_header.command_offset +
                index * sizeof(progpu_native_scene_command));
        PROGPU_REQUIRE(scene_command.kind !=
            PROGPU_NATIVE_SCENE_COMMAND_DRAW_MESH_3D_BATCH);
    }
    std::vector<std::byte> restore_child;
    append_command(
        restore_child,
        command::visual3d_insert_child_at,
        root_visual3d,
        child_visual3d,
        0U);
    PROGPU_REQUIRE(state.apply(restore_child) == status::success);
    PROGPU_REQUIRE(
        state.build_scene(target, 8'400U, 4U, stream) == status::success);
    return true;
}

bool bitmap_dpi_is_atomic_and_preserves_legacy_bindings() {
    channel state;
    std::vector<std::byte> create;
    append_create(create, 1U, 95U);
    append_create(create, 2U, 96U);
    PROGPU_REQUIRE(state.apply(create) == status::success);
    const std::array pixels{std::byte{1}, std::byte{2}, std::byte{3}, std::byte{255}};
    double dpi_x = -1.0;
    double dpi_y = -2.0;
    PROGPU_REQUIRE(state.get_bitmap_source_dpi(1U, dpi_x, dpi_y) == status::invalid_handle);
    PROGPU_REQUIRE(dpi_x == -1.0 && dpi_y == -2.0);
    for (const auto handle : {1U, 2U}) {
        for (const bool external : {false, true}) {
            const auto bind = [&](double x, double y) {
                if (handle == 1U) {
                    return external
                        ? state.set_bitmap_source_external_image(handle, 1U, 1U, x, y)
                        : state.set_bitmap_source_rgba8(handle, 1U, 1U, 4U, pixels, x, y);
                }
                return external
                    ? state.set_double_buffered_bitmap_external_image(handle, 1U, 1U, x, y)
                    : state.set_double_buffered_bitmap_rgba8(handle, 1U, 1U, 4U, pixels, x, y);
            };
            PROGPU_REQUIRE(bind(144.0, 192.0) == status::success);
            PROGPU_REQUIRE(state.get_bitmap_source_dpi(handle, dpi_x, dpi_y) == status::success);
            PROGPU_REQUIRE(dpi_x == 144.0 && dpi_y == 192.0);
            const auto generation = state.resource_generation(handle);
            const std::array invalid{
                0.0, -1.0, std::numeric_limits<double>::infinity(),
                std::numeric_limits<double>::quiet_NaN(),
                std::numeric_limits<double>::denorm_min()};
            for (const double value : invalid) {
                PROGPU_REQUIRE(bind(value, 96.0) == status::invalid_argument);
                PROGPU_REQUIRE(bind(96.0, value) == status::invalid_argument);
                PROGPU_REQUIRE(state.resource_generation(handle) == generation);
                PROGPU_REQUIRE(state.get_bitmap_source_dpi(handle, dpi_x, dpi_y) == status::success);
                PROGPU_REQUIRE(dpi_x == 144.0 && dpi_y == 192.0);
            }
            PROGPU_REQUIRE(bind(72.0, 120.0) == status::success);
            PROGPU_REQUIRE(state.resource_generation(handle) == generation + 1U);
            PROGPU_REQUIRE(state.get_bitmap_source_dpi(handle, dpi_x, dpi_y) == status::success);
            PROGPU_REQUIRE(dpi_x == 72.0 && dpi_y == 120.0);
            const auto legacy = handle == 1U
                ? (external ? state.set_bitmap_source_external_image(handle, 1U, 1U)
                    : state.set_bitmap_source_rgba8(handle, 1U, 1U, 4U, pixels))
                : (external ? state.set_double_buffered_bitmap_external_image(handle, 1U, 1U)
                    : state.set_double_buffered_bitmap_rgba8(handle, 1U, 1U, 4U, pixels));
            PROGPU_REQUIRE(legacy == status::success);
            PROGPU_REQUIRE(state.get_bitmap_source_dpi(handle, dpi_x, dpi_y) == status::success);
            PROGPU_REQUIRE(dpi_x == 96.0 && dpi_y == 96.0);
        }
    }
    // Exercise all additive C ABI entry points, including rejection before mutation.
    progpu_native_mil_channel* abi = nullptr;
    PROGPU_REQUIRE(progpu_native_mil_channel_create(&abi) == PROGPU_NATIVE_MIL_STATUS_SUCCESS);
    PROGPU_REQUIRE(progpu_native_mil_channel_apply(abi, create.data(), create.size(), nullptr)
        == PROGPU_NATIVE_MIL_STATUS_SUCCESS);
    const std::array copied{
        progpu_native_mil_channel_set_bitmap_source_rgba8_with_dpi,
        progpu_native_mil_channel_set_double_buffered_bitmap_rgba8_with_dpi};
    const std::array external{
        progpu_native_mil_channel_set_bitmap_source_external_image_with_dpi,
        progpu_native_mil_channel_set_double_buffered_bitmap_external_image_with_dpi};
    for (std::uint32_t i = 0U; i < copied.size(); ++i) {
        const auto handle = i + 1U;
        PROGPU_REQUIRE(copied[i](abi, handle, 1U, 1U, 4U, pixels.data(), pixels.size(), 144.0, 192.0)
            == PROGPU_NATIVE_MIL_STATUS_SUCCESS);
        const auto generation = progpu_native_mil_channel_get_resource_generation(abi, handle);
        PROGPU_REQUIRE(copied[i](abi, handle, 1U, 1U, 4U, nullptr, pixels.size(), 144.0, 192.0)
            == PROGPU_NATIVE_MIL_STATUS_INVALID_ARGUMENT);
        PROGPU_REQUIRE(external[i](abi, handle, 1U, 1U, 0.0, 192.0)
            == PROGPU_NATIVE_MIL_STATUS_INVALID_ARGUMENT);
        PROGPU_REQUIRE(progpu_native_mil_channel_get_resource_generation(abi, handle) == generation);
        PROGPU_REQUIRE(external[i](abi, handle, 1U, 1U, 72.0, 120.0)
            == PROGPU_NATIVE_MIL_STATUS_SUCCESS);
        PROGPU_REQUIRE(external[i](nullptr, handle, 1U, 1U, 72.0, 120.0)
            == PROGPU_NATIVE_MIL_STATUS_INVALID_ARGUMENT);
    }
    progpu_native_mil_channel_destroy(abi);
    return true;
}

bool canonical_tile_brush_packets_are_transactional_and_typed() {
    using layout = command_layouts::image_brush;
    static_assert(layout::fixed_size == command_layouts::drawing_brush::fixed_size);
    static_assert(layout::fixed_size == command_layouts::visual_brush::fixed_size);
    static_assert(layout::h_image_source_offset == command_layouts::drawing_brush::h_drawing_offset);
    static_assert(layout::h_image_source_offset == command_layouts::visual_brush::h_visual_offset);
    const std::array kinds{command::image_brush, command::drawing_brush, command::visual_brush};
    const std::array brush_types{80U, 81U, 82U};
    const std::array source_types{95U, 87U, 39U};
    const auto frame = [](const std::vector<std::byte>& packet) {
        std::vector<std::byte> bytes;
        append_value(bytes, static_cast<std::uint32_t>(packet.size() + 4U));
        bytes.insert(bytes.end(), packet.begin(), packet.end());
        return bytes;
    };
    for (std::size_t kind = 0U; kind < kinds.size(); ++kind) {
        channel state;
        std::vector<std::byte> create;
        append_create(create, 1U, brush_types[kind]);
        append_create(create, 2U, source_types[kind]);
        append_create(create, 3U, 66U);
        append_create(create, 4U, 49U);
        append_create(create, 5U, 52U);
        append_create(create, 6U, 52U);
        PROGPU_REQUIRE(state.apply(create) == status::success);
        std::vector<std::byte> packet(layout::fixed_size);
        write_value(packet, 0U, kinds[kind]);
        write_value(packet, layout::handle_offset, 1U);
        write_value(packet, layout::opacity_offset, 0.75);
        const std::array viewport{0.1, 0.2, 0.5, 0.25};
        const std::array viewbox{0.0, 0.0, 1.0, 1.0};
        write_value(packet, layout::viewport_offset, viewport);
        write_value(packet, layout::viewbox_offset, viewbox);
        write_value(packet, layout::cache_invalidation_threshold_minimum_offset, 0.707);
        write_value(packet, layout::cache_invalidation_threshold_maximum_offset, 1.414);
        write_value(packet, layout::h_opacity_animations_offset, 4U);
        write_value(packet, layout::h_transform_offset, 3U);
        write_value(packet, layout::h_relative_transform_offset, 3U);
        write_value(packet, layout::viewport_units_offset, 1U);
        write_value(packet, layout::viewbox_units_offset, 1U);
        write_value(packet, layout::h_viewport_animations_offset, 5U);
        write_value(packet, layout::h_viewbox_animations_offset, 6U);
        write_value(packet, layout::stretch_offset, 3U);
        write_value(packet, layout::tile_mode_offset, 3U);
        write_value(packet, layout::alignment_x_offset, 2U);
        write_value(packet, layout::alignment_y_offset, 1U);
        write_value(packet, layout::caching_hint_offset, 1U);
        write_value(packet, layout::h_image_source_offset, 2U);
        batch_metrics metrics{};
        PROGPU_REQUIRE(state.apply(frame(packet), &metrics) == status::success);
        PROGPU_REQUIRE(metrics.updated_resource_count == 1U);
        const auto generation = state.resource_generation(1U);
        const std::array enum_offsets{
            layout::viewport_units_offset, layout::viewbox_units_offset,
            layout::stretch_offset, layout::tile_mode_offset,
            layout::alignment_x_offset, layout::alignment_y_offset,
            layout::caching_hint_offset};
        for (const auto offset : enum_offsets) {
            auto invalid = packet;
            write_value(invalid, offset, std::numeric_limits<std::uint32_t>::max());
            auto transaction = frame(packet);
            const auto malformed = frame(invalid);
            transaction.insert(transaction.end(), malformed.begin(), malformed.end());
            PROGPU_REQUIRE(state.apply(transaction) == status::malformed_batch);
            PROGPU_REQUIRE(state.resource_generation(1U) == generation);
        }
        const std::array double_offsets{
            layout::opacity_offset, layout::viewport_offset, layout::viewbox_offset};
        for (const auto offset : double_offsets) {
            auto invalid = packet;
            write_value(invalid, offset, std::numeric_limits<double>::quiet_NaN());
            PROGPU_REQUIRE(state.apply(frame(invalid)) == status::malformed_batch);
            PROGPU_REQUIRE(state.resource_generation(1U) == generation);
        }
        const std::array reference_offsets{
            layout::h_opacity_animations_offset, layout::h_transform_offset,
            layout::h_relative_transform_offset, layout::h_viewport_animations_offset,
            layout::h_viewbox_animations_offset, layout::h_image_source_offset};
        for (const auto offset : reference_offsets) {
            auto invalid = packet;
            write_value(invalid, offset, 1U); // The brush is never a valid dependency type.
            PROGPU_REQUIRE(state.apply(frame(invalid)) == status::invalid_handle);
            PROGPU_REQUIRE(state.resource_generation(1U) == generation);
        }
        const std::array dependency_types{source_types[kind], 66U, 49U, 52U, 52U};
        for (std::uint32_t i = 0U; i < dependency_types.size(); ++i) {
            std::vector<std::byte> remove;
            append_command(remove, command::channel_delete_resource, i + 2U, dependency_types[i]);
            PROGPU_REQUIRE(state.apply(remove) == status::invalid_graph);
            PROGPU_REQUIRE(state.resource_count() == 6U);
        }
        auto truncated = packet;
        truncated.resize(packet.size() - 4U);
        PROGPU_REQUIRE(state.apply(frame(truncated)) == status::malformed_batch);
        auto oversized = packet;
        oversized.resize(packet.size() + 4U);
        PROGPU_REQUIRE(state.apply(frame(oversized)) == status::malformed_batch);
        // All reference fields are replaceable, including a null content source.
        for (const auto offset : reference_offsets) write_value(packet, offset, 0U);
        PROGPU_REQUIRE(state.apply(frame(packet)) == status::success);
        PROGPU_REQUIRE(state.resource_generation(1U) == generation + 1U);
        const double infinity = std::numeric_limits<double>::infinity();
        const std::array empty{infinity, infinity, -infinity, -infinity};
        write_value(packet, layout::viewport_offset, empty);
        write_value(packet, layout::viewbox_offset, empty);
        write_value(packet, layout::cache_invalidation_threshold_minimum_offset,
            std::numeric_limits<double>::quiet_NaN());
        write_value(packet, layout::cache_invalidation_threshold_maximum_offset, infinity);
        PROGPU_REQUIRE(state.apply(frame(packet)) == status::success);
        for (std::uint32_t i = 0U; i < dependency_types.size(); ++i) {
            std::vector<std::byte> remove;
            append_command(remove, command::channel_delete_resource, i + 2U, dependency_types[i]);
            PROGPU_REQUIRE(state.apply(remove) == status::success);
        }
        std::vector<std::byte> remove;
        append_command(remove, command::channel_delete_resource, 1U, brush_types[kind]);
        PROGPU_REQUIRE(state.apply(remove) == status::success);
        PROGPU_REQUIRE(state.resource_count() == 0U);
    }
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
    static_assert(sizeof(progpu_native_mil_scene_build_request) == 64U);
    static_assert(sizeof(progpu_native_mil_scene_build_result) == 32U);
    progpu_native_mil_channel* native_channel = nullptr;
    PROGPU_REQUIRE(
        progpu_native_mil_channel_create(&native_channel) ==
        PROGPU_NATIVE_MIL_STATUS_SUCCESS);
    PROGPU_REQUIRE(native_channel != nullptr);

    std::vector<std::byte> batch;
    append_create(batch, 17U, 39U);
    append_create(batch, 18U, 47U);
    append_command(batch, command::visual_create, 17U);
    append_command(batch, command::visual_set_offset, 17U, 2.0, 4.0);
    append_command(
        batch,
        command::generic_target_create,
        18U,
        std::uint64_t{0U},
        std::uint64_t{0U},
        640U,
        480U,
        0U);
    append_command(batch, command::target_set_root, 18U, 17U);
    progpu_native_mil_batch_metrics metrics{};
    metrics.struct_size = sizeof(metrics);
    PROGPU_REQUIRE(
        progpu_native_mil_channel_apply(
            native_channel,
            batch.data(),
            batch.size(),
            &metrics) == PROGPU_NATIVE_MIL_STATUS_SUCCESS);
    PROGPU_REQUIRE(metrics.command_count == 6U);
    PROGPU_REQUIRE(
        progpu_native_mil_channel_get_resource_count(native_channel) == 2U);
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

    std::vector<std::byte> writeable_bitmap;
    append_create(writeable_bitmap, 19U, 96U);
    append_command(
        writeable_bitmap,
        command::double_buffered_bitmap,
        19U,
        std::uint64_t{0U},
        0U);
    PROGPU_REQUIRE(
        progpu_native_mil_channel_apply(
            native_channel,
            writeable_bitmap.data(),
            writeable_bitmap.size(),
            nullptr) == PROGPU_NATIVE_MIL_STATUS_SUCCESS);
    const std::array<std::byte, 4U> writeable_pixel{
        std::byte{0x20},
        std::byte{0x40},
        std::byte{0x80},
        std::byte{0xFF}};
    PROGPU_REQUIRE(
        progpu_native_mil_channel_set_double_buffered_bitmap_rgba8(
            native_channel,
            19U,
            1U,
            1U,
            4U,
            writeable_pixel.data(),
            writeable_pixel.size()) == PROGPU_NATIVE_MIL_STATUS_SUCCESS);
    PROGPU_REQUIRE(
        progpu_native_mil_channel_set_double_buffered_bitmap_external_image(
            native_channel,
            19U,
            1U,
            1U) == PROGPU_NATIVE_MIL_STATUS_SUCCESS);
    PROGPU_REQUIRE(
        progpu_native_mil_channel_set_double_buffered_bitmap_external_image(
            native_channel,
            17U,
            1U,
            1U) == PROGPU_NATIVE_MIL_STATUS_INVALID_HANDLE);

    progpu_native_mil_scene_build_request request{};
    request.struct_size = sizeof(request);
    request.target_handle = 18U;
    request.scene_id = 9'001U;
    request.generation = 1U;
    request.dpi_scale_x = 1.25;
    request.dpi_scale_y = 1.5;
    request.monotonic_time_nanoseconds = 123'456'789U;
    request.request_serial = 41U;
    progpu_native_mil_scene_metrics scene_metrics{};
    scene_metrics.struct_size = sizeof(scene_metrics);
    progpu_native_mil_scene_build_result build_result{};
    build_result.struct_size = sizeof(build_result);
    std::size_t required_bytes = 0U;
    PROGPU_REQUIRE(
        progpu_native_mil_channel_build_scene_with_request(
            native_channel,
            &request,
            nullptr,
            0U,
            &required_bytes,
            &scene_metrics,
            &build_result) == PROGPU_NATIVE_MIL_STATUS_SUCCESS);
    PROGPU_REQUIRE(required_bytes != 0U);
    PROGPU_REQUIRE(build_result.request_serial == request.request_serial);
    PROGPU_REQUIRE(build_result.stream_bytes == required_bytes);
    PROGPU_REQUIRE(build_result.flags ==
        PROGPU_NATIVE_MIL_SCENE_BUILD_RESULT_NONE);
    PROGPU_REQUIRE(build_result.next_due_time_nanoseconds == 0U);

    std::vector<std::byte> first_stream(required_bytes);
    std::size_t written_bytes = 0U;
    scene_metrics.struct_size = sizeof(scene_metrics);
    build_result.struct_size = sizeof(build_result);
    PROGPU_REQUIRE(
        progpu_native_mil_channel_build_scene_with_request(
            native_channel,
            &request,
            first_stream.data(),
            first_stream.size(),
            &written_bytes,
            &scene_metrics,
            &build_result) == PROGPU_NATIVE_MIL_STATUS_SUCCESS);
    PROGPU_REQUIRE(written_bytes == required_bytes);

    std::vector<std::byte> repeated_stream(required_bytes);
    build_result.struct_size = sizeof(build_result);
    PROGPU_REQUIRE(
        progpu_native_mil_channel_build_scene_with_request(
            native_channel,
            &request,
            repeated_stream.data(),
            repeated_stream.size(),
            &written_bytes,
            nullptr,
            &build_result) == PROGPU_NATIVE_MIL_STATUS_SUCCESS);
    PROGPU_REQUIRE(repeated_stream == first_stream);

    auto invalid_request = request;
    invalid_request.monotonic_time_nanoseconds += 1U;
    build_result.struct_size = sizeof(build_result);
    PROGPU_REQUIRE(
        progpu_native_mil_channel_build_scene_with_request(
            native_channel,
            &invalid_request,
            nullptr,
            0U,
            &written_bytes,
            nullptr,
            &build_result) == PROGPU_NATIVE_MIL_STATUS_INVALID_ARGUMENT);

    invalid_request = request;
    invalid_request.flags = 0x8000'0000U;
    build_result.struct_size = sizeof(build_result);
    PROGPU_REQUIRE(
        progpu_native_mil_channel_build_scene_with_request(
            native_channel,
            &invalid_request,
            nullptr,
            0U,
            &written_bytes,
            nullptr,
            &build_result) == PROGPU_NATIVE_MIL_STATUS_INVALID_ARGUMENT);
    invalid_request = request;
    invalid_request.reserved0 = 1U;
    build_result.struct_size = sizeof(build_result);
    PROGPU_REQUIRE(
        progpu_native_mil_channel_build_scene_with_request(
            native_channel,
            &invalid_request,
            nullptr,
            0U,
            &written_bytes,
            nullptr,
            &build_result) == PROGPU_NATIVE_MIL_STATUS_INVALID_ARGUMENT);
    invalid_request = request;
    invalid_request.dpi_scale_x =
        std::numeric_limits<double>::quiet_NaN();
    build_result.struct_size = sizeof(build_result);
    PROGPU_REQUIRE(
        progpu_native_mil_channel_build_scene_with_request(
            native_channel,
            &invalid_request,
            nullptr,
            0U,
            &written_bytes,
            nullptr,
            &build_result) == PROGPU_NATIVE_MIL_STATUS_INVALID_ARGUMENT);
    invalid_request = request;
    invalid_request.request_serial = 0U;
    build_result.struct_size = sizeof(build_result);
    PROGPU_REQUIRE(
        progpu_native_mil_channel_build_scene_with_request(
            native_channel,
            &invalid_request,
            nullptr,
            0U,
            &written_bytes,
            nullptr,
            &build_result) == PROGPU_NATIVE_MIL_STATUS_INVALID_ARGUMENT);

    request.request_serial = 42U;
    build_result.struct_size = sizeof(build_result);
    PROGPU_REQUIRE(
        progpu_native_mil_channel_build_scene_with_request(
            native_channel,
            &request,
            repeated_stream.data(),
            repeated_stream.size(),
            &written_bytes,
            nullptr,
            &build_result) == PROGPU_NATIVE_MIL_STATUS_SUCCESS);
    PROGPU_REQUIRE(repeated_stream == first_stream);
    PROGPU_REQUIRE(build_result.request_serial == 42U);

    std::vector<std::byte> offset_update;
    append_command(
        offset_update,
        command::visual_set_offset,
        17U,
        7.0,
        11.0);
    PROGPU_REQUIRE(
        progpu_native_mil_channel_apply(
            native_channel,
            offset_update.data(),
            offset_update.size(),
            nullptr) == PROGPU_NATIVE_MIL_STATUS_SUCCESS);
    build_result.struct_size = sizeof(build_result);
    PROGPU_REQUIRE(
        progpu_native_mil_channel_build_scene_with_request(
            native_channel,
            &request,
            repeated_stream.data(),
            repeated_stream.size(),
            &written_bytes,
            nullptr,
            &build_result) == PROGPU_NATIVE_MIL_STATUS_SUCCESS);
    PROGPU_REQUIRE(repeated_stream != first_stream);

    std::size_t legacy_required_bytes = 0U;
    PROGPU_REQUIRE(
        progpu_native_mil_channel_build_scene(
            native_channel,
            18U,
            9'001U,
            2U,
            nullptr,
            0U,
            &legacy_required_bytes,
            nullptr) == PROGPU_NATIVE_MIL_STATUS_SUCCESS);
    PROGPU_REQUIRE(legacy_required_bytes != 0U);
    progpu_native_mil_channel_destroy(native_channel);
    return true;
}

} // namespace

int main() {
    {
        static_assert(sizeof(progpu_native_scene_tile_composite) == 64U);
        progpu::native::semantic_scene_builder builder(9510U, 1U);
        std::uint32_t state_index{}, tile_index{};
        PROGPU_REQUIRE(builder.add_state(progpu::native::semantic_scene_builder::identity_state(), state_index));
        progpu_native_scene_tile_composite tile{sizeof(tile), 1U, 2U, 0U,
            8.0F, 4.0F, 32.0F, 16.0F, 0.125F, 0.0F, 0.0F, 0.25F, -2.0F, -2.0F, 0U, 0U};
        PROGPU_REQUIRE(builder.add_tile_composite(tile, tile_index));
        progpu_native_scene_layer layer{};
        layer.struct_size = sizeof(layer);
        layer.flags = PROGPU_NATIVE_SCENE_LAYER_BOUNDS | PROGPU_NATIVE_SCENE_LAYER_CACHE_CONTENT |
            PROGPU_NATIVE_SCENE_LAYER_CACHE_LOCAL_SPACE | PROGPU_NATIVE_SCENE_LAYER_CACHE_TILE;
        layer.bounds = {0.0F, 0.0F, 3.0F, 5.0F};
        layer.opacity = 1.0F;
        layer.mask_resource_index = layer.effect_resource_index = PROGPU_NATIVE_SCENE_NO_INDEX;
        layer.content_revision = 7U;
        layer.composite_revision = 9U;
        layer.reserved0 = state_index;
        layer.reserved1 = tile_index;
        PROGPU_REQUIRE(builder.push_layer(layer));
        PROGPU_REQUIRE(builder.pop_layer());
        std::vector<std::byte> stream;
        PROGPU_REQUIRE(builder.build(stream));
        PROGPU_REQUIRE(!stream.empty());
        layer.flags |= PROGPU_NATIVE_SCENE_LAYER_CACHE_FANT | PROGPU_NATIVE_SCENE_LAYER_CACHE_NEAREST;
        PROGPU_REQUIRE(!builder.push_layer(layer));
        layer.flags &= ~(PROGPU_NATIVE_SCENE_LAYER_CACHE_FANT | PROGPU_NATIVE_SCENE_LAYER_CACHE_NEAREST);
        layer.reserved1 = state_index;
        PROGPU_REQUIRE(!builder.push_layer(layer));
        tile.address_u = 3U;
        PROGPU_REQUIRE(!builder.add_tile_composite(tile, tile_index));
    }
    for (const std::uint32_t sampling : {PROGPU_NATIVE_IMAGE_SAMPLING_NEAREST,
        PROGPU_NATIVE_IMAGE_SAMPLING_LINEAR, PROGPU_NATIVE_IMAGE_SAMPLING_FANT}) {
        {
            std::array<progpu::native::vector_vertex, 5U> quad{};
            constexpr std::array<std::array<float, 2U>, 4U> positions{{
                {2.0F, 3.0F}, {9.0F, 5.0F}, {7.0F, 11.0F}, {0.0F, 9.0F}}};
            for (std::size_t index = 0U; index < 4U; ++index)
                std::copy(positions[index].begin(), positions[index].end(), quad[index].position);
            quad[4].brush_index = 42.0F;
            PROGPU_REQUIRE(progpu::native::try_encode_captured_page_quad(quad, 3U, 5U, 64U, 128U, sampling, 0.25F));
            for (std::size_t index = 0U; index < 4U; ++index) {
                PROGPU_REQUIRE(quad[index].position[0] == positions[index][0] && quad[index].position[1] == positions[index][1]);
                PROGPU_REQUIRE(quad[index].color[0] == 3.0F && quad[index].color[1] == 5.0F && quad[index].color[3] == 0.25F);
                PROGPU_REQUIRE(quad[index].brush_index == -2.0F && quad[index].corner_radius == 0.0F && quad[index].stroke_thickness == 0.0F);
                PROGPU_REQUIRE(quad[index].shape_size[0] == (sampling == PROGPU_NATIVE_IMAGE_SAMPLING_NEAREST ? -128.0F :
                    sampling == PROGPU_NATIVE_IMAGE_SAMPLING_FANT ? -32.0F : -64.0F));
            }
            PROGPU_REQUIRE(quad[0].texture_coordinate[0] == 0.0F && quad[0].texture_coordinate[1] == 0.0F);
            PROGPU_REQUIRE(quad[2].texture_coordinate[0] == 1.0F && quad[2].texture_coordinate[1] == 1.0F);
            PROGPU_REQUIRE(quad[4].brush_index == 42.0F);
            quad[0].brush_index = 43.0F;
            quad[3].position[0] = std::numeric_limits<float>::quiet_NaN();
            PROGPU_REQUIRE(!progpu::native::try_encode_captured_page_quad(quad, 3U, 5U, 64U, 128U, sampling, 0.25F));
            PROGPU_REQUIRE(quad[0].brush_index == 43.0F);
            PROGPU_REQUIRE(!progpu::native::try_encode_captured_page_quad(quad, 65U, 5U, 64U, 128U, sampling, 0.25F));
            PROGPU_REQUIRE(quad[0].brush_index == 43.0F);
        }
        for (std::uint32_t u = 0U; u <= 2U; ++u) {
            for (std::uint32_t v = 0U; v <= 2U; ++v) {
                std::array<progpu::native::vector_vertex, 5U> vertices{};
                vertices[4].brush_index = 42.0F;
                PROGPU_REQUIRE(progpu::native::try_write_tile_page_quad(vertices,
                    {8.0F, 4.0F, 32.0F, 16.0F}, {0.125F, 0.0F, 0.0F, 0.25F, -2.0F, -2.0F},
                    3U, 5U, 64U, 64U, u, v, sampling, 0.5F));
                PROGPU_REQUIRE(vertices[0].texture_coordinate[0] == -1.0F);
                PROGPU_REQUIRE(vertices[0].texture_coordinate[1] == -1.0F);
                PROGPU_REQUIRE(vertices[2].texture_coordinate[0] == 3.0F);
                PROGPU_REQUIRE(vertices[2].texture_coordinate[1] == 3.0F);
                PROGPU_REQUIRE(vertices[2].color[0] == 3.0F && vertices[2].color[1] == 5.0F);
                PROGPU_REQUIRE(vertices[2].color[3] == 0.5F && vertices[2].brush_index == -2.0F);
                PROGPU_REQUIRE(vertices[2].shape_size[0] == (sampling == PROGPU_NATIVE_IMAGE_SAMPLING_NEAREST ? -128.0F :
                    sampling == PROGPU_NATIVE_IMAGE_SAMPLING_FANT ? -32.0F : -64.0F));
                PROGPU_REQUIRE(vertices[2].corner_radius == static_cast<float>(u));
                PROGPU_REQUIRE(vertices[2].stroke_thickness == static_cast<float>(v));
                PROGPU_REQUIRE(vertices[4].brush_index == 42.0F);
                vertices[0].brush_index = 42.0F;
                PROGPU_REQUIRE(!progpu::native::try_write_tile_page_quad(vertices,
                    {0.0F, 0.0F, 8.0F, 8.0F}, {1.0F, 0.0F, 0.0F, 1.0F, 0.0F, 0.0F},
                    65U, 5U, 64U, 64U, u, v, sampling, 1.0F));
                PROGPU_REQUIRE(vertices[0].brush_index == 42.0F);
            }
        }
    }
    for (const auto source : {progpu::native::tests::mil_brush_fixture_source::bitmap,
        progpu::native::tests::mil_brush_fixture_source::drawing,
        progpu::native::tests::mil_brush_fixture_source::drawing_image,
        progpu::native::tests::mil_brush_fixture_source::visual}) {
        for (const auto shape : {progpu::native::tests::mil_brush_fixture_shape::line,
            progpu::native::tests::mil_brush_fixture_shape::rectangle,
            progpu::native::tests::mil_brush_fixture_shape::ellipse,
            progpu::native::tests::mil_brush_fixture_shape::rounded_rectangle}) {
            for (std::uint32_t mode = 0U; mode <= 4U; ++mode) {
                for (const bool dashed : {false, true}) {
                    std::vector<std::byte> scene;
                    PROGPU_REQUIRE(progpu::native::tests::build_mil_image_brush_fixture(scene,
                        {.tile_mode = mode, .opacity = 0.5, .skew = true, .source = source,
                            .shape = shape, .inherited_clip = true, .paint_transform = true,
                            .viewport = {0.0, 0.0, 0.25, 0.5}, .fant = true,
                            .pen = true, .dashed = dashed, .cap = mode % 4U}, 9600U + mode));
                    const auto header = read_value<progpu_native_scene_header>(scene, 0U);
                    bool found_stroke_mask = false;
                    for (std::uint32_t index = 0U; index < header.resource_count; ++index) {
                        const auto resource = read_value<progpu_native_scene_resource>(scene,
                            header.resource_offset + index * sizeof(progpu_native_scene_resource));
                        if (resource.kind == PROGPU_NATIVE_SCENE_RESOURCE_LAYER_MASK &&
                            resource.payload_size == sizeof(progpu_native_scene_layer_geometry_mask)) {
                            const auto mask = read_value<progpu_native_scene_layer_geometry_mask>(scene, resource.payload_offset);
                            found_stroke_mask |= mask.kind == PROGPU_NATIVE_SCENE_LAYER_MASK_GEOMETRY &&
                                mask.primitive_count > 0U && mask.opacity == 1.0F && mask.brush.opacity == 1.0F;
                        }
                    }
                    PROGPU_REQUIRE(found_stroke_mask);
                }
            }
        }
    }
    // Authored during implementation-first work; execution and pixel parity
    // remain part of the final validation phase.
    for (const auto source : {progpu::native::tests::mil_brush_fixture_source::bitmap,
        progpu::native::tests::mil_brush_fixture_source::drawing,
        progpu::native::tests::mil_brush_fixture_source::drawing_image,
        progpu::native::tests::mil_brush_fixture_source::visual}) {
        for (const auto shape : {progpu::native::tests::mil_brush_fixture_shape::ellipse,
            progpu::native::tests::mil_brush_fixture_shape::rounded_rectangle,
            progpu::native::tests::mil_brush_fixture_shape::path,
            progpu::native::tests::mil_brush_fixture_shape::group,
            progpu::native::tests::mil_brush_fixture_shape::combined}) {
            for (const bool transformed : {false, true}) {
                std::vector<std::byte> scene;
                const auto figures = make_curve_path_figures();
                PROGPU_REQUIRE(progpu::native::tests::build_mil_image_brush_fixture(
                    scene, {.opacity = 0.5, .skew = transformed, .source = source,
                        .shape = shape, .inherited_clip = true, .paint_transform = transformed,
                        .path_figures = figures}, 9480U));
                const auto header = read_value<progpu_native_scene_header>(scene, 0U);
                bool found_shape_chain = false;
                for (std::uint32_t index = 0U; index < header.resource_count; ++index) {
                    const auto resource = read_value<progpu_native_scene_resource>(scene,
                        header.resource_offset + index * sizeof(progpu_native_scene_resource));
                    if (resource.kind != PROGPU_NATIVE_SCENE_RESOURCE_LAYER_MASK) continue;
                    const auto mask_kind = read_value<std::uint32_t>(scene,
                        resource.payload_offset + offsetof(progpu_native_scene_layer_mask, kind));
                    if (mask_kind != PROGPU_NATIVE_SCENE_LAYER_MASK_VECTOR_CLIP_CHAIN) continue;
                    const auto mask = read_value<progpu_native_scene_layer_vector_mask>(scene,
                        resource.payload_offset);
                    // Inherited ellipse plus paint shape must survive both
                    // rectangle scissors and non-axis-aligned viewport masks.
                    found_shape_chain |= mask.path_count >= 2U &&
                        mask.segment_count >= (shape == progpu::native::tests::mil_brush_fixture_shape::rounded_rectangle ? 9U : 2U) &&
                        (shape != progpu::native::tests::mil_brush_fixture_shape::combined || mask.boolean_node_count >= 3U);
                }
                PROGPU_REQUIRE(found_shape_chain);
            }
        }
    }
    for (const auto source : {progpu::native::tests::mil_brush_fixture_source::drawing,
        progpu::native::tests::mil_brush_fixture_source::drawing_image,
        progpu::native::tests::mil_brush_fixture_source::visual}) {
        std::vector<std::byte> scene;
        PROGPU_REQUIRE(progpu::native::tests::build_mil_image_brush_fixture(
            scene, {.opacity = 0.5, .source = source}, 9490U));
        PROGPU_REQUIRE(!scene.empty());
        scene = {std::byte{0x5a}};
        PROGPU_REQUIRE(!progpu::native::tests::build_mil_image_brush_fixture(
            scene, {.source = source, .source_cycle = true}, 9491U));
        PROGPU_REQUIRE(scene == std::vector<std::byte>{std::byte{0x5a}});
    }
    for (std::uint32_t tile_mode = 1U; tile_mode <= 4U; ++tile_mode) {
        for (const auto source : {progpu::native::tests::mil_brush_fixture_source::bitmap,
            progpu::native::tests::mil_brush_fixture_source::drawing,
            progpu::native::tests::mil_brush_fixture_source::drawing_image,
            progpu::native::tests::mil_brush_fixture_source::visual}) {
            for (const std::uint32_t filter : {0U, 1U, 2U}) {
                std::vector<std::byte> scene;
                PROGPU_REQUIRE(progpu::native::tests::build_mil_image_brush_fixture(scene,
                    {.stretch = 2U, .tile_mode = tile_mode, .opacity = 0.5, .linear = filter == 1U,
                        .source = source, .shape = progpu::native::tests::mil_brush_fixture_shape::ellipse,
                        .inherited_clip = true, .viewport = {0.0, 0.0, 0.25, 0.5}, .fant = filter == 2U}, 9500U + tile_mode));
                const auto header = read_value<progpu_native_scene_header>(scene, 0U);
                std::uint32_t tile_resources = 0U;
                for (std::uint32_t index = 0U; index < header.resource_count; ++index) {
                    const auto resource = read_value<progpu_native_scene_resource>(scene,
                        header.resource_offset + index * sizeof(progpu_native_scene_resource));
                    if (resource.kind != PROGPU_NATIVE_SCENE_RESOURCE_TILE_COMPOSITE) continue;
                    const auto tile = read_value<progpu_native_scene_tile_composite>(scene, resource.payload_offset);
                    PROGPU_REQUIRE(tile.address_u == (tile_mode == 1U || tile_mode == 3U ? 2U : 1U));
                    PROGPU_REQUIRE(tile.address_v == (tile_mode == 2U || tile_mode == 3U ? 2U : 1U));
                    ++tile_resources;
                }
                PROGPU_REQUIRE(tile_resources == 1U);
                if (source != progpu::native::tests::mil_brush_fixture_source::bitmap) {
                    scene = {std::byte{0x5a}};
                    PROGPU_REQUIRE(!progpu::native::tests::build_mil_image_brush_fixture(scene,
                        {.tile_mode = tile_mode, .source = source, .source_cycle = true}, 9510U + tile_mode));
                    PROGPU_REQUIRE(scene == std::vector<std::byte>{std::byte{0x5a}});
                }
            }
        }
    }
    PROGPU_REQUIRE(curve_dashes_match_managed_reference_contracts());
    PROGPU_REQUIRE(
        semantic_path_strokes_preserve_curves_and_forced_joins());
    PROGPU_REQUIRE(channel_retains_visual_target_graph());
    PROGPU_REQUIRE(canonical_hwnd_target_uses_portable_surface_state());
    PROGPU_REQUIRE(failed_batches_roll_back());
    PROGPU_REQUIRE(invalid_visual_graphs_fail_closed());
    PROGPU_REQUIRE(solid_rectangle_compiles_to_semantic_scene());
    PROGPU_REQUIRE(animated_value_resources_drive_render_data_primitives());
    PROGPU_REQUIRE(
        animated_fixed_geometry_resources_drive_retained_geometry());
    PROGPU_REQUIRE(animated_pen_and_dash_resources_drive_strokes());
    PROGPU_REQUIRE(visual_clips_compile_to_exact_semantic_state());
    PROGPU_REQUIRE(visual_geometry_clips_apply_after_effects());
    PROGPU_REQUIRE(viewport3d_geometry_clips_apply_to_isolated_outputs());
    PROGPU_REQUIRE(visual_geometry_clips_apply_after_local_caches());
    PROGPU_REQUIRE(visual_solid_opacity_mask_composes_and_updates());
    PROGPU_REQUIRE(visual_gaussian_effects_compile_to_isolated_layers());
    PROGPU_REQUIRE(visual_bitmap_cache_uses_canonical_typed_retention());
    PROGPU_REQUIRE(visual_bitmap_cache_controls_clear_type_rasterization());
    PROGPU_REQUIRE(visual_bitmap_cache_applies_root_state_at_composite());
    PROGPU_REQUIRE(
        visual_bitmap_cache_applies_gradient_mask_at_composite());
    PROGPU_REQUIRE(
        visual_bitmap_cache_preserves_nested_effect_ordering());
    PROGPU_REQUIRE(visual_static_guidelines_reset_at_child_boundaries());
    PROGPU_REQUIRE(matrix_transform_scopes_compile_to_semantic_state());
    PROGPU_REQUIRE(
        static_transform_resources_compose_and_retain_dependencies());
    PROGPU_REQUIRE(solid_pen_line_compiles_to_geometry_scene());
    PROGPU_REQUIRE(wpf_arc_lowering_matches_core_piece_policy());
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
        render_data_static_guideline_scope_uses_active_transform());
    PROGPU_REQUIRE(dynamic_guidelines_follow_wpf_phase_state());
    PROGPU_REQUIRE(
        compact_dynamic_guidelines_retain_and_reset_phase_state());
    PROGPU_REQUIRE(render_data_opacity_mask_scope_uses_gpu_brush_layer());
    PROGPU_REQUIRE(
        canonical_bitmap_packets_preserve_pointer_free_sideband());
    PROGPU_REQUIRE(
        writeable_bitmap_uses_pointer_free_front_buffer_sideband());
    PROGPU_REQUIRE(
        retained_image_drawing_uses_pointer_free_bitmap_sideband());
    PROGPU_REQUIRE(
        canonical_d3d_image_uses_synchronized_external_image_sideband());
    PROGPU_REQUIRE(render_data_video_uses_live_external_image_sideband());
    PROGPU_REQUIRE(
        retained_video_drawing_uses_pointer_free_media_packet());
    PROGPU_REQUIRE(
        retained_drawing_image_maps_vector_content_into_destination());
    PROGPU_REQUIRE(retained_drawing_image_infers_line_path_bounds());
    PROGPU_REQUIRE(retained_drawing_image_infers_fixed_stroke_bounds());
    PROGPU_REQUIRE(retained_drawing_image_infers_drawing_group_bounds());
    PROGPU_REQUIRE(
        retained_glyph_run_drawing_uses_pointer_free_sfnt_sideband());
    PROGPU_REQUIRE(retained_geometry_group_compiles_to_one_semantic_path());
    PROGPU_REQUIRE(
        retained_geometry_group_accepts_combined_fill_and_clip_children());
    PROGPU_REQUIRE(
        retained_gradient_brushes_compile_with_wpf_mapping_and_animation());
    PROGPU_REQUIRE(degenerate_gradient_pen_caps_use_wpf_stroke_bounds());
    PROGPU_REQUIRE(
        retained_gradient_stops_match_wpf_coincidence_and_pad_edges());
    PROGPU_REQUIRE(retained_viewport3d_uses_pointer_free_mesh_sideband());
    PROGPU_REQUIRE(
        canonical_viewport3d_camera_uses_wpf_transform_resources());
    PROGPU_REQUIRE(canonical_viewport3d_scene_uses_wpf_resources());
    PROGPU_REQUIRE(render_data_scope_errors_fail_closed());
    PROGPU_REQUIRE(canonical_tile_brush_packets_are_transactional_and_typed());
    PROGPU_REQUIRE(bitmap_dpi_is_atomic_and_preserves_legacy_bindings());
    PROGPU_REQUIRE(malformed_and_unsupported_packets_fail_closed());
    PROGPU_REQUIRE(c_abi_is_typed_and_size_versioned());
    return 0;
}
