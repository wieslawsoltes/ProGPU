#pragma once

#include "progpu_native_direct2d_prepared_glyph_fixture.hpp"
#include <algorithm>
#include <cstdio>

// Windows SDK declarations precede this header. The genuine original font
// loader and DrawGlyphRun own the oracle; no product rasterizer produces it.
namespace progpu::native::direct2d::tests {

class original_prepared_rectangle_sink final : public ID2D1SimplifiedGeometrySink {
public:
    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID id, void** output) override {
        if (output == nullptr) return E_POINTER;
        *output = nullptr;
        if (id != __uuidof(IUnknown) && id != __uuidof(ID2D1SimplifiedGeometrySink)) return E_NOINTERFACE;
        *output = static_cast<ID2D1SimplifiedGeometrySink*>(this); AddRef(); return S_OK;
    }
    ULONG STDMETHODCALLTYPE AddRef() override { return ++references_; }
    ULONG STDMETHODCALLTYPE Release() override { return --references_; }
    void STDMETHODCALLTYPE SetFillMode(D2D1_FILL_MODE) noexcept override {}
    void STDMETHODCALLTYPE SetSegmentFlags(D2D1_PATH_SEGMENT) noexcept override {}
    void STDMETHODCALLTYPE BeginFigure(D2D1_POINT_2F point, D2D1_FIGURE_BEGIN begin) noexcept override {
        if (open_ || count_ == bounds.size() || begin != D2D1_FIGURE_BEGIN_FILLED) { valid_ = false; return; }
        bounds[count_++] = {point.x, point.y, point.x, point.y}; open_ = true;
    }
    void STDMETHODCALLTYPE AddLines(const D2D1_POINT_2F* points, UINT32 count) noexcept override {
        if (!open_ || (points == nullptr && count != 0U)) { valid_ = false; return; }
        auto& box = bounds[count_ - 1U];
        for (UINT32 i = 0U; i < count; ++i) {
            box.left = std::min(box.left, points[i].x); box.right = std::max(box.right, points[i].x);
            box.top = std::min(box.top, points[i].y); box.bottom = std::max(box.bottom, points[i].y);
        }
    }
    void STDMETHODCALLTYPE AddBeziers(const D2D1_BEZIER_SEGMENT*, UINT32) noexcept override { valid_ = false; }
    void STDMETHODCALLTYPE EndFigure(D2D1_FIGURE_END end) noexcept override {
        if (!open_ || end != D2D1_FIGURE_END_CLOSED) valid_ = false;
        open_ = false;
    }
    HRESULT STDMETHODCALLTYPE Close() noexcept override { return complete() ? S_OK : E_FAIL; }
    bool complete() const noexcept { return valid_ && !open_ && count_ == bounds.size() && references_ == 1U; }
    std::array<D2D1_RECT_F, 2U> bounds{};
private:
    ULONG references_ = 1U;
    std::size_t count_ = 0U;
    bool open_ = false, valid_ = true;
};

template<class Require>
void verify_original_prepared_glyph_pixels(ID2D1DeviceContext* source_context,
    IDWriteFactory* write_factory, Require require)
{
    std::uint32_t pixel_cases = 0U;
    std::uint32_t placement_failures = 0U;
    std::uint32_t nominal_failures = 0U;
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
    // Query the genuine original face, not the product parser or prepared cache.
    // All three authored horizontal advances, including the no-ink glyph, are
    // independently known to be 500 design units at UPM 1000.
    ComPtr<IDWriteFontFace1> metric_face;
    require(face.As(&metric_face) == S_OK, "original nominal design-metric interface");
    const std::uint16_t indices[]{1U, 0U, 2U};
    std::array<INT32, 3U> design_advances{};
    DWRITE_FONT_METRICS metrics{};
    face->GetMetrics(&metrics);
    require(metric_face->GetDesignGlyphAdvances(3U, indices, design_advances.data(), FALSE) == S_OK &&
        metrics.designUnitsPerEm == 1000U && design_advances == std::array<INT32, 3U>{500, 500, 500},
        "original nominal horizontal design metrics");
    std::array<float, 3U> nominal_advances{};
    for (std::size_t index = 0U; index < nominal_advances.size(); ++index) {
        nominal_advances[index] = static_cast<float>(design_advances[index]) *
            (31.25F / static_cast<float>(metrics.designUnitsPerEm));
        require(nominal_advances[index] == 15.625F, "original nominal DIP advance");
    }
    for (const bool right_to_left : {false, true}) {
    for (const bool nominal : {false, true}) {
    for (std::uint32_t variant = 0U; variant < 4U; ++variant) {
        const float advances[]{24, -3, right_to_left ? 24.0F : 9.0F};
        const compat::glyph_offset offsets[]{{0, 0}, {0, 0}, {-0.75F, 2.5F}};
        const compat::glyph_run run{typed_face.get(), nominal ? 31.25F : 62.5F,
            3U, indices, nominal ? nullptr : advances, offsets, 0, right_to_left ? 3U : 2U};
        original_glyph_target frame;
        frame.identity = com::pointer<com::unknown>(typed_target.get());
        frame.generation = (right_to_left ? 24U : 0U) + (nominal ? 12U : 0U) + origins * 4U + variant + 1U; // Test-owned observation, not a native renderer generation.
        frame.baseline = prepared_pixel_baseline(nominal, right_to_left);
        frame.pixels = {64U, 64U}; frame.dpi_x = 96; frame.dpi_y = 96;
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
        require((glyphs->request().glyphs.advances() == nullptr) == nominal,
            "nominal source absence retained without materializing a replacement array");
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
        // Observe each original source occurrence independently of both the
        // product decoder and the SDK rasterizer's antialiasing policy.
        original_prepared_rectangle_sink original_outline;
        const DWRITE_GLYPH_OFFSET original_offsets[]{{0, 0}, {0, 0}, {-0.75F, 2.5F}};
        require(face->GetGlyphRunOutline(run.font_em_size, indices, nominal ? nullptr : advances,
            original_offsets, 3U, FALSE, right_to_left, &original_outline) == S_OK && original_outline.complete(),
            "original prepared logical-run outline observation");
        const auto literal = prepared_pixel_rectangles(origins, nominal, right_to_left);
        for (std::size_t glyph = 0U; glyph < literal.size(); ++glyph) {
            const auto& observed = original_outline.bounds[glyph];
            const auto& expected = literal[glyph];
            require(observed.left + frame.baseline.x == expected.left &&
                observed.top + frame.baseline.y == expected.top &&
                observed.right + frame.baseline.x == expected.right &&
                observed.bottom + frame.baseline.y == expected.bottom,
                "original outline occurrence differs from independent design-width/positioned-advance placement");
        }
        std::array<std::vector<std::uint8_t>, 4U> pixels;
        const std::array paths{prepared_pixel_path::original, prepared_pixel_path::independent_geometry,
            prepared_pixel_path::prepared_geometry, prepared_pixel_path::original_design_advances};
        const std::size_t path_count = nominal ? 4U : 3U;
        for (std::size_t index = 0U; index < path_count; ++index) {
            context->SetTarget(target.Get());
            record_prepared_pixel_case(typed_factory.get(), typed_target.get(), prepared, typed_parameters.get(),
                variant, paths[index], require, geometry.get(), origins, nominal, nominal_advances.data(), right_to_left);
            pixels[index] = copy_pixels();
        }
        for (std::size_t index = 1U; index < path_count; ++index) {
            if (pixels[0] == pixels[index]) continue;
            const auto difference = static_cast<std::size_t>(
                std::mismatch(pixels[0].begin(), pixels[0].end(), pixels[index].begin()).first - pixels[0].begin());
            std::fprintf(stderr, "Original prepared glyph origins=%u nominal=%u rtl=%u variant=%u path=%zu "
                "pixel=(%zu,%zu) channel=%zu original=%u comparison=%u independent-prepared-equal=%u\n",
                origins, unsigned(nominal), unsigned(right_to_left), variant, index,
                (difference / 4U) % 64U, difference / 256U, difference % 4U,
                unsigned(pixels[0][difference]), unsigned(pixels[index][difference]), unsigned(pixels[1] == pixels[2]));
        }
        // Complete the original inventory before rejecting the run. A first
        // grayscale mismatch otherwise hides later origin, size and RTL
        // differences, encouraging a fix based on only one source frame.
        // Structural/native failures still reject immediately through require.
        ++pixel_cases;
        if (pixels[0] != pixels[1] || pixels[0] != pixels[2]) ++placement_failures;
        if (nominal && pixels[0] != pixels[3]) ++nominal_failures;
    }
    }
    }
    }
    std::fprintf(stderr, "Original prepared glyph cases=%u placement-failures=%u nominal-failures=%u\n",
        pixel_cases, placement_failures, nominal_failures);
    require(pixel_cases == 48U, "original prepared glyph pixel inventory changed");
    require(placement_failures == 0U,
        "original DrawGlyphRun differs from independent or prepared full-byte placement");
    require(nominal_failures == 0U,
        "original null advances differ from original explicit horizontal design advances");
}
} // namespace progpu::native::direct2d::tests
