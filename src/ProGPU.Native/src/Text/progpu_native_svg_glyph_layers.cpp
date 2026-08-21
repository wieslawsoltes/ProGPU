#include "progpu_native_svg_document_internal.hpp"
#include "progpu_native_svg_path_internal.hpp"

#include <algorithm>
#include <bit>
#include <cctype>
#include <cmath>
#include <iterator>
#include <limits>
#include <numbers>

// Direct native port provenance: ProGPU-owned OpenTypeSvgGlyphParser at
// checkpoint b7849116. XML shapes lower directly to the same canonical path,
// brush, and gradient-stop records used by the native retained scene.
namespace progpu::native::text::svg_document_detail {
namespace {

using svg_path_detail::point;
using svg_path_detail::resolve_arc;

constexpr float primitive_epsilon = 0.0001F;

void set_identity_brush_transform(progpu_native_scene_brush& brush) noexcept {
    brush.coordinate_transform0[0] = 1.0F;
    brush.coordinate_transform1[1] = 1.0F;
}

void include(
    progpu_native_point value,
    float& minimum_x,
    float& minimum_y,
    float& maximum_x,
    float& maximum_y) noexcept {
    minimum_x = std::min(minimum_x, value.x);
    minimum_y = std::min(minimum_y, value.y);
    maximum_x = std::max(maximum_x, value.x);
    maximum_y = std::max(maximum_y, value.y);
}

void append_line(
    std::vector<progpu_native_path_segment>& output,
    progpu_native_point start,
    progpu_native_point end) {
    progpu_native_path_segment segment{};
    segment.p0 = start;
    segment.p1 = end;
    segment.kind = PROGPU_NATIVE_PATH_SEGMENT_LINE;
    output.push_back(segment);
}

bool append_arc(
    std::vector<progpu_native_path_segment>& output,
    progpu_native_point start,
    progpu_native_point end,
    float radius_x,
    float radius_y) {
    point center{};
    float theta = 0.0F;
    float delta = 0.0F;
    float resolved_x = 0.0F;
    float resolved_y = 0.0F;
    if (!resolve_arc(
            {start.x, start.y}, {end.x, end.y},
            {radius_x, radius_y}, 0.0F, false, true,
            center, theta, delta, resolved_x, resolved_y)) {
        append_line(output, start, end);
        return true;
    }
    progpu_native_path_segment segment{};
    segment.p0 = start;
    segment.p1 = end;
    segment.p2 = {center.x, center.y};
    segment.p3 = {resolved_x, resolved_y};
    segment.kind = PROGPU_NATIVE_PATH_SEGMENT_ARC;
    segment.pad0 = std::bit_cast<std::uint32_t>(theta);
    segment.pad1 = std::bit_cast<std::uint32_t>(delta);
    segment.pad2 = std::bit_cast<std::uint32_t>(0.0F);
    output.push_back(segment);
    return true;
}

bool create_path_geometry(
    const node& element,
    decoded_glyph& output,
    svg_path_requirements& requirements) {
    const auto* data = find_attribute(element, "d");
    if (data == nullptr || data->find_first_not_of(" \t\r\n") ==
        std::string::npos ||
        !try_get_svg_path_requirements(*data, requirements)) {
        return false;
    }
    const auto start = output.segments.size();
    output.segments.resize(start + requirements.segment_count);
    svg_path_requirements decoded{};
    if (!try_decode_svg_path(*data,
            std::span<progpu_native_path_segment>{output.segments}.subspan(start),
            decoded)) {
        output.segments.resize(start);
        return false;
    }
    return decoded.segment_count != 0U;
}

bool create_rectangle_geometry(
    const node& element,
    decoded_glyph& output,
    svg_path_requirements& requirements) {
    const float x = read_float(element, "x");
    const float y = read_float(element, "y");
    const float width = read_float(element, "width");
    const float height = read_float(element, "height");
    if (!std::isfinite(width) || !std::isfinite(height) ||
        width <= primitive_epsilon || height <= primitive_epsilon ||
        !std::isfinite(x) || !std::isfinite(y)) {
        return false;
    }
    const progpu_native_point p0{x, y};
    const progpu_native_point p1{x + width, y};
    const progpu_native_point p2{x + width, y + height};
    const progpu_native_point p3{x, y + height};
    append_line(output.segments, p0, p1);
    append_line(output.segments, p1, p2);
    append_line(output.segments, p2, p3);
    append_line(output.segments, p3, p0);
    requirements = {4U, x, y, x + width, y + height,
        PROGPU_NATIVE_FILL_RULE_NON_ZERO};
    return true;
}

bool create_ellipse_geometry(
    const node& element,
    bool circle,
    decoded_glyph& output,
    svg_path_requirements& requirements) {
    const float center_x = read_float(element, "cx");
    const float center_y = read_float(element, "cy");
    const float radius_x = read_float(element, circle ? "r" : "rx");
    const float radius_y = circle ? radius_x : read_float(element, "ry");
    if (!std::isfinite(center_x) || !std::isfinite(center_y) ||
        !std::isfinite(radius_x) || !std::isfinite(radius_y) ||
        radius_x <= primitive_epsilon || radius_y <= primitive_epsilon) {
        return false;
    }
    const progpu_native_point right{center_x + radius_x, center_y};
    const progpu_native_point bottom{center_x, center_y + radius_y};
    const progpu_native_point left{center_x - radius_x, center_y};
    const progpu_native_point top{center_x, center_y - radius_y};
    append_arc(output.segments, right, bottom, radius_x, radius_y);
    append_arc(output.segments, bottom, left, radius_x, radius_y);
    append_arc(output.segments, left, top, radius_x, radius_y);
    append_arc(output.segments, top, right, radius_x, radius_y);
    requirements = {4U, center_x - radius_x, center_y - radius_y,
        center_x + radius_x, center_y + radius_y,
        PROGPU_NATIVE_FILL_RULE_NON_ZERO};
    return true;
}

bool create_polygon_geometry(
    const node& element,
    decoded_glyph& output,
    svg_path_requirements& requirements) {
    const auto* points = find_attribute(element, "points");
    if (points == nullptr) {
        return false;
    }
    std::vector<float> values;
    if (!parse_number_list(*points, values) || values.size() < 6U ||
        (values.size() & 1U) != 0U) {
        return false;
    }
    float minimum_x = std::numeric_limits<float>::max();
    float minimum_y = std::numeric_limits<float>::max();
    float maximum_x = std::numeric_limits<float>::lowest();
    float maximum_y = std::numeric_limits<float>::lowest();
    const progpu_native_point first{values[0], values[1]};
    auto current = first;
    include(first, minimum_x, minimum_y, maximum_x, maximum_y);
    for (std::size_t index = 2U; index < values.size(); index += 2U) {
        const progpu_native_point next{values[index], values[index + 1U]};
        append_line(output.segments, current, next);
        include(next, minimum_x, minimum_y, maximum_x, maximum_y);
        current = next;
    }
    append_line(output.segments, current, first);
    requirements = {values.size() / 2U, minimum_x, minimum_y,
        maximum_x, maximum_y, PROGPU_NATIVE_FILL_RULE_NON_ZERO};
    return true;
}

bool create_geometry(
    const node& element,
    decoded_glyph& output,
    svg_path_requirements& requirements) {
    const auto name = local_name(element.name);
    if (name == "path") {
        return create_path_geometry(element, output, requirements);
    }
    if (name == "rect") {
        return create_rectangle_geometry(element, output, requirements);
    }
    if (name == "circle" || name == "ellipse") {
        return create_ellipse_geometry(
            element, name == "circle", output, requirements);
    }
    return name == "polygon" &&
        create_polygon_geometry(element, output, requirements);
}

render_state apply_state(
    const node& element,
    const render_state& parent) {
    auto local = identity_transform();
    if (const auto* transform = find_attribute(element, "transform")) {
        local = parse_transform(*transform);
    }
    if (local_name(element.name) == "use") {
        const float x = read_float(element, "x");
        const float y = read_float(element, "y");
        if (x != 0.0F || y != 0.0F) {
            auto translation = identity_transform();
            translation.m31 = x;
            translation.m32 = y;
            local = multiply(translation, local);
        }
    }
    render_state state = parent;
    state.transform = multiply(local, parent.transform);
    if (const auto* fill = find_attribute(element, "fill")) {
        state.fill = *fill;
    }
    state.opacity = parent.opacity *
        read_unit_interval(element, "opacity", 1.0F);
    if (find_attribute(element, "fill-opacity") != nullptr) {
        state.fill_opacity = read_unit_interval(
            element, "fill-opacity", parent.fill_opacity);
    }
    return state;
}

bool try_url_reference(std::string_view value, std::string& id) {
    while (!value.empty() && std::isspace(
        static_cast<unsigned char>(value.front())) != 0) {
        value.remove_prefix(1U);
    }
    while (!value.empty() && std::isspace(
        static_cast<unsigned char>(value.back())) != 0) {
        value.remove_suffix(1U);
    }
    if (value.size() < 7U ||
        !equals_ascii_ignore_case(value.substr(0U, 5U), "url(#") ||
        value.back() != ')') {
        return false;
    }
    value = value.substr(5U, value.size() - 6U);
    while (!value.empty() && std::isspace(
        static_cast<unsigned char>(value.front())) != 0) {
        value.remove_prefix(1U);
    }
    while (!value.empty() && std::isspace(
        static_cast<unsigned char>(value.back())) != 0) {
        value.remove_suffix(1U);
    }
    if (value.empty()) {
        return false;
    }
    id.assign(value);
    return true;
}

std::uint32_t spread_method(const node& gradient) noexcept {
    const auto* value = find_attribute(gradient, "spreadMethod");
    if (value != nullptr && equals_ascii_ignore_case(*value, "reflect")) {
        return PROGPU_NATIVE_SCENE_GRADIENT_REFLECT;
    }
    if (value != nullptr && equals_ascii_ignore_case(*value, "repeat")) {
        return PROGPU_NATIVE_SCENE_GRADIENT_REPEAT;
    }
    return PROGPU_NATIVE_SCENE_GRADIENT_PAD;
}

bool append_gradient_stops(
    const document& source,
    const node& gradient,
    float opacity,
    decoded_glyph& output,
    std::uint32_t& offset,
    std::uint32_t& count) {
    std::vector<progpu_native_scene_gradient_stop> stops;
    for (const auto child_index : gradient.children) {
        const auto& child = source.nodes[child_index];
        if (local_name(child.name) != "stop") {
            continue;
        }
        progpu_native_color color{};
        const auto* color_text = find_attribute(child, "stop-color");
        if (!try_parse_color(
                color_text == nullptr ? std::string_view{"black"} :
                    std::string_view{*color_text},
                color)) {
            continue;
        }
        color.a *= opacity * read_unit_interval(
            child, "stop-opacity", 1.0F);
        progpu_native_scene_gradient_stop stop{};
        stop.color = color;
        const auto* offset_text = find_attribute(child, "offset");
        node temporary{};
        if (offset_text != nullptr) {
            temporary.attributes.push_back({"offset", *offset_text});
        }
        stop.offset = read_unit_interval(temporary, "offset", 0.0F);
        stops.push_back(stop);
    }
    if (stops.empty()) {
        return false;
    }
    std::stable_sort(stops.begin(), stops.end(),
        [](const auto& left, const auto& right) {
            return left.offset < right.offset;
        });
    offset = static_cast<std::uint32_t>(output.gradient_stops.size());
    count = static_cast<std::uint32_t>(stops.size());
    output.gradient_stops.insert(
        output.gradient_stops.end(), stops.begin(), stops.end());
    return true;
}

void copy_inline_stops(
    progpu_native_scene_brush& brush,
    std::span<const progpu_native_scene_gradient_stop> stops) noexcept {
    const float defaults[8]{0.0F, 1.0F, 1.0F, 1.0F, 1.0F, 1.0F, 1.0F, 1.0F};
    std::copy(std::begin(defaults), std::begin(defaults) + 4,
        brush.offsets0);
    std::copy(std::begin(defaults) + 4, std::end(defaults), brush.offsets1);
    const auto count = std::min<std::size_t>(stops.size(), 8U);
    for (std::size_t index = 0U; index < count; ++index) {
        brush.colors[index] = stops[index].color;
        if (index < 4U) {
            brush.offsets0[index] = stops[index].offset;
        } else {
            brush.offsets1[index - 4U] = stops[index].offset;
        }
    }
}

bool create_gradient_brush(
    const document& source,
    const node& gradient,
    float opacity,
    const progpu_native_affine_2d& shape_transform,
    std::uint16_t units_per_em,
    decoded_glyph& output,
    std::uint32_t& brush_index) {
    const auto stop_start = output.gradient_stops.size();
    std::uint32_t stop_offset = 0U;
    std::uint32_t stop_count = 0U;
    if (!append_gradient_stops(source, gradient, opacity, output,
            stop_offset, stop_count)) {
        return false;
    }
    progpu_native_scene_brush brush{};
    brush.opacity = 1.0F;
    brush.stop_offset = stop_offset;
    brush.stop_count = stop_count;
    brush.spread_method = spread_method(gradient);
    brush.color_interpolation_mode =
        PROGPU_NATIVE_SCENE_GRADIENT_INTERPOLATE_SRGB;
    set_identity_brush_transform(brush);
    auto gradient_transform = identity_transform();
    if (const auto* text = find_attribute(gradient, "gradientTransform")) {
        gradient_transform = parse_transform(*text);
    }
    const auto transform = multiply(gradient_transform, shape_transform);
    const auto name = local_name(gradient.name);
    if (name == "linearGradient") {
        brush.type = PROGPU_NATIVE_SCENE_BRUSH_LINEAR_GRADIENT;
        brush.start_point = transform_point({
            read_coordinate(gradient, "x1", 0.0F, units_per_em),
            read_coordinate(gradient, "y1", 0.0F, units_per_em)}, transform);
        brush.end_point = transform_point({
            read_coordinate(gradient, "x2", units_per_em, units_per_em),
            read_coordinate(gradient, "y2", 0.0F, units_per_em)}, transform);
    } else if (name == "radialGradient") {
        brush.type = PROGPU_NATIVE_SCENE_BRUSH_RADIAL_GRADIENT;
        const float center_x = read_coordinate(
            gradient, "cx", units_per_em * 0.5F, units_per_em);
        const float center_y = read_coordinate(
            gradient, "cy", units_per_em * 0.5F, units_per_em);
        const float radius = read_coordinate(
            gradient, "r", units_per_em * 0.5F, units_per_em);
        const float origin_x = read_coordinate(
            gradient, "fx", center_x, units_per_em);
        const float origin_y = read_coordinate(
            gradient, "fy", center_y, units_per_em);
        brush.center = transform_point({center_x, center_y}, transform);
        brush.start_point = transform_point({origin_x, origin_y}, transform);
        const auto radius_x = transform_point(
            {center_x + radius, center_y}, transform);
        const auto radius_y = transform_point(
            {center_x, center_y + radius}, transform);
        brush.radius = std::hypot(
            radius_x.x - brush.center.x, radius_x.y - brush.center.y);
        brush.radius_y = std::hypot(
            radius_y.x - brush.center.x, radius_y.y - brush.center.y);
    } else {
        output.gradient_stops.resize(stop_start);
        return false;
    }
    copy_inline_stops(brush,
        std::span<const progpu_native_scene_gradient_stop>{
            output.gradient_stops}.subspan(stop_start));
    brush_index = static_cast<std::uint32_t>(output.brushes.size());
    output.brushes.push_back(brush);
    return true;
}

bool resolve_brush(
    const document& source,
    const render_state& state,
    std::uint16_t units_per_em,
    decoded_glyph& output,
    std::uint32_t& brush_index) {
    const float opacity = state.opacity * state.fill_opacity;
    if (opacity <= 0.0F || equals_ascii_ignore_case(state.fill, "none")) {
        return false;
    }
    std::string id;
    if (try_url_reference(state.fill, id)) {
        const auto found = source.ids.find(id);
        return found != source.ids.end() &&
            create_gradient_brush(source, source.nodes[found->second], opacity,
                state.transform, units_per_em, output, brush_index);
    }
    progpu_native_color color{};
    if (!try_parse_color(state.fill, color)) {
        return false;
    }
    color.a *= std::clamp(opacity, 0.0F, 1.0F);
    progpu_native_scene_brush brush{};
    brush.type = PROGPU_NATIVE_SCENE_BRUSH_SOLID;
    brush.opacity = 1.0F;
    brush.colors[0] = color;
    set_identity_brush_transform(brush);
    brush_index = static_cast<std::uint32_t>(output.brushes.size());
    output.brushes.push_back(brush);
    return true;
}

class renderer final {
public:
    renderer(
        const document& source,
        std::uint16_t units_per_em,
        decoded_glyph& output)
        : source_(source), units_per_em_(units_per_em), output_(output),
          active_(source.nodes.size(), false) {}

    void render_element(
        std::size_t element_index,
        const render_state& parent,
        std::size_t depth,
        bool allow_definition = false) {
        if (depth > maximum_reference_depth) {
            return;
        }
        const auto& element = source_.nodes[element_index];
        const auto name = local_name(element.name);
        if (name == "defs" && !allow_definition) {
            return;
        }
        const auto state = apply_state(element, parent);
        if (name == "svg" || name == "g" || name == "defs") {
            for (const auto child : element.children) {
                render_element(child, state, depth + 1U);
            }
        } else if (name == "use") {
            render_use(element, state, depth + 1U);
        } else if (name == "path" || name == "circle" ||
            name == "ellipse" || name == "rect" || name == "polygon") {
            render_shape(element, state);
        }
    }

private:
    void render_use(
        const node& element,
        const render_state& state,
        std::size_t depth) {
        const auto* href = find_attribute(element, "href");
        if (href == nullptr || href->size() < 2U || (*href)[0] != '#') {
            return;
        }
        const auto found = source_.ids.find(href->substr(1U));
        if (found == source_.ids.end() || active_[found->second]) {
            return;
        }
        active_[found->second] = true;
        render_element(found->second, state, depth, true);
        active_[found->second] = false;
    }

    void render_shape(const node& element, const render_state& state) {
        const auto segment_start = output_.segments.size();
        svg_path_requirements requirements{};
        if (!create_geometry(element, output_, requirements) ||
            output_.segments.size() == segment_start) {
            output_.segments.resize(segment_start);
            return;
        }
        if (const auto* fill_rule = find_attribute(element, "fill-rule");
            fill_rule != nullptr &&
            equals_ascii_ignore_case(*fill_rule, "evenodd")) {
            requirements.fill_rule = PROGPU_NATIVE_FILL_RULE_EVEN_ODD;
        }
        std::uint32_t brush_index = 0U;
        if (!resolve_brush(source_, state, units_per_em_, output_, brush_index)) {
            output_.segments.resize(segment_start);
            return;
        }
        svg_glyph_layer layer{};
        layer.segment_offset = segment_start;
        layer.segment_count = output_.segments.size() - segment_start;
        layer.minimum_x = requirements.minimum_x;
        layer.minimum_y = requirements.minimum_y;
        layer.maximum_x = requirements.maximum_x;
        layer.maximum_y = requirements.maximum_y;
        layer.transform = state.transform;
        layer.brush_index = brush_index;
        layer.fill_rule = requirements.fill_rule;
        output_.layers.push_back(layer);
    }

    const document& source_;
    std::uint16_t units_per_em_;
    decoded_glyph& output_;
    std::vector<bool> active_;
};

} // namespace

bool decode_glyph(
    std::string_view xml,
    std::uint16_t glyph_index,
    std::uint16_t units_per_em,
    decoded_glyph& result) noexcept {
    result = {};
    if (units_per_em == 0U) {
        return false;
    }
    try {
        document source{};
        if (!parse_document(xml, source)) {
            return false;
        }
        const std::string glyph_id = "glyph" + std::to_string(glyph_index);
        const auto found = source.ids.find(glyph_id);
        if (found == source.ids.end()) {
            return false;
        }
        render_state state{};
        std::vector<std::size_t> ancestors;
        auto parent = source.nodes[found->second].parent;
        while (parent != no_node) {
            ancestors.push_back(parent);
            parent = source.nodes[parent].parent;
        }
        for (auto iterator = ancestors.rbegin(); iterator != ancestors.rend();
             ++iterator) {
            state = apply_state(source.nodes[*iterator], state);
        }
        renderer instance{source, units_per_em, result};
        instance.render_element(found->second, state, 0U, true);
        return !result.layers.empty();
    } catch (...) {
        result = {};
        return false;
    }
}

} // namespace progpu::native::text::svg_document_detail
