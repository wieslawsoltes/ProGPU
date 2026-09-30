#include "../src/Text/Interop/progpu_native_hinted_paragraph_glyph_frame.hpp"
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

// Real original paragraph/context/font controls, independent raw descriptor
// and positioned-record unpacking. The borrowed mock view is never rendered:
// this text-core executable admits no GPU, source Display, B/W/grayscale pixel,
// package or application parity. No source-generation copies or flag stripping.
namespace {
using namespace progpu::native::text;
static_assert(!std::is_copy_constructible_v<hinted_paragraph_generation> &&
    !std::is_copy_constructible_v<hinted_paragraph_glyph_frame>);

void require(bool value, std::source_location at = std::source_location::current()) {
    if (!value) throw std::runtime_error("hinted paragraph glyph packing control at " + std::to_string(at.line()));
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
    context_owner context{};
    std::vector<progpu_native_text_scalar> input{};
    std::array<progpu_native_text_feature, 2U> features{{{0x6C696761U, 1U, 0U, UINT32_MAX},
        {0x6B65726EU, 1U, 0U, UINT32_MAX}}};
    std::array<progpu_native_text_style_run, 2U> styles{};
    std::array<progpu_native_text_style_metrics, 2U> metrics{{{5.5F, 1.75F}, {9.25F, 2.5F}}};
    std::array<hinted_paragraph_style_configuration, 2U> device_styles{};
    progpu_native_text_shape_request shaping{};
    progpu_native_text_layout_options layout{};

    explicit fixture(font_hint_policy policy = font_hint_policy::truetype_40,
        float first_units = 0.8F, float second_units = 0.8F) {
        require(progpu_native_text_context_create(PROGPU_NATIVE_ABI_VERSION,
            reinterpret_cast<const std::uint8_t*>(bytes.data()), bytes.size(), 0U,
            nullptr, 0U, &context.value) == PROGPU_NATIVE_STATUS_SUCCESS);
        std::uint32_t second_font = UINT32_MAX;
        require(progpu_native_text_context_add_fallback_font(context.value,
            reinterpret_cast<const std::uint8_t*>(bytes.data()), bytes.size(), 0U, 0x7799U,
            &second_font) == PROGPU_NATIVE_STATUS_SUCCESS && second_font == 1U);
        const std::array<std::uint32_t, 9U> values{'A', 'B', 0x2007U, 0x2008U, ' ', 0x0627U, 0x03A9U, 'A', 'B'};
        std::uint32_t source = 9U;
        for (const auto value : values) {
            const auto length = value == 'A' ? std::uint16_t{2U} : std::uint16_t{1U};
            input.push_back({value, source, length, 0U, 0U, 0U}); source += length;
        }
        // Caller-authored source identities, distinct from the sole units/64
        // writer conversion. No metric or source scale is inferred by the driver.
        styles = {{{0U, 5U, 0U, (13.0F + 17.0F / 64.0F) * first_units / 1000.0F,
                0U, 2U, 0U, 0U, 0U, 0U, 0U},
            {5U, 4U, 1U, (17.0F + 23.0F / 64.0F) * second_units / 1000.0F,
                0U, 2U, 0U, 0U, 0U, 0U, 0U}}};
        device_styles = {{{0U, styles[0].scale,
            {13U * 64U + 17U, 13U * 64U + 17U, policy, 7U, 11U, {}}, first_units},
            {1U, styles[1].scale,
            {17U * 64U + 23U, 17U * 64U + 23U, policy, 19U, 23U, {}}, second_units}}};
        shaping.struct_size = sizeof(shaping); shaping.abi_version = PROGPU_NATIVE_ABI_VERSION;
        shaping.input = input.data(); shaping.input_count = static_cast<std::uint32_t>(input.size());
        shaping.features = features.data(); shaping.feature_count = static_cast<std::uint32_t>(features.size());
        shaping.direction = PROGPU_NATIVE_TEXT_DIRECTION_LEFT_TO_RIGHT;
        layout.struct_size = sizeof(layout); layout.scale = 1.0F; layout.maximum_width = 200.0F;
        layout.direction = PROGPU_NATIVE_TEXT_DIRECTION_LEFT_TO_RIGHT;
    }

    std::shared_ptr<const hinted_paragraph_generation> paragraph() {
        std::shared_ptr<const hinted_paragraph_generation> result;
        progpu_native_text_paragraph_result diagnostic{}; diagnostic.struct_size = sizeof(diagnostic);
        const auto status = try_layout_context_hinted_paragraph(context.value, shaping, layout, styles, metrics,
            device_styles, result, diagnostic);
        if (status != PROGPU_NATIVE_STATUS_SUCCESS)
            std::cerr << "paragraph fixture status=" << static_cast<unsigned int>(status) << " stage=" << diagnostic.error_stage
                << " error=" << diagnostic.error_code << '\n';
        require(status == PROGPU_NATIVE_STATUS_SUCCESS && result != nullptr);
        return result;
    }
};

hinted_paragraph_glyph_target target(float dpi = 1.25F) {
    return {320U, 180U, dpi, std::uintptr_t{0x771U}, {10.25F, 20.5F}, {0.125F, 0.25F, 0.5F, 0.75F}};
}

std::array<progpu_native_color, 3U> paints() {
    return {{{0.875F, 0.125F, 0.25F, 0.75F}, {0.25F, 0.5F, 0.875F, 0.625F},
        {-717.0F, -719.0F, -723.0F, -727.0F}}}; // Caller tail, outside the declared two paints.
}

using raw_geometry = std::vector<std::vector<hinted_glyph>>;
raw_geometry snapshot_raw(const hinted_paragraph_generation& paragraph) {
    raw_geometry result;
    for (const auto& run : paragraph.runs) result.push_back(run.generation->batch->glyphs);
    return result; // Raw value records only, never a self-referential paragraph copy.
}

void verify_raw_unchanged(const hinted_paragraph_generation& paragraph, const raw_geometry& before) {
    require(paragraph.runs.size() == before.size());
    for (std::size_t index = 0U; index < before.size(); ++index)
        require(paragraph.runs[index].generation->batch->glyphs == before[index]);
}

std::shared_ptr<const hinted_paragraph_glyph_frame> pack(std::shared_ptr<const hinted_paragraph_generation> paragraph,
    hinted_paragraph_glyph_target request, std::span<const progpu_native_color> colors,
    hinted_projection_policy policy = hinted_projection_policy::scalar_reference) {
    const auto result = create_hinted_paragraph_glyph_frame(std::move(paragraph), request, colors,
        policy, hinted_outline_coverage::nonzero_vector);
    if (result.status != PROGPU_NATIVE_STATUS_SUCCESS)
        std::cerr << "paragraph frame status=" << static_cast<unsigned int>(result.status) << " packing=" <<
            static_cast<unsigned int>(result.error.code) << " outline=" << static_cast<unsigned int>(result.error.outline) << '\n';
    require(result.status == PROGPU_NATIVE_STATUS_SUCCESS && result.error == hinted_glyph_frame_error{} && result.generation != nullptr);
    return result.generation;
}

progpu_native_point physical(hinted_outline_point point) {
    return {static_cast<float>(point.x_26_6) / 64.0F, static_cast<float>(point.y_26_6) / 64.0F};
}
bool equal(progpu_native_point a, progpu_native_point b) { return a.x == b.x && a.y == b.y; }

bool equal(progpu_native_color a, progpu_native_color b) {
    return a.r == b.r && a.g == b.g && a.b == b.b && a.a == b.a;
}

template<class T> bool bytes_equal(std::span<const T> a, std::span<const T> b) {
    static_assert(std::is_trivially_copyable_v<T>);
    return a.size() == b.size() && (a.empty() || std::memcmp(a.data(), b.data(), a.size_bytes()) == 0);
}

// Independent authored-font reference: four ON points, exactly four straight
// edges including explicit closure. No product converter/counting helper is
// used to construct this expected topology, bounds, source maps or draw records.
void verify_unpacked(const hinted_paragraph_glyph_frame& packed) {
    const auto& paragraph = *packed.paragraph();
    const auto request = packed.target();
    require(packed.coverage() == hinted_outline_coverage::nonzero_vector);
    require(packed.style_colors().size() == paragraph.styles.size());
    require(packed.run_slices().size() == paragraph.runs.size());
    std::size_t source_start = 0U, run_start = 0U, outline_start = 0U, segment_start = 0U;
    std::size_t no_ink = 0U, auxiliary = 0U, repeated_ids = 0U;
    for (std::size_t r = 0U; r < paragraph.runs.size(); ++r) {
        const auto& run = *paragraph.runs[r].generation;
        const auto& slice = packed.run_slices()[r];
        const auto& source_run = paragraph.runs[r];
        const auto& device = paragraph.device_styles[source_run.style_index];
        const auto& identity = *run.batch->identity;
        require(identity.source == paragraph.font_sources[source_run.font_index]);
        require(identity.policy == device.policy && identity.x_pixels_per_em_26_6 == device.x_pixels_per_em_26_6 &&
            identity.y_pixels_per_em_26_6 == device.y_pixels_per_em_26_6);
        require(identity.x_phase_26_6 == device.x_phase_26_6 && identity.y_phase_26_6 == device.y_phase_26_6);
        require(source_run.source_scale == paragraph.styles[source_run.style_index].scale);
        require(slice.source_start == source_start && slice.source_count == run.source_descriptor_count);
        require(slice.run_start == run_start && slice.run_count == run.glyphs.size());
        require(slice.outline_start == outline_start && slice.segment_start == segment_start);
        require(run.batch->glyphs.size() >= run.source_descriptor_count);
        auxiliary += run.batch->glyphs.size() - run.source_descriptor_count;
        std::vector<std::uint32_t> expected_sources;
        for (std::size_t d = 0U; d < run.source_descriptor_count; ++d) {
            const auto& raw = run.batch->glyphs[d];
            for (std::size_t old = 0U; old < d; ++old)
                if (run.batch->glyphs[old].glyph_index == raw.glyph_index) ++repeated_ids;
            if (raw.points.empty()) {
                require(raw.tags.empty() && raw.contour_ends.empty());
                expected_sources.push_back(hinted_no_outline); ++no_ink;
                require(packed.source_outline_indices()[source_start + d] == hinted_no_outline);
                continue;
            }
            require(raw.points.size() == 4U && raw.tags.size() == 4U && raw.contour_ends == std::vector<std::int16_t>{3});
            for (const auto tag : raw.tags) require((tag & 3U) == 1U);
            const auto global = static_cast<std::uint32_t>(outline_start++);
            expected_sources.push_back(global);
            require(packed.source_outline_indices()[source_start + d] == global);
            require(packed.outline_owners()[global] == hinted_paragraph_outline_owner{
                static_cast<std::uint32_t>(r), static_cast<std::uint32_t>(d)});
            const auto& outline = packed.outlines()[global];
            auto lo = physical(raw.points[0]), hi = lo;
            for (const auto point : raw.points) {
                const auto p = physical(point);
                lo.x = std::min(lo.x, p.x); lo.y = std::min(lo.y, p.y);
                hi.x = std::max(hi.x, p.x); hi.y = std::max(hi.y, p.y);
            }
            require(outline.segment_offset == segment_start && outline.segment_count == 4U);
            require(outline.min_x == lo.x && outline.min_y == lo.y && outline.max_x == hi.x && outline.max_y == hi.y);
            require(outline.raster_scale == 1.0F && outline.subpixel_x == 0.0F);
            // Raw coordinates already include this exact generation's X/Y
            // phase. Preserve Y-up here; positions below are already Y-down.
            require(lo.y > 0.0F && run.batch->identity->x_phase_26_6 != 0U && run.batch->identity->y_phase_26_6 != 0U);
            for (std::size_t p = 0U; p < 4U; ++p) {
                const auto& segment = packed.segments()[segment_start++];
                require(segment.kind == PROGPU_NATIVE_PATH_SEGMENT_LINE);
                require(equal(segment.p0, physical(raw.points[p])) && equal(segment.p1, physical(raw.points[(p + 1U) % 4U])));
                require(equal(segment.p2, {}) && equal(segment.p3, {}));
                require(segment.pad0 == 0U && segment.pad1 == 0U && segment.pad2 == 0U);
            }
        }
        for (std::size_t g = 0U; g < run.glyphs.size(); ++g) {
            const auto descriptor = run.descriptor_indices[g];
            require(descriptor < expected_sources.size());
            require(packed.run_outline_indices()[run_start + g] == expected_sources[descriptor]);
        }
        require(slice.outline_count == outline_start - slice.outline_start && slice.segment_count == segment_start - slice.segment_start);
        source_start += run.source_descriptor_count; run_start += run.glyphs.size();
    }
    require(source_start == packed.source_outline_indices().size() && run_start == packed.run_outline_indices().size());
    require(outline_start == packed.outlines().size() && segment_start == packed.segments().size());
    require(packed.outline_owners().size() == outline_start && no_ink != 0U && auxiliary != 0U && repeated_ids != 0U);
    std::size_t draw = 0U;
    for (std::size_t positioned = 0U; positioned < paragraph.glyphs.size(); ++positioned) {
        const auto& original = paragraph.glyphs[positioned];
        const auto owner = paragraph.positioned_owners[positioned];
        require(owner == paragraph.logical_owners[original.glyph_index]);
        const auto& run = paragraph.runs[owner.run_index];
        const auto& slice = packed.run_slices()[owner.run_index];
        require(run.generation->descriptor_indices[owner.run_glyph_index] == owner.descriptor_index);
        const auto outline = packed.source_outline_indices()[slice.source_start + owner.descriptor_index];
        if (outline == hinted_no_outline) continue;
        const auto& glyph = packed.glyphs()[draw];
        require(glyph.outline_index == outline && glyph.reserved == 0U && glyph.reserved2 == 0.0F);
        require(equal(glyph.position, {original.x + request.logical_origin.x, original.y + request.logical_origin.y}));
        require(equal(glyph.basis_x, {1.0F, 0.0F}) && equal(glyph.basis_y, {0.0F, 1.0F}));
        require(equal(glyph.color, packed.style_colors()[run.style_index]));
        require(glyph.atlas_to_logical_scale == 1.0F && glyph.bold_offset == 0.0F && glyph.italic_skew == 0.0F);
        require(packed.draw_owners()[draw] == hinted_paragraph_draw_owner{static_cast<std::uint32_t>(positioned),
            original.glyph_index, owner.run_index, owner.run_glyph_index, owner.descriptor_index, run.font_index, run.style_index});
        ++draw;
    }
    require(draw == packed.glyphs().size() && draw == packed.draw_owners().size());
    const auto wire = packed.frame();
    require(wire.struct_size == sizeof(wire) && wire.width == request.width && wire.height == request.height);
    require(wire.dpi_scale == request.dpi_scale && wire.target_view == request.target_view && equal(wire.clear_color, request.clear_color));
    require(wire.outlines == packed.outlines().data() && wire.outline_count == packed.outlines().size());
    require(wire.segments == packed.segments().data() && wire.segment_count == packed.segments().size());
    require(wire.glyphs == packed.glyphs().data() && wire.glyph_count == draw);
    require(wire.flags == 0U && wire.content_revision == 0U && wire.draw_state == nullptr);
}

void actual_paragraph_controls() {
    for (const auto policy : {font_hint_policy::truetype_35, font_hint_policy::truetype_40}) {
        fixture source(policy);
        const auto paragraph = source.paragraph();
        auto colors = paints(); const auto before = colors;
        const auto raw = snapshot_raw(*paragraph);
        const auto reference = pack(paragraph, target(), std::span(colors).first(2U));
        verify_unpacked(*reference);
        require(paragraph->font_sources.size() == 2U && paragraph->font_sources[0] != paragraph->font_sources[1]);
        require(paragraph->styles[0].scale != paragraph->styles[1].scale);
        require(std::any_of(reference->draw_owners().begin(), reference->draw_owners().end(), [](const auto owner) { return owner.style_index == 0U; }));
        require(std::any_of(reference->draw_owners().begin(), reference->draw_owners().end(), [](const auto owner) { return owner.style_index == 1U; }));
        // Identical final glyph IDs in different original slots are never
        // merged. These are real repeated source draws, not fabricated copies.
        require(reference->outlines().size() > 1U && reference->glyphs().size() > 1U);
        for (const auto path : {hinted_projection_policy::automatic, hinted_projection_policy::intrinsic_simd}) {
            const auto other = pack(paragraph, target(), std::span(colors).first(2U), path);
            verify_unpacked(*other);
            require(bytes_equal(reference->outlines(), other->outlines()) && bytes_equal(reference->segments(), other->segments()));
            require(bytes_equal(reference->glyphs(), other->glyphs()));
            require(std::equal(reference->draw_owners().begin(), reference->draw_owners().end(), other->draw_owners().begin(), other->draw_owners().end()));
        }
        const auto strict = create_hinted_paragraph_glyph_frame(paragraph, target(), std::span(colors).first(2U));
        require(strict.status == PROGPU_NATIVE_STATUS_UNSUPPORTED && strict.generation == nullptr);
        require(strict.error.code == hinted_glyph_frame_error_code::outline_conversion_failed && strict.error.outline == hinted_outline_error::unsupported_flags);
        // Actual observed native raster metadata must not be stripped to make
        // strict admission pass. The explicit vector policy is a separate gap.
        require(std::any_of(raw.begin(), raw.end(), [](const auto& run) {
            return std::any_of(run.begin(), run.end(), [](const auto& glyph) { return (glyph.outline_flags & 0x108) != 0; });
        }));
        verify_raw_unchanged(*paragraph, raw);
        require(bytes_equal<progpu_native_color>(colors, before));
        // Borrowing previously owned paints is safe: all publication is by
        // value and the new frame owns a distinct exact copy, not aliasing it.
        const auto borrowed = pack(paragraph, target(), reference->style_colors());
        require(borrowed->style_colors().data() != reference->style_colors().data());
        verify_unpacked(*reference); verify_unpacked(*borrowed);

        // Actual original explicit bidi controls put ink-bearing Latin glyphs
        // in an odd run. Their retained reverse descriptor map, not first-ID
        // lookup, must own both draws even though final glyph IDs are equal.
        source.input[5].code_point = 0x202EU; source.input[6].code_point = 'A';
        source.input[7].code_point = 'B'; source.input[8].code_point = 0x202CU;
        const auto rtl = source.paragraph();
        const auto rtl_raw = snapshot_raw(*rtl);
        const auto rtl_frame = pack(rtl, target(), std::span(colors).first(2U));
        require(std::any_of(rtl->runs.begin(), rtl->runs.end(), [](const auto& run) {
            return (run.bidi_level & 1) != 0 && run.generation->descriptor_indices.size() >= 2U &&
                run.generation->descriptor_indices.front() > run.generation->descriptor_indices.back();
        }));
        verify_unpacked(*rtl_frame); verify_raw_unchanged(*rtl, rtl_raw);
    }
}

void mapping_and_failure_controls() {
    auto colors = paints(); const auto before = colors;
    for (const float dpi : {1.0F, 1.25F, 2.0F}) {
        fixture source(font_hint_policy::truetype_40, 1.0F / dpi, 1.0F / dpi);
        const auto paragraph = source.paragraph();
        verify_unpacked(*pack(paragraph, target(dpi), std::span(colors).first(2U)));
    }
    fixture source;
    const auto paragraph = source.paragraph(); const auto raw = snapshot_raw(*paragraph);
    const auto old = pack(paragraph, target(), std::span(colors).first(2U));
    const auto reject = [&](hinted_paragraph_glyph_target request, std::span<const progpu_native_color> paints_span,
        hinted_glyph_frame_error_code code, progpu_native_status status = PROGPU_NATIVE_STATUS_INVALID_ARGUMENT,
        hinted_projection_policy policy = hinted_projection_policy::automatic,
        hinted_outline_coverage coverage = hinted_outline_coverage::nonzero_vector) {
        const auto result = create_hinted_paragraph_glyph_frame(paragraph, request, paints_span, policy, coverage);
        require(result.status == status && result.error.code == code && result.generation == nullptr);
        if (code == hinted_glyph_frame_error_code::outline_conversion_failed)
            require(result.error.outline == hinted_outline_error::unsupported_policy);
        verify_raw_unchanged(*paragraph, raw); verify_unpacked(*old);
        require(bytes_equal<progpu_native_color>(colors, before));
    };
    auto invalid = target(); invalid.width = 0U;
    reject(invalid, std::span(colors).first(2U), hinted_glyph_frame_error_code::invalid_argument);
    invalid = target(); invalid.height = 0U;
    reject(invalid, std::span(colors).first(2U), hinted_glyph_frame_error_code::invalid_argument);
    invalid = target(); invalid.target_view = 0U;
    reject(invalid, std::span(colors).first(2U), hinted_glyph_frame_error_code::invalid_argument);
    invalid = target(); invalid.logical_origin.y = std::numeric_limits<float>::quiet_NaN();
    reject(invalid, std::span(colors).first(2U), hinted_glyph_frame_error_code::invalid_argument);
    invalid = target(); invalid.clear_color.r = std::numeric_limits<float>::infinity();
    reject(invalid, std::span(colors).first(2U), hinted_glyph_frame_error_code::invalid_argument);
    invalid = target(); invalid.dpi_scale = 0.0F;
    reject(invalid, std::span(colors).first(2U), hinted_glyph_frame_error_code::invalid_argument);
    invalid = target(); invalid.dpi_scale = std::numeric_limits<float>::denorm_min();
    reject(invalid, std::span(colors).first(2U), hinted_glyph_frame_error_code::unsupported_mapping, PROGPU_NATIVE_STATUS_UNSUPPORTED);
    reject(target(), std::span(colors).first(1U), hinted_glyph_frame_error_code::invalid_argument);
    reject(target(), colors, hinted_glyph_frame_error_code::invalid_argument);
    auto invalid_colors = colors; invalid_colors[1].a = std::numeric_limits<float>::quiet_NaN();
    reject(target(), std::span(invalid_colors).first(2U), hinted_glyph_frame_error_code::invalid_argument);
    alignas(progpu_native_color) std::array<std::byte, sizeof(progpu_native_color) * 2U + 1U> misaligned{};
    reject(target(), {reinterpret_cast<const progpu_native_color*>(misaligned.data() + 1U), 2U}, hinted_glyph_frame_error_code::invalid_argument);
    const auto near_end = std::numeric_limits<std::uintptr_t>::max() & ~(std::uintptr_t{alignof(progpu_native_color)} - 1U);
    reject(target(), {reinterpret_cast<const progpu_native_color*>(near_end), 2U}, hinted_glyph_frame_error_code::invalid_argument);
    reject(target(), std::span(colors).first(2U), hinted_glyph_frame_error_code::outline_conversion_failed,
        PROGPU_NATIVE_STATUS_UNSUPPORTED, hinted_projection_policy::gpu_shader);
    reject(target(), std::span(colors).first(2U), hinted_glyph_frame_error_code::outline_conversion_failed,
        PROGPU_NATIVE_STATUS_UNSUPPORTED, hinted_projection_policy::automatic, static_cast<hinted_outline_coverage>(UINT32_MAX));

    // A late, real styled/font run mismatches the target; partial CPU packing
    // cannot escape the by-value failure result or replace an earlier frame.
    fixture wrong(font_hint_policy::truetype_40, 0.8F, 0.5F);
    auto mismatch = create_hinted_paragraph_glyph_frame(wrong.paragraph(), target(), std::span(colors).first(2U),
        hinted_projection_policy::automatic, hinted_outline_coverage::nonzero_vector);
    require(mismatch.status == PROGPU_NATIVE_STATUS_UNSUPPORTED && mismatch.generation == nullptr &&
        mismatch.error.code == hinted_glyph_frame_error_code::unsupported_mapping);
    // A positioned no-ink glyph is still a real fitted item in that run and
    // must not bypass its units contract merely because no draw is emitted.
    for (std::size_t i = 5U; i < wrong.input.size(); ++i) wrong.input[i].code_point = 0x03A9U;
    const auto noink_paragraph = wrong.paragraph();
    require(std::any_of(noink_paragraph->positioned_owners.begin(), noink_paragraph->positioned_owners.end(),
        [&](const auto owner) { return noink_paragraph->runs[owner.run_index].style_index == 1U; }));
    mismatch = create_hinted_paragraph_glyph_frame(noink_paragraph, target(), std::span(colors).first(2U),
        hinted_projection_policy::automatic, hinted_outline_coverage::nonzero_vector);
    require(mismatch.status == PROGPU_NATIVE_STATUS_UNSUPPORTED && mismatch.generation == nullptr &&
        mismatch.error.code == hinted_glyph_frame_error_code::unsupported_mapping);

    const float dpi = std::nextafter(1.0F, 2.0F), inverse = 1.0F / dpi;
    const float rounded_product_only = std::nextafter(inverse, 1.0F);
    require(rounded_product_only != inverse && rounded_product_only * dpi == 1.0F);
    fixture exact(font_hint_policy::truetype_40, inverse, inverse);
    verify_unpacked(*pack(exact.paragraph(), target(dpi), std::span(colors).first(2U)));
    fixture adjacent(font_hint_policy::truetype_40, inverse, rounded_product_only);
    mismatch = create_hinted_paragraph_glyph_frame(adjacent.paragraph(), target(dpi), std::span(colors).first(2U),
        hinted_projection_policy::automatic, hinted_outline_coverage::nonzero_vector);
    require(mismatch.status == PROGPU_NATIVE_STATUS_UNSUPPORTED && mismatch.generation == nullptr &&
        mismatch.error.code == hinted_glyph_frame_error_code::unsupported_mapping);
    verify_unpacked(*old); verify_raw_unchanged(*paragraph, raw);
}

void maximum_lines_and_retirement_controls() {
    auto colors = paints(); const auto before = colors;
    fixture source;
    source.input[4].code_point = '\n'; // Real original hard boundary, not a fake drawable glyph.
    const auto full = source.paragraph();
    const auto full_frame = pack(full, target(), std::span(colors).first(2U));
    source.layout.maximum_lines = 1U;
    // A retained run not positioned due to max-lines owns source outlines but
    // has no admitted target draw mapping. This is not a unit fallback.
    source.device_styles[1].logical_units_per_physical_pixel = 0.5F;
    const auto limited = source.paragraph();
    const auto limited_frame = pack(limited, target(), std::span(colors).first(2U));
    require(full->lines.size() > limited->lines.size() && limited->lines.size() == 1U);
    require(limited->source_input.size() == full->source_input.size() && limited->runs.size() == full->runs.size());
    require(limited->logical_glyphs.size() == full->logical_glyphs.size() && limited_frame->glyphs().size() < full_frame->glyphs().size());
    require(bytes_equal(full_frame->outlines(), limited_frame->outlines()) && bytes_equal(full_frame->segments(), limited_frame->segments()));
    require(std::equal(full_frame->source_outline_indices().begin(), full_frame->source_outline_indices().end(),
        limited_frame->source_outline_indices().begin(), limited_frame->source_outline_indices().end()));
    require(std::none_of(limited_frame->draw_owners().begin(), limited_frame->draw_owners().end(), [](const auto owner) { return owner.style_index == 1U; }));
    verify_unpacked(*full_frame); verify_unpacked(*limited_frame);
    require(bytes_equal<progpu_native_color>(colors, before));

    std::shared_ptr<const hinted_paragraph_glyph_frame> retained;
    std::weak_ptr<const hinted_paragraph_generation> paragraph_weak;
    std::weak_ptr<const hinted_shaped_run> run_weak;
    std::weak_ptr<const hinted_glyph_batch> batch_weak;
    std::weak_ptr<const owned_font_source> font_weak;
    raw_geometry raw;
    {
        fixture transient;
        auto paragraph = transient.paragraph(); raw = snapshot_raw(*paragraph);
        paragraph_weak = paragraph; run_weak = paragraph->runs.front().generation;
        batch_weak = paragraph->runs.front().generation->batch; font_weak = paragraph->font_sources[0];
        retained = pack(paragraph, target(), std::span(colors).first(2U));
        paragraph.reset();
        std::fill(transient.bytes.begin(), transient.bytes.end(), std::byte{0});
        for (auto& scalar : transient.input) scalar.code_point = 0U;
        transient.styles.fill({}); transient.metrics.fill({}); transient.device_styles.fill({});
        colors[0] = {}; colors[1] = {};
        // The context retires at scope exit; retained immutable frame owns the
        // exact source/layout/font generations and every uploaded-input array.
    }
    require(!paragraph_weak.expired() && !run_weak.expired() && !batch_weak.expired() && !font_weak.expired());
    verify_unpacked(*retained); verify_raw_unchanged(*retained->paragraph(), raw);
    require(equal(retained->style_colors()[0], before[0]) && equal(retained->style_colors()[1], before[1]));
    require(equal(colors[2], before[2]));
    const auto borrowed_wire = retained->frame();
    require(borrowed_wire.glyphs == retained->glyphs().data() && borrowed_wire.outlines == retained->outlines().data());
    retained.reset();
    require(paragraph_weak.expired() && run_weak.expired() && batch_weak.expired() && font_weak.expired());
}
#endif
} // namespace

int main() {
    try {
        const auto empty = create_hinted_paragraph_glyph_frame(nullptr, {}, {});
        require(empty.status == PROGPU_NATIVE_STATUS_INVALID_ARGUMENT && empty.generation == nullptr);
#if defined(PROGPU_NATIVE_FONT_HINTING)
        actual_paragraph_controls();
        mapping_and_failure_controls();
        maximum_lines_and_retirement_controls();
#endif
        return 0;
    } catch (const std::exception& error) {
        std::cerr << error.what() << '\n'; return 1;
    }
}
