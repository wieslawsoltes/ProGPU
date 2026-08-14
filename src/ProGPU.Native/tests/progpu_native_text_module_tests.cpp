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
    return tag.value == 0x636D6170U ? 0 : 1;
}
