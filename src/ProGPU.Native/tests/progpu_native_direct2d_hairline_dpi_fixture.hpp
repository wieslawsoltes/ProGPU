#pragma once

#include "progpu_native_direct2d_copy_fixture.hpp"

#include <algorithm>
#include <cstdio>
#include <span>

namespace progpu::native::direct2d::tests {

inline constexpr std::array<float, 2U> hairline_dpi_dashes{4, 4};
inline constexpr std::array<compat::color_f, 4U> hairline_dpi_colors{{
    {1, 0, 0, 1}, {0, 1, 0, 1}, {0, 0, 1, 1}, {1, 1, 1, 1}}};
inline constexpr std::array<float, 4U> hairline_dpi_requested_widths{0, 17, 123, 0};

inline compat::stroke_style_properties hairline_dpi_style(std::uint32_t band)
{
    const auto cap = band >= 2U ? compat::cap_style::square : compat::cap_style::flat;
    return {cap, cap, cap, compat::line_join::miter, 4.0F,
        compat::dash_style::custom, (band & 1U) == 0U ? 0.0F : 2.0F};
}

inline std::span<const compat::rectangle_f> hairline_dpi_rectangles(std::uint32_t band)
{
    // Literal physical coverage, independently authored from the four source
    // patterns. No product walker, outline, payload or bounds query is used.
    // Quarter-X edges avoid aliased pixel-center/cap ties. A square cap adds
    // exactly half of the one-physical-pixel width at both ends of each dash.
    static constexpr std::array<compat::rectangle_f, 6U> flat{{
        {8.25F, 8, 12.25F, 9}, {16.25F, 8, 20.25F, 9}, {24.25F, 8, 28.25F, 9},
        {32.25F, 8, 36.25F, 9}, {40.25F, 8, 44.25F, 9}, {48.25F, 8, 52.25F, 9}}};
    static constexpr std::array<compat::rectangle_f, 7U> phase{{
        {8.25F, 16, 10.25F, 17}, {14.25F, 16, 18.25F, 17}, {22.25F, 16, 26.25F, 17},
        {30.25F, 16, 34.25F, 17}, {38.25F, 16, 42.25F, 17}, {46.25F, 16, 50.25F, 17},
        {54.25F, 16, 56.25F, 17}}};
    // Original Microsoft Direct2D retains the terminal zero-length dash at
    // this exact phase boundary, including its square dash/source-end caps.
    static constexpr std::array<compat::rectangle_f, 7U> square{{
        {7.75F, 24, 12.75F, 25}, {15.75F, 24, 20.75F, 25}, {23.75F, 24, 28.75F, 25},
        {31.75F, 24, 36.75F, 25}, {39.75F, 24, 44.75F, 25}, {47.75F, 24, 52.75F, 25},
        {55.75F, 24, 56.75F, 25}}};
    static constexpr std::array<compat::rectangle_f, 7U> square_phase{{
        {7.75F, 32, 10.75F, 33}, {13.75F, 32, 18.75F, 33}, {21.75F, 32, 26.75F, 33},
        {29.75F, 32, 34.75F, 33}, {37.75F, 32, 42.75F, 33}, {45.75F, 32, 50.75F, 33},
        {53.75F, 32, 56.75F, 33}}};
    if (band == 0U) return flat;
    if (band == 1U) return phase;
    if (band == 2U) return square;
    if (band == 3U) return square_phase;
    return {};
}

template<class Require>
void record_hairline_dpi_case(compat::factory* factory, compat::render_target* target,
    float dpi, bool curved, bool independent, std::span<compat::stroke_style* const> injected,
    Require require)
{
    require((dpi == 96.0F || dpi == 192.0F) && (injected.empty() || injected.size() == 4U),
        "hairline DPI source inventory/style ownership differs");
    const float scale = dpi / 96.0F;
    constexpr compat::matrix_3x2_f identity{1, 0, 0, 1, 0, 0};
    constexpr compat::color_f black{0, 0, 0, 1};
    target->SetDpi(dpi, dpi);
    target->SetTransform(&identity);
    target->SetAntialiasMode(compat::antialias_mode::aliased);
    target->BeginDraw();
    target->Clear(&black);
    for (std::uint32_t band = 0U; band < 4U; ++band) {
        com::pointer<compat::solid_color_brush> brush;
        require(target->CreateSolidColorBrush(&hairline_dpi_colors[band], nullptr, brush.put()) == com::ok,
            "hairline DPI brush creation failed");
        if (independent) {
            com::pointer<compat::path_geometry> geometry;
            com::pointer<compat::geometry_sink> sink;
            require(factory->CreatePathGeometry(geometry.put()) == com::ok && geometry->Open(sink.put()) == com::ok,
                "independent hairline coverage geometry failed");
            sink->SetFillMode(compat::fill_mode::winding);
            for (const auto& physical : hairline_dpi_rectangles(band)) {
                const compat::rectangle_f rectangle{physical.left / scale, physical.top / scale,
                    physical.right / scale, physical.bottom / scale};
                sink->BeginFigure({rectangle.left, rectangle.top}, compat::figure_begin::filled);
                sink->AddLine({rectangle.right, rectangle.top});
                sink->AddLine({rectangle.right, rectangle.bottom});
                sink->AddLine({rectangle.left, rectangle.bottom});
                sink->EndFigure(compat::figure_end::closed);
            }
            require(sink->Close() == com::ok, "independent hairline coverage close failed");
            target->FillGeometry(geometry.get(), brush.get(), nullptr);
            continue;
        }
        com::pointer<compat::stroke_style1> owned_style;
        compat::stroke_style* style{};
        if (injected.empty()) {
            const auto properties = hairline_dpi_style(band);
            require(compat::create_stroke_style1(factory, &properties, compat::stroke_transform_type::hairline,
                hairline_dpi_dashes.data(), static_cast<std::uint32_t>(hairline_dpi_dashes.size()), owned_style.put()) == com::ok,
                "portable hairline DPI style creation failed");
            style = owned_style.get();
        } else {
            style = injected[band];
            require(style != nullptr, "original hairline DPI style is missing");
        }
        const float y = (8.5F + 8.0F * static_cast<float>(band)) / scale;
        const compat::point_2f begin{8.25F / scale, y}, end{56.25F / scale, y};
        if (curved) {
            com::pointer<compat::path_geometry> geometry;
            com::pointer<compat::geometry_sink> sink;
            require(factory->CreatePathGeometry(geometry.put()) == com::ok && geometry->Open(sink.put()) == com::ok,
                "hairline DPI collinear cubic creation failed");
            sink->BeginFigure(begin, compat::figure_begin::hollow);
            const compat::bezier_segment curve{{24.25F / scale, y}, {40.25F / scale, y}, end};
            sink->AddBezier(&curve);
            sink->EndFigure(compat::figure_end::open);
            require(sink->Close() == com::ok, "hairline DPI collinear cubic close failed");
            target->DrawGeometry(geometry.get(), brush.get(), hairline_dpi_requested_widths[band], style);
        } else {
            target->DrawLine(begin, end, brush.get(), hairline_dpi_requested_widths[band], style);
        }
    }
    require(target->EndDraw(nullptr, nullptr) == com::ok, "hairline DPI recording failed");
}

template<class Require>
void hairline_dpi_pixels(std::span<const std::uint8_t> pixels, bool bgra, Require require)
{
    require(pixels.size() == 64U * 64U * 4U, "hairline DPI frame size differs");
    std::uint32_t mismatched = 0U;
    for (std::uint32_t y = 0U; y < 64U; ++y) for (std::uint32_t x = 0U; x < 64U; ++x) {
        std::array<std::uint8_t, 4U> expected{0, 0, 0, 255};
        const float px = static_cast<float>(x) + 0.5F, py = static_cast<float>(y) + 0.5F;
        for (std::uint32_t band = 0U; band < 4U; ++band) {
            for (const auto& rectangle : hairline_dpi_rectangles(band)) {
                if (px < rectangle.left || px >= rectangle.right || py < rectangle.top || py >= rectangle.bottom) continue;
                if (band == 3U) expected = {255, 255, 255, 255};
                else expected[bgra ? 2U - band : band] = 255;
            }
        }
        const auto* actual = pixels.data() + (y * 64U + x) * 4U;
        if (!std::equal(expected.begin(), expected.end(), actual) && mismatched++ < 32U)
            std::fprintf(stderr, "Hairline DPI pixel=(%u,%u) actual=%u,%u,%u,%u expected=%u,%u,%u,%u\n", x, y,
                static_cast<unsigned>(actual[0]), static_cast<unsigned>(actual[1]),
                static_cast<unsigned>(actual[2]), static_cast<unsigned>(actual[3]),
                static_cast<unsigned>(expected[0]), static_cast<unsigned>(expected[1]),
                static_cast<unsigned>(expected[2]), static_cast<unsigned>(expected[3]));
    }
    if (mismatched != 0U) std::fprintf(stderr, "Hairline DPI mismatched pixels=%u\n", mismatched);
    require(mismatched == 0U, "hairline DPI phase/cap/width/untouched-pixel or alpha contract differs");
}

template<class Render, class Require>
void verify_hairline_dpi_pixels(Render render, Require require)
{
    com::pointer<compat::factory> factory;
    com::pointer<compat::scene_factory_native> scene_factory;
    require(compat::create_factory(factory.put()) == com::ok &&
        factory.as(compat::scene_factory_native_interface_id, scene_factory) == com::ok,
        "hairline DPI fixture factory failed");
    std::vector<std::uint8_t> first;
    std::uint64_t generation = 1U;
    for (const float dpi : {96.0F, 192.0F}) for (const bool curved : {false, true}) {
        std::array<std::vector<std::byte>, 2U> scenes;
        std::array<progpu_native_scene_header, 2U> headers{};
        for (std::uint32_t reference = 0U; reference < 2U; ++reference) {
            const compat::scene_render_target_properties properties{64U, 64U, dpi, dpi, 0x95EAU, generation++};
            com::pointer<compat::render_target> target;
            com::pointer<compat::scene_render_target_native> scene;
            require(scene_factory->CreateSceneRenderTarget(&properties, target.put()) == com::ok &&
                target.as(compat::scene_render_target_native_interface_id, scene) == com::ok,
                "hairline DPI fixture target failed");
            record_hairline_dpi_case(factory.get(), target.get(), dpi, curved, reference != 0U, {}, require);
            require(export_copy_scene(scene.get(), scenes[reference]) && read_scene_value(scenes[reference], 0U, headers[reference]),
                "hairline DPI immutable scene export failed");
            require(headers[reference].command_count == 4U, "hairline DPI source draw inventory changed");
        }
        // Targets/styles/geometry have retired before any provider replay.
        const auto cold = render(false, scenes[0], headers[0], dpi / 96.0F);
        const auto warm = render(false, scenes[0], headers[0], dpi / 96.0F);
        const auto independent = render(true, scenes[1], headers[1], dpi / 96.0F);
        if (cold != warm || cold != independent) {
            std::fprintf(stderr, "Hairline DPI replay dpi=%g curved=%u cold/warm=%u cold/independent=%u\n",
                static_cast<double>(dpi), curved ? 1U : 0U, cold == warm ? 1U : 0U, cold == independent ? 1U : 0U);
            hairline_dpi_pixels(cold, false, require);
            hairline_dpi_pixels(warm, false, require);
            hairline_dpi_pixels(independent, false, require);
        }
        require(cold == warm && cold == independent, "hairline DPI cold/warm/independent coverage differs");
        hairline_dpi_pixels(cold, false, require);
        if (first.empty()) first = cold;
        else require(cold == first, "hairline physical pixels changed with source DPI or collinear cubic path");
    }
}

} // namespace progpu::native::direct2d::tests
