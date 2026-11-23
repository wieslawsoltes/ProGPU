// Original native normalization; plan/input validation and allocation precede timing.
#include "progpu_native_text.hpp"
#include <algorithm>
#include <chrono>
#include <cstdio>
#include <cstdlib>
#include <fstream>
#include <iterator>
#include <string>
#include <vector>
using namespace progpu::native::text;
int main(int argc,char** argv){if(argc!=5)return 1;const std::string kind=argv[1],mode=argv[2];if(kind!="ascii"&&kind!="composed"&&kind!="reordered")return 2;if(mode!="nfd"&&mode!="nfc")return 3;
 const auto repeats=std::stoul(argv[3]);if(repeats==0||repeats>4096)return 4;
 const auto form=mode=="nfd"?unicode_normalization_form::canonical_decomposition:unicode_normalization_form::canonical_composition;
 const std::u16string pattern=kind=="ascii"?u"ProGPU sample 123 ":kind=="composed"?u"Åé각𝅗𝅥 日本語 ":u"A\u0315\u0301\u0327 क\u093c \u1100\u1161\u11a8 ";
 std::vector<std::uint16_t> input;for(std::size_t i=0;i<repeats;++i)for(auto c:pattern)input.push_back(c);
 unicode_decode_requirements req{};if(!try_get_utf16_decode_requirements(input,req))return 5;std::vector<unicode_scalar> scalars(req.scalar_count);std::uint32_t written{};if(!try_decode_utf16(input,scalars,written))return 6;
 std::ifstream stream(argv[4],std::ios::binary);std::vector<char> bytes((std::istreambuf_iterator<char>(stream)),{});unicode_normalization_data data{};if(!unicode_normalization_data::try_create({reinterpret_cast<const std::byte*>(bytes.data()),bytes.size()},data))return 7;
 unicode_normalization_requirements capacity{};if(!try_get_unicode_normalization_requirements(scalars,data,capacity))return 8;std::vector<unicode_scalar> output(capacity.scalar_capacity);
 auto execute=[&]{std::uint32_t count{};if(!try_normalize_unicode(scalars,data,form,output,count))std::exit(9);std::uint64_t hash=14695981039346656037ULL;for(std::uint32_t i=0;i<count;++i)hash=(hash^output[i].code_point)*1099511628211ULL;return std::pair{hash,count};};
 const auto identity=execute();for(int i=0;i<5;++i)if(execute()!=identity)return 10;std::vector<double> times;times.reserve(40);for(int i=0;i<40;++i){const auto start=std::chrono::steady_clock::now();const auto actual=execute();times.push_back(std::chrono::duration<double,std::micro>(std::chrono::steady_clock::now()-start).count());if(actual!=identity)return 11;}
 std::sort(times.begin(),times.end());std::printf("units=%zu scalars=%zu output=%u buffer_bytes=%zu checksum=%llu round_us_p50=%.3f p95=%.3f p99=%.3f\n",input.size(),scalars.size(),identity.second,output.size()*sizeof(unicode_scalar),static_cast<unsigned long long>(identity.first),times[19],times[37],times[39]);}
