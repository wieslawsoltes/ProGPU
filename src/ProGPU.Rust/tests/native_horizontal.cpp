// Original horizontal metrics/HVAR/phantom sources; result transport only.
#include "progpu_native_text.hpp"
#include <array>
#include <bit>
#include <cstdio>
#include <cstdlib>
#include <vector>
using namespace progpu::native::text;
void emit(std::uint32_t v){if(std::fwrite(&v,4,1,stdout)!=1)std::exit(2);}
void scalar(float v){emit(std::bit_cast<std::uint32_t>(v));}
struct phantom_scratch {
 std::vector<sfnt_gvar_tuple_header> h;std::vector<std::int16_t> c,x,y;std::vector<std::uint32_t> p,q;
 explicit phantom_scratch(const sfnt_design_advance_width_requirements& r):h(r.phantom.tuple_header_count),c(r.phantom.region_coordinate_count),x(r.phantom.delta_count),y(r.phantom.delta_count),p(r.phantom.point_number_count),q(r.phantom.point_number_count){}
 sfnt_glyph_phantom_variation_scratch get(){return {h,c,p,q,x,y};}
};
int main(){std::array<std::uint32_t,5>a{};
 while(std::fread(a.data(),4,a.size(),stdin)==a.size()){
  std::vector<std::byte>d(a[0]);if(std::fread(d.data(),1,d.size(),stdin)!=d.size())return 3;
  sfnt_font_view f{};font_error e{};auto ok=sfnt_font_view::try_create(d,a[1],f,&e);emit(static_cast<std::uint32_t>(e));if(!ok)continue;
  const auto glyph=static_cast<std::uint16_t>(a[2]);std::vector<std::int16_t>coords(a[3],static_cast<std::int16_t>(a[4]));
  std::uint32_t items{};ok=f.try_get_glyph_variation_item_count(glyph,items,&e);emit(static_cast<std::uint32_t>(e));if(ok)emit(items);
  float value{};bool uses{};ok=f.try_get_horizontal_advance_variation(glyph,coords,value,uses,&e);emit(static_cast<std::uint32_t>(e));if(ok){emit(uses);scalar(value);}
  std::uint16_t count{};ok=f.try_get_horizontal_advance_variation_region_count(coords,count,uses,&e);emit(static_cast<std::uint32_t>(e));if(ok){emit(uses);emit(count);}
  auto result=[&](bool success){emit(static_cast<std::uint32_t>(e));if(success)scalar(value);};
  result(f.try_get_design_advance_width(glyph,coords,value,&e));
  sfnt_design_advance_width_requirements r{};f.try_get_design_advance_width_requirements(glyph,coords,r,&e);phantom_scratch scratch(r);
  result(f.try_get_design_advance_width(glyph,coords,value,scratch.get(),&e));
  std::vector<float>scalars(count);
  sfnt_horizontal_advance_variation_instance advance{};ok=f.try_prepare_horizontal_advance_variation(coords,scalars,advance,&e);
  emit(static_cast<std::uint32_t>(e));if(ok){emit(advance.uses_hvar);emit(advance.region_scalars.size());for(auto v:advance.region_scalars)scalar(v);
   result(f.try_get_design_advance_width(glyph,coords,&advance,value,&e));
   result(f.try_get_design_advance_width(glyph,coords,&advance,value,scratch.get(),&e));}
  sfnt_horizontal_metrics_variation_instance metrics{};ok=f.try_prepare_horizontal_metrics_variation(coords,scalars,metrics,&e);
  emit(static_cast<std::uint32_t>(e));if(ok){emit(metrics.advance.uses_hvar);bool mapping{};
   ok=f.try_get_horizontal_left_side_bearing_variation(glyph,metrics,value,mapping,&e);emit(static_cast<std::uint32_t>(e));if(ok){emit(mapping);scalar(value);}}
 }
 return std::ferror(stdin)?4:0;
}
