#include <progpu_native_scene_builder.hpp>

#include <cstddef>
#include <cstdint>
#include <vector>

int main() {
    progpu::native::semantic_scene_builder builder(42U, 1U);
    if (!builder.reserve(1U, 1U, 256U)) {
        return 1;
    }

    std::uint32_t brush = 0U;
    if (!builder.add_solid_brush(
            progpu_native_color{0.0F, 0.5F, 1.0F, 1.0F}, 1.0F, brush)) {
        return 2;
    }

    std::vector<std::byte> stream;
    progpu::native::scene_build_metrics metrics{};
    if (!builder.build(stream, &metrics) || stream.empty() ||
        metrics.brush_count != 1U) {
        return 3;
    }
    return 0;
}
