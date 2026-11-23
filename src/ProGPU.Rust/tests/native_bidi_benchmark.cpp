// Original native resolver and decoder; input setup stays outside timing.
#include "progpu_native_text.hpp"
#include <algorithm>
#include <chrono>
#include <cstdio>
#include <cstdlib>
#include <string>
#include <vector>
using namespace progpu::native::text;
int main(int argc,char** argv){if(argc!=3)return 1;const std::string kind=argv[1];if(kind!="ascii"&&kind!="mixed"&&kind!="isolates")return 2;
 const auto repeats=std::stoul(argv[2]);if(repeats==0||repeats>4096)return 3;
 const std::u16string pattern=kind=="ascii"?u"ProGPU sample 123 ":kind=="mixed"?u"abc אבג (123) العربية A\u0301 日本語 😀 ":u"A\u2068אב \u2066abc (123)\u2069 ד\u2069 z ";
 std::vector<std::uint16_t> input;for(std::size_t i=0;i<repeats;++i)for(auto c:pattern)input.push_back(c);
 unicode_decode_requirements decode{};if(!try_get_utf16_decode_requirements(input,decode))return 4;
 std::vector<unicode_scalar> scalars(decode.scalar_count);std::uint32_t written{};if(!try_decode_utf16(input,scalars,written))return 5;
 unicode_bidi_requirements r{};if(!try_get_unicode_bidi_requirements(scalars,r))return 6;
 std::vector<unicode_bidi_unit> units(r.unit_count);std::vector<std::uint32_t> indices(r.index_count);std::vector<unicode_bidi_level_run> runs(r.run_count);std::vector<unicode_bidi_bracket_pair> pairs(r.bracket_pair_count);std::vector<unicode_bidi_level> output(r.unit_count);
 auto execute=[&]{std::int8_t paragraph{};if(!try_resolve_unicode_bidi(scalars,2,{units,indices,runs,pairs},output,paragraph,written)||written!=scalars.size())std::exit(7);
 std::uint64_t hash=14695981039346656037ULL;auto add=[&](std::uint32_t v){hash=(hash^v)*1099511628211ULL;};add(static_cast<std::uint32_t>(paragraph));for(const auto& v:output){add(v.input_index);add(v.input_length);add(static_cast<std::uint32_t>(v.level));add(v.reserved);}return hash;};
 const auto checksum=execute();for(int i=0;i<5;++i)if(execute()!=checksum)return 8;
 std::vector<double> times;times.reserve(40);for(int i=0;i<40;++i){auto start=std::chrono::steady_clock::now();auto actual=execute();times.push_back(std::chrono::duration<double,std::micro>(std::chrono::steady_clock::now()-start).count());if(actual!=checksum)return 9;}
 std::sort(times.begin(),times.end());const auto bytes=units.size()*sizeof(unicode_bidi_unit)+indices.size()*4+runs.size()*sizeof(unicode_bidi_level_run)+pairs.size()*sizeof(unicode_bidi_bracket_pair);
 std::printf("units=%zu scalars=%zu scratch_bytes=%zu checksum=%llu round_us_p50=%.3f p95=%.3f p99=%.3f\n",input.size(),scalars.size(),bytes,static_cast<unsigned long long>(checksum),times[19],times[37],times[39]);}
