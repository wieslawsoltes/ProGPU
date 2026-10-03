#pragma once

#include "progpu_native_direct2d_variable_sideways_glyph_fixture.hpp"
#include <bit>

namespace progpu::native::direct2d::tests {

// Actual original DirectWrite objects, not a mocked face or product metric
// reader. The instance selector below preserves the SDK's complete axis list.
template<class Require>
void verify_original_variable_sideways_glyph_pixels(ID2D1DeviceContext* source_context,
    IDWriteFactory* write_factory, Require require)
{
    using Microsoft::WRL::ComPtr;
    ComPtr<IDWriteFactory5> extended_factory;
    require(write_factory->QueryInterface(IID_PPV_ARGS(extended_factory.GetAddressOf())) == S_OK,
        "original variable sideways factory");
    ComPtr<IDWriteInMemoryFontFileLoader> loader;
    require(extended_factory->CreateInMemoryFontFileLoader(loader.GetAddressOf()) == S_OK &&
        write_factory->RegisterFontFileLoader(loader.Get()) == S_OK, "original variable sideways loader registration");
    struct unregister_loader final {
        IDWriteFactory* factory; IDWriteFontFileLoader* loader; Require& check;
        ~unregister_loader() { check(factory->UnregisterFontFileLoader(loader) == S_OK, "original variable sideways loader retirement"); }
    } registered{write_factory,loader.Get(),require};
    ComPtr<ID2D1Device> device;
    source_context->GetDevice(device.GetAddressOf());
    ComPtr<ID2D1DeviceContext> context;
    require(device->CreateDeviceContext(D2D1_DEVICE_CONTEXT_OPTIONS_NONE,context.GetAddressOf()) == S_OK,
        "original variable sideways independent context");
    context->SetDpi(96,96);
    ComPtr<ID2D1Factory> factory;
    context->GetFactory(factory.GetAddressOf());
    com::pointer<compat::factory> typed_factory;
    com::pointer<compat::render_target> typed_target;
    require(factory->QueryInterface(compat::factory_interface_id,reinterpret_cast<void**>(typed_factory.put())) == S_OK &&
        context->QueryInterface(compat::render_target_interface_id,reinterpret_cast<void**>(typed_target.put())) == S_OK,
        "original variable sideways typed target identity");
    const auto target_properties=D2D1::BitmapProperties1(D2D1_BITMAP_OPTIONS_TARGET,
        D2D1::PixelFormat(DXGI_FORMAT_B8G8R8A8_UNORM,D2D1_ALPHA_MODE_PREMULTIPLIED),96,96);
    const auto read_properties=D2D1::BitmapProperties1(D2D1_BITMAP_OPTIONS_CPU_READ | D2D1_BITMAP_OPTIONS_CANNOT_DRAW,
        target_properties.pixelFormat,96,96);
    ComPtr<ID2D1Bitmap1> target, readback;
    require(context->CreateBitmap({64U,64U},nullptr,0U,&target_properties,target.GetAddressOf()) == S_OK &&
        context->CreateBitmap({64U,64U},nullptr,0U,&read_properties,readback.GetAddressOf()) == S_OK,
        "original variable sideways targets");
    ComPtr<IDWriteRenderingParams> parameters;
    require(write_factory->CreateCustomRenderingParams(1,0,0,DWRITE_PIXEL_GEOMETRY_FLAT,DWRITE_RENDERING_MODE_OUTLINE,
        parameters.GetAddressOf()) == S_OK, "original variable sideways OUTLINE parameters");
    com::pointer<compat::rendering_parameters> typed_parameters;
    require(parameters->QueryInterface(compat::rendering_parameters_interface_id,
        reinterpret_cast<void**>(typed_parameters.put())) == S_OK, "original variable sideways typed parameters");
    const auto pixels=[&] {
        context->SetTarget(nullptr);
        require(readback->CopyFromBitmap(nullptr,target.Get(),nullptr) == S_OK, "original variable sideways pixel copy");
        D2D1_MAPPED_RECT mapped{};
        require(readback->Map(D2D1_MAP_OPTIONS_READ,&mapped) == S_OK && mapped.pitch >= 256U,
            "original variable sideways pixel map");
        std::vector<std::uint8_t> result(64U*256U);
        for (std::size_t row=0U; row<64U; ++row) std::memcpy(result.data()+row*256U,mapped.bits+row*mapped.pitch,256U);
        require(readback->Unmap() == S_OK, "original variable sideways pixel unmap"); return result;
    };
    std::size_t configurations=0U;
    std::uint64_t generation=0U;
    for (const auto options : variable_sideways_pixel_fonts) {
        require(!options.vvar_precedence_discriminator, "contradictory source metrics are observations, not a positive pixel oracle");
        const bool cff=options.kind == vertical_font_kind::cff2_variable;
        const auto bytes=make_vertical_font(options);
        ComPtr<IDWriteFontFile> file;
        require(loader->CreateInMemoryFontFileReference(write_factory,bytes.data(),static_cast<UINT32>(bytes.size()),
            nullptr,file.GetAddressOf()) == S_OK, "original variable sideways file ownership");
        BOOL supported=FALSE; UINT32 face_count=0U;
        DWRITE_FONT_FILE_TYPE file_type=DWRITE_FONT_FILE_TYPE_UNKNOWN;
        DWRITE_FONT_FACE_TYPE face_type=DWRITE_FONT_FACE_TYPE_UNKNOWN;
        require(file->Analyze(&supported,&file_type,&face_type,&face_count) == S_OK && supported && face_count == 1U &&
            face_type == (cff ? DWRITE_FONT_FACE_TYPE_CFF : DWRITE_FONT_FACE_TYPE_TRUETYPE),
            "original variable sideways actual outline family");
        IDWriteFontFile* files[]{file.Get()};
        ComPtr<IDWriteFontFace> default_face;
        require(write_factory->CreateFontFace(face_type,1U,files,0U,DWRITE_FONT_SIMULATIONS_NONE,
            default_face.GetAddressOf()) == S_OK, "original variable sideways default face");
        ComPtr<IDWriteFontFace5> default_extended;
        ComPtr<IDWriteFontResource> resource;
        require(default_face.As(&default_extended) == S_OK && default_extended->HasVariations() &&
            default_extended->GetFontResource(resource.GetAddressOf()) == S_OK,
            "original variable sideways genuine Face5 resource");
        for (std::size_t instance=0U; instance<vertical_font_weights.size(); ++instance) {
            const DWRITE_FONT_AXIS_VALUE coordinate{DWRITE_FONT_AXIS_TAG_WEIGHT,vertical_font_weights[instance]};
            ComPtr<IDWriteFontFace5> face;
            require(resource->CreateFontFace(DWRITE_FONT_SIMULATIONS_NONE,&coordinate,1U,face.GetAddressOf()) == S_OK &&
                face->HasVariations(), "original variable sideways exact axis instance");
            const auto axis_count=face->GetFontAxisValueCount();
            require(axis_count != 0U && axis_count <= original_font_capture::maximum_axes,
                "original variable sideways complete axis bound");
            std::vector<DWRITE_FONT_AXIS_VALUE> axes(axis_count);
            require(face->GetFontAxisValues(axes.data(),axis_count) == S_OK, "original variable sideways actual axes");
            com::pointer<compat::font_face> typed_face;
            std::shared_ptr<const original_font_capture> captured;
            require(face->QueryInterface(compat::font_face_interface_id,reinterpret_cast<void**>(typed_face.put())) == S_OK &&
                capture_original_font(typed_face.get(),captured) == S_OK && captured->axis_values_available &&
                captured->has_variations && captured->face_type == static_cast<std::uint32_t>(face_type) &&
                captured->face_index == 0U && captured->simulations == 0U && captured->glyph_count == 3U &&
                captured->files.size() == 1U && captured->files[0] == bytes && captured->axis_values.size() == axes.size(),
                "original variable sideways complete retained source identity");
            bool found_weight=false;
            for (std::size_t axis=0U; axis<axes.size(); ++axis) {
                require(captured->axis_values[axis].tag == static_cast<std::uint32_t>(axes[axis].axisTag) &&
                    std::bit_cast<std::uint32_t>(captured->axis_values[axis].value) == std::bit_cast<std::uint32_t>(axes[axis].value),
                    "original variable sideways canonical axis order/tag/bits");
                if (axes[axis].axisTag == DWRITE_FONT_AXIS_TAG_WEIGHT) {
                    require(!found_weight && std::bit_cast<std::uint32_t>(axes[axis].value) ==
                        std::bit_cast<std::uint32_t>(vertical_font_weights[instance]), "original variable sideways requested weight bits");
                    found_weight=true;
                }
            }
            require(found_weight, "original variable sideways actual weight, not inferred defaults");
            for (const auto& table : vertical_font_wire::original_tables(bytes)) {
                if (table.tag != 0x76686561U && table.tag != 0x766D7478U && table.tag != 0x564F5247U &&
                    table.tag != 0x56564152U && table.tag != 0x67766172U) continue;
                const auto tag=((table.tag&0xFFU)<<24U)|((table.tag&0xFF00U)<<8U)|((table.tag&0xFF0000U)>>8U)|(table.tag>>24U);
                const void* contents=nullptr; UINT32 size=0U; void* owner=nullptr; BOOL exists=FALSE;
                require(face->TryGetFontTable(tag,&contents,&size,&owner,&exists) == S_OK && exists && contents != nullptr &&
                    size == table.data.size(), "original variable sideways metric/phantom table readback");
                const bool equal=std::memcmp(contents,table.data.data(),table.data.size()) == 0;
                face->ReleaseFontTable(owner); require(equal, "original variable sideways source table byte identity");
            }
            constexpr std::array<std::uint16_t,3U> indices{1U,0U,2U};
            std::array<DWRITE_GLYPH_METRICS,3U> ordinary_metrics{}, sideways_metrics{};
            std::array<INT32,3U> horizontal_advances{}, vertical_advances{};
            DWRITE_FONT_METRICS font_metrics{};
            static_cast<IDWriteFontFace*>(face.Get())->GetMetrics(&font_metrics);
            require(font_metrics.designUnitsPerEm == 1000U &&
                face->GetDesignGlyphMetrics(indices.data(),3U,ordinary_metrics.data(),FALSE) == S_OK &&
                face->GetDesignGlyphMetrics(indices.data(),3U,sideways_metrics.data(),TRUE) == S_OK &&
                face->GetDesignGlyphAdvances(3U,indices.data(),horizontal_advances.data(),FALSE) == S_OK &&
                face->GetDesignGlyphAdvances(3U,indices.data(),vertical_advances.data(),TRUE) == S_OK,
                "original variable sideways independent design metrics");
            std::array<float,3U> original_advances{};
            for (std::size_t item=0U; item<indices.size(); ++item) {
                const auto expected=expected_vertical_glyph(options,instance,indices[item]);
                // These five original coordinates produce integral authored
                // metrics. No float-to-design-integer rounding rule is chosen.
                require(static_cast<float>(horizontal_advances[item]) == expected.horizontal_advance &&
                    static_cast<float>(vertical_advances[item]) == expected.vertical_advance,
                    "original variable h/v advances including no-ink movement");
                original_advances[item]=static_cast<float>(vertical_advances[item])/64.0F;
                for (const auto* observed : {&ordinary_metrics[item],&sideways_metrics[item]}) {
                    require(static_cast<float>(observed->advanceWidth) == expected.horizontal_advance &&
                        static_cast<float>(observed->advanceHeight) == expected.vertical_advance,
                        "original variable integral design advance fields");
                    if (expected.count != 0U) {
                        require(static_cast<float>(observed->leftSideBearing) == expected.x_min-expected.horizontal_origin &&
                            static_cast<float>(observed->topSideBearing) == expected.top_side_bearing &&
                            static_cast<float>(observed->bottomSideBearing) == expected.bottom_side_bearing &&
                            static_cast<float>(observed->verticalOriginY) == expected.vertical_origin,
                            "original variable independent bearings/origins");
                    }
                }
            }
            std::shared_ptr<prepared_original_font> prepared;
            require(prepared_original_font::create(captured,prepared) == S_OK, "original variable sideways retained native owner");
            for (const bool nominal : {false,true}) {
                const auto variant=static_cast<std::uint32_t>((instance+(nominal ? 1U : 0U))%3U);
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
                    "original variable sideways original occurrence identity");
                com::pointer<compat::path_geometry> geometry;
                com::pointer<compat::geometry_sink> sink;
                require(typed_factory->CreatePathGeometry(geometry.put()) == S_OK && geometry->Open(sink.put()) == S_OK,
                    "original variable sideways prepared geometry sink");
                sink->SetFillMode(compat::fill_mode::winding);
                for (std::size_t segment=0U; segment<glyphs->segments().size(); ++segment) {
                    const auto& value=glyphs->segments()[segment];
                    require(value.kind == PROGPU_NATIVE_PATH_SEGMENT_LINE, "original variable sideways line inventory");
                    if (segment%4U == 0U) sink->BeginFigure({value.p0.x,value.p0.y},compat::figure_begin::filled);
                    sink->AddLine({value.p1.x,value.p1.y});
                    if (segment%4U == 3U) sink->EndFigure(compat::figure_end::closed);
                }
                require(sink->Close() == S_OK, "original variable sideways prepared geometry close");
                ComPtr<ID2D1PathGeometry> original_geometry;
                ComPtr<ID2D1GeometrySink> original_sink;
                require(factory->CreatePathGeometry(original_geometry.GetAddressOf()) == S_OK &&
                    original_geometry->Open(original_sink.GetAddressOf()) == S_OK, "original variable sideways outline sink");
                const DWRITE_GLYPH_OFFSET original_offsets[]{{0.25F,0.5F},{0,0},{-0.75F,2.5F}};
                require(face->GetGlyphRunOutline(15.625F,indices.data(),nominal ? nullptr : advances,original_offsets,3U,
                    variant == 1U ? -1 : TRUE,FALSE,original_sink.Get()) == S_OK && original_sink->Close() == S_OK,
                    "original variable sideways direct outline extraction");
                D2D1_RECT_F actual_bounds{};
                const auto boxes=variable_sideways_pixel_rectangles(options,instance,nominal);
                require(original_geometry->GetBounds(nullptr,&actual_bounds) == S_OK &&
                    actual_bounds.left == std::min(boxes[0].left,boxes[1].left)-4 &&
                    actual_bounds.top == std::min(boxes[0].top,boxes[1].top)-20 &&
                    actual_bounds.right == std::max(boxes[0].right,boxes[1].right)-4 &&
                    actual_bounds.bottom == std::max(boxes[0].bottom,boxes[1].bottom)-20,
                    "original variable sideways literal run geometry envelope");
                const std::array paths{sideways_pixel_path::original,sideways_pixel_path::independent_geometry,
                    sideways_pixel_path::prepared_geometry,sideways_pixel_path::original_design_advances};
                std::array<std::vector<std::uint8_t>,4U> images;
                for (std::size_t path=0U; path<(nominal ? 4U : 3U); ++path) {
                    context->SetTarget(target.Get());
                    record_variable_sideways_pixel_case(typed_factory.get(),typed_target.get(),prepared,typed_parameters.get(),
                        options,instance,nominal,variant,paths[path],require,geometry.get(),original_advances.data());
                    images[path]=pixels();
                }
                require(images[0].size() == 64U*256U && images[0] == images[1] && images[0] == images[2],
                    "original variable sideways DrawGlyphRun/independent/prepared full-byte mismatch");
                if (nominal) require(images[0] == images[3], "original variable sideways null/design advance mismatch");
                ++configurations;
            }
        }
    }
    require(configurations == 80U, "original variable sideways independent configuration inventory");
}
} // namespace progpu::native::direct2d::tests
