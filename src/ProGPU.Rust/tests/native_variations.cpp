// Original fvar/avar implementation linked unchanged; test transport only.
#include "progpu_native_text.hpp"
#include <bit>
#include <cstdio>
#include <cstdlib>
#include <vector>
using namespace progpu::native::text;
void emit(std::uint32_t value) { if (std::fwrite(&value,4,1,stdout)!=1) std::exit(2); }
void axis(sfnt_variation_axis a) {
    emit(a.tag.value);emit(a.minimum_fixed);emit(a.default_fixed);emit(a.maximum_fixed);
    emit(a.flags);emit(a.name_id);emit(std::bit_cast<std::uint32_t>(a.minimum()));
    emit(std::bit_cast<std::uint32_t>(a.default_value()));
    emit(std::bit_cast<std::uint32_t>(a.maximum()));emit(a.hidden());
}
int main() {
    std::uint32_t length{};
    while (std::fread(&length,4,1,stdin)==1) {
        std::vector<std::byte> data(length);
        std::uint32_t args[3]{};
        if(std::fread(data.data(),1,length,stdin)!=length || std::fread(args,4,3,stdin)!=3) return 3;
        sfnt_font_view face{};font_error error{};
        const auto accepted=sfnt_font_view::try_create(data,0,face,&error);
        emit(static_cast<std::uint32_t>(error));if(!accepted) continue;
        std::uint16_t count{};
        const auto counted=face.try_get_variation_axis_count(count,&error);
        emit(static_cast<std::uint32_t>(error));
        if(counted) {
            emit(count);std::vector<sfnt_variation_axis> axes(count);std::uint16_t written{};
            if(!face.try_decode_variation_axes(axes,written,&error)||written!=count) return 4;
            for(const auto a:axes) axis(a);
        }
        sfnt_variation_axis selected{};
        const auto found=face.try_get_variation_axis(args[0],selected,&error);
        emit(static_cast<std::uint32_t>(error));if(found) axis(selected);
        std::int16_t coordinate{};
        const auto fixed=face.try_normalize_variation_coordinate(args[0],static_cast<std::int32_t>(args[1]),coordinate,&error);
        emit(static_cast<std::uint32_t>(error));if(fixed) emit(static_cast<std::uint32_t>(coordinate));
        const auto design=face.try_normalize_variation_design_coordinate(args[0],std::bit_cast<float>(args[2]),coordinate,&error);
        emit(static_cast<std::uint32_t>(error));if(design) emit(static_cast<std::uint32_t>(coordinate));
    }
    return std::ferror(stdin)?5:0;
}
