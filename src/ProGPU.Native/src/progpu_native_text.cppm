module;

#include "progpu_native_text.hpp"

export module progpu.native.text;

export namespace progpu::native::text {

using ::progpu::native::text::font_error;
using ::progpu::native::text::sfnt_container_requirements;
using ::progpu::native::text::try_get_sfnt_container_requirements;
using ::progpu::native::text::try_normalize_sfnt_container;
using ::progpu::native::text::open_type_tag;
using ::progpu::native::text::sfnt_font_view;
using ::progpu::native::text::sfnt_bitmap_glyph_data_view;
using ::progpu::native::text::sfnt_color_glyph_layer;
using ::progpu::native::text::sfnt_color_rgba8;
using ::progpu::native::text::sfnt_svg_glyph_document_view;
using ::progpu::native::text::try_decode_svg_glyph_document;
using ::progpu::native::text::try_get_svg_glyph_document_size;
using ::progpu::native::text::sfnt_composite_component;
using ::progpu::native::text::sfnt_composite_glyph_decode_requirements;
using ::progpu::native::text::sfnt_composite_glyph_variation_requirements;
using ::progpu::native::text::sfnt_composite_glyph_variation_scratch;
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
using ::progpu::native::text::sfnt_gvar_header;
using ::progpu::native::text::sfnt_gvar_tuple_data;
using ::progpu::native::text::sfnt_gvar_deltas;
using ::progpu::native::text::sfnt_gvar_tuple_header;
using ::progpu::native::text::sfnt_gvar_tuple_requirements;
using ::progpu::native::text::sfnt_glyph_variation_data_view;
using ::progpu::native::text::sfnt_glyph_phantom_variation_requirements;
using ::progpu::native::text::sfnt_glyph_phantom_variation_scratch;
using ::progpu::native::text::sfnt_item_variation_data;
using ::progpu::native::text::sfnt_item_variation_store_view;
using ::progpu::native::text::sfnt_delta_set_index_map_view;
using ::progpu::native::text::sfnt_cff_data;
using ::progpu::native::text::sfnt_cff_fd_select_view;
using ::progpu::native::text::sfnt_cff_index_view;
using ::progpu::native::text::sfnt_cff1_font_view;
using ::progpu::native::text::sfnt_cff1_outline_requirements;
using ::progpu::native::text::sfnt_cff1_top_dictionary;
using ::progpu::native::text::sfnt_packed_delta_requirements;
using ::progpu::native::text::sfnt_packed_point_requirements;
using ::progpu::native::text::sfnt_packed_variation_data;
using ::progpu::native::text::sfnt_simple_glyph_variation_requirements;
using ::progpu::native::text::sfnt_simple_glyph_variation_scratch;
using ::progpu::native::text::sfnt_varied_glyph_requirements;
using ::progpu::native::text::sfnt_varied_glyph_scratch;

} // namespace progpu::native::text

export using ::progpu_native_path_segment;
