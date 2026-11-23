// Original APIs only; input construction and allocations stay outside timing.
#include "progpu_native_text.hpp"
#include <algorithm>
#include <chrono>
#include <cstdio>
#include <cstdlib>
#include <string>
#include <vector>
using namespace progpu::native::text;
int main(int argc,char** argv) {
    if(argc!=4)return 1;
    const std::string kind=argv[1], encoding=argv[2];
    if((kind!="ascii"&&kind!="mixed")||(encoding!="8"&&encoding!="16"))return 2;
    const auto repeats=std::stoul(argv[3]);if(repeats==0||repeats>4096)return 3;
    const bool wide=encoding=="16";
    const std::u8string p8=kind=="ascii"?u8"ProGPU sample 123 ":u8" A\u0301 العربية 日本語 😀 \U0010ffff";
    const std::u16string p16=kind=="ascii"?u"ProGPU sample 123 ":u" A\u0301 العربية 日本語 😀 \U0010ffff";
    std::vector<std::byte> input8;std::vector<std::uint16_t> input16;
    for(std::size_t i=0;i<repeats;++i){for(auto c:p8)input8.push_back(static_cast<std::byte>(c));for(auto c:p16)input16.push_back(c);}
    unicode_decode_requirements req{};
    if(!(wide?try_get_utf16_decode_requirements(input16,req):try_get_utf8_decode_requirements(input8,req)))return 4;
    std::vector<unicode_scalar> scalars(req.scalar_count);std::vector<unicode_script_run> runs(req.scalar_count);
    std::uint32_t run_count{};
    auto execute=[&]{std::uint32_t written{};if(!(wide?try_decode_utf16(input16,scalars,written):try_decode_utf8(input8,scalars,written))||written!=req.scalar_count||!try_itemize_unicode_scripts(scalars,runs,run_count))std::exit(5);
        std::uint64_t hash=14695981039346656037ULL;auto add=[&](std::uint32_t v){hash=(hash^v)*1099511628211ULL;};
        for(const auto& s:scalars){add(s.code_point);add(s.input_index);add(s.input_length);add(s.canonical_combining_class);add(s.reserved);add(s.script.value);}
        for(std::size_t i=0;i<run_count;++i){const auto& r=runs[i];add(r.scalar_start);add(r.scalar_count);add(r.input_start);add(r.input_length);add(r.script.value);}return hash;};
    const auto checksum=execute();for(int i=0;i<5;++i)if(execute()!=checksum)return 6;
    std::vector<double> times;times.reserve(40);
    for(int i=0;i<40;++i){auto start=std::chrono::steady_clock::now();auto actual=execute();times.push_back(std::chrono::duration<double,std::micro>(std::chrono::steady_clock::now()-start).count());if(actual!=checksum)return 7;}
    std::sort(times.begin(),times.end());std::printf("units=%zu scalars=%u runs=%u checksum=%llu round_us_p50=%.3f p95=%.3f p99=%.3f\n",wide?input16.size():input8.size(),req.scalar_count,run_count,static_cast<unsigned long long>(checksum),times[19],times[37],times[39]);
}
