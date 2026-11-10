#include "progpu_native_text.hpp"
#include <algorithm>
#include <chrono>
#include <cstdio>
#include <fstream>
#include <iterator>
#include <vector>
using namespace progpu::native::text;
int main(int argc,char** argv){if(argc!=2)return 2;std::ifstream file(argv[1],std::ios::binary);std::vector<unsigned char> bytes((std::istreambuf_iterator<char>(file)),{});auto u16=[&](std::size_t at){return static_cast<std::uint16_t>((bytes[at]<<8)|bytes[at+1]);};
 open_type_layout_table_view table{};if(!open_type_layout_table_view::try_create(std::as_bytes(std::span(bytes)),table))return 3;
 const auto at=u16(6);std::vector<open_type_tag> features;for(std::uint32_t i=0;i<u16(at);++i){const auto p=at+2+i*6;features.push_back({(static_cast<std::uint32_t>(u16(p))<<16)|u16(p+2)});}
 const auto script=open_type_tag::from_chars('D','F','L','T');const std::int16_t coordinates[]{0};open_type_layout_table_view::lookup_selection_requirements r{};
 if(!table.try_get_lookup_selection_requirements(script,{},features,coordinates,r))return 4;std::vector<std::uint16_t> output(r.lookup_capacity);std::vector<double> times;std::uint32_t count{},hash{},old{};
 for(int round=0;round<45;++round){const auto start=std::chrono::steady_clock::now();if(!table.try_select_lookups(script,{},features,coordinates,output,count))return 5;
  const double us=std::chrono::duration<double,std::micro>(std::chrono::steady_clock::now()-start).count();hash=2166136261U;for(std::uint32_t i=0;i<count;++i)hash=(hash^output[i])*16777619U;if(round&&hash!=old)return 6;old=hash;if(round>=5)times.push_back(us);
 }std::sort(times.begin(),times.end());std::printf("lookups=%u checksum=%u p50_us=%.3f p95_us=%.3f p99_us=%.3f\n",count,hash,times[19],times[37],times[39]);
}
