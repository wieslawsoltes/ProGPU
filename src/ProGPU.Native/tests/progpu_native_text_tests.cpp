#include "progpu_native_text.hpp"

#include <algorithm>
#include <array>
#include <bit>
#include <cstddef>
#include <cstdint>
#include <cstdlib>
#include <cstdio>
#include <fstream>
#include <iterator>
#include <limits>
#include <span>
#include <source_location>
#include <string_view>
#include <utility>
#include <vector>

namespace {

using progpu::native::text::font_error;
using progpu::native::text::open_type_tag;
using progpu::native::text::unicode_error;
using progpu::native::text::unicode_decode_requirements;
using progpu::native::text::unicode_scalar;
using progpu::native::text::unicode_bidi_class;
using progpu::native::text::unicode_bidi_bracket_kind;
using progpu::native::text::shaping_feature;
using progpu::native::text::shaping_glyph;
using progpu::native::text::shaping_glyph_flags;
using progpu::native::text::shaping_cluster_level;
using progpu::native::text::shaping_buffer_flags;
using progpu::native::text::get_unicode_script;
using progpu::native::text::get_unicode_arabic_joining_type;
using progpu::native::text::open_type_arabic_action;
using progpu::native::text::try_assign_open_type_arabic_actions;
using progpu::native::text::unicode_arabic_joining_type;
using progpu::native::text::get_unicode_canonical_combining_class;
using progpu::native::text::get_unicode_bidi_class;
using progpu::native::text::try_get_unicode_bidi_bracket;
using progpu::native::text::unicode_bidi_level;
using progpu::native::text::unicode_bidi_unit;
using progpu::native::text::unicode_bidi_level_run;
using progpu::native::text::unicode_bidi_bracket_pair;
using progpu::native::text::unicode_bidi_requirements;
using progpu::native::text::unicode_bidi_scratch;
using progpu::native::text::try_get_unicode_bidi_requirements;
using progpu::native::text::try_resolve_unicode_bidi;
using progpu::native::text::unicode_grapheme_break_class;
using progpu::native::text::unicode_indic_conjunct_class;
using progpu::native::text::unicode_grapheme_cluster;
using progpu::native::text::get_unicode_grapheme_break_class;
using progpu::native::text::get_unicode_indic_conjunct_class;
using progpu::native::text::is_unicode_extended_pictographic;
using progpu::native::text::try_get_unicode_grapheme_cluster_count;
using progpu::native::text::try_segment_unicode_graphemes;
using progpu::native::text::try_get_utf8_decode_requirements;
using progpu::native::text::try_decode_utf8;
using progpu::native::text::try_get_utf16_decode_requirements;
using progpu::native::text::try_decode_utf16;
using progpu::native::text::unicode_script_run;
using progpu::native::text::try_get_unicode_script_run_count;
using progpu::native::text::try_itemize_unicode_scripts;
using progpu::native::text::unicode_normalization_form;
using progpu::native::text::unicode_normalization_requirements;
using progpu::native::text::unicode_normalization_data;
using progpu::native::text::try_get_unicode_normalization_requirements;
using progpu::native::text::try_normalize_unicode;
using progpu::native::text::open_type_coverage_view;
using progpu::native::text::open_type_class_definition_view;
using progpu::native::text::open_type_lookup_view;
using progpu::native::text::open_type_layout_table_view;
using progpu::native::text::open_type_glyph_class;
using progpu::native::text::open_type_gdef_view;
using progpu::native::text::is_open_type_gdef_blocklisted;
using progpu::native::text::open_type_gsub_apply_options;
using progpu::native::text::try_apply_open_type_gsub_lookup;
using progpu::native::text::open_type_gpos_apply_options;
using progpu::native::text::shaping_attachment;
using progpu::native::text::shaping_attachment_kind;
using progpu::native::text::try_apply_open_type_gpos_lookup;
using progpu::native::text::try_resolve_open_type_attachments;
using progpu::native::text::open_type_shape_run_options;
using progpu::native::text::open_type_complex_script;
using progpu::native::text::open_type_shape_run_scratch;
using progpu::native::text::open_type_shape_run_requirements;
using progpu::native::text::try_get_open_type_shape_run_requirements;
using progpu::native::text::open_type_shape_plan_requirements;
using progpu::native::text::open_type_shape_plan;
using progpu::native::text::try_get_open_type_shape_plan_requirements;
using progpu::native::text::try_build_open_type_shape_plan;
using progpu::native::text::try_shape_open_type_run;
using progpu::native::text::try_prepare_open_type_hangul;
using progpu::native::text::font_fallback_candidate;
using progpu::native::text::font_fallback_run;
using progpu::native::text::try_get_font_fallback_run_count;
using progpu::native::text::try_itemize_font_fallback;
using progpu::native::text::font_provider_slant;
using progpu::native::text::font_provider_face;
using progpu::native::text::font_provider_view;
using progpu::native::text::font_provider_cache_entry;
using progpu::native::text::font_provider_result;
using progpu::native::text::try_resolve_font_provider_face;
using progpu::native::text::try_preprocess_open_type_glyphs;
using progpu::native::text::unicode_line_break_class;
using progpu::native::text::text_line_break_kind;
using progpu::native::text::get_unicode_line_break_class;
using progpu::native::text::try_resolve_unicode_line_breaks;
using progpu::native::text::text_trimming;
using progpu::native::text::unicode_indic_shaping_properties;
using progpu::native::text::unicode_syllable_machine;
using progpu::native::text::unicode_syllable_transition;
using progpu::native::text::get_unicode_indic_shaping_properties;
using progpu::native::text::get_unicode_use_shaping_category;
using progpu::native::text::get_unicode_syllable_machine_state_count;
using progpu::native::text::get_unicode_syllable_machine_start_state;
using progpu::native::text::try_get_unicode_syllable_transition;
using progpu::native::text::try_get_unicode_syllable_eof_transition;
using progpu::native::text::try_assign_unicode_syllables;
using progpu::native::text::text_layout_options;
using progpu::native::text::positioned_text_glyph;
using progpu::native::text::positioned_text_line;
using progpu::native::text::text_layout_requirements;
using progpu::native::text::try_get_text_layout_requirements;
using progpu::native::text::try_layout_shaped_text;
using progpu::native::text::text_cluster_box;
using progpu::native::text::text_caret_stop;
using progpu::native::text::text_rectangle;
using progpu::native::text::text_hit_test_result;
using progpu::native::text::text_interaction_requirements;
using progpu::native::text::try_get_text_interaction_requirements;
using progpu::native::text::try_build_text_interaction;
using progpu::native::text::try_hit_test_text;
using progpu::native::text::try_get_text_selection_rectangles;
using progpu::native::text::sfnt_container_requirements;
using progpu::native::text::try_get_sfnt_container_requirements;
using progpu::native::text::try_normalize_sfnt_container;
using progpu::native::text::sfnt_composite_component;
using progpu::native::text::sfnt_composite_glyph_decode_requirements;
using progpu::native::text::sfnt_composite_glyph_variation_requirements;
using progpu::native::text::sfnt_composite_glyph_variation_scratch;
using progpu::native::text::sfnt_cff_data;
using progpu::native::text::sfnt_cff_fd_select_view;
using progpu::native::text::sfnt_cff_index_view;
using progpu::native::text::sfnt_cff1_font_view;
using progpu::native::text::sfnt_cff1_outline_requirements;
using progpu::native::text::sfnt_cff1_top_dictionary;
using progpu::native::text::sfnt_cff2_font_view;
using progpu::native::text::sfnt_cff2_outline_requirements;
using progpu::native::text::sfnt_cff2_top_dictionary;
using progpu::native::text::sfnt_bitmap_glyph_data_view;
using progpu::native::text::sfnt_color_glyph_layer;
using progpu::native::text::sfnt_svg_glyph_document_view;
using progpu::native::text::svg_path_requirements;
using progpu::native::text::try_decode_svg_path;
using progpu::native::text::try_decode_svg_glyph_document;
using progpu::native::text::try_get_svg_glyph_document_size;
using progpu::native::text::try_get_svg_path_requirements;
using progpu::native::text::sfnt_expanded_glyph_requirements;
using progpu::native::text::sfnt_font_view;
using progpu::native::text::sfnt_glyph_data_view;
using progpu::native::text::sfnt_glyph_decode_requirements;
using progpu::native::text::sfnt_glyph_kind;
using progpu::native::text::sfnt_glyph_variation_data_view;
using progpu::native::text::sfnt_glyph_phantom_variation_requirements;
using progpu::native::text::sfnt_glyph_phantom_variation_scratch;
using progpu::native::text::sfnt_gvar_header;
using progpu::native::text::sfnt_gvar_deltas;
using progpu::native::text::sfnt_gvar_tuple_data;
using progpu::native::text::sfnt_gvar_tuple_header;
using progpu::native::text::sfnt_gvar_tuple_requirements;
using progpu::native::text::sfnt_header_metrics;
using progpu::native::text::sfnt_horizontal_glyph_metrics;
using progpu::native::text::sfnt_horizontal_header_metrics;
using progpu::native::text::sfnt_item_variation_data;
using progpu::native::text::sfnt_item_variation_store_view;
using progpu::native::text::sfnt_delta_set_index_map_view;
using progpu::native::text::sfnt_outline_point;
using progpu::native::text::sfnt_packed_delta_requirements;
using progpu::native::text::sfnt_packed_point_requirements;
using progpu::native::text::sfnt_packed_variation_data;
using progpu::native::text::sfnt_simple_glyph_path;
using progpu::native::text::sfnt_simple_glyph_variation_requirements;
using progpu::native::text::sfnt_simple_glyph_variation_scratch;
using progpu::native::text::sfnt_varied_glyph_requirements;
using progpu::native::text::sfnt_varied_glyph_scratch;
using progpu::native::text::sfnt_table_view;
using progpu::native::text::sfnt_variation_axis;

void require(
    bool condition,
    const std::source_location location =
        std::source_location::current()) {
    if (!condition) {
        std::fprintf(
            stderr,
            "require failed at %s:%u\n",
            location.file_name(),
            location.line());
        std::abort();
    }
}

void write_u16(
    std::span<std::byte> destination,
    std::size_t offset,
    std::uint16_t value);

void unicode_contract_and_strict_decoders_are_transactional() {
    static_assert(sizeof(shaping_feature) == 16U);
    static_assert(sizeof(shaping_glyph) == 32U);
    static_assert(sizeof(shaping_attachment) == 8U);
    static_assert(sizeof(unicode_scalar) == 16U);

    const shaping_feature feature{
        open_type_tag::from_chars('l', 'i', 'g', 'a'), 1U, 2U, 8U};
    require(!feature.applies_to(1U) && feature.applies_to(2U) &&
        feature.applies_to(7U) && !feature.applies_to(8U));
    const shaping_glyph glyph{
        42U, 0x41U, 3, shaping_glyph_flags::unsafe_to_break,
        600, 0, 4, -2};
    require(glyph.glyph_id == 42U && glyph.advance_x == 600 &&
        glyph.offset_y == -2);

    require(get_unicode_script(0x41U) ==
        open_type_tag::from_chars('l', 'a', 't', 'n'));
    require(get_unicode_arabic_joining_type(0x0627U) ==
        unicode_arabic_joining_type::right_joining);
    require(get_unicode_arabic_joining_type(0x0628U) ==
        unicode_arabic_joining_type::dual_joining);
    require(get_unicode_arabic_joining_type(0x064BU) ==
        unicode_arabic_joining_type::transparent);
    require(get_unicode_arabic_joining_type(0x200EU) ==
        unicode_arabic_joining_type::transparent);

    const std::array<unicode_scalar, 3U> arabic{
        unicode_scalar{0x0628U, 0U, 1U},
        unicode_scalar{0x064BU, 1U, 1U},
        unicode_scalar{0x0627U, 2U, 1U}};
    std::array<open_type_arabic_action, 3U> actions{};
    std::uint32_t action_count = 0U;
    unicode_error unicode_result = unicode_error::none;
    require(try_assign_open_type_arabic_actions(
        arabic, actions, action_count, &unicode_result));
    require(action_count == 3U &&
        actions[0U] == open_type_arabic_action::initial &&
        actions[1U] == open_type_arabic_action::none &&
        actions[2U] == open_type_arabic_action::final);
    require(get_unicode_bidi_class(0x41U) ==
        unicode_bidi_class::left_to_right);
    require(get_unicode_bidi_class(0x05D0U) ==
        unicode_bidi_class::right_to_left);
    require(get_unicode_bidi_class(0x0627U) ==
        unicode_bidi_class::arabic_letter);
    std::uint32_t bracket_pair = 0U;
    unicode_bidi_bracket_kind bracket_kind =
        unicode_bidi_bracket_kind::none;
    require(try_get_unicode_bidi_bracket(
        0x28U, bracket_pair, bracket_kind));
    require(bracket_pair == 0x29U &&
        bracket_kind == unicode_bidi_bracket_kind::open);
    require(get_unicode_script(0x3042U) ==
        open_type_tag::from_chars('k', 'a', 'n', 'a'));
    require(get_unicode_script(0x0E81U) ==
        open_type_tag::from_chars('l', 'a', 'o', ' '));
    require(get_unicode_script(0x0301U) ==
        open_type_tag::from_chars('D', 'F', 'L', 'T'));
    require(get_unicode_canonical_combining_class(0x0301U) == 230U);
    require(get_unicode_canonical_combining_class(0x41U) == 0U);

    constexpr std::array<std::byte, 7U> utf8{
        std::byte{0x41U},
        std::byte{0xCCU}, std::byte{0x81U},
        std::byte{0xF0U}, std::byte{0x9FU},
        std::byte{0x98U}, std::byte{0x80U}};
    unicode_decode_requirements requirements{};
    unicode_error error = unicode_error::invalid_argument;
    require(try_get_utf8_decode_requirements(utf8, requirements, &error));
    require(error == unicode_error::none && requirements.scalar_count == 3U);
    std::array<unicode_scalar, 3U> scalars{};
    std::uint32_t written = 99U;
    require(try_decode_utf8(utf8, scalars, written, &error));
    require(written == 3U && scalars[0U].code_point == 0x41U &&
        scalars[0U].input_index == 0U && scalars[0U].input_length == 1U &&
        scalars[1U].code_point == 0x0301U &&
        scalars[1U].input_index == 1U && scalars[1U].input_length == 2U &&
        scalars[1U].canonical_combining_class == 230U &&
        scalars[2U].code_point == 0x1F600U &&
        scalars[2U].input_index == 3U && scalars[2U].input_length == 4U);

    constexpr std::array<std::uint16_t, 4U> utf16{
        0x0041U, 0xD83DU, 0xDE00U, 0x0E81U};
    require(try_get_utf16_decode_requirements(utf16, requirements, &error));
    require(requirements.scalar_count == 3U);
    std::array<unicode_scalar, 3U> utf16_scalars{};
    require(try_decode_utf16(utf16, utf16_scalars, written, &error));
    require(written == 3U && utf16_scalars[1U].code_point == 0x1F600U &&
        utf16_scalars[1U].input_index == 1U &&
        utf16_scalars[1U].input_length == 2U &&
        utf16_scalars[2U].script ==
            open_type_tag::from_chars('l', 'a', 'o', ' '));

    constexpr std::array<std::byte, 2U> overlong{
        std::byte{0xC0U}, std::byte{0x80U}};
    std::array<unicode_scalar, 3U> untouched{
        unicode_scalar{99U}, unicode_scalar{99U}, unicode_scalar{99U}};
    require(!try_decode_utf8(overlong, untouched, written, &error));
    require(error == unicode_error::invalid_encoding && written == 0U &&
        untouched[0U].code_point == 99U);
    require(!try_decode_utf8(utf8, std::span<unicode_scalar>{untouched}.first(2U),
        written, &error));
    require(error == unicode_error::insufficient_buffer && written == 0U &&
        untouched[0U].code_point == 99U);

    constexpr std::array<std::uint16_t, 1U> lone_high{0xD800U};
    require(!try_decode_utf16(lone_high, untouched, written, &error));
    require(error == unicode_error::invalid_encoding && written == 0U &&
        untouched[0U].code_point == 99U);
}

void write_u24(
    std::span<std::byte> destination,
    std::size_t offset,
    std::uint32_t value) {
    destination[offset] = static_cast<std::byte>((value >> 16U) & 0xFFU);
    destination[offset + 1U] =
        static_cast<std::byte>((value >> 8U) & 0xFFU);
    destination[offset + 2U] = static_cast<std::byte>(value & 0xFFU);
}

void unicode_bidi_resolution_is_bounded_and_source_preserving() {
    static_assert(sizeof(unicode_bidi_level) == 8U);
    static_assert(sizeof(unicode_bidi_unit) == 20U);
    static_assert(sizeof(unicode_bidi_level_run) == 16U);
    static_assert(sizeof(unicode_bidi_bracket_pair) == 8U);

    std::array<unicode_scalar, 7U> input{};
    constexpr std::array<std::uint32_t, 7U> code_points{
        0x61U, 0x62U, 0x63U, 0x20U, 0x05D0U, 0x05D1U, 0x05D2U};
    for (std::size_t index = 0U; index < input.size(); ++index) {
        input[index].code_point = code_points[index];
        input[index].input_index = static_cast<std::uint32_t>(index);
        input[index].input_length = 1U;
    }
    unicode_bidi_requirements requirements{};
    unicode_error error = unicode_error::invalid_argument;
    require(try_get_unicode_bidi_requirements(input, requirements, &error));
    require(requirements.unit_count == 7U && requirements.index_count == 28U &&
        requirements.run_count == 7U && requirements.bracket_pair_count == 3U);
    std::array<unicode_bidi_unit, 7U> units{};
    std::array<std::uint32_t, 28U> indices{};
    std::array<unicode_bidi_level_run, 7U> runs{};
    std::array<unicode_bidi_bracket_pair, 3U> pairs{};
    std::array<unicode_bidi_level, 7U> levels{};
    std::int8_t paragraph = -1;
    std::uint32_t written = 99U;
    require(try_resolve_unicode_bidi(
        input,
        -1,
        unicode_bidi_scratch{units, indices, runs, pairs},
        levels,
        paragraph,
        written,
        &error));
    require(paragraph == 0 && written == 7U);
    for (std::size_t index = 0U; index < 4U; ++index) {
        require(levels[index].level == 0 &&
            levels[index].input_index == index &&
            levels[index].input_length == 1U);
    }
    for (std::size_t index = 4U; index < levels.size(); ++index) {
        require(levels[index].level == 1);
    }
    require(try_resolve_unicode_bidi(
        input,
        1,
        unicode_bidi_scratch{units, indices, runs, pairs},
        levels,
        paragraph,
        written,
        &error));
    require(paragraph == 1 && levels[0U].level == 2 &&
        levels[1U].level == 2 && levels[2U].level == 2 &&
        levels[3U].level == 1 && levels[4U].level == 1 &&
        levels[5U].level == 1 && levels[6U].level == 1);

    std::array<unicode_scalar, 3U> explicit_input{};
    explicit_input[0U] = unicode_scalar{0x202EU, 0U, 1U};
    explicit_input[1U] = unicode_scalar{0x41U, 1U, 1U};
    explicit_input[2U] = unicode_scalar{0x202CU, 2U, 1U};
    std::array<unicode_bidi_unit, 3U> explicit_units{};
    std::array<std::uint32_t, 12U> explicit_indices{};
    std::array<unicode_bidi_level_run, 3U> explicit_runs{};
    std::array<unicode_bidi_bracket_pair, 1U> explicit_pairs{};
    std::array<unicode_bidi_level, 3U> explicit_levels{};
    require(try_resolve_unicode_bidi(
        explicit_input,
        0,
        unicode_bidi_scratch{
            explicit_units, explicit_indices, explicit_runs, explicit_pairs},
        explicit_levels,
        paragraph,
        written,
        &error));
    require(paragraph == 0 && written == 3U &&
        explicit_levels[0U].level == 0 &&
        explicit_levels[1U].level == 1 &&
        explicit_levels[2U].level == 1);

    levels.fill(unicode_bidi_level{99U, 99U, 99, 0U});
    require(!try_resolve_unicode_bidi(
        input,
        -1,
        unicode_bidi_scratch{
            units, std::span<std::uint32_t>{indices}.first(27U), runs, pairs},
        levels,
        paragraph,
        written,
        &error));
    require(error == unicode_error::insufficient_buffer && written == 0U &&
        levels[0U].input_index == 99U);
}

void unicode_grapheme_segmentation_covers_extended_rules() {
    static_assert(sizeof(unicode_grapheme_cluster) == 16U);
    require(get_unicode_grapheme_break_class(0x0301U) ==
        unicode_grapheme_break_class::extend);
    require(get_unicode_grapheme_break_class(0x1F1FAU) ==
        unicode_grapheme_break_class::regional_indicator);
    require(get_unicode_indic_conjunct_class(0x094DU) ==
        unicode_indic_conjunct_class::linker);
    require(is_unicode_extended_pictographic(0x1F469U));

    constexpr std::array<std::uint32_t, 15U> code_points{
        0x41U, 0x0301U, 0x42U,
        0x1F469U, 0x200DU, 0x1F469U,
        0x1F1FAU, 0x1F1F8U, 0x1F1E8U,
        0x1100U, 0x1161U, 0x11A8U,
        0x0915U, 0x094DU, 0x0937U};
    std::array<unicode_scalar, code_points.size()> input{};
    for (std::size_t index = 0U; index < input.size(); ++index) {
        input[index].code_point = code_points[index];
        input[index].input_index = static_cast<std::uint32_t>(index);
        input[index].input_length = 1U;
    }
    std::uint32_t count = 0U;
    unicode_error error = unicode_error::invalid_argument;
    require(try_get_unicode_grapheme_cluster_count(input, count, &error));
    require(count == 7U);
    std::array<unicode_grapheme_cluster, 7U> clusters{};
    std::uint32_t written = 99U;
    require(try_segment_unicode_graphemes(
        input, clusters, written, &error));
    require(written == 7U);
    require(clusters[0U].scalar_index == 0U &&
        clusters[0U].scalar_count == 2U && clusters[0U].input_length == 2U);
    require(clusters[1U].scalar_index == 2U &&
        clusters[1U].scalar_count == 1U);
    require(clusters[2U].scalar_index == 3U &&
        clusters[2U].scalar_count == 3U);
    require(clusters[3U].scalar_index == 6U &&
        clusters[3U].scalar_count == 2U);
    require(clusters[4U].scalar_index == 8U &&
        clusters[4U].scalar_count == 1U);
    require(clusters[5U].scalar_index == 9U &&
        clusters[5U].scalar_count == 3U);
    require(clusters[6U].scalar_index == 12U &&
        clusters[6U].scalar_count == 3U);

    clusters.fill(unicode_grapheme_cluster{99U, 99U, 99U, 99U});
    require(!try_segment_unicode_graphemes(
        input,
        std::span<unicode_grapheme_cluster>{clusters}.first(6U),
        written,
        &error));
    require(error == unicode_error::insufficient_buffer && written == 0U &&
        clusters[0U].input_index == 99U);
}

void canonical_unicode_normalization_uses_shared_borrowed_data() {
    std::ifstream stream(
        PROGPU_NATIVE_TEST_UNICODE_NORMALIZATION_DATA,
        std::ios::binary);
    require(stream.good());
    const std::vector<char> source{
        std::istreambuf_iterator<char>(stream),
        std::istreambuf_iterator<char>()};
    std::vector<std::byte> bytes(source.size());
    for (std::size_t index = 0U; index < source.size(); ++index) {
        bytes[index] = static_cast<std::byte>(source[index]);
    }

    unicode_normalization_data data{};
    unicode_error error = unicode_error::invalid_argument;
    require(unicode_normalization_data::try_create(bytes, data, &error));
    require(error == unicode_error::none);
    std::span<const std::byte> decomposition{};
    require(data.try_get_decomposition(0x01FAU, decomposition));
    require(decomposition.size() == 12U);
    std::uint32_t composed = 0U;
    require(data.try_compose(0x0041U, 0x030AU, composed));
    require(composed == 0x00C5U);

    constexpr std::array<unicode_scalar, 1U> precomposed{
        unicode_scalar{
            0x01FAU,
            4U,
            1U,
            0U,
            0U,
            open_type_tag::from_chars('l', 'a', 't', 'n')}};
    unicode_normalization_requirements requirements{};
    require(try_get_unicode_normalization_requirements(
        precomposed, data, requirements, &error));
    require(requirements.scalar_capacity == 3U);
    std::array<unicode_scalar, 3U> normalized{};
    std::uint32_t written = 99U;
    require(try_normalize_unicode(
        precomposed,
        data,
        unicode_normalization_form::canonical_decomposition,
        normalized,
        written,
        &error));
    require(written == 3U && normalized[0U].code_point == 0x0041U &&
        normalized[1U].code_point == 0x030AU &&
        normalized[2U].code_point == 0x0301U &&
        normalized[0U].input_index == 4U &&
        normalized[2U].input_index == 4U);
    require(try_normalize_unicode(
        precomposed,
        data,
        unicode_normalization_form::canonical_composition,
        normalized,
        written,
        &error));
    require(written == 1U && normalized[0U].code_point == 0x01FAU);

    constexpr std::array<unicode_scalar, 3U> unordered{
        unicode_scalar{0x0041U, 0U, 1U, 0U, 0U,
            open_type_tag::from_chars('l', 'a', 't', 'n')},
        unicode_scalar{0x0301U, 1U, 2U, 230U, 0U,
            open_type_tag::from_chars('D', 'F', 'L', 'T')},
        unicode_scalar{0x0323U, 3U, 2U, 220U, 0U,
            open_type_tag::from_chars('D', 'F', 'L', 'T')}};
    require(try_normalize_unicode(
        unordered,
        data,
        unicode_normalization_form::canonical_decomposition,
        normalized,
        written,
        &error));
    require(written == 3U && normalized[1U].code_point == 0x0323U &&
        normalized[2U].code_point == 0x0301U);
    require(try_normalize_unicode(
        unordered,
        data,
        unicode_normalization_form::canonical_composition,
        normalized,
        written,
        &error));
    require(written == 2U && normalized[0U].code_point == 0x1EA0U &&
        normalized[1U].code_point == 0x0301U &&
        normalized[0U].input_index == 0U &&
        normalized[0U].input_length == 5U);

    std::array<unicode_scalar, 3U> untouched{
        unicode_scalar{99U}, unicode_scalar{99U}, unicode_scalar{99U}};
    require(!try_normalize_unicode(
        precomposed,
        data,
        unicode_normalization_form::canonical_decomposition,
        std::span<unicode_scalar>{untouched}.first(2U),
        written,
        &error));
    require(error == unicode_error::insufficient_buffer && written == 0U &&
        untouched[0U].code_point == 99U);

    auto malformed = bytes;
    malformed[0U] = std::byte{0U};
    unicode_normalization_data invalid{};
    require(!unicode_normalization_data::try_create(
        malformed, invalid, &error));
    require(error == unicode_error::invalid_argument);
}

void unicode_script_itemization_preserves_source_ranges() {
    constexpr auto dflt = open_type_tag::from_chars('D', 'F', 'L', 'T');
    constexpr auto latn = open_type_tag::from_chars('l', 'a', 't', 'n');
    constexpr auto arab = open_type_tag::from_chars('a', 'r', 'a', 'b');
    constexpr std::array<unicode_scalar, 6U> scalars{
        unicode_scalar{0x0022U, 0U, 1U, 0U, 0U, dflt},
        unicode_scalar{0x0041U, 1U, 1U, 0U, 0U, latn},
        unicode_scalar{0x0020U, 2U, 1U, 0U, 0U, dflt},
        unicode_scalar{0x0627U, 3U, 2U, 0U, 0U, arab},
        unicode_scalar{0x0651U, 5U, 2U, 33U, 0U, dflt},
        unicode_scalar{0x002EU, 7U, 1U, 0U, 0U, dflt}};
    unicode_error error = unicode_error::invalid_argument;
    std::uint32_t count = 99U;
    require(try_get_unicode_script_run_count(scalars, count, &error));
    require(error == unicode_error::none && count == 2U);
    std::array<unicode_script_run, 2U> runs{};
    std::uint32_t written = 99U;
    require(try_itemize_unicode_scripts(scalars, runs, written, &error));
    require(written == 2U && runs[0U].scalar_start == 0U &&
        runs[0U].scalar_count == 3U && runs[0U].input_start == 0U &&
        runs[0U].input_length == 3U && runs[0U].script == latn &&
        runs[1U].scalar_start == 3U && runs[1U].scalar_count == 3U &&
        runs[1U].input_start == 3U && runs[1U].input_length == 5U &&
        runs[1U].script == arab);

    std::array<unicode_script_run, 2U> untouched{
        unicode_script_run{99U}, unicode_script_run{99U}};
    require(!try_itemize_unicode_scripts(
        scalars,
        std::span<unicode_script_run>{untouched}.first(1U),
        written,
        &error));
    require(error == unicode_error::insufficient_buffer && written == 0U &&
        untouched[0U].scalar_start == 99U);

    constexpr std::array<unicode_scalar, 2U> common{
        unicode_scalar{0x1F600U, 0U, 2U, 0U, 0U, dflt},
        unicode_scalar{0x0021U, 2U, 1U, 0U, 0U, dflt}};
    require(try_itemize_unicode_scripts(common, runs, written, &error));
    require(written == 1U && runs[0U].script == dflt &&
        runs[0U].input_length == 3U);
}

void open_type_common_layout_views_are_borrowed_and_bounded() {
    std::array<std::byte, 10U> coverage1{};
    write_u16(coverage1, 0U, 1U);
    write_u16(coverage1, 2U, 3U);
    write_u16(coverage1, 4U, 3U);
    write_u16(coverage1, 6U, 5U);
    write_u16(coverage1, 8U, 9U);
    open_type_coverage_view coverage{};
    font_error error = font_error::invalid_argument;
    require(open_type_coverage_view::try_create(
        coverage1, 0U, coverage, &error));
    require(error == font_error::none && coverage.find(3U) == 0 &&
        coverage.find(5U) == 1 && coverage.find(9U) == 2 &&
        coverage.find(4U) == -1);

    std::array<std::byte, 16U> coverage2{};
    write_u16(coverage2, 0U, 2U);
    write_u16(coverage2, 2U, 2U);
    write_u16(coverage2, 4U, 10U);
    write_u16(coverage2, 6U, 12U);
    write_u16(coverage2, 8U, 0U);
    write_u16(coverage2, 10U, 20U);
    write_u16(coverage2, 12U, 20U);
    write_u16(coverage2, 14U, 3U);
    require(open_type_coverage_view::try_create(
        coverage2, 0U, coverage, &error));
    require(coverage.find(10U) == 0 && coverage.find(12U) == 2 &&
        coverage.find(20U) == 3 && coverage.find(13U) == -1);

    std::array<std::byte, 12U> class1{};
    write_u16(class1, 0U, 1U);
    write_u16(class1, 2U, 5U);
    write_u16(class1, 4U, 3U);
    write_u16(class1, 6U, 1U);
    write_u16(class1, 8U, 2U);
    write_u16(class1, 10U, 2U);
    open_type_class_definition_view classes{};
    require(open_type_class_definition_view::try_create(
        class1, 0U, classes, &error));
    require(classes.get(4U) == 0U && classes.get(5U) == 1U &&
        classes.get(7U) == 2U && classes.get(8U) == 0U);

    std::array<std::byte, 16U> class2{};
    write_u16(class2, 0U, 2U);
    write_u16(class2, 2U, 2U);
    write_u16(class2, 4U, 10U);
    write_u16(class2, 6U, 12U);
    write_u16(class2, 8U, 4U);
    write_u16(class2, 10U, 20U);
    write_u16(class2, 12U, 21U);
    write_u16(class2, 14U, 7U);
    require(open_type_class_definition_view::try_create(
        class2, 0U, classes, &error));
    require(classes.get(11U) == 4U && classes.get(20U) == 7U &&
        classes.get(30U) == 0U);

    std::array<std::byte, 30U> layout{};
    write_u16(layout, 0U, 1U);
    write_u16(layout, 2U, 0U);
    write_u16(layout, 4U, 10U);
    write_u16(layout, 6U, 12U);
    write_u16(layout, 8U, 14U);
    write_u16(layout, 10U, 0U);
    write_u16(layout, 12U, 0U);
    write_u16(layout, 14U, 1U);
    write_u16(layout, 16U, 4U);
    write_u16(layout, 18U, 1U);
    write_u16(layout, 20U, 0x0010U);
    write_u16(layout, 22U, 1U);
    write_u16(layout, 24U, 10U);
    write_u16(layout, 26U, 7U);
    write_u16(layout, 28U, 1U);
    open_type_layout_table_view table{};
    require(open_type_layout_table_view::try_create(
        layout, table, &error));
    require(error == font_error::none && table.lookup_count() == 1U);
    open_type_lookup_view lookup{};
    require(table.try_get_lookup(0U, lookup, &error));
    require(lookup.type == 1U && lookup.flags == 0x0010U &&
        lookup.subtable_count == 1U && lookup.mark_filtering_set == 7U);
    std::size_t subtable = 0U;
    require(lookup.try_get_subtable(0U, subtable, &error));
    require(subtable == 28U);

    auto malformed = coverage1;
    write_u16(malformed, 6U, 2U);
    require(!open_type_coverage_view::try_create(
        malformed, 0U, coverage, &error));
    require(error == font_error::invalid_face && coverage.find(3U) == -1);
    auto malformed_layout = layout;
    write_u16(malformed_layout, 24U, 0U);
    require(open_type_layout_table_view::try_create(
        malformed_layout, table, &error));
    require(!table.try_get_lookup(0U, lookup, &error));
    require(error == font_error::invalid_face && lookup.table.empty());
}

void open_type_gdef_classes_and_mark_sets_are_borrowed_and_bounded() {
    // GDEF 1.2: class format 1 at 14, mark-attachment range class at 24,
    // and two MarkGlyphSets coverages at 40 and 48.
    std::array<std::byte, 60U> table{};
    write_u16(table, 0U, 1U);
    write_u16(table, 2U, 2U);
    write_u16(table, 4U, 14U);
    write_u16(table, 10U, 24U);
    write_u16(table, 12U, 34U);
    write_u16(table, 14U, 1U);
    write_u16(table, 16U, 10U);
    write_u16(table, 18U, 2U);
    write_u16(table, 20U, 1U);
    write_u16(table, 22U, 3U);
    write_u16(table, 24U, 2U);
    write_u16(table, 26U, 1U);
    write_u16(table, 28U, 20U);
    write_u16(table, 30U, 21U);
    write_u16(table, 32U, 7U);
    write_u16(table, 34U, 1U);
    write_u16(table, 36U, 2U);
    table[41U] = std::byte{12U};
    table[45U] = std::byte{20U};
    write_u16(table, 46U, 1U);
    write_u16(table, 48U, 1U);
    write_u16(table, 50U, 10U);
    write_u16(table, 52U, 0U);
    write_u16(table, 54U, 1U);
    write_u16(table, 56U, 1U);
    write_u16(table, 58U, 11U);

    open_type_gdef_view gdef{};
    font_error error = font_error::invalid_argument;
    require(open_type_gdef_view::try_create(table, gdef, &error));
    require(error == font_error::none &&
        gdef.glyph_class(10U) == open_type_glyph_class::base &&
        gdef.glyph_class(11U) == open_type_glyph_class::mark &&
        gdef.glyph_class(12U) == open_type_glyph_class::unclassified);
    require(gdef.mark_attachment_class(20U) == 7U &&
        gdef.mark_attachment_class(22U) == 0U);
    require(gdef.mark_set_count() == 2U &&
        gdef.is_in_mark_set(0U, 10U) &&
        gdef.is_in_mark_set(1U, 11U) &&
        !gdef.is_in_mark_set(0U, 11U) &&
        !gdef.is_in_mark_set(2U, 10U));
    require(is_open_type_gdef_blocklisted(442U, 2874U, 42038U));
    require(!is_open_type_gdef_blocklisted(442U, 2874U, 42039U));

    auto malformed = table;
    write_u16(malformed, 54U, 10U);
    require(!open_type_gdef_view::try_create(malformed, gdef, &error));
    require(error == font_error::invalid_face && gdef.mark_set_count() == 0U);
}

void open_type_gsub_basic_lookups_use_caller_owned_storage() {
    font_error error = font_error::invalid_argument;
    bool applied = false;

    // One SingleSubst format 2 lookup: glyph 5 -> 9.
    std::array<std::byte, 42U> single{};
    write_u16(single, 0U, 1U);
    write_u16(single, 4U, 10U);
    write_u16(single, 6U, 12U);
    write_u16(single, 8U, 14U);
    write_u16(single, 14U, 1U);
    write_u16(single, 16U, 4U);
    write_u16(single, 18U, 1U);
    write_u16(single, 22U, 1U);
    write_u16(single, 24U, 8U);
    write_u16(single, 26U, 2U);
    write_u16(single, 28U, 10U);
    write_u16(single, 30U, 1U);
    write_u16(single, 32U, 9U);
    write_u16(single, 36U, 1U);
    write_u16(single, 38U, 1U);
    write_u16(single, 40U, 5U);
    open_type_layout_table_view gsub{};
    require(open_type_layout_table_view::try_create(single, gsub, &error));
    std::array<shaping_glyph, 6U> glyphs{
        shaping_glyph{5U, 0x66U, 0}, shaping_glyph{7U, 0x78U, 1}};
    std::uint32_t count = 2U;
    require(try_apply_open_type_gsub_lookup(
        gsub, 0U, glyphs, count, {}, applied, &error));
    require(applied && count == 2U && glyphs[0U].glyph_id == 9U &&
        glyphs[1U].glyph_id == 7U);

    glyphs = {shaping_glyph{5U, 0x66U, 0}};
    count = 1U;
    open_type_gsub_apply_options gated{};
    gated.required_glyph_flags = 1U << 21U;
    gated.mark_substituted = true;
    require(try_apply_open_type_gsub_lookup(
        gsub, 0U, glyphs, count, gated, applied, &error));
    require(!applied && glyphs[0U].glyph_id == 5U);
    glyphs[0U].flags = static_cast<shaping_glyph_flags>(1U << 21U);
    require(try_apply_open_type_gsub_lookup(
        gsub, 0U, glyphs, count, gated, applied, &error));
    require(applied && glyphs[0U].glyph_id == 9U &&
        (static_cast<std::uint32_t>(glyphs[0U].flags) & 0x80000000U) != 0U);

    // One MultipleSubst: glyph 5 -> [8, 9], preserving source metadata.
    std::array<std::byte, 46U> multiple{};
    write_u16(multiple, 0U, 1U);
    write_u16(multiple, 4U, 10U);
    write_u16(multiple, 6U, 12U);
    write_u16(multiple, 8U, 14U);
    write_u16(multiple, 14U, 1U);
    write_u16(multiple, 16U, 4U);
    write_u16(multiple, 18U, 2U);
    write_u16(multiple, 22U, 1U);
    write_u16(multiple, 24U, 8U);
    write_u16(multiple, 26U, 1U);
    write_u16(multiple, 28U, 8U);
    write_u16(multiple, 30U, 1U);
    write_u16(multiple, 32U, 14U);
    write_u16(multiple, 34U, 1U);
    write_u16(multiple, 36U, 1U);
    write_u16(multiple, 38U, 5U);
    write_u16(multiple, 40U, 2U);
    write_u16(multiple, 42U, 8U);
    write_u16(multiple, 44U, 9U);
    require(open_type_layout_table_view::try_create(multiple, gsub, &error));
    glyphs = {shaping_glyph{5U, 0x66U, 12},
        shaping_glyph{7U, 0x78U, 13}};
    count = 2U;
    require(try_apply_open_type_gsub_lookup(
        gsub, 0U, glyphs, count, {}, applied, &error));
    require(applied && count == 3U && glyphs[0U].glyph_id == 8U &&
        glyphs[1U].glyph_id == 9U && glyphs[1U].cluster == 12 &&
        glyphs[2U].glyph_id == 7U);

    auto insufficient = glyphs;
    count = 2U;
    insufficient[0U] = shaping_glyph{5U, 0x66U, 12};
    insufficient[1U] = shaping_glyph{7U, 0x78U, 13};
    require(!try_apply_open_type_gsub_lookup(
        gsub,
        0U,
        std::span<shaping_glyph>{insufficient}.first(2U),
        count,
        {},
        applied,
        &error));
    require(error == font_error::insufficient_buffer && count == 2U &&
        insufficient[0U].glyph_id == 5U && insufficient[1U].glyph_id == 7U);

    // One AlternateSubst: value 2 selects the second member.
    multiple[18U] = std::byte{0U};
    multiple[19U] = std::byte{3U};
    require(open_type_layout_table_view::try_create(multiple, gsub, &error));
    glyphs[0U] = shaping_glyph{5U, 0x66U, 2};
    count = 1U;
    open_type_gsub_apply_options alternate{};
    alternate.alternate_value = 2U;
    require(try_apply_open_type_gsub_lookup(
        gsub, 0U, glyphs, count, alternate, applied, &error));
    require(applied && count == 1U && glyphs[0U].glyph_id == 9U);

    // Ligature lookup ignores a GDEF mark between matching components.
    std::array<std::byte, 50U> ligature{};
    write_u16(ligature, 0U, 1U);
    write_u16(ligature, 4U, 10U);
    write_u16(ligature, 6U, 12U);
    write_u16(ligature, 8U, 14U);
    write_u16(ligature, 14U, 1U);
    write_u16(ligature, 16U, 4U);
    write_u16(ligature, 18U, 4U);
    write_u16(ligature, 20U, 0x0008U);
    write_u16(ligature, 22U, 1U);
    write_u16(ligature, 24U, 8U);
    write_u16(ligature, 26U, 1U);
    write_u16(ligature, 28U, 8U);
    write_u16(ligature, 30U, 1U);
    write_u16(ligature, 32U, 14U);
    write_u16(ligature, 34U, 1U);
    write_u16(ligature, 36U, 1U);
    write_u16(ligature, 38U, 5U);
    write_u16(ligature, 40U, 1U);
    write_u16(ligature, 42U, 4U);
    write_u16(ligature, 44U, 12U);
    write_u16(ligature, 46U, 2U);
    write_u16(ligature, 48U, 6U);
    std::array<std::byte, 24U> gdef_bytes{};
    write_u16(gdef_bytes, 0U, 1U);
    write_u16(gdef_bytes, 4U, 12U);
    write_u16(gdef_bytes, 12U, 1U);
    write_u16(gdef_bytes, 14U, 10U);
    write_u16(gdef_bytes, 16U, 2U);
    write_u16(gdef_bytes, 18U, 1U);
    write_u16(gdef_bytes, 20U, 3U);
    open_type_gdef_view gdef{};
    require(open_type_gdef_view::try_create(gdef_bytes, gdef, &error));
    require(open_type_layout_table_view::try_create(ligature, gsub, &error));
    glyphs = {shaping_glyph{5U, 0x66U, 0},
        shaping_glyph{11U, 0x0301U, 0},
        shaping_glyph{6U, 0x69U, 1}};
    count = 3U;
    open_type_gsub_apply_options ligature_options{};
    ligature_options.gdef = &gdef;
    require(try_apply_open_type_gsub_lookup(
        gsub, 0U, glyphs, count, ligature_options, applied, &error));
    require(applied && count == 2U && glyphs[0U].glyph_id == 12U &&
        glyphs[1U].glyph_id == 11U);

    auto malformed = single;
    write_u16(malformed, 28U, 0xFFFFU);
    require(open_type_layout_table_view::try_create(malformed, gsub, &error));
    glyphs[0U] = shaping_glyph{5U};
    count = 1U;
    require(!try_apply_open_type_gsub_lookup(
        gsub, 0U, glyphs, count, {}, applied, &error));
    require(error == font_error::invalid_face && glyphs[0U].glyph_id == 5U);
}

void open_type_gsub_reverse_chaining_matches_bounded_context() {
    std::array<std::byte, 64U> reverse{};
    write_u16(reverse, 0U, 1U);
    write_u16(reverse, 4U, 10U);
    write_u16(reverse, 6U, 12U);
    write_u16(reverse, 8U, 14U);
    write_u16(reverse, 14U, 1U);
    write_u16(reverse, 16U, 4U);
    write_u16(reverse, 18U, 8U);
    write_u16(reverse, 22U, 1U);
    write_u16(reverse, 24U, 8U);
    write_u16(reverse, 26U, 1U);
    write_u16(reverse, 28U, 20U);
    write_u16(reverse, 30U, 1U);
    write_u16(reverse, 32U, 26U);
    write_u16(reverse, 34U, 1U);
    write_u16(reverse, 36U, 32U);
    write_u16(reverse, 38U, 1U);
    write_u16(reverse, 40U, 12U);
    write_u16(reverse, 46U, 1U);
    write_u16(reverse, 48U, 1U);
    write_u16(reverse, 50U, 5U);
    write_u16(reverse, 52U, 1U);
    write_u16(reverse, 54U, 1U);
    write_u16(reverse, 56U, 1U);
    write_u16(reverse, 58U, 1U);
    write_u16(reverse, 60U, 1U);
    write_u16(reverse, 62U, 7U);

    open_type_layout_table_view gsub{};
    font_error error = font_error::invalid_argument;
    require(open_type_layout_table_view::try_create(reverse, gsub, &error));
    std::array<shaping_glyph, 4U> glyphs{
        shaping_glyph{1U}, shaping_glyph{5U}, shaping_glyph{7U}};
    std::uint32_t count = 3U;
    bool applied = false;
    require(try_apply_open_type_gsub_lookup(
        gsub, 0U, glyphs, count, {}, applied, &error));
    require(applied && count == 3U && glyphs[0U].glyph_id == 1U &&
        glyphs[1U].glyph_id == 12U && glyphs[2U].glyph_id == 7U);

    glyphs = {shaping_glyph{2U}, shaping_glyph{5U}, shaping_glyph{7U}};
    count = 3U;
    require(try_apply_open_type_gsub_lookup(
        gsub, 0U, glyphs, count, {}, applied, &error));
    require(!applied && glyphs[1U].glyph_id == 5U);

    auto malformed = reverse;
    write_u16(malformed, 36U, 0xFFFFU);
    require(open_type_layout_table_view::try_create(malformed, gsub, &error));
    glyphs = {shaping_glyph{1U}, shaping_glyph{5U}, shaping_glyph{7U}};
    count = 3U;
    require(!try_apply_open_type_gsub_lookup(
        gsub, 0U, glyphs, count, {}, applied, &error));
    require(error == font_error::invalid_face && glyphs[1U].glyph_id == 5U);
}

void open_type_gsub_context_format3_applies_bounded_nested_lookups() {
    std::array<std::byte, 74U> context{};
    write_u16(context, 0U, 1U);
    write_u16(context, 4U, 10U);
    write_u16(context, 6U, 12U);
    write_u16(context, 8U, 14U);
    write_u16(context, 14U, 2U);
    write_u16(context, 16U, 6U);
    write_u16(context, 18U, 40U);
    write_u16(context, 20U, 5U);
    write_u16(context, 24U, 1U);
    write_u16(context, 26U, 8U);
    write_u16(context, 28U, 3U);
    write_u16(context, 30U, 2U);
    write_u16(context, 32U, 1U);
    write_u16(context, 34U, 14U);
    write_u16(context, 36U, 20U);
    write_u16(context, 38U, 1U);
    write_u16(context, 40U, 1U);
    write_u16(context, 42U, 1U);
    write_u16(context, 44U, 1U);
    write_u16(context, 46U, 5U);
    write_u16(context, 48U, 1U);
    write_u16(context, 50U, 1U);
    write_u16(context, 52U, 6U);
    write_u16(context, 54U, 1U);
    write_u16(context, 58U, 1U);
    write_u16(context, 60U, 8U);
    write_u16(context, 62U, 1U);
    write_u16(context, 64U, 6U);
    write_u16(context, 66U, 6U);
    write_u16(context, 68U, 1U);
    write_u16(context, 70U, 1U);
    write_u16(context, 72U, 6U);

    open_type_layout_table_view gsub{};
    font_error error = font_error::invalid_argument;
    require(open_type_layout_table_view::try_create(context, gsub, &error));
    std::array<shaping_glyph, 4U> glyphs{
        shaping_glyph{5U}, shaping_glyph{6U}};
    std::uint32_t count = 2U;
    bool applied = false;
    require(try_apply_open_type_gsub_lookup(
        gsub, 0U, glyphs, count, {}, applied, &error));
    require(applied && count == 2U && glyphs[0U].glyph_id == 5U &&
        glyphs[1U].glyph_id == 12U &&
        (static_cast<std::uint32_t>(glyphs[0U].flags) & 3U) == 0U &&
        (static_cast<std::uint32_t>(glyphs[1U].flags) & 3U) == 0U);

    std::array<std::byte, 86U> chaining{};
    write_u16(chaining, 0U, 1U);
    write_u16(chaining, 4U, 10U);
    write_u16(chaining, 6U, 12U);
    write_u16(chaining, 8U, 14U);
    write_u16(chaining, 14U, 2U);
    write_u16(chaining, 16U, 6U);
    write_u16(chaining, 18U, 52U);
    write_u16(chaining, 20U, 6U);
    write_u16(chaining, 24U, 1U);
    write_u16(chaining, 26U, 8U);
    write_u16(chaining, 28U, 3U);
    write_u16(chaining, 30U, 1U);
    write_u16(chaining, 32U, 20U);
    write_u16(chaining, 34U, 1U);
    write_u16(chaining, 36U, 26U);
    write_u16(chaining, 38U, 1U);
    write_u16(chaining, 40U, 32U);
    write_u16(chaining, 42U, 1U);
    write_u16(chaining, 44U, 0U);
    write_u16(chaining, 46U, 1U);
    write_u16(chaining, 48U, 1U);
    write_u16(chaining, 50U, 1U);
    write_u16(chaining, 52U, 1U);
    write_u16(chaining, 54U, 1U);
    write_u16(chaining, 56U, 1U);
    write_u16(chaining, 58U, 5U);
    write_u16(chaining, 60U, 1U);
    write_u16(chaining, 62U, 1U);
    write_u16(chaining, 64U, 7U);
    write_u16(chaining, 66U, 1U);
    write_u16(chaining, 70U, 1U);
    write_u16(chaining, 72U, 8U);
    write_u16(chaining, 74U, 1U);
    write_u16(chaining, 76U, 6U);
    write_u16(chaining, 78U, 7U);
    write_u16(chaining, 80U, 1U);
    write_u16(chaining, 82U, 1U);
    write_u16(chaining, 84U, 5U);
    require(open_type_layout_table_view::try_create(chaining, gsub, &error));
    glyphs = {shaping_glyph{1U}, shaping_glyph{5U}, shaping_glyph{7U}};
    count = 3U;
    require(try_apply_open_type_gsub_lookup(
        gsub, 0U, glyphs, count, {}, applied, &error));
    require(applied && count == 3U && glyphs[0U].glyph_id == 1U &&
        glyphs[1U].glyph_id == 12U && glyphs[2U].glyph_id == 7U);

    glyphs = {shaping_glyph{2U}, shaping_glyph{5U}, shaping_glyph{7U}};
    count = 3U;
    require(try_apply_open_type_gsub_lookup(
        gsub, 0U, glyphs, count, {}, applied, &error));
    require(!applied && glyphs[1U].glyph_id == 5U);

    std::array<std::byte, 116U> class_chaining{};
    write_u16(class_chaining, 0U, 1U);
    write_u16(class_chaining, 4U, 10U);
    write_u16(class_chaining, 6U, 12U);
    write_u16(class_chaining, 8U, 14U);
    write_u16(class_chaining, 14U, 2U);
    write_u16(class_chaining, 16U, 6U);
    write_u16(class_chaining, 18U, 82U);
    write_u16(class_chaining, 20U, 6U);
    write_u16(class_chaining, 24U, 1U);
    write_u16(class_chaining, 26U, 8U);
    write_u16(class_chaining, 28U, 2U);
    write_u16(class_chaining, 30U, 16U);
    write_u16(class_chaining, 32U, 22U);
    write_u16(class_chaining, 34U, 30U);
    write_u16(class_chaining, 36U, 38U);
    write_u16(class_chaining, 38U, 2U);
    write_u16(class_chaining, 42U, 46U);
    write_u16(class_chaining, 44U, 1U);
    write_u16(class_chaining, 46U, 1U);
    write_u16(class_chaining, 48U, 5U);
    write_u16(class_chaining, 50U, 1U);
    write_u16(class_chaining, 52U, 1U);
    write_u16(class_chaining, 54U, 1U);
    write_u16(class_chaining, 56U, 1U);
    write_u16(class_chaining, 58U, 1U);
    write_u16(class_chaining, 60U, 5U);
    write_u16(class_chaining, 62U, 1U);
    write_u16(class_chaining, 64U, 1U);
    write_u16(class_chaining, 66U, 1U);
    write_u16(class_chaining, 68U, 7U);
    write_u16(class_chaining, 70U, 1U);
    write_u16(class_chaining, 72U, 1U);
    write_u16(class_chaining, 74U, 1U);
    write_u16(class_chaining, 76U, 4U);
    write_u16(class_chaining, 78U, 1U);
    write_u16(class_chaining, 80U, 1U);
    write_u16(class_chaining, 82U, 1U);
    write_u16(class_chaining, 84U, 1U);
    write_u16(class_chaining, 86U, 1U);
    write_u16(class_chaining, 88U, 1U);
    write_u16(class_chaining, 90U, 0U);
    write_u16(class_chaining, 92U, 1U);
    write_u16(class_chaining, 94U, 1U);
    write_u16(class_chaining, 96U, 1U);
    write_u16(class_chaining, 100U, 1U);
    write_u16(class_chaining, 102U, 8U);
    write_u16(class_chaining, 104U, 1U);
    write_u16(class_chaining, 106U, 6U);
    write_u16(class_chaining, 108U, 7U);
    write_u16(class_chaining, 110U, 1U);
    write_u16(class_chaining, 112U, 1U);
    write_u16(class_chaining, 114U, 5U);
    require(open_type_layout_table_view::try_create(
        class_chaining, gsub, &error));
    glyphs = {shaping_glyph{1U}, shaping_glyph{5U}, shaping_glyph{7U}};
    count = 3U;
    require(try_apply_open_type_gsub_lookup(
        gsub, 0U, glyphs, count, {}, applied, &error));
    require(applied && glyphs[1U].glyph_id == 12U);
}

void open_type_gsub_context_glyph_and_class_rules_are_bounded() {
    std::array<std::byte, 76U> glyph_rules{};
    write_u16(glyph_rules, 0U, 1U);
    write_u16(glyph_rules, 4U, 10U);
    write_u16(glyph_rules, 6U, 12U);
    write_u16(glyph_rules, 8U, 14U);
    write_u16(glyph_rules, 14U, 2U);
    write_u16(glyph_rules, 16U, 6U);
    write_u16(glyph_rules, 18U, 42U);
    write_u16(glyph_rules, 20U, 5U);
    write_u16(glyph_rules, 24U, 1U);
    write_u16(glyph_rules, 26U, 8U);
    write_u16(glyph_rules, 28U, 1U);
    write_u16(glyph_rules, 30U, 8U);
    write_u16(glyph_rules, 32U, 1U);
    write_u16(glyph_rules, 34U, 14U);
    write_u16(glyph_rules, 36U, 1U);
    write_u16(glyph_rules, 38U, 1U);
    write_u16(glyph_rules, 40U, 5U);
    write_u16(glyph_rules, 42U, 1U);
    write_u16(glyph_rules, 44U, 4U);
    write_u16(glyph_rules, 46U, 2U);
    write_u16(glyph_rules, 48U, 1U);
    write_u16(glyph_rules, 50U, 6U);
    write_u16(glyph_rules, 52U, 1U);
    write_u16(glyph_rules, 54U, 1U);
    write_u16(glyph_rules, 56U, 1U);
    write_u16(glyph_rules, 60U, 1U);
    write_u16(glyph_rules, 62U, 8U);
    write_u16(glyph_rules, 64U, 1U);
    write_u16(glyph_rules, 66U, 6U);
    write_u16(glyph_rules, 68U, 6U);
    write_u16(glyph_rules, 70U, 1U);
    write_u16(glyph_rules, 72U, 1U);
    write_u16(glyph_rules, 74U, 6U);

    open_type_layout_table_view gsub{};
    font_error error = font_error::invalid_argument;
    require(open_type_layout_table_view::try_create(
        glyph_rules, gsub, &error));
    std::array<shaping_glyph, 4U> glyphs{
        shaping_glyph{5U}, shaping_glyph{6U}};
    std::uint32_t count = 2U;
    bool applied = false;
    require(try_apply_open_type_gsub_lookup(
        gsub, 0U, glyphs, count, {}, applied, &error));
    require(applied && glyphs[1U].glyph_id == 12U);

    std::array<std::byte, 90U> class_rules{};
    write_u16(class_rules, 0U, 1U);
    write_u16(class_rules, 4U, 10U);
    write_u16(class_rules, 6U, 12U);
    write_u16(class_rules, 8U, 14U);
    write_u16(class_rules, 14U, 2U);
    write_u16(class_rules, 16U, 6U);
    write_u16(class_rules, 18U, 56U);
    write_u16(class_rules, 20U, 5U);
    write_u16(class_rules, 24U, 1U);
    write_u16(class_rules, 26U, 8U);
    write_u16(class_rules, 28U, 2U);
    write_u16(class_rules, 30U, 12U);
    write_u16(class_rules, 32U, 18U);
    write_u16(class_rules, 34U, 2U);
    write_u16(class_rules, 38U, 28U);
    write_u16(class_rules, 40U, 1U);
    write_u16(class_rules, 42U, 1U);
    write_u16(class_rules, 44U, 5U);
    write_u16(class_rules, 46U, 1U);
    write_u16(class_rules, 48U, 5U);
    write_u16(class_rules, 50U, 2U);
    write_u16(class_rules, 52U, 1U);
    write_u16(class_rules, 54U, 2U);
    write_u16(class_rules, 56U, 1U);
    write_u16(class_rules, 58U, 4U);
    write_u16(class_rules, 60U, 2U);
    write_u16(class_rules, 62U, 1U);
    write_u16(class_rules, 64U, 2U);
    write_u16(class_rules, 66U, 1U);
    write_u16(class_rules, 68U, 1U);
    write_u16(class_rules, 70U, 1U);
    write_u16(class_rules, 74U, 1U);
    write_u16(class_rules, 76U, 8U);
    write_u16(class_rules, 78U, 1U);
    write_u16(class_rules, 80U, 6U);
    write_u16(class_rules, 82U, 6U);
    write_u16(class_rules, 84U, 1U);
    write_u16(class_rules, 86U, 1U);
    write_u16(class_rules, 88U, 6U);
    require(open_type_layout_table_view::try_create(
        class_rules, gsub, &error));
    glyphs = {shaping_glyph{5U}, shaping_glyph{6U}};
    count = 2U;
    require(try_apply_open_type_gsub_lookup(
        gsub, 0U, glyphs, count, {}, applied, &error));
    require(applied && glyphs[1U].glyph_id == 12U);

    glyphs = {shaping_glyph{5U}, shaping_glyph{7U}};
    count = 2U;
    require(try_apply_open_type_gsub_lookup(
        gsub, 0U, glyphs, count, {}, applied, &error));
    require(!applied && glyphs[1U].glyph_id == 7U);
}

void open_type_gsub_chaining_glyph_rules_apply_nested_lookup() {
    std::array<std::byte, 82U> chaining{};
    write_u16(chaining, 0U, 1U);
    write_u16(chaining, 4U, 10U);
    write_u16(chaining, 6U, 12U);
    write_u16(chaining, 8U, 14U);
    write_u16(chaining, 14U, 2U);
    write_u16(chaining, 16U, 6U);
    write_u16(chaining, 18U, 48U);
    write_u16(chaining, 20U, 6U);
    write_u16(chaining, 24U, 1U);
    write_u16(chaining, 26U, 8U);
    write_u16(chaining, 28U, 1U);
    write_u16(chaining, 30U, 8U);
    write_u16(chaining, 32U, 1U);
    write_u16(chaining, 34U, 14U);
    write_u16(chaining, 36U, 1U);
    write_u16(chaining, 38U, 1U);
    write_u16(chaining, 40U, 5U);
    write_u16(chaining, 42U, 1U);
    write_u16(chaining, 44U, 4U);
    write_u16(chaining, 46U, 1U);
    write_u16(chaining, 48U, 1U);
    write_u16(chaining, 50U, 1U);
    write_u16(chaining, 52U, 1U);
    write_u16(chaining, 54U, 7U);
    write_u16(chaining, 56U, 1U);
    write_u16(chaining, 58U, 0U);
    write_u16(chaining, 60U, 1U);
    write_u16(chaining, 62U, 1U);
    write_u16(chaining, 66U, 1U);
    write_u16(chaining, 68U, 8U);
    write_u16(chaining, 70U, 1U);
    write_u16(chaining, 72U, 6U);
    write_u16(chaining, 74U, 7U);
    write_u16(chaining, 76U, 1U);
    write_u16(chaining, 78U, 1U);
    write_u16(chaining, 80U, 5U);

    open_type_layout_table_view gsub{};
    font_error error = font_error::invalid_argument;
    require(open_type_layout_table_view::try_create(chaining, gsub, &error));
    std::array<shaping_glyph, 4U> glyphs{
        shaping_glyph{1U}, shaping_glyph{5U}, shaping_glyph{7U}};
    std::uint32_t count = 3U;
    bool applied = false;
    require(try_apply_open_type_gsub_lookup(
        gsub, 0U, glyphs, count, {}, applied, &error));
    require(applied && glyphs[1U].glyph_id == 12U);

    glyphs = {shaping_glyph{1U}, shaping_glyph{5U}, shaping_glyph{8U}};
    count = 3U;
    require(try_apply_open_type_gsub_lookup(
        gsub, 0U, glyphs, count, {}, applied, &error));
    require(!applied && glyphs[1U].glyph_id == 5U);
}

void open_type_script_language_feature_selection_is_bounded() {
    std::array<std::byte, 88U> layout{};
    write_u16(layout, 0U, 1U);
    write_u16(layout, 4U, 10U);
    write_u16(layout, 6U, 32U);
    write_u16(layout, 8U, 62U);
    write_u16(layout, 10U, 1U);
    write_u16(layout, 12U, 0x6C61U);
    write_u16(layout, 14U, 0x746EU);
    write_u16(layout, 16U, 8U);
    write_u16(layout, 18U, 4U);
    write_u16(layout, 22U, 0U);
    write_u16(layout, 24U, 0U);
    write_u16(layout, 26U, 2U);
    write_u16(layout, 28U, 0U);
    write_u16(layout, 30U, 1U);
    write_u16(layout, 32U, 2U);
    write_u16(layout, 34U, 0x726CU);
    write_u16(layout, 36U, 0x6967U);
    write_u16(layout, 38U, 14U);
    write_u16(layout, 40U, 0x6C69U);
    write_u16(layout, 42U, 0x6761U);
    write_u16(layout, 44U, 22U);
    write_u16(layout, 48U, 2U);
    write_u16(layout, 50U, 0U);
    write_u16(layout, 52U, 1U);
    write_u16(layout, 56U, 2U);
    write_u16(layout, 58U, 1U);
    write_u16(layout, 60U, 2U);
    write_u16(layout, 62U, 3U);
    write_u16(layout, 64U, 8U);
    write_u16(layout, 66U, 14U);
    write_u16(layout, 68U, 20U);
    write_u16(layout, 70U, 1U);
    write_u16(layout, 76U, 1U);
    write_u16(layout, 82U, 1U);

    open_type_layout_table_view table{};
    font_error error = font_error::invalid_argument;
    require(open_type_layout_table_view::try_create(layout, table, &error));
    constexpr std::array<open_type_tag, 1U> requested{
        open_type_tag::from_chars('l', 'i', 'g', 'a')};
    open_type_layout_table_view::lookup_selection_requirements requirements{};
    require(table.try_get_lookup_selection_requirements(
        open_type_tag::from_chars('l', 'a', 't', 'n'),
        {},
        requested,
        requirements,
        &error));
    require(requirements.lookup_capacity == 3U);
    std::array<std::uint16_t, 4U> selected{99U, 99U, 99U, 99U};
    std::uint32_t written = 99U;
    require(table.try_select_lookups(
        open_type_tag::from_chars('l', 'a', 't', 'n'),
        {},
        requested,
        selected,
        written,
        &error));
    require(written == 3U && selected[0U] == 0U && selected[1U] == 1U &&
        selected[2U] == 2U && selected[3U] == 99U);

    selected.fill(99U);
    require(table.try_select_feature_lookups(
        open_type_tag::from_chars('l', 'a', 't', 'n'),
        {},
        open_type_tag::from_chars('l', 'i', 'g', 'a'),
        selected,
        written,
        &error));
    require(written == 2U && selected[0U] == 1U && selected[1U] == 2U &&
        selected[2U] == 99U);

    written = 99U;
    require(!table.try_select_lookups(
        open_type_tag::from_chars('l', 'a', 't', 'n'),
        {},
        requested,
        std::span<std::uint16_t>{selected}.first(2U),
        written,
        &error));
    require(error == font_error::insufficient_buffer && written == 0U);

    require(table.try_get_lookup_selection_requirements(
        open_type_tag::from_chars('c', 'y', 'r', 'l'),
        {},
        requested,
        requirements,
        &error));
    require(requirements.lookup_capacity == 0U);
}

void open_type_gpos_single_and_pair_adjustments_are_bounded() {
    std::array<std::byte, 42U> single{};
    write_u16(single, 0U, 1U);
    write_u16(single, 4U, 10U);
    write_u16(single, 6U, 12U);
    write_u16(single, 8U, 14U);
    write_u16(single, 14U, 1U);
    write_u16(single, 16U, 4U);
    write_u16(single, 18U, 1U);
    write_u16(single, 22U, 1U);
    write_u16(single, 24U, 8U);
    write_u16(single, 26U, 1U);
    write_u16(single, 28U, 10U);
    write_u16(single, 30U, 0x0005U);
    write_u16(single, 32U, 3U);
    write_u16(single, 34U, 0xFFFEU);
    write_u16(single, 36U, 1U);
    write_u16(single, 38U, 1U);
    write_u16(single, 40U, 5U);
    open_type_layout_table_view gpos{};
    font_error error = font_error::invalid_argument;
    require(open_type_layout_table_view::try_create(single, gpos, &error));
    std::array<shaping_glyph, 3U> glyphs{
        shaping_glyph{5U, 0U, 0, shaping_glyph_flags::none, 10, 0, 1, 0}};
    bool applied = false;
    require(try_apply_open_type_gpos_lookup(
        gpos, 0U, std::span<shaping_glyph>{glyphs}.first(1U),
        open_type_gpos_apply_options{},
        applied, &error));
    require(applied && glyphs[0U].offset_x == 4 &&
        glyphs[0U].advance_x == 8);

    std::array<std::byte, 52U> pair{};
    write_u16(pair, 0U, 1U);
    write_u16(pair, 4U, 10U);
    write_u16(pair, 6U, 12U);
    write_u16(pair, 8U, 14U);
    write_u16(pair, 14U, 1U);
    write_u16(pair, 16U, 4U);
    write_u16(pair, 18U, 2U);
    write_u16(pair, 22U, 1U);
    write_u16(pair, 24U, 8U);
    write_u16(pair, 26U, 1U);
    write_u16(pair, 28U, 12U);
    write_u16(pair, 30U, 0x0004U);
    write_u16(pair, 32U, 0x0001U);
    write_u16(pair, 34U, 1U);
    write_u16(pair, 36U, 18U);
    write_u16(pair, 38U, 1U);
    write_u16(pair, 40U, 1U);
    write_u16(pair, 42U, 5U);
    write_u16(pair, 44U, 1U);
    write_u16(pair, 46U, 6U);
    write_u16(pair, 48U, 0xFFFEU);
    write_u16(pair, 50U, 3U);
    require(open_type_layout_table_view::try_create(pair, gpos, &error));
    glyphs = {shaping_glyph{5U, 0U, 0, shaping_glyph_flags::none, 10},
        shaping_glyph{6U}};
    require(try_apply_open_type_gpos_lookup(
        gpos, 0U, std::span<shaping_glyph>{glyphs}.first(2U),
        {}, applied, &error));
    require(applied && glyphs[0U].advance_x == 8 &&
        glyphs[1U].offset_x == 3);

    auto malformed = pair;
    write_u16(malformed, 36U, 0xFFFFU);
    require(open_type_layout_table_view::try_create(malformed, gpos, &error));
    glyphs[0U] = shaping_glyph{5U};
    glyphs[1U] = shaping_glyph{6U};
    require(!try_apply_open_type_gpos_lookup(
        gpos, 0U, std::span<shaping_glyph>{glyphs}.first(2U),
        {}, applied, &error));
    require(error == font_error::invalid_face &&
        glyphs[0U].advance_x == 0 && glyphs[1U].offset_x == 0);
}

void open_type_gpos_attachments_are_caller_owned_and_resolved() {
    std::array<std::byte, 100U> mark{};
    write_u16(mark, 0U, 1U);
    write_u16(mark, 4U, 10U);
    write_u16(mark, 6U, 12U);
    write_u16(mark, 8U, 14U);
    write_u16(mark, 14U, 1U);
    write_u16(mark, 16U, 4U);
    write_u16(mark, 18U, 4U);
    write_u16(mark, 22U, 1U);
    write_u16(mark, 24U, 8U);
    constexpr std::size_t mark_subtable = 26U;
    write_u16(mark, mark_subtable, 1U);
    write_u16(mark, mark_subtable + 2U, 40U);
    write_u16(mark, mark_subtable + 4U, 46U);
    write_u16(mark, mark_subtable + 6U, 1U);
    write_u16(mark, mark_subtable + 8U, 52U);
    write_u16(mark, mark_subtable + 10U, 64U);
    write_u16(mark, 66U, 1U);
    write_u16(mark, 68U, 1U);
    write_u16(mark, 70U, 6U);
    write_u16(mark, 72U, 1U);
    write_u16(mark, 74U, 1U);
    write_u16(mark, 76U, 5U);
    write_u16(mark, 78U, 1U);
    write_u16(mark, 80U, 0U);
    write_u16(mark, 82U, 6U);
    write_u16(mark, 84U, 1U);
    write_u16(mark, 86U, 2U);
    write_u16(mark, 88U, 3U);
    write_u16(mark, 90U, 1U);
    write_u16(mark, 92U, 4U);
    write_u16(mark, 94U, 1U);
    write_u16(mark, 96U, 8U);
    write_u16(mark, 98U, 10U);

    open_type_layout_table_view gpos{};
    font_error error = font_error::none;
    require(open_type_layout_table_view::try_create(mark, gpos, &error));
    std::array<shaping_glyph, 2U> glyphs{
        shaping_glyph{5U, 0U, 0, shaping_glyph_flags::none, 10},
        shaping_glyph{6U}};
    std::array<shaping_attachment, 2U> attachments{};
    bool applied = false;
    require(try_apply_open_type_gpos_lookup(
        gpos,
        0U,
        glyphs,
        open_type_gpos_apply_options{
            nullptr,
            progpu::native::text::shaping_direction::left_to_right,
            attachments},
        applied,
        &error));
    require(applied && glyphs[1U].offset_x == 6 &&
        glyphs[1U].offset_y == 7);
    require(attachments[1U].target == 0 &&
        attachments[1U].kind == shaping_attachment_kind::mark);
    std::array<std::uint8_t, 2U> states{};
    require(try_resolve_open_type_attachments(
        glyphs,
        attachments,
        progpu::native::text::shaping_direction::left_to_right,
        states,
        &error));
    require(glyphs[1U].offset_x == -4 && glyphs[1U].offset_y == 7);

    glyphs = {shaping_glyph{5U, 0U, 0, shaping_glyph_flags::none, 10},
        shaping_glyph{6U}};
    require(!try_apply_open_type_gpos_lookup(
        gpos, 0U, glyphs, {}, applied, &error));
    require(error == font_error::invalid_argument &&
        glyphs[1U].offset_x == 0);

    std::array<std::byte, 108U> extension{};
    std::copy(mark.begin(), mark.begin() + mark_subtable, extension.begin());
    write_u16(extension, 18U, 9U);
    write_u16(extension, mark_subtable, 1U);
    write_u16(extension, mark_subtable + 2U, 4U);
    write_u16(extension, mark_subtable + 4U, 0U);
    write_u16(extension, mark_subtable + 6U, 8U);
    std::copy(
        mark.begin() + mark_subtable,
        mark.end(),
        extension.begin() + mark_subtable + 8U);
    require(open_type_layout_table_view::try_create(extension, gpos, &error));
    require(!try_apply_open_type_gpos_lookup(
        gpos, 0U, glyphs, {}, applied, &error));
    require(error == font_error::invalid_argument &&
        glyphs[1U].offset_x == 0);

    std::array<std::byte, 60U> cursive{};
    write_u16(cursive, 0U, 1U);
    write_u16(cursive, 4U, 10U);
    write_u16(cursive, 6U, 12U);
    write_u16(cursive, 8U, 14U);
    write_u16(cursive, 14U, 1U);
    write_u16(cursive, 16U, 4U);
    write_u16(cursive, 18U, 3U);
    write_u16(cursive, 22U, 1U);
    write_u16(cursive, 24U, 8U);
    write_u16(cursive, 26U, 1U);
    write_u16(cursive, 28U, 14U);
    write_u16(cursive, 30U, 2U);
    write_u16(cursive, 32U, 0U);
    write_u16(cursive, 34U, 22U);
    write_u16(cursive, 36U, 28U);
    write_u16(cursive, 38U, 0U);
    write_u16(cursive, 40U, 1U);
    write_u16(cursive, 42U, 2U);
    write_u16(cursive, 44U, 5U);
    write_u16(cursive, 46U, 6U);
    write_u16(cursive, 48U, 1U);
    write_u16(cursive, 50U, 8U);
    write_u16(cursive, 52U, 10U);
    write_u16(cursive, 54U, 1U);
    write_u16(cursive, 56U, 2U);
    write_u16(cursive, 58U, 3U);
    require(open_type_layout_table_view::try_create(cursive, gpos, &error));
    glyphs = {shaping_glyph{5U, 0U, 0, shaping_glyph_flags::none, 10},
        shaping_glyph{6U, 0U, 0, shaping_glyph_flags::none, 10}};
    attachments = {};
    require(try_apply_open_type_gpos_lookup(
        gpos,
        0U,
        glyphs,
        open_type_gpos_apply_options{
            nullptr,
            progpu::native::text::shaping_direction::left_to_right,
            attachments},
        applied,
        &error));
    require(applied && glyphs[0U].advance_x == 8 &&
        glyphs[1U].advance_x == 8 && glyphs[1U].offset_x == -2 &&
        glyphs[1U].offset_y == 7);
    require(attachments[1U].target == 0 &&
        attachments[1U].kind ==
            shaping_attachment_kind::cursive_horizontal);
}

void open_type_gpos_context_format3_applies_nested_lookup() {
    std::array<std::byte, 82U> table{};
    write_u16(table, 0U, 1U);
    write_u16(table, 4U, 10U);
    write_u16(table, 6U, 12U);
    write_u16(table, 8U, 14U);
    write_u16(table, 14U, 2U);
    write_u16(table, 16U, 6U);
    write_u16(table, 18U, 44U);

    write_u16(table, 20U, 7U);
    write_u16(table, 24U, 1U);
    write_u16(table, 26U, 8U);
    write_u16(table, 28U, 3U);
    write_u16(table, 30U, 2U);
    write_u16(table, 32U, 1U);
    write_u16(table, 34U, 18U);
    write_u16(table, 36U, 24U);
    write_u16(table, 38U, 1U);
    write_u16(table, 40U, 1U);
    write_u16(table, 42U, 1U);
    write_u16(table, 46U, 1U);
    write_u16(table, 48U, 1U);
    write_u16(table, 50U, 5U);
    write_u16(table, 52U, 1U);
    write_u16(table, 54U, 1U);
    write_u16(table, 56U, 6U);

    write_u16(table, 58U, 1U);
    write_u16(table, 62U, 1U);
    write_u16(table, 64U, 8U);
    write_u16(table, 66U, 1U);
    write_u16(table, 68U, 10U);
    write_u16(table, 70U, 0x0004U);
    write_u16(table, 72U, 0xFFFEU);
    write_u16(table, 76U, 1U);
    write_u16(table, 78U, 1U);
    write_u16(table, 80U, 6U);

    open_type_layout_table_view gpos{};
    font_error error = font_error::none;
    require(open_type_layout_table_view::try_create(table, gpos, &error));
    std::array<shaping_glyph, 2U> glyphs{
        shaping_glyph{5U, 0U, 0, shaping_glyph_flags::none, 10},
        shaping_glyph{6U, 0U, 1, shaping_glyph_flags::none, 10}};
    bool applied = false;
    require(try_apply_open_type_gpos_lookup(
        gpos, 0U, glyphs, {}, applied, &error));
    require(applied && glyphs[0U].advance_x == 10 &&
        glyphs[1U].advance_x == 8 &&
        (static_cast<std::uint32_t>(glyphs[0U].flags) &
            static_cast<std::uint32_t>(
                shaping_glyph_flags::unsafe_to_break)) == 0U &&
        (static_cast<std::uint32_t>(glyphs[1U].flags) &
            static_cast<std::uint32_t>(
                shaping_glyph_flags::unsafe_to_break)) != 0U &&
        (static_cast<std::uint32_t>(glyphs[1U].flags) &
            static_cast<std::uint32_t>(
                shaping_glyph_flags::unsafe_to_concat)) != 0U);
}

void open_type_gpos_rule_and_chain_contexts_are_bounded() {
    std::array<std::byte, 84U> context{};
    write_u16(context, 0U, 1U);
    write_u16(context, 4U, 10U);
    write_u16(context, 6U, 12U);
    write_u16(context, 8U, 14U);
    write_u16(context, 14U, 2U);
    write_u16(context, 16U, 6U);
    write_u16(context, 18U, 46U);
    write_u16(context, 20U, 7U);
    write_u16(context, 24U, 1U);
    write_u16(context, 26U, 8U);
    write_u16(context, 28U, 1U);
    write_u16(context, 30U, 26U);
    write_u16(context, 32U, 1U);
    write_u16(context, 34U, 8U);
    write_u16(context, 36U, 1U);
    write_u16(context, 38U, 4U);
    write_u16(context, 40U, 2U);
    write_u16(context, 42U, 1U);
    write_u16(context, 44U, 6U);
    write_u16(context, 46U, 1U);
    write_u16(context, 48U, 1U);
    write_u16(context, 54U, 1U);
    write_u16(context, 56U, 1U);
    write_u16(context, 58U, 5U);
    write_u16(context, 60U, 1U);
    write_u16(context, 64U, 1U);
    write_u16(context, 66U, 8U);
    write_u16(context, 68U, 1U);
    write_u16(context, 70U, 10U);
    write_u16(context, 72U, 0x0004U);
    write_u16(context, 74U, 0xFFFEU);
    write_u16(context, 78U, 1U);
    write_u16(context, 80U, 1U);
    write_u16(context, 82U, 6U);

    open_type_layout_table_view gpos{};
    font_error error = font_error::none;
    require(open_type_layout_table_view::try_create(context, gpos, &error));
    std::array<shaping_glyph, 2U> glyphs{
        shaping_glyph{5U, 0U, 0, shaping_glyph_flags::none, 10},
        shaping_glyph{6U, 0U, 1, shaping_glyph_flags::none, 10}};
    bool applied = false;
    require(try_apply_open_type_gpos_lookup(
        gpos, 0U, glyphs, {}, applied, &error));
    require(applied && glyphs[1U].advance_x == 8);

    std::array<std::byte, 100U> chain{};
    write_u16(chain, 0U, 1U);
    write_u16(chain, 4U, 10U);
    write_u16(chain, 6U, 12U);
    write_u16(chain, 8U, 14U);
    write_u16(chain, 14U, 2U);
    write_u16(chain, 16U, 6U);
    write_u16(chain, 18U, 62U);
    write_u16(chain, 20U, 8U);
    write_u16(chain, 24U, 1U);
    write_u16(chain, 26U, 8U);
    write_u16(chain, 28U, 3U);
    write_u16(chain, 30U, 1U);
    write_u16(chain, 32U, 30U);
    write_u16(chain, 34U, 1U);
    write_u16(chain, 36U, 36U);
    write_u16(chain, 38U, 1U);
    write_u16(chain, 40U, 42U);
    write_u16(chain, 42U, 1U);
    write_u16(chain, 44U, 0U);
    write_u16(chain, 46U, 1U);
    write_u16(chain, 58U, 1U);
    write_u16(chain, 60U, 1U);
    write_u16(chain, 62U, 4U);
    write_u16(chain, 64U, 1U);
    write_u16(chain, 66U, 1U);
    write_u16(chain, 68U, 5U);
    write_u16(chain, 70U, 1U);
    write_u16(chain, 72U, 1U);
    write_u16(chain, 74U, 6U);
    write_u16(chain, 76U, 1U);
    write_u16(chain, 80U, 1U);
    write_u16(chain, 82U, 8U);
    write_u16(chain, 84U, 1U);
    write_u16(chain, 86U, 10U);
    write_u16(chain, 88U, 0x0004U);
    write_u16(chain, 90U, 0xFFFEU);
    write_u16(chain, 94U, 1U);
    write_u16(chain, 96U, 1U);
    write_u16(chain, 98U, 5U);
    require(open_type_layout_table_view::try_create(chain, gpos, &error));
    std::array<shaping_glyph, 3U> chain_glyphs{
        shaping_glyph{4U, 0U, 0, shaping_glyph_flags::none, 10},
        shaping_glyph{5U, 0U, 1, shaping_glyph_flags::none, 10},
        shaping_glyph{6U, 0U, 2, shaping_glyph_flags::none, 10}};
    require(try_apply_open_type_gpos_lookup(
        gpos, 0U, chain_glyphs, {}, applied, &error));
    require(applied && chain_glyphs[1U].advance_x == 8 &&
        (static_cast<std::uint32_t>(chain_glyphs[0U].flags) &
            static_cast<std::uint32_t>(
                shaping_glyph_flags::unsafe_to_break)) == 0U &&
        (static_cast<std::uint32_t>(chain_glyphs[1U].flags) &
            static_cast<std::uint32_t>(
                shaping_glyph_flags::unsafe_to_concat)) != 0U &&
        (static_cast<std::uint32_t>(chain_glyphs[2U].flags) &
            static_cast<std::uint32_t>(
                shaping_glyph_flags::unsafe_to_break)) != 0U &&
        (static_cast<std::uint32_t>(chain_glyphs[2U].flags) &
            static_cast<std::uint32_t>(
                shaping_glyph_flags::unsafe_to_concat)) != 0U);
}

std::uint64_t hash_path_segments(
    std::span<const progpu_native_path_segment> segments) {
    std::uint64_t hash = 1469598103934665603ULL;
    constexpr std::uint64_t prime = 1099511628211ULL;
    const auto append = [&](std::uint32_t value) {
        hash = (hash ^ value) * prime;
    };
    for (const auto& segment : segments) {
        append(segment.kind);
        append(std::bit_cast<std::uint32_t>(segment.p0.x));
        append(std::bit_cast<std::uint32_t>(segment.p0.y));
        append(std::bit_cast<std::uint32_t>(segment.p1.x));
        append(std::bit_cast<std::uint32_t>(segment.p1.y));
        append(std::bit_cast<std::uint32_t>(segment.p2.x));
        append(std::bit_cast<std::uint32_t>(segment.p2.y));
    }
    return hash;
}

std::uint64_t hash_complete_path_segments(
    std::span<const progpu_native_path_segment> segments) {
    std::uint64_t hash = 1469598103934665603ULL;
    constexpr std::uint64_t prime = 1099511628211ULL;
    const auto append = [&](std::uint32_t value) {
        hash = (hash ^ value) * prime;
    };
    for (const auto& segment : segments) {
        append(segment.kind);
        append(std::bit_cast<std::uint32_t>(segment.p0.x));
        append(std::bit_cast<std::uint32_t>(segment.p0.y));
        append(std::bit_cast<std::uint32_t>(segment.p1.x));
        append(std::bit_cast<std::uint32_t>(segment.p1.y));
        append(std::bit_cast<std::uint32_t>(segment.p2.x));
        append(std::bit_cast<std::uint32_t>(segment.p2.y));
        append(std::bit_cast<std::uint32_t>(segment.p3.x));
        append(std::bit_cast<std::uint32_t>(segment.p3.y));
    }
    return hash;
}

void write_u16(
    std::span<std::byte> destination,
    std::size_t offset,
    std::uint16_t value) {
    destination[offset] = static_cast<std::byte>(value >> 8U);
    destination[offset + 1U] = static_cast<std::byte>(value);
}

void write_i16(
    std::span<std::byte> destination,
    std::size_t offset,
    std::int16_t value) {
    write_u16(destination, offset, static_cast<std::uint16_t>(value));
}

void write_u32(
    std::span<std::byte> destination,
    std::size_t offset,
    std::uint32_t value) {
    destination[offset] = static_cast<std::byte>(value >> 24U);
    destination[offset + 1U] = static_cast<std::byte>(value >> 16U);
    destination[offset + 2U] = static_cast<std::byte>(value >> 8U);
    destination[offset + 3U] = static_cast<std::byte>(value);
}

std::vector<std::byte> make_woff1_fixture() {
    constexpr std::array<unsigned char, 26U> compressed_values{
        0x78U, 0x01U, 0x4BU, 0xCBU, 0xACU, 0x48U, 0x4DU, 0xD1U,
        0xCDU, 0x28U, 0x4DU, 0x4BU, 0xCBU, 0x4DU, 0xCCU, 0xD3U,
        0x4DU, 0x1BU, 0xE5U, 0x41U, 0x79U, 0x00U, 0x62U, 0xC2U,
        0x6AU, 0x2DU};
    constexpr std::size_t first_source = 84U;
    constexpr std::size_t second_source = 112U;
    constexpr std::size_t declared_length = 116U;
    constexpr std::size_t normalized_size = 328U;
    std::vector<std::byte> result(declared_length);
    write_u32(result, 0U, 0x774F4646U);
    write_u32(result, 4U, 0x00010000U);
    write_u32(result, 8U, static_cast<std::uint32_t>(declared_length));
    write_u16(result, 12U, 2U);
    write_u32(result, 16U, static_cast<std::uint32_t>(normalized_size));
    write_u32(result, 44U,
        open_type_tag::from_chars('T', 'E', 'S', 'T').value);
    write_u32(result, 48U, first_source);
    write_u32(result, 52U,
        static_cast<std::uint32_t>(compressed_values.size()));
    write_u32(result, 56U, 280U);
    write_u32(result, 60U, 0x10203040U);
    write_u32(result, 64U,
        open_type_tag::from_chars('D', 'A', 'T', 'A').value);
    write_u32(result, 68U, second_source);
    write_u32(result, 72U, 4U);
    write_u32(result, 76U, 4U);
    write_u32(result, 80U, 0x50607080U);
    for (std::size_t index = 0U; index < compressed_values.size(); ++index) {
        result[first_source + index] =
            static_cast<std::byte>(compressed_values[index]);
    }
    result[second_source] = std::byte{1U};
    result[second_source + 1U] = std::byte{2U};
    result[second_source + 2U] = std::byte{3U};
    result[second_source + 3U] = std::byte{4U};
    return result;
}

void woff1_normalization_is_bounded_and_transactional() {
    auto woff = make_woff1_fixture();
    sfnt_container_requirements requirements{};
    font_error error = font_error::invalid_container;
    require(try_get_sfnt_container_requirements(
        woff, requirements, &error));
    require(error == font_error::none &&
        requirements.requires_normalization &&
        requirements.table_count == 2U &&
        requirements.normalized_bytes == 328U &&
        requirements.table_scratch_bytes == 280U);
    std::vector<std::byte> scratch(requirements.table_scratch_bytes);
    std::vector<std::byte> normalized(
        requirements.normalized_bytes, std::byte{0xA5U});
    sfnt_container_requirements normalized_requirements{};
    require(try_normalize_sfnt_container(
        woff,
        scratch,
        normalized,
        normalized_requirements,
        &error));
    require(normalized_requirements.normalized_bytes == normalized.size());
    require(normalized[0U] == std::byte{0U} &&
        normalized[1U] == std::byte{1U} &&
        normalized[4U] == std::byte{0U} &&
        normalized[5U] == std::byte{2U} &&
        normalized[6U] == std::byte{0U} &&
        normalized[7U] == std::byte{32U} &&
        normalized[8U] == std::byte{0U} &&
        normalized[9U] == std::byte{1U});
    constexpr std::string_view pattern = "fixed-huffman-";
    for (std::size_t index = 0U; index < 280U; ++index) {
        require(normalized[44U + index] ==
            static_cast<std::byte>(pattern[index % pattern.size()]));
    }
    require(normalized[324U] == std::byte{1U} &&
        normalized[327U] == std::byte{4U});
    sfnt_font_view face{};
    require(sfnt_font_view::try_create(normalized, 0U, face, &error));
    sfnt_table_view table{};
    require(face.try_get_table(
        open_type_tag::from_chars('T', 'E', 'S', 'T'), table));
    require(table.bytes.size() == 280U &&
        table.checksum == 0x10203040U);

    std::array<std::byte, 4U> raw{
        std::byte{0U}, std::byte{1U}, std::byte{0U}, std::byte{0U}};
    std::array<std::byte, 4U> raw_copy{};
    require(try_normalize_sfnt_container(
        raw, {}, raw_copy, normalized_requirements, &error));
    require(!normalized_requirements.requires_normalization &&
        raw == raw_copy);

    auto invalid = woff;
    invalid[84U] ^= std::byte{1U};
    std::fill(normalized.begin(), normalized.end(), std::byte{0xA5U});
    require(!try_normalize_sfnt_container(
        invalid,
        scratch,
        normalized,
        normalized_requirements,
        &error));
    require(error == font_error::invalid_compressed_data &&
        std::all_of(normalized.begin(), normalized.end(), [](std::byte value) {
            return value == std::byte{0xA5U};
        }));

    invalid = woff;
    write_u32(invalid, 0U, 0x774F4632U);
    require(!try_get_sfnt_container_requirements(
        invalid, requirements, &error));
    require(error == font_error::unsupported_container);
}

struct table_data final {
    open_type_tag tag{};
    std::vector<std::byte> bytes{};
};

std::vector<std::byte> make_cmap() {
    std::vector<std::byte> result(80U);
    write_u16(result, 2U, 2U);
    write_u16(result, 4U, 3U);
    write_u16(result, 6U, 1U);
    write_u32(result, 8U, 20U);
    write_u16(result, 12U, 3U);
    write_u16(result, 14U, 10U);
    write_u32(result, 16U, 52U);

    write_u16(result, 20U, 4U);
    write_u16(result, 22U, 32U);
    write_u16(result, 26U, 4U);
    write_u16(result, 34U, 0x0041U);
    write_u16(result, 36U, 0xFFFFU);
    write_u16(result, 40U, 0x0041U);
    write_u16(result, 42U, 0xFFFFU);
    write_i16(result, 44U, -62);
    write_i16(result, 46U, 1);

    write_u16(result, 52U, 12U);
    write_u32(result, 56U, 28U);
    write_u32(result, 64U, 1U);
    write_u32(result, 68U, 0x1F600U);
    write_u32(result, 72U, 0x1F600U);
    write_u32(result, 76U, 7U);
    return result;
}

std::vector<std::byte> make_cmap_groups(
    std::span<const std::pair<std::uint32_t, std::uint32_t>> mappings) {
    std::vector<std::byte> result(28U + mappings.size() * 12U);
    write_u16(result, 2U, 1U);
    write_u16(result, 4U, 3U);
    write_u16(result, 6U, 10U);
    write_u32(result, 8U, 12U);
    write_u16(result, 12U, 12U);
    write_u32(result, 16U, static_cast<std::uint32_t>(result.size() - 12U));
    write_u32(result, 24U, static_cast<std::uint32_t>(mappings.size()));
    for (std::size_t index = 0U; index < mappings.size(); ++index) {
        const std::size_t offset = 28U + index * 12U;
        write_u32(result, offset, mappings[index].first);
        write_u32(result, offset + 4U, mappings[index].first);
        write_u32(result, offset + 8U, mappings[index].second);
    }
    return result;
}

std::vector<std::byte> make_cmap14() {
    std::vector<std::byte> result(90U);
    write_u16(result, 2U, 2U);
    write_u16(result, 4U, 3U);
    write_u16(result, 6U, 1U);
    write_u32(result, 8U, 20U);
    write_u16(result, 12U, 0U);
    write_u16(result, 14U, 5U);
    write_u32(result, 16U, 52U);

    write_u16(result, 20U, 4U);
    write_u16(result, 22U, 32U);
    write_u16(result, 26U, 4U);
    write_u16(result, 34U, 0x0041U);
    write_u16(result, 36U, 0xFFFFU);
    write_u16(result, 40U, 0x0041U);
    write_u16(result, 42U, 0xFFFFU);
    write_i16(result, 44U, -62);
    write_i16(result, 46U, 1);

    write_u16(result, 52U, 14U);
    write_u32(result, 54U, 38U);
    write_u32(result, 58U, 1U);
    write_u24(result, 62U, 0xFE0FU);
    write_u32(result, 65U, 21U);
    write_u32(result, 69U, 29U);
    write_u32(result, 73U, 1U);
    write_u24(result, 77U, 0x41U);
    result[80U] = std::byte{0U};
    write_u32(result, 81U, 1U);
    write_u24(result, 85U, 0x42U);
    write_u16(result, 88U, 7U);
    return result;
}

std::vector<std::byte> make_fvar() {
    std::vector<std::byte> result(56U);
    write_u16(result, 0U, 1U);
    write_u16(result, 2U, 0U);
    write_u16(result, 4U, 16U);
    write_u16(result, 6U, 2U);
    write_u16(result, 8U, 2U);
    write_u16(result, 10U, 20U);
    write_u16(result, 12U, 0U);
    write_u16(result, 14U, 0U);
    write_u32(result, 16U,
        open_type_tag::from_chars('o', 'p', 's', 'z').value);
    write_u32(result, 20U, 14U << 16U);
    write_u32(result, 24U, 14U << 16U);
    write_u32(result, 28U, 32U << 16U);
    write_u16(result, 32U, 1U);
    write_u16(result, 34U, 256U);
    write_u32(result, 36U,
        open_type_tag::from_chars('w', 'g', 'h', 't').value);
    write_u32(result, 40U, 100U << 16U);
    write_u32(result, 44U, 400U << 16U);
    write_u32(result, 48U, 900U << 16U);
    write_u16(result, 52U, 0U);
    write_u16(result, 54U, 257U);
    return result;
}

std::vector<std::byte> make_avar() {
    std::vector<std::byte> result(44U);
    write_u16(result, 0U, 1U);
    write_u16(result, 2U, 0U);
    write_u16(result, 4U, 0U);
    write_u16(result, 6U, 2U);
    write_u16(result, 8U, 3U);
    write_i16(result, 10U, -16384);
    write_i16(result, 12U, -16384);
    write_i16(result, 14U, 0);
    write_i16(result, 16U, 0);
    write_i16(result, 18U, 16384);
    write_i16(result, 20U, 16384);
    write_u16(result, 22U, 5U);
    write_i16(result, 24U, -16384);
    write_i16(result, 26U, -16384);
    write_i16(result, 28U, 0);
    write_i16(result, 30U, 0);
    write_i16(result, 32U, 3277);
    write_i16(result, 34U, 2949);
    write_i16(result, 36U, 9830);
    write_i16(result, 38U, 8847);
    write_i16(result, 40U, 16384);
    write_i16(result, 42U, 16384);
    return result;
}

std::vector<std::byte> make_gvar() {
    std::vector<std::byte> result(72U);
    write_u16(result, 0U, 1U);
    write_u16(result, 2U, 0U);
    write_u16(result, 4U, 2U);
    write_u16(result, 6U, 1U);
    write_u32(result, 8U, 38U);
    write_u16(result, 12U, 8U);
    write_u16(result, 14U, 0U);
    write_u32(result, 16U, 42U);
    for (std::size_t index = 5U; index <= 8U; ++index) {
        write_u16(result, 20U + index * 2U, 15U);
    }
    write_i16(result, 38U, -16384);
    write_i16(result, 40U, 8192);
    write_u16(result, 42U, 1U);
    write_u16(result, 44U, 20U);
    write_u16(result, 46U, 10U);
    write_u16(result, 48U, 0xE000U);
    write_i16(result, 50U, 8192);
    write_i16(result, 52U, -4096);
    write_i16(result, 54U, 0);
    write_i16(result, 56U, -8192);
    write_i16(result, 58U, 16384);
    write_i16(result, 60U, 0);
    result[62U] = std::byte{0U};
    result[63U] = std::byte{6U};
    result[64U] = std::byte{2U};
    result[65U] = std::byte{0U};
    result[66U] = std::byte{0U};
    result[67U] = std::byte{4U};
    result[68U] = std::byte{10U};
    result[69U] = std::byte{0U};
    result[70U] = std::byte{0U};
    result[71U] = std::byte{0x86U};
    return result;
}

std::vector<std::byte> make_sbix_strike(
    std::uint16_t pixels_per_em,
    std::int16_t origin_x,
    std::int16_t origin_y,
    std::array<std::byte, 3U> image) {
    constexpr std::uint32_t data_start = 40U;
    constexpr std::uint32_t duplicate_start = data_start + 11U;
    constexpr std::uint32_t end = duplicate_start + 10U;
    std::vector<std::byte> result(end);
    write_u16(result, 0U, pixels_per_em);
    write_u16(result, 2U, 72U);
    write_u32(result, 4U, data_start);
    write_u32(result, 8U, data_start);
    write_u32(result, 12U, duplicate_start);
    for (std::size_t glyph = 3U; glyph <= 8U; ++glyph) {
        write_u32(result, 4U + glyph * 4U, end);
    }
    write_i16(result, data_start, origin_x);
    write_i16(result, data_start + 2U, origin_y);
    write_u32(result, data_start + 4U,
        open_type_tag::from_chars('p', 'n', 'g', ' ').value);
    result[data_start + 8U] = image[0U];
    result[data_start + 9U] = image[1U];
    result[data_start + 10U] = image[2U];
    write_i16(result, duplicate_start, 7);
    write_i16(result, duplicate_start + 2U, 8);
    write_u32(result, duplicate_start + 4U,
        open_type_tag::from_chars('d', 'u', 'p', 'e').value);
    write_u16(result, duplicate_start + 8U, 1U);
    return result;
}

std::vector<std::byte> make_sbix() {
    const auto strike_20 = make_sbix_strike(
        20U, -2, 6, {std::byte{20U}, std::byte{21U}, std::byte{22U}});
    const auto strike_40 = make_sbix_strike(
        40U, -4, 12, {std::byte{40U}, std::byte{41U}, std::byte{42U}});
    std::vector<std::byte> result(16U + strike_20.size() + strike_40.size());
    write_u16(result, 0U, 1U);
    write_u16(result, 2U, 1U);
    write_u32(result, 4U, 2U);
    write_u32(result, 8U, 16U);
    write_u32(result, 12U,
        static_cast<std::uint32_t>(16U + strike_20.size()));
    std::copy(strike_20.begin(), strike_20.end(), result.begin() + 16);
    std::copy(
        strike_40.begin(),
        strike_40.end(),
        result.begin() + static_cast<std::ptrdiff_t>(16U + strike_20.size()));
    return result;
}

std::vector<std::byte> make_svg_glyph_table(bool gzip) {
    const std::array<std::byte, 6U> plain{
        std::byte{0x3CU}, std::byte{0x73U}, std::byte{0x76U},
        std::byte{0x67U}, std::byte{0x2FU}, std::byte{0x3EU}};
    const std::array<std::byte, 26U> compressed{
        std::byte{0x1FU}, std::byte{0x8BU}, std::byte{0x08U},
        std::byte{0x00U}, std::byte{0x9CU}, std::byte{0x67U},
        std::byte{0x7FU}, std::byte{0x6AU}, std::byte{0x00U},
        std::byte{0x03U}, std::byte{0xB3U}, std::byte{0x29U},
        std::byte{0x2EU}, std::byte{0x4BU}, std::byte{0xD7U},
        std::byte{0xB7U}, std::byte{0x03U}, std::byte{0x00U},
        std::byte{0x49U}, std::byte{0xFBU}, std::byte{0xB9U},
        std::byte{0xACU}, std::byte{0x06U}, std::byte{0x00U},
        std::byte{0x00U}, std::byte{0x00U}};
    const std::span<const std::byte> document = gzip
        ? std::span<const std::byte>(compressed)
        : std::span<const std::byte>(plain);
    std::vector<std::byte> result(24U + document.size());
    write_u16(result, 0U, 0U);
    write_u32(result, 2U, 10U);
    write_u32(result, 6U, 0U);
    write_u16(result, 10U, 1U);
    write_u16(result, 12U, 1U);
    write_u16(result, 14U, 2U);
    write_u32(result, 16U, 14U);
    write_u32(result, 20U, static_cast<std::uint32_t>(document.size()));
    std::copy(document.begin(), document.end(), result.begin() + 24);
    return result;
}

struct cbdt_tables final {
    std::vector<std::byte> cblc{};
    std::vector<std::byte> cbdt{};
};

cbdt_tables make_cbdt_tables(
    std::uint16_t index_format,
    std::uint16_t image_format = 0U) {
    const auto metrics_in_index = index_format == 2U || index_format == 5U;
    if (image_format == 0U) {
        image_format = metrics_in_index ? 19U : 17U;
    }
    const std::array image{
        std::byte{0x89U}, std::byte{0x50U}, std::byte{0x4EU}};
    const auto metrics_size = image_format == 17U
        ? 5U
        : image_format == 18U ? 8U : 0U;
    std::vector<std::byte> cbdt(4U + metrics_size + 4U + image.size());
    write_u16(cbdt, 0U, 3U);
    write_u16(cbdt, 2U, 0U);
    if (metrics_size != 0U) {
        cbdt[4U] = std::byte{1U};
        cbdt[5U] = std::byte{1U};
        cbdt[6U] = std::byte{3U};
        cbdt[7U] = std::byte{4U};
        cbdt[8U] = std::byte{5U};
        if (metrics_size == 8U) {
            cbdt[9U] = std::byte{0U};
            cbdt[10U] = std::byte{0U};
            cbdt[11U] = std::byte{5U};
        }
    }
    const auto length_offset = 4U + metrics_size;
    write_u32(cbdt, length_offset,
        static_cast<std::uint32_t>(image.size()));
    std::copy(
        image.begin(), image.end(),
        cbdt.begin() + static_cast<std::ptrdiff_t>(length_offset + 4U));

    const auto bitmap_data_length =
        static_cast<std::uint32_t>(cbdt.size() - 4U);
    std::size_t subtable_size = 0U;
    switch (index_format) {
    case 1U:
        subtable_size = 16U;
        break;
    case 2U:
        subtable_size = 20U;
        break;
    case 3U:
        subtable_size = 12U;
        break;
    case 4U:
        subtable_size = 20U;
        break;
    case 5U:
        subtable_size = 28U;
        break;
    default:
        std::abort();
    }
    std::vector<std::byte> subtable(subtable_size);
    write_u16(subtable, 0U, index_format);
    write_u16(subtable, 2U, image_format);
    write_u32(subtable, 4U, 4U);
    switch (index_format) {
    case 1U:
        write_u32(subtable, 8U, 0U);
        write_u32(subtable, 12U, bitmap_data_length);
        break;
    case 2U:
        write_u32(subtable, 8U, bitmap_data_length);
        break;
    case 3U:
        write_u16(subtable, 8U, 0U);
        write_u16(subtable, 10U,
            static_cast<std::uint16_t>(bitmap_data_length));
        break;
    case 4U:
        write_u32(subtable, 8U, 1U);
        write_u16(subtable, 12U, 1U);
        write_u16(subtable, 14U, 0U);
        write_u16(subtable, 16U, 0xFFFFU);
        write_u16(subtable, 18U,
            static_cast<std::uint16_t>(bitmap_data_length));
        break;
    case 5U:
        write_u32(subtable, 8U, bitmap_data_length);
        write_u32(subtable, 20U, 1U);
        write_u16(subtable, 24U, 1U);
        break;
    default:
        std::abort();
    }
    if (metrics_in_index) {
        subtable[12U] = std::byte{1U};
        subtable[13U] = std::byte{1U};
        subtable[14U] = std::byte{3U};
        subtable[15U] = std::byte{4U};
        subtable[16U] = std::byte{5U};
        subtable[19U] = std::byte{5U};
    }

    std::vector<std::byte> cblc(64U + subtable.size());
    write_u16(cblc, 0U, 3U);
    write_u16(cblc, 2U, 0U);
    write_u32(cblc, 4U, 1U);
    write_u32(cblc, 8U, 56U);
    write_u32(cblc, 12U,
        static_cast<std::uint32_t>(8U + subtable.size()));
    write_u32(cblc, 16U, 1U);
    write_u16(cblc, 48U, 1U);
    write_u16(cblc, 50U, 1U);
    cblc[52U] = std::byte{20U};
    cblc[53U] = std::byte{20U};
    cblc[54U] = std::byte{32U};
    cblc[55U] = std::byte{1U};
    write_u16(cblc, 56U, 1U);
    write_u16(cblc, 58U, 1U);
    write_u32(cblc, 60U, 8U);
    std::copy(subtable.begin(), subtable.end(), cblc.begin() + 64);
    return {std::move(cblc), std::move(cbdt)};
}

std::vector<std::byte> make_colr() {
    std::vector<std::byte> result(32U);
    write_u16(result, 0U, 0U);
    write_u16(result, 2U, 1U);
    write_u32(result, 4U, 14U);
    write_u32(result, 8U, 20U);
    write_u16(result, 12U, 3U);
    write_u16(result, 14U, 1U);
    write_u16(result, 16U, 0U);
    write_u16(result, 18U, 3U);
    write_u16(result, 20U, 2U);
    write_u16(result, 22U, 0U);
    write_u16(result, 24U, 3U);
    write_u16(result, 26U, 1U);
    write_u16(result, 28U, 4U);
    write_u16(result, 30U, 0xFFFFU);
    return result;
}

std::vector<std::byte> make_cpal() {
    std::vector<std::byte> result(32U);
    write_u16(result, 0U, 0U);
    write_u16(result, 2U, 2U);
    write_u16(result, 4U, 2U);
    write_u16(result, 6U, 4U);
    write_u32(result, 8U, 16U);
    write_u16(result, 12U, 0U);
    write_u16(result, 14U, 2U);
    result[16U] = std::byte{0U};
    result[17U] = std::byte{0U};
    result[18U] = std::byte{255U};
    result[19U] = std::byte{255U};
    result[20U] = std::byte{255U};
    result[21U] = std::byte{0U};
    result[22U] = std::byte{0U};
    result[23U] = std::byte{255U};
    result[24U] = std::byte{0U};
    result[25U] = std::byte{255U};
    result[26U] = std::byte{0U};
    result[27U] = std::byte{255U};
    result[28U] = std::byte{255U};
    result[29U] = std::byte{255U};
    result[30U] = std::byte{255U};
    result[31U] = std::byte{128U};
    return result;
}

std::vector<std::byte> make_cff2_table() {
    constexpr std::size_t top_size = 13U;
    constexpr std::size_t char_strings_offset = 22U;
    constexpr std::size_t font_dictionaries_offset = 116U;
    constexpr std::array<std::byte, 10U> char_string{
        std::byte{0x8B}, std::byte{0x8B}, std::byte{0x15},
        std::byte{0xEF}, std::byte{0x8B}, std::byte{0x8B},
        std::byte{0xEF}, std::byte{0x27}, std::byte{0x8B},
        std::byte{0x05}};
    std::vector<std::byte> result(126U);
    result[0U] = std::byte{2U};
    result[1U] = std::byte{0U};
    result[2U] = std::byte{5U};
    write_u16(result, 3U, static_cast<std::uint16_t>(top_size));
    result[5U] = std::byte{29U};
    write_u32(result, 6U, static_cast<std::uint32_t>(char_strings_offset));
    result[10U] = std::byte{17U};
    result[11U] = std::byte{29U};
    write_u32(
        result, 12U, static_cast<std::uint32_t>(font_dictionaries_offset));
    result[16U] = std::byte{12U};
    result[17U] = std::byte{36U};
    write_u32(result, 18U, 0U);

    write_u32(result, char_strings_offset, 8U);
    result[char_strings_offset + 4U] = std::byte{1U};
    for (std::size_t index = 0U; index <= 8U; ++index) {
        result[char_strings_offset + 5U + index] =
            static_cast<std::byte>(1U + index * char_string.size());
    }
    auto char_cursor = char_strings_offset + 14U;
    for (std::size_t glyph = 0U; glyph < 8U; ++glyph) {
        std::copy(
            char_string.begin(), char_string.end(),
            result.begin() + static_cast<std::ptrdiff_t>(char_cursor));
        char_cursor += char_string.size();
    }

    write_u32(result, font_dictionaries_offset, 1U);
    result[font_dictionaries_offset + 4U] = std::byte{1U};
    result[font_dictionaries_offset + 5U] = std::byte{1U};
    result[font_dictionaries_offset + 6U] = std::byte{4U};
    result[font_dictionaries_offset + 7U] = std::byte{0x8BU};
    result[font_dictionaries_offset + 8U] = std::byte{0x8BU};
    result[font_dictionaries_offset + 9U] = std::byte{18U};
    return result;
}

std::vector<std::byte> make_font(
    std::size_t face_offset = 0U,
    std::size_t glyph_size = 22U,
    std::size_t second_glyph_size = 0U,
    bool include_variations = false,
    bool include_axis_mapping = false,
    bool include_glyph_variations = false,
    std::span<const table_data> extra_tables = {},
    std::span<const std::byte> cmap_override = {}) {
    std::vector<table_data> tables{};
    table_data head{open_type_tag::from_chars('h', 'e', 'a', 'd'),
        std::vector<std::byte>(54U)};
    write_u16(head.bytes, 18U, 1000U);
    write_i16(head.bytes, 36U, -20);
    write_i16(head.bytes, 38U, -200);
    write_i16(head.bytes, 40U, 900);
    write_i16(head.bytes, 42U, 800);
    write_i16(head.bytes, 50U, 1);
    tables.push_back(std::move(head));

    table_data hhea{open_type_tag::from_chars('h', 'h', 'e', 'a'),
        std::vector<std::byte>(36U)};
    write_i16(hhea.bytes, 4U, 800);
    write_i16(hhea.bytes, 6U, -200);
    write_i16(hhea.bytes, 8U, 40);
    write_u16(hhea.bytes, 10U, 1200U);
    write_u16(hhea.bytes, 34U, 2U);
    tables.push_back(std::move(hhea));

    table_data hmtx{open_type_tag::from_chars('h', 'm', 't', 'x'),
        std::vector<std::byte>(20U)};
    write_u16(hmtx.bytes, 0U, 500U);
    write_i16(hmtx.bytes, 2U, 10);
    write_u16(hmtx.bytes, 4U, 600U);
    write_i16(hmtx.bytes, 6U, 20);
    write_i16(hmtx.bytes, 10U, 30);
    tables.push_back(std::move(hmtx));

    table_data maxp{open_type_tag::from_chars('m', 'a', 'x', 'p'),
        std::vector<std::byte>(6U)};
    write_u16(maxp.bytes, 4U, 8U);
    tables.push_back(std::move(maxp));
    table_data loca{open_type_tag::from_chars('l', 'o', 'c', 'a'),
        std::vector<std::byte>(36U)};
    write_u32(loca.bytes, 20U, static_cast<std::uint32_t>(glyph_size));
    const auto complete_glyph_size = glyph_size + second_glyph_size;
    write_u32(loca.bytes, 24U,
        static_cast<std::uint32_t>(complete_glyph_size));
    write_u32(loca.bytes, 28U,
        static_cast<std::uint32_t>(complete_glyph_size));
    write_u32(loca.bytes, 32U,
        static_cast<std::uint32_t>(complete_glyph_size));
    tables.push_back(std::move(loca));
    table_data glyf{open_type_tag::from_chars('g', 'l', 'y', 'f'),
        std::vector<std::byte>(complete_glyph_size)};
    write_i16(glyf.bytes, 0U, 1);
    write_i16(glyf.bytes, 2U, 10);
    write_i16(glyf.bytes, 4U, 0);
    write_i16(glyf.bytes, 6U, 30);
    write_i16(glyf.bytes, 8U, 40);
    write_u16(glyf.bytes, 10U, 2U);
    write_u16(glyf.bytes, 12U, 0U);
    glyf.bytes[14U] = static_cast<std::byte>(0x33U);
    glyf.bytes[15U] = static_cast<std::byte>(0x37U);
    glyf.bytes[16U] = static_cast<std::byte>(0x26U);
    glyf.bytes[17U] = static_cast<std::byte>(10U);
    glyf.bytes[18U] = static_cast<std::byte>(20U);
    glyf.bytes[19U] = static_cast<std::byte>(5U);
    glyf.bytes[20U] = static_cast<std::byte>(30U);
    glyf.bytes[21U] = static_cast<std::byte>(10U);
    tables.push_back(std::move(glyf));
    tables.push_back(table_data{
        open_type_tag::from_chars('c', 'm', 'a', 'p'),
        cmap_override.empty()
            ? make_cmap()
            : std::vector<std::byte>(
                cmap_override.begin(), cmap_override.end())});
    if (include_variations) {
        tables.push_back(table_data{
            open_type_tag::from_chars('f', 'v', 'a', 'r'), make_fvar()});
    }
    if (include_axis_mapping) {
        tables.push_back(table_data{
            open_type_tag::from_chars('a', 'v', 'a', 'r'), make_avar()});
    }
    if (include_glyph_variations) {
        tables.push_back(table_data{
            open_type_tag::from_chars('g', 'v', 'a', 'r'), make_gvar()});
    }
    for (const auto& table : extra_tables) {
        tables.push_back(table);
    }

    const auto directory_size = 12U + tables.size() * 16U;
    std::size_t cursor = face_offset + directory_size;
    for (const auto& table : tables) {
        cursor += table.bytes.size();
    }
    std::vector<std::byte> result(cursor);
    const auto face = std::span<std::byte>(result).subspan(face_offset);
    write_u32(face, 0U, 0x00010000U);
    write_u16(face, 4U, static_cast<std::uint16_t>(tables.size()));
    cursor = face_offset + directory_size;
    for (std::size_t index = 0U; index < tables.size(); ++index) {
        const auto record = face_offset + 12U + index * 16U;
        write_u32(result, record, tables[index].tag.value);
        write_u32(result, record + 4U, 0x1000U +
            static_cast<std::uint32_t>(index));
        write_u32(result, record + 8U, static_cast<std::uint32_t>(cursor));
        write_u32(result, record + 12U,
            static_cast<std::uint32_t>(tables[index].bytes.size()));
        for (const auto value : tables[index].bytes) {
            result[cursor++] = value;
        }
    }
    return result;
}

void open_type_uniform_run_shaper_connects_unicode_font_and_metrics() {
    const auto data = make_font();
    sfnt_font_view font{};
    font_error error = font_error::none;
    require(sfnt_font_view::try_create(data, 0U, font, &error));
    std::array<unicode_scalar, 2U> input{
        unicode_scalar{0x41U, 0U, 1U},
        unicode_scalar{0x41U, 1U, 1U}};
    open_type_shape_run_requirements requirements{};
    require(try_get_open_type_shape_run_requirements(
        font, input, requirements, &error));
    require(requirements.initial_glyph_count == 2U &&
        requirements.glyph_capacity == 6U &&
        requirements.grapheme_capacity == 2U &&
        requirements.gsub_lookup_capacity == 0U &&
        requirements.gpos_lookup_capacity == 0U);

    std::array<shaping_glyph, 4U> glyphs{};
    std::array<unicode_grapheme_cluster, 2U> graphemes{};
    std::array<shaping_attachment, 4U> attachments{};
    std::array<std::uint8_t, 4U> states{};
    std::uint32_t glyph_count = 99U;
    const open_type_shape_run_options latin_options{
        open_type_tag::from_chars('l', 'a', 't', 'n')};
    open_type_shape_plan_requirements plan_requirements{};
    require(try_get_open_type_shape_plan_requirements(
        font, latin_options, plan_requirements, &error));
    require(plan_requirements.gsub_lookup_capacity == 0U &&
        plan_requirements.gpos_lookup_capacity == 0U);
    open_type_shape_plan plan{};
    require(try_build_open_type_shape_plan(
        font, latin_options, {}, {}, plan, &error));
    require(plan.matches(font, latin_options));
    require(try_shape_open_type_run(
        font,
        input,
        latin_options,
        glyphs,
        open_type_shape_run_scratch{
            graphemes, {}, {}, attachments, states},
        glyph_count,
        &error,
        &plan));
    require(glyph_count == 2U && glyphs[0U].glyph_id == 3U &&
        glyphs[1U].glyph_id == 3U && glyphs[0U].cluster == 0 &&
        glyphs[1U].cluster == 1 && glyphs[0U].advance_x > 0 &&
        glyphs[0U].advance_x == glyphs[1U].advance_x);

    glyphs.fill(shaping_glyph{77U});
    const std::array changed_features{
        open_type_tag::from_chars('k', 'e', 'r', 'n')};
    auto mismatched_options = latin_options;
    mismatched_options.requested_features = changed_features;
    require(!try_shape_open_type_run(
        font,
        input,
        mismatched_options,
        glyphs,
        open_type_shape_run_scratch{
            graphemes, {}, {}, attachments, states},
        glyph_count,
        &error,
        &plan));
    require(error == font_error::invalid_argument && glyph_count == 0U &&
        glyphs[0U].glyph_id == 77U);

    glyphs.fill(shaping_glyph{99U});
    require(!try_shape_open_type_run(
        font,
        input,
        {},
        glyphs,
        open_type_shape_run_scratch{
            graphemes,
            {},
            {},
            std::span<shaping_attachment>{attachments}.first(3U),
            states},
        glyph_count,
        &error));
    require(error == font_error::insufficient_buffer && glyph_count == 0U &&
        glyphs[0U].glyph_id == 99U);
}

void open_type_common_preprocessing_matches_managed_stages() {
    constexpr std::array mappings{
        std::pair{0x05BCU, 2U},
        std::pair{0x05E9U, 6U},
        std::pair{0x0E31U, 2U},
        std::pair{0x0E32U, 3U},
        std::pair{0x0E33U, 4U},
        std::pair{0x0E4DU, 5U},
        std::pair{0x25CCU, 1U},
        std::pair{0xFB49U, 7U}};
    const auto cmap = make_cmap_groups(mappings);
    const auto data = make_font(
        0U, 22U, 0U, false, false, false, {}, cmap);
    sfnt_font_view font{};
    font_error error = font_error::none;
    require(sfnt_font_view::try_create(data, 0U, font, &error));

    std::array<shaping_glyph, 6U> marks{
        shaping_glyph{0U, 0x0301U, 0},
        shaping_glyph{0U, 0x0316U, 1}};
    std::uint32_t count = 2U;
    require(try_preprocess_open_type_glyphs(
        font,
        open_type_tag::from_chars('l', 'a', 't', 'n'),
        shaping_cluster_level::monotone_characters,
        shaping_buffer_flags::beginning_of_text,
        true,
        marks,
        count,
        &error));
    require(count == 3U && marks[0U].code_point == 0x25CCU &&
        marks[1U].code_point == 0x0316U &&
        marks[2U].code_point == 0x0301U && marks[2U].cluster == 0);

    std::array<shaping_glyph, 4U> hebrew_glyphs{
        shaping_glyph{6U, 0x05E9U, 4},
        shaping_glyph{2U, 0x05BCU, 4}};
    count = 2U;
    require(try_preprocess_open_type_glyphs(
        font,
        open_type_tag::from_chars('h', 'e', 'b', 'r'),
        shaping_cluster_level::monotone_graphemes,
        shaping_buffer_flags::none,
        true,
        hebrew_glyphs,
        count,
        &error));
    require(count == 1U && hebrew_glyphs[0U].code_point == 0xFB49U &&
        hebrew_glyphs[0U].glyph_id == 7U);

    std::array<shaping_glyph, 6U> thai_glyphs{
        shaping_glyph{2U, 0x0E31U, 0},
        shaping_glyph{4U, 0x0E33U, 1}};
    count = 2U;
    require(try_preprocess_open_type_glyphs(
        font,
        open_type_tag::from_chars('t', 'h', 'a', 'i'),
        shaping_cluster_level::monotone_graphemes,
        shaping_buffer_flags::none,
        true,
        thai_glyphs,
        count,
        &error));
    require(count == 3U && thai_glyphs[0U].code_point == 0x0E4DU &&
        thai_glyphs[1U].code_point == 0x0E31U &&
        thai_glyphs[2U].code_point == 0x0E32U &&
        thai_glyphs[0U].cluster == 0 && thai_glyphs[1U].cluster == 0 &&
        thai_glyphs[2U].cluster == 0);

    std::array<shaping_glyph, 2U> short_storage{
        shaping_glyph{0U, 0x0301U, 5},
        shaping_glyph{0U, 0x0316U, 6}};
    count = 2U;
    require(!try_preprocess_open_type_glyphs(
        font,
        open_type_tag::from_chars('l', 'a', 't', 'n'),
        shaping_cluster_level::monotone_graphemes,
        shaping_buffer_flags::beginning_of_text,
        true,
        short_storage,
        count,
        &error));
    require(error == font_error::insufficient_buffer && count == 2U &&
        short_storage[0U].code_point == 0x0301U &&
        short_storage[1U].code_point == 0x0316U);
}

void open_type_khmer_preparation_reorders_prebase_vowels() {
    constexpr std::array mappings{
        std::pair{0x1780U, 2U},
        std::pair{0x17C1U, 3U},
        std::pair{0x25CCU, 1U}};
    const auto cmap = make_cmap_groups(mappings);
    const auto data = make_font(
        0U, 22U, 0U, false, false, false, {}, cmap);
    sfnt_font_view font{};
    font_error error = font_error::none;
    require(sfnt_font_view::try_create(data, 0U, font, &error));
    const std::array<unicode_scalar, 2U> input{
        unicode_scalar{0x1780U, 0U, 1U},
        unicode_scalar{0x17C1U, 1U, 1U}};
    open_type_shape_run_requirements requirements{};
    require(try_get_open_type_shape_run_requirements(
        font, input, requirements, &error));
    require(requirements.complex_script_capacity == 2U &&
        requirements.complex_script_index_capacity == 3U);

    std::array<shaping_glyph, 6U> glyphs{};
    std::array<unicode_grapheme_cluster, 2U> graphemes{};
    std::array<shaping_attachment, 6U> attachments{};
    std::array<std::uint8_t, 6U> states{};
    std::array<std::uint8_t, 2U> categories{};
    std::array<std::uint8_t, 2U> syllables{};
    auto options = open_type_shape_run_options{
        open_type_tag::from_chars('k', 'h', 'm', 'r')};
    options.complex_script = open_type_complex_script::khmer;
    std::uint32_t glyph_count = 0U;
    require(try_shape_open_type_run(
        font,
        input,
        options,
        glyphs,
        open_type_shape_run_scratch{
            .grapheme_clusters = graphemes,
            .attachments = attachments,
            .attachment_states = states,
            .script_categories = categories,
            .script_syllables = syllables},
        glyph_count,
        &error));
    require(glyph_count == 2U && glyphs[0U].code_point == 0x17C1U &&
        glyphs[1U].code_point == 0x1780U && glyphs[0U].cluster == 0 &&
        glyphs[1U].cluster == 0);
    constexpr std::uint32_t public_flags =
        static_cast<std::uint32_t>(shaping_glyph_flags::unsafe_to_break) |
        static_cast<std::uint32_t>(shaping_glyph_flags::unsafe_to_concat) |
        static_cast<std::uint32_t>(
            shaping_glyph_flags::safe_to_insert_tatweel);
    require((static_cast<std::uint32_t>(glyphs[0U].flags) & ~public_flags) ==
        0U);
    require((static_cast<std::uint32_t>(glyphs[1U].flags) & ~public_flags) ==
        0U);
}

void open_type_myanmar_preparation_reorders_prebase_vowels() {
    constexpr std::array mappings{
        std::pair{0x1000U, 2U},
        std::pair{0x1031U, 3U},
        std::pair{0x25CCU, 1U}};
    const auto cmap = make_cmap_groups(mappings);
    const auto data = make_font(
        0U, 22U, 0U, false, false, false, {}, cmap);
    sfnt_font_view font{};
    font_error error = font_error::none;
    require(sfnt_font_view::try_create(data, 0U, font, &error));
    const std::array<unicode_scalar, 2U> input{
        unicode_scalar{0x1000U, 0U, 1U},
        unicode_scalar{0x1031U, 1U, 1U}};
    std::array<shaping_glyph, 6U> glyphs{};
    std::array<unicode_grapheme_cluster, 2U> graphemes{};
    std::array<shaping_attachment, 6U> attachments{};
    std::array<std::uint8_t, 6U> states{};
    std::array<std::uint8_t, 2U> categories{};
    std::array<std::uint8_t, 2U> syllables{};
    auto options = open_type_shape_run_options{
        open_type_tag::from_chars('m', 'y', 'm', 'r')};
    options.complex_script = open_type_complex_script::myanmar;
    std::uint32_t glyph_count = 0U;
    require(try_shape_open_type_run(
        font,
        input,
        options,
        glyphs,
        open_type_shape_run_scratch{
            .grapheme_clusters = graphemes,
            .attachments = attachments,
            .attachment_states = states,
            .script_categories = categories,
            .script_syllables = syllables},
        glyph_count,
        &error));
    require(glyph_count == 2U && glyphs[0U].code_point == 0x1031U &&
        glyphs[1U].code_point == 0x1000U && glyphs[0U].cluster == 0 &&
        glyphs[1U].cluster == 0);
    constexpr std::uint32_t public_flags =
        static_cast<std::uint32_t>(shaping_glyph_flags::unsafe_to_break) |
        static_cast<std::uint32_t>(shaping_glyph_flags::unsafe_to_concat) |
        static_cast<std::uint32_t>(
            shaping_glyph_flags::safe_to_insert_tatweel);
    require((static_cast<std::uint32_t>(glyphs[0U].flags) & ~public_flags) ==
        0U);
    require((static_cast<std::uint32_t>(glyphs[1U].flags) & ~public_flags) ==
        0U);
}

void open_type_use_preparation_reorders_prebase_vowels() {
    require(get_unicode_use_shaping_category(0x0D9AU) == 1U);
    require(get_unicode_use_shaping_category(0x0DD9U) == 22U);
    constexpr std::array mappings{
        std::pair{0x0D9AU, 2U},
        std::pair{0x0DD9U, 3U},
        std::pair{0x25CCU, 1U}};
    const auto cmap = make_cmap_groups(mappings);
    const auto data = make_font(
        0U, 22U, 0U, false, false, false, {}, cmap);
    sfnt_font_view font{};
    font_error error = font_error::none;
    require(sfnt_font_view::try_create(data, 0U, font, &error));
    const std::array<unicode_scalar, 2U> input{
        unicode_scalar{0x0D9AU, 0U, 1U},
        unicode_scalar{0x0DD9U, 1U, 1U}};
    std::array<shaping_glyph, 6U> glyphs{};
    std::array<unicode_grapheme_cluster, 2U> graphemes{};
    std::array<shaping_attachment, 6U> attachments{};
    std::array<std::uint8_t, 6U> states{};
    std::array<std::uint8_t, 2U> categories{};
    std::array<std::uint8_t, 2U> syllables{};
    std::array<std::uint32_t, 3U> indices{};
    auto options = open_type_shape_run_options{
        open_type_tag::from_chars('s', 'i', 'n', 'h')};
    options.complex_script = open_type_complex_script::use;
    std::uint32_t glyph_count = 0U;
    require(try_shape_open_type_run(
        font,
        input,
        options,
        glyphs,
        open_type_shape_run_scratch{
            .grapheme_clusters = graphemes,
            .attachments = attachments,
            .attachment_states = states,
            .script_categories = categories,
            .script_syllables = syllables,
            .script_indices = indices},
        glyph_count,
        &error));
    require(glyph_count == 2U && glyphs[0U].code_point == 0x0DD9U &&
        glyphs[1U].code_point == 0x0D9AU && glyphs[0U].cluster == 0 &&
        glyphs[1U].cluster == 0);
}

void open_type_indic_preparation_reorders_prebase_matras() {
    const auto consonant_properties =
        get_unicode_indic_shaping_properties(0x0915U);
    const auto matra_properties =
        get_unicode_indic_shaping_properties(0x093FU);
    require(consonant_properties.category == 1U &&
        matra_properties.position == 2U);
    constexpr std::array mappings{
        std::pair{0x0915U, 2U},
        std::pair{0x093FU, 3U},
        std::pair{0x25CCU, 1U}};
    const auto cmap = make_cmap_groups(mappings);
    const auto data = make_font(
        0U, 22U, 0U, false, false, false, {}, cmap);
    sfnt_font_view font{};
    font_error error = font_error::none;
    require(sfnt_font_view::try_create(data, 0U, font, &error));
    const std::array<unicode_scalar, 2U> input{
        unicode_scalar{0x0915U, 0U, 1U},
        unicode_scalar{0x093FU, 1U, 1U}};
    std::array<shaping_glyph, 6U> glyphs{};
    std::array<unicode_grapheme_cluster, 2U> graphemes{};
    std::array<shaping_attachment, 6U> attachments{};
    std::array<std::uint8_t, 6U> states{};
    std::array<std::uint8_t, 2U> categories{};
    std::array<std::uint8_t, 2U> syllables{};
    auto options = open_type_shape_run_options{
        open_type_tag::from_chars('d', 'e', 'v', '2')};
    options.complex_script = open_type_complex_script::indic;
    std::uint32_t glyph_count = 0U;
    require(try_shape_open_type_run(
        font,
        input,
        options,
        glyphs,
        open_type_shape_run_scratch{
            .grapheme_clusters = graphemes,
            .attachments = attachments,
            .attachment_states = states,
            .script_categories = categories,
            .script_syllables = syllables},
        glyph_count,
        &error));
    require(glyph_count == 2U && glyphs[0U].code_point == 0x093FU &&
        glyphs[1U].code_point == 0x0915U && glyphs[0U].cluster == 0 &&
        glyphs[1U].cluster == 0);
}

void open_type_hangul_preparation_composes_and_decomposes() {
    std::ifstream stream(PROGPU_NATIVE_TEST_NOTO_CFF_FONT, std::ios::binary);
    require(stream.good());
    const std::vector<char> source{
        std::istreambuf_iterator<char>(stream),
        std::istreambuf_iterator<char>()};
    std::vector<std::byte> data(source.size());
    for (std::size_t index = 0U; index < source.size(); ++index) {
        data[index] = static_cast<std::byte>(source[index]);
    }
    sfnt_font_view font{};
    font_error error = font_error::none;
    require(sfnt_font_view::try_create(data, 0U, font, &error));

    std::uint16_t leading = 0U;
    std::uint16_t vowel = 0U;
    std::uint16_t syllable = 0U;
    require(font.try_get_glyph_index(0x1100U, leading));
    require(font.try_get_glyph_index(0x1161U, vowel));
    require(font.try_get_glyph_index(0xAC00U, syllable));
    require(leading != 0U && vowel != 0U && syllable != 0U);

    std::array<shaping_glyph, 6U> composed_storage{
        shaping_glyph{leading, 0x1100U, 2},
        shaping_glyph{vowel, 0x1161U, 3}};
    std::uint32_t glyph_count = 2U;
    require(try_prepare_open_type_hangul(
        font, composed_storage, glyph_count, &error));
    require(glyph_count == 1U && composed_storage[0U].glyph_id == syllable &&
        composed_storage[0U].code_point == 0xAC00U &&
        composed_storage[0U].cluster == 2);

    std::array<shaping_glyph, 3U> decomposed_storage{
        shaping_glyph{0U, 0xAC00U, 7}};
    glyph_count = 1U;
    require(try_prepare_open_type_hangul(
        font, decomposed_storage, glyph_count, &error));
    require(glyph_count == 2U &&
        decomposed_storage[0U].code_point == 0x1100U &&
        decomposed_storage[0U].glyph_id == leading &&
        decomposed_storage[1U].code_point == 0x1161U &&
        decomposed_storage[1U].glyph_id == vowel &&
        decomposed_storage[1U].cluster == 7);

    const auto before = composed_storage;
    glyph_count = 2U;
    require(!try_prepare_open_type_hangul(
        font,
        std::span<shaping_glyph>{composed_storage}.first(2U),
        glyph_count,
        &error));
    require(error == font_error::insufficient_buffer && glyph_count == 2U &&
        composed_storage[0U].glyph_id == before[0U].glyph_id &&
        composed_storage[0U].code_point == before[0U].code_point &&
        composed_storage[1U].glyph_id == before[1U].glyph_id &&
        composed_storage[1U].code_point == before[1U].code_point);
}

void open_type_gpos_device_and_variation_deltas_are_applied() {
    const auto make_device_gpos = [](std::uint16_t first,
                                      std::uint16_t second,
                                      std::uint16_t format,
                                      std::uint16_t packed) {
        std::vector<std::byte> table(52U);
        write_u16(table, 0U, 1U);
        write_u16(table, 4U, 10U);
        write_u16(table, 6U, 12U);
        write_u16(table, 8U, 14U);
        write_u16(table, 14U, 1U);
        write_u16(table, 16U, 4U);
        write_u16(table, 18U, 1U);
        write_u16(table, 22U, 1U);
        write_u16(table, 24U, 8U);
        write_u16(table, 26U, 1U);
        write_u16(table, 28U, 10U);
        write_u16(table, 30U, 0x0040U);
        write_u16(table, 32U, 18U);
        write_u16(table, 36U, 1U);
        write_u16(table, 38U, 1U);
        write_u16(table, 40U, 3U);
        write_u16(table, 44U, first);
        write_u16(table, 46U, second);
        write_u16(table, 48U, format);
        write_u16(table, 50U, packed);
        return table;
    };

    const auto device_gpos = make_device_gpos(20U, 20U, 3U, 0xFF00U);
    const std::array device_tables{table_data{
        open_type_tag::from_chars('G', 'P', 'O', 'S'), device_gpos}};
    const auto device_font_bytes = make_font(
        0U, 22U, 0U, false, false, false, device_tables);
    sfnt_font_view device_font{};
    font_error error = font_error::none;
    require(sfnt_font_view::try_create(
        device_font_bytes, 0U, device_font, &error));
    sfnt_table_view gpos_table{};
    require(device_font.try_get_table(
        open_type_tag::from_chars('G', 'P', 'O', 'S'), gpos_table));
    open_type_layout_table_view gpos{};
    require(open_type_layout_table_view::try_create(
        gpos_table.bytes, gpos, &error));
    std::array<shaping_glyph, 1U> glyphs{
        shaping_glyph{3U, 0U, 0, shaping_glyph_flags::none, 500}};
    bool applied = false;
    require(try_apply_open_type_gpos_lookup(
        gpos,
        0U,
        glyphs,
        open_type_gpos_apply_options{
            nullptr,
            progpu::native::text::shaping_direction::left_to_right,
            {},
            &device_font,
            {},
            20U,
            20U},
        applied,
        &error));
    require(applied && glyphs[0U].advance_x == 450);

    std::vector<std::byte> gdef(56U);
    write_u16(gdef, 0U, 1U);
    write_u16(gdef, 2U, 3U);
    write_u32(gdef, 14U, 18U);
    write_u16(gdef, 18U, 1U);
    write_u32(gdef, 20U, 12U);
    write_u16(gdef, 24U, 1U);
    write_u32(gdef, 26U, 28U);
    write_u16(gdef, 30U, 2U);
    write_u16(gdef, 32U, 1U);
    write_i16(gdef, 34U, 0);
    write_i16(gdef, 36U, 8192);
    write_i16(gdef, 38U, 16384);
    write_i16(gdef, 40U, 0);
    write_i16(gdef, 42U, 8192);
    write_i16(gdef, 44U, 16384);
    write_u16(gdef, 46U, 1U);
    write_u16(gdef, 48U, 1U);
    write_u16(gdef, 50U, 1U);
    write_u16(gdef, 52U, 0U);
    write_i16(gdef, 54U, 20);
    const auto variation_gpos =
        make_device_gpos(0U, 0U, 0x8000U, 0U);
    const std::array variation_tables{
        table_data{open_type_tag::from_chars('G', 'D', 'E', 'F'), gdef},
        table_data{
            open_type_tag::from_chars('G', 'P', 'O', 'S'), variation_gpos}};
    const auto variation_font_bytes = make_font(
        0U, 22U, 0U, true, false, false, variation_tables);
    sfnt_font_view variation_font{};
    require(sfnt_font_view::try_create(
        variation_font_bytes, 0U, variation_font, &error));
    require(variation_font.try_get_table(
        open_type_tag::from_chars('G', 'P', 'O', 'S'), gpos_table));
    require(open_type_layout_table_view::try_create(
        gpos_table.bytes, gpos, &error));
    glyphs[0U] = shaping_glyph{
        3U, 0U, 0, shaping_glyph_flags::none, 500};
    const std::array<std::int16_t, 2U> normalized{8192, 8192};
    require(try_apply_open_type_gpos_lookup(
        gpos,
        0U,
        glyphs,
        open_type_gpos_apply_options{
            nullptr,
            progpu::native::text::shaping_direction::left_to_right,
            {},
            &variation_font,
            normalized},
        applied,
        &error));
    require(applied && glyphs[0U].advance_x == 520);
}

void native_font_fallback_preserves_graphemes_and_missing_state() {
    const auto data = make_font();
    sfnt_font_view font{};
    font_error error = font_error::none;
    require(sfnt_font_view::try_create(data, 0U, font, &error));
    const std::array<unicode_scalar, 2U> input{
        unicode_scalar{0x41U, 0U, 1U},
        unicode_scalar{0x42U, 1U, 1U}};
    const std::array<unicode_grapheme_cluster, 2U> graphemes{
        unicode_grapheme_cluster{0U, 1U, 0U, 1U},
        unicode_grapheme_cluster{1U, 1U, 1U, 1U}};
    const std::array<font_fallback_candidate, 2U> candidates{
        font_fallback_candidate{nullptr, 10U},
        font_fallback_candidate{&font, 20U}};
    std::uint32_t run_count = 0U;
    require(try_get_font_fallback_run_count(
        input, graphemes, candidates, 0U, run_count, &error));
    require(run_count == 2U);
    std::array<font_fallback_run, 2U> runs{};
    std::uint32_t written = 0U;
    require(try_itemize_font_fallback(
        input, graphemes, candidates, 0U, runs, written, &error));
    require(written == 2U && runs[0U].font_index == 1U &&
        !runs[0U].has_missing_glyphs && runs[1U].font_index == 1U &&
        runs[1U].has_missing_glyphs && runs[1U].input_index == 1U);

    written = 99U;
    require(!try_itemize_font_fallback(
        input,
        graphemes,
        candidates,
        0U,
        std::span<font_fallback_run>{runs}.first(1U),
        written,
        &error));
    require(error == font_error::insufficient_buffer && written == 0U);
}

void native_font_provider_cache_is_borrowed_and_generation_safe() {
    constexpr std::array regular_mappings{std::pair{0x41U, 2U}};
    constexpr std::array bold_mappings{std::pair{0x41U, 3U}};
    const auto regular_cmap = make_cmap_groups(regular_mappings);
    const auto bold_cmap = make_cmap_groups(bold_mappings);
    const auto regular = make_font(
        0U, 22U, 0U, false, false, false, {}, regular_cmap);
    const auto bold = make_font(
        0U, 22U, 0U, false, false, false, {}, bold_cmap);
    struct provider_context final {
        std::array<font_provider_face, 2U> faces{};
        std::uint32_t reads = 0U;
    } context{
        std::array{
            font_provider_face{regular, 1U, 7U, 0U, 400U, 5U,
                font_provider_slant::normal},
            font_provider_face{bold, 2U, 7U, 0U, 700U, 5U,
                font_provider_slant::normal}}};
    const auto count = +[](void* value) noexcept -> std::uint32_t {
        return static_cast<std::uint32_t>(
            static_cast<provider_context*>(value)->faces.size());
    };
    const auto get = +[](void* value, std::uint32_t index,
                         font_provider_face& result) noexcept -> bool {
        auto& source = *static_cast<provider_context*>(value);
        ++source.reads;
        if (index >= source.faces.size()) {
            return false;
        }
        result = source.faces[index];
        return true;
    };
    font_provider_view provider{&context, 3U, count, get};
    std::array<font_provider_cache_entry, 4U> cache{};
    std::uint32_t cursor = 0U;
    font_provider_result result{};
    font_error error = font_error::invalid_argument;
    require(try_resolve_font_provider_face(
        provider, 7U, 650U, 5U, font_provider_slant::normal, 0x41U,
        cache, cursor, result, &error));
    require(result.found && result.provider_index == 1U &&
        result.face.identity == 2U && context.reads == 2U);
    require(try_resolve_font_provider_face(
        provider, 7U, 650U, 5U, font_provider_slant::normal, 0x41U,
        cache, cursor, result, &error));
    require(result.found && context.reads == 3U);

    require(try_resolve_font_provider_face(
        provider, 7U, 400U, 5U, font_provider_slant::normal, 0x2603U,
        cache, cursor, result, &error));
    require(!result.found && context.reads == 5U);
    require(try_resolve_font_provider_face(
        provider, 7U, 400U, 5U, font_provider_slant::normal, 0x2603U,
        cache, cursor, result, &error));
    require(!result.found && context.reads == 5U);

    provider.generation = 4U;
    require(try_resolve_font_provider_face(
        provider, 7U, 650U, 5U, font_provider_slant::normal, 0x41U,
        cache, cursor, result, &error));
    require(result.found && context.reads == 7U);
}

void native_positioned_text_layout_wraps_without_allocation() {
    const std::array<shaping_glyph, 4U> glyphs{
        shaping_glyph{1U, 0U, 0, shaping_glyph_flags::none, 10},
        shaping_glyph{2U, 1U, 1, shaping_glyph_flags::none, 10},
        shaping_glyph{3U, 2U, 2, shaping_glyph_flags::none, 10},
        shaping_glyph{4U, 3U, 3, shaping_glyph_flags::none, 10}};
    const std::array<text_line_break_kind, 4U> breaks{
        text_line_break_kind::prohibited,
        text_line_break_kind::opportunity,
        text_line_break_kind::prohibited,
        text_line_break_kind::opportunity};
    const text_layout_options options{
        1.0F,
        25.0F,
        12.0F,
        0U,
        progpu::native::text::shaping_direction::left_to_right};
    text_layout_requirements requirements{};
    font_error error = font_error::none;
    require(try_get_text_layout_requirements(
        glyphs, breaks, options, requirements, &error));
    require(requirements.glyph_capacity == 4U &&
        requirements.line_capacity == 2U);

    std::array<positioned_text_glyph, 4U> positioned{};
    std::array<positioned_text_line, 2U> lines{};
    std::uint32_t glyph_count = 0U;
    std::uint32_t line_count = 0U;
    require(try_layout_shaped_text(
        glyphs,
        breaks,
        options,
        positioned,
        lines,
        glyph_count,
        line_count,
        &error));
    require(glyph_count == 4U && line_count == 2U);
    require(positioned[0U].x == 0.0F && positioned[1U].x == 10.0F &&
        positioned[2U].x == 0.0F && positioned[2U].y == 12.0F);
    require(lines[0U].glyph_start == 0U && lines[0U].glyph_count == 2U &&
        lines[0U].width == 20.0F && !lines[0U].clipped);
    require(lines[1U].glyph_start == 2U && lines[1U].glyph_count == 2U &&
        lines[1U].baseline_y == 12.0F);

    const std::array<std::int32_t, 4U> cluster_ends{1, 2, 3, 4};
    const std::array<std::int8_t, 4U> bidi_levels{0, 0, 1, 1};
    text_interaction_requirements interaction_requirements{};
    require(try_get_text_interaction_requirements(
        positioned,
        lines,
        cluster_ends,
        bidi_levels,
        interaction_requirements,
        &error));
    require(interaction_requirements.cluster_box_capacity == 4U &&
        interaction_requirements.caret_stop_capacity == 8U);
    std::array<text_cluster_box, 4U> boxes{};
    std::array<text_caret_stop, 8U> carets{};
    std::uint32_t box_count = 0U;
    std::uint32_t caret_count = 0U;
    require(try_build_text_interaction(
        positioned,
        lines,
        cluster_ends,
        bidi_levels,
        boxes,
        carets,
        box_count,
        caret_count,
        &error));
    require(box_count == 4U && caret_count == 8U &&
        boxes[1U].input_start == 1 && boxes[1U].input_end == 2 &&
        boxes[2U].line_index == 1U && boxes[2U].bidi_level == 1);
    require(carets[4U].input_position == 3 && carets[4U].trailing &&
        carets[5U].input_position == 2 && !carets[5U].trailing);

    text_hit_test_result hit{};
    require(try_hit_test_text(boxes, 16.0F, 2.0F, hit, &error));
    require(hit.input_position == 2 && hit.trailing && hit.inside &&
        hit.line_index == 0U && hit.bounds.width == 10.0F);
    std::array<text_rectangle, 4U> selection{};
    std::uint32_t selection_count = 0U;
    require(try_get_text_selection_rectangles(
        boxes, 1, 3, selection, selection_count, &error));
    require(selection_count == 2U && selection[0U].x == 10.0F &&
        selection[0U].width == 10.0F && selection[1U].y == 12.0F);

    box_count = 99U;
    caret_count = 99U;
    require(!try_build_text_interaction(
        positioned,
        lines,
        cluster_ends,
        bidi_levels,
        std::span<text_cluster_box>{boxes}.first(3U),
        carets,
        box_count,
        caret_count,
        &error));
    require(error == font_error::insufficient_buffer &&
        box_count == 0U && caret_count == 0U);

    auto clustered = glyphs;
    clustered[1U].cluster = 0;
    const std::array<text_line_break_kind, 4U> clustered_breaks{
        text_line_break_kind::opportunity,
        text_line_break_kind::prohibited,
        text_line_break_kind::opportunity,
        text_line_break_kind::opportunity};
    const text_layout_options narrow_options{
        1.0F,
        15.0F,
        12.0F,
        0U,
        progpu::native::text::shaping_direction::left_to_right};
    require(try_get_text_layout_requirements(
        clustered, clustered_breaks, narrow_options, requirements, &error));
    require(requirements.line_capacity == 3U);

    std::array<positioned_text_line, 3U> narrow_lines{};
    require(try_layout_shaped_text(
        clustered,
        clustered_breaks,
        narrow_options,
        positioned,
        narrow_lines,
        glyph_count,
        line_count,
        &error));
    require(line_count == 3U && narrow_lines[0U].glyph_count == 2U);

    auto clipped_options = options;
    clipped_options.maximum_lines = 1U;
    positioned.fill(positioned_text_glyph{});
    lines.fill(positioned_text_line{});
    require(try_layout_shaped_text(
        glyphs,
        breaks,
        clipped_options,
        positioned,
        lines,
        glyph_count,
        line_count,
        &error));
    require(glyph_count == 2U && line_count == 1U && lines[0U].clipped);

    auto ellipsis_options = clipped_options;
    ellipsis_options.trimming = text_trimming::character_ellipsis;
    ellipsis_options.ellipsis_glyph_id = 99U;
    ellipsis_options.ellipsis_advance = 6.0F;
    require(try_get_text_layout_requirements(
        glyphs, breaks, ellipsis_options, requirements, &error));
    require(requirements.glyph_capacity == 4U &&
        requirements.line_capacity == 1U);
    std::array<positioned_text_glyph, 5U> ellipsized{};
    require(try_layout_shaped_text(
        glyphs,
        breaks,
        ellipsis_options,
        ellipsized,
        lines,
        glyph_count,
        line_count,
        &error));
    require(glyph_count == 2U && line_count == 1U && lines[0U].clipped &&
        lines[0U].glyph_count == 2U && lines[0U].width == 16.0F);
    require(ellipsized[0U].glyph_id == 1U &&
        ellipsized[1U].glyph_index ==
            std::numeric_limits<std::uint32_t>::max() &&
        ellipsized[1U].glyph_id == 99U && ellipsized[1U].x == 10.0F &&
        ellipsized[1U].advance_x == 6.0F);

    auto word_options = ellipsis_options;
    word_options.trimming = text_trimming::word_ellipsis;
    require(try_layout_shaped_text(
        glyphs,
        breaks,
        word_options,
        ellipsized,
        lines,
        glyph_count,
        line_count,
        &error));
    require(glyph_count == 2U && ellipsized[1U].glyph_id == 99U);

    glyph_count = 99U;
    line_count = 99U;
    require(!try_layout_shaped_text(
        glyphs,
        breaks,
        options,
        std::span<positioned_text_glyph>{positioned}.first(3U),
        lines,
        glyph_count,
        line_count,
        &error));
    require(error == font_error::insufficient_buffer &&
        glyph_count == 0U && line_count == 0U);
}

void unicode_line_breaks_feed_native_layout_without_allocation() {
    static_assert(sizeof(unicode_line_break_class) == 1U);
    static_assert(sizeof(text_line_break_kind) == 1U);
    require(get_unicode_line_break_class(0x20U) ==
        unicode_line_break_class::space);
    require(get_unicode_line_break_class(0x4E00U) ==
        unicode_line_break_class::ideographic);
    require(get_unicode_line_break_class(0x1F1E6U) ==
        unicode_line_break_class::regional_indicator);

    const std::array<unicode_scalar, 17U> input{
        unicode_scalar{0x41U}, unicode_scalar{0x20U},
        unicode_scalar{0x42U}, unicode_scalar{0x0DU},
        unicode_scalar{0x0AU}, unicode_scalar{0x43U},
        unicode_scalar{0x0301U}, unicode_scalar{0xA0U},
        unicode_scalar{0x44U}, unicode_scalar{0x4E00U},
        unicode_scalar{0x4E01U}, unicode_scalar{0x31U},
        unicode_scalar{0x2EU}, unicode_scalar{0x32U},
        unicode_scalar{0x1F1E6U}, unicode_scalar{0x1F1E7U},
        unicode_scalar{0x1F1E8U}};
    std::array<unicode_line_break_class, input.size()> classes{};
    std::array<text_line_break_kind, input.size()> breaks{};
    unicode_error error = unicode_error::none;
    require(try_resolve_unicode_line_breaks(
        input, classes, breaks, &error));
    require(breaks[0U] == text_line_break_kind::prohibited);
    require(breaks[1U] == text_line_break_kind::opportunity);
    require(breaks[3U] == text_line_break_kind::prohibited);
    require(breaks[4U] == text_line_break_kind::mandatory);
    require(breaks[5U] == text_line_break_kind::prohibited);
    require(breaks[6U] == text_line_break_kind::prohibited);
    require(breaks[8U] == text_line_break_kind::opportunity);
    require(breaks[9U] == text_line_break_kind::opportunity);
    require(breaks[11U] == text_line_break_kind::prohibited);
    require(breaks[12U] == text_line_break_kind::prohibited);
    require(breaks[14U] == text_line_break_kind::prohibited);
    require(breaks[15U] == text_line_break_kind::opportunity);
    require(breaks.back() == text_line_break_kind::mandatory);

    breaks.fill(text_line_break_kind::mandatory);
    require(!try_resolve_unicode_line_breaks(
        input,
        std::span<unicode_line_break_class>{classes}.first(input.size() - 1U),
        breaks,
        &error));
    require(error == unicode_error::insufficient_buffer);
    require(std::ranges::all_of(
        breaks,
        [](text_line_break_kind item) {
            return item == text_line_break_kind::mandatory;
        }));
}

void complex_script_properties_and_syllable_machines_are_bounded() {
    static_assert(sizeof(unicode_indic_shaping_properties) == 2U);
    static_assert(sizeof(unicode_syllable_transition) == 4U);

    const auto consonant = get_unicode_indic_shaping_properties(0x0915U);
    require(consonant.category == 1U && consonant.position == 4U);
    const auto matra = get_unicode_indic_shaping_properties(0x093FU);
    require(matra.category == 7U && matra.position == 2U);
    const auto halant = get_unicode_indic_shaping_properties(0x094DU);
    require(halant.category == 4U && halant.position == 8U);
    const auto dotted_circle =
        get_unicode_indic_shaping_properties(0x25CCU);
    require(dotted_circle.category == 11U && dotted_circle.position == 4U);
    const auto out_of_range =
        get_unicode_indic_shaping_properties(0x110000U);
    require(out_of_range.category == 0U && out_of_range.position == 14U);

    require(get_unicode_use_shaping_category(0x0915U) == 1U);
    require(get_unicode_use_shaping_category(0x093FU) == 22U);
    require(get_unicode_use_shaping_category(0x094DU) == 12U);
    require(get_unicode_use_shaping_category(0x1031U) == 22U);
    require(get_unicode_use_shaping_category(0x25CCU) == 1U);
    require(get_unicode_use_shaping_category(0x110000U) == 0U);

    struct machine_expectation final {
        unicode_syllable_machine machine;
        std::uint16_t state_count;
        std::uint16_t start_state;
        std::uint16_t target;
        std::uint8_t action;
    };
    constexpr std::array<machine_expectation, 4U> expectations{{
        {unicode_syllable_machine::indic, 138U, 31U, 32U, 2U},
        {unicode_syllable_machine::use, 127U, 1U, 31U, 0U},
        {unicode_syllable_machine::myanmar, 53U, 0U, 1U, 0U},
        {unicode_syllable_machine::khmer, 43U, 21U, 22U, 2U},
    }};
    for (const auto& expected : expectations) {
        require(get_unicode_syllable_machine_state_count(expected.machine) ==
            expected.state_count);
        require(get_unicode_syllable_machine_start_state(expected.machine) ==
            expected.start_state);
        unicode_syllable_transition transition{99U, 99U, 99U};
        require(try_get_unicode_syllable_transition(
            expected.machine, expected.start_state, 1U, transition));
        require(transition.target == expected.target &&
            transition.action == expected.action &&
            transition.reserved == 0U);

        transition = {99U, 99U, 99U};
        require(!try_get_unicode_syllable_transition(
            expected.machine, expected.state_count, 1U, transition));
        require(transition.target == 0U && transition.action == 0U &&
            transition.reserved == 0U);

        transition = {99U, 99U, 99U};
        require(!try_get_unicode_syllable_eof_transition(
            expected.machine, expected.state_count, transition));
        require(transition.target == 0U && transition.action == 0U &&
            transition.reserved == 0U);
    }

    constexpr std::array<std::uint8_t, 1U> consonant_category{1U};
    std::array<std::uint8_t, 1U> assigned{0U};
    require(try_assign_unicode_syllables(
        unicode_syllable_machine::indic, consonant_category, {}, assigned));
    require(assigned[0U] == 0x10U);
    require(try_assign_unicode_syllables(
        unicode_syllable_machine::myanmar, consonant_category, {}, assigned));
    require(assigned[0U] == 0x10U);
    require(try_assign_unicode_syllables(
        unicode_syllable_machine::khmer, consonant_category, {}, assigned));
    require(assigned[0U] == 0x10U);
    require(try_assign_unicode_syllables(
        unicode_syllable_machine::use, consonant_category, {}, assigned));
    require(assigned[0U] == 0x12U);

    constexpr std::array<std::uint8_t, 3U> filtered_categories{
        1U, 6U, 22U};
    constexpr std::array<std::uint32_t, 3U> filtered_indices{0U, 2U, 3U};
    std::array<std::uint8_t, 3U> filtered_assigned{};
    require(try_assign_unicode_syllables(
        unicode_syllable_machine::use,
        filtered_categories,
        filtered_indices,
        filtered_assigned));
    require(filtered_assigned ==
        std::array<std::uint8_t, 3U>{0x12U, 0x12U, 0x12U});

    constexpr std::array<std::uint32_t, 3U> invalid_indices{0U, 0U, 3U};
    filtered_assigned.fill(99U);
    require(!try_assign_unicode_syllables(
        unicode_syllable_machine::use,
        filtered_categories,
        invalid_indices,
        filtered_assigned));
    require(filtered_assigned ==
        std::array<std::uint8_t, 3U>{99U, 99U, 99U});
}

void variation_axes_are_borrowed_bounded_and_transactional() {
    const auto data = make_font(0U, 22U, 0U, true);
    sfnt_font_view font{};
    require(sfnt_font_view::try_create(data, 0U, font));
    std::uint16_t count = 0U;
    require(font.try_get_variation_axis_count(count));
    require(count == 2U);
    std::array<sfnt_variation_axis, 1U> short_axes{};
    std::uint16_t written = 99U;
    font_error error = font_error::none;
    require(!font.try_decode_variation_axes(short_axes, written, &error));
    require(error == font_error::insufficient_buffer);
    require(written == 0U);
    require(short_axes[0].tag.value == 0U);

    std::array<sfnt_variation_axis, 2U> axes{};
    require(font.try_decode_variation_axes(axes, written, &error));
    require(error == font_error::none && written == 2U);
    require(axes[0].tag ==
        open_type_tag::from_chars('o', 'p', 's', 'z'));
    require(axes[0].minimum() == 14.0F);
    require(axes[0].default_value() == 14.0F);
    require(axes[0].maximum() == 32.0F);
    require(axes[0].hidden());
    require(axes[0].name_id == 256U);
    require(axes[1].tag ==
        open_type_tag::from_chars('w', 'g', 'h', 't'));
    require(axes[1].minimum() == 100.0F);
    require(axes[1].default_value() == 400.0F);
    require(axes[1].maximum() == 900.0F);
    require(!axes[1].hidden());
    require(axes[1].name_id == 257U);

    auto truncated = data;
    const auto table_count = static_cast<std::size_t>(
        (std::to_integer<std::uint16_t>(truncated[4U]) << 8U) |
        std::to_integer<std::uint16_t>(truncated[5U]));
    const auto fvar_record = 12U + (table_count - 1U) * 16U;
    write_u32(truncated, fvar_record + 12U, 20U);
    require(sfnt_font_view::try_create(truncated, 0U, font));
    require(!font.try_get_variation_axis_count(count, &error));
    require(error == font_error::invalid_face && count == 0U);
}

void variation_coordinates_apply_bounded_avar_mapping() {
    const auto data = make_font(0U, 22U, 0U, true, true);
    sfnt_font_view font{};
    require(sfnt_font_view::try_create(data, 0U, font));
    std::int16_t normalized = 99;
    font_error error = font_error::none;
    require(font.try_normalize_variation_coordinate(
        0U, 23 * 65536, normalized, &error));
    require(error == font_error::none && normalized == 8192);
    require(font.try_normalize_variation_coordinate(
        1U, 500 * 65536, normalized, &error));
    require(normalized == 2949);
    require(font.try_normalize_variation_coordinate(
        1U, 700 * 65536, normalized, &error));
    require(normalized == 8847);
    require(font.try_normalize_variation_coordinate(
        1U, 1000 * 65536, normalized, &error));
    require(normalized == 16384);
    require(!font.try_normalize_variation_coordinate(
        2U, 0, normalized, &error));
    require(error == font_error::invalid_argument && normalized == 0);

    auto truncated = data;
    truncated.pop_back();
    require(sfnt_font_view::try_create(truncated, 0U, font));
    require(font.try_normalize_variation_coordinate(
        1U, 700 * 65536, normalized, &error));
    require(error == font_error::none && normalized == 9830);
}

void packed_variation_streams_are_transactional_and_exact() {
    const std::array point_bytes{
        std::byte{4U},
        std::byte{3U},
        std::byte{1U},
        std::byte{2U},
        std::byte{0U},
        std::byte{5U}};
    sfnt_packed_point_requirements point_requirements{};
    font_error error = font_error::none;
    require(sfnt_packed_variation_data::try_get_point_requirements(
        point_bytes, point_requirements, &error));
    require(point_requirements.point_count == 4U);
    require(point_requirements.bytes_consumed == point_bytes.size());
    require(!point_requirements.all_points);
    std::array<std::uint32_t, 3U> short_points{};
    std::uint32_t written = 99U;
    std::size_t consumed = 99U;
    require(!sfnt_packed_variation_data::try_decode_points(
        point_bytes, short_points, written, consumed, &error));
    require(error == font_error::insufficient_buffer);
    require(written == 0U && consumed == 0U);
    require(short_points[0] == 0U);
    std::array<std::uint32_t, 4U> points{};
    require(sfnt_packed_variation_data::try_decode_points(
        point_bytes, points, written, consumed, &error));
    require(written == 4U && consumed == point_bytes.size());
    require(points == std::array<std::uint32_t, 4U>{1U, 3U, 3U, 8U});

    const std::array all_points{std::byte{0U}};
    require(sfnt_packed_variation_data::try_get_point_requirements(
        all_points, point_requirements, &error));
    require(point_requirements.all_points);
    require(point_requirements.point_count == 0U);
    require(point_requirements.bytes_consumed == 1U);

    const std::array delta_bytes{
        std::byte{0x81U},
        std::byte{0x41U},
        std::byte{0x00U},
        std::byte{0x64U},
        std::byte{0xffU},
        std::byte{0xfeU},
        std::byte{0x01U},
        std::byte{0x03U},
        std::byte{0xfcU}};
    sfnt_packed_delta_requirements delta_requirements{};
    require(sfnt_packed_variation_data::try_get_delta_requirements(
        delta_bytes, 6U, delta_requirements, &error));
    require(delta_requirements.delta_count == 6U);
    require(delta_requirements.bytes_consumed == delta_bytes.size());
    std::array<std::int16_t, 6U> deltas{};
    require(sfnt_packed_variation_data::try_decode_deltas(
        delta_bytes,
        deltas,
        6U,
        written,
        consumed,
        &error));
    require(written == 6U && consumed == delta_bytes.size());
    require(deltas == std::array<std::int16_t, 6U>{0, 0, 100, -2, 3, -4});

    const std::array invalid_points{
        std::byte{2U}, std::byte{2U}, std::byte{1U}};
    require(!sfnt_packed_variation_data::try_get_point_requirements(
        invalid_points, point_requirements, &error));
    require(error == font_error::invalid_glyph);
    const std::array invalid_deltas{std::byte{0x03U}, std::byte{1U}};
    require(!sfnt_packed_variation_data::try_get_delta_requirements(
        invalid_deltas, 2U, delta_requirements, &error));
    require(error == font_error::invalid_glyph);
}

void glyph_variation_tuple_headers_are_bounded_and_exact() {
    const auto data = make_font(0U, 22U, 0U, true, true, true);
    sfnt_font_view font{};
    require(sfnt_font_view::try_create(data, 0U, font));
    sfnt_gvar_tuple_requirements requirements{};
    font_error error = font_error::none;
    require(font.try_get_glyph_variation_tuple_requirements(
        4U, requirements, &error));
    require(error == font_error::none);
    require(requirements.tuple_count == 1U);
    require(requirements.region_coordinate_count == 6U);
    std::array<sfnt_gvar_tuple_header, 1U> headers{};
    std::array<std::int16_t, 5U> short_coordinates{};
    std::uint16_t headers_written = 99U;
    std::uint32_t coordinates_written = 99U;
    require(!font.try_decode_glyph_variation_tuple_headers(
        4U,
        headers,
        short_coordinates,
        headers_written,
        coordinates_written,
        &error));
    require(error == font_error::insufficient_buffer);
    require(headers_written == 0U && coordinates_written == 0U);
    require(headers[0].flags == 0U && short_coordinates[0] == 0);
    std::array<std::int16_t, 6U> coordinates{};
    require(font.try_decode_glyph_variation_tuple_headers(
        4U,
        headers,
        coordinates,
        headers_written,
        coordinates_written,
        &error));
    require(headers_written == 1U && coordinates_written == 6U);
    require(headers[0].serialized_data_size == 10U);
    require(headers[0].flags == 0xE000U);
    require(headers[0].has_private_point_numbers());
    require(coordinates ==
        std::array<std::int16_t, 6U>{0, -8192, 8192, -4096, 16384, 0});
    const std::array<std::int16_t, 2U> rising{4096, -4096};
    require(sfnt_gvar_tuple_data::calculate_scalar(rising, coordinates) ==
        0.5F);
    const std::array<std::int16_t, 2U> falling{8192, -2048};
    require(sfnt_gvar_tuple_data::calculate_scalar(falling, coordinates) ==
        0.5F);
    const std::array<std::int16_t, 2U> outside{8192, 4096};
    require(sfnt_gvar_tuple_data::calculate_scalar(outside, coordinates) ==
        0.0F);
}

void untouched_glyph_deltas_interpolate_without_allocation() {
    const std::array<progpu_native_point, 5U> points{{
        {0.0F, 0.0F},
        {10.0F, 10.0F},
        {20.0F, 20.0F},
        {30.0F, 30.0F},
        {40.0F, 40.0F}}};
    const std::array<std::uint16_t, 1U> contour_ends{4U};
    std::array<float, 5U> x{0.0F, 2.0F, 0.0F, 6.0F, 0.0F};
    std::array<float, 5U> y{0.0F, 10.0F, 0.0F, 20.0F, 0.0F};
    const std::array<std::uint8_t, 5U> touched{0U, 1U, 0U, 1U, 0U};
    font_error error = font_error::none;
    require(sfnt_gvar_deltas::try_infer_untouched(
        points, contour_ends, x, y, touched, &error));
    require(error == font_error::none);
    require(x == std::array<float, 5U>{2.0F, 2.0F, 4.0F, 6.0F, 6.0F});
    require(y ==
        std::array<float, 5U>{10.0F, 10.0F, 15.0F, 20.0F, 20.0F});

    x = {0.0F, 0.0F, 7.0F, 0.0F, 0.0F};
    y = {0.0F, 0.0F, -3.0F, 0.0F, 0.0F};
    const std::array<std::uint8_t, 5U> one_touched{0U, 0U, 1U, 0U, 0U};
    require(sfnt_gvar_deltas::try_infer_untouched(
        points, contour_ends, x, y, one_touched, &error));
    require(x == std::array<float, 5U>{7.0F, 7.0F, 7.0F, 7.0F, 7.0F});
    require(y ==
        std::array<float, 5U>{-3.0F, -3.0F, -3.0F, -3.0F, -3.0F});

    const std::array<std::uint16_t, 1U> invalid_contour{5U};
    const auto original_x = x;
    require(!sfnt_gvar_deltas::try_infer_untouched(
        points, invalid_contour, x, y, one_touched, &error));
    require(error == font_error::invalid_glyph && x == original_x);
    std::array<float, 4U> short_x{};
    require(!sfnt_gvar_deltas::try_infer_untouched(
        points, contour_ends, short_x, y, one_touched, &error));
    require(error == font_error::insufficient_buffer);
}

void simple_glyph_variations_apply_packed_tuple_deltas() {
    const auto data = make_font(0U, 22U, 0U, true, true, true);
    sfnt_font_view font{};
    require(sfnt_font_view::try_create(data, 0U, font));
    sfnt_glyph_decode_requirements glyph_requirements{};
    require(font.try_get_glyph_decode_requirements(
        4U, glyph_requirements));
    require(glyph_requirements.point_count == 3U);
    std::array<std::uint16_t, 1U> contour_ends{};
    std::array<sfnt_outline_point, 3U> original_points{};
    require(font.try_decode_simple_glyph(
        4U, contour_ends, original_points));

    sfnt_simple_glyph_variation_requirements requirements{};
    require(font.try_get_simple_glyph_variation_requirements(
        4U, 3U, requirements));
    require(requirements.tuple_header_count == 1U);
    require(requirements.region_coordinate_count == 6U);
    require(requirements.point_number_count == 7U);
    require(requirements.delta_count == 7U);
    require(requirements.tuple_point_count == 3U);

    std::array<sfnt_gvar_tuple_header, 1U> headers{};
    std::array<std::int16_t, 6U> regions{};
    std::array<std::uint32_t, 7U> shared_points{};
    std::array<std::uint32_t, 7U> private_points{};
    std::array<std::int16_t, 7U> x_deltas{};
    std::array<std::int16_t, 7U> y_deltas{};
    std::array<float, 3U> tuple_x{};
    std::array<float, 3U> tuple_y{};
    std::array<std::uint8_t, 3U> touched{};
    sfnt_simple_glyph_variation_scratch scratch{
        headers,
        regions,
        shared_points,
        private_points,
        x_deltas,
        y_deltas,
        tuple_x,
        tuple_y,
        touched};
    const std::array<std::int16_t, 2U> normalized{4096, -4096};
    std::array<progpu_native_point, 3U> varied{};
    font_error error = font_error::none;
    require(font.try_apply_simple_glyph_variations(
        4U,
        normalized,
        contour_ends,
        original_points,
        varied,
        scratch,
        &error));
    require(error == font_error::none);
    require(varied[0].x == 11.0F && varied[0].y == 0.0F);
    require(varied[1].x == 30.0F && varied[1].y == 30.0F);
    require(varied[2].x == 25.0F && varied[2].y == 40.0F);

    std::array<progpu_native_point, 2U> short_varied{{
        {99.0F, 99.0F}, {99.0F, 99.0F}}};
    require(!font.try_apply_simple_glyph_variations(
        4U,
        normalized,
        contour_ends,
        original_points,
        short_varied,
        scratch,
        &error));
    require(error == font_error::insufficient_buffer);
    require(short_varied[0].x == 99.0F && short_varied[0].y == 99.0F);
}

void composite_glyph_variations_apply_component_offsets() {
    const auto data = make_font(0U, 22U, 0U, true, true, true);
    sfnt_font_view font{};
    require(sfnt_font_view::try_create(data, 0U, font));
    sfnt_composite_glyph_variation_requirements requirements{};
    require(font.try_get_composite_glyph_variation_requirements(
        4U, 3U, requirements));
    require(requirements.tuple_header_count == 1U);
    require(requirements.region_coordinate_count == 6U);
    require(requirements.point_number_count == 7U);
    require(requirements.delta_count == 7U);

    std::array<sfnt_gvar_tuple_header, 1U> headers{};
    std::array<std::int16_t, 6U> regions{};
    std::array<std::uint32_t, 7U> shared_points{};
    std::array<std::uint32_t, 7U> private_points{};
    std::array<std::int16_t, 7U> x_deltas{};
    std::array<std::int16_t, 7U> y_deltas{};
    sfnt_composite_glyph_variation_scratch scratch{
        headers,
        regions,
        shared_points,
        private_points,
        x_deltas,
        y_deltas};
    const std::array<std::int16_t, 2U> normalized{4096, -4096};
    std::array<progpu_native_point, 3U> offsets{{
        {99.0F, 99.0F}, {99.0F, 99.0F}, {99.0F, 99.0F}}};
    font_error error = font_error::none;
    require(font.try_get_composite_glyph_variation_offsets(
        4U,
        normalized,
        3U,
        offsets,
        scratch,
        &error));
    require(error == font_error::none);
    require(offsets[0].x == 1.0F && offsets[0].y == 0.0F);
    require(offsets[1].x == 0.0F && offsets[1].y == 0.0F);
    require(offsets[2].x == 0.0F && offsets[2].y == 0.0F);

    std::array<progpu_native_point, 2U> short_offsets{{
        {99.0F, 99.0F}, {99.0F, 99.0F}}};
    require(!font.try_get_composite_glyph_variation_offsets(
        4U,
        normalized,
        3U,
        short_offsets,
        scratch,
        &error));
    require(error == font_error::insufficient_buffer);
    require(short_offsets[0].x == 99.0F && short_offsets[0].y == 99.0F);
}

void phantom_glyph_variations_apply_advance_delta() {
    const auto data = make_font(0U, 22U, 0U, true, true, true);
    sfnt_font_view font{};
    require(sfnt_font_view::try_create(data, 0U, font));
    sfnt_glyph_phantom_variation_requirements requirements{};
    require(font.try_get_glyph_phantom_variation_requirements(
        4U, 7U, requirements));
    require(requirements.tuple_header_count == 1U);
    require(requirements.region_coordinate_count == 6U);
    require(requirements.point_number_count == 7U);
    require(requirements.delta_count == 7U);

    std::array<sfnt_gvar_tuple_header, 1U> headers{};
    std::array<std::int16_t, 6U> regions{};
    std::array<std::uint32_t, 7U> shared_points{};
    std::array<std::uint32_t, 7U> private_points{};
    std::array<std::int16_t, 7U> x_deltas{};
    std::array<std::int16_t, 7U> y_deltas{};
    sfnt_glyph_phantom_variation_scratch scratch{
        headers,
        regions,
        shared_points,
        private_points,
        x_deltas,
        y_deltas};
    const std::array<std::int16_t, 2U> normalized{4096, -4096};
    float delta = 99.0F;
    font_error error = font_error::none;
    require(font.try_get_glyph_phantom_advance_delta(
        4U, normalized, 7U, delta, scratch, &error));
    require(error == font_error::none && delta == 3.0F);

    delta = 99.0F;
    require(font.try_get_glyph_phantom_advance_delta(
        4U, normalized, 3U, delta, {}, &error));
    require(error == font_error::none && delta == 0.0F);

    auto short_scratch = scratch;
    short_scratch.x_deltas = std::span<std::int16_t>{x_deltas}.first(6U);
    delta = 99.0F;
    require(!font.try_get_glyph_phantom_advance_delta(
        4U, normalized, 7U, delta, short_scratch, &error));
    require(error == font_error::insufficient_buffer && delta == 0.0F);

    bool uses_hvar = true;
    delta = 99.0F;
    require(font.try_get_horizontal_advance_variation(
        4U, normalized, delta, uses_hvar, &error));
    require(error == font_error::none && !uses_hvar && delta == 0.0F);
}

void item_variation_store_and_index_map_are_bounded() {
    std::vector<std::byte> data(46U);
    write_u16(data, 0U, 1U);
    write_u32(data, 2U, 12U);
    write_u16(data, 6U, 1U);
    write_u32(data, 8U, 28U);
    write_u16(data, 12U, 1U);
    write_u16(data, 14U, 1U);
    write_i16(data, 16U, 0);
    write_i16(data, 18U, 8192);
    write_i16(data, 20U, 16384);
    write_u16(data, 28U, 2U);
    write_u16(data, 30U, 1U);
    write_u16(data, 32U, 1U);
    write_u16(data, 34U, 0U);
    write_i16(data, 36U, 20);
    write_i16(data, 38U, -10);
    data[40U] = std::byte{0U};
    data[41U] = std::byte{0U};
    write_u16(data, 42U, 2U);
    data[44U] = std::byte{0U};
    data[45U] = std::byte{1U};

    sfnt_item_variation_store_view store{};
    font_error error = font_error::none;
    require(sfnt_item_variation_data::try_get_store(
        data, 0U, 1U, store, &error));
    require(error == font_error::none);
    const std::array<std::int16_t, 1U> normalized{8192};
    float delta = 99.0F;
    require(sfnt_item_variation_data::try_get_delta(
        store, normalized, 0U, 0U, delta, &error));
    require(delta == 20.0F);
    require(sfnt_item_variation_data::try_get_delta(
        store, normalized, 0U, 1U, delta, &error));
    require(delta == -10.0F);

    sfnt_delta_set_index_map_view map{};
    require(sfnt_item_variation_data::try_get_delta_set_index_map(
        data, 40U, map, &error));
    std::uint16_t outer = 99U;
    std::uint16_t inner = 99U;
    sfnt_item_variation_data::get_delta_set_index(
        map, 1U, outer, inner);
    require(outer == 0U && inner == 1U);
    sfnt_item_variation_data::get_delta_set_index(
        map, 99U, outer, inner);
    require(outer == 0U && inner == 1U);

    auto truncated = data;
    truncated.resize(38U);
    require(!sfnt_item_variation_data::try_get_store(
        truncated, 0U, 1U, store, &error));
    require(error == font_error::invalid_face);
}

void borrowed_sfnt_view_reads_tables_metrics_and_cmap() {
    const auto data = make_font();
    sfnt_font_view font{};
    font_error error = font_error::invalid_argument;
    require(sfnt_font_view::try_create(data, 0U, font, &error));
    require(error == font_error::none);
    require(font.face_index() == 0U);
    require(font.face_offset() == 0U);
    require(font.table_count() == 7U);
    require(!font.uses_symbol_character_map());
    require(font.data().data() == data.data());

    sfnt_table_view cmap{};
    require(font.try_get_table(
        open_type_tag::from_chars('c', 'm', 'a', 'p'), cmap));
    require(cmap.bytes.size() == 80U);
    require(cmap.checksum == 0x1006U);

    sfnt_header_metrics head{};
    require(font.try_get_header_metrics(head));
    require(head.units_per_em == 1000U);
    require(head.x_min == -20 && head.y_min == -200);
    require(head.x_max == 900 && head.y_max == 800);
    require(head.index_to_loc_format == 1);

    sfnt_horizontal_header_metrics horizontal{};
    require(font.try_get_horizontal_header_metrics(horizontal));
    require(horizontal.ascender == 800);
    require(horizontal.descender == -200);
    require(horizontal.line_gap == 40);
    require(horizontal.advance_width_max == 1200U);
    require(horizontal.number_of_horizontal_metrics == 2U);

    sfnt_horizontal_glyph_metrics glyph_metrics{};
    require(font.try_get_horizontal_glyph_metrics(3U, glyph_metrics));
    require(glyph_metrics.advance_width == 600U);
    require(glyph_metrics.left_side_bearing == 30);
    std::uint16_t glyph_count = 0U;
    require(font.try_get_glyph_count(glyph_count));
    require(glyph_count == 8U);

    sfnt_glyph_data_view empty_glyph{};
    require(font.try_get_glyph_data(3U, empty_glyph));
    require(empty_glyph.empty());
    sfnt_glyph_data_view glyph_data{};
    require(font.try_get_glyph_data(4U, glyph_data));
    require(!glyph_data.empty());
    require(glyph_data.bytes.size() == 22U);
    require(glyph_data.contour_count == 1);
    require(glyph_data.x_min == 10 && glyph_data.y_min == 0);
    require(glyph_data.x_max == 30 && glyph_data.y_max == 40);

    sfnt_glyph_decode_requirements requirements{};
    require(font.try_get_glyph_decode_requirements(
        4U, requirements, &error));
    require(error == font_error::none);
    require(requirements.kind == sfnt_glyph_kind::simple);
    require(requirements.contour_count == 1U);
    require(requirements.point_count == 3U);
    require(requirements.path_segment_count == 2U);
    require(requirements.instruction_bytes == 0U);
    std::array<std::uint16_t, 1U> contour_ends{};
    std::array<sfnt_outline_point, 3U> outline_points{};
    require(font.try_decode_simple_glyph(
        4U, contour_ends, outline_points, &error));
    require(contour_ends[0] == 2U);
    require(outline_points[0].x == 10 && outline_points[0].y == 0);
    require(outline_points[0].on_curve());
    require(outline_points[1].x == 30 && outline_points[1].y == 30);
    require(outline_points[1].on_curve());
    require(outline_points[2].x == 25 && outline_points[2].y == 40);
    require(!outline_points[2].on_curve());
    std::uint32_t segment_count = 0U;
    require(sfnt_simple_glyph_path::try_get_segment_count(
        contour_ends, outline_points, segment_count, &error));
    require(segment_count == 2U);
    std::array<progpu_native_path_segment, 2U> path_segments{};
    std::uint32_t written = 0U;
    require(sfnt_simple_glyph_path::try_write_segments(
        contour_ends, outline_points, path_segments, written, &error));
    require(written == 2U);
    require(path_segments[0].kind == PROGPU_NATIVE_PATH_SEGMENT_LINE);
    require(path_segments[0].p0.x == 10.0F &&
        path_segments[0].p0.y == 0.0F);
    require(path_segments[0].p1.x == 30.0F &&
        path_segments[0].p1.y == 30.0F);
    require(path_segments[1].kind == PROGPU_NATIVE_PATH_SEGMENT_QUADRATIC);
    require(path_segments[1].p0.x == 30.0F &&
        path_segments[1].p0.y == 30.0F);
    require(path_segments[1].p1.x == 25.0F &&
        path_segments[1].p1.y == 40.0F);
    require(path_segments[1].p2.x == 10.0F &&
        path_segments[1].p2.y == 0.0F);
    require(!font.try_decode_simple_glyph(
        4U,
        contour_ends,
        std::span<sfnt_outline_point>{},
        &error));
    require(error == font_error::insufficient_buffer);

    std::uint16_t glyph = 0U;
    require(font.try_get_glyph_index(0x41U, glyph));
    require(glyph == 3U);
    require(font.try_get_glyph_index(0x1F600U, glyph));
    require(glyph == 7U);
    require(font.try_get_glyph_index(0x42U, glyph));
    require(glyph == 0U);
}

void variation_selector_cmap_is_borrowed_and_bounded() {
    const std::array<table_data, 1U> tables{
        table_data{
            open_type_tag::from_chars('c', 'm', 'a', 'p'), make_cmap14()}};
    const auto data = make_font(
        0U, 22U, 0U, false, false, false, tables);
    sfnt_font_view font{};
    font_error error = font_error::none;
    require(sfnt_font_view::try_create(data, 0U, font, &error));
    std::uint16_t glyph = 0U;
    require(font.try_get_variation_glyph(0x41U, 0xFE0FU, glyph));
    require(glyph == 3U);
    require(font.try_get_variation_glyph(0x42U, 0xFE0FU, glyph));
    require(glyph == 7U);
    require(!font.try_get_variation_glyph(0x41U, 0xFE0EU, glyph));
    require(glyph == 0U);

    const std::array<unicode_scalar, 2U> input{
        unicode_scalar{0x42U, 0U, 1U},
        unicode_scalar{0xFE0FU, 1U, 1U}};
    open_type_shape_run_requirements requirements{};
    require(try_get_open_type_shape_run_requirements(
        font, input, requirements, &error));
    std::array<shaping_glyph, 6U> glyphs{};
    std::array<unicode_grapheme_cluster, 2U> graphemes{};
    std::array<shaping_attachment, 6U> attachments{};
    std::array<std::uint8_t, 6U> states{};
    std::uint32_t glyph_count = 0U;
    require(try_shape_open_type_run(
        font,
        input,
        open_type_shape_run_options{
            open_type_tag::from_chars('l', 'a', 't', 'n')},
        glyphs,
        open_type_shape_run_scratch{
            graphemes, {}, {}, attachments, states},
        glyph_count,
        &error));
    require(glyph_count == 1U);
    require(glyphs[0U].glyph_id == 7U);
    require(glyphs[0U].code_point == 0x42U);
    require(glyphs[0U].cluster == 0);

    auto malformed_cmap = make_cmap14();
    write_u32(malformed_cmap, 81U, 0xFFFFFFFFU);
    const std::array<table_data, 1U> malformed_tables{
        table_data{
            open_type_tag::from_chars('c', 'm', 'a', 'p'),
            std::move(malformed_cmap)}};
    const auto malformed_data = make_font(
        0U, 22U, 0U, false, false, false, malformed_tables);
    require(sfnt_font_view::try_create(
        malformed_data, 0U, font, &error));
    require(!font.try_get_variation_glyph(0x42U, 0xFE0FU, glyph));
    require(glyph == 0U);
}

void collection_and_failure_paths_are_bounded() {
    auto collection = make_font(16U);
    write_u32(collection, 0U, 0x74746366U);
    write_u32(collection, 4U, 0x00010000U);
    write_u32(collection, 8U, 1U);
    write_u32(collection, 12U, 16U);

    std::uint32_t face_count = 0U;
    font_error error = font_error::none;
    require(sfnt_font_view::try_get_face_count(
        collection, face_count, &error));
    require(face_count == 1U);
    sfnt_font_view font{};
    require(sfnt_font_view::try_create(collection, 0U, font, &error));
    require(font.face_offset() == 16U);
    require(!sfnt_font_view::try_create(collection, 1U, font, &error));
    require(error == font_error::invalid_argument);

    const std::array<std::byte, 11U> short_data{};
    require(!sfnt_font_view::try_get_face_count(
        short_data, face_count, &error));
    require(error == font_error::invalid_face);

    auto truncated = make_font();
    write_u16(truncated, 4U, 0xFFFFU);
    require(!sfnt_font_view::try_create(truncated, 0U, font, &error));
    require(error == font_error::truncated_directory);

    auto invalid_collection = collection;
    write_u32(invalid_collection, 8U, 0U);
    require(!sfnt_font_view::try_get_face_count(
        invalid_collection, face_count, &error));
    require(error == font_error::invalid_collection);

    std::array<std::byte, 44U> woff{};
    write_u32(woff, 0U, 0x774F4646U);
    require(!sfnt_font_view::try_get_face_count(woff, face_count, &error));
    require(error == font_error::unsupported_container);
}

void table_directory_preserves_managed_duplicate_and_bounds_rules() {
    auto duplicate = make_font();
    const auto last_record = 12U + 6U * 16U;
    write_u32(duplicate, last_record,
        open_type_tag::from_chars('h', 'e', 'a', 'd').value);
    sfnt_font_view font{};
    require(sfnt_font_view::try_create(duplicate, 0U, font));
    sfnt_table_view head{};
    require(font.try_get_table(
        open_type_tag::from_chars('h', 'e', 'a', 'd'), head));
    require(head.checksum == 0x1006U);
    require(head.bytes.size() == 80U);

    auto invalid_record = make_font();
    write_u32(invalid_record, last_record + 8U, 0xFFFFFFF0U);
    require(sfnt_font_view::try_create(invalid_record, 0U, font));
    sfnt_table_view cmap{};
    require(!font.try_get_table(
        open_type_tag::from_chars('c', 'm', 'a', 'p'), cmap));
    std::uint16_t glyph = 99U;
    require(!font.try_get_glyph_index(0x41U, glyph));
    require(glyph == 0U);
}

void simple_glyph_repeat_composite_and_malformed_paths_are_explicit() {
    auto repeated = make_font();
    sfnt_font_view font{};
    require(sfnt_font_view::try_create(repeated, 0U, font));
    sfnt_table_view glyf{};
    require(font.try_get_table(
        open_type_tag::from_chars('g', 'l', 'y', 'f'), glyf));
    const auto glyph_offset = static_cast<std::size_t>(
        glyf.bytes.data() - repeated.data());
    repeated[glyph_offset + 14U] = static_cast<std::byte>(0x39U);
    repeated[glyph_offset + 15U] = static_cast<std::byte>(2U);
    require(sfnt_font_view::try_create(repeated, 0U, font));
    sfnt_glyph_decode_requirements requirements{};
    font_error error = font_error::none;
    require(font.try_get_glyph_decode_requirements(
        4U, requirements, &error));
    require(requirements.point_count == 3U);
    require(requirements.path_segment_count == 3U);
    std::array<std::uint16_t, 1U> contour_ends{};
    std::array<sfnt_outline_point, 3U> points{};
    require(font.try_decode_simple_glyph(
        4U, contour_ends, points, &error));
    for (const auto& point : points) {
        require(point.x == 0 && point.y == 0 && point.on_curve());
    }

    std::uint32_t segment_count = 0U;
    require(sfnt_simple_glyph_path::try_get_segment_count(
        contour_ends, points, segment_count, &error));
    require(segment_count == 3U);
    std::array<progpu_native_path_segment, 3U> repeated_segments{};
    std::uint32_t written = 0U;
    require(sfnt_simple_glyph_path::try_write_segments(
        contour_ends, points, repeated_segments, written, &error));
    require(written == 3U);
    for (const auto& segment : repeated_segments) {
        require(segment.kind == PROGPU_NATIVE_PATH_SEGMENT_LINE);
    }

    auto excessive_repeat = repeated;
    excessive_repeat[glyph_offset + 15U] = static_cast<std::byte>(3U);
    require(sfnt_font_view::try_create(excessive_repeat, 0U, font));
    require(!font.try_get_glyph_decode_requirements(
        4U, requirements, &error));
    require(error == font_error::invalid_glyph);

    auto decreasing_ends = make_font();
    write_i16(decreasing_ends, glyph_offset, 2);
    write_u16(decreasing_ends, glyph_offset + 10U, 2U);
    write_u16(decreasing_ends, glyph_offset + 12U, 2U);
    require(sfnt_font_view::try_create(decreasing_ends, 0U, font));
    require(!font.try_get_glyph_decode_requirements(
        4U, requirements, &error));
    require(error == font_error::invalid_glyph);

    auto zero_contours = make_font();
    write_i16(zero_contours, glyph_offset, 0);
    require(sfnt_font_view::try_create(zero_contours, 0U, font));
    require(font.try_get_glyph_decode_requirements(
        4U, requirements, &error));
    require(requirements.kind == sfnt_glyph_kind::empty);

    auto composite = make_font();
    write_i16(composite, glyph_offset, -1);
    write_u16(composite, glyph_offset + 10U, 0x000BU);
    write_u16(composite, glyph_offset + 12U, 4U);
    write_i16(composite, glyph_offset + 14U, 12);
    write_i16(composite, glyph_offset + 16U, -7);
    write_i16(composite, glyph_offset + 18U, 8192);
    require(sfnt_font_view::try_create(composite, 0U, font));
    require(font.try_get_glyph_decode_requirements(
        4U, requirements, &error));
    require(requirements.kind == sfnt_glyph_kind::composite);
    require(!font.try_decode_simple_glyph(
        4U, contour_ends, points, &error));
    require(error == font_error::invalid_glyph);
    sfnt_composite_glyph_decode_requirements composite_requirements{};
    require(font.try_get_composite_glyph_decode_requirements(
        4U, composite_requirements, &error));
    require(composite_requirements.component_count == 1U);
    require(composite_requirements.instruction_bytes == 0U);
    std::array<sfnt_composite_component, 1U> component{};
    require(font.try_decode_composite_glyph(
        4U, component, &error));
    require(component[0].flags == 0x000BU);
    require(component[0].glyph_index == 4U);
    require(component[0].argument1 == 12);
    require(component[0].argument2 == -7);
    require(component[0].m00 == 0.5F && component[0].m11 == 0.5F);
    require(component[0].m01 == 0.0F && component[0].m10 == 0.0F);
    require(!font.try_decode_composite_glyph(
        4U, std::span<sfnt_composite_component>{}, &error));
    require(error == font_error::insufficient_buffer);

    auto two_components = make_font();
    write_i16(two_components, glyph_offset, -1);
    write_u16(two_components, glyph_offset + 10U, 0x0020U);
    write_u16(two_components, glyph_offset + 12U, 4U);
    two_components[glyph_offset + 14U] = static_cast<std::byte>(3U);
    two_components[glyph_offset + 15U] = static_cast<std::byte>(0xFEU);
    write_u16(two_components, glyph_offset + 16U, 0x0002U);
    write_u16(two_components, glyph_offset + 18U, 5U);
    two_components[glyph_offset + 20U] = static_cast<std::byte>(0xFDU);
    two_components[glyph_offset + 21U] = static_cast<std::byte>(4U);
    require(sfnt_font_view::try_create(two_components, 0U, font));
    require(font.try_get_composite_glyph_decode_requirements(
        4U, composite_requirements, &error));
    require(composite_requirements.component_count == 2U);
    std::array<sfnt_composite_component, 2U> decoded_components{};
    require(font.try_decode_composite_glyph(
        4U, decoded_components, &error));
    require(decoded_components[0].argument1 == 3);
    require(decoded_components[0].argument2 == 254);
    require(decoded_components[1].glyph_index == 5U);
    require(decoded_components[1].argument1 == -3);
    require(decoded_components[1].argument2 == 4);

    auto instructed_composite = make_font();
    write_i16(instructed_composite, glyph_offset, -1);
    write_u16(instructed_composite, glyph_offset + 10U, 0x0102U);
    write_u16(instructed_composite, glyph_offset + 12U, 4U);
    instructed_composite[glyph_offset + 14U] = static_cast<std::byte>(1U);
    instructed_composite[glyph_offset + 15U] = static_cast<std::byte>(2U);
    write_u16(instructed_composite, glyph_offset + 16U, 4U);
    require(sfnt_font_view::try_create(instructed_composite, 0U, font));
    require(font.try_get_composite_glyph_decode_requirements(
        4U, composite_requirements, &error));
    require(composite_requirements.component_count == 1U);
    require(composite_requirements.instruction_bytes == 4U);

    auto truncated_instructions = instructed_composite;
    write_u16(truncated_instructions, glyph_offset + 16U, 5U);
    require(sfnt_font_view::try_create(truncated_instructions, 0U, font));
    require(!font.try_get_composite_glyph_decode_requirements(
        4U, composite_requirements, &error));
    require(error == font_error::invalid_glyph);

    auto axis_composite = make_font();
    write_i16(axis_composite, glyph_offset, -1);
    write_u16(axis_composite, glyph_offset + 10U, 0x0043U);
    write_u16(axis_composite, glyph_offset + 12U, 4U);
    write_i16(axis_composite, glyph_offset + 14U, 1);
    write_i16(axis_composite, glyph_offset + 16U, 2);
    write_i16(axis_composite, glyph_offset + 18U, 8192);
    write_i16(axis_composite, glyph_offset + 20U, -8192);
    require(sfnt_font_view::try_create(axis_composite, 0U, font));
    require(font.try_decode_composite_glyph(4U, component, &error));
    require(component[0].m00 == 0.5F && component[0].m11 == -0.5F);

    auto matrix_composite = make_font(0U, 24U);
    require(sfnt_font_view::try_create(matrix_composite, 0U, font));
    sfnt_table_view matrix_glyf{};
    require(font.try_get_table(
        open_type_tag::from_chars('g', 'l', 'y', 'f'), matrix_glyf));
    const auto matrix_glyph_offset = static_cast<std::size_t>(
        matrix_glyf.bytes.data() - matrix_composite.data());
    write_i16(matrix_composite, matrix_glyph_offset, -1);
    write_u16(matrix_composite, matrix_glyph_offset + 10U, 0x0082U);
    write_u16(matrix_composite, matrix_glyph_offset + 12U, 4U);
    matrix_composite[matrix_glyph_offset + 14U] = static_cast<std::byte>(0U);
    matrix_composite[matrix_glyph_offset + 15U] = static_cast<std::byte>(0U);
    write_i16(matrix_composite, matrix_glyph_offset + 16U, 8192);
    write_i16(matrix_composite, matrix_glyph_offset + 18U, 4096);
    write_i16(matrix_composite, matrix_glyph_offset + 20U, -4096);
    write_i16(matrix_composite, matrix_glyph_offset + 22U, 16384);
    require(sfnt_font_view::try_create(matrix_composite, 0U, font));
    require(font.try_decode_composite_glyph(4U, component, &error));
    require(component[0].m00 == 0.5F && component[0].m01 == 0.25F);
    require(component[0].m10 == -0.25F && component[0].m11 == 1.0F);

    auto truncated_composite = composite;
    sfnt_table_view composite_loca{};
    require(sfnt_font_view::try_create(truncated_composite, 0U, font));
    require(font.try_get_table(
        open_type_tag::from_chars('l', 'o', 'c', 'a'), composite_loca));
    const auto composite_loca_offset = static_cast<std::size_t>(
        composite_loca.bytes.data() - truncated_composite.data());
    write_u32(truncated_composite, composite_loca_offset + 20U, 15U);
    require(sfnt_font_view::try_create(truncated_composite, 0U, font));
    require(!font.try_get_composite_glyph_decode_requirements(
        4U, composite_requirements, &error));
    require(error == font_error::invalid_glyph);

    auto truncated_coordinates = make_font();
    sfnt_table_view loca{};
    require(sfnt_font_view::try_create(
        truncated_coordinates, 0U, font));
    require(font.try_get_table(
        open_type_tag::from_chars('l', 'o', 'c', 'a'), loca));
    const auto loca_offset = static_cast<std::size_t>(
        loca.bytes.data() - truncated_coordinates.data());
    write_u32(truncated_coordinates, loca_offset + 20U, 21U);
    require(sfnt_font_view::try_create(
        truncated_coordinates, 0U, font));
    require(!font.try_get_glyph_decode_requirements(
        4U, requirements, &error));
    require(error == font_error::invalid_glyph);
}

void simple_glyph_path_preserves_implicit_midpoints_and_is_transactional() {
    const std::array<std::uint16_t, 1U> contour_ends{{2U}};
    const std::array<sfnt_outline_point, 3U> points{{
        {0, 0, 0U},
        {20, 0, 0U},
        {20, 20, 1U}}};
    std::uint32_t count = 0U;
    font_error error = font_error::none;
    require(sfnt_simple_glyph_path::try_get_segment_count(
        contour_ends, points, count, &error));
    require(count == 2U);
    std::array<progpu_native_path_segment, 2U> segments{};
    std::uint32_t written = 99U;
    require(sfnt_simple_glyph_path::try_write_segments(
        contour_ends, points, segments, written, &error));
    require(written == 2U);
    require(segments[0].kind == PROGPU_NATIVE_PATH_SEGMENT_QUADRATIC);
    require(segments[0].p0.x == 20.0F && segments[0].p0.y == 20.0F);
    require(segments[0].p1.x == 0.0F && segments[0].p1.y == 0.0F);
    require(segments[0].p2.x == 10.0F && segments[0].p2.y == 0.0F);
    require(segments[1].kind == PROGPU_NATIVE_PATH_SEGMENT_QUADRATIC);
    require(segments[1].p0.x == 10.0F && segments[1].p0.y == 0.0F);
    require(segments[1].p1.x == 20.0F && segments[1].p1.y == 0.0F);
    require(segments[1].p2.x == 20.0F && segments[1].p2.y == 20.0F);

    written = 99U;
    require(!sfnt_simple_glyph_path::try_write_segments(
        contour_ends,
        points,
        std::span<progpu_native_path_segment>{},
        written,
        &error));
    require(written == 0U);
    require(error == font_error::insufficient_buffer);

    const std::array<std::uint16_t, 1U> invalid_ends{{1U}};
    require(!sfnt_simple_glyph_path::try_get_segment_count(
        invalid_ends, points, count, &error));
    require(count == 0U);
    require(error == font_error::invalid_argument);

    const std::array<std::uint16_t, 1U> singleton_end{{0U}};
    const std::array<sfnt_outline_point, 1U> singleton{{{4, 9, 1U}}};
    require(sfnt_simple_glyph_path::try_get_segment_count(
        singleton_end, singleton, count, &error));
    require(count == 0U);

    const std::array<std::uint16_t, 2U> two_contours{{1U, 3U}};
    const std::array<sfnt_outline_point, 4U> contour_points{{
        {0, 0, 1U},
        {5, 0, 1U},
        {20, 20, 1U},
        {25, 20, 1U}}};
    require(sfnt_simple_glyph_path::try_get_segment_count(
        two_contours, contour_points, count, &error));
    require(count == 4U);
    std::array<progpu_native_path_segment, 4U> contour_segments{};
    require(sfnt_simple_glyph_path::try_write_segments(
        two_contours,
        contour_points,
        contour_segments,
        written,
        &error));
    require(written == 4U);
    require(contour_segments[2].p0.x == 20.0F);
    require(contour_segments[2].p0.y == 20.0F);
}

void expanded_composite_glyphs_preserve_transforms_and_point_attachment() {
    auto scaled = make_font(0U, 22U, 20U);
    sfnt_font_view font{};
    require(sfnt_font_view::try_create(scaled, 0U, font));
    sfnt_table_view glyf{};
    require(font.try_get_table(
        open_type_tag::from_chars('g', 'l', 'y', 'f'), glyf));
    const auto composite_offset = static_cast<std::size_t>(
        glyf.bytes.data() - scaled.data()) + 22U;
    write_i16(scaled, composite_offset, -1);
    write_u16(scaled, composite_offset + 10U, 0x000BU);
    write_u16(scaled, composite_offset + 12U, 4U);
    write_i16(scaled, composite_offset + 14U, 12);
    write_i16(scaled, composite_offset + 16U, -7);
    write_i16(scaled, composite_offset + 18U, 8192);
    require(sfnt_font_view::try_create(scaled, 0U, font));

    sfnt_expanded_glyph_requirements requirements{};
    font_error error = font_error::none;
    require(font.try_get_expanded_glyph_requirements(
        5U, requirements, &error));
    require(requirements.point_count == 3U);
    require(requirements.path_segment_count == 2U);
    require(requirements.simple_point_scratch_count == 3U);
    require(requirements.simple_contour_scratch_count == 1U);
    std::array<std::uint16_t, 1U> contour_scratch{};
    std::array<sfnt_outline_point, 3U> point_scratch{};
    std::array<progpu_native_point, 3U> points{};
    std::array<progpu_native_path_segment, 2U> segments{};
    std::uint32_t points_written = 0U;
    std::uint32_t segments_written = 0U;
    require(font.try_decode_glyph_outline(
        5U,
        contour_scratch,
        point_scratch,
        points,
        segments,
        points_written,
        segments_written,
        &error));
    require(points_written == 3U && segments_written == 2U);
    require(points[0].x == 17.0F && points[0].y == -7.0F);
    require(points[1].x == 27.0F && points[1].y == 8.0F);
    require(points[2].x == 24.5F && points[2].y == 13.0F);
    require(segments[0].p0.x == 17.0F && segments[0].p0.y == -7.0F);
    require(segments[1].p2.x == 17.0F && segments[1].p2.y == -7.0F);

    points_written = 99U;
    segments_written = 99U;
    require(!font.try_decode_glyph_outline(
        5U,
        contour_scratch,
        point_scratch,
        std::span<progpu_native_point>{},
        segments,
        points_written,
        segments_written,
        &error));
    require(points_written == 0U && segments_written == 0U);
    require(error == font_error::insufficient_buffer);

    auto attached = make_font(0U, 22U, 24U);
    require(sfnt_font_view::try_create(attached, 0U, font));
    require(font.try_get_table(
        open_type_tag::from_chars('g', 'l', 'y', 'f'), glyf));
    const auto attached_offset = static_cast<std::size_t>(
        glyf.bytes.data() - attached.data()) + 22U;
    write_i16(attached, attached_offset, -1);
    write_u16(attached, attached_offset + 10U, 0x0022U);
    write_u16(attached, attached_offset + 12U, 4U);
    attached[attached_offset + 14U] = static_cast<std::byte>(0U);
    attached[attached_offset + 15U] = static_cast<std::byte>(0U);
    write_u16(attached, attached_offset + 16U, 0x0001U);
    write_u16(attached, attached_offset + 18U, 4U);
    write_u16(attached, attached_offset + 20U, 1U);
    write_u16(attached, attached_offset + 22U, 0U);
    require(sfnt_font_view::try_create(attached, 0U, font));
    require(font.try_get_expanded_glyph_requirements(
        5U, requirements, &error));
    require(requirements.point_count == 6U);
    require(requirements.path_segment_count == 4U);
    std::array<progpu_native_point, 6U> attached_points{};
    std::array<progpu_native_path_segment, 4U> attached_segments{};
    require(font.try_decode_glyph_outline(
        5U,
        contour_scratch,
        point_scratch,
        attached_points,
        attached_segments,
        points_written,
        segments_written,
        &error));
    require(points_written == 6U && segments_written == 4U);
    require(attached_points[1].x == attached_points[3].x);
    require(attached_points[1].y == attached_points[3].y);
    require(attached_points[4].x == 50.0F && attached_points[4].y == 60.0F);

    write_u16(attached, attached_offset + 18U, 5U);
    require(sfnt_font_view::try_create(attached, 0U, font));
    require(font.try_get_expanded_glyph_requirements(
        5U, requirements, &error));
    require(requirements.point_count == 3U);
    require(requirements.path_segment_count == 2U);
    require(font.try_decode_glyph_outline(
        5U,
        contour_scratch,
        point_scratch,
        attached_points,
        attached_segments,
        points_written,
        segments_written,
        &error));
    require(points_written == 3U && segments_written == 2U);
}

void cff1_indexes_and_dictionaries_are_borrowed_and_bounded() {
    const std::array<std::byte, 9U> index_bytes{
        std::byte{0x00}, std::byte{0x02}, std::byte{0x01},
        std::byte{0x01}, std::byte{0x03}, std::byte{0x04},
        std::byte{0xAA}, std::byte{0xBB}, std::byte{0xCC}};
    std::size_t cursor = 0U;
    sfnt_cff_index_view index{};
    font_error error = font_error::none;
    require(sfnt_cff_data::try_read_index(
        index_bytes, cursor, index, &error));
    require(error == font_error::none);
    require(index.count == 2U && index.offset_size == 1U);
    require(cursor == index_bytes.size());
    std::span<const std::byte> item{};
    require(sfnt_cff_data::try_get_index_item(index, 0U, item, &error));
    require(item.size() == 2U && item[0] == std::byte{0xAA} &&
        item[1] == std::byte{0xBB});
    require(sfnt_cff_data::try_get_index_item(index, 1U, item, &error));
    require(item.size() == 1U && item[0] == std::byte{0xCC});
    require(!sfnt_cff_data::try_get_index_item(index, 2U, item, &error));
    require(error == font_error::invalid_argument && item.empty());

    const std::array<std::byte, 2U> empty_index{
        std::byte{0x00}, std::byte{0x00}};
    cursor = 0U;
    require(sfnt_cff_data::try_read_index(
        empty_index, cursor, index, &error));
    require(index.count == 0U && cursor == empty_index.size());

    const std::array<std::byte, 6U> descending_offsets{
        std::byte{0x00}, std::byte{0x01}, std::byte{0x01},
        std::byte{0x02}, std::byte{0x01}, std::byte{0xAA}};
    cursor = 0U;
    require(!sfnt_cff_data::try_read_index(
        descending_offsets, cursor, index, &error));
    require(error == font_error::invalid_face && index.count == 0U);

    const std::array<std::byte, 6U> truncated_data{
        std::byte{0x00}, std::byte{0x01}, std::byte{0x01},
        std::byte{0x01}, std::byte{0x03}, std::byte{0xAA}};
    cursor = 0U;
    require(!sfnt_cff_data::try_read_index(
        truncated_data, cursor, index, &error));
    require(error == font_error::invalid_face && index.count == 0U);

    const std::array<std::byte, 3U> real_number{
        std::byte{0x1E}, std::byte{0x1A}, std::byte{0x5F}};
    cursor = 1U;
    double decoded = 0.0;
    require(sfnt_cff_data::try_read_dictionary_number(
        real_number, cursor, 30U, decoded));
    require(decoded == 1.5 && cursor == real_number.size());

    const std::array<std::byte, 5U> exponent_number{
        std::byte{0x1E}, std::byte{0xE1}, std::byte{0xA2},
        std::byte{0x5C}, std::byte{0x2F}};
    cursor = 1U;
    require(sfnt_cff_data::try_read_dictionary_number(
        exponent_number, cursor, 30U, decoded));
    require(decoded < -0.01249 && decoded > -0.01251 &&
        cursor == exponent_number.size());

    const std::array<std::byte, 2U> reserved_real_nibble{
        std::byte{0x1E}, std::byte{0x1D}};
    cursor = 1U;
    require(!sfnt_cff_data::try_read_dictionary_number(
        reserved_real_nibble, cursor, 30U, decoded));

    const std::array<std::byte, 16U> dictionary{
        std::byte{0xF7}, std::byte{0xC0}, std::byte{0x11},
        std::byte{0x95}, std::byte{0xF8}, std::byte{0x24}, std::byte{0x12},
        std::byte{0xF8}, std::byte{0x88}, std::byte{0x0C}, std::byte{0x24},
        std::byte{0xF8}, std::byte{0xEC}, std::byte{0x0C}, std::byte{0x25},
        std::byte{0x00}};
    sfnt_cff1_top_dictionary top{};
    require(sfnt_cff_data::try_get_top_dictionary(
        dictionary, top, &error));
    require(error == font_error::none);
    require(top.char_strings_offset == 300U);
    require(top.private_size == 10U && top.private_offset == 400U);
    require(top.font_dictionary_offset == 500U);
    require(top.fd_select_offset == 600U);

    const std::array<std::byte, 8U> private_and_subroutines{
        std::byte{0x8D}, std::byte{0x13},
        std::byte{0x00}, std::byte{0x01}, std::byte{0x01},
        std::byte{0x01}, std::byte{0x02}, std::byte{0x0B}};
    sfnt_cff_index_view local_subroutines{};
    require(sfnt_cff_data::try_read_local_subroutines(
        private_and_subroutines,
        0U,
        2U,
        local_subroutines,
        &error));
    require(local_subroutines.count == 1U);
    require(sfnt_cff_data::try_get_index_item(
        local_subroutines, 0U, item, &error));
    require(item.size() == 1U && item[0] == std::byte{0x0B});
    require(!sfnt_cff_data::try_read_local_subroutines(
        private_and_subroutines,
        7U,
        2U,
        local_subroutines,
        &error));
    require(error == font_error::invalid_face &&
        local_subroutines.count == 0U);
}

void cff1_fd_select_formats_are_borrowed_and_searchable() {
    const std::array<std::byte, 5U> format_zero{
        std::byte{0x00}, std::byte{0x00}, std::byte{0x01},
        std::byte{0x01}, std::byte{0x00}};
    sfnt_cff_fd_select_view view{};
    font_error error = font_error::none;
    require(sfnt_cff_data::try_read_fd_select(
        format_zero, 0U, 4U, 2U, view, &error));
    require(view.format == 0U && view.range_count == 4U);
    std::uint32_t dictionary = 0U;
    require(sfnt_cff_data::try_get_font_dictionary(
        view, 0U, dictionary, &error));
    require(dictionary == 0U);
    require(sfnt_cff_data::try_get_font_dictionary(
        view, 2U, dictionary, &error));
    require(dictionary == 1U);

    const std::array<std::byte, 11U> format_three{
        std::byte{0x03}, std::byte{0x00}, std::byte{0x02},
        std::byte{0x00}, std::byte{0x00}, std::byte{0x00},
        std::byte{0x00}, std::byte{0x02}, std::byte{0x01},
        std::byte{0x00}, std::byte{0x04}};
    require(sfnt_cff_data::try_read_fd_select(
        format_three, 0U, 4U, 2U, view, &error));
    require(view.format == 3U && view.range_count == 2U);
    require(sfnt_cff_data::try_get_font_dictionary(
        view, 1U, dictionary, &error));
    require(dictionary == 0U);
    require(sfnt_cff_data::try_get_font_dictionary(
        view, 3U, dictionary, &error));
    require(dictionary == 1U);

    const std::array<std::byte, 21U> format_four{
        std::byte{0x04},
        std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x02},
        std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00},
        std::byte{0x00}, std::byte{0x00},
        std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x02},
        std::byte{0x00}, std::byte{0x01},
        std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x04}};
    require(sfnt_cff_data::try_read_fd_select(
        format_four, 0U, 4U, 2U, view, &error));
    require(view.format == 4U && view.range_count == 2U);
    require(sfnt_cff_data::try_get_font_dictionary(
        view, 2U, dictionary, &error));
    require(dictionary == 1U);
    require(!sfnt_cff_data::try_get_font_dictionary(
        view, 4U, dictionary, &error));
    require(error == font_error::invalid_argument);

    auto invalid = format_three;
    invalid[8] = std::byte{0x02};
    require(!sfnt_cff_data::try_read_fd_select(
        invalid, 0U, 4U, 2U, view, &error));
    require(error == font_error::invalid_face && view.bytes.empty());
}

void cff1_type2_outline_is_transactional_and_closes_figures() {
    const std::array<std::byte, 16U> encoded{
        std::byte{0x00}, std::byte{0x01}, std::byte{0x01},
        std::byte{0x01}, std::byte{0x0C},
        std::byte{0x8B}, std::byte{0x8B}, std::byte{0x15},
        std::byte{0xEF}, std::byte{0x8B}, std::byte{0x8B},
        std::byte{0xEF}, std::byte{0x27}, std::byte{0x8B},
        std::byte{0x05}, std::byte{0x0E}};
    std::size_t cursor = 0U;
    sfnt_cff_index_view char_strings{};
    font_error error = font_error::none;
    require(sfnt_cff_data::try_read_index(
        encoded, cursor, char_strings, &error));
    sfnt_cff1_font_view font{};
    font.bytes = encoded;
    font.char_strings = char_strings;

    sfnt_cff1_outline_requirements requirements{};
    require(sfnt_cff_data::try_get_outline_requirements(
        font, 0U, requirements, &error));
    require(error == font_error::none &&
        requirements.path_segment_count == 4U);
    std::array<progpu_native_path_segment, 4U> segments{};
    std::uint32_t written = 0U;
    require(sfnt_cff_data::try_decode_outline(
        font, 0U, segments, written, &error));
    require(error == font_error::none && written == segments.size());
    require(segments[0].p0.x == 0.0F && segments[0].p0.y == 0.0F);
    require(segments[0].p1.x == 100.0F && segments[0].p1.y == 0.0F);
    require(segments[2].p1.x == 0.0F && segments[2].p1.y == 100.0F);
    require(segments[3].p1.x == 0.0F && segments[3].p1.y == 0.0F);

    std::array<progpu_native_path_segment, 3U> short_segments{};
    short_segments[0].kind = 99U;
    written = 99U;
    require(!sfnt_cff_data::try_decode_outline(
        font, 0U, short_segments, written, &error));
    require(error == font_error::insufficient_buffer && written == 0U);
    require(short_segments[0].kind == 99U);
}

void cff2_indexes_blends_and_outlines_are_borrowed_and_bounded() {
    const std::array<std::byte, 17U> static_char_strings{
        std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x01},
        std::byte{0x01}, std::byte{0x01}, std::byte{0x0B},
        std::byte{0x8B}, std::byte{0x8B}, std::byte{0x15},
        std::byte{0xEF}, std::byte{0x8B}, std::byte{0x8B},
        std::byte{0xEF}, std::byte{0x27}, std::byte{0x8B},
        std::byte{0x05}};
    const std::array<std::byte, 10U> font_dictionaries{
        std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x01},
        std::byte{0x01}, std::byte{0x01}, std::byte{0x04},
        std::byte{0x8B}, std::byte{0x8B}, std::byte{0x12}};
    std::size_t cursor = 0U;
    sfnt_cff_index_view char_strings{};
    sfnt_cff_index_view dictionaries{};
    font_error error = font_error::none;
    require(sfnt_cff_data::try_read_cff2_index(
        static_char_strings, cursor, char_strings, &error));
    require(cursor == static_char_strings.size() &&
        char_strings.count == 1U);
    cursor = 0U;
    require(sfnt_cff_data::try_read_cff2_index(
        font_dictionaries, cursor, dictionaries, &error));

    const std::array<std::byte, 8U> top_dictionary{
        std::byte{0xBD}, std::byte{0x11},
        std::byte{0xD1}, std::byte{0x0C}, std::byte{0x24},
        std::byte{0xE5}, std::byte{0x18}, std::byte{0x00}};
    sfnt_cff2_top_dictionary top{};
    require(!sfnt_cff_data::try_get_cff2_top_dictionary(
        top_dictionary, top, &error));
    const auto valid_top =
        std::span<const std::byte>{top_dictionary}.first(7U);
    require(sfnt_cff_data::try_get_cff2_top_dictionary(
        valid_top, top, &error));
    require(top.char_strings_offset == 50U &&
        top.font_dictionary_offset == 70U &&
        top.variation_store_offset == 90U &&
        !top.has_font_matrix);

    sfnt_cff2_font_view font{};
    font.char_strings = char_strings;
    font.font_dictionaries = dictionaries;
    sfnt_cff2_outline_requirements requirements{};
    require(sfnt_cff_data::try_get_outline_requirements(
        font, 0U, {}, requirements, &error));
    require(error == font_error::none &&
        requirements.path_segment_count == 4U);
    std::array<progpu_native_path_segment, 4U> static_segments{};
    std::uint32_t written = 0U;
    require(sfnt_cff_data::try_decode_outline(
        font, 0U, {}, static_segments, written, &error));
    require(written == static_segments.size());
    require(static_segments[0U].p1.x == 100.0F &&
        static_segments[1U].p1.y == 100.0F &&
        static_segments[3U].p1.x == 0.0F &&
        static_segments[3U].p1.y == 0.0F);

    std::vector<std::byte> variation_bytes(30U);
    write_u16(variation_bytes, 0U, 1U);
    write_u32(variation_bytes, 2U, 12U);
    write_u16(variation_bytes, 6U, 1U);
    write_u32(variation_bytes, 8U, 22U);
    write_u16(variation_bytes, 12U, 1U);
    write_u16(variation_bytes, 14U, 1U);
    write_i16(variation_bytes, 16U, 0);
    write_i16(variation_bytes, 18U, 8192);
    write_i16(variation_bytes, 20U, 16384);
    write_u16(variation_bytes, 22U, 0U);
    write_u16(variation_bytes, 24U, 0U);
    write_u16(variation_bytes, 26U, 1U);
    write_u16(variation_bytes, 28U, 0U);
    sfnt_item_variation_store_view store{};
    require(sfnt_item_variation_data::try_get_store(
        variation_bytes, 0U, 1U, store, &error));
    std::uint16_t scalar_count = 0U;
    require(sfnt_item_variation_data::try_get_region_scalar_count(
        store, 0U, scalar_count, &error));
    require(scalar_count == 1U);

    const std::array<std::byte, 19U> varied_char_strings{
        std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x01},
        std::byte{0x01}, std::byte{0x01}, std::byte{0x0C},
        std::byte{0x8B}, std::byte{0x0F},
        std::byte{0x8B}, std::byte{0x8B}, std::byte{0x15},
        std::byte{0xEF}, std::byte{0xB3}, std::byte{0x8C},
        std::byte{0x10}, std::byte{0x8B}, std::byte{0x05},
        std::byte{0x00}};
    cursor = 0U;
    require(sfnt_cff_data::try_read_cff2_index(
        std::span<const std::byte>{varied_char_strings}.first(18U),
        cursor,
        char_strings,
        &error));
    font.char_strings = char_strings;
    font.variation_store = store;
    font.axis_count = 1U;
    const std::array<std::int16_t, 1U> default_coordinates{0};
    const std::array<std::int16_t, 1U> peak_coordinates{8192};
    require(sfnt_cff_data::try_get_outline_requirements(
        font, 0U, default_coordinates, requirements, &error));
    require(requirements.path_segment_count == 2U);
    std::array<progpu_native_path_segment, 2U> default_segments{};
    std::array<progpu_native_path_segment, 2U> peak_segments{};
    require(sfnt_cff_data::try_decode_outline(
        font,
        0U,
        default_coordinates,
        default_segments,
        written,
        &error));
    require(default_segments[0U].p1.x == 100.0F);
    require(sfnt_cff_data::try_decode_outline(
        font,
        0U,
        peak_coordinates,
        peak_segments,
        written,
        &error));
    require(peak_segments[0U].p1.x == 140.0F);

    auto forbidden_endchar = varied_char_strings;
    forbidden_endchar[17U] = std::byte{0x0E};
    forbidden_endchar[6U] = std::byte{0x0C};
    cursor = 0U;
    require(sfnt_cff_data::try_read_cff2_index(
        std::span<const std::byte>{forbidden_endchar}.first(18U),
        cursor,
        char_strings,
        &error));
    font.char_strings = char_strings;
    require(!sfnt_cff_data::try_get_outline_requirements(
        font, 0U, peak_coordinates, requirements, &error));
    require(error == font_error::invalid_glyph);

    const std::array<table_data, 1U> extra{
        table_data{
            open_type_tag::from_chars('C', 'F', 'F', '2'),
            make_cff2_table()}};
    const auto sfnt = make_font(
        0U, 22U, 0U, false, false, false, extra);
    sfnt_font_view face{};
    require(sfnt_font_view::try_create(sfnt, 0U, face, &error));
    require(face.try_get_cff2_font(8U, font, &error));
    require(error == font_error::none && font.char_strings.count == 8U &&
        font.font_dictionaries.count == 1U && font.axis_count == 0U);
    require(sfnt_cff_data::try_get_outline_requirements(
        font, 7U, {}, requirements, &error));
    require(requirements.path_segment_count == 4U);
    sfnt_cff2_font_view mismatch{};
    require(!face.try_get_cff2_font(7U, mismatch, &error));
    require(error == font_error::invalid_face && mismatch.bytes.empty());
}

void sbix_strikes_and_duplicates_remain_borrowed() {
    const std::array<table_data, 1U> extra{
        table_data{
            open_type_tag::from_chars('s', 'b', 'i', 'x'), make_sbix()}};
    const auto data = make_font(
        0U, 22U, 0U, false, false, false, extra);
    sfnt_font_view font{};
    require(sfnt_font_view::try_create(data, 0U, font));
    sfnt_bitmap_glyph_data_view glyph{};
    font_error error = font_error::none;
    require(font.try_get_sbix_glyph(1U, 35.0F, glyph, &error));
    require(error == font_error::none);
    require(glyph.pixels_per_em == 40U && glyph.pixels_per_inch == 72U);
    require(glyph.origin_offset_x == -4 && glyph.origin_offset_y == 12);
    require(glyph.graphic_type ==
        open_type_tag::from_chars('p', 'n', 'g', ' '));
    require(glyph.bytes.size() == 3U &&
        glyph.bytes[0U] == std::byte{40U} &&
        glyph.bytes[1U] == std::byte{41U} &&
        glyph.bytes[2U] == std::byte{42U});

    require(font.try_get_sbix_glyph(2U, 19.0F, glyph, &error));
    require(glyph.pixels_per_em == 20U);
    require(glyph.origin_offset_x == 7 && glyph.origin_offset_y == 8);
    require(glyph.bytes.size() == 3U &&
        glyph.bytes[0U] == std::byte{20U});

    require(font.try_get_sbix_glyph(1U, 30.0F, glyph, &error));
    require(glyph.pixels_per_em == 40U);

    require(!font.try_get_sbix_glyph(8U, 20.0F, glyph, &error));
    require(error == font_error::invalid_argument && glyph.bytes.empty());
}

void svg_glyph_documents_remain_borrowed_and_bounded() {
    for (const auto gzip : {false, true}) {
        const std::array<table_data, 1U> extra{
            table_data{
                open_type_tag::from_chars('S', 'V', 'G', ' '),
                make_svg_glyph_table(gzip)}};
        const auto data = make_font(
            0U, 22U, 0U, false, false, false, extra);
        sfnt_font_view font{};
        require(sfnt_font_view::try_create(data, 0U, font));
        sfnt_svg_glyph_document_view document{};
        font_error error = font_error::none;
        require(font.try_get_svg_glyph_document(1U, document, &error));
        require(error == font_error::none &&
            document.bytes.size() == (gzip ? 26U : 6U));
        require(document.first_glyph == 1U && document.last_glyph == 2U);
        require(document.gzip_compressed == gzip);
        std::size_t document_size = 0U;
        require(try_get_svg_glyph_document_size(
            document, document_size, &error));
        require(document_size == 6U && error == font_error::none);
        std::array<std::byte, 6U> decoded{};
        std::size_t written = 0U;
        require(try_decode_svg_glyph_document(
            document, decoded, written, &error));
        require(written == decoded.size() &&
            decoded == std::array{
                std::byte{0x3CU}, std::byte{0x73U}, std::byte{0x76U},
                std::byte{0x67U}, std::byte{0x2FU}, std::byte{0x3EU}});
        std::array<std::byte, 5U> short_output{};
        require(!try_decode_svg_glyph_document(
            document, short_output, written, &error));
        require(error == font_error::insufficient_buffer && written == 0U);
        require(!font.try_get_svg_glyph_document(3U, document, &error));
        require(error == font_error::invalid_glyph && document.bytes.empty());
    }
}

void svg_path_data_matches_managed_canonical_segments() {
    constexpr std::string_view path =
        "F0 M 10,10 l 20,0 v 20 h -20 z "
        "M40 10 Q50 0 60 10 T80 10 "
        "C90 0 100 20 110 10 S130 20 140 10 "
        "A15 10 30 0 1 170 20 Z";
    svg_path_requirements requirements{};
    font_error error = font_error::none;
    require(try_get_svg_path_requirements(
        path, requirements, &error));
    require(error == font_error::none);
    require(requirements.segment_count == 10U);
    require(requirements.fill_rule == PROGPU_NATIVE_FILL_RULE_EVEN_ODD);
    require(requirements.minimum_x <= 10.0F &&
        requirements.minimum_y <= 0.0F &&
        requirements.maximum_x >= 170.0F &&
        requirements.maximum_y >= 20.0F);

    std::vector<progpu_native_path_segment> segments(
        requirements.segment_count);
    svg_path_requirements decoded{};
    require(try_decode_svg_path(path, segments, decoded, &error));
    require(error == font_error::none);
    require(decoded.segment_count == requirements.segment_count);
    require(segments[0U].kind == PROGPU_NATIVE_PATH_SEGMENT_LINE &&
        segments[0U].p0.x == 10.0F && segments[0U].p0.y == 10.0F &&
        segments[0U].p1.x == 30.0F && segments[0U].p1.y == 10.0F);
    require(segments[3U].kind == PROGPU_NATIVE_PATH_SEGMENT_LINE &&
        segments[3U].p1.x == 10.0F && segments[3U].p1.y == 10.0F);
    require(segments[4U].kind == PROGPU_NATIVE_PATH_SEGMENT_QUADRATIC &&
        segments[5U].kind == PROGPU_NATIVE_PATH_SEGMENT_QUADRATIC);
    require(segments[5U].p1.x == 70.0F &&
        segments[5U].p1.y == 20.0F);
    require(segments[6U].kind == PROGPU_NATIVE_PATH_SEGMENT_CUBIC &&
        segments[7U].kind == PROGPU_NATIVE_PATH_SEGMENT_CUBIC);
    require(segments[7U].p1.x == 120.0F &&
        segments[7U].p1.y == 0.0F);
    require(segments[8U].kind == PROGPU_NATIVE_PATH_SEGMENT_ARC);
    require(std::isfinite(std::bit_cast<float>(segments[8U].pad0)) &&
        std::isfinite(std::bit_cast<float>(segments[8U].pad1)) &&
        std::isfinite(std::bit_cast<float>(segments[8U].pad2)));
    require(segments[9U].kind == PROGPU_NATIVE_PATH_SEGMENT_LINE &&
        segments[9U].p1.x == 40.0F && segments[9U].p1.y == 10.0F);
}

void svg_path_decode_is_transactional_and_bounded() {
    constexpr std::string_view path = "M0 0 L10 0 10 10 0 10 Z";
    svg_path_requirements requirements{};
    font_error error = font_error::none;
    require(try_get_svg_path_requirements(
        path, requirements, &error));
    require(requirements.segment_count == 4U);

    std::array<progpu_native_path_segment, 3U> short_output{};
    short_output[0U].p0 = {123.0F, 456.0F};
    require(!try_decode_svg_path(
        path, short_output, requirements, &error));
    require(error == font_error::insufficient_buffer &&
        requirements.segment_count == 4U &&
        short_output[0U].p0.x == 123.0F &&
        short_output[0U].p0.y == 456.0F);

    constexpr std::string_view malformed = "M0 0 C 1 2 3";
    require(!try_get_svg_path_requirements(
        malformed, requirements, &error));
    require(error == font_error::invalid_glyph &&
        requirements.segment_count == 0U);

    constexpr std::string_view degenerate_arc =
        "M0 0 A0 0 0 0 1 10 0";
    require(try_get_svg_path_requirements(
        degenerate_arc, requirements, &error));
    require(requirements.segment_count == 2U);
    std::array<progpu_native_path_segment, 2U> arc_segments{};
    require(try_decode_svg_path(
        degenerate_arc, arc_segments, requirements, &error));
    require(arc_segments[0U].kind == PROGPU_NATIVE_PATH_SEGMENT_LINE &&
        arc_segments[1U].kind == PROGPU_NATIVE_PATH_SEGMENT_LINE);
}

void cbdt_index_and_image_formats_remain_borrowed_and_bounded() {
    for (std::uint16_t index_format = 1U; index_format <= 5U;
        ++index_format) {
        auto tables = make_cbdt_tables(index_format);
        const std::array<table_data, 2U> extra{
            table_data{
                open_type_tag::from_chars('C', 'B', 'L', 'C'),
                std::move(tables.cblc)},
            table_data{
                open_type_tag::from_chars('C', 'B', 'D', 'T'),
                std::move(tables.cbdt)}};
        const auto data = make_font(
            0U, 22U, 0U, false, false, false, extra);
        sfnt_font_view font{};
        require(sfnt_font_view::try_create(data, 0U, font));
        sfnt_bitmap_glyph_data_view glyph{};
        font_error error = font_error::none;
        require(font.try_get_cbdt_glyph(1U, 30.0F, glyph, &error));
        require(error == font_error::none);
        require(glyph.pixels_per_em == 20U &&
            glyph.pixels_per_inch == 72U);
        require(glyph.uses_horizontal_metrics &&
            glyph.bearing_x == 3 && glyph.bearing_y == 4);
        require(glyph.origin_offset_x == 0 && glyph.origin_offset_y == 0);
        require(glyph.graphic_type ==
            open_type_tag::from_chars('p', 'n', 'g', ' '));
        require(glyph.bytes.size() == 3U &&
            glyph.bytes[0U] == std::byte{0x89U} &&
            glyph.bytes[1U] == std::byte{0x50U} &&
            glyph.bytes[2U] == std::byte{0x4EU});
    }

    auto format_18 = make_cbdt_tables(1U, 18U);
    const std::array<table_data, 2U> extra{
        table_data{
            open_type_tag::from_chars('C', 'B', 'L', 'C'),
            std::move(format_18.cblc)},
        table_data{
            open_type_tag::from_chars('C', 'B', 'D', 'T'),
            std::move(format_18.cbdt)}};
    const auto data = make_font(
        0U, 22U, 0U, false, false, false, extra);
    sfnt_font_view font{};
    require(sfnt_font_view::try_create(data, 0U, font));
    sfnt_bitmap_glyph_data_view glyph{};
    font_error error = font_error::none;
    require(font.try_get_cbdt_glyph(1U, 20.0F, glyph, &error));
    require(glyph.uses_horizontal_metrics && glyph.bearing_x == 3 &&
        glyph.bearing_y == 4 && glyph.bytes.size() == 3U);
    require(!font.try_get_cbdt_glyph(8U, 20.0F, glyph, &error));
    require(error == font_error::invalid_argument && glyph.bytes.empty());

    auto malformed = make_cbdt_tables(1U);
    write_u32(malformed.cblc, 68U, 0xFFFFFFF0U);
    const std::array<table_data, 2U> malformed_extra{
        table_data{
            open_type_tag::from_chars('C', 'B', 'L', 'C'),
            std::move(malformed.cblc)},
        table_data{
            open_type_tag::from_chars('C', 'B', 'D', 'T'),
            std::move(malformed.cbdt)}};
    const auto malformed_data = make_font(
        0U, 22U, 0U, false, false, false, malformed_extra);
    sfnt_font_view malformed_font{};
    require(sfnt_font_view::try_create(
        malformed_data, 0U, malformed_font));
    require(!malformed_font.try_get_cbdt_glyph(
        1U, 20.0F, glyph, &error));
    require(error == font_error::invalid_glyph && glyph.bytes.empty());
}

void colr_layers_and_cpal_palettes_are_transactional() {
    const std::array<table_data, 2U> extra{
        table_data{
            open_type_tag::from_chars('C', 'O', 'L', 'R'), make_colr()},
        table_data{
            open_type_tag::from_chars('C', 'P', 'A', 'L'), make_cpal()}};
    const auto data = make_font(
        0U, 22U, 0U, false, false, false, extra);
    sfnt_font_view font{};
    require(sfnt_font_view::try_create(data, 0U, font));
    font_error error = font_error::none;
    std::uint16_t count = 0U;
    require(font.try_get_colr_layer_count(1U, count, &error));
    require(error == font_error::none && count == 3U);

    std::array<sfnt_color_glyph_layer, 3U> layers{};
    std::uint16_t written = 0U;
    require(font.try_decode_colr_layers(
        1U, 0U, layers, written, &error));
    require(written == 3U);
    require(layers[0U].glyph_index == 2U &&
        layers[0U].palette_entry_index == 0U &&
        layers[0U].color.red == 255U &&
        layers[0U].color.green == 0U &&
        layers[0U].color.blue == 0U &&
        layers[0U].color.alpha == 255U);
    require(layers[1U].glyph_index == 3U &&
        layers[1U].color.red == 0U &&
        layers[1U].color.blue == 255U);
    require(layers[2U].glyph_index == 4U &&
        layers[2U].uses_foreground_color &&
        layers[2U].color.red == 255U);

    require(font.try_decode_colr_layers(
        1U, 1U, layers, written, &error));
    require(layers[0U].color.red == 0U &&
        layers[0U].color.green == 255U &&
        layers[0U].color.blue == 0U);
    require(layers[1U].color.red == 255U &&
        layers[1U].color.green == 255U &&
        layers[1U].color.blue == 255U &&
        layers[1U].color.alpha == 128U);
    require(font.try_decode_colr_layers(
        1U, 9U, layers, written, &error));
    require(layers[0U].color.red == 255U &&
        layers[0U].color.green == 0U);

    std::array<sfnt_color_glyph_layer, 2U> short_layers{};
    short_layers[0U].glyph_index = 99U;
    written = 99U;
    require(!font.try_decode_colr_layers(
        1U, 0U, short_layers, written, &error));
    require(error == font_error::insufficient_buffer && written == 0U &&
        short_layers[0U].glyph_index == 99U);
    require(!font.try_get_colr_layer_count(7U, count, &error));
    require(error == font_error::invalid_glyph && count == 0U);

    const std::array<table_data, 1U> colr_only{
        table_data{
            open_type_tag::from_chars('C', 'O', 'L', 'R'), make_colr()}};
    const auto colr_only_data = make_font(
        0U, 22U, 0U, false, false, false, colr_only);
    sfnt_font_view colr_only_font{};
    require(sfnt_font_view::try_create(
        colr_only_data, 0U, colr_only_font));
    require(colr_only_font.try_decode_colr_layers(
        1U, 0U, layers, written, &error));
    require(written == 3U && layers[0U].color.red == 255U &&
        layers[0U].color.green == 255U &&
        layers[0U].color.blue == 255U &&
        !layers[0U].uses_foreground_color &&
        layers[2U].uses_foreground_color);

    auto malformed_cpal = make_cpal();
    write_u16(malformed_cpal, 12U, 4U);
    const std::array<table_data, 2U> malformed_palette_tables{
        table_data{
            open_type_tag::from_chars('C', 'O', 'L', 'R'), make_colr()},
        table_data{
            open_type_tag::from_chars('C', 'P', 'A', 'L'),
            std::move(malformed_cpal)}};
    const auto malformed_palette_data = make_font(
        0U, 22U, 0U, false, false, false, malformed_palette_tables);
    sfnt_font_view malformed_palette_font{};
    require(sfnt_font_view::try_create(
        malformed_palette_data, 0U, malformed_palette_font));
    layers[0U].glyph_index = 99U;
    require(!malformed_palette_font.try_decode_colr_layers(
        1U, 0U, layers, written, &error));
    require(error == font_error::invalid_face && written == 0U &&
        layers[0U].glyph_index == 99U);

    auto malformed_colr = make_colr();
    write_u16(malformed_colr, 16U, 2U);
    const std::array<table_data, 1U> malformed_layer_tables{
        table_data{
            open_type_tag::from_chars('C', 'O', 'L', 'R'),
            std::move(malformed_colr)}};
    const auto malformed_layer_data = make_font(
        0U, 22U, 0U, false, false, false, malformed_layer_tables);
    sfnt_font_view malformed_layer_font{};
    require(sfnt_font_view::try_create(
        malformed_layer_data, 0U, malformed_layer_font));
    count = 99U;
    require(!malformed_layer_font.try_get_colr_layer_count(
        1U, count, &error));
    require(error == font_error::invalid_face && count == 0U);
}

void production_noto_cff1_container_matches_sfnt_glyph_count() {
    std::ifstream stream(PROGPU_NATIVE_TEST_NOTO_CFF_FONT, std::ios::binary);
    require(stream.good());
    const std::vector<char> source{
        std::istreambuf_iterator<char>(stream),
        std::istreambuf_iterator<char>()};
    std::vector<std::byte> data(source.size());
    for (std::size_t index = 0U; index < source.size(); ++index) {
        data[index] = static_cast<std::byte>(source[index]);
    }

    sfnt_font_view font{};
    require(sfnt_font_view::try_create(data, 0U, font));
    std::uint16_t glyph_count = 0U;
    require(font.try_get_glyph_count(glyph_count));
    sfnt_cff1_font_view cff{};
    font_error error = font_error::none;
    require(font.try_get_cff1_font(glyph_count, cff, &error));
    require(error == font_error::none);

    constexpr std::array<std::uint32_t, 2U> codepoints{0x41U, 0x65E5U};
    constexpr std::array<std::uint16_t, 2U> glyphs{34U, 20220U};
    constexpr std::array<std::uint32_t, 2U> segment_counts{14U, 16U};
    constexpr std::array<std::uint64_t, 2U> hashes{
        1714381338565491643ULL,
        5620540281806238275ULL};
    for (std::size_t checkpoint = 0U;
        checkpoint < codepoints.size();
        ++checkpoint) {
        const auto codepoint = codepoints[checkpoint];
        std::uint16_t glyph = 0U;
        require(font.try_get_glyph_index(codepoint, glyph));
        require(glyph == glyphs[checkpoint]);
        sfnt_cff1_outline_requirements requirements{};
        require(sfnt_cff_data::try_get_outline_requirements(
            cff, glyph, requirements, &error));
        require(requirements.path_segment_count == segment_counts[checkpoint]);
        std::vector<progpu_native_path_segment> segments(
            requirements.path_segment_count);
        std::uint32_t written = 0U;
        require(sfnt_cff_data::try_decode_outline(
            cff, glyph, segments, written, &error));
        require(written == segments.size());
        require(hash_complete_path_segments(segments) == hashes[checkpoint]);
    }
    require(cff.char_strings.count == glyph_count);
    require(!cff.bytes.empty() && cff.top_dictionary.char_strings_offset > 0U);
    require(cff.font_dictionaries.count > 0U &&
        !cff.fd_select.bytes.empty());
    std::span<const std::byte> notdef{};
    require(sfnt_cff_data::try_get_index_item(
        cff.char_strings, 0U, notdef, &error));
    require(error == font_error::none && !notdef.empty());
    std::uint32_t dictionary = 0U;
    require(sfnt_cff_data::try_get_font_dictionary(
        cff.fd_select, 0U, dictionary, &error));
    require(dictionary < cff.font_dictionaries.count);
    sfnt_cff_index_view local_subroutines{};
    require(sfnt_cff_data::try_get_local_subroutines(
        cff, 0U, local_subroutines, &error));
    require(error == font_error::none);

    sfnt_cff1_font_view mismatch{};
    require(!font.try_get_cff1_font(
        static_cast<std::uint16_t>(glyph_count - 1U), mismatch, &error));
    require(error == font_error::invalid_face && mismatch.bytes.empty());
}

void production_inter_font_decodes_real_simple_outline() {
    std::ifstream stream(PROGPU_NATIVE_TEST_INTER_FONT, std::ios::binary);
    require(stream.good());
    const std::vector<char> source{
        std::istreambuf_iterator<char>(stream),
        std::istreambuf_iterator<char>()};
    std::vector<std::byte> data(source.size());
    for (std::size_t index = 0U; index < source.size(); ++index) {
        data[index] = static_cast<std::byte>(source[index]);
    }
    sfnt_font_view font{};
    require(sfnt_font_view::try_create(data, 0U, font));
    sfnt_header_metrics header{};
    require(font.try_get_header_metrics(header));
    require(header.units_per_em == 2048U);
    require(header.x_min == -1546 && header.y_min == -668);
    require(header.x_max == 5290 && header.y_max == 2272);
    sfnt_horizontal_header_metrics horizontal{};
    require(font.try_get_horizontal_header_metrics(horizontal));
    require(horizontal.ascender == 1984);
    require(horizontal.descender == -494);
    require(horizontal.line_gap == 0);
    std::uint16_t glyph_index = 0U;
    require(font.try_get_glyph_index(0x53U, glyph_index));
    require(glyph_index == 397U);
    std::uint16_t glyph_count = 0U;
    require(font.try_get_glyph_count(glyph_count));
    require(glyph_count == 2937U);
    sfnt_horizontal_glyph_metrics glyph_metrics{};
    require(font.try_get_horizontal_glyph_metrics(
        glyph_index, glyph_metrics));
    require(glyph_metrics.advance_width == 1323U);
    require(glyph_metrics.left_side_bearing == 106);
    sfnt_glyph_data_view glyph_data{};
    require(font.try_get_glyph_data(glyph_index, glyph_data));
    require(glyph_data.x_min == 106 && glyph_data.y_min == -25);
    require(glyph_data.x_max == 1217 && glyph_data.y_max == 1510);
    sfnt_glyph_decode_requirements requirements{};
    require(font.try_get_glyph_decode_requirements(
        glyph_index, requirements));
    require(requirements.kind == sfnt_glyph_kind::simple);
    require(requirements.contour_count == 1U);
    require(requirements.point_count == 46U);
    require(requirements.path_segment_count == 34U);
    require(requirements.instruction_bytes == 59U);
    std::vector<std::uint16_t> contours(requirements.contour_count);
    std::vector<sfnt_outline_point> points(requirements.point_count);
    require(font.try_decode_simple_glyph(
        glyph_index, contours, points));
    require(contours.back() + 1U == points.size());
    bool has_on_curve = false;
    bool has_off_curve = false;
    for (const auto& point : points) {
        has_on_curve = has_on_curve || point.on_curve();
        has_off_curve = has_off_curve || !point.on_curve();
    }
    require(has_on_curve && has_off_curve);
    std::uint32_t segment_count = 0U;
    require(sfnt_simple_glyph_path::try_get_segment_count(
        contours, points, segment_count));
    require(segment_count == 34U);
    std::vector<progpu_native_path_segment> segments(segment_count);
    std::uint32_t written = 0U;
    require(sfnt_simple_glyph_path::try_write_segments(
        contours, points, segments, written));
    require(written == segment_count);
    require(segments.front().p0.x == 665.0F);
    require(segments.front().p0.y == -25.0F);
    require(hash_path_segments(segments) == 13245664145576799719ULL);
    const auto last_end = segments.back().kind ==
            PROGPU_NATIVE_PATH_SEGMENT_LINE
        ? segments.back().p1
        : segments.back().p2;
    require(segments.front().p0.x == last_end.x);
    require(segments.front().p0.y == last_end.y);

    std::uint16_t composite_index = 0U;
    require(font.try_get_glyph_index(0x00E9U, composite_index));
    sfnt_glyph_decode_requirements composite_kind{};
    require(font.try_get_glyph_decode_requirements(
        composite_index, composite_kind));
    sfnt_composite_glyph_decode_requirements composite_requirements{};
    require(composite_index == 618U);
    require(composite_kind.kind == sfnt_glyph_kind::composite);
    require(font.try_get_composite_glyph_decode_requirements(
        composite_index, composite_requirements));
    require(composite_requirements.component_count == 2U);
    require(composite_requirements.instruction_bytes == 0U);
    std::array<sfnt_composite_component, 2U> decoded{};
    require(font.try_decode_composite_glyph(composite_index, decoded));
    require(decoded[0].flags == 550U);
    require(decoded[0].glyph_index == 614U);
    require(decoded[0].argument1 == 0 && decoded[0].argument2 == 0);
    require(decoded[0].m00 == 1.0F && decoded[0].m11 == 1.0F);
    require(decoded[1].flags == 7U);
    require(decoded[1].glyph_index == 1770U);
    require(decoded[1].argument1 == 349 && decoded[1].argument2 == 0);
    sfnt_expanded_glyph_requirements expanded{};
    require(font.try_get_expanded_glyph_requirements(
        composite_index, expanded));
    std::vector<std::uint16_t> expanded_contours(
        expanded.simple_contour_scratch_count);
    std::vector<sfnt_outline_point> expanded_scratch(
        expanded.simple_point_scratch_count);
    std::vector<progpu_native_point> expanded_points(expanded.point_count);
    std::vector<progpu_native_path_segment> expanded_segments(
        expanded.path_segment_count);
    std::uint32_t expanded_points_written = 0U;
    std::uint32_t expanded_segments_written = 0U;
    require(font.try_decode_glyph_outline(
        composite_index,
        expanded_contours,
        expanded_scratch,
        expanded_points,
        expanded_segments,
        expanded_points_written,
        expanded_segments_written));
    require(expanded.point_count == 35U);
    require(expanded.path_segment_count == 27U);
    require(expanded.simple_point_scratch_count == 31U);
    require(expanded.simple_contour_scratch_count == 2U);
    require(expanded_points_written == 35U);
    require(expanded_segments_written == 27U);
    require(expanded_segments.front().p0.x == 630.0F);
    require(expanded_segments.front().p0.y == -23.0F);
    require(hash_path_segments(expanded_segments) ==
        5543379682355176128ULL);
}

void production_inter_variable_font_matches_fvar_axes() {
    std::ifstream stream(
        PROGPU_NATIVE_TEST_INTER_VARIABLE_FONT,
        std::ios::binary);
    require(stream.good());
    const std::vector<char> source{
        std::istreambuf_iterator<char>(stream),
        std::istreambuf_iterator<char>()};
    std::vector<std::byte> data(source.size());
    for (std::size_t index = 0U; index < source.size(); ++index) {
        data[index] = static_cast<std::byte>(source[index]);
    }
    sfnt_font_view font{};
    require(sfnt_font_view::try_create(data, 0U, font));
    std::uint16_t count = 0U;
    require(font.try_get_variation_axis_count(count));
    require(count == 2U);
    std::array<sfnt_variation_axis, 2U> axes{};
    std::uint16_t written = 0U;
    require(font.try_decode_variation_axes(axes, written));
    require(written == axes.size());
    require(axes[0].tag ==
        open_type_tag::from_chars('o', 'p', 's', 'z'));
    require(axes[0].minimum_fixed == 14 * 65536);
    require(axes[0].default_fixed == 14 * 65536);
    require(axes[0].maximum_fixed == 32 * 65536);
    require(axes[0].flags == 0U && axes[0].name_id == 256U);
    require(axes[1].tag ==
        open_type_tag::from_chars('w', 'g', 'h', 't'));
    require(axes[1].minimum_fixed == 100 * 65536);
    require(axes[1].default_fixed == 400 * 65536);
    require(axes[1].maximum_fixed == 900 * 65536);
    require(axes[1].flags == 0U && axes[1].name_id == 257U);
    std::int16_t normalized = 0;
    require(font.try_normalize_variation_coordinate(
        0U, 23 * 65536, normalized));
    require(normalized == 8192);
    require(font.try_normalize_variation_coordinate(
        1U, 500 * 65536, normalized));
    require(normalized == 2949);
    require(font.try_normalize_variation_coordinate(
        1U, 700 * 65536, normalized));
    require(normalized == 8847);

    sfnt_gvar_header gvar{};
    require(font.try_get_gvar_header(gvar));
    require(gvar.axis_count == 2U);
    require(gvar.shared_tuple_count == 5U);
    require(gvar.glyph_count == 2937U);
    require(gvar.uses_long_offsets);
    std::array<std::int16_t, 2U> tuple{};
    std::uint16_t tuple_written = 0U;
    require(font.try_decode_gvar_shared_tuple(0U, tuple, tuple_written));
    require(tuple_written == 2U);
    require(tuple == std::array<std::int16_t, 2U>{16384, 0});
    require(font.try_decode_gvar_shared_tuple(4U, tuple, tuple_written));
    require(tuple == std::array<std::int16_t, 2U>{0, -16384});

    sfnt_glyph_variation_data_view glyph_variation{};
    require(font.try_get_glyph_variation_data(397U, glyph_variation));
    require(glyph_variation.bytes.size() == 594U);
    require(glyph_variation.tuple_count == 5U);
    require(glyph_variation.serialized_data_offset == 24U);
    require(glyph_variation.has_shared_point_numbers);
    sfnt_gvar_tuple_requirements tuple_requirements{};
    require(font.try_get_glyph_variation_tuple_requirements(
        397U, tuple_requirements));
    require(tuple_requirements.tuple_count == 5U);
    require(tuple_requirements.region_coordinate_count == 30U);
    std::array<sfnt_gvar_tuple_header, 4U> short_headers{};
    std::array<std::int16_t, 30U> tuple_coordinates{};
    std::uint16_t headers_written = 99U;
    std::uint32_t coordinates_written = 99U;
    require(!font.try_decode_glyph_variation_tuple_headers(
        397U,
        short_headers,
        tuple_coordinates,
        headers_written,
        coordinates_written));
    require(headers_written == 0U && coordinates_written == 0U);
    std::array<sfnt_gvar_tuple_header, 5U> tuple_headers{};
    require(font.try_decode_glyph_variation_tuple_headers(
        397U,
        tuple_headers,
        tuple_coordinates,
        headers_written,
        coordinates_written));
    require(headers_written == 5U && coordinates_written == 30U);
    require(tuple_headers[0].serialized_data_size == 108U);
    require(tuple_headers[0].flags == 0U);
    require(tuple_headers[0].region_coordinate_offset == 0U);
    require(!tuple_headers[0].has_private_point_numbers());
    require(tuple_coordinates[0] == 0 && tuple_coordinates[1] == 0);
    require(tuple_coordinates[2] == 16384 && tuple_coordinates[3] == 0);
    require(tuple_coordinates[4] == 16384 && tuple_coordinates[5] == 0);
    require(tuple_headers[1].serialized_data_size == 111U);
    require(tuple_headers[1].flags == 4U);
    require(tuple_headers[4].serialized_data_size == 107U);
    require(tuple_headers[4].flags == 1U);
    const std::array<std::int16_t, 2U> half_opsz{8192, 0};
    require(sfnt_gvar_tuple_data::calculate_scalar(
        half_opsz,
        std::span<const std::int16_t>(tuple_coordinates).first(6U)) ==
        0.5F);
    const std::array<std::int16_t, 2U> outside_opsz{-8192, 0};
    require(sfnt_gvar_tuple_data::calculate_scalar(
        outside_opsz,
        std::span<const std::int16_t>(tuple_coordinates).first(6U)) ==
        0.0F);
    sfnt_packed_point_requirements shared_points{};
    require(sfnt_packed_variation_data::try_get_point_requirements(
        glyph_variation.bytes.subspan(
            glyph_variation.serialized_data_offset),
        shared_points));
    require(shared_points.all_points && shared_points.bytes_consumed == 1U);
    require(font.try_get_glyph_variation_data(618U, glyph_variation));
    require(glyph_variation.bytes.size() == 60U);
    require(glyph_variation.tuple_count == 5U);
    require(glyph_variation.serialized_data_offset == 24U);

    sfnt_glyph_decode_requirements outline_requirements{};
    require(font.try_get_glyph_decode_requirements(
        397U, outline_requirements));
    std::vector<std::uint16_t> contour_ends(
        outline_requirements.contour_count);
    std::vector<sfnt_outline_point> original_points(
        outline_requirements.point_count);
    require(font.try_decode_simple_glyph(
        397U, contour_ends, original_points));
    sfnt_simple_glyph_variation_requirements variation_requirements{};
    require(font.try_get_simple_glyph_variation_requirements(
        397U,
        outline_requirements.point_count,
        variation_requirements));
    std::vector<sfnt_gvar_tuple_header> varied_headers(
        variation_requirements.tuple_header_count);
    std::vector<std::int16_t> varied_regions(
        variation_requirements.region_coordinate_count);
    std::vector<std::uint32_t> shared_point_numbers(
        variation_requirements.point_number_count);
    std::vector<std::uint32_t> private_point_numbers(
        variation_requirements.point_number_count);
    std::vector<std::int16_t> varied_x(
        variation_requirements.delta_count);
    std::vector<std::int16_t> varied_y(
        variation_requirements.delta_count);
    std::vector<float> tuple_x(variation_requirements.tuple_point_count);
    std::vector<float> tuple_y(variation_requirements.tuple_point_count);
    std::vector<std::uint8_t> touched(
        variation_requirements.tuple_point_count);
    sfnt_simple_glyph_variation_scratch variation_scratch{
        varied_headers,
        varied_regions,
        shared_point_numbers,
        private_point_numbers,
        varied_x,
        varied_y,
        tuple_x,
        tuple_y,
        touched};
    std::vector<progpu_native_point> varied_points(
        outline_requirements.point_count);
    const std::array<std::int16_t, 2U> optical_coordinates{8192, 0};
    float horizontal_advance_delta = 99.0F;
    bool uses_hvar = false;
    require(font.try_get_horizontal_advance_variation(
        397U,
        optical_coordinates,
        horizontal_advance_delta,
        uses_hvar));
    require(uses_hvar && horizontal_advance_delta == -28.0F);
    float x_height_delta = 99.0F;
    bool has_x_height_record = false;
    require(font.try_get_metric_variation(
        open_type_tag::from_chars('x', 'h', 'g', 't'),
        optical_coordinates,
        x_height_delta,
        has_x_height_record));
    require(has_x_height_record && x_height_delta == -31.0F);
    float layout_delta = 99.0F;
    bool uses_layout_store = false;
    require(font.try_get_layout_variation(
        0U,
        0U,
        optical_coordinates,
        layout_delta,
        uses_layout_store));
    require(uses_layout_store);
    require(font.try_apply_simple_glyph_variations(
        397U,
        optical_coordinates,
        contour_ends,
        original_points,
        varied_points,
        variation_scratch));
    std::vector<progpu_native_path_segment> varied_segments(
        outline_requirements.path_segment_count);
    std::uint32_t varied_written = 0U;
    require(sfnt_simple_glyph_path::try_write_varied_segments(
        contour_ends,
        original_points,
        varied_points,
        varied_segments,
        varied_written));
    require(varied_written == 39U);
    require(varied_segments[0].p0.x == 648.5F);
    require(varied_segments[0].p0.y == -25.0F);
    require(hash_path_segments(varied_segments) ==
        12343280691057163238ULL);

    sfnt_composite_glyph_decode_requirements composite_requirements{};
    require(font.try_get_composite_glyph_decode_requirements(
        618U, composite_requirements));
    require(composite_requirements.component_count == 2U);
    sfnt_composite_glyph_variation_requirements component_variations{};
    require(font.try_get_composite_glyph_variation_requirements(
        618U, 2U, component_variations));
    std::vector<sfnt_gvar_tuple_header> component_headers(
        component_variations.tuple_header_count);
    std::vector<std::int16_t> component_regions(
        component_variations.region_coordinate_count);
    std::vector<std::uint32_t> component_shared_points(
        component_variations.point_number_count);
    std::vector<std::uint32_t> component_private_points(
        component_variations.point_number_count);
    std::vector<std::int16_t> component_x(component_variations.delta_count);
    std::vector<std::int16_t> component_y(component_variations.delta_count);
    sfnt_composite_glyph_variation_scratch component_scratch{
        component_headers,
        component_regions,
        component_shared_points,
        component_private_points,
        component_x,
        component_y};
    std::array<progpu_native_point, 2U> component_offsets{};
    require(font.try_get_composite_glyph_variation_offsets(
        618U,
        optical_coordinates,
        2U,
        component_offsets,
        component_scratch));
    require(component_offsets[0].x == 0.0F &&
        component_offsets[0].y == 0.0F &&
        component_offsets[1].x == 15.0F &&
        component_offsets[1].y == 0.0F);

    sfnt_varied_glyph_requirements varied_composite_requirements{};
    require(font.try_get_varied_glyph_requirements(
        618U, varied_composite_requirements));
    require(varied_composite_requirements.component_offset_count == 2U);
    const auto& simple_variation =
        varied_composite_requirements.simple_variation;
    const auto& composite_variation =
        varied_composite_requirements.composite_variation;
    std::vector<std::uint16_t> varied_contours(
        varied_composite_requirements.outline.simple_contour_scratch_count);
    std::vector<sfnt_outline_point> varied_original_points(
        varied_composite_requirements.outline.simple_point_scratch_count);
    std::vector<progpu_native_point> varied_point_scratch(
        varied_composite_requirements.varied_simple_point_count);
    std::vector<progpu_native_point> varied_component_offsets(
        varied_composite_requirements.component_offset_count);
    std::vector<sfnt_gvar_tuple_header> varied_simple_headers(
        simple_variation.tuple_header_count);
    std::vector<std::int16_t> varied_simple_regions(
        simple_variation.region_coordinate_count);
    std::vector<std::uint32_t> varied_simple_shared(
        simple_variation.point_number_count);
    std::vector<std::uint32_t> varied_simple_private(
        simple_variation.point_number_count);
    std::vector<std::int16_t> varied_simple_x(simple_variation.delta_count);
    std::vector<std::int16_t> varied_simple_y(simple_variation.delta_count);
    std::vector<float> varied_tuple_x(simple_variation.tuple_point_count);
    std::vector<float> varied_tuple_y(simple_variation.tuple_point_count);
    std::vector<std::uint8_t> varied_touched(
        simple_variation.tuple_point_count);
    std::vector<sfnt_gvar_tuple_header> varied_composite_headers(
        composite_variation.tuple_header_count);
    std::vector<std::int16_t> varied_composite_regions(
        composite_variation.region_coordinate_count);
    std::vector<std::uint32_t> varied_composite_shared(
        composite_variation.point_number_count);
    std::vector<std::uint32_t> varied_composite_private(
        composite_variation.point_number_count);
    std::vector<std::int16_t> varied_composite_x(
        composite_variation.delta_count);
    std::vector<std::int16_t> varied_composite_y(
        composite_variation.delta_count);
    sfnt_varied_glyph_scratch varied_scratch{
        varied_contours,
        varied_original_points,
        varied_point_scratch,
        varied_component_offsets,
        sfnt_simple_glyph_variation_scratch{
            varied_simple_headers,
            varied_simple_regions,
            varied_simple_shared,
            varied_simple_private,
            varied_simple_x,
            varied_simple_y,
            varied_tuple_x,
            varied_tuple_y,
            varied_touched},
        sfnt_composite_glyph_variation_scratch{
            varied_composite_headers,
            varied_composite_regions,
            varied_composite_shared,
            varied_composite_private,
            varied_composite_x,
            varied_composite_y}};
    std::vector<progpu_native_point> varied_composite_points(
        varied_composite_requirements.outline.point_count);
    std::vector<progpu_native_path_segment> varied_composite_segments(
        varied_composite_requirements.outline.path_segment_count);
    std::uint32_t varied_composite_points_written = 0U;
    std::uint32_t varied_composite_segments_written = 0U;
    require(font.try_decode_varied_glyph_outline(
        618U,
        optical_coordinates,
        varied_scratch,
        varied_composite_points,
        varied_composite_segments,
        varied_composite_points_written,
        varied_composite_segments_written));
    require(varied_composite_points_written ==
        varied_composite_requirements.outline.point_count);
    require(varied_composite_segments_written == 36U);
    require(varied_composite_segments[0].p0.x == 595.0F);
    require(varied_composite_segments[0].p0.y == -24.0F);
    require(hash_path_segments(varied_composite_segments) ==
        12064242707506207632ULL);

    auto short_varied_scratch = varied_scratch;
    short_varied_scratch.component_offsets =
        std::span<progpu_native_point>{varied_component_offsets}.first(1U);
    std::vector<progpu_native_point> untouched_points(
        varied_composite_requirements.outline.point_count,
        progpu_native_point{99.0F, 99.0F});
    std::uint32_t short_points_written = 99U;
    std::uint32_t short_segments_written = 99U;
    font_error short_error = font_error::none;
    require(!font.try_decode_varied_glyph_outline(
        618U,
        optical_coordinates,
        short_varied_scratch,
        untouched_points,
        varied_composite_segments,
        short_points_written,
        short_segments_written,
        &short_error));
    require(short_error == font_error::insufficient_buffer);
    require(short_points_written == 0U && short_segments_written == 0U);
    require(untouched_points[0].x == 99.0F &&
        untouched_points[0].y == 99.0F);
}

void production_inter_shaping_is_stable_and_reusable() {
    std::ifstream stream(PROGPU_NATIVE_TEST_INTER_FONT, std::ios::binary);
    require(stream.good());
    const std::vector<char> source{
        std::istreambuf_iterator<char>(stream),
        std::istreambuf_iterator<char>()};
    std::vector<std::byte> data(source.size());
    for (std::size_t index = 0U; index < source.size(); ++index) {
        data[index] = static_cast<std::byte>(source[index]);
    }
    sfnt_font_view font{};
    font_error error = font_error::none;
    require(sfnt_font_view::try_create(data, 0U, font, &error));
    constexpr std::array input{
        unicode_scalar{0x41U, 0U, 1U},
        unicode_scalar{0x56U, 1U, 1U},
        unicode_scalar{0x41U, 2U, 1U},
        unicode_scalar{0x54U, 3U, 1U},
        unicode_scalar{0x41U, 4U, 1U},
        unicode_scalar{0x52U, 5U, 1U}};
    constexpr std::array default_features{
        open_type_tag::from_chars('l', 't', 'r', 'a'),
        open_type_tag::from_chars('l', 't', 'r', 'm'),
        open_type_tag::from_chars('r', 'v', 'r', 'n'),
        open_type_tag::from_chars('f', 'r', 'a', 'c'),
        open_type_tag::from_chars('n', 'u', 'm', 'r'),
        open_type_tag::from_chars('d', 'n', 'o', 'm'),
        open_type_tag::from_chars('c', 'c', 'm', 'p'),
        open_type_tag::from_chars('l', 'o', 'c', 'l'),
        open_type_tag::from_chars('i', 's', 'o', 'l'),
        open_type_tag::from_chars('f', 'i', 'n', 'a'),
        open_type_tag::from_chars('f', 'i', 'n', '2'),
        open_type_tag::from_chars('f', 'i', 'n', '3'),
        open_type_tag::from_chars('m', 'e', 'd', 'i'),
        open_type_tag::from_chars('m', 'e', 'd', '2'),
        open_type_tag::from_chars('i', 'n', 'i', 't'),
        open_type_tag::from_chars('r', 'l', 'i', 'g'),
        open_type_tag::from_chars('m', 'a', 'r', 'k'),
        open_type_tag::from_chars('m', 'k', 'm', 'k'),
        open_type_tag::from_chars('c', 'a', 'l', 't'),
        open_type_tag::from_chars('c', 'l', 'i', 'g'),
        open_type_tag::from_chars('c', 'u', 'r', 's'),
        open_type_tag::from_chars('d', 'i', 's', 't'),
        open_type_tag::from_chars('a', 'b', 'v', 'm'),
        open_type_tag::from_chars('b', 'l', 'w', 'm'),
        open_type_tag::from_chars('k', 'e', 'r', 'n'),
        open_type_tag::from_chars('l', 'i', 'g', 'a'),
        open_type_tag::from_chars('r', 'c', 'l', 't'),
        open_type_tag::from_chars('r', 'a', 'n', 'd')};
    const open_type_shape_run_options options{
        open_type_tag::from_chars('l', 'a', 't', 'n'),
        {},
        progpu::native::text::shaping_direction::left_to_right,
        default_features};
    open_type_shape_run_requirements requirements{};
    require(try_get_open_type_shape_run_requirements(
        font, input, requirements, &error));
    require(requirements.glyph_capacity <= 64U &&
        requirements.grapheme_capacity <= 16U &&
        requirements.gsub_lookup_capacity <= 128U &&
        requirements.gpos_lookup_capacity <= 128U);
    open_type_shape_plan_requirements plan_requirements{};
    require(try_get_open_type_shape_plan_requirements(
        font, options, plan_requirements, &error));
    require(plan_requirements.gsub_lookup_capacity <= 128U &&
        plan_requirements.gpos_lookup_capacity <= 128U);

    std::array<std::uint16_t, 128U> plan_gsub{};
    std::array<std::uint16_t, 128U> plan_gpos{};
    open_type_shape_plan plan{};
    require(try_build_open_type_shape_plan(
        font,
        options,
        std::span<std::uint16_t>(plan_gsub).first(
            plan_requirements.gsub_lookup_capacity),
        std::span<std::uint16_t>(plan_gpos).first(
            plan_requirements.gpos_lookup_capacity),
        plan,
        &error));

    std::array<shaping_glyph, 64U> glyphs{};
    std::array<unicode_grapheme_cluster, 16U> graphemes{};
    std::array<std::uint16_t, 128U> gsub{};
    std::array<std::uint16_t, 128U> gpos{};
    std::array<shaping_attachment, 64U> attachments{};
    std::array<std::uint8_t, 64U> states{};
    std::array<open_type_arabic_action, 64U> arabic_actions{};
    std::array<std::uint8_t, 64U> categories{};
    std::array<std::uint8_t, 64U> syllables{};
    std::array<std::uint32_t, 64U> indices{};
    const open_type_shape_run_scratch scratch{
        std::span<unicode_grapheme_cluster>(graphemes).first(
            requirements.grapheme_capacity),
        std::span<std::uint16_t>(gsub).first(
            requirements.gsub_lookup_capacity),
        std::span<std::uint16_t>(gpos).first(
            requirements.gpos_lookup_capacity),
        std::span<shaping_attachment>(attachments).first(
            requirements.glyph_capacity),
        std::span<std::uint8_t>(states).first(requirements.glyph_capacity),
        std::span<open_type_arabic_action>(arabic_actions).first(
            requirements.script_action_capacity),
        std::span<std::uint8_t>(categories).first(
            requirements.complex_script_capacity),
        std::span<std::uint8_t>(syllables).first(
            requirements.complex_script_capacity),
        std::span<std::uint32_t>(indices).first(
            requirements.complex_script_index_capacity)};

    std::uint64_t stable_hash = 0U;
    for (std::uint32_t iteration = 0U; iteration < 1024U; ++iteration) {
        std::uint32_t glyph_count = 0U;
        require(try_shape_open_type_run(
            font,
            input,
            options,
            std::span<shaping_glyph>(glyphs).first(
                requirements.glyph_capacity),
            scratch,
            glyph_count,
            &error,
            &plan));
        std::uint64_t hash = 1469598103934665603ULL;
        const auto mix = [&hash](std::uint32_t value) {
            for (std::uint32_t shift = 0U; shift < 32U; shift += 8U) {
                hash ^= (value >> shift) & 0xFFU;
                hash *= 1099511628211ULL;
            }
        };
        mix(glyph_count);
        for (std::uint32_t index = 0U; index < glyph_count; ++index) {
            const auto& glyph = glyphs[index];
            mix(glyph.glyph_id);
            mix(static_cast<std::uint32_t>(glyph.cluster));
            mix(static_cast<std::uint32_t>(glyph.advance_x));
            mix(static_cast<std::uint32_t>(glyph.advance_y));
            mix(static_cast<std::uint32_t>(glyph.offset_x));
            mix(static_cast<std::uint32_t>(glyph.offset_y));
            mix(static_cast<std::uint32_t>(glyph.flags));
        }
        if (iteration == 0U) {
            stable_hash = hash;
        } else {
            require(hash == stable_hash);
        }
    }
    require(stable_hash == 13341559627338683649ULL);

    constexpr std::array contextual_input{
        unicode_scalar{0x6FU, 0U, 1U},
        unicode_scalar{0x66U, 1U, 1U},
        unicode_scalar{0x66U, 2U, 1U},
        unicode_scalar{0x69U, 3U, 1U},
        unicode_scalar{0x63U, 4U, 1U},
        unicode_scalar{0x65U, 5U, 1U},
        unicode_scalar{0x20U, 6U, 1U},
        unicode_scalar{0x41U, 7U, 1U},
        unicode_scalar{0x56U, 8U, 1U}};
    const open_type_shape_run_scratch contextual_scratch{
        graphemes,
        gsub,
        gpos,
        attachments,
        states,
        arabic_actions,
        categories,
        syllables,
        indices};
    std::uint32_t contextual_count = 0U;
    require(try_shape_open_type_run(
        font,
        contextual_input,
        options,
        glyphs,
        contextual_scratch,
        contextual_count,
        &error,
        &plan));
    std::uint64_t contextual_hash = 1469598103934665603ULL;
    const auto contextual_mix = [&contextual_hash](std::uint32_t value) {
        for (std::uint32_t shift = 0U; shift < 32U; shift += 8U) {
            contextual_hash ^= (value >> shift) & 0xFFU;
            contextual_hash *= 1099511628211ULL;
        }
    };
    contextual_mix(contextual_count);
    for (std::uint32_t index = 0U; index < contextual_count; ++index) {
        const auto& glyph = glyphs[index];
        contextual_mix(glyph.glyph_id);
        contextual_mix(static_cast<std::uint32_t>(glyph.cluster));
        contextual_mix(static_cast<std::uint32_t>(glyph.advance_x));
        contextual_mix(static_cast<std::uint32_t>(glyph.advance_y));
        contextual_mix(static_cast<std::uint32_t>(glyph.offset_x));
        contextual_mix(static_cast<std::uint32_t>(glyph.offset_y));
        contextual_mix(static_cast<std::uint32_t>(glyph.flags));
    }
    require(contextual_hash == 17720644002999414799ULL);

    constexpr auto liga = open_type_tag::from_chars('l', 'i', 'g', 'a');
    constexpr auto kern = open_type_tag::from_chars('k', 'e', 'r', 'n');
    constexpr std::array ranged_explicit{liga, kern};
    constexpr std::array ranged_settings{
        shaping_feature{liga, 1U, 0U, 0xFFFFFFFFU},
        shaping_feature{liga, 0U, 0U, 6U},
        shaping_feature{kern, 1U, 0U, 0xFFFFFFFFU},
        shaping_feature{kern, 0U, 7U, 9U}};
    auto ranged_options = options;
    ranged_options.explicit_features = ranged_explicit;
    ranged_options.feature_settings = ranged_settings;
    std::uint32_t ranged_count = 0U;
    require(try_shape_open_type_run(
        font,
        contextual_input,
        ranged_options,
        glyphs,
        contextual_scratch,
        ranged_count,
        &error,
        &plan));
    std::uint64_t ranged_hash = 1469598103934665603ULL;
    const auto ranged_mix = [&ranged_hash](std::uint32_t value) {
        for (std::uint32_t shift = 0U; shift < 32U; shift += 8U) {
            ranged_hash ^= (value >> shift) & 0xFFU;
            ranged_hash *= 1099511628211ULL;
        }
    };
    ranged_mix(ranged_count);
    for (std::uint32_t index = 0U; index < ranged_count; ++index) {
        const auto& glyph = glyphs[index];
        ranged_mix(glyph.glyph_id);
        ranged_mix(static_cast<std::uint32_t>(glyph.cluster));
        ranged_mix(static_cast<std::uint32_t>(glyph.advance_x));
        ranged_mix(static_cast<std::uint32_t>(glyph.advance_y));
        ranged_mix(static_cast<std::uint32_t>(glyph.offset_x));
        ranged_mix(static_cast<std::uint32_t>(glyph.offset_y));
        ranged_mix(static_cast<std::uint32_t>(glyph.flags));
    }
    require(ranged_hash == 14240206642389312925ULL);

    constexpr std::array fraction_input{
        unicode_scalar{0x31U, 0U, 1U},
        unicode_scalar{0x2044U, 1U, 1U},
        unicode_scalar{0x32U, 2U, 1U}};
    std::uint32_t fraction_count = 0U;
    require(try_shape_open_type_run(
        font,
        fraction_input,
        options,
        glyphs,
        contextual_scratch,
        fraction_count,
        &error,
        &plan));
    std::uint64_t fraction_hash = 1469598103934665603ULL;
    const auto fraction_mix = [&fraction_hash](std::uint32_t value) {
        for (std::uint32_t shift = 0U; shift < 32U; shift += 8U) {
            fraction_hash ^= (value >> shift) & 0xFFU;
            fraction_hash *= 1099511628211ULL;
        }
    };
    fraction_mix(fraction_count);
    for (std::uint32_t index = 0U; index < fraction_count; ++index) {
        const auto& glyph = glyphs[index];
        fraction_mix(glyph.glyph_id);
        fraction_mix(static_cast<std::uint32_t>(glyph.cluster));
        fraction_mix(static_cast<std::uint32_t>(glyph.advance_x));
        fraction_mix(static_cast<std::uint32_t>(glyph.advance_y));
        fraction_mix(static_cast<std::uint32_t>(glyph.offset_x));
        fraction_mix(static_cast<std::uint32_t>(glyph.offset_y));
        fraction_mix(static_cast<std::uint32_t>(glyph.flags));
    }
    require(fraction_hash == 13775989768008147903ULL);
}

} // namespace

int main() {
    unicode_contract_and_strict_decoders_are_transactional();
    unicode_bidi_resolution_is_bounded_and_source_preserving();
    unicode_grapheme_segmentation_covers_extended_rules();
    canonical_unicode_normalization_uses_shared_borrowed_data();
    unicode_script_itemization_preserves_source_ranges();
    open_type_common_layout_views_are_borrowed_and_bounded();
    open_type_gdef_classes_and_mark_sets_are_borrowed_and_bounded();
    open_type_gsub_basic_lookups_use_caller_owned_storage();
    open_type_gsub_reverse_chaining_matches_bounded_context();
    open_type_gsub_context_format3_applies_bounded_nested_lookups();
    open_type_gsub_context_glyph_and_class_rules_are_bounded();
    open_type_gsub_chaining_glyph_rules_apply_nested_lookup();
    open_type_script_language_feature_selection_is_bounded();
    open_type_gpos_single_and_pair_adjustments_are_bounded();
    open_type_gpos_attachments_are_caller_owned_and_resolved();
    open_type_gpos_context_format3_applies_nested_lookup();
    open_type_gpos_rule_and_chain_contexts_are_bounded();
    open_type_uniform_run_shaper_connects_unicode_font_and_metrics();
    open_type_common_preprocessing_matches_managed_stages();
    open_type_khmer_preparation_reorders_prebase_vowels();
    open_type_myanmar_preparation_reorders_prebase_vowels();
    open_type_use_preparation_reorders_prebase_vowels();
    open_type_indic_preparation_reorders_prebase_matras();
    open_type_hangul_preparation_composes_and_decomposes();
    open_type_gpos_device_and_variation_deltas_are_applied();
    native_font_fallback_preserves_graphemes_and_missing_state();
    native_font_provider_cache_is_borrowed_and_generation_safe();
    native_positioned_text_layout_wraps_without_allocation();
    unicode_line_breaks_feed_native_layout_without_allocation();
    complex_script_properties_and_syllable_machines_are_bounded();
    woff1_normalization_is_bounded_and_transactional();
    borrowed_sfnt_view_reads_tables_metrics_and_cmap();
    variation_selector_cmap_is_borrowed_and_bounded();
    variation_axes_are_borrowed_bounded_and_transactional();
    variation_coordinates_apply_bounded_avar_mapping();
    packed_variation_streams_are_transactional_and_exact();
    glyph_variation_tuple_headers_are_bounded_and_exact();
    untouched_glyph_deltas_interpolate_without_allocation();
    simple_glyph_variations_apply_packed_tuple_deltas();
    composite_glyph_variations_apply_component_offsets();
    phantom_glyph_variations_apply_advance_delta();
    item_variation_store_and_index_map_are_bounded();
    collection_and_failure_paths_are_bounded();
    table_directory_preserves_managed_duplicate_and_bounds_rules();
    simple_glyph_repeat_composite_and_malformed_paths_are_explicit();
    simple_glyph_path_preserves_implicit_midpoints_and_is_transactional();
    expanded_composite_glyphs_preserve_transforms_and_point_attachment();
    cff1_indexes_and_dictionaries_are_borrowed_and_bounded();
    cff1_fd_select_formats_are_borrowed_and_searchable();
    cff1_type2_outline_is_transactional_and_closes_figures();
    cff2_indexes_blends_and_outlines_are_borrowed_and_bounded();
    sbix_strikes_and_duplicates_remain_borrowed();
    svg_glyph_documents_remain_borrowed_and_bounded();
    svg_path_data_matches_managed_canonical_segments();
    svg_path_decode_is_transactional_and_bounded();
    cbdt_index_and_image_formats_remain_borrowed_and_bounded();
    colr_layers_and_cpal_palettes_are_transactional();
    production_noto_cff1_container_matches_sfnt_glyph_count();
    production_inter_font_decodes_real_simple_outline();
    production_inter_variable_font_matches_fvar_axes();
    production_inter_shaping_is_stable_and_reusable();
    return 0;
}
