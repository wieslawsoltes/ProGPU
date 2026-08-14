#pragma once

#include "progpu_native_frame_execution_common.hpp"

namespace progpu::native::execution {

progpu_native_status encode_semantic_analytic_bundle_draw(
    progpu_native_engine& engine,
    WGPURenderBundleEncoder encoder,
    const semantic_analytic_draw& draw,
    std::uint32_t target_layer,
    WGPUBindGroup mask_bind_group);

progpu_native_status encode_semantic_path_bundle_draw(
    progpu_native_engine& engine,
    WGPURenderBundleEncoder encoder,
    const semantic_path_draw& draw,
    std::uint32_t target_layer,
    WGPUBindGroup mask_bind_group);

progpu_native_status encode_semantic_glyph_bundle_draw(
    progpu_native_engine& engine,
    WGPURenderBundleEncoder encoder,
    const semantic_glyph_draw& draw,
    std::uint32_t target_layer);

progpu_native_status encode_semantic_image_bundle_draw(
    progpu_native_engine& engine,
    WGPURenderBundleEncoder encoder,
    const semantic_image_draw& draw,
    std::uint32_t target_layer);

} // namespace progpu::native::execution
