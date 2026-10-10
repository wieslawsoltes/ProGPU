#pragma once

#include "progpu_native_direct2d_prepared_glyph_fixture.hpp"
#include "progpu_native_direct2d_font_axis_fixture.hpp"
#include "progpu_native_direct2d_cff_font_fixture.hpp"

namespace progpu::native::direct2d::tests {

enum class cff_pixel_path { original, prepared, independent_geometry, prepared_geometry, original_design_advances, prepared_outline };
inline constexpr std::array cff_pixel_fonts{cff_font_kind::cff1_default, cff_font_kind::cff1_affine,
    cff_font_kind::cff1_cid, cff_font_kind::cff1_cid_inherited, cff_font_kind::cff2_static,
    cff_font_kind::cff2_variable_fixed, cff_font_kind::cff2_variable_hvar};

inline compat::matrix_3x2_f cff_pixel_transform(std::uint32_t variant)
{
    return variant == 0U ? compat::matrix_3x2_f{1, 0, 0, 1, 0, 0} : variant == 1U
        ? compat::matrix_3x2_f{1, 0, 0, 1, 0.25F, 0.5F}
        : compat::matrix_3x2_f{1, 0.25F, -0.125F, 1, 7, 9};
}

template<class Require>
void append_independent_cff_contours(compat::geometry_sink* sink, cff_font_kind kind,
    std::size_t instance, bool nominal, Require require)
{
    constexpr std::array<std::uint16_t, 3U> glyphs{1U, 0U, 2U};
    constexpr std::array<float, 3U> supplied{12, -3, 9};
    float pen = 0;
    for (std::size_t occurrence = 0U; occurrence < glyphs.size(); ++occurrence) {
        const auto expected = expected_source_cff_glyph(kind, instance, glyphs[occurrence]);
        const float x = 4.0F + pen + (occurrence == 2U ? -0.75F : 0.0F);
        const float y = occurrence == 2U ? 25.5F : 28.0F;
        const auto point = [&](progpu_native_point p) { return compat::point_2f{p.x / 32.0F + x, y - p.y / 32.0F}; };
        if (expected.count != 0U) {
            require(expected.count <= expected.segments.size(), "independent CFF segment inventory");
            sink->BeginFigure(point(expected.segments[0].p0), compat::figure_begin::filled);
            for (std::uint32_t index = 0U; index < expected.count; ++index) {
                const auto& segment = expected.segments[index];
                if (segment.kind == PROGPU_NATIVE_PATH_SEGMENT_CUBIC) {
                    const compat::bezier_segment curve{point(segment.p1), point(segment.p2), point(segment.p3)};
                    sink->AddBezier(&curve);
                } else {
                    require(segment.kind == PROGPU_NATIVE_PATH_SEGMENT_LINE, "independent CFF line/cubic only");
                    sink->AddLine(point(segment.p1));
                }
            }
            sink->EndFigure(compat::figure_end::closed);
        }
        pen += nominal ? expected.advance / 32.0F : supplied[occurrence];
    }
}

template<class Require>
void record_cff_pixel_case(compat::factory* factory, compat::render_target* target,
    const std::shared_ptr<prepared_original_font>& font, compat::rendering_parameters* parameters,
    cff_font_kind kind, std::size_t instance, bool nominal, std::uint32_t variant,
    cff_pixel_path path, Require require, compat::geometry* prepared_geometry = nullptr,
    const float* original_design_advances = nullptr)
{
    require(instance < cff_font_case_count(kind) && variant < 3U, "CFF pixel inventory");
    const compat::matrix_3x2_f identity{1, 0, 0, 1, 0, 0};
    const auto transform = cff_pixel_transform(variant);
    const compat::rectangle_f clip{9, 8, 48, 42};
    const compat::layer_parameters layer{{0, 0, 64, 64}, nullptr, compat::antialias_mode::aliased,
        identity, 0.5F, nullptr, compat::layer_options::none};
    constexpr compat::color_f black{0, 0, 0, 1}, red{1, 0, 0, 1};
    com::pointer<compat::solid_color_brush> brush;
    require(target->CreateSolidColorBrush(&red, nullptr, brush.put()) == com::ok, "CFF source brush");
    target->BeginDraw(); target->Clear(&black); target->SetTransform(&identity);
    target->SetAntialiasMode(variant == 0U ? compat::antialias_mode::aliased : compat::antialias_mode::per_primitive);
    target->SetTextAntialiasMode(variant == 0U ? compat::text_antialias_mode::aliased : compat::text_antialias_mode::grayscale);
    target->SetTextRenderingParams(parameters);
    if (variant == 2U) { target->PushAxisAlignedClip(&clip, compat::antialias_mode::aliased); target->PushLayer(&layer, nullptr); }
    target->SetTransform(&transform);
    if (path == cff_pixel_path::prepared_geometry) {
        require(prepared_geometry != nullptr, "original rasterizer CFF prepared geometry");
        target->FillGeometry(prepared_geometry, brush.get(), nullptr);
    } else if (path == cff_pixel_path::independent_geometry) {
        com::pointer<compat::path_geometry> geometry;
        com::pointer<compat::geometry_sink> sink;
        require(factory->CreatePathGeometry(geometry.put()) == com::ok && geometry->Open(sink.put()) == com::ok,
            "independent CFF geometry creation");
        sink->SetFillMode(compat::fill_mode::winding);
        append_independent_cff_contours(sink.get(), kind, instance, nominal, require);
        require(sink->Close() == com::ok, "independent CFF geometry close");
        target->FillGeometry(geometry.get(), brush.get(), nullptr);
    } else {
        const std::uint16_t indices[]{1U, 0U, 2U};
        const float advances[]{12, -3, 9};
        const compat::glyph_offset offsets[]{{0, 0}, {0, 0}, {-0.75F, 2.5F}};
        const bool design = path == cff_pixel_path::original_design_advances;
        require(!design || (nominal && original_design_advances != nullptr), "original CFF design advances required");
        const compat::glyph_run run{font->source()->face.get(), static_cast<float>(cff_font_units(kind)) / 32.0F,
            3U, indices, design ? original_design_advances : nominal ? nullptr : advances, offsets, 0, 2U};
        if (path == cff_pixel_path::prepared_outline) {
            draw_prepared_outline_geometry(factory, target, font, parameters, {4, 28}, run, brush.get(), require);
        } else if (path == cff_pixel_path::original || design) {
            target->DrawGlyphRun({4, 28}, &run, brush.get(), compat::measuring_mode::natural);
        } else {
            com::pointer<prepared_glyph_target> prepared;
            require(target->QueryInterface(prepared_glyph_target_id, reinterpret_cast<void**>(prepared.put())) == com::ok &&
                prepared->DrawOwnedGlyphRun(font, {4, 28}, &run, brush.get(), compat::measuring_mode::natural) == com::ok,
                "actual CFF prepared retained draw");
        }
    }
    if (variant == 2U) { target->PopLayer(); target->PopAxisAlignedClip(); }
    require(target->EndDraw(nullptr, nullptr) == com::ok, "CFF prepared EndDraw");
    target->SetTextRenderingParams(nullptr);
}

template<class Render, class Require>
void verify_cff_glyph_pixels(Render render, Require require)
{
    std::uint32_t original_frame = 129U;
    com::pointer<compat::factory> factory;
    com::pointer<compat::scene_factory_native> scene_factory;
    require(compat::create_factory(factory.put()) == com::ok &&
        factory.as(compat::scene_factory_native_interface_id, scene_factory) == com::ok, "CFF source factory");
    rendering_parameters parameters; parameters.mode = compat::rendering_mode::outline;
    std::uint64_t generation = 0U;
    std::size_t cases = 0U;
    for (const auto kind : cff_pixel_fonts) {
        for (std::size_t instance = 0U; instance < cff_font_case_count(kind); ++instance) {
            font_stream stream; stream.bytes = make_cff_font(kind); stream.declared_size = stream.bytes.size();
            font_loader loader; loader.stream = &stream;
            font_file file; file.loader = &loader;
            font_face5 face; face.files = {&file}; face.declared_count = 1U;
            face.type = 0U; face.index = 0U; face.simulations = 0U; face.glyph_count = 3U;
            face.variable = cff_font_case_count(kind) != 1U;
            face.axes = {{0x74686777U, cff_font_weight(instance)}};
            std::shared_ptr<const original_font_capture> source;
            std::shared_ptr<prepared_original_font> font;
            require(capture_original_font(&face, source) == com::ok && prepared_original_font::create(source, font) == com::ok,
                "CFF retained original file/matrix/axis owner");
            const auto reads = stream.reads, values = face.value_reads;
            face.count_result = compat::not_implemented; face.values_result = compat::not_implemented;
            face.axes[0].value = -12345; stream.bytes.assign(16U, std::byte{0});
            for (const bool nominal : {false, true}) {
                const auto variant = static_cast<std::uint32_t>((instance + (nominal ? 1U : 0U)) % 3U);
                std::array<std::vector<std::byte>, 3U> scenes;
                std::array<progpu_native_scene_header, 3U> headers{};
                for (std::uint32_t reference = 0U; reference < 3U; ++reference) {
                    const compat::scene_render_target_properties properties{64U, 64U, 96, 96, 0x95D8U, ++generation};
                    com::pointer<compat::render_target> target;
                    com::pointer<compat::scene_render_target_native> scene;
                    require(scene_factory->CreateSceneRenderTarget(&properties, target.put()) == com::ok &&
                        target.as(compat::scene_render_target_native_interface_id, scene) == com::ok, "CFF source target");
                    record_cff_pixel_case(factory.get(), target.get(), font, &parameters, kind, instance, nominal, variant,
                        reference == 0U ? cff_pixel_path::prepared : reference == 1U
                        ? cff_pixel_path::independent_geometry : cff_pixel_path::prepared_outline, require);
                    require(export_copy_scene(scene.get(), scenes[reference]) && read_scene_value(scenes[reference], 0U, headers[reference]),
                        "CFF immutable scene export");
                }
                const auto draws = cff_source_has_ink(kind) ? 1U : 0U;
                const auto cold = verify_original_glyph_frame(render, require, original_frame, scenes, headers, draws);
                original_frame += 1U;
                bool ink = false;
                for (std::size_t pixel = 0U; pixel < cold.size(); pixel += 4U) {
                    require(cold[pixel + 1U] == 0U && cold[pixel + 2U] == 0U && cold[pixel + 3U] == 255U,
                        "CFF untouched channels and opaque alpha");
                    ink = ink || cold[pixel] != 0U;
                }
                require(ink == cff_source_has_ink(kind) && cold[0] == 0U && cold[1] == 0U && cold[2] == 0U && cold[3] == 255U,
                    "CFF original source ink admission and untouched background");
                ++cases;
            }
            require(stream.reads == reads && face.value_reads == values && face.outline_calls == 0U &&
                face.table_calls == 0U && face.unused_calls == 0U && font->cached_glyph_count() == 3U &&
                source->axis_values[0].value == cff_font_weight(instance), "CFF immutable cache requires no source callbacks");
        }
    }
    require(cases == 22U, "CFF independent source configuration count");
    require(original_frame == 151U, "complete original cff_glyph frame inventory");
}
} // namespace progpu::native::direct2d::tests
