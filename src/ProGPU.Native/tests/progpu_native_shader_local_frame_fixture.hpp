#pragma once

#include "progpu_native_shader_padding_fixture.hpp"

namespace progpu::native::tests {

enum class shader_local_history { flat, nested, separately_narrowed, intermediate_bounds_rounding };
struct shader_local_case {
    shader_padding_output output;
    float dpi;
    std::array<double, 4U> padding;
    std::array<std::uint32_t, 4U> allocation; // original independent scale-space A/E
    shader_local_history history{shader_local_history::flat};
    bool clipped{};
};
// Same eighteen integral-final source inputs as the independent Microsoft WPF
// reference. GPU UVs retain hardware pixel centers; SoftwareOnly's integer
// phase is deliberately not imported into this renderer oracle.
inline constexpr std::array shader_local_cases{
    shader_local_case{shader_padding_output::input, 1, {}, {16,16,17,8}},
    shader_local_case{shader_padding_output::constant, 1, {}, {16,16,17,8}},
    shader_local_case{shader_padding_output::input, 1, {.25,1.25,.5,1.5}, {16,16,18,9}},
    shader_local_case{shader_padding_output::constant, 1, {.25,1.25,.5,1.5}, {16,16,18,9}},
    shader_local_case{shader_padding_output::uv, 1, {2,6.25,4,11.5}, {12,14,32,16}},
    shader_local_case{shader_padding_output::derivatives, 1, {.25,1.25,.5,1.5}, {16,16,18,9}},
    shader_local_case{shader_padding_output::image, 1, {.25,1.25,.5,1.5}, {16,16,18,9}},
    shader_local_case{shader_padding_output::input, 2, {}, {33,32,32,16}},
    shader_local_case{shader_padding_output::constant, 2, {.25,1.25,.5,1.5}, {32,32,36,18}},
    shader_local_case{shader_padding_output::uv, 2, {}, {33,32,32,16}},
    shader_local_case{shader_padding_output::derivatives, 2, {.25,1.25,.5,1.5}, {32,32,36,18}},
    shader_local_case{shader_padding_output::image, 2, {.25,1.25,.5,1.5}, {32,32,36,18}},
    shader_local_case{shader_padding_output::constant, 2, {.25,1.25,.5,1.5}, {32,32,36,18}, shader_local_history::flat, true},
    shader_local_case{shader_padding_output::constant, 1, {}, {16,16,17,8}},
    shader_local_case{shader_padding_output::constant, 1, {.25,1.25,.5,1.5}, {16,16,18,9}},
    shader_local_case{shader_padding_output::constant, 1, {}, {16,16,17,8}},
    shader_local_case{shader_padding_output::constant, 1, {}, {16,16,17,8}, shader_local_history::nested},
    shader_local_case{shader_padding_output::constant, 2, {.25,1.25,.5,1.5}, {32,32,36,18}, shader_local_history::nested}
};

// Actual original packets and C scene request, never a fabricated positive v4
// descriptor. One live channel owns all source generations and is retired before
// any GPU replay. Optional controls change real source history, not wire claims.
inline progpu_native_mil_status build_shader_local_scene(progpu_native_mil_channel* channel,
    std::uint32_t variant, const shader_local_case& test, std::vector<std::byte>& scene,
    bool baseline = false, double device_x = 2.0, double device_y = 3.0,
    double extra_scale = 1.0, double extra_scale_y = 1.0,
    double clip_shift_x = 0.0, bool ancestor_clip = false) {
    using mil::command;
    using mil_clip_fixture_detail::append;
    using mil_clip_fixture_detail::packet;
    const double dpi = test.dpi;
    std::vector<std::byte> batch, content;
    if (progpu_native_mil_channel_get_resource_generation(channel, 1U) == 0U) {
        constexpr std::array types{39U,43U,47U,75U,38U,33U,34U,69U,80U,95U,39U,39U,66U,66U};
        for (std::uint32_t i = 0U; i < types.size(); ++i)
            packet(batch, command::channel_create_resource, i + 1U, types[i]);
        for (const auto visual : {1U,11U,12U}) packet(batch, command::visual_create, visual);
        packet(batch, command::visual_insert_child_at, 11U, 12U, 0U);
        packet(batch, command::visual_insert_child_at, 12U, 1U, 0U);
        packet(batch, command::generic_target_create, 3U, std::uint64_t{}, std::uint64_t{}, 96U,64U,0U);
        packet(batch, command::target_set_root, 3U, 11U);
        packet(batch, command::solid_color_brush, 4U, 1.0, progpu_native_color{1,1,1,1}, 0U,0U,0U,0U);
        packet(batch, command::implicit_input_brush, 7U, 1.0, 0U,0U,0U);
    }
    const double bounds_translation = test.history == shader_local_history::intermediate_bounds_rounding ? 0x1p24 : 0.0;
    packet(batch, command::visual_set_offset, 11U, device_x / dpi - bounds_translation, device_y / dpi);
    constexpr double separate = 1.0 + 0x1p-24;
    const double parent_scale = test.history == shader_local_history::nested ? 2.0 :
        test.history == shader_local_history::separately_narrowed ? separate : 1.0;
    const double child_scale = test.history == shader_local_history::nested ? .5 :
        test.history == shader_local_history::separately_narrowed ? separate : 1.0;
    packet(batch, command::matrix_transform, 13U, parent_scale,0.0,0.0,parent_scale,0.0,0.0,0U);
    packet(batch, command::matrix_transform, 14U, child_scale * extra_scale,0.0,0.0,child_scale * extra_scale_y,bounds_translation,0.0,0U);
    packet(batch, command::visual_set_transform, 12U, 13U);
    packet(batch, command::visual_set_transform, 1U, 14U);
    packet(batch, command::visual_set_render_options, 1U, 3U,1U,0U,3U,0U,0U,0U);
    packet(batch, command::rectangle_geometry, 8U, 0.0,0.0, (36.0+clip_shift_x)/dpi,34.0/dpi,
        20.0/dpi,10.0/dpi,0U,0U,0U,0U);
    packet(batch, command::visual_set_clip, 1U, test.clipped && !ancestor_clip ? 8U : 0U);
    packet(batch, command::visual_set_clip, 11U, test.clipped && ancestor_clip ? 8U : 0U);
    packet(batch, command::image_brush, 9U, 1.0, std::array{0.0,0.0,1.0,1.0},
        std::array{0.0,0.0,1.0,1.0}, .707,1.414,0U,0U,0U,1U,1U,0U,0U,1U,0U,1U,1U,0U,10U);
    const auto program = shader_padding_program(test.output);
    append(batch, static_cast<std::uint32_t>(24U + program.size() * sizeof(std::uint32_t)));
    append(batch, static_cast<std::uint32_t>(command::pixel_shader)); append(batch, 6U); append(batch, 0U);
    append(batch, static_cast<std::uint32_t>(program.size() * sizeof(std::uint32_t))); append(batch, 0U);
    const auto code = std::as_bytes(std::span(program)); batch.insert(batch.end(), code.begin(), code.end());
    append_shader_padding_effect(batch, test.padding, test.output);
    packet(batch, command::visual_set_effect, 1U, baseline ? 0U : 5U);
    packet(content, command::draw_rectangle, 16.75,16.25,15.5,7.5,4U,0U);
    append(batch, static_cast<std::uint32_t>(16U + content.size()));
    append(batch, static_cast<std::uint32_t>(command::render_data)); append(batch, 2U);
    append(batch, static_cast<std::uint32_t>(content.size())); batch.insert(batch.end(), content.begin(), content.end());
    packet(batch, command::visual_set_content, 1U, 2U);
    const auto generation = progpu_native_mil_channel_get_resource_generation(channel, 5U);
    constexpr std::array<std::uint8_t,8U> pixels{255,0,0,255,0,255,0,255};
    if (progpu_native_mil_channel_apply(channel, batch.data(), batch.size(), nullptr) != PROGPU_NATIVE_MIL_STATUS_SUCCESS ||
        progpu_native_mil_channel_get_resource_generation(channel, 5U) <= generation ||
        progpu_native_mil_channel_set_visual_cache_bounds(channel, 1U,16.75,16.25,15.5,7.5) != PROGPU_NATIVE_MIL_STATUS_SUCCESS ||
        progpu_native_mil_channel_set_bitmap_source_rgba8_with_dpi(channel,10U,2U,1U,8U,pixels.data(),pixels.size(),
            144.0,192.0) != PROGPU_NATIVE_MIL_STATUS_SUCCESS) return PROGPU_NATIVE_MIL_STATUS_INVALID_GRAPH;
    const progpu_native_mil_scene_build_request request{
        sizeof(request),0U,3U,0U,baseline ? 0x9498U : 0x9497U,variant + 1U,dpi,dpi,0U,variant + 1U};
    std::size_t written{};
    progpu_native_mil_scene_build_result result{}; result.struct_size = sizeof(result);
    const auto status = progpu_native_mil_channel_build_scene_with_request(channel,&request,nullptr,0U,&written,nullptr,&result);
    if (status != PROGPU_NATIVE_MIL_STATUS_SUCCESS) return status;
    std::vector<std::byte> candidate(written);
    const auto final_status = progpu_native_mil_channel_build_scene_with_request(channel,&request,
        candidate.data(),candidate.size(),&written,nullptr,&result);
    if (final_status == PROGPU_NATIVE_MIL_STATUS_SUCCESS && written == candidate.size()) scene = std::move(candidate);
    return final_status;
}

// Negative wire controls retain the same source scene, but explicitly remove the
// new contract. They remain valid legacy descriptors and MUST fail preflight;
// admitting new actual source input must not weaken old version validation.
inline bool shader_local_legacy_wire(std::vector<std::byte>& scene) {
    progpu_native_scene_header header{}; std::memcpy(&header,scene.data(),sizeof(header));
    for (std::uint32_t i = 0U; i < header.resource_count; ++i) {
        const auto offset = header.resource_offset + i * header.resource_stride;
        progpu_native_scene_resource resource{}; std::memcpy(&resource,scene.data()+offset,sizeof(resource));
        if (resource.kind != PROGPU_NATIVE_SCENE_RESOURCE_WPF_SHADER_EFFECT) continue;
        if (resource.payload_size != sizeof(progpu_native_scene_shader_effect_capture)) return false;
        progpu_native_scene_shader_effect_capture source{};
        std::memcpy(&source,scene.data()+resource.payload_offset,sizeof(source));
        if (source.derivative_register != PROGPU_NATIVE_SCENE_NO_INDEX) {
            const progpu_native_scene_shader_effect_derivatives old{
                sizeof(old),3U,source.sampler_resource_index,source.derivative_register,0U,{0U,0U,0U},source.program};
            std::memcpy(scene.data()+resource.payload_offset,&old,sizeof(old)); resource.payload_size=sizeof(old);
        } else if (source.sampler_resource_index != PROGPU_NATIVE_SCENE_NO_INDEX) {
            const progpu_native_scene_shader_effect_picture old{sizeof(old),2U,source.sampler_resource_index,0U,source.program};
            std::memcpy(scene.data()+resource.payload_offset,&old,sizeof(old)); resource.payload_size=sizeof(old);
        } else {
            std::memcpy(scene.data()+resource.payload_offset,&source.program,sizeof(source.program));
            resource.payload_size=sizeof(source.program);
        }
        std::memcpy(scene.data()+offset,&resource,sizeof(resource)); return true;
    }
    return false;
}

template<class Render, class Require>
void verify_original_shader_local_frame_pixels(Render render, Require require) {
    std::array<std::vector<std::byte>,shader_local_cases.size()> scenes, baselines;
    {
        progpu_native_mil_channel* raw{};
        require(progpu_native_mil_channel_create(&raw)==PROGPU_NATIVE_MIL_STATUS_SUCCESS,"local capture channel creation failed");
        mil_clip_channel owner(raw);
        for (std::uint32_t i=0U;i<scenes.size();++i) {
            require(build_shader_local_scene(raw,i,shader_local_cases[i],scenes[i])==PROGPU_NATIVE_MIL_STATUS_SUCCESS,
                "original local-frame source rejected");
            if (shader_local_cases[i].output==shader_padding_output::input)
                require(build_shader_local_scene(raw,i,shader_local_cases[i],baselines[i],true)==PROGPU_NATIVE_MIL_STATUS_SUCCESS,
                    "original ordinary drawing baseline rejected");
        }
    }
    for (std::uint32_t variant=0U;variant<scenes.size();++variant) {
        const auto& test=shader_local_cases[variant];
        progpu_native_scene_header header{}; std::memcpy(&header,scenes[variant].data(),sizeof(header));
        std::vector<std::uint8_t> baseline;
        if (!baselines[variant].empty()) {
            progpu_native_scene_header source{}; std::memcpy(&source,baselines[variant].data(),sizeof(source));
            progpu_native_layer_metrics layers{}; layers.struct_size=sizeof(layers);
            progpu_native_scene_frame_metrics frame{}; frame.struct_size=sizeof(frame);
            baseline=render(true,baselines[variant],source,test,1U,PROGPU_NATIVE_STATUS_SUCCESS,layers,frame);
            require(baseline.size()==96U*64U*4U && frame.submission_count==1U && frame.command_count==source.command_count,
                "local-frame ordinary baseline size/counters differ");
            bool ink=false;
            for(std::size_t i=0U;i<baseline.size();i+=4U) {
                require(baseline[i]==baseline[i+1U] && baseline[i]==baseline[i+2U] &&
                    (baseline[i]==0U || baseline[i]==255U) && baseline[i+3U]==255U,"ordinary baseline is not binary opaque white");
                ink |= baseline[i]!=0U;
            }
            require(ink,"ordinary baseline is empty");
        }
        std::array<std::vector<std::uint8_t>,3U> images;
        for(std::uint32_t replay=0U;replay<3U;++replay) {
            progpu_native_layer_metrics layers{}; layers.struct_size=sizeof(layers);
            progpu_native_scene_frame_metrics frame{}; frame.struct_size=sizeof(frame);
            const std::uint32_t submissions=replay!=1U && test.output==shader_padding_output::image ? 2U:1U;
            images[replay]=render(replay==2U,scenes[variant],header,test,submissions,PROGPU_NATIVE_STATUS_SUCCESS,layers,frame);
            require(frame.command_count==header.command_count && frame.submission_count==submissions &&
                layers.effect_count==1U && layers.effect_kind==PROGPU_NATIVE_GROUP_EFFECT_WPF_SHADER &&
                layers.effect_pass_count==(replay==1U?0U:1U) && layers.effect_cache_hit==(replay==1U?1U:0U) &&
                layers.effect_uniform_upload_bytes==(replay==1U?0U:528U),"local capture source/cache/pass counters differ");
        }
        require(images[0]==images[1] && images[0]==images[2] && images[0].size()==96U*64U*4U,
            "local capture cold/warm/independent pixels differ");
        for(std::uint32_t y=0U;y<64U;++y) for(std::uint32_t x=0U;x<96U;++x) {
            const auto index=(y*96U+x)*4U;
            std::array<std::uint8_t,4U> expected{0,0,0,255};
            const auto left=test.allocation[0]+2U,top=test.allocation[1]+3U;
            const auto extent_x=test.allocation[2],extent_y=test.allocation[3];
            if (!baseline.empty()) std::copy_n(baseline.data()+index,4U,expected.data());
            else if(x>=left && x<left+extent_x && y>=top && y<top+extent_y &&
                (!test.clipped || (x>=38U && x<58U && y>=37U && y<47U))) {
                if(test.output==shader_padding_output::constant) expected={64,128,191,255};
                if(test.output==shader_padding_output::image) expected[x-left<extent_x/2U?0U:1U]=255U;
                if(test.output==shader_padding_output::derivatives) {
                    expected[0]=static_cast<std::uint8_t>((255U+extent_x/2U)/extent_x);
                    expected[1]=static_cast<std::uint8_t>((255U+extent_y/2U)/extent_y);
                }
                if(test.output==shader_padding_output::uv) {
                    expected[0]=static_cast<std::uint8_t>(((2U*(x-left)+1U)*255U+extent_x)/(2U*extent_x));
                    expected[1]=static_cast<std::uint8_t>(((2U*(y-top)+1U)*255U+extent_y)/(2U*extent_y));
                }
            }
            // Independent original aliased output clip. Retain the complete
            // allocation above for UVs/derivatives, including transparent border.
            const auto edge = [](double local, double padding, double dpi, double offset) {
                const auto mapped = static_cast<float>((static_cast<float>(local) + static_cast<float>(padding)) *
                    static_cast<float>(dpi) + static_cast<float>(offset));
                return static_cast<std::int32_t>(std::floor((std::floor(static_cast<double>(mapped * 16.0F) + .5) + 7.0) / 16.0));
            };
            const auto visible_left=edge(16.75,-test.padding[2],test.dpi,2.0);
            const auto visible_top=edge(16.25,-test.padding[0],test.dpi,3.0);
            const auto visible_right=edge(32.25,test.padding[3],test.dpi,2.0);
            const auto visible_bottom=edge(23.75,test.padding[1],test.dpi,3.0);
            if (static_cast<std::int32_t>(x)<visible_left || static_cast<std::int32_t>(x)>=visible_right ||
                static_cast<std::int32_t>(y)<visible_top || static_cast<std::int32_t>(y)>=visible_bottom) expected={0,0,0,255};
            const auto* actual=images[0].data()+index;
            const bool same=std::equal(expected.begin(),expected.end(),actual);
            if(!same) std::fprintf(stderr,"Shader local frame variant=%u pixel=(%u,%u) actual=(%u,%u,%u,%u) expected=(%u,%u,%u,%u)\n",
                variant,x,y,actual[0],actual[1],actual[2],actual[3],expected[0],expected[1],expected[2],expected[3]);
            require(same,"original local capture input/UV/derivative/sampler/final clip differs");
        }
    }
    for(const auto variant:{0U,5U,6U}) {
        auto legacy=scenes[variant]; require(shader_local_legacy_wire(legacy),"legacy local capture control missing");
        progpu_native_scene_header header{}; std::memcpy(&header,legacy.data(),sizeof(header));
        progpu_native_layer_metrics layers{}; layers.struct_size=sizeof(layers);
        progpu_native_scene_frame_metrics frame{}; frame.struct_size=sizeof(frame);
        const auto pixels=render(false,legacy,header,shader_local_cases[variant],0U,PROGPU_NATIVE_STATUS_UNSUPPORTED,layers,frame);
        require(pixels.empty() && frame.submission_count==0U,"legacy fractional wire acquired new admission");
    }
}
} // namespace progpu::native::tests
