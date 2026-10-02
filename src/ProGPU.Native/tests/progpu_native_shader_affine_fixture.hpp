#pragma once

#include "progpu_native_shader_local_frame_fixture.hpp"

namespace progpu::native::tests {
struct shader_affine_case {
    std::array<double,6U> matrix;
    shader_padding_output output;
    double tx,ty;
    bool nested{};
};
inline constexpr std::array shader_affine_cases{
    shader_affine_case{{0,1,-1,0,0,0},shader_padding_output::constant,64,0},
    shader_affine_case{{0,1,-1,0,0,0},shader_padding_output::uv_squared,64,0},
    shader_affine_case{{-1,0,0,1,0,0},shader_padding_output::uv_squared,64,0},
    shader_affine_case{{0,1,1,0,0,0},shader_padding_output::image,0,0},
    shader_affine_case{{0,1,1,0,0,0},shader_padding_output::derivatives,0,0},
    shader_affine_case{{-1,0,0,1,0,0},shader_padding_output::constant,64,0,true},
    shader_affine_case{{1,0,.75,1,0,0},shader_padding_output::constant,4.25,3.25}
};

template<class Render,class Require>
void verify_shader_affine_frames(Render render,Require require) {
    std::array<std::vector<std::byte>,shader_affine_cases.size()> scenes;
    {
        progpu_native_mil_channel* raw{};
        require(progpu_native_mil_channel_create(&raw)==PROGPU_NATIVE_MIL_STATUS_SUCCESS,"affine source channel failed");
        mil_clip_channel owner(raw);
        for(std::uint32_t variant=0;variant<scenes.size();++variant) {
            const auto& test=shader_affine_cases[variant];
            const shader_local_case source{test.output,1,{2,6.25,4,11.5},{}};
            auto local=test.matrix;
            const std::array<double,6U> parent{2,0,0,2,0,0};
            if(test.nested) for(auto& value:local) value*=.5;
            require(build_shader_local_scene(raw,400U+variant,source,scenes[variant],false,test.tx,test.ty,
                1,1,0,false,true,128,1,&local,test.nested?&parent:nullptr)==PROGPU_NATIVE_MIL_STATUS_SUCCESS,
                "actual full-XY source scene rejected");
            progpu_native_scene_header header{}; std::memcpy(&header,scenes[variant].data(),sizeof(header));
            bool found=false;
            for(std::uint32_t index=0;index<header.resource_count;++index) {
                progpu_native_scene_resource resource{};
                std::memcpy(&resource,scenes[variant].data()+header.resource_offset+index*header.resource_stride,sizeof(resource));
                if(resource.kind!=PROGPU_NATIVE_SCENE_RESOURCE_WPF_SHADER_EFFECT) continue;
                require(resource.payload_size==sizeof(progpu_native_scene_shader_effect_affine),"affine source lost distinct wire");
                progpu_native_scene_shader_effect_affine descriptor{};
                std::memcpy(&descriptor,scenes[variant].data()+resource.payload_offset,sizeof(descriptor));
                require(descriptor.version==6U && descriptor.input_resource_index<index &&
                    descriptor.frame.source_m12==test.matrix[1] && descriptor.frame.source_m21==test.matrix[2] &&
                    descriptor.frame.placement.source_scale_x==test.matrix[0] &&
                    descriptor.frame.placement.source_scale_y==test.matrix[3],"original source XY history lost");
                found=true;
            }
            require(found,"affine shader descriptor absent");
        }
    } // All native source owners retire before GPU replay.
    for(std::uint32_t variant=0;variant<scenes.size();++variant) {
        const auto& test=shader_affine_cases[variant];
        progpu_native_scene_header header{}; std::memcpy(&header,scenes[variant].data(),sizeof(header));
        std::array<std::vector<std::uint8_t>,3U> images;
        for(std::uint32_t replay=0;replay<3U;++replay) {
            progpu_native_layer_metrics layers{}; layers.struct_size=sizeof(layers);
            progpu_native_scene_frame_metrics frame{}; frame.struct_size=sizeof(frame);
            const auto submissions=replay==1U?1U:test.output==shader_padding_output::image?3U:2U;
            images[replay]=render(replay==2U,scenes[variant],header,submissions,layers,frame);
            require(frame.command_count==header.command_count && frame.submission_count==submissions &&
                frame.draw_call_count==1U && layers.content_pass_count==0U &&
                layers.effect_count==1U && layers.effect_kind==PROGPU_NATIVE_GROUP_EFFECT_WPF_SHADER &&
                layers.effect_pass_count==1U && layers.effect_cache_hit==0U &&
                layers.effect_uniform_upload_bytes==(replay==1U?0U:608U),"affine ownership/pass/upload counters differ");
        }
        require(images[0]==images[1] && images[0]==images[2] && images[0].size()==128U*64U*4U,
            "affine cold/warm/independent frame differs");
        // Independent source equations. The six orthogonal controls have exact
        // dyadic transforms and 32x16 captures. The shear's separate lengths are
        // 1 and 1.25; its constant output checks the quad/clip, not filter weights.
        const auto& m=test.matrix;
        const double sy=variant==6U?1.25:1.0;
        const double ax=12,ay=std::floor(14.25*sy),ex=32,ey=std::ceil(30.0*sy)-ay;
        std::array<double,4U> clip{1e9,1e9,-1e9,-1e9};
        for(const double y:{14.25,30.0}) for(const double x:{12.75,43.75}) {
            const double px=x*m[0]+y*m[2]+test.tx,py=x*m[1]+y*m[3]+test.ty;
            clip[0]=std::min(clip[0],px);clip[1]=std::min(clip[1],py);
            clip[2]=std::max(clip[2],px);clip[3]=std::max(clip[3],py);
        }
        for(auto& edge:clip) edge=std::floor((std::floor(edge*16+.5)+7)/16);
        const double det=m[0]*m[3]-m[1]*m[2];
        for(std::uint32_t y=0;y<64U;++y) for(std::uint32_t x=0;x<128U;++x) {
            const double px=x+.5-test.tx,py=y+.5-test.ty;
            const double local_x=(px*m[3]-py*m[2])/det,local_y=(py*m[0]-px*m[1])/det;
            const double u=(local_x-ax)/ex,v=(local_y*sy-ay)/ey;
            std::array<std::uint8_t,4U> expected{0,0,0,255};
            if(x>=clip[0] && x<clip[2] && y>=clip[1] && y<clip[3] && u>=0 && u<1 && v>=0 && v<1) {
                if(test.output==shader_padding_output::constant) expected={64,128,191,255};
                else if(test.output==shader_padding_output::uv_squared) expected={
                    static_cast<std::uint8_t>(std::floor(u*u*255+.5)),static_cast<std::uint8_t>(std::floor(v*v*255+.5)),0,255};
                else if(test.output==shader_padding_output::image) expected[u<.5?0U:1U]=255;
                else if(test.output==shader_padding_output::derivatives) expected={0,0,16,255};
            }
            const auto* actual=images[0].data()+(y*128U+x)*4U;
            const bool same=std::equal(expected.begin(),expected.end(),actual);
            if(!same) std::fprintf(stderr,"Affine shader variant=%u pixel=(%u,%u) actual=(%u,%u,%u,%u) expected=(%u,%u,%u,%u)\n",
                variant,x,y,actual[0],actual[1],actual[2],actual[3],expected[0],expected[1],expected[2],expected[3]);
            require(same,"affine source differs from independent mapping control");
        }
    }
}
} // namespace progpu::native::tests
