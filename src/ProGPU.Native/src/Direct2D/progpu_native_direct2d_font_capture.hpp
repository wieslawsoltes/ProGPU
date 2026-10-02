#pragma once

#include "progpu_native_direct2d_compat.hpp"
#include "progpu_native_direct2d_text_capture.hpp"

#include <cstddef>
#include <memory>
#include <vector>

namespace progpu::native::direct2d {

// Private ABI prefixes from the published DirectWrite contracts. Never extend
// compat::font_face beyond its supported prefix to obtain these objects.
inline constexpr com::guid original_font_file_id{
    0x739D886AU, 0xCEF5U, 0x47DCU, {0x87U, 0x69U, 0x1AU, 0x8BU, 0x41U, 0xBEU, 0xBBU, 0xB0U}};

struct original_font_stream : com::unknown {
    virtual com::result PROGPU_NATIVE_COM_CALL ReadFileFragment(
        const void** data, std::uint64_t offset, std::uint64_t size,
        void** context) noexcept = 0;
    virtual void PROGPU_NATIVE_COM_CALL ReleaseFileFragment(void* context) noexcept = 0;
    virtual com::result PROGPU_NATIVE_COM_CALL GetFileSize(std::uint64_t* size) noexcept = 0;
};

struct original_font_loader : com::unknown {
    virtual com::result PROGPU_NATIVE_COM_CALL CreateStreamFromKey(
        const void* key, std::uint32_t key_size, original_font_stream** stream) noexcept = 0;
};

struct original_font_file : com::unknown {
    virtual com::result PROGPU_NATIVE_COM_CALL GetReferenceKey(
        const void** key, std::uint32_t* key_size) noexcept = 0;
    virtual com::result PROGPU_NATIVE_COM_CALL GetLoader(original_font_loader** loader) noexcept = 0;
};

struct original_font_capture final {
    static constexpr std::uint32_t maximum_files = 16U;
    static constexpr std::uint32_t maximum_key_bytes = 65536U;
    static constexpr std::uint64_t maximum_total_bytes = 64U * 1024U * 1024U;

    // This retained original face is also the instance identity. File/index
    // metadata alone does NOT prove variable-axis or raster-mode equivalence.
    com::pointer<compat::font_face> face;
    std::uint32_t face_type = 0U;
    std::uint32_t face_index = 0U;
    std::uint32_t simulations = 0U;
    std::int32_t symbol_font = 0;
    std::uint16_t glyph_count = 0U;
    std::vector<std::vector<std::byte>> files;
};

// Explicit generation preparation, not a draw-time read/cache keyed by a COM
// address. Owns complete original files in returned order; never reconstructs a
// font from table reads. O(B + F) time/storage, B <= 64 MiB, F <= 16.
// On any failure output is unchanged, including original source HRESULTs.
[[nodiscard]] com::result capture_original_font(
    compat::font_face* face,
    std::shared_ptr<const original_font_capture>& output) noexcept;

struct original_glyph_target final {
    com::pointer<com::unknown> identity;
    std::uint64_t generation = 0U;
    compat::matrix_3x2_f transform{};
    compat::point_2f baseline{};
    compat::size_u pixels{};
    float dpi_x = 0.0F;
    float dpi_y = 0.0F;
    compat::pixel_format format{};
    compat::text_antialias_mode antialias{};
    std::uint64_t tag1 = 0U;
    std::uint64_t tag2 = 0U;
    compat::unit_mode units{};
    compat::primitive_blend blend{};
};

struct original_glyph_request final {
    std::shared_ptr<const original_font_capture> font;
    glyph_run_capture<compat::glyph_offset> glyphs;
    float em_size = 0.0F;
    std::int32_t sideways = 0;
    std::uint32_t bidi_level = 0U;
    compat::measuring_mode measuring{};
    text_rendering_values rendering;
    original_glyph_target target;
};

// Source values only: target must be captured by its actual owner before this
// call. The caller must retain its capture-generation guard across this call and
// publication (parameter getters/AddRef/Release are external callbacks). This
// immutable request is not proof that a live recorder generation still matches.
// No font queries, nominal advances, source rounding or render policy selection.
// The target's captured clip/layer command scopes remain owned by the recorder;
// this request neither serializes them nor proves them unnecessary for replay.
[[nodiscard]] com::result capture_original_glyph_request(
    std::shared_ptr<const original_font_capture> font,
    const compat::glyph_run& run, compat::measuring_mode measuring,
    compat::rendering_parameters* rendering, const original_glyph_target& target,
    std::shared_ptr<const original_glyph_request>& output) noexcept;

} // namespace progpu::native::direct2d
