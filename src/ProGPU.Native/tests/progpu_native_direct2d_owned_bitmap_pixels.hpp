#pragma once

#include "progpu_native_direct2d_owned_bitmap_fixture.hpp"

#include <cmath>

namespace progpu::native::direct2d::tests {

// The same immutable copies and independent all-pixel oracle run through both
// native providers. Render returns tightly packed RGBA8, including alpha.
template<class Render, class Require>
void verify_owned_bitmap_scene_copy_pixels(compat::factory* owner, Render render, Require require)
{
    namespace d2d = compat;
    com::pointer<d2d::formatted_scene_factory_native> factory;
    require(owner->QueryInterface(d2d::formatted_scene_factory_native_interface_id,
        reinterpret_cast<void**>(factory.put())) == com::ok, "owned bitmap formatted factory");
    const d2d::scene_render_target_properties host_properties{64U, 64U, 96, 96, 9351U, 1U};
    const d2d::pixel_format host_format{28U, d2d::alpha_mode::premultiplied};
    com::pointer<d2d::render_target> host;
    com::pointer<d2d::scene_render_target_native> host_scene;
    require(factory->CreateFormattedSceneRenderTarget(&host_properties, &host_format, host.put()) == com::ok &&
        host.as(d2d::scene_render_target_native_interface_id, host_scene) == com::ok, "owned bitmap host creation");
    std::uint64_t source_id = 9352U;
    for (const d2d::pixel_format format : {
            d2d::pixel_format{28U, d2d::alpha_mode::premultiplied},
            d2d::pixel_format{87U, d2d::alpha_mode::premultiplied},
            d2d::pixel_format{28U, d2d::alpha_mode::ignore},
            d2d::pixel_format{87U, d2d::alpha_mode::ignore},
            d2d::pixel_format{65U, d2d::alpha_mode::premultiplied}}) {
        const bool alpha_only = format.format == 65U;
        const bool opaque_source = format.alpha == d2d::alpha_mode::ignore;
        const d2d::scene_render_target_properties properties{16U, 16U, 192, 144, source_id++, 1U};
        com::pointer<d2d::render_target> source;
        require(factory->CreateFormattedSceneRenderTarget(&properties, &format, source.put()) == com::ok,
            "owned bitmap source creation");
        const d2d::color_f red{1, 0, 0, 0.5F}, blue{0, 0, 1, 1}, green{0, 1, 0, 1};
        com::pointer<d2d::solid_color_brush> brush;
        require(source->CreateSolidColorBrush(&blue, nullptr, brush.put()) == com::ok, "owned bitmap source brush");
        source->BeginDraw();
        source->Clear(&red);
        source->SetAntialiasMode(d2d::antialias_mode::aliased);
        const d2d::rectangle_f patch{2, 2, 4, 4};
        source->FillRectangle(&patch, brush.get());
        std::vector<std::uint8_t> original(16U * 16U * (alpha_only ? 1U : 4U), 64U);
        if (!alpha_only) {
            for (std::size_t i = 0U; i < original.size(); i += 4U) {
                original[i] = original[i + 2U] = 0U;
                original[i + 1U] = 32U;
            }
            if (opaque_source) {
                for (std::size_t y = 0U; y < 16U; ++y) {
                    original[y * 64U + 1U] = 96U;
                    original[y * 64U + 3U] = 0U;
                }
            }
        }
        const d2d::bitmap_properties properties_bitmap{format, 192, 96};
        com::pointer<d2d::bitmap> bitmap, alias;
        require(host->CreateBitmap({16U, 16U}, original.data(), alpha_only ? 16U : 64U,
            &properties_bitmap, bitmap.put()) == com::ok, "owned bitmap upload creation");
        const d2d::rectangle_u crop{2U, 1U, 10U, 9U};
        const d2d::point_2u destination{3U, 4U};
        require(bitmap->CopyFromRenderTarget(&destination, source.get(), &crop) == com::ok,
            "owned bitmap cropped scene promotion");
        // Reinterpretation is a storage view, not conversion. Untouched upload
        // alpha=64 must survive even when the original view specified IGNORE.
        const d2d::bitmap_properties alias_properties{{format.format, d2d::alpha_mode::premultiplied}, 96, 192};
        require(host->CreateSharedBitmap(d2d::bitmap_interface_id, bitmap.get(),
            &alias_properties, alias.put()) == com::ok, "owned bitmap alpha/DPI alias");
        source->Clear(&green);
        require(source->EndDraw(nullptr, nullptr) == com::ok, "owned bitmap source mutation");
        brush.reset();
        source.reset();
        const d2d::rectangle_f bounds{0, 0, 16, 16}, alias_bounds{24, 0, 40, 16};
        const auto draw_views = [&] {
            host->BeginDraw();
            host->Clear(nullptr);
            host->DrawBitmap(bitmap.get(), &bounds, 1, d2d::bitmap_interpolation_mode::nearest_neighbor, nullptr);
            host->DrawBitmap(alias.get(), &alias_bounds, 1, d2d::bitmap_interpolation_mode::nearest_neighbor, nullptr);
            require(host->EndDraw(nullptr, nullptr) == com::ok, "owned bitmap parent draw");
        };
        draw_views();
        // The two views share one retained image. Equal paint merges to two
        // patches; different alpha policy and A8's color matrix stay separate.
        const bool separate_commands = opaque_source || alpha_only;
        const std::uint32_t commands = separate_commands ? 2U : 1U;
        std::vector<std::byte> bytes;
        progpu_native_scene_header header{};
        progpu_native_scene_command command{};
        progpu_native_scene_image_draw image{};
        require(export_copy_scene(host_scene.get(), bytes) && read_scene_value(bytes, 0U, header) &&
            header.resource_count == 1U && header.command_count == commands &&
            read_scene_value(bytes, header.command_offset, command) &&
            command.kind == PROGPU_NATIVE_SCENE_COMMAND_DRAW_IMAGE &&
            read_scene_value(bytes, command.payload_offset, image), "owned bitmap shared image structure");
        if (!separate_commands) {
            progpu_native_scene_image_patch_batch batch{};
            require((image.flags & PROGPU_NATIVE_SCENE_IMAGE_PATCH_BATCH) != 0U &&
                read_scene_value(bytes, command.payload_offset + sizeof(image), batch) &&
                batch.patch_count == 2U, "owned bitmap retained two-patch view batch");
        }
        // The source picture and owned storage each submit once on first use;
        // the warm replay submits only the parent scene.
        const auto cold = render(host_scene.get(), commands, commands, 3U);
        const auto warm = render(host_scene.get(), commands, commands, 1U);
        require(cold == warm && cold.size() == 64U * 64U * 4U, "owned bitmap warm replay mutated pixels");
        for (std::uint32_t y = 0U; y < 64U; ++y) {
            for (std::uint32_t x = 0U; x < 64U; ++x) {
                std::array<int, 4U> expected{};
                const bool second = x >= 24U && x < 40U;
                if (y < 16U && (x < 16U || second)) {
                    const auto local_x = second ? x - 24U : x;
                    const bool copied = local_x >= 3U && local_x < 11U && y >= 4U && y < 12U;
                    const bool blue_pixel = local_x >= 5U && local_x < 9U && y >= 6U && y < 9U;
                    const bool ignore_view = !second && opaque_source;
                    expected = copied
                        ? blue_pixel ? std::array<int, 4U>{0, 0, alpha_only ? 0 : 255, 255}
                            : std::array<int, 4U>{alpha_only ? 0 : opaque_source ? 255 : 128, 0, 0, opaque_source ? 255 : 128}
                        : std::array<int, 4U>{0, alpha_only ? 0 : 32, 0, ignore_view ? 255 : 64};
                    if (opaque_source && local_x == 0U)
                        expected = {0, 96, 0, ignore_view ? 255 : 0};
                }
                const auto* actual = cold.data() + (y * 64U + x) * 4U;
                for (std::size_t channel = 0U; channel < 4U; ++channel)
                    require(std::abs(static_cast<int>(actual[channel]) - expected[channel]) <= 1,
                        "owned bitmap crop, original alpha, view DPI or lifetime differs");
            }
        }
        // A compatible source's full replacement can unwrap its upload. Unlike
        // an owned bitmap's raw storage, that recorder has already requested
        // IGNORE conversion; copying its resource must retain that operation.
        const d2d::size_u size{16U, 16U};
        com::pointer<d2d::bitmap_render_target> compatible;
        com::pointer<d2d::bitmap> compatible_bitmap;
        require(host->CreateCompatibleRenderTarget(nullptr, &size, &format,
            d2d::compatible_render_target_options::none, compatible.put()) == com::ok &&
            compatible->GetBitmap(compatible_bitmap.put()) == com::ok &&
            compatible_bitmap->CopyFromMemory(nullptr, original.data(), alpha_only ? 16U : 64U) == com::ok &&
            bitmap->CopyFromBitmap(nullptr, compatible_bitmap.get(), nullptr) == com::ok,
            "owned bitmap unwrapped compatible-source copy");
        compatible_bitmap.reset();
        compatible.reset();
        draw_views();
        const auto replaced = render(host_scene.get(), commands, commands, 2U);
        const auto replaced_warm = render(host_scene.get(), commands, commands, 1U);
        require(replaced == replaced_warm && replaced.size() == 64U * 64U * 4U,
            "owned bitmap replaced-source warm replay");
        for (unsigned y = 0U; y < 64U; ++y) {
            for (unsigned x = 0U; x < 64U; ++x) {
                const bool second = x >= 24U && x < 40U;
                const bool inside = y < 16U && (x < 16U || second);
                const auto local_x = second ? x - 24U : x;
                const auto green_channel = inside && !alpha_only ? opaque_source && local_x == 0U ? 96U : 32U : 0U;
                const auto alpha = inside ? opaque_source ? 255U : 64U : 0U;
                const auto* pixel = replaced.data() + (y * 64U + x) * 4U;
                require(pixel[0] == 0U && pixel[1] == green_channel && pixel[2] == 0U && pixel[3] == alpha,
                    "owned bitmap dropped compatible source alpha conversion");
            }
        }
    }
}

} // namespace progpu::native::direct2d::tests
