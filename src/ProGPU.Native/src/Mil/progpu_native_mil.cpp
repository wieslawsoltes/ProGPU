#include "progpu_native_mil.hpp"
#include "progpu_native_scene_builder.hpp"

#include <algorithm>
#include <array>
#include <cmath>
#include <cstring>
#include <limits>
#include <new>
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
constexpr std::uint32_t type_solid_color_brush = 75U;
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

    struct resource_state {
        std::uint32_t type{};
        std::uint64_t generation{1U};
        std::vector<std::byte> render_data;
    };

    std::unordered_map<std::uint32_t, resource_state> resources;
    std::unordered_map<std::uint32_t, visual_state> visuals;
    std::unordered_map<std::uint32_t, target_state> targets;
    std::unordered_map<std::uint32_t, solid_brush_state> solid_brushes;

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
                    (visual.content_handle == handle ||
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
            visuals.erase(handle);
            targets.erase(handle);
            solid_brushes.erase(handle);
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
        default:
            ++metrics.unsupported_command_count;
            return status::unsupported_command;
        }
    }


    status append_render_data(
        std::uint32_t content_handle,
        double offset_x,
        double offset_y,
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
        double current_opacity = base_opacity;
        std::vector<double> scope_opacities;
        for (;;) {
            const status read_status = reader.next(view);
            if (read_status == status::end_of_batch) {
                return scope_opacities.empty()
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
                const double combined_opacity = current_opacity * opacity;
                if (!std::isfinite(combined_opacity) ||
                    combined_opacity < 0.0 || combined_opacity > 1.0) {
                    return status::invalid_graph;
                }
                auto state = native::semantic_scene_builder::identity_state();
                state.transform.m31 = static_cast<float>(offset_x);
                state.transform.m32 = static_cast<float>(offset_y);
                state.opacity = static_cast<float>(combined_opacity);
                std::uint32_t state_index = PROGPU_NATIVE_SCENE_NO_INDEX;
                if (!builder.add_state(state, state_index) ||
                    !builder.save(state_index)) {
                    return status::invalid_graph;
                }
                scope_opacities.push_back(current_opacity);
                current_opacity = combined_opacity;
                continue;
            }
            if (view.kind == command::pop) {
                if (!has_exact_size(view, 4U)) {
                    return status::malformed_batch;
                }
                if (scope_opacities.empty()) {
                    return status::invalid_graph;
                }
                if (!builder.restore()) {
                    return status::invalid_graph;
                }
                current_opacity = scope_opacities.back();
                scope_opacities.pop_back();
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
            if (brush_handle == 0U || pen_handle != 0U) {
                return status::unsupported_command;
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
                !finite_double_as_float(height) ||
                !finite_double_as_float(x + offset_x) ||
                !finite_double_as_float(y + offset_y)) {
                return status::malformed_batch;
            }

            const auto brush = solid_brushes.find(brush_handle);
            if (brush == solid_brushes.end()) {
                return status::invalid_handle;
            }
            auto brush_index = brush_indices.find(brush_handle);
            if (brush_index == brush_indices.end()) {
                std::uint32_t added = PROGPU_NATIVE_SCENE_NO_INDEX;
                if (!builder.add_solid_brush(
                        brush->second.color,
                        static_cast<float>(brush->second.opacity),
                        added)) {
                    return status::invalid_graph;
                }
                brush_index = brush_indices.emplace(brush_handle, added).first;
            }

            const std::array primitive{
                progpu_native_analytic_primitive{
                    is_ellipse
                        ? PROGPU_NATIVE_PRIMITIVE_ELLIPSE
                        : is_rounded
                            ? PROGPU_NATIVE_PRIMITIVE_ROUNDED_RECTANGLE
                            : PROGPU_NATIVE_PRIMITIVE_RECTANGLE,
                    0U,
                    static_cast<float>(x),
                    static_cast<float>(y),
                    static_cast<float>(width),
                    static_cast<float>(height),
                    static_cast<float>(radius_x),
                    0.0F,
                    {1.0F, 1.0F, 1.0F, 1.0F},
                    native::semantic_scene_builder::identity_transform()}};
            const std::array brushes{brush_index->second};
            if (!builder.draw_analytic(
                    primitive,
                    brushes,
                    {static_cast<float>(x + offset_x),
                     static_cast<float>(y + offset_y),
                     static_cast<float>(width),
                     static_cast<float>(height)})) {
                return status::invalid_graph;
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
        double parent_offset_x,
        double parent_offset_y,
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
        const double offset_x = parent_offset_x + visual->second.offset_x;
        const double offset_y = parent_offset_y + visual->second.offset_y;
        const double opacity = parent_opacity * visual->second.opacity;
        if (!finite_double_as_float(offset_x) ||
            !finite_double_as_float(offset_y) || !std::isfinite(opacity) ||
            opacity < 0.0 || opacity > 1.0) {
            active_visuals.erase(handle);
            return status::invalid_graph;
        }

        auto state = native::semantic_scene_builder::identity_state();
        state.transform = {
            1.0F,
            0.0F,
            0.0F,
            1.0F,
            static_cast<float>(offset_x),
            static_cast<float>(offset_y)};
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
                offset_x,
                offset_y,
                opacity,
                builder,
                brush_indices,
                metrics);
        }
        if (result == status::success) {
            for (const auto child : visual->second.children) {
                result = append_visual(
                    child,
                    offset_x,
                    offset_y,
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
                0.0,
                0.0,
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
