// Unchanged original static vertical metrics and neutral VVAR; timing/transport only.
#include "progpu_native_text.hpp"
#include <algorithm>
#include <bit>
#include <chrono>
#include <cstdio>
#include <cstdlib>
#include <vector>
using namespace progpu::native::text;
std::uint64_t run(const sfnt_font_view& face,std::uint16_t count,const sfnt_vertical_metrics_variation_instance& instance){
 std::uint64_t checksum=14695981039346656037ULL;auto hash=[&](std::uint32_t v){checksum=(checksum^v)*1099511628211ULL;};
 for(std::uint32_t g=0;g<count;++g){const auto glyph=static_cast<std::uint16_t>(g);
  std::int32_t value{};auto ok=face.try_get_design_advance_height(glyph,value);hash(ok);hash(ok?value:0);
  ok=face.try_get_design_vertical_origin_y(glyph,value);hash(ok);hash(ok?value:0);
  sfnt_vertical_metrics_variation delta{};if(!face.try_get_vertical_metrics_variation(glyph,instance,delta))std::exit(11);
  for(auto n:{delta.advance_height,delta.top_side_bearing,delta.bottom_side_bearing,delta.vertical_origin_y})hash(std::bit_cast<std::uint32_t>(n));
  for(auto b:{delta.uses_vvar,delta.has_top_side_bearing,delta.has_bottom_side_bearing,delta.has_vertical_origin_y})hash(b);
 }
 return checksum;
}
int main(int argc,char**argv){
 if(argc<2||argc>4)return 1;auto* f=std::fopen(argv[1],"rb");if(!f)return 2;
 if(std::fseek(f,0,SEEK_END))return 3;const auto size=std::ftell(f);if(size<0||std::fseek(f,0,SEEK_SET))return 4;
 std::vector<std::byte> data(static_cast<std::size_t>(size));if(std::fread(data.data(),1,data.size(),f)!=data.size())return 5;std::fclose(f);
 sfnt_font_view face{};if(!sfnt_font_view::try_create(data,argc>=3?static_cast<std::uint32_t>(std::strtoul(argv[2],nullptr,10)):0,face))return 6;
 std::uint16_t axes{},count{},regions{};if(!face.try_get_variation_axis_count(axes)||!face.try_get_glyph_count(count)||count==0)return 7;
 std::vector<std::int16_t> coords(axes,argc>=4?static_cast<std::int16_t>(std::strtol(argv[3],nullptr,10)):8192);bool uses{};
 if(!face.try_get_vertical_metrics_variation_region_count(coords,regions,uses))return 8;
 std::vector<float> scalars(regions);sfnt_vertical_metrics_variation_instance instance{};
 if(!face.try_prepare_vertical_metrics_variation(coords,scalars,instance))return 9;
 const auto checksum=run(face,count,instance);for(unsigned i=0;i<5;++i)if(run(face,count,instance)!=checksum)return 12;
 if(const auto* value=std::getenv("PROGPU_RUST_PROFILE_ROUNDS")){const auto rounds=std::strtoull(value,nullptr,10);for(unsigned long long i=0;i<rounds;++i)if(run(face,count,instance)!=checksum)return 12;std::printf("profile_rounds=%llu checksum=%llu\n",rounds,static_cast<unsigned long long>(checksum));return 0;}
 std::vector<double> times;for(unsigned i=0;i<40;++i){const auto start=std::chrono::steady_clock::now();const auto actual=run(face,count,instance);times.push_back(std::chrono::duration<double,std::micro>(std::chrono::steady_clock::now()-start).count());if(actual!=checksum)return 13;}
 std::sort(times.begin(),times.end());std::printf("glyphs=%u axes=%u regions=%u vvar=%s checksum=%llu round_us_p50=%.3f p95=%.3f p99=%.3f\n",count,axes,regions,uses?"true":"false",static_cast<unsigned long long>(checksum),times[19],times[37],times[39]);
}
