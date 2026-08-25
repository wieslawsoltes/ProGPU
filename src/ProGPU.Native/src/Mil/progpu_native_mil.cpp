#include "progpu_native_mil.hpp"
#include "progpu_native_scene_builder.hpp"
#include "../Geometry/progpu_native_arc.hpp"

#include <algorithm>
#include <array>
#include <bit>
#include <cmath>
#include <cstring>
#include <limits>
#include <new>
#include <numbers>
#include <unordered_map>
#include <unordered_set>
#include <utility>
#include <vector>

namespace progpu::native::mil {
namespace {

constexpr std::uint32_t type_visual = 39U;
constexpr std::uint32_t type_viewport3d_visual = 40U;
constexpr std::uint32_t type_render_data = 43U;
constexpr std::uint32_t type_render_target = 45U;
constexpr std::uint32_t type_hwnd_render_target = 46U;
constexpr std::uint32_t type_generic_render_target = 47U;
constexpr std::uint32_t type_matrix_transform = 66U;
constexpr std::uint32_t type_line_geometry = 68U;
constexpr std::uint32_t type_rectangle_geometry = 69U;
constexpr std::uint32_t type_ellipse_geometry = 70U;
constexpr std::uint32_t type_geometry_group = 71U;
constexpr std::uint32_t type_combined_geometry = 72U;
constexpr std::uint32_t type_path_geometry = 73U;
constexpr std::uint32_t type_solid_color_brush = 75U;
constexpr std::uint32_t type_dash_style = 84U;
constexpr std::uint32_t type_pen = 85U;
constexpr std::uint32_t type_last = 98U;
constexpr std::uint32_t maximum_visual_depth = 256U;
constexpr std::uint32_t maximum_path_record_count = 1U << 20U;

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

bool is_visual_type(std::uint32_t type) noexcept {
    return type == type_visual || type == type_viewport3d_visual;
}

bool is_target_type(std::uint32_t type) noexcept {
    return type == type_render_target || type == type_hwnd_render_target ||
        type == type_generic_render_target;
}

bool finite_double_as_float(double value) noexcept {
    constexpr auto maximum =
        static_cast<double>(std::numeric_limits<float>::max());
    return std::isfinite(value) && value >= -maximum && value <= maximum;
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
        std::uint32_t content_handle{};
        std::vector<std::uint32_t> children;
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
    };

    struct pen_state {
        double thickness{};
        double miter_limit{10.0};
        std::uint32_t brush_handle{};
        std::uint32_t start_line_cap{};
        std::uint32_t end_line_cap{};
        std::uint32_t dash_cap{};
        std::uint32_t line_join{};
        std::uint32_t dash_style_handle{};
    };

    struct dash_style_state {
        double offset{};
        std::vector<double> intervals;
    };

    using matrix_transform_state = affine_2d_double;

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
    };

    struct path_stroke_contour_state {
        std::vector<progpu_native_point> points;
        std::vector<progpu_native_path_segment> segments;
        std::vector<std::uint8_t> smooth_joins;
        bool closed{};
        bool start_uses_dash_cap{};
        bool end_uses_dash_cap{};
        bool crosses_closed_figure_start{};
    };

    struct path_geometry_state {
        std::vector<progpu_native_path_segment> segments;
        std::vector<path_stroke_contour_state> stroke_contours;
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
    std::unordered_map<std::uint32_t, visual_state> visuals;
    std::unordered_map<std::uint32_t, target_state> targets;
    std::unordered_map<std::uint32_t, matrix_transform_state>
        matrix_transforms;
    std::unordered_map<std::uint32_t, fixed_geometry_state> fixed_geometries;
    std::unordered_map<std::uint32_t, geometry_group_state> geometry_groups;
    std::unordered_map<std::uint32_t, combined_geometry_state>
        combined_geometries;
    std::unordered_map<std::uint32_t, path_geometry_state> path_geometries;
    std::unordered_map<std::uint32_t, solid_brush_state> solid_brushes;
    std::unordered_map<std::uint32_t, dash_style_state> dash_styles;
    std::unordered_map<std::uint32_t, pen_state> pens;

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
            return has_exact_size(view, 4U)
                ? status::success
                : status::malformed_batch;
        case command::channel_create_resource: {
            std::uint32_t type = 0U;
            if (!has_exact_size(view, 12U) ||
                !read_at(view.packet, 4U, handle) ||
                !read_at(view.packet, 8U, type) || handle == 0U) {
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
            std::uint32_t type = 0U;
            if (!has_exact_size(view, 12U) ||
                !read_at(view.packet, 4U, handle) ||
                !read_at(view.packet, 8U, type)) {
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
                     visual.content_handle == handle ||
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
                     pen.dash_style_handle == handle)) {
                    return status::invalid_graph;
                }
            }
            for (const auto& [geometry_handle, geometry] : fixed_geometries) {
                if (geometry_handle != handle &&
                    geometry.transform_handle == handle) {
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
            matrix_transforms.erase(handle);
            fixed_geometries.erase(handle);
            geometry_groups.erase(handle);
            combined_geometries.erase(handle);
            path_geometries.erase(handle);
            solid_brushes.erase(handle);
            dash_styles.erase(handle);
            pens.erase(handle);
            resources.erase(found);
            ++metrics.deleted_resource_count;
            return status::success;
        }
        case command::visual_create: {
            if (!has_exact_size(view, 8U) ||
                !read_at(view.packet, 4U, handle)) {
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
            double x = 0.0;
            double y = 0.0;
            if (!has_exact_size(view, 24U) ||
                !read_at(view.packet, 4U, handle) ||
                !read_at(view.packet, 8U, x) ||
                !read_at(view.packet, 16U, y)) {
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
            std::uint32_t transform = 0U;
            if (!has_exact_size(view, 12U) ||
                !read_at(view.packet, 4U, handle) ||
                !read_at(view.packet, 8U, transform)) {
                return status::malformed_batch;
            }
            if (!require_visual(handle) ||
                (transform != 0U &&
                 !require_resource(transform, type_matrix_transform))) {
                return status::invalid_handle;
            }
            visuals.at(handle).transform_handle = transform;
            increment_generation(handle);
            ++metrics.updated_resource_count;
            return status::success;
        }
        case command::visual_set_alpha: {
            double opacity = 0.0;
            if (!has_exact_size(view, 16U) ||
                !read_at(view.packet, 4U, handle) ||
                !read_at(view.packet, 8U, opacity)) {
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
        case command::visual_set_content: {
            std::uint32_t content = 0U;
            if (!has_exact_size(view, 12U) ||
                !read_at(view.packet, 4U, handle) ||
                !read_at(view.packet, 8U, content)) {
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
        case command::visual_remove_all_children: {
            if (!has_exact_size(view, 8U) ||
                !read_at(view.packet, 4U, handle)) {
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
            std::uint32_t child = 0U;
            if (!has_exact_size(view, 12U) ||
                !read_at(view.packet, 4U, handle) ||
                !read_at(view.packet, 8U, child)) {
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
            std::uint32_t child = 0U;
            std::uint32_t index = 0U;
            if (!has_exact_size(view, 16U) ||
                !read_at(view.packet, 4U, handle) ||
                !read_at(view.packet, 8U, child) ||
                !read_at(view.packet, 12U, index)) {
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
            if (!has_exact_size(view, 36U) ||
                !read_at(view.packet, 4U, handle)) {
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
            std::uint32_t root = 0U;
            if (!has_exact_size(view, 12U) ||
                !read_at(view.packet, 4U, handle) ||
                !read_at(view.packet, 8U, root)) {
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
            float red = 0.0F;
            float green = 0.0F;
            float blue = 0.0F;
            float alpha = 0.0F;
            if (!has_exact_size(view, 24U) ||
                !read_at(view.packet, 4U, handle) ||
                !read_at(view.packet, 8U, red) ||
                !read_at(view.packet, 12U, green) ||
                !read_at(view.packet, 16U, blue) ||
                !read_at(view.packet, 20U, alpha)) {
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
            std::uint32_t flags = 0U;
            if (!has_exact_size(view, 12U) ||
                !read_at(view.packet, 4U, handle) ||
                !read_at(view.packet, 8U, flags)) {
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
        case command::target_invalidate:
            if (!has_exact_size(view, 24U) ||
                !read_at(view.packet, 4U, handle)) {
                return status::malformed_batch;
            }
            if (!require_target(handle)) {
                return status::invalid_handle;
            }
            ++metrics.updated_resource_count;
            return status::success;
        case command::render_data: {
            std::uint32_t data_size = 0U;
            if (view.packet.size() < 12U ||
                !read_at(view.packet, 4U, handle) ||
                !read_at(view.packet, 8U, data_size) ||
                data_size > view.packet.size() - 12U ||
                view.packet.size() - 12U - data_size > 3U) {
                return status::malformed_batch;
            }
            if (!require_resource(handle, type_render_data)) {
                return status::invalid_handle;
            }
            auto& resource = resources.at(handle);
            resource.render_data.assign(
                view.packet.begin() + 12,
                view.packet.begin() + 12 + data_size);
            increment_generation(handle);
            ++metrics.updated_resource_count;
            return status::success;
        }
        case command::matrix_transform: {
            matrix_transform_state matrix{};
            std::uint32_t animations = 0U;
            if (!has_exact_size(view, 60U) ||
                !read_at(view.packet, 4U, handle) ||
                !read_at(view.packet, 8U, matrix.m11) ||
                !read_at(view.packet, 16U, matrix.m12) ||
                !read_at(view.packet, 24U, matrix.m21) ||
                !read_at(view.packet, 32U, matrix.m22) ||
                !read_at(view.packet, 40U, matrix.m31) ||
                !read_at(view.packet, 48U, matrix.m32) ||
                !read_at(view.packet, 56U, animations)) {
                return status::malformed_batch;
            }
            if (!require_resource(handle, type_matrix_transform)) {
                return status::invalid_handle;
            }
            if (animations != 0U) {
                return status::unsupported_command;
            }
            progpu_native_affine_2d native_matrix{};
            if (!try_to_native_affine(matrix, native_matrix)) {
                return status::malformed_batch;
            }
            matrix_transforms.insert_or_assign(handle, matrix);
            increment_generation(handle);
            ++metrics.updated_resource_count;
            return status::success;
        }
        case command::line_geometry: {
            fixed_geometry_state geometry{};
            geometry.kind = fixed_geometry_kind::line;
            std::uint32_t start_animations = 0U;
            std::uint32_t end_animations = 0U;
            if (!has_exact_size(view, 52U) ||
                !read_at(view.packet, 4U, handle) ||
                !read_at(view.packet, 8U, geometry.first) ||
                !read_at(view.packet, 16U, geometry.second) ||
                !read_at(view.packet, 24U, geometry.third) ||
                !read_at(view.packet, 32U, geometry.fourth) ||
                !read_at(view.packet, 40U, geometry.transform_handle) ||
                !read_at(view.packet, 44U, start_animations) ||
                !read_at(view.packet, 48U, end_animations)) {
                return status::malformed_batch;
            }
            if (!require_resource(handle, type_line_geometry) ||
                (geometry.transform_handle != 0U &&
                 !require_resource(
                     geometry.transform_handle,
                     type_matrix_transform))) {
                return status::invalid_handle;
            }
            if (start_animations != 0U || end_animations != 0U) {
                return status::unsupported_command;
            }
            if (!finite_double_as_float(geometry.first) ||
                !finite_double_as_float(geometry.second) ||
                !finite_double_as_float(geometry.third) ||
                !finite_double_as_float(geometry.fourth)) {
                return status::malformed_batch;
            }
            fixed_geometries.insert_or_assign(handle, geometry);
            increment_generation(handle);
            ++metrics.updated_resource_count;
            return status::success;
        }
        case command::rectangle_geometry: {
            fixed_geometry_state geometry{};
            geometry.kind = fixed_geometry_kind::rectangle;
            std::uint32_t radius_x_animations = 0U;
            std::uint32_t radius_y_animations = 0U;
            std::uint32_t rect_animations = 0U;
            if (!has_exact_size(view, 72U) ||
                !read_at(view.packet, 4U, handle) ||
                !read_at(view.packet, 8U, geometry.radius_x) ||
                !read_at(view.packet, 16U, geometry.radius_y) ||
                !read_at(view.packet, 24U, geometry.first) ||
                !read_at(view.packet, 32U, geometry.second) ||
                !read_at(view.packet, 40U, geometry.third) ||
                !read_at(view.packet, 48U, geometry.fourth) ||
                !read_at(view.packet, 56U, geometry.transform_handle) ||
                !read_at(view.packet, 60U, radius_x_animations) ||
                !read_at(view.packet, 64U, radius_y_animations) ||
                !read_at(view.packet, 68U, rect_animations)) {
                return status::malformed_batch;
            }
            if (!require_resource(handle, type_rectangle_geometry) ||
                (geometry.transform_handle != 0U &&
                 !require_resource(
                     geometry.transform_handle,
                     type_matrix_transform))) {
                return status::invalid_handle;
            }
            if (radius_x_animations != 0U || radius_y_animations != 0U ||
                rect_animations != 0U) {
                return status::unsupported_command;
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
            fixed_geometries.insert_or_assign(handle, geometry);
            increment_generation(handle);
            ++metrics.updated_resource_count;
            return status::success;
        }
        case command::ellipse_geometry: {
            fixed_geometry_state geometry{};
            geometry.kind = fixed_geometry_kind::ellipse;
            std::uint32_t radius_x_animations = 0U;
            std::uint32_t radius_y_animations = 0U;
            std::uint32_t center_animations = 0U;
            if (!has_exact_size(view, 56U) ||
                !read_at(view.packet, 4U, handle) ||
                !read_at(view.packet, 8U, geometry.third) ||
                !read_at(view.packet, 16U, geometry.fourth) ||
                !read_at(view.packet, 24U, geometry.first) ||
                !read_at(view.packet, 32U, geometry.second) ||
                !read_at(view.packet, 40U, geometry.transform_handle) ||
                !read_at(view.packet, 44U, radius_x_animations) ||
                !read_at(view.packet, 48U, radius_y_animations) ||
                !read_at(view.packet, 52U, center_animations)) {
                return status::malformed_batch;
            }
            if (!require_resource(handle, type_ellipse_geometry) ||
                (geometry.transform_handle != 0U &&
                 !require_resource(
                     geometry.transform_handle,
                     type_matrix_transform))) {
                return status::invalid_handle;
            }
            if (radius_x_animations != 0U || radius_y_animations != 0U ||
                center_animations != 0U) {
                return status::unsupported_command;
            }
            if (!finite_double_as_float(geometry.first) ||
                !finite_double_as_float(geometry.second) ||
                !finite_double_as_float(geometry.third) ||
                !finite_double_as_float(geometry.fourth) ||
                geometry.third < 0.0 || geometry.fourth < 0.0) {
                return status::malformed_batch;
            }
            fixed_geometries.insert_or_assign(handle, geometry);
            increment_generation(handle);
            ++metrics.updated_resource_count;
            return status::success;
        }
        case command::geometry_group: {
            geometry_group_state geometry{};
            std::uint32_t children_size = 0U;
            if (view.packet.size() < 20U ||
                !read_at(view.packet, 4U, handle) ||
                !read_at(view.packet, 8U, geometry.transform_handle) ||
                !read_at(view.packet, 12U, geometry.fill_rule) ||
                !read_at(view.packet, 16U, children_size) ||
                children_size % sizeof(std::uint32_t) != 0U ||
                view.packet.size() != 20U + children_size) {
                return status::malformed_batch;
            }
            if (!require_resource(handle, type_geometry_group) ||
                (geometry.transform_handle != 0U &&
                 !require_resource(
                     geometry.transform_handle,
                     type_matrix_transform))) {
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
                        20U + index * sizeof(std::uint32_t),
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
            combined_geometry_state geometry{};
            if (!has_exact_size(view, 24U) ||
                !read_at(view.packet, 4U, handle) ||
                !read_at(view.packet, 8U, geometry.transform_handle) ||
                !read_at(view.packet, 12U, geometry.combine_mode) ||
                !read_at(view.packet, 16U, geometry.geometry1_handle) ||
                !read_at(view.packet, 20U, geometry.geometry2_handle)) {
                return status::malformed_batch;
            }
            if (!require_resource(handle, type_combined_geometry) ||
                (geometry.transform_handle != 0U &&
                 !require_resource(
                     geometry.transform_handle,
                     type_matrix_transform))) {
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
            path_geometry_state geometry{};
            std::uint32_t figures_size = 0U;
            if (view.packet.size() < 20U ||
                !read_at(view.packet, 4U, handle) ||
                !read_at(view.packet, 8U, geometry.transform_handle) ||
                !read_at(view.packet, 12U, geometry.fill_rule) ||
                !read_at(view.packet, 16U, figures_size) ||
                figures_size > view.packet.size() - 20U ||
                view.packet.size() != 20U + figures_size) {
                return status::malformed_batch;
            }
            if (!require_resource(handle, type_path_geometry) ||
                (geometry.transform_handle != 0U &&
                 !require_resource(
                     geometry.transform_handle,
                     type_matrix_transform))) {
                return status::invalid_handle;
            }
            if (geometry.fill_rule > 1U || figures_size < 48U) {
                return status::malformed_batch;
            }
            const auto figures = view.packet.subspan(20U, figures_size);
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
                    if (current.x != start.x || current.y != start.y) {
                        progpu_native_path_segment closing{};
                        closing.p0 = current;
                        closing.p1 = start;
                        closing.kind = PROGPU_NATIVE_PATH_SEGMENT_LINE;
                        geometry.segments.push_back(closing);
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
                        bool end_uses_dash_cap,
                        bool crosses_closed_figure_start = false) {
                        path_stroke_contour_state contour{};
                        contour.start_uses_dash_cap = start_uses_dash_cap;
                        contour.end_uses_dash_cap = end_uses_dash_cap;
                        contour.crosses_closed_figure_start =
                            crosses_closed_figure_start;
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
                                    true,
                                    first_after_gap + first <
                                            stroke_edges.size() &&
                                        first_after_gap + consumed >
                                            stroke_edges.size());
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
                                    index != stroke_edges.size(),
                                    false);
                            }
                        }
                    }
                }
                previous_figure_size = figure_size;
            }
            if (offset != figures.size()) {
                return status::malformed_batch;
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
            double opacity = 0.0;
            progpu_native_color color{};
            std::uint32_t opacity_animations = 0U;
            std::uint32_t transform = 0U;
            std::uint32_t relative_transform = 0U;
            std::uint32_t color_animations = 0U;
            if (!has_exact_size(view, 48U) ||
                !read_at(view.packet, 4U, handle) ||
                !read_at(view.packet, 8U, opacity) ||
                !read_at(view.packet, 16U, color) ||
                !read_at(view.packet, 32U, opacity_animations) ||
                !read_at(view.packet, 36U, transform) ||
                !read_at(view.packet, 40U, relative_transform) ||
                !read_at(view.packet, 44U, color_animations)) {
                return status::malformed_batch;
            }
            if (!require_resource(handle, type_solid_color_brush)) {
                return status::invalid_handle;
            }
            if (opacity_animations != 0U || transform != 0U ||
                relative_transform != 0U || color_animations != 0U) {
                return status::unsupported_command;
            }
            if (!std::isfinite(opacity) || opacity < 0.0 || opacity > 1.0 ||
                !std::isfinite(color.r) || !std::isfinite(color.g) ||
                !std::isfinite(color.b) || !std::isfinite(color.a)) {
                return status::malformed_batch;
            }
            solid_brushes.insert_or_assign(
                handle, solid_brush_state{opacity, color});
            increment_generation(handle);
            ++metrics.updated_resource_count;
            return status::success;
        }
        case command::dash_style: {
            dash_style_state dash{};
            std::uint32_t offset_animations = 0U;
            std::uint32_t dashes_size = 0U;
            if (view.packet.size() < 24U ||
                !read_at(view.packet, 4U, handle) ||
                !read_at(view.packet, 8U, dash.offset) ||
                !read_at(view.packet, 16U, offset_animations) ||
                !read_at(view.packet, 20U, dashes_size) ||
                dashes_size % sizeof(double) != 0U ||
                view.packet.size() != 24U + dashes_size) {
                return status::malformed_batch;
            }
            if (!require_resource(handle, type_dash_style)) {
                return status::invalid_handle;
            }
            if (offset_animations != 0U) {
                return status::unsupported_command;
            }
            if (!finite_double_as_float(dash.offset)) {
                return status::malformed_batch;
            }
            const std::size_t dash_count =
                dashes_size / sizeof(double);
            try {
                dash.intervals.resize(dash_count);
            } catch (const std::bad_alloc&) {
                return status::capacity_exceeded;
            }
            for (std::size_t index = 0U; index < dash_count; ++index) {
                if (!read_at(
                        view.packet,
                        24U + index * sizeof(double),
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
            pen_state pen{};
            std::uint32_t thickness_animations = 0U;
            std::uint32_t dash_style = 0U;
            if (!has_exact_size(view, 52U) ||
                !read_at(view.packet, 4U, handle) ||
                !read_at(view.packet, 8U, pen.thickness) ||
                !read_at(view.packet, 16U, pen.miter_limit) ||
                !read_at(view.packet, 24U, pen.brush_handle) ||
                !read_at(view.packet, 28U, thickness_animations) ||
                !read_at(view.packet, 32U, pen.start_line_cap) ||
                !read_at(view.packet, 36U, pen.end_line_cap) ||
                !read_at(view.packet, 40U, pen.dash_cap) ||
                !read_at(view.packet, 44U, pen.line_join) ||
                !read_at(view.packet, 48U, dash_style)) {
                return status::malformed_batch;
            }
            pen.dash_style_handle = dash_style;
            if (!require_resource(handle, type_pen) ||
                (pen.brush_handle != 0U &&
                 !require_resource(
                     pen.brush_handle,
                     type_solid_color_brush)) ||
                (dash_style != 0U &&
                 !require_resource(dash_style, type_dash_style))) {
                return status::invalid_handle;
            }
            if (thickness_animations != 0U) {
                return status::unsupported_command;
            }
            if (!finite_double_as_float(pen.thickness) ||
                pen.thickness < 0.0 ||
                !finite_double_as_float(pen.miter_limit) ||
                pen.miter_limit < 0.0 || pen.start_line_cap > 3U ||
                pen.end_line_cap > 3U || pen.dash_cap > 3U ||
                pen.line_join > 2U) {
                return status::malformed_batch;
            }
            pens.insert_or_assign(handle, pen);
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
        affine_2d_double parent_transform = {}) const {
        leaf = {};
        leaf.segment_offset = segments.size();
        const auto path = path_geometries.find(geometry_handle);
        if (path != path_geometries.end()) {
            if (path->second.segments.empty()) {
                return status::success;
            }
            affine_2d_double transform = parent_transform;
            if (path->second.transform_handle != 0U) {
                const auto found = matrix_transforms.find(
                    path->second.transform_handle);
                if (found == matrix_transforms.end()) {
                    return status::invalid_handle;
                }
                transform = compose_affine(
                    found->second,
                    parent_transform);
            }
            const bool transform_is_identity =
                transform.m11 == 1.0 && transform.m12 == 0.0 &&
                transform.m21 == 0.0 && transform.m22 == 1.0 &&
                transform.m31 == 0.0 && transform.m32 == 0.0;
            if (transform_is_identity) {
                segments.insert(
                    segments.end(),
                    path->second.segments.begin(),
                    path->second.segments.end());
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
                for (const auto& source : path->second.segments) {
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
        if (fixed->second.kind == fixed_geometry_kind::line) {
            return status::success;
        }
        affine_2d_double transform = parent_transform;
        if (fixed->second.transform_handle != 0U) {
            const auto found = matrix_transforms.find(
                fixed->second.transform_handle);
            if (found == matrix_transforms.end()) {
                return status::invalid_handle;
            }
            transform = compose_affine(
                found->second,
                parent_transform);
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
        const auto append_cubic = [
            &segments,
            &include_point,
            &map_point](
            double x0,
            double y0,
            double x1,
            double y1,
            double x2,
            double y2,
            double x3,
            double y3) {
            progpu_native_path_segment segment{};
            if (!map_point(x0, y0, segment.p0) ||
                !map_point(x1, y1, segment.p1) ||
                !map_point(x2, y2, segment.p2) ||
                !map_point(x3, y3, segment.p3)) {
                return false;
            }
            segment.kind = PROGPU_NATIVE_PATH_SEGMENT_CUBIC;
            include_point(segment.p0);
            include_point(segment.p1);
            include_point(segment.p2);
            include_point(segment.p3);
            segments.push_back(segment);
            return true;
        };
        bool appended = false;
        if (fixed->second.kind == fixed_geometry_kind::ellipse) {
            constexpr double arc_as_bezier = 0.5522847498307933984;
            const double center_x = fixed->second.first;
            const double center_y = fixed->second.second;
            const double radius_x = fixed->second.third;
            const double radius_y = fixed->second.fourth;
            const double mid_x = radius_x * arc_as_bezier;
            const double mid_y = radius_y * arc_as_bezier;
            appended = append_cubic(
                    center_x + radius_x,
                    center_y,
                    center_x + radius_x,
                    center_y + mid_y,
                    center_x + mid_x,
                    center_y + radius_y,
                    center_x,
                    center_y + radius_y) &&
                append_cubic(
                    center_x,
                    center_y + radius_y,
                    center_x - mid_x,
                    center_y + radius_y,
                    center_x - radius_x,
                    center_y + mid_y,
                    center_x - radius_x,
                    center_y) &&
                append_cubic(
                    center_x - radius_x,
                    center_y,
                    center_x - radius_x,
                    center_y - mid_y,
                    center_x - mid_x,
                    center_y - radius_y,
                    center_x,
                    center_y - radius_y) &&
                append_cubic(
                    center_x,
                    center_y - radius_y,
                    center_x + mid_x,
                    center_y - radius_y,
                    center_x + radius_x,
                    center_y - mid_y,
                    center_x + radius_x,
                    center_y);
        } else {
            const double left = fixed->second.first;
            const double top = fixed->second.second;
            const double right = left + fixed->second.third;
            const double bottom = top + fixed->second.fourth;
            const double radius_x = std::min(
                fixed->second.radius_x,
                fixed->second.third * 0.5);
            const double radius_y = std::min(
                fixed->second.radius_y,
                fixed->second.fourth * 0.5);
            if (radius_x == 0.0 && radius_y == 0.0) {
                appended = append_line(left, top, right, top) &&
                    append_line(right, top, right, bottom) &&
                    append_line(right, bottom, left, bottom) &&
                    append_line(left, bottom, left, top);
            } else {
                constexpr double one_minus_arc_as_bezier =
                    1.0 - 0.5522847498307933984;
                const double bezier_x =
                    radius_x * one_minus_arc_as_bezier;
                const double bezier_y =
                    radius_y * one_minus_arc_as_bezier;
                appended = append_cubic(
                        left,
                        top + radius_y,
                        left,
                        top + bezier_y,
                        left + bezier_x,
                        top,
                        left + radius_x,
                        top) &&
                    append_line(
                        left + radius_x,
                        top,
                        right - radius_x,
                        top) &&
                    append_cubic(
                        right - radius_x,
                        top,
                        right - bezier_x,
                        top,
                        right,
                        top + bezier_y,
                        right,
                        top + radius_y) &&
                    append_line(
                        right,
                        top + radius_y,
                        right,
                        bottom - radius_y) &&
                    append_cubic(
                        right,
                        bottom - radius_y,
                        right,
                        bottom - bezier_y,
                        right - bezier_x,
                        bottom,
                        right - radius_x,
                        bottom) &&
                    append_line(
                        right - radius_x,
                        bottom,
                        left + radius_x,
                        bottom) &&
                    append_cubic(
                        left + radius_x,
                        bottom,
                        left + bezier_x,
                        bottom,
                        left,
                        bottom - bezier_y,
                        left,
                        bottom - radius_y) &&
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
        leaf.segment_count = segments.size() - original_size;
        return status::success;
    }

    status append_group_fill_leaf(
        std::uint32_t geometry_handle,
        std::vector<progpu_native_path_segment>& segments,
        shallow_fill_leaf& leaf,
        affine_2d_double parent_transform = {},
        std::uint32_t depth = 1U) const {
        const auto group = geometry_groups.find(geometry_handle);
        if (group == geometry_groups.end()) {
            return append_shallow_fill_leaf(
                geometry_handle,
                segments,
                leaf,
                parent_transform);
        }
        if (depth == 0U || depth > maximum_visual_depth) {
            return status::invalid_graph;
        }
        affine_2d_double transform = parent_transform;
        if (group->second.transform_handle != 0U) {
            const auto found = matrix_transforms.find(
                group->second.transform_handle);
            if (found == matrix_transforms.end()) {
                return status::invalid_handle;
            }
            transform = compose_affine(found->second, parent_transform);
        }
        const std::size_t original_size = segments.size();
        leaf = {};
        leaf.segment_offset = original_size;
        leaf.fill_rule = group->second.fill_rule == 0U
            ? PROGPU_NATIVE_FILL_RULE_EVEN_ODD
            : PROGPU_NATIVE_FILL_RULE_NON_ZERO;
        for (const std::uint32_t child_handle : group->second.children) {
            shallow_fill_leaf child{};
            const status child_status = append_group_fill_leaf(
                child_handle,
                segments,
                child,
                transform,
                depth + 1U);
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
            const auto found = matrix_transforms.find(
                combined->second.transform_handle);
            if (found == matrix_transforms.end()) {
                return status::invalid_handle;
            }
            transform = compose_affine(found->second, parent_transform);
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
        const affine_2d_double& base_transform,
        double base_opacity,
        native::semantic_scene_builder& builder,
        std::unordered_map<std::uint32_t, std::uint32_t>& brush_indices,
        scene_metrics& metrics) const {
        const auto resource = resources.find(content_handle);
        if (resource == resources.end() ||
            resource->second.type != type_render_data) {
            return status::invalid_handle;
        }

        batch_reader reader(resource->second.render_data);
        command_view view{};
        struct render_scope_state {
            affine_2d_double transform;
            double opacity{1.0};
        };
        render_scope_state current{base_transform, base_opacity};
        std::vector<render_scope_state> scope_states;
        const auto save_state = [&builder](
            const render_scope_state& source) noexcept {
            auto state = native::semantic_scene_builder::identity_state();
            if (!try_to_native_affine(source.transform, state.transform)) {
                return false;
            }
            state.opacity = static_cast<float>(source.opacity);
            std::uint32_t state_index = PROGPU_NATIVE_SCENE_NO_INDEX;
            return builder.add_state(state, state_index) &&
                builder.save(state_index);
        };
        const auto resolve_brush_index = [
            this,
            &builder,
            &brush_indices](
            std::uint32_t brush_handle,
            std::uint32_t& result) noexcept {
            const auto brush = solid_brushes.find(brush_handle);
            if (brush == solid_brushes.end()) {
                return status::invalid_handle;
            }
            const auto existing = brush_indices.find(brush_handle);
            if (existing != brush_indices.end()) {
                result = existing->second;
                return status::success;
            }
            std::uint32_t added = PROGPU_NATIVE_SCENE_NO_INDEX;
            if (!builder.add_solid_brush(
                    brush->second.color,
                    static_cast<float>(brush->second.opacity),
                    added)) {
                return status::invalid_graph;
            }
            brush_indices.emplace(brush_handle, added);
            result = added;
            return status::success;
        };
        const auto append_polyline_stroke = [
            this,
            &builder](
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
                dash_offset = dash->second.offset;
            }
            progpu_native_scene_stroke stroke{};
            stroke.struct_size = sizeof(stroke);
            stroke.kind = PROGPU_NATIVE_SCENE_STROKE_POLYLINE;
            stroke.flags = closed
                ? PROGPU_NATIVE_POLYLINE_FLAG_CLOSED
                : 0U;
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
        const auto append_line_stroke = [
            this,
            &builder,
            &resolve_brush_index,
            &append_polyline_stroke,
            &metrics](
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
            const auto pen = pens.find(pen_handle);
            if (pen == pens.end()) {
                return status::invalid_handle;
            }
            if (pen->second.brush_handle == 0U ||
                pen->second.thickness == 0.0) {
                return status::success;
            }
            if (x0 == x1 && y0 == y1) {
                return pen->second.start_line_cap == 0U &&
                    pen->second.end_line_cap == 0U
                    ? status::success
                    : status::unsupported_command;
            }
            std::uint32_t brush_index = PROGPU_NATIVE_SCENE_NO_INDEX;
            const status brush_status = resolve_brush_index(
                pen->second.brush_handle,
                brush_index);
            if (brush_status != status::success) {
                return brush_status;
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
                    pen->second.thickness,
                    pen->second.start_line_cap,
                    pen->second.end_line_cap,
                    local_x,
                    local_y,
                    local_width,
                    local_height)) {
                return status::invalid_graph;
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
                (pen->second.start_line_cap <<
                    PROGPU_NATIVE_PRIMITIVE_START_CAP_SHIFT) |
                (pen->second.end_line_cap <<
                    PROGPU_NATIVE_PRIMITIVE_END_CAP_SHIFT);
            if (pen->second.dash_style_handle == 0U) {
                const std::array primitives{
                    progpu_native_geometry_primitive{
                        PROGPU_NATIVE_GEOMETRY_LINE,
                        flags,
                        {static_cast<float>(x0), static_cast<float>(y0)},
                        {static_cast<float>(x1), static_cast<float>(y1)},
                        {},
                        {},
                        static_cast<float>(pen->second.thickness),
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
                    pen->second,
                    points,
                    false,
                    brush_index,
                    transformed_bounds,
                    native_local_transform,
                    pen->second.start_line_cap,
                    pen->second.end_line_cap);
                if (stroke_status != status::success) {
                    return stroke_status;
                }
            }
            ++metrics.line_count;
            return status::success;
        };
        const auto append_path_strokes = [
            this,
            &builder,
            &resolve_brush_index,
            &append_polyline_stroke](
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
            std::uint32_t brush_index = PROGPU_NATIVE_SCENE_NO_INDEX;
            const status brush_status = resolve_brush_index(
                pen.brush_handle,
                brush_index);
            if (brush_status != status::success) {
                return brush_status;
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
                if (contour.crosses_closed_figure_start &&
                    pen.dash_style_handle != 0U) {
                    const auto dash = dash_styles.find(
                        pen.dash_style_handle);
                    if (dash == dash_styles.end()) {
                        return status::invalid_handle;
                    }
                    if (!dash->second.intervals.empty()) {
                        return status::unsupported_command;
                    }
                }
                if (contour.points.size() < 2U) {
                    if (pen.start_line_cap != 0U ||
                        pen.end_line_cap != 0U) {
                        return status::unsupported_command;
                    }
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
                    if (pen.start_line_cap != 0U ||
                        pen.end_line_cap != 0U) {
                        return status::unsupported_command;
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
                        &pen](
                        const progpu_native_path_segment& segment,
                        progpu_native_geometry_primitive& primitive) noexcept {
                        primitive = {};
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
                        brush_index](
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
                        primitive.flags = cap <<
                            PROGPU_NATIVE_PRIMITIVE_START_CAP_SHIFT;
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
                        brush_index](
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
                        join.flags = (smooth_join
                                ? PROGPU_NATIVE_STROKE_JOIN_ROUND
                                : pen.line_join) <<
                            PROGPU_NATIVE_PRIMITIVE_START_CAP_SHIFT;
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
        for (;;) {
            const status read_status = reader.next(view);
            if (read_status == status::end_of_batch) {
                return scope_states.empty()
                    ? status::success
                    : status::invalid_graph;
            }
            if (read_status != status::success) {
                return read_status;
            }

            if (view.kind == command::push_opacity) {
                double opacity = 0.0;
                if (!has_exact_size(view, 12U) ||
                    !read_at(view.packet, 4U, opacity)) {
                    return status::malformed_batch;
                }
                if (!std::isfinite(opacity) || opacity < 0.0 ||
                    opacity > 1.0) {
                    return status::malformed_batch;
                }
                const double combined_opacity = current.opacity * opacity;
                if (!std::isfinite(combined_opacity) ||
                    combined_opacity < 0.0 || combined_opacity > 1.0) {
                    return status::invalid_graph;
                }
                const render_scope_state next{
                    current.transform,
                    combined_opacity};
                if (!save_state(next)) {
                    return status::invalid_graph;
                }
                scope_states.push_back(current);
                current = next;
                continue;
            }
            if (view.kind == command::push_transform) {
                std::uint32_t transform_handle = 0U;
                std::uint32_t padding = 0U;
                if (!has_exact_size(view, 12U) ||
                    !read_at(view.packet, 4U, transform_handle) ||
                    !read_at(view.packet, 8U, padding)) {
                    return status::malformed_batch;
                }
                if (padding != 0U) {
                    return status::malformed_batch;
                }
                affine_2d_double pushed_transform{};
                if (transform_handle != 0U) {
                    const auto transform =
                        matrix_transforms.find(transform_handle);
                    if (transform == matrix_transforms.end()) {
                        return status::invalid_handle;
                    }
                    pushed_transform = transform->second;
                }
                const render_scope_state next{
                    compose_affine(pushed_transform, current.transform),
                    current.opacity};
                if (!save_state(next)) {
                    return status::invalid_graph;
                }
                scope_states.push_back(current);
                current = next;
                continue;
            }
            if (view.kind == command::pop) {
                if (!has_exact_size(view, 4U)) {
                    return status::malformed_batch;
                }
                if (scope_states.empty()) {
                    return status::invalid_graph;
                }
                if (!builder.restore()) {
                    return status::invalid_graph;
                }
                current = scope_states.back();
                scope_states.pop_back();
                continue;
            }
            if (view.kind == command::draw_line) {
                double x0 = 0.0;
                double y0 = 0.0;
                double x1 = 0.0;
                double y1 = 0.0;
                std::uint32_t pen_handle = 0U;
                std::uint32_t padding = 0U;
                if (!has_exact_size(view, 44U) ||
                    !read_at(view.packet, 4U, x0) ||
                    !read_at(view.packet, 12U, y0) ||
                    !read_at(view.packet, 20U, x1) ||
                    !read_at(view.packet, 28U, y1) ||
                    !read_at(view.packet, 36U, pen_handle) ||
                    !read_at(view.packet, 40U, padding)) {
                    return status::malformed_batch;
                }
                if (padding != 0U || !finite_double_as_float(x0) ||
                    !finite_double_as_float(y0) ||
                    !finite_double_as_float(x1) ||
                    !finite_double_as_float(y1)) {
                    return status::malformed_batch;
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
            affine_2d_double local_transform{};
            affine_2d_double effective_transform = current.transform;
            if (view.kind == command::draw_geometry) {
                std::uint32_t geometry_handle = 0U;
                std::uint32_t padding = 0U;
                if (!has_exact_size(view, 20U) ||
                    !read_at(view.packet, 4U, brush_handle) ||
                    !read_at(view.packet, 8U, pen_handle) ||
                    !read_at(view.packet, 12U, geometry_handle) ||
                    !read_at(view.packet, 16U, padding)) {
                    return status::malformed_batch;
                }
                if (padding != 0U || geometry_handle == 0U) {
                    return status::malformed_batch;
                }
                if (brush_handle != 0U &&
                    !solid_brushes.contains(brush_handle)) {
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
                const std::uint32_t geometry_transform_handle =
                    geometry != fixed_geometries.end()
                        ? geometry->second.transform_handle
                        : geometry_group != geometry_groups.end()
                            ? geometry_group->second.transform_handle
                            : combined_geometry != combined_geometries.end()
                                ? combined_geometry->second.transform_handle
                                : path_geometry->second.transform_handle;
                if (geometry_transform_handle != 0U) {
                    const auto transform = matrix_transforms.find(
                        geometry_transform_handle);
                    if (transform == matrix_transforms.end()) {
                        return status::invalid_handle;
                    }
                    local_transform = transform->second;
                }
                effective_transform = compose_affine(
                    local_transform,
                    current.transform);
                if (geometry_group != geometry_groups.end()) {
                    if (pen_handle != 0U) {
                        const auto pen = pens.find(pen_handle);
                        if (pen == pens.end()) {
                            return status::invalid_handle;
                        }
                        if (pen->second.brush_handle != 0U &&
                            pen->second.thickness > 0.0) {
                            return status::unsupported_command;
                        }
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
                            child);
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
                    const status brush_status = resolve_brush_index(
                        brush_handle,
                        brush_index);
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
                            8U}};
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
                    if (pen_handle != 0U) {
                        const auto pen = pens.find(pen_handle);
                        if (pen == pens.end()) {
                            return status::invalid_handle;
                        }
                        if (pen->second.brush_handle != 0U &&
                            pen->second.thickness > 0.0) {
                            return status::unsupported_command;
                        }
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
                    const status brush_status = resolve_brush_index(
                        brush_handle,
                        brush_index);
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
                            8U}};
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
                    if (brush_handle != 0U &&
                        !path_geometry->second.segments.empty()) {
                        std::uint32_t brush_index =
                            PROGPU_NATIVE_SCENE_NO_INDEX;
                        const status brush_status = resolve_brush_index(
                            brush_handle,
                            brush_index);
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
                                path_geometry->second.segments.size(),
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
                                8U}};
                        const std::array brushes{brush_index};
                        if (!builder.draw_paths(
                                paths,
                                path_geometry->second.segments,
                                brushes,
                                path_bounds)) {
                            return status::invalid_graph;
                        }
                    }
                    if (pen_handle != 0U) {
                        const auto pen = pens.find(pen_handle);
                        if (pen == pens.end()) {
                            return status::invalid_handle;
                        }
                        const status stroke_status = append_path_strokes(
                            path_geometry->second,
                            pen->second,
                            local_transform,
                            effective_transform);
                        if (stroke_status != status::success) {
                            return stroke_status;
                        }
                    }
                    continue;
                }
                if (geometry->second.kind == fixed_geometry_kind::line) {
                    const status line_status = append_line_stroke(
                        geometry->second.first,
                        geometry->second.second,
                        geometry->second.third,
                        geometry->second.fourth,
                        pen_handle,
                        local_transform,
                        effective_transform);
                    if (line_status != status::success) {
                        return line_status;
                    }
                    continue;
                }
                is_geometry_shape = true;
                first = geometry->second.first;
                second = geometry->second.second;
                third = geometry->second.third;
                fourth = geometry->second.fourth;
                radius_x = geometry->second.radius_x;
                radius_y = geometry->second.radius_y;
                is_ellipse =
                    geometry->second.kind == fixed_geometry_kind::ellipse;
                is_rounded = !is_ellipse &&
                    (radius_x != 0.0 || radius_y != 0.0);
            }
            if (!is_geometry_shape) {
                if (view.kind != command::draw_rectangle &&
                    view.kind != command::draw_rounded_rectangle &&
                    view.kind != command::draw_ellipse) {
                    return status::unsupported_command;
                }
                is_rounded =
                    view.kind == command::draw_rounded_rectangle;
                is_ellipse = view.kind == command::draw_ellipse;
                if (!has_exact_size(view, is_rounded ? 60U : 44U) ||
                    !read_at(view.packet, 4U, first) ||
                    !read_at(view.packet, 12U, second) ||
                    !read_at(view.packet, 20U, third) ||
                    !read_at(view.packet, 28U, fourth)) {
                    return status::malformed_batch;
                }
                if (is_rounded) {
                    if (!read_at(view.packet, 36U, radius_x) ||
                        !read_at(view.packet, 44U, radius_y) ||
                        !read_at(view.packet, 52U, brush_handle) ||
                        !read_at(view.packet, 56U, pen_handle)) {
                        return status::malformed_batch;
                    }
                } else if (!read_at(view.packet, 36U, brush_handle) ||
                    !read_at(view.packet, 40U, pen_handle)) {
                    return status::malformed_batch;
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
            if (is_rounded && radius_x != radius_y) {
                return status::unsupported_command;
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
            if (brush_handle == 0U && pen_handle == 0U) {
                continue;
            }
            progpu_native_affine_2d native_local_transform{};
            if (!try_to_native_affine(
                    local_transform,
                    native_local_transform)) {
                return status::invalid_graph;
            }
            if (brush_handle != 0U) {
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
                const status brush_status = resolve_brush_index(
                    brush_handle,
                    brush_index);
                if (brush_status != status::success) {
                    return brush_status;
                }
                const std::array primitive{
                    progpu_native_analytic_primitive{
                        static_cast<std::uint32_t>(
                            is_ellipse
                                ? PROGPU_NATIVE_PRIMITIVE_ELLIPSE
                                : is_rounded
                                    ? PROGPU_NATIVE_PRIMITIVE_ROUNDED_RECTANGLE
                                    : PROGPU_NATIVE_PRIMITIVE_RECTANGLE),
                        0U,
                        static_cast<float>(x),
                        static_cast<float>(y),
                        static_cast<float>(width),
                        static_cast<float>(height),
                        static_cast<float>(radius_x),
                        0.0F,
                        {1.0F, 1.0F, 1.0F, 1.0F},
                        native_local_transform}};
                const std::array brushes{brush_index};
                if (!builder.draw_analytic(
                        primitive,
                        brushes,
                        fill_bounds)) {
                    return status::invalid_graph;
                }
            }
            if (pen_handle != 0U) {
                const auto pen = pens.find(pen_handle);
                if (pen == pens.end()) {
                    return status::invalid_handle;
                }
                if (pen->second.brush_handle != 0U &&
                    pen->second.thickness > 0.0) {
                    if (width == 0.0 || height == 0.0) {
                        return status::unsupported_command;
                    }
                    std::uint32_t pen_brush_index =
                        PROGPU_NATIVE_SCENE_NO_INDEX;
                    const status brush_status = resolve_brush_index(
                        pen->second.brush_handle,
                        pen_brush_index);
                    if (brush_status != status::success) {
                        return brush_status;
                    }
                    const double half_thickness =
                        pen->second.thickness * 0.5;
                    progpu_native_image_rect stroke_bounds{};
                    if (!try_transform_bounds(
                            x - half_thickness,
                            y - half_thickness,
                            width + pen->second.thickness,
                            height + pen->second.thickness,
                            effective_transform,
                            stroke_bounds)) {
                        return status::invalid_graph;
                    }
                    if (is_ellipse) {
                        if (pen->second.dash_style_handle != 0U) {
                            const auto dash = dash_styles.find(
                                pen->second.dash_style_handle);
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
                                0U,
                                {static_cast<float>(first),
                                    static_cast<float>(second)},
                                {static_cast<float>(third), 0.0F},
                                {0.0F, static_cast<float>(fourth)},
                                {0.0F,
                                    std::numbers::pi_v<float> * 2.0F},
                                static_cast<float>(pen->second.thickness),
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
                    } else if (is_rounded && radius_x > 0.0) {
                        if (pen->second.dash_style_handle != 0U) {
                            const auto dash = dash_styles.find(
                                pen->second.dash_style_handle);
                            if (dash == dash_styles.end()) {
                                return status::invalid_handle;
                            }
                            if (!dash->second.intervals.empty()) {
                                return status::unsupported_command;
                            }
                        }
                        const std::array primitive{
                            progpu_native_analytic_primitive{
                                PROGPU_NATIVE_PRIMITIVE_ROUNDED_RECTANGLE,
                                0U,
                                static_cast<float>(x),
                                static_cast<float>(y),
                                static_cast<float>(width),
                                static_cast<float>(height),
                                static_cast<float>(radius_x),
                                static_cast<float>(pen->second.thickness),
                                {1.0F, 1.0F, 1.0F, 1.0F},
                                native_local_transform}};
                        const std::array brushes{pen_brush_index};
                        if (!builder.draw_analytic(
                                primitive,
                                brushes,
                                stroke_bounds)) {
                            return status::invalid_graph;
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
                        const status stroke_status = append_polyline_stroke(
                            pen->second,
                            points,
                            true,
                            pen_brush_index,
                            stroke_bounds,
                            native_local_transform,
                            pen->second.start_line_cap,
                            pen->second.end_line_cap);
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

    status append_visual(
        std::uint32_t handle,
        const affine_2d_double& parent_transform,
        double parent_opacity,
        std::uint32_t depth,
        native::semantic_scene_builder& builder,
        std::unordered_map<std::uint32_t, std::uint32_t>& brush_indices,
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
            const auto transform = matrix_transforms.find(
                visual->second.transform_handle);
            if (transform == matrix_transforms.end()) {
                active_visuals.erase(handle);
                return status::invalid_handle;
            }
            local_transform = transform->second;
        }
        const affine_2d_double offset_transform{
            1.0,
            0.0,
            0.0,
            1.0,
            visual->second.offset_x,
            visual->second.offset_y};
        const affine_2d_double transform = compose_affine(
            compose_affine(local_transform, offset_transform),
            parent_transform);
        const double opacity = parent_opacity * visual->second.opacity;
        if (!std::isfinite(opacity) ||
            opacity < 0.0 || opacity > 1.0) {
            active_visuals.erase(handle);
            return status::invalid_graph;
        }

        auto state = native::semantic_scene_builder::identity_state();
        if (!try_to_native_affine(transform, state.transform)) {
            active_visuals.erase(handle);
            return status::invalid_graph;
        }
        state.opacity = static_cast<float>(opacity);
        std::uint32_t state_index = PROGPU_NATIVE_SCENE_NO_INDEX;
        if (!builder.add_state(state, state_index) ||
            !builder.save(state_index)) {
            active_visuals.erase(handle);
            return status::invalid_graph;
        }

        ++metrics.visual_count;
        metrics.maximum_visual_depth =
            std::max(metrics.maximum_visual_depth, depth);
        status result = status::success;
        if (visual->second.content_handle != 0U) {
            result = append_render_data(
                visual->second.content_handle,
                transform,
                opacity,
                builder,
                brush_indices,
                metrics);
        }
        if (result == status::success) {
            for (const auto child : visual->second.children) {
                result = append_visual(
                    child,
                    transform,
                    opacity,
                    depth + 1U,
                    builder,
                    brush_indices,
                    active_visuals,
                    metrics);
                if (result != status::success) {
                    break;
                }
            }
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
        std::unordered_set<std::uint32_t> active_visuals;
        if (target->second.root_handle != 0U) {
            const status append_status = implementation_->append_visual(
                target->second.root_handle,
                affine_2d_double{},
                1.0,
                1U,
                builder,
                brush_indices,
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
