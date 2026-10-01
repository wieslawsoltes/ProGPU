#include "../src/Mil/progpu_native_mil_hinted_glyphs.hpp"
#include "../src/Scene/progpu_native_semantic_state.hpp"
#include "progpu_native_hinted_shape_fixture.hpp"

#include <algorithm>
#include <array>
#include <cstring>
#include <iostream>
#include <limits>
#include <source_location>
#include <stdexcept>
#include <string>

// Original ProGPU fixture provenance: hinted_shape_fixture.hpp and the packet
// layouts/helpers in progpu_native_mil_tests.cpp. Expected geometry below is
// independently unpacked from the authored font's four ON-point raw slots.
// CPU semantic controls only: no device/view, pixels, Display or public ABI admission.
namespace {
#if defined(PROGPU_NATIVE_FONT_HINTING)
using namespace progpu::native::text;
using namespace progpu::native::mil;
using bytes = std::vector<std::byte>;
constexpr float dpi = 1.25F, em = 8.0F;
constexpr progpu_native_point origin{10.25F, 20.5F};
constexpr progpu_native_image_rect bounds{-10.0F, -20.0F, 100.0F, 60.0F};

void require(bool value, std::source_location at = std::source_location::current()) {
    if (!value) throw std::runtime_error("MIL hinted glyph control at " + std::to_string(at.line()));
}
template<class T> void append(bytes& out, const T& value) {
    const auto start = out.size(); out.resize(start + sizeof(T)); std::memcpy(out.data() + start, &value, sizeof(T));
}
template<class T> T read(std::span<const std::byte> in, std::size_t offset) {
    require(offset <= in.size() && sizeof(T) <= in.size() - offset);
    T value{}; std::memcpy(&value, in.data() + offset, sizeof(T)); return value;
}
template<class T> void write(bytes& out, std::size_t offset, const T& value) {
    require(offset <= out.size() && sizeof(T) <= out.size() - offset); std::memcpy(out.data() + offset, &value, sizeof(T));
}
void packet(bytes& out, const bytes& data) {
    const auto size = static_cast<std::uint32_t>((data.size() + 7U) & ~std::size_t{3U});
    append(out, size); out.insert(out.end(), data.begin(), data.end()); out.resize(out.size() + size - 4U - data.size());
}
template<class... T> void cmd(bytes& out, command kind, const T&... values) {
    bytes data; append(data, static_cast<std::uint32_t>(kind)); (append(data, values), ...); packet(out, data);
}
void content(bytes& out, const bytes& drawing) {
    bytes data; append(data, static_cast<std::uint32_t>(command::render_data)); append(data, 2U);
    append(data, static_cast<std::uint32_t>(drawing.size())); data.insert(data.end(), drawing.begin(), drawing.end()); packet(out, data);
}
bool equal(progpu_native_point a, progpu_native_point b) { return a.x == b.x && a.y == b.y; }
bool equal(progpu_native_color a, progpu_native_color b) { return a.r == b.r && a.g == b.g && a.b == b.b && a.a == b.a; }
void equal_bytes(std::span<const std::byte> scene, std::size_t offset, std::size_t size,
    const void* expected, std::size_t expected_size) {
    require(size == expected_size && offset <= scene.size() && size <= scene.size() - offset);
    require(size == 0U || std::memcmp(scene.data() + offset, expected, size) == 0);
}

struct fixture final {
    bytes font = progpu::native::tests::make_hinted_shape_font();
    progpu_native_text_context* context = nullptr;
    std::vector<progpu_native_text_scalar> input;
    std::array<progpu_native_text_feature, 2U> features{{{0x6C696761U, 1U, 0U, UINT32_MAX}, {0x6B65726EU, 1U, 0U, UINT32_MAX}}};
    std::array<progpu_native_text_style_run, 3U> styles{};
    std::array<progpu_native_text_style_metrics, 3U> metrics{{{5.5F, 1.75F}, {5.5F, 1.75F}, {5.5F, 1.75F}}};
    std::array<hinted_paragraph_style_configuration, 3U> devices{};
    progpu_native_text_shape_request shaping{};
    progpu_native_text_layout_options layout{};
    fixture(font_hint_policy policy, bool rtl) {
        require(progpu_native_text_context_create(PROGPU_NATIVE_ABI_VERSION,
            reinterpret_cast<const std::uint8_t*>(font.data()), font.size(), 0U, nullptr, 0U, &context) == PROGPU_NATIVE_STATUS_SUCCESS);
        std::uint32_t fallback = UINT32_MAX;
        require(progpu_native_text_context_add_fallback_font(context, reinterpret_cast<const std::uint8_t*>(font.data()),
            font.size(), 0U, 0x7799U, &fallback) == PROGPU_NATIVE_STATUS_SUCCESS && fallback == 1U);
        std::uint32_t source = 9U;
        const auto scalar = [&](std::uint32_t value) {
            const auto length = value == 'A' ? std::uint16_t{2U} : std::uint16_t{1U};
            input.push_back({value, source, length, 0U, 0U, 0U}); source += length;
        };
        for (std::uint32_t style = 0U; style < 3U; ++style) {
            const auto first = static_cast<std::uint32_t>(input.size());
            if (rtl) scalar(0x202EU); // Actual RLO/PDF bidi ownership, not a relabeled LTR snapshot.
            scalar('A'); if (style != 2U) scalar('B');
            if (rtl) scalar(0x202CU);
            if (style != 2U) scalar(0x03A9U); // Authored missing glyph zero: retained owner, no ink.
            const auto font_index = style == 2U ? 1U : 0U;
            styles[style] = {first, static_cast<std::uint32_t>(input.size()) - first, font_index, em / 1000.0F,
                0U, 2U, 0U, 0U, 0U, 0U, 0U};
            devices[style] = {font_index, styles[style].scale,
                {10U * 64U, 10U * 64U, policy, 7U + style * 12U, 11U + style * 12U, {}}, 0.8F};
        }
        shaping.struct_size = sizeof(shaping); shaping.abi_version = PROGPU_NATIVE_ABI_VERSION;
        shaping.input = input.data(); shaping.input_count = static_cast<std::uint32_t>(input.size());
        shaping.features = features.data(); shaping.feature_count = static_cast<std::uint32_t>(features.size());
        shaping.direction = rtl ? PROGPU_NATIVE_TEXT_DIRECTION_RIGHT_TO_LEFT : PROGPU_NATIVE_TEXT_DIRECTION_LEFT_TO_RIGHT;
        layout.struct_size = sizeof(layout); layout.scale = 1.0F; layout.maximum_width = 200.0F; layout.direction = shaping.direction;
    }
    ~fixture() { progpu_native_text_context_destroy(context); }
    fixture(const fixture&) = delete;
    fixture& operator=(const fixture&) = delete;
    std::shared_ptr<const hinted_paragraph_generation> format() {
        std::shared_ptr<const hinted_paragraph_generation> result;
        progpu_native_text_paragraph_result diagnostic{}; diagnostic.struct_size = sizeof(diagnostic);
        require(try_layout_context_hinted_paragraph(context, shaping, layout, styles, metrics, devices, result, diagnostic)
            == PROGPU_NATIVE_STATUS_SUCCESS && result != nullptr); return result;
    }
};

struct expected final {
    std::vector<progpu_native_scene_glyph_outline> outlines;
    std::vector<progpu_native_path_segment> segments;
    std::vector<std::uint32_t> selected, draw_outlines;
    std::vector<std::uint16_t> ids;
    std::vector<progpu_native_point> positions, draw_positions;
    std::uint32_t other_font = UINT32_MAX;
};
expected unpack(const hinted_paragraph_glyph_resource& resource, bool rtl) {
    const auto& p = *resource.paragraph(); expected result;
    std::vector<std::vector<std::uint32_t>> descriptors(p.runs.size());
    std::size_t noink = 0U; bool odd = false, reversed = false, different_phases = false;
    std::vector<hinted_outline_point> first_points;
    for (std::size_t r = 0U; r < p.runs.size(); ++r) {
        const auto& run = p.runs[r]; const auto& raw_run = *run.generation;
        odd |= (run.bidi_level & 1U) != 0U;
        reversed |= (run.bidi_level & 1U) != 0U && raw_run.descriptor_indices.size() >= 2U &&
            raw_run.descriptor_indices.front() > raw_run.descriptor_indices.back();
        require(run.source_scale == em / 1000.0F && raw_run.batch->identity->device_frame.units_per_em == 1000U);
        for (std::size_t d = 0U; d < raw_run.source_descriptor_count; ++d) {
            const auto& raw = raw_run.batch->glyphs[d]; auto index = hinted_no_outline;
            if (!raw.points.empty()) {
                require(raw.points.size() == 4U && raw.tags.size() == 4U && raw.contour_ends == std::vector<std::int16_t>{3});
                for (const auto tag : raw.tags) require((tag & 3U) == 1U);
                if (run.font_index == 0U && run.style_index == 0U) first_points = raw.points;
                if (run.font_index == 0U && run.style_index == 1U && raw.glyph_index == 1U)
                    different_phases |= !first_points.empty() && first_points != raw.points;
                const auto point = [&](std::size_t at) { return progpu_native_point{
                    static_cast<float>(raw.points[at].x_26_6) / 64.0F, static_cast<float>(raw.points[at].y_26_6) / 64.0F}; };
                auto lo = point(0U), hi = lo;
                for (std::size_t j = 0U; j < 4U; ++j) {
                    const auto q = point(j); lo.x = std::min(lo.x, q.x); lo.y = std::min(lo.y, q.y);
                    hi.x = std::max(hi.x, q.x); hi.y = std::max(hi.y, q.y);
                }
                index = static_cast<std::uint32_t>(result.outlines.size());
                result.outlines.push_back({result.segments.size(), 4U, lo.x, lo.y, hi.x, hi.y, 1.0F, 0.0F});
                require(resource.outline_owners()[index] == hinted_paragraph_outline_owner{static_cast<std::uint32_t>(r), static_cast<std::uint32_t>(d)});
                for (std::size_t j = 0U; j < 4U; ++j) {
                    progpu_native_path_segment edge{}; edge.kind = PROGPU_NATIVE_PATH_SEGMENT_LINE;
                    edge.p0 = point(j); edge.p1 = point((j + 1U) % 4U); result.segments.push_back(edge);
                }
            } else { require(raw.tags.empty() && raw.contour_ends.empty()); ++noink; }
            descriptors[r].push_back(index);
        }
    }
    require(noink != 0U && different_phases && (!rtl || (odd && reversed)));
    require(resource.positioned_owners().size() == p.glyphs.size() && resource.positioned_outline_indices().size() == p.glyphs.size());
    std::size_t selected_noink = 0U, repeated = 0U;
    for (std::size_t i = 0U; i < p.glyphs.size(); ++i) {
        const auto& glyph = p.glyphs[i]; const auto owner = p.positioned_owners[i]; const auto& run = p.runs[owner.run_index];
        require(owner == p.logical_owners[glyph.glyph_index]);
        require(run.generation->descriptor_indices[owner.run_glyph_index] == owner.descriptor_index);
        const auto outline = descriptors[owner.run_index][owner.descriptor_index];
        require(resource.positioned_outline_indices()[i] == outline);
        require(resource.positioned_owners()[i] == hinted_paragraph_draw_owner{static_cast<std::uint32_t>(i), glyph.glyph_index,
            owner.run_index, owner.run_glyph_index, owner.descriptor_index, run.font_index, run.style_index});
        if (run.font_index != 0U) { result.other_font = static_cast<std::uint32_t>(i); continue; }
        for (const auto previous : result.ids) if (previous == glyph.glyph_id && outline != hinted_no_outline) ++repeated;
        result.selected.push_back(static_cast<std::uint32_t>(i)); result.ids.push_back(static_cast<std::uint16_t>(glyph.glyph_id));
        result.positions.push_back({glyph.x + origin.x, glyph.y + origin.y});
        if (outline == hinted_no_outline) { ++selected_noink; continue; }
        result.draw_outlines.push_back(outline); result.draw_positions.push_back(result.positions.back());
    }
    require(selected_noink != 0U && repeated != 0U && result.other_font != UINT32_MAX && result.draw_outlines.size() >= 4U);
    require(result.outlines.size() == resource.outlines().size() && result.segments.size() == resource.segments().size());
    return result; // Value geometry/identity controls only; never copy a paragraph generation.
}

void glyph_packet(bytes& batch, std::uint32_t handle, const expected& e, progpu_native_image_rect source_bounds = bounds,
    float size = em, std::uint16_t flags = 0x10U) {
    bytes data(76U); write(data, 0U, static_cast<std::uint32_t>(command::glyph_run_create)); write(data, 4U, handle);
    write(data, 16U, flags); write(data, 28U, size);
    write(data, 32U, static_cast<double>(source_bounds.x)); write(data, 40U, static_cast<double>(source_bounds.y));
    write(data, 48U, static_cast<double>(source_bounds.width)); write(data, 56U, static_cast<double>(source_bounds.height));
    write(data, 64U, static_cast<std::uint16_t>(e.ids.size()));
    for (const auto id : e.ids) append(data, id);
    for (std::size_t i = 0U; i < e.ids.size(); ++i) append(data, 0.0F);
    for (const auto position : e.positions) append(data, position);
    packet(batch, data); // Zero advances, literal source offsets: no extra float prefix arithmetic.
}
void source_scene(channel& state, const expected& e, std::span<const std::byte> font) {
    bytes batch, drawing;
    for (const auto item : std::array<std::array<std::uint32_t, 2U>, 7U>{{{1U,39U},{2U,43U},{3U,47U},{4U,75U},{6U,66U},{7U,69U},{8U,77U}}})
        cmd(batch, command::channel_create_resource, item[0], item[1]);
    cmd(batch, command::visual_create, 1U); cmd(batch, command::visual_set_content, 1U, 2U);
    cmd(batch, command::solid_color_brush, 4U, 0.75, progpu_native_color{0.2F,0.4F,0.8F,1.0F}, 0U,0U,0U,0U);
    cmd(batch, command::matrix_transform, 6U, 1.0,0.0,0.0,1.0,3.5,-2.25,0U);
    cmd(batch, command::rectangle_geometry, 7U, 0.0,0.0, -10.0,-20.0,100.0,60.0, 0U,0U,0U,0U);
    cmd(batch, command::linear_gradient_brush, 8U, 1.0,0.0,0.0,1.0,0.0,
        0U,0U,0U,0U,1U,0U,48U,0U,0U, 0.0,progpu_native_color{1,0,0,1}, 1.0,progpu_native_color{0,0,1,1});
    glyph_packet(batch, 5U, e);
    cmd(drawing, command::push_transform, 6U,0U); cmd(drawing, command::push_clip, 7U,0U); cmd(drawing, command::push_opacity, 0.5);
    cmd(drawing, command::draw_glyph_run, 4U,5U); cmd(drawing, command::draw_glyph_run, 8U,5U);
    for (std::uint32_t i = 0U; i < 3U; ++i) cmd(drawing, command::pop);
    content(batch, drawing); cmd(batch, command::generic_target_create, 3U, std::uint64_t{0},std::uint64_t{0},160U,120U,0U);
    cmd(batch, command::target_set_root, 3U,1U);
    require(state.apply(batch) == status::success && state.set_glyph_run_font_sfnt(5U,0U,0U,font) == status::success);
}
void verify_scene(std::span<const std::byte> scene, const expected& e, bool coverage = false) {
    const auto h = read<progpu_native_scene_header>(scene, 0U);
    require(h.struct_size == sizeof(h) && h.total_size == scene.size() && h.command_stride == sizeof(progpu_native_scene_command) &&
        h.resource_stride == sizeof(progpu_native_scene_resource));
    const auto resource = [&](std::uint32_t index) { require(index < h.resource_count);
        return read<progpu_native_scene_resource>(scene, h.resource_offset + index * h.resource_stride); };
    std::size_t glyph_draws = 0U, pictures = 0U, gradients = 0U, materials = 0U, source_opacities = 0U;
    std::vector<float> layer_opacities{1.0F};
    progpu::native::semantic::semantic_state_cursor cursor(scene.data(), h, dpi);
    for (std::uint32_t i = 0U; i < h.command_count; ++i) {
        const auto c = read<progpu_native_scene_command>(scene, h.command_offset + i * h.command_stride); const auto state = cursor.advance(c);
        if (c.kind == PROGPU_NATIVE_SCENE_COMMAND_PUSH_LAYER) {
            const auto layer = read<progpu_native_scene_layer>(scene,c.payload_offset);
            require(layer.struct_size == sizeof(layer) && layer.blend_mode == PROGPU_NATIVE_BLEND_SRC_OVER);
            layer_opacities.push_back(layer_opacities.back()*layer.opacity);
            if (layer.opacity == 0.5F) {
                require(!coverage && layer.mask_resource_index == PROGPU_NATIVE_SCENE_NO_INDEX && layer.effect_resource_index == PROGPU_NATIVE_SCENE_NO_INDEX);
                ++source_opacities;
            } else require(layer.opacity == 1.0F);
            continue;
        }
        if (c.kind == PROGPU_NATIVE_SCENE_COMMAND_POP_LAYER) { require(layer_opacities.size() > 1U); layer_opacities.pop_back(); continue; }
        if (c.kind == PROGPU_NATIVE_SCENE_COMMAND_DRAW_ANALYTIC) {
            require(!coverage && layer_opacities.back() == 0.5F && state.opacity == 1.0F && state.transform.m31 == 0.0F && state.transform.m32 == 0.0F);
            require((state.flags & PROGPU_NATIVE_SCENE_STATE_CLIP_RECT) != 0U && state.clip_rect.x == -6.5F && state.clip_rect.y == -22.25F);
            ++materials; continue;
        }
        if (c.kind != PROGPU_NATIVE_SCENE_COMMAND_DRAW_GLYPH_RUN) continue;
        require((c.flags & PROGPU_NATIVE_SCENE_GLYPH_STYLED) != 0U && !cursor.has_per_point_guidelines(state));
        require(state.transform.m11 == 1.0F && state.transform.m12 == 0.0F && state.transform.m21 == 0.0F && state.transform.m22 == 1.0F);
        require(state.transform.m31 == 3.5F && state.transform.m32 == -2.25F && state.opacity == 1.0F);
        require(layer_opacities.back() == (coverage ? 1.0F : 0.5F));
        require(((state.flags & PROGPU_NATIVE_SCENE_STATE_CLIP_RECT) != 0U) == !coverage);
        if (!coverage) require(state.clip_rect.x == -6.5F && state.clip_rect.y == -22.25F && state.clip_rect.width == 100.0F && state.clip_rect.height == 60.0F);
        const auto r = resource(c.resource_index); require(r.kind == PROGPU_NATIVE_SCENE_RESOURCE_GLYPH_RUN);
        equal_bytes(scene,r.payload_offset,r.payload_size,e.outlines.data(),e.outlines.size()*sizeof(e.outlines[0]));
        equal_bytes(scene,r.auxiliary_offset,r.auxiliary_size,e.segments.data(),e.segments.size()*sizeof(e.segments[0]));
        const auto draw = read<progpu_native_scene_glyph_draw>(scene,c.payload_offset);
        require(draw.glyph_count == e.draw_outlines.size() && c.payload_size == sizeof(draw) + draw.glyph_count*sizeof(progpu_native_positioned_glyph));
        const auto styles = resource(draw.style_resource_index); require(styles.kind == PROGPU_NATIVE_SCENE_RESOURCE_TEXT_STYLE_TABLE);
        const auto style = read<progpu_native_scene_text_style>(scene,styles.payload_offset + draw.style_index*sizeof(progpu_native_scene_text_style));
        require(equal(style.color, coverage ? progpu_native_color{1,1,1,1} : progpu_native_color{0.2F,0.4F,0.8F,0.75F}));
        require(style.text_rendering_mode == PROGPU_NATIVE_SCENE_TEXT_GRAYSCALE);
        for (std::size_t j = 0U; j < e.draw_outlines.size(); ++j) {
            const auto g = read<progpu_native_positioned_glyph>(scene,c.payload_offset + sizeof(draw) + j*sizeof(progpu_native_positioned_glyph));
            require(g.outline_index == e.draw_outlines[j] && equal(g.position,e.draw_positions[j]));
            require(equal(g.basis_x,{1,0}) && equal(g.basis_y,{0,1}) && equal(g.color,{1,1,1,1}));
            require(g.atlas_to_logical_scale == 1.0F && g.bold_offset == 0.0F && g.italic_skew == 0.0F && g.reserved == 0U && g.reserved2 == 0.0F);
        }
        ++glyph_draws;
    }
    for (std::uint32_t i = 0U; i < h.resource_count; ++i) {
        const auto r = resource(i);
        if (r.kind == PROGPU_NATIVE_SCENE_RESOURCE_LAYER_MASK) {
            const auto mask = read<progpu_native_scene_layer_picture_mask>(scene,r.payload_offset);
            require(!coverage && mask.kind == PROGPU_NATIVE_SCENE_LAYER_MASK_PICTURE && mask.stream_offset == 0U && mask.stream_size == r.auxiliary_size);
            require(mask.bounds.x == -6.5F && mask.bounds.y == -22.25F && mask.bounds.width == 100.0F && mask.bounds.height == 60.0F);
            require(r.auxiliary_offset <= scene.size() && r.auxiliary_size <= scene.size() - r.auxiliary_offset);
            verify_scene(scene.subspan(r.auxiliary_offset,r.auxiliary_size),e,true); ++pictures;
        } else if (r.kind == PROGPU_NATIVE_SCENE_RESOURCE_BRUSH_TABLE) {
            const auto brush = read<progpu_native_scene_brush>(scene,r.payload_offset);
            require(!coverage && brush.type == PROGPU_NATIVE_SCENE_BRUSH_LINEAR_GRADIENT && brush.stop_count == 2U && brush.opacity == 1.0F);
            const auto start = read<progpu_native_scene_gradient_stop>(scene,r.auxiliary_offset + brush.stop_offset*sizeof(progpu_native_scene_gradient_stop));
            const auto end = read<progpu_native_scene_gradient_stop>(scene,r.auxiliary_offset + (brush.stop_offset+1U)*sizeof(progpu_native_scene_gradient_stop));
            require(equal(start.color,{1,0,0,1}) && equal(end.color,{0,0,1,1}) && start.offset == 0.0F && end.offset == 1.0F); ++gradients;
        }
    }
    require(layer_opacities.size() == 1U && source_opacities == (coverage ? 0U : 1U));
    require(glyph_draws == 1U && pictures == (coverage ? 0U : 1U) && gradients == (coverage ? 0U : 1U) && materials == (coverage ? 0U : 1U));
}

void controls(font_hint_policy policy, bool rtl) {
    std::weak_ptr<const hinted_paragraph_generation> weak_paragraph;
    std::weak_ptr<const hinted_paragraph_glyph_resource> weak_resource;
    {
        channel state; expected e;
        {
            fixture source(policy,rtl); auto paragraph = source.format();
            const auto packed = create_hinted_paragraph_glyph_resource(paragraph,dpi,hinted_projection_policy::scalar_reference,hinted_outline_coverage::nonzero_vector);
            require(packed.status == PROGPU_NATIVE_STATUS_SUCCESS && packed.generation != nullptr);
            auto retained = packed.generation; e = unpack(*retained,rtl); source_scene(state,e,source.font);
            weak_paragraph = paragraph; weak_resource = retained;
            const auto before = state.resource_generation(5U);
            require(hinted_glyph_binding_access::bind(state,5U,retained,e.selected,0U,dpi,origin) == status::success);
            require(state.resource_generation(5U) == before + 1U);
            std::fill(source.font.begin(),source.font.end(),std::byte{0}); source.input.clear(); source.styles = {};
        } // Caller buffers, context and caller paragraph/resource references retire before compilation.
        require(!weak_paragraph.expired() && !weak_resource.expired());
        scene_build_request request{}; request.target_handle = 3U; request.scene_id = 17001U;
        request.generation = request.request_serial = 1U; request.dpi_scale_x = request.dpi_scale_y = dpi;
        std::span<const std::byte> compiled; require(state.build_scene(request,compiled) == status::success);
        const bytes previous(compiled.begin(),compiled.end()); verify_scene(previous,e);
        auto retained = weak_resource.lock(); const auto generation = state.resource_generation(5U); const auto* cached = compiled.data();
        const auto rejected = [&](std::span<const std::uint32_t> selection, std::uint32_t font, float scale,
            progpu_native_affine_2d basis = {1,0,0,1,0,0}, status expected_status = status::invalid_argument) {
            require(hinted_glyph_binding_access::bind(state,5U,retained,selection,font,scale,origin,basis) == expected_status);
            require(state.resource_generation(5U) == generation && state.build_scene(request,compiled) == status::success && compiled.data() == cached);
            equal_bytes(compiled,0U,compiled.size(),previous.data(),previous.size());
        };
        auto bad = e.selected; bad.back() = UINT32_MAX; rejected(bad,0U,dpi); // Late index, after valid earlier slots.
        bad = e.selected; bad.back() = e.other_font; rejected(bad,0U,dpi); // Same bytes/em, wrong original owner.
        bad = e.selected;
        const auto unlike = std::find_if(e.ids.begin(),e.ids.end(),[&](auto id) { return id != e.ids.back(); }); require(unlike != e.ids.end());
        bad.back() = e.selected[static_cast<std::size_t>(unlike - e.ids.begin())]; rejected(bad,0U,dpi); // Late ID mismatch.
        rejected(std::span(e.selected).first(e.selected.size()-1U),0U,dpi);
        rejected(e.selected,1U,dpi); rejected(e.selected,UINT32_MAX,dpi); rejected(e.selected,0U,2.0F); rejected(e.selected,0U,0.0F);
        rejected(e.selected,0U,dpi,{1,0,0,1,1,0},status::unsupported_command);
        rejected(e.selected,0U,dpi,{2,0,0,1,0,0},status::unsupported_command);
        require(hinted_glyph_binding_access::bind(state,5U,nullptr,e.selected,0U,dpi,origin) == status::invalid_argument);
        require(state.resource_generation(5U) == generation && state.build_scene(request,compiled) == status::success && compiled.data() == cached);
        equal_bytes(compiled,0U,compiled.size(),previous.data(),previous.size());

        // Independent raw-slot ink admission: candidate rectangle fits every earlier
        // ink right edge but excludes the final greatest edge. It cannot be enlarged.
        std::vector<float> rights;
        for (std::size_t i = 0U; i < e.draw_outlines.size(); ++i)
            rights.push_back(e.draw_positions[i].x + e.outlines[e.draw_outlines[i]].max_x / dpi);
        require(rights.back() > *std::max_element(rights.begin(),rights.end()-1));
        const float edge = (rights.back() + *std::max_element(rights.begin(),rights.end()-1)) * 0.5F;
        bytes update; glyph_packet(update,9U,e,{bounds.x,bounds.y,edge-bounds.x,bounds.height});
        require(state.apply(update) == status::success);
        const auto& font = retained->paragraph()->font_sources[0U]->bytes;
        require(state.set_glyph_run_font_sfnt(9U,0U,0U,font) == status::success);
        require(state.build_scene(request,compiled) == status::success); const auto* candidate_cache = compiled.data();
        const bytes candidate_scene(compiled.begin(),compiled.end()); const auto candidate_generation = state.resource_generation(9U);
        require(hinted_glyph_binding_access::bind(state,9U,retained,e.selected,0U,dpi,origin) == status::invalid_argument);
        require(state.resource_generation(9U) == candidate_generation && state.build_scene(request,compiled) == status::success && compiled.data() == candidate_cache);
        equal_bytes(compiled,0U,compiled.size(),candidate_scene.data(),candidate_scene.size()); verify_scene(compiled,e);
        auto wrong_dpi = request; wrong_dpi.request_serial = 2U; wrong_dpi.dpi_scale_x = wrong_dpi.dpi_scale_y = 2.0;
        require(state.build_scene(wrong_dpi,compiled) == status::unsupported_command);
        require(compiled.empty()); // A different serial reaches the hinted DPI gate, not request-cache validation.

        // Binding identity alone cannot admit a later source transform. These
        // real resource updates must reach compilation and publish no scene.
        const auto bound_generation = state.resource_generation(5U);
        for (const auto linear : std::array<std::array<double,4U>,2U>{{{2.0,0.0,0.0,1.0},{1.0,0.25,0.0,1.0}}}) {
            bytes transformed;
            cmd(transformed,command::matrix_transform,6U,linear[0],linear[1],linear[2],linear[3],3.5,-2.25,0U);
            require(state.apply(transformed) == status::success);
            require(state.resource_generation(5U) == bound_generation && !weak_paragraph.expired() && !weak_resource.expired());
            require(state.build_scene(request,compiled) == status::unsupported_command && compiled.empty());
            bytes restored; cmd(restored,command::matrix_transform,6U,1.0,0.0,0.0,1.0,3.5,-2.25,0U);
            require(state.apply(restored) == status::success && state.build_scene(request,compiled) == status::success);
            verify_scene(compiled,e);
        }
        bytes guidelines;
        cmd(guidelines,command::visual_set_guideline_collection,1U,
            std::uint16_t{1U},std::uint16_t{0U},std::uint16_t{1U},std::uint16_t{0U},10.25F,20.5F);
        require(state.apply(guidelines) == status::success);
        require(state.resource_generation(5U) == bound_generation && !weak_paragraph.expired() && !weak_resource.expired());
        require(state.build_scene(request,compiled) == status::unsupported_command && compiled.empty());
        bytes no_guidelines;
        cmd(no_guidelines,command::visual_set_guideline_collection,1U,
            std::uint16_t{0U},std::uint16_t{0U},std::uint16_t{0U},std::uint16_t{0U});
        require(state.apply(no_guidelines) == status::success && state.build_scene(request,compiled) == status::success);
        verify_scene(compiled,e);

        // A successful, byte-distinct SFNT sideband replacement explicitly
        // retires hinted ownership, even while the channel and glyph run live.
        const auto replacement = progpu::native::tests::make_hinted_mapping_font();
        require(replacement != font);
        retained.reset(); require(!weak_paragraph.expired() && !weak_resource.expired());
        require(state.set_glyph_run_font_sfnt(5U,0U,0U,replacement) == status::success);
        require(state.resource_generation(5U) == bound_generation + 1U);
        require(weak_paragraph.expired() && weak_resource.expired());
        require(state.build_scene(request,compiled) == status::success && !compiled.empty());
        require(state.build_scene(wrong_dpi,compiled) == status::success && !compiled.empty()); // Original design-font DPI path, no stale hinted binding.
    }
    require(weak_paragraph.expired() && weak_resource.expired());
}
#endif
} // namespace

int main() {
    try {
#if defined(PROGPU_NATIVE_FONT_HINTING)
        for (const auto policy : {font_hint_policy::truetype_35,font_hint_policy::truetype_40})
            for (const bool rtl : {false,true}) controls(policy,rtl);
#endif
        std::cout << "MIL retained hinted glyph semantic controls passed\n"; return 0;
    } catch (const std::exception& failure) { std::cerr << failure.what() << '\n'; return 1; }
}
