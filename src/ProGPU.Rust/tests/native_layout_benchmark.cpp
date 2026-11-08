#include "progpu_native_text.hpp"
#include <algorithm>
#include <chrono>
#include <cstdio>
#include <cstdlib>
#include <fstream>
#include <iterator>
#include <vector>
using namespace progpu::native::text;
int main(int argc,char** argv){if(argc!=3)return 1;const bool coverage_kind=std::atoi(argv[1])==0;
 std::ifstream input(argv[2],std::ios::binary);std::vector<char> storage((std::istreambuf_iterator<char>(input)),{});
 const auto bytes=std::as_bytes(std::span(storage));open_type_coverage_view coverage{};open_type_class_definition_view classes{};
 if(coverage_kind?!open_type_coverage_view::try_create(bytes,0,coverage):!open_type_class_definition_view::try_create(bytes,0,classes))return 2;
 std::vector<std::uint16_t> glyphs(65536);std::vector<std::uint32_t> output(glyphs.size());for(std::uint32_t i=0;i<glyphs.size();++i)glyphs[i]=static_cast<std::uint16_t>(i*25173U+13849U);
 std::vector<double> times;std::uint64_t checksum{};
 for(int round=0;round<45;++round){const auto start=std::chrono::steady_clock::now();
  if(coverage_kind){for(std::size_t i=0;i<glyphs.size();++i)output[i]=static_cast<std::uint32_t>(coverage.find(glyphs[i]));}
  else {for(std::size_t i=0;i<glyphs.size();++i)output[i]=classes.get(glyphs[i]);}
  const auto elapsed=std::chrono::duration<double,std::micro>(std::chrono::steady_clock::now()-start).count();
  std::uint64_t hash=1469598103934665603ULL;for(auto v:output)hash=(hash^v)*1099511628211ULL;
  if(round!=0&&checksum!=hash)return 3;checksum=hash;if(round>=5)times.push_back(elapsed);
 }std::sort(times.begin(),times.end());std::printf("queries=65536 checksum=%llu min_us=%.3f p50_us=%.3f p95_us=%.3f max_us=%.3f\n",static_cast<unsigned long long>(checksum),times[0],times[20],times[37],times[39]);
}
