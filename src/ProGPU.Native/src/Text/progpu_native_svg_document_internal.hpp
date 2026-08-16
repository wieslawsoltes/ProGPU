#pragma once

#include "progpu_native_text.hpp"

#include <cstddef>
#include <cstdint>
#include <string>
#include <string_view>
#include <unordered_map>
#include <utility>
#include <vector>

namespace progpu::native::text::svg_document_detail {

constexpr std::size_t no_node = static_cast<std::size_t>(-1);
constexpr std::size_t maximum_document_bytes = 16U * 1024U * 1024U;
constexpr std::size_t maximum_reference_depth = 64U;

struct attribute final {
    std::string name;
    std::string value;
};

struct node final {
    std::string name;
    std::vector<attribute> attributes;
    std::size_t parent = no_node;
    std::vector<std::size_t> children;
};

struct document final {
    std::vector<node> nodes;
    std::unordered_map<std::string, std::size_t> ids;
    std::size_t root = no_node;
};

struct render_state final {
    progpu_native_affine_2d transform{1.0F, 0.0F, 0.0F, 1.0F, 0.0F, 0.0F};
    std::string fill{"black"};
    float opacity = 1.0F;
    float fill_opacity = 1.0F;
};

struct decoded_glyph final {
    std::vector<svg_glyph_layer> layers;
    std::vector<progpu_native_path_segment> segments;
    std::vector<progpu_native_scene_brush> brushes;
    std::vector<progpu_native_scene_gradient_stop> gradient_stops;
};

bool parse_document(std::string_view xml, document& result) noexcept;

const std::string* find_attribute(
    const node& element,
    std::string_view local_name) noexcept;

std::string_view local_name(std::string_view qualified_name) noexcept;

bool equals_ascii_ignore_case(
    std::string_view left,
    std::string_view right) noexcept;

float read_float(
    const node& element,
    std::string_view name,
    float default_value = 0.0F) noexcept;

float read_coordinate(
    const node& element,
    std::string_view name,
    float default_value,
    std::uint16_t units_per_em) noexcept;

float read_unit_interval(
    const node& element,
    std::string_view name,
    float default_value) noexcept;

bool parse_number_list(
    std::string_view text,
    std::vector<float>& values) noexcept;

progpu_native_affine_2d identity_transform() noexcept;
progpu_native_affine_2d multiply(
    const progpu_native_affine_2d& left,
    const progpu_native_affine_2d& right) noexcept;
progpu_native_point transform_point(
    progpu_native_point point,
    const progpu_native_affine_2d& transform) noexcept;
progpu_native_affine_2d parse_transform(std::string_view text) noexcept;

bool try_parse_color(
    std::string_view text,
    progpu_native_color& color) noexcept;

bool decode_glyph(
    std::string_view xml,
    std::uint16_t glyph_index,
    std::uint16_t units_per_em,
    decoded_glyph& result) noexcept;

} // namespace progpu::native::text::svg_document_detail
