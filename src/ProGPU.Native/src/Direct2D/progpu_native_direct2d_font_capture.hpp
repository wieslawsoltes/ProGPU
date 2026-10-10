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

// Published IDWriteFontFace5 identity and its complete inherited ABI. The
// existing portable font_face prefix stays unchanged. This interface is usable
// only after this exact IID succeeds; never cast the prefix to an undeclared
// tail. Pointer-only records/interfaces below stay opaque because capture never
// invokes those methods. Method suffixes distinguish original overloaded slots.
inline constexpr com::guid original_font_face5_id{
    0x98EFF3A5U, 0xB667U, 0x479AU, {0xB1U, 0x45U, 0xE2U, 0xFAU, 0x5BU, 0x9FU, 0xDCU, 0x29U}};
struct original_font_metrics;
struct original_font_metrics1;
struct original_glyph_metrics;
struct original_caret_metrics;
struct original_unicode_range;
union original_panose;
struct original_glyph_image_data;
struct original_face_reference;
struct original_localized_strings;
struct original_font_resource;

struct original_font_axis_value final {
    // DWRITE_FONT_AXIS_TAG byte order, NOT the native big-endian OpenType tag
    // integer. Preserve source order, tag case and exact user-coordinate bits.
    std::uint32_t tag = 0U;
    float value = 0.0F;
};
static_assert(sizeof(original_font_axis_value) == 8U);
static_assert(offsetof(original_font_axis_value, value) == 4U);

struct original_font_face5 : compat::font_face {
    // Remaining IDWriteFontFace slots, after GetGlyphRunOutline.
    virtual com::result PROGPU_NATIVE_COM_CALL GetRecommendedRenderingMode(float, float,
        compat::measuring_mode, compat::rendering_parameters*, compat::rendering_mode*) noexcept = 0;
    virtual com::result PROGPU_NATIVE_COM_CALL GetGdiCompatibleMetrics(float, float,
        const compat::matrix_3x2_f*, original_font_metrics*) noexcept = 0;
    virtual com::result PROGPU_NATIVE_COM_CALL GetGdiCompatibleGlyphMetrics(float, float,
        const compat::matrix_3x2_f*, std::int32_t, const std::uint16_t*, std::uint32_t,
        original_glyph_metrics*, std::int32_t) noexcept = 0;
    // IDWriteFontFace1.
    virtual void PROGPU_NATIVE_COM_CALL GetMetrics1(original_font_metrics1*) noexcept = 0;
    virtual com::result PROGPU_NATIVE_COM_CALL GetGdiCompatibleMetrics1(float, float,
        const compat::matrix_3x2_f*, original_font_metrics1*) noexcept = 0;
    virtual void PROGPU_NATIVE_COM_CALL GetCaretMetrics(original_caret_metrics*) noexcept = 0;
    virtual com::result PROGPU_NATIVE_COM_CALL GetUnicodeRanges(std::uint32_t,
        original_unicode_range*, std::uint32_t*) noexcept = 0;
    virtual std::int32_t PROGPU_NATIVE_COM_CALL IsMonospacedFont() noexcept = 0;
    virtual com::result PROGPU_NATIVE_COM_CALL GetDesignGlyphAdvances(std::uint32_t,
        const std::uint16_t*, std::int32_t*, std::int32_t) noexcept = 0;
    virtual com::result PROGPU_NATIVE_COM_CALL GetGdiCompatibleGlyphAdvances(float, float,
        const compat::matrix_3x2_f*, std::int32_t, std::int32_t, std::uint32_t,
        const std::uint16_t*, std::int32_t*) noexcept = 0;
    virtual com::result PROGPU_NATIVE_COM_CALL GetKerningPairAdjustments(std::uint32_t,
        const std::uint16_t*, std::int32_t*) noexcept = 0;
    virtual std::int32_t PROGPU_NATIVE_COM_CALL HasKerningPairs() noexcept = 0;
    virtual com::result PROGPU_NATIVE_COM_CALL GetRecommendedRenderingMode1(float, float, float,
        const compat::matrix_3x2_f*, std::int32_t, std::uint32_t, compat::measuring_mode,
        compat::rendering_mode*) noexcept = 0;
    virtual com::result PROGPU_NATIVE_COM_CALL GetVerticalGlyphVariants(std::uint32_t,
        const std::uint16_t*, std::uint16_t*) noexcept = 0;
    virtual std::int32_t PROGPU_NATIVE_COM_CALL HasVerticalGlyphVariants() noexcept = 0;
    // IDWriteFontFace2.
    virtual std::int32_t PROGPU_NATIVE_COM_CALL IsColorFont() noexcept = 0;
    virtual std::uint32_t PROGPU_NATIVE_COM_CALL GetColorPaletteCount() noexcept = 0;
    virtual std::uint32_t PROGPU_NATIVE_COM_CALL GetPaletteEntryCount() noexcept = 0;
    virtual com::result PROGPU_NATIVE_COM_CALL GetPaletteEntries(std::uint32_t, std::uint32_t,
        std::uint32_t, compat::color_f*) noexcept = 0;
    virtual com::result PROGPU_NATIVE_COM_CALL GetRecommendedRenderingMode2(float, float, float,
        const compat::matrix_3x2_f*, std::int32_t, std::uint32_t, compat::measuring_mode,
        compat::rendering_parameters*, compat::rendering_mode*, std::uint32_t*) noexcept = 0;
    // IDWriteFontFace3.
    virtual com::result PROGPU_NATIVE_COM_CALL GetFontFaceReference(original_face_reference**) noexcept = 0;
    virtual void PROGPU_NATIVE_COM_CALL GetPanose(original_panose*) noexcept = 0;
    virtual std::uint32_t PROGPU_NATIVE_COM_CALL GetWeight() noexcept = 0;
    virtual std::uint32_t PROGPU_NATIVE_COM_CALL GetStretch() noexcept = 0;
    virtual std::uint32_t PROGPU_NATIVE_COM_CALL GetStyle() noexcept = 0;
    virtual com::result PROGPU_NATIVE_COM_CALL GetFamilyNames(original_localized_strings**) noexcept = 0;
    virtual com::result PROGPU_NATIVE_COM_CALL GetFaceNames(original_localized_strings**) noexcept = 0;
    virtual com::result PROGPU_NATIVE_COM_CALL GetInformationalStrings(std::uint32_t,
        original_localized_strings**, std::int32_t*) noexcept = 0;
    virtual std::int32_t PROGPU_NATIVE_COM_CALL HasCharacter(std::uint32_t) noexcept = 0;
    virtual com::result PROGPU_NATIVE_COM_CALL GetRecommendedRenderingMode3(float, float, float,
        const compat::matrix_3x2_f*, std::int32_t, std::uint32_t, compat::measuring_mode,
        compat::rendering_parameters*, std::uint32_t*, std::uint32_t*) noexcept = 0;
    virtual std::int32_t PROGPU_NATIVE_COM_CALL IsCharacterLocal(std::uint32_t) noexcept = 0;
    virtual std::int32_t PROGPU_NATIVE_COM_CALL IsGlyphLocal(std::uint16_t) noexcept = 0;
    virtual com::result PROGPU_NATIVE_COM_CALL AreCharactersLocal(const char16_t*, std::uint32_t,
        std::int32_t, std::int32_t*) noexcept = 0;
    virtual com::result PROGPU_NATIVE_COM_CALL AreGlyphsLocal(const std::uint16_t*, std::uint32_t,
        std::int32_t, std::int32_t*) noexcept = 0;
    // IDWriteFontFace4.
    virtual std::uint32_t PROGPU_NATIVE_COM_CALL GetGlyphImageFormats() noexcept = 0;
    virtual com::result PROGPU_NATIVE_COM_CALL GetGlyphImageFormatsForRange(std::uint16_t,
        std::uint32_t, std::uint32_t, std::uint32_t*) noexcept = 0;
    virtual com::result PROGPU_NATIVE_COM_CALL GetGlyphImageData(std::uint16_t, std::uint32_t,
        std::uint32_t, original_glyph_image_data*, void**) noexcept = 0;
    virtual void PROGPU_NATIVE_COM_CALL ReleaseGlyphImageData(void*) noexcept = 0;
    // IDWriteFontFace5.
    virtual std::uint32_t PROGPU_NATIVE_COM_CALL GetFontAxisValueCount() noexcept = 0;
    virtual com::result PROGPU_NATIVE_COM_CALL GetFontAxisValues(original_font_axis_value*, std::uint32_t) noexcept = 0;
    virtual std::int32_t PROGPU_NATIVE_COM_CALL HasVariations() noexcept = 0;
    virtual com::result PROGPU_NATIVE_COM_CALL GetFontResource(original_font_resource**) noexcept = 0;
    virtual std::int32_t PROGPU_NATIVE_COM_CALL Equals(compat::font_face*) noexcept = 0;
};

struct original_font_capture final {
    static constexpr std::uint32_t maximum_files = 16U;
    static constexpr std::uint32_t maximum_key_bytes = 65536U;
    static constexpr std::uint64_t maximum_total_bytes = 64U * 1024U * 1024U;
    // Bounded capture storage: native fvar has a uint16 axis domain and Face5
    // can additionally expose five standard static design attributes. This is
    // not a fabricated fvar inventory; never equate these two counts.
    static constexpr std::uint32_t maximum_axes = 65535U + 5U;

    // This retained original face is also the instance identity. File/index
    // metadata alone does NOT prove variable-axis or raster-mode equivalence.
    com::pointer<compat::font_face> face;
    std::uint32_t face_type = 0U;
    std::uint32_t face_index = 0U;
    std::uint32_t simulations = 0U;
    std::int32_t symbol_font = 0;
    std::uint16_t glyph_count = 0U;
    bool axis_values_available = false;
    bool has_variations = false;
    std::vector<original_font_axis_value> axis_values;
    std::vector<std::vector<std::byte>> files;
};

// Explicit generation preparation, not a draw-time read/cache keyed by a COM
// address. Owns complete original files in returned order; never reconstructs a
// font from table reads. O(B + F + A) expected time/storage for original bytes,
// files and axis values; B <= 64 MiB, F <= 16, A <= 65535 + 5.
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
