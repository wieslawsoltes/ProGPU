#pragma once

#include "progpu_native_shader_visual_brush_fixture.hpp"
#include "progpu_native_shader_drawing_image_fixture.hpp"
#include <thread>

namespace progpu::native::tests {

inline constexpr std::uint32_t shader_bitmap_cache_case_count = 20U;

// Retained identities for the nested empty-image ownership/refill controls.
// These are not replacement handles when either source becomes nonempty.
namespace shader_cache_image_source {
inline constexpr std::uint32_t target=100U, leaf=101U, leaf_content=102U,
    red=103U, cache=104U, cache_brush=105U, geometry=106U, rectangle=107U,
    drawing=108U, group=109U, image=110U, image_brush=111U,
    receiver=112U, receiver_content=113U;
}

inline void append_shader_cache_image_source(std::vector<std::byte>& batch) {
    using mil_clip_fixture_detail::packet;
    using mil::command;
    namespace source=shader_cache_image_source;
    for (const auto resource : std::array{
        std::array{source::target,39U},std::array{source::leaf,39U},std::array{source::leaf_content,43U},
        std::array{source::red,75U},std::array{source::cache,94U},std::array{source::cache_brush,83U},
        std::array{source::geometry,71U},std::array{source::rectangle,69U},std::array{source::drawing,87U},
        std::array{source::group,91U},std::array{source::image,59U},std::array{source::image_brush,80U},
        std::array{source::receiver,39U},std::array{source::receiver_content,43U}})
        packet(batch,command::channel_create_resource,resource[0],resource[1]);
    for (const auto handle : {source::target,source::leaf,source::receiver}) {
        packet(batch,command::visual_create,handle);
        packet(batch,command::visual_set_render_options,handle,3U,1U,0U,3U,0U,0U,0U);
    }
    packet(batch,command::solid_color_brush,source::red,1.0,progpu_native_color{1,0,0,1},0U,0U,0U,0U);
    packet(batch,command::bitmap_cache,source::cache,1.0,0U,0U,0U);
    packet(batch,command::bitmap_cache_brush,source::cache_brush,1.0,0U,0U,0U,source::cache,0U);
    packet(batch,command::rectangle_geometry,source::rectangle,0.0,0.0,0.0,0.0,8.0,12.0,0U,0U,0U,0U);
    packet(batch,command::geometry_group,source::geometry,0U,0U,0U);
    packet(batch,command::geometry_drawing,source::drawing,source::cache_brush,0U,source::geometry);
    append_shader_drawing_group(batch,source::group,1.0,std::array{source::drawing});
    packet(batch,command::drawing_image,source::image,0U);
    packet(batch,command::image_brush,source::image_brush,1.0,
        std::array{0.0,0.0,1.0,1.0},std::array{0.0,0.0,1.0,1.0},
        0.707,1.414,0U,0U,0U,1U,1U,0U,0U,1U,0U,1U,1U,0U,source::image);
    std::vector<std::byte> content;
    packet(content,command::draw_rectangle,0.0,0.0,8.0,12.0,source::red,0U);
    append_visual_sampler_content(batch,source::leaf,source::leaf_content,content);
    content.clear();
    packet(content,command::draw_rectangle,-4.0,4.0,8.0,12.0,source::image_brush,0U);
    append_visual_sampler_content(batch,source::receiver,source::receiver_content,content);
}

inline void append_shader_bitmap_cache_brush(std::vector<std::byte>& batch, std::uint32_t target,
    std::uint32_t cache, double opacity = 1.0, std::uint32_t transform = 0U,
    std::uint32_t relative = 0U) {
    mil_clip_fixture_detail::packet(batch, mil::command::bitmap_cache_brush, 5U, opacity,
        0U, transform, relative, cache, target);
}

// CPU controls supply explicit authored limits; provider controls replace this
// with their actual owned-device query before any scene is captured.
inline constexpr progpu_native_mil_bitmap_cache_raster_policy shader_cache_policy{
    sizeof(progpu_native_mil_bitmap_cache_raster_policy),1U,0U,0U,1.0F,1.0F,256U,256U,1U};

template<class Require>
progpu_native_cache_raster_limits read_owned_cache_raster_limits(progpu_native_engine* engine,Require require) {
    progpu_native_cache_raster_limits result{sizeof(result),1U,0U,0U};
    require(progpu_native_engine_get_cache_raster_limits(engine,&result) == PROGPU_NATIVE_STATUS_SUCCESS &&
        result.maximum_texture_width != 0U && result.maximum_texture_height != 0U,
        "actual owned cache device limits");
    for (std::uint32_t fault=0U; fault<3U; ++fault) {
        auto invalid=result;
        if (fault == 0U) --invalid.struct_size;
        if (fault == 1U) ++invalid.version;
        const auto unchanged=invalid;
        require(progpu_native_engine_get_cache_raster_limits(fault == 2U ? nullptr : engine,&invalid) ==
            PROGPU_NATIVE_STATUS_INVALID_ARGUMENT && std::memcmp(&invalid,&unchanged,sizeof(invalid)) == 0,
            "invalid device limit query leaves caller storage intact");
    }
    auto other_thread=result;
    progpu_native_status status=PROGPU_NATIVE_STATUS_SUCCESS;
    std::thread worker([&] { status=progpu_native_engine_get_cache_raster_limits(engine,&other_thread); });
    worker.join();
    require(status == PROGPU_NATIVE_STATUS_WRONG_THREAD && std::memcmp(&other_thread,&result,sizeof(result)) == 0,
        "cache limit query retains creating-thread ownership");
    auto again=result;
    require(progpu_native_engine_get_cache_raster_limits(engine,&again) == PROGPU_NATIVE_STATUS_SUCCESS &&
        std::memcmp(&again,&result,sizeof(result)) == 0,"actual device limit identity retained after failed query");
    return result;
}

inline bool initialize_shader_bitmap_cache(progpu_native_mil_channel* channel, bool with_policy = true) {
    using mil_clip_fixture_detail::packet;
    using mil::command;
    std::vector<std::byte> batch, content;
    for (const auto resource : std::array{
        std::array{1U,39U},std::array{2U,43U},std::array{4U,47U},std::array{5U,83U},
        std::array{6U,33U},std::array{7U,38U},std::array{8U,75U},std::array{40U,39U},
        std::array{41U,39U},std::array{42U,75U},std::array{43U,75U},
        std::array{44U,39U},std::array{45U,39U},std::array{46U,43U},std::array{47U,43U},
        std::array{48U,69U},std::array{49U,69U},std::array{50U,94U},std::array{51U,94U},
        std::array{52U,66U},std::array{53U,66U},std::array{54U,66U},std::array{55U,36U},std::array{56U,75U}})
        packet(batch,command::channel_create_resource,resource[0],resource[1]);
    for (const auto id : {1U,40U,41U,44U,45U}) packet(batch,command::visual_create,id);
    packet(batch,command::visual_insert_child_at,40U,41U,0U);
    packet(batch,command::visual_insert_child_at,41U,44U,0U);
    packet(batch,command::visual_insert_child_at,41U,45U,1U);
    packet(batch,command::generic_target_create,4U,std::uint64_t{0U},std::uint64_t{0U},64U,64U,0U);
    packet(batch,command::target_set_root,4U,1U);
    packet(batch,command::visual_set_render_options,1U,3U,1U,0U,3U,0U,0U,0U);
    packet(batch,command::visual_set_render_options,40U,3U,1U,0U,3U,0U,0U,0U);
    packet(batch,command::solid_color_brush,8U,1.0,progpu_native_color{1,1,1,1},0U,0U,0U,0U);
    packet(batch,command::solid_color_brush,56U,1.0,progpu_native_color{0,0,0,0},0U,0U,0U,0U);
    packet(batch,command::rectangle_geometry,48U,0.0,0.0,12.0,6.0,8.0,12.0,0U,0U,0U,0U);
    packet(batch,command::rectangle_geometry,49U,0.0,0.0,0.0,0.0,1.0,1.0,0U,0U,0U,0U);
    packet(batch,command::matrix_transform,52U,1.0,0.0,0.0,1.0,0.0,2.0,0U);
    packet(batch,command::matrix_transform,53U,1.0,0.0,0.0,1.0,.25,0.0,0U);
    packet(batch,command::matrix_transform,54U,1.0,0.0,0.0,1.0,29.0,31.0,0U);
    packet(batch,command::blur_effect,55U,3.0,0U,0U,1U);
    append_shader_bitmap_cache_brush(batch,40U,0U);
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
        progpu_native_mil_channel_set_visual_cache_bounds(channel,1U,8,10,32,24) == PROGPU_NATIVE_MIL_STATUS_SUCCESS &&
        (!with_policy || progpu_native_mil_channel_set_bitmap_cache_brush_raster_policy(channel,5U,
            &shader_cache_policy) == PROGPU_NATIVE_MIL_STATUS_SUCCESS);
}

inline bool update_shader_bitmap_cache(progpu_native_mil_channel* channel,std::uint32_t index) {
    if (index >= shader_bitmap_cache_case_count) return false;
    using mil_clip_fixture_detail::packet;
    using mil::command;
    std::vector<std::byte> batch,content;
    const double x=index >= 8U ? -4.0 : 4.0, y=index >= 8U ? 4.0 : 6.0;
    packet(batch,command::solid_color_brush,42U,1.0,
        index >= 8U ? progpu_native_color{0,0,1,1} : progpu_native_color{1,0,0,1},0U,0U,0U,0U);
    packet(batch,command::solid_color_brush,43U,1.0,progpu_native_color{0,1,0,1},0U,0U,0U,0U);
    packet(content,command::draw_rectangle,x,y,8.0,12.0,42U,0U);
    append_visual_sampler_content(batch,44U,46U,content);
    content.clear(); packet(content,command::draw_rectangle,x+8.0,y,8.0,12.0,43U,0U);
    append_visual_sampler_content(batch,45U,47U,content);
    packet(batch,command::bitmap_cache,50U,2.0,0U,0U,0U);
    packet(batch,command::bitmap_cache,51U,index == 6U ? 0.0 : 1.0,0U,1U,0U);
    packet(batch,command::visual_set_cache_mode,40U,index == 0U ? 0U : 50U);
    packet(batch,command::visual_set_clip,41U,index == 4U ? 48U : 0U);
    packet(batch,command::visual_set_alpha,41U,index == 4U ? .5 : 1.0);
    const bool ignored=index >= 3U;
    packet(batch,command::visual_set_offset,40U,ignored ? 101.0 : 0.0,ignored ? 103.0 : 0.0);
    packet(batch,command::visual_set_transform,40U,ignored ? 54U : 0U);
    packet(batch,command::visual_set_clip,40U,ignored ? 49U : 0U);
    packet(batch,command::visual_set_effect,40U,ignored ? 55U : 0U);
    packet(batch,command::visual_set_alpha,40U,ignored ? .25 : 1.0);
    packet(batch,command::visual_set_alpha_mask,40U,ignored ? 56U : 0U);
    packet(batch,command::visual_set_scrollable_area_clip,40U,1000.0,1000.0,1.0,1.0,
        index == 12U || index == 13U ? 1U : 0U);
    packet(batch,command::visual_set_scrollable_area_clip,41U,1000.0,1000.0,1.0,1.0,
        index == 13U ? 1U : 0U);
    if (index >= 9U || progpu_native_mil_channel_get_resource_generation(channel,
            shader_cache_image_source::image) != 0U) {
        packet(batch,command::visual_remove_all_children,41U);
        packet(batch,command::visual_remove_all_children,40U);
        if (index != 10U) packet(batch,command::visual_insert_child_at,40U,41U,0U);
        if (index < 9U || index >= 11U) {
            packet(batch,command::visual_insert_child_at,41U,44U,0U);
            packet(batch,command::visual_insert_child_at,41U,45U,1U);
        }
    }
    if (index >= 15U) {
        namespace source=shader_cache_image_source;
        if (progpu_native_mil_channel_get_resource_generation(channel,source::image) == 0U)
            append_shader_cache_image_source(batch);
        packet(batch,command::visual_insert_child_at,41U,source::receiver,2U);
        packet(batch,command::visual_remove_all_children,source::target);
        if (index >= 16U) packet(batch,command::visual_insert_child_at,source::target,source::leaf,0U);
        packet(batch,command::bitmap_cache_brush,source::cache_brush,1.0,0U,0U,0U,
            source::cache,index >= 16U ? source::target : 0U);
        const bool ink=index == 17U || index == 19U;
        if (ink) packet(batch,command::geometry_group,source::geometry,0U,0U,4U,source::rectangle);
        else packet(batch,command::geometry_group,source::geometry,0U,0U,0U);
        packet(batch,command::drawing_image,source::image,ink ? source::group : 0U);
    }
    append_shader_bitmap_cache_brush(batch,index == 7U ? 0U : 40U,index >= 2U ? 51U : 0U,
        index == 4U ? .5 : 1.0,index == 5U ? 52U : 0U,index == 5U ? 53U : 0U);
    if (progpu_native_mil_channel_apply(channel,batch.data(),batch.size(),nullptr) != PROGPU_NATIVE_MIL_STATUS_SUCCESS)
        return false;
    if (index >= 15U) {
        namespace source=shader_cache_image_source;
        if (index == 15U) {
            if (progpu_native_mil_channel_set_visual_source_empty_bounds(channel,source::target) !=
                    PROGPU_NATIVE_MIL_STATUS_SUCCESS ||
                progpu_native_mil_channel_set_bitmap_cache_brush_empty_source(channel,source::cache_brush,source::target) !=
                    PROGPU_NATIVE_MIL_STATUS_SUCCESS) return false;
        } else if (progpu_native_mil_channel_set_visual_cache_bounds(channel,source::target,0,0,8,12) !=
                PROGPU_NATIVE_MIL_STATUS_SUCCESS) return false;
        const auto bound=index == 17U || index == 19U
            ? progpu_native_mil_channel_set_drawing_image_bounds(channel,source::image,0,0,8,12)
            : progpu_native_mil_channel_set_drawing_image_empty_source(channel,source::image,source::group);
        if (bound != PROGPU_NATIVE_MIL_STATUS_SUCCESS) return false;
    }
    if (index == 9U || index == 10U)
        return progpu_native_mil_channel_set_visual_source_empty_bounds(channel,41U) == PROGPU_NATIVE_MIL_STATUS_SUCCESS &&
            progpu_native_mil_channel_set_visual_source_empty_bounds(channel,40U) == PROGPU_NATIVE_MIL_STATUS_SUCCESS;
    return progpu_native_mil_channel_set_visual_cache_bounds(channel,41U,x,y,16,12) == PROGPU_NATIVE_MIL_STATUS_SUCCESS &&
        progpu_native_mil_channel_set_visual_cache_bounds(channel,40U,index == 4U ? 12.0 : x,y,
            index == 4U ? 8.0 : 16.0,12) == PROGPU_NATIVE_MIL_STATUS_SUCCESS;
}

inline progpu_native_mil_scene_build_request shader_bitmap_cache_request(std::uint32_t index) {
    return {sizeof(progpu_native_mil_scene_build_request),0U,4U,0U,0xB1CA5U,index+1U,1.0,1.0,0U,index+1U};
}

inline bool build_shader_bitmap_cache(progpu_native_mil_channel* channel,std::uint32_t index,
    std::vector<std::byte>& scene) {
    const auto request=shader_bitmap_cache_request(index);
    progpu_native_mil_scene_build_result result{}; result.struct_size=sizeof(result);
    std::size_t written{};
    if (progpu_native_mil_channel_build_scene_with_request(channel,&request,nullptr,0U,&written,nullptr,&result) !=
        PROGPU_NATIVE_MIL_STATUS_SUCCESS) return false;
    scene.resize(written);
    return progpu_native_mil_channel_build_scene_with_request(channel,&request,scene.data(),scene.size(),&written,
        nullptr,&result) == PROGPU_NATIVE_MIL_STATUS_SUCCESS && written == scene.size();
}

template<class Render,class Require>
void verify_shader_bitmap_cache_pixels(Render render,Require require,
    const progpu_native_cache_raster_limits& limits) {
    std::array<std::vector<std::byte>,shader_bitmap_cache_case_count> scenes;
    {
        progpu_native_mil_channel* raw{};
        require(progpu_native_mil_channel_create(&raw) == PROGPU_NATIVE_MIL_STATUS_SUCCESS,"cache sampler channel");
        mil_clip_channel owner(raw);
        require(initialize_shader_bitmap_cache(raw),"cache sampler declaration");
        auto policy=shader_cache_policy;
        policy.maximum_texture_width=limits.maximum_texture_width;
        policy.maximum_texture_height=limits.maximum_texture_height;
        require(progpu_native_mil_channel_set_bitmap_cache_brush_raster_policy(raw,5U,&policy) ==
            PROGPU_NATIVE_MIL_STATUS_SUCCESS,"cache sampler actual owned device policy");
        for (std::uint32_t index=0U; index<scenes.size(); ++index) {
            require(update_shader_bitmap_cache(raw,index),"cache sampler exact source mutation");
            require(build_shader_bitmap_cache(raw,index,scenes[index]),"cache sampler immutable capture");
        }
    }
    std::vector<std::uint8_t> zero_scale_pixels, refill_pixels, nested_refill_pixels;
    for (std::uint32_t index=0U; index<scenes.size(); ++index) {
        progpu_native_scene_header header{};
        require(scenes[index].size() >= sizeof(header),"cache sampler header");
        std::memcpy(&header,scenes[index].data(),sizeof(header));
        verify_shader_sampler_visual_commands(scenes[index], header, require);
        std::array<std::vector<std::uint8_t>,3U> images;
        for (std::uint32_t replay=0U; replay<3U; ++replay) {
            progpu_native_layer_metrics layers{}; layers.struct_size=sizeof(layers);
            progpu_native_scene_frame_metrics frame{}; frame.struct_size=sizeof(frame);
            // Zero scale -> null and empty group -> empty root retain the
            // same owned transparent 1x1 picture. Its child submission is
            // reused even though the parent effect has a new source revision.
            const bool unchanged_empty_picture = index == 7U || index == 10U;
            const std::uint64_t submissions=replay == 1U || unchanged_empty_picture ? 1U : 2U;
            // The nested ordinary ImageBrush and cache are layers inside the
            // existing raw page. They use its child encoder (one vector mask),
            // not an additional picture/submit; the parent shader is unchanged.
            images[replay]=render(false,replay == 2U,scenes[index],header,submissions,layers,frame);
            require(frame.submission_count == submissions && frame.command_count == shader_sampler_visual_commands.size(),
                "cache sampler actual commands/submissions");
            require(layers.effect_kind == PROGPU_NATIVE_GROUP_EFFECT_WPF_SHADER && layers.effect_count == 1U &&
                layers.effect_pass_count == (replay == 1U ? 0U : 1U) && layers.effect_cache_hit == (replay == 1U ? 1U : 0U),
                "cache sampler retained effect execution");
        }
        require(images[0].size() == 64U*64U*4U && images[0] == images[1] && images[0] == images[2],
            "cache sampler complete cold/warm/independent frames");
        if (index == 6U) zero_scale_pixels=images[0];
        if (index == 7U || index == 9U || index == 10U || index == 13U)
            require(images[0] == zero_scale_pixels,"distinct null/empty/zero-scale full-frame equality");
        if (index == 8U) refill_pixels=images[0];
        if (index == 11U || index == 12U || index == 14U || index == 15U || index == 16U || index == 18U)
            require(images[0] == refill_pixels,"same-owner empty source reappears without replacement");
        if (index == 17U) nested_refill_pixels=images[0];
        if (index == 19U)
            require(images[0] == nested_refill_pixels,"same nested DrawingImage/cache owners refill without replacement");
        for (std::uint32_t y=0U; y<64U; ++y) for (std::uint32_t x=0U; x<64U; ++x) {
            std::array<std::uint8_t,4U> expected{0U,0U,0U,255U};
            if (x >= 8U && x < 40U && y >= 10U && y < 34U &&
                index != 6U && index != 7U && index != 9U && index != 10U && index != 13U) {
                if (index == 4U) expected[1]=128U; // descendant opacity only; brush opacity is not sampled
                else if (x >= 24U) expected[1]=255U;
                else expected[index >= 8U && index != 17U && index != 19U ? 2U : 0U]=255U;
            }
            const auto* actual=images[0].data()+(y*64U+x)*4U;
            if (!std::equal(expected.begin(),expected.end(),actual))
                std::fprintf(stderr,"BitmapCacheBrush sampler case=%u pixel=(%u,%u) RGBA=%u,%u,%u,%u expected=%u,%u,%u,%u\n",
                    index,x,y,static_cast<unsigned>(actual[0]),static_cast<unsigned>(actual[1]),
                    static_cast<unsigned>(actual[2]),static_cast<unsigned>(actual[3]),static_cast<unsigned>(expected[0]),
                    static_cast<unsigned>(expected[1]),static_cast<unsigned>(expected[2]),static_cast<unsigned>(expected[3]));
            require(std::equal(expected.begin(),expected.end(),actual),"cache sampler literal raw-texture pixels");
        }
    }
}

} // namespace progpu::native::tests
