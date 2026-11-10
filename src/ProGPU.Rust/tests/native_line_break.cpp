// Transport only; link original ProGPU line/grapheme sources unchanged.
#include "progpu_native_text.hpp"
#include "progpu_native_unicode_line_break_internal.hpp"
#include <cstdio>
#include <vector>
using namespace progpu::native::text;
template<class T> bool read(T& v) { return std::fread(&v,sizeof(v),1,stdin)==1; }
template<class T> void emit(const T& v) { std::fwrite(&v,sizeof(v),1,stdout); }
int main() {
 std::uint32_t mode=0;
 while(read(mode)) {
  if(mode==0) { std::uint32_t cp=0;if(!read(cp))return 1;emit(std::uint32_t(get_unicode_line_break_class(cp))); }
  else if(mode==1) {
   std::uint32_t n=0,c=0,b=0,p=0;if(!read(n))return 2;
   std::vector<unicode_scalar> input(n);
   if(std::fread(input.data(),sizeof(unicode_scalar),n,stdin)!=n||!read(c)||!read(b)||!read(p))return 3;
   std::vector<unicode_line_break_class> classes(c,unicode_line_break_class::surrogate);
   std::vector<text_line_break_kind> breaks(b,text_line_break_kind::opportunity);
   unicode_error error=unicode_error::invalid_encoding;
   const bool ok=p==0?try_resolve_unicode_line_breaks(input,classes,breaks,&error):detail::try_resolve_edit_selection_line_breaks(input,classes,breaks,&error);
   emit(std::uint32_t(ok));emit(std::uint32_t(error));
   std::fwrite(classes.data(),1,c,stdout);std::fwrite(breaks.data(),1,b,stdout);
   std::fwrite(input.data(),sizeof(unicode_scalar),n,stdout);
  } else return 4;
 }
 return std::ferror(stdout)?5:0;
}
