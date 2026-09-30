#include "progpu_native_hinted_font.hpp"

#include <ft2build.h>
#include FT_FREETYPE_H
#include FT_FONT_FORMATS_H
#include FT_MODULE_H
#include FT_MULTIPLE_MASTERS_H
#include FT_OUTLINE_H

#include <array>
#include <cstring>
#include <limits>
#include <mutex>
#include <new>
#include <stdexcept>
#include <type_traits>
#include <utility>
#if defined(_WIN32)
#ifndef NOMINMAX
#define NOMINMAX
#endif
#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#include <windows.h>
#elif defined(__APPLE__) || defined(__linux__)
#include <dlfcn.h>
#endif
#if defined(__aarch64__) || defined(_M_ARM64)
#include <arm_neon.h>
#elif defined(__SSE2__) || defined(_M_X64)
#include <emmintrin.h>
#endif

namespace progpu::native::text {

struct hinted_font::state final {
    std::shared_ptr<const hinted_font_identity> identity{};
    FT_Library library = nullptr;
    FT_Face face = nullptr;
    std::uint32_t glyph_count = 0U;
    std::mutex mutex{};

    ~state()
    {
        // Both native owners end before the immutable memory face's bytes.
        if (face != nullptr) FT_Done_Face(face);
        if (library != nullptr) FT_Done_FreeType(library);
    }
};

namespace {

const void* module_of(const void* address) noexcept
{
#if defined(_WIN32)
    HMODULE module = nullptr;
    return GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
        reinterpret_cast<LPCWSTR>(address), &module) != 0 ? module : nullptr;
#elif defined(__APPLE__) || defined(__linux__)
    Dl_info information{};
    return dladdr(address, &information) != 0 ? information.dli_fbase : nullptr;
#else
    (void)address;
    return nullptr;
#endif
}

bool dependency_owned() noexcept
{
    const auto owner = module_of(reinterpret_cast<const void*>(&dependency_owned));
    const std::array<const void*, 16> functions{
        reinterpret_cast<const void*>(&FT_Init_FreeType), reinterpret_cast<const void*>(&FT_Done_FreeType),
        reinterpret_cast<const void*>(&FT_Library_Version), reinterpret_cast<const void*>(&FT_Property_Set),
        reinterpret_cast<const void*>(&FT_Property_Get), reinterpret_cast<const void*>(&FT_New_Memory_Face),
        reinterpret_cast<const void*>(&FT_Done_Face), reinterpret_cast<const void*>(&FT_Get_Font_Format),
        reinterpret_cast<const void*>(&FT_Get_MM_Var), reinterpret_cast<const void*>(&FT_Done_MM_Var),
        reinterpret_cast<const void*>(&FT_Set_Var_Design_Coordinates), reinterpret_cast<const void*>(&FT_Request_Size),
        reinterpret_cast<const void*>(&FT_Set_Transform), reinterpret_cast<const void*>(&FT_Load_Glyph),
        reinterpret_cast<const void*>(&FT_Outline_Check), reinterpret_cast<const void*>(&FT_Get_Char_Index)};
    if (owner == nullptr) return false;
    // Each selected public function must belong to this executing image. A
    // matching version or archive path alone cannot establish loaded ownership.
    for (const auto function : functions) if (module_of(function) != owner) return false;
    return true;
}

bool fail(hinted_font_error value, hinted_font_error& error) noexcept
{
    error = value;
    return false;
}

bool valid_configuration(const hinted_font_configuration& value) noexcept
{
    return value.x_pixels_per_em_26_6 > 0U && value.y_pixels_per_em_26_6 > 0U &&
        value.x_pixels_per_em_26_6 <= static_cast<std::uint32_t>(std::numeric_limits<std::int32_t>::max()) &&
        value.y_pixels_per_em_26_6 <= static_cast<std::uint32_t>(std::numeric_limits<std::int32_t>::max()) &&
        value.x_phase_26_6 < 64U && value.y_phase_26_6 < 64U &&
        (value.policy == font_hint_policy::truetype_35 || value.policy == font_hint_policy::truetype_40);
}

// Original ProGPU provenance: SIMD glyph-ID admission in
// Interop/progpu_native_text_shaping_interop.cpp:get_device_advances.
// Retain unsigned identity on every lane, with an alignment-safe bounded tail.
bool valid_glyphs(std::span<const std::uint32_t> values, std::uint32_t limit) noexcept
{
    std::size_t index = 0U;
#if defined(__aarch64__) || defined(_M_ARM64)
    const auto maximum = vdupq_n_u32(limit);
    for (; values.size() - index >= 4U; index += 4U) {
        if (vminvq_u32(vcltq_u32(vld1q_u32(values.data() + index), maximum)) == 0U) return false;
    }
#elif defined(__SSE2__) || defined(_M_X64)
    const auto sign = _mm_set1_epi32(std::numeric_limits<std::int32_t>::min());
    const auto maximum = _mm_xor_si128(_mm_set1_epi32(static_cast<int>(limit - 1U)), sign);
    for (; values.size() - index >= 4U; index += 4U) {
        const auto lanes = _mm_loadu_si128(reinterpret_cast<const __m128i*>(values.data() + index));
        if (_mm_movemask_epi8(_mm_cmpgt_epi32(_mm_xor_si128(lanes, sign), maximum)) != 0) return false;
    }
#endif
    for (; index < values.size(); ++index) if (values[index] >= limit) return false;
    return true;
}

struct variation_owner final {
    FT_Library library;
    FT_MM_Var* value = nullptr;
    ~variation_owner() { if (value != nullptr) FT_Done_MM_Var(library, value); }
};

bool select_variation(FT_Library library, FT_Face face,
    std::span<const std::int32_t> coordinates)
{
    if (!FT_HAS_MULTIPLE_MASTERS(face)) return coordinates.empty();
    variation_owner axes{library};
    if (FT_Get_MM_Var(face, &axes.value) != 0 || axes.value == nullptr ||
        axes.value->num_axis == 0U || axes.value->axis == nullptr ||
        coordinates.size() != axes.value->num_axis) return false;
    std::vector<FT_Fixed> selected(coordinates.begin(), coordinates.end());
    // Axis validation is cold, dependent metadata work, not a glyph/frame loop.
    for (std::size_t index = 0U; index < selected.size(); ++index) {
        if (selected[index] < axes.value->axis[index].minimum ||
            selected[index] > axes.value->axis[index].maximum) return false;
    }
    return FT_Set_Var_Design_Coordinates(face, axes.value->num_axis, selected.data()) == 0;
}

} // namespace

hinted_font::hinted_font(std::unique_ptr<state> value) noexcept : state_(std::move(value)) {}
hinted_font::~hinted_font() = default;

bool hinted_font::try_create(std::span<const std::byte> font_bytes,
    std::uint32_t face_index, const hinted_font_configuration& configuration,
    std::unique_ptr<hinted_font>& result, hinted_font_error& error) noexcept
{
    if (!valid_configuration(configuration) || font_bytes.empty() ||
        font_bytes.size() > static_cast<std::size_t>(std::numeric_limits<FT_Long>::max()) ||
        face_index > 0xFFFFU) return fail(hinted_font_error::invalid_argument, error);
    try {
        return try_create(std::make_shared<const owned_font_source>(font_bytes, face_index),
            configuration, result, error);
    } catch (const std::bad_alloc&) {
        return fail(hinted_font_error::resource_exhausted, error);
    } catch (const std::length_error&) {
        return fail(hinted_font_error::resource_exhausted, error);
    } catch (...) {
        return fail(hinted_font_error::hinting_failed, error);
    }
}

bool hinted_font::try_create(std::shared_ptr<const owned_font_source> source,
    const hinted_font_configuration& configuration,
    std::unique_ptr<hinted_font>& result, hinted_font_error& error) noexcept
{
    if (source == nullptr || !valid_configuration(configuration) || source->bytes.empty() ||
        source->bytes.size() > static_cast<std::size_t>(std::numeric_limits<FT_Long>::max()) ||
        source->face_index > 0xFFFFU) return fail(hinted_font_error::invalid_argument, error);
    if (!dependency_owned()) return fail(hinted_font_error::dependency_mismatch, error);
    try {
        auto identity = std::make_shared<hinted_font_identity>();
        identity->source = std::move(source);
        identity->variation_coordinates_16_16.assign(configuration.variation_coordinates_16_16.begin(),
            configuration.variation_coordinates_16_16.end());
        identity->x_pixels_per_em_26_6 = configuration.x_pixels_per_em_26_6;
        identity->y_pixels_per_em_26_6 = configuration.y_pixels_per_em_26_6;
        identity->policy = configuration.policy;
        identity->x_phase_26_6 = configuration.x_phase_26_6;
        identity->y_phase_26_6 = configuration.y_phase_26_6;
        auto value = std::make_unique<state>();
        value->identity = identity;
        if (FT_Init_FreeType(&value->library) != 0) return fail(hinted_font_error::resource_exhausted, error);
        FT_Int major = 0, minor = 0, patch = 0;
        FT_Library_Version(value->library, &major, &minor, &patch);
        if (major != 2 || minor != 14 || patch != 3 ||
            FREETYPE_MAJOR != major || FREETYPE_MINOR != minor || FREETYPE_PATCH != patch)
            return fail(hinted_font_error::dependency_mismatch, error);
        unsigned int policy = static_cast<unsigned int>(configuration.policy);
        unsigned int actual = 0U;
        if (FT_Property_Set(value->library, "truetype", "interpreter-version", &policy) != 0 ||
            FT_Property_Get(value->library, "truetype", "interpreter-version", &actual) != 0 || actual != policy)
            return fail(hinted_font_error::dependency_mismatch, error);
        if (FT_New_Memory_Face(value->library,
                reinterpret_cast<const FT_Byte*>(identity->source->bytes.data()),
                static_cast<FT_Long>(identity->source->bytes.size()), static_cast<FT_Long>(identity->source->face_index),
                &value->face) != 0) return fail(hinted_font_error::invalid_font, error);
        const char* format = FT_Get_Font_Format(value->face);
        if (!FT_IS_SCALABLE(value->face) || FT_HAS_COLOR(value->face) ||
            format == nullptr || std::strcmp(format, "TrueType") != 0 || value->face->num_glyphs <= 0 ||
            !std::in_range<std::uint32_t>(value->face->num_glyphs))
            return fail(hinted_font_error::unsupported_font, error);
        if (!select_variation(value->library, value->face, identity->variation_coordinates_16_16))
            return fail(hinted_font_error::invalid_argument, error);
        FT_Size_RequestRec size{};
        size.type = FT_SIZE_REQUEST_TYPE_NOMINAL;
        size.width = static_cast<FT_Long>(configuration.x_pixels_per_em_26_6);
        size.height = static_cast<FT_Long>(configuration.y_pixels_per_em_26_6);
        if (FT_Request_Size(value->face, &size) != 0) return fail(hinted_font_error::invalid_argument, error);
        FT_Vector phase{static_cast<FT_Pos>(configuration.x_phase_26_6),
            static_cast<FT_Pos>(configuration.y_phase_26_6)};
        FT_Set_Transform(value->face, nullptr, &phase);
        value->glyph_count = static_cast<std::uint32_t>(value->face->num_glyphs);
        auto candidate = std::unique_ptr<hinted_font>(new hinted_font(std::move(value)));
        result.swap(candidate);
        error = hinted_font_error::none;
        return true;
    } catch (const std::bad_alloc&) {
        return fail(hinted_font_error::resource_exhausted, error);
    } catch (const std::length_error&) {
        return fail(hinted_font_error::resource_exhausted, error);
    } catch (...) {
        return fail(hinted_font_error::hinting_failed, error);
    }
}

bool hinted_font::try_capture(std::span<const std::uint32_t> glyph_indices,
    std::shared_ptr<const hinted_glyph_batch>& result, hinted_font_error& error) noexcept
{
    if (!valid_glyphs(glyph_indices, state_->glyph_count))
        return fail(hinted_font_error::invalid_argument, error);
    try {
        // The face slot is mutable. No two batches may interleave its loads,
        // including when source contexts later borrow it under their use lease.
        const std::lock_guard lock(state_->mutex);
        auto candidate = std::make_shared<hinted_glyph_batch>();
        candidate->identity = state_->identity;
        candidate->glyphs.reserve(glyph_indices.size());
        // Algorithm: sequential native glyph execution and immediate slot capture.
        // Time: O(H + P + C + G), H = dependent bytecode work, P/C = points/contours.
        // Space: O(P + C + G); exact geometry uses bulk copies, not scalar conversion.
        for (const auto glyph_index : glyph_indices) {
            constexpr FT_Int32 flags = FT_LOAD_NO_BITMAP | FT_LOAD_NO_AUTOHINT |
                FT_LOAD_PEDANTIC | FT_LOAD_TARGET_NORMAL;
            if (FT_Load_Glyph(state_->face, glyph_index, flags) != 0)
                return fail(hinted_font_error::hinting_failed, error);
            const auto slot = state_->face->glyph;
            if (slot == nullptr || slot->glyph_index != glyph_index ||
                slot->format != FT_GLYPH_FORMAT_OUTLINE || slot->outline.n_points < 0 ||
                slot->outline.n_contours < 0 ||
                (slot->outline.n_points > 0 && (slot->outline.points == nullptr || slot->outline.tags == nullptr)) ||
                (slot->outline.n_contours > 0 && slot->outline.contours == nullptr) ||
                FT_Outline_Check(&slot->outline) != 0)
                return fail(hinted_font_error::hinting_failed, error);
            hinted_glyph glyph{};
            glyph.glyph_index = glyph_index;
            glyph.advance_x_26_6 = slot->advance.x;
            glyph.advance_y_26_6 = slot->advance.y;
            glyph.horizontal_bearing_x_26_6 = slot->metrics.horiBearingX;
            glyph.horizontal_bearing_y_26_6 = slot->metrics.horiBearingY;
            glyph.width_26_6 = slot->metrics.width;
            glyph.height_26_6 = slot->metrics.height;
            glyph.horizontal_advance_26_6 = slot->metrics.horiAdvance;
            glyph.vertical_bearing_x_26_6 = slot->metrics.vertBearingX;
            glyph.vertical_bearing_y_26_6 = slot->metrics.vertBearingY;
            glyph.vertical_advance_26_6 = slot->metrics.vertAdvance;
            glyph.linear_horizontal_advance_16_16 = slot->linearHoriAdvance;
            glyph.linear_vertical_advance_16_16 = slot->linearVertAdvance;
            glyph.left_side_bearing_delta_26_6 = slot->lsb_delta;
            glyph.right_side_bearing_delta_26_6 = slot->rsb_delta;
            glyph.outline_flags = slot->outline.flags;
            static_assert(std::is_same_v<FT_Pos, long>);
            static_assert(std::is_same_v<FT_Short, std::int16_t>);
            static_assert(sizeof(hinted_outline_point) == sizeof(FT_Vector));
            static_assert(offsetof(hinted_outline_point, y_26_6) == offsetof(FT_Vector, y));
            static_assert(std::is_trivially_copyable_v<hinted_outline_point>);
            const auto points = static_cast<std::size_t>(slot->outline.n_points);
            const auto contours = static_cast<std::size_t>(slot->outline.n_contours);
            glyph.points.resize(points);
            glyph.tags.resize(points);
            glyph.contour_ends.resize(contours);
            if (points != 0U) {
                std::memcpy(glyph.points.data(), slot->outline.points, points * sizeof(FT_Vector));
                std::memcpy(glyph.tags.data(), slot->outline.tags, points);
            }
            if (contours != 0U)
                std::memcpy(glyph.contour_ends.data(), slot->outline.contours, contours * sizeof(FT_Short));
            candidate->glyphs.push_back(std::move(glyph));
        }
        result = std::move(candidate);
        error = hinted_font_error::none;
        return true;
    } catch (const std::bad_alloc&) {
        return fail(hinted_font_error::resource_exhausted, error);
    } catch (const std::length_error&) {
        return fail(hinted_font_error::resource_exhausted, error);
    } catch (...) {
        return fail(hinted_font_error::hinting_failed, error);
    }
}

} // namespace progpu::native::text
