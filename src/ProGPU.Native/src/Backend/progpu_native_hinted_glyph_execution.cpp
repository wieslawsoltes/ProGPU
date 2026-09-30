#include "progpu_native_hinted_glyph_execution.hpp"

#include <new>

namespace progpu::native::execution {
hinted_glyph_render_result render_hinted_glyph_frame(progpu_native_engine* engine,
    std::shared_ptr<const text::hinted_glyph_frame> frame) noexcept {
    hinted_glyph_render_result result{};
    result.metrics.struct_size = sizeof(result.metrics);
    if (engine == nullptr || frame == nullptr) return result;
    const auto borrowed = frame->frame();
    progpu_native_glyph_frame_metrics measured{};
    measured.struct_size = sizeof(measured);
    try {
        // The only renderer crossing. Ownership and completion remain entirely
        // with the actual selected engine/provider's existing glyph executor.
        result.status = progpu_native_engine_render_glyphs(engine, &borrowed, &measured);
        if (result.status == PROGPU_NATIVE_STATUS_SUCCESS) result.metrics = measured;
    } catch (const std::bad_alloc&) {
        result.status = PROGPU_NATIVE_STATUS_OUT_OF_MEMORY;
    } catch (...) {
        result.status = PROGPU_NATIVE_STATUS_INTERNAL_ERROR;
    }
    return result;
}
} // namespace progpu::native::execution
