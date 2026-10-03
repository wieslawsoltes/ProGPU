#include <progpu_native_scene_builder.hpp>
#include <progpu_native_hit_testing.hpp>
#include <progpu_native_mil.hpp>
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
    progpu::native::mil::channel mil_channel;
    if (mil_channel.resource_count() != 0U) {
        return 6;
    }
    if (mil_channel.set_visual_source_empty_bounds(1U) != progpu::native::mil::status::invalid_handle ||
        progpu_native_mil_channel_set_visual_source_empty_bounds(nullptr, 1U) != PROGPU_NATIVE_MIL_STATUS_INVALID_ARGUMENT) {
        return 7;
    }
    const progpu::native::mil::bitmap_cache_raster_policy raster_policy{1.5F,2.0F,1024U,2048U,1U};
    const progpu_native_mil_bitmap_cache_raster_policy wire_policy{
        sizeof(wire_policy),1U,0U,0U,1.5F,2.0F,1024U,2048U,1U};
    progpu_native_cache_raster_limits limits{sizeof(limits),1U,17U,19U};
    if (mil_channel.set_bitmap_cache_brush_raster_policy(1U,raster_policy) !=
            progpu::native::mil::status::invalid_handle ||
        progpu_native_mil_channel_set_bitmap_cache_brush_raster_policy(nullptr,1U,&wire_policy) !=
            PROGPU_NATIVE_MIL_STATUS_INVALID_ARGUMENT ||
        progpu_native_engine_get_cache_raster_limits(nullptr,&limits) != PROGPU_NATIVE_STATUS_INVALID_ARGUMENT ||
        limits.maximum_texture_width != 17U || limits.maximum_texture_height != 19U) return 8;
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
