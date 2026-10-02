#pragma once

#include "progpu_native_direct2d_prepared_glyph_fixture.hpp"
#include "progpu_native_direct2d_font_axis_fixture.hpp"
#include "progpu_native_direct2d_variable_font_fixture.hpp"

namespace progpu::native::direct2d::tests {

enum class variable_pixel_path { original, prepared, independent_geometry, prepared_geometry, original_design_advances };

inline constexpr std::array<variable_font_options, 4U> variable_pixel_font_options{{
    {false, false, false}, {false, false, true}, {true, false, false}, {true, true, true}}};

// Every expected design bound/origin/advance comes from the fixture's authored
// table, never a product font parser, outline, HVAR or glyph-run output.
inline std::array<compat::rectangle_f, 2U> variable_pixel_rectangles(std::size_t case_index, bool nominal)
{
    const auto first = expected_variable_glyph(1U, case_index);
    const auto empty = expected_variable_glyph(0U, case_index);
    const auto second = expected_variable_glyph(2U, case_index);
    constexpr float scale = 1.0F / 32.0F;
    const auto pen = nominal ? first.advance * scale + empty.advance * scale : 9.0F;
    return {{{(first.x_min - first.horizontal_origin) * scale + 4.0F, 28.0F - first.y_max * scale,
              (first.x_max - first.horizontal_origin) * scale + 4.0F, 28.0F - first.y_min * scale},
             {(second.x_min - second.horizontal_origin) * scale + 4.0F + pen - 0.75F,
              25.5F - second.y_max * scale,
              (second.x_max - second.horizontal_origin) * scale + 4.0F + pen - 0.75F,
              25.5F - second.y_min * scale}}};
}

template<class Require>
void record_variable_pixel_case(compat::factory* factory, compat::render_target* target,
    const std::shared_ptr<prepared_original_font>& font, compat::rendering_parameters* parameters,
    std::size_t case_index, bool nominal, std::uint32_t variant, variable_pixel_path path,
    Require require, compat::geometry* prepared_geometry = nullptr, const float* original_design_advances = nullptr)
{
    require(case_index < variable_font_cases.size() && variant < 3U, "variable pixel inventory");
    const compat::matrix_3x2_f identity{1, 0, 0, 1, 0, 0};
    const auto transform = variant == 0U ? identity : variant == 1U
        ? compat::matrix_3x2_f{1, 0, 0, 1, 0.25F, 0.5F}
        : compat::matrix_3x2_f{1, 0.25F, -0.125F, 1, 7, 9};
    const compat::rectangle_f clip{9, 8, 48, 42};
    const compat::layer_parameters layer{{0, 0, 64, 64}, nullptr, compat::antialias_mode::aliased,
        identity, 0.5F, nullptr, compat::layer_options::none};
    constexpr compat::color_f black{0, 0, 0, 1}, red{1, 0, 0, 1};
    com::pointer<compat::solid_color_brush> brush;
    require(target->CreateSolidColorBrush(&red, nullptr, brush.put()) == com::ok, "variable glyph source brush");
    target->BeginDraw(); target->Clear(&black); target->SetTransform(&identity);
    target->SetAntialiasMode(variant == 0U ? compat::antialias_mode::aliased : compat::antialias_mode::per_primitive);
    target->SetTextAntialiasMode(variant == 0U ? compat::text_antialias_mode::aliased : compat::text_antialias_mode::grayscale);
    target->SetTextRenderingParams(parameters);
    if (variant == 2U) { target->PushAxisAlignedClip(&clip, compat::antialias_mode::aliased); target->PushLayer(&layer, nullptr); }
    target->SetTransform(&transform);
    if (path == variable_pixel_path::prepared_geometry) {
        require(prepared_geometry != nullptr, "variable original rasterizer prepared geometry");
        target->FillGeometry(prepared_geometry, brush.get(), nullptr);
    } else if (path == variable_pixel_path::independent_geometry) {
        com::pointer<compat::path_geometry> geometry;
        com::pointer<compat::geometry_sink> sink;
        require(factory->CreatePathGeometry(geometry.put()) == com::ok && geometry->Open(sink.put()) == com::ok,
            "variable independent geometry creation");
        sink->SetFillMode(compat::fill_mode::winding);
        for (const auto& rectangle : variable_pixel_rectangles(case_index, nominal)) {
            sink->BeginFigure({rectangle.left, rectangle.bottom}, compat::figure_begin::filled);
            sink->AddLine({rectangle.right, rectangle.bottom}); sink->AddLine({rectangle.right, rectangle.top});
            sink->AddLine({rectangle.left, rectangle.top}); sink->EndFigure(compat::figure_end::closed);
        }
        require(sink->Close() == com::ok, "variable independent geometry close");
        target->FillGeometry(geometry.get(), brush.get(), nullptr);
    } else {
        const std::uint16_t indices[]{1U, 0U, 2U};
        const float advances[]{12, -3, 9};
        const compat::glyph_offset offsets[]{{0, 0}, {0, 0}, {-0.75F, 2.5F}};
        const bool design_reference = path == variable_pixel_path::original_design_advances;
        require(!design_reference || (nominal && original_design_advances != nullptr), "genuine variable design advances required");
        const compat::glyph_run run{font->source()->face.get(), 31.25F, 3U, indices,
            design_reference ? original_design_advances : nominal ? nullptr : advances, offsets, 0, 2U};
        if (path == variable_pixel_path::original || design_reference) {
            target->DrawGlyphRun({4, 28}, &run, brush.get(), compat::measuring_mode::natural);
        } else {
            com::pointer<prepared_glyph_target> prepared;
            require(target->QueryInterface(prepared_glyph_target_id, reinterpret_cast<void**>(prepared.put())) == com::ok &&
                prepared->DrawOwnedGlyphRun(font, {4, 28}, &run, brush.get(), compat::measuring_mode::natural) == com::ok,
                "actual variable prepared retained draw");
        }
    }
    if (variant == 2U) { target->PopLayer(); target->PopAxisAlignedClip(); }
    require(target->EndDraw(nullptr, nullptr) == com::ok, "variable prepared EndDraw");
    target->SetTextRenderingParams(nullptr);
}

template<class Render, class Require>
void verify_variable_glyph_pixels(Render render, Require require)
{
    com::pointer<compat::factory> factory;
    com::pointer<compat::scene_factory_native> scene_factory;
    require(compat::create_factory(factory.put()) == com::ok &&
        factory.as(compat::scene_factory_native_interface_id, scene_factory) == com::ok, "variable source factory");
    rendering_parameters parameters; parameters.mode = compat::rendering_mode::outline;
    std::uint64_t generation = 0U;
    for (const auto& options : variable_pixel_font_options) {
        for (std::size_t instance = 0U; instance < variable_font_cases.size(); ++instance) {
            font_stream stream; stream.bytes = make_variable_font(options); stream.declared_size = stream.bytes.size();
            font_loader loader; loader.stream = &stream;
            font_file file; file.loader = &loader;
            font_face5 face; face.files = {&file}; face.declared_count = 1U;
            face.type = 1U; face.index = 0U; face.simulations = 0U; face.glyph_count = 3U;
            face.axes = {{0x74686777U, variable_font_cases[instance].weight}};
            std::shared_ptr<const original_font_capture> source;
            std::shared_ptr<prepared_original_font> font;
            require(capture_original_font(&face, source) == com::ok && prepared_original_font::create(source, font) == com::ok,
                "variable original byte/axis owner");
            const auto reads = stream.reads, values = face.value_reads;
            // Retire every mutable producer path after the original capture.
            face.count_result = compat::not_implemented; face.values_result = compat::not_implemented;
            face.axes[0].value = -12345.0F;
            stream.bytes.assign(16U, std::byte{0});
            for (const bool nominal : {false, true}) {
                const auto variant = static_cast<std::uint32_t>((instance + (nominal ? 1U : 0U)) % 3U);
                std::array<std::vector<std::byte>, 2U> scenes;
                std::array<progpu_native_scene_header, 2U> headers{};
                for (std::uint32_t reference = 0U; reference < 2U; ++reference) {
                    const compat::scene_render_target_properties properties{64U, 64U, 96, 96, 0x95D5U, ++generation};
                    com::pointer<compat::render_target> target;
                    com::pointer<compat::scene_render_target_native> scene;
                    require(scene_factory->CreateSceneRenderTarget(&properties, target.put()) == com::ok &&
                        target.as(compat::scene_render_target_native_interface_id, scene) == com::ok, "variable source target");
                    record_variable_pixel_case(factory.get(), target.get(), font, &parameters, instance, nominal, variant,
                        reference == 0U ? variable_pixel_path::prepared : variable_pixel_path::independent_geometry, require);
                    require(export_copy_scene(scene.get(), scenes[reference]) && read_scene_value(scenes[reference], 0U, headers[reference]),
                        "variable immutable scene export");
                }
                const auto cold = render(false, scenes[0], headers[0]);
                const auto warm = render(false, scenes[0], headers[0]);
                const auto independent = render(true, scenes[1], headers[1]);
                require(cold.size() == 64U * 64U * 4U && cold == warm && cold == independent,
                    "variable whole-image cold/warm/independent contours+origin+advance");
                bool has_ink = false;
                for (std::size_t pixel = 0U; pixel < cold.size(); pixel += 4U) {
                    require(cold[pixel + 1U] == 0U && cold[pixel + 2U] == 0U && cold[pixel + 3U] == 255U,
                        "variable full background/channel/alpha invariant");
                    has_ink = has_ink || cold[pixel] != 0U;
                }
                require(has_ink && cold[0] == 0U && cold[1] == 0U && cold[2] == 0U && cold[3] == 255U,
                    "variable nonempty independent ink and untouched corner");
            }
            require(source->axis_values[0].value == variable_font_cases[instance].weight &&
                stream.reads == reads && face.value_reads == values && face.outline_calls == 0U &&
                face.table_calls == 0U && face.unused_calls == 0U && font->cached_glyph_count() == 3U,
                "variable retained generation survives source mutation without source callbacks");
        }
    }
}
} // namespace progpu::native::direct2d::tests
