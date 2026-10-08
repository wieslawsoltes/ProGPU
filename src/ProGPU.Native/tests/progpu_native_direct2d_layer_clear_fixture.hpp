#pragma once

#include "progpu_native_direct2d_clear_fixture.hpp"

#include <cstdio>
#include <vector>

namespace progpu::native::direct2d::tests {

// Identical original source calls for legacy PushLayer and OPTIONS1_NONE.
// Bits: geometry, opacity brush, group opacity, null Clear, partial clip.
template<class Push>
com::result record_transparent_layer_clear(compat::render_target* target,
    compat::factory* factory, std::uint32_t variant, Push push)
{
    const compat::color_f white{1, 1, 1, 1}, red{1, 0, 0, 1}, blue{0, 0, 1, 1};
    const compat::color_f green{0, 1, 0, 1}, half{1, 1, 1, 0.5F};
    com::pointer<compat::solid_color_brush> before, after, opacity;
    auto status = target->CreateSolidColorBrush(&red, nullptr, before.put());
    if (com::failed(status)) return status;
    status = target->CreateSolidColorBrush(&blue, nullptr, after.put());
    if (com::failed(status)) return status;
    if ((variant & 2U) != 0U) {
        status = target->CreateSolidColorBrush(&half, nullptr, opacity.put());
        if (com::failed(status)) return status;
    }
    com::pointer<compat::rectangle_geometry> mask;
    const compat::rectangle_f mask_bounds{8, 4, 32, 36};
    if ((variant & 1U) != 0U) {
        status = factory->CreateRectangleGeometry(&mask_bounds, mask.put());
        if (com::failed(status)) return status;
    }
    const compat::matrix_3x2_f identity{1, 0, 0, 1, 0, 0}, capture{1, 0, 0, 1, 4, 6};
    const compat::matrix_3x2_f unrelated{0, 0, 0, 0, 123, 456};
    const compat::rectangle_f whole{0, 0, 48, 44}, clip{20, 16, 44, 36}, suffix{0, 0, 4, 4};
    const compat::layer_parameters parameters{whole, mask.get(), compat::antialias_mode::aliased,
        identity, (variant & 4U) != 0U ? 0.5F : 1.0F, opacity.get(), compat::layer_options::none};
    target->BeginDraw();
    target->SetTransform(&identity);
    target->SetAntialiasMode(compat::antialias_mode::aliased);
    target->Clear(&white);
    target->SetTransform(&capture);
    push(parameters);
    target->FillRectangle(&whole, before.get());
    target->SetTransform(&identity);
    if ((variant & 16U) != 0U) target->PushAxisAlignedClip(&clip, compat::antialias_mode::aliased);
    target->SetTransform(&unrelated);
    target->SetTags(123U, 456U);
    target->Clear((variant & 8U) != 0U ? nullptr : &green);
    compat::matrix_3x2_f retained{};
    target->GetTransform(&retained);
    std::uint64_t tag1{}, tag2{};
    target->GetTags(&tag1, &tag2);
    const bool unchanged = std::memcmp(&retained, &unrelated, sizeof(retained)) == 0 &&
        tag1 == 123U && tag2 == 456U && target->GetAntialiasMode() == compat::antialias_mode::aliased;
    if ((variant & 16U) != 0U) target->PopAxisAlignedClip();
    target->PopLayer();
    target->SetTransform(&identity);
    target->FillRectangle(&suffix, after.get());
    status = target->EndDraw(nullptr, nullptr);
    return com::failed(status) || unchanged ? status : compat::wrong_state;
}

inline bool transparent_layer_clear_contract(std::span<const std::byte> bytes,
    std::uint32_t variant, std::uint32_t source_draws = 2U)
{
    progpu_native_scene_header header{};
    progpu_native_scene_command first{};
    progpu_native_scene_layer parent{};
    const bool mask = (variant & 1U) != 0U, clipped = (variant & 16U) != 0U;
    const std::uint32_t flags = PROGPU_NATIVE_SCENE_LAYER_BOUNDS |
        ((variant & 7U) == 0U ? static_cast<std::uint32_t>(PROGPU_NATIVE_SCENE_LAYER_FORCE_ISOLATION) : 0U);
    if (!read_scene_value(bytes, 0U, header) || header.command_count != source_draws + 5U + (clipped ? 2U : 0U) ||
        !read_scene_value(bytes, header.command_offset, first) ||
        first.kind != PROGPU_NATIVE_SCENE_COMMAND_PUSH_LAYER ||
        !read_scene_value(bytes, first.payload_offset, parent) || parent.flags != flags ||
        parent.bounds.x != (mask ? 12 : 4) || parent.bounds.y != (mask ? 10 : 6) ||
        parent.bounds.width != (mask ? 24 : 48) || parent.bounds.height != (mask ? 32 : 44) ||
        parent.opacity != ((variant & 4U) != 0U ? 0.5F : 1.0F) ||
        parent.blend_mode != PROGPU_NATIVE_BLEND_SRC_OVER ||
        (parent.mask_resource_index != PROGPU_NATIVE_SCENE_NO_INDEX) != ((variant & 3U) != 0U) ||
        parent.effect_resource_index != PROGPU_NATIVE_SCENE_NO_INDEX) return false;

    const float left = clipped ? 20.0F : mask ? 12.0F : 4.0F;
    const float top = clipped ? 16.0F : mask ? 10.0F : 6.0F;
    const float width = clipped ? mask ? 16.0F : 24.0F : mask ? 24.0F : 48.0F;
    const float height = clipped ? 20.0F : mask ? 32.0F : 44.0F;
    std::uint32_t replacements = 0U;
    for (std::uint32_t i = 1U; i < header.command_count; ++i) {
        progpu_native_scene_command command{};
        if (!read_scene_value(bytes, header.command_offset + std::uint64_t{i} * header.command_stride, command)) return false;
        if (command.kind != PROGPU_NATIVE_SCENE_COMMAND_PUSH_LAYER) continue;
        progpu_native_scene_layer replacement{};
        if (!read_scene_value(bytes, command.payload_offset, replacement) ||
            replacement.flags != (PROGPU_NATIVE_SCENE_LAYER_BOUNDS | PROGPU_NATIVE_SCENE_LAYER_ALIASED_COMPOSITE_BOUNDS) ||
            replacement.bounds.x != left || replacement.bounds.y != top ||
            replacement.bounds.width != width || replacement.bounds.height != height ||
            replacement.opacity != 1 || replacement.blend_mode != PROGPU_NATIVE_BLEND_SRC ||
            replacement.mask_resource_index != PROGPU_NATIVE_SCENE_NO_INDEX ||
            replacement.effect_resource_index != PROGPU_NATIVE_SCENE_NO_INDEX || i + 2U >= header.command_count) return false;
        progpu_native_scene_command draw{}, pop{};
        progpu_native_scene_resource geometry{}, table{};
        progpu_native_analytic_primitive rectangle{};
        progpu_native_scene_draw_brushes brushes{};
        progpu_native_scene_brush color{};
        std::uint32_t brush_index{};
        if (!read_scene_value(bytes, header.command_offset + std::uint64_t{i + 1U} * header.command_stride, draw) ||
            !read_scene_value(bytes, header.command_offset + std::uint64_t{i + 2U} * header.command_stride, pop) ||
            draw.kind != PROGPU_NATIVE_SCENE_COMMAND_DRAW_ANALYTIC || pop.kind != PROGPU_NATIVE_SCENE_COMMAND_POP_LAYER ||
            !read_scene_value(bytes, header.resource_offset + std::uint64_t{draw.resource_index} * header.resource_stride, geometry) ||
            geometry.kind != PROGPU_NATIVE_SCENE_RESOURCE_ANALYTIC_BATCH || geometry.payload_size != sizeof(rectangle) ||
            !read_scene_value(bytes, geometry.payload_offset, rectangle) ||
            rectangle.kind != PROGPU_NATIVE_PRIMITIVE_RECTANGLE || rectangle.flags != PROGPU_NATIVE_PRIMITIVE_FLAG_EDGE_ALIASED ||
            rectangle.x != left || rectangle.y != top || rectangle.width != width || rectangle.height != height ||
            rectangle.transform.m11 != 1 || rectangle.transform.m12 != 0 || rectangle.transform.m21 != 0 ||
            rectangle.transform.m22 != 1 || rectangle.transform.m31 != 0 || rectangle.transform.m32 != 0 ||
            !read_scene_value(bytes, draw.payload_offset, brushes) || brushes.brush_count != 1U ||
            !read_scene_value(bytes, draw.payload_offset + sizeof(brushes), brush_index) ||
            !read_scene_value(bytes, header.resource_offset + std::uint64_t{brushes.brush_resource_index} * header.resource_stride, table) ||
            !read_scene_value(bytes, table.payload_offset + std::uint64_t{brush_index} * sizeof(color), color) ||
            color.type != PROGPU_NATIVE_SCENE_BRUSH_SOLID || color.opacity != 1 ||
            color.colors[0].r != 0 || color.colors[0].g != ((variant & 8U) != 0U ? 0 : 1) ||
            color.colors[0].b != 0 || color.colors[0].a != ((variant & 8U) != 0U ? 0 : 1)) return false;
        ++replacements;
    }
    return replacements == 1U;
}

inline bool transparent_layer_clear_pixels(std::span<const std::uint8_t> pixels,
    std::uint32_t image_height, std::uint32_t variant, bool bgra = false)
{
    if (pixels.size() != std::size_t{image_height} * 256U) return false;
    // Independent source-over onto white. These exact dyadic opacities do not
    // permit atlas-quantization tolerances to conceal applying coverage twice.
    const unsigned halves = ((variant & 2U) != 0U ? 1U : 0U) + ((variant & 4U) != 0U ? 1U : 0U);
    const std::uint8_t opposite = halves == 0U ? 0U : halves == 1U ? 128U : 191U;
    for (std::uint32_t y = 0U; y < image_height; ++y) for (std::uint32_t x = 0U; x < 64U; ++x) {
        const bool in_layer = x >= 4U && x < 52U && y >= 6U && y < 50U &&
            ((variant & 1U) == 0U || (x >= 12U && x < 36U && y >= 10U && y < 42U));
        const bool cleared = in_layer && ((variant & 16U) == 0U ||
            (x >= 20U && x < 44U && y >= 16U && y < 36U));
        std::array<std::uint8_t, 4U> expected{255, 255, 255, 255};
        if (in_layer && !(cleared && (variant & 8U) != 0U)) expected = cleared
            ? std::array<std::uint8_t, 4U>{opposite, 255, opposite, 255}
            : std::array<std::uint8_t, 4U>{255, opposite, opposite, 255};
        if (x < 4U && y < 4U) expected = {0, 0, 255, 255};
        for (std::size_t channel = 0U; channel < 4U; ++channel) {
            const auto actual = pixels[std::size_t{y} * 256U + x * 4U + (bgra && channel < 3U ? 2U - channel : channel)];
            if (actual != expected[channel]) {
                std::fprintf(stderr, "transparent layer Clear variant=%u x=%u y=%u channel=%zu actual=%u expected=%u\n",
                    variant, x, y, channel, static_cast<unsigned>(actual), static_cast<unsigned>(expected[channel]));
                return false;
            }
        }
    }
    return true;
}

inline com::result record_portable_transparent_layer_clear(compat::render_target* target,
    compat::factory* factory, std::uint32_t variant, bool legacy)
{
    com::pointer<compat::layer> layer;
    com::pointer<compat::scene_layer_options_native> options;
    const auto status = legacy ? target->CreateLayer(nullptr, layer.put())
        : target->QueryInterface(compat::scene_layer_options_native_interface_id,
            reinterpret_cast<void**>(options.put()));
    if (com::failed(status)) return status;
    return record_transparent_layer_clear(target, factory, variant, [&](const compat::layer_parameters& parameters) {
        if (legacy) target->PushLayer(&parameters, layer.get());
        else {
            const compat::layer_parameters1 typed{parameters.content_bounds, parameters.geometric_mask,
                parameters.mask_antialias_mode, parameters.mask_transform, parameters.opacity,
                parameters.opacity_brush, compat::layer_options1::none};
            options->PushLayer1(&typed, nullptr);
        }
    });
}

inline bool transparent_layer_clear_source_contract(compat::factory* factory)
{
    com::pointer<compat::scene_factory_native> create;
    if (factory->QueryInterface(compat::scene_factory_native_interface_id,
            reinterpret_cast<void**>(create.put())) != com::ok) return false;
    for (const bool legacy : {true, false}) for (std::uint32_t variant = 0U; variant < 32U; ++variant) {
        const compat::scene_render_target_properties properties{64, 64, 96, 96, 0xBE40U + variant, 1U};
        com::pointer<compat::render_target> target;
        com::pointer<compat::scene_render_target_native> scene;
        if (create->CreateSceneRenderTarget(&properties, target.put()) != com::ok ||
            target.as(compat::scene_render_target_native_interface_id, scene) != com::ok ||
            record_portable_transparent_layer_clear(target.get(), factory, variant, legacy) != com::ok) return false;
        std::vector<std::byte> bytes(static_cast<std::size_t>(scene->GetRequiredSceneSize()));
        std::uint64_t written{};
        if (scene->BuildScene(bytes.data(), bytes.size(), &written) != com::ok || written != bytes.size() ||
            !transparent_layer_clear_contract(bytes, variant)) return false;
    }
    return true;
}

template<class Render, class Require>
void verify_transparent_layer_clear(Render render, Require require)
{
    com::pointer<compat::factory> factory;
    com::pointer<compat::scene_factory_native> create;
    require(compat::create_factory(factory.put()) == com::ok &&
        factory.as(compat::scene_factory_native_interface_id, create) == com::ok,
        "transparent layer Clear factory failed");
    for (const bool legacy : {true, false}) for (std::uint32_t variant = 0U; variant < 32U; ++variant) {
        const compat::scene_render_target_properties properties{64, 64, 96, 96, 0xBE80U + variant, 1U};
        com::pointer<compat::render_target> target;
        com::pointer<compat::scene_render_target_native> scene;
        require(create->CreateSceneRenderTarget(&properties, target.put()) == com::ok &&
            target.as(compat::scene_render_target_native_interface_id, scene) == com::ok &&
            record_portable_transparent_layer_clear(target.get(), factory.get(), variant, legacy) == com::ok,
            "transparent layer Clear recording failed");
        std::vector<std::byte> bytes(static_cast<std::size_t>(scene->GetRequiredSceneSize()));
        std::uint64_t written{};
        require(scene->BuildScene(bytes.data(), bytes.size(), &written) == com::ok && written == bytes.size() &&
            transparent_layer_clear_contract(bytes, variant), "transparent layer Clear retained structure changed");
        const auto cold = render(scene.get(), variant);
        const auto warm = render(scene.get(), variant);
        require(cold == warm && transparent_layer_clear_pixels(cold, 64U, variant),
            "transparent layer Clear full bytes changed isolation, captured frame, mask or opacity");
    }
}

} // namespace progpu::native::direct2d::tests
