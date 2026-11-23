// Original prepared HVAR and full-advance workload; no copied metric algorithm.
#include "progpu_native_text.hpp"
#include <algorithm>
#include <bit>
#include <chrono>
#include <cstdio>
#include <cstdlib>
#include <vector>
using namespace progpu::native::text;
std::uint64_t run(const sfnt_font_view& face,std::uint16_t count,const std::vector<std::int16_t>& coords,const sfnt_horizontal_metrics_variation_instance& instance){
 std::uint64_t checksum=14695981039346656037ULL;auto hash=[&](std::uint32_t v){checksum=(checksum^v)*1099511628211ULL;};
 for(std::uint32_t g=0;g<count;++g){const auto glyph=static_cast<std::uint16_t>(g);sfnt_design_advance_width_requirements r{};
  // An active prepared HVAR instance has no phantom scratch requirement.
  if(!instance.advance.uses_hvar && !face.try_get_design_advance_width_requirements(glyph,coords,r))std::exit(10);
  std::vector<sfnt_gvar_tuple_header> h(r.phantom.tuple_header_count);std::vector<std::int16_t> c(r.phantom.region_coordinate_count),x(r.phantom.delta_count),y(r.phantom.delta_count);
  std::vector<std::uint32_t> p(r.phantom.point_number_count),q(r.phantom.point_number_count);
  float width{},bearing{};bool mapping{};
  if(!face.try_get_design_advance_width(glyph,coords,&instance.advance,width,{h,c,p,q,x,y}) || !face.try_get_horizontal_left_side_bearing_variation(glyph,instance,bearing,mapping))std::exit(11);
  hash(std::bit_cast<std::uint32_t>(width));hash(mapping);hash(std::bit_cast<std::uint32_t>(bearing));
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
 if(!face.try_get_horizontal_advance_variation_region_count(coords,regions,uses))return 8;
 std::vector<float> scalars(regions);sfnt_horizontal_metrics_variation_instance instance{};
 if(!face.try_prepare_horizontal_metrics_variation(coords,scalars,instance))return 9;
 const auto checksum=run(face,count,coords,instance);for(unsigned i=0;i<5;++i)if(run(face,count,coords,instance)!=checksum)return 12;
 if(const auto* value=std::getenv("PROGPU_RUST_PROFILE_ROUNDS")){const auto rounds=std::strtoull(value,nullptr,10);for(unsigned long long i=0;i<rounds;++i)if(run(face,count,coords,instance)!=checksum)return 12;std::printf("profile_rounds=%llu checksum=%llu\n",rounds,static_cast<unsigned long long>(checksum));return 0;}
 std::vector<double> times;for(unsigned i=0;i<40;++i){const auto start=std::chrono::steady_clock::now();const auto actual=run(face,count,coords,instance);times.push_back(std::chrono::duration<double,std::micro>(std::chrono::steady_clock::now()-start).count());if(actual!=checksum)return 13;}
 std::sort(times.begin(),times.end());std::printf("glyphs=%u axes=%u regions=%u hvar=%s checksum=%llu round_us_p50=%.3f p95=%.3f p99=%.3f\n",count,axes,regions,uses?"true":"false",static_cast<unsigned long long>(checksum),times[19],times[37],times[39]);
}
