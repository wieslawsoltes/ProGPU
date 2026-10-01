#include "../include/progpu_native_text_hinting.h"
#include "../src/Text/Interop/progpu_native_hinted_paragraph_transport_internal.hpp"
#include "progpu_native_hinted_shape_fixture.hpp"
#include "progpu_native_hinted_variable_font_fixture.hpp"

#include <algorithm>
#include <array>
#include <cstring>
#include <iostream>
#include <limits>
#include <memory>
#include <source_location>
#include <stdexcept>
#include <string>
#include <type_traits>
#include <vector>

// Original producer controls only: retained CPU geometry/format/interaction,
// failure-atomic C publication and the originating-library borrow lifetime.
// No target, engine, GPU, source Display or package admission is fabricated.
namespace {
using namespace progpu::native::text;

void require(bool value, std::source_location at = std::source_location::current()) {
    if (!value) throw std::runtime_error("hinted glyph resource transport control at " + std::to_string(at.line()));
}
template<class T> bool same_bytes(const T& a, const T& b) {
    static_assert(std::is_trivially_copyable_v<T>);
    return std::memcmp(&a, &b, sizeof(T)) == 0;
}
template<class T> bool same_span(std::span<const T> a, std::span<const T> b) {
    static_assert(std::is_trivially_copyable_v<T>);
    return a.size() == b.size() && (a.empty() || std::memcmp(a.data(), b.data(), a.size_bytes()) == 0);
}
template<class T> struct tailed final { T value{}; std::array<std::byte, 32U> tail{}; };
template<class T> tailed<T> sentinel() {
    static_assert(std::is_trivially_copyable_v<tailed<T>>);
    tailed<T> result{}; std::memset(static_cast<void*>(&result), 0x71, sizeof(result)); return result;
}

#define RESOURCE_SPANS(X) \
    X(font_sources, progpu_native_hinted_glyph_font_source, v.font_source_count) \
    X(font_bytes, std::uint8_t, v.font_byte_count) \
    X(device_styles, progpu_native_hinted_paragraph_device_style, v.counts.style_count) \
    X(variation_coordinates_16_16, std::int32_t, v.variation_coordinate_count) \
    X(normalized_coordinates, std::int16_t, v.normalized_coordinate_count) \
    X(outlines, progpu_native_glyph_outline, v.outline_count) \
    X(segments, progpu_native_path_segment, v.segment_count) \
    X(run_slices, progpu_native_hinted_glyph_run_slice, v.counts.run_count) \
    X(source_outline_indices, std::uint32_t, v.source_outline_count) \
    X(run_outline_indices, std::uint32_t, v.run_outline_count) \
    X(outline_owners, progpu_native_hinted_glyph_outline_owner, v.outline_count) \
    X(positioned_outline_indices, std::uint32_t, v.counts.positioned_glyph_count) \
    X(source_scalars, progpu_native_text_scalar, v.counts.source_scalar_count) \
    X(admitted_scalars, progpu_native_text_scalar, v.counts.admitted_scalar_count) \
    X(scalar_levels, progpu_native_text_bidi_level, v.counts.admitted_scalar_count) \
    X(styles, progpu_native_text_style_run, v.counts.style_count) \
    X(source_metrics, progpu_native_text_style_metrics, v.counts.style_count) \
    X(runs, progpu_native_hinted_paragraph_run, v.counts.run_count) \
    X(logical_glyphs, progpu_native_text_shaping_glyph, v.counts.logical_glyph_count) \
    X(logical_owners, progpu_native_hinted_paragraph_glyph_owner, v.counts.logical_glyph_count) \
    X(logical_cluster_ends, std::int32_t, v.counts.logical_glyph_count) \
    X(logical_bidi_levels, std::int8_t, v.counts.logical_glyph_count) \
    X(glyph_scales, float, v.counts.logical_glyph_count) \
    X(positioned_glyphs, progpu_native_positioned_text_glyph, v.counts.positioned_glyph_count) \
    X(positioned_owners, progpu_native_hinted_paragraph_glyph_owner, v.counts.positioned_glyph_count) \
    X(positioned_cluster_ends, std::int32_t, v.counts.positioned_glyph_count) \
    X(positioned_bidi_levels, std::int8_t, v.counts.positioned_glyph_count) \
    X(lines, progpu_native_positioned_text_line, v.counts.line_count) \
    X(line_origins, float, v.counts.line_count) \
    X(boxes, progpu_native_text_cluster_box, v.counts.cluster_box_count) \
    X(carets, progpu_native_text_caret_stop, v.counts.caret_stop_count) \
    X(pre_context, progpu_native_text_scalar, v.pre_context_count) \
    X(post_context, progpu_native_text_scalar, v.post_context_count) \
    X(features, progpu_native_text_feature, v.feature_count)

struct saved_borrow final {
    progpu_native_hinted_glyph_resource_view header;
#define DECLARE(name, type, count) std::vector<type> name;
    RESOURCE_SPANS(DECLARE)
#undef DECLARE
    explicit saved_borrow(const progpu_native_hinted_glyph_resource_view& v) : header{} {
        std::memcpy(&header, &v, sizeof(v));
#define COPY(name, type, count) if ((count) != 0U) name.assign(v.name, v.name + (count));
        RESOURCE_SPANS(COPY)
#undef COPY
    }
    void unchanged(const progpu_native_hinted_glyph_resource_view& v) const {
        require(same_bytes(header, v)); // Repeated borrow keeps all original addresses and metadata.
#define COMPARE(name, type, count) require(same_span<type>({v.name, (count)}, name));
        RESOURCE_SPANS(COMPARE)
#undef COMPARE
    }
};

#if defined(PROGPU_NATIVE_FONT_HINTING)
struct context_owner final {
    progpu_native_text_context* value = nullptr;
    context_owner() = default;
    context_owner(const context_owner&) = delete;
    context_owner& operator=(const context_owner&) = delete;
    ~context_owner() { progpu_native_text_context_destroy(value); }
};
struct paragraph_owner final {
    progpu_native_hinted_paragraph* value = nullptr;
    paragraph_owner() = default;
    paragraph_owner(const paragraph_owner&) = delete;
    paragraph_owner& operator=(const paragraph_owner&) = delete;
    ~paragraph_owner() { progpu_native_hinted_paragraph_destroy(value); }
};
struct resource_owner final {
    progpu_native_hinted_glyph_resource* value = nullptr;
    resource_owner() = default;
    resource_owner(const resource_owner&) = delete;
    resource_owner& operator=(const resource_owner&) = delete;
    ~resource_owner() { progpu_native_hinted_glyph_resource_destroy(value); }
};

struct fixture final {
    std::vector<std::byte> bytes = progpu::native::tests::make_hinted_shape_font();
    context_owner context;
    std::vector<progpu_native_text_scalar> input;
    std::array<progpu_native_text_scalar, 1U> pre{{{'B', 0U, 1U, 0U, 0U, 0U}}};
    std::array<progpu_native_text_scalar, 1U> post{{{'A', 30U, 1U, 0U, 0U, 0U}}};
    std::array<progpu_native_text_feature, 2U> features{{{0x6C696761U, 1U, 0U, UINT32_MAX},
        {0x6B65726EU, 1U, 0U, UINT32_MAX}}};
    std::array<progpu_native_text_style_run, 2U> styles{};
    std::array<progpu_native_text_style_metrics, 2U> metrics{{{5.5F, 1.75F}, {9.25F, 2.5F}}};
    std::array<progpu_native_hinted_paragraph_device_style, 2U> devices{};
    progpu_native_text_shape_request shape{};
    progpu_native_text_layout_options layout{};

    fixture(font_hint_policy interpreter, bool source_bidi) {
        require(progpu_native_text_context_create(PROGPU_NATIVE_ABI_VERSION,
            reinterpret_cast<const std::uint8_t*>(bytes.data()), bytes.size(), 0U, nullptr, 0U,
            &context.value) == PROGPU_NATIVE_STATUS_SUCCESS);
        std::uint32_t second = UINT32_MAX;
        require(progpu_native_text_context_add_fallback_font(context.value,
            reinterpret_cast<const std::uint8_t*>(bytes.data()), bytes.size(), 0U, 0x9177U,
            &second) == PROGPU_NATIVE_STATUS_SUCCESS && second == 1U);
        std::uint32_t start = 9U;
        for (const auto code : std::array<std::uint32_t, 9U>{'A', 'B', ' ', '1', '2', 0x0627U, 'A', 0x03A9U, 'B'}) {
            const auto length = code == 'A' ? std::uint16_t{2U} : std::uint16_t{1U};
            input.push_back({code, start, length, 0U, 0U, 0U}); start += length;
        }
        const auto digits = 0x0660U | (source_bidi ? PROGPU_NATIVE_TEXT_DIGIT_SUBSTITUTION_SOURCE_BIDI : 0U);
        styles = {{{0U, 5U, 0U, 0.0106125F, 0U, 2U, 0U, digits, 0U, 0U, 0U},
            {5U, 4U, 1U, 0.0138875F, 0U, 2U, 0U, digits, 0U, 0U, 0U}}};
        devices = {{{0U, styles[0].scale, 0.8F, 13U * 64U + 17U, 13U * 64U + 17U,
                static_cast<std::uint32_t>(interpreter), 7U, 11U, 0U, 0U, 0U},
            {1U, styles[1].scale, 0.8F, 17U * 64U + 23U, 17U * 64U + 23U,
                static_cast<std::uint32_t>(interpreter), 19U, 23U, 0U, 0U, 0U}}};
        shape.struct_size = sizeof(shape); shape.abi_version = PROGPU_NATIVE_ABI_VERSION;
        shape.input = input.data(); shape.input_count = static_cast<std::uint32_t>(input.size());
        shape.pre_context = pre.data(); shape.pre_context_count = 1U;
        shape.post_context = post.data(); shape.post_context_count = 1U;
        shape.features = features.data(); shape.feature_count = static_cast<std::uint32_t>(features.size());
        shape.direction = PROGPU_NATIVE_TEXT_DIRECTION_LEFT_TO_RIGHT; shape.flags = PROGPU_NATIVE_TEXT_SHAPE_ZERO_MARK_ADVANCES;
        layout.struct_size = sizeof(layout); layout.scale = 1.0F; layout.maximum_width = 200.0F;
        layout.direction = PROGPU_NATIVE_TEXT_DIRECTION_LEFT_TO_RIGHT;
        layout.alignment = PROGPU_NATIVE_TEXT_ALIGNMENT_CENTER;
    }
    void produce(paragraph_owner& paragraph) {
        auto diagnostic = sentinel<progpu_native_text_paragraph_result>(); const auto tail = diagnostic.tail;
        require(progpu_native_text_context_layout_hinted_paragraph(context.value, &shape, &layout,
            styles.data(), 2U, metrics.data(), devices.data(), 2U, nullptr, 0U, &paragraph.value,
            &diagnostic.value) == PROGPU_NATIVE_STATUS_SUCCESS && paragraph.value != nullptr && diagnostic.tail == tail);
    }
};

progpu_native_hinted_glyph_resource_request request_for(hinted_projection_policy policy) {
    return {PROGPU_NATIVE_ABI_VERSION, sizeof(progpu_native_hinted_glyph_resource_request), 1.25F,
        static_cast<std::uint32_t>(policy), PROGPU_NATIVE_HINTED_COVERAGE_NONZERO_VECTOR, 0U};
}

template<class Wire, class Source, class Convert>
void compare_records(const Wire* wire, std::span<const Source> source, Convert convert) {
    for (std::size_t i = 0U; i < source.size(); ++i) require(same_bytes(wire[i], convert(source[i])));
}

void compare_original(const progpu_native_hinted_glyph_resource_view& v,
    const hinted_paragraph_generation& p, const hinted_paragraph_interaction& interaction,
    const hinted_paragraph_glyph_resource& geometry) {
    const progpu_native_hinted_paragraph_counts counts{static_cast<std::uint32_t>(p.source_input.size()),
        static_cast<std::uint32_t>(p.shaping_input.size()), static_cast<std::uint32_t>(p.styles.size()),
        static_cast<std::uint32_t>(p.runs.size()), static_cast<std::uint32_t>(p.logical_glyphs.size()),
        static_cast<std::uint32_t>(p.glyphs.size()), static_cast<std::uint32_t>(p.lines.size()),
        static_cast<std::uint32_t>(interaction.boxes().size()), static_cast<std::uint32_t>(interaction.carets().size())};
    require(v.abi_version == PROGPU_NATIVE_ABI_VERSION && v.struct_size == sizeof(v) && same_bytes(v.counts, counts) &&
        same_bytes(v.result, p.paragraph_result) && same_bytes(v.layout, p.layout) &&
        v.source_digit_bidi == (p.source_digit_bidi ? 1U : 0U) && v.paragraph_level == p.paragraph_level &&
        v.shaping_direction == p.shaping.direction && v.shaping_flags == p.shaping.flags &&
        v.dpi_scale == geometry.dpi_scale() && v.projection_policy == static_cast<std::uint32_t>(geometry.projection_policy()) &&
        v.coverage == static_cast<std::uint32_t>(geometry.coverage()));
    require(same_span<progpu_native_text_scalar>({v.source_scalars, counts.source_scalar_count}, p.source_input));
    compare_records(v.admitted_scalars, std::span<const unicode_scalar>(p.shaping_input), [](const auto& s) {
        return progpu_native_text_scalar{s.code_point, s.input_index, s.input_length, s.canonical_combining_class, s.reserved, s.script.value};
    });
    compare_records(v.scalar_levels, std::span<const unicode_bidi_level>(p.scalar_levels), [](const auto& s) {
        return progpu_native_text_bidi_level{s.input_index, s.input_length, s.level, s.reserved};
    });
    require(same_span<progpu_native_text_style_run>({v.styles, counts.style_count}, p.styles) &&
        same_span<progpu_native_text_style_metrics>({v.source_metrics, counts.style_count}, p.source_metrics));
    std::size_t axes = 0U;
    for (std::size_t i = 0U; i < p.device_styles.size(); ++i) {
        const auto& s = p.device_styles[i];
        const progpu_native_hinted_paragraph_device_style expected{s.font_index, s.source_scale, s.logical_units_per_physical_pixel,
            s.x_pixels_per_em_26_6, s.y_pixels_per_em_26_6, static_cast<std::uint32_t>(s.policy), s.x_phase_26_6, s.y_phase_26_6,
            static_cast<std::uint32_t>(axes), static_cast<std::uint32_t>(s.variation_coordinates_16_16.size()), 0U};
        require(same_bytes(v.device_styles[i], expected));
        if (!s.variation_coordinates_16_16.empty())
            require(same_span<std::int32_t>({v.variation_coordinates_16_16 + axes, s.variation_coordinates_16_16.size()}, s.variation_coordinates_16_16));
        axes += s.variation_coordinates_16_16.size();
    }
    require(axes == v.variation_coordinate_count && same_span<std::int16_t>({v.normalized_coordinates, v.normalized_coordinate_count}, p.normalized_coordinates));
    std::size_t bytes = 0U;
    require(v.font_source_count == p.font_sources.size());
    for (std::size_t i = 0U; i < p.font_sources.size(); ++i) {
        const auto& font = *p.font_sources[i]; sfnt_font_view parsed{}; sfnt_header_metrics metrics{};
        require(sfnt_font_view::try_create(font.bytes, font.face_index, parsed) && parsed.try_get_header_metrics(metrics));
        const progpu_native_hinted_glyph_font_source expected{static_cast<std::uint32_t>(bytes),
            static_cast<std::uint32_t>(font.bytes.size()), font.face_index, metrics.units_per_em};
        require(same_bytes(v.font_sources[i], expected) && std::memcmp(v.font_bytes + bytes, font.bytes.data(), font.bytes.size()) == 0);
        bytes += font.bytes.size();
    }
    require(bytes == v.font_byte_count);
    compare_records(v.runs, std::span<const hinted_paragraph_run>(p.runs), [](const auto& r) {
        return progpu_native_hinted_paragraph_run{r.scalar_start, r.scalar_count, r.logical_start, r.logical_count,
            r.font_index, r.style_index, r.bidi_level, r.source_scale, r.logical_units_per_physical_pixel,
            static_cast<std::uint32_t>(r.generation->source_descriptor_count)};
    });
    compare_records(v.logical_glyphs, std::span<const shaping_glyph>(p.logical_glyphs), [](const auto& g) {
        return progpu_native_text_shaping_glyph{g.glyph_id, g.code_point, g.cluster, static_cast<std::uint32_t>(g.flags),
            g.advance_x, g.advance_y, g.offset_x, g.offset_y};
    });
    const auto owner_wire = [](const auto& o) { return progpu_native_hinted_paragraph_glyph_owner{o.run_index, o.run_glyph_index, o.descriptor_index}; };
    compare_records(v.logical_owners, std::span<const hinted_paragraph_glyph_owner>(p.logical_owners), owner_wire);
    compare_records(v.positioned_owners, std::span<const hinted_paragraph_glyph_owner>(p.positioned_owners), owner_wire);
    for (std::size_t i = 0U; i < p.glyphs.size(); ++i) {
        const auto& g = p.glyphs[i];
        const progpu_native_positioned_text_glyph expected{g.glyph_index, g.glyph_id, p.logical_font_indices[g.glyph_index],
            g.cluster, g.x, g.y, g.advance_x, g.advance_y};
        require(same_bytes(v.positioned_glyphs[i], expected));
    }
    require(same_span<std::int32_t>({v.logical_cluster_ends, counts.logical_glyph_count}, p.logical_cluster_ends) &&
        same_span<std::int8_t>({v.logical_bidi_levels, counts.logical_glyph_count}, p.logical_bidi_levels) &&
        same_span<float>({v.glyph_scales, counts.logical_glyph_count}, p.glyph_scales) &&
        same_span<std::int32_t>({v.positioned_cluster_ends, counts.positioned_glyph_count}, p.cluster_ends) &&
        same_span<std::int8_t>({v.positioned_bidi_levels, counts.positioned_glyph_count}, p.bidi_levels));
    compare_records(v.lines, std::span<const positioned_text_line>(p.lines), [](const auto& l) {
        return progpu_native_positioned_text_line{l.glyph_start, l.glyph_count, l.input_start, l.input_end, l.width,
            l.baseline_y, l.height, static_cast<std::uint8_t>(l.clipped), l.flags, 0U, 0U};
    });
    require(same_span<float>({v.line_origins, counts.line_count}, p.line_origins));
    compare_records(v.boxes, interaction.boxes(), [](const auto& b) {
        return progpu_native_text_cluster_box{b.input_start, b.input_end, b.line_index, b.bidi_level, 0U, 0U, 0U,
            b.x, b.y, b.width, b.height};
    });
    compare_records(v.carets, interaction.carets(), [](const auto& c) {
        return progpu_native_text_caret_stop{c.input_position, c.line_index, c.x, c.y, c.height, c.bidi_level,
            static_cast<std::uint8_t>(c.trailing), 0U, 0U};
    });
    require(same_span<progpu_native_text_scalar>({v.pre_context, v.pre_context_count}, p.pre_context) &&
        same_span<progpu_native_text_scalar>({v.post_context, v.post_context_count}, p.post_context) &&
        same_span<progpu_native_text_feature>({v.features, v.feature_count}, p.features));
    require(same_span<progpu_native_glyph_outline>({v.outlines, v.outline_count}, geometry.outlines()) &&
        same_span<progpu_native_path_segment>({v.segments, v.segment_count}, geometry.segments()) &&
        same_span<std::uint32_t>({v.source_outline_indices, v.source_outline_count}, geometry.source_outline_indices()) &&
        same_span<std::uint32_t>({v.run_outline_indices, v.run_outline_count}, geometry.run_outline_indices()) &&
        same_span<std::uint32_t>({v.positioned_outline_indices, counts.positioned_glyph_count}, geometry.positioned_outline_indices()));
    compare_records(v.run_slices, geometry.run_slices(), [](const auto& s) {
        return progpu_native_hinted_glyph_run_slice{s.source_start, s.source_count, s.run_start, s.run_count,
            s.outline_start, s.outline_count, s.segment_start, s.segment_count};
    });
    compare_records(v.outline_owners, geometry.outline_owners(), [](const auto& o) {
        return progpu_native_hinted_glyph_outline_owner{o.run_index, o.descriptor_index};
    });
}

void aliases(const paragraph_owner& paragraph, const resource_owner& resource,
    const progpu_native_hinted_glyph_resource_request& request, const saved_borrow& saved) {
    const auto p = select_hinted_paragraph_generation(paragraph.value);
    const auto interaction = select_hinted_paragraph_interaction(paragraph.value);
    const auto geometry = select_hinted_glyph_resource_generation(resource.value);
    require(p != nullptr && interaction != nullptr && geometry != nullptr && geometry->paragraph() == p);
    const auto reject_borrow = [&](const void* storage, std::size_t bytes) {
        if (bytes == 0U) return;
        const auto address = reinterpret_cast<std::uintptr_t>(storage);
        const auto aligned = (address + alignof(progpu_native_hinted_glyph_resource_view) - 1U) &
            ~(std::uintptr_t{alignof(progpu_native_hinted_glyph_resource_view)} - 1U);
        if (aligned - address >= bytes) return;
        require(progpu_native_hinted_glyph_resource_borrow(resource.value,
            reinterpret_cast<progpu_native_hinted_glyph_resource_view*>(aligned)) == PROGPU_NATIVE_STATUS_INVALID_ARGUMENT);
        auto again = sentinel<progpu_native_hinted_glyph_resource_view>(); const auto tail = again.tail;
        require(progpu_native_hinted_glyph_resource_borrow(resource.value, &again.value) == PROGPU_NATIVE_STATUS_SUCCESS && again.tail == tail);
        saved.unchanged(again.value);
    };
    reject_borrow(resource.value, sizeof(void*)); reject_borrow(geometry.get(), sizeof(*geometry));
    reject_borrow(p.get(), sizeof(*p)); reject_borrow(interaction.get(), sizeof(*interaction));
    const auto& v = saved.header;
#define BORROW_ALIAS(name, type, count) reject_borrow(v.name, static_cast<std::size_t>(count) * sizeof(type));
    RESOURCE_SPANS(BORROW_ALIAS)
#undef BORROW_ALIAS
    for (const auto& font : p->font_sources) { reject_borrow(font.get(), sizeof(*font)); reject_borrow(font->bytes.data(), font->bytes.capacity()); }
    for (const auto& run : p->runs) {
        reject_borrow(run.generation.get(), sizeof(*run.generation));
        reject_borrow(run.generation->batch.get(), sizeof(*run.generation->batch));
        reject_borrow(run.generation->batch->identity.get(), sizeof(*run.generation->batch->identity));
    }
    reject_borrow(interaction->boxes().data(), interaction->boxes().size_bytes());
    reject_borrow(interaction->carets().data(), interaction->carets().size_bytes());
    const auto reject_prepare = [&](const void* storage, std::size_t bytes) {
        if (bytes == 0U) return;
        const auto address = reinterpret_cast<std::uintptr_t>(storage);
        const auto aligned = (address + alignof(progpu_native_hinted_glyph_resource*) - 1U) &
            ~(std::uintptr_t{alignof(progpu_native_hinted_glyph_resource*)} - 1U);
        if (aligned - address >= bytes) return;
        require(progpu_native_hinted_paragraph_prepare_glyph_resource(paragraph.value, &request,
            reinterpret_cast<progpu_native_hinted_glyph_resource**>(aligned)) == PROGPU_NATIVE_STATUS_INVALID_ARGUMENT);
    };
    reject_prepare(&request, sizeof(request)); reject_prepare(paragraph.value, sizeof(void*));
    reject_prepare(p.get(), sizeof(*p)); reject_prepare(interaction.get(), sizeof(*interaction));
    for (const auto& font : p->font_sources) { reject_prepare(font.get(), sizeof(*font)); reject_prepare(font->bytes.data(), font->bytes.capacity()); }
    for (const auto& style : p->device_styles) {
        reject_prepare(style.variation_coordinates_16_16.data(), style.variation_coordinates_16_16.capacity() * sizeof(std::int32_t));
        reject_borrow(style.variation_coordinates_16_16.data(), style.variation_coordinates_16_16.capacity() * sizeof(std::int32_t));
    }
    for (const auto& run : p->runs) {
        const auto& retained = *run.generation;
        const auto& batch = *retained.batch;
        const auto& identity = *batch.identity;
        reject_prepare(&retained, sizeof(retained)); reject_prepare(&batch, sizeof(batch)); reject_prepare(&identity, sizeof(identity));
#define RUN_ALLOCATION(values) \
        reject_prepare((values).data(), (values).capacity() * sizeof((values)[0])); \
        reject_borrow((values).data(), (values).capacity() * sizeof((values)[0]));
        RUN_ALLOCATION(retained.shaping_input) RUN_ALLOCATION(retained.glyphs) RUN_ALLOCATION(retained.descriptor_indices)
        RUN_ALLOCATION(retained.normalized_coordinates) RUN_ALLOCATION(batch.glyphs)
        RUN_ALLOCATION(identity.variation_coordinates_16_16)
        for (const auto& glyph : batch.glyphs) {
            RUN_ALLOCATION(glyph.points) RUN_ALLOCATION(glyph.tags) RUN_ALLOCATION(glyph.contour_ends)
        }
#undef RUN_ALLOCATION
    }
#define ORIGINAL_ALLOCATION(field) \
    reject_prepare(p->field.data(), p->field.capacity() * sizeof(p->field[0])); \
    reject_borrow(p->field.data(), p->field.capacity() * sizeof(p->field[0])); \
    if (p->field.capacity() > p->field.size()) { \
        reject_prepare(p->field.data() + p->field.size(), (p->field.capacity() - p->field.size()) * sizeof(p->field[0])); \
        reject_borrow(p->field.data() + p->field.size(), (p->field.capacity() - p->field.size()) * sizeof(p->field[0])); \
    }
    ORIGINAL_ALLOCATION(source_input) ORIGINAL_ALLOCATION(pre_context) ORIGINAL_ALLOCATION(post_context)
    ORIGINAL_ALLOCATION(features) ORIGINAL_ALLOCATION(normalized_coordinates) ORIGINAL_ALLOCATION(shaping_input)
    ORIGINAL_ALLOCATION(scalar_levels) ORIGINAL_ALLOCATION(script_runs) ORIGINAL_ALLOCATION(graphemes)
    ORIGINAL_ALLOCATION(fallback_runs) ORIGINAL_ALLOCATION(font_sources) ORIGINAL_ALLOCATION(styles)
    ORIGINAL_ALLOCATION(source_metrics) ORIGINAL_ALLOCATION(device_styles) ORIGINAL_ALLOCATION(runs)
    ORIGINAL_ALLOCATION(logical_glyphs) ORIGINAL_ALLOCATION(logical_bidi_levels) ORIGINAL_ALLOCATION(logical_font_indices)
    ORIGINAL_ALLOCATION(logical_source_scales) ORIGINAL_ALLOCATION(glyph_scales) ORIGINAL_ALLOCATION(logical_owners)
    ORIGINAL_ALLOCATION(line_break_classes) ORIGINAL_ALLOCATION(scalar_breaks) ORIGINAL_ALLOCATION(breaks_after)
    ORIGINAL_ALLOCATION(justification_classes) ORIGINAL_ALLOCATION(item_metrics) ORIGINAL_ALLOCATION(logical_cluster_ends)
    ORIGINAL_ALLOCATION(glyphs) ORIGINAL_ALLOCATION(lines) ORIGINAL_ALLOCATION(positioned_owners)
    ORIGINAL_ALLOCATION(bidi_levels) ORIGINAL_ALLOCATION(cluster_ends) ORIGINAL_ALLOCATION(line_origins)
#undef ORIGINAL_ALLOCATION
    auto publication = sentinel<progpu_native_hinted_glyph_resource*>(); const auto old = publication;
    require(progpu_native_hinted_paragraph_prepare_glyph_resource(paragraph.value,
        reinterpret_cast<const progpu_native_hinted_glyph_resource_request*>(p.get()), &publication.value) ==
        PROGPU_NATIVE_STATUS_INVALID_ARGUMENT && same_bytes(publication, old));
    auto again = sentinel<progpu_native_hinted_glyph_resource_view>();
    require(progpu_native_hinted_glyph_resource_borrow(resource.value, &again.value) == PROGPU_NATIVE_STATUS_SUCCESS);
    saved.unchanged(again.value);
}

void producer_controls(font_hint_policy interpreter, bool source_bidi, hinted_projection_policy policy, bool limited,
    hinted_outline_coverage coverage = hinted_outline_coverage::nonzero_vector) {
    paragraph_owner paragraph; resource_owner resource;
    std::weak_ptr<const hinted_paragraph_generation> paragraph_weak;
    std::weak_ptr<const hinted_paragraph_interaction> interaction_weak;
    std::weak_ptr<const hinted_shaped_run> run_weak;
    std::weak_ptr<const owned_font_source> font_weak;
    auto wire = sentinel<progpu_native_hinted_glyph_resource_view>(); const auto wire_tail = wire.tail;
    auto request = request_for(policy); request.coverage = static_cast<std::uint32_t>(coverage);
    const auto original_request = request;
    {
        fixture source(interpreter, source_bidi);
        if (limited) {
            source.input[1].code_point = '\r'; source.input[2].code_point = '\n';
            source.input[3].code_point = 'B'; source.input[4].code_point = ' ';
            source.layout.maximum_lines = 1U;
            source.shape.direction = PROGPU_NATIVE_TEXT_DIRECTION_RIGHT_TO_LEFT;
            source.layout.direction = PROGPU_NATIVE_TEXT_DIRECTION_RIGHT_TO_LEFT;
        }
        source.produce(paragraph);
        const auto p = select_hinted_paragraph_generation(paragraph.value);
        const auto interaction = select_hinted_paragraph_interaction(paragraph.value);
        paragraph_weak = p; interaction_weak = interaction; run_weak = p->runs.front().generation; font_weak = p->font_sources[0];
        std::vector<std::vector<hinted_glyph>> original_captures;
        for (const auto& run : p->runs) original_captures.push_back(run.generation->batch->glyphs);
        auto publication = sentinel<progpu_native_hinted_glyph_resource*>(); const auto publication_tail = publication.tail;
        require(progpu_native_hinted_paragraph_prepare_glyph_resource(paragraph.value, &request, &publication.value) == PROGPU_NATIVE_STATUS_SUCCESS &&
            publication.value != nullptr && publication.tail == publication_tail && same_bytes(request, original_request));
        resource.value = publication.value;
        require(progpu_native_hinted_glyph_resource_borrow(resource.value, &wire.value) == PROGPU_NATIVE_STATUS_SUCCESS && wire.tail == wire_tail);
        const auto independent = create_hinted_paragraph_glyph_resource(p, request.dpi_scale, policy, coverage);
        require(independent.status == PROGPU_NATIVE_STATUS_SUCCESS && independent.generation != nullptr);
        compare_original(wire.value, *p, *interaction, *independent.generation);
        require(wire.value.font_source_count == 2U && wire.value.font_sources[1].byte_offset == source.bytes.size());
        require(std::find(wire.value.source_outline_indices, wire.value.source_outline_indices + wire.value.source_outline_count, hinted_no_outline) !=
            wire.value.source_outline_indices + wire.value.source_outline_count);
        if (limited) require(wire.value.counts.positioned_glyph_count < wire.value.counts.logical_glyph_count);
        const saved_borrow saved(wire.value); aliases(paragraph, resource, request, saved);
        const auto reject = [&](progpu_native_status status) {
            auto slot = sentinel<progpu_native_hinted_glyph_resource*>(); slot.value = resource.value; const auto old = slot;
            const auto before_request = request;
            require(progpu_native_hinted_paragraph_prepare_glyph_resource(paragraph.value, &request, &slot.value) == status &&
                same_bytes(slot, old) && same_bytes(request, before_request));
            require(progpu_native_hinted_glyph_resource_borrow(resource.value, &wire.value) == PROGPU_NATIVE_STATUS_SUCCESS); saved.unchanged(wire.value);
        };
        request.abi_version = UINT32_MAX; reject(PROGPU_NATIVE_STATUS_INVALID_ARGUMENT); request = original_request;
        request.struct_size -= 1U; reject(PROGPU_NATIVE_STATUS_INVALID_ARGUMENT); request = original_request;
        request.reserved = 1U; reject(PROGPU_NATIVE_STATUS_INVALID_ARGUMENT); request = original_request;
        request.coverage = PROGPU_NATIVE_HINTED_COVERAGE_ANTIALIASED_VECTOR + 1U;
        reject(PROGPU_NATIVE_STATUS_INVALID_ARGUMENT); request = original_request;
        request.coverage = PROGPU_NATIVE_HINTED_COVERAGE_STRICT; reject(PROGPU_NATIVE_STATUS_UNSUPPORTED); request = original_request;
        request.projection_policy = UINT32_MAX; reject(PROGPU_NATIVE_STATUS_INVALID_ARGUMENT); request = original_request;
        request.projection_policy = PROGPU_NATIVE_HINTED_PROJECTION_NATIVE_COMPUTE; reject(PROGPU_NATIVE_STATUS_UNSUPPORTED); request = original_request;
        request.projection_policy = PROGPU_NATIVE_HINTED_PROJECTION_GPU_SHADER; reject(PROGPU_NATIVE_STATUS_UNSUPPORTED); request = original_request;
        for (const auto dpi : {0.0F, -1.0F, std::numeric_limits<float>::quiet_NaN(), std::numeric_limits<float>::infinity()}) {
            request.dpi_scale = dpi; reject(PROGPU_NATIVE_STATUS_INVALID_ARGUMENT); request = original_request;
        }
        request.dpi_scale = 2.0F; reject(PROGPU_NATIVE_STATUS_UNSUPPORTED); request = original_request;
        for (std::size_t i = 0U; i < original_captures.size(); ++i) require(p->runs[i].generation->batch->glyphs == original_captures[i]);
        std::fill(source.bytes.begin(), source.bytes.end(), std::byte{0});
        source.styles.fill({}); source.devices.fill({}); source.metrics.fill({}); source.input.clear();
    } // Retire the actual context and all independent reference factories.
    require(!paragraph_weak.expired() && !interaction_weak.expired() && !run_weak.expired() && !font_weak.expired());
    const saved_borrow after_context(wire.value);
    progpu_native_hinted_paragraph_destroy(paragraph.value); paragraph.value = nullptr;
    require(!paragraph_weak.expired() && !interaction_weak.expired() && !run_weak.expired() && !font_weak.expired());
    for (unsigned i = 0U; i < 3U; ++i) {
        require(progpu_native_hinted_glyph_resource_borrow(resource.value, &wire.value) == PROGPU_NATIVE_STATUS_SUCCESS && wire.tail == wire_tail);
        after_context.unchanged(wire.value);
    }
    {
        const auto retained = select_hinted_glyph_resource_generation(resource.value);
        require(retained != nullptr && retained->paragraph() == paragraph_weak.lock());
        compare_original(wire.value, *paragraph_weak.lock(), *interaction_weak.lock(), *retained);
    }
    progpu_native_hinted_glyph_resource_destroy(resource.value); resource.value = nullptr;
    require(paragraph_weak.expired() && interaction_weak.expired() && run_weak.expired() && font_weak.expired());
}

void variable_font_controls() {
    paragraph_owner paragraph; resource_owner resource;
    std::weak_ptr<const hinted_paragraph_generation> paragraph_weak;
    std::weak_ptr<const hinted_paragraph_interaction> interaction_weak;
    std::weak_ptr<const owned_font_source> font_weak;
    auto wire = sentinel<progpu_native_hinted_glyph_resource_view>(); const auto tail = wire.tail;
    std::unique_ptr<saved_borrow> saved;
    auto request = request_for(hinted_projection_policy::scalar_reference);
    request.coverage = PROGPU_NATIVE_HINTED_COVERAGE_ANTIALIASED_VECTOR;
    {
        progpu::native::tests::hinted_variable_font_fixture source;
        source.produce(paragraph.value);
        const auto p = select_hinted_paragraph_generation(paragraph.value);
        const auto interaction = select_hinted_paragraph_interaction(paragraph.value);
        require(p != nullptr && interaction != nullptr && p->device_styles.size() == 2U && p->runs.size() >= 2U);
        paragraph_weak = p; interaction_weak = interaction; font_weak = p->font_sources[0U];
        require(same_span<std::int16_t>(p->normalized_coordinates, source.normalized));
        std::vector<std::vector<hinted_glyph>> original_captures;
        for (const auto& run : p->runs) {
            require(same_span<std::int16_t>(run.generation->normalized_coordinates, source.normalized) &&
                same_span<std::int32_t>(run.generation->batch->identity->variation_coordinates_16_16,
                    std::span<const std::int32_t>(source.axes).first(2U)));
            original_captures.push_back(run.generation->batch->glyphs);
        }
        require(progpu_native_hinted_paragraph_prepare_glyph_resource(paragraph.value, &request, &resource.value) == PROGPU_NATIVE_STATUS_SUCCESS &&
            progpu_native_hinted_glyph_resource_borrow(resource.value, &wire.value) == PROGPU_NATIVE_STATUS_SUCCESS && wire.tail == tail);
        auto rejected = sentinel<progpu_native_hinted_glyph_resource*>(); rejected.value = resource.value;
        const auto previous = rejected;
        require(progpu_native_hinted_paragraph_prepare_glyph_resource_with_nominal_metrics(paragraph.value, &request, &rejected.value) ==
            PROGPU_NATIVE_STATUS_UNSUPPORTED && same_bytes(previous, rejected));
        const auto independent = create_hinted_paragraph_glyph_resource(p, request.dpi_scale,
            hinted_projection_policy::scalar_reference, hinted_outline_coverage::antialiased_vector);
        require(independent.status == PROGPU_NATIVE_STATUS_SUCCESS && independent.generation != nullptr);
        compare_original(wire.value, *p, *interaction, *independent.generation);
        require(wire.value.font_source_count == 1U && wire.value.font_sources[0U].face_index == 0U &&
            wire.value.font_sources[0U].units_per_em == 2048U && wire.value.font_byte_count == source.font.size() &&
            std::memcmp(wire.value.font_bytes, source.font.data(), source.font.size()) == 0 &&
            wire.value.variation_coordinate_count == 4U && wire.value.normalized_coordinate_count == 2U &&
            same_span<std::int32_t>({wire.value.variation_coordinates_16_16, 4U}, source.axes) &&
            same_span<std::int16_t>({wire.value.normalized_coordinates, 2U}, source.normalized));
        for (std::uint32_t i = 0U; i < 2U; ++i)
            require(wire.value.device_styles[i].variation_start == i * 2U && wire.value.device_styles[i].variation_count == 2U);
        require(std::find(wire.value.positioned_outline_indices,
            wire.value.positioned_outline_indices + wire.value.counts.positioned_glyph_count, hinted_no_outline) !=
            wire.value.positioned_outline_indices + wire.value.counts.positioned_glyph_count);
        saved = std::make_unique<saved_borrow>(wire.value); aliases(paragraph, resource, request, *saved);
        for (std::size_t i = 0U; i < original_captures.size(); ++i) require(p->runs[i].generation->batch->glyphs == original_captures[i]);
        source.retire_inputs(); // Exact original bytes, raw axes and normalized coordinates are now solely retained.
    }
    require(!paragraph_weak.expired() && !interaction_weak.expired() && !font_weak.expired());
    progpu_native_hinted_paragraph_destroy(paragraph.value); paragraph.value = nullptr;
    for (unsigned i = 0U; i < 3U; ++i) {
        require(progpu_native_hinted_glyph_resource_borrow(resource.value, &wire.value) == PROGPU_NATIVE_STATUS_SUCCESS && wire.tail == tail);
        saved->unchanged(wire.value);
    }
    {
        const auto retained = select_hinted_glyph_resource_generation(resource.value);
        require(retained != nullptr && retained->paragraph() == paragraph_weak.lock());
        compare_original(wire.value, *paragraph_weak.lock(), *interaction_weak.lock(), *retained);
    }
    progpu_native_hinted_glyph_resource_destroy(resource.value); resource.value = nullptr;
    require(paragraph_weak.expired() && interaction_weak.expired() && font_weak.expired());
}

void nominal_metric_controls() {
    paragraph_owner paragraph; resource_owner resource, ordinary;
    auto metrics = sentinel<progpu_native_hinted_glyph_nominal_metrics_view>(); const auto tail = metrics.tail;
    {
        fixture source(font_hint_policy::truetype_40, true);
        source.produce(paragraph);
        const auto request = request_for(hinted_projection_policy::scalar_reference);
        require(progpu_native_hinted_paragraph_prepare_glyph_resource(paragraph.value, &request, &ordinary.value) == PROGPU_NATIVE_STATUS_SUCCESS);
        const auto unchanged = metrics;
        require(progpu_native_hinted_glyph_resource_borrow_nominal_metrics(ordinary.value, &metrics.value) ==
            PROGPU_NATIVE_STATUS_UNSUPPORTED && same_bytes(metrics, unchanged));
        require(progpu_native_hinted_paragraph_prepare_glyph_resource_with_nominal_metrics(paragraph.value, &request, &resource.value) ==
            PROGPU_NATIVE_STATUS_SUCCESS);
        require(progpu_native_hinted_glyph_resource_borrow_nominal_metrics(resource.value, &metrics.value) == PROGPU_NATIVE_STATUS_SUCCESS && metrics.tail == tail);
        progpu_native_hinted_glyph_resource_view original{};
        require(progpu_native_hinted_glyph_resource_borrow(resource.value, &original) == PROGPU_NATIVE_STATUS_SUCCESS);
        require(metrics.value.abi_version == PROGPU_NATIVE_ABI_VERSION && metrics.value.struct_size == sizeof(metrics.value) &&
            metrics.value.reserved == 0U && metrics.value.metric_count == original.counts.positioned_glyph_count);
        bool no_ink = false, repeated = false, positioned_differs = false;
        for (std::uint32_t i = 0U; i < metrics.value.metric_count; ++i) {
            const auto& metric = metrics.value.metrics[i]; const auto& glyph = original.positioned_glyphs[i];
            // Every original hmtx entry is independently authored as 500 in
            // progpu_native_hint_fault_fixture.hpp, including its empty glyph.
            require(metric.positioned_index == i && metric.font_index == glyph.font_index &&
                metric.glyph_id == glyph.glyph_id && metric.advance_width_design_units == 500U);
            no_ink |= original.positioned_outline_indices[i] == hinted_no_outline;
            positioned_differs |= glyph.advance_x != 500.0F;
            for (std::uint32_t prior = 0U; prior < i; ++prior)
                repeated |= metrics.value.metrics[prior].glyph_id == metric.glyph_id;
        }
        require(no_ink && repeated && positioned_differs);
        // Both old and additive borrows reject aliases into the new allocation,
        // including an output larger than the remaining selected storage.
        const auto* storage = metrics.value.metrics;
        require(progpu_native_hinted_glyph_resource_borrow_nominal_metrics(resource.value,
            reinterpret_cast<progpu_native_hinted_glyph_nominal_metrics_view*>(const_cast<progpu_native_hinted_glyph_nominal_metrics*>(storage))) ==
            PROGPU_NATIVE_STATUS_INVALID_ARGUMENT);
        require(progpu_native_hinted_glyph_resource_borrow(resource.value,
            reinterpret_cast<progpu_native_hinted_glyph_resource_view*>(const_cast<progpu_native_hinted_glyph_nominal_metrics*>(storage))) ==
            PROGPU_NATIVE_STATUS_INVALID_ARGUMENT);
        require(progpu_native_hinted_glyph_resource_borrow_nominal_metrics(resource.value,
            reinterpret_cast<progpu_native_hinted_glyph_nominal_metrics_view*>(const_cast<std::uint8_t*>(original.font_bytes))) ==
            PROGPU_NATIVE_STATUS_INVALID_ARGUMENT);
        std::fill(source.bytes.begin(), source.bytes.end(), std::byte{0});
    }
    const auto before = metrics;
    const std::vector saved(metrics.value.metrics, metrics.value.metrics + metrics.value.metric_count);
    progpu_native_hinted_paragraph_destroy(paragraph.value); paragraph.value = nullptr;
    require(progpu_native_hinted_glyph_resource_borrow_nominal_metrics(resource.value, &metrics.value) == PROGPU_NATIVE_STATUS_SUCCESS &&
        same_bytes(metrics, before) && same_span<progpu_native_hinted_glyph_nominal_metrics>(
            {metrics.value.metrics, metrics.value.metric_count}, saved));
}

void empty_controls() {
    fixture source(font_hint_policy::truetype_40, false);
    source.shape.input = nullptr; source.shape.input_count = 0U;
    source.shape.pre_context = nullptr; source.shape.pre_context_count = 0U;
    source.shape.post_context = nullptr; source.shape.post_context_count = 0U;
    source.shape.features = nullptr; source.shape.feature_count = 0U;
    paragraph_owner paragraph; resource_owner resource;
    progpu_native_text_paragraph_result diagnostic{};
    require(progpu_native_text_context_layout_hinted_paragraph(source.context.value, &source.shape, &source.layout,
        nullptr, 0U, nullptr, nullptr, 0U, nullptr, 0U, &paragraph.value, &diagnostic) == PROGPU_NATIVE_STATUS_SUCCESS);
    const auto request = request_for(hinted_projection_policy::scalar_reference);
    require(progpu_native_hinted_paragraph_prepare_glyph_resource(paragraph.value, &request, &resource.value) == PROGPU_NATIVE_STATUS_SUCCESS);
    auto wire = sentinel<progpu_native_hinted_glyph_resource_view>(); const auto tail = wire.tail;
    require(progpu_native_hinted_glyph_resource_borrow(resource.value, &wire.value) == PROGPU_NATIVE_STATUS_SUCCESS && wire.tail == tail);
    const progpu_native_hinted_paragraph_counts empty{};
    require(same_bytes(wire.value.counts, empty) && wire.value.outline_count == 0U && wire.value.segment_count == 0U &&
        wire.value.source_outline_count == 0U && wire.value.run_outline_count == 0U && wire.value.variation_coordinate_count == 0U &&
        wire.value.normalized_coordinate_count == 0U && wire.value.pre_context_count == 0U && wire.value.post_context_count == 0U &&
        wire.value.feature_count == 0U); // Original empty input never manufactures a row/glyph/caret.
    const auto retained = select_hinted_glyph_resource_generation(resource.value);
    const auto interaction = select_hinted_paragraph_interaction(paragraph.value);
    compare_original(wire.value, *retained->paragraph(), *interaction, *retained);
}
#endif
#undef RESOURCE_SPANS
} // namespace

int main() {
    try {
        auto view = sentinel<progpu_native_hinted_glyph_resource_view>(); const auto old_view = view;
        auto slot = sentinel<progpu_native_hinted_glyph_resource*>(); const auto old_slot = slot;
        const progpu_native_hinted_glyph_resource_request request{PROGPU_NATIVE_ABI_VERSION,
            sizeof(progpu_native_hinted_glyph_resource_request), 1.25F,
            PROGPU_NATIVE_HINTED_PROJECTION_SCALAR_REFERENCE, PROGPU_NATIVE_HINTED_COVERAGE_NONZERO_VECTOR, 0U};
        require(progpu_native_hinted_paragraph_prepare_glyph_resource(nullptr, &request, &slot.value) ==
            PROGPU_NATIVE_STATUS_INVALID_ARGUMENT && same_bytes(slot, old_slot));
        require(progpu_native_hinted_glyph_resource_borrow(nullptr, &view.value) ==
            PROGPU_NATIVE_STATUS_INVALID_ARGUMENT && same_bytes(view, old_view));
        progpu_native_hinted_glyph_resource_destroy(nullptr);
#if defined(PROGPU_NATIVE_FONT_HINTING)
        producer_controls(font_hint_policy::truetype_35, true, hinted_projection_policy::scalar_reference, false);
        producer_controls(font_hint_policy::truetype_40, false, hinted_projection_policy::automatic, false);
        producer_controls(font_hint_policy::truetype_40, true, hinted_projection_policy::intrinsic_simd, true);
        producer_controls(font_hint_policy::truetype_40, true, hinted_projection_policy::scalar_reference, false,
            hinted_outline_coverage::antialiased_vector);
        variable_font_controls();
        nominal_metric_controls();
        empty_controls();
#endif
        return 0;
    } catch (const std::exception& error) { std::cerr << error.what() << '\n'; return 1; }
}
