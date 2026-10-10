#ifndef NOMINMAX
#define NOMINMAX
#endif

#include "progpu_native_mil.hpp"
#include "progpu_native_shader_bitmap_cache_fixture.hpp"
#include "../src/Scene/progpu_native_scene.hpp"

#include <array>
#include <cstddef>
#include <cstdio>
#include <vector>

#define EMPTY_IMAGE_REQUIRE(expression) do { \
    if (!(expression)) { \
        std::fprintf(stderr,"empty DrawingImage ownership: %s (line %d)\n",#expression,__LINE__); \
        return false; \
    } \
} while (false)

namespace {

using namespace progpu::native::tests;
using progpu::native::mil::channel;
using progpu::native::mil::command;
using progpu::native::mil::status;
using mil_clip_fixture_detail::packet;
namespace source=shader_cache_image_source;

bool typed_initialized_drawing_owners() {
    channel owned;
    std::vector<std::byte> batch;
    packet(batch,command::channel_create_resource,1U,59U);
    for (std::uint32_t type=87U; type<=91U; ++type)
        packet(batch,command::channel_create_resource,type,type);
    EMPTY_IMAGE_REQUIRE(owned.apply(batch) == status::success);
    EMPTY_IMAGE_REQUIRE(owned.set_drawing_image_empty_source(1U,91U) == status::invalid_handle);
    batch.clear(); packet(batch,command::drawing_image,1U,0U);
    EMPTY_IMAGE_REQUIRE(owned.apply(batch) == status::success);
    for (std::uint32_t type=87U; type<=91U; ++type)
        EMPTY_IMAGE_REQUIRE(owned.set_drawing_image_empty_source(1U,type) == status::invalid_handle);
    batch.clear();
    packet(batch,command::geometry_drawing,87U,0U,0U,0U);
    packet(batch,command::glyph_run_drawing,88U,0U,0U);
    packet(batch,command::image_drawing,89U,0.0,0.0,0.0,0.0,0U,0U);
    packet(batch,command::video_drawing,90U,0.0,0.0,0.0,0.0,0U,0U);
    append_shader_drawing_group(batch,91U,1.0,{});
    EMPTY_IMAGE_REQUIRE(owned.apply(batch) == status::success);
    // The setter owns the actual initialized drawing family. It does not
    // pre-admit video/glyph replay or require a fabricated positive rectangle.
    for (std::uint32_t type=87U; type<=91U; ++type) {
        const auto generation=owned.resource_generation(1U);
        EMPTY_IMAGE_REQUIRE(owned.set_drawing_image_empty_source(1U,type) == status::success);
        EMPTY_IMAGE_REQUIRE(owned.resource_generation(1U) != generation);
        batch.clear(); packet(batch,command::channel_delete_resource,type,type);
        EMPTY_IMAGE_REQUIRE(owned.apply(batch) == status::invalid_graph);
    }
    const auto generation=owned.resource_generation(1U);
    for (const auto handles : std::array{std::array{0U,91U},std::array{91U,91U},
            std::array{999U,91U},std::array{1U,0U},std::array{1U,1U},std::array{1U,999U}})
        EMPTY_IMAGE_REQUIRE(owned.set_drawing_image_empty_source(handles[0],handles[1]) == status::invalid_handle);
    EMPTY_IMAGE_REQUIRE(owned.set_drawing_image_bounds(1U,0,0,8,12) == status::invalid_argument);
    EMPTY_IMAGE_REQUIRE(owned.resource_generation(1U) == generation);
    batch.clear(); packet(batch,command::drawing_image,1U,91U);
    EMPTY_IMAGE_REQUIRE(owned.apply(batch) == status::success);
    EMPTY_IMAGE_REQUIRE(owned.set_drawing_image_empty_source(1U,91U) == status::invalid_argument);
    EMPTY_IMAGE_REQUIRE(owned.set_drawing_image_bounds(1U,0,0,8,12) == status::success);
    batch.clear(); packet(batch,command::drawing_image,1U,0U);
    packet(batch,command::channel_delete_resource,91U,91U);
    EMPTY_IMAGE_REQUIRE(owned.apply(batch) == status::success);
    EMPTY_IMAGE_REQUIRE(owned.set_drawing_image_empty_source(1U,91U) == status::invalid_handle);
    return true;
}

bool nested_capture_owns_empty_drawing_dependencies() {
    progpu_native_mil_channel* raw{};
    EMPTY_IMAGE_REQUIRE(progpu_native_mil_channel_create(&raw) == PROGPU_NATIVE_MIL_STATUS_SUCCESS);
    mil_clip_channel owner(raw);
    EMPTY_IMAGE_REQUIRE(initialize_shader_bitmap_cache(raw) && update_shader_bitmap_cache(raw,15U));
    std::vector<std::byte> batch,content,scene;
    const auto apply=[&] { return progpu_native_mil_channel_apply(raw,batch.data(),batch.size(),nullptr); };
    const auto rejected_capture=[&](progpu_native_mil_status expected) {
        const auto request=shader_bitmap_cache_request(0U);
        std::array<std::byte,256U> bytes; bytes.fill(std::byte{0xD9});
        const auto unchanged=bytes;
        std::size_t written=171U;
        progpu_native_mil_scene_build_result result{}; result.struct_size=sizeof(result);
        return progpu_native_mil_channel_build_scene_with_request(raw,&request,bytes.data(),bytes.size(),
            &written,nullptr,&result) == expected && written == 0U && bytes == unchanged;
    };
    EMPTY_IMAGE_REQUIRE(progpu_native_mil_channel_set_drawing_image_empty_source(nullptr,source::image,source::group) ==
        PROGPU_NATIVE_MIL_STATUS_INVALID_ARGUMENT);
    EMPTY_IMAGE_REQUIRE(build_shader_bitmap_cache(raw,0U,scene));
    const auto initial=scene;
    const auto image_generation=progpu_native_mil_channel_get_resource_generation(raw,source::image);
    const auto selected_generation=progpu_native_mil_channel_get_resource_generation(raw,5U);
    // The canonical image remains empty while the real drawing changes. Its
    // dependency revision, not a replacement image or selected brush, changes
    // the retained raw-cache sampler resource and complete compiled stream.
    append_shader_drawing_group(batch,source::group,.75,std::array{source::drawing});
    EMPTY_IMAGE_REQUIRE(apply() == PROGPU_NATIVE_MIL_STATUS_SUCCESS &&
        build_shader_bitmap_cache(raw,0U,scene) && scene != initial);
    EMPTY_IMAGE_REQUIRE(progpu_native_mil_channel_get_resource_generation(raw,source::image) == image_generation &&
        progpu_native_mil_channel_get_resource_generation(raw,5U) == selected_generation);
    const auto revised=scene;
    for (const auto dependency : std::array{std::array{source::group,91U},std::array{source::drawing,87U},
            std::array{source::cache_brush,83U},std::array{source::target,39U},std::array{source::cache,94U}}) {
        const auto group_generation=progpu_native_mil_channel_get_resource_generation(raw,source::group);
        batch.clear(); append_shader_drawing_group(batch,source::group,.25,std::array{source::drawing});
        packet(batch,command::channel_delete_resource,dependency[0],dependency[1]);
        EMPTY_IMAGE_REQUIRE(apply() == PROGPU_NATIVE_MIL_STATUS_INVALID_GRAPH);
        EMPTY_IMAGE_REQUIRE(progpu_native_mil_channel_get_resource_generation(raw,source::group) == group_generation &&
            build_shader_bitmap_cache(raw,0U,scene) && scene == revised);
    }
    // Candidate canonical clearing must not escape a failed later command.
    batch.clear(); packet(batch,command::drawing_image,source::image,0U);
    packet(batch,command::visual_insert_child_at,40U,999U,0U);
    EMPTY_IMAGE_REQUIRE(apply() == PROGPU_NATIVE_MIL_STATUS_INVALID_HANDLE);
    EMPTY_IMAGE_REQUIRE(progpu_native_mil_channel_get_resource_generation(raw,source::image) == image_generation &&
        build_shader_bitmap_cache(raw,0U,scene) && scene == revised);
    EMPTY_IMAGE_REQUIRE(progpu_native_mil_channel_set_drawing_image_bounds(raw,source::image,0,0,8,12) ==
        PROGPU_NATIVE_MIL_STATUS_INVALID_ARGUMENT);
    EMPTY_IMAGE_REQUIRE(build_shader_bitmap_cache(raw,0U,scene) && scene == revised);

    // A mixed image->drawing->cache->visual->image cycle is visible even
    // behind explicit empty witnesses, hidden descendants and zero scale.
    batch.clear(); packet(batch,command::visual_insert_child_at,source::target,source::leaf,0U);
    packet(content,command::draw_image,0.0,0.0,8.0,12.0,source::image,0U);
    append_visual_sampler_content(batch,source::leaf,source::leaf_content,content);
    packet(batch,command::bitmap_cache,51U,0.0,0U,0U,0U);
    EMPTY_IMAGE_REQUIRE(apply() == PROGPU_NATIVE_MIL_STATUS_SUCCESS);
    const progpu_native_mil_visual_visibility hidden{source::leaf,PROGPU_NATIVE_MIL_VISIBILITY_HIDDEN};
    EMPTY_IMAGE_REQUIRE(progpu_native_mil_channel_set_visual_visibilities(raw,&hidden,1U) == PROGPU_NATIVE_MIL_STATUS_SUCCESS);
    EMPTY_IMAGE_REQUIRE(rejected_capture(PROGPU_NATIVE_MIL_STATUS_INVALID_GRAPH));
    batch.clear(); packet(batch,command::channel_create_resource,200U,95U);
    content.clear(); packet(content,command::draw_image,0.0,0.0,8.0,12.0,200U,0U);
    append_visual_sampler_content(batch,source::leaf,source::leaf_content,content);
    EMPTY_IMAGE_REQUIRE(apply() == PROGPU_NATIVE_MIL_STATUS_SUCCESS);
    EMPTY_IMAGE_REQUIRE(rejected_capture(PROGPU_NATIVE_MIL_STATUS_INVALID_HANDLE));
    EMPTY_IMAGE_REQUIRE(progpu_native_mil_channel_set_bitmap_source_external_image(raw,200U,8U,12U) ==
        PROGPU_NATIVE_MIL_STATUS_SUCCESS && rejected_capture(PROGPU_NATIVE_MIL_STATUS_UNSUPPORTED_COMMAND));
    // Repair only the authored source content, then exercise retained source
    // refill and genuine canonical-null clearing without changing any owner.
    batch.clear(); content.clear(); packet(content,command::draw_rectangle,0.0,0.0,8.0,12.0,source::red,0U);
    append_visual_sampler_content(batch,source::leaf,source::leaf_content,content);
    append_shader_drawing_group(batch,source::group,1.0,std::array{source::drawing});
    EMPTY_IMAGE_REQUIRE(apply() == PROGPU_NATIVE_MIL_STATUS_SUCCESS);
    const progpu_native_mil_visual_visibility visible{source::leaf,PROGPU_NATIVE_MIL_VISIBILITY_VISIBLE};
    EMPTY_IMAGE_REQUIRE(progpu_native_mil_channel_set_visual_visibilities(raw,&visible,1U) == PROGPU_NATIVE_MIL_STATUS_SUCCESS);
    std::array<std::vector<std::byte>,5U> retained;
    for (std::uint32_t index=15U; index<20U; ++index) {
        EMPTY_IMAGE_REQUIRE(update_shader_bitmap_cache(raw,index) && build_shader_bitmap_cache(raw,0U,retained[index-15U]));
    }
    // Empty binding removes a previous explicit positive image rectangle.
    EMPTY_IMAGE_REQUIRE(update_shader_bitmap_cache(raw,18U));
    EMPTY_IMAGE_REQUIRE(progpu_native_mil_channel_set_drawing_image_bounds(raw,source::image,0,0,8,12) ==
        PROGPU_NATIVE_MIL_STATUS_INVALID_ARGUMENT);
    batch.clear(); packet(batch,command::drawing_image,source::image,0U);
    packet(batch,command::channel_delete_resource,source::group,91U);
    EMPTY_IMAGE_REQUIRE(apply() == PROGPU_NATIVE_MIL_STATUS_SUCCESS && build_shader_bitmap_cache(raw,0U,scene));
    EMPTY_IMAGE_REQUIRE(progpu_native_mil_channel_set_drawing_image_empty_source(raw,source::image,source::group) ==
        PROGPU_NATIVE_MIL_STATUS_INVALID_HANDLE);
    owner.reset();
    for (const auto& captured : retained)
        EMPTY_IMAGE_REQUIRE(progpu::native::scene::validate(captured.data(),captured.size()).status ==
            PROGPU_NATIVE_STATUS_SUCCESS);
    return true;
}

} // namespace

bool native_empty_drawing_image_ownership_controls() {
    return typed_initialized_drawing_owners() && nested_capture_owns_empty_drawing_dependencies();
}

#undef EMPTY_IMAGE_REQUIRE
