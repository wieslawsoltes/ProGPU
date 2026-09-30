#include "progpu_native_hinted_glyph_frame.hpp"

#include <algorithm>
#include <array>
#include <cmath>
#include <cstring>
#include <iostream>
#include <limits>
#include <source_location>
#include <stdexcept>
#include <string>

// Independent authored retained geometry/metric controls. The target descriptor
// is never passed to a GPU: this executable links the text core, not the Backend
// call module. It does not qualify fonts, pixel output, either provider or UI.
namespace {
using namespace progpu::native::text;
void require(bool value, std::source_location at = std::source_location::current()) {
    if (!value) throw std::runtime_error("hinted glyph frame control at " + std::to_string(at.line()));
}

struct fixture final {
    std::shared_ptr<hinted_glyph_batch> batch{};
    std::shared_ptr<hinted_shaped_run> run{};
    std::shared_ptr<const hinted_text_layout> layout{};
    std::vector<text_line_break_kind> breaks{};
    std::vector<std::int8_t> levels{};
    std::vector<std::int32_t> ends{};
    std::vector<text_item_metrics> metrics{};

    explicit fixture(float units = 0.5F, std::uint32_t maximum_lines = 0U) {
        batch = std::make_shared<hinted_glyph_batch>();
        auto identity = std::make_shared<hinted_font_identity>();
        const std::array<std::byte, 64U> bytes{};
        identity->source = std::make_shared<const owned_font_source>(bytes, 0U);
        identity->x_phase_26_6 = 7U; identity->y_phase_26_6 = 11U;
        identity->variation_coordinates_16_16 = {-65536, 65536};
        batch->identity = identity;
        batch->glyphs.resize(5U);
        for (auto& glyph : batch->glyphs) glyph.glyph_index = 7U; // IDs cannot select a source slot.
        batch->glyphs[0].points = {{7, 11}, {135, 11}, {135, 139}, {7, 139}};
        batch->glyphs[0].tags = {1, 1, 1, 1}; batch->glyphs[0].contour_ends = {3};
        // Descriptor 1 is a real no-ink source; descriptor 2 has the SAME ID,
        // different retained geometry, and two distinct positioned repetitions.
        batch->glyphs[2].points = {{263, 11}, {455, 11}, {455, 203}, {263, 203}};
        batch->glyphs[2].tags = {1, 1, 1, 1}; batch->glyphs[2].contour_ends = {3};
        batch->glyphs[3].points = {{7, 11}, {71, 11}, {71, 75}, {7, 75}};
        batch->glyphs[3].tags = {0x18, 0, 0x08, 0x10}; batch->glyphs[3].contour_ends = {3};
        batch->glyphs[4].outline_flags = 2; batch->glyphs[4].points = {{999, 1001}}; // auxiliary, never drawn
        run = std::make_shared<hinted_shaped_run>();
        run->batch = batch; run->direction = shaping_direction::right_to_left;
        run->source_descriptor_count = 4U; run->figure_descriptor_start = 4U; run->figure_descriptor_count = 1U;
        run->shaping_input = {{'A', 0U, 1U}, {' ', 1U, 1U}, {'B', 2U, 1U}, {'C', 3U, 1U}};
        run->shaping_input.reserve(16U);
        run->glyphs = {{7U, 'C', 3, shaping_glyph_flags::none, 128, 0, 16, 16},
            {7U, 'B', 2, shaping_glyph_flags::none, 64, 0, -8, -8},
            {7U, 'B', 2, shaping_glyph_flags::none, 64, 0, 24, 0},
            {7U, ' ', 1, shaping_glyph_flags::none, 64, 0, 0, 0},
            {7U, 'A', 0, shaping_glyph_flags::none, 128, 0, -16, 32}};
        run->descriptor_indices = {3U, 2U, 2U, 1U, 0U};
        for (std::size_t index = 0U; index < run->glyphs.size(); ++index) {
            const auto cluster = run->glyphs[index].cluster;
            breaks.push_back(index + 1U < run->glyphs.size() && run->glyphs[index + 1U].cluster == cluster ?
                text_line_break_kind::prohibited : text_line_break_kind::opportunity);
            levels.push_back(1); ends.push_back(cluster + 1); metrics.push_back({3.0F, 1.0F});
        }
        text_layout_options options{};
        options.direction = run->direction; options.maximum_width = maximum_lines == 0U ? 20.0F : 2.0F * units;
        options.maximum_lines = maximum_lines;
        require(try_layout_hinted_shaped_run(run, breaks, levels, ends, metrics, {}, 1, units, options, layout));
    }
};

hinted_glyph_target target(float dpi = 2.0F) {
    return {128U, 64U, dpi, std::uintptr_t{0x771U}, {10.25F, 20.5F},
        {0.125F, 0.25F, 0.5F, 0.75F}, {0.875F, 0.625F, 0.375F, 0.25F}};
}

std::shared_ptr<const hinted_glyph_frame> pack(const fixture& value, const hinted_glyph_target& request) {
    std::shared_ptr<const hinted_glyph_frame> result;
    hinted_glyph_frame_error error{hinted_glyph_frame_error_code::invalid_layout, hinted_outline_error::invalid_run};
    require(try_create_hinted_glyph_frame(value.layout, value.run, request, result, &error,
        hinted_projection_policy::scalar_reference) && error == hinted_glyph_frame_error{});
    return result;
}

void exact_packing_controls() {
    fixture value;
    const auto snapshot = value.batch->glyphs;
    const auto request = target();
    const auto packed = pack(value, request);
    require(packed->layout() == value.layout && packed->layout()->run == value.run);
    require(packed->outlines().size() == 3U && packed->segments().size() == 12U && packed->glyphs().size() == 4U);
    const std::array<std::uint32_t, 4U> source{0U, hinted_no_outline, 1U, 2U};
    const std::array<std::uint32_t, 5U> original{2U, 1U, 1U, hinted_no_outline, 0U};
    const std::array<std::uint32_t, 4U> draw{0U, 1U, 2U, 4U};
    require(std::equal(source.begin(), source.end(), packed->source_outline_indices().begin()));
    require(std::equal(original.begin(), original.end(), packed->run_outline_indices().begin()));
    require(std::equal(draw.begin(), draw.end(), packed->draw_layout_indices().begin()));
    const std::array<progpu_native_point, 4U> positions{{{10.375F, 23.375F}, {11.1875F, 23.5625F},
        {11.9375F, 23.5F}, {12.625F, 23.25F}}};
    const std::array<std::uint32_t, 4U> indices{2U, 1U, 1U, 0U};
    for (std::size_t index = 0U; index < packed->glyphs().size(); ++index) {
        const auto& glyph = packed->glyphs()[index];
        require(glyph.outline_index == indices[index] && glyph.position.x == positions[index].x && glyph.position.y == positions[index].y &&
            glyph.basis_x.x == 1.0F && glyph.basis_x.y == 0.0F && glyph.basis_y.x == 0.0F && glyph.basis_y.y == 1.0F &&
            std::memcmp(&glyph.color, &request.color, sizeof(glyph.color)) == 0 && glyph.atlas_to_logical_scale == 1.0F &&
            glyph.bold_offset == 0.0F && glyph.italic_skew == 0.0F && glyph.reserved == 0U && glyph.reserved2 == 0.0F);
    }
    // Phase is already present exactly once in physical Y-up outline points,
    // never stripped, quarter-snapped, applied to instances or Y-flipped again.
    require(packed->segments()[0].p0.x == 7.0F / 64.0F && packed->segments()[0].p0.y == 11.0F / 64.0F);
    require(packed->segments()[4].p0.x == 263.0F / 64.0F && packed->segments()[4].p0.y == 11.0F / 64.0F);
    require(packed->segments()[8].kind == PROGPU_NATIVE_PATH_SEGMENT_QUADRATIC &&
        packed->segments()[8].p0.x == 7.0F / 64.0F && packed->segments()[8].p0.y == 43.0F / 64.0F &&
        packed->segments()[8].p1.y == 11.0F / 64.0F && packed->segments()[8].p2.x == 39.0F / 64.0F);
    for (const auto& outline : packed->outlines()) require(outline.raster_scale == 1.0F && outline.subpixel_x == 0.0F);
    const auto frame = packed->frame();
    require(frame.struct_size == sizeof(frame) && frame.width == request.width && frame.height == request.height &&
        frame.dpi_scale == request.dpi_scale && frame.target_view == request.target_view && frame.flags == 0U && frame.content_revision == 0U &&
        frame.draw_state == nullptr && frame.outlines == packed->outlines().data() && frame.segments == packed->segments().data() &&
        frame.glyphs == packed->glyphs().data() && frame.glyph_count == 4U &&
        std::memcmp(&frame.clear_color, &request.clear_color, sizeof(frame.clear_color)) == 0);
    require(value.batch->glyphs == snapshot);
#if defined(__aarch64__) || defined(_M_ARM64) || defined(__SSE2__) || defined(_M_X64)
    std::shared_ptr<const hinted_glyph_frame> intrinsic;
    require(try_create_hinted_glyph_frame(value.layout, value.run, request, intrinsic, nullptr, hinted_projection_policy::intrinsic_simd));
    require(std::memcmp(intrinsic->glyphs().data(), packed->glyphs().data(), packed->glyphs().size_bytes()) == 0 &&
        std::memcmp(intrinsic->outlines().data(), packed->outlines().data(), packed->outlines().size_bytes()) == 0 &&
        std::memcmp(intrinsic->segments().data(), packed->segments().data(), packed->segments().size_bytes()) == 0);
#endif
    fixture truncated(0.5F, 1U);
    const auto partial = pack(truncated, request);
    require(partial->layout()->glyphs.size() < truncated.run->glyphs.size() && partial->outlines().size() == 3U &&
        partial->source_outline_indices().size() == 4U && partial->run_outline_indices().size() == 5U);
    fixture no_ink;
    for (std::size_t index = 0U; index < no_ink.run->source_descriptor_count; ++index) {
        no_ink.batch->glyphs[index].points.clear(); no_ink.batch->glyphs[index].tags.clear(); no_ink.batch->glyphs[index].contour_ends.clear();
    }
    const auto blank = pack(no_ink, request);
    require(blank->glyphs().empty() && blank->outlines().empty() && blank->segments().empty() &&
        blank->layout()->glyphs.size() == 5U && blank->source_outline_indices().size() == 4U &&
        std::all_of(blank->source_outline_indices().begin(), blank->source_outline_indices().end(),
            [](std::uint32_t index) { return index == hinted_no_outline; }));
}

void mapping_and_retirement_controls() {
    for (const auto pair : {std::array{1.0F, 1.0F}, std::array{0.8F, 1.25F}, std::array{0.5F, 2.0F}, std::array{2.0F, 0.5F}}) {
        fixture value(pair[0]);
        const auto packed = pack(value, target(pair[1]));
        require(packed->glyphs().front().atlas_to_logical_scale == 1.0F && packed->target().dpi_scale == pair[1]);
    }
    std::weak_ptr<const hinted_text_layout> weak_layout;
    std::weak_ptr<const hinted_shaped_run> weak_run;
    std::weak_ptr<const hinted_glyph_batch> weak_batch;
    std::weak_ptr<const owned_font_source> weak_source;
    std::shared_ptr<const hinted_glyph_frame> packed;
    {
        fixture value;
        weak_layout = value.layout; weak_run = value.run; weak_batch = value.batch; weak_source = value.batch->identity->source;
        packed = pack(value, target());
        // Original caller metadata can retire/change; the actual fitting
        // generation and its exact shaping input/descriptors stay owned.
        value.breaks.clear(); value.levels.clear(); value.ends.clear(); value.metrics.clear();
        value.layout.reset(); value.run.reset(); value.batch.reset();
    }
    require(!weak_layout.expired() && !weak_run.expired() && !weak_batch.expired() && !weak_source.expired());
    require(packed->layout()->item_metrics.front().ascent == 3.0F && packed->layout()->run->shaping_input.size() == 4U &&
        packed->layout()->run->batch->glyphs.size() == 5U && packed->frame().glyphs[0].position.y == 23.375F);
    packed.reset();
    require(weak_layout.expired() && weak_run.expired() && weak_batch.expired() && weak_source.expired());
}

void atomic_rejection_and_alias_controls() {
    fixture value;
    auto result = pack(value, target());
    const auto previous = result;
    const auto old_frame = result->frame();
    std::vector<progpu_native_positioned_glyph> old_glyphs(result->glyphs().begin(), result->glyphs().end());
    const auto reject = [&](std::shared_ptr<const hinted_text_layout> layout, std::shared_ptr<const hinted_shaped_run> run,
        const hinted_glyph_target& request, hinted_glyph_frame_error_code code,
        hinted_outline_error outline = hinted_outline_error::none) {
        hinted_glyph_frame_error error{};
        require(!try_create_hinted_glyph_frame(std::move(layout), std::move(run), request, result, &error,
            hinted_projection_policy::scalar_reference) && error == hinted_glyph_frame_error{code, outline} && result == previous);
        require(result->frame().glyphs == old_frame.glyphs &&
            std::memcmp(result->glyphs().data(), old_glyphs.data(), old_glyphs.size() * sizeof(old_glyphs[0])) == 0);
    };
    for (unsigned int invalid = 0U; invalid < 8U; ++invalid) {
        auto request = target();
        if (invalid == 0U) request.width = 0U;
        if (invalid == 1U) request.height = 0U;
        if (invalid == 2U) request.target_view = 0U;
        if (invalid == 3U) request.dpi_scale = 0.0F;
        if (invalid == 4U) request.dpi_scale = std::numeric_limits<float>::infinity();
        if (invalid == 5U) request.logical_origin.x = std::numeric_limits<float>::quiet_NaN();
        if (invalid == 6U) request.color.a = std::numeric_limits<float>::quiet_NaN();
        if (invalid == 7U) request.clear_color.b = std::numeric_limits<float>::quiet_NaN();
        reject(value.layout, value.run, request, hinted_glyph_frame_error_code::invalid_argument);
    }
    for (const auto dpi : {1.0F, 1.25F, std::numeric_limits<float>::denorm_min(), std::numeric_limits<float>::max()})
        reject(value.layout, value.run, target(dpi), hinted_glyph_frame_error_code::unsupported_mapping);
    const float adjacent_dpi = std::nextafter(1.0F, 2.0F);
    const float actual_inverse = 1.0F / adjacent_dpi;
    const float rounded_product_only = std::nextafter(actual_inverse, 1.0F);
    require(rounded_product_only * adjacent_dpi == 1.0F && rounded_product_only != actual_inverse);
    fixture ambiguous(rounded_product_only);
    reject(ambiguous.layout, ambiguous.run, target(adjacent_dpi), hinted_glyph_frame_error_code::unsupported_mapping);
    fixture actual_mapping(actual_inverse);
    require(pack(actual_mapping, target(adjacent_dpi))->glyphs().size() == 4U);
    auto different = std::make_shared<hinted_shaped_run>(*value.run);
    reject(value.layout, different, target(), hinted_glyph_frame_error_code::invalid_argument); // even exact bytes are not the same generation
    for (unsigned int invalid = 0U; invalid < 10U; ++invalid) {
        auto layout = std::make_shared<hinted_text_layout>(*value.layout);
        if (invalid == 0U) layout->descriptor_indices[0] = 2U; // SAME ID, wrong descriptor
        if (invalid == 1U) layout->glyphs[0].glyph_index = UINT32_MAX;
        if (invalid == 2U) layout->glyphs[0].glyph_id = 99U;
        if (invalid == 3U) layout->glyphs[0].cluster = 99;
        if (invalid == 4U) layout->logical_run_indices[0] = layout->logical_run_indices[1];
        if (invalid == 5U) layout->bidi_levels[0] = 0;
        if (invalid == 6U) layout->cluster_ends[0] = -1;
        if (invalid == 7U) layout->glyphs[0].advance_x = std::numeric_limits<float>::quiet_NaN();
        if (invalid == 8U) layout->descriptor_indices.pop_back();
        if (invalid == 9U) layout->lines[0].glyph_count = UINT32_MAX;
        reject(layout, value.run, target(), hinted_glyph_frame_error_code::invalid_layout);
    }
    auto overflow = std::make_shared<hinted_text_layout>(*value.layout);
    overflow->glyphs[0].x = std::numeric_limits<float>::max();
    auto overflow_target = target(); overflow_target.logical_origin.x = std::numeric_limits<float>::max();
    reject(overflow, value.run, overflow_target, hinted_glyph_frame_error_code::invalid_layout);
    fixture bad_outline;
    bad_outline.batch->glyphs[3].points.back().x_26_6 = 16777217L;
    reject(bad_outline.layout, bad_outline.run, target(), hinted_glyph_frame_error_code::outline_conversion_failed,
        hinted_outline_error::unsupported_coordinates);
    bad_outline.batch->glyphs[3].points.back().x_26_6 = 7L;
    bad_outline.batch->glyphs[3].outline_flags = 2;
    reject(bad_outline.layout, bad_outline.run, target(), hinted_glyph_frame_error_code::outline_conversion_failed,
        hinted_outline_error::unsupported_flags);
    bad_outline.batch->glyphs[3].outline_flags = 0;
    bad_outline.batch->glyphs[3].tags[0] = 4U;
    reject(bad_outline.layout, bad_outline.run, target(), hinted_glyph_frame_error_code::outline_conversion_failed,
        hinted_outline_error::unsupported_flags);
    hinted_glyph_frame_error policy_error{};
    require(!try_create_hinted_glyph_frame(value.layout, value.run, target(), result, &policy_error,
        hinted_projection_policy::gpu_shader) && result == previous &&
        policy_error.outline == hinted_outline_error::unsupported_policy);
    // No error may overwrite any old/new owner or unused retained capacity.
    const auto alias_reject = [&](hinted_glyph_frame_error* error) {
        require(!try_create_hinted_glyph_frame(value.layout, value.run, target(), result, error,
            hinted_projection_policy::scalar_reference) && result == previous);
    };
    alias_reject(reinterpret_cast<hinted_glyph_frame_error*>(&result));
    alias_reject(reinterpret_cast<hinted_glyph_frame_error*>(const_cast<progpu_native_positioned_glyph*>(previous->glyphs().data())));
    alias_reject(reinterpret_cast<hinted_glyph_frame_error*>(const_cast<text_layout_metrics*>(&previous->layout()->metrics)));
    const auto input_before = value.run->shaping_input;
    alias_reject(reinterpret_cast<hinted_glyph_frame_error*>(value.run->shaping_input.data() + value.run->shaping_input.size()));
    require(value.run->shaping_input.size() == input_before.size() &&
        std::memcmp(value.run->shaping_input.data(), input_before.data(), input_before.size() * sizeof(unicode_scalar)) == 0);
    auto mutable_layout = std::make_shared<hinted_text_layout>(*value.layout);
    mutable_layout->logical_cluster_ends.reserve(16U);
    auto* spare = reinterpret_cast<hinted_glyph_frame_error*>(mutable_layout->logical_cluster_ends.data() + mutable_layout->logical_cluster_ends.size());
    require(!try_create_hinted_glyph_frame(mutable_layout, value.run, target(), result, spare,
        hinted_projection_policy::scalar_reference) && result == previous && mutable_layout->logical_cluster_ends == value.layout->logical_cluster_ends);
    auto request = target(); const auto request_before = request;
    require(!try_create_hinted_glyph_frame(value.layout, value.run, request, result,
        reinterpret_cast<hinted_glyph_frame_error*>(&request), hinted_projection_policy::scalar_reference) && result == previous &&
        std::memcmp(&request, &request_before, sizeof(request)) == 0);
    struct error_tail final { hinted_glyph_frame_error value{}; std::array<std::uint32_t, 4U> tail{71U, 73U, 79U, 83U}; } tail;
    const auto saved_tail = tail.tail;
    require(try_create_hinted_glyph_frame(value.layout, value.run, target(), result, &tail.value,
        hinted_projection_policy::scalar_reference) && tail.value == hinted_glyph_frame_error{} && tail.tail == saved_tail);
    const auto output = result->source_outline_indices();
    require(hinted_glyph_frame_output_aliases(*result,
        {reinterpret_cast<std::byte*>(const_cast<std::uint32_t*>(output.data())), output.size_bytes()}));
}
} // namespace

int main() {
    try {
        exact_packing_controls(); mapping_and_retirement_controls(); atomic_rejection_and_alias_controls();
        std::cout << "owned hinted glyph frame CPU packing controls passed\n"; return 0;
    } catch (const std::exception& error) {
        std::cerr << error.what() << '\n'; return 1;
    }
}
