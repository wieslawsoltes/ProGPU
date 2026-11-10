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
  }else if(a[0]==4){
   sfnt_font_view face{};auto ok=sfnt_font_view::try_create(b,0,face,&e);emit(static_cast<std::uint32_t>(e));if(!ok)continue;
   std::vector<std::int16_t> coords(a[3],static_cast<std::int16_t>(a[4]));
   const auto point_count=a[6];std::vector<sfnt_outline_point> original(point_count);std::vector<std::uint16_t> ends;
   for(std::uint32_t i=0;i<point_count;++i)original[i]={static_cast<std::int32_t>(i*11)-7,static_cast<std::int32_t>((i*i*7)%43)-20,static_cast<std::uint8_t>(i&1)};
   if(point_count)ends.push_back(static_cast<std::uint16_t>(point_count-1));
   sfnt_simple_glyph_variation_requirements sr{};face.try_get_simple_glyph_variation_requirements(a[2],point_count,sr,&e);
   std::vector<sfnt_gvar_tuple_header> sh(sr.tuple_header_count);std::vector<std::int16_t> sc(sr.region_coordinate_count),sx(sr.delta_count),sy(sr.delta_count);
   std::vector<std::uint32_t> sp(sr.point_number_count),pp(sr.point_number_count);std::vector<float> tx(sr.tuple_point_count),ty(sr.tuple_point_count);std::vector<std::uint8_t> touched(sr.tuple_point_count);
   std::vector<progpu_native_point> varied(point_count);
   ok=face.try_apply_simple_glyph_variations(a[2],coords,ends,original,varied,{sh,sc,sp,pp,sx,sy,tx,ty,touched},&e);emit(static_cast<std::uint32_t>(e));
   if(ok)for(auto p:varied){emit(std::bit_cast<std::uint32_t>(p.x));emit(std::bit_cast<std::uint32_t>(p.y));}
   sfnt_composite_glyph_variation_requirements cr{};face.try_get_composite_glyph_variation_requirements(a[2],a[5],cr,&e);
   sh.resize(cr.tuple_header_count);sc.resize(cr.region_coordinate_count);sp.resize(cr.point_number_count);pp.resize(cr.point_number_count);sx.resize(cr.delta_count);sy.resize(cr.delta_count);
   std::vector<progpu_native_point> offsets(a[5]);ok=face.try_get_composite_glyph_variation_offsets(a[2],coords,a[5],offsets,{sh,sc,sp,pp,sx,sy},&e);emit(static_cast<std::uint32_t>(e));
   if(ok)for(auto p:offsets){emit(std::bit_cast<std::uint32_t>(p.x));emit(std::bit_cast<std::uint32_t>(p.y));}
   sfnt_glyph_phantom_variation_requirements pr{};face.try_get_glyph_phantom_variation_requirements(a[2],a[7],pr,&e);
   sh.resize(pr.tuple_header_count);sc.resize(pr.region_coordinate_count);sp.resize(pr.point_number_count);pp.resize(pr.point_number_count);sx.resize(pr.delta_count);sy.resize(pr.delta_count);
   float left{},right{};ok=face.try_get_glyph_horizontal_phantom_deltas(a[2],coords,a[7],left,right,{sh,sc,sp,pp,sx,sy},&e);emit(static_cast<std::uint32_t>(e));
   if(ok){emit(std::bit_cast<std::uint32_t>(left));emit(std::bit_cast<std::uint32_t>(right));}
  }else if(a[0]==5){
   sfnt_font_view face{};auto ok=sfnt_font_view::try_create(b,a[5],face,&e);emit(static_cast<std::uint32_t>(e));if(!ok)continue;
   std::vector<std::int16_t> coords(a[3],static_cast<std::int16_t>(a[4]));
   sfnt_varied_glyph_requirements r{};face.try_get_varied_glyph_requirements(a[2],r,&e);
   std::vector<std::uint16_t> ends(r.outline.simple_contour_scratch_count);std::vector<sfnt_outline_point> original(r.outline.simple_point_scratch_count);
   std::vector<progpu_native_point> varied(r.varied_simple_point_count),offsets(r.component_offset_count),points(r.outline.point_count);
   std::vector<progpu_native_path_segment> segments(r.outline.path_segment_count);
   const auto& sr=r.simple_variation;const auto& cr=r.composite_variation;
   std::vector<sfnt_gvar_tuple_header> sh(sr.tuple_header_count),ch(cr.tuple_header_count);
   std::vector<std::int16_t> sc(sr.region_coordinate_count),sx(sr.delta_count),sy(sr.delta_count),cc(cr.region_coordinate_count),cx(cr.delta_count),cy(cr.delta_count);
   std::vector<std::uint32_t> sp(sr.point_number_count),pp(sr.point_number_count),cp(cr.point_number_count),pc(cr.point_number_count);
   std::vector<float> tx(sr.tuple_point_count),ty(sr.tuple_point_count);std::vector<std::uint8_t> touched(sr.tuple_point_count);
   std::uint32_t pw{},sw{};
   ok=face.try_decode_varied_glyph_outline(a[2],coords,{ends,original,varied,offsets,{sh,sc,sp,pp,sx,sy,tx,ty,touched},{ch,cc,cp,pc,cx,cy}},points,segments,pw,sw,&e);
   emit(static_cast<std::uint32_t>(e));if(!ok)continue;emit(pw);emit(sw);
   for(auto p:points){emit(std::bit_cast<std::uint32_t>(p.x));emit(std::bit_cast<std::uint32_t>(p.y));}
   if(std::fwrite(segments.data(),sizeof(progpu_native_path_segment),sw,stdout)!=sw)return 9;
  }else return 5;
 }
 return std::ferror(stdin)?6:0;
}
