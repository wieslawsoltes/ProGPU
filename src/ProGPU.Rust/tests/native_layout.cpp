// Invoke the unchanged original implementations with the compiler's defaults.
#include "progpu_native_text.hpp"
#include <cstdio>
#include <cstdlib>
#include <vector>
using namespace progpu::native::text;
template<class T> T read() {T v{};if(std::fread(&v,sizeof(v),1,stdin)!=1)std::exit(2);return v;}
void emit(std::uint32_t v) {if(std::fwrite(&v,4,1,stdout)!=1)std::exit(3);}
int main(){std::uint32_t kind{};
 while(std::fread(&kind,4,1,stdin)==1){
  if(kind==4){const auto a=read<std::uint32_t>(),b=read<std::uint32_t>(),c=read<std::uint32_t>();emit(is_open_type_gdef_blocklisted(a,b,c));continue;}
  const auto offset=read<std::uint64_t>();const auto length=read<std::uint32_t>();
  std::vector<std::byte> data(length);if(std::fread(data.data(),1,length,stdin)!=length)return 4;
  const auto count=read<std::uint32_t>();std::vector<std::uint32_t> queries(count);for(auto& q:queries)q=read<std::uint32_t>();
  font_error error{};bool ok{};
  open_type_coverage_view coverage{};open_type_class_definition_view classes{};open_type_layout_table_view layout{};open_type_gdef_view gdef{};
  if(kind==0)ok=open_type_coverage_view::try_create(data,static_cast<std::size_t>(offset),coverage,&error);
  else if(kind==1)ok=open_type_class_definition_view::try_create(data,static_cast<std::size_t>(offset),classes,&error);
  else if(kind==2)ok=open_type_layout_table_view::try_create(data,layout,&error);
  else if(kind==3)ok=open_type_gdef_view::try_create(data,gdef,&error);
  else return 5;
  emit(static_cast<std::uint32_t>(error));if(!ok)continue;
  if(kind==2)emit(layout.lookup_count());if(kind==3)emit(gdef.mark_set_count());
  for(auto q:queries){const auto glyph=static_cast<std::uint16_t>(q);
   if(kind==0)emit(static_cast<std::uint32_t>(coverage.find(glyph)));
   else if(kind==1)emit(classes.get(glyph));
   else if(kind==3){emit(static_cast<std::uint16_t>(gdef.glyph_class(glyph)));emit(gdef.mark_attachment_class(glyph));emit(gdef.is_in_mark_set(static_cast<std::uint16_t>(q>>16),glyph));}
   else {open_type_lookup_view lookup{};ok=layout.try_get_lookup(glyph,lookup,&error);emit(static_cast<std::uint32_t>(error));if(!ok)continue;
    emit(static_cast<std::uint32_t>(lookup.offset));emit(lookup.type);emit(lookup.flags);emit(lookup.subtable_count);emit(lookup.mark_filtering_set);
    for(std::uint32_t i=0;i<=lookup.subtable_count;++i){std::size_t at{};lookup.try_get_subtable(static_cast<std::uint16_t>(i),at,&error);emit(static_cast<std::uint32_t>(error));emit(static_cast<std::uint32_t>(at));}
   }
  }
 }return std::ferror(stdin)?6:0;
}
