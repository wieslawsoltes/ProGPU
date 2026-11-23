// Transport only; all decoding, mapping, control and advance logic is original.
#include "progpu_native_text.hpp"
#include <cstdio>
#include <cstdlib>
#include <vector>
using namespace progpu::native::text;
void emit(std::uint32_t v){if(std::fwrite(&v,4,1,stdout)!=1)std::exit(2);}
template<class T> T read(){T v{};if(std::fread(&v,sizeof(v),1,stdin)!=1)std::exit(3);return v;}
template<class T> std::vector<T> vector(){std::vector<T> v(read<std::uint32_t>());if(std::fread(v.data(),sizeof(T),v.size(),stdin)!=v.size())std::exit(4);return v;}
int main(){std::uint32_t mode{};
 while(std::fread(&mode,4,1,stdin)==1){font_error e{};
  if(mode==0){auto text=vector<char16_t>();sfnt_simple_glyph_run_requirements r{};
   auto ok=try_get_sfnt_simple_glyph_run_requirements(text,r,&e);emit(static_cast<std::uint32_t>(e));if(ok){emit(r.cluster_map_count);emit(r.glyph_count);}
   for(std::size_t i=0;i<=text.size();++i){std::uint32_t cp{},count{};ok=try_read_sfnt_simple_code_point(text,i,cp,count,&e);emit(static_cast<std::uint32_t>(e));if(ok){emit(cp);emit(count);emit(is_sfnt_simple_formatting_control(cp));}}
  }else if(mode==1){auto data=vector<std::byte>();auto face=read<std::uint32_t>();auto text=vector<char16_t>();auto blank=read<std::uint32_t>();auto hyphen=read<std::uint32_t>();auto cc=read<std::uint32_t>();auto gc=read<std::uint32_t>();
   sfnt_font_view f{};auto ok=sfnt_font_view::try_create(data,face,f,&e);emit(static_cast<std::uint32_t>(e));if(!ok)continue;
   std::vector<std::uint16_t> clusters(cc,0xBEEF),glyphs(gc,0xCAFE);std::uint32_t count=99;
   try_build_sfnt_simple_glyph_run(f,text,static_cast<std::uint16_t>(blank),static_cast<std::uint16_t>(hyphen),clusters,glyphs,count,&e);
   emit(static_cast<std::uint32_t>(e));emit(count);for(auto v:clusters)emit(v);for(auto v:glyphs)emit(v);
  }else if(mode==2){auto text=vector<char16_t>();auto clusters=vector<std::uint16_t>();auto glyphs=vector<std::uint16_t>();
   std::vector<sfnt_simple_glyph_metrics> metrics(read<std::uint32_t>());for(auto& m:metrics){m.advance_width=read<std::uint32_t>();m.advance_height=read<std::uint32_t>();}
   auto upem=read<std::uint32_t>();auto em=read<double>();auto scale=read<double>();auto sideways=read<std::uint32_t>();
   std::vector<std::uint8_t> state(read<std::uint32_t>(),0xA5);std::vector<std::int32_t> advances(read<std::uint32_t>(),-1234567);
   try_fill_sfnt_simple_glyph_advances(text,clusters,glyphs,metrics,static_cast<std::uint16_t>(upem),em,scale,sideways!=0,state,advances,&e);
   emit(static_cast<std::uint32_t>(e));for(auto v:state)emit(v);for(auto v:advances)emit(v);
  }else return 5;
 }
 return std::ferror(stdin)?6:0;
}
