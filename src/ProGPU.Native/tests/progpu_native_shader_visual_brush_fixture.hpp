#pragma once

#include "progpu_native_shader_sampler_pixel_fixture.hpp"

namespace progpu::native::tests {

inline constexpr std::uint32_t shader_visual_brush_case_count = 9U;

inline void append_shader_visual_brush(std::vector<std::byte>& batch, std::uint32_t index,
    std::uint32_t source = 40U, bool empty_viewport = false) {
    using mil_clip_fixture_detail::packet;
    const bool absolute = index == 2U || index == 3U;
    packet(batch, mil::command::visual_brush, 5U, index == 1U ? .5 : 1.0,
        absolute ? index == 2U ? std::array{8.0,0.0,16.0,24.0} : std::array{0.0,0.0,32.0,24.0}
            : std::array{0.0,0.0,empty_viewport ? 0.0 : index == 4U ? .5 : 1.0,1.0},
        absolute ? index == 2U ? std::array{14.0,20.0,4.0,6.0} : std::array{10.0,20.0,8.0,6.0}
            : std::array{0.0,0.0,1.0,1.0},
        .707,1.414,0U,0U,0U,absolute ? 0U : 1U,absolute ? 0U : 1U,
        0U,0U,1U,index == 4U ? 4U : 0U,1U,1U,0U,source);
}

inline void append_visual_sampler_content(std::vector<std::byte>& batch, std::uint32_t visual,
    std::uint32_t content, std::span<const std::byte> commands) {
    using mil_clip_fixture_detail::append;
    using mil_clip_fixture_detail::packet;
    append(batch,static_cast<std::uint32_t>(16U+commands.size()));
    append(batch,static_cast<std::uint32_t>(mil::command::render_data));
    append(batch,content); append(batch,static_cast<std::uint32_t>(commands.size()));
    batch.insert(batch.end(),commands.begin(),commands.end());
    packet(batch,mil::command::visual_set_content,visual,content);
}

inline bool initialize_shader_visual_brush(progpu_native_mil_channel* channel, bool initialize_source = true) {
    using mil_clip_fixture_detail::packet;
    using mil::command;
    std::vector<std::byte> batch,content;
    for (const auto resource : std::array{
        std::array{1U,39U},std::array{2U,43U},std::array{4U,47U},std::array{5U,82U},
        std::array{6U,33U},std::array{7U,38U},std::array{8U,75U},std::array{40U,39U},
        std::array{41U,39U},std::array{42U,75U},std::array{43U,75U},
        std::array{44U,39U},std::array{45U,39U},std::array{46U,43U},std::array{47U,43U},
        std::array{48U,69U},std::array{49U,66U}})
        packet(batch,command::channel_create_resource,resource[0],resource[1]);
    packet(batch,command::visual_create,1U);
    if (initialize_source) packet(batch,command::visual_create,40U);
    for (const auto id : {41U,44U,45U}) packet(batch,command::visual_create,id);
    packet(batch,command::generic_target_create,4U,std::uint64_t{0U},std::uint64_t{0U},64U,64U,0U);
    packet(batch,command::target_set_root,4U,1U);
    packet(batch,command::visual_set_render_options,1U,3U,1U,0U,3U,0U,0U,0U);
    packet(batch,command::solid_color_brush,8U,1.0,progpu_native_color{1,1,1,1},0U,0U,0U,0U);
    append_shader_visual_brush(batch,0U);
    constexpr std::array<std::uint32_t,15U> program{
        0xFFFF0200U,0x0200001FU,0x80000000U,0xB0030000U,
        0x0200001FU,0x90000000U,0xA00F0800U,
        0x03000042U,0x800F0000U,0xB0E40000U,0xA0E40800U,
        0x02000001U,0x800F0800U,0x80E40000U,0xFFFFU};
    packet(batch,command::pixel_shader,6U,0U,static_cast<std::uint32_t>(sizeof(program)),0U,program);
    packet(batch,command::shader_effect,7U,0.0,0.0,0.0,0.0,6U,0xFFFFFFFFU,
        std::array<std::uint32_t,8U>{0U,0U,0U,0U,0U,0U,8U,4U},0U,1U,5U);
    packet(batch,command::visual_set_effect,1U,7U);
    packet(content,command::draw_rectangle,8.0,10.0,32.0,24.0,8U,0U);
    append_visual_sampler_content(batch,1U,2U,content);
    return progpu_native_mil_channel_apply(channel,batch.data(),batch.size(),nullptr) == PROGPU_NATIVE_MIL_STATUS_SUCCESS &&
        progpu_native_mil_channel_set_visual_cache_bounds(channel,1U,8,10,32,24) == PROGPU_NATIVE_MIL_STATUS_SUCCESS;
}

inline bool update_shader_visual_brush(progpu_native_mil_channel* channel,std::uint32_t index) {
    if (index >= shader_visual_brush_case_count) return false;
    using mil_clip_fixture_detail::packet;
    using mil::command;
    std::vector<std::byte> batch,content;
    packet(batch,command::solid_color_brush,42U,1.0,
        index >= 4U ? progpu_native_color{0,0,1,1} : progpu_native_color{1,0,0,1},0U,0U,0U,0U);
    packet(batch,command::solid_color_brush,43U,1.0,progpu_native_color{0,1,0,1},0U,0U,0U,0U);
    const double x=index == 7U ? -6.0 : 10.0, y=index == 7U ? 9.0 : 20.0;
    packet(content,command::draw_rectangle,x,y,index == 1U || index == 8U ? 6.0 : 4.0,6.0,42U,0U);
    append_visual_sampler_content(batch,44U,46U,content);
    content.clear(); packet(content,command::draw_rectangle,x+4.0,y,4.0,6.0,43U,0U);
    append_visual_sampler_content(batch,45U,47U,content);
    packet(batch,command::rectangle_geometry,48U,0.0,0.0,10.0,20.0,6.0,6.0,0U,0U,0U,0U);
    packet(batch,command::matrix_transform,49U,1.0,0.0,0.0,1.0,3.0,-2.0,0U);
    packet(batch,command::visual_set_alpha,41U,index == 1U ? .5 : 1.0);
    packet(batch,command::visual_set_clip,41U,index == 3U ? 48U : 0U);
    packet(batch,command::visual_set_transform,41U,index == 8U ? 49U : 0U);
    packet(batch,command::visual_remove_all_children,40U);
    packet(batch,command::visual_remove_all_children,41U);
    if (index != 6U) packet(batch,command::visual_insert_child_at,40U,41U,0U);
    packet(batch,command::visual_insert_child_at,41U,index == 8U ? 45U : 44U,0U);
    packet(batch,command::visual_insert_child_at,41U,index == 8U ? 44U : 45U,1U);
    append_shader_visual_brush(batch,index,index == 5U ? 0U : 40U);
    if (progpu_native_mil_channel_apply(channel,batch.data(),batch.size(),nullptr) != PROGPU_NATIVE_MIL_STATUS_SUCCESS)
        return false;
    // Independent literal descendant bounds. Root includes the inner clip and
    // transform; the inner visual's own content bounds stay in its local frame.
    return progpu_native_mil_channel_set_visual_cache_bounds(channel,41U,x,y,8,6) == PROGPU_NATIVE_MIL_STATUS_SUCCESS &&
        (index == 6U ? progpu_native_mil_channel_set_visual_source_empty_bounds(channel,40U)
            : progpu_native_mil_channel_set_visual_cache_bounds(channel,40U,
                index == 8U ? 13.0 : x,index == 8U ? 18.0 : y,index == 3U ? 6.0 : 8.0,6.0)) ==
        PROGPU_NATIVE_MIL_STATUS_SUCCESS;
}

inline progpu_native_mil_scene_build_request shader_visual_brush_request(std::uint32_t index) {
    return {sizeof(progpu_native_mil_scene_build_request),0U,4U,0U,0xB1258U,index+1U,1.0,1.0,0U,index+1U};
}

inline bool build_shader_visual_brush(progpu_native_mil_channel* channel,std::uint32_t index,
    std::vector<std::byte>& scene) {
    const auto request=shader_visual_brush_request(index);
    progpu_native_mil_scene_build_result result{}; result.struct_size=sizeof(result);
    std::size_t written{};
    if (progpu_native_mil_channel_build_scene_with_request(channel,&request,nullptr,0U,&written,nullptr,&result) !=
        PROGPU_NATIVE_MIL_STATUS_SUCCESS) return false;
    scene.resize(written);
    return progpu_native_mil_channel_build_scene_with_request(channel,&request,scene.data(),scene.size(),&written,
        nullptr,&result) == PROGPU_NATIVE_MIL_STATUS_SUCCESS && written == scene.size();
}

template<class Render,class Require>
void verify_shader_visual_brush_pixels(Render render,Require require) {
    std::array<std::vector<std::byte>,shader_visual_brush_case_count> scenes;
    {
        progpu_native_mil_channel* raw{};
        require(progpu_native_mil_channel_create(&raw) == PROGPU_NATIVE_MIL_STATUS_SUCCESS,"visual sampler channel");
        mil_clip_channel owner(raw);
        require(initialize_shader_visual_brush(raw),"visual sampler declaration");
        for (std::uint32_t index=0U; index<scenes.size(); ++index) {
            require(update_shader_visual_brush(raw,index),"visual sampler source mutation");
            require(build_shader_visual_brush(raw,index,scenes[index]),"visual sampler immutable capture");
        }
    }
    for (std::uint32_t index=0U; index<scenes.size(); ++index) {
        progpu_native_scene_header header{};
        require(scenes[index].size() >= sizeof(header),"visual sampler header");
        std::memcpy(&header,scenes[index].data(),sizeof(header));
        require(header.command_count == 3U,"visual sampler original source commands");
        std::array<std::vector<std::uint8_t>,3U> images;
        for (std::uint32_t replay=0U; replay<3U; ++replay) {
            progpu_native_layer_metrics layers{}; layers.struct_size=sizeof(layers);
            progpu_native_scene_frame_metrics frame{}; frame.struct_size=sizeof(frame);
            const std::uint64_t submissions=replay == 1U ? 1U : 2U;
            images[replay]=render(false,replay == 2U,scenes[index],header,submissions,layers,frame);
            require(frame.submission_count == submissions && frame.command_count == 3U,
                "visual sampler actual commands/submissions");
            require(layers.effect_kind == PROGPU_NATIVE_GROUP_EFFECT_WPF_SHADER && layers.effect_count == 1U &&
                layers.effect_pass_count == (replay == 1U ? 0U : 1U) && layers.effect_cache_hit == (replay == 1U ? 1U : 0U),
                "visual sampler retained effect execution");
        }
        require(images[0].size() == 64U*64U*4U && images[0] == images[1] && images[0] == images[2],
            "visual sampler complete cold/warm/independent frames");
        for (std::uint32_t y=0U; y<64U; ++y) for (std::uint32_t x=0U; x<64U; ++x) {
            std::array<std::uint8_t,4U> expected{0,0,0,255};
            if (x>=8U && x<40U && y>=10U && y<34U && index != 5U && index != 6U) {
                const auto local=x-8U;
                const bool inside=index == 2U ? local>=8U && local<24U : index != 3U || local<24U;
                const bool green=index == 2U || (index == 4U ? local%16U>=8U : local>=(index == 8U ? 24U : 16U));
                if (inside) expected[green ? 1U : index>=4U ? 2U : 0U]=index == 1U ? 64U : 255U;
            }
            const auto* actual=images[0].data()+(y*64U+x)*4U;
            if (!std::equal(expected.begin(),expected.end(),actual))
                std::fprintf(stderr,"VisualBrush sampler case=%u pixel=(%u,%u) RGBA=%u,%u,%u,%u expected=%u,%u,%u,%u\n",
                    index,x,y,static_cast<unsigned>(actual[0]),static_cast<unsigned>(actual[1]),
                    static_cast<unsigned>(actual[2]),static_cast<unsigned>(actual[3]),static_cast<unsigned>(expected[0]),
                    static_cast<unsigned>(expected[1]),static_cast<unsigned>(expected[2]),static_cast<unsigned>(expected[3]));
            require(std::equal(expected.begin(),expected.end(),actual),"visual sampler independent full-frame colors");
        }
    }
}

} // namespace progpu::native::tests
