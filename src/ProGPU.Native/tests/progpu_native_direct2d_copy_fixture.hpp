#pragma once

#include "progpu_native_direct2d_compat.hpp"
#include "progpu_native_direct2d_brush_fixture.hpp"

#include <limits>
#include <array>
#include <vector>

namespace progpu::native::direct2d::tests {

inline bool export_copy_scene(compat::scene_render_target_native* source, std::vector<std::byte>& bytes)
{
    bytes.resize(static_cast<std::size_t>(source->GetRequiredSceneSize()));
    std::uint64_t written = 0U;
    return !bytes.empty() && source->BuildScene(bytes.data(), bytes.size(), &written) == com::ok &&
        written == bytes.size();
}

inline bool formatted_axis_scene_copy_contract(compat::factory* owner, compat::factory* foreign_owner)
{
    com::pointer<compat::formatted_scene_factory_native> factory, foreign_factory;
    if (owner->QueryInterface(compat::formatted_scene_factory_native_interface_id,
            reinterpret_cast<void**>(factory.put())) != com::ok ||
        foreign_owner->QueryInterface(compat::formatted_scene_factory_native_interface_id,
            reinterpret_cast<void**>(foreign_factory.put())) != com::ok) return false;
    for (const compat::size_f density : {compat::size_f{192, 144}, compat::size_f{144, 192}, compat::size_f{120, 168}}) {
        for (const compat::pixel_format format : {
                compat::pixel_format{28U, compat::alpha_mode::premultiplied},
                compat::pixel_format{87U, compat::alpha_mode::premultiplied},
                compat::pixel_format{28U, compat::alpha_mode::ignore},
                compat::pixel_format{87U, compat::alpha_mode::ignore},
                compat::pixel_format{65U, compat::alpha_mode::premultiplied}}) {
            const compat::scene_render_target_properties properties{16U, 12U, density.width, density.height, 7902U, 1U};
            com::pointer<compat::render_target> source, foreign;
            if (factory->CreateFormattedSceneRenderTarget(&properties, &format, source.put()) != com::ok ||
                foreign_factory->CreateFormattedSceneRenderTarget(&properties, &format, foreign.put()) != com::ok) return false;
            const compat::size_f logical{8, 12};
            const compat::size_u physical{16U, 12U};
            com::pointer<compat::bitmap_render_target> child, next;
            com::pointer<compat::bitmap> bitmap, next_bitmap;
            com::pointer<compat::scene_render_target_native> scene, next_scene;
            if (source->CreateCompatibleRenderTarget(&logical, &physical, &format,
                    compat::compatible_render_target_options::none, child.put()) != com::ok ||
                child->GetBitmap(bitmap.put()) != com::ok ||
                bitmap.as(compat::scene_render_target_native_interface_id, scene) != com::ok ||
                source->CreateCompatibleRenderTarget(&logical, &physical, &format,
                    compat::compatible_render_target_options::none, next.put()) != com::ok ||
                next->GetBitmap(next_bitmap.put()) != com::ok ||
                next_bitmap.as(compat::scene_render_target_native_interface_id, next_scene) != com::ok) return false;
            float dpi_x = 0, dpi_y = 0;
            bitmap->GetDpi(&dpi_x, &dpi_y);
            if (dpi_x != 192 || dpi_y != 96 || bitmap->GetSize().width != 8 || bitmap->GetSize().height != 12) return false;
            const compat::color_f red{1, 0, 0, 0.5F};
            source->BeginDraw();
            source->Clear(&red);
            foreign->BeginDraw();
            const compat::rectangle_u crop{2U, 3U, 10U, 9U};
            const compat::point_2u destination{2U, 3U};
            if (bitmap->CopyFromRenderTarget(&destination, source.get(), &crop) != com::ok) return false;
            std::vector<std::byte> captured, after;
            if (!export_copy_scene(scene.get(), captured)) return false;
            progpu_native_scene_header header{};
            progpu_native_scene_resource resource{};
            progpu_native_scene_command command{};
            progpu_native_scene_image_draw draw{};
            progpu_native_scene_picture_image picture{};
            progpu_native_scene_presentation axes{};
            const auto read_capture = [&](const std::vector<std::byte>& bytes) {
                return read_scene_value(bytes, 0U, header) && header.command_count == 3U && header.resource_count == 1U &&
                    read_scene_value(bytes, header.resource_offset, resource) &&
                    read_scene_value(bytes, header.command_offset + header.command_stride, command) &&
                    read_scene_value(bytes, command.payload_offset, draw) &&
                    read_scene_value(bytes, resource.payload_offset, picture) &&
                    resource.payload_size == sizeof(picture) + sizeof(axes) &&
                    read_scene_value(bytes, resource.payload_offset + sizeof(picture), axes) &&
                    picture.flags == PROGPU_NATIVE_SCENE_PICTURE_IMAGE_PRESENTATION &&
                    axes.struct_size == sizeof(axes) && axes.viewport_x == 0 && axes.viewport_y == 0 &&
                    axes.viewport_width == 16 && axes.viewport_height == 12 && axes.reserved == 0 &&
                    axes.dpi_scale_x == density.width / 96.0F && axes.dpi_scale_y == density.height / 96.0F;
            };
            if (!read_capture(captured) || draw.source_rect.x != 2 || draw.source_rect.y != 3 ||
                draw.source_rect.width != 8 || draw.source_rect.height != 6 ||
                draw.destination_rect.x != 1 || draw.destination_rect.y != 3 ||
                draw.destination_rect.width != 4 || draw.destination_rect.height != 6) return false;
            if (bitmap->CopyFromRenderTarget(nullptr, foreign.get(), nullptr) != compat::wrong_factory ||
                !export_copy_scene(scene.get(), after) || after != captured) return false;
            if (source->EndDraw(nullptr, nullptr) != com::ok ||
                bitmap->CopyFromRenderTarget(nullptr, source.get(), nullptr) != com::ok ||
                next_bitmap->CopyFromBitmap(nullptr, bitmap.get(), nullptr) != com::ok ||
                !export_copy_scene(next_scene.get(), after) || !read_capture(after)) return false;
            // Full-copy flattening retains the original source axes, not child DPI.
            if (draw.destination_rect.width != 8 || draw.destination_rect.height != 12) return false;
            captured = after;
            if (next_bitmap->CopyFromBitmap(nullptr, next_bitmap.get(), nullptr) != com::ok ||
                !export_copy_scene(next_scene.get(), after) || after != captured) return false;
            // Overlap snapshots old pixels, not an ownership cycle or live alias.
            const compat::point_2u overlap{4, 4};
            if (bitmap->CopyFromBitmap(&overlap, bitmap.get(), &crop) != com::ok) return false;
            source->BeginDraw();
            source->Clear(nullptr);
            if (source->EndDraw(nullptr, nullptr) != com::ok) return false;
            source.reset();
            if (!export_copy_scene(next_scene.get(), after) || after != captured) return false;
            // Mixed-axis history still fails until a full replacement establishes
            // a new epoch. Bitmap-view DPI remains its original creation metadata.
            child->SetDpi(96, 144);
            std::array<std::byte, 16U * 12U * 4U> upload{};
            const auto pitch = format.format == 65U ? 16U : 64U;
            if (bitmap->CopyFromMemory(&crop, upload.data(), pitch) != compat::not_implemented ||
                bitmap->CopyFromMemory(nullptr, upload.data(), pitch) != com::ok ||
                !export_copy_scene(scene.get(), after) ||
                !read_scene_value(after, 0U, header) ||
                !read_scene_value(after, header.command_offset + header.command_stride, command) ||
                !read_scene_value(after, command.payload_offset, draw) ||
                draw.destination_rect.width != 16 || draw.destination_rect.height != 8) return false;
            bitmap->GetDpi(&dpi_x, &dpi_y);
            if (dpi_x != 192 || dpi_y != 96) return false;
        }
    }
    return true;
}

// Shared by the portable factory and the actual Windows Factory1 provider.
// Inspect complete owned source bytes, not merely a successful copy HRESULT.
inline bool formatted_scene_copy_contract(compat::factory* owner, compat::factory* foreign_owner)
{
    com::pointer<compat::formatted_scene_factory_native> factory;
    com::pointer<compat::scene_factory_native> old_factory;
    if (owner->QueryInterface(compat::formatted_scene_factory_native_interface_id,
            reinterpret_cast<void**>(factory.put())) != com::ok ||
        owner->QueryInterface(compat::scene_factory_native_interface_id,
            reinterpret_cast<void**>(old_factory.put())) != com::ok) return false;
    com::pointer<com::unknown> identity;
    if (factory.as(com::unknown_interface_id(), identity) != com::ok ||
        identity.get() != static_cast<com::unknown*>(owner)) return false;
    const compat::scene_render_target_properties properties{16U, 12U, 192.0F, 192.0F, 7901U, 1U};
    for (const compat::pixel_format format : {
            compat::pixel_format{28U, compat::alpha_mode::premultiplied},
            compat::pixel_format{87U, compat::alpha_mode::premultiplied},
            compat::pixel_format{28U, compat::alpha_mode::ignore},
            compat::pixel_format{87U, compat::alpha_mode::ignore},
            compat::pixel_format{65U, compat::alpha_mode::premultiplied}}) {
        com::pointer<compat::render_target> source;
        if (factory->CreateFormattedSceneRenderTarget(&properties, &format, source.put()) != com::ok) return false;
        const auto actual_format = source->GetPixelFormat();
        com::pointer<compat::factory> actual_owner;
        source->GetFactory(actual_owner.put());
        com::pointer<compat::bitmap_render_target> forbidden_bitmap_target;
        if (actual_format.format != format.format || actual_format.alpha != format.alpha ||
            actual_owner.get() != owner ||
            source.as(compat::bitmap_render_target_interface_id, forbidden_bitmap_target) != com::no_interface ||
            forbidden_bitmap_target) return false;

        compat::render_target_properties support{};
        if (source->IsSupported(nullptr) != 0 || source->IsSupported(&support) == 0) return false;
        support.pixel_format_value = format;
        support.dpi_x = std::numeric_limits<float>::quiet_NaN();
        support.dpi_y = -1.0F; // IsSupported does not evaluate DPI.
        if (source->IsSupported(&support) == 0) return false;
        support.type = compat::render_target_type::hardware;
        if (source->IsSupported(&support) != 0) return false;
        support.type = compat::render_target_type::software;
        if (source->IsSupported(&support) != 0) return false;
        support.type = compat::render_target_type::default_value;
        support.usage = compat::render_target_usage::gdi_compatible;
        if (source->IsSupported(&support) != 0) return false;
        support.usage = compat::render_target_usage::force_bitmap_remoting;
        if (source->IsSupported(&support) != 0) return false;
        support.usage = compat::render_target_usage::none;
        support.minimum_level = compat::feature_level::level_9;
        if (source->IsSupported(&support) != 0) return false;
        support.minimum_level = compat::feature_level::default_value;
        support.pixel_format_value = {28U, compat::alpha_mode::straight};
        if (source->IsSupported(&support) != 0) return false;

        // The ordinary formatted target is a source, not a disguised compatible
        // target. Its child inherits format but owns independent 96-DPI pixels.
        const compat::size_f child_size{16.0F, 12.0F};
        const compat::size_u child_pixels{16U, 12U};
        com::pointer<compat::bitmap_render_target> child;
        com::pointer<compat::bitmap> bitmap;
        if (source->CreateCompatibleRenderTarget(&child_size, &child_pixels, &format,
                compat::compatible_render_target_options::none, child.put()) != com::ok ||
            child->GetBitmap(bitmap.put()) != com::ok) return false;
        com::pointer<compat::bitmap_render_target> inherited;
        if (source->CreateCompatibleRenderTarget(nullptr, nullptr, nullptr,
                compat::compatible_render_target_options::none, inherited.put()) != com::ok ||
            inherited->GetPixelFormat().format != format.format ||
            inherited->GetPixelFormat().alpha != compat::alpha_mode::premultiplied) return false;
        float dpi_x = 0.0F, dpi_y = 0.0F;
        child->GetDpi(&dpi_x, &dpi_y);
        if (dpi_x != 96.0F || dpi_y != 96.0F ||
            bitmap->CopyFromRenderTarget(nullptr, source.get(), nullptr) != compat::wrong_state) return false;
        com::pointer<compat::scene_render_target_native> source_scene, child_scene;
        if (source.as(compat::scene_render_target_native_interface_id, source_scene) != com::ok ||
            bitmap.as(compat::scene_render_target_native_interface_id, child_scene) != com::ok) return false;
        const compat::color_f red{1.0F, 0.0F, 0.0F, 0.5F};
        const compat::rectangle_f rectangle{1.0F, 2.0F, 5.0F, 4.0F};
        com::pointer<compat::solid_color_brush> brush;
        if (source->CreateSolidColorBrush(&red, nullptr, brush.put()) != com::ok) return false;
        source->BeginDraw();
        source->Clear(&red);
        source->FillRectangle(&rectangle, brush.get());
        const compat::rectangle_u crop{2U, 4U, 10U, 8U};
        const compat::point_2u destination{3U, 5U};
        if (bitmap->CopyFromRenderTarget(&destination, source.get(), &crop) != com::ok ||
            source_scene->GetRequiredSceneSize() != 0U) return false;
        std::vector<std::byte> first;
        if (!export_copy_scene(child_scene.get(), first)) return false;
        progpu_native_scene_header header{};
        progpu_native_scene_command command{};
        progpu_native_scene_resource resource{};
        progpu_native_scene_image_draw draw{};
        progpu_native_scene_picture_image picture{};
        if (!read_scene_value(first, 0U, header) || header.command_count != 3U || header.resource_count != 1U ||
            !read_scene_value(first, header.command_offset + header.command_stride, command) ||
            !read_scene_value(first, command.payload_offset, draw) ||
            !read_scene_value(first, header.resource_offset, resource) ||
            !read_scene_value(first, resource.payload_offset, picture) ||
            (resource.flags & PROGPU_NATIVE_SCENE_IMAGE_PICTURE) == 0U ||
            draw.source_rect.x != 2.0F || draw.source_rect.y != 4.0F ||
            draw.source_rect.width != 8.0F || draw.source_rect.height != 4.0F ||
            draw.destination_rect.x != 3.0F || draw.destination_rect.y != 5.0F ||
            draw.destination_rect.width != 8.0F || draw.destination_rect.height != 4.0F ||
            picture.width != 16U || picture.height != 12U || picture.dpi_scale != 2.0F ||
            picture.clear_color.a != (format.alpha == compat::alpha_mode::ignore ? 1.0F : 0.5F)) return false;

        // Capture retains no source COM reference. Completing/mutating the
        // same active source cannot change the already published child bytes.
        source->Clear(nullptr);
        if (source->EndDraw(nullptr, nullptr) != com::ok) return false;
        std::vector<std::byte> after;
        if (!export_copy_scene(child_scene.get(), after) || after != first) return false;
        if (bitmap->CopyFromRenderTarget(nullptr, source.get(), nullptr) != com::ok) return false;
        std::vector<std::byte> completed;
        if (!export_copy_scene(child_scene.get(), completed) || completed == first) return false;
        source->BeginDraw(); // Ordinary target starts a new independent frame.
        source->PushAxisAlignedClip(&rectangle, compat::antialias_mode::aliased);
        if (bitmap->CopyFromRenderTarget(nullptr, source.get(), nullptr) != compat::render_target_has_layer_or_cliprect)
            return false;
        source->PopAxisAlignedClip();
        source->FillRectangle(&rectangle, brush.get());
        source->SetDpi(96.0F, 96.0F);
        if (bitmap->CopyFromRenderTarget(nullptr, source.get(), nullptr) != compat::wrong_state) return false;
        source->Clear(nullptr); // Whole replacement establishes a new DPI epoch.
        if (bitmap->CopyFromRenderTarget(nullptr, source.get(), nullptr) != com::ok) return false;
        if (!export_copy_scene(child_scene.get(), completed)) return false;
        source->SetDpi(96.0F, 144.0F);
        if (bitmap->CopyFromRenderTarget(nullptr, source.get(), nullptr) != com::ok ||
            !export_copy_scene(child_scene.get(), completed)) return false;
        source->SetDpi(96.0F, 96.0F);
        const compat::color_f invalid{std::numeric_limits<float>::quiet_NaN(), 0, 0, 1};
        source->Clear(&invalid);
        if (bitmap->CopyFromRenderTarget(nullptr, source.get(), nullptr) != com::invalid_argument ||
            source->EndDraw(nullptr, nullptr) != com::invalid_argument ||
            !export_copy_scene(child_scene.get(), after) || after != completed) return false;

        com::pointer<compat::formatted_scene_factory_native> foreign_factory;
        com::pointer<compat::render_target> foreign_source, mismatched_source;
        const compat::pixel_format other_format{format.format == 87U ? 28U : 87U, format.alpha};
        if (foreign_owner == nullptr || foreign_owner == owner ||
            foreign_owner->QueryInterface(compat::formatted_scene_factory_native_interface_id,
                reinterpret_cast<void**>(foreign_factory.put())) != com::ok ||
            foreign_factory->CreateFormattedSceneRenderTarget(&properties, &format, foreign_source.put()) != com::ok ||
            factory->CreateFormattedSceneRenderTarget(&properties, &other_format, mismatched_source.put()) != com::ok)
            return false;
        foreign_source->BeginDraw();
        mismatched_source->BeginDraw();
        if (foreign_source->EndDraw(nullptr, nullptr) != com::ok ||
            mismatched_source->EndDraw(nullptr, nullptr) != com::ok ||
            bitmap->CopyFromRenderTarget(nullptr, foreign_source.get(), nullptr) != compat::wrong_factory ||
            bitmap->CopyFromRenderTarget(nullptr, mismatched_source.get(), nullptr) != com::invalid_argument ||
            !export_copy_scene(child_scene.get(), after) || after != completed) return false;
        if (format.format != 65U) {
            const compat::pixel_format other_alpha{format.format,
                format.alpha == compat::alpha_mode::ignore ? compat::alpha_mode::premultiplied : compat::alpha_mode::ignore};
            if (factory->CreateFormattedSceneRenderTarget(&properties, &other_alpha, mismatched_source.put()) != com::ok)
                return false;
            mismatched_source->BeginDraw();
            if (mismatched_source->EndDraw(nullptr, nullptr) != com::ok ||
                bitmap->CopyFromRenderTarget(nullptr, mismatched_source.get(), nullptr) != com::invalid_argument ||
                !export_copy_scene(child_scene.get(), after) || after != completed) return false;
        }

        // A source recording may itself draw this destination. The immutable
        // capture contains an older generation, never a target ownership cycle.
        source->BeginDraw();
        source->DrawBitmap(bitmap.get(), nullptr, 1.0F,
            compat::bitmap_interpolation_mode::nearest_neighbor, nullptr);
        if (source->EndDraw(nullptr, nullptr) != com::ok ||
            bitmap->CopyFromRenderTarget(nullptr, source.get(), nullptr) != com::ok) return false;
        if (!export_copy_scene(child_scene.get(), completed)) return false;
        source_scene.reset();
        inherited.reset();
        brush.reset();
        auto* retiring_source = source.detach();
        if (retiring_source->Release() != 0U ||
            !export_copy_scene(child_scene.get(), after) || after != completed) return false;

        com::pointer<compat::render_target> formatless;
        if (old_factory->CreateSceneRenderTarget(&properties, formatless.put()) != com::ok ||
            formatless->GetPixelFormat().format != 0U) return false;
        formatless->BeginDraw();
        if (formatless->EndDraw(nullptr, nullptr) != com::ok ||
            bitmap->CopyFromRenderTarget(nullptr, formatless.get(), nullptr) != compat::not_implemented ||
            !export_copy_scene(child_scene.get(), after) || after != completed) return false;
    }
    compat::render_target* invalid_output = reinterpret_cast<compat::render_target*>(std::uintptr_t{1U});
    const compat::pixel_format invalid_format{0U, compat::alpha_mode::premultiplied};
    if (factory->CreateFormattedSceneRenderTarget(&properties, &invalid_format, &invalid_output) != compat::not_implemented ||
        invalid_output != nullptr ||
        factory->CreateFormattedSceneRenderTarget(&properties, nullptr, &invalid_output) != com::invalid_argument ||
        invalid_output != nullptr ||
        factory->CreateFormattedSceneRenderTarget(&properties, &invalid_format, nullptr) != com::pointer_error) return false;
    const compat::pixel_format valid_format{87U, compat::alpha_mode::premultiplied};
    for (const compat::pixel_format rejected : {
            compat::pixel_format{87U, compat::alpha_mode::unknown},
            compat::pixel_format{87U, compat::alpha_mode::straight},
            compat::pixel_format{65U, compat::alpha_mode::ignore},
            compat::pixel_format{65U, compat::alpha_mode::straight},
            compat::pixel_format{999U, compat::alpha_mode::premultiplied}}) {
        if (factory->CreateFormattedSceneRenderTarget(&properties, &rejected, &invalid_output) != compat::not_implemented ||
            invalid_output != nullptr) return false;
    }
    auto invalid_properties = properties;
    invalid_properties.dpi_x = 0.0F;
    if (factory->CreateFormattedSceneRenderTarget(&invalid_properties, &valid_format, &invalid_output) != com::invalid_argument ||
        invalid_output != nullptr) return false;
    invalid_properties = properties;
    invalid_properties.pixel_width = 16385U;
    if (factory->CreateFormattedSceneRenderTarget(&invalid_properties, &valid_format, &invalid_output) != com::invalid_argument ||
        invalid_output != nullptr) return false;
    return formatted_axis_scene_copy_contract(owner, foreign_owner);
}

} // namespace progpu::native::direct2d::tests
