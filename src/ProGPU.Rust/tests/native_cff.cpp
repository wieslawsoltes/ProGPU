// Original CFF containers and DICT readers, linked without source changes.
#include "progpu_native_text.hpp"
#include "../../ProGPU.Native/src/Text/progpu_native_cff_type2_internal.hpp"
#include <bit>
#include <cstdio>
#include <cstdlib>
#include <array>
#include <algorithm>
#include <vector>
#include <limits>
using namespace progpu::native::text;
void emit(std::uint32_t v){if(std::fwrite(&v,4,1,stdout)!=1)std::exit(2);}
void blob(std::span<const std::byte> b){emit(b.size());if(std::fwrite(b.data(),1,b.size(),stdout)!=b.size())std::exit(3);}
void number(double v){const auto bits=std::bit_cast<std::uint64_t>(v);emit(bits);emit(bits>>32);}
void index_snapshot(sfnt_cff_index_view v){
 emit(v.count);emit(v.offset_size);
 for(std::uint32_t i=0;i<std::min(v.count,64U);++i){std::span<const std::byte>b{};font_error e{};const auto ok=sfnt_cff_data::try_get_index_item(v,i,b,&e);emit(static_cast<std::uint32_t>(e));if(ok)blob(b);}
}
void top_snapshot(sfnt_cff1_top_dictionary v){for(auto n:{v.char_strings_offset,v.private_size,v.private_offset,v.font_dictionary_offset,v.fd_select_offset})emit(n);}
int main(){
 std::uint32_t mode{},len{};std::array<std::uint32_t,4>a{};
 while(std::fread(&mode,4,1,stdin)==1){
  if(std::fread(&len,4,1,stdin)!=1||std::fread(a.data(),4,4,stdin)!=4)return 4;
  std::vector<std::byte>b(len);if(std::fread(b.data(),1,len,stdin)!=len)return 5;
  font_error e{};bool ok=false;
  if(mode<=1){std::size_t cursor=a[0];sfnt_cff_index_view v{};
   ok=mode==0?sfnt_cff_data::try_read_index(b,cursor,v,&e):sfnt_cff_data::try_read_cff2_index(b,cursor,v,&e);
   emit(static_cast<std::uint32_t>(e));emit(cursor);if(ok)index_snapshot(v);
  }else if(mode==2){std::size_t cursor=a[1];double v{};ok=sfnt_cff_data::try_read_dictionary_number(b,cursor,a[0],v);emit(ok);emit(cursor);if(ok)number(v);
  }else if(mode==3){sfnt_cff1_top_dictionary v{};ok=sfnt_cff_data::try_get_top_dictionary(b,v,&e);emit(static_cast<std::uint32_t>(e));if(ok)top_snapshot(v);
  }else if(mode==4){sfnt_cff2_top_dictionary v{};ok=sfnt_cff_data::try_get_cff2_top_dictionary(b,v,&e);emit(static_cast<std::uint32_t>(e));if(ok){emit(v.char_strings_offset);emit(v.font_dictionary_offset);emit(v.fd_select_offset);emit(v.variation_store_offset);number(v.font_matrix_scale);emit(v.has_font_matrix);}
  }else if(mode==5){sfnt_cff_fd_select_view v{};ok=sfnt_cff_data::try_read_fd_select(b,a[0],a[1],a[2],v,&e);emit(static_cast<std::uint32_t>(e));if(ok){emit(v.format);emit(v.glyph_count);emit(v.range_count);
   for(auto g:std::array<std::uint32_t,8>{0,1,2,3,17,a[1]-1,a[1],0xffffffffU}){std::uint32_t d{};ok=sfnt_cff_data::try_get_font_dictionary(v,g,d,&e);emit(static_cast<std::uint32_t>(e));if(ok)emit(d);}
  }}else if(mode==6){sfnt_cff_index_view v{};ok=sfnt_cff_data::try_read_local_subroutines(b,a[0],a[1],v,&e);emit(static_cast<std::uint32_t>(e));if(ok)index_snapshot(v);
  }else if(mode==7){sfnt_font_view face{};ok=sfnt_font_view::try_create(b,0,face,&e);emit(static_cast<std::uint32_t>(e));if(!ok)continue;
   sfnt_cff1_font_view v{};ok=face.try_get_cff1_font(a[0],v,&e);emit(static_cast<std::uint32_t>(e));if(!ok)continue;
   blob(v.bytes);top_snapshot(v.top_dictionary);index_snapshot(v.char_strings);index_snapshot(v.global_subroutines);index_snapshot(v.font_dictionaries);
   for(auto g:std::array<std::uint32_t,5>{0,1,v.char_strings.count-1,v.char_strings.count,0xffffffffU}){sfnt_cff_index_view s{};ok=sfnt_cff_data::try_get_local_subroutines(v,g,s,&e);emit(static_cast<std::uint32_t>(e));if(ok)index_snapshot(s);}
  }else if(mode==8){sfnt_font_view face{};ok=sfnt_font_view::try_create(b,0,face,&e);emit(static_cast<std::uint32_t>(e));if(!ok)continue;
   sfnt_cff1_font_view v{};ok=face.try_get_cff1_font(0,v,&e);emit(static_cast<std::uint32_t>(e));if(!ok)continue;
   sfnt_cff_outline_transform t{};if(a[0]==2)t={0.5,-0.25,0.125,2.0,3.125,-1.5};
   if(a[0]==3)t.m11=std::numeric_limits<double>::quiet_NaN();if(a[0]==4)t.m11=std::numeric_limits<double>::max();if(a[0]==5)t={0,0,0,0,0,0};
   sfnt_cff1_outline_requirements r{};
   ok=a[0]?sfnt_cff_data::try_get_outline_requirements(v,a[1],t,r,&e):sfnt_cff_data::try_get_outline_requirements(v,a[1],r,&e);
   emit(static_cast<std::uint32_t>(e));if(!ok)continue;
   std::vector<progpu_native_path_segment> segments(r.path_segment_count);std::uint32_t written{};
   ok=a[0]?sfnt_cff_data::try_decode_outline(v,a[1],t,segments,written,&e):sfnt_cff_data::try_decode_outline(v,a[1],segments,written,&e);
   if(!ok||written!=segments.size())return 8;emit(written);
   if(std::fwrite(segments.data(),sizeof(progpu_native_path_segment),written,stdout)!=written)return 9;
  }else if(mode==9){sfnt_font_view face{};ok=sfnt_font_view::try_create(b,0,face,&e);emit(static_cast<std::uint32_t>(e));if(!ok)continue;
   sfnt_cff2_font_view v{};ok=face.try_get_cff2_font(a[0],v,&e);emit(static_cast<std::uint32_t>(e));if(!ok)continue;
   emit(v.axis_count);emit(v.char_strings.count);emit(v.global_subroutines.count);emit(v.font_dictionaries.count);
   sfnt_cff_index_view local{};std::uint16_t initial{};
   ok=detail::try_get_cff2_glyph_private(v,a[1],local,initial,&e);emit(static_cast<std::uint32_t>(e));
   if(ok){emit(initial);index_snapshot(local);}
   std::vector<std::int16_t> coords(a[2]);if(coords.size()>0)coords[0]=static_cast<std::int16_t>(a[3]);if(coords.size()>1)coords[1]=static_cast<std::int16_t>(a[3]>>16);
   sfnt_cff2_outline_requirements r{};ok=sfnt_cff_data::try_get_outline_requirements(v,a[1],coords,r,&e);
   // Original oversized-charstring guard returns false with error::none.
   // Rust's Result represents this rejection as InvalidGlyph; preserve the
   // original success flag independently, without accepting the invalid program.
   emit(ok);emit(static_cast<std::uint32_t>(!ok&&e==font_error::none?font_error::invalid_glyph:e));if(!ok)continue;
   std::vector<progpu_native_path_segment> segments(r.path_segment_count);std::uint32_t written{};
   if(!sfnt_cff_data::try_decode_outline(v,a[1],coords,segments,written,&e)||written!=segments.size())return 10;
   emit(written);if(std::fwrite(segments.data(),sizeof(progpu_native_path_segment),written,stdout)!=written)return 11;
  }else return 6;
 }
 return std::ferror(stdin)?7:0;
}
