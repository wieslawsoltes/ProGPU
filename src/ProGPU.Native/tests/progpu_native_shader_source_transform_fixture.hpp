#pragma once

#include "progpu_native_shader_local_frame_fixture.hpp"

namespace progpu::native::tests {

// Independent literals, not evaluated by the primitive packet writer or the
// production centering helper. The reversed products have distinct offsets.
inline constexpr std::array<std::array<double,6U>,4U> source_transform_matrices{{
    {-1,0,0,1,6.5,0}, {-1,0,0,1,8.5,3},
    {-1,0,0,1,4.5,3}, {-1,0,0,1,8.5,9}
}};

template<class Require>
void append_source_transform_case(progpu_native_mil_channel* channel,
    std::uint32_t variant, Require require) {
    using mil::command;
    using mil_clip_fixture_detail::append;
    using mil_clip_fixture_detail::packet;
    std::vector<std::byte> batch;
    if(progpu_native_mil_channel_get_resource_generation(channel,15U)==0U) {
        packet(batch,command::channel_create_resource,15U,63U); // Scale
        packet(batch,command::channel_create_resource,16U,62U); // Translate
        packet(batch,command::channel_create_resource,17U,61U); // Group
        packet(batch,command::channel_create_resource,18U,61U); // Nested Group
    }
    packet(batch,command::scale_transform,15U,-1.0,1.0,3.25,-4.5,0U,0U,0U,0U);
    packet(batch,command::translate_transform,16U,2.0,3.0,0U,0U);
    const auto group=[&](std::uint32_t handle,std::span<const std::uint32_t> children) {
        append(batch,static_cast<std::uint32_t>(16U+children.size_bytes()));
        append(batch,static_cast<std::uint32_t>(command::transform_group));
        append(batch,handle); append(batch,static_cast<std::uint32_t>(children.size_bytes()));
        for(const auto child:children) append(batch,child);
    };
    std::uint32_t selected=15U;
    if(variant==1U) {
        constexpr std::array children{15U,16U}; group(17U,children); selected=17U;
    } else if(variant==2U || variant==3U) {
        constexpr std::array children{16U,15U}; group(17U,children); selected=17U;
        if(variant==3U) {
            // Same original Translate handle occurs twice AFTER the nested
            // group. Do not replace it with two resources or deduplicate it.
            constexpr std::array repeated{17U,16U,16U}; group(18U,repeated); selected=18U;
        }
    }
    packet(batch,command::visual_set_transform,1U,selected);
    require(progpu_native_mil_channel_apply(channel,batch.data(),batch.size(),nullptr)==PROGPU_NATIVE_MIL_STATUS_SUCCESS,
        "typed source transform graph rejected");
    require(progpu_native_mil_channel_get_resource_generation(channel,15U)!=0U &&
        progpu_native_mil_channel_get_resource_generation(channel,16U)!=0U,
        "typed primitive source identity was not retained");
}

template<class Require>
std::vector<std::byte> export_source_transform_scene(progpu_native_mil_channel* channel,
    std::uint64_t owner,std::uint32_t generation,Require require) {
    const progpu_native_mil_scene_build_request request{
        sizeof(request),0U,3U,0U,owner,generation,1,1,0U,generation};
    std::size_t written{};
    progpu_native_mil_scene_build_result result{}; result.struct_size=sizeof(result);
    require(progpu_native_mil_channel_build_scene_with_request(channel,&request,nullptr,0U,&written,nullptr,&result)==
        PROGPU_NATIVE_MIL_STATUS_SUCCESS,"typed transform source size rejected");
    std::vector<std::byte> scene(written);
    require(progpu_native_mil_channel_build_scene_with_request(channel,&request,scene.data(),scene.size(),&written,nullptr,&result)==
        PROGPU_NATIVE_MIL_STATUS_SUCCESS && written==scene.size(),"typed transform source publication rejected");
    return scene;
}

template<class Require>
progpu_native_scene_shader_affine_frame read_source_transform_frame(const std::vector<std::byte>& scene,Require require) {
    progpu_native_scene_header header{}; std::memcpy(&header,scene.data(),sizeof(header));
    for(std::uint32_t resource_index=0U;resource_index<header.resource_count;++resource_index) {
        progpu_native_scene_resource resource{};
        std::memcpy(&resource,scene.data()+header.resource_offset+resource_index*header.resource_stride,sizeof(resource));
        if(resource.kind!=PROGPU_NATIVE_SCENE_RESOURCE_WPF_SHADER_EFFECT) continue;
        require(resource.payload_size==sizeof(progpu_native_scene_shader_effect_affine),"typed primitive lost affine wire");
        progpu_native_scene_shader_effect_affine effect{};
        std::memcpy(&effect,scene.data()+resource.payload_offset,sizeof(effect));
        require(effect.version==6U && effect.input_resource_index<resource_index,"typed primitive lost owned earlier input");
        return effect.frame;
    }
    require(false,"typed primitive shader frame absent");
    return {};
}

template<class Render,class Require>
void verify_shader_source_transform_frames(Render render,Require require) {
    std::array<std::vector<std::byte>,4U> source_scenes,literal_scenes;
    {
        progpu_native_mil_channel* raw{};
        require(progpu_native_mil_channel_create(&raw)==PROGPU_NATIVE_MIL_STATUS_SUCCESS,"typed transform channel failed");
        mil_clip_channel owner(raw);
        for(std::uint32_t variant=0U;variant<4U;++variant) {
            const shader_local_case input{shader_padding_output::uv_squared,1,{2,6.25,4,11.5},{}};
            std::vector<std::byte> bootstrap;
            require(build_shader_local_scene(raw,800U+variant,input,bootstrap,false,64,0,1,1,0,false,true,128,1,
                &source_transform_matrices[variant])==PROGPU_NATIVE_MIL_STATUS_SUCCESS,"independent literal matrix scene rejected");
            literal_scenes[variant]=export_source_transform_scene(raw,0x94D0U,variant+1U,require);
            append_source_transform_case(raw,variant,require);
            source_scenes[variant]=export_source_transform_scene(raw,0x94D1U,variant+1U,require);
            const auto actual=read_source_transform_frame(source_scenes[variant],require);
            const auto literal=read_source_transform_frame(literal_scenes[variant],require);
            require(std::memcmp(&actual,&literal,sizeof(actual))==0,"typed primitive differs from independent literal frame");
            require(actual.placement.source_scale_x==-1 && actual.placement.source_scale_y==1 &&
                actual.source_m12==0 && actual.source_m21==0 &&
                actual.placement.source_offset_x==64+source_transform_matrices[variant][4] &&
                actual.placement.source_offset_y==source_transform_matrices[variant][5],
                "typed center/order/repeated-child identity lost in source witness");
        }
    } // All original typed resources retire before either provider replays.
    for(std::uint32_t variant=0U;variant<4U;++variant) {
        const auto replay_scene=[&](bool independent,const std::vector<std::byte>& scene,bool warm) {
            progpu_native_scene_header header{}; std::memcpy(&header,scene.data(),sizeof(header));
            progpu_native_layer_metrics layers{}; layers.struct_size=sizeof(layers);
            progpu_native_scene_frame_metrics frame{}; frame.struct_size=sizeof(frame);
            const std::uint32_t submissions=warm?1U:2U;
            auto pixels=render(independent,scene,header,submissions,layers,frame);
            require(frame.command_count==header.command_count && frame.submission_count==submissions &&
                frame.draw_call_count==1U && layers.content_pass_count==0U && layers.effect_count==1U &&
                layers.effect_kind==PROGPU_NATIVE_GROUP_EFFECT_WPF_SHADER && layers.effect_pass_count==1U &&
                layers.effect_cache_hit==0U && layers.effect_uniform_upload_bytes==(warm?0U:608U),
                "typed primitive source cold/warm pass/upload counters differ");
            require(pixels.size()==128U*64U*4U,"typed primitive source pixel extent differs");
            return pixels;
        };
        const auto literal=replay_scene(true,literal_scenes[variant],false);
        const auto cold=replay_scene(false,source_scenes[variant],false);
        const auto warm=replay_scene(false,source_scenes[variant],true);
        const auto independent=replay_scene(true,source_scenes[variant],false);
        require(cold==warm && cold==independent && cold==literal,
            "typed primitive pixels differ from literal/cold/warm/independent replay");
        const auto& matrix=source_transform_matrices[variant];
        const double final_x=64+matrix[4],final_y=matrix[5];
        const auto edge=[](double value) { return std::floor((std::floor(value*16+.5)+7)/16); };
        const double left=edge(final_x-43.75),right=edge(final_x-12.75);
        const double top=edge(final_y+14.25),bottom=edge(final_y+30);
        bool nonempty=false;
        for(std::uint32_t pixel_y=0U;pixel_y<64U;++pixel_y) for(std::uint32_t pixel_x=0U;pixel_x<128U;++pixel_x) {
            const double u=(final_x-(pixel_x+.5)-12)/32;
            const double v=(pixel_y+.5-final_y-14)/16;
            std::array<std::uint8_t,4U> expected{0,0,0,255};
            if(pixel_x>=left && pixel_x<right && pixel_y>=top && pixel_y<bottom && u>=0 && u<1 && v>=0 && v<1) {
                expected={static_cast<std::uint8_t>(std::floor(u*u*255+.5)),
                    static_cast<std::uint8_t>(std::floor(v*v*255+.5)),0,255};
                nonempty=true;
            }
            const auto* actual=cold.data()+(pixel_y*128U+pixel_x)*4U;
            const bool same=std::equal(expected.begin(),expected.end(),actual);
            if(!same) std::fprintf(stderr,"Typed transform variant=%u pixel=(%u,%u) actual=(%u,%u,%u,%u) expected=(%u,%u,%u,%u)\n",
                variant,pixel_x,pixel_y,actual[0],actual[1],actual[2],actual[3],expected[0],expected[1],expected[2],expected[3]);
            require(same,"typed primitive differs from independent dyadic pixel equation");
        }
        require(nonempty,"typed primitive positive control has empty independent coverage");
    }
}
} // namespace progpu::native::tests
