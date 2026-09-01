#pragma once

#include "progpu_native.h"
#include "progpu_native_direct2d_compat.hpp"

#include <cstddef>
#include <cstdint>
#include <limits>
#include <span>

namespace progpu::native::direct2d::compat {

enum class scene_submission_stage : std::uint32_t {
    none = 0U,
    query_target,
    build_scene,
    update_engine,
    render_engine
};

struct scene_submission_diagnostics final {
    scene_submission_stage stage = scene_submission_stage::none;
    com::result recording_result = com::ok;
    progpu_native_status engine_status = PROGPU_NATIVE_STATUS_SUCCESS;
    std::uint64_t required_bytes = 0U;
    std::uint64_t written_bytes = 0U;
};

struct scene_render_options final {
    std::uintptr_t target_view = 0U;
    std::uint32_t flags = PROGPU_NATIVE_SCENE_FRAME_NONE;
};

namespace detail {

inline void initialize_diagnostics(
    scene_submission_diagnostics* diagnostics) noexcept
{
    if (diagnostics != nullptr) {
        *diagnostics = {};
    }
}

inline progpu_native_status fail_recording(
    scene_submission_diagnostics* diagnostics,
    scene_submission_stage stage,
    com::result result) noexcept
{
    if (diagnostics != nullptr) {
        diagnostics->stage = stage;
        diagnostics->recording_result = result;
        diagnostics->engine_status = PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
    }
    return result == com::out_of_memory
        ? PROGPU_NATIVE_STATUS_OUT_OF_MEMORY
        : PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
}

inline progpu_native_status build_and_update_scene(
    scene_render_target_native* target,
    progpu_native_engine* engine,
    std::span<std::byte> scratch,
    progpu_native_scene_metrics* metrics,
    scene_submission_diagnostics* diagnostics) noexcept
{
    if (target == nullptr || engine == nullptr) {
        return fail_recording(
            diagnostics,
            scene_submission_stage::query_target,
            com::invalid_argument);
    }
    const std::uint64_t required = target->GetRequiredSceneSize();
    if (diagnostics != nullptr) {
        diagnostics->required_bytes = required;
    }
    if (required == 0U || required > scratch.size() ||
        required > std::numeric_limits<std::size_t>::max()) {
        return fail_recording(
            diagnostics,
            scene_submission_stage::build_scene,
            com::invalid_argument);
    }
    std::uint64_t written = 0U;
    const com::result build_result = target->BuildScene(
        scratch.data(),
        static_cast<std::uint64_t>(scratch.size()),
        &written);
    if (diagnostics != nullptr) {
        diagnostics->written_bytes = written;
        diagnostics->recording_result = build_result;
    }
    if (com::failed(build_result) || written != required) {
        return fail_recording(
            diagnostics,
            scene_submission_stage::build_scene,
            com::failed(build_result) ? build_result : failure);
    }
    const progpu_native_status status = progpu_native_engine_update_scene(
        engine,
        scratch.data(),
        static_cast<std::size_t>(written),
        metrics);
    if (diagnostics != nullptr) {
        diagnostics->engine_status = status;
        diagnostics->stage = status == PROGPU_NATIVE_STATUS_SUCCESS
            ? scene_submission_stage::none
            : scene_submission_stage::update_engine;
    }
    return status;
}

} // namespace detail

/* Serializes into caller-owned scratch and updates the engine's retained
 * snapshot. The engine performs its normal transactional copy; no pixel data,
 * GPU resource, or Windows handle crosses this boundary. */
inline progpu_native_status update_scene_target(
    scene_render_target_native* target,
    progpu_native_engine* engine,
    std::span<std::byte> scratch,
    progpu_native_scene_metrics* metrics = nullptr,
    scene_submission_diagnostics* diagnostics = nullptr) noexcept
{
    detail::initialize_diagnostics(diagnostics);
    return detail::build_and_update_scene(
        target, engine, scratch, metrics, diagnostics);
}

/* Updates and renders one target in a single host call. A session without an
 * explicit Clear preserves the existing attachment, matching Direct2D target
 * behavior. Direct2D's two-axis DPI is accepted only when the semantic
 * renderer's scalar dpi_scale can preserve it exactly. */
inline progpu_native_status render_scene_target(
    scene_render_target_native* target,
    progpu_native_engine* engine,
    const scene_render_options& options,
    std::span<std::byte> scratch,
    progpu_native_scene_metrics* scene_metrics = nullptr,
    progpu_native_scene_frame_metrics* frame_metrics = nullptr,
    scene_submission_diagnostics* diagnostics = nullptr) noexcept
{
    detail::initialize_diagnostics(diagnostics);
    constexpr std::uint32_t supported_flags =
        PROGPU_NATIVE_SCENE_FRAME_PRESERVE_TARGET;
    if (target == nullptr || engine == nullptr || options.target_view == 0U ||
        (options.flags & ~supported_flags) != 0U) {
        return detail::fail_recording(
            diagnostics,
            scene_submission_stage::query_target,
            com::invalid_argument);
    }

    render_target* raw_render_target = nullptr;
    const com::result query_result = target->QueryInterface(
        render_target_interface_id,
        reinterpret_cast<void**>(&raw_render_target));
    com::pointer<render_target> render_target_value;
    render_target_value.attach(raw_render_target);
    if (com::failed(query_result) || !render_target_value) {
        return detail::fail_recording(
            diagnostics,
            scene_submission_stage::query_target,
            query_result);
    }
    float dpi_x = 0.0F;
    float dpi_y = 0.0F;
    render_target_value->GetDpi(&dpi_x, &dpi_y);
    const size_u pixel_size = render_target_value->GetPixelSize();
    if (!(dpi_x > 0.0F) || dpi_x != dpi_y || pixel_size.width == 0U ||
        pixel_size.height == 0U) {
        return detail::fail_recording(
            diagnostics,
            scene_submission_stage::query_target,
            com::invalid_argument);
    }

    const progpu_native_status update_status =
        detail::build_and_update_scene(
            target, engine, scratch, scene_metrics, diagnostics);
    if (update_status != PROGPU_NATIVE_STATUS_SUCCESS) {
        return update_status;
    }

    scene_render_target_summary summary{};
    target->GetSummary(&summary);
    progpu_native_scene_frame frame{};
    frame.struct_size = sizeof(frame);
    frame.width = pixel_size.width;
    frame.height = pixel_size.height;
    frame.dpi_scale = dpi_x / 96.0F;
    frame.target_view = options.target_view;
    frame.clear_color = {
        summary.clear_color.red,
        summary.clear_color.green,
        summary.clear_color.blue,
        summary.clear_color.alpha};
    frame.scene_id = summary.scene_id;
    frame.generation = summary.generation;
    frame.flags = options.flags;
    if (summary.has_clear == 0) {
        frame.flags |= PROGPU_NATIVE_SCENE_FRAME_PRESERVE_TARGET;
    } else {
        frame.flags &= ~PROGPU_NATIVE_SCENE_FRAME_PRESERVE_TARGET;
    }
    const progpu_native_status render_status = progpu_native_engine_render_scene(
        engine, &frame, frame_metrics);
    if (diagnostics != nullptr) {
        diagnostics->engine_status = render_status;
        diagnostics->stage = render_status == PROGPU_NATIVE_STATUS_SUCCESS
            ? scene_submission_stage::none
            : scene_submission_stage::render_engine;
    }
    return render_status;
}

} // namespace progpu::native::direct2d::compat
