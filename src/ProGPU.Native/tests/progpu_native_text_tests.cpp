#include "progpu_native_text.hpp"

#include <algorithm>
#include <array>
#include <bit>
#include <cmath>
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
using progpu::native::text::open_type_feature_tag_requirements;
using progpu::native::text::try_get_open_type_feature_tag_requirements;
using progpu::native::text::try_decode_open_type_feature_tags;
using progpu::native::text::shaping_glyph;
using progpu::native::text::shaping_glyph_flags;
using progpu::native::text::shaping_cluster_level;
using progpu::native::text::shaping_direction;
using progpu::native::text::shaping_buffer_flags;
using progpu::native::text::get_unicode_script;
using progpu::native::text::try_parse_open_type_tag;
using progpu::native::text::try_write_open_type_tag;
using progpu::native::text::infer_open_type_script;
using progpu::native::text::uses_universal_shaping_engine;
using progpu::native::text::get_unicode_arabic_joining_type;
using progpu::native::text::open_type_arabic_action;
using progpu::native::text::try_assign_open_type_arabic_actions;
using progpu::native::text::try_assign_open_type_arabic_actions_and_flags;
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
using progpu::native::text::get_default_unicode_normalization_data;
using progpu::native::text::try_get_unicode_normalization_requirements;
using progpu::native::text::try_normalize_unicode;
using progpu::native::text::open_type_coverage_view;
using progpu::native::text::open_type_class_definition_view;
using progpu::native::text::open_type_lookup_view;
using progpu::native::text::open_type_layout_table_view;
using progpu::native::text::open_type_glyph_set_digest;
using progpu::native::text::open_type_context_coverage_requirement;
using progpu::native::text::open_type_context_subtable_requirement;
using progpu::native::text::open_type_context_accelerator_requirements;
using progpu::native::text::open_type_lookup_accelerator;
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
using progpu::native::text::fallback_mark_metadata;
using progpu::native::text::try_apply_fallback_mark_positioning;
using progpu::native::text::open_type_shape_run_options;
using progpu::native::text::open_type_complex_script;
using progpu::native::text::open_type_shaping_route;
using progpu::native::text::resolve_open_type_language_tag;
using progpu::native::text::try_resolve_open_type_shaping_route;
using progpu::native::text::open_type_feature_setting;
using progpu::native::text::open_type_feature_plan_requirements;
using progpu::native::text::get_default_open_type_feature_settings;
using progpu::native::text::try_get_open_type_feature_plan_requirements;
using progpu::native::text::try_resolve_open_type_feature_plan;
using progpu::native::text::open_type_requested_feature_requirements;
using progpu::native::text::try_get_open_type_requested_feature_requirements;
using progpu::native::text::try_resolve_open_type_requested_features;
using progpu::native::text::open_type_shape_configuration_request;
using progpu::native::text::open_type_shape_configuration_requirements;
using progpu::native::text::open_type_shape_configuration;
using progpu::native::text::try_get_open_type_shape_configuration_requirements;
using progpu::native::text::try_prepare_open_type_shape_configuration;
using progpu::native::text::open_type_shape_run_scratch;
using progpu::native::text::open_type_shape_verification_scratch;
using progpu::native::text::open_type_shape_run_requirements;
using progpu::native::text::try_get_open_type_shape_run_requirements;
using progpu::native::text::try_verify_open_type_shape_result;
using progpu::native::text::open_type_shape_plan_requirements;
using progpu::native::text::open_type_shape_plan;
using progpu::native::text::try_get_open_type_shape_plan_requirements;
using progpu::native::text::try_build_open_type_shape_plan;
using progpu::native::text::try_shape_open_type_run;
using progpu::native::text::try_prepare_open_type_hangul;
using progpu::native::text::try_apply_directional_code_point_fallback;
using progpu::native::text::get_unicode_mirrored_code_point;
using progpu::native::text::get_unicode_vertical_code_point;
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
using progpu::native::text::try_resolve_font_provider_fallback_face;
using progpu::native::text::font_style_request;
using progpu::native::text::font_style_variation_requirements;
using progpu::native::text::font_style_variation;
using progpu::native::text::try_get_font_style_variation_requirements;
using progpu::native::text::try_resolve_font_style_variations;
using progpu::native::text::try_get_font_fallback_family_preference_count;
using progpu::native::text::try_get_font_fallback_family_preferences;
using progpu::native::text::try_preprocess_open_type_glyphs;
using progpu::native::text::unicode_line_break_class;
using progpu::native::text::text_line_break_kind;
using progpu::native::text::get_unicode_line_break_class;
using progpu::native::text::try_resolve_unicode_line_breaks;
using progpu::native::text::text_trimming;
using progpu::native::text::text_alignment;
using progpu::native::text::unicode_indic_shaping_properties;
using progpu::native::text::unicode_syllable_machine;
using progpu::native::text::unicode_syllable_transition;
using progpu::native::text::unicode_vowel_constraint;
using progpu::native::text::get_unicode_indic_shaping_properties;
using progpu::native::text::get_unicode_use_shaping_category;
using progpu::native::text::is_unicode_mark;
using progpu::native::text::get_unicode_vowel_constraint_count;
using progpu::native::text::try_get_unicode_vowel_constraint;
using progpu::native::text::get_unicode_syllable_machine_state_count;
using progpu::native::text::get_unicode_syllable_machine_start_state;
using progpu::native::text::try_get_unicode_syllable_to_state_action;
using progpu::native::text::try_get_unicode_syllable_from_state_action;
using progpu::native::text::try_get_unicode_syllable_transition;
using progpu::native::text::try_get_unicode_syllable_eof_transition;
using progpu::native::text::try_assign_unicode_syllables;
using progpu::native::text::text_layout_options;
using progpu::native::text::positioned_text_glyph;
using progpu::native::text::positioned_text_line;
using progpu::native::text::text_layout_requirements;
using progpu::native::text::try_get_text_layout_requirements;
using progpu::native::text::try_layout_shaped_text;
using progpu::native::text::try_layout_open_type_text;
using progpu::native::text::text_visual_cluster_group;
using progpu::native::text::text_visual_order_requirements;
using progpu::native::text::try_get_text_visual_order_requirements;
using progpu::native::text::try_reorder_text_line_visual;
using progpu::native::text::try_get_text_line_visual_indices;
using progpu::native::text::text_logical_layout_scratch;
using progpu::native::text::try_layout_logical_shaped_text;
using progpu::native::text::positioned_text_column;
using progpu::native::text::text_vertical_layout_requirements;
using progpu::native::text::text_vertical_open_type_metrics;
using progpu::native::text::try_get_vertical_text_layout_requirements;
using progpu::native::text::try_layout_vertical_shaped_text;
using progpu::native::text::try_layout_vertical_open_type_text;
using progpu::native::text::open_type_shaped_glyph;
using progpu::native::text::try_project_open_type_shape_result;
using progpu::native::text::text_layout_metrics;
using progpu::native::text::try_measure_positioned_text_lines;
using progpu::native::text::try_measure_positioned_text_columns;
using progpu::native::text::text_vertical_cluster_box;
using progpu::native::text::text_vertical_caret_stop;
using progpu::native::text::text_vertical_hit_test_result;
using progpu::native::text::try_get_vertical_text_interaction_requirements;
using progpu::native::text::try_build_vertical_text_interaction;
using progpu::native::text::try_hit_test_vertical_text;
using progpu::native::text::try_get_vertical_text_caret_stop;
using progpu::native::text::try_move_vertical_text_caret_visually;
using progpu::native::text::try_get_vertical_text_selection_rectangles;
using progpu::native::text::text_cluster_box;
using progpu::native::text::text_caret_stop;
using progpu::native::text::text_rectangle;
using progpu::native::text::text_hit_test_result;
using progpu::native::text::text_interaction_requirements;
using progpu::native::text::try_get_text_interaction_requirements;
using progpu::native::text::try_build_text_interaction;
using progpu::native::text::try_hit_test_text;
using progpu::native::text::try_get_text_caret_stop;
using progpu::native::text::try_move_text_caret_visually;
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
using progpu::native::text::sfnt_color_palette_override;
using progpu::native::text::sfnt_svg_glyph_document_view;
using progpu::native::text::svg_glyph_layer;
using progpu::native::text::svg_glyph_requirements;
using progpu::native::text::svg_path_requirements;
using progpu::native::text::try_decode_svg_glyph;
using progpu::native::text::try_decode_svg_path;
using progpu::native::text::try_decode_svg_glyph_document;
using progpu::native::text::try_get_svg_glyph_requirements;
using progpu::native::text::try_get_svg_glyph_document_size;
using progpu::native::text::try_get_svg_path_requirements;
using progpu::native::text::sfnt_expanded_glyph_requirements;
using progpu::native::text::sfnt_font_view;
using progpu::native::text::sfnt_glyph_data_view;
using progpu::native::text::sfnt_glyph_decode_requirements;
using progpu::native::text::sfnt_glyph_kind;
using progpu::native::text::sfnt_glyph_outline_source;
using progpu::native::text::sfnt_glyph_outline_bounds_requirements;
using progpu::native::text::sfnt_glyph_outline_bounds_scratch;
using progpu::native::text::fallback_mark_positioning_scratch;
using progpu::native::text::unicode_general_category;
using progpu::native::text::get_unicode_general_category;
using progpu::native::text::arabic_stretch_run;
using progpu::native::text::arabic_stretch_requirements;
using progpu::native::text::try_get_arabic_stretch_requirements;
using progpu::native::text::try_apply_arabic_stretch;
using progpu::native::text::sfnt_glyph_variation_data_view;
using progpu::native::text::sfnt_glyph_phantom_variation_requirements;
using progpu::native::text::sfnt_glyph_phantom_variation_scratch;
using progpu::native::text::sfnt_design_advance_width_requirements;
using progpu::native::text::sfnt_gvar_header;
using progpu::native::text::sfnt_gvar_deltas;
using progpu::native::text::sfnt_gvar_tuple_data;
using progpu::native::text::sfnt_gvar_tuple_header;
using progpu::native::text::sfnt_gvar_tuple_requirements;
using progpu::native::text::sfnt_header_metrics;
using progpu::native::text::sfnt_horizontal_glyph_metrics;
using progpu::native::text::sfnt_horizontal_header_metrics;
using progpu::native::text::sfnt_vertical_header_metrics;
using progpu::native::text::sfnt_vertical_glyph_metrics;
using progpu::native::text::sfnt_simple_glyph_run_requirements;
using progpu::native::text::sfnt_simple_glyph_metrics;
using progpu::native::text::try_get_sfnt_simple_glyph_run_requirements;
using progpu::native::text::try_build_sfnt_simple_glyph_run;
using progpu::native::text::is_sfnt_simple_formatting_control;
using progpu::native::text::try_read_sfnt_simple_code_point;
using progpu::native::text::try_fill_sfnt_simple_glyph_advances;
using progpu::native::text::sfnt_glyph_bounds;
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
using progpu::native::text::sfnt_subset_requirements;
using progpu::native::text::sfnt_glyph_remap;
using progpu::native::text::sfnt_name_requirements;
using progpu::native::text::sfnt_face_style;
using progpu::native::text::sfnt_glyph_resident_requirements;
using progpu::native::text::sfnt_standalone_requirements;
using progpu::native::text::sfnt_directory_record;
using progpu::native::text::try_create_compact_sfnt_subset;
using progpu::native::text::try_create_glyph_id_preserving_sfnt_subset;
using progpu::native::text::try_get_compact_sfnt_subset_requirements;
using progpu::native::text::try_get_glyph_id_preserving_sfnt_subset_requirements;
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

struct normalization_fixture final {
    std::vector<std::byte> bytes{};
    unicode_normalization_data data{};

    normalization_fixture() {
        std::ifstream stream(
            PROGPU_NATIVE_TEST_UNICODE_NORMALIZATION_DATA,
            std::ios::binary);
        require(stream.good());
        const std::vector<char> source{
            std::istreambuf_iterator<char>(stream),
            std::istreambuf_iterator<char>()};
        bytes.resize(source.size());
        for (std::size_t index = 0U; index < source.size(); ++index) {
            bytes[index] = static_cast<std::byte>(source[index]);
        }
        unicode_error error = unicode_error::invalid_argument;
        require(unicode_normalization_data::try_create(bytes, data, &error));
        require(error == unicode_error::none);
    }
};

void write_u16(
    std::span<std::byte> destination,
    std::size_t offset,
    std::uint16_t value);

void unicode_contract_and_strict_decoders_are_transactional() {
    static_assert(sizeof(shaping_feature) == 16U);
    static_assert(sizeof(shaping_glyph) == 32U);
    static_assert(sizeof(shaping_attachment) == 8U);
    static_assert(sizeof(unicode_scalar) == 16U);

    open_type_tag parsed_tag{99U};
    require(try_parse_open_type_tag("kern", parsed_tag) &&
        parsed_tag == open_type_tag::from_chars('k', 'e', 'r', 'n'));
    require(try_parse_open_type_tag("lao ", parsed_tag) &&
        parsed_tag == open_type_tag::from_chars('l', 'a', 'o', ' '));
    require(!try_parse_open_type_tag("abc", parsed_tag) &&
        parsed_tag.value == 0U);
    constexpr char invalid_tag[]{'a', 'b', static_cast<char>(0x1F), 'd'};
    require(!try_parse_open_type_tag(
        std::string_view{invalid_tag, 4U}, parsed_tag) &&
        parsed_tag.value == 0U);
    std::array<char, 4U> formatted_tag{};
    require(try_write_open_type_tag(
        open_type_tag::from_chars('D', 'F', 'L', 'T'), formatted_tag));
    require(std::string_view{formatted_tag.data(), formatted_tag.size()} ==
        "DFLT");
    std::array<char, 3U> short_tag{'x', 'x', 'x'};
    require(!try_write_open_type_tag(
        open_type_tag::from_chars('D', 'F', 'L', 'T'), short_tag) &&
        short_tag[0U] == 'x' && short_tag[2U] == 'x');

    const shaping_feature feature{
        open_type_tag::from_chars('l', 'i', 'g', 'a'), 1U, 2U, 8U};
    require(!feature.applies_to(1U) && feature.applies_to(2U) &&
        feature.applies_to(7U) && !feature.applies_to(8U));
    const shaping_glyph glyph{
        42U, 0x41U, 3, shaping_glyph_flags::unsafe_to_break,
        600, 0, 4, -2};
    require(glyph.glyph_id == 42U && glyph.advance_x == 600 &&
        glyph.offset_y == -2);

    require(get_unicode_mirrored_code_point(0x28U) == 0x29U);
    require(get_unicode_mirrored_code_point(0x2208U) == 0x220BU);
    require(get_unicode_mirrored_code_point(0x41U) == 0x41U);
    require(get_unicode_vertical_code_point(0x3001U) == 0xFE11U);
    require(get_unicode_vertical_code_point(0x41U) == 0x41U);

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
    constexpr std::array one_beh{unicode_scalar{0x0628U, 0U, 1U}};
    constexpr std::array transparent_then_beh{
        unicode_scalar{0x0628U, 0U, 1U},
        unicode_scalar{0x064BU, 1U, 1U}};
    std::array<open_type_arabic_action, 1U> boundary_action{};
    require(try_assign_open_type_arabic_actions(
        one_beh,
        transparent_then_beh,
        one_beh,
        boundary_action,
        action_count,
        &unicode_result));
    require(action_count == 1U &&
        boundary_action[0U] == open_type_arabic_action::medial);
    require(try_assign_open_type_arabic_actions(
        one_beh,
        {},
        one_beh,
        boundary_action,
        action_count,
        &unicode_result));
    require(boundary_action[0U] == open_type_arabic_action::initial);
    require(try_assign_open_type_arabic_actions(
        one_beh,
        one_beh,
        {},
        boundary_action,
        action_count,
        &unicode_result));
    require(boundary_action[0U] == open_type_arabic_action::final);

    constexpr std::array arabic_graphemes{
        unicode_grapheme_cluster{0U, 2U, 0U, 2U},
        unicode_grapheme_cluster{2U, 1U, 2U, 1U}};
    std::array<shaping_glyph_flags, 3U> joining_flags{};
    require(try_assign_open_type_arabic_actions_and_flags(
        arabic,
        arabic_graphemes,
        {},
        {},
        shaping_buffer_flags::none,
        actions,
        joining_flags,
        action_count,
        &unicode_result));
    constexpr auto unsafe_break_and_concat =
        static_cast<std::uint32_t>(shaping_glyph_flags::unsafe_to_break) |
        static_cast<std::uint32_t>(shaping_glyph_flags::unsafe_to_concat);
    require(joining_flags[0U] == shaping_glyph_flags::none &&
        joining_flags[1U] == shaping_glyph_flags::none &&
        static_cast<std::uint32_t>(joining_flags[2U]) ==
            unsafe_break_and_concat);
    require(try_assign_open_type_arabic_actions_and_flags(
        arabic,
        arabic_graphemes,
        {},
        {},
        shaping_buffer_flags::produce_safe_to_insert_tatweel,
        actions,
        joining_flags,
        action_count,
        &unicode_result));
    require(joining_flags[2U] ==
        shaping_glyph_flags::safe_to_insert_tatweel);
    constexpr std::array one_grapheme{
        unicode_grapheme_cluster{0U, 1U, 0U, 1U}};
    std::array<shaping_glyph_flags, 1U> boundary_flags{};
    require(try_assign_open_type_arabic_actions_and_flags(
        one_beh,
        one_grapheme,
        {},
        {},
        shaping_buffer_flags::produce_unsafe_to_concat,
        boundary_action,
        boundary_flags,
        action_count,
        &unicode_result));
    require(boundary_flags[0U] == shaping_glyph_flags::unsafe_to_concat);
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
    constexpr std::array<std::uint32_t, 3U> inferred_script_input{
        0x20U, 0x0301U, 0x0905U};
    require(infer_open_type_script(inferred_script_input) ==
        open_type_tag::from_chars('d', 'e', 'v', 'a'));
    require(infer_open_type_script(std::span<const std::uint32_t>{}) ==
        open_type_tag::from_chars('D', 'F', 'L', 'T'));
    require(uses_universal_shaping_engine(
        open_type_tag::from_chars('d', 'e', 'v', '3')));
    require(uses_universal_shaping_engine(
        open_type_tag::from_chars('D', 'E', 'V', '3')));
    require(uses_universal_shaping_engine(
        open_type_tag::from_chars('t', 'i', 'b', 't')));
    require(!uses_universal_shaping_engine(
        open_type_tag::from_chars('l', 'a', 't', 'n')));
    require(get_unicode_canonical_combining_class(0x0301U) == 230U);
    require(get_unicode_canonical_combining_class(0x41U) == 0U);
    require(get_unicode_general_category(0x41U) ==
        unicode_general_category::uppercase_letter);
    require(get_unicode_general_category(0x0301U) ==
        unicode_general_category::nonspacing_mark);
    require(get_unicode_general_category(0x0661U) ==
        unicode_general_category::decimal_digit_number);
    require(get_unicode_general_category(0x1F600U) ==
        unicode_general_category::other_symbol);
    require(get_unicode_general_category(0xD800U) ==
        unicode_general_category::other_not_assigned);
    require(get_unicode_general_category(0x110000U) ==
        unicode_general_category::other_not_assigned);

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

void canonical_unicode_normalization_embeds_the_default_resource() {
    const unicode_normalization_data* data =
        get_default_unicode_normalization_data();
    require(data != nullptr);

    std::span<const std::byte> decomposition{};
    require(data->try_get_decomposition(0x00E9U, decomposition));
    require(decomposition.size() == 8U);

    std::uint32_t composed = 0U;
    require(data->try_compose(0x0065U, 0x0301U, composed));
    require(composed == 0x00E9U);
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

    std::array<std::byte, 8U> duplicate_coverage{};
    write_u16(duplicate_coverage, 0U, 1U);
    write_u16(duplicate_coverage, 2U, 2U);
    write_u16(duplicate_coverage, 4U, 5U);
    write_u16(duplicate_coverage, 6U, 5U);
    require(open_type_coverage_view::try_create(
        duplicate_coverage, 0U, coverage, &error));
    require(error == font_error::none && coverage.find(5U) == 0);

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

    open_type_glyph_set_digest single_digest{};
    bool has_single_digest = false;
    require(gsub.try_get_lookup_digest(
        0U, 7U, single_digest, has_single_digest, &error));
    require(has_single_digest);
    glyphs = {shaping_glyph{5U, 0x66U, 0}, shaping_glyph{7U, 0x78U, 1}};
    count = 2U;
    open_type_gsub_apply_options accelerated{};
    accelerated.lookup_digest = &single_digest;
    require(try_apply_open_type_gsub_lookup(
        gsub, 0U, glyphs, count, accelerated, applied, &error));
    require(applied && glyphs[0U].glyph_id == 9U &&
        glyphs[1U].glyph_id == 7U);
    open_type_glyph_set_digest disjoint_digest{};
    disjoint_digest.add(7U);
    accelerated.lookup_digest = &disjoint_digest;
    glyphs = {shaping_glyph{5U, 0x66U, 0}};
    count = 1U;
    require(try_apply_open_type_gsub_lookup(
        gsub, 0U, glyphs, count, accelerated, applied, &error));
    require(!applied && glyphs[0U].glyph_id == 5U);

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
        (static_cast<std::uint32_t>(glyphs[0U].flags) & 0xE0000000U) ==
            0x80000000U);

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

    glyphs = {shaping_glyph{5U, 0x66U, 12},
        shaping_glyph{7U, 0x78U, 13}};
    count = 2U;
    open_type_gsub_apply_options substitution_provenance{};
    substitution_provenance.mark_substituted = true;
    require(try_apply_open_type_gsub_lookup(
        gsub,
        0U,
        glyphs,
        count,
        substitution_provenance,
        applied,
        &error));
    require(applied && count == 3U &&
        (static_cast<std::uint32_t>(glyphs[0U].flags) & 0xE0000000U) ==
            0xA0000000U &&
        (static_cast<std::uint32_t>(glyphs[1U].flags) & 0xE0000000U) ==
            0xA0000000U &&
        (static_cast<std::uint32_t>(glyphs[2U].flags) & 0xE0000000U) == 0U);

    glyphs = {shaping_glyph{5U, 0x66U, 12},
        shaping_glyph{7U, 0x78U, 13}};
    count = 2U;
    open_type_gsub_apply_options stretch_metadata{};
    stretch_metadata.track_arabic_stretch_metadata = true;
    require(try_apply_open_type_gsub_lookup(
        gsub, 0U, glyphs, count, stretch_metadata, applied, &error));
    const auto stretch_flags0 =
        static_cast<std::uint32_t>(glyphs[0U].flags);
    const auto stretch_flags1 =
        static_cast<std::uint32_t>(glyphs[1U].flags);
    const auto stretch_flags2 =
        static_cast<std::uint32_t>(glyphs[2U].flags);
    require(applied && count == 3U &&
        (stretch_flags0 & 0x00080000U) != 0U &&
        (stretch_flags0 & 0x0FF00000U) == 0U &&
        (stretch_flags1 & 0x00080000U) != 0U &&
        ((stretch_flags1 & 0x0FF00000U) >> 20U) == 1U &&
        (stretch_flags2 & 0x00080000U) == 0U);

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

    glyphs = {shaping_glyph{5U, 0x66U, 0},
        shaping_glyph{5U, 0x66U, 1}};
    count = 2U;
    std::uint32_t random_state = 1U;
    alternate.alternate_value =
        std::numeric_limits<std::uint16_t>::max();
    alternate.random_state = &random_state;
    alternate.random_alternate = true;
    require(try_apply_open_type_gsub_lookup(
        gsub, 0U, glyphs, count, alternate, applied, &error));
    constexpr auto unsafe_break_and_concat =
        static_cast<std::uint32_t>(shaping_glyph_flags::unsafe_to_break) |
        static_cast<std::uint32_t>(shaping_glyph_flags::unsafe_to_concat);
    require(applied && count == 2U && glyphs[0U].glyph_id == 9U &&
        glyphs[1U].glyph_id == 8U && random_state == 182605794U &&
        glyphs[0U].flags == shaping_glyph_flags::none &&
        static_cast<std::uint32_t>(glyphs[1U].flags) ==
            unsafe_break_and_concat);

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
    ligature_options.track_fallback_mark_metadata = true;
    ligature_options.mark_substituted = true;
    require(try_apply_open_type_gsub_lookup(
        gsub, 0U, glyphs, count, ligature_options, applied, &error));
    require(applied && count == 2U && glyphs[0U].glyph_id == 12U &&
        glyphs[1U].glyph_id == 11U);
    require((static_cast<std::uint32_t>(glyphs[0U].flags) &
            0xE0000000U) == 0xC0000000U);
    require(((static_cast<std::uint32_t>(glyphs[0U].flags) >> 3U) &
            0xFFU) == 2U);
    require(((static_cast<std::uint32_t>(glyphs[1U].flags) >> 11U) &
            0xFFU) == 1U);

    // Managed ReplaceLigature expands both endpoint clusters before removing
    // components. A mark or matra adjacent to the final component therefore
    // inherits the ligature's minimum cluster even when it is not consumed.
    glyphs = {shaping_glyph{5U, 0x66U, 2},
        shaping_glyph{11U, 0x0301U, 2},
        shaping_glyph{6U, 0x69U, 4},
        shaping_glyph{7U, 0x0302U, 4}};
    count = 4U;
    require(try_apply_open_type_gsub_lookup(
        gsub, 0U, glyphs, count, ligature_options, applied, &error));
    require(applied && count == 3U && glyphs[0U].glyph_id == 12U &&
        glyphs[0U].cluster == 2 && glyphs[1U].cluster == 2 &&
        glyphs[2U].cluster == 2);

    glyphs = {shaping_glyph{5U}, shaping_glyph{6U}};
    glyphs[0U].flags = static_cast<shaping_glyph_flags>(1U << 13U);
    glyphs[1U].flags = static_cast<shaping_glyph_flags>(2U << 13U);
    count = 2U;
    ligature_options.restrict_to_syllable = true;
    require(try_apply_open_type_gsub_lookup(
        gsub, 0U, glyphs, count, ligature_options, applied, &error));
    require(!applied && count == 2U && glyphs[0U].glyph_id == 5U &&
        glyphs[1U].glyph_id == 6U);

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
    open_type_context_accelerator_requirements context_requirements{};
    require(gsub.try_get_lookup_context_accelerator_requirements(
        0U, 7U, context_requirements, &error));
    require(context_requirements.supported &&
        context_requirements.subtable_capacity == 1U &&
        context_requirements.coverage_capacity == 2U);
    std::array<open_type_context_subtable_requirement, 1U>
        context_subtables{};
    std::array<open_type_context_coverage_requirement, 2U>
        context_coverages{};
    std::uint16_t context_flags = 99U;
    bool has_context = false;
    require(gsub.try_build_lookup_context_accelerator(
        0U,
        7U,
        context_subtables,
        context_coverages,
        context_flags,
        has_context,
        &error));
    require(has_context && context_flags == 0U &&
        context_subtables[0U].coverage_offset == 0U &&
        context_subtables[0U].coverage_count == 2U &&
        context_subtables[0U].backtrack_count == 0U &&
        context_subtables[0U].input_count == 2U &&
        context_coverages[0U].coverage.find(5U) == 0 &&
        context_coverages[1U].coverage.find(6U) == 0);

    std::array<open_type_lookup_accelerator, 1U> accelerators{};
    accelerators[0U].has_context = true;
    accelerators[0U].context_subtable_count = 1U;
    open_type_shape_plan context_plan{};
    context_plan.gsub_accelerators = accelerators;
    context_plan.gsub_context_subtables = context_subtables;
    context_plan.gsub_context_coverages = context_coverages;
    std::array<shaping_glyph, 2U> matching_context{
        shaping_glyph{5U}, shaping_glyph{6U}};
    open_type_glyph_set_digest matching_digest{};
    matching_digest.add(5U);
    matching_digest.add(6U);
    require(context_plan.gsub_lookup_may_match_context(
        0U, matching_context, matching_digest));
    std::array<shaping_glyph, 2U> missing_context{
        shaping_glyph{5U}, shaping_glyph{8U}};
    open_type_glyph_set_digest missing_digest{};
    missing_digest.add(5U);
    missing_digest.add(8U);
    require(!context_plan.gsub_lookup_may_match_context(
        0U, missing_context, missing_digest));
    accelerators[0U].lookup_flags = 0x0008U;
    require(context_plan.gsub_lookup_may_match_context(
        0U, missing_context, missing_digest));

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
    context_requirements = {};
    require(gsub.try_get_lookup_context_accelerator_requirements(
        0U, 7U, context_requirements, &error));
    require(context_requirements.supported &&
        context_requirements.subtable_capacity == 1U &&
        context_requirements.coverage_capacity == 3U);
    std::array<open_type_context_subtable_requirement, 1U>
        chaining_subtables{};
    std::array<open_type_context_coverage_requirement, 3U>
        chaining_coverages{};
    require(gsub.try_build_lookup_context_accelerator(
        0U,
        7U,
        chaining_subtables,
        chaining_coverages,
        context_flags,
        has_context,
        &error));
    require(has_context && chaining_subtables[0U].backtrack_count == 1U &&
        chaining_subtables[0U].input_count == 1U &&
        chaining_subtables[0U].coverage_count == 3U);
    accelerators[0U].lookup_flags = context_flags;
    context_plan.gsub_context_subtables = chaining_subtables;
    context_plan.gsub_context_coverages = chaining_coverages;
    const std::array<shaping_glyph, 3U> matching_chain{
        shaping_glyph{1U}, shaping_glyph{5U}, shaping_glyph{7U}};
    open_type_glyph_set_digest matching_chain_digest{};
    matching_chain_digest.add(1U);
    matching_chain_digest.add(5U);
    matching_chain_digest.add(7U);
    require(context_plan.gsub_lookup_may_match_context(
        0U, matching_chain, matching_chain_digest));
    const std::array<shaping_glyph, 3U> missing_lookahead{
        shaping_glyph{1U}, shaping_glyph{5U}, shaping_glyph{9U}};
    open_type_glyph_set_digest missing_lookahead_digest{};
    missing_lookahead_digest.add(1U);
    missing_lookahead_digest.add(5U);
    missing_lookahead_digest.add(9U);
    require(!context_plan.gsub_lookup_may_match_context(
        0U, missing_lookahead, missing_lookahead_digest));
    glyphs = {shaping_glyph{1U, 0U, 0},
        shaping_glyph{5U, 0U, 1},
        shaping_glyph{7U, 0U, 2}};
    count = 3U;
    require(try_apply_open_type_gsub_lookup(
        gsub, 0U, glyphs, count, {}, applied, &error));
    require(applied && count == 3U && glyphs[0U].glyph_id == 1U &&
        glyphs[1U].glyph_id == 12U && glyphs[2U].glyph_id == 7U &&
        (static_cast<std::uint32_t>(glyphs[0U].flags) & 3U) == 0U &&
        (static_cast<std::uint32_t>(glyphs[1U].flags) & 3U) == 3U &&
        (static_cast<std::uint32_t>(glyphs[2U].flags) & 3U) == 3U);

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

void open_type_gsub_context_contraction_advances_in_mutated_space() {
    // Lookup 0 is ContextSubst format 3 for [5, 6]. Its nested lookup 1
    // contracts that pair to ligature glyph 12. Repeating the same pair in a
    // second syllable verifies that the top-level iterator advances against
    // the post-substitution buffer rather than the stale input match end.
    std::array<std::byte, 86U> context{};
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
    write_u16(context, 38U, 0U);
    write_u16(context, 40U, 1U);
    write_u16(context, 42U, 1U);
    write_u16(context, 44U, 1U);
    write_u16(context, 46U, 5U);
    write_u16(context, 48U, 1U);
    write_u16(context, 50U, 1U);
    write_u16(context, 52U, 6U);
    write_u16(context, 54U, 4U);
    write_u16(context, 58U, 1U);
    write_u16(context, 60U, 8U);
    write_u16(context, 62U, 1U);
    write_u16(context, 64U, 8U);
    write_u16(context, 66U, 1U);
    write_u16(context, 68U, 14U);
    write_u16(context, 70U, 1U);
    write_u16(context, 72U, 1U);
    write_u16(context, 74U, 5U);
    write_u16(context, 76U, 1U);
    write_u16(context, 78U, 4U);
    write_u16(context, 80U, 12U);
    write_u16(context, 82U, 2U);
    write_u16(context, 84U, 6U);

    open_type_layout_table_view gsub{};
    font_error error = font_error::invalid_argument;
    require(open_type_layout_table_view::try_create(context, gsub, &error));
    constexpr auto first_syllable =
        static_cast<shaping_glyph_flags>(1U << 13U);
    constexpr auto second_syllable =
        static_cast<shaping_glyph_flags>(2U << 13U);
    std::array<shaping_glyph, 4U> glyphs{
        shaping_glyph{5U}, shaping_glyph{6U},
        shaping_glyph{5U}, shaping_glyph{6U}};
    glyphs[0U].flags = first_syllable;
    glyphs[1U].flags = first_syllable;
    glyphs[2U].flags = second_syllable;
    glyphs[3U].flags = second_syllable;

    open_type_gsub_apply_options options{};
    options.restrict_to_syllable = true;
    options.mark_substituted = true;
    std::uint32_t count = 4U;
    std::uint32_t context_match_end = 0U;
    options.context_match_end = &context_match_end;
    bool applied = false;
    require(try_apply_open_type_gsub_lookup_at(
        gsub, 0U, glyphs, count, 0U, options, applied, &error));
    require(applied && count == 3U && context_match_end == 1U &&
        glyphs[0U].glyph_id == 12U && glyphs[1U].glyph_id == 5U &&
        glyphs[2U].glyph_id == 6U &&
        (static_cast<std::uint32_t>(glyphs[0U].flags) & 0xE0000000U) ==
            0xC0000000U &&
        (static_cast<std::uint32_t>(glyphs[1U].flags) & 0xE0000000U) == 0U);

    glyphs = {shaping_glyph{5U}, shaping_glyph{6U},
        shaping_glyph{5U}, shaping_glyph{6U}};
    glyphs[0U].flags = first_syllable;
    glyphs[1U].flags = first_syllable;
    glyphs[2U].flags = second_syllable;
    glyphs[3U].flags = second_syllable;
    count = 4U;
    context_match_end = 0U;
    require(try_apply_open_type_gsub_lookup(
        gsub, 0U, glyphs, count, options, applied, &error));
    require(applied && count == 2U && glyphs[0U].glyph_id == 12U &&
        glyphs[1U].glyph_id == 12U &&
        (static_cast<std::uint32_t>(glyphs[0U].flags) & 0xE0000000U) ==
            0xC0000000U &&
        (static_cast<std::uint32_t>(glyphs[1U].flags) & 0xE0000000U) ==
            0xC0000000U);
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

void open_type_lookup_digest_extends_managed_negative_filter() {
    std::array<std::byte, 40U> layout{};
    write_u16(layout, 0U, 1U);
    write_u16(layout, 4U, 10U);
    write_u16(layout, 6U, 12U);
    write_u16(layout, 8U, 14U);
    write_u16(layout, 14U, 1U);
    write_u16(layout, 16U, 4U);
    write_u16(layout, 18U, 1U);
    write_u16(layout, 22U, 1U);
    write_u16(layout, 24U, 8U);
    write_u16(layout, 26U, 1U);
    write_u16(layout, 28U, 6U);
    write_u16(layout, 32U, 1U);
    write_u16(layout, 34U, 2U);
    write_u16(layout, 36U, 5U);
    write_u16(layout, 38U, 70U);

    open_type_layout_table_view table{};
    font_error error = font_error::invalid_argument;
    require(open_type_layout_table_view::try_create(layout, table, &error));
    open_type_glyph_set_digest digest{};
    bool has_digest = false;
    require(table.try_get_lookup_digest(
        0U, 7U, digest, has_digest, &error));
    require(error == font_error::none && has_digest && digest.may_have(5U) &&
        digest.may_have(70U) && !digest.may_have(7U));
    open_type_lookup_view retained_lookup{};
    open_type_coverage_view retained_coverage{};
    bool has_coverage = false;
    require(table.try_get_single_subtable_coverage(
        0U,
        7U,
        retained_lookup,
        retained_coverage,
        has_coverage,
        &error));
    require(error == font_error::none && has_coverage &&
        retained_lookup.subtable_count == 1U &&
        retained_coverage.find(5U) == 0 &&
        retained_coverage.find(70U) == 1 &&
        retained_coverage.find(7U) < 0);

    open_type_glyph_set_digest overlapping{};
    overlapping.add(70U);
    open_type_glyph_set_digest disjoint{};
    disjoint.add(7U);
    require(digest.may_intersect(overlapping) &&
        !digest.may_intersect(disjoint));
    open_type_glyph_set_digest range{};
    range.add_range(100U, 140U);
    require(range.may_have(100U) && range.may_have(140U));
    range.add_range(140U, 100U);

    /* Context format 3 stores a glyph count rather than a leading coverage
     * offset at byte 2. It uses the retained contextual plan instead. */
    write_u16(layout, 18U, 5U);
    write_u16(layout, 22U, 3U);
    has_coverage = true;
    require(table.try_get_single_subtable_coverage(
        0U,
        7U,
        retained_lookup,
        retained_coverage,
        has_coverage,
        &error));
    require(error == font_error::none && !has_coverage);

    /* GSUB ReverseChainSingle type 8 has the same leading coverage position
     * and must retain the optional exact accelerator even though type 7 is
     * the extension lookup wrapper. */
    write_u16(layout, 18U, 8U);
    write_u16(layout, 22U, 1U);
    has_coverage = false;
    require(table.try_get_single_subtable_coverage(
        0U,
        7U,
        retained_lookup,
        retained_coverage,
        has_coverage,
        &error));
    require(error == font_error::none && has_coverage &&
        retained_coverage.find(5U) == 0 &&
        retained_coverage.find(70U) == 1);

    digest = {};
    has_digest = true;
    require(!table.try_get_lookup_digest(
        1U, 7U, digest, has_digest, &error));
    require(error == font_error::invalid_argument && !has_digest &&
        digest.shift0 == 0U && digest.shift2 == 0U &&
        digest.shift4 == 0U && digest.shift6 == 0U &&
        digest.shift10 == 0U);
    has_coverage = true;
    require(!table.try_get_single_subtable_coverage(
        1U,
        7U,
        retained_lookup,
        retained_coverage,
        has_coverage,
        &error));
    require(error == font_error::invalid_argument && !has_coverage);
}

void open_type_feature_variations_match_managed_lookup_selection() {
    // Exact native fixture for the ProGPU-owned FeatureVariationFontFace GSUB
    // in ShapingContractsTests.cs at checkpoint bd056100.
    std::array<std::byte, 116U> layout{};
    const auto write32 = []<typename T>(
        T& bytes,
        std::size_t offset,
        std::uint32_t value) noexcept {
        bytes[offset] = static_cast<std::byte>(value >> 24U);
        bytes[offset + 1U] = static_cast<std::byte>(value >> 16U);
        bytes[offset + 2U] = static_cast<std::byte>(value >> 8U);
        bytes[offset + 3U] = static_cast<std::byte>(value);
    };
    write_u16(layout, 0U, 1U);
    write_u16(layout, 2U, 1U);
    write_u16(layout, 4U, 14U);
    write_u16(layout, 6U, 34U);
    write_u16(layout, 8U, 50U);
    write32(layout, 10U, 68U);
    write_u16(layout, 14U, 1U);
    write32(layout, 16U, 0x44464C54U);
    write_u16(layout, 20U, 8U);
    write_u16(layout, 22U, 4U);
    write_u16(layout, 24U, 0U);
    write_u16(layout, 26U, 0U);
    write_u16(layout, 28U, 0xFFFFU);
    write_u16(layout, 30U, 1U);
    write_u16(layout, 32U, 0U);
    write_u16(layout, 34U, 1U);
    write32(layout, 36U, 0x6C696761U);
    write_u16(layout, 40U, 10U);
    write_u16(layout, 44U, 0U);
    write_u16(layout, 46U, 1U);
    write_u16(layout, 48U, 0U);
    write_u16(layout, 50U, 2U);
    write_u16(layout, 52U, 6U);
    write_u16(layout, 54U, 12U);
    write_u16(layout, 56U, 1U);
    write_u16(layout, 58U, 0U);
    write_u16(layout, 60U, 0U);
    write_u16(layout, 62U, 1U);
    write_u16(layout, 64U, 0U);
    write_u16(layout, 66U, 0U);
    write_u16(layout, 68U, 1U);
    write_u16(layout, 70U, 0U);
    write32(layout, 72U, 1U);
    write32(layout, 76U, 16U);
    write32(layout, 80U, 30U);
    write_u16(layout, 84U, 1U);
    write32(layout, 86U, 6U);
    write_u16(layout, 90U, 1U);
    write_u16(layout, 92U, 0U);
    write_u16(layout, 94U, 0x2000U);
    write_u16(layout, 96U, 0x4000U);
    write_u16(layout, 98U, 1U);
    write_u16(layout, 100U, 0U);
    write_u16(layout, 102U, 1U);
    write_u16(layout, 104U, 0U);
    write32(layout, 106U, 12U);
    write_u16(layout, 110U, 0U);
    write_u16(layout, 112U, 1U);
    write_u16(layout, 114U, 1U);

    open_type_layout_table_view table{};
    font_error error = font_error::invalid_argument;
    require(open_type_layout_table_view::try_create(layout, table, &error));
    constexpr std::array requested{
        open_type_tag::from_chars('l', 'i', 'g', 'a')};
    std::array<std::uint16_t, 2U> selected{99U, 99U};
    std::uint32_t written = 99U;
    require(table.try_select_lookups(
        open_type_tag::from_chars('D', 'F', 'L', 'T'),
        {},
        requested,
        selected,
        written,
        &error));
    require(written == 1U && selected[0U] == 0U && selected[1U] == 99U);

    constexpr std::array<std::int16_t, 1U> matching{0x2000};
    selected.fill(99U);
    require(table.try_select_lookups(
        open_type_tag::from_chars('D', 'F', 'L', 'T'),
        {},
        requested,
        matching,
        selected,
        written,
        &error));
    require(written == 1U && selected[0U] == 1U && selected[1U] == 99U);
    bool contains = false;
    require(table.try_feature_contains_lookup(
        open_type_tag::from_chars('D', 'F', 'L', 'T'),
        {},
        requested[0U],
        1U,
        matching,
        contains,
        &error));
    require(contains);

    constexpr std::array<std::int16_t, 1U> maximum{0x4000};
    selected.fill(99U);
    require(table.try_select_lookups(
        open_type_tag::from_chars('D', 'F', 'L', 'T'),
        {},
        requested,
        maximum,
        selected,
        written,
        &error));
    require(written == 1U && selected[0U] == 1U);

    constexpr std::array<std::int16_t, 1U> outside{0x1FFF};
    selected.fill(99U);
    require(table.try_select_lookups(
        open_type_tag::from_chars('D', 'F', 'L', 'T'),
        {},
        requested,
        outside,
        selected,
        written,
        &error));
    require(written == 1U && selected[0U] == 0U);

    auto universal = layout;
    write32(universal, 76U, 0U);
    require(open_type_layout_table_view::try_create(
        universal, table, &error));
    selected.fill(99U);
    require(table.try_select_lookups(
        open_type_tag::from_chars('D', 'F', 'L', 'T'),
        {},
        requested,
        selected,
        written,
        &error));
    require(written == 1U && selected[0U] == 1U);

    std::array<std::byte, 130U> unsupported_first{};
    std::copy_n(layout.begin(), 68U, unsupported_first.begin());
    write_u16(unsupported_first, 68U, 1U);
    write_u16(unsupported_first, 70U, 0U);
    write32(unsupported_first, 72U, 2U);
    write32(unsupported_first, 76U, 0U);
    write32(unsupported_first, 80U, 56U);
    write32(unsupported_first, 84U, 24U);
    write32(unsupported_first, 88U, 38U);
    write_u16(unsupported_first, 92U, 1U);
    write32(unsupported_first, 94U, 6U);
    write_u16(unsupported_first, 98U, 1U);
    write_u16(unsupported_first, 100U, 0U);
    write_u16(unsupported_first, 102U, 0x2000U);
    write_u16(unsupported_first, 104U, 0x4000U);
    write_u16(unsupported_first, 106U, 1U);
    write_u16(unsupported_first, 108U, 0U);
    write_u16(unsupported_first, 110U, 1U);
    write_u16(unsupported_first, 112U, 0U);
    write32(unsupported_first, 114U, 12U);
    write_u16(unsupported_first, 118U, 0U);
    write_u16(unsupported_first, 120U, 1U);
    write_u16(unsupported_first, 122U, 1U);
    write_u16(unsupported_first, 124U, 2U);
    write_u16(unsupported_first, 126U, 0U);
    write_u16(unsupported_first, 128U, 0U);
    require(open_type_layout_table_view::try_create(
        unsupported_first, table, &error));
    selected.fill(99U);
    require(table.try_select_lookups(
        open_type_tag::from_chars('D', 'F', 'L', 'T'),
        {},
        requested,
        matching,
        selected,
        written,
        &error));
    require(written == 1U && selected[0U] == 1U);
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

    open_type_glyph_set_digest single_digest{};
    bool has_single_digest = false;
    require(gpos.try_get_lookup_digest(
        0U, 9U, single_digest, has_single_digest, &error));
    require(has_single_digest);
    glyphs[0U] = shaping_glyph{
        5U, 0U, 0, shaping_glyph_flags::none, 10, 0, 1, 0};
    open_type_gpos_apply_options accelerated{};
    accelerated.lookup_digest = &single_digest;
    require(try_apply_open_type_gpos_lookup(
        gpos,
        0U,
        std::span<shaping_glyph>{glyphs}.first(1U),
        accelerated,
        applied,
        &error));
    require(applied && glyphs[0U].offset_x == 4 &&
        glyphs[0U].advance_x == 8);
    open_type_glyph_set_digest disjoint_digest{};
    disjoint_digest.add(7U);
    accelerated.lookup_digest = &disjoint_digest;
    glyphs[0U] = shaping_glyph{
        5U, 0U, 0, shaping_glyph_flags::none, 10, 0, 1, 0};
    require(try_apply_open_type_gpos_lookup(
        gpos,
        0U,
        std::span<shaping_glyph>{glyphs}.first(1U),
        accelerated,
        applied,
        &error));
    require(!applied && glyphs[0U].offset_x == 1 &&
        glyphs[0U].advance_x == 10);

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
        attachments[1U].kind == shaping_attachment_kind::mark &&
        attachments[1U].reserved2 == 1U);
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

std::uint16_t read_u16(
    std::span<const std::byte> source,
    std::size_t offset) {
    return static_cast<std::uint16_t>(
        (std::to_integer<std::uint16_t>(source[offset]) << 8U) |
        std::to_integer<std::uint16_t>(source[offset + 1U]));
}

std::uint32_t read_u32(
    std::span<const std::byte> source,
    std::size_t offset) {
    return (std::to_integer<std::uint32_t>(source[offset]) << 24U) |
        (std::to_integer<std::uint32_t>(source[offset + 1U]) << 16U) |
        (std::to_integer<std::uint32_t>(source[offset + 2U]) << 8U) |
        std::to_integer<std::uint32_t>(source[offset + 3U]);
}

std::vector<std::byte> make_sfnt_subset_fixture() {
    const auto simple = [](std::int16_t maximum, std::size_t extra = 0U) {
        std::vector<std::byte> glyph(10U + extra);
        write_i16(glyph, 0U, 1);
        write_i16(glyph, 2U, 0);
        write_i16(glyph, 4U, 0);
        write_i16(glyph, 6U, maximum);
        write_i16(glyph, 8U, maximum);
        return glyph;
    };
    std::vector<std::byte> composite(16U);
    write_i16(composite, 0U, -1);
    write_i16(composite, 2U, 0);
    write_i16(composite, 4U, 0);
    write_i16(composite, 6U, 40);
    write_i16(composite, 8U, 40);
    write_u16(composite, 10U, 0x0002U);
    write_u16(composite, 12U, 1U);

    const std::array glyphs{
        std::vector<std::byte>{}, simple(20), composite, simple(300, 768U)};
    std::array<std::uint32_t, 5U> glyph_offsets{};
    std::vector<std::byte> glyf;
    for (std::size_t index = 0U; index < glyphs.size(); ++index) {
        glyph_offsets[index] = static_cast<std::uint32_t>(glyf.size());
        glyf.insert(glyf.end(), glyphs[index].begin(), glyphs[index].end());
        glyf.resize((glyf.size() + 3U) & ~std::size_t{3U});
    }
    glyph_offsets.back() = static_cast<std::uint32_t>(glyf.size());

    std::vector<std::byte> head(54U);
    write_u32(head, 0U, 0x00010000U);
    write_u32(head, 4U, 0x00010000U);
    write_u16(head, 18U, 1000U);
    write_i16(head, 50U, 1);
    std::vector<std::byte> maxp(6U);
    write_u32(maxp, 0U, 0x00010000U);
    write_u16(maxp, 4U, 4U);
    std::vector<std::byte> hhea(36U);
    write_i16(hhea, 4U, 800);
    write_i16(hhea, 6U, -200);
    write_u16(hhea, 34U, 4U);
    std::vector<std::byte> hmtx(16U);
    for (std::size_t glyph = 0U; glyph < 4U; ++glyph) {
        write_u16(hmtx, glyph * 4U,
            static_cast<std::uint16_t>(500U + glyph));
    }
    std::vector<std::byte> loca(glyph_offsets.size() * 4U);
    for (std::size_t index = 0U; index < glyph_offsets.size(); ++index) {
        write_u32(loca, index * 4U, glyph_offsets[index]);
    }
    std::vector<std::byte> dsig(128U, std::byte{0xA5U});

    struct fixture_table final {
        std::uint32_t tag;
        std::vector<std::byte> bytes;
    };
    std::array tables{
        fixture_table{0x68656164U, std::move(head)},
        fixture_table{0x68686561U, std::move(hhea)},
        fixture_table{0x6D617870U, std::move(maxp)},
        fixture_table{0x686D7478U, std::move(hmtx)},
        fixture_table{0x6C6F6361U, std::move(loca)},
        fixture_table{0x676C7966U, std::move(glyf)},
        fixture_table{0x44534947U, std::move(dsig)}};
    const std::size_t directory_bytes = 12U + tables.size() * 16U;
    std::size_t output_size = directory_bytes;
    for (const auto& table : tables) {
        output_size += (table.bytes.size() + 3U) & ~std::size_t{3U};
    }
    std::vector<std::byte> result(output_size);
    write_u32(result, 0U, 0x00010000U);
    write_u16(result, 4U, static_cast<std::uint16_t>(tables.size()));
    std::size_t table_offset = directory_bytes;
    for (std::size_t index = 0U; index < tables.size(); ++index) {
        const auto record = 12U + index * 16U;
        write_u32(result, record, tables[index].tag);
        write_u32(result, record + 8U,
            static_cast<std::uint32_t>(table_offset));
        write_u32(result, record + 12U,
            static_cast<std::uint32_t>(tables[index].bytes.size()));
        std::copy(tables[index].bytes.begin(), tables[index].bytes.end(),
            result.begin() + static_cast<std::ptrdiff_t>(table_offset));
        table_offset +=
            (tables[index].bytes.size() + 3U) & ~std::size_t{3U};
    }
    return result;
}

std::vector<std::byte> make_sfnt_metadata_fixture() {
    std::vector<std::byte> name(42U);
    write_u16(name, 2U, 3U);
    write_u16(name, 4U, 42U);
    const auto append_utf16 = [&](std::u16string_view value) {
        const auto offset = static_cast<std::uint16_t>(name.size() - 42U);
        for (const auto code_unit : value) {
            name.push_back(static_cast<std::byte>(code_unit >> 8U));
            name.push_back(static_cast<std::byte>(code_unit));
        }
        return std::pair{offset,
            static_cast<std::uint16_t>(value.size() * 2U)};
    };
    const auto arabic = append_utf16(u"\u062c\u064a\u0632\u0627");
    const auto family = append_utf16(u"ProGPU Sans");
    const auto full = append_utf16(
        std::u16string_view{u" \0ProGPU Sans Regular ", 22U});
    const auto write_record = [&](std::size_t record,
        std::uint16_t platform, std::uint16_t encoding,
        std::uint16_t language, std::uint16_t name_id,
        std::pair<std::uint16_t, std::uint16_t> value) {
        write_u16(name, record, platform);
        write_u16(name, record + 2U, encoding);
        write_u16(name, record + 4U, language);
        write_u16(name, record + 6U, name_id);
        write_u16(name, record + 8U, value.second);
        write_u16(name, record + 10U, value.first);
    };
    write_record(6U, 3U, 1U, 0x0420U,
        progpu::native::text::sfnt_name_ids::family_name, arabic);
    write_record(18U, 0U, 4U, 0U,
        progpu::native::text::sfnt_name_ids::family_name, family);
    write_record(30U, 3U, 1U, 0x0409U,
        progpu::native::text::sfnt_name_ids::full_name, full);

    std::vector<std::byte> head(54U);
    write_u16(head, 44U, 0x0002U);
    std::vector<std::byte> os2(64U);
    write_u16(os2, 4U, 725U);
    write_u16(os2, 6U, 8U);
    write_u16(os2, 8U, 0x0008U);
    write_u16(os2, 62U, 0x0001U);

    struct table final {
        std::uint32_t tag;
        std::vector<std::byte> bytes;
    };
    std::array tables{
        table{0x6E616D65U, std::move(name)},
        table{0x68656164U, std::move(head)},
        table{0x4F532F32U, std::move(os2)}};
    const std::size_t directory_bytes = 12U + tables.size() * 16U;
    std::size_t output_size = directory_bytes;
    for (const auto& value : tables) {
        output_size += (value.bytes.size() + 3U) & ~std::size_t{3U};
    }
    std::vector<std::byte> result(output_size);
    write_u32(result, 0U, 0x00010000U);
    write_u16(result, 4U, static_cast<std::uint16_t>(tables.size()));
    std::size_t table_offset = directory_bytes;
    for (std::size_t index = 0U; index < tables.size(); ++index) {
        const auto record = 12U + index * 16U;
        write_u32(result, record, tables[index].tag);
        write_u32(result, record + 8U,
            static_cast<std::uint32_t>(table_offset));
        write_u32(result, record + 12U,
            static_cast<std::uint32_t>(tables[index].bytes.size()));
        std::copy(tables[index].bytes.begin(), tables[index].bytes.end(),
            result.begin() + static_cast<std::ptrdiff_t>(table_offset));
        table_offset +=
            (tables[index].bytes.size() + 3U) & ~std::size_t{3U};
    }
    return result;
}

void sfnt_metadata_matches_managed_selection_and_style() {
    const auto font_data = make_sfnt_metadata_fixture();
    sfnt_font_view font{};
    font_error error = font_error::none;
    require(sfnt_font_view::try_create(font_data, 0U, font, &error));

    sfnt_name_requirements requirements{};
    require(font.try_get_name_requirements(
        progpu::native::text::sfnt_name_ids::family_name,
        requirements, &error));
    require(error == font_error::none && requirements.utf8_bytes == 11U &&
        requirements.platform_id == 0U && requirements.score == 12);
    std::array<char, 11U> family{};
    std::size_t written = 0U;
    require(font.try_decode_name(
        progpu::native::text::sfnt_name_ids::family_name,
        family, written, &requirements, &error));
    require(written == family.size() &&
        std::string_view{family.data(), family.size()} == "ProGPU Sans");

    std::array<char, 19U> full{};
    require(font.try_decode_name(
        progpu::native::text::sfnt_name_ids::full_name,
        full, written, &requirements, &error));
    require(written == full.size() && requirements.score == 14 &&
        std::string_view{full.data(), full.size()} == "ProGPU Sans Regular");

    std::array<char, 18U> short_output{};
    short_output.fill('x');
    require(!font.try_decode_name(
        progpu::native::text::sfnt_name_ids::full_name,
        short_output, written, nullptr, &error));
    require(error == font_error::insufficient_buffer && written == 0U &&
        short_output.front() == 'x');

    sfnt_face_style style{};
    require(font.try_get_face_style(style));
    require(style.weight == 725U && style.width == 8U && style.italic);
    std::uint16_t embedding_rights = 0U;
    require(font.try_get_embedding_rights(embedding_rights) &&
        embedding_rights == 0x0008U);
    require(!font.try_get_name_requirements(
        progpu::native::text::sfnt_name_ids::version,
        requirements, &error));
}

void glyph_id_preserving_sfnt_subset_matches_managed_contract() {
    const auto font_data = make_sfnt_subset_fixture();
    constexpr std::array<std::uint16_t, 1U> requested{2U};
    sfnt_subset_requirements requirements{};
    font_error error = font_error::none;
    require(try_get_glyph_id_preserving_sfnt_subset_requirements(
        font_data, 0U, requested, requirements, &error));
    require(error == font_error::none &&
        requirements.font_bytes < font_data.size());

    std::vector<std::byte> short_output(requirements.font_bytes - 1U,
        std::byte{0x5AU});
    require(!try_create_glyph_id_preserving_sfnt_subset(
        font_data, 0U, requested, short_output, requirements, &error));
    require(error == font_error::insufficient_buffer &&
        short_output.front() == std::byte{0x5AU});

    std::vector<std::byte> output(requirements.font_bytes);
    require(try_create_glyph_id_preserving_sfnt_subset(
        font_data, 0U, requested, output, requirements, &error));
    std::uint64_t subset_hash = 1469598103934665603ULL;
    for (const auto value : output) {
        subset_hash ^= std::to_integer<std::uint8_t>(value);
        subset_hash *= 1099511628211ULL;
    }
    require(output.size() == 272U &&
        subset_hash == 10017802304682166674ULL);
    sfnt_font_view subset{};
    require(sfnt_font_view::try_create(output, 0U, subset, &error));
    std::uint16_t glyph_count = 0U;
    require(subset.try_get_glyph_count(glyph_count) && glyph_count == 4U);
    sfnt_table_view table{};
    require(!subset.try_get_table(
        open_type_tag::from_chars('D', 'S', 'I', 'G'), table));
    require(subset.try_get_table(
        open_type_tag::from_chars('h', 'e', 'a', 'd'), table));
    require(static_cast<std::int16_t>(
        (std::to_integer<std::uint16_t>(table.bytes[50U]) << 8U) |
        std::to_integer<std::uint16_t>(table.bytes[51U])) == 1);
    sfnt_glyph_data_view component{};
    sfnt_glyph_data_view composite_glyph{};
    sfnt_glyph_data_view omitted{};
    require(subset.try_get_glyph_data(1U, component) &&
        component.x_max == 20);
    require(subset.try_get_glyph_data(2U, composite_glyph) &&
        composite_glyph.x_max == 40);
    require(subset.try_get_glyph_data(3U, omitted) && omitted.empty());

    constexpr std::array<std::byte, 3U> invalid{};
    require(!try_get_glyph_id_preserving_sfnt_subset_requirements(
        invalid, 0U, requested, requirements, &error));
    require(error == font_error::invalid_face &&
        requirements.font_bytes == 0U);
}

void compact_sfnt_subset_matches_managed_contract() {
    const auto font_data = make_sfnt_subset_fixture();
    constexpr std::array<std::uint16_t, 1U> requested{2U};
    sfnt_subset_requirements requirements{};
    font_error error = font_error::none;
    require(try_get_compact_sfnt_subset_requirements(
        font_data, 0U, requested, requirements, &error));
    require(error == font_error::none &&
        requirements.glyph_map_count == 3U);
    std::vector<std::byte> output(requirements.font_bytes);
    std::vector<sfnt_glyph_remap> glyph_map(requirements.glyph_map_count);
    require(try_create_compact_sfnt_subset(
        font_data, 0U, requested, output, glyph_map, requirements, &error));
    require(glyph_map == std::vector<sfnt_glyph_remap>{
        {0U, 0U}, {1U, 1U}, {2U, 2U}});

    std::uint64_t subset_hash = 1469598103934665603ULL;
    for (const auto value : output) {
        subset_hash ^= std::to_integer<std::uint8_t>(value);
        subset_hash *= 1099511628211ULL;
    }
    require(output.size() == 264U &&
        subset_hash == 5117190155084041207ULL);

    sfnt_font_view subset{};
    require(sfnt_font_view::try_create(output, 0U, subset, &error));
    std::uint16_t glyph_count = 0U;
    require(subset.try_get_glyph_count(glyph_count) && glyph_count == 3U);
    sfnt_horizontal_glyph_metrics metrics{};
    require(subset.try_get_horizontal_glyph_metrics(2U, metrics) &&
        metrics.advance_width == 502U);
    sfnt_glyph_data_view composite{};
    require(subset.try_get_glyph_data(2U, composite) &&
        composite.contour_count == -1 && composite.x_max == 40 &&
        composite.bytes.size() >= 14U);
    require((std::to_integer<std::uint16_t>(composite.bytes[12U]) << 8U |
        std::to_integer<std::uint16_t>(composite.bytes[13U])) == 1U);

    std::vector<std::byte> short_output(requirements.font_bytes - 1U,
        std::byte{0x6BU});
    std::vector<sfnt_glyph_remap> short_map(
        requirements.glyph_map_count, sfnt_glyph_remap{9U, 9U});
    require(!try_create_compact_sfnt_subset(
        font_data, 0U, requested, short_output, short_map,
        requirements, &error));
    require(error == font_error::insufficient_buffer &&
        short_output.front() == std::byte{0x6BU} &&
        short_map.front() == sfnt_glyph_remap{9U, 9U});
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

std::vector<std::byte> make_style_fvar() {
    std::vector<std::byte> result(96U);
    write_u16(result, 0U, 1U);
    write_u16(result, 2U, 0U);
    write_u16(result, 4U, 16U);
    write_u16(result, 6U, 2U);
    write_u16(result, 8U, 4U);
    write_u16(result, 10U, 20U);
    const auto write_axis = [&](std::size_t offset,
                                open_type_tag tag,
                                std::int32_t minimum,
                                std::int32_t default_value,
                                std::int32_t maximum,
                                std::uint16_t name_id) {
        write_u32(result, offset, tag.value);
        write_u32(result, offset + 4U,
            static_cast<std::uint32_t>(minimum));
        write_u32(result, offset + 8U,
            static_cast<std::uint32_t>(default_value));
        write_u32(result, offset + 12U,
            static_cast<std::uint32_t>(maximum));
        write_u16(result, offset + 16U, 0U);
        write_u16(result, offset + 18U, name_id);
    };
    write_axis(16U, open_type_tag::from_chars('w', 'g', 'h', 't'),
        100 << 16, 400 << 16, 900 << 16, 256U);
    write_axis(36U, open_type_tag::from_chars('w', 'd', 't', 'h'),
        50 << 16, 100 << 16, 200 << 16, 257U);
    write_axis(56U, open_type_tag::from_chars('i', 't', 'a', 'l'),
        0, 0, 1 << 16, 258U);
    write_axis(76U, open_type_tag::from_chars('s', 'l', 'n', 't'),
        -20 * (1 << 16), 0, 0, 259U);
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

void sfnt_simple_glyph_shaper_matches_managed_utf16_contract() {
    const auto data = make_font();
    sfnt_font_view font{};
    font_error error = font_error::invalid_argument;
    require(sfnt_font_view::try_create(data, 0U, font, &error));

    constexpr std::array<char16_t, 7U> text{
        u'A',
        static_cast<char16_t>(0xD83DU),
        static_cast<char16_t>(0xDE00U),
        static_cast<char16_t>(0x00ADU),
        u'\n',
        static_cast<char16_t>(0x0085U),
        static_cast<char16_t>(0xD800U)};
    sfnt_simple_glyph_run_requirements requirements{};
    require(try_get_sfnt_simple_glyph_run_requirements(
        text, requirements, &error));
    require(error == font_error::none &&
        requirements.cluster_map_count == 7U &&
        requirements.glyph_count == 6U);
    require(is_sfnt_simple_formatting_control(0x1FU) &&
        is_sfnt_simple_formatting_control(0x7FU) &&
        is_sfnt_simple_formatting_control(0x9FU) &&
        !is_sfnt_simple_formatting_control(0x20U));
    std::uint32_t code_point = 99U;
    std::uint32_t code_unit_count = 99U;
    require(try_read_sfnt_simple_code_point(
        text, 1U, code_point, code_unit_count, &error));
    require(code_point == 0x1F600U && code_unit_count == 2U);
    require(try_read_sfnt_simple_code_point(
        text, 6U, code_point, code_unit_count, &error));
    require(code_point == 0xD800U && code_unit_count == 1U);
    require(!try_read_sfnt_simple_code_point(
        text, text.size(), code_point, code_unit_count, &error));
    require(error == font_error::invalid_argument && code_point == 0U &&
        code_unit_count == 0U);

    std::array<std::uint16_t, 7U> cluster_map{};
    std::array<std::uint16_t, 6U> glyph_indices{};
    std::uint32_t glyph_count = 99U;
    require(try_build_sfnt_simple_glyph_run(
        font,
        text,
        2U,
        6U,
        cluster_map,
        glyph_indices,
        glyph_count,
        &error));
    require(glyph_count == 6U &&
        cluster_map == std::array<std::uint16_t, 7U>{0U, 1U, 1U, 2U, 3U, 4U, 5U} &&
        glyph_indices == std::array<std::uint16_t, 6U>{3U, 7U, 6U, 2U, 2U, 0U});

    constexpr std::array<sfnt_simple_glyph_metrics, 6U> metrics{
        sfnt_simple_glyph_metrics{250U, 300U},
        sfnt_simple_glyph_metrics{350U, 400U},
        sfnt_simple_glyph_metrics{450U, 500U},
        sfnt_simple_glyph_metrics{999U, 999U},
        sfnt_simple_glyph_metrics{999U, 999U},
        sfnt_simple_glyph_metrics{550U, 600U}};
    std::array<std::uint8_t, 6U> glyph_state_scratch{};
    std::array<std::int32_t, 6U> advances{};
    require(try_fill_sfnt_simple_glyph_advances(
        text,
        cluster_map,
        glyph_indices,
        metrics,
        1000U,
        10.0,
        1.0,
        false,
        glyph_state_scratch,
        advances,
        &error));
    require(advances == std::array<std::int32_t, 6U>{2, 4, 4, 0, 0, 6});
    require(try_fill_sfnt_simple_glyph_advances(
        text,
        cluster_map,
        glyph_indices,
        metrics,
        1000U,
        10.0,
        1.0,
        true,
        glyph_state_scratch,
        advances,
        &error));
    require(advances == std::array<std::int32_t, 6U>{3, 4, 5, 0, 0, 6});

    constexpr std::array<std::uint16_t, 2U> shared_cluster_map{0U, 0U};
    constexpr std::array<std::uint16_t, 1U> shared_glyph{3U};
    constexpr std::array<sfnt_simple_glyph_metrics, 1U> shared_metrics{
        sfnt_simple_glyph_metrics{250U, 300U}};
    std::array<std::uint8_t, 1U> shared_state{};
    std::array<std::int32_t, 1U> shared_advance{};
    require(try_fill_sfnt_simple_glyph_advances(
        std::array<char16_t, 2U>{u'A', u'\n'},
        shared_cluster_map,
        shared_glyph,
        shared_metrics,
        1000U,
        10.0,
        1.0,
        false,
        shared_state,
        shared_advance,
        &error));
    require(shared_advance[0U] == 2);
    require(try_fill_sfnt_simple_glyph_advances(
        std::array<char16_t, 2U>{u'\n', u'A'},
        shared_cluster_map,
        shared_glyph,
        shared_metrics,
        1000U,
        10.0,
        1.0,
        false,
        shared_state,
        shared_advance,
        &error));
    require(shared_advance[0U] == 0);

    cluster_map.fill(99U);
    glyph_indices.fill(99U);
    glyph_count = 99U;
    require(!try_build_sfnt_simple_glyph_run(
        font,
        text,
        2U,
        6U,
        std::span<std::uint16_t>{cluster_map}.first(6U),
        glyph_indices,
        glyph_count,
        &error));
    require(error == font_error::insufficient_buffer && glyph_count == 0U &&
        cluster_map[0U] == 99U && glyph_indices[0U] == 99U);

    advances.fill(99);
    auto overflowing_metrics = metrics;
    overflowing_metrics[0U].advance_width =
        std::numeric_limits<std::uint32_t>::max();
    require(!try_fill_sfnt_simple_glyph_advances(
        text,
        std::array<std::uint16_t, 7U>{0U, 1U, 1U, 2U, 3U, 4U, 5U},
        std::array<std::uint16_t, 6U>{3U, 7U, 6U, 2U, 2U, 0U},
        overflowing_metrics,
        1U,
        1.0,
        1.0,
        false,
        glyph_state_scratch,
        advances,
        &error));
    require(error == font_error::invalid_argument && advances[0U] == 99);
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
    constexpr std::array<std::int16_t, 1U> changed_coordinates{1};
    auto coordinate_options = latin_options;
    coordinate_options.normalized_coordinates = changed_coordinates;
    require(!plan.matches(font, coordinate_options));
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

void open_type_shaping_route_matches_managed_plan_selection() {
    const auto make_script_table = [](
                                       std::span<const open_type_tag> scripts) {
        std::vector<std::byte> table(12U + scripts.size() * 6U);
        write_u16(table, 0U, 1U);
        write_u16(table, 2U, 0U);
        write_u16(table, 4U, 10U);
        write_u16(table, 10U, static_cast<std::uint16_t>(scripts.size()));
        for (std::size_t index = 0U; index < scripts.size(); ++index) {
            write_u32(table, 12U + index * 6U, scripts[index].value);
        }
        return table;
    };
    const auto make_feature_table = [](open_type_tag feature) {
        std::vector<std::byte> table(18U);
        write_u16(table, 0U, 1U);
        write_u16(table, 2U, 0U);
        write_u16(table, 6U, 10U);
        write_u16(table, 10U, 1U);
        write_u32(table, 12U, feature.value);
        return table;
    };
    constexpr auto deva = open_type_tag::from_chars('d', 'e', 'v', 'a');
    constexpr auto dev2 = open_type_tag::from_chars('d', 'e', 'v', '2');
    constexpr auto dev3 = open_type_tag::from_chars('d', 'e', 'v', '3');
    constexpr std::array all_devanagari_generations{dev2, dev3};
    const std::array all_tables{table_data{
        open_type_tag::from_chars('G', 'S', 'U', 'B'),
        make_script_table(all_devanagari_generations)}};
    const auto all_data = make_font(
        0U, 22U, 0U, false, false, false, all_tables);
    sfnt_font_view all_font{};
    font_error error = font_error::invalid_argument;
    require(sfnt_font_view::try_create(all_data, 0U, all_font, &error));
    open_type_shaping_route route{};
    require(try_resolve_open_type_shaping_route(
        all_font, deva, shaping_direction::unspecified, route, &error));
    require(error == font_error::none && route.unicode_script == deva &&
        route.layout_script == dev3 &&
        route.direction == shaping_direction::left_to_right &&
        route.complex_script == open_type_complex_script::use &&
        route.use_shaper && !route.indic_shaper && !route.khmer_shaper &&
        !route.myanmar_shaper && !route.arabic_shaper);

    constexpr std::array second_only{dev2};
    const std::array second_tables{table_data{
        open_type_tag::from_chars('G', 'S', 'U', 'B'),
        make_script_table(second_only)}};
    const auto second_data = make_font(
        0U, 22U, 0U, false, false, false, second_tables);
    sfnt_font_view second_font{};
    require(sfnt_font_view::try_create(
        second_data, 0U, second_font, &error));
    require(try_resolve_open_type_shaping_route(
        second_font, deva, shaping_direction::unspecified, route, &error));
    require(route.layout_script == dev2 && !route.use_shaper &&
        route.indic_shaper &&
        route.complex_script == open_type_complex_script::indic);

    const auto plain_data = make_font();
    sfnt_font_view plain_font{};
    require(sfnt_font_view::try_create(
        plain_data, 0U, plain_font, &error));
    require(try_resolve_open_type_shaping_route(
        plain_font,
        open_type_tag::from_chars('s', 'i', 'n', 'h'),
        shaping_direction::unspecified,
        route,
        &error));
    require(route.use_shaper &&
        route.complex_script == open_type_complex_script::use);
    require(try_resolve_open_type_shaping_route(
        plain_font,
        open_type_tag::from_chars('k', 'h', 'm', 'r'),
        shaping_direction::unspecified,
        route,
        &error));
    require(route.khmer_shaper &&
        route.complex_script == open_type_complex_script::khmer);
    require(try_resolve_open_type_shaping_route(
        plain_font,
        open_type_tag::from_chars('m', 'y', 'm', 'r'),
        shaping_direction::unspecified,
        route,
        &error));
    require(route.myanmar_shaper &&
        route.complex_script == open_type_complex_script::myanmar);
    require(try_resolve_open_type_shaping_route(
        plain_font,
        open_type_tag::from_chars('a', 'r', 'a', 'b'),
        shaping_direction::unspecified,
        route,
        &error));
    require(route.arabic_shaper && !route.use_shaper &&
        route.direction == shaping_direction::right_to_left &&
        route.complex_script == open_type_complex_script::none);
    require(try_resolve_open_type_shaping_route(
        plain_font,
        open_type_tag::from_chars('h', 'e', 'b', 'r'),
        shaping_direction::unspecified,
        route,
        &error));
    require(route.compose_hebrew_presentation_forms);
    const std::array mark_tables{table_data{
        open_type_tag::from_chars('G', 'P', 'O', 'S'),
        make_feature_table(open_type_tag::from_chars('m', 'a', 'r', 'k'))}};
    const auto mark_data = make_font(
        0U, 22U, 0U, false, false, false, mark_tables);
    sfnt_font_view mark_font{};
    require(sfnt_font_view::try_create(mark_data, 0U, mark_font, &error));
    require(try_resolve_open_type_shaping_route(
        mark_font,
        open_type_tag::from_chars('h', 'e', 'b', 'r'),
        shaping_direction::unspecified,
        route,
        &error));
    require(!route.compose_hebrew_presentation_forms);
    require(try_resolve_open_type_shaping_route(
        plain_font,
        open_type_tag::from_chars('a', 'd', 'l', 'm'),
        shaping_direction::top_to_bottom,
        route,
        &error));
    require(route.arabic_shaper && route.use_shaper &&
        route.direction == shaping_direction::top_to_bottom &&
        route.complex_script == open_type_complex_script::use);
    require(try_resolve_open_type_shaping_route(
        plain_font,
        open_type_tag::from_chars('h', 'i', 'r', 'a'),
        shaping_direction::unspecified,
        route,
        &error));
    require(route.unicode_script ==
            open_type_tag::from_chars('k', 'a', 'n', 'a') &&
        route.layout_script == route.unicode_script);

    const auto untouched = route;
    require(!try_resolve_open_type_shaping_route(
        plain_font,
        deva,
        static_cast<shaping_direction>(99U),
        route,
        &error));
    require(error == font_error::invalid_argument &&
        route.unicode_script.value == 0U &&
        untouched.unicode_script.value != 0U);

    require(resolve_open_type_language_tag("AZ_latn") ==
        open_type_tag::from_chars('A', 'Z', 'E', ' '));
    require(resolve_open_type_language_tag("zh-HANT-HK") ==
        open_type_tag::from_chars('Z', 'H', 'H', ' '));
    require(resolve_open_type_language_tag("zh_sg") ==
        open_type_tag::from_chars('Z', 'H', 'S', ' '));
    require(resolve_open_type_language_tag("pl") ==
        open_type_tag::from_chars('P', 'L', 'K', ' '));
    require(resolve_open_type_language_tag("unknown") ==
        open_type_tag::from_chars('d', 'f', 'l', 't'));
}

void open_type_feature_plan_matches_managed_script_and_direction_policy() {
    constexpr auto feature = [](char a, char b, char c, char d) {
        return open_type_tag::from_chars(a, b, c, d);
    };
    const auto defaults = get_default_open_type_feature_settings();
    require(defaults.size() == 26U &&
        defaults.front().tag == feature('r', 'v', 'r', 'n') &&
        defaults.back().tag == feature('r', 'a', 'n', 'd') &&
        defaults.back().value == 0xFFFFU);

    std::array<open_type_tag, 128U> requested{};
    std::array<shaping_feature, 128U> settings{};
    std::uint32_t requested_written = 0U;
    std::uint32_t settings_written = 0U;
    font_error error = font_error::invalid_argument;
    open_type_feature_plan_requirements requirements{};
    open_type_shaping_route route{
        feature('l', 'a', 't', 'n'),
        feature('l', 'a', 't', 'n'),
        shaping_direction::left_to_right};
    require(try_get_open_type_feature_plan_requirements(
        route, defaults, requirements, &error));
    require(requirements.requested_feature_capacity == 28U &&
        requirements.feature_setting_capacity == 28U);
    require(try_resolve_open_type_feature_plan(
        route,
        defaults,
        {},
        requested,
        settings,
        requested_written,
        settings_written,
        &error));
    require(error == font_error::none && requested_written == 28U &&
        settings_written == 1U &&
        requested[0U] == feature('l', 't', 'r', 'a') &&
        requested[1U] == feature('l', 't', 'r', 'm') &&
        requested[2U] == feature('r', 'v', 'r', 'n') &&
        settings[0U].tag == feature('r', 'a', 'n', 'd') &&
        settings[0U].value == 0xFFFFU);

    route.direction = shaping_direction::top_to_bottom;
    require(try_resolve_open_type_feature_plan(
        route,
        defaults,
        {},
        requested,
        settings,
        requested_written,
        settings_written,
        &error));
    require(requested_written == 28U && settings_written == 1U &&
        requested[0U] == feature('v', 'e', 'r', 't') &&
        requested[1U] == feature('v', 'r', 't', '2') &&
        requested[2U] == feature('v', 'k', 'r', 'n') &&
        std::find(requested.begin(), requested.begin() + requested_written,
            feature('k', 'e', 'r', 'n')) ==
            requested.begin() + requested_written);

    route = open_type_shaping_route{
        feature('k', 'h', 'm', 'r'),
        feature('k', 'h', 'm', 'r'),
        shaping_direction::left_to_right,
        open_type_complex_script::khmer,
        false,
        false,
        true};
    require(try_resolve_open_type_feature_plan(
        route,
        defaults,
        {},
        requested,
        settings,
        requested_written,
        settings_written,
        &error));
    const auto find_setting = [&](open_type_tag tag) {
        return std::find_if(
            settings.begin(),
            settings.begin() + settings_written,
            [tag](const shaping_feature& item) { return item.tag == tag; });
    };
    require(requested[0U] == feature('l', 't', 'r', 'a') &&
        requested[2U] == feature('r', 'v', 'r', 'n') &&
        std::find(requested.begin(), requested.begin() + requested_written,
            feature('c', 'f', 'a', 'r')) !=
            requested.begin() + requested_written);
    const auto disabled_khmer_liga = find_setting(feature('l', 'i', 'g', 'a'));
    require(disabled_khmer_liga != settings.begin() + settings_written &&
        disabled_khmer_liga->value == 0U);

    constexpr std::array explicit_liga{feature('l', 'i', 'g', 'a')};
    require(try_resolve_open_type_feature_plan(
        route,
        defaults,
        explicit_liga,
        requested,
        settings,
        requested_written,
        settings_written,
        &error));
    require(find_setting(feature('l', 'i', 'g', 'a')) ==
        settings.begin() + settings_written);

    constexpr std::array indic_base{
        open_type_feature_setting{feature('l', 'i', 'g', 'a'), 7U},
        open_type_feature_setting{feature('k', 'e', 'r', 'n'), 3U}};
    route = open_type_shaping_route{
        feature('d', 'e', 'v', 'a'),
        feature('d', 'e', 'v', '2'),
        shaping_direction::left_to_right,
        open_type_complex_script::indic,
        false,
        true};
    require(try_resolve_open_type_feature_plan(
        route,
        indic_base,
        explicit_liga,
        requested,
        settings,
        requested_written,
        settings_written,
        &error));
    const auto disabled_indic_liga = find_setting(feature('l', 'i', 'g', 'a'));
    require(disabled_indic_liga != settings.begin() + settings_written &&
        disabled_indic_liga->value == 0U);

    constexpr std::array arabic_base{
        open_type_feature_setting{feature('s', 't', 'c', 'h'), 4U},
        open_type_feature_setting{feature('k', 'e', 'r', 'n'), 1U}};
    route = open_type_shaping_route{
        feature('a', 'r', 'a', 'b'),
        feature('a', 'r', 'a', 'b'),
        shaping_direction::right_to_left,
        open_type_complex_script::none,
        false,
        false,
        false,
        false,
        true};
    require(try_resolve_open_type_feature_plan(
        route,
        arabic_base,
        {},
        requested,
        settings,
        requested_written,
        settings_written,
        &error));
    const auto custom_stch = find_setting(feature('s', 't', 'c', 'h'));
    require(requested[0U] == feature('r', 't', 'l', 'a') &&
        requested[1U] == feature('r', 't', 'l', 'm') &&
        custom_stch != settings.begin() + settings_written &&
        custom_stch->value == 4U &&
        std::find(requested.begin(), requested.begin() + requested_written,
            feature('m', 's', 'e', 't')) !=
            requested.begin() + requested_written);

    requested.fill(feature('z', 'z', 'z', 'z'));
    settings.fill(shaping_feature{feature('z', 'z', 'z', 'z'), 9U, 2U, 3U});
    requested_written = 99U;
    settings_written = 99U;
    require(!try_resolve_open_type_feature_plan(
        route,
        arabic_base,
        {},
        std::span<open_type_tag>{requested}.first(1U),
        std::span<shaping_feature>{settings}.first(1U),
        requested_written,
        settings_written,
        &error));
    require(error == font_error::insufficient_buffer &&
        requested_written == 0U && settings_written == 0U &&
        requested[0U] == feature('z', 'z', 'z', 'z') &&
        settings[0U].tag == feature('z', 'z', 'z', 'z'));
}

void open_type_requested_features_match_managed_cpu_shaper_normalization() {
    constexpr auto tag = [](char a, char b, char c, char d) {
        return open_type_tag::from_chars(a, b, c, d);
    };
    constexpr std::array requested{
        shaping_feature{tag('l', 'i', 'g', 'a'), 0U, 0U, 0xFFFFFFFFU},
        shaping_feature{tag('s', 's', '0', '1'), 0xFFFFFFFFU, 0U, 0xFFFFFFFFU},
        shaping_feature{tag('f', 'r', 'a', 'c'), 0U, 2U, 4U},
        shaping_feature{tag('c', 'v', '0', '1'), 2U, 1U, 3U},
        shaping_feature{tag('c', 'v', '0', '1'), 3U, 4U, 5U},
        shaping_feature{tag('l', 'i', 'g', 'a'), 1U, 6U, 8U}};
    open_type_requested_feature_requirements requirements{};
    font_error error = font_error::invalid_argument;
    require(try_get_open_type_requested_feature_requirements(
        requested, requirements, &error));
    require(error == font_error::none &&
        requirements.base_feature_capacity == 29U &&
        requirements.explicit_feature_capacity == 4U &&
        requirements.ranged_feature_capacity == 4U);

    std::array<open_type_feature_setting, 29U> base{};
    std::array<open_type_tag, 4U> explicit_tags{};
    std::array<shaping_feature, 4U> ranges{};
    std::uint32_t base_written = 0U;
    std::uint32_t explicit_written = 0U;
    std::uint32_t ranged_written = 0U;
    require(try_resolve_open_type_requested_features(
        requested,
        base,
        explicit_tags,
        ranges,
        base_written,
        explicit_written,
        ranged_written,
        &error));
    require(base_written == 29U && explicit_written == 4U &&
        ranged_written == 4U);
    const auto find_base = [&](open_type_tag feature) {
        return std::find_if(
            base.begin(),
            base.begin() + base_written,
            [feature](const auto& item) { return item.tag == feature; });
    };
    const auto liga = find_base(tag('l', 'i', 'g', 'a'));
    const auto ss01 = find_base(tag('s', 's', '0', '1'));
    require(liga != base.begin() + base_written && liga->value == 0U &&
        ss01 != base.begin() + base_written &&
        ss01->value == 0x7FFFFFFFU &&
        base[base_written - 2U] ==
            open_type_feature_setting{tag('c', 'v', '0', '1'), 1U} &&
        base[base_written - 1U] ==
            open_type_feature_setting{tag('l', 'i', 'g', 'a'), 1U});
    require(explicit_tags == std::array{
        tag('l', 'i', 'g', 'a'), tag('s', 's', '0', '1'),
        tag('f', 'r', 'a', 'c'), tag('c', 'v', '0', '1')});
    require(ranges[0U] == requested[2U] && ranges[1U] == requested[3U] &&
        ranges[2U] == requested[4U] && ranges[3U] == requested[5U]);

    std::array<open_type_feature_setting, 1U> short_base{
        open_type_feature_setting{tag('z', 'z', 'z', 'z'), 9U}};
    std::array<open_type_tag, 1U> short_explicit{tag('z', 'z', 'z', 'z')};
    std::array<shaping_feature, 1U> short_ranges{
        shaping_feature{tag('z', 'z', 'z', 'z'), 9U, 2U, 3U}};
    base_written = explicit_written = ranged_written = 99U;
    require(!try_resolve_open_type_requested_features(
        requested,
        short_base,
        short_explicit,
        short_ranges,
        base_written,
        explicit_written,
        ranged_written,
        &error));
    require(error == font_error::insufficient_buffer &&
        base_written == 0U && explicit_written == 0U &&
        ranged_written == 0U && short_base[0U].value == 9U &&
        short_explicit[0U] == tag('z', 'z', 'z', 'z') &&
        short_ranges[0U].value == 9U);

    constexpr std::array invalid{
        shaping_feature{tag('l', 'i', 'g', 'a'), 1U, 8U, 4U}};
    require(!try_get_open_type_requested_feature_requirements(
        invalid, requirements, &error));
    require(error == font_error::invalid_argument);
}

void open_type_shape_configuration_connects_managed_planning_stages() {
    constexpr auto tag = [](char a, char b, char c, char d) {
        return open_type_tag::from_chars(a, b, c, d);
    };
    std::vector<std::byte> gsub(18U);
    write_u16(gsub, 0U, 1U);
    write_u16(gsub, 2U, 0U);
    write_u16(gsub, 4U, 10U);
    write_u16(gsub, 10U, 1U);
    write_u32(gsub, 12U, tag('d', 'e', 'v', '2').value);
    const std::array tables{table_data{
        tag('G', 'S', 'U', 'B'), std::move(gsub)}};
    const auto data = make_font(
        0U, 22U, 0U, false, false, false, tables);
    sfnt_font_view font{};
    font_error error = font_error::invalid_argument;
    require(sfnt_font_view::try_create(data, 0U, font, &error));

    constexpr std::array input{
        unicode_scalar{0x0915U, 0U, 1U},
        unicode_scalar{0x093FU, 1U, 1U}};
    constexpr std::array features{
        shaping_feature{tag('l', 'i', 'g', 'a'), 0U, 0U, 0xFFFFFFFFU},
        shaping_feature{tag('c', 'v', '0', '1'), 2U, 1U, 2U},
        shaping_feature{tag('z', 'z', 'z', 'z'), 0U, 0U, 1U}};
    const open_type_shape_configuration_request request{
        {}, "pl", shaping_direction::unspecified, features};
    open_type_shape_configuration_requirements requirements{};
    require(try_get_open_type_shape_configuration_requirements(
        font, input, request, requirements, &error));
    require(error == font_error::none &&
        requirements.base_feature_capacity == 27U &&
        requirements.explicit_feature_capacity == 3U &&
        requirements.requested_feature_capacity == 59U &&
        requirements.feature_setting_capacity == 61U);

    std::vector<open_type_feature_setting> base(
        requirements.base_feature_capacity);
    std::vector<open_type_tag> explicit_tags(
        requirements.explicit_feature_capacity);
    std::vector<open_type_tag> requested(
        requirements.requested_feature_capacity);
    std::vector<shaping_feature> settings(
        requirements.feature_setting_capacity);
    open_type_shape_configuration configuration{};
    require(try_prepare_open_type_shape_configuration(
        font,
        input,
        request,
        base,
        explicit_tags,
        requested,
        settings,
        configuration,
        &error));
    const auto contains_tag = [](auto values, open_type_tag value) {
        return std::find(values.begin(), values.end(), value) != values.end();
    };
    require(error == font_error::none &&
        configuration.route.unicode_script == tag('d', 'e', 'v', 'a') &&
        configuration.route.layout_script == tag('d', 'e', 'v', '2') &&
        configuration.route.indic_shaper &&
        configuration.route.complex_script == open_type_complex_script::indic &&
        configuration.options.unicode_script == tag('d', 'e', 'v', 'a') &&
        configuration.options.script == tag('d', 'e', 'v', '2') &&
        configuration.options.language == tag('P', 'L', 'K', ' ') &&
        configuration.options.direction == shaping_direction::left_to_right &&
        configuration.base_features_written == 27U &&
        configuration.explicit_features_written == 3U &&
        contains_tag(configuration.options.requested_features,
            tag('l', 't', 'r', 'a')) &&
        contains_tag(configuration.options.requested_features,
            tag('c', 'v', '0', '1')) &&
        !contains_tag(configuration.options.requested_features,
            tag('z', 'z', 'z', 'z')) &&
        contains_tag(configuration.options.explicit_features,
            tag('z', 'z', 'z', 'z')));
    const auto find_setting = [&](open_type_tag value) {
        return std::find_if(
            configuration.options.feature_settings.begin(),
            configuration.options.feature_settings.end(),
            [value](const shaping_feature& item) {
                return item.tag == value;
            });
    };
    const auto cv01 = find_setting(tag('c', 'v', '0', '1'));
    require(cv01 != configuration.options.feature_settings.end() &&
        cv01->value == 2U && cv01->start == 1U && cv01->end == 2U &&
        find_setting(tag('z', 'z', 'z', 'z')) ==
            configuration.options.feature_settings.end());

    base.assign(base.size(), open_type_feature_setting{
        tag('z', 'z', 'z', 'z'), 9U});
    explicit_tags.assign(explicit_tags.size(), tag('z', 'z', 'z', 'z'));
    requested.assign(requested.size(), tag('z', 'z', 'z', 'z'));
    settings.assign(settings.size(), shaping_feature{
        tag('z', 'z', 'z', 'z'), 9U, 2U, 3U});
    require(!try_prepare_open_type_shape_configuration(
        font,
        input,
        request,
        base,
        explicit_tags,
        std::span<open_type_tag>{requested}.first(requested.size() - 1U),
        settings,
        configuration,
        &error));
    require(error == font_error::insufficient_buffer &&
        configuration.requested_features_written == 0U &&
        base.front().value == 9U &&
        explicit_tags.front() == tag('z', 'z', 'z', 'z') &&
        requested.front() == tag('z', 'z', 'z', 'z') &&
        settings.front().value == 9U);

    auto invalid_request = request;
    invalid_request.cluster_level =
        static_cast<shaping_cluster_level>(99U);
    requirements = open_type_shape_configuration_requirements{
        99U, 99U, 99U, 99U};
    require(!try_get_open_type_shape_configuration_requirements(
        font, input, invalid_request, requirements, &error));
    require(error == font_error::invalid_argument &&
        requirements.base_feature_capacity == 0U);
    invalid_request = request;
    invalid_request.buffer_flags = static_cast<shaping_buffer_flags>(
        static_cast<std::uint8_t>(
            shaping_buffer_flags::preserve_default_ignorables) |
        static_cast<std::uint8_t>(
            shaping_buffer_flags::remove_default_ignorables));
    require(!try_get_open_type_shape_configuration_requirements(
        font, input, invalid_request, requirements, &error));
    require(error == font_error::invalid_argument);
}

void open_type_printable_ascii_matches_managed_fast_path() {
    constexpr auto tag = [](char a, char b, char c, char d) {
        return open_type_tag::from_chars(a, b, c, d);
    };
    constexpr std::array mappings{
        std::pair{0x20U, 1U},
        std::pair{0x41U, 2U},
        std::pair{0x56U, 3U}};
    const auto cmap = make_cmap_groups(mappings);
    const auto data = make_font(
        0U, 22U, 0U, false, false, false, {}, cmap);
    sfnt_font_view font{};
    font_error error = font_error::invalid_argument;
    require(sfnt_font_view::try_create(data, 0U, font, &error));
    constexpr std::array input{
        unicode_scalar{0x41U, 0U, 1U},
        unicode_scalar{0x20U, 1U, 1U},
        unicode_scalar{0x56U, 2U, 1U}};
    auto options = open_type_shape_run_options{};
    options.script = tag('l', 'a', 't', 'n');
    options.unicode_script = tag('l', 'a', 't', 'n');
    open_type_shape_run_requirements requirements{};
    require(try_get_open_type_shape_run_requirements(
        font, input, options, requirements, &error));
    require(error == font_error::none &&
        requirements.initial_glyph_count == input.size() &&
        requirements.grapheme_capacity == input.size());

    std::vector<shaping_glyph> glyphs(requirements.glyph_capacity);
    std::vector<unicode_grapheme_cluster> graphemes(
        requirements.grapheme_capacity);
    std::vector<shaping_attachment> attachments(
        requirements.glyph_capacity);
    std::vector<std::uint8_t> states(requirements.glyph_capacity);
    std::uint32_t glyph_count = 0U;
    require(try_shape_open_type_run(
        font,
        input,
        options,
        glyphs,
        open_type_shape_run_scratch{
            graphemes, {}, {}, attachments, states},
        glyph_count,
        &error));
    require(error == font_error::none && glyph_count == input.size() &&
        glyphs[0U].glyph_id == 2U && glyphs[0U].code_point == 0x41U &&
        glyphs[0U].cluster == 0 && glyphs[0U].advance_x == 600 &&
        glyphs[1U].glyph_id == 1U && glyphs[1U].code_point == 0x20U &&
        glyphs[1U].cluster == 1 && glyphs[1U].advance_x == 600 &&
        glyphs[2U].glyph_id == 3U && glyphs[2U].code_point == 0x56U &&
        glyphs[2U].cluster == 2 && glyphs[2U].advance_x == 600 &&
        graphemes[0U].input_index == 0U &&
        graphemes[0U].input_length == 1U &&
        graphemes[0U].scalar_index == 0U &&
        graphemes[0U].scalar_count == 1U &&
        graphemes[1U].input_index == 1U &&
        graphemes[1U].scalar_index == 1U &&
        graphemes[2U].input_index == 2U &&
        graphemes[2U].scalar_index == 2U);

    auto invalid_options = options;
    invalid_options.direction = shaping_direction::unspecified;
    require(!try_get_open_type_shape_run_requirements(
        font, input, invalid_options, requirements, &error));
    require(error == font_error::invalid_argument &&
        requirements.glyph_capacity == 0U);
    invalid_options = options;
    invalid_options.buffer_flags = static_cast<shaping_buffer_flags>(
        static_cast<std::uint8_t>(
            shaping_buffer_flags::preserve_default_ignorables) |
        static_cast<std::uint8_t>(
            shaping_buffer_flags::remove_default_ignorables));
    require(!try_get_open_type_shape_run_requirements(
        font, input, invalid_options, requirements, &error));
    require(error == font_error::invalid_argument);

    options.buffer_flags = shaping_buffer_flags::verify;
    require(try_get_open_type_shape_run_requirements(
        font, input, options, requirements, &error));
    require(requirements.verification_glyph_capacity ==
        requirements.glyph_capacity);
    glyphs.assign(requirements.glyph_capacity, {});
    graphemes.assign(requirements.grapheme_capacity, {});
    attachments.assign(requirements.glyph_capacity, {});
    states.assign(requirements.glyph_capacity, 0U);
    std::vector<shaping_glyph> verification_glyphs(
        requirements.verification_glyph_capacity);
    open_type_shape_verification_scratch verification{
        verification_glyphs};
    auto scratch = open_type_shape_run_scratch{};
    scratch.grapheme_clusters = graphemes;
    scratch.attachments = attachments;
    scratch.attachment_states = states;
    scratch.verification = &verification;
    require(try_shape_open_type_run(
        font,
        input,
        options,
        glyphs,
        scratch,
        glyph_count,
        &error));
    require(error == font_error::none && glyph_count == input.size());

    auto corrupted = glyphs;
    ++corrupted[1U].advance_x;
    require(!try_verify_open_type_shape_result(
        font,
        input,
        options,
        std::span<const shaping_glyph>{corrupted}.first(glyph_count),
        verification_glyphs,
        scratch,
        &error));
    require(error == font_error::verification_failed);
    require(!try_verify_open_type_shape_result(
        font,
        input,
        options,
        std::span<const shaping_glyph>{glyphs}.first(glyph_count),
        std::span<shaping_glyph>{verification_glyphs}.first(
            verification_glyphs.size() - 1U),
        scratch,
        &error));
    require(error == font_error::insufficient_buffer);

    verification.glyphs = std::span<shaping_glyph>{verification_glyphs}.first(
        verification_glyphs.size() - 1U);
    glyph_count = 99U;
    require(!try_shape_open_type_run(
        font,
        input,
        options,
        glyphs,
        scratch,
        glyph_count,
        &error));
    require(error == font_error::insufficient_buffer && glyph_count == 0U);

    verification.glyphs = verification_glyphs;
    options.direction = shaping_direction::right_to_left;
    require(try_get_open_type_shape_run_requirements(
        font, input, options, requirements, &error));
    require(try_shape_open_type_run(
        font,
        input,
        options,
        glyphs,
        scratch,
        glyph_count,
        &error));
    require(error == font_error::none && glyph_count == input.size() &&
        glyphs[0U].cluster == 2 && glyphs[1U].cluster == 1 &&
        glyphs[2U].cluster == 0);

    options.cluster_level = shaping_cluster_level::characters;
    require(try_get_open_type_shape_run_requirements(
        font, input, options, requirements, &error));
    require(requirements.verification_glyph_capacity == 0U);
    scratch.verification = nullptr;
    require(try_shape_open_type_run(
        font,
        input,
        options,
        glyphs,
        scratch,
        glyph_count,
        &error));
    require(error == font_error::none && glyph_count == input.size());
}

void open_type_random_alternates_match_managed_run_state() {
    std::vector<std::byte> gsub(80U);
    write_u16(gsub, 0U, 1U);
    write_u16(gsub, 4U, 10U);
    write_u16(gsub, 6U, 30U);
    write_u16(gsub, 8U, 44U);
    write_u16(gsub, 10U, 1U);
    write_u32(gsub, 12U,
        open_type_tag::from_chars('l', 'a', 't', 'n').value);
    write_u16(gsub, 16U, 8U);
    write_u16(gsub, 18U, 4U);
    write_u16(gsub, 22U, 0U);
    write_u16(gsub, 24U, 0xFFFFU);
    write_u16(gsub, 26U, 1U);
    write_u16(gsub, 28U, 0U);
    write_u16(gsub, 30U, 1U);
    write_u32(gsub, 32U,
        open_type_tag::from_chars('r', 'a', 'n', 'd').value);
    write_u16(gsub, 36U, 8U);
    write_u16(gsub, 38U, 0U);
    write_u16(gsub, 40U, 1U);
    write_u16(gsub, 42U, 0U);
    write_u16(gsub, 44U, 1U);
    write_u16(gsub, 46U, 6U);
    write_u16(gsub, 50U, 3U);
    write_u16(gsub, 52U, 0U);
    write_u16(gsub, 54U, 1U);
    write_u16(gsub, 56U, 8U);
    write_u16(gsub, 58U, 1U);
    write_u16(gsub, 60U, 10U);
    write_u16(gsub, 62U, 1U);
    write_u16(gsub, 64U, 16U);
    write_u16(gsub, 68U, 1U);
    write_u16(gsub, 70U, 1U);
    write_u16(gsub, 72U, 2U);
    write_u16(gsub, 74U, 2U);
    write_u16(gsub, 76U, 4U);
    write_u16(gsub, 78U, 5U);
    constexpr std::array mappings{
        std::pair{0x41U, 2U}, std::pair{0x42U, 2U}};
    const auto cmap = make_cmap_groups(mappings);
    const std::array tables{
        table_data{open_type_tag::from_chars('G', 'S', 'U', 'B'), gsub}};
    const auto data = make_font(
        0U, 22U, 0U, false, false, false, tables, cmap);
    sfnt_font_view font{};
    font_error error = font_error::none;
    require(sfnt_font_view::try_create(data, 0U, font, &error));

    constexpr std::array input{
        unicode_scalar{0x41U, 0U, 1U},
        unicode_scalar{0x42U, 1U, 1U}};
    constexpr auto random_feature =
        open_type_tag::from_chars('r', 'a', 'n', 'd');
    constexpr std::array requested{random_feature};
    constexpr std::array settings{shaping_feature{
        random_feature,
        std::numeric_limits<std::uint16_t>::max(),
        0U,
        std::numeric_limits<std::uint32_t>::max()}};
    open_type_shape_run_options options{
        open_type_tag::from_chars('l', 'a', 't', 'n')};
    options.requested_features = requested;
    options.feature_settings = settings;
    open_type_shape_run_requirements requirements{};
    require(try_get_open_type_shape_run_requirements(
        font, input, options, requirements, &error));
    std::array<shaping_glyph, 6U> glyphs{};
    std::array<unicode_grapheme_cluster, 2U> graphemes{};
    std::array<std::uint16_t, 1U> gsub_lookups{};
    std::array<shaping_attachment, 6U> attachments{};
    std::array<std::uint8_t, 6U> states{};
    const open_type_shape_run_scratch scratch{
        graphemes, gsub_lookups, {}, attachments, states};
    std::uint32_t glyph_count = 0U;
    require(try_shape_open_type_run(
        font, input, options, glyphs, scratch, glyph_count, &error));
    constexpr auto unsafe_break_and_concat =
        static_cast<std::uint32_t>(shaping_glyph_flags::unsafe_to_break) |
        static_cast<std::uint32_t>(shaping_glyph_flags::unsafe_to_concat);
    require(error == font_error::none && glyph_count == 2U &&
        glyphs[0U].glyph_id == 5U && glyphs[1U].glyph_id == 4U &&
        glyphs[0U].flags == shaping_glyph_flags::none &&
        static_cast<std::uint32_t>(glyphs[1U].flags) ==
            unsafe_break_and_concat);

    glyphs.fill({});
    require(try_shape_open_type_run(
        font, input, options, glyphs, scratch, glyph_count, &error));
    require(glyph_count == 2U && glyphs[0U].glyph_id == 5U &&
        glyphs[1U].glyph_id == 4U);
}

void open_type_common_preprocessing_matches_managed_stages() {
    constexpr std::array mappings{
        std::pair{0x05BCU, 2U},
        std::pair{0x05E9U, 6U},
        std::pair{0x0905U, 3U},
        std::pair{0x093AU, 4U},
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

    marks = {
        shaping_glyph{0U, 0x0301U, 0},
        shaping_glyph{0U, 0x0316U, 1}};
    count = 2U;
    require(try_preprocess_open_type_glyphs(
        font,
        open_type_tag::from_chars('l', 'a', 't', 'n'),
        shaping_cluster_level::monotone_characters,
        shaping_buffer_flags::beginning_of_text,
        true,
        marks,
        count,
        &error,
        nullptr,
        true));
    require(count == 2U && marks[0U].code_point == 0x0316U &&
        marks[1U].code_point == 0x0301U);

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

    std::array<shaping_glyph, 3U> constrained{
        shaping_glyph{3U, 0x0905U, 10},
        shaping_glyph{4U, 0x093AU, 11}};
    count = 2U;
    require(try_preprocess_open_type_glyphs(
        font,
        open_type_tag::from_chars('d', 'e', 'v', 'a'),
        shaping_cluster_level::monotone_graphemes,
        shaping_buffer_flags::none,
        true,
        constrained,
        count,
        &error));
    require(count == 3U && constrained[0U].code_point == 0x0905U &&
        constrained[1U].code_point == 0x25CCU &&
        constrained[1U].glyph_id == 1U &&
        constrained[1U].cluster == 11 &&
        constrained[2U].code_point == 0x093AU);

    const auto deva = open_type_tag::from_chars('d', 'e', 'v', 'a');
    const std::array<unicode_scalar, 2U> generated_input{
        unicode_scalar{0x0905U, 0U, 1U, 0U, 0U, deva},
        unicode_scalar{0x093AU, 1U, 1U, 0U, 0U, deva}};
    std::array<shaping_glyph, 6U> generated_glyphs{};
    std::array<unicode_grapheme_cluster, 2U> generated_graphemes{};
    std::array<shaping_attachment, 6U> generated_attachments{};
    std::array<std::uint8_t, 6U> generated_states{};
    auto generated_options = open_type_shape_run_options{};
    generated_options.script =
        open_type_tag::from_chars('d', 'e', 'v', '2');
    generated_options.unicode_script = deva;
    std::uint32_t generated_count = 0U;
    require(try_shape_open_type_run(
        font,
        generated_input,
        generated_options,
        generated_glyphs,
        open_type_shape_run_scratch{
            generated_graphemes,
            {},
            {},
            generated_attachments,
            generated_states},
        generated_count,
        &error));
    require(generated_count == 3U &&
        generated_glyphs[0U].code_point == 0x0905U &&
        generated_glyphs[1U].code_point == 0x25CCU &&
        generated_glyphs[2U].code_point == 0x093AU);

    std::array<shaping_glyph, 2U> constrained_short{
        shaping_glyph{3U, 0x0905U, 10},
        shaping_glyph{4U, 0x093AU, 11}};
    count = 2U;
    require(!try_preprocess_open_type_glyphs(
        font,
        open_type_tag::from_chars('d', 'e', 'v', 'a'),
        shaping_cluster_level::monotone_graphemes,
        shaping_buffer_flags::none,
        true,
        constrained_short,
        count,
        &error));
    require(error == font_error::insufficient_buffer && count == 2U &&
        constrained_short[0U].code_point == 0x0905U &&
        constrained_short[1U].code_point == 0x093AU);
}

void directional_code_point_fallback_matches_managed_stages() {
    constexpr std::array mappings{
        std::pair{0x28U, 1U},
        std::pair{0x29U, 2U},
        std::pair{0x41U, 5U},
        std::pair{0x3001U, 3U},
        std::pair{0xFE11U, 4U}};
    const auto cmap = make_cmap_groups(mappings);
    const auto data = make_font(
        0U, 22U, 0U, false, false, false, {}, cmap);
    sfnt_font_view font{};
    font_error error = font_error::none;
    require(sfnt_font_view::try_create(data, 0U, font, &error));

    std::array<shaping_glyph, 3U> glyphs{
        shaping_glyph{1U, 0x28U, 0},
        shaping_glyph{3U, 0x3001U, 1},
        shaping_glyph{5U, 0x41U, 2}};
    require(try_apply_directional_code_point_fallback(
        font,
        glyphs,
        shaping_direction::right_to_left,
        false,
        &error));
    require(glyphs[0U].code_point == 0x29U &&
        glyphs[0U].glyph_id == 2U &&
        glyphs[1U].code_point == 0x3001U &&
        glyphs[2U].code_point == 0x41U);

    glyphs[1U] = shaping_glyph{3U, 0x3001U, 1};
    require(try_apply_directional_code_point_fallback(
        font,
        std::span<shaping_glyph>{glyphs}.subspan(1U, 1U),
        shaping_direction::top_to_bottom,
        false,
        &error));
    require(glyphs[1U].code_point == 0xFE11U &&
        glyphs[1U].glyph_id == 4U);

    glyphs[1U] = shaping_glyph{3U, 0x3001U, 1};
    require(try_apply_directional_code_point_fallback(
        font,
        std::span<shaping_glyph>{glyphs}.subspan(1U, 1U),
        shaping_direction::top_to_bottom,
        true,
        &error));
    require(glyphs[1U].code_point == 0x3001U &&
        glyphs[1U].glyph_id == 3U);

    const std::array<unicode_scalar, 1U> input{
        unicode_scalar{0x28U, 0U, 1U}};
    std::array<shaping_glyph, 1U> shaped{};
    std::array<unicode_grapheme_cluster, 1U> graphemes{};
    std::array<shaping_attachment, 1U> attachments{};
    std::array<std::uint8_t, 1U> states{};
    std::uint32_t glyph_count = 0U;
    auto options = open_type_shape_run_options{};
    options.direction = shaping_direction::right_to_left;
    require(try_shape_open_type_run(
        font,
        input,
        options,
        shaped,
        open_type_shape_run_scratch{
            graphemes, {}, {}, attachments, states},
        glyph_count,
        &error));
    require(glyph_count == 1U && shaped[0U].code_point == 0x29U &&
        shaped[0U].glyph_id == 2U);

    const auto make_vertical_gsub = [] {
        std::vector<std::byte> table(70U);
        write_u16(table, 0U, 1U);
        write_u16(table, 4U, 10U);
        write_u16(table, 6U, 30U);
        write_u16(table, 8U, 44U);
        write_u16(table, 10U, 1U);
        write_u32(table, 12U,
            open_type_tag::from_chars('h', 'a', 'n', 'i').value);
        write_u16(table, 16U, 8U);
        write_u16(table, 18U, 4U);
        write_u16(table, 22U, 0U);
        write_u16(table, 24U, 0xFFFFU);
        write_u16(table, 26U, 1U);
        write_u16(table, 28U, 0U);
        write_u16(table, 30U, 1U);
        write_u32(table, 32U,
            open_type_tag::from_chars('v', 'e', 'r', 't').value);
        write_u16(table, 36U, 8U);
        write_u16(table, 38U, 0U);
        write_u16(table, 40U, 1U);
        write_u16(table, 42U, 0U);
        write_u16(table, 44U, 1U);
        write_u16(table, 46U, 4U);
        write_u16(table, 48U, 1U);
        write_u16(table, 50U, 0U);
        write_u16(table, 52U, 1U);
        write_u16(table, 54U, 8U);
        write_u16(table, 56U, 2U);
        write_u16(table, 58U, 8U);
        write_u16(table, 60U, 1U);
        write_u16(table, 62U, 6U);
        write_u16(table, 64U, 1U);
        write_u16(table, 66U, 1U);
        write_u16(table, 68U, 3U);
        return table;
    };
    const std::array vertical_tables{
        table_data{open_type_tag::from_chars('G', 'S', 'U', 'B'),
            make_vertical_gsub()}};
    const auto vertical_data = make_font(
        0U, 22U, 0U, false, false, false, vertical_tables, cmap);
    require(sfnt_font_view::try_create(vertical_data, 0U, font, &error));
    constexpr std::array vertical_input{
        unicode_scalar{0x3001U, 0U, 1U}};
    constexpr std::array vertical_features{
        open_type_tag::from_chars('v', 'e', 'r', 't')};
    options = open_type_shape_run_options{
        open_type_tag::from_chars('h', 'a', 'n', 'i'),
        {},
        shaping_direction::top_to_bottom,
        vertical_features};
    std::array<std::uint16_t, 1U> gsub_lookups{};
    require(try_shape_open_type_run(
        font,
        vertical_input,
        options,
        shaped,
        open_type_shape_run_scratch{
            graphemes, gsub_lookups, {}, attachments, states},
        glyph_count,
        &error));
    require(glyph_count == 1U && shaped[0U].code_point == 0x3001U &&
        shaped[0U].glyph_id == 6U);
}

void special_space_fallback_matches_managed_metrics() {
    constexpr std::array mappings{
        std::pair{0x20U, 1U},
        std::pair{0x2CU, 3U},
        std::pair{0x30U, 2U}};
    const auto cmap = make_cmap_groups(mappings);
    const auto data = make_font(
        0U, 22U, 0U, false, false, false, {}, cmap);
    sfnt_font_view font{};
    font_error error = font_error::none;
    require(sfnt_font_view::try_create(data, 0U, font, &error));

    constexpr std::array code_points{
        0x00A0U,
        0x2000U,
        0x2001U,
        0x2004U,
        0x2005U,
        0x2006U,
        0x2007U,
        0x2008U,
        0x2009U,
        0x200AU,
        0x202FU,
        0x205FU,
        0x3000U};
    constexpr std::array expected_advances{
        600, 500, 1000, 333, 250, 167, 600,
        600, 200, 63, 300, 222, 1000};
    std::array<unicode_scalar, code_points.size()> input{};
    for (std::size_t index = 0U; index < code_points.size(); ++index) {
        input[index] = unicode_scalar{
            code_points[index],
            static_cast<std::uint32_t>(index),
            1U};
    }
    std::array<shaping_glyph, code_points.size()> glyphs{};
    std::array<unicode_grapheme_cluster, code_points.size()> graphemes{};
    std::array<shaping_attachment, code_points.size()> attachments{};
    std::array<std::uint8_t, code_points.size()> states{};
    std::uint32_t glyph_count = 0U;
    require(try_shape_open_type_run(
        font,
        input,
        {},
        glyphs,
        open_type_shape_run_scratch{
            graphemes, {}, {}, attachments, states},
        glyph_count,
        &error));
    require(glyph_count == code_points.size());
    for (std::size_t index = 0U; index < code_points.size(); ++index) {
        require(glyphs[index].code_point == code_points[index] &&
            glyphs[index].glyph_id == 1U &&
            glyphs[index].advance_x == expected_advances[index]);
    }

    const std::array<unicode_scalar, 1U> vertical_input{
        unicode_scalar{0x2000U, 0U, 1U}};
    std::array<shaping_glyph, 1U> vertical_glyph{};
    std::array<unicode_grapheme_cluster, 1U> vertical_grapheme{};
    std::array<shaping_attachment, 1U> vertical_attachment{};
    std::array<std::uint8_t, 1U> vertical_state{};
    auto vertical_options = open_type_shape_run_options{};
    vertical_options.direction = shaping_direction::top_to_bottom;
    require(try_shape_open_type_run(
        font,
        vertical_input,
        vertical_options,
        vertical_glyph,
        open_type_shape_run_scratch{
            vertical_grapheme,
            {},
            {},
            vertical_attachment,
            vertical_state},
        glyph_count,
        &error));
    require(glyph_count == 1U && vertical_glyph[0U].glyph_id == 1U &&
        vertical_glyph[0U].advance_y == -500);

    const auto no_space_data = make_font();
    require(sfnt_font_view::try_create(no_space_data, 0U, font, &error));
    require(try_shape_open_type_run(
        font,
        vertical_input,
        {},
        vertical_glyph,
        open_type_shape_run_scratch{
            vertical_grapheme,
            {},
            {},
            vertical_attachment,
            vertical_state},
        glyph_count,
        &error));
    require(glyph_count == 1U && vertical_glyph[0U].glyph_id == 0U &&
        vertical_glyph[0U].advance_x == 500);
}

void arabic_fallback_forms_and_ligatures_match_managed() {
    constexpr auto unsafe_break_and_concat =
        static_cast<std::uint32_t>(shaping_glyph_flags::unsafe_to_break) |
        static_cast<std::uint32_t>(shaping_glyph_flags::unsafe_to_concat);
    constexpr std::array mappings{
        std::pair{0x0628U, 1U},
        std::pair{0x0645U, 3U},
        std::pair{0xFC08U, 5U},
        std::pair{0xFE90U, 6U},
        std::pair{0xFE91U, 2U},
        std::pair{0xFEE2U, 4U}};
    const auto cmap = make_cmap_groups(mappings);
    const auto data = make_font(
        0U, 22U, 0U, false, false, false, {}, cmap);
    sfnt_font_view font{};
    font_error error = font_error::none;
    require(sfnt_font_view::try_create(data, 0U, font, &error));

    constexpr std::array input{
        unicode_scalar{0x0628U, 0U, 1U},
        unicode_scalar{0x0645U, 1U, 1U}};
    constexpr std::array form_features{
        open_type_tag::from_chars('i', 'n', 'i', 't'),
        open_type_tag::from_chars('f', 'i', 'n', 'a')};
    constexpr std::array ligature_features{
        open_type_tag::from_chars('i', 'n', 'i', 't'),
        open_type_tag::from_chars('f', 'i', 'n', 'a'),
        open_type_tag::from_chars('r', 'l', 'i', 'g')};
    std::array<shaping_glyph, 6U> glyphs{};
    std::array<unicode_grapheme_cluster, 2U> graphemes{};
    std::array<shaping_attachment, 6U> attachments{};
    std::array<std::uint8_t, 6U> states{};
    std::array<open_type_arabic_action, 2U> actions{};
    std::array<shaping_glyph_flags, 2U> joining_flags{};
    auto scratch = open_type_shape_run_scratch{};
    scratch.grapheme_clusters = graphemes;
    scratch.attachments = attachments;
    scratch.attachment_states = states;
    scratch.arabic_actions = actions;
    scratch.arabic_flags = joining_flags;
    auto options = open_type_shape_run_options{};
    options.script = open_type_tag::from_chars('a', 'r', 'a', 'b');
    options.direction = shaping_direction::right_to_left;
    options.requested_features = form_features;
    options.zero_mark_advances = false;
    std::uint32_t glyph_count = 0U;
    require(try_shape_open_type_run(
        font, input, options, glyphs, scratch, glyph_count, &error));
    require(glyph_count == 2U &&
        glyphs[0U].code_point == 0x0645U && glyphs[0U].glyph_id == 4U &&
        glyphs[1U].code_point == 0x0628U && glyphs[1U].glyph_id == 2U);

    constexpr std::array one_beh{unicode_scalar{0x0628U, 0U, 1U}};
    glyphs.fill({});
    options.pre_context = one_beh;
    require(try_shape_open_type_run(
        font, one_beh, options, glyphs, scratch, glyph_count, &error));
    require(glyph_count == 1U && glyphs[0U].glyph_id == 6U);
    glyphs.fill({});
    options.pre_context = {};
    options.post_context = one_beh;
    require(try_shape_open_type_run(
        font, one_beh, options, glyphs, scratch, glyph_count, &error));
    require(glyph_count == 1U && glyphs[0U].glyph_id == 2U);
    options.post_context = {};

    glyphs.fill({});
    options.requested_features = ligature_features;
    require(try_shape_open_type_run(
        font, input, options, glyphs, scratch, glyph_count, &error));
    require(glyph_count == 1U && glyphs[0U].code_point == 0x0628U &&
        glyphs[0U].glyph_id == 5U && glyphs[0U].cluster == 0);

    glyphs.fill({});
    options.requested_features = {};
    require(try_shape_open_type_run(
        font, input, options, glyphs, scratch, glyph_count, &error));
    require(glyph_count == 2U &&
        glyphs[0U].code_point == 0x0645U && glyphs[0U].glyph_id == 3U &&
        glyphs[1U].code_point == 0x0628U && glyphs[1U].glyph_id == 1U &&
        static_cast<std::uint32_t>(glyphs[0U].flags) ==
            unsafe_break_and_concat &&
        glyphs[1U].flags == shaping_glyph_flags::none);

    options.buffer_flags = static_cast<shaping_buffer_flags>(
        static_cast<std::uint8_t>(
            shaping_buffer_flags::produce_unsafe_to_concat) |
        static_cast<std::uint8_t>(
            shaping_buffer_flags::produce_safe_to_insert_tatweel));
    glyphs.fill({});
    require(try_shape_open_type_run(
        font, input, options, glyphs, scratch, glyph_count, &error));
    require(glyph_count == 2U &&
        glyphs[0U].flags ==
            shaping_glyph_flags::safe_to_insert_tatweel &&
        glyphs[1U].flags == shaping_glyph_flags::unsafe_to_concat);
    options.buffer_flags = shaping_buffer_flags::none;

    const auto make_initial_gsub = [] {
        std::vector<std::byte> table(70U);
        write_u16(table, 0U, 1U);
        write_u16(table, 4U, 10U);
        write_u16(table, 6U, 30U);
        write_u16(table, 8U, 44U);
        write_u16(table, 10U, 1U);
        write_u32(table, 12U,
            open_type_tag::from_chars('a', 'r', 'a', 'b').value);
        write_u16(table, 16U, 8U);
        write_u16(table, 18U, 4U);
        write_u16(table, 22U, 0U);
        write_u16(table, 24U, 0xFFFFU);
        write_u16(table, 26U, 1U);
        write_u16(table, 28U, 0U);
        write_u16(table, 30U, 1U);
        write_u32(table, 32U,
            open_type_tag::from_chars('i', 'n', 'i', 't').value);
        write_u16(table, 36U, 8U);
        write_u16(table, 38U, 0U);
        write_u16(table, 40U, 1U);
        write_u16(table, 42U, 0U);
        write_u16(table, 44U, 1U);
        write_u16(table, 46U, 4U);
        write_u16(table, 48U, 1U);
        write_u16(table, 50U, 0U);
        write_u16(table, 52U, 1U);
        write_u16(table, 54U, 8U);
        write_u16(table, 56U, 2U);
        write_u16(table, 58U, 8U);
        write_u16(table, 60U, 1U);
        write_u16(table, 62U, 7U);
        write_u16(table, 64U, 1U);
        write_u16(table, 66U, 1U);
        write_u16(table, 68U, 1U);
        return table;
    };
    const std::array initial_tables{
        table_data{open_type_tag::from_chars('G', 'S', 'U', 'B'),
            make_initial_gsub()}};
    const auto initial_data = make_font(
        0U, 22U, 0U, false, false, false, initial_tables, cmap);
    require(sfnt_font_view::try_create(initial_data, 0U, font, &error));
    constexpr std::array two_beh{
        unicode_scalar{0x0628U, 0U, 1U},
        unicode_scalar{0x0628U, 1U, 1U}};
    std::array<std::uint16_t, 1U> gsub_lookups{};
    scratch.gsub_lookups = gsub_lookups;
    glyphs.fill({});
    options.requested_features = std::span{form_features}.first(1U);
    require(try_shape_open_type_run(
        font, two_beh, options, glyphs, scratch, glyph_count, &error));
    require(glyph_count == 2U &&
        glyphs[0U].code_point == 0x0628U && glyphs[0U].glyph_id == 1U &&
        glyphs[1U].code_point == 0x0628U && glyphs[1U].glyph_id == 7U);

    // The authoritative managed Arabic pipeline applies joining forms before
    // required ligatures. Lookup 0 changes the initial BEH from glyph 1 to 7;
    // lookup 1 can form glyph 9 only from the resulting [7, 1] sequence.
    const auto make_form_then_ligature_gsub = [] {
        std::vector<std::byte> table(120U);
        write_u16(table, 0U, 1U);
        write_u16(table, 4U, 10U);
        write_u16(table, 6U, 34U);
        write_u16(table, 8U, 60U);
        write_u16(table, 10U, 1U);
        write_u32(table, 12U,
            open_type_tag::from_chars('a', 'r', 'a', 'b').value);
        write_u16(table, 16U, 8U);
        write_u16(table, 18U, 4U);
        write_u16(table, 22U, 0U);
        write_u16(table, 24U, 1U);
        write_u16(table, 26U, 2U);
        write_u16(table, 28U, 0U);
        write_u16(table, 30U, 1U);

        write_u16(table, 34U, 2U);
        write_u32(table, 36U,
            open_type_tag::from_chars('i', 'n', 'i', 't').value);
        write_u16(table, 40U, 14U);
        write_u32(table, 42U,
            open_type_tag::from_chars('r', 'l', 'i', 'g').value);
        write_u16(table, 46U, 20U);
        write_u16(table, 48U, 0U);
        write_u16(table, 50U, 1U);
        write_u16(table, 52U, 0U);
        write_u16(table, 54U, 0U);
        write_u16(table, 56U, 1U);
        write_u16(table, 58U, 1U);

        write_u16(table, 60U, 2U);
        write_u16(table, 62U, 6U);
        write_u16(table, 64U, 28U);
        write_u16(table, 66U, 1U);
        write_u16(table, 68U, 0U);
        write_u16(table, 70U, 1U);
        write_u16(table, 72U, 8U);
        write_u16(table, 74U, 2U);
        write_u16(table, 76U, 8U);
        write_u16(table, 78U, 1U);
        write_u16(table, 80U, 7U);
        write_u16(table, 82U, 1U);
        write_u16(table, 84U, 1U);
        write_u16(table, 86U, 1U);

        write_u16(table, 88U, 4U);
        write_u16(table, 90U, 0U);
        write_u16(table, 92U, 1U);
        write_u16(table, 94U, 8U);
        write_u16(table, 96U, 1U);
        write_u16(table, 98U, 18U);
        write_u16(table, 100U, 1U);
        write_u16(table, 102U, 8U);
        write_u16(table, 104U, 1U);
        write_u16(table, 106U, 4U);
        write_u16(table, 108U, 9U);
        write_u16(table, 110U, 2U);
        write_u16(table, 112U, 1U);
        write_u16(table, 114U, 1U);
        write_u16(table, 116U, 1U);
        write_u16(table, 118U, 7U);
        return table;
    };
    const std::array staged_tables{
        table_data{open_type_tag::from_chars('G', 'S', 'U', 'B'),
            make_form_then_ligature_gsub()}};
    const auto staged_data = make_font(
        0U, 22U, 0U, false, false, false, staged_tables, cmap);
    require(sfnt_font_view::try_create(staged_data, 0U, font, &error));
    progpu::native::text::sfnt_table_view staged_gsub_table{};
    open_type_layout_table_view staged_gsub{};
    require(font.try_get_table(
        open_type_tag::from_chars('G', 'S', 'U', 'B'), staged_gsub_table));
    require(open_type_layout_table_view::try_create(
        staged_gsub_table.bytes, staged_gsub, &error));
    std::array<shaping_glyph, 2U> direct_ligature{
        shaping_glyph{7U}, shaping_glyph{1U}};
    std::uint32_t direct_count = 2U;
    bool direct_applied = false;
    require(try_apply_open_type_gsub_lookup(
        staged_gsub,
        1U,
        direct_ligature,
        direct_count,
        {},
        direct_applied,
        &error));
    require(direct_applied && direct_count == 1U &&
        direct_ligature[0U].glyph_id == 9U);
    constexpr std::array staged_features{
        open_type_tag::from_chars('i', 'n', 'i', 't'),
        open_type_tag::from_chars('r', 'l', 'i', 'g')};
    std::array<std::uint16_t, 2U> staged_lookups{};
    scratch.gsub_lookups = staged_lookups;
    options.requested_features = staged_features;
    glyphs.fill({});
    require(try_shape_open_type_run(
        font, two_beh, options, glyphs, scratch, glyph_count, &error));
    require(glyph_count == 1U && glyphs[0U].glyph_id == 9U &&
        glyphs[0U].cluster == 0);
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
    std::array<std::uint8_t, 6U> categories{};
    std::array<std::uint8_t, 6U> syllables{};
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
    std::array<std::uint8_t, 6U> categories{};
    std::array<std::uint8_t, 6U> syllables{};
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
    std::array<std::uint8_t, 6U> categories{};
    std::array<std::uint8_t, 6U> syllables{};
    std::array<std::uint32_t, 7U> indices{};
    auto options = open_type_shape_run_options{
        open_type_tag::from_chars('s', 'i', 'n', 'h')};
    options.complex_script = open_type_complex_script::use;
    const normalization_fixture normalization{};
    options.normalization_data = &normalization.data;
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

    // USE topographical substitutions run after reordering. This `isol`
    // ligature intentionally matches only the reordered [prebase-vowel,
    // consonant] sequence; applying topographical forms in the basic stage
    // would leave two glyphs and never revisit the lookup.
    std::vector<std::byte> topographical_gsub(84U);
    write_u16(topographical_gsub, 0U, 1U);
    write_u16(topographical_gsub, 4U, 10U);
    write_u16(topographical_gsub, 6U, 32U);
    write_u16(topographical_gsub, 8U, 48U);
    write_u16(topographical_gsub, 10U, 1U);
    write_u32(topographical_gsub, 12U,
        open_type_tag::from_chars('s', 'i', 'n', 'h').value);
    write_u16(topographical_gsub, 16U, 8U);
    write_u16(topographical_gsub, 18U, 4U);
    write_u16(topographical_gsub, 20U, 0U);
    write_u16(topographical_gsub, 22U, 0U);
    write_u16(topographical_gsub, 24U, 0xFFFFU);
    write_u16(topographical_gsub, 26U, 1U);
    write_u16(topographical_gsub, 28U, 0U);
    write_u16(topographical_gsub, 32U, 1U);
    write_u32(topographical_gsub, 34U,
        open_type_tag::from_chars('i', 's', 'o', 'l').value);
    write_u16(topographical_gsub, 38U, 8U);
    write_u16(topographical_gsub, 40U, 0U);
    write_u16(topographical_gsub, 42U, 1U);
    write_u16(topographical_gsub, 44U, 0U);
    write_u16(topographical_gsub, 48U, 1U);
    write_u16(topographical_gsub, 50U, 4U);
    write_u16(topographical_gsub, 52U, 4U);
    write_u16(topographical_gsub, 54U, 0U);
    write_u16(topographical_gsub, 56U, 1U);
    write_u16(topographical_gsub, 58U, 8U);
    write_u16(topographical_gsub, 60U, 1U);
    write_u16(topographical_gsub, 62U, 18U);
    write_u16(topographical_gsub, 64U, 1U);
    write_u16(topographical_gsub, 66U, 8U);
    write_u16(topographical_gsub, 68U, 1U);
    write_u16(topographical_gsub, 70U, 4U);
    write_u16(topographical_gsub, 72U, 9U);
    write_u16(topographical_gsub, 74U, 2U);
    write_u16(topographical_gsub, 76U, 2U);
    write_u16(topographical_gsub, 78U, 1U);
    write_u16(topographical_gsub, 80U, 1U);
    write_u16(topographical_gsub, 82U, 3U);
    const std::array topographical_tables{
        table_data{open_type_tag::from_chars('G', 'S', 'U', 'B'),
            std::move(topographical_gsub)}};
    const auto topographical_data = make_font(
        0U, 22U, 0U, false, false, false, topographical_tables, cmap);
    require(sfnt_font_view::try_create(
        topographical_data, 0U, font, &error));
    constexpr std::array topographical_features{
        open_type_tag::from_chars('i', 's', 'o', 'l')};
    options.requested_features = topographical_features;
    std::array<std::uint16_t, 1U> lookup_scratch{};
    glyphs.fill({});
    graphemes.fill({});
    attachments.fill({});
    states.fill({});
    categories.fill({});
    syllables.fill({});
    indices.fill({});
    glyph_count = 0U;
    require(try_shape_open_type_run(
        font,
        input,
        options,
        glyphs,
        open_type_shape_run_scratch{
            .grapheme_clusters = graphemes,
            .gsub_lookups = lookup_scratch,
            .attachments = attachments,
            .attachment_states = states,
            .script_categories = categories,
            .script_syllables = syllables,
            .script_indices = indices},
        glyph_count,
        &error));
    require(glyph_count == 1U && glyphs[0U].glyph_id == 9U);
}

void open_type_use_diacritic_normalization_matches_managed() {
    constexpr std::array mappings{
        std::pair{0x0C95U, 2U},
        std::pair{0x0CC2U, 4U},
        std::pair{0x0CC6U, 3U},
        std::pair{0x0CCBU, 6U},
        std::pair{0x0CD5U, 5U},
        std::pair{0x25CCU, 1U}};
    const auto cmap = make_cmap_groups(mappings);
    const auto data = make_font(
        0U, 22U, 0U, false, false, false, {}, cmap);
    sfnt_font_view font{};
    font_error error = font_error::none;
    require(sfnt_font_view::try_create(data, 0U, font, &error));
    const normalization_fixture normalization{};
    constexpr std::array input{
        unicode_scalar{0x0C95U, 0U, 1U},
        unicode_scalar{0x0CCBU, 1U, 1U}};
    auto options = open_type_shape_run_options{
        open_type_tag::from_chars('k', 'n', 'd', '2')};
    options.complex_script = open_type_complex_script::use;
    options.normalization_data = &normalization.data;

    open_type_shape_run_requirements requirements{};
    require(try_get_open_type_shape_run_requirements(
        font, input, options, requirements, &error));
    require(requirements.glyph_capacity == 6U &&
        requirements.complex_script_capacity == 6U &&
        requirements.complex_script_index_capacity == 7U);

    std::array<shaping_glyph, 6U> glyphs{};
    std::array<unicode_grapheme_cluster, 2U> graphemes{};
    std::array<shaping_attachment, 6U> attachments{};
    std::array<std::uint8_t, 6U> states{};
    std::array<std::uint8_t, 6U> categories{};
    std::array<std::uint8_t, 6U> syllables{};
    std::array<std::uint32_t, 7U> indices{};
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
    require(glyph_count == 4U);
    constexpr std::array expected{0x0C95U, 0x0CC6U, 0x0CC2U, 0x0CD5U};
    for (std::size_t index = 0U; index < expected.size(); ++index) {
        require(glyphs[index].code_point == expected[index] &&
            glyphs[index].cluster == 0);
    }
    require(glyphs[0U].glyph_id == 2U && glyphs[1U].glyph_id == 3U &&
        glyphs[2U].glyph_id == 4U && glyphs[3U].glyph_id == 5U);

    std::array<shaping_glyph, 5U> short_glyphs{};
    short_glyphs.fill(shaping_glyph{88U});
    glyph_count = 99U;
    require(!try_shape_open_type_run(
        font,
        input,
        options,
        short_glyphs,
        open_type_shape_run_scratch{},
        glyph_count,
        &error));
    require(error == font_error::insufficient_buffer && glyph_count == 0U &&
        short_glyphs[0U].glyph_id == 88U);

    auto missing_plan_options = options;
    missing_plan_options.normalization_data = nullptr;
    glyphs.fill(shaping_glyph{77U});
    glyph_count = 99U;
    require(!try_shape_open_type_run(
        font,
        input,
        missing_plan_options,
        glyphs,
        open_type_shape_run_scratch{},
        glyph_count,
        &error));
    require(error == font_error::invalid_argument && glyph_count == 0U &&
        glyphs[0U].glyph_id == 77U);
}

void open_type_initial_mapping_matches_managed() {
    constexpr std::array mappings{
        std::pair{0x0041U, 2U},
        std::pair{0x0301U, 3U},
        std::pair{0x030AU, 4U},
        std::pair{0x0C95U, 2U},
        std::pair{0x0CC2U, 4U},
        std::pair{0x0CC6U, 3U},
        std::pair{0x0CCBU, 6U},
        std::pair{0x0CD5U, 5U},
        std::pair{0x1780U, 2U},
        std::pair{0x17BEU, 4U},
        std::pair{0x17C1U, 3U},
        std::pair{0x2010U, 5U},
        std::pair{0x25CCU, 1U}};
    const auto cmap = make_cmap_groups(mappings);
    const auto data = make_font(
        0U, 22U, 0U, false, false, false, {}, cmap);
    sfnt_font_view font{};
    font_error error = font_error::none;
    require(sfnt_font_view::try_create(data, 0U, font, &error));
    const normalization_fixture normalization{};

    constexpr std::array decomposed_input{
        unicode_scalar{0x01FAU, 7U, 1U}};
    auto latin_options = open_type_shape_run_options{
        open_type_tag::from_chars('l', 'a', 't', 'n')};
    latin_options.normalization_data = &normalization.data;
    open_type_shape_run_requirements requirements{};
    require(try_get_open_type_shape_run_requirements(
        font, decomposed_input, latin_options, requirements, &error));
    require(requirements.initial_glyph_count == 3U &&
        requirements.glyph_capacity == 4U);
    std::array<shaping_glyph, 3U> decomposed_glyphs{};
    std::array<unicode_grapheme_cluster, 1U> graphemes{};
    std::array<shaping_attachment, 3U> attachments{};
    std::array<std::uint8_t, 3U> states{};
    std::uint32_t glyph_count = 0U;
    require(try_shape_open_type_run(
        font,
        decomposed_input,
        latin_options,
        decomposed_glyphs,
        open_type_shape_run_scratch{
            .grapheme_clusters = graphemes,
            .attachments = attachments,
            .attachment_states = states},
        glyph_count,
        &error));
    constexpr std::array decomposed_expected{0x0041U, 0x030AU, 0x0301U};
    require(glyph_count == decomposed_expected.size());
    for (std::size_t index = 0U; index < decomposed_expected.size(); ++index) {
        require(decomposed_glyphs[index].code_point ==
                decomposed_expected[index] &&
            decomposed_glyphs[index].cluster == 7);
    }

    std::array<shaping_glyph, 2U> short_glyphs{};
    short_glyphs.fill(shaping_glyph{91U});
    glyph_count = 99U;
    require(!try_shape_open_type_run(
        font,
        decomposed_input,
        latin_options,
        short_glyphs,
        open_type_shape_run_scratch{},
        glyph_count,
        &error));
    require(error == font_error::insufficient_buffer && glyph_count == 0U &&
        short_glyphs[0U].glyph_id == 91U);

    constexpr std::array hyphen_input{
        unicode_scalar{0x2011U, 4U, 1U}};
    std::array<shaping_glyph, 1U> hyphen_glyphs{};
    std::array<unicode_grapheme_cluster, 1U> hyphen_graphemes{};
    std::array<shaping_attachment, 1U> hyphen_attachments{};
    std::array<std::uint8_t, 1U> hyphen_states{};
    glyph_count = 0U;
    require(try_shape_open_type_run(
        font,
        hyphen_input,
        latin_options,
        hyphen_glyphs,
        open_type_shape_run_scratch{
            .grapheme_clusters = hyphen_graphemes,
            .attachments = hyphen_attachments,
            .attachment_states = hyphen_states},
        glyph_count,
        &error));
    require(glyph_count == 1U && hyphen_glyphs[0U].glyph_id == 5U &&
        hyphen_glyphs[0U].code_point == 0x2010U &&
        hyphen_glyphs[0U].cluster == 4);

    constexpr std::array indic_input{
        unicode_scalar{0x0C95U, 0U, 1U},
        unicode_scalar{0x0CCBU, 1U, 1U}};
    auto indic_options = open_type_shape_run_options{
        open_type_tag::from_chars('k', 'n', 'd', '2')};
    indic_options.complex_script = open_type_complex_script::indic;
    indic_options.normalization_data = &normalization.data;
    require(try_get_open_type_shape_run_requirements(
        font, indic_input, indic_options, requirements, &error));
    require(requirements.initial_glyph_count == 4U &&
        requirements.glyph_capacity == 6U &&
        requirements.complex_script_capacity == 6U);
    std::array<shaping_glyph, 6U> indic_glyphs{};
    std::array<unicode_grapheme_cluster, 2U> indic_graphemes{};
    std::array<shaping_attachment, 6U> indic_attachments{};
    std::array<std::uint8_t, 6U> indic_states{};
    std::array<std::uint8_t, 6U> indic_categories{};
    std::array<std::uint8_t, 6U> indic_syllables{};
    std::array<std::uint32_t, 7U> indic_indices{};
    glyph_count = 0U;
    require(try_shape_open_type_run(
        font,
        indic_input,
        indic_options,
        indic_glyphs,
        open_type_shape_run_scratch{
            .grapheme_clusters = indic_graphemes,
            .attachments = indic_attachments,
            .attachment_states = indic_states,
            .script_categories = indic_categories,
            .script_syllables = indic_syllables,
            .script_indices = indic_indices},
        glyph_count,
        &error));
    require(glyph_count == 4U);
    constexpr std::array indic_expected{0x0C95U, 0x0CC6U, 0x0CC2U, 0x0CD5U};
    for (const auto expected : indic_expected) {
        require(std::find_if(
            indic_glyphs.begin(),
            indic_glyphs.begin() + glyph_count,
            [expected](const shaping_glyph& glyph) {
                return glyph.code_point == expected;
            }) != indic_glyphs.begin() + glyph_count);
    }

    constexpr std::array khmer_input{
        unicode_scalar{0x1780U, 0U, 1U},
        unicode_scalar{0x17BEU, 1U, 1U}};
    auto khmer_options = open_type_shape_run_options{
        open_type_tag::from_chars('k', 'h', 'm', 'r')};
    khmer_options.complex_script = open_type_complex_script::khmer;
    require(try_get_open_type_shape_run_requirements(
        font, khmer_input, khmer_options, requirements, &error));
    require(requirements.initial_glyph_count == 3U &&
        requirements.complex_script_capacity == 6U);
    std::array<shaping_glyph, 6U> khmer_glyphs{};
    std::array<unicode_grapheme_cluster, 2U> khmer_graphemes{};
    std::array<shaping_attachment, 6U> khmer_attachments{};
    std::array<std::uint8_t, 6U> khmer_states{};
    std::array<std::uint8_t, 6U> khmer_categories{};
    std::array<std::uint8_t, 6U> khmer_syllables{};
    glyph_count = 0U;
    require(try_shape_open_type_run(
        font,
        khmer_input,
        khmer_options,
        khmer_glyphs,
        open_type_shape_run_scratch{
            .grapheme_clusters = khmer_graphemes,
            .attachments = khmer_attachments,
            .attachment_states = khmer_states,
            .script_categories = khmer_categories,
            .script_syllables = khmer_syllables},
        glyph_count,
        &error));
    require(glyph_count == 3U);
    require(std::count_if(
        khmer_glyphs.begin(),
        khmer_glyphs.begin() + glyph_count,
        [](const shaping_glyph& glyph) {
            return glyph.code_point == 0x17C1U;
        }) == 1);
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
    std::array<std::uint8_t, 6U> categories{};
    std::array<std::uint8_t, 6U> syllables{};
    std::array<std::uint32_t, 7U> indices{};
    auto options = open_type_shape_run_options{
        open_type_tag::from_chars('d', 'e', 'v', '2')};
    options.complex_script = open_type_complex_script::indic;
    const normalization_fixture normalization{};
    options.normalization_data = &normalization.data;
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
    require(glyph_count == 2U && glyphs[0U].code_point == 0x093FU &&
        glyphs[1U].code_point == 0x0915U && glyphs[0U].cluster == 0 &&
        glyphs[1U].cluster == 0);

    // Direct port regression for OpenTypeTextShaper.MergeIndicSortClusters:
    // modern Indic runs merge only reordered permutation cycles, not the
    // complete consonant syllable. The second Tamil KA therefore retains its
    // source cluster instead of being collapsed into the first KA cluster.
    constexpr std::array tamil_mappings{
        std::pair{0x0B95U, 2U},
        std::pair{0x0BA3U, 3U},
        std::pair{0x0BAEU, 4U},
        std::pair{0x0BB5U, 5U},
        std::pair{0x0BCDU, 6U},
        std::pair{0x25CCU, 1U}};
    const auto tamil_cmap = make_cmap_groups(tamil_mappings);
    const auto tamil_data = make_font(
        0U, 22U, 0U, false, false, false, {}, tamil_cmap);
    require(sfnt_font_view::try_create(tamil_data, 0U, font, &error));
    constexpr std::array tamil_input{
        unicode_scalar{0x0BB5U, 0U, 1U},
        unicode_scalar{0x0BA3U, 1U, 1U},
        unicode_scalar{0x0B95U, 2U, 1U},
        unicode_scalar{0x0BCDU, 3U, 1U},
        unicode_scalar{0x0B95U, 4U, 1U},
        unicode_scalar{0x0BAEU, 5U, 1U},
        unicode_scalar{0x0BCDU, 6U, 1U}};
    std::array<shaping_glyph, 21U> tamil_glyphs{};
    std::array<unicode_grapheme_cluster, 7U> tamil_graphemes{};
    std::array<shaping_attachment, 21U> tamil_attachments{};
    std::array<std::uint8_t, 21U> tamil_states{};
    std::array<std::uint8_t, 21U> tamil_categories{};
    std::array<std::uint8_t, 21U> tamil_syllables{};
    std::array<std::uint32_t, 22U> tamil_indices{};
    options.script = open_type_tag::from_chars('t', 'm', 'l', '2');
    glyph_count = 0U;
    require(try_shape_open_type_run(
        font,
        tamil_input,
        options,
        tamil_glyphs,
        open_type_shape_run_scratch{
            .grapheme_clusters = tamil_graphemes,
            .attachments = tamil_attachments,
            .attachment_states = tamil_states,
            .script_categories = tamil_categories,
            .script_syllables = tamil_syllables,
            .script_indices = tamil_indices},
        glyph_count,
        &error));
    const auto second_ka = std::find_if(
        tamil_glyphs.begin(),
        tamil_glyphs.begin() + glyph_count,
        [](const shaping_glyph& glyph) {
            return glyph.code_point == 0x0B95U && glyph.cluster == 4;
        });
    require(second_ka != tamil_glyphs.begin() + glyph_count);

    // The managed shaper's StringInfo boundary contract predates UAX #29
    // GB9c: consonant+virama and the following consonant begin separate
    // source clusters. Indic syllable safety flags must preserve that split
    // even though the public Unicode 17 grapheme API joins the conjunct.
    constexpr std::array devanagari_mappings{
        std::pair{0x0915U, 2U},
        std::pair{0x0937U, 3U},
        std::pair{0x093EU, 4U},
        std::pair{0x094DU, 5U},
        std::pair{0x25CCU, 1U}};
    const auto devanagari_cmap = make_cmap_groups(devanagari_mappings);
    const auto devanagari_data = make_font(
        0U, 22U, 0U, false, false, false, {}, devanagari_cmap);
    require(sfnt_font_view::try_create(devanagari_data, 0U, font, &error));
    constexpr std::array devanagari_input{
        unicode_scalar{0x0915U, 0U, 1U},
        unicode_scalar{0x094DU, 1U, 1U},
        unicode_scalar{0x0937U, 2U, 1U},
        unicode_scalar{0x093EU, 3U, 1U}};
    std::array<shaping_glyph, 12U> devanagari_glyphs{};
    std::array<unicode_grapheme_cluster, 4U> devanagari_graphemes{};
    std::array<shaping_attachment, 12U> devanagari_attachments{};
    std::array<std::uint8_t, 12U> devanagari_states{};
    std::array<std::uint8_t, 12U> devanagari_categories{};
    std::array<std::uint8_t, 12U> devanagari_syllables{};
    std::array<std::uint32_t, 13U> devanagari_indices{};
    options.script = open_type_tag::from_chars('d', 'e', 'v', '2');
    glyph_count = 0U;
    require(try_shape_open_type_run(
        font,
        devanagari_input,
        options,
        devanagari_glyphs,
        open_type_shape_run_scratch{
            .grapheme_clusters = devanagari_graphemes,
            .attachments = devanagari_attachments,
            .attachment_states = devanagari_states,
            .script_categories = devanagari_categories,
            .script_syllables = devanagari_syllables,
            .script_indices = devanagari_indices},
        glyph_count,
        &error));
    const auto unsafe = static_cast<std::uint32_t>(
        shaping_glyph_flags::unsafe_to_break) |
        static_cast<std::uint32_t>(
            shaping_glyph_flags::unsafe_to_concat);
    const auto final_matra = std::find_if(
        devanagari_glyphs.begin(),
        devanagari_glyphs.begin() + glyph_count,
        [](const shaping_glyph& glyph) {
            return glyph.code_point == 0x093EU;
        });
    require(final_matra != devanagari_glyphs.begin() + glyph_count &&
        (static_cast<std::uint32_t>(final_matra->flags) & unsafe) == unsafe);

    // A pre-base matra at a word-internal syllable boundary depends on the
    // preceding letter. The managed final reorder suppresses `init` there and
    // marks the moved matra unsafe instead of losing that boundary metadata.
    constexpr std::array malayalam_mappings{
        std::pair{0x0D2EU, 2U},
        std::pair{0x0D28U, 3U},
        std::pair{0x0D47U, 4U},
        std::pair{0x25CCU, 1U}};
    const auto malayalam_cmap = make_cmap_groups(malayalam_mappings);
    const auto malayalam_data = make_font(
        0U, 22U, 0U, false, false, false, {}, malayalam_cmap);
    require(sfnt_font_view::try_create(malayalam_data, 0U, font, &error));
    constexpr std::array malayalam_input{
        unicode_scalar{0x0D2EU, 0U, 1U},
        unicode_scalar{0x0D28U, 1U, 1U},
        unicode_scalar{0x0D47U, 2U, 1U}};
    std::array<shaping_glyph, 9U> malayalam_glyphs{};
    std::array<unicode_grapheme_cluster, 3U> malayalam_graphemes{};
    std::array<shaping_attachment, 9U> malayalam_attachments{};
    std::array<std::uint8_t, 9U> malayalam_states{};
    std::array<std::uint8_t, 9U> malayalam_categories{};
    std::array<std::uint8_t, 9U> malayalam_syllables{};
    std::array<std::uint32_t, 10U> malayalam_indices{};
    options.script = open_type_tag::from_chars('m', 'l', 'm', '2');
    glyph_count = 0U;
    require(try_shape_open_type_run(
        font,
        malayalam_input,
        options,
        malayalam_glyphs,
        open_type_shape_run_scratch{
            .grapheme_clusters = malayalam_graphemes,
            .attachments = malayalam_attachments,
            .attachment_states = malayalam_states,
            .script_categories = malayalam_categories,
            .script_syllables = malayalam_syllables,
            .script_indices = malayalam_indices},
        glyph_count,
        &error));
    const auto malayalam_matra = std::find_if(
        malayalam_glyphs.begin(),
        malayalam_glyphs.begin() + glyph_count,
        [](const shaping_glyph& glyph) {
            return glyph.code_point == 0x0D47U;
        });
    require(malayalam_matra !=
            malayalam_glyphs.begin() + glyph_count &&
        (static_cast<std::uint32_t>(malayalam_matra->flags) & unsafe) ==
            unsafe);

    // Khmer starts a new shaping cluster after COENG when another base
    // follows, even though the public UAX #29 grapheme remains joined. The
    // pre-base vowel is then moved and cluster-merged while retaining the
    // unsafe dependency established from that script-specific boundary.
    constexpr std::array khmer_cluster_mappings{
        std::pair{0x1781U, 2U},
        std::pair{0x17D2U, 3U},
        std::pair{0x1798U, 4U},
        std::pair{0x17C2U, 5U},
        std::pair{0x25CCU, 1U}};
    const auto khmer_cluster_cmap =
        make_cmap_groups(khmer_cluster_mappings);
    const auto khmer_cluster_data = make_font(
        0U, 22U, 0U, false, false, false, {}, khmer_cluster_cmap);
    require(sfnt_font_view::try_create(
        khmer_cluster_data, 0U, font, &error));
    constexpr std::array khmer_cluster_input{
        unicode_scalar{0x1781U, 0U, 1U},
        unicode_scalar{0x17D2U, 1U, 1U},
        unicode_scalar{0x1798U, 2U, 1U},
        unicode_scalar{0x17C2U, 3U, 1U}};
    std::array<shaping_glyph, 12U> khmer_cluster_glyphs{};
    std::array<unicode_grapheme_cluster, 4U> khmer_cluster_graphemes{};
    std::array<shaping_attachment, 12U> khmer_cluster_attachments{};
    std::array<std::uint8_t, 12U> khmer_cluster_states{};
    std::array<std::uint8_t, 12U> khmer_cluster_categories{};
    std::array<std::uint8_t, 12U> khmer_cluster_syllables{};
    auto khmer_cluster_options = open_type_shape_run_options{
        open_type_tag::from_chars('k', 'h', 'm', 'r')};
    khmer_cluster_options.complex_script =
        open_type_complex_script::khmer;
    glyph_count = 0U;
    require(try_shape_open_type_run(
        font,
        khmer_cluster_input,
        khmer_cluster_options,
        khmer_cluster_glyphs,
        open_type_shape_run_scratch{
            .grapheme_clusters = khmer_cluster_graphemes,
            .attachments = khmer_cluster_attachments,
            .attachment_states = khmer_cluster_states,
            .script_categories = khmer_cluster_categories,
            .script_syllables = khmer_cluster_syllables},
        glyph_count,
        &error));
    const auto khmer_prebase = std::find_if(
        khmer_cluster_glyphs.begin(),
        khmer_cluster_glyphs.begin() + glyph_count,
        [](const shaping_glyph& glyph) {
            return glyph.code_point == 0x17C2U;
        });
    require(khmer_prebase != khmer_cluster_glyphs.begin() + glyph_count &&
        (static_cast<std::uint32_t>(khmer_prebase->flags) & unsafe) ==
            unsafe);
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

void native_font_fallback_family_preferences_match_managed_policy() {
    font_error error = font_error::invalid_argument;
    std::uint32_t count = 0U;
    require(try_get_font_fallback_family_preference_count(
        {}, 0x0870U, count, &error));
    require(error == font_error::none && count == 9U);

    std::array<std::string_view, 9U> arabic_families{};
    std::uint32_t written = 0U;
    require(try_get_font_fallback_family_preferences(
        {}, 0x1EE00U, arabic_families, written, &error));
    require(written == arabic_families.size() &&
        arabic_families.front() == "Geeza Pro" &&
        arabic_families.back() == "DejaVu Sans");

    constexpr std::array languages{
        std::string_view{" zh_Hant-TW "},
        std::string_view{"ja"},
        std::string_view{"ZH-hant"}};
    require(try_get_font_fallback_family_preference_count(
        languages, 0x4E00U, count, &error));
    require(count == 21U);
    std::array<std::string_view, 21U> cjk_families{};
    require(try_get_font_fallback_family_preferences(
        languages, 0x4E00U, cjk_families, written, &error));
    require(written == cjk_families.size() &&
        cjk_families[0U] == "PingFang TC" &&
        cjk_families[5U] == "Songti TC" &&
        cjk_families[6U] == "Hiragino Sans" &&
        cjk_families[12U] == ".Aqua Kana" &&
        cjk_families[13U] == "PingFang SC" &&
        cjk_families[20U] == "Songti SC");
    require(std::count(
        cjk_families.begin(), cjk_families.end(), "Noto Sans CJK JP") == 1);

    std::array<std::string_view, 6U> latin_families{};
    require(try_get_font_fallback_family_preferences(
        {}, 0x41U, latin_families, written, &error));
    constexpr std::array expected_latin{
        std::string_view{"Helvetica"},
        std::string_view{"Arial"},
        std::string_view{"Segoe UI"},
        std::string_view{"Noto Sans"},
        std::string_view{"DejaVu Sans"},
        std::string_view{"Liberation Sans"}};
    require(latin_families == expected_latin);

    std::array<std::string_view, 5U> short_output{};
    short_output.fill("unchanged");
    written = 99U;
    require(!try_get_font_fallback_family_preferences(
        {}, 0x41U, short_output, written, &error));
    require(error == font_error::insufficient_buffer && written == 0U &&
        std::all_of(
            short_output.begin(), short_output.end(),
            [](std::string_view value) { return value == "unchanged"; }));

    require(try_get_font_fallback_family_preference_count(
        {}, 0xD800U, count, &error));
    require(error == font_error::none && count == 0U);
    require(!try_get_font_fallback_family_preference_count(
        {}, 0x110000U, count, &error));
    require(error == font_error::invalid_argument && count == 0U);
}

void native_font_provider_cache_is_borrowed_and_generation_safe() {
    constexpr std::array regular_mappings{std::pair{0x41U, 4U}};
    constexpr std::array bold_mappings{std::pair{0x41U, 4U}};
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
        result.face.identity == 2U && result.glyph_index == 4U &&
        context.reads == 2U);
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

    struct slant_provider_context final {
        std::array<font_provider_face, 3U> faces{};
    } slant_context{
        std::array{
            font_provider_face{regular, 10U, 9U, 0U, 500U, 5U,
                font_provider_slant::italic},
            font_provider_face{regular, 11U, 9U, 0U, 700U, 5U,
                font_provider_slant::oblique},
            font_provider_face{regular, 12U, 9U, 0U, 900U, 5U,
                font_provider_slant::normal}}};
    const auto slant_count = +[](void* value) noexcept -> std::uint32_t {
        return static_cast<std::uint32_t>(
            static_cast<slant_provider_context*>(value)->faces.size());
    };
    const auto slant_get = +[](void* value, std::uint32_t index,
                               font_provider_face& face) noexcept -> bool {
        const auto& source =
            *static_cast<slant_provider_context*>(value);
        if (index >= source.faces.size()) return false;
        face = source.faces[index];
        return true;
    };
    const font_provider_view slant_provider{
        &slant_context, 1U, slant_count, slant_get};
    std::array<font_provider_cache_entry, 2U> slant_cache{};
    std::uint32_t slant_cursor = 0U;
    require(try_resolve_font_provider_face(
        slant_provider, 9U, 500U, 5U, font_provider_slant::oblique, 0x41U,
        slant_cache, slant_cursor, result, &error));
    require(result.found && result.face.identity == 10U);
    require(try_resolve_font_provider_face(
        slant_provider, 9U, 500U, 5U, font_provider_slant::normal, 0x41U,
        slant_cache, slant_cursor, result, &error));
    require(result.found && result.face.identity == 12U);

    constexpr std::array empty_mappings{std::pair{0x41U, 2U}};
    constexpr std::array space_mappings{std::pair{0x20U, 2U}};
    const auto empty_cmap = make_cmap_groups(empty_mappings);
    const auto space_cmap = make_cmap_groups(space_mappings);
    const auto empty = make_font(
        0U, 22U, 0U, false, false, false, {}, empty_cmap);
    const auto space = make_font(
        0U, 22U, 0U, false, false, false, {}, space_cmap);
    struct coverage_provider_context final {
        std::array<font_provider_face, 3U> faces{};
    } coverage_context{
        std::array{
            font_provider_face{empty, 20U, 10U, 0U, 400U, 5U,
                font_provider_slant::normal},
            font_provider_face{regular, 21U, 10U, 0U, 400U, 5U,
                font_provider_slant::normal},
            font_provider_face{space, 22U, 11U, 0U, 400U, 5U,
                font_provider_slant::normal}}};
    const auto coverage_count = +[](void* value) noexcept -> std::uint32_t {
        return static_cast<std::uint32_t>(
            static_cast<coverage_provider_context*>(value)->faces.size());
    };
    const auto coverage_get =
        +[](void* value, std::uint32_t index,
            font_provider_face& face) noexcept -> bool {
        const auto& source =
            *static_cast<coverage_provider_context*>(value);
        if (index >= source.faces.size()) return false;
        face = source.faces[index];
        return true;
    };
    const font_provider_view coverage_provider{
        &coverage_context, 1U, coverage_count, coverage_get};
    std::array<font_provider_cache_entry, 2U> coverage_cache{};
    std::uint32_t coverage_cursor = 0U;
    require(try_resolve_font_provider_face(
        coverage_provider, 10U, 400U, 5U, font_provider_slant::normal, 0x41U,
        coverage_cache, coverage_cursor, result, &error));
    require(result.found && result.face.identity == 21U &&
        result.glyph_index == 4U);
    require(try_resolve_font_provider_face(
        coverage_provider, 11U, 400U, 5U, font_provider_slant::normal, 0x20U,
        coverage_cache, coverage_cursor, result, &error));
    require(result.found && result.face.identity == 22U &&
        result.glyph_index == 2U);

    struct priority_provider_context final {
        std::array<font_provider_face, 3U> faces{};
        std::uint32_t reads = 0U;
    } priority_context{
        std::array{
            font_provider_face{regular, 30U, 30U, 0U, 700U, 5U,
                font_provider_slant::normal, false},
            font_provider_face{regular, 31U, 31U, 0U, 400U, 5U,
                font_provider_slant::normal, false},
            font_provider_face{regular, 32U, 32U, 0U, 400U, 5U,
                font_provider_slant::normal, true}}};
    const auto priority_count = +[](void* value) noexcept -> std::uint32_t {
        return static_cast<std::uint32_t>(
            static_cast<priority_provider_context*>(value)->faces.size());
    };
    const auto priority_get =
        +[](void* value, std::uint32_t index,
            font_provider_face& face) noexcept -> bool {
        auto& source = *static_cast<priority_provider_context*>(value);
        ++source.reads;
        if (index >= source.faces.size()) return false;
        face = source.faces[index];
        return true;
    };
    const font_provider_view priority_provider{
        &priority_context, 1U, priority_count, priority_get};
    constexpr std::array<std::uint64_t, 2U> preferred_families{30U, 31U};
    require(try_resolve_font_provider_fallback_face(
        priority_provider,
        preferred_families,
        400U,
        5U,
        font_provider_slant::normal,
        0x41U,
        0U,
        result,
        &error));
    require(result.found && result.face.identity == 30U &&
        result.glyph_index == 4U && priority_context.reads == 3U);
    require(try_resolve_font_provider_fallback_face(
        priority_provider, {}, 400U, 5U, font_provider_slant::normal,
        0x41U, 0U, result, &error));
    require(result.found && result.face.identity == 32U &&
        priority_context.reads == 6U);
    require(try_resolve_font_provider_fallback_face(
        priority_provider, {}, 400U, 5U, font_provider_slant::normal,
        0x41U, 32U, result, &error));
    require(result.found && result.face.identity == 31U &&
        priority_context.reads == 9U);

    result = font_provider_result{priority_context.faces[0U], 0U, 4U, true};
    require(!try_resolve_font_provider_face(
        priority_provider, 30U, 400U, 5U, font_provider_slant::normal,
        0x110000U, {}, cursor, result, &error));
    require(error == font_error::invalid_argument && !result.found &&
        priority_context.reads == 9U);
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

    auto open_type_glyphs = glyphs;
    open_type_glyphs[0U].advance_y = -4;
    open_type_glyphs[0U].offset_y = -3;
    std::array<shaping_glyph, 4U> public_metric_scratch{};
    require(try_layout_open_type_text(
        open_type_glyphs,
        breaks,
        options,
        public_metric_scratch,
        positioned,
        lines,
        glyph_count,
        line_count,
        &error));
    require(glyph_count == 4U && line_count == 2U &&
        public_metric_scratch[0U].advance_y == 4 &&
        public_metric_scratch[0U].offset_y == 3 &&
        positioned[0U].advance_y == 4.0F && positioned[0U].y == 3.0F);
    positioned[0U].glyph_id = 99U;
    require(!try_layout_open_type_text(
        open_type_glyphs,
        breaks,
        options,
        std::span<shaping_glyph>{public_metric_scratch}.first(3U),
        positioned,
        lines,
        glyph_count,
        line_count,
        &error));
    require(error == font_error::insufficient_buffer &&
        glyph_count == 0U && line_count == 0U &&
        positioned[0U].glyph_id == 99U);
    auto invalid_open_type_glyphs = open_type_glyphs;
    invalid_open_type_glyphs[3U].offset_y =
        std::numeric_limits<std::int32_t>::min();
    public_metric_scratch[0U].glyph_id = 98U;
    require(!try_layout_open_type_text(
        invalid_open_type_glyphs,
        breaks,
        options,
        public_metric_scratch,
        positioned,
        lines,
        glyph_count,
        line_count,
        &error));
    require(error == font_error::invalid_argument &&
        public_metric_scratch[0U].glyph_id == 98U);
    text_layout_metrics metrics{};
    require(try_measure_positioned_text_lines(
        lines, options.maximum_width, metrics, &error));
    require(metrics.content_width == 20.0F &&
        metrics.content_height == 24.0F &&
        metrics.measured_width == 25.0F &&
        metrics.measured_height == 24.0F);

    auto centered_options = options;
    centered_options.alignment = text_alignment::center;
    require(try_layout_shaped_text(
        glyphs,
        breaks,
        centered_options,
        positioned,
        lines,
        glyph_count,
        line_count,
        &error));
    require(positioned[0U].x == 2.5F && positioned[1U].x == 12.5F &&
        positioned[2U].x == 2.5F && positioned[3U].x == 12.5F);

    auto right_options = options;
    right_options.alignment = text_alignment::right;
    require(try_layout_shaped_text(
        glyphs,
        breaks,
        right_options,
        positioned,
        lines,
        glyph_count,
        line_count,
        &error));
    require(positioned[0U].x == 5.0F && positioned[1U].x == 15.0F &&
        positioned[2U].x == 5.0F && positioned[3U].x == 15.0F);

    auto justified_options = options;
    justified_options.alignment = text_alignment::justify;
    require(try_layout_shaped_text(
        glyphs,
        breaks,
        justified_options,
        positioned,
        lines,
        glyph_count,
        line_count,
        &error));
    require(positioned[0U].x == 0.0F && positioned[1U].x == 10.0F);

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

    text_caret_stop caret{};
    require(try_get_text_caret_stop(
        carets, 1, true, caret, &error));
    require(caret.input_position == 1 && caret.trailing &&
        caret.line_index == 0U && caret.x == 10.0F);
    require(try_get_text_caret_stop(
        carets, 1, false, caret, &error));
    require(caret.input_position == 1 && !caret.trailing &&
        caret.line_index == 0U && caret.x == 10.0F);
    require(try_move_text_caret_visually(
        carets, 1, false, 1, caret, &error));
    require(caret.input_position == 2 && caret.trailing &&
        caret.line_index == 0U && caret.x == 20.0F);
    require(try_move_text_caret_visually(
        carets, 1, false, -1, caret, &error));
    require(caret.input_position == 1 && caret.trailing &&
        caret.line_index == 0U && caret.x == 10.0F);
    require(try_move_text_caret_visually(
        carets, 4, true, 1, caret, &error));
    require(caret.input_position == 3 && !caret.trailing &&
        caret.line_index == 1U && caret.x == 20.0F);
    require(!try_get_text_caret_stop({}, 0, false, caret, &error));
    require(error == font_error::invalid_argument);

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

    auto unsafe = glyphs;
    unsafe[1U].flags = shaping_glyph_flags::unsafe_to_break;
    unsafe[2U].flags = shaping_glyph_flags::unsafe_to_break;
    constexpr std::array<text_line_break_kind, 4U> unsafe_breaks{
        text_line_break_kind::opportunity,
        text_line_break_kind::opportunity,
        text_line_break_kind::opportunity,
        text_line_break_kind::opportunity};
    require(try_get_text_layout_requirements(
        unsafe, unsafe_breaks, narrow_options, requirements, &error));
    require(requirements.line_capacity == 2U);
    require(try_layout_shaped_text(
        unsafe,
        unsafe_breaks,
        narrow_options,
        positioned,
        narrow_lines,
        glyph_count,
        line_count,
        &error));
    require(line_count == 2U && narrow_lines[0U].glyph_count == 3U &&
        narrow_lines[0U].width == 30.0F &&
        narrow_lines[1U].glyph_count == 1U);

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

    auto vertical_options = options;
    vertical_options.direction =
        progpu::native::text::shaping_direction::top_to_bottom;
    require(!try_get_text_layout_requirements(
        glyphs, breaks, vertical_options, requirements, &error));
    require(error == font_error::invalid_argument &&
        requirements.glyph_capacity == 0U);
}

void native_text_visual_order_matches_managed_cluster_policy() {
    const std::array<shaping_glyph, 5U> logical{
        shaping_glyph{1U, 0x41U, 0},
        shaping_glyph{10U, 0x05D0U, 1},
        shaping_glyph{11U, 0x05B0U, 1},
        shaping_glyph{20U, 0x05D1U, 2},
        shaping_glyph{30U, 0x20U, 3}};
    constexpr std::array<std::int8_t, 5U> levels{0, 1, 1, 1, 1};
    text_visual_order_requirements requirements{};
    font_error error = font_error::invalid_argument;
    require(try_get_text_visual_order_requirements(
        logical, levels, requirements, &error));
    require(error == font_error::none &&
        requirements.glyph_capacity == 5U &&
        requirements.group_capacity == 4U);

    std::array<text_visual_cluster_group, 4U> groups{};
    std::array<shaping_glyph, 5U> visual{};
    std::uint32_t written = 0U;
    require(try_reorder_text_line_visual(
        logical, levels, 0, groups, visual, written, &error));
    require(written == visual.size() &&
        visual[0U].glyph_id == 1U &&
        visual[1U].glyph_id == 20U &&
        visual[2U].glyph_id == 10U &&
        visual[3U].glyph_id == 11U &&
        visual[4U].glyph_id == 30U);

    std::array<std::uint32_t, 5U> visual_indices{};
    require(try_get_text_line_visual_indices(
        logical, levels, 0, groups, visual_indices, written, &error));
    require(written == visual_indices.size() &&
        visual_indices == std::array<std::uint32_t, 5U>{0U, 3U, 1U, 2U, 4U});

    constexpr std::array<std::int8_t, 5U> even_levels{0, 2, 2, 2, 0};
    require(try_reorder_text_line_visual(
        logical, even_levels, 0, groups, visual, written, &error));
    require(std::equal(
        logical.begin(), logical.end(), visual.begin(),
        [](const shaping_glyph& left, const shaping_glyph& right) {
            return left.glyph_id == right.glyph_id;
        }));

    visual.fill(shaping_glyph{99U});
    written = 99U;
    require(!try_reorder_text_line_visual(
        logical,
        levels,
        0,
        std::span<text_visual_cluster_group>{groups}.first(3U),
        visual,
        written,
        &error));
    require(error == font_error::insufficient_buffer && written == 0U &&
        visual[0U].glyph_id == 99U);
    auto invalid_levels = levels;
    invalid_levels[0U] = -1;
    require(!try_get_text_visual_order_requirements(
        logical, invalid_levels, requirements, &error));
    require(error == font_error::invalid_argument &&
        requirements.glyph_capacity == 0U);
}

void native_logical_text_layout_reorders_bidi_per_line() {
    const std::array<shaping_glyph, 5U> logical{
        shaping_glyph{1U, 0x41U, 0, shaping_glyph_flags::none, 10},
        shaping_glyph{10U, 0x05D0U, 1, shaping_glyph_flags::none, 10},
        shaping_glyph{11U, 0x05B0U, 1, shaping_glyph_flags::none, 0},
        shaping_glyph{20U, 0x05D1U, 2, shaping_glyph_flags::none, 10},
        shaping_glyph{30U, 0x20U, 3, shaping_glyph_flags::none, 5}};
    constexpr std::array<std::int8_t, 5U> levels{0, 1, 1, 1, 1};
    constexpr std::array<text_line_break_kind, 5U> breaks{
        text_line_break_kind::prohibited,
        text_line_break_kind::prohibited,
        text_line_break_kind::prohibited,
        text_line_break_kind::prohibited,
        text_line_break_kind::mandatory};
    const text_layout_options options{
        1.0F,
        0.0F,
        12.0F,
        0U,
        progpu::native::text::shaping_direction::left_to_right};

    std::array<text_visual_cluster_group, 5U> groups{};
    std::array<std::uint32_t, 5U> indices{};
    std::array<positioned_text_glyph, 5U> positioned{};
    std::array<positioned_text_line, 1U> lines{};
    const text_logical_layout_scratch scratch{groups, indices};
    std::uint32_t glyph_count = 0U;
    std::uint32_t line_count = 0U;
    font_error error = font_error::invalid_argument;
    require(try_layout_logical_shaped_text(
        logical,
        breaks,
        levels,
        0,
        options,
        scratch,
        positioned,
        lines,
        glyph_count,
        line_count,
        &error));
    require(error == font_error::none && glyph_count == 5U && line_count == 1U);
    require(positioned[0U].glyph_index == 0U &&
        positioned[1U].glyph_index == 3U &&
        positioned[2U].glyph_index == 1U &&
        positioned[3U].glyph_index == 2U &&
        positioned[4U].glyph_index == 4U);
    require(positioned[0U].x == 0.0F && positioned[1U].x == 10.0F &&
        positioned[2U].x == 20.0F && positioned[3U].x == 30.0F &&
        positioned[4U].x == 30.0F);
    require(lines[0U].glyph_start == 0U && lines[0U].glyph_count == 5U &&
        lines[0U].input_start == 0 && lines[0U].input_end == 4 &&
        lines[0U].width == 35.0F && !lines[0U].clipped);

    auto right_options = options;
    right_options.maximum_width = 45.0F;
    right_options.alignment = text_alignment::right;
    require(try_layout_logical_shaped_text(
        logical,
        breaks,
        levels,
        0,
        right_options,
        scratch,
        positioned,
        lines,
        glyph_count,
        line_count,
        &error));
    require(positioned[0U].x == 10.0F && positioned[1U].x == 20.0F &&
        positioned[2U].x == 30.0F && positioned[3U].x == 40.0F &&
        positioned[4U].x == 40.0F);

    indices.fill(99U);
    glyph_count = 99U;
    line_count = 99U;
    require(!try_layout_logical_shaped_text(
        logical,
        breaks,
        levels,
        0,
        options,
        text_logical_layout_scratch{
            groups, std::span<std::uint32_t>{indices}.first(4U)},
        positioned,
        lines,
        glyph_count,
        line_count,
        &error));
    require(error == font_error::insufficient_buffer && glyph_count == 0U &&
        line_count == 0U && indices[0U] == 99U);
}

void native_vertical_text_layout_matches_managed_columns() {
    const std::array<shaping_glyph, 4U> top_to_bottom{
        shaping_glyph{1U, 0x4E00U, 0, shaping_glyph_flags::none, 0, 8},
        shaping_glyph{2U, 0x4E01U, 1, shaping_glyph_flags::none, 0, 8},
        shaping_glyph{3U, 0x4E02U, 2, shaping_glyph_flags::none, 0, 8},
        shaping_glyph{4U, 0x4E03U, 3, shaping_glyph_flags::none, 0, 8}};
    constexpr std::array<text_line_break_kind, 4U> breaks{
        text_line_break_kind::prohibited,
        text_line_break_kind::mandatory,
        text_line_break_kind::prohibited,
        text_line_break_kind::mandatory};
    const text_layout_options options{
        1.0F,
        30.0F,
        10.0F,
        0U,
        progpu::native::text::shaping_direction::top_to_bottom,
        text_trimming::none,
        text_alignment::center};

    text_vertical_layout_requirements requirements{};
    font_error error = font_error::invalid_argument;
    require(try_get_vertical_text_layout_requirements(
        top_to_bottom, breaks, options, requirements, &error));
    require(error == font_error::none &&
        requirements.glyph_capacity == 4U &&
        requirements.column_capacity == 2U);

    std::array<positioned_text_glyph, 4U> positioned{};
    std::array<positioned_text_column, 2U> columns{};
    std::uint32_t glyph_count = 0U;
    std::uint32_t column_count = 0U;
    require(try_layout_vertical_shaped_text(
        top_to_bottom,
        breaks,
        options,
        positioned,
        columns,
        glyph_count,
        column_count,
        &error));
    require(glyph_count == 4U && column_count == 2U &&
        positioned[0U].x == 10.0F && positioned[0U].y == 0.0F &&
        positioned[1U].x == 10.0F && positioned[1U].y == 8.0F &&
        positioned[2U].x == 20.0F && positioned[2U].y == 0.0F &&
        positioned[3U].x == 20.0F && positioned[3U].y == 8.0F);
    require(columns[0U].glyph_start == 0U &&
        columns[0U].glyph_count == 2U && columns[0U].height == 16.0F &&
        columns[0U].x == 5.0F && columns[0U].width == 10.0F &&
        !columns[0U].clipped && columns[1U].x == 15.0F);
    text_layout_metrics metrics{};
    require(try_measure_positioned_text_columns(
        columns, options.maximum_width, metrics, &error));
    require(metrics.content_width == 20.0F &&
        metrics.content_height == 16.0F &&
        metrics.measured_width == 30.0F &&
        metrics.measured_height == 16.0F);

    constexpr std::array<std::int32_t, 4U> cluster_ends{1, 2, 3, 4};
    constexpr std::array<std::int8_t, 4U> bidi_levels{0, 0, 0, 0};
    text_interaction_requirements interaction_requirements{};
    require(try_get_vertical_text_interaction_requirements(
        positioned,
        columns,
        cluster_ends,
        bidi_levels,
        options.direction,
        interaction_requirements,
        &error));
    require(interaction_requirements.cluster_box_capacity == 4U &&
        interaction_requirements.caret_stop_capacity == 8U);
    std::array<text_vertical_cluster_box, 4U> boxes{};
    std::array<text_vertical_caret_stop, 8U> carets{};
    std::uint32_t box_count = 0U;
    std::uint32_t caret_count = 0U;
    require(try_build_vertical_text_interaction(
        positioned,
        columns,
        cluster_ends,
        bidi_levels,
        options.direction,
        boxes,
        carets,
        box_count,
        caret_count,
        &error));
    require(box_count == 4U && caret_count == 8U &&
        boxes[0U].x == 5.0F && boxes[0U].y == 0.0F &&
        boxes[0U].width == 10.0F && boxes[0U].height == 8.0F &&
        !boxes[0U].bottom_to_top && boxes[2U].column_index == 1U);
    require(carets[0U].input_position == 0 && carets[0U].y == 0.0F &&
        !carets[0U].trailing && carets[1U].input_position == 1 &&
        carets[1U].y == 8.0F && carets[1U].trailing);

    text_vertical_hit_test_result hit{};
    require(try_hit_test_vertical_text(boxes, 8.0F, 6.0F, hit, &error));
    require(hit.input_position == 1 && hit.trailing && hit.inside &&
        hit.column_index == 0U && hit.bounds.height == 8.0F);
    text_vertical_caret_stop caret{};
    require(try_get_vertical_text_caret_stop(
        carets, 1, true, caret, &error));
    require(caret.input_position == 1 && caret.trailing && caret.y == 8.0F);
    require(try_move_vertical_text_caret_visually(
        carets, 1, true, 1, caret, &error));
    require(caret.input_position == 1 && !caret.trailing && caret.y == 8.0F);
    std::array<text_rectangle, 4U> selection{};
    std::uint32_t selection_count = 0U;
    require(try_get_vertical_text_selection_rectangles(
        boxes, 0, 2, selection, selection_count, &error));
    require(selection_count == 1U && selection[0U].x == 5.0F &&
        selection[0U].y == 0.0F && selection[0U].width == 10.0F &&
        selection[0U].height == 16.0F);

    auto bottom_to_top = top_to_bottom;
    for (auto& glyph : bottom_to_top) glyph.advance_y = -8;
    auto reverse_options = options;
    reverse_options.direction =
        progpu::native::text::shaping_direction::bottom_to_top;
    require(try_layout_vertical_shaped_text(
        bottom_to_top,
        breaks,
        reverse_options,
        positioned,
        columns,
        glyph_count,
        column_count,
        &error));
    require(positioned[0U].y == 0.0F && positioned[1U].y == -8.0F &&
        positioned[0U].advance_y == -8.0F && columns[0U].height == 16.0F);
    require(try_build_vertical_text_interaction(
        positioned,
        columns,
        cluster_ends,
        bidi_levels,
        reverse_options.direction,
        boxes,
        carets,
        box_count,
        caret_count,
        &error));
    require(boxes[0U].bottom_to_top && boxes[0U].y == -8.0F &&
        carets[0U].y == 0.0F && carets[1U].y == -8.0F);
    require(try_hit_test_vertical_text(boxes, 8.0F, -6.0F, hit, &error));
    require(hit.input_position == 1 && hit.trailing && hit.inside);

    box_count = 99U;
    caret_count = 99U;
    require(!try_build_vertical_text_interaction(
        positioned,
        columns,
        cluster_ends,
        bidi_levels,
        reverse_options.direction,
        std::span<text_vertical_cluster_box>{boxes}.first(1U),
        carets,
        box_count,
        caret_count,
        &error));
    require(error == font_error::insufficient_buffer && box_count == 0U &&
        caret_count == 0U);

    auto clipped_options = options;
    clipped_options.maximum_lines = 1U;
    require(try_get_vertical_text_layout_requirements(
        top_to_bottom, breaks, clipped_options, requirements, &error));
    require(requirements.glyph_capacity == 2U &&
        requirements.column_capacity == 1U);
    require(try_layout_vertical_shaped_text(
        top_to_bottom,
        breaks,
        clipped_options,
        positioned,
        columns,
        glyph_count,
        column_count,
        &error));
    require(glyph_count == 2U && column_count == 1U && columns[0U].clipped);

    positioned.fill(positioned_text_glyph{99U});
    glyph_count = 99U;
    column_count = 99U;
    require(!try_layout_vertical_shaped_text(
        top_to_bottom,
        breaks,
        options,
        std::span<positioned_text_glyph>{positioned}.first(3U),
        columns,
        glyph_count,
        column_count,
        &error));
    require(error == font_error::insufficient_buffer && glyph_count == 0U &&
        column_count == 0U && positioned[0U].glyph_index == 99U);

    auto invalid_options = options;
    invalid_options.direction =
        progpu::native::text::shaping_direction::left_to_right;
    require(!try_get_vertical_text_layout_requirements(
        top_to_bottom, breaks, invalid_options, requirements, &error));
    require(error == font_error::invalid_argument &&
        requirements.glyph_capacity == 0U);

    metrics = text_layout_metrics{1.0F, 2.0F, 3.0F, 4.0F};
    require(!try_measure_positioned_text_columns(
        columns,
        std::numeric_limits<float>::quiet_NaN(),
        metrics,
        &error));
    require(error == font_error::invalid_argument &&
        metrics.content_width == 0.0F && metrics.content_height == 0.0F);
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
    static_assert(sizeof(unicode_vowel_constraint) == 16U);

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
    require(is_unicode_mark(0x093FU));
    require(is_unicode_mark(0x0DCFU));
    require(is_unicode_mark(0x1038U));
    require(is_unicode_mark(0x20DDU));
    require(!is_unicode_mark(0x0915U));
    require(!is_unicode_mark(0x110000U));

    require(get_unicode_vowel_constraint_count() == 103U);
    unicode_vowel_constraint constraint{};
    require(try_get_unicode_vowel_constraint(0U, constraint));
    require(constraint.script == open_type_tag::from_chars('b', 'e', 'n', 'g') &&
        constraint.first == 0x985U && constraint.second == 0x9BEU &&
        constraint.third == 0U);
    require(try_get_unicode_vowel_constraint(27U, constraint));
    require(constraint.script == open_type_tag::from_chars('d', 'e', 'v', 'a') &&
        constraint.first == 0x930U && constraint.second == 0x94DU &&
        constraint.third == 0x907U);
    constraint = {open_type_tag{1U}, 1U, 1U, 1U};
    require(!try_get_unicode_vowel_constraint(103U, constraint));
    require(constraint.script.value == 0U && constraint.first == 0U &&
        constraint.second == 0U && constraint.third == 0U);

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
        std::uint8_t state_action = 99U;
        require(try_get_unicode_syllable_to_state_action(
            expected.machine, expected.start_state, state_action));
        require(state_action <= 10U);
        state_action = 99U;
        require(try_get_unicode_syllable_from_state_action(
            expected.machine, expected.start_state, state_action));
        require(state_action <= 10U);
        state_action = 99U;
        require(!try_get_unicode_syllable_to_state_action(
            expected.machine, expected.state_count, state_action));
        require(state_action == 0U);
        state_action = 99U;
        require(!try_get_unicode_syllable_from_state_action(
            expected.machine, expected.state_count, state_action));
        require(state_action == 0U);
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
    sfnt_variation_axis selected{};
    require(font.try_get_variation_axis(1U, selected, &error));
    require(selected.tag == axes[1].tag &&
        selected.minimum_fixed == axes[1].minimum_fixed);
    require(!font.try_get_variation_axis(2U, selected, &error));
    require(error == font_error::invalid_argument &&
        selected.tag.value == 0U);

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

void font_style_variations_match_managed_font_manager_policy() {
    const std::array<table_data, 1U> tables{
        table_data{
            open_type_tag::from_chars('f', 'v', 'a', 'r'),
            make_style_fvar()}};
    const auto data = make_font(
        0U, 22U, 0U, false, false, false, tables);
    sfnt_font_view font{};
    font_error error = font_error::none;
    require(sfnt_font_view::try_create(data, 0U, font, &error));

    const font_style_request request{
        700, 2, font_provider_slant::italic};
    font_style_variation_requirements requirements{};
    require(try_get_font_style_variation_requirements(
        font, request, requirements, &error));
    require(requirements.setting_count == 4U);
    std::array<font_style_variation, 3U> short_output{};
    short_output.fill(font_style_variation{
        open_type_tag::from_chars('x', 'x', 'x', 'x'), 1, 2, 3U});
    std::uint16_t written = 99U;
    require(!try_resolve_font_style_variations(
        font, request, short_output, written, nullptr, &error));
    require(error == font_error::insufficient_buffer && written == 0U);
    require(short_output[0].tag ==
        open_type_tag::from_chars('x', 'x', 'x', 'x'));

    std::array<font_style_variation, 4U> output{};
    font_style_variation_requirements reported{};
    require(try_resolve_font_style_variations(
        font, request, output, written, &reported, &error));
    require(error == font_error::none && written == 4U &&
        reported.setting_count == 4U);
    require(output[0].tag ==
        open_type_tag::from_chars('w', 'g', 'h', 't'));
    require(output[0].user_fixed == 700 << 16 &&
        output[0].normalized == 9830 && output[0].axis_index == 0U);
    require(output[1].tag ==
        open_type_tag::from_chars('w', 'd', 't', 'h'));
    require(output[1].user_fixed == (62 << 16) + (1 << 15) &&
        output[1].normalized == -12288 && output[1].axis_index == 1U);
    require(output[2].tag ==
        open_type_tag::from_chars('i', 't', 'a', 'l'));
    require(output[2].user_fixed == 1 << 16 &&
        output[2].normalized == 16384);
    require(output[3].tag ==
        open_type_tag::from_chars('s', 'l', 'n', 't'));
    require(output[3].user_fixed == -20 * (1 << 16) &&
        output[3].normalized == -16384);

    require(try_resolve_font_style_variations(
        font, font_style_request{0, 0, font_provider_slant::normal},
        output, written, nullptr, &error));
    require(written == 4U);
    require(output[0].user_fixed == 400 << 16 &&
        output[0].normalized == 0);
    require(output[1].user_fixed == 100 << 16 &&
        output[1].normalized == 0);
    require(output[2].user_fixed == 0 && output[2].normalized == 0);
    require(output[3].user_fixed == 0 && output[3].normalized == 0);

    const auto invalid_slant = static_cast<font_provider_slant>(99U);
    require(!try_get_font_style_variation_requirements(
        font, font_style_request{400, 5, invalid_slant},
        requirements, &error));
    require(error == font_error::invalid_argument &&
        requirements.setting_count == 0U);

    const auto fixed_data = make_font();
    require(sfnt_font_view::try_create(fixed_data, 0U, font, &error));
    require(try_get_font_style_variation_requirements(
        font, request, requirements, &error));
    require(requirements.setting_count == 0U);
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
    std::uint32_t item_count = 0U;
    require(font.try_get_glyph_variation_item_count(
        4U, item_count));
    require(item_count == 7U);
    sfnt_design_advance_width_requirements advance_requirements{};
    require(font.try_get_design_advance_width_requirements(
        4U, normalized, advance_requirements));
    require(advance_requirements.glyph_variation_item_count == 7U &&
        advance_requirements.phantom.tuple_header_count == 1U &&
        advance_requirements.phantom.region_coordinate_count == 6U &&
        advance_requirements.phantom.point_number_count == 7U &&
        advance_requirements.phantom.delta_count == 7U);
    float delta = 99.0F;
    font_error error = font_error::none;
    require(font.try_get_glyph_phantom_advance_delta(
        4U, normalized, 7U, delta, scratch, &error));
    require(error == font_error::none && delta == 3.0F);

    float advance = 99.0F;
    require(font.try_get_design_advance_width(
        4U, normalized, advance, scratch, &error));
    require(error == font_error::none && advance == 603.0F);

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
    advance = 99.0F;
    require(!font.try_get_design_advance_width(
        4U, normalized, advance, short_scratch, &error));
    require(error == font_error::insufficient_buffer && advance == 0.0F);

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

void standalone_sfnt_snapshot_matches_managed_contract() {
    const std::array<std::byte, 3U> cff2_bytes{
        std::byte{0x02U}, std::byte{0x00U}, std::byte{0x05U}};
    const std::array<table_data, 1U> extra_tables{
        table_data{
            open_type_tag::from_chars('C', 'F', 'F', '2'),
            std::vector<std::byte>(cff2_bytes.begin(), cff2_bytes.end())}};
    auto collection = make_font(
        16U, 22U, 0U, false, false, false, extra_tables);
    write_u32(collection, 0U, 0x74746366U);
    write_u32(collection, 4U, 0x00010000U);
    write_u32(collection, 8U, 1U);
    write_u32(collection, 12U, 16U);

    sfnt_font_view font{};
    font_error error = font_error::none;
    require(sfnt_font_view::try_create(collection, 0U, font, &error));
    sfnt_standalone_requirements requirements{};
    require(font.try_get_standalone_requirements(requirements, &error));
    require(requirements.table_scratch_count == 8U);
    require(requirements.font_bytes == 404U);

    std::vector<sfnt_directory_record> scratch(
        requirements.table_scratch_count);
    std::vector<std::byte> short_output(
        requirements.font_bytes - 1U, std::byte{0xA5U});
    std::size_t written = 99U;
    require(!font.try_create_standalone_font(
        short_output, scratch, written, nullptr, &error));
    require(error == font_error::insufficient_buffer && written == 0U);
    require(std::ranges::all_of(short_output,
        [](std::byte value) { return value == std::byte{0xA5U}; }));

    std::vector<std::byte> output(
        requirements.font_bytes, std::byte{0xA5U});
    const auto too_little_scratch =
        std::span<sfnt_directory_record>(scratch).first(
            requirements.table_scratch_count - 1U);
    require(!font.try_create_standalone_font(
        output, too_little_scratch, written, nullptr, &error));
    require(error == font_error::insufficient_buffer && written == 0U);
    require(std::ranges::all_of(output,
        [](std::byte value) { return value == std::byte{0xA5U}; }));

    sfnt_standalone_requirements reported{};
    require(font.try_create_standalone_font(
        output, scratch, written, &reported, &error));
    require(error == font_error::none && written == output.size());
    require(reported.font_bytes == requirements.font_bytes &&
        reported.table_scratch_count == requirements.table_scratch_count);
    require(read_u32(output, 0U) ==
        open_type_tag::from_chars('O', 'T', 'T', 'O').value);
    require(read_u16(output, 4U) == 8U);
    require(read_u16(output, 6U) == 128U);
    require(read_u16(output, 8U) == 3U);
    require(read_u16(output, 10U) == 0U);
    std::uint32_t prior_tag = 0U;
    for (std::size_t index = 0U; index < 8U; ++index) {
        const auto record = 12U + index * 16U;
        const auto tag = read_u32(output, record);
        require(index == 0U || prior_tag < tag);
        require((read_u32(output, record + 8U) & 3U) == 0U);
        prior_tag = tag;
    }

    sfnt_font_view standalone{};
    require(sfnt_font_view::try_create(output, 0U, standalone, &error));
    require(standalone.face_offset() == 0U &&
        standalone.table_count() == 8U);
    sfnt_table_view cff2{};
    require(standalone.try_get_table(
        open_type_tag::from_chars('C', 'F', 'F', '2'), cff2));
    require(std::ranges::equal(cff2.bytes, cff2_bytes));
    sfnt_header_metrics metrics{};
    require(standalone.try_get_header_metrics(metrics));
    require(metrics.units_per_em == 1000U && metrics.x_min == -20);

    auto duplicate = make_font();
    const auto final_record = 12U + 6U * 16U;
    write_u32(duplicate, final_record,
        open_type_tag::from_chars('h', 'e', 'a', 'd').value);
    require(sfnt_font_view::try_create(duplicate, 0U, font, &error));
    require(font.try_get_standalone_requirements(requirements, &error));
    require(requirements.table_scratch_count == 6U);
    output.assign(requirements.font_bytes, std::byte{});
    scratch.assign(requirements.table_scratch_count, {});
    require(font.try_create_standalone_font(
        output, scratch, written, nullptr, &error));
    require(sfnt_font_view::try_create(output, 0U, standalone, &error));
    sfnt_table_view head{};
    require(standalone.try_get_table(
        open_type_tag::from_chars('h', 'e', 'a', 'd'), head));
    require(head.checksum == 0x1006U && head.bytes.size() == 80U);
}

void vertical_font_metrics_and_shaping_match_managed_policy() {
    table_data vhea{open_type_tag::from_chars('v', 'h', 'e', 'a'),
        std::vector<std::byte>(36U)};
    write_i16(vhea.bytes, 4U, 1000);
    write_i16(vhea.bytes, 6U, -500);
    write_i16(vhea.bytes, 8U, 50);
    write_u16(vhea.bytes, 10U, 1200U);
    write_u16(vhea.bytes, 34U, 2U);
    table_data vmtx{open_type_tag::from_chars('v', 'm', 't', 'x'),
        std::vector<std::byte>(20U)};
    write_u16(vmtx.bytes, 0U, 800U);
    write_i16(vmtx.bytes, 2U, 40);
    write_u16(vmtx.bytes, 4U, 900U);
    write_i16(vmtx.bytes, 6U, 50);
    write_i16(vmtx.bytes, 10U, 60);
    write_i16(vmtx.bytes, 12U, 70);
    table_data vorg{open_type_tag::from_chars('V', 'O', 'R', 'G'),
        std::vector<std::byte>(16U)};
    write_u16(vorg.bytes, 0U, 1U);
    write_u16(vorg.bytes, 2U, 0U);
    write_i16(vorg.bytes, 4U, 880);
    write_u16(vorg.bytes, 6U, 2U);
    write_u16(vorg.bytes, 8U, 4U);
    write_i16(vorg.bytes, 10U, 920);
    write_u16(vorg.bytes, 12U, 7U);
    write_i16(vorg.bytes, 14U, 940);

    const std::array<table_data, 3U> with_vorg{vhea, vmtx, vorg};
    const auto vorg_data = make_font(
        0U, 22U, 0U, false, false, false, with_vorg);
    sfnt_font_view font{};
    font_error error = font_error::none;
    require(sfnt_font_view::try_create(vorg_data, 0U, font, &error));
    sfnt_vertical_header_metrics header{};
    require(font.try_get_vertical_header_metrics(header));
    require(header.ascender == 1000 && header.descender == -500 &&
        header.line_gap == 50 && header.advance_height_max == 1200U &&
        header.number_of_vertical_metrics == 2U);
    sfnt_vertical_glyph_metrics metrics{};
    require(font.try_get_vertical_glyph_metrics(4U, metrics));
    require(metrics.advance_height == 900U &&
        metrics.top_side_bearing == 70 && metrics.has_top_side_bearing);
    sfnt_glyph_bounds bounds{};
    require(font.try_get_glyph_bounds(4U, bounds));
    require(bounds.x_min == 10 && bounds.y_min == 0 &&
        bounds.x_max == 30 && bounds.y_max == 40);
    require(!font.try_get_glyph_bounds(3U, bounds));
    std::int32_t value = 0;
    require(font.try_get_design_advance_height(4U, value) && value == 900);
    require(font.try_get_design_vertical_origin_y(4U, value) && value == 920);
    require(font.try_get_design_vertical_origin_y(5U, value) && value == 880);

    constexpr std::array mappings{
        std::pair{0x41U, 4U}, std::pair{0x42U, 4U},
        std::pair{0x0301U, 4U}, std::pair{0x0308U, 4U}};
    const auto cmap = make_cmap_groups(mappings);
    const std::array<table_data, 2U> vertical_tables{vhea, vmtx};
    const auto vertical_data = make_font(
        0U, 22U, 0U, false, false, false, vertical_tables, cmap);
    require(sfnt_font_view::try_create(vertical_data, 0U, font, &error));
    require(font.try_get_design_vertical_origin_y(4U, value) && value == 110);
    require(font.try_get_design_vertical_origin_y(3U, value) && value == 500);

    const std::array<unicode_scalar, 2U> input{
        unicode_scalar{0x41U, 0U, 1U},
        unicode_scalar{0x42U, 1U, 1U}};
    open_type_shape_run_requirements requirements{};
    require(try_get_open_type_shape_run_requirements(
        font, input, requirements, &error));
    std::array<shaping_glyph, 6U> glyphs{};
    std::array<unicode_grapheme_cluster, 2U> graphemes{};
    std::array<shaping_attachment, 6U> attachments{};
    std::array<std::uint8_t, 6U> states{};
    open_type_shape_run_options options{
        open_type_tag::from_chars('l', 'a', 't', 'n')};
    options.direction = shaping_direction::top_to_bottom;
    std::uint32_t glyph_count = 0U;
    require(try_shape_open_type_run(
        font,
        input,
        options,
        glyphs,
        open_type_shape_run_scratch{
            graphemes, {}, {}, attachments, states},
        glyph_count,
        &error));
    require(glyph_count == 2U && glyphs[0].glyph_id == 4U &&
        glyphs[0].cluster == 0 && glyphs[1].cluster == 1);
    require(glyphs[0].advance_x == 0 && glyphs[0].advance_y == -900);
    require(glyphs[0].offset_x == -300 && glyphs[0].offset_y == -110);

    static_assert(sizeof(open_type_shaped_glyph) == 32U);
    std::array<open_type_shaped_glyph, 2U> projected{};
    require(try_project_open_type_shape_result(
        font,
        {},
        std::span<const shaping_glyph>{glyphs}.first(glyph_count),
        500.0F,
        shaping_direction::top_to_bottom,
        projected,
        nullptr,
        &error));
    require(projected[0U].glyph_id == 4U &&
        projected[0U].code_point == 0x41U &&
        projected[0U].cluster == 0 &&
        projected[0U].advance_x == 0.0F &&
        projected[0U].advance_y == 450.0F &&
        projected[0U].offset_x == -150.0F &&
        projected[0U].offset_y == 55.0F);

    constexpr std::array<shaping_glyph, 1U> horizontal_design{{
        {4U,
            0x41U,
            7,
            shaping_glyph_flags::unsafe_to_break,
            600,
            -20,
            30,
            -40}}};
    std::array<open_type_shaped_glyph, 1U> horizontal_projected{};
    require(try_project_open_type_shape_result(
        font,
        {},
        horizontal_design,
        500.0F,
        shaping_direction::left_to_right,
        horizontal_projected,
        nullptr,
        &error));
    require(horizontal_projected[0U].glyph_id == 4U &&
        horizontal_projected[0U].cluster == 7 &&
        horizontal_projected[0U].flags ==
            shaping_glyph_flags::unsafe_to_break &&
        horizontal_projected[0U].advance_x == 300.0F &&
        horizontal_projected[0U].advance_y == 10.0F &&
        horizontal_projected[0U].offset_x == 15.0F &&
        horizontal_projected[0U].offset_y == 20.0F);

    projected[0U].glyph_id = 99U;
    require(!try_project_open_type_shape_result(
        font,
        {},
        std::span<const shaping_glyph>{glyphs}.first(glyph_count),
        500.0F,
        shaping_direction::top_to_bottom,
        std::span<open_type_shaped_glyph>{projected}.first(1U),
        nullptr,
        &error));
    require(error == font_error::insufficient_buffer &&
        projected[0U].glyph_id == 99U);

    auto invalid_projection_glyphs =
        std::array<shaping_glyph, 2U>{glyphs[0U], glyphs[1U]};
    invalid_projection_glyphs[1U].glyph_id = 0x10000U;
    projected[0U].glyph_id = 98U;
    projected[1U].glyph_id = 97U;
    require(!try_project_open_type_shape_result(
        font,
        {},
        invalid_projection_glyphs,
        500.0F,
        shaping_direction::top_to_bottom,
        projected,
        nullptr,
        &error));
    require(error == font_error::invalid_glyph &&
        projected[0U].glyph_id == 98U && projected[1U].glyph_id == 97U);

    constexpr std::array<text_line_break_kind, 2U> vertical_breaks{
        text_line_break_kind::prohibited,
        text_line_break_kind::mandatory};
    const text_layout_options vertical_layout_options{
        0.5F,
        1000.0F,
        20.0F,
        0U,
        shaping_direction::top_to_bottom,
        text_trimming::none,
        text_alignment::left};
    std::array<text_vertical_open_type_metrics, 2U> metric_scratch{};
    std::array<positioned_text_glyph, 2U> positioned{};
    std::array<positioned_text_column, 1U> columns{};
    std::uint32_t positioned_count = 0U;
    std::uint32_t column_count = 0U;
    require(try_layout_vertical_open_type_text(
        font,
        {},
        std::span<const shaping_glyph>{glyphs}.first(glyph_count),
        vertical_breaks,
        vertical_layout_options,
        metric_scratch,
        positioned,
        columns,
        positioned_count,
        column_count,
        nullptr,
        &error));
    require(positioned_count == 2U && column_count == 1U &&
        metric_scratch[0U].advance_y == 450.0F &&
        metric_scratch[0U].offset_x == -150.0F &&
        metric_scratch[0U].offset_y == 55.0F &&
        positioned[0U].x == -140.0F && positioned[0U].y == 55.0F &&
        positioned[1U].y == 505.0F && columns[0U].height == 900.0F);
    positioned[0U].glyph_index = 99U;
    positioned_count = 99U;
    column_count = 99U;
    require(!try_layout_vertical_open_type_text(
        font,
        {},
        std::span<const shaping_glyph>{glyphs}.first(glyph_count),
        vertical_breaks,
        vertical_layout_options,
        std::span<text_vertical_open_type_metrics>{metric_scratch}.first(1U),
        positioned,
        columns,
        positioned_count,
        column_count,
        nullptr,
        &error));
    require(error == font_error::insufficient_buffer &&
        positioned_count == 0U && column_count == 0U &&
        positioned[0U].glyph_index == 99U);

    options.direction = shaping_direction::bottom_to_top;
    require(try_shape_open_type_run(
        font,
        input,
        options,
        glyphs,
        open_type_shape_run_scratch{
            graphemes, {}, {}, attachments, states},
        glyph_count,
        &error));
    require(glyph_count == 2U && glyphs[0].advance_y == -900 &&
        glyphs[0].offset_y == -110 && glyphs[0].cluster == 1 &&
        glyphs[1].cluster == 0);
    auto bottom_to_top_layout_options = vertical_layout_options;
    bottom_to_top_layout_options.direction =
        shaping_direction::bottom_to_top;
    require(try_layout_vertical_open_type_text(
        font,
        {},
        std::span<const shaping_glyph>{glyphs}.first(glyph_count),
        vertical_breaks,
        bottom_to_top_layout_options,
        metric_scratch,
        positioned,
        columns,
        positioned_count,
        column_count,
        nullptr,
        &error));
    require(positioned_count == 2U && column_count == 1U &&
        positioned[0U].cluster == 1 && positioned[1U].cluster == 0 &&
        positioned[0U].advance_y == 450.0F &&
        positioned[1U].y == 505.0F);
    options.direction = shaping_direction::right_to_left;
    require(try_shape_open_type_run(
        font,
        input,
        options,
        glyphs,
        open_type_shape_run_scratch{
            graphemes, {}, {}, attachments, states},
        glyph_count,
        &error));
    require(glyph_count == 2U && glyphs[0].advance_x == 600 &&
        glyphs[0].advance_y == 0 && glyphs[0].cluster == 1 &&
        glyphs[1].cluster == 0);

    const std::array<unicode_scalar, 3U> combining_input{
        unicode_scalar{0x41U, 0U, 1U},
        unicode_scalar{0x0301U, 1U, 1U},
        unicode_scalar{0x0308U, 2U, 1U}};
    std::array<shaping_glyph, 9U> combining_glyphs{};
    std::array<unicode_grapheme_cluster, 1U> combining_graphemes{};
    std::array<shaping_attachment, 9U> combining_attachments{};
    std::array<std::uint8_t, 9U> combining_states{};
    options.cluster_level = shaping_cluster_level::monotone_characters;
    require(try_shape_open_type_run(
        font,
        combining_input,
        options,
        combining_glyphs,
        open_type_shape_run_scratch{
            combining_graphemes,
            {},
            {},
            combining_attachments,
            combining_states},
        glyph_count,
        &error));
    require(glyph_count == 3U);
    require(combining_glyphs[0].code_point == 0x0308U &&
        combining_glyphs[0].cluster == 0);
    require(combining_glyphs[1].code_point == 0x0301U &&
        combining_glyphs[1].cluster == 0);
    require(combining_glyphs[2].code_point == 0x41U &&
        combining_glyphs[2].cluster == 0);

    const auto horizontal_data = make_font();
    require(sfnt_font_view::try_create(horizontal_data, 0U, font, &error));
    require(font.try_get_design_advance_height(4U, value) && value == 1000);
    require(font.try_get_design_vertical_origin_y(4U, value) && value == 520);
}

void horizontal_font_metrics_match_managed_policy() {
    sfnt_font_view font{};
    font_error error = font_error::none;
    float advance = 0.0F;
    const auto normal_data = make_font();
    require(sfnt_font_view::try_create(normal_data, 0U, font, &error));
    require(font.try_get_design_advance_width(4U, {}, advance, &error));
    require(error == font_error::none && advance == 600.0F);

    table_data missing_metrics_header{
        open_type_tag::from_chars('h', 'h', 'e', 'a'),
        std::vector<std::byte>(36U)};
    write_i16(missing_metrics_header.bytes, 4U, 800);
    write_i16(missing_metrics_header.bytes, 6U, -200);
    const std::array<table_data, 1U> missing_metrics{
        missing_metrics_header};
    const auto fallback_data = make_font(
        0U, 22U, 0U, false, false, false, missing_metrics);
    require(sfnt_font_view::try_create(fallback_data, 0U, font, &error));
    require(font.try_get_design_advance_width(4U, {}, advance, &error));
    require(error == font_error::none && advance == 500.0F);

    constexpr std::array mappings{std::pair{0x41U, 4U}};
    const auto cmap = make_cmap_groups(mappings);
    const auto fallback_shape_data = make_font(
        0U,
        22U,
        0U,
        false,
        false,
        false,
        missing_metrics,
        cmap);
    require(sfnt_font_view::try_create(
        fallback_shape_data, 0U, font, &error));
    const std::array<unicode_scalar, 1U> input{
        unicode_scalar{0x41U, 0U, 1U}};
    open_type_shape_run_requirements requirements{};
    require(try_get_open_type_shape_run_requirements(
        font, input, requirements, &error));
    std::array<shaping_glyph, 3U> glyphs{};
    std::array<unicode_grapheme_cluster, 1U> graphemes{};
    std::array<shaping_attachment, 3U> attachments{};
    std::array<std::uint8_t, 3U> states{};
    std::uint32_t glyph_count = 0U;
    open_type_shape_run_options options{
        open_type_tag::from_chars('l', 'a', 't', 'n')};
    require(try_shape_open_type_run(
        font,
        input,
        options,
        glyphs,
        open_type_shape_run_scratch{
            graphemes, {}, {}, attachments, states},
        glyph_count,
        &error));
    require(glyph_count == 1U && glyphs[0U].advance_x == 500);
    options.direction = shaping_direction::top_to_bottom;
    require(try_shape_open_type_run(
        font,
        input,
        options,
        glyphs,
        open_type_shape_run_scratch{
            graphemes, {}, {}, attachments, states},
        glyph_count,
        &error));
    require(glyph_count == 1U && glyphs[0U].offset_x == -250);

    table_data truncated_hmtx{
        open_type_tag::from_chars('h', 'm', 't', 'x'),
        std::vector<std::byte>(1U)};
    const std::array<table_data, 1U> malformed_metrics{truncated_hmtx};
    const auto malformed_data = make_font(
        0U, 22U, 0U, false, false, false, malformed_metrics);
    require(sfnt_font_view::try_create(malformed_data, 0U, font, &error));
    advance = 99.0F;
    require(!font.try_get_design_advance_width(
        4U, {}, advance, &error));
    require(error == font_error::invalid_face && advance == 0.0F);
}

void fallback_mark_positioning_matches_managed_policy() {
    const auto data = make_font();
    sfnt_font_view font{};
    font_error error = font_error::none;
    require(sfnt_font_view::try_create(data, 0U, font, &error));

    std::array<shaping_glyph, 3U> glyphs{
        shaping_glyph{4U, 0x41U, 0, {}, 600, 0, 0, 0},
        shaping_glyph{4U, 0x0301U, 1, {}, 600, 0, 0, 0},
        shaping_glyph{4U, 0x0301U, 1, {}, 600, 0, 0, 0}};
    require(try_apply_fallback_mark_positioning(
        font, glyphs, shaping_direction::left_to_right, {}, {}, &error));
    require(error == font_error::none);
    require(glyphs[0U].advance_x == 600 && glyphs[0U].offset_x == 0);
    require(glyphs[1U].advance_x == 0 && glyphs[1U].advance_y == 0 &&
        glyphs[1U].offset_x == -320 && glyphs[1U].offset_y == 102);
    require(glyphs[2U].advance_x == 0 && glyphs[2U].advance_y == 0 &&
        glyphs[2U].offset_x == -320 && glyphs[2U].offset_y == 204);
    constexpr auto dependencies =
        static_cast<std::uint32_t>(shaping_glyph_flags::unsafe_to_break) |
        static_cast<std::uint32_t>(shaping_glyph_flags::unsafe_to_concat);
    require((static_cast<std::uint32_t>(glyphs[1U].flags) & dependencies) ==
        dependencies);

    glyphs = {
        shaping_glyph{4U, 0x41U, 0, {}, 600, 0, 0, 0},
        shaping_glyph{4U, 0x0301U, 1, {}, 600, 0, 0, 0},
        shaping_glyph{4U, 0x0301U, 1, {}, 600, 0, 0, 0}};
    fallback_mark_positioning_scratch fallback_scratch{};
    require(try_apply_fallback_mark_positioning(
        font,
        glyphs,
        shaping_direction::left_to_right,
        {},
        {},
        fallback_scratch,
        &error));
    require(error == font_error::none &&
        glyphs[1U].offset_x == -320 && glyphs[1U].offset_y == 102 &&
        glyphs[2U].offset_x == -320 && glyphs[2U].offset_y == 204);

    glyphs = {
        shaping_glyph{4U, 0x41U, 0, {}, 600, 0, 0, 0},
        shaping_glyph{4U, 0x0301U, 0, {}, 0, 0, 7, 9},
        shaping_glyph{4U, 0x0301U, 0, {}, 0, 0, 0, 0}};
    const std::array<fallback_mark_metadata, 3U> positioned{
        fallback_mark_metadata{},
        fallback_mark_metadata{0U, 0xFFU, true},
        fallback_mark_metadata{}};
    require(try_apply_fallback_mark_positioning(
        font,
        glyphs,
        shaping_direction::left_to_right,
        positioned,
        {},
        &error));
    require(glyphs[1U].offset_x == 7 && glyphs[1U].offset_y == 9);
    require(glyphs[2U].offset_x == -320 && glyphs[2U].offset_y == 102);

    glyphs = {
        shaping_glyph{4U, 0x41U, 0, {}, 600, 0, 0, 0},
        shaping_glyph{4U, 0x0301U, 0, {}, 0, 0, 0, 0},
        shaping_glyph{4U, 0x0301U, 0, {}, 0, 0, 0, 0}};
    const std::array<fallback_mark_metadata, 3U> ligature_components{
        fallback_mark_metadata{2U, 0xFFU, false},
        fallback_mark_metadata{0U, 0U, false},
        fallback_mark_metadata{0U, 1U, false}};
    require(try_apply_fallback_mark_positioning(
        font,
        glyphs,
        shaping_direction::left_to_right,
        ligature_components,
        {},
        &error));
    require(glyphs[1U].offset_x == -470 && glyphs[1U].offset_y == 102);
    require(glyphs[2U].offset_x == -170 && glyphs[2U].offset_y == 102);

    require(!try_apply_fallback_mark_positioning(
        font,
        glyphs,
        shaping_direction::left_to_right,
        std::span<const fallback_mark_metadata>{ligature_components}.first(2U),
        {},
        &error));
    require(error == font_error::invalid_argument &&
        glyphs[1U].offset_x == -470 && glyphs[1U].offset_y == 102 &&
        glyphs[2U].offset_x == -170 && glyphs[2U].offset_y == 102);

    constexpr std::array mappings{
        std::pair{0x41U, 4U}, std::pair{0x0301U, 4U}};
    const auto cmap = make_cmap_groups(mappings);
    const auto shaping_data = make_font(
        0U, 22U, 0U, false, false, false, {}, cmap);
    require(sfnt_font_view::try_create(shaping_data, 0U, font, &error));
    const std::array<unicode_scalar, 2U> input{
        unicode_scalar{0x41U, 0U, 1U},
        unicode_scalar{0x0301U, 1U, 1U}};
    open_type_shape_run_requirements requirements{};
    require(try_get_open_type_shape_run_requirements(
        font, input, requirements, &error));
    std::array<shaping_glyph, 6U> shaped{};
    std::array<unicode_grapheme_cluster, 2U> graphemes{};
    std::array<shaping_attachment, 6U> attachments{};
    std::array<std::uint8_t, 6U> states{};
    std::uint32_t glyph_count = 0U;
    const open_type_shape_run_options options{
        open_type_tag::from_chars('l', 'a', 't', 'n')};
    fallback_mark_positioning_scratch full_run_fallback_scratch{};
    open_type_shape_run_scratch full_run_scratch{};
    full_run_scratch.grapheme_clusters = graphemes;
    full_run_scratch.attachments = attachments;
    full_run_scratch.attachment_states = states;
    full_run_scratch.fallback_marks = &full_run_fallback_scratch;
    require(try_shape_open_type_run(
        font,
        input,
        options,
        shaped,
        full_run_scratch,
        glyph_count,
        &error));
    require(glyph_count == 2U && shaped[0U].advance_x == 600 &&
        shaped[1U].advance_x == 0 && shaped[1U].advance_y == 0 &&
        shaped[1U].offset_x == -320 && shaped[1U].offset_y == 102);
}

void arabic_stretch_matches_managed_bounded_expansion() {
    const auto data = make_font();
    sfnt_font_view font{};
    font_error error = font_error::none;
    require(sfnt_font_view::try_create(data, 0U, font, &error));

    std::array<shaping_glyph, 6U> storage{
        shaping_glyph{4U, 0x0628U, 0, {}, 600, 0, 0, 0},
        shaping_glyph{4U, 0x0628U, 1, {}, 600, 0, 0, 0},
        shaping_glyph{4U, 0x0628U, 2, {}, 600, 0, 0, 0},
        shaping_glyph{4U, 0x0640U, 3, {}, 600, 0, 0, 0}};
    constexpr std::array actions{
        open_type_arabic_action::none,
        open_type_arabic_action::none,
        open_type_arabic_action::none,
        open_type_arabic_action::stretch_repeating};
    arabic_stretch_requirements requirements{};
    require(try_get_arabic_stretch_requirements(
        font,
        std::span<const shaping_glyph>{storage}.first(4U),
        actions,
        true,
        {},
        requirements,
        &error));
    require(error == font_error::none &&
        requirements.glyph_capacity == 6U &&
        requirements.run_capacity == 1U);

    auto insufficient = storage;
    std::uint32_t insufficient_count = 4U;
    std::array<arabic_stretch_run, 1U> runs{};
    require(!try_apply_arabic_stretch(
        font,
        std::span<shaping_glyph>{insufficient}.first(5U),
        insufficient_count,
        actions,
        true,
        {},
        runs,
        &error));
    require(error == font_error::insufficient_buffer &&
        insufficient_count == 4U && insufficient[3U].advance_x == 600);

    std::uint32_t count = 4U;
    require(try_apply_arabic_stretch(
        font, storage, count, actions, true, {}, runs, &error));
    require(error == font_error::none && count == 6U &&
        storage[3U].advance_x == 0 && storage[3U].offset_x == -900 &&
        storage[4U].advance_x == 0 && storage[4U].offset_x == -300 &&
        storage[5U].advance_x == 0 && storage[5U].offset_x == 300);
    constexpr auto unsafe =
        static_cast<std::uint32_t>(shaping_glyph_flags::unsafe_to_break) |
        static_cast<std::uint32_t>(shaping_glyph_flags::unsafe_to_concat);
    require((static_cast<std::uint32_t>(storage[0U].flags) & unsafe) ==
        unsafe);

    const auto make_stretch_gsub = [] {
        std::vector<std::byte> table(76U);
        write_u16(table, 0U, 1U);
        write_u16(table, 4U, 10U);
        write_u16(table, 6U, 30U);
        write_u16(table, 8U, 44U);
        write_u16(table, 10U, 1U);
        write_u32(table, 12U,
            open_type_tag::from_chars('a', 'r', 'a', 'b').value);
        write_u16(table, 16U, 8U);
        write_u16(table, 18U, 4U);
        write_u16(table, 22U, 0U);
        write_u16(table, 24U, 0xFFFFU);
        write_u16(table, 26U, 1U);
        write_u16(table, 28U, 0U);
        write_u16(table, 30U, 1U);
        write_u32(table, 32U,
            open_type_tag::from_chars('s', 't', 'c', 'h').value);
        write_u16(table, 36U, 8U);
        write_u16(table, 40U, 1U);
        write_u16(table, 42U, 0U);
        write_u16(table, 44U, 1U);
        write_u16(table, 46U, 4U);
        write_u16(table, 48U, 2U);
        write_u16(table, 50U, 0U);
        write_u16(table, 52U, 1U);
        write_u16(table, 54U, 8U);
        write_u16(table, 56U, 1U);
        write_u16(table, 58U, 8U);
        write_u16(table, 60U, 1U);
        write_u16(table, 62U, 14U);
        write_u16(table, 64U, 1U);
        write_u16(table, 66U, 1U);
        write_u16(table, 68U, 5U);
        write_u16(table, 70U, 2U);
        write_u16(table, 72U, 6U);
        write_u16(table, 74U, 7U);
        return table;
    };
    constexpr std::array mappings{
        std::pair{0x0628U, 4U}, std::pair{0x0640U, 5U}};
    const auto cmap = make_cmap_groups(mappings);
    const std::array stretch_tables{
        table_data{open_type_tag::from_chars('G', 'S', 'U', 'B'),
            make_stretch_gsub()}};
    const auto stretch_font_data = make_font(
        0U, 22U, 0U, false, false, false, stretch_tables, cmap);
    require(sfnt_font_view::try_create(
        stretch_font_data, 0U, font, &error));
    constexpr std::array stretch_input{
        unicode_scalar{0x0640U, 0U, 1U},
        unicode_scalar{0x0628U, 1U, 1U},
        unicode_scalar{0x0628U, 2U, 1U},
        unicode_scalar{0x0628U, 3U, 1U}};
    constexpr std::array stretch_features{
        open_type_tag::from_chars('s', 't', 'c', 'h')};
    std::array<shaping_glyph, 12U> shaped{};
    std::array<unicode_grapheme_cluster, 4U> graphemes{};
    std::array<std::uint16_t, 1U> gsub_lookups{};
    std::array<shaping_attachment, 12U> attachments{};
    std::array<std::uint8_t, 12U> states{};
    std::array<open_type_arabic_action, 4U> joining_actions{};
    std::array<shaping_glyph_flags, 4U> joining_flags{};
    open_type_shape_run_scratch shape_scratch{};
    shape_scratch.grapheme_clusters = graphemes;
    shape_scratch.gsub_lookups = gsub_lookups;
    shape_scratch.attachments = attachments;
    shape_scratch.attachment_states = states;
    shape_scratch.arabic_actions = joining_actions;
    shape_scratch.arabic_flags = joining_flags;
    shape_scratch.arabic_stretch_runs = runs;
    const open_type_shape_run_options stretch_options{
        open_type_tag::from_chars('a', 'r', 'a', 'b'),
        {},
        shaping_direction::right_to_left,
        stretch_features};
    count = 0U;
    require(try_shape_open_type_run(
        font,
        stretch_input,
        stretch_options,
        shaped,
        shape_scratch,
        count,
        &error));
    std::uint32_t stretched_glyphs = 0U;
    for (std::uint32_t index = 0U; index < count; ++index) {
        if (shaped[index].code_point == 0x0640U) ++stretched_glyphs;
        require((static_cast<std::uint32_t>(shaped[index].flags) &
            0xFFF80000U) == 0U);
    }
    require(error == font_error::none && count == 6U &&
        stretched_glyphs == 3U);
}

void legacy_kern_shaping_matches_managed_policy() {
    constexpr auto kern = open_type_tag::from_chars('k', 'e', 'r', 'n');
    const auto make_format_zero = [](
        bool apple,
        bool cross_stream,
        std::uint16_t left,
        std::uint16_t right,
        std::int16_t value) {
        const std::size_t table_header = apple ? 8U : 4U;
        const std::size_t subtable_header = apple ? 8U : 6U;
        const std::size_t subtable_length = subtable_header + 8U + 6U;
        std::vector<std::byte> result(table_header + subtable_length);
        if (apple) {
            write_u32(result, 0U, 0x00010000U);
            write_u32(result, 4U, 1U);
            write_u32(result, 8U,
                static_cast<std::uint32_t>(subtable_length));
            result[12U] = static_cast<std::byte>(
                cross_stream ? 0x40U : 0U);
            result[13U] = std::byte{0U};
        } else {
            write_u16(result, 0U, 0U);
            write_u16(result, 2U, 1U);
            write_u16(result, 4U, 0U);
            write_u16(result, 6U,
                static_cast<std::uint16_t>(subtable_length));
            write_u16(result, 8U,
                static_cast<std::uint16_t>(cross_stream ? 0x0005U : 1U));
        }
        const auto body = table_header + subtable_header;
        write_u16(result, body, 1U);
        const auto record = body + 8U;
        write_u32(result, record,
            (static_cast<std::uint32_t>(left) << 16U) | right);
        write_i16(result, record + 4U, value);
        return result;
    };
    const auto make_format_two = [](
        std::uint16_t left,
        std::uint16_t right,
        std::int16_t value) {
        constexpr std::size_t subtable = 4U;
        constexpr std::size_t subtable_length = 36U;
        std::vector<std::byte> result(subtable + subtable_length);
        write_u16(result, 0U, 0U);
        write_u16(result, 2U, 1U);
        write_u16(result, subtable, 0U);
        write_u16(result, subtable + 2U,
            static_cast<std::uint16_t>(subtable_length));
        write_u16(result, subtable + 4U, 0x0201U);
        write_u16(result, subtable + 6U, 4U);
        write_u16(result, subtable + 8U, 14U);
        write_u16(result, subtable + 10U, 22U);
        write_u16(result, subtable + 12U, 30U);
        write_u16(result, subtable + 14U, left);
        write_u16(result, subtable + 16U, 1U);
        write_u16(result, subtable + 18U, 30U);
        write_u16(result, subtable + 22U, right);
        write_u16(result, subtable + 24U, 1U);
        write_u16(result, subtable + 26U, 2U);
        write_i16(result, subtable + 32U, value);
        return result;
    };
    const auto make_gdef_mark = [](std::uint16_t glyph) {
        std::vector<std::byte> result(20U);
        write_u16(result, 0U, 1U);
        write_u16(result, 2U, 0U);
        write_u16(result, 4U, 12U);
        write_u16(result, 12U, 1U);
        write_u16(result, 14U, glyph);
        write_u16(result, 16U, 1U);
        write_u16(result, 18U, 3U);
        return result;
    };
    const auto make_gpos_kern = [](
        std::uint16_t left,
        std::uint16_t right,
        std::int16_t adjustment,
        bool required = false) {
        std::vector<std::byte> result(88U);
        write_u16(result, 0U, 1U);
        write_u16(result, 2U, 0U);
        write_u16(result, 4U, 10U);
        write_u16(result, 6U, 30U);
        write_u16(result, 8U, 44U);

        write_u16(result, 10U, 1U);
        write_u32(result, 12U,
            open_type_tag::from_chars('l', 'a', 't', 'n').value);
        write_u16(result, 16U, 8U);
        write_u16(result, 18U, 4U);
        write_u16(result, 20U, 0U);
        write_u16(result, 22U, 0U);
        write_u16(result, 24U, required ? 0U : 0xFFFFU);
        write_u16(result, 26U, required ? 0U : 1U);
        write_u16(result, 28U, 0U);

        write_u16(result, 30U, 1U);
        write_u32(result, 32U, kern.value);
        write_u16(result, 36U, 8U);
        write_u16(result, 38U, 0U);
        write_u16(result, 40U, 1U);
        write_u16(result, 42U, 0U);

        write_u16(result, 44U, 1U);
        write_u16(result, 46U, 4U);
        write_u16(result, 48U, 2U);
        write_u16(result, 50U, 0U);
        write_u16(result, 52U, 1U);
        write_u16(result, 54U, 8U);

        constexpr std::size_t pair = 56U;
        write_u16(result, pair, 1U);
        write_u16(result, pair + 2U, 18U);
        write_u16(result, pair + 4U, 0x0004U);
        write_u16(result, pair + 6U, 0x0004U);
        write_u16(result, pair + 8U, 1U);
        write_u16(result, pair + 10U, 24U);
        write_u16(result, pair + 18U, 1U);
        write_u16(result, pair + 20U, 1U);
        write_u16(result, pair + 22U, left);
        write_u16(result, pair + 24U, 1U);
        write_u16(result, pair + 26U, right);
        write_i16(result, pair + 28U, adjustment);
        write_i16(result, pair + 30U, adjustment);
        return result;
    };
    const auto shape = [](
        const sfnt_font_view& font,
        std::span<const unicode_scalar> input,
        const open_type_shape_run_options& options,
        std::span<shaping_glyph> glyphs) {
        open_type_shape_run_requirements requirements{};
        font_error error = font_error::none;
        require(try_get_open_type_shape_run_requirements(
            font, input, requirements, &error));
        std::array<unicode_grapheme_cluster, 4U> graphemes{};
        std::array<std::uint16_t, 4U> gsub{};
        std::array<std::uint16_t, 4U> gpos{};
        std::array<shaping_attachment, 12U> attachments{};
        std::array<std::uint8_t, 12U> states{};
        std::uint32_t glyph_count = 0U;
        require(try_shape_open_type_run(
            font,
            input,
            options,
            glyphs,
            open_type_shape_run_scratch{
                graphemes, gsub, gpos, attachments, states},
            glyph_count,
            &error));
        return glyph_count;
    };

    constexpr std::array mappings{
        std::pair{0x41U, 4U},
        std::pair{0x42U, 5U},
        std::pair{0x43U, 6U}};
    const auto cmap = make_cmap_groups(mappings);
    constexpr std::array requested{kern};
    open_type_shape_run_options options{
        open_type_tag::from_chars('l', 'a', 't', 'n')};
    options.requested_features = requested;
    constexpr std::array<unicode_scalar, 2U> pair_input{
        unicode_scalar{0x41U, 0U, 1U},
        unicode_scalar{0x42U, 1U, 1U}};
    std::array<shaping_glyph, 12U> glyphs{};
    sfnt_font_view font{};
    font_error error = font_error::none;

    table_data windows_kern{kern, make_format_zero(false, false, 4U, 5U, -101)};
    const std::array<table_data, 1U> windows_tables{windows_kern};
    const auto windows_data = make_font(
        0U, 22U, 0U, false, false, false, windows_tables, cmap);
    require(sfnt_font_view::try_create(windows_data, 0U, font, &error));
    std::int32_t design_kerning = 0;
    require(font.try_get_design_kerning(0x41U, 0x42U, design_kerning) &&
        design_kerning == -101);
    require(shape(font, pair_input, options, glyphs) == 2U);
    require(glyphs[0U].advance_x == 549 &&
        glyphs[1U].advance_x == 550 && glyphs[1U].offset_x == -50);
    require((static_cast<std::uint32_t>(glyphs[1U].flags) &
        static_cast<std::uint32_t>(shaping_glyph_flags::unsafe_to_break)) !=
        0U);

    table_data gpos{open_type_tag::from_chars('G', 'P', 'O', 'S'),
        make_gpos_kern(4U, 5U, -20)};
    const std::array<table_data, 2U> gpos_tables{windows_kern, gpos};
    const auto gpos_data = make_font(
        0U, 22U, 0U, false, false, false, gpos_tables, cmap);
    require(sfnt_font_view::try_create(gpos_data, 0U, font, &error));
    require(shape(font, pair_input, options, glyphs) == 2U);
    require(glyphs[0U].advance_x == 580 &&
        glyphs[1U].advance_x == 580 && glyphs[1U].offset_x == 0);

    table_data required_gpos{open_type_tag::from_chars('G', 'P', 'O', 'S'),
        make_gpos_kern(4U, 5U, -25, true)};
    const std::array<table_data, 2U> required_tables{
        windows_kern, required_gpos};
    const auto required_data = make_font(
        0U, 22U, 0U, false, false, false, required_tables, cmap);
    require(sfnt_font_view::try_create(required_data, 0U, font, &error));
    require(shape(font, pair_input, options, glyphs) == 2U);
    require(glyphs[0U].advance_x == 575 &&
        glyphs[1U].advance_x == 575 && glyphs[1U].offset_x == 0);

    constexpr std::array disabled_setting{
        shaping_feature{kern, 0U, 0U, 0xFFFFFFFFU}};
    options.feature_settings = disabled_setting;
    require(sfnt_font_view::try_create(windows_data, 0U, font, &error));
    require(shape(font, pair_input, options, glyphs) == 2U);
    require(glyphs[0U].advance_x == 600 &&
        glyphs[1U].advance_x == 600 && glyphs[1U].offset_x == 0);
    options.feature_settings = {};

    table_data apple_kern{kern, make_format_zero(true, true, 4U, 5U, -70)};
    const std::array<table_data, 1U> apple_tables{apple_kern};
    const auto apple_data = make_font(
        0U, 22U, 0U, false, false, false, apple_tables, cmap);
    require(sfnt_font_view::try_create(apple_data, 0U, font, &error));
    require(font.try_get_design_kerning(0x41U, 0x42U, design_kerning) &&
        design_kerning == 0);
    require(shape(font, pair_input, options, glyphs) == 2U);
    require(glyphs[0U].advance_x == 600 &&
        glyphs[1U].advance_x == 600 && glyphs[1U].offset_y == -70);

    table_data class_kern{kern, make_format_two(4U, 5U, -80)};
    const std::array<table_data, 1U> class_tables{class_kern};
    const auto class_data = make_font(
        0U, 22U, 0U, false, false, false, class_tables, cmap);
    require(sfnt_font_view::try_create(class_data, 0U, font, &error));
    require(font.try_get_design_kerning(0x41U, 0x42U, design_kerning) &&
        design_kerning == 0);
    require(shape(font, pair_input, options, glyphs) == 2U);
    require(glyphs[0U].advance_x == 560 &&
        glyphs[1U].advance_x == 560 && glyphs[1U].offset_x == -40);

    table_data skip_kern{kern, make_format_zero(false, false, 4U, 6U, -60)};
    table_data gdef{open_type_tag::from_chars('G', 'D', 'E', 'F'),
        make_gdef_mark(5U)};
    const std::array<table_data, 2U> skip_tables{skip_kern, gdef};
    const auto skip_data = make_font(
        0U, 22U, 0U, false, false, false, skip_tables, cmap);
    require(sfnt_font_view::try_create(skip_data, 0U, font, &error));
    constexpr std::array<unicode_scalar, 3U> skip_input{
        unicode_scalar{0x41U, 0U, 1U},
        unicode_scalar{0x42U, 1U, 1U},
        unicode_scalar{0x43U, 2U, 1U}};
    require(shape(font, skip_input, options, glyphs) == 3U);
    require(glyphs[0U].advance_x == 570 && glyphs[1U].advance_x == 0 &&
        glyphs[2U].advance_x == 570 && glyphs[2U].offset_x == -30);
}

void open_type_feature_tags_match_managed_sorted_union() {
    const auto make_layout = [](
        std::span<const open_type_tag> tags,
        std::uint16_t declared_count) {
        std::vector<std::byte> result(12U + tags.size() * 6U);
        write_u16(result, 0U, 1U);
        write_u16(result, 2U, 0U);
        write_u16(result, 6U, 10U);
        write_u16(result, 10U, declared_count);
        for (std::size_t index = 0U; index < tags.size(); ++index) {
            const auto record = 12U + index * 6U;
            write_u32(result, record, tags[index].value);
            write_u16(result, record + 4U, 0U);
        }
        return result;
    };
    constexpr auto kern = open_type_tag::from_chars('k', 'e', 'r', 'n');
    constexpr auto liga = open_type_tag::from_chars('l', 'i', 'g', 'a');
    constexpr auto mark = open_type_tag::from_chars('m', 'a', 'r', 'k');
    constexpr std::array gsub_tags{liga, kern};
    constexpr std::array gpos_tags{mark, kern};
    const std::array tables{
        table_data{open_type_tag::from_chars('G', 'S', 'U', 'B'),
            make_layout(gsub_tags, 2U)},
        table_data{open_type_tag::from_chars('G', 'P', 'O', 'S'),
            make_layout(gpos_tags, 4U)}};
    const auto data = make_font(
        0U, 22U, 0U, false, false, false, tables);
    sfnt_font_view font{};
    font_error error = font_error::invalid_argument;
    require(sfnt_font_view::try_create(data, 0U, font, &error));
    open_type_feature_tag_requirements requirements{};
    require(try_get_open_type_feature_tag_requirements(
        font, requirements, &error));
    require(error == font_error::none && requirements.tag_capacity == 3U);
    std::array<open_type_tag, 3U> output{};
    std::uint32_t written = 99U;
    require(try_decode_open_type_feature_tags(
        font, output, written, &error));
    require(written == 3U && output[0U] == kern && output[1U] == liga &&
        output[2U] == mark);

    std::array<open_type_tag, 2U> short_output{
        open_type_tag{0x11111111U}, open_type_tag{0x22222222U}};
    written = 99U;
    require(!try_decode_open_type_feature_tags(
        font, short_output, written, &error));
    require(error == font_error::insufficient_buffer && written == 0U &&
        short_output[0U].value == 0x11111111U &&
        short_output[1U].value == 0x22222222U);

    const auto empty_data = make_font();
    require(sfnt_font_view::try_create(empty_data, 0U, font, &error));
    require(try_get_open_type_feature_tag_requirements(
        font, requirements, &error));
    require(requirements.tag_capacity == 0U);
    written = 99U;
    require(try_decode_open_type_feature_tags(
        font, {}, written, &error));
    require(written == 0U && error == font_error::none);

    std::ifstream stream(PROGPU_NATIVE_TEST_INTER_FONT, std::ios::binary);
    require(stream.good());
    const std::vector<char> source{
        std::istreambuf_iterator<char>(stream),
        std::istreambuf_iterator<char>()};
    std::vector<std::byte> inter_data(source.size());
    for (std::size_t index = 0U; index < source.size(); ++index) {
        inter_data[index] = static_cast<std::byte>(source[index]);
    }
    require(sfnt_font_view::try_create(inter_data, 0U, font, &error));
    require(try_get_open_type_feature_tag_requirements(
        font, requirements, &error));
    std::vector<open_type_tag> inter_tags(requirements.tag_capacity);
    require(try_decode_open_type_feature_tags(
        font, inter_tags, written, &error));
    require(written == inter_tags.size() &&
        std::is_sorted(
            inter_tags.begin(), inter_tags.end(),
            [](open_type_tag left, open_type_tag right) {
                return left.value < right.value;
            }));
    constexpr std::array managed_documented{
        open_type_tag::from_chars('a', 'a', 'l', 't'),
        open_type_tag::from_chars('c', 'a', 'l', 't'),
        open_type_tag::from_chars('c', 'c', 'm', 'p'),
        open_type_tag::from_chars('c', 'v', '1', '4'),
        open_type_tag::from_chars('f', 'r', 'a', 'c'),
        open_type_tag::from_chars('k', 'e', 'r', 'n'),
        open_type_tag::from_chars('m', 'a', 'r', 'k'),
        open_type_tag::from_chars('m', 'k', 'm', 'k'),
        open_type_tag::from_chars('s', 's', '0', '8'),
        open_type_tag::from_chars('z', 'e', 'r', 'o')};
    for (const auto expected : managed_documented) {
        require(std::binary_search(
            inter_tags.begin(), inter_tags.end(), expected,
            [](open_type_tag left, open_type_tag right) {
                return left.value < right.value;
            }));
    }
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

    sfnt_glyph_resident_requirements resident_requirements{};
    require(font.try_get_glyph_resident_requirements(
        2U, resident_requirements, &error));
    require(error == font_error::none &&
        resident_requirements.strike_count == 2U &&
        resident_requirements.sbix_bytes == 118U &&
        resident_requirements.font_bytes > resident_requirements.sbix_bytes);
    std::vector<std::byte> short_font(
        resident_requirements.font_bytes - 1U, std::byte{0xA5U});
    std::size_t written = 99U;
    require(!font.try_create_glyph_resident_font(
        2U, short_font, written, nullptr, &error));
    require(error == font_error::insufficient_buffer && written == 0U &&
        short_font.front() == std::byte{0xA5U});

    std::vector<std::byte> resident_font(resident_requirements.font_bytes);
    require(font.try_create_glyph_resident_font(
        2U, resident_font, written, &resident_requirements, &error));
    require(written == resident_font.size());
    sfnt_font_view resident{};
    require(sfnt_font_view::try_create(
        resident_font, 0U, resident, &error));
    require(resident.try_get_sbix_glyph(2U, 19.0F, glyph, &error));
    require(glyph.pixels_per_em == 20U &&
        glyph.origin_offset_x == 7 && glyph.origin_offset_y == 8 &&
        glyph.bytes.size() == 3U && glyph.bytes[0U] == std::byte{20U});
    require(!resident.try_get_sbix_glyph(1U, 20.0F, glyph, &error));

    std::vector<std::byte> resident_sbix(resident_requirements.sbix_bytes);
    require(font.try_create_glyph_resident_sbix(
        2U, resident_sbix, written, nullptr, &error));
    require(written == resident_sbix.size() &&
        resident_sbix[7U] == std::byte{2U} &&
        resident_sbix[11U] == std::byte{16U});
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

    constexpr std::string_view decimal_exponent_path =
        "M.5 -.25 L1e2 2E-1";
    require(try_get_svg_path_requirements(
        decimal_exponent_path, requirements, &error));
    require(requirements.segment_count == 2U);
    std::array<progpu_native_path_segment, 2U> decimal_segments{};
    require(try_decode_svg_path(
        decimal_exponent_path, decimal_segments, requirements, &error));
    require(decimal_segments[0U].p0.x == 0.5F &&
        decimal_segments[0U].p0.y == -0.25F &&
        decimal_segments[0U].p1.x == 100.0F &&
        decimal_segments[0U].p1.y == 0.2F);

    constexpr std::string_view overflowing_number =
        "M0 0 L1e100 0";
    require(!try_get_svg_path_requirements(
        overflowing_number, requirements, &error));
    require(error == font_error::invalid_glyph);
}

void svg_glyph_layers_match_managed_shapes_references_and_paints() {
    constexpr std::string_view xml = R"svg(
        <?xml version="1.0"?>
        <svg xmlns="http://www.w3.org/2000/svg"
             fill="red" transform="translate(1 2)">
          <defs>
            <linearGradient id="gradient" gradientTransform="scale(2)"
                            spreadMethod="reflect">
              <stop offset="0%" stop-color="#000"/>
              <stop offset="100%" stop-color="#fff" stop-opacity="50%"/>
            </linearGradient>
            <radialGradient id="radial" cx="20" cy="20" r="5"
                            fx="18" fy="19" spreadMethod="repeat">
              <stop offset="0" stop-color="red"/>
              <stop offset="1" stop-color="blue"/>
            </radialGradient>
            <rect id="box" x="0" y="0" width="5" height="6"
                  fill="url(#gradient)"/>
          </defs>
          <g id="glyph7" opacity="50%" transform="translate(10 20)">
            <path d="M0 0L10 0L0 10Z" fill="#0f08"
                  fill-rule="evenodd"/>
            <use href="#box" x="3" y="4"/>
            <circle cx="20" cy="20" r="5" fill="url(#radial)"/>
            <polygon points="30,0 40,0 35,10" fill="currentColor"/>
          </g>
        </svg>)svg";

    svg_glyph_requirements requirements{};
    font_error error = font_error::none;
    require(try_get_svg_glyph_requirements(
        xml, 7U, 1000U, requirements, &error));
    require(error == font_error::none);
    require(requirements.layer_count == 4U);
    require(requirements.segment_count == 14U);
    require(requirements.brush_count == 4U);
    require(requirements.gradient_stop_count == 4U);

    std::vector<svg_glyph_layer> layers(requirements.layer_count);
    std::vector<progpu_native_path_segment> segments(
        requirements.segment_count);
    std::vector<progpu_native_scene_brush> brushes(
        requirements.brush_count);
    std::vector<progpu_native_scene_gradient_stop> stops(
        requirements.gradient_stop_count);
    require(try_decode_svg_glyph(
        xml, 7U, 1000U, layers, segments, brushes, stops,
        requirements, &error));
    require(error == font_error::none);
    require(layers[0U].segment_offset == 0U &&
        layers[0U].segment_count == 3U &&
        layers[0U].fill_rule == PROGPU_NATIVE_FILL_RULE_EVEN_ODD);
    require(layers[0U].transform.m31 == 11.0F &&
        layers[0U].transform.m32 == 22.0F);
    require(brushes[0U].type == PROGPU_NATIVE_SCENE_BRUSH_SOLID);
    require(std::abs(brushes[0U].colors[0U].g - 1.0F) < 0.0001F);
    require(std::abs(brushes[0U].colors[0U].a - (4.0F / 15.0F)) <
        0.0001F);

    require(layers[1U].segment_offset == 3U &&
        layers[1U].segment_count == 4U &&
        layers[1U].transform.m31 == 14.0F &&
        layers[1U].transform.m32 == 26.0F);
    require(brushes[1U].type ==
        PROGPU_NATIVE_SCENE_BRUSH_LINEAR_GRADIENT);
    require(brushes[1U].spread_method ==
        PROGPU_NATIVE_SCENE_GRADIENT_REFLECT);
    require(brushes[1U].stop_count == 2U &&
        brushes[1U].stop_offset == 0U);
    require(brushes[1U].start_point.x == 14.0F &&
        brushes[1U].start_point.y == 26.0F &&
        brushes[1U].end_point.x == 2014.0F &&
        brushes[1U].end_point.y == 26.0F);
    require(stops[0U].offset == 0.0F && stops[1U].offset == 1.0F);
    require(std::abs(stops[1U].color.a - 0.25F) < 0.0001F);

    require(layers[2U].segment_count == 4U &&
        segments[layers[2U].segment_offset].kind ==
            PROGPU_NATIVE_PATH_SEGMENT_ARC);
    require(brushes[2U].type ==
        PROGPU_NATIVE_SCENE_BRUSH_RADIAL_GRADIENT);
    require(brushes[2U].spread_method ==
        PROGPU_NATIVE_SCENE_GRADIENT_REPEAT);
    require(brushes[2U].stop_offset == 2U &&
        brushes[2U].stop_count == 2U);
    require(brushes[2U].center.x == 31.0F &&
        brushes[2U].center.y == 42.0F &&
        brushes[2U].start_point.x == 29.0F &&
        brushes[2U].start_point.y == 41.0F &&
        brushes[2U].radius == 5.0F && brushes[2U].radius_y == 5.0F);
    require(layers[3U].segment_count == 3U &&
        segments[layers[3U].segment_offset + 2U].p1.x == 30.0F);

    std::array<svg_glyph_layer, 3U> short_layers{};
    short_layers[0U].minimum_x = 123.0F;
    require(!try_decode_svg_glyph(
        xml, 7U, 1000U, short_layers, segments, brushes, stops,
        requirements, &error));
    require(error == font_error::insufficient_buffer &&
        requirements.layer_count == 4U &&
        short_layers[0U].minimum_x == 123.0F);

    constexpr std::string_view cyclic =
        "<svg><defs><g id='a'><use href='#a'/></g></defs>"
        "<g id='glyph1'><use href='#a'/></g></svg>";
    require(!try_get_svg_glyph_requirements(
        cyclic, 1U, 1000U, requirements, &error));
    require(error == font_error::invalid_glyph);
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

    constexpr std::array<sfnt_color_palette_override, 4U> overrides{{
        {0U, 0xFF010203U},
        {9U, 0xFFFFFFFFU},
        {1U, 0xFF112233U},
        {0U, 0x80402010U}}};
    require(font.try_decode_colr_layers(
        1U, 1U, overrides, layers, written, &error));
    require(written == 3U &&
        layers[0U].color.red == 0x40U &&
        layers[0U].color.green == 0x20U &&
        layers[0U].color.blue == 0x10U &&
        layers[0U].color.alpha == 0x80U &&
        layers[1U].color.red == 0x11U &&
        layers[1U].color.green == 0x22U &&
        layers[1U].color.blue == 0x33U &&
        layers[1U].color.alpha == 0xFFU &&
        layers[2U].uses_foreground_color &&
        layers[2U].color.red == 255U);

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
        1U, 0U, overrides, layers, written, &error));
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
        sfnt_glyph_outline_bounds_requirements bounds_requirements{};
        require(font.try_get_outline_bounds_requirements(
            glyph, {}, bounds_requirements, &error));
        require(bounds_requirements.source == sfnt_glyph_outline_source::cff1 &&
            bounds_requirements.path_segment_count == segments.size());
        sfnt_glyph_bounds bounds{};
        bool has_bounds = false;
        require(font.try_get_outline_bounds(
            glyph,
            {},
            sfnt_glyph_outline_bounds_scratch{{}, {}, segments},
            bounds,
            has_bounds,
            &error));
        require(has_bounds && bounds.x_max > bounds.x_min &&
            bounds.y_max > bounds.y_min);
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
    sfnt_glyph_outline_bounds_requirements bounds_requirements{};
    require(font.try_get_outline_bounds_requirements(
        glyph_index, {}, bounds_requirements));
    require(bounds_requirements.source ==
        sfnt_glyph_outline_source::true_type_static);
    sfnt_glyph_bounds bounds{};
    bool has_bounds = false;
    require(font.try_get_outline_bounds(
        glyph_index, {}, {}, bounds, has_bounds));
    require(has_bounds && bounds.x_min == 106 && bounds.y_min == -25 &&
        bounds.x_max == 1217 && bounds.y_max == 1510);
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
    font_style_variation_requirements style_requirements{};
    require(try_get_font_style_variation_requirements(
        font,
        font_style_request{700, 5, font_provider_slant::normal},
        style_requirements));
    require(style_requirements.setting_count == 1U);
    std::array<font_style_variation, 1U> style_setting{};
    require(try_resolve_font_style_variations(
        font,
        font_style_request{700, 5, font_provider_slant::normal},
        style_setting,
        written));
    require(written == 1U && style_setting[0].tag == axes[1].tag &&
        style_setting[0].user_fixed == 700 * 65536 &&
        style_setting[0].normalized == 8847 &&
        style_setting[0].axis_index == 1U);

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
    sfnt_horizontal_glyph_metrics horizontal_metrics{};
    require(font.try_get_horizontal_glyph_metrics(
        397U, horizontal_metrics));
    float varied_advance = 0.0F;
    require(font.try_get_design_advance_width(
        397U, optical_coordinates, varied_advance));
    require(varied_advance ==
        static_cast<float>(horizontal_metrics.advance_width) - 28.0F);
    sfnt_design_advance_width_requirements advance_requirements{};
    require(font.try_get_design_advance_width_requirements(
        397U, optical_coordinates, advance_requirements));
    require(advance_requirements.glyph_variation_item_count == 0U &&
        advance_requirements.phantom.tuple_header_count == 0U);
    varied_advance = 0.0F;
    require(font.try_get_design_advance_width(
        397U, optical_coordinates, varied_advance, {}));
    require(varied_advance ==
        static_cast<float>(horizontal_metrics.advance_width) - 28.0F);
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
    sfnt_glyph_outline_bounds_requirements bounds_requirements{};
    require(font.try_get_outline_bounds_requirements(
        397U, optical_coordinates, bounds_requirements));
    require(bounds_requirements.source ==
            sfnt_glyph_outline_source::true_type_varied &&
        bounds_requirements.point_count == outline_requirements.point_count &&
        bounds_requirements.path_segment_count == varied_segments.size());
    std::vector<progpu_native_point> bounds_points(
        bounds_requirements.point_count);
    sfnt_glyph_bounds varied_bounds{};
    bool has_varied_bounds = false;
    require(font.try_get_outline_bounds(
        397U,
        optical_coordinates,
        sfnt_glyph_outline_bounds_scratch{
            sfnt_varied_glyph_scratch{
                contour_ends,
                original_points,
                varied_points,
                {},
                variation_scratch,
                {}},
            bounds_points,
            varied_segments},
        varied_bounds,
        has_varied_bounds));
    require(has_varied_bounds && varied_bounds.x_max > varied_bounds.x_min &&
        varied_bounds.y_max > varied_bounds.y_min);

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
        plan_requirements.gpos_lookup_capacity <= 128U &&
        plan_requirements.gsub_accelerator_capacity ==
            plan_requirements.gsub_lookup_capacity &&
        plan_requirements.gpos_accelerator_capacity ==
            plan_requirements.gpos_lookup_capacity);

    std::array<std::uint16_t, 128U> plan_gsub{};
    std::array<std::uint16_t, 128U> plan_gpos{};
    std::array<open_type_lookup_accelerator, 128U> plan_gsub_accelerators{};
    std::array<open_type_lookup_accelerator, 128U> plan_gpos_accelerators{};
    std::vector<open_type_context_subtable_requirement>
        plan_gsub_context_subtables(
            plan_requirements.gsub_context_subtable_capacity);
    std::vector<open_type_context_coverage_requirement>
        plan_gsub_context_coverages(
            plan_requirements.gsub_context_coverage_capacity);
    std::vector<open_type_context_subtable_requirement>
        plan_gpos_context_subtables(
            plan_requirements.gpos_context_subtable_capacity);
    std::vector<open_type_context_coverage_requirement>
        plan_gpos_context_coverages(
            plan_requirements.gpos_context_coverage_capacity);
    open_type_shape_plan plan{};
    require(try_build_open_type_shape_plan(
        font,
        options,
        std::span<std::uint16_t>(plan_gsub).first(
            plan_requirements.gsub_lookup_capacity),
        std::span<std::uint16_t>(plan_gpos).first(
            plan_requirements.gpos_lookup_capacity),
        std::span<open_type_lookup_accelerator>(plan_gsub_accelerators).first(
            plan_requirements.gsub_accelerator_capacity),
        std::span<open_type_lookup_accelerator>(plan_gpos_accelerators).first(
            plan_requirements.gpos_accelerator_capacity),
        plan_gsub_context_subtables,
        plan_gsub_context_coverages,
        plan_gpos_context_subtables,
        plan_gpos_context_coverages,
        plan,
        &error));
    require(plan.gsub_accelerators.size() == plan.gsub_lookups.size() &&
        plan.gpos_accelerators.size() == plan.gpos_lookups.size() &&
        plan.gsub_context_subtables.size() <=
            plan_requirements.gsub_context_subtable_capacity &&
        plan.gsub_context_coverages.size() <=
            plan_requirements.gsub_context_coverage_capacity &&
        plan.gpos_context_subtables.size() <=
            plan_requirements.gpos_context_subtable_capacity &&
        plan.gpos_context_coverages.size() <=
            plan_requirements.gpos_context_coverage_capacity);
    require(std::any_of(
        plan.gsub_accelerators.begin(),
        plan.gsub_accelerators.end(),
        [](const auto& accelerator) { return accelerator.has_coverage; }));
    require(std::any_of(
        plan.gsub_accelerators.begin(),
        plan.gsub_accelerators.end(),
        [](const auto& accelerator) { return accelerator.has_context; }));
    constexpr std::array explicit_fraction{
        open_type_tag::from_chars('f', 'r', 'a', 'c')};
    auto explicit_fraction_options = options;
    explicit_fraction_options.explicit_features = explicit_fraction;
    require(!plan.matches(font, explicit_fraction_options));
    const auto require_cached_features = [&](
        const open_type_layout_table_view& layout,
        std::span<const std::uint16_t> lookups,
        std::span<const open_type_lookup_accelerator> accelerators) {
        for (std::size_t index = 0U; index < lookups.size(); ++index) {
            open_type_tag required_feature{};
            bool required = false;
            require(layout.try_required_feature_for_lookup(
                options.script,
                options.language,
                lookups[index],
                options.normalized_coordinates,
                required_feature,
                required,
                &error));
            require(accelerators[index].feature_required == required);
            if (required) {
                require(accelerators[index].feature == required_feature &&
                    !accelerators[index].feature_found);
                continue;
            }
            if (accelerators[index].feature_found) {
                bool contains = false;
                require(layout.try_feature_contains_lookup(
                    options.script,
                    options.language,
                    accelerators[index].feature,
                    lookups[index],
                    options.normalized_coordinates,
                    contains,
                    &error));
                require(contains);
            }
        }
    };
    require_cached_features(
        plan.gsub, plan.gsub_lookups, plan.gsub_accelerators);
    require_cached_features(
        plan.gpos, plan.gpos_lookups, plan.gpos_accelerators);
    require(plan.has_gpos_kerning);

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
    const auto contextual_planned = glyphs;
    const std::uint32_t contextual_planned_count = contextual_count;
    require(try_shape_open_type_run(
        font,
        contextual_input,
        options,
        glyphs,
        contextual_scratch,
        contextual_count,
        &error,
        nullptr));
    require(contextual_count == contextual_planned_count);
    for (std::uint32_t index = 0U; index < contextual_count; ++index) {
        const auto& left = contextual_planned[index];
        const auto& right = glyphs[index];
        require(left.glyph_id == right.glyph_id &&
            left.code_point == right.code_point &&
            left.cluster == right.cluster && left.flags == right.flags &&
            left.advance_x == right.advance_x &&
            left.advance_y == right.advance_y &&
            left.offset_x == right.offset_x &&
            left.offset_y == right.offset_y);
    }
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
    const auto fraction_planned = glyphs;
    const std::uint32_t fraction_planned_count = fraction_count;
    require(try_shape_open_type_run(
        font,
        fraction_input,
        options,
        glyphs,
        contextual_scratch,
        fraction_count,
        &error,
        nullptr));
    require(fraction_count == fraction_planned_count);
    for (std::uint32_t index = 0U; index < fraction_count; ++index) {
        const auto& left = fraction_planned[index];
        const auto& right = glyphs[index];
        require(left.glyph_id == right.glyph_id &&
            left.code_point == right.code_point &&
            left.cluster == right.cluster && left.flags == right.flags &&
            left.advance_x == right.advance_x &&
            left.advance_y == right.advance_y &&
            left.offset_x == right.offset_x &&
            left.offset_y == right.offset_y);
    }
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
    canonical_unicode_normalization_embeds_the_default_resource();
    unicode_script_itemization_preserves_source_ranges();
    open_type_common_layout_views_are_borrowed_and_bounded();
    open_type_gdef_classes_and_mark_sets_are_borrowed_and_bounded();
    open_type_gsub_basic_lookups_use_caller_owned_storage();
    open_type_gsub_reverse_chaining_matches_bounded_context();
    open_type_gsub_context_format3_applies_bounded_nested_lookups();
    open_type_gsub_context_contraction_advances_in_mutated_space();
    open_type_gsub_context_glyph_and_class_rules_are_bounded();
    open_type_gsub_chaining_glyph_rules_apply_nested_lookup();
    open_type_script_language_feature_selection_is_bounded();
    open_type_lookup_digest_extends_managed_negative_filter();
    open_type_feature_variations_match_managed_lookup_selection();
    open_type_gpos_single_and_pair_adjustments_are_bounded();
    open_type_gpos_attachments_are_caller_owned_and_resolved();
    open_type_gpos_context_format3_applies_nested_lookup();
    open_type_gpos_rule_and_chain_contexts_are_bounded();
    open_type_uniform_run_shaper_connects_unicode_font_and_metrics();
    open_type_shaping_route_matches_managed_plan_selection();
    open_type_feature_plan_matches_managed_script_and_direction_policy();
    open_type_requested_features_match_managed_cpu_shaper_normalization();
    open_type_shape_configuration_connects_managed_planning_stages();
    open_type_printable_ascii_matches_managed_fast_path();
    open_type_random_alternates_match_managed_run_state();
    open_type_common_preprocessing_matches_managed_stages();
    directional_code_point_fallback_matches_managed_stages();
    special_space_fallback_matches_managed_metrics();
    arabic_fallback_forms_and_ligatures_match_managed();
    open_type_khmer_preparation_reorders_prebase_vowels();
    open_type_myanmar_preparation_reorders_prebase_vowels();
    open_type_use_preparation_reorders_prebase_vowels();
    open_type_use_diacritic_normalization_matches_managed();
    open_type_initial_mapping_matches_managed();
    open_type_indic_preparation_reorders_prebase_matras();
    open_type_hangul_preparation_composes_and_decomposes();
    open_type_gpos_device_and_variation_deltas_are_applied();
    native_font_fallback_preserves_graphemes_and_missing_state();
    native_font_fallback_family_preferences_match_managed_policy();
    native_font_provider_cache_is_borrowed_and_generation_safe();
    native_positioned_text_layout_wraps_without_allocation();
    native_text_visual_order_matches_managed_cluster_policy();
    native_logical_text_layout_reorders_bidi_per_line();
    native_vertical_text_layout_matches_managed_columns();
    sfnt_simple_glyph_shaper_matches_managed_utf16_contract();
    unicode_line_breaks_feed_native_layout_without_allocation();
    complex_script_properties_and_syllable_machines_are_bounded();
    woff1_normalization_is_bounded_and_transactional();
    glyph_id_preserving_sfnt_subset_matches_managed_contract();
    compact_sfnt_subset_matches_managed_contract();
    sfnt_metadata_matches_managed_selection_and_style();
    borrowed_sfnt_view_reads_tables_metrics_and_cmap();
    standalone_sfnt_snapshot_matches_managed_contract();
    vertical_font_metrics_and_shaping_match_managed_policy();
    horizontal_font_metrics_match_managed_policy();
    fallback_mark_positioning_matches_managed_policy();
    arabic_stretch_matches_managed_bounded_expansion();
    legacy_kern_shaping_matches_managed_policy();
    open_type_feature_tags_match_managed_sorted_union();
    variation_selector_cmap_is_borrowed_and_bounded();
    variation_axes_are_borrowed_bounded_and_transactional();
    font_style_variations_match_managed_font_manager_policy();
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
    svg_glyph_layers_match_managed_shapes_references_and_paints();
    cbdt_index_and_image_formats_remain_borrowed_and_bounded();
    colr_layers_and_cpal_palettes_are_transactional();
    production_noto_cff1_container_matches_sfnt_glyph_count();
    production_inter_font_decodes_real_simple_outline();
    production_inter_variable_font_matches_fvar_axes();
    production_inter_shaping_is_stable_and_reusable();
    return 0;
}
