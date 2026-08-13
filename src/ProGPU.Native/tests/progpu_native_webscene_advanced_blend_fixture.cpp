#include "progpu_native_webscene_advanced_blend_fixture.hpp"

#include <cmath>
#include <cstdint>
#include <cstdio>
#include <cstdlib>

namespace progpu::native::tests {
namespace {

[[noreturn]] void fail(const char* message) {
    std::fprintf(stderr,
        "ProGPU WebScene advanced-blend fixture failed: %s\n",
        message);
    std::abort();
}

void require(bool condition, const char* message) {
    if (!condition) {
        fail(message);
    }
}

} // namespace

void verify_semantic_advanced_blend_scene(
    IOSurfaceRef surface,
    const char* output_path) {
    require(surface != nullptr,
        "semantic advanced-blend scene has no IOSurface");
    require(IOSurfaceLock(surface, kIOSurfaceLockReadOnly, nullptr) ==
        kIOReturnSuccess,
        "could not lock semantic advanced-blend IOSurface");
    const auto* bytes = static_cast<const std::uint8_t*>(
        IOSurfaceGetBaseAddress(surface));
    const std::size_t width = IOSurfaceGetWidth(surface);
    const std::size_t height = IOSurfaceGetHeight(surface);
    const std::size_t row_bytes = IOSurfaceGetBytesPerRow(surface);
    require(bytes != nullptr && width == 64U && height == 48U &&
        row_bytes >= width * 4U,
        "unexpected semantic advanced-blend IOSurface storage");
    const auto pixel = [bytes, row_bytes](std::size_t x, std::size_t y) {
        return bytes + y * row_bytes + x * 4U;
    };
    const auto near = [](std::uint8_t actual, std::uint8_t expected) {
        return std::abs(static_cast<int>(actual) - expected) <= 20;
    };
    const auto* destination = pixel(8U, 8U);
    const auto* blended = pixel(20U, 20U);
    require(near(destination[2], 51U) &&
            near(destination[1], 204U) &&
            near(destination[0], 102U) && destination[3] >= 240U,
        "semantic advanced blend lost the rendered parent destination");
    require(near(blended[2], 26U) &&
            near(blended[1], 102U) &&
            near(blended[0], 51U) && blended[3] >= 240U,
        "semantic multiply did not sample the rendered parent destination");

    if (output_path != nullptr && output_path[0] != '\0') {
        std::FILE* output = std::fopen(output_path, "wb");
        require(output != nullptr,
            "could not create semantic advanced-blend capture");
        std::fprintf(output, "P6\n%zu %zu\n255\n", width, height);
        for (std::size_t y = 0U; y < height; ++y) {
            for (std::size_t x = 0U; x < width; ++x) {
                const auto* value = pixel(x, y);
                const std::uint8_t rgb[]{value[2], value[1], value[0]};
                require(std::fwrite(rgb, sizeof(rgb), 1U, output) == 1U,
                    "semantic advanced-blend capture write failed");
            }
        }
        require(std::fclose(output) == 0,
            "semantic advanced-blend capture close failed");
    }
    require(IOSurfaceUnlock(surface, kIOSurfaceLockReadOnly, nullptr) ==
        kIOReturnSuccess,
        "could not unlock semantic advanced-blend IOSurface");
}

} // namespace progpu::native::tests
