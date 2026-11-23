// Transport only: all selection semantics run in original ProGPU sources.
#include "progpu_native_text.hpp"
#include <cstdio>
#include <cstdlib>
#include <vector>
using namespace progpu::native::text;
template<class T>T read(){T v{};if(std::fread(&v,sizeof(v),1,stdin)!=1)std::exit(2);return v;}
void emit(std::uint32_t v){if(std::fwrite(&v,4,1,stdout)!=1)std::exit(3);}
int main(){std::uint32_t length{};while(std::fread(&length,4,1,stdin)==1){
 const open_type_tag script{read<std::uint32_t>()},language{read<std::uint32_t>()},feature{read<std::uint32_t>()};
 const auto target=static_cast<std::uint16_t>(read<std::uint32_t>());const auto capacity=read<std::uint32_t>(),fc=read<std::uint32_t>(),ec=read<std::uint32_t>(),nc=read<std::uint32_t>();
 std::vector<std::byte> bytes(length);if(std::fread(bytes.data(),1,length,stdin)!=length)return 4;
 std::vector<open_type_tag> features(fc),excluded(ec);for(auto& t:features)t={read<std::uint32_t>()};for(auto& t:excluded)t={read<std::uint32_t>()};
 std::vector<std::int16_t> coordinates(nc);for(auto& n:coordinates)n=static_cast<std::int16_t>(read<std::uint32_t>());
 open_type_layout_table_view table{};font_error error{};const bool ok=open_type_layout_table_view::try_create(bytes,table,&error);emit(static_cast<std::uint32_t>(error));if(!ok)continue;
 open_type_layout_table_view::lookup_selection_requirements r{};table.try_get_lookup_selection_requirements(script,language,features,coordinates,r,&error);emit(static_cast<std::uint32_t>(error));emit(r.lookup_capacity);
 for(int mode=0;mode<3;++mode){std::vector<std::uint16_t> out(capacity,0xbeef);std::uint32_t written{};
  if(mode==0)table.try_select_lookups(script,language,features,coordinates,out,written,&error);
  else if(mode==1)table.try_select_lookups_excluding(script,language,features,excluded,coordinates,out,written,&error);
  else table.try_select_feature_lookups(script,language,feature,coordinates,out,written,&error);
  emit(static_cast<std::uint32_t>(error));emit(written);for(auto v:out)emit(v);
 }
 bool contains{};table.try_feature_contains_lookup(script,language,feature,target,coordinates,contains,&error);emit(static_cast<std::uint32_t>(error));emit(contains);
 open_type_tag tag{};table.try_required_feature_for_lookup(script,language,target,coordinates,tag,contains,&error);emit(static_cast<std::uint32_t>(error));emit(contains);emit(tag.value);
 }return std::ferror(stdin)?5:0;}
