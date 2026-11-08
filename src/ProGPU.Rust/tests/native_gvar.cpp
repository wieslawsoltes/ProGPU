// Original packed variation and IUP sources, unchanged; exact transport only.
#include "progpu_native_text.hpp"
#include <array>
#include <bit>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <vector>
using namespace progpu::native::text;
void emit(std::uint32_t v){if(std::fwrite(&v,4,1,stdout)!=1)std::exit(2);}
int main(){
 std::array<std::uint32_t,8>a{};
 while(std::fread(a.data(),4,8,stdin)==8){
  std::vector<std::byte>b(a[1]);if(std::fread(b.data(),1,b.size(),stdin)!=b.size())return 3;
  font_error e{};
  if(a[0]==0){sfnt_packed_point_requirements r{};auto ok=sfnt_packed_variation_data::try_get_point_requirements(b,r,&e);
   emit(static_cast<std::uint32_t>(e));if(!ok)continue;emit(r.point_count);emit(r.bytes_consumed);emit(r.all_points);
   std::vector<std::uint32_t> output(a[3],0x12345678U);std::uint32_t written{};std::size_t consumed{};
   ok=sfnt_packed_variation_data::try_decode_points(b,output,written,consumed,&e);emit(static_cast<std::uint32_t>(e));
   for(auto v:output)emit(v);
  }else if(a[0]==1){sfnt_packed_delta_requirements r{};auto ok=sfnt_packed_variation_data::try_get_delta_requirements(b,a[2],r,&e);
   emit(static_cast<std::uint32_t>(e));if(!ok)continue;emit(r.delta_count);emit(r.bytes_consumed);
   std::vector<std::int16_t> output(a[3],-12345);std::uint32_t written{};std::size_t consumed{};
   ok=sfnt_packed_variation_data::try_decode_deltas(b,output,a[2],written,consumed,&e);emit(static_cast<std::uint32_t>(e));
   for(auto v:output)emit(static_cast<std::uint32_t>(v));
  }else if(a[0]==2){
   std::vector<progpu_native_point> points(a[7]);std::vector<std::uint16_t> ends(a[6]);std::vector<float>x(a[3]),y(a[4]);std::vector<std::uint8_t>touched(a[5]);std::size_t cursor=0;
   auto copy=[&](void* out,std::size_t n){if(n>b.size()-cursor)std::exit(4);if(n)std::memcpy(out,b.data()+cursor,n);cursor+=n;};
   copy(points.data(),points.size()*sizeof(progpu_native_point));copy(ends.data(),ends.size()*2);copy(x.data(),x.size()*4);copy(y.data(),y.size()*4);copy(touched.data(),touched.size());
   sfnt_gvar_deltas::try_infer_untouched(points,ends,x,y,touched,&e);emit(static_cast<std::uint32_t>(e));
   for(float v:x)emit(std::bit_cast<std::uint32_t>(v));for(float v:y)emit(std::bit_cast<std::uint32_t>(v));
  }else if(a[0]==3){
   sfnt_font_view face{};auto ok=sfnt_font_view::try_create(b,0,face,&e);emit(static_cast<std::uint32_t>(e));if(!ok)continue;
   sfnt_gvar_header h{};ok=face.try_get_gvar_header(h,&e);emit(static_cast<std::uint32_t>(e));if(!ok)continue;
   sfnt_table_view table{};const bool present=face.try_get_table(open_type_tag::from_chars('g','v','a','r'),table);emit(present);if(!present)continue;
   emit(h.axis_count);emit(h.shared_tuple_count);emit(h.glyph_count);emit(h.uses_long_offsets);
   std::vector<std::int16_t> shared(a[4],-12345);std::uint16_t written{};face.try_decode_gvar_shared_tuple(a[3],shared,written,&e);emit(static_cast<std::uint32_t>(e));for(auto v:shared)emit(static_cast<std::uint32_t>(v));
   sfnt_glyph_variation_data_view view{};ok=face.try_get_glyph_variation_data(a[2],view,&e);emit(static_cast<std::uint32_t>(e));
   if(ok){emit(view.tuple_count);emit(view.serialized_data_offset);emit(view.has_shared_point_numbers);emit(view.bytes.size());if(std::fwrite(view.bytes.data(),1,view.bytes.size(),stdout)!=view.bytes.size())return 7;}
   sfnt_gvar_tuple_requirements r{};ok=face.try_get_glyph_variation_tuple_requirements(a[2],r,&e);emit(static_cast<std::uint32_t>(e));if(!ok)continue;
   emit(r.tuple_count);emit(r.region_coordinate_count);std::vector<sfnt_gvar_tuple_header> headers(r.tuple_count);std::vector<std::int16_t> regions(r.region_coordinate_count);std::uint16_t hw{};std::uint32_t cw{};
   if(!face.try_decode_glyph_variation_tuple_headers(a[2],headers,regions,hw,cw,&e))return 8;
   for(const auto t:headers){emit(t.region_coordinate_offset);emit(t.serialized_data_size);emit(t.flags);}
   for(auto v:regions)emit(static_cast<std::uint32_t>(v));std::array<std::int16_t,2> coords{static_cast<std::int16_t>(a[5]),static_cast<std::int16_t>(a[6])};
   for(const auto t:headers)emit(std::bit_cast<std::uint32_t>(sfnt_gvar_tuple_data::calculate_scalar(std::span(coords).first(a[7]),std::span(regions).subspan(t.region_coordinate_offset,h.axis_count*3U))));
  }else return 5;
 }
 return std::ferror(stdin)?6:0;
}
