#pragma once

#include "progpu_native_direct2d_font_source_fixture.hpp"
#include <algorithm>
#include <limits>

namespace progpu::native::direct2d::tests {

// Complete Face5 test object: every unused inherited slot is observable and
// rejects. No padded vtable, guessed slot indexing or foreign implementation.
class font_face5 final : public font_face_base<capture::original_font_face5> {
public:
    com::result PROGPU_NATIVE_COM_CALL QueryInterface(com::guid_ref id, void** output) noexcept override
    {
        if (output == nullptr) return com::pointer_error;
        if (com::guid_equal(id, capture::original_font_face5_id)) {
            ++axis_queries;
            *output = nullptr;
            if (axis_query_output) { *output = static_cast<capture::original_font_face5*>(this); AddRef(); }
            return axis_query_result;
        }
        if (com::guid_equal(id, com::unknown_interface_id())) {
            ++identity_queries;
            if (foreign_identity != nullptr && identity_queries == foreign_identity_at_query) {
                *output = foreign_identity; foreign_identity->AddRef(); return com::ok;
            }
        }
        return font_face_base<capture::original_font_face5>::QueryInterface(id, output);
    }
    std::uint32_t PROGPU_NATIVE_COM_CALL GetFontAxisValueCount() noexcept override
    {
        ++count_reads;
        return declared_axes == std::numeric_limits<std::uint32_t>::max()
            ? static_cast<std::uint32_t>(axes.size()) : declared_axes;
    }
    com::result PROGPU_NATIVE_COM_CALL GetFontAxisValues(capture::original_font_axis_value* output,
        std::uint32_t count) noexcept override
    {
        ++value_reads;
        if (com::failed(values_result)) return values_result;
        if (count != axes.size() || output == nullptr) return com::invalid_argument;
        std::copy(axes.begin(), axes.end(), output);
        if (values_callback != nullptr) values_callback(values_context);
        return com::ok;
    }
    std::int32_t PROGPU_NATIVE_COM_CALL HasVariations() noexcept override { return variable ? 1 : 0; }
    com::result PROGPU_NATIVE_COM_CALL GetFontResource(capture::original_font_resource**) noexcept override { return unused(); }
    std::int32_t PROGPU_NATIVE_COM_CALL Equals(compat::font_face*) noexcept override { ++unused_calls; return 0; }
    com::result PROGPU_NATIVE_COM_CALL GetRecommendedRenderingMode(float, float, compat::measuring_mode,
        compat::rendering_parameters*, compat::rendering_mode*) noexcept override { return unused(); }
    com::result PROGPU_NATIVE_COM_CALL GetGdiCompatibleMetrics(float, float,
        const compat::matrix_3x2_f*, capture::original_font_metrics*) noexcept override { return unused(); }
    com::result PROGPU_NATIVE_COM_CALL GetGdiCompatibleGlyphMetrics(float, float, const compat::matrix_3x2_f*,
        std::int32_t, const std::uint16_t*, std::uint32_t, capture::original_glyph_metrics*, std::int32_t) noexcept override { return unused(); }
    void PROGPU_NATIVE_COM_CALL GetMetrics1(capture::original_font_metrics1*) noexcept override { ++unused_calls; }
    com::result PROGPU_NATIVE_COM_CALL GetGdiCompatibleMetrics1(float, float, const compat::matrix_3x2_f*,
        capture::original_font_metrics1*) noexcept override { return unused(); }
    void PROGPU_NATIVE_COM_CALL GetCaretMetrics(capture::original_caret_metrics*) noexcept override { ++unused_calls; }
    com::result PROGPU_NATIVE_COM_CALL GetUnicodeRanges(std::uint32_t, capture::original_unicode_range*,
        std::uint32_t*) noexcept override { return unused(); }
    std::int32_t PROGPU_NATIVE_COM_CALL IsMonospacedFont() noexcept override { ++unused_calls; return 0; }
    com::result PROGPU_NATIVE_COM_CALL GetDesignGlyphAdvances(std::uint32_t, const std::uint16_t*,
        std::int32_t*, std::int32_t) noexcept override { return unused(); }
    com::result PROGPU_NATIVE_COM_CALL GetGdiCompatibleGlyphAdvances(float, float, const compat::matrix_3x2_f*,
        std::int32_t, std::int32_t, std::uint32_t, const std::uint16_t*, std::int32_t*) noexcept override { return unused(); }
    com::result PROGPU_NATIVE_COM_CALL GetKerningPairAdjustments(std::uint32_t, const std::uint16_t*,
        std::int32_t*) noexcept override { return unused(); }
    std::int32_t PROGPU_NATIVE_COM_CALL HasKerningPairs() noexcept override { ++unused_calls; return 0; }
    com::result PROGPU_NATIVE_COM_CALL GetRecommendedRenderingMode1(float, float, float, const compat::matrix_3x2_f*,
        std::int32_t, std::uint32_t, compat::measuring_mode, compat::rendering_mode*) noexcept override { return unused(); }
    com::result PROGPU_NATIVE_COM_CALL GetVerticalGlyphVariants(std::uint32_t, const std::uint16_t*,
        std::uint16_t*) noexcept override { return unused(); }
    std::int32_t PROGPU_NATIVE_COM_CALL HasVerticalGlyphVariants() noexcept override { ++unused_calls; return 0; }
    std::int32_t PROGPU_NATIVE_COM_CALL IsColorFont() noexcept override { ++unused_calls; return 0; }
    std::uint32_t PROGPU_NATIVE_COM_CALL GetColorPaletteCount() noexcept override { ++unused_calls; return 0; }
    std::uint32_t PROGPU_NATIVE_COM_CALL GetPaletteEntryCount() noexcept override { ++unused_calls; return 0; }
    com::result PROGPU_NATIVE_COM_CALL GetPaletteEntries(std::uint32_t, std::uint32_t, std::uint32_t,
        compat::color_f*) noexcept override { return unused(); }
    com::result PROGPU_NATIVE_COM_CALL GetRecommendedRenderingMode2(float, float, float, const compat::matrix_3x2_f*,
        std::int32_t, std::uint32_t, compat::measuring_mode, compat::rendering_parameters*,
        compat::rendering_mode*, std::uint32_t*) noexcept override { return unused(); }
    com::result PROGPU_NATIVE_COM_CALL GetFontFaceReference(capture::original_face_reference**) noexcept override { return unused(); }
    void PROGPU_NATIVE_COM_CALL GetPanose(capture::original_panose*) noexcept override { ++unused_calls; }
    std::uint32_t PROGPU_NATIVE_COM_CALL GetWeight() noexcept override { ++unused_calls; return 0; }
    std::uint32_t PROGPU_NATIVE_COM_CALL GetStretch() noexcept override { ++unused_calls; return 0; }
    std::uint32_t PROGPU_NATIVE_COM_CALL GetStyle() noexcept override { ++unused_calls; return 0; }
    com::result PROGPU_NATIVE_COM_CALL GetFamilyNames(capture::original_localized_strings**) noexcept override { return unused(); }
    com::result PROGPU_NATIVE_COM_CALL GetFaceNames(capture::original_localized_strings**) noexcept override { return unused(); }
    com::result PROGPU_NATIVE_COM_CALL GetInformationalStrings(std::uint32_t, capture::original_localized_strings**,
        std::int32_t*) noexcept override { return unused(); }
    std::int32_t PROGPU_NATIVE_COM_CALL HasCharacter(std::uint32_t) noexcept override { ++unused_calls; return 0; }
    com::result PROGPU_NATIVE_COM_CALL GetRecommendedRenderingMode3(float, float, float, const compat::matrix_3x2_f*,
        std::int32_t, std::uint32_t, compat::measuring_mode, compat::rendering_parameters*,
        std::uint32_t*, std::uint32_t*) noexcept override { return unused(); }
    std::int32_t PROGPU_NATIVE_COM_CALL IsCharacterLocal(std::uint32_t) noexcept override { ++unused_calls; return 0; }
    std::int32_t PROGPU_NATIVE_COM_CALL IsGlyphLocal(std::uint16_t) noexcept override { ++unused_calls; return 0; }
    com::result PROGPU_NATIVE_COM_CALL AreCharactersLocal(const char16_t*, std::uint32_t,
        std::int32_t, std::int32_t*) noexcept override { return unused(); }
    com::result PROGPU_NATIVE_COM_CALL AreGlyphsLocal(const std::uint16_t*, std::uint32_t,
        std::int32_t, std::int32_t*) noexcept override { return unused(); }
    std::uint32_t PROGPU_NATIVE_COM_CALL GetGlyphImageFormats() noexcept override { ++unused_calls; return 0; }
    com::result PROGPU_NATIVE_COM_CALL GetGlyphImageFormatsForRange(std::uint16_t, std::uint32_t,
        std::uint32_t, std::uint32_t*) noexcept override { return unused(); }
    com::result PROGPU_NATIVE_COM_CALL GetGlyphImageData(std::uint16_t, std::uint32_t, std::uint32_t,
        capture::original_glyph_image_data*, void**) noexcept override { return unused(); }
    void PROGPU_NATIVE_COM_CALL ReleaseGlyphImageData(void*) noexcept override { ++unused_calls; }

    std::vector<capture::original_font_axis_value> axes{{0x74686777U, 625.25F}, {0x544D5343U, -0.0F}};
    std::uint32_t declared_axes = std::numeric_limits<std::uint32_t>::max();
    std::uint32_t axis_queries = 0U, count_reads = 0U, value_reads = 0U, unused_calls = 0U;
    std::uint32_t identity_queries = 0U, foreign_identity_at_query = 2U;
    com::result axis_query_result = com::ok, values_result = com::ok;
    bool axis_query_output = true, variable = true;
    com::unknown* foreign_identity = nullptr;
    void (*values_callback)(void*) noexcept = nullptr;
    void* values_context = nullptr;
private:
    com::result unused() noexcept { ++unused_calls; return compat::not_implemented; }
};
} // namespace progpu::native::direct2d::tests
