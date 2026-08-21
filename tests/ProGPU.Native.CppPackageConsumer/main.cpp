#include <progpu_native_scene_builder.hpp>
#include <progpu_native_hit_testing.hpp>
#include <progpu_native_dawn.h>

#include <cstddef>
#include <cstdint>
#include <vector>

int main() {
    if (progpu_native_dawn_get_adapter_abi_version() !=
        PROGPU_NATIVE_DAWN_ADAPTER_ABI_VERSION) {
        return 5;
    }
    progpu::native::hit_testing::hit_test_index hit_test_index;
    if (!hit_test_index.nodes().empty()) {
        return 1;
    }
    progpu::native::semantic_scene_builder builder(42U, 1U);
    if (!builder.reserve(1U, 1U, 256U)) {
        return 2;
    }

    std::uint32_t brush = 0U;
    if (!builder.add_solid_brush(
            progpu_native_color{0.0F, 0.5F, 1.0F, 1.0F}, 1.0F, brush)) {
        return 3;
    }

    const std::size_t required_size = builder.required_stream_size();
    std::vector<std::byte> stream(required_size);
    std::size_t bytes_written = 0U;
    progpu::native::scene_build_metrics metrics{};
    if (required_size == 0U ||
        !builder.build_into(stream, bytes_written, &metrics) ||
        bytes_written != required_size ||
        metrics.brush_count != 1U) {
        return 4;
    }
    return 0;
}
