#include "progpu_native_webscene_state_mask_fixture.hpp"

#include <cmath>
#include <cstdint>
#include <cstdio>
#include <cstdlib>

namespace progpu::native::tests {
namespace {

[[noreturn]] void fail(const char* message) {
    std::fprintf(
        stderr,
        "ProGPU state-mask evidence failed: %s\n",
        message);
    std::abort();
}

void require(bool condition, const char* message) {
    if (!condition) {
        fail(message);
    }
}

} // namespace

void verify_semantic_state_mask_scene(
    IOSurfaceRef surface,
    const char* output_path) {
    require(surface != nullptr, "state-mask scene has no IOSurface");
    require(
        IOSurfaceLock(surface, kIOSurfaceLockReadOnly, nullptr) ==
            kIOReturnSuccess,
        "could not lock state-mask IOSurface");
    const auto* bytes = static_cast<const std::uint8_t*>(
        IOSurfaceGetBaseAddress(surface));
    const std::size_t width = IOSurfaceGetWidth(surface);
    const std::size_t height = IOSurfaceGetHeight(surface);
    const std::size_t row_bytes = IOSurfaceGetBytesPerRow(surface);
    require(
        bytes != nullptr && width == 64U && height == 48U &&
            row_bytes >= width * 4U,
        "unexpected state-mask IOSurface storage");
    const auto pixel = [bytes, row_bytes](std::size_t x, std::size_t y) {
        return bytes + y * row_bytes + x * 4U;
    };
    const auto near_bgra = [](const std::uint8_t* value,
                              int blue,
                              int green,
                              int red,
                              int tolerance) {
        return std::abs(static_cast<int>(value[0]) - blue) <= tolerance &&
            std::abs(static_cast<int>(value[1]) - green) <= tolerance &&
            std::abs(static_cast<int>(value[2]) - red) <= tolerance &&
            value[3] >= 240U;
    };
    require(
        near_bgra(pixel(15U, 24U), 136, 108, 1, 20),
        "per-draw mask lost the cyan source");
    require(
        near_bgra(pixel(30U, 24U), 138, 78, 133, 18),
        "mask was not applied independently before overlap blending");
    require(
        near_bgra(pixel(48U, 24U), 77, 28, 134, 20),
        "per-draw mask lost the magenta source");
    require(
        near_bgra(pixel(6U, 24U), 8, 4, 3, 12),
        "per-draw mask escaped its transformed bounds");

    if (output_path != nullptr && output_path[0] != '\0') {
        std::FILE* output = std::fopen(output_path, "wb");
        require(output != nullptr, "could not create state-mask capture");
        std::fprintf(output, "P6\n%zu %zu\n255\n", width, height);
        for (std::size_t y = 0U; y < height; ++y) {
            for (std::size_t x = 0U; x < width; ++x) {
                const std::uint8_t* source = pixel(x, y);
                const std::uint8_t rgb[]{
                    source[2], source[1], source[0]};
                require(
                    std::fwrite(rgb, sizeof(rgb), 1U, output) == 1U,
                    "state-mask capture write failed");
            }
        }
        require(
            std::fclose(output) == 0,
            "state-mask capture close failed");
    }
    require(
        IOSurfaceUnlock(surface, kIOSurfaceLockReadOnly, nullptr) ==
            kIOReturnSuccess,
        "could not unlock state-mask IOSurface");
}

} // namespace progpu::native::tests
