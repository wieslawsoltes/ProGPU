// Matched original decoder/contour-writer workload; no external font engine.
#include "progpu_native_text.hpp"
#include <algorithm>
#include <array>
#include <bit>
#include <chrono>
#include <cstdio>
#include <cstdlib>
#include <vector>
using namespace progpu::native::text;
std::uint64_t run(const sfnt_font_view& face,const std::vector<std::uint16_t>& glyphs) {
    std::uint64_t checksum=14695981039346656037ULL;
    auto hash=[&](std::uint32_t value){checksum=(checksum^value)*1099511628211ULL;};
    for(const auto id:glyphs) {
        sfnt_glyph_decode_requirements r{};
        if(!face.try_get_glyph_decode_requirements(id,r))std::exit(2);
        std::vector<std::uint16_t> contours(r.contour_count);
        std::vector<sfnt_outline_point> points(r.point_count);
        if(!face.try_decode_simple_glyph(id,contours,points))std::exit(3);
        for(const auto p:points){hash(p.x);hash(p.y);hash(p.flags);}
        std::vector<progpu_native_path_segment> segments(r.path_segment_count);
        std::uint32_t written{};
        if(!sfnt_simple_glyph_path::try_write_segments(contours,points,segments,written)||written!=segments.size())std::exit(4);
        for(const auto& s:segments)for(const auto word:std::bit_cast<std::array<std::uint32_t,12>>(s))hash(word);
    }
    return checksum;
}
int main(int argc,char** argv) {
    if(argc<2||argc>3)return 1;
    auto* file=std::fopen(argv[1],"rb");if(!file)return 5;
    if(std::fseek(file,0,SEEK_END))return 6;
    const auto size=std::ftell(file);if(size<0||std::fseek(file,0,SEEK_SET))return 7;
    std::vector<std::byte> bytes(static_cast<std::size_t>(size));
    if(std::fread(bytes.data(),1,bytes.size(),file)!=bytes.size())return 8;
    std::fclose(file);
    sfnt_font_view face{};
    if(!sfnt_font_view::try_create(bytes,argc==3?static_cast<std::uint32_t>(std::strtoul(argv[2],nullptr,10)):0,face))return 9;
    std::uint16_t count{};if(!face.try_get_glyph_count(count))return 10;
    std::vector<std::uint16_t> glyphs;std::uint64_t points{},segments{};
    for(std::uint32_t id=0;id<count;++id) {
        sfnt_glyph_decode_requirements r{};if(!face.try_get_glyph_decode_requirements(static_cast<std::uint16_t>(id),r))return 11;
        if(r.kind==sfnt_glyph_kind::simple){glyphs.push_back(static_cast<std::uint16_t>(id));points+=r.point_count;segments+=r.path_segment_count;}
    }
    if(glyphs.empty())return 12;
    const auto checksum=run(face,glyphs);
    for(unsigned i=0;i<5;++i)if(run(face,glyphs)!=checksum)return 13;
    std::vector<double> times;
    for(unsigned i=0;i<40;++i) {
        const auto start=std::chrono::steady_clock::now();const auto actual=run(face,glyphs);
        times.push_back(std::chrono::duration<double,std::micro>(std::chrono::steady_clock::now()-start).count());if(actual!=checksum)return 14;
    }
    std::sort(times.begin(),times.end());
    std::printf("simple_glyphs=%zu points=%llu segments=%llu checksum=%llu round_us_p50=%.3f p95=%.3f p99=%.3f\n",glyphs.size(),static_cast<unsigned long long>(points),static_cast<unsigned long long>(segments),static_cast<unsigned long long>(checksum),times[19],times[37],times[39]);
}
