#include "progpu_native_motion_mark.hpp"

#include <cmath>
#include <cstddef>
#include <cstdint>
#include <cstdlib>
#include <vector>

namespace {

void require(bool condition) {
    if (!condition) {
        std::abort();
    }
}

void managed_motion_mark_contract_is_preserved() {
    using progpu::native::samples::motion_mark_scene;
    using progpu::native::samples::motion_mark_scene_metrics;

    motion_mark_scene scene{1000U, 0x12345678U};
    require(scene.element_count() == 1000U);
    require(scene.group_count() > 0U);
    require(scene.group_count() <= scene.element_count());
    require(scene.primitives().size() >= scene.element_count());
    require(scene.primitives().size() < scene.element_count() * 2U);

    std::vector<std::byte> stream{};
    motion_mark_scene_metrics metrics{};
    require(scene.compile(stream, metrics));
    require(!stream.empty());
    require(metrics.element_count == 1000U);
    require(metrics.group_count == scene.group_count());
    require(metrics.primitive_count == scene.primitives().size());
    require(metrics.brush_count > 0U);
    require(metrics.brush_count <= metrics.group_count);
    require(metrics.command_count == 1U);
    require(metrics.resource_count == 2U);
    require(metrics.stream_bytes == stream.size());
    const auto first_generation = metrics.generation;

    require(!scene.dirty());
    require(!scene.advance(1.0F / 240.0F));
    require(!scene.dirty());
    require(scene.advance(1.0F / 60.0F));
    require(scene.dirty());
    require(scene.compile(stream, metrics));
    require(metrics.generation > first_generation);
    require(metrics.command_count == 1U);

    require(scene.resize(640.0F, 360.0F));
    for (const auto& primitive : scene.primitives()) {
        require(std::isfinite(primitive.p0.x));
        require(std::isfinite(primitive.p0.y));
        require(primitive.stroke_thickness > 0.0F);
    }
    require(scene.set_element_count(2500U));
    require(scene.element_count() == 2500U);
    require(scene.set_color_mode(1U));
    require(scene.regenerate(0xCAFEBABEU));
    require(scene.compile(stream, metrics));
    require(metrics.element_count == 2500U);
    require(metrics.brush_count > 0U);
    require(metrics.command_count == 1U);
}

} // namespace

int main() {
    managed_motion_mark_contract_is_preserved();
    return 0;
}
