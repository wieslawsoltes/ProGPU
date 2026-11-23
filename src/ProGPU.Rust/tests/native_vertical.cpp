// Transport only: execute unchanged original static metrics and strict VVAR.
#include "progpu_native_text.hpp"
#include <array>
#include <bit>
#include <cstdio>
#include <cstdlib>
#include <vector>
using namespace progpu::native::text;
void emit(std::uint32_t v){if(std::fwrite(&v,4,1,stdout)!=1)std::exit(2);}
void scalar(float v){emit(std::bit_cast<std::uint32_t>(v));}
void delta(const sfnt_vertical_metrics_variation& v){
 for(auto n:{v.advance_height,v.top_side_bearing,v.bottom_side_bearing,v.vertical_origin_y})scalar(n);
 for(auto b:{v.uses_vvar,v.has_top_side_bearing,v.has_bottom_side_bearing,v.has_vertical_origin_y})emit(b);
}
int main(){std::array<std::uint32_t,5>a{};
 while(std::fread(a.data(),4,a.size(),stdin)==a.size()){
  std::vector<std::byte>d(a[0]);if(std::fread(d.data(),1,d.size(),stdin)!=d.size())return 3;
  sfnt_font_view f{};font_error e{};auto ok=sfnt_font_view::try_create(d,a[1],f,&e);emit(static_cast<std::uint32_t>(e));if(!ok)continue;
  const auto g=static_cast<std::uint16_t>(a[2]);std::vector<std::int16_t>coords(a[3],static_cast<std::int16_t>(a[4]));
  sfnt_vertical_header_metrics h{};ok=f.try_get_vertical_header_metrics(h);emit(ok);if(ok){emit(h.ascender);emit(h.descender);emit(h.line_gap);emit(h.advance_height_max);emit(h.number_of_vertical_metrics);}
  sfnt_vertical_glyph_metrics m{};ok=f.try_get_vertical_glyph_metrics(g,m);emit(ok);if(ok){emit(m.advance_height);emit(m.top_side_bearing);emit(m.has_top_side_bearing);}
  sfnt_glyph_bounds b{};ok=f.try_get_glyph_bounds(g,b);emit(ok);if(ok){emit(b.x_min);emit(b.y_min);emit(b.x_max);emit(b.y_max);}
  std::int32_t n{};ok=f.try_get_design_advance_height(g,n);emit(ok);if(ok)emit(n);
  ok=f.try_get_design_vertical_origin_y(g,n);emit(ok);if(ok)emit(n);
  std::uint16_t count{};bool uses{};ok=f.try_get_vertical_metrics_variation_region_count(coords,count,uses,&e);emit(static_cast<std::uint32_t>(e));if(ok){emit(uses);emit(count);}
  std::vector<float>scalars(count);sfnt_vertical_metrics_variation_instance v{};
  ok=f.try_prepare_vertical_metrics_variation(coords,scalars,v,&e);emit(static_cast<std::uint32_t>(e));if(!ok)continue;
  emit(v.uses_vvar());emit(v.region_count());for(auto s:scalars)scalar(s);
  sfnt_vertical_metrics_variation result{};ok=f.try_get_vertical_metrics_variation(g,v,result,&e);emit(static_cast<std::uint32_t>(e));if(ok)delta(result);
  const std::array<std::uint16_t,3>glyphs{0,g,1};
  for(std::size_t len:{2U,4U}){std::array<sfnt_vertical_metrics_variation,4>output{};output.fill({19,23,29,31,true,true,true,true});
   f.try_get_vertical_metrics_variation(glyphs,v,std::span(output).first(len),&e);emit(static_cast<std::uint32_t>(e));for(const auto& r:output)delta(r);
  }
 }
 return std::ferror(stdin)?4:0;
}
