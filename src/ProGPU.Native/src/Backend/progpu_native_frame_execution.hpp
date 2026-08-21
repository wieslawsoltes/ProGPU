#pragma once

#include "progpu_native.h"

#include <cstddef>

struct progpu_native_engine;

namespace progpu::native::execution {

progpu_native_status update_scene(
    progpu_native_engine* engine,
    const void* stream,
    std::size_t stream_size,
    progpu_native_scene_metrics* metrics);

progpu_native_status bind_scene_external_images(
    progpu_native_engine* engine,
    const progpu_native_scene_external_image_binding* bindings,
    std::size_t binding_count);

progpu_native_status render_solid(
    progpu_native_engine* engine,
    const progpu_native_frame* frame,
    progpu_native_frame_metrics* metrics);

progpu_native_status render_analytic(
    progpu_native_engine* engine,
    const progpu_native_analytic_frame* frame,
    progpu_native_analytic_frame_metrics* metrics);

progpu_native_status render_geometry(
    progpu_native_engine* engine,
    const progpu_native_geometry_frame* frame,
    progpu_native_geometry_frame_metrics* metrics);

progpu_native_status render_paths(
    progpu_native_engine* engine,
    const progpu_native_path_frame* frame,
    progpu_native_path_frame_metrics* metrics);

progpu_native_status render_glyphs(
    progpu_native_engine* engine,
    const progpu_native_glyph_frame* frame,
    progpu_native_glyph_frame_metrics* metrics);

progpu_native_status render_image(
    progpu_native_engine* engine,
    const progpu_native_image_frame* frame,
    progpu_native_image_frame_metrics* metrics);

progpu_native_status render_scene(
    progpu_native_engine* engine,
    const progpu_native_scene_frame* frame,
    progpu_native_scene_frame_metrics* metrics);

} // namespace progpu::native::execution
