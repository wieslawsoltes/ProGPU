#include "progpu_native_mil.hpp"
#include "progpu_native_scene_builder.hpp"

#include <algorithm>
#include <array>
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
constexpr std::uint32_t type_solid_color_brush = 75U;
constexpr std::uint32_t type_dash_style = 84U;
constexpr std::uint32_t type_pen = 85U;
constexpr std::uint32_t type_last = 98U;
constexpr std::uint32_t maximum_visual_depth = 256U;

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
            visuals.erase(handle);
            targets.erase(handle);
            matrix_transforms.erase(handle);
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
            progpu_native_image_rect bounds) noexcept {
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
            stroke.transform =
                native::semantic_scene_builder::identity_transform();
            stroke.stroke_thickness = static_cast<float>(pen.thickness);
            stroke.miter_limit =
                static_cast<float>(std::max(1.0, pen.miter_limit));
            stroke.dash_offset = dash_offset;
            stroke.start_cap = pen.start_line_cap;
            stroke.end_cap = pen.end_line_cap;
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
                if (pen_handle == 0U) {
                    continue;
                }
                const auto pen = pens.find(pen_handle);
                if (pen == pens.end()) {
                    return status::invalid_handle;
                }
                if (pen->second.brush_handle == 0U ||
                    pen->second.thickness == 0.0) {
                    continue;
                }
                if (x0 == x1 && y0 == y1) {
                    if (pen->second.start_line_cap == 0U &&
                        pen->second.end_line_cap == 0U) {
                        continue;
                    }
                    return status::unsupported_command;
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
                        current.transform,
                        transformed_bounds)) {
                    return status::invalid_graph;
                }
                const std::uint32_t flags =
                    (pen->second.start_line_cap <<
                        PROGPU_NATIVE_PRIMITIVE_START_CAP_SHIFT) |
                    (pen->second.end_line_cap <<
                        PROGPU_NATIVE_PRIMITIVE_END_CAP_SHIFT);
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
                        native::semantic_scene_builder::identity_transform()}};
                const std::array brushes{brush_index};
                if (pen->second.dash_style_handle == 0U) {
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
                        transformed_bounds);
                    if (stroke_status != status::success) {
                        return stroke_status;
                    }
                }
                ++metrics.line_count;
                continue;
            }
            if (view.kind != command::draw_rectangle &&
                view.kind != command::draw_rounded_rectangle &&
                view.kind != command::draw_ellipse) {
                return status::unsupported_command;
            }

            const bool is_rounded =
                view.kind == command::draw_rounded_rectangle;
            double first = 0.0;
            double second = 0.0;
            double third = 0.0;
            double fourth = 0.0;
            double radius_x = 0.0;
            double radius_y = 0.0;
            std::uint32_t brush_handle = 0U;
            std::uint32_t pen_handle = 0U;
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
            const bool is_ellipse = view.kind == command::draw_ellipse;
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
            if (brush_handle != 0U) {
                progpu_native_image_rect fill_bounds{};
                if (!try_transform_bounds(
                        x,
                        y,
                        width,
                        height,
                        current.transform,
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
                        native::semantic_scene_builder::identity_transform()}};
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
                            current.transform,
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
                                native::semantic_scene_builder::
                                    identity_transform()}};
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
                                native::semantic_scene_builder::
                                    identity_transform()}};
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
                            stroke_bounds);
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
