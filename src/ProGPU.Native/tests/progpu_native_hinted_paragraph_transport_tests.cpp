#include "../include/progpu_native_text_hinting.h"
#include "../src/Text/Interop/progpu_native_hinted_paragraph_internal.hpp"
#include "../src/Text/Interop/progpu_native_hinted_paragraph_interaction.hpp"
#include "../src/Text/Interop/progpu_native_hinted_paragraph_glyph_frame.hpp"
#include "../src/Text/Interop/progpu_native_hinted_paragraph_transport_internal.hpp"
#include "progpu_native_hinted_shape_fixture.hpp"

#include <algorithm>
#include <array>
#include <cmath>
#include <cstring>
#include <iostream>
#include <limits>
#include <source_location>
#include <stdexcept>
#include <string>
#include <type_traits>

// Actual C ownership/transport over the original retained producer. Separate
// original private factories supply reference generations; no source snapshot
// is copied, and production never reshapes for copies or interaction. The mock
// target view is inspected only: no engine, GPU, Display or package admission.
namespace {
using namespace progpu::native::text;
static_assert(!std::is_copy_constructible_v<hinted_paragraph_generation>);

void require(bool value, std::source_location at = std::source_location::current()) {
    if (!value) throw std::runtime_error("hinted paragraph transport control at " + std::to_string(at.line()));
}

template<class T> bool same_bytes(const T& a, const T& b) {
    static_assert(std::is_trivially_copyable_v<T>);
    return std::memcmp(&a, &b, sizeof(T)) == 0;
}

template<class T> bool same_span(std::span<const T> a, std::span<const T> b) {
    static_assert(std::is_trivially_copyable_v<T>);
    return a.size() == b.size() && (a.empty() || std::memcmp(a.data(), b.data(), a.size_bytes()) == 0);
}

template<class T> std::vector<T> sentinels(std::size_t count) {
    static_assert(std::is_trivially_copyable_v<T>);
    std::vector<T> result(count);
    if (!result.empty()) std::memset(result.data(), 0x71, result.size() * sizeof(T));
    return result;
}

template<class T> struct tailed final { T value{}; std::array<std::byte, 32U> tail{}; };
template<class T> tailed<T> sentinel_output() {
    static_assert(std::is_trivially_copyable_v<tailed<T>>);
    tailed<T> result{}; std::memset(static_cast<void*>(&result), 0x71, sizeof(result)); return result;
}

#if defined(PROGPU_NATIVE_FONT_HINTING)
struct context_owner final {
    progpu_native_text_context* value = nullptr;
    context_owner() = default;
    context_owner(const context_owner&) = delete;
    context_owner& operator=(const context_owner&) = delete;
    ~context_owner() { progpu_native_text_context_destroy(value); }
};

struct fixture final {
    std::vector<std::byte> bytes = progpu::native::tests::make_hinted_shape_font();
    context_owner context;
    std::vector<progpu_native_text_scalar> input;
    std::array<progpu_native_text_feature, 2U> features{{{0x6C696761U, 1U, 0U, UINT32_MAX},
        {0x6B65726EU, 1U, 0U, UINT32_MAX}}};
    std::array<progpu_native_text_style_run, 2U> styles;
    std::array<progpu_native_text_style_metrics, 2U> metrics{{{5.5F, 1.75F}, {9.25F, 2.5F}}};
    std::array<hinted_paragraph_style_configuration, 2U> configurations;
    progpu_native_text_shape_request shaping{};
    progpu_native_text_layout_options layout{};

    fixture(font_hint_policy policy, bool source_bidi) {
        require(progpu_native_text_context_create(PROGPU_NATIVE_ABI_VERSION,
            reinterpret_cast<const std::uint8_t*>(bytes.data()), bytes.size(), 0U, nullptr, 0U,
            &context.value) == PROGPU_NATIVE_STATUS_SUCCESS);
        std::uint32_t second = UINT32_MAX;
        require(progpu_native_text_context_add_fallback_font(context.value,
            reinterpret_cast<const std::uint8_t*>(bytes.data()), bytes.size(), 0U, 0x9177U,
            &second) == PROGPU_NATIVE_STATUS_SUCCESS && second == 1U);
        const std::array<std::uint32_t, 9U> values{'A', 'B', ' ', '1', '2', 0x0627U, 'A', 0x03A9U, 'B'};
        std::uint32_t start = 9U;
        for (const auto value : values) {
            const auto length = value == 'A' ? std::uint16_t{2U} : std::uint16_t{1U};
            input.push_back({value, start, length, 0U, 0U, 0U}); start += length;
        }
        const auto digits = 0x0660U | (source_bidi ? PROGPU_NATIVE_TEXT_DIGIT_SUBSTITUTION_SOURCE_BIDI : 0U);
        styles = {{{0U, 5U, 0U, 0.0106125F, 0U, 2U, 0U, digits, 0U, 0U, 0U},
            {5U, 4U, 1U, 0.0138875F, 0U, 2U, 0U, digits, 0U, 0U, 0U}}};
        configurations = {{{0U, styles[0].scale,
            {13U * 64U + 17U, 13U * 64U + 17U, policy, 7U, 11U, {}}, 0.8F},
            {1U, styles[1].scale,
            {17U * 64U + 23U, 17U * 64U + 23U, policy, 19U, 23U, {}}, 0.8F}}};
        shaping.struct_size = sizeof(shaping); shaping.abi_version = PROGPU_NATIVE_ABI_VERSION;
        shaping.input = input.data(); shaping.input_count = static_cast<std::uint32_t>(input.size());
        shaping.features = features.data(); shaping.feature_count = static_cast<std::uint32_t>(features.size());
        shaping.direction = PROGPU_NATIVE_TEXT_DIRECTION_LEFT_TO_RIGHT;
        layout.struct_size = sizeof(layout); layout.scale = 1.0F; layout.maximum_width = 200.0F;
        layout.direction = PROGPU_NATIVE_TEXT_DIRECTION_LEFT_TO_RIGHT;
    }

    std::shared_ptr<const hinted_paragraph_generation> reference() {
        std::shared_ptr<const hinted_paragraph_generation> result;
        progpu_native_text_paragraph_result diagnostic{}; diagnostic.struct_size = sizeof(diagnostic);
        require(try_layout_context_hinted_paragraph(context.value, shaping, layout, styles, metrics,
            configurations, result, diagnostic) == PROGPU_NATIVE_STATUS_SUCCESS && result != nullptr);
        return result;
    }
};

struct paragraph_owner final {
    progpu_native_hinted_paragraph* value = nullptr;
    paragraph_owner() = default;
    paragraph_owner(const paragraph_owner&) = delete;
    paragraph_owner& operator=(const paragraph_owner&) = delete;
    ~paragraph_owner() { progpu_native_hinted_paragraph_destroy(value); }
};
struct frame_owner final {
    progpu_native_hinted_paragraph_frame* value = nullptr;
    frame_owner() = default;
    frame_owner(const frame_owner&) = delete;
    frame_owner& operator=(const frame_owner&) = delete;
    ~frame_owner() { progpu_native_hinted_paragraph_frame_destroy(value); }
};

std::array<progpu_native_hinted_paragraph_device_style, 2U> device_wire(const fixture& source) {
    std::array<progpu_native_hinted_paragraph_device_style, 2U> result{};
    for (std::size_t i = 0U; i < result.size(); ++i) {
        const auto& c = source.configurations[i]; const auto& h = c.hinting;
        result[i] = {c.font_index, c.source_scale, c.logical_units_per_physical_pixel,
            h.x_pixels_per_em_26_6, h.y_pixels_per_em_26_6, static_cast<std::uint32_t>(h.policy),
            h.x_phase_26_6, h.y_phase_26_6, 0U, 0U, 0U};
    }
    return result;
}

void produce(fixture& source, paragraph_owner& result) {
    const auto wire = device_wire(source);
    auto diagnostic = sentinel_output<progpu_native_text_paragraph_result>(); const auto before = diagnostic;
    auto publication = sentinel_output<progpu_native_hinted_paragraph*>(); const auto publication_tail = publication.tail;
    require(progpu_native_text_context_layout_hinted_paragraph(source.context.value, &source.shaping, &source.layout,
        source.styles.data(), 2U, source.metrics.data(), wire.data(), 2U, nullptr, 0U,
        &publication.value, &diagnostic.value) == PROGPU_NATIVE_STATUS_SUCCESS && publication.value != nullptr);
    result.value = publication.value;
    require(diagnostic.value.struct_size == sizeof(diagnostic.value) && diagnostic.value.error_code == 0U &&
        diagnostic.value.error_stage == 0U && diagnostic.tail == before.tail && publication.tail == publication_tail);
}

progpu_native_hinted_paragraph_counts expected_counts(const hinted_paragraph_generation& p,
    const hinted_paragraph_interaction& interaction) {
    return {static_cast<std::uint32_t>(p.source_input.size()), static_cast<std::uint32_t>(p.shaping_input.size()),
        static_cast<std::uint32_t>(p.styles.size()), static_cast<std::uint32_t>(p.runs.size()),
        static_cast<std::uint32_t>(p.logical_glyphs.size()), static_cast<std::uint32_t>(p.glyphs.size()),
        static_cast<std::uint32_t>(p.lines.size()), static_cast<std::uint32_t>(interaction.boxes().size()),
        static_cast<std::uint32_t>(interaction.carets().size())};
}

// Every declared output includes three unused caller-owned slots. The field
// table drives all 17 independent short-capacity controls, not only positions.
#define FORMAT_FIELDS(X) \
    X(source_scalars, source_scalar_capacity, progpu_native_text_scalar, source_scalar_count) \
    X(admitted_scalars, admitted_scalar_capacity, progpu_native_text_scalar, admitted_scalar_count) \
    X(scalar_levels, scalar_level_capacity, progpu_native_text_bidi_level, admitted_scalar_count) \
    X(styles, style_capacity, progpu_native_text_style_run, style_count) \
    X(source_metrics, source_metric_capacity, progpu_native_text_style_metrics, style_count) \
    X(runs, run_capacity, progpu_native_hinted_paragraph_run, run_count) \
    X(logical_glyphs, logical_glyph_capacity, progpu_native_text_shaping_glyph, logical_glyph_count) \
    X(logical_owners, logical_owner_capacity, progpu_native_hinted_paragraph_glyph_owner, logical_glyph_count) \
    X(logical_cluster_ends, logical_cluster_end_capacity, std::int32_t, logical_glyph_count) \
    X(logical_bidi_levels, logical_bidi_level_capacity, std::int8_t, logical_glyph_count) \
    X(glyph_scales, glyph_scale_capacity, float, logical_glyph_count) \
    X(positioned_glyphs, positioned_glyph_capacity, progpu_native_positioned_text_glyph, positioned_glyph_count) \
    X(positioned_owners, positioned_owner_capacity, progpu_native_hinted_paragraph_glyph_owner, positioned_glyph_count) \
    X(positioned_cluster_ends, positioned_cluster_end_capacity, std::int32_t, positioned_glyph_count) \
    X(positioned_bidi_levels, positioned_bidi_level_capacity, std::int8_t, positioned_glyph_count) \
    X(lines, line_capacity, progpu_native_positioned_text_line, line_count) \
    X(line_origins, line_origin_capacity, float, line_count)

struct format_storage final {
    progpu_native_hinted_paragraph_counts counts;
#define DECLARE(name, cap, type, count) std::vector<type> name;
    FORMAT_FIELDS(DECLARE)
#undef DECLARE
    explicit format_storage(progpu_native_hinted_paragraph_counts c) : counts(c) {
#define INITIALIZE(name, cap, type, count) name = sentinels<type>(static_cast<std::size_t>(c.count) + 3U);
        FORMAT_FIELDS(INITIALIZE)
#undef INITIALIZE
    }
    progpu_native_hinted_paragraph_format_buffers buffers() {
        progpu_native_hinted_paragraph_format_buffers result{}; result.struct_size = sizeof(result);
#define BUFFER(name, cap, type, count) result.name = name.data(); result.cap = static_cast<std::uint32_t>(name.size());
        FORMAT_FIELDS(BUFFER)
#undef BUFFER
        return result;
    }
    void unchanged(const format_storage& before) const {
#define UNCHANGED(name, cap, type, count) require(same_span<type>(name, before.name));
        FORMAT_FIELDS(UNCHANGED)
#undef UNCHANGED
    }
    void tails_unchanged(const format_storage& before) const {
#define TAIL(name, cap, type, count) require(same_span<type>(std::span(name).subspan(counts.count), std::span(before.name).subspan(counts.count)));
        FORMAT_FIELDS(TAIL)
#undef TAIL
    }
};

void compare_format(const format_storage& wire, const hinted_paragraph_generation& p) {
    require(same_span<progpu_native_text_scalar>(std::span(wire.source_scalars).first(p.source_input.size()), p.source_input));
    for (std::size_t i = 0U; i < p.shaping_input.size(); ++i) {
        const auto& scalar = p.shaping_input[i];
        const progpu_native_text_scalar expected{scalar.code_point, scalar.input_index, scalar.input_length,
            scalar.canonical_combining_class, scalar.reserved, scalar.script.value};
        require(same_bytes(wire.admitted_scalars[i], expected));
        const auto& level = p.scalar_levels[i];
        const progpu_native_text_bidi_level bidi{level.input_index, level.input_length, level.level, level.reserved};
        require(same_bytes(wire.scalar_levels[i], bidi));
    }
    require(same_span<progpu_native_text_style_run>(std::span(wire.styles).first(p.styles.size()), p.styles));
    require(same_span<progpu_native_text_style_metrics>(std::span(wire.source_metrics).first(p.source_metrics.size()), p.source_metrics));
    for (std::size_t i = 0U; i < p.runs.size(); ++i) {
        const auto& run = p.runs[i];
        const progpu_native_hinted_paragraph_run expected{run.scalar_start, run.scalar_count, run.logical_start,
            run.logical_count, run.font_index, run.style_index, run.bidi_level, run.source_scale,
            run.logical_units_per_physical_pixel, static_cast<std::uint32_t>(run.generation->source_descriptor_count)};
        require(same_bytes(wire.runs[i], expected));
    }
    for (std::size_t i = 0U; i < p.logical_glyphs.size(); ++i) {
        const auto& g = p.logical_glyphs[i]; const auto owner = p.logical_owners[i];
        const progpu_native_text_shaping_glyph expected{g.glyph_id, g.code_point, g.cluster, static_cast<std::uint32_t>(g.flags),
            g.advance_x, g.advance_y, g.offset_x, g.offset_y};
        const progpu_native_hinted_paragraph_glyph_owner map{owner.run_index, owner.run_glyph_index, owner.descriptor_index};
        require(same_bytes(wire.logical_glyphs[i], expected) && same_bytes(wire.logical_owners[i], map));
        require(wire.logical_cluster_ends[i] == p.logical_cluster_ends[i] && wire.logical_bidi_levels[i] == p.logical_bidi_levels[i]);
        require(wire.glyph_scales[i] == p.glyph_scales[i] && wire.glyph_scales[i] ==
            p.runs[owner.run_index].logical_units_per_physical_pixel / 64.0F);
        const auto& original = p.runs[owner.run_index].generation->glyphs[owner.run_glyph_index];
        require(g.glyph_id == original.glyph_id && g.advance_x == original.advance_x && g.offset_x == original.offset_x &&
            g.advance_y == -original.advance_y && g.offset_y == -original.offset_y);
    }
    for (std::size_t i = 0U; i < p.glyphs.size(); ++i) {
        const auto& g = p.glyphs[i]; const auto owner = p.positioned_owners[i];
        const progpu_native_positioned_text_glyph expected{g.glyph_index, g.glyph_id,
            p.runs[owner.run_index].font_index, g.cluster, g.x, g.y, g.advance_x, g.advance_y};
        const progpu_native_hinted_paragraph_glyph_owner map{owner.run_index, owner.run_glyph_index, owner.descriptor_index};
        require(same_bytes(wire.positioned_glyphs[i], expected) && same_bytes(wire.positioned_owners[i], map));
        require(wire.positioned_cluster_ends[i] == p.cluster_ends[i] && wire.positioned_bidi_levels[i] == p.bidi_levels[i]);
    }
    for (std::size_t i = 0U; i < p.lines.size(); ++i) {
        const auto& line = p.lines[i];
        const progpu_native_positioned_text_line expected{line.glyph_start, line.glyph_count, line.input_start, line.input_end,
            line.width, line.baseline_y, line.height, static_cast<std::uint8_t>(line.clipped), line.flags, line.reserved1, line.reserved2};
        require(same_bytes(wire.lines[i], expected) && wire.line_origins[i] == p.line_origins[i]);
    }
}

void compare_interaction(std::span<const progpu_native_text_cluster_box> boxes,
    std::span<const progpu_native_text_caret_stop> carets, const hinted_paragraph_interaction& reference) {
    for (std::size_t i = 0U; i < reference.boxes().size(); ++i) {
        const auto& b = reference.boxes()[i];
        const progpu_native_text_cluster_box expected{b.input_start, b.input_end, b.line_index, b.bidi_level,
            b.reserved0, b.reserved1, b.reserved2, b.x, b.y, b.width, b.height};
        require(same_bytes(boxes[i], expected));
    }
    for (std::size_t i = 0U; i < reference.carets().size(); ++i) {
        const auto& c = reference.carets()[i];
        const progpu_native_text_caret_stop expected{c.input_position, c.line_index, c.x, c.y, c.height,
            c.bidi_level, static_cast<std::uint8_t>(c.trailing), c.reserved0, c.reserved1};
        require(same_bytes(carets[i], expected));
    }
}

void format_failures(const progpu_native_hinted_paragraph* handle, const progpu_native_hinted_paragraph_counts& counts) {
    format_storage storage(counts); const auto before = storage;
    auto buffers = storage.buffers(); const auto record = buffers;
    const auto reject = [&] {
        require(progpu_native_hinted_paragraph_copy_format(handle, &buffers) == PROGPU_NATIVE_STATUS_INVALID_ARGUMENT);
        storage.unchanged(before);
    };
    // Each independent destination must be preflighted before any earlier
    // formatted field is written. Required counts, not arbitrary small buffers.
#define SHORT(name, cap, type, count) \
    require(counts.count != 0U); buffers.cap = counts.count - 1U; reject(); buffers = record;
    FORMAT_FIELDS(SHORT)
#undef SHORT
    buffers.reserved = 1U; reject(); buffers = record;
    --buffers.struct_size; reject(); buffers = record;
    buffers.positioned_bidi_levels = reinterpret_cast<std::int8_t*>(storage.logical_glyphs.data() + counts.logical_glyph_count);
    reject(); buffers = record; // Cross-output overlap exists ONLY in the declared unused tail.
    buffers.source_scalars = reinterpret_cast<progpu_native_text_scalar*>(&buffers);
    reject(); buffers = record; // Buffer record self-alias.
    buffers.logical_glyphs = nullptr; reject(); buffers = record;
    buffers.lines = reinterpret_cast<progpu_native_positioned_text_line*>(
        reinterpret_cast<std::byte*>(storage.lines.data()) + 1U);
    reject(); buffers = record;
    const auto near_end = std::numeric_limits<std::uintptr_t>::max() & ~(std::uintptr_t{alignof(progpu_native_text_scalar)} - 1U);
    buffers.source_scalars = reinterpret_cast<progpu_native_text_scalar*>(near_end);
    reject(); buffers = record; // Wrapped full declared pointer range, never dereferenced.
    const auto actual = select_hinted_paragraph_generation(handle);
    require(actual != nullptr && actual->source_input.size() == counts.source_scalar_count);
    const auto source_copy = actual->source_input;
    buffers.source_scalars = const_cast<progpu_native_text_scalar*>(actual->source_input.data());
    buffers.source_scalar_capacity = counts.source_scalar_count;
    reject(); buffers = record;
    require(same_span<progpu_native_text_scalar>(actual->source_input, source_copy));
    const auto raw = actual->runs.front().generation->batch->glyphs;
    require(!raw.empty() && !raw.front().points.empty());
    buffers.logical_bidi_levels = reinterpret_cast<std::int8_t*>(const_cast<hinted_outline_point*>(
        actual->runs.front().generation->batch->glyphs.front().points.data()));
    reject(); buffers = record;
    require(actual->runs.front().generation->batch->glyphs == raw);
    const auto& owned_origins = actual->line_origins;
    require(owned_origins.capacity() > owned_origins.size());
    buffers.line_origins = const_cast<float*>(owned_origins.data() + owned_origins.size());
    buffers.line_origin_capacity = counts.line_count;
    reject(); buffers = record; // Actual retained spare storage, not a copied/fake generation.
    const auto spare_address = (reinterpret_cast<std::uintptr_t>(owned_origins.data() + owned_origins.size()) +
        alignof(progpu_native_hinted_paragraph_format_buffers) - 1U) &
        ~(std::uintptr_t{alignof(progpu_native_hinted_paragraph_format_buffers)} - 1U);
    require(spare_address < reinterpret_cast<std::uintptr_t>(owned_origins.data() + owned_origins.capacity()));
    require(progpu_native_hinted_paragraph_copy_format(handle,
        reinterpret_cast<const progpu_native_hinted_paragraph_format_buffers*>(spare_address)) == PROGPU_NATIVE_STATUS_INVALID_ARGUMENT);
    storage.unchanged(before); // Invalid owned-spare input record must be rejected before header dereference.
    require(progpu_native_hinted_paragraph_copy_format(handle, &buffers) == PROGPU_NATIVE_STATUS_SUCCESS);
    compare_format(storage, *actual); storage.tails_unchanged(before);
    require(same_bytes(buffers, record));
}

void interaction_failures(const progpu_native_hinted_paragraph* handle,
    const progpu_native_hinted_paragraph_counts& counts, const hinted_paragraph_interaction& reference) {
    const auto caret_bytes = (static_cast<std::size_t>(counts.caret_stop_count) + 3U) * sizeof(progpu_native_text_caret_stop);
    const auto spare_boxes = (caret_bytes + sizeof(progpu_native_text_cluster_box) - 1U) / sizeof(progpu_native_text_cluster_box);
    auto boxes = sentinels<progpu_native_text_cluster_box>(counts.cluster_box_count + spare_boxes + 3U);
    auto carets = sentinels<progpu_native_text_caret_stop>(counts.caret_stop_count + 3U);
    const auto old_boxes = boxes; const auto old_carets = carets;
    const auto unchanged = [&] {
        require(same_span<progpu_native_text_cluster_box>(boxes, old_boxes) && same_span<progpu_native_text_caret_stop>(carets, old_carets));
    };
    const auto copy = [&](progpu_native_text_cluster_box* b, std::uint32_t bn,
        progpu_native_text_caret_stop* c, std::uint32_t cn) {
        return progpu_native_hinted_paragraph_copy_interaction(handle, b, bn, c, cn);
    };
    require(counts.cluster_box_count != 0U && counts.caret_stop_count != 0U);
    require(copy(boxes.data(), counts.cluster_box_count - 1U, carets.data(), static_cast<std::uint32_t>(carets.size())) ==
        PROGPU_NATIVE_STATUS_INVALID_ARGUMENT); unchanged();
    require(copy(boxes.data(), static_cast<std::uint32_t>(boxes.size()), carets.data(), counts.caret_stop_count - 1U) ==
        PROGPU_NATIVE_STATUS_INVALID_ARGUMENT); unchanged();
    require(copy(boxes.data(), static_cast<std::uint32_t>(boxes.size()),
        reinterpret_cast<progpu_native_text_caret_stop*>(boxes.data() + counts.cluster_box_count), counts.caret_stop_count) ==
        PROGPU_NATIVE_STATUS_INVALID_ARGUMENT); unchanged(); // Used/spare destination overlap, not used-prefix only.
    const auto owned = select_hinted_paragraph_interaction(handle);
    require(owned != nullptr);
    require(copy(reinterpret_cast<progpu_native_text_cluster_box*>(const_cast<text_cluster_box*>(owned->boxes().data())),
        counts.cluster_box_count, carets.data(), static_cast<std::uint32_t>(carets.size())) ==
        PROGPU_NATIVE_STATUS_INVALID_ARGUMENT); unchanged();
    require(copy(boxes.data(), static_cast<std::uint32_t>(boxes.size()),
        reinterpret_cast<progpu_native_text_caret_stop*>(const_cast<text_caret_stop*>(owned->carets().data())), counts.caret_stop_count) ==
        PROGPU_NATIVE_STATUS_INVALID_ARGUMENT); unchanged();
    require(copy(boxes.data(), static_cast<std::uint32_t>(boxes.size()), carets.data(), static_cast<std::uint32_t>(carets.size())) ==
        PROGPU_NATIVE_STATUS_SUCCESS);
    compare_interaction(boxes, carets, reference);
    require(same_span<progpu_native_text_cluster_box>(std::span(boxes).subspan(counts.cluster_box_count), std::span(old_boxes).subspan(counts.cluster_box_count)));
    require(same_span<progpu_native_text_caret_stop>(std::span(carets).subspan(counts.caret_stop_count), std::span(old_carets).subspan(counts.caret_stop_count)));
}

void counts_and_aliases(const progpu_native_hinted_paragraph* handle,
    const hinted_paragraph_generation& reference, const hinted_paragraph_interaction& interaction) {
    alignas(progpu_native_text_paragraph_result) auto counts = sentinel_output<progpu_native_hinted_paragraph_counts>();
    auto diagnostic = sentinel_output<progpu_native_text_paragraph_result>();
    const auto before_counts = counts; const auto before_diagnostic = diagnostic;
    require(progpu_native_hinted_paragraph_get_counts(handle, &counts.value, &diagnostic.value) == PROGPU_NATIVE_STATUS_SUCCESS);
    require(same_bytes(counts.value, expected_counts(reference, interaction)) && counts.tail == before_counts.tail && diagnostic.tail == before_diagnostic.tail);
    const auto actual = select_hinted_paragraph_generation(handle);
    require(actual != nullptr && same_bytes(diagnostic.value, actual->paragraph_result));
    require(diagnostic.value.glyph_count == reference.glyphs.size() && diagnostic.value.line_count == reference.lines.size() &&
        diagnostic.value.shaped_glyph_count == reference.logical_glyphs.size() && diagnostic.value.paragraph_level == reference.paragraph_level);
    counts = before_counts; diagnostic = before_diagnostic;
    require(progpu_native_hinted_paragraph_get_counts(handle, &counts.value,
        reinterpret_cast<progpu_native_text_paragraph_result*>(&counts.value)) == PROGPU_NATIVE_STATUS_INVALID_ARGUMENT);
    require(same_bytes(counts, before_counts) && same_bytes(diagnostic, before_diagnostic));
    require(progpu_native_hinted_paragraph_get_counts(handle,
        reinterpret_cast<progpu_native_hinted_paragraph_counts*>(const_cast<progpu_native_hinted_paragraph*>(handle)),
        &diagnostic.value) == PROGPU_NATIVE_STATUS_INVALID_ARGUMENT);
    require(same_bytes(diagnostic, before_diagnostic));
    for (auto* output : {reinterpret_cast<progpu_native_hinted_paragraph_counts*>(const_cast<progpu_native_text_scalar*>(actual->source_input.data())),
            reinterpret_cast<progpu_native_hinted_paragraph_counts*>(const_cast<float*>(actual->line_origins.data() + actual->line_origins.size())),
            reinterpret_cast<progpu_native_hinted_paragraph_counts*>(const_cast<std::byte*>(actual->font_sources[0]->bytes.data()))}) {
        require(progpu_native_hinted_paragraph_get_counts(handle, output, &diagnostic.value) == PROGPU_NATIVE_STATUS_INVALID_ARGUMENT);
        require(same_bytes(diagnostic, before_diagnostic));
    }
    const auto spare_address = (reinterpret_cast<std::uintptr_t>(actual->logical_owners.data() + actual->logical_owners.size()) +
        alignof(progpu_native_text_paragraph_result) - 1U) & ~(std::uintptr_t{alignof(progpu_native_text_paragraph_result)} - 1U);
    require(spare_address + sizeof(progpu_native_text_paragraph_result) <=
        reinterpret_cast<std::uintptr_t>(actual->logical_owners.data() + actual->logical_owners.capacity()));
    require(progpu_native_hinted_paragraph_get_counts(handle, &counts.value,
        reinterpret_cast<progpu_native_text_paragraph_result*>(spare_address)) ==
        PROGPU_NATIVE_STATUS_INVALID_ARGUMENT);
    require(same_bytes(counts, before_counts));
    require(progpu_native_hinted_paragraph_get_counts(nullptr, &counts.value, &diagnostic.value) == PROGPU_NATIVE_STATUS_INVALID_ARGUMENT);
    require(same_bytes(counts, before_counts) && same_bytes(diagnostic, before_diagnostic));
}

progpu_native_hinted_paragraph_frame_request frame_request() {
    return {PROGPU_NATIVE_ABI_VERSION, sizeof(progpu_native_hinted_paragraph_frame_request), 320U, 180U,
        1.25F, std::uintptr_t{0x773U}, {10.25F, 20.5F}, {0.125F, 0.25F, 0.5F, 0.75F},
        PROGPU_NATIVE_HINTED_PROJECTION_SCALAR_REFERENCE, PROGPU_NATIVE_HINTED_COVERAGE_NONZERO_VECTOR, 0U};
}

std::array<progpu_native_color, 3U> colors() {
    return {{{0.875F, 0.125F, 0.25F, 0.75F}, {0.25F, 0.5F, 0.875F, 0.625F}, {-717.0F, -719.0F, -723.0F, -727.0F}}};
}

void compare_frame(const progpu_native_glyph_frame& wire, const hinted_paragraph_glyph_frame& reference) {
    const auto expected = reference.frame();
    require(wire.struct_size == sizeof(wire) && wire.width == expected.width && wire.height == expected.height &&
        wire.dpi_scale == expected.dpi_scale && wire.target_view == expected.target_view && same_bytes(wire.clear_color, expected.clear_color));
    require(wire.flags == 0U && wire.content_revision == 0U && wire.draw_state == nullptr);
    require(same_span<progpu_native_glyph_outline>({wire.outlines, wire.outline_count}, reference.outlines()) &&
        same_span<progpu_native_path_segment>({wire.segments, wire.segment_count}, reference.segments()) &&
        same_span<progpu_native_positioned_glyph>({wire.glyphs, wire.glyph_count}, reference.glyphs()));
}

void prepare_and_borrow(const progpu_native_hinted_paragraph* handle,
    std::shared_ptr<const hinted_paragraph_generation> reference) {
    auto request = frame_request(); const auto request_before = request;
    auto paints = colors(); const auto before_paints = paints;
    frame_owner frame;
    auto publication = sentinel_output<progpu_native_hinted_paragraph_frame*>(); const auto publication_tail = publication.tail;
    require(progpu_native_hinted_paragraph_prepare_frame(handle, &request, paints.data(), 2U, &publication.value) == PROGPU_NATIVE_STATUS_SUCCESS &&
        publication.tail == publication_tail);
    frame.value = publication.value;
    const auto original = create_hinted_paragraph_glyph_frame(reference,
        {request.width, request.height, request.dpi_scale, request.target_view, request.logical_origin, request.clear_color},
        std::span(paints).first(2U), hinted_projection_policy::scalar_reference, hinted_outline_coverage::nonzero_vector);
    require(original.status == PROGPU_NATIVE_STATUS_SUCCESS && original.generation != nullptr);
    auto wire = sentinel_output<progpu_native_glyph_frame>(); const auto untouched = wire;
    require(progpu_native_hinted_paragraph_frame_borrow(frame.value, &wire.value) == PROGPU_NATIVE_STATUS_SUCCESS);
    compare_frame(wire.value, *original.generation); require(wire.tail == untouched.tail);
    const auto actual = select_hinted_paragraph_frame_generation(frame.value);
    require(actual != nullptr && actual->paragraph() == select_hinted_paragraph_generation(handle));
    require(wire.value.glyphs == actual->glyphs().data() && wire.value.outlines == actual->outlines().data());
    require(same_bytes(request, request_before) && same_bytes(paints, before_paints));
    const auto reject = [&](progpu_native_status expected) {
        auto old_frame = sentinel_output<progpu_native_hinted_paragraph_frame*>(); old_frame.value = frame.value; const auto before = old_frame;
        require(progpu_native_hinted_paragraph_prepare_frame(handle, &request, paints.data(), 2U, &old_frame.value) == expected && same_bytes(old_frame, before));
        auto again = sentinel_output<progpu_native_glyph_frame>();
        require(progpu_native_hinted_paragraph_frame_borrow(frame.value, &again.value) == PROGPU_NATIVE_STATUS_SUCCESS);
        compare_frame(again.value, *original.generation);
    };
    request.coverage = PROGPU_NATIVE_HINTED_COVERAGE_STRICT; reject(PROGPU_NATIVE_STATUS_UNSUPPORTED); request = request_before;
    request.coverage = UINT32_MAX; reject(PROGPU_NATIVE_STATUS_INVALID_ARGUMENT); request = request_before;
    request.projection_policy = PROGPU_NATIVE_HINTED_PROJECTION_GPU_SHADER; reject(PROGPU_NATIVE_STATUS_UNSUPPORTED); request = request_before;
    request.dpi_scale = 2.0F; reject(PROGPU_NATIVE_STATUS_UNSUPPORTED); request = request_before;
    request.reserved = 1U; reject(PROGPU_NATIVE_STATUS_INVALID_ARGUMENT); request = request_before;
    auto old_frame = frame.value;
    require(progpu_native_hinted_paragraph_prepare_frame(handle, &request, paints.data(), 1U, &old_frame) == PROGPU_NATIVE_STATUS_INVALID_ARGUMENT && old_frame == frame.value);
    require(progpu_native_hinted_paragraph_prepare_frame(handle, &request, paints.data(), 3U, &old_frame) == PROGPU_NATIVE_STATUS_INVALID_ARGUMENT && old_frame == frame.value);
    const auto paragraph = select_hinted_paragraph_generation(handle);
    const auto spare_address = (reinterpret_cast<std::uintptr_t>(paragraph->line_origins.data() + paragraph->line_origins.size()) +
        alignof(progpu_native_hinted_paragraph_frame*) - 1U) & ~(std::uintptr_t{alignof(progpu_native_hinted_paragraph_frame*)} - 1U);
    require(spare_address < reinterpret_cast<std::uintptr_t>(paragraph->line_origins.data() + paragraph->line_origins.capacity()));
    require(progpu_native_hinted_paragraph_prepare_frame(handle, &request, paints.data(), 2U,
        reinterpret_cast<progpu_native_hinted_paragraph_frame**>(spare_address)) ==
        PROGPU_NATIVE_STATUS_INVALID_ARGUMENT);
    require(progpu_native_hinted_paragraph_prepare_frame(handle, &request, paints.data(), 2U,
        reinterpret_cast<progpu_native_hinted_paragraph_frame**>(&request)) == PROGPU_NATIVE_STATUS_INVALID_ARGUMENT);
    require(progpu_native_hinted_paragraph_prepare_frame(handle,
        reinterpret_cast<const progpu_native_hinted_paragraph_frame_request*>(spare_address), paints.data(), 2U, &old_frame) ==
        PROGPU_NATIVE_STATUS_INVALID_ARGUMENT && old_frame == frame.value);
    wire = untouched;
    require(progpu_native_hinted_paragraph_frame_borrow(frame.value,
        reinterpret_cast<progpu_native_glyph_frame*>(const_cast<progpu_native_hinted_paragraph_frame*>(frame.value))) == PROGPU_NATIVE_STATUS_INVALID_ARGUMENT);
    require(progpu_native_hinted_paragraph_frame_borrow(frame.value,
        reinterpret_cast<progpu_native_glyph_frame*>(const_cast<progpu_native_positioned_glyph*>(actual->glyphs().data()))) == PROGPU_NATIVE_STATUS_INVALID_ARGUMENT);
    // Actual factory reserves one instance per positioned item; omitted no-ink
    // items leave owned spare capacity. Borrow must reject the unused storage.
    require(paragraph->glyphs.size() > actual->glyphs().size());
    require((paragraph->glyphs.size() - actual->glyphs().size()) * sizeof(progpu_native_positioned_glyph) >= sizeof(progpu_native_glyph_frame));
    require(progpu_native_hinted_paragraph_frame_borrow(frame.value,
        reinterpret_cast<progpu_native_glyph_frame*>(const_cast<progpu_native_positioned_glyph*>(actual->glyphs().data() + actual->glyphs().size()))) ==
        PROGPU_NATIVE_STATUS_INVALID_ARGUMENT);
    require(progpu_native_hinted_paragraph_frame_borrow(nullptr, &wire.value) == PROGPU_NATIVE_STATUS_INVALID_ARGUMENT && same_bytes(wire, untouched));
    require(same_bytes(request, request_before) && same_bytes(paints, before_paints));
}

void factory_failures(fixture& source, const progpu_native_hinted_paragraph* existing) {
    alignas(progpu_native_text_paragraph_result) auto devices = device_wire(source);
    const auto original_devices = devices; const auto original_metrics = source.metrics;
    const auto original_styles = source.styles; const auto original_shape = source.shaping; const auto original_layout = source.layout;
    auto diagnostic = sentinel_output<progpu_native_text_paragraph_result>(); const auto untouched = diagnostic;
    auto* old = const_cast<progpu_native_hinted_paragraph*>(existing);
    const auto reject = [&](const std::int32_t* axes = nullptr, std::uint32_t axis_count = 0U,
        progpu_native_status expected = PROGPU_NATIVE_STATUS_INVALID_ARGUMENT, std::uint32_t style_count = 2U) {
        auto publication = sentinel_output<progpu_native_hinted_paragraph*>(); publication.value = old; const auto publication_before = publication;
        const auto before_devices = devices; const auto before_metrics = source.metrics;
        const auto before_styles = source.styles; const auto before_input = source.input;
        require(progpu_native_text_context_layout_hinted_paragraph(source.context.value, &source.shaping, &source.layout,
            source.styles.data(), 2U, source.metrics.data(), devices.data(), style_count, axes, axis_count,
            &publication.value, &diagnostic.value) == expected);
        require(same_bytes(publication, publication_before) && same_bytes(diagnostic, untouched) && same_bytes(devices, before_devices) &&
            same_bytes(source.metrics, before_metrics) && same_bytes(source.styles, before_styles) &&
            same_span<progpu_native_text_scalar>(source.input, before_input));
    };
    devices[1].reserved = 1U; reject(); devices = original_devices;
    devices[1].font_index = 0U; reject(); devices = original_devices;
    devices[1].source_scale = std::nextafter(devices[1].source_scale, 1.0F); reject(); devices = original_devices;
    devices[1].logical_units_per_physical_pixel = 0.0F; reject(); devices = original_devices;
    devices[1].x_phase_26_6 = 64U; reject(); devices = original_devices;
    devices[1].interpreter = 99U; reject(); devices = original_devices;
    devices[1].variation_start = UINT32_MAX; devices[1].variation_count = 1U; reject(); devices = original_devices;
    alignas(progpu_native_hinted_paragraph*) std::array<std::int32_t, 2U> axes{-65536, 65536}; const auto before_axes = axes;
    devices[1].variation_start = 2U; devices[1].variation_count = 1U; reject(axes.data(), 2U); devices = original_devices;
    devices[1].variation_count = 65536U; reject(axes.data(), 2U); devices = original_devices;
    reject(nullptr, 1U);
    const auto near_end = std::numeric_limits<std::uintptr_t>::max() & ~(std::uintptr_t{alignof(std::int32_t)} - 1U);
    reject(reinterpret_cast<const std::int32_t*>(near_end), 2U);
    reject(nullptr, 0U, PROGPU_NATIVE_STATUS_INVALID_ARGUMENT, 1U);
    source.metrics[1].ascent = std::numeric_limits<float>::quiet_NaN(); reject(); source.metrics = original_metrics;
    source.shaping.reserved1 = 1U; reject(); source.shaping = original_shape;
    source.layout.trimming = PROGPU_NATIVE_TEXT_TRIMMING_CHARACTER_ELLIPSIS; reject(); source.layout = original_layout;
    // No-feature input still uses original default liga. Explicitly disable
    // ONLY the later style's liga, leaving earlier original runs unchanged.
    const std::array<progpu_native_text_feature, 3U> disabled_liga{source.features[0], source.features[1],
        progpu_native_text_feature{0x6C696761U, 0U, 0U, UINT32_MAX}};
    source.shaping.features = disabled_liga.data(); source.shaping.feature_count = 3U;
    source.styles[1].feature_start = 2U; source.styles[1].feature_count = 1U;
    reject(nullptr, 0U, PROGPU_NATIVE_STATUS_INTERNAL_ERROR); source.styles = original_styles; source.shaping = original_shape;
    // A failure in the later actual font's instruction-fault B occurs after
    // earlier runs were captured, yet neither publication nor diagnostics/tail
    // may expose that partial generation. No failed artifact is qualified.
    require(axes == before_axes);
    const auto alias_reject = [&](progpu_native_hinted_paragraph** output, progpu_native_text_paragraph_result* result,
        const std::int32_t* flat_axes = nullptr, std::uint32_t axis_count = 0U) {
        require(progpu_native_text_context_layout_hinted_paragraph(source.context.value, &source.shaping, &source.layout,
            source.styles.data(), 2U, source.metrics.data(), devices.data(), 2U, flat_axes, axis_count,
            output, result) == PROGPU_NATIVE_STATUS_INVALID_ARGUMENT);
        require(same_bytes(diagnostic, untouched) && same_bytes(devices, original_devices) &&
            same_bytes(source.metrics, original_metrics) && same_bytes(source.styles, original_styles));
    };
    const auto old_input = source.input;
    alias_reject(reinterpret_cast<progpu_native_hinted_paragraph**>(source.input.data()), &diagnostic.value);
    require(same_span<progpu_native_text_scalar>(source.input, old_input));
    alias_reject(reinterpret_cast<progpu_native_hinted_paragraph**>(&source.shaping), &diagnostic.value);
    alias_reject(&old, reinterpret_cast<progpu_native_text_paragraph_result*>(devices.data()));
    alias_reject(reinterpret_cast<progpu_native_hinted_paragraph**>(&diagnostic.value), &diagnostic.value);
    alias_reject(reinterpret_cast<progpu_native_hinted_paragraph**>(axes.data()), &diagnostic.value, axes.data(), 2U);
    require(axes == before_axes && old == existing && same_bytes(source.shaping, original_shape));
    const auto actual = select_hinted_paragraph_generation(existing);
    const auto font_bytes = actual->font_sources[0]->bytes;
    alias_reject(reinterpret_cast<progpu_native_hinted_paragraph**>(const_cast<std::byte*>(actual->font_sources[0]->bytes.data())), &diagnostic.value);
    require(actual->font_sources[0]->bytes == font_bytes);
}

void actual_transport_controls() {
    for (const auto policy : {font_hint_policy::truetype_35, font_hint_policy::truetype_40}) {
        for (const bool source_bidi : {false, true}) {
            fixture source(policy, source_bidi);
            const auto reference = source.reference();
            const auto interaction = create_hinted_paragraph_interaction(reference);
            require(interaction.status == PROGPU_NATIVE_STATUS_SUCCESS && interaction.generation != nullptr);
            paragraph_owner handle; produce(source, handle);
            const auto actual = select_hinted_paragraph_generation(handle.value);
            require(actual != nullptr && actual != reference && actual->source_digit_bidi == source_bidi);
            require(actual->font_sources[0] != actual->font_sources[1]);
            require(actual->source_input[3].code_point == '1' && actual->shaping_input[3].code_point == 0x0661U &&
                actual->scalar_levels[3].level == (source_bidi ? 0 : 2));
            for (std::size_t i = 0U; i < reference->runs.size(); ++i)
                require(actual->runs[i].generation->batch->glyphs == reference->runs[i].generation->batch->glyphs &&
                    actual->runs[i].generation->descriptor_indices == reference->runs[i].generation->descriptor_indices);
            counts_and_aliases(handle.value, *reference, *interaction.generation);
            const auto counts = expected_counts(*reference, *interaction.generation);
            format_storage copied(counts); const auto tails = copied; const auto buffers = copied.buffers();
            require(progpu_native_hinted_paragraph_copy_format(handle.value, &buffers) == PROGPU_NATIVE_STATUS_SUCCESS);
            compare_format(copied, *reference); copied.tails_unchanged(tails);
            format_failures(handle.value, counts); interaction_failures(handle.value, counts, *interaction.generation);
            prepare_and_borrow(handle.value, reference); factory_failures(source, handle.value);
        }
    }
    // Transport the actual writer's L1-used trailing whitespace levels, not a
    // reconstruction from pre-L1 logical levels or positioned ink offsets.
    fixture trailing(font_hint_policy::truetype_40, true);
    // Original paragraph CPU L1 fixture: the space before the final Alef is
    // internal paragraph text with odd logical level, then ends a wrapped row.
    const std::array<std::uint32_t, 6U> l1_values{'A', 0x0627U, ' ', '1', ' ', 0x0627U};
    trailing.input.clear();
    std::uint32_t l1_source = 9U;
    for (const auto value : l1_values) {
        const auto length = value == 'A' ? std::uint16_t{2U} : std::uint16_t{1U};
        trailing.input.push_back({value, l1_source, length, 0U, 0U, 0U}); l1_source += length;
    }
    trailing.styles[0].scalar_count = 1U;
    trailing.styles[1].scalar_start = 1U; trailing.styles[1].scalar_count = 5U;
    trailing.shaping.input = trailing.input.data(); trailing.shaping.input_count = static_cast<std::uint32_t>(trailing.input.size());
    const auto unwrapped = trailing.reference();
    require(unwrapped->logical_glyphs.size() == l1_values.size());
    float prefix = 0.0F;
    for (std::size_t i = 0U; i < 5U; ++i)
        prefix += static_cast<float>(unwrapped->logical_glyphs[i].advance_x) * unwrapped->glyph_scales[i];
    const float last = static_cast<float>(unwrapped->logical_glyphs.back().advance_x) * unwrapped->glyph_scales.back();
    require(last > 0.0F && unwrapped->logical_glyphs[4].code_point == ' ' && unwrapped->logical_bidi_levels[4] == 1);
    trailing.layout.maximum_width = prefix + last * 0.5F;
    const auto l1 = trailing.reference(); const auto l1_interaction = create_hinted_paragraph_interaction(l1);
    require(l1_interaction.status == PROGPU_NATIVE_STATUS_SUCCESS && l1->lines.size() == 2U && l1->lines[0].glyph_count == 5U);
    require(std::any_of(l1->glyphs.begin(), l1->glyphs.end(), [&](const auto& glyph) {
        const auto index = static_cast<std::size_t>(&glyph - l1->glyphs.data());
        return l1->bidi_levels[index] != l1->logical_bidi_levels[glyph.glyph_index];
    }));
    const auto wrapped_space = std::find_if(l1->glyphs.begin(), l1->glyphs.end(), [](const auto& glyph) { return glyph.glyph_index == 4U; });
    require(wrapped_space != l1->glyphs.end() && l1->logical_bidi_levels[4] == 1 &&
        l1->bidi_levels[static_cast<std::size_t>(wrapped_space - l1->glyphs.begin())] == 0);
    paragraph_owner l1_handle; produce(trailing, l1_handle);
    format_storage l1_wire(expected_counts(*l1, *l1_interaction.generation)); const auto l1_buffers = l1_wire.buffers();
    require(progpu_native_hinted_paragraph_copy_format(l1_handle.value, &l1_buffers) == PROGPU_NATIVE_STATUS_SUCCESS);
    compare_format(l1_wire, *l1);
    // Real CRLF source ranges and max-lines: the full logical/font generation
    // remains owned/copied even when only its first row is positioned.
    fixture hard(font_hint_policy::truetype_40, false);
    hard.input[1].code_point = '\r'; hard.input[2].code_point = '\n'; hard.input[3].code_point = 'B'; hard.input[4].code_point = ' ';
    hard.layout.maximum_lines = 1U;
    const auto limited = hard.reference(); const auto limited_interaction = create_hinted_paragraph_interaction(limited);
    require(limited_interaction.status == PROGPU_NATIVE_STATUS_SUCCESS && limited->lines.size() == 1U &&
        limited->glyphs.size() < limited->logical_glyphs.size() && limited->source_input.size() == hard.input.size());
    paragraph_owner limited_handle; produce(hard, limited_handle);
    const auto counts = expected_counts(*limited, *limited_interaction.generation);
    format_storage limited_wire(counts); const auto limited_buffers = limited_wire.buffers();
    require(progpu_native_hinted_paragraph_copy_format(limited_handle.value, &limited_buffers) == PROGPU_NATIVE_STATUS_SUCCESS);
    compare_format(limited_wire, *limited); counts_and_aliases(limited_handle.value, *limited, *limited_interaction.generation);
}

void retirement_controls() {
    paragraph_owner paragraph;
    frame_owner frame;
    std::weak_ptr<const hinted_paragraph_generation> paragraph_weak;
    std::weak_ptr<const hinted_paragraph_interaction> interaction_weak;
    std::weak_ptr<const hinted_shaped_run> run_weak;
    std::weak_ptr<const owned_font_source> font_weak;
    std::vector<std::vector<hinted_glyph>> raw;
    progpu_native_hinted_paragraph_counts counts{};
    auto paints = colors(); const auto owned_paints = paints;
    const auto request = frame_request();
    {
        fixture source(font_hint_policy::truetype_35, true);
        produce(source, paragraph);
        {
            const auto generation = select_hinted_paragraph_generation(paragraph.value);
            paragraph_weak = generation; interaction_weak = select_hinted_paragraph_interaction(paragraph.value);
            run_weak = generation->runs.front().generation; font_weak = generation->font_sources[0];
            for (const auto& run : generation->runs) raw.push_back(run.generation->batch->glyphs);
        }
        progpu_native_text_paragraph_result result{};
        require(progpu_native_hinted_paragraph_get_counts(paragraph.value, &counts, &result) == PROGPU_NATIVE_STATUS_SUCCESS);
        require(progpu_native_hinted_paragraph_prepare_frame(paragraph.value, &request, paints.data(), 2U, &frame.value) == PROGPU_NATIVE_STATUS_SUCCESS);
        std::fill(source.bytes.begin(), source.bytes.end(), std::byte{0});
        for (auto& scalar : source.input) scalar.code_point = 0U;
        source.styles.fill({}); source.metrics.fill({}); source.configurations.fill({});
        paints[0] = {}; paints[1] = {};
    } // Actual context retirement, not a fake or borrowed source generation.
    require(!paragraph_weak.expired() && !interaction_weak.expired() && !run_weak.expired() && !font_weak.expired());
    format_storage after_context(counts); const auto context_buffers = after_context.buffers();
    require(progpu_native_hinted_paragraph_copy_format(paragraph.value, &context_buffers) == PROGPU_NATIVE_STATUS_SUCCESS);
    auto boxes = sentinels<progpu_native_text_cluster_box>(counts.cluster_box_count + 1U);
    auto carets = sentinels<progpu_native_text_caret_stop>(counts.caret_stop_count + 1U);
    const auto box_tail = boxes.back(); const auto caret_tail = carets.back();
    require(progpu_native_hinted_paragraph_copy_interaction(paragraph.value, boxes.data(), counts.cluster_box_count,
        carets.data(), counts.caret_stop_count) == PROGPU_NATIVE_STATUS_SUCCESS);
    require(same_bytes(boxes.back(), box_tail) && same_bytes(carets.back(), caret_tail));
    progpu_native_hinted_paragraph_destroy(paragraph.value); paragraph.value = nullptr;
    require(!paragraph_weak.expired() && !interaction_weak.expired()); // Prepared frame retains both same original owners.
    auto wire = sentinel_output<progpu_native_glyph_frame>(); const auto tail = wire.tail;
    require(progpu_native_hinted_paragraph_frame_borrow(frame.value, &wire.value) == PROGPU_NATIVE_STATUS_SUCCESS && wire.tail == tail);
    {
        const auto owned = select_hinted_paragraph_frame_generation(frame.value);
        require(owned != nullptr && owned->paragraph() == paragraph_weak.lock());
        compare_format(after_context, *owned->paragraph()); compare_frame(wire.value, *owned);
        compare_interaction(boxes, carets, *interaction_weak.lock());
        require(same_span<progpu_native_color>(owned->style_colors(), std::span(owned_paints).first(2U)) && same_bytes(paints[2], owned_paints[2]));
        for (std::size_t i = 0U; i < raw.size(); ++i) require(owned->paragraph()->runs[i].generation->batch->glyphs == raw[i]);
    }
    progpu_native_hinted_paragraph_frame_destroy(frame.value); frame.value = nullptr;
    require(paragraph_weak.expired() && interaction_weak.expired() && run_weak.expired() && font_weak.expired());
}
#undef FORMAT_FIELDS
#endif
} // namespace

int main() {
    try {
        auto counts = sentinel_output<progpu_native_hinted_paragraph_counts>(); const auto old_counts = counts;
        auto result = sentinel_output<progpu_native_text_paragraph_result>(); const auto old_result = result;
        auto frame = sentinel_output<progpu_native_glyph_frame>(); const auto old_frame = frame;
        require(progpu_native_hinted_paragraph_get_counts(nullptr, &counts.value, &result.value) == PROGPU_NATIVE_STATUS_INVALID_ARGUMENT &&
            same_bytes(counts, old_counts) && same_bytes(result, old_result));
        require(progpu_native_hinted_paragraph_frame_borrow(nullptr, &frame.value) == PROGPU_NATIVE_STATUS_INVALID_ARGUMENT && same_bytes(frame, old_frame));
        progpu_native_hinted_paragraph_format_buffers empty{}; empty.struct_size = sizeof(empty);
        require(progpu_native_hinted_paragraph_copy_format(nullptr, &empty) == PROGPU_NATIVE_STATUS_INVALID_ARGUMENT);
        progpu_native_hinted_paragraph_destroy(nullptr); progpu_native_hinted_paragraph_frame_destroy(nullptr);
#if defined(PROGPU_NATIVE_FONT_HINTING)
        actual_transport_controls(); retirement_controls();
#else
        // A real original context still exists without FreeType; the additive
        // device factory must fail unsupported, preserving both output objects.
        const auto bytes = progpu::native::tests::make_hinted_shape_font();
        progpu_native_text_context* context = nullptr;
        require(progpu_native_text_context_create(PROGPU_NATIVE_ABI_VERSION,
            reinterpret_cast<const std::uint8_t*>(bytes.data()), bytes.size(), 0U, nullptr, 0U, &context) == PROGPU_NATIVE_STATUS_SUCCESS);
        const progpu_native_text_scalar scalar{'A', 0U, 1U, 0U, 0U, 0U};
        progpu_native_text_shape_request shape{}; shape.struct_size = sizeof(shape); shape.abi_version = PROGPU_NATIVE_ABI_VERSION;
        shape.input = &scalar; shape.input_count = 1U; shape.direction = PROGPU_NATIVE_TEXT_DIRECTION_LEFT_TO_RIGHT;
        progpu_native_text_layout_options layout{}; layout.struct_size = sizeof(layout); layout.scale = 1.0F;
        layout.maximum_width = 200.0F; layout.direction = PROGPU_NATIVE_TEXT_DIRECTION_LEFT_TO_RIGHT;
        const progpu_native_text_style_run style{0U, 1U, 0U, 0.013F, 0U, 0U, 0U, 0U, 0U, 0U, 0U};
        const progpu_native_text_style_metrics metric{18.0F, 4.0F};
        const progpu_native_hinted_paragraph_device_style device{0U, style.scale, 1.0F, 13U * 64U, 13U * 64U, 40U, 0U, 0U, 0U, 0U, 0U};
        auto* paragraph = reinterpret_cast<progpu_native_hinted_paragraph*>(std::uintptr_t{0x773U}); const auto previous = paragraph;
        require(progpu_native_text_context_layout_hinted_paragraph(context, &shape, &layout, &style, 1U, &metric,
            &device, 1U, nullptr, 0U, &paragraph, &result.value) == PROGPU_NATIVE_STATUS_UNSUPPORTED && paragraph == previous && same_bytes(result, old_result));
        progpu_native_text_context_destroy(context);
#endif
        return 0;
    } catch (const std::exception& error) {
        std::cerr << error.what() << '\n'; return 1;
    }
}
