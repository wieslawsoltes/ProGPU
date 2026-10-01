#include "progpu_native_glyph_coverage_frame.hpp"

#include <array>
#include <cmath>
#include <cstdint>
#include <cstdio>
#include <limits>

namespace {

using progpu::native::gpu_uniforms;

gpu_uniforms root_uniforms(float dpi = 2.0F) {
    gpu_uniforms uniforms{};
    const float logical_size = 96.0F / dpi;
    uniforms.dpi_scale = dpi;
    uniforms.canvas_size[0] = logical_size;
    uniforms.canvas_size[1] = logical_size;
    uniforms.projection[0] = 2.0F / logical_size;
    uniforms.projection[5] = -2.0F / logical_size;
    uniforms.projection[10] = -1.0F;
    uniforms.projection[12] = -1.0F;
    uniforms.projection[13] = 1.0F;
    uniforms.projection[15] = 1.0F;
    for (std::uint32_t i = 0U; i < 16U; ++i) {
        const float identity = i % 5U == 0U ? 1.0F : 0.0F;
        uniforms.model_view_projection[i] = identity;
        uniforms.view[i] = identity;
    }
    return uniforms;
}

bool certify(gpu_uniforms& uniforms, float x = 0.0F, float y = 0.0F,
    float width = 96.0F, float height = 96.0F) {
    return progpu::native::certify_root_glyph_coverage_frame(
        uniforms, 96U, 96U, x, y, width, height);
}

bool rejected(gpu_uniforms uniforms) {
    // A copied/stale certificate must be actively cleared on every rejection.
    uniforms.pad0 = -1.0F;
    return !certify(uniforms) && uniforms.pad0 == 0.0F;
}

bool check(bool value, const char* message) {
    if (!value) {
        std::fprintf(stderr, "%s\n", message);
    }
    return value;
}

} // namespace

int main() {
    bool success = true;
    for (const float dpi : {1.0F, 1.25F, 1.5F, 2.0F}) {
        auto uniforms = root_uniforms(dpi);
        success &= check(uniforms.pad0 == 0.0F && certify(uniforms) &&
            uniforms.pad0 == -1.0F, "Canonical root pass did not certify.");
    }
    auto uniforms = root_uniforms();
    success &= check(!certify(uniforms, 0.5F) && uniforms.pad0 == 0.0F,
        "Translated viewport certified.");
    success &= check(!certify(uniforms, 0.0F, 0.0F, 95.0F),
        "Partial viewport certified.");
    success &= check(!certify(uniforms, 0.0F, 0.0F, 96.0F,
        std::numeric_limits<float>::quiet_NaN()), "NaN viewport certified.");
    for (const float dpi : {0.0F, -1.0F,
        std::numeric_limits<float>::infinity(),
        std::numeric_limits<float>::quiet_NaN()}) {
        uniforms = root_uniforms();
        uniforms.dpi_scale = dpi;
        success &= check(rejected(uniforms), "Invalid DPI certified.");
    }
    for (std::uint32_t i = 0U; i < 16U; ++i) {
        uniforms = root_uniforms();
        uniforms.projection[i] = std::nextafter(uniforms.projection[i], 2.0F);
        success &= check(rejected(uniforms), "Changed projection certified.");
        uniforms = root_uniforms();
        uniforms.model_view_projection[i] = std::nextafter(
            uniforms.model_view_projection[i], 2.0F);
        success &= check(rejected(uniforms), "Late MVP certified.");
        uniforms = root_uniforms();
        uniforms.view[i] = std::nextafter(uniforms.view[i], 2.0F);
        success &= check(rejected(uniforms), "Nonidentity view certified.");
    }
    for (std::uint32_t i = 0U; i < 2U; ++i) {
        uniforms = root_uniforms();
        uniforms.canvas_size[i] += 1.0F;
        success &= check(rejected(uniforms), "Changed canvas certified.");
        uniforms = root_uniforms();
        uniforms.render_origin[i] = 0.25F;
        success &= check(rejected(uniforms), "Translated render origin certified.");
    }
    uniforms = root_uniforms();
    uniforms.pad0 = -1.0F;
    success &= check(!progpu::native::certify_root_glyph_coverage_frame(
        uniforms, 0U, 96U, 0.0F, 0.0F, 0.0F, 96.0F) &&
        uniforms.pad0 == 0.0F, "Zero target certified.");
    for (const std::uint32_t width : {16'777'217U,
        std::numeric_limits<std::uint32_t>::max()}) {
        uniforms = root_uniforms();
        uniforms.pad0 = -1.0F;
        success &= check(!progpu::native::certify_root_glyph_coverage_frame(
            uniforms, width, 96U, 0.0F, 0.0F,
            static_cast<float>(width), 96.0F) && uniforms.pad0 == 0.0F,
            "Rounded uint target extent certified.");
    }
    uniforms = root_uniforms();
    success &= check(certify(uniforms) && uniforms.pad0 <= 0.5F &&
        uniforms.pad0 <= 1.5F, "Glyph tag activates bounded Texture/ROP policy.");
    return success ? 0 : 1;
}
