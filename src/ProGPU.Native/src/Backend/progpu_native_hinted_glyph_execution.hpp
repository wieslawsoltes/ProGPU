#pragma once

#include "../Text/Font/progpu_native_hinted_glyph_frame.hpp"

namespace progpu::native::execution {
struct hinted_glyph_render_result final {
    progpu_native_status status = PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
    progpu_native_glyph_frame_metrics metrics{};
};

// Private explicit consumer shared by both real providers. Retains the owned
// CPU frame/layout/run through exactly one existing render_glyphs call. No
// caller metrics pointer; failed calls expose no successful metrics payload.
// The original executor owns uploads and in-flight resources. Submission count
// reports the original submission boundary, never GPU completion. No extra
// poll, wait, readback or submit; no source factory/style/Display admission.
hinted_glyph_render_result render_hinted_glyph_frame(progpu_native_engine* engine,
    std::shared_ptr<const text::hinted_glyph_frame> frame) noexcept;
} // namespace progpu::native::execution
