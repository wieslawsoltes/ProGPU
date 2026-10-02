#pragma once

#include "progpu_native_direct2d_prepared_glyph_fixture.hpp"
#include "progpu_native_direct2d_vertical_font_fixture.hpp"

namespace progpu::native::direct2d::tests {

enum class sideways_pixel_path { original, prepared, independent_geometry, prepared_geometry, original_design_advances };

inline std::array<compat::rectangle_f, 2U> sideways_pixel_rectangles(vertical_font_options options, bool nominal)
{
    // Literal original-font oracle at em15.625/UPM1000: these values are not
    // derived from a font parser, outline decoder, metric owner or prepared run.
    const float second_left = nominal ? (options.compact_metrics ? 32.625F : 34.1875F) : 17.5F;
    if (options.kind == vertical_font_kind::cff) return {{{9.5625F, 19.5F, 15.8125F, 23.875F},
        {second_left, 20.3125F, second_left + 7.8125F, 23.4375F}}};
    return {{{6.4375F, 19.625F, 12.6875F, 24},
        {second_left, 20.53125F, second_left + 7.8125F, 23.65625F}}};
}

inline compat::matrix_3x2_f sideways_pixel_transform(std::uint32_t variant)
{
    return variant == 2U ? compat::matrix_3x2_f{0, 1, -1, 0, 48, 0}
        : compat::matrix_3x2_f{1, 0, 0, 1, variant == 1U ? 0.25F : 0.0F, variant == 1U ? 0.5F : 0.0F};
}

template<class Require>
void record_sideways_pixel_case(compat::factory* factory, compat::render_target* target,
    const std::shared_ptr<prepared_original_font>& font, compat::rendering_parameters* parameters,
    vertical_font_options options, bool nominal, std::uint32_t variant, sideways_pixel_path path,
    Require require, compat::geometry* prepared_geometry = nullptr, const float* original_design_advances = nullptr)
{
    require(variant < 3U && (options.kind == vertical_font_kind::truetype ||
        (options.kind == vertical_font_kind::cff && options.vorg)), "explicit sideways source inventory");
    const compat::matrix_3x2_f identity{1, 0, 0, 1, 0, 0};
    const auto transform = sideways_pixel_transform(variant);
    const compat::rectangle_f clip{3, 4, 44, 44};
    const compat::layer_parameters layer{{0, 0, 64, 64}, nullptr, compat::antialias_mode::aliased,
        identity, 0.5F, nullptr, compat::layer_options::none};
    constexpr compat::color_f black{0, 0, 0, 1}, red{1, 0, 0, 1};
    com::pointer<compat::solid_color_brush> brush;
    require(target->CreateSolidColorBrush(&red, nullptr, brush.put()) == com::ok, "sideways pixel brush");
    target->BeginDraw(); target->Clear(&black); target->SetTransform(&identity);
    target->SetAntialiasMode(variant == 0U ? compat::antialias_mode::aliased : compat::antialias_mode::per_primitive);
    target->SetTextAntialiasMode(variant == 0U ? compat::text_antialias_mode::aliased : compat::text_antialias_mode::grayscale);
    target->SetTextRenderingParams(parameters);
    if (variant == 2U) { target->PushAxisAlignedClip(&clip, compat::antialias_mode::aliased); target->PushLayer(&layer, nullptr); }
    target->SetTransform(&transform);
    if (path == sideways_pixel_path::prepared_geometry) {
        require(prepared_geometry != nullptr, "original rasterizer sideways prepared geometry");
        target->FillGeometry(prepared_geometry, brush.get(), nullptr);
    } else if (path == sideways_pixel_path::independent_geometry) {
        com::pointer<compat::path_geometry> geometry;
        com::pointer<compat::geometry_sink> sink;
        require(factory->CreatePathGeometry(geometry.put()) == com::ok && geometry->Open(sink.put()) == com::ok,
            "independent sideways geometry");
        sink->SetFillMode(compat::fill_mode::winding);
        for (const auto& rectangle : sideways_pixel_rectangles(options, nominal)) {
            sink->BeginFigure({rectangle.left, rectangle.bottom}, compat::figure_begin::filled);
            sink->AddLine({rectangle.right, rectangle.bottom}); sink->AddLine({rectangle.right, rectangle.top});
            sink->AddLine({rectangle.left, rectangle.top}); sink->EndFigure(compat::figure_end::closed);
        }
        require(sink->Close() == com::ok, "independent sideways sink close");
        target->FillGeometry(geometry.get(), brush.get(), nullptr);
    } else {
        const std::uint16_t indices[]{1U, 0U, 2U};
        const float advances[]{16, -3, 9};
        const compat::glyph_offset offsets[]{{0.25F, 0.5F}, {0, 0}, {-0.75F, 2.5F}};
        const bool design_reference = path == sideways_pixel_path::original_design_advances;
        require(!design_reference || (nominal && original_design_advances != nullptr),
            "sideways nominal oracle requires genuine original vertical advances");
        const auto* selected = design_reference ? original_design_advances : nominal ? nullptr : advances;
        const compat::glyph_run run{font->source()->face.get(), 15.625F, 3U, indices, selected,
            offsets, variant == 1U ? -1 : 1, 2U};
        const compat::point_2f baseline{4, 20};
        if (path == sideways_pixel_path::original || design_reference)
            target->DrawGlyphRun(baseline, &run, brush.get(), compat::measuring_mode::natural);
        else {
            com::pointer<prepared_glyph_target> prepared;
            require(target->QueryInterface(prepared_glyph_target_id, reinterpret_cast<void**>(prepared.put())) == com::ok &&
                prepared->DrawOwnedGlyphRun(font, baseline, &run, brush.get(), compat::measuring_mode::natural) == com::ok,
                "actual retained sideways source draw");
        }
    }
    if (variant == 2U) { target->PopLayer(); target->PopAxisAlignedClip(); }
    require(target->EndDraw(nullptr, nullptr) == com::ok, "sideways source EndDraw");
    target->SetTextRenderingParams(nullptr);
}

template<class Render, class Require>
void verify_sideways_glyph_pixels(Render render, Require require)
{
    std::uint64_t generation = 0U;
    for (const bool cff : {false, true}) {
    for (const bool compact : {false, true}) {
        vertical_font_options options{}; options.kind = cff ? vertical_font_kind::cff : vertical_font_kind::truetype;
        options.compact_metrics = compact; options.vorg = cff;
        font_stream stream; stream.bytes = make_vertical_font(options); stream.declared_size = stream.bytes.size();
        font_loader loader; loader.stream = &stream;
        font_file file; file.loader = &loader;
        font_face face; face.files[0] = &file; face.declared_count = 1U;
        face.type = cff ? 0U : 1U; face.index = 0U; face.simulations = 0U; face.glyph_count = 3U;
        std::shared_ptr<const original_font_capture> source;
        std::shared_ptr<prepared_original_font> font;
        require(capture_original_font(&face, source) == com::ok && prepared_original_font::create(source, font) == com::ok,
            "sideways original byte owner");
        face.count_result = compat::not_implemented;
        const auto reads = stream.reads;
        rendering_parameters parameters; parameters.mode = compat::rendering_mode::outline;
        com::pointer<compat::factory> factory;
        com::pointer<compat::scene_factory_native> scene_factory;
        require(compat::create_factory(factory.put()) == com::ok &&
            factory.as(compat::scene_factory_native_interface_id, scene_factory) == com::ok, "sideways source factory");
        for (const bool nominal : {false, true}) {
        for (std::uint32_t variant = 0U; variant < 3U; ++variant) {
            std::array<std::vector<std::byte>, 2U> scenes;
            std::array<progpu_native_scene_header, 2U> headers{};
            for (std::size_t reference = 0U; reference < 2U; ++reference) {
                const compat::scene_render_target_properties properties{64U, 64U, 96, 96, 0x95D4U, ++generation};
                com::pointer<compat::render_target> target;
                com::pointer<compat::scene_render_target_native> scene;
                require(scene_factory->CreateSceneRenderTarget(&properties, target.put()) == com::ok &&
                    target.as(compat::scene_render_target_native_interface_id, scene) == com::ok, "sideways scene target");
                record_sideways_pixel_case(factory.get(), target.get(), font, &parameters, options, nominal, variant,
                    reference == 0U ? sideways_pixel_path::prepared : sideways_pixel_path::independent_geometry, require);
                require(export_copy_scene(scene.get(), scenes[reference]) && read_scene_value(scenes[reference], 0U, headers[reference]),
                    "sideways immutable source scene");
            }
            const auto cold = render(false, scenes[0], headers[0]);
            const auto warm = render(false, scenes[0], headers[0]);
            const auto independent = render(true, scenes[1], headers[1]);
            require(cold.size() == 64U * 64U * 4U && cold == warm && cold == independent,
                "sideways full-byte cold/warm/independent source frame");
            if (variant == 0U) {
                constexpr std::array<std::uint8_t, 4U> red{255, 0, 0, 255}, black{0, 0, 0, 255};
                const std::uint32_t first_x = cff ? 12U : 9U;
                const std::uint32_t second_x = nominal ? (compact ? 36U : 38U) : 21U;
                require(std::equal(red.begin(), red.end(), cold.data() + (21U * 64U + first_x) * 4U) &&
                    std::equal(red.begin(), red.end(), cold.data() + (22U * 64U + second_x) * 4U) &&
                    std::equal(black.begin(), black.end(), cold.data() + (21U * 64U + 16U) * 4U) &&
                    std::equal(black.begin(), black.end(), cold.data()), "sideways absolute ink/empty advance/gap/background");
            }
        }
        }
        require(stream.reads == reads && face.outline_calls == 0U && face.table_calls == 0U && font->cached_glyph_count() == 3U,
            "sideways warm replay retains exact original owner without source callbacks");
    }
    }
}
} // namespace progpu::native::direct2d::tests
