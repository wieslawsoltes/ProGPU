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
        glyph_remap.source_glyph_id != 0U) {
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
