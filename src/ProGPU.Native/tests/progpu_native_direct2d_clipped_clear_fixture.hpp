#pragma once

#include "progpu_native_direct2d_clear_fixture.hpp"
#include "progpu_native_direct2d_compat.hpp"

#include <cstdio>
#include <vector>

namespace progpu::native::direct2d::tests {

// This same public-vtable workload runs against portable COM and Microsoft's
// original render target. The captured intersection is [12,24) x [14,26).
inline com::result record_clipped_clear(compat::render_target* target, bool null_clear,
    bool empty = false, bool fractional = false)
{
    const compat::color_f red{1, 0, 0, 1}, blue{0, 0, 1, 1}, clear{0.75F, 0.5F, 0.25F, 0.5F};
    com::pointer<compat::solid_color_brush> before, after;
    auto status = target->CreateSolidColorBrush(&red, nullptr, before.put());
    if (com::failed(status)) return status;
    status = target->CreateSolidColorBrush(&blue, nullptr, after.put());
    if (com::failed(status)) return status;
    const compat::matrix_3x2_f identity{1, 0, 0, 1, 0, 0}, capture{2, 0, 0, 2, 4, 6};
    const compat::matrix_3x2_f singular{0, 0, 0, 0, 99, 88}, suffix{1, 0, 0, 1, 2, 3};
    const compat::rectangle_f whole{0, 0, 64, 64}, first{2, 3, 14, 15};
    const compat::rectangle_f second = empty ? compat::rectangle_f{40, 40, 45, 45}
        : fractional ? compat::rectangle_f{12.75F, 14.75F, 23.75F, 25.75F}
                     : compat::rectangle_f{12, 14, 24, 26};
    const compat::rectangle_f last{0, 0, 3, 4};
    target->BeginDraw();
    target->SetTransform(&identity);
    target->SetAntialiasMode(compat::antialias_mode::aliased);
    target->Clear(nullptr);
    target->FillRectangle(&whole, before.get());
    target->SetTransform(&capture);
    target->PushAxisAlignedClip(&first, compat::antialias_mode::aliased);
    target->SetTransform(&identity);
    target->PushAxisAlignedClip(&second, compat::antialias_mode::aliased);
    target->SetTransform(&singular);
    target->SetTags(123U, 456U);
    target->Clear(null_clear ? nullptr : &clear);
    compat::matrix_3x2_f retained{};
    target->GetTransform(&retained);
    std::uint64_t tag1{}, tag2{};
    target->GetTags(&tag1, &tag2);
    const bool retained_state = std::memcmp(&retained, &singular, sizeof(singular)) == 0 &&
        tag1 == 123U && tag2 == 456U && target->GetAntialiasMode() == compat::antialias_mode::aliased;
    target->PopAxisAlignedClip();
    target->PopAxisAlignedClip();
    target->SetTransform(&suffix);
    target->FillRectangle(&last, after.get());
    status = target->EndDraw(nullptr, nullptr);
    return com::failed(status) || retained_state ? status : compat::wrong_state;
}

inline bool clipped_clear_contract(std::span<const std::byte> bytes, bool null_clear,
    bool ignore_alpha = false, bool empty = false, bool fractional = false)
{
    progpu_native_scene_header header{};
    if (!read_scene_value(bytes, 0U, header) || header.command_count != (empty ? 6U : 9U)) return false;
    constexpr std::array kinds{PROGPU_NATIVE_SCENE_COMMAND_DRAW_ANALYTIC,
        PROGPU_NATIVE_SCENE_COMMAND_SAVE, PROGPU_NATIVE_SCENE_COMMAND_SAVE,
        PROGPU_NATIVE_SCENE_COMMAND_PUSH_LAYER, PROGPU_NATIVE_SCENE_COMMAND_DRAW_ANALYTIC,
        PROGPU_NATIVE_SCENE_COMMAND_POP_LAYER, PROGPU_NATIVE_SCENE_COMMAND_RESTORE,
        PROGPU_NATIVE_SCENE_COMMAND_RESTORE, PROGPU_NATIVE_SCENE_COMMAND_DRAW_ANALYTIC};
    std::array<progpu_native_scene_command, kinds.size()> commands{};
    for (std::uint32_t i = 0U; i < header.command_count; ++i) {
        if (!read_scene_value(bytes, header.command_offset + std::uint64_t{i} * header.command_stride, commands[i]) ||
            commands[i].kind != static_cast<std::uint32_t>(kinds[empty && i >= 3U ? i + 3U : i])) return false;
    }
    if (empty) return true;
    const float left = fractional ? 12.75F : 12.0F, top = fractional ? 14.75F : 14.0F;
    const float extent = fractional ? 11.0F : 12.0F;
    progpu_native_scene_layer layer{};
    if (!read_scene_value(bytes, commands[3].payload_offset, layer) ||
        layer.struct_size != sizeof(layer) || layer.flags != (PROGPU_NATIVE_SCENE_LAYER_BOUNDS |
            PROGPU_NATIVE_SCENE_LAYER_ALIASED_COMPOSITE_BOUNDS) ||
        layer.bounds.x != left || layer.bounds.y != top || layer.bounds.width != extent || layer.bounds.height != extent ||
        layer.opacity != 1 || layer.blend_mode != PROGPU_NATIVE_BLEND_SRC ||
        layer.mask_resource_index != PROGPU_NATIVE_SCENE_NO_INDEX ||
        layer.effect_resource_index != PROGPU_NATIVE_SCENE_NO_INDEX) return false;
    progpu_native_scene_resource geometry{}, table{};
    progpu_native_analytic_primitive primitive{};
    progpu_native_scene_draw_brushes draw{};
    progpu_native_scene_brush brush{};
    std::uint32_t brush_index{};
    const auto& command = commands[4];
    if (command.bounds_x != left || command.bounds_y != top || command.bounds_width != extent || command.bounds_height != extent ||
        !read_scene_value(bytes, header.resource_offset + std::uint64_t{command.resource_index} * header.resource_stride, geometry) ||
        geometry.kind != PROGPU_NATIVE_SCENE_RESOURCE_ANALYTIC_BATCH || geometry.payload_size != sizeof(primitive) ||
        !read_scene_value(bytes, geometry.payload_offset, primitive) ||
        primitive.x != left || primitive.y != top || primitive.width != extent || primitive.height != extent ||
        primitive.flags != PROGPU_NATIVE_PRIMITIVE_FLAG_EDGE_ALIASED ||
        primitive.transform.m11 != 1 || primitive.transform.m12 != 0 || primitive.transform.m21 != 0 ||
        primitive.transform.m22 != 1 || primitive.transform.m31 != 0 || primitive.transform.m32 != 0 ||
        !read_scene_value(bytes, command.payload_offset, draw) || draw.brush_count != 1U ||
        !read_scene_value(bytes, command.payload_offset + sizeof(draw), brush_index) ||
        !read_scene_value(bytes, header.resource_offset + std::uint64_t{draw.brush_resource_index} * header.resource_stride, table) ||
        !read_scene_value(bytes, table.payload_offset + std::uint64_t{brush_index} * sizeof(brush), brush)) return false;
    return brush.type == PROGPU_NATIVE_SCENE_BRUSH_SOLID && brush.opacity == 1 &&
        brush.colors[0].r == (null_clear ? 0 : 0.75F) && brush.colors[0].g == (null_clear ? 0 : 0.5F) &&
        brush.colors[0].b == (null_clear ? 0 : 0.25F) && brush.colors[0].a == (ignore_alpha ? 1 : null_clear ? 0 : 0.5F);
}

inline bool clipped_clear_pixels(std::span<const std::uint8_t> pixels, std::uint32_t image_height,
    bool null_clear, bool ignore_alpha, bool bgra = false, bool fractional = false)
{
    if (pixels.size() != std::size_t{image_height} * 256U) return false;
    for (std::uint32_t y = 0U; y < image_height; ++y) {
        for (std::uint32_t x = 0U; x < 64U; ++x) {
            const bool suffix = x >= 2U && x < 5U && y >= 3U && y < 7U;
            const bool cleared = x >= (fractional ? 13U : 12U) && x < 24U &&
                y >= (fractional ? 15U : 14U) && y < 26U;
            const std::array<int, 4U> expected = suffix ? std::array<int, 4U>{0, 0, 255, 255}
                : !cleared ? std::array<int, 4U>{255, 0, 0, 255}
                : null_clear ? std::array<int, 4U>{0, 0, 0, ignore_alpha ? 255 : 0}
                : ignore_alpha ? std::array<int, 4U>{191, 128, 64, 255}
                               : std::array<int, 4U>{96, 64, 32, 128};
            for (std::size_t channel = 0U; channel < 4U; ++channel) {
                const auto index = std::size_t{y} * 256U + x * 4U + (bgra && channel < 3U ? 2U - channel : channel);
                // Only nonintegral normalized-color conversion may differ by
                // one byte; history, clip coverage and binary alpha stay exact.
                const int tolerance = expected[channel] == 0 || expected[channel] == 255 ? 0 : 1;
                if (std::abs(static_cast<int>(pixels[index]) - expected[channel]) > tolerance) {
                    std::fprintf(stderr, "clipped Clear mismatch x=%u y=%u channel=%zu actual=%u expected=%d "
                        "null=%u ignore=%u bgra=%u fractional=%u\n", x, y, channel,
                        static_cast<unsigned>(pixels[index]), expected[channel],
                        static_cast<unsigned>(null_clear), static_cast<unsigned>(ignore_alpha),
                        static_cast<unsigned>(bgra), static_cast<unsigned>(fractional));
                    return false;
                }
            }
        }
    }
    return true;
}

template<class Render, class Require>
void verify_clipped_clear(Render render, Require require)
{
    com::pointer<compat::factory> factory;
    require(compat::create_factory(factory.put()) == com::ok, "clipped Clear factory creation failed");
    com::pointer<compat::formatted_scene_factory_native> scene_factory;
    require(factory.as(compat::formatted_scene_factory_native_interface_id, scene_factory) == com::ok,
        "clipped Clear formatted factory query failed");
    for (unsigned variant = 0U; variant < 8U; ++variant) {
        const bool null_clear = (variant & 1U) != 0U, ignore_alpha = (variant & 2U) != 0U;
        const bool fractional = variant >= 4U;
        const compat::scene_render_target_properties properties{64, 64, 96, 96, 0x94F8U + variant, 1U};
        const compat::pixel_format format{87U, ignore_alpha ? compat::alpha_mode::ignore : compat::alpha_mode::premultiplied};
        com::pointer<compat::render_target> target;
        require(scene_factory->CreateFormattedSceneRenderTarget(&properties, &format, target.put()) == com::ok,
            "clipped Clear target creation failed");
        require(record_clipped_clear(target.get(), null_clear, false, fractional) == com::ok, "clipped Clear recording failed");
        com::pointer<compat::scene_render_target_native> scene;
        require(target.as(compat::scene_render_target_native_interface_id, scene) == com::ok,
            "clipped Clear scene query failed");
        std::vector<std::byte> bytes(static_cast<std::size_t>(scene->GetRequiredSceneSize()));
        std::uint64_t written{};
        require(scene->BuildScene(bytes.data(), bytes.size(), &written) == com::ok && written == bytes.size() &&
            clipped_clear_contract(bytes, null_clear, ignore_alpha, false, fractional), "clipped Clear retained structure changed");
        const auto cold = render(scene.get());
        const auto warm = render(scene.get());
        if (cold != warm) {
            const auto mismatch = std::mismatch(cold.begin(), cold.end(), warm.begin(), warm.end());
            std::fprintf(stderr, "clipped Clear cold/warm mismatch variant=%u offset=%zu cold-size=%zu warm-size=%zu\n",
                variant, static_cast<std::size_t>(mismatch.first - cold.begin()), cold.size(), warm.size());
        }
        const bool expected_pixels = clipped_clear_pixels(cold, 64U, null_clear, ignore_alpha, false, fractional);
        require(cold == warm && expected_pixels,
            "clipped Clear cold/warm pixels lost history, transparent replacement, alpha or captured clip frame");
    }
}

} // namespace progpu::native::direct2d::tests
