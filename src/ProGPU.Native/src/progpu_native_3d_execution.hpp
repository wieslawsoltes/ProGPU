#pragma once

#include "progpu_native_frame_execution_common.hpp"

namespace progpu::native::execution {

bool create_semantic_3d_pipelines(progpu_native_engine& engine);

progpu_native_status compile_semantic_3d_page(
    progpu_native_engine& engine,
    const std::byte* bytes,
    const progpu_native_scene_header& header,
    const progpu_native_scene_frame& frame,
    std::uint32_t expected_draw_count,
    std::uint64_t& upload_bytes);

progpu_native_status encode_semantic_3d_bundle_draw(
    progpu_native_engine& engine,
    WGPURenderBundleEncoder encoder,
    const semantic_3d_draw& draw);

} // namespace progpu::native::execution
