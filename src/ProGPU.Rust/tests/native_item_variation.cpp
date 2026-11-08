// Original item variation source unchanged; exact result/error transport only.
#include "progpu_native_text.hpp"
#include <bit>
#include <cstdio>
#include <cstdlib>
#include <vector>
using namespace progpu::native::text;
void emit(std::uint32_t v) { if(std::fwrite(&v,4,1,stdout)!=1) std::exit(2); }
int main() {
    std::uint32_t header[8]{};
    while(std::fread(header,4,8,stdin)==8) {
        std::vector<std::byte> data(header[1]);
        if(std::fread(data.data(),1,data.size(),stdin)!=data.size()) return 3;
        std::uint32_t cc{},sc{};
        if(std::fread(&cc,4,1,stdin)!=1) return 4;
        std::vector<std::int16_t> coords(cc);
        if(std::fread(coords.data(),2,cc,stdin)!=cc || std::fread(&sc,4,1,stdin)!=1) return 5;
        std::vector<float> scalars(sc);
        if(std::fread(scalars.data(),4,sc,stdin)!=sc) return 6;
        font_error error{};
        if(header[0]==0) {
            sfnt_delta_set_index_map_view map{};
            const auto ok=sfnt_item_variation_data::try_get_delta_set_index_map(data,header[2],map,&error);
            emit(static_cast<std::uint32_t>(error));if(!ok) continue;
            emit(map.entry_count);std::uint16_t outer{},inner{};
            sfnt_item_variation_data::get_delta_set_index(map,header[4],outer,inner);
            emit(outer);emit(inner);continue;
        }
        sfnt_item_variation_store_view store{};
        const auto ok=sfnt_item_variation_data::try_get_store(data,header[2],header[3],store,&error);
        emit(static_cast<std::uint32_t>(error));if(!ok) continue;
        emit(store.axis_count);emit(store.region_count);emit(store.subtable_count);
        float value{};
        auto scalar_result=[&](bool accepted) {emit(static_cast<std::uint32_t>(error));if(accepted) emit(std::bit_cast<std::uint32_t>(value));};
        scalar_result(sfnt_item_variation_data::try_get_region_scalar(store,coords,header[4],value,&error));
        scalar_result(sfnt_item_variation_data::try_get_delta(store,coords,header[5],header[6],value,&error));
        scalar_result(sfnt_item_variation_data::try_get_delta(store,scalars,header[5],header[6],value,&error));
        std::uint16_t count{};
        const auto counted=sfnt_item_variation_data::try_get_region_scalar_count(store,header[5],count,&error);
        emit(static_cast<std::uint32_t>(error));if(counted) emit(count);
        scalar_result(sfnt_item_variation_data::try_get_region_scalar(store,coords,header[5],header[7],value,&error));
    }
    return std::ferror(stdin)?7:0;
}
