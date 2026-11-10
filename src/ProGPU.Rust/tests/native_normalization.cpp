// Binary transport only; original normalization and property sources are unchanged.
#include "progpu_native_text.hpp"
#include <cstdio>
#include <vector>
using namespace progpu::native::text;
template<class T> bool read(T& v){return std::fread(&v,sizeof(v),1,stdin)==1;}
template<class T> void emit(const T& v){std::fwrite(&v,sizeof(v),1,stdout);}
int main(){std::vector<std::byte> bytes;unicode_normalization_data data{};std::uint32_t mode=0;
 while(read(mode)){
  if(mode==0){std::uint32_t n=0;if(!read(n))return 1;bytes.resize(n);if(std::fread(bytes.data(),1,n,stdin)!=n)return 2;unicode_error e{};bool ok=unicode_normalization_data::try_create(bytes,data,&e);emit(std::uint32_t(ok));emit(std::uint32_t(e));}
  else if(mode==1){std::uint32_t cp=0,a=0,b=0;if(!read(cp)||!read(a)||!read(b))return 3;std::span<const std::byte> values;bool found=data.try_get_decomposition(cp,values);emit(std::uint32_t(found));emit(std::uint32_t(values.size()));std::fwrite(values.data(),1,values.size(),stdout);std::uint32_t composed=77;found=data.try_compose(a,b,composed);emit(std::uint32_t(found));emit(composed);}
  else if(mode==2){std::uint32_t n=0,cap=0,form=0;if(!read(n))return 4;std::vector<unicode_scalar> input(n);if(std::fread(input.data(),sizeof(unicode_scalar),n,stdin)!=n||!read(cap)||!read(form))return 5;unicode_normalization_requirements req{77};unicode_error e{};bool ok=try_get_unicode_normalization_requirements(input,data,req,&e);emit(std::uint32_t(ok));emit(std::uint32_t(e));emit(req.scalar_capacity);
   const unicode_scalar sentinel{0x33333333,0x44444444,0x5555,0x66,0x77,{0x88888888}};std::vector<unicode_scalar> output(cap,sentinel);std::uint32_t written=88;e=unicode_error::none;ok=try_normalize_unicode(input,data,static_cast<unicode_normalization_form>(form),output,written,&e);emit(std::uint32_t(ok));emit(std::uint32_t(e));emit(written);std::fwrite(output.data(),sizeof(unicode_scalar),cap,stdout);std::fwrite(input.data(),sizeof(unicode_scalar),n,stdout);
  }else return 6;
 }
 return std::ferror(stdout)?7:0;
}
