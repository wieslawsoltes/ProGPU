#pragma once

#include "../src/Direct2D/progpu_native_direct2d_font_capture.hpp"
#include <array>
#include <cstring>

namespace progpu::native::direct2d::tests {
namespace capture = progpu::native::direct2d;
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
    std::vector<std::byte> bytes{std::byte{1}, std::byte{2}, std::byte{3}, std::byte{4},
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

template<class Interface>
class font_face_base : public source_object<Interface> {
public:
    font_face_base() { this->interface_id = &compat::font_face_interface_id; }
    std::uint32_t PROGPU_NATIVE_COM_CALL GetType() noexcept override { return type; }
    std::uint32_t PROGPU_NATIVE_COM_CALL GetIndex() noexcept override { return index; }
    std::uint32_t PROGPU_NATIVE_COM_CALL GetSimulations() noexcept override { return simulations; }
    std::int32_t PROGPU_NATIVE_COM_CALL IsSymbolFont() noexcept override { return 1; }
    std::uint16_t PROGPU_NATIVE_COM_CALL GetGlyphCount() noexcept override { return glyph_count; }
    com::result PROGPU_NATIVE_COM_CALL GetFiles(std::uint32_t* count, com::unknown** output) noexcept override
    {
        if (output == nullptr) { *count = declared_count; return count_result; }
        for (std::uint32_t i = 0; i < *count && i < files.size(); ++i) {
            output[i] = files[i];
            if (output[i] != nullptr) output[i]->AddRef();
        }
        if (change_count) *count = 1U;
        if (files_callback != nullptr) files_callback(callback_context);
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
    std::uint32_t type = 3U, index = 2U, simulations = 3U;
    std::uint16_t glyph_count = 400U;
    com::result count_result = com::ok, files_result = com::ok;
    bool change_count = false;
    void (*files_callback)(void*) noexcept = nullptr;
    void* callback_context = nullptr;
};
using font_face = font_face_base<compat::font_face>;

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
    { return mode; }
    compat::rendering_mode mode = compat::rendering_mode::natural_symmetric;
    float gamma = 1.8F;
    std::uint32_t reads = 0U;
    void (*callback)(void*) noexcept = nullptr;
    void* context = nullptr;
};

} // namespace progpu::native::direct2d::tests
