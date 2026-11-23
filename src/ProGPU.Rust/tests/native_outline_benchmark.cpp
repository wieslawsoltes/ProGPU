// Matched original decoder/contour-writer workload; no external font engine.
#include "progpu_native_text.hpp"
#include <algorithm>
#include <array>
#include <bit>
#include <chrono>
#include <cstdio>
#include <cstdlib>
#include <vector>
#include <cstring>
#include "native_varied_benchmark.hpp"
using namespace progpu::native::text;
std::uint64_t run(const sfnt_font_view& face,const std::vector<std::uint16_t>& glyphs,bool expanded,const sfnt_cff1_font_view* cff,const std::vector<std::int16_t>* coords) {
    std::uint64_t checksum=14695981039346656037ULL;
    auto hash=[&](std::uint32_t value){checksum=(checksum^value)*1099511628211ULL;};
    for(const auto id:glyphs) {
        if(coords){varied_outline(face,id,*coords,hash);continue;}
        if(cff) {
            sfnt_cff1_outline_requirements r{};
            if(!sfnt_cff_data::try_get_outline_requirements(*cff,id,r))std::exit(18);
            std::vector<progpu_native_path_segment> segments(r.path_segment_count);std::uint32_t written{};
            if(!sfnt_cff_data::try_decode_outline(*cff,id,segments,written)||written!=segments.size())std::exit(19);
            for(const auto& s:segments)for(const auto word:std::bit_cast<std::array<std::uint32_t,12>>(s))hash(word);
            continue;
        }
        if(expanded) {
            sfnt_expanded_glyph_requirements r{};
            if(!face.try_get_expanded_glyph_requirements(id,r))std::exit(15);
            std::vector<std::uint16_t> contours(r.simple_contour_scratch_count);
            std::vector<sfnt_outline_point> scratch(r.simple_point_scratch_count);
            std::vector<progpu_native_point> points(r.point_count);
            std::vector<progpu_native_path_segment> segments(r.path_segment_count);
            std::uint32_t pc{},sc{};
            if(!face.try_decode_glyph_outline(id,contours,scratch,points,segments,pc,sc)||pc!=points.size()||sc!=segments.size())std::exit(16);
            for(const auto p:points){hash(std::bit_cast<std::uint32_t>(p.x));hash(std::bit_cast<std::uint32_t>(p.y));}
            for(const auto& s:segments)for(const auto word:std::bit_cast<std::array<std::uint32_t,12>>(s))hash(word);
            continue;
        }
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
    if(argc<2||argc>5)return 1;
    const bool varied=argc>=4 && std::strcmp(argv[3],"--varied")==0;
    const bool expanded=varied || (argc==4 && std::strcmp(argv[3],"--expanded")==0);
    const bool cff_mode=argc==4 && std::strcmp(argv[3],"--cff")==0;
    if(argc>=4 && !expanded && !cff_mode)return 1;
    auto* file=std::fopen(argv[1],"rb");if(!file)return 5;
    if(std::fseek(file,0,SEEK_END))return 6;
    const auto size=std::ftell(file);if(size<0||std::fseek(file,0,SEEK_SET))return 7;
    std::vector<std::byte> bytes(static_cast<std::size_t>(size));
    if(std::fread(bytes.data(),1,bytes.size(),file)!=bytes.size())return 8;
    std::fclose(file);
    sfnt_font_view face{};
    if(!sfnt_font_view::try_create(bytes,argc>=3?static_cast<std::uint32_t>(std::strtoul(argv[2],nullptr,10)):0,face))return 9;
    std::vector<std::int16_t> coordinates;
    if(varied){sfnt_gvar_header g{};if(!face.try_get_gvar_header(g))return 22;coordinates.assign(g.axis_count,argc==5?static_cast<std::int16_t>(std::strtol(argv[4],nullptr,10)):8192);}
    std::uint16_t count{};sfnt_cff1_font_view cff{};
    if(cff_mode){if(!face.try_get_cff1_font(0,cff)||cff.char_strings.count>65535)return 20;count=static_cast<std::uint16_t>(cff.char_strings.count);}
    else if(!face.try_get_glyph_count(count))return 10;
    std::vector<std::uint16_t> glyphs;std::uint64_t points{},segments{};
    for(std::uint32_t id=0;id<count;++id) {
        if(cff_mode){sfnt_cff1_outline_requirements r{};if(!sfnt_cff_data::try_get_outline_requirements(cff,id,r))return 21;glyphs.push_back(static_cast<std::uint16_t>(id));segments+=r.path_segment_count;continue;}
        if(expanded) {
            sfnt_expanded_glyph_requirements r{};
            if(!face.try_get_expanded_glyph_requirements(static_cast<std::uint16_t>(id),r))return 17;
            glyphs.push_back(static_cast<std::uint16_t>(id));points+=r.point_count;segments+=r.path_segment_count;
            continue;
        }
        sfnt_glyph_decode_requirements r{};if(!face.try_get_glyph_decode_requirements(static_cast<std::uint16_t>(id),r))return 11;
        if(r.kind==sfnt_glyph_kind::simple){glyphs.push_back(static_cast<std::uint16_t>(id));points+=r.point_count;segments+=r.path_segment_count;}
    }
    if(glyphs.empty())return 12;
    const auto checksum=run(face,glyphs,expanded,cff_mode?&cff:nullptr,varied?&coordinates:nullptr);
    for(unsigned i=0;i<5;++i)if(run(face,glyphs,expanded,cff_mode?&cff:nullptr,varied?&coordinates:nullptr)!=checksum)return 13;
    std::vector<double> times;
    for(unsigned i=0;i<40;++i) {
        const auto start=std::chrono::steady_clock::now();const auto actual=run(face,glyphs,expanded,cff_mode?&cff:nullptr,varied?&coordinates:nullptr);
        times.push_back(std::chrono::duration<double,std::micro>(std::chrono::steady_clock::now()-start).count());if(actual!=checksum)return 14;
    }
    std::sort(times.begin(),times.end());
    std::printf("mode=%s glyphs=%zu points=%llu segments=%llu checksum=%llu round_us_p50=%.3f p95=%.3f p99=%.3f\n",varied?"varied":cff_mode?"cff":expanded?"expanded":"simple",glyphs.size(),static_cast<unsigned long long>(points),static_cast<unsigned long long>(segments),static_cast<unsigned long long>(checksum),times[19],times[37],times[39]);
}
