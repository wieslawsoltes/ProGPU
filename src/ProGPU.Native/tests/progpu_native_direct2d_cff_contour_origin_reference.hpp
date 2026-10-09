#pragma once

#include "progpu_native_direct2d_cff_contour_origin_fixture.hpp"
#include "progpu_native_direct2d_cff_glyph_reference.hpp"
#include <cstdio>

namespace progpu::native::direct2d::tests {

// Actual original Windows source calls only. Fractional INT32 metric fields are
// observations, not a guessed rounding oracle. Full outline/pixel expectations
// remain the independent precise contour contract and must pass unchanged.
template<class Require, class Compare>
void verify_original_cff_contour_origin_pixels(ID2D1DeviceContext* source_context,
    IDWriteFactory* write_factory, Require require, Compare compare)
{
    using Microsoft::WRL::ComPtr;
    ComPtr<IDWriteFactory5> extended_factory;
    require(write_factory->QueryInterface(IID_PPV_ARGS(extended_factory.GetAddressOf())) == S_OK,
        "original CFF contour-origin factory");
    ComPtr<IDWriteInMemoryFontFileLoader> loader;
    require(extended_factory->CreateInMemoryFontFileLoader(loader.GetAddressOf()) == S_OK &&
        write_factory->RegisterFontFileLoader(loader.Get()) == S_OK, "original CFF contour-origin loader");
    struct unregister_loader final {
        IDWriteFactory* factory; IDWriteFontFileLoader* loader; Require& check;
        ~unregister_loader() { check(factory->UnregisterFontFileLoader(loader) == S_OK, "original CFF contour-origin loader retirement"); }
    } registered{write_factory,loader.Get(),require};
    ComPtr<ID2D1Device> device; source_context->GetDevice(device.GetAddressOf());
    ComPtr<ID2D1DeviceContext> context;
    require(device->CreateDeviceContext(D2D1_DEVICE_CONTEXT_OPTIONS_NONE,context.GetAddressOf()) == S_OK,
        "original CFF contour-origin independent context");
    context->SetDpi(96,96);
    ComPtr<ID2D1Factory> factory; context->GetFactory(factory.GetAddressOf());
    com::pointer<compat::factory> typed_factory; com::pointer<compat::render_target> typed_target;
    require(factory->QueryInterface(compat::factory_interface_id,reinterpret_cast<void**>(typed_factory.put())) == S_OK &&
        context->QueryInterface(compat::render_target_interface_id,reinterpret_cast<void**>(typed_target.put())) == S_OK,
        "original CFF contour-origin typed identity");
    const auto target_properties = D2D1::BitmapProperties1(D2D1_BITMAP_OPTIONS_TARGET,
        D2D1::PixelFormat(DXGI_FORMAT_B8G8R8A8_UNORM,D2D1_ALPHA_MODE_PREMULTIPLIED),96,96);
    const auto read_properties = D2D1::BitmapProperties1(D2D1_BITMAP_OPTIONS_CPU_READ | D2D1_BITMAP_OPTIONS_CANNOT_DRAW,
        target_properties.pixelFormat,96,96);
    ComPtr<ID2D1Bitmap1> target, readback;
    require(context->CreateBitmap({64U,64U},nullptr,0U,&target_properties,target.GetAddressOf()) == S_OK &&
        context->CreateBitmap({64U,64U},nullptr,0U,&read_properties,readback.GetAddressOf()) == S_OK,
        "original CFF contour-origin targets");
    ComPtr<IDWriteRenderingParams> parameters;
    require(write_factory->CreateCustomRenderingParams(1,0,0,DWRITE_PIXEL_GEOMETRY_FLAT,DWRITE_RENDERING_MODE_OUTLINE,
        parameters.GetAddressOf()) == S_OK, "original CFF contour-origin OUTLINE parameters");
    com::pointer<compat::rendering_parameters> typed_parameters;
    require(parameters->QueryInterface(compat::rendering_parameters_interface_id,
        reinterpret_cast<void**>(typed_parameters.put())) == S_OK, "original CFF contour-origin typed parameters");
    const auto pixels = [&] {
        context->SetTarget(nullptr);
        require(readback->CopyFromBitmap(nullptr,target.Get(),nullptr) == S_OK, "original CFF contour-origin copy");
        D2D1_MAPPED_RECT mapped{};
        require(readback->Map(D2D1_MAP_OPTIONS_READ,&mapped) == S_OK && mapped.pitch >= 256U, "original CFF contour-origin map");
        std::vector<std::uint8_t> result(64U*256U);
        for (std::size_t row = 0U; row < 64U; ++row) std::memcpy(result.data()+row*256U,mapped.bits+row*mapped.pitch,256U);
        require(readback->Unmap() == S_OK, "original CFF contour-origin unmap"); return result;
    };
    std::uint64_t generation = 0U; std::size_t configurations = 0U;
    for (const bool fractional : {false,true}) {
        const auto bytes = make_cff_vertical_font(fractional);
        ComPtr<IDWriteFontFile> file;
        require(loader->CreateInMemoryFontFileReference(write_factory,bytes.data(),static_cast<UINT32>(bytes.size()),
            nullptr,file.GetAddressOf()) == S_OK, "original CFF contour-origin complete file");
        BOOL supported = FALSE; UINT32 face_count = 0U;
        DWRITE_FONT_FILE_TYPE file_type = DWRITE_FONT_FILE_TYPE_UNKNOWN;
        DWRITE_FONT_FACE_TYPE face_type = DWRITE_FONT_FACE_TYPE_UNKNOWN;
        require(file->Analyze(&supported,&file_type,&face_type,&face_count) == S_OK && supported && face_count == 1U &&
            face_type == DWRITE_FONT_FACE_TYPE_CFF, "original CFF contour-origin actual family");
        IDWriteFontFile* files[]{file.Get()}; ComPtr<IDWriteFontFace> face;
        require(write_factory->CreateFontFace(face_type,1U,files,0U,DWRITE_FONT_SIMULATIONS_NONE,face.GetAddressOf()) == S_OK,
            "original CFF contour-origin face");
        ComPtr<IDWriteFontFace1> extended_face;
        require(face.As(&extended_face) == S_OK, "original CFF contour-origin advance capability");
        com::pointer<compat::font_face> typed_face;
        std::shared_ptr<const original_font_capture> captured;
        require(face->QueryInterface(compat::font_face_interface_id,reinterpret_cast<void**>(typed_face.put())) == S_OK &&
            capture_original_font(typed_face.get(),captured) == S_OK && captured->face_type == 0U &&
            captured->face_index == 0U && captured->glyph_count == 3U && !captured->has_variations &&
            captured->simulations == 0U && captured->files.size() == 1U && captured->files[0] == bytes,
            "original CFF contour-origin immutable source identity");
        for (const auto& table : vertical_font_wire::original_tables(bytes)) {
            if (table.tag != 0x43464620U && table.tag != 0x76686561U && table.tag != 0x766D7478U) continue;
            const auto tag = ((table.tag&0xFFU)<<24U)|((table.tag&0xFF00U)<<8U)|((table.tag&0xFF0000U)>>8U)|(table.tag>>24U);
            const void* contents = nullptr; UINT32 size = 0U; void* owner = nullptr; BOOL exists = FALSE;
            require(face->TryGetFontTable(tag,&contents,&size,&owner,&exists) == S_OK && exists && contents != nullptr &&
                size == table.data.size(), "original CFF contour-origin complete tables");
            const bool equal = std::memcmp(contents,table.data.data(),table.data.size()) == 0;
            face->ReleaseFontTable(owner); require(equal, "original CFF contour-origin table byte identity");
        }
        const void* vorg = nullptr; UINT32 vorg_size = 0U; void* vorg_owner = nullptr; BOOL vorg_exists = TRUE;
        require(face->TryGetFontTable(DWRITE_MAKE_OPENTYPE_TAG('V','O','R','G'),&vorg,&vorg_size,&vorg_owner,&vorg_exists) == S_OK &&
            !vorg_exists, "original CFF contour-origin genuinely absent VORG");
        if (vorg_owner != nullptr) face->ReleaseFontTable(vorg_owner);
        constexpr std::array<std::uint16_t,3U> indices{1U,0U,2U};
        std::array<INT32,3U> advances{};
        require(extended_face->GetDesignGlyphAdvances(3U,indices.data(),advances.data(),TRUE) == S_OK &&
            advances == std::array<INT32,3U>{1000,900,1100}, "original actual unvaried vertical advances including empty glyph");
        std::array<float,3U> scaled_advances{};
        for (std::size_t index = 0U; index < 3U; ++index) scaled_advances[index] = static_cast<float>(advances[index])/64.0F;
        for (const BOOL sideways : {FALSE,TRUE}) {
            std::array<DWRITE_GLYPH_METRICS,3U> observed{};
            require(face->GetDesignGlyphMetrics(indices.data(),3U,observed.data(),sideways) == S_OK,
                "original CFF contour-origin metric observation");
            for (std::size_t item = 0U; item < indices.size(); ++item) {
                const auto& value = observed[item];
                // Retain integer SDK observations without asserting a rounded
                // version of the fractional301.5 design maximum/origin.
                std::printf("CFF_CONTOUR_ORIGIN_METRICS fractional=%u glyph=%u sideways=%d lsb=%d aw=%u rsb=%d tsb=%d ah=%u bsb=%d origin=%d\n",
                    fractional ? 1U : 0U,static_cast<unsigned>(indices[item]),static_cast<int>(sideways),
                    static_cast<int>(value.leftSideBearing),static_cast<unsigned>(value.advanceWidth),
                    static_cast<int>(value.rightSideBearing),static_cast<int>(value.topSideBearing),
                    static_cast<unsigned>(value.advanceHeight),static_cast<int>(value.bottomSideBearing),static_cast<int>(value.verticalOriginY));
                require(value.advanceHeight == static_cast<UINT32>(advances[item]), "original CFF vertical advance fields");
            }
        }
        std::shared_ptr<prepared_original_font> prepared;
        require(prepared_original_font::create(captured,prepared) == S_OK, "original CFF contour-origin prepared owner");
        for (const bool nominal : {false,true}) for (std::uint32_t variant = 0U; variant < 3U; ++variant) {
            const float explicit_advances[]{16,-3,9};
            const compat::glyph_offset offsets[]{{0.25F,0.5F},{0,0},{-0.75F,2.5F}};
            const auto wanted = cff_contour_origin_segments(fractional,nominal);
            const float prior_pen = nominal ? scaled_advances[0]+scaled_advances[1] : 13.0F;
            for (const std::size_t item : {std::size_t{0},std::size_t{2}}) {
                original_cff_outline_sink observed; const float advance = 0;
                const DWRITE_GLYPH_OFFSET offset{offsets[item].advance_offset,offsets[item].ascender_offset};
                require(face->GetGlyphRunOutline(15.625F,&indices[item],&advance,&offset,1U,
                    variant == 1U ? -1 : TRUE,FALSE,&observed) == S_OK && observed.complete() && observed.segments().size() == 2U,
                    "original CFF contour-origin direct cubic inventory");
                for (std::size_t segment = 0U; segment < 2U; ++segment) {
                    const auto& actual = observed.segments()[segment]; const auto& expected = wanted[item+segment];
                    require(actual.kind == expected.kind, "original CFF contour-origin exact segment kind");
                    const auto equal = [&](progpu_native_point point, progpu_native_point target_point) {
                        return point.x+4+(item == 0U ? 0.0F : prior_pen) == target_point.x && -point.y+20 == target_point.y;
                    };
                    // The original call and complete segment inventory were
                    // validated above. Collect this exact value comparison so
                    // one origin mismatch cannot hide later source families;
                    // the enclosing runner still fails if any comparison fails.
                    compare(equal(actual.p0,expected.p0) && equal(actual.p1,expected.p1) &&
                        (actual.kind != PROGPU_NATIVE_PATH_SEGMENT_CUBIC || (equal(actual.p2,expected.p2) && equal(actual.p3,expected.p3))),
                        "original CFF precise contour-origin literal control points");
                }
            }
            const compat::glyph_run run{captured->face.get(),15.625F,3U,indices.data(),nominal ? nullptr : explicit_advances,
                offsets,variant == 1U ? -1 : 1,2U};
            original_glyph_target frame;
            frame.identity = com::pointer<com::unknown>(typed_target.get()); frame.generation = ++generation;
            frame.transform = sideways_pixel_transform(variant); frame.baseline = {4,20}; frame.pixels = {64U,64U};
            frame.dpi_x = 96; frame.dpi_y = 96; frame.format = {87U,compat::alpha_mode::premultiplied};
            frame.antialias = variant == 0U ? compat::text_antialias_mode::aliased : compat::text_antialias_mode::grayscale;
            std::shared_ptr<const original_glyph_request> request; std::shared_ptr<const prepared_original_glyph_run> glyphs;
            require(capture_original_glyph_request(captured,run,compat::measuring_mode::natural,typed_parameters.get(),frame,request) == S_OK &&
                prepared->prepare(request,glyphs) == S_OK && glyphs->segments().size() == 4U &&
                glyphs->request().sideways == run.is_sideways && (glyphs->request().glyphs.advances() == nullptr) == nominal,
                "original CFF contour-origin exact request");
            com::pointer<compat::path_geometry> geometry; com::pointer<compat::geometry_sink> sink;
            require(typed_factory->CreatePathGeometry(geometry.put()) == S_OK && geometry->Open(sink.put()) == S_OK,
                "original CFF contour-origin prepared sink");
            append_cff_contour_origin_geometry(sink.get(),glyphs->segments(),require);
            require(sink->Close() == S_OK, "original CFF contour-origin prepared close");
            ComPtr<ID2D1PathGeometry> original_geometry; ComPtr<ID2D1GeometrySink> original_sink;
            require(factory->CreatePathGeometry(original_geometry.GetAddressOf()) == S_OK &&
                original_geometry->Open(original_sink.GetAddressOf()) == S_OK, "original CFF whole-run outline sink");
            const DWRITE_GLYPH_OFFSET original_offsets[]{{0.25F,0.5F},{0,0},{-0.75F,2.5F}};
            require(face->GetGlyphRunOutline(15.625F,indices.data(),nominal ? nullptr : explicit_advances,original_offsets,3U,
                variant == 1U ? -1 : TRUE,FALSE,original_sink.Get()) == S_OK && original_sink->Close() == S_OK,
                "original CFF whole-run actual GetGlyphRunOutline");
            ComPtr<ID2D1TransformedGeometry> placed_original;
            const auto baseline = D2D1::Matrix3x2F::Translation(4,20);
            require(factory->CreateTransformedGeometry(original_geometry.Get(),&baseline,placed_original.GetAddressOf()) == S_OK,
                "original CFF outline actual baseline");
            com::pointer<compat::geometry> typed_original;
            require(placed_original->QueryInterface(compat::geometry_interface_id,reinterpret_cast<void**>(typed_original.put())) == S_OK,
                "original CFF outline geometry identity");
            const std::array paths{sideways_pixel_path::original,sideways_pixel_path::independent_geometry,
                sideways_pixel_path::prepared_geometry,sideways_pixel_path::prepared_geometry,sideways_pixel_path::original_design_advances};
            std::array<std::vector<std::uint8_t>,5U> images;
            for (std::size_t path = 0U; path < (nominal ? 5U : 4U); ++path) {
                context->SetTarget(target.Get());
                record_cff_contour_origin_pixel_case(typed_factory.get(),typed_target.get(),prepared,typed_parameters.get(),fractional,
                    nominal,variant,paths[path],require,path == 3U ? typed_original.get() : geometry.get(),scaled_advances.data());
                images[path] = pixels();
            }
            require(images[0].size() == 64U * 256U, "original glyph frame byte inventory");
            compare(images[0] == images[1] && images[0] == images[2] && images[0] == images[3],
                "original CFF contour-origin DrawGlyphRun/GetGlyphRunOutline/independent/prepared full-byte mismatch");
            if (nominal) compare(images[0] == images[4], "original CFF null versus actual design advances mismatch");
            context->SetTarget(target.Get());
            record_cff_contour_origin_pixel_case(typed_factory.get(),typed_target.get(),prepared,typed_parameters.get(),fractional,
                nominal,variant,sideways_pixel_path::original,require);
            compare(images[0] == pixels(), "original CFF contour-origin same-owner warm replay");
            bool ink = false;
            for (std::size_t pixel = 0U; pixel < images[0].size(); pixel += 4U) {
                require(images[0][pixel] == 0U && images[0][pixel+1U] == 0U && images[0][pixel+3U] == 255U,
                    "original CFF contour-origin BGRA channels"); ink |= images[0][pixel+2U] != 0U;
            }
            require(ink && images[0][2U] == 0U, "original CFF contour-origin nonempty full frame");
            ++configurations;
        }
    }
    require(configurations == 12U, "original CFF contour-origin full configuration inventory");
    std::fprintf(stderr, "Original CFF contour-origin complete configurations=%zu\n", configurations);
}
} // namespace progpu::native::direct2d::tests
