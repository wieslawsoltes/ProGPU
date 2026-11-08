// Timing/transport only; invoke unchanged original simple text and metric APIs.
#include "progpu_native_text.hpp"
#include <algorithm>
#include <array>
#include <chrono>
#include <cstdio>
#include <cstdlib>
#include <vector>
using namespace progpu::native::text;
struct work { std::vector<char16_t> text;std::vector<std::uint16_t> clusters,glyphs;std::vector<sfnt_simple_glyph_metrics> metrics;std::vector<std::uint8_t> state;std::vector<std::int32_t> advances;std::uint16_t upem{},blank{},hyphen{}; };
std::uint64_t run(const sfnt_font_view& f,work& w){std::uint32_t count{};
 if(!try_build_sfnt_simple_glyph_run(f,w.text,w.blank,w.hyphen,w.clusters,w.glyphs,count)||count!=w.glyphs.size())std::exit(10);
 if(!try_fill_sfnt_simple_glyph_advances(w.text,w.clusters,w.glyphs,w.metrics,w.upem,13.25,1.25,false,w.state,w.advances))std::exit(11);
 std::uint64_t hash=14695981039346656037ULL;auto add=[&](std::uint32_t v){hash=(hash^v)*1099511628211ULL;};
 for(auto v:w.clusters)add(v);for(auto v:w.glyphs)add(v);for(auto v:w.advances)add(v);for(auto v:w.state)add(v);return hash;
}
int main(int argc,char**argv){
 if(argc<2||argc>4)return 1;auto* f=std::fopen(argv[1],"rb");if(!f)return 2;
 if(std::fseek(f,0,SEEK_END))return 3;const auto size=std::ftell(f);if(size<0||std::fseek(f,0,SEEK_SET))return 4;
 std::vector<std::byte> data(static_cast<std::size_t>(size));if(std::fread(data.data(),1,data.size(),f)!=data.size())return 5;std::fclose(f);
 sfnt_font_view font{};if(!sfnt_font_view::try_create(data,argc>=3?static_cast<std::uint32_t>(std::strtoul(argv[2],nullptr,10)):0,font))return 6;
 const auto repeats=argc>=4?std::strtoul(argv[3],nullptr,10):256UL;if(repeats==0||repeats>4096)return 7;
 constexpr std::array<char16_t,14> pattern{0x41,0x56,0x57,0x20,0x69,0x6a,0xad,10,0x85,0xd83d,0xde00,0xd800,0x200d,0x5d0};work w;
 for(unsigned long i=0;i<repeats;++i)w.text.insert(w.text.end(),pattern.begin(),pattern.end());
 sfnt_simple_glyph_run_requirements r{};sfnt_header_metrics header{};
 if(!try_get_sfnt_simple_glyph_run_requirements(w.text,r)||!font.try_get_header_metrics(header)||!font.try_get_glyph_index(0x20,w.blank)||!font.try_get_glyph_index(0x2d,w.hyphen))return 8;
 w.upem=header.units_per_em;w.clusters.resize(r.cluster_map_count);w.glyphs.resize(r.glyph_count);w.metrics.resize(r.glyph_count);w.state.resize(r.glyph_count);w.advances.resize(r.glyph_count);
 std::uint32_t count{};if(!try_build_sfnt_simple_glyph_run(font,w.text,w.blank,w.hyphen,w.clusters,w.glyphs,count))return 9;
 for(std::size_t i=0;i<w.glyphs.size();++i){sfnt_horizontal_glyph_metrics h{};std::int32_t height{};if(!font.try_get_horizontal_glyph_metrics(w.glyphs[i],h)||!font.try_get_design_advance_height(w.glyphs[i],height)||height<0)return 12;w.metrics[i]={h.advance_width,static_cast<std::uint32_t>(height)};}
 const auto checksum=run(font,w);for(unsigned i=0;i<5;++i)if(run(font,w)!=checksum)return 13;
 if(const auto* value=std::getenv("PROGPU_RUST_PROFILE_ROUNDS")){const auto rounds=std::strtoull(value,nullptr,10);for(unsigned long long i=0;i<rounds;++i)if(run(font,w)!=checksum)return 13;std::printf("profile_rounds=%llu checksum=%llu\n",rounds,static_cast<unsigned long long>(checksum));return 0;}
 std::vector<double> times;for(unsigned i=0;i<40;++i){const auto start=std::chrono::steady_clock::now();const auto actual=run(font,w);times.push_back(std::chrono::duration<double,std::micro>(std::chrono::steady_clock::now()-start).count());if(actual!=checksum)return 14;}
 std::sort(times.begin(),times.end());std::printf("units=%zu glyphs=%zu scratch_bytes=%zu checksum=%llu round_us_p50=%.3f p95=%.3f p99=%.3f\n",w.text.size(),w.glyphs.size(),w.state.size(),static_cast<unsigned long long>(checksum),times[19],times[37],times[39]);
}
