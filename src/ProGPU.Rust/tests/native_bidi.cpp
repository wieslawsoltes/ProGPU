// Transport only; executes the unchanged original ProGPU bidi resolver.
#include "progpu_native_text.hpp"
#include <cstdio>
#include <cstdlib>
#include <vector>
using namespace progpu::native::text;
static_assert(sizeof(unicode_scalar)==16&&sizeof(unicode_bidi_level)==8);
template<class T>T read(){T v{};if(std::fread(&v,sizeof(v),1,stdin)!=1)std::exit(2);return v;}
void emit(std::uint32_t v){if(std::fwrite(&v,4,1,stdout)!=1)std::exit(3);}
int main(){std::uint32_t count{};while(std::fread(&count,4,1,stdin)==1){
 std::vector<unicode_scalar> input(count);if(std::fread(input.data(),16,count,stdin)!=count)return 4;
 const auto requested=static_cast<std::int8_t>(read<std::int32_t>());
 std::vector<unicode_bidi_unit> units(read<std::uint32_t>(),unicode_bidi_unit{0x11111111,0x22222222,0x3333,unicode_bidi_class::right_to_left,unicode_bidi_class::arabic_letter,7,0x66,123});
 std::vector<std::uint32_t> indices(read<std::uint32_t>(),0xa5a5a5a5);
 std::vector<unicode_bidi_level_run> runs(read<std::uint32_t>(),unicode_bidi_level_run{0x11111111,0x22222222,13,9,true,0x55,0x66});
 std::vector<unicode_bidi_bracket_pair> pairs(read<std::uint32_t>(),unicode_bidi_bracket_pair{0x66666666,0x77777777});
 std::vector<unicode_bidi_level> output(read<std::uint32_t>(),unicode_bidi_level{0xf0f0f0f0,0x1234,7,0xcc});
 unicode_bidi_requirements r{};unicode_error e{};try_get_unicode_bidi_requirements(input,r,&e);emit(static_cast<std::uint32_t>(e));emit(r.unit_count);emit(r.index_count);emit(r.run_count);emit(r.bracket_pair_count);
 std::int8_t paragraph{};std::uint32_t written{};try_resolve_unicode_bidi(input,requested,{units,indices,runs,pairs},output,paragraph,written,&e);
 emit(static_cast<std::uint32_t>(e));emit(static_cast<std::uint32_t>(paragraph));emit(written);
 if(std::fwrite(output.data(),8,output.size(),stdout)!=output.size())return 5;
 for(const auto& u:units){emit(u.code_point);emit(u.input_index);emit(u.input_length);emit(static_cast<std::uint32_t>(u.original));emit(static_cast<std::uint32_t>(u.type));emit(static_cast<std::uint32_t>(u.level));emit(u.reserved);emit(static_cast<std::uint32_t>(u.matching_isolate));}
 for(auto i:indices)emit(i);
 for(const auto& v:runs){emit(v.active_start);emit(v.active_count);emit(static_cast<std::uint32_t>(v.next));emit(static_cast<std::uint32_t>(v.explicit_level));emit(v.has_predecessor);emit(v.reserved0);emit(v.reserved1);}
 for(const auto& v:pairs){emit(v.open_position);emit(v.close_position);}
}return std::ferror(stdin)?6:0;}
