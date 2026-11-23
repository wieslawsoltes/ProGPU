// Allocate the original public caller scratch and hash the actual varied output.
// This adapter does not implement parsing, variation math or outline generation.
#pragma once
#include "progpu_native_text.hpp"
#include <array>
#include <bit>
#include <cstdlib>
#include <vector>
template<class Hash>
void varied_outline(const progpu::native::text::sfnt_font_view& face, std::uint16_t glyph,
    const std::vector<std::int16_t>& coords, Hash& hash) {
    using namespace progpu::native::text;
    sfnt_varied_glyph_requirements r{};
    if(!face.try_get_varied_glyph_requirements(glyph,r))std::exit(23);
    std::vector<std::uint16_t> ends(r.outline.simple_contour_scratch_count);
    std::vector<sfnt_outline_point> original(r.outline.simple_point_scratch_count);
    std::vector<progpu_native_point> varied(r.varied_simple_point_count), offsets(r.component_offset_count), points(r.outline.point_count);
    std::vector<progpu_native_path_segment> segments(r.outline.path_segment_count);
    const auto& s=r.simple_variation;const auto& c=r.composite_variation;
    std::vector<sfnt_gvar_tuple_header> sh(s.tuple_header_count),ch(c.tuple_header_count);
    std::vector<std::int16_t> sc(s.region_coordinate_count),sx(s.delta_count),sy(s.delta_count),cc(c.region_coordinate_count),cx(c.delta_count),cy(c.delta_count);
    std::vector<std::uint32_t> sp(s.point_number_count),pp(s.point_number_count),cp(c.point_number_count),pc(c.point_number_count);
    std::vector<float> tx(s.tuple_point_count),ty(s.tuple_point_count);std::vector<std::uint8_t> touched(s.tuple_point_count);
    std::uint32_t pw{},sw{};
    if(!face.try_decode_varied_glyph_outline(glyph,coords,{ends,original,varied,offsets,{sh,sc,sp,pp,sx,sy,tx,ty,touched},{ch,cc,cp,pc,cx,cy}},points,segments,pw,sw)
        || pw!=points.size() || sw!=segments.size()) std::exit(24);
    for(const auto p:points){hash(std::bit_cast<std::uint32_t>(p.x));hash(std::bit_cast<std::uint32_t>(p.y));}
    for(const auto& segment:segments)for(const auto word:std::bit_cast<std::array<std::uint32_t,12>>(segment))hash(word);
}
