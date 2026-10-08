#pragma once

#include "../src/Direct2D/progpu_native_direct2d_prepared_glyphs.hpp"
#include "progpu_native_direct2d_copy_fixture.hpp"
#include "progpu_native_direct2d_font_source_fixture.hpp"
#include "progpu_native_hint_fault_fixture.hpp"

namespace progpu::native::direct2d::tests {

enum class prepared_pixel_path { original, prepared, independent_geometry, prepared_geometry, original_design_advances };

// These coordinates follow the authored font's two [13,13]..[313,413]
// rectangles at em 62.5 / UPM 1000, not a product decoder/placement query.
// The empty middle glyph advances by -3; offsets do not change the next pen.
inline constexpr std::array<compat::rectangle_f, 2U> prepared_reference_rectangles{{
    {4, 5, 22.75F, 30}, {24.25F, 2.5F, 43, 27.5F}}};

inline std::vector<std::byte> prepared_pixel_font(std::uint32_t origins)
{
    return origins == 0U ? progpu::native::tests::make_hint_fault_font()
        : origins == 1U ? progpu::native::tests::make_hint_fault_font(29, -19)
        : progpu::native::tests::make_hint_fault_font(-19, 29, true);
}

inline compat::point_2f prepared_pixel_baseline(bool nominal, bool right_to_left = false)
{
    if (right_to_left) return nominal ? compat::point_2f{60.59375F, 17.90625F}
        : compat::point_2f{60.1875F, 30.8125F};
    return nominal ? compat::point_2f{3.59375F, 17.90625F} : compat::point_2f{3.1875F, 30.8125F};
}

inline std::array<compat::rectangle_f, 2U> prepared_pixel_rectangles(std::uint32_t origins, bool nominal = false,
    bool right_to_left = false)
{
    if (right_to_left) {
        // Separate literal RTL oracle. Each left-oriented outline starts one
        // original design width left of its pen (31.25 or 15.625). Caller
        // advances 24,-3,24 move successive pens independently; the third
        // advanceOffset=-.75 moves right without changing the following pen.
        // Neither font decoding nor prepared placement supplies these values.
        if (nominal) {
            if (origins == 1U) return {{{45.875F, 5, 55.25F, 17.5F}, {13.875F, 2.5F, 23.25F, 15}}};
            if (origins == 2U) return {{{44.375F, 5, 53.75F, 17.5F}, {15.375F, 2.5F, 24.75F, 15}}};
            return {{{45.375F, 5, 54.75F, 17.5F}, {14.875F, 2.5F, 24.25F, 15}}};
        }
        if (origins == 1U) return {{{30.75F, 5, 49.5F, 30}, {7.5F, 2.5F, 26.25F, 27.5F}}};
        if (origins == 2U) return {{{27.75F, 5, 46.5F, 30}, {10.5F, 2.5F, 29.25F, 27.5F}}};
        return {{{29.75F, 5, 48.5F, 30}, {9.5F, 2.5F, 28.25F, 27.5F}}};
    }
    if (nominal) {
        // Original authored hmtx widths are 500 at em 31.25 / UPM 1000.
        // The no-ink middle glyph consumes 15.625, independently of offsets.
        if (origins == 1U) return {{{4.5F, 5, 13.875F, 17.5F}, {33.5F, 2.5F, 42.875F, 15}}};
        if (origins == 2U) return {{{3, 5, 12.375F, 17.5F}, {35, 2.5F, 44.375F, 15}}};
        return {{{4, 5, 13.375F, 17.5F}, {34.5F, 2.5F, 43.875F, 15}}};
    }
    // Independent exact design-unit bearing deltas at 1/16 scale. No product
    // bounds, metrics, decoded points or phantom values enter this oracle.
    if (origins == 1U) return {{{5, 5, 23.75F, 30}, {22.25F, 2.5F, 41, 27.5F}}};
    if (origins == 2U) return {{{2, 5, 20.75F, 30}, {25.25F, 2.5F, 44, 27.5F}}};
    return prepared_reference_rectangles;
}

inline compat::matrix_3x2_f prepared_pixel_transform(std::uint32_t variant)
{
    return variant >= 2U ? compat::matrix_3x2_f{1, 0.25F, -0.125F, 1, 7, 9}
        : compat::matrix_3x2_f{1, 0, 0, 1, (variant & 1U) != 0U ? 0.25F : 0.0F,
            (variant & 1U) != 0U ? 0.5F : 0.0F};
}

template<class Require>
void record_prepared_pixel_case(compat::factory* factory, compat::render_target* target,
    const std::shared_ptr<prepared_original_font>& font, compat::rendering_parameters* parameters,
    std::uint32_t variant, prepared_pixel_path path, Require require, compat::geometry* prepared_geometry = nullptr,
    std::uint32_t origins = 0U, bool nominal = false, const float* original_design_advances = nullptr,
    bool right_to_left = false)
{
    require(variant < 4U, "prepared glyph pixel inventory");
    const bool grayscale = (variant & 1U) != 0U;
    const auto transform = prepared_pixel_transform(variant);
    const compat::matrix_3x2_f identity{1, 0, 0, 1, 0, 0};
    const compat::rectangle_f clip{9, 8, 46, 38};
    const compat::layer_parameters layer{{0, 0, 64, 64}, nullptr, compat::antialias_mode::aliased,
        identity, 0.5F, nullptr, compat::layer_options::none};
    constexpr compat::color_f black{0, 0, 0, 1}, red{1, 0, 0, 1};
    com::pointer<compat::solid_color_brush> brush;
    require(target->CreateSolidColorBrush(&red, nullptr, brush.put()) == com::ok, "prepared pixel brush");
    const auto antialias = grayscale ? compat::antialias_mode::per_primitive : compat::antialias_mode::aliased;
    target->BeginDraw(); target->Clear(&black); target->SetTransform(&identity);
    target->SetAntialiasMode(antialias);
    target->SetTextAntialiasMode(grayscale ? compat::text_antialias_mode::grayscale : compat::text_antialias_mode::aliased);
    target->SetTextRenderingParams(parameters);
    if (variant >= 2U) target->PushAxisAlignedClip(&clip, compat::antialias_mode::aliased);
    if (variant == 3U) target->PushLayer(&layer, nullptr);
    target->SetTransform(&transform);
    if (path == prepared_pixel_path::prepared_geometry) {
        require(prepared_geometry != nullptr, "original rasterizer prepared outline geometry");
        target->FillGeometry(prepared_geometry, brush.get(), nullptr);
    } else if (path == prepared_pixel_path::independent_geometry) {
        com::pointer<compat::path_geometry> geometry;
        com::pointer<compat::geometry_sink> sink;
        require(factory->CreatePathGeometry(geometry.put()) == com::ok && geometry->Open(sink.put()) == com::ok,
            "independent prepared glyph geometry");
        sink->SetFillMode(compat::fill_mode::winding);
        for (const auto& rectangle : prepared_pixel_rectangles(origins, nominal, right_to_left)) {
            sink->BeginFigure({rectangle.left, rectangle.bottom}, compat::figure_begin::filled);
            sink->AddLine({rectangle.right, rectangle.bottom});
            sink->AddLine({rectangle.right, rectangle.top});
            sink->AddLine({rectangle.left, rectangle.top});
            sink->EndFigure(compat::figure_end::closed);
        }
        require(sink->Close() == com::ok, "independent prepared glyph sink close");
        target->FillGeometry(geometry.get(), brush.get(), nullptr);
    } else {
        const std::uint16_t indices[]{1U, 0U, 2U};
        const float advances[]{24, -3, right_to_left ? 24.0F : 9.0F};
        const compat::glyph_offset offsets[]{{0, 0}, {0, 0}, {-0.75F, 2.5F}};
        const bool design_reference = path == prepared_pixel_path::original_design_advances;
        require(!design_reference || (nominal && original_design_advances != nullptr),
            "original nominal comparison requires actual design-metric query");
        const auto* selected_advances = design_reference ? original_design_advances : nominal ? nullptr : advances;
        const compat::glyph_run run{font->source()->face.get(), nominal ? 31.25F : 62.5F,
            3U, indices, selected_advances, offsets, 0, right_to_left ? 3U : 2U};
        const auto baseline = prepared_pixel_baseline(nominal, right_to_left);
        if (path == prepared_pixel_path::original || design_reference) {
            target->DrawGlyphRun(baseline, &run, brush.get(), compat::measuring_mode::natural);
        } else {
            com::pointer<prepared_glyph_target> prepared;
            require(target->QueryInterface(prepared_glyph_target_id, reinterpret_cast<void**>(prepared.put())) == com::ok &&
                prepared->DrawOwnedGlyphRun(font, baseline, &run, brush.get(), compat::measuring_mode::natural) == com::ok,
                "actual prepared glyph retained draw");
        }
    }
    if (variant == 3U) target->PopLayer();
    if (variant >= 2U) target->PopAxisAlignedClip();
    require(target->EndDraw(nullptr, nullptr) == com::ok, "prepared glyph pixel EndDraw");
    target->SetTextRenderingParams(nullptr);
}

template<class Render, class Require>
void verify_prepared_glyph_pixels(Render render, Require require)
{
    for (std::uint32_t origins = 0U; origins < 3U; ++origins) {
    font_stream stream; stream.bytes = prepared_pixel_font(origins); stream.declared_size = stream.bytes.size();
    font_loader loader; loader.stream = &stream;
    font_file file; file.loader = &loader;
    font_face face; face.files[0] = &file; face.declared_count = 1U;
    face.type = 1U; face.index = 0U; face.simulations = 0U; face.glyph_count = 3U;
    std::shared_ptr<const original_font_capture> source;
    std::shared_ptr<prepared_original_font> font;
    require(capture_original_font(&face, source) == com::ok && prepared_original_font::create(source, font) == com::ok,
        "prepared glyph original-byte owner");
    face.count_result = compat::not_implemented;
    const auto reads = stream.reads;
    rendering_parameters parameters; parameters.mode = compat::rendering_mode::outline;
    com::pointer<compat::factory> factory;
    com::pointer<compat::scene_factory_native> scene_factory;
    require(compat::create_factory(factory.put()) == com::ok &&
        factory.as(compat::scene_factory_native_interface_id, scene_factory) == com::ok, "prepared pixel factory");
    for (const bool right_to_left : {false, true}) {
    for (const bool nominal : {false, true}) {
    for (std::uint32_t variant = 0U; variant < 4U; ++variant) {
        std::array<std::vector<std::byte>, 2U> scenes;
        std::array<progpu_native_scene_header, 2U> headers{};
        for (std::uint32_t reference = 0U; reference < 2U; ++reference) {
            const compat::scene_render_target_properties properties{64U, 64U, 96, 96, 0x95D1U,
                1U + origins * 32U + (right_to_left ? 16U : 0U) + (nominal ? 8U : 0U) + variant * 2U + reference};
            com::pointer<compat::render_target> target;
            com::pointer<compat::scene_render_target_native> scene;
            require(scene_factory->CreateSceneRenderTarget(&properties, target.put()) == com::ok &&
                target.as(compat::scene_render_target_native_interface_id, scene) == com::ok, "prepared pixel target");
            record_prepared_pixel_case(factory.get(), target.get(), font, &parameters, variant,
                reference == 0U ? prepared_pixel_path::prepared : prepared_pixel_path::independent_geometry,
                require, nullptr, origins, nominal, nullptr, right_to_left);
            require(export_copy_scene(scene.get(), scenes[reference]) && read_scene_value(scenes[reference], 0U, headers[reference]),
                "prepared glyph immutable scene export");
        } // Source targets and brushes end before native replay.
        const auto cold = render(false, scenes[0], headers[0]);
        const auto warm = render(false, scenes[0], headers[0]);
        const auto independent = render(true, scenes[1], headers[1]);
        require(cold.size() == 64U * 64U * 4U && cold == warm && cold == independent,
            "prepared glyph full-byte original-coordinate/cold/warm/independent comparison");
        if (variant == 0U && origins == 0U && !nominal && !right_to_left) {
            constexpr std::array<std::uint8_t, 4U> black{0, 0, 0, 255}, red{255, 0, 0, 255};
            require(std::equal(red.begin(), red.end(), cold.data() + (10U * 64U + 10U) * 4U) &&
                std::equal(red.begin(), red.end(), cold.data() + (10U * 64U + 30U) * 4U) &&
                std::equal(black.begin(), black.end(), cold.data() + (10U * 64U + 23U) * 4U) &&
                std::equal(black.begin(), black.end(), cold.data()), "prepared absolute ink/no-ink-advance/background pixels");
        }
        if (variant == 0U && origins == 0U && nominal && !right_to_left) {
            constexpr std::array<std::uint8_t, 4U> black{0, 0, 0, 255}, red{255, 0, 0, 255};
            require(std::equal(red.begin(), red.end(), cold.data() + (10U * 64U + 10U) * 4U) &&
                std::equal(red.begin(), red.end(), cold.data() + (10U * 64U + 40U) * 4U) &&
                std::equal(black.begin(), black.end(), cold.data() + (10U * 64U + 25U) * 4U) &&
                std::equal(black.begin(), black.end(), cold.data()), "nominal absolute ink/no-ink-advance/background pixels");
        }
        if (variant == 0U && origins == 0U && right_to_left) {
            constexpr std::array<std::uint8_t, 4U> black{0, 0, 0, 255}, red{255, 0, 0, 255};
            const std::uint32_t first = nominal ? 50U : 40U, gap = nominal ? 35U : 29U;
            require(std::equal(red.begin(), red.end(), cold.data() + (10U * 64U + first) * 4U) &&
                std::equal(red.begin(), red.end(), cold.data() + (10U * 64U + 20U) * 4U) &&
                std::equal(black.begin(), black.end(), cold.data() + (10U * 64U + gap) * 4U) &&
                std::equal(black.begin(), black.end(), cold.data()), "RTL absolute logical-glyph/no-ink-advance/background pixels");
        }
    }
    }
    }
    require(stream.reads == reads && face.outline_calls == 0U && face.table_calls == 0U && font->cached_glyph_count() == 3U,
        "prepared repeated draw must reuse original context without source callbacks");
    }
}
} // namespace progpu::native::direct2d::tests
