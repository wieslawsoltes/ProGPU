#pragma once

#include "progpu_native_direct2d_copy_fixture.hpp"

#include <algorithm>
#include <array>
#include <cstdio>

namespace progpu::native::direct2d::tests {

// Only original Direct2D vtable calls: Windows also runs this sequence on a
// genuine system factory/WIC target, not on the portable implementation.
template<class Copy, class Require>
void record_scoped_bitmap_copy(compat::render_target* parent, std::uint32_t variant, Copy copy, Require require) {
    namespace d2d = compat;
    require(variant < 4U, "scoped copy variant invalid");
    const float scale = variant >= 2U ? 2.0F : 1.0F;
    const bool full = (variant & 1U) != 0U;
    const d2d::size_f logical_size{32.0F / scale, 32.0F / scale};
    const d2d::size_u pixel_size{32U, 32U};
    const d2d::pixel_format format{87U, d2d::alpha_mode::premultiplied};
    com::pointer<d2d::bitmap_render_target> child;
    com::pointer<d2d::bitmap> bitmap;
    require(parent->CreateCompatibleRenderTarget(&logical_size, &pixel_size, &format,
        d2d::compatible_render_target_options::none, child.put()) == com::ok &&
        child->GetBitmap(bitmap.put()) == com::ok, "scoped copy compatible target creation");
    float dpi_x{}, dpi_y{};
    bitmap->GetDpi(&dpi_x, &dpi_y);
    require(dpi_x == 96.0F * scale && dpi_y == dpi_x, "scoped copy actual target DPI differs");
    const std::array colors{d2d::color_f{1, 0, 0, 1}, d2d::color_f{0, 1, 0, 1},
        d2d::color_f{1, 1, 0, 1}, d2d::color_f{1, 1, 1, 1}};
    std::array<com::pointer<d2d::solid_color_brush>, 3U> brushes;
    for (std::size_t i = 0U; i < brushes.size(); ++i)
        require(child->CreateSolidColorBrush(&colors[i + 1U], nullptr, brushes[i].put()) == com::ok,
            "scoped copy owned brush creation");
    const d2d::matrix_3x2_f identity{1, 0, 0, 1, 0, 0};
    const d2d::matrix_3x2_f capture{1, 0, 0, 1, 2.0F / scale, 3.0F / scale};
    const d2d::matrix_3x2_f copy_transform{2, 0, 0, 3, 40.0F / scale, 50.0F / scale};
    const d2d::rectangle_f outer{6.0F / scale, 5.0F / scale, 22.0F / scale, 21.0F / scale};
    const d2d::rectangle_f inner{12.0F / scale, 10.0F / scale, 20.0F / scale, 18.0F / scale};
    const d2d::rectangle_f whole{0, 0, 32.0F / scale, 32.0F / scale};
    const d2d::rectangle_f stripe{0, 0, 32.0F / scale, 12.0F / scale};
    const d2d::rectangle_f white{28.0F / scale, 28.0F / scale, 30.0F / scale, 30.0F / scale};
    child->BeginDraw();
    child->Clear(&colors[0]);
    child->SetAntialiasMode(d2d::antialias_mode::aliased);
    child->SetTransform(&capture);
    child->PushAxisAlignedClip(&outer, d2d::antialias_mode::aliased);
    child->SetTransform(&identity);
    child->PushAxisAlignedClip(&inner, d2d::antialias_mode::aliased);
    child->SetTransform(&copy_transform);
    child->SetTags(77U, 88U);
    const auto copy_size = full ? 32U : 4U;
    const auto pitch = copy_size * 4U + 4U;
    std::vector<std::uint8_t> upload(pitch * copy_size, 0x7dU);
    for (std::uint32_t y = 0U; y < copy_size; ++y)
        for (std::uint32_t x = 0U; x < copy_size; ++x) {
            const auto at = y * pitch + x * 4U;
            upload[at] = upload[at + 3U] = 255U; // original BGRA blue
            upload[at + 1U] = upload[at + 2U] = 0U;
        }
    std::fill_n(upload.data() + pitch + 4U, 4U, std::uint8_t{0}); // transparent SRC replacement
    const d2d::rectangle_u destination{2U, 2U, 6U, 6U};
    const auto copied = copy(child.get(), bitmap.get(), full ? nullptr : &destination, upload, pitch);
    if (copied != com::ok) std::fprintf(stderr, "Scoped bitmap copy variant=%u HRESULT=0x%08x\n",
        variant, static_cast<unsigned>(copied));
    require(copied == com::ok, "active aliased bitmap copy failed");
    std::fill(upload.begin(), upload.end(), 0xffU); // source storage is borrowed only during the call
    d2d::matrix_3x2_f observed{};
    child->GetTransform(&observed);
    std::uint64_t tag1{}, tag2{};
    child->GetTags(&tag1, &tag2);
    require(observed.m11 == 2.0F && observed.m22 == 3.0F &&
        observed.m31 == copy_transform.m31 && observed.m32 == copy_transform.m32 &&
        tag1 == 77U && tag2 == 88U, "scoped copy changed caller drawing state");
    child->SetTransform(&identity);
    child->FillRectangle(&whole, brushes[0].get());
    child->PopAxisAlignedClip();
    child->FillRectangle(&stripe, brushes[1].get());
    child->PopAxisAlignedClip();
    child->FillRectangle(&white, brushes[2].get());
    require(child->EndDraw(nullptr, nullptr) == com::ok, "scoped copy post-copy drawing or scope restoration failed");
    const d2d::color_f black{0, 0, 0, 1};
    const d2d::rectangle_f output{4, 4, 36, 36};
    parent->BeginDraw();
    parent->Clear(&black);
    parent->SetTransform(&identity);
    parent->DrawBitmap(bitmap.get(), &output, 1.0F, d2d::bitmap_interpolation_mode::nearest_neighbor, nullptr);
    require(parent->EndDraw(nullptr, nullptr) == com::ok, "scoped copy final retained source recording");
    // Child, bitmap, brushes and upload bytes retire before parent replay.
}

template<class Require>
void record_scoped_memory_copy(compat::render_target* parent, std::uint32_t variant, Require require) {
    record_scoped_bitmap_copy(parent, variant,
        [](compat::render_target*, compat::bitmap* destination, const compat::rectangle_u* rectangle,
            const auto& upload, std::uint32_t pitch) {
            return destination->CopyFromMemory(rectangle, upload.data(), pitch);
        }, require);
}

inline std::array<std::uint8_t, 4U> scoped_copy_expected_pixel(
    std::uint32_t variant, std::uint32_t x, std::uint32_t y) {
    std::array<std::uint8_t, 4U> result{0, 0, 0, 255};
    if (x < 4U || y < 4U || x >= 36U || y >= 36U) return result;
    x -= 4U; y -= 4U;
    const bool full = (variant & 1U) != 0U;
    result[full ? 2U : 0U] = 255U;
    if (full || (x >= 2U && y >= 2U && x < 6U && y < 6U)) {
        result = {0, 0, 255, 255};
        if (x == (full ? 1U : 3U) && y == (full ? 1U : 3U)) result = {0, 0, 0, 255};
    }
    if (x >= 12U && x < 20U && y >= 10U && y < 18U) result = {0, 255, 0, 255};
    if (x >= 8U && x < 24U && y >= 8U && y < 12U) result = {255, 255, 0, 255};
    if (x >= 28U && x < 30U && y >= 28U && y < 30U) result = {255, 255, 255, 255};
    return result;
}

template<class Render, class Require>
void verify_scoped_memory_copy_pixels(Render render, Require require) {
    com::pointer<compat::factory> owner;
    com::pointer<compat::scene_factory_native> factory;
    require(compat::create_factory(owner.put()) == com::ok &&
        owner.as(compat::scene_factory_native_interface_id, factory) == com::ok,
        "scoped copy source factory creation");
    for (std::uint32_t variant = 0U; variant < 4U; ++variant) {
        const compat::scene_render_target_properties properties{64U, 64U, 96, 96, 0x95A3U, variant + 1U};
        com::pointer<compat::render_target> target;
        com::pointer<compat::scene_render_target_native> scene;
        require(factory->CreateSceneRenderTarget(&properties, target.put()) == com::ok &&
            target.as(compat::scene_render_target_native_interface_id, scene) == com::ok,
            "scoped copy parent target creation");
        record_scoped_memory_copy(target.get(), variant, require);
        std::vector<std::byte> stream;
        progpu_native_scene_header header{};
        require(export_copy_scene(scene.get(), stream) && read_scene_value(stream, 0U, header) &&
            header.command_count == 1U, "scoped copy actual scene export");
        scene.reset(); target.reset();
        std::array<std::vector<std::uint8_t>, 3U> pixels;
        for (std::uint32_t replay = 0U; replay < 3U; ++replay)
            pixels[replay] = render(replay == 2U, stream, header.generation, replay == 1U ? 1U : 2U);
        require(pixels[0] == pixels[1] && pixels[0] == pixels[2] && pixels[0].size() == 64U * 64U * 4U,
            "scoped copy cold/warm/independent retained pixels differ");
        for (std::uint32_t y = 0U; y < 64U; ++y) for (std::uint32_t x = 0U; x < 64U; ++x) {
            const auto expected = scoped_copy_expected_pixel(variant, x, y);
            const auto* actual = pixels[0].data() + (y * 64U + x) * 4U;
            const bool matches = std::equal(expected.begin(), expected.end(), actual);
            if (!matches) std::fprintf(stderr,
                "Scoped copy variant=%u pixel=(%u,%u) actual=(%u,%u,%u,%u) expected=(%u,%u,%u,%u)\n",
                variant, x, y, actual[0], actual[1], actual[2], actual[3],
                expected[0], expected[1], expected[2], expected[3]);
            require(matches, "scoped copy replacement, DPI, clipping or following draw differs");
        }
    }
}
} // namespace progpu::native::direct2d::tests
