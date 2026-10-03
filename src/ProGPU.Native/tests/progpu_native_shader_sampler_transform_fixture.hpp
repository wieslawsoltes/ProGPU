#pragma once

#include "progpu_native_shader_sampler_animation_fixture.hpp"

namespace progpu::native::tests {

struct sampler_transform_case {
    std::uint32_t left, right, split;
    bool mirrored;
};

// Independent physical capture columns, never computed from emitted matrices.
inline constexpr std::array<sampler_transform_case,12U> sampler_transform_cases{{
    sampler_transform_case{0U,32U,16U,false}, {8U,32U,24U,false}, {0U,32U,16U,true},
    {8U,32U,24U,false}, {0U,24U,8U,false}, {8U,24U,16U,false}, {0U,32U,16U,true},
    {4U,20U,12U,false}, {2U,18U,10U,false}, {4U,20U,12U,false}, {4U,20U,12U,false},
    {0U,32U,16U,false}}};

inline void append_sampler_transform_brush(std::vector<std::byte>& batch, bool relative,
    std::uint32_t transform, bool empty = false) {
    mil_clip_fixture_detail::packet(batch, mil::command::image_brush, 5U, .5,
        std::array{0.0,0.0,empty ? 0.0 : 1.0,1.0}, std::array{0.0,0.0,1.0,1.0},
        .707,1.414,0U,relative ? 0U : transform,relative ? transform : 0U,
        1U,1U,0U,0U,1U,0U,1U,1U,0U,3U);
}

inline void append_sampler_transform_group(std::vector<std::byte>& batch,
    std::initializer_list<std::uint32_t> children) {
    using mil_clip_fixture_detail::append;
    append(batch, static_cast<std::uint32_t>(16U + children.size() * sizeof(std::uint32_t)));
    append(batch, static_cast<std::uint32_t>(mil::command::transform_group));
    append(batch, 63U);
    append(batch, static_cast<std::uint32_t>(children.size() * sizeof(std::uint32_t)));
    for (const auto child : children) append(batch, child);
}

inline bool initialize_sampler_transform_animation(progpu_native_mil_channel* channel) {
    if (!initialize_shader_sampler_animation(channel)) return false;
    using mil_clip_fixture_detail::packet;
    using mil::command;
    std::vector<std::byte> batch;
    packet(batch, command::channel_create_resource,50U,54U);
    for (std::uint32_t id = 51U; id <= 56U; ++id) {
        packet(batch, command::channel_create_resource,id,49U);
        packet(batch, command::double_resource,id,id == 53U || id == 54U ? 1.0 : 0.0);
    }
    packet(batch, command::channel_create_resource,60U,66U);
    packet(batch, command::channel_create_resource,61U,62U);
    packet(batch, command::channel_create_resource,62U,63U);
    packet(batch, command::channel_create_resource,63U,61U);
    packet(batch, command::matrix_resource,50U,1.0,0.0,0.0,1.0,0.0,0.0);
    packet(batch, command::matrix_transform,60U,1.0,0.0,0.0,1.0,0.0,0.0,50U);
    packet(batch, command::translate_transform,61U,0.0,0.0,51U,52U);
    packet(batch, command::scale_transform,62U,1.0,1.0,0.0,0.0,53U,54U,55U,56U);
    append_sampler_transform_group(batch,{61U,62U});
    return progpu_native_mil_channel_apply(channel,batch.data(),batch.size(),nullptr) == PROGPU_NATIVE_MIL_STATUS_SUCCESS;
}

inline bool update_sampler_transform_animation(progpu_native_mil_channel* channel, bool relative,
    std::uint32_t index) {
    if (index >= sampler_transform_cases.size()) return false;
    using mil_clip_fixture_detail::packet;
    using mil::command;
    std::vector<std::byte> batch;
    const double unit = relative ? 1.0 / 32.0 : 1.0;
    if (index <= 2U) {
        packet(batch,command::matrix_resource,50U,index == 2U ? -1.0 : 1.0,0.0,0.0,1.0,
            (index == 2U ? 32.0 : index == 1U ? 8.0 : 0.0) * unit,0.0);
        if (index == 0U) append_sampler_transform_brush(batch,relative,60U);
    } else if (index <= 4U) {
        packet(batch,command::double_resource,51U,(index == 3U ? 8.0 : -8.0) * unit);
        if (index == 3U) append_sampler_transform_brush(batch,relative,61U);
    } else if (index <= 6U) {
        packet(batch,command::double_resource,53U,index == 5U ? .5 : -1.0);
        packet(batch,command::double_resource,55U,16.0 * unit);
        if (index == 5U) append_sampler_transform_brush(batch,relative,62U);
    } else if (index == 7U) {
        packet(batch,command::double_resource,51U,8.0 * unit);
        packet(batch,command::double_resource,53U,.5);
        packet(batch,command::double_resource,55U,0.0);
        append_sampler_transform_brush(batch,relative,63U);
    } else if (index == 8U) {
        // Only a current-value resource changes; all transform, brush, bitmap
        // and shader owners keep the exact original handle and generation.
        packet(batch,command::double_resource,51U,4.0 * unit);
    } else if (index == 9U) {
        append_sampler_transform_group(batch,{62U,61U});
    } else if (index == 10U) {
        append_sampler_transform_group(batch,{61U,61U,62U});
    } else {
        packet(batch,command::matrix_transform,60U,1.0,0.0,0.0,1.0,0.0,0.0,0U);
        packet(batch,command::channel_delete_resource,50U,54U);
        append_sampler_transform_brush(batch,relative,60U);
    }
    return progpu_native_mil_channel_apply(channel,batch.data(),batch.size(),nullptr) == PROGPU_NATIVE_MIL_STATUS_SUCCESS;
}

inline bool build_sampler_transform_animation(progpu_native_mil_channel* channel, bool relative,
    std::uint32_t index, std::vector<std::byte>& scene) {
    const progpu_native_mil_scene_build_request request{sizeof(request),0U,4U,0U,
        relative ? 0x94B0U : 0x94AFU,index + 1U,1.0,1.0,0U,index + 1U};
    progpu_native_mil_scene_build_result result{}; result.struct_size = sizeof(result);
    std::size_t written{};
    if (progpu_native_mil_channel_build_scene_with_request(channel,&request,nullptr,0U,&written,nullptr,&result) !=
        PROGPU_NATIVE_MIL_STATUS_SUCCESS) return false;
    scene.resize(written);
    return progpu_native_mil_channel_build_scene_with_request(channel,&request,scene.data(),scene.size(),
        &written,nullptr,&result) == PROGPU_NATIVE_MIL_STATUS_SUCCESS && written == scene.size();
}

template<class Render, class Require>
void verify_sampler_transform_animation_pixels(Render render, Require require) {
    std::array<std::vector<std::uint8_t>,sampler_transform_cases.size()> absolute;
    for (const bool relative : {false,true}) {
        std::array<std::vector<std::byte>,sampler_transform_cases.size()> scenes;
        {
            progpu_native_mil_channel* raw{};
            require(progpu_native_mil_channel_create(&raw) == PROGPU_NATIVE_MIL_STATUS_SUCCESS,
                "sampler transform animation channel creation failed");
            mil_clip_channel owner(raw);
            require(initialize_sampler_transform_animation(raw),"sampler transform animation initialization failed");
            for (std::uint32_t index = 0U; index < scenes.size(); ++index) {
                require(update_sampler_transform_animation(raw,relative,index),"sampler transform animation update failed");
                require(build_sampler_transform_animation(raw,relative,index,scenes[index]),"sampler transform animation capture failed");
            }
        }
        // All captures outlive removal of their matrix animation and channel.
        for (std::uint32_t index = 0U; index < scenes.size(); ++index) {
            progpu_native_scene_header header{};
            require(scenes[index].size() >= sizeof(header),"sampler transform animation header missing");
            std::memcpy(&header,scenes[index].data(),sizeof(header));
            require(header.command_count == 3U,"sampler transform animation changed outer commands");
            std::array<std::vector<std::uint8_t>,3U> pixels;
            for (std::uint32_t replay = 0U; replay < pixels.size(); ++replay) {
                progpu_native_layer_metrics layers{}; layers.struct_size = sizeof(layers);
                progpu_native_scene_frame_metrics frame{}; frame.struct_size = sizeof(frame);
                const auto submissions = replay == 1U ? 1U : 2U;
                pixels[replay] = render(relative,replay == 2U,scenes[index],header,submissions,layers,frame);
                require(frame.submission_count == submissions && frame.command_count == 3U,
                    "sampler transform animation submission/command ownership changed");
                require(layers.effect_kind == PROGPU_NATIVE_GROUP_EFFECT_WPF_SHADER && layers.effect_count == 1U &&
                    layers.effect_pass_count == (replay == 1U ? 0U : 1U) && layers.effect_cache_hit == (replay == 1U ? 1U : 0U),
                    "sampler transform animation effect retention changed");
            }
            require(pixels[0].size() == 64U * 64U * 4U && pixels[0] == pixels[1] && pixels[0] == pixels[2],
                "sampler transform animation cold/warm/independent pixels differ");
            if (relative) require(pixels[0] == absolute[index],"relative/absolute transform animation pixels differ");
            else absolute[index] = pixels[0];
            const auto& expected_columns = sampler_transform_cases[index];
            for (std::uint32_t y = 0U; y < 64U; ++y) for (std::uint32_t x = 0U; x < 64U; ++x) {
                std::array<std::uint8_t,4U> expected{0U,0U,0U,255U};
                if (y >= 10U && y < 34U && x >= 8U + expected_columns.left && x < 8U + expected_columns.right) {
                    const bool green = (x >= 8U + expected_columns.split) != expected_columns.mirrored;
                    expected[green ? 1U : 0U] = 128U;
                }
                const auto* actual = pixels[0].data() + (y * 64U + x) * 4U;
                if (!std::equal(expected.begin(),expected.end(),actual))
                    std::fprintf(stderr,"Sampler transform relative=%u case=%u pixel=%u,%u RGBA=%u,%u,%u,%u expected=%u,%u,%u,%u\n",
                        static_cast<unsigned>(relative),index,x,y,static_cast<unsigned>(actual[0]),
                        static_cast<unsigned>(actual[1]),static_cast<unsigned>(actual[2]),static_cast<unsigned>(actual[3]),
                        static_cast<unsigned>(expected[0]),static_cast<unsigned>(expected[1]),
                        static_cast<unsigned>(expected[2]),static_cast<unsigned>(expected[3]));
                require(std::equal(expected.begin(),expected.end(),actual),"sampler transform animation independent pixel differs");
            }
        }
    }
}

} // namespace progpu::native::tests
