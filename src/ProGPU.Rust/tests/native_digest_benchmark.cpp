#include "progpu_native_text.hpp"
#include <algorithm>
#include <chrono>
#include <cstdio>
#include <fstream>
#include <iterator>
#include <vector>
using namespace progpu::native::text;
int main(int argc,char** argv){if(argc!=2)return 2;std::ifstream file(argv[1],std::ios::binary);std::vector<char> raw((std::istreambuf_iterator<char>(file)),{});
 const auto begin=std::chrono::steady_clock::now();open_type_layout_table_view table{};open_type_glyph_set_digest digest{};bool has{};
 if(!open_type_layout_table_view::try_create(std::as_bytes(std::span(raw)),table)||!table.try_get_lookup_digest(0,7,digest,has)||!has)return 3;
 const double preparation=std::chrono::duration<double,std::micro>(std::chrono::steady_clock::now()-begin).count();
 std::vector<std::uint8_t> output(65536);std::vector<double> times;
 for(int round=0;round<45;++round){const auto start=std::chrono::steady_clock::now();
  for(std::uint32_t i=0;i<65536;++i)output[i]=digest.may_have(static_cast<std::uint16_t>(i*257U));
  const double us=std::chrono::duration<double,std::micro>(std::chrono::steady_clock::now()-start).count();
  volatile auto* observed=output.data();(void)*observed;if(round>=5)times.push_back(us);
 }
 std::uint32_t hash=2166136261U;for(auto v:output)hash=(hash^v)*16777619U;
 std::sort(times.begin(),times.end());std::printf("prepare_us=%.3f queries=65536 checksum=%u p50_us=%.3f p95_us=%.3f p99_us=%.3f\n",preparation,hash,times[19],times[37],times[39]);
}
