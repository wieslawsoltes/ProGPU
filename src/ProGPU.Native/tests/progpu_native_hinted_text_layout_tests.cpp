#include "progpu_native_hinted_text_layout.hpp"
#include "progpu_native_hinted_shape_fixture.hpp"

#include <algorithm>
#include <array>
#include <cmath>
#include <cstring>
#include <iostream>
#include <limits>
#include <numeric>
#include <source_location>
#include <stdexcept>
#include <string>

// Independent raw controls plus the original authored actual hint fixture.
// Neither group enables source Display or runs a renderer/application host.
namespace {
using namespace progpu::native::text;

void require(bool value, std::source_location location = std::source_location::current()) {
    if (!value) throw std::runtime_error("hinted text fitting control at " + std::to_string(location.line()));
}

struct metadata final {
    std::vector<text_line_break_kind> breaks{};
    std::vector<std::int8_t> levels{};
    std::vector<std::int32_t> ends{};
    std::vector<text_item_metrics> metrics{};
    std::vector<text_justification_class> classes{};
};

metadata source_metadata(const hinted_shaped_run& run) {
    metadata values;
    const auto count = run.glyphs.size();
    for (std::size_t index = 0U; index < count; ++index) {
        const auto& glyph = run.glyphs[index];
        values.breaks.push_back(index + 1U < count && run.glyphs[index + 1U].cluster == glyph.cluster
            ? text_line_break_kind::prohibited : text_line_break_kind::opportunity);
        values.levels.push_back(run.direction == shaping_direction::right_to_left ? 1 : 0);
        std::int32_t end = std::numeric_limits<std::int32_t>::max();
        for (const auto& other : run.glyphs)
            if (other.cluster > glyph.cluster) end = std::min(end, other.cluster);
        if (end == std::numeric_limits<std::int32_t>::max()) end = glyph.cluster + 1;
        values.ends.push_back(end);
        values.metrics.push_back({5.0F + static_cast<float>(glyph.cluster) * 0.25F, 2.0F});
        values.classes.push_back(glyph.code_point == 0x20U ? text_justification_class::word_space :
            text_justification_class::content);
    }
    // Invalid unused metadata is capacity, never published or modified.
    values.breaks.push_back(static_cast<text_line_break_kind>(99U));
    values.levels.push_back(-77);
    values.ends.push_back(-717);
    values.metrics.push_back({-719.0F, -723.0F});
    values.classes.push_back(static_cast<text_justification_class>(99U));
    return values;
}

bool fit(std::shared_ptr<const hinted_shaped_run> run, const metadata& values,
    float units, const text_layout_options& options,
    std::shared_ptr<const hinted_text_layout>& result, font_error* error = nullptr) {
    return try_layout_hinted_shaped_run(std::move(run), values.breaks, values.levels, values.ends,
        values.metrics, values.classes, options.direction == shaping_direction::right_to_left ? 1 : 0,
        units, options, result, error);
}

std::shared_ptr<hinted_shaped_run> raw_run(shaping_direction direction) {
    const auto bytes = progpu::native::tests::make_hinted_shape_font();
    auto identity = std::make_shared<hinted_font_identity>();
    identity->source = std::make_shared<owned_font_source>(bytes, 0U);
    identity->x_pixels_per_em_26_6 = 13U * 64U + 17U;
    identity->y_pixels_per_em_26_6 = 17U * 64U + 23U;
    auto batch = std::make_shared<hinted_glyph_batch>();
    batch->identity = identity;
    batch->glyphs.resize(7U);
    for (std::size_t index = 0U; index < batch->glyphs.size(); ++index) {
        auto& glyph = batch->glyphs[index];
        glyph.glyph_index = 1U;
        glyph.advance_x_26_6 = static_cast<long>(640U + index * 64U);
        glyph.points.reserve(3U);
        glyph.points.push_back({static_cast<long>(index * 11U), static_cast<long>(index * 13U)});
    }
    auto run = std::make_shared<hinted_shaped_run>();
    run->batch = batch;
    run->direction = direction;
    run->source_descriptor_count = 5U;
    run->figure_descriptor_start = 5U; run->figure_descriptor_count = 1U;
    run->punctuation_descriptor_start = 6U; run->punctuation_descriptor_count = 1U;
    run->glyphs = {{1U, 'A', 0, shaping_glyph_flags::none, 320, 0, 11, 13},
        {1U, 'B', 2, shaping_glyph_flags::none, 96, 0, -15, -17},
        {1U, 0x301U, 2, shaping_glyph_flags::none, 64, 0, 9, 19},
        {1U, 'C', 4, shaping_glyph_flags::none, 384, 0, 7, -23},
        {1U, 0x20U, 6, shaping_glyph_flags::none, 160, 0, -2, 0}};
    run->descriptor_indices = {0U, 1U, 2U, 3U, 4U};
    if (direction == shaping_direction::right_to_left) {
        std::reverse(run->glyphs.begin(), run->glyphs.end());
        std::reverse(run->descriptor_indices.begin(), run->descriptor_indices.end());
    }
    return run;
}

void compare_original_writer(const hinted_text_layout& layout, const metadata& values) {
    const auto& run = *layout.run;
    // Independently stable-sort by source clusters rather than repeating the
    // adapter's reverse-group walk. Equal-cluster shaper order remains exact.
    std::vector<std::uint32_t> original(run.glyphs.size());
    std::iota(original.begin(), original.end(), 0U);
    std::stable_sort(original.begin(), original.end(), [&](auto left, auto right) {
        return run.glyphs[left].cluster < run.glyphs[right].cluster;
    });
    std::vector<shaping_glyph> glyphs;
    metadata logical;
    for (const auto index : original) {
        auto glyph = run.glyphs[index];
        glyph.advance_y = -glyph.advance_y; glyph.offset_y = -glyph.offset_y;
        glyphs.push_back(glyph);
        logical.breaks.push_back(values.breaks[index]); logical.levels.push_back(values.levels[index]);
        logical.ends.push_back(values.ends[index]); logical.metrics.push_back(values.metrics[index]);
        logical.classes.push_back(values.classes[index]);
    }
    require(layout.logical_run_indices == original && layout.logical_glyphs.size() == glyphs.size());
    for (std::size_t index = 0U; index < glyphs.size(); ++index)
        require(std::memcmp(&glyphs[index], &layout.logical_glyphs[index], sizeof(shaping_glyph)) == 0);
    require(layout.breaks_after == logical.breaks && layout.logical_bidi_levels == logical.levels &&
        layout.logical_cluster_ends == logical.ends && layout.justification_classes == logical.classes);
    text_layout_requirements requirements{};
    const float scale = layout.logical_units_per_physical_pixel / 64.0F;
    std::vector<float> scales(glyphs.size(), scale);
    require(try_get_scaled_text_layout_requirements(glyphs, logical.breaks, scales,
        layout.options, requirements));
    std::vector<positioned_text_glyph> positioned(requirements.glyph_capacity);
    std::vector<positioned_text_line> lines(requirements.line_capacity);
    std::vector<text_visual_cluster_group> groups(requirements.glyph_capacity);
    std::vector<std::uint32_t> indices(requirements.glyph_capacity);
    std::uint32_t count = 0U, line_count = 0U;
    require(try_layout_measured_logical_shaped_text(glyphs, logical.breaks, logical.levels,
        scales, layout.paragraph_level, layout.options, {}, {}, {groups, indices},
        positioned, lines, count, line_count, logical.classes, logical.metrics));
    require(count == layout.glyphs.size() && line_count == layout.lines.size());
    for (std::size_t index = 0U; index < count; ++index) {
        const auto source = original[positioned[index].glyph_index];
        positioned[index].glyph_index = source;
        const auto& actual = layout.glyphs[index];
        const auto& expected = positioned[index];
        require(actual.glyph_index == expected.glyph_index && actual.glyph_id == expected.glyph_id &&
            actual.cluster == expected.cluster && actual.x == expected.x && actual.y == expected.y &&
            actual.advance_x == expected.advance_x && actual.advance_y == expected.advance_y &&
            layout.descriptor_indices[index] == run.descriptor_indices[source] &&
            layout.bidi_levels[index] == values.levels[source] && layout.cluster_ends[index] == values.ends[source]);
        const auto& descriptor = run.batch->glyphs[layout.descriptor_indices[index]];
        require(descriptor.glyph_index == actual.glyph_id);
    }
    for (std::size_t index = 0U; index < line_count; ++index) {
        const auto& actual = layout.lines[index];
        const auto& expected = lines[index];
        require(actual.glyph_start == expected.glyph_start && actual.glyph_count == expected.glyph_count &&
            actual.input_start == expected.input_start && actual.input_end == expected.input_end &&
            actual.width == expected.width && actual.baseline_y == expected.baseline_y &&
            actual.height == expected.height && actual.clipped == expected.clipped && actual.flags == expected.flags);
    }
    text_layout_metrics measured{};
    require(try_measure_measured_text_lines(std::span<const positioned_text_line>(lines).first(line_count),
        layout.options.maximum_width, measured));
    require(layout.metrics.content_width == measured.content_width && layout.metrics.content_height == measured.content_height &&
        layout.metrics.measured_width == measured.measured_width && layout.metrics.measured_height == measured.measured_height);
}

void raw_controls() {
    for (const auto direction : {shaping_direction::left_to_right, shaping_direction::right_to_left}) {
        for (const auto units : {0.75F, 1.0F, 2.0F}) {
            const auto run = raw_run(direction);
            auto values = source_metadata(*run);
            const auto before = values;
            for (const auto alignment : {text_alignment::left, text_alignment::center, text_alignment::right}) {
                for (const auto maximum_lines : {0U, 1U}) {
                    text_layout_options options{};
                    options.direction = direction; options.maximum_width = 8.0F * units;
                    options.line_height = 2.0F; options.alignment = alignment; options.maximum_lines = maximum_lines;
                    std::shared_ptr<const hinted_text_layout> layout;
                    font_error error = font_error::verification_failed;
                    require(fit(run, values, units, options, layout, &error) && error == font_error::none && layout->run == run);
                    compare_original_writer(*layout, values);
                    require(layout->logical_glyphs.size() == run->glyphs.size() && layout->run->batch->glyphs.size() == 7U &&
                        layout->options.scale == units / 64.0F);
                    if (maximum_lines == 1U) require(layout->glyphs.size() < run->glyphs.size() && layout->lines.size() == 1U);
                    for (const auto& line : layout->lines) require(line.height >= 7.0F && line.baseline_y >= 5.0F);
                    for (std::size_t index = 0U; index < layout->lines.size(); ++index) {
                        const auto& line = layout->lines[index];
                        const auto extra = std::max(0.0F, options.maximum_width - line.width);
                        const auto expected = alignment == text_alignment::center ? extra * 0.5F :
                            alignment == text_alignment::right ? extra : 0.0F;
                        require(layout->line_origins[index] == expected);
                    }
                }
            }
            require(values.breaks == before.breaks && values.levels == before.levels && values.ends == before.ends &&
                values.classes == before.classes && std::memcmp(values.metrics.data(), before.metrics.data(),
                    values.metrics.size() * sizeof(text_item_metrics)) == 0);
            text_layout_options options{}; options.direction = direction;
            std::shared_ptr<const hinted_text_layout> retained;
            require(fit(run, values, units, options, retained));
            const auto owned_end = retained->logical_cluster_ends.front();
            std::fill(values.ends.begin(), values.ends.end(), -1);
            require(retained->logical_cluster_ends.front() == owned_end && retained->run == run);
        }
    }
}

void justification_controls() {
    const auto run = raw_run(shaping_direction::right_to_left);
    run->source_descriptor_count = 5U;
    for (std::size_t index = 0U; index < run->glyphs.size(); ++index) {
        auto& glyph = run->glyphs[index];
        glyph.cluster = static_cast<std::int32_t>(4U - index);
        glyph.code_point = glyph.cluster == 1 || glyph.cluster == 3 ? 0x20U : 'A';
        glyph.advance_x = 64; glyph.offset_x = 6400; // Ink cannot own a caret origin.
    }
    const auto values = source_metadata(*run);
    text_layout_options options{};
    options.direction = run->direction; options.maximum_width = 4.5F; options.alignment = text_alignment::justify;
    std::shared_ptr<const hinted_text_layout> layout;
    require(fit(run, values, 1.0F, options, layout));
    compare_original_writer(*layout, values);
    require(layout->lines.size() == 2U && layout->lines[0].flags ==
        static_cast<std::uint8_t>(positioned_text_line_flags::right_to_left_justified) &&
        layout->line_origins[0] == -1.0F && layout->line_origins[1] == 0.0F);
    std::vector<text_cluster_box> boxes(layout->glyphs.size());
    std::vector<text_caret_stop> carets(layout->glyphs.size() * 2U);
    std::uint32_t box_count = 0U, caret_count = 0U;
    require(try_build_advance_text_interaction(layout->glyphs, layout->lines, layout->cluster_ends,
        layout->bidi_levels, layout->line_origins, boxes, carets, box_count, caret_count));
    require(box_count != 0U && caret_count != 0U && boxes[0].x == -1.0F && layout->glyphs[0].x > 90.0F);
}

void rejection_controls() {
    const auto run = raw_run(shaping_direction::left_to_right);
    const auto values = source_metadata(*run);
    text_layout_options options{};
    std::shared_ptr<const hinted_text_layout> retained;
    require(fit(run, values, 1.0F, options, retained));
    const auto previous = retained;
    for (const auto units : {0.0F, -1.0F, std::numeric_limits<float>::infinity(),
        std::numeric_limits<float>::denorm_min(), std::numeric_limits<float>::max()}) {
        require(!fit(run, values, units, options, retained) && retained == previous);
    }
    for (const auto direction : {shaping_direction::unspecified, shaping_direction::top_to_bottom,
        shaping_direction::bottom_to_top}) {
        auto invalid = std::make_shared<hinted_shaped_run>(*run); invalid->direction = direction;
        require(!fit(invalid, values, 1.0F, options, retained) && retained == previous);
    }
    auto invalid = std::make_shared<hinted_shaped_run>(*run);
    invalid->glyphs.back().offset_y = std::numeric_limits<std::int32_t>::min();
    require(!fit(invalid, values, 1.0F, options, retained) && retained == previous);
    invalid = std::make_shared<hinted_shaped_run>(*run);
    invalid->glyphs.back().advance_y = std::numeric_limits<std::int32_t>::min();
    require(!fit(invalid, values, 1.0F, options, retained) && retained == previous);
    invalid = std::make_shared<hinted_shaped_run>(*run); invalid->descriptor_indices.back() = 5U;
    require(!fit(invalid, values, 1.0F, options, retained) && retained == previous);
    invalid = std::make_shared<hinted_shaped_run>(*run); invalid->glyphs.back().glyph_id = 2U;
    require(!fit(invalid, values, 1.0F, options, retained) && retained == previous);
    auto bad = values; bad.metrics.back() = {}; bad.metrics[0].ascent = -1.0F;
    require(!fit(run, bad, 1.0F, options, retained) && retained == previous);
    bad = values; bad.ends[0] = 0;
    require(!fit(run, bad, 1.0F, options, retained) && retained == previous);
    bad = values; bad.levels[0] = 1;
    require(!fit(run, bad, 1.0F, options, retained) && retained == previous);
    options.scale = 0.01F;
    require(!fit(run, values, 1.0F, options, retained) && retained == previous);
    options.scale = 1.0F; options.trimming = text_trimming::character_ellipsis;
    require(!fit(run, values, 1.0F, options, retained) && retained == previous);
    options.trimming = text_trimming::none;
    require(!try_layout_hinted_shaped_run(run, values.breaks, values.levels, values.ends,
        {}, values.classes, 0, 1.0F, options, retained) && retained == previous);
    bad = values;
    const auto tail = bad.ends.back();
    require(!fit(run, bad, 1.0F, options, retained,
        reinterpret_cast<font_error*>(&bad.ends.back())) && retained == previous && bad.ends.back() == tail);
    const auto metrics_before = previous->metrics;
    require(!fit(run, values, 1.0F, options, retained,
        reinterpret_cast<font_error*>(const_cast<float*>(&previous->metrics.content_width))) && retained == previous &&
        previous->metrics.content_width == metrics_before.content_width);
    require(!fit(run, values, 1.0F, options, retained,
        reinterpret_cast<font_error*>(&retained)) && retained == previous);
    const auto source_count = run->source_descriptor_count;
    require(!fit(run, values, 1.0F, options, retained,
        reinterpret_cast<font_error*>(&run->source_descriptor_count)) && retained == previous &&
        run->source_descriptor_count == source_count);
    const auto replacement = raw_run(shaping_direction::left_to_right);
    const auto replacement_values = source_metadata(*replacement);
    // The old snapshot's exact generation remains protected even when the
    // prospective input owns an entirely different run and batch.
    require(!fit(replacement, replacement_values, 1.0F, options, retained,
        reinterpret_cast<font_error*>(&run->source_descriptor_count)) && retained == previous &&
        run->source_descriptor_count == source_count);
    const auto& old_points = run->batch->glyphs.back().points;
    // This slot is outside the old generation's used point span but inside its
    // retained allocation. No write or dereference of the unused object occurs.
    require(!fit(replacement, replacement_values, 1.0F, options, retained,
        reinterpret_cast<font_error*>(const_cast<hinted_outline_point*>(old_points.data() + old_points.size()))) &&
        retained == previous && old_points.size() == 1U);
    auto empty = std::make_shared<hinted_shaped_run>(*run);
    empty->glyphs.clear(); empty->descriptor_indices.clear(); empty->source_descriptor_count = 0U;
    metadata none;
    // A valid shared_ptr publication object appears only in unused metadata
    // capacity. Rejection precedes any shared_ptr read/write through that span.
    const auto publication_words = std::span<const std::int32_t>(
        reinterpret_cast<const std::int32_t*>(&retained), sizeof(retained) / sizeof(std::int32_t));
    require(!try_layout_hinted_shaped_run(empty, {}, {}, publication_words, {}, {}, 0,
        1.0F, options, retained) && retained == previous);
    require(fit(empty, none, 1.0F, options, retained) && retained->glyphs.empty() && retained->lines.empty());
}

#if defined(PROGPU_NATIVE_FONT_HINTING)
void actual_generation_controls() {
    const auto bytes = progpu::native::tests::make_hinted_shape_font();
    for (const auto interpreter : {font_hint_policy::truetype_35, font_hint_policy::truetype_40}) {
        progpu_native_text_context* context = nullptr;
        require(progpu_native_text_context_create(PROGPU_NATIVE_ABI_VERSION,
            reinterpret_cast<const std::uint8_t*>(bytes.data()), bytes.size(), 0U, nullptr, 0U,
            &context) == PROGPU_NATIVE_STATUS_SUCCESS);
        struct owner final {
            progpu_native_text_context* value;
            ~owner() { if (value != nullptr) progpu_native_text_context_destroy(value); }
        } lifetime{context};
        hinted_font_configuration configuration{};
        configuration.x_pixels_per_em_26_6 = 13U * 64U + 17U;
        configuration.y_pixels_per_em_26_6 = 17U * 64U + 23U;
        configuration.policy = interpreter; configuration.x_phase_26_6 = 7U; configuration.y_phase_26_6 = 11U;
        const std::array<unicode_scalar, 5U> input{{{'B', 0U, 1U}, {'A', 1U, 1U}, {'B', 2U, 1U},
            {0x2007U, 3U, 1U}, {0x2008U, 4U, 1U}}};
        const std::array features{open_type_tag::from_chars('l', 'i', 'g', 'a'),
            open_type_tag::from_chars('k', 'e', 'r', 'n')};
        for (const auto direction : {shaping_direction::left_to_right, shaping_direction::right_to_left}) {
            open_type_shape_run_options shape{};
            shape.direction = direction; shape.script = open_type_tag::from_chars('l', 'a', 't', 'n');
            shape.requested_features = features;
            std::shared_ptr<const hinted_shaped_run> run;
            hinted_shape_error failure{};
            require(try_shape_context_hinted(context, 0U, configuration, input, shape, run, failure));
            require(run->source_descriptor_count == 5U && run->batch->glyphs.size() == 7U);
            auto values = source_metadata(*run);
            text_layout_options options{};
            options.direction = direction; options.maximum_width = 12.0F; options.line_height = 3.0F;
            std::shared_ptr<const hinted_text_layout> layout;
            require(fit(run, values, 0.75F, options, layout));
            compare_original_writer(*layout, values);
            const auto batch = run->batch;
            const auto saved = layout;
            options.maximum_width = 24.0F;
            require(fit(run, values, 0.75F, options, layout) && layout->run == run && run->batch == batch);
            compare_original_writer(*layout, values);
            require(saved->run == run && saved->options.maximum_width == 12.0F);
        }
        // Capture remains owned after its actual context/cache has retired.
        open_type_shape_run_options shape{}; shape.script = open_type_tag::from_chars('l', 'a', 't', 'n');
        shape.requested_features = features;
        std::shared_ptr<const hinted_shaped_run> run;
        hinted_shape_error failure{};
        require(try_shape_context_hinted(context, 0U, configuration, input, shape, run, failure));
        progpu_native_text_context_destroy(context); lifetime.value = nullptr;
        const auto values = source_metadata(*run);
        text_layout_options options{}; options.maximum_width = 20.0F;
        std::shared_ptr<const hinted_text_layout> layout;
        require(fit(run, values, 1.0F, options, layout));
        compare_original_writer(*layout, values);
    }
}
#endif
} // namespace

int main() {
    try {
        raw_controls(); justification_controls(); rejection_controls();
#if defined(PROGPU_NATIVE_FONT_HINTING)
        actual_generation_controls();
#endif
        std::cout << "native hinted generation fitting controls passed\n";
        return 0;
    } catch (const std::exception& failure) {
        std::cerr << failure.what() << '\n';
        return 1;
    }
}
