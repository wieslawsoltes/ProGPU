// Original ProGPU simple-glyph decoder and contour writer, linked unchanged.
#include "progpu_native_text.hpp"
#include <cstdio>
#include <cstdlib>
#include <vector>
using namespace progpu::native::text;
void emit(std::uint32_t value) { if(std::fwrite(&value,4,1,stdout)!=1) std::exit(2); }
int main() {
    std::uint32_t index{},length{},glyph{};
    while(std::fread(&index,4,1,stdin)==1) {
        if(std::fread(&length,4,1,stdin)!=1) return 3;
        std::vector<std::byte> data(length);
        if(std::fread(data.data(),1,length,stdin)!=length || std::fread(&glyph,4,1,stdin)!=1) return 4;
        sfnt_font_view face{};font_error error{};
        const auto accepted=sfnt_font_view::try_create(data,index,face,&error);
        emit(static_cast<std::uint32_t>(error));if(!accepted) continue;
        sfnt_glyph_decode_requirements required{};
        const auto inspected=face.try_get_glyph_decode_requirements(static_cast<std::uint16_t>(glyph),required,&error);
        emit(static_cast<std::uint32_t>(error));
        if(inspected) {emit(static_cast<std::uint32_t>(required.kind));emit(required.contour_count);emit(required.point_count);emit(required.path_segment_count);emit(required.instruction_bytes);}
        std::vector<std::uint16_t> ends(required.contour_count);
        std::vector<sfnt_outline_point> points(required.point_count);
        const auto decoded=face.try_decode_simple_glyph(static_cast<std::uint16_t>(glyph),ends,points,&error);
        emit(static_cast<std::uint32_t>(error));if(!decoded) continue;
        for(const auto end:ends) emit(end);
        for(const auto point:points) {emit(point.x);emit(point.y);emit(point.flags);}
        for(unsigned mode=0;mode<2;++mode) {
            std::vector<progpu_native_point> varied;
            if(mode) for(const auto p:points) varied.push_back({static_cast<float>(p.x)*0.25F+0.125F,static_cast<float>(p.y)*-0.5F-0.25F});
            std::vector<progpu_native_path_segment> segments(required.path_segment_count);
            std::uint32_t written{};
            if(!sfnt_simple_glyph_path::try_write_varied_segments(ends,points,varied,segments,written,&error)||written!=segments.size()) return 5;
            emit(written);
            if(std::fwrite(segments.data(),sizeof(progpu_native_path_segment),written,stdout)!=written) return 6;
        }
    }
    return std::ferror(stdin)?7:0;
}
