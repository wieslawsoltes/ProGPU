// Transport only: unchanged original executor, parsers and Unicode classifiers.
#include "progpu_native_text.hpp"
#include <algorithm>
#include <chrono>
#include <cstdio>
#include <cstdlib>
#include <vector>
using namespace progpu::native::text;
template<class T> T read(){T v{};if(std::fread(&v,sizeof(v),1,stdin)!=1)std::exit(2);return v;}
void emit(std::uint32_t v){if(std::fwrite(&v,4,1,stdout)!=1)std::exit(3);}
std::vector<std::byte> bytes(std::uint32_t n){std::vector<std::byte>b(n);if(std::fread(b.data(),1,n,stdin)!=n)std::exit(4);return b;}
int main(){std::uint32_t n{};while(std::fread(&n,4,1,stdin)==1){
 const auto gn=read<std::uint32_t>(),index=read<std::uint32_t>(),position=read<std::uint32_t>(),capacity=read<std::uint32_t>();
 auto count=read<std::uint32_t>();const auto bits=read<std::uint32_t>(),alt=read<std::uint32_t>(),required=read<std::uint32_t>(),syllable=read<std::uint32_t>();
 auto end=read<std::uint32_t>(),random=read<std::uint32_t>();const auto data=bytes(n),gd=bytes(gn);
 std::vector<shaping_glyph> storage(capacity);for(auto& g:storage){g.glyph_id=read<std::uint32_t>();g.code_point=read<std::uint32_t>();g.cluster=read<std::int32_t>();g.flags=static_cast<shaping_glyph_flags>(read<std::uint32_t>());g.advance_x=read<std::int32_t>();g.advance_y=read<std::int32_t>();g.offset_x=read<std::int32_t>();g.offset_y=read<std::int32_t>();}
 font_error error{};open_type_layout_table_view table{};if(!open_type_layout_table_view::try_create(data,table,&error)){emit(static_cast<std::uint32_t>(error));continue;}emit(0);
 open_type_gdef_view gdef{};if(gn&&!open_type_gdef_view::try_create(gd,gdef,&error)){emit(static_cast<std::uint32_t>(error));continue;}emit(0);
 open_type_gsub_apply_options o{};o.gdef=gn?&gdef:nullptr;o.alternate_value=alt;o.required_glyph_flags=required;o.restricted_syllable=static_cast<std::uint8_t>(syllable);
 o.mark_substituted=bits&1;o.track_fallback_mark_metadata=bits&2;o.track_arabic_stretch_metadata=bits&4;o.random_alternate=bits&8;o.restrict_to_syllable=bits&16;o.track_substitution_provenance=bits&32;o.context_match_end=bits&64?&end:nullptr;o.random_state=bits&128?&random:nullptr;
 open_type_glyph_set_digest digest{},empty_digest{};open_type_coverage_view coverage{},empty_coverage{};open_type_lookup_view lookup{};bool has{};
 if(bits&256){table.try_get_lookup_digest(static_cast<std::uint16_t>(index),7,digest,has);if(has)o.lookup_digest=&digest;}
 if(bits&512){table.try_get_single_subtable_coverage(static_cast<std::uint16_t>(index),7,lookup,coverage,has);if(has)o.lookup_coverage=&coverage;}
 std::vector<open_type_context_subtable_requirement> subs;std::vector<open_type_context_coverage_requirement> covs;
 if(bits&1024){open_type_context_accelerator_requirements r{};table.try_get_lookup_context_accelerator_requirements(static_cast<std::uint16_t>(index),7,r);if(r.supported){subs.resize(r.subtable_capacity);covs.resize(r.coverage_capacity);std::uint16_t flags{};table.try_build_lookup_context_accelerator(static_cast<std::uint16_t>(index),7,subs,covs,flags,has);if(has){o.lookup_context_subtables=subs;o.lookup_context_coverages=covs;}}}
 if(bits&2048)o.lookup_digest=&empty_digest;if(bits&4096)o.lookup_coverage=&empty_coverage;
 const auto initial_storage=storage;const auto initial_count=count,initial_end=end,initial_random=random;
 bool applied=true;const bool ok=position==0xffffffffU?try_apply_open_type_gsub_lookup(table,static_cast<std::uint16_t>(index),storage,count,o,applied,&error):try_apply_open_type_gsub_lookup_at(table,static_cast<std::uint16_t>(index),storage,count,position,o,applied,&error);
 emit(ok);emit(static_cast<std::uint32_t>(error));emit(count);emit(applied);emit(end);emit(random);
 for(auto g:storage){emit(g.glyph_id);emit(g.code_point);emit(static_cast<std::uint32_t>(g.cluster));emit(static_cast<std::uint32_t>(g.flags));emit(static_cast<std::uint32_t>(g.advance_x));emit(static_cast<std::uint32_t>(g.advance_y));emit(static_cast<std::uint32_t>(g.offset_x));emit(static_cast<std::uint32_t>(g.offset_y));}
 if(std::getenv("PROGPU_RUST_GSUB_BENCHMARK")){
  std::vector<double> times;for(unsigned round=0;round<45;++round){storage=initial_storage;count=initial_count;end=initial_end;random=initial_random;
   const auto start=std::chrono::steady_clock::now();const bool success=position==0xffffffffU?try_apply_open_type_gsub_lookup(table,static_cast<std::uint16_t>(index),storage,count,o,applied,&error):try_apply_open_type_gsub_lookup_at(table,static_cast<std::uint16_t>(index),storage,count,position,o,applied,&error);
   const double us=std::chrono::duration<double,std::micro>(std::chrono::steady_clock::now()-start).count();if(!success)return 9;if(round>=5)times.push_back(us);
  }std::sort(times.begin(),times.end());std::fprintf(stderr,"execute_p50_us=%.3f execute_p95_us=%.3f execute_p99_us=%.3f\n",times[19],times[37],times[39]);
 }
 }return std::ferror(stdin)?7:0;}
