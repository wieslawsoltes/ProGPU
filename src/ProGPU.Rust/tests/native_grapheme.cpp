// Binary transport only: original ProGPU Unicode/grapheme sources are linked unchanged.
#include "progpu_native_text.hpp"
#include "Unicode/progpu_native_unicode_grapheme_internal.hpp"
#include <cstdint>
#include <cstdio>
#include <vector>
using namespace progpu::native::text;
template<class T> bool read(T& value) { return std::fread(&value,sizeof(value),1,stdin)==1; }
template<class T> void emit(const T& value) { std::fwrite(&value,sizeof(value),1,stdout); }
int main() {
 std::uint32_t mode=0;
 while(read(mode)) {
  if(mode==0) { std::uint32_t cp=0; if(!read(cp))return 1; emit(std::uint32_t(get_unicode_grapheme_break_class(cp)));emit(std::uint32_t(get_unicode_indic_conjunct_class(cp)));emit(std::uint32_t(is_unicode_extended_pictographic(cp))); }
  else if(mode==1) {
   std::uint32_t n=0,cap=0,policy=0;if(!read(n))return 2;
   std::vector<unicode_scalar> input(n);if(std::fread(input.data(),sizeof(unicode_scalar),n,stdin)!=n)return 3;
   if(!read(cap)||!read(policy))return 4;
   std::uint32_t count=77,written=88;unicode_error error=unicode_error::invalid_argument;
   const bool counted=try_get_unicode_grapheme_cluster_count(input,count,&error);
   emit(std::uint32_t(counted));emit(std::uint32_t(error));emit(count);
   std::vector<unicode_grapheme_cluster> output(cap,{0x11111111,0x22222222,0x33333333,0x44444444});
   error=unicode_error::invalid_argument;
   const bool ok=policy==0?try_segment_unicode_graphemes(input,output,written,&error):detail::try_segment_managed_compatible_graphemes(input,output,written,&error);
   emit(std::uint32_t(ok));emit(std::uint32_t(error));emit(written);
   std::fwrite(output.data(),sizeof(unicode_grapheme_cluster),cap,stdout);
  } else if(mode==2) {
   std::uint32_t n=0,cap=0;if(!read(n))return 5;
   std::vector<std::uint16_t> input(n);if(std::fread(input.data(),2,n,stdin)!=n||!read(cap))return 6;
   std::vector<std::uint8_t> output(cap,0xa5);unicode_error error=unicode_error::invalid_argument;
   const bool ok=try_get_utf16_grapheme_starts(input,output,&error);
   emit(std::uint32_t(ok));emit(std::uint32_t(error));std::fwrite(output.data(),1,cap,stdout);
  } else return 7;
 }
 return std::ferror(stdout)?8:0;
}
