#pragma once

#include "progpu_native_shader_local_frame_fixture.hpp"

namespace progpu::native::tests {

// Exact differential against the same original ordinary drawing, without an
// effect. This tests shared clip coverage/ownership, not an ideal ellipse or a
// new claim about the existing curve rasterizer's Windows numeric parity.
inline progpu_native_mil_status build_shader_source_mask_scene(progpu_native_mil_channel* channel,
    std::uint32_t variant, bool baseline, std::vector<std::byte>& scene, bool unproven = false) {
    using mil_clip_fixture_detail::packet;
    const float dpi=variant==5U?2.0F:1.0F;
    const double opacity=variant==4U?.25:1.0;
    const shader_local_case test{shader_padding_output::constant,dpi,{},{}};
    // A single half-ULP component narrows to identity in both paths. A real
    // mismatch needs two non-dyadic float pushes: the ordinary double product
    // differs from the original source's rounded float composition.
    const std::array<double, 6U> unproven_parent{.1, 0, 0, 1, 0, 0};
    std::vector<std::byte> scratch;
    auto status=build_shader_local_scene(channel,200U+variant,test,scratch,baseline,2.25,3.5,
        unproven?.1:variant==7U?1.0+0x1p-24:1.0,1.0,0.0,false,true,128U,opacity,
        nullptr, unproven ? &unproven_parent : nullptr);
    if(status!=PROGPU_NATIVE_MIL_STATUS_SUCCESS) return status;
    std::vector<std::byte> batch;
    if(progpu_native_mil_channel_get_resource_generation(channel,15U)==0U) {
        packet(batch,mil::command::channel_create_resource,15U,70U);
        packet(batch,mil::command::channel_create_resource,16U,69U);
        packet(batch,mil::command::channel_create_resource,17U,72U);
        packet(batch,mil::command::channel_create_resource,18U,70U);
    }
    packet(batch,mil::command::visual_set_alpha,11U,opacity);
    packet(batch,mil::command::solid_color_brush,4U,1.0,progpu_native_color{.25F,.5F,.75F,1},0U,0U,0U,0U);
    packet(batch,mil::command::ellipse_geometry,15U,variant==6U?3.0:5.0,2.0,24.0,20.0,0U,0U,0U,0U);
    packet(batch,mil::command::ellipse_geometry,18U,4.0,3.0,25.0,20.0,0U,0U,0U,0U);
    packet(batch,mil::command::rectangle_geometry,16U,0.0,0.0,23.5,19.0,1.0,2.0,0U,0U,0U,0U);
    packet(batch,mil::command::combined_geometry,17U,0U,3U,15U,16U);
    packet(batch,mil::command::visual_set_clip,1U,variant==1U?0U:variant==2U || variant==5U?17U:15U);
    packet(batch,mil::command::visual_set_clip,11U,variant==1U?15U:variant==3U?18U:0U);
    status=progpu_native_mil_channel_apply(channel,batch.data(),batch.size(),nullptr);
    if(status!=PROGPU_NATIVE_MIL_STATUS_SUCCESS) return status;
    const progpu_native_mil_scene_build_request request{
        sizeof(request),0U,3U,0U,baseline?0x94A1U:0x94A0U,variant+1U,dpi,dpi,0U,variant+1U};
    std::size_t size{};
    progpu_native_mil_scene_build_result result{};result.struct_size=sizeof(result);
    status=progpu_native_mil_channel_build_scene_with_request(channel,&request,nullptr,0U,&size,nullptr,&result);
    if(status!=PROGPU_NATIVE_MIL_STATUS_SUCCESS) return status;
    std::vector<std::byte> candidate(size);
    status=progpu_native_mil_channel_build_scene_with_request(channel,&request,candidate.data(),candidate.size(),&size,nullptr,&result);
    if(status==PROGPU_NATIVE_MIL_STATUS_SUCCESS && size==candidate.size()) scene=std::move(candidate);
    return status;
}

template<class Render,class Require>
void verify_shader_source_masks(Render render,Require require) {
    std::array<std::vector<std::byte>,8U> scenes,baselines;
    {
        progpu_native_mil_channel* raw{};
        require(progpu_native_mil_channel_create(&raw)==PROGPU_NATIVE_MIL_STATUS_SUCCESS,"shader mask channel unavailable");
        mil_clip_channel owner(raw);
        for(std::uint32_t variant=0U;variant<scenes.size();++variant) {
            require(build_shader_source_mask_scene(raw,variant,false,scenes[variant])==PROGPU_NATIVE_MIL_STATUS_SUCCESS &&
                build_shader_source_mask_scene(raw,variant,true,baselines[variant])==PROGPU_NATIVE_MIL_STATUS_SUCCESS,
                "actual source curve/Boolean mask did not compile");
            progpu_native_scene_header header{};std::memcpy(&header,scenes[variant].data(),sizeof(header));
            bool found=false;
            for(std::uint32_t index=0U;index<header.command_count;++index) {
                progpu_native_scene_command command{};
                std::memcpy(&command,scenes[variant].data()+header.command_offset+index*header.command_stride,sizeof(command));
                if(command.kind!=PROGPU_NATIVE_SCENE_COMMAND_PUSH_LAYER) continue;
                progpu_native_scene_layer layer{};std::memcpy(&layer,scenes[variant].data()+command.payload_offset,sizeof(layer));
                if(layer.effect_resource_index==PROGPU_NATIVE_SCENE_NO_INDEX) continue;
                require(layer.mask_resource_index!=PROGPU_NATIVE_SCENE_NO_INDEX,"source shape was replaced by an output envelope");
                progpu_native_scene_resource resource{};
                std::memcpy(&resource,scenes[variant].data()+header.resource_offset+layer.mask_resource_index*header.resource_stride,sizeof(resource));
                progpu_native_scene_layer_vector_mask mask{};
                std::memcpy(&mask,scenes[variant].data()+resource.payload_offset,sizeof(mask));
                require(mask.kind==PROGPU_NATIVE_SCENE_LAYER_MASK_VECTOR_CLIP_CHAIN && mask.opacity==1.0F &&
                    mask.path_count==(variant==3U?2U:1U) && mask.segment_count>=4U &&
                    ((variant==2U || variant==5U)?mask.boolean_node_count!=0U:mask.boolean_node_count==0U),
                    "source mask lost its retained curve/Boolean/intersection identity");
                found=true;
            }
            require(found,"source masked shader missing");
        }
        const std::vector<std::byte> sentinel{std::byte{0x5A}};
        auto unchanged=sentinel;
        require(build_shader_source_mask_scene(raw,0U,false,unchanged,true)==PROGPU_NATIVE_MIL_STATUS_UNSUPPORTED_COMMAND &&
            unchanged==sentinel,"unproven source mask mapping published an approximate frame");
    } // All source handles and geometry packets retire before either provider renders.
    for(std::uint32_t variant=0U;variant<scenes.size();++variant) {
        const float dpi=variant==5U?2.0F:1.0F;
        progpu_native_scene_header header{},baseline_header{};
        std::memcpy(&header,scenes[variant].data(),sizeof(header));
        std::memcpy(&baseline_header,baselines[variant].data(),sizeof(baseline_header));
        progpu_native_layer_metrics baseline_layers{};baseline_layers.struct_size=sizeof(baseline_layers);
        progpu_native_scene_frame_metrics baseline_frame{};baseline_frame.struct_size=sizeof(baseline_frame);
        const auto baseline=render(true,baselines[variant],baseline_header,dpi,true,1U,baseline_layers,baseline_frame,
            PROGPU_NATIVE_STATUS_SUCCESS);
        require(baseline.size()==128U*64U*4U,"source mask baseline extent differs");
        bool ink=false,partial=false;
        for(std::size_t i=0U;i<baseline.size();i+=4U) {
            require(baseline[i+3U]==255U,"source mask baseline lost opaque target alpha");
            ink|=baseline[i+2U]!=0U;
            partial|=baseline[i+2U]!=0U && baseline[i+2U]!=(variant==4U?48U:191U);
        }
        require(ink && partial,"source mask fixture did not retain nontrivial curve coverage");
        for(std::uint32_t replay=0U;replay<3U;++replay) {
            progpu_native_layer_metrics layers{};layers.struct_size=sizeof(layers);
            progpu_native_scene_frame_metrics frame{};frame.struct_size=sizeof(frame);
            const auto submissions=replay==1U?1U:2U;
            const auto pixels=render(replay==2U,scenes[variant],header,dpi,false,submissions,layers,frame,
                PROGPU_NATIVE_STATUS_SUCCESS);
            require(pixels==baseline,"final shader changed exact ordinary-source mask pixels");
            require(frame.submission_count==submissions && frame.command_count==header.command_count &&
                layers.mask_kind==PROGPU_NATIVE_GROUP_MASK_TEXTURE && layers.effect_count==1U &&
                layers.effect_pass_count==1U && layers.effect_cache_hit==0U &&
                layers.effect_uniform_upload_bytes==(replay==1U?0U:592U),
                "source mask retention/pass/uniform counters differ");
        }
    }
    // Mutate the current owner revision; replaying generation one here would
    // stop at stale-scene validation before testing the mask preflight.
    auto invalid=scenes.back();
    progpu_native_scene_header header{};std::memcpy(&header,invalid.data(),sizeof(header));
    ++header.generation;
    std::memcpy(invalid.data(), &header, sizeof(header));
    bool changed=false;
    for(std::uint32_t i=0U;i<header.resource_count;++i) {
        progpu_native_scene_resource resource{};
        std::memcpy(&resource,invalid.data()+header.resource_offset+i*header.resource_stride,sizeof(resource));
        if(resource.kind!=PROGPU_NATIVE_SCENE_RESOURCE_LAYER_MASK) continue;
        progpu_native_scene_layer_vector_mask mask{};
        std::memcpy(&mask,invalid.data()+resource.payload_offset,sizeof(mask));
        if(mask.kind!=PROGPU_NATIVE_SCENE_LAYER_MASK_VECTOR_CLIP_CHAIN) continue;
        mask.opacity=.5F;std::memcpy(invalid.data()+resource.payload_offset,&mask,sizeof(mask));
        ++resource.generation;
        std::memcpy(invalid.data()+header.resource_offset+i*header.resource_stride,&resource,sizeof(resource));
        changed=true;
    }
    require(changed,"raw source mask rejection control absent");
    progpu_native_layer_metrics layers{};layers.struct_size=sizeof(layers);
    progpu_native_scene_frame_metrics frame{};frame.struct_size=sizeof(frame);
    const auto rejected=render(false,invalid,header,1.0F,false,0U,layers,frame,PROGPU_NATIVE_STATUS_UNSUPPORTED);
    require(rejected.empty() && frame.submission_count==0U,"non-unit raw mask acquired source-clip admission");
}
} // namespace progpu::native::tests
