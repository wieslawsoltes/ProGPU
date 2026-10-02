#include "../src/Direct2D/progpu_native_direct2d_font_capture.hpp"

#include <array>
#include <cstdio>
#include <cstring>
#include <limits>

#if defined(_WIN32)
#include <dwrite.h>
#endif

namespace {
namespace capture = progpu::native::direct2d;
namespace compat = capture::compat;
namespace com = progpu::native::com;

// Stack-owned source objects keep observable reference/fragment balances; no
// renderer, native font parser or device is involved in these source contracts.
template<typename Interface>
class source_object : public Interface {
public:
    com::result PROGPU_NATIVE_COM_CALL QueryInterface(com::guid_ref id, void** output) noexcept override
    {
        if (output == nullptr) return com::pointer_error;
        *output = nullptr;
        if (com::failed(query_result)) return query_result;
        if (!com::guid_equal(id, com::unknown_interface_id()) &&
            (interface_id == nullptr || !com::guid_equal(id, *interface_id))) return com::no_interface;
        *output = static_cast<Interface*>(this);
        AddRef();
        return com::ok;
    }
    com::reference_count_value PROGPU_NATIVE_COM_CALL AddRef() noexcept override { return ++references; }
    com::reference_count_value PROGPU_NATIVE_COM_CALL Release() noexcept override { return --references; }
    std::uint32_t references = 1U;
    const com::guid* interface_id = nullptr;
    com::result query_result = com::ok;
};

class font_stream final : public source_object<capture::original_font_stream> {
public:
    com::result PROGPU_NATIVE_COM_CALL ReadFileFragment(const void** data, std::uint64_t offset,
        std::uint64_t size, void** context) noexcept override
    {
        ++reads;
        if (com::failed(read_result)) return read_result;
        if (offset != 0U || size != bytes.size()) return com::invalid_argument;
        *data = null_data ? nullptr : bytes.data();
        *context = null_context ? nullptr : this;
        return com::ok;
    }
    void PROGPU_NATIVE_COM_CALL ReleaseFileFragment(void* context) noexcept override
    {
        ++releases;
        bad_release = bad_release || context != (null_context ? nullptr : this);
    }
    com::result PROGPU_NATIVE_COM_CALL GetFileSize(std::uint64_t* size) noexcept override
    {
        *size = declared_size;
        return size_result;
    }
    std::array<std::byte, 8U> bytes{std::byte{1}, std::byte{2}, std::byte{3}, std::byte{4},
        std::byte{5}, std::byte{6}, std::byte{7}, std::byte{8}};
    std::uint64_t declared_size = bytes.size();
    std::uint32_t reads = 0U, releases = 0U;
    com::result size_result = com::ok, read_result = com::ok;
    bool null_data = false, null_context = false, bad_release = false;
};

class font_loader final : public source_object<capture::original_font_loader> {
public:
    com::result PROGPU_NATIVE_COM_CALL CreateStreamFromKey(const void* key, std::uint32_t size,
        capture::original_font_stream** output) noexcept override
    {
        *output = nullptr;
        if (com::failed(create_result)) return create_result;
        if (key == nullptr || size != sizeof(expected_key) ||
            std::memcmp(key, &expected_key, size) != 0) return com::invalid_argument;
        if (stream != nullptr) { stream->AddRef(); *output = stream; }
        return com::ok;
    }
    font_stream* stream = nullptr;
    std::uint32_t expected_key = 0xEA132456U;
    com::result create_result = com::ok;
};

class font_file final : public source_object<capture::original_font_file> {
public:
    font_file() { interface_id = &capture::original_font_file_id; }
    com::result PROGPU_NATIVE_COM_CALL GetReferenceKey(const void** value, std::uint32_t* size) noexcept override
    {
        *value = null_key ? nullptr : &key;
        *size = key_size;
        return key_result;
    }
    com::result PROGPU_NATIVE_COM_CALL GetLoader(capture::original_font_loader** output) noexcept override
    {
        // Prove the opaque key was copied before this source callback.
        if (change_key) key = 0U;
        *output = nullptr;
        if (com::failed(loader_result)) return loader_result;
        if (loader != nullptr) { loader->AddRef(); *output = loader; }
        return com::ok;
    }
    font_loader* loader = nullptr;
    std::uint32_t key = 0xEA132456U, key_size = sizeof(key);
    com::result key_result = com::ok, loader_result = com::ok;
    bool null_key = false, change_key = false;
};

class font_face final : public source_object<compat::font_face> {
public:
    font_face() { interface_id = &compat::font_face_interface_id; }
    std::uint32_t PROGPU_NATIVE_COM_CALL GetType() noexcept override { return 3U; }
    std::uint32_t PROGPU_NATIVE_COM_CALL GetIndex() noexcept override { return 2U; }
    std::uint32_t PROGPU_NATIVE_COM_CALL GetSimulations() noexcept override { return 3U; }
    std::int32_t PROGPU_NATIVE_COM_CALL IsSymbolFont() noexcept override { return 1; }
    std::uint16_t PROGPU_NATIVE_COM_CALL GetGlyphCount() noexcept override { return 400U; }
    com::result PROGPU_NATIVE_COM_CALL GetFiles(std::uint32_t* count, com::unknown** output) noexcept override
    {
        if (output == nullptr) { *count = declared_count; return count_result; }
        for (std::uint32_t i = 0; i < *count && i < files.size(); ++i) {
            output[i] = files[i];
            if (output[i] != nullptr) output[i]->AddRef();
        }
        if (change_count) *count = 1U;
        return files_result;
    }
    void PROGPU_NATIVE_COM_CALL GetMetrics(void*) noexcept override {}
    com::result PROGPU_NATIVE_COM_CALL GetDesignGlyphMetrics(const std::uint16_t*, std::uint32_t,
        void*, std::int32_t) noexcept override { return compat::not_implemented; }
    com::result PROGPU_NATIVE_COM_CALL GetGlyphIndices(const std::uint32_t*, std::uint32_t,
        std::uint16_t*) noexcept override { return compat::not_implemented; }
    com::result PROGPU_NATIVE_COM_CALL TryGetFontTable(std::uint32_t, const void**, std::uint32_t*,
        void**, std::int32_t*) noexcept override { ++table_calls; return compat::not_implemented; }
    void PROGPU_NATIVE_COM_CALL ReleaseFontTable(void*) noexcept override {}
    com::result PROGPU_NATIVE_COM_CALL GetGlyphRunOutline(float, const std::uint16_t*, const float*,
        const compat::glyph_offset*, std::uint32_t, std::int32_t, std::int32_t,
        compat::simplified_geometry_sink*) noexcept override { ++outline_calls; return compat::not_implemented; }
    std::array<com::unknown*, 2U> files{};
    std::uint32_t declared_count = 2U, table_calls = 0U, outline_calls = 0U;
    com::result count_result = com::ok, files_result = com::ok;
    bool change_count = false;
};

class rendering_parameters final : public source_object<compat::rendering_parameters> {
public:
    float PROGPU_NATIVE_COM_CALL GetGamma() noexcept override
    {
        ++reads;
        if (callback != nullptr) callback(context);
        return gamma;
    }
    float PROGPU_NATIVE_COM_CALL GetEnhancedContrast() noexcept override { return 0.5F; }
    float PROGPU_NATIVE_COM_CALL GetClearTypeLevel() noexcept override { return 0.25F; }
    compat::pixel_geometry PROGPU_NATIVE_COM_CALL GetPixelGeometry() noexcept override
    { return compat::pixel_geometry::bgr; }
    compat::rendering_mode PROGPU_NATIVE_COM_CALL GetRenderingMode() noexcept override
    { return compat::rendering_mode::natural_symmetric; }
    float gamma = 1.8F;
    std::uint32_t reads = 0U;
    void (*callback)(void*) noexcept = nullptr;
    void* context = nullptr;
};

[[nodiscard]] bool check(bool value, const char* message)
{
    if (!value) std::fprintf(stderr, "original font capture: %s\n", message);
    return value;
}

[[nodiscard]] bool source_contracts()
{
    font_stream first_stream, second_stream;
    second_stream.bytes[0] = std::byte{0xCA};
    font_loader first_loader, second_loader;
    first_loader.stream = &first_stream;
    second_loader.stream = &second_stream;
    font_file first_file, second_file;
    first_file.loader = &first_loader;
    second_file.loader = &second_loader;
    font_face face;
    face.files = {&first_file, &second_file};
    std::shared_ptr<const capture::original_font_capture> font;
    if (!check(capture::capture_original_font(&face, font) == com::ok && font &&
        font->face.get() == &face && font->face_index == 2U && font->face_type == 3U &&
        font->simulations == 3U && font->symbol_font == 1 && font->glyph_count == 400U &&
        font->files.size() == 2U && font->files[0][0] == std::byte{1} &&
        font->files[1][0] == std::byte{0xCA}, "exact multi-file identity/order")) return false;
    const auto retained = font;
    first_stream.bytes[0] = std::byte{0xBB};
    if (!check(font->files[0][0] == std::byte{1}, "borrowed bytes escaped capture")) return false;
    const auto balanced = [&] {
        return first_file.references == 1U && second_file.references == 1U &&
            first_loader.references == 1U && second_loader.references == 1U &&
            first_stream.references == 1U && second_stream.references == 1U &&
            !first_stream.bad_release && !second_stream.bad_release &&
            face.references == 2U && face.table_calls == 0U && face.outline_calls == 0U;
    };
    // Every failed boundary preserves the exact earlier immutable result and
    // releases even files returned before a later GetFiles failure.
    for (std::uint32_t boundary = 0U; boundary < 8U; ++boundary) {
        auto* status = boundary == 0U ? &face.count_result : boundary == 1U ? &face.files_result :
            boundary == 2U ? &second_file.query_result : boundary == 3U ? &second_file.key_result :
            boundary == 4U ? &second_file.loader_result : boundary == 5U ? &second_loader.create_result :
            boundary == 6U ? &second_stream.size_result : &second_stream.read_result;
        *status = compat::not_implemented;
        const auto releases = second_stream.releases;
        const auto result = capture::capture_original_font(&face, font);
        *status = com::ok;
        if (!check(result == compat::not_implemented && font == retained && balanced() &&
            (boundary != 7U || second_stream.releases == releases),
            "source failure HRESULT/publication/ownership")) return false;
    }
    for (std::uint32_t invalid = 0U; invalid < 10U; ++invalid) {
        if (invalid == 0U) face.declared_count = 17U;
        if (invalid == 1U) face.declared_count = 0U;
        if (invalid == 2U) face.change_count = true;
        if (invalid == 3U) second_file.key_size = 65537U;
        if (invalid == 4U) second_stream.declared_size = 0U;
        if (invalid == 5U) second_stream.declared_size = capture::original_font_capture::maximum_total_bytes;
        if (invalid == 6U) second_file.null_key = true;
        if (invalid == 7U) second_file.loader = nullptr;
        if (invalid == 8U) second_loader.stream = nullptr;
        if (invalid == 9U) second_stream.null_data = true;
        const auto releases = second_stream.releases;
        const auto result = capture::capture_original_font(&face, font);
        face.declared_count = 2U; face.change_count = false;
        second_file.key_size = sizeof(second_file.key); second_file.null_key = false;
        second_file.loader = &second_loader; second_loader.stream = &second_stream;
        second_stream.declared_size = second_stream.bytes.size(); second_stream.null_data = false;
        if (!check(com::failed(result) && font == retained && balanced() &&
            (invalid != 9U || second_stream.releases == releases + 1U),
            "malformed source/budget atomicity/fragment cleanup")) return false;
    }
    first_file.change_key = true;
    first_stream.null_context = true;
    const auto releases = first_stream.releases;
    std::shared_ptr<const capture::original_font_capture> second_capture;
    if (!check(capture::capture_original_font(&face, second_capture) == com::ok &&
        first_stream.releases == releases + 1U && !first_stream.bad_release,
        "copied loader key/null fragment context")) return false;
    second_capture.reset();

    source_object<com::unknown> target_object;
    capture::original_glyph_target target{
        com::pointer<com::unknown>(&target_object), 71U, {1, 0.25F, -0.5F, 2, 3, -4},
        {5.25F, 21.5F}, {91U, 49U}, 144, 120, {87U, compat::alpha_mode::ignore},
        compat::text_antialias_mode::cleartype, 12U, 13U};
    std::array<std::uint16_t, 2U> indices{4U, 398U};
    std::array<float, 2U> advances{-2.0F, 8.75F};
    std::array<compat::glyph_offset, 2U> offsets{compat::glyph_offset{0.25F, -1.5F}, {2, 3}};
    compat::glyph_run run{&face, 17.5F, 2U, indices.data(), advances.data(), offsets.data(), -1, 5U};
    rendering_parameters parameters;
    struct mutation final { std::uint16_t* index; capture::original_glyph_target* target; };
    mutation source{indices.data(), &target};
    parameters.context = &source;
    parameters.callback = [](void* context) noexcept {
        auto& item = *static_cast<mutation*>(context);
        item.index[0] = 7U;
        item.target->generation = 99U;
        item.target->baseline.x = -300;
        item.target->identity.reset();
    };
    std::shared_ptr<const capture::original_glyph_request> request;
    if (!check(capture::capture_original_glyph_request(font, run, compat::measuring_mode::gdi_natural,
        &parameters, target, request) == com::ok && request && request->glyphs.indices()[0] == 4U &&
        request->glyphs.advances()[0] == -2.0F && request->glyphs.offsets()[0].ascender_offset == -1.5F &&
        request->em_size == 17.5F && request->sideways == -1 && request->bidi_level == 5U &&
        request->target.generation == 71U && request->target.baseline.x == 5.25F &&
        request->target.identity.get() == &target_object && request->target.dpi_x == 144 &&
        request->target.dpi_y == 120 && request->target.transform.m21 == -0.5F &&
        request->target.tag1 == 12U && request->target.tag2 == 13U &&
        request->rendering.supplied && request->rendering.gamma == 1.8F &&
        request->rendering.cleartype_level == 0.25F && request->rendering.pixel_geometry == 2U &&
        request->rendering.rendering_mode == 5U, "original request before reentrant getters")) return false;
    parameters.callback = nullptr;
    target = request->target;
    const auto original_request = request;
    parameters.gamma = std::numeric_limits<float>::quiet_NaN();
    if (!check(capture::capture_original_glyph_request(font, run, compat::measuring_mode::natural,
        &parameters, target, request) == com::invalid_argument && request == original_request,
        "invalid rendering values preserve request")) return false;
    indices[0] = 400U;
    if (!check(capture::capture_original_glyph_request(font, run, compat::measuring_mode::natural,
        nullptr, target, request) == com::invalid_argument && request == original_request,
        "original face glyph range")) return false;
    indices[0] = 0U;
    font_face other_face;
    parameters.gamma = 1.8F;
    for (std::uint32_t invalid = 0U; invalid < 12U; ++invalid) {
        auto candidate_target = target;
        auto candidate_run = run;
        auto measuring = compat::measuring_mode::natural;
        if (invalid == 0U) candidate_target.generation = 0U;
        if (invalid == 1U) candidate_target.identity.reset();
        if (invalid == 2U) candidate_target.pixels.width = 0U;
        if (invalid == 3U) candidate_target.dpi_y = 0;
        if (invalid == 4U) candidate_target.transform.m21 = std::numeric_limits<float>::infinity();
        if (invalid == 5U) candidate_target.baseline.x = std::numeric_limits<float>::quiet_NaN();
        if (invalid == 6U) candidate_target.units = static_cast<compat::unit_mode>(2U);
        if (invalid == 7U) candidate_target.blend = static_cast<compat::primitive_blend>(5U);
        if (invalid == 8U) candidate_run.font_face_value = &other_face;
        if (invalid == 9U) candidate_run.font_em_size = 0;
        if (invalid == 10U) candidate_run.glyph_count = capture::glyph_run_capture<compat::glyph_offset>::maximum_glyph_count + 1U;
        if (invalid == 11U) measuring = static_cast<compat::measuring_mode>(3U);
        const auto reads = parameters.reads;
        if (!check(capture::capture_original_glyph_request(font, candidate_run, measuring,
            &parameters, candidate_target, request) == com::invalid_argument && request == original_request &&
            parameters.reads == reads, "request preflight before callbacks/publication")) return false;
    }
    run.glyph_advances = nullptr; run.glyph_offsets = nullptr;
    if (!check(capture::capture_original_glyph_request(font, run, compat::measuring_mode::natural,
        nullptr, target, request) == com::ok && !request->rendering.supplied &&
        request->glyphs.advances() == nullptr && request->glyphs.offsets() == nullptr,
        "absent values stay absent")) return false;
    return balanced() && parameters.references == 1U;
}

#if defined(_WIN32)
[[nodiscard]] bool original_windows_contract()
{
    com::pointer<IDWriteFactory> factory;
    if (!check(SUCCEEDED(DWriteCreateFactory(DWRITE_FACTORY_TYPE_ISOLATED, __uuidof(IDWriteFactory),
        reinterpret_cast<IUnknown**>(factory.put()))), "original factory")) return false;
    com::pointer<IDWriteFontCollection> collection;
    com::pointer<IDWriteFontFamily> family;
    com::pointer<IDWriteFont> original_font;
    com::pointer<IDWriteFontFace> original_face;
    if (!check(SUCCEEDED(factory->GetSystemFontCollection(collection.put())) &&
        collection->GetFontFamilyCount() != 0U && SUCCEEDED(collection->GetFontFamily(0U, family.put())) &&
        SUCCEEDED(family->GetFont(0U, original_font.put())) &&
        SUCCEEDED(original_font->CreateFontFace(original_face.put())), "original face")) return false;
    // Obtain the declared canonical face interface; no undocumented tail or
    // reinterpretation of an unrelated font/interface object is permitted.
    com::pointer<compat::font_face> face;
    if (!check(SUCCEEDED(original_face.as(compat::font_face_interface_id, face)), "typed original face")) return false;
    std::shared_ptr<const capture::original_font_capture> font;
    if (!check(capture::capture_original_font(face.get(), font) == com::ok &&
        font->face_index == original_face->GetIndex() && font->face_type == original_face->GetType() &&
        font->simulations == original_face->GetSimulations() && font->glyph_count == original_face->GetGlyphCount(),
        "original face metadata")) return false;
    UINT32 count = 0U;
    if (!check(SUCCEEDED(original_face->GetFiles(&count, nullptr)) && count == font->files.size(),
        "original ordered file count")) return false;
    std::vector<IDWriteFontFile*> files(count, nullptr);
    struct release_files final {
        std::vector<IDWriteFontFile*>& files;
        ~release_files() { for (auto* file : files) if (file != nullptr) file->Release(); }
    } release{files};
    if (!check(SUCCEEDED(original_face->GetFiles(&count, files.data())), "original files")) return false;
    for (UINT32 index = 0U; index < count; ++index) {
        com::pointer<IDWriteFontFileLoader> loader;
        com::pointer<IDWriteFontFileStream> stream;
        const void* key = nullptr; UINT32 key_size = 0U;
        UINT64 size = 0U;
        if (!check(SUCCEEDED(files[index]->GetReferenceKey(&key, &key_size)) &&
            SUCCEEDED(files[index]->GetLoader(loader.put())) &&
            SUCCEEDED(loader->CreateStreamFromKey(key, key_size, stream.put())) &&
            SUCCEEDED(stream->GetFileSize(&size)) && size == font->files[index].size(),
            "independent original stream")) return false;
        const void* bytes = nullptr; void* context = nullptr;
        if (!check(SUCCEEDED(stream->ReadFileFragment(&bytes, 0U, size, &context)), "original fragment")) return false;
        const bool identical = std::memcmp(bytes, font->files[index].data(), static_cast<std::size_t>(size)) == 0;
        stream->ReleaseFileFragment(context);
        if (!check(identical, "every original font byte")) return false;
    }
    return true;
}
#endif
} // namespace

bool progpu_native_direct2d_font_capture_tests()
{
    if (!source_contracts()) return false;
#if defined(_WIN32)
    if (!original_windows_contract()) return false;
#endif
    return true;
}
