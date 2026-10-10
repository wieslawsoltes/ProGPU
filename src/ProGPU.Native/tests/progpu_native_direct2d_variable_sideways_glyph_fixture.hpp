#pragma once

#include "progpu_native_direct2d_sideways_glyph_fixture.hpp"
#include "progpu_native_direct2d_font_axis_fixture.hpp"

namespace progpu::native::direct2d::tests {

inline constexpr std::array<vertical_font_options, 8U> variable_sideways_pixel_fonts{{
    {vertical_font_kind::truetype_variable, false, false, false, false, false},
    {vertical_font_kind::truetype_variable, true, false, false, false, false},
    {vertical_font_kind::truetype_variable, false, true, false, false, false},
    {vertical_font_kind::truetype_variable, true, true, true, false, false},
    {vertical_font_kind::cff2_variable, false, false, false, false, true},
    {vertical_font_kind::cff2_variable, true, false, false, false, true},
    {vertical_font_kind::cff2_variable, false, true, false, false, true},
    {vertical_font_kind::cff2_variable, true, true, false, true, true}}};

inline std::array<compat::rectangle_f, 2U> variable_sideways_pixel_rectangles(
    vertical_font_options options, std::size_t instance, bool nominal)
{
    // Independent literal design-instance boxes at scale1/64. No parser,
    // prepared geometry, variation walker or product placement enters here.
    if (instance >= 5U || options.vvar_precedence_discriminator)
        throw std::invalid_argument("variable sideways independent inventory");
    constexpr std::array<compat::rectangle_f, 5U> first{{
        {6.4375F,19.5F,12.6875F,23.875F}, {6.59375F,19.25F,13.21875F,24},
        {6.75F,19,13.75F,24.125F}, {6.359375F,19.625F,12.421875F,23.8125F},
        {6.28125F,19.75F,12.15625F,23.75F}}};
    constexpr std::array<compat::rectangle_f, 5U> second{{
        {4.5F,20.3125F,12.3125F,23.4375F}, {4.40625F,20.15625F,12.71875F,23.65625F},
        {4.3125F,20,13.125F,23.875F}, {4.546875F,20.390625F,12.109375F,23.328125F},
        {4.59375F,20.46875F,11.90625F,23.21875F}}};
    constexpr std::array<float, 5U> variable_pen{29.6875F,30.9375F,32.1875F,29.0625F,28.4375F};
    std::array<compat::rectangle_f, 2U> result{first.at(instance), second.at(instance)};
    const bool cff = options.kind == vertical_font_kind::cff2_variable;
    if (cff) {
        result = {{{9.5625F,19.5F,15.8125F,23.875F}, {4.5F,20.3125F,12.3125F,23.4375F}}};
        // Original DirectWrite retains these VORG origins across all five
        // instances, including the font with a nonzero VVAR vOrg map.
    }
    const float pen = nominal ? ((cff && !options.vvar ? 29.6875F : variable_pen.at(instance)) -
        (options.compact_metrics ? 1.5625F : 0.0F)) : 13.0F;
    result[1].left += pen; result[1].right += pen;
    return result;
}

template<class Require>
void record_variable_sideways_pixel_case(compat::factory* factory, compat::render_target* target,
    const std::shared_ptr<prepared_original_font>& font, compat::rendering_parameters* parameters,
    vertical_font_options options, std::size_t instance, bool nominal, std::uint32_t variant,
    sideways_pixel_path path, Require require, compat::geometry* prepared_geometry = nullptr,
    const float* original_design_advances = nullptr)
{
    require(variant < 3U && instance < 5U && !options.vvar_precedence_discriminator &&
        (options.kind == vertical_font_kind::truetype_variable ||
            (options.kind == vertical_font_kind::cff2_variable && options.vorg)), "variable sideways source inventory");
    const compat::matrix_3x2_f identity{1,0,0,1,0,0};
    const auto transform = sideways_pixel_transform(variant);
    const compat::rectangle_f clip{3,4,44,44};
    const compat::layer_parameters layer{{0,0,64,64},nullptr,compat::antialias_mode::aliased,
        identity,0.5F,nullptr,compat::layer_options::none};
    constexpr compat::color_f black{0,0,0,1}, red{1,0,0,1};
    com::pointer<compat::solid_color_brush> brush;
    require(target->CreateSolidColorBrush(&red, nullptr, brush.put()) == com::ok, "variable sideways brush");
    target->BeginDraw(); target->Clear(&black); target->SetTransform(&identity);
    target->SetAntialiasMode(variant == 0U ? compat::antialias_mode::aliased : compat::antialias_mode::per_primitive);
    target->SetTextAntialiasMode(variant == 0U ? compat::text_antialias_mode::aliased : compat::text_antialias_mode::grayscale);
    target->SetTextRenderingParams(parameters);
    if (variant == 2U) { target->PushAxisAlignedClip(&clip, compat::antialias_mode::aliased); target->PushLayer(&layer, nullptr); }
    target->SetTransform(&transform);
    if (path == sideways_pixel_path::prepared_geometry) {
        require(prepared_geometry != nullptr, "variable sideways original prepared geometry");
        target->FillGeometry(prepared_geometry, brush.get(), nullptr);
    } else if (path == sideways_pixel_path::independent_geometry) {
        com::pointer<compat::path_geometry> geometry;
        com::pointer<compat::geometry_sink> sink;
        require(factory->CreatePathGeometry(geometry.put()) == com::ok && geometry->Open(sink.put()) == com::ok,
            "variable sideways independent geometry");
        sink->SetFillMode(compat::fill_mode::winding);
        for (const auto& rectangle : variable_sideways_pixel_rectangles(options, instance, nominal)) {
            sink->BeginFigure({rectangle.left,rectangle.bottom}, compat::figure_begin::filled);
            sink->AddLine({rectangle.right,rectangle.bottom}); sink->AddLine({rectangle.right,rectangle.top});
            sink->AddLine({rectangle.left,rectangle.top}); sink->EndFigure(compat::figure_end::closed);
        }
        require(sink->Close() == com::ok, "variable sideways independent sink");
        target->FillGeometry(geometry.get(), brush.get(), nullptr);
    } else {
        const std::uint16_t indices[]{1U,0U,2U};
        const float advances[]{16,-3,9};
        const compat::glyph_offset offsets[]{{0.25F,0.5F},{0,0},{-0.75F,2.5F}};
        const bool design_reference = path == sideways_pixel_path::original_design_advances;
        require(!design_reference || (nominal && original_design_advances != nullptr), "genuine original variable advances");
        const auto* selected = design_reference ? original_design_advances : nominal ? nullptr : advances;
        const compat::glyph_run run{font->source()->face.get(),15.625F,3U,indices,selected,offsets,variant == 1U ? -1 : 1,2U};
        if (path == sideways_pixel_path::prepared_outline)
            draw_prepared_outline_geometry(factory, target, font, parameters, {4, 20}, run, brush.get(), require);
        else if (path == sideways_pixel_path::original || design_reference)
            target->DrawGlyphRun({4,20}, &run, brush.get(), compat::measuring_mode::natural);
        else {
            com::pointer<prepared_glyph_target> prepared;
            require(target->QueryInterface(prepared_glyph_target_id, reinterpret_cast<void**>(prepared.put())) == com::ok &&
                prepared->DrawOwnedGlyphRun(font, {4,20}, &run, brush.get(), compat::measuring_mode::natural) == com::ok,
                "actual retained variable sideways draw");
        }
    }
    if (variant == 2U) { target->PopLayer(); target->PopAxisAlignedClip(); }
    require(target->EndDraw(nullptr,nullptr) == com::ok, "variable sideways EndDraw");
    target->SetTextRenderingParams(nullptr);
}

template<class Render, class Require>
void verify_variable_sideways_glyph_pixels(Render render, Require require)
{
    std::uint32_t original_frame = 199U;
    std::uint64_t generation = 0U;
    for (const auto options : variable_sideways_pixel_fonts) for (std::size_t instance = 0U; instance < 5U; ++instance) {
        font_stream stream; stream.bytes = make_vertical_font(options); stream.declared_size = stream.bytes.size();
        font_loader loader; loader.stream = &stream;
        font_file file; file.loader = &loader;
        font_face5 face; face.files = {&file}; face.declared_count = 1U;
        face.type = options.kind == vertical_font_kind::cff2_variable ? 0U : 1U;
        face.index = 0U; face.simulations = 0U; face.glyph_count = 3U; face.variable = true;
        face.axes = {{0x74686777U, vertical_font_weights.at(instance)}};
        std::shared_ptr<const original_font_capture> source;
        std::shared_ptr<prepared_original_font> font;
        require(capture_original_font(&face, source) == com::ok && prepared_original_font::create(source, font) == com::ok,
            "variable sideways original font/axis owner");
        const auto reads = stream.reads, axis_reads = face.value_reads;
        face.count_result = compat::not_implemented; face.axes[0].value = 123; stream.bytes.assign(1U, std::byte{0xFF});
        rendering_parameters parameters; parameters.mode = compat::rendering_mode::outline;
        com::pointer<compat::factory> factory;
        com::pointer<compat::scene_factory_native> scenes;
        require(compat::create_factory(factory.put()) == com::ok &&
            factory.as(compat::scene_factory_native_interface_id, scenes) == com::ok, "variable sideways factory");
        for (const bool nominal : {false,true}) {
            const auto variant = static_cast<std::uint32_t>((instance + static_cast<std::size_t>(nominal)) % 3U);
            std::array<std::vector<std::byte>, 3U> bytes;
            std::array<progpu_native_scene_header, 3U> headers{};
            for (std::size_t reference = 0U; reference < 3U; ++reference) {
                const compat::scene_render_target_properties properties{64U,64U,96,96,0x95DFU,++generation};
                com::pointer<compat::render_target> target;
                com::pointer<compat::scene_render_target_native> scene;
                require(scenes->CreateSceneRenderTarget(&properties,target.put()) == com::ok &&
                    target.as(compat::scene_render_target_native_interface_id,scene) == com::ok, "variable sideways target");
                record_variable_sideways_pixel_case(factory.get(), target.get(), font, &parameters, options, instance, nominal,
                    variant, reference == 0U ? sideways_pixel_path::prepared : reference == 1U
                        ? sideways_pixel_path::independent_geometry : sideways_pixel_path::prepared_outline, require);
                require(export_copy_scene(scene.get(), bytes[reference]) && read_scene_value(bytes[reference],0U,headers[reference]),
                    "variable sideways immutable scene");
            }
            const auto cold = verify_original_glyph_frame(render, require, original_frame, bytes, headers);
            original_frame += 1U;
            bool ink = false;
            for (std::size_t pixel = 0U; pixel < cold.size(); pixel += 4U) {
                require(cold[pixel+1U] == 0U && cold[pixel+2U] == 0U && cold[pixel+3U] == 255U,
                    "variable sideways exact channel/opaque background ownership");
                ink |= cold[pixel] != 0U;
            }
            require(ink && cold[0] == 0U, "variable sideways nonempty ink and untouched corner");
        }
        require(stream.reads == reads && face.value_reads == axis_reads && face.outline_calls == 0U &&
            face.table_calls == 0U && font->cached_glyph_count() == 3U, "variable sideways retained generation and no callbacks");
    }
    require(original_frame == 279U, "complete original variable_sideways_glyph frame inventory");
}
} // namespace progpu::native::direct2d::tests
