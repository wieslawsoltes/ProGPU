#pragma once

#include "progpu_native_direct2d_copy_fixture.hpp"

#include <algorithm>
#include <array>
#include <cstdio>
#include <limits>

namespace progpu::native::direct2d::tests {

struct bitmap_destination_case {
    float source_x, source_y, target_x, target_y;
};

inline bitmap_destination_case bitmap_destination_policy(std::uint32_t variant) {
    return {(variant & 1U) != 0U ? 2.0F : 1.0F, (variant & 2U) != 0U ? 2.0F : 1.0F,
        (variant & 4U) != 0U ? 2.0F : 1.0F, (variant & 4U) != 0U ? 1.0F : 2.0F};
}

// Genuine public Direct2D calls, reused without implementation inspection by
// the independent Windows factory/WIC oracle and the portable recorder.
template<class Require>
void record_bitmap_destinations(compat::render_target* parent, std::uint32_t variant, Require require) {
    namespace d2d = compat;
    require(variant < 8U, "bitmap destination variant");
    const auto policy = bitmap_destination_policy(variant);
    const d2d::size_u size{32U, 32U};
    const d2d::size_f logical{32.0F / policy.target_x, 32.0F / policy.target_y};
    const d2d::pixel_format format{87U, d2d::alpha_mode::premultiplied};
    com::pointer<d2d::bitmap_render_target> child;
    com::pointer<d2d::bitmap> bitmap, child_bitmap;
    require(parent->CreateCompatibleRenderTarget(&logical, &size, &format,
        d2d::compatible_render_target_options::none, child.put()) == com::ok &&
        child->GetBitmap(child_bitmap.put()) == com::ok, "bitmap destination target");
    std::array<std::uint8_t, 8U * 8U * 4U> pixels{};
    for (std::size_t i = 0; i < pixels.size(); i += 4U) pixels[i] = pixels[i + 3U] = 255U;
    const d2d::bitmap_properties properties{format, 96.0F * policy.source_x, 96.0F * policy.source_y};
    require(child->CreateBitmap({8U, 8U}, pixels.data(), 32U, &properties, bitmap.put()) == com::ok,
        "bitmap destination source");
    const d2d::color_f red{1, 0, 0, 1}, green{0, 1, 0, 1}, black{0, 0, 0, 1};
    com::pointer<d2d::solid_color_brush> brush;
    require(child->CreateSolidColorBrush(&green, nullptr, brush.put()) == com::ok, "bitmap destination brush");
    const d2d::matrix_3x2_f identity{1, 0, 0, 1, 0, 0};
    const d2d::matrix_3x2_f translated{1, 0, 0, 1, 4.0F / policy.target_x, 6.0F / policy.target_y};
    const d2d::rectangle_f source{2.0F / policy.source_x, 2.0F / policy.source_y,
        6.0F / policy.source_x, 6.0F / policy.source_y};
    const float drawn_width = 4.0F * policy.target_x / policy.source_x;
    const d2d::rectangle_f clip{4.0F / policy.target_x, 6.0F / policy.target_y,
        (4.0F + drawn_width - 1.0F) / policy.target_x, 24.0F / policy.target_y};
    const auto sampling = (variant & 4U) != 0U ? d2d::bitmap_interpolation_mode::linear
        : d2d::bitmap_interpolation_mode::nearest_neighbor;
    child->BeginDraw(); child->Clear(&red);
    child->SetAntialiasMode(d2d::antialias_mode::aliased);
    child->PushAxisAlignedClip(&clip, d2d::antialias_mode::aliased);
    child->SetTransform(&translated);
    child->DrawBitmap(bitmap.get(), nullptr, 1, sampling, &source);
    child->PopAxisAlignedClip();
    const d2d::matrix_3x2_f uncut{1, 0, 0, 1, 16.0F / policy.target_x, 6.0F / policy.target_y};
    child->SetTransform(&uncut);
    child->DrawBitmap(bitmap.get(), nullptr, 1, sampling, &source);
    child->SetTransform(&identity);
    const std::array<d2d::rectangle_f, 3U> inverted{{{12, 0, 1, 12}, {0, 12, 12, 1}, {12, 12, 1, 1}}};
    for (const auto& destination : inverted) child->DrawBitmap(bitmap.get(), &destination, 1, sampling, &source);
    const d2d::rectangle_f following{20.0F / policy.target_x, 20.0F / policy.target_y,
        24.0F / policy.target_x, 24.0F / policy.target_y};
    child->FillRectangle(&following, brush.get());
    require(child->EndDraw(nullptr, nullptr) == com::ok, "inverted destination poisoned following draw");
    parent->BeginDraw(); parent->Clear(&black); parent->SetTransform(&identity);
    const d2d::rectangle_f output{4, 4, 36, 36};
    parent->DrawBitmap(child_bitmap.get(), &output, 1, d2d::bitmap_interpolation_mode::nearest_neighbor, nullptr);
    require(parent->EndDraw(nullptr, nullptr) == com::ok, "bitmap destination final draw");
}

inline std::array<std::uint8_t, 4U> bitmap_destination_expected(std::uint32_t variant,
    std::uint32_t x, std::uint32_t y) {
    if (x < 4U || y < 4U || x >= 36U || y >= 36U) return {0, 0, 0, 255};
    x -= 4U; y -= 4U;
    if (x >= 20U && y >= 20U && x < 24U && y < 24U) return {0, 255, 0, 255};
    const auto p = bitmap_destination_policy(variant);
    const auto w = static_cast<std::uint32_t>(4.0F * p.target_x / p.source_x) - 1U;
    const auto h = static_cast<std::uint32_t>(4.0F * p.target_y / p.source_y);
    if (x >= 4U && x < 4U + w && y >= 6U && y < 6U + h) return {0, 0, 255, 255};
    if (x >= 16U && x < 17U + w && y >= 6U && y < 6U + h) return {0, 0, 255, 255};
    return {255, 0, 0, 255};
}

template<class Render, class Require>
void verify_bitmap_destination_pixels(Render render, Require require) {
    com::pointer<compat::factory> owner;
    com::pointer<compat::scene_factory_native> factory;
    require(compat::create_factory(owner.put()) == com::ok &&
        owner.as(compat::scene_factory_native_interface_id, factory) == com::ok, "bitmap destination factory");
    for (std::uint32_t variant = 0; variant < 8U; ++variant) {
        const compat::scene_render_target_properties properties{64U, 64U, 96, 96, 0x95C3U, variant + 1U};
        com::pointer<compat::render_target> target;
        com::pointer<compat::scene_render_target_native> scene;
        require(factory->CreateSceneRenderTarget(&properties, target.put()) == com::ok &&
            target.as(compat::scene_render_target_native_interface_id, scene) == com::ok, "bitmap destination parent");
        record_bitmap_destinations(target.get(), variant, require);
        std::vector<std::byte> stream;
        progpu_native_scene_header header{};
        require(export_copy_scene(scene.get(), stream) && read_scene_value(stream, 0U, header) &&
            header.command_count == 1U, "bitmap destination export");
        scene.reset(); target.reset();
        std::array<std::vector<std::uint8_t>, 3U> pixels;
        for (std::uint32_t replay = 0U; replay < 3U; ++replay)
            pixels[replay] = render(replay == 2U, stream, header.generation, replay == 1U ? 1U : 2U);
        require(pixels[0].size() == 64U * 64U * 4U && pixels[0] == pixels[1] && pixels[0] == pixels[2],
            "bitmap destination cold/warm/independent replay");
        for (std::uint32_t y = 0; y < 64U; ++y) for (std::uint32_t x = 0; x < 64U; ++x) {
            const auto expected = bitmap_destination_expected(variant, x, y);
            require(std::equal(expected.begin(), expected.end(), pixels[0].data() + (y * 64U + x) * 4U),
                "bitmap destination DIP extent, no-op, clip or following draw");
        }
    }
}

// Failures are compared with the same provider's ordered-destination call:
// retain pre-existing status/tag precedence rather than assuming that a no-op
// swallows bad sources. Windows executes this against the actual system object.
template<class Require>
std::array<com::result, 4U> verify_bitmap_destination_error_precedence(compat::render_target* target,
    compat::bitmap* foreign, Require require) {
    namespace d2d = compat;
    const d2d::bitmap_properties properties{{87U, d2d::alpha_mode::premultiplied}, 96, 96};
    com::pointer<d2d::bitmap> bitmap;
    require(target->CreateBitmap({8U, 8U}, nullptr, 0U, &properties, bitmap.put()) == com::ok,
        "bitmap precedence source");
    const float nan = std::numeric_limits<float>::quiet_NaN();
    const std::array<d2d::rectangle_f, 4U> sources{{{0, 0, 8, 8}, {nan, 0, 8, 8}, {6, 0, 2, 8}, {0, 0, 9, 8}}};
    std::array<com::result, 4U> observed{};
    for (std::size_t kind = 0U; kind < sources.size(); ++kind) {
        std::array<com::result, 2U> results{};
        std::array<std::array<std::uint64_t, 2U>, 2U> tags{};
        for (std::size_t inverted = 0U; inverted < 2U; ++inverted) {
            const d2d::rectangle_f destination = inverted == 0U
                ? d2d::rectangle_f{0, 0, 8, 8} : d2d::rectangle_f{8, 0, 0, 8};
            target->BeginDraw(); target->SetTags(741U, 852U);
            target->DrawBitmap(kind == 0U ? foreign : bitmap.get(), &destination, 1,
                d2d::bitmap_interpolation_mode::nearest_neighbor, &sources[kind]);
            results[inverted] = target->EndDraw(&tags[inverted][0], &tags[inverted][1]);
        }
        std::printf("DrawBitmap precedence kind=%zu ordered=0x%08x inverted=0x%08x tags=%llu/%llu,%llu/%llu\n",
            kind, static_cast<unsigned>(results[0]), static_cast<unsigned>(results[1]),
            static_cast<unsigned long long>(tags[0][0]), static_cast<unsigned long long>(tags[0][1]),
            static_cast<unsigned long long>(tags[1][0]), static_cast<unsigned long long>(tags[1][1]));
        require(results[0] == results[1] && tags[0] == tags[1], "inverted destination changed original error precedence");
        observed[kind] = results[0];
    }
    return observed;
}

inline bool bitmap_destination_contract(compat::factory* owner, compat::factory* foreign_owner) {
    namespace d2d = compat;
    com::pointer<d2d::scene_factory_native> factory, foreign_factory;
    if (owner->QueryInterface(d2d::scene_factory_native_interface_id, reinterpret_cast<void**>(factory.put())) != com::ok ||
        foreign_owner->QueryInterface(d2d::scene_factory_native_interface_id, reinterpret_cast<void**>(foreign_factory.put())) != com::ok)
        return false;
    const d2d::scene_render_target_properties properties{64U, 64U, 144, 192, 0x95C4U, 1U};
    com::pointer<d2d::render_target> target, foreign_target;
    com::pointer<d2d::scene_render_target_native> scene;
    if (factory->CreateSceneRenderTarget(&properties, target.put()) != com::ok ||
        foreign_factory->CreateSceneRenderTarget(&properties, foreign_target.put()) != com::ok ||
        target.as(d2d::scene_render_target_native_interface_id, scene) != com::ok) return false;
    const d2d::bitmap_properties bitmap_properties{{87U, d2d::alpha_mode::premultiplied}, 192, 96};
    com::pointer<d2d::bitmap> bitmap, foreign;
    if (target->CreateBitmap({16, 12}, nullptr, 0, &bitmap_properties, bitmap.put()) != com::ok ||
        foreign_target->CreateBitmap({16, 12}, nullptr, 0, &bitmap_properties, foreign.put()) != com::ok) return false;
    bool valid = true;
    const auto errors = verify_bitmap_destination_error_precedence(target.get(), foreign.get(),
        [&](bool value, const char*) { valid &= value; });
    if (!valid || errors != std::array<com::result, 4U>{d2d::wrong_factory, com::invalid_argument,
        com::invalid_argument, com::invalid_argument}) return false;
    const float nan = std::numeric_limits<float>::quiet_NaN(), infinity = std::numeric_limits<float>::infinity();
    const std::array<d2d::rectangle_f, 4U> nonfinite{{{nan, 0, 8, 8}, {0, nan, 8, 8},
        {infinity, 8, 0, 0}, {8, 8, 0, infinity}}};
    for (const auto& destination : nonfinite) {
        target->BeginDraw();
        target->DrawBitmap(bitmap.get(), &destination, 1, d2d::bitmap_interpolation_mode::nearest_neighbor, nullptr);
        if (target->EndDraw(nullptr, nullptr) != com::invalid_argument) return false;
    }
    const std::array<d2d::rectangle_f, 3U> sources{{{2, 3, 6, 9}, {0, 0, 8, 12}, {1, 1, 2, 3}}};
    for (std::size_t index = 0U; index <= sources.size(); ++index) {
        const d2d::rectangle_f source = index == sources.size() ? d2d::rectangle_f{0, 0, 8, 12} : sources[index];
        const auto* source_pointer = index == sources.size() ? nullptr : &source;
        target->BeginDraw();
        const d2d::rectangle_f inverted{9, 4, 1, 2};
        target->DrawBitmap(bitmap.get(), &inverted, 1, d2d::bitmap_interpolation_mode::nearest_neighbor, source_pointer);
        target->DrawBitmap(bitmap.get(), nullptr, 1, d2d::bitmap_interpolation_mode::nearest_neighbor, source_pointer);
        if (target->EndDraw(nullptr, nullptr) != com::ok) return false;
        std::vector<std::byte> bytes;
        progpu_native_scene_header header{};
        progpu_native_scene_command command{};
        progpu_native_scene_image_draw draw{};
        if (!export_copy_scene(scene.get(), bytes) || !read_scene_value(bytes, 0U, header) || header.command_count != 1U ||
            !read_scene_value(bytes, header.command_offset, command) || !read_scene_value(bytes, command.payload_offset, draw) ||
            draw.destination_rect.x != 0 || draw.destination_rect.y != 0 ||
            draw.destination_rect.width != source.right - source.left || draw.destination_rect.height != source.bottom - source.top ||
            draw.source_rect.x != source.left * 2 || draw.source_rect.y != source.top ||
            draw.source_rect.width != (source.right - source.left) * 2 || draw.source_rect.height != source.bottom - source.top) return false;
    }
    return true;
}
} // namespace progpu::native::direct2d::tests
