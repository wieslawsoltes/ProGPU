#pragma once

#include "progpu_native_direct2d_sideways_glyph_fixture.hpp"

namespace progpu::native::direct2d::tests {

// Genuine original SDK reference; caller owns the Windows COM/runtime scope.
// These controls are authored only until the final integrated reference runs.
// No original metric or output is replaced by a product-side font observation.
template<class Require, class Compare>
void verify_original_sideways_glyph_pixels(ID2D1DeviceContext* source_context,
    IDWriteFactory* write_factory, Require require, Compare compare)
{
    std::uint32_t original_frame = 151U;
    using Microsoft::WRL::ComPtr;
    ComPtr<IDWriteFactory5> extended_factory;
    require(write_factory->QueryInterface(IID_PPV_ARGS(extended_factory.GetAddressOf())) == S_OK,
        "original vertical reference factory");
    ComPtr<IDWriteInMemoryFontFileLoader> loader;
    require(extended_factory->CreateInMemoryFontFileLoader(loader.GetAddressOf()) == S_OK &&
        write_factory->RegisterFontFileLoader(loader.Get()) == S_OK, "original vertical loader registration");
    struct unregister_loader final {
        IDWriteFactory* factory; IDWriteFontFileLoader* loader; Require& check;
        ~unregister_loader() { check(factory->UnregisterFontFileLoader(loader) == S_OK, "original vertical loader retirement"); }
    } registered{write_factory,loader.Get(),require};
    ComPtr<ID2D1Device> device;
    source_context->GetDevice(device.GetAddressOf());
    ComPtr<ID2D1DeviceContext> context;
    require(device->CreateDeviceContext(D2D1_DEVICE_CONTEXT_OPTIONS_NONE,context.GetAddressOf()) == S_OK,
        "original sideways independent context");
    context->SetDpi(96,96);
    ComPtr<ID2D1Factory> factory;
    context->GetFactory(factory.GetAddressOf());
    com::pointer<compat::factory> typed_factory;
    com::pointer<compat::render_target> typed_target;
    require(factory->QueryInterface(compat::factory_interface_id,reinterpret_cast<void**>(typed_factory.put())) == S_OK &&
        context->QueryInterface(compat::render_target_interface_id,reinterpret_cast<void**>(typed_target.put())) == S_OK,
        "original sideways typed target identity");
    const auto target_properties = D2D1::BitmapProperties1(D2D1_BITMAP_OPTIONS_TARGET,
        D2D1::PixelFormat(DXGI_FORMAT_B8G8R8A8_UNORM,D2D1_ALPHA_MODE_PREMULTIPLIED),96,96);
    const auto read_properties = D2D1::BitmapProperties1(D2D1_BITMAP_OPTIONS_CPU_READ | D2D1_BITMAP_OPTIONS_CANNOT_DRAW,
        target_properties.pixelFormat,96,96);
    ComPtr<ID2D1Bitmap1> target, readback;
    require(context->CreateBitmap({64U,64U},nullptr,0U,&target_properties,target.GetAddressOf()) == S_OK &&
        context->CreateBitmap({64U,64U},nullptr,0U,&read_properties,readback.GetAddressOf()) == S_OK,
        "original sideways targets");
    ComPtr<IDWriteRenderingParams> parameters;
    require(write_factory->CreateCustomRenderingParams(1,0,0,DWRITE_PIXEL_GEOMETRY_FLAT,DWRITE_RENDERING_MODE_OUTLINE,
        parameters.GetAddressOf()) == S_OK, "original sideways OUTLINE parameters");
    com::pointer<compat::rendering_parameters> typed_parameters;
    require(parameters->QueryInterface(compat::rendering_parameters_interface_id,
        reinterpret_cast<void**>(typed_parameters.put())) == S_OK, "original sideways typed parameters");
    const auto pixels = [&] {
        context->SetTarget(nullptr);
        require(readback->CopyFromBitmap(nullptr,target.Get(),nullptr) == S_OK, "original sideways pixel copy");
        D2D1_MAPPED_RECT mapped{};
        require(readback->Map(D2D1_MAP_OPTIONS_READ,&mapped) == S_OK && mapped.pitch >= 256U, "original sideways map");
        std::vector<std::uint8_t> result(64U*256U);
        for (std::size_t row=0U; row<64U; ++row) std::memcpy(result.data()+row*256U,mapped.bits+row*mapped.pitch,256U);
        require(readback->Unmap() == S_OK, "original sideways unmap"); return result;
    };
    std::size_t configurations=0U;
    std::uint64_t generation=0U;
    for (const bool cff : {false,true}) {
    for (const bool compact : {false,true}) {
        vertical_font_options options{};
        options.kind=cff ? vertical_font_kind::cff : vertical_font_kind::truetype;
        options.compact_metrics=compact; options.vorg=cff;
        const auto bytes=make_vertical_font(options);
        ComPtr<IDWriteFontFile> file;
        require(loader->CreateInMemoryFontFileReference(write_factory,bytes.data(),static_cast<UINT32>(bytes.size()),
            nullptr,file.GetAddressOf()) == S_OK, "original vertical complete font file");
        BOOL supported=FALSE; UINT32 face_count=0U;
        DWRITE_FONT_FILE_TYPE file_type=DWRITE_FONT_FILE_TYPE_UNKNOWN;
        DWRITE_FONT_FACE_TYPE face_type=DWRITE_FONT_FACE_TYPE_UNKNOWN;
        require(file->Analyze(&supported,&file_type,&face_type,&face_count) == S_OK && supported && face_count == 1U &&
            face_type == (cff ? DWRITE_FONT_FACE_TYPE_CFF : DWRITE_FONT_FACE_TYPE_TRUETYPE),
            "original vertical actual source outline family");
        IDWriteFontFile* files[]{file.Get()};
        ComPtr<IDWriteFontFace> face;
        require(write_factory->CreateFontFace(face_type,1U,files,0U,DWRITE_FONT_SIMULATIONS_NONE,face.GetAddressOf()) == S_OK,
            "original vertical source face");
        ComPtr<IDWriteFontFace1> extended_face;
        require(face.As(&extended_face) == S_OK, "original vertical design-advance capability");
        com::pointer<compat::font_face> typed_face;
        std::shared_ptr<const original_font_capture> captured;
        require(face->QueryInterface(compat::font_face_interface_id,reinterpret_cast<void**>(typed_face.put())) == S_OK &&
            capture_original_font(typed_face.get(),captured) == S_OK && !captured->has_variations &&
            captured->face_type == static_cast<std::uint32_t>(face_type) && captured->face_index == 0U &&
            captured->glyph_count == 3U && captured->simulations == 0U && captured->files.size() == 1U && captured->files[0] == bytes,
            "original vertical immutable font/face identity");
        // Read back the actual original tables, retaining their exact complete
        // compact tail and CFF-only VORG. No decoder supplies this oracle.
        for (const auto& table : vertical_font_wire::original_tables(bytes)) {
            if (table.tag != 0x76686561U && table.tag != 0x766D7478U && table.tag != 0x564F5247U) continue;
            const auto tag=((table.tag&0xFFU)<<24U)|((table.tag&0xFF00U)<<8U)|((table.tag&0xFF0000U)>>8U)|(table.tag>>24U);
            const void* contents=nullptr; UINT32 size=0U; void* owner=nullptr; BOOL exists=FALSE;
            require(face->TryGetFontTable(tag,&contents,&size,&owner,&exists) == S_OK && exists && contents != nullptr &&
                size == table.data.size(), "original complete vertical table readback");
            const bool equal=std::memcmp(contents,table.data.data(),table.data.size()) == 0;
            face->ReleaseFontTable(owner); require(equal, "original vertical table byte identity");
        }
        constexpr std::array<std::uint16_t,3U> indices{1U,0U,2U};
        std::array<DWRITE_GLYPH_METRICS,3U> normal_metrics{}, sideways_metrics{};
        std::array<INT32,3U> horizontal_advances{}, vertical_advances{};
        require(face->GetDesignGlyphMetrics(indices.data(),3U,normal_metrics.data(),FALSE) == S_OK &&
            face->GetDesignGlyphMetrics(indices.data(),3U,sideways_metrics.data(),TRUE) == S_OK &&
            extended_face->GetDesignGlyphAdvances(3U,indices.data(),horizontal_advances.data(),FALSE) == S_OK &&
            extended_face->GetDesignGlyphAdvances(3U,indices.data(),vertical_advances.data(),TRUE) == S_OK,
            "original horizontal/sideways design metrics and advances");
        std::array<float,3U> original_advances{};
        for (std::size_t item=0U; item<indices.size(); ++item) {
            const auto expected=expected_vertical_glyph(options,0U,indices[item]);
            require(static_cast<float>(horizontal_advances[item]) == expected.horizontal_advance &&
                static_cast<float>(vertical_advances[item]) == expected.vertical_advance,
                "original independent h/v advances including no-ink glyph");
            original_advances[item]=static_cast<float>(vertical_advances[item])/64.0F;
            for (const auto* observed : {&normal_metrics[item],&sideways_metrics[item]}) {
                require(static_cast<float>(observed->advanceWidth) == expected.horizontal_advance &&
                    static_cast<float>(observed->advanceHeight) == expected.vertical_advance,
                    "original complete design advance fields");
                if (expected.count != 0U) {
                    require(static_cast<float>(observed->leftSideBearing) == expected.x_min-expected.horizontal_origin &&
                        static_cast<float>(observed->topSideBearing) == expected.top_side_bearing &&
                        static_cast<float>(observed->bottomSideBearing) == expected.bottom_side_bearing &&
                        static_cast<float>(observed->verticalOriginY) == expected.vertical_origin,
                        "original asymmetric horizontal and vertical origin metrics");
                }
            }
        }
        std::shared_ptr<prepared_original_font> prepared;
        require(prepared_original_font::create(captured,prepared) == S_OK, "original sideways retained prepared owner");
        for (const bool nominal : {false,true}) {
        for (std::uint32_t variant=0U; variant<3U; ++variant) {
            const float advances[]{16,-3,9};
            const compat::glyph_offset offsets[]{{0.25F,0.5F},{0,0},{-0.75F,2.5F}};
            const compat::glyph_run run{captured->face.get(),15.625F,3U,indices.data(),nominal ? nullptr : advances,
                offsets,variant == 1U ? -1 : 1,2U};
            original_glyph_target frame;
            frame.identity=com::pointer<com::unknown>(typed_target.get()); frame.generation=++generation;
            frame.transform=sideways_pixel_transform(variant); frame.baseline={4,20};
            frame.pixels={64U,64U}; frame.dpi_x=96; frame.dpi_y=96; frame.format={87U,compat::alpha_mode::premultiplied};
            frame.antialias=variant == 0U ? compat::text_antialias_mode::aliased : compat::text_antialias_mode::grayscale;
            std::shared_ptr<const original_glyph_request> request;
            std::shared_ptr<const prepared_original_glyph_run> glyphs;
            require(capture_original_glyph_request(captured,run,compat::measuring_mode::natural,
                typed_parameters.get(),frame,request) == S_OK && prepared->prepare(request,glyphs) == S_OK &&
                glyphs->segments().size() == 8U && glyphs->request().sideways == run.is_sideways &&
                (glyphs->request().glyphs.advances() == nullptr) == nominal,
                "original sideways exact BOOL/source/nominal identity");
            com::pointer<compat::path_geometry> geometry;
            com::pointer<compat::geometry_sink> sink;
            require(typed_factory->CreatePathGeometry(geometry.put()) == S_OK && geometry->Open(sink.put()) == S_OK,
                "original sideways prepared geometry sink");
            sink->SetFillMode(compat::fill_mode::winding);
            for (std::size_t segment=0U; segment<glyphs->segments().size(); ++segment) {
                const auto& value=glyphs->segments()[segment];
                require(value.kind == PROGPU_NATIVE_PATH_SEGMENT_LINE, "original sideways line inventory");
                if (segment%4U == 0U) sink->BeginFigure({value.p0.x,value.p0.y},compat::figure_begin::filled);
                sink->AddLine({value.p1.x,value.p1.y});
                if (segment%4U == 3U) sink->EndFigure(compat::figure_end::closed);
            }
            require(sink->Close() == S_OK, "original sideways prepared geometry close");
            // Direct SDK outline extraction observes the same original run,
            // independent of DrawGlyphRun and of the native prepared geometry.
            ComPtr<ID2D1PathGeometry> original_geometry;
            ComPtr<ID2D1GeometrySink> original_sink;
            require(factory->CreatePathGeometry(original_geometry.GetAddressOf()) == S_OK &&
                original_geometry->Open(original_sink.GetAddressOf()) == S_OK, "original sideways outline sink");
            const DWRITE_GLYPH_OFFSET original_offsets[]{{0.25F,0.5F},{0,0},{-0.75F,2.5F}};
            require(face->GetGlyphRunOutline(15.625F,indices.data(),nominal ? nullptr : advances,original_offsets,3U,
                variant == 1U ? -1 : TRUE,FALSE,original_sink.Get()) == S_OK && original_sink->Close() == S_OK,
                "original actual sideways outline extraction");
            D2D1_RECT_F actual_bounds{};
            const auto boxes=sideways_pixel_rectangles(options,nominal);
            require(original_geometry->GetBounds(nullptr,&actual_bounds) == S_OK &&
                actual_bounds.left == std::min(boxes[0].left,boxes[1].left)-4 &&
                actual_bounds.top == std::min(boxes[0].top,boxes[1].top)-20 &&
                actual_bounds.right == std::max(boxes[0].right,boxes[1].right)-4 &&
                actual_bounds.bottom == std::max(boxes[0].bottom,boxes[1].bottom)-20,
                "original sideways literal run geometry envelope");
            const std::array paths{sideways_pixel_path::original,sideways_pixel_path::independent_geometry,
                sideways_pixel_path::prepared_geometry,sideways_pixel_path::original_design_advances};
            std::array<std::vector<std::uint8_t>,4U> images;
            for (std::size_t path=0U; path<(nominal ? 4U : 3U); ++path) {
                context->SetTarget(target.Get());
                record_sideways_pixel_case(typed_factory.get(),typed_target.get(),prepared,typed_parameters.get(),options,
                    nominal,variant,paths[path],require,geometry.get(),original_advances.data());
                images[path]=pixels();
            }
            require(images[0].size() == 64U * 256U, "original glyph frame byte inventory");
            compare(original_glyph_reference_matches_bgra(images[0], original_frame++),
                "original DrawGlyphRun changed from its complete independent source receipt");
            compare(images[1] == images[2],
                "original sideways FillGeometry independent/prepared full-byte mismatch");
            if (nominal) compare(images[0] == images[3], "original sideways null versus actual vertical advance mismatch");
            ++configurations;
        }
        }
    }
    }
    require(configurations == 24U, "original sideways static TT/CFF full configuration inventory");
    require(original_frame == 175U, "complete original sideways_glyph receipt inventory");
}
} // namespace progpu::native::direct2d::tests
