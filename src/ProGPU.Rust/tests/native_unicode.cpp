// Transport only: unchanged original strict Unicode/property/itemization sources.
#include "progpu_native_text.hpp"
#include <cstdio>
#include <cstdlib>
#include <vector>
using namespace progpu::native::text;
static_assert(sizeof(unicode_scalar)==16&&sizeof(unicode_script_run)==20);
void emit(std::uint32_t v){if(std::fwrite(&v,4,1,stdout)!=1)std::exit(2);}
template<class T>T read(){T v{};if(std::fread(&v,sizeof(v),1,stdin)!=1)std::exit(3);return v;}
template<class T>std::vector<T> array(){std::vector<T> v(read<std::uint32_t>());if(std::fread(v.data(),sizeof(T),v.size(),stdin)!=v.size())std::exit(4);return v;}
template<class T>void emit_array(const std::vector<T>& v){if(std::fwrite(v.data(),sizeof(T),v.size(),stdout)!=v.size())std::exit(5);}
constexpr unicode_scalar marker{0x41414141,0x42424242,0x4343,0x44,0x45,{0x46464646}};
constexpr unicode_script_run run_marker{0x41414141,0x42424242,0x43434343,0x44444444,{0x45454545}};
int main(){std::uint32_t mode{};while(std::fread(&mode,4,1,stdin)==1){unicode_error e{};std::uint32_t written{};
 if(mode==0){auto codes=array<std::uint32_t>();for(auto cp:codes){emit(get_unicode_script(cp).value);emit(get_unicode_canonical_combining_class(cp));emit(static_cast<std::uint32_t>(get_unicode_bidi_class(cp)));emit(static_cast<std::uint32_t>(get_unicode_general_category(cp)));emit(static_cast<std::uint32_t>(get_unicode_decimal_digit_value(cp)));emit(get_unicode_mirrored_code_point(cp));emit(get_unicode_vertical_code_point(cp));std::uint32_t pair{};unicode_bidi_bracket_kind kind{};auto ok=try_get_unicode_bidi_bracket(cp,pair,kind);emit(ok);emit(pair);emit(static_cast<std::uint32_t>(kind));}emit(infer_open_type_script(codes).value);
 }else if(mode==1){auto input=array<std::byte>();std::vector<unicode_scalar> output(read<std::uint32_t>(),marker);unicode_decode_requirements r{};auto ok=try_get_utf8_decode_requirements(input,r,&e);emit(static_cast<std::uint32_t>(e));if(ok)emit(r.scalar_count);try_decode_utf8(input,output,written,&e);emit(static_cast<std::uint32_t>(e));emit(written);emit_array(output);
 }else if(mode==2){auto input=array<std::uint16_t>();std::vector<unicode_scalar> output(read<std::uint32_t>(),marker);unicode_decode_requirements r{};auto ok=try_get_utf16_decode_requirements(input,r,&e);emit(static_cast<std::uint32_t>(e));if(ok)emit(r.scalar_count);try_decode_utf16(input,output,written,&e);emit(static_cast<std::uint32_t>(e));emit(written);emit_array(output);
 }else if(mode==3){auto input=array<unicode_scalar>();std::vector<unicode_script_run> output(read<std::uint32_t>(),run_marker);std::uint32_t count{};auto ok=try_get_unicode_script_run_count(input,count,&e);emit(static_cast<std::uint32_t>(e));if(ok)emit(count);try_itemize_unicode_scripts(input,output,written,&e);emit(static_cast<std::uint32_t>(e));emit(written);emit_array(output);
 }else return 6;
}return std::ferror(stdin)?7:0;}
