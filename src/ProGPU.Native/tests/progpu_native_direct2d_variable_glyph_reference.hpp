#pragma once

#include "progpu_native_direct2d_variable_glyph_fixture.hpp"
#include "progpu_native_direct2d_compact_hvar_fixture.hpp"
#include <bit>

// Genuine Windows SDK declarations precede this header. All instances and
// original outlines below come from the system DirectWrite implementation;
// native prepared output is a compared result, never the expected geometry.
namespace progpu::native::direct2d::tests {

template<class Require>
void verify_original_compact_hvar(IDWriteFactory5* factory, IDWriteInMemoryFontFileLoader* loader,
    ID2D1Factory* drawing_factory, compat::render_target* target, compat::rendering_parameters* parameters,
    Require require)
{
    using Microsoft::WRL::ComPtr;
    unsigned cases = 0U;
    for (const auto& control : compact_hvar_controls) {
        const auto bytes = make_compact_hvar_control(control.metric_count, control.map_kind);
        ComPtr<IDWriteFontFile> file;
        require(loader->CreateInMemoryFontFileReference(factory, bytes.data(), static_cast<UINT32>(bytes.size()),
            nullptr, file.GetAddressOf()) == S_OK, "original compact HVAR independent bytes");
        IDWriteFontFile* files[]{file.Get()};
        ComPtr<IDWriteFontFace> base;
        ComPtr<IDWriteFontFace5> base5, face;
        ComPtr<IDWriteFontResource> resource;
        const DWRITE_FONT_AXIS_VALUE axis{DWRITE_FONT_AXIS_TAG_WEIGHT, control.weight};
        require(factory->CreateFontFace(DWRITE_FONT_FACE_TYPE_TRUETYPE, 1U, files, 0U,
            DWRITE_FONT_SIMULATIONS_NONE, base.GetAddressOf()) == S_OK && base.As(&base5) == S_OK &&
            base5->GetFontResource(resource.GetAddressOf()) == S_OK &&
            resource->CreateFontFace(DWRITE_FONT_SIMULATIONS_NONE, &axis, 1U, face.GetAddressOf()) == S_OK,
            "original compact HVAR exact instance");
        constexpr UINT16 indices[]{0U, 1U, 2U};
        INT32 advances[3]{};
        require(face->GetDesignGlyphAdvances(3U, indices, advances, FALSE) == S_OK &&
            std::equal(std::begin(advances), std::end(advances), control.advances.begin()),
            "original compact HVAR independent source advances");
        com::pointer<compat::font_face> typed_face;
        require(face->QueryInterface(compat::font_face_interface_id, reinterpret_cast<void**>(typed_face.put())) == S_OK,
            "original compact HVAR typed face");
        std::shared_ptr<const original_font_capture> source;
        std::shared_ptr<prepared_original_font> prepared;
        require(capture_original_font(typed_face.get(), source) == S_OK &&
            prepared_original_font::create(source, prepared) == S_OK, "original compact HVAR retained source");
        const original_glyph_target frame{com::pointer<com::unknown>(target), 1U,
            {1, 0, 0, 1, 0, 0}, {0, 0}, {64U, 64U}, 96, 96, {}, compat::text_antialias_mode::aliased};
        // Reverse the ink IDs around the original empty glyph so its null
        // advance contributes to a following outline in both directions.
        constexpr UINT16 run_ids[]{2U, 0U, 1U};
        for (const bool rtl : {false, true}) {
            ComPtr<ID2D1PathGeometry> original;
            ComPtr<ID2D1GeometrySink> sink;
            require(drawing_factory->CreatePathGeometry(original.GetAddressOf()) == S_OK &&
                original->Open(sink.GetAddressOf()) == S_OK &&
                face->GetGlyphRunOutline(1000.0F, run_ids, nullptr, nullptr, 3U, FALSE, rtl, sink.Get()) == S_OK &&
                sink->Close() == S_OK, "original compact HVAR null-advance outline");
            D2D1_RECT_F original_bounds{};
            require(original->GetBounds(nullptr, &original_bounds) == S_OK, "original compact HVAR bounds");
            const compat::glyph_run run{typed_face.get(), 1000.0F, 3U, run_ids, nullptr, nullptr, 0, rtl ? 1U : 0U};
            std::shared_ptr<const original_glyph_request> request;
            std::shared_ptr<const prepared_original_glyph_run> glyphs;
            require(capture_original_glyph_request(source, run, compat::measuring_mode::natural,
                parameters, frame, request) == S_OK && prepared->prepare(request, glyphs) == S_OK &&
                glyphs->segments().size() == 8U, "original compact HVAR prepared occurrences");
            const auto& first = glyphs->segments()[0U];
            D2D1_RECT_F actual{first.p0.x, first.p0.y, first.p0.x, first.p0.y};
            for (const auto& segment : glyphs->segments()) {
                require(segment.kind == PROGPU_NATIVE_PATH_SEGMENT_LINE, "original compact HVAR line inventory");
                for (const auto point : {segment.p0, segment.p1}) {
                    actual.left = std::min(actual.left, point.x); actual.right = std::max(actual.right, point.x);
                    actual.top = std::min(actual.top, point.y); actual.bottom = std::max(actual.bottom, point.y);
                }
            }
            require(actual.left == original_bounds.left && actual.top == original_bounds.top &&
                actual.right == original_bounds.right && actual.bottom == original_bounds.bottom,
                "original compact HVAR complete source/prepared outline bounds");
        }
        ++cases;
    }
    require(cases == 45U, "original compact HVAR inventory");
    std::fprintf(stderr, "Original compact HVAR cases=%u outline-comparisons=%u\n", cases, cases * 2U);
}

template<class Require, class Compare>
void verify_original_variable_glyph_pixels(ID2D1DeviceContext* source_context,
    IDWriteFactory* write_factory, Require require, Compare compare)
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

    verify_original_compact_hvar(extended_factory.Get(), loader.Get(), factory.Get(),
        typed_target.get(), typed_parameters.get(), require);

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
                    require(!found_weight, "original unique wght axis");
                    if (weight != variable_precision_weight)
                        require(std::bit_cast<std::uint32_t>(observed[index].value) ==
                            std::bit_cast<std::uint32_t>(weight), "original pixel-instance user-coordinate bits");
                    found_weight = true;
                }
            }
            require(found_weight, "original actual wght axis not inferred from static descriptors");
        };

        // The source for capture is Face5's complete canonical axis result,
        // not the earlier CreateFontFace request. The original SDK rounds this
        // fine request before publishing its face. Keep the request unchanged,
        // preserve every actual source bit above, and independently prove that
        // a fresh face made from that canonical result reports identical bits.
        {
            ComPtr<IDWriteFontFace5> fine_face;
            std::shared_ptr<const original_font_capture> fine_capture;
            create_instance(variable_precision_weight, fine_face, fine_capture);
            std::vector<DWRITE_FONT_AXIS_VALUE> canonical(fine_face->GetFontAxisValueCount());
            require(fine_face->GetFontAxisValues(canonical.data(), static_cast<UINT32>(canonical.size())) == S_OK,
                "original fine canonical axis observation");
            ComPtr<IDWriteFontFace5> repeated_face;
            require(resource->CreateFontFace(DWRITE_FONT_SIMULATIONS_NONE, canonical.data(),
                static_cast<UINT32>(canonical.size()), repeated_face.GetAddressOf()) == S_OK &&
                repeated_face->GetFontAxisValueCount() == canonical.size(),
                "original independent canonical-axis instance");
            std::vector<DWRITE_FONT_AXIS_VALUE> repeated(canonical.size());
            require(repeated_face->GetFontAxisValues(repeated.data(), static_cast<UINT32>(repeated.size())) == S_OK,
                "original independent canonical-axis observation");
            for (std::size_t axis = 0U; axis < canonical.size(); ++axis) {
                require(canonical[axis].axisTag == repeated[axis].axisTag &&
                    std::bit_cast<std::uint32_t>(canonical[axis].value) == std::bit_cast<std::uint32_t>(repeated[axis].value),
                    "original canonical-axis round-trip tag/order/bits");
                if (canonical[axis].axisTag == DWRITE_FONT_AXIS_TAG_WEIGHT)
                    std::fprintf(stderr, "Original fine axis request=%08x Face5=%08x retained=%08x\n",
                        std::bit_cast<std::uint32_t>(variable_precision_weight),
                        std::bit_cast<std::uint32_t>(canonical[axis].value),
                        std::bit_cast<std::uint32_t>(fine_capture->axis_values[axis].value));
            }
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
                const auto expected_advance = expected_variable_source_advance(options, indices[index], case_index);
                if (static_cast<float>(design_advances[index]) != expected_advance)
                    std::fprintf(stderr, "Original variable advance hvar=%u compact=%u bearings=%u rtl=%u instance=%zu glyph=%u actual=%d expected=%g\n",
                        unsigned(options.hvar), unsigned(options.compact_metrics), unsigned(options.side_bearing_maps),
                        unsigned(right_to_left), case_index, unsigned(indices[index]), design_advances[index], expected_advance);
                compare(static_cast<float>(design_advances[index]) == expected_advance,
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
                    const auto expected = variable_rtl_pixel_rectangles(case_index, nominal, options);
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
                        case_index, nominal, variant, paths[index], require, geometry.get(), original_advances.data(), right_to_left, options);
                    pixels[index] = copy_pixels();
                }
                compare(pixels[0] == pixels[1] && pixels[0] == pixels[2],
                    "original variable DrawGlyphRun/independent/prepared full-byte mismatch");
                if (nominal) compare(pixels[0] == pixels[3],
                    "original variable null advance differs from original design advance");
            }
        }
    }
    }
}
} // namespace progpu::native::direct2d::tests
