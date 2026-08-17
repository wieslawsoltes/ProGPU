import progpu.native.text;

int main() {
    constexpr auto tag =
        progpu::native::text::open_type_tag::from_chars('c', 'm', 'a', 'p');
    const unsigned short contour_ends[]{1U};
    const progpu::native::text::sfnt_outline_point points[]{
        {0, 0, 1U},
        {10, 10, 1U}};
    unsigned int segment_count = 0U;
    if (!progpu::native::text::sfnt_simple_glyph_path::
            try_get_segment_count(contour_ends, points, segment_count) ||
        segment_count != 2U) {
        return 1;
    }
    progpu_native_path_segment segments[2]{};
    const progpu::native::text::sfnt_composite_component component{};
    const progpu::native::text::sfnt_expanded_glyph_requirements expanded{};
    const progpu::native::text::sfnt_variation_axis axis{};
    const progpu::native::text::sfnt_gvar_header gvar{};
    const progpu::native::text::sfnt_gvar_tuple_header gvar_tuple{};
    const progpu::native::text::sfnt_glyph_phantom_variation_requirements
        phantom_requirements{};
    const progpu::native::text::sfnt_item_variation_store_view item_store{};
    const progpu::native::text::sfnt_cff_index_view cff_index{};
    const progpu::native::text::sfnt_cff_fd_select_view fd_select{};
    const progpu::native::text::sfnt_cff1_font_view cff_font{};
    const progpu::native::text::sfnt_cff1_outline_requirements cff_outline{};
    const progpu::native::text::sfnt_bitmap_glyph_data_view bitmap_glyph{};
    const progpu::native::text::sfnt_color_glyph_layer color_layer{};
    const progpu::native::text::sfnt_svg_glyph_document_view svg_glyph{};
    const progpu::native::text::sfnt_container_requirements container{};
    const progpu::native::text::sfnt_subset_requirements subset{};
    const progpu::native::text::sfnt_glyph_remap glyph_remap{};
    const progpu::native::text::sfnt_name_requirements name_requirements{};
    const progpu::native::text::sfnt_face_style face_style{};
    const progpu::native::text::sfnt_glyph_resident_requirements resident{};
    const progpu::native::text::sfnt_standalone_requirements standalone{};
    const progpu::native::text::sfnt_directory_record directory_record{};
    const progpu::native::text::font_style_request style_request{};
    const progpu::native::text::font_style_variation_requirements
        style_requirements{};
    const progpu::native::text::font_style_variation style_variation{};
    const progpu::native::text::font_provider_result provider_result{};
    const progpu::native::text::text_visual_order_requirements
        visual_order_requirements{};
    const progpu::native::text::text_logical_layout_scratch
        logical_layout_scratch{};
    const progpu::native::text::text_layout_options layout_options{};
    const progpu::native::text::positioned_text_column positioned_column{};
    const progpu::native::text::text_vertical_layout_requirements
        vertical_layout_requirements{};
    const progpu::native::text::text_layout_metrics layout_metrics{};
    const progpu::native::text::text_vertical_cluster_box
        vertical_cluster_box{};
    const progpu::native::text::text_vertical_caret_stop
        vertical_caret_stop{};
    const progpu::native::text::text_vertical_hit_test_result
        vertical_hit_test{};
    const progpu::native::text::open_type_feature_tag_requirements
        feature_tag_requirements{};
    const progpu::native::text::sfnt_vertical_header_metrics vertical_header{};
    const progpu::native::text::sfnt_vertical_glyph_metrics vertical_glyph{};
    const progpu::native::text::sfnt_simple_glyph_run_requirements
        simple_run_requirements{};
    const progpu::native::text::sfnt_simple_glyph_metrics
        simple_glyph_metrics{};
    const progpu::native::text::sfnt_glyph_bounds glyph_bounds{};
    const progpu::native::text::fallback_mark_metadata fallback_mark{};
    const progpu::native::text::open_type_shape_run_options shape_options{};
    const progpu::native::text::open_type_shape_verification_scratch
        shape_verification{};
    const progpu::native::text::open_type_shaping_route shaping_route{};
    const progpu::native::text::open_type_feature_setting feature_setting{};
    const progpu::native::text::open_type_feature_plan_requirements
        feature_plan_requirements{};
    const progpu::native::text::open_type_requested_feature_requirements
        requested_feature_requirements{};
    const progpu::native::text::open_type_shape_configuration_request
        shape_configuration_request{};
    const progpu::native::text::open_type_shape_configuration_requirements
        shape_configuration_requirements{};
    const progpu::native::text::open_type_shape_configuration
        shape_configuration{};
    const auto shaping_route_resolver =
        &progpu::native::text::try_resolve_open_type_shaping_route;
    const auto language_tag =
        progpu::native::text::resolve_open_type_language_tag("pl");
    const auto feature_plan_requirements_resolver =
        &progpu::native::text::try_get_open_type_feature_plan_requirements;
    const auto feature_plan_resolver =
        &progpu::native::text::try_resolve_open_type_feature_plan;
    const auto requested_feature_requirements_resolver =
        &progpu::native::text::try_get_open_type_requested_feature_requirements;
    const auto requested_feature_resolver =
        &progpu::native::text::try_resolve_open_type_requested_features;
    const auto shape_configuration_requirements_resolver =
        &progpu::native::text::try_get_open_type_shape_configuration_requirements;
    const auto shape_configuration_resolver =
        &progpu::native::text::try_prepare_open_type_shape_configuration;
    const auto shape_result_verifier =
        &progpu::native::text::try_verify_open_type_shape_result;
    const auto arabic_joining_flag_resolver =
        &progpu::native::text::try_assign_open_type_arabic_actions_and_flags;
    const auto fallback_preference_counter =
        &progpu::native::text::try_get_font_fallback_family_preference_count;
    const auto fallback_preference_writer =
        &progpu::native::text::try_get_font_fallback_family_preferences;
    const auto fallback_face_resolver =
        &progpu::native::text::try_resolve_font_provider_fallback_face;
    const auto visual_order_resolver =
        &progpu::native::text::try_reorder_text_line_visual;
    const auto visual_index_resolver =
        &progpu::native::text::try_get_text_line_visual_indices;
    const auto logical_layout_resolver =
        &progpu::native::text::try_layout_logical_shaped_text;
    const auto vertical_layout_requirements_resolver =
        &progpu::native::text::try_get_vertical_text_layout_requirements;
    const auto vertical_layout_resolver =
        &progpu::native::text::try_layout_vertical_shaped_text;
    const auto line_metrics_resolver =
        &progpu::native::text::try_measure_positioned_text_lines;
    const auto column_metrics_resolver =
        &progpu::native::text::try_measure_positioned_text_columns;
    const auto vertical_interaction_requirements_resolver =
        &progpu::native::text::try_get_vertical_text_interaction_requirements;
    const auto vertical_interaction_resolver =
        &progpu::native::text::try_build_vertical_text_interaction;
    const auto vertical_hit_test_resolver =
        &progpu::native::text::try_hit_test_vertical_text;
    const auto vertical_caret_resolver =
        &progpu::native::text::try_get_vertical_text_caret_stop;
    const auto vertical_caret_movement_resolver =
        &progpu::native::text::try_move_vertical_text_caret_visually;
    const auto vertical_selection_resolver =
        &progpu::native::text::try_get_vertical_text_selection_rectangles;
    const auto simple_run_requirements_resolver =
        &progpu::native::text::try_get_sfnt_simple_glyph_run_requirements;
    const auto simple_run_resolver =
        &progpu::native::text::try_build_sfnt_simple_glyph_run;
    const auto simple_control_resolver =
        &progpu::native::text::is_sfnt_simple_formatting_control;
    const auto simple_code_point_resolver =
        &progpu::native::text::try_read_sfnt_simple_code_point;
    const auto simple_advance_resolver =
        &progpu::native::text::try_fill_sfnt_simple_glyph_advances;
    const auto script_infer_resolver =
        &progpu::native::text::infer_open_type_script;
    const auto universal_shaper_resolver =
        &progpu::native::text::uses_universal_shaping_engine;
    const auto default_feature_settings =
        progpu::native::text::get_default_open_type_feature_settings();
    static_assert(progpu::native::text::sfnt_name_ids::family_name == 1U);
    const auto latin_script =
        progpu::native::text::get_unicode_script(0x41U);
    const progpu::native::text::shaping_glyph shaping_glyph{
        42U,
        0x41U,
        0,
        progpu::native::text::shaping_glyph_flags::unsafe_to_break,
        600,
        0,
        0,
        0};
    using gvar_deltas = progpu::native::text::sfnt_gvar_deltas;
    const progpu::native::text::sfnt_simple_glyph_variation_requirements
        variation_requirements{};
    const progpu::native::text::sfnt_composite_glyph_variation_requirements
        composite_variation_requirements{};
    const progpu::native::text::sfnt_varied_glyph_requirements
        varied_requirements{};
    const progpu::native::text::sfnt_packed_point_requirements packed{};
    if (component.m00 != 1.0F || component.m11 != 1.0F) {
        return 1;
    }
    if (expanded.point_count != 0U || expanded.path_segment_count != 0U) {
        return 1;
    }
    if (axis.minimum() != 0.0F || axis.hidden()) {
        return 1;
    }
    if (gvar.axis_count != 0U || gvar_tuple.flags != 0U ||
        packed.point_count != 0U || phantom_requirements.delta_count != 0U) {
        return 1;
    }
    if (item_store.region_count != 0U) {
        return 1;
    }
    if (cff_index.count != 0U || fd_select.range_count != 0U ||
        cff_outline.path_segment_count != 0U ||
        !cff_font.bytes.empty() || !bitmap_glyph.bytes.empty() ||
        bitmap_glyph.uses_horizontal_metrics ||
        color_layer.color.alpha != 255U ||
        !svg_glyph.bytes.empty() || container.requires_normalization ||
        subset.font_bytes != 0U || subset.glyph_map_count != 0U ||
        glyph_remap.source_glyph_id != 0U ||
        name_requirements.utf8_bytes != 0U ||
        face_style.weight != 400U || face_style.width != 5U ||
        resident.sbix_bytes != 0U || standalone.font_bytes != 0U ||
        directory_record.tag.value != 0U || style_request.weight != 400 ||
        style_requirements.setting_count != 0U ||
        style_variation.tag.value != 0U ||
        provider_result.glyph_index != 0U ||
        visual_order_requirements.group_capacity != 0U ||
        !logical_layout_scratch.visual_groups.empty() ||
        !logical_layout_scratch.visual_indices.empty() ||
        layout_options.alignment !=
            progpu::native::text::text_alignment::left ||
        positioned_column.glyph_count != 0U ||
        vertical_layout_requirements.column_capacity != 0U ||
        layout_metrics.content_width != 0.0F ||
        vertical_cluster_box.column_index != 0U ||
        vertical_caret_stop.width != 0.0F ||
        vertical_hit_test.inside ||
        feature_tag_requirements.tag_capacity != 0U ||
        vertical_header.number_of_vertical_metrics != 0U ||
        vertical_glyph.advance_height != 0U || glyph_bounds.x_min != 0 ||
        simple_run_requirements.glyph_count != 0U ||
        simple_glyph_metrics.advance_width != 0U ||
        fallback_mark.ligature_component != 0xFFU ||
        shape_options.normalization_data != nullptr ||
        !shape_verification.glyphs.empty() ||
        shape_options.unicode_script.value != 0U ||
        shaping_route.layout_script.value != 0U ||
        language_tag.value != 0x504C4B20U ||
        feature_setting.value != 1U ||
        feature_plan_requirements.requested_feature_capacity != 0U ||
        requested_feature_requirements.base_feature_capacity != 0U ||
        shape_configuration_request.unicode_script.value != 0U ||
        shape_configuration_requirements.base_feature_capacity != 0U ||
        shape_configuration.options.script.value != 0U ||
        feature_plan_requirements_resolver == nullptr ||
        feature_plan_resolver == nullptr ||
        requested_feature_requirements_resolver == nullptr ||
        requested_feature_resolver == nullptr ||
        shape_configuration_requirements_resolver == nullptr ||
        shape_configuration_resolver == nullptr ||
        shape_result_verifier == nullptr ||
        arabic_joining_flag_resolver == nullptr ||
        fallback_preference_counter == nullptr ||
        fallback_preference_writer == nullptr ||
        fallback_face_resolver == nullptr ||
        visual_order_resolver == nullptr ||
        visual_index_resolver == nullptr ||
        logical_layout_resolver == nullptr ||
        vertical_layout_requirements_resolver == nullptr ||
        vertical_layout_resolver == nullptr ||
        line_metrics_resolver == nullptr ||
        column_metrics_resolver == nullptr ||
        vertical_interaction_requirements_resolver == nullptr ||
        vertical_interaction_resolver == nullptr ||
        vertical_hit_test_resolver == nullptr ||
        vertical_caret_resolver == nullptr ||
        vertical_caret_movement_resolver == nullptr ||
        vertical_selection_resolver == nullptr ||
        simple_run_requirements_resolver == nullptr ||
        simple_run_resolver == nullptr ||
        simple_control_resolver == nullptr ||
        simple_code_point_resolver == nullptr ||
        simple_advance_resolver == nullptr ||
        script_infer_resolver == nullptr ||
        universal_shaper_resolver == nullptr ||
        default_feature_settings.size() != 26U ||
        shaping_route_resolver == nullptr ||
        !shape_options.pre_context.empty() ||
        !shape_options.post_context.empty() ||
        fallback_mark.positioned) {
        return 1;
    }
    (void)sizeof(gvar_deltas);
    if (variation_requirements.delta_count != 0U) {
        return 1;
    }
    if (composite_variation_requirements.delta_count != 0U) {
        return 1;
    }
    if (varied_requirements.component_offset_count != 0U) {
        return 1;
    }
    unsigned int written = 0U;
    if (!progpu::native::text::sfnt_simple_glyph_path::try_write_segments(
            contour_ends, points, segments, written) ||
        written != 2U) {
        return 1;
    }
    progpu::native::text::svg_path_requirements svg_path{};
    if (!progpu::native::text::try_get_svg_path_requirements(
            "M0 0L4 0L4 4Z", svg_path) ||
        svg_path.segment_count != 3U) {
        return 1;
    }
    progpu::native::text::svg_glyph_requirements svg_layers{};
    if (!progpu::native::text::try_get_svg_glyph_requirements(
            "<svg><path id='glyph2' d='M0 0L4 0L0 4Z'/></svg>",
            2U, 1000U, svg_layers) ||
        svg_layers.layer_count != 1U ||
        svg_layers.segment_count != 3U) {
        return 1;
    }
    progpu::native::text::svg_glyph_layer svg_layer_output[1]{};
    progpu_native_path_segment svg_segment_output[3]{};
    progpu::native::text::svg_brush_record svg_brush_output[1]{};
    if (!progpu::native::text::try_decode_svg_glyph(
            "<svg><path id='glyph2' d='M0 0L4 0L0 4Z'/></svg>",
            2U, 1000U, svg_layer_output, svg_segment_output,
            svg_brush_output, {}, svg_layers) ||
        svg_layer_output[0].segment_count != 3U ||
        svg_brush_output[0].type != 0U) {
        return 1;
    }
    return tag.value == 0x636D6170U &&
        latin_script.value == 0x6C61746EU &&
        shaping_glyph.advance_x == 600 ? 0 : 1;
}
