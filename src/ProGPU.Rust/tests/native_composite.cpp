// Link original component parsing and recursive outline expansion unchanged.
#include "progpu_native_text.hpp"
#include <cstdio>
#include <cstdlib>
#include <bit>
#include <vector>
using namespace progpu::native::text;
void emit(std::uint32_t v) { if(std::fwrite(&v,4,1,stdout)!=1) std::exit(2); }
void scalar(float v) { emit(std::bit_cast<std::uint32_t>(v)); }
int main() {
    std::uint32_t index{},length{},glyph{};
    while(std::fread(&index,4,1,stdin)==1) {
        if(std::fread(&length,4,1,stdin)!=1) return 3;
        std::vector<std::byte> data(length);
        if(std::fread(data.data(),1,length,stdin)!=length || std::fread(&glyph,4,1,stdin)!=1) return 4;
        sfnt_font_view face{};font_error error{};
        const auto accepted=sfnt_font_view::try_create(data,index,face,&error);
        emit(static_cast<std::uint32_t>(error));if(!accepted) continue;
        sfnt_composite_glyph_decode_requirements cr{};
        const auto inspected=face.try_get_composite_glyph_decode_requirements(glyph,cr,&error);
        emit(static_cast<std::uint32_t>(error));
        if(inspected) {
            emit(cr.component_count);emit(cr.instruction_bytes);
            std::vector<sfnt_composite_component> cs(cr.component_count);
            if(!face.try_decode_composite_glyph(glyph,cs,&error)) return 5;
            for(const auto c:cs) {
                emit(c.flags);emit(c.glyph_index);emit(c.argument1);emit(c.argument2);
                scalar(c.m00);scalar(c.m01);scalar(c.m10);scalar(c.m11);
            }
        }
        sfnt_expanded_glyph_requirements r{};
        const auto measured=face.try_get_expanded_glyph_requirements(glyph,r,&error);
        emit(static_cast<std::uint32_t>(error));if(!measured) continue;
        emit(r.point_count);emit(r.path_segment_count);emit(r.simple_point_scratch_count);emit(r.simple_contour_scratch_count);
        std::vector<std::uint16_t> ends(r.simple_contour_scratch_count);
        std::vector<sfnt_outline_point> scratch(r.simple_point_scratch_count);
        std::vector<progpu_native_point> points(r.point_count);
        std::vector<progpu_native_path_segment> segments(r.path_segment_count);
        std::uint32_t pc{},sc{};
        const auto decoded=face.try_decode_glyph_outline(glyph,ends,scratch,points,segments,pc,sc,&error);
        emit(static_cast<std::uint32_t>(error));if(!decoded) continue;
        emit(pc);emit(sc);
        for(const auto p:points) {scalar(p.x);scalar(p.y);}
        if(std::fwrite(segments.data(),sizeof(progpu_native_path_segment),sc,stdout)!=sc) return 6;
    }
    return std::ferror(stdin)?7:0;
}
