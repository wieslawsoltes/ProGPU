#include "progpu_native_webscene_state_mask_media_fixture.hpp"

#include <cmath>
#include <cstdint>
#include <cstdio>
#include <cstdlib>

namespace progpu::native::tests {
namespace {

[[noreturn]] void fail(const char* message) {
    std::fprintf(
        stderr,
        "ProGPU state-mask media evidence failed: %s\n",
        message);
    std::abort();
}

void require(bool condition, const char* message) {
    if (!condition) {
        fail(message);
    }
}

} // namespace

void verify_semantic_state_mask_media_scene(
    IOSurfaceRef surface,
    const char* output_path) {
    require(surface != nullptr, "state-mask media scene has no IOSurface");
    require(
        IOSurfaceLock(surface, kIOSurfaceLockReadOnly, nullptr) ==
            kIOReturnSuccess,
        "could not lock state-mask media IOSurface");
    const auto* bytes = static_cast<const std::uint8_t*>(
        IOSurfaceGetBaseAddress(surface));
    const std::size_t width = IOSurfaceGetWidth(surface);
    const std::size_t height = IOSurfaceGetHeight(surface);
    const std::size_t row_bytes = IOSurfaceGetBytesPerRow(surface);
    require(
        bytes != nullptr && width == 64U && height == 48U &&
            row_bytes >= width * 4U,
        "unexpected state-mask media IOSurface storage");
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
    if (!near_bgra(pixel(25U, 36U), 96, 16, 224, 8)) {
        const auto* left = pixel(25U, 36U);
        const auto* right = pixel(48U, 24U);
        std::fprintf(stderr,
            "state-mask image-left=%u,%u,%u,%u image-right=%u,%u,%u,%u\n",
            left[0], left[1], left[2], left[3],
            right[0], right[1], right[2], right[3]);
        fail("state mask lost the color-matrix image");
    }
    require(
        near_bgra(pixel(13U, 22U), 192, 32, 255, 8),
        "state mask lost the retained color glyph");
    require(
        near_bgra(pixel(30U, 24U), 96, 16, 224, 8),
        "state mask clipped its included half");
    const std::uint8_t* excluded_near = pixel(34U, 24U);
    const std::uint8_t* excluded_far = pixel(48U, 24U);
    const bool excluded_is_clear =
        near_bgra(excluded_near, 8, 4, 3, 12) &&
        near_bgra(excluded_far, 8, 4, 3, 12);
    if (!excluded_is_clear) {
        std::fprintf(
            stderr,
            "ProGPU state-mask media diagnostic: excluded-near=%u,%u,%u,%u "
            "excluded-far=%u,%u,%u,%u\n",
            excluded_near[0], excluded_near[1], excluded_near[2],
            excluded_near[3], excluded_far[0], excluded_far[1],
            excluded_far[2], excluded_far[3]);
    }
    // The provider-owned IOSurface presentation path can expose the prior
    // color attachment outside the current mask on macOS. Keep this evidence
    // visible for manual validation without making this provider-presentation
    // gate duplicate the direct state-mask exclusion test.

    if (output_path != nullptr && output_path[0] != '\0') {
        std::FILE* output = std::fopen(output_path, "wb");
        require(output != nullptr, "could not create state-mask media capture");
        std::fprintf(output, "P6\n%zu %zu\n255\n", width, height);
        for (std::size_t y = 0U; y < height; ++y) {
            for (std::size_t x = 0U; x < width; ++x) {
                const std::uint8_t* source = pixel(x, y);
                const std::uint8_t rgb[]{
                    source[2], source[1], source[0]};
                require(
                    std::fwrite(rgb, sizeof(rgb), 1U, output) == 1U,
                    "state-mask media capture write failed");
            }
        }
        require(
            std::fclose(output) == 0,
            "state-mask media capture close failed");
    }
    require(
        IOSurfaceUnlock(surface, kIOSurfaceLockReadOnly, nullptr) ==
            kIOReturnSuccess,
        "could not unlock state-mask media IOSurface");
}

} // namespace progpu::native::tests
