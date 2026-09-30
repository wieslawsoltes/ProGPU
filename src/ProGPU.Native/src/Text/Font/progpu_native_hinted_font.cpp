#include "progpu_native_hinted_font.hpp"
#include "progpu_native_hinted_transport.hpp"

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
    const std::array<const void*, 17> functions{
        reinterpret_cast<const void*>(&FT_Init_FreeType), reinterpret_cast<const void*>(&FT_Done_FreeType),
        reinterpret_cast<const void*>(&FT_Library_Version), reinterpret_cast<const void*>(&FT_Property_Set),
        reinterpret_cast<const void*>(&FT_Property_Get), reinterpret_cast<const void*>(&FT_New_Memory_Face),
        reinterpret_cast<const void*>(&FT_Done_Face), reinterpret_cast<const void*>(&FT_Get_Font_Format),
        reinterpret_cast<const void*>(&FT_Get_MM_Var), reinterpret_cast<const void*>(&FT_Done_MM_Var),
        reinterpret_cast<const void*>(&FT_Set_Var_Design_Coordinates), reinterpret_cast<const void*>(&FT_Request_Size),
        reinterpret_cast<const void*>(&FT_Set_Transform), reinterpret_cast<const void*>(&FT_Load_Glyph),
        reinterpret_cast<const void*>(&FT_Outline_Check), reinterpret_cast<const void*>(&FT_Get_Char_Index),
        reinterpret_cast<const void*>(&FT_MulFix)};
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
        if (value->face->size == nullptr || value->face->units_per_EM == 0U ||
            value->face->size->metrics.x_scale <= 0 || value->face->size->metrics.y_scale <= 0)
            return fail(hinted_font_error::invalid_font, error);
        const auto& metrics = value->face->size->metrics;
        identity->device_frame = {value->face->units_per_EM, metrics.x_ppem, metrics.y_ppem,
            metrics.x_scale, metrics.y_scale, metrics.ascender, metrics.descender,
            metrics.height, metrics.max_advance, value->face->ascender, value->face->descender,
            value->face->height, value->face->max_advance_width};
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

hinted_projection_result project_hinted_design_vectors(const hinted_glyph_batch& batch,
    std::span<const hinted_design_vector> input, std::span<hinted_outline_point> output,
    hinted_projection_policy policy) noexcept
{
    static_assert(sizeof(hinted_design_vector) == 8U && offsetof(hinted_design_vector, y) == 4U);
    static_assert(sizeof(hinted_outline_point) == 2U * sizeof(long));
    static_assert(offsetof(hinted_outline_point, y_26_6) == sizeof(long));
    hinted_projection_path path = hinted_projection_path::none;
    if (policy == hinted_projection_policy::scalar_reference) path = hinted_projection_path::scalar_reference;
#if defined(__aarch64__) || defined(_M_ARM64) || defined(__SSE2__) || defined(_M_X64)
    else if (policy == hinted_projection_policy::automatic || policy == hinted_projection_policy::intrinsic_simd)
        path = hinted_projection_path::intrinsic_simd;
#endif
    // These CPU-visible displacement values precede native GPOS. A GPU pass
    // would require a new readback; forced GPU policies therefore fail closed.
    if (path == hinted_projection_path::none) return {hinted_projection_error::unsupported_policy, path};
    const auto fail_projection = [path](hinted_projection_error error) noexcept {
        return hinted_projection_result{error, path};
    };
    if (batch.identity == nullptr || batch.identity->source == nullptr)
        return fail_projection(hinted_projection_error::invalid_argument);
    const auto& frame = batch.identity->device_frame;
    // Public fixed computations admit signed-32-bit operands even on LP64.
    if (frame.units_per_em == 0U || frame.x_scale_16_16 <= 0 || frame.y_scale_16_16 <= 0 ||
        frame.x_scale_16_16 > std::numeric_limits<std::int32_t>::max() ||
        frame.y_scale_16_16 > std::numeric_limits<std::int32_t>::max())
        return fail_projection(hinted_projection_error::unsupported_frame);
    if (output.size() < input.size()) return fail_projection(hinted_projection_error::insufficient_capacity);
    const auto input_address = reinterpret_cast<std::uintptr_t>(input.data());
    if ((!input.empty() && (input.data() == nullptr || input_address % alignof(hinted_design_vector) != 0U)) ||
        input.size() > (std::numeric_limits<std::uintptr_t>::max() - input_address) / sizeof(hinted_design_vector) ||
        hinted_batch_output_aliases(batch, output)) return fail_projection(hinted_projection_error::invalid_argument);
    const auto output_address = reinterpret_cast<std::uintptr_t>(output.data());
    if (!input.empty() && !output.empty() &&
        (input_address <= output_address ? output_address - input_address < input.size_bytes() :
            input_address - output_address < output.size_bytes())) return fail_projection(hinted_projection_error::invalid_argument);

    const auto sx = static_cast<std::int32_t>(frame.x_scale_16_16);
    const auto sy = static_cast<std::int32_t>(frame.y_scale_16_16);
    const auto bound = [](std::int32_t scale, bool negative) noexcept {
        const std::int64_t magnitude = negative ? 2147483648LL : 2147483647LL;
        const auto maximum = (magnitude * 65536 + 32767) / scale;
        const auto admitted = maximum < magnitude ? maximum : magnitude;
        return static_cast<std::int32_t>(negative ? -admitted : admitted);
    };
    const hinted_design_vector minimum{bound(sx, true), bound(sy, true)};
    const hinted_design_vector maximum{bound(sx, false), bound(sy, false)};
    std::size_t index = 0U;
    if (path == hinted_projection_path::intrinsic_simd) {
#if defined(__aarch64__) || defined(_M_ARM64)
        const std::array<std::int32_t, 4> lower{minimum.x, minimum.y, minimum.x, minimum.y};
        const std::array<std::int32_t, 4> upper{maximum.x, maximum.y, maximum.x, maximum.y};
        for (; input.size() - index >= 2U; index += 2U) {
            int32x4_t lanes{};
            std::memcpy(&lanes, input.data() + index, sizeof(lanes));
            if (vminvq_u32(vandq_u32(vcgeq_s32(lanes, vld1q_s32(lower.data())),
                vcleq_s32(lanes, vld1q_s32(upper.data())))) == 0U)
                return fail_projection(hinted_projection_error::unsupported_frame);
        }
#elif defined(__SSE2__) || defined(_M_X64)
        const auto lower = _mm_setr_epi32(minimum.x, minimum.y, minimum.x, minimum.y);
        const auto upper = _mm_setr_epi32(maximum.x, maximum.y, maximum.x, maximum.y);
        for (; input.size() - index >= 2U; index += 2U) {
            const auto lanes = _mm_loadu_si128(reinterpret_cast<const __m128i*>(input.data() + index));
            if (_mm_movemask_epi8(_mm_or_si128(_mm_cmpgt_epi32(lower, lanes), _mm_cmpgt_epi32(lanes, upper))) != 0)
                return fail_projection(hinted_projection_error::unsupported_frame);
        }
#endif
    }
    for (; index < input.size(); ++index) {
        const auto value = input[index];
        if (value.x < minimum.x || value.x > maximum.x || value.y < minimum.y || value.y > maximum.y)
            return fail_projection(hinted_projection_error::unsupported_frame);
    }
    // Only after the complete ownership/domain pass may any output be written.
    index = 0U;
    if (path == hinted_projection_path::intrinsic_simd) {
#if defined(__aarch64__) || defined(_M_ARM64)
        const std::array<std::uint32_t, 4> scales{static_cast<std::uint32_t>(sx), static_cast<std::uint32_t>(sy),
            static_cast<std::uint32_t>(sx), static_cast<std::uint32_t>(sy)};
        const auto scale = vld1q_u32(scales.data());
        const auto rounded = [](uint64x2_t product, int64x2_t sign) noexcept {
            const auto magnitude = vreinterpretq_s64_u64(vshrq_n_u64(vaddq_u64(product, vdupq_n_u64(32768U)), 16));
            return vsubq_s64(veorq_s64(magnitude, sign), sign);
        };
        for (; input.size() - index >= 2U; index += 2U) {
            int32x4_t lanes{};
            std::memcpy(&lanes, input.data() + index, sizeof(lanes));
            const auto sign = vshrq_n_s32(lanes, 31);
            const auto magnitude = vreinterpretq_u32_s32(vabsq_s32(lanes));
            const auto first = rounded(vmull_u32(vget_low_u32(magnitude), vget_low_u32(scale)), vmovl_s32(vget_low_s32(sign)));
            const auto second = rounded(vmull_u32(vget_high_u32(magnitude), vget_high_u32(scale)), vmovl_s32(vget_high_s32(sign)));
            if constexpr (sizeof(long) == 8U) {
                std::memcpy(output.data() + index, &first, sizeof(first));
                std::memcpy(output.data() + index + 1U, &second, sizeof(second));
            } else {
                const auto values = vcombine_s32(vmovn_s64(first), vmovn_s64(second));
                std::memcpy(output.data() + index, &values, sizeof(values));
            }
        }
#elif defined(__SSE2__) || defined(_M_X64)
        const auto scales = _mm_setr_epi32(sx, sy, sx, sy);
        const auto rounded = [](__m128i product, __m128i sign) noexcept {
            const auto magnitude = _mm_srli_epi64(_mm_add_epi64(product, _mm_set1_epi64x(32768)), 16);
            return _mm_sub_epi64(_mm_xor_si128(magnitude, sign), sign);
        };
        for (; input.size() - index >= 2U; index += 2U) {
            const auto lanes = _mm_loadu_si128(reinterpret_cast<const __m128i*>(input.data() + index));
            const auto sign = _mm_srai_epi32(lanes, 31);
            const auto magnitude = _mm_sub_epi32(_mm_xor_si128(lanes, sign), sign);
            const auto x = rounded(_mm_mul_epu32(magnitude, scales), _mm_shuffle_epi32(sign, _MM_SHUFFLE(2, 2, 0, 0)));
            const auto y = rounded(_mm_mul_epu32(_mm_srli_si128(magnitude, 4), _mm_srli_si128(scales, 4)),
                _mm_shuffle_epi32(sign, _MM_SHUFFLE(3, 3, 1, 1)));
            if constexpr (sizeof(long) == 8U) {
                const auto first = _mm_unpacklo_epi64(x, y);
                const auto second = _mm_unpackhi_epi64(x, y);
                std::memcpy(output.data() + index, &first, sizeof(first));
                std::memcpy(output.data() + index + 1U, &second, sizeof(second));
            } else {
                const auto values = _mm_unpacklo_epi32(_mm_shuffle_epi32(x, _MM_SHUFFLE(2, 0, 2, 0)),
                    _mm_shuffle_epi32(y, _MM_SHUFFLE(2, 0, 2, 0)));
                std::memcpy(output.data() + index, &values, sizeof(values));
            }
        }
#endif
    }
    // At most one vector remains in SIMD mode. Explicit scalar-reference mode
    // uses the actual public dependency function for its independent oracle.
    for (; index < input.size(); ++index)
        output[index] = {FT_MulFix(input[index].x, sx), FT_MulFix(input[index].y, sy)};
    return {hinted_projection_error::none, path};
}

} // namespace progpu::native::text
