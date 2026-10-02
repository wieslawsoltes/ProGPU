#pragma once

#include "progpu_native_direct2d_clear_fixture.hpp"

#include <array>
#include <cstdio>
#include <vector>

namespace progpu::native::direct2d::tests {

inline compat::layer_options1 layer_background_options(std::uint32_t variant) noexcept
{
    return static_cast<compat::layer_options1>(variant < 8U ? 1U : variant < 16U ? 3U : 2U);
}

// Same original source calls for portable OPTIONS1 and actual Windows device
// contexts. Only the typed PushLayer1 crossing differs; no legacy enum cast.
template<class Push>
com::result record_layer_background(compat::render_target* target,
    compat::factory* factory, std::uint32_t variant, Push push)
{
    const compat::color_f blue{0, 0, 1, (variant & 1U) != 0U ? 0.5F : 1.0F};
    const compat::color_f red{1, 0, 0, 1}, green{0, 1, 0, 1};
    com::pointer<compat::solid_color_brush> paint, suffix;
    auto result = target->CreateSolidColorBrush(&red, nullptr, paint.put());
    if (com::failed(result)) return result;
    result = target->CreateSolidColorBrush(&green, nullptr, suffix.put());
    if (com::failed(result)) return result;
    com::pointer<compat::rectangle_geometry> mask;
    const compat::rectangle_f mask_bounds{16, 8, 40, 50};
    if ((variant & 4U) != 0U) {
        result = factory->CreateRectangleGeometry(&mask_bounds, mask.put());
        if (com::failed(result)) return result;
    }
    const compat::matrix_3x2_f identity{1, 0, 0, 1, 0, 0};
    const compat::layer_parameters1 parameters{{4, 6, 56, 54}, mask.get(),
        compat::antialias_mode::aliased, identity, (variant & 2U) != 0U ? 0.5F : 1.0F,
        nullptr, layer_background_options(variant)};
    const compat::rectangle_f rectangle{12, 10, 48, 44}, tail{0, 0, 4, 4};
    target->BeginDraw();
    target->SetTransform(&identity);
    target->SetAntialiasMode(compat::antialias_mode::aliased);
    target->Clear(&blue);
    push(parameters);
    target->FillRectangle(&rectangle, paint.get());
    target->PopLayer();
    target->FillRectangle(&tail, suffix.get());
    return target->EndDraw(nullptr, nullptr);
}

inline bool layer_background_contract(std::span<const std::byte> bytes, std::uint32_t variant)
{
    progpu_native_scene_header header{};
    progpu_native_scene_command push{};
    progpu_native_scene_layer layer{};
    return read_scene_value(bytes, 0U, header) && header.command_count == 4U &&
        read_scene_value(bytes, header.command_offset, push) &&
        push.kind == PROGPU_NATIVE_SCENE_COMMAND_PUSH_LAYER &&
        read_scene_value(bytes, push.payload_offset, layer) &&
        layer.flags == (PROGPU_NATIVE_SCENE_LAYER_BOUNDS |
            (variant < 16U ? PROGPU_NATIVE_SCENE_LAYER_INITIALIZE_FROM_BACKGROUND : 0U) |
            (variant >= 8U ? PROGPU_NATIVE_SCENE_LAYER_IGNORE_ALPHA : 0U)) &&
        layer.blend_mode == PROGPU_NATIVE_BLEND_SRC_OVER &&
        layer.effect_resource_index == PROGPU_NATIVE_SCENE_NO_INDEX;
}

inline bool layer_background_pixels(std::span<const std::uint8_t> pixels,
    std::uint32_t image_height, std::uint32_t variant, bool bgra = false)
{
    if (pixels.size() != std::size_t{image_height} * 256U) return false;
    const std::uint8_t background_alpha = (variant & 1U) != 0U ? 128U : 255U;
    for (std::uint32_t y = 0U; y < image_height; ++y) {
        for (std::uint32_t x = 0U; x < 64U; ++x) {
            const bool layer = x >= 4U && x < 56U && y >= 6U && y < 54U &&
                ((variant & 4U) == 0U || (x >= 16U && x < 40U && y >= 8U && y < 50U));
            std::array<std::uint8_t, 4U> expected{0, 0, background_alpha, background_alpha};
            if (layer) {
                auto child = variant < 16U ? expected : std::array<std::uint8_t, 4U>{0, 0, 0, 255};
                if (variant >= 8U) child[3] = 255U;
                if (x >= 12U && x < 48U && y >= 10U && y < 44U) child = {255, 0, 0, 255};
                for (std::size_t c = 0U; c < 4U; ++c) expected[c] = (variant & 2U) == 0U ? child[c]
                    : static_cast<std::uint8_t>((static_cast<unsigned>(expected[c]) + child[c] + 1U) / 2U);
            }
            if (x < 4U && y < 4U) expected = {0, 255, 0, 255};
            for (std::size_t c = 0U; c < 4U; ++c) {
                const auto actual = pixels[std::size_t{y} * 256U + x * 4U + (bgra && c < 3U ? 2U - c : c)];
                if (actual != expected[c]) {
                    std::fprintf(stderr, "layer background variant=%u x=%u y=%u channel=%zu actual=%u expected=%u\n",
                        variant, x, y, c, static_cast<unsigned>(actual), static_cast<unsigned>(expected[c]));
                    std::fflush(stderr);
                    return false;
                }
            }
        }
    }
    return true;
}

inline bool layer_background_source_contract(compat::factory* factory)
{
    com::pointer<compat::scene_factory_native> create;
    if (factory->QueryInterface(compat::scene_factory_native_interface_id,
            reinterpret_cast<void**>(create.put())) != com::ok) return false;
    for (std::uint32_t variant = 0U; variant < 24U; ++variant) {
        const compat::scene_render_target_properties properties{64, 64, 96, 96, 0xBD00U + variant, 1U};
        com::pointer<compat::render_target> target;
        com::pointer<compat::scene_layer_options_native> options;
        com::pointer<compat::scene_render_target_native> scene;
        if (create->CreateSceneRenderTarget(&properties, target.put()) != com::ok ||
            target.as(compat::scene_layer_options_native_interface_id, options) != com::ok ||
            target.as(compat::scene_render_target_native_interface_id, scene) != com::ok ||
            record_layer_background(target.get(), factory, variant,
                [&](const auto& parameters) { options->PushLayer1(&parameters, nullptr); }) != com::ok) return false;
        std::vector<std::byte> bytes(static_cast<std::size_t>(scene->GetRequiredSceneSize()));
        std::uint64_t written{};
        if (scene->BuildScene(bytes.data(), bytes.size(), &written) != com::ok ||
            written != bytes.size() || !layer_background_contract(bytes, variant)) return false;
        // Unknown OPTIONS1 values cannot publish a scope or a usable scene.
        const compat::layer_parameters1 invalid{{0, 0, 64, 64}, nullptr,
            compat::antialias_mode::aliased, {1, 0, 0, 1, 0, 0}, 1, nullptr,
            static_cast<compat::layer_options1>(4U << (variant % 8U))};
        target->BeginDraw();
        target->SetTags(31, 47);
        options->PushLayer1(&invalid, nullptr);
        std::uint64_t tag1{}, tag2{};
        if (target->EndDraw(&tag1, &tag2) != com::invalid_argument || tag1 != 31U || tag2 != 47U) return false;
        std::array<std::byte, 64U> untouched;
        untouched.fill(std::byte{0x5a});
        const auto before = untouched;
        if (com::succeeded(scene->BuildScene(untouched.data(), untouched.size(), &written)) ||
            untouched != before || written != 0U) return false;
    }
    return true;
}

template<class Render, class Require>
void verify_layer_background(Render render, Require require)
{
    com::pointer<compat::factory> factory;
    require(compat::create_factory(factory.put()) == com::ok, "background layer factory failed");
    com::pointer<compat::scene_factory_native> create;
    require(factory.as(compat::scene_factory_native_interface_id, create) == com::ok,
        "background layer factory interface failed");
    for (std::uint32_t variant = 0U; variant < 24U; ++variant) {
        const compat::scene_render_target_properties properties{64, 64, 96, 96, 0xBA00U + variant, 1U};
        com::pointer<compat::render_target> target;
        require(create->CreateSceneRenderTarget(&properties, target.put()) == com::ok, "background layer target failed");
        com::pointer<compat::scene_layer_options_native> options;
        com::pointer<compat::scene_render_target_native> scene;
        require(target.as(compat::scene_layer_options_native_interface_id, options) == com::ok &&
            target.as(compat::scene_render_target_native_interface_id, scene) == com::ok,
            "background layer typed OPTIONS1 interface failed");
        require(record_layer_background(target.get(), factory.get(), variant,
                [&](const auto& parameters) { options->PushLayer1(&parameters, nullptr); }) == com::ok,
            "background layer source recording failed");
        std::vector<std::byte> bytes(static_cast<std::size_t>(scene->GetRequiredSceneSize()));
        std::uint64_t written{};
        require(scene->BuildScene(bytes.data(), bytes.size(), &written) == com::ok &&
            written == bytes.size() && layer_background_contract(bytes, variant),
            "background layer must retain its own initialization identity");
        const auto cold = render(scene.get());
        const auto warm = render(scene.get());
        require(cold == warm && layer_background_pixels(cold, 64U, variant),
            "background layer cold/warm full bytes changed copied alpha or independent coverage");
    }
}

} // namespace progpu::native::direct2d::tests
