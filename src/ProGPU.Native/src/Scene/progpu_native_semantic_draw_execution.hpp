#pragma once

#include "progpu_native_frame_execution_common.hpp"

namespace progpu::native::execution {

progpu_native_status encode_semantic_analytic_bundle_draw(
    progpu_native_engine& engine,
    WGPURenderBundleEncoder encoder,
    const semantic_analytic_draw& draw,
    std::uint32_t target_layer,
    WGPUBindGroup mask_bind_group,
    WGPUBindGroup mask_chain_bind_group);

progpu_native_status encode_semantic_path_bundle_draw(
    progpu_native_engine& engine,
    WGPURenderBundleEncoder encoder,
    const semantic_path_draw& draw,
    std::uint32_t target_layer,
    WGPUBindGroup mask_bind_group,
    WGPUBindGroup mask_chain_bind_group);

progpu_native_status encode_semantic_glyph_bundle_draw(
    progpu_native_engine& engine,
    WGPURenderBundleEncoder encoder,
    const semantic_glyph_draw& draw,
    std::uint32_t target_layer,
    WGPUBindGroup mask_bind_group,
    WGPUBindGroup mask_chain_bind_group);

progpu_native_status encode_semantic_image_bundle_draw(
    progpu_native_engine& engine,
    WGPURenderBundleEncoder encoder,
    const semantic_image_draw& draw,
    std::uint32_t target_layer,
    WGPUBindGroup mask_bind_group,
    WGPUBindGroup mask_chain_bind_group);

// The same draws, written into the command list of a span instead of into a
// bundle encoder.
progpu_native_status record_semantic_analytic_draw(
    progpu_native_engine& engine,
    semantic_span_command_store& store,
    const semantic_analytic_draw& draw,
    std::uint32_t target_layer,
    WGPUBindGroup mask_bind_group,
    WGPUBindGroup mask_chain_bind_group);

progpu_native_status record_semantic_path_draw(
    progpu_native_engine& engine,
    semantic_span_command_store& store,
    const semantic_path_draw& draw,
    std::uint32_t target_layer,
    WGPUBindGroup mask_bind_group,
    WGPUBindGroup mask_chain_bind_group);

progpu_native_status record_semantic_glyph_draw(
    progpu_native_engine& engine,
    semantic_span_command_store& store,
    const semantic_glyph_draw& draw,
    std::uint32_t target_layer,
    WGPUBindGroup mask_bind_group,
    WGPUBindGroup mask_chain_bind_group);

progpu_native_status record_semantic_image_draw(
    progpu_native_engine& engine,
    semantic_span_command_store& store,
    const semantic_image_draw& draw,
    std::uint32_t target_layer,
    WGPUBindGroup mask_bind_group,
    WGPUBindGroup mask_chain_bind_group);

// Replays a command list into a render pass. state is what the pass was last
// given by such a replay; reset it when anything else drew into the pass.
bool replay_semantic_span_commands(
    WGPURenderPassEncoder pass,
    const std::uint64_t* words,
    std::size_t count,
    progpu::native::span_commands::replay_state& state);

// Makes a bundle of a command list for the target format of the engine, or
// null when it could not be made.
WGPURenderBundle create_semantic_span_bundle(
    progpu_native_engine& engine,
    const std::uint64_t* words,
    std::size_t count);

} // namespace progpu::native::execution
