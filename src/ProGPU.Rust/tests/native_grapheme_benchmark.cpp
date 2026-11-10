// Original native segmentation/decoding, with all allocation outside timing.
#include "progpu_native_text.hpp"
#include "Unicode/progpu_native_unicode_grapheme_internal.hpp"
#include <algorithm>
#include <chrono>
#include <cstdio>
#include <cstdlib>
#include <string>
#include <vector>
using namespace progpu::native::text;
int main(int argc,char** argv){if(argc!=4)return 1;const std::string kind=argv[1],mode=argv[2];if(kind!="ascii"&&kind!="mixed"&&kind!="emoji")return 2;if(mode!="clusters"&&mode!="managed"&&mode!="starts")return 3;
 const auto repeats=std::stoul(argv[3]);if(repeats==0||repeats>4096)return 4;
 const std::u16string pattern=kind=="ascii"?u"ProGPU sample 123 ":kind=="mixed"?u"A\u0301 العربية 日本語\r\n क्क ":u"🇵🇱👩\u0301\u200d👧👍🏽🇦 \u1100\u1161\u11a8 ";
 std::vector<std::uint16_t> input;for(std::size_t i=0;i<repeats;++i)for(auto c:pattern)input.push_back(c);
 unicode_decode_requirements decode{};if(!try_get_utf16_decode_requirements(input,decode))return 5;std::vector<unicode_scalar> scalars(decode.scalar_count);std::uint32_t written{};if(!try_decode_utf16(input,scalars,written))return 6;
 std::vector<unicode_grapheme_cluster> output(scalars.size());std::vector<std::uint8_t> starts(input.size());
 auto segment=[&]{return mode=="managed"?detail::try_segment_managed_compatible_graphemes(scalars,output,written):try_segment_unicode_graphemes(scalars,output,written);};if(!segment())return 7;const auto count=written;output.resize(count);
 auto execute=[&]{std::uint64_t hash=14695981039346656037ULL;auto add=[&](std::uint32_t v){hash=(hash^v)*1099511628211ULL;};if(mode=="starts"){if(!try_get_utf16_grapheme_starts(input,starts))std::exit(8);for(auto v:starts)add(v);}else{if(!segment()||written!=count)std::exit(9);for(const auto& v:output){add(v.input_index);add(v.input_length);add(v.scalar_index);add(v.scalar_count);}}return hash;};
 const auto checksum=execute();for(int i=0;i<5;++i)if(execute()!=checksum)return 10;std::vector<double> times;times.reserve(40);for(int i=0;i<40;++i){const auto start=std::chrono::steady_clock::now();const auto actual=execute();times.push_back(std::chrono::duration<double,std::micro>(std::chrono::steady_clock::now()-start).count());if(actual!=checksum)return 11;}std::sort(times.begin(),times.end());std::printf("units=%zu scalars=%zu clusters=%u scratch_bytes=0 checksum=%llu round_us_p50=%.3f p95=%.3f p99=%.3f\n",input.size(),scalars.size(),count,static_cast<unsigned long long>(checksum),times[19],times[37],times[39]);}
