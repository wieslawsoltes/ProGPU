// Original native resolver/decoding; allocation stays outside timing.
#include "progpu_native_text.hpp"
#include "progpu_native_unicode_line_break_internal.hpp"
#include <algorithm>
#include <chrono>
#include <cstdio>
#include <cstdlib>
#include <string>
#include <vector>
using namespace progpu::native::text;
int main(int argc,char** argv){if(argc!=4)return 1;const std::string kind=argv[1],mode=argv[2];if(kind!="ascii"&&kind!="mixed"&&kind!="emoji")return 2;if(mode!="unicode"&&mode!="edit")return 3;
 const auto repeats=std::stoul(argv[3]);if(repeats==0||repeats>4096)return 4;
 const std::u16string pattern=kind=="ascii"?u"ProGPU sample ($123.45) - end ":kind=="mixed"?u"A\u0301 العربية 日本語\r\n क्क ":u"🇵🇱👩\u0301\u200d👧👍🏽🇦 \u1100\u1161\u11a8 ";
 std::vector<std::uint16_t> input;for(std::size_t i=0;i<repeats;++i)for(auto c:pattern)input.push_back(c);
 unicode_decode_requirements req{};if(!try_get_utf16_decode_requirements(input,req))return 5;std::vector<unicode_scalar> scalars(req.scalar_count);std::uint32_t written{};if(!try_decode_utf16(input,scalars,written))return 6;
 std::vector<unicode_line_break_class> classes(scalars.size());std::vector<text_line_break_kind> breaks(scalars.size());
 auto execute=[&]{const bool ok=mode=="edit"?detail::try_resolve_edit_selection_line_breaks(scalars,classes,breaks):try_resolve_unicode_line_breaks(scalars,classes,breaks);if(!ok)std::exit(7);std::uint64_t hash=14695981039346656037ULL;for(std::size_t i=0;i<scalars.size();++i){hash=(hash^std::uint8_t(classes[i]))*1099511628211ULL;hash=(hash^std::uint8_t(breaks[i]))*1099511628211ULL;}return hash;};
 const auto checksum=execute();for(int i=0;i<5;++i)if(execute()!=checksum)return 8;
 std::vector<double> times;times.reserve(40);for(int i=0;i<40;++i){const auto start=std::chrono::steady_clock::now();const auto actual=execute();times.push_back(std::chrono::duration<double,std::micro>(std::chrono::steady_clock::now()-start).count());if(actual!=checksum)return 9;}
 std::sort(times.begin(),times.end());std::printf("units=%zu scalars=%zu scratch_bytes=%zu checksum=%llu round_us_p50=%.3f p95=%.3f p99=%.3f\n",input.size(),scalars.size(),classes.size(),static_cast<unsigned long long>(checksum),times[19],times[37],times[39]);}
