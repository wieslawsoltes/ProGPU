#pragma once

#include "progpu_native_direct2d_sideways_glyph_fixture.hpp"
#include "progpu_native_direct2d_cff_vertical_fixture.hpp"

namespace progpu::native::direct2d::tests {

inline std::array<progpu_native_path_segment,4U> cff_contour_origin_segments(bool fractional, bool nominal)
{
    // Literal independent original DirectWrite coordinates: the observed
    // design origins are480/482 and340. Exact curve maxima remain300/301.5
    // and280 in their separate algebraic controls; no product calculation
    // supplies these source-placement expectations.
    const float first_endpoint = fractional ? 11.78125F : 11.75F;
    const float first_control = 5.5F;
    const float second_endpoint = nominal ? 38.5625F : 21.875F;
    const float second_control = nominal ? 32.3125F : 15.625F;
    return {{{{first_endpoint,23.875F},{first_control,23.875F},{first_control,19.5F},{first_endpoint,19.5F},
            PROGPU_NATIVE_PATH_SEGMENT_CUBIC,0,0,0},
        {{first_endpoint,19.5F},{first_endpoint,23.875F},{},{},PROGPU_NATIVE_PATH_SEGMENT_LINE,0,0,0},
        {{second_endpoint,22.96875F},{second_control,22.96875F},{second_control,19.84375F},{second_endpoint,19.84375F},
            PROGPU_NATIVE_PATH_SEGMENT_CUBIC,0,0,0},
        {{second_endpoint,19.84375F},{second_endpoint,22.96875F},{},{},PROGPU_NATIVE_PATH_SEGMENT_LINE,0,0,0}}};
}

template<class Require>
void append_cff_contour_origin_geometry(compat::geometry_sink* sink,
    std::span<const progpu_native_path_segment> segments, Require require)
{
    require(segments.size() == 4U, "CFF contour-origin two-cubic inventory");
    sink->SetFillMode(compat::fill_mode::winding);
    for (std::size_t index = 0U; index < segments.size(); ++index) {
        const auto& value = segments[index];
        if (index % 2U == 0U) {
            require(value.kind == PROGPU_NATIVE_PATH_SEGMENT_CUBIC, "CFF contour-origin original cubic");
            sink->BeginFigure({value.p0.x,value.p0.y},compat::figure_begin::filled);
            const compat::bezier_segment curve{{value.p1.x,value.p1.y},{value.p2.x,value.p2.y},{value.p3.x,value.p3.y}};
            sink->AddBezier(&curve);
        } else {
            require(value.kind == PROGPU_NATIVE_PATH_SEGMENT_LINE, "CFF contour-origin actual closing line");
            sink->AddLine({value.p1.x,value.p1.y}); sink->EndFigure(compat::figure_end::closed);
        }
    }
}

template<class Require>
void record_cff_contour_origin_pixel_case(compat::factory* factory, compat::render_target* target,
    const std::shared_ptr<prepared_original_font>& font, compat::rendering_parameters* parameters,
    bool fractional, bool nominal, std::uint32_t variant, sideways_pixel_path path, Require require,
    compat::geometry* prepared_geometry = nullptr, const float* original_design_advances = nullptr)
{
    require(variant < 3U, "CFF contour-origin frame inventory");
    const compat::matrix_3x2_f identity{1,0,0,1,0,0};
    const auto transform = sideways_pixel_transform(variant);
    const compat::rectangle_f clip{3,4,44,44};
    const compat::layer_parameters layer{{0,0,64,64},nullptr,compat::antialias_mode::aliased,
        identity,0.5F,nullptr,compat::layer_options::none};
    constexpr compat::color_f black{0,0,0,1}, red{1,0,0,1};
    com::pointer<compat::solid_color_brush> brush;
    require(target->CreateSolidColorBrush(&red,nullptr,brush.put()) == com::ok, "CFF contour-origin brush");
    target->BeginDraw(); target->Clear(&black); target->SetTransform(&identity);
    target->SetAntialiasMode(variant == 0U ? compat::antialias_mode::aliased : compat::antialias_mode::per_primitive);
    target->SetTextAntialiasMode(variant == 0U ? compat::text_antialias_mode::aliased : compat::text_antialias_mode::grayscale);
    target->SetTextRenderingParams(parameters);
    if (variant == 2U) { target->PushAxisAlignedClip(&clip,compat::antialias_mode::aliased); target->PushLayer(&layer,nullptr); }
    target->SetTransform(&transform);
    if (path == sideways_pixel_path::prepared_geometry) {
        require(prepared_geometry != nullptr, "CFF contour-origin retained geometry");
        target->FillGeometry(prepared_geometry,brush.get(),nullptr);
    } else if (path == sideways_pixel_path::independent_geometry) {
        com::pointer<compat::path_geometry> geometry; com::pointer<compat::geometry_sink> sink;
        require(factory->CreatePathGeometry(geometry.put()) == com::ok && geometry->Open(sink.put()) == com::ok,
            "CFF contour-origin independent cubic sink");
        append_cff_contour_origin_geometry(sink.get(),cff_contour_origin_segments(fractional,nominal),require);
        require(sink->Close() == com::ok, "CFF contour-origin independent close");
        target->FillGeometry(geometry.get(),brush.get(),nullptr);
    } else {
        const std::uint16_t indices[]{1U,0U,2U};
        const float advances[]{16,-3,9};
        const compat::glyph_offset offsets[]{{0.25F,0.5F},{0,0},{-0.75F,2.5F}};
        const bool design_reference = path == sideways_pixel_path::original_design_advances;
        require(!design_reference || (nominal && original_design_advances != nullptr), "actual original vertical advances required");
        const auto* selected = design_reference ? original_design_advances : nominal ? nullptr : advances;
        const compat::glyph_run run{font->source()->face.get(),15.625F,3U,indices,selected,offsets,variant == 1U ? -1 : 1,2U};
        if (path == sideways_pixel_path::original || design_reference)
            target->DrawGlyphRun({4,20},&run,brush.get(),compat::measuring_mode::natural);
        else {
            com::pointer<prepared_glyph_target> prepared;
            require(target->QueryInterface(prepared_glyph_target_id,reinterpret_cast<void**>(prepared.put())) == com::ok &&
                prepared->DrawOwnedGlyphRun(font,{4,20},&run,brush.get(),compat::measuring_mode::natural) == com::ok,
                "actual CFF contour-origin source draw");
        }
    }
    if (variant == 2U) { target->PopLayer(); target->PopAxisAlignedClip(); }
    require(target->EndDraw(nullptr,nullptr) == com::ok, "CFF contour-origin EndDraw");
    target->SetTextRenderingParams(nullptr);
}

template<class Render, class Require>
void verify_cff_contour_origin_pixels(Render render, Require require)
{
    std::uint64_t generation = 0U;
    for (const bool fractional : {false,true}) {
        font_stream stream; stream.bytes = make_cff_vertical_font(fractional); stream.declared_size = stream.bytes.size();
        font_loader loader; loader.stream = &stream; font_file file; file.loader = &loader;
        font_face face; face.files = {&file}; face.declared_count = 1U;
        face.type = 0U; face.index = 0U; face.simulations = 0U; face.glyph_count = 3U;
        std::shared_ptr<const original_font_capture> source; std::shared_ptr<prepared_original_font> font;
        require(capture_original_font(&face,source) == com::ok && prepared_original_font::create(source,font) == com::ok,
            "CFF contour-origin immutable source owner");
        const auto reads = stream.reads; face.count_result = compat::not_implemented;
        stream.bytes.assign(1U,std::byte{0xFF});
        rendering_parameters parameters; parameters.mode = compat::rendering_mode::outline;
        com::pointer<compat::factory> factory; com::pointer<compat::scene_factory_native> scenes;
        require(compat::create_factory(factory.put()) == com::ok &&
            factory.as(compat::scene_factory_native_interface_id,scenes) == com::ok, "CFF contour-origin factory");
        for (const bool nominal : {false,true}) for (std::uint32_t variant = 0U; variant < 3U; ++variant) {
            std::array<std::vector<std::byte>,2U> bytes;
            std::array<progpu_native_scene_header,2U> headers{};
            for (std::size_t reference = 0U; reference < 2U; ++reference) {
                const compat::scene_render_target_properties properties{64U,64U,96,96,0x95E1U,++generation};
                com::pointer<compat::render_target> target; com::pointer<compat::scene_render_target_native> scene;
                require(scenes->CreateSceneRenderTarget(&properties,target.put()) == com::ok &&
                    target.as(compat::scene_render_target_native_interface_id,scene) == com::ok, "CFF contour-origin target");
                record_cff_contour_origin_pixel_case(factory.get(),target.get(),font,&parameters,fractional,nominal,variant,
                    reference == 0U ? sideways_pixel_path::prepared : sideways_pixel_path::independent_geometry,require);
                require(export_copy_scene(scene.get(),bytes[reference]) && read_scene_value(bytes[reference],0U,headers[reference]),
                    "CFF contour-origin immutable scene");
            }
            const auto cold = render(false,bytes[0],headers[0]); const auto warm = render(false,bytes[0],headers[0]);
            const auto independent = render(true,bytes[1],headers[1]);
            require(cold.size() == 64U*256U && cold == warm && cold == independent,
                "CFF contour-origin full-byte cold/warm/independent frame");
            bool ink = false;
            for (std::size_t pixel = 0U; pixel < cold.size(); pixel += 4U) {
                require(cold[pixel+1U] == 0U && cold[pixel+2U] == 0U && cold[pixel+3U] == 255U,
                    "CFF contour-origin channels and alpha"); ink |= cold[pixel] != 0U;
            }
            require(ink && cold[0] == 0U, "CFF contour-origin nonempty output and untouched corner");
        }
        require(stream.reads == reads && face.outline_calls == 0U && face.table_calls == 0U && font->cached_glyph_count() == 3U,
            "CFF contour-origin retained warm ownership without new source calls");
    }
}
} // namespace progpu::native::direct2d::tests
