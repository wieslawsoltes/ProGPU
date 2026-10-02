#pragma once

#include "progpu_native_direct2d_prepared_glyph_fixture.hpp"

// Windows SDK declarations precede this header. The genuine original font
// loader and DrawGlyphRun own the oracle; no product rasterizer produces it.
namespace progpu::native::direct2d::tests {

template<class Require>
void verify_original_prepared_glyph_pixels(ID2D1DeviceContext* source_context,
    IDWriteFactory* write_factory, Require require)
{
    for (std::uint32_t origins = 0U; origins < 3U; ++origins) {
    using Microsoft::WRL::ComPtr;
    ComPtr<IDWriteFactory5> extended_factory;
    require(SUCCEEDED(write_factory->QueryInterface(IID_PPV_ARGS(extended_factory.GetAddressOf()))),
        "original prepared font loader factory");
    ComPtr<IDWriteInMemoryFontFileLoader> loader;
    require(extended_factory->CreateInMemoryFontFileLoader(loader.GetAddressOf()) == S_OK &&
        write_factory->RegisterFontFileLoader(loader.Get()) == S_OK, "original prepared font loader registration");
    struct unregister_loader final {
        IDWriteFactory* factory;
        IDWriteFontFileLoader* loader;
        Require& check;
        ~unregister_loader() { check(factory->UnregisterFontFileLoader(loader) == S_OK, "original font loader retirement"); }
    } registered{write_factory, loader.Get(), require};
    const auto bytes = prepared_pixel_font(origins);
    ComPtr<IDWriteFontFile> file;
    // Null owner makes the original SDK copy these authored bytes. Its loader
    // registration outlives the face, every capture and each prepared context.
    require(loader->CreateInMemoryFontFileReference(write_factory, bytes.data(),
        static_cast<UINT32>(bytes.size()), nullptr, file.GetAddressOf()) == S_OK, "original authored font reference");
    IDWriteFontFile* files[]{file.Get()};
    ComPtr<IDWriteFontFace> face;
    require(write_factory->CreateFontFace(DWRITE_FONT_FACE_TYPE_TRUETYPE, 1U, files, 0U,
        DWRITE_FONT_SIMULATIONS_NONE, face.GetAddressOf()) == S_OK, "original authored font face");
    com::pointer<compat::font_face> typed_face;
    require(face->QueryInterface(compat::font_face_interface_id, reinterpret_cast<void**>(typed_face.put())) == S_OK,
        "original authored typed face");
    std::shared_ptr<const original_font_capture> captured;
    std::shared_ptr<prepared_original_font> prepared;
    require(capture_original_font(typed_face.get(), captured) == S_OK && captured->files.size() == 1U &&
        captured->files[0] == bytes && prepared_original_font::create(captured, prepared) == S_OK,
        "original authored bytes/face context identity");
    ComPtr<ID2D1Device> device;
    source_context->GetDevice(device.GetAddressOf());
    ComPtr<ID2D1DeviceContext> context;
    require(device->CreateDeviceContext(D2D1_DEVICE_CONTEXT_OPTIONS_NONE, context.GetAddressOf()) == S_OK,
        "original prepared independent context");
    context->SetDpi(96, 96);
    ComPtr<ID2D1Factory> factory;
    context->GetFactory(factory.GetAddressOf());
    com::pointer<compat::factory> typed_factory;
    com::pointer<compat::render_target> typed_target;
    require(factory->QueryInterface(compat::factory_interface_id, reinterpret_cast<void**>(typed_factory.put())) == S_OK &&
        context->QueryInterface(compat::render_target_interface_id, reinterpret_cast<void**>(typed_target.put())) == S_OK,
        "original prepared typed target/factory");
    const auto target_properties = D2D1::BitmapProperties1(D2D1_BITMAP_OPTIONS_TARGET,
        D2D1::PixelFormat(DXGI_FORMAT_B8G8R8A8_UNORM, D2D1_ALPHA_MODE_PREMULTIPLIED), 96, 96);
    const auto read_properties = D2D1::BitmapProperties1(D2D1_BITMAP_OPTIONS_CPU_READ | D2D1_BITMAP_OPTIONS_CANNOT_DRAW,
        target_properties.pixelFormat, 96, 96);
    ComPtr<ID2D1Bitmap1> target, readback;
    require(context->CreateBitmap({64U, 64U}, nullptr, 0U, &target_properties, target.GetAddressOf()) == S_OK &&
        context->CreateBitmap({64U, 64U}, nullptr, 0U, &read_properties, readback.GetAddressOf()) == S_OK,
        "original prepared pixel targets");
    ComPtr<IDWriteRenderingParams> parameters;
    require(write_factory->CreateCustomRenderingParams(1, 0, 0, DWRITE_PIXEL_GEOMETRY_FLAT,
        DWRITE_RENDERING_MODE_OUTLINE, parameters.GetAddressOf()) == S_OK, "original explicit OUTLINE parameters");
    com::pointer<compat::rendering_parameters> typed_parameters;
    require(parameters->QueryInterface(compat::rendering_parameters_interface_id,
        reinterpret_cast<void**>(typed_parameters.put())) == S_OK, "original typed OUTLINE parameters");
    const auto copy_pixels = [&] {
        context->SetTarget(nullptr);
        require(readback->CopyFromBitmap(nullptr, target.Get(), nullptr) == S_OK, "original prepared pixel copy");
        D2D1_MAPPED_RECT mapped{};
        require(readback->Map(D2D1_MAP_OPTIONS_READ, &mapped) == S_OK && mapped.pitch >= 256U,
            "original prepared pixel map");
        std::vector<std::uint8_t> pixels(64U * 256U);
        for (std::size_t row = 0U; row < 64U; ++row)
            std::memcpy(pixels.data() + row * 256U, mapped.bits + row * mapped.pitch, 256U);
        require(readback->Unmap() == S_OK, "original prepared pixel unmap");
        return pixels;
    };
    for (std::uint32_t variant = 0U; variant < 4U; ++variant) {
        const std::uint16_t indices[]{1U, 0U, 2U};
        const float advances[]{24, -3, 9};
        const compat::glyph_offset offsets[]{{0, 0}, {0, 0}, {-0.75F, 2.5F}};
        const compat::glyph_run run{typed_face.get(), 62.5F, 3U, indices, advances, offsets, 0, 2U};
        original_glyph_target frame;
        frame.identity = com::pointer<com::unknown>(typed_target.get());
        frame.generation = origins * 4U + variant + 1U; // Test-owned observation, not a native renderer generation.
        frame.baseline = {3.1875F, 30.8125F}; frame.pixels = {64U, 64U}; frame.dpi_x = 96; frame.dpi_y = 96;
        frame.transform = prepared_pixel_transform(variant); frame.format = {87U, compat::alpha_mode::premultiplied};
        frame.antialias = (variant & 1U) != 0U ? compat::text_antialias_mode::grayscale : compat::text_antialias_mode::aliased;
        typed_target->SetTransform(&frame.transform);
        typed_target->SetTextAntialiasMode(frame.antialias);
        context->SetTarget(target.Get());
        std::shared_ptr<const original_glyph_request> request;
        std::shared_ptr<const prepared_original_glyph_run> glyphs;
        require(capture_original_glyph_request(captured, run, compat::measuring_mode::natural,
            typed_parameters.get(), frame, request) == S_OK && prepared->prepare(request, glyphs) == S_OK,
            "original prepared source occurrence geometry");
        com::pointer<compat::path_geometry> geometry;
        com::pointer<compat::geometry_sink> sink;
        require(typed_factory->CreatePathGeometry(geometry.put()) == S_OK && geometry->Open(sink.put()) == S_OK &&
            glyphs->segments().size() == 8U, "original prepared outline sink");
        sink->SetFillMode(compat::fill_mode::winding);
        for (std::size_t index = 0U; index < glyphs->segments().size(); ++index) {
            const auto& segment = glyphs->segments()[index];
            require(segment.kind == PROGPU_NATIVE_PATH_SEGMENT_LINE, "authored prepared rectangle segment kind");
            if (index % 4U == 0U) sink->BeginFigure({segment.p0.x, segment.p0.y}, compat::figure_begin::filled);
            sink->AddLine({segment.p1.x, segment.p1.y});
            if (index % 4U == 3U) sink->EndFigure(compat::figure_end::closed);
        }
        require(sink->Close() == S_OK, "original prepared outline close");
        std::array<std::vector<std::uint8_t>, 3U> pixels;
        const std::array paths{prepared_pixel_path::original, prepared_pixel_path::independent_geometry,
            prepared_pixel_path::prepared_geometry};
        for (std::size_t index = 0U; index < paths.size(); ++index) {
            context->SetTarget(target.Get());
            record_prepared_pixel_case(typed_factory.get(), typed_target.get(), prepared, typed_parameters.get(),
                variant, paths[index], require, geometry.get(), origins);
            pixels[index] = copy_pixels();
        }
        require(pixels[0] == pixels[1] && pixels[0] == pixels[2],
            "original DrawGlyphRun differs from independent or prepared full-byte placement");
    }
    }
}
} // namespace progpu::native::direct2d::tests
