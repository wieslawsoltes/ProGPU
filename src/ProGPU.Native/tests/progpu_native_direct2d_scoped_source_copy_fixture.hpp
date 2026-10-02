#pragma once

#include "progpu_native_direct2d_scoped_copy_fixture.hpp"

namespace progpu::native::direct2d::tests {

inline bool scoped_source_copy_contract(compat::factory* owner) {
    namespace d2d = compat;
    com::pointer<d2d::scene_factory_native> factory;
    if (owner->QueryInterface(d2d::scene_factory_native_interface_id,
        reinterpret_cast<void**>(factory.put())) != com::ok) return false;
    const d2d::scene_render_target_properties properties{32U, 32U, 96, 96, 0x95B4U, 1U};
    const d2d::size_u size{16U, 16U};
    const d2d::rectangle_f clip{2, 2, 10, 10};
    const d2d::color_f red{1, 0, 0, 1};
    const d2d::pixel_format format{87U, d2d::alpha_mode::premultiplied};
    for (unsigned int api = 0U; api < 2U; ++api) for (unsigned int kind = 0U; kind < 6U; ++kind) {
        com::pointer<d2d::render_target> parent;
        com::pointer<d2d::bitmap_render_target> target, source;
        com::pointer<d2d::bitmap> target_bitmap, source_bitmap;
        com::pointer<d2d::scene_render_target_native> scene;
        if (factory->CreateSceneRenderTarget(&properties, parent.put()) != com::ok ||
            parent->CreateCompatibleRenderTarget(nullptr, &size, &format,
                d2d::compatible_render_target_options::none, target.put()) != com::ok ||
            parent->CreateCompatibleRenderTarget(nullptr, &size, &format,
                d2d::compatible_render_target_options::none, source.put()) != com::ok ||
            target->GetBitmap(target_bitmap.put()) != com::ok || source->GetBitmap(source_bitmap.put()) != com::ok ||
            target.as(d2d::scene_render_target_native_interface_id, scene) != com::ok) return false;
        source->BeginDraw(); source->Clear(&red);
        if (kind == 0U) source->PushAxisAlignedClip(&clip, d2d::antialias_mode::aliased);
        if (kind == 4U) source->PopAxisAlignedClip(); // Preserve original failed-source state.
        if (kind != 0U && kind != 4U && source->EndDraw(nullptr, nullptr) != com::ok) return false;
        target->BeginDraw(); target->Clear(&red);
        if (kind == 2U) {
            const d2d::layer_parameters layer{clip, nullptr, d2d::antialias_mode::aliased,
                {1, 0, 0, 1, 0, 0}, 0.5F, nullptr, d2d::layer_options::none};
            target->PushLayer(&layer, nullptr);
        } else target->PushAxisAlignedClip(&clip,
            kind == 1U ? d2d::antialias_mode::per_primitive : d2d::antialias_mode::aliased);
        d2d::scene_render_target_summary before{}, after{};
        scene->GetSummary(&before);
        const d2d::rectangle_u invalid{0, 0, 17, 16};
        const auto* crop = kind == 5U ? &invalid : nullptr;
        const auto result = api == 0U
            ? target_bitmap->CopyFromBitmap(nullptr, kind == 3U ? target_bitmap.get() : source_bitmap.get(), crop)
            : target_bitmap->CopyFromRenderTarget(nullptr, kind == 3U ? target.get() : source.get(), crop);
        const auto expected = kind == 0U || kind == 3U ? d2d::render_target_has_layer_or_cliprect
            : kind == 5U ? com::invalid_argument : d2d::wrong_state;
        scene->GetSummary(&after);
        if (result != expected || before.generation != after.generation || before.draw_count != after.draw_count)
            return false;
        if (kind == 2U) target->PopLayer(); else target->PopAxisAlignedClip();
        if (target->EndDraw(nullptr, nullptr) != com::ok) return false;
        std::vector<std::byte> bytes;
        if (!export_copy_scene(scene.get(), bytes)) return false;
        if (kind == 0U) {
            source->PopAxisAlignedClip();
            if (source->EndDraw(nullptr, nullptr) != com::ok) return false;
        }
    }
    return true;
}

// The same public-vtable sequence runs on genuine Windows Direct2D. The source
// is independent of the clipped destination, with nonuniform source/view DPI.
// Source mutation and all COM releases precede deferred parent replay.
template<class Require>
void record_scoped_source_copy(compat::render_target* parent, std::uint32_t variant,
    bool from_target, Require require) {
    namespace d2d = compat;
    record_scoped_bitmap_copy(parent, variant,
        [&](d2d::render_target* child, d2d::bitmap* destination, const d2d::rectangle_u* rectangle,
            const auto& upload, std::uint32_t pitch) {
            const auto extent = rectangle == nullptr ? 32U : 4U;
            const d2d::size_u size{extent + 2U, extent + 4U};
            const d2d::rectangle_u crop{1U, 2U, extent + 1U, extent + 2U};
            const d2d::point_2u point = rectangle == nullptr ? d2d::point_2u{} :
                d2d::point_2u{rectangle->left, rectangle->top};
            const d2d::pixel_format format{87U, d2d::alpha_mode::premultiplied};
            if (!from_target) {
                const auto source_pitch = size.width * 4U + 4U;
                std::vector<std::uint8_t> source_bytes(source_pitch * size.height, 0U);
                for (std::uint32_t y = 0U; y < extent; ++y)
                    std::memcpy(source_bytes.data() + (y + 2U) * source_pitch + 4U,
                        upload.data() + y * pitch, extent * 4U);
                const d2d::bitmap_properties properties{format, 192.0F, 96.0F};
                com::pointer<d2d::bitmap> source;
                require(child->CreateBitmap(size, source_bytes.data(), source_pitch, &properties, source.put()) == com::ok,
                    "scoped source upload creation");
                const auto result = destination->CopyFromBitmap(&point, source.get(), &crop);
                std::fill(source_bytes.begin(), source_bytes.end(), 255U);
                require(source->CopyFromMemory(nullptr, source_bytes.data(), source_pitch) == com::ok,
                    "scoped source upload mutation");
                return result;
            }
            const d2d::size_f logical{static_cast<float>(size.width) / 2.0F, static_cast<float>(size.height)};
            com::pointer<d2d::bitmap_render_target> source;
            require(child->CreateCompatibleRenderTarget(&logical, &size, &format,
                d2d::compatible_render_target_options::none, source.put()) == com::ok,
                "scoped source retained target creation");
            const d2d::color_f transparent{0, 0, 0, 0}, blue{0, 0, 1, 1}, white{1, 1, 1, 1};
            com::pointer<d2d::solid_color_brush> brush;
            require(source->CreateSolidColorBrush(&blue, nullptr, brush.put()) == com::ok,
                "scoped source retained brush creation");
            source->BeginDraw(); source->Clear(&transparent);
            source->SetAntialiasMode(d2d::antialias_mode::aliased);
            // Actual source DIP geometry leaves one transparent physical pixel
            // at (2,3), becoming (1,1) after the original integer source crop.
            const std::array<d2d::rectangle_f, 4U> fills{{
                {0.5F, 2, (extent + 1U) / 2.0F, 3},
                {0.5F, 3, 1, 4}, {1.5F, 3, (extent + 1U) / 2.0F, 4},
                {0.5F, 4, (extent + 1U) / 2.0F, static_cast<float>(extent + 2U)}}};
            for (const auto& fill : fills) source->FillRectangle(&fill, brush.get());
            require(source->EndDraw(nullptr, nullptr) == com::ok, "scoped source retained drawing");
            const auto result = destination->CopyFromRenderTarget(&point, source.get(), &crop);
            source->BeginDraw(); source->Clear(&white);
            require(source->EndDraw(nullptr, nullptr) == com::ok, "scoped source retained mutation");
            return result;
        }, require);
}

template<class Render, class Require>
void verify_scoped_source_copy_pixels(Render render, Require require) {
    com::pointer<compat::factory> owner;
    com::pointer<compat::scene_factory_native> factory;
    require(compat::create_factory(owner.put()) == com::ok &&
        owner.as(compat::scene_factory_native_interface_id, factory) == com::ok,
        "scoped source copy factory");
    for (std::uint32_t source_kind = 0U; source_kind < 2U; ++source_kind)
        for (std::uint32_t variant = 0U; variant < 4U; ++variant) {
            const compat::scene_render_target_properties properties{64U, 64U, 96, 96, 0x95B3U,
                source_kind * 4U + variant + 1U};
            com::pointer<compat::render_target> target;
            com::pointer<compat::scene_render_target_native> scene;
            require(factory->CreateSceneRenderTarget(&properties, target.put()) == com::ok &&
                target.as(compat::scene_render_target_native_interface_id, scene) == com::ok,
                "scoped source copy parent");
            record_scoped_source_copy(target.get(), variant, source_kind != 0U, require);
            std::vector<std::byte> stream;
            progpu_native_scene_header header{};
            require(export_copy_scene(scene.get(), stream) && read_scene_value(stream, 0U, header) &&
                header.command_count == 1U, "scoped source copy export");
            target.reset(); scene.reset();
            std::array<std::vector<std::uint8_t>, 3U> pixels;
            for (std::uint32_t replay = 0U; replay < 3U; ++replay)
                pixels[replay] = render(replay == 2U, stream, header.generation,
                    replay == 1U ? 1U : source_kind + 2U);
            require(pixels[0].size() == 64U * 64U * 4U && pixels[0] == pixels[1] && pixels[0] == pixels[2],
                "scoped source copy cold/warm/independent pixels");
            for (std::uint32_t y = 0U; y < 64U; ++y) for (std::uint32_t x = 0U; x < 64U; ++x) {
                const auto expected = scoped_copy_expected_pixel(variant, x, y);
                require(std::equal(expected.begin(), expected.end(), pixels[0].data() + (y * 64U + x) * 4U),
                    "scoped source copy original physical pixels/clip restoration");
            }
        }
}
} // namespace progpu::native::direct2d::tests
