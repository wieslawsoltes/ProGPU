module;

#include "progpu_native_text.hpp"

export module progpu.native.text;

export namespace progpu::native::text {

using ::progpu::native::text::font_error;
using ::progpu::native::text::open_type_tag;
using ::progpu::native::text::sfnt_font_view;
using ::progpu::native::text::sfnt_composite_component;
using ::progpu::native::text::sfnt_composite_glyph_decode_requirements;
using ::progpu::native::text::sfnt_expanded_glyph_requirements;
using ::progpu::native::text::sfnt_glyph_data_view;
using ::progpu::native::text::sfnt_glyph_decode_requirements;
using ::progpu::native::text::sfnt_glyph_kind;
using ::progpu::native::text::sfnt_header_metrics;
using ::progpu::native::text::sfnt_horizontal_glyph_metrics;
using ::progpu::native::text::sfnt_horizontal_header_metrics;
using ::progpu::native::text::sfnt_table_view;
using ::progpu::native::text::sfnt_outline_point;
using ::progpu::native::text::sfnt_simple_glyph_path;
using ::progpu::native::text::sfnt_variation_axis;

} // namespace progpu::native::text

export using ::progpu_native_path_segment;
