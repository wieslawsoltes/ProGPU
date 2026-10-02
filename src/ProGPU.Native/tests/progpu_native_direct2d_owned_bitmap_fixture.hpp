#pragma once

#include "progpu_native_direct2d_copy_fixture.hpp"

namespace progpu::native::direct2d::tests {

// Both real factory providers use this contract. Captures inspect the actual
// recorder's owned image resource; no CPU raster or public readback is invented.
inline bool owned_bitmap_scene_copy_contract(compat::factory* owner, compat::factory* foreign_owner)
{
    com::pointer<compat::formatted_scene_factory_native> factory, foreign_factory;
    if (owner->QueryInterface(compat::formatted_scene_factory_native_interface_id,
            reinterpret_cast<void**>(factory.put())) != com::ok ||
        foreign_owner->QueryInterface(compat::formatted_scene_factory_native_interface_id,
            reinterpret_cast<void**>(foreign_factory.put())) != com::ok) return false;
    for (const compat::pixel_format format : {
            compat::pixel_format{28U, compat::alpha_mode::premultiplied},
            compat::pixel_format{87U, compat::alpha_mode::premultiplied},
            compat::pixel_format{28U, compat::alpha_mode::ignore},
            compat::pixel_format{87U, compat::alpha_mode::ignore},
            compat::pixel_format{65U, compat::alpha_mode::premultiplied}}) {
        const compat::scene_render_target_properties properties{16U, 12U, 192, 144, 7951U, 1U};
        auto host_properties = properties;
        host_properties.dpi_x = host_properties.dpi_y = 96;
        host_properties.scene_id = 7952U;
        com::pointer<compat::render_target> source, host, foreign;
        com::pointer<compat::scene_render_target_native> host_scene;
        if (factory->CreateFormattedSceneRenderTarget(&properties, &format, source.put()) != com::ok ||
            factory->CreateFormattedSceneRenderTarget(&host_properties, &format, host.put()) != com::ok ||
            foreign_factory->CreateFormattedSceneRenderTarget(&properties, &format, foreign.put()) != com::ok ||
            host.as(compat::scene_render_target_native_interface_id, host_scene) != com::ok) return false;
        const auto pitch = format.format == 65U ? 16U : 64U;
        std::vector<std::byte> original(pitch * 12U, std::byte{0x20});
        if (format.format != 65U)
            for (std::size_t i = 3U; i < original.size(); i += 4U) original[i] = std::byte{0x80};
        const compat::bitmap_properties bitmap_properties{format, 120, 168};
        com::pointer<compat::bitmap> bitmap, alias, copied;
        if (host->CreateBitmap({16U, 12U}, original.data(), pitch, &bitmap_properties, bitmap.put()) != com::ok ||
            host->CreateBitmap({16U, 12U}, nullptr, 0U, &bitmap_properties, copied.put()) != com::ok ||
            host->CreateSharedBitmap(compat::bitmap_interface_id, bitmap.get(), &bitmap_properties, alias.put()) != com::ok)
            return false;
        const auto capture = [&](compat::bitmap* value, std::vector<std::byte>& content, bool picture) {
            host->BeginDraw();
            const compat::rectangle_f bounds{0, 0, 16, 12};
            host->DrawBitmap(value, &bounds, 1, compat::bitmap_interpolation_mode::nearest_neighbor, nullptr);
            if (host->EndDraw(nullptr, nullptr) != com::ok) return false;
            std::vector<std::byte> bytes;
            progpu_native_scene_header header{};
            progpu_native_scene_resource resource{};
            if (!export_copy_scene(host_scene.get(), bytes) || !read_scene_value(bytes, 0U, header) ||
                header.resource_count != 1U || !read_scene_value(bytes, header.resource_offset, resource) ||
                ((resource.flags & PROGPU_NATIVE_SCENE_IMAGE_PICTURE) != 0U) != picture) return false;
            const auto offset = picture ? resource.auxiliary_offset : resource.payload_offset;
            const auto count = picture ? resource.auxiliary_size : resource.payload_size;
            if (offset > bytes.size() || count > bytes.size() - offset) return false;
            content.assign(bytes.begin() + offset, bytes.begin() + offset + count);
            return true;
        };
        std::vector<std::byte> bytes, after;
        if (!capture(bitmap.get(), bytes, false) || bytes != original) return false;
        const compat::color_f red{1, 0, 0, 0.5F};
        const compat::rectangle_u crop{2U, 2U, 6U, 6U};
        const compat::point_2u destination{4U, 3U};
        source->BeginDraw();
        source->Clear(&red);
        foreign->BeginDraw();
        const compat::rectangle_u outside{0U, 0U, 17U, 12U};
        const compat::point_2u overflow{15U, 11U};
        if (bitmap->CopyFromRenderTarget(&destination, foreign.get(), &crop) != compat::wrong_factory ||
            bitmap->CopyFromRenderTarget(nullptr, nullptr, nullptr) != com::invalid_argument ||
            bitmap->CopyFromRenderTarget(nullptr, source.get(), &outside) != com::invalid_argument ||
            bitmap->CopyFromRenderTarget(&overflow, source.get(), &crop) != com::invalid_argument ||
            !capture(bitmap.get(), after, false) || after != bytes ||
            bitmap->CopyFromRenderTarget(&destination, source.get(), &crop) != com::ok ||
            !capture(alias.get(), bytes, true)) return false;
        progpu_native_scene_header header{};
        progpu_native_scene_resource upload{};
        progpu_native_scene_command command{};
        progpu_native_scene_image_draw draw{};
        if (!read_scene_value(bytes, 0U, header) || header.command_count != 6U || header.resource_count != 2U ||
            !read_scene_value(bytes, header.resource_offset, upload) || upload.payload_size != original.size() ||
            upload.payload_offset > bytes.size() || upload.payload_size > bytes.size() - upload.payload_offset ||
            std::memcmp(bytes.data() + upload.payload_offset, original.data(), original.size()) != 0 ||
            !read_scene_value(bytes, header.command_offset + header.command_stride, command) ||
            !read_scene_value(bytes, command.payload_offset, draw) ||
            (draw.flags & PROGPU_NATIVE_SCENE_IMAGE_SOURCE_ALPHA_IGNORE) != 0U ||
            !read_scene_value(bytes, header.command_offset + header.command_stride * 4U, command) ||
            !read_scene_value(bytes, command.payload_offset, draw) || draw.source_rect.x != 2 || draw.source_rect.y != 2 ||
            draw.destination_rect.x != 4 || draw.destination_rect.y != 3 ||
            draw.destination_rect.width != 4 || draw.destination_rect.height != 4) return false;
        float dpi_x{}, dpi_y{};
        bitmap->GetDpi(&dpi_x, &dpi_y);
        com::pointer<compat::wic_bitmap_lock> forbidden_lock;
        com::pointer<compat::scene_render_target_native> forbidden_scene;
        if (dpi_x != 120 || dpi_y != 168 ||
            bitmap.as(compat::wic_bitmap_lock_interface_id, forbidden_lock) != com::no_interface ||
            bitmap.as(compat::scene_render_target_native_interface_id, forbidden_scene) != com::no_interface) return false;
        const compat::rectangle_f clip{0, 0, 2, 2};
        source->PushAxisAlignedClip(&clip, compat::antialias_mode::aliased);
        if (bitmap->CopyFromRenderTarget(nullptr, source.get(), nullptr) != compat::render_target_has_layer_or_cliprect ||
            !capture(bitmap.get(), after, true) || after != bytes) return false;
        source->PopAxisAlignedClip();
        source->Clear(nullptr);
        if (source->EndDraw(nullptr, nullptr) != com::ok ||
            !capture(bitmap.get(), after, true) || after != bytes) return false;
        auto* retiring = source.detach();
        if (retiring->Release() != 0U || !capture(bitmap.get(), after, true) || after != bytes) return false;
        // An alias must see writes to the same owned storage; identity self-copy
        // is a no-op, and overlapping self-copy captures the old generation first.
        if (bitmap->CopyFromBitmap(nullptr, alias.get(), nullptr) != com::ok ||
            !capture(bitmap.get(), after, true) || after != bytes ||
            bitmap->CopyFromBitmap(&destination, alias.get(), &crop) != com::ok ||
            copied->CopyFromBitmap(nullptr, bitmap.get(), nullptr) != com::ok ||
            !capture(copied.get(), after, true)) return false;
        const auto full_copy_size = after.size();
        const auto copy_source = [](const std::vector<std::byte>& scene, std::vector<std::byte>& inner) {
            progpu_native_scene_header header{};
            progpu_native_scene_resource resource{};
            if (!read_scene_value(scene, 0U, header) || header.command_count != 3U || header.resource_count != 1U ||
                !read_scene_value(scene, header.resource_offset, resource) ||
                (resource.flags & PROGPU_NATIVE_SCENE_IMAGE_PICTURE) == 0U ||
                resource.auxiliary_offset > scene.size() || resource.auxiliary_size > scene.size() - resource.auxiliary_offset)
                return false;
            inner.assign(scene.begin() + resource.auxiliary_offset,
                scene.begin() + resource.auxiliary_offset + resource.auxiliary_size);
            return true;
        };
        std::vector<std::byte> original_copy_source, next_copy_source;
        if (!copy_source(after, original_copy_source)) return false;
        for (unsigned transfer = 0U; transfer < 32U; ++transfer) {
            if (bitmap->CopyFromBitmap(nullptr, copied.get(), nullptr) != com::ok ||
                copied->CopyFromBitmap(nullptr, bitmap.get(), nullptr) != com::ok ||
                !capture(copied.get(), after, true) || after.size() != full_copy_size ||
                !copy_source(after, next_copy_source) || next_copy_source != original_copy_source) return false;
        }
        const auto immutable = after;
        if (alias->CopyFromMemory(&crop, original.data(), pitch) != com::ok ||
            !capture(copied.get(), after, true) || after != immutable ||
            alias->CopyFromMemory(nullptr, original.data(), pitch) != com::ok ||
            !capture(bitmap.get(), after, false) || after != original ||
            copied->CopyFromBitmap(nullptr, bitmap.get(), nullptr) != com::ok ||
            !capture(copied.get(), after, false) || after != original) return false;
    }
    return true;
}

} // namespace progpu::native::direct2d::tests
