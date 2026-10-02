#pragma once

#include "progpu_native_direct2d_copy_fixture.hpp"

#include <algorithm>
#include <array>
#include <cstdio>

namespace progpu::native::direct2d::tests {

struct compatible_dpi_case {
    compat::size_f parent_dpi;
    compat::size_f requested_size;
    compat::size_u rounded_pixels;
    bool integral_x;
    bool integral_y;
};

inline constexpr std::array<compatible_dpi_case, 6U> compatible_dpi_cases{{
    {{96, 96}, {15.25F, 15.25F}, {16, 16}, false, false},
    {{192, 96}, {7.625F, 16}, {16, 16}, false, true},
    {{96, 192}, {16, 7.625F}, {16, 16}, true, false},
    {{144, 120}, {10.125F, 12.125F}, {16, 16}, false, false},
    {{120, 144}, {12.125F, 10.125F}, {16, 16}, false, false},
    {{144, 192}, {8, 6}, {12, 12}, true, true}}};

struct compatible_dpi_snapshot {
    // Actual public observations, never a substituted requested DIP extent.
    // Child size/DPI, then GetBitmap size/DPI; physical sizes in the same order.
    std::array<float, 8U> logical{};
    std::array<std::uint32_t, 4U> physical{};
    bool operator==(const compatible_dpi_snapshot&) const = default;
};

template<class Require>
compatible_dpi_snapshot record_compatible_dpi(compat::render_target* parent,
    unsigned variant, unsigned size_mode, Require require) {
    namespace d2d = compat;
    require(variant < compatible_dpi_cases.size() && size_mode < 4U, "compatible DPI fixture inputs");
    const auto& test = compatible_dpi_cases[variant];
    const bool has_logical = (size_mode & 1U) != 0U;
    const bool has_pixels = (size_mode & 2U) != 0U;
    const d2d::size_u explicit_pixels{16, 16};
    parent->SetDpi(test.parent_dpi.width, test.parent_dpi.height);
    const auto parent_pixels = parent->GetPixelSize();
    com::pointer<d2d::bitmap_render_target> child;
    require(parent->CreateCompatibleRenderTarget(has_logical ? &test.requested_size : nullptr,
        has_pixels ? &explicit_pixels : nullptr, nullptr, d2d::compatible_render_target_options::none,
        child.put()) == com::ok, "compatible target DPI creation");
    com::pointer<d2d::bitmap> bitmap;
    require(child->GetBitmap(bitmap.put()) == com::ok, "compatible target bitmap view");
    const auto size = child->GetSize();
    const auto pixels = child->GetPixelSize();
    const auto bitmap_size = bitmap->GetSize();
    const auto bitmap_pixels = bitmap->GetPixelSize();
    compatible_dpi_snapshot observed;
    observed.logical = {size.width, size.height, 0, 0, bitmap_size.width, bitmap_size.height, 0, 0};
    child->GetDpi(&observed.logical[2], &observed.logical[3]);
    bitmap->GetDpi(&observed.logical[6], &observed.logical[7]);
    observed.physical = {pixels.width, pixels.height, bitmap_pixels.width, bitmap_pixels.height};
    const auto expected_pixels = has_pixels ? explicit_pixels : has_logical ? test.rounded_pixels : parent_pixels;
    require(pixels.width == expected_pixels.width && pixels.height == expected_pixels.height &&
        bitmap_pixels.width == pixels.width && bitmap_pixels.height == pixels.height &&
        observed.logical[2] == observed.logical[6] && observed.logical[3] == observed.logical[7] &&
        size.width == bitmap_size.width && size.height == bitmap_size.height,
        "compatible target/bitmap metrics identity");
    if (!has_logical) {
        require(observed.logical[2] == test.parent_dpi.width && observed.logical[3] == test.parent_dpi.height,
            "absent logical size changed inherited DPI");
    } else if (!has_pixels) {
        require(test.integral_x ? observed.logical[2] == test.parent_dpi.width
                : observed.logical[2] > test.parent_dpi.width,
            "size-only X DPI rounding contract");
        require(test.integral_y ? observed.logical[3] == test.parent_dpi.height
                : observed.logical[3] > test.parent_dpi.height,
            "size-only Y DPI rounding contract");
    }

    // This is the caller's original requested geometry, not a metric repaired
    // from the observed DPI. For omitted size, derive it from explicit pixels
    // and the documented inherited parent density.
    const d2d::size_f draw_size = has_logical ? test.requested_size : d2d::size_f{
        static_cast<float>(expected_pixels.width) * 96.0F / test.parent_dpi.width,
        static_cast<float>(expected_pixels.height) * 96.0F / test.parent_dpi.height};
    const d2d::color_f red{1, 0, 0, 1}, blue{0, 0, 1, 1}, black{0, 0, 0, 1}, green{0, 1, 0, 1};
    com::pointer<d2d::solid_color_brush> brush;
    require(child->CreateSolidColorBrush(&blue, nullptr, brush.put()) == com::ok,
        "compatible DPI source brush");
    child->BeginDraw();
    child->Clear(&red);
    child->SetAntialiasMode(d2d::antialias_mode::aliased);
    const d2d::rectangle_f source{0, 0, draw_size.width * 0.75F, draw_size.height * 0.75F};
    child->FillRectangle(&source, brush.get());
    require(child->EndDraw(nullptr, nullptr) == com::ok, "compatible DPI source draw");
    parent->SetDpi(96, 96);
    const d2d::matrix_3x2_f identity{1, 0, 0, 1, 0, 0};
    parent->SetTransform(&identity);
    parent->BeginDraw();
    parent->Clear(&black);
    const d2d::rectangle_f destination{0, 0, 32, 32};
    parent->DrawBitmap(bitmap.get(), &destination, 1, d2d::bitmap_interpolation_mode::nearest_neighbor, nullptr);
    require(parent->EndDraw(nullptr, nullptr) == com::ok, "compatible DPI retained draw");

    // Parent owns the original bitmap snapshot and its source DPI even after
    // the source target changes density/content and all source handles retire.
    child->SetDpi(72, 144);
    child->BeginDraw();
    child->Clear(&green);
    require(child->EndDraw(nullptr, nullptr) == com::ok, "compatible DPI source replacement");
    return observed;
}

inline std::array<std::uint8_t, 4U> compatible_dpi_expected(unsigned x, unsigned y) {
    if (x >= 32U || y >= 32U) return {0, 0, 0, 255};
    if (x < 24U && y < 24U) return {0, 0, 255, 255};
    return {255, 0, 0, 255};
}

inline void report_compatible_dpi_pixel(const char* comparison, unsigned variant, unsigned size_mode,
    unsigned x, unsigned y, const std::uint8_t* expected, const std::uint8_t* actual) {
    std::fprintf(stderr, "compatible DPI %s variant=%u mode=%u xy=(%u,%u) "
        "expected=(%u,%u,%u,%u) actual=(%u,%u,%u,%u)\n", comparison, variant, size_mode, x, y,
        static_cast<unsigned>(expected[0]), static_cast<unsigned>(expected[1]),
        static_cast<unsigned>(expected[2]), static_cast<unsigned>(expected[3]),
        static_cast<unsigned>(actual[0]), static_cast<unsigned>(actual[1]),
        static_cast<unsigned>(actual[2]), static_cast<unsigned>(actual[3]));
    std::fflush(stderr);
}

template<class Render, class Require>
void verify_compatible_dpi_pixels(Render render, Require require) {
    com::pointer<compat::factory> owner;
    com::pointer<compat::scene_factory_native> factory;
    require(compat::create_factory(owner.put()) == com::ok &&
        owner.as(compat::scene_factory_native_interface_id, factory) == com::ok,
        "compatible DPI pixel factory");
    for (unsigned variant = 0U; variant < compatible_dpi_cases.size(); ++variant) {
        for (unsigned mode = 0U; mode < 4U; ++mode) {
            const compat::scene_render_target_properties properties{64, 64, 96, 96, 0x95CAU,
                1U + static_cast<std::uint64_t>(variant) * 4U + mode};
            com::pointer<compat::render_target> target;
            com::pointer<compat::scene_render_target_native> scene;
            require(factory->CreateSceneRenderTarget(&properties, target.put()) == com::ok &&
                target.as(compat::scene_render_target_native_interface_id, scene) == com::ok,
                "compatible DPI pixel target");
            (void)record_compatible_dpi(target.get(), variant, mode, require);
            std::vector<std::byte> stream;
            require(export_copy_scene(scene.get(), stream), "compatible DPI pixel export");
            scene.reset(); target.reset();
            const auto pixels = render(false, stream, properties.generation, 2U);
            require(pixels.size() == 64U * 64U * 4U &&
                pixels == render(false, stream, properties.generation, 1U) &&
                pixels == render(true, stream, properties.generation, 2U),
                "compatible DPI retained cold/warm/independent pixels");
            for (unsigned y = 0; y < 64U; ++y) for (unsigned x = 0; x < 64U; ++x) {
                const auto expected = compatible_dpi_expected(x, y);
                const auto* actual = pixels.data() + (y * 64U + x) * 4U;
                const bool equal = std::equal(expected.begin(), expected.end(), actual);
                if (!equal) report_compatible_dpi_pixel("absolute RGBA", variant, mode, x, y, expected.data(), actual);
                require(equal, "compatible DPI absolute retained pixels");
            }
        }
    }
}

inline bool compatible_dpi_contract(compat::scene_factory_native* factory) {
    const compat::scene_render_target_properties properties{64, 64, 96, 96, 0x95CBU, 1};
    com::pointer<compat::render_target> target;
    if (factory->CreateSceneRenderTarget(&properties, target.put()) != com::ok) return false;
    bool valid = true;
    for (unsigned variant = 0U; variant < compatible_dpi_cases.size(); ++variant)
        for (unsigned mode = 0U; mode < 4U; ++mode) {
            (void)record_compatible_dpi(target.get(), variant, mode,
                [&](bool success, const char*) { valid &= success; });
            if (!valid) return false;
        }
    return true;
}
} // namespace progpu::native::direct2d::tests
