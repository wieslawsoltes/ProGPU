#include "progpu_native_mil.hpp"
#include "progpu_native_mil_curve_dash.hpp"
#include "progpu_native_scene_builder.hpp"
#include "progpu_native_text.hpp"
#include "../Geometry/progpu_native_arc.hpp"
#include "../Scene/progpu_native_semantic_brush.hpp"
#include "../Scene/progpu_native_semantic_validation.hpp"
#include "../Scene/progpu_native_semantic_path_stroke.hpp"
#include "../Direct2D/progpu_native_direct2d_path.hpp"

#include <algorithm>
#include <array>
#include <bit>
#include <cmath>
#include <cstring>
#include <limits>
#include <new>
#include <numbers>
#include <type_traits>
#include <unordered_map>
#include <unordered_set>
#include <utility>
#include <vector>

#if defined(__aarch64__) || defined(_M_ARM64)
#include <arm_neon.h>
#define PROGPU_NATIVE_MIL_INTRINSICS_NEON 1
#elif defined(__SSE2__) || defined(_M_X64) || \
    (defined(_M_IX86_FP) && _M_IX86_FP >= 2)
#include <emmintrin.h>
#define PROGPU_NATIVE_MIL_INTRINSICS_SSE2 1
#endif

namespace progpu::native::mil {
namespace {

constexpr std::uint32_t type_media_player = 1U;
constexpr std::uint32_t type_axis_angle_rotation3d = 3U;
constexpr std::uint32_t type_quaternion_rotation3d = 4U;
constexpr std::uint32_t type_perspective_camera = 7U;
constexpr std::uint32_t type_orthographic_camera = 8U;
constexpr std::uint32_t type_matrix_camera = 9U;
constexpr std::uint32_t type_model3d_group = 11U;
constexpr std::uint32_t type_ambient_light = 13U;
constexpr std::uint32_t type_directional_light = 14U;
constexpr std::uint32_t type_point_light = 16U;
constexpr std::uint32_t type_spot_light = 17U;
constexpr std::uint32_t type_geometry_model3d = 18U;
constexpr std::uint32_t type_mesh_geometry3d = 20U;
constexpr std::uint32_t type_material_group = 22U;
constexpr std::uint32_t type_diffuse_material = 23U;
constexpr std::uint32_t type_specular_material = 24U;
constexpr std::uint32_t type_emissive_material = 25U;
constexpr std::uint32_t type_visual = 39U;
constexpr std::uint32_t type_viewport3d_visual = 40U;
constexpr std::uint32_t type_visual3d = 41U;
constexpr std::uint32_t type_glyph_run = 42U;
constexpr std::uint32_t type_render_data = 43U;
constexpr std::uint32_t type_render_target = 45U;
constexpr std::uint32_t type_hwnd_render_target = 46U;
constexpr std::uint32_t type_generic_render_target = 47U;
constexpr std::uint32_t type_double_resource = 49U;
constexpr std::uint32_t type_color_resource = 50U;
constexpr std::uint32_t type_point_resource = 51U;
constexpr std::uint32_t type_rect_resource = 52U;
constexpr std::uint32_t type_size_resource = 53U;
constexpr std::uint32_t type_matrix_resource = 54U;
constexpr std::uint32_t type_point3d_resource = 55U;
constexpr std::uint32_t type_vector3d_resource = 56U;
constexpr std::uint32_t type_quaternion_resource = 57U;
constexpr std::uint32_t type_blur_effect = 36U;
constexpr std::uint32_t type_drop_shadow_effect = 37U;
constexpr std::uint32_t type_drawing_image = 59U;
constexpr std::uint32_t type_transform3d_group = 27U;
constexpr std::uint32_t type_translate_transform3d = 29U;
constexpr std::uint32_t type_scale_transform3d = 30U;
constexpr std::uint32_t type_rotate_transform3d = 31U;
constexpr std::uint32_t type_matrix_transform3d = 32U;
constexpr std::uint32_t type_transform_group = 61U;
constexpr std::uint32_t type_translate_transform = 62U;
constexpr std::uint32_t type_scale_transform = 63U;
constexpr std::uint32_t type_skew_transform = 64U;
constexpr std::uint32_t type_rotate_transform = 65U;
constexpr std::uint32_t type_matrix_transform = 66U;
constexpr std::uint32_t type_line_geometry = 68U;
constexpr std::uint32_t type_rectangle_geometry = 69U;
constexpr std::uint32_t type_ellipse_geometry = 70U;
constexpr std::uint32_t type_geometry_group = 71U;
constexpr std::uint32_t type_combined_geometry = 72U;
constexpr std::uint32_t type_path_geometry = 73U;
constexpr std::uint32_t type_solid_color_brush = 75U;
constexpr std::uint32_t type_linear_gradient_brush = 77U;
constexpr std::uint32_t type_radial_gradient_brush = 78U;
constexpr std::uint32_t type_image_brush = 80U;
constexpr std::uint32_t type_drawing_brush = 81U;
constexpr std::uint32_t type_visual_brush = 82U;
constexpr std::uint32_t type_dash_style = 84U;
constexpr std::uint32_t type_pen = 85U;
constexpr std::uint32_t type_geometry_drawing = 87U;
constexpr std::uint32_t type_glyph_run_drawing = 88U;
constexpr std::uint32_t type_image_drawing = 89U;
constexpr std::uint32_t type_video_drawing = 90U;
constexpr std::uint32_t type_drawing_group = 91U;
constexpr std::uint32_t type_guideline_set = 92U;
constexpr std::uint32_t type_bitmap_cache = 94U;
constexpr std::uint32_t type_bitmap_source = 95U;
constexpr std::uint32_t type_double_buffered_bitmap = 96U;
constexpr std::uint32_t type_d3d_image = 97U;
constexpr std::uint32_t type_last = 98U;
constexpr std::uint32_t maximum_visual_depth = 256U;
constexpr std::uint32_t maximum_path_record_count = 1U << 20U;
constexpr std::uint32_t render_option_bitmap_scaling = 0x01U;
constexpr std::uint32_t render_option_edge_mode = 0x02U;
constexpr std::uint32_t render_option_compositing_mode = 0x04U;
constexpr std::uint32_t render_option_clear_type_hint = 0x08U;
constexpr std::uint32_t render_option_text_rendering_mode = 0x10U;
constexpr std::uint32_t render_option_text_hinting_mode = 0x20U;
constexpr std::uint32_t render_option_supported_mask =
    render_option_bitmap_scaling | render_option_edge_mode |
    render_option_clear_type_hint | render_option_text_rendering_mode |
    render_option_text_hinting_mode;
constexpr std::uint32_t render_option_known_mask =
    render_option_supported_mask | render_option_compositing_mode |
    render_option_text_rendering_mode | render_option_text_hinting_mode;

template<typename T>
bool read_at(
    std::span<const std::byte> packet,
    std::size_t offset,
    T& value) noexcept {
    if (offset > packet.size() || sizeof(T) > packet.size() - offset) {
        return false;
    }
    std::memcpy(&value, packet.data() + offset, sizeof(T));
    return true;
}

bool has_exact_size(
    const command_view& view,
    std::size_t expected) noexcept {
    return view.packet.size() == expected;
}

status validate_render_data_command_framing(
    const command_view& view) noexcept {
    const auto raw = static_cast<std::uint32_t>(view.kind);
    if (raw < static_cast<std::uint32_t>(command::draw_line) ||
        raw > static_cast<std::uint32_t>(command::pop)) {
        return status::unsupported_command;
    }
    return has_exact_size(
        view,
        command_layouts::fixed_header_size(view.kind))
        ? status::success
        : status::malformed_batch;
}

bool render_data_contains_compact_guidelines(
    std::span<const std::byte> bytes) noexcept {
    batch_reader reader(bytes);
    command_view view{};
    for (;;) {
        const status read_status = reader.next(view);
        if (read_status != status::success) {
            return false;
        }
        if (view.kind == command::push_guideline_y1 ||
            view.kind == command::push_guideline_y2) {
            return true;
        }
    }
}

bool is_visual_type(std::uint32_t type) noexcept {
    return type == type_visual || type == type_viewport3d_visual;
}

bool is_target_type(std::uint32_t type) noexcept {
    return type == type_render_target || type == type_hwnd_render_target ||
        type == type_generic_render_target;
}

bool is_transform_type(std::uint32_t type) noexcept {
    return type >= type_transform_group && type <= type_matrix_transform;
}

bool is_rotation3d_type(std::uint32_t type) noexcept {
    return type == type_axis_angle_rotation3d ||
        type == type_quaternion_rotation3d;
}

bool is_camera3d_type(std::uint32_t type) noexcept {
    return type == type_perspective_camera ||
        type == type_orthographic_camera || type == type_matrix_camera;
}

bool is_model3d_type(std::uint32_t type) noexcept {
    return type == type_model3d_group || type == type_ambient_light ||
        type == type_directional_light || type == type_point_light ||
        type == type_spot_light || type == type_geometry_model3d;
}

bool is_light3d_type(std::uint32_t type) noexcept {
    return type >= type_ambient_light && type <= type_spot_light &&
        type != 15U;
}

bool is_material3d_type(std::uint32_t type) noexcept {
    return type >= type_material_group && type <= type_emissive_material;
}

bool is_transform3d_type(std::uint32_t type) noexcept {
    return type >= type_transform3d_group &&
        type <= type_matrix_transform3d;
}

bool is_drawing_type(std::uint32_t type) noexcept {
    return type >= type_geometry_drawing && type <= type_drawing_group;
}

bool is_effect_type(std::uint32_t type) noexcept {
    return type == type_blur_effect || type == type_drop_shadow_effect;
}

bool finite_double_as_float(double value) noexcept {
    constexpr auto maximum =
        static_cast<double>(std::numeric_limits<float>::max());
    return std::isfinite(value) && value >= -maximum && value <= maximum;
}

progpu_native_matrix_4x4 identity_matrix_4x4() noexcept {
    return {
        1.0F, 0.0F, 0.0F, 0.0F,
        0.0F, 1.0F, 0.0F, 0.0F,
        0.0F, 0.0F, 1.0F, 0.0F,
        0.0F, 0.0F, 0.0F, 1.0F};
}

bool finite_matrix_4x4(const progpu_native_matrix_4x4& matrix) noexcept {
    const auto* values = &matrix.m11;
    return std::all_of(
        values,
        values + 16U,
        [](float value) noexcept { return std::isfinite(value); });
}

progpu_native_matrix_4x4 multiply_matrix_4x4(
    const progpu_native_matrix_4x4& left,
    const progpu_native_matrix_4x4& right) noexcept {
    progpu_native_matrix_4x4 result{};
    const auto* a = &left.m11;
    const auto* b = &right.m11;
    auto* destination = &result.m11;
    for (std::size_t row = 0U; row < 4U; ++row) {
        for (std::size_t column = 0U; column < 4U; ++column) {
            float value = 0.0F;
            for (std::size_t inner = 0U; inner < 4U; ++inner) {
                value += a[row * 4U + inner] *
                    b[inner * 4U + column];
            }
            destination[row * 4U + column] = value;
        }
    }
    return result;
}

progpu_native_matrix_4x4 transpose_matrix_4x4(
    const progpu_native_matrix_4x4& value) noexcept {
    return {
        value.m11, value.m21, value.m31, value.m41,
        value.m12, value.m22, value.m32, value.m42,
        value.m13, value.m23, value.m33, value.m43,
        value.m14, value.m24, value.m34, value.m44};
}

bool try_invert_matrix_4x4(
    const progpu_native_matrix_4x4& source,
    progpu_native_matrix_4x4& inverse) noexcept {
    if (!finite_matrix_4x4(source)) {
        return false;
    }
    std::array<std::array<double, 8U>, 4U> rows{};
    const auto* values = &source.m11;
    for (std::size_t row = 0U; row < 4U; ++row) {
        for (std::size_t column = 0U; column < 4U; ++column) {
            rows[row][column] = values[row * 4U + column];
        }
        rows[row][4U + row] = 1.0;
    }
    for (std::size_t column = 0U; column < 4U; ++column) {
        std::size_t pivot = column;
        double pivot_magnitude = std::abs(rows[pivot][column]);
        for (std::size_t candidate = column + 1U;
             candidate < 4U;
             ++candidate) {
            const double magnitude = std::abs(rows[candidate][column]);
            if (magnitude > pivot_magnitude) {
                pivot = candidate;
                pivot_magnitude = magnitude;
            }
        }
        if (!(pivot_magnitude > 0.0) || !std::isfinite(pivot_magnitude)) {
            return false;
        }
        if (pivot != column) {
            std::swap(rows[pivot], rows[column]);
        }
        const double divisor = rows[column][column];
        for (double& value : rows[column]) {
            value /= divisor;
        }
        for (std::size_t row = 0U; row < 4U; ++row) {
            if (row == column) {
                continue;
            }
            const double factor = rows[row][column];
            for (std::size_t item = 0U; item < 8U; ++item) {
                rows[row][item] -= factor * rows[column][item];
            }
        }
    }
    auto* destination = &inverse.m11;
    for (std::size_t row = 0U; row < 4U; ++row) {
        for (std::size_t column = 0U; column < 4U; ++column) {
            const double value = rows[row][4U + column];
            if (!finite_double_as_float(value)) {
                return false;
            }
            destination[row * 4U + column] = static_cast<float>(value);
        }
    }
    return finite_matrix_4x4(inverse);
}

bool try_transform_origin(
    const progpu_native_matrix_4x4& transform,
    progpu_native_point_3d& point) noexcept {
    const float w = transform.m44;
    if (!std::isfinite(w) || w == 0.0F) {
        return false;
    }
    point = {
        transform.m41 / w,
        transform.m42 / w,
        transform.m43 / w,
        0.0F};
    return std::isfinite(point.x) && std::isfinite(point.y) &&
        std::isfinite(point.z);
}

bool try_create_look_at_rh(
    const std::array<float, 3U>& position,
    const std::array<float, 3U>& look_direction,
    const std::array<float, 3U>& up_direction,
    progpu_native_matrix_4x4& view) noexcept {
    const auto normalize = [](std::array<double, 3U>& value) noexcept {
        const double length_squared = value[0] * value[0] +
            value[1] * value[1] + value[2] * value[2];
        if (!(length_squared > 0.0) || !std::isfinite(length_squared)) {
            return false;
        }
        const double inverse_length = 1.0 / std::sqrt(length_squared);
        value[0] *= inverse_length;
        value[1] *= inverse_length;
        value[2] *= inverse_length;
        return std::ranges::all_of(
            value,
            [](double component) noexcept {
                return finite_double_as_float(component);
            });
    };
    std::array<double, 3U> z{
        -static_cast<double>(look_direction[0]),
        -static_cast<double>(look_direction[1]),
        -static_cast<double>(look_direction[2])};
    if (!normalize(z)) {
        return false;
    }
    std::array<double, 3U> x{
        static_cast<double>(up_direction[1]) * z[2] -
            static_cast<double>(up_direction[2]) * z[1],
        static_cast<double>(up_direction[2]) * z[0] -
            static_cast<double>(up_direction[0]) * z[2],
        static_cast<double>(up_direction[0]) * z[1] -
            static_cast<double>(up_direction[1]) * z[0]};
    if (!normalize(x)) {
        return false;
    }
    const std::array<double, 3U> y{
        z[1] * x[2] - z[2] * x[1],
        z[2] * x[0] - z[0] * x[2],
        z[0] * x[1] - z[1] * x[0]};
    const auto dot_position = [&](const std::array<double, 3U>& axis) {
        return axis[0] * position[0] + axis[1] * position[1] +
            axis[2] * position[2];
    };
    view = {
        static_cast<float>(x[0]), static_cast<float>(y[0]),
            static_cast<float>(z[0]), 0.0F,
        static_cast<float>(x[1]), static_cast<float>(y[1]),
            static_cast<float>(z[1]), 0.0F,
        static_cast<float>(x[2]), static_cast<float>(y[2]),
            static_cast<float>(z[2]), 0.0F,
        static_cast<float>(-dot_position(x)),
            static_cast<float>(-dot_position(y)),
            static_cast<float>(-dot_position(z)), 1.0F};
    return finite_matrix_4x4(view);
}

template<typename T>
void append_fnv1a64(std::uint64_t& hash, const T& value) noexcept {
    static_assert(std::is_trivially_copyable_v<T>);
    const auto* bytes = reinterpret_cast<const unsigned char*>(&value);
    for (std::size_t index = 0U; index < sizeof(T); ++index) {
        hash ^= bytes[index];
        hash *= 1099511628211ULL;
    }
}

std::uint64_t finish_nonzero_hash(std::uint64_t hash) noexcept {
    return hash == 0U ? 1U : hash;
}

struct affine_2d_double {
    double m11{1.0};
    double m12{};
    double m21{};
    double m22{1.0};
    double m31{};
    double m32{};
};

affine_2d_double compose_affine(
    const affine_2d_double& first,
    const affine_2d_double& second) noexcept {
    return {
        first.m11 * second.m11 + first.m12 * second.m21,
        first.m11 * second.m12 + first.m12 * second.m22,
        first.m21 * second.m11 + first.m22 * second.m21,
        first.m21 * second.m12 + first.m22 * second.m22,
        first.m31 * second.m11 + first.m32 * second.m21 + second.m31,
        first.m31 * second.m12 + first.m32 * second.m22 + second.m32};
}

bool affine_preserves_axis_alignment(
    const affine_2d_double& transform) noexcept {
    return (transform.m12 == 0.0 && transform.m21 == 0.0) ||
        (transform.m11 == 0.0 && transform.m22 == 0.0);
}

bool affine_has_zero_area(const affine_2d_double& transform) noexcept {
    const double determinant =
        transform.m11 * transform.m22 - transform.m12 * transform.m21;
    return std::isfinite(determinant) && determinant == 0.0;
}

bool try_invert_affine(
    const affine_2d_double& source,
    affine_2d_double& inverse) noexcept {
    const double determinant =
        source.m11 * source.m22 - source.m12 * source.m21;
    if (!std::isfinite(determinant) || determinant == 0.0) {
        return false;
    }
    const double reciprocal = 1.0 / determinant;
    inverse = {
        source.m22 * reciprocal,
        -source.m12 * reciprocal,
        -source.m21 * reciprocal,
        source.m11 * reciprocal,
        (source.m21 * source.m32 - source.m31 * source.m22) * reciprocal,
        (source.m31 * source.m12 - source.m11 * source.m32) * reciprocal};
    return finite_double_as_float(inverse.m11) &&
        finite_double_as_float(inverse.m12) &&
        finite_double_as_float(inverse.m21) &&
        finite_double_as_float(inverse.m22) &&
        finite_double_as_float(inverse.m31) &&
        finite_double_as_float(inverse.m32);
}

float linear_to_srgb(float value) noexcept {
    if (value <= 0.0F) {
        return 0.0F;
    }
    if (value <= 0.0031308F) {
        return value * 12.92F;
    }
    return 1.055F * std::pow(value, 1.0F / 2.4F) - 0.055F;
}

progpu_native_color sc_rgb_to_s_rgb(progpu_native_color color) noexcept {
    color.r = linear_to_srgb(color.r);
    color.g = linear_to_srgb(color.g);
    color.b = linear_to_srgb(color.b);
    return color;
}

progpu_native_color interpolate_color(
    progpu_native_color first,
    progpu_native_color second,
    float factor) noexcept {
    return {
        first.r + (second.r - first.r) * factor,
        first.g + (second.g - first.g) * factor,
        first.b + (second.b - first.b) * factor,
        first.a + (second.a - first.a) * factor};
}

bool try_to_native_affine(
    const affine_2d_double& source,
    progpu_native_affine_2d& destination) noexcept {
    if (!finite_double_as_float(source.m11) ||
        !finite_double_as_float(source.m12) ||
        !finite_double_as_float(source.m21) ||
        !finite_double_as_float(source.m22) ||
        !finite_double_as_float(source.m31) ||
        !finite_double_as_float(source.m32)) {
        return false;
    }
    destination = {
        static_cast<float>(source.m11),
        static_cast<float>(source.m12),
        static_cast<float>(source.m21),
        static_cast<float>(source.m22),
        static_cast<float>(source.m31),
        static_cast<float>(source.m32)};
    return true;
}

bool try_quantize_wpf_affine(affine_2d_double& transform) noexcept {
    progpu_native_affine_2d native{};
    if (!try_to_native_affine(transform, native)) {
        return false;
    }
    transform = {
        native.m11,
        native.m12,
        native.m21,
        native.m22,
        native.m31,
        native.m32};
    return true;
}

affine_2d_double compose_wpf_affine(
    const affine_2d_double& first,
    const affine_2d_double& second) noexcept {
    const float first_m11 = static_cast<float>(first.m11);
    const float first_m12 = static_cast<float>(first.m12);
    const float first_m21 = static_cast<float>(first.m21);
    const float first_m22 = static_cast<float>(first.m22);
    const float first_m31 = static_cast<float>(first.m31);
    const float first_m32 = static_cast<float>(first.m32);
    const float second_m11 = static_cast<float>(second.m11);
    const float second_m12 = static_cast<float>(second.m12);
    const float second_m21 = static_cast<float>(second.m21);
    const float second_m22 = static_cast<float>(second.m22);
    const float second_m31 = static_cast<float>(second.m31);
    const float second_m32 = static_cast<float>(second.m32);
    return {
        first_m11 * second_m11 + first_m12 * second_m21,
        first_m11 * second_m12 + first_m12 * second_m22,
        first_m21 * second_m11 + first_m22 * second_m21,
        first_m21 * second_m12 + first_m22 * second_m22,
        first_m31 * second_m11 + first_m32 * second_m21 + second_m31,
        first_m31 * second_m12 + first_m32 * second_m22 + second_m32};
}

bool try_transform_bounds(
    double x,
    double y,
    double width,
    double height,
    const affine_2d_double& transform,
    progpu_native_image_rect& bounds) noexcept {
    const auto transform_point = [&transform](
        double point_x,
        double point_y,
        double& result_x,
        double& result_y) noexcept {
        result_x = point_x * transform.m11 +
            point_y * transform.m21 + transform.m31;
        result_y = point_x * transform.m12 +
            point_y * transform.m22 + transform.m32;
    };
    std::array<double, 4U> xs{};
    std::array<double, 4U> ys{};
    transform_point(x, y, xs[0], ys[0]);
    transform_point(x + width, y, xs[1], ys[1]);
    transform_point(x + width, y + height, xs[2], ys[2]);
    transform_point(x, y + height, xs[3], ys[3]);
    const auto [minimum_x, maximum_x] =
        std::ranges::minmax_element(xs);
    const auto [minimum_y, maximum_y] =
        std::ranges::minmax_element(ys);
    const double transformed_width = *maximum_x - *minimum_x;
    const double transformed_height = *maximum_y - *minimum_y;
    if (!finite_double_as_float(*minimum_x) ||
        !finite_double_as_float(*minimum_y) ||
        !finite_double_as_float(transformed_width) ||
        !finite_double_as_float(transformed_height) ||
        transformed_width < 0.0 || transformed_height < 0.0) {
        return false;
    }
    bounds = {
        static_cast<float>(*minimum_x),
        static_cast<float>(*minimum_y),
        static_cast<float>(transformed_width),
        static_cast<float>(transformed_height)};
    return true;
}

bool try_fixed_shape_stroke_bounds(
    double x,
    double y,
    double width,
    double height,
    double thickness,
    const affine_2d_double& transform,
    progpu_native_image_rect& bounds) noexcept {
    if (width < 0.0 || height < 0.0 || thickness <= 0.0) {
        return false;
    }
    const double half_thickness = thickness * 0.5;
    return try_transform_bounds(
        x - half_thickness,
        y - half_thickness,
        width + thickness,
        height + thickness,
        transform,
        bounds);
}

bool try_transformed_cubic_contour_stroke_bounds(
    std::span<const std::array<std::array<double, 2U>, 4U>> source_cubics,
    std::span<const bool> line_segments,
    double thickness,
    const affine_2d_double& geometry_transform,
    const affine_2d_double& world_transform,
    progpu_native_image_rect& bounds) noexcept {
    if (source_cubics.empty() ||
        (!line_segments.empty() &&
            line_segments.size() != source_cubics.size()) ||
        thickness <= 0.0) {
        return false;
    }
    using point = std::array<double, 2U>;
    const auto map_point = [](
        const point& source,
        const affine_2d_double& transform,
        point& destination) noexcept {
        destination = {
            source[0U] * transform.m11 +
                source[1U] * transform.m21 + transform.m31,
            source[0U] * transform.m12 +
                source[1U] * transform.m22 + transform.m32};
        return std::isfinite(destination[0U]) &&
            std::isfinite(destination[1U]);
    };
    double minimum_x = std::numeric_limits<double>::infinity();
    double minimum_y = std::numeric_limits<double>::infinity();
    double maximum_x = -std::numeric_limits<double>::infinity();
    double maximum_y = -std::numeric_limits<double>::infinity();
    const double pen_radius = thickness * 0.5;
    constexpr double default_tolerance = 0.25;
    const double metric_xx = world_transform.m11 * world_transform.m11 +
        world_transform.m12 * world_transform.m12;
    const double metric_xy = world_transform.m11 * world_transform.m21 +
        world_transform.m12 * world_transform.m22;
    const double metric_yy = world_transform.m21 * world_transform.m21 +
        world_transform.m22 * world_transform.m22;
    const double half_metric_difference = (metric_xx - metric_yy) * 0.5;
    const double maximum_eigenvalue =
        (metric_xx + metric_yy) * 0.5 +
        std::hypot(half_metric_difference, metric_xy);
    if (!std::isfinite(maximum_eigenvalue) || maximum_eigenvalue <= 0.0) {
        return false;
    }
    const double maximum_radius_bound =
        pen_radius * std::sqrt(maximum_eigenvalue);
    if (!std::isfinite(maximum_radius_bound) ||
        maximum_radius_bound <= 0.0) {
        return false;
    }
    const double refinement_threshold =
        maximum_radius_bound < default_tolerance
        ? -2.0
        : 2.0 * (1.0 - default_tolerance / maximum_radius_bound) *
                (1.0 - default_tolerance / maximum_radius_bound) -
            1.0;
    const auto include_mapped = [
        &minimum_x,
        &minimum_y,
        &maximum_x,
        &maximum_y](const point& mapped) noexcept {
        if (!std::isfinite(mapped[0U]) || !std::isfinite(mapped[1U])) {
            return false;
        }
        minimum_x = std::min(minimum_x, mapped[0U]);
        minimum_y = std::min(minimum_y, mapped[1U]);
        maximum_x = std::max(maximum_x, mapped[0U]);
        maximum_y = std::max(maximum_y, mapped[1U]);
        return true;
    };
    const auto include_world = [
        &map_point,
        &world_transform,
        &include_mapped](const point& source) noexcept {
        point mapped{};
        return map_point(source, world_transform, mapped) &&
            include_mapped(mapped);
    };
    const auto include_cubic = [
        &map_point,
        &world_transform,
        &include_mapped](
        const point& p0,
        const point& p1,
        const point& p2,
        const point& p3) noexcept {
        std::array<point, 4U> mapped{};
        if (!map_point(p0, world_transform, mapped[0U]) ||
            !map_point(p1, world_transform, mapped[1U]) ||
            !map_point(p2, world_transform, mapped[2U]) ||
            !map_point(p3, world_transform, mapped[3U])) {
            return false;
        }
        const auto include_parameter = [
            &mapped,
            &include_mapped](double t) noexcept {
            if (t <= 0.0 || t >= 1.0) {
                return true;
            }
            const double inverse = 1.0 - t;
            return include_mapped({
                inverse * inverse * inverse * mapped[0U][0U] +
                    3.0 * inverse * inverse * t * mapped[1U][0U] +
                    3.0 * inverse * t * t * mapped[2U][0U] +
                    t * t * t * mapped[3U][0U],
                inverse * inverse * inverse * mapped[0U][1U] +
                    3.0 * inverse * inverse * t * mapped[1U][1U] +
                    3.0 * inverse * t * t * mapped[2U][1U] +
                    t * t * t * mapped[3U][1U]});
        };
        const auto include_axis_extrema = [
            &mapped,
            &include_parameter](std::size_t axis) noexcept {
            const double p0_axis = mapped[0U][axis];
            const double p1_axis = mapped[1U][axis];
            const double p2_axis = mapped[2U][axis];
            const double p3_axis = mapped[3U][axis];
            const double quadratic = 3.0 *
                (-p0_axis + 3.0 * p1_axis - 3.0 * p2_axis + p3_axis);
            const double linear = 6.0 *
                (p0_axis - 2.0 * p1_axis + p2_axis);
            const double constant = 3.0 * (p1_axis - p0_axis);
            if (quadratic == 0.0) {
                return linear == 0.0 ||
                    include_parameter(-constant / linear);
            }
            const double discriminant = linear * linear -
                4.0 * quadratic * constant;
            if (discriminant < 0.0) {
                return true;
            }
            const double root = std::sqrt(discriminant);
            const double denominator = 2.0 * quadratic;
            return include_parameter((-linear - root) / denominator) &&
                include_parameter((-linear + root) / denominator);
        };
        return include_mapped(mapped[0U]) &&
            include_mapped(mapped[3U]) &&
            include_axis_extrema(0U) && include_axis_extrema(1U);
    };
    const auto bezier_distance = [pen_radius](double dot) noexcept {
        const double radius_squared = pen_radius * pen_radius;
        const double a = std::max(0.0, 0.5 * (radius_squared + dot));
        const double denominator_squared = radius_squared - a;
        if (denominator_squared <= 0.0) {
            return 0.0;
        }
        const double denominator = std::sqrt(denominator_squared);
        const double numerator = (4.0 / 3.0) *
            (pen_radius - std::sqrt(a));
        return numerator <= denominator * 0.000001
            ? 0.0
            : numerator / denominator;
    };
    const auto include_round_to = [
        pen_radius,
        refinement_threshold,
        &bezier_distance,
        &include_world,
        &include_cubic](
        const point& center,
        const point& incoming,
        const point& outgoing) noexcept {
        const double turn = incoming[0U] * outgoing[1U] -
            incoming[1U] * outgoing[0U];
        const double direction_dot = incoming[0U] * outgoing[0U] +
            incoming[1U] * outgoing[1U];
        if (!std::isfinite(turn) || !std::isfinite(direction_dot) ||
            std::abs(turn) <= 0.0001) {
            return direction_dot > 0.0;
        }
        const double side_sign = turn > 0.0 ? -1.0 : 1.0;
        const point start{
            center[0U] - incoming[1U] * side_sign * pen_radius,
            center[1U] + incoming[0U] * side_sign * pen_radius};
        const point end{
            center[0U] - outgoing[1U] * side_sign * pen_radius,
            center[1U] + outgoing[0U] * side_sign * pen_radius};
        if (direction_dot > refinement_threshold) {
            return include_world(end);
        }
        if (direction_dot >= 0.0) {
            const double distance = bezier_distance(
                direction_dot * pen_radius * pen_radius);
            return include_cubic(
                start,
                {start[0U] + incoming[0U] * pen_radius * distance,
                    start[1U] + incoming[1U] * pen_radius * distance},
                {end[0U] - outgoing[0U] * pen_radius * distance,
                    end[1U] - outgoing[1U] * pen_radius * distance},
                end);
        }
        const point tangent_sum{
            incoming[0U] + outgoing[0U],
            incoming[1U] + outgoing[1U]};
        const double tangent_sum_length = std::hypot(
            tangent_sum[0U], tangent_sum[1U]);
        const point radial_sum{
            start[0U] + end[0U] - 2.0 * center[0U],
            start[1U] + end[1U] - 2.0 * center[1U]};
        const double radial_sum_length = std::hypot(
            radial_sum[0U], radial_sum[1U]);
        if (!std::isfinite(tangent_sum_length) ||
            !std::isfinite(radial_sum_length) ||
            tangent_sum_length <= 0.0001 || radial_sum_length <= 0.0001) {
            return false;
        }
        const point middle_tangent{
            tangent_sum[0U] / tangent_sum_length,
            tangent_sum[1U] / tangent_sum_length};
        const point middle{
            center[0U] + radial_sum[0U] / radial_sum_length * pen_radius,
            center[1U] + radial_sum[1U] / radial_sum_length * pen_radius};
        const double half_dot = std::abs(
            outgoing[0U] * middle_tangent[0U] +
            outgoing[1U] * middle_tangent[1U]);
        const double distance = bezier_distance(
            half_dot * pen_radius * pen_radius);
        return include_cubic(
                   start,
                   {start[0U] + incoming[0U] * pen_radius * distance,
                       start[1U] + incoming[1U] * pen_radius * distance},
                   {middle[0U] -
                           middle_tangent[0U] * pen_radius * distance,
                       middle[1U] -
                           middle_tangent[1U] * pen_radius * distance},
                   middle) &&
            include_cubic(
                middle,
                {middle[0U] +
                        middle_tangent[0U] * pen_radius * distance,
                    middle[1U] +
                        middle_tangent[1U] * pen_radius * distance},
                {end[0U] - outgoing[0U] * pen_radius * distance,
                    end[1U] - outgoing[1U] * pen_radius * distance},
                end);
    };
    const auto include_offset_pair = [
        pen_radius,
        &include_world](const point& position, const point& direction) noexcept {
        const point offset{
            -direction[1U] * pen_radius,
            direction[0U] * pen_radius};
        return include_world({position[0U] - offset[0U],
                   position[1U] - offset[1U]}) &&
            include_world({position[0U] + offset[0U],
                position[1U] + offset[1U]});
    };
    point previous_direction{};
    point previous_position{};
    bool has_previous_point = false;
    const auto include_curve_point = [
        refinement_threshold,
        &previous_direction,
        &previous_position,
        &has_previous_point,
        &include_round_to,
        &include_offset_pair](
        const point& position,
        const point& tangent,
        bool is_last) noexcept {
        const double tangent_length = std::hypot(tangent[0U], tangent[1U]);
        if (!std::isfinite(tangent_length) || tangent_length <= 0.000001) {
            return false;
        }
        const point direction{
            tangent[0U] / tangent_length,
            tangent[1U] / tangent_length};
        if (!has_previous_point) {
            previous_position = position;
            previous_direction = direction;
            has_previous_point = true;
            return include_offset_pair(position, direction);
        }
        const double direction_dot =
            previous_direction[0U] * direction[0U] +
            previous_direction[1U] * direction[1U];
        if (!std::isfinite(direction_dot)) {
            return false;
        }
        if (direction_dot < refinement_threshold) {
            const point chord{
                position[0U] - previous_position[0U],
                position[1U] - previous_position[1U]};
            const double chord_length = std::hypot(chord[0U], chord[1U]);
            if (!std::isfinite(chord_length) || chord_length <= 0.000001) {
                return false;
            }
            const point chord_direction{
                chord[0U] / chord_length,
                chord[1U] / chord_length};
            if (!include_round_to(
                    previous_position,
                    previous_direction,
                    chord_direction) ||
                !include_offset_pair(position, chord_direction) ||
                !include_round_to(
                    position,
                    chord_direction,
                    direction) ||
                (is_last && !include_offset_pair(position, direction))) {
                return false;
            }
        } else if (!include_offset_pair(position, direction)) {
            return false;
        }
        previous_position = position;
        previous_direction = direction;
        return true;
    };
    constexpr double flattened_tolerance = default_tolerance * 6.0;
    constexpr double quarter_tolerance = flattened_tolerance * 0.25;
    constexpr double twice_minimum_step = 0.001;
    for (std::size_t source_index = 0U;
         source_index < source_cubics.size();
         ++source_index) {
        const auto& source_cubic = source_cubics[source_index];
        std::array<point, 4U> cubic{};
        for (std::size_t index = 0U; index < cubic.size(); ++index) {
            if (!map_point(
                    source_cubic[index],
                    geometry_transform,
                    cubic[index])) {
                return false;
            }
        }
        if (!line_segments.empty() && line_segments[source_index]) {
            const point tangent{
                cubic[3U][0U] - cubic[0U][0U],
                cubic[3U][1U] - cubic[0U][1U]};
            const double tangent_length = std::hypot(
                tangent[0U], tangent[1U]);
            if (!std::isfinite(tangent_length) ||
                tangent_length <= 0.000001) {
                return false;
            }
            if (!has_previous_point) {
                previous_position = cubic[0U];
                previous_direction = {
                    tangent[0U] / tangent_length,
                    tangent[1U] / tangent_length};
                has_previous_point = true;
                if (!include_offset_pair(
                        previous_position, previous_direction)) {
                    return false;
                }
            }
            if (!include_offset_pair(cubic[3U], previous_direction)) {
                return false;
            }
            previous_position = cubic[3U];
            continue;
        }
        std::array<point, 4U> flatness_cubic{};
        for (std::size_t index = 0U;
             index < flatness_cubic.size();
             ++index) {
            if (!map_point(
                    cubic[index],
                    world_transform,
                    flatness_cubic[index])) {
                return false;
            }
        }
        point e0 = cubic[0U];
        point e1{
            cubic[3U][0U] - cubic[0U][0U],
            cubic[3U][1U] - cubic[0U][1U]};
        point e2{
            6.0 * (cubic[1U][0U] - 2.0 * cubic[2U][0U] +
                cubic[3U][0U]),
            6.0 * (cubic[1U][1U] - 2.0 * cubic[2U][1U] +
                cubic[3U][1U])};
        point e3{
            6.0 * (cubic[0U][0U] - 2.0 * cubic[1U][0U] +
                cubic[2U][0U]),
            6.0 * (cubic[0U][1U] - 2.0 * cubic[1U][1U] +
                cubic[2U][1U])};
        point flat_e1{
            flatness_cubic[3U][0U] - flatness_cubic[0U][0U],
            flatness_cubic[3U][1U] - flatness_cubic[0U][1U]};
        point flat_e2{
            6.0 * (flatness_cubic[1U][0U] -
                2.0 * flatness_cubic[2U][0U] +
                flatness_cubic[3U][0U]),
            6.0 * (flatness_cubic[1U][1U] -
                2.0 * flatness_cubic[2U][1U] +
                flatness_cubic[3U][1U])};
        point flat_e3{
            6.0 * (flatness_cubic[0U][0U] -
                2.0 * flatness_cubic[1U][0U] +
                flatness_cubic[2U][0U]),
            6.0 * (flatness_cubic[0U][1U] -
                2.0 * flatness_cubic[1U][1U] +
                flatness_cubic[2U][1U])};
        const auto approximate_norm = [](const point& value) noexcept {
            return std::max(std::abs(value[0U]), std::abs(value[1U]));
        };
        std::uint32_t step_count = 1U;
        double step_size = 1.0;
        const auto halve_step = [
            &e1,
            &e2,
            &e3,
            &flat_e1,
            &flat_e2,
            &flat_e3,
            &step_count,
            &step_size]() noexcept {
            e2 = {(e2[0U] + e3[0U]) * 0.125,
                (e2[1U] + e3[1U]) * 0.125};
            e1 = {(e1[0U] - e2[0U]) * 0.5,
                (e1[1U] - e2[1U]) * 0.5};
            e3 = {e3[0U] * 0.25, e3[1U] * 0.25};
            flat_e2 = {(flat_e2[0U] + flat_e3[0U]) * 0.125,
                (flat_e2[1U] + flat_e3[1U]) * 0.125};
            flat_e1 = {(flat_e1[0U] - flat_e2[0U]) * 0.5,
                (flat_e1[1U] - flat_e2[1U]) * 0.5};
            flat_e3 = {
                flat_e3[0U] * 0.25, flat_e3[1U] * 0.25};
            step_count *= 2U;
            step_size *= 0.5;
        };
        while ((approximate_norm(flat_e2) > flattened_tolerance ||
                approximate_norm(flat_e3) > flattened_tolerance) &&
            step_size > twice_minimum_step) {
            halve_step();
        }
        const point first_tangent{
            cubic[1U][0U] - cubic[0U][0U],
            cubic[1U][1U] - cubic[0U][1U]};
        if (!include_curve_point(cubic[0U], first_tangent, false)) {
            return false;
        }
        while (step_count > 1U) {
            e0 = {e0[0U] + e1[0U], e0[1U] + e1[1U]};
            const point previous_e2 = e2;
            e1 = {e1[0U] + previous_e2[0U],
                e1[1U] + previous_e2[1U]};
            e2 = {2.0 * e2[0U] - e3[0U],
                2.0 * e2[1U] - e3[1U]};
            e3 = previous_e2;
            const point previous_flat_e2 = flat_e2;
            flat_e1 = {flat_e1[0U] + previous_flat_e2[0U],
                flat_e1[1U] + previous_flat_e2[1U]};
            flat_e2 = {2.0 * flat_e2[0U] - flat_e3[0U],
                2.0 * flat_e2[1U] - flat_e3[1U]};
            flat_e3 = previous_flat_e2;
            const point tangent{
                6.0 * e1[0U] - e2[0U] - 2.0 * e3[0U],
                6.0 * e1[1U] - e2[1U] - 2.0 * e3[1U]};
            if (!include_curve_point(e0, tangent, false)) {
                return false;
            }
            --step_count;
            if (approximate_norm(flat_e2) > flattened_tolerance &&
                step_size > twice_minimum_step) {
                halve_step();
                continue;
            }
            while ((step_count & 1U) == 0U) {
                const point candidate{
                    2.0 * e2[0U] - e3[0U],
                    2.0 * e2[1U] - e3[1U]};
                const point flat_candidate{
                    2.0 * flat_e2[0U] - flat_e3[0U],
                    2.0 * flat_e2[1U] - flat_e3[1U]};
                if (approximate_norm(flat_e3) > quarter_tolerance ||
                    approximate_norm(flat_candidate) > quarter_tolerance) {
                    break;
                }
                e1 = {2.0 * e1[0U] + e2[0U],
                    2.0 * e1[1U] + e2[1U]};
                e3 = {4.0 * e3[0U], 4.0 * e3[1U]};
                e2 = {4.0 * candidate[0U], 4.0 * candidate[1U]};
                flat_e1 = {
                    2.0 * flat_e1[0U] + flat_e2[0U],
                    2.0 * flat_e1[1U] + flat_e2[1U]};
                flat_e3 = {
                    4.0 * flat_e3[0U], 4.0 * flat_e3[1U]};
                flat_e2 = {4.0 * flat_candidate[0U],
                    4.0 * flat_candidate[1U]};
                step_count /= 2U;
                step_size *= 2.0;
            }
        }
        const point last_tangent{
            cubic[3U][0U] - cubic[2U][0U],
            cubic[3U][1U] - cubic[2U][1U]};
        if (!include_curve_point(cubic[3U], last_tangent, true)) {
            return false;
        }
    }
    const double bounds_width = maximum_x - minimum_x;
    const double bounds_height = maximum_y - minimum_y;
    if (!finite_double_as_float(minimum_x) ||
        !finite_double_as_float(minimum_y) ||
        !finite_double_as_float(bounds_width) ||
        !finite_double_as_float(bounds_height) ||
        bounds_width <= 0.0 || bounds_height <= 0.0) {
        return false;
    }
    bounds = {
        static_cast<float>(minimum_x),
        static_cast<float>(minimum_y),
        static_cast<float>(bounds_width),
        static_cast<float>(bounds_height)};
    return true;
}

bool try_transformed_ellipse_stroke_bounds(
    double center_x,
    double center_y,
    double radius_x,
    double radius_y,
    double thickness,
    const affine_2d_double& geometry_transform,
    const affine_2d_double& world_transform,
    progpu_native_image_rect& bounds) noexcept {
    using point = std::array<double, 2U>;
    if (radius_x <= 0.0 || radius_y <= 0.0 || thickness <= 0.0) {
        return false;
    }
    const float resolved_center_x = static_cast<float>(center_x);
    const float resolved_center_y = static_cast<float>(center_y);
    const float resolved_radius_x = std::abs(static_cast<float>(radius_x));
    const float resolved_radius_y = std::abs(static_cast<float>(radius_y));
    if (!std::isfinite(resolved_center_x) ||
        !std::isfinite(resolved_center_y) ||
        !std::isfinite(resolved_radius_x) ||
        !std::isfinite(resolved_radius_y) ||
        resolved_radius_x <= 0.0F || resolved_radius_y <= 0.0F) {
        return false;
    }
    constexpr double arc_as_bezier = 0.5522847498307933984;
    const float middle_x = static_cast<float>(
        static_cast<double>(resolved_radius_x) * arc_as_bezier);
    const float middle_y = static_cast<float>(
        static_cast<double>(resolved_radius_y) * arc_as_bezier);
    const float left = resolved_center_x - resolved_radius_x;
    const float right = resolved_center_x + resolved_radius_x;
    const float top = resolved_center_y - resolved_radius_y;
    const float bottom = resolved_center_y + resolved_radius_y;
    const std::array<std::array<point, 4U>, 4U> source_cubics{{
        {{{right, resolved_center_y},
          {right, resolved_center_y + middle_y},
          {resolved_center_x + middle_x, bottom},
          {resolved_center_x, bottom}}},
        {{{resolved_center_x, bottom},
          {resolved_center_x - middle_x, bottom},
          {left, resolved_center_y + middle_y},
          {left, resolved_center_y}}},
        {{{left, resolved_center_y},
          {left, resolved_center_y - middle_y},
          {resolved_center_x - middle_x, top},
          {resolved_center_x, top}}},
        {{{resolved_center_x, top},
          {resolved_center_x + middle_x, top},
          {right, resolved_center_y - middle_y},
          {right, resolved_center_y}}}}};
    return try_transformed_cubic_contour_stroke_bounds(
        source_cubics,
        {},
        thickness,
        geometry_transform,
        world_transform,
        bounds);
}

bool try_transformed_rounded_rectangle_stroke_bounds(
    double x,
    double y,
    double width,
    double height,
    double radius_x,
    double radius_y,
    double thickness,
    const affine_2d_double& geometry_transform,
    const affine_2d_double& world_transform,
    progpu_native_image_rect& bounds) noexcept {
    using point = std::array<double, 2U>;
    if (width <= 0.0 || height <= 0.0 || radius_x <= 0.0 ||
        radius_y <= 0.0 || thickness <= 0.0) {
        return false;
    }
    const double left = x;
    const double top = y;
    const double right = x + width;
    const double bottom = y + height;
    const double resolved_radius_x = std::min(
        std::abs(radius_x), (right - left) * 0.5);
    const double resolved_radius_y = std::min(
        std::abs(radius_y), (bottom - top) * 0.5);
    if (!std::isfinite(left) || !std::isfinite(top) ||
        !std::isfinite(right) || !std::isfinite(bottom) ||
        !std::isfinite(resolved_radius_x) ||
        !std::isfinite(resolved_radius_y) ||
        right <= left || bottom <= top ||
        resolved_radius_x <= 0.0 || resolved_radius_y <= 0.0) {
        return false;
    }
    constexpr double arc_as_bezier = 0.5522847498307933984;
    const double bezier_x = (1.0 - arc_as_bezier) * resolved_radius_x;
    const double bezier_y = (1.0 - arc_as_bezier) * resolved_radius_y;
    const auto line_cubic = [](point start, point end) noexcept {
        return std::array<point, 4U>{
            start,
            point{start[0U] + (end[0U] - start[0U]) / 3.0,
                start[1U] + (end[1U] - start[1U]) / 3.0},
            point{start[0U] + (end[0U] - start[0U]) * (2.0 / 3.0),
                start[1U] + (end[1U] - start[1U]) * (2.0 / 3.0)},
            end};
    };
    const point left_start{left, top + resolved_radius_y};
    const point top_left{left + resolved_radius_x, top};
    const point top_right{right - resolved_radius_x, top};
    const point right_start{right, top + resolved_radius_y};
    const point right_end{right, bottom - resolved_radius_y};
    const point bottom_right{right - resolved_radius_x, bottom};
    const point bottom_left{left + resolved_radius_x, bottom};
    const point left_end{left, bottom - resolved_radius_y};
    const std::array<std::array<point, 4U>, 8U> source_cubics{{
        {{left_start,
          {left, top + bezier_y},
          {left + bezier_x, top},
          top_left}},
        line_cubic(top_left, top_right),
        {{top_right,
          {right - bezier_x, top},
          {right, top + bezier_y},
          right_start}},
        line_cubic(right_start, right_end),
        {{right_end,
          {right, bottom - bezier_y},
          {right - bezier_x, bottom},
          bottom_right}},
        line_cubic(bottom_right, bottom_left),
        {{bottom_left,
          {left + bezier_x, bottom},
          {left, bottom - bezier_y},
          left_end}},
        line_cubic(left_end, left_start)}};
    constexpr std::array<bool, 8U> line_segments{
        false, true, false, true, false, true, false, true};
    return try_transformed_cubic_contour_stroke_bounds(
        source_cubics,
        line_segments,
        thickness,
        geometry_transform,
        world_transform,
        bounds);
}

bool try_transformed_rectangle_stroke_bounds(
    double x,
    double y,
    double width,
    double height,
    double thickness,
    std::uint32_t line_join,
    double miter_limit,
    const affine_2d_double& geometry_transform,
    const affine_2d_double& world_transform,
    progpu_native_image_rect& bounds) noexcept {
    if (width <= 0.0 || height <= 0.0 || thickness <= 0.0 ||
        line_join > PROGPU_NATIVE_STROKE_JOIN_ROUND) {
        return false;
    }
    using point = std::array<double, 2U>;
    const auto map_point = [](
        const point& source,
        const affine_2d_double& transform,
        point& destination) noexcept {
        destination = {
            source[0U] * transform.m11 +
                source[1U] * transform.m21 + transform.m31,
            source[0U] * transform.m12 +
                source[1U] * transform.m22 + transform.m32};
        return std::isfinite(destination[0U]) &&
            std::isfinite(destination[1U]);
    };
    std::array<point, 4U> vertices{
        point{x, y},
        point{x + width, y},
        point{x + width, y + height},
        point{x, y + height}};
    for (auto& vertex : vertices) {
        point transformed{};
        if (!map_point(vertex, geometry_transform, transformed)) {
            return false;
        }
        vertex = transformed;
    }
    std::array<point, 4U> directions{};
    for (std::size_t index = 0U; index < vertices.size(); ++index) {
        const point& start = vertices[index];
        const point& end = vertices[(index + 1U) % vertices.size()];
        const double delta_x = end[0U] - start[0U];
        const double delta_y = end[1U] - start[1U];
        const double length = std::hypot(delta_x, delta_y);
        if (!std::isfinite(length) || length <= 0.0) {
            return false;
        }
        directions[index] = {delta_x / length, delta_y / length};
    }
    double minimum_x = std::numeric_limits<double>::infinity();
    double minimum_y = std::numeric_limits<double>::infinity();
    double maximum_x = -std::numeric_limits<double>::infinity();
    double maximum_y = -std::numeric_limits<double>::infinity();
    const auto include_mapped = [
        &minimum_x,
        &minimum_y,
        &maximum_x,
        &maximum_y](const point& mapped) noexcept {
        if (!std::isfinite(mapped[0U]) || !std::isfinite(mapped[1U])) {
            return false;
        }
        minimum_x = std::min(minimum_x, mapped[0U]);
        minimum_y = std::min(minimum_y, mapped[1U]);
        maximum_x = std::max(maximum_x, mapped[0U]);
        maximum_y = std::max(maximum_y, mapped[1U]);
        return true;
    };
    const auto include = [
        &map_point,
        &world_transform,
        &include_mapped](const point& source) noexcept {
        point transformed{};
        if (!map_point(source, world_transform, transformed)) {
            return false;
        }
        return include_mapped(transformed);
    };
    const auto include_cubic = [
        &map_point,
        &world_transform,
        &include_mapped](
        const point& p0,
        const point& p1,
        const point& p2,
        const point& p3) noexcept {
        std::array<point, 4U> mapped{};
        if (!map_point(p0, world_transform, mapped[0U]) ||
            !map_point(p1, world_transform, mapped[1U]) ||
            !map_point(p2, world_transform, mapped[2U]) ||
            !map_point(p3, world_transform, mapped[3U])) {
            return false;
        }
        const auto evaluate = [&mapped](double t, point& result) noexcept {
            const double inverse = 1.0 - t;
            result = {
                inverse * inverse * inverse * mapped[0U][0U] +
                    3.0 * inverse * inverse * t * mapped[1U][0U] +
                    3.0 * inverse * t * t * mapped[2U][0U] +
                    t * t * t * mapped[3U][0U],
                inverse * inverse * inverse * mapped[0U][1U] +
                    3.0 * inverse * inverse * t * mapped[1U][1U] +
                    3.0 * inverse * t * t * mapped[2U][1U] +
                    t * t * t * mapped[3U][1U]};
            return std::isfinite(result[0U]) &&
                std::isfinite(result[1U]);
        };
        const auto include_parameter = [
            &evaluate,
            &include_mapped](double t) noexcept {
            if (t <= 0.0 || t >= 1.0) {
                return true;
            }
            point value{};
            if (!evaluate(t, value)) {
                return false;
            }
            return include_mapped(value);
        };
        const auto include_axis_extrema = [
            &mapped,
            &include_parameter](std::size_t axis) noexcept {
            const double p0_axis = mapped[0U][axis];
            const double p1_axis = mapped[1U][axis];
            const double p2_axis = mapped[2U][axis];
            const double p3_axis = mapped[3U][axis];
            const double quadratic = 3.0 *
                (-p0_axis + 3.0 * p1_axis - 3.0 * p2_axis + p3_axis);
            const double linear = 6.0 *
                (p0_axis - 2.0 * p1_axis + p2_axis);
            const double constant = 3.0 * (p1_axis - p0_axis);
            if (quadratic == 0.0) {
                return linear == 0.0 ||
                    include_parameter(-constant / linear);
            }
            const double discriminant = linear * linear -
                4.0 * quadratic * constant;
            if (discriminant < 0.0) {
                return true;
            }
            const double root = std::sqrt(discriminant);
            const double denominator = 2.0 * quadratic;
            return include_parameter((-linear - root) / denominator) &&
                include_parameter((-linear + root) / denominator);
        };
        return include_mapped(mapped[0U]) &&
            include_mapped(mapped[3U]) &&
            include_axis_extrema(0U) && include_axis_extrema(1U);
    };
    const double radius = thickness * 0.5;
    for (std::size_t index = 0U; index < vertices.size(); ++index) {
        const point& start = vertices[index];
        const point& end = vertices[(index + 1U) % vertices.size()];
        const point& direction = directions[index];
        const point normal{-direction[1U] * radius,
            direction[0U] * radius};
        if (!include({start[0U] - normal[0U],
                start[1U] - normal[1U]}) ||
            !include({start[0U] + normal[0U],
                start[1U] + normal[1U]}) ||
            !include({end[0U] - normal[0U],
                end[1U] - normal[1U]}) ||
            !include({end[0U] + normal[0U],
                end[1U] + normal[1U]})) {
            return false;
        }
    }
    if (line_join == PROGPU_NATIVE_STROKE_JOIN_MITER) {
        const double resolved_limit = std::max(1.0, miter_limit);
        for (std::size_t index = 0U; index < vertices.size(); ++index) {
            const point& join = vertices[index];
            const point& incoming = directions[
                (index + directions.size() - 1U) % directions.size()];
            const point& outgoing = directions[index];
            const double turn = incoming[0U] * outgoing[1U] -
                incoming[1U] * outgoing[0U];
            if (!std::isfinite(turn) || std::abs(turn) <= 0.0001) {
                return false;
            }
            const double outer_sign = turn > 0.0 ? -1.0 : 1.0;
            const point previous_outer{
                join[0U] - incoming[1U] * outer_sign * radius,
                join[1U] + incoming[0U] * outer_sign * radius};
            const point next_outer{
                join[0U] - outgoing[1U] * outer_sign * radius,
                join[1U] + outgoing[0U] * outer_sign * radius};
            const point delta{
                next_outer[0U] - previous_outer[0U],
                next_outer[1U] - previous_outer[1U]};
            const double denominator =
                incoming[0U] * outgoing[1U] -
                incoming[1U] * outgoing[0U];
            const double distance =
                (delta[0U] * outgoing[1U] -
                    delta[1U] * outgoing[0U]) /
                denominator;
            const point miter{
                previous_outer[0U] + incoming[0U] * distance,
                previous_outer[1U] + incoming[1U] * distance};
            if (!std::isfinite(miter[0U]) || !std::isfinite(miter[1U])) {
                return false;
            }
            const double miter_distance = std::hypot(
                    miter[0U] - join[0U],
                    miter[1U] - join[1U]);
            if (miter_distance > radius * resolved_limit + 0.0001) {
                const double dot = incoming[0U] * outgoing[0U] +
                    incoming[1U] * outgoing[1U];
                const double clip_denominator = radius * std::sqrt(
                    std::max(0.0, (1.0 - dot) * 0.5));
                const double clip_numerator = radius * std::sqrt(
                    std::max(0.0, (1.0 + dot) * 0.5));
                if (!std::isfinite(clip_denominator) ||
                    clip_denominator <= 0.0001) {
                    return false;
                }
                const double ratio = std::max(
                    0.0,
                    (resolved_limit * radius - clip_numerator) /
                        clip_denominator);
                const point first_clip{
                    previous_outer[0U] +
                        incoming[0U] * radius * ratio,
                    previous_outer[1U] +
                        incoming[1U] * radius * ratio};
                const point second_clip{
                    next_outer[0U] - outgoing[0U] * radius * ratio,
                    next_outer[1U] - outgoing[1U] * radius * ratio};
                if (!include(first_clip) || !include(second_clip)) {
                    return false;
                }
                continue;
            }
            if (!include(miter)) {
                return false;
            }
        }
    } else if (line_join == PROGPU_NATIVE_STROKE_JOIN_ROUND) {
        constexpr double default_tolerance = 0.25;
        const double refinement_threshold = radius < default_tolerance
            ? -2.0
            : 2.0 * (1.0 - default_tolerance / radius) *
                    (1.0 - default_tolerance / radius) -
                1.0;
        const auto bezier_distance = [radius](double dot) noexcept {
            const double radius_squared = radius * radius;
            const double a = std::max(0.0, 0.5 * (radius_squared + dot));
            const double denominator_squared = radius_squared - a;
            if (denominator_squared <= 0.0) {
                return 0.0;
            }
            const double denominator = std::sqrt(denominator_squared);
            const double numerator = (4.0 / 3.0) *
                (radius - std::sqrt(a));
            return numerator <= denominator * 0.000001
                ? 0.0
                : numerator / denominator;
        };
        for (std::size_t index = 0U; index < vertices.size(); ++index) {
            const point& join = vertices[index];
            const point& incoming = directions[
                (index + directions.size() - 1U) % directions.size()];
            const point& outgoing = directions[index];
            const double turn = incoming[0U] * outgoing[1U] -
                incoming[1U] * outgoing[0U];
            if (!std::isfinite(turn) || std::abs(turn) <= 0.0001) {
                return false;
            }
            const double outer_sign = turn > 0.0 ? -1.0 : 1.0;
            const point previous_outer{
                join[0U] - incoming[1U] * outer_sign * radius,
                join[1U] + incoming[0U] * outer_sign * radius};
            const point next_outer{
                join[0U] - outgoing[1U] * outer_sign * radius,
                join[1U] + outgoing[0U] * outer_sign * radius};
            const double direction_dot =
                incoming[0U] * outgoing[0U] +
                incoming[1U] * outgoing[1U];
            if (!std::isfinite(direction_dot)) {
                return false;
            }
            if (direction_dot > refinement_threshold) {
                continue;
            }
            if (direction_dot >= 0.0) {
                const double distance = bezier_distance(
                    direction_dot * radius * radius);
                const point control1{
                    previous_outer[0U] + incoming[0U] * radius * distance,
                    previous_outer[1U] + incoming[1U] * radius * distance};
                const point control2{
                    next_outer[0U] - outgoing[0U] * radius * distance,
                    next_outer[1U] - outgoing[1U] * radius * distance};
                if (!include_cubic(
                        previous_outer, control1, control2, next_outer)) {
                    return false;
                }
                continue;
            }
            const point tangent_sum{
                incoming[0U] + outgoing[0U],
                incoming[1U] + outgoing[1U]};
            const double tangent_sum_length = std::hypot(
                tangent_sum[0U], tangent_sum[1U]);
            const point radial_sum{
                previous_outer[0U] + next_outer[0U] - 2.0 * join[0U],
                previous_outer[1U] + next_outer[1U] - 2.0 * join[1U]};
            const double radial_sum_length = std::hypot(
                radial_sum[0U], radial_sum[1U]);
            if (!std::isfinite(tangent_sum_length) ||
                !std::isfinite(radial_sum_length) ||
                tangent_sum_length <= 0.0001 ||
                radial_sum_length <= 0.0001) {
                return false;
            }
            const point middle_tangent{
                tangent_sum[0U] / tangent_sum_length,
                tangent_sum[1U] / tangent_sum_length};
            const point middle{
                join[0U] + radial_sum[0U] / radial_sum_length * radius,
                join[1U] + radial_sum[1U] / radial_sum_length * radius};
            const double half_dot = std::abs(
                outgoing[0U] * middle_tangent[0U] +
                outgoing[1U] * middle_tangent[1U]);
            const double distance = bezier_distance(
                half_dot * radius * radius);
            const point first_control1{
                previous_outer[0U] + incoming[0U] * radius * distance,
                previous_outer[1U] + incoming[1U] * radius * distance};
            const point first_control2{
                middle[0U] - middle_tangent[0U] * radius * distance,
                middle[1U] - middle_tangent[1U] * radius * distance};
            const point second_control1{
                middle[0U] + middle_tangent[0U] * radius * distance,
                middle[1U] + middle_tangent[1U] * radius * distance};
            const point second_control2{
                next_outer[0U] - outgoing[0U] * radius * distance,
                next_outer[1U] - outgoing[1U] * radius * distance};
            if (!include_cubic(
                    previous_outer,
                    first_control1,
                    first_control2,
                    middle) ||
                !include_cubic(
                    middle,
                    second_control1,
                    second_control2,
                    next_outer)) {
                return false;
            }
        }
    }
    const double bounds_width = maximum_x - minimum_x;
    const double bounds_height = maximum_y - minimum_y;
    if (!finite_double_as_float(minimum_x) ||
        !finite_double_as_float(minimum_y) ||
        !finite_double_as_float(bounds_width) ||
        !finite_double_as_float(bounds_height) ||
        bounds_width <= 0.0 || bounds_height <= 0.0) {
        return false;
    }
    bounds = {
        static_cast<float>(minimum_x),
        static_cast<float>(minimum_y),
        static_cast<float>(bounds_width),
        static_cast<float>(bounds_height)};
    return true;
}

bool try_get_path_segment_bounds(
    std::span<const progpu_native_path_segment> segments,
    progpu_native_image_rect& bounds) noexcept {
    double left = std::numeric_limits<double>::infinity();
    double top = std::numeric_limits<double>::infinity();
    double right = -std::numeric_limits<double>::infinity();
    double bottom = -std::numeric_limits<double>::infinity();
    const auto include_point = [
        &left,
        &top,
        &right,
        &bottom](double x, double y) noexcept {
        if (!std::isfinite(x) || !std::isfinite(y)) {
            return false;
        }
        left = std::min(left, x);
        top = std::min(top, y);
        right = std::max(right, x);
        bottom = std::max(bottom, y);
        return true;
    };
    const auto include_quadratic = [
        &include_point](
        const progpu_native_path_segment& segment,
        double t) noexcept {
        if (t <= 0.0 || t >= 1.0) {
            return true;
        }
        const double inverse = 1.0 - t;
        return include_point(
            inverse * inverse * segment.p0.x +
                2.0 * inverse * t * segment.p1.x +
                t * t * segment.p2.x,
            inverse * inverse * segment.p0.y +
                2.0 * inverse * t * segment.p1.y +
                t * t * segment.p2.y);
    };
    const auto include_cubic = [
        &include_point](
        const progpu_native_path_segment& segment,
        double t) noexcept {
        if (t <= 0.0 || t >= 1.0) {
            return true;
        }
        const double inverse = 1.0 - t;
        return include_point(
            inverse * inverse * inverse * segment.p0.x +
                3.0 * inverse * inverse * t * segment.p1.x +
                3.0 * inverse * t * t * segment.p2.x +
                t * t * t * segment.p3.x,
            inverse * inverse * inverse * segment.p0.y +
                3.0 * inverse * inverse * t * segment.p1.y +
                3.0 * inverse * t * t * segment.p2.y +
                t * t * t * segment.p3.y);
    };
    const auto include_cubic_axis_extrema = [
        &include_cubic](
        const progpu_native_path_segment& segment,
        double p0,
        double p1,
        double p2,
        double p3) noexcept {
        const double quadratic = 3.0 * (-p0 + 3.0 * p1 - 3.0 * p2 + p3);
        const double linear = 6.0 * (p0 - 2.0 * p1 + p2);
        const double constant = 3.0 * (p1 - p0);
        if (quadratic == 0.0) {
            return linear == 0.0 ||
                include_cubic(segment, -constant / linear);
        }
        const double discriminant =
            linear * linear - 4.0 * quadratic * constant;
        if (discriminant < 0.0) {
            return true;
        }
        const double root = std::sqrt(discriminant);
        const double denominator = 2.0 * quadratic;
        return include_cubic(segment, (-linear - root) / denominator) &&
            include_cubic(segment, (-linear + root) / denominator);
    };
    for (const auto& segment : segments) {
        if (!include_point(segment.p0.x, segment.p0.y)) {
            return false;
        }
        if (segment.kind == PROGPU_NATIVE_PATH_SEGMENT_LINE) {
            if (!include_point(segment.p1.x, segment.p1.y)) {
                return false;
            }
        } else if (segment.kind == PROGPU_NATIVE_PATH_SEGMENT_QUADRATIC) {
            if (!include_point(segment.p2.x, segment.p2.y)) {
                return false;
            }
            const double denominator_x = segment.p0.x -
                2.0 * segment.p1.x + segment.p2.x;
            const double denominator_y = segment.p0.y -
                2.0 * segment.p1.y + segment.p2.y;
            if ((denominator_x != 0.0 &&
                 !include_quadratic(
                     segment,
                     (segment.p0.x - segment.p1.x) / denominator_x)) ||
                (denominator_y != 0.0 &&
                 !include_quadratic(
                     segment,
                     (segment.p0.y - segment.p1.y) / denominator_y))) {
                return false;
            }
        } else if (segment.kind == PROGPU_NATIVE_PATH_SEGMENT_CUBIC) {
            if (!include_point(segment.p3.x, segment.p3.y) ||
                !include_cubic_axis_extrema(
                    segment,
                    segment.p0.x,
                    segment.p1.x,
                    segment.p2.x,
                    segment.p3.x) ||
                !include_cubic_axis_extrema(
                    segment,
                    segment.p0.y,
                    segment.p1.y,
                    segment.p2.y,
                    segment.p3.y)) {
                return false;
            }
        } else if (segment.kind == PROGPU_NATIVE_PATH_SEGMENT_ARC) {
            if (!include_point(segment.p1.x, segment.p1.y)) {
                return false;
            }
            const float theta1 = std::bit_cast<float>(segment.pad0);
            const float delta_theta = std::bit_cast<float>(segment.pad1);
            const float rotation_radians =
                std::bit_cast<float>(segment.pad2);
            const float rotation_degrees = rotation_radians * 180.0F /
                std::numbers::pi_v<float>;
            const float cosine_rotation = std::cos(rotation_radians);
            const float sine_rotation = std::sin(rotation_radians);
            const float x_extrema = std::atan2(
                -segment.p3.y * sine_rotation,
                segment.p3.x * cosine_rotation);
            const float y_extrema = std::atan2(
                segment.p3.y * cosine_rotation,
                segment.p3.x * sine_rotation);
            const float extrema[4U]{
                x_extrema,
                x_extrema + std::numbers::pi_v<float>,
                y_extrema,
                y_extrema + std::numbers::pi_v<float>};
            for (const float theta : extrema) {
                if (!progpu::native::geometry::angle_within_sweep(
                        theta, theta1, delta_theta)) {
                    continue;
                }
                const auto point =
                    progpu::native::geometry::evaluate_arc(
                        {segment.p2.x, segment.p2.y},
                        segment.p3.x,
                        segment.p3.y,
                        rotation_degrees,
                        theta);
                if (!include_point(point.x, point.y)) {
                    return false;
                }
            }
        } else {
            return false;
        }
    }
    const double width = right - left;
    const double height = bottom - top;
    if (!finite_double_as_float(left) || !finite_double_as_float(top) ||
        !finite_double_as_float(width) || !finite_double_as_float(height) ||
        width <= 0.0 || height <= 0.0) {
        return false;
    }
    bounds = {
        static_cast<float>(left),
        static_cast<float>(top),
        static_cast<float>(width),
        static_cast<float>(height)};
    return true;
}

bool try_transform_arc_segment(
    const progpu_native_path_segment& source,
    const affine_2d_double& transform,
    progpu_native_path_segment& destination) noexcept {
    if (source.kind != PROGPU_NATIVE_PATH_SEGMENT_ARC) {
        return false;
    }
    const double theta1 = std::bit_cast<float>(source.pad0);
    const double delta_theta = std::bit_cast<float>(source.pad1);
    const double rotation = std::bit_cast<float>(source.pad2);
    const double radius_x = source.p3.x;
    const double radius_y = source.p3.y;
    if (!std::isfinite(theta1) || !std::isfinite(delta_theta) ||
        !std::isfinite(rotation) || radius_x <= 0.0 || radius_y <= 0.0) {
        return false;
    }
    const auto map_point = [&transform](
        progpu_native_point source_point,
        progpu_native_point& result) noexcept {
        const double mapped_x = source_point.x * transform.m11 +
            source_point.y * transform.m21 + transform.m31;
        const double mapped_y = source_point.x * transform.m12 +
            source_point.y * transform.m22 + transform.m32;
        if (!finite_double_as_float(mapped_x) ||
            !finite_double_as_float(mapped_y)) {
            return false;
        }
        result = {
            static_cast<float>(mapped_x),
            static_cast<float>(mapped_y)};
        return true;
    };
    if (transform.m11 == 1.0 && transform.m12 == 0.0 &&
        transform.m21 == 0.0 && transform.m22 == 1.0) {
        destination = source;
        return map_point(source.p0, destination.p0) &&
            map_point(source.p1, destination.p1) &&
            map_point(source.p2, destination.p2);
    }
    const double cosine_rotation = std::cos(rotation);
    const double sine_rotation = std::sin(rotation);
    const double basis_x_x = radius_x * cosine_rotation;
    const double basis_x_y = radius_x * sine_rotation;
    const double basis_y_x = -radius_y * sine_rotation;
    const double basis_y_y = radius_y * cosine_rotation;
    const double transformed_x_x =
        basis_x_x * transform.m11 + basis_x_y * transform.m21;
    const double transformed_x_y =
        basis_x_x * transform.m12 + basis_x_y * transform.m22;
    const double transformed_y_x =
        basis_y_x * transform.m11 + basis_y_y * transform.m21;
    const double transformed_y_y =
        basis_y_x * transform.m12 + basis_y_y * transform.m22;
    const double metric_xx = transformed_x_x * transformed_x_x +
        transformed_y_x * transformed_y_x;
    const double metric_xy = transformed_x_x * transformed_x_y +
        transformed_y_x * transformed_y_y;
    const double metric_yy = transformed_x_y * transformed_x_y +
        transformed_y_y * transformed_y_y;
    const double half_difference = (metric_xx - metric_yy) * 0.5;
    const double maximum_eigenvalue =
        (metric_xx + metric_yy) * 0.5 +
        std::hypot(half_difference, metric_xy);
    const double determinant = transformed_x_x * transformed_y_y -
        transformed_x_y * transformed_y_x;
    if (!std::isfinite(maximum_eigenvalue) ||
        !std::isfinite(determinant) || maximum_eigenvalue <= 0.0 ||
        determinant == 0.0) {
        return false;
    }
    const double transformed_radius_x = std::sqrt(maximum_eigenvalue);
    const double transformed_radius_y =
        std::abs(determinant) / transformed_radius_x;
    if (!finite_double_as_float(transformed_radius_x) ||
        !finite_double_as_float(transformed_radius_y) ||
        static_cast<float>(transformed_radius_x) <= 0.0F ||
        static_cast<float>(transformed_radius_y) <= 0.0F) {
        return false;
    }
    double axis_x = metric_xy;
    double axis_y = maximum_eigenvalue - metric_xx;
    const double alternate_x = maximum_eigenvalue - metric_yy;
    const double alternate_y = metric_xy;
    if (std::hypot(alternate_x, alternate_y) >
        std::hypot(axis_x, axis_y)) {
        axis_x = alternate_x;
        axis_y = alternate_y;
    }
    double axis_length = std::hypot(axis_x, axis_y);
    if (!std::isfinite(axis_length)) {
        return false;
    }
    if (axis_length == 0.0) {
        axis_x = 1.0;
        axis_y = 0.0;
        axis_length = 1.0;
    }
    axis_x /= axis_length;
    axis_y /= axis_length;
    const double perpendicular_x = -axis_y;
    const double perpendicular_y = axis_x;

    destination = source;
    if (!map_point(source.p0, destination.p0) ||
        !map_point(source.p1, destination.p1) ||
        !map_point(source.p2, destination.p2)) {
        return false;
    }
    const double start_x = destination.p0.x - destination.p2.x;
    const double start_y = destination.p0.y - destination.p2.y;
    double cosine_theta =
        (start_x * axis_x + start_y * axis_y) /
        transformed_radius_x;
    double sine_theta =
        (start_x * perpendicular_x + start_y * perpendicular_y) /
        transformed_radius_y;
    const double theta_length = std::hypot(cosine_theta, sine_theta);
    if (!std::isfinite(theta_length) || theta_length == 0.0) {
        return false;
    }
    cosine_theta /= theta_length;
    sine_theta /= theta_length;
    const double transformed_theta1 = std::atan2(sine_theta, cosine_theta);
    const double transformed_delta_theta =
        determinant > 0.0 ? delta_theta : -delta_theta;
    const double transformed_rotation = std::atan2(axis_y, axis_x);
    if (!finite_double_as_float(transformed_theta1) ||
        !finite_double_as_float(transformed_delta_theta) ||
        !finite_double_as_float(transformed_rotation)) {
        return false;
    }
    destination.p3 = {
        static_cast<float>(transformed_radius_x),
        static_cast<float>(transformed_radius_y)};
    destination.pad0 = std::bit_cast<std::uint32_t>(
        static_cast<float>(transformed_theta1));
    destination.pad1 = std::bit_cast<std::uint32_t>(
        static_cast<float>(transformed_delta_theta));
    destination.pad2 = std::bit_cast<std::uint32_t>(
        static_cast<float>(transformed_rotation));
    return true;
}

bool try_line_stroke_bounds(
    double x0,
    double y0,
    double x1,
    double y1,
    double thickness,
    std::uint32_t start_cap,
    std::uint32_t end_cap,
    double& x,
    double& y,
    double& width,
    double& height) noexcept {
    const double delta_x = x1 - x0;
    const double delta_y = y1 - y0;
    const double length = std::hypot(delta_x, delta_y);
    if (!std::isfinite(length) || length <= 0.0) {
        return false;
    }
    const double half_thickness = thickness * 0.5;
    const double unit_x = delta_x / length;
    const double unit_y = delta_y / length;
    const double normal_x = -unit_y * half_thickness;
    const double normal_y = unit_x * half_thickness;
    double minimum_x = std::numeric_limits<double>::infinity();
    double minimum_y = std::numeric_limits<double>::infinity();
    double maximum_x = -std::numeric_limits<double>::infinity();
    double maximum_y = -std::numeric_limits<double>::infinity();
    const auto include = [
        &minimum_x,
        &minimum_y,
        &maximum_x,
        &maximum_y](double point_x, double point_y) noexcept {
        minimum_x = std::min(minimum_x, point_x);
        minimum_y = std::min(minimum_y, point_y);
        maximum_x = std::max(maximum_x, point_x);
        maximum_y = std::max(maximum_y, point_y);
    };
    include(x0 - normal_x, y0 - normal_y);
    include(x0 + normal_x, y0 + normal_y);
    include(x1 - normal_x, y1 - normal_y);
    include(x1 + normal_x, y1 + normal_y);
    const auto include_cap = [
        &include,
        half_thickness,
        normal_x,
        normal_y,
        unit_x,
        unit_y](
        double center_x,
        double center_y,
        double outward_sign,
        std::uint32_t cap) noexcept {
        if (cap == PROGPU_NATIVE_STROKE_CAP_ROUND) {
            include(center_x - half_thickness, center_y - half_thickness);
            include(center_x + half_thickness, center_y + half_thickness);
            return;
        }
        if (cap == PROGPU_NATIVE_STROKE_CAP_SQUARE) {
            const double outer_x =
                center_x + outward_sign * unit_x * half_thickness;
            const double outer_y =
                center_y + outward_sign * unit_y * half_thickness;
            include(outer_x - normal_x, outer_y - normal_y);
            include(outer_x + normal_x, outer_y + normal_y);
            return;
        }
        if (cap == PROGPU_NATIVE_STROKE_CAP_TRIANGLE) {
            include(
                center_x + outward_sign * unit_x * half_thickness,
                center_y + outward_sign * unit_y * half_thickness);
        }
    };
    include_cap(x0, y0, -1.0, start_cap);
    include_cap(x1, y1, 1.0, end_cap);
    x = minimum_x;
    y = minimum_y;
    width = maximum_x - minimum_x;
    height = maximum_y - minimum_y;
    return finite_double_as_float(x) && finite_double_as_float(y) &&
        finite_double_as_float(width) && finite_double_as_float(height) &&
        width >= 0.0 && height >= 0.0;
}

bool try_transformed_line_stroke_bounds(
    double x0,
    double y0,
    double x1,
    double y1,
    double thickness,
    std::uint32_t start_cap,
    std::uint32_t end_cap,
    const affine_2d_double& transform,
    progpu_native_image_rect& bounds) noexcept {
    const double delta_x = x1 - x0;
    const double delta_y = y1 - y0;
    const double length = std::hypot(delta_x, delta_y);
    if (!std::isfinite(length) || length <= 0.0 || thickness <= 0.0) {
        return false;
    }
    const double half_thickness = thickness * 0.5;
    const double unit_x = delta_x / length;
    const double unit_y = delta_y / length;
    const double normal_x = -unit_y * half_thickness;
    const double normal_y = unit_x * half_thickness;
    double minimum_x = std::numeric_limits<double>::infinity();
    double minimum_y = std::numeric_limits<double>::infinity();
    double maximum_x = -std::numeric_limits<double>::infinity();
    double maximum_y = -std::numeric_limits<double>::infinity();
    const auto include_world = [
        &minimum_x,
        &minimum_y,
        &maximum_x,
        &maximum_y](double point_x, double point_y) noexcept {
        if (!std::isfinite(point_x) || !std::isfinite(point_y)) {
            return false;
        }
        minimum_x = std::min(minimum_x, point_x);
        minimum_y = std::min(minimum_y, point_y);
        maximum_x = std::max(maximum_x, point_x);
        maximum_y = std::max(maximum_y, point_y);
        return true;
    };
    const auto transform_point = [
        &transform](
        double point_x,
        double point_y,
        progpu_native_point& result) noexcept {
        const double transformed_x = point_x * transform.m11 +
            point_y * transform.m21 + transform.m31;
        const double transformed_y = point_x * transform.m12 +
            point_y * transform.m22 + transform.m32;
        if (!finite_double_as_float(transformed_x) ||
            !finite_double_as_float(transformed_y)) {
            return false;
        }
        result = {
            static_cast<float>(transformed_x),
            static_cast<float>(transformed_y)};
        return true;
    };
    const auto include = [
        &transform_point,
        &include_world](double point_x, double point_y) noexcept {
        progpu_native_point transformed{};
        return transform_point(point_x, point_y, transformed) &&
            include_world(transformed.x, transformed.y);
    };
    if (!include(x0 - normal_x, y0 - normal_y) ||
        !include(x0 + normal_x, y0 + normal_y) ||
        !include(x1 - normal_x, y1 - normal_y) ||
        !include(x1 + normal_x, y1 + normal_y)) {
        return false;
    }
    const auto include_cap = [
        &include,
        &include_world,
        &transform_point,
        half_thickness,
        normal_x,
        normal_y,
        unit_x,
        unit_y](
        double center_x,
        double center_y,
        double outward_sign,
        std::uint32_t cap) noexcept {
        const double outer_x =
            center_x + outward_sign * unit_x * half_thickness;
        const double outer_y =
            center_y + outward_sign * unit_y * half_thickness;
        if (cap == PROGPU_NATIVE_STROKE_CAP_SQUARE) {
            return include(outer_x - normal_x, outer_y - normal_y) &&
                include(outer_x + normal_x, outer_y + normal_y);
        }
        if (cap == PROGPU_NATIVE_STROKE_CAP_TRIANGLE) {
            return include(outer_x, outer_y);
        }
        if (cap == PROGPU_NATIVE_STROKE_CAP_ROUND) {
            // Matches WPF WpfGfx's ARC_AS_BEZIER round-cap widening.
            constexpr double arc_as_bezier =
                0.5522847498307933984;
            const double start_x = center_x - normal_x;
            const double start_y = center_y - normal_y;
            const double end_x = center_x + normal_x;
            const double end_y = center_y + normal_y;
            const double across_x = normal_x * arc_as_bezier;
            const double across_y = normal_y * arc_as_bezier;
            const double along_x =
                outward_sign * unit_x * half_thickness;
            const double along_y =
                outward_sign * unit_y * half_thickness;
            const double mid_x = center_x + along_x;
            const double mid_y = center_y + along_y;
            const double control_along_x = along_x * arc_as_bezier;
            const double control_along_y = along_y * arc_as_bezier;
            std::array<progpu_native_path_segment, 2U> cap_segments{};
            auto& first = cap_segments[0U];
            auto& second = cap_segments[1U];
            first.kind = PROGPU_NATIVE_PATH_SEGMENT_CUBIC;
            second.kind = PROGPU_NATIVE_PATH_SEGMENT_CUBIC;
            if (!transform_point(start_x, start_y, first.p0) ||
                !transform_point(
                    start_x + control_along_x,
                    start_y + control_along_y,
                    first.p1) ||
                !transform_point(
                    mid_x - across_x,
                    mid_y - across_y,
                    first.p2) ||
                !transform_point(mid_x, mid_y, first.p3) ||
                !transform_point(mid_x, mid_y, second.p0) ||
                !transform_point(
                    mid_x + across_x,
                    mid_y + across_y,
                    second.p1) ||
                !transform_point(
                    end_x + control_along_x,
                    end_y + control_along_y,
                    second.p2) ||
                !transform_point(end_x, end_y, second.p3)) {
                return false;
            }
            progpu_native_image_rect cap_bounds{};
            return try_get_path_segment_bounds(cap_segments, cap_bounds) &&
                include_world(cap_bounds.x, cap_bounds.y) &&
                include_world(
                    cap_bounds.x + cap_bounds.width,
                    cap_bounds.y + cap_bounds.height);
        }
        return cap == PROGPU_NATIVE_STROKE_CAP_FLAT;
    };
    if (!include_cap(x0, y0, -1.0, start_cap) ||
        !include_cap(x1, y1, 1.0, end_cap)) {
        return false;
    }
    const double width = maximum_x - minimum_x;
    const double height = maximum_y - minimum_y;
    if (!finite_double_as_float(minimum_x) ||
        !finite_double_as_float(minimum_y) ||
        !finite_double_as_float(width) ||
        !finite_double_as_float(height) || width <= 0.0 || height <= 0.0) {
        return false;
    }
    bounds = {
        static_cast<float>(minimum_x),
        static_cast<float>(minimum_y),
        static_cast<float>(width),
        static_cast<float>(height)};
    return true;
}

bool try_degenerate_cap_stroke_bounds(
    double point_x,
    double point_y,
    double thickness,
    std::uint32_t start_cap,
    std::uint32_t end_cap,
    double& x,
    double& y,
    double& width,
    double& height) noexcept {
    const double half_thickness = thickness * 0.5;
    const double right = point_x +
        (end_cap == PROGPU_NATIVE_STROKE_CAP_FLAT
            ? 0.0
            : half_thickness);
    x = point_x -
        (start_cap == PROGPU_NATIVE_STROKE_CAP_FLAT
            ? 0.0
            : half_thickness);
    y = point_y - half_thickness;
    width = right - x;
    height = thickness;
    return finite_double_as_float(x) && finite_double_as_float(y) &&
        finite_double_as_float(width) && finite_double_as_float(height) &&
        width > 0.0 && height > 0.0;
}

} // namespace

batch_reader::batch_reader(std::span<const std::byte> bytes) noexcept
    : bytes_(bytes) {
}

status batch_reader::next(command_view& view) noexcept {
    view = {};
    if (offset_ == bytes_.size()) {
        return status::end_of_batch;
    }
    if (offset_ > bytes_.size() || bytes_.size() - offset_ < 8U) {
        return status::malformed_batch;
    }

    std::uint32_t item_size = 0U;
    std::uint32_t raw_command = 0U;
    std::memcpy(&item_size, bytes_.data() + offset_, sizeof(item_size));
    std::memcpy(
        &raw_command,
        bytes_.data() + offset_ + sizeof(item_size),
        sizeof(raw_command));
    if (item_size < 8U || (item_size & 3U) != 0U ||
        item_size > bytes_.size() - offset_) {
        return status::malformed_batch;
    }

    const auto kind = static_cast<command>(raw_command);
    if (!is_known(kind)) {
        return status::unknown_command;
    }
    view.kind = kind;
    view.packet = bytes_.subspan(
        offset_ + sizeof(item_size),
        item_size - sizeof(item_size));
    view.batch_offset = offset_;
    offset_ += item_size;
    return status::success;
}

std::uint32_t batch_reader::offset() const noexcept {
    return offset_;
}

struct scene_compile_context {
    const scene_build_request& request;
    std::uint32_t current_time_milliseconds{};
    bool needs_more_cycles{};
    std::uint32_t visual_brush_depth{};
    std::uint64_t scene_id{};

    bool is_visual_brush() const noexcept {
        return visual_brush_depth != 0U ||
            (static_cast<std::uint32_t>(request.flags) &
                static_cast<std::uint32_t>(scene_build_request_flags::visual_brush)) != 0U;
    }
};

struct channel::implementation {
    struct visual_state {
        double offset_x{};
        double offset_y{};
        double opacity{1.0};
        std::uint32_t transform_handle{};
        std::uint32_t effect_handle{};
        std::uint32_t cache_mode_handle{};
        std::uint32_t clip_geometry_handle{};
        std::uint32_t content_handle{};
        std::uint32_t alpha_mask_handle{};
        double scroll_clip_x{};
        double scroll_clip_y{};
        double scroll_clip_width{};
        double scroll_clip_height{};
        bool has_scroll_clip{};
        std::uint32_t render_options_flags{};
        std::uint32_t edge_mode{};
        std::uint32_t bitmap_scaling_mode{};
        std::uint32_t clear_type_hint{};
        std::uint32_t text_rendering_mode{};
        std::uint32_t text_hinting_mode{};
        double cache_bounds_x{};
        double cache_bounds_y{};
        double cache_bounds_width{};
        double cache_bounds_height{};
        bool has_cache_bounds{};
        std::vector<double> guidelines_x;
        std::vector<double> guidelines_y;
        std::vector<std::uint32_t> children;
    };

    struct effect_state {
        enum class kind : std::uint32_t {
            blur,
            drop_shadow
        } type{kind::blur};
        double radius{};
        double shadow_depth{};
        double direction{};
        double opacity{1.0};
        progpu_native_color color{};
        std::array<std::uint32_t, 5U> animations{};
        bool box_blur{};
    };

    struct target_state {
        std::uint32_t root_handle{};
        float clear_red{};
        float clear_green{};
        float clear_blue{};
        float clear_alpha{};
        std::uint32_t flags{};
        std::uint32_t width{};
        std::uint32_t height{};
        double dpi_x{1.0};
        double dpi_y{1.0};
        std::int32_t dpi_awareness_context{};
        std::int32_t window_left{};
        std::int32_t window_top{};
        std::int32_t window_right{};
        std::int32_t window_bottom{};
        std::uint32_t window_layer_type{};
        std::uint32_t transparency_mode{};
        float constant_alpha{1.0F};
        progpu_native_color color_key{};
        std::uint32_t disable_cookie{};
        bool is_window_target{};
        bool suppress_layered{};
        bool rendering_enabled{true};
        bool is_child{};
        bool is_rtl{};
        bool gdi_blt{};
        bool dpi_after_parent{};
    };

    struct viewport3d_visual_state {
        double x{};
        double y{};
        double width{};
        double height{};
        std::uint32_t camera_handle{};
        bool has_viewport{};
        bool has_camera_binding{};
        std::uint32_t child_handle{};
        bool has_child_binding{};
    };

    struct visual3d_state {
        std::uint32_t content_handle{};
        std::uint32_t transform_handle{};
        std::vector<std::uint32_t> children;
    };

    struct model3d_group_state {
        std::uint32_t transform_handle{};
        std::vector<std::uint32_t> children;
    };

    struct light3d_state {
        std::uint32_t kind{};
        progpu_native_color color{};
        std::array<float, 3U> position{};
        std::array<float, 3U> direction{};
        double range{};
        double constant_attenuation{};
        double linear_attenuation{};
        double quadratic_attenuation{};
        double outer_cone_angle{};
        double inner_cone_angle{};
        std::uint32_t transform_handle{};
        std::array<std::uint32_t, 9U> animations{};
    };

    struct geometry_model3d_state {
        std::uint32_t transform_handle{};
        std::uint32_t geometry_handle{};
        std::uint32_t material_handle{};
        std::uint32_t back_material_handle{};
    };

    struct mesh_geometry3d_state {
        std::vector<std::array<float, 3U>> positions;
        std::vector<std::array<float, 3U>> normals;
        std::vector<std::array<double, 2U>> texture_coordinates;
        std::vector<std::uint32_t> indices;
    };

    struct material_group_state {
        std::vector<std::uint32_t> children;
    };

    struct material3d_state {
        enum class kind : std::uint32_t {
            diffuse,
            specular,
            emissive
        } type{kind::diffuse};
        progpu_native_color color{};
        progpu_native_color ambient_color{};
        double specular_power{1.0};
        std::uint32_t brush_handle{};
    };

    struct solid_brush_state {
        double opacity{1.0};
        progpu_native_color color{};
        std::uint32_t transform_handle{};
        std::uint32_t relative_transform_handle{};
        std::uint32_t opacity_animation_handle{};
        std::uint32_t color_animation_handle{};
    };

    struct point_resource_state {
        double x{};
        double y{};
    };

    struct rect_resource_state {
        double x{};
        double y{};
        double width{};
        double height{};
    };

    struct gradient_stop_state {
        double position{};
        progpu_native_color color{};
    };

    struct tile_brush_state {
        double opacity{1.0};
        rect_resource_state viewport{0.0, 0.0, 1.0, 1.0};
        rect_resource_state viewbox{0.0, 0.0, 1.0, 1.0};
        double cache_threshold_minimum{0.707};
        double cache_threshold_maximum{1.414};
        std::uint32_t opacity_animation{};
        std::uint32_t transform_handle{};
        std::uint32_t relative_transform_handle{};
        std::uint32_t viewport_units{1U};
        std::uint32_t viewbox_units{1U};
        std::uint32_t viewport_animation{};
        std::uint32_t viewbox_animation{};
        std::uint32_t stretch{1U};
        std::uint32_t tile_mode{};
        std::uint32_t alignment_x{1U};
        std::uint32_t alignment_y{1U};
        std::uint32_t caching_hint{};
        std::uint32_t source_handle{};
    };

    struct gradient_brush_state {
        enum class kind : std::uint32_t {
            linear,
            radial
        } type{kind::linear};
        double opacity{1.0};
        double first_x{};
        double first_y{};
        double second_x{};
        double second_y{};
        double radius_x{};
        double radius_y{};
        std::uint32_t opacity_animation{};
        std::uint32_t transform_handle{};
        std::uint32_t relative_transform_handle{};
        std::uint32_t color_interpolation_mode{};
        std::uint32_t mapping_mode{};
        std::uint32_t spread_method{};
        std::uint32_t first_point_animation{};
        std::uint32_t second_point_animation{};
        std::uint32_t radius_x_animation{};
        std::uint32_t radius_y_animation{};
        std::vector<gradient_stop_state> stops;
    };

    struct pen_state {
        double thickness{};
        double miter_limit{10.0};
        std::uint32_t brush_handle{};
        std::uint32_t thickness_animation_handle{};
        std::uint32_t start_line_cap{};
        std::uint32_t end_line_cap{};
        std::uint32_t dash_cap{};
        std::uint32_t line_join{};
        std::uint32_t dash_style_handle{};
    };

    struct dash_style_state {
        double offset{};
        std::uint32_t offset_animation_handle{};
        std::vector<double> intervals;
    };

    struct geometry_drawing_state {
        std::uint32_t brush_handle{};
        std::uint32_t pen_handle{};
        std::uint32_t geometry_handle{};
    };

    struct glyph_run_state {
        std::uint16_t flags{};
        float origin_x{};
        float origin_y{};
        float em_size{};
        double bounds_x{};
        double bounds_y{};
        double bounds_width{};
        double bounds_height{};
        std::uint16_t bidi_level{};
        std::uint16_t measuring_method{};
        std::uint32_t face_index{};
        std::uint32_t style_simulations{};
        std::vector<std::uint16_t> glyph_indices;
        std::vector<float> advances;
        std::vector<progpu_native_point> offsets;
        std::shared_ptr<const std::vector<std::byte>> font_data;
    };

    struct glyph_run_drawing_state {
        std::uint32_t glyph_run_handle{};
        std::uint32_t foreground_brush_handle{};
    };

    struct image_drawing_state {
        double x{};
        double y{};
        double width{};
        double height{};
        std::uint32_t image_source_handle{};
        std::uint32_t rect_animation_handle{};
    };

    struct video_drawing_state {
        double x{};
        double y{};
        double width{};
        double height{};
        std::uint32_t player_handle{};
        std::uint32_t rect_animation_handle{};
    };

    struct bitmap_source_state {
        std::uint32_t width{};
        std::uint32_t height{};
        std::uint32_t row_bytes{};
        double dpi_x{96.0};
        double dpi_y{96.0};
        std::vector<std::byte> pixels;
        bool external_image{};
    };

    struct media_player_state {
        std::uint32_t width{};
        std::uint32_t height{};
    };

    struct d3d_image_state {
        std::uint32_t width{};
        std::uint32_t height{};
        std::uint64_t content_version{};
        bool has_external_image{};
    };

    struct drawing_image_state {
        std::uint32_t drawing_handle{};
        double bounds_x{};
        double bounds_y{};
        double bounds_width{};
        double bounds_height{};
        bool has_bounds{};
        std::vector<std::byte> child_render_data;
    };

    struct guideline_set_state {
        enum class phase : std::uint8_t {
            start,
            quiet,
            animation,
            landing,
            flight
        };
        struct runtime_state {
            phase current_phase{phase::start};
            std::uint32_t bump_time{};
            float last_given_coordinate{};
            float last_offset{};
        };
        bool is_dynamic{};
        std::vector<double> guidelines_x;
        std::vector<double> guidelines_y;
        mutable std::vector<runtime_state> runtime_x;
        mutable std::vector<runtime_state> runtime_y;
    };

    using compact_guideline_state_map = std::unordered_map<
        std::uint32_t,
        guideline_set_state>;

    struct bitmap_cache_state {
        double render_at_scale{1.0};
        std::uint32_t render_at_scale_animation_handle{};
        bool snaps_to_device_pixels{};
        bool enable_clear_type{};
    };

    struct drawing_group_state {
        double opacity{1.0};
        std::uint32_t clip_geometry_handle{};
        std::uint32_t opacity_animation_handle{};
        std::uint32_t opacity_mask_handle{};
        std::uint32_t transform_handle{};
        std::uint32_t guideline_set_handle{};
        std::uint32_t edge_mode{};
        std::uint32_t bitmap_scaling_mode{};
        std::uint32_t clear_type_hint{};
        double bounds_x{};
        double bounds_y{};
        double bounds_width{};
        double bounds_height{};
        bool has_bounds{};
        std::vector<std::uint32_t> children;
        std::vector<std::byte> child_render_data;
    };

    struct render_scope_state {
        affine_2d_double transform;
        double opacity{1.0};
        progpu_native_image_rect clip_rect{};
        bool has_clip{};
        std::size_t clip_path_count{};
        std::size_t clip_segment_count{};
        std::size_t clip_boolean_node_count{};
        std::uint32_t mask_resource_index{
            PROGPU_NATIVE_SCENE_NO_INDEX};
        std::uint32_t image_sampling{
            PROGPU_NATIVE_IMAGE_SAMPLING_LINEAR};
        std::uint32_t guideline_resource_index{
            PROGPU_NATIVE_SCENE_NO_INDEX};
        bool per_point_guidelines{};
        bool edge_aliased{};
        bool clear_type_enabled{};
        bool subpixel_text_disabled{};
        std::uint32_t text_rendering_mode{};
        std::uint32_t text_hinting_mode{};
    };

    struct brush_use_state {
        double x{};
        double y{};
        double width{};
        double height{};
        affine_2d_double effective_transform{};
    };

    struct transform_state {
        enum class kind : std::uint32_t {
            matrix,
            translate,
            scale,
            skew,
            rotate,
            group
        } type{kind::matrix};
        affine_2d_double matrix{};
        std::array<double, 4U> values{};
        std::array<std::uint32_t, 4U> animations{};
        std::vector<std::uint32_t> children;
    };

    struct rotation3d_state {
        enum class kind : std::uint32_t {
            axis_angle,
            quaternion
        } type{kind::axis_angle};
        std::array<float, 3U> axis{};
        double angle{};
        std::array<float, 4U> quaternion{0.0F, 0.0F, 0.0F, 1.0F};
        std::uint32_t vector_animation_handle{};
        std::uint32_t scalar_animation_handle{};
    };

    struct transform3d_state {
        enum class kind : std::uint32_t {
            matrix,
            translate,
            scale,
            rotate,
            group
        } type{kind::matrix};
        progpu_native_matrix_4x4 matrix{identity_matrix_4x4()};
        std::array<double, 6U> values{};
        std::array<std::uint32_t, 6U> animations{};
        std::uint32_t rotation_handle{};
        std::vector<std::uint32_t> children;
    };

    struct camera3d_state {
        enum class kind : std::uint32_t {
            perspective,
            orthographic,
            matrix
        } type{kind::perspective};
        double near_plane{};
        double far_plane{};
        double projection_value{};
        std::array<float, 3U> position{};
        std::array<float, 3U> look_direction{};
        std::array<float, 3U> up_direction{};
        progpu_native_matrix_4x4 view{identity_matrix_4x4()};
        progpu_native_matrix_4x4 projection{identity_matrix_4x4()};
        std::uint32_t transform_handle{};
        std::array<std::uint32_t, 6U> animations{};
    };

    enum class fixed_geometry_kind : std::uint32_t {
        line,
        rectangle,
        ellipse
    };

    struct fixed_geometry_state {
        fixed_geometry_kind kind{fixed_geometry_kind::line};
        double first{};
        double second{};
        double third{};
        double fourth{};
        double radius_x{};
        double radius_y{};
        std::uint32_t transform_handle{};
        std::array<std::uint32_t, 3U> animations{};
    };

    struct path_stroke_contour_state {
        std::vector<progpu_native_point> points;
        std::vector<progpu_native_path_segment> segments;
        std::vector<std::uint8_t> smooth_joins;
        bool closed{};
        bool start_uses_dash_cap{};
        bool end_uses_dash_cap{};
    };

    struct path_geometry_state {
        std::vector<progpu_native_path_segment> segments;
        std::vector<progpu_native_path_segment> per_point_segments;
        std::vector<path_stroke_contour_state> stroke_contours;
        bool per_point_segments_supported{true};
        double left{};
        double top{};
        double right{};
        double bottom{};
        std::uint32_t transform_handle{};
        std::uint32_t fill_rule{};
    };

    struct geometry_group_state {
        std::vector<std::uint32_t> children;
        std::uint32_t transform_handle{};
        std::uint32_t fill_rule{};
    };

    struct combined_geometry_state {
        std::uint32_t transform_handle{};
        std::uint32_t combine_mode{};
        std::uint32_t geometry1_handle{};
        std::uint32_t geometry2_handle{};
    };

    struct resource_state {
        std::uint32_t type{};
        std::uint64_t generation{1U};
        std::vector<std::byte> render_data;
        bool has_compact_dynamic_guidelines{};
        mutable compact_guideline_state_map compact_guidelines;
    };

    struct viewport3d_scene_state {
        progpu_native_scene_camera_3d camera{};
        progpu_native_image_rect viewport{};
        std::vector<progpu_native_scene_mesh_3d> meshes;
        std::vector<progpu_native_scene_mesh_3d_vertex> vertices;
        std::vector<std::uint32_t> indices;
        std::vector<progpu_native_scene_light_3d> lights;
        std::vector<progpu_native_scene_brush> materials;
        std::vector<progpu_native_scene_gradient_stop> gradient_stops;
    };

    std::unordered_map<std::uint32_t, resource_state> resources;
    std::unordered_map<std::uint32_t, double> double_resources;
    std::unordered_map<std::uint32_t, std::array<float, 4U>> color_resources;
    std::unordered_map<std::uint32_t, point_resource_state> point_resources;
    std::unordered_map<std::uint32_t, rect_resource_state> rect_resources;
    std::unordered_map<std::uint32_t, std::array<double, 2U>> size_resources;
    std::unordered_map<std::uint32_t, affine_2d_double> matrix_resources;
    std::unordered_map<std::uint32_t, std::array<float, 3U>> point3d_resources;
    std::unordered_map<std::uint32_t, std::array<float, 3U>> vector3d_resources;
    std::unordered_map<std::uint32_t, std::array<float, 4U>>
        quaternion_resources;
    std::unordered_map<std::uint32_t, visual_state> visuals;
    std::unordered_map<std::uint32_t, viewport3d_visual_state>
        viewport3d_visuals;
    std::unordered_map<std::uint32_t, visual3d_state> visuals3d;
    std::unordered_map<std::uint32_t, model3d_group_state> model3d_groups;
    std::unordered_map<std::uint32_t, light3d_state> lights3d;
    std::unordered_map<std::uint32_t, geometry_model3d_state>
        geometry_models3d;
    std::unordered_map<std::uint32_t, mesh_geometry3d_state>
        mesh_geometries3d;
    std::unordered_map<std::uint32_t, material_group_state>
        material_groups3d;
    std::unordered_map<std::uint32_t, material3d_state> materials3d;
    std::unordered_map<std::uint32_t, viewport3d_scene_state>
        viewport3d_scenes;
    std::unordered_map<std::uint32_t, target_state> targets;
    std::unordered_map<std::uint32_t, transform_state> transforms;
    std::unordered_map<std::uint32_t, rotation3d_state> rotations3d;
    std::unordered_map<std::uint32_t, transform3d_state> transforms3d;
    std::unordered_map<std::uint32_t, camera3d_state> cameras3d;
    std::unordered_map<std::uint32_t, fixed_geometry_state> fixed_geometries;
    std::unordered_map<std::uint32_t, geometry_group_state> geometry_groups;
    std::unordered_map<std::uint32_t, combined_geometry_state>
        combined_geometries;
    std::unordered_map<std::uint32_t, path_geometry_state> path_geometries;
    std::unordered_map<std::uint32_t, solid_brush_state> solid_brushes;
    std::unordered_map<std::uint32_t, gradient_brush_state> gradient_brushes;
    std::unordered_map<std::uint32_t, tile_brush_state> tile_brushes;
    std::unordered_map<std::uint32_t, dash_style_state> dash_styles;
    std::unordered_map<std::uint32_t, pen_state> pens;
    std::unordered_map<std::uint32_t, geometry_drawing_state>
        geometry_drawings;
    std::unordered_map<std::uint32_t, glyph_run_state> glyph_runs;
    std::unordered_map<std::uint32_t, glyph_run_drawing_state>
        glyph_run_drawings;
    std::unordered_map<std::uint32_t, image_drawing_state> image_drawings;
    std::unordered_map<std::uint32_t, video_drawing_state> video_drawings;
    std::unordered_map<std::uint32_t, media_player_state> media_players;
    std::unordered_map<std::uint32_t, bitmap_source_state> bitmap_sources;
    std::unordered_map<std::uint32_t, d3d_image_state> d3d_images;
    std::unordered_map<std::uint32_t, drawing_image_state> drawing_images;
    std::unordered_map<std::uint32_t, drawing_group_state> drawing_groups;
    std::unordered_map<std::uint32_t, guideline_set_state> guideline_sets;
    std::unordered_map<std::uint32_t, bitmap_cache_state> bitmap_caches;
    std::unordered_map<std::uint32_t, effect_state> effects;

    bool require_resource(
        std::uint32_t handle,
        std::uint32_t expected_type = 0U) const noexcept {
        const auto found = resources.find(handle);
        return found != resources.end() &&
            (expected_type == 0U || found->second.type == expected_type);
    }

    bool require_visual(std::uint32_t handle) const noexcept {
        const auto found = resources.find(handle);
        return found != resources.end() && is_visual_type(found->second.type) &&
            visuals.contains(handle);
    }

    bool require_target(std::uint32_t handle) const noexcept {
        const auto found = resources.find(handle);
        return found != resources.end() && is_target_type(found->second.type) &&
            targets.contains(handle);
    }

    bool require_transform(std::uint32_t handle) const noexcept {
        const auto found = resources.find(handle);
        return found != resources.end() &&
            is_transform_type(found->second.type);
    }

    bool require_rotation3d(std::uint32_t handle) const noexcept {
        const auto found = resources.find(handle);
        return found != resources.end() &&
            is_rotation3d_type(found->second.type);
    }

    bool require_transform3d(std::uint32_t handle) const noexcept {
        const auto found = resources.find(handle);
        return found != resources.end() &&
            is_transform3d_type(found->second.type);
    }

    bool require_camera3d(std::uint32_t handle) const noexcept {
        const auto found = resources.find(handle);
        return found != resources.end() &&
            is_camera3d_type(found->second.type);
    }

    bool require_model3d(std::uint32_t handle) const noexcept {
        const auto found = resources.find(handle);
        return found != resources.end() && is_model3d_type(found->second.type);
    }

    bool require_material3d(std::uint32_t handle) const noexcept {
        const auto found = resources.find(handle);
        return found != resources.end() &&
            is_material3d_type(found->second.type);
    }

    bool require_brush(std::uint32_t handle) const noexcept {
        const auto found = resources.find(handle);
        return found != resources.end() &&
            (found->second.type == type_solid_color_brush ||
             found->second.type == type_linear_gradient_brush ||
             found->second.type == type_radial_gradient_brush ||
             found->second.type == type_image_brush ||
             found->second.type == type_drawing_brush ||
             found->second.type == type_visual_brush);
    }

    bool require_effect(std::uint32_t handle) const noexcept {
        const auto found = resources.find(handle);
        return found != resources.end() && is_effect_type(found->second.type) &&
            effects.contains(handle);
    }

    bool has_brush_state(std::uint32_t handle) const noexcept {
        return solid_brushes.contains(handle) ||
            gradient_brushes.contains(handle) || tile_brushes.contains(handle);
    }

    bool transform_reaches(
        std::uint32_t start,
        std::uint32_t destination) const {
        std::vector<std::uint32_t> pending{start};
        std::unordered_set<std::uint32_t> visited;
        while (!pending.empty()) {
            const std::uint32_t current = pending.back();
            pending.pop_back();
            if (current == destination) {
                return true;
            }
            if (!visited.insert(current).second) {
                continue;
            }
            const auto found = transforms.find(current);
            if (found == transforms.end() ||
                found->second.type != transform_state::kind::group) {
                continue;
            }
            pending.insert(
                pending.end(),
                found->second.children.begin(),
                found->second.children.end());
        }
        return false;
    }

    bool transform3d_reaches(
        std::uint32_t start,
        std::uint32_t destination) const {
        std::vector<std::uint32_t> pending{start};
        std::unordered_set<std::uint32_t> visited;
        while (!pending.empty()) {
            const std::uint32_t current = pending.back();
            pending.pop_back();
            if (current == destination) {
                return true;
            }
            if (!visited.insert(current).second) {
                continue;
            }
            const auto found = transforms3d.find(current);
            if (found == transforms3d.end() ||
                found->second.type != transform3d_state::kind::group) {
                continue;
            }
            pending.insert(
                pending.end(),
                found->second.children.begin(),
                found->second.children.end());
        }
        return false;
    }

    bool visual3d_reaches(
        std::uint32_t start,
        std::uint32_t destination) const {
        std::vector<std::uint32_t> pending{start};
        std::unordered_set<std::uint32_t> visited;
        while (!pending.empty()) {
            const std::uint32_t current = pending.back();
            pending.pop_back();
            if (current == destination) {
                return true;
            }
            if (!visited.insert(current).second) {
                continue;
            }
            const auto found = visuals3d.find(current);
            if (found != visuals3d.end()) {
                pending.insert(
                    pending.end(),
                    found->second.children.begin(),
                    found->second.children.end());
            }
        }
        return false;
    }

    bool model3d_reaches(
        std::uint32_t start,
        std::uint32_t destination) const {
        std::vector<std::uint32_t> pending{start};
        std::unordered_set<std::uint32_t> visited;
        while (!pending.empty()) {
            const std::uint32_t current = pending.back();
            pending.pop_back();
            if (current == destination) {
                return true;
            }
            if (!visited.insert(current).second) {
                continue;
            }
            const auto found = model3d_groups.find(current);
            if (found != model3d_groups.end()) {
                pending.insert(
                    pending.end(),
                    found->second.children.begin(),
                    found->second.children.end());
            }
        }
        return false;
    }

    bool material3d_reaches(
        std::uint32_t start,
        std::uint32_t destination) const {
        std::vector<std::uint32_t> pending{start};
        std::unordered_set<std::uint32_t> visited;
        while (!pending.empty()) {
            const std::uint32_t current = pending.back();
            pending.pop_back();
            if (current == destination) {
                return true;
            }
            if (!visited.insert(current).second) {
                continue;
            }
            const auto found = material_groups3d.find(current);
            if (found != material_groups3d.end()) {
                pending.insert(
                    pending.end(),
                    found->second.children.begin(),
                    found->second.children.end());
            }
        }
        return false;
    }

    bool drawing_reaches(
        std::uint32_t start,
        std::uint32_t destination) const {
        std::vector<std::uint32_t> pending{start};
        std::unordered_set<std::uint32_t> visited;
        while (!pending.empty()) {
            const std::uint32_t current = pending.back();
            pending.pop_back();
            if (current == destination) {
                return true;
            }
            if (!visited.insert(current).second) {
                continue;
            }
            const auto found = drawing_groups.find(current);
            if (found == drawing_groups.end()) {
                continue;
            }
            pending.insert(
                pending.end(),
                found->second.children.begin(),
                found->second.children.end());
        }
        return false;
    }

    status resolve_animated_double(
        double base_value,
        std::uint32_t animation_handle,
        double& value) const noexcept {
        if (animation_handle == 0U) {
            value = base_value;
            return status::success;
        }
        const auto animation = double_resources.find(animation_handle);
        if (animation == double_resources.end()) {
            return status::invalid_handle;
        }
        value = animation->second;
        return status::success;
    }

    status resolve_animated_color(
        progpu_native_color base_value,
        std::uint32_t animation_handle,
        progpu_native_color& value) const noexcept {
        if (animation_handle == 0U) {
            value = base_value;
            return status::success;
        }
        const auto animation = color_resources.find(animation_handle);
        if (animation == color_resources.end()) {
            return status::invalid_handle;
        }
        value = {
            animation->second[0],
            animation->second[1],
            animation->second[2],
            animation->second[3]};
        return status::success;
    }

    status resolve_solid_brush(
        std::uint32_t handle,
        progpu_native_color& color,
        double& opacity) const noexcept {
        const auto brush = solid_brushes.find(handle);
        if (brush == solid_brushes.end()) {
            return require_brush(handle)
                ? status::unsupported_command
                : status::invalid_handle;
        }
        const status opacity_status = resolve_animated_double(
            brush->second.opacity,
            brush->second.opacity_animation_handle,
            opacity);
        if (opacity_status != status::success) {
            return opacity_status;
        }
        const status color_status = resolve_animated_color(
            brush->second.color,
            brush->second.color_animation_handle,
            color);
        if (color_status != status::success) {
            return color_status;
        }
        if (!finite_double_as_float(opacity) ||
            opacity < 0.0 || opacity > 1.0 ||
            !std::isfinite(color.r) || !std::isfinite(color.g) ||
            !std::isfinite(color.b) || !std::isfinite(color.a)) {
            return status::invalid_graph;
        }
        return status::success;
    }

    status resolve_pen(
        std::uint32_t handle,
        pen_state& value) const noexcept {
        const auto pen = pens.find(handle);
        if (pen == pens.end()) {
            return status::invalid_handle;
        }
        value = pen->second;
        const status thickness_status = resolve_animated_double(
            value.thickness,
            value.thickness_animation_handle,
            value.thickness);
        if (thickness_status != status::success) {
            return thickness_status;
        }
        if (!finite_double_as_float(value.thickness) ||
            value.thickness < 0.0) {
            return status::invalid_graph;
        }
        return status::success;
    }

    status resolve_dash_offset(
        std::uint32_t handle,
        double& value) const noexcept {
        const auto dash = dash_styles.find(handle);
        if (dash == dash_styles.end()) {
            return status::invalid_handle;
        }
        const status offset_status = resolve_animated_double(
            dash->second.offset,
            dash->second.offset_animation_handle,
            value);
        if (offset_status != status::success) {
            return offset_status;
        }
        return finite_double_as_float(value)
            ? status::success
            : status::invalid_graph;
    }

    status resolve_effect(
        std::uint32_t handle,
        effect_state& value) const noexcept {
        const auto effect = effects.find(handle);
        if (effect == effects.end()) {
            return status::invalid_handle;
        }
        value = effect->second;
        if (value.type == effect_state::kind::blur) {
            const status radius_status = resolve_animated_double(
                value.radius, value.animations[0], value.radius);
            if (radius_status != status::success) {
                return radius_status;
            }
        } else {
            const status depth_status = resolve_animated_double(
                value.shadow_depth,
                value.animations[0],
                value.shadow_depth);
            if (depth_status != status::success) {
                return depth_status;
            }
            const status color_status = resolve_animated_color(
                value.color, value.animations[1], value.color);
            if (color_status != status::success) {
                return color_status;
            }
            const status direction_status = resolve_animated_double(
                value.direction, value.animations[2], value.direction);
            if (direction_status != status::success) {
                return direction_status;
            }
            const status opacity_status = resolve_animated_double(
                value.opacity, value.animations[3], value.opacity);
            if (opacity_status != status::success) {
                return opacity_status;
            }
            const status radius_status = resolve_animated_double(
                value.radius, value.animations[4], value.radius);
            if (radius_status != status::success) {
                return radius_status;
            }
        }
        if (!finite_double_as_float(value.radius) ||
            !finite_double_as_float(value.shadow_depth) ||
            !finite_double_as_float(value.direction) ||
            !finite_double_as_float(value.opacity) ||
            !std::isfinite(value.color.r) ||
            !std::isfinite(value.color.g) ||
            !std::isfinite(value.color.b) ||
            !std::isfinite(value.color.a)) {
            return status::invalid_graph;
        }
        value.radius = std::max(0.0, value.radius);
        value.shadow_depth = std::max(0.0, value.shadow_depth);
        value.opacity = std::clamp(value.opacity, 0.0, 1.0);
        return status::success;
    }

    status resolve_fixed_geometry(
        std::uint32_t handle,
        fixed_geometry_state& value) const noexcept {
        const auto geometry = fixed_geometries.find(handle);
        if (geometry == fixed_geometries.end()) {
            return status::invalid_handle;
        }
        value = geometry->second;
        if (value.kind == fixed_geometry_kind::line) {
            const status start_status = resolve_animated_point(
                value.first,
                value.second,
                value.animations[0],
                value.first,
                value.second);
            if (start_status != status::success) {
                return start_status;
            }
            const status end_status = resolve_animated_point(
                value.third,
                value.fourth,
                value.animations[1],
                value.third,
                value.fourth);
            if (end_status != status::success) {
                return end_status;
            }
        } else if (value.kind == fixed_geometry_kind::rectangle) {
            const status radius_x_status = resolve_animated_double(
                value.radius_x,
                value.animations[0],
                value.radius_x);
            if (radius_x_status != status::success) {
                return radius_x_status;
            }
            const status radius_y_status = resolve_animated_double(
                value.radius_y,
                value.animations[1],
                value.radius_y);
            if (radius_y_status != status::success) {
                return radius_y_status;
            }
            const status rect_status = resolve_animated_rect(
                value.first,
                value.second,
                value.third,
                value.fourth,
                value.animations[2],
                value.first,
                value.second,
                value.third,
                value.fourth);
            if (rect_status != status::success) {
                return rect_status;
            }
        } else {
            const status radius_x_status = resolve_animated_double(
                value.third,
                value.animations[0],
                value.third);
            if (radius_x_status != status::success) {
                return radius_x_status;
            }
            const status radius_y_status = resolve_animated_double(
                value.fourth,
                value.animations[1],
                value.fourth);
            if (radius_y_status != status::success) {
                return radius_y_status;
            }
            const status center_status = resolve_animated_point(
                value.first,
                value.second,
                value.animations[2],
                value.first,
                value.second);
            if (center_status != status::success) {
                return center_status;
            }
        }
        if (!finite_double_as_float(value.first) ||
            !finite_double_as_float(value.second) ||
            !finite_double_as_float(value.third) ||
            !finite_double_as_float(value.fourth) ||
            !finite_double_as_float(value.radius_x) ||
            !finite_double_as_float(value.radius_y) ||
            ((value.kind == fixed_geometry_kind::rectangle ||
              value.kind == fixed_geometry_kind::ellipse) &&
             (value.third < 0.0 || value.fourth < 0.0)) ||
            (value.kind == fixed_geometry_kind::rectangle &&
             (value.radius_x < 0.0 || value.radius_y < 0.0))) {
            return status::invalid_graph;
        }
        return status::success;
    }

    status resolve_animated_point(
        double base_x,
        double base_y,
        std::uint32_t animation_handle,
        double& x,
        double& y) const noexcept {
        if (animation_handle == 0U) {
            x = base_x;
            y = base_y;
            return status::success;
        }
        const auto animation = point_resources.find(animation_handle);
        if (animation == point_resources.end()) {
            return status::invalid_handle;
        }
        x = animation->second.x;
        y = animation->second.y;
        return status::success;
    }

    status resolve_animated_rect(
        double base_x,
        double base_y,
        double base_width,
        double base_height,
        std::uint32_t animation_handle,
        double& x,
        double& y,
        double& width,
        double& height) const noexcept {
        if (animation_handle == 0U) {
            x = base_x;
            y = base_y;
            width = base_width;
            height = base_height;
            return status::success;
        }
        const auto animation = rect_resources.find(animation_handle);
        if (animation == rect_resources.end()) {
            return status::invalid_handle;
        }
        x = animation->second.x;
        y = animation->second.y;
        width = animation->second.width;
        height = animation->second.height;
        return status::success;
    }

    status resolve_leaf_transform(
        const transform_state& transform,
        affine_2d_double& matrix) const noexcept {
        if (transform.type == transform_state::kind::matrix) {
            if (transform.animations[0] == 0U) {
                matrix = transform.matrix;
                return status::success;
            }
            const auto animation = matrix_resources.find(
                transform.animations[0]);
            if (animation == matrix_resources.end()) {
                return status::invalid_handle;
            }
            matrix = animation->second;
            return status::success;
        }

        std::array<double, 4U> values{};
        const std::size_t value_count =
            transform.type == transform_state::kind::translate ? 2U
            : transform.type == transform_state::kind::rotate ? 3U
            : 4U;
        for (std::size_t index = 0U; index < value_count; ++index) {
            const status value_status = resolve_animated_double(
                transform.values[index],
                transform.animations[index],
                values[index]);
            if (value_status != status::success) {
                return value_status;
            }
            if (!finite_double_as_float(values[index])) {
                return status::invalid_graph;
            }
        }

        const float first = static_cast<float>(values[0]);
        const float second = static_cast<float>(values[1]);
        if (transform.type == transform_state::kind::translate) {
            matrix = {1.0, 0.0, 0.0, 1.0, first, second};
            return status::success;
        }

        const float center_x = transform.type == transform_state::kind::rotate
            ? static_cast<float>(values[1])
            : static_cast<float>(values[2]);
        const float center_y = transform.type == transform_state::kind::rotate
            ? static_cast<float>(values[2])
            : static_cast<float>(values[3]);
        affine_2d_double core{};
        if (transform.type == transform_state::kind::scale) {
            core.m11 = first;
            core.m22 = second;
        } else if (transform.type == transform_state::kind::skew) {
            const float angle_x = static_cast<float>(
                std::fmod(values[0], 360.0)) *
                std::numbers::pi_v<float> / 180.0F;
            const float angle_y = static_cast<float>(
                std::fmod(values[1], 360.0)) *
                std::numbers::pi_v<float> / 180.0F;
            core.m12 = std::tan(angle_y);
            core.m21 = std::tan(angle_x);
        } else if (transform.type == transform_state::kind::rotate) {
            const float radians = static_cast<float>(
                std::fmod(values[0], 360.0)) *
                std::numbers::pi_v<float> / 180.0F;
            const float cosine = std::cos(radians);
            const float sine = std::sin(radians);
            core = {cosine, sine, -sine, cosine, 0.0, 0.0};
        } else {
            return status::invalid_graph;
        }
        const affine_2d_double before{
            1.0, 0.0, 0.0, 1.0, -center_x, -center_y};
        const affine_2d_double after{
            1.0, 0.0, 0.0, 1.0, center_x, center_y};
        matrix = compose_wpf_affine(
            compose_wpf_affine(before, core),
            after);
        return try_quantize_wpf_affine(matrix)
            ? status::success
            : status::invalid_graph;
    }

    status resolve_transform_core(
        std::uint32_t handle,
        affine_2d_double& matrix,
        std::array<std::uint32_t, maximum_visual_depth>& active,
        std::size_t depth) const noexcept {
        if (depth >= active.size() ||
            std::find(active.begin(), active.begin() + depth, handle) !=
                active.begin() + depth) {
            return status::invalid_graph;
        }
        const auto resource = resources.find(handle);
        const auto transform = transforms.find(handle);
        if (resource == resources.end() ||
            !is_transform_type(resource->second.type) ||
            transform == transforms.end()) {
            return status::invalid_handle;
        }
        if (transform->second.type != transform_state::kind::group) {
            return resolve_leaf_transform(transform->second, matrix);
        }
        active[depth] = handle;
        matrix = {};
        for (const std::uint32_t child : transform->second.children) {
            affine_2d_double child_matrix{};
            const status child_status = resolve_transform_core(
                child,
                child_matrix,
                active,
                depth + 1U);
            if (child_status != status::success) {
                return child_status;
            }
            matrix = compose_wpf_affine(matrix, child_matrix);
        }
        return status::success;
    }

    status resolve_transform(
        std::uint32_t handle,
        affine_2d_double& matrix) const noexcept {
        std::array<std::uint32_t, maximum_visual_depth> active{};
        return resolve_transform_core(handle, matrix, active, 0U);
    }

    status resolve_animated_vector3(
        const std::array<float, 3U>& base_value,
        std::uint32_t animation_handle,
        bool point,
        std::array<float, 3U>& value) const noexcept {
        if (animation_handle == 0U) {
            value = base_value;
        } else {
            const auto& values = point
                ? point3d_resources
                : vector3d_resources;
            const auto animation = values.find(animation_handle);
            if (animation == values.end()) {
                return status::invalid_handle;
            }
            value = animation->second;
        }
        return std::ranges::all_of(
            value,
            [](float component) noexcept {
                return std::isfinite(component);
            })
            ? status::success
            : status::invalid_graph;
    }

    status resolve_rotation3d(
        std::uint32_t handle,
        progpu_native_matrix_4x4& matrix) const noexcept {
        if (handle == 0U) {
            matrix = identity_matrix_4x4();
            return status::success;
        }
        const auto resource = resources.find(handle);
        const auto rotation = rotations3d.find(handle);
        if (resource == resources.end() ||
            !is_rotation3d_type(resource->second.type) ||
            rotation == rotations3d.end()) {
            return status::invalid_handle;
        }
        std::array<float, 4U> quaternion{};
        if (rotation->second.type == rotation3d_state::kind::quaternion) {
            if (rotation->second.vector_animation_handle == 0U) {
                quaternion = rotation->second.quaternion;
            } else {
                const auto animation = quaternion_resources.find(
                    rotation->second.vector_animation_handle);
                if (animation == quaternion_resources.end()) {
                    return status::invalid_handle;
                }
                quaternion = animation->second;
            }
        } else {
            std::array<float, 3U> axis{};
            const status axis_status = resolve_animated_vector3(
                rotation->second.axis,
                rotation->second.vector_animation_handle,
                false,
                axis);
            if (axis_status != status::success) {
                return axis_status;
            }
            double angle = 0.0;
            const status angle_status = resolve_animated_double(
                rotation->second.angle,
                rotation->second.scalar_animation_handle,
                angle);
            if (angle_status != status::success) {
                return angle_status;
            }
            if (!finite_double_as_float(angle)) {
                return status::invalid_graph;
            }
            const float length_squared = axis[0] * axis[0] +
                axis[1] * axis[1] + axis[2] * axis[2];
            if (!(length_squared > std::numeric_limits<float>::min())) {
                matrix = identity_matrix_4x4();
                return status::success;
            }
            const float inverse_length = 1.0F / std::sqrt(length_squared);
            if (angle > 360.0 || angle < -360.0) {
                angle = std::fmod(angle, 360.0);
            }
            const float radians = static_cast<float>(angle) *
                std::numbers::pi_v<float> / 180.0F;
            const float sine = std::sin(radians * 0.5F);
            quaternion = {
                axis[0] * inverse_length * sine,
                axis[1] * inverse_length * sine,
                axis[2] * inverse_length * sine,
                std::cos(radians * 0.5F)};
        }
        if (!std::ranges::all_of(
                quaternion,
                [](float component) noexcept {
                    return std::isfinite(component);
                })) {
            return status::invalid_graph;
        }
        const float x2 = quaternion[0] + quaternion[0];
        const float y2 = quaternion[1] + quaternion[1];
        const float z2 = quaternion[2] + quaternion[2];
        const float xx = quaternion[0] * x2;
        const float xy = quaternion[0] * y2;
        const float xz = quaternion[0] * z2;
        const float yy = quaternion[1] * y2;
        const float yz = quaternion[1] * z2;
        const float zz = quaternion[2] * z2;
        const float wx = quaternion[3] * x2;
        const float wy = quaternion[3] * y2;
        const float wz = quaternion[3] * z2;
        matrix = {
            1.0F - (yy + zz), xy + wz, xz - wy, 0.0F,
            xy - wz, 1.0F - (xx + zz), yz + wx, 0.0F,
            xz + wy, yz - wx, 1.0F - (xx + yy), 0.0F,
            0.0F, 0.0F, 0.0F, 1.0F};
        return finite_matrix_4x4(matrix)
            ? status::success
            : status::invalid_graph;
    }

    status resolve_transform3d_leaf(
        const transform3d_state& transform,
        progpu_native_matrix_4x4& matrix) const noexcept {
        if (transform.type == transform3d_state::kind::matrix) {
            matrix = transform.matrix;
            return finite_matrix_4x4(matrix)
                ? status::success
                : status::invalid_graph;
        }
        const std::size_t value_count =
            transform.type == transform3d_state::kind::scale ? 6U : 3U;
        std::array<double, 6U> values{};
        for (std::size_t index = 0U; index < value_count; ++index) {
            const status value_status = resolve_animated_double(
                transform.values[index],
                transform.animations[index],
                values[index]);
            if (value_status != status::success) {
                return value_status;
            }
            if (!finite_double_as_float(values[index])) {
                return status::invalid_graph;
            }
        }
        if (transform.type == transform3d_state::kind::translate) {
            matrix = identity_matrix_4x4();
            matrix.m41 = static_cast<float>(values[0]);
            matrix.m42 = static_cast<float>(values[1]);
            matrix.m43 = static_cast<float>(values[2]);
            return status::success;
        }
        if (transform.type == transform3d_state::kind::scale) {
            const float scale_x = static_cast<float>(values[0]);
            const float scale_y = static_cast<float>(values[1]);
            const float scale_z = static_cast<float>(values[2]);
            const float center_x = static_cast<float>(values[3]);
            const float center_y = static_cast<float>(values[4]);
            const float center_z = static_cast<float>(values[5]);
            matrix = {
                scale_x, 0.0F, 0.0F, 0.0F,
                0.0F, scale_y, 0.0F, 0.0F,
                0.0F, 0.0F, scale_z, 0.0F,
                center_x - scale_x * center_x,
                center_y - scale_y * center_y,
                center_z - scale_z * center_z,
                1.0F};
            return status::success;
        }
        if (transform.type != transform3d_state::kind::rotate) {
            return status::invalid_graph;
        }
        const status rotation_status = resolve_rotation3d(
            transform.rotation_handle, matrix);
        if (rotation_status != status::success) {
            return rotation_status;
        }
        const float center_x = static_cast<float>(values[0]);
        const float center_y = static_cast<float>(values[1]);
        const float center_z = static_cast<float>(values[2]);
        matrix.m41 = center_x - matrix.m11 * center_x -
            matrix.m21 * center_y - matrix.m31 * center_z;
        matrix.m42 = center_y - matrix.m12 * center_x -
            matrix.m22 * center_y - matrix.m32 * center_z;
        matrix.m43 = center_z - matrix.m13 * center_x -
            matrix.m23 * center_y - matrix.m33 * center_z;
        return finite_matrix_4x4(matrix)
            ? status::success
            : status::invalid_graph;
    }

    status resolve_transform3d_core(
        std::uint32_t handle,
        progpu_native_matrix_4x4& matrix,
        std::array<std::uint32_t, maximum_visual_depth>& active,
        std::size_t depth) const noexcept {
        if (depth >= active.size() ||
            std::find(active.begin(), active.begin() + depth, handle) !=
                active.begin() + depth) {
            return status::invalid_graph;
        }
        const auto resource = resources.find(handle);
        const auto transform = transforms3d.find(handle);
        if (resource == resources.end() ||
            !is_transform3d_type(resource->second.type) ||
            transform == transforms3d.end()) {
            return status::invalid_handle;
        }
        if (transform->second.type != transform3d_state::kind::group) {
            return resolve_transform3d_leaf(transform->second, matrix);
        }
        active[depth] = handle;
        matrix = identity_matrix_4x4();
        for (const std::uint32_t child : transform->second.children) {
            progpu_native_matrix_4x4 child_matrix{};
            const status child_status = resolve_transform3d_core(
                child, child_matrix, active, depth + 1U);
            if (child_status != status::success) {
                return child_status;
            }
            matrix = multiply_matrix_4x4(matrix, child_matrix);
        }
        return finite_matrix_4x4(matrix)
            ? status::success
            : status::invalid_graph;
    }

    status resolve_transform3d(
        std::uint32_t handle,
        progpu_native_matrix_4x4& matrix) const noexcept {
        if (handle == 0U) {
            matrix = identity_matrix_4x4();
            return status::success;
        }
        std::array<std::uint32_t, maximum_visual_depth> active{};
        return resolve_transform3d_core(handle, matrix, active, 0U);
    }

    status resolve_camera3d(
        std::uint32_t handle,
        double aspect_ratio,
        progpu_native_scene_camera_3d& camera) const noexcept {
        const auto resource = resources.find(handle);
        const auto source = cameras3d.find(handle);
        if (resource == resources.end() ||
            !is_camera3d_type(resource->second.type) ||
            source == cameras3d.end()) {
            return status::invalid_handle;
        }
        if (!finite_double_as_float(aspect_ratio) || aspect_ratio <= 0.0) {
            return status::invalid_graph;
        }
        progpu_native_matrix_4x4 view{};
        progpu_native_matrix_4x4 projection{};
        if (source->second.type == camera3d_state::kind::matrix) {
            view = source->second.view;
            projection = source->second.projection;
        } else {
            std::array<float, 3U> position{};
            std::array<float, 3U> look_direction{};
            std::array<float, 3U> up_direction{};
            status value_status = resolve_animated_vector3(
                source->second.position,
                source->second.animations[2],
                true,
                position);
            if (value_status == status::success) {
                value_status = resolve_animated_vector3(
                    source->second.look_direction,
                    source->second.animations[3],
                    false,
                    look_direction);
            }
            if (value_status == status::success) {
                value_status = resolve_animated_vector3(
                    source->second.up_direction,
                    source->second.animations[4],
                    false,
                    up_direction);
            }
            if (value_status != status::success ||
                !try_create_look_at_rh(
                    position, look_direction, up_direction, view)) {
                return value_status == status::success
                    ? status::invalid_graph
                    : value_status;
            }
            double near_plane = 0.0;
            double far_plane = 0.0;
            double projection_value = 0.0;
            value_status = resolve_animated_double(
                source->second.near_plane,
                source->second.animations[0],
                near_plane);
            if (value_status == status::success) {
                value_status = resolve_animated_double(
                    source->second.far_plane,
                    source->second.animations[1],
                    far_plane);
            }
            if (value_status == status::success) {
                value_status = resolve_animated_double(
                    source->second.projection_value,
                    source->second.animations[5],
                    projection_value);
            }
            const bool infinite_far = std::isinf(far_plane) &&
                far_plane > 0.0;
            if (value_status != status::success ||
                !finite_double_as_float(near_plane) ||
                (!finite_double_as_float(far_plane) && !infinite_far) ||
                !finite_double_as_float(projection_value) ||
                far_plane <= near_plane || projection_value <= 0.0 ||
                (source->second.type == camera3d_state::kind::perspective &&
                 (near_plane <= 0.0 || projection_value >= 180.0))) {
                return value_status == status::success
                    ? status::invalid_graph
                    : value_status;
            }
            const float near_value = static_cast<float>(near_plane);
            const float far_value = static_cast<float>(far_plane);
            if (source->second.type == camera3d_state::kind::perspective) {
                const double radians = projection_value *
                    std::numbers::pi_v<double> / 180.0;
                const double half_width_depth_ratio =
                    std::tan(radians * 0.5);
                const float m11 = static_cast<float>(
                    1.0 / half_width_depth_ratio);
                const float m22 = static_cast<float>(
                    aspect_ratio / half_width_depth_ratio);
                const float m33 = infinite_far
                    ? -1.0F
                    : far_value / (near_value - far_value);
                projection = {
                    m11, 0.0F, 0.0F, 0.0F,
                    0.0F, m22, 0.0F, 0.0F,
                    0.0F, 0.0F, m33, -1.0F,
                    0.0F, 0.0F, near_value * m33, 0.0F};
            } else {
                const float width = static_cast<float>(projection_value);
                const float height = static_cast<float>(
                    projection_value / aspect_ratio);
                const float m33 = infinite_far
                    ? -0.0F
                    : 1.0F / (near_value - far_value);
                projection = {
                    2.0F / width, 0.0F, 0.0F, 0.0F,
                    0.0F, 2.0F / height, 0.0F, 0.0F,
                    0.0F, 0.0F, m33, 0.0F,
                    0.0F, 0.0F, near_value * m33, 1.0F};
            }
        }
        if (!finite_matrix_4x4(view) || !finite_matrix_4x4(projection)) {
            return status::invalid_graph;
        }
        if (source->second.transform_handle != 0U) {
            progpu_native_matrix_4x4 transform{};
            const status transform_status = resolve_transform3d(
                source->second.transform_handle, transform);
            if (transform_status != status::success) {
                return transform_status;
            }
            progpu_native_matrix_4x4 inverse_transform{};
            if (!try_invert_matrix_4x4(transform, inverse_transform)) {
                return status::invalid_graph;
            }
            view = multiply_matrix_4x4(inverse_transform, view);
        }
        progpu_native_matrix_4x4 camera_to_world{};
        progpu_native_point_3d position{};
        if (!try_invert_matrix_4x4(view, camera_to_world) ||
            !try_transform_origin(camera_to_world, position)) {
            return status::invalid_graph;
        }
        camera = {};
        camera.struct_size = sizeof(camera);
        camera.projection = projection;
        camera.view = view;
        camera.camera_position = position;
        return semantic::is_valid_semantic_camera_3d(camera)
            ? status::success
            : status::invalid_graph;
    }

    static bool normalize_vector3(std::array<float, 3U>& value) noexcept {
        const float length_squared = value[0] * value[0] +
            value[1] * value[1] + value[2] * value[2];
        if (!(length_squared > std::numeric_limits<float>::min()) ||
            !std::isfinite(length_squared)) {
            value = {};
            return false;
        }
        const float inverse_length = 1.0F / std::sqrt(length_squared);
        value[0] *= inverse_length;
        value[1] *= inverse_length;
        value[2] *= inverse_length;
        return std::ranges::all_of(
            value,
            [](float component) noexcept {
                return std::isfinite(component);
            });
    }

    static void normalize_vector3_buffer(
        std::span<std::array<float, 3U>> values) noexcept {
        static_assert(sizeof(std::array<float, 3U>) == 3U * sizeof(float));
        std::size_t index = 0U;
#if defined(PROGPU_NATIVE_MIL_INTRINSICS_NEON)
        const float32x4_t one = vdupq_n_f32(1.0F);
        const float32x4_t zero = vdupq_n_f32(0.0F);
        const float32x4_t minimum =
            vdupq_n_f32(std::numeric_limits<float>::min());
        const float32x4_t maximum =
            vdupq_n_f32(std::numeric_limits<float>::max());
        for (; index + 4U <= values.size(); index += 4U) {
            float32x4x3_t coordinates = vld3q_f32(values[index].data());
            const float32x4_t length_squared = vaddq_f32(
                vaddq_f32(
                    vmulq_f32(coordinates.val[0], coordinates.val[0]),
                    vmulq_f32(coordinates.val[1], coordinates.val[1])),
                vmulq_f32(coordinates.val[2], coordinates.val[2]));
            const uint32x4_t valid = vandq_u32(
                vcgtq_f32(length_squared, minimum),
                vcleq_f32(length_squared, maximum));
            const float32x4_t inverse_length = vdivq_f32(
                one, vsqrtq_f32(length_squared));
            coordinates.val[0] = vbslq_f32(
                valid,
                vmulq_f32(coordinates.val[0], inverse_length),
                zero);
            coordinates.val[1] = vbslq_f32(
                valid,
                vmulq_f32(coordinates.val[1], inverse_length),
                zero);
            coordinates.val[2] = vbslq_f32(
                valid,
                vmulq_f32(coordinates.val[2], inverse_length),
                zero);
            vst3q_f32(values[index].data(), coordinates);
        }
#elif defined(PROGPU_NATIVE_MIL_INTRINSICS_SSE2)
        const __m128 one = _mm_set1_ps(1.0F);
        const __m128 minimum =
            _mm_set1_ps(std::numeric_limits<float>::min());
        const __m128 maximum =
            _mm_set1_ps(std::numeric_limits<float>::max());
        for (; index + 4U <= values.size(); index += 4U) {
            const __m128 x = _mm_set_ps(
                values[index + 3U][0],
                values[index + 2U][0],
                values[index + 1U][0],
                values[index][0]);
            const __m128 y = _mm_set_ps(
                values[index + 3U][1],
                values[index + 2U][1],
                values[index + 1U][1],
                values[index][1]);
            const __m128 z = _mm_set_ps(
                values[index + 3U][2],
                values[index + 2U][2],
                values[index + 1U][2],
                values[index][2]);
            const __m128 length_squared = _mm_add_ps(
                _mm_add_ps(_mm_mul_ps(x, x), _mm_mul_ps(y, y)),
                _mm_mul_ps(z, z));
            const __m128 valid = _mm_and_ps(
                _mm_cmpgt_ps(length_squared, minimum),
                _mm_cmple_ps(length_squared, maximum));
            const __m128 inverse_length = _mm_div_ps(
                one, _mm_sqrt_ps(length_squared));
            alignas(16) std::array<float, 4U> normalized_x{};
            alignas(16) std::array<float, 4U> normalized_y{};
            alignas(16) std::array<float, 4U> normalized_z{};
            _mm_store_ps(
                normalized_x.data(),
                _mm_and_ps(_mm_mul_ps(x, inverse_length), valid));
            _mm_store_ps(
                normalized_y.data(),
                _mm_and_ps(_mm_mul_ps(y, inverse_length), valid));
            _mm_store_ps(
                normalized_z.data(),
                _mm_and_ps(_mm_mul_ps(z, inverse_length), valid));
            for (std::size_t lane = 0U; lane < 4U; ++lane) {
                values[index + lane] = {
                    normalized_x[lane],
                    normalized_y[lane],
                    normalized_z[lane]};
            }
        }
#endif
        for (; index < values.size(); ++index) {
            normalize_vector3(values[index]);
        }
    }

    static bool transform_vector3(
        const std::array<float, 3U>& source,
        const progpu_native_matrix_4x4& transform,
        std::array<float, 3U>& destination) noexcept {
        destination = {
            source[0] * transform.m11 + source[1] * transform.m21 +
                source[2] * transform.m31,
            source[0] * transform.m12 + source[1] * transform.m22 +
                source[2] * transform.m32,
            source[0] * transform.m13 + source[1] * transform.m23 +
                source[2] * transform.m33};
        return std::ranges::all_of(
            destination,
            [](float component) noexcept {
                return std::isfinite(component);
            });
    }

    static bool transform_point3(
        const std::array<float, 3U>& source,
        const progpu_native_matrix_4x4& transform,
        std::array<float, 3U>& destination) noexcept {
        const float x = source[0] * transform.m11 +
            source[1] * transform.m21 + source[2] * transform.m31 +
            transform.m41;
        const float y = source[0] * transform.m12 +
            source[1] * transform.m22 + source[2] * transform.m32 +
            transform.m42;
        const float z = source[0] * transform.m13 +
            source[1] * transform.m23 + source[2] * transform.m33 +
            transform.m43;
        const float w = source[0] * transform.m14 +
            source[1] * transform.m24 + source[2] * transform.m34 +
            transform.m44;
        if (!std::isfinite(x) || !std::isfinite(y) ||
            !std::isfinite(z) || !std::isfinite(w) || w == 0.0F) {
            return false;
        }
        destination = {x / w, y / w, z / w};
        return std::ranges::all_of(
            destination,
            [](float component) noexcept {
                return std::isfinite(component);
            });
    }

    status resolve_light3d(
        std::uint32_t handle,
        const progpu_native_matrix_4x4& parent_transform,
        progpu_native_scene_light_3d& native) const noexcept {
        const auto source = lights3d.find(handle);
        if (source == lights3d.end()) {
            return status::invalid_handle;
        }
        const auto& light = source->second;
        progpu_native_color color{};
        status value_status = resolve_animated_color(
            light.color, light.animations[0], color);
        if (value_status != status::success) {
            return value_status;
        }
        progpu_native_matrix_4x4 local_transform{};
        value_status = resolve_transform3d(
            light.transform_handle, local_transform);
        if (value_status != status::success) {
            return value_status;
        }
        const progpu_native_matrix_4x4 effective_transform =
            multiply_matrix_4x4(local_transform, parent_transform);
        native = {};
        native.struct_size = sizeof(native);
        native.kind = light.kind;
        native.color = color;
        if (light.kind == PROGPU_NATIVE_LIGHT_3D_AMBIENT) {
            return semantic::is_valid_semantic_light_3d(native)
                ? status::success
                : status::invalid_graph;
        }
        if (light.kind == PROGPU_NATIVE_LIGHT_3D_DIRECTIONAL ||
            light.kind == PROGPU_NATIVE_LIGHT_3D_SPOT) {
            std::array<float, 3U> direction{};
            value_status = resolve_animated_vector3(
                light.direction,
                light.animations[6],
                false,
                direction);
            if (value_status != status::success ||
                !transform_vector3(
                    direction, effective_transform, direction) ||
                !normalize_vector3(direction)) {
                return value_status == status::success
                    ? status::invalid_graph
                    : value_status;
            }
            native.direction_inner_cos = {
                direction[0], direction[1], direction[2], 0.0F};
        }
        if (light.kind == PROGPU_NATIVE_LIGHT_3D_POINT ||
            light.kind == PROGPU_NATIVE_LIGHT_3D_SPOT) {
            std::array<float, 3U> position{};
            value_status = resolve_animated_vector3(
                light.position,
                light.animations[1],
                true,
                position);
            if (value_status != status::success ||
                !transform_point3(
                    position, effective_transform, position)) {
                return value_status == status::success
                    ? status::invalid_graph
                    : value_status;
            }
            std::array<double, 4U> values{};
            const std::array base_values{
                light.range,
                light.constant_attenuation,
                light.linear_attenuation,
                light.quadratic_attenuation};
            for (std::size_t index = 0U; index < values.size(); ++index) {
                value_status = resolve_animated_double(
                    base_values[index],
                    light.animations[index + 2U],
                    values[index]);
                if (value_status != status::success) {
                    return value_status;
                }
            }
            const bool infinite_range = std::isinf(values[0]) &&
                values[0] > 0.0;
            if ((!finite_double_as_float(values[0]) && !infinite_range) ||
                values[0] <= 0.0 ||
                !finite_double_as_float(values[1]) || values[1] < 0.0 ||
                !finite_double_as_float(values[2]) || values[2] < 0.0 ||
                !finite_double_as_float(values[3]) || values[3] < 0.0 ||
                (values[1] == 0.0 && values[2] == 0.0 &&
                 values[3] == 0.0)) {
                return status::invalid_graph;
            }
            native.position_range = {
                position[0],
                position[1],
                position[2],
                infinite_range
                    ? std::numeric_limits<float>::max()
                    : static_cast<float>(values[0])};
            native.attenuation_outer_cos = {
                static_cast<float>(values[1]),
                static_cast<float>(values[2]),
                static_cast<float>(values[3]),
                0.0F};
        }
        if (light.kind == PROGPU_NATIVE_LIGHT_3D_SPOT) {
            double outer = 0.0;
            double inner = 0.0;
            value_status = resolve_animated_double(
                light.outer_cone_angle,
                light.animations[7],
                outer);
            if (value_status == status::success) {
                value_status = resolve_animated_double(
                    light.inner_cone_angle,
                    light.animations[8],
                    inner);
            }
            if (value_status != status::success ||
                !finite_double_as_float(outer) ||
                !finite_double_as_float(inner) ||
                outer < 0.0 || outer > 180.0 ||
                inner < 0.0 || inner > 180.0) {
                return value_status == status::success
                    ? status::invalid_graph
                    : value_status;
            }
            inner = std::min(inner, outer);
            native.direction_inner_cos.w = static_cast<float>(std::cos(
                inner * std::numbers::pi_v<double> / 360.0));
            native.attenuation_outer_cos.w = static_cast<float>(std::cos(
                outer * std::numbers::pi_v<double> / 360.0));
        }
        return semantic::is_valid_semantic_light_3d(native)
            ? status::success
            : status::invalid_graph;
    }

    status append_material3d_handles(
        std::uint32_t handle,
        std::vector<std::uint32_t>& handles,
        std::unordered_set<std::uint32_t>& active,
        std::uint32_t depth) const {
        if (handle == 0U) {
            return status::success;
        }
        if (depth > maximum_visual_depth || !active.insert(handle).second) {
            return status::invalid_graph;
        }
        const auto resource = resources.find(handle);
        if (resource == resources.end() ||
            !is_material3d_type(resource->second.type)) {
            active.erase(handle);
            return status::invalid_handle;
        }
        if (resource->second.type != type_material_group) {
            try {
                handles.push_back(handle);
            } catch (const std::bad_alloc&) {
                active.erase(handle);
                return status::capacity_exceeded;
            }
            active.erase(handle);
            return status::success;
        }
        const auto group = material_groups3d.find(handle);
        if (group == material_groups3d.end()) {
            active.erase(handle);
            return status::invalid_handle;
        }
        status result = status::success;
        for (const std::uint32_t child : group->second.children) {
            result = append_material3d_handles(
                child, handles, active, depth + 1U);
            if (result != status::success) {
                break;
            }
        }
        active.erase(handle);
        return result;
    }

    static progpu_native_scene_brush white_scene_brush() noexcept {
        progpu_native_scene_brush brush{};
        brush.type = PROGPU_NATIVE_SCENE_BRUSH_SOLID;
        brush.opacity = 1.0F;
        brush.colors[0] = {1.0F, 1.0F, 1.0F, 1.0F};
        brush.coordinate_transform0[0] = 1.0F;
        brush.coordinate_transform1[1] = 1.0F;
        return brush;
    }

    status append_material3d_pass(
        std::uint32_t handle,
        bool back_face,
        const progpu_native_scene_mesh_3d& source_mesh,
        viewport3d_scene_state& scene) const {
        const auto material = materials3d.find(handle);
        if (material == materials3d.end()) {
            return status::invalid_handle;
        }
        if (material->second.brush_handle == 0U) {
            return status::success;
        }
        progpu_native_scene_mesh_3d mesh = source_mesh;
        mesh.flags = back_face
            ? PROGPU_NATIVE_MESH_3D_BACK_FACE
            : PROGPU_NATIVE_MESH_3D_FRONT_FACE;
        mesh.color = {0.0F, 0.0F, 0.0F, 1.0F};
        mesh.specular_color = {0.0F, 0.0F, 0.0F, 1.0F};
        mesh.material_ambient = {0.0F, 0.0F, 0.0F, 1.0F};
        mesh.opacity = 1.0F;
        mesh.shading_mode = 1U;
        progpu_native_scene_brush native_brush = white_scene_brush();
        const auto solid = solid_brushes.find(
            material->second.brush_handle);
        if (solid != solid_brushes.end()) {
            progpu_native_color brush_color{};
            double brush_opacity = 0.0;
            const status brush_status = resolve_solid_brush(
                material->second.brush_handle,
                brush_color,
                brush_opacity);
            if (brush_status != status::success) {
                return brush_status;
            }
            const progpu_native_color multiplied{
                brush_color.r * material->second.color.r,
                brush_color.g * material->second.color.g,
                brush_color.b * material->second.color.b,
                1.0F};
            mesh.opacity = static_cast<float>(brush_opacity) *
                brush_color.a * material->second.color.a;
            if (material->second.type ==
                material3d_state::kind::specular) {
                mesh.flags |= PROGPU_NATIVE_MESH_3D_SPECULAR_MATERIAL;
                mesh.specular_color = {
                    multiplied.r,
                    multiplied.g,
                    multiplied.b,
                    static_cast<float>(std::max(
                        material->second.specular_power,
                        0.001))};
            } else {
                mesh.color = multiplied;
            }
        } else {
            std::vector<progpu_native_scene_gradient_stop> stops;
            const brush_use_state use{
                0.0,
                0.0,
                1.0,
                1.0,
                {}};
            const status brush_status = resolve_gradient_scene_brush(
                material->second.brush_handle,
                use,
                native_brush,
                stops);
            if (brush_status != status::success) {
                return brush_status;
            }
            if (scene.gradient_stops.size() >
                std::numeric_limits<std::uint32_t>::max() -
                    native_brush.stop_offset) {
                return status::capacity_exceeded;
            }
            native_brush.stop_offset += static_cast<std::uint32_t>(
                scene.gradient_stops.size());
            try {
                scene.gradient_stops.insert(
                    scene.gradient_stops.end(),
                    stops.begin(),
                    stops.end());
            } catch (const std::bad_alloc&) {
                return status::capacity_exceeded;
            }
            mesh.opacity = material->second.color.a;
            if (material->second.type ==
                material3d_state::kind::specular) {
                mesh.flags |= PROGPU_NATIVE_MESH_3D_SPECULAR_MATERIAL;
                mesh.specular_color = {
                    material->second.color.r,
                    material->second.color.g,
                    material->second.color.b,
                    static_cast<float>(std::max(
                        material->second.specular_power,
                        0.001))};
            } else {
                mesh.color = {
                    material->second.color.r,
                    material->second.color.g,
                    material->second.color.b,
                    1.0F};
            }
        }
        if (material->second.type == material3d_state::kind::diffuse) {
            mesh.material_ambient = {
                material->second.ambient_color.r,
                material->second.ambient_color.g,
                material->second.ambient_color.b,
                1.0F};
        } else if (material->second.type ==
            material3d_state::kind::emissive) {
            mesh.shading_mode = 0U;
        }
        if (!semantic::is_valid_semantic_mesh_3d(
                mesh,
                scene.vertices.size(),
                scene.indices.size(),
                scene.lights.size())) {
            return status::invalid_graph;
        }
        try {
            scene.meshes.push_back(mesh);
            scene.materials.push_back(native_brush);
        } catch (const std::bad_alloc&) {
            return status::capacity_exceeded;
        }
        return status::success;
    }

    status append_geometry_model3d(
        std::uint32_t handle,
        const progpu_native_matrix_4x4& parent_transform,
        viewport3d_scene_state& scene) const {
        const auto model = geometry_models3d.find(handle);
        if (model == geometry_models3d.end()) {
            return status::invalid_handle;
        }
        if (model->second.geometry_handle == 0U ||
            (model->second.material_handle == 0U &&
             model->second.back_material_handle == 0U)) {
            return status::success;
        }
        const auto geometry = mesh_geometries3d.find(
            model->second.geometry_handle);
        if (geometry == mesh_geometries3d.end()) {
            return status::invalid_handle;
        }
        const auto& source = geometry->second;
        if (source.positions.empty()) {
            return status::success;
        }
        std::size_t used_vertex_count = source.positions.size();
        std::vector<std::uint32_t> local_indices;
        try {
            if (source.indices.empty()) {
                used_vertex_count -= used_vertex_count % 3U;
                local_indices.resize(used_vertex_count);
                for (std::size_t index = 0U;
                     index < used_vertex_count;
                     ++index) {
                    local_indices[index] = static_cast<std::uint32_t>(index);
                }
            } else {
                std::size_t valid_count = 0U;
                while (valid_count < source.indices.size() &&
                    source.indices[valid_count] < source.positions.size()) {
                    ++valid_count;
                }
                valid_count -= valid_count % 3U;
                local_indices.assign(
                    source.indices.begin(),
                    source.indices.begin() + valid_count);
            }
        } catch (const std::bad_alloc&) {
            return status::capacity_exceeded;
        }
        if (used_vertex_count < 3U || local_indices.size() < 3U ||
            used_vertex_count > std::numeric_limits<std::uint32_t>::max() ||
            local_indices.size() >
                std::numeric_limits<std::uint32_t>::max()) {
            return status::success;
        }
        std::vector<std::uint32_t> front_materials;
        std::vector<std::uint32_t> back_materials;
        std::unordered_set<std::uint32_t> active_materials;
        status result = append_material3d_handles(
            model->second.material_handle,
            front_materials,
            active_materials,
            0U);
        if (result == status::success) {
            result = append_material3d_handles(
                model->second.back_material_handle,
                back_materials,
                active_materials,
                0U);
        }
        if (result != status::success ||
            (front_materials.empty() && back_materials.empty())) {
            return result;
        }
        progpu_native_matrix_4x4 local_transform{};
        result = resolve_transform3d(
            model->second.transform_handle,
            local_transform);
        if (result != status::success) {
            return result;
        }
        const progpu_native_matrix_4x4 model_transform =
            multiply_matrix_4x4(local_transform, parent_transform);
        progpu_native_matrix_4x4 inverse_model{};
        if (!try_invert_matrix_4x4(model_transform, inverse_model)) {
            return status::invalid_graph;
        }
        const progpu_native_matrix_4x4 normal_transform =
            transpose_matrix_4x4(inverse_model);
        std::vector<std::array<float, 3U>> normals;
        try {
            normals.resize(used_vertex_count);
        } catch (const std::bad_alloc&) {
            return status::capacity_exceeded;
        }
        if (source.normals.size() < used_vertex_count) {
            // Indexed face accumulation has loop-carried scatter dependencies;
            // intrinsic SIMD is not applicable to this WPF-compatible phase.
            // Normalization below is bounded to three lanes per vertex.
            for (std::size_t index = 0U;
                 index < local_indices.size();
                 index += 3U) {
                const auto& first = source.positions[local_indices[index]];
                const auto& second =
                    source.positions[local_indices[index + 1U]];
                const auto& third =
                    source.positions[local_indices[index + 2U]];
                const std::array<float, 3U> edge0{
                    first[0] - second[0],
                    first[1] - second[1],
                    first[2] - second[2]};
                const std::array<float, 3U> edge1{
                    first[0] - third[0],
                    first[1] - third[1],
                    first[2] - third[2]};
                std::array<float, 3U> face{
                    edge0[1] * edge1[2] - edge0[2] * edge1[1],
                    edge0[2] * edge1[0] - edge0[0] * edge1[2],
                    edge0[0] * edge1[1] - edge0[1] * edge1[0]};
                normalize_vector3(face);
                for (std::size_t corner = 0U; corner < 3U; ++corner) {
                    auto& destination =
                        normals[local_indices[index + corner]];
                    destination[0] += face[0];
                    destination[1] += face[1];
                    destination[2] += face[2];
                }
            }
        }
        const std::size_t supplied_normal_count = std::min(
            source.normals.size(), used_vertex_count);
        for (std::size_t index = 0U;
             index < supplied_normal_count;
             ++index) {
            normals[index] = source.normals[index];
        }
        normalize_vector3_buffer(normals);
        const std::size_t vertex_offset = scene.vertices.size();
        const std::size_t index_offset = scene.indices.size();
        if (vertex_offset > std::numeric_limits<std::uint32_t>::max() ||
            index_offset > std::numeric_limits<std::uint32_t>::max()) {
            return status::capacity_exceeded;
        }
        try {
            scene.vertices.reserve(vertex_offset + used_vertex_count);
            for (std::size_t index = 0U;
                 index < used_vertex_count;
                 ++index) {
                const auto texture = index < source.texture_coordinates.size()
                    ? source.texture_coordinates[index]
                    : std::array<double, 2U>{};
                scene.vertices.push_back({
                    {source.positions[index][0],
                     source.positions[index][1],
                     source.positions[index][2],
                     0.0F},
                    {normals[index][0],
                     normals[index][1],
                     normals[index][2],
                     0.0F},
                    {static_cast<float>(texture[0]),
                     static_cast<float>(texture[1])},
                    0U,
                    0U});
            }
            scene.indices.insert(
                scene.indices.end(),
                local_indices.begin(),
                local_indices.end());
        } catch (const std::bad_alloc&) {
            return status::capacity_exceeded;
        }
        progpu_native_scene_mesh_3d mesh{};
        mesh.struct_size = sizeof(mesh);
        mesh.topology = PROGPU_NATIVE_MESH_3D_TRIANGLES;
        mesh.render_mode = PROGPU_NATIVE_MESH_3D_SOLID;
        mesh.vertex_offset = static_cast<std::uint32_t>(vertex_offset);
        mesh.vertex_count = static_cast<std::uint32_t>(used_vertex_count);
        mesh.index_offset = static_cast<std::uint32_t>(index_offset);
        mesh.index_count = static_cast<std::uint32_t>(local_indices.size());
        mesh.model_transform = model_transform;
        mesh.normal_transform = normal_transform;
        mesh.light_direction = {0.0F, 0.0F, -1.0F, 0.0F};
        mesh.ambient_color = {0.0F, 0.0F, 0.0F, 0.0F};
        mesh.specular_color = {0.0F, 0.0F, 0.0F, 1.0F};
        mesh.material_ambient = {0.0F, 0.0F, 0.0F, 1.0F};
        mesh.opacity = 1.0F;
        mesh.shading_mode = 1U;
        mesh.light_count = static_cast<std::uint32_t>(scene.lights.size());
        for (const std::uint32_t material_handle : front_materials) {
            result = append_material3d_pass(
                material_handle, false, mesh, scene);
            if (result != status::success) {
                return result;
            }
        }
        for (const std::uint32_t material_handle : back_materials) {
            result = append_material3d_pass(
                material_handle, true, mesh, scene);
            if (result != status::success) {
                return result;
            }
        }
        return status::success;
    }

    status build_canonical_viewport3d_scene(
        std::uint32_t root_handle,
        viewport3d_scene_state& scene) const {
        scene.meshes.clear();
        scene.vertices.clear();
        scene.indices.clear();
        scene.lights.clear();
        scene.materials.clear();
        scene.gradient_stops.clear();
        std::unordered_set<std::uint32_t> active_visuals;
        std::unordered_set<std::uint32_t> active_models;
        const auto visit_model_lights = [
            this,
            &scene,
            &active_models](auto&& self,
                            std::uint32_t handle,
                            const progpu_native_matrix_4x4& parent_transform,
                            std::uint32_t depth) -> status {
            if (depth > maximum_visual_depth ||
                !active_models.insert(handle).second) {
                return status::invalid_graph;
            }
            const auto resource = resources.find(handle);
            if (resource == resources.end() ||
                !is_model3d_type(resource->second.type)) {
                active_models.erase(handle);
                return status::invalid_handle;
            }
            status result = status::success;
            if (resource->second.type == type_model3d_group) {
                const auto group = model3d_groups.find(handle);
                if (group == model3d_groups.end()) {
                    result = status::invalid_handle;
                } else {
                    progpu_native_matrix_4x4 local_transform{};
                    result = resolve_transform3d(
                        group->second.transform_handle,
                        local_transform);
                    const auto effective_transform = multiply_matrix_4x4(
                        local_transform, parent_transform);
                    for (const std::uint32_t child : group->second.children) {
                        if (result != status::success) {
                            break;
                        }
                        result = self(
                            self,
                            child,
                            effective_transform,
                            depth + 1U);
                    }
                }
            } else if (is_light3d_type(resource->second.type)) {
                if (scene.lights.size() >=
                    PROGPU_NATIVE_SCENE_MAX_3D_LIGHTS_PER_MESH) {
                    result = status::unsupported_command;
                } else {
                    progpu_native_scene_light_3d light{};
                    result = resolve_light3d(
                        handle, parent_transform, light);
                    if (result == status::success) {
                        try {
                            scene.lights.push_back(light);
                        } catch (const std::bad_alloc&) {
                            result = status::capacity_exceeded;
                        }
                    }
                }
            }
            active_models.erase(handle);
            return result;
        };
        const auto visit_visual_lights = [
            this,
            &active_visuals,
            &visit_model_lights](auto&& self,
                                 std::uint32_t handle,
                                 const progpu_native_matrix_4x4&
                                     parent_transform,
                                 std::uint32_t depth) -> status {
            if (depth > maximum_visual_depth ||
                !active_visuals.insert(handle).second) {
                return status::invalid_graph;
            }
            const auto visual = visuals3d.find(handle);
            if (visual == visuals3d.end()) {
                active_visuals.erase(handle);
                return status::invalid_handle;
            }
            progpu_native_matrix_4x4 local_transform{};
            status result = resolve_transform3d(
                visual->second.transform_handle,
                local_transform);
            const auto effective_transform = multiply_matrix_4x4(
                local_transform, parent_transform);
            if (result == status::success &&
                visual->second.content_handle != 0U) {
                result = visit_model_lights(
                    visit_model_lights,
                    visual->second.content_handle,
                    effective_transform,
                    depth + 1U);
            }
            for (const std::uint32_t child : visual->second.children) {
                if (result != status::success) {
                    break;
                }
                result = self(
                    self,
                    child,
                    effective_transform,
                    depth + 1U);
            }
            active_visuals.erase(handle);
            return result;
        };
        status result = visit_visual_lights(
            visit_visual_lights,
            root_handle,
            identity_matrix_4x4(),
            0U);
        if (result != status::success) {
            return result;
        }
        active_visuals.clear();
        active_models.clear();
        const auto visit_model_meshes = [
            this,
            &scene,
            &active_models](auto&& self,
                            std::uint32_t handle,
                            const progpu_native_matrix_4x4& parent_transform,
                            std::uint32_t depth) -> status {
            if (depth > maximum_visual_depth ||
                !active_models.insert(handle).second) {
                return status::invalid_graph;
            }
            const auto resource = resources.find(handle);
            if (resource == resources.end() ||
                !is_model3d_type(resource->second.type)) {
                active_models.erase(handle);
                return status::invalid_handle;
            }
            status local_result = status::success;
            if (resource->second.type == type_model3d_group) {
                const auto group = model3d_groups.find(handle);
                if (group == model3d_groups.end()) {
                    local_result = status::invalid_handle;
                } else {
                    progpu_native_matrix_4x4 local_transform{};
                    local_result = resolve_transform3d(
                        group->second.transform_handle,
                        local_transform);
                    const auto effective_transform = multiply_matrix_4x4(
                        local_transform, parent_transform);
                    for (const std::uint32_t child : group->second.children) {
                        if (local_result != status::success) {
                            break;
                        }
                        local_result = self(
                            self,
                            child,
                            effective_transform,
                            depth + 1U);
                    }
                }
            } else if (resource->second.type == type_geometry_model3d) {
                local_result = append_geometry_model3d(
                    handle, parent_transform, scene);
            }
            active_models.erase(handle);
            return local_result;
        };
        const auto visit_visual_meshes = [
            this,
            &active_visuals,
            &visit_model_meshes](auto&& self,
                                 std::uint32_t handle,
                                 const progpu_native_matrix_4x4&
                                     parent_transform,
                                 std::uint32_t depth) -> status {
            if (depth > maximum_visual_depth ||
                !active_visuals.insert(handle).second) {
                return status::invalid_graph;
            }
            const auto visual = visuals3d.find(handle);
            if (visual == visuals3d.end()) {
                active_visuals.erase(handle);
                return status::invalid_handle;
            }
            progpu_native_matrix_4x4 local_transform{};
            status local_result = resolve_transform3d(
                visual->second.transform_handle,
                local_transform);
            const auto effective_transform = multiply_matrix_4x4(
                local_transform, parent_transform);
            if (local_result == status::success &&
                visual->second.content_handle != 0U) {
                local_result = visit_model_meshes(
                    visit_model_meshes,
                    visual->second.content_handle,
                    effective_transform,
                    depth + 1U);
            }
            for (const std::uint32_t child : visual->second.children) {
                if (local_result != status::success) {
                    break;
                }
                local_result = self(
                    self,
                    child,
                    effective_transform,
                    depth + 1U);
            }
            active_visuals.erase(handle);
            return local_result;
        };
        return visit_visual_meshes(
            visit_visual_meshes,
            root_handle,
            identity_matrix_4x4(),
            0U);
    }

    bool require_geometry(std::uint32_t handle) const noexcept {
        const auto found = resources.find(handle);
        return found != resources.end() &&
            (found->second.type == type_line_geometry ||
             found->second.type == type_rectangle_geometry ||
             found->second.type == type_ellipse_geometry ||
             found->second.type == type_geometry_group ||
             found->second.type == type_combined_geometry ||
             found->second.type == type_path_geometry);
    }

    bool geometry_reaches(
        std::uint32_t start,
        std::uint32_t destination) const {
        std::vector<std::uint32_t> pending{start};
        std::unordered_set<std::uint32_t> visited;
        while (!pending.empty()) {
            const auto current = pending.back();
            pending.pop_back();
            if (current == destination) {
                return true;
            }
            if (!visited.insert(current).second) {
                continue;
            }
            const auto found = geometry_groups.find(current);
            if (found != geometry_groups.end()) {
                pending.insert(
                    pending.end(),
                    found->second.children.begin(),
                    found->second.children.end());
            }
            const auto combined = combined_geometries.find(current);
            if (combined != combined_geometries.end()) {
                if (combined->second.geometry1_handle != 0U) {
                    pending.push_back(combined->second.geometry1_handle);
                }
                if (combined->second.geometry2_handle != 0U) {
                    pending.push_back(combined->second.geometry2_handle);
                }
            }
        }
        return false;
    }

    bool visual_reaches(
        std::uint32_t start,
        std::uint32_t destination) const {
        std::vector<std::uint32_t> pending{start};
        std::unordered_set<std::uint32_t> visited;
        while (!pending.empty()) {
            const auto current = pending.back();
            pending.pop_back();
            if (current == destination) {
                return true;
            }
            if (!visited.insert(current).second) {
                continue;
            }
            const auto found = visuals.find(current);
            if (found != visuals.end()) {
                pending.insert(
                    pending.end(),
                    found->second.children.begin(),
                    found->second.children.end());
            }
        }
        return false;
    }

    void increment_generation(std::uint32_t handle) noexcept {
        auto& generation = resources.at(handle).generation;
        if (generation != std::numeric_limits<std::uint64_t>::max()) {
            ++generation;
        }
    }

    template<typename Layout>
    status apply_tile_brush(
        const command_view& view,
        std::uint32_t resource_type,
        std::uint32_t source_offset,
        batch_metrics& metrics) {
        tile_brush_state brush{};
        std::uint32_t handle{};
        const auto read_rect = [&](std::uint32_t offset,
                                   rect_resource_state& rect) {
            return read_at(view.packet, offset, rect.x) &&
                read_at(view.packet, offset + 8U, rect.y) &&
                read_at(view.packet, offset + 16U, rect.width) &&
                read_at(view.packet, offset + 24U, rect.height);
        };
        if (!has_exact_size(view, Layout::fixed_size) ||
            !read_at(view.packet, Layout::handle_offset, handle) ||
            !read_at(view.packet, Layout::opacity_offset, brush.opacity) ||
            !read_rect(Layout::viewport_offset, brush.viewport) ||
            !read_rect(Layout::viewbox_offset, brush.viewbox) ||
            !read_at(view.packet, Layout::cache_invalidation_threshold_minimum_offset,
                brush.cache_threshold_minimum) ||
            !read_at(view.packet, Layout::cache_invalidation_threshold_maximum_offset,
                brush.cache_threshold_maximum) ||
            !read_at(view.packet, Layout::h_opacity_animations_offset, brush.opacity_animation) ||
            !read_at(view.packet, Layout::h_transform_offset, brush.transform_handle) ||
            !read_at(view.packet, Layout::h_relative_transform_offset, brush.relative_transform_handle) ||
            !read_at(view.packet, Layout::viewport_units_offset, brush.viewport_units) ||
            !read_at(view.packet, Layout::viewbox_units_offset, brush.viewbox_units) ||
            !read_at(view.packet, Layout::h_viewport_animations_offset, brush.viewport_animation) ||
            !read_at(view.packet, Layout::h_viewbox_animations_offset, brush.viewbox_animation) ||
            !read_at(view.packet, Layout::stretch_offset, brush.stretch) ||
            !read_at(view.packet, Layout::tile_mode_offset, brush.tile_mode) ||
            !read_at(view.packet, Layout::alignment_x_offset, brush.alignment_x) ||
            !read_at(view.packet, Layout::alignment_y_offset, brush.alignment_y) ||
            !read_at(view.packet, Layout::caching_hint_offset, brush.caching_hint) ||
            !read_at(view.packet, source_offset, brush.source_handle)) {
            return status::malformed_batch;
        }
        if (!require_resource(handle, resource_type) ||
            (brush.transform_handle != 0U && !require_transform(brush.transform_handle)) ||
            (brush.relative_transform_handle != 0U && !require_transform(brush.relative_transform_handle)) ||
            (brush.opacity_animation != 0U && !require_resource(brush.opacity_animation, type_double_resource)) ||
            (brush.viewport_animation != 0U && !require_resource(brush.viewport_animation, type_rect_resource)) ||
            (brush.viewbox_animation != 0U && !require_resource(brush.viewbox_animation, type_rect_resource))) {
            return status::invalid_handle;
        }
        if (brush.source_handle != 0U) {
            const auto source = resources.find(brush.source_handle);
            if (source == resources.end()) {
                return status::invalid_handle;
            }
            const auto type = source->second.type;
            const bool valid_source = resource_type == type_image_brush
                ? (type == type_bitmap_source || type == type_double_buffered_bitmap ||
                   type == type_d3d_image || type == type_drawing_image)
                : resource_type == type_drawing_brush
                    ? is_drawing_type(type) : is_visual_type(type);
            if (!valid_source) {
                return status::invalid_handle;
            }
        }
        const auto valid_rect = [](const rect_resource_state& rect) {
            const double infinity = std::numeric_limits<double>::infinity();
            const bool empty = rect.x == infinity && rect.y == infinity &&
                rect.width == -infinity && rect.height == -infinity;
            return empty || (std::isfinite(rect.x) && std::isfinite(rect.y) &&
                std::isfinite(rect.width) && std::isfinite(rect.height) &&
                rect.width >= 0.0 && rect.height >= 0.0);
        };
        if (!std::isfinite(brush.opacity) || brush.opacity < 0.0 || brush.opacity > 1.0 ||
            !valid_rect(brush.viewport) || !valid_rect(brush.viewbox) ||
            brush.viewport_units > 1U || brush.viewbox_units > 1U ||
            brush.stretch > 3U || brush.tile_mode > 4U ||
            brush.alignment_x > 2U || brush.alignment_y > 2U || brush.caching_hint > 1U) {
            return status::malformed_batch;
        }
        // WPF does not validate the optional cache-threshold double properties.
        // Preserve them as hints; they must never become unchecked allocations
        // or geometry extents when a tile cache is selected.
        tile_brushes.insert_or_assign(handle, brush);
        increment_generation(handle);
        ++metrics.updated_resource_count;
        return status::success;
    }

    status apply_command(
        const command_view& view,
        batch_metrics& metrics) {
        std::uint32_t handle = 0U;
        switch (view.kind) {
        case command::transport_sync_flush:
            return has_exact_size(
                view,
                command_layouts::transport_sync_flush::fixed_size)
                ? status::success
                : status::malformed_batch;
        case command::channel_create_resource: {
            using layout = command_layouts::channel_create_resource;
            std::uint32_t type = 0U;
            if (!has_exact_size(view, layout::fixed_size) ||
                !read_at(view.packet, layout::handle_offset, handle) ||
                !read_at(view.packet, layout::res_type_offset, type) ||
                handle == 0U) {
                return status::malformed_batch;
            }
            if (type == 0U || type >= type_last) {
                return status::invalid_resource_type;
            }
            if (resources.contains(handle)) {
                return status::duplicate_handle;
            }
            resource_state resource{};
            resource.type = type;
            resources.emplace(handle, std::move(resource));
            ++metrics.created_resource_count;
            return status::success;
        }
        case command::channel_delete_resource: {
            using layout = command_layouts::channel_delete_resource;
            std::uint32_t type = 0U;
            if (!has_exact_size(view, layout::fixed_size) ||
                !read_at(view.packet, layout::handle_offset, handle) ||
                !read_at(view.packet, layout::res_type_offset, type)) {
                return status::malformed_batch;
            }
            const auto found = resources.find(handle);
            if (found == resources.end()) {
                return status::invalid_handle;
            }
            if (found->second.type != type) {
                return status::resource_type_mismatch;
            }
            for (const auto& [visual_handle, visual] : visuals) {
                if (visual_handle != handle &&
                    (visual.transform_handle == handle ||
                     visual.effect_handle == handle ||
                     visual.cache_mode_handle == handle ||
                     visual.clip_geometry_handle == handle ||
                     visual.content_handle == handle ||
                     visual.alpha_mask_handle == handle ||
                     std::ranges::find(visual.children, handle) !=
                        visual.children.end())) {
                    return status::invalid_graph;
                }
            }
            for (const auto& [target_handle, target] : targets) {
                if (target_handle != handle && target.root_handle == handle) {
                    return status::invalid_graph;
                }
            }
            for (const auto& [viewport_handle, viewport] :
                 viewport3d_visuals) {
                if (viewport_handle != handle &&
                    (viewport.camera_handle == handle ||
                     viewport.child_handle == handle)) {
                    return status::invalid_graph;
                }
            }
            for (const auto& [visual_handle, visual] : visuals3d) {
                if (visual_handle != handle &&
                    (visual.content_handle == handle ||
                     visual.transform_handle == handle ||
                     std::ranges::find(visual.children, handle) !=
                         visual.children.end())) {
                    return status::invalid_graph;
                }
            }
            for (const auto& [group_handle, group] : model3d_groups) {
                if (group_handle != handle &&
                    (group.transform_handle == handle ||
                     std::ranges::find(group.children, handle) !=
                         group.children.end())) {
                    return status::invalid_graph;
                }
            }
            for (const auto& [light_handle, light] : lights3d) {
                if (light_handle != handle &&
                    (light.transform_handle == handle ||
                     std::ranges::find(light.animations, handle) !=
                         light.animations.end())) {
                    return status::invalid_graph;
                }
            }
            for (const auto& [model_handle, model] : geometry_models3d) {
                if (model_handle != handle &&
                    (model.transform_handle == handle ||
                     model.geometry_handle == handle ||
                     model.material_handle == handle ||
                     model.back_material_handle == handle)) {
                    return status::invalid_graph;
                }
            }
            for (const auto& [group_handle, group] : material_groups3d) {
                if (group_handle != handle &&
                    std::ranges::find(group.children, handle) !=
                        group.children.end()) {
                    return status::invalid_graph;
                }
            }
            for (const auto& [material_handle, material] : materials3d) {
                if (material_handle != handle &&
                    material.brush_handle == handle) {
                    return status::invalid_graph;
                }
            }
            for (const auto& [rotation_handle, rotation] : rotations3d) {
                if (rotation_handle != handle &&
                    (rotation.vector_animation_handle == handle ||
                     rotation.scalar_animation_handle == handle)) {
                    return status::invalid_graph;
                }
            }
            for (const auto& [transform_handle, transform] : transforms3d) {
                if (transform_handle == handle) {
                    continue;
                }
                if (transform.rotation_handle == handle ||
                    std::ranges::find(transform.children, handle) !=
                        transform.children.end() ||
                    std::ranges::find(transform.animations, handle) !=
                        transform.animations.end()) {
                    return status::invalid_graph;
                }
            }
            for (const auto& [camera_handle, camera] : cameras3d) {
                if (camera_handle != handle &&
                    (camera.transform_handle == handle ||
                     std::ranges::find(camera.animations, handle) !=
                         camera.animations.end())) {
                    return status::invalid_graph;
                }
            }
            for (const auto& [pen_handle, pen] : pens) {
                if (pen_handle != handle &&
                    (pen.brush_handle == handle ||
                     pen.dash_style_handle == handle ||
                     pen.thickness_animation_handle == handle)) {
                    return status::invalid_graph;
                }
            }
            for (const auto& [dash_handle, dash] : dash_styles) {
                if (dash_handle != handle &&
                    dash.offset_animation_handle == handle) {
                    return status::invalid_graph;
                }
            }
            for (const auto& [drawing_handle, drawing] : geometry_drawings) {
                if (drawing_handle != handle &&
                    (drawing.brush_handle == handle ||
                     drawing.pen_handle == handle ||
                     drawing.geometry_handle == handle)) {
                    return status::invalid_graph;
                }
            }
            for (const auto& [drawing_handle, drawing] :
                 glyph_run_drawings) {
                if (drawing_handle != handle &&
                    (drawing.glyph_run_handle == handle ||
                     drawing.foreground_brush_handle == handle)) {
                    return status::invalid_graph;
                }
            }
            for (const auto& [drawing_handle, drawing] : image_drawings) {
                if (drawing_handle != handle &&
                    (drawing.image_source_handle == handle ||
                     drawing.rect_animation_handle == handle)) {
                    return status::invalid_graph;
                }
            }
            for (const auto& [drawing_handle, drawing] : video_drawings) {
                if (drawing_handle != handle &&
                    (drawing.player_handle == handle ||
                     drawing.rect_animation_handle == handle)) {
                    return status::invalid_graph;
                }
            }
            for (const auto& [image_handle, image] : drawing_images) {
                if (image_handle != handle &&
                    image.drawing_handle == handle) {
                    return status::invalid_graph;
                }
            }
            for (const auto& [group_handle, group] : drawing_groups) {
                if (group_handle != handle &&
                    (group.clip_geometry_handle == handle ||
                     group.opacity_animation_handle == handle ||
                     group.opacity_mask_handle == handle ||
                     group.transform_handle == handle ||
                     group.guideline_set_handle == handle ||
                     std::find(
                         group.children.begin(),
                         group.children.end(),
                         handle) != group.children.end())) {
                    return status::invalid_graph;
                }
            }
            for (const auto& [brush_handle, brush] : gradient_brushes) {
                if (brush_handle != handle &&
                    (brush.opacity_animation == handle ||
                     brush.transform_handle == handle ||
                     brush.relative_transform_handle == handle ||
                     brush.first_point_animation == handle ||
                     brush.second_point_animation == handle ||
                     brush.radius_x_animation == handle ||
                     brush.radius_y_animation == handle)) {
                    return status::invalid_graph;
                }
            }
            for (const auto& [brush_handle, brush] : solid_brushes) {
                if (brush_handle != handle &&
                    (brush.transform_handle == handle ||
                     brush.relative_transform_handle == handle ||
                     brush.opacity_animation_handle == handle ||
                     brush.color_animation_handle == handle)) {
                    return status::invalid_graph;
                }
            }
            for (const auto& [effect_handle, effect] : effects) {
                if (effect_handle != handle &&
                    std::ranges::find(effect.animations, handle) !=
                        effect.animations.end()) {
                    return status::invalid_graph;
                }
            }
            for (const auto& [cache_handle, cache] : bitmap_caches) {
                if (cache_handle != handle &&
                    cache.render_at_scale_animation_handle == handle) {
                    return status::invalid_graph;
                }
            }
            for (const auto& [transform_handle, transform] : transforms) {
                if (transform_handle != handle) {
                    if (transform.type == transform_state::kind::group &&
                        std::ranges::find(transform.children, handle) !=
                            transform.children.end()) {
                        return status::invalid_graph;
                    }
                    if (std::ranges::find(transform.animations, handle) !=
                        transform.animations.end()) {
                        return status::invalid_graph;
                    }
                }
            }
            for (const auto& [geometry_handle, geometry] : fixed_geometries) {
                if (geometry_handle != handle &&
                    (geometry.transform_handle == handle ||
                     std::ranges::find(geometry.animations, handle) !=
                         geometry.animations.end())) {
                    return status::invalid_graph;
                }
            }
            for (const auto& [geometry_handle, geometry] : path_geometries) {
                if (geometry_handle != handle &&
                    geometry.transform_handle == handle) {
                    return status::invalid_graph;
                }
            }
            for (const auto& [geometry_handle, geometry] : geometry_groups) {
                if (geometry_handle != handle &&
                    (geometry.transform_handle == handle ||
                     std::ranges::find(
                         geometry.children,
                         handle) != geometry.children.end())) {
                    return status::invalid_graph;
                }
            }
            for (const auto& [geometry_handle, geometry] :
                 combined_geometries) {
                if (geometry_handle != handle &&
                    (geometry.transform_handle == handle ||
                     geometry.geometry1_handle == handle ||
                     geometry.geometry2_handle == handle)) {
                    return status::invalid_graph;
                }
            }
            for (const auto& [brush_handle, brush] : tile_brushes) {
                if (brush_handle != handle &&
                    (brush.source_handle == handle || brush.opacity_animation == handle ||
                     brush.transform_handle == handle || brush.relative_transform_handle == handle ||
                     brush.viewport_animation == handle || brush.viewbox_animation == handle)) {
                    return status::invalid_graph;
                }
            }
            visuals.erase(handle);
            viewport3d_visuals.erase(handle);
            visuals3d.erase(handle);
            model3d_groups.erase(handle);
            lights3d.erase(handle);
            geometry_models3d.erase(handle);
            mesh_geometries3d.erase(handle);
            material_groups3d.erase(handle);
            materials3d.erase(handle);
            viewport3d_scenes.erase(handle);
            targets.erase(handle);
            double_resources.erase(handle);
            color_resources.erase(handle);
            point_resources.erase(handle);
            rect_resources.erase(handle);
            size_resources.erase(handle);
            matrix_resources.erase(handle);
            point3d_resources.erase(handle);
            vector3d_resources.erase(handle);
            quaternion_resources.erase(handle);
            transforms.erase(handle);
            rotations3d.erase(handle);
            transforms3d.erase(handle);
            cameras3d.erase(handle);
            fixed_geometries.erase(handle);
            geometry_groups.erase(handle);
            combined_geometries.erase(handle);
            path_geometries.erase(handle);
            solid_brushes.erase(handle);
            gradient_brushes.erase(handle);
            tile_brushes.erase(handle);
            dash_styles.erase(handle);
            pens.erase(handle);
            geometry_drawings.erase(handle);
            glyph_run_drawings.erase(handle);
            glyph_runs.erase(handle);
            image_drawings.erase(handle);
            video_drawings.erase(handle);
            media_players.erase(handle);
            bitmap_sources.erase(handle);
            d3d_images.erase(handle);
            drawing_images.erase(handle);
            drawing_groups.erase(handle);
            guideline_sets.erase(handle);
            bitmap_caches.erase(handle);
            effects.erase(handle);
            resources.erase(found);
            ++metrics.deleted_resource_count;
            return status::success;
        }
        case command::d3d_image: {
            using layout = command_layouts::d3d_image;
            std::uint64_t interop_bitmap = 0U;
            std::uint64_t software_bitmap = 0U;
            if (!has_exact_size(view, layout::fixed_size) ||
                !read_at(view.packet, layout::handle_offset, handle) ||
                !read_at(
                    view.packet,
                    layout::p_interop_device_bitmap_offset,
                    interop_bitmap) ||
                !read_at(
                    view.packet,
                    layout::p_software_bitmap_offset,
                    software_bitmap)) {
                return status::malformed_batch;
            }
            if (!require_resource(handle, type_d3d_image)) {
                return status::invalid_handle;
            }
            // These are COM pointers in desktop WPF and cannot cross the
            // portable ABI. A Windows adapter must import them into a typed
            // texture lease before it submits this canonical packet.
            if (interop_bitmap != 0U || software_bitmap != 0U) {
                return status::invalid_argument;
            }
            d3d_images.try_emplace(handle);
            increment_generation(handle);
            ++metrics.updated_resource_count;
            return status::success;
        }
        case command::d3d_image_present: {
            using layout = command_layouts::d3d_image_present;
            std::uint64_t event_handle = 0U;
            if (!has_exact_size(view, layout::fixed_size) ||
                !read_at(view.packet, layout::handle_offset, handle) ||
                !read_at(view.packet, layout::h_event_offset, event_handle)) {
                return status::malformed_batch;
            }
            if (!require_resource(handle, type_d3d_image) ||
                !d3d_images.contains(handle)) {
                return status::invalid_handle;
            }
            // HANDLE ownership is process-local. Portable synchronization is
            // completed by acquiring/releasing the typed external image lease.
            if (event_handle != 0U) {
                return status::invalid_argument;
            }
            increment_generation(handle);
            ++metrics.updated_resource_count;
            return status::success;
        }
        case command::bitmap_source: {
            using layout = command_layouts::bitmap_source;
            std::uint64_t bitmap_pointer = 0U;
            if (!has_exact_size(view, layout::fixed_size) ||
                !read_at(view.packet, layout::handle_offset, handle) ||
                !read_at(
                    view.packet,
                    layout::p_i_bitmap_offset,
                    bitmap_pointer)) {
                return status::malformed_batch;
            }
            if (!require_resource(handle, type_bitmap_source)) {
                return status::invalid_handle;
            }
            // Desktop WPF carries an in-process IWICBitmapSource pointer.
            // Portable producers must bind copied pixels or a same-device
            // external image through the typed channel sideband instead.
            if (bitmap_pointer != 0U) {
                return status::invalid_argument;
            }
            increment_generation(handle);
            ++metrics.updated_resource_count;
            return status::success;
        }
        case command::bitmap_invalidate: {
            using layout = command_layouts::bitmap_invalidate;
            std::uint32_t use_dirty_rect = 0U;
            if (!has_exact_size(view, layout::fixed_size) ||
                !read_at(view.packet, layout::handle_offset, handle) ||
                !read_at(
                    view.packet,
                    layout::use_dirty_rect_offset,
                    use_dirty_rect)) {
                return status::malformed_batch;
            }
            if (!require_resource(handle, type_bitmap_source)) {
                return status::invalid_handle;
            }
            if (use_dirty_rect > 1U) {
                return status::malformed_batch;
            }
            if (use_dirty_rect != 0U) {
                std::array<std::int32_t, 4U> dirty_rect{};
                if (!read_at(
                        view.packet,
                        layout::dirty_rect_offset,
                        dirty_rect) ||
                    dirty_rect[0U] < 0 || dirty_rect[1U] < 0 ||
                    dirty_rect[2U] <= dirty_rect[0U] ||
                    dirty_rect[3U] <= dirty_rect[1U]) {
                    return status::malformed_batch;
                }
                const auto bitmap = bitmap_sources.find(handle);
                if (bitmap != bitmap_sources.end() &&
                    (static_cast<std::uint64_t>(dirty_rect[2U]) >
                            bitmap->second.width ||
                     static_cast<std::uint64_t>(dirty_rect[3U]) >
                            bitmap->second.height)) {
                    return status::malformed_batch;
                }
            }
            // The sideband owns the new pixels or live texture. This packet
            // only invalidates retained consumers, matching WPF's change
            // notification without copying or reading image data here.
            increment_generation(handle);
            ++metrics.updated_resource_count;
            return status::success;
        }
        case command::media_player: {
            using layout = command_layouts::media_player;
            std::uint64_t media_pointer = 0U;
            std::uint32_t notify_direct = 0U;
            if (!has_exact_size(view, layout::fixed_size) ||
                !read_at(view.packet, layout::handle_offset, handle) ||
                !read_at(
                    view.packet,
                    layout::p_media_offset,
                    media_pointer) ||
                !read_at(
                    view.packet,
                    layout::notify_uce_direct_offset,
                    notify_direct)) {
                return status::malformed_batch;
            }
            if (!require_resource(handle, type_media_player)) {
                return status::invalid_handle;
            }
            // The desktop pointer identifies process-local media state. A
            // portable producer publishes the current same-device frame
            // through set_media_player_external_image instead.
            if (media_pointer != 0U) {
                return status::invalid_argument;
            }
            if (notify_direct > 1U) {
                return status::malformed_batch;
            }
            increment_generation(handle);
            ++metrics.updated_resource_count;
            return status::success;
        }
        case command::double_buffered_bitmap: {
            using layout = command_layouts::double_buffered_bitmap;
            std::uint64_t bitmap_pointer = 0U;
            std::uint32_t use_back_buffer = 0U;
            if (!has_exact_size(view, layout::fixed_size) ||
                !read_at(view.packet, layout::handle_offset, handle) ||
                !read_at(
                    view.packet,
                    layout::sw_double_buffered_bitmap_offset,
                    bitmap_pointer) ||
                !read_at(
                    view.packet,
                    layout::use_back_buffer_offset,
                    use_back_buffer)) {
                return status::malformed_batch;
            }
            if (!require_resource(handle, type_double_buffered_bitmap)) {
                return status::invalid_handle;
            }
            if (bitmap_pointer != 0U) {
                return status::invalid_argument;
            }
            if (use_back_buffer > 1U) {
                return status::malformed_batch;
            }
            increment_generation(handle);
            ++metrics.updated_resource_count;
            return status::success;
        }
        case command::double_buffered_bitmap_copy_forward: {
            using layout =
                command_layouts::double_buffered_bitmap_copy_forward;
            std::uint64_t completion_event = 0U;
            if (!has_exact_size(view, layout::fixed_size) ||
                !read_at(view.packet, layout::handle_offset, handle) ||
                !read_at(
                    view.packet,
                    layout::copy_completed_event_offset,
                    completion_event)) {
                return status::malformed_batch;
            }
            if (!require_resource(handle, type_double_buffered_bitmap)) {
                return status::invalid_handle;
            }
            // A Windows HANDLE cannot cross the portable channel. The host
            // completes its typed producer synchronization before binding the
            // new front-buffer sideband, so the canonical event stays zero.
            if (completion_event != 0U) {
                return status::invalid_argument;
            }
            increment_generation(handle);
            ++metrics.updated_resource_count;
            return status::success;
        }
        case command::visual_create: {
            using layout = command_layouts::visual_create;
            if (!has_exact_size(view, layout::fixed_size) ||
                !read_at(view.packet, layout::handle_offset, handle)) {
                return status::malformed_batch;
            }
            const auto found = resources.find(handle);
            if (found == resources.end()) {
                return status::invalid_handle;
            }
            if (!is_visual_type(found->second.type)) {
                return status::resource_type_mismatch;
            }
            visuals.try_emplace(handle);
            increment_generation(handle);
            ++metrics.updated_resource_count;
            return status::success;
        }
        case command::visual_set_offset: {
            using layout = command_layouts::visual_set_offset;
            double x = 0.0;
            double y = 0.0;
            if (!has_exact_size(view, layout::fixed_size) ||
                !read_at(view.packet, layout::handle_offset, handle) ||
                !read_at(view.packet, layout::offset_x_offset, x) ||
                !read_at(view.packet, layout::offset_y_offset, y)) {
                return status::malformed_batch;
            }
            if (!require_visual(handle)) {
                return status::invalid_handle;
            }
            if (!std::isfinite(x) || !std::isfinite(y)) {
                return status::malformed_batch;
            }
            auto& visual = visuals.at(handle);
            visual.offset_x = x;
            visual.offset_y = y;
            increment_generation(handle);
            ++metrics.updated_resource_count;
            return status::success;
        }
        case command::visual_set_transform: {
            using layout = command_layouts::visual_set_transform;
            std::uint32_t transform = 0U;
            if (!has_exact_size(view, layout::fixed_size) ||
                !read_at(view.packet, layout::handle_offset, handle) ||
                !read_at(view.packet, layout::h_transform_offset, transform)) {
                return status::malformed_batch;
            }
            if (!require_visual(handle) ||
                (transform != 0U && !require_transform(transform))) {
                return status::invalid_handle;
            }
            visuals.at(handle).transform_handle = transform;
            increment_generation(handle);
            ++metrics.updated_resource_count;
            return status::success;
        }
        case command::visual_set_effect: {
            using layout = command_layouts::visual_set_effect;
            std::uint32_t effect = 0U;
            if (!has_exact_size(view, layout::fixed_size) ||
                !read_at(view.packet, layout::handle_offset, handle) ||
                !read_at(view.packet, layout::h_effect_offset, effect)) {
                return status::malformed_batch;
            }
            if (!require_visual(handle) ||
                (effect != 0U && !require_effect(effect))) {
                return status::invalid_handle;
            }
            visuals.at(handle).effect_handle = effect;
            increment_generation(handle);
            ++metrics.updated_resource_count;
            return status::success;
        }
        case command::visual_set_cache_mode: {
            using layout = command_layouts::visual_set_cache_mode;
            std::uint32_t cache_mode = 0U;
            if (!has_exact_size(view, layout::fixed_size) ||
                !read_at(view.packet, layout::handle_offset, handle) ||
                !read_at(
                    view.packet,
                    layout::h_cache_mode_offset,
                    cache_mode)) {
                return status::malformed_batch;
            }
            if (!require_visual(handle) ||
                (cache_mode != 0U &&
                 !require_resource(cache_mode, type_bitmap_cache))) {
                return status::invalid_handle;
            }
            visuals.at(handle).cache_mode_handle = cache_mode;
            increment_generation(handle);
            ++metrics.updated_resource_count;
            return status::success;
        }
        case command::visual_set_clip: {
            using layout = command_layouts::visual_set_clip;
            std::uint32_t clip_geometry = 0U;
            if (!has_exact_size(view, layout::fixed_size) ||
                !read_at(view.packet, layout::handle_offset, handle) ||
                !read_at(view.packet, layout::h_clip_offset, clip_geometry)) {
                return status::malformed_batch;
            }
            if (!require_visual(handle) ||
                (clip_geometry != 0U &&
                 !require_geometry(clip_geometry))) {
                return status::invalid_handle;
            }
            visuals.at(handle).clip_geometry_handle = clip_geometry;
            increment_generation(handle);
            ++metrics.updated_resource_count;
            return status::success;
        }
        case command::visual_set_alpha: {
            using layout = command_layouts::visual_set_alpha;
            double opacity = 0.0;
            if (!has_exact_size(view, layout::fixed_size) ||
                !read_at(view.packet, layout::handle_offset, handle) ||
                !read_at(view.packet, layout::alpha_offset, opacity)) {
                return status::malformed_batch;
            }
            if (!require_visual(handle)) {
                return status::invalid_handle;
            }
            if (!std::isfinite(opacity) || opacity < 0.0 || opacity > 1.0) {
                return status::malformed_batch;
            }
            visuals.at(handle).opacity = opacity;
            increment_generation(handle);
            ++metrics.updated_resource_count;
            return status::success;
        }
        case command::visual_set_render_options: {
            using layout = command_layouts::visual_set_render_options;
            std::uint32_t flags = 0U;
            std::uint32_t edge_mode = 0U;
            std::uint32_t compositing_mode = 0U;
            std::uint32_t bitmap_scaling_mode = 0U;
            std::uint32_t clear_type_hint = 0U;
            std::uint32_t text_rendering_mode = 0U;
            std::uint32_t text_hinting_mode = 0U;
            const std::size_t options = layout::render_options_offset;
            if (!has_exact_size(view, layout::fixed_size) ||
                !read_at(view.packet, layout::handle_offset, handle) ||
                !read_at(view.packet, options, flags) ||
                !read_at(view.packet, options + 4U, edge_mode) ||
                !read_at(view.packet, options + 8U, compositing_mode) ||
                !read_at(view.packet, options + 12U, bitmap_scaling_mode) ||
                !read_at(view.packet, options + 16U, clear_type_hint) ||
                !read_at(view.packet, options + 20U, text_rendering_mode) ||
                !read_at(view.packet, options + 24U, text_hinting_mode)) {
                return status::malformed_batch;
            }
            if (!require_visual(handle)) {
                return status::invalid_handle;
            }
            if ((flags & ~render_option_known_mask) != 0U ||
                edge_mode > 1U || bitmap_scaling_mode > 3U ||
                clear_type_hint > 1U || text_rendering_mode > 3U ||
                text_hinting_mode > 2U) {
                return status::malformed_batch;
            }
            if ((flags & ~render_option_supported_mask) != 0U) {
                return status::unsupported_command;
            }
            if (compositing_mode != 0U ||
                ((flags & render_option_text_rendering_mode) == 0U &&
                 text_rendering_mode != 0U) ||
                ((flags & render_option_text_hinting_mode) == 0U &&
                 text_hinting_mode != 0U)) {
                return status::malformed_batch;
            }
            auto& visual = visuals.at(handle);
            visual.render_options_flags = flags;
            visual.edge_mode = edge_mode;
            visual.bitmap_scaling_mode = bitmap_scaling_mode;
            visual.clear_type_hint = clear_type_hint;
            visual.text_rendering_mode = text_rendering_mode;
            visual.text_hinting_mode = text_hinting_mode;
            increment_generation(handle);
            ++metrics.updated_resource_count;
            return status::success;
        }
        case command::visual_set_content: {
            using layout = command_layouts::visual_set_content;
            std::uint32_t content = 0U;
            if (!has_exact_size(view, layout::fixed_size) ||
                !read_at(view.packet, layout::handle_offset, handle) ||
                !read_at(view.packet, layout::h_content_offset, content)) {
                return status::malformed_batch;
            }
            if (!require_visual(handle) ||
                (content != 0U &&
                 !require_resource(content, type_render_data))) {
                return status::invalid_handle;
            }
            visuals.at(handle).content_handle = content;
            increment_generation(handle);
            ++metrics.updated_resource_count;
            return status::success;
        }
        case command::visual_set_alpha_mask: {
            using layout = command_layouts::visual_set_alpha_mask;
            std::uint32_t alpha_mask = 0U;
            if (!has_exact_size(view, layout::fixed_size) ||
                !read_at(view.packet, layout::handle_offset, handle) ||
                !read_at(
                    view.packet,
                    layout::h_alpha_mask_offset,
                    alpha_mask)) {
                return status::malformed_batch;
            }
            if (!require_visual(handle) ||
                (alpha_mask != 0U && !require_brush(alpha_mask))) {
                return status::invalid_handle;
            }
            visuals.at(handle).alpha_mask_handle = alpha_mask;
            increment_generation(handle);
            ++metrics.updated_resource_count;
            return status::success;
        }
        case command::visual_set_guideline_collection: {
            using layout = command_layouts::visual_set_guideline_collection;
            std::uint16_t count_x = 0U;
            std::uint16_t padding_x = 0U;
            std::uint16_t count_y = 0U;
            std::uint16_t padding_y = 0U;
            if (view.packet.size() < layout::fixed_size ||
                !read_at(view.packet, layout::handle_offset, handle) ||
                !read_at(view.packet, layout::count_x_offset, count_x) ||
                !read_at(
                    view.packet,
                    layout::count_x_offset + layout::count_x_size,
                    padding_x) ||
                !read_at(view.packet, layout::count_y_offset, count_y) ||
                !read_at(
                    view.packet,
                    layout::count_y_offset + layout::count_y_size,
                    padding_y) ||
                padding_x != 0U || padding_y != 0U ||
                static_cast<std::uint64_t>(count_x + count_y) *
                        sizeof(float) !=
                    view.packet.size() - layout::fixed_size) {
                return status::malformed_batch;
            }
            if (!require_visual(handle)) {
                return status::invalid_handle;
            }
            std::vector<double> guidelines_x;
            std::vector<double> guidelines_y;
            try {
                guidelines_x.resize(count_x);
                guidelines_y.resize(count_y);
            } catch (const std::bad_alloc&) {
                return status::capacity_exceeded;
            }
            std::size_t offset = layout::fixed_size;
            for (double& coordinate : guidelines_x) {
                float value = 0.0F;
                if (!read_at(view.packet, offset, value) ||
                    !std::isfinite(value)) {
                    return status::malformed_batch;
                }
                coordinate = value;
                offset += sizeof(value);
            }
            for (double& coordinate : guidelines_y) {
                float value = 0.0F;
                if (!read_at(view.packet, offset, value) ||
                    !std::isfinite(value)) {
                    return status::malformed_batch;
                }
                coordinate = value;
                offset += sizeof(value);
            }
            std::ranges::sort(guidelines_x);
            std::ranges::sort(guidelines_y);
            auto& visual = visuals.at(handle);
            visual.guidelines_x = std::move(guidelines_x);
            visual.guidelines_y = std::move(guidelines_y);
            increment_generation(handle);
            ++metrics.updated_resource_count;
            return status::success;
        }
        case command::visual_set_scrollable_area_clip: {
            using layout = command_layouts::visual_set_scrollable_area_clip;
            double x = 0.0;
            double y = 0.0;
            double width = 0.0;
            double height = 0.0;
            std::uint32_t enabled = 0U;
            const std::size_t clip = layout::clip_offset;
            if (!has_exact_size(view, layout::fixed_size) ||
                !read_at(view.packet, layout::handle_offset, handle) ||
                !read_at(view.packet, clip, x) ||
                !read_at(view.packet, clip + 8U, y) ||
                !read_at(view.packet, clip + 16U, width) ||
                !read_at(view.packet, clip + 24U, height) ||
                !read_at(view.packet, layout::is_enabled_offset, enabled)) {
                return status::malformed_batch;
            }
            if (!require_visual(handle)) {
                return status::invalid_handle;
            }
            if (enabled > 1U ||
                (enabled != 0U &&
                 (!std::isfinite(x) || !std::isfinite(y) ||
                  !std::isfinite(width) || !std::isfinite(height) ||
                  width < 0.0 || height < 0.0))) {
                return status::malformed_batch;
            }
            auto& visual = visuals.at(handle);
            visual.scroll_clip_x = x;
            visual.scroll_clip_y = y;
            visual.scroll_clip_width = width;
            visual.scroll_clip_height = height;
            visual.has_scroll_clip = enabled != 0U;
            increment_generation(handle);
            ++metrics.updated_resource_count;
            return status::success;
        }
        case command::visual_remove_all_children: {
            using layout = command_layouts::visual_remove_all_children;
            if (!has_exact_size(view, layout::fixed_size) ||
                !read_at(view.packet, layout::handle_offset, handle)) {
                return status::malformed_batch;
            }
            if (!require_visual(handle)) {
                return status::invalid_handle;
            }
            visuals.at(handle).children.clear();
            increment_generation(handle);
            ++metrics.updated_resource_count;
            return status::success;
        }
        case command::visual_remove_child: {
            using layout = command_layouts::visual_remove_child;
            std::uint32_t child = 0U;
            if (!has_exact_size(view, layout::fixed_size) ||
                !read_at(view.packet, layout::handle_offset, handle) ||
                !read_at(view.packet, layout::h_child_offset, child)) {
                return status::malformed_batch;
            }
            if (!require_visual(handle) || !require_visual(child)) {
                return status::invalid_handle;
            }
            auto& children = visuals.at(handle).children;
            const auto found = std::ranges::find(children, child);
            if (found == children.end()) {
                return status::invalid_graph;
            }
            children.erase(found);
            increment_generation(handle);
            ++metrics.updated_resource_count;
            return status::success;
        }
        case command::visual_insert_child_at: {
            using layout = command_layouts::visual_insert_child_at;
            std::uint32_t child = 0U;
            std::uint32_t index = 0U;
            if (!has_exact_size(view, layout::fixed_size) ||
                !read_at(view.packet, layout::handle_offset, handle) ||
                !read_at(view.packet, layout::h_child_offset, child) ||
                !read_at(view.packet, layout::index_offset, index)) {
                return status::malformed_batch;
            }
            if (!require_visual(handle) || !require_visual(child) ||
                handle == child) {
                return status::invalid_handle;
            }
            auto& children = visuals.at(handle).children;
            if (index > children.size() ||
                std::ranges::find(children, child) != children.end() ||
                visual_reaches(child, handle)) {
                return status::invalid_graph;
            }
            for (const auto& [parent_handle, parent] : visuals) {
                if (parent_handle != handle &&
                    std::ranges::find(parent.children, child) !=
                        parent.children.end()) {
                    return status::invalid_graph;
                }
            }
            children.insert(children.begin() + index, child);
            increment_generation(handle);
            ++metrics.updated_resource_count;
            return status::success;
        }
        case command::viewport3d_visual_set_camera: {
            using layout = command_layouts::viewport3d_visual_set_camera;
            std::uint32_t camera = 0U;
            if (!has_exact_size(view, layout::fixed_size) ||
                !read_at(view.packet, layout::handle_offset, handle) ||
                !read_at(view.packet, layout::h_camera_offset, camera)) {
                return status::malformed_batch;
            }
            if (!require_resource(handle, type_viewport3d_visual) ||
                !visuals.contains(handle)) {
                return status::invalid_handle;
            }
            if (camera != 0U && !require_camera3d(camera)) {
                return status::invalid_handle;
            }
            auto& viewport = viewport3d_visuals[handle];
            viewport.camera_handle = camera;
            viewport.has_camera_binding = true;
            increment_generation(handle);
            ++metrics.updated_resource_count;
            return status::success;
        }
        case command::viewport3d_visual_set_viewport: {
            using layout = command_layouts::viewport3d_visual_set_viewport;
            viewport3d_visual_state viewport{};
            const std::size_t offset = layout::viewport_offset;
            if (!has_exact_size(view, layout::fixed_size) ||
                !read_at(view.packet, layout::handle_offset, handle) ||
                !read_at(view.packet, offset, viewport.x) ||
                !read_at(view.packet, offset + 8U, viewport.y) ||
                !read_at(view.packet, offset + 16U, viewport.width) ||
                !read_at(view.packet, offset + 24U, viewport.height)) {
                return status::malformed_batch;
            }
            if (!require_resource(handle, type_viewport3d_visual) ||
                !visuals.contains(handle)) {
                return status::invalid_handle;
            }
            const bool empty =
                std::isinf(viewport.x) && viewport.x > 0.0 &&
                std::isinf(viewport.y) && viewport.y > 0.0 &&
                std::isinf(viewport.width) && viewport.width < 0.0 &&
                std::isinf(viewport.height) && viewport.height < 0.0;
            if (!empty &&
                (!finite_double_as_float(viewport.x) ||
                 !finite_double_as_float(viewport.y) ||
                 !finite_double_as_float(viewport.width) ||
                 !finite_double_as_float(viewport.height) ||
                 viewport.width < 0.0 || viewport.height < 0.0)) {
                return status::malformed_batch;
            }
            const auto existing = viewport3d_visuals.find(handle);
            if (existing != viewport3d_visuals.end()) {
                viewport.camera_handle = existing->second.camera_handle;
                viewport.has_camera_binding =
                    existing->second.has_camera_binding;
                viewport.child_handle = existing->second.child_handle;
                viewport.has_child_binding =
                    existing->second.has_child_binding;
            }
            viewport.has_viewport = true;
            viewport3d_visuals.insert_or_assign(handle, viewport);
            increment_generation(handle);
            ++metrics.updated_resource_count;
            return status::success;
        }
        case command::viewport3d_visual_set_3d_child: {
            using layout = command_layouts::viewport3d_visual_set_3d_child;
            std::uint32_t child = 0U;
            if (!has_exact_size(view, layout::fixed_size) ||
                !read_at(view.packet, layout::handle_offset, handle) ||
                !read_at(view.packet, layout::h_child_offset, child)) {
                return status::malformed_batch;
            }
            if (!require_resource(handle, type_viewport3d_visual) ||
                !visuals.contains(handle) ||
                (child != 0U &&
                 !require_resource(child, type_visual3d))) {
                return status::invalid_handle;
            }
            if (child != 0U) {
                for (const auto& [parent_handle, parent] : visuals3d) {
                    if (std::ranges::find(parent.children, child) !=
                        parent.children.end()) {
                        return status::invalid_graph;
                    }
                }
                for (const auto& [viewport_handle, viewport] :
                     viewport3d_visuals) {
                    if (viewport_handle != handle &&
                        viewport.has_child_binding &&
                        viewport.child_handle == child) {
                        return status::invalid_graph;
                    }
                }
            }
            auto& viewport = viewport3d_visuals[handle];
            viewport.child_handle = child;
            viewport.has_child_binding = true;
            increment_generation(handle);
            ++metrics.updated_resource_count;
            return status::success;
        }
        case command::visual3d_set_content: {
            using layout = command_layouts::visual3d_set_content;
            std::uint32_t content = 0U;
            if (!has_exact_size(view, layout::fixed_size) ||
                !read_at(view.packet, layout::handle_offset, handle) ||
                !read_at(view.packet, layout::h_content_offset, content)) {
                return status::malformed_batch;
            }
            if (!require_resource(handle, type_visual3d) ||
                (content != 0U && !require_model3d(content))) {
                return status::invalid_handle;
            }
            visuals3d[handle].content_handle = content;
            increment_generation(handle);
            ++metrics.updated_resource_count;
            return status::success;
        }
        case command::visual3d_set_transform: {
            using layout = command_layouts::visual3d_set_transform;
            std::uint32_t transform = 0U;
            if (!has_exact_size(view, layout::fixed_size) ||
                !read_at(view.packet, layout::handle_offset, handle) ||
                !read_at(view.packet, layout::h_transform_offset, transform)) {
                return status::malformed_batch;
            }
            if (!require_resource(handle, type_visual3d) ||
                (transform != 0U && !require_transform3d(transform))) {
                return status::invalid_handle;
            }
            visuals3d[handle].transform_handle = transform;
            increment_generation(handle);
            ++metrics.updated_resource_count;
            return status::success;
        }
        case command::visual3d_remove_all_children: {
            using layout = command_layouts::visual3d_remove_all_children;
            if (!has_exact_size(view, layout::fixed_size) ||
                !read_at(view.packet, layout::handle_offset, handle)) {
                return status::malformed_batch;
            }
            if (!require_resource(handle, type_visual3d)) {
                return status::invalid_handle;
            }
            visuals3d[handle].children.clear();
            increment_generation(handle);
            ++metrics.updated_resource_count;
            return status::success;
        }
        case command::visual3d_remove_child: {
            using layout = command_layouts::visual3d_remove_child;
            std::uint32_t child = 0U;
            if (!has_exact_size(view, layout::fixed_size) ||
                !read_at(view.packet, layout::handle_offset, handle) ||
                !read_at(view.packet, layout::h_child_offset, child)) {
                return status::malformed_batch;
            }
            if (!require_resource(handle, type_visual3d) ||
                !require_resource(child, type_visual3d)) {
                return status::invalid_handle;
            }
            auto& children = visuals3d[handle].children;
            const auto found = std::ranges::find(children, child);
            if (found == children.end()) {
                return status::invalid_graph;
            }
            children.erase(found);
            increment_generation(handle);
            ++metrics.updated_resource_count;
            return status::success;
        }
        case command::visual3d_insert_child_at: {
            using layout = command_layouts::visual3d_insert_child_at;
            std::uint32_t child = 0U;
            std::uint32_t index = 0U;
            if (!has_exact_size(view, layout::fixed_size) ||
                !read_at(view.packet, layout::handle_offset, handle) ||
                !read_at(view.packet, layout::h_child_offset, child) ||
                !read_at(view.packet, layout::index_offset, index)) {
                return status::malformed_batch;
            }
            if (!require_resource(handle, type_visual3d) ||
                !require_resource(child, type_visual3d)) {
                return status::invalid_handle;
            }
            auto& children = visuals3d[handle].children;
            if (index > children.size() || child == handle ||
                std::ranges::find(children, child) != children.end() ||
                visual3d_reaches(child, handle)) {
                return status::invalid_graph;
            }
            for (const auto& [parent_handle, parent] : visuals3d) {
                if (parent_handle != handle &&
                    std::ranges::find(parent.children, child) !=
                        parent.children.end()) {
                    return status::invalid_graph;
                }
            }
            for (const auto& [viewport_handle, viewport] :
                 viewport3d_visuals) {
                if (viewport_handle != handle &&
                    viewport.has_child_binding &&
                    viewport.child_handle == child) {
                    return status::invalid_graph;
                }
            }
            children.insert(children.begin() + index, child);
            increment_generation(handle);
            ++metrics.updated_resource_count;
            return status::success;
        }
        case command::hwnd_target_create: {
            using layout = command_layouts::hwnd_target_create;
            std::uint64_t hwnd = 0U;
            std::uint64_t section = 0U;
            std::uint64_t master_device = 0U;
            std::uint32_t width = 0U;
            std::uint32_t height = 0U;
            std::array<float, 4U> clear_color{};
            std::uint32_t flags = 0U;
            std::uint32_t bitmap = 0U;
            std::uint32_t stride = 0U;
            std::uint32_t pixel_format = 0U;
            std::int32_t dpi_awareness_context = 0;
            double dpi_x = 0.0;
            double dpi_y = 0.0;
            if (!has_exact_size(view, layout::fixed_size) ||
                !read_at(view.packet, layout::handle_offset, handle) ||
                !read_at(view.packet, layout::hwnd_offset, hwnd) ||
                !read_at(view.packet, layout::h_section_offset, section) ||
                !read_at(
                    view.packet,
                    layout::master_device_offset,
                    master_device) ||
                !read_at(view.packet, layout::width_offset, width) ||
                !read_at(view.packet, layout::height_offset, height) ||
                !read_at(
                    view.packet,
                    layout::clear_color_offset,
                    clear_color) ||
                !read_at(view.packet, layout::flags_offset, flags) ||
                !read_at(view.packet, layout::h_bitmap_offset, bitmap) ||
                !read_at(view.packet, layout::stride_offset, stride) ||
                !read_at(
                    view.packet,
                    layout::e_pixel_format_offset,
                    pixel_format) ||
                !read_at(
                    view.packet,
                    layout::dpi_awareness_context_offset,
                    dpi_awareness_context) ||
                !read_at(view.packet, layout::dpi_x_offset, dpi_x) ||
                !read_at(view.packet, layout::dpi_y_offset, dpi_y)) {
                return status::malformed_batch;
            }
            if (!require_resource(handle, type_hwnd_render_target)) {
                return status::invalid_handle;
            }
            // HWND, shared-section, master-device, and bitmap handles are
            // process-local Windows state. The source-integrated producer
            // supplies a typed portable surface and keeps these wire fields
            // zero instead of leaking them into the native scene graph.
            if (hwnd != 0U || section != 0U || master_device != 0U ||
                bitmap != 0U || stride != 0U || pixel_format != 0U) {
                return status::invalid_argument;
            }
            if (!std::ranges::all_of(
                    clear_color,
                    [](float component) noexcept {
                        return std::isfinite(component);
                    }) ||
                !std::isfinite(dpi_x) || !std::isfinite(dpi_y) ||
                dpi_x <= 0.0 || dpi_y <= 0.0) {
                return status::malformed_batch;
            }
            target_state target{};
            target.clear_red = clear_color[0];
            target.clear_green = clear_color[1];
            target.clear_blue = clear_color[2];
            target.clear_alpha = clear_color[3];
            target.flags = flags;
            target.width = width;
            target.height = height;
            target.dpi_x = dpi_x;
            target.dpi_y = dpi_y;
            target.dpi_awareness_context = dpi_awareness_context;
            target.is_window_target = true;
            targets.insert_or_assign(handle, target);
            increment_generation(handle);
            ++metrics.updated_resource_count;
            return status::success;
        }
        case command::hwnd_target_suppress_layered: {
            using layout = command_layouts::hwnd_target_suppress_layered;
            std::uint32_t suppress = 0U;
            if (!has_exact_size(view, layout::fixed_size) ||
                !read_at(view.packet, layout::handle_offset, handle) ||
                !read_at(view.packet, layout::suppress_offset, suppress)) {
                return status::malformed_batch;
            }
            if (!require_resource(handle, type_hwnd_render_target) ||
                !targets.contains(handle)) {
                return status::invalid_handle;
            }
            if (suppress > 1U) {
                return status::malformed_batch;
            }
            targets.at(handle).suppress_layered = suppress != 0U;
            increment_generation(handle);
            ++metrics.updated_resource_count;
            return status::success;
        }
        case command::target_update_window_settings: {
            using layout = command_layouts::target_update_window_settings;
            std::array<std::int32_t, 4U> window_rect{};
            std::uint32_t window_layer_type = 0U;
            std::uint32_t transparency_mode = 0U;
            float constant_alpha = 0.0F;
            std::uint32_t is_child = 0U;
            std::uint32_t is_rtl = 0U;
            std::uint32_t rendering_enabled = 0U;
            progpu_native_color color_key{};
            std::uint32_t disable_cookie = 0U;
            std::uint32_t gdi_blt = 0U;
            if (!has_exact_size(view, layout::fixed_size) ||
                !read_at(view.packet, layout::handle_offset, handle) ||
                !read_at(
                    view.packet,
                    layout::window_rect_offset,
                    window_rect) ||
                !read_at(
                    view.packet,
                    layout::window_layer_type_offset,
                    window_layer_type) ||
                !read_at(
                    view.packet,
                    layout::transparency_mode_offset,
                    transparency_mode) ||
                !read_at(
                    view.packet,
                    layout::constant_alpha_offset,
                    constant_alpha) ||
                !read_at(view.packet, layout::is_child_offset, is_child) ||
                !read_at(view.packet, layout::is_rtl_offset, is_rtl) ||
                !read_at(
                    view.packet,
                    layout::rendering_enabled_offset,
                    rendering_enabled) ||
                !read_at(
                    view.packet,
                    layout::color_key_offset,
                    color_key) ||
                !read_at(
                    view.packet,
                    layout::disable_cookie_offset,
                    disable_cookie) ||
                !read_at(view.packet, layout::gdi_blt_offset, gdi_blt)) {
                return status::malformed_batch;
            }
            if (!require_resource(handle, type_hwnd_render_target) ||
                !targets.contains(handle)) {
                return status::invalid_handle;
            }
            constexpr std::uint32_t transparency_mask = 0x7U;
            if (window_layer_type > 2U ||
                (transparency_mode & ~transparency_mask) != 0U ||
                is_child > 1U || is_rtl > 1U ||
                rendering_enabled > 1U || gdi_blt > 1U ||
                !std::isfinite(constant_alpha) ||
                !std::isfinite(color_key.r) ||
                !std::isfinite(color_key.g) ||
                !std::isfinite(color_key.b) ||
                !std::isfinite(color_key.a)) {
                return status::malformed_batch;
            }
            auto& target = targets.at(handle);
            if (is_child != 0U) {
                target.rendering_enabled = true;
            } else if (rendering_enabled != 0U) {
                if (target.disable_cookie != disable_cookie) {
                    // WPF deliberately ignores a stale out-of-order enable.
                    return status::success;
                }
                target.rendering_enabled = true;
            } else {
                target.disable_cookie = disable_cookie;
                target.rendering_enabled = false;
            }
            if (is_child == 0U || window_layer_type == 2U) {
                if (window_layer_type == 0U) {
                    transparency_mode = 0U;
                } else if (window_layer_type == 1U) {
                    transparency_mode &= ~0x2U;
                }
                target.window_layer_type = window_layer_type;
                target.transparency_mode = transparency_mode;
                target.constant_alpha = constant_alpha;
                target.color_key = color_key;
                target.window_left = window_rect[0];
                target.window_top = window_rect[1];
                target.window_right = window_rect[2];
                target.window_bottom = window_rect[3];
            }
            target.is_child = is_child != 0U;
            target.is_rtl = is_rtl != 0U;
            target.gdi_blt = gdi_blt != 0U;
            increment_generation(handle);
            ++metrics.updated_resource_count;
            return status::success;
        }
        case command::hwnd_target_dpi_changed: {
            using layout = command_layouts::hwnd_target_dpi_changed;
            double dpi_x = 0.0;
            double dpi_y = 0.0;
            std::uint32_t after_parent = 0U;
            if (!has_exact_size(view, layout::fixed_size) ||
                !read_at(view.packet, layout::handle_offset, handle) ||
                !read_at(view.packet, layout::dpi_x_offset, dpi_x) ||
                !read_at(view.packet, layout::dpi_y_offset, dpi_y) ||
                !read_at(
                    view.packet,
                    layout::after_parent_offset,
                    after_parent)) {
                return status::malformed_batch;
            }
            if (!require_resource(handle, type_hwnd_render_target) ||
                !targets.contains(handle)) {
                return status::invalid_handle;
            }
            if (!std::isfinite(dpi_x) || !std::isfinite(dpi_y) ||
                dpi_x <= 0.0 || dpi_y <= 0.0 || after_parent > 1U) {
                return status::malformed_batch;
            }
            auto& target = targets.at(handle);
            target.dpi_x = dpi_x;
            target.dpi_y = dpi_y;
            target.dpi_after_parent = after_parent != 0U;
            increment_generation(handle);
            ++metrics.updated_resource_count;
            return status::success;
        }
        case command::generic_target_create: {
            using layout = command_layouts::generic_target_create;
            if (!has_exact_size(view, layout::fixed_size) ||
                !read_at(view.packet, layout::handle_offset, handle)) {
                return status::malformed_batch;
            }
            const auto found = resources.find(handle);
            if (found == resources.end()) {
                return status::invalid_handle;
            }
            if (!is_target_type(found->second.type)) {
                return status::resource_type_mismatch;
            }
            targets.try_emplace(handle);
            increment_generation(handle);
            ++metrics.updated_resource_count;
            return status::success;
        }
        case command::target_set_root: {
            using layout = command_layouts::target_set_root;
            std::uint32_t root = 0U;
            if (!has_exact_size(view, layout::fixed_size) ||
                !read_at(view.packet, layout::handle_offset, handle) ||
                !read_at(view.packet, layout::h_root_offset, root)) {
                return status::malformed_batch;
            }
            if (!require_target(handle) ||
                (root != 0U && !require_visual(root))) {
                return status::invalid_handle;
            }
            targets.at(handle).root_handle = root;
            increment_generation(handle);
            ++metrics.updated_resource_count;
            return status::success;
        }
        case command::target_set_clear_color: {
            using layout = command_layouts::target_set_clear_color;
            float red = 0.0F;
            float green = 0.0F;
            float blue = 0.0F;
            float alpha = 0.0F;
            const std::size_t color = layout::clear_color_offset;
            if (!has_exact_size(view, layout::fixed_size) ||
                !read_at(view.packet, layout::handle_offset, handle) ||
                !read_at(view.packet, color, red) ||
                !read_at(view.packet, color + 4U, green) ||
                !read_at(view.packet, color + 8U, blue) ||
                !read_at(view.packet, color + 12U, alpha)) {
                return status::malformed_batch;
            }
            if (!require_target(handle)) {
                return status::invalid_handle;
            }
            if (!std::isfinite(red) || !std::isfinite(green) ||
                !std::isfinite(blue) || !std::isfinite(alpha)) {
                return status::malformed_batch;
            }
            auto& target = targets.at(handle);
            target.clear_red = red;
            target.clear_green = green;
            target.clear_blue = blue;
            target.clear_alpha = alpha;
            increment_generation(handle);
            ++metrics.updated_resource_count;
            return status::success;
        }
        case command::target_set_flags: {
            using layout = command_layouts::target_set_flags;
            std::uint32_t flags = 0U;
            if (!has_exact_size(view, layout::fixed_size) ||
                !read_at(view.packet, layout::handle_offset, handle) ||
                !read_at(view.packet, layout::flags_offset, flags)) {
                return status::malformed_batch;
            }
            if (!require_target(handle)) {
                return status::invalid_handle;
            }
            targets.at(handle).flags = flags;
            increment_generation(handle);
            ++metrics.updated_resource_count;
            return status::success;
        }
        case command::target_invalidate: {
            using layout = command_layouts::target_invalidate;
            if (!has_exact_size(view, layout::fixed_size) ||
                !read_at(view.packet, layout::handle_offset, handle)) {
                return status::malformed_batch;
            }
            if (!require_target(handle)) {
                return status::invalid_handle;
            }
            ++metrics.updated_resource_count;
            return status::success;
        }
        case command::double_resource: {
            using layout = command_layouts::double_resource;
            double value = 0.0;
            if (!has_exact_size(view, layout::fixed_size) ||
                !read_at(view.packet, layout::handle_offset, handle) ||
                !read_at(view.packet, layout::value_offset, value)) {
                return status::malformed_batch;
            }
            if (!require_resource(handle, type_double_resource)) {
                return status::invalid_handle;
            }
            if (!std::isfinite(value)) {
                return status::malformed_batch;
            }
            double_resources.insert_or_assign(handle, value);
            increment_generation(handle);
            ++metrics.updated_resource_count;
            return status::success;
        }
        case command::color_resource: {
            using layout = command_layouts::color_resource;
            std::array<float, 4U> value{};
            if (!has_exact_size(view, layout::fixed_size) ||
                !read_at(view.packet, layout::handle_offset, handle) ||
                !read_at(view.packet, layout::value_offset, value)) {
                return status::malformed_batch;
            }
            if (!require_resource(handle, type_color_resource)) {
                return status::invalid_handle;
            }
            if (!std::ranges::all_of(value, [](float component) noexcept {
                    return std::isfinite(component);
                })) {
                return status::malformed_batch;
            }
            color_resources.insert_or_assign(handle, value);
            increment_generation(handle);
            ++metrics.updated_resource_count;
            return status::success;
        }
        case command::point_resource: {
            using layout = command_layouts::point_resource;
            point_resource_state point{};
            if (!has_exact_size(view, layout::fixed_size) ||
                !read_at(view.packet, layout::handle_offset, handle) ||
                !read_at(view.packet, layout::value_offset, point.x) ||
                !read_at(
                    view.packet,
                    layout::value_offset + sizeof(double),
                    point.y)) {
                return status::malformed_batch;
            }
            if (!require_resource(handle, type_point_resource)) {
                return status::invalid_handle;
            }
            if (!finite_double_as_float(point.x) ||
                !finite_double_as_float(point.y)) {
                return status::malformed_batch;
            }
            point.x = static_cast<float>(point.x);
            point.y = static_cast<float>(point.y);
            point_resources.insert_or_assign(handle, point);
            increment_generation(handle);
            ++metrics.updated_resource_count;
            return status::success;
        }
        case command::rect_resource: {
            using layout = command_layouts::rect_resource;
            rect_resource_state value{};
            const std::size_t offset = layout::value_offset;
            if (!has_exact_size(view, layout::fixed_size) ||
                !read_at(view.packet, layout::handle_offset, handle) ||
                !read_at(view.packet, offset, value.x) ||
                !read_at(view.packet, offset + 8U, value.y) ||
                !read_at(view.packet, offset + 16U, value.width) ||
                !read_at(view.packet, offset + 24U, value.height)) {
                return status::malformed_batch;
            }
            if (!require_resource(handle, type_rect_resource)) {
                return status::invalid_handle;
            }
            if (!finite_double_as_float(value.x) ||
                !finite_double_as_float(value.y) ||
                !finite_double_as_float(value.width) ||
                !finite_double_as_float(value.height) ||
                value.width < 0.0 || value.height < 0.0) {
                return status::malformed_batch;
            }
            rect_resources.insert_or_assign(handle, value);
            increment_generation(handle);
            ++metrics.updated_resource_count;
            return status::success;
        }
        case command::size_resource: {
            using layout = command_layouts::size_resource;
            std::array<double, 2U> value{};
            if (!has_exact_size(view, layout::fixed_size) ||
                !read_at(view.packet, layout::handle_offset, handle) ||
                !read_at(view.packet, layout::value_offset, value)) {
                return status::malformed_batch;
            }
            if (!require_resource(handle, type_size_resource)) {
                return status::invalid_handle;
            }
            if (!finite_double_as_float(value[0]) ||
                !finite_double_as_float(value[1]) ||
                value[0] < 0.0 || value[1] < 0.0) {
                return status::malformed_batch;
            }
            size_resources.insert_or_assign(handle, value);
            increment_generation(handle);
            ++metrics.updated_resource_count;
            return status::success;
        }
        case command::matrix_resource: {
            using layout = command_layouts::matrix_resource;
            affine_2d_double matrix{};
            const std::size_t value = layout::value_offset;
            if (!has_exact_size(view, layout::fixed_size) ||
                !read_at(view.packet, layout::handle_offset, handle) ||
                !read_at(view.packet, value, matrix.m11) ||
                !read_at(view.packet, value + 8U, matrix.m12) ||
                !read_at(view.packet, value + 16U, matrix.m21) ||
                !read_at(view.packet, value + 24U, matrix.m22) ||
                !read_at(view.packet, value + 32U, matrix.m31) ||
                !read_at(view.packet, value + 40U, matrix.m32)) {
                return status::malformed_batch;
            }
            if (!require_resource(handle, type_matrix_resource)) {
                return status::invalid_handle;
            }
            if (!try_quantize_wpf_affine(matrix)) {
                return status::malformed_batch;
            }
            matrix_resources.insert_or_assign(handle, matrix);
            increment_generation(handle);
            ++metrics.updated_resource_count;
            return status::success;
        }
        case command::point3d_resource:
        case command::vector3d_resource: {
            const bool is_point = view.kind == command::point3d_resource;
            const std::size_t fixed_size = is_point
                ? command_layouts::point3d_resource::fixed_size
                : command_layouts::vector3d_resource::fixed_size;
            const std::size_t handle_offset = is_point
                ? command_layouts::point3d_resource::handle_offset
                : command_layouts::vector3d_resource::handle_offset;
            const std::size_t value_offset = is_point
                ? command_layouts::point3d_resource::value_offset
                : command_layouts::vector3d_resource::value_offset;
            std::array<float, 3U> value{};
            if (!has_exact_size(view, fixed_size) ||
                !read_at(view.packet, handle_offset, handle) ||
                !read_at(view.packet, value_offset, value)) {
                return status::malformed_batch;
            }
            if (!require_resource(
                    handle,
                    is_point
                        ? type_point3d_resource
                        : type_vector3d_resource)) {
                return status::invalid_handle;
            }
            if (!std::ranges::all_of(value, [](float component) noexcept {
                    return std::isfinite(component);
                })) {
                return status::malformed_batch;
            }
            (is_point ? point3d_resources : vector3d_resources)
                .insert_or_assign(handle, value);
            increment_generation(handle);
            ++metrics.updated_resource_count;
            return status::success;
        }
        case command::quaternion_resource: {
            using layout = command_layouts::quaternion_resource;
            std::array<float, 4U> value{};
            if (!has_exact_size(view, layout::fixed_size) ||
                !read_at(view.packet, layout::handle_offset, handle) ||
                !read_at(view.packet, layout::value_offset, value)) {
                return status::malformed_batch;
            }
            if (!require_resource(handle, type_quaternion_resource)) {
                return status::invalid_handle;
            }
            if (!std::ranges::all_of(value, [](float component) noexcept {
                    return std::isfinite(component);
                })) {
                return status::malformed_batch;
            }
            quaternion_resources.insert_or_assign(handle, value);
            increment_generation(handle);
            ++metrics.updated_resource_count;
            return status::success;
        }
        case command::axis_angle_rotation3d: {
            using layout = command_layouts::axis_angle_rotation3d;
            rotation3d_state rotation{};
            rotation.type = rotation3d_state::kind::axis_angle;
            if (!has_exact_size(view, layout::fixed_size) ||
                !read_at(view.packet, layout::handle_offset, handle) ||
                !read_at(view.packet, layout::angle_offset, rotation.angle) ||
                !read_at(view.packet, layout::axis_offset, rotation.axis) ||
                !read_at(
                    view.packet,
                    layout::h_axis_animations_offset,
                    rotation.vector_animation_handle) ||
                !read_at(
                    view.packet,
                    layout::h_angle_animations_offset,
                    rotation.scalar_animation_handle)) {
                return status::malformed_batch;
            }
            if (!require_resource(handle, type_axis_angle_rotation3d)) {
                return status::invalid_handle;
            }
            if ((rotation.vector_animation_handle != 0U &&
                 !require_resource(
                     rotation.vector_animation_handle,
                     type_vector3d_resource)) ||
                (rotation.scalar_animation_handle != 0U &&
                 !require_resource(
                     rotation.scalar_animation_handle,
                     type_double_resource))) {
                return status::invalid_handle;
            }
            if ((rotation.vector_animation_handle == 0U &&
                 !std::ranges::all_of(
                     rotation.axis,
                     [](float value) noexcept {
                         return std::isfinite(value);
                     })) ||
                (rotation.scalar_animation_handle == 0U &&
                 !finite_double_as_float(rotation.angle))) {
                return status::malformed_batch;
            }
            rotations3d.insert_or_assign(handle, rotation);
            increment_generation(handle);
            ++metrics.updated_resource_count;
            return status::success;
        }
        case command::quaternion_rotation3d: {
            using layout = command_layouts::quaternion_rotation3d;
            rotation3d_state rotation{};
            rotation.type = rotation3d_state::kind::quaternion;
            if (!has_exact_size(view, layout::fixed_size) ||
                !read_at(view.packet, layout::handle_offset, handle) ||
                !read_at(
                    view.packet,
                    layout::quaternion_offset,
                    rotation.quaternion) ||
                !read_at(
                    view.packet,
                    layout::h_quaternion_animations_offset,
                    rotation.vector_animation_handle)) {
                return status::malformed_batch;
            }
            if (!require_resource(handle, type_quaternion_rotation3d)) {
                return status::invalid_handle;
            }
            if (rotation.vector_animation_handle != 0U &&
                !require_resource(
                    rotation.vector_animation_handle,
                    type_quaternion_resource)) {
                return status::invalid_handle;
            }
            if (rotation.vector_animation_handle == 0U &&
                !std::ranges::all_of(
                    rotation.quaternion,
                    [](float value) noexcept {
                        return std::isfinite(value);
                    })) {
                return status::malformed_batch;
            }
            rotations3d.insert_or_assign(handle, rotation);
            increment_generation(handle);
            ++metrics.updated_resource_count;
            return status::success;
        }
        case command::perspective_camera:
        case command::orthographic_camera: {
            using perspective_layout = command_layouts::perspective_camera;
            using orthographic_layout = command_layouts::orthographic_camera;
            const bool perspective = view.kind == command::perspective_camera;
            camera3d_state camera{};
            camera.type = perspective
                ? camera3d_state::kind::perspective
                : camera3d_state::kind::orthographic;
            const std::size_t projection_offset = perspective
                ? perspective_layout::field_of_view_offset
                : orthographic_layout::width_offset;
            const std::size_t projection_animation_offset = perspective
                ? perspective_layout::h_field_of_view_animations_offset
                : orthographic_layout::h_width_animations_offset;
            if (!has_exact_size(
                    view,
                    perspective
                        ? perspective_layout::fixed_size
                        : orthographic_layout::fixed_size) ||
                !read_at(view.packet, 4U, handle) ||
                !read_at(view.packet, 8U, camera.near_plane) ||
                !read_at(view.packet, 16U, camera.far_plane) ||
                !read_at(
                    view.packet,
                    projection_offset,
                    camera.projection_value) ||
                !read_at(view.packet, 32U, camera.position) ||
                !read_at(view.packet, 44U, camera.transform_handle) ||
                !read_at(view.packet, 48U, camera.look_direction) ||
                !read_at(view.packet, 60U, camera.animations[0]) ||
                !read_at(view.packet, 64U, camera.up_direction) ||
                !read_at(view.packet, 76U, camera.animations[1]) ||
                !read_at(view.packet, 80U, camera.animations[2]) ||
                !read_at(view.packet, 84U, camera.animations[3]) ||
                !read_at(view.packet, 88U, camera.animations[4]) ||
                !read_at(
                    view.packet,
                    projection_animation_offset,
                    camera.animations[5])) {
                return status::malformed_batch;
            }
            const std::uint32_t expected_type = perspective
                ? type_perspective_camera
                : type_orthographic_camera;
            if (!require_resource(handle, expected_type)) {
                return status::invalid_handle;
            }
            if (camera.transform_handle != 0U &&
                !require_transform3d(camera.transform_handle)) {
                return status::invalid_handle;
            }
            if ((camera.animations[0] != 0U &&
                 !require_resource(
                     camera.animations[0], type_double_resource)) ||
                (camera.animations[1] != 0U &&
                 !require_resource(
                     camera.animations[1], type_double_resource)) ||
                (camera.animations[2] != 0U &&
                 !require_resource(
                     camera.animations[2], type_point3d_resource)) ||
                (camera.animations[3] != 0U &&
                 !require_resource(
                     camera.animations[3], type_vector3d_resource)) ||
                (camera.animations[4] != 0U &&
                 !require_resource(
                     camera.animations[4], type_vector3d_resource)) ||
                (camera.animations[5] != 0U &&
                 !require_resource(
                     camera.animations[5], type_double_resource))) {
                return status::invalid_handle;
            }
            const auto finite_static_vector = [](const auto& value) {
                return std::ranges::all_of(
                    value,
                    [](float component) noexcept {
                        return std::isfinite(component);
                    });
            };
            const bool static_far_valid =
                finite_double_as_float(camera.far_plane) ||
                (std::isinf(camera.far_plane) && camera.far_plane > 0.0);
            if ((camera.animations[0] == 0U &&
                 !finite_double_as_float(camera.near_plane)) ||
                (camera.animations[1] == 0U && !static_far_valid) ||
                (camera.animations[2] == 0U &&
                 !finite_static_vector(camera.position)) ||
                (camera.animations[3] == 0U &&
                 !finite_static_vector(camera.look_direction)) ||
                (camera.animations[4] == 0U &&
                 !finite_static_vector(camera.up_direction)) ||
                (camera.animations[5] == 0U &&
                 !finite_double_as_float(camera.projection_value))) {
                return status::malformed_batch;
            }
            cameras3d.insert_or_assign(handle, camera);
            increment_generation(handle);
            ++metrics.updated_resource_count;
            return status::success;
        }
        case command::matrix_camera: {
            using layout = command_layouts::matrix_camera;
            camera3d_state camera{};
            camera.type = camera3d_state::kind::matrix;
            if (!has_exact_size(view, layout::fixed_size) ||
                !read_at(view.packet, layout::handle_offset, handle) ||
                !read_at(
                    view.packet,
                    layout::view_matrix_offset,
                    camera.view) ||
                !read_at(
                    view.packet,
                    layout::projection_matrix_offset,
                    camera.projection) ||
                !read_at(
                    view.packet,
                    layout::htransform_offset,
                    camera.transform_handle)) {
                return status::malformed_batch;
            }
            if (!require_resource(handle, type_matrix_camera)) {
                return status::invalid_handle;
            }
            if (camera.transform_handle != 0U &&
                !require_transform3d(camera.transform_handle)) {
                return status::invalid_handle;
            }
            if (!finite_matrix_4x4(camera.view) ||
                !finite_matrix_4x4(camera.projection)) {
                return status::malformed_batch;
            }
            cameras3d.insert_or_assign(handle, camera);
            increment_generation(handle);
            ++metrics.updated_resource_count;
            return status::success;
        }
        case command::model3d_group: {
            using layout = command_layouts::model3d_group;
            model3d_group_state group{};
            std::uint32_t children_size = 0U;
            if (view.packet.size() < layout::fixed_size ||
                !read_at(view.packet, layout::handle_offset, handle) ||
                !read_at(
                    view.packet,
                    layout::htransform_offset,
                    group.transform_handle) ||
                !read_at(
                    view.packet,
                    layout::children_size_offset,
                    children_size) ||
                children_size % sizeof(std::uint32_t) != 0U ||
                view.packet.size() != layout::fixed_size + children_size ||
                children_size / sizeof(std::uint32_t) >
                    maximum_path_record_count) {
                return status::malformed_batch;
            }
            if (!require_resource(handle, type_model3d_group) ||
                (group.transform_handle != 0U &&
                 !require_transform3d(group.transform_handle))) {
                return status::invalid_handle;
            }
            const std::size_t child_count =
                children_size / sizeof(std::uint32_t);
            group.children.reserve(child_count);
            for (std::size_t index = 0U; index < child_count; ++index) {
                std::uint32_t child = 0U;
                if (!read_at(
                        view.packet,
                        layout::fixed_size +
                            index * sizeof(std::uint32_t),
                        child)) {
                    return status::malformed_batch;
                }
                if (child == 0U || !require_model3d(child)) {
                    return status::invalid_handle;
                }
                if (child == handle || model3d_reaches(child, handle)) {
                    return status::invalid_graph;
                }
                group.children.push_back(child);
            }
            model3d_groups.insert_or_assign(handle, std::move(group));
            increment_generation(handle);
            ++metrics.updated_resource_count;
            return status::success;
        }
        case command::ambient_light:
        case command::directional_light:
        case command::point_light:
        case command::spot_light: {
            const bool ambient = view.kind == command::ambient_light;
            const bool directional = view.kind == command::directional_light;
            const bool point = view.kind == command::point_light;
            const std::uint32_t expected_type = ambient
                ? type_ambient_light
                : directional
                    ? type_directional_light
                    : point
                        ? type_point_light
                        : type_spot_light;
            const std::size_t fixed_size = ambient
                ? command_layouts::ambient_light::fixed_size
                : directional
                    ? command_layouts::directional_light::fixed_size
                    : point
                        ? command_layouts::point_light::fixed_size
                        : command_layouts::spot_light::fixed_size;
            light3d_state light{};
            light.kind = ambient
                ? PROGPU_NATIVE_LIGHT_3D_AMBIENT
                : directional
                    ? PROGPU_NATIVE_LIGHT_3D_DIRECTIONAL
                    : point
                        ? PROGPU_NATIVE_LIGHT_3D_POINT
                        : PROGPU_NATIVE_LIGHT_3D_SPOT;
            if (!has_exact_size(view, fixed_size) ||
                !read_at(view.packet, 4U, handle) ||
                !read_at(view.packet, 8U, light.color)) {
                return status::malformed_batch;
            }
            if (ambient) {
                if (!read_at(view.packet, 24U, light.transform_handle) ||
                    !read_at(view.packet, 28U, light.animations[0])) {
                    return status::malformed_batch;
                }
            } else if (directional) {
                if (!read_at(view.packet, 24U, light.direction) ||
                    !read_at(view.packet, 36U, light.transform_handle) ||
                    !read_at(view.packet, 40U, light.animations[0]) ||
                    !read_at(view.packet, 44U, light.animations[6])) {
                    return status::malformed_batch;
                }
            } else if (point) {
                if (!read_at(view.packet, 24U, light.range) ||
                    !read_at(
                        view.packet,
                        32U,
                        light.constant_attenuation) ||
                    !read_at(
                        view.packet,
                        40U,
                        light.linear_attenuation) ||
                    !read_at(
                        view.packet,
                        48U,
                        light.quadratic_attenuation) ||
                    !read_at(view.packet, 56U, light.position) ||
                    !read_at(view.packet, 68U, light.transform_handle) ||
                    !read_at(view.packet, 72U, light.animations[0]) ||
                    !read_at(view.packet, 76U, light.animations[1]) ||
                    !read_at(view.packet, 80U, light.animations[2]) ||
                    !read_at(view.packet, 84U, light.animations[3]) ||
                    !read_at(view.packet, 88U, light.animations[4]) ||
                    !read_at(view.packet, 92U, light.animations[5])) {
                    return status::malformed_batch;
                }
            } else {
                if (!read_at(view.packet, 24U, light.range) ||
                    !read_at(
                        view.packet,
                        32U,
                        light.constant_attenuation) ||
                    !read_at(
                        view.packet,
                        40U,
                        light.linear_attenuation) ||
                    !read_at(
                        view.packet,
                        48U,
                        light.quadratic_attenuation) ||
                    !read_at(view.packet, 56U, light.outer_cone_angle) ||
                    !read_at(view.packet, 64U, light.inner_cone_angle) ||
                    !read_at(view.packet, 72U, light.position) ||
                    !read_at(view.packet, 84U, light.transform_handle) ||
                    !read_at(view.packet, 88U, light.direction) ||
                    !read_at(view.packet, 100U, light.animations[0]) ||
                    !read_at(view.packet, 104U, light.animations[1]) ||
                    !read_at(view.packet, 108U, light.animations[2]) ||
                    !read_at(view.packet, 112U, light.animations[3]) ||
                    !read_at(view.packet, 116U, light.animations[4]) ||
                    !read_at(view.packet, 120U, light.animations[5]) ||
                    !read_at(view.packet, 124U, light.animations[6]) ||
                    !read_at(view.packet, 128U, light.animations[7]) ||
                    !read_at(view.packet, 132U, light.animations[8])) {
                    return status::malformed_batch;
                }
            }
            if (!require_resource(handle, expected_type) ||
                (light.transform_handle != 0U &&
                 !require_transform3d(light.transform_handle))) {
                return status::invalid_handle;
            }
            for (std::size_t index = 0U;
                 index < light.animations.size();
                 ++index) {
                const std::uint32_t animation = light.animations[index];
                if (animation == 0U) {
                    continue;
                }
                const std::uint32_t animation_type = index == 0U
                    ? type_color_resource
                    : index == 1U
                        ? type_point3d_resource
                        : index == 6U
                            ? type_vector3d_resource
                            : type_double_resource;
                if (!require_resource(animation, animation_type)) {
                    return status::invalid_handle;
                }
            }
            const auto finite_vector = [](const auto& value) noexcept {
                return std::ranges::all_of(
                    value,
                    [](float component) noexcept {
                        return std::isfinite(component);
                    });
            };
            const bool finite_color =
                std::isfinite(light.color.r) &&
                std::isfinite(light.color.g) &&
                std::isfinite(light.color.b) &&
                std::isfinite(light.color.a);
            if ((light.animations[0] == 0U && !finite_color) ||
                (!ambient && !directional &&
                 light.animations[1] == 0U &&
                 !finite_vector(light.position)) ||
                (!ambient && !point &&
                 light.animations[6] == 0U &&
                 !finite_vector(light.direction))) {
                return status::malformed_batch;
            }
            if (!ambient && !directional) {
                const std::array values{
                    light.range,
                    light.constant_attenuation,
                    light.linear_attenuation,
                    light.quadratic_attenuation};
                for (std::size_t index = 0U; index < values.size(); ++index) {
                    const std::uint32_t animation =
                        light.animations[index + 2U];
                    const bool positive_infinity = index == 0U &&
                        std::isinf(values[index]) && values[index] > 0.0;
                    if (animation == 0U &&
                        !finite_double_as_float(values[index]) &&
                        !positive_infinity) {
                        return status::malformed_batch;
                    }
                }
            }
            if (!ambient && !directional && !point &&
                ((light.animations[7] == 0U &&
                  !finite_double_as_float(light.outer_cone_angle)) ||
                 (light.animations[8] == 0U &&
                  !finite_double_as_float(light.inner_cone_angle)))) {
                return status::malformed_batch;
            }
            lights3d.insert_or_assign(handle, light);
            increment_generation(handle);
            ++metrics.updated_resource_count;
            return status::success;
        }
        case command::geometry_model3d: {
            using layout = command_layouts::geometry_model3d;
            geometry_model3d_state model{};
            if (!has_exact_size(view, layout::fixed_size) ||
                !read_at(view.packet, layout::handle_offset, handle) ||
                !read_at(
                    view.packet,
                    layout::htransform_offset,
                    model.transform_handle) ||
                !read_at(
                    view.packet,
                    layout::hgeometry_offset,
                    model.geometry_handle) ||
                !read_at(
                    view.packet,
                    layout::hmaterial_offset,
                    model.material_handle) ||
                !read_at(
                    view.packet,
                    layout::hback_material_offset,
                    model.back_material_handle)) {
                return status::malformed_batch;
            }
            if (!require_resource(handle, type_geometry_model3d) ||
                (model.transform_handle != 0U &&
                 !require_transform3d(model.transform_handle)) ||
                (model.geometry_handle != 0U &&
                 !require_resource(
                     model.geometry_handle,
                     type_mesh_geometry3d)) ||
                (model.material_handle != 0U &&
                 !require_material3d(model.material_handle)) ||
                (model.back_material_handle != 0U &&
                 !require_material3d(model.back_material_handle))) {
                return status::invalid_handle;
            }
            geometry_models3d.insert_or_assign(handle, model);
            increment_generation(handle);
            ++metrics.updated_resource_count;
            return status::success;
        }
        case command::mesh_geometry3d: {
            using layout = command_layouts::mesh_geometry3d;
            std::uint32_t positions_size = 0U;
            std::uint32_t normals_size = 0U;
            std::uint32_t texture_coordinates_size = 0U;
            std::uint32_t triangle_indices_size = 0U;
            if (view.packet.size() < layout::fixed_size ||
                !read_at(view.packet, layout::handle_offset, handle) ||
                !read_at(
                    view.packet,
                    layout::positions_size_offset,
                    positions_size) ||
                !read_at(
                    view.packet,
                    layout::normals_size_offset,
                    normals_size) ||
                !read_at(
                    view.packet,
                    layout::texture_coordinates_size_offset,
                    texture_coordinates_size) ||
                !read_at(
                    view.packet,
                    layout::triangle_indices_size_offset,
                    triangle_indices_size) ||
                positions_size % (sizeof(float) * 3U) != 0U ||
                normals_size % (sizeof(float) * 3U) != 0U ||
                texture_coordinates_size % (sizeof(double) * 2U) != 0U ||
                triangle_indices_size % sizeof(std::uint32_t) != 0U) {
                return status::malformed_batch;
            }
            const std::size_t payload_size =
                static_cast<std::size_t>(positions_size) + normals_size +
                texture_coordinates_size + triangle_indices_size;
            if (payload_size > view.packet.size() - layout::fixed_size ||
                view.packet.size() != layout::fixed_size + payload_size) {
                return status::malformed_batch;
            }
            if (!require_resource(handle, type_mesh_geometry3d)) {
                return status::invalid_handle;
            }
            mesh_geometry3d_state mesh{};
            const std::size_t position_count =
                positions_size / (sizeof(float) * 3U);
            const std::size_t normal_count =
                normals_size / (sizeof(float) * 3U);
            const std::size_t texture_coordinate_count =
                texture_coordinates_size / (sizeof(double) * 2U);
            const std::size_t index_count =
                triangle_indices_size / sizeof(std::uint32_t);
            if (position_count > maximum_path_record_count ||
                normal_count > maximum_path_record_count ||
                texture_coordinate_count > maximum_path_record_count ||
                index_count > maximum_path_record_count) {
                return status::capacity_exceeded;
            }
            try {
                mesh.positions.resize(position_count);
                mesh.normals.resize(normal_count);
                mesh.texture_coordinates.resize(texture_coordinate_count);
                mesh.indices.resize(index_count);
            } catch (const std::bad_alloc&) {
                return status::capacity_exceeded;
            }
            std::size_t offset = layout::fixed_size;
            for (auto& position : mesh.positions) {
                if (!read_at(view.packet, offset, position) ||
                    !std::ranges::all_of(
                        position,
                        [](float value) noexcept {
                            return std::isfinite(value);
                        })) {
                    return status::malformed_batch;
                }
                offset += sizeof(position);
            }
            for (auto& normal : mesh.normals) {
                if (!read_at(view.packet, offset, normal) ||
                    !std::ranges::all_of(
                        normal,
                        [](float value) noexcept {
                            return std::isfinite(value);
                        })) {
                    return status::malformed_batch;
                }
                offset += sizeof(normal);
            }
            for (auto& coordinate : mesh.texture_coordinates) {
                if (!read_at(view.packet, offset, coordinate) ||
                    !std::ranges::all_of(
                        coordinate,
                        [](double value) noexcept {
                            return finite_double_as_float(value);
                        })) {
                    return status::malformed_batch;
                }
                offset += sizeof(coordinate);
            }
            for (auto& index : mesh.indices) {
                if (!read_at(view.packet, offset, index)) {
                    return status::malformed_batch;
                }
                offset += sizeof(index);
            }
            mesh_geometries3d.insert_or_assign(handle, std::move(mesh));
            increment_generation(handle);
            ++metrics.updated_resource_count;
            return status::success;
        }
        case command::material_group: {
            using layout = command_layouts::material_group;
            std::uint32_t children_size = 0U;
            if (view.packet.size() < layout::fixed_size ||
                !read_at(view.packet, layout::handle_offset, handle) ||
                !read_at(
                    view.packet,
                    layout::children_size_offset,
                    children_size) ||
                children_size % sizeof(std::uint32_t) != 0U ||
                view.packet.size() != layout::fixed_size + children_size ||
                children_size / sizeof(std::uint32_t) >
                    maximum_path_record_count) {
                return status::malformed_batch;
            }
            if (!require_resource(handle, type_material_group)) {
                return status::invalid_handle;
            }
            material_group_state group{};
            const std::size_t child_count =
                children_size / sizeof(std::uint32_t);
            group.children.reserve(child_count);
            for (std::size_t index = 0U; index < child_count; ++index) {
                std::uint32_t child = 0U;
                if (!read_at(
                        view.packet,
                        layout::fixed_size +
                            index * sizeof(std::uint32_t),
                        child)) {
                    return status::malformed_batch;
                }
                if (child == 0U || !require_material3d(child)) {
                    return status::invalid_handle;
                }
                if (child == handle || material3d_reaches(child, handle)) {
                    return status::invalid_graph;
                }
                group.children.push_back(child);
            }
            material_groups3d.insert_or_assign(handle, std::move(group));
            increment_generation(handle);
            ++metrics.updated_resource_count;
            return status::success;
        }
        case command::diffuse_material:
        case command::specular_material:
        case command::emissive_material: {
            const bool diffuse = view.kind == command::diffuse_material;
            const bool specular = view.kind == command::specular_material;
            const std::size_t fixed_size = diffuse
                ? command_layouts::diffuse_material::fixed_size
                : specular
                    ? command_layouts::specular_material::fixed_size
                    : command_layouts::emissive_material::fixed_size;
            const std::uint32_t expected_type = diffuse
                ? type_diffuse_material
                : specular
                    ? type_specular_material
                    : type_emissive_material;
            material3d_state material{};
            material.type = diffuse
                ? material3d_state::kind::diffuse
                : specular
                    ? material3d_state::kind::specular
                    : material3d_state::kind::emissive;
            const std::size_t brush_offset = diffuse
                ? command_layouts::diffuse_material::hbrush_offset
                : specular
                    ? command_layouts::specular_material::hbrush_offset
                    : command_layouts::emissive_material::hbrush_offset;
            if (!has_exact_size(view, fixed_size) ||
                !read_at(view.packet, 4U, handle) ||
                !read_at(view.packet, 8U, material.color) ||
                !read_at(
                    view.packet,
                    brush_offset,
                    material.brush_handle) ||
                (diffuse &&
                 !read_at(view.packet, 24U, material.ambient_color)) ||
                (specular &&
                 !read_at(view.packet, 24U, material.specular_power))) {
                return status::malformed_batch;
            }
            if (!require_resource(handle, expected_type) ||
                (material.brush_handle != 0U &&
                 !require_brush(material.brush_handle))) {
                return status::invalid_handle;
            }
            if (!std::isfinite(material.color.r) ||
                !std::isfinite(material.color.g) ||
                !std::isfinite(material.color.b) ||
                !std::isfinite(material.color.a) ||
                (diffuse &&
                 (!std::isfinite(material.ambient_color.r) ||
                  !std::isfinite(material.ambient_color.g) ||
                  !std::isfinite(material.ambient_color.b) ||
                  !std::isfinite(material.ambient_color.a))) ||
                (specular &&
                 (!finite_double_as_float(material.specular_power) ||
                  material.specular_power < 0.0))) {
                return status::malformed_batch;
            }
            materials3d.insert_or_assign(handle, material);
            increment_generation(handle);
            ++metrics.updated_resource_count;
            return status::success;
        }
        case command::transform3d_group: {
            using layout = command_layouts::transform3d_group;
            std::uint32_t children_size = 0U;
            if (view.packet.size() < layout::fixed_size ||
                !read_at(view.packet, layout::handle_offset, handle) ||
                !read_at(
                    view.packet,
                    layout::children_size_offset,
                    children_size) ||
                children_size % sizeof(std::uint32_t) != 0U ||
                view.packet.size() != layout::fixed_size + children_size ||
                children_size / sizeof(std::uint32_t) >
                    maximum_path_record_count) {
                return status::malformed_batch;
            }
            if (!require_resource(handle, type_transform3d_group)) {
                return status::invalid_handle;
            }
            transform3d_state transform{};
            transform.type = transform3d_state::kind::group;
            transform.children.reserve(
                children_size / sizeof(std::uint32_t));
            for (std::size_t index = 0U;
                 index < children_size / sizeof(std::uint32_t);
                 ++index) {
                std::uint32_t child = 0U;
                if (!read_at(
                        view.packet,
                        layout::fixed_size +
                            index * sizeof(std::uint32_t),
                        child)) {
                    return status::malformed_batch;
                }
                if (child == 0U || !require_transform3d(child)) {
                    return status::invalid_handle;
                }
                if (child == handle || transform3d_reaches(child, handle)) {
                    return status::invalid_graph;
                }
                transform.children.push_back(child);
            }
            transforms3d.insert_or_assign(handle, std::move(transform));
            increment_generation(handle);
            ++metrics.updated_resource_count;
            return status::success;
        }
        case command::translate_transform3d: {
            using layout = command_layouts::translate_transform3d;
            transform3d_state transform{};
            transform.type = transform3d_state::kind::translate;
            if (!has_exact_size(view, layout::fixed_size) ||
                !read_at(view.packet, layout::handle_offset, handle) ||
                !read_at(view.packet, layout::offset_x_offset, transform.values[0]) ||
                !read_at(view.packet, layout::offset_y_offset, transform.values[1]) ||
                !read_at(view.packet, layout::offset_z_offset, transform.values[2]) ||
                !read_at(view.packet, layout::h_offset_x_animations_offset, transform.animations[0]) ||
                !read_at(view.packet, layout::h_offset_y_animations_offset, transform.animations[1]) ||
                !read_at(view.packet, layout::h_offset_z_animations_offset, transform.animations[2])) {
                return status::malformed_batch;
            }
            if (!require_resource(handle, type_translate_transform3d)) {
                return status::invalid_handle;
            }
            for (std::size_t index = 0U; index < 3U; ++index) {
                if ((transform.animations[index] != 0U &&
                     !require_resource(
                         transform.animations[index], type_double_resource))) {
                    return status::invalid_handle;
                }
                if (transform.animations[index] == 0U &&
                    !finite_double_as_float(transform.values[index])) {
                    return status::malformed_batch;
                }
            }
            transforms3d.insert_or_assign(handle, transform);
            increment_generation(handle);
            ++metrics.updated_resource_count;
            return status::success;
        }
        case command::scale_transform3d: {
            using layout = command_layouts::scale_transform3d;
            transform3d_state transform{};
            transform.type = transform3d_state::kind::scale;
            const std::array<std::size_t, 6U> value_offsets{
                layout::scale_x_offset,
                layout::scale_y_offset,
                layout::scale_z_offset,
                layout::center_x_offset,
                layout::center_y_offset,
                layout::center_z_offset};
            const std::array<std::size_t, 6U> animation_offsets{
                layout::h_scale_x_animations_offset,
                layout::h_scale_y_animations_offset,
                layout::h_scale_z_animations_offset,
                layout::h_center_x_animations_offset,
                layout::h_center_y_animations_offset,
                layout::h_center_z_animations_offset};
            if (!has_exact_size(view, layout::fixed_size) ||
                !read_at(view.packet, layout::handle_offset, handle)) {
                return status::malformed_batch;
            }
            for (std::size_t index = 0U; index < 6U; ++index) {
                if (!read_at(
                        view.packet,
                        value_offsets[index],
                        transform.values[index]) ||
                    !read_at(
                        view.packet,
                        animation_offsets[index],
                        transform.animations[index])) {
                    return status::malformed_batch;
                }
            }
            if (!require_resource(handle, type_scale_transform3d)) {
                return status::invalid_handle;
            }
            for (std::size_t index = 0U; index < 6U; ++index) {
                if (transform.animations[index] != 0U &&
                    !require_resource(
                        transform.animations[index], type_double_resource)) {
                    return status::invalid_handle;
                }
                if (transform.animations[index] == 0U &&
                    !finite_double_as_float(transform.values[index])) {
                    return status::malformed_batch;
                }
            }
            transforms3d.insert_or_assign(handle, transform);
            increment_generation(handle);
            ++metrics.updated_resource_count;
            return status::success;
        }
        case command::rotate_transform3d: {
            using layout = command_layouts::rotate_transform3d;
            transform3d_state transform{};
            transform.type = transform3d_state::kind::rotate;
            if (!has_exact_size(view, layout::fixed_size) ||
                !read_at(view.packet, layout::handle_offset, handle) ||
                !read_at(view.packet, layout::center_x_offset, transform.values[0]) ||
                !read_at(view.packet, layout::center_y_offset, transform.values[1]) ||
                !read_at(view.packet, layout::center_z_offset, transform.values[2]) ||
                !read_at(view.packet, layout::h_center_x_animations_offset, transform.animations[0]) ||
                !read_at(view.packet, layout::h_center_y_animations_offset, transform.animations[1]) ||
                !read_at(view.packet, layout::h_center_z_animations_offset, transform.animations[2]) ||
                !read_at(view.packet, layout::hrotation_offset, transform.rotation_handle)) {
                return status::malformed_batch;
            }
            if (!require_resource(handle, type_rotate_transform3d)) {
                return status::invalid_handle;
            }
            if (transform.rotation_handle != 0U &&
                !require_rotation3d(transform.rotation_handle)) {
                return status::invalid_handle;
            }
            for (std::size_t index = 0U; index < 3U; ++index) {
                if (transform.animations[index] != 0U &&
                    !require_resource(
                        transform.animations[index], type_double_resource)) {
                    return status::invalid_handle;
                }
                if (transform.animations[index] == 0U &&
                    !finite_double_as_float(transform.values[index])) {
                    return status::malformed_batch;
                }
            }
            transforms3d.insert_or_assign(handle, transform);
            increment_generation(handle);
            ++metrics.updated_resource_count;
            return status::success;
        }
        case command::matrix_transform3d: {
            using layout = command_layouts::matrix_transform3d;
            transform3d_state transform{};
            transform.type = transform3d_state::kind::matrix;
            if (!has_exact_size(view, layout::fixed_size) ||
                !read_at(view.packet, layout::handle_offset, handle) ||
                !read_at(view.packet, layout::matrix_offset, transform.matrix)) {
                return status::malformed_batch;
            }
            if (!require_resource(handle, type_matrix_transform3d)) {
                return status::invalid_handle;
            }
            if (!finite_matrix_4x4(transform.matrix)) {
                return status::malformed_batch;
            }
            transforms3d.insert_or_assign(handle, transform);
            increment_generation(handle);
            ++metrics.updated_resource_count;
            return status::success;
        }
        case command::render_data: {
            using layout = command_layouts::render_data;
            std::uint32_t data_size = 0U;
            if (view.packet.size() < layout::fixed_size ||
                !read_at(view.packet, layout::handle_offset, handle) ||
                !read_at(view.packet, layout::cb_data_offset, data_size) ||
                data_size > view.packet.size() - layout::fixed_size ||
                view.packet.size() - layout::fixed_size - data_size > 3U) {
                return status::malformed_batch;
            }
            if (!require_resource(handle, type_render_data)) {
                return status::invalid_handle;
            }
            auto& resource = resources.at(handle);
            resource.render_data.assign(
                view.packet.begin() + layout::fixed_size,
                view.packet.begin() + layout::fixed_size + data_size);
            resource.has_compact_dynamic_guidelines =
                render_data_contains_compact_guidelines(
                    resource.render_data);
            resource.compact_guidelines.clear();
            increment_generation(handle);
            ++metrics.updated_resource_count;
            return status::success;
        }
        case command::transform_group: {
            using layout = command_layouts::transform_group;
            std::uint32_t children_size = 0U;
            if (view.packet.size() < layout::fixed_size ||
                !read_at(view.packet, layout::handle_offset, handle) ||
                !read_at(
                    view.packet,
                    layout::children_size_offset,
                    children_size) ||
                children_size % sizeof(std::uint32_t) != 0U ||
                view.packet.size() != layout::fixed_size + children_size ||
                children_size / sizeof(std::uint32_t) >
                    maximum_path_record_count) {
                return status::malformed_batch;
            }
            if (!require_resource(handle, type_transform_group)) {
                return status::invalid_handle;
            }
            transform_state transform{};
            transform.type = transform_state::kind::group;
            transform.children.reserve(
                children_size / sizeof(std::uint32_t));
            for (std::size_t index = 0U;
                 index < children_size / sizeof(std::uint32_t);
                 ++index) {
                std::uint32_t child = 0U;
                if (!read_at(
                        view.packet,
                        layout::fixed_size +
                            index * sizeof(std::uint32_t),
                        child)) {
                    return status::malformed_batch;
                }
                if (child == 0U || !require_transform(child)) {
                    return status::invalid_handle;
                }
                if (child == handle || transform_reaches(child, handle)) {
                    return status::invalid_graph;
                }
                transform.children.push_back(child);
            }
            transforms.insert_or_assign(handle, std::move(transform));
            increment_generation(handle);
            ++metrics.updated_resource_count;
            return status::success;
        }
        case command::translate_transform: {
            using layout = command_layouts::translate_transform;
            double x = 0.0;
            double y = 0.0;
            std::uint32_t x_animations = 0U;
            std::uint32_t y_animations = 0U;
            if (!has_exact_size(view, layout::fixed_size) ||
                !read_at(view.packet, layout::handle_offset, handle) ||
                !read_at(view.packet, layout::x_offset, x) ||
                !read_at(view.packet, layout::y_offset, y) ||
                !read_at(
                    view.packet,
                    layout::h_x_animations_offset,
                    x_animations) ||
                !read_at(
                    view.packet,
                    layout::h_y_animations_offset,
                    y_animations)) {
                return status::malformed_batch;
            }
            if (!require_resource(handle, type_translate_transform)) {
                return status::invalid_handle;
            }
            if ((x_animations != 0U &&
                 !require_resource(x_animations, type_double_resource)) ||
                (y_animations != 0U &&
                 !require_resource(y_animations, type_double_resource))) {
                return status::invalid_handle;
            }
            transform_state transform{};
            transform.type = transform_state::kind::translate;
            transform.values[0] = x;
            transform.values[1] = y;
            transform.animations[0] = x_animations;
            transform.animations[1] = y_animations;
            if (!finite_double_as_float(x) || !finite_double_as_float(y)) {
                return status::malformed_batch;
            }
            transforms.insert_or_assign(handle, std::move(transform));
            increment_generation(handle);
            ++metrics.updated_resource_count;
            return status::success;
        }
        case command::scale_transform:
        case command::skew_transform: {
            using scale_layout = command_layouts::scale_transform;
            using skew_layout = command_layouts::skew_transform;
            const bool is_scale = view.kind == command::scale_transform;
            const std::size_t fixed_size = is_scale
                ? scale_layout::fixed_size
                : skew_layout::fixed_size;
            const std::size_t handle_offset = is_scale
                ? scale_layout::handle_offset
                : skew_layout::handle_offset;
            const std::size_t first_offset = is_scale
                ? scale_layout::scale_x_offset
                : skew_layout::angle_x_offset;
            const std::size_t second_offset = is_scale
                ? scale_layout::scale_y_offset
                : skew_layout::angle_y_offset;
            const std::size_t center_x_offset = is_scale
                ? scale_layout::center_x_offset
                : skew_layout::center_x_offset;
            const std::size_t center_y_offset = is_scale
                ? scale_layout::center_y_offset
                : skew_layout::center_y_offset;
            const std::size_t first_animation_offset = is_scale
                ? scale_layout::h_scale_x_animations_offset
                : skew_layout::h_angle_x_animations_offset;
            const std::size_t second_animation_offset = is_scale
                ? scale_layout::h_scale_y_animations_offset
                : skew_layout::h_angle_y_animations_offset;
            const std::size_t center_x_animation_offset = is_scale
                ? scale_layout::h_center_x_animations_offset
                : skew_layout::h_center_x_animations_offset;
            const std::size_t center_y_animation_offset = is_scale
                ? scale_layout::h_center_y_animations_offset
                : skew_layout::h_center_y_animations_offset;
            double first = 0.0;
            double second = 0.0;
            double center_x = 0.0;
            double center_y = 0.0;
            std::array<std::uint32_t, 4U> animations{};
            if (!has_exact_size(view, fixed_size) ||
                !read_at(view.packet, handle_offset, handle) ||
                !read_at(view.packet, first_offset, first) ||
                !read_at(view.packet, second_offset, second) ||
                !read_at(view.packet, center_x_offset, center_x) ||
                !read_at(view.packet, center_y_offset, center_y) ||
                !read_at(
                    view.packet,
                    first_animation_offset,
                    animations[0]) ||
                !read_at(
                    view.packet,
                    second_animation_offset,
                    animations[1]) ||
                !read_at(
                    view.packet,
                    center_x_animation_offset,
                    animations[2]) ||
                !read_at(
                    view.packet,
                    center_y_animation_offset,
                    animations[3])) {
                return status::malformed_batch;
            }
            const std::uint32_t expected_type =
                is_scale
                ? type_scale_transform
                : type_skew_transform;
            if (!require_resource(handle, expected_type)) {
                return status::invalid_handle;
            }
            for (const std::uint32_t animation : animations) {
                if (animation != 0U &&
                    !require_resource(animation, type_double_resource)) {
                    return status::invalid_handle;
                }
            }
            transform_state transform{};
            transform.type = is_scale
                ? transform_state::kind::scale
                : transform_state::kind::skew;
            transform.values = {first, second, center_x, center_y};
            transform.animations = animations;
            if (!finite_double_as_float(first) ||
                !finite_double_as_float(second) ||
                !finite_double_as_float(center_x) ||
                !finite_double_as_float(center_y)) {
                return status::malformed_batch;
            }
            transforms.insert_or_assign(handle, std::move(transform));
            increment_generation(handle);
            ++metrics.updated_resource_count;
            return status::success;
        }
        case command::rotate_transform: {
            using layout = command_layouts::rotate_transform;
            double angle = 0.0;
            double center_x = 0.0;
            double center_y = 0.0;
            std::array<std::uint32_t, 3U> animations{};
            if (!has_exact_size(view, layout::fixed_size) ||
                !read_at(view.packet, layout::handle_offset, handle) ||
                !read_at(view.packet, layout::angle_offset, angle) ||
                !read_at(view.packet, layout::center_x_offset, center_x) ||
                !read_at(view.packet, layout::center_y_offset, center_y) ||
                !read_at(
                    view.packet,
                    layout::h_angle_animations_offset,
                    animations[0]) ||
                !read_at(
                    view.packet,
                    layout::h_center_x_animations_offset,
                    animations[1]) ||
                !read_at(
                    view.packet,
                    layout::h_center_y_animations_offset,
                    animations[2])) {
                return status::malformed_batch;
            }
            if (!require_resource(handle, type_rotate_transform)) {
                return status::invalid_handle;
            }
            for (const std::uint32_t animation : animations) {
                if (animation != 0U &&
                    !require_resource(animation, type_double_resource)) {
                    return status::invalid_handle;
                }
            }
            transform_state transform{};
            transform.type = transform_state::kind::rotate;
            transform.values = {angle, center_x, center_y, 0.0};
            transform.animations[0] = animations[0];
            transform.animations[1] = animations[1];
            transform.animations[2] = animations[2];
            if (!finite_double_as_float(angle) ||
                !finite_double_as_float(center_x) ||
                !finite_double_as_float(center_y)) {
                return status::malformed_batch;
            }
            transforms.insert_or_assign(handle, std::move(transform));
            increment_generation(handle);
            ++metrics.updated_resource_count;
            return status::success;
        }
        case command::matrix_transform: {
            using layout = command_layouts::matrix_transform;
            affine_2d_double matrix{};
            std::uint32_t animations = 0U;
            const std::size_t matrix_offset = layout::matrix_offset;
            if (!has_exact_size(view, layout::fixed_size) ||
                !read_at(view.packet, layout::handle_offset, handle) ||
                !read_at(view.packet, matrix_offset, matrix.m11) ||
                !read_at(view.packet, matrix_offset + 8U, matrix.m12) ||
                !read_at(view.packet, matrix_offset + 16U, matrix.m21) ||
                !read_at(view.packet, matrix_offset + 24U, matrix.m22) ||
                !read_at(view.packet, matrix_offset + 32U, matrix.m31) ||
                !read_at(view.packet, matrix_offset + 40U, matrix.m32) ||
                !read_at(
                    view.packet,
                    layout::h_matrix_animations_offset,
                    animations)) {
                return status::malformed_batch;
            }
            if (!require_resource(handle, type_matrix_transform)) {
                return status::invalid_handle;
            }
            if (animations != 0U &&
                !require_resource(animations, type_matrix_resource)) {
                return status::invalid_handle;
            }
            progpu_native_affine_2d native_matrix{};
            if (!try_to_native_affine(matrix, native_matrix)) {
                return status::malformed_batch;
            }
            matrix = {
                native_matrix.m11,
                native_matrix.m12,
                native_matrix.m21,
                native_matrix.m22,
                native_matrix.m31,
                native_matrix.m32};
            transform_state transform{};
            transform.type = transform_state::kind::matrix;
            transform.matrix = matrix;
            transform.animations[0] = animations;
            transforms.insert_or_assign(handle, std::move(transform));
            increment_generation(handle);
            ++metrics.updated_resource_count;
            return status::success;
        }
        case command::line_geometry: {
            using layout = command_layouts::line_geometry;
            fixed_geometry_state geometry{};
            geometry.kind = fixed_geometry_kind::line;
            std::uint32_t start_animations = 0U;
            std::uint32_t end_animations = 0U;
            if (!has_exact_size(view, layout::fixed_size) ||
                !read_at(view.packet, layout::handle_offset, handle) ||
                !read_at(
                    view.packet,
                    layout::start_point_offset,
                    geometry.first) ||
                !read_at(
                    view.packet,
                    layout::start_point_offset + 8U,
                    geometry.second) ||
                !read_at(
                    view.packet,
                    layout::end_point_offset,
                    geometry.third) ||
                !read_at(
                    view.packet,
                    layout::end_point_offset + 8U,
                    geometry.fourth) ||
                !read_at(
                    view.packet,
                    layout::h_transform_offset,
                    geometry.transform_handle) ||
                !read_at(
                    view.packet,
                    layout::h_start_point_animations_offset,
                    start_animations) ||
                !read_at(
                    view.packet,
                    layout::h_end_point_animations_offset,
                    end_animations)) {
                return status::malformed_batch;
            }
            if (!require_resource(handle, type_line_geometry) ||
                (geometry.transform_handle != 0U &&
                 !require_transform(geometry.transform_handle))) {
                return status::invalid_handle;
            }
            if ((start_animations != 0U &&
                 !require_resource(start_animations, type_point_resource)) ||
                (end_animations != 0U &&
                 !require_resource(end_animations, type_point_resource))) {
                return status::invalid_handle;
            }
            if (!finite_double_as_float(geometry.first) ||
                !finite_double_as_float(geometry.second) ||
                !finite_double_as_float(geometry.third) ||
                !finite_double_as_float(geometry.fourth)) {
                return status::malformed_batch;
            }
            geometry.animations[0] = start_animations;
            geometry.animations[1] = end_animations;
            fixed_geometries.insert_or_assign(handle, geometry);
            increment_generation(handle);
            ++metrics.updated_resource_count;
            return status::success;
        }
        case command::rectangle_geometry: {
            using layout = command_layouts::rectangle_geometry;
            fixed_geometry_state geometry{};
            geometry.kind = fixed_geometry_kind::rectangle;
            std::uint32_t radius_x_animations = 0U;
            std::uint32_t radius_y_animations = 0U;
            std::uint32_t rect_animations = 0U;
            const std::size_t rect = layout::rect_offset;
            if (!has_exact_size(view, layout::fixed_size) ||
                !read_at(view.packet, layout::handle_offset, handle) ||
                !read_at(
                    view.packet,
                    layout::radius_x_offset,
                    geometry.radius_x) ||
                !read_at(
                    view.packet,
                    layout::radius_y_offset,
                    geometry.radius_y) ||
                !read_at(view.packet, rect, geometry.first) ||
                !read_at(view.packet, rect + 8U, geometry.second) ||
                !read_at(view.packet, rect + 16U, geometry.third) ||
                !read_at(view.packet, rect + 24U, geometry.fourth) ||
                !read_at(
                    view.packet,
                    layout::h_transform_offset,
                    geometry.transform_handle) ||
                !read_at(
                    view.packet,
                    layout::h_radius_x_animations_offset,
                    radius_x_animations) ||
                !read_at(
                    view.packet,
                    layout::h_radius_y_animations_offset,
                    radius_y_animations) ||
                !read_at(
                    view.packet,
                    layout::h_rect_animations_offset,
                    rect_animations)) {
                return status::malformed_batch;
            }
            if (!require_resource(handle, type_rectangle_geometry) ||
                (geometry.transform_handle != 0U &&
                 !require_transform(geometry.transform_handle))) {
                return status::invalid_handle;
            }
            if ((radius_x_animations != 0U &&
                 !require_resource(
                     radius_x_animations, type_double_resource)) ||
                (radius_y_animations != 0U &&
                 !require_resource(
                     radius_y_animations, type_double_resource)) ||
                (rect_animations != 0U &&
                 !require_resource(rect_animations, type_rect_resource))) {
                return status::invalid_handle;
            }
            if (!finite_double_as_float(geometry.radius_x) ||
                !finite_double_as_float(geometry.radius_y) ||
                !finite_double_as_float(geometry.first) ||
                !finite_double_as_float(geometry.second) ||
                !finite_double_as_float(geometry.third) ||
                !finite_double_as_float(geometry.fourth) ||
                geometry.radius_x < 0.0 || geometry.radius_y < 0.0 ||
                geometry.third < 0.0 || geometry.fourth < 0.0) {
                return status::malformed_batch;
            }
            geometry.animations = {
                radius_x_animations,
                radius_y_animations,
                rect_animations};
            fixed_geometries.insert_or_assign(handle, geometry);
            increment_generation(handle);
            ++metrics.updated_resource_count;
            return status::success;
        }
        case command::ellipse_geometry: {
            using layout = command_layouts::ellipse_geometry;
            fixed_geometry_state geometry{};
            geometry.kind = fixed_geometry_kind::ellipse;
            std::uint32_t radius_x_animations = 0U;
            std::uint32_t radius_y_animations = 0U;
            std::uint32_t center_animations = 0U;
            if (!has_exact_size(view, layout::fixed_size) ||
                !read_at(view.packet, layout::handle_offset, handle) ||
                !read_at(
                    view.packet,
                    layout::radius_x_offset,
                    geometry.third) ||
                !read_at(
                    view.packet,
                    layout::radius_y_offset,
                    geometry.fourth) ||
                !read_at(
                    view.packet,
                    layout::center_offset,
                    geometry.first) ||
                !read_at(
                    view.packet,
                    layout::center_offset + 8U,
                    geometry.second) ||
                !read_at(
                    view.packet,
                    layout::h_transform_offset,
                    geometry.transform_handle) ||
                !read_at(
                    view.packet,
                    layout::h_radius_x_animations_offset,
                    radius_x_animations) ||
                !read_at(
                    view.packet,
                    layout::h_radius_y_animations_offset,
                    radius_y_animations) ||
                !read_at(
                    view.packet,
                    layout::h_center_animations_offset,
                    center_animations)) {
                return status::malformed_batch;
            }
            if (!require_resource(handle, type_ellipse_geometry) ||
                (geometry.transform_handle != 0U &&
                 !require_transform(geometry.transform_handle))) {
                return status::invalid_handle;
            }
            if ((radius_x_animations != 0U &&
                 !require_resource(
                     radius_x_animations, type_double_resource)) ||
                (radius_y_animations != 0U &&
                 !require_resource(
                     radius_y_animations, type_double_resource)) ||
                (center_animations != 0U &&
                 !require_resource(center_animations, type_point_resource))) {
                return status::invalid_handle;
            }
            if (!finite_double_as_float(geometry.first) ||
                !finite_double_as_float(geometry.second) ||
                !finite_double_as_float(geometry.third) ||
                !finite_double_as_float(geometry.fourth) ||
                geometry.third < 0.0 || geometry.fourth < 0.0) {
                return status::malformed_batch;
            }
            geometry.animations = {
                radius_x_animations,
                radius_y_animations,
                center_animations};
            fixed_geometries.insert_or_assign(handle, geometry);
            increment_generation(handle);
            ++metrics.updated_resource_count;
            return status::success;
        }
        case command::geometry_group: {
            using layout = command_layouts::geometry_group;
            geometry_group_state geometry{};
            std::uint32_t children_size = 0U;
            if (view.packet.size() < layout::fixed_size ||
                !read_at(view.packet, layout::handle_offset, handle) ||
                !read_at(
                    view.packet,
                    layout::h_transform_offset,
                    geometry.transform_handle) ||
                !read_at(
                    view.packet,
                    layout::fill_rule_offset,
                    geometry.fill_rule) ||
                !read_at(
                    view.packet,
                    layout::children_size_offset,
                    children_size) ||
                children_size % sizeof(std::uint32_t) != 0U ||
                view.packet.size() != layout::fixed_size + children_size) {
                return status::malformed_batch;
            }
            if (!require_resource(handle, type_geometry_group) ||
                (geometry.transform_handle != 0U &&
                 !require_transform(geometry.transform_handle))) {
                return status::invalid_handle;
            }
            if (geometry.fill_rule > 1U ||
                children_size / sizeof(std::uint32_t) >
                    maximum_path_record_count) {
                return status::malformed_batch;
            }
            const std::size_t child_count =
                children_size / sizeof(std::uint32_t);
            geometry.children.reserve(child_count);
            for (std::size_t index = 0U; index < child_count; ++index) {
                std::uint32_t child = 0U;
                if (!read_at(
                        view.packet,
                        layout::fixed_size +
                            index * sizeof(std::uint32_t),
                        child)) {
                    return status::malformed_batch;
                }
                if (child == 0U || !require_geometry(child)) {
                    return status::invalid_handle;
                }
                if (child == handle || geometry_reaches(child, handle)) {
                    return status::invalid_graph;
                }
                geometry.children.push_back(child);
            }
            geometry_groups.insert_or_assign(handle, std::move(geometry));
            increment_generation(handle);
            ++metrics.updated_resource_count;
            return status::success;
        }
        case command::combined_geometry: {
            using layout = command_layouts::combined_geometry;
            combined_geometry_state geometry{};
            if (!has_exact_size(view, layout::fixed_size) ||
                !read_at(view.packet, layout::handle_offset, handle) ||
                !read_at(
                    view.packet,
                    layout::h_transform_offset,
                    geometry.transform_handle) ||
                !read_at(
                    view.packet,
                    layout::geometry_combine_mode_offset,
                    geometry.combine_mode) ||
                !read_at(
                    view.packet,
                    layout::h_geometry1_offset,
                    geometry.geometry1_handle) ||
                !read_at(
                    view.packet,
                    layout::h_geometry2_offset,
                    geometry.geometry2_handle)) {
                return status::malformed_batch;
            }
            if (!require_resource(handle, type_combined_geometry) ||
                (geometry.transform_handle != 0U &&
                 !require_transform(geometry.transform_handle))) {
                return status::invalid_handle;
            }
            if (geometry.combine_mode > 3U) {
                return status::malformed_batch;
            }
            const std::array operands{
                geometry.geometry1_handle,
                geometry.geometry2_handle};
            for (const std::uint32_t operand : operands) {
                if (operand == 0U) {
                    continue;
                }
                if (!require_geometry(operand)) {
                    return status::invalid_handle;
                }
                if (operand == handle || geometry_reaches(operand, handle)) {
                    return status::invalid_graph;
                }
            }
            combined_geometries.insert_or_assign(handle, geometry);
            increment_generation(handle);
            ++metrics.updated_resource_count;
            return status::success;
        }
        case command::path_geometry: {
            using layout = command_layouts::path_geometry;
            path_geometry_state geometry{};
            std::uint32_t figures_size = 0U;
            if (view.packet.size() < layout::fixed_size ||
                !read_at(view.packet, layout::handle_offset, handle) ||
                !read_at(
                    view.packet,
                    layout::h_transform_offset,
                    geometry.transform_handle) ||
                !read_at(
                    view.packet,
                    layout::fill_rule_offset,
                    geometry.fill_rule) ||
                !read_at(
                    view.packet,
                    layout::figures_size_offset,
                    figures_size) ||
                figures_size > view.packet.size() - layout::fixed_size ||
                view.packet.size() != layout::fixed_size + figures_size) {
                return status::malformed_batch;
            }
            if (!require_resource(handle, type_path_geometry) ||
                (geometry.transform_handle != 0U &&
                 !require_transform(geometry.transform_handle))) {
                return status::invalid_handle;
            }
            if (geometry.fill_rule > 1U || figures_size < 48U) {
                return status::malformed_batch;
            }
            const auto figures = view.packet.subspan(
                layout::fixed_size,
                figures_size);
            std::uint32_t declared_size = 0U;
            std::uint32_t path_flags = 0U;
            std::uint32_t figure_count = 0U;
            std::uint32_t force_packing = 0U;
            if (!read_at(figures, 0U, declared_size) ||
                !read_at(figures, 4U, path_flags) ||
                !read_at(figures, 8U, geometry.left) ||
                !read_at(figures, 16U, geometry.top) ||
                !read_at(figures, 24U, geometry.right) ||
                !read_at(figures, 32U, geometry.bottom) ||
                !read_at(figures, 40U, figure_count) ||
                !read_at(figures, 44U, force_packing) ||
                declared_size != figures_size || force_packing != 0U ||
                (path_flags & ~0x1FU) != 0U ||
                figure_count > maximum_path_record_count) {
                return status::malformed_batch;
            }
            const bool packet_bounds_valid = (path_flags & 0x02U) != 0U;
            if (packet_bounds_valid &&
                (!finite_double_as_float(geometry.left) ||
                 !finite_double_as_float(geometry.top) ||
                 !finite_double_as_float(geometry.right) ||
                 !finite_double_as_float(geometry.bottom) ||
                 geometry.right < geometry.left ||
                 geometry.bottom < geometry.top)) {
                return status::malformed_batch;
            }
            bool has_computed_bounds = false;
            double computed_left = 0.0;
            double computed_top = 0.0;
            double computed_right = 0.0;
            double computed_bottom = 0.0;
            const auto include_bounds_point = [
                &has_computed_bounds,
                &computed_left,
                &computed_top,
                &computed_right,
                &computed_bottom](progpu_native_point point) noexcept {
                if (!has_computed_bounds) {
                    computed_left = point.x;
                    computed_top = point.y;
                    computed_right = point.x;
                    computed_bottom = point.y;
                    has_computed_bounds = true;
                    return;
                }
                computed_left = std::min(computed_left, double{point.x});
                computed_top = std::min(computed_top, double{point.y});
                computed_right = std::max(computed_right, double{point.x});
                computed_bottom = std::max(computed_bottom, double{point.y});
            };
            const auto read_point = [&figures](
                std::size_t offset,
                progpu_native_point& point) noexcept {
                double x = 0.0;
                double y = 0.0;
                if (!read_at(figures, offset, x) ||
                    !read_at(figures, offset + 8U, y) ||
                    !finite_double_as_float(x) ||
                    !finite_double_as_float(y)) {
                    return false;
                }
                point = {
                    static_cast<float>(x),
                    static_cast<float>(y)};
                return true;
            };
            struct parsed_stroke_edge {
                progpu_native_path_segment segment{};
                bool stroked{};
                bool smooth_join{};
            };
            bool has_per_point_arc = false;
            std::size_t offset = 48U;
            std::uint32_t previous_figure_size = 0U;
            for (std::uint32_t figure_index = 0U;
                figure_index < figure_count;
                ++figure_index) {
                const std::size_t figure_offset = offset;
                std::uint32_t back_size = 0U;
                std::uint32_t figure_flags = 0U;
                std::uint32_t segment_count = 0U;
                std::uint32_t figure_size = 0U;
                std::uint32_t last_segment_offset = 0U;
                std::uint32_t figure_padding = 0U;
                progpu_native_point start{};
                if (figures.size() - std::min(figures.size(), offset) < 40U ||
                    !read_at(figures, offset, back_size) ||
                    !read_at(figures, offset + 4U, figure_flags) ||
                    !read_at(figures, offset + 8U, segment_count) ||
                    !read_at(figures, offset + 12U, figure_size) ||
                    !read_point(offset + 16U, start) ||
                    !read_at(figures, offset + 32U, last_segment_offset) ||
                    !read_at(figures, offset + 36U, figure_padding) ||
                    back_size != previous_figure_size ||
                    (figure_flags & ~0x1FU) != 0U ||
                    figure_padding != 0U || figure_size < 40U ||
                    figure_size > figures.size() - offset ||
                    segment_count > maximum_path_record_count) {
                    return status::malformed_batch;
                }
                offset += 40U;
                include_bounds_point(start);
                std::uint32_t previous_segment_size = 0U;
                std::uint32_t actual_last_segment_offset = 0U;
                progpu_native_point current = start;
                std::vector<progpu_native_path_segment> figure_segments;
                std::vector<progpu_native_path_segment>
                    figure_per_point_segments;
                bool figure_has_per_point_arc = false;
                bool figure_per_point_supported = true;
                std::vector<parsed_stroke_edge> stroke_edges;
                for (std::uint32_t segment_index = 0U;
                    segment_index < segment_count;
                    ++segment_index) {
                    actual_last_segment_offset = static_cast<std::uint32_t>(
                        offset - figure_offset);
                    std::uint32_t segment_type = 0U;
                    std::uint32_t segment_flags = 0U;
                    std::uint32_t segment_back_size = 0U;
                    if (figures.size() - std::min(figures.size(), offset) < 12U ||
                        !read_at(figures, offset, segment_type) ||
                        !read_at(figures, offset + 4U, segment_flags) ||
                        !read_at(figures, offset + 8U, segment_back_size) ||
                        segment_back_size != previous_segment_size ||
                        (segment_flags & ~0x3FU) != 0U) {
                        return status::malformed_batch;
                    }
                    const bool segment_is_stroked =
                        (segment_flags & 0x04U) == 0U;
                    const bool segment_is_smooth_join =
                        (segment_flags & 0x08U) != 0U;
                    std::uint32_t point_count = 0U;
                    std::size_t segment_size = 0U;
                    std::size_t points_offset = 0U;
                    std::uint32_t point_stride = 1U;
                    std::uint32_t native_kind = PROGPU_NATIVE_PATH_SEGMENT_LINE;
                    if (segment_type >= 5U && segment_type <= 7U) {
                        if (!read_at(figures, offset + 12U, point_count)) {
                            return status::malformed_batch;
                        }
                        points_offset = offset + 16U;
                        segment_size = 16U +
                            static_cast<std::size_t>(point_count) * 16U;
                        if (segment_type == 6U) {
                            point_stride = 3U;
                            native_kind = PROGPU_NATIVE_PATH_SEGMENT_CUBIC;
                        } else if (segment_type == 7U) {
                            point_stride = 2U;
                            native_kind = PROGPU_NATIVE_PATH_SEGMENT_QUADRATIC;
                        }
                    } else if (segment_type >= 1U && segment_type <= 3U) {
                        point_count = segment_type == 1U
                            ? 1U
                            : segment_type == 2U ? 3U : 2U;
                        point_stride = point_count;
                        native_kind = segment_type == 1U
                            ? PROGPU_NATIVE_PATH_SEGMENT_LINE
                            : segment_type == 2U
                                ? PROGPU_NATIVE_PATH_SEGMENT_CUBIC
                                : PROGPU_NATIVE_PATH_SEGMENT_QUADRATIC;
                        points_offset = offset + 16U;
                        segment_size = 16U +
                            static_cast<std::size_t>(point_count) * 16U;
                        std::uint32_t segment_padding = 0U;
                        if (!read_at(figures, offset + 12U, segment_padding) ||
                            segment_padding != 0U) {
                            return status::malformed_batch;
                        }
                    } else if (segment_type == 4U) {
                        constexpr std::size_t arc_segment_size = 64U;
                        const std::size_t figure_consumed =
                            offset - figure_offset;
                        std::uint32_t large_arc = 0U;
                        std::uint32_t sweep = 0U;
                        std::uint32_t segment_padding = 0U;
                        progpu_native_point end{};
                        double radius_x = 0.0;
                        double radius_y = 0.0;
                        double rotation = 0.0;
                        if (figure_consumed > figure_size ||
                            arc_segment_size > figures.size() - offset ||
                            arc_segment_size > figure_size - figure_consumed ||
                            !read_at(figures, offset + 12U, large_arc) ||
                            !read_point(offset + 16U, end) ||
                            !read_at(figures, offset + 32U, radius_x) ||
                            !read_at(figures, offset + 40U, radius_y) ||
                            !read_at(figures, offset + 48U, rotation) ||
                            !read_at(figures, offset + 56U, sweep) ||
                            !read_at(
                                figures,
                                offset + 60U,
                                segment_padding) ||
                            large_arc > 1U || sweep > 1U ||
                            segment_padding != 0U ||
                            !finite_double_as_float(radius_x) ||
                            !finite_double_as_float(radius_y) ||
                            !finite_double_as_float(rotation) ||
                            radius_x < 0.0 || radius_y < 0.0) {
                            return status::malformed_batch;
                        }
                        progpu_native_path_segment stroke_segment{};
                        stroke_segment.p0 = current;
                        stroke_segment.p1 = end;
                        stroke_segment.kind =
                            PROGPU_NATIVE_PATH_SEGMENT_LINE;
                        const progpu::native::geometry::arc_point arc_start{
                            current.x,
                            current.y};
                        const progpu::native::geometry::arc_point arc_end{
                            end.x,
                            end.y};
                        progpu::native::geometry::arc_point center{};
                        float theta1 = 0.0F;
                        float delta_theta = 0.0F;
                        float resolved_radius_x = 0.0F;
                        float resolved_radius_y = 0.0F;
                        if (progpu::native::geometry::resolve_arc(
                                arc_start,
                                arc_end,
                                {static_cast<float>(radius_x),
                                 static_cast<float>(radius_y)},
                                static_cast<float>(rotation),
                                large_arc != 0U,
                                sweep != 0U,
                                center,
                                theta1,
                                delta_theta,
                                resolved_radius_x,
                                resolved_radius_y)) {
                            progpu_native_path_segment segment{};
                            segment.p0 = current;
                            segment.p1 = end;
                            segment.p2 = {center.x, center.y};
                            segment.p3 = {
                                resolved_radius_x,
                                resolved_radius_y};
                            segment.kind = PROGPU_NATIVE_PATH_SEGMENT_ARC;
                            segment.pad0 = std::bit_cast<std::uint32_t>(
                                theta1);
                            segment.pad1 = std::bit_cast<std::uint32_t>(
                                delta_theta);
                            segment.pad2 = std::bit_cast<std::uint32_t>(
                                static_cast<float>(rotation) *
                                std::numbers::pi_v<float> / 180.0F);
                            stroke_segment = segment;
                            figure_segments.push_back(segment);
                            std::array<
                                progpu::native::geometry::wpf_cubic_arc_piece,
                                4U> cubic_pieces{};
                            int cubic_piece_count = -1;
                            if (progpu::native::geometry::
                                    lower_wpf_arc_to_cubics(
                                        arc_start,
                                        arc_end,
                                        {static_cast<float>(radius_x),
                                         static_cast<float>(radius_y)},
                                        static_cast<float>(rotation),
                                        large_arc != 0U,
                                        sweep != 0U,
                                        cubic_pieces,
                                        cubic_piece_count) &&
                                cubic_piece_count > 0) {
                                progpu_native_point cubic_start = current;
                                for (int piece_index = 0;
                                     piece_index < cubic_piece_count;
                                     ++piece_index) {
                                    const auto& piece = cubic_pieces[
                                        static_cast<std::size_t>(
                                            piece_index)];
                                    progpu_native_path_segment cubic{};
                                    cubic.p0 = cubic_start;
                                    cubic.p1 = {
                                        piece.control1.x,
                                        piece.control1.y};
                                    cubic.p2 = {
                                        piece.control2.x,
                                        piece.control2.y};
                                    cubic.p3 = {
                                        piece.end.x,
                                        piece.end.y};
                                    cubic.kind =
                                        PROGPU_NATIVE_PATH_SEGMENT_CUBIC;
                                    figure_per_point_segments.push_back(
                                        cubic);
                                    cubic_start = cubic.p3;
                                }
                                figure_has_per_point_arc = true;
                            } else {
                                figure_per_point_supported = false;
                                figure_per_point_segments.push_back(segment);
                            }
                            include_bounds_point(segment.p0);
                            include_bounds_point(segment.p1);
                            const float rotation_degrees =
                                static_cast<float>(rotation);
                            const float rotation_radians =
                                rotation_degrees *
                                std::numbers::pi_v<float> / 180.0F;
                            const float cosine_rotation =
                                std::cos(rotation_radians);
                            const float sine_rotation =
                                std::sin(rotation_radians);
                            const float x_extrema = std::atan2(
                                -resolved_radius_y * sine_rotation,
                                resolved_radius_x * cosine_rotation);
                            const float y_extrema = std::atan2(
                                resolved_radius_y * cosine_rotation,
                                resolved_radius_x * sine_rotation);
                            const float arc_extrema[4]{
                                x_extrema,
                                x_extrema + std::numbers::pi_v<float>,
                                y_extrema,
                                y_extrema + std::numbers::pi_v<float>};
                            for (const float theta : arc_extrema) {
                                if (!progpu::native::geometry::
                                        angle_within_sweep(
                                            theta,
                                            theta1,
                                            delta_theta)) {
                                    continue;
                                }
                                const auto point =
                                    progpu::native::geometry::evaluate_arc(
                                        center,
                                        resolved_radius_x,
                                        resolved_radius_y,
                                        rotation_degrees,
                                        theta);
                                include_bounds_point({point.x, point.y});
                            }
                        } else if (!progpu::native::geometry::equal(
                                arc_start,
                                arc_end)) {
                            progpu_native_path_segment segment{};
                            segment.p0 = current;
                            segment.p1 = end;
                            segment.kind = PROGPU_NATIVE_PATH_SEGMENT_LINE;
                            figure_segments.push_back(segment);
                            figure_per_point_segments.push_back(segment);
                            include_bounds_point(segment.p0);
                            include_bounds_point(segment.p1);
                        }
                        stroke_edges.push_back({
                            stroke_segment,
                            segment_is_stroked,
                            segment_is_smooth_join});
                        current = end;
                        offset += arc_segment_size;
                        previous_segment_size = static_cast<std::uint32_t>(
                            arc_segment_size);
                        continue;
                    } else {
                        return status::malformed_batch;
                    }
                    const std::size_t figure_consumed =
                        offset - figure_offset;
                    if (point_count == 0U || point_count % point_stride != 0U ||
                        figure_consumed > figure_size ||
                        segment_size > figures.size() - offset ||
                        segment_size > figure_size - figure_consumed) {
                        return status::malformed_batch;
                    }
                    for (std::uint32_t point_index = 0U;
                        point_index < point_count;
                        point_index += point_stride) {
                        progpu_native_path_segment segment{};
                        segment.p0 = current;
                        segment.kind = native_kind;
                        if (native_kind == PROGPU_NATIVE_PATH_SEGMENT_LINE) {
                            if (!read_point(
                                    points_offset +
                                        static_cast<std::size_t>(point_index) * 16U,
                                    segment.p1)) {
                                return status::malformed_batch;
                            }
                            current = segment.p1;
                            include_bounds_point(segment.p0);
                            include_bounds_point(segment.p1);
                        } else if (
                            native_kind ==
                            PROGPU_NATIVE_PATH_SEGMENT_QUADRATIC) {
                            if (!read_point(
                                    points_offset +
                                        static_cast<std::size_t>(point_index) * 16U,
                                    segment.p1) ||
                                !read_point(
                                    points_offset +
                                        static_cast<std::size_t>(point_index + 1U) * 16U,
                                    segment.p2)) {
                                return status::malformed_batch;
                            }
                            current = segment.p2;
                            include_bounds_point(segment.p0);
                            include_bounds_point(segment.p1);
                            include_bounds_point(segment.p2);
                        } else {
                            if (!read_point(
                                    points_offset +
                                        static_cast<std::size_t>(point_index) * 16U,
                                    segment.p1) ||
                                !read_point(
                                    points_offset +
                                        static_cast<std::size_t>(point_index + 1U) * 16U,
                                    segment.p2) ||
                                !read_point(
                                    points_offset +
                                        static_cast<std::size_t>(point_index + 2U) * 16U,
                                    segment.p3)) {
                                return status::malformed_batch;
                            }
                            current = segment.p3;
                            include_bounds_point(segment.p0);
                            include_bounds_point(segment.p1);
                            include_bounds_point(segment.p2);
                            include_bounds_point(segment.p3);
                        }
                        stroke_edges.push_back({
                            segment,
                            segment_is_stroked,
                            segment_is_smooth_join});
                        figure_segments.push_back(segment);
                        figure_per_point_segments.push_back(segment);
                    }
                    offset += segment_size;
                    previous_segment_size = static_cast<std::uint32_t>(
                        segment_size);
                }
                if (offset - figure_offset != figure_size ||
                    (segment_count == 0U && last_segment_offset != 0U) ||
                    (segment_count != 0U &&
                     last_segment_offset != actual_last_segment_offset)) {
                    return status::malformed_batch;
                }
                if ((figure_flags & 0x08U) != 0U) {
                    geometry.segments.insert(
                        geometry.segments.end(),
                        figure_segments.begin(),
                        figure_segments.end());
                    geometry.per_point_segments.insert(
                        geometry.per_point_segments.end(),
                        figure_per_point_segments.begin(),
                        figure_per_point_segments.end());
                    has_per_point_arc = has_per_point_arc ||
                        figure_has_per_point_arc;
                    geometry.per_point_segments_supported =
                        geometry.per_point_segments_supported &&
                        figure_per_point_supported;
                    if (current.x != start.x || current.y != start.y) {
                        progpu_native_path_segment closing{};
                        closing.p0 = current;
                        closing.p1 = start;
                        closing.kind = PROGPU_NATIVE_PATH_SEGMENT_LINE;
                        geometry.segments.push_back(closing);
                        geometry.per_point_segments.push_back(closing);
                    }
                }
                const bool figure_is_closed = (figure_flags & 0x04U) != 0U;
                if (figure_is_closed &&
                    (current.x != start.x || current.y != start.y)) {
                    progpu_native_path_segment closing{};
                    closing.p0 = current;
                    closing.p1 = start;
                    closing.kind = PROGPU_NATIVE_PATH_SEGMENT_LINE;
                    stroke_edges.push_back({closing, true, false});
                }
                if (!stroke_edges.empty()) {
                    const auto append_open_run = [
                        &geometry,
                        &stroke_edges](
                        std::size_t first,
                        std::size_t count,
                        bool start_uses_dash_cap,
                        bool end_uses_dash_cap) {
                        path_stroke_contour_state contour{};
                        contour.start_uses_dash_cap = start_uses_dash_cap;
                        contour.end_uses_dash_cap = end_uses_dash_cap;
                        contour.points.reserve(count + 1U);
                        contour.points.push_back(
                            stroke_edges[first % stroke_edges.size()].segment.p0);
                        contour.segments.reserve(count);
                        contour.smooth_joins.reserve(count);
                        for (std::size_t index = 0U; index < count; ++index) {
                            const auto& edge = stroke_edges[
                                (first + index) % stroke_edges.size()];
                            contour.points.push_back(
                                edge.segment.kind ==
                                        PROGPU_NATIVE_PATH_SEGMENT_QUADRATIC
                                    ? edge.segment.p2
                                    : edge.segment.kind ==
                                            PROGPU_NATIVE_PATH_SEGMENT_CUBIC
                                        ? edge.segment.p3
                                        : edge.segment.p1);
                            contour.segments.push_back(edge.segment);
                            contour.smooth_joins.push_back(
                                edge.smooth_join ? 1U : 0U);
                        }
                        geometry.stroke_contours.push_back(
                            std::move(contour));
                    };
                    const bool all_edges_stroked = std::ranges::all_of(
                        stroke_edges,
                        [](const parsed_stroke_edge& edge) {
                            return edge.stroked;
                        });
                    if (figure_is_closed && all_edges_stroked) {
                        path_stroke_contour_state contour{};
                        contour.closed = true;
                        contour.points.reserve(stroke_edges.size());
                        contour.segments.reserve(stroke_edges.size());
                        contour.smooth_joins.reserve(stroke_edges.size());
                        contour.points.push_back(
                            stroke_edges.front().segment.p0);
                        for (std::size_t index = 0U;
                             index + 1U < stroke_edges.size();
                             ++index) {
                            const auto& edge = stroke_edges[index];
                            contour.points.push_back(
                                edge.segment.kind ==
                                        PROGPU_NATIVE_PATH_SEGMENT_QUADRATIC
                                    ? edge.segment.p2
                                    : edge.segment.kind ==
                                            PROGPU_NATIVE_PATH_SEGMENT_CUBIC
                                        ? edge.segment.p3
                                        : edge.segment.p1);
                        }
                        for (const auto& edge : stroke_edges) {
                            contour.segments.push_back(edge.segment);
                            contour.smooth_joins.push_back(
                                edge.smooth_join ? 1U : 0U);
                        }
                        geometry.stroke_contours.push_back(
                            std::move(contour));
                    } else if (figure_is_closed) {
                        const auto gap = std::ranges::find_if(
                            stroke_edges,
                            [](const parsed_stroke_edge& edge) {
                                return !edge.stroked;
                            });
                        const std::size_t first_after_gap =
                            (static_cast<std::size_t>(
                                std::distance(stroke_edges.begin(), gap)) +
                             1U) % stroke_edges.size();
                        std::size_t consumed = 0U;
                        while (consumed < stroke_edges.size()) {
                            while (consumed < stroke_edges.size() &&
                                !stroke_edges[
                                    (first_after_gap + consumed) %
                                    stroke_edges.size()].stroked) {
                                ++consumed;
                            }
                            const std::size_t first = consumed;
                            while (consumed < stroke_edges.size() &&
                                stroke_edges[
                                    (first_after_gap + consumed) %
                                    stroke_edges.size()].stroked) {
                                ++consumed;
                            }
                            if (consumed != first) {
                                append_open_run(
                                    first_after_gap + first,
                                    consumed - first,
                                    true,
                                    true);
                            }
                        }
                    } else {
                        std::size_t index = 0U;
                        while (index < stroke_edges.size()) {
                            while (index < stroke_edges.size() &&
                                !stroke_edges[index].stroked) {
                                ++index;
                            }
                            const std::size_t first = index;
                            while (index < stroke_edges.size() &&
                                stroke_edges[index].stroked) {
                                ++index;
                            }
                            if (index != first) {
                                append_open_run(
                                    first,
                                    index - first,
                                    first != 0U,
                                    index != stroke_edges.size());
                            }
                        }
                    }
                }
                previous_figure_size = figure_size;
            }
            if (offset != figures.size()) {
                return status::malformed_batch;
            }
            if (!has_per_point_arc ||
                !geometry.per_point_segments_supported) {
                geometry.per_point_segments.clear();
            }
            if (!packet_bounds_valid) {
                geometry.left = has_computed_bounds ? computed_left : 0.0;
                geometry.top = has_computed_bounds ? computed_top : 0.0;
                geometry.right = has_computed_bounds ? computed_right : 0.0;
                geometry.bottom = has_computed_bounds ? computed_bottom : 0.0;
            }
            path_geometries.insert_or_assign(handle, std::move(geometry));
            increment_generation(handle);
            ++metrics.updated_resource_count;
            return status::success;
        }
        case command::image_brush:
            return apply_tile_brush<command_layouts::image_brush>(view,
                type_image_brush, command_layouts::image_brush::h_image_source_offset, metrics);
        case command::drawing_brush:
            return apply_tile_brush<command_layouts::drawing_brush>(view,
                type_drawing_brush, command_layouts::drawing_brush::h_drawing_offset, metrics);
        case command::visual_brush:
            return apply_tile_brush<command_layouts::visual_brush>(view,
                type_visual_brush, command_layouts::visual_brush::h_visual_offset, metrics);
        case command::solid_color_brush: {
            using layout = command_layouts::solid_color_brush;
            double opacity = 0.0;
            progpu_native_color color{};
            std::uint32_t opacity_animations = 0U;
            std::uint32_t transform = 0U;
            std::uint32_t relative_transform = 0U;
            std::uint32_t color_animations = 0U;
            if (!has_exact_size(view, layout::fixed_size) ||
                !read_at(view.packet, layout::handle_offset, handle) ||
                !read_at(view.packet, layout::opacity_offset, opacity) ||
                !read_at(view.packet, layout::color_offset, color) ||
                !read_at(
                    view.packet,
                    layout::h_opacity_animations_offset,
                    opacity_animations) ||
                !read_at(view.packet, layout::h_transform_offset, transform) ||
                !read_at(
                    view.packet,
                    layout::h_relative_transform_offset,
                    relative_transform) ||
                !read_at(
                    view.packet,
                    layout::h_color_animations_offset,
                    color_animations)) {
                return status::malformed_batch;
            }
            if (!require_resource(handle, type_solid_color_brush)) {
                return status::invalid_handle;
            }
            if ((transform != 0U && !require_transform(transform)) ||
                (relative_transform != 0U &&
                 !require_transform(relative_transform)) ||
                (opacity_animations != 0U &&
                 !require_resource(
                     opacity_animations,
                     type_double_resource)) ||
                (color_animations != 0U &&
                 !require_resource(
                     color_animations,
                     type_color_resource))) {
                return status::invalid_handle;
            }
            if (!std::isfinite(opacity) || opacity < 0.0 || opacity > 1.0 ||
                !std::isfinite(color.r) || !std::isfinite(color.g) ||
                !std::isfinite(color.b) || !std::isfinite(color.a)) {
                return status::malformed_batch;
            }
            solid_brushes.insert_or_assign(
                handle,
                solid_brush_state{
                    opacity,
                    color,
                    transform,
                    relative_transform,
                    opacity_animations,
                    color_animations});
            increment_generation(handle);
            ++metrics.updated_resource_count;
            return status::success;
        }
        case command::blur_effect: {
            using layout = command_layouts::blur_effect;
            double radius = 0.0;
            std::uint32_t radius_animation = 0U;
            std::uint32_t kernel_type = 0U;
            std::uint32_t rendering_bias = 0U;
            if (!has_exact_size(view, layout::fixed_size) ||
                !read_at(view.packet, layout::handle_offset, handle) ||
                !read_at(view.packet, layout::radius_offset, radius) ||
                !read_at(
                    view.packet,
                    layout::h_radius_animations_offset,
                    radius_animation) ||
                !read_at(
                    view.packet,
                    layout::kernel_type_offset,
                    kernel_type) ||
                !read_at(
                    view.packet,
                    layout::rendering_bias_offset,
                    rendering_bias)) {
                return status::malformed_batch;
            }
            if (!require_resource(handle, type_blur_effect)) {
                return status::invalid_handle;
            }
            if (kernel_type > 1U) {
                return status::unsupported_command;
            }
            if (radius_animation != 0U &&
                !require_resource(radius_animation, type_double_resource)) {
                return status::invalid_handle;
            }
            if (!std::isfinite(radius) || rendering_bias > 1U) {
                return status::malformed_batch;
            }
            effect_state effect{};
            effect.type = effect_state::kind::blur;
            effect.radius = std::max(0.0, radius);
            effect.animations[0] = radius_animation;
            effect.box_blur = kernel_type == 1U;
            effects.insert_or_assign(handle, effect);
            increment_generation(handle);
            ++metrics.updated_resource_count;
            return status::success;
        }
        case command::drop_shadow_effect: {
            using layout = command_layouts::drop_shadow_effect;
            effect_state effect{};
            effect.type = effect_state::kind::drop_shadow;
            std::array<std::uint32_t, 5U> animations{};
            std::uint32_t rendering_bias = 0U;
            if (!has_exact_size(view, layout::fixed_size) ||
                !read_at(view.packet, layout::handle_offset, handle) ||
                !read_at(
                    view.packet,
                    layout::shadow_depth_offset,
                    effect.shadow_depth) ||
                !read_at(view.packet, layout::color_offset, effect.color) ||
                !read_at(
                    view.packet,
                    layout::direction_offset,
                    effect.direction) ||
                !read_at(
                    view.packet,
                    layout::opacity_offset,
                    effect.opacity) ||
                !read_at(
                    view.packet,
                    layout::blur_radius_offset,
                    effect.radius) ||
                !read_at(
                    view.packet,
                    layout::h_shadow_depth_animations_offset,
                    animations[0]) ||
                !read_at(
                    view.packet,
                    layout::h_color_animations_offset,
                    animations[1]) ||
                !read_at(
                    view.packet,
                    layout::h_direction_animations_offset,
                    animations[2]) ||
                !read_at(
                    view.packet,
                    layout::h_opacity_animations_offset,
                    animations[3]) ||
                !read_at(
                    view.packet,
                    layout::h_blur_radius_animations_offset,
                    animations[4]) ||
                !read_at(
                    view.packet,
                    layout::rendering_bias_offset,
                    rendering_bias)) {
                return status::malformed_batch;
            }
            if (!require_resource(handle, type_drop_shadow_effect)) {
                return status::invalid_handle;
            }
            for (std::size_t index = 0U; index < animations.size(); ++index) {
                const std::uint32_t animation = animations[index];
                const std::uint32_t animation_type = index == 1U
                    ? type_color_resource
                    : type_double_resource;
                if (animation != 0U &&
                    !require_resource(animation, animation_type)) {
                    return status::invalid_handle;
                }
            }
            if (!std::isfinite(effect.shadow_depth) ||
                !std::isfinite(effect.direction) ||
                !std::isfinite(effect.opacity) ||
                !std::isfinite(effect.radius) ||
                !std::isfinite(effect.color.r) ||
                !std::isfinite(effect.color.g) ||
                !std::isfinite(effect.color.b) ||
                !std::isfinite(effect.color.a) || rendering_bias > 1U) {
                return status::malformed_batch;
            }
            effect.shadow_depth = std::max(0.0, effect.shadow_depth);
            effect.radius = std::max(0.0, effect.radius);
            effect.opacity = std::clamp(effect.opacity, 0.0, 1.0);
            effect.animations = animations;
            effects.insert_or_assign(handle, effect);
            increment_generation(handle);
            ++metrics.updated_resource_count;
            return status::success;
        }
        case command::glyph_run_create: {
            using layout = command_layouts::glyph_run_create;
            std::uint64_t ignored_dwrite_font = 0U;
            glyph_run_state glyph_run{};
            std::uint16_t glyph_count = 0U;
            const std::size_t origin = layout::origin_offset;
            const std::size_t bounds = layout::managed_bounds_offset;
            if (view.packet.size() < layout::fixed_size ||
                !read_at(view.packet, layout::handle_offset, handle) ||
                handle == 0U ||
                !read_at(
                    view.packet,
                    layout::p_id_write_font_offset,
                    ignored_dwrite_font) ||
                !read_at(
                    view.packet,
                    layout::glyph_run_flags_offset,
                    glyph_run.flags) ||
                !read_at(view.packet, origin, glyph_run.origin_x) ||
                !read_at(view.packet, origin + 4U, glyph_run.origin_y) ||
                !read_at(
                    view.packet,
                    layout::mu_size_offset,
                    glyph_run.em_size) ||
                !read_at(view.packet, bounds, glyph_run.bounds_x) ||
                !read_at(view.packet, bounds + 8U, glyph_run.bounds_y) ||
                !read_at(
                    view.packet,
                    bounds + 16U,
                    glyph_run.bounds_width) ||
                !read_at(
                    view.packet,
                    bounds + 24U,
                    glyph_run.bounds_height) ||
                !read_at(
                    view.packet,
                    layout::glyph_count_offset,
                    glyph_count) ||
                !read_at(
                    view.packet,
                    layout::bidi_level_offset,
                    glyph_run.bidi_level) ||
                !read_at(
                    view.packet,
                    layout::d_write_text_measuring_method_offset,
                    glyph_run.measuring_method)) {
                return status::malformed_batch;
            }
            (void)ignored_dwrite_font;
            constexpr std::uint16_t sideways_flag = 0x0001U;
            constexpr std::uint16_t has_offsets_flag = 0x0010U;
            if (glyph_count == 0U ||
                (glyph_run.flags & ~(sideways_flag | has_offsets_flag)) != 0U ||
                !std::isfinite(glyph_run.origin_x) ||
                !std::isfinite(glyph_run.origin_y) ||
                !std::isfinite(glyph_run.em_size) ||
                glyph_run.em_size <= 0.0F ||
                !finite_double_as_float(glyph_run.bounds_x) ||
                !finite_double_as_float(glyph_run.bounds_y) ||
                !finite_double_as_float(glyph_run.bounds_width) ||
                !finite_double_as_float(glyph_run.bounds_height) ||
                glyph_run.bounds_width < 0.0 ||
                glyph_run.bounds_height < 0.0 ||
                glyph_run.measuring_method > 2U) {
                return status::malformed_batch;
            }
            const std::size_t index_bytes =
                static_cast<std::size_t>(glyph_count) * sizeof(std::uint16_t);
            const std::size_t advance_bytes =
                static_cast<std::size_t>(glyph_count) * sizeof(float);
            const std::size_t offset_bytes =
                (glyph_run.flags & has_offsets_flag) != 0U
                    ? static_cast<std::size_t>(glyph_count) *
                        sizeof(progpu_native_point)
                    : 0U;
            const std::size_t required_size = layout::fixed_size +
                index_bytes + advance_bytes + offset_bytes;
            const std::size_t padded_size = (required_size + 3U) & ~3U;
            if (view.packet.size() != padded_size) {
                return status::malformed_batch;
            }
            for (std::size_t index = required_size;
                 index < padded_size;
                 ++index) {
                if (view.packet[index] != std::byte{}) {
                    return status::malformed_batch;
                }
            }
            const auto existing_resource = resources.find(handle);
            const bool created = existing_resource == resources.end();
            if (!created && existing_resource->second.type != type_glyph_run) {
                return status::resource_type_mismatch;
            }
            const auto existing_glyph_run = glyph_runs.find(handle);
            if (existing_glyph_run != glyph_runs.end()) {
                glyph_run.face_index = existing_glyph_run->second.face_index;
                glyph_run.style_simulations =
                    existing_glyph_run->second.style_simulations;
                glyph_run.font_data = existing_glyph_run->second.font_data;
            }
            glyph_run.glyph_indices.resize(glyph_count);
            glyph_run.advances.resize(glyph_count);
            if (offset_bytes != 0U) {
                glyph_run.offsets.resize(glyph_count);
            }
            std::memcpy(
                glyph_run.glyph_indices.data(),
                view.packet.data() + layout::fixed_size,
                index_bytes);
            std::memcpy(
                glyph_run.advances.data(),
                view.packet.data() + layout::fixed_size + index_bytes,
                advance_bytes);
            if (offset_bytes != 0U) {
                std::memcpy(
                    glyph_run.offsets.data(),
                    view.packet.data() + layout::fixed_size + index_bytes +
                        advance_bytes,
                    offset_bytes);
            }
            for (std::size_t index = 0U; index < glyph_count; ++index) {
                if (!std::isfinite(glyph_run.advances[index]) ||
                    (offset_bytes != 0U &&
                     (!std::isfinite(glyph_run.offsets[index].x) ||
                      !std::isfinite(glyph_run.offsets[index].y)))) {
                    return status::malformed_batch;
                }
            }
            if (created) {
                resource_state resource{};
                resource.type = type_glyph_run;
                resources.emplace(handle, std::move(resource));
                ++metrics.created_resource_count;
            } else {
                increment_generation(handle);
            }
            glyph_runs.insert_or_assign(handle, std::move(glyph_run));
            ++metrics.updated_resource_count;
            return status::success;
        }
        case command::linear_gradient_brush:
        case command::radial_gradient_brush: {
            using linear_layout = command_layouts::linear_gradient_brush;
            using radial_layout = command_layouts::radial_gradient_brush;
            const bool radial = view.kind == command::radial_gradient_brush;
            const std::size_t fixed_size = radial
                ? radial_layout::fixed_size
                : linear_layout::fixed_size;
            gradient_brush_state brush{};
            brush.type = radial
                ? gradient_brush_state::kind::radial
                : gradient_brush_state::kind::linear;
            std::uint32_t stops_size = 0U;
            if (view.packet.size() < fixed_size ||
                !read_at(
                    view.packet,
                    radial
                        ? radial_layout::handle_offset
                        : linear_layout::handle_offset,
                    handle) ||
                !read_at(
                    view.packet,
                    radial
                        ? radial_layout::opacity_offset
                        : linear_layout::opacity_offset,
                    brush.opacity)) {
                return status::malformed_batch;
            }
            if (radial) {
                if (!read_at(
                        view.packet,
                        radial_layout::center_offset,
                        brush.first_x) ||
                    !read_at(
                        view.packet,
                        radial_layout::center_offset + 8U,
                        brush.first_y) ||
                    !read_at(
                        view.packet,
                        radial_layout::radius_x_offset,
                        brush.radius_x) ||
                    !read_at(
                        view.packet,
                        radial_layout::radius_y_offset,
                        brush.radius_y) ||
                    !read_at(
                        view.packet,
                        radial_layout::gradient_origin_offset,
                        brush.second_x) ||
                    !read_at(
                        view.packet,
                        radial_layout::gradient_origin_offset + 8U,
                        brush.second_y) ||
                    !read_at(
                        view.packet,
                        radial_layout::h_opacity_animations_offset,
                        brush.opacity_animation) ||
                    !read_at(
                        view.packet,
                        radial_layout::h_transform_offset,
                        brush.transform_handle) ||
                    !read_at(
                        view.packet,
                        radial_layout::h_relative_transform_offset,
                        brush.relative_transform_handle) ||
                    !read_at(
                        view.packet,
                        radial_layout::color_interpolation_mode_offset,
                        brush.color_interpolation_mode) ||
                    !read_at(
                        view.packet,
                        radial_layout::mapping_mode_offset,
                        brush.mapping_mode) ||
                    !read_at(
                        view.packet,
                        radial_layout::spread_method_offset,
                        brush.spread_method) ||
                    !read_at(
                        view.packet,
                        radial_layout::gradient_stops_size_offset,
                        stops_size) ||
                    !read_at(
                        view.packet,
                        radial_layout::h_center_animations_offset,
                        brush.first_point_animation) ||
                    !read_at(
                        view.packet,
                        radial_layout::h_radius_x_animations_offset,
                        brush.radius_x_animation) ||
                    !read_at(
                        view.packet,
                        radial_layout::h_radius_y_animations_offset,
                        brush.radius_y_animation) ||
                    !read_at(
                        view.packet,
                        radial_layout::h_gradient_origin_animations_offset,
                        brush.second_point_animation)) {
                    return status::malformed_batch;
                }
            } else if (!read_at(
                    view.packet,
                    linear_layout::start_point_offset,
                    brush.first_x) ||
                !read_at(
                    view.packet,
                    linear_layout::start_point_offset + 8U,
                    brush.first_y) ||
                !read_at(
                    view.packet,
                    linear_layout::end_point_offset,
                    brush.second_x) ||
                !read_at(
                    view.packet,
                    linear_layout::end_point_offset + 8U,
                    brush.second_y) ||
                !read_at(
                    view.packet,
                    linear_layout::h_opacity_animations_offset,
                    brush.opacity_animation) ||
                !read_at(
                    view.packet,
                    linear_layout::h_transform_offset,
                    brush.transform_handle) ||
                !read_at(
                    view.packet,
                    linear_layout::h_relative_transform_offset,
                    brush.relative_transform_handle) ||
                !read_at(
                    view.packet,
                    linear_layout::color_interpolation_mode_offset,
                    brush.color_interpolation_mode) ||
                !read_at(
                    view.packet,
                    linear_layout::mapping_mode_offset,
                    brush.mapping_mode) ||
                !read_at(
                    view.packet,
                    linear_layout::spread_method_offset,
                    brush.spread_method) ||
                !read_at(
                    view.packet,
                    linear_layout::gradient_stops_size_offset,
                    stops_size) ||
                !read_at(
                    view.packet,
                    linear_layout::h_start_point_animations_offset,
                    brush.first_point_animation) ||
                !read_at(
                    view.packet,
                    linear_layout::h_end_point_animations_offset,
                    brush.second_point_animation)) {
                return status::malformed_batch;
            }
            const std::uint32_t expected_type = radial
                ? type_radial_gradient_brush
                : type_linear_gradient_brush;
            if (!require_resource(handle, expected_type)) {
                return status::invalid_handle;
            }
            if (stops_size % 24U != 0U ||
                view.packet.size() != fixed_size + stops_size ||
                !finite_double_as_float(brush.opacity) ||
                brush.opacity < 0.0 || brush.opacity > 1.0 ||
                !finite_double_as_float(brush.first_x) ||
                !finite_double_as_float(brush.first_y) ||
                !finite_double_as_float(brush.second_x) ||
                !finite_double_as_float(brush.second_y) ||
                (radial && (!finite_double_as_float(brush.radius_x) ||
                    !finite_double_as_float(brush.radius_y))) ||
                brush.color_interpolation_mode > 1U ||
                brush.mapping_mode > 1U || brush.spread_method > 2U) {
                return status::malformed_batch;
            }
            if ((brush.opacity_animation != 0U &&
                    !require_resource(
                        brush.opacity_animation, type_double_resource)) ||
                (brush.transform_handle != 0U &&
                    !require_transform(brush.transform_handle)) ||
                (brush.relative_transform_handle != 0U &&
                    !require_transform(brush.relative_transform_handle)) ||
                (brush.first_point_animation != 0U &&
                    !require_resource(
                        brush.first_point_animation, type_point_resource)) ||
                (brush.second_point_animation != 0U &&
                    !require_resource(
                        brush.second_point_animation, type_point_resource)) ||
                (brush.radius_x_animation != 0U &&
                    !require_resource(
                        brush.radius_x_animation, type_double_resource)) ||
                (brush.radius_y_animation != 0U &&
                    !require_resource(
                        brush.radius_y_animation, type_double_resource))) {
                return status::invalid_handle;
            }
            const std::size_t stop_count = stops_size / 24U;
            try {
                brush.stops.resize(stop_count);
            } catch (const std::bad_alloc&) {
                return status::capacity_exceeded;
            }
            for (std::size_t index = 0U; index < stop_count; ++index) {
                auto& stop = brush.stops[index];
                const std::size_t offset = fixed_size + index * 24U;
                if (!read_at(view.packet, offset, stop.position) ||
                    !read_at(view.packet, offset + 8U, stop.color) ||
                    !finite_double_as_float(stop.position) ||
                    !std::isfinite(stop.color.r) ||
                    !std::isfinite(stop.color.g) ||
                    !std::isfinite(stop.color.b) ||
                    !std::isfinite(stop.color.a)) {
                    return status::malformed_batch;
                }
            }
            gradient_brushes.insert_or_assign(handle, std::move(brush));
            increment_generation(handle);
            ++metrics.updated_resource_count;
            return status::success;
        }
        case command::dash_style: {
            using layout = command_layouts::dash_style;
            dash_style_state dash{};
            std::uint32_t offset_animations = 0U;
            std::uint32_t dashes_size = 0U;
            if (view.packet.size() < layout::fixed_size ||
                !read_at(view.packet, layout::handle_offset, handle) ||
                !read_at(view.packet, layout::offset_offset, dash.offset) ||
                !read_at(
                    view.packet,
                    layout::h_offset_animations_offset,
                    offset_animations) ||
                !read_at(
                    view.packet,
                    layout::dashes_size_offset,
                    dashes_size) ||
                dashes_size % sizeof(double) != 0U ||
                view.packet.size() != layout::fixed_size + dashes_size) {
                return status::malformed_batch;
            }
            if (!require_resource(handle, type_dash_style)) {
                return status::invalid_handle;
            }
            if (offset_animations != 0U &&
                !require_resource(offset_animations, type_double_resource)) {
                return status::invalid_handle;
            }
            if (!finite_double_as_float(dash.offset)) {
                return status::malformed_batch;
            }
            const std::size_t dash_count =
                dashes_size / sizeof(double);
            dash.offset_animation_handle = offset_animations;
            try {
                dash.intervals.resize(dash_count);
            } catch (const std::bad_alloc&) {
                return status::capacity_exceeded;
            }
            for (std::size_t index = 0U; index < dash_count; ++index) {
                if (!read_at(
                        view.packet,
                        layout::fixed_size + index * sizeof(double),
                        dash.intervals[index]) ||
                    !finite_double_as_float(dash.intervals[index]) ||
                    dash.intervals[index] < 0.0) {
                    return status::malformed_batch;
                }
            }
            dash_styles.insert_or_assign(handle, std::move(dash));
            increment_generation(handle);
            ++metrics.updated_resource_count;
            return status::success;
        }
        case command::pen: {
            using layout = command_layouts::pen;
            pen_state pen{};
            std::uint32_t thickness_animations = 0U;
            std::uint32_t dash_style = 0U;
            if (!has_exact_size(view, layout::fixed_size) ||
                !read_at(view.packet, layout::handle_offset, handle) ||
                !read_at(
                    view.packet,
                    layout::thickness_offset,
                    pen.thickness) ||
                !read_at(
                    view.packet,
                    layout::miter_limit_offset,
                    pen.miter_limit) ||
                !read_at(
                    view.packet,
                    layout::h_brush_offset,
                    pen.brush_handle) ||
                !read_at(
                    view.packet,
                    layout::h_thickness_animations_offset,
                    thickness_animations) ||
                !read_at(
                    view.packet,
                    layout::start_line_cap_offset,
                    pen.start_line_cap) ||
                !read_at(
                    view.packet,
                    layout::end_line_cap_offset,
                    pen.end_line_cap) ||
                !read_at(view.packet, layout::dash_cap_offset, pen.dash_cap) ||
                !read_at(view.packet, layout::line_join_offset, pen.line_join) ||
                !read_at(
                    view.packet,
                    layout::h_dash_style_offset,
                    dash_style)) {
                return status::malformed_batch;
            }
            pen.dash_style_handle = dash_style;
            if (!require_resource(handle, type_pen) ||
                (pen.brush_handle != 0U &&
                 !require_brush(pen.brush_handle)) ||
                (dash_style != 0U &&
                 !require_resource(dash_style, type_dash_style))) {
                return status::invalid_handle;
            }
            if (thickness_animations != 0U &&
                !require_resource(
                    thickness_animations, type_double_resource)) {
                return status::invalid_handle;
            }
            if (!finite_double_as_float(pen.thickness) ||
                pen.thickness < 0.0 ||
                !finite_double_as_float(pen.miter_limit) ||
                pen.miter_limit < 0.0 || pen.start_line_cap > 3U ||
                pen.end_line_cap > 3U || pen.dash_cap > 3U ||
                pen.line_join > 2U) {
                return status::malformed_batch;
            }
            pen.thickness_animation_handle = thickness_animations;
            pens.insert_or_assign(handle, pen);
            increment_generation(handle);
            ++metrics.updated_resource_count;
            return status::success;
        }
        case command::geometry_drawing: {
            using layout = command_layouts::geometry_drawing;
            geometry_drawing_state drawing{};
            if (!has_exact_size(view, layout::fixed_size) ||
                !read_at(view.packet, layout::handle_offset, handle) ||
                !read_at(
                    view.packet,
                    layout::h_brush_offset,
                    drawing.brush_handle) ||
                !read_at(
                    view.packet,
                    layout::h_pen_offset,
                    drawing.pen_handle) ||
                !read_at(
                    view.packet,
                    layout::h_geometry_offset,
                    drawing.geometry_handle)) {
                return status::malformed_batch;
            }
            if (!require_resource(handle, type_geometry_drawing) ||
                (drawing.brush_handle != 0U &&
                 !require_brush(drawing.brush_handle)) ||
                (drawing.pen_handle != 0U &&
                 !require_resource(drawing.pen_handle, type_pen)) ||
                (drawing.geometry_handle != 0U &&
                 !require_geometry(drawing.geometry_handle))) {
                return status::invalid_handle;
            }
            geometry_drawings.insert_or_assign(handle, drawing);
            increment_generation(handle);
            ++metrics.updated_resource_count;
            return status::success;
        }
        case command::glyph_run_drawing: {
            using layout = command_layouts::glyph_run_drawing;
            glyph_run_drawing_state drawing{};
            if (!has_exact_size(view, layout::fixed_size) ||
                !read_at(view.packet, layout::handle_offset, handle) ||
                !read_at(
                    view.packet,
                    layout::h_glyph_run_offset,
                    drawing.glyph_run_handle) ||
                !read_at(
                    view.packet,
                    layout::h_foreground_brush_offset,
                    drawing.foreground_brush_handle)) {
                return status::malformed_batch;
            }
            if (!require_resource(handle, type_glyph_run_drawing) ||
                (drawing.glyph_run_handle != 0U &&
                 !require_resource(
                     drawing.glyph_run_handle, type_glyph_run)) ||
                (drawing.foreground_brush_handle != 0U &&
                 !require_brush(drawing.foreground_brush_handle))) {
                return status::invalid_handle;
            }
            glyph_run_drawings.insert_or_assign(handle, drawing);
            increment_generation(handle);
            ++metrics.updated_resource_count;
            return status::success;
        }
        case command::image_drawing: {
            using layout = command_layouts::image_drawing;
            image_drawing_state drawing{};
            const std::size_t rect = layout::rect_offset;
            if (!has_exact_size(view, layout::fixed_size) ||
                !read_at(view.packet, layout::handle_offset, handle) ||
                !read_at(view.packet, rect, drawing.x) ||
                !read_at(view.packet, rect + 8U, drawing.y) ||
                !read_at(view.packet, rect + 16U, drawing.width) ||
                !read_at(view.packet, rect + 24U, drawing.height) ||
                !read_at(
                    view.packet,
                    layout::h_image_source_offset,
                    drawing.image_source_handle) ||
                !read_at(
                    view.packet,
                    layout::h_rect_animations_offset,
                    drawing.rect_animation_handle)) {
                return status::malformed_batch;
            }
            const auto image_source = resources.find(
                drawing.image_source_handle);
            if (!require_resource(handle, type_image_drawing) ||
                (drawing.image_source_handle != 0U &&
                 (image_source == resources.end() ||
                  (image_source->second.type != type_bitmap_source &&
                   image_source->second.type !=
                       type_double_buffered_bitmap &&
                   image_source->second.type != type_d3d_image &&
                   image_source->second.type != type_drawing_image))) ||
                (drawing.rect_animation_handle != 0U &&
                 !require_resource(
                     drawing.rect_animation_handle,
                     type_rect_resource))) {
                return status::invalid_handle;
            }
            if (!finite_double_as_float(drawing.x) ||
                !finite_double_as_float(drawing.y) ||
                !finite_double_as_float(drawing.width) ||
                !finite_double_as_float(drawing.height) ||
                drawing.width < 0.0 || drawing.height < 0.0) {
                return status::malformed_batch;
            }
            image_drawings.insert_or_assign(handle, drawing);
            increment_generation(handle);
            ++metrics.updated_resource_count;
            return status::success;
        }
        case command::video_drawing: {
            using layout = command_layouts::video_drawing;
            video_drawing_state drawing{};
            const std::size_t rect = layout::rect_offset;
            if (!has_exact_size(view, layout::fixed_size) ||
                !read_at(view.packet, layout::handle_offset, handle) ||
                !read_at(view.packet, rect, drawing.x) ||
                !read_at(view.packet, rect + 8U, drawing.y) ||
                !read_at(view.packet, rect + 16U, drawing.width) ||
                !read_at(view.packet, rect + 24U, drawing.height) ||
                !read_at(
                    view.packet,
                    layout::h_player_offset,
                    drawing.player_handle) ||
                !read_at(
                    view.packet,
                    layout::h_rect_animations_offset,
                    drawing.rect_animation_handle)) {
                return status::malformed_batch;
            }
            if (!require_resource(handle, type_video_drawing) ||
                (drawing.player_handle != 0U &&
                 !require_resource(
                     drawing.player_handle,
                     type_media_player)) ||
                (drawing.rect_animation_handle != 0U &&
                 !require_resource(
                     drawing.rect_animation_handle,
                     type_rect_resource))) {
                return status::invalid_handle;
            }
            if (!finite_double_as_float(drawing.x) ||
                !finite_double_as_float(drawing.y) ||
                !finite_double_as_float(drawing.width) ||
                !finite_double_as_float(drawing.height) ||
                drawing.width < 0.0 || drawing.height < 0.0) {
                return status::malformed_batch;
            }
            video_drawings.insert_or_assign(handle, drawing);
            increment_generation(handle);
            ++metrics.updated_resource_count;
            return status::success;
        }
        case command::drawing_image: {
            using layout = command_layouts::drawing_image;
            std::uint32_t drawing_handle = 0U;
            if (!has_exact_size(view, layout::fixed_size) ||
                !read_at(view.packet, layout::handle_offset, handle) ||
                !read_at(
                    view.packet,
                    layout::h_drawing_offset,
                    drawing_handle)) {
                return status::malformed_batch;
            }
            const auto drawing = resources.find(drawing_handle);
            if (!require_resource(handle, type_drawing_image) ||
                (drawing_handle != 0U &&
                 (drawing == resources.end() ||
                  !is_drawing_type(drawing->second.type)))) {
                return status::invalid_handle;
            }
            drawing_image_state image{};
            const auto previous = drawing_images.find(handle);
            if (previous != drawing_images.end() &&
                previous->second.has_bounds) {
                image.bounds_x = previous->second.bounds_x;
                image.bounds_y = previous->second.bounds_y;
                image.bounds_width = previous->second.bounds_width;
                image.bounds_height = previous->second.bounds_height;
                image.has_bounds = true;
            }
            image.drawing_handle = drawing_handle;
            if (drawing_handle != 0U) {
                try {
                    image.child_render_data.resize(16U);
                } catch (const std::bad_alloc&) {
                    return status::capacity_exceeded;
                }
                constexpr std::uint32_t record_size = 16U;
                constexpr std::uint32_t draw_command =
                    static_cast<std::uint32_t>(command::draw_drawing);
                constexpr std::uint32_t padding = 0U;
                std::memcpy(image.child_render_data.data(),
                    &record_size, sizeof(record_size));
                std::memcpy(image.child_render_data.data() + 4U,
                    &draw_command, sizeof(draw_command));
                std::memcpy(image.child_render_data.data() + 8U,
                    &drawing_handle, sizeof(drawing_handle));
                std::memcpy(image.child_render_data.data() + 12U,
                    &padding, sizeof(padding));
            }
            drawing_images.insert_or_assign(handle, std::move(image));
            increment_generation(handle);
            ++metrics.updated_resource_count;
            return status::success;
        }
        case command::guideline_set: {
            using layout = command_layouts::guideline_set;
            std::uint32_t guidelines_x_size = 0U;
            std::uint32_t guidelines_y_size = 0U;
            std::uint32_t is_dynamic = 0U;
            if (view.packet.size() < layout::fixed_size ||
                !read_at(view.packet, layout::handle_offset, handle) ||
                !read_at(
                    view.packet,
                    layout::guidelines_x_size_offset,
                    guidelines_x_size) ||
                !read_at(
                    view.packet,
                    layout::guidelines_y_size_offset,
                    guidelines_y_size) ||
                !read_at(
                    view.packet,
                    layout::is_dynamic_offset,
                    is_dynamic) ||
                guidelines_x_size % sizeof(double) != 0U ||
                guidelines_y_size % sizeof(double) != 0U ||
                static_cast<std::uint64_t>(guidelines_x_size) +
                        guidelines_y_size !=
                    view.packet.size() - layout::fixed_size ||
                guidelines_x_size / sizeof(double) >
                    std::numeric_limits<std::uint16_t>::max() ||
                guidelines_y_size / sizeof(double) >
                    std::numeric_limits<std::uint16_t>::max() ||
                is_dynamic > 1U ||
                (is_dynamic != 0U &&
                    ((guidelines_x_size / sizeof(double)) % 2U != 0U ||
                     (guidelines_y_size / sizeof(double)) % 2U != 0U))) {
                return status::malformed_batch;
            }
            if (!require_resource(handle, type_guideline_set)) {
                return status::invalid_handle;
            }
            guideline_set_state guidelines{};
            guidelines.is_dynamic = is_dynamic != 0U;
            try {
                guidelines.guidelines_x.resize(
                    guidelines_x_size / sizeof(double));
                guidelines.guidelines_y.resize(
                    guidelines_y_size / sizeof(double));
            } catch (const std::bad_alloc&) {
                return status::capacity_exceeded;
            }
            std::size_t offset = layout::fixed_size;
            for (double& coordinate : guidelines.guidelines_x) {
                if (!read_at(view.packet, offset, coordinate) ||
                    !finite_double_as_float(coordinate)) {
                    return status::malformed_batch;
                }
                offset += sizeof(coordinate);
            }
            for (double& coordinate : guidelines.guidelines_y) {
                if (!read_at(view.packet, offset, coordinate) ||
                    !finite_double_as_float(coordinate)) {
                    return status::malformed_batch;
                }
                offset += sizeof(coordinate);
            }
            if (!guidelines.is_dynamic) {
                std::ranges::sort(guidelines.guidelines_x);
                std::ranges::sort(guidelines.guidelines_y);
            }
            guideline_sets.insert_or_assign(handle, std::move(guidelines));
            increment_generation(handle);
            ++metrics.updated_resource_count;
            return status::success;
        }
        case command::drawing_group: {
            using layout = command_layouts::drawing_group;
            drawing_group_state group{};
            std::uint32_t children_size = 0U;
            if (view.packet.size() < layout::fixed_size ||
                !read_at(view.packet, layout::handle_offset, handle) ||
                !read_at(
                    view.packet,
                    layout::opacity_offset,
                    group.opacity) ||
                !read_at(
                    view.packet,
                    layout::children_size_offset,
                    children_size) ||
                !read_at(
                    view.packet,
                    layout::h_clip_geometry_offset,
                    group.clip_geometry_handle) ||
                !read_at(
                    view.packet,
                    layout::h_opacity_animations_offset,
                    group.opacity_animation_handle) ||
                !read_at(
                    view.packet,
                    layout::h_opacity_mask_offset,
                    group.opacity_mask_handle) ||
                !read_at(
                    view.packet,
                    layout::h_transform_offset,
                    group.transform_handle) ||
                !read_at(
                    view.packet,
                    layout::h_guideline_set_offset,
                    group.guideline_set_handle) ||
                !read_at(
                    view.packet,
                    layout::edge_mode_offset,
                    group.edge_mode) ||
                !read_at(
                    view.packet,
                    layout::bitmap_scaling_mode_offset,
                    group.bitmap_scaling_mode) ||
                !read_at(
                    view.packet,
                    layout::clear_type_hint_offset,
                    group.clear_type_hint) ||
                children_size % sizeof(std::uint32_t) != 0U ||
                static_cast<std::size_t>(children_size) !=
                    view.packet.size() - layout::fixed_size) {
                return status::malformed_batch;
            }
            const auto previous = drawing_groups.find(handle);
            if (previous != drawing_groups.end() &&
                previous->second.has_bounds) {
                group.bounds_x = previous->second.bounds_x;
                group.bounds_y = previous->second.bounds_y;
                group.bounds_width = previous->second.bounds_width;
                group.bounds_height = previous->second.bounds_height;
                group.has_bounds = true;
            }
            if (!require_resource(handle, type_drawing_group) ||
                (group.clip_geometry_handle != 0U &&
                 !require_geometry(group.clip_geometry_handle)) ||
                (group.opacity_animation_handle != 0U &&
                 !require_resource(
                     group.opacity_animation_handle,
                     type_double_resource)) ||
                (group.opacity_mask_handle != 0U &&
                 !require_brush(group.opacity_mask_handle)) ||
                (group.transform_handle != 0U &&
                 !require_transform(group.transform_handle)) ||
                (group.guideline_set_handle != 0U &&
                 !require_resource(
                     group.guideline_set_handle,
                     type_guideline_set))) {
                return status::invalid_handle;
            }
            if (!finite_double_as_float(group.opacity) ||
                group.opacity < 0.0 || group.opacity > 1.0 ||
                group.edge_mode > 1U ||
                group.bitmap_scaling_mode > 3U ||
                group.clear_type_hint > 1U) {
                return status::malformed_batch;
            }
            const std::size_t child_count =
                children_size / sizeof(std::uint32_t);
            if (child_count > maximum_path_record_count) {
                return status::capacity_exceeded;
            }
            try {
                group.children.resize(child_count);
                group.child_render_data.resize(child_count * 16U);
            } catch (const std::bad_alloc&) {
                return status::capacity_exceeded;
            }
            constexpr std::uint32_t child_record_size = 16U;
            constexpr std::uint32_t child_padding = 0U;
            constexpr std::uint32_t child_command =
                static_cast<std::uint32_t>(command::draw_drawing);
            for (std::size_t index = 0U; index < child_count; ++index) {
                std::uint32_t child = 0U;
                if (!read_at(
                        view.packet,
                        layout::fixed_size +
                            index * sizeof(std::uint32_t),
                        child)) {
                    return status::malformed_batch;
                }
                const auto resource = resources.find(child);
                if (child == 0U || resource == resources.end() ||
                    !is_drawing_type(resource->second.type)) {
                    return status::invalid_handle;
                }
                if (drawing_reaches(child, handle)) {
                    return status::invalid_graph;
                }
                group.children[index] = child;
                std::byte* record =
                    group.child_render_data.data() + index * 16U;
                std::memcpy(
                    record,
                    &child_record_size,
                    sizeof(child_record_size));
                std::memcpy(
                    record + 4U,
                    &child_command,
                    sizeof(child_command));
                std::memcpy(record + 8U, &child, sizeof(child));
                std::memcpy(
                    record + 12U,
                    &child_padding,
                    sizeof(child_padding));
            }
            drawing_groups.insert_or_assign(handle, std::move(group));
            increment_generation(handle);
            ++metrics.updated_resource_count;
            return status::success;
        }
        case command::bitmap_cache: {
            using layout = command_layouts::bitmap_cache;
            bitmap_cache_state cache{};
            std::uint32_t snaps_to_device_pixels = 0U;
            std::uint32_t enable_clear_type = 0U;
            if (!has_exact_size(view, layout::fixed_size) ||
                !read_at(view.packet, layout::handle_offset, handle) ||
                !read_at(
                    view.packet,
                    layout::render_at_scale_offset,
                    cache.render_at_scale) ||
                !read_at(
                    view.packet,
                    layout::h_render_at_scale_animations_offset,
                    cache.render_at_scale_animation_handle) ||
                !read_at(
                    view.packet,
                    layout::snaps_to_device_pixels_offset,
                    snaps_to_device_pixels) ||
                !read_at(
                    view.packet,
                    layout::enable_clear_type_offset,
                    enable_clear_type)) {
                return status::malformed_batch;
            }
            if (!require_resource(handle, type_bitmap_cache) ||
                (cache.render_at_scale_animation_handle != 0U &&
                 !require_resource(
                     cache.render_at_scale_animation_handle,
                     type_double_resource))) {
                return status::invalid_handle;
            }
            if (!std::isfinite(cache.render_at_scale) ||
                snaps_to_device_pixels > 1U || enable_clear_type > 1U) {
                return status::malformed_batch;
            }
            cache.snaps_to_device_pixels = snaps_to_device_pixels != 0U;
            cache.enable_clear_type = enable_clear_type != 0U;
            bitmap_caches.insert_or_assign(handle, cache);
            increment_generation(handle);
            ++metrics.updated_resource_count;
            return status::success;
        }
        default:
            ++metrics.unsupported_command_count;
            return status::unsupported_command;
        }
    }


    struct shallow_fill_leaf {
        std::size_t segment_offset{};
        std::size_t segment_count{};
        double left{};
        double top{};
        double right{};
        double bottom{};
        std::uint32_t fill_rule{PROGPU_NATIVE_FILL_RULE_NON_ZERO};
        bool has_bounds{};
    };

    struct glyph_scene_resource {
        std::uint32_t resource_index{PROGPU_NATIVE_SCENE_NO_INDEX};
        std::uint16_t units_per_em{};
        std::unordered_map<std::uint64_t, std::uint32_t> outline_indices;
    };

    struct glyph_placement {
        std::uint32_t subpixel_index{};
        progpu_native_point position{};
    };

    static std::uint64_t glyph_outline_key(
        std::uint16_t glyph_index,
        std::uint32_t subpixel_index) noexcept {
        return static_cast<std::uint64_t>(glyph_index) << 32U |
            subpixel_index;
    }

    static progpu_native_point transform_affine_point(
        const progpu_native_point& point,
        const affine_2d_double& transform) noexcept {
        return {
            static_cast<float>(
                point.x * transform.m11 + point.y * transform.m21 +
                transform.m31),
            static_cast<float>(
                point.x * transform.m12 + point.y * transform.m22 +
                transform.m32)};
    }

    static glyph_placement resolve_glyph_placement(
        const progpu_native_point& position,
        const affine_2d_double& transform,
        float target_raster_size,
        std::uint32_t text_hinting_mode) noexcept {
        constexpr double transformed_epsilon = 0.0001;
        const bool transformed_placement =
            std::abs(transform.m12) > transformed_epsilon ||
            std::abs(transform.m21) > transformed_epsilon ||
            transform.m11 < 0.0 || transform.m22 < 0.0;
        if (text_hinting_mode == 2U || transformed_placement) {
            return {0U, position};
        }
        affine_2d_double inverse{};
        if (!try_invert_affine(transform, inverse)) {
            return {0U, position};
        }
        progpu_native_point world = transform_affine_point(
            position, transform);
        std::uint32_t phase = 0U;
        if (target_raster_size <= 24.0F) {
            double integer_x = std::floor(world.x);
            auto rounded_phase = static_cast<std::int32_t>(
                std::nearbyint((world.x - integer_x) * 4.0));
            if (rounded_phase == 4) {
                rounded_phase = 0;
                integer_x += 1.0;
            }
            phase = static_cast<std::uint32_t>(rounded_phase);
            world.x = static_cast<float>(integer_x);
        } else {
            world.x = static_cast<float>(std::nearbyint(world.x));
        }
        world.y = static_cast<float>(std::nearbyint(world.y));
        return {phase, transform_affine_point(world, inverse)};
    }

    static bool try_get_affine_scale(
        const affine_2d_double& transform,
        float& scale) noexcept {
        const double metric_xx = transform.m11 * transform.m11 +
            transform.m12 * transform.m12;
        const double metric_xy = transform.m11 * transform.m21 +
            transform.m12 * transform.m22;
        const double metric_yy = transform.m21 * transform.m21 +
            transform.m22 * transform.m22;
        const double half_difference = (metric_xx - metric_yy) * 0.5;
        const double maximum_eigenvalue =
            (metric_xx + metric_yy) * 0.5 +
            std::hypot(half_difference, metric_xy);
        if (!std::isfinite(maximum_eigenvalue) ||
            maximum_eigenvalue <= 0.0) {
            return false;
        }
        const double result = std::sqrt(maximum_eigenvalue);
        if (!finite_double_as_float(result) || result <= 0.0) {
            return false;
        }
        scale = static_cast<float>(result);
        return true;
    }

    status append_glyph_run(
        std::uint32_t glyph_run_handle,
        std::uint32_t foreground_brush_handle,
        const render_scope_state& current,
        native::semantic_scene_builder& builder,
        std::unordered_map<std::uint64_t, glyph_scene_resource>&
            glyph_resources) const {
        if (glyph_run_handle == 0U || foreground_brush_handle == 0U) {
            return status::success;
        }
        const auto glyph_run_entry = glyph_runs.find(glyph_run_handle);
        if (glyph_run_entry == glyph_runs.end()) {
            return status::invalid_handle;
        }
        progpu_native_color text_color{};
        double text_opacity = 0.0;
        const status brush_status = resolve_solid_brush(
            foreground_brush_handle,
            text_color,
            text_opacity);
        if (brush_status != status::success) {
            return brush_status;
        }
        const auto& glyph_run = glyph_run_entry->second;
        if ((glyph_run.flags & 0x0001U) != 0U) {
            return status::unsupported_command;
        }
        if (!glyph_run.font_data || glyph_run.font_data->empty() ||
            glyph_run.glyph_indices.empty() ||
            glyph_run.glyph_indices.size() != glyph_run.advances.size() ||
            (!glyph_run.offsets.empty() &&
             glyph_run.offsets.size() != glyph_run.glyph_indices.size())) {
            return status::invalid_handle;
        }
        float transform_scale = 0.0F;
        if (!try_get_affine_scale(current.transform, transform_scale)) {
            return status::invalid_graph;
        }
        const float target_raster_size = std::clamp(
            glyph_run.em_size * transform_scale, 4.0F, 128.0F);
        const std::uint64_t cache_key =
            static_cast<std::uint64_t>(glyph_run_handle) << 32U |
            std::bit_cast<std::uint32_t>(target_raster_size);
        auto cached = glyph_resources.find(cache_key);
        if (cached == glyph_resources.end()) {
            text::sfnt_font_view font{};
            text::font_error font_error = text::font_error::none;
            if (!text::sfnt_font_view::try_create(
                    *glyph_run.font_data,
                    glyph_run.face_index,
                    font,
                    &font_error)) {
                return status::invalid_graph;
            }
            text::sfnt_header_metrics header{};
            if (!font.try_get_header_metrics(header) ||
                header.units_per_em == 0U) {
                return status::invalid_graph;
            }
            glyph_scene_resource scene_resource{};
            scene_resource.units_per_em = header.units_per_em;
            std::vector<progpu_native_scene_glyph_outline> outlines;
            std::vector<progpu_native_path_segment> segments;
            outlines.reserve(glyph_run.glyph_indices.size() * 4U);
            scene_resource.outline_indices.reserve(
                glyph_run.glyph_indices.size() * 4U);
            for (const std::uint16_t glyph_index :
                 glyph_run.glyph_indices) {
                if (scene_resource.outline_indices.contains(
                        glyph_outline_key(glyph_index, 0U))) {
                    continue;
                }
                text::sfnt_glyph_data_view glyph_data{};
                if (!font.try_get_glyph_data(glyph_index, glyph_data)) {
                    return status::unsupported_command;
                }
                if (glyph_data.empty()) {
                    continue;
                }
                text::sfnt_expanded_glyph_requirements requirements{};
                if (!font.try_get_expanded_glyph_requirements(
                        glyph_index, requirements, &font_error)) {
                    return status::unsupported_command;
                }
                if (requirements.path_segment_count == 0U) {
                    continue;
                }
                std::vector<std::uint16_t> contour_scratch(
                    requirements.simple_contour_scratch_count);
                std::vector<text::sfnt_outline_point> point_scratch(
                    requirements.simple_point_scratch_count);
                std::vector<progpu_native_point> points(
                    requirements.point_count);
                const std::size_t segment_offset = segments.size();
                segments.resize(
                    segment_offset + requirements.path_segment_count);
                std::uint32_t points_written = 0U;
                std::uint32_t segments_written = 0U;
                if (!font.try_decode_glyph_outline(
                        glyph_index,
                        contour_scratch,
                        point_scratch,
                        points,
                        std::span(segments).subspan(
                            segment_offset,
                            requirements.path_segment_count),
                        points_written,
                        segments_written,
                        &font_error) ||
                    points_written != requirements.point_count ||
                    segments_written != requirements.path_segment_count ||
                    glyph_data.x_max <= glyph_data.x_min ||
                    glyph_data.y_max <= glyph_data.y_min) {
                    segments.resize(segment_offset);
                    return status::unsupported_command;
                }
                for (std::uint32_t phase = 0U; phase < 4U; ++phase) {
                    const auto outline_index = static_cast<std::uint32_t>(
                        outlines.size());
                    scene_resource.outline_indices.emplace(
                        glyph_outline_key(glyph_index, phase),
                        outline_index);
                    outlines.push_back({
                        segment_offset,
                        segments_written,
                        static_cast<float>(glyph_data.x_min),
                        static_cast<float>(glyph_data.y_min),
                        static_cast<float>(glyph_data.x_max),
                        static_cast<float>(glyph_data.y_max),
                        target_raster_size /
                            static_cast<float>(header.units_per_em),
                        phase * 0.25F});
                }
            }
            if (!outlines.empty() &&
                !builder.add_glyph_outlines(
                    outlines,
                    segments,
                    scene_resource.resource_index)) {
                return status::invalid_graph;
            }
            cached = glyph_resources.emplace(
                cache_key, std::move(scene_resource)).first;
        }

        const auto& scene_resource = cached->second;
        if (scene_resource.resource_index ==
            PROGPU_NATIVE_SCENE_NO_INDEX) {
            return status::success;
        }
        const bool bold = (glyph_run.style_simulations & 0x01U) != 0U;
        const bool italic = (glyph_run.style_simulations & 0x02U) != 0U;
        std::vector<progpu_native_positioned_glyph> positioned;
        positioned.reserve(
            glyph_run.glyph_indices.size() * (bold ? 2U : 1U));
        float cursor_x = 0.0F;
        float cursor_y = 0.0F;
        for (std::size_t index = 0U;
             index < glyph_run.glyph_indices.size();
             ++index) {
            const progpu_native_point offset = glyph_run.offsets.empty()
                ? progpu_native_point{}
                : glyph_run.offsets[index];
            const progpu_native_point source_position{
                glyph_run.origin_x + cursor_x + offset.x,
                glyph_run.origin_y + cursor_y + offset.y};
            const glyph_placement placement = resolve_glyph_placement(
                source_position,
                current.transform,
                target_raster_size,
                current.text_hinting_mode);
            const auto outline = scene_resource.outline_indices.find(
                glyph_outline_key(
                    glyph_run.glyph_indices[index],
                    placement.subpixel_index));
            if (outline != scene_resource.outline_indices.end()) {
                const std::uint32_t pass_count = bold ? 2U : 1U;
                for (std::uint32_t pass = 0U;
                     pass < pass_count;
                     ++pass) {
                    positioned.push_back({
                        outline->second,
                        0U,
                        placement.position,
                        {1.0F, 0.0F},
                        {0.0F, 1.0F},
                        {1.0F, 1.0F, 1.0F, 1.0F},
                        glyph_run.em_size / target_raster_size,
                        pass * glyph_run.em_size * 0.035F,
                        italic ? 0.22F : 0.0F,
                        0.0F});
                }
            }
            cursor_x += glyph_run.advances[index];
        }
        if (positioned.empty()) {
            return status::success;
        }
        text_color.a *= static_cast<float>(text_opacity);
        const std::uint32_t text_rendering_mode =
            current.text_rendering_mode == 1U
            ? PROGPU_NATIVE_SCENE_TEXT_ALIASED
            : !current.subpixel_text_disabled &&
                (current.text_rendering_mode == 3U ||
                (current.text_rendering_mode == 0U &&
                 current.clear_type_enabled))
            ? PROGPU_NATIVE_SCENE_TEXT_CLEARTYPE
            : PROGPU_NATIVE_SCENE_TEXT_GRAYSCALE;
        const progpu_native_scene_text_style style{
            text_color,
            text_rendering_mode,
            0U,
            0U,
            0U};
        std::uint32_t style_index = PROGPU_NATIVE_SCENE_NO_INDEX;
        if (!builder.add_text_style(style, style_index)) {
            return status::invalid_graph;
        }
        progpu_native_image_rect bounds{};
        if (!try_transform_bounds(
                glyph_run.bounds_x,
                glyph_run.bounds_y,
                glyph_run.bounds_width,
                glyph_run.bounds_height,
                current.transform,
                bounds)) {
            return status::invalid_graph;
        }
        if (bounds.width == 0.0F || bounds.height == 0.0F) {
            const double fallback_width = std::max(
                double{glyph_run.em_size},
                std::abs(double{cursor_x}) + glyph_run.em_size * 2.0);
            if (!try_transform_bounds(
                    glyph_run.origin_x - glyph_run.em_size,
                    glyph_run.origin_y - glyph_run.em_size * 2.0F,
                    fallback_width,
                    glyph_run.em_size * 3.0F,
                    current.transform,
                    bounds)) {
                return status::invalid_graph;
            }
        }
        return builder.draw_glyph_run(
                scene_resource.resource_index,
                positioned,
                bounds,
                PROGPU_NATIVE_SCENE_NO_INDEX,
                style_index)
            ? status::success
            : status::invalid_graph;
    }

    status append_shallow_fill_leaf(
        std::uint32_t geometry_handle,
        std::vector<progpu_native_path_segment>& segments,
        shallow_fill_leaf& leaf,
        affine_2d_double parent_transform = {},
        bool per_point_guidelines = false) const {
        leaf = {};
        leaf.segment_offset = segments.size();
        const auto path = path_geometries.find(geometry_handle);
        if (path != path_geometries.end()) {
            if (path->second.segments.empty()) {
                return status::success;
            }
            if (per_point_guidelines &&
                !path->second.per_point_segments_supported) {
                return status::unsupported_command;
            }
            const auto& source_segments = per_point_guidelines &&
                    !path->second.per_point_segments.empty()
                ? path->second.per_point_segments
                : path->second.segments;
            affine_2d_double transform = parent_transform;
            if (path->second.transform_handle != 0U) {
                affine_2d_double local_transform{};
                const status transform_status = resolve_transform(
                    path->second.transform_handle,
                    local_transform);
                if (transform_status != status::success) {
                    return transform_status;
                }
                transform = compose_affine(
                    local_transform,
                    parent_transform);
            }
            if (affine_has_zero_area(transform)) {
                return status::success;
            }
            const bool transform_is_identity =
                transform.m11 == 1.0 && transform.m12 == 0.0 &&
                transform.m21 == 0.0 && transform.m22 == 1.0 &&
                transform.m31 == 0.0 && transform.m32 == 0.0;
            const std::size_t original_size = segments.size();
            if (transform_is_identity) {
                segments.insert(
                    segments.end(),
                    source_segments.begin(),
                    source_segments.end());
            } else {
                const auto map_point = [&transform](
                    progpu_native_point& point) noexcept {
                    const double mapped_x =
                        point.x * transform.m11 +
                        point.y * transform.m21 +
                        transform.m31;
                    const double mapped_y =
                        point.x * transform.m12 +
                        point.y * transform.m22 +
                        transform.m32;
                    if (!finite_double_as_float(mapped_x) ||
                        !finite_double_as_float(mapped_y)) {
                        return false;
                    }
                    point = {
                        static_cast<float>(mapped_x),
                        static_cast<float>(mapped_y)};
                    return true;
                };
                for (const auto& source : source_segments) {
                    if (source.kind == PROGPU_NATIVE_PATH_SEGMENT_ARC) {
                        progpu_native_path_segment transformed_arc{};
                        if (!try_transform_arc_segment(
                                source,
                                transform,
                                transformed_arc)) {
                            segments.resize(original_size);
                            return status::unsupported_command;
                        }
                        segments.push_back(transformed_arc);
                        continue;
                    }
                    auto segment = source;
                    bool mapped = map_point(segment.p0) &&
                        map_point(segment.p1);
                    if (segment.kind ==
                            PROGPU_NATIVE_PATH_SEGMENT_QUADRATIC ||
                        segment.kind == PROGPU_NATIVE_PATH_SEGMENT_CUBIC) {
                        mapped = mapped && map_point(segment.p2);
                    }
                    if (segment.kind ==
                        PROGPU_NATIVE_PATH_SEGMENT_CUBIC) {
                        mapped = mapped && map_point(segment.p3);
                    }
                    if (!mapped) {
                        segments.resize(original_size);
                        return status::invalid_graph;
                    }
                    segments.push_back(segment);
                }
            }
            progpu_native_image_rect bounds{};
            if (!try_get_path_segment_bounds(
                    std::span<const progpu_native_path_segment>{segments}
                        .subspan(original_size),
                    bounds)) {
                segments.resize(original_size);
                return status::success;
            }
            leaf.left = bounds.x;
            leaf.top = bounds.y;
            leaf.right = bounds.x + bounds.width;
            leaf.bottom = bounds.y + bounds.height;
            leaf.segment_count = source_segments.size();
            leaf.fill_rule = path->second.fill_rule == 0U
                ? PROGPU_NATIVE_FILL_RULE_EVEN_ODD
                : PROGPU_NATIVE_FILL_RULE_NON_ZERO;
            leaf.has_bounds = true;
            return status::success;
        }

        const auto fixed = fixed_geometries.find(geometry_handle);
        if (fixed == fixed_geometries.end()) {
            return status::unsupported_command;
        }
        fixed_geometry_state resolved_fixed{};
        const status resolved_fixed_status = resolve_fixed_geometry(
            geometry_handle, resolved_fixed);
        if (resolved_fixed_status != status::success) {
            return resolved_fixed_status;
        }
        if (resolved_fixed.kind == fixed_geometry_kind::line) {
            return status::success;
        }
        affine_2d_double transform = parent_transform;
        if (resolved_fixed.transform_handle != 0U) {
            affine_2d_double local_transform{};
            const status transform_status = resolve_transform(
                resolved_fixed.transform_handle,
                local_transform);
            if (transform_status != status::success) {
                return transform_status;
            }
            transform = compose_affine(
                local_transform,
                parent_transform);
        }
        if (affine_has_zero_area(transform)) {
            return status::success;
        }
        const std::size_t original_size = segments.size();
        const auto include_point = [&leaf](progpu_native_point point) noexcept {
            if (!leaf.has_bounds) {
                leaf.left = point.x;
                leaf.top = point.y;
                leaf.right = point.x;
                leaf.bottom = point.y;
                leaf.has_bounds = true;
                return;
            }
            leaf.left = std::min(leaf.left, double{point.x});
            leaf.top = std::min(leaf.top, double{point.y});
            leaf.right = std::max(leaf.right, double{point.x});
            leaf.bottom = std::max(leaf.bottom, double{point.y});
        };
        const auto map_point = [&transform](
            double x,
            double y,
            progpu_native_point& point) noexcept {
            const double mapped_x =
                x * transform.m11 + y * transform.m21 + transform.m31;
            const double mapped_y =
                x * transform.m12 + y * transform.m22 + transform.m32;
            if (!finite_double_as_float(mapped_x) ||
                !finite_double_as_float(mapped_y)) {
                return false;
            }
            point = {
                static_cast<float>(mapped_x),
                static_cast<float>(mapped_y)};
            return true;
        };
        const auto append_line = [
            &segments,
            &include_point,
            &map_point](
            double x0,
            double y0,
            double x1,
            double y1) {
            progpu_native_path_segment segment{};
            if (!map_point(x0, y0, segment.p0) ||
                !map_point(x1, y1, segment.p1)) {
                return false;
            }
            segment.kind = PROGPU_NATIVE_PATH_SEGMENT_LINE;
            include_point(segment.p0);
            include_point(segment.p1);
            segments.push_back(segment);
            return true;
        };
        const auto append_arc = [
            &segments,
            &include_point,
            &transform](
            double x0,
            double y0,
            double x1,
            double y1,
            double center_x,
            double center_y,
            double radius_x,
            double radius_y,
            float theta1,
            float delta_theta) {
            progpu_native_path_segment source{};
            source.p0 = {static_cast<float>(x0), static_cast<float>(y0)};
            source.p1 = {static_cast<float>(x1), static_cast<float>(y1)};
            source.p2 = {
                static_cast<float>(center_x),
                static_cast<float>(center_y)};
            source.p3 = {
                static_cast<float>(radius_x),
                static_cast<float>(radius_y)};
            source.kind = PROGPU_NATIVE_PATH_SEGMENT_ARC;
            source.pad0 = std::bit_cast<std::uint32_t>(theta1);
            source.pad1 = std::bit_cast<std::uint32_t>(delta_theta);
            source.pad2 = std::bit_cast<std::uint32_t>(0.0F);
            progpu_native_path_segment segment{};
            if (!try_transform_arc_segment(source, transform, segment)) {
                return false;
            }
            include_point(segment.p0);
            include_point(segment.p1);
            segments.push_back(segment);
            return true;
        };
        bool appended = false;
        if (resolved_fixed.kind == fixed_geometry_kind::ellipse) {
            const double center_x = resolved_fixed.first;
            const double center_y = resolved_fixed.second;
            const double radius_x = resolved_fixed.third;
            const double radius_y = resolved_fixed.fourth;
            if (radius_x == 0.0 || radius_y == 0.0) {
                return status::success;
            }
            constexpr float half_pi =
                std::numbers::pi_v<float> * 0.5F;
            appended = append_arc(
                    center_x + radius_x,
                    center_y,
                    center_x,
                    center_y + radius_y,
                    center_x,
                    center_y,
                    radius_x,
                    radius_y,
                    0.0F,
                    half_pi) &&
                append_arc(
                    center_x,
                    center_y + radius_y,
                    center_x - radius_x,
                    center_y,
                    center_x,
                    center_y,
                    radius_x,
                    radius_y,
                    half_pi,
                    half_pi) &&
                append_arc(
                    center_x - radius_x,
                    center_y,
                    center_x,
                    center_y - radius_y,
                    center_x,
                    center_y,
                    radius_x,
                    radius_y,
                    std::numbers::pi_v<float>,
                    half_pi) &&
                append_arc(
                    center_x,
                    center_y - radius_y,
                    center_x + radius_x,
                    center_y,
                    center_x,
                    center_y,
                    radius_x,
                    radius_y,
                    std::numbers::pi_v<float> + half_pi,
                    half_pi);
        } else {
            const double left = resolved_fixed.first;
            const double top = resolved_fixed.second;
            const double right = left + resolved_fixed.third;
            const double bottom = top + resolved_fixed.fourth;
            const double radius_x = std::min(
                resolved_fixed.radius_x,
                resolved_fixed.third * 0.5);
            const double radius_y = std::min(
                resolved_fixed.radius_y,
                resolved_fixed.fourth * 0.5);
            if (radius_x == 0.0 || radius_y == 0.0) {
                appended = append_line(left, top, right, top) &&
                    append_line(right, top, right, bottom) &&
                    append_line(right, bottom, left, bottom) &&
                    append_line(left, bottom, left, top);
            } else {
                constexpr float half_pi =
                    std::numbers::pi_v<float> * 0.5F;
                appended = append_arc(
                        left,
                        top + radius_y,
                        left + radius_x,
                        top,
                        left + radius_x,
                        top + radius_y,
                        radius_x,
                        radius_y,
                        std::numbers::pi_v<float>,
                        half_pi) &&
                    append_line(
                        left + radius_x,
                        top,
                        right - radius_x,
                        top) &&
                    append_arc(
                        right - radius_x,
                        top,
                        right,
                        top + radius_y,
                        right - radius_x,
                        top + radius_y,
                        radius_x,
                        radius_y,
                        std::numbers::pi_v<float> + half_pi,
                        half_pi) &&
                    append_line(
                        right,
                        top + radius_y,
                        right,
                        bottom - radius_y) &&
                    append_arc(
                        right,
                        bottom - radius_y,
                        right - radius_x,
                        bottom,
                        right - radius_x,
                        bottom - radius_y,
                        radius_x,
                        radius_y,
                        0.0F,
                        half_pi) &&
                    append_line(
                        right - radius_x,
                        bottom,
                        left + radius_x,
                        bottom) &&
                    append_arc(
                        left + radius_x,
                        bottom,
                        left,
                        bottom - radius_y,
                        left + radius_x,
                        bottom - radius_y,
                        radius_x,
                        radius_y,
                        half_pi,
                        half_pi) &&
                    append_line(
                        left,
                        bottom - radius_y,
                        left,
                        top + radius_y);
            }
        }
        if (!appended) {
            segments.resize(original_size);
            leaf = {};
            leaf.segment_offset = original_size;
            return status::invalid_graph;
        }
        progpu_native_image_rect transformed_bounds{};
        if (!try_get_path_segment_bounds(
                std::span<const progpu_native_path_segment>{segments}
                    .subspan(original_size),
                transformed_bounds)) {
            segments.resize(original_size);
            leaf = {};
            leaf.segment_offset = original_size;
            return status::invalid_graph;
        }
        leaf.left = transformed_bounds.x;
        leaf.top = transformed_bounds.y;
        leaf.right = transformed_bounds.x + transformed_bounds.width;
        leaf.bottom = transformed_bounds.y + transformed_bounds.height;
        leaf.has_bounds = true;
        leaf.segment_count = segments.size() - original_size;
        return status::success;
    }

    status append_group_fill_leaf(
        std::uint32_t geometry_handle,
        std::vector<progpu_native_path_segment>& segments,
        shallow_fill_leaf& leaf,
        affine_2d_double parent_transform = {},
        std::uint32_t depth = 1U,
        bool per_point_guidelines = false) const {
        const auto group = geometry_groups.find(geometry_handle);
        if (group == geometry_groups.end()) {
            return append_shallow_fill_leaf(
                geometry_handle,
                segments,
                leaf,
                parent_transform,
                per_point_guidelines);
        }
        if (depth == 0U || depth > maximum_visual_depth) {
            return status::invalid_graph;
        }
        affine_2d_double transform = parent_transform;
        if (group->second.transform_handle != 0U) {
            affine_2d_double local_transform{};
            const status transform_status = resolve_transform(
                group->second.transform_handle,
                local_transform);
            if (transform_status != status::success) {
                return transform_status;
            }
            transform = compose_affine(local_transform, parent_transform);
        }
        const std::size_t original_size = segments.size();
        leaf = {};
        leaf.segment_offset = original_size;
        leaf.fill_rule = group->second.fill_rule == 0U
            ? PROGPU_NATIVE_FILL_RULE_EVEN_ODD
            : PROGPU_NATIVE_FILL_RULE_NON_ZERO;
        if (affine_has_zero_area(transform)) {
            return status::success;
        }
        for (const std::uint32_t child_handle : group->second.children) {
            shallow_fill_leaf child{};
            const status child_status = append_group_fill_leaf(
                child_handle,
                segments,
                child,
                transform,
                depth + 1U,
                per_point_guidelines);
            if (child_status != status::success) {
                segments.resize(original_size);
                leaf = {};
                leaf.segment_offset = original_size;
                return child_status;
            }
            if (!child.has_bounds) {
                continue;
            }
            if (!leaf.has_bounds) {
                leaf.left = child.left;
                leaf.top = child.top;
                leaf.right = child.right;
                leaf.bottom = child.bottom;
                leaf.has_bounds = true;
            } else {
                leaf.left = std::min(leaf.left, child.left);
                leaf.top = std::min(leaf.top, child.top);
                leaf.right = std::max(leaf.right, child.right);
                leaf.bottom = std::max(leaf.bottom, child.bottom);
            }
        }
        leaf.segment_count = segments.size() - original_size;
        return status::success;
    }

    status append_group_winding_program(
        std::uint32_t geometry_handle,
        std::uint32_t root_fill_rule,
        std::vector<progpu_native_path_segment>& segments,
        std::vector<progpu_native_scene_path_boolean_node>& nodes,
        shallow_fill_leaf& tree,
        affine_2d_double parent_transform = {},
        std::uint32_t depth = 1U,
        bool per_point_guidelines = false,
        bool apply_group_transform = true) const {
        const std::size_t original_segment_size = segments.size();
        const std::size_t original_node_size = nodes.size();
        tree = {};
        tree.segment_offset = original_segment_size;
        tree.fill_rule = root_fill_rule;
        if (depth == 0U || depth > maximum_visual_depth ||
            root_fill_rule > PROGPU_NATIVE_FILL_RULE_EVEN_ODD) {
            return status::invalid_graph;
        }
        const auto group = geometry_groups.find(geometry_handle);
        if (group == geometry_groups.end() ||
            group->second.children.size() > 32U) {
            return status::unsupported_command;
        }

        affine_2d_double transform = parent_transform;
        if (apply_group_transform &&
            group->second.transform_handle != 0U) {
            affine_2d_double local_transform{};
            const status transform_status = resolve_transform(
                group->second.transform_handle,
                local_transform);
            if (transform_status != status::success) {
                return transform_status;
            }
            transform = compose_affine(local_transform, parent_transform);
        }
        if (affine_has_zero_area(transform)) {
            progpu_native_scene_path_boolean_node empty{};
            empty.kind = PROGPU_NATIVE_PATH_BOOLEAN_EMPTY;
            nodes.push_back(empty);
            return status::success;
        }

        std::size_t appended_child_count = 0U;
        for (const std::uint32_t child_handle : group->second.children) {
            const std::size_t child_segment_offset = segments.size();
            const std::size_t child_node_offset = nodes.size();
            shallow_fill_leaf child{};
            status child_status = status::unsupported_command;
            const auto child_group = geometry_groups.find(child_handle);
            if (child_group != geometry_groups.end()) {
                child_status = append_group_winding_program(
                    child_handle,
                    root_fill_rule,
                    segments,
                    nodes,
                    child,
                    transform,
                    depth + 1U,
                    per_point_guidelines,
                    true);
            } else {
                const auto child_combined =
                    combined_geometries.find(child_handle);
                if (child_combined != combined_geometries.end()) {
                    child_status = append_boolean_geometry(
                        child_handle,
                        segments,
                        nodes,
                        child,
                        transform,
                        depth + 1U,
                        per_point_guidelines);
                    const double determinant =
                        transform.m11 * transform.m22 -
                        transform.m12 * transform.m21;
                    if (child_status == status::success &&
                        child.has_bounds &&
                        root_fill_rule ==
                            PROGPU_NATIVE_FILL_RULE_NON_ZERO &&
                        determinant < 0.0) {
                        progpu_native_scene_path_boolean_node negate{};
                        negate.kind =
                            PROGPU_NATIVE_PATH_BOOLEAN_WINDING_NEGATE;
                        nodes.push_back(negate);
                    }
                } else {
                    child_status = append_shallow_fill_leaf(
                        child_handle,
                        segments,
                        child,
                        transform,
                        per_point_guidelines);
                    if (child_status == status::success &&
                        child.has_bounds) {
                        nodes.push_back({
                            child.segment_offset,
                            child.segment_count,
                            static_cast<float>(child.left),
                            static_cast<float>(child.top),
                            static_cast<float>(child.right),
                            static_cast<float>(child.bottom),
                            static_cast<std::uint32_t>(
                                root_fill_rule ==
                                        PROGPU_NATIVE_FILL_RULE_EVEN_ODD
                                    ? PROGPU_NATIVE_FILL_RULE_EVEN_ODD
                                    : PROGPU_NATIVE_FILL_RULE_NON_ZERO),
                            static_cast<std::uint32_t>(
                                root_fill_rule ==
                                        PROGPU_NATIVE_FILL_RULE_EVEN_ODD
                                    ? PROGPU_NATIVE_PATH_BOOLEAN_LEAF
                                    : PROGPU_NATIVE_PATH_BOOLEAN_WINDING_LEAF),
                            0U,
                            0U});
                    }
                }
            }
            if (child_status != status::success) {
                segments.resize(original_segment_size);
                nodes.resize(original_node_size);
                tree = {};
                tree.segment_offset = original_segment_size;
                return child_status;
            }
            if (!child.has_bounds) {
                segments.resize(child_segment_offset);
                nodes.resize(child_node_offset);
                continue;
            }
            if (!tree.has_bounds) {
                tree.left = child.left;
                tree.top = child.top;
                tree.right = child.right;
                tree.bottom = child.bottom;
                tree.has_bounds = true;
            } else {
                tree.left = std::min(tree.left, child.left);
                tree.top = std::min(tree.top, child.top);
                tree.right = std::max(tree.right, child.right);
                tree.bottom = std::max(tree.bottom, child.bottom);
            }
            if (appended_child_count != 0U) {
                progpu_native_scene_path_boolean_node operation{};
                operation.kind = root_fill_rule ==
                        PROGPU_NATIVE_FILL_RULE_EVEN_ODD
                    ? PROGPU_NATIVE_PATH_BOOLEAN_XOR
                    : PROGPU_NATIVE_PATH_BOOLEAN_WINDING_ADD;
                nodes.push_back(operation);
            }
            ++appended_child_count;
            if (nodes.size() - original_node_size > 63U) {
                segments.resize(original_segment_size);
                nodes.resize(original_node_size);
                tree = {};
                tree.segment_offset = original_segment_size;
                return status::unsupported_command;
            }
        }
        if (appended_child_count == 0U) {
            progpu_native_scene_path_boolean_node empty{};
            empty.kind = PROGPU_NATIVE_PATH_BOOLEAN_EMPTY;
            nodes.push_back(empty);
        }
        tree.segment_count = segments.size() - original_segment_size;
        return status::success;
    }

    status append_boolean_geometry(
        std::uint32_t geometry_handle,
        std::vector<progpu_native_path_segment>& segments,
        std::vector<progpu_native_scene_path_boolean_node>& nodes,
        shallow_fill_leaf& tree,
        affine_2d_double parent_transform = {},
        std::uint32_t depth = 1U,
        bool per_point_guidelines = false,
        bool expand_root_group = false) const {
        if (depth == 0U || depth > maximum_visual_depth) {
            return status::invalid_graph;
        }
        const std::size_t original_segment_size = segments.size();
        const std::size_t original_node_size = nodes.size();
        tree = {};
        tree.segment_offset = original_segment_size;
        if (geometry_handle == 0U) {
            progpu_native_scene_path_boolean_node empty{};
            empty.kind = PROGPU_NATIVE_PATH_BOOLEAN_EMPTY;
            nodes.push_back(empty);
            return status::success;
        }

        const auto group = geometry_groups.find(geometry_handle);
        if (!expand_root_group || group == geometry_groups.end()) {
            const status shallow_status = append_group_fill_leaf(
                geometry_handle,
                segments,
                tree,
                parent_transform,
                depth,
                per_point_guidelines);
            if (shallow_status == status::success) {
                progpu_native_scene_path_boolean_node leaf{};
                if (!tree.has_bounds) {
                    leaf.kind = PROGPU_NATIVE_PATH_BOOLEAN_EMPTY;
                } else {
                    leaf.segment_offset = tree.segment_offset;
                    leaf.segment_count = tree.segment_count;
                    leaf.min_x = static_cast<float>(tree.left);
                    leaf.min_y = static_cast<float>(tree.top);
                    leaf.max_x = static_cast<float>(tree.right);
                    leaf.max_y = static_cast<float>(tree.bottom);
                    leaf.fill_rule = tree.fill_rule;
                    leaf.kind = PROGPU_NATIVE_PATH_BOOLEAN_LEAF;
                }
                nodes.push_back(leaf);
                return status::success;
            }
            segments.resize(original_segment_size);
            nodes.resize(original_node_size);
            tree = {};
            tree.segment_offset = original_segment_size;
            if (shallow_status != status::unsupported_command) {
                return shallow_status;
            }
        }

        if (group != geometry_groups.end()) {
            return append_group_winding_program(
                geometry_handle,
                group->second.fill_rule == 0U
                    ? PROGPU_NATIVE_FILL_RULE_EVEN_ODD
                    : PROGPU_NATIVE_FILL_RULE_NON_ZERO,
                segments,
                nodes,
                tree,
                parent_transform,
                depth,
                per_point_guidelines);
        }

        const auto combined = combined_geometries.find(geometry_handle);
        if (combined == combined_geometries.end()) {
            return status::unsupported_command;
        }

        affine_2d_double transform = parent_transform;
        if (combined->second.transform_handle != 0U) {
            affine_2d_double local_transform{};
            const status transform_status = resolve_transform(
                combined->second.transform_handle,
                local_transform);
            if (transform_status != status::success) {
                return transform_status;
            }
            transform = compose_affine(local_transform, parent_transform);
        }
        if (affine_has_zero_area(transform)) {
            progpu_native_scene_path_boolean_node empty{};
            empty.kind = PROGPU_NATIVE_PATH_BOOLEAN_EMPTY;
            nodes.push_back(empty);
            return status::success;
        }
        const std::array operands{
            combined->second.geometry1_handle,
            combined->second.geometry2_handle};
        for (const std::uint32_t operand_handle : operands) {
            shallow_fill_leaf operand{};
            const status operand_status = append_boolean_geometry(
                operand_handle,
                segments,
                nodes,
                operand,
                transform,
                depth + 1U,
                per_point_guidelines);
            if (operand_status != status::success) {
                segments.resize(original_segment_size);
                nodes.resize(original_node_size);
                tree = {};
                tree.segment_offset = original_segment_size;
                return operand_status;
            }
            if (!operand.has_bounds) {
                continue;
            }
            if (!tree.has_bounds) {
                tree.left = operand.left;
                tree.top = operand.top;
                tree.right = operand.right;
                tree.bottom = operand.bottom;
                tree.has_bounds = true;
            } else {
                tree.left = std::min(tree.left, operand.left);
                tree.top = std::min(tree.top, operand.top);
                tree.right = std::max(tree.right, operand.right);
                tree.bottom = std::max(tree.bottom, operand.bottom);
            }
        }
        progpu_native_scene_path_boolean_node operation{};
        switch (combined->second.combine_mode) {
        case 0U:
            operation.kind = PROGPU_NATIVE_PATH_BOOLEAN_UNION;
            break;
        case 1U:
            operation.kind = PROGPU_NATIVE_PATH_BOOLEAN_INTERSECT;
            break;
        case 2U:
            operation.kind = PROGPU_NATIVE_PATH_BOOLEAN_XOR;
            break;
        case 3U:
            operation.kind = PROGPU_NATIVE_PATH_BOOLEAN_DIFFERENCE;
            break;
        default:
            segments.resize(original_segment_size);
            nodes.resize(original_node_size);
            tree = {};
            tree.segment_offset = original_segment_size;
            return status::invalid_graph;
        }
        nodes.push_back(operation);
        tree.segment_count = segments.size() - original_segment_size;
        return status::success;
    }

    status append_geometry_clip(
        std::uint32_t geometry_handle,
        const affine_2d_double& target_transform,
        render_scope_state& state,
        native::semantic_scene_builder& builder,
        std::vector<progpu_native_scene_clip_path>& clip_paths,
        std::vector<progpu_native_path_segment>& clip_segments,
        std::vector<progpu_native_scene_path_boolean_node>&
            clip_boolean_nodes) const {
        clip_paths.resize(state.clip_path_count);
        clip_segments.resize(state.clip_segment_count);
        clip_boolean_nodes.resize(state.clip_boolean_node_count);
        const std::size_t segment_offset =
            clip_segments.size();
        const std::size_t boolean_node_offset =
            clip_boolean_nodes.size();
        shallow_fill_leaf tree{};
        std::uint32_t fill_rule = PROGPU_NATIVE_FILL_RULE_NON_ZERO;

        const auto combined = combined_geometries.find(geometry_handle);
        const auto group = geometry_groups.find(geometry_handle);
        status append_status = status::success;
        if (combined != combined_geometries.end()) {
            append_status = append_boolean_geometry(
                geometry_handle,
                clip_segments,
                clip_boolean_nodes,
                tree,
                target_transform);
        } else if (group != geometry_groups.end()) {
            fill_rule = group->second.fill_rule == 0U
                ? PROGPU_NATIVE_FILL_RULE_EVEN_ODD
                : PROGPU_NATIVE_FILL_RULE_NON_ZERO;
            if (fill_rule == PROGPU_NATIVE_FILL_RULE_EVEN_ODD) {
                append_status = append_boolean_geometry(
                    geometry_handle,
                    clip_segments,
                    clip_boolean_nodes,
                    tree,
                    target_transform,
                    1U,
                    false,
                    true);
                if (append_status == status::success &&
                    clip_boolean_nodes.size() ==
                        boolean_node_offset + 1U &&
                    clip_boolean_nodes.back().kind ==
                        PROGPU_NATIVE_PATH_BOOLEAN_LEAF) {
                    clip_boolean_nodes.resize(boolean_node_offset);
                }
            } else {
                append_status = append_group_fill_leaf(
                    geometry_handle,
                    clip_segments,
                    tree,
                    target_transform);
                if (append_status == status::unsupported_command) {
                    clip_segments.resize(segment_offset);
                    clip_boolean_nodes.resize(boolean_node_offset);
                    append_status = append_boolean_geometry(
                        geometry_handle,
                        clip_segments,
                        clip_boolean_nodes,
                        tree,
                        target_transform,
                        1U,
                        false,
                        true);
                }
            }
        } else {
            append_status = append_group_fill_leaf(
                geometry_handle,
                clip_segments,
                tree,
                target_transform);
            fill_rule = tree.fill_rule;
        }
        if (append_status != status::success) {
            clip_segments.resize(segment_offset);
            clip_boolean_nodes.resize(boolean_node_offset);
            return append_status;
        }
        if (!tree.has_bounds || tree.segment_count == 0U ||
            tree.right <= tree.left || tree.bottom <= tree.top) {
            clip_segments.resize(segment_offset);
            clip_boolean_nodes.resize(boolean_node_offset);
            state.clip_rect = {};
            state.has_clip = true;
            return status::success;
        }
        const std::size_t boolean_node_count =
            clip_boolean_nodes.size() - boolean_node_offset;
        if (clip_paths.size() >= 64U ||
            boolean_node_count > 63U) {
            clip_segments.resize(segment_offset);
            clip_boolean_nodes.resize(boolean_node_offset);
            return status::unsupported_command;
        }
        clip_paths.push_back({
            segment_offset,
            clip_segments.size() - segment_offset,
            boolean_node_count == 0U ? 0U : boolean_node_offset,
            boolean_node_count,
            static_cast<float>(tree.left),
            static_cast<float>(tree.top),
            static_cast<float>(tree.right),
            static_cast<float>(tree.bottom),
            {1.0F, 0.0F, 0.0F, 1.0F, 0.0F, 0.0F},
            fill_rule,
            8U,
            PROGPU_NATIVE_CLIP_INTERSECT,
            0U});
        std::uint32_t mask_resource_index =
            PROGPU_NATIVE_SCENE_NO_INDEX;
        if (!builder.add_vector_clip_mask(
                clip_paths,
                clip_segments,
                clip_boolean_nodes,
                1.0F,
                mask_resource_index)) {
            clip_paths.pop_back();
            clip_segments.resize(segment_offset);
            clip_boolean_nodes.resize(boolean_node_offset);
            return status::invalid_graph;
        }
        state.clip_path_count = clip_paths.size();
        state.clip_segment_count = clip_segments.size();
        state.clip_boolean_node_count = clip_boolean_nodes.size();
        state.mask_resource_index = mask_resource_index;
        return status::success;
    }

    status append_render_data(
        std::uint32_t content_handle,
        const render_scope_state& base_state,
        native::semantic_scene_builder& builder,
        std::unordered_map<std::uint32_t, std::uint32_t>& brush_indices,
        std::unordered_map<std::uint32_t, std::uint32_t>& image_indices,
        std::unordered_map<std::uint64_t, glyph_scene_resource>&
            glyph_resources,
        scene_compile_context* compile_context,
        std::unordered_set<std::uint32_t>& active_drawings,
        std::vector<progpu_native_scene_clip_path>& clip_paths,
        std::vector<progpu_native_path_segment>& clip_segments,
        std::vector<progpu_native_scene_path_boolean_node>&
            clip_boolean_nodes,
        scene_metrics& metrics) const {
        const auto resource = resources.find(content_handle);
        if (resource == resources.end() ||
            resource->second.type != type_render_data) {
            return status::invalid_handle;
        }

        return append_render_stream(
            resource->second.render_data,
            &resource->second.compact_guidelines,
            base_state,
            1U,
            builder,
            brush_indices,
            image_indices,
            glyph_resources,
            compile_context,
            active_drawings,
            clip_paths,
            clip_segments,
            clip_boolean_nodes,
            metrics);
    }

    // Algorithm: Reuse portable Direct2D boolean/outline processing, then expose
    // its actual closed boundary as native MIL stroke contours. No operand-edge
    // concatenation or CPU pixel mask. Cost is the shared bounded arrangement
    // algorithm plus O(B) retained boundary points; traversal depth is bounded.
    status resolve_combined_stroke_outline(std::uint32_t handle, float tolerance,
        path_geometry_state& output) const {
        namespace d2d = native::direct2d::compat;
        namespace com = native::com;
        const auto convert = [](com::result result) {
            return com::succeeded(result) ? status::success :
                result == com::out_of_memory ? status::capacity_exceeded : status::unsupported_command;
        };
        com::pointer<d2d::factory> factory;
        auto hr = d2d::create_factory(factory.put());
        if (com::failed(hr)) return convert(hr);
        const auto build = [&](auto&& self, std::uint32_t resource, std::uint32_t depth,
            bool root, float target_tolerance, com::pointer<d2d::geometry>& destination) -> status {
            if (depth > maximum_visual_depth) return status::invalid_graph;
            std::uint32_t transform_handle = 0U;
            const auto group = geometry_groups.find(resource);
            const auto combined = combined_geometries.find(resource);
            if (group != geometry_groups.end()) transform_handle = group->second.transform_handle;
            else if (combined != combined_geometries.end()) transform_handle = combined->second.transform_handle;
            float local_tolerance = target_tolerance;
            if (!root && transform_handle != 0U) {
                affine_2d_double transform{};
                const status result = resolve_transform(transform_handle, transform);
                if (result != status::success) return result;
                const double scale = std::hypot(std::hypot(transform.m11, transform.m12),
                    std::hypot(transform.m21, transform.m22));
                local_tolerance = static_cast<float>(target_tolerance / std::max(1.0, scale));
                if (!std::isfinite(local_tolerance) || local_tolerance <= 0.0F) return status::unsupported_command;
            }
            if (group != geometry_groups.end()) {
                std::vector<com::pointer<d2d::geometry>> children(group->second.children.size());
                std::vector<d2d::geometry*> pointers;
                pointers.reserve(children.size());
                for (std::size_t index = 0U; index < children.size(); ++index) {
                    const status result = self(self, group->second.children[index], depth + 1U, false, local_tolerance, children[index]);
                    if (result != status::success) return result;
                    pointers.push_back(children[index].get());
                }
                com::pointer<d2d::geometry_group> value;
                hr = factory->CreateGeometryGroup(group->second.fill_rule == 0U ? d2d::fill_mode::alternate : d2d::fill_mode::winding,
                    pointers.data(), static_cast<std::uint32_t>(pointers.size()), value.put());
                if (com::failed(hr)) return convert(hr);
                destination.attach(value.detach());
                transform_handle = group->second.transform_handle;
            } else if (combined != combined_geometries.end()) {
                com::pointer<d2d::geometry> first, second;
                status result = self(self, combined->second.geometry1_handle, depth + 1U, false, local_tolerance, first);
                if (result != status::success) return result;
                result = self(self, combined->second.geometry2_handle, depth + 1U, false, local_tolerance, second);
                if (result != status::success) return result;
                com::pointer<d2d::path_geometry> value;
                hr = factory->CreatePathGeometry(value.put());
                if (com::failed(hr)) return convert(hr);
                com::pointer<d2d::geometry_sink> sink;
                hr = value->Open(sink.put());
                if (com::failed(hr)) return convert(hr);
                hr = first->CombineWithGeometry(second.get(), static_cast<d2d::combine_mode>(combined->second.combine_mode),
                    nullptr, local_tolerance, sink.get());
                if (com::failed(hr)) return convert(hr);
                hr = sink->Close();
                if (com::failed(hr)) return convert(hr);
                destination.attach(value.detach());
                transform_handle = combined->second.transform_handle;
            } else {
                std::vector<progpu_native_path_segment> segments;
                shallow_fill_leaf leaf{};
                if (resource != 0U) {
                    const status result = append_group_fill_leaf(resource, segments, leaf);
                    if (result != status::success) return result;
                }
                com::pointer<d2d::path_geometry> value;
                hr = d2d::detail::create_native_fill_geometry(factory.get(), segments,
                    leaf.fill_rule == PROGPU_NATIVE_FILL_RULE_EVEN_ODD ? d2d::fill_mode::alternate : d2d::fill_mode::winding,
                    value.put());
                if (com::failed(hr)) return convert(hr);
                destination.attach(value.detach());
                // Leaf lowering already includes its own resource transform.
            }
            if (!root && transform_handle != 0U) {
                affine_2d_double transform{};
                const status result = resolve_transform(transform_handle, transform);
                if (result != status::success) return result;
                progpu_native_affine_2d native{};
                if (!try_to_native_affine(transform, native)) return status::invalid_graph;
                const d2d::matrix_3x2_f matrix{native.m11, native.m12, native.m21, native.m22, native.m31, native.m32};
                com::pointer<d2d::transformed_geometry> transformed;
                hr = factory->CreateTransformedGeometry(destination.get(), &matrix, transformed.put());
                if (com::failed(hr)) return convert(hr);
                destination.attach(transformed.detach());
            }
            return status::success;
        };
        com::pointer<d2d::geometry> geometry;
        const status built = build(build, handle, 1U, true, tolerance, geometry);
        if (built != status::success) return built;
        std::vector<std::vector<d2d::point_2f>> contours;
        hr = d2d::detail::extract_outline_contours(geometry.get(), tolerance, contours);
        if (com::failed(hr)) return convert(hr);
        path_geometry_state result{};
        result.fill_rule = 1U;
        if (!contours.empty()) {
            d2d::rectangle_f bounds{};
            hr = geometry->GetBounds(nullptr, &bounds);
            if (com::failed(hr)) return convert(hr);
            result.left = bounds.left;
            result.top = bounds.top;
            result.right = bounds.right;
            result.bottom = bounds.bottom;
        }
        for (const auto& points : contours) {
            if (points.size() < 3U) return status::invalid_graph;
            path_stroke_contour_state contour{};
            contour.closed = true;
            contour.points.reserve(points.size());
            contour.segments.reserve(points.size());
            contour.smooth_joins.assign(points.size(), 0U);
            for (std::size_t index = 0U; index < points.size(); ++index) {
                const auto& point = points[index];
                const auto& next = points[(index + 1U) % points.size()];
                progpu_native_path_segment segment{};
                segment.kind = PROGPU_NATIVE_PATH_SEGMENT_LINE;
                segment.p0 = {point.x, point.y};
                segment.p1 = {next.x, next.y};
                contour.points.push_back(segment.p0);
                contour.segments.push_back(segment);
            }
            result.stroke_contours.push_back(std::move(contour));
        }
        output = std::move(result);
        return status::success;
    }

    status append_render_stream(
        std::span<const std::byte> bytes,
        compact_guideline_state_map* compact_guidelines,
        render_scope_state current,
        std::uint32_t drawing_depth,
        native::semantic_scene_builder& builder,
        std::unordered_map<std::uint32_t, std::uint32_t>& brush_indices,
        std::unordered_map<std::uint32_t, std::uint32_t>& image_indices,
        std::unordered_map<std::uint64_t, glyph_scene_resource>&
            glyph_resources,
        scene_compile_context* compile_context,
        std::unordered_set<std::uint32_t>& active_drawings,
        std::vector<progpu_native_scene_clip_path>& clip_paths,
        std::vector<progpu_native_path_segment>& clip_segments,
        std::vector<progpu_native_scene_path_boolean_node>&
            clip_boolean_nodes,
        scene_metrics& metrics) const {
        if (drawing_depth == 0U ||
            drawing_depth > maximum_visual_depth) {
            return status::invalid_graph;
        }

        batch_reader reader(bytes);
        command_view view{};
        std::vector<render_scope_state> scope_states;
        std::vector<bool> scope_layers;
        curve_dash::run_buffer curve_dash_scratch;
        std::vector<progpu_native_path_segment>
            drawing_image_bounds_segments;
        const auto save_state = [&builder](
            const render_scope_state& source) noexcept {
            auto state = native::semantic_scene_builder::identity_state();
            if (!try_to_native_affine(source.transform, state.transform)) {
                return false;
            }
            state.opacity = static_cast<float>(source.opacity);
            if (source.has_clip) {
                state.flags |= PROGPU_NATIVE_SCENE_STATE_CLIP_RECT;
                state.clip_rect = source.clip_rect;
            }
            if (source.mask_resource_index !=
                PROGPU_NATIVE_SCENE_NO_INDEX) {
                state.flags |= PROGPU_NATIVE_SCENE_STATE_MASK;
                state.mask_resource_index = source.mask_resource_index;
            }
            if (source.guideline_resource_index !=
                PROGPU_NATIVE_SCENE_NO_INDEX) {
                state.flags |= PROGPU_NATIVE_SCENE_STATE_GUIDELINE_SET;
                state.guideline_resource_index =
                    source.guideline_resource_index;
            }
            std::uint32_t state_index = PROGPU_NATIVE_SCENE_NO_INDEX;
            return builder.add_state(state, state_index) &&
                builder.save(state_index);
        };
        const auto append_vector_clip = [
            this,
            &builder,
            &clip_paths,
            &clip_segments,
            &clip_boolean_nodes](
            std::uint32_t geometry_handle,
            const affine_2d_double& target_transform,
            render_scope_state& state) {
            return append_geometry_clip(
                geometry_handle, target_transform, state, builder,
                clip_paths, clip_segments, clip_boolean_nodes);
        };
        const auto apply_clip = [
            this,
            &append_vector_clip](
            std::uint32_t geometry_handle,
            const render_scope_state& source,
            render_scope_state& destination) {
            const auto geometry = fixed_geometries.find(geometry_handle);
            bool lowered_rectangle = false;
            fixed_geometry_state resolved_geometry{};
            if (geometry != fixed_geometries.end()) {
                const status geometry_status = resolve_fixed_geometry(
                    geometry_handle, resolved_geometry);
                if (geometry_status != status::success) {
                    return geometry_status;
                }
            }
            if (geometry != fixed_geometries.end() &&
                resolved_geometry.kind == fixed_geometry_kind::rectangle &&
                (resolved_geometry.radius_x == 0.0 ||
                 resolved_geometry.radius_y == 0.0)) {
                affine_2d_double local_transform{};
                if (resolved_geometry.transform_handle != 0U) {
                    const status transform_status = resolve_transform(
                        resolved_geometry.transform_handle,
                        local_transform);
                    if (transform_status != status::success) {
                        return transform_status;
                    }
                }
                const auto effective_transform = compose_affine(
                    local_transform,
                    source.transform);
                const bool preserves_axis_alignment =
                    (effective_transform.m12 == 0.0 &&
                     effective_transform.m21 == 0.0) ||
                    (effective_transform.m11 == 0.0 &&
                     effective_transform.m22 == 0.0);
                if (preserves_axis_alignment) {
                    progpu_native_image_rect clip_rect{};
                    if (!try_transform_bounds(
                            resolved_geometry.first,
                            resolved_geometry.second,
                            resolved_geometry.third,
                            resolved_geometry.fourth,
                            effective_transform,
                            clip_rect)) {
                        return status::invalid_graph;
                    }
                    if (source.has_clip) {
                        const float left = std::max(
                            source.clip_rect.x,
                            clip_rect.x);
                        const float top = std::max(
                            source.clip_rect.y,
                            clip_rect.y);
                        const float right = std::min(
                            source.clip_rect.x + source.clip_rect.width,
                            clip_rect.x + clip_rect.width);
                        const float bottom = std::min(
                            source.clip_rect.y + source.clip_rect.height,
                            clip_rect.y + clip_rect.height);
                        clip_rect = {
                            left,
                            top,
                            std::max(0.0F, right - left),
                            std::max(0.0F, bottom - top)};
                    }
                    destination.clip_rect = clip_rect;
                    destination.has_clip = true;
                    lowered_rectangle = true;
                }
            }
            if (lowered_rectangle) {
                return status::success;
            }
            const bool known_geometry =
                geometry != fixed_geometries.end() ||
                path_geometries.contains(geometry_handle) ||
                geometry_groups.contains(geometry_handle) ||
                combined_geometries.contains(geometry_handle);
            if (!known_geometry) {
                return status::invalid_handle;
            }
            return append_vector_clip(
                geometry_handle,
                source.transform,
                destination);
        };
        const auto apply_rectangle_clip = [
            &builder,
            &clip_paths,
            &clip_segments,
            &clip_boolean_nodes](
            double x,
            double y,
            double width,
            double height,
            const render_scope_state& source,
            render_scope_state& destination) {
            const auto& transform = source.transform;
            const bool preserves_axis_alignment =
                (transform.m12 == 0.0 && transform.m21 == 0.0) ||
                (transform.m11 == 0.0 && transform.m22 == 0.0);
            if (preserves_axis_alignment) {
                progpu_native_image_rect clip_rect{};
                if (!try_transform_bounds(
                        x, y, width, height, transform, clip_rect)) {
                    return status::invalid_graph;
                }
                if (source.has_clip) {
                    const float left = std::max(
                        source.clip_rect.x, clip_rect.x);
                    const float top = std::max(
                        source.clip_rect.y, clip_rect.y);
                    const float right = std::min(
                        source.clip_rect.x + source.clip_rect.width,
                        clip_rect.x + clip_rect.width);
                    const float bottom = std::min(
                        source.clip_rect.y + source.clip_rect.height,
                        clip_rect.y + clip_rect.height);
                    clip_rect = {
                        left,
                        top,
                        std::max(0.0F, right - left),
                        std::max(0.0F, bottom - top)};
                }
                destination.clip_rect = clip_rect;
                destination.has_clip = true;
                return status::success;
            }

            clip_paths.resize(source.clip_path_count);
            clip_segments.resize(source.clip_segment_count);
            clip_boolean_nodes.resize(source.clip_boolean_node_count);
            const std::size_t segment_offset = clip_segments.size();
            const auto map_point = [&transform](
                double point_x,
                double point_y,
                progpu_native_point& result) noexcept {
                const double mapped_x = point_x * transform.m11 +
                    point_y * transform.m21 + transform.m31;
                const double mapped_y = point_x * transform.m12 +
                    point_y * transform.m22 + transform.m32;
                if (!finite_double_as_float(mapped_x) ||
                    !finite_double_as_float(mapped_y)) {
                    return false;
                }
                result = {static_cast<float>(mapped_x),
                    static_cast<float>(mapped_y)};
                return true;
            };
            std::array<progpu_native_point, 4U> points{};
            if (!map_point(x, y, points[0]) ||
                !map_point(x + width, y, points[1]) ||
                !map_point(x + width, y + height, points[2]) ||
                !map_point(x, y + height, points[3])) {
                return status::invalid_graph;
            }
            float left = points[0].x;
            float top = points[0].y;
            float right = points[0].x;
            float bottom = points[0].y;
            try {
                for (std::size_t index = 0U; index < points.size(); ++index) {
                    progpu_native_path_segment segment{};
                    segment.kind = PROGPU_NATIVE_PATH_SEGMENT_LINE;
                    segment.p0 = points[index];
                    segment.p1 = points[(index + 1U) % points.size()];
                    clip_segments.push_back(segment);
                    left = std::min(left, segment.p0.x);
                    top = std::min(top, segment.p0.y);
                    right = std::max(right, segment.p0.x);
                    bottom = std::max(bottom, segment.p0.y);
                }
            } catch (const std::bad_alloc&) {
                clip_segments.resize(segment_offset);
                return status::capacity_exceeded;
            }
            if (clip_paths.size() >= 64U) {
                clip_segments.resize(segment_offset);
                return status::unsupported_command;
            }
            clip_paths.push_back({
                segment_offset,
                4U,
                0U,
                0U,
                left,
                top,
                right,
                bottom,
                {1.0F, 0.0F, 0.0F, 1.0F, 0.0F, 0.0F},
                PROGPU_NATIVE_FILL_RULE_NON_ZERO,
                8U,
                PROGPU_NATIVE_CLIP_INTERSECT,
                0U});
            std::uint32_t mask_resource_index =
                PROGPU_NATIVE_SCENE_NO_INDEX;
            if (!builder.add_vector_clip_mask(
                    clip_paths,
                    clip_segments,
                    clip_boolean_nodes,
                    1.0F,
                    mask_resource_index)) {
                clip_paths.pop_back();
                clip_segments.resize(segment_offset);
                return status::invalid_graph;
            }
            destination.clip_path_count = clip_paths.size();
            destination.clip_segment_count = clip_segments.size();
            destination.clip_boolean_node_count = clip_boolean_nodes.size();
            destination.mask_resource_index = mask_resource_index;
            return status::success;
        };
        const auto resolve_brush_index = [
            this,
            &builder,
            &brush_indices](
            std::uint32_t brush_handle,
            std::uint32_t& result,
            const brush_use_state* use = nullptr) noexcept {
            const auto solid = solid_brushes.find(brush_handle);
            if (solid != solid_brushes.end()) {
                const auto existing = brush_indices.find(brush_handle);
                if (existing != brush_indices.end()) {
                    result = existing->second;
                    return status::success;
                }
                progpu_native_color color{};
                double opacity = 0.0;
                const status brush_status = resolve_solid_brush(
                    brush_handle,
                    color,
                    opacity);
                if (brush_status != status::success) {
                    return brush_status;
                }
                std::uint32_t added = PROGPU_NATIVE_SCENE_NO_INDEX;
                if (!builder.add_solid_brush(
                        color,
                        static_cast<float>(opacity),
                        added)) {
                    return status::invalid_graph;
                }
                brush_indices.emplace(brush_handle, added);
                result = added;
                return status::success;
            }
            if (tile_brushes.contains(brush_handle)) {
                // Tile sources require a sampled-image/layer brush, not a
                // gradient-table approximation. The image lowering path owns it.
                return status::unsupported_command;
            }
            if (use == nullptr) {
                return status::unsupported_command;
            }
            progpu_native_scene_brush native{};
            std::vector<progpu_native_scene_gradient_stop> stops;
            const status brush_status = resolve_gradient_scene_brush(
                brush_handle, *use, native, stops);
            if (brush_status != status::success) {
                return brush_status;
            }
            if (native.type == PROGPU_NATIVE_SCENE_BRUSH_SOLID) {
                return builder.add_solid_brush(
                        native.colors[0], native.opacity, result)
                    ? status::success
                    : status::invalid_graph;
            }
            return builder.add_brush(native, stops, result)
                ? status::success
                : status::invalid_graph;
        };
        const auto append_polyline_stroke = [
            this,
            &builder,
            &current](
            const pen_state& pen,
            std::span<const progpu_native_point> points,
            bool closed,
            std::uint32_t brush_index,
            progpu_native_image_rect bounds,
            const progpu_native_affine_2d& local_transform,
            std::uint32_t start_cap,
            std::uint32_t end_cap,
            bool use_wpf_join_semantics = false) noexcept {
            std::span<const double> intervals;
            double dash_offset = 0.0;
            if (pen.dash_style_handle != 0U) {
                const auto dash = dash_styles.find(pen.dash_style_handle);
                if (dash == dash_styles.end()) {
                    return status::invalid_handle;
                }
                intervals = dash->second.intervals;
                const status dash_status = resolve_dash_offset(
                    pen.dash_style_handle,
                    dash_offset);
                if (dash_status != status::success) {
                    return dash_status;
                }
            }
            progpu_native_scene_stroke stroke{};
            stroke.struct_size = sizeof(stroke);
            stroke.kind = PROGPU_NATIVE_SCENE_STROKE_POLYLINE;
            stroke.flags = current.edge_aliased
                ? static_cast<std::uint32_t>(
                    PROGPU_NATIVE_POLYLINE_FLAG_EDGE_ALIASED)
                : 0U;
            if (closed) {
                stroke.flags |= PROGPU_NATIVE_POLYLINE_FLAG_CLOSED;
            }
            if (use_wpf_join_semantics) {
                stroke.flags |=
                    PROGPU_NATIVE_POLYLINE_FLAG_WPF_JOIN_SEMANTICS;
            }
            stroke.point_count = points.size();
            stroke.dash_interval_count = intervals.size();
            stroke.color = {1.0F, 1.0F, 1.0F, 1.0F};
            stroke.transform = local_transform;
            stroke.stroke_thickness = static_cast<float>(pen.thickness);
            stroke.miter_limit =
                static_cast<float>(std::max(1.0, pen.miter_limit));
            stroke.dash_offset = dash_offset;
            stroke.start_cap = start_cap;
            stroke.end_cap = end_cap;
            stroke.line_join = pen.line_join;
            stroke.dash_cap = pen.dash_cap;
            const std::array brushes{brush_index};
            return builder.draw_strokes(
                    std::span<const progpu_native_scene_stroke>(&stroke, 1U),
                    points,
                    intervals,
                    brushes,
                    bounds)
                ? status::success
                : status::invalid_graph;
        };
        const auto append_degenerate_cap_stroke = [
            this,
            &builder,
            &current](
            const pen_state& pen,
            progpu_native_point point,
            std::uint32_t brush_index,
            const affine_2d_double& local_transform,
            const affine_2d_double& effective_transform,
            std::uint32_t start_cap,
            std::uint32_t end_cap,
            bool& emitted,
            std::vector<progpu_native_geometry_primitive>* collected = nullptr) {
            emitted = false;
            if (start_cap == PROGPU_NATIVE_STROKE_CAP_FLAT &&
                end_cap == PROGPU_NATIVE_STROKE_CAP_FLAT) {
                return status::success;
            }
            if (pen.dash_style_handle != 0U) {
                const auto dash = dash_styles.find(pen.dash_style_handle);
                if (dash == dash_styles.end()) {
                    return status::invalid_handle;
                }
                if (!dash->second.intervals.empty()) {
                    const std::size_t source_count =
                        dash->second.intervals.size();
                    if (source_count >
                        std::numeric_limits<std::size_t>::max() / 2U) {
                        return status::unsupported_command;
                    }
                    const std::size_t effective_count =
                        (source_count & 1U) == 0U
                            ? source_count
                            : source_count * 2U;
                    double pattern_length = 0.0;
                    for (std::size_t index = 0U;
                         index < effective_count;
                         ++index) {
                        const double interval = dash->second.intervals[
                            index % source_count];
                        if (pattern_length >
                            std::numeric_limits<double>::max() - interval) {
                            return status::unsupported_command;
                        }
                        pattern_length += interval;
                    }
                    if (!std::isfinite(pattern_length) ||
                        pattern_length <= 0.0) {
                        return status::unsupported_command;
                    }
                    double dash_offset = 0.0;
                    const status dash_status = resolve_dash_offset(
                        pen.dash_style_handle,
                        dash_offset);
                    if (dash_status != status::success) {
                        return dash_status;
                    }
                    double offset = std::fmod(dash_offset, pattern_length);
                    if (!std::isfinite(offset)) {
                        return status::unsupported_command;
                    }
                    if (offset < 0.0) {
                        offset += pattern_length;
                    }
                    std::size_t dash_index = 0U;
                    double dash_end = dash->second.intervals[0U];
                    while (dash_index + 1U < effective_count &&
                        dash_end < offset) {
                        ++dash_index;
                        dash_end += dash->second.intervals[
                            dash_index % source_count];
                    }
                    if ((dash_index & 1U) != 0U) {
                        return status::success;
                    }
                }
            }
            progpu_native_affine_2d native_local_transform{};
            if (!try_to_native_affine(
                    local_transform,
                    native_local_transform)) {
                return status::invalid_graph;
            }
            double local_x = 0.0;
            double local_y = 0.0;
            double local_width = 0.0;
            double local_height = 0.0;
            if (!try_degenerate_cap_stroke_bounds(
                    point.x,
                    point.y,
                    pen.thickness,
                    start_cap,
                    end_cap,
                    local_x,
                    local_y,
                    local_width,
                    local_height)) {
                return status::invalid_graph;
            }
            progpu_native_image_rect stroke_bounds{};
            if (!try_transform_bounds(
                    local_x,
                    local_y,
                    local_width,
                    local_height,
                    effective_transform,
                    stroke_bounds)) {
                return status::invalid_graph;
            }
            std::array<progpu_native_geometry_primitive, 2U> primitives{};
            std::array<std::uint32_t, 2U> brushes{};
            std::size_t primitive_count = 0U;
            const auto append_cap = [
                &primitives,
                &brushes,
                &primitive_count,
                &pen,
                point,
                brush_index,
                &native_local_transform,
                &current](
                std::uint32_t cap,
                bool at_start) {
                if (cap == PROGPU_NATIVE_STROKE_CAP_FLAT) {
                    return;
                }
                progpu_native_geometry_primitive primitive{};
                primitive.kind = PROGPU_NATIVE_GEOMETRY_PATH_CAP;
                primitive.flags =
                    (current.edge_aliased
                        ? static_cast<std::uint32_t>(
                            PROGPU_NATIVE_PRIMITIVE_FLAG_EDGE_ALIASED)
                        : 0U) |
                    (cap << PROGPU_NATIVE_PRIMITIVE_START_CAP_SHIFT);
                primitive.p0 = point;
                primitive.p1 = {1.0F, 0.0F};
                primitive.p2.x = at_start ? 1.0F : 0.0F;
                primitive.stroke_thickness =
                    static_cast<float>(pen.thickness);
                primitive.color = {1.0F, 1.0F, 1.0F, 1.0F};
                primitive.transform = native_local_transform;
                primitives[primitive_count] = primitive;
                brushes[primitive_count] = brush_index;
                ++primitive_count;
            };
            append_cap(start_cap, true);
            append_cap(end_cap, false);
            if (collected != nullptr) {
                // Algorithm: reuse the fixed pair of analytic cap primitives
                // for a tile-mask collector. Time/space: O(1), at most two caps.
                // The caller supplies target-space transforms for mask storage.
                collected->insert(collected->end(), primitives.begin(), primitives.begin() + primitive_count);
                emitted = true;
                return status::success;
            }
            if (!builder.draw_geometry(
                    std::span<const progpu_native_geometry_primitive>(
                        primitives.data(),
                        primitive_count),
                    std::span<const std::uint32_t>(
                        brushes.data(),
                        primitive_count),
                    stroke_bounds)) {
                return status::invalid_graph;
            }
            emitted = true;
            return status::success;
        };
        const auto append_resolved_line_stroke = [
            &builder,
            &resolve_brush_index,
            &append_polyline_stroke,
            &append_degenerate_cap_stroke,
            &metrics,
            &current](
            double x0,
            double y0,
            double x1,
            double y1,
            const pen_state& pen,
            const affine_2d_double& local_transform,
            const affine_2d_double& effective_transform,
            std::uint32_t supplied_brush_index) noexcept {
            if (pen.brush_handle == 0U || pen.thickness == 0.0) {
                return status::success;
            }
            if (affine_has_zero_area(effective_transform)) {
                return status::success;
            }
            if (x0 == x1 && y0 == y1) {
                if (pen.start_line_cap ==
                        PROGPU_NATIVE_STROKE_CAP_FLAT &&
                    pen.end_line_cap ==
                        PROGPU_NATIVE_STROKE_CAP_FLAT) {
                    return status::success;
                }
                double local_x = 0.0;
                double local_y = 0.0;
                double local_width = 0.0;
                double local_height = 0.0;
                if (!try_degenerate_cap_stroke_bounds(
                        x0,
                        y0,
                        pen.thickness,
                        pen.start_line_cap,
                        pen.end_line_cap,
                        local_x,
                        local_y,
                        local_width,
                        local_height)) {
                    return status::invalid_graph;
                }
                std::uint32_t brush_index = supplied_brush_index;
                const brush_use_state brush_use{
                    local_x,
                    local_y,
                    local_width,
                    local_height,
                    effective_transform};
                if (brush_index == PROGPU_NATIVE_SCENE_NO_INDEX) {
                    const status brush_status = resolve_brush_index(
                        pen.brush_handle,
                        brush_index,
                        &brush_use);
                    if (brush_status != status::success) {
                        return brush_status;
                    }
                }
                bool emitted = false;
                const status cap_status = append_degenerate_cap_stroke(
                    pen,
                    {static_cast<float>(x0), static_cast<float>(y0)},
                    brush_index,
                    local_transform,
                    effective_transform,
                    pen.start_line_cap,
                    pen.end_line_cap,
                    emitted);
                if (cap_status == status::success && emitted) {
                    ++metrics.line_count;
                }
                return cap_status;
            }
            double local_x = 0.0;
            double local_y = 0.0;
            double local_width = 0.0;
            double local_height = 0.0;
            if (!try_line_stroke_bounds(
                    x0,
                    y0,
                    x1,
                    y1,
                    pen.thickness,
                    pen.start_line_cap,
                    pen.end_line_cap,
                    local_x,
                    local_y,
                    local_width,
                    local_height)) {
                return status::invalid_graph;
            }
            std::uint32_t brush_index = supplied_brush_index;
            const brush_use_state brush_use{
                local_x,
                local_y,
                local_width,
                local_height,
                effective_transform};
            if (brush_index == PROGPU_NATIVE_SCENE_NO_INDEX) {
                const status brush_status = resolve_brush_index(
                    pen.brush_handle,
                    brush_index,
                    &brush_use);
                if (brush_status != status::success) {
                    return brush_status;
                }
            }
            progpu_native_image_rect transformed_bounds{};
            if (!try_transform_bounds(
                    local_x,
                    local_y,
                    local_width,
                    local_height,
                    effective_transform,
                    transformed_bounds)) {
                return status::invalid_graph;
            }
            progpu_native_affine_2d native_local_transform{};
            if (!try_to_native_affine(
                    local_transform,
                    native_local_transform)) {
                return status::invalid_graph;
            }
            const std::uint32_t flags =
                (current.edge_aliased
                    ? static_cast<std::uint32_t>(
                        PROGPU_NATIVE_PRIMITIVE_FLAG_EDGE_ALIASED)
                    : 0U) |
                (pen.start_line_cap <<
                    PROGPU_NATIVE_PRIMITIVE_START_CAP_SHIFT) |
                (pen.end_line_cap <<
                    PROGPU_NATIVE_PRIMITIVE_END_CAP_SHIFT);
            if (pen.dash_style_handle == 0U) {
                const std::array primitives{
                    progpu_native_geometry_primitive{
                        PROGPU_NATIVE_GEOMETRY_LINE,
                        flags,
                        {static_cast<float>(x0), static_cast<float>(y0)},
                        {static_cast<float>(x1), static_cast<float>(y1)},
                        {},
                        {},
                        static_cast<float>(pen.thickness),
                        0.0F,
                        {1.0F, 1.0F, 1.0F, 1.0F},
                        native_local_transform}};
                const std::array brushes{brush_index};
                if (!builder.draw_geometry(
                        primitives,
                        brushes,
                        transformed_bounds)) {
                    return status::invalid_graph;
                }
            } else {
                const std::array points{
                    progpu_native_point{
                        static_cast<float>(x0),
                        static_cast<float>(y0)},
                    progpu_native_point{
                        static_cast<float>(x1),
                        static_cast<float>(y1)}};
                const status stroke_status = append_polyline_stroke(
                    pen,
                    points,
                    false,
                    brush_index,
                    transformed_bounds,
                    native_local_transform,
                    pen.start_line_cap,
                    pen.end_line_cap,
                    true);
                if (stroke_status != status::success) {
                    return stroke_status;
                }
            }
            ++metrics.line_count;
            return status::success;
        };
        const auto append_line_stroke = [
            this,
            &append_resolved_line_stroke](
            double x0,
            double y0,
            double x1,
            double y1,
            std::uint32_t pen_handle,
            const affine_2d_double& local_transform,
            const affine_2d_double& effective_transform) noexcept {
            if (pen_handle == 0U) {
                return status::success;
            }
            pen_state pen{};
            const status pen_status = resolve_pen(pen_handle, pen);
            if (pen_status != status::success) {
                return pen_status;
            }
            return append_resolved_line_stroke(
                x0,
                y0,
                x1,
                y1,
                pen,
                local_transform,
                effective_transform,
                PROGPU_NATIVE_SCENE_NO_INDEX);
        };
        // Algorithm: retain the ordinary collapsed ellipse's traversal order.
        // Time/space complexity: O(1), exactly four points.
        const auto degenerate_ellipse_points = [](double x, double y, double rx, double ry) {
            const progpu_native_point center{static_cast<float>(x), static_cast<float>(y)};
            return rx == 0.0
                ? std::array{center, progpu_native_point{center.x, static_cast<float>(y + ry)},
                    center, progpu_native_point{center.x, static_cast<float>(y - ry)}}
                : std::array{progpu_native_point{static_cast<float>(x + rx), center.y}, center,
                    progpu_native_point{static_cast<float>(x - rx), center.y}, center};
        };
        const auto append_degenerate_ellipse_stroke = [
            this,
            &builder,
            &resolve_brush_index,
            &append_degenerate_cap_stroke,
            &degenerate_ellipse_points,
            &append_polyline_stroke,
            &current](
            double center_x,
            double center_y,
            double radius_x,
            double radius_y,
            const pen_state& pen,
            const affine_2d_double& local_transform,
            const affine_2d_double& effective_transform) noexcept {
            if (pen.brush_handle == 0U || pen.thickness == 0.0) {
                return status::success;
            }
            if (radius_x == 0.0 && radius_y == 0.0) {
                double local_x = 0.0;
                double local_y = 0.0;
                double local_width = 0.0;
                double local_height = 0.0;
                if (!try_degenerate_cap_stroke_bounds(
                        center_x,
                        center_y,
                        pen.thickness,
                        PROGPU_NATIVE_STROKE_CAP_ROUND,
                        PROGPU_NATIVE_STROKE_CAP_ROUND,
                        local_x,
                        local_y,
                        local_width,
                        local_height)) {
                    return status::invalid_graph;
                }
                std::uint32_t brush_index = PROGPU_NATIVE_SCENE_NO_INDEX;
                const brush_use_state brush_use{
                    local_x,
                    local_y,
                    local_width,
                    local_height,
                    effective_transform};
                const status brush_status = resolve_brush_index(
                    pen.brush_handle,
                    brush_index,
                    &brush_use);
                if (brush_status != status::success) {
                    return brush_status;
                }
                bool emitted = false;
                return append_degenerate_cap_stroke(
                    pen,
                    {static_cast<float>(center_x),
                        static_cast<float>(center_y)},
                    brush_index,
                    local_transform,
                    effective_transform,
                    PROGPU_NATIVE_STROKE_CAP_ROUND,
                    PROGPU_NATIVE_STROKE_CAP_ROUND,
                    emitted);
            }
            bool has_nonempty_dash = false;
            if (pen.dash_style_handle != 0U) {
                const auto dash = dash_styles.find(pen.dash_style_handle);
                if (dash == dash_styles.end()) {
                    return status::invalid_handle;
                }
                has_nonempty_dash = !dash->second.intervals.empty();
            }
            const double half_thickness = pen.thickness * 0.5;
            const brush_use_state brush_use{
                center_x - radius_x - half_thickness,
                center_y - radius_y - half_thickness,
                radius_x * 2.0 + pen.thickness,
                radius_y * 2.0 + pen.thickness,
                effective_transform};
            std::uint32_t brush_index = PROGPU_NATIVE_SCENE_NO_INDEX;
            const status brush_status = resolve_brush_index(
                pen.brush_handle,
                brush_index,
                &brush_use);
            if (brush_status != status::success) {
                return brush_status;
            }
            progpu_native_image_rect stroke_bounds{};
            if (!try_fixed_shape_stroke_bounds(
                    center_x - radius_x,
                    center_y - radius_y,
                    radius_x * 2.0,
                    radius_y * 2.0,
                    pen.thickness,
                    effective_transform,
                    stroke_bounds)) {
                return status::invalid_graph;
            }
            progpu_native_affine_2d native_local_transform{};
            if (!try_to_native_affine(
                    local_transform,
                    native_local_transform)) {
                return status::invalid_graph;
            }
            if (has_nonempty_dash) {
                const auto points = degenerate_ellipse_points(center_x, center_y, radius_x, radius_y);
                pen_state smooth_pen = pen;
                smooth_pen.line_join = PROGPU_NATIVE_STROKE_JOIN_ROUND;
                return append_polyline_stroke(
                    smooth_pen,
                    points,
                    true,
                    brush_index,
                    stroke_bounds,
                    native_local_transform,
                    pen.start_line_cap,
                    pen.end_line_cap,
                    true);
            }
            const std::uint32_t flags =
                (current.edge_aliased
                    ? static_cast<std::uint32_t>(
                        PROGPU_NATIVE_PRIMITIVE_FLAG_EDGE_ALIASED)
                    : 0U) |
                (PROGPU_NATIVE_STROKE_CAP_ROUND <<
                    PROGPU_NATIVE_PRIMITIVE_START_CAP_SHIFT) |
                (PROGPU_NATIVE_STROKE_CAP_ROUND <<
                    PROGPU_NATIVE_PRIMITIVE_END_CAP_SHIFT);
            const progpu_native_point first = radius_x == 0.0
                ? progpu_native_point{
                      static_cast<float>(center_x),
                      static_cast<float>(center_y - radius_y)}
                : progpu_native_point{
                      static_cast<float>(center_x - radius_x),
                      static_cast<float>(center_y)};
            const progpu_native_point second = radius_x == 0.0
                ? progpu_native_point{
                      static_cast<float>(center_x),
                      static_cast<float>(center_y + radius_y)}
                : progpu_native_point{
                      static_cast<float>(center_x + radius_x),
                      static_cast<float>(center_y)};
            const std::array primitives{
                progpu_native_geometry_primitive{
                    PROGPU_NATIVE_GEOMETRY_LINE,
                    flags,
                    first,
                    second,
                    {},
                    {},
                    static_cast<float>(pen.thickness),
                    0.0F,
                    {1.0F, 1.0F, 1.0F, 1.0F},
                    native_local_transform}};
            const std::array brushes{brush_index};
            return builder.draw_geometry(
                    primitives,
                    brushes,
                    stroke_bounds)
                ? status::success
                : status::invalid_graph;
        };
        const auto append_path_strokes = [
            this,
            &builder,
            &resolve_brush_index,
            &append_polyline_stroke,
            &append_degenerate_cap_stroke,
            &curve_dash_scratch,
            &current](
            const path_geometry_state& geometry,
            const pen_state& pen,
            const affine_2d_double& local_transform,
            const affine_2d_double& effective_transform,
            std::uint32_t supplied_brush_index) noexcept {
            if (pen.brush_handle == 0U || pen.thickness == 0.0) {
                return status::success;
            }
            if (geometry.stroke_contours.empty()) {
                return status::success;
            }
            progpu_native_affine_2d native_local_transform{};
            if (!try_to_native_affine(
                    local_transform,
                    native_local_transform)) {
                return status::invalid_graph;
            }
            const double half_thickness = pen.thickness * 0.5;
            const double expansion = half_thickness *
                std::max(1.0, pen.miter_limit);
            if (!finite_double_as_float(expansion)) {
                return status::invalid_graph;
            }
            const brush_use_state brush_use{
                geometry.left - expansion,
                geometry.top - expansion,
                geometry.right - geometry.left + expansion * 2.0,
                geometry.bottom - geometry.top + expansion * 2.0,
                effective_transform};
            std::uint32_t brush_index = supplied_brush_index;
            if (brush_index == PROGPU_NATIVE_SCENE_NO_INDEX) {
                const status brush_status = resolve_brush_index(
                    pen.brush_handle,
                    brush_index,
                    &brush_use);
                if (brush_status != status::success) {
                    return brush_status;
                }
            }
            auto& dashed_runs = curve_dash_scratch;
            for (const auto& contour : geometry.stroke_contours) {
                const bool has_curves = std::ranges::any_of(
                    contour.segments,
                    [](const progpu_native_path_segment& segment) {
                        return segment.kind !=
                            PROGPU_NATIVE_PATH_SEGMENT_LINE;
                    });
                if (contour.smooth_joins.size() != contour.segments.size()) {
                    return status::invalid_graph;
                }
                const bool has_smooth_joins = std::ranges::any_of(
                    contour.smooth_joins,
                    [](std::uint8_t smooth_join) {
                        return smooth_join != 0U;
                    });
                if (contour.points.empty()) {
                    continue;
                }
                bool has_length = false;
                double left = contour.points.front().x;
                double top = contour.points.front().y;
                double right = left;
                double bottom = top;
                for (std::size_t index = 0U;
                     index < contour.points.size();
                     ++index) {
                    const auto point = contour.points[index];
                    left = std::min(left, double{point.x});
                    top = std::min(top, double{point.y});
                    right = std::max(right, double{point.x});
                    bottom = std::max(bottom, double{point.y});
                    if (index != 0U &&
                        (point.x != contour.points[index - 1U].x ||
                         point.y != contour.points[index - 1U].y)) {
                        has_length = true;
                    }
                }
                if (contour.closed &&
                    (contour.points.front().x != contour.points.back().x ||
                     contour.points.front().y != contour.points.back().y)) {
                    has_length = true;
                }
                if (!has_length) {
                    has_length = std::ranges::any_of(
                        contour.segments,
                        [](const progpu_native_path_segment& segment) {
                            const auto differs = [start = segment.p0](
                                progpu_native_point point) noexcept {
                                return point.x != start.x ||
                                    point.y != start.y;
                            };
                            if (segment.kind ==
                                PROGPU_NATIVE_PATH_SEGMENT_QUADRATIC) {
                                return differs(segment.p1) ||
                                    differs(segment.p2);
                            }
                            if (segment.kind ==
                                PROGPU_NATIVE_PATH_SEGMENT_CUBIC) {
                                return differs(segment.p1) ||
                                    differs(segment.p2) ||
                                    differs(segment.p3);
                            }
                            if (segment.kind ==
                                PROGPU_NATIVE_PATH_SEGMENT_ARC) {
                                return segment.p3.x > 0.0F &&
                                    segment.p3.y > 0.0F &&
                                    std::bit_cast<float>(segment.pad1) !=
                                        0.0F;
                            }
                            return false;
                        });
                }
                if (!has_length) {
                    const std::uint32_t start_cap = contour.closed
                        ? static_cast<std::uint32_t>(
                            PROGPU_NATIVE_STROKE_CAP_ROUND)
                        : contour.start_uses_dash_cap
                            ? pen.dash_cap
                            : pen.start_line_cap;
                    const std::uint32_t end_cap = contour.closed
                        ? static_cast<std::uint32_t>(
                            PROGPU_NATIVE_STROKE_CAP_ROUND)
                        : contour.end_uses_dash_cap
                            ? pen.dash_cap
                            : pen.end_line_cap;
                    bool emitted = false;
                    const status cap_status = append_degenerate_cap_stroke(
                        pen,
                        contour.points.front(),
                        brush_index,
                        local_transform,
                        effective_transform,
                        start_cap,
                        end_cap,
                        emitted);
                    if (cap_status != status::success) {
                        return cap_status;
                    }
                    continue;
                }
                progpu_native_image_rect stroke_bounds{};
                if (!try_transform_bounds(
                        left - expansion,
                        top - expansion,
                        right - left + expansion * 2.0,
                        bottom - top + expansion * 2.0,
                        effective_transform,
                        stroke_bounds)) {
                    return status::invalid_graph;
                }
                if (has_curves || has_smooth_joins) {
                    bool has_dashed_runs = false;
                    if (pen.dash_style_handle != 0U) {
                        const auto dash = dash_styles.find(
                            pen.dash_style_handle);
                        if (dash == dash_styles.end()) {
                            return status::invalid_handle;
                        }
                        if (!dash->second.intervals.empty()) {
                            double dash_offset = 0.0;
                            const status dash_status = resolve_dash_offset(
                                pen.dash_style_handle,
                                dash_offset);
                            if (dash_status != status::success) {
                                return dash_status;
                            }
                            const auto dash_result =
                                curve_dash::try_create_runs(
                                    contour.segments,
                                    contour.smooth_joins,
                                    contour.closed,
                                    dash->second.intervals,
                                    dash_offset,
                                    static_cast<float>(pen.thickness),
                                    dashed_runs);
                            if (dash_result ==
                                curve_dash::result::capacity_exceeded) {
                                return status::capacity_exceeded;
                            }
                            if (dash_result != curve_dash::result::success) {
                                return status::unsupported_command;
                            }
                            has_dashed_runs = true;
                        }
                    }
                    const std::uint32_t start_cap =
                        contour.start_uses_dash_cap
                            ? pen.dash_cap
                            : pen.start_line_cap;
                    const std::uint32_t end_cap =
                        contour.end_uses_dash_cap
                            ? pen.dash_cap
                            : pen.end_line_cap;
                    const auto segment_end = [](
                        const progpu_native_path_segment& segment) noexcept {
                        return segment.kind ==
                                PROGPU_NATIVE_PATH_SEGMENT_QUADRATIC
                            ? segment.p2
                            : segment.kind ==
                                    PROGPU_NATIVE_PATH_SEGMENT_CUBIC
                                ? segment.p3
                                : segment.p1;
                    };
                    const auto subtract = [](
                        progpu_native_point first,
                        progpu_native_point second) noexcept {
                        return progpu_native_point{
                            first.x - second.x,
                            first.y - second.y};
                    };
                    const auto nonzero = [](progpu_native_point value) noexcept {
                        return value.x != 0.0F || value.y != 0.0F;
                    };
                    const auto try_tangent = [
                        &subtract,
                        &nonzero](
                        const progpu_native_path_segment& segment,
                        bool at_start,
                        progpu_native_point& tangent) noexcept {
                        switch (segment.kind) {
                        case PROGPU_NATIVE_PATH_SEGMENT_LINE:
                            tangent = subtract(segment.p1, segment.p0);
                            return nonzero(tangent);
                        case PROGPU_NATIVE_PATH_SEGMENT_QUADRATIC:
                            tangent = at_start
                                ? subtract(segment.p1, segment.p0)
                                : subtract(segment.p2, segment.p1);
                            if (!nonzero(tangent)) {
                                tangent = subtract(segment.p2, segment.p0);
                            }
                            return nonzero(tangent);
                        case PROGPU_NATIVE_PATH_SEGMENT_CUBIC: {
                            const std::array candidates = at_start
                                ? std::array{
                                      subtract(segment.p1, segment.p0),
                                      subtract(segment.p2, segment.p0),
                                      subtract(segment.p3, segment.p0)}
                                : std::array{
                                      subtract(segment.p3, segment.p2),
                                      subtract(segment.p3, segment.p1),
                                      subtract(segment.p3, segment.p0)};
                            for (const auto candidate : candidates) {
                                if (nonzero(candidate)) {
                                    tangent = candidate;
                                    return true;
                                }
                            }
                            return false;
                        }
                        case PROGPU_NATIVE_PATH_SEGMENT_ARC: {
                            const float theta =
                                std::bit_cast<float>(segment.pad0) +
                                (at_start
                                     ? 0.0F
                                     : std::bit_cast<float>(segment.pad1));
                            const float direction =
                                std::bit_cast<float>(segment.pad1) < 0.0F
                                ? -1.0F
                                : 1.0F;
                            const float rotation =
                                std::bit_cast<float>(segment.pad2);
                            const float cosine_rotation = std::cos(rotation);
                            const float sine_rotation = std::sin(rotation);
                            const progpu_native_point axis_x{
                                segment.p3.x * cosine_rotation,
                                segment.p3.x * sine_rotation};
                            const progpu_native_point axis_y{
                                -segment.p3.y * sine_rotation,
                                segment.p3.y * cosine_rotation};
                            tangent = {
                                direction *
                                    (-axis_x.x * std::sin(theta) +
                                     axis_y.x * std::cos(theta)),
                                direction *
                                    (-axis_x.y * std::sin(theta) +
                                     axis_y.y * std::cos(theta))};
                            return nonzero(tangent);
                        }
                        default:
                            return false;
                        }
                    };
                    const auto make_primitive = [
                        &native_local_transform,
                        &pen,
                        &current](
                        const progpu_native_path_segment& segment,
                        progpu_native_geometry_primitive& primitive) noexcept {
                        primitive = {};
                        primitive.flags = current.edge_aliased
                            ? static_cast<std::uint32_t>(
                                PROGPU_NATIVE_PRIMITIVE_FLAG_EDGE_ALIASED)
                            : 0U;
                        primitive.stroke_thickness =
                            static_cast<float>(pen.thickness);
                        primitive.color = {1.0F, 1.0F, 1.0F, 1.0F};
                        primitive.transform = native_local_transform;
                        switch (segment.kind) {
                        case PROGPU_NATIVE_PATH_SEGMENT_LINE:
                            primitive.kind = PROGPU_NATIVE_GEOMETRY_LINE;
                            primitive.p0 = segment.p0;
                            primitive.p1 = segment.p1;
                            return true;
                        case PROGPU_NATIVE_PATH_SEGMENT_QUADRATIC:
                            primitive.kind =
                                PROGPU_NATIVE_GEOMETRY_QUADRATIC_BEZIER;
                            primitive.p0 = segment.p0;
                            primitive.p1 = segment.p1;
                            primitive.p2 = segment.p2;
                            return true;
                        case PROGPU_NATIVE_PATH_SEGMENT_CUBIC:
                            primitive.kind =
                                PROGPU_NATIVE_GEOMETRY_CUBIC_BEZIER;
                            primitive.p0 = segment.p0;
                            primitive.p1 = segment.p1;
                            primitive.p2 = segment.p2;
                            primitive.p3 = segment.p3;
                            return true;
                        case PROGPU_NATIVE_PATH_SEGMENT_ARC: {
                            primitive.kind = PROGPU_NATIVE_GEOMETRY_ARC;
                            primitive.p0 = segment.p2;
                            const float rotation =
                                std::bit_cast<float>(segment.pad2);
                            const float cosine_rotation = std::cos(rotation);
                            const float sine_rotation = std::sin(rotation);
                            primitive.p1 = {
                                segment.p3.x * cosine_rotation,
                                segment.p3.x * sine_rotation};
                            primitive.p2 = {
                                -segment.p3.y * sine_rotation,
                                segment.p3.y * cosine_rotation};
                            primitive.p3 = {
                                std::bit_cast<float>(segment.pad0),
                                std::bit_cast<float>(segment.pad1)};
                            return true;
                        }
                        default:
                            return false;
                        }
                    };
                    std::vector<progpu_native_geometry_primitive> primitives;
                    std::vector<std::uint32_t> brushes;
                    primitives.reserve(contour.segments.size() * 2U + 2U);
                    brushes.reserve(contour.segments.size() * 2U + 2U);
                    const auto append_cap = [
                        &primitives,
                        &brushes,
                        &try_tangent,
                        &segment_end,
                        &native_local_transform,
                        &pen,
                        brush_index,
                        &current](
                        const progpu_native_path_segment& segment,
                        std::uint32_t cap,
                        bool at_start) {
                        if (cap == PROGPU_NATIVE_STROKE_CAP_FLAT) {
                            return true;
                        }
                        progpu_native_point tangent{};
                        if (!try_tangent(segment, at_start, tangent)) {
                            return false;
                        }
                        progpu_native_geometry_primitive primitive{};
                        primitive.kind = PROGPU_NATIVE_GEOMETRY_PATH_CAP;
                        primitive.flags =
                            (current.edge_aliased
                                ? static_cast<std::uint32_t>(
                                    PROGPU_NATIVE_PRIMITIVE_FLAG_EDGE_ALIASED)
                                : 0U) |
                            (cap <<
                                PROGPU_NATIVE_PRIMITIVE_START_CAP_SHIFT);
                        primitive.p0 = at_start ? segment.p0 : segment_end(segment);
                        primitive.p1 = tangent;
                        primitive.p2.x = at_start ? 1.0F : 0.0F;
                        primitive.stroke_thickness =
                            static_cast<float>(pen.thickness);
                        primitive.color = {1.0F, 1.0F, 1.0F, 1.0F};
                        primitive.transform = native_local_transform;
                        primitives.push_back(primitive);
                        brushes.push_back(brush_index);
                        return true;
                    };
                    const auto append_join = [
                        &primitives,
                        &brushes,
                        &try_tangent,
                        &segment_end,
                        &native_local_transform,
                        &pen,
                        brush_index,
                        &current](
                        const progpu_native_path_segment& incoming,
                        const progpu_native_path_segment& outgoing,
                        bool smooth_join) {
                        const auto join_point = segment_end(incoming);
                        if (join_point.x != outgoing.p0.x ||
                            join_point.y != outgoing.p0.y) {
                            return false;
                        }
                        progpu_native_point incoming_tangent{};
                        progpu_native_point outgoing_tangent{};
                        if (!try_tangent(
                                incoming,
                                false,
                                incoming_tangent) ||
                            !try_tangent(
                                outgoing,
                                true,
                                outgoing_tangent)) {
                            return false;
                        }
                        progpu_native_geometry_primitive join{};
                        join.kind = PROGPU_NATIVE_GEOMETRY_PATH_JOIN;
                        join.flags =
                            (current.edge_aliased
                                ? static_cast<std::uint32_t>(
                                    PROGPU_NATIVE_PRIMITIVE_FLAG_EDGE_ALIASED)
                                : 0U) |
                            ((smooth_join
                                  ? static_cast<std::uint32_t>(
                                      PROGPU_NATIVE_STROKE_JOIN_ROUND)
                                  : pen.line_join) <<
                                PROGPU_NATIVE_PRIMITIVE_START_CAP_SHIFT);
                        join.p0 = join_point;
                        join.p1 = incoming_tangent;
                        join.p2 = outgoing_tangent;
                        join.p3.x = static_cast<float>(pen.miter_limit);
                        join.stroke_thickness =
                            static_cast<float>(pen.thickness);
                        join.color = {1.0F, 1.0F, 1.0F, 1.0F};
                        join.transform = native_local_transform;
                        primitives.push_back(join);
                        brushes.push_back(brush_index);
                        return true;
                    };
                    const auto append_run = [
                        &append_cap,
                        &append_join,
                        &make_primitive,
                        &primitives,
                        &brushes,
                        brush_index](
                        std::span<const progpu_native_path_segment> segments,
                        std::span<const std::uint8_t> smooth_joins,
                        bool closed,
                        bool closing_smooth_join,
                        std::uint32_t run_start_cap,
                        std::uint32_t run_end_cap) {
                        if (segments.empty() ||
                            smooth_joins.size() + 1U != segments.size()) {
                            return false;
                        }
                        if (!closed && !append_cap(
                                segments.front(), run_start_cap, true)) {
                            return false;
                        }
                        for (std::size_t segment_index = 0U;
                             segment_index < segments.size();
                             ++segment_index) {
                            if (segment_index != 0U &&
                                !append_join(
                                    segments[segment_index - 1U],
                                    segments[segment_index],
                                    smooth_joins[segment_index - 1U] != 0U)) {
                                return false;
                            }
                            progpu_native_geometry_primitive primitive{};
                            if (!make_primitive(
                                    segments[segment_index], primitive)) {
                                return false;
                            }
                            primitives.push_back(primitive);
                            brushes.push_back(brush_index);
                        }
                        if (closed && !append_join(
                                segments.back(),
                                segments.front(),
                                closing_smooth_join)) {
                            return false;
                        }
                        return closed || append_cap(
                            segments.back(), run_end_cap, false);
                    };
                    if (has_dashed_runs) {
                        for (const auto& run : dashed_runs.runs) {
                            const std::uint32_t run_start_cap =
                                run.starts_at_source_start
                                    ? start_cap
                                    : pen.dash_cap;
                            const std::uint32_t run_end_cap =
                                run.ends_at_source_end
                                    ? end_cap
                                    : pen.dash_cap;
                            if (!append_run(
                                    dashed_runs.segments_for(run),
                                    dashed_runs.smooth_joins_for(run),
                                    run.closed,
                                    run.closing_smooth_join,
                                    run_start_cap,
                                    run_end_cap)) {
                                return status::unsupported_command;
                            }
                        }
                        if (dashed_runs.terminal_visible_point) {
                            progpu_native_point tangent{};
                            if (!try_tangent(
                                    contour.segments.back(),
                                    false,
                                    tangent)) {
                                return status::unsupported_command;
                            }
                            const progpu_native_point endpoint =
                                segment_end(contour.segments.back());
                            progpu_native_path_segment terminal_start{};
                            terminal_start.kind =
                                PROGPU_NATIVE_PATH_SEGMENT_LINE;
                            terminal_start.p0 = endpoint;
                            terminal_start.p1 = {
                                endpoint.x + tangent.x,
                                endpoint.y + tangent.y};
                            progpu_native_path_segment terminal_end{};
                            terminal_end.kind =
                                PROGPU_NATIVE_PATH_SEGMENT_LINE;
                            terminal_end.p0 = {
                                endpoint.x - tangent.x,
                                endpoint.y - tangent.y};
                            terminal_end.p1 = endpoint;
                            if (!append_cap(
                                    terminal_start,
                                    pen.dash_cap,
                                    true) ||
                                !append_cap(
                                    terminal_end,
                                    end_cap,
                                    false)) {
                                return status::unsupported_command;
                            }
                        }
                    } else if (!append_run(
                            contour.segments,
                            std::span<const std::uint8_t>(
                                contour.smooth_joins.data(),
                                contour.smooth_joins.size() - 1U),
                            contour.closed,
                            contour.smooth_joins.back() != 0U,
                            start_cap,
                            end_cap)) {
                        return status::unsupported_command;
                    }
                    if (primitives.empty()) {
                        continue;
                    }
                    if (!builder.draw_geometry(
                            primitives,
                            brushes,
                            stroke_bounds)) {
                        return status::invalid_graph;
                    }
                    continue;
                }
                const status stroke_status = append_polyline_stroke(
                    pen,
                    contour.points,
                    contour.closed,
                    brush_index,
                    stroke_bounds,
                    native_local_transform,
                    contour.start_uses_dash_cap
                        ? pen.dash_cap
                        : pen.start_line_cap,
                    contour.end_uses_dash_cap
                        ? pen.dash_cap
                        : pen.end_line_cap,
                    true);
                if (stroke_status != status::success) {
                    return stroke_status;
                }
            }
            return status::success;
        };
        const auto append_rounded_rectangle_path = [](
            std::vector<progpu_native_path_segment>& segments,
            double left,
            double top,
            double right,
            double bottom,
            double radius_x,
            double radius_y) {
            radius_x = std::clamp(
                radius_x, 0.0, (right - left) * 0.5);
            radius_y = std::clamp(
                radius_y, 0.0, (bottom - top) * 0.5);
            const auto append_line = [&segments](
                double x0,
                double y0,
                double x1,
                double y1) {
                progpu_native_path_segment segment{};
                segment.kind = PROGPU_NATIVE_PATH_SEGMENT_LINE;
                segment.p0 = {
                    static_cast<float>(x0), static_cast<float>(y0)};
                segment.p1 = {
                    static_cast<float>(x1), static_cast<float>(y1)};
                segments.push_back(segment);
            };
            const auto append_arc = [&segments](
                double x0,
                double y0,
                double x1,
                double y1,
                double center_x,
                double center_y,
                double arc_radius_x,
                double arc_radius_y,
                float theta1) {
                progpu_native_path_segment segment{};
                segment.kind = PROGPU_NATIVE_PATH_SEGMENT_ARC;
                segment.p0 = {
                    static_cast<float>(x0), static_cast<float>(y0)};
                segment.p1 = {
                    static_cast<float>(x1), static_cast<float>(y1)};
                segment.p2 = {static_cast<float>(center_x),
                    static_cast<float>(center_y)};
                segment.p3 = {static_cast<float>(arc_radius_x),
                    static_cast<float>(arc_radius_y)};
                segment.pad0 = std::bit_cast<std::uint32_t>(theta1);
                segment.pad1 = std::bit_cast<std::uint32_t>(
                    std::numbers::pi_v<float> * 0.5F);
                segment.pad2 = std::bit_cast<std::uint32_t>(0.0F);
                segments.push_back(segment);
            };
            constexpr float half_pi =
                std::numbers::pi_v<float> * 0.5F;
            append_arc(
                left,
                top + radius_y,
                left + radius_x,
                top,
                left + radius_x,
                top + radius_y,
                radius_x,
                radius_y,
                std::numbers::pi_v<float>);
            append_line(left + radius_x, top, right - radius_x, top);
            append_arc(
                right - radius_x,
                top,
                right,
                top + radius_y,
                right - radius_x,
                top + radius_y,
                radius_x,
                radius_y,
                std::numbers::pi_v<float> + half_pi);
            append_line(right, top + radius_y, right, bottom - radius_y);
            append_arc(
                right,
                bottom - radius_y,
                right - radius_x,
                bottom,
                right - radius_x,
                bottom - radius_y,
                radius_x,
                radius_y,
                0.0F);
            append_line(right - radius_x, bottom, left + radius_x, bottom);
            append_arc(
                left + radius_x,
                bottom,
                left,
                bottom - radius_y,
                left + radius_x,
                bottom - radius_y,
                radius_x,
                radius_y,
                half_pi);
            append_line(left, bottom - radius_y, left, top + radius_y);
        };
        const auto make_rounded_rectangle_geometry = [
            &append_rounded_rectangle_path](
            double x,
            double y,
            double width,
            double height,
            double radius_x,
            double radius_y) {
            path_geometry_state geometry{};
            geometry.left = x;
            geometry.top = y;
            geometry.right = x + width;
            geometry.bottom = y + height;
            geometry.fill_rule = 0U;
            geometry.segments.reserve(8U);
            append_rounded_rectangle_path(
                geometry.segments,
                geometry.left,
                geometry.top,
                geometry.right,
                geometry.bottom,
                radius_x,
                radius_y);
            path_stroke_contour_state contour{};
            contour.closed = true;
            contour.points.reserve(geometry.segments.size());
            contour.segments = geometry.segments;
            contour.smooth_joins.assign(geometry.segments.size(), 1U);
            for (const auto& segment : geometry.segments) {
                contour.points.push_back(segment.p0);
            }
            geometry.stroke_contours.push_back(std::move(contour));
            return geometry;
        };
        const auto make_wpf_rounded_rectangle_geometry = [](
            double x,
            double y,
            double width,
            double height,
            double radius_x,
            double radius_y) {
            path_geometry_state geometry{};
            geometry.left = x;
            geometry.top = y;
            geometry.right = x + width;
            geometry.bottom = y + height;
            geometry.fill_rule = 0U;
            radius_x = std::clamp(radius_x, 0.0, width * 0.5);
            radius_y = std::clamp(radius_y, 0.0, height * 0.5);
            constexpr double arc_as_bezier =
                0.5522847498307933984;
            const double bezier_x =
                (1.0 - arc_as_bezier) * radius_x;
            const double bezier_y =
                (1.0 - arc_as_bezier) * radius_y;
            const double left = x;
            const double top = y;
            const double right = x + width;
            const double bottom = y + height;
            const std::array points{
                progpu_native_point{static_cast<float>(left),
                    static_cast<float>(top + radius_y)},
                progpu_native_point{static_cast<float>(left),
                    static_cast<float>(top + bezier_y)},
                progpu_native_point{static_cast<float>(left + bezier_x),
                    static_cast<float>(top)},
                progpu_native_point{static_cast<float>(left + radius_x),
                    static_cast<float>(top)},
                progpu_native_point{static_cast<float>(right - radius_x),
                    static_cast<float>(top)},
                progpu_native_point{static_cast<float>(right - bezier_x),
                    static_cast<float>(top)},
                progpu_native_point{static_cast<float>(right),
                    static_cast<float>(top + bezier_y)},
                progpu_native_point{static_cast<float>(right),
                    static_cast<float>(top + radius_y)},
                progpu_native_point{static_cast<float>(right),
                    static_cast<float>(bottom - radius_y)},
                progpu_native_point{static_cast<float>(right),
                    static_cast<float>(bottom - bezier_y)},
                progpu_native_point{static_cast<float>(right - bezier_x),
                    static_cast<float>(bottom)},
                progpu_native_point{static_cast<float>(right - radius_x),
                    static_cast<float>(bottom)},
                progpu_native_point{static_cast<float>(left + radius_x),
                    static_cast<float>(bottom)},
                progpu_native_point{static_cast<float>(left + bezier_x),
                    static_cast<float>(bottom)},
                progpu_native_point{static_cast<float>(left),
                    static_cast<float>(bottom - bezier_y)},
                progpu_native_point{static_cast<float>(left),
                    static_cast<float>(bottom - radius_y)},
                progpu_native_point{static_cast<float>(left),
                    static_cast<float>(top + radius_y)}};
            geometry.segments.reserve(8U);
            const auto append_cubic = [&geometry, &points](
                std::size_t start) {
                progpu_native_path_segment segment{};
                segment.kind = PROGPU_NATIVE_PATH_SEGMENT_CUBIC;
                segment.p0 = points[start];
                segment.p1 = points[start + 1U];
                segment.p2 = points[start + 2U];
                segment.p3 = points[start + 3U];
                geometry.segments.push_back(segment);
            };
            const auto append_line = [&geometry, &points](
                std::size_t start) {
                progpu_native_path_segment segment{};
                segment.kind = PROGPU_NATIVE_PATH_SEGMENT_LINE;
                segment.p0 = points[start];
                segment.p1 = points[start + 1U];
                geometry.segments.push_back(segment);
            };
            append_cubic(0U);
            append_line(3U);
            append_cubic(4U);
            append_line(7U);
            append_cubic(8U);
            append_line(11U);
            append_cubic(12U);
            append_line(15U);
            path_stroke_contour_state contour{};
            contour.closed = true;
            contour.points.reserve(geometry.segments.size());
            contour.segments = geometry.segments;
            contour.smooth_joins.assign(geometry.segments.size(), 1U);
            for (const auto& segment : geometry.segments) {
                contour.points.push_back(segment.p0);
            }
            geometry.stroke_contours.push_back(std::move(contour));
            return geometry;
        };
        const auto make_ellipse_path_geometry = [](
            double center_x,
            double center_y,
            double radius_x,
            double radius_y) {
            path_geometry_state geometry{};
            geometry.left = center_x - radius_x;
            geometry.top = center_y - radius_y;
            geometry.right = center_x + radius_x;
            geometry.bottom = center_y + radius_y;
            geometry.fill_rule = 0U;
            progpu_native_path_segment arc{};
            arc.kind = PROGPU_NATIVE_PATH_SEGMENT_ARC;
            arc.p0 = {
                static_cast<float>(center_x + radius_x),
                static_cast<float>(center_y)};
            arc.p1 = arc.p0;
            arc.p2 = {
                static_cast<float>(center_x),
                static_cast<float>(center_y)};
            arc.p3 = {
                static_cast<float>(radius_x),
                static_cast<float>(radius_y)};
            arc.pad0 = std::bit_cast<std::uint32_t>(0.0F);
            arc.pad1 = std::bit_cast<std::uint32_t>(
                std::numbers::pi_v<float> * 2.0F);
            arc.pad2 = std::bit_cast<std::uint32_t>(0.0F);
            geometry.segments.push_back(arc);
            path_stroke_contour_state contour{};
            contour.closed = true;
            contour.points.push_back(arc.p0);
            contour.segments.push_back(arc);
            contour.smooth_joins.push_back(1U);
            geometry.stroke_contours.push_back(std::move(contour));
            return geometry;
        };
        const auto append_positive_fixed_shape_stroke = [
            this,
            &builder,
            &resolve_brush_index,
            &append_polyline_stroke,
            &append_path_strokes,
            &make_rounded_rectangle_geometry,
            &make_ellipse_path_geometry,
            &current](
            const fixed_geometry_state& geometry,
            const pen_state& pen,
            const affine_2d_double& local_transform,
            const affine_2d_double& effective_transform,
            std::uint32_t supplied_brush_index) noexcept {
            const bool is_ellipse =
                geometry.kind == fixed_geometry_kind::ellipse;
            if (geometry.kind == fixed_geometry_kind::line) {
                return status::unsupported_command;
            }
            const double x = is_ellipse
                ? geometry.first - geometry.third
                : geometry.first;
            const double y = is_ellipse
                ? geometry.second - geometry.fourth
                : geometry.second;
            const double width = is_ellipse
                ? geometry.third * 2.0
                : geometry.third;
            const double height = is_ellipse
                ? geometry.fourth * 2.0
                : geometry.fourth;
            if (width <= 0.0 || height <= 0.0 ||
                pen.brush_handle == 0U || pen.thickness == 0.0) {
                return status::unsupported_command;
            }
            const double half_thickness = pen.thickness * 0.5;
            const brush_use_state brush_use{
                x - half_thickness,
                y - half_thickness,
                width + pen.thickness,
                height + pen.thickness,
                effective_transform};
            std::uint32_t brush_index = supplied_brush_index;
            if (brush_index == PROGPU_NATIVE_SCENE_NO_INDEX) {
                const status brush_status = resolve_brush_index(
                    pen.brush_handle,
                    brush_index,
                    &brush_use);
                if (brush_status != status::success) {
                    return brush_status;
                }
            }
            progpu_native_image_rect stroke_bounds{};
            if (!try_fixed_shape_stroke_bounds(
                    x,
                    y,
                    width,
                    height,
                    pen.thickness,
                    effective_transform,
                    stroke_bounds)) {
                return status::invalid_graph;
            }
            progpu_native_affine_2d native_local_transform{};
            if (!try_to_native_affine(
                    local_transform,
                    native_local_transform)) {
                return status::invalid_graph;
            }
            bool has_nonempty_dash = false;
            if (pen.dash_style_handle != 0U) {
                const auto dash = dash_styles.find(pen.dash_style_handle);
                if (dash == dash_styles.end()) {
                    return status::invalid_handle;
                }
                has_nonempty_dash = !dash->second.intervals.empty();
            }
            if (is_ellipse) {
                if (has_nonempty_dash) {
                    const auto ellipse_geometry =
                        make_ellipse_path_geometry(
                            geometry.first,
                            geometry.second,
                            geometry.third,
                            geometry.fourth);
                    return append_path_strokes(
                        ellipse_geometry,
                        pen,
                        local_transform,
                        effective_transform,
                        brush_index);
                }
                const std::array primitive{
                    progpu_native_geometry_primitive{
                        PROGPU_NATIVE_GEOMETRY_ARC,
                        current.edge_aliased
                            ? static_cast<std::uint32_t>(
                                PROGPU_NATIVE_PRIMITIVE_FLAG_EDGE_ALIASED)
                            : 0U,
                        {static_cast<float>(geometry.first),
                            static_cast<float>(geometry.second)},
                        {static_cast<float>(geometry.third), 0.0F},
                        {0.0F, static_cast<float>(geometry.fourth)},
                        {0.0F, std::numbers::pi_v<float> * 2.0F},
                        static_cast<float>(pen.thickness),
                        0.0F,
                        {1.0F, 1.0F, 1.0F, 1.0F},
                        native_local_transform}};
                const std::array brushes{brush_index};
                return builder.draw_geometry(
                        primitive,
                        brushes,
                        stroke_bounds)
                    ? status::success
                    : status::invalid_graph;
            }
            const bool has_rounded_corners =
                geometry.radius_x > 0.0 && geometry.radius_y > 0.0;
            if (has_rounded_corners) {
                if (has_nonempty_dash ||
                    geometry.radius_x != geometry.radius_y) {
                    const auto rounded_geometry =
                        make_rounded_rectangle_geometry(
                            x,
                            y,
                            width,
                            height,
                            geometry.radius_x,
                            geometry.radius_y);
                    return append_path_strokes(
                        rounded_geometry,
                        pen,
                        local_transform,
                        effective_transform,
                        brush_index);
                }
                const std::array primitive{
                    progpu_native_analytic_primitive{
                        PROGPU_NATIVE_PRIMITIVE_ROUNDED_RECTANGLE,
                        current.edge_aliased
                            ? static_cast<std::uint32_t>(
                                PROGPU_NATIVE_PRIMITIVE_FLAG_EDGE_ALIASED)
                            : 0U,
                        static_cast<float>(x),
                        static_cast<float>(y),
                        static_cast<float>(width),
                        static_cast<float>(height),
                        static_cast<float>(geometry.radius_x),
                        static_cast<float>(pen.thickness),
                        {1.0F, 1.0F, 1.0F, 1.0F},
                        native_local_transform}};
                const std::array brushes{brush_index};
                return builder.draw_analytic(
                        primitive,
                        brushes,
                        stroke_bounds)
                    ? status::success
                    : status::invalid_graph;
            }
            const std::array points{
                progpu_native_point{
                    static_cast<float>(x),
                    static_cast<float>(y)},
                progpu_native_point{
                    static_cast<float>(x + width),
                    static_cast<float>(y)},
                progpu_native_point{
                    static_cast<float>(x + width),
                    static_cast<float>(y + height)},
                progpu_native_point{
                    static_cast<float>(x),
                    static_cast<float>(y + height)}};
            return append_polyline_stroke(
                pen,
                points,
                true,
                brush_index,
                stroke_bounds,
                native_local_transform,
                pen.start_line_cap,
                pen.end_line_cap,
                true);
        };
        // Algorithm: preserve the ordinary MIL collapsed-rectangle outer contour.
        // Time/space: O(1), bounded line/rounded-corner segment storage.
        const auto make_degenerate_rectangle_outline = [&append_rounded_rectangle_path](
            double left, double top, double right, double bottom,
            double radius_x, double radius_y, const pen_state& pen) {
            const double half_thickness = pen.thickness * 0.5;
            std::vector<progpu_native_path_segment> segments;
            const auto append_line = [&segments](
                double x0,
                double y0,
                double x1,
                double y1) {
                progpu_native_path_segment segment{};
                segment.kind = PROGPU_NATIVE_PATH_SEGMENT_LINE;
                segment.p0 = {
                    static_cast<float>(x0), static_cast<float>(y0)};
                segment.p1 = {
                    static_cast<float>(x1), static_cast<float>(y1)};
                segments.push_back(segment);
            };
            if ((radius_x > 0.0 && radius_y > 0.0) ||
                pen.line_join == PROGPU_NATIVE_STROKE_JOIN_ROUND) {
                const double outer_radius_x = radius_x + half_thickness;
                const double outer_radius_y = radius_y + half_thickness;
                const double clamped_radius_x = std::min(
                    outer_radius_x, (right - left) * 0.5);
                const double clamped_radius_y = std::min(
                    outer_radius_y, (bottom - top) * 0.5);
                append_rounded_rectangle_path(
                    segments,
                    left,
                    top,
                    right,
                    bottom,
                    clamped_radius_x,
                    clamped_radius_y);
            } else {
                double bevel_offset = 0.0;
                if (pen.line_join == PROGPU_NATIVE_STROKE_JOIN_BEVEL) {
                    bevel_offset = half_thickness;
                } else {
                    bevel_offset = std::clamp(
                        2.0 - std::numbers::sqrt2_v<double> *
                            pen.miter_limit,
                        0.0,
                        1.0) * half_thickness;
                }
                bevel_offset = std::clamp(
                    bevel_offset,
                    0.0,
                    0.5 * std::min(right - left, bottom - top));
                if (bevel_offset == 0.0) {
                    append_line(left, top, right, top);
                    append_line(right, top, right, bottom);
                    append_line(right, bottom, left, bottom);
                    append_line(left, bottom, left, top);
                } else {
                    append_line(
                        left, top + bevel_offset, left + bevel_offset, top);
                    append_line(
                        left + bevel_offset,
                        top,
                        right - bevel_offset,
                        top);
                    append_line(
                        right - bevel_offset,
                        top,
                        right,
                        top + bevel_offset);
                    append_line(
                        right,
                        top + bevel_offset,
                        right,
                        bottom - bevel_offset);
                    append_line(
                        right,
                        bottom - bevel_offset,
                        right - bevel_offset,
                        bottom);
                    append_line(
                        right - bevel_offset,
                        bottom,
                        left + bevel_offset,
                        bottom);
                    append_line(
                        left + bevel_offset,
                        bottom,
                        left,
                        bottom - bevel_offset);
                    append_line(
                        left,
                        bottom - bevel_offset,
                        left,
                        top + bevel_offset);
                }
            }
            return segments;
        };
        const auto append_degenerate_rectangle_stroke = [
            this,
            &builder,
            &resolve_brush_index,
            &append_polyline_stroke,
            &append_degenerate_cap_stroke,
            &append_path_strokes,
            &make_wpf_rounded_rectangle_geometry,
            &make_degenerate_rectangle_outline,
            &current](
            double x,
            double y,
            double width,
            double height,
            double radius_x,
            double radius_y,
            const pen_state& pen,
            const affine_2d_double& local_transform,
            const affine_2d_double& effective_transform) noexcept {
            if (pen.brush_handle == 0U || pen.thickness == 0.0) {
                return status::success;
            }
            bool has_nonempty_dash = false;
            if (pen.dash_style_handle != 0U) {
                const auto dash = dash_styles.find(pen.dash_style_handle);
                if (dash == dash_styles.end()) {
                    return status::invalid_handle;
                }
                has_nonempty_dash = !dash->second.intervals.empty();
            }
            if (has_nonempty_dash && radius_x == 0.0 &&
                radius_y == 0.0 &&
                width == 0.0 && height == 0.0) {
                const double half_thickness = pen.thickness * 0.5;
                const brush_use_state brush_use{
                    x - half_thickness,
                    y - half_thickness,
                    pen.thickness,
                    pen.thickness,
                    effective_transform};
                std::uint32_t brush_index =
                    PROGPU_NATIVE_SCENE_NO_INDEX;
                const status brush_status = resolve_brush_index(
                    pen.brush_handle,
                    brush_index,
                    &brush_use);
                if (brush_status != status::success) {
                    return brush_status;
                }
                bool emitted = false;
                return append_degenerate_cap_stroke(
                    pen,
                    {static_cast<float>(x), static_cast<float>(y)},
                    brush_index,
                    local_transform,
                    effective_transform,
                    PROGPU_NATIVE_STROKE_CAP_ROUND,
                    PROGPU_NATIVE_STROKE_CAP_ROUND,
                    emitted);
            }
            if (has_nonempty_dash && radius_x > 0.0 &&
                radius_y > 0.0) {
                const auto geometry =
                    make_wpf_rounded_rectangle_geometry(
                        x,
                        y,
                        width,
                        height,
                        radius_x,
                        radius_y);
                pen_state smooth_pen = pen;
                smooth_pen.miter_limit = 1.0;
                return append_path_strokes(
                    geometry,
                    smooth_pen,
                    local_transform,
                    effective_transform,
                    PROGPU_NATIVE_SCENE_NO_INDEX);
            }
            const double half_thickness = pen.thickness * 0.5;
            const double left = x - half_thickness;
            const double top = y - half_thickness;
            const double right = x + width + half_thickness;
            const double bottom = y + height + half_thickness;
            const brush_use_state brush_use{
                left,
                top,
                right - left,
                bottom - top,
                effective_transform};
            std::uint32_t brush_index = PROGPU_NATIVE_SCENE_NO_INDEX;
            const status brush_status = resolve_brush_index(
                pen.brush_handle,
                brush_index,
                &brush_use);
            if (brush_status != status::success) {
                return brush_status;
            }
            progpu_native_image_rect stroke_bounds{};
            if (!try_fixed_shape_stroke_bounds(
                    x,
                    y,
                    width,
                    height,
                    pen.thickness,
                    effective_transform,
                    stroke_bounds)) {
                return status::invalid_graph;
            }
            progpu_native_affine_2d native_local_transform{};
            if (!try_to_native_affine(
                    local_transform,
                    native_local_transform)) {
                return status::invalid_graph;
            }
            if (has_nonempty_dash) {
                const std::array points{
                    progpu_native_point{
                        static_cast<float>(x), static_cast<float>(y)},
                    progpu_native_point{
                        static_cast<float>(x + width),
                        static_cast<float>(y)},
                    progpu_native_point{
                        static_cast<float>(x + width),
                        static_cast<float>(y + height)},
                    progpu_native_point{
                        static_cast<float>(x),
                        static_cast<float>(y + height)}};
                return append_polyline_stroke(
                    pen,
                    points,
                    true,
                    brush_index,
                    stroke_bounds,
                    native_local_transform,
                    pen.start_line_cap,
                    pen.end_line_cap,
                    true);
            }
            const auto segments = make_degenerate_rectangle_outline(
                left, top, right, bottom, radius_x, radius_y, pen);
            const std::array paths{
                progpu_native_scene_path_fill{
                    0U,
                    segments.size(),
                    0U,
                    0U,
                    static_cast<float>(left),
                    static_cast<float>(top),
                    static_cast<float>(right),
                    static_cast<float>(bottom),
                    {1.0F, 1.0F, 1.0F, 1.0F},
                    native_local_transform,
                    PROGPU_NATIVE_FILL_RULE_EVEN_ODD,
                    current.edge_aliased ? 1U : 8U}};
            const std::array brushes{brush_index};
            return builder.draw_paths(
                    paths,
                    segments,
                    brushes,
                    stroke_bounds)
                ? status::success
                : status::invalid_graph;
        };
        const auto resolve_drawing_image_bounds = [
            this,
            &drawing_image_bounds_segments](
            auto&& resolve_bounds,
            std::uint32_t drawing_handle,
            std::uint32_t depth,
            const affine_2d_double& current_transform,
            const progpu_native_image_rect* active_clip,
            progpu_native_image_rect& bounds) noexcept {
            if (depth >= maximum_visual_depth) {
                return status::invalid_graph;
            }
            const auto drawing_group = drawing_groups.find(drawing_handle);
            if (drawing_group != drawing_groups.end()) {
                const auto& group = drawing_group->second;
                if (group.children.empty()) {
                    bounds = {};
                    return status::success;
                }
                affine_2d_double next_transform = current_transform;
                if (group.transform_handle != 0U) {
                    affine_2d_double group_transform{};
                    const status transform_status = resolve_transform(
                        group.transform_handle,
                        group_transform);
                    if (transform_status != status::success) {
                        return transform_status;
                    }
                    next_transform = compose_affine(
                        group_transform,
                        current_transform);
                }
                if (affine_has_zero_area(next_transform)) {
                    bounds = {};
                    return status::success;
                }
                progpu_native_image_rect combined_clip{};
                const progpu_native_image_rect* child_clip = active_clip;
                if (group.clip_geometry_handle != 0U) {
                    drawing_image_bounds_segments.clear();
                    shallow_fill_leaf clip{};
                    const status clip_status = geometry_groups.contains(
                            group.clip_geometry_handle)
                        ? append_group_fill_leaf(
                            group.clip_geometry_handle,
                            drawing_image_bounds_segments,
                            clip,
                            next_transform)
                        : append_shallow_fill_leaf(
                            group.clip_geometry_handle,
                            drawing_image_bounds_segments,
                            clip,
                            next_transform);
                    if (clip_status != status::success) {
                        return clip_status;
                    }
                    if (!clip.has_bounds || clip.right <= clip.left ||
                        clip.bottom <= clip.top) {
                        bounds = {};
                        return status::success;
                    }
                    double clip_left = clip.left;
                    double clip_top = clip.top;
                    double clip_right = clip.right;
                    double clip_bottom = clip.bottom;
                    if (active_clip != nullptr) {
                        clip_left = std::max(
                            clip_left,
                            double{active_clip->x});
                        clip_top = std::max(
                            clip_top,
                            double{active_clip->y});
                        clip_right = std::min(
                            clip_right,
                            double{active_clip->x + active_clip->width});
                        clip_bottom = std::min(
                            clip_bottom,
                            double{active_clip->y + active_clip->height});
                    }
                    if (clip_right <= clip_left ||
                        clip_bottom <= clip_top ||
                        !finite_double_as_float(clip_left) ||
                        !finite_double_as_float(clip_top) ||
                        !finite_double_as_float(clip_right - clip_left) ||
                        !finite_double_as_float(clip_bottom - clip_top)) {
                        bounds = {};
                        return status::success;
                    }
                    combined_clip = {
                        static_cast<float>(clip_left),
                        static_cast<float>(clip_top),
                        static_cast<float>(clip_right - clip_left),
                        static_cast<float>(clip_bottom - clip_top)};
                    child_clip = &combined_clip;
                }
                double left = 0.0;
                double top = 0.0;
                double right = 0.0;
                double bottom = 0.0;
                bool has_bounds = false;
                for (const std::uint32_t child : group.children) {
                    progpu_native_image_rect child_bounds{};
                    const status child_status = resolve_bounds(
                        resolve_bounds,
                        child,
                        depth + 1U,
                        next_transform,
                        child_clip,
                        child_bounds);
                    if (child_status != status::success) {
                        return child_status;
                    }
                    if (child_bounds.width <= 0.0F ||
                        child_bounds.height <= 0.0F) {
                        continue;
                    }
                    double child_left = child_bounds.x;
                    double child_top = child_bounds.y;
                    double child_right = child_left +
                        child_bounds.width;
                    double child_bottom = child_top +
                        child_bounds.height;
                    if (!has_bounds) {
                        left = child_left;
                        top = child_top;
                        right = child_right;
                        bottom = child_bottom;
                        has_bounds = true;
                    } else {
                        left = std::min(left, child_left);
                        top = std::min(top, child_top);
                        right = std::max(right, child_right);
                        bottom = std::max(bottom, child_bottom);
                    }
                }
                if (!has_bounds || right <= left || bottom <= top) {
                    bounds = {};
                    return status::success;
                }
                bounds = {
                    static_cast<float>(left),
                    static_cast<float>(top),
                    static_cast<float>(right - left),
                    static_cast<float>(bottom - top)};
                return status::success;
            }
            const auto finish_bounds = [&bounds, active_clip]() noexcept {
                if (bounds.width <= 0.0F || bounds.height <= 0.0F) {
                    bounds = {};
                    return status::success;
                }
                if (active_clip == nullptr) {
                    return status::success;
                }
                const float left = std::max(bounds.x, active_clip->x);
                const float top = std::max(bounds.y, active_clip->y);
                const float right = std::min(
                    bounds.x + bounds.width,
                    active_clip->x + active_clip->width);
                const float bottom = std::min(
                    bounds.y + bounds.height,
                    active_clip->y + active_clip->height);
                if (right <= left || bottom <= top) {
                    bounds = {};
                    return status::success;
                }
                bounds = {left, top, right - left, bottom - top};
                return status::success;
            };
            const auto image_drawing = image_drawings.find(drawing_handle);
            if (image_drawing != image_drawings.end()) {
                auto image = image_drawing->second;
                const status rectangle_status = resolve_animated_rect(
                    image.x,
                    image.y,
                    image.width,
                    image.height,
                    image.rect_animation_handle,
                    image.x,
                    image.y,
                    image.width,
                    image.height);
                if (rectangle_status != status::success) {
                    return rectangle_status;
                }
                if (image.image_source_handle == 0U ||
                    image.width <= 0.0 || image.height <= 0.0) {
                    bounds = {};
                    return status::success;
                }
                if (!try_transform_bounds(
                        image.x,
                        image.y,
                        image.width,
                        image.height,
                        current_transform,
                        bounds)) {
                    return status::unsupported_command;
                }
                return finish_bounds();
            }
            const auto video_drawing = video_drawings.find(drawing_handle);
            if (video_drawing != video_drawings.end()) {
                auto video = video_drawing->second;
                const status rectangle_status = resolve_animated_rect(
                    video.x,
                    video.y,
                    video.width,
                    video.height,
                    video.rect_animation_handle,
                    video.x,
                    video.y,
                    video.width,
                    video.height);
                if (rectangle_status != status::success) {
                    return rectangle_status;
                }
                if (video.player_handle == 0U ||
                    video.width <= 0.0 || video.height <= 0.0) {
                    bounds = {};
                    return status::success;
                }
                if (!try_transform_bounds(
                        video.x,
                        video.y,
                        video.width,
                        video.height,
                        current_transform,
                        bounds)) {
                    return status::unsupported_command;
                }
                return finish_bounds();
            }
            const auto glyph_drawing = glyph_run_drawings.find(
                drawing_handle);
            if (glyph_drawing != glyph_run_drawings.end()) {
                if (glyph_drawing->second.foreground_brush_handle == 0U ||
                    glyph_drawing->second.glyph_run_handle == 0U) {
                    bounds = {};
                    return status::success;
                }
                const auto glyph_run = glyph_runs.find(
                    glyph_drawing->second.glyph_run_handle);
                if (glyph_run == glyph_runs.end()) {
                    return status::invalid_handle;
                }
                if (glyph_run->second.bounds_width <= 0.0 ||
                    glyph_run->second.bounds_height <= 0.0) {
                    bounds = {};
                    return status::success;
                }
                if (!try_transform_bounds(
                        glyph_run->second.bounds_x,
                        glyph_run->second.bounds_y,
                        glyph_run->second.bounds_width,
                        glyph_run->second.bounds_height,
                        current_transform,
                        bounds)) {
                    return status::unsupported_command;
                }
                return finish_bounds();
            }
            const auto drawing = geometry_drawings.find(drawing_handle);
            if (drawing == geometry_drawings.end() ||
                drawing->second.geometry_handle == 0U) {
                return status::unsupported_command;
            }
            if (drawing->second.pen_handle != 0U) {
                const auto fixed = fixed_geometries.find(
                    drawing->second.geometry_handle);
                if (fixed == fixed_geometries.end()) {
                    return status::unsupported_command;
                }
                fixed_geometry_state geometry{};
                const status geometry_status = resolve_fixed_geometry(
                    drawing->second.geometry_handle, geometry);
                if (geometry_status != status::success) {
                    return geometry_status;
                }
                pen_state pen{};
                const status pen_status = resolve_pen(
                    drawing->second.pen_handle, pen);
                if (pen_status != status::success) {
                    return pen_status;
                }
                if (pen.brush_handle == 0U || pen.thickness <= 0.0) {
                    return status::unsupported_command;
                }
                if (pen.dash_style_handle != 0U) {
                    const auto dash = dash_styles.find(
                        pen.dash_style_handle);
                    if (dash == dash_styles.end()) {
                        return status::invalid_handle;
                    }
                    if (!dash->second.intervals.empty()) {
                        return status::unsupported_command;
                    }
                }
                affine_2d_double geometry_transform{};
                if (geometry.transform_handle != 0U) {
                    const status transform_status = resolve_transform(
                        geometry.transform_handle, geometry_transform);
                    if (transform_status != status::success) {
                        return transform_status;
                    }
                }
                if (geometry.kind != fixed_geometry_kind::line) {
                    const bool is_ellipse =
                        geometry.kind == fixed_geometry_kind::ellipse;
                    const double shape_x = is_ellipse
                        ? geometry.first - geometry.third
                        : geometry.first;
                    const double shape_y = is_ellipse
                        ? geometry.second - geometry.fourth
                        : geometry.second;
                    const double shape_width = is_ellipse
                        ? geometry.third * 2.0
                        : geometry.third;
                    const double shape_height = is_ellipse
                        ? geometry.fourth * 2.0
                        : geometry.fourth;
                    const bool has_rounded_corners =
                        geometry.kind == fixed_geometry_kind::rectangle &&
                        geometry.radius_x > 0.0 && geometry.radius_y > 0.0;
                    if (is_ellipse && shape_width > 0.0 &&
                        shape_height > 0.0) {
                        if (!try_transformed_ellipse_stroke_bounds(
                                geometry.first,
                                geometry.second,
                                geometry.third,
                                geometry.fourth,
                                pen.thickness,
                                geometry_transform,
                                current_transform,
                                bounds) ||
                            bounds.width <= 0.0F || bounds.height <= 0.0F) {
                            return status::unsupported_command;
                        }
                        return finish_bounds();
                    }
                    if (has_rounded_corners && shape_width > 0.0 &&
                        shape_height > 0.0) {
                        if (!try_transformed_rounded_rectangle_stroke_bounds(
                                shape_x,
                                shape_y,
                                shape_width,
                                shape_height,
                                geometry.radius_x,
                                geometry.radius_y,
                                pen.thickness,
                                geometry_transform,
                                current_transform,
                                bounds) ||
                            bounds.width <= 0.0F || bounds.height <= 0.0F) {
                            return status::unsupported_command;
                        }
                        return finish_bounds();
                    }
                    if (geometry.kind == fixed_geometry_kind::rectangle &&
                        !has_rounded_corners && shape_width > 0.0 &&
                        shape_height > 0.0) {
                        // Geometry.Transform changes the closed spine before
                        // WPF widens it; DrawingGroup/world state transforms
                        // the completed strip and joins afterward.
                        if (!try_transformed_rectangle_stroke_bounds(
                                shape_x,
                                shape_y,
                                shape_width,
                                shape_height,
                                pen.thickness,
                                pen.line_join,
                                pen.miter_limit,
                                geometry_transform,
                                current_transform,
                                bounds) ||
                            bounds.width <= 0.0F || bounds.height <= 0.0F) {
                            return status::unsupported_command;
                        }
                        return finish_bounds();
                    }
                    const affine_2d_double transform = compose_affine(
                        geometry_transform,
                        current_transform);
                    if (affine_has_zero_area(transform) ||
                        !affine_preserves_axis_alignment(transform)) {
                        return status::unsupported_command;
                    }
                    if (!try_fixed_shape_stroke_bounds(
                            shape_x,
                            shape_y,
                            shape_width,
                            shape_height,
                            pen.thickness,
                            transform,
                            bounds) ||
                        bounds.width <= 0.0F || bounds.height <= 0.0F) {
                        return status::unsupported_command;
                    }
                    return finish_bounds();
                }
                // WPF transforms the spine by Geometry.Transform before pen
                // widening, then applies the DrawingGroup/world transform to
                // the widened stroke. Keep those matrices separate.
                const double x0 = geometry.first * geometry_transform.m11 +
                    geometry.second * geometry_transform.m21 +
                    geometry_transform.m31;
                const double y0 = geometry.first * geometry_transform.m12 +
                    geometry.second * geometry_transform.m22 +
                    geometry_transform.m32;
                const double x1 = geometry.third * geometry_transform.m11 +
                    geometry.fourth * geometry_transform.m21 +
                    geometry_transform.m31;
                const double y1 = geometry.third * geometry_transform.m12 +
                    geometry.fourth * geometry_transform.m22 +
                    geometry_transform.m32;
                if (!try_transformed_line_stroke_bounds(
                        x0,
                        y0,
                        x1,
                        y1,
                        pen.thickness,
                        pen.start_line_cap,
                        pen.end_line_cap,
                        current_transform,
                        bounds) ||
                    bounds.width <= 0.0F || bounds.height <= 0.0F) {
                    return status::unsupported_command;
                }
                return finish_bounds();
            }
            if (drawing->second.brush_handle == 0U) {
                return status::unsupported_command;
            }
            const auto group = geometry_groups.find(
                drawing->second.geometry_handle);
            if (group != geometry_groups.end()) {
                std::uint32_t child_handle =
                    drawing->second.geometry_handle;
                for (std::uint32_t geometry_depth = 0U;
                     geometry_depth < maximum_visual_depth;
                     ++geometry_depth) {
                    const auto child_group = geometry_groups.find(
                        child_handle);
                    if (child_group == geometry_groups.end()) {
                        break;
                    }
                    if (child_group->second.children.size() != 1U) {
                        return status::unsupported_command;
                    }
                    child_handle = child_group->second.children.front();
                }
                if (geometry_groups.contains(child_handle)) {
                    return status::invalid_graph;
                }
            }
            drawing_image_bounds_segments.clear();
            shallow_fill_leaf leaf{};
            const status geometry_status = group != geometry_groups.end()
                ? append_group_fill_leaf(
                    drawing->second.geometry_handle,
                    drawing_image_bounds_segments,
                    leaf,
                    current_transform)
                : append_shallow_fill_leaf(
                    drawing->second.geometry_handle,
                    drawing_image_bounds_segments,
                    leaf,
                    current_transform);
            if (geometry_status != status::success) {
                return geometry_status;
            }
            if (!leaf.has_bounds || leaf.right <= leaf.left ||
                leaf.bottom <= leaf.top) {
                bounds = {};
                return status::success;
            }
            if (!finite_double_as_float(leaf.left) ||
                !finite_double_as_float(leaf.top) ||
                !finite_double_as_float(leaf.right - leaf.left) ||
                !finite_double_as_float(leaf.bottom - leaf.top)) {
                return status::unsupported_command;
            }
            bounds = {
                static_cast<float>(leaf.left),
                static_cast<float>(leaf.top),
                static_cast<float>(leaf.right - leaf.left),
                static_cast<float>(leaf.bottom - leaf.top)};
            return finish_bounds();
        };
        const auto append_drawing_image = [
            this,
            drawing_depth,
            &builder,
            &brush_indices,
            &image_indices,
            &glyph_resources,
            &active_drawings,
            &clip_paths,
            &clip_segments,
            &clip_boolean_nodes,
            &apply_rectangle_clip,
            &save_state,
            &resolve_drawing_image_bounds,
            compile_context,
            &metrics](
            std::uint32_t image_source_handle,
            double x,
            double y,
            double width,
            double height,
            const render_scope_state& state) {
            const auto drawing_image = drawing_images.find(
                image_source_handle);
            if (drawing_image == drawing_images.end()) {
                return status::invalid_handle;
            }
            const auto& source = drawing_image->second;
            if (source.drawing_handle == 0U) {
                return status::success;
            }
            double source_x = source.bounds_x;
            double source_y = source.bounds_y;
            double source_width = source.bounds_width;
            double source_height = source.bounds_height;
            if (!source.has_bounds) {
                progpu_native_image_rect inferred_bounds{};
                const affine_2d_double identity{};
                const status bounds_status = resolve_drawing_image_bounds(
                    resolve_drawing_image_bounds,
                    source.drawing_handle,
                    0U,
                    identity,
                    nullptr,
                    inferred_bounds);
                if (bounds_status != status::success) {
                    return bounds_status;
                }
                if (inferred_bounds.width <= 0.0F ||
                    inferred_bounds.height <= 0.0F) {
                    return status::success;
                }
                source_x = inferred_bounds.x;
                source_y = inferred_bounds.y;
                source_width = inferred_bounds.width;
                source_height = inferred_bounds.height;
            }
            if (!active_drawings.insert(image_source_handle).second) {
                return status::invalid_graph;
            }
            render_scope_state next = state;
            const status clip_status = apply_rectangle_clip(
                x,
                y,
                width,
                height,
                state,
                next);
            if (clip_status != status::success) {
                active_drawings.erase(image_source_handle);
                return clip_status;
            }
            const affine_2d_double mapping{
                width / source_width,
                0.0,
                0.0,
                height / source_height,
                x - source_x * width / source_width,
                y - source_y * height / source_height};
            next.transform = compose_affine(mapping, state.transform);
            if (!save_state(next)) {
                active_drawings.erase(image_source_handle);
                return status::invalid_graph;
            }
            const status image_status = append_render_stream(
                source.child_render_data,
                nullptr,
                next,
                drawing_depth + 1U,
                builder,
                brush_indices,
                image_indices,
                glyph_resources,
                compile_context,
                active_drawings,
                clip_paths,
                clip_segments,
                clip_boolean_nodes,
                metrics);
            const bool restored = builder.restore();
            active_drawings.erase(image_source_handle);
            if (image_status != status::success) {
                return image_status;
            }
            return restored ? status::success : status::invalid_graph;
        };
        const auto append_bitmap_source = [
            this,
            &builder,
            &image_indices,
            &append_drawing_image](
            std::uint32_t image_source_handle,
            double x,
            double y,
            double width,
            double height,
            const render_scope_state& state) {
            if (image_source_handle == 0U || width == 0.0 || height == 0.0) {
                return status::success;
            }
            const auto bitmap = bitmap_sources.find(image_source_handle);
            if (bitmap == bitmap_sources.end()) {
                const auto d3d_image = d3d_images.find(image_source_handle);
                if (d3d_image != d3d_images.end()) {
                    if (!d3d_image->second.has_external_image) {
                        return status::invalid_handle;
                    }
                    const auto image = image_indices.find(image_source_handle);
                    if (image == image_indices.end()) {
                        return status::invalid_handle;
                    }
                    progpu_native_affine_2d native_transform{};
                    progpu_native_image_rect bounds{};
                    if (!try_to_native_affine(
                            state.transform, native_transform) ||
                        !try_transform_bounds(
                            x,
                            y,
                            width,
                            height,
                            state.transform,
                            bounds)) {
                        return status::invalid_graph;
                    }
                    const auto& descriptor = d3d_image->second;
                    const progpu_native_scene_image_draw image_draw{
                        sizeof(progpu_native_scene_image_draw),
                        0U,
                        descriptor.width,
                        descriptor.height,
                        descriptor.width * 4U,
                        state.image_sampling,
                        {0.0F,
                         0.0F,
                         static_cast<float>(descriptor.width),
                         static_cast<float>(descriptor.height)},
                        {static_cast<float>(x),
                         static_cast<float>(y),
                         static_cast<float>(width),
                         static_cast<float>(height)},
                        native_transform,
                        1.0F,
                        1U};
                    const progpu_native_scene_image_sampling_options
                        cubic_options{
                            sizeof(
                                progpu_native_scene_image_sampling_options),
                            0U,
                            1.0F / 3.0F,
                            1.0F / 3.0F};
                    const auto* sampling_options = state.image_sampling ==
                        PROGPU_NATIVE_IMAGE_SAMPLING_CUBIC
                        ? &cubic_options
                        : nullptr;
                    return builder.draw_image(
                            image->second,
                            image_draw,
                            bounds,
                            PROGPU_NATIVE_SCENE_NO_INDEX,
                            sampling_options)
                        ? status::success
                        : status::invalid_graph;
                }
                const auto source = resources.find(image_source_handle);
                return source != resources.end() &&
                    source->second.type == type_drawing_image
                    ? append_drawing_image(
                        image_source_handle,
                        x,
                        y,
                        width,
                        height,
                        state)
                    : status::invalid_handle;
            }
            std::uint32_t image_index = PROGPU_NATIVE_SCENE_NO_INDEX;
            const auto existing = image_indices.find(image_source_handle);
            if (existing != image_indices.end()) {
                image_index = existing->second;
            } else {
                if (bitmap->second.external_image) {
                    const auto external = image_indices.find(
                        image_source_handle);
                    if (external == image_indices.end()) {
                        return status::invalid_handle;
                    }
                    image_index = external->second;
                } else {
                    if (!builder.add_rgba8_image(
                            bitmap->second.width,
                            bitmap->second.height,
                            bitmap->second.row_bytes,
                            bitmap->second.pixels,
                            image_index)) {
                        return status::invalid_graph;
                    }
                    image_indices.emplace(image_source_handle, image_index);
                }
            }
            progpu_native_affine_2d native_transform{};
            progpu_native_image_rect bounds{};
            if (!try_to_native_affine(state.transform, native_transform) ||
                !try_transform_bounds(
                    x,
                    y,
                    width,
                    height,
                    state.transform,
                    bounds)) {
                return status::invalid_graph;
            }
            const progpu_native_scene_image_draw image_draw{
                sizeof(progpu_native_scene_image_draw),
                0U,
                bitmap->second.width,
                bitmap->second.height,
                bitmap->second.row_bytes,
                state.image_sampling,
                {0.0F,
                 0.0F,
                 static_cast<float>(bitmap->second.width),
                 static_cast<float>(bitmap->second.height)},
                {static_cast<float>(x),
                 static_cast<float>(y),
                 static_cast<float>(width),
                 static_cast<float>(height)},
                native_transform,
                1.0F,
                1U};
            const progpu_native_scene_image_sampling_options cubic_options{
                sizeof(progpu_native_scene_image_sampling_options),
                0U,
                1.0F / 3.0F,
                1.0F / 3.0F};
            const auto* sampling_options = state.image_sampling ==
                PROGPU_NATIVE_IMAGE_SAMPLING_CUBIC
                ? &cubic_options
                : nullptr;
            return builder.draw_image(
                    image_index,
                    image_draw,
                    bounds,
                    PROGPU_NATIVE_SCENE_NO_INDEX,
                    sampling_options)
                ? status::success
                : status::invalid_graph;
        };
        // A single tile is source content mapped into its viewport,
        // then clipped to the independently transformed paint geometry. This
        // reuses image batching and exact MIL clips; it never copies pixels to
        // synthesize padding or broadens a transformed viewport to loose bounds.
        const auto append_single_tile_brush = [
            this, &builder, &append_bitmap_source, &apply_rectangle_clip,
            &save_state, &resolve_drawing_image_bounds, drawing_depth,
            &brush_indices, &image_indices, &glyph_resources, compile_context,
            &active_drawings, &clip_paths, &clip_segments, &clip_boolean_nodes,
            &metrics](
            std::uint32_t brush_handle,
            const brush_use_state& use,
            const render_scope_state& state) -> status {
            const auto found = tile_brushes.find(brush_handle);
            const bool drawing_brush = require_resource(brush_handle, type_drawing_brush);
            const bool visual_brush = require_resource(brush_handle, type_visual_brush);
            if (found == tile_brushes.end() ||
                (!require_resource(brush_handle, type_image_brush) && !drawing_brush && !visual_brush)) {
                return status::unsupported_command;
            }
            const auto& brush = found->second;
            if (brush.source_handle == 0U) {
                return status::success;
            }
            const bool repeated = brush.tile_mode != 0U;
            if (brush.tile_mode > 4U || (repeated && (compile_context == nullptr ||
                (state.image_sampling != PROGPU_NATIVE_IMAGE_SAMPLING_NEAREST &&
                 state.image_sampling != PROGPU_NATIVE_IMAGE_SAMPLING_LINEAR &&
                 state.image_sampling != PROGPU_NATIVE_IMAGE_SAMPLING_FANT)))) {
                return status::unsupported_command;
            }
            double content_x = 0.0;
            double content_y = 0.0;
            double content_width = 0.0;
            double content_height = 0.0;
            bool vector_source = drawing_brush || visual_brush;
            if (visual_brush) {
                if (compile_context == nullptr) return status::unsupported_command;
                const auto visual = visuals.find(brush.source_handle);
                if (visual == visuals.end()) return status::invalid_handle;
                // Source-built Visual owns exact descendant bounds. Never infer
                // a visual's extent by scraping UI properties or using viewport
                // bounds as a substitute for missing content geometry.
                if (!visual->second.has_cache_bounds) return status::unsupported_command;
                content_x = visual->second.cache_bounds_x;
                content_y = visual->second.cache_bounds_y;
                content_width = visual->second.cache_bounds_width;
                content_height = visual->second.cache_bounds_height;
            } else if (drawing_brush) {
                progpu_native_image_rect bounds{};
                const status resolved = resolve_drawing_image_bounds(
                    resolve_drawing_image_bounds, brush.source_handle, 0U, {}, nullptr, bounds);
                if (resolved != status::success) return resolved;
                content_x = bounds.x;
                content_y = bounds.y;
                content_width = bounds.width;
                content_height = bounds.height;
            } else if (const auto bitmap = bitmap_sources.find(brush.source_handle);
                bitmap != bitmap_sources.end()) {
                content_width = static_cast<double>(bitmap->second.width) * 96.0 / bitmap->second.dpi_x;
                content_height = static_cast<double>(bitmap->second.height) * 96.0 / bitmap->second.dpi_y;
            } else if (const auto shared = d3d_images.find(brush.source_handle);
                shared != d3d_images.end()) {
                if (!shared->second.has_external_image) return status::invalid_handle;
                content_width = shared->second.width;
                content_height = shared->second.height;
            } else if (const auto image = drawing_images.find(brush.source_handle);
                image != drawing_images.end()) {
                if (image->second.drawing_handle == 0U) return status::success;
                vector_source = true;
                if (image->second.has_bounds) {
                    content_width = image->second.bounds_width;
                    content_height = image->second.bounds_height;
                } else {
                    progpu_native_image_rect bounds{};
                    const status resolved = resolve_drawing_image_bounds(
                        resolve_drawing_image_bounds, image->second.drawing_handle, 0U, {}, nullptr, bounds);
                    if (resolved != status::success) return resolved;
                    content_width = bounds.width;
                    content_height = bounds.height;
                }
                // DrawingImage has a zero-origin natural image extent. Its
                // drawing bounds origin is removed by append_drawing_image.
            } else {
                return status::unsupported_command;
            }
            if (content_width <= 0.0 || content_height <= 0.0) return status::success;
            rect_resource_state viewport{};
            rect_resource_state viewbox{};
            const auto resolve_rect = [this](const rect_resource_state& base,
                std::uint32_t animation, rect_resource_state& value) {
                return resolve_animated_rect(base.x, base.y, base.width, base.height,
                    animation, value.x, value.y, value.width, value.height);
            };
            const status viewport_status = resolve_rect(
                brush.viewport, brush.viewport_animation, viewport);
            const status viewbox_status = resolve_rect(
                brush.viewbox, brush.viewbox_animation, viewbox);
            if (viewport_status != status::success) return viewport_status;
            if (viewbox_status != status::success) return viewbox_status;
            if (viewport.width <= 0.0 || viewport.height <= 0.0 ||
                viewbox.width <= 0.0 || viewbox.height <= 0.0) {
                return status::success;
            }
            if (brush.viewport_units == 1U) {
                viewport = {use.x + viewport.x * use.width,
                    use.y + viewport.y * use.height,
                    viewport.width * use.width, viewport.height * use.height};
            }
            if (brush.viewbox_units == 1U) {
                viewbox = {content_x + viewbox.x * content_width, content_y + viewbox.y * content_height,
                    viewbox.width * content_width, viewbox.height * content_height};
            }
            double scale_x = viewport.width / viewbox.width;
            double scale_y = viewport.height / viewbox.height;
            if (brush.stretch == 0U) {
                scale_x = scale_y = 1.0;
            } else if (brush.stretch == 2U) {
                scale_x = scale_y = std::min(scale_x, scale_y);
            } else if (brush.stretch == 3U) {
                scale_x = scale_y = std::max(scale_x, scale_y);
            }
            const affine_2d_double content_to_viewport{
                scale_x, 0.0, 0.0, scale_y,
                viewport.x + (viewport.width - viewbox.width * scale_x) *
                    static_cast<double>(brush.alignment_x) * 0.5 - viewbox.x * scale_x,
                viewport.y + (viewport.height - viewbox.height * scale_y) *
                    static_cast<double>(brush.alignment_y) * 0.5 - viewbox.y * scale_y};
            affine_2d_double brush_transform{};
            if (brush.relative_transform_handle != 0U) {
                affine_2d_double relative{};
                const status resolved = resolve_transform(brush.relative_transform_handle, relative);
                if (resolved != status::success) return resolved;
                const affine_2d_double to_relative{1.0 / use.width, 0.0, 0.0,
                    1.0 / use.height, -use.x / use.width, -use.y / use.height};
                const affine_2d_double from_relative{use.width, 0.0, 0.0,
                    use.height, use.x, use.y};
                brush_transform = compose_affine(compose_affine(to_relative, relative), from_relative);
            }
            if (brush.transform_handle != 0U) {
                affine_2d_double absolute{};
                const status resolved = resolve_transform(brush.transform_handle, absolute);
                if (resolved != status::success) return resolved;
                brush_transform = compose_affine(brush_transform, absolute);
            }
            const auto tile_to_target = compose_affine(brush_transform, use.effective_transform);
            const auto content_to_target = compose_affine(content_to_viewport, tile_to_target);
            progpu_native_affine_2d validated{};
            if (!try_to_native_affine(content_to_target, validated) ||
                !finite_double_as_float(content_width) || !finite_double_as_float(content_height)) {
                return status::unsupported_command;
            }
            if (affine_has_zero_area(content_to_target)) return status::success;
            double opacity{};
            const status opacity_status = resolve_animated_double(
                brush.opacity, brush.opacity_animation, opacity);
            if (opacity_status != status::success) return opacity_status;
            if (!std::isfinite(opacity) || opacity < 0.0 || opacity > 1.0) {
                return status::invalid_graph;
            }
            render_scope_state paint = state;
            paint.transform = use.effective_transform;
            render_scope_state clipped = paint;
            const status paint_clip = apply_rectangle_clip(
                use.x, use.y, use.width, use.height, paint, clipped);
            if (paint_clip != status::success) return paint_clip;
            if (!repeated) {
                render_scope_state tile = clipped;
                tile.transform = tile_to_target;
                clipped = tile;
                const status tile_clip = apply_rectangle_clip(viewport.x, viewport.y,
                    viewport.width, viewport.height, tile, clipped);
                if (tile_clip != status::success) return tile_clip;
            }
            // WPF Viewbox selects the mapping, not a source clip. Content
            // outside it may remain visible until the Viewport clips it.
            clipped.transform = content_to_target;
            clipped.opacity *= opacity;
            if (vector_source || repeated) {
                if (active_drawings.size() >= maximum_visual_depth ||
                    !active_drawings.insert(brush_handle).second) return status::invalid_graph;
                // A vector source may contain overlapping primitives. Brush
                // opacity and exact output masks apply once to the completed
                // tile, not separately to each primitive. Source effects keep
                // their own input bounds; viewport clipping is at restoration.
                progpu_native_scene_layer layer{};
                layer.struct_size = sizeof(layer);
                layer.flags = PROGPU_NATIVE_SCENE_LAYER_FORCE_ISOLATION;
                layer.opacity = static_cast<float>(clipped.opacity);
                layer.blend_mode = PROGPU_NATIVE_BLEND_SRC_OVER;
                layer.effect_resource_index = PROGPU_NATIVE_SCENE_NO_INDEX;
                status final_clip = status::success;
                if (repeated) {
                    // The captured page occupies whole physical texels, even
                    // when the mapped viewport has fractional dimensions.
                    const double dpi = static_cast<float>(compile_context->request.dpi_scale_x);
                    if (compile_context->request.dpi_scale_x != compile_context->request.dpi_scale_y ||
                        !std::isfinite(dpi) || dpi <= 0.0) {
                        active_drawings.erase(brush_handle);
                        return status::unsupported_command;
                    }
                    const double pixels_x = std::ceil(viewport.width * dpi *
                        std::hypot(tile_to_target.m11, tile_to_target.m12));
                    const double pixels_y = std::ceil(viewport.height * dpi *
                        std::hypot(tile_to_target.m21, tile_to_target.m22));
                    const double page_width = pixels_x / dpi;
                    const double page_height = pixels_y / dpi;
                    affine_2d_double inverse{};
                    if (!finite_double_as_float(page_width) || !finite_double_as_float(page_height) ||
                        page_width <= 0.0 || page_height <= 0.0 ||
                        !try_invert_affine(tile_to_target, inverse)) {
                        active_drawings.erase(brush_handle);
                        return status::unsupported_command;
                    }
                    float bound_width = static_cast<float>(page_width);
                    float bound_height = static_cast<float>(page_height);
                    // Match the executor's ceil(float logical extent * float
                    // DPI), avoiding a spurious transparent pool texel after
                    // rounding an exact pixel extent up to float.
                    if (std::ceil(static_cast<double>(bound_width) * dpi) > pixels_x)
                        bound_width = std::nextafter(bound_width, 0.0F);
                    if (std::ceil(static_cast<double>(bound_height) * dpi) > pixels_y)
                        bound_height = std::nextafter(bound_height, 0.0F);
                    if (pixels_x > 16777216.0 || pixels_y > 16777216.0 ||
                        std::ceil(static_cast<double>(bound_width) * dpi) != pixels_x ||
                        std::ceil(static_cast<double>(bound_height) * dpi) != pixels_y) {
                        active_drawings.erase(brush_handle);
                        return status::unsupported_command;
                    }
                    const affine_2d_double normalize{1.0 / viewport.width, 0.0, 0.0,
                        1.0 / viewport.height, -viewport.x / viewport.width, -viewport.y / viewport.height};
                    progpu_native_affine_2d inverse_tile{};
                    progpu_native_image_rect output{};
                    if (!try_to_native_affine(compose_affine(inverse, normalize), inverse_tile) ||
                        !try_transform_bounds(use.x, use.y, use.width, use.height, use.effective_transform, output)) {
                        active_drawings.erase(brush_handle);
                        return status::invalid_graph;
                    }
                    progpu_native_scene_tile_composite tile_composite{sizeof(tile_composite),
                        brush.tile_mode == 1U || brush.tile_mode == 3U ? 2U : 1U,
                        brush.tile_mode == 2U || brush.tile_mode == 3U ? 2U : 1U, 0U,
                        output.x, output.y, output.width, output.height,
                        inverse_tile.m11, inverse_tile.m12, inverse_tile.m21,
                        inverse_tile.m22, inverse_tile.m31, inverse_tile.m32, 0U, 0U};
                    auto composite = native::semantic_scene_builder::identity_state();
                    if (clipped.has_clip) {
                        composite.flags = PROGPU_NATIVE_SCENE_STATE_CLIP_RECT;
                        composite.clip_rect = clipped.clip_rect;
                    }
                    layer.flags = PROGPU_NATIVE_SCENE_LAYER_BOUNDS |
                        PROGPU_NATIVE_SCENE_LAYER_CACHE_CONTENT | PROGPU_NATIVE_SCENE_LAYER_CACHE_LOCAL_SPACE |
                        PROGPU_NATIVE_SCENE_LAYER_CACHE_TILE;
                    if (state.image_sampling == PROGPU_NATIVE_IMAGE_SAMPLING_NEAREST)
                        layer.flags |= PROGPU_NATIVE_SCENE_LAYER_CACHE_NEAREST;
                    else if (state.image_sampling == PROGPU_NATIVE_IMAGE_SAMPLING_FANT)
                        layer.flags |= PROGPU_NATIVE_SCENE_LAYER_CACHE_FANT;
                    layer.bounds = {0.0F, 0.0F, bound_width, bound_height};
                    layer.mask_resource_index = clipped.mask_resource_index;
                    const affine_2d_double viewport_to_page{page_width / viewport.width, 0.0, 0.0,
                        page_height / viewport.height, -viewport.x * page_width / viewport.width,
                        -viewport.y * page_height / viewport.height};
                    clipped.transform = compose_affine(content_to_viewport, viewport_to_page);
                    std::uint64_t mapping = 14695981039346656037ULL;
                    append_fnv1a64(mapping, std::uint32_t{0x54494C45U});
                    append_fnv1a64(mapping, compile_context->scene_id);
                    append_fnv1a64(mapping, brush_handle);
                    append_fnv1a64(mapping, dpi);
                    append_fnv1a64(mapping, page_width);
                    append_fnv1a64(mapping, page_height);
                    append_fnv1a64(mapping, clipped.transform);
                    append_fnv1a64(mapping, content_width);
                    append_fnv1a64(mapping, content_height);
                    append_fnv1a64(mapping, state.image_sampling);
                    append_fnv1a64(mapping, state.edge_aliased);
                    append_fnv1a64(mapping, state.clear_type_enabled);
                    append_fnv1a64(mapping, state.subpixel_text_disabled);
                    append_fnv1a64(mapping, state.text_rendering_mode);
                    append_fnv1a64(mapping, state.text_hinting_mode);
                    layer.composite_revision = finish_nonzero_hash(mapping);
                    std::unordered_set<std::uint32_t> revision_resources;
                    final_clip = append_cache_resource_revision(brush.source_handle, revision_resources, mapping);
                    layer.content_revision = finish_nonzero_hash(mapping);
                    if (final_clip == status::success &&
                        (!builder.add_state(composite, layer.reserved0) ||
                         !builder.add_tile_composite(tile_composite, layer.reserved1))) {
                        final_clip = status::invalid_graph;
                    }
                } else {
                    final_clip = attach_visual_output_clip(layer, clipped, builder);
                }
                if (final_clip != status::success || !builder.push_layer(layer)) {
                    active_drawings.erase(brush_handle);
                    return final_clip != status::success ? final_clip : status::invalid_graph;
                }
                render_scope_state content = clipped;
                content.opacity = 1.0;
                content.has_clip = false;
                content.clip_rect = {};
                content.mask_resource_index = PROGPU_NATIVE_SCENE_NO_INDEX;
                content.clip_path_count = content.clip_segment_count = content.clip_boolean_node_count = 0U;
                content.guideline_resource_index = PROGPU_NATIVE_SCENE_NO_INDEX;
                content.per_point_guidelines = false;
                // Isolated clip scratch must not overwrite parent mask prefixes.
                std::vector<progpu_native_scene_clip_path> source_clip_paths;
                std::vector<progpu_native_path_segment> source_clip_segments;
                std::vector<progpu_native_scene_path_boolean_node> source_clip_nodes;
                status drawn = status::invalid_graph;
                if (visual_brush) {
                    ++compile_context->visual_brush_depth;
                    drawn = append_visual(brush.source_handle, content,
                        static_cast<std::uint32_t>(active_drawings.size() + 1U),
                        compile_context->scene_id, builder, brush_indices,
                        image_indices, glyph_resources, compile_context,
                        active_drawings, source_clip_paths, source_clip_segments,
                        source_clip_nodes, metrics);
                    --compile_context->visual_brush_depth;
                } else if (drawing_brush) {
                    if (save_state(content)) {
                        // Original MIL DrawDrawing framing: one borrowed stack
                        // packet, decoded synchronously with shared cycle/depth state.
                        const std::array<std::uint32_t, 4U> packet{
                            16U, static_cast<std::uint32_t>(command::draw_drawing),
                            brush.source_handle, 0U};
                        drawn = append_render_stream(std::as_bytes(std::span(packet)),
                            nullptr, content, drawing_depth + 1U, builder,
                            brush_indices, image_indices, glyph_resources, compile_context,
                            active_drawings, source_clip_paths, source_clip_segments,
                            source_clip_nodes, metrics);
                        if (!builder.restore() && drawn == status::success) drawn = status::invalid_graph;
                    }
                } else if (vector_source) {
                    // DrawingImage owns its nested mapping and save/restore.
                    // Keep independent clip scratch across that recursion too.
                    auto saved_paths = std::exchange(clip_paths, {});
                    auto saved_segments = std::exchange(clip_segments, {});
                    auto saved_nodes = std::exchange(clip_boolean_nodes, {});
                    drawn = append_bitmap_source(brush.source_handle,
                        0.0, 0.0, content_width, content_height, content);
                    clip_paths = std::move(saved_paths);
                    clip_segments = std::move(saved_segments);
                    clip_boolean_nodes = std::move(saved_nodes);
                } else {
                    render_scope_state image_state = content;
                    image_state.transform = {};
                    if (save_state(image_state)) {
                        drawn = append_bitmap_source(brush.source_handle,
                            0.0, 0.0, content_width, content_height, content);
                        if (!builder.restore() && drawn == status::success) drawn = status::invalid_graph;
                    }
                }
                if (!builder.pop_layer() && drawn == status::success) drawn = status::invalid_graph;
                active_drawings.erase(brush_handle);
                return drawn;
            }
            // append_bitmap_source emits the complete content-to-target matrix
            // in the image record. The saved state owns only opacity/clipping;
            // composing the same matrix there would transform the image twice.
            render_scope_state image_state = clipped;
            image_state.transform = {};
            if (!save_state(image_state)) return status::invalid_graph;
            const status drawn = append_bitmap_source(brush.source_handle,
                0.0, 0.0, content_width, content_height, clipped);
            const bool restored = builder.restore();
            if (drawn != status::success) return drawn;
            return restored ? status::success : status::invalid_graph;
        };
        const auto append_path_tile_brush = [
            &builder, &clip_paths, &clip_segments, &clip_boolean_nodes,
            &append_single_tile_brush](
            std::uint32_t brush_handle,
            const brush_use_state& use,
            const render_scope_state& state,
            std::span<const progpu_native_path_segment> segments,
            std::span<const progpu_native_scene_path_boolean_node> nodes,
            std::uint32_t fill_rule) -> status {
            if (segments.empty() || use.width <= 0.0 || use.height <= 0.0) {
                return status::success;
            }
            if (state.clip_path_count >= 64U || nodes.size() > 63U) {
                return status::unsupported_command;
            }
            progpu_native_affine_2d transform{};
            if (!try_to_native_affine(use.effective_transform, transform) ||
                !finite_double_as_float(use.x) || !finite_double_as_float(use.y) ||
                !finite_double_as_float(use.x + use.width) ||
                !finite_double_as_float(use.y + use.height)) {
                return status::invalid_graph;
            }
            clip_paths.resize(state.clip_path_count);
            clip_segments.resize(state.clip_segment_count);
            clip_boolean_nodes.resize(state.clip_boolean_node_count);
            const auto segment_offset = clip_segments.size();
            const auto node_offset = clip_boolean_nodes.size();
            // Reuse the renderer's already-lowered fill program. Only leaf
            // segment references change when appended after inherited clips;
            // postfix operations and winding semantics remain unchanged.
            clip_segments.insert(clip_segments.end(), segments.begin(), segments.end());
            clip_boolean_nodes.insert(clip_boolean_nodes.end(), nodes.begin(), nodes.end());
            for (std::size_t index = node_offset; index < clip_boolean_nodes.size(); ++index) {
                auto& node = clip_boolean_nodes[index];
                if (node.kind == PROGPU_NATIVE_PATH_BOOLEAN_LEAF ||
                    node.kind == PROGPU_NATIVE_PATH_BOOLEAN_WINDING_LEAF) {
                    node.segment_offset += segment_offset;
                }
            }
            clip_paths.push_back({segment_offset, segments.size(),
                nodes.empty() ? 0U : node_offset, nodes.size(),
                static_cast<float>(use.x), static_cast<float>(use.y),
                static_cast<float>(use.x + use.width), static_cast<float>(use.y + use.height),
                transform, fill_rule, state.edge_aliased ? 1U : 8U,
                PROGPU_NATIVE_CLIP_INTERSECT, 0U});
            render_scope_state paint = state;
            if (!builder.add_vector_clip_mask(clip_paths, clip_segments,
                    clip_boolean_nodes, 1.0F, paint.mask_resource_index)) {
                clip_paths.resize(state.clip_path_count);
                clip_segments.resize(state.clip_segment_count);
                clip_boolean_nodes.resize(state.clip_boolean_node_count);
                return status::invalid_graph;
            }
            paint.clip_path_count = clip_paths.size();
            paint.clip_segment_count = clip_segments.size();
            paint.clip_boolean_node_count = clip_boolean_nodes.size();
            return append_single_tile_brush(brush_handle, use, paint);
        };
        // Algorithm: Compile the canonical stroke once into a GPU alpha mask,
        // then apply the native tile source through an isolated masked layer.
        // Time/space: O(S + D) retained primitives for S segments and D dash
        // pieces; dash traversal is sequential, not an independent CPU pixel loop.
        const auto paint_tile_pen_mask = [&builder, &append_single_tile_brush](
            const pen_state& pen, const brush_use_state& use,
            const render_scope_state& state,
            std::span<const progpu_native_geometry_primitive> primitives) -> status {
            if (primitives.empty()) return status::success;
            progpu_native_image_rect bounds{};
            if (!try_transform_bounds(use.x, use.y, use.width, use.height,
                    use.effective_transform, bounds)) return status::invalid_graph;
            progpu_native_scene_layer_geometry_mask mask{};
            mask.bounds = bounds;
            mask.transform = {1.0F, 0.0F, 0.0F, 1.0F, 0.0F, 0.0F};
            mask.opacity = 1.0F;
            mask.brush.type = PROGPU_NATIVE_SCENE_BRUSH_SOLID;
            mask.brush.opacity = 1.0F;
            mask.brush.colors[0] = {1.0F, 1.0F, 1.0F, 1.0F};
            progpu_native_scene_layer layer{};
            layer.struct_size = sizeof(layer);
            layer.flags = PROGPU_NATIVE_SCENE_LAYER_FORCE_ISOLATION | PROGPU_NATIVE_SCENE_LAYER_BOUNDS;
            layer.bounds = bounds;
            layer.opacity = 1.0F;
            layer.blend_mode = PROGPU_NATIVE_BLEND_SRC_OVER;
            layer.effect_resource_index = PROGPU_NATIVE_SCENE_NO_INDEX;
            if (!builder.add_geometry_mask(mask, primitives, {}, layer.mask_resource_index) ||
                !builder.push_layer(layer)) return status::invalid_graph;
            const status drawn = append_single_tile_brush(pen.brush_handle, use, state);
            const bool restored = builder.pop_layer();
            return drawn != status::success ? drawn : restored ? status::success : status::invalid_graph;
        };
        const auto append_tile_pen = [this, &paint_tile_pen_mask, &append_degenerate_cap_stroke](
            const pen_state& pen, const brush_use_state& use,
            const render_scope_state& state,
            std::span<const progpu_native_path_segment> segments,
            std::span<const std::uint8_t> smooth_joins, bool closed,
            std::span<const path_stroke_contour_state> contours = {},
            std::vector<progpu_native_geometry_primitive>* collected = nullptr) -> status {
            if (pen.thickness <= 0.0 || pen.brush_handle == 0U ||
                affine_has_zero_area(use.effective_transform)) return status::success;
            // Geometry-mask primitives currently have no guideline-resource
            // carrier. Do not silently discard active snapping on a tile pen.
            if (state.guideline_resource_index != PROGPU_NATIVE_SCENE_NO_INDEX ||
                state.per_point_guidelines) return status::unsupported_command;
            std::span<const double> intervals;
            double dash_offset = 0.0;
            if (pen.dash_style_handle != 0U) {
                const auto found = dash_styles.find(pen.dash_style_handle);
                if (found == dash_styles.end()) return status::invalid_handle;
                intervals = found->second.intervals;
                const status resolved = resolve_dash_offset(pen.dash_style_handle, dash_offset);
                if (resolved != status::success) return resolved;
            }
            progpu_native_affine_2d transform{};
            if (!try_to_native_affine(use.effective_transform, transform)) return status::invalid_graph;
            native::semantic_path_stroke::style style{transform,
                static_cast<float>(pen.thickness), static_cast<float>(std::max(1.0, pen.miter_limit)),
                dash_offset, pen.start_line_cap, pen.end_line_cap, pen.dash_cap, pen.line_join,
                state.edge_aliased ? static_cast<std::uint32_t>(PROGPU_NATIVE_PRIMITIVE_FLAG_EDGE_ALIASED) : 0U};
            curve_dash::run_buffer scratch;
            std::vector<progpu_native_geometry_primitive> local_primitives;
            auto& primitives = collected == nullptr ? local_primitives : *collected;
            std::vector<std::uint32_t> brushes;
            const auto compile_run = [&](std::span<const progpu_native_path_segment> run,
                std::span<const std::uint8_t> joins, bool run_closed,
                const progpu_native_point* anchor = nullptr) {
                if (run.empty() && anchor == nullptr) return status::success;
                if (!std::ranges::any_of(run, [](const auto& segment) {
                        progpu_native_point tangent{};
                        return native::semantic_path_stroke::try_tangent(segment, true, tangent) ||
                            native::semantic_path_stroke::try_tangent(segment, false, tangent);
                    })) {
                    // Reuse ordinary MIL zero-length cap and dash-phase semantics;
                    // closed collapsed contours use a round cap pair there too.
                    const auto cap = static_cast<std::uint32_t>(PROGPU_NATIVE_STROKE_CAP_ROUND);
                    bool emitted = false;
                    return append_degenerate_cap_stroke(pen, anchor == nullptr ? run.front().p0 : *anchor,
                        0U, use.effective_transform, use.effective_transform,
                        run_closed ? cap : style.start_cap, run_closed ? cap : style.end_cap,
                        emitted, &primitives);
                }
                const auto compiled = native::semantic_path_stroke::compile(run, joins,
                    run_closed, intervals, style, 0U, scratch, primitives, brushes);
                return compiled == native::semantic_path_stroke::result::success ? status::success :
                    compiled == native::semantic_path_stroke::result::capacity_exceeded
                        ? status::capacity_exceeded : status::unsupported_command;
            };
            if (contours.empty()) {
                const status compiled = compile_run(segments, smooth_joins, closed);
                if (compiled != status::success) return compiled;
            } else {
                for (const auto& contour : contours) {
                    style.start_cap = contour.start_uses_dash_cap ? pen.dash_cap : pen.start_line_cap;
                    style.end_cap = contour.end_uses_dash_cap ? pen.dash_cap : pen.end_line_cap;
                    if (contour.points.empty() && contour.segments.empty()) continue;
                    const status compiled = compile_run(contour.segments,
                        contour.smooth_joins, contour.closed,
                        contour.points.empty() ? nullptr : &contour.points.front());
                    if (compiled != status::success) return compiled;
                }
            }
            // The outer mask survives inner paint/viewport clip construction.
            // Collection mode defers the one brush paint until all children finish.
            return collected == nullptr ? paint_tile_pen_mask(pen, use, state, primitives) : status::success;
        };
        const auto append_tile_line_pen = [&append_tile_pen](
            const pen_state& pen, double x0, double y0, double x1, double y1,
            const affine_2d_double& transform, const render_scope_state& state,
            std::vector<progpu_native_geometry_primitive>* collected = nullptr) -> status {
            if (pen.thickness <= 0.0) return status::success;
            brush_use_state use{};
            use.effective_transform = transform;
            if (x0 == x1 && y0 == y1) {
                if (pen.start_line_cap == PROGPU_NATIVE_STROKE_CAP_FLAT &&
                    pen.end_line_cap == PROGPU_NATIVE_STROKE_CAP_FLAT) return status::success;
                if (!try_degenerate_cap_stroke_bounds(x0, y0, pen.thickness,
                        pen.start_line_cap, pen.end_line_cap,
                        use.x, use.y, use.width, use.height)) return status::invalid_graph;
            } else if (!try_line_stroke_bounds(x0, y0, x1, y1, pen.thickness,
                           pen.start_line_cap, pen.end_line_cap,
                           use.x, use.y, use.width, use.height)) return status::unsupported_command;
            progpu_native_path_segment segment{};
            segment.kind = PROGPU_NATIVE_PATH_SEGMENT_LINE;
            segment.p0 = {static_cast<float>(x0), static_cast<float>(y0)};
            segment.p1 = {static_cast<float>(x1), static_cast<float>(y1)};
            const std::array<std::uint8_t, 1U> smooth{0U};
            return append_tile_pen(pen, use, state, std::span(&segment, 1U), smooth, false, {}, collected);
        };
        const auto make_tile_fixed_geometry = [&make_ellipse_path_geometry, &make_rounded_rectangle_geometry](
            const fixed_geometry_state& shape) {
            if (shape.kind == fixed_geometry_kind::ellipse)
                return make_ellipse_path_geometry(shape.first, shape.second, shape.third, shape.fourth);
            if (shape.radius_x > 0.0 && shape.radius_y > 0.0)
                return make_rounded_rectangle_geometry(shape.first, shape.second, shape.third, shape.fourth,
                    shape.radius_x, shape.radius_y);
            path_geometry_state geometry;
            constexpr std::array<std::array<double, 2U>, 4U> corners{{
                {0.0, 0.0}, {1.0, 0.0}, {1.0, 1.0}, {0.0, 1.0}}};
            path_stroke_contour_state contour{};
            contour.closed = true;
            for (std::size_t index = 0U; index < 4U; ++index) {
                const auto& next = corners[(index + 1U) % 4U];
                progpu_native_path_segment segment{};
                segment.kind = PROGPU_NATIVE_PATH_SEGMENT_LINE;
                segment.p0 = {static_cast<float>(shape.first + corners[index][0] * shape.third),
                    static_cast<float>(shape.second + corners[index][1] * shape.fourth)};
                segment.p1 = {static_cast<float>(shape.first + next[0] * shape.third),
                    static_cast<float>(shape.second + next[1] * shape.fourth)};
                contour.segments.push_back(segment);
                contour.smooth_joins.push_back(0U);
            }
            geometry.stroke_contours.push_back(std::move(contour));
            return geometry;
        };
        // Algorithm: lower collapsed fixed shapes using the ordinary MIL
        // contour/centerline rules, then paint one tile source through coverage.
        // Time/space: O(1) shape setup plus O(D) emitted dash pieces.
        const auto append_degenerate_tile_shape = [this, &append_tile_pen, &append_tile_line_pen,
            &append_path_tile_brush, &make_degenerate_rectangle_outline, &make_tile_fixed_geometry,
            &make_wpf_rounded_rectangle_geometry, &degenerate_ellipse_points](
            const fixed_geometry_state& shape, const pen_state& pen,
            const brush_use_state& use, const render_scope_state& state) -> status {
            if (state.guideline_resource_index != PROGPU_NATIVE_SCENE_NO_INDEX || state.per_point_guidelines)
                return status::unsupported_command;
            bool dashed = false;
            if (pen.dash_style_handle != 0U) {
                const auto found = dash_styles.find(pen.dash_style_handle);
                if (found == dash_styles.end()) return status::invalid_handle;
                dashed = !found->second.intervals.empty();
            }
            if (shape.kind == fixed_geometry_kind::ellipse) {
                pen_state smooth_pen = pen;
                smooth_pen.line_join = PROGPU_NATIVE_STROKE_JOIN_ROUND;
                if (!dashed && (shape.third != 0.0 || shape.fourth != 0.0)) {
                    smooth_pen.start_line_cap = PROGPU_NATIVE_STROKE_CAP_ROUND;
                    smooth_pen.end_line_cap = PROGPU_NATIVE_STROKE_CAP_ROUND;
                    return append_tile_line_pen(smooth_pen, shape.first - shape.third, shape.second - shape.fourth,
                        shape.first + shape.third, shape.second + shape.fourth, use.effective_transform, state);
                }
                const auto points = degenerate_ellipse_points(shape.first, shape.second, shape.third, shape.fourth);
                std::array<progpu_native_path_segment, 4U> segments{};
                const std::array<std::uint8_t, 4U> joins{};
                for (std::size_t index = 0U; index < segments.size(); ++index) {
                    segments[index].kind = PROGPU_NATIVE_PATH_SEGMENT_LINE;
                    segments[index].p0 = points[index];
                    segments[index].p1 = points[(index + 1U) % points.size()];
                }
                return append_tile_pen(smooth_pen, use, state, segments, joins, true);
            }
            if (!dashed) {
                const auto segments = make_degenerate_rectangle_outline(use.x, use.y,
                    use.x + use.width, use.y + use.height, shape.radius_x, shape.radius_y, pen);
                return append_path_tile_brush(pen.brush_handle, use, state, segments, {}, PROGPU_NATIVE_FILL_RULE_EVEN_ODD);
            }
            const bool rounded = shape.radius_x > 0.0 && shape.radius_y > 0.0;
            const auto geometry = rounded ? make_wpf_rounded_rectangle_geometry(shape.first, shape.second,
                shape.third, shape.fourth, shape.radius_x, shape.radius_y) : make_tile_fixed_geometry(shape);
            pen_state stroke_pen = pen;
            if (rounded) stroke_pen.miter_limit = 1.0;
            return append_tile_pen(stroke_pen, use, state, {}, {}, true, geometry.stroke_contours);
        };
        const auto append_media_player = [
            this,
            &builder,
            &image_indices](
            std::uint32_t media_player_handle,
            double x,
            double y,
            double width,
            double height,
            const render_scope_state& state) {
            if (media_player_handle == 0U) {
                return status::invalid_handle;
            }
            if (width == 0.0 || height == 0.0) {
                return status::success;
            }
            const auto player = media_players.find(media_player_handle);
            const auto image = image_indices.find(media_player_handle);
            if (player == media_players.end() || image == image_indices.end()) {
                return status::invalid_handle;
            }
            progpu_native_affine_2d native_transform{};
            progpu_native_image_rect bounds{};
            if (!try_to_native_affine(state.transform, native_transform) ||
                !try_transform_bounds(
                    x,
                    y,
                    width,
                    height,
                    state.transform,
                    bounds)) {
                return status::invalid_graph;
            }
            const auto& descriptor = player->second;
            const progpu_native_scene_image_draw image_draw{
                sizeof(progpu_native_scene_image_draw),
                0U,
                descriptor.width,
                descriptor.height,
                descriptor.width * 4U,
                state.image_sampling,
                {0.0F,
                 0.0F,
                 static_cast<float>(descriptor.width),
                 static_cast<float>(descriptor.height)},
                {static_cast<float>(x),
                 static_cast<float>(y),
                 static_cast<float>(width),
                 static_cast<float>(height)},
                native_transform,
                1.0F,
                1U};
            const progpu_native_scene_image_sampling_options cubic_options{
                sizeof(progpu_native_scene_image_sampling_options),
                0U,
                1.0F / 3.0F,
                1.0F / 3.0F};
            const auto* sampling_options = state.image_sampling ==
                PROGPU_NATIVE_IMAGE_SAMPLING_CUBIC
                ? &cubic_options
                : nullptr;
            return builder.draw_image(
                    image->second,
                    image_draw,
                    bounds,
                    PROGPU_NATIVE_SCENE_NO_INDEX,
                    sampling_options)
                ? status::success
                : status::invalid_graph;
        };
        for (;;) {
            const status read_status = reader.next(view);
            if (read_status == status::end_of_batch) {
                return scope_states.empty() && scope_layers.empty()
                    ? status::success
                    : status::invalid_graph;
            }
            if (read_status != status::success) {
                return read_status;
            }
            const status framing_status =
                validate_render_data_command_framing(view);
            if (framing_status != status::success) {
                return framing_status;
            }

            if (view.kind == command::push_opacity) {
                using layout = command_layouts::push_opacity;
                double opacity = 0.0;
                if (!has_exact_size(view, layout::fixed_size) ||
                    !read_at(view.packet, layout::opacity_offset, opacity)) {
                    return status::malformed_batch;
                }
                if (!std::isfinite(opacity) || opacity < 0.0 ||
                    opacity > 1.0) {
                    return status::malformed_batch;
                }
                const progpu_native_scene_layer layer{
                    sizeof(progpu_native_scene_layer),
                    opacity == 1.0
                        ? 0U
                        : static_cast<std::uint32_t>(
                            PROGPU_NATIVE_SCENE_LAYER_FORCE_ISOLATION),
                    {},
                    static_cast<float>(opacity),
                    PROGPU_NATIVE_BLEND_SRC_OVER,
                    PROGPU_NATIVE_SCENE_NO_INDEX,
                    PROGPU_NATIVE_SCENE_NO_INDEX,
                    0U,
                    0U,
                    0U,
                    0U};
                if (!builder.push_layer(layer)) {
                    return status::invalid_graph;
                }
                scope_states.push_back(current);
                scope_layers.push_back(true);
                continue;
            }
            if (view.kind == command::push_opacity_animate) {
                using layout = command_layouts::push_opacity_animate;
                double opacity = 0.0;
                std::uint32_t opacity_animation_handle = 0U;
                std::uint32_t padding = 0U;
                if (!has_exact_size(view, layout::fixed_size) ||
                    !read_at(view.packet, layout::opacity_offset, opacity) ||
                    !read_at(
                        view.packet,
                        layout::h_opacity_animations_offset,
                        opacity_animation_handle) ||
                    !read_at(
                        view.packet,
                        layout::quad_word_pad0_offset,
                        padding) ||
                    padding != 0U ||
                    !std::isfinite(opacity) || opacity < 0.0 ||
                    opacity > 1.0) {
                    return status::malformed_batch;
                }
                double resolved_opacity = opacity;
                if (opacity_animation_handle != 0U) {
                    const status opacity_status = resolve_animated_double(
                        opacity,
                        opacity_animation_handle,
                        resolved_opacity);
                    if (opacity_status != status::success) {
                        return opacity_status;
                    }
                }
                if (!finite_double_as_float(resolved_opacity) ||
                    resolved_opacity < 0.0 || resolved_opacity > 1.0) {
                    return status::invalid_graph;
                }
                const progpu_native_scene_layer layer{
                    sizeof(progpu_native_scene_layer),
                    resolved_opacity == 1.0
                        ? 0U
                        : static_cast<std::uint32_t>(
                            PROGPU_NATIVE_SCENE_LAYER_FORCE_ISOLATION),
                    {},
                    static_cast<float>(resolved_opacity),
                    PROGPU_NATIVE_BLEND_SRC_OVER,
                    PROGPU_NATIVE_SCENE_NO_INDEX,
                    PROGPU_NATIVE_SCENE_NO_INDEX,
                    0U,
                    0U,
                    0U,
                    0U};
                if (!builder.push_layer(layer)) {
                    return status::invalid_graph;
                }
                scope_states.push_back(current);
                scope_layers.push_back(true);
                continue;
            }
            if (view.kind == command::push_opacity_mask) {
                using layout = command_layouts::push_opacity_mask;
                float left = 0.0F;
                float top = 0.0F;
                float right = 0.0F;
                float bottom = 0.0F;
                std::uint32_t opacity_mask_handle = 0U;
                std::uint32_t padding = 0U;
                const std::size_t bounds =
                    layout::bounding_box_cache_local_space_offset;
                if (!has_exact_size(view, layout::fixed_size) ||
                    !read_at(view.packet, bounds, left) ||
                    !read_at(view.packet, bounds + 4U, top) ||
                    !read_at(view.packet, bounds + 8U, right) ||
                    !read_at(view.packet, bounds + 12U, bottom) ||
                    !read_at(
                        view.packet,
                        layout::h_opacity_mask_offset,
                        opacity_mask_handle) ||
                    !read_at(
                        view.packet,
                        layout::quad_word_pad0_offset,
                        padding) ||
                    padding != 0U ||
                    !std::isfinite(left) || !std::isfinite(top) ||
                    !std::isfinite(right) || !std::isfinite(bottom) ||
                    right < left || bottom < top ||
                    opacity_mask_handle == 0U) {
                    return status::malformed_batch;
                }
                const double width = static_cast<double>(right) - left;
                const double height = static_cast<double>(bottom) - top;
                double uniform_alpha = 1.0;
                std::uint32_t mask_resource_index =
                    PROGPU_NATIVE_SCENE_NO_INDEX;
                const bool spatial_mask = gradient_brushes.contains(
                    opacity_mask_handle);
                const status mask_status = spatial_mask
                    ? add_gradient_opacity_mask(
                        opacity_mask_handle,
                        left,
                        top,
                        width,
                        height,
                        current.transform,
                        builder,
                        mask_resource_index)
                    : resolve_uniform_opacity_mask_alpha(
                        opacity_mask_handle,
                        uniform_alpha);
                if (mask_status != status::success) {
                    return mask_status;
                }
                progpu_native_scene_layer layer{};
                layer.struct_size = sizeof(layer);
                layer.flags = PROGPU_NATIVE_SCENE_LAYER_BOUNDS;
                if (spatial_mask || uniform_alpha != 1.0) {
                    layer.flags |= PROGPU_NATIVE_SCENE_LAYER_FORCE_ISOLATION;
                }
                if (!try_transform_bounds(
                        left,
                        top,
                        width,
                        height,
                        current.transform,
                        layer.bounds)) {
                    return status::invalid_graph;
                }
                layer.opacity = static_cast<float>(uniform_alpha);
                layer.blend_mode = PROGPU_NATIVE_BLEND_SRC_OVER;
                layer.mask_resource_index = mask_resource_index;
                layer.effect_resource_index =
                    PROGPU_NATIVE_SCENE_NO_INDEX;
                if (!builder.push_layer(layer)) {
                    return status::invalid_graph;
                }
                scope_states.push_back(current);
                scope_layers.push_back(true);
                continue;
            }
            if (view.kind == command::push_clip) {
                using layout = command_layouts::push_clip;
                std::uint32_t geometry_handle = 0U;
                std::uint32_t padding = 0U;
                if (!has_exact_size(view, layout::fixed_size) ||
                    !read_at(
                        view.packet,
                        layout::h_clip_geometry_offset,
                        geometry_handle) ||
                    !read_at(
                        view.packet,
                        layout::quad_word_pad0_offset,
                        padding)) {
                    return status::malformed_batch;
                }
                if (geometry_handle == 0U || padding != 0U) {
                    return status::malformed_batch;
                }
                render_scope_state next = current;
                const status clip_status = apply_clip(
                    geometry_handle,
                    current,
                    next);
                if (clip_status != status::success) {
                    return clip_status;
                }
                if (!save_state(next)) {
                    return status::invalid_graph;
                }
                scope_states.push_back(current);
                scope_layers.push_back(false);
                current = next;
                continue;
            }
            if (view.kind == command::push_transform) {
                using layout = command_layouts::push_transform;
                std::uint32_t transform_handle = 0U;
                std::uint32_t padding = 0U;
                if (!has_exact_size(view, layout::fixed_size) ||
                    !read_at(
                        view.packet,
                        layout::h_transform_offset,
                        transform_handle) ||
                    !read_at(
                        view.packet,
                        layout::quad_word_pad0_offset,
                        padding)) {
                    return status::malformed_batch;
                }
                if (padding != 0U) {
                    return status::malformed_batch;
                }
                affine_2d_double pushed_transform{};
                if (transform_handle != 0U) {
                    const status transform_status = resolve_transform(
                        transform_handle,
                        pushed_transform);
                    if (transform_status != status::success) {
                        return transform_status;
                    }
                }
                render_scope_state next = current;
                next.transform = compose_affine(
                    pushed_transform,
                    current.transform);
                if (!save_state(next)) {
                    return status::invalid_graph;
                }
                scope_states.push_back(current);
                scope_layers.push_back(false);
                current = next;
                continue;
            }
            if (view.kind == command::push_guideline_set) {
                using layout = command_layouts::push_guideline_set;
                std::uint32_t guideline_set_handle = 0U;
                std::uint32_t padding = 0U;
                if (!has_exact_size(view, layout::fixed_size) ||
                    !read_at(
                        view.packet,
                        layout::h_guidelines_offset,
                        guideline_set_handle) ||
                    !read_at(
                        view.packet,
                        layout::quad_word_pad0_offset,
                        padding) ||
                    padding != 0U) {
                    return status::malformed_batch;
                }
                render_scope_state next = current;
                if (guideline_set_handle == 0U) {
                    next.guideline_resource_index =
                        PROGPU_NATIVE_SCENE_NO_INDEX;
                    next.per_point_guidelines = false;
                } else {
                    const auto guidelines = guideline_sets.find(
                        guideline_set_handle);
                    if (guidelines == guideline_sets.end()) {
                        return status::invalid_handle;
                    }
                    if (guidelines->second.is_dynamic) {
                        const status guideline_status =
                            apply_dynamic_guidelines(
                                guidelines->second,
                                next,
                                builder,
                                false,
                                compile_context);
                        if (guideline_status != status::success) {
                            return guideline_status;
                        }
                    } else {
                        const status guideline_status = apply_static_guidelines(
                            guidelines->second.guidelines_x,
                            guidelines->second.guidelines_y,
                            next,
                            builder,
                            false);
                        if (guideline_status != status::success) {
                            return guideline_status;
                        }
                    }
                }
                if (!save_state(next)) {
                    return status::invalid_graph;
                }
                scope_states.push_back(current);
                scope_layers.push_back(false);
                current = next;
                continue;
            }
            if (view.kind == command::push_guideline_y1 ||
                view.kind == command::push_guideline_y2) {
                if (compact_guidelines == nullptr ||
                    compile_context == nullptr) {
                    return status::unsupported_command;
                }
                double leading = 0.0;
                double shift = 0.0;
                if (view.kind == command::push_guideline_y1) {
                    using layout = command_layouts::push_guideline_y1;
                    if (!read_at(
                            view.packet,
                            layout::coordinate_offset,
                            leading)) {
                        return status::malformed_batch;
                    }
                } else {
                    using layout = command_layouts::push_guideline_y2;
                    if (!read_at(
                            view.packet,
                            layout::leading_coordinate_offset,
                            leading) ||
                        !read_at(
                            view.packet,
                            layout::offset_to_driven_coordinate_offset,
                            shift)) {
                        return status::malformed_batch;
                    }
                }
                if (!finite_double_as_float(leading) ||
                    !finite_double_as_float(shift)) {
                    return status::malformed_batch;
                }
                auto compact = compact_guidelines->find(view.batch_offset);
                if (compact == compact_guidelines->end()) {
                    try {
                        guideline_set_state state{};
                        state.is_dynamic = true;
                        state.guidelines_y = {leading, shift};
                        compact = compact_guidelines->emplace(
                            view.batch_offset,
                            std::move(state)).first;
                    } catch (const std::bad_alloc&) {
                        return status::capacity_exceeded;
                    }
                }
                render_scope_state next = current;
                const status guideline_status = apply_dynamic_guidelines(
                    compact->second,
                    next,
                    builder,
                    false,
                    compile_context);
                if (guideline_status != status::success) {
                    return guideline_status;
                }
                if (!save_state(next)) {
                    return status::invalid_graph;
                }
                scope_states.push_back(current);
                scope_layers.push_back(false);
                current = next;
                continue;
            }
            if (view.kind == command::push_effect) {
                using layout = command_layouts::push_effect;
                std::uint32_t effect_handle = 0U;
                std::uint32_t effect_input_handle = 0U;
                if (!has_exact_size(view, layout::fixed_size) ||
                    !read_at(
                        view.packet,
                        layout::h_effect_offset,
                        effect_handle) ||
                    !read_at(
                        view.packet,
                        layout::h_effect_input_offset,
                        effect_input_handle)) {
                    return status::malformed_batch;
                }
                // WPF's native render-data executor intentionally disables
                // legacy BitmapEffect execution and lowers this command to
                // PushOpacity(1). The two managed-only handles are opaque to
                // milcore, but the scope still participates in Pop matching.
                (void)effect_handle;
                (void)effect_input_handle;
                if (!save_state(current)) {
                    return status::invalid_graph;
                }
                scope_states.push_back(current);
                scope_layers.push_back(false);
                continue;
            }
            if (view.kind == command::pop) {
                if (!has_exact_size(
                        view,
                        command_layouts::pop::fixed_size)) {
                    return status::malformed_batch;
                }
                if (scope_states.empty() || scope_layers.empty()) {
                    return status::invalid_graph;
                }
                const bool restored = scope_layers.back()
                    ? builder.pop_layer()
                    : builder.restore();
                if (!restored) {
                    return status::invalid_graph;
                }
                current = scope_states.back();
                scope_states.pop_back();
                scope_layers.pop_back();
                continue;
            }
            if (view.kind == command::draw_image ||
                view.kind == command::draw_image_animate) {
                const bool animated =
                    view.kind == command::draw_image_animate;
                using layout = command_layouts::draw_image;
                double x = 0.0;
                double y = 0.0;
                double width = 0.0;
                double height = 0.0;
                std::uint32_t image_source_handle = 0U;
                std::uint32_t trailing_value = 0U;
                if (!has_exact_size(
                        view,
                        animated
                            ? command_layouts::draw_image_animate::fixed_size
                            : layout::fixed_size) ||
                    !read_at(view.packet, layout::rectangle_offset, x) ||
                    !read_at(view.packet, layout::rectangle_offset + 8U, y) ||
                    !read_at(
                        view.packet,
                        layout::rectangle_offset + 16U,
                        width) ||
                    !read_at(
                        view.packet,
                        layout::rectangle_offset + 24U,
                        height) ||
                    !read_at(
                        view.packet,
                        layout::h_image_source_offset,
                        image_source_handle) ||
                    !read_at(
                        view.packet,
                        animated
                            ? command_layouts::draw_image_animate::
                                h_rectangle_animations_offset
                            : layout::quad_word_pad0_offset,
                        trailing_value)) {
                    return status::malformed_batch;
                }
                if (!animated && trailing_value != 0U) {
                    return status::malformed_batch;
                }
                if (!finite_double_as_float(x) ||
                    !finite_double_as_float(y) ||
                    !finite_double_as_float(width) ||
                    !finite_double_as_float(height) ||
                    width < 0.0 || height < 0.0) {
                    return status::malformed_batch;
                }
                const status rectangle_status = resolve_animated_rect(
                    x,
                    y,
                    width,
                    height,
                    animated ? trailing_value : 0U,
                    x,
                    y,
                    width,
                    height);
                if (rectangle_status != status::success) {
                    return rectangle_status;
                }
                const status image_status = append_bitmap_source(
                    image_source_handle,
                    x,
                    y,
                    width,
                    height,
                    current);
                if (image_status != status::success) {
                    return image_status;
                }
                continue;
            }
            if (view.kind == command::draw_video ||
                view.kind == command::draw_video_animate) {
                const bool animated =
                    view.kind == command::draw_video_animate;
                using layout = command_layouts::draw_video;
                double x = 0.0;
                double y = 0.0;
                double width = 0.0;
                double height = 0.0;
                std::uint32_t media_player_handle = 0U;
                std::uint32_t trailing_value = 0U;
                if (!has_exact_size(
                        view,
                        animated
                            ? command_layouts::draw_video_animate::fixed_size
                            : layout::fixed_size) ||
                    !read_at(view.packet, layout::rectangle_offset, x) ||
                    !read_at(view.packet, layout::rectangle_offset + 8U, y) ||
                    !read_at(
                        view.packet,
                        layout::rectangle_offset + 16U,
                        width) ||
                    !read_at(
                        view.packet,
                        layout::rectangle_offset + 24U,
                        height) ||
                    !read_at(
                        view.packet,
                        layout::h_player_offset,
                        media_player_handle) ||
                    !read_at(
                        view.packet,
                        animated
                            ? command_layouts::draw_video_animate::
                                h_rectangle_animations_offset
                            : layout::quad_word_pad0_offset,
                        trailing_value)) {
                    return status::malformed_batch;
                }
                if (!animated && trailing_value != 0U) {
                    return status::malformed_batch;
                }
                if (!finite_double_as_float(x) ||
                    !finite_double_as_float(y) ||
                    !finite_double_as_float(width) ||
                    !finite_double_as_float(height) ||
                    width < 0.0 || height < 0.0) {
                    return status::malformed_batch;
                }
                const status rectangle_status = resolve_animated_rect(
                    x,
                    y,
                    width,
                    height,
                    animated ? trailing_value : 0U,
                    x,
                    y,
                    width,
                    height);
                if (rectangle_status != status::success) {
                    return rectangle_status;
                }
                const status video_status = append_media_player(
                    media_player_handle,
                    x,
                    y,
                    width,
                    height,
                    current);
                if (video_status != status::success) {
                    return video_status;
                }
                continue;
            }
            if (view.kind == command::draw_glyph_run) {
                using layout = command_layouts::draw_glyph_run;
                std::uint32_t foreground_brush_handle = 0U;
                std::uint32_t glyph_run_handle = 0U;
                if (!has_exact_size(view, layout::fixed_size) ||
                    !read_at(
                        view.packet,
                        layout::h_foreground_brush_offset,
                        foreground_brush_handle) ||
                    !read_at(
                        view.packet,
                        layout::h_glyph_run_offset,
                        glyph_run_handle)) {
                    return status::malformed_batch;
                }
                const status glyph_status = append_glyph_run(
                    glyph_run_handle,
                    foreground_brush_handle,
                    current,
                    builder,
                    glyph_resources);
                if (glyph_status != status::success) {
                    return glyph_status;
                }
                continue;
            }
            if (view.kind == command::draw_line ||
                view.kind == command::draw_line_animate) {
                const bool animated =
                    view.kind == command::draw_line_animate;
                const std::size_t fixed_size = animated
                    ? command_layouts::draw_line_animate::fixed_size
                    : command_layouts::draw_line::fixed_size;
                const std::size_t padding_offset = animated
                    ? command_layouts::draw_line_animate::quad_word_pad0_offset
                    : command_layouts::draw_line::quad_word_pad0_offset;
                double x0 = 0.0;
                double y0 = 0.0;
                double x1 = 0.0;
                double y1 = 0.0;
                std::uint32_t pen_handle = 0U;
                std::uint32_t point0_animation = 0U;
                std::uint32_t point1_animation = 0U;
                std::uint32_t padding = 0U;
                if (!has_exact_size(view, fixed_size) ||
                    !read_at(
                        view.packet,
                        command_layouts::draw_line::point0_offset,
                        x0) ||
                    !read_at(
                        view.packet,
                        command_layouts::draw_line::point0_offset + 8U,
                        y0) ||
                    !read_at(
                        view.packet,
                        command_layouts::draw_line::point1_offset,
                        x1) ||
                    !read_at(
                        view.packet,
                        command_layouts::draw_line::point1_offset + 8U,
                        y1) ||
                    !read_at(
                        view.packet,
                        command_layouts::draw_line::h_pen_offset,
                        pen_handle) ||
                    !read_at(
                        view.packet,
                        padding_offset,
                        padding)) {
                    return status::malformed_batch;
                }
                if (animated &&
                    (!read_at(
                        view.packet,
                        command_layouts::draw_line_animate::
                            h_point0_animations_offset,
                        point0_animation) ||
                     !read_at(
                         view.packet,
                         command_layouts::draw_line_animate::
                             h_point1_animations_offset,
                         point1_animation))) {
                    return status::malformed_batch;
                }
                if (padding != 0U || !finite_double_as_float(x0) ||
                    !finite_double_as_float(y0) ||
                    !finite_double_as_float(x1) ||
                    !finite_double_as_float(y1)) {
                    return status::malformed_batch;
                }
                const status point0_status = resolve_animated_point(
                    x0, y0, point0_animation, x0, y0);
                if (point0_status != status::success) {
                    return point0_status;
                }
                const status point1_status = resolve_animated_point(
                    x1, y1, point1_animation, x1, y1);
                if (point1_status != status::success) {
                    return point1_status;
                }
                const affine_2d_double identity{};
                if (pen_handle != 0U) {
                    pen_state pen{};
                    const status resolved = resolve_pen(pen_handle, pen);
                    if (resolved != status::success) return resolved;
                    if (tile_brushes.contains(pen.brush_handle)) {
                        const status drawn = append_tile_line_pen(pen, x0, y0, x1, y1,
                            current.transform, current);
                        if (drawn != status::success) return drawn;
                        ++metrics.line_count;
                        continue;
                    }
                }
                const status line_status = append_line_stroke(
                    x0,
                    y0,
                    x1,
                    y1,
                    pen_handle,
                    identity,
                    current.transform);
                if (line_status != status::success) {
                    return line_status;
                }
                continue;
            }
            bool is_geometry_shape = false;
            bool is_rounded = false;
            bool is_ellipse = false;
            double first = 0.0;
            double second = 0.0;
            double third = 0.0;
            double fourth = 0.0;
            double radius_x = 0.0;
            double radius_y = 0.0;
            std::uint32_t brush_handle = 0U;
            std::uint32_t pen_handle = 0U;
            std::uint32_t geometry_handle = 0U;
            affine_2d_double local_transform{};
            affine_2d_double effective_transform = current.transform;
            const bool is_drawing_resource =
                view.kind == command::draw_drawing;
            if (is_drawing_resource) {
                using layout = command_layouts::draw_drawing;
                std::uint32_t drawing_handle = 0U;
                std::uint32_t padding = 0U;
                if (!has_exact_size(view, layout::fixed_size) ||
                    !read_at(
                        view.packet,
                        layout::h_drawing_offset,
                        drawing_handle) ||
                    !read_at(
                        view.packet,
                        layout::quad_word_pad0_offset,
                        padding) ||
                    padding != 0U ||
                    drawing_handle == 0U) {
                    return status::malformed_batch;
                }
                const auto drawing = geometry_drawings.find(drawing_handle);
                if (drawing != geometry_drawings.end()) {
                    brush_handle = drawing->second.brush_handle;
                    pen_handle = drawing->second.pen_handle;
                    geometry_handle = drawing->second.geometry_handle;
                    if (geometry_handle == 0U) {
                        continue;
                    }
                } else {
                    const auto image = image_drawings.find(drawing_handle);
                    if (image != image_drawings.end()) {
                        auto image_state = image->second;
                        const status rectangle_status =
                            resolve_animated_rect(
                                image_state.x,
                                image_state.y,
                                image_state.width,
                                image_state.height,
                                image_state.rect_animation_handle,
                                image_state.x,
                                image_state.y,
                                image_state.width,
                                image_state.height);
                        if (rectangle_status != status::success) {
                            return rectangle_status;
                        }
                        if (image_state.image_source_handle == 0U ||
                            image_state.width == 0.0 ||
                            image_state.height == 0.0) {
                            continue;
                        }
                        const status image_status = append_bitmap_source(
                            image_state.image_source_handle,
                            image_state.x,
                            image_state.y,
                            image_state.width,
                            image_state.height,
                            current);
                        if (image_status != status::success) {
                            return image_status;
                        }
                        continue;
                    }
                    const auto video = video_drawings.find(drawing_handle);
                    if (video != video_drawings.end()) {
                        auto video_state = video->second;
                        const status rectangle_status =
                            resolve_animated_rect(
                                video_state.x,
                                video_state.y,
                                video_state.width,
                                video_state.height,
                                video_state.rect_animation_handle,
                                video_state.x,
                                video_state.y,
                                video_state.width,
                                video_state.height);
                        if (rectangle_status != status::success) {
                            return rectangle_status;
                        }
                        if (video_state.player_handle == 0U ||
                            video_state.width == 0.0 ||
                            video_state.height == 0.0) {
                            continue;
                        }
                        const status video_status = append_media_player(
                            video_state.player_handle,
                            video_state.x,
                            video_state.y,
                            video_state.width,
                            video_state.height,
                            current);
                        if (video_status != status::success) {
                            return video_status;
                        }
                        continue;
                    }
                    const auto glyph = glyph_run_drawings.find(
                        drawing_handle);
                    if (glyph != glyph_run_drawings.end()) {
                        const status glyph_status = append_glyph_run(
                            glyph->second.glyph_run_handle,
                            glyph->second.foreground_brush_handle,
                            current,
                            builder,
                            glyph_resources);
                        if (glyph_status != status::success) {
                            return glyph_status;
                        }
                        continue;
                    }
                    const auto group = drawing_groups.find(drawing_handle);
                    if (group == drawing_groups.end()) {
                        const auto resource = resources.find(drawing_handle);
                        return resource != resources.end() &&
                                is_drawing_type(resource->second.type)
                            ? status::unsupported_command
                            : status::invalid_handle;
                    }
                    if (!active_drawings.insert(drawing_handle).second) {
                        return status::invalid_graph;
                    }
                    double group_opacity = 0.0;
                    const status opacity_status = resolve_animated_double(
                        group->second.opacity,
                        group->second.opacity_animation_handle,
                        group_opacity);
                    if (opacity_status != status::success ||
                        !finite_double_as_float(group_opacity) ||
                        group_opacity < 0.0 || group_opacity > 1.0) {
                        active_drawings.erase(drawing_handle);
                        return opacity_status != status::success
                            ? opacity_status
                            : status::invalid_graph;
                    }
                    render_scope_state next = current;
                    if (group->second.bitmap_scaling_mode != 0U) {
                        next.image_sampling =
                            group->second.bitmap_scaling_mode == 3U
                            ? PROGPU_NATIVE_IMAGE_SAMPLING_NEAREST
                            : group->second.bitmap_scaling_mode == 2U
                            ? PROGPU_NATIVE_IMAGE_SAMPLING_FANT
                            : PROGPU_NATIVE_IMAGE_SAMPLING_LINEAR;
                    }
                    if (group->second.edge_mode != 0U) {
                        next.edge_aliased = true;
                    }
                    if (group->second.clear_type_hint != 0U) {
                        next.clear_type_enabled = true;
                    }
                    double opacity_mask_alpha = 1.0;
                    const bool has_spatial_opacity_mask =
                        group->second.opacity_mask_handle != 0U &&
                        gradient_brushes.contains(
                            group->second.opacity_mask_handle);
                    const status opacity_mask_status =
                        has_spatial_opacity_mask
                        ? status::success
                        : resolve_uniform_opacity_mask_alpha(
                            group->second.opacity_mask_handle,
                            opacity_mask_alpha);
                    if (opacity_mask_status != status::success) {
                        active_drawings.erase(drawing_handle);
                        return opacity_mask_status;
                    }
                    if (has_spatial_opacity_mask &&
                        !group->second.has_bounds) {
                        active_drawings.erase(drawing_handle);
                        return status::unsupported_command;
                    }
                    const double group_composite_opacity =
                        group_opacity * opacity_mask_alpha;
                    if (!finite_double_as_float(group_composite_opacity) ||
                        group_composite_opacity < 0.0 ||
                        group_composite_opacity > 1.0) {
                        active_drawings.erase(drawing_handle);
                        return status::invalid_graph;
                    }
                    if (group->second.transform_handle != 0U) {
                        affine_2d_double group_transform{};
                        const status transform_status = resolve_transform(
                            group->second.transform_handle,
                            group_transform);
                        if (transform_status != status::success) {
                            active_drawings.erase(drawing_handle);
                            return transform_status;
                        }
                        next.transform = compose_affine(
                            group_transform,
                            current.transform);
                    }
                    if (group->second.guideline_set_handle != 0U) {
                        const auto guidelines = guideline_sets.find(
                            group->second.guideline_set_handle);
                        if (guidelines == guideline_sets.end()) {
                            active_drawings.erase(drawing_handle);
                            return status::invalid_handle;
                        }
                        if (guidelines->second.is_dynamic) {
                            const status guideline_status =
                                apply_dynamic_guidelines(
                                    guidelines->second,
                                    next,
                                    builder,
                                    false,
                                    compile_context);
                            if (guideline_status != status::success) {
                                active_drawings.erase(drawing_handle);
                                return guideline_status;
                            }
                        } else {
                            const status guideline_status =
                                apply_static_guidelines(
                                guidelines->second.guidelines_x,
                                guidelines->second.guidelines_y,
                                next,
                                builder,
                                false);
                            if (guideline_status != status::success) {
                                active_drawings.erase(drawing_handle);
                                return guideline_status;
                            }
                        }
                    }
                    if (group->second.clip_geometry_handle != 0U) {
                        render_scope_state clipped = next;
                        const status clip_status = apply_clip(
                            group->second.clip_geometry_handle,
                            next,
                            clipped);
                        if (clip_status != status::success) {
                            active_drawings.erase(drawing_handle);
                            return clip_status;
                        }
                        next = clipped;
                    }
                    std::uint32_t opacity_mask_resource_index =
                        PROGPU_NATIVE_SCENE_NO_INDEX;
                    if (has_spatial_opacity_mask) {
                        const status spatial_mask_status =
                            add_gradient_opacity_mask(
                                group->second.opacity_mask_handle,
                                group->second.bounds_x,
                                group->second.bounds_y,
                                group->second.bounds_width,
                                group->second.bounds_height,
                                next.transform,
                                builder,
                                opacity_mask_resource_index);
                        if (spatial_mask_status != status::success) {
                            active_drawings.erase(drawing_handle);
                            return spatial_mask_status;
                        }
                    }
                    const bool isolate_group =
                        group_composite_opacity != 1.0 ||
                        has_spatial_opacity_mask;
                    if (isolate_group) {
                        progpu_native_scene_layer layer{};
                        layer.struct_size = sizeof(layer);
                        layer.flags =
                            PROGPU_NATIVE_SCENE_LAYER_FORCE_ISOLATION;
                        layer.opacity = static_cast<float>(
                            group_composite_opacity);
                        layer.blend_mode = PROGPU_NATIVE_BLEND_SRC_OVER;
                        layer.mask_resource_index =
                            opacity_mask_resource_index;
                        layer.effect_resource_index =
                            PROGPU_NATIVE_SCENE_NO_INDEX;
                        if (group->second.has_bounds) {
                            if (!try_transform_bounds(
                                    group->second.bounds_x,
                                    group->second.bounds_y,
                                    group->second.bounds_width,
                                    group->second.bounds_height,
                                    next.transform,
                                    layer.bounds)) {
                                active_drawings.erase(drawing_handle);
                                return status::invalid_graph;
                            }
                            layer.flags |= PROGPU_NATIVE_SCENE_LAYER_BOUNDS;
                        }
                        if (!builder.push_layer(layer)) {
                            active_drawings.erase(drawing_handle);
                            return status::invalid_graph;
                        }
                    }
                    if (!save_state(next)) {
                        active_drawings.erase(drawing_handle);
                        return status::invalid_graph;
                    }
                    const status group_status = append_render_stream(
                        group->second.child_render_data,
                        nullptr,
                        next,
                        drawing_depth + 1U,
                        builder,
                        brush_indices,
                        image_indices,
                        glyph_resources,
                        compile_context,
                        active_drawings,
                        clip_paths,
                        clip_segments,
                        clip_boolean_nodes,
                        metrics);
                    const bool restored = builder.restore();
                    const bool popped_group =
                        !isolate_group || builder.pop_layer();
                    active_drawings.erase(drawing_handle);
                    if (group_status != status::success) {
                        return group_status;
                    }
                    if (!restored || !popped_group) {
                        return status::invalid_graph;
                    }
                    continue;
                }
            }
            if (view.kind == command::draw_geometry || is_drawing_resource) {
                std::uint32_t padding = 0U;
                if (!is_drawing_resource) {
                    using layout = command_layouts::draw_geometry;
                    if (!has_exact_size(view, layout::fixed_size) ||
                        !read_at(
                            view.packet,
                            layout::h_brush_offset,
                            brush_handle) ||
                        !read_at(
                            view.packet,
                            layout::h_pen_offset,
                            pen_handle) ||
                        !read_at(
                            view.packet,
                            layout::h_geometry_offset,
                            geometry_handle) ||
                        !read_at(
                            view.packet,
                            layout::quad_word_pad0_offset,
                            padding)) {
                        return status::malformed_batch;
                    }
                    if (padding != 0U || geometry_handle == 0U) {
                        return status::malformed_batch;
                    }
                }
                if (brush_handle != 0U &&
                    !has_brush_state(brush_handle)) {
                    return status::invalid_handle;
                }
                const auto geometry = fixed_geometries.find(geometry_handle);
                const auto geometry_group = geometry_groups.find(
                    geometry_handle);
                const auto combined_geometry = combined_geometries.find(
                    geometry_handle);
                const auto path_geometry = path_geometries.find(
                    geometry_handle);
                if (geometry == fixed_geometries.end() &&
                    geometry_group == geometry_groups.end() &&
                    combined_geometry == combined_geometries.end() &&
                    path_geometry == path_geometries.end()) {
                    return status::invalid_handle;
                }
                fixed_geometry_state resolved_geometry{};
                if (geometry != fixed_geometries.end()) {
                    const status geometry_status = resolve_fixed_geometry(
                        geometry_handle, resolved_geometry);
                    if (geometry_status != status::success) {
                        return geometry_status;
                    }
                }
                const std::uint32_t geometry_transform_handle =
                    geometry != fixed_geometries.end()
                        ? resolved_geometry.transform_handle
                        : geometry_group != geometry_groups.end()
                            ? geometry_group->second.transform_handle
                            : combined_geometry != combined_geometries.end()
                                ? combined_geometry->second.transform_handle
                                : path_geometry->second.transform_handle;
                if (geometry_transform_handle != 0U) {
                    const status transform_status = resolve_transform(
                        geometry_transform_handle,
                        local_transform);
                    if (transform_status != status::success) {
                        return transform_status;
                    }
                }
                effective_transform = compose_affine(
                    local_transform,
                    current.transform);
                const auto combined_stroke_tolerance = [compile_context](affine_2d_double transform) {
                    // Algorithm: convert a quarter-physical-pixel target using
                    // the affine Frobenius norm. Time/space complexity: O(1).
                    const double dpi = compile_context == nullptr ? 1.0 : std::max(
                        compile_context->request.dpi_scale_x, compile_context->request.dpi_scale_y);
                    const double scale = std::hypot(std::hypot(transform.m11, transform.m12),
                        std::hypot(transform.m21, transform.m22));
                    return static_cast<float>(0.25 / (dpi * scale));
                };
                if (geometry_group != geometry_groups.end()) {
                    const bool has_zero_area =
                        affine_has_zero_area(effective_transform);
                    pen_state group_pen{};
                    bool has_group_stroke = false;
                    bool has_group_stroke_bounds = false;
                    double group_stroke_left = 0.0;
                    double group_stroke_top = 0.0;
                    double group_stroke_right = 0.0;
                    double group_stroke_bottom = 0.0;
                    // Algorithm: retain one outline per non-collapsed combined
                    // occurrence in depth-first order, then consume that same
                    // order for stroking. Repeated handles can have different
                    // transforms/tolerances. Time: O(C) bookkeeping; space: O(P)
                    // retained outline points, in addition to the core solver.
                    std::vector<std::pair<std::uint32_t, path_geometry_state>> combined_stroke_outlines;
                    if (pen_handle != 0U) {
                        const status pen_status = resolve_pen(
                            pen_handle, group_pen);
                        if (pen_status != status::success) {
                            return pen_status;
                        }
                        has_group_stroke = !has_zero_area &&
                            group_pen.brush_handle != 0U &&
                            group_pen.thickness > 0.0;
                        if (has_group_stroke) {
                            const bool tile_stroke_bounds = tile_brushes.contains(group_pen.brush_handle);
                            const auto include_stroke_bounds = [
                                this,
                                &group_pen, tile_stroke_bounds,
                                &combined_stroke_outlines, &combined_stroke_tolerance, &current,
                                &effective_transform,
                                &has_group_stroke_bounds,
                                &group_stroke_left,
                                &group_stroke_top,
                                &group_stroke_right,
                                &group_stroke_bottom](
                                auto&& self,
                                std::uint32_t child_handle,
                                affine_2d_double parent_transform,
                                std::uint32_t depth) -> status {
                                if (depth == 0U ||
                                    depth > maximum_visual_depth) {
                                    return status::invalid_graph;
                                }
                                const auto nested_group =
                                    geometry_groups.find(child_handle);
                                if (nested_group != geometry_groups.end()) {
                                    if (nested_group->second
                                            .transform_handle != 0U) {
                                        affine_2d_double nested_transform{};
                                        const status transform_status =
                                            resolve_transform(
                                                nested_group->second
                                                    .transform_handle,
                                                nested_transform);
                                        if (transform_status !=
                                            status::success) {
                                            return transform_status;
                                        }
                                        parent_transform = compose_affine(
                                            nested_transform,
                                            parent_transform);
                                    }
                                    if (affine_has_zero_area(compose_affine(
                                            parent_transform,
                                            effective_transform))) {
                                        return status::success;
                                    }
                                    for (const std::uint32_t nested_child :
                                         nested_group->second.children) {
                                        const status child_status = self(
                                            self,
                                            nested_child,
                                            parent_transform,
                                            depth + 1U);
                                        if (child_status != status::success) {
                                            return child_status;
                                        }
                                    }
                                    return status::success;
                                }
                                const auto child =
                                    path_geometries.find(child_handle);
                                const auto combined_child = combined_geometries.find(child_handle);
                                double child_left = 0.0;
                                double child_top = 0.0;
                                double child_right = 0.0;
                                double child_bottom = 0.0;
                                std::uint32_t child_transform_handle = 0U;
                                if (child != path_geometries.end()) {
                                    child_left = child->second.left;
                                    child_top = child->second.top;
                                    child_right = child->second.right;
                                    child_bottom = child->second.bottom;
                                    child_transform_handle =
                                        child->second.transform_handle;
                                } else if (combined_child != combined_geometries.end()) {
                                    child_transform_handle = combined_child->second.transform_handle;
                                } else {
                                    const auto fixed =
                                        fixed_geometries.find(child_handle);
                                    if (fixed == fixed_geometries.end()) {
                                        return status::unsupported_command;
                                    }
                                    fixed_geometry_state resolved_fixed{};
                                    const status fixed_status =
                                        resolve_fixed_geometry(
                                            child_handle,
                                            resolved_fixed);
                                    if (fixed_status != status::success) {
                                        return fixed_status;
                                    }
                                    if (resolved_fixed.kind ==
                                        fixed_geometry_kind::line) {
                                        child_left = std::min(
                                            resolved_fixed.first,
                                            resolved_fixed.third);
                                        child_top = std::min(
                                            resolved_fixed.second,
                                            resolved_fixed.fourth);
                                        child_right = std::max(
                                            resolved_fixed.first,
                                            resolved_fixed.third);
                                        child_bottom = std::max(
                                            resolved_fixed.second,
                                            resolved_fixed.fourth);
                                    } else if (
                                        resolved_fixed.kind ==
                                            fixed_geometry_kind::ellipse &&
                                        resolved_fixed.third > 0.0 &&
                                        resolved_fixed.fourth > 0.0) {
                                        child_left = resolved_fixed.first -
                                            resolved_fixed.third;
                                        child_top = resolved_fixed.second -
                                            resolved_fixed.fourth;
                                        child_right = resolved_fixed.first +
                                            resolved_fixed.third;
                                        child_bottom = resolved_fixed.second +
                                            resolved_fixed.fourth;
                                    } else if (
                                        resolved_fixed.kind ==
                                            fixed_geometry_kind::rectangle &&
                                        resolved_fixed.third > 0.0 &&
                                        resolved_fixed.fourth > 0.0) {
                                        child_left = resolved_fixed.first;
                                        child_top = resolved_fixed.second;
                                        child_right = resolved_fixed.first +
                                            resolved_fixed.third;
                                        child_bottom = resolved_fixed.second +
                                            resolved_fixed.fourth;
                                    } else {
                                        return status::unsupported_command;
                                    }
                                    child_transform_handle =
                                        resolved_fixed.transform_handle;
                                }
                                affine_2d_double child_transform =
                                    parent_transform;
                                if (child_transform_handle != 0U) {
                                    affine_2d_double local_transform{};
                                    const status transform_status =
                                        resolve_transform(
                                            child_transform_handle,
                                            local_transform);
                                    if (transform_status != status::success) {
                                        return transform_status;
                                    }
                                    child_transform = compose_affine(
                                        local_transform,
                                        parent_transform);
                                }
                                if (affine_has_zero_area(compose_affine(
                                        child_transform,
                                        effective_transform))) {
                                    return status::success;
                                }
                                if (combined_child != combined_geometries.end()) {
                                    if (current.guideline_resource_index != PROGPU_NATIVE_SCENE_NO_INDEX || current.per_point_guidelines)
                                        return status::unsupported_command;
                                    const float tolerance = combined_stroke_tolerance(
                                        compose_affine(child_transform, effective_transform));
                                    if (!std::isfinite(tolerance) || tolerance <= 0.0F) return status::unsupported_command;
                                    path_geometry_state outline;
                                    const status outlined = resolve_combined_stroke_outline(child_handle, tolerance, outline);
                                    if (outlined != status::success) return outlined;
                                    combined_stroke_outlines.emplace_back(child_handle, std::move(outline));
                                    const auto& prepared = combined_stroke_outlines.back().second;
                                    if (prepared.stroke_contours.empty()) return status::success;
                                    child_left = prepared.left;
                                    child_top = prepared.top;
                                    child_right = prepared.right;
                                    child_bottom = prepared.bottom;
                                }
                                progpu_native_image_rect child_bounds{};
                                // Tile mask storage must include the pen before
                                // each child's scale/shear, not merely expand
                                // the already-transformed group by one width.
                                const double child_expansion = tile_stroke_bounds
                                    ? group_pen.thickness * 0.5 * std::max(1.0, group_pen.miter_limit) : 0.0;
                                if (!try_transform_bounds(
                                        child_left - child_expansion,
                                        child_top - child_expansion,
                                        child_right - child_left + child_expansion * 2.0,
                                        child_bottom - child_top + child_expansion * 2.0,
                                        child_transform,
                                        child_bounds)) {
                                    return status::invalid_graph;
                                }
                                const double transformed_child_right =
                                    child_bounds.x + child_bounds.width;
                                const double transformed_child_bottom =
                                    child_bounds.y + child_bounds.height;
                                if (!has_group_stroke_bounds) {
                                    group_stroke_left = child_bounds.x;
                                    group_stroke_top = child_bounds.y;
                                    group_stroke_right =
                                        transformed_child_right;
                                    group_stroke_bottom =
                                        transformed_child_bottom;
                                    has_group_stroke_bounds = true;
                                } else {
                                    group_stroke_left = std::min(
                                        group_stroke_left,
                                        double{child_bounds.x});
                                    group_stroke_top = std::min(
                                        group_stroke_top,
                                        double{child_bounds.y});
                                    group_stroke_right = std::max(
                                        group_stroke_right,
                                        transformed_child_right);
                                    group_stroke_bottom = std::max(
                                        group_stroke_bottom,
                                        transformed_child_bottom);
                                }
                                return status::success;
                            };
                            for (const std::uint32_t child_handle :
                                 geometry_group->second.children) {
                                const status child_status =
                                    include_stroke_bounds(
                                        include_stroke_bounds,
                                        child_handle,
                                        {},
                                        1U);
                                if (child_status != status::success) {
                                    return child_status;
                                }
                            }
                        }
                    }
                    if (has_zero_area) {
                        continue;
                    }
                    if ((brush_handle == 0U && !has_group_stroke) ||
                        geometry_group->second.children.empty()) {
                        continue;
                    }
                    std::vector<progpu_native_path_segment> group_segments;
                    std::vector<progpu_native_scene_path_boolean_node>
                        group_boolean_nodes;
                    const std::uint32_t group_fill_rule =
                        geometry_group->second.fill_rule == 0U
                        ? PROGPU_NATIVE_FILL_RULE_EVEN_ODD
                        : PROGPU_NATIVE_FILL_RULE_NON_ZERO;
                    shallow_fill_leaf group_tree{};
                    status group_fill_status = status::success;
                    if (group_fill_rule ==
                        PROGPU_NATIVE_FILL_RULE_EVEN_ODD) {
                        group_fill_status = append_group_winding_program(
                            geometry_handle,
                            group_fill_rule,
                            group_segments,
                            group_boolean_nodes,
                            group_tree,
                            {},
                            1U,
                            current.per_point_guidelines,
                            false);
                    } else {
                        group_tree.fill_rule = group_fill_rule;
                        group_tree.segment_offset = 0U;
                        for (const std::uint32_t child_handle :
                             geometry_group->second.children) {
                            shallow_fill_leaf child{};
                            group_fill_status = append_group_fill_leaf(
                                child_handle,
                                group_segments,
                                child,
                                {},
                                1U,
                                current.per_point_guidelines);
                            if (group_fill_status != status::success) {
                                break;
                            }
                            if (!child.has_bounds) {
                                continue;
                            }
                            if (!group_tree.has_bounds) {
                                group_tree.left = child.left;
                                group_tree.top = child.top;
                                group_tree.right = child.right;
                                group_tree.bottom = child.bottom;
                                group_tree.has_bounds = true;
                            } else {
                                group_tree.left = std::min(
                                    group_tree.left,
                                    child.left);
                                group_tree.top = std::min(
                                    group_tree.top,
                                    child.top);
                                group_tree.right = std::max(
                                    group_tree.right,
                                    child.right);
                                group_tree.bottom = std::max(
                                    group_tree.bottom,
                                    child.bottom);
                            }
                        }
                        group_tree.segment_count = group_segments.size();
                        if (group_fill_status ==
                            status::unsupported_command) {
                            group_segments.clear();
                            group_boolean_nodes.clear();
                            group_fill_status =
                                append_group_winding_program(
                                    geometry_handle,
                                    group_fill_rule,
                                    group_segments,
                                    group_boolean_nodes,
                                    group_tree,
                                    {},
                                    1U,
                                    current.per_point_guidelines,
                                    false);
                        }
                    }
                    if (group_fill_status != status::success) {
                        return group_fill_status;
                    }
                    const bool has_group_bounds = group_tree.has_bounds;
                    const double group_left = group_tree.left;
                    const double group_top = group_tree.top;
                    const double group_right = group_tree.right;
                    const double group_bottom = group_tree.bottom;
                    const bool has_group_fill = brush_handle != 0U &&
                        !group_segments.empty() && has_group_bounds;
                    if (!has_group_fill &&
                        (!has_group_stroke || !has_group_stroke_bounds)) {
                        continue;
                    }
                    if (group_fill_rule ==
                            PROGPU_NATIVE_FILL_RULE_EVEN_ODD &&
                        group_boolean_nodes.size() == 1U &&
                        group_boolean_nodes.front().kind ==
                            PROGPU_NATIVE_PATH_BOOLEAN_LEAF) {
                        group_boolean_nodes.clear();
                    }
                    if (has_group_fill &&
                        group_boolean_nodes.size() > 63U) {
                        return status::unsupported_command;
                    }
                    if (has_group_fill && tile_brushes.contains(brush_handle)) {
                        const brush_use_state brush_use{group_left, group_top,
                            group_right - group_left, group_bottom - group_top,
                            effective_transform};
                        const status tile_status = append_path_tile_brush(brush_handle,
                            brush_use, current, group_segments, group_boolean_nodes, group_fill_rule);
                        if (tile_status != status::success) return tile_status;
                    }
                    if (has_group_fill && !tile_brushes.contains(brush_handle)) {
                        const brush_use_state brush_use{
                            group_left,
                            group_top,
                            group_right - group_left,
                            group_bottom - group_top,
                            effective_transform};
                        std::uint32_t brush_index =
                            PROGPU_NATIVE_SCENE_NO_INDEX;
                        const status brush_status = resolve_brush_index(
                            brush_handle,
                            brush_index,
                            &brush_use);
                        if (brush_status != status::success) {
                            return brush_status;
                        }
                        progpu_native_image_rect path_bounds{};
                        if (!try_transform_bounds(
                                group_left,
                                group_top,
                                group_right - group_left,
                                group_bottom - group_top,
                                effective_transform,
                                path_bounds)) {
                            return status::invalid_graph;
                        }
                        progpu_native_affine_2d native_local_transform{};
                        if (!try_to_native_affine(
                                local_transform,
                                native_local_transform)) {
                            return status::invalid_graph;
                        }
                        const std::array paths{
                            progpu_native_scene_path_fill{
                                0U,
                                group_segments.size(),
                                0U,
                                group_boolean_nodes.size(),
                                static_cast<float>(group_left),
                                static_cast<float>(group_top),
                                static_cast<float>(group_right),
                                static_cast<float>(group_bottom),
                                {1.0F, 1.0F, 1.0F, 1.0F},
                                native_local_transform,
                                static_cast<std::uint32_t>(
                                    geometry_group->second.fill_rule == 0U
                                        ? PROGPU_NATIVE_FILL_RULE_EVEN_ODD
                                        : PROGPU_NATIVE_FILL_RULE_NON_ZERO),
                                current.edge_aliased ? 1U : 8U}};
                        const std::array brushes{brush_index};
                        if (!builder.draw_paths(
                                paths,
                                group_segments,
                                brushes,
                                path_bounds,
                                PROGPU_NATIVE_SCENE_NO_INDEX,
                                group_boolean_nodes)) {
                            return status::invalid_graph;
                        }
                    }
                    if (has_group_stroke) {
                        const bool tile_pen = tile_brushes.contains(group_pen.brush_handle);
                        const double expansion =
                            tile_pen ? 0.0 : group_pen.thickness * 0.5 *
                            std::max(1.0, group_pen.miter_limit);
                        if (!finite_double_as_float(expansion)) {
                            return status::invalid_graph;
                        }
                        const brush_use_state stroke_brush_use{
                            group_stroke_left - expansion,
                            group_stroke_top - expansion,
                            group_stroke_right - group_stroke_left +
                                expansion * 2.0,
                            group_stroke_bottom - group_stroke_top +
                                expansion * 2.0,
                            effective_transform};
                        std::uint32_t stroke_brush_index =
                            PROGPU_NATIVE_SCENE_NO_INDEX;
                        std::vector<progpu_native_geometry_primitive> tile_primitives;
                        std::size_t combined_stroke_cursor = 0U;
                        const status brush_status = tile_pen ? status::success : resolve_brush_index(
                            group_pen.brush_handle,
                            stroke_brush_index,
                            &stroke_brush_use);
                        if (brush_status != status::success) {
                            return brush_status;
                        }
                        const auto append_group_stroke = [
                            this,
                            &append_path_strokes,
                            &append_resolved_line_stroke,
                            &append_positive_fixed_shape_stroke,
                            &append_tile_pen, &append_tile_line_pen, &make_tile_fixed_geometry,
                            &tile_primitives, &stroke_brush_use, tile_pen,
                            &combined_stroke_outlines, &combined_stroke_cursor,
                            &group_pen,
                            stroke_brush_index,
                            &current](
                            auto&& self,
                            std::uint32_t child_handle,
                            affine_2d_double parent_local_transform,
                            std::uint32_t depth) -> status {
                            if (depth == 0U ||
                                depth > maximum_visual_depth) {
                                return status::invalid_graph;
                            }
                            const auto nested_group =
                                geometry_groups.find(child_handle);
                            if (nested_group != geometry_groups.end()) {
                                if (nested_group->second.transform_handle !=
                                    0U) {
                                    affine_2d_double nested_transform{};
                                    const status transform_status =
                                        resolve_transform(
                                            nested_group->second
                                                .transform_handle,
                                            nested_transform);
                                    if (transform_status != status::success) {
                                        return transform_status;
                                    }
                                    parent_local_transform = compose_affine(
                                        nested_transform,
                                        parent_local_transform);
                                }
                                if (affine_has_zero_area(compose_affine(
                                        parent_local_transform,
                                        current.transform))) {
                                    return status::success;
                                }
                                for (const std::uint32_t nested_child :
                                     nested_group->second.children) {
                                    const status child_status = self(
                                        self,
                                        nested_child,
                                        parent_local_transform,
                                        depth + 1U);
                                    if (child_status != status::success) {
                                        return child_status;
                                    }
                                }
                                return status::success;
                            }
                            const auto child =
                                path_geometries.find(child_handle);
                            const auto combined_child = combined_geometries.find(child_handle);
                            fixed_geometry_state resolved_line{};
                            std::uint32_t child_transform_handle = 0U;
                            if (child != path_geometries.end()) {
                                if (current.per_point_guidelines &&
                                    !child->second
                                         .per_point_segments_supported) {
                                    return status::unsupported_command;
                                }
                                child_transform_handle =
                                    child->second.transform_handle;
                            } else if (combined_child != combined_geometries.end()) {
                                child_transform_handle = combined_child->second.transform_handle;
                            } else {
                                const status line_status =
                                    resolve_fixed_geometry(
                                        child_handle,
                                        resolved_line);
                                if (line_status != status::success) {
                                    return line_status;
                                }
                                if (resolved_line.kind !=
                                        fixed_geometry_kind::line &&
                                    !((resolved_line.kind ==
                                               fixed_geometry_kind::ellipse ||
                                          resolved_line.kind ==
                                               fixed_geometry_kind::rectangle) &&
                                        resolved_line.third > 0.0 &&
                                        resolved_line.fourth > 0.0)) {
                                    return status::unsupported_command;
                                }
                                child_transform_handle =
                                    resolved_line.transform_handle;
                            }
                            affine_2d_double child_local_transform =
                                parent_local_transform;
                            if (child_transform_handle != 0U) {
                                affine_2d_double child_transform{};
                                const status transform_status =
                                    resolve_transform(
                                        child_transform_handle,
                                        child_transform);
                                if (transform_status != status::success) {
                                    return transform_status;
                                }
                                child_local_transform = compose_affine(
                                    child_transform,
                                    parent_local_transform);
                            }
                            const affine_2d_double child_effective_transform =
                                compose_affine(
                                    child_local_transform,
                                    current.transform);
                            if (affine_has_zero_area(
                                    child_effective_transform)) {
                                return status::success;
                            }
                            const path_geometry_state* stroke_path = child != path_geometries.end() ? &child->second : nullptr;
                            if (combined_child != combined_geometries.end()) {
                                if (combined_stroke_cursor >= combined_stroke_outlines.size() ||
                                    combined_stroke_outlines[combined_stroke_cursor].first != child_handle)
                                    return status::invalid_graph;
                                stroke_path = &combined_stroke_outlines[combined_stroke_cursor++].second;
                                if (stroke_path->stroke_contours.empty()) return status::success;
                            }
                            status stroke_status = status::success;
                            if (tile_pen) {
                                brush_use_state child_use = stroke_brush_use;
                                child_use.effective_transform = child_effective_transform;
                                if (stroke_path != nullptr) {
                                    if (stroke_path->stroke_contours.empty()) return status::success;
                                    stroke_status = append_tile_pen(group_pen, child_use, current, {}, {}, false,
                                        stroke_path->stroke_contours, &tile_primitives);
                                } else if (resolved_line.kind == fixed_geometry_kind::line) {
                                    stroke_status = append_tile_line_pen(group_pen, resolved_line.first,
                                        resolved_line.second, resolved_line.third, resolved_line.fourth,
                                        child_effective_transform, current, &tile_primitives);
                                } else {
                                    const auto geometry = make_tile_fixed_geometry(resolved_line);
                                    stroke_status = append_tile_pen(group_pen, child_use, current, {}, {}, false,
                                        geometry.stroke_contours, &tile_primitives);
                                }
                            } else if (stroke_path != nullptr) {
                                stroke_status = append_path_strokes(
                                    *stroke_path,
                                    group_pen,
                                    child_local_transform,
                                    child_effective_transform,
                                    stroke_brush_index);
                            } else if (resolved_line.kind ==
                                fixed_geometry_kind::line) {
                                stroke_status = append_resolved_line_stroke(
                                    resolved_line.first,
                                    resolved_line.second,
                                    resolved_line.third,
                                    resolved_line.fourth,
                                    group_pen,
                                    child_local_transform,
                                    child_effective_transform,
                                    stroke_brush_index);
                            } else {
                                stroke_status =
                                    append_positive_fixed_shape_stroke(
                                        resolved_line,
                                        group_pen,
                                        child_local_transform,
                                        child_effective_transform,
                                        stroke_brush_index);
                            }
                            if (stroke_status != status::success) {
                                return stroke_status;
                            }
                            return status::success;
                        };
                        for (const std::uint32_t child_handle :
                             geometry_group->second.children) {
                            const status stroke_status = append_group_stroke(
                                append_group_stroke,
                                child_handle,
                                local_transform,
                                1U);
                            if (stroke_status != status::success) {
                                return stroke_status;
                            }
                        }
                        if (combined_stroke_cursor != combined_stroke_outlines.size()) return status::invalid_graph;
                        if (tile_pen) {
                            const status painted = paint_tile_pen_mask(group_pen, stroke_brush_use, current, tile_primitives);
                            if (painted != status::success) return painted;
                        }
                    }
                    continue;
                }
                if (combined_geometry != combined_geometries.end()) {
                    const bool has_zero_area =
                        affine_has_zero_area(effective_transform);
                    pen_state combined_pen{};
                    if (pen_handle != 0U) {
                        const status pen_status = resolve_pen(
                            pen_handle, combined_pen);
                        if (pen_status != status::success) {
                            return pen_status;
                        }
                    }
                    if (has_zero_area) {
                        continue;
                    }
                    const auto append_combined_pen = [&]() -> status {
                        if (combined_pen.brush_handle == 0U || combined_pen.thickness <= 0.0) return status::success;
                        if (current.guideline_resource_index != PROGPU_NATIVE_SCENE_NO_INDEX || current.per_point_guidelines)
                            return status::unsupported_command;
                        // Quarter-physical-pixel flattening target, conservatively
                        // scaled by the transform Frobenius norm. Fail if the
                        // required tolerance cannot be represented by the core.
                        const float tolerance = combined_stroke_tolerance(effective_transform);
                        if (!std::isfinite(tolerance) || tolerance <= 0.0F) return status::unsupported_command;
                        path_geometry_state outline;
                        const status outlined = resolve_combined_stroke_outline(geometry_handle, tolerance, outline);
                        if (outlined != status::success || outline.stroke_contours.empty()) return outlined;
                        if (tile_brushes.contains(combined_pen.brush_handle)) {
                            const double expansion = combined_pen.thickness * 0.5 * std::max(1.0, combined_pen.miter_limit);
                            const brush_use_state use{outline.left - expansion, outline.top - expansion,
                                outline.right - outline.left + expansion * 2.0,
                                outline.bottom - outline.top + expansion * 2.0, effective_transform};
                            return append_tile_pen(combined_pen, use, current, {}, {}, false, outline.stroke_contours);
                        }
                        return append_path_strokes(outline, combined_pen, local_transform, effective_transform,
                            PROGPU_NATIVE_SCENE_NO_INDEX);
                    };
                    if (brush_handle == 0U) {
                        const status stroked = append_combined_pen();
                        if (stroked != status::success) return stroked;
                        continue;
                    }
                    std::vector<progpu_native_path_segment>
                        combined_segments;
                    std::vector<progpu_native_scene_path_boolean_node>
                        boolean_nodes;
                    boolean_nodes.reserve(3U);
                    const std::array operands{
                        combined_geometry->second.geometry1_handle,
                        combined_geometry->second.geometry2_handle};
                    shallow_fill_leaf combined_tree{};
                    for (const std::uint32_t operand_handle : operands) {
                        shallow_fill_leaf operand{};
                        const status operand_status =
                            append_boolean_geometry(
                                operand_handle,
                                combined_segments,
                                boolean_nodes,
                                operand);
                        if (operand_status != status::success) {
                            return operand_status;
                        }
                        if (!operand.has_bounds) {
                            continue;
                        }
                        if (!combined_tree.has_bounds) {
                            combined_tree.left = operand.left;
                            combined_tree.top = operand.top;
                            combined_tree.right = operand.right;
                            combined_tree.bottom = operand.bottom;
                            combined_tree.has_bounds = true;
                        } else {
                            combined_tree.left = std::min(
                                combined_tree.left, operand.left);
                            combined_tree.top = std::min(
                                combined_tree.top, operand.top);
                            combined_tree.right = std::max(
                                combined_tree.right, operand.right);
                            combined_tree.bottom = std::max(
                                combined_tree.bottom, operand.bottom);
                        }
                    }
                    if (combined_segments.empty() ||
                        !combined_tree.has_bounds) {
                        continue;
                    }
                    progpu_native_scene_path_boolean_node operation{};
                    switch (combined_geometry->second.combine_mode) {
                    case 0U:
                        operation.kind = PROGPU_NATIVE_PATH_BOOLEAN_UNION;
                        break;
                    case 1U:
                        operation.kind = PROGPU_NATIVE_PATH_BOOLEAN_INTERSECT;
                        break;
                    case 2U:
                        operation.kind = PROGPU_NATIVE_PATH_BOOLEAN_XOR;
                        break;
                    case 3U:
                        operation.kind = PROGPU_NATIVE_PATH_BOOLEAN_DIFFERENCE;
                        break;
                    default:
                        return status::invalid_graph;
                    }
                    boolean_nodes.push_back(operation);
                    std::uint32_t brush_index =
                        PROGPU_NATIVE_SCENE_NO_INDEX;
                    const brush_use_state brush_use{
                        combined_tree.left,
                        combined_tree.top,
                        combined_tree.right - combined_tree.left,
                        combined_tree.bottom - combined_tree.top,
                        effective_transform};
                    if (tile_brushes.contains(brush_handle)) {
                        const status tile_status = append_path_tile_brush(brush_handle,
                            brush_use, current, combined_segments, boolean_nodes,
                            PROGPU_NATIVE_FILL_RULE_NON_ZERO);
                        if (tile_status != status::success) return tile_status;
                        const status stroked = append_combined_pen();
                        if (stroked != status::success) return stroked;
                        continue;
                    }
                    const status brush_status = resolve_brush_index(
                        brush_handle,
                        brush_index,
                        &brush_use);
                    if (brush_status != status::success) {
                        return brush_status;
                    }
                    progpu_native_image_rect path_bounds{};
                    if (!try_transform_bounds(
                            combined_tree.left,
                            combined_tree.top,
                            combined_tree.right - combined_tree.left,
                            combined_tree.bottom - combined_tree.top,
                            effective_transform,
                            path_bounds)) {
                        return status::invalid_graph;
                    }
                    progpu_native_affine_2d native_local_transform{};
                    if (!try_to_native_affine(
                            local_transform,
                            native_local_transform)) {
                        return status::invalid_graph;
                    }
                    const std::array paths{
                        progpu_native_scene_path_fill{
                            0U,
                            combined_segments.size(),
                            0U,
                            boolean_nodes.size(),
                            static_cast<float>(combined_tree.left),
                            static_cast<float>(combined_tree.top),
                            static_cast<float>(combined_tree.right),
                            static_cast<float>(combined_tree.bottom),
                            {1.0F, 1.0F, 1.0F, 1.0F},
                            native_local_transform,
                            PROGPU_NATIVE_FILL_RULE_NON_ZERO,
                            current.edge_aliased ? 1U : 8U}};
                    const std::array brushes{brush_index};
                    if (!builder.draw_paths(
                            paths,
                            combined_segments,
                            brushes,
                            path_bounds,
                            PROGPU_NATIVE_SCENE_NO_INDEX,
                            boolean_nodes)) {
                        return status::invalid_graph;
                    }
                    const status stroked = append_combined_pen();
                    if (stroked != status::success) return stroked;
                    continue;
                }
                if (path_geometry != path_geometries.end()) {
                    if (affine_has_zero_area(effective_transform)) {
                        if (pen_handle != 0U &&
                            !pens.contains(pen_handle)) {
                            return status::invalid_handle;
                        }
                        continue;
                    }
                    if (current.per_point_guidelines &&
                        !path_geometry->second
                            .per_point_segments_supported) {
                        return status::unsupported_command;
                    }
                    const auto& fill_segments =
                        current.per_point_guidelines &&
                            !path_geometry->second
                                .per_point_segments.empty()
                        ? path_geometry->second.per_point_segments
                        : path_geometry->second.segments;
                    progpu_native_image_rect local_path_bounds{};
                    const bool has_fill_bounds =
                        !fill_segments.empty() &&
                        try_get_path_segment_bounds(
                            fill_segments, local_path_bounds);
                    if (brush_handle != 0U && has_fill_bounds && tile_brushes.contains(brush_handle)) {
                        const brush_use_state brush_use{local_path_bounds.x, local_path_bounds.y,
                            local_path_bounds.width, local_path_bounds.height, effective_transform};
                        const status tile_status = append_path_tile_brush(brush_handle,
                            brush_use, current, fill_segments, {},
                            path_geometry->second.fill_rule == 0U
                                ? PROGPU_NATIVE_FILL_RULE_EVEN_ODD : PROGPU_NATIVE_FILL_RULE_NON_ZERO);
                        if (tile_status != status::success) return tile_status;
                    }
                    if (brush_handle != 0U && has_fill_bounds && !tile_brushes.contains(brush_handle)) {
                        std::uint32_t brush_index =
                            PROGPU_NATIVE_SCENE_NO_INDEX;
                        const brush_use_state brush_use{
                            local_path_bounds.x,
                            local_path_bounds.y,
                            local_path_bounds.width,
                            local_path_bounds.height,
                            effective_transform};
                        const status brush_status = resolve_brush_index(
                            brush_handle,
                            brush_index,
                            &brush_use);
                        if (brush_status != status::success) {
                            return brush_status;
                        }
                        progpu_native_image_rect path_bounds{};
                        if (!try_transform_bounds(
                                local_path_bounds.x,
                                local_path_bounds.y,
                                local_path_bounds.width,
                                local_path_bounds.height,
                                effective_transform,
                                path_bounds)) {
                            return status::invalid_graph;
                        }
                        progpu_native_affine_2d native_local_transform{};
                        if (!try_to_native_affine(
                                local_transform,
                                native_local_transform)) {
                            return status::invalid_graph;
                        }
                        const std::array paths{
                            progpu_native_scene_path_fill{
                                0U,
                                fill_segments.size(),
                                0U,
                                0U,
                                local_path_bounds.x,
                                local_path_bounds.y,
                                local_path_bounds.x + local_path_bounds.width,
                                local_path_bounds.y + local_path_bounds.height,
                                {1.0F, 1.0F, 1.0F, 1.0F},
                                native_local_transform,
                                static_cast<std::uint32_t>(
                                    path_geometry->second.fill_rule == 0U
                                        ? PROGPU_NATIVE_FILL_RULE_EVEN_ODD
                                        : PROGPU_NATIVE_FILL_RULE_NON_ZERO),
                                current.edge_aliased ? 1U : 8U}};
                        const std::array brushes{brush_index};
                        if (!builder.draw_paths(
                                paths,
                                fill_segments,
                                brushes,
                                path_bounds)) {
                            return status::invalid_graph;
                        }
                    }
                    if (pen_handle != 0U) {
                        pen_state pen{};
                        const status pen_status = resolve_pen(
                            pen_handle, pen);
                        if (pen_status != status::success) {
                            return pen_status;
                        }
                        status stroke_status = status::success;
                        if (tile_brushes.contains(pen.brush_handle)) {
                            const auto& geometry = path_geometry->second;
                            if (pen.thickness > 0.0 && !geometry.stroke_contours.empty()) {
                                const double expansion = pen.thickness * 0.5 * std::max(1.0, pen.miter_limit);
                                if (!finite_double_as_float(expansion)) return status::invalid_graph;
                                const brush_use_state use{geometry.left - expansion, geometry.top - expansion,
                                    geometry.right - geometry.left + expansion * 2.0,
                                    geometry.bottom - geometry.top + expansion * 2.0, effective_transform};
                                // All contours share one mask and one brush paint,
                                // including disjoint/non-stroked-segment breaks.
                                stroke_status = append_tile_pen(pen, use, current, {}, {}, false,
                                    geometry.stroke_contours);
                            }
                        } else {
                            stroke_status = append_path_strokes(path_geometry->second, pen,
                                local_transform, effective_transform, PROGPU_NATIVE_SCENE_NO_INDEX);
                        }
                        if (stroke_status != status::success) {
                            return stroke_status;
                        }
                    }
                    continue;
                }
                if (resolved_geometry.kind == fixed_geometry_kind::line) {
                    if (pen_handle != 0U) {
                        pen_state pen{};
                        const status resolved = resolve_pen(pen_handle, pen);
                        if (resolved != status::success) return resolved;
                        if (tile_brushes.contains(pen.brush_handle)) {
                            const status drawn = append_tile_line_pen(pen, resolved_geometry.first,
                                resolved_geometry.second, resolved_geometry.third, resolved_geometry.fourth,
                                effective_transform, current);
                            if (drawn != status::success) return drawn;
                            ++metrics.line_count;
                            continue;
                        }
                    }
                    const status line_status = append_line_stroke(
                        resolved_geometry.first,
                        resolved_geometry.second,
                        resolved_geometry.third,
                        resolved_geometry.fourth,
                        pen_handle,
                        local_transform,
                        effective_transform);
                    if (line_status != status::success) {
                        return line_status;
                    }
                    continue;
                }
                is_geometry_shape = true;
                first = resolved_geometry.first;
                second = resolved_geometry.second;
                third = resolved_geometry.third;
                fourth = resolved_geometry.fourth;
                radius_x = resolved_geometry.radius_x;
                radius_y = resolved_geometry.radius_y;
                is_ellipse =
                    resolved_geometry.kind == fixed_geometry_kind::ellipse;
                is_rounded = !is_ellipse &&
                    (radius_x != 0.0 || radius_y != 0.0);
            }
            if (!is_geometry_shape) {
                const bool animated_rectangle =
                    view.kind == command::draw_rectangle_animate;
                const bool animated_rounded =
                    view.kind == command::draw_rounded_rectangle_animate;
                const bool animated_ellipse =
                    view.kind == command::draw_ellipse_animate;
                const bool animated = animated_rectangle ||
                    animated_rounded || animated_ellipse;
                if (view.kind != command::draw_rectangle &&
                    !animated_rectangle &&
                    view.kind != command::draw_rounded_rectangle &&
                    !animated_rounded &&
                    view.kind != command::draw_ellipse &&
                    !animated_ellipse) {
                    return status::unsupported_command;
                }
                is_rounded =
                    view.kind == command::draw_rounded_rectangle ||
                    animated_rounded;
                is_ellipse = view.kind == command::draw_ellipse ||
                    animated_ellipse;
                const std::size_t fixed_size = is_rounded
                    ? animated
                        ? command_layouts::draw_rounded_rectangle_animate::
                            fixed_size
                        : command_layouts::draw_rounded_rectangle::fixed_size
                    : is_ellipse
                        ? animated
                            ? command_layouts::draw_ellipse_animate::fixed_size
                            : command_layouts::draw_ellipse::fixed_size
                        : animated
                            ? command_layouts::draw_rectangle_animate::
                                fixed_size
                            : command_layouts::draw_rectangle::fixed_size;
                const std::size_t geometry_offset = is_rounded
                    ? command_layouts::draw_rounded_rectangle::rectangle_offset
                    : is_ellipse
                        ? command_layouts::draw_ellipse::center_offset
                        : command_layouts::draw_rectangle::rectangle_offset;
                if (!has_exact_size(view, fixed_size) ||
                    !read_at(view.packet, geometry_offset, first) ||
                    !read_at(view.packet, geometry_offset + 8U, second) ||
                    !read_at(view.packet, geometry_offset + 16U, third) ||
                    !read_at(view.packet, geometry_offset + 24U, fourth)) {
                    return status::malformed_batch;
                }
                if (is_rounded) {
                    using layout =
                        command_layouts::draw_rounded_rectangle;
                    if (!read_at(
                            view.packet,
                            layout::radius_x_offset,
                            radius_x) ||
                        !read_at(
                            view.packet,
                            layout::radius_y_offset,
                            radius_y) ||
                        !read_at(
                            view.packet,
                            layout::h_brush_offset,
                            brush_handle) ||
                        !read_at(
                        view.packet,
                        layout::h_pen_offset,
                        pen_handle)) {
                        return status::malformed_batch;
                    }
                } else if (is_ellipse) {
                    using layout = command_layouts::draw_ellipse;
                    if (!read_at(
                            view.packet,
                            layout::h_brush_offset,
                            brush_handle) ||
                        !read_at(
                            view.packet,
                            layout::h_pen_offset,
                            pen_handle)) {
                        return status::malformed_batch;
                    }
                } else {
                    using layout = command_layouts::draw_rectangle;
                    if (!read_at(
                            view.packet,
                            layout::h_brush_offset,
                            brush_handle) ||
                        !read_at(
                            view.packet,
                            layout::h_pen_offset,
                            pen_handle)) {
                        return status::malformed_batch;
                    }
                }
                if (!finite_double_as_float(first) ||
                    !finite_double_as_float(second) ||
                    !finite_double_as_float(third) ||
                    !finite_double_as_float(fourth) ||
                    (is_rounded &&
                     (!finite_double_as_float(radius_x) ||
                      !finite_double_as_float(radius_y)))) {
                    return status::malformed_batch;
                }
                if (animated) {
                    std::uint32_t primary_animation = 0U;
                    std::uint32_t radius_x_animation = 0U;
                    std::uint32_t radius_y_animation = 0U;
                    std::uint32_t padding = 0U;
                    const std::size_t primary_offset = is_rounded
                        ? command_layouts::draw_rounded_rectangle_animate::
                            h_rectangle_animations_offset
                        : is_ellipse
                            ? command_layouts::draw_ellipse_animate::
                                h_center_animations_offset
                            : command_layouts::draw_rectangle_animate::
                                h_rectangle_animations_offset;
                    const std::size_t padding_offset = is_rounded
                        ? command_layouts::draw_rounded_rectangle_animate::
                            quad_word_pad0_offset
                        : is_ellipse
                            ? command_layouts::draw_ellipse_animate::
                                quad_word_pad0_offset
                            : command_layouts::draw_rectangle_animate::
                                quad_word_pad0_offset;
                    if (!read_at(
                            view.packet,
                            primary_offset,
                            primary_animation) ||
                        !read_at(view.packet, padding_offset, padding) ||
                        padding != 0U) {
                        return status::malformed_batch;
                    }
                    if (is_ellipse) {
                        const status center_status = resolve_animated_point(
                            first,
                            second,
                            primary_animation,
                            first,
                            second);
                        if (center_status != status::success) {
                            return center_status;
                        }
                    } else {
                        const status rectangle_status = resolve_animated_rect(
                            first,
                            second,
                            third,
                            fourth,
                            primary_animation,
                            first,
                            second,
                            third,
                            fourth);
                        if (rectangle_status != status::success) {
                            return rectangle_status;
                        }
                    }
                    if (is_rounded || is_ellipse) {
                        const std::size_t radius_x_offset = is_rounded
                            ? command_layouts::
                                draw_rounded_rectangle_animate::
                                    h_radius_x_animations_offset
                            : command_layouts::draw_ellipse_animate::
                                h_radius_x_animations_offset;
                        const std::size_t radius_y_offset = is_rounded
                            ? command_layouts::
                                draw_rounded_rectangle_animate::
                                    h_radius_y_animations_offset
                            : command_layouts::draw_ellipse_animate::
                                h_radius_y_animations_offset;
                        if (!read_at(
                                view.packet,
                                radius_x_offset,
                                radius_x_animation) ||
                            !read_at(
                                view.packet,
                                radius_y_offset,
                                radius_y_animation)) {
                            return status::malformed_batch;
                        }
                        double& resolved_radius_x =
                            is_ellipse ? third : radius_x;
                        double& resolved_radius_y =
                            is_ellipse ? fourth : radius_y;
                        const status radius_x_status =
                            resolve_animated_double(
                                resolved_radius_x,
                                radius_x_animation,
                                resolved_radius_x);
                        if (radius_x_status != status::success) {
                            return radius_x_status;
                        }
                        const status radius_y_status =
                            resolve_animated_double(
                                resolved_radius_y,
                                radius_y_animation,
                                resolved_radius_y);
                        if (radius_y_status != status::success) {
                            return radius_y_status;
                        }
                    }
                }
            }
            if (!finite_double_as_float(first) ||
                !finite_double_as_float(second) ||
                !finite_double_as_float(third) ||
                !finite_double_as_float(fourth) || third < 0.0 ||
                fourth < 0.0) {
                return status::malformed_batch;
            }
            if (is_rounded &&
                (!finite_double_as_float(radius_x) ||
                 !finite_double_as_float(radius_y) || radius_x < 0.0 ||
                 radius_y < 0.0)) {
                return status::malformed_batch;
            }
            if (affine_has_zero_area(effective_transform)) {
                if (brush_handle != 0U &&
                    !has_brush_state(brush_handle)) {
                    return status::invalid_handle;
                }
                if (pen_handle != 0U && !pens.contains(pen_handle)) {
                    return status::invalid_handle;
                }
                continue;
            }
            const double x = is_ellipse ? first - third : first;
            const double y = is_ellipse ? second - fourth : second;
            const double width = is_ellipse ? third * 2.0 : third;
            const double height = is_ellipse ? fourth * 2.0 : fourth;
            if (!finite_double_as_float(x) || !finite_double_as_float(y) ||
                !finite_double_as_float(width) ||
                !finite_double_as_float(height)) {
                return status::malformed_batch;
            }
            const bool has_rounded_corners = is_rounded &&
                radius_x > 0.0 && radius_y > 0.0;
            if (brush_handle == 0U && pen_handle == 0U) {
                continue;
            }
            if (brush_handle != 0U &&
                !has_brush_state(brush_handle)) {
                return status::invalid_handle;
            }
            if (pen_handle != 0U && !pens.contains(pen_handle)) {
                return status::invalid_handle;
            }
            progpu_native_affine_2d native_local_transform{};
            if (!try_to_native_affine(
                    local_transform,
                    native_local_transform)) {
                return status::invalid_graph;
            }
            const bool has_tile_fill = brush_handle != 0U && tile_brushes.contains(brush_handle);
            if (has_tile_fill && width > 0.0 && height > 0.0) {
                render_scope_state paint_state = current;
                if (is_ellipse || has_rounded_corners) {
                    // Append to the inherited vector chain so the subsequent
                    // paint/viewport clips intersect this shape rather than
                    // replacing its mask. Segment coordinates stay local; the
                    // path owns the complete geometry-to-target transform.
                    if (current.clip_path_count >= 64U ||
                        !finite_double_as_float(x + width) ||
                        !finite_double_as_float(y + height)) {
                        return status::unsupported_command;
                    }
                    progpu_native_affine_2d shape_transform{};
                    if (!try_to_native_affine(effective_transform, shape_transform)) {
                        return status::invalid_graph;
                    }
                    clip_paths.resize(current.clip_path_count);
                    clip_segments.resize(current.clip_segment_count);
                    clip_boolean_nodes.resize(current.clip_boolean_node_count);
                    const auto segment_offset = clip_segments.size();
                    if (is_ellipse) {
                        progpu_native_path_segment arc{};
                        arc.kind = PROGPU_NATIVE_PATH_SEGMENT_ARC;
                        arc.p0 = arc.p1 = {static_cast<float>(first + third),
                            static_cast<float>(second)};
                        arc.p2 = {static_cast<float>(first), static_cast<float>(second)};
                        arc.p3 = {static_cast<float>(third), static_cast<float>(fourth)};
                        arc.pad1 = std::bit_cast<std::uint32_t>(2.0F * std::numbers::pi_v<float>);
                        clip_segments.push_back(arc);
                    } else {
                        append_rounded_rectangle_path(clip_segments, x, y,
                            x + width, y + height, radius_x, radius_y);
                    }
                    clip_paths.push_back({segment_offset,
                        clip_segments.size() - segment_offset,
                        0U, 0U,
                        static_cast<float>(x), static_cast<float>(y),
                        static_cast<float>(x + width), static_cast<float>(y + height),
                        shape_transform, PROGPU_NATIVE_FILL_RULE_NON_ZERO,
                        current.edge_aliased ? 1U : 8U, PROGPU_NATIVE_CLIP_INTERSECT, 0U});
                    if (!builder.add_vector_clip_mask(clip_paths, clip_segments,
                            clip_boolean_nodes, 1.0F, paint_state.mask_resource_index)) {
                        clip_paths.resize(current.clip_path_count);
                        clip_segments.resize(current.clip_segment_count);
                        return status::invalid_graph;
                    }
                    paint_state.clip_path_count = clip_paths.size();
                    paint_state.clip_segment_count = clip_segments.size();
                    paint_state.clip_boolean_node_count = clip_boolean_nodes.size();
                }
                const brush_use_state brush_use{x, y, width, height, effective_transform};
                const status tile_status = append_single_tile_brush(brush_handle, brush_use, paint_state);
                if (tile_status != status::success) return tile_status;
            }
            if (!has_tile_fill && brush_handle != 0U && width > 0.0 && height > 0.0) {
                progpu_native_image_rect fill_bounds{};
                if (!try_transform_bounds(
                        x,
                        y,
                        width,
                        height,
                        effective_transform,
                        fill_bounds)) {
                    return status::invalid_graph;
                }
                std::uint32_t brush_index = PROGPU_NATIVE_SCENE_NO_INDEX;
                const brush_use_state brush_use{
                    x, y, width, height, effective_transform};
                const status brush_status = resolve_brush_index(
                    brush_handle,
                    brush_index,
                    &brush_use);
                if (brush_status != status::success) {
                    return brush_status;
                }
                const std::array brushes{brush_index};
                if (has_rounded_corners && radius_x != radius_y) {
                    const auto rounded_geometry =
                        make_rounded_rectangle_geometry(
                            x,
                            y,
                            width,
                            height,
                            radius_x,
                            radius_y);
                    const std::array paths{
                        progpu_native_scene_path_fill{
                            0U,
                            rounded_geometry.segments.size(),
                            0U,
                            0U,
                            static_cast<float>(rounded_geometry.left),
                            static_cast<float>(rounded_geometry.top),
                            static_cast<float>(rounded_geometry.right),
                            static_cast<float>(rounded_geometry.bottom),
                            {1.0F, 1.0F, 1.0F, 1.0F},
                            native_local_transform,
                            PROGPU_NATIVE_FILL_RULE_NON_ZERO,
                            current.edge_aliased ? 1U : 8U}};
                    if (!builder.draw_paths(
                            paths,
                            rounded_geometry.segments,
                            brushes,
                            fill_bounds)) {
                        return status::invalid_graph;
                    }
                } else {
                    const std::array primitive{
                        progpu_native_analytic_primitive{
                            static_cast<std::uint32_t>(
                                is_ellipse
                                    ? PROGPU_NATIVE_PRIMITIVE_ELLIPSE
                                    : has_rounded_corners
                                        ? PROGPU_NATIVE_PRIMITIVE_ROUNDED_RECTANGLE
                                        : PROGPU_NATIVE_PRIMITIVE_RECTANGLE),
                            current.edge_aliased
                                ? static_cast<std::uint32_t>(
                                    PROGPU_NATIVE_PRIMITIVE_FLAG_EDGE_ALIASED)
                                : 0U,
                            static_cast<float>(x),
                            static_cast<float>(y),
                            static_cast<float>(width),
                            static_cast<float>(height),
                            static_cast<float>(radius_x),
                            0.0F,
                            {1.0F, 1.0F, 1.0F, 1.0F},
                            native_local_transform}};
                    if (!builder.draw_analytic(
                            primitive,
                            brushes,
                            fill_bounds)) {
                        return status::invalid_graph;
                    }
                }
            }
            if (pen_handle != 0U) {
                pen_state pen{};
                const status pen_status = resolve_pen(pen_handle, pen);
                if (pen_status != status::success) {
                    return pen_status;
                }
                if (pen.brush_handle != 0U && pen.thickness > 0.0) {
                    if (tile_brushes.contains(pen.brush_handle)) {
                        fixed_geometry_state shape{};
                        shape.kind = is_ellipse ? fixed_geometry_kind::ellipse : fixed_geometry_kind::rectangle;
                        shape.first = first;
                        shape.second = second;
                        shape.third = third;
                        shape.fourth = fourth;
                        shape.radius_x = radius_x;
                        shape.radius_y = radius_y;
                        const double half = pen.thickness * 0.5;
                        const brush_use_state use{x - half, y - half, width + pen.thickness,
                            height + pen.thickness, effective_transform};
                        status drawn = status::success;
                        if (width == 0.0 || height == 0.0) {
                            // Unrounded rectangles ignore otherwise unused radii,
                            // exactly as the ordinary collapsed-shape route does.
                            if (!has_rounded_corners) shape.radius_x = shape.radius_y = 0.0;
                            drawn = append_degenerate_tile_shape(shape, pen, use, current);
                        } else {
                            const auto geometry = make_tile_fixed_geometry(shape);
                            const auto& contour = geometry.stroke_contours.front();
                            drawn = append_tile_pen(pen, use, current,
                                contour.segments, contour.smooth_joins, true);
                        }
                        if (drawn != status::success) return drawn;
                    } else if (width == 0.0 || height == 0.0) {
                        const status stroke_status = is_ellipse
                            ? append_degenerate_ellipse_stroke(
                                  first,
                                  second,
                                  third,
                                  fourth,
                                  pen,
                                  local_transform,
                                  effective_transform)
                            : append_degenerate_rectangle_stroke(
                                  x,
                                  y,
                                  width,
                                  height,
                                  has_rounded_corners ? radius_x : 0.0,
                                  has_rounded_corners ? radius_y : 0.0,
                                  pen,
                                  local_transform,
                                  effective_transform);
                        if (stroke_status != status::success) {
                            return stroke_status;
                        }
                    } else {
                        fixed_geometry_state positive_geometry{};
                        positive_geometry.kind = is_ellipse
                            ? fixed_geometry_kind::ellipse
                            : fixed_geometry_kind::rectangle;
                        positive_geometry.first = first;
                        positive_geometry.second = second;
                        positive_geometry.third = third;
                        positive_geometry.fourth = fourth;
                        positive_geometry.radius_x = radius_x;
                        positive_geometry.radius_y = radius_y;
                        const status stroke_status =
                            append_positive_fixed_shape_stroke(
                                positive_geometry,
                                pen,
                                local_transform,
                                effective_transform,
                                PROGPU_NATIVE_SCENE_NO_INDEX);
                        if (stroke_status != status::success) {
                            return stroke_status;
                        }
                    }
                }
            }
            if (is_ellipse) {
                ++metrics.ellipse_count;
            } else if (is_rounded) {
                ++metrics.rounded_rectangle_count;
            } else {
                ++metrics.rectangle_count;
            }
        }
    }

    static void intersect_scope_clip(
        render_scope_state& state,
        const progpu_native_image_rect& clip) noexcept {
        if (!state.has_clip) {
            state.clip_rect = clip;
            state.has_clip = true;
            return;
        }
        const float left = std::max(state.clip_rect.x, clip.x);
        const float top = std::max(state.clip_rect.y, clip.y);
        const float right = std::min(
            state.clip_rect.x + state.clip_rect.width,
            clip.x + clip.width);
        const float bottom = std::min(
            state.clip_rect.y + state.clip_rect.height,
            clip.y + clip.height);
        state.clip_rect = {
            left,
            top,
            std::max(0.0F, right - left),
            std::max(0.0F, bottom - top)};
    }

    status resolve_uniform_opacity_mask_alpha(
        std::uint32_t brush_handle,
        double& alpha) const noexcept {
        alpha = 1.0;
        if (brush_handle == 0U) {
            return status::success;
        }
        progpu_native_color color{};
        double opacity = 0.0;
        const status brush_status = resolve_solid_brush(
            brush_handle,
            color,
            opacity);
        if (brush_status != status::success) {
            return brush_status;
        }
        alpha = opacity * static_cast<double>(color.a);
        if (!finite_double_as_float(alpha) ||
            alpha < 0.0 || alpha > 1.0) {
            return status::invalid_graph;
        }
        return status::success;
    }

    status resolve_gradient_scene_brush(
        std::uint32_t brush_handle,
        const brush_use_state& use,
        progpu_native_scene_brush& native,
        std::vector<progpu_native_scene_gradient_stop>& stops) const {
        native = {};
        stops.clear();
        const auto gradient = gradient_brushes.find(brush_handle);
        if (gradient == gradient_brushes.end()) {
            return status::invalid_handle;
        }
        if (use.width <= 0.0 || use.height <= 0.0) {
            return status::unsupported_command;
        }
        const auto& source = gradient->second;
        double opacity = 0.0;
        const status opacity_status = resolve_animated_double(
            source.opacity,
            source.opacity_animation,
            opacity);
        if (opacity_status != status::success) {
            return opacity_status;
        }
        if (!finite_double_as_float(opacity) ||
            opacity < 0.0 || opacity > 1.0) {
            return status::invalid_graph;
        }
        double first_x = 0.0;
        double first_y = 0.0;
        double second_x = 0.0;
        double second_y = 0.0;
        const status first_status = resolve_animated_point(
            source.first_x,
            source.first_y,
            source.first_point_animation,
            first_x,
            first_y);
        const status second_status = resolve_animated_point(
            source.second_x,
            source.second_y,
            source.second_point_animation,
            second_x,
            second_y);
        if (first_status != status::success) {
            return first_status;
        }
        if (second_status != status::success) {
            return second_status;
        }
        double radius_x = source.radius_x;
        double radius_y = source.radius_y;
        if (source.type == gradient_brush_state::kind::radial) {
            const status radius_x_status = resolve_animated_double(
                source.radius_x,
                source.radius_x_animation,
                radius_x);
            const status radius_y_status = resolve_animated_double(
                source.radius_y,
                source.radius_y_animation,
                radius_y);
            if (radius_x_status != status::success) {
                return radius_x_status;
            }
            if (radius_y_status != status::success) {
                return radius_y_status;
            }
        }
        if (source.mapping_mode == 1U) {
            first_x = use.x + first_x * use.width;
            first_y = use.y + first_y * use.height;
            second_x = use.x + second_x * use.width;
            second_y = use.y + second_y * use.height;
            radius_x *= use.width;
            radius_y *= use.height;
        }
        if (!finite_double_as_float(first_x) ||
            !finite_double_as_float(first_y) ||
            !finite_double_as_float(second_x) ||
            !finite_double_as_float(second_y) ||
            (source.type == gradient_brush_state::kind::radial &&
                (!finite_double_as_float(radius_x) ||
                 !finite_double_as_float(radius_y) ||
                 radius_x < 0.0 || radius_y < 0.0 ||
                 (radius_x == 0.0 && radius_y == 0.0)))) {
            return status::invalid_graph;
        }
        affine_2d_double brush_transform{};
        if (source.relative_transform_handle != 0U) {
            affine_2d_double relative{};
            const status relative_status = resolve_transform(
                source.relative_transform_handle,
                relative);
            if (relative_status != status::success) {
                return relative_status;
            }
            const affine_2d_double to_relative{
                1.0 / use.width,
                0.0,
                0.0,
                1.0 / use.height,
                -use.x / use.width,
                -use.y / use.height};
            const affine_2d_double from_relative{
                use.width,
                0.0,
                0.0,
                use.height,
                use.x,
                use.y};
            brush_transform = compose_affine(
                compose_affine(to_relative, relative),
                from_relative);
        }
        if (source.transform_handle != 0U) {
            affine_2d_double absolute{};
            const status absolute_status = resolve_transform(
                source.transform_handle,
                absolute);
            if (absolute_status != status::success) {
                return absolute_status;
            }
            brush_transform = compose_affine(
                brush_transform,
                absolute);
        }
        affine_2d_double inverse_draw{};
        affine_2d_double inverse_brush{};
        if (!try_invert_affine(use.effective_transform, inverse_draw) ||
            !try_invert_affine(brush_transform, inverse_brush)) {
            return status::unsupported_command;
        }
        const affine_2d_double coordinate = compose_affine(
            inverse_draw,
            inverse_brush);
        progpu_native_affine_2d native_coordinate{};
        if (!try_to_native_affine(coordinate, native_coordinate)) {
            return status::invalid_graph;
        }

        native.coordinate_transform0[0] = 1.0F;
        native.coordinate_transform1[1] = 1.0F;
        if (source.stops.empty()) {
            native.type = PROGPU_NATIVE_SCENE_BRUSH_SOLID;
            native.opacity = 0.0F;
            return status::success;
        }
        if (source.stops.size() == 1U) {
            native.type = PROGPU_NATIVE_SCENE_BRUSH_SOLID;
            native.opacity = static_cast<float>(opacity);
            native.colors[0] = sc_rgb_to_s_rgb(source.stops[0].color);
            return status::success;
        }
        std::vector<gradient_stop_state> working;
        try {
            working = source.stops;
        } catch (const std::bad_alloc&) {
            return status::capacity_exceeded;
        }
        for (auto& stop : working) {
            stop.position = static_cast<float>(stop.position);
            if (source.color_interpolation_mode == 1U) {
                stop.color = sc_rgb_to_s_rgb(stop.color);
            }
        }
        try {
            std::stable_sort(
                working.begin(),
                working.end(),
                [](const gradient_stop_state& left,
                   const gradient_stop_state& right) {
                    return left.position < right.position;
                });
        } catch (const std::bad_alloc&) {
            return status::capacity_exceeded;
        }
        // WPF's GradientTexture.cpp normalizes an ordered stop dependency
        // chain with a relative float epsilon. This resource-compilation pass
        // is intentionally scalar: stable ordering, in-place consolidation,
        // and the previous normalized stop determine each next decision, so
        // its lanes are not independent and intrinsic SIMD is inapplicable.
        const auto coincident = [](float left, float right) noexcept {
            const float divisor = right == 0.0F ? 1.0F : right;
            return std::abs((left - right) / divisor) <
                10.0F * std::numeric_limits<float>::epsilon();
        };
        const auto less_than = [&coincident](
            float value, float boundary) noexcept {
            return value < boundary && !coincident(value, boundary);
        };
        const auto less_than_or_equal = [&coincident](
            float value, float boundary) noexcept {
            return value < boundary || coincident(value, boundary);
        };
        const auto color_at = [](const gradient_stop_state& left,
                                  const gradient_stop_state& right,
                                  float position) noexcept {
            const float left_position = static_cast<float>(left.position);
            const float right_position = static_cast<float>(right.position);
            const float factor = (position - left_position) /
                (right_position - left_position);
            return interpolate_color(left.color, right.color, factor);
        };

        progpu_native_color start_extend_color{};
        progpu_native_color end_extend_color{};
        std::size_t current_index = 0U;
        if (less_than_or_equal(
                static_cast<float>(working.front().position), 0.0F)) {
            while (current_index < working.size() &&
                   less_than(
                       static_cast<float>(working[current_index].position),
                       0.0F)) {
                ++current_index;
            }
            if (current_index == working.size()) {
                working[0] = {0.0, working.back().color};
                start_extend_color = working[0].color;
            } else if (coincident(
                           static_cast<float>(
                               working[current_index].position),
                           0.0F)) {
                start_extend_color = working[current_index].color;
                ++current_index;
                while (current_index < working.size() &&
                       coincident(
                           static_cast<float>(
                               working[current_index].position),
                           0.0F)) {
                    ++current_index;
                }
                working[0] = {0.0, working[current_index - 1U].color};
            } else {
                const auto color = color_at(
                    working[current_index - 1U],
                    working[current_index],
                    0.0F);
                working[0] = {0.0, color};
                start_extend_color = color;
            }
        } else {
            try {
                working.insert(working.begin(), working.front());
            } catch (const std::bad_alloc&) {
                return status::capacity_exceeded;
            }
            working[0].position = 0.0;
            start_extend_color = working[0].color;
            current_index = 1U;
        }

        std::size_t next_free_index = 1U;
        while (current_index < working.size() &&
               less_than(
                   static_cast<float>(working[current_index].position),
                   1.0F)) {
            if (coincident(
                    static_cast<float>(
                        working[current_index - 1U].position),
                    static_cast<float>(working[current_index].position))) {
                std::size_t not_coincident_index = current_index + 1U;
                while (not_coincident_index < working.size() &&
                       less_than(
                           static_cast<float>(
                               working[not_coincident_index].position),
                           1.0F) &&
                       coincident(
                           static_cast<float>(
                               working[current_index - 1U].position),
                           static_cast<float>(
                               working[not_coincident_index].position))) {
                    ++not_coincident_index;
                }
                --not_coincident_index;
                working[not_coincident_index].position =
                    working[current_index - 1U].position;
                current_index = not_coincident_index;
            }
            working[next_free_index++] = working[current_index++];
        }

        gradient_stop_state last_stop{};
        if (current_index == working.size()) {
            last_stop = {1.0, working.back().color};
            end_extend_color = working.back().color;
        } else if (coincident(
                       static_cast<float>(working[current_index].position),
                       1.0F)) {
            last_stop = {1.0, working[current_index].color};
            ++current_index;
            while (current_index < working.size() &&
                   coincident(
                       static_cast<float>(working[current_index].position),
                       1.0F)) {
                ++current_index;
            }
            end_extend_color = working[current_index - 1U].color;
        } else {
            const auto color = color_at(
                working[current_index - 1U],
                working[current_index],
                1.0F);
            last_stop = {1.0, color};
            end_extend_color = color;
        }
        try {
            if (next_free_index == working.size()) {
                working.push_back(last_stop);
            } else {
                working[next_free_index] = last_stop;
            }
        } catch (const std::bad_alloc&) {
            return status::capacity_exceeded;
        }
        working.resize(next_free_index + 1U);

        try {
            stops.reserve(working.size());
            for (const auto& stop : working) {
                stops.push_back({
                    stop.color,
                    static_cast<float>(stop.position),
                    0U,
                    0U,
                    0U});
            }
        } catch (const std::bad_alloc&) {
            return status::capacity_exceeded;
        }
        if (source.color_interpolation_mode == 0U) {
            for (auto& stop : stops) {
                stop.color = sc_rgb_to_s_rgb(stop.color);
            }
            start_extend_color = sc_rgb_to_s_rgb(start_extend_color);
            end_extend_color = sc_rgb_to_s_rgb(end_extend_color);
        }
        native = {};
        native.type = source.type == gradient_brush_state::kind::linear
            ? PROGPU_NATIVE_SCENE_BRUSH_LINEAR_GRADIENT
            : PROGPU_NATIVE_SCENE_BRUSH_RADIAL_GRADIENT;
        native.opacity = static_cast<float>(opacity);
        native.start_point = {
            static_cast<float>(
                source.type == gradient_brush_state::kind::linear
                    ? first_x
                    : second_x),
            static_cast<float>(
                source.type == gradient_brush_state::kind::linear
                    ? first_y
                    : second_y)};
        native.end_point = {
            static_cast<float>(second_x),
            static_cast<float>(second_y)};
        native.center = {
            static_cast<float>(first_x),
            static_cast<float>(first_y)};
        native.radius = static_cast<float>(radius_x);
        native.radius_y = static_cast<float>(radius_y);
        native.stop_count = static_cast<std::uint32_t>(stops.size());
        native.spread_method = source.spread_method;
        const auto same_color = [](const progpu_native_color& left,
                                   const progpu_native_color& right) noexcept {
            return left.r == right.r && left.g == right.g &&
                left.b == right.b && left.a == right.a;
        };
        if (source.spread_method == PROGPU_NATIVE_SCENE_GRADIENT_PAD &&
            (!same_color(start_extend_color, stops.front().color) ||
                !same_color(end_extend_color, stops.back().color))) {
            native.spread_method |=
                PROGPU_NATIVE_SCENE_GRADIENT_PAD_OUTSIDE_COLORS;
            native.colors[0] = start_extend_color;
            native.colors[1] = end_extend_color;
        }
        native.color_interpolation_mode =
            source.color_interpolation_mode == 0U
                ? PROGPU_NATIVE_SCENE_GRADIENT_INTERPOLATE_SCRGB
                : PROGPU_NATIVE_SCENE_GRADIENT_INTERPOLATE_SRGB;
        native.coordinate_transform0[0] = native_coordinate.m11;
        native.coordinate_transform0[1] = native_coordinate.m21;
        native.coordinate_transform0[2] = native_coordinate.m31;
        native.coordinate_transform1[0] = native_coordinate.m12;
        native.coordinate_transform1[1] = native_coordinate.m22;
        native.coordinate_transform1[2] = native_coordinate.m32;
        return status::success;
    }

    static status apply_static_guidelines(
        std::span<const double> guidelines_x,
        std::span<const double> guidelines_y,
        render_scope_state& state,
        native::semantic_scene_builder& builder,
        bool composite_only) {
        const bool multiple =
            guidelines_x.size() > 1U || guidelines_y.size() > 1U;
        if (state.transform.m12 != 0.0 || state.transform.m21 != 0.0 ||
            (guidelines_x.empty() && guidelines_y.empty())) {
            state.guideline_resource_index =
                PROGPU_NATIVE_SCENE_NO_INDEX;
            state.per_point_guidelines = false;
            return status::success;
        }
        std::vector<double> mapped_x;
        std::vector<double> mapped_y;
        try {
            mapped_x.reserve(guidelines_x.size());
            mapped_y.reserve(guidelines_y.size());
        } catch (const std::bad_alloc&) {
            return status::capacity_exceeded;
        }
        const auto map_axis = [](
            std::span<const double> source,
            double scale,
            double offset,
            std::vector<double>& destination) {
            if (scale < 0.0) {
                for (auto iterator = source.rbegin();
                     iterator != source.rend();
                     ++iterator) {
                    destination.push_back(static_cast<float>(
                        static_cast<float>(*iterator) *
                            static_cast<float>(scale) +
                        static_cast<float>(offset)));
                }
            } else {
                for (double coordinate : source) {
                    destination.push_back(static_cast<float>(
                        static_cast<float>(coordinate) *
                            static_cast<float>(scale) +
                        static_cast<float>(offset)));
                }
            }
        };
        try {
            map_axis(
                guidelines_x,
                state.transform.m11,
                state.transform.m31,
                mapped_x);
            map_axis(
                guidelines_y,
                state.transform.m22,
                state.transform.m32,
                mapped_y);
        } catch (const std::bad_alloc&) {
            return status::capacity_exceeded;
        }
        std::uint32_t guideline_resource_index =
            PROGPU_NATIVE_SCENE_NO_INDEX;
        if (!builder.add_guideline_set(
                mapped_x,
                mapped_y,
                guideline_resource_index,
                multiple && composite_only,
                multiple && !composite_only)) {
            return status::invalid_graph;
        }
        state.guideline_resource_index = guideline_resource_index;
        state.per_point_guidelines = multiple && !composite_only;
        return status::success;
    }

    static float wpf_offset_to_rounded(float value) noexcept {
        constexpr float minimum_without_fraction = 8'388'608.0F;
        if (!(std::abs(value) < minimum_without_fraction)) {
            return 0.0F;
        }
        const float rounded = std::floor(value + 0.5F);
        float offset = rounded - value;
        if (offset <= -0.5F) {
            offset += 1.0F;
        }
        return offset;
    }

    static void advance_dynamic_guideline(
        guideline_set_state::runtime_state& runtime,
        float coordinate,
        std::uint32_t current_time,
        bool& needs_more_cycles) noexcept {
        constexpr std::uint32_t time_mask = (1U << 29U) - 1U;
        constexpr std::uint32_t critical_time = 200U;
        constexpr float allowed_step = 0.05F;
        constexpr float big_jump = 3.0F;
        const auto bumped_recently = [&]() noexcept {
            return ((current_time - runtime.bump_time) & time_mask) <
                critical_time;
        };
        switch (runtime.current_phase) {
        case guideline_set_state::phase::start:
        case guideline_set_state::phase::flight:
            runtime.last_given_coordinate = coordinate;
            runtime.last_offset = wpf_offset_to_rounded(coordinate);
            runtime.bump_time = current_time & time_mask;
            runtime.current_phase = guideline_set_state::phase::quiet;
            break;
        case guideline_set_state::phase::quiet: {
            const bool recent = bumped_recently();
            bool bumped = runtime.last_given_coordinate != coordinate;
            if (bumped) {
                if (std::abs(coordinate - runtime.last_given_coordinate) >=
                    big_jump) {
                    bumped = false;
                    runtime.bump_time =
                        (current_time - critical_time) & time_mask;
                } else {
                    runtime.bump_time = current_time & time_mask;
                }
                runtime.last_given_coordinate = coordinate;
            }
            if (bumped && recent) {
                runtime.current_phase =
                    guideline_set_state::phase::animation;
                runtime.last_offset = 0.0F;
                needs_more_cycles = true;
            } else {
                runtime.last_offset = wpf_offset_to_rounded(coordinate);
            }
            break;
        }
        case guideline_set_state::phase::animation: {
            const bool recent = bumped_recently();
            const bool bumped = runtime.last_given_coordinate != coordinate;
            if (bumped) {
                runtime.bump_time = current_time & time_mask;
                runtime.last_given_coordinate = coordinate;
            }
            if (!bumped && !recent) {
                runtime.current_phase = guideline_set_state::phase::landing;
            }
            runtime.last_offset = 0.0F;
            needs_more_cycles = true;
            break;
        }
        case guideline_set_state::phase::landing: {
            const bool bumped = runtime.last_given_coordinate != coordinate;
            if (bumped) {
                runtime.bump_time = current_time & time_mask;
                runtime.last_given_coordinate = coordinate;
                runtime.current_phase =
                    guideline_set_state::phase::animation;
                runtime.last_offset = 0.0F;
                needs_more_cycles = true;
                break;
            }
            const float final_offset = wpf_offset_to_rounded(coordinate);
            const float distance = final_offset - runtime.last_offset;
            if (std::abs(distance) > allowed_step) {
                runtime.last_offset += std::copysign(allowed_step, distance);
                needs_more_cycles = true;
            } else {
                runtime.last_offset = final_offset;
                runtime.current_phase = guideline_set_state::phase::quiet;
            }
            break;
        }
        }
    }

    static status apply_dynamic_guidelines(
        const guideline_set_state& source,
        render_scope_state& state,
        native::semantic_scene_builder& builder,
        bool composite_only,
        scene_compile_context* context) {
        if (context == nullptr) {
            return status::unsupported_command;
        }
        const std::size_t count_x = source.guidelines_x.size() / 2U;
        const std::size_t count_y = source.guidelines_y.size() / 2U;
        if (state.transform.m12 != 0.0 || state.transform.m21 != 0.0) {
            if (!context->is_visual_brush()) {
                try {
                    source.runtime_x.resize(count_x);
                    source.runtime_y.resize(count_y);
                } catch (const std::bad_alloc&) {
                    return status::capacity_exceeded;
                }
                for (auto& runtime : source.runtime_x) {
                    runtime.current_phase = guideline_set_state::phase::flight;
                    runtime.bump_time = context->current_time_milliseconds;
                }
                for (auto& runtime : source.runtime_y) {
                    runtime.current_phase = guideline_set_state::phase::flight;
                    runtime.bump_time = context->current_time_milliseconds;
                }
            }
            state.guideline_resource_index = PROGPU_NATIVE_SCENE_NO_INDEX;
            state.per_point_guidelines = false;
            return status::success;
        }
        try {
            source.runtime_x.resize(count_x);
            source.runtime_y.resize(count_y);
        } catch (const std::bad_alloc&) {
            return status::capacity_exceeded;
        }
        std::vector<double> coordinates_x;
        std::vector<double> coordinates_y;
        std::vector<double> offsets_x;
        std::vector<double> offsets_y;
        try {
            coordinates_x.resize(count_x);
            coordinates_y.resize(count_y);
            offsets_x.resize(count_x);
            offsets_y.resize(count_y);
        } catch (const std::bad_alloc&) {
            return status::capacity_exceeded;
        }
        const bool suppress_animation = context->is_visual_brush();
        const auto resolve_axis = [&](std::span<const double> values,
                                      std::vector<guideline_set_state::runtime_state>& runtime,
                                      double scale,
                                      double translation,
                                      double dpi_scale,
                                      std::vector<double>& coordinates,
                                      std::vector<double>& offsets) {
            const float dpi = static_cast<float>(dpi_scale);
            const float physical_scale = static_cast<float>(scale) * dpi;
            const float physical_translation =
                static_cast<float>(translation) * dpi;
            for (std::size_t index = 0U; index < runtime.size(); ++index) {
                const float leading = static_cast<float>(values[index * 2U]);
                const float shift = static_cast<float>(values[index * 2U + 1U]);
                const float given =
                    leading * physical_scale + physical_translation;
                float leading_offset = wpf_offset_to_rounded(given);
                if (!suppress_animation) {
                    advance_dynamic_guideline(
                        runtime[index],
                        given,
                        context->current_time_milliseconds,
                        context->needs_more_cycles);
                    leading_offset = runtime[index].last_offset;
                }
                const float physical_shift = shift * physical_scale;
                coordinates[index] = static_cast<double>(
                    (given + physical_shift) / dpi);
                offsets[index] = static_cast<double>(
                    leading_offset + wpf_offset_to_rounded(physical_shift));
            }
            if (scale < 0.0) {
                std::ranges::reverse(coordinates);
                std::ranges::reverse(offsets);
            }
        };
        resolve_axis(
            source.guidelines_x,
            source.runtime_x,
            state.transform.m11,
            state.transform.m31,
            context->request.dpi_scale_x,
            coordinates_x,
            offsets_x);
        resolve_axis(
            source.guidelines_y,
            source.runtime_y,
            state.transform.m22,
            state.transform.m32,
            context->request.dpi_scale_y,
            coordinates_y,
            offsets_y);
        const bool multiple = count_x > 1U || count_y > 1U;
        std::uint32_t resource_index = PROGPU_NATIVE_SCENE_NO_INDEX;
        if (!builder.add_guideline_set_with_offsets(
                coordinates_x,
                coordinates_y,
                offsets_x,
                offsets_y,
                resource_index,
                multiple && composite_only,
                multiple && !composite_only)) {
            return status::invalid_graph;
        }
        state.guideline_resource_index = resource_index;
        state.per_point_guidelines = multiple && !composite_only;
        return status::success;
    }

    status apply_visual_rectangle_clip(
        std::uint32_t geometry_handle,
        render_scope_state& state) const {
        const auto geometry = fixed_geometries.find(geometry_handle);
        if (geometry == fixed_geometries.end()) {
            return require_geometry(geometry_handle)
                ? status::unsupported_command
                : status::invalid_handle;
        }
        fixed_geometry_state resolved_geometry{};
        const status geometry_status = resolve_fixed_geometry(
            geometry_handle, resolved_geometry);
        if (geometry_status != status::success) {
            return geometry_status;
        }
        if (resolved_geometry.kind != fixed_geometry_kind::rectangle ||
            resolved_geometry.radius_x != 0.0 ||
            resolved_geometry.radius_y != 0.0) {
            return status::unsupported_command;
        }
        affine_2d_double local_transform{};
        if (resolved_geometry.transform_handle != 0U) {
            const status transform_status = resolve_transform(
                resolved_geometry.transform_handle,
                local_transform);
            if (transform_status != status::success) {
                return transform_status;
            }
        }
        const affine_2d_double effective_transform = compose_affine(
            local_transform, state.transform);
        if (!affine_preserves_axis_alignment(effective_transform)) {
            return status::unsupported_command;
        }
        progpu_native_image_rect clip{};
        if (!try_transform_bounds(
                resolved_geometry.first,
                resolved_geometry.second,
                resolved_geometry.third,
                resolved_geometry.fourth,
                effective_transform,
                clip)) {
            return status::invalid_graph;
        }
        intersect_scope_clip(state, clip);
        return status::success;
    }

    static status attach_visual_output_clip(
        progpu_native_scene_layer& layer,
        const render_scope_state& state,
        native::semantic_scene_builder& builder) {
        layer.mask_resource_index = state.mask_resource_index;
        if (!state.has_clip) return status::success;
        auto composite_state = native::semantic_scene_builder::identity_state();
        composite_state.flags = PROGPU_NATIVE_SCENE_STATE_CLIP_RECT;
        composite_state.clip_rect = state.clip_rect;
        std::uint32_t composite_state_index = PROGPU_NATIVE_SCENE_NO_INDEX;
        if (!builder.add_state(composite_state, composite_state_index)) {
            return status::invalid_graph;
        }
        layer.flags |= PROGPU_NATIVE_SCENE_LAYER_COMPOSITE_STATE;
        layer.reserved0 = composite_state_index;
        return status::success;
    }

    status add_visual_effect_layer(
        std::uint32_t visual_handle,
        std::uint32_t effect_handle,
        const render_scope_state& state,
        bool has_local_cache_input,
        native::semantic_scene_builder& builder,
        std::uint32_t& pushed_count) const {
        pushed_count = 0U;
        if (effect_handle == 0U) {
            return status::success;
        }
        const auto effect = effects.find(effect_handle);
        const auto resource = resources.find(effect_handle);
        const auto visual = visuals.find(visual_handle);
        if (effect == effects.end() || resource == resources.end() ||
            visual == visuals.end()) {
            return status::invalid_handle;
        }
        effect_state resolved_effect{};
        const status resolved_effect_status = resolve_effect(
            effect_handle, resolved_effect);
        if (resolved_effect_status != status::success) {
            return resolved_effect_status;
        }
        std::uint64_t effect_revision = 14695981039346656037ULL;
        std::unordered_set<std::uint32_t> active_effect_resources;
        const status effect_revision_status = append_cache_resource_revision(
            effect_handle, active_effect_resources, effect_revision);
        if (effect_revision_status != status::success) {
            return effect_revision_status;
        }
        effect_revision = finish_nonzero_hash(effect_revision);
        // WPF composes Clip > Effect > OpacityMask/Opacity. A local cache is
        // already the required isolated input, so its composite opacity can
        // stay on the inner cache layer and execute before this outer effect.
        // The clip is attached to the effect's final composite instead of its
        // source state so blur and shadow kernels can sample the untruncated
        // visual. Uncached uniform opacity and a typed spatial mask are
        // represented by one bounded inner isolation layer so they execute
        // once before the outer effect.
        const bool has_spatial_opacity_mask =
            !has_local_cache_input &&
            visual->second.alpha_mask_handle != 0U &&
            gradient_brushes.contains(
                visual->second.alpha_mask_handle);
        const bool isolate_source_composite =
            !has_local_cache_input &&
            (state.opacity != 1.0 || has_spatial_opacity_mask);
        const auto attach_final_clip = [&](
                progpu_native_scene_layer& layer) -> status {
            return attach_visual_output_clip(layer, state, builder);
        };
        const auto push_source_composite_layer = [&]() -> status {
            if (!isolate_source_composite) {
                return status::success;
            }
            std::uint32_t opacity_mask_resource_index =
                PROGPU_NATIVE_SCENE_NO_INDEX;
            if (has_spatial_opacity_mask) {
                const status opacity_mask_status = add_visual_opacity_mask(
                    visual->second.alpha_mask_handle,
                    visual->second,
                    state.transform,
                    builder,
                    opacity_mask_resource_index);
                if (opacity_mask_status != status::success) {
                    return opacity_mask_status;
                }
            }
            progpu_native_scene_layer opacity_layer{};
            opacity_layer.struct_size = sizeof(opacity_layer);
            opacity_layer.flags =
                PROGPU_NATIVE_SCENE_LAYER_FORCE_ISOLATION;
            opacity_layer.opacity = static_cast<float>(state.opacity);
            opacity_layer.blend_mode = PROGPU_NATIVE_BLEND_SRC_OVER;
            opacity_layer.mask_resource_index =
                opacity_mask_resource_index;
            opacity_layer.effect_resource_index =
                PROGPU_NATIVE_SCENE_NO_INDEX;
            if (visual->second.has_cache_bounds) {
                if (!try_transform_bounds(
                        visual->second.cache_bounds_x,
                        visual->second.cache_bounds_y,
                        visual->second.cache_bounds_width,
                        visual->second.cache_bounds_height,
                        state.transform,
                        opacity_layer.bounds)) {
                    return status::invalid_graph;
                }
                opacity_layer.flags |= PROGPU_NATIVE_SCENE_LAYER_BOUNDS;
            }
            if (!builder.push_layer(opacity_layer)) {
                return status::invalid_graph;
            }
            ++pushed_count;
            return status::success;
        };
        const double scale_x = std::hypot(
            state.transform.m11, state.transform.m12);
        const double scale_y = std::hypot(
            state.transform.m21, state.transform.m22);
        if (!finite_double_as_float(scale_x) ||
            !finite_double_as_float(scale_y) ||
            scale_x <= 0.0 || scale_y <= 0.0) {
            return status::invalid_graph;
        }
        const double row_dot =
            state.transform.m11 * state.transform.m21 +
            state.transform.m12 * state.transform.m22;
        if (!std::isfinite(row_dot) ||
            std::abs(row_dot) > 1.0e-9 * scale_x * scale_y) {
            return status::unsupported_command;
        }
        const double minimum_scale = std::min(scale_x, scale_y);
        const auto wpf_scaled_radius = [&](double radius,
                                           float& sigma,
                                           double& scaled_radius) noexcept {
            if (radius > static_cast<double>(
                    std::numeric_limits<std::uint32_t>::max())) {
                return false;
            }
            const double local_radius = std::floor(radius);
            const double scaled = std::min(
                100.0, std::floor(local_radius * minimum_scale));
            scaled_radius = scaled;
            sigma = static_cast<float>(scaled / 3.0);
            return std::isfinite(sigma);
        };
        progpu_native_group_effect descriptor{};
        descriptor.struct_size = sizeof(descriptor);
        descriptor.revision = static_cast<std::uint32_t>(
            effect_revision ^ (effect_revision >> 32U));
        if (descriptor.revision == 0U) {
            descriptor.revision = 1U;
        }
        double effect_radius = 0.0;
        if (!wpf_scaled_radius(
                resolved_effect.radius,
                descriptor.sigma_x,
                effect_radius)) {
            return status::unsupported_command;
        }
        descriptor.sigma_y = descriptor.sigma_x;
        if (resolved_effect.type == effect_state::kind::blur) {
            if (descriptor.sigma_x <= 0.01F) {
                const bool has_final_clip = state.has_clip ||
                    state.mask_resource_index != PROGPU_NATIVE_SCENE_NO_INDEX;
                if (!has_final_clip && !isolate_source_composite) {
                    return status::success;
                }
                if (has_final_clip) {
                    progpu_native_scene_layer clip_layer{};
                    clip_layer.struct_size = sizeof(clip_layer);
                    clip_layer.flags =
                        PROGPU_NATIVE_SCENE_LAYER_FORCE_ISOLATION;
                    clip_layer.opacity = 1.0F;
                    clip_layer.blend_mode = PROGPU_NATIVE_BLEND_SRC_OVER;
                    clip_layer.mask_resource_index =
                        PROGPU_NATIVE_SCENE_NO_INDEX;
                    clip_layer.effect_resource_index =
                        PROGPU_NATIVE_SCENE_NO_INDEX;
                    const status clip_status = attach_final_clip(clip_layer);
                    if (clip_status != status::success) {
                        return clip_status;
                    }
                    if (visual->second.has_cache_bounds) {
                        if (!try_transform_bounds(
                                visual->second.cache_bounds_x,
                                visual->second.cache_bounds_y,
                                visual->second.cache_bounds_width,
                                visual->second.cache_bounds_height,
                                state.transform,
                                clip_layer.bounds)) {
                            return status::invalid_graph;
                        }
                        clip_layer.flags |=
                            PROGPU_NATIVE_SCENE_LAYER_BOUNDS;
                    }
                    if (!builder.push_layer(clip_layer)) {
                        return status::invalid_graph;
                    }
                    ++pushed_count;
                }
                return push_source_composite_layer();
            }
            if (resolved_effect.box_blur) {
                descriptor.kind = PROGPU_NATIVE_GROUP_EFFECT_BOX_BLUR;
                descriptor.sigma_x = static_cast<float>(effect_radius);
                descriptor.sigma_y = descriptor.sigma_x;
            } else {
                descriptor.kind = PROGPU_NATIVE_GROUP_EFFECT_GAUSSIAN_BLUR;
            }
        } else {
            descriptor.kind = PROGPU_NATIVE_GROUP_EFFECT_DROP_SHADOW;
            const double radians = resolved_effect.direction *
                std::numbers::pi_v<double> / 180.0;
            const double distance =
                resolved_effect.shadow_depth * minimum_scale;
            const double offset_x = distance * std::cos(radians);
            const double offset_y = -distance * std::sin(radians);
            const double rest_m11 = state.transform.m11 / scale_x;
            const double rest_m12 = state.transform.m12 / scale_x;
            const double rest_m21 = state.transform.m21 / scale_y;
            const double rest_m22 = state.transform.m22 / scale_y;
            descriptor.offset_x = static_cast<float>(
                offset_x * rest_m11 + offset_y * rest_m21);
            descriptor.offset_y = static_cast<float>(
                offset_x * rest_m12 + offset_y * rest_m22);
            descriptor.color_r = resolved_effect.color.r;
            descriptor.color_g = resolved_effect.color.g;
            descriptor.color_b = resolved_effect.color.b;
            descriptor.color_a = resolved_effect.color.a *
                static_cast<float>(resolved_effect.opacity);
            if (!std::isfinite(descriptor.offset_x) ||
                !std::isfinite(descriptor.offset_y) ||
                descriptor.color_r < 0.0F || descriptor.color_r > 1.0F ||
                descriptor.color_g < 0.0F || descriptor.color_g > 1.0F ||
                descriptor.color_b < 0.0F || descriptor.color_b > 1.0F ||
                descriptor.color_a < 0.0F || descriptor.color_a > 1.0F) {
                return status::unsupported_command;
            }
        }
        std::uint32_t effect_index = PROGPU_NATIVE_SCENE_NO_INDEX;
        if (!builder.add_effect_chain(
                std::span<const progpu_native_group_effect>(
                    &descriptor, 1U),
                descriptor.revision,
                effect_index)) {
            return status::invalid_graph;
        }
        progpu_native_scene_layer layer{};
        layer.struct_size = sizeof(layer);
        const status clip_status = attach_final_clip(layer);
        if (clip_status != status::success) {
            return clip_status;
        }
        layer.opacity = 1.0F;
        layer.blend_mode = PROGPU_NATIVE_BLEND_SRC_OVER;
        layer.effect_resource_index = effect_index;
        layer.content_revision = effect_revision;
        layer.composite_revision = effect_revision;
        if (visual->second.has_cache_bounds) {
            progpu_native_image_rect source_bounds{};
            if (!try_transform_bounds(
                    visual->second.cache_bounds_x,
                    visual->second.cache_bounds_y,
                    visual->second.cache_bounds_width,
                    visual->second.cache_bounds_height,
                    state.transform,
                    source_bounds)) {
                return status::invalid_graph;
            }
            const double source_left = source_bounds.x;
            const double source_top = source_bounds.y;
            const double source_right = source_left + source_bounds.width;
            const double source_bottom = source_top + source_bounds.height;
            double effect_left = source_left - effect_radius;
            double effect_top = source_top - effect_radius;
            double effect_right = source_right + effect_radius;
            double effect_bottom = source_bottom + effect_radius;
            if (resolved_effect.type == effect_state::kind::drop_shadow) {
                effect_left = std::min(
                    source_left,
                    source_left + descriptor.offset_x - effect_radius);
                effect_top = std::min(
                    source_top,
                    source_top + descriptor.offset_y - effect_radius);
                effect_right = std::max(
                    source_right,
                    source_right + descriptor.offset_x + effect_radius);
                effect_bottom = std::max(
                    source_bottom,
                    source_bottom + descriptor.offset_y + effect_radius);
            }
            const double effect_width = effect_right - effect_left;
            const double effect_height = effect_bottom - effect_top;
            if (!finite_double_as_float(effect_left) ||
                !finite_double_as_float(effect_top) ||
                !finite_double_as_float(effect_width) ||
                !finite_double_as_float(effect_height) ||
                effect_width <= 0.0 || effect_height <= 0.0) {
                return status::invalid_graph;
            }
            layer.flags |= PROGPU_NATIVE_SCENE_LAYER_BOUNDS;
            layer.bounds = {
                static_cast<float>(effect_left),
                static_cast<float>(effect_top),
                static_cast<float>(effect_width),
                static_cast<float>(effect_height)};
        }
        if (!builder.push_layer(layer)) {
            return status::invalid_graph;
        }
        ++pushed_count;
        return push_source_composite_layer();
    }

    status append_cache_resource_revision(
        std::uint32_t handle,
        std::unordered_set<std::uint32_t>& active_resources,
        std::uint64_t& hash) const {
        append_fnv1a64(hash, handle);
        if (handle == 0U) {
            return status::success;
        }
        if (active_resources.size() >= maximum_visual_depth || !active_resources.insert(handle).second) {
            return status::invalid_graph;
        }
        const auto resource = resources.find(handle);
        if (resource == resources.end()) {
            active_resources.erase(handle);
            return status::invalid_handle;
        }
        append_fnv1a64(hash, resource->second.type);
        append_fnv1a64(hash, resource->second.generation);
        const auto append_dependency = [&](std::uint32_t dependency) {
            return append_cache_resource_revision(
                dependency, active_resources, hash);
        };
        status result = status::success;
        const auto append_if_success = [&](std::uint32_t dependency) {
            if (result == status::success) {
                result = append_dependency(dependency);
            }
        };
        if (resource->second.type == type_visual || resource->second.type == type_viewport3d_visual) {
            std::unordered_set<std::uint32_t> active_visuals;
            result = compute_visual_cache_content_revision(handle, true, active_visuals, active_resources, hash);
        } else if (resource->second.type == type_visual3d) {
            const auto visual = visuals3d.find(handle);
            if (visual == visuals3d.end()) {
                result = status::invalid_handle;
            } else {
                append_if_success(visual->second.content_handle);
                append_if_success(visual->second.transform_handle);
                for (const std::uint32_t child : visual->second.children) {
                    append_if_success(child);
                }
            }
        } else if (resource->second.type == type_model3d_group) {
            const auto group = model3d_groups.find(handle);
            if (group == model3d_groups.end()) {
                result = status::invalid_handle;
            } else {
                append_if_success(group->second.transform_handle);
                for (const std::uint32_t child : group->second.children) {
                    append_if_success(child);
                }
            }
        } else if (is_light3d_type(resource->second.type)) {
            const auto light = lights3d.find(handle);
            if (light == lights3d.end()) {
                result = status::invalid_handle;
            } else {
                append_if_success(light->second.transform_handle);
                for (const std::uint32_t animation :
                     light->second.animations) {
                    append_if_success(animation);
                }
            }
        } else if (resource->second.type == type_geometry_model3d) {
            const auto model = geometry_models3d.find(handle);
            if (model == geometry_models3d.end()) {
                result = status::invalid_handle;
            } else {
                append_if_success(model->second.transform_handle);
                append_if_success(model->second.geometry_handle);
                append_if_success(model->second.material_handle);
                append_if_success(model->second.back_material_handle);
            }
        } else if (resource->second.type == type_material_group) {
            const auto group = material_groups3d.find(handle);
            if (group == material_groups3d.end()) {
                result = status::invalid_handle;
            } else {
                for (const std::uint32_t child : group->second.children) {
                    append_if_success(child);
                }
            }
        } else if (resource->second.type == type_diffuse_material ||
            resource->second.type == type_specular_material ||
            resource->second.type == type_emissive_material) {
            const auto material = materials3d.find(handle);
            if (material == materials3d.end()) {
                result = status::invalid_handle;
            } else {
                append_if_success(material->second.brush_handle);
            }
        } else if (is_rotation3d_type(resource->second.type)) {
            const auto rotation = rotations3d.find(handle);
            if (rotation == rotations3d.end()) {
                result = status::invalid_handle;
            } else {
                append_if_success(
                    rotation->second.vector_animation_handle);
                append_if_success(
                    rotation->second.scalar_animation_handle);
            }
        } else if (is_transform3d_type(resource->second.type)) {
            const auto transform = transforms3d.find(handle);
            if (transform == transforms3d.end()) {
                result = status::invalid_handle;
            } else {
                append_if_success(transform->second.rotation_handle);
                for (const std::uint32_t child : transform->second.children) {
                    append_if_success(child);
                }
                for (const std::uint32_t animation :
                     transform->second.animations) {
                    append_if_success(animation);
                }
            }
        } else if (is_camera3d_type(resource->second.type)) {
            const auto camera = cameras3d.find(handle);
            if (camera == cameras3d.end()) {
                result = status::invalid_handle;
            } else {
                append_if_success(camera->second.transform_handle);
                for (const std::uint32_t animation :
                     camera->second.animations) {
                    append_if_success(animation);
                }
            }
        } else if (is_transform_type(resource->second.type)) {
            const auto transform = transforms.find(handle);
            if (transform == transforms.end()) {
                result = status::invalid_handle;
            } else {
                for (const std::uint32_t child : transform->second.children) {
                    append_if_success(child);
                }
                for (const std::uint32_t animation :
                     transform->second.animations) {
                    append_if_success(animation);
                }
            }
        } else if (resource->second.type == type_solid_color_brush) {
            const auto brush = solid_brushes.find(handle);
            if (brush == solid_brushes.end()) {
                result = status::invalid_handle;
            } else {
                append_if_success(brush->second.transform_handle);
                append_if_success(
                    brush->second.relative_transform_handle);
                append_if_success(brush->second.opacity_animation_handle);
                append_if_success(brush->second.color_animation_handle);
            }
        } else if (resource->second.type == type_image_brush ||
            resource->second.type == type_drawing_brush ||
            resource->second.type == type_visual_brush) {
            const auto brush = tile_brushes.find(handle);
            if (brush == tile_brushes.end()) {
                result = status::invalid_handle;
            } else {
                append_if_success(brush->second.source_handle);
                append_if_success(brush->second.transform_handle);
                append_if_success(brush->second.relative_transform_handle);
                append_if_success(brush->second.opacity_animation);
                append_if_success(brush->second.viewport_animation);
                append_if_success(brush->second.viewbox_animation);
            }
        } else if (is_effect_type(resource->second.type)) {
            const auto effect = effects.find(handle);
            if (effect == effects.end()) {
                result = status::invalid_handle;
            } else {
                for (const std::uint32_t animation :
                     effect->second.animations) {
                    append_if_success(animation);
                }
            }
        } else if (resource->second.type == type_linear_gradient_brush ||
            resource->second.type == type_radial_gradient_brush) {
            const auto brush = gradient_brushes.find(handle);
            if (brush == gradient_brushes.end()) {
                result = status::invalid_handle;
            } else {
                append_if_success(brush->second.opacity_animation);
                append_if_success(brush->second.transform_handle);
                append_if_success(brush->second.relative_transform_handle);
                append_if_success(brush->second.first_point_animation);
                append_if_success(brush->second.second_point_animation);
                append_if_success(brush->second.radius_x_animation);
                append_if_success(brush->second.radius_y_animation);
            }
        } else if (resource->second.type == type_pen) {
            const auto pen = pens.find(handle);
            if (pen == pens.end()) {
                result = status::invalid_handle;
            } else {
                append_if_success(pen->second.brush_handle);
                append_if_success(pen->second.dash_style_handle);
                append_if_success(pen->second.thickness_animation_handle);
            }
        } else if (resource->second.type == type_dash_style) {
            const auto dash = dash_styles.find(handle);
            if (dash == dash_styles.end()) {
                result = status::invalid_handle;
            } else {
                append_if_success(dash->second.offset_animation_handle);
            }
        } else if (resource->second.type == type_line_geometry ||
            resource->second.type == type_rectangle_geometry ||
            resource->second.type == type_ellipse_geometry) {
            const auto geometry = fixed_geometries.find(handle);
            if (geometry == fixed_geometries.end()) {
                result = status::invalid_handle;
            } else {
                append_if_success(geometry->second.transform_handle);
                for (const std::uint32_t animation :
                     geometry->second.animations) {
                    append_if_success(animation);
                }
            }
        } else if (resource->second.type == type_path_geometry) {
            const auto geometry = path_geometries.find(handle);
            if (geometry == path_geometries.end()) {
                result = status::invalid_handle;
            } else {
                append_if_success(geometry->second.transform_handle);
            }
        } else if (resource->second.type == type_geometry_group) {
            const auto geometry = geometry_groups.find(handle);
            if (geometry == geometry_groups.end()) {
                result = status::invalid_handle;
            } else {
                append_if_success(geometry->second.transform_handle);
                for (const std::uint32_t child : geometry->second.children) {
                    append_if_success(child);
                }
            }
        } else if (resource->second.type == type_combined_geometry) {
            const auto geometry = combined_geometries.find(handle);
            if (geometry == combined_geometries.end()) {
                result = status::invalid_handle;
            } else {
                append_if_success(geometry->second.transform_handle);
                append_if_success(geometry->second.geometry1_handle);
                append_if_success(geometry->second.geometry2_handle);
            }
        } else if (resource->second.type == type_geometry_drawing) {
            const auto drawing = geometry_drawings.find(handle);
            if (drawing == geometry_drawings.end()) {
                result = status::invalid_handle;
            } else {
                append_if_success(drawing->second.brush_handle);
                append_if_success(drawing->second.pen_handle);
                append_if_success(drawing->second.geometry_handle);
            }
        } else if (resource->second.type == type_glyph_run_drawing) {
            const auto drawing = glyph_run_drawings.find(handle);
            if (drawing == glyph_run_drawings.end()) {
                result = status::invalid_handle;
            } else {
                append_if_success(drawing->second.glyph_run_handle);
                append_if_success(drawing->second.foreground_brush_handle);
            }
        } else if (resource->second.type == type_image_drawing) {
            const auto drawing = image_drawings.find(handle);
            if (drawing == image_drawings.end()) {
                result = status::invalid_handle;
            } else {
                append_if_success(drawing->second.image_source_handle);
                append_if_success(drawing->second.rect_animation_handle);
            }
        } else if (resource->second.type == type_video_drawing) {
            const auto drawing = video_drawings.find(handle);
            if (drawing == video_drawings.end()) {
                result = status::invalid_handle;
            } else {
                append_if_success(drawing->second.player_handle);
                append_if_success(drawing->second.rect_animation_handle);
            }
        } else if (resource->second.type == type_drawing_image) {
            const auto image = drawing_images.find(handle);
            if (image == drawing_images.end()) {
                result = status::invalid_handle;
            } else {
                append_if_success(image->second.drawing_handle);
            }
        } else if (resource->second.type == type_drawing_group) {
            const auto group = drawing_groups.find(handle);
            if (group == drawing_groups.end()) {
                result = status::invalid_handle;
            } else {
                append_if_success(group->second.clip_geometry_handle);
                append_if_success(group->second.opacity_animation_handle);
                append_if_success(group->second.opacity_mask_handle);
                append_if_success(group->second.transform_handle);
                append_if_success(group->second.guideline_set_handle);
                for (const std::uint32_t child : group->second.children) {
                    append_if_success(child);
                }
            }
        } else if (resource->second.type == type_bitmap_cache) {
            const auto cache = bitmap_caches.find(handle);
            if (cache == bitmap_caches.end()) {
                result = status::invalid_handle;
            } else {
                append_if_success(
                    cache->second.render_at_scale_animation_handle);
            }
        } else if (resource->second.type == type_render_data) {
            batch_reader reader(resource->second.render_data);
            command_view view{};
            for (;;) {
                const status read_status = reader.next(view);
                if (read_status == status::end_of_batch) {
                    break;
                }
                if (read_status != status::success) {
                    result = read_status;
                    break;
                }
                const status framing_status =
                    validate_render_data_command_framing(view);
                if (framing_status != status::success) {
                    result = framing_status;
                    break;
                }
                const auto append_packet_handle = [&](std::size_t offset) {
                    std::uint32_t dependency = 0U;
                    if (result == status::success &&
                        !read_at(view.packet, offset, dependency)) {
                        result = status::malformed_batch;
                    } else if (result == status::success) {
                        append_if_success(dependency);
                    }
                };
                if (view.kind == command::push_opacity ||
                    view.kind == command::push_guideline_y1 ||
                    view.kind == command::push_guideline_y2 ||
                    view.kind == command::pop) {
                    continue;
                } else if (view.kind == command::push_opacity_animate) {
                    append_packet_handle(
                        command_layouts::push_opacity_animate::
                            h_opacity_animations_offset);
                } else if (view.kind == command::push_opacity_mask) {
                    append_packet_handle(
                        command_layouts::push_opacity_mask::
                            h_opacity_mask_offset);
                } else if (view.kind == command::push_clip) {
                    append_packet_handle(
                        command_layouts::push_clip::h_clip_geometry_offset);
                } else if (view.kind == command::push_transform) {
                    append_packet_handle(
                        command_layouts::push_transform::h_transform_offset);
                } else if (view.kind == command::push_guideline_set) {
                    append_packet_handle(
                        command_layouts::push_guideline_set::
                            h_guidelines_offset);
                } else if (view.kind == command::push_effect) {
                    using layout = command_layouts::push_effect;
                    std::uint32_t effect_handle = 0U;
                    std::uint32_t effect_input_handle = 0U;
                    if (!has_exact_size(view, layout::fixed_size) ||
                        !read_at(
                            view.packet,
                            layout::h_effect_offset,
                            effect_handle) ||
                        !read_at(
                            view.packet,
                            layout::h_effect_input_offset,
                            effect_input_handle)) {
                        result = status::malformed_batch;
                    } else {
                        // These are managed-only dependent-resource indices.
                        // WPF milcore ignores them because legacy BitmapEffect
                        // execution is disabled, so they are not native cache
                        // dependencies.
                        (void)effect_handle;
                        (void)effect_input_handle;
                    }
                } else if (view.kind == command::draw_drawing) {
                    append_packet_handle(
                        command_layouts::draw_drawing::h_drawing_offset);
                } else if (view.kind == command::draw_glyph_run) {
                    append_packet_handle(
                        command_layouts::draw_glyph_run::
                            h_foreground_brush_offset);
                    append_packet_handle(
                        command_layouts::draw_glyph_run::h_glyph_run_offset);
                } else if (view.kind == command::draw_image) {
                    append_packet_handle(
                        command_layouts::draw_image::h_image_source_offset);
                } else if (view.kind == command::draw_image_animate) {
                    append_packet_handle(
                        command_layouts::draw_image_animate::
                            h_image_source_offset);
                    append_packet_handle(
                        command_layouts::draw_image_animate::
                            h_rectangle_animations_offset);
                } else if (view.kind == command::draw_line) {
                    append_packet_handle(
                        command_layouts::draw_line::h_pen_offset);
                } else if (view.kind == command::draw_line_animate) {
                    append_packet_handle(
                        command_layouts::draw_line_animate::h_pen_offset);
                    append_packet_handle(
                        command_layouts::draw_line_animate::
                            h_point0_animations_offset);
                    append_packet_handle(
                        command_layouts::draw_line_animate::
                            h_point1_animations_offset);
                } else if (view.kind == command::draw_geometry) {
                    append_packet_handle(
                        command_layouts::draw_geometry::h_brush_offset);
                    append_packet_handle(
                        command_layouts::draw_geometry::h_pen_offset);
                    append_packet_handle(
                        command_layouts::draw_geometry::h_geometry_offset);
                } else if (view.kind == command::draw_rectangle) {
                    append_packet_handle(
                        command_layouts::draw_rectangle::h_brush_offset);
                    append_packet_handle(
                        command_layouts::draw_rectangle::h_pen_offset);
                } else if (
                    view.kind == command::draw_rectangle_animate) {
                    append_packet_handle(
                        command_layouts::draw_rectangle_animate::
                            h_brush_offset);
                    append_packet_handle(
                        command_layouts::draw_rectangle_animate::h_pen_offset);
                    append_packet_handle(
                        command_layouts::draw_rectangle_animate::
                            h_rectangle_animations_offset);
                } else if (view.kind == command::draw_ellipse) {
                    append_packet_handle(
                        command_layouts::draw_ellipse::h_brush_offset);
                    append_packet_handle(
                        command_layouts::draw_ellipse::h_pen_offset);
                } else if (view.kind == command::draw_ellipse_animate) {
                    append_packet_handle(
                        command_layouts::draw_ellipse_animate::h_brush_offset);
                    append_packet_handle(
                        command_layouts::draw_ellipse_animate::h_pen_offset);
                    append_packet_handle(
                        command_layouts::draw_ellipse_animate::
                            h_center_animations_offset);
                    append_packet_handle(
                        command_layouts::draw_ellipse_animate::
                            h_radius_x_animations_offset);
                    append_packet_handle(
                        command_layouts::draw_ellipse_animate::
                            h_radius_y_animations_offset);
                } else if (view.kind == command::draw_rounded_rectangle) {
                    append_packet_handle(
                        command_layouts::draw_rounded_rectangle::
                            h_brush_offset);
                    append_packet_handle(
                        command_layouts::draw_rounded_rectangle::h_pen_offset);
                } else if (
                    view.kind == command::draw_rounded_rectangle_animate) {
                    append_packet_handle(
                        command_layouts::draw_rounded_rectangle_animate::
                            h_brush_offset);
                    append_packet_handle(
                        command_layouts::draw_rounded_rectangle_animate::
                            h_pen_offset);
                    append_packet_handle(
                        command_layouts::draw_rounded_rectangle_animate::
                            h_rectangle_animations_offset);
                    append_packet_handle(
                        command_layouts::draw_rounded_rectangle_animate::
                            h_radius_x_animations_offset);
                    append_packet_handle(
                        command_layouts::draw_rounded_rectangle_animate::
                            h_radius_y_animations_offset);
                } else {
                    result = status::unsupported_command;
                }
                if (result != status::success) {
                    break;
                }
            }
        }
        active_resources.erase(handle);
        return result;
    }

    status compute_visual_cache_content_revision(
        std::uint32_t handle,
        bool include_outer_state,
        std::unordered_set<std::uint32_t>& active_visuals,
        std::unordered_set<std::uint32_t>& active_resources,
        std::uint64_t& hash) const {
        if (active_visuals.size() + active_resources.size() >= maximum_visual_depth ||
            !active_visuals.insert(handle).second) {
            return status::invalid_graph;
        }
        const auto visual = visuals.find(handle);
        const auto resource = resources.find(handle);
        if (visual == visuals.end() || resource == resources.end()) {
            active_visuals.erase(handle);
            return status::invalid_handle;
        }
        const auto append_resource = [&](std::uint32_t dependency) {
            return append_cache_resource_revision(
                dependency, active_resources, hash) == status::success;
        };
        append_fnv1a64(hash, handle);
        if (resource->second.type == type_viewport3d_visual) {
            append_fnv1a64(hash, resource->second.generation);
            const auto viewport = viewport3d_visuals.find(handle);
            if (viewport != viewport3d_visuals.end()) {
                append_fnv1a64(hash, viewport->second.x);
                append_fnv1a64(hash, viewport->second.y);
                append_fnv1a64(hash, viewport->second.width);
                append_fnv1a64(hash, viewport->second.height);
                append_fnv1a64(hash, viewport->second.has_viewport);
                append_fnv1a64(hash, viewport->second.has_camera_binding);
                append_fnv1a64(hash, viewport->second.has_child_binding);
                if (!append_resource(viewport->second.camera_handle)) {
                    active_visuals.erase(handle);
                    return status::invalid_handle;
                }
                if (!append_resource(viewport->second.child_handle)) {
                    active_visuals.erase(handle);
                    return status::invalid_handle;
                }
            }
        }
        if (!append_resource(visual->second.content_handle)) {
            active_visuals.erase(handle);
            return status::invalid_handle;
        }
        if (include_outer_state) {
            append_fnv1a64(hash, visual->second.render_options_flags);
            append_fnv1a64(hash, visual->second.edge_mode);
            append_fnv1a64(hash, visual->second.bitmap_scaling_mode);
            append_fnv1a64(hash, visual->second.clear_type_hint);
            append_fnv1a64(hash, visual->second.text_rendering_mode);
            append_fnv1a64(hash, visual->second.text_hinting_mode);
            for (const double coordinate : visual->second.guidelines_x) {
                append_fnv1a64(hash, coordinate);
            }
            for (const double coordinate : visual->second.guidelines_y) {
                append_fnv1a64(hash, coordinate);
            }
            append_fnv1a64(hash, visual->second.offset_x);
            append_fnv1a64(hash, visual->second.offset_y);
            append_fnv1a64(hash, visual->second.opacity);
            append_fnv1a64(hash, visual->second.has_scroll_clip);
            append_fnv1a64(hash, visual->second.scroll_clip_x);
            append_fnv1a64(hash, visual->second.scroll_clip_y);
            append_fnv1a64(hash, visual->second.scroll_clip_width);
            append_fnv1a64(hash, visual->second.scroll_clip_height);
            append_fnv1a64(hash, visual->second.has_cache_bounds);
            append_fnv1a64(hash, visual->second.cache_bounds_x);
            append_fnv1a64(hash, visual->second.cache_bounds_y);
            append_fnv1a64(hash, visual->second.cache_bounds_width);
            append_fnv1a64(hash, visual->second.cache_bounds_height);
            if (!append_resource(visual->second.transform_handle) ||
                !append_resource(visual->second.effect_handle) ||
                !append_resource(visual->second.cache_mode_handle) ||
                !append_resource(visual->second.clip_geometry_handle) ||
                !append_resource(visual->second.alpha_mask_handle)) {
                active_visuals.erase(handle);
                return status::invalid_handle;
            }
        }
        append_fnv1a64(
            hash,
            static_cast<std::uint64_t>(visual->second.children.size()));
        for (const std::uint32_t child : visual->second.children) {
            const status child_status = compute_visual_cache_content_revision(
                child, true, active_visuals, active_resources, hash);
            if (child_status != status::success) {
                active_visuals.erase(handle);
                return child_status;
            }
        }
        active_visuals.erase(handle);
        return status::success;
    }

    status add_gradient_opacity_mask(
        std::uint32_t brush_handle,
        double bounds_x,
        double bounds_y,
        double bounds_width,
        double bounds_height,
        const affine_2d_double& mask_transform,
        native::semantic_scene_builder& builder,
        std::uint32_t& mask_resource_index,
        std::span<const progpu_native_scene_clip_path> clip_paths = {},
        std::span<const progpu_native_path_segment> clip_segments = {},
        std::span<const progpu_native_scene_path_boolean_node>
            clip_boolean_nodes = {}) const {
        mask_resource_index = PROGPU_NATIVE_SCENE_NO_INDEX;
        if (brush_handle == 0U) {
            return status::success;
        }
        const brush_use_state use{
            bounds_x,
            bounds_y,
            bounds_width,
            bounds_height,
            mask_transform};
        progpu_native_scene_brush brush{};
        std::vector<progpu_native_scene_gradient_stop> stops;
        const status brush_status = resolve_gradient_scene_brush(
            brush_handle, use, brush, stops);
        if (brush_status != status::success) {
            return brush_status;
        }
        progpu_native_scene_layer_brush_mask mask{};
        mask.struct_size = sizeof(mask);
        mask.kind = PROGPU_NATIVE_SCENE_LAYER_MASK_BRUSH;
        mask.gradient_stop_count = static_cast<std::uint32_t>(stops.size());
        mask.bounds = {
            static_cast<float>(bounds_x),
            static_cast<float>(bounds_y),
            static_cast<float>(bounds_width),
            static_cast<float>(bounds_height)};
        if (!try_to_native_affine(mask_transform, mask.transform)) {
            return status::invalid_graph;
        }
        mask.opacity = 1.0F;
        mask.brush = brush;
        if (!clip_paths.empty()) {
            return builder.add_composite_mask(
                std::span(&mask, 1U), {}, {}, {}, {},
                clip_paths, clip_segments, clip_boolean_nodes,
                stops, 1.0F, mask_resource_index)
                ? status::success : status::invalid_graph;
        }
        return builder.add_brush_mask(mask, stops, mask_resource_index)
            ? status::success
            : status::invalid_graph;
    }

    status add_visual_opacity_mask(
        std::uint32_t brush_handle,
        const visual_state& visual,
        const affine_2d_double& mask_transform,
        native::semantic_scene_builder& builder,
        std::uint32_t& mask_resource_index,
        std::span<const progpu_native_scene_clip_path> clip_paths = {},
        std::span<const progpu_native_path_segment> clip_segments = {},
        std::span<const progpu_native_scene_path_boolean_node>
            clip_boolean_nodes = {}) const {
        return add_gradient_opacity_mask(
            brush_handle,
            visual.cache_bounds_x,
            visual.cache_bounds_y,
            visual.cache_bounds_width,
            visual.cache_bounds_height,
            mask_transform,
            builder,
            mask_resource_index, clip_paths, clip_segments, clip_boolean_nodes);
    }

    status add_visual_cache_layer(
        std::uint32_t cache_handle,
        std::uint32_t visual_handle,
        std::uint64_t scene_id,
        bool visual_brush,
        const render_scope_state& state,
        std::span<const progpu_native_scene_clip_path> clip_paths,
        std::span<const progpu_native_path_segment> clip_segments,
        std::span<const progpu_native_scene_path_boolean_node> clip_boolean_nodes,
        native::semantic_scene_builder& builder,
        bool& pushed,
        bool& skip_content,
        bool& pushed_content_state,
        render_scope_state& content_state) const {
        pushed = false;
        skip_content = false;
        pushed_content_state = false;
        content_state = state;
        if (cache_handle == 0U) {
            return status::success;
        }
        const auto cache = bitmap_caches.find(cache_handle);
        const auto cache_resource = resources.find(cache_handle);
        if (cache == bitmap_caches.end() ||
            cache_resource == resources.end() ||
            cache_resource->second.type != type_bitmap_cache) {
            return status::invalid_handle;
        }
        double render_at_scale = 1.0;
        const status scale_status = resolve_animated_double(
            cache->second.render_at_scale,
            cache->second.render_at_scale_animation_handle,
            render_at_scale);
        if (scale_status != status::success ||
            !std::isfinite(render_at_scale)) {
            return scale_status == status::success
                ? status::invalid_graph
                : scale_status;
        }
        render_at_scale = std::max(0.0, render_at_scale);
        if (render_at_scale == 0.0) {
            skip_content = true;
            return status::success;
        }
        const auto& cache_visual = visuals.at(visual_handle);
        if (!cache_visual.has_cache_bounds) {
            return status::unsupported_command;
        }
        const bool has_spatial_opacity_mask =
            cache_visual.alpha_mask_handle != 0U &&
            gradient_brushes.contains(cache_visual.alpha_mask_handle);
        // Local cached pixels are independent of the cache-root Visual's
        // properties. WPF applies those properties while drawing the retained
        // bitmap. A single typed linear/radial opacity brush can use the
        // reusable GPU brush-mask resource at composite time. When an effect
        // is present this cache layer is nested inside the effect layer, so
        // the spatial mask is applied to the isolated retained page before
        // effect execution, matching WPF. Static cache-root guidelines share
        // the same composite State; the backend deforms the retained quad and
        // brush-mask coverage through that frame without rerasterizing cached
        // content. Exact inherited vector clips are multiplied with the
        // gradient in the existing GPU composite-mask resource. Their world
        // coordinates do not inherit cache-root guideline deformation.
        // Fant/HighQuality sampling is retained as composite-only state.
        if (state.image_sampling != PROGPU_NATIVE_IMAGE_SAMPLING_LINEAR &&
                state.image_sampling != PROGPU_NATIVE_IMAGE_SAMPLING_NEAREST &&
                state.image_sampling != PROGPU_NATIVE_IMAGE_SAMPLING_FANT) {
            return status::unsupported_command;
        }
        if (state.clip_path_count > clip_paths.size() ||
            state.clip_segment_count > clip_segments.size() ||
            state.clip_boolean_node_count > clip_boolean_nodes.size()) {
            return status::invalid_graph;
        }
        if (state.mask_resource_index != PROGPU_NATIVE_SCENE_NO_INDEX &&
            state.clip_path_count == 0U) {
            return status::unsupported_command;
        }
        const double raster_width =
            cache_visual.cache_bounds_width * render_at_scale;
        const double raster_height =
            cache_visual.cache_bounds_height * render_at_scale;
        if (!finite_double_as_float(raster_width) ||
            !finite_double_as_float(raster_height) ||
            raster_width <= 0.0 || raster_height <= 0.0) {
            return status::invalid_graph;
        }
        std::uint64_t content_revision = 14695981039346656037ULL;
        if (visual_brush) append_fnv1a64(content_revision, std::uint32_t{0x56425253U});
        append_fnv1a64(content_revision, cache_handle);
        append_fnv1a64(content_revision, render_at_scale);
        append_fnv1a64(content_revision, cache->second.enable_clear_type);
        append_fnv1a64(content_revision, cache_visual.cache_bounds_x);
        append_fnv1a64(content_revision, cache_visual.cache_bounds_y);
        append_fnv1a64(content_revision, cache_visual.cache_bounds_width);
        append_fnv1a64(content_revision, cache_visual.cache_bounds_height);
        if (cache->second.render_at_scale_animation_handle != 0U) {
            const auto animation = resources.find(
                cache->second.render_at_scale_animation_handle);
            if (animation == resources.end()) {
                return status::invalid_handle;
            }
            append_fnv1a64(content_revision, animation->second.generation);
        }
        std::unordered_set<std::uint32_t> active_visuals;
        std::unordered_set<std::uint32_t> active_resources;
        const status revision_status = compute_visual_cache_content_revision(
            visual_handle,
            false,
            active_visuals,
            active_resources,
            content_revision);
        if (revision_status != status::success) {
            return revision_status;
        }
        std::uint64_t owner_identity = 14695981039346656037ULL;
        constexpr std::uint32_t owner_kind = 0x43414348U; // CACH
        append_fnv1a64(owner_identity, owner_kind);
        append_fnv1a64(owner_identity, scene_id);
        // Brush rendering suppresses dynamic-guideline animation. Its page
        // must not alias an onscreen page of the same Visual in the same frame.
        if (visual_brush) append_fnv1a64(owner_identity, std::uint32_t{0x56425253U});
        append_fnv1a64(owner_identity, visual_handle);
        const affine_2d_double raster_to_local{
            1.0 / render_at_scale,
            0.0,
            0.0,
            1.0 / render_at_scale,
            cache_visual.cache_bounds_x,
            cache_visual.cache_bounds_y};
        affine_2d_double mask_transform = state.transform;
        affine_2d_double composite_transform = compose_affine(
            raster_to_local, state.transform);
        if (cache->second.snaps_to_device_pixels) {
            progpu_native_image_rect world_bounds{};
            if (!try_transform_bounds(
                    cache_visual.cache_bounds_x,
                    cache_visual.cache_bounds_y,
                    cache_visual.cache_bounds_width,
                    cache_visual.cache_bounds_height,
                    state.transform,
                    world_bounds)) {
                return status::invalid_graph;
            }
            const affine_2d_double snap_offset{
                1.0,
                0.0,
                0.0,
                1.0,
                -static_cast<double>(
                    world_bounds.x - std::floor(world_bounds.x)),
                -static_cast<double>(
                    world_bounds.y - std::floor(world_bounds.y))};
            composite_transform = compose_affine(
                composite_transform, snap_offset);
            mask_transform = compose_affine(mask_transform, snap_offset);
        }
        auto composite_state =
            native::semantic_scene_builder::identity_state();
        if (!try_to_native_affine(
                composite_transform, composite_state.transform)) {
            return status::invalid_graph;
        }
        if (state.has_clip && cache_visual.effect_handle == 0U) {
            composite_state.flags |= PROGPU_NATIVE_SCENE_STATE_CLIP_RECT;
            composite_state.clip_rect = state.clip_rect;
        }
        if (state.guideline_resource_index !=
            PROGPU_NATIVE_SCENE_NO_INDEX) {
            composite_state.flags |=
                PROGPU_NATIVE_SCENE_STATE_GUIDELINE_SET;
            composite_state.guideline_resource_index =
                state.guideline_resource_index;
        }
        std::uint32_t composite_state_index =
            PROGPU_NATIVE_SCENE_NO_INDEX;
        if (!builder.add_state(composite_state, composite_state_index)) {
            return status::invalid_graph;
        }
        std::uint32_t opacity_mask_resource_index =
            state.mask_resource_index;
        if (has_spatial_opacity_mask) {
            const status opacity_mask_status = add_visual_opacity_mask(
                cache_visual.alpha_mask_handle,
                cache_visual,
                mask_transform,
                builder,
                opacity_mask_resource_index,
                clip_paths.first(state.clip_path_count),
                clip_segments.first(state.clip_segment_count),
                clip_boolean_nodes.first(state.clip_boolean_node_count));
            if (opacity_mask_status != status::success) {
                return opacity_mask_status;
            }
        }
        content_state.transform = {
            render_at_scale,
            0.0,
            0.0,
            render_at_scale,
            -cache_visual.cache_bounds_x * render_at_scale,
            -cache_visual.cache_bounds_y * render_at_scale};
        content_state.opacity = 1.0;
        content_state.has_clip = false;
        content_state.mask_resource_index = PROGPU_NATIVE_SCENE_NO_INDEX;
        content_state.clip_path_count = 0U;
        content_state.clip_segment_count = 0U;
        content_state.clip_boolean_node_count = 0U;
        content_state.guideline_resource_index =
            PROGPU_NATIVE_SCENE_NO_INDEX;
        content_state.per_point_guidelines = false;
        content_state.image_sampling =
            PROGPU_NATIVE_IMAGE_SAMPLING_LINEAR;
        content_state.edge_aliased = false;
        content_state.clear_type_enabled = false;
        content_state.text_rendering_mode = 0U;
        content_state.text_hinting_mode = 0U;
        content_state.subpixel_text_disabled =
            !cache->second.enable_clear_type;
        auto raster_state =
            native::semantic_scene_builder::identity_state();
        if (!try_to_native_affine(
                content_state.transform, raster_state.transform)) {
            return status::invalid_graph;
        }
        std::uint32_t raster_state_index = PROGPU_NATIVE_SCENE_NO_INDEX;
        if (!builder.add_state(raster_state, raster_state_index)) {
            return status::invalid_graph;
        }
        progpu_native_scene_layer layer{};
        layer.struct_size = sizeof(layer);
        layer.flags = PROGPU_NATIVE_SCENE_LAYER_CACHE_CONTENT |
            PROGPU_NATIVE_SCENE_LAYER_CACHE_LOCAL_SPACE |
            PROGPU_NATIVE_SCENE_LAYER_BOUNDS;
        if (state.image_sampling == PROGPU_NATIVE_IMAGE_SAMPLING_NEAREST) {
            layer.flags |= PROGPU_NATIVE_SCENE_LAYER_CACHE_NEAREST;
        } else if (state.image_sampling == PROGPU_NATIVE_IMAGE_SAMPLING_FANT) {
            layer.flags |= PROGPU_NATIVE_SCENE_LAYER_CACHE_FANT;
        }
        layer.opacity = static_cast<float>(state.opacity);
        layer.blend_mode = PROGPU_NATIVE_BLEND_SRC_OVER;
        layer.mask_resource_index = opacity_mask_resource_index;
        layer.effect_resource_index = PROGPU_NATIVE_SCENE_NO_INDEX;
        layer.content_revision = finish_nonzero_hash(content_revision);
        layer.composite_revision = finish_nonzero_hash(owner_identity);
        layer.bounds = {
            0.0F,
            0.0F,
            static_cast<float>(raster_width),
            static_cast<float>(raster_height)};
        layer.reserved0 = composite_state_index;
        if (!builder.push_layer(layer)) {
            return status::invalid_graph;
        }
        pushed = true;
        if (!builder.save(raster_state_index)) {
            return status::invalid_graph;
        }
        pushed_content_state = true;
        return status::success;
    }

    status append_visual(
        std::uint32_t handle,
        const render_scope_state& parent_state,
        std::uint32_t depth,
        std::uint64_t scene_id,
        native::semantic_scene_builder& builder,
        std::unordered_map<std::uint32_t, std::uint32_t>& brush_indices,
        std::unordered_map<std::uint32_t, std::uint32_t>& image_indices,
        std::unordered_map<std::uint64_t, glyph_scene_resource>&
            glyph_resources,
        scene_compile_context* compile_context,
        std::unordered_set<std::uint32_t>& active_visuals,
        std::vector<progpu_native_scene_clip_path>& clip_paths,
        std::vector<progpu_native_path_segment>& clip_segments,
        std::vector<progpu_native_scene_path_boolean_node>&
            clip_boolean_nodes,
        scene_metrics& metrics) const {
        if (depth == 0U || depth > maximum_visual_depth ||
            active_visuals.size() >= maximum_visual_depth ||
            !active_visuals.insert(handle).second) {
            return status::invalid_graph;
        }
        const auto visual = visuals.find(handle);
        if (visual == visuals.end()) {
            active_visuals.erase(handle);
            return status::invalid_handle;
        }
        affine_2d_double local_transform{};
        if (visual->second.transform_handle != 0U) {
            const status transform_status = resolve_transform(
                visual->second.transform_handle,
                local_transform);
            if (transform_status != status::success) {
                active_visuals.erase(handle);
                return transform_status;
            }
        }
        double offset_x = visual->second.offset_x;
        double offset_y = visual->second.offset_y;
        render_scope_state current = parent_state;
        if (visual->second.has_scroll_clip) {
            // WPF defines ScrollableAreaClip as a world-space pixel-aligned
            // rectangle and disables the accelerated path under rotation.
            // A transformed AABB would broaden that clip, so retain only the
            // exact axis-preserving subset until native geometry clipping is
            // available here.
            if (!affine_preserves_axis_alignment(parent_state.transform)) {
                active_visuals.erase(handle);
                return status::unsupported_command;
            }
            progpu_native_image_rect scroll_clip{};
            if (!try_transform_bounds(
                    visual->second.scroll_clip_x,
                    visual->second.scroll_clip_y,
                    visual->second.scroll_clip_width,
                    visual->second.scroll_clip_height,
                    parent_state.transform,
                    scroll_clip)) {
                active_visuals.erase(handle);
                return status::invalid_graph;
            }
            const float left = std::ceil(scroll_clip.x);
            const float top = std::ceil(scroll_clip.y);
            const float right = std::floor(
                scroll_clip.x + scroll_clip.width);
            const float bottom = std::floor(
                scroll_clip.y + scroll_clip.height);
            scroll_clip = {
                left,
                top,
                std::max(0.0F, right - left),
                std::max(0.0F, bottom - top)};
            intersect_scope_clip(current, scroll_clip);

            const progpu_native_point offset_world =
                transform_affine_point(
                    {static_cast<float>(offset_x),
                     static_cast<float>(offset_y)},
                    parent_state.transform);
            affine_2d_double inverse_parent{};
            if (!try_invert_affine(
                    parent_state.transform, inverse_parent)) {
                active_visuals.erase(handle);
                return status::invalid_graph;
            }
            const progpu_native_point snapped_offset =
                transform_affine_point(
                    {std::floor(offset_world.x),
                     std::floor(offset_world.y)},
                    inverse_parent);
            offset_x = snapped_offset.x;
            offset_y = snapped_offset.y;
        }
        const affine_2d_double offset_transform{
            1.0,
            0.0,
            0.0,
            1.0,
            offset_x,
            offset_y};
        const affine_2d_double transform = compose_affine(
            compose_affine(local_transform, offset_transform),
            parent_state.transform);
        current.transform = transform;
        // WPF Visual content never inherits the parent's guideline frame.
        current.guideline_resource_index = PROGPU_NATIVE_SCENE_NO_INDEX;
        current.per_point_guidelines = false;
        const status guideline_status = apply_static_guidelines(
            visual->second.guidelines_x,
            visual->second.guidelines_y,
            current,
            builder,
            visual->second.cache_mode_handle != 0U);
        if (guideline_status != status::success) {
            active_visuals.erase(handle);
            return guideline_status;
        }
        double opacity_mask_alpha = 1.0;
        const bool has_spatial_visual_opacity_mask =
            visual->second.alpha_mask_handle != 0U &&
            gradient_brushes.contains(
                visual->second.alpha_mask_handle);
        const status opacity_mask_status = has_spatial_visual_opacity_mask
            ? status::success
            : resolve_uniform_opacity_mask_alpha(
                visual->second.alpha_mask_handle,
                opacity_mask_alpha);
        if (opacity_mask_status != status::success) {
            active_visuals.erase(handle);
            return opacity_mask_status;
        }
        const double local_visual_opacity =
            visual->second.opacity * opacity_mask_alpha;
        current.opacity *= local_visual_opacity;
        if (!std::isfinite(current.opacity) ||
            current.opacity < 0.0 || current.opacity > 1.0) {
            active_visuals.erase(handle);
            return status::invalid_graph;
        }
        const bool isolate_uncached_effect_composite =
            visual->second.effect_handle != 0U &&
            visual->second.cache_mode_handle == 0U &&
            (current.opacity != 1.0 ||
                has_spatial_visual_opacity_mask);
        if (isolate_uncached_effect_composite &&
            parent_state.opacity != 1.0) {
            active_visuals.erase(handle);
            return status::unsupported_command;
        }
        // WPF owns opacity and its opacity mask at each Visual boundary. For
        // an uncached Visual without its own effect, retain that boundary as
        // one outer group so descendant effects are completed before the
        // ancestor alpha/mask is applied. Exact typed descendant bounds keep
        // the intermediate bounded. A spatial mask cannot use the legacy
        // ungrouped compatibility path, so missing bounds fail closed.
        const bool needs_uncached_visual_composite =
            visual->second.effect_handle == 0U &&
            visual->second.cache_mode_handle == 0U &&
            (local_visual_opacity != 1.0 ||
                has_spatial_visual_opacity_mask);
        if (needs_uncached_visual_composite &&
            !visual->second.has_cache_bounds &&
            has_spatial_visual_opacity_mask) {
            active_visuals.erase(handle);
            return status::unsupported_command;
        }
        const bool isolate_uncached_visual_composite =
            needs_uncached_visual_composite &&
            visual->second.has_cache_bounds;
        if ((visual->second.render_options_flags &
                render_option_bitmap_scaling) != 0U) {
            current.image_sampling =
                visual->second.bitmap_scaling_mode == 3U
                ? PROGPU_NATIVE_IMAGE_SAMPLING_NEAREST
                : visual->second.bitmap_scaling_mode == 2U
                ? PROGPU_NATIVE_IMAGE_SAMPLING_FANT
                : PROGPU_NATIVE_IMAGE_SAMPLING_LINEAR;
        }
        if ((visual->second.render_options_flags &
                render_option_edge_mode) != 0U) {
            current.edge_aliased = visual->second.edge_mode != 0U;
        }
        if ((visual->second.render_options_flags &
                render_option_clear_type_hint) != 0U) {
            current.clear_type_enabled =
                visual->second.clear_type_hint != 0U;
        }
        if ((visual->second.render_options_flags &
                render_option_text_rendering_mode) != 0U) {
            current.text_rendering_mode =
                visual->second.text_rendering_mode;
        }
        if ((visual->second.render_options_flags &
                render_option_text_hinting_mode) != 0U) {
            current.text_hinting_mode = visual->second.text_hinting_mode;
        }
        if (visual->second.clip_geometry_handle != 0U) {
            status clip_status = apply_visual_rectangle_clip(
                visual->second.clip_geometry_handle,
                current);
            if (clip_status == status::unsupported_command) {
                clip_status = append_geometry_clip(
                    visual->second.clip_geometry_handle,
                    current.transform, current, builder,
                    clip_paths, clip_segments, clip_boolean_nodes);
            }
            if (clip_status != status::success) {
                active_visuals.erase(handle);
                return clip_status;
            }
        }

        const auto visual_resource = resources.find(handle);
        const bool is_viewport3d = visual_resource != resources.end() &&
            visual_resource->second.type == type_viewport3d_visual;
        const bool isolate_viewport_clip = is_viewport3d &&
            current.mask_resource_index != PROGPU_NATIVE_SCENE_NO_INDEX &&
            visual->second.effect_handle == 0U &&
            visual->second.cache_mode_handle == 0U;
        auto state = native::semantic_scene_builder::identity_state();
        if (!try_to_native_affine(current.transform, state.transform)) {
            active_visuals.erase(handle);
            return status::invalid_graph;
        }
        state.opacity = isolate_uncached_effect_composite
            ? 1.0F
            : isolate_uncached_visual_composite
            ? static_cast<float>(parent_state.opacity)
            : static_cast<float>(current.opacity);
        if (current.has_clip && visual->second.effect_handle == 0U) {
            state.flags |= PROGPU_NATIVE_SCENE_STATE_CLIP_RECT;
            state.clip_rect = current.clip_rect;
        }
        if (current.mask_resource_index != PROGPU_NATIVE_SCENE_NO_INDEX &&
            visual->second.effect_handle == 0U &&
            visual->second.cache_mode_handle == 0U && !isolate_viewport_clip) {
            state.flags |= PROGPU_NATIVE_SCENE_STATE_MASK;
            state.mask_resource_index = current.mask_resource_index;
        }
        const bool composite_only_guidelines =
            visual->second.cache_mode_handle != 0U &&
            (visual->second.guidelines_x.size() > 1U ||
                visual->second.guidelines_y.size() > 1U);
        if (current.guideline_resource_index !=
                PROGPU_NATIVE_SCENE_NO_INDEX &&
            !composite_only_guidelines) {
            state.flags |= PROGPU_NATIVE_SCENE_STATE_GUIDELINE_SET;
            state.guideline_resource_index =
                current.guideline_resource_index;
        }
        std::uint32_t state_index = PROGPU_NATIVE_SCENE_NO_INDEX;
        if (!builder.add_state(state, state_index) ||
            !builder.save(state_index)) {
            active_visuals.erase(handle);
            return status::invalid_graph;
        }

        bool visual_composite_layer_pushed = false;
        if (isolate_uncached_visual_composite) {
            std::uint32_t opacity_mask_resource_index =
                PROGPU_NATIVE_SCENE_NO_INDEX;
            if (has_spatial_visual_opacity_mask) {
                const status mask_status = add_visual_opacity_mask(
                    visual->second.alpha_mask_handle,
                    visual->second,
                    current.transform,
                    builder,
                    opacity_mask_resource_index);
                if (mask_status != status::success) {
                    builder.restore();
                    active_visuals.erase(handle);
                    return mask_status;
                }
            }
            progpu_native_scene_layer composite_layer{};
            composite_layer.struct_size = sizeof(composite_layer);
            composite_layer.flags =
                PROGPU_NATIVE_SCENE_LAYER_FORCE_ISOLATION |
                PROGPU_NATIVE_SCENE_LAYER_BOUNDS;
            composite_layer.opacity =
                static_cast<float>(local_visual_opacity);
            composite_layer.blend_mode = PROGPU_NATIVE_BLEND_SRC_OVER;
            composite_layer.mask_resource_index =
                opacity_mask_resource_index;
            composite_layer.effect_resource_index =
                PROGPU_NATIVE_SCENE_NO_INDEX;
            if (!try_transform_bounds(
                    visual->second.cache_bounds_x,
                    visual->second.cache_bounds_y,
                    visual->second.cache_bounds_width,
                    visual->second.cache_bounds_height,
                    current.transform,
                    composite_layer.bounds) ||
                !builder.push_layer(composite_layer)) {
                builder.restore();
                active_visuals.erase(handle);
                return status::invalid_graph;
            }
            visual_composite_layer_pushed = true;
        }

        std::uint32_t effect_layer_count = 0U;
        status effect_status = add_visual_effect_layer(
            handle,
            visual->second.effect_handle,
            current,
            visual->second.cache_mode_handle != 0U,
            builder,
            effect_layer_count);
        if (effect_status == status::success && isolate_viewport_clip) {
            // Mesh shaders retain their depth-tested rendering contract.
            // Clip the completed 3D image with the shared layer-mask path;
            // never silently drop a mask from an unsupported per-draw state.
            progpu_native_scene_layer clip_layer{};
            clip_layer.struct_size = sizeof(clip_layer);
            clip_layer.flags = PROGPU_NATIVE_SCENE_LAYER_FORCE_ISOLATION;
            clip_layer.opacity = 1.0F;
            clip_layer.blend_mode = PROGPU_NATIVE_BLEND_SRC_OVER;
            clip_layer.effect_resource_index = PROGPU_NATIVE_SCENE_NO_INDEX;
            effect_status = attach_visual_output_clip(clip_layer, current, builder);
            if (effect_status == status::success) {
                if (builder.push_layer(clip_layer)) {
                    ++effect_layer_count;
                } else {
                    effect_status = status::invalid_graph;
                }
            }
        }
        if (effect_status != status::success) {
            while (effect_layer_count != 0U) {
                builder.pop_layer();
                --effect_layer_count;
            }
            if (visual_composite_layer_pushed) {
                builder.pop_layer();
            }
            builder.restore();
            active_visuals.erase(handle);
            return effect_status;
        }

        bool cache_layer_pushed = false;
        bool skip_cached_content = false;
        bool cache_content_state_pushed = false;
        render_scope_state content_scope = current;
        // Clip belongs to the completed effect/isolated-3D output. Nested render-data
        // scopes and descendant visuals must see the untruncated source,
        // including when that source is a local bitmap cache.
        if (effect_layer_count != 0U) {
            content_scope.has_clip = false;
            content_scope.mask_resource_index = PROGPU_NATIVE_SCENE_NO_INDEX;
            content_scope.clip_path_count = 0U;
            content_scope.clip_segment_count = 0U;
            content_scope.clip_boolean_node_count = 0U;
        }
        const render_scope_state cache_input_scope = content_scope;
        const status cache_status = add_visual_cache_layer(
            visual->second.cache_mode_handle,
            handle,
            scene_id,
            compile_context != nullptr && compile_context->is_visual_brush(),
            cache_input_scope,
            clip_paths,
            clip_segments,
            clip_boolean_nodes,
            builder,
            cache_layer_pushed,
            skip_cached_content,
            cache_content_state_pushed,
            content_scope);
        if (cache_status != status::success) {
            if (cache_content_state_pushed) {
                builder.restore();
            }
            if (cache_layer_pushed) {
                builder.pop_layer();
            }
            while (effect_layer_count != 0U) {
                builder.pop_layer();
                --effect_layer_count;
            }
            if (visual_composite_layer_pushed) {
                builder.pop_layer();
            }
            builder.restore();
            active_visuals.erase(handle);
            return cache_status;
        }
        if (!cache_layer_pushed) {
            if (isolate_uncached_effect_composite) {
                content_scope.opacity = 1.0;
            } else if (isolate_uncached_visual_composite) {
                content_scope.opacity = parent_state.opacity;
            }
        }

        // Keep outer clip prefixes intact while an isolated source records
        // its own clip coordinate frame. Empty vectors allocate nothing;
        // only actual nested geometry clips materialize scratch storage.
        std::vector<progpu_native_scene_clip_path> isolated_clip_paths;
        std::vector<progpu_native_path_segment> isolated_clip_segments;
        std::vector<progpu_native_scene_path_boolean_node>
            isolated_clip_boolean_nodes;
        const bool isolated_clip_frame = effect_layer_count != 0U ||
            cache_layer_pushed;
        auto& content_clip_paths = isolated_clip_frame
            ? isolated_clip_paths : clip_paths;
        auto& content_clip_segments = isolated_clip_frame
            ? isolated_clip_segments : clip_segments;
        auto& content_clip_boolean_nodes = isolated_clip_frame
            ? isolated_clip_boolean_nodes : clip_boolean_nodes;

        ++metrics.visual_count;
        metrics.maximum_visual_depth =
            std::max(metrics.maximum_visual_depth, depth);
        status result = status::success;
        if (!skip_cached_content && is_viewport3d) {
            if (content_scope.mask_resource_index !=
                           PROGPU_NATIVE_SCENE_NO_INDEX ||
                       content_scope.guideline_resource_index !=
                           PROGPU_NATIVE_SCENE_NO_INDEX) {
                result = status::unsupported_command;
            } else if (!affine_preserves_axis_alignment(
                           content_scope.transform)) {
                result = status::unsupported_command;
            } else {
                const auto canonical = viewport3d_visuals.find(handle);
                viewport3d_scene_state canonical_scene{};
                const viewport3d_scene_state* render_scene = nullptr;
                const bool has_canonical_graph =
                    canonical != viewport3d_visuals.end() &&
                    canonical->second.has_child_binding;
                bool render_viewport = true;
                if (has_canonical_graph) {
                    if (canonical->second.child_handle == 0U) {
                        render_viewport = false;
                    } else {
                        result = build_canonical_viewport3d_scene(
                            canonical->second.child_handle,
                            canonical_scene);
                        render_scene = &canonical_scene;
                    }
                } else {
                    const auto sideband = viewport3d_scenes.find(handle);
                    if (sideband == viewport3d_scenes.end()) {
                        result = status::unsupported_command;
                    } else {
                        render_scene = &sideband->second;
                    }
                }
                progpu_native_image_rect source_viewport = render_scene
                    ? render_scene->viewport
                    : progpu_native_image_rect{};
                progpu_native_scene_camera_3d camera = render_scene
                    ? render_scene->camera
                    : progpu_native_scene_camera_3d{};
                if (canonical != viewport3d_visuals.end()) {
                    if (canonical->second.has_viewport) {
                        if (canonical->second.width <= 0.0 ||
                            canonical->second.height <= 0.0 ||
                            !finite_double_as_float(canonical->second.x) ||
                            !finite_double_as_float(canonical->second.y) ||
                            !finite_double_as_float(
                                canonical->second.width) ||
                            !finite_double_as_float(
                                canonical->second.height)) {
                            render_viewport = false;
                        } else {
                            source_viewport = {
                                static_cast<float>(canonical->second.x),
                                static_cast<float>(canonical->second.y),
                                static_cast<float>(canonical->second.width),
                                static_cast<float>(canonical->second.height)};
                        }
                    }
                    if (canonical->second.has_camera_binding) {
                        if (canonical->second.camera_handle == 0U) {
                            render_viewport = false;
                        } else if (render_viewport) {
                            const status camera_status = resolve_camera3d(
                                canonical->second.camera_handle,
                                static_cast<double>(source_viewport.width) /
                                    source_viewport.height,
                                camera);
                            if (camera_status != status::success) {
                                result = camera_status;
                            }
                        }
                    } else if (has_canonical_graph) {
                        render_viewport = false;
                    }
                }
                progpu_native_image_rect viewport{};
                if (result == status::success && render_viewport &&
                    !try_transform_bounds(
                        source_viewport.x,
                        source_viewport.y,
                        source_viewport.width,
                        source_viewport.height,
                        content_scope.transform,
                        viewport)) {
                    result = status::invalid_graph;
                } else if (result == status::success && render_viewport &&
                    render_scene != nullptr &&
                    !render_scene->meshes.empty()) {
                    auto viewport_state =
                        native::semantic_scene_builder::identity_state();
                    viewport_state.opacity = static_cast<float>(
                        content_scope.opacity);
                    if (content_scope.has_clip) {
                        viewport_state.flags |=
                            PROGPU_NATIVE_SCENE_STATE_CLIP_RECT;
                        viewport_state.clip_rect = content_scope.clip_rect;
                    }
                    std::uint32_t viewport_state_index =
                        PROGPU_NATIVE_SCENE_NO_INDEX;
                    if (!builder.add_state(
                            viewport_state, viewport_state_index) ||
                        !builder.draw_meshes_3d(
                            render_scene->meshes,
                            render_scene->vertices,
                            render_scene->indices,
                            render_scene->lights,
                            render_scene->materials,
                            render_scene->gradient_stops,
                            camera,
                            viewport,
                            viewport_state_index)) {
                        result = status::invalid_graph;
                    }
                }
            }
        }
        if (!skip_cached_content && visual->second.content_handle != 0U) {
            if (result == status::success) {
                result = append_render_data(
                    visual->second.content_handle,
                    content_scope,
                    builder,
                    brush_indices,
                    image_indices,
                    glyph_resources,
                    compile_context,
                    active_visuals,
                    content_clip_paths,
                    content_clip_segments,
                    content_clip_boolean_nodes,
                    metrics);
            }
        }
        if (!skip_cached_content && result == status::success) {
            for (const auto child : visual->second.children) {
                result = append_visual(
                    child,
                    content_scope,
                    depth + 1U,
                    scene_id,
                    builder,
                    brush_indices,
                    image_indices,
                    glyph_resources,
                    compile_context,
                    active_visuals,
                    content_clip_paths,
                    content_clip_segments,
                    content_clip_boolean_nodes,
                    metrics);
                if (result != status::success) {
                    break;
                }
            }
        }
        if (cache_content_state_pushed && !builder.restore() &&
            result == status::success) {
            result = status::invalid_graph;
        }
        if (cache_layer_pushed && !builder.pop_layer() &&
            result == status::success) {
            result = status::invalid_graph;
        }
        while (effect_layer_count != 0U) {
            if (!builder.pop_layer() && result == status::success) {
                result = status::invalid_graph;
            }
            --effect_layer_count;
        }
        if (visual_composite_layer_pushed && !builder.pop_layer() &&
            result == status::success) {
            result = status::invalid_graph;
        }
        if (!builder.restore() && result == status::success) {
            result = status::invalid_graph;
        }
        active_visuals.erase(handle);
        return result;
    }
};

struct channel::build_cache {
    scene_build_request request{};
    std::vector<std::byte> stream;
    scene_metrics metrics{};
    scene_build_result result{};
};

namespace {

constexpr std::uint32_t known_scene_build_request_flags =
    static_cast<std::uint32_t>(scene_build_request_flags::visual_brush);

bool valid_scene_build_request(const scene_build_request& request) noexcept {
    const auto raw_flags = static_cast<std::uint32_t>(request.flags);
    return (raw_flags & ~known_scene_build_request_flags) == 0U &&
        request.target_handle != 0U && request.scene_id != 0U &&
        request.generation != 0U && request.request_serial != 0U &&
        std::isfinite(request.dpi_scale_x) &&
        std::isfinite(request.dpi_scale_y) &&
        request.dpi_scale_x > 0.0 && request.dpi_scale_x <= 65'536.0 &&
        request.dpi_scale_y > 0.0 && request.dpi_scale_y <= 65'536.0;
}

bool same_scene_build_request(
    const scene_build_request& left,
    const scene_build_request& right) noexcept {
    return left.flags == right.flags &&
        left.target_handle == right.target_handle &&
        left.scene_id == right.scene_id &&
        left.generation == right.generation &&
        left.dpi_scale_x == right.dpi_scale_x &&
        left.dpi_scale_y == right.dpi_scale_y &&
        left.monotonic_time_nanoseconds ==
            right.monotonic_time_nanoseconds &&
        left.request_serial == right.request_serial;
}

} // namespace

channel::channel()
    : implementation_(std::make_unique<implementation>()) {
}

channel::~channel() = default;
channel::channel(channel&&) noexcept = default;
channel& channel::operator=(channel&&) noexcept = default;

status channel::apply(
    std::span<const std::byte> bytes,
    batch_metrics* metrics) noexcept {
    if (bytes.data() == nullptr && !bytes.empty()) {
        return status::invalid_argument;
    }
    batch_metrics local_metrics{};
    local_metrics.total_bytes = static_cast<std::uint32_t>(
        std::min<std::size_t>(
            bytes.size(),
            std::numeric_limits<std::uint32_t>::max()));
    try {
        auto candidate = std::make_unique<implementation>(*implementation_);
        batch_reader reader(bytes);
        command_view view{};
        for (;;) {
            const status read_status = reader.next(view);
            if (read_status == status::end_of_batch) {
                implementation_ = std::move(candidate);
                build_cache_.reset();
                if (metrics != nullptr) {
                    *metrics = local_metrics;
                }
                return status::success;
            }
            if (read_status != status::success) {
                if (metrics != nullptr) {
                    *metrics = local_metrics;
                }
                return read_status;
            }
            ++local_metrics.command_count;
            const status apply_status =
                candidate->apply_command(view, local_metrics);
            if (apply_status != status::success) {
                if (metrics != nullptr) {
                    *metrics = local_metrics;
                }
                return apply_status;
            }
            ++local_metrics.supported_command_count;
        }
    } catch (const std::bad_alloc&) {
        if (metrics != nullptr) {
            *metrics = local_metrics;
        }
        return status::invalid_argument;
    }
}

status channel::get_bitmap_source_dpi(
    std::uint32_t handle, double& dpi_x, double& dpi_y) const noexcept {
    const auto source = implementation_->bitmap_sources.find(handle);
    if (source == implementation_->bitmap_sources.end()) {
        return status::invalid_handle;
    }
    dpi_x = source->second.dpi_x;
    dpi_y = source->second.dpi_y;
    return status::success;
}

status channel::set_bitmap_source_rgba8(
    std::uint32_t handle,
    std::uint32_t width,
    std::uint32_t height,
    std::uint32_t row_bytes,
    std::span<const std::byte> pixels,
    double dpi_x,
    double dpi_y) noexcept {
    if (!std::isfinite(dpi_x) || !std::isfinite(dpi_y) ||
        dpi_x <= 0.0 || dpi_y <= 0.0 ||
        !std::isfinite(static_cast<double>(width) * 96.0 / dpi_x) ||
        !std::isfinite(static_cast<double>(height) * 96.0 / dpi_y)) {
        return status::invalid_argument;
    }
    if (!implementation_->require_resource(handle, type_bitmap_source)) {
        return status::invalid_handle;
    }
    const std::uint64_t minimum_row_bytes =
        static_cast<std::uint64_t>(width) * 4U;
    const std::uint64_t required_bytes = height == 0U
        ? 0U
        : static_cast<std::uint64_t>(row_bytes) * (height - 1U) +
            minimum_row_bytes;
    if (width == 0U || height == 0U || width > 16'384U ||
        height > 16'384U || row_bytes < minimum_row_bytes ||
        required_bytes != pixels.size()) {
        return status::invalid_argument;
    }
    try {
        implementation::bitmap_source_state source{};
        source.width = width;
        source.height = height;
        source.dpi_x = dpi_x;
        source.dpi_y = dpi_y;
        source.row_bytes = row_bytes;
        source.pixels.assign(pixels.begin(), pixels.end());
        source.external_image = false;
        implementation_->bitmap_sources.insert_or_assign(
            handle, std::move(source));
        implementation_->increment_generation(handle);
        build_cache_.reset();
        return status::success;
    } catch (const std::bad_alloc&) {
        return status::capacity_exceeded;
    } catch (...) {
        return status::invalid_argument;
    }
}

status channel::set_bitmap_source_external_image(
    std::uint32_t handle,
    std::uint32_t width,
    std::uint32_t height,
    double dpi_x,
    double dpi_y) noexcept {
    if (!std::isfinite(dpi_x) || !std::isfinite(dpi_y) ||
        dpi_x <= 0.0 || dpi_y <= 0.0 ||
        !std::isfinite(static_cast<double>(width) * 96.0 / dpi_x) ||
        !std::isfinite(static_cast<double>(height) * 96.0 / dpi_y)) {
        return status::invalid_argument;
    }
    if (!implementation_->require_resource(handle, type_bitmap_source)) {
        return status::invalid_handle;
    }
    if (width == 0U || height == 0U || width > 16'384U ||
        height > 16'384U) {
        return status::invalid_argument;
    }
    try {
        implementation::bitmap_source_state source{};
        source.width = width;
        source.height = height;
        source.dpi_x = dpi_x;
        source.dpi_y = dpi_y;
        source.row_bytes = width * 4U;
        source.external_image = true;
        implementation_->bitmap_sources.insert_or_assign(
            handle, std::move(source));
        implementation_->increment_generation(handle);
        build_cache_.reset();
        return status::success;
    } catch (const std::bad_alloc&) {
        return status::capacity_exceeded;
    } catch (...) {
        return status::invalid_argument;
    }
}

status channel::set_double_buffered_bitmap_rgba8(
    std::uint32_t handle,
    std::uint32_t width,
    std::uint32_t height,
    std::uint32_t row_bytes,
    std::span<const std::byte> pixels,
    double dpi_x,
    double dpi_y) noexcept {
    if (!std::isfinite(dpi_x) || !std::isfinite(dpi_y) ||
        dpi_x <= 0.0 || dpi_y <= 0.0 ||
        !std::isfinite(static_cast<double>(width) * 96.0 / dpi_x) ||
        !std::isfinite(static_cast<double>(height) * 96.0 / dpi_y)) {
        return status::invalid_argument;
    }
    if (!implementation_->require_resource(
            handle, type_double_buffered_bitmap)) {
        return status::invalid_handle;
    }
    const std::uint64_t minimum_row_bytes =
        static_cast<std::uint64_t>(width) * 4U;
    const std::uint64_t required_bytes = height == 0U
        ? 0U
        : static_cast<std::uint64_t>(row_bytes) * (height - 1U) +
            minimum_row_bytes;
    if (width == 0U || height == 0U || width > 16'384U ||
        height > 16'384U || row_bytes < minimum_row_bytes ||
        required_bytes != pixels.size()) {
        return status::invalid_argument;
    }
    try {
        implementation::bitmap_source_state source{};
        source.width = width;
        source.height = height;
        source.dpi_x = dpi_x;
        source.dpi_y = dpi_y;
        source.row_bytes = row_bytes;
        source.pixels.assign(pixels.begin(), pixels.end());
        source.external_image = false;
        implementation_->bitmap_sources.insert_or_assign(
            handle, std::move(source));
        implementation_->increment_generation(handle);
        build_cache_.reset();
        return status::success;
    } catch (const std::bad_alloc&) {
        return status::capacity_exceeded;
    } catch (...) {
        return status::invalid_argument;
    }
}

status channel::set_double_buffered_bitmap_external_image(
    std::uint32_t handle,
    std::uint32_t width,
    std::uint32_t height,
    double dpi_x,
    double dpi_y) noexcept {
    if (!std::isfinite(dpi_x) || !std::isfinite(dpi_y) ||
        dpi_x <= 0.0 || dpi_y <= 0.0 ||
        !std::isfinite(static_cast<double>(width) * 96.0 / dpi_x) ||
        !std::isfinite(static_cast<double>(height) * 96.0 / dpi_y)) {
        return status::invalid_argument;
    }
    if (!implementation_->require_resource(
            handle, type_double_buffered_bitmap)) {
        return status::invalid_handle;
    }
    if (width == 0U || height == 0U || width > 16'384U ||
        height > 16'384U) {
        return status::invalid_argument;
    }
    try {
        implementation::bitmap_source_state source{};
        source.width = width;
        source.height = height;
        source.dpi_x = dpi_x;
        source.dpi_y = dpi_y;
        source.row_bytes = width * 4U;
        source.external_image = true;
        implementation_->bitmap_sources.insert_or_assign(
            handle, std::move(source));
        implementation_->increment_generation(handle);
        build_cache_.reset();
        return status::success;
    } catch (const std::bad_alloc&) {
        return status::capacity_exceeded;
    } catch (...) {
        return status::invalid_argument;
    }
}

status channel::set_media_player_external_image(
    std::uint32_t handle,
    std::uint32_t width,
    std::uint32_t height) noexcept {
    if (!implementation_->require_resource(handle, type_media_player)) {
        return status::invalid_handle;
    }
    if (width == 0U || height == 0U || width > 16'384U ||
        height > 16'384U) {
        return status::invalid_argument;
    }
    try {
        implementation_->media_players.insert_or_assign(
            handle,
            implementation::media_player_state{width, height});
        implementation_->increment_generation(handle);
        build_cache_.reset();
        return status::success;
    } catch (const std::bad_alloc&) {
        return status::capacity_exceeded;
    } catch (...) {
        return status::invalid_argument;
    }
}

status channel::set_d3d_image_external_image(
    std::uint32_t handle,
    std::uint32_t width,
    std::uint32_t height,
    std::uint64_t content_version) noexcept {
    if (!implementation_->require_resource(handle, type_d3d_image) ||
        !implementation_->d3d_images.contains(handle)) {
        return status::invalid_handle;
    }
    if (width == 0U || height == 0U || width > 16'384U ||
        height > 16'384U || content_version == 0U) {
        return status::invalid_argument;
    }
    try {
        implementation_->d3d_images.insert_or_assign(
            handle,
            implementation::d3d_image_state{
                width, height, content_version, true});
        implementation_->increment_generation(handle);
        build_cache_.reset();
        return status::success;
    } catch (const std::bad_alloc&) {
        return status::capacity_exceeded;
    } catch (...) {
        return status::invalid_argument;
    }
}

status channel::set_drawing_image_bounds(
    std::uint32_t handle,
    double x,
    double y,
    double width,
    double height) noexcept {
    if (!implementation_->require_resource(handle, type_drawing_image) ||
        !implementation_->drawing_images.contains(handle)) {
        return status::invalid_handle;
    }
    if (!finite_double_as_float(x) || !finite_double_as_float(y) ||
        !finite_double_as_float(width) || !finite_double_as_float(height) ||
        width <= 0.0 || height <= 0.0) {
        return status::invalid_argument;
    }
    auto& image = implementation_->drawing_images.at(handle);
    image.bounds_x = x;
    image.bounds_y = y;
    image.bounds_width = width;
    image.bounds_height = height;
    image.has_bounds = true;
    implementation_->increment_generation(handle);
    build_cache_.reset();
    return status::success;
}

status channel::set_drawing_group_bounds(
    std::uint32_t handle,
    double x,
    double y,
    double width,
    double height) noexcept {
    if (!implementation_->require_resource(handle, type_drawing_group) ||
        !implementation_->drawing_groups.contains(handle)) {
        return status::invalid_handle;
    }
    if (!finite_double_as_float(x) || !finite_double_as_float(y) ||
        !finite_double_as_float(width) || !finite_double_as_float(height) ||
        width <= 0.0 || height <= 0.0) {
        return status::invalid_argument;
    }
    auto& group = implementation_->drawing_groups.at(handle);
    group.bounds_x = x;
    group.bounds_y = y;
    group.bounds_width = width;
    group.bounds_height = height;
    group.has_bounds = true;
    implementation_->increment_generation(handle);
    build_cache_.reset();
    return status::success;
}

status channel::set_visual_cache_bounds(
    std::uint32_t handle,
    double x,
    double y,
    double width,
    double height) noexcept {
    if (!implementation_->require_visual(handle)) {
        return status::invalid_handle;
    }
    if (!finite_double_as_float(x) || !finite_double_as_float(y) ||
        !finite_double_as_float(width) || !finite_double_as_float(height) ||
        width <= 0.0 || height <= 0.0) {
        return status::invalid_argument;
    }
    auto& visual = implementation_->visuals.at(handle);
    visual.cache_bounds_x = x;
    visual.cache_bounds_y = y;
    visual.cache_bounds_width = width;
    visual.cache_bounds_height = height;
    visual.has_cache_bounds = true;
    implementation_->increment_generation(handle);
    build_cache_.reset();
    return status::success;
}

status channel::set_viewport3d_scene(
    std::uint32_t handle,
    const progpu_native_scene_camera_3d& camera,
    progpu_native_image_rect viewport,
    std::span<const progpu_native_scene_mesh_3d> meshes,
    std::span<const progpu_native_scene_mesh_3d_vertex> vertices,
    std::span<const std::uint32_t> indices) noexcept {
    return set_viewport3d_scene(
        handle, camera, viewport, meshes, vertices, indices,
        std::span<const progpu_native_scene_light_3d>{});
}

status channel::set_viewport3d_scene(
    std::uint32_t handle,
    const progpu_native_scene_camera_3d& camera,
    progpu_native_image_rect viewport,
    std::span<const progpu_native_scene_mesh_3d> meshes,
    std::span<const progpu_native_scene_mesh_3d_vertex> vertices,
    std::span<const std::uint32_t> indices,
    std::span<const progpu_native_scene_light_3d> lights) noexcept {
    return set_viewport3d_scene(
        handle,
        camera,
        viewport,
        meshes,
        vertices,
        indices,
        lights,
        std::span<const progpu_native_scene_brush>{},
        std::span<const progpu_native_scene_gradient_stop>{});
}

status channel::set_viewport3d_scene(
    std::uint32_t handle,
    const progpu_native_scene_camera_3d& camera,
    progpu_native_image_rect viewport,
    std::span<const progpu_native_scene_mesh_3d> meshes,
    std::span<const progpu_native_scene_mesh_3d_vertex> vertices,
    std::span<const std::uint32_t> indices,
    std::span<const progpu_native_scene_light_3d> lights,
    std::span<const progpu_native_scene_brush> materials,
    std::span<const progpu_native_scene_gradient_stop>
        gradient_stops) noexcept {
    if (!implementation_->require_resource(handle, type_viewport3d_visual) ||
        !implementation_->visuals.contains(handle)) {
        return status::invalid_handle;
    }
    const std::uint64_t payload_bytes =
        static_cast<std::uint64_t>(meshes.size_bytes()) +
        vertices.size_bytes() + indices.size_bytes() + lights.size_bytes() +
        materials.size_bytes() + gradient_stops.size_bytes();
    if (meshes.empty() || vertices.empty() || indices.empty() ||
        (!materials.empty() && materials.size() != meshes.size()) ||
        (materials.empty() && !gradient_stops.empty()) ||
        meshes.size() > std::numeric_limits<std::uint32_t>::max() ||
        vertices.size() > std::numeric_limits<std::uint32_t>::max() ||
        indices.size() > std::numeric_limits<std::uint32_t>::max() ||
        payload_bytes > PROGPU_NATIVE_SCENE_MAX_STREAM_BYTES ||
        !std::isfinite(viewport.x) || !std::isfinite(viewport.y) ||
        !std::isfinite(viewport.width) || viewport.width <= 0.0F ||
        !std::isfinite(viewport.height) || viewport.height <= 0.0F ||
        !semantic::is_valid_semantic_camera_3d(camera)) {
        return status::invalid_argument;
    }
    // The owned baseline passed every semantic validator before insertion.
    // Exact equality (including all reserved bits and array lengths) therefore
    // proves validity without repeating per-element validation on a no-op.
    // Changed input continues through every validator below. These pointer-free
    // wire records have no implicit padding (ABI layout tests). libc memcmp
    // selects the platform SIMD implementation without temporary allocations.
    const auto equal_bytes = []<typename T>(
        std::span<const T> left, std::span<const T> right) noexcept {
        return left.size() == right.size() &&
            (left.empty() || std::memcmp(
                left.data(), right.data(), left.size_bytes()) == 0);
    };
    const auto retained = implementation_->viewport3d_scenes.find(handle);
    if (retained != implementation_->viewport3d_scenes.end()) {
        const auto& old = retained->second;
        if (equal_bytes(std::span{&old.camera, 1U},
                std::span{&camera, 1U}) &&
            equal_bytes(std::span{&old.viewport, 1U},
                std::span<const progpu_native_image_rect>{&viewport, 1U}) &&
            equal_bytes(std::span{old.meshes}, meshes) &&
            equal_bytes(std::span{old.vertices}, vertices) &&
            equal_bytes(std::span{old.indices}, indices) &&
            equal_bytes(std::span{old.lights}, lights) &&
            equal_bytes(std::span{old.materials}, materials) &&
            equal_bytes(std::span{old.gradient_stops}, gradient_stops)) {
            return status::success;
        }
    }
    for (const auto& vertex : vertices) {
        if (!semantic::is_valid_semantic_mesh_3d_vertex(vertex)) {
            return status::invalid_argument;
        }
    }
    for (const auto& light : lights) {
        if (!semantic::is_valid_semantic_light_3d(light)) {
            return status::invalid_argument;
        }
    }
    for (const auto& material : materials) {
        if (!semantic::is_valid_semantic_brush(
                material, gradient_stops) ||
            (material.type != PROGPU_NATIVE_SCENE_BRUSH_SOLID &&
                material.type != PROGPU_NATIVE_SCENE_BRUSH_LINEAR_GRADIENT &&
                material.type != PROGPU_NATIVE_SCENE_BRUSH_RADIAL_GRADIENT)) {
            return status::invalid_argument;
        }
    }
    for (const auto& mesh : meshes) {
        if (!semantic::is_valid_semantic_mesh_3d(
                mesh, vertices.size(), indices.size(), lights.size())) {
            return status::invalid_argument;
        }
        for (std::size_t index = mesh.index_offset;
             index < static_cast<std::size_t>(mesh.index_offset) +
                 mesh.index_count;
             ++index) {
            if (indices[index] >= mesh.vertex_count) {
                return status::invalid_argument;
            }
        }
    }
    try {
        implementation::viewport3d_scene_state scene{};
        scene.camera = camera;
        scene.viewport = viewport;
        scene.meshes.assign(meshes.begin(), meshes.end());
        scene.vertices.assign(vertices.begin(), vertices.end());
        scene.indices.assign(indices.begin(), indices.end());
        scene.lights.assign(lights.begin(), lights.end());
        scene.materials.assign(materials.begin(), materials.end());
        scene.gradient_stops.assign(
            gradient_stops.begin(), gradient_stops.end());
        implementation_->viewport3d_scenes.insert_or_assign(
            handle, std::move(scene));
        implementation_->increment_generation(handle);
        build_cache_.reset();
        return status::success;
    } catch (const std::bad_alloc&) {
        return status::capacity_exceeded;
    } catch (...) {
        return status::invalid_argument;
    }
}

status channel::set_glyph_run_font_sfnt(
    std::uint32_t handle,
    std::uint32_t face_index,
    std::uint32_t style_simulations,
    std::span<const std::byte> font_data) noexcept {
    constexpr std::uint32_t supported_style_simulations = 0x03U;
    constexpr std::size_t maximum_font_bytes = 256U * 1024U * 1024U;
    if (!implementation_->require_resource(handle, type_glyph_run) ||
        !implementation_->glyph_runs.contains(handle)) {
        return status::invalid_handle;
    }
    if (font_data.empty() || font_data.size() > maximum_font_bytes ||
        face_index > std::numeric_limits<std::uint16_t>::max() ||
        (style_simulations & ~supported_style_simulations) != 0U) {
        return status::invalid_argument;
    }
    text::sfnt_font_view font{};
    text::font_error font_error = text::font_error::none;
    if (!text::sfnt_font_view::try_create(
            font_data, face_index, font, &font_error)) {
        return status::invalid_argument;
    }
    try {
        std::shared_ptr<const std::vector<std::byte>> retained_font;
        for (const auto& [other_handle, other] :
             implementation_->glyph_runs) {
            (void)other_handle;
            if (other.font_data &&
                other.font_data->size() == font_data.size() &&
                std::memcmp(
                    other.font_data->data(),
                    font_data.data(),
                    font_data.size()) == 0) {
                retained_font = other.font_data;
                break;
            }
        }
        if (!retained_font) {
            retained_font = std::make_shared<const std::vector<std::byte>>(
                font_data.begin(), font_data.end());
        }
        auto& glyph_run = implementation_->glyph_runs.at(handle);
        glyph_run.face_index = face_index;
        glyph_run.style_simulations = style_simulations;
        glyph_run.font_data = std::move(retained_font);
        implementation_->increment_generation(handle);
        build_cache_.reset();
        return status::success;
    } catch (const std::bad_alloc&) {
        return status::capacity_exceeded;
    } catch (...) {
        return status::invalid_argument;
    }
}

std::size_t channel::resource_count() const noexcept {
    return implementation_->resources.size();
}

bool channel::has_resource(std::uint32_t handle) const noexcept {
    return implementation_->resources.contains(handle);
}

std::uint32_t channel::resource_type(std::uint32_t handle) const noexcept {
    const auto found = implementation_->resources.find(handle);
    return found == implementation_->resources.end() ? 0U : found->second.type;
}

std::uint64_t channel::resource_generation(
    std::uint32_t handle) const noexcept {
    const auto found = implementation_->resources.find(handle);
    return found == implementation_->resources.end()
        ? 0U
        : found->second.generation;
}

bool channel::try_get_visual(
    std::uint32_t handle,
    visual_snapshot& snapshot) const noexcept {
    const auto found = implementation_->visuals.find(handle);
    if (found == implementation_->visuals.end()) {
        return false;
    }
    snapshot = {
        handle,
        found->second.offset_x,
        found->second.offset_y,
        found->second.opacity,
        found->second.content_handle,
        static_cast<std::uint32_t>(found->second.children.size())};
    return true;
}

bool channel::try_get_visual_child(
    std::uint32_t handle,
    std::uint32_t index,
    std::uint32_t& child_handle) const noexcept {
    const auto found = implementation_->visuals.find(handle);
    if (found == implementation_->visuals.end() ||
        index >= found->second.children.size()) {
        return false;
    }
    child_handle = found->second.children[index];
    return true;
}

bool channel::try_get_target(
    std::uint32_t handle,
    target_snapshot& snapshot) const noexcept {
    const auto found = implementation_->targets.find(handle);
    if (found == implementation_->targets.end()) {
        return false;
    }
    snapshot = {
        handle,
        found->second.root_handle,
        found->second.clear_red,
        found->second.clear_green,
        found->second.clear_blue,
        found->second.clear_alpha,
        found->second.flags};
    return true;
}

status channel::build_scene_core(
    const implementation& source,
    std::uint32_t target_handle,
    std::uint64_t scene_id,
    std::uint64_t generation,
    const scene_build_request* request,
    std::vector<std::byte>& stream,
    scene_metrics* metrics,
    scene_build_result* result) const noexcept {
    scene_metrics local_metrics{};
    const auto target = source.targets.find(target_handle);
    if (scene_id == 0U || generation == 0U) {
        return status::invalid_argument;
    }
    if (target == source.targets.end()) {
        return status::invalid_handle;
    }
    scene_build_request legacy_request{};
    scene_compile_context compile_context{
        request == nullptr ? legacy_request : *request,
        request == nullptr
            ? 0U
            : static_cast<std::uint32_t>(
                request->monotonic_time_nanoseconds / 1'000'000U),
        false, 0U, scene_id};
    try {
        native::semantic_scene_builder builder(scene_id, generation);
        std::unordered_map<std::uint32_t, std::uint32_t> brush_indices;
        std::unordered_map<std::uint32_t, std::uint32_t> image_indices;
        struct ordered_external_image final {
            std::uint32_t handle{};
            std::uint32_t width{};
            std::uint32_t height{};
        };
        std::vector<ordered_external_image> ordered_external_images;
        ordered_external_images.reserve(
            source.bitmap_sources.size() + source.media_players.size() +
            source.d3d_images.size());
        for (const auto& [handle, bitmap] : source.bitmap_sources) {
            if (bitmap.external_image) {
                ordered_external_images.push_back(
                    {handle, bitmap.width, bitmap.height});
            }
        }
        for (const auto& [handle, player] : source.media_players) {
            ordered_external_images.push_back(
                {handle, player.width, player.height});
        }
        for (const auto& [handle, image] : source.d3d_images) {
            if (image.has_external_image) {
                ordered_external_images.push_back(
                    {handle, image.width, image.height});
            }
        }
        std::ranges::sort(ordered_external_images, {},
            &ordered_external_image::handle);
        for (const auto& external : ordered_external_images) {
            std::uint32_t image_index = PROGPU_NATIVE_SCENE_NO_INDEX;
            if (!builder.add_external_image(
                    external.width, external.height, image_index)) {
                return status::invalid_graph;
            }
            if (!image_indices.emplace(
                    external.handle, image_index).second) {
                return status::invalid_graph;
            }
        }
        std::unordered_map<std::uint64_t,
            implementation::glyph_scene_resource> glyph_resources;
        std::unordered_set<std::uint32_t> active_visuals;
        std::vector<progpu_native_scene_clip_path> clip_paths;
        std::vector<progpu_native_path_segment> clip_segments;
        std::vector<progpu_native_scene_path_boolean_node>
            clip_boolean_nodes;
        if (target->second.root_handle != 0U &&
            (!target->second.is_window_target ||
             target->second.rendering_enabled)) {
            const status append_status = source.append_visual(
                target->second.root_handle,
                implementation::render_scope_state{},
                1U,
                scene_id,
                builder,
                brush_indices,
                image_indices,
                glyph_resources,
                request == nullptr ? nullptr : &compile_context,
                active_visuals,
                clip_paths,
                clip_segments,
                clip_boolean_nodes,
                local_metrics);
            if (append_status != status::success) {
                return append_status;
            }
        }
        native::scene_build_metrics builder_metrics{};
        std::vector<std::byte> candidate;
        if (!builder.build(candidate, &builder_metrics)) {
            return status::invalid_graph;
        }
        local_metrics.brush_count = builder_metrics.brush_count;
        local_metrics.stream_bytes = builder_metrics.stream_bytes;
        stream = std::move(candidate);
        if (metrics != nullptr) {
            *metrics = local_metrics;
        }
        if (result != nullptr && request != nullptr) {
            result->flags = compile_context.needs_more_cycles
                ? scene_build_result_flags::needs_more_cycles
                : scene_build_result_flags::none;
            result->request_serial = request->request_serial;
            result->stream_bytes = stream.size();
            result->next_due_time_nanoseconds =
                compile_context.needs_more_cycles
                ? request->monotonic_time_nanoseconds <=
                        std::numeric_limits<std::uint64_t>::max() -
                            50'000'000U
                    ? request->monotonic_time_nanoseconds + 50'000'000U
                    : std::numeric_limits<std::uint64_t>::max()
                : 0U;
        }
        return status::success;
    } catch (const std::bad_alloc&) {
        return status::invalid_argument;
    }
}

status channel::build_scene(
    std::uint32_t target_handle,
    std::uint64_t scene_id,
    std::uint64_t generation,
    std::vector<std::byte>& stream,
    scene_metrics* metrics) const noexcept {
    return build_scene_core(
        *implementation_,
        target_handle,
        scene_id,
        generation,
        nullptr,
        stream,
        metrics,
        nullptr);
}

status channel::build_scene(
    const scene_build_request& request,
    std::span<const std::byte>& stream,
    scene_metrics* metrics,
    scene_build_result* result) noexcept {
    stream = {};
    if (!valid_scene_build_request(request)) {
        return status::invalid_argument;
    }
    if (build_cache_ &&
        build_cache_->request.request_serial == request.request_serial &&
        !same_scene_build_request(build_cache_->request, request)) {
        return status::invalid_argument;
    }
    if (!build_cache_ ||
        !same_scene_build_request(build_cache_->request, request)) {
        try {
            auto candidate = std::make_unique<build_cache>();
            const bool has_dynamic_guidelines =
                std::ranges::any_of(
                    implementation_->guideline_sets,
                    [](const auto& entry) {
                        return entry.second.is_dynamic;
                    }) ||
                std::ranges::any_of(
                    implementation_->resources,
                    [](const auto& entry) {
                        return entry.second.has_compact_dynamic_guidelines;
                    });
            std::unique_ptr<implementation> state_candidate;
            const implementation* compile_source = implementation_.get();
            if (has_dynamic_guidelines) {
                state_candidate =
                    std::make_unique<implementation>(*implementation_);
                compile_source = state_candidate.get();
            }
            candidate->request = request;
            const status build_status = build_scene_core(
                *compile_source,
                request.target_handle,
                request.scene_id,
                request.generation,
                &request,
                candidate->stream,
                &candidate->metrics,
                &candidate->result);
            if (build_status != status::success) {
                return build_status;
            }
            if (state_candidate) {
                implementation_ = std::move(state_candidate);
            }
            build_cache_ = std::move(candidate);
        } catch (const std::bad_alloc&) {
            return status::capacity_exceeded;
        }
    }
    stream = build_cache_->stream;
    if (metrics != nullptr) {
        *metrics = build_cache_->metrics;
    }
    if (result != nullptr) {
        *result = build_cache_->result;
    }
    return status::success;
}

} // namespace progpu::native::mil
