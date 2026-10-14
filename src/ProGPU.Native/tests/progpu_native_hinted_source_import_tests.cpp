#include "progpu_native_text_source_resource.h"
#include "progpu_native_hinted_shape_fixture.hpp"
#include "../src/Text/Interop/progpu_native_hinted_paragraph_glyph_frame.hpp"
#include "../src/Text/Interop/progpu_native_hinted_paragraph_transport_internal.hpp"

#include <algorithm>
#include <array>
#include <cmath>
#include <cstring>
#include <iostream>
#include <limits>
#include <source_location>
#include <stdexcept>
#include <string>

namespace {
using namespace progpu::native::text;
void require(bool value, std::source_location at = std::source_location::current()) {
    if (!value) throw std::runtime_error("source import control at " + std::to_string(at.line()));
}
struct context_owner final {
    progpu_native_text_context* value{};
    ~context_owner() { progpu_native_text_context_destroy(value); }
};
struct paragraph_owner final {
    progpu_native_hinted_paragraph* value{};
    ~paragraph_owner() { progpu_native_hinted_paragraph_destroy(value); }
};
struct resource_owner final {
    progpu_native_hinted_glyph_resource* value{};
    ~resource_owner() { progpu_native_hinted_glyph_resource_destroy(value); }
};
#if defined(PROGPU_NATIVE_FONT_HINTING)
template<class T> void owned_span(const hinted_paragraph_glyph_resource& resource,
    const T* original, const T* owned, std::uint32_t count) {
    if (count == 0U) return;
    const auto bytes = std::size_t{count} * sizeof(T);
    require(owned != original && std::memcmp(owned, original, bytes) == 0 &&
        resource.allocation_aliases(owned, bytes) && resource.allocation_aliases(owned + count - 1U, sizeof(T)) &&
        !resource.allocation_aliases(original, bytes));
}
std::shared_ptr<const hinted_paragraph_glyph_resource> verify_import(progpu_native_hinted_paragraph* paragraph,
    bool expected_slices, bool suffix, bool rtl, std::uint32_t expected_count = 4U, bool expected_soft_spaces = false) {
    resource_owner producer;
    const progpu_native_hinted_glyph_resource_request request{PROGPU_NATIVE_ABI_VERSION, sizeof(request), 1.5F,
        PROGPU_NATIVE_HINTED_PROJECTION_SCALAR_REFERENCE, PROGPU_NATIVE_HINTED_COVERAGE_ANTIALIASED_VECTOR, 0U};
    require(progpu_native_hinted_paragraph_prepare_glyph_resource_with_nominal_metrics(paragraph, &request, &producer.value) ==
        PROGPU_NATIVE_STATUS_SUCCESS);
    progpu_native_hinted_glyph_resource_view raster{};
    progpu_native_hinted_source_glyph_resource_view source{};
    require(progpu_native_hinted_glyph_resource_borrow(producer.value, &raster) == PROGPU_NATIVE_STATUS_SUCCESS &&
        progpu_native_hinted_glyph_resource_borrow_source(producer.value, &source) == PROGPU_NATIVE_STATUS_SUCCESS &&
        raster.counts.logical_glyph_count == expected_count && raster.counts.cluster_box_count == 0U && raster.counts.caret_stop_count == 0U &&
        source.source.box_count != 0U && source.source.caret_count != 0U &&
        (source.slice_count != 0U) == expected_slices && (source.first_logical_glyph != 0U) == suffix &&
        (raster.runs[0].bidi_level & 1) == (rtl ? 1 : 0));
    // A nonempty source generation cannot masquerade as old float interaction.
    require(import_hinted_paragraph_glyph_resource(raster).status == PROGPU_NATIVE_STATUS_INVALID_ARGUMENT);
    auto imported = import_hinted_paragraph_glyph_resource(raster, source);
    require(imported.status == PROGPU_NATIVE_STATUS_SUCCESS && imported.generation != nullptr && imported.generation->paragraph() == nullptr);
    const auto* owned = imported.generation->imported_source_view();
    require(owned != nullptr && owned != &source && owned->source.options.em_size == source.source.options.em_size &&
        owned->source.options.pixels_per_dip == source.source.options.pixels_per_dip &&
        owned->first_logical_glyph == source.first_logical_glyph && imported.generation->allocation_aliases(owned, sizeof(*owned)));
#define OWNED(field, count) owned_span(*imported.generation, source.field, owned->field, (count))
    OWNED(source.styles, source.source.style_count); OWNED(source.logical_metrics, source.source.logical_count);
    OWNED(source.glyph_metrics, source.source.glyph_count); OWNED(source.line_metrics, source.source.line_count);
    OWNED(source.boxes, source.source.box_count); OWNED(source.carets, source.source.caret_count);
    OWNED(raw_logical_glyphs, source.source.logical_count); OWNED(effective_logical_glyphs, source.source.logical_count);
    OWNED(positioning_runs, raster.counts.run_count); OWNED(prepared_glyphs, source.prepared_count);
    OWNED(slices, source.slice_count); OWNED(slice_indices, source.source.logical_count);
    OWNED(fitted_lines, source.source.line_count); OWNED(breaks_after, source.source.logical_count);
#undef OWNED
    const auto unchanged_source = source;
    const auto unchanged_raster = raster;
    const auto reject = [&](const progpu_native_hinted_glyph_resource_view& base,
        const progpu_native_hinted_source_glyph_resource_view& extension) {
        const auto rejected = import_hinted_paragraph_glyph_resource(base, extension);
        require(rejected.status == PROGPU_NATIVE_STATUS_INVALID_ARGUMENT && rejected.generation == nullptr &&
            std::memcmp(&source, &unchanged_source, sizeof(source)) == 0 &&
            std::memcmp(&raster, &unchanged_raster, sizeof(raster)) == 0);
    };
    for (unsigned invalid = 0U; invalid < 19U; ++invalid) {
        auto bad = source;
        switch (invalid) {
        case 0U: ++bad.abi_version; break;
        case 1U: --bad.struct_size; break;
        case 2U: bad.version = 1U; break;
        case 3U: bad.flags = 1U; break;
        case 4U: bad.reserved = 1U; break;
        case 5U: bad.source.options.flags |= 8U; break;
        case 6U: bad.source.options.em_policy = 4U; break;
        case 7U: bad.source.options.advance_policy = 3U; break;
        case 8U: bad.source.options.offset_policy = 2U; break;
        case 9U: bad.source.options.pixels_per_dip = std::numeric_limits<double>::quiet_NaN(); break;
        case 10U: bad.first_logical_glyph = UINT32_MAX; break;
        case 11U: bad.source.logical_count = UINT32_MAX; break;
        case 12U: bad.prepared_count = UINT32_MAX; break;
        case 13U: bad.raw_logical_glyphs = nullptr; break;
        case 14U: bad.source.logical_metrics = reinterpret_cast<const progpu_native_hinted_source_logical_metrics*>(1U); break;
        case 15U: bad.fitted_lines = nullptr; break;
        case 16U: bad.breaks_after = nullptr; break;
        case 17U: bad.raw_logical_glyphs = reinterpret_cast<const progpu_native_text_shaping_glyph*>(
            std::numeric_limits<std::uintptr_t>::max() & ~(std::uintptr_t{alignof(progpu_native_text_shaping_glyph)} - 1U)); break;
        default: bad.source.caret_count = UINT32_MAX; break;
        }
        reject(raster, bad);
    }
    auto bad = source;
    if (expected_soft_spaces) {
        require((source.source.options.flags & PROGPU_NATIVE_SOURCE_PRESERVE_SOFT_WRAP_SPACE_GEOMETRY) != 0U);
        bad.source.options.flags &= ~static_cast<std::uint32_t>(PROGPU_NATIVE_SOURCE_PRESERVE_SOFT_WRAP_SPACE_GEOMETRY);
        reject(raster, bad);
        bad = source;
    }
    std::vector<progpu_native_hinted_source_logical_metrics> logical(source.source.logical_metrics,
        source.source.logical_metrics + source.source.logical_count);
    bad.source.logical_metrics = logical.data(); logical.back().advance_x = std::nextafter(logical.back().advance_x, INFINITY);
    reject(raster, bad);
    std::vector<progpu_native_hinted_source_glyph_metrics> glyphs(source.source.glyph_metrics,
        source.source.glyph_metrics + source.source.glyph_count);
    bad = source; bad.source.glyph_metrics = glyphs.data(); glyphs.back().x = std::nextafter(glyphs.back().x, INFINITY);
    reject(raster, bad); // still the same float projection is not double identity
    std::vector<progpu_native_hinted_source_line_metrics> lines(source.source.line_metrics,
        source.source.line_metrics + source.source.line_count);
    bad = source; bad.source.line_metrics = lines.data(); lines.back().baseline_y = std::nextafter(lines.back().baseline_y, INFINITY);
    reject(raster, bad);
    std::vector<progpu_native_hinted_source_caret_stop> carets(source.source.carets, source.source.carets + source.source.caret_count);
    bad = source; bad.source.carets = carets.data(); carets.back().x = std::nextafter(carets.back().x, INFINITY);
    reject(raster, bad);
    std::vector<progpu_native_text_shaping_glyph> raw(source.raw_logical_glyphs, source.raw_logical_glyphs + source.source.logical_count);
    bad = source; bad.raw_logical_glyphs = raw.data(); raw.back().advance_x += 64;
    reject(raster, bad);
    std::vector<progpu_native_text_shaping_glyph> effective(source.effective_logical_glyphs,
        source.effective_logical_glyphs + source.source.logical_count);
    bad = source; bad.effective_logical_glyphs = effective.data(); effective.back().offset_x += 64;
    reject(raster, bad);
    std::vector<progpu_native_hinted_source_fitted_line> fitted(source.fitted_lines, source.fitted_lines + source.source.line_count);
    bad = source; bad.fitted_lines = fitted.data(); ++fitted.back().glyph_start;
    reject(raster, bad);
    std::vector<std::uint8_t> breaks(source.breaks_after, source.breaks_after + source.source.logical_count);
    bad = source; bad.breaks_after = breaks.data(); breaks.back() = 3U;
    reject(raster, bad);
    std::vector<progpu_native_positioned_text_glyph> raster_glyphs(raster.positioned_glyphs,
        raster.positioned_glyphs + raster.counts.positioned_glyph_count);
    auto bad_raster = raster; bad_raster.positioned_glyphs = raster_glyphs.data();
    raster_glyphs.back().x = std::nextafter(raster_glyphs.back().x, INFINITY);
    reject(bad_raster, source);
    if (expected_slices) {
        bad = source;
        bad.source.options.flags |= PROGPU_NATIVE_SOURCE_PRESERVE_WHOLE_RUN_PLACEMENT;
        reject(raster, bad);
        std::vector<progpu_native_hinted_source_fitting_slice> slices(source.slices, source.slices + source.slice_count);
        bad = source; bad.slices = slices.data(); ++slices.back().prepared_start;
        reject(raster, bad);
        std::vector<std::uint32_t> mapped(source.slice_indices, source.slice_indices + source.source.logical_count);
        bad = source; bad.slice_indices = mapped.data(); mapped.back() = UINT32_MAX;
        reject(raster, bad);
        std::vector<progpu_native_text_shaping_glyph> prepared(source.prepared_glyphs, source.prepared_glyphs + source.prepared_count);
        bad = source; bad.prepared_glyphs = prepared.data(); prepared[2U].flags |= 1U;
        reject(raster, bad);
    }
    if ((source.source.options.flags & PROGPU_NATIVE_SOURCE_PRESERVE_WHOLE_RUN_PLACEMENT) != 0U &&
        (source.source.line_count > 1U || source.first_logical_glyph != 0U)) {
        std::vector<progpu_native_text_shaping_glyph> prepared(source.prepared_glyphs, source.prepared_glyphs + source.prepared_count);
        const auto boundary = source.first_logical_glyph != 0U ? source.first_logical_glyph : source.fitted_lines[1U].glyph_start;
        const auto cluster = raster.logical_glyphs[boundary].cluster;
        const auto edge = std::lower_bound(prepared.begin(), prepared.end(), cluster,
            [](const auto& glyph, std::int32_t value) { return glyph.cluster < value; });
        require(edge != prepared.end() && edge->cluster == cluster);
        bad = source; bad.prepared_glyphs = prepared.data(); edge->flags |= 1U;
        reject(raster, bad); // Retained placement does not admit an unsafe GSUB cut.
    }
    {
        // Positioning may add dependency flags, but cannot erase an original
        // prepared unsafe edge. Keep every transported raw/effective/raster
        // flag coherent so rejection depends on that original prepared proof.
        std::vector<progpu_native_text_shaping_glyph> prepared(source.prepared_glyphs,
            source.prepared_glyphs + source.prepared_count);
        std::vector<progpu_native_text_shaping_glyph> raw_copy(source.raw_logical_glyphs,
            source.raw_logical_glyphs + source.source.logical_count);
        std::vector<progpu_native_text_shaping_glyph> effective_copy(source.effective_logical_glyphs,
            source.effective_logical_glyphs + source.source.logical_count);
        std::vector<progpu_native_text_shaping_glyph> logical_copy(raster.logical_glyphs,
            raster.logical_glyphs + raster.counts.logical_glyph_count);
        require(prepared.size() > 2U);
        prepared[2U].flags |= 1U;
        for (auto& glyph : raw_copy) glyph.flags &= ~1U;
        for (auto& glyph : effective_copy) glyph.flags &= ~1U;
        for (auto& glyph : logical_copy) glyph.flags &= ~1U;
        bad = source; bad.prepared_glyphs = prepared.data();
        bad.raw_logical_glyphs = raw_copy.data(); bad.effective_logical_glyphs = effective_copy.data();
        bad_raster = raster; bad_raster.logical_glyphs = logical_copy.data();
        reject(bad_raster, bad);
    }
    // Byte-identical warm imports remain independent owners; no source pointer,
    // hinted driver, paragraph or context lease crosses this flat boundary.
    const auto repeated = import_hinted_paragraph_glyph_resource(raster, source);
    require(repeated.status == PROGPU_NATIVE_STATUS_SUCCESS && repeated.generation != imported.generation &&
        repeated.generation->imported_source_view()->source.glyph_metrics != owned->source.glyph_metrics);
    return imported.generation;
}
#endif

void controls(std::uint32_t interpreter, bool rtl, std::uint32_t advance_policy, bool preserve_placement,
    bool marks = false, bool six_glyph_width = false) {
    const auto bytes = progpu::native::tests::make_hinted_fixture_font(true, true, marks);
    context_owner context;
    require(progpu_native_text_context_create(PROGPU_NATIVE_ABI_VERSION,
        reinterpret_cast<const std::uint8_t*>(bytes.data()), bytes.size(), 0U, nullptr, 0U, &context.value) == PROGPU_NATIVE_STATUS_SUCCESS);
    const std::uint32_t count = marks ? 8U : six_glyph_width ? 6U : 4U;
    std::vector<progpu_native_text_scalar> input(count);
    for (std::uint32_t i = 0U; i < input.size(); ++i) input[i] = {rtl ? 0x05D0U : std::uint32_t{'A'}, 9U + i * 2U, 2U, 0U, 0U, 0U};
    if (marks) { input[1U].code_point = 0x05B0U; input[7U].code_point = 0x05B0U; }
    const progpu_native_text_feature kern{0x6B65726EU, 1U, 0U, UINT32_MAX};
    progpu_native_text_shape_request shape{};
    shape.abi_version = PROGPU_NATIVE_ABI_VERSION; shape.struct_size = sizeof(shape);
    shape.input = input.data(); shape.input_count = count; shape.features = &kern; shape.feature_count = 1U;
    shape.direction = rtl ? PROGPU_NATIVE_TEXT_DIRECTION_RIGHT_TO_LEFT : PROGPU_NATIVE_TEXT_DIRECTION_LEFT_TO_RIGHT;
    progpu_native_text_layout_options layout{}; layout.struct_size = sizeof(layout); layout.scale = 1.0F;
    layout.maximum_width = 200.0F; layout.direction = shape.direction;
    const progpu_native_text_style_run style{0U, count, 0U, 0.01325F, 0U, 0U, 0U, 0U, 0U, 0U, 0U};
    const progpu_native_text_style_metrics metrics{9.25F, 2.5F};
    const progpu_native_hinted_paragraph_device_style device{0U, style.scale, 1.0F / 1.5F,
        1280U, 1280U, interpreter, 0U, 0U, 0U, 0U, 0U};
    const progpu_native_hinted_source_style source_style{std::nextafter(13.25, 14.0), std::nextafter(9.25, 10.0), 2.5};
    const progpu_native_hinted_source_options options{PROGPU_NATIVE_ABI_VERSION, sizeof(options), 1U,
        PROGPU_NATIVE_SOURCE_MEASURE_INTRINSIC_WIDTHS |
            (preserve_placement ? static_cast<std::uint32_t>(PROGPU_NATIVE_SOURCE_PRESERVE_WHOLE_RUN_PLACEMENT) : 0U),
        source_style.em_size, 1.5, 200.0, 0.0, 0.0,
        PROGPU_NATIVE_SOURCE_EM_FLOAT_CAPTURE_NEAREST_HALF_UP, advance_policy,
        advance_policy == PROGPU_NATIVE_SOURCE_ADVANCE_IDEAL_UNITS ? 1U : 0U, 1U};
    paragraph_owner paragraph;
    progpu_native_text_paragraph_result diagnostic{}; diagnostic.struct_size = sizeof(diagnostic);
    const auto status = progpu_native_text_context_layout_hinted_source_paragraph(context.value, &shape, &layout,
        &style, 1U, &metrics, &device, 1U, nullptr, 0U, &options, &source_style, 1U, &paragraph.value, &diagnostic);
#if defined(PROGPU_NATIVE_FONT_HINTING)
    require(status == PROGPU_NATIVE_STATUS_SUCCESS);
    const auto full = verify_import(paragraph.value, false, false, rtl, count);
    if (six_glyph_width) require(full->imported_source_view()->source.line_count == 1U &&
        full->imported_source_view()->source.line_metrics[0U].width == 50.0 / 1.5);
    progpu_native_hinted_source_paragraph_view view{};
    require(progpu_native_hinted_source_paragraph_borrow(paragraph.value, &view) == PROGPU_NATIVE_STATUS_SUCCESS && view.logical_count == count);
    double narrow = view.logical_metrics[0U].advance_x + view.logical_metrics[3U].advance_x;
    if (marks) {
        double first = 0.0, last = 0.0;
        for (std::uint32_t i = 0U; i < count / 2U; ++i) {
            first += view.logical_metrics[i].advance_x;
            last += view.logical_metrics[count / 2U + i].advance_x;
        }
        narrow = std::max(first, last);
        const auto retained = select_hinted_paragraph_generation(paragraph.value);
        const auto& run = *retained->runs[0U].generation;
        require(run.positioning == nullptr && run.boundary_glyphs.size() == count);
        const auto saved = run.boundary_glyphs;
        require(progpu_native_hinted_source_paragraph_get_intrinsic_widths(paragraph.value,
            reinterpret_cast<progpu_native_hinted_source_intrinsic_widths*>(const_cast<shaping_glyph*>(run.boundary_glyphs.data()))) ==
                PROGPU_NATIVE_STATUS_INVALID_ARGUMENT &&
            std::memcmp(saved.data(), run.boundary_glyphs.data(), saved.size() * sizeof(shaping_glyph)) == 0);
    }
    paragraph_owner wrapped;
    require(progpu_native_hinted_source_paragraph_reflow(paragraph.value, 9, narrow, &wrapped.value) == PROGPU_NATIVE_STATUS_SUCCESS);
    const auto fitted = verify_import(wrapped.value, !preserve_placement, false, rtl, count);
    // Keep the original requested width. In the mark/ideal control its ordered
    // double sum is just below 6800/300; the original source floors the width
    // constraint to 6799 units, so the 6800-unit tail must take another line.
    const auto expected_lines = six_glyph_width ? 4U :
        marks && advance_policy == PROGPU_NATIVE_SOURCE_ADVANCE_IDEAL_UNITS ? 3U : 2U;
    require(fitted->imported_source_view()->source.line_count == expected_lines);
    if (preserve_placement) {
        const auto& kept = *fitted->imported_source_view();
        require(kept.source.options.flags == options.flags && kept.slice_count == 0U);
        for (std::uint32_t i = 0U; i < count; ++i) {
            require(kept.slice_indices[i] == UINT32_MAX &&
                std::memcmp(&kept.raw_logical_glyphs[i], &kept.effective_logical_glyphs[i], sizeof(kept.raw_logical_glyphs[i])) == 0 &&
                std::memcmp(&kept.source.logical_metrics[i], &view.logical_metrics[i], sizeof(view.logical_metrics[i])) == 0);
        }
    }
    paragraph_owner tail;
    require(progpu_native_hinted_source_paragraph_reflow(wrapped.value, static_cast<std::int32_t>(input[count / 2U].input_index),
        narrow, &tail.value) == PROGPU_NATIVE_STATUS_SUCCESS);
    const auto suffix = verify_import(tail.value, !preserve_placement, true, rtl, count);
    const auto captured = *fitted->imported_source_view();
    const auto saved_metric = captured.source.glyph_metrics[0U];
    progpu_native_hinted_paragraph_destroy(tail.value); tail.value = nullptr;
    progpu_native_hinted_paragraph_destroy(wrapped.value); wrapped.value = nullptr;
    progpu_native_hinted_paragraph_destroy(paragraph.value); paragraph.value = nullptr;
    progpu_native_text_context_destroy(context.value); context.value = nullptr;
    require(full->paragraph() == nullptr && fitted->paragraph() == nullptr && suffix->paragraph() == nullptr &&
        fitted->imported_source_view()->source.styles[0U].em_size == source_style.em_size &&
        fitted->imported_source_view()->source.styles[0U].ascent == source_style.ascent &&
        fitted->imported_source_view()->source.options.flags == options.flags &&
        suffix->imported_source_view()->source.options.flags == options.flags &&
        std::memcmp(&fitted->imported_source_view()->source.glyph_metrics[0U], &saved_metric, sizeof(saved_metric)) == 0 &&
        fitted->binding_view().glyphs.size() == count && suffix->imported_source_view()->first_logical_glyph == count / 2U &&
        suffix->binding_view().glyphs.size() == count / 2U);
#else
    require(status == PROGPU_NATIVE_STATUS_UNSUPPORTED && paragraph.value == nullptr);
#endif
}

void soft_wrap_controls(std::uint32_t interpreter, bool hebrew) {
    const auto bytes = progpu::native::tests::make_hinted_fixture_font(true, true);
    context_owner context;
    require(progpu_native_text_context_create(PROGPU_NATIVE_ABI_VERSION,
        reinterpret_cast<const std::uint8_t*>(bytes.data()), bytes.size(), 0U, nullptr, 0U, &context.value) == PROGPU_NATIVE_STATUS_SUCCESS);
    const auto letter = hebrew ? 0x05D0U : std::uint32_t{'A'};
    const std::array<std::uint32_t, 9U> code_points{letter, letter, ' ', letter, letter, ' ', ' ', ' ', letter};
    std::array<progpu_native_text_scalar, 9U> input{};
    for (std::uint32_t i = 0U; i < input.size(); ++i) input[i] = {code_points[i], i, 1U, 0U, 0U, 0U};
    progpu_native_text_shape_request shape{};
    shape.abi_version = PROGPU_NATIVE_ABI_VERSION; shape.struct_size = sizeof(shape);
    shape.input = input.data(); shape.input_count = static_cast<std::uint32_t>(input.size());
    shape.direction = hebrew ? PROGPU_NATIVE_TEXT_DIRECTION_LEFT_TO_RIGHT : PROGPU_NATIVE_TEXT_DIRECTION_RIGHT_TO_LEFT;
    progpu_native_text_layout_options layout{}; layout.struct_size = sizeof(layout); layout.scale = 1.0F;
    layout.maximum_width = 200.0F; layout.direction = shape.direction;
    const progpu_native_text_style_run style{0U, 9U, 0U, 0.01325F, 0U, 0U, 0U, 0U, 0U, 0U, 0U};
    const progpu_native_text_style_metrics metrics{9.25F, 2.5F};
    const progpu_native_hinted_paragraph_device_style device{0U, style.scale, 1.0F / 1.5F,
        1280U, 1280U, interpreter, 0U, 0U, 0U, 0U, 0U};
    const progpu_native_hinted_source_style source_style{13.25, 9.25, 2.5};
    const progpu_native_hinted_source_options options{PROGPU_NATIVE_ABI_VERSION, sizeof(options), 1U,
        PROGPU_NATIVE_SOURCE_PRESERVE_WHOLE_RUN_PLACEMENT | PROGPU_NATIVE_SOURCE_PRESERVE_SOFT_WRAP_SPACE_GEOMETRY,
        13.25, 1.5, 200.0, 0.0, 0.0, PROGPU_NATIVE_SOURCE_EM_FLOAT_CAPTURE_NEAREST_HALF_UP,
        PROGPU_NATIVE_SOURCE_ADVANCE_UNCHANGED, PROGPU_NATIVE_SOURCE_OFFSET_UNCHANGED, 1U};
    paragraph_owner paragraph;
    progpu_native_text_paragraph_result diagnostic{}; diagnostic.struct_size = sizeof(diagnostic);
    const auto status = progpu_native_text_context_layout_hinted_source_paragraph(context.value, &shape, &layout,
        &style, 1U, &metrics, &device, 1U, nullptr, 0U, &options, &source_style, 1U, &paragraph.value, &diagnostic);
#if defined(PROGPU_NATIVE_FONT_HINTING)
    require(status == PROGPU_NATIVE_STATUS_SUCCESS);
    const auto wide = verify_import(paragraph.value, false, false, hebrew, 9U);
    const auto& source = wide->imported_source_view()->source;
    double width = 0.0, content = 0.0;
    for (std::uint32_t i = 0U; i < 7U; ++i) {
        width += source.logical_metrics[i].advance_x;
        if (i < 5U) content += source.logical_metrics[i].advance_x;
    }
    paragraph_owner wrapped;
    require(progpu_native_hinted_source_paragraph_reflow(paragraph.value, 0, width, &wrapped.value) == PROGPU_NATIVE_STATUS_SUCCESS);
    const auto retained = verify_import(wrapped.value, false, false, hebrew, 9U, true);
    const auto* owned = retained->imported_source_view();
    require(owned->source.line_count == 2U && owned->source.line_metrics[0U].glyph_count == 7U &&
        owned->source.line_metrics[0U].width == content && owned->fitted_lines[0U].width == width &&
        owned->source.options.flags == options.flags);
    progpu_native_hinted_paragraph_destroy(wrapped.value); wrapped.value = nullptr;
    progpu_native_hinted_paragraph_destroy(paragraph.value); paragraph.value = nullptr;
    progpu_native_text_context_destroy(context.value); context.value = nullptr;
    require(owned->source.line_metrics[0U].width == content && owned->source.options.flags == options.flags &&
        retained->binding_view().glyphs.size() == input.size());
#else
    require(status == PROGPU_NATIVE_STATUS_UNSUPPORTED && paragraph.value == nullptr);
#endif
}
} // namespace

int main() {
    try {
        for (const auto interpreter : {35U, 40U}) for (const bool rtl : {false, true})
            for (const auto policy : {0U, 1U, 2U}) for (const bool preserve : {false, true}) controls(interpreter, rtl, policy, preserve);
        for (const auto interpreter : {35U, 40U}) for (const auto policy : {0U, 1U, 2U})
            controls(interpreter, true, policy, true, true);
        for (const auto interpreter : {35U, 40U})
            controls(interpreter, false, PROGPU_NATIVE_SOURCE_ADVANCE_IDEAL_UNITS, true, false, true);
        for (const auto interpreter : {35U, 40U}) for (const bool hebrew : {false, true}) soft_wrap_controls(interpreter, hebrew);
        return 0;
    } catch (const std::exception& error) { std::cerr << error.what() << '\n'; return 1; }
}
