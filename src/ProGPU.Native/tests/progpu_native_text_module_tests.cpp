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
    if (component.m00 != 1.0F || component.m11 != 1.0F) {
        return 1;
    }
    if (expanded.point_count != 0U || expanded.path_segment_count != 0U) {
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
