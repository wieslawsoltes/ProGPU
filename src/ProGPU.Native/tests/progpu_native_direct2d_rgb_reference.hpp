#pragma once

// Windows-only independent observation. No ProGPU coverage or blend formula is
// used to produce these bytes. Include after the original SDK/WRL declarations.
#include <bcrypt.h>
#include <atomic>
#include <bit>
#include <iomanip>
#include <locale>
#include <sstream>
#include <string>
#include "../src/Direct2D/progpu_native_direct2d_font_capture.hpp"

namespace progpu::native::direct2d::tests {

template<class Require>
void capture_original_rgb_parameters(ID2D1DeviceContext* source, IDWriteFactory* write_factory,
    IDWriteFontFace* face, std::uint16_t first_glyph, Require&& require)
{
    using Microsoft::WRL::ComPtr;
    com::pointer<compat::font_face> typed_face;
    void* original_face_interface = nullptr;
    require(face->QueryInterface(compat::font_face_interface_id, &original_face_interface) == S_OK,
        "original typed font-face query failed");
    typed_face.attach(static_cast<compat::font_face*>(original_face_interface));
    std::shared_ptr<const original_font_capture> captured_font;
    require(capture_original_font(typed_face.get(), captured_font) == S_OK && captured_font &&
        captured_font->face_index == face->GetIndex() && captured_font->face_type == face->GetType() &&
        captured_font->simulations == face->GetSimulations(), "owned original font capture failed");
    constexpr std::uint32_t width = 32U, height = 24U, stride = width * 4U;
    struct policy final { float gamma, contrast, level; DWRITE_PIXEL_GEOMETRY geometry; DWRITE_RENDERING_MODE mode; };
    constexpr std::array policies{
        policy{1, 0, 1, DWRITE_PIXEL_GEOMETRY_RGB, DWRITE_RENDERING_MODE_NATURAL_SYMMETRIC},
        policy{1, 0, 1, DWRITE_PIXEL_GEOMETRY_BGR, DWRITE_RENDERING_MODE_NATURAL_SYMMETRIC},
        policy{1, 0, 1, DWRITE_PIXEL_GEOMETRY_FLAT, DWRITE_RENDERING_MODE_NATURAL_SYMMETRIC},
        policy{1.8F, 0, 1, DWRITE_PIXEL_GEOMETRY_RGB, DWRITE_RENDERING_MODE_NATURAL_SYMMETRIC},
        policy{2.2F, 0, 1, DWRITE_PIXEL_GEOMETRY_RGB, DWRITE_RENDERING_MODE_NATURAL_SYMMETRIC},
        policy{1, 0.5F, 1, DWRITE_PIXEL_GEOMETRY_RGB, DWRITE_RENDERING_MODE_NATURAL_SYMMETRIC},
        policy{1, 0, 0, DWRITE_PIXEL_GEOMETRY_RGB, DWRITE_RENDERING_MODE_NATURAL_SYMMETRIC},
        policy{1, 0, 0.5F, DWRITE_PIXEL_GEOMETRY_RGB, DWRITE_RENDERING_MODE_NATURAL_SYMMETRIC},
        policy{1, 0, 1, DWRITE_PIXEL_GEOMETRY_RGB, DWRITE_RENDERING_MODE_NATURAL}};
    constexpr std::array colors{
        D2D1_COLOR_F{0, 0, 0, 1}, D2D1_COLOR_F{0.25F, 0.5F, 0.75F, 1},
        D2D1_COLOR_F{0.25F, 0.5F, 0.75F, 0.5F}};
    constexpr std::array phases{0.0F, 0.25F};
    static std::atomic_uint32_t sequence{0U};
    const auto capture_index = sequence.fetch_add(1U);
    require(capture_index < 16U, "original RGB source capture count exceeds test inventory");
    const std::wstring directory = L"direct2d-rgb-reference-" + std::to_wstring(GetCurrentProcessId()) +
        L"-" + std::to_wstring(capture_index);
    require(CreateDirectoryW(directory.c_str(), nullptr) != FALSE,
        "original RGB reference directory must be new");
    const auto write_new = [&](const std::wstring& name, const void* data, std::size_t size) {
        require(size <= std::numeric_limits<DWORD>::max(), "original RGB receipt exceeds file bound");
        const auto path = directory + L"/" + name;
        HANDLE file = CreateFileW(path.c_str(), GENERIC_WRITE, 0, nullptr, CREATE_NEW, FILE_ATTRIBUTE_NORMAL, nullptr);
        require(file != INVALID_HANDLE_VALUE, "original RGB receipt must not overwrite an earlier file");
        DWORD written = 0U;
        const bool complete = WriteFile(file, data, static_cast<DWORD>(size), &written, nullptr) != FALSE && written == size;
        const bool closed = CloseHandle(file) != FALSE;
        require(complete && closed, "original RGB receipt write failed");
    };
    const auto digest = [&](const void* data, std::size_t size) {
        require(size <= std::numeric_limits<ULONG>::max(), "original RGB hash input exceeds bound");
        BCRYPT_ALG_HANDLE algorithm = nullptr;
        require(BCryptOpenAlgorithmProvider(&algorithm, BCRYPT_SHA256_ALGORITHM, nullptr, 0U) >= 0,
            "original RGB SHA256 provider failed");
        BCRYPT_HASH_HANDLE hash = nullptr;
        require(BCryptCreateHash(algorithm, &hash, nullptr, 0U, nullptr, 0U, 0U) >= 0,
            "original RGB SHA256 owner failed");
        std::array<UCHAR, 32U> bytes{};
        const bool complete = BCryptHashData(hash, const_cast<PUCHAR>(static_cast<const UCHAR*>(data)),
            static_cast<ULONG>(size), 0U) >= 0 && BCryptFinishHash(hash, bytes.data(), static_cast<ULONG>(bytes.size()), 0U) >= 0;
        const bool hash_closed = BCryptDestroyHash(hash) >= 0;
        const bool algorithm_closed = BCryptCloseAlgorithmProvider(algorithm, 0U) >= 0;
        require(complete && hash_closed && algorithm_closed, "original RGB SHA256 capture failed");
        std::ostringstream value;
        value.imbue(std::locale::classic());
        value << std::hex << std::setfill('0');
        for (const auto byte : bytes) value << std::setw(2) << static_cast<unsigned>(byte);
        return value.str();
    };
    const auto bits = [](float value) { return std::bit_cast<std::uint32_t>(value); };
    std::ostringstream manifest;
    manifest.imbue(std::locale::classic());
    manifest << "{\"schema\":1,\"producer\":\"original-Windows-Direct2D\",\"floatEncoding\":\"ieee754-binary32-bits\","
        "\"width\":" << width << ",\"height\":" << height << ",\"stride\":" << stride <<
        ",\"pixelFormat\":\"BGRA8Unorm\",\"alphaMode\":\"IGNORE\",\"dpi\":[" << bits(96) << ',' << bits(96) <<
        "],\"background\":[" << bits(1) << ',' << bits(1) << ',' << bits(1) << ',' << bits(1) <<
        "],\"fontEm\":" << bits(16) << ",\"glyph\":" << first_glyph <<
        ",\"advance\":" << bits(16) << ",\"offset\":[0,0],\"sideways\":false,\"bidiLevel\":0,\"faceIndex\":" <<
        face->GetIndex() << ",\"faceType\":" << static_cast<unsigned>(face->GetType()) <<
        ",\"simulations\":" << static_cast<unsigned>(face->GetSimulations()) << ",\"fontFiles\":[";

    UINT32 file_count = 0U;
    require(face->GetFiles(&file_count, nullptr) == S_OK && file_count > 0U && file_count <= 4U,
        "original RGB face file inventory failed");
    require(captured_font->files.size() == file_count,
        "owned original font file count differs from independent source inventory");
    std::array<IDWriteFontFile*, 4U> files{};
    const auto expected_count = file_count;
    require(face->GetFiles(&file_count, files.data()) == S_OK && file_count == expected_count,
        "original RGB face file inventory changed");
    for (UINT32 index = 0U; index < file_count; ++index) {
        ComPtr<IDWriteFontFile> file;
        file.Attach(files[index]);
        const void* key = nullptr;
        UINT32 key_size = 0U;
        ComPtr<IDWriteFontFileLoader> loader;
        ComPtr<IDWriteFontFileStream> stream;
        require(file->GetReferenceKey(&key, &key_size) == S_OK && file->GetLoader(loader.GetAddressOf()) == S_OK &&
            loader->CreateStreamFromKey(key, key_size, stream.GetAddressOf()) == S_OK,
            "original RGB immutable font stream failed");
        UINT64 size = 0U;
        require(stream->GetFileSize(&size) == S_OK && size > 0U && size <= 64U * 1024U * 1024U,
            "original RGB font file exceeds capture bound");
        const void* bytes = nullptr;
        void* owner = nullptr;
        require(stream->ReadFileFragment(&bytes, 0U, size, &owner) == S_OK && bytes != nullptr,
            "original RGB font file fragment failed");
        const auto name = "font-" + std::to_string(index) + ".bin";
        write_new(std::wstring(name.begin(), name.end()), bytes, static_cast<std::size_t>(size));
        const auto sha256 = digest(bytes, static_cast<std::size_t>(size));
        require(index < captured_font->files.size() && captured_font->files[index].size() == size &&
            std::memcmp(bytes, captured_font->files[index].data(), static_cast<std::size_t>(size)) == 0,
            "owned original font differs from independent original file bytes");
        stream->ReleaseFileFragment(owner);
        if (index != 0U) manifest << ',';
        manifest << "{\"file\":\"" << name << "\",\"bytes\":" << size << ",\"sha256\":\"" << sha256 << "\"}";
    }
    manifest << "],\"modules\":[";
    bool first_module = true;
    for (const wchar_t* name : {L"dwrite.dll", L"d2d1.dll"}) {
        const auto module = GetModuleHandleW(name);
        std::array<wchar_t, 32768U> path{};
        const auto path_size = GetModuleFileNameW(module, path.data(), static_cast<DWORD>(path.size()));
        require(module != nullptr && path_size != 0U && path_size < path.size(), "original RGB loaded module identity failed");
        HANDLE file = CreateFileW(path.data(), GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
            nullptr, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr);
        require(file != INVALID_HANDLE_VALUE, "original RGB loaded module file failed");
        LARGE_INTEGER size{};
        require(GetFileSizeEx(file, &size) != FALSE && size.QuadPart > 0 && size.QuadPart <= 64LL * 1024LL * 1024LL,
            "original RGB loaded module file exceeds bound");
        std::vector<UCHAR> bytes(static_cast<std::size_t>(size.QuadPart));
        DWORD read = 0U;
        const bool complete = ReadFile(file, bytes.data(), static_cast<DWORD>(bytes.size()), &read, nullptr) != FALSE && read == bytes.size();
        const bool closed = CloseHandle(file) != FALSE;
        require(complete && closed, "original RGB loaded module hash read failed");
        if (!first_module) manifest << ',';
        first_module = false;
        manifest << "{\"path\":\"" << std::hex << std::setfill('0');
        for (DWORD index = 0U; index < path_size; ++index)
            manifest << "\\u" << std::setw(4) << static_cast<unsigned>(path[index]);
        manifest << std::dec << "\",\"bytes\":" << bytes.size() << ",\"sha256\":\"" << digest(bytes.data(), bytes.size()) << "\"}";
    }
    manifest << "],\"cases\":[";

    ComPtr<ID2D1Device> device;
    source->GetDevice(device.GetAddressOf());
    ComPtr<ID2D1DeviceContext> context;
    require(device->CreateDeviceContext(D2D1_DEVICE_CONTEXT_OPTIONS_NONE, context.GetAddressOf()) == S_OK,
        "original RGB context creation failed");
    context->SetDpi(96, 96);
    context->SetTransform(D2D1::Matrix3x2F::Identity());
    const auto target_properties = D2D1::BitmapProperties1(D2D1_BITMAP_OPTIONS_TARGET,
        D2D1::PixelFormat(DXGI_FORMAT_B8G8R8A8_UNORM, D2D1_ALPHA_MODE_IGNORE), 96, 96);
    const auto read_properties = D2D1::BitmapProperties1(D2D1_BITMAP_OPTIONS_CPU_READ | D2D1_BITMAP_OPTIONS_CANNOT_DRAW,
        target_properties.pixelFormat, 96, 96);
    ComPtr<ID2D1Bitmap1> target, readback;
    require(context->CreateBitmap(D2D1::SizeU(width, height), nullptr, 0U, &target_properties, target.GetAddressOf()) == S_OK &&
        context->CreateBitmap(D2D1::SizeU(width, height), nullptr, 0U, &read_properties, readback.GetAddressOf()) == S_OK,
        "original RGB target/readback creation failed");
    const auto copy_pixels = [&] {
        context->SetTarget(nullptr);
        require(readback->CopyFromBitmap(nullptr, target.Get(), nullptr) == S_OK, "original RGB pixel copy failed");
        D2D1_MAPPED_RECT mapped{};
        require(readback->Map(D2D1_MAP_OPTIONS_READ, &mapped) == S_OK && mapped.pitch >= stride, "original RGB pixel map failed");
        std::vector<std::uint8_t> pixels(stride * height);
        for (std::size_t row = 0U; row < height; ++row)
            std::memcpy(pixels.data() + row * stride, mapped.bits + row * mapped.pitch, stride);
        require(readback->Unmap() == S_OK, "original RGB pixel unmap failed");
        return pixels;
    };
    std::uint32_t case_id = 0U;
    for (const auto& selected : policies) {
        ComPtr<IDWriteRenderingParams> parameters;
        require(write_factory->CreateCustomRenderingParams(selected.gamma, selected.contrast, selected.level,
            selected.geometry, selected.mode, parameters.GetAddressOf()) == S_OK,
            "original RGB caller rendering parameters failed");
        require(parameters->GetGamma() == selected.gamma && parameters->GetEnhancedContrast() == selected.contrast &&
            parameters->GetClearTypeLevel() == selected.level && parameters->GetPixelGeometry() == selected.geometry &&
            parameters->GetRenderingMode() == selected.mode, "original RGB parameter readback changed");
        com::pointer<compat::rendering_parameters> typed_parameters;
        void* original_parameter_interface = nullptr;
        require(parameters->QueryInterface(compat::rendering_parameters_interface_id, &original_parameter_interface) == S_OK,
            "original typed rendering-parameters query failed");
        typed_parameters.attach(static_cast<compat::rendering_parameters*>(original_parameter_interface));
        for (const auto& color : colors) for (const auto phase : phases) {
            ComPtr<ID2D1SolidColorBrush> brush;
            require(context->CreateSolidColorBrush(&color, nullptr, brush.GetAddressOf()) == S_OK,
                "original RGB foreground creation failed");
            constexpr float advance = 16.0F;
            const DWRITE_GLYPH_OFFSET offset{};
            const DWRITE_GLYPH_RUN run{face, 16.0F, 1U, &first_glyph, &advance, &offset, FALSE, 0U};
            const D2D1_POINT_2F baseline{4.0F + phase, 19.0F};
            context->SetTarget(target.Get());
            context->BeginDraw();
            context->Clear(D2D1::ColorF(D2D1::ColorF::White));
            context->SetTextAntialiasMode(D2D1_TEXT_ANTIALIAS_MODE_CLEARTYPE);
            context->SetTextRenderingParams(parameters.Get());
            // Capture the actual original target and typed source request before
            // drawing. This test owns one observation generation per case; it is
            // not an engine/device generation or proof of modern RGB replay.
            original_glyph_target captured_target;
            captured_target.identity = com::pointer<com::unknown>(context.Get());
            captured_target.generation = static_cast<std::uint64_t>(case_id) + 1U;
            D2D1_MATRIX_3X2_F original_transform{};
            context->GetTransform(&original_transform);
            captured_target.transform = {original_transform._11, original_transform._12,
                original_transform._21, original_transform._22, original_transform._31, original_transform._32};
            captured_target.baseline = {baseline.x, baseline.y};
            const auto original_pixels = target->GetPixelSize();
            captured_target.pixels = {original_pixels.width, original_pixels.height};
            context->GetDpi(&captured_target.dpi_x, &captured_target.dpi_y);
            const auto original_format = target->GetPixelFormat();
            captured_target.format = {static_cast<std::uint32_t>(original_format.format),
                static_cast<compat::alpha_mode>(original_format.alphaMode)};
            captured_target.antialias = static_cast<compat::text_antialias_mode>(context->GetTextAntialiasMode());
            D2D1_TAG tag1 = 0U, tag2 = 0U;
            context->GetTags(&tag1, &tag2);
            captured_target.tag1 = tag1; captured_target.tag2 = tag2;
            captured_target.units = static_cast<compat::unit_mode>(context->GetUnitMode());
            captured_target.blend = static_cast<compat::primitive_blend>(context->GetPrimitiveBlend());
            const compat::glyph_offset original_offset{offset.advanceOffset, offset.ascenderOffset};
            const compat::glyph_run original_run{typed_face.get(), run.fontEmSize, run.glyphCount,
                run.glyphIndices, run.glyphAdvances, &original_offset, run.isSideways, run.bidiLevel};
            std::shared_ptr<const original_glyph_request> captured_request;
            require(capture_original_glyph_request(captured_font, original_run, compat::measuring_mode::natural,
                typed_parameters.get(), captured_target, captured_request) == S_OK && captured_request &&
                captured_request->font == captured_font && captured_request->glyphs.count() == 1U &&
                captured_request->glyphs.indices()[0] == first_glyph && captured_request->glyphs.advances()[0] == advance &&
                captured_request->glyphs.offsets()[0].advance_offset == offset.advanceOffset &&
                captured_request->glyphs.offsets()[0].ascender_offset == offset.ascenderOffset &&
                captured_request->em_size == run.fontEmSize && captured_request->sideways == run.isSideways &&
                captured_request->bidi_level == run.bidiLevel &&
                captured_request->target.identity.get() == static_cast<IUnknown*>(context.Get()) &&
                captured_request->target.baseline.x == baseline.x && captured_request->target.baseline.y == baseline.y &&
                captured_request->target.dpi_x == 96 && captured_request->target.dpi_y == 96 &&
                captured_request->target.pixels.width == original_pixels.width &&
                captured_request->target.pixels.height == original_pixels.height &&
                captured_request->target.format.format == static_cast<std::uint32_t>(original_format.format) &&
                captured_request->target.format.alpha == static_cast<compat::alpha_mode>(original_format.alphaMode) &&
                captured_request->target.units == static_cast<compat::unit_mode>(context->GetUnitMode()) &&
                captured_request->target.blend == static_cast<compat::primitive_blend>(context->GetPrimitiveBlend()) &&
                captured_request->rendering.supplied && captured_request->rendering.gamma == selected.gamma &&
                captured_request->rendering.enhanced_contrast == selected.contrast &&
                captured_request->rendering.cleartype_level == selected.level &&
                captured_request->rendering.pixel_geometry == static_cast<std::uint32_t>(selected.geometry) &&
                captured_request->rendering.rendering_mode == static_cast<std::uint32_t>(selected.mode),
                "owned original glyph request changed source identity or policy");
            context->DrawGlyphRun(baseline, &run, brush.Get(), DWRITE_MEASURING_MODE_NATURAL);
            require(context->EndDraw() == S_OK, "original RGB direct glyph draw failed");
            const auto direct = copy_pixels();

            ComPtr<ID2D1CommandList> list;
            require(context->CreateCommandList(list.GetAddressOf()) == S_OK, "original RGB command list creation failed");
            context->SetTarget(list.Get());
            context->BeginDraw();
            context->SetTextAntialiasMode(D2D1_TEXT_ANTIALIAS_MODE_CLEARTYPE);
            context->SetTextRenderingParams(parameters.Get());
            context->DrawGlyphRun(baseline, &run, brush.Get(), DWRITE_MEASURING_MODE_NATURAL);
            require(context->EndDraw() == S_OK && list->Close() == S_OK, "original RGB command list recording failed");
            context->SetTarget(target.Get());
            context->BeginDraw();
            context->Clear(D2D1::ColorF(D2D1::ColorF::White));
            context->DrawImage(list.Get());
            require(context->EndDraw() == S_OK, "original RGB command list replay failed");
            const auto replay = copy_pixels();
            require(replay == direct, "original RGB direct and command-list full pixels differ");
            bool ink = false;
            for (std::size_t index = 0U; index < direct.size(); index += 4U)
                ink |= direct[index] != 255U || direct[index + 1U] != 255U || direct[index + 2U] != 255U;
            require(ink, "original RGB glyph produced no observed ink");
            const auto file = "case-" + std::to_string(case_id) + ".bgra";
            write_new(std::wstring(file.begin(), file.end()), direct.data(), direct.size());
            if (case_id != 0U) manifest << ',';
            manifest << "{\"id\":" << case_id++ << ",\"file\":\"" << file << "\",\"sha256\":\"" <<
                digest(direct.data(), direct.size()) << "\",\"gamma\":" << bits(selected.gamma) <<
                ",\"contrast\":" << bits(selected.contrast) << ",\"clearTypeLevel\":" << bits(selected.level) <<
                ",\"pixelGeometry\":" << static_cast<unsigned>(selected.geometry) << ",\"renderingMode\":" << static_cast<unsigned>(selected.mode) <<
                ",\"measuringMode\":0,\"textAntialiasMode\":1,\"baseline\":[" << bits(baseline.x) << ',' << bits(baseline.y) <<
                "],\"foreground\":[" << bits(color.r) << ',' << bits(color.g) << ',' << bits(color.b) << ',' << bits(color.a) <<
                "],\"commandListFullBytesEqual\":true}";
        }
    }
    require(case_id == 54U, "original RGB parameter inventory changed");
    manifest << "],\"caseCount\":" << case_id << ",\"complete\":true}";
    const auto receipt = manifest.str();
    write_new(L"reference.json", receipt.data(), receipt.size());
    std::wcout << L"Original RGB source receipts: " << directory << L"/reference.json (54 complete cases)\n";
}

} // namespace progpu::native::direct2d::tests
