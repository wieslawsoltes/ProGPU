#include "progpu_native_hinted_source_fitting.hpp"
#include "progpu_native_owned_allocation_internal.hpp"

#include <algorithm>
#include <cmath>
#include <limits>
#include <new>

namespace progpu::native::text {
namespace {
constexpr std::size_t maximum_candidate_glyph_visits = 1U << 24U;
bool unsafe(const shaping_glyph& glyph) noexcept {
    return (static_cast<std::uint32_t>(glyph.flags) & static_cast<std::uint32_t>(shaping_glyph_flags::unsafe_to_break)) != 0U;
}
bool same(const shaping_glyph& a, const shaping_glyph& b) noexcept {
    return a.glyph_id == b.glyph_id && a.code_point == b.code_point && a.cluster == b.cluster && a.flags == b.flags &&
        a.advance_x == b.advance_x && a.advance_y == b.advance_y && a.offset_x == b.offset_x && a.offset_y == b.offset_y;
}
bool same(text_source_glyph_metrics a, text_source_glyph_metrics b) noexcept {
    return a.advance_x == b.advance_x && a.advance_y == b.advance_y && a.offset_x == b.offset_x && a.offset_y == b.offset_y;
}
bool wire_fitting(const shaping_glyph& raw, hinted_source_advance_policy policy, shaping_glyph& output) noexcept {
    if (raw.advance_y == INT32_MIN || raw.offset_y == INT32_MIN || !project_hinted_source_advance(raw, policy, output)) return false;
    output.advance_y = -output.advance_y; output.offset_y = -output.offset_y;
    return true;
}

std::size_t prepared_edge(const hinted_shaped_run& run, std::int32_t cluster) noexcept {
    const auto& glyphs = run.positioning->prepared_glyphs;
    return static_cast<std::size_t>(std::lower_bound(glyphs.begin(), glyphs.end(), cluster,
        [](const shaping_glyph& glyph, std::int32_t value) { return glyph.cluster < value; }) - glyphs.begin());
}

bool safe_boundary(const hinted_paragraph_generation& paragraph, std::size_t index) noexcept {
    if (index == 0U || index == paragraph.logical_glyphs.size()) return true;
    const auto& glyph = paragraph.logical_glyphs[index];
    if (glyph.cluster == paragraph.logical_glyphs[index - 1U].cluster) return false;
    const auto owner = paragraph.logical_owners[index];
    if (owner.run_index != paragraph.logical_owners[index - 1U].run_index || !unsafe(glyph)) return true;
    const auto& run = *paragraph.runs[owner.run_index].generation;
    if (run.positioning == nullptr) return false;
    const auto edge = prepared_edge(run, glyph.cluster);
    return edge < run.positioning->prepared_glyphs.size() && run.positioning->prepared_glyphs[edge].cluster == glyph.cluster &&
        (edge == 0U || (run.positioning->prepared_glyphs[edge - 1U].cluster != glyph.cluster &&
            !unsafe(run.positioning->prepared_glyphs[edge])));
}

struct candidate_line final {
    std::uint32_t start = 0U, end = 0U;
    double width = 0.0, visible_width = 0.0;
    bool nonnegative_advances = true;
    std::vector<shaping_glyph> glyphs{};
    std::vector<text_source_glyph_metrics> metrics{};
    std::vector<hinted_source_fitted_slice> slices{};
};

bool original_occurrence_map(const hinted_paragraph_generation& paragraph,
    std::vector<std::vector<std::uint32_t>>& raw_to_logical) {
    const auto count = paragraph.logical_glyphs.size();
    if (!paragraph.has_source_geometry || count > UINT32_MAX || paragraph.logical_owners.size() != count ||
        paragraph.breaks_after.size() != count || paragraph.source_styles.size() != paragraph.styles.size()) return false;
    raw_to_logical.resize(paragraph.runs.size());
    std::size_t run_end = 0U;
    for (std::size_t run = 0U; run < paragraph.runs.size(); ++run) {
        const auto& value = paragraph.runs[run];
        if (value.generation == nullptr || value.style_index >= paragraph.source_styles.size() ||
            value.logical_start != run_end || value.logical_count > count - run_end ||
            value.generation->glyphs.size() != value.logical_count) return false;
        run_end += value.logical_count;
        raw_to_logical[run].resize(value.generation->glyphs.size(), UINT32_MAX);
    }
    if (run_end != count) return false;
    for (std::size_t i = 0U; i < count; ++i) {
        const auto owner = paragraph.logical_owners[i];
        if (owner.run_index >= raw_to_logical.size() || owner.run_glyph_index >= raw_to_logical[owner.run_index].size() ||
            raw_to_logical[owner.run_index][owner.run_glyph_index] != UINT32_MAX) return false;
        const auto& run = paragraph.runs[owner.run_index];
        if (i < run.logical_start || i - run.logical_start >= run.logical_count) return false;
        raw_to_logical[owner.run_index][owner.run_glyph_index] = static_cast<std::uint32_t>(i);
    }
    return true;
}

progpu_native_status compose_candidate(const hinted_paragraph_generation& paragraph,
    std::span<const std::vector<std::uint32_t>> raw_to_logical, std::uint32_t start, std::uint32_t end,
    std::size_t& visits, candidate_line& output) {
    const auto length = static_cast<std::size_t>(end - start);
    if (end <= start || length > maximum_candidate_glyph_visits - visits) return PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
    visits += length;
    candidate_line candidate{}; candidate.start = start; candidate.end = end;
    candidate.glyphs.assign(paragraph.logical_glyphs.begin() + start, paragraph.logical_glyphs.begin() + end);
    candidate.metrics.resize(length);
    for (std::uint32_t first = start; first < end;) {
        const auto run_index = paragraph.logical_owners[first].run_index;
        const auto& run = paragraph.runs[run_index];
        const auto last = std::min<std::uint32_t>(end, run.logical_start + run.logical_count);
        if (last <= first) return PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
        const bool unsafe_left = first > run.logical_start && unsafe(paragraph.logical_glyphs[first]);
        const bool unsafe_right = last < run.logical_start + run.logical_count && unsafe(paragraph.logical_glyphs[last]);
        if (unsafe_left || unsafe_right) {
            if (run.generation->positioning == nullptr) return PROGPU_NATIVE_STATUS_UNSUPPORTED;
            const auto prepared_first = prepared_edge(*run.generation, paragraph.logical_glyphs[first].cluster);
            const auto prepared_last = last == run.logical_start + run.logical_count ? run.generation->positioning->prepared_glyphs.size() :
                prepared_edge(*run.generation, paragraph.logical_glyphs[last].cluster);
            if (prepared_first >= prepared_last || prepared_last > UINT32_MAX) return PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
            std::shared_ptr<const hinted_positioned_slice> slice;
            font_error error = font_error::none;
            if (!try_recompose_hinted_positioning(run.generation, static_cast<std::uint32_t>(prepared_first),
                static_cast<std::uint32_t>(prepared_last - prepared_first), slice, &error))
                return error == font_error::insufficient_buffer ? PROGPU_NATIVE_STATUS_OUT_OF_MEMORY : PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
            if (slice->glyphs.size() != last - first) return PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
            for (std::size_t i = 0U; i < slice->glyphs.size(); ++i) {
                const auto raw = slice->original_glyph_indices[i];
                if (raw >= raw_to_logical[run_index].size()) return PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
                const auto logical = raw_to_logical[run_index][raw];
                if (logical < first || logical >= last || !wire_fitting(slice->glyphs[i],
                    paragraph.source_styles[run.style_index].advance_policy, candidate.glyphs[logical - start]))
                    return PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
            }
            candidate.slices.push_back({run_index, std::move(slice)});
        }
        first = last;
    }
    for (std::uint32_t i = start; i < end; ++i) {
        const auto& glyph = candidate.glyphs[i - start];
        const auto& original = paragraph.logical_glyphs[i];
        const auto style = paragraph.runs[paragraph.logical_owners[i].run_index].style_index;
        if (glyph.glyph_id != original.glyph_id || glyph.cluster != original.cluster || glyph.code_point != original.code_point ||
            glyph.advance_y != 0 || !project_hinted_source_geometry(glyph, paragraph.source_styles[style],
                candidate.metrics[i - start])) return PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
        candidate.width += candidate.metrics[i - start].advance_x;
        candidate.nonnegative_advances &= candidate.metrics[i - start].advance_x >= 0.0;
        if (!is_text_layout_trailing_space(glyph.code_point)) candidate.visible_width = candidate.width;
        if (!std::isfinite(candidate.width) || std::abs(candidate.width) > std::numeric_limits<float>::max())
            return PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
    }
    output = std::move(candidate);
    return PROGPU_NATIVE_STATUS_SUCCESS;
}
} // namespace

bool hinted_source_fitting::allocation_aliases(const void* output, std::size_t bytes) const noexcept {
    const owned_output_range range{output, bytes};
    if (range.overlaps(this, sizeof(*this)) || range.overlaps(fitting_glyphs) || range.overlaps(metrics) ||
        range.overlaps(lines) || range.overlaps(slices) || range.overlaps(slice_indices) || range.overlaps(slice_glyph_indices)) return true;
    for (const auto& slice : slices)
        if (slice.placement != nullptr && (range.overlaps(slice.placement.get(), sizeof(*slice.placement)) ||
            range.overlaps(slice.placement->glyphs) || range.overlaps(slice.placement->original_glyph_indices))) return true;
    return false;
}

hinted_source_fitting_result fit_hinted_source_paragraph(const hinted_paragraph_generation& paragraph,
    std::uint32_t first_logical_glyph, double maximum_width) noexcept {
    hinted_source_fitting_result result{};
    const auto count = paragraph.logical_glyphs.size();
    if (!paragraph.has_source_geometry || count > UINT32_MAX || first_logical_glyph > count ||
        !std::isfinite(maximum_width) || maximum_width < 0.0 || maximum_width > std::numeric_limits<float>::max() ||
        paragraph.logical_owners.size() != count || paragraph.breaks_after.size() != count ||
        paragraph.source_styles.size() != paragraph.styles.size()) return result;
    try {
        auto fitted = std::make_shared<hinted_source_fitting>();
        fitted->first_logical_glyph = first_logical_glyph;
        fitted->fitting_glyphs = paragraph.logical_glyphs;
        fitted->metrics.resize(count);
        fitted->slice_indices.resize(count, UINT32_MAX);
        fitted->slice_glyph_indices.resize(count, UINT32_MAX);
        std::vector<std::vector<std::uint32_t>> raw_to_logical;
        if (!original_occurrence_map(paragraph, raw_to_logical)) return result;
        for (std::size_t i = 0U; i < count; ++i) {
            const auto owner = paragraph.logical_owners[i];
            const auto style = paragraph.runs[owner.run_index].style_index;
            if (!project_hinted_source_geometry(paragraph.logical_glyphs[i], paragraph.source_styles[style], fitted->metrics[i])) return result;
        }
        if (!safe_boundary(paragraph, first_logical_glyph)) { result.status = PROGPU_NATIVE_STATUS_UNSUPPORTED; return result; }
        std::size_t visits = 0U;
        for (std::uint32_t start = first_logical_glyph; start < count;) {
            std::uint32_t hard_end = start + 1U;
            while (hard_end < count && paragraph.breaks_after[hard_end - 1U] != text_line_break_kind::mandatory) ++hard_end;
            candidate_line selected{}, last_fit{}, last_break{};
            bool chosen = false, soft_break = false;
            // Avoid quadratic candidate work for the common complete hard row.
            // A cut at the start still goes through real placement recomposition.
            candidate_line complete;
            auto status = compose_candidate(paragraph, raw_to_logical, start, hard_end, visits, complete);
            if (status != PROGPU_NATIVE_STATUS_SUCCESS) { result.status = status; return result; }
            if (maximum_width <= 0.0 || (complete.nonnegative_advances && (complete.width <= maximum_width ||
                (is_text_layout_trailing_space(paragraph.logical_glyphs[hard_end - 1U].code_point) && complete.visible_width <= maximum_width)))) {
                selected = std::move(complete); chosen = true;
            }
            for (std::uint32_t end = start + 1U; !chosen && end <= hard_end; ++end) {
                if (!safe_boundary(paragraph, end)) continue;
                candidate_line candidate;
                status = compose_candidate(paragraph, raw_to_logical, start, end, visits, candidate);
                if (status != PROGPU_NATIVE_STATUS_SUCCESS) { result.status = status; return result; }
                const bool legal = paragraph.breaks_after[end - 1U] != text_line_break_kind::prohibited;
                const bool mandatory = paragraph.breaks_after[end - 1U] == text_line_break_kind::mandatory;
                const bool fits = maximum_width <= 0.0 || candidate.width <= maximum_width ||
                    (legal && is_text_layout_trailing_space(paragraph.logical_glyphs[end - 1U].code_point) && candidate.visible_width <= maximum_width);
                if (!fits) {
                    if (last_break.end > start) { selected = std::move(last_break); chosen = true; soft_break = true; }
                    else if (paragraph.source_allow_emergency_break) {
                        if (last_fit.end > start) selected = std::move(last_fit);
                        else {
                            // Multiple source clusters held indivisible by
                            // GSUB/script state require a different recomposer,
                            // not a silently overflowing emergency line.
                            if (paragraph.logical_glyphs[start].cluster != paragraph.logical_glyphs[end - 1U].cluster) {
                                result.status = PROGPU_NATIVE_STATUS_UNSUPPORTED; return result;
                            }
                            selected = std::move(candidate);
                        }
                        chosen = true; soft_break = true;
                    } else if (legal || end == hard_end) { selected = std::move(candidate); chosen = true; soft_break = true; }
                }
                if (!chosen && (mandatory || end == hard_end)) { selected = std::move(candidate); chosen = true; }
                if (!chosen && fits) {
                    if (legal) last_break = candidate;
                    last_fit = std::move(candidate);
                }
            }
            if (!chosen || selected.end <= start || selected.width < 0.0) return result;
            const bool final = paragraph.layout.maximum_lines != 0U && fitted->lines.size() + 1U >= paragraph.layout.maximum_lines;
            fitted->lines.push_back({start - first_logical_glyph, selected.end - start, selected.width, final && soft_break});
            std::copy(selected.glyphs.begin(), selected.glyphs.end(), fitted->fitting_glyphs.begin() + start);
            std::copy(selected.metrics.begin(), selected.metrics.end(), fitted->metrics.begin() + start);
            for (auto& slice : selected.slices) {
                const auto slice_index = static_cast<std::uint32_t>(fitted->slices.size());
                for (std::uint32_t i = 0U; i < slice.placement->original_glyph_indices.size(); ++i) {
                    const auto logical = raw_to_logical[slice.run_index][slice.placement->original_glyph_indices[i]];
                    if (fitted->slice_indices[logical] != UINT32_MAX) return result;
                    fitted->slice_indices[logical] = slice_index; fitted->slice_glyph_indices[logical] = i;
                }
                fitted->slices.push_back(std::move(slice));
            }
            start = selected.end;
            if (final) break;
        }
        result.generation = std::move(fitted); result.status = PROGPU_NATIVE_STATUS_SUCCESS;
    } catch (const std::bad_alloc&) { result.status = PROGPU_NATIVE_STATUS_OUT_OF_MEMORY; }
    catch (...) { result.status = PROGPU_NATIVE_STATUS_INTERNAL_ERROR; }
    return result;
}

progpu_native_status measure_hinted_source_intrinsic_widths(const hinted_paragraph_generation& paragraph,
    hinted_source_intrinsic_widths& result) noexcept {
    try {
        std::vector<std::vector<std::uint32_t>> raw_to_logical;
        if (!original_occurrence_map(paragraph, raw_to_logical)) return PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
        const auto count = paragraph.logical_glyphs.size();
        std::vector<std::uint8_t> whitespace(count);
        std::size_t scalar = 0U;
        for (std::size_t first = 0U; first < count;) {
            const auto cluster = paragraph.logical_glyphs[first].cluster;
            if (cluster < 0 || scalar >= paragraph.source_input.size() ||
                paragraph.source_input[scalar].input_index != static_cast<std::uint32_t>(cluster)) return PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
            auto end = first + 1U;
            while (end < count && paragraph.logical_glyphs[end].cluster == cluster) ++end;
            if (end < count && paragraph.logical_glyphs[end].cluster <= cluster) return PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
            const auto next = end < count ? static_cast<std::uint64_t>(paragraph.logical_glyphs[end].cluster) :
                static_cast<std::uint64_t>(paragraph.source_input.back().input_index) + paragraph.source_input.back().input_length;
            bool spaces = true;
            while (scalar < paragraph.source_input.size() && paragraph.source_input[scalar].input_index < next)
                spaces &= is_text_layout_trailing_space(paragraph.source_input[scalar++].code_point);
            std::fill(whitespace.begin() + first, whitespace.begin() + end, static_cast<std::uint8_t>(spaces));
            first = end;
        }
        const auto visible_width = [&](const candidate_line& candidate, double& width) noexcept {
            double advance = 0.0; width = 0.0;
            for (std::size_t i = 0U; i < candidate.metrics.size(); ++i) {
                advance += candidate.metrics[i].advance_x;
                if (!std::isfinite(advance) || advance < 0.0) return false;
                if (whitespace[candidate.start + i] == 0U) width = advance;
            }
            return true;
        };
        hinted_source_intrinsic_widths measured{};
        std::size_t visits = 0U;
        std::uint32_t word_start = 0U, paragraph_start = 0U;
        for (std::uint32_t end = 1U; end <= count; ++end) {
            const bool final = end == count;
            const auto break_kind = paragraph.breaks_after[end - 1U];
            if (!final && break_kind == text_line_break_kind::prohibited) continue;
            if (!safe_boundary(paragraph, end)) return PROGPU_NATIVE_STATUS_UNSUPPORTED;
            candidate_line word;
            auto status = compose_candidate(paragraph, raw_to_logical, word_start, end, visits, word);
            if (status != PROGPU_NATIVE_STATUS_SUCCESS) return status;
            double visible = 0.0;
            if (!visible_width(word, visible)) return PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
            measured.minimum = std::max(measured.minimum, visible);
            word_start = end;
            if (final || break_kind == text_line_break_kind::mandatory) {
                candidate_line row;
                status = compose_candidate(paragraph, raw_to_logical, paragraph_start, end, visits, row);
                if (status != PROGPU_NATIVE_STATUS_SUCCESS) return status;
                if (!visible_width(row, visible)) return PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
                measured.maximum = std::max(measured.maximum, visible);
                paragraph_start = end;
            }
        }
        result = measured;
        return PROGPU_NATIVE_STATUS_SUCCESS;
    } catch (const std::bad_alloc&) { return PROGPU_NATIVE_STATUS_OUT_OF_MEMORY; }
    catch (...) { return PROGPU_NATIVE_STATUS_INTERNAL_ERROR; }
}

bool validate_hinted_source_fitting(const hinted_paragraph_generation& paragraph, const hinted_source_fitting& fitting) noexcept {
    const auto count = paragraph.logical_glyphs.size();
    if (!paragraph.has_source_geometry || fitting.first_logical_glyph > count || fitting.fitting_glyphs.size() != count ||
        fitting.metrics.size() != count || paragraph.source_logical_metrics.size() != count ||
        fitting.slice_indices.size() != count || fitting.slice_glyph_indices.size() != count || paragraph.logical_owners.size() != count ||
        paragraph.breaks_after.size() != count || fitting.slices.size() > UINT32_MAX ||
        (paragraph.layout.maximum_lines != 0U && fitting.lines.size() > paragraph.layout.maximum_lines)) return false;
    std::size_t referenced_slice_glyphs = 0U;
    for (std::size_t i = 0U; i < count; ++i) {
        const auto owner = paragraph.logical_owners[i];
        if (owner.run_index >= paragraph.runs.size()) return false;
        const auto& run = paragraph.runs[owner.run_index];
        if (run.generation == nullptr || owner.run_glyph_index >= run.generation->glyphs.size() || run.style_index >= paragraph.source_styles.size()) return false;
        const shaping_glyph* raw = &run.generation->glyphs[owner.run_glyph_index];
        const auto slice_index = fitting.slice_indices[i], glyph_index = fitting.slice_glyph_indices[i];
        if (slice_index != UINT32_MAX) {
            ++referenced_slice_glyphs;
            if (slice_index >= fitting.slices.size()) return false;
            const auto& slice = fitting.slices[slice_index];
            if (slice.run_index != owner.run_index || slice.placement == nullptr || slice.placement->original != run.generation ||
                glyph_index >= slice.placement->glyphs.size() || glyph_index >= slice.placement->original_glyph_indices.size() ||
                slice.placement->original_glyph_indices[glyph_index] != owner.run_glyph_index) return false;
            raw = &slice.placement->glyphs[glyph_index];
        } else if (glyph_index != UINT32_MAX) return false;
        shaping_glyph expected{};
        text_source_glyph_metrics geometry{};
        if (!wire_fitting(*raw, paragraph.source_styles[run.style_index].advance_policy, expected) ||
            !same(expected, fitting.fitting_glyphs[i]) || expected.glyph_id != paragraph.logical_glyphs[i].glyph_id ||
            expected.code_point != paragraph.logical_glyphs[i].code_point || expected.cluster != paragraph.logical_glyphs[i].cluster ||
            !project_hinted_source_geometry(expected, paragraph.source_styles[run.style_index], geometry) ||
            !same(geometry, fitting.metrics[i]) || !same(geometry, paragraph.source_logical_metrics[i])) return false;
    }
    std::size_t declared_slice_glyphs = 0U;
    for (const auto& slice : fitting.slices) {
        if (slice.placement == nullptr || slice.placement->glyphs.empty() ||
            slice.placement->glyphs.size() != slice.placement->original_glyph_indices.size() ||
            slice.placement->glyphs.size() > count - declared_slice_glyphs) return false;
        declared_slice_glyphs += slice.placement->glyphs.size();
    }
    if (declared_slice_glyphs != referenced_slice_glyphs || !safe_boundary(paragraph, fitting.first_logical_glyph)) return false;
    std::size_t end = fitting.first_logical_glyph;
    for (const auto& line : fitting.lines) {
        if (line.glyph_start != end - fitting.first_logical_glyph || line.glyph_count == 0U || line.glyph_count > count - end ||
            !std::isfinite(line.width) || line.width < 0.0 || !safe_boundary(paragraph, end + line.glyph_count)) return false;
        double width = 0.0;
        for (std::size_t i = end; i < end + line.glyph_count; ++i) {
            width += fitting.metrics[i].advance_x;
            if (i + 1U < end + line.glyph_count && paragraph.breaks_after[i] == text_line_break_kind::mandatory) return false;
        }
        if (width != line.width) return false;
        end += line.glyph_count;
    }
    if (end != count && (paragraph.layout.maximum_lines == 0U || fitting.lines.size() != paragraph.layout.maximum_lines)) return false;
    for (std::size_t i = 0U; i < count; ++i)
        if ((i < fitting.first_logical_glyph || i >= end) && fitting.slice_indices[i] != UINT32_MAX) return false;
    return true;
}
} // namespace progpu::native::text
