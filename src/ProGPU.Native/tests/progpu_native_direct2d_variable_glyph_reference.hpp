#pragma once

#include "progpu_native_direct2d_variable_glyph_fixture.hpp"
#include <bit>

// Genuine Windows SDK declarations precede this header. All instances and
// original outlines below come from the system DirectWrite implementation;
// native prepared output is a compared result, never the expected geometry.
namespace progpu::native::direct2d::tests {

template<class Require>
void verify_original_variable_glyph_pixels(ID2D1DeviceContext* source_context,
    IDWriteFactory* write_factory, Require require)
{
    using Microsoft::WRL::ComPtr;
    ComPtr<IDWriteFactory5> extended_factory;
    require(write_factory->QueryInterface(IID_PPV_ARGS(extended_factory.GetAddressOf())) == S_OK,
        "original variable font factory");
    ComPtr<IDWriteInMemoryFontFileLoader> loader;
    require(extended_factory->CreateInMemoryFontFileLoader(loader.GetAddressOf()) == S_OK &&
        write_factory->RegisterFontFileLoader(loader.Get()) == S_OK, "original variable loader registration");
    struct unregister_loader final {
        IDWriteFactory* factory;
        IDWriteFontFileLoader* loader;
        Require& check;
        ~unregister_loader() { check(factory->UnregisterFontFileLoader(loader) == S_OK, "original variable loader retirement"); }
    } registered{write_factory, loader.Get(), require};

    ComPtr<ID2D1Device> device;
    source_context->GetDevice(device.GetAddressOf());
    ComPtr<ID2D1DeviceContext> context;
    require(device->CreateDeviceContext(D2D1_DEVICE_CONTEXT_OPTIONS_NONE, context.GetAddressOf()) == S_OK,
        "original variable independent context");
    context->SetDpi(96, 96);
    ComPtr<ID2D1Factory> factory;
    context->GetFactory(factory.GetAddressOf());
    com::pointer<compat::factory> typed_factory;
    com::pointer<compat::render_target> typed_target;
    require(factory->QueryInterface(compat::factory_interface_id, reinterpret_cast<void**>(typed_factory.put())) == S_OK &&
        context->QueryInterface(compat::render_target_interface_id, reinterpret_cast<void**>(typed_target.put())) == S_OK,
        "original variable typed target/factory");
    const auto target_properties = D2D1::BitmapProperties1(D2D1_BITMAP_OPTIONS_TARGET,
        D2D1::PixelFormat(DXGI_FORMAT_B8G8R8A8_UNORM, D2D1_ALPHA_MODE_PREMULTIPLIED), 96, 96);
    const auto read_properties = D2D1::BitmapProperties1(D2D1_BITMAP_OPTIONS_CPU_READ | D2D1_BITMAP_OPTIONS_CANNOT_DRAW,
        target_properties.pixelFormat, 96, 96);
    ComPtr<ID2D1Bitmap1> target, readback;
    require(context->CreateBitmap({64U, 64U}, nullptr, 0U, &target_properties, target.GetAddressOf()) == S_OK &&
        context->CreateBitmap({64U, 64U}, nullptr, 0U, &read_properties, readback.GetAddressOf()) == S_OK,
        "original variable pixel targets");
    ComPtr<IDWriteRenderingParams> parameters;
    require(write_factory->CreateCustomRenderingParams(1, 0, 0, DWRITE_PIXEL_GEOMETRY_FLAT,
        DWRITE_RENDERING_MODE_OUTLINE, parameters.GetAddressOf()) == S_OK, "original variable OUTLINE parameters");
    com::pointer<compat::rendering_parameters> typed_parameters;
    require(parameters->QueryInterface(compat::rendering_parameters_interface_id,
        reinterpret_cast<void**>(typed_parameters.put())) == S_OK, "original variable typed OUTLINE parameters");
    const auto copy_pixels = [&] {
        context->SetTarget(nullptr);
        require(readback->CopyFromBitmap(nullptr, target.Get(), nullptr) == S_OK, "original variable pixel copy");
        D2D1_MAPPED_RECT mapped{};
        require(readback->Map(D2D1_MAP_OPTIONS_READ, &mapped) == S_OK && mapped.pitch >= 256U,
            "original variable pixel map");
        std::vector<std::uint8_t> pixels(64U * 256U);
        for (std::size_t row = 0U; row < 64U; ++row)
            std::memcpy(pixels.data() + row * 256U, mapped.bits + row * mapped.pitch, 256U);
        require(readback->Unmap() == S_OK, "original variable pixel unmap");
        return pixels;
    };

    // Preserve the original forty LTR observations, then request the separate
    // forty RTL runs from genuine SDK instances of the same immutable bytes.
    for (const bool right_to_left : {false, true}) {
    for (const auto options : variable_pixel_font_options) {
        const auto bytes = make_variable_font(options);
        ComPtr<IDWriteFontFile> file;
        // Null owner makes the original SDK own its copy of these authored
        // bytes. Registration outlives every source and native context below.
        require(loader->CreateInMemoryFontFileReference(write_factory, bytes.data(),
            static_cast<UINT32>(bytes.size()), nullptr, file.GetAddressOf()) == S_OK,
            "original variable authored font file");
        IDWriteFontFile* files[]{file.Get()};
        ComPtr<IDWriteFontFace> default_face;
        require(write_factory->CreateFontFace(DWRITE_FONT_FACE_TYPE_TRUETYPE, 1U, files, 0U,
            DWRITE_FONT_SIMULATIONS_NONE, default_face.GetAddressOf()) == S_OK, "original variable default face");
        ComPtr<IDWriteFontFace5> default_extended;
        ComPtr<IDWriteFontResource> resource;
        require(default_face.As(&default_extended) == S_OK && default_extended->HasVariations() &&
            default_extended->GetFontResource(resource.GetAddressOf()) == S_OK,
            "original variable genuine Face5 resource");

        const auto create_instance = [&](float weight, ComPtr<IDWriteFontFace5>& face,
            std::shared_ptr<const original_font_capture>& captured) {
            const DWRITE_FONT_AXIS_VALUE coordinate{DWRITE_FONT_AXIS_TAG_WEIGHT, weight};
            require(resource->CreateFontFace(DWRITE_FONT_SIMULATIONS_NONE, &coordinate, 1U,
                face.GetAddressOf()) == S_OK && face->HasVariations(), "original exact axis instance creation");
            const auto count = face->GetFontAxisValueCount();
            require(count > 0U && count <= original_font_capture::maximum_axes, "original axis inventory bound");
            std::vector<DWRITE_FONT_AXIS_VALUE> observed(count);
            require(face->GetFontAxisValues(observed.data(), count) == S_OK, "original actual axis instance values");
            com::pointer<compat::font_face> typed_face;
            require(face->QueryInterface(compat::font_face_interface_id, reinterpret_cast<void**>(typed_face.put())) == S_OK &&
                capture_original_font(typed_face.get(), captured) == S_OK && captured->axis_values_available &&
                captured->has_variations && captured->axis_values.size() == observed.size() &&
                captured->files.size() == 1U && captured->files[0] == bytes,
                "original variable capture owns actual complete file and instance");
            bool found_weight = false;
            for (std::size_t index = 0U; index < observed.size(); ++index) {
                require(captured->axis_values[index].tag == static_cast<std::uint32_t>(observed[index].axisTag) &&
                    std::bit_cast<std::uint32_t>(captured->axis_values[index].value) ==
                        std::bit_cast<std::uint32_t>(observed[index].value),
                    "original variable canonical axis order/tag/bits");
                if (observed[index].axisTag == DWRITE_FONT_AXIS_TAG_WEIGHT) {
                    require(!found_weight && std::bit_cast<std::uint32_t>(observed[index].value) ==
                        std::bit_cast<std::uint32_t>(weight), "original in-range user-coordinate bits");
                    found_weight = true;
                }
            }
            require(found_weight, "original actual wght axis not inferred from static descriptors");
        };

        // Fine user coordinates are independently requested and read back by
        // the SDK. This control does not manufacture a fractional outline or
        // treat an original INT32 design-advance result as sub-unit precision.
        {
            ComPtr<IDWriteFontFace5> fine_face;
            std::shared_ptr<const original_font_capture> fine_capture;
            create_instance(variable_precision_weight, fine_face, fine_capture);
        }
        for (std::size_t case_index = 0U; case_index < variable_font_cases.size(); ++case_index) {
            ComPtr<IDWriteFontFace5> face;
            std::shared_ptr<const original_font_capture> captured;
            create_instance(variable_font_cases[case_index].weight, face, captured);
            std::shared_ptr<prepared_original_font> prepared;
            require(prepared_original_font::create(captured, prepared) == S_OK,
                "original variable retained native font context");
            const std::uint16_t indices[]{1U, 0U, 2U};
            std::array<INT32, 3U> design_advances{};
            DWRITE_FONT_METRICS metrics{};
            static_cast<IDWriteFontFace*>(face.Get())->GetMetrics(&metrics);
            require(metrics.designUnitsPerEm == 1000U &&
                face->GetDesignGlyphAdvances(3U, indices, design_advances.data(), FALSE) == S_OK,
                "original variable design advance query");
            std::array<float, 3U> original_advances{};
            for (std::size_t index = 0U; index < 3U; ++index) {
                const auto expected = expected_variable_glyph(indices[index], case_index);
                require(static_cast<float>(design_advances[index]) == expected.advance,
                    "original variable design advances including empty glyph");
                original_advances[index] = static_cast<float>(design_advances[index]) * (31.25F / 1000.0F);
            }
            // Direct original outline observation in design units. Nonzero
            // left phantom displacement must not disappear under head bit1.
            for (const std::uint16_t glyph : {std::uint16_t{1}, std::uint16_t{2}}) {
                ComPtr<ID2D1PathGeometry> original_geometry;
                ComPtr<ID2D1GeometrySink> original_sink;
                require(factory->CreatePathGeometry(original_geometry.GetAddressOf()) == S_OK &&
                    original_geometry->Open(original_sink.GetAddressOf()) == S_OK,
                    "original variable direct outline sink");
                const float no_following_advance = 0.0F;
                require(face->GetGlyphRunOutline(1000.0F, &glyph, &no_following_advance, nullptr,
                    1U, FALSE, FALSE, original_sink.Get()) == S_OK && original_sink->Close() == S_OK,
                    "original variable direct outline extraction");
                D2D1_RECT_F bounds{};
                const auto expected = expected_variable_glyph(glyph, case_index);
                require(original_geometry->GetBounds(nullptr, &bounds) == S_OK &&
                    bounds.left == expected.x_min - expected.horizontal_origin && bounds.top == -expected.y_max &&
                    bounds.right == expected.x_max - expected.horizontal_origin && bounds.bottom == -expected.y_min,
                    "original variable independent contour/phantom bounds");
            }
            for (const bool nominal : {false, true}) {
                const auto variant = static_cast<std::uint32_t>((case_index + (nominal ? 1U : 0U)) % 3U);
                const float advances[]{12, -3, right_to_left ? 20.0F : 9.0F};
                const compat::glyph_offset offsets[]{{0,0}, {0,0}, {-0.75F,2.5F}};
                const compat::glyph_run run{captured->face.get(), 31.25F, 3U, indices,
                    nominal ? nullptr : advances, offsets, 0, right_to_left ? 3U : 2U};
                original_glyph_target frame;
                frame.identity = com::pointer<com::unknown>(typed_target.get());
                frame.generation = (right_to_left ? 10U : 0U) + case_index * 2U + (nominal ? 2U : 1U);
                frame.transform = variable_pixel_transform(variant); frame.baseline = {right_to_left ? 56.0F : 4.0F,28};
                frame.pixels = {64U,64U}; frame.dpi_x = 96; frame.dpi_y = 96;
                frame.format = {87U, compat::alpha_mode::premultiplied};
                frame.antialias = variant == 0U ? compat::text_antialias_mode::aliased : compat::text_antialias_mode::grayscale;
                std::shared_ptr<const original_glyph_request> request;
                std::shared_ptr<const prepared_original_glyph_run> glyphs;
                require(capture_original_glyph_request(captured, run, compat::measuring_mode::natural,
                    typed_parameters.get(), frame, request) == S_OK && prepared->prepare(request, glyphs) == S_OK &&
                    (glyphs->request().glyphs.advances() == nullptr) == nominal,
                    "original variable prepared source occurrence identity");
                if (right_to_left) {
                    const auto& retained = glyphs->request();
                    require(retained.font == captured && retained.bidi_level == 3U && retained.sideways == 0 &&
                        retained.glyphs.count() == 3U && retained.glyphs.indices()[0] == 1U &&
                        retained.glyphs.indices()[1] == 0U && retained.glyphs.indices()[2] == 2U &&
                        retained.glyphs.offsets() != nullptr && retained.glyphs.offsets()[2].advance_offset == -0.75F &&
                        retained.glyphs.offsets()[2].ascender_offset == 2.5F &&
                        (nominal || (retained.glyphs.advances()[0] == 12.0F && retained.glyphs.advances()[1] == -3.0F &&
                                     retained.glyphs.advances()[2] == 20.0F)),
                        "variable RTL retains logical IDs, exact source owner, signed advances and offsets");
                    const auto expected = variable_rtl_pixel_rectangles(case_index, nominal);
                    require(glyphs->segments().size() == 8U, "variable RTL two original ink occurrences");
                    for (std::size_t occurrence = 0U; occurrence < 2U; ++occurrence) {
                        const auto& first = glyphs->segments()[occurrence * 4U];
                        compat::rectangle_f bounds{first.p0.x, first.p0.y, first.p0.x, first.p0.y};
                        for (std::size_t edge = 0U; edge < 4U; ++edge) {
                            const auto& segment = glyphs->segments()[occurrence * 4U + edge];
                            require(segment.kind == PROGPU_NATIVE_PATH_SEGMENT_LINE,
                                "variable RTL original rectangular contour kind");
                            for (const auto point : {segment.p0, segment.p1}) {
                                bounds.left = std::min(bounds.left, point.x); bounds.right = std::max(bounds.right, point.x);
                                bounds.top = std::min(bounds.top, point.y); bounds.bottom = std::max(bounds.bottom, point.y);
                            }
                        }
                        require(bounds.left == expected[occurrence].left && bounds.top == expected[occurrence].top &&
                            bounds.right == expected[occurrence].right && bounds.bottom == expected[occurrence].bottom,
                            "variable RTL preserves each logical occurrence's independent varied origin/advance rectangle");
                    }
                    ComPtr<ID2D1PathGeometry> original_rtl_geometry;
                    ComPtr<ID2D1GeometrySink> original_rtl_sink;
                    const DWRITE_GLYPH_OFFSET original_offsets[]{{0,0}, {0,0}, {-0.75F,2.5F}};
                    require(factory->CreatePathGeometry(original_rtl_geometry.GetAddressOf()) == S_OK &&
                        original_rtl_geometry->Open(original_rtl_sink.GetAddressOf()) == S_OK &&
                        face->GetGlyphRunOutline(31.25F, indices, nominal ? nullptr : advances, original_offsets,
                            3U, FALSE, TRUE, original_rtl_sink.Get()) == S_OK && original_rtl_sink->Close() == S_OK,
                        "original variable RTL logical-run outline observation");
                    D2D1_RECT_F original_bounds{};
                    require(original_rtl_geometry->GetBounds(nullptr, &original_bounds) == S_OK &&
                        original_bounds.left == std::min(expected[0].left, expected[1].left) - 56.0F &&
                        original_bounds.top == std::min(expected[0].top, expected[1].top) - 28.0F &&
                        original_bounds.right == std::max(expected[0].right, expected[1].right) - 56.0F &&
                        original_bounds.bottom == std::max(expected[0].bottom, expected[1].bottom) - 28.0F,
                        "original variable RTL outline bounds preserve current/empty advances and signed offsets");
                }
                com::pointer<compat::path_geometry> geometry;
                com::pointer<compat::geometry_sink> sink;
                require(typed_factory->CreatePathGeometry(geometry.put()) == S_OK && geometry->Open(sink.put()) == S_OK &&
                    glyphs->segments().size() == 8U, "original variable prepared outline sink");
                sink->SetFillMode(compat::fill_mode::winding);
                for (std::size_t index = 0U; index < glyphs->segments().size(); ++index) {
                    const auto& segment = glyphs->segments()[index];
                    require(segment.kind == PROGPU_NATIVE_PATH_SEGMENT_LINE, "original variable authored line segments");
                    if (index % 4U == 0U) sink->BeginFigure({segment.p0.x,segment.p0.y}, compat::figure_begin::filled);
                    sink->AddLine({segment.p1.x,segment.p1.y});
                    if (index % 4U == 3U) sink->EndFigure(compat::figure_end::closed);
                }
                require(sink->Close() == S_OK, "original variable prepared outline close");
                const std::array paths{variable_pixel_path::original, variable_pixel_path::independent_geometry,
                    variable_pixel_path::prepared_geometry, variable_pixel_path::original_design_advances};
                std::array<std::vector<std::uint8_t>, 4U> pixels;
                for (std::size_t index = 0U; index < (nominal ? 4U : 3U); ++index) {
                    context->SetTarget(target.Get());
                    record_variable_pixel_case(typed_factory.get(), typed_target.get(), prepared, typed_parameters.get(),
                        case_index, nominal, variant, paths[index], require, geometry.get(), original_advances.data(), right_to_left);
                    pixels[index] = copy_pixels();
                }
                require(pixels[0] == pixels[1] && pixels[0] == pixels[2],
                    "original variable DrawGlyphRun/independent/prepared full-byte mismatch");
                if (nominal) require(pixels[0] == pixels[3],
                    "original variable null advance differs from original design advance");
            }
        }
    }
    }
}
} // namespace progpu::native::direct2d::tests
