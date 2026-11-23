// Test bridge to unchanged original C++ implementations with compiler defaults.
#include "progpu_native_text.hpp"
#include <algorithm>
#include <cstdio>
#include <cstdlib>
#include <vector>
using namespace progpu::native::text;
template<class T> T read(){T v{};if(std::fread(&v,sizeof(v),1,stdin)!=1)std::exit(2);return v;}
void emit(std::uint32_t v){if(std::fwrite(&v,4,1,stdout)!=1)std::exit(3);}
void masks(const open_type_glyph_set_digest& d){for(auto v:{d.shift2,d.shift4,d.shift0,d.shift6,d.shift10}){emit(static_cast<std::uint32_t>(v));emit(static_cast<std::uint32_t>(v>>32));}}
int main(){std::uint32_t length{};
 while(std::fread(&length,4,1,stdin)==1){
  const auto index=static_cast<std::uint16_t>(read<std::uint32_t>()),extension=static_cast<std::uint16_t>(read<std::uint32_t>());
  const auto sc=read<std::uint32_t>(),cc=read<std::uint32_t>(),qc=read<std::uint32_t>();
  std::vector<std::byte> data(length);if(std::fread(data.data(),1,length,stdin)!=length)return 4;
  std::vector<std::uint16_t> queries(qc);for(auto& q:queries)q=static_cast<std::uint16_t>(read<std::uint32_t>());
  font_error error{};open_type_layout_table_view table{};
  bool ok=open_type_layout_table_view::try_create(data,table,&error);emit(static_cast<std::uint32_t>(error));if(!ok)continue;
  open_type_glyph_set_digest digest{};bool has{};
  ok=table.try_get_lookup_digest(index,extension,digest,has,&error);emit(static_cast<std::uint32_t>(error));if(!ok)continue;
  emit(has);if(has){masks(digest);for(auto q:queries){emit(digest.may_have(q));open_type_glyph_set_digest other{};other.add_range(q,static_cast<std::uint16_t>(std::min(65535U,static_cast<std::uint32_t>(q)+3U)));other.add(static_cast<std::uint16_t>(q+4096U));emit(digest.may_intersect(other));}}
  open_type_lookup_view lookup{};open_type_coverage_view coverage{};
  ok=table.try_get_single_subtable_coverage(index,extension,lookup,coverage,has,&error);emit(static_cast<std::uint32_t>(error));if(!ok)return 5;
  emit(lookup.flags);emit(lookup.type);emit(lookup.subtable_count);emit(has);if(has)for(auto q:queries)emit(static_cast<std::uint32_t>(coverage.find(q)));
  open_type_context_accelerator_requirements r{};
  ok=table.try_get_lookup_context_accelerator_requirements(index,extension,r,&error);emit(static_cast<std::uint32_t>(error));if(!ok)return 6;
  emit(r.supported);if(r.supported){emit(r.subtable_capacity);emit(r.coverage_capacity);}
  std::vector<open_type_context_subtable_requirement> s(sc,{0x12345678,0x23456789,0x3456,0x4567});
  std::vector<open_type_context_coverage_requirement> c(cc);std::uint16_t flags{};
  table.try_build_lookup_context_accelerator(index,extension,s,c,flags,has,&error);
  emit(static_cast<std::uint32_t>(error));emit(has);emit(flags);
  for(auto v:s){emit(v.coverage_offset);emit(v.coverage_count);emit(v.backtrack_count);emit(v.input_count);}
  for(auto v:c){masks(v.digest);for(auto q:queries)emit(static_cast<std::uint32_t>(v.coverage.find(q)));}
 }return std::ferror(stdin)?7:0;
}
