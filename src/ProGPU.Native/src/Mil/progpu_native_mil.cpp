#include "progpu_native_mil.hpp"
#include "progpu_native_scene_builder.hpp"
#include "progpu_native_text.hpp"
#include "../Geometry/progpu_native_arc.hpp"

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

namespace progpu::native::mil {
namespace {

constexpr std::uint32_t type_visual = 39U;
constexpr std::uint32_t type_viewport3d_visual = 40U;
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
constexpr std::uint32_t type_dash_style = 84U;
constexpr std::uint32_t type_pen = 85U;
constexpr std::uint32_t type_geometry_drawing = 87U;
constexpr std::uint32_t type_glyph_run_drawing = 88U;
constexpr std::uint32_t type_image_drawing = 89U;
constexpr std::uint32_t type_drawing_group = 91U;
constexpr std::uint32_t type_guideline_set = 92U;
constexpr std::uint32_t type_bitmap_cache = 94U;
constexpr std::uint32_t type_bitmap_source = 95U;
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
    };

    struct solid_brush_state {
        double opacity{1.0};
        progpu_native_color color{};
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

    struct bitmap_source_state {
        std::uint32_t width{};
        std::uint32_t height{};
        std::uint32_t row_bytes{};
        std::vector<std::byte> pixels;
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
        bool is_dynamic{};
        std::vector<double> guidelines_x;
        std::vector<double> guidelines_y;
    };

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
    std::unordered_map<std::uint32_t, target_state> targets;
    std::unordered_map<std::uint32_t, transform_state> transforms;
    std::unordered_map<std::uint32_t, fixed_geometry_state> fixed_geometries;
    std::unordered_map<std::uint32_t, geometry_group_state> geometry_groups;
    std::unordered_map<std::uint32_t, combined_geometry_state>
        combined_geometries;
    std::unordered_map<std::uint32_t, path_geometry_state> path_geometries;
    std::unordered_map<std::uint32_t, solid_brush_state> solid_brushes;
    std::unordered_map<std::uint32_t, gradient_brush_state> gradient_brushes;
    std::unordered_map<std::uint32_t, dash_style_state> dash_styles;
    std::unordered_map<std::uint32_t, pen_state> pens;
    std::unordered_map<std::uint32_t, geometry_drawing_state>
        geometry_drawings;
    std::unordered_map<std::uint32_t, glyph_run_state> glyph_runs;
    std::unordered_map<std::uint32_t, glyph_run_drawing_state>
        glyph_run_drawings;
    std::unordered_map<std::uint32_t, image_drawing_state> image_drawings;
    std::unordered_map<std::uint32_t, bitmap_source_state> bitmap_sources;
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

    bool require_brush(std::uint32_t handle) const noexcept {
        const auto found = resources.find(handle);
        return found != resources.end() &&
            (found->second.type == type_solid_color_brush ||
             found->second.type == type_linear_gradient_brush ||
             found->second.type == type_radial_gradient_brush);
    }

    bool require_effect(std::uint32_t handle) const noexcept {
        const auto found = resources.find(handle);
        return found != resources.end() && is_effect_type(found->second.type) &&
            effects.contains(handle);
    }

    bool has_brush_state(std::uint32_t handle) const noexcept {
        return solid_brushes.contains(handle) ||
            gradient_brushes.contains(handle);
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
                    (brush.opacity_animation_handle == handle ||
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
            visuals.erase(handle);
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
            fixed_geometries.erase(handle);
            geometry_groups.erase(handle);
            combined_geometries.erase(handle);
            path_geometries.erase(handle);
            solid_brushes.erase(handle);
            gradient_brushes.erase(handle);
            dash_styles.erase(handle);
            pens.erase(handle);
            geometry_drawings.erase(handle);
            glyph_run_drawings.erase(handle);
            glyph_runs.erase(handle);
            image_drawings.erase(handle);
            bitmap_sources.erase(handle);
            drawing_images.erase(handle);
            drawing_groups.erase(handle);
            guideline_sets.erase(handle);
            bitmap_caches.erase(handle);
            effects.erase(handle);
            resources.erase(found);
            ++metrics.deleted_resource_count;
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
            if (transform != 0U || relative_transform != 0U) {
                return status::unsupported_command;
            }
            if ((opacity_animations != 0U &&
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

    static bool has_overlapping_translated_equivalent_leaves(
        std::span<const progpu_native_path_segment> segments,
        std::span<const shallow_fill_leaf> leaves) noexcept {
        const auto nearly_equal = [](float first, float second) noexcept {
            const float scale = std::max(
                1.0F,
                std::max(std::abs(first), std::abs(second)));
            return std::abs(first - second) <= 0.00001F * scale;
        };
        const auto translated_point_equal = [
            &nearly_equal](
            progpu_native_point first,
            progpu_native_point second,
            float translation_x,
            float translation_y) noexcept {
            return nearly_equal(
                       first.x + translation_x,
                       second.x) &&
                nearly_equal(first.y + translation_y, second.y);
        };
        const auto invariant_point_equal = [
            &nearly_equal](
            progpu_native_point first,
            progpu_native_point second) noexcept {
            return nearly_equal(first.x, second.x) &&
                nearly_equal(first.y, second.y);
        };
        const auto translated_segment_equal = [
            &translated_point_equal,
            &invariant_point_equal](
            const progpu_native_path_segment& first,
            const progpu_native_path_segment& second,
            float translation_x,
            float translation_y) noexcept {
            if (first.kind != second.kind || first.pad0 != second.pad0 ||
                first.pad1 != second.pad1 || first.pad2 != second.pad2 ||
                !translated_point_equal(
                    first.p0,
                    second.p0,
                    translation_x,
                    translation_y) ||
                !translated_point_equal(
                    first.p1,
                    second.p1,
                    translation_x,
                    translation_y)) {
                return false;
            }
            switch (first.kind) {
            case PROGPU_NATIVE_PATH_SEGMENT_LINE:
                return invariant_point_equal(first.p2, second.p2) &&
                    invariant_point_equal(first.p3, second.p3);
            case PROGPU_NATIVE_PATH_SEGMENT_QUADRATIC:
                return translated_point_equal(
                           first.p2,
                           second.p2,
                           translation_x,
                           translation_y) &&
                    invariant_point_equal(first.p3, second.p3);
            case PROGPU_NATIVE_PATH_SEGMENT_CUBIC:
                return translated_point_equal(
                           first.p2,
                           second.p2,
                           translation_x,
                           translation_y) &&
                    translated_point_equal(
                        first.p3,
                        second.p3,
                        translation_x,
                        translation_y);
            case PROGPU_NATIVE_PATH_SEGMENT_ARC:
                return translated_point_equal(
                           first.p2,
                           second.p2,
                           translation_x,
                           translation_y) &&
                    invariant_point_equal(first.p3, second.p3);
            default:
                return false;
            }
        };

        for (std::size_t first_index = 0U;
             first_index < leaves.size();
             ++first_index) {
            const auto& first = leaves[first_index];
            if (first.segment_count == 0U ||
                first.segment_offset > segments.size() ||
                first.segment_count >
                    segments.size() - first.segment_offset) {
                continue;
            }
            for (std::size_t second_index = first_index + 1U;
                 second_index < leaves.size();
                 ++second_index) {
                const auto& second = leaves[second_index];
                if (first.segment_count != second.segment_count ||
                    second.segment_offset > segments.size() ||
                    second.segment_count >
                        segments.size() - second.segment_offset ||
                    std::max(first.left, second.left) >=
                        std::min(first.right, second.right) ||
                    std::max(first.top, second.top) >=
                        std::min(first.bottom, second.bottom)) {
                    continue;
                }
                const auto& first_segment =
                    segments[first.segment_offset];
                const auto& second_segment =
                    segments[second.segment_offset];
                const float translation_x =
                    second_segment.p0.x - first_segment.p0.x;
                const float translation_y =
                    second_segment.p0.y - first_segment.p0.y;
                if (nearly_equal(translation_x, 0.0F) &&
                    nearly_equal(translation_y, 0.0F)) {
                    continue;
                }
                bool equivalent = true;
                for (std::size_t segment_index = 0U;
                     segment_index < first.segment_count;
                     ++segment_index) {
                    if (!translated_segment_equal(
                            segments[first.segment_offset + segment_index],
                            segments[second.segment_offset + segment_index],
                            translation_x,
                            translation_y)) {
                        equivalent = false;
                        break;
                    }
                }
                if (equivalent) {
                    return true;
                }
            }
        }
        return false;
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
            if (transform_is_identity) {
                segments.insert(
                    segments.end(),
                    source_segments.begin(),
                    source_segments.end());
                leaf.left = path->second.left;
                leaf.top = path->second.top;
                leaf.right = path->second.right;
                leaf.bottom = path->second.bottom;
            } else {
                const std::size_t original_size = segments.size();
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
                progpu_native_image_rect bounds{};
                if (!try_transform_bounds(
                        path->second.left,
                        path->second.top,
                        path->second.right - path->second.left,
                        path->second.bottom - path->second.top,
                        transform,
                        bounds)) {
                    segments.resize(original_size);
                    return status::invalid_graph;
                }
                leaf.left = bounds.x;
                leaf.top = bounds.y;
                leaf.right = bounds.x + bounds.width;
                leaf.bottom = bounds.y + bounds.height;
            }
            leaf.segment_count = path->second.segments.size();
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
        const double bounds_x = resolved_fixed.kind ==
                fixed_geometry_kind::ellipse
            ? resolved_fixed.first - resolved_fixed.third
            : resolved_fixed.first;
        const double bounds_y = resolved_fixed.kind ==
                fixed_geometry_kind::ellipse
            ? resolved_fixed.second - resolved_fixed.fourth
            : resolved_fixed.second;
        const double bounds_width = resolved_fixed.kind ==
                fixed_geometry_kind::ellipse
            ? resolved_fixed.third * 2.0
            : resolved_fixed.third;
        const double bounds_height = resolved_fixed.kind ==
                fixed_geometry_kind::ellipse
            ? resolved_fixed.fourth * 2.0
            : resolved_fixed.fourth;
        if (!try_transform_bounds(
                bounds_x,
                bounds_y,
                bounds_width,
                bounds_height,
                transform,
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

    status append_boolean_geometry(
        std::uint32_t geometry_handle,
        std::vector<progpu_native_path_segment>& segments,
        std::vector<progpu_native_scene_path_boolean_node>& nodes,
        shallow_fill_leaf& tree,
        affine_2d_double parent_transform = {},
        std::uint32_t depth = 1U) const {
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

        const auto combined = combined_geometries.find(geometry_handle);
        if (combined == combined_geometries.end()) {
            const status leaf_status = append_group_fill_leaf(
                geometry_handle,
                segments,
                tree,
                parent_transform,
                depth);
            if (leaf_status != status::success) {
                segments.resize(original_segment_size);
                nodes.resize(original_node_size);
                return leaf_status;
            }
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
                depth + 1U);
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

    status append_render_data(
        std::uint32_t content_handle,
        const render_scope_state& base_state,
        native::semantic_scene_builder& builder,
        std::unordered_map<std::uint32_t, std::uint32_t>& brush_indices,
        std::unordered_map<std::uint32_t, std::uint32_t>& image_indices,
        std::unordered_map<std::uint64_t, glyph_scene_resource>&
            glyph_resources,
        scene_metrics& metrics) const {
        const auto resource = resources.find(content_handle);
        if (resource == resources.end() ||
            resource->second.type != type_render_data) {
            return status::invalid_handle;
        }

        std::unordered_set<std::uint32_t> active_drawings;
        std::vector<progpu_native_scene_clip_path> clip_paths;
        std::vector<progpu_native_path_segment> clip_segments;
        std::vector<progpu_native_scene_path_boolean_node>
            clip_boolean_nodes;
        return append_render_stream(
            resource->second.render_data,
            base_state,
            1U,
            builder,
            brush_indices,
            image_indices,
            glyph_resources,
            active_drawings,
            clip_paths,
            clip_segments,
            clip_boolean_nodes,
            metrics);
    }

    status append_render_stream(
        std::span<const std::byte> bytes,
        render_scope_state current,
        std::uint32_t drawing_depth,
        native::semantic_scene_builder& builder,
        std::unordered_map<std::uint32_t, std::uint32_t>& brush_indices,
        std::unordered_map<std::uint32_t, std::uint32_t>& image_indices,
        std::unordered_map<std::uint64_t, glyph_scene_resource>&
            glyph_resources,
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
            } else if (group != geometry_groups.end() &&
                group->second.fill_rule == 0U &&
                group->second.children.size() > 1U &&
                group->second.children.size() <= 32U) {
                affine_2d_double group_transform = target_transform;
                if (group->second.transform_handle != 0U) {
                    affine_2d_double local_transform{};
                    const status transform_status = resolve_transform(
                        group->second.transform_handle,
                        local_transform);
                    if (transform_status != status::success) {
                        return transform_status;
                    }
                    group_transform = compose_affine(
                        local_transform,
                        target_transform);
                }
                std::vector<shallow_fill_leaf> leaves;
                leaves.reserve(group->second.children.size());
                for (const std::uint32_t child_handle :
                     group->second.children) {
                    shallow_fill_leaf child{};
                    append_status = append_group_fill_leaf(
                        child_handle,
                        clip_segments,
                        child,
                        group_transform);
                    if (append_status != status::success) {
                        break;
                    }
                    if (!child.has_bounds) {
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
                    if (child.segment_count != 0U) {
                        leaves.push_back(child);
                    }
                }
                if (append_status == status::success &&
                    has_overlapping_translated_equivalent_leaves(
                        clip_segments,
                        leaves)) {
                    append_status = status::unsupported_command;
                }
                if (append_status == status::success) {
                    for (std::size_t leaf_index = 0U;
                         leaf_index < leaves.size();
                         ++leaf_index) {
                        const auto& leaf = leaves[leaf_index];
                        clip_boolean_nodes.push_back({
                            leaf.segment_offset,
                            leaf.segment_count,
                            static_cast<float>(leaf.left),
                            static_cast<float>(leaf.top),
                            static_cast<float>(leaf.right),
                            static_cast<float>(leaf.bottom),
                            PROGPU_NATIVE_FILL_RULE_EVEN_ODD,
                            PROGPU_NATIVE_PATH_BOOLEAN_LEAF,
                            0U,
                            0U});
                        if (leaf_index != 0U) {
                            progpu_native_scene_path_boolean_node operation{};
                            operation.kind = PROGPU_NATIVE_PATH_BOOLEAN_XOR;
                            clip_boolean_nodes.push_back(operation);
                        }
                    }
                    tree.segment_offset = segment_offset;
                    tree.segment_count =
                        clip_segments.size() - segment_offset;
                    fill_rule = PROGPU_NATIVE_FILL_RULE_EVEN_ODD;
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
                boolean_node_offset,
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
                clip_boolean_nodes.size(),
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
            std::uint32_t end_cap) noexcept {
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
            bool& emitted) noexcept {
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
            const double half_thickness = pen.thickness * 0.5;
            const double left = double{point.x} -
                (start_cap == PROGPU_NATIVE_STROKE_CAP_FLAT
                    ? 0.0
                    : half_thickness);
            const double right = double{point.x} +
                (end_cap == PROGPU_NATIVE_STROKE_CAP_FLAT
                    ? 0.0
                    : half_thickness);
            progpu_native_image_rect stroke_bounds{};
            if (!try_transform_bounds(
                    left,
                    double{point.y} - half_thickness,
                    right - left,
                    pen.thickness,
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
        const auto append_line_stroke = [
            this,
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
                std::uint32_t brush_index = PROGPU_NATIVE_SCENE_NO_INDEX;
                const status brush_status = resolve_brush_index(
                    pen.brush_handle,
                    brush_index);
                if (brush_status != status::success) {
                    return brush_status;
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
                    pen.end_line_cap);
                if (stroke_status != status::success) {
                    return stroke_status;
                }
            }
            ++metrics.line_count;
            return status::success;
        };
        const auto append_degenerate_ellipse_stroke = [
            this,
            &builder,
            &resolve_brush_index,
            &append_degenerate_cap_stroke,
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
                std::uint32_t brush_index = PROGPU_NATIVE_SCENE_NO_INDEX;
                const status brush_status = resolve_brush_index(
                    pen.brush_handle,
                    brush_index);
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
            if (pen.dash_style_handle != 0U) {
                const auto dash = dash_styles.find(pen.dash_style_handle);
                if (dash == dash_styles.end()) {
                    return status::invalid_handle;
                }
                if (!dash->second.intervals.empty()) {
                    return status::unsupported_command;
                }
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
            if (!try_transform_bounds(
                    center_x - radius_x - half_thickness,
                    center_y - radius_y - half_thickness,
                    radius_x * 2.0 + pen.thickness,
                    radius_y * 2.0 + pen.thickness,
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
            &current](
            const path_geometry_state& geometry,
            const pen_state& pen,
            const affine_2d_double& local_transform,
            const affine_2d_double& effective_transform) noexcept {
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
            std::uint32_t brush_index = PROGPU_NATIVE_SCENE_NO_INDEX;
            const status brush_status = resolve_brush_index(
                pen.brush_handle,
                brush_index,
                &brush_use);
            if (brush_status != status::success) {
                return brush_status;
            }
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
                    if (!contour.closed && !append_cap(
                            contour.segments.front(),
                            start_cap,
                            true)) {
                        return status::unsupported_command;
                    }
                    for (std::size_t segment_index = 0U;
                         segment_index < contour.segments.size();
                         ++segment_index) {
                        if (segment_index != 0U &&
                            !append_join(
                                contour.segments[segment_index - 1U],
                                contour.segments[segment_index],
                                contour.smooth_joins[segment_index - 1U] != 0U)) {
                            return status::unsupported_command;
                        }
                        progpu_native_geometry_primitive primitive{};
                        if (!make_primitive(
                                contour.segments[segment_index],
                                primitive)) {
                            return status::invalid_graph;
                        }
                        primitives.push_back(primitive);
                        brushes.push_back(brush_index);
                    }
                    if (contour.closed && !append_join(
                            contour.segments.back(),
                            contour.segments.front(),
                            contour.smooth_joins.back() != 0U)) {
                        return status::unsupported_command;
                    }
                    if (!contour.closed && !append_cap(
                            contour.segments.back(),
                            end_cap,
                            false)) {
                        return status::unsupported_command;
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
                        : pen.end_line_cap);
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
        const auto append_degenerate_rectangle_stroke = [
            this,
            &builder,
            &resolve_brush_index,
            &append_rounded_rectangle_path,
            &current](
            double x,
            double y,
            double width,
            double height,
            double radius,
            const pen_state& pen,
            const affine_2d_double& local_transform,
            const affine_2d_double& effective_transform) noexcept {
            if (pen.brush_handle == 0U || pen.thickness == 0.0) {
                return status::success;
            }
            if (pen.dash_style_handle != 0U) {
                const auto dash = dash_styles.find(pen.dash_style_handle);
                if (dash == dash_styles.end()) {
                    return status::invalid_handle;
                }
                if (!dash->second.intervals.empty()) {
                    return status::unsupported_command;
                }
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
            if (!try_transform_bounds(
                    left,
                    top,
                    right - left,
                    bottom - top,
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
            if (radius > 0.0 ||
                pen.line_join == PROGPU_NATIVE_STROKE_JOIN_ROUND) {
                const double outer_radius = radius + half_thickness;
                const double radius_x = std::min(
                    outer_radius, (right - left) * 0.5);
                const double radius_y = std::min(
                    outer_radius, (bottom - top) * 0.5);
                append_rounded_rectangle_path(
                    segments,
                    left,
                    top,
                    right,
                    bottom,
                    radius_x,
                    radius_y);
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
            if (!source.has_bounds) {
                return status::unsupported_command;
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
                width / source.bounds_width,
                0.0,
                0.0,
                height / source.bounds_height,
                x - source.bounds_x * width / source.bounds_width,
                y - source.bounds_y * height / source.bounds_height};
            next.transform = compose_affine(mapping, state.transform);
            if (!save_state(next)) {
                active_drawings.erase(image_source_handle);
                return status::invalid_graph;
            }
            const status image_status = append_render_stream(
                source.child_render_data,
                next,
                drawing_depth + 1U,
                builder,
                brush_indices,
                image_indices,
                glyph_resources,
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
                        : PROGPU_NATIVE_SCENE_LAYER_FORCE_ISOLATION,
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
                        : PROGPU_NATIVE_SCENE_LAYER_FORCE_ISOLATION,
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
                        return status::unsupported_command;
                    }
                    const status guideline_status =
                        apply_static_guidelines(
                            guidelines->second.guidelines_x,
                            guidelines->second.guidelines_y,
                            next,
                            builder,
                            false);
                    if (guideline_status != status::success) {
                        return guideline_status;
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
                            active_drawings.erase(drawing_handle);
                            return status::unsupported_command;
                        }
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
                        next,
                        drawing_depth + 1U,
                        builder,
                        brush_indices,
                        image_indices,
                        glyph_resources,
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
                if (geometry_group != geometry_groups.end()) {
                    const bool has_zero_area =
                        affine_has_zero_area(effective_transform);
                    if (pen_handle != 0U) {
                        pen_state pen{};
                        const status pen_status = resolve_pen(
                            pen_handle, pen);
                        if (pen_status != status::success) {
                            return pen_status;
                        }
                        if (!has_zero_area &&
                            pen.brush_handle != 0U &&
                            pen.thickness > 0.0) {
                            return status::unsupported_command;
                        }
                    }
                    if (has_zero_area) {
                        continue;
                    }
                    if (brush_handle == 0U ||
                        geometry_group->second.children.empty()) {
                        continue;
                    }
                    std::vector<progpu_native_path_segment> group_segments;
                    std::vector<shallow_fill_leaf> group_leaves;
                    group_leaves.reserve(
                        geometry_group->second.children.size());
                    bool has_group_bounds = false;
                    double group_left = 0.0;
                    double group_top = 0.0;
                    double group_right = 0.0;
                    double group_bottom = 0.0;
                    const auto include_group_point = [
                        &has_group_bounds,
                        &group_left,
                        &group_top,
                        &group_right,
                        &group_bottom](progpu_native_point point) noexcept {
                        if (!has_group_bounds) {
                            group_left = point.x;
                            group_top = point.y;
                            group_right = point.x;
                            group_bottom = point.y;
                            has_group_bounds = true;
                            return;
                        }
                        group_left = std::min(group_left, double{point.x});
                        group_top = std::min(group_top, double{point.y});
                        group_right = std::max(group_right, double{point.x});
                        group_bottom = std::max(group_bottom, double{point.y});
                    };
                    for (const std::uint32_t child_handle :
                         geometry_group->second.children) {
                        shallow_fill_leaf child{};
                        const status child_status = append_group_fill_leaf(
                            child_handle,
                            group_segments,
                            child,
                            {},
                            1U,
                            current.per_point_guidelines);
                        if (child_status != status::success) {
                            return child_status;
                        }
                        if (child.has_bounds) {
                            if (child.segment_count != 0U) {
                                group_leaves.push_back(child);
                            }
                            include_group_point({
                                static_cast<float>(child.left),
                                static_cast<float>(child.top)});
                            include_group_point({
                                static_cast<float>(child.right),
                                static_cast<float>(child.bottom)});
                        }
                    }
                    if (group_segments.empty() || !has_group_bounds) {
                        continue;
                    }
                    std::vector<progpu_native_scene_path_boolean_node>
                        group_boolean_nodes;
                    const bool use_even_odd_leaf_program =
                        geometry_group->second.fill_rule == 0U &&
                        group_leaves.size() > 1U &&
                        group_leaves.size() <= 32U;
                    if (use_even_odd_leaf_program) {
                        if (has_overlapping_translated_equivalent_leaves(
                                group_segments,
                                group_leaves)) {
                            return status::unsupported_command;
                        }
                        group_boolean_nodes.reserve(
                            group_leaves.size() * 2U - 1U);
                        for (std::size_t leaf_index = 0U;
                             leaf_index < group_leaves.size();
                             ++leaf_index) {
                            const auto& leaf = group_leaves[leaf_index];
                            group_boolean_nodes.push_back({
                                leaf.segment_offset,
                                leaf.segment_count,
                                static_cast<float>(leaf.left),
                                static_cast<float>(leaf.top),
                                static_cast<float>(leaf.right),
                                static_cast<float>(leaf.bottom),
                                PROGPU_NATIVE_FILL_RULE_EVEN_ODD,
                                PROGPU_NATIVE_PATH_BOOLEAN_LEAF,
                                0U,
                                0U});
                            if (leaf_index != 0U) {
                                progpu_native_scene_path_boolean_node
                                    operation{};
                                operation.kind =
                                    PROGPU_NATIVE_PATH_BOOLEAN_XOR;
                                group_boolean_nodes.push_back(operation);
                            }
                        }
                    }
                    std::uint32_t brush_index =
                        PROGPU_NATIVE_SCENE_NO_INDEX;
                    const brush_use_state brush_use{
                        group_left,
                        group_top,
                        group_right - group_left,
                        group_bottom - group_top,
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
                    continue;
                }
                if (combined_geometry != combined_geometries.end()) {
                    const bool has_zero_area =
                        affine_has_zero_area(effective_transform);
                    if (pen_handle != 0U) {
                        pen_state pen{};
                        const status pen_status = resolve_pen(
                            pen_handle, pen);
                        if (pen_status != status::success) {
                            return pen_status;
                        }
                        if (!has_zero_area &&
                            pen.brush_handle != 0U &&
                            pen.thickness > 0.0) {
                            return status::unsupported_command;
                        }
                    }
                    if (has_zero_area) {
                        continue;
                    }
                    if (brush_handle == 0U) {
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
                    if (brush_handle != 0U &&
                        !fill_segments.empty()) {
                        std::uint32_t brush_index =
                            PROGPU_NATIVE_SCENE_NO_INDEX;
                        const brush_use_state brush_use{
                            path_geometry->second.left,
                            path_geometry->second.top,
                            path_geometry->second.right -
                                path_geometry->second.left,
                            path_geometry->second.bottom -
                                path_geometry->second.top,
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
                                path_geometry->second.left,
                                path_geometry->second.top,
                                path_geometry->second.right -
                                    path_geometry->second.left,
                                path_geometry->second.bottom -
                                    path_geometry->second.top,
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
                                static_cast<float>(path_geometry->second.left),
                                static_cast<float>(path_geometry->second.top),
                                static_cast<float>(path_geometry->second.right),
                                static_cast<float>(path_geometry->second.bottom),
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
                        const status stroke_status = append_path_strokes(
                            path_geometry->second,
                            pen,
                            local_transform,
                            effective_transform);
                        if (stroke_status != status::success) {
                            return stroke_status;
                        }
                    }
                    continue;
                }
                if (resolved_geometry.kind == fixed_geometry_kind::line) {
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
            if (is_rounded && radius_x != radius_y &&
                (radius_x == 0.0 || radius_y == 0.0) &&
                (width == 0.0 || height == 0.0)) {
                return status::unsupported_command;
            }
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
            if (brush_handle != 0U && width > 0.0 && height > 0.0) {
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
                    if (width == 0.0 || height == 0.0) {
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
                                  pen,
                                  local_transform,
                                  effective_transform);
                        if (stroke_status != status::success) {
                            return stroke_status;
                        }
                    } else {
                        const double half_thickness =
                            pen.thickness * 0.5;
                        const brush_use_state brush_use{
                            x - half_thickness,
                            y - half_thickness,
                            width + pen.thickness,
                            height + pen.thickness,
                            effective_transform};
                        std::uint32_t pen_brush_index =
                            PROGPU_NATIVE_SCENE_NO_INDEX;
                        const status brush_status = resolve_brush_index(
                            pen.brush_handle,
                            pen_brush_index,
                            &brush_use);
                        if (brush_status != status::success) {
                            return brush_status;
                        }
                        progpu_native_image_rect stroke_bounds{};
                        if (!try_transform_bounds(
                                x - half_thickness,
                                y - half_thickness,
                                width + pen.thickness,
                                height + pen.thickness,
                                effective_transform,
                                stroke_bounds)) {
                            return status::invalid_graph;
                        }
                        if (is_ellipse) {
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
                            const std::array primitive{
                                progpu_native_geometry_primitive{
                                    PROGPU_NATIVE_GEOMETRY_ARC,
                                    current.edge_aliased
                                        ? static_cast<std::uint32_t>(
                                            PROGPU_NATIVE_PRIMITIVE_FLAG_EDGE_ALIASED)
                                        : 0U,
                                    {static_cast<float>(first),
                                        static_cast<float>(second)},
                                    {static_cast<float>(third), 0.0F},
                                    {0.0F, static_cast<float>(fourth)},
                                    {0.0F,
                                        std::numbers::pi_v<float> * 2.0F},
                                    static_cast<float>(
                                        pen.thickness),
                                    0.0F,
                                    {1.0F, 1.0F, 1.0F, 1.0F},
                                    native_local_transform}};
                            const std::array brushes{pen_brush_index};
                            if (!builder.draw_geometry(
                                    primitive,
                                    brushes,
                                    stroke_bounds)) {
                                return status::invalid_graph;
                            }
                        } else if (has_rounded_corners) {
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
                            if (radius_x != radius_y) {
                                const auto rounded_geometry =
                                    make_rounded_rectangle_geometry(
                                        x,
                                        y,
                                        width,
                                        height,
                                        radius_x,
                                        radius_y);
                                const status stroke_status =
                                    append_path_strokes(
                                        rounded_geometry,
                                        pen,
                                        local_transform,
                                        effective_transform);
                                if (stroke_status != status::success) {
                                    return stroke_status;
                                }
                            } else {
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
                                        static_cast<float>(radius_x),
                                        static_cast<float>(
                                            pen.thickness),
                                        {1.0F, 1.0F, 1.0F, 1.0F},
                                        native_local_transform}};
                                const std::array brushes{pen_brush_index};
                                if (!builder.draw_analytic(
                                        primitive,
                                        brushes,
                                        stroke_bounds)) {
                                    return status::invalid_graph;
                                }
                            }
                        } else {
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
                            const status stroke_status =
                                append_polyline_stroke(
                                    pen,
                                    points,
                                    true,
                                    pen_brush_index,
                                    stroke_bounds,
                                    native_local_transform,
                                    pen.start_line_cap,
                                    pen.end_line_cap);
                            if (stroke_status != status::success) {
                                return stroke_status;
                            }
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
        const auto color_at = [&working](
            float position,
            bool prefer_first_exact) noexcept {
            auto first_exact = working.end();
            auto last_exact = working.end();
            for (auto current = working.begin();
                 current != working.end();
                 ++current) {
                const float current_position =
                    static_cast<float>(current->position);
                if (current_position == position) {
                    if (first_exact == working.end()) {
                        first_exact = current;
                    }
                    last_exact = current;
                }
            }
            if (first_exact != working.end()) {
                return (prefer_first_exact
                    ? first_exact
                    : last_exact)->color;
            }
            if (working.front().position > position) {
                return working.front().color;
            }
            if (working.back().position < position) {
                return working.back().color;
            }
            for (std::size_t index = 1U;
                 index < working.size();
                 ++index) {
                const float right = static_cast<float>(
                    working[index].position);
                if (right > position) {
                    const float left = static_cast<float>(
                        working[index - 1U].position);
                    const float factor =
                        (position - left) / (right - left);
                    return interpolate_color(
                        working[index - 1U].color,
                        working[index].color,
                        factor);
                }
            }
            return working.back().color;
        };
        const progpu_native_color first_color = color_at(0.0F, false);
        const progpu_native_color last_color = color_at(1.0F, true);
        try {
            stops.reserve(working.size() + 2U);
            stops.push_back({first_color, 0.0F, 0U, 0U, 0U});
            for (const auto& stop : working) {
                const float position = static_cast<float>(stop.position);
                if (position > 0.0F && position < 1.0F) {
                    stops.push_back({stop.color, position, 0U, 0U, 0U});
                }
            }
            stops.push_back({last_color, 1.0F, 0U, 0U, 0U});
        } catch (const std::bad_alloc&) {
            return status::capacity_exceeded;
        }
        if (source.color_interpolation_mode == 0U) {
            for (auto& stop : stops) {
                stop.color = sc_rgb_to_s_rgb(stop.color);
            }
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
        if (state.mask_resource_index != PROGPU_NATIVE_SCENE_NO_INDEX) {
            return status::unsupported_command;
        }
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
            if (!state.has_clip) {
                return status::success;
            }
            auto composite_state =
                native::semantic_scene_builder::identity_state();
            composite_state.flags = PROGPU_NATIVE_SCENE_STATE_CLIP_RECT;
            composite_state.clip_rect = state.clip_rect;
            std::uint32_t composite_state_index =
                PROGPU_NATIVE_SCENE_NO_INDEX;
            if (!builder.add_state(
                    composite_state, composite_state_index)) {
                return status::invalid_graph;
            }
            layer.flags |= PROGPU_NATIVE_SCENE_LAYER_COMPOSITE_STATE;
            layer.reserved0 = composite_state_index;
            return status::success;
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
                if (!state.has_clip && !isolate_source_composite) {
                    return status::success;
                }
                if (state.has_clip) {
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
        layer.mask_resource_index = PROGPU_NATIVE_SCENE_NO_INDEX;
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
        if (!active_resources.insert(handle).second) {
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
        if (is_transform_type(resource->second.type)) {
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
                append_if_success(brush->second.opacity_animation_handle);
                append_if_success(brush->second.color_animation_handle);
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
        if (!active_visuals.insert(handle).second) {
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
        std::uint32_t& mask_resource_index) const {
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
        return builder.add_brush_mask(mask, stops, mask_resource_index)
            ? status::success
            : status::invalid_graph;
    }

    status add_visual_opacity_mask(
        std::uint32_t brush_handle,
        const visual_state& visual,
        const affine_2d_double& mask_transform,
        native::semantic_scene_builder& builder,
        std::uint32_t& mask_resource_index) const {
        return add_gradient_opacity_mask(
            brush_handle,
            visual.cache_bounds_x,
            visual.cache_bounds_y,
            visual.cache_bounds_width,
            visual.cache_bounds_height,
            mask_transform,
            builder,
            mask_resource_index);
    }

    status add_visual_cache_layer(
        std::uint32_t cache_handle,
        std::uint32_t visual_handle,
        std::uint64_t scene_id,
        const render_scope_state& state,
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
        // content. Arbitrary inherited semantic masks remain fail closed.
        // Fant/HighQuality sampling is retained as composite-only state.
        if (state.mask_resource_index != PROGPU_NATIVE_SCENE_NO_INDEX ||
            (state.image_sampling != PROGPU_NATIVE_IMAGE_SAMPLING_LINEAR &&
                state.image_sampling != PROGPU_NATIVE_IMAGE_SAMPLING_NEAREST &&
                state.image_sampling != PROGPU_NATIVE_IMAGE_SAMPLING_FANT)) {
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
            PROGPU_NATIVE_SCENE_NO_INDEX;
        if (has_spatial_opacity_mask) {
            const status opacity_mask_status = add_visual_opacity_mask(
                cache_visual.alpha_mask_handle,
                cache_visual,
                mask_transform,
                builder,
                opacity_mask_resource_index);
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
        std::unordered_set<std::uint32_t>& active_visuals,
        scene_metrics& metrics) const {
        if (depth == 0U || depth > maximum_visual_depth ||
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
            const status clip_status = apply_visual_rectangle_clip(
                visual->second.clip_geometry_handle,
                current);
            if (clip_status != status::success) {
                active_visuals.erase(handle);
                return clip_status;
            }
        }

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
        if (current.mask_resource_index !=
            PROGPU_NATIVE_SCENE_NO_INDEX) {
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
        const status effect_status = add_visual_effect_layer(
            handle,
            visual->second.effect_handle,
            current,
            visual->second.cache_mode_handle != 0U,
            builder,
            effect_layer_count);
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
        const status cache_status = add_visual_cache_layer(
            visual->second.cache_mode_handle,
            handle,
            scene_id,
            current,
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

        ++metrics.visual_count;
        metrics.maximum_visual_depth =
            std::max(metrics.maximum_visual_depth, depth);
        status result = status::success;
        if (!skip_cached_content && visual->second.content_handle != 0U) {
            result = append_render_data(
                visual->second.content_handle,
                content_scope,
                builder,
                brush_indices,
                image_indices,
                glyph_resources,
                metrics);
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
                    active_visuals,
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

status channel::set_bitmap_source_rgba8(
    std::uint32_t handle,
    std::uint32_t width,
    std::uint32_t height,
    std::uint32_t row_bytes,
    std::span<const std::byte> pixels) noexcept {
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
        source.row_bytes = row_bytes;
        source.pixels.assign(pixels.begin(), pixels.end());
        implementation_->bitmap_sources.insert_or_assign(
            handle, std::move(source));
        implementation_->increment_generation(handle);
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
    return status::success;
}

status channel::set_visual_cache_bounds(
    std::uint32_t handle,
    double x,
    double y,
    double width,
    double height) noexcept {
    if (!implementation_->require_resource(handle, type_visual) ||
        !implementation_->visuals.contains(handle)) {
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
    return status::success;
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

status channel::build_scene(
    std::uint32_t target_handle,
    std::uint64_t scene_id,
    std::uint64_t generation,
    std::vector<std::byte>& stream,
    scene_metrics* metrics) const noexcept {
    scene_metrics local_metrics{};
    const auto target = implementation_->targets.find(target_handle);
    if (scene_id == 0U || generation == 0U) {
        return status::invalid_argument;
    }
    if (target == implementation_->targets.end()) {
        return status::invalid_handle;
    }
    try {
        native::semantic_scene_builder builder(scene_id, generation);
        std::unordered_map<std::uint32_t, std::uint32_t> brush_indices;
        std::unordered_map<std::uint32_t, std::uint32_t> image_indices;
        std::unordered_map<std::uint64_t,
            implementation::glyph_scene_resource> glyph_resources;
        std::unordered_set<std::uint32_t> active_visuals;
        if (target->second.root_handle != 0U) {
            const status append_status = implementation_->append_visual(
                target->second.root_handle,
                implementation::render_scope_state{},
                1U,
                scene_id,
                builder,
                brush_indices,
                image_indices,
                glyph_resources,
                active_visuals,
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
        return status::success;
    } catch (const std::bad_alloc&) {
        return status::invalid_argument;
    }
}

} // namespace progpu::native::mil
