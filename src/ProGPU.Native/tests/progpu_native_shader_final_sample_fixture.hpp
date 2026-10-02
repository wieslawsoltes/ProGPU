#pragma once

#include "progpu_native_shader_local_frame_fixture.hpp"

namespace progpu::native::tests {

struct shader_final_sample_case {
    shader_local_case source;
    double x{2.0}, y{3.0}, scale_x{1.0}, scale_y{1.0};
    bool aliased{true}, ancestor_clip{};
    double clip_shift{};
    double parent_opacity{1.0};
};

// Independent final-sample controls. Power-of-two target/capture extents isolate
// the nonlinear UV placement from hardware filtering precision. Constant cases
// separately exercise the full non-dyadic source decomposition and source clips.
inline constexpr std::array shader_final_sample_cases{
    shader_final_sample_case{{shader_padding_output::constant,1,{},{}},2.25,3.5},
    shader_final_sample_case{{shader_padding_output::constant,1.25F,{},{} }},
    shader_final_sample_case{{shader_padding_output::constant,1.5F,{.25,1.25,.5,1.5},{}},2.25,3.5,1.25,.75},
    shader_final_sample_case{{shader_padding_output::constant,1,{},{}},2.25,3.5,1,1,false},
    shader_final_sample_case{{shader_padding_output::uv_squared,1,{2,6.25,4,11.5},{}},2.25,3.5},
    shader_final_sample_case{{shader_padding_output::uv_squared,1,{2,6.25,4,11.5},{}},3.75,4.25},
    shader_final_sample_case{{shader_padding_output::constant,2,{.25,1.25,.5,1.5},{},shader_local_history::flat,true},2,3,1,1,true,false,.75},
    shader_final_sample_case{{shader_padding_output::constant,2,{.25,1.25,.5,1.5},{},shader_local_history::flat,true},2,3,1,1,true,true,.75},
    shader_final_sample_case{{shader_padding_output::constant,1.3F,{},{}},2.25,3.5},
    shader_final_sample_case{{shader_padding_output::input,1,{2,6.25,4,11.5},{}},2.25,3.5},
    shader_final_sample_case{{shader_padding_output::image,1,{2,6.25,4,11.5},{}},2.25,3.5},
    shader_final_sample_case{{shader_padding_output::derivatives,1,{2,6.25,4,11.5},{}},2.25,3.5},
    shader_final_sample_case{{shader_padding_output::constant,1,{},{}},2.25,3.5,1,1,true,false,0,.25},
    shader_final_sample_case{{shader_padding_output::uv_squared,1,{2,6.25,4,11.5},{}},2.25,3.5,1,1,true,false,0,.25},
    shader_final_sample_case{{shader_padding_output::constant,1,{},{}},200.25,3.5,1,1,true,false,0,.25},
    shader_final_sample_case{{shader_padding_output::constant,1,{},{}},2.25,3.5,1,1,true,false,0,.25}
};

template<class Render, class Require>
void verify_original_shader_final_samples(Render render, Require require) {
    std::array<std::vector<std::byte>,shader_final_sample_cases.size()> scenes;
    {
        progpu_native_mil_channel* raw{};
        require(progpu_native_mil_channel_create(&raw)==PROGPU_NATIVE_MIL_STATUS_SUCCESS,
            "final-sample source channel creation failed");
        mil_clip_channel owner(raw);
        for(std::uint32_t i=0U;i<scenes.size();++i) {
            const auto& test=shader_final_sample_cases[i];
            require(build_shader_local_scene(raw,100U+i,test.source,scenes[i],false,test.x,test.y,
                test.scale_x,test.scale_y,test.clip_shift,test.ancestor_clip,test.aliased,128U,
                test.parent_opacity)==PROGPU_NATIVE_MIL_STATUS_SUCCESS,
                "actual final-sample source was rejected");
            progpu_native_scene_header header{}; std::memcpy(&header,scenes[i].data(),sizeof(header));
            bool selected=false;
            for(std::uint32_t resource_index=0U;resource_index<header.resource_count;++resource_index) {
                progpu_native_scene_resource resource{};
                std::memcpy(&resource,scenes[i].data()+header.resource_offset+resource_index*header.resource_stride,sizeof(resource));
                if(resource.kind!=PROGPU_NATIVE_SCENE_RESOURCE_WPF_SHADER_EFFECT) continue;
                require(resource.payload_size==sizeof(progpu_native_scene_shader_effect_samples),
                    "new source silently selected a legacy shader frame");
                progpu_native_scene_shader_effect_samples value{};
                std::memcpy(&value,scenes[i].data()+resource.payload_offset,sizeof(value));
                require(value.version==5U && value.input_resource_index<resource_index &&
                    (test.source.output==shader_padding_output::image
                        ? value.sampler_resource_index<resource_index && value.sampler_resource_index!=value.input_resource_index
                        : value.sampler_resource_index==PROGPU_NATIVE_SCENE_NO_INDEX) &&
                    value.derivative_register==(test.source.output==shader_padding_output::derivatives?0U:PROGPU_NATIVE_SCENE_NO_INDEX) &&
                    value.frame.clip_antialias==(test.aliased?0U:1U),"final-sample owned source identity differs");
                selected=true;
            }
            require(selected,"final-sample program absent");
            if(test.parent_opacity!=1.0) {
                std::uint32_t depth=0U; bool nested=false;
                for(std::uint32_t command_index=0U;command_index<header.command_count;++command_index) {
                    progpu_native_scene_command command{};
                    std::memcpy(&command,scenes[i].data()+header.command_offset+
                        command_index*header.command_stride,sizeof(command));
                    if(command.kind==PROGPU_NATIVE_SCENE_COMMAND_PUSH_LAYER) {
                        progpu_native_scene_layer layer{};
                        std::memcpy(&layer,scenes[i].data()+command.payload_offset,sizeof(layer));
                        if(layer.effect_resource_index!=PROGPU_NATIVE_SCENE_NO_INDEX) nested=depth!=0U;
                        ++depth;
                    } else if(command.kind==PROGPU_NATIVE_SCENE_COMMAND_POP_LAYER) {
                        require(depth!=0U,"nested source layer underflow"); --depth;
                    }
                }
                require(nested && depth==0U,"source shader did not retain its actual parent target");
            }
        }
    } // Source packets, channel and original resource handles retire before GPU use.
    for(std::uint32_t variant=0U;variant<scenes.size();++variant) {
        const auto& test=shader_final_sample_cases[variant];
        progpu_native_scene_header header{}; std::memcpy(&header,scenes[variant].data(),sizeof(header));
        std::array<std::vector<std::uint8_t>,3U> images;
        for(std::uint32_t replay=0U;replay<3U;++replay) {
            progpu_native_layer_metrics layers{}; layers.struct_size=sizeof(layers);
            progpu_native_scene_frame_metrics frame{}; frame.struct_size=sizeof(frame);
            const bool outside=test.x==200.25;
            const auto submissions=replay==1U || outside?1U:test.source.output==shader_padding_output::image?3U:2U;
            images[replay]=render(replay==2U,scenes[variant],header,test.source,submissions,layers,frame);
            require(frame.command_count==header.command_count && frame.submission_count==submissions &&
                frame.draw_call_count==(outside?1U:test.parent_opacity==1.0?1U:2U) &&
                layers.effect_count==1U && layers.effect_kind==PROGPU_NATIVE_GROUP_EFFECT_WPF_SHADER &&
                layers.effect_pass_count==(outside?0U:1U) && layers.effect_cache_hit==0U &&
                layers.content_pass_count==(test.parent_opacity==1.0?0U:1U) &&
                layers.effect_uniform_upload_bytes==(replay==1U || outside?0U:592U),
                "final-target source retention/pass/upload counters differ");
        }
        require(images[0]==images[1] && images[0]==images[2] && images[0].size()==128U*64U*4U,
            "final-target cold/warm/independent pixels differ");
        // Independent original source operation order, no production frame,
        // projection, clip or resource-reader helper participates in this oracle.
        const auto mul=[](float a,float b){volatile float c=a*b;return c;};
        const auto add=[](float a,float b){volatile float c=a+b;return c;};
        const auto dpi=test.source.dpi;
        const auto tx=mul(static_cast<float>(test.x/dpi),dpi);
        const auto ty=mul(static_cast<float>(test.y/dpi),dpi);
        const auto& p=test.source.padding;
        const float l=static_cast<float>(16.75)-static_cast<float>(p[2]);
        const float t=static_cast<float>(16.25)-static_cast<float>(p[0]);
        const float r=static_cast<float>(32.25)+static_cast<float>(p[3]);
        const float b=static_cast<float>(23.75)+static_cast<float>(p[1]);
        std::array<float,4U> clip{
            add(mul(mul(l,static_cast<float>(test.scale_x)),dpi),tx),
            add(mul(mul(t,static_cast<float>(test.scale_y)),dpi),ty),
            add(mul(mul(r,static_cast<float>(test.scale_x)),dpi),tx),
            add(mul(mul(b,static_cast<float>(test.scale_y)),dpi),ty)};
        if(!test.aliased) {
            clip={std::floor(add(clip[0],-1)),std::floor(add(clip[1],-1)),
                std::ceil(add(clip[2],1)),std::ceil(add(clip[3],1))};
        }
        if(test.source.clipped) {
            const float left=add(mul(static_cast<float>((36.0+test.clip_shift)/dpi),dpi),tx);
            const float top=add(mul(static_cast<float>(34.0/dpi),dpi),ty);
            const float right=add(mul(static_cast<float>((56.0+test.clip_shift)/dpi),dpi),tx);
            const float bottom=add(mul(static_cast<float>(44.0/dpi),dpi),ty);
            clip={std::max(clip[0],left),std::max(clip[1],top),std::min(clip[2],right),std::min(clip[3],bottom)};
        }
        const auto integer=[](float value){return std::floor((std::floor(static_cast<double>(value*16.0F)+.5)+7.0)/16.0);};
        std::array<double,4U> limits{integer(clip[0]),integer(clip[1]),integer(clip[2]),integer(clip[3])};
        for(std::uint32_t y=0U;y<64U;++y) for(std::uint32_t x=0U;x<128U;++x) {
            std::array<std::uint8_t,4U> expected{0,0,0,255};
            bool covered=x>=limits[0] && x<limits[2] && y>=limits[1] && y<limits[3];
            // Only the explicit dyadic AA case reaches the quad's edge: it
            // uses actual single-sample coverage, not inflated-clip coverage.
            if(!test.aliased) covered=covered && x+.5>=16+test.x && x+.5<33+test.x &&
                y+.5>=16+test.y && y+.5<24+test.y;
            if(covered) {
                if(test.source.output==shader_padding_output::constant) expected={64,128,191,255};
                else if(test.source.output==shader_padding_output::uv_squared) {
                    const double u=(x+.5-(12+test.x))/32.0;
                    const double v=(y+.5-(14+test.y))/16.0;
                    expected={static_cast<std::uint8_t>(std::floor(u*u*255+.5)),
                        static_cast<std::uint8_t>(std::floor(v*v*255+.5)),0,255};
                } else if(test.source.output==shader_padding_output::derivatives) {
                    // Exact dyadic inverse of the complete 32x16 input frame.
                    expected={8,16,0,255};
                } else {
                    const auto source_x=static_cast<std::int32_t>(std::floor(x+.5-(12+test.x)));
                    const auto source_y=static_cast<std::int32_t>(std::floor(y+.5-(14+test.y)));
                    if(test.source.output==shader_padding_output::image) {
                        expected[source_x<16?0U:1U]=255U;
                    } else {
                        // The original aliased white rectangle occupies these
                        // integer texels in the complete padded input. Sample
                        // nearest only AFTER the final fractional placement.
                        const bool ink=source_x>=5 && source_x<20 && source_y>=2 && source_y<10;
                        if(ink) expected={255,255,255,255};
                    }
                }
                if(test.parent_opacity!=1.0) {
                    // The original completed shader is stored in its parent's
                    // RGBA8 surface before that parent's own alpha is applied.
                    // Keep this quantization boundary independent of shader UVs.
                    for(std::size_t channel=0U;channel<3U;++channel)
                        expected[channel]=static_cast<std::uint8_t>(std::floor(expected[channel]*test.parent_opacity+.5));
                }
            }
            const auto* actual=images[0].data()+(y*128U+x)*4U;
            const bool same=std::equal(expected.begin(),expected.end(),actual);
            if(!same) std::fprintf(stderr,"Shader final sample variant=%u pixel=(%u,%u) actual=(%u,%u,%u,%u) expected=(%u,%u,%u,%u)\n",
                variant,x,y,actual[0],actual[1],actual[2],actual[3],expected[0],expected[1],expected[2],expected[3]);
            require(same,"final-device shader output differs from independent original-frame control");
        }
    }
}
} // namespace progpu::native::tests
