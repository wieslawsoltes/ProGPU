#include "progpu_native.h"
#include "progpu_native_text_styles.h"
#include "progpu_native_text_flow.h"
#include "progpu_native_text.hpp"
#include "progpu_native_text_font_source.hpp"
#include "../progpu_native_text_cluster_breaks_internal.hpp"
#include "../Font/progpu_native_hinted_transport.hpp"
#include "../Font/progpu_native_hinted_shaper.hpp"
#include "progpu_native_hinted_paragraph_internal.hpp"
#include "progpu_native_hinted_source_fitting.hpp"
#include "progpu_native_hinted_paragraph_transport_internal.hpp"
#include "progpu_native_owned_allocation_internal.hpp"
#include "../progpu_native_text_layout_retained_internal.hpp"
#if defined(PROGPU_NATIVE_FONT_HINTING)
#include "../Font/progpu_native_hinted_font_cache.hpp"
#endif

#include <algorithm>
#include <array>
#include <cstddef>
#include <cstdint>
#include <cmath>
#include <cstring>
#include <limits>
#include <new>
#include <span>
#include <type_traits>
#include <utility>
#include <vector>
#if defined(__aarch64__) || defined(_M_ARM64)
#include <arm_neon.h>
#elif defined(__SSE2__) || defined(_M_X64)
#include <emmintrin.h>
#endif

// Stable C ABI adapter for the ProGPU-owned native text stack. The shaping
// algorithms remain in Text/Shaping; this file only validates fixed-layout
// wire records, partitions caller-owned scratch, and performs bulk copies at
// the language boundary. No supplied pointer is retained.

struct progpu_native_text_owned_font final {
    std::shared_ptr<const progpu::native::text::owned_font_source> source{};
    progpu::native::text::sfnt_font_view font{};
    std::uint64_t identity = 0U;
};

struct progpu_native_hinted_batch final {
    std::shared_ptr<const progpu::native::text::hinted_glyph_batch> generation{};
};

struct progpu_native_hinted_run final {
    std::shared_ptr<const progpu::native::text::hinted_shaped_run> generation{};
};

struct progpu_native_hinted_paragraph final {
    std::shared_ptr<const progpu::native::text::hinted_paragraph_generation> generation{};
    std::shared_ptr<const progpu::native::text::hinted_paragraph_interaction> interaction{};
};

struct progpu_native_hinted_paragraph_frame final {
    std::shared_ptr<const progpu::native::text::hinted_paragraph_glyph_frame> generation{};
    std::shared_ptr<const progpu::native::text::hinted_paragraph_interaction> interaction{};
};

// Original-library owner for a cached, read-only C borrow. Already-flat records
// remain in the retained original paragraph/resource; only native writer types
// need fieldwise conversion. No target, paint or reconstructed generation is
// part of this producer handle.
struct progpu_native_hinted_glyph_resource final {
    std::shared_ptr<const progpu::native::text::hinted_paragraph_glyph_resource> generation{};
    std::shared_ptr<const progpu::native::text::hinted_paragraph_interaction> interaction{};
    std::vector<progpu_native_hinted_paragraph_device_style> device_styles{};
    std::vector<std::int32_t> variation_coordinates_16_16{};
    std::vector<progpu_native_hinted_glyph_run_slice> run_slices{};
    std::vector<progpu_native_hinted_glyph_outline_owner> outline_owners{};
    std::vector<progpu_native_text_scalar> admitted_scalars{};
    std::vector<progpu_native_text_bidi_level> scalar_levels{};
    std::vector<progpu_native_text_shaping_glyph> logical_glyphs{};
    std::vector<progpu_native_hinted_paragraph_glyph_owner> logical_owners{};
    std::vector<progpu_native_hinted_paragraph_glyph_owner> positioned_owners{};
    std::vector<progpu_native_positioned_text_line> lines{};
    std::vector<progpu_native_text_cluster_box> boxes{};
    std::vector<progpu_native_text_caret_stop> carets{};
    progpu_native_hinted_glyph_resource_view view{};
    std::vector<progpu_native_hinted_glyph_nominal_metrics> nominal_metrics{};
    bool has_nominal_metrics = false;
    std::vector<progpu_native_hinted_text_line_frame> line_frames{};
};

struct progpu_native_text_plan_entry final {
    std::vector<std::uint16_t> gsub_lookups{};
    std::vector<std::uint16_t> gpos_lookups{};
    std::vector<progpu::native::text::open_type_lookup_accelerator>
        gsub_accelerators{};
    std::vector<progpu::native::text::open_type_lookup_accelerator>
        gpos_accelerators{};
    std::vector<progpu::native::text::open_type_context_subtable_requirement>
        gsub_context_subtables{};
    std::vector<progpu::native::text::open_type_context_coverage_requirement>
        gsub_context_coverages{};
    std::vector<progpu::native::text::open_type_context_subtable_requirement>
        gpos_context_subtables{};
    std::vector<progpu::native::text::open_type_context_coverage_requirement>
        gpos_context_coverages{};
    progpu::native::text::open_type_shape_plan plan{};
    std::uint64_t last_used = 0U;
    bool valid = false;
};

struct progpu_native_text_context final {
    static constexpr std::size_t plan_capacity = 16U;

    struct device_width_entry final {
        progpu::native::text::sfnt_horizontal_device_metrics metrics{};
        std::uint32_t font_index = 0U;
        std::uint16_t pixels_per_em = 0U;
    };

    std::shared_ptr<const progpu::native::text::owned_font_source> source{};
    std::vector<std::byte> normalization_bytes{};
    progpu::native::text::sfnt_font_view font{};
    progpu::native::text::unicode_normalization_data normalization{};
    bool has_normalization = false;
    std::vector<progpu_native_text_owned_font> fallback_fonts{};
    std::vector<progpu_native_text_plan_entry> plans{};
    std::uint64_t plan_clock = 0U;
    std::uint32_t plan_build_count = 0U;
    // Borrow only this context's immutable font storage, including fallback
    // vector moves. Zero ppem marks unused slots; absence is also cached.
    std::array<device_width_entry, 16U> device_widths{};
    std::size_t device_width_cursor = 0U;
#if defined(PROGPU_NATIVE_FONT_HINTING)
    // Last member retires native faces before this context releases its sources.
    // Created only by an explicit whole-batch capture, never context startup.
    std::unique_ptr<progpu::native::text::hinted_font_cache> hinted_cache{};
#endif

    bool try_get_device_widths(std::uint32_t font_index,
        std::uint16_t pixels_per_em,
        progpu::native::text::sfnt_horizontal_device_metrics& result,
        progpu::native::text::font_error& error) noexcept {
        for (const auto& entry : device_widths) {
            if (entry.pixels_per_em == pixels_per_em && entry.font_index == font_index) {
                result = entry.metrics;
                error = progpu::native::text::font_error::none;
                return true;
            }
        }
        const auto* selected = font_at(font_index);
        bool available = false;
        if (selected == nullptr || !selected->try_get_horizontal_device_metrics(
                pixels_per_em, result, available, &error)) return false;
        device_widths[device_width_cursor] = {result, font_index, pixels_per_em};
        device_width_cursor = (device_width_cursor + 1U) % device_widths.size();
        return true;
    }

    bool try_get_plan(
        const progpu::native::text::sfnt_font_view& selected_font,
        const progpu::native::text::open_type_shape_run_options& options,
        const progpu::native::text::open_type_shape_plan*& result,
        progpu::native::text::font_error& error) {
        using namespace progpu::native::text;
        result = nullptr;
        ++plan_clock;
        if (plan_clock == 0U) {
            plan_clock = 1U;
            for (auto& entry : plans) entry.last_used = 0U;
        }
        for (auto& entry : plans) {
            if (!entry.valid || !entry.plan.matches(selected_font, options)) {
                continue;
            }
            entry.last_used = plan_clock;
            result = &entry.plan;
            error = font_error::none;
            return true;
        }
        open_type_shape_plan_requirements requirements{};
        if (!try_get_open_type_shape_plan_requirements(
                selected_font, options, requirements, &error)) {
            return false;
        }
        progpu_native_text_plan_entry* entry = nullptr;
        if (plans.size() < plan_capacity) {
            plans.emplace_back();
            entry = &plans.back();
        } else {
            entry = &*std::min_element(
                plans.begin(),
                plans.end(),
                [](const auto& left, const auto& right) noexcept {
                    return left.last_used < right.last_used;
                });
        }
        entry->valid = false;
        entry->gsub_lookups.resize(requirements.gsub_lookup_capacity);
        entry->gpos_lookups.resize(requirements.gpos_lookup_capacity);
        entry->gsub_accelerators.resize(requirements.gsub_accelerator_capacity);
        entry->gpos_accelerators.resize(requirements.gpos_accelerator_capacity);
        entry->gsub_context_subtables.resize(
            requirements.gsub_context_subtable_capacity);
        entry->gsub_context_coverages.resize(
            requirements.gsub_context_coverage_capacity);
        entry->gpos_context_subtables.resize(
            requirements.gpos_context_subtable_capacity);
        entry->gpos_context_coverages.resize(
            requirements.gpos_context_coverage_capacity);
        if (!try_build_open_type_shape_plan(
                selected_font,
                options,
                entry->gsub_lookups,
                entry->gpos_lookups,
                entry->gsub_accelerators,
                entry->gpos_accelerators,
                entry->gsub_context_subtables,
                entry->gsub_context_coverages,
                entry->gpos_context_subtables,
                entry->gpos_context_coverages,
                entry->plan,
                &error)) {
            return false;
        }
        entry->valid = true;
        entry->last_used = plan_clock;
        if (plan_build_count != std::numeric_limits<std::uint32_t>::max()) {
            ++plan_build_count;
        }
        result = &entry->plan;
        return true;
    }

    std::size_t font_count() const noexcept {
        return fallback_fonts.size() + 1U;
    }

    std::shared_ptr<const progpu::native::text::owned_font_source> source_at(std::size_t index) const noexcept {
        if (index == 0U) return source;
        const auto fallback = index - 1U;
        return fallback < fallback_fonts.size() ? fallback_fonts[fallback].source : nullptr;
    }

    const progpu::native::text::sfnt_font_view* font_at(
        std::size_t index) const noexcept {
        if (index == 0U) return &font;
        const std::size_t fallback = index - 1U;
        return fallback < fallback_fonts.size()
            ? &fallback_fonts[fallback].font
            : nullptr;
    }
};

std::shared_ptr<const progpu::native::text::owned_font_source>
progpu::native::text::select_context_font_source(
    progpu_native_text_context* context, std::uint32_t font_index) noexcept
{
    return context != nullptr ? context->source_at(font_index) : nullptr;
}

bool progpu::native::text::capture_context_hinted(progpu_native_text_context* context,
    std::uint32_t font_index, const hinted_font_configuration& configuration,
    std::span<const std::uint32_t> glyph_indices, std::shared_ptr<const hinted_glyph_batch>& result,
    hinted_font_error& error) noexcept
{
    const auto source = select_context_font_source(context, font_index);
    if (source == nullptr) {
        error = hinted_font_error::invalid_argument;
        return false;
    }
#if defined(PROGPU_NATIVE_FONT_HINTING)
    try {
        if (context->hinted_cache == nullptr) context->hinted_cache = std::make_unique<hinted_font_cache>();
        return context->hinted_cache->try_capture(source, configuration, glyph_indices, result, error);
    } catch (const std::bad_alloc&) {
        error = hinted_font_error::resource_exhausted;
    } catch (...) {
        error = hinted_font_error::hinting_failed;
    }
#else
    (void)configuration;
    (void)glyph_indices;
    (void)result;
    error = hinted_font_error::dependency_unavailable;
#endif
    return false;
}

#if defined(PROGPU_NATIVE_FONT_HINTING)
std::shared_ptr<const progpu::native::text::hinted_shaped_run>
progpu::native::text::select_hinted_run_generation(const progpu_native_hinted_run* run) noexcept {
    return run != nullptr ? run->generation : nullptr;
}
#endif

namespace {

using namespace progpu::native::text;

constexpr auto default_script =
    open_type_tag::from_chars('D', 'F', 'L', 'T');
constexpr auto gsub_tag =
    open_type_tag::from_chars('G', 'S', 'U', 'B');
constexpr auto gpos_tag =
    open_type_tag::from_chars('G', 'P', 'O', 'S');
constexpr std::uint32_t default_feature_capacity = 26U;
constexpr std::uint32_t policy_feature_capacity = 32U;
constexpr std::uint32_t allowed_shape_flags =
    PROGPU_NATIVE_TEXT_SHAPE_ZERO_MARK_ADVANCES;

std::int32_t negate_managed_metric(std::int32_t value) noexcept {
    // C# unchecked integer negation preserves Int32.MinValue. Avoid signed
    // overflow while keeping the stable native boundary bit-identical.
    return value == std::numeric_limits<std::int32_t>::min()
        ? value
        : -value;
}

struct shape_capacities final {
    std::uint32_t glyphs = 0U;
    std::uint32_t graphemes = 0U;
    std::uint32_t gsub_lookups = 0U;
    std::uint32_t gpos_lookups = 0U;
    std::uint32_t script_actions = 0U;
    std::uint32_t complex_values = 0U;
    std::uint32_t complex_indices = 0U;
    std::uint32_t verification_glyphs = 0U;
    std::uint32_t normalization_scalars = 0U;
    std::uint32_t base_features = 0U;
    std::uint32_t explicit_features = 0U;
    std::uint32_t requested_features = 0U;
    std::uint32_t feature_settings = 0U;
    std::uint32_t variation_region_scalars = 0U;
    std::size_t scratch_bytes = 0U;
};

class scratch_size_builder final {
public:
    template <typename T>
    bool add(std::size_t count) noexcept {
        static_assert(std::is_trivially_destructible_v<T>);
        const std::size_t alignment = alignof(T);
        const std::size_t padding =
            (alignment - (size_ % alignment)) % alignment;
        if (padding > std::numeric_limits<std::size_t>::max() - size_) {
            return false;
        }
        size_ += padding;
        if (count != 0U &&
            count > (std::numeric_limits<std::size_t>::max() - size_) /
                sizeof(T)) {
            return false;
        }
        size_ += count * sizeof(T);
        return true;
    }

    [[nodiscard]] std::size_t size() const noexcept { return size_; }

private:
    std::size_t size_ = 0U;
};

class scratch_arena final {
public:
    scratch_arena(void* data, std::size_t size) noexcept
        : data_(static_cast<std::byte*>(data)), size_(size) {}

    template <typename T>
    bool take(std::size_t count, std::span<T>& result) noexcept {
        result = {};
        const std::size_t alignment = alignof(T);
        const auto address = reinterpret_cast<std::uintptr_t>(data_ + offset_);
        const std::size_t padding =
            (alignment - (address % alignment)) % alignment;
        if (padding > size_ - std::min(size_, offset_)) return false;
        offset_ += padding;
        if (count > (size_ - std::min(size_, offset_)) / sizeof(T)) {
            return false;
        }
        if (count != 0U) {
            result = std::span<T>{
                reinterpret_cast<T*>(data_ + offset_), count};
        }
        offset_ += count * sizeof(T);
        return true;
    }

    [[nodiscard]] std::size_t used() const noexcept { return offset_; }

private:
    std::byte* data_ = nullptr;
    std::size_t size_ = 0U;
    std::size_t offset_ = 0U;
};

bool has_pointer(const void* pointer, std::uint32_t count) noexcept {
    return count == 0U || pointer != nullptr;
}

bool byte_ranges_overlap(const void* left, std::uint64_t left_size,
    const void* right, std::uint64_t right_size) noexcept {
    if (left_size == 0U || right_size == 0U) return false;
    const auto a = reinterpret_cast<std::uintptr_t>(left);
    const auto b = reinterpret_cast<std::uintptr_t>(right);
    return a <= b ? b - a < left_size : a - b < right_size;
}

template <typename T>
bool has_aligned_pointer(const T* pointer, std::uint32_t count) noexcept {
    return count == 0U ||
        (pointer != nullptr &&
            reinterpret_cast<std::uintptr_t>(pointer) % alignof(T) == 0U);
}

template<class T>
bool valid_hinted_buffer(const T* pointer, std::uint32_t count) noexcept {
    if (!has_aligned_pointer(pointer, count)) return false;
    const auto address = reinterpret_cast<std::uintptr_t>(pointer);
    return static_cast<std::uint64_t>(count) <=
        (std::numeric_limits<std::uintptr_t>::max() - address) / sizeof(T);
}

bool hinted_publication_aliases_context(progpu_native_hinted_batch** output,
    const progpu_native_text_context& context) noexcept {
    const auto overlaps = [output](const void* data, std::size_t bytes) noexcept {
        return byte_ranges_overlap(output, sizeof(*output), data, bytes);
    };
    if (overlaps(&context, sizeof(context)) ||
        overlaps(context.fallback_fonts.data(), context.fallback_fonts.capacity() * sizeof(progpu_native_text_owned_font)) ||
        overlaps(context.normalization_bytes.data(), context.normalization_bytes.capacity())) return true;
    for (std::size_t index = 0U; index < context.font_count(); ++index) {
        const auto source = context.source_at(index);
        if (source != nullptr && (overlaps(source.get(), sizeof(*source)) ||
            overlaps(source->bytes.data(), source->bytes.size()))) return true;
    }
    return false;
}

progpu_native_status hinted_status(progpu::native::text::hinted_font_error error) noexcept {
    using progpu::native::text::hinted_font_error;
    switch (error) {
    case hinted_font_error::none: return PROGPU_NATIVE_STATUS_SUCCESS;
    case hinted_font_error::invalid_argument:
    case hinted_font_error::invalid_font: return PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
    case hinted_font_error::unsupported_font:
    case hinted_font_error::dependency_unavailable: return PROGPU_NATIVE_STATUS_UNSUPPORTED;
    case hinted_font_error::resource_exhausted: return PROGPU_NATIVE_STATUS_OUT_OF_MEMORY;
    default: return PROGPU_NATIVE_STATUS_INTERNAL_ERROR;
    }
}

bool valid_scalar(std::uint32_t value) noexcept {
    return value <= 0x10FFFFU && (value < 0xD800U || value > 0xDFFFU);
}

bool valid_tag(std::uint32_t value) noexcept {
    if (value == 0U) return true;
    for (std::uint32_t shift = 0U; shift <= 24U; shift += 8U) {
        const auto character = static_cast<std::uint8_t>(value >> shift);
        if (character < 0x20U || character > 0x7EU) return false;
    }
    return true;
}

bool valid_wire_scalars(
    const progpu_native_text_scalar* values,
    std::uint32_t count) noexcept {
    if (!has_aligned_pointer(values, count)) return false;
    for (std::uint32_t index = 0U; index < count; ++index) {
        if (!valid_scalar(values[index].code_point) ||
            values[index].input_length == 0U ||
            values[index].reserved != 0U) {
            return false;
        }
    }
    return true;
}

bool valid_request(
    const progpu_native_text_shape_request* request,
    bool require_font = true) noexcept {
    if (request == nullptr ||
        request->struct_size < sizeof(progpu_native_text_shape_request) ||
        request->abi_version != PROGPU_NATIVE_ABI_VERSION ||
        (require_font &&
            (request->font_data == nullptr || request->font_size == 0U)) ||
        (!require_font &&
            ((request->font_data == nullptr) != (request->font_size == 0U))) ||
        request->flags & ~allowed_shape_flags ||
        request->reserved0 != 0U || request->reserved1 != 0U ||
        request->direction > PROGPU_NATIVE_TEXT_DIRECTION_BOTTOM_TO_TOP ||
        request->cluster_level > PROGPU_NATIVE_TEXT_CLUSTER_GRAPHEMES ||
        request->buffer_flags > 0xFFU ||
        !valid_tag(request->unicode_script) || !valid_tag(request->language) ||
        !valid_wire_scalars(request->input, request->input_count) ||
        !valid_wire_scalars(
            request->pre_context, request->pre_context_count) ||
        !valid_wire_scalars(
            request->post_context, request->post_context_count) ||
        !has_aligned_pointer(request->features, request->feature_count) ||
        !has_aligned_pointer(
            request->normalized_coordinates,
            request->normalized_coordinate_count) ||
        !has_pointer(
            request->normalization_data,
            static_cast<std::uint32_t>(std::min<std::size_t>(
                request->normalization_data_size,
                std::numeric_limits<std::uint32_t>::max())))) {
        return false;
    }
    if ((request->normalization_data == nullptr) !=
        (request->normalization_data_size == 0U)) {
        return false;
    }
    const std::uint32_t contradictory =
        PROGPU_NATIVE_TEXT_BUFFER_PRESERVE_DEFAULT_IGNORABLES |
        PROGPU_NATIVE_TEXT_BUFFER_REMOVE_DEFAULT_IGNORABLES;
    if ((request->buffer_flags & contradictory) == contradictory) {
        return false;
    }
    for (std::uint32_t index = 0U; index < request->feature_count; ++index) {
        const auto& feature = request->features[index];
        if (!valid_tag(feature.tag) || feature.tag == 0U ||
            feature.start > feature.end) {
            return false;
        }
    }
    return true;
}

open_type_tag infer_script(
    const progpu_native_text_shape_request& request) noexcept {
    if (request.unicode_script != 0U &&
        request.unicode_script != default_script.value) {
        return open_type_tag{request.unicode_script};
    }
    for (std::uint32_t index = 0U; index < request.input_count; ++index) {
        const auto script = get_unicode_script(request.input[index].code_point);
        if (script != default_script) return script;
    }
    return default_script;
}

progpu_native_status status_from_error(font_error error) noexcept {
    switch (error) {
        case font_error::none:
            return PROGPU_NATIVE_STATUS_SUCCESS;
        case font_error::unsupported_container:
            return PROGPU_NATIVE_STATUS_UNSUPPORTED;
        case font_error::invalid_argument:
        case font_error::invalid_collection:
        case font_error::invalid_face:
        case font_error::truncated_directory:
        case font_error::invalid_glyph:
        case font_error::insufficient_buffer:
        case font_error::invalid_container:
        case font_error::invalid_compressed_data:
        case font_error::verification_failed:
            return PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
    }
    return PROGPU_NATIVE_STATUS_INTERNAL_ERROR;
}

bool try_resolve_normalization_data(
    const progpu_native_text_shape_request& request,
    unicode_normalization_data& storage,
    const unicode_normalization_data*& result) noexcept {
    result = get_default_unicode_normalization_data();
    if (request.normalization_data_size == 0U) {
        return result != nullptr;
    }
    unicode_error error = unicode_error::none;
    if (!unicode_normalization_data::try_create(
            std::span<const std::byte>{
                reinterpret_cast<const std::byte*>(
                    request.normalization_data),
                request.normalization_data_size},
            storage,
            &error)) {
        result = nullptr;
        return false;
    }
    result = &storage;
    return true;
}

bool try_get_lookup_count(
    const sfnt_font_view& font,
    open_type_tag tag,
    std::uint32_t& result,
    font_error& error) noexcept {
    result = 0U;
    sfnt_table_view table{};
    if (!font.try_get_table(tag, table)) return true;
    open_type_layout_table_view layout{};
    if (!open_type_layout_table_view::try_create(
            table.bytes, layout, &error)) {
        return false;
    }
    result = layout.lookup_count();
    return true;
}

bool try_add_capacity(
    std::uint32_t left,
    std::uint32_t right,
    std::uint32_t& result) noexcept {
    const auto sum = static_cast<std::uint64_t>(left) + right;
    if (sum > std::numeric_limits<std::uint32_t>::max()) return false;
    result = static_cast<std::uint32_t>(sum);
    return true;
}

bool try_compute_shape_scratch_bytes(
    const progpu_native_text_shape_request& request,
    shape_capacities& result,
    font_error& error) noexcept {
    scratch_size_builder size{};
    if (!size.add<unicode_scalar>(request.input_count) ||
        !size.add<unicode_scalar>(request.pre_context_count) ||
        !size.add<unicode_scalar>(request.post_context_count) ||
        !size.add<shaping_feature>(request.feature_count) ||
        !size.add<open_type_feature_setting>(result.base_features) ||
        !size.add<open_type_tag>(result.explicit_features) ||
        !size.add<open_type_tag>(result.requested_features) ||
        !size.add<shaping_feature>(result.feature_settings) ||
        !size.add<shaping_glyph>(result.glyphs) ||
        !size.add<unicode_grapheme_cluster>(result.graphemes) ||
        !size.add<std::uint16_t>(result.gsub_lookups) ||
        !size.add<std::uint16_t>(result.gpos_lookups) ||
        !size.add<shaping_attachment>(result.glyphs) ||
        !size.add<std::uint8_t>(result.glyphs) ||
        !size.add<open_type_arabic_action>(result.script_actions) ||
        !size.add<shaping_glyph_flags>(result.script_actions) ||
        !size.add<std::uint8_t>(result.complex_values) ||
        !size.add<std::uint8_t>(result.complex_values) ||
        !size.add<std::uint32_t>(result.complex_indices) ||
        !size.add<arabic_stretch_run>(result.glyphs) ||
        !size.add<shaping_glyph>(result.verification_glyphs) ||
        !size.add<unicode_scalar>(result.normalization_scalars) ||
        !size.add<float>(result.variation_region_scalars)) {
        error = font_error::invalid_argument;
        return false;
    }
    if (size.size() > std::numeric_limits<std::size_t>::max() -
            (alignof(std::max_align_t) - 1U)) {
        error = font_error::invalid_argument;
        return false;
    }
    result.scratch_bytes = size.size() + alignof(std::max_align_t) - 1U;
    return true;
}

bool try_build_capacities(
    const progpu_native_text_shape_request& request,
    const sfnt_font_view& font,
    const unicode_normalization_data* normalization,
    shape_capacities& result,
    font_error& error) noexcept {
    result = {};
    open_type_shaping_route route{};
    if (!try_resolve_open_type_shaping_route(
            font,
            infer_script(request),
            static_cast<shaping_direction>(request.direction),
            route,
            &error)) {
        return false;
    }
    if ((route.complex_script == open_type_complex_script::indic ||
            route.complex_script == open_type_complex_script::use) &&
        normalization == nullptr) {
        error = font_error::invalid_argument;
        return false;
    }
    if (request.input_count > std::numeric_limits<std::uint32_t>::max() / 3U) {
        error = font_error::invalid_argument;
        return false;
    }
    std::uint64_t decomposed_count = request.input_count;
    if (normalization != nullptr) {
        decomposed_count = 0U;
        for (std::uint32_t index = 0U; index < request.input_count; ++index) {
            std::span<const std::byte> decomposition{};
            decomposed_count += normalization->try_get_decomposition(
                    request.input[index].code_point, decomposition) &&
                    !decomposition.empty()
                ? decomposition.size() / 4U
                : 1U;
            if (decomposed_count > std::numeric_limits<std::uint32_t>::max()) {
                error = font_error::invalid_argument;
                return false;
            }
        }
    }
    const std::uint64_t expanded_count = decomposed_count + request.input_count;
    const std::uint64_t base_capacity =
        static_cast<std::uint64_t>(request.input_count) * 3U;
    const std::uint64_t glyph_capacity =
        std::max(base_capacity, expanded_count);
    if (glyph_capacity > std::numeric_limits<std::uint32_t>::max()) {
        error = font_error::invalid_argument;
        return false;
    }
    result.glyphs = static_cast<std::uint32_t>(glyph_capacity);
    result.normalization_scalars = static_cast<std::uint32_t>(decomposed_count);
    result.graphemes = request.input_count;
    result.script_actions = std::max(
        request.input_count, result.normalization_scalars);
    result.complex_values = route.complex_script == open_type_complex_script::none
        ? 0U
        : result.glyphs;
    result.complex_indices =
        (route.complex_script == open_type_complex_script::indic ||
            route.complex_script == open_type_complex_script::use)
        ? result.glyphs + 1U
        : 0U;
    const bool verify =
        (request.buffer_flags & PROGPU_NATIVE_TEXT_BUFFER_VERIFY) != 0U &&
        request.cluster_level <=
            PROGPU_NATIVE_TEXT_CLUSTER_MONOTONE_CHARACTERS;
    result.verification_glyphs = verify ? result.glyphs : 0U;
    if (!try_get_lookup_count(font, gsub_tag, result.gsub_lookups, error) ||
        !try_get_lookup_count(font, gpos_tag, result.gpos_lookups, error) ||
        !try_add_capacity(
            default_feature_capacity,
            request.feature_count,
            result.base_features) ||
        !try_add_capacity(
            result.base_features,
            policy_feature_capacity,
            result.requested_features) ||
        !try_add_capacity(
            result.requested_features,
            request.feature_count,
            result.feature_settings)) {
        if (error == font_error::none) error = font_error::invalid_argument;
        return false;
    }
    result.explicit_features = request.feature_count;

    if (request.normalized_coordinate_count != 0U) {
        std::uint16_t region_count = 0U;
        bool uses_hvar = false;
        if (!font.try_get_horizontal_advance_variation_region_count(
                std::span<const std::int16_t>{
                    request.normalized_coordinates,
                    request.normalized_coordinate_count},
                region_count,
                uses_hvar,
                &error)) {
            return false;
        }
        result.variation_region_scalars = uses_hvar ? region_count : 0U;
    }

    if (!try_compute_shape_scratch_bytes(request, result, error)) return false;
    error = font_error::none;
    return true;
}

unicode_scalar convert_scalar(const progpu_native_text_scalar& source) noexcept {
    return unicode_scalar{
        source.code_point,
        source.input_index,
        source.input_length,
        get_unicode_canonical_combining_class(source.code_point),
        0U,
        get_unicode_script(source.code_point)};
}

void copy_scalars(
    const progpu_native_text_scalar* source,
    std::span<unicode_scalar> destination) noexcept {
    for (std::size_t index = 0U; index < destination.size(); ++index) {
        destination[index] = convert_scalar(source[index]);
    }
}

bool hinted_run_output_aliases(const progpu_native_hinted_run& handle,
    const void* output, std::uint64_t bytes) noexcept {
    const auto overlaps = [=](const void* storage, std::size_t size) noexcept {
        return byte_ranges_overlap(output, bytes, storage, size);
    };
    const auto vector_aliases = [&](const auto& values) noexcept {
        return overlaps(values.data(), values.capacity() * sizeof(values[0]));
    };
    if (overlaps(&handle, sizeof(handle)) || handle.generation == nullptr) return true;
    const auto& run = *handle.generation;
    if (overlaps(&run, sizeof(run)) || vector_aliases(run.shaping_input) || vector_aliases(run.glyphs) ||
        vector_aliases(run.descriptor_indices) || vector_aliases(run.normalized_coordinates) ||
        (run.positioning != nullptr && run.positioning->allocation_aliases(output, static_cast<std::size_t>(bytes))) ||
        run.batch == nullptr) return true;
    const auto& batch = *run.batch;
    if (overlaps(&batch, sizeof(batch)) || vector_aliases(batch.glyphs) || batch.identity == nullptr) return true;
    const auto& identity = *batch.identity;
    if (overlaps(&identity, sizeof(identity)) || vector_aliases(identity.variation_coordinates_16_16) ||
        identity.source == nullptr || overlaps(identity.source.get(), sizeof(*identity.source)) ||
        vector_aliases(identity.source->bytes)) return true;
    for (const auto& glyph : batch.glyphs)
        if (vector_aliases(glyph.points) || vector_aliases(glyph.tags) || vector_aliases(glyph.contour_ends)) return true;
    return false;
}

bool hinted_run_publication_aliases_context(const void* output, std::uint64_t bytes,
    const progpu_native_text_context& context) noexcept {
    const auto overlaps = [=](const void* storage, std::size_t size) noexcept {
        return byte_ranges_overlap(output, bytes, storage, size);
    };
    const auto vector_aliases = [&](const auto& values) noexcept {
        return overlaps(values.data(), values.capacity() * sizeof(values[0]));
    };
    if (overlaps(&context, sizeof(context)) || vector_aliases(context.fallback_fonts) ||
        vector_aliases(context.normalization_bytes) || vector_aliases(context.plans)) return true;
    for (std::size_t index = 0U; index < context.font_count(); ++index) {
        const auto source = context.source_at(index);
        if (source != nullptr && (overlaps(source.get(), sizeof(*source)) || vector_aliases(source->bytes))) return true;
    }
    for (const auto& plan : context.plans)
        if (vector_aliases(plan.gsub_lookups) || vector_aliases(plan.gpos_lookups) ||
            vector_aliases(plan.gsub_accelerators) || vector_aliases(plan.gpos_accelerators) ||
            vector_aliases(plan.gsub_context_subtables) || vector_aliases(plan.gsub_context_coverages) ||
            vector_aliases(plan.gpos_context_subtables) || vector_aliases(plan.gpos_context_coverages)) return true;
#if defined(PROGPU_NATIVE_FONT_HINTING)
    if (context.hinted_cache != nullptr && overlaps(context.hinted_cache.get(), sizeof(*context.hinted_cache))) return true;
#endif
    return false;
}

bool valid_hinted_run(const progpu_native_hinted_run* handle) noexcept {
    if (!valid_hinted_buffer(handle, 1U) || handle->generation == nullptr || handle->generation->batch == nullptr) return false;
    const auto& run = *handle->generation;
    if (run.glyphs.size() > std::numeric_limits<std::uint32_t>::max() ||
        run.descriptor_indices.size() != run.glyphs.size() || run.source_descriptor_count > run.batch->glyphs.size()) return false;
    progpu_native_hinted_batch_counts counts{};
    if (get_hinted_batch_counts(*run.batch, counts) != hinted_transport_error::none) return false;
    for (std::size_t index = 0U; index < run.glyphs.size(); ++index) {
        const auto slot = run.descriptor_indices[index];
        if (slot >= run.source_descriptor_count || run.batch->glyphs[slot].glyph_index != run.glyphs[index].glyph_id) return false;
    }
    return true;
}

bool hinted_run_c_metrics_representable(const hinted_shaped_run& run) noexcept {
    for (const auto& glyph : run.glyphs)
        if (glyph.advance_y == std::numeric_limits<std::int32_t>::min() || glyph.offset_y == std::numeric_limits<std::int32_t>::min()) return false;
    return true;
}

void copy_hinted_run_glyph(const shaping_glyph& source, progpu_native_text_shaping_glyph& output) noexcept {
    output.glyph_id = source.glyph_id; output.code_point = source.code_point;
    output.cluster = source.cluster; output.flags = static_cast<std::uint32_t>(source.flags);
    static_assert(offsetof(progpu_native_text_shaping_glyph, advance_y) == offsetof(progpu_native_text_shaping_glyph, advance_x) + 4U);
    static_assert(offsetof(progpu_native_text_shaping_glyph, offset_x) == offsetof(progpu_native_text_shaping_glyph, advance_x) + 8U);
    static_assert(offsetof(progpu_native_text_shaping_glyph, offset_y) == offsetof(progpu_native_text_shaping_glyph, advance_x) + 12U);
    // Descriptor validation is ordered/dependent. The four independent signed32
    // metric lanes publish through the existing platform intrinsics, after the
    // whole run's exact Y-negation preflight. CPU-owned copy needs no GPU path.
#if defined(__aarch64__) || defined(_M_ARM64) || defined(__SSE2__) || defined(_M_X64)
    const std::array<std::int32_t, 4U> input{source.advance_x, source.advance_y, source.offset_x, source.offset_y};
    constexpr std::array<std::int32_t, 4U> sign{0, -1, 0, -1};
#if defined(__aarch64__) || defined(_M_ARM64)
    const auto lanes = vld1q_s32(input.data()); const auto mask = vld1q_s32(sign.data());
    const auto converted = vsubq_s32(veorq_s32(lanes, mask), mask);
#else
    const auto lanes = _mm_loadu_si128(reinterpret_cast<const __m128i*>(input.data()));
    const auto mask = _mm_loadu_si128(reinterpret_cast<const __m128i*>(sign.data()));
    const auto converted = _mm_sub_epi32(_mm_xor_si128(lanes, mask), mask);
#endif
    std::memcpy(reinterpret_cast<std::byte*>(&output) + offsetof(progpu_native_text_shaping_glyph, advance_x), &converted, sizeof(converted));
#else
    output.advance_x = source.advance_x; output.advance_y = -source.advance_y;
    output.offset_x = source.offset_x; output.offset_y = -source.offset_y;
#endif
}

bool valid_hinted_shape_inputs(const progpu_native_hinted_font_request* hint,
    const std::int32_t* axes, const progpu_native_text_shape_request* shape) noexcept {
    if (!valid_hinted_buffer(hint, 1U) || !valid_hinted_buffer(shape, 1U) ||
        hint->abi_version != PROGPU_NATIVE_ABI_VERSION || hint->struct_size != sizeof(*hint) || hint->reserved != 0U ||
        hint->x_pixels_per_em_26_6 == 0U || hint->y_pixels_per_em_26_6 == 0U ||
        hint->x_pixels_per_em_26_6 > static_cast<std::uint32_t>(std::numeric_limits<std::int32_t>::max()) ||
        hint->y_pixels_per_em_26_6 > static_cast<std::uint32_t>(std::numeric_limits<std::int32_t>::max()) ||
        hint->x_phase_26_6 >= 64U || hint->y_phase_26_6 >= 64U ||
        (hint->interpreter != 35U && hint->interpreter != 40U) || hint->variation_count > 65535U ||
        !valid_hinted_buffer(axes, hint->variation_count) || shape->struct_size < sizeof(*shape) ||
        !valid_hinted_buffer(reinterpret_cast<const std::byte*>(shape), shape->struct_size) ||
        shape->font_data != nullptr || shape->font_size != 0U || shape->face_index != 0U ||
        shape->normalization_data != nullptr || shape->normalization_data_size != 0U ||
        !valid_hinted_buffer(shape->input, shape->input_count) ||
        !valid_hinted_buffer(shape->pre_context, shape->pre_context_count) ||
        !valid_hinted_buffer(shape->post_context, shape->post_context_count) ||
        !valid_hinted_buffer(shape->features, shape->feature_count) ||
        !valid_hinted_buffer(shape->normalized_coordinates, shape->normalized_coordinate_count)) return false;
    return valid_request(shape, false);
}

bool hinted_run_publication_aliases_inputs(const void* output, std::uint64_t bytes,
    const progpu_native_hinted_font_request& hint, const std::int32_t* axes,
    const progpu_native_text_shape_request& shape) noexcept {
    return byte_ranges_overlap(output, bytes, &hint, sizeof(hint)) ||
        byte_ranges_overlap(output, bytes, axes, static_cast<std::uint64_t>(hint.variation_count) * sizeof(*axes)) ||
        byte_ranges_overlap(output, bytes, &shape, shape.struct_size) ||
        byte_ranges_overlap(output, bytes, shape.input, static_cast<std::uint64_t>(shape.input_count) * sizeof(*shape.input)) ||
        byte_ranges_overlap(output, bytes, shape.pre_context, static_cast<std::uint64_t>(shape.pre_context_count) * sizeof(*shape.pre_context)) ||
        byte_ranges_overlap(output, bytes, shape.post_context, static_cast<std::uint64_t>(shape.post_context_count) * sizeof(*shape.post_context)) ||
        byte_ranges_overlap(output, bytes, shape.features, static_cast<std::uint64_t>(shape.feature_count) * sizeof(*shape.features)) ||
        byte_ranges_overlap(output, bytes, shape.normalized_coordinates,
            static_cast<std::uint64_t>(shape.normalized_coordinate_count) * sizeof(*shape.normalized_coordinates));
}

constexpr std::uint32_t digit_scalar_mask =
    PROGPU_NATIVE_TEXT_DIGIT_SUBSTITUTION_SCALAR_MASK;
constexpr std::uint32_t contextual_digit_flag =
    PROGPU_NATIVE_TEXT_DIGIT_SUBSTITUTION_CONTEXTUAL;
constexpr std::uint32_t source_bidi_digit_flag =
    PROGPU_NATIVE_TEXT_DIGIT_SUBSTITUTION_SOURCE_BIDI;

bool valid_digit_substitution(std::uint32_t value) noexcept {
    if (value == 0U) return true;
    if ((value & ~(digit_scalar_mask | contextual_digit_flag | source_bidi_digit_flag)) != 0U) return false;
    const auto zero = value & digit_scalar_mask;
    if (zero == 0U || zero > 0x10FFF6U ||
        (zero <= 0xDFFFU && zero + 9U >= 0xD800U)) return false;
    for (std::uint32_t offset = 0U; offset < 10U; ++offset)
        if (get_unicode_decimal_digit_value(zero + offset) !=
            static_cast<std::int8_t>(offset)) return false;
    return true;
}

bool valid_number_symbols(const progpu_native_text_style_run& style) noexcept {
    return (style.percent == 0U || valid_scalar(style.percent)) &&
        (style.group_separator == 0U || valid_scalar(style.group_separator)) &&
        (style.decimal_separator == 0U || valid_scalar(style.decimal_separator));
}

bool has_number_substitution(
    const progpu_native_text_style_run* styles,
    std::uint32_t count) noexcept {
    for (std::uint32_t index = 0U; index < count; ++index)
        if (styles[index].digit_substitution != 0U || styles[index].percent != 0U ||
            styles[index].group_separator != 0U ||
            styles[index].decimal_separator != 0U) return true;
    return false;
}

bool preserve_source_digit_bidi(
    const progpu_native_text_style_run* styles,
    std::uint32_t count) noexcept {
    for (std::uint32_t index = 0U; index < count; ++index)
        if ((styles[index].digit_substitution & source_bidi_digit_flag) != 0U) return true;
    return false;
}

bool advance_digit_context(std::uint32_t code_point,
    bool initial_arabic_context, bool arabic_context) noexcept {
    // Hard segment boundaries reset the context independently of style runs.
    const auto line_break = get_unicode_line_break_class(code_point);
    if (line_break == unicode_line_break_class::mandatory ||
        line_break == unicode_line_break_class::carriage_return ||
        line_break == unicode_line_break_class::line_feed ||
        line_break == unicode_line_break_class::next_line)
        return initial_arabic_context;
    const auto bidi = get_unicode_bidi_class(code_point);
    if (bidi == unicode_bidi_class::arabic_letter) return true;
    if (bidi == unicode_bidi_class::left_to_right ||
        bidi == unicode_bidi_class::right_to_left) return false;
    return arabic_context;
}

template <typename Scalar>
void apply_number_substitution(
    std::span<Scalar> input,
    std::span<const progpu_native_text_style_run> styles,
    std::uint32_t paragraph_direction) noexcept {
    const bool initial_arabic_context = paragraph_direction ==
        PROGPU_NATIVE_TEXT_DIRECTION_RIGHT_TO_LEFT;
    bool arabic_context = initial_arabic_context;
    std::size_t style_index = 0U;
    for (std::size_t index = 0U; index < input.size(); ++index) {
        while (style_index + 1U < styles.size() &&
            index >= static_cast<std::size_t>(styles[style_index].scalar_start) +
                styles[style_index].scalar_count) ++style_index;
        auto& scalar = input[index];
        const auto original = scalar.code_point;
        if (!styles.empty()) {
            const auto& style = styles[style_index];
            const auto policy = style.digit_substitution;
            const auto zero = policy & digit_scalar_mask;
            const bool contextual = (policy & contextual_digit_flag) != 0U;
            const bool active = !contextual || arabic_context;
            std::uint32_t replacement = 0U;
            if (active && original >= 0x30U && original <= 0x39U && zero != 0U)
                replacement = zero + original - 0x30U;
            else if (active && original == '%') replacement = style.percent;
            else if (active && original == ',') replacement = style.group_separator;
            else if (active && original == '.') replacement = style.decimal_separator;
            if (replacement != 0U) {
                scalar.code_point = replacement;
                scalar.canonical_combining_class =
                    get_unicode_canonical_combining_class(scalar.code_point);
                if constexpr (std::is_same_v<Scalar, unicode_scalar>)
                    scalar.script = get_unicode_script(scalar.code_point);
                else
                    scalar.script = get_unicode_script(scalar.code_point).value;
            }
        }
        arabic_context = advance_digit_context(
            original, initial_arabic_context, arabic_context);
    }
}

bool valid_layout_request(
    const progpu_native_text_layout_request* request) noexcept {
    if (request == nullptr ||
        request->struct_size < sizeof(progpu_native_text_layout_request) ||
        request->abi_version != PROGPU_NATIVE_ABI_VERSION ||
        request->glyph_count != request->break_count ||
        !has_aligned_pointer(request->glyphs, request->glyph_count) ||
        !has_pointer(request->breaks_after, request->break_count) ||
        !std::isfinite(request->scale) || request->scale <= 0.0F ||
        !std::isfinite(request->maximum_width) ||
        request->maximum_width < 0.0F ||
        !std::isfinite(request->line_height) || request->line_height < 0.0F ||
        !std::isfinite(request->ellipsis_advance) ||
        request->ellipsis_advance < 0.0F ||
        (request->direction != PROGPU_NATIVE_TEXT_DIRECTION_LEFT_TO_RIGHT &&
            request->direction !=
                PROGPU_NATIVE_TEXT_DIRECTION_RIGHT_TO_LEFT) ||
        request->trimming > PROGPU_NATIVE_TEXT_TRIMMING_WORD_ELLIPSIS ||
        request->alignment > PROGPU_NATIVE_TEXT_ALIGNMENT_JUSTIFY ||
        request->reserved != 0U) {
        return false;
    }
    for (std::uint32_t index = 0U; index < request->glyph_count; ++index) {
        if (request->glyphs[index].flags > 0x07U ||
            request->breaks_after[index] >
                PROGPU_NATIVE_TEXT_LINE_BREAK_MANDATORY) {
            return false;
        }
    }
    return true;
}

bool valid_vertical_layout_request(
    const progpu_native_text_layout_request* request) noexcept {
    if (request == nullptr ||
        request->struct_size < sizeof(progpu_native_text_layout_request) ||
        request->abi_version != PROGPU_NATIVE_ABI_VERSION ||
        request->glyph_count != request->break_count ||
        !has_aligned_pointer(request->glyphs, request->glyph_count) ||
        !has_pointer(request->breaks_after, request->break_count) ||
        !std::isfinite(request->scale) || request->scale <= 0.0F ||
        !std::isfinite(request->maximum_width) ||
        request->maximum_width < 0.0F ||
        !std::isfinite(request->line_height) || request->line_height < 0.0F ||
        request->direction < PROGPU_NATIVE_TEXT_DIRECTION_TOP_TO_BOTTOM ||
        request->direction > PROGPU_NATIVE_TEXT_DIRECTION_BOTTOM_TO_TOP ||
        request->trimming != PROGPU_NATIVE_TEXT_TRIMMING_NONE ||
        request->alignment > PROGPU_NATIVE_TEXT_ALIGNMENT_JUSTIFY ||
        request->ellipsis_glyph_id != 0U ||
        request->ellipsis_advance != 0.0F || request->reserved != 0U) {
        return false;
    }
    for (std::uint32_t index = 0U; index < request->glyph_count; ++index) {
        if (request->glyphs[index].flags > 0x07U ||
            request->breaks_after[index] >
                PROGPU_NATIVE_TEXT_LINE_BREAK_MANDATORY) {
            return false;
        }
    }
    return true;
}

bool try_get_layout_scratch_bytes(
    std::uint32_t glyph_count,
    std::size_t& result) noexcept {
    if (glyph_count == 0U) {
        result = 0U;
        return true;
    }
    scratch_size_builder size{};
    if (!size.add<shaping_glyph>(glyph_count) ||
        !size.add<text_line_break_kind>(glyph_count) ||
        !size.add<shaping_glyph>(glyph_count) ||
        !size.add<positioned_text_glyph>(glyph_count) ||
        !size.add<positioned_text_line>(glyph_count)) {
        return false;
    }
    if (size.size() > std::numeric_limits<std::size_t>::max() -
            (alignof(std::max_align_t) - 1U)) {
        return false;
    }
    result = size.size() + alignof(std::max_align_t) - 1U;
    return true;
}

bool try_get_vertical_layout_scratch_bytes(
    std::uint32_t glyph_count,
    std::size_t& result) noexcept {
    if (glyph_count == 0U) {
        result = 0U;
        return true;
    }
    scratch_size_builder size{};
    if (!size.add<shaping_glyph>(glyph_count) ||
        !size.add<text_line_break_kind>(glyph_count) ||
        !size.add<positioned_text_glyph>(glyph_count) ||
        !size.add<positioned_text_column>(glyph_count)) {
        return false;
    }
    if (size.size() > std::numeric_limits<std::size_t>::max() -
            (alignof(std::max_align_t) - 1U)) {
        return false;
    }
    result = size.size() + alignof(std::max_align_t) - 1U;
    return true;
}

bool try_get_line_break_scratch_bytes(
    std::uint32_t input_count,
    std::size_t& result) noexcept {
    if (input_count == 0U) {
        result = 0U;
        return true;
    }
    scratch_size_builder size{};
    if (!size.add<unicode_scalar>(input_count) ||
        !size.add<unicode_line_break_class>(input_count) ||
        !size.add<text_line_break_kind>(input_count)) {
        return false;
    }
    if (size.size() > std::numeric_limits<std::size_t>::max() -
            (alignof(std::max_align_t) - 1U)) {
        return false;
    }
    result = size.size() + alignof(std::max_align_t) - 1U;
    return true;
}

bool try_get_bidi_scratch_bytes(
    std::uint32_t input_count,
    std::size_t& result) noexcept {
    if (input_count == 0U) {
        result = 0U;
        return true;
    }
    if (input_count > std::numeric_limits<std::uint32_t>::max() / 4U) {
        return false;
    }
    scratch_size_builder size{};
    if (!size.add<unicode_scalar>(input_count) ||
        !size.add<unicode_bidi_unit>(input_count) ||
        !size.add<std::uint32_t>(static_cast<std::size_t>(input_count) * 4U) ||
        !size.add<unicode_bidi_level_run>(input_count) ||
        !size.add<unicode_bidi_bracket_pair>(input_count / 2U) ||
        !size.add<unicode_bidi_level>(input_count)) {
        return false;
    }
    if (size.size() > std::numeric_limits<std::size_t>::max() -
            (alignof(std::max_align_t) - 1U)) {
        return false;
    }
    result = size.size() + alignof(std::max_align_t) - 1U;
    return true;
}

progpu_native_status status_from_unicode_error(unicode_error error) noexcept {
    switch (error) {
        case unicode_error::none:
            return PROGPU_NATIVE_STATUS_SUCCESS;
        case unicode_error::invalid_argument:
        case unicode_error::invalid_encoding:
        case unicode_error::insufficient_buffer:
            return PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
    }
    return PROGPU_NATIVE_STATUS_INTERNAL_ERROR;
}

bool valid_paragraph_layout_options(
    const progpu_native_text_layout_options* options) noexcept {
    return options != nullptr &&
        options->struct_size >= sizeof(progpu_native_text_layout_options) &&
        std::isfinite(options->scale) && options->scale > 0.0F &&
        std::isfinite(options->maximum_width) &&
        options->maximum_width >= 0.0F &&
        std::isfinite(options->line_height) &&
        options->line_height >= 0.0F &&
        options->direction <= PROGPU_NATIVE_TEXT_DIRECTION_RIGHT_TO_LEFT &&
        options->trimming <= PROGPU_NATIVE_TEXT_TRIMMING_WORD_ELLIPSIS &&
        options->alignment <= PROGPU_NATIVE_TEXT_ALIGNMENT_JUSTIFY &&
        std::isfinite(options->ellipsis_advance) &&
        options->ellipsis_advance >= 0.0F &&
        options->reserved0 == 0U && options->reserved1 == 0U;
}

text_layout_options convert_paragraph_layout_options(
    const progpu_native_text_layout_options& source,
    std::int8_t paragraph_level) noexcept {
    return text_layout_options{
        source.scale,
        source.maximum_width,
        source.line_height,
        source.maximum_lines,
        paragraph_level == 1 ? shaping_direction::right_to_left
                             : shaping_direction::left_to_right,
        static_cast<text_trimming>(source.trimming),
        static_cast<text_alignment>(source.alignment),
        0U,
        source.ellipsis_glyph_id,
        source.ellipsis_advance};
}

struct paragraph_capacities final {
    shape_capacities shaping{};
    std::size_t scratch_bytes = 0U;
};

bool try_build_paragraph_capacities(
    const progpu_native_text_shape_request& request,
    const progpu_native_text_context& context,
    paragraph_capacities& result,
    font_error& error, bool measured = false, bool excluded = false,
    std::uint32_t exclusion_count = 0U, bool substitute_numbers = false) noexcept {
    result = {};
    if (request.input_count == 0U) {
        error = font_error::none;
        return true;
    }
    if (request.input_count > std::numeric_limits<std::uint32_t>::max() / 4U) {
        error = font_error::invalid_argument;
        return false;
    }
    const auto normalization = context.has_normalization
        ? &context.normalization
        : nullptr;
    auto merge = [](shape_capacities& target,
                     const shape_capacities& source) noexcept {
        target.glyphs = std::max(target.glyphs, source.glyphs);
        target.normalization_scalars = std::max(
            target.normalization_scalars, source.normalization_scalars);
        target.graphemes = std::max(target.graphemes, source.graphemes);
        target.gsub_lookups = std::max(target.gsub_lookups, source.gsub_lookups);
        target.gpos_lookups = std::max(target.gpos_lookups, source.gpos_lookups);
        target.script_actions =
            std::max(target.script_actions, source.script_actions);
        target.complex_values =
            std::max(target.complex_values, source.complex_values);
        target.complex_indices =
            std::max(target.complex_indices, source.complex_indices);
        target.verification_glyphs =
            std::max(target.verification_glyphs, source.verification_glyphs);
        target.base_features =
            std::max(target.base_features, source.base_features);
        target.explicit_features =
            std::max(target.explicit_features, source.explicit_features);
        target.requested_features =
            std::max(target.requested_features, source.requested_features);
        target.feature_settings =
            std::max(target.feature_settings, source.feature_settings);
        target.variation_region_scalars = std::max(
            target.variation_region_scalars,
            source.variation_region_scalars);
    };
    for (std::size_t index = 0U; index < context.font_count(); ++index) {
        const auto* font = context.font_at(index);
        shape_capacities candidate{};
        if (font == nullptr || !try_build_capacities(
                request, *font, normalization, candidate, error)) {
            if (error == font_error::none) error = font_error::invalid_argument;
            return false;
        }
        merge(result.shaping, candidate);
    }
    result.shaping.complex_values = result.shaping.glyphs;
    if (result.shaping.glyphs == std::numeric_limits<std::uint32_t>::max()) {
        error = font_error::invalid_argument;
        return false;
    }
    result.shaping.complex_indices = result.shaping.glyphs + 1U;
    auto scratch_request = request;
    scratch_request.pre_context_count = std::max(
        request.pre_context_count, request.input_count);
    scratch_request.post_context_count = std::max(
        request.post_context_count, request.input_count);
    if (!try_compute_shape_scratch_bytes(
            scratch_request, result.shaping, error)) {
        return false;
    }
    const std::size_t input_count = request.input_count;
    const std::size_t glyph_count = result.shaping.glyphs;
    scratch_size_builder size{};
    if (!size.add<std::byte>(result.shaping.scratch_bytes) ||
        !size.add<progpu_native_text_shaping_glyph>(glyph_count) ||
        (substitute_numbers && !size.add<progpu_native_text_scalar>(input_count)) ||
        !size.add<unicode_scalar>(input_count) ||
        !size.add<unicode_bidi_unit>(input_count) ||
        !size.add<std::uint32_t>(input_count * 4U) ||
        !size.add<unicode_bidi_level_run>(input_count) ||
        !size.add<unicode_bidi_bracket_pair>(input_count / 2U) ||
        !size.add<unicode_bidi_level>(input_count) ||
        !size.add<unicode_script_run>(input_count) ||
        !size.add<unicode_grapheme_cluster>(input_count) ||
        !size.add<font_fallback_candidate>(context.font_count()) ||
        !size.add<font_fallback_run>(input_count) ||
        !size.add<unicode_line_break_class>(input_count) ||
        !size.add<text_line_break_kind>(input_count) ||
        !size.add<shaping_glyph>(glyph_count) ||
        !size.add<std::int8_t>(glyph_count) ||
        !size.add<float>(glyph_count) ||
        !size.add<float>(glyph_count) ||
        !size.add<text_justification_class>(glyph_count) ||
        !size.add<std::uint32_t>(glyph_count) ||
        !size.add<text_line_break_kind>(glyph_count) ||
        !size.add<text_visual_cluster_group>(glyph_count) ||
        !size.add<std::uint32_t>(glyph_count) ||
        !size.add<positioned_text_glyph>(glyph_count) ||
        !size.add<positioned_text_line>(glyph_count) ||
        (measured && !size.add<text_item_metrics>(glyph_count)) ||
        (excluded && (!size.add<text_exclusion_rectangle>(exclusion_count) ||
            !size.add<text_line_interval>(exclusion_count) ||
            !size.add<text_line_interval>(static_cast<std::size_t>(exclusion_count) + 1U) ||
            !size.add<text_line_fragment>(static_cast<std::size_t>(exclusion_count) + 1U) ||
            !size.add<text_fragment_placement>(glyph_count)))) {
        error = font_error::invalid_argument;
        return false;
    }
    if (size.size() > std::numeric_limits<std::size_t>::max() -
            (alignof(std::max_align_t) - 1U)) {
        error = font_error::invalid_argument;
        return false;
    }
    result.scratch_bytes = size.size() + alignof(std::max_align_t) - 1U;
    error = font_error::none;
    return true;
}

shaping_glyph convert_wire_glyph(
    const progpu_native_text_shaping_glyph& source) noexcept {
    return shaping_glyph{
        source.glyph_id,
        source.code_point,
        source.cluster,
        static_cast<shaping_glyph_flags>(source.flags),
        source.advance_x,
        source.advance_y,
        source.offset_x,
        source.offset_y};
}

bool append_logical_bidi_run(
    std::span<const progpu_native_text_shaping_glyph> run,
    std::int8_t level,
    std::uint32_t font_index,
    std::span<shaping_glyph> output,
    std::span<std::int8_t> levels,
    std::span<std::uint32_t> font_indices,
    std::span<float> scales,
    float scale,
    std::uint32_t& written,
    std::span<hinted_paragraph_glyph_owner> owners = {},
    const hinted_shaped_run* retained_run = nullptr,
    std::uint32_t retained_run_index = 0U) noexcept {
    if (font_indices.size() < output.size() || scales.size() < output.size() ||
        (retained_run != nullptr && (owners.size() < output.size() ||
            retained_run->descriptor_indices.size() != run.size())) ||
        run.size() > output.size() -
            std::min<std::size_t>(written, output.size())) {
        return false;
    }
    auto append_group = [&](std::size_t start, std::size_t end) noexcept {
        for (std::size_t index = start; index < end; ++index) {
            output[written] = convert_wire_glyph(run[index]);
            levels[written] = level;
            font_indices[written] = font_index;
            scales[written] = scale;
            if (retained_run != nullptr)
                owners[written] = {retained_run_index, static_cast<std::uint32_t>(index),
                    retained_run->descriptor_indices[index]};
            ++written;
        }
    };
    if ((level & 1) == 0) {
        append_group(0U, run.size());
        return true;
    }
    std::size_t end = run.size();
    while (end != 0U) {
        const std::int32_t cluster = run[end - 1U].cluster;
        std::size_t start = end - 1U;
        while (start != 0U && run[start - 1U].cluster == cluster) --start;
        append_group(start, end);
        end = start;
    }
    return true;
}

text_layout_options convert_layout_options(
    const progpu_native_text_layout_request& request) noexcept {
    return text_layout_options{
        request.scale,
        request.maximum_width,
        request.line_height,
        request.maximum_lines,
        static_cast<shaping_direction>(request.direction),
        static_cast<text_trimming>(request.trimming),
        static_cast<text_alignment>(request.alignment),
        0U,
        request.ellipsis_glyph_id,
        request.ellipsis_advance};
}

void copy_layout_inputs(
    const progpu_native_text_layout_request& request,
    std::span<shaping_glyph> glyphs,
    std::span<text_line_break_kind> breaks) noexcept {
    for (std::size_t index = 0U; index < glyphs.size(); ++index) {
        const auto& source = request.glyphs[index];
        glyphs[index] = shaping_glyph{
            source.glyph_id,
            source.code_point,
            source.cluster,
            static_cast<shaping_glyph_flags>(source.flags),
            source.advance_x,
            source.advance_y,
            source.offset_x,
            source.offset_y};
        breaks[index] =
            static_cast<text_line_break_kind>(request.breaks_after[index]);
    }
}

progpu_native_status shape_core(
    const progpu_native_text_shape_request& request,
    const sfnt_font_view& font,
    const unicode_normalization_data* normalization,
    const shape_capacities& capacities,
    progpu_native_text_shaping_glyph* glyphs,
    std::uint32_t glyph_capacity,
    void* scratch,
    std::size_t scratch_size,
    progpu_native_text_shape_result& result,
    progpu_native_text_context* context) {
    if ((capacities.glyphs != 0U && glyphs == nullptr) ||
        glyph_capacity < capacities.glyphs ||
        (capacities.scratch_bytes != 0U && scratch == nullptr) ||
        scratch_size < capacities.scratch_bytes) {
        result.error_code =
            static_cast<std::uint32_t>(font_error::insufficient_buffer);
        return PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
    }
    scratch_arena arena{scratch, scratch_size};
    std::span<unicode_scalar> input{};
    std::span<unicode_scalar> pre_context{};
    std::span<unicode_scalar> post_context{};
    std::span<shaping_feature> features{};
    std::span<open_type_feature_setting> base_features{};
    std::span<open_type_tag> explicit_features{};
    std::span<open_type_tag> requested_features{};
    std::span<shaping_feature> feature_settings{};
    std::span<shaping_glyph> native_glyphs{};
    std::span<unicode_grapheme_cluster> graphemes{};
    std::span<std::uint16_t> gsub_lookups{};
    std::span<std::uint16_t> gpos_lookups{};
    std::span<shaping_attachment> attachments{};
    std::span<std::uint8_t> attachment_states{};
    std::span<open_type_arabic_action> arabic_actions{};
    std::span<shaping_glyph_flags> arabic_flags{};
    std::span<std::uint8_t> script_categories{};
    std::span<std::uint8_t> script_syllables{};
    std::span<std::uint32_t> script_indices{};
    std::span<arabic_stretch_run> arabic_stretch_runs{};
    std::span<shaping_glyph> verification_glyphs{};
    std::span<unicode_scalar> normalization_scalars{};
    std::span<float> variation_region_scalars{};
    if (!arena.take(request.input_count, input) ||
        !arena.take(request.pre_context_count, pre_context) ||
        !arena.take(request.post_context_count, post_context) ||
        !arena.take(request.feature_count, features) ||
        !arena.take(capacities.base_features, base_features) ||
        !arena.take(capacities.explicit_features, explicit_features) ||
        !arena.take(capacities.requested_features, requested_features) ||
        !arena.take(capacities.feature_settings, feature_settings) ||
        !arena.take(capacities.glyphs, native_glyphs) ||
        !arena.take(capacities.graphemes, graphemes) ||
        !arena.take(capacities.gsub_lookups, gsub_lookups) ||
        !arena.take(capacities.gpos_lookups, gpos_lookups) ||
        !arena.take(capacities.glyphs, attachments) ||
        !arena.take(capacities.glyphs, attachment_states) ||
        !arena.take(capacities.script_actions, arabic_actions) ||
        !arena.take(capacities.script_actions, arabic_flags) ||
        !arena.take(capacities.complex_values, script_categories) ||
        !arena.take(capacities.complex_values, script_syllables) ||
        !arena.take(capacities.complex_indices, script_indices) ||
        !arena.take(capacities.glyphs, arabic_stretch_runs) ||
        !arena.take(capacities.verification_glyphs, verification_glyphs) ||
        !arena.take(capacities.normalization_scalars, normalization_scalars) ||
        !arena.take(
            capacities.variation_region_scalars,
            variation_region_scalars)) {
        result.error_code =
            static_cast<std::uint32_t>(font_error::insufficient_buffer);
        return PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
    }
    copy_scalars(request.input, input);
    copy_scalars(request.pre_context, pre_context);
    copy_scalars(request.post_context, post_context);
    for (std::size_t index = 0U; index < features.size(); ++index) {
        const auto& source = request.features[index];
        features[index] = shaping_feature{
            open_type_tag{source.tag}, source.value, source.start, source.end};
    }

    const open_type_shape_configuration_request configuration_request{
        open_type_tag{request.unicode_script},
        {},
        static_cast<shaping_direction>(request.direction),
        features,
        std::span<const std::int16_t>{
            request.normalized_coordinates,
            request.normalized_coordinate_count},
        request.alternate_value,
        (request.flags & PROGPU_NATIVE_TEXT_SHAPE_ZERO_MARK_ADVANCES) != 0U,
        static_cast<shaping_cluster_level>(request.cluster_level),
        static_cast<shaping_buffer_flags>(request.buffer_flags),
        normalization,
        pre_context,
        post_context,
        open_type_tag{request.language}};
    open_type_shape_configuration configuration{};
    font_error error = font_error::none;
    if (!try_prepare_open_type_shape_configuration(
            font,
            input,
            configuration_request,
            base_features,
            explicit_features,
            requested_features,
            feature_settings,
            configuration,
            &error)) {
        result.error_code = static_cast<std::uint32_t>(error);
        return status_from_error(error);
    }
    const open_type_shape_plan* plan = nullptr;
    if (context != nullptr) {
        if (!context->try_get_plan(
                font, configuration.options, plan, error)) {
            result.error_code = static_cast<std::uint32_t>(error);
            return status_from_error(error);
        }
    }

    open_type_shape_verification_scratch verification{verification_glyphs};
    open_type_shape_run_scratch shaping_scratch{
        graphemes,
        gsub_lookups,
        gpos_lookups,
        attachments,
        attachment_states,
        arabic_actions,
        script_categories,
        script_syllables,
        script_indices,
        arabic_stretch_runs,
        nullptr,
        verification_glyphs.empty() ? nullptr : &verification,
        arabic_flags,
        normalization_scalars,
        variation_region_scalars};
    std::uint32_t written = 0U;
    if (!try_shape_open_type_run(
            font,
            input,
            configuration.options,
            native_glyphs,
            shaping_scratch,
            written,
            &error,
            plan)) {
        result.error_code = static_cast<std::uint32_t>(error);
        return status_from_error(error);
    }
    for (std::uint32_t index = 0U; index < written; ++index) {
        const auto& source = native_glyphs[index];
        // The shaping engine retains OpenType Y-up design units internally.
        // The stable C ABI is the .NET/WebScene substitution boundary and
        // publishes the same Y-down design-unit convention as managed
        // ShapedGlyph at fontSize == unitsPerEm. Horizontal/vertical layout
        // therefore consumes the returned records without a second transform.
        glyphs[index] = progpu_native_text_shaping_glyph{
            source.glyph_id,
            source.code_point,
            source.cluster,
            static_cast<std::uint32_t>(source.flags),
            source.advance_x,
            negate_managed_metric(source.advance_y),
            source.offset_x,
            negate_managed_metric(source.offset_y)};
    }
    result.glyph_count = written;
    result.error_code = static_cast<std::uint32_t>(font_error::none);
    result.scratch_bytes_used = arena.used();
    return PROGPU_NATIVE_STATUS_SUCCESS;
}

// Canonical explicit hinted request planning, shared by the additive C owner
// and the original paragraph producer's private opt-in. No second planner.
progpu_native_status shape_hinted_request_generation(
    progpu_native_text_context* context, const progpu_native_hinted_font_request& hint,
    const std::int32_t* variation_coordinates, const progpu_native_text_shape_request& request,
    std::shared_ptr<const hinted_shaped_run>& generation, bool retain_positioning = false) {
#if !defined(PROGPU_NATIVE_FONT_HINTING)
    (void)context; (void)hint; (void)variation_coordinates; (void)request; (void)generation; (void)retain_positioning;
    return PROGPU_NATIVE_STATUS_UNSUPPORTED;
#else
    const auto& font = *context->font_at(hint.font_index);
    std::vector<unicode_scalar> input(request.input_count), pre(request.pre_context_count), post(request.post_context_count);
    copy_scalars(request.input, input); copy_scalars(request.pre_context, pre); copy_scalars(request.post_context, post);
    std::vector<shaping_feature> features(request.feature_count);
    for (std::size_t index = 0U; index < features.size(); ++index) {
        const auto& feature = request.features[index];
        features[index] = {open_type_tag{feature.tag}, feature.value, feature.start, feature.end};
    }
    std::vector<std::int16_t> normalized(request.normalized_coordinate_count);
    if (!normalized.empty()) std::copy_n(request.normalized_coordinates, normalized.size(), normalized.begin());
    std::vector<std::int32_t> axes(hint.variation_count);
    if (!axes.empty()) std::copy_n(variation_coordinates, axes.size(), axes.begin());
    const open_type_shape_configuration_request configuration_request{
        open_type_tag{request.unicode_script}, {}, static_cast<shaping_direction>(request.direction), features,
        normalized, request.alternate_value, (request.flags & PROGPU_NATIVE_TEXT_SHAPE_ZERO_MARK_ADVANCES) != 0U,
        static_cast<shaping_cluster_level>(request.cluster_level), static_cast<shaping_buffer_flags>(request.buffer_flags),
        context->has_normalization ? &context->normalization : nullptr, pre, post, open_type_tag{request.language}};
    open_type_shape_configuration_requirements needs{};
    font_error error = font_error::none;
    if (!try_get_open_type_shape_configuration_requirements(font, input, configuration_request, needs, &error)) return status_from_error(error);
    std::vector<open_type_feature_setting> base(needs.base_feature_capacity);
    std::vector<open_type_tag> explicit_features(needs.explicit_feature_capacity), requested_features(needs.requested_feature_capacity);
    std::vector<shaping_feature> settings(needs.feature_setting_capacity);
    open_type_shape_configuration configuration{};
    if (!try_prepare_open_type_shape_configuration(font, input, configuration_request, base, explicit_features,
        requested_features, settings, configuration, &error)) return status_from_error(error);
    const open_type_shape_plan* plan = nullptr;
    if (!context->try_get_plan(font, configuration.options, plan, error)) return status_from_error(error);
    const hinted_font_configuration hinted_configuration{hint.x_pixels_per_em_26_6, hint.y_pixels_per_em_26_6,
        static_cast<font_hint_policy>(hint.interpreter), hint.x_phase_26_6, hint.y_phase_26_6, axes};
    std::shared_ptr<const hinted_shaped_run> candidate{};
    hinted_shape_error failure{};
    if (!try_shape_context_hinted(context, hint.font_index, hinted_configuration, input, configuration.options,
        candidate, failure, hinted_projection_policy::automatic, plan, retain_positioning)) {
        if (failure.resource_exhausted) return PROGPU_NATIVE_STATUS_OUT_OF_MEMORY;
        if (failure.capture != hinted_font_error::none) return hinted_status(failure.capture);
        if (failure.projection == hinted_projection_error::unsupported_policy ||
            failure.projection == hinted_projection_error::unsupported_frame) return PROGPU_NATIVE_STATUS_UNSUPPORTED;
        if (failure.projection != hinted_projection_error::none) return PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
        return failure.shaping == font_error::none ? PROGPU_NATIVE_STATUS_INTERNAL_ERROR : status_from_error(failure.shaping);
    }
    const progpu_native_hinted_run handle{candidate};
    if (!valid_hinted_run(&handle) || !hinted_run_c_metrics_representable(*candidate))
        return PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
    generation = std::move(candidate);
    return PROGPU_NATIVE_STATUS_SUCCESS;
#endif
}

} // namespace

extern "C" {

static progpu_native_status resolve_digit_context(
    const std::uint16_t* text, std::uint32_t text_length,
    std::uint8_t initial_arabic_context, std::uint8_t* substitution_context,
    std::uint32_t context_capacity, std::uint8_t* final_arabic_context,
    bool include_graphemes, std::uint8_t* grapheme_starts,
    std::uint32_t grapheme_capacity) {
    if (initial_arabic_context > 1U || final_arabic_context == nullptr ||
        context_capacity < text_length ||
        !has_aligned_pointer(text, text_length) ||
        !has_pointer(substitution_context, text_length) ||
        (include_graphemes && (grapheme_capacity < text_length ||
            !has_pointer(grapheme_starts, text_length))))
        return PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
    const auto text_bytes = static_cast<std::uint64_t>(text_length) * sizeof(std::uint16_t);
    if (byte_ranges_overlap(text, text_bytes, substitution_context, text_length) ||
        byte_ranges_overlap(text, text_bytes, final_arabic_context, 1U) ||
        byte_ranges_overlap(substitution_context, text_length, final_arabic_context, 1U) ||
        (include_graphemes &&
            (byte_ranges_overlap(text, text_bytes, grapheme_starts, text_length) ||
             byte_ranges_overlap(substitution_context, text_length, grapheme_starts, text_length) ||
             byte_ranges_overlap(final_arabic_context, 1U, grapheme_starts, text_length))))
        return PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
    unicode_decode_requirements requirements{};
    const std::span<const std::uint16_t> source{text, text_length};
    if (include_graphemes ? !try_get_utf16_grapheme_starts(source,
            std::span<std::uint8_t>{grapheme_starts, grapheme_capacity}) :
            !try_get_utf16_decode_requirements(source, requirements))
        return PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
    bool context = initial_arabic_context != 0U;
    for (std::uint32_t offset = 0U; offset < text_length;) {
        const auto start = offset;
        std::uint32_t code_point = text[offset++];
        if (code_point >= 0xD800U && code_point <= 0xDBFFU) {
            code_point = 0x10000U + ((code_point - 0xD800U) << 10U) +
                (text[offset++] - 0xDC00U);
        }
        context = advance_digit_context(code_point,
            initial_arabic_context != 0U, context);
        substitution_context[start] = static_cast<std::uint8_t>(context);
        if (offset - start == 2U)
            substitution_context[start + 1U] = static_cast<std::uint8_t>(context);
    }
    *final_arabic_context = static_cast<std::uint8_t>(context);
    return PROGPU_NATIVE_STATUS_SUCCESS;
}

progpu_native_status progpu_native_text_resolve_digit_context(
    const std::uint16_t* text, std::uint32_t text_length,
    std::uint8_t initial_arabic_context, std::uint8_t* substitution_context,
    std::uint32_t context_capacity, std::uint8_t* final_arabic_context) {
    return resolve_digit_context(text, text_length, initial_arabic_context,
        substitution_context, context_capacity, final_arabic_context,
        false, nullptr, 0U);
}

progpu_native_status progpu_native_text_resolve_digit_context_with_graphemes(
    const std::uint16_t* text, std::uint32_t text_length,
    std::uint8_t initial_arabic_context, std::uint8_t* substitution_context,
    std::uint32_t context_capacity, std::uint8_t* grapheme_starts,
    std::uint32_t grapheme_capacity, std::uint8_t* final_arabic_context) {
    return resolve_digit_context(text, text_length, initial_arabic_context,
        substitution_context, context_capacity, final_arabic_context,
        true, grapheme_starts, grapheme_capacity);
}

progpu_native_status progpu_native_text_resolve_language_tag(
    const char* language_utf8,
    std::size_t language_size,
    std::uint32_t* language_tag) {
    constexpr std::size_t maximum_language_size = 255U;
    if (language_tag == nullptr || language_size > maximum_language_size ||
        (language_utf8 == nullptr && language_size != 0U)) {
        return PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
    }
    const std::string_view language = language_size == 0U
        ? std::string_view{}
        : std::string_view{language_utf8, language_size};
    *language_tag = resolve_open_type_language_tag(language).value;
    return PROGPU_NATIVE_STATUS_SUCCESS;
}

progpu_native_status progpu_native_text_get_shape_requirements(
    const progpu_native_text_shape_request* request,
    progpu_native_text_shape_requirements* requirements) {
    if (requirements == nullptr ||
        requirements->struct_size <
            sizeof(progpu_native_text_shape_requirements)) {
        return PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
    }
    *requirements = {};
    requirements->struct_size = sizeof(*requirements);
    if (!valid_request(request)) {
        requirements->error_code =
            static_cast<std::uint32_t>(font_error::invalid_argument);
        return PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
    }
    font_error error = font_error::none;
    const auto font_bytes = std::span<const std::byte>{
        reinterpret_cast<const std::byte*>(request->font_data),
        request->font_size};
    sfnt_font_view font{};
    if (!sfnt_font_view::try_create(
            font_bytes, request->face_index, font, &error)) {
        requirements->error_code = static_cast<std::uint32_t>(error);
        return status_from_error(error);
    }
    unicode_normalization_data normalization{};
    const unicode_normalization_data* normalization_pointer = nullptr;
    if (!try_resolve_normalization_data(
            *request, normalization, normalization_pointer)) {
        requirements->error_code =
            static_cast<std::uint32_t>(font_error::invalid_argument);
        return PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
    }
    shape_capacities capacities{};
    if (!try_build_capacities(
            *request, font, normalization_pointer, capacities, error)) {
        requirements->error_code = static_cast<std::uint32_t>(error);
        return status_from_error(error);
    }
    requirements->glyph_capacity = capacities.glyphs;
    requirements->scratch_alignment = 1U;
    requirements->scratch_bytes = capacities.scratch_bytes;
    requirements->error_code = static_cast<std::uint32_t>(font_error::none);
    return PROGPU_NATIVE_STATUS_SUCCESS;
}

progpu_native_status progpu_native_text_context_create(
    std::uint32_t abi_version,
    const std::uint8_t* font_data,
    std::size_t font_size,
    std::uint32_t face_index,
    const std::uint8_t* normalization_data,
    std::size_t normalization_data_size,
    progpu_native_text_context** context) {
    if (context == nullptr) return PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
    *context = nullptr;
    if (abi_version != PROGPU_NATIVE_ABI_VERSION || font_data == nullptr ||
        font_size == 0U ||
        ((normalization_data == nullptr) !=
            (normalization_data_size == 0U))) {
        return PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
    }
    try {
        auto result = std::make_unique<progpu_native_text_context>();
        result->plans.reserve(progpu_native_text_context::plan_capacity);
        result->source = std::make_shared<const owned_font_source>(
            std::span<const std::byte>(reinterpret_cast<const std::byte*>(font_data), font_size), face_index);
        font_error error = font_error::none;
        if (!sfnt_font_view::try_create(
                result->source->bytes, result->source->face_index, result->font, &error)) {
            return status_from_error(error);
        }
        if (normalization_data_size != 0U) {
            result->normalization_bytes.assign(
                reinterpret_cast<const std::byte*>(normalization_data),
                reinterpret_cast<const std::byte*>(normalization_data) +
                    normalization_data_size);
            unicode_error unicode_result = unicode_error::none;
            if (!unicode_normalization_data::try_create(
                    result->normalization_bytes,
                    result->normalization,
                    &unicode_result)) {
                return PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
            }
            result->has_normalization = true;
        } else {
            const auto* normalization =
                get_default_unicode_normalization_data();
            if (normalization == nullptr) {
                return PROGPU_NATIVE_STATUS_INTERNAL_ERROR;
            }
            result->normalization = *normalization;
            result->has_normalization = true;
        }
        *context = result.release();
        return PROGPU_NATIVE_STATUS_SUCCESS;
    } catch (const std::bad_alloc&) {
        return PROGPU_NATIVE_STATUS_OUT_OF_MEMORY;
    } catch (...) {
        return PROGPU_NATIVE_STATUS_INTERNAL_ERROR;
    }
}

void progpu_native_text_context_destroy(progpu_native_text_context* context) {
    delete context;
}

progpu_native_status progpu_native_text_context_get_device_advances(
    progpu_native_text_context* context,
    std::uint32_t font_index,
    std::uint32_t pixels_per_em,
    const std::uint32_t* glyph_indices,
    std::uint32_t glyph_count,
    float* advances,
    std::uint32_t advance_capacity,
    std::uint32_t* available) {
    if (!has_aligned_pointer(available, 1U)) return PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
    const auto input_bytes = static_cast<std::uint64_t>(glyph_count) * sizeof(std::uint32_t);
    const auto output_bytes = static_cast<std::uint64_t>(advance_capacity) * sizeof(float);
    // An aliased status pointer cannot be cleared without modifying input or
    // an output tail. Reject overlaps before writing any caller memory.
    if (byte_ranges_overlap(glyph_indices, input_bytes, available, sizeof(*available)) ||
        byte_ranges_overlap(advances, output_bytes, available, sizeof(*available)) ||
        byte_ranges_overlap(glyph_indices, input_bytes, advances, output_bytes))
        return PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
    *available = 0U;
    const auto input_address = reinterpret_cast<std::uintptr_t>(glyph_indices);
    const auto output_address = reinterpret_cast<std::uintptr_t>(advances);
    if (context == nullptr || font_index >= context->font_count() ||
        pixels_per_em == 0U || pixels_per_em > std::numeric_limits<std::uint16_t>::max() ||
        advance_capacity < glyph_count ||
        !has_aligned_pointer(glyph_indices, glyph_count) ||
        !has_aligned_pointer(advances, advance_capacity) ||
        input_bytes > std::numeric_limits<std::size_t>::max() ||
        output_bytes > std::numeric_limits<std::size_t>::max() ||
        input_bytes > std::numeric_limits<std::uintptr_t>::max() - input_address ||
        output_bytes > std::numeric_limits<std::uintptr_t>::max() - output_address)
        return PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;

    // Validate IDs independently of optional-record availability. A missing
    // ppem must not turn an invalid glyph request into successful absence.
    std::uint16_t font_glyph_count = 0U;
    if (!context->font_at(font_index)->try_get_glyph_count(font_glyph_count) ||
        font_glyph_count == 0U) return PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
    std::size_t index = 0U;
    const auto count = static_cast<std::size_t>(glyph_count);
#if defined(__aarch64__) || defined(_M_ARM64)
    const auto limit = vdupq_n_u32(font_glyph_count);
    for (; count - index >= 4U; index += 4U) {
        if (vminvq_u32(vcltq_u32(vld1q_u32(glyph_indices + index), limit)) == 0U)
            return PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
    }
#elif defined(__SSE2__) || defined(_M_X64)
    const auto maximum = _mm_set1_epi32(static_cast<int>(font_glyph_count) - 1);
    for (; count - index >= 4U; index += 4U) {
        const auto values = _mm_loadu_si128(reinterpret_cast<const __m128i*>(glyph_indices + index));
        const auto invalid = _mm_or_si128(_mm_cmpgt_epi32(values, maximum), _mm_srai_epi32(values, 31));
        if (_mm_movemask_epi8(invalid) != 0) return PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
    }
#endif
    for (; index < count; ++index)
        if (glyph_indices[index] >= font_glyph_count) return PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;

    sfnt_horizontal_device_metrics metrics{};
    font_error error = font_error::none;
    if (!context->try_get_device_widths(font_index,
            static_cast<std::uint16_t>(pixels_per_em), metrics, error))
        return status_from_error(error);
    if (metrics.glyph_widths.empty()) return PROGPU_NATIVE_STATUS_SUCCESS;

    const auto width = [&](std::size_t offset) {
        return std::to_integer<std::uint32_t>(metrics.glyph_widths[glyph_indices[offset]]);
    };
    index = 0U;
    // Sparse byte gathers use scalar loads on NEON/SSE2; independent numeric
    // conversion and publication use four lanes. No font-unit rescaling occurs.
#if defined(__aarch64__) || defined(_M_ARM64)
    for (; count - index >= 4U; index += 4U) {
        const std::uint32_t values[4]{width(index), width(index + 1U), width(index + 2U), width(index + 3U)};
        vst1q_f32(advances + index, vcvtq_f32_u32(vld1q_u32(values)));
    }
#elif defined(__SSE2__) || defined(_M_X64)
    for (; count - index >= 4U; index += 4U) {
        const auto values = _mm_setr_epi32(static_cast<int>(width(index)), static_cast<int>(width(index + 1U)),
            static_cast<int>(width(index + 2U)), static_cast<int>(width(index + 3U)));
        _mm_storeu_ps(advances + index, _mm_cvtepi32_ps(values));
    }
#endif
    for (; index < count; ++index) advances[index] = static_cast<float>(width(index));
    *available = 1U;
    return PROGPU_NATIVE_STATUS_SUCCESS;
}

progpu_native_status progpu_native_text_context_add_fallback_font(
    progpu_native_text_context* context,
    const std::uint8_t* font_data,
    std::size_t font_size,
    std::uint32_t face_index,
    std::uint64_t identity,
    std::uint32_t* font_index) {
    if (font_index == nullptr) return PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
    *font_index = 0U;
    if (context == nullptr || font_data == nullptr || font_size == 0U ||
        context->fallback_fonts.size() >=
            std::numeric_limits<std::uint32_t>::max() - 1U) {
        return PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
    }
    try {
        progpu_native_text_owned_font owned{};
        owned.source = std::make_shared<const owned_font_source>(
            std::span<const std::byte>(reinterpret_cast<const std::byte*>(font_data), font_size), face_index);
        owned.identity = identity;
        font_error error = font_error::none;
        if (!sfnt_font_view::try_create(
                owned.source->bytes, owned.source->face_index, owned.font, &error)) {
            return status_from_error(error);
        }
        context->fallback_fonts.push_back(std::move(owned));
        *font_index =
            static_cast<std::uint32_t>(context->fallback_fonts.size());
        return PROGPU_NATIVE_STATUS_SUCCESS;
    } catch (const std::bad_alloc&) {
        return PROGPU_NATIVE_STATUS_OUT_OF_MEMORY;
    } catch (...) {
        return PROGPU_NATIVE_STATUS_INTERNAL_ERROR;
    }
}

progpu_native_status progpu_native_text_context_get_shape_requirements(
    progpu_native_text_context* context,
    const progpu_native_text_shape_request* request,
    progpu_native_text_shape_requirements* requirements) {
    if (requirements == nullptr ||
        requirements->struct_size <
            sizeof(progpu_native_text_shape_requirements)) {
        return PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
    }
    *requirements = {};
    requirements->struct_size = sizeof(*requirements);
    if (context == nullptr || !valid_request(request, false)) {
        requirements->error_code =
            static_cast<std::uint32_t>(font_error::invalid_argument);
        return PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
    }
    font_error error = font_error::none;
    shape_capacities capacities{};
    if (!try_build_capacities(
            *request,
            context->font,
            context->has_normalization ? &context->normalization : nullptr,
            capacities,
            error)) {
        requirements->error_code = static_cast<std::uint32_t>(error);
        return status_from_error(error);
    }
    requirements->glyph_capacity = capacities.glyphs;
    requirements->scratch_alignment = 1U;
    requirements->scratch_bytes = capacities.scratch_bytes;
    return PROGPU_NATIVE_STATUS_SUCCESS;
}

progpu_native_status progpu_native_text_context_shape(
    progpu_native_text_context* context,
    const progpu_native_text_shape_request* request,
    progpu_native_text_shaping_glyph* glyphs,
    std::uint32_t glyph_capacity,
    void* scratch,
    std::size_t scratch_size,
    progpu_native_text_shape_result* result) {
    if (result == nullptr ||
        result->struct_size < sizeof(progpu_native_text_shape_result)) {
        return PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
    }
    *result = {};
    result->struct_size = sizeof(*result);
    if (context == nullptr || !valid_request(request, false)) {
        result->error_code =
            static_cast<std::uint32_t>(font_error::invalid_argument);
        return PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
    }
    font_error error = font_error::none;
    shape_capacities capacities{};
    if (!try_build_capacities(
            *request,
            context->font,
            context->has_normalization ? &context->normalization : nullptr,
            capacities,
            error)) {
        result->error_code = static_cast<std::uint32_t>(error);
        return status_from_error(error);
    }
    try {
        return shape_core(
            *request,
            context->font,
            context->has_normalization ? &context->normalization : nullptr,
            capacities,
            glyphs,
            glyph_capacity,
            scratch,
            scratch_size,
            *result,
            context);
    } catch (const std::bad_alloc&) {
        result->error_code = static_cast<std::uint32_t>(error);
        return PROGPU_NATIVE_STATUS_OUT_OF_MEMORY;
    } catch (...) {
        result->error_code = static_cast<std::uint32_t>(error);
        return PROGPU_NATIVE_STATUS_INTERNAL_ERROR;
    }
}

progpu_native_status progpu_native_text_shape(
    const progpu_native_text_shape_request* request,
    progpu_native_text_shaping_glyph* glyphs,
    std::uint32_t glyph_capacity,
    void* scratch,
    std::size_t scratch_size,
    progpu_native_text_shape_result* result) {
    if (result == nullptr ||
        result->struct_size < sizeof(progpu_native_text_shape_result)) {
        return PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
    }
    *result = {};
    result->struct_size = sizeof(*result);
    if (!valid_request(request)) {
        result->error_code =
            static_cast<std::uint32_t>(font_error::invalid_argument);
        return PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
    }
    font_error error = font_error::none;
    const auto font_bytes = std::span<const std::byte>{
        reinterpret_cast<const std::byte*>(request->font_data),
        request->font_size};
    sfnt_font_view font{};
    if (!sfnt_font_view::try_create(
            font_bytes, request->face_index, font, &error)) {
        result->error_code = static_cast<std::uint32_t>(error);
        return status_from_error(error);
    }
    unicode_normalization_data normalization{};
    const unicode_normalization_data* normalization_pointer = nullptr;
    if (!try_resolve_normalization_data(
            *request, normalization, normalization_pointer)) {
        result->error_code =
            static_cast<std::uint32_t>(font_error::invalid_argument);
        return PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
    }
    shape_capacities capacities{};
    if (!try_build_capacities(
            *request, font, normalization_pointer, capacities, error)) {
        result->error_code = static_cast<std::uint32_t>(error);
        return status_from_error(error);
    }
    return shape_core(
        *request,
        font,
        normalization_pointer,
        capacities,
        glyphs,
        glyph_capacity,
        scratch,
        scratch_size,
        *result,
        nullptr);
}

progpu_native_status progpu_native_text_layout_get_requirements(
    const progpu_native_text_layout_request* request,
    progpu_native_text_layout_requirements* requirements) {
    if (requirements == nullptr ||
        requirements->struct_size <
            sizeof(progpu_native_text_layout_requirements)) {
        return PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
    }
    *requirements = {};
    requirements->struct_size = sizeof(*requirements);
    if (!valid_layout_request(request)) {
        requirements->error_code =
            static_cast<std::uint32_t>(font_error::invalid_argument);
        return PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
    }
    std::size_t scratch_bytes = 0U;
    if (!try_get_layout_scratch_bytes(request->glyph_count, scratch_bytes)) {
        requirements->error_code =
            static_cast<std::uint32_t>(font_error::invalid_argument);
        return PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
    }
    requirements->glyph_capacity = request->glyph_count;
    requirements->line_capacity = request->glyph_count;
    requirements->scratch_alignment = 1U;
    requirements->scratch_bytes = scratch_bytes;
    return PROGPU_NATIVE_STATUS_SUCCESS;
}

progpu_native_status progpu_native_text_layout(
    const progpu_native_text_layout_request* request,
    progpu_native_positioned_text_glyph* glyphs,
    std::uint32_t glyph_capacity,
    progpu_native_positioned_text_line* lines,
    std::uint32_t line_capacity,
    void* scratch,
    std::size_t scratch_size,
    progpu_native_text_layout_result* result) {
    if (result == nullptr ||
        result->struct_size < sizeof(progpu_native_text_layout_result)) {
        return PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
    }
    *result = {};
    result->struct_size = sizeof(*result);
    if (!valid_layout_request(request) ||
        (request->glyph_count != 0U &&
            (glyphs == nullptr || lines == nullptr || scratch == nullptr)) ||
        !has_aligned_pointer(glyphs, request->glyph_count) ||
        !has_aligned_pointer(lines, request->glyph_count) ||
        glyph_capacity < request->glyph_count ||
        line_capacity < request->glyph_count) {
        result->error_code =
            static_cast<std::uint32_t>(font_error::invalid_argument);
        return PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
    }
    if (request->glyph_count == 0U) {
        return PROGPU_NATIVE_STATUS_SUCCESS;
    }
    std::size_t required_scratch = 0U;
    if (!try_get_layout_scratch_bytes(
            request->glyph_count, required_scratch) ||
        scratch_size < required_scratch) {
        result->error_code =
            static_cast<std::uint32_t>(font_error::insufficient_buffer);
        return PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
    }
    scratch_arena arena{scratch, scratch_size};
    std::span<shaping_glyph> native_glyphs{};
    std::span<text_line_break_kind> native_breaks{};
    std::span<shaping_glyph> public_metric_scratch{};
    std::span<positioned_text_glyph> native_positioned{};
    std::span<positioned_text_line> native_lines{};
    if (!arena.take(request->glyph_count, native_glyphs) ||
        !arena.take(request->glyph_count, native_breaks) ||
        !arena.take(request->glyph_count, public_metric_scratch) ||
        !arena.take(request->glyph_count, native_positioned) ||
        !arena.take(request->glyph_count, native_lines)) {
        result->error_code =
            static_cast<std::uint32_t>(font_error::insufficient_buffer);
        return PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
    }
    copy_layout_inputs(*request, native_glyphs, native_breaks);
    std::uint32_t written_glyphs = 0U;
    std::uint32_t written_lines = 0U;
    font_error error = font_error::none;
    if (!try_layout_open_type_text(
            native_glyphs,
            native_breaks,
            convert_layout_options(*request),
            public_metric_scratch,
            native_positioned,
            native_lines,
            written_glyphs,
            written_lines,
            &error)) {
        result->error_code = static_cast<std::uint32_t>(error);
        return status_from_error(error);
    }
    for (std::uint32_t index = 0U; index < written_glyphs; ++index) {
        const auto& source = native_positioned[index];
        glyphs[index] = progpu_native_positioned_text_glyph{
            source.glyph_index,
            source.glyph_id,
            0U,
            source.cluster,
            source.x,
            source.y,
            source.advance_x,
            source.advance_y};
    }
    for (std::uint32_t index = 0U; index < written_lines; ++index) {
        const auto& source = native_lines[index];
        lines[index] = progpu_native_positioned_text_line{
            source.glyph_start,
            source.glyph_count,
            source.input_start,
            source.input_end,
            source.width,
            source.baseline_y,
            source.height,
            static_cast<std::uint8_t>(source.clipped ? 1U : 0U),
            source.flags,
            0U,
            0U};
    }
    text_layout_metrics metrics{};
    if (!try_measure_positioned_text_lines(
            native_lines.first(written_lines),
            request->maximum_width,
            metrics,
            &error)) {
        result->error_code = static_cast<std::uint32_t>(error);
        return status_from_error(error);
    }
    result->glyph_count = written_glyphs;
    result->line_count = written_lines;
    result->content_width = metrics.content_width;
    result->content_height = metrics.content_height;
    result->measured_width = metrics.measured_width;
    result->measured_height = metrics.measured_height;
    result->scratch_bytes_used = arena.used();
    return PROGPU_NATIVE_STATUS_SUCCESS;
}

progpu_native_status progpu_native_text_vertical_layout_get_requirements(
    const progpu_native_text_layout_request* request,
    progpu_native_text_vertical_layout_requirements* requirements) {
    if (requirements == nullptr || requirements->struct_size <
            sizeof(progpu_native_text_vertical_layout_requirements)) {
        return PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
    }
    *requirements = {};
    requirements->struct_size = sizeof(*requirements);
    if (!valid_vertical_layout_request(request)) {
        requirements->error_code =
            static_cast<std::uint32_t>(font_error::invalid_argument);
        return PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
    }
    std::size_t scratch_bytes = 0U;
    if (!try_get_vertical_layout_scratch_bytes(
            request->glyph_count, scratch_bytes)) {
        requirements->error_code =
            static_cast<std::uint32_t>(font_error::invalid_argument);
        return PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
    }
    requirements->glyph_capacity = request->glyph_count;
    requirements->column_capacity = request->glyph_count;
    requirements->scratch_alignment = 1U;
    requirements->scratch_bytes = scratch_bytes;
    return PROGPU_NATIVE_STATUS_SUCCESS;
}

progpu_native_status progpu_native_text_vertical_layout(
    const progpu_native_text_layout_request* request,
    progpu_native_positioned_text_glyph* glyphs,
    std::uint32_t glyph_capacity,
    progpu_native_positioned_text_column* columns,
    std::uint32_t column_capacity,
    void* scratch,
    std::size_t scratch_size,
    progpu_native_text_vertical_layout_result* result) {
    if (result == nullptr || result->struct_size <
            sizeof(progpu_native_text_vertical_layout_result)) {
        return PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
    }
    *result = {};
    result->struct_size = sizeof(*result);
    if (!valid_vertical_layout_request(request) ||
        (request->glyph_count != 0U &&
            (glyphs == nullptr || columns == nullptr || scratch == nullptr)) ||
        !has_aligned_pointer(glyphs, request->glyph_count) ||
        !has_aligned_pointer(columns, request->glyph_count) ||
        glyph_capacity < request->glyph_count ||
        column_capacity < request->glyph_count) {
        result->error_code =
            static_cast<std::uint32_t>(font_error::invalid_argument);
        return PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
    }
    if (request->glyph_count == 0U) return PROGPU_NATIVE_STATUS_SUCCESS;
    std::size_t required_scratch = 0U;
    if (!try_get_vertical_layout_scratch_bytes(
            request->glyph_count, required_scratch) ||
        scratch_size < required_scratch) {
        result->error_code =
            static_cast<std::uint32_t>(font_error::insufficient_buffer);
        return PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
    }

    scratch_arena arena{scratch, scratch_size};
    std::span<shaping_glyph> native_glyphs{};
    std::span<text_line_break_kind> native_breaks{};
    std::span<positioned_text_glyph> native_positioned{};
    std::span<positioned_text_column> native_columns{};
    if (!arena.take(request->glyph_count, native_glyphs) ||
        !arena.take(request->glyph_count, native_breaks) ||
        !arena.take(request->glyph_count, native_positioned) ||
        !arena.take(request->glyph_count, native_columns)) {
        result->error_code =
            static_cast<std::uint32_t>(font_error::insufficient_buffer);
        return PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
    }
    copy_layout_inputs(*request, native_glyphs, native_breaks);
    std::uint32_t written_glyphs = 0U;
    std::uint32_t written_columns = 0U;
    font_error error = font_error::none;
    if (!try_layout_vertical_shaped_text(
            native_glyphs,
            native_breaks,
            convert_layout_options(*request),
            native_positioned,
            native_columns,
            written_glyphs,
            written_columns,
            &error)) {
        result->error_code = static_cast<std::uint32_t>(error);
        return status_from_error(error);
    }
    for (std::uint32_t index = 0U; index < written_glyphs; ++index) {
        const auto& source = native_positioned[index];
        glyphs[index] = progpu_native_positioned_text_glyph{
            source.glyph_index,
            source.glyph_id,
            0U,
            source.cluster,
            source.x,
            source.y,
            source.advance_x,
            source.advance_y};
    }
    for (std::uint32_t index = 0U; index < written_columns; ++index) {
        const auto& source = native_columns[index];
        columns[index] = progpu_native_positioned_text_column{
            source.glyph_start,
            source.glyph_count,
            source.input_start,
            source.input_end,
            source.height,
            source.x,
            source.width,
            static_cast<std::uint8_t>(source.clipped ? 1U : 0U),
            0U,
            0U,
            0U};
    }
    text_layout_metrics metrics{};
    if (!try_measure_positioned_text_columns(
            native_columns.first(written_columns),
            request->maximum_width,
            metrics,
            &error)) {
        result->error_code = static_cast<std::uint32_t>(error);
        return status_from_error(error);
    }
    result->glyph_count = written_glyphs;
    result->column_count = written_columns;
    result->content_width = metrics.content_width;
    result->content_height = metrics.content_height;
    result->measured_width = metrics.measured_width;
    result->measured_height = metrics.measured_height;
    result->scratch_bytes_used = arena.used();
    return PROGPU_NATIVE_STATUS_SUCCESS;
}

progpu_native_status progpu_native_text_get_line_break_requirements(
    const progpu_native_text_scalar* input,
    std::uint32_t input_count,
    progpu_native_text_line_break_requirements* requirements) {
    if (requirements == nullptr ||
        requirements->struct_size <
            sizeof(progpu_native_text_line_break_requirements)) {
        return PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
    }
    *requirements = {};
    requirements->struct_size = sizeof(*requirements);
    if (!valid_wire_scalars(input, input_count)) {
        requirements->error_code =
            static_cast<std::uint32_t>(unicode_error::invalid_argument);
        return PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
    }
    std::size_t scratch_bytes = 0U;
    if (!try_get_line_break_scratch_bytes(input_count, scratch_bytes)) {
        requirements->error_code =
            static_cast<std::uint32_t>(unicode_error::invalid_argument);
        return PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
    }
    requirements->break_capacity = input_count;
    requirements->scratch_alignment = 1U;
    requirements->scratch_bytes = scratch_bytes;
    return PROGPU_NATIVE_STATUS_SUCCESS;
}

progpu_native_status progpu_native_text_resolve_line_breaks(
    const progpu_native_text_scalar* input,
    std::uint32_t input_count,
    std::uint8_t* breaks_after,
    std::uint32_t break_capacity,
    void* scratch,
    std::size_t scratch_size,
    progpu_native_text_line_break_result* result) {
    if (result == nullptr ||
        result->struct_size < sizeof(progpu_native_text_line_break_result)) {
        return PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
    }
    *result = {};
    result->struct_size = sizeof(*result);
    if (!valid_wire_scalars(input, input_count) ||
        break_capacity < input_count ||
        (input_count != 0U &&
            (breaks_after == nullptr || scratch == nullptr))) {
        result->error_code =
            static_cast<std::uint32_t>(unicode_error::invalid_argument);
        return PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
    }
    if (input_count == 0U) {
        return PROGPU_NATIVE_STATUS_SUCCESS;
    }
    std::size_t required_scratch = 0U;
    if (!try_get_line_break_scratch_bytes(input_count, required_scratch) ||
        scratch_size < required_scratch) {
        result->error_code =
            static_cast<std::uint32_t>(unicode_error::insufficient_buffer);
        return PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
    }
    scratch_arena arena{scratch, scratch_size};
    std::span<unicode_scalar> native_input{};
    std::span<unicode_line_break_class> classes{};
    std::span<text_line_break_kind> native_breaks{};
    if (!arena.take(input_count, native_input) ||
        !arena.take(input_count, classes) ||
        !arena.take(input_count, native_breaks)) {
        result->error_code =
            static_cast<std::uint32_t>(unicode_error::insufficient_buffer);
        return PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
    }
    copy_scalars(input, native_input);
    unicode_error error = unicode_error::none;
    if (!try_resolve_unicode_line_breaks(
            native_input, classes, native_breaks, &error)) {
        result->error_code = static_cast<std::uint32_t>(error);
        return status_from_unicode_error(error);
    }
    for (std::uint32_t index = 0U; index < input_count; ++index) {
        breaks_after[index] = static_cast<std::uint8_t>(native_breaks[index]);
    }
    result->break_count = input_count;
    result->scratch_bytes_used = arena.used();
    return PROGPU_NATIVE_STATUS_SUCCESS;
}

progpu_native_status progpu_native_text_get_bidi_requirements(
    const progpu_native_text_scalar* input,
    std::uint32_t input_count,
    progpu_native_text_bidi_requirements* requirements) {
    if (requirements == nullptr ||
        requirements->struct_size <
            sizeof(progpu_native_text_bidi_requirements)) {
        return PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
    }
    *requirements = {};
    requirements->struct_size = sizeof(*requirements);
    if (!valid_wire_scalars(input, input_count)) {
        requirements->error_code =
            static_cast<std::uint32_t>(unicode_error::invalid_argument);
        return PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
    }
    std::size_t scratch_bytes = 0U;
    if (!try_get_bidi_scratch_bytes(input_count, scratch_bytes)) {
        requirements->error_code =
            static_cast<std::uint32_t>(unicode_error::invalid_argument);
        return PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
    }
    requirements->level_capacity = input_count;
    requirements->scratch_alignment = 1U;
    requirements->scratch_bytes = scratch_bytes;
    return PROGPU_NATIVE_STATUS_SUCCESS;
}

static progpu_native_status resolve_bidi(
    const progpu_native_text_scalar* input,
    std::uint32_t input_count,
    std::int32_t requested_paragraph_level,
    const progpu_native_text_style_run* styles,
    std::uint32_t style_count,
    progpu_native_text_bidi_level* levels,
    std::uint32_t level_capacity,
    void* scratch,
    std::size_t scratch_size,
    progpu_native_text_bidi_result* result) {
    if (result == nullptr ||
        result->struct_size < sizeof(progpu_native_text_bidi_result)) {
        return PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
    }
    *result = {};
    result->struct_size = sizeof(*result);
    if (!valid_wire_scalars(input, input_count) ||
        requested_paragraph_level < -1 || requested_paragraph_level > 1 ||
        style_count > input_count || !has_aligned_pointer(styles, style_count) ||
        level_capacity < input_count ||
        !has_aligned_pointer(levels, input_count) ||
        (input_count != 0U && scratch == nullptr)) {
        result->error_code =
            static_cast<std::uint32_t>(unicode_error::invalid_argument);
        return PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
    }
    std::uint32_t expected_start = 0U;
    for (const auto& style : std::span(styles, style_count)) {
        if (style.scalar_start != expected_start || style.scalar_count == 0U ||
            style.scalar_count > input_count - expected_start ||
            !valid_digit_substitution(style.digit_substitution) ||
            !valid_number_symbols(style)) {
            result->error_code = static_cast<std::uint32_t>(unicode_error::invalid_argument);
            return PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
        }
        expected_start += style.scalar_count;
    }
    if (style_count != 0U && expected_start != input_count) {
        result->error_code = static_cast<std::uint32_t>(unicode_error::invalid_argument);
        return PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
    }
    result->paragraph_level = requested_paragraph_level == 1 ? 1 : 0;
    if (input_count == 0U) {
        return PROGPU_NATIVE_STATUS_SUCCESS;
    }
    std::size_t required_scratch = 0U;
    if (!try_get_bidi_scratch_bytes(input_count, required_scratch) ||
        scratch_size < required_scratch) {
        result->error_code =
            static_cast<std::uint32_t>(unicode_error::insufficient_buffer);
        return PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
    }
    scratch_arena arena{scratch, scratch_size};
    std::span<unicode_scalar> native_input{};
    std::span<unicode_bidi_unit> units{};
    std::span<std::uint32_t> indices{};
    std::span<unicode_bidi_level_run> runs{};
    std::span<unicode_bidi_bracket_pair> bracket_pairs{};
    std::span<unicode_bidi_level> native_levels{};
    if (!arena.take(input_count, native_input) ||
        !arena.take(input_count, units) ||
        !arena.take(static_cast<std::size_t>(input_count) * 4U, indices) ||
        !arena.take(input_count, runs) ||
        !arena.take(input_count / 2U, bracket_pairs) ||
        !arena.take(input_count, native_levels)) {
        result->error_code =
            static_cast<std::uint32_t>(unicode_error::insufficient_buffer);
        return PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
    }
    copy_scalars(input, native_input);
    if (has_number_substitution(styles, style_count) &&
        !preserve_source_digit_bidi(styles, style_count))
        apply_number_substitution(native_input, std::span(styles, style_count),
            requested_paragraph_level == 1 ? PROGPU_NATIVE_TEXT_DIRECTION_RIGHT_TO_LEFT :
            requested_paragraph_level == 0 ? PROGPU_NATIVE_TEXT_DIRECTION_LEFT_TO_RIGHT :
            PROGPU_NATIVE_TEXT_DIRECTION_UNSPECIFIED);
    unicode_bidi_scratch bidi_scratch{units, indices, runs, bracket_pairs};
    std::int8_t paragraph_level = 0;
    std::uint32_t written = 0U;
    unicode_error error = unicode_error::none;
    if (!try_resolve_unicode_bidi(
            native_input,
            static_cast<std::int8_t>(requested_paragraph_level),
            bidi_scratch,
            native_levels,
            paragraph_level,
            written,
            &error)) {
        result->error_code = static_cast<std::uint32_t>(error);
        return status_from_unicode_error(error);
    }
    for (std::uint32_t index = 0U; index < written; ++index) {
        const auto& source = native_levels[index];
        levels[index] = progpu_native_text_bidi_level{
            source.input_index,
            source.input_length,
            source.level,
            0U};
    }
    result->level_count = written;
    result->paragraph_level = paragraph_level;
    result->scratch_bytes_used = arena.used();
    return PROGPU_NATIVE_STATUS_SUCCESS;
}

progpu_native_status progpu_native_text_resolve_bidi(
    const progpu_native_text_scalar* input, std::uint32_t input_count,
    std::int32_t requested_paragraph_level,
    progpu_native_text_bidi_level* levels, std::uint32_t level_capacity,
    void* scratch, std::size_t scratch_size, progpu_native_text_bidi_result* result) {
    return resolve_bidi(input, input_count, requested_paragraph_level, nullptr, 0U,
        levels, level_capacity, scratch, scratch_size, result);
}

progpu_native_status progpu_native_text_resolve_styled_bidi(
    const progpu_native_text_scalar* input, std::uint32_t input_count,
    std::int32_t requested_paragraph_level,
    const progpu_native_text_style_run* styles, std::uint32_t style_count,
    progpu_native_text_bidi_level* levels, std::uint32_t level_capacity,
    void* scratch, std::size_t scratch_size, progpu_native_text_bidi_result* result) {
    if (!has_aligned_pointer(result, 1U)) return PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
    struct borrowed_range { const void* data; std::uint64_t size; };
    const borrowed_range ranges[]{
        {input, static_cast<std::uint64_t>(input_count) * sizeof(*input)},
        {styles, static_cast<std::uint64_t>(style_count) * sizeof(*styles)},
        {levels, static_cast<std::uint64_t>(level_capacity) * sizeof(*levels)},
        {scratch, scratch_size}, {result, sizeof(*result)}};
    for (std::size_t first = 0U; first < 5U; ++first)
        for (std::size_t second = first + 1U; second < 5U; ++second)
            if (byte_ranges_overlap(ranges[first].data, ranges[first].size,
                    ranges[second].data, ranges[second].size))
                return PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
    return resolve_bidi(input, input_count, requested_paragraph_level, styles, style_count,
        levels, level_capacity, scratch, scratch_size, result);
}

static bool valid_style_runs(const progpu_native_text_context& context,
    const progpu_native_text_shape_request& shaping,
    const progpu_native_text_style_run* styles, std::uint32_t count) noexcept {
    if (count == 0U) return true;
    if (count > shaping.input_count || !has_aligned_pointer(styles, count)) return false;
    std::uint32_t expected = 0U;
    for (std::uint32_t i = 0U; i < count; ++i) {
        const auto& style = styles[i];
        if (style.scalar_start != expected || style.scalar_count == 0U ||
            style.scalar_count > shaping.input_count - expected || style.font_index >= context.font_count() ||
            !std::isfinite(style.scale) || style.scale <= 0.0F ||
            !valid_digit_substitution(style.digit_substitution) ||
            !valid_number_symbols(style) ||
            style.feature_start > shaping.feature_count || style.feature_count > shaping.feature_count - style.feature_start)
            return false;
        expected += style.scalar_count;
    }
    return expected == shaping.input_count;
}

static bool valid_flow_options(const progpu_native_text_flow_options* flow,
    const progpu_native_text_layout_options* layout) noexcept {
    return flow == nullptr || (flow->struct_size >= sizeof(*flow) && flow->reserved == 0U &&
        std::isfinite(flow->incremental_tab) && flow->incremental_tab >= 0.0F &&
        std::isfinite(flow->tab_origin) && layout != nullptr);
}

struct inline_flow_policy {
    const progpu_native_text_style_metrics* metrics;
    const progpu_native_text_inline_object* objects;
    std::uint32_t object_count;
};

struct hinted_paragraph_policy {
    hinted_paragraph_generation* generation;
};

struct excluded_flow_policy {
    const progpu_native_text_exclusion_options* options;
    const progpu_native_text_exclusion_rectangle* rectangles;
    std::uint32_t count;
    progpu_native_text_fragment_placement* output;
    std::uint32_t capacity;
    double origin_y{};
};

struct floating_flow_policy {
    const progpu_native_text_floating_options* options;
    const progpu_native_text_floating_item* events;
    std::uint32_t count;
    progpu_native_text_floating_placement* output;
    std::uint32_t capacity;
    progpu_native_text_floating_result* result;
};

static bool valid_excluded_flow(const progpu_native_text_layout_options& layout,
    const excluded_flow_policy* policy) noexcept {
    if (policy == nullptr) return true;
    if (!std::isfinite(policy->origin_y) || policy->origin_y < 0 ||
        policy->origin_y > static_cast<double>(std::numeric_limits<float>::max()) ||
        layout.maximum_width <= 0 || layout.trimming != 0 || policy->options == nullptr ||
        policy->options->struct_size != sizeof(*policy->options) ||
        policy->options->reserved0 != 0 || policy->options->reserved1 != 0 ||
        policy->options->maximum_attempts == 0 || policy->options->maximum_attempts > (1U << 20U) ||
        policy->count > (1U << 20U) || !has_aligned_pointer(policy->rectangles, policy->count)) return false;
    for (const auto& rectangle : std::span(policy->rectangles, policy->count)) {
#if defined(__aarch64__) || defined(_M_ARM64)
        const float values[4]{rectangle.left, rectangle.top, rectangle.right, rectangle.bottom};
        const auto lanes = vabsq_f32(vld1q_f32(values));
        if (vminvq_u32(vcleq_f32(lanes, vdupq_n_f32(std::numeric_limits<float>::max()))) == 0U) return false;
#elif defined(__SSE2__) || defined(_M_X64)
        const auto lanes = _mm_setr_ps(rectangle.left, rectangle.top, rectangle.right, rectangle.bottom);
        const auto maximum = _mm_set1_ps(std::numeric_limits<float>::max());
        if (_mm_movemask_ps(_mm_and_ps(_mm_cmple_ps(lanes, maximum),
            _mm_cmpge_ps(lanes, _mm_sub_ps(_mm_setzero_ps(), maximum)))) != 15) return false;
#else
        if (!std::isfinite(rectangle.left) || !std::isfinite(rectangle.top) ||
            !std::isfinite(rectangle.right) || !std::isfinite(rectangle.bottom)) return false;
#endif
        if (rectangle.right < rectangle.left || rectangle.bottom < rectangle.top) return false;
    }
    return true;
}

static bool valid_inline_values(float width, float ascent, float descent) noexcept {
#if defined(__aarch64__) || defined(_M_ARM64)
    const float values[4]{width, ascent, descent, 0.0F};
    const auto lanes = vld1q_f32(values);
    return vminvq_u32(vandq_u32(vcgeq_f32(lanes, vdupq_n_f32(0.0F)),
        vcleq_f32(lanes, vdupq_n_f32(std::numeric_limits<float>::max())))) != 0U;
#elif defined(__SSE2__) || defined(_M_X64)
    const auto lanes = _mm_setr_ps(width, ascent, descent, 0.0F);
    return _mm_movemask_ps(_mm_and_ps(_mm_cmpge_ps(lanes, _mm_setzero_ps()),
        _mm_cmple_ps(lanes, _mm_set1_ps(std::numeric_limits<float>::max())))) == 15;
#else
    return std::isfinite(width) && width >= 0.0F && std::isfinite(ascent) &&
        ascent >= 0.0F && std::isfinite(descent) && descent >= 0.0F;
#endif
}

static bool valid_inline_flow(const progpu_native_text_shape_request& shaping,
    const progpu_native_text_layout_options& layout, std::uint32_t style_count,
    const inline_flow_policy* policy) noexcept {
    if (policy == nullptr) return true;
    if (layout.trimming != 0U || (shaping.input_count != 0U && style_count == 0U) ||
        !has_aligned_pointer(policy->metrics, style_count) ||
        !has_aligned_pointer(policy->objects, policy->object_count) ||
        policy->object_count > shaping.input_count) return false;
    for (std::uint32_t i = 0; i < style_count; ++i)
        if (!valid_inline_values(0, policy->metrics[i].ascent, policy->metrics[i].descent)) return false;
    for (std::uint32_t i = 0; i < policy->object_count; ++i) {
        const auto& object = policy->objects[i];
        if (object.scalar_index >= shaping.input_count ||
            (i != 0U && object.scalar_index <= policy->objects[i - 1U].scalar_index) ||
            shaping.input[object.scalar_index].code_point != 0xFFFCU ||
            !valid_inline_values(object.width, object.ascent, object.descent)) return false;
    }
    std::uint32_t object_index = 0U;
    for (std::uint32_t i = 0; i < shaping.input_count; ++i)
        if (shaping.input[i].code_point == 0xFFFCU) {
            if (object_index >= policy->object_count ||
                policy->objects[object_index++].scalar_index != i) return false;
        }
    return object_index == policy->object_count;
}

static bool valid_floating_flow(const progpu_native_text_shape_request& shaping,
    const excluded_flow_policy* excluded, const floating_flow_policy* floating) noexcept {
    if (floating == nullptr) return true;
    const auto* options = floating->options;
    if (excluded == nullptr || options == nullptr || options->struct_size != sizeof(*options) ||
        options->reserved0 != 0U || options->reserved1 != 0U ||
        floating->count > (1U << 20U) - excluded->count ||
        !has_aligned_pointer(floating->events, floating->count) ||
        !valid_inline_values(0, options->empty_ascent, options->empty_descent)) return false;
    std::uint32_t previous = 0;
    for (const auto& item : std::span(floating->events, floating->count)) {
        if (item.scalar_index < previous || item.scalar_index > shaping.input_count ||
            (item.scalar_index < shaping.input_count && shaping.input[item.scalar_index].input_index > INT32_MAX) ||
            item.alignment > 2U || item.width <= 0 || item.height <= 0 ||
            !valid_inline_values(item.width, item.height, 0)) return false;
        previous = item.scalar_index;
    }
    return true;
}

static bool build_flow_capacities(const progpu_native_text_shape_request& request,
    const progpu_native_text_context& context, paragraph_capacities& result, font_error& error,
    bool substitute_numbers,
    const inline_flow_policy* inline_flow, const excluded_flow_policy* excluded,
    const floating_flow_policy* floating) noexcept {
    const auto float_count = floating == nullptr ? 0U : floating->count;
    const auto count = (excluded == nullptr ? 0U : excluded->count) + float_count;
    if (!try_build_paragraph_capacities(request, context, result, error,
        inline_flow != nullptr, excluded != nullptr, count, substitute_numbers)) return false;
    if (floating == nullptr || (request.input_count == 0U && float_count == 0U)) return true;
    scratch_size_builder extra{};
    if (!extra.add<std::byte>(result.scratch_bytes) ||
        (request.input_count == 0U && (!extra.add<text_exclusion_rectangle>(count) ||
            !extra.add<text_line_interval>(count) || !extra.add<text_line_interval>(static_cast<std::size_t>(count) + 1U) ||
            !extra.add<text_line_fragment>(static_cast<std::size_t>(count) + 1U))) ||
        !extra.add<text_floating_item>(float_count) || !extra.add<text_floating_placement>(float_count) ||
        !extra.add<text_exclusion_rectangle>(count) ||
        extra.size() > std::numeric_limits<std::size_t>::max() - (alignof(std::max_align_t) - 1U)) {
        error = font_error::invalid_argument; return false;
    }
    result.scratch_bytes = extra.size() + alignof(std::max_align_t) - 1U;
    return true;
}

static void publish_floating_flow(const text_floating_flow_result& source,
    std::span<const text_floating_placement> placements, const floating_flow_policy& target) noexcept {
    for (std::uint32_t i = 0; i < source.float_count; ++i) {
        const auto& value = placements[i];
        target.output[i] = {value.source_row, 0U, value.bounds.left, value.bounds.top,
            value.bounds.right, value.bounds.bottom};
    }
    *target.result = {sizeof(*target.result), source.float_count, source.height, source.content_width,
        source.text.row_count, source.text.next_glyph, source.text.attempts};
}

static progpu_native_status paragraph_requirements_core(
    progpu_native_text_context* context,
    const progpu_native_text_shape_request* shaping,
    const progpu_native_text_layout_options* layout,
    const progpu_native_text_style_run* styles, std::uint32_t style_count,
    const progpu_native_text_flow_options* flow,
    progpu_native_text_paragraph_requirements* requirements,
    const inline_flow_policy* inline_flow = nullptr, const excluded_flow_policy* excluded_flow = nullptr,
    const floating_flow_policy* floating_flow = nullptr) {
    if (requirements == nullptr ||
        requirements->struct_size <
            sizeof(progpu_native_text_paragraph_requirements)) {
        return PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
    }
    *requirements = {};
    requirements->struct_size = sizeof(*requirements);
    if (context == nullptr || !valid_request(shaping, false) ||
        shaping->direction > PROGPU_NATIVE_TEXT_DIRECTION_RIGHT_TO_LEFT ||
        !valid_paragraph_layout_options(layout) || !valid_style_runs(*context, *shaping, styles, style_count) ||
        !valid_flow_options(flow, layout) || !valid_inline_flow(*shaping, *layout, style_count, inline_flow) ||
        !valid_excluded_flow(*layout, excluded_flow) || !valid_floating_flow(*shaping, excluded_flow, floating_flow)) {
        requirements->error_code =
            static_cast<std::uint32_t>(font_error::invalid_argument);
        requirements->error_stage = PROGPU_NATIVE_TEXT_PARAGRAPH_STAGE_SHAPING;
        return PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
    }
    paragraph_capacities capacities{};
    font_error error = font_error::none;
    if (!build_flow_capacities(*shaping, *context, capacities, error,
            has_number_substitution(styles, style_count), inline_flow, excluded_flow, floating_flow)) {
        requirements->error_code = static_cast<std::uint32_t>(error);
        requirements->error_stage = PROGPU_NATIVE_TEXT_PARAGRAPH_STAGE_SHAPING;
        return status_from_error(error);
    }
    requirements->glyph_capacity = capacities.shaping.glyphs;
    requirements->line_capacity = capacities.shaping.glyphs;
    if (floating_flow != nullptr && floating_flow->count != 0U && shaping->input_count == 0U)
        requirements->line_capacity = 1U;
    requirements->scratch_alignment = 1U;
    requirements->scratch_bytes = capacities.scratch_bytes;
    return PROGPU_NATIVE_STATUS_SUCCESS;
}

static progpu_native_status layout_empty_floating_flow(const progpu_native_text_layout_options& layout,
    const excluded_flow_policy& excluded, const floating_flow_policy& floating,
    progpu_native_positioned_text_line* output_lines, void* workspace, std::size_t workspace_size,
    progpu_native_text_paragraph_result& output) noexcept {
    scratch_arena arena{workspace, workspace_size};
    const auto count = excluded.count + floating.count;
    std::span<text_exclusion_rectangle> rectangles{}, collisions{};
    std::span<text_line_interval> exclusion_scratch{}, intervals{};
    std::span<text_line_fragment> fragments{};
    std::span<text_floating_item> items{};
    std::span<text_floating_placement> placed{};
    font_error error{};
    const auto fail = [&](font_error value) noexcept {
        output.error_code = static_cast<std::uint32_t>(value);
        output.error_stage = PROGPU_NATIVE_TEXT_PARAGRAPH_STAGE_LAYOUT;
        return status_from_error(value);
    };
    if (!arena.take(count, rectangles) || !arena.take(count, exclusion_scratch) ||
        !arena.take(static_cast<std::size_t>(count) + 1U, intervals) ||
        !arena.take(static_cast<std::size_t>(count) + 1U, fragments) ||
        !arena.take(floating.count, items) || !arena.take(floating.count, placed) ||
        !arena.take(count, collisions)) return fail(font_error::insufficient_buffer);
    for (std::uint32_t i = 0; i < excluded.count; ++i) {
        const auto r = excluded.rectangles[i];
        rectangles[i] = {r.left, r.top, r.right, r.bottom};
    }
    for (std::uint32_t i = 0; i < floating.count; ++i) {
        const auto item = floating.events[i];
        items[i] = {0, item.width, item.height, static_cast<text_anchor_alignment>(item.alignment)};
    }
    positioned_text_line lines[1]{};
    text_fragment_placement frames[1]{};
    text_floating_flow_result result{};
    const auto paragraph_level = static_cast<std::int8_t>(output.paragraph_level);
    if (!try_layout_floating_logical_shaped_text_at({}, {}, {}, {}, {}, {}, paragraph_level,
        convert_paragraph_layout_options(layout, paragraph_level), {}, excluded.origin_y,
        {floating.options->empty_ascent, floating.options->empty_descent}, items, rectangles.first(excluded.count),
        collisions, exclusion_scratch, intervals, fragments, {}, {}, {}, lines, frames, placed, result,
        excluded.options->maximum_attempts, &error)) return fail(error);
    text_layout_metrics metrics{};
    if (!try_measure_fragment_text_lines(lines, frames, layout.maximum_width, metrics, &error)) return fail(error);
    const auto line = lines[0];
    output_lines[0] = {0, 0, 0, 0, line.width, line.baseline_y, line.height, 0, 0, 0, 0};
    excluded.output[0] = {frames[0].top, 0, frames[0].left, frames[0].width, 0};
    output.line_count = 1;
    output.content_width = metrics.content_width;
    output.content_height = metrics.content_height;
    output.measured_width = metrics.measured_width;
    output.measured_height = metrics.measured_height;
    output.scratch_bytes_used = arena.used();
    publish_floating_flow(result, placed, floating);
    return PROGPU_NATIVE_STATUS_SUCCESS;
}

static progpu_native_status paragraph_layout_core(
    progpu_native_text_context* context,
    const progpu_native_text_shape_request* shaping,
    const progpu_native_text_layout_options* layout,
    const progpu_native_text_style_run* styles, std::uint32_t style_count,
    const progpu_native_text_flow_options* flow,
    progpu_native_positioned_text_glyph* glyphs,
    std::uint32_t glyph_capacity,
    progpu_native_positioned_text_line* lines,
    std::uint32_t line_capacity,
    void* scratch,
    std::size_t scratch_size,
    progpu_native_text_paragraph_result* result,
    progpu_native_text_intrinsic_widths* widths = nullptr,
    std::uint32_t wrapping = PROGPU_NATIVE_TEXT_WRAPPING_EMERGENCY,
    float collapse_width = -1.0F, const inline_flow_policy* inline_flow = nullptr,
    const excluded_flow_policy* excluded_flow = nullptr, const floating_flow_policy* floating_flow = nullptr,
    std::int32_t continuation_start = -1,
    const hinted_paragraph_policy* hinted = nullptr) {
    if (result == nullptr ||
        result->struct_size < sizeof(progpu_native_text_paragraph_result)) {
        return PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
    }
    *result = {};
    result->struct_size = sizeof(*result);
    if (floating_flow != nullptr) {
        if (!has_aligned_pointer(floating_flow->result, 1U) ||
            floating_flow->result->struct_size < sizeof(*floating_flow->result)) return PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
        *floating_flow->result = {};
        floating_flow->result->struct_size = sizeof(*floating_flow->result);
    }
    if (widths != nullptr) {
        if (widths->struct_size < sizeof(*widths)) return PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
        *widths = {};
        widths->struct_size = sizeof(*widths);
        if (layout == nullptr || layout->maximum_lines != 0U || layout->trimming != 0U)
            return PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
    }
    if (context == nullptr || !valid_request(shaping, false) ||
        shaping->direction > PROGPU_NATIVE_TEXT_DIRECTION_RIGHT_TO_LEFT ||
        !valid_paragraph_layout_options(layout) || !valid_style_runs(*context, *shaping, styles, style_count) ||
        !valid_flow_options(flow, layout) || !valid_inline_flow(*shaping, *layout, style_count, inline_flow) ||
        !valid_excluded_flow(*layout, excluded_flow) || !valid_floating_flow(*shaping, excluded_flow, floating_flow) ||
        wrapping > PROGPU_NATIVE_TEXT_WRAPPING_WHOLE_WORD || continuation_start < -1 ||
        (continuation_start >= 0 && (excluded_flow != nullptr || floating_flow != nullptr || widths != nullptr ||
            ((layout->maximum_lines != 0U || layout->trimming != 0U) && collapse_width < 0.0F) ||
            (shaping->input_count == 0U && continuation_start != 0))) ||
        !std::isfinite(collapse_width) || collapse_width < -1.0F ||
        (collapse_width >= 0.0F && (layout->maximum_lines == 0U || layout->trimming == 0U))) {
        result->error_code =
            static_cast<std::uint32_t>(font_error::invalid_argument);
        result->error_stage = PROGPU_NATIVE_TEXT_PARAGRAPH_STAGE_SHAPING;
        return PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
    }
    // This private owned path does not silently admit contracts it cannot retain.
    if (hinted != nullptr && (hinted->generation == nullptr || inline_flow == nullptr ||
        flow != nullptr || widths != nullptr || excluded_flow != nullptr || floating_flow != nullptr ||
        collapse_width != -1.0F || continuation_start != -1 || layout->trimming != 0U)) {
        result->error_code = static_cast<std::uint32_t>(font_error::invalid_argument);
        result->error_stage = PROGPU_NATIVE_TEXT_PARAGRAPH_STAGE_SHAPING;
        return PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
    }
    result->paragraph_level =
        shaping->direction == PROGPU_NATIVE_TEXT_DIRECTION_RIGHT_TO_LEFT
        ? 1
        : 0;
    if (shaping->input_count == 0U && (floating_flow == nullptr || floating_flow->count == 0U)) {
        if (excluded_flow != nullptr)
            result->content_height = static_cast<float>(excluded_flow->origin_y);
        if (floating_flow != nullptr) floating_flow->result->content_height = excluded_flow->origin_y;
        return PROGPU_NATIVE_STATUS_SUCCESS;
    }
    paragraph_capacities capacities{};
    font_error font_result = font_error::none;
    const bool substitute_numbers = has_number_substitution(styles, style_count);
    if (!build_flow_capacities(*shaping, *context, capacities, font_result,
            substitute_numbers, inline_flow, excluded_flow, floating_flow)) {
        result->error_code = static_cast<std::uint32_t>(font_result);
        result->error_stage = PROGPU_NATIVE_TEXT_PARAGRAPH_STAGE_SHAPING;
        return status_from_error(font_result);
    }
    const auto required_lines = shaping->input_count == 0U && floating_flow != nullptr ? 1U : capacities.shaping.glyphs;
    if (glyph_capacity < capacities.shaping.glyphs ||
        line_capacity < required_lines ||
        !has_aligned_pointer(glyphs, capacities.shaping.glyphs) ||
        !has_aligned_pointer(lines, required_lines) ||
        scratch == nullptr || scratch_size < capacities.scratch_bytes ||
        (excluded_flow != nullptr && (excluded_flow->capacity < required_lines ||
            !has_aligned_pointer(excluded_flow->output, required_lines))) ||
        (floating_flow != nullptr && (floating_flow->capacity < floating_flow->count ||
            !has_aligned_pointer(floating_flow->output, floating_flow->count)))) {
        result->error_code =
            static_cast<std::uint32_t>(font_error::insufficient_buffer);
        result->error_stage = PROGPU_NATIVE_TEXT_PARAGRAPH_STAGE_LAYOUT;
        return PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
    }

    if (floating_flow != nullptr) {
        struct region { std::uintptr_t begin; std::size_t size; bool writable; };
        const auto memory = [](auto* pointer, std::size_t count, bool writable = false) noexcept {
            if (count > std::numeric_limits<std::size_t>::max() / sizeof(*pointer))
                return region{0, std::numeric_limits<std::size_t>::max(), writable};
            return region{reinterpret_cast<std::uintptr_t>(pointer), count * sizeof(*pointer), writable};
        };
        const region buffers[]{memory(shaping, 1), memory(layout, 1), memory(styles, style_count),
            memory(flow, flow == nullptr ? 0U : 1U), memory(inline_flow->metrics, style_count),
            memory(inline_flow->objects, inline_flow->object_count), memory(floating_flow->options, 1),
            memory(floating_flow->events, floating_flow->count), memory(excluded_flow->rectangles, excluded_flow->count),
            memory(shaping->input, shaping->input_count), memory(shaping->pre_context, shaping->pre_context_count),
            memory(shaping->post_context, shaping->post_context_count), memory(shaping->features, shaping->feature_count),
            memory(shaping->normalized_coordinates, shaping->normalized_coordinate_count),
            memory(shaping->font_data, shaping->font_size), memory(shaping->normalization_data, shaping->normalization_data_size),
            memory(glyphs, glyph_capacity, true), memory(lines, line_capacity, true),
            memory(excluded_flow->output, excluded_flow->capacity, true),
            memory(floating_flow->output, floating_flow->capacity, true), memory(result, 1, true),
            memory(floating_flow->result, 1, true), memory(static_cast<std::byte*>(scratch), scratch_size, true)};
        for (std::size_t i = 0; i < std::size(buffers); ++i) {
            const auto a = buffers[i];
            if (a.size == 0U) continue;
            bool valid = a.begin != 0U && a.begin <= UINTPTR_MAX - a.size;
            for (std::size_t j = 0; valid && j < i; ++j) {
                const auto b = buffers[j];
                if (b.size != 0U && (a.writable || b.writable) &&
                    a.begin < b.begin + b.size && b.begin < a.begin + a.size) valid = false;
            }
            if (!valid) {
                result->error_code = static_cast<std::uint32_t>(font_error::invalid_argument);
                result->error_stage = PROGPU_NATIVE_TEXT_PARAGRAPH_STAGE_LAYOUT;
                return PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
            }
        }
    }
    try {
        if (shaping->input_count == 0U && floating_flow != nullptr)
            return layout_empty_floating_flow(*layout, *excluded_flow, *floating_flow,
                lines, scratch, scratch_size, *result);
        scratch_arena arena{scratch, scratch_size};
        const std::size_t input_count = shaping->input_count;
        const std::size_t glyph_limit = capacities.shaping.glyphs;
        const auto exclusion_capacity = (excluded_flow == nullptr ? 0U : excluded_flow->count) +
            (floating_flow == nullptr ? 0U : floating_flow->count);
        std::span<std::byte> shape_scratch{};
        std::span<progpu_native_text_shaping_glyph> run_glyphs{};
        std::span<progpu_native_text_scalar> substituted_input{};
        std::span<unicode_scalar> native_input{};
        std::span<unicode_bidi_unit> bidi_units{};
        std::span<std::uint32_t> bidi_indices{};
        std::span<unicode_bidi_level_run> bidi_runs{};
        std::span<unicode_bidi_bracket_pair> bidi_pairs{};
        std::span<unicode_bidi_level> scalar_levels{};
        std::span<unicode_script_run> script_runs{};
        std::span<unicode_grapheme_cluster> graphemes{};
        std::span<font_fallback_candidate> fallback_candidates{};
        std::span<font_fallback_run> fallback_runs{};
        std::span<unicode_line_break_class> line_classes{};
        std::span<text_line_break_kind> scalar_breaks{};
        std::span<shaping_glyph> logical_glyphs{};
        std::span<std::int8_t> glyph_levels{};
        std::span<float> glyph_scales{};
        std::span<float> tab_advances{};
        std::span<text_justification_class> justification{};
        std::span<std::uint32_t> glyph_font_indices{};
        std::span<text_line_break_kind> glyph_breaks{};
        std::span<text_visual_cluster_group> visual_groups{};
        std::span<std::uint32_t> visual_indices{};
        std::span<positioned_text_glyph> positioned{};
        std::span<positioned_text_line> native_lines{};
        std::span<text_item_metrics> item_metrics{};
        std::span<text_exclusion_rectangle> rectangles{};
        std::span<text_line_interval> exclusion_scratch{}, intervals{};
        std::span<text_line_fragment> fragments{};
        std::span<text_fragment_placement> placements{};
        std::span<text_floating_item> floating_items{};
        std::span<text_floating_placement> floating_placements{};
        std::span<text_exclusion_rectangle> floating_collisions{};
        if (!arena.take(capacities.shaping.scratch_bytes, shape_scratch) ||
            !arena.take(glyph_limit, run_glyphs) ||
            (substitute_numbers && !arena.take(input_count, substituted_input)) ||
            !arena.take(input_count, native_input) ||
            !arena.take(input_count, bidi_units) ||
            !arena.take(input_count * 4U, bidi_indices) ||
            !arena.take(input_count, bidi_runs) ||
            !arena.take(input_count / 2U, bidi_pairs) ||
            !arena.take(input_count, scalar_levels) ||
            !arena.take(input_count, script_runs) ||
            !arena.take(input_count, graphemes) ||
            !arena.take(context->font_count(), fallback_candidates) ||
            !arena.take(input_count, fallback_runs) ||
            !arena.take(input_count, line_classes) ||
            !arena.take(input_count, scalar_breaks) ||
            !arena.take(glyph_limit, logical_glyphs) ||
            !arena.take(glyph_limit, glyph_levels) ||
            !arena.take(glyph_limit, glyph_scales) ||
            !arena.take(glyph_limit, tab_advances) ||
            !arena.take(glyph_limit, justification) ||
            !arena.take(glyph_limit, glyph_font_indices) ||
            !arena.take(glyph_limit, glyph_breaks) ||
            !arena.take(glyph_limit, visual_groups) ||
            !arena.take(glyph_limit, visual_indices) ||
            !arena.take(glyph_limit, positioned) ||
            !arena.take(glyph_limit, native_lines) ||
            (inline_flow != nullptr && !arena.take(glyph_limit, item_metrics)) ||
            (excluded_flow != nullptr && (!arena.take(exclusion_capacity, rectangles) ||
                !arena.take(exclusion_capacity, exclusion_scratch) ||
                !arena.take(static_cast<std::size_t>(exclusion_capacity) + 1U, intervals) ||
                !arena.take(static_cast<std::size_t>(exclusion_capacity) + 1U, fragments) ||
                !arena.take(glyph_limit, placements))) ||
            (floating_flow != nullptr && (!arena.take(floating_flow->count, floating_items) ||
                !arena.take(floating_flow->count, floating_placements) || !arena.take(exclusion_capacity, floating_collisions)))) {
            result->error_code =
                static_cast<std::uint32_t>(font_error::insufficient_buffer);
            result->error_stage = PROGPU_NATIVE_TEXT_PARAGRAPH_STAGE_LAYOUT;
            return PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
        }

        if (excluded_flow != nullptr)
            for (std::size_t i = 0; i < excluded_flow->count; ++i) {
                const auto& source = excluded_flow->rectangles[i];
                rectangles[i] = {source.left, source.top, source.right, source.bottom};
            }
        const progpu_native_text_scalar* paragraph_input = shaping->input;
        if (substitute_numbers) {
            std::copy_n(shaping->input, input_count, substituted_input.data());
            apply_number_substitution(substituted_input,
                std::span(styles, style_count), shaping->direction);
            paragraph_input = substituted_input.data();
        }
        const bool source_digit_bidi = preserve_source_digit_bidi(styles, style_count);
        copy_scalars(source_digit_bidi ? shaping->input : paragraph_input, native_input);
        unicode_error unicode_result = unicode_error::none;
        unicode_bidi_scratch bidi_scratch{
            bidi_units, bidi_indices, bidi_runs, bidi_pairs};
        const std::int8_t requested_level =
            shaping->direction == PROGPU_NATIVE_TEXT_DIRECTION_LEFT_TO_RIGHT
            ? 0
            : shaping->direction ==
                    PROGPU_NATIVE_TEXT_DIRECTION_RIGHT_TO_LEFT
                ? 1
                : -1;
        std::int8_t paragraph_level = 0;
        std::uint32_t scalar_level_count = 0U;
        if (!try_resolve_unicode_bidi(
                native_input,
                requested_level,
                bidi_scratch,
                scalar_levels,
                paragraph_level,
                scalar_level_count,
                &unicode_result)) {
            result->error_code = static_cast<std::uint32_t>(unicode_result);
            result->error_stage = PROGPU_NATIVE_TEXT_PARAGRAPH_STAGE_BIDI;
            return status_from_unicode_error(unicode_result);
        }
        if (source_digit_bidi)
            copy_scalars(paragraph_input, native_input);
        result->paragraph_level = paragraph_level;
        const bool itemize_scripts = shaping->unicode_script == 0U ||
            shaping->unicode_script == default_script.value;
        std::uint32_t script_run_count = 1U;
        if (itemize_scripts) {
            if (!try_itemize_unicode_scripts(
                    native_input,
                    script_runs,
                    script_run_count,
                    &unicode_result)) {
                result->error_code =
                    static_cast<std::uint32_t>(unicode_result);
                result->error_stage =
                    PROGPU_NATIVE_TEXT_PARAGRAPH_STAGE_SHAPING;
                return status_from_unicode_error(unicode_result);
            }
        } else {
            script_runs[0U] = unicode_script_run{
                0U,
                static_cast<std::uint32_t>(input_count),
                native_input.front().input_index,
                static_cast<std::uint32_t>(
                    static_cast<std::uint64_t>(native_input.back().input_index) +
                    native_input.back().input_length -
                    native_input.front().input_index),
                open_type_tag{shaping->unicode_script}};
        }
        std::uint32_t grapheme_count = 0U;
        if (!try_segment_unicode_graphemes(
                native_input, graphemes, grapheme_count, &unicode_result)) {
            result->error_code = static_cast<std::uint32_t>(unicode_result);
            result->error_stage = PROGPU_NATIVE_TEXT_PARAGRAPH_STAGE_SHAPING;
            return status_from_unicode_error(unicode_result);
        }
        fallback_candidates[0U] = font_fallback_candidate{
            &context->font, 0U};
        for (std::size_t index = 0U;
             index < context->fallback_fonts.size();
             ++index) {
            const auto& fallback = context->fallback_fonts[index];
            fallback_candidates[index + 1U] = font_fallback_candidate{
                &fallback.font, fallback.identity};
        }
        std::uint32_t fallback_run_count = 0U;
        if (style_count != 0U) {
            for (std::uint32_t i = 0U; i < style_count; ++i) {
                const auto& style = styles[i];
                const auto& first = paragraph_input[style.scalar_start];
                const auto& last = paragraph_input[style.scalar_start + style.scalar_count - 1U];
                fallback_runs[i] = font_fallback_run{style.scalar_start, style.scalar_count,
                    first.input_index, last.input_index + last.input_length - first.input_index,
                    style.font_index, false, 0U, 0U, 0U};
            }
            fallback_run_count = style_count;
        } else if (!try_itemize_font_fallback(
                native_input,
                graphemes.first(grapheme_count),
                fallback_candidates,
                0U,
                fallback_runs,
                fallback_run_count,
                &font_result)) {
            result->error_code = static_cast<std::uint32_t>(font_result);
            result->error_stage = PROGPU_NATIVE_TEXT_PARAGRAPH_STAGE_SHAPING;
            return status_from_error(font_result);
        }
        if (!try_resolve_unicode_line_breaks(
                native_input,
                line_classes,
                scalar_breaks,
                &unicode_result)) {
            result->error_code = static_cast<std::uint32_t>(unicode_result);
            result->error_stage =
                PROGPU_NATIVE_TEXT_PARAGRAPH_STAGE_LINE_BREAK;
            return status_from_unicode_error(unicode_result);
        }

        if (hinted != nullptr) {
            auto& retained = *hinted->generation;
            // Number-symbol substitution must not sneak an unretained item
            // contract into this opt-in. Inspect the actual producer output.
            for (const auto& scalar : native_input)
                if (scalar.code_point == 9U || scalar.code_point == 0xFFFCU) {
                    result->error_code = static_cast<std::uint32_t>(font_error::invalid_argument);
                    result->error_stage = PROGPU_NATIVE_TEXT_PARAGRAPH_STAGE_SHAPING;
                    return PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
                }
            retained.shaping_input.assign(native_input.begin(), native_input.end());
            retained.source_digit_bidi = source_digit_bidi;
            retained.paragraph_level = paragraph_level;
            retained.scalar_levels.assign(scalar_levels.begin(), scalar_levels.begin() + scalar_level_count);
            retained.script_runs.assign(script_runs.begin(), script_runs.begin() + script_run_count);
            retained.graphemes.assign(graphemes.begin(), graphemes.begin() + grapheme_count);
            retained.fallback_runs.assign(fallback_runs.begin(), fallback_runs.begin() + fallback_run_count);
            retained.line_break_classes.assign(line_classes.begin(), line_classes.end());
            retained.scalar_breaks.assign(scalar_breaks.begin(), scalar_breaks.end());
            retained.logical_owners.resize(glyph_limit);
        }

        std::uint32_t logical_count = 0U;
        std::size_t scalar_start = 0U;
        std::size_t script_run_index = 0U;
        std::size_t fallback_run_index = 0U;
        std::uint32_t object_index = 0U;
        while (scalar_start < input_count) {
            while (script_run_index < script_run_count &&
                scalar_start >= static_cast<std::size_t>(
                    script_runs[script_run_index].scalar_start) +
                    script_runs[script_run_index].scalar_count) {
                ++script_run_index;
            }
            if (script_run_index >= script_run_count) {
                result->error_code =
                    static_cast<std::uint32_t>(font_error::invalid_argument);
                result->error_stage =
                    PROGPU_NATIVE_TEXT_PARAGRAPH_STAGE_SHAPING;
                return PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
            }
            const auto& script_run = script_runs[script_run_index];
            const std::size_t script_end =
                static_cast<std::size_t>(script_run.scalar_start) +
                script_run.scalar_count;
            while (fallback_run_index < fallback_run_count &&
                scalar_start >= static_cast<std::size_t>(
                    fallback_runs[fallback_run_index].scalar_index) +
                    fallback_runs[fallback_run_index].scalar_count) {
                ++fallback_run_index;
            }
            if (fallback_run_index >= fallback_run_count) {
                result->error_code =
                    static_cast<std::uint32_t>(font_error::invalid_argument);
                result->error_stage =
                    PROGPU_NATIVE_TEXT_PARAGRAPH_STAGE_SHAPING;
                return PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
            }
            const auto& fallback_run = fallback_runs[fallback_run_index];
            const std::size_t fallback_end =
                static_cast<std::size_t>(fallback_run.scalar_index) +
                fallback_run.scalar_count;
            const auto* selected_font =
                context->font_at(fallback_run.font_index);
            if (selected_font == nullptr) {
                result->error_code =
                    static_cast<std::uint32_t>(font_error::invalid_argument);
                result->error_stage =
                    PROGPU_NATIVE_TEXT_PARAGRAPH_STAGE_SHAPING;
                return PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
            }
            const std::int8_t level = scalar_levels[scalar_start].level;
            if (inline_flow != nullptr && object_index < inline_flow->object_count &&
                inline_flow->objects[object_index].scalar_index == scalar_start) {
                const auto& object = inline_flow->objects[object_index++];
                if (logical_count >= logical_glyphs.size()) return PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
                shaping_glyph item{};
                item.glyph_id = UINT32_MAX - 1U;
                item.code_point = 0xFFFCU;
                item.cluster = static_cast<std::int32_t>(paragraph_input[scalar_start].input_index);
                item.advance_x = object.width == 0.0F ? 0 : 1;
                logical_glyphs[logical_count] = item;
                glyph_levels[logical_count] = level;
                glyph_font_indices[logical_count] = UINT32_MAX;
                glyph_scales[logical_count] = object.width == 0.0F ? 1.0F : object.width;
                item_metrics[logical_count] = {object.ascent, object.descent};
                ++logical_count; ++scalar_start;
                continue;
            }
            const bool tab_flow = flow != nullptr && flow->incremental_tab > 0.0F;
            if (tab_flow && paragraph_input[scalar_start].code_point == 9U) {
                if (logical_count >= logical_glyphs.size()) return PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
                shaping_glyph tab{};
                tab.glyph_id = text_tab_glyph_id;
                tab.code_point = 9U;
                tab.cluster = static_cast<std::int32_t>(paragraph_input[scalar_start].input_index);
                logical_glyphs[logical_count] = tab;
                glyph_levels[logical_count] = paragraph_level;
                glyph_font_indices[logical_count] = fallback_run.font_index;
                glyph_scales[logical_count] = layout->scale;
                if (inline_flow != nullptr) item_metrics[logical_count] = {
                    inline_flow->metrics[fallback_run_index].ascent, inline_flow->metrics[fallback_run_index].descent};
                ++logical_count;
                ++scalar_start;
                continue;
            }
            std::size_t scalar_end = scalar_start + 1U;
            while (scalar_end < input_count && scalar_end < script_end &&
                scalar_end < fallback_end &&
                scalar_levels[scalar_end].level == level &&
                (!tab_flow || paragraph_input[scalar_end].code_point != 9U) &&
                (inline_flow == nullptr || paragraph_input[scalar_end].code_point != 0xFFFCU)) {
                ++scalar_end;
            }
            auto run_request = *shaping;
            float run_scale = layout->scale;
            if (style_count != 0U) {
                const auto& style = styles[fallback_run_index];
                run_scale = style.scale;
                run_request.features = style.feature_count == 0U ? nullptr : shaping->features + style.feature_start;
                run_request.feature_count = style.feature_count;
                run_request.language = style.language;
            }
            run_request.input = paragraph_input + scalar_start;
            run_request.input_count = static_cast<std::uint32_t>(
                scalar_end - scalar_start);
            run_request.direction = (level & 1) == 0
                ? PROGPU_NATIVE_TEXT_DIRECTION_LEFT_TO_RIGHT
                : PROGPU_NATIVE_TEXT_DIRECTION_RIGHT_TO_LEFT;
            if (shaping->unicode_script == 0U ||
                shaping->unicode_script == default_script.value) {
                run_request.unicode_script = script_run.script.value;
            }
            if (scalar_start != 0U) {
                run_request.pre_context = paragraph_input;
                run_request.pre_context_count =
                    static_cast<std::uint32_t>(scalar_start);
            }
            if (scalar_end != input_count) {
                run_request.post_context = paragraph_input + scalar_end;
                run_request.post_context_count = static_cast<std::uint32_t>(
                    input_count - scalar_end);
            }
            progpu_native_text_shape_result shape_result{};
            shape_result.struct_size = sizeof(shape_result);
            std::shared_ptr<const hinted_shaped_run> retained_run{};
            progpu_native_status shape_status = PROGPU_NATIVE_STATUS_SUCCESS;
            if (hinted == nullptr) {
                shape_status = shape_core(run_request, *selected_font,
                    context->has_normalization ? &context->normalization : nullptr,
                    capacities.shaping, run_glyphs.data(), static_cast<std::uint32_t>(run_glyphs.size()),
                    shape_scratch.data(), shape_scratch.size(), shape_result, context);
            } else {
                const auto& device = hinted->generation->device_styles[fallback_run_index];
                const progpu_native_hinted_font_request hint_request{PROGPU_NATIVE_ABI_VERSION, sizeof(progpu_native_hinted_font_request),
                    device.font_index, device.x_pixels_per_em_26_6, device.y_pixels_per_em_26_6,
                    static_cast<std::uint32_t>(device.policy), device.x_phase_26_6, device.y_phase_26_6,
                    static_cast<std::uint32_t>(device.variation_coordinates_16_16.size()), 0U};
                shape_status = shape_hinted_request_generation(context, hint_request,
                    device.variation_coordinates_16_16.data(), run_request, retained_run, hinted->generation->has_source_geometry);
                if (shape_status == PROGPU_NATIVE_STATUS_SUCCESS) {
                    if (retained_run->glyphs.size() > run_glyphs.size())
                        shape_status = PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
                    else {
                        shape_result.glyph_count = static_cast<std::uint32_t>(retained_run->glyphs.size());
                        for (std::size_t i = 0U; i < retained_run->glyphs.size(); ++i) {
                            auto fitting_glyph = retained_run->glyphs[i];
                            if (!hinted->generation->source_styles.empty() &&
                                !project_hinted_source_advance(retained_run->glyphs[i],
                                    hinted->generation->source_styles[fallback_run_index].advance_policy,
                                    fitting_glyph)) {
                                shape_status = PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
                                break;
                            }
                            copy_hinted_run_glyph(fitting_glyph, run_glyphs[i]);
                        }
                    }
                }
                // Source scale remains identity; it never reprojects device metrics.
                run_scale = device.logical_units_per_physical_pixel / 64.0F;
            }
            if (shape_status != PROGPU_NATIVE_STATUS_SUCCESS) {
                result->error_code = shape_result.error_code;
                result->error_stage =
                    PROGPU_NATIVE_TEXT_PARAGRAPH_STAGE_SHAPING;
                return shape_status;
            }
            const auto first_logical = logical_count;
            if (!append_logical_bidi_run(
                    run_glyphs.first(shape_result.glyph_count),
                    level,
                    fallback_run.font_index,
                    logical_glyphs,
                    glyph_levels,
                    glyph_font_indices,
                    glyph_scales,
                    run_scale,
                    logical_count,
                    hinted == nullptr ? std::span<hinted_paragraph_glyph_owner>{} : hinted->generation->logical_owners,
                    retained_run.get(), hinted == nullptr ? 0U : static_cast<std::uint32_t>(hinted->generation->runs.size()))) {
                result->error_code =
                    static_cast<std::uint32_t>(font_error::insufficient_buffer);
                result->error_stage =
                    PROGPU_NATIVE_TEXT_PARAGRAPH_STAGE_SHAPING;
                return PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
            }
            ++result->shaping_run_count;
            if (hinted != nullptr) {
                const auto& device = hinted->generation->device_styles[fallback_run_index];
                hinted->generation->runs.push_back({std::move(retained_run), static_cast<std::uint32_t>(scalar_start),
                    static_cast<std::uint32_t>(scalar_end - scalar_start), first_logical, logical_count - first_logical,
                    fallback_run.font_index, static_cast<std::uint32_t>(fallback_run_index), level,
                    styles[fallback_run_index].scale, device.logical_units_per_physical_pixel});
            }
            if (inline_flow != nullptr) {
                const auto& metric = inline_flow->metrics[fallback_run_index];
                std::fill(item_metrics.begin() + first_logical, item_metrics.begin() + logical_count,
                    text_item_metrics{metric.ascent, metric.descent});
            }
            scalar_start = scalar_end;
        }
        result->shaped_glyph_count = logical_count;
        const auto logical = logical_glyphs.first(logical_count);
        if (floating_flow != nullptr) {
            std::size_t glyph_index = 0;
            for (std::uint32_t i = 0; i < floating_flow->count; ++i) {
                const auto& item = floating_flow->events[i];
                if (item.scalar_index == input_count) glyph_index = logical_count;
                else {
                    const auto source_index = static_cast<std::int32_t>(paragraph_input[item.scalar_index].input_index);
                    while (glyph_index < logical_count && logical[glyph_index].cluster < source_index) ++glyph_index;
                    if (glyph_index == logical_count || logical[glyph_index].cluster != source_index) {
                        result->error_code = static_cast<std::uint32_t>(font_error::invalid_argument);
                        result->error_stage = PROGPU_NATIVE_TEXT_PARAGRAPH_STAGE_CLUSTER_MAP;
                        return PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
                    }
                }
                floating_items[i] = {static_cast<std::uint32_t>(glyph_index), item.width, item.height,
                    static_cast<text_anchor_alignment>(item.alignment)};
            }
        }
        if (!detail::try_map_logical_cluster_breaks(
            std::span<const progpu_native_text_scalar>{
                    paragraph_input, input_count},
                scalar_breaks,
                logical,
                glyph_breaks.first(logical_count))) {
            result->error_code =
                static_cast<std::uint32_t>(font_error::invalid_argument);
            result->error_stage =
                PROGPU_NATIVE_TEXT_PARAGRAPH_STAGE_CLUSTER_MAP;
            return PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
        }

        text_intrinsic_widths intrinsic{};
        if (widths != nullptr && !try_measure_text_intrinsic_widths(native_input,
                logical, glyph_breaks.first(logical_count),
                style_count == 0U ? std::span<const float>{} : glyph_scales.first(logical_count),
                convert_paragraph_layout_options(*layout, paragraph_level),
                flow == nullptr ? text_tab_options{} : text_tab_options{flow->incremental_tab, flow->tab_origin},
                intrinsic, &font_result)) {
            result->error_code = static_cast<std::uint32_t>(font_result);
            result->error_stage = PROGPU_NATIVE_TEXT_PARAGRAPH_STAGE_LAYOUT;
            return status_from_error(font_result);
        }
        text_logical_layout_scratch logical_scratch{
            visual_groups, visual_indices};
        std::uint32_t positioned_count = 0U;
        std::uint32_t written_lines = 0U;
        auto positioning_options = convert_paragraph_layout_options(*layout, paragraph_level);
        positioning_options.collapse_width = collapse_width;
        const bool justify = positioning_options.alignment == text_alignment::justify;
        if (justify && !try_classify_text_justification(native_input, logical,
                justification.first(logical_count), &font_result)) {
            result->error_code = static_cast<std::uint32_t>(font_result);
            result->error_stage = PROGPU_NATIVE_TEXT_PARAGRAPH_STAGE_CLUSTER_MAP;
            return status_from_error(font_result);
        }
        if (hinted != nullptr) {
            auto& retained = *hinted->generation;
            retained.logical_glyphs.assign(logical.begin(), logical.end());
            retained.logical_bidi_levels.assign(glyph_levels.begin(), glyph_levels.begin() + logical_count);
            retained.logical_font_indices.assign(glyph_font_indices.begin(), glyph_font_indices.begin() + logical_count);
            retained.glyph_scales.assign(glyph_scales.begin(), glyph_scales.begin() + logical_count);
            retained.breaks_after.assign(glyph_breaks.begin(), glyph_breaks.begin() + logical_count);
            retained.item_metrics.assign(item_metrics.begin(), item_metrics.begin() + logical_count);
            retained.logical_owners.resize(logical_count);
            retained.logical_source_scales.resize(logical_count);
            retained.bidi_levels.resize(glyph_limit);
            retained.line_origins.resize(glyph_limit);
            retained.line_frames.resize(glyph_limit);
            for (std::size_t i = 0U; i < logical_count; ++i)
                retained.logical_source_scales[i] = retained.runs[retained.logical_owners[i].run_index].source_scale;
            if (justify) retained.justification_classes.assign(justification.begin(), justification.begin() + logical_count);
            if (retained.has_source_geometry) {
                retained.source_logical_metrics.resize(logical_count);
                retained.source_item_metrics.resize(logical_count);
                retained.source_glyphs.resize(glyph_limit);
                retained.source_lines.resize(glyph_limit);
                for (std::size_t i = 0U; i < logical_count; ++i) {
                    const auto style_index = retained.runs[retained.logical_owners[i].run_index].style_index;
                    const double dpi = retained.source_styles[style_index].pixels_per_dip;
                    const auto& glyph = logical[i];
                    if (!project_hinted_source_geometry(glyph, dpi, retained.source_logical_metrics[i])) {
                        result->error_code = static_cast<std::uint32_t>(font_error::invalid_argument);
                        result->error_stage = PROGPU_NATIVE_TEXT_PARAGRAPH_STAGE_LAYOUT;
                        return PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
                    }
                    retained.source_item_metrics[i] = retained.source_style_metrics[style_index];
                }
                const auto fitted = fit_hinted_source_paragraph(retained, 0U, retained.source_maximum_width);
                if (fitted.status != PROGPU_NATIVE_STATUS_SUCCESS) {
                    result->error_code = static_cast<std::uint32_t>(font_error::invalid_argument);
                    result->error_stage = PROGPU_NATIVE_TEXT_PARAGRAPH_STAGE_LAYOUT;
                    return fitted.status;
                }
                retained.source_fitting = fitted.generation;
                retained.source_logical_metrics = fitted.generation->metrics;
                if (retained.has_source_intrinsic_widths) {
                    hinted_source_intrinsic_widths widths{};
                    const auto measured = measure_hinted_source_intrinsic_widths(retained, widths);
                    if (measured != PROGPU_NATIVE_STATUS_SUCCESS) {
                        result->error_code = static_cast<std::uint32_t>(font_error::invalid_argument);
                        result->error_stage = PROGPU_NATIVE_TEXT_PARAGRAPH_STAGE_LAYOUT;
                        return measured;
                    }
                    retained.source_minimum_intrinsic_width = widths.minimum;
                    retained.source_maximum_intrinsic_width = widths.maximum;
                }
            }
        }
        // Shape and resolve bidi over the complete original paragraph first.
        // Only placement consumes a suffix, at an actual shaped cluster boundary.
        // Preserve full-paragraph glyph/font indices when publishing that suffix.
        std::uint32_t continuation_glyph_start = 0U;
        if (continuation_start >= 0) {
            const auto first = std::lower_bound(logical.begin(), logical.end(), continuation_start,
                [](const shaping_glyph& glyph, std::int32_t start) { return glyph.cluster < start; });
            continuation_glyph_start = static_cast<std::uint32_t>(first - logical.begin());
            if (continuation_glyph_start == logical_count ||
                logical[continuation_glyph_start].cluster != continuation_start) {
                result->error_code = static_cast<std::uint32_t>(font_error::invalid_argument);
                result->error_stage = PROGPU_NATIVE_TEXT_PARAGRAPH_STAGE_CLUSTER_MAP;
                return PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
            }
        }
        text_exclusion_flow_result excluded_result{};
        text_floating_flow_result floating_result{};
        const bool laid_out = floating_flow != nullptr
            ? try_layout_floating_logical_shaped_text_at(logical, glyph_breaks.first(logical_count),
                glyph_levels.first(logical_count), glyph_scales.first(logical_count),
                justify ? justification.first(logical_count) : std::span<const text_justification_class>{},
                item_metrics.first(logical_count), paragraph_level, positioning_options,
                text_tab_options{flow == nullptr ? 0.0F : flow->incremental_tab,
                    flow == nullptr ? 0.0F : flow->tab_origin, wrapping == PROGPU_NATIVE_TEXT_WRAPPING_EMERGENCY},
                excluded_flow->origin_y, {floating_flow->options->empty_ascent, floating_flow->options->empty_descent},
                floating_items, rectangles.first(excluded_flow->count), floating_collisions,
                exclusion_scratch, intervals, fragments, tab_advances, logical_scratch,
                positioned, native_lines, placements, floating_placements, floating_result,
                excluded_flow->options->maximum_attempts, &font_result)
            : excluded_flow != nullptr
            ? try_layout_excluded_logical_shaped_text_at(logical, glyph_breaks.first(logical_count),
                glyph_levels.first(logical_count), glyph_scales.first(logical_count),
                justify ? justification.first(logical_count) : std::span<const text_justification_class>{},
                item_metrics.first(logical_count), paragraph_level, positioning_options,
                text_tab_options{flow == nullptr ? 0.0F : flow->incremental_tab,
                    flow == nullptr ? 0.0F : flow->tab_origin, wrapping == PROGPU_NATIVE_TEXT_WRAPPING_EMERGENCY},
                excluded_flow->origin_y, rectangles, exclusion_scratch, intervals, fragments, tab_advances, logical_scratch,
                positioned, native_lines, placements, excluded_result,
                excluded_flow->options->maximum_attempts, &font_result)
            : hinted != nullptr && hinted->generation->has_source_geometry
            ? try_layout_source_measured_logical_shaped_text_retained(logical, glyph_breaks.first(logical_count),
                glyph_levels.first(logical_count), glyph_scales.first(logical_count), paragraph_level,
                positioning_options, {0.0F, 0.0F, hinted->generation->source_allow_emergency_break},
                logical_scratch, positioned, native_lines, positioned_count, written_lines,
                {hinted->generation->bidi_levels, hinted->generation->line_origins, hinted->generation->line_frames},
                {hinted->generation->source_maximum_width, hinted->generation->source_line_height,
                    hinted->generation->source_logical_metrics, hinted->generation->source_item_metrics,
                    hinted->generation->source_glyphs, hinted->generation->source_lines,
                    hinted->generation->source_fitting->lines}, &font_result)
            : hinted != nullptr
            ? try_layout_measured_logical_shaped_text_retained(logical, glyph_breaks.first(logical_count),
                glyph_levels.first(logical_count), glyph_scales.first(logical_count), paragraph_level,
                positioning_options, {}, tab_advances, logical_scratch, positioned, native_lines,
                positioned_count, written_lines,
                justify ? justification.first(logical_count) : std::span<const text_justification_class>{},
                item_metrics.first(logical_count),
                {hinted->generation->bidi_levels, hinted->generation->line_origins, hinted->generation->line_frames}, &font_result)
            : try_layout_measured_logical_shaped_text(
                logical.subspan(continuation_glyph_start),
                glyph_breaks.first(logical_count).subspan(continuation_glyph_start),
                glyph_levels.first(logical_count).subspan(continuation_glyph_start),
                style_count == 0U ? std::span<const float>{} : glyph_scales.first(logical_count).subspan(continuation_glyph_start),
                paragraph_level,
                positioning_options,
                text_tab_options{flow == nullptr ? 0.0F : flow->incremental_tab,
                    flow == nullptr ? 0.0F : flow->tab_origin, wrapping == PROGPU_NATIVE_TEXT_WRAPPING_EMERGENCY},
                tab_advances,
                logical_scratch,
                positioned,
                native_lines,
                positioned_count,
                written_lines,
                justify ? justification.first(logical_count).subspan(continuation_glyph_start) : std::span<const text_justification_class>{},
                inline_flow == nullptr ? std::span<const text_item_metrics>{} : item_metrics.first(logical_count).subspan(continuation_glyph_start),
                &font_result);
        if (!laid_out) {
            result->error_code = static_cast<std::uint32_t>(font_result);
            result->error_stage = PROGPU_NATIVE_TEXT_PARAGRAPH_STAGE_LAYOUT;
            return status_from_error(font_result);
        }
        if (excluded_flow != nullptr) {
            if (floating_flow != nullptr) excluded_result = floating_result.text;
            positioned_count = excluded_result.glyph_count;
            written_lines = excluded_result.fragment_count;
        }
        text_layout_metrics metrics{};
        const auto measure_lines = inline_flow == nullptr ? try_measure_positioned_text_lines : try_measure_measured_text_lines;
        const bool measured = excluded_flow != nullptr
            ? try_measure_fragment_text_lines(native_lines.first(written_lines), placements.first(written_lines),
                layout->maximum_width, metrics, &font_result)
            : measure_lines(native_lines.first(written_lines), layout->maximum_width, metrics, &font_result);
        if (!measured) {
            result->error_code = static_cast<std::uint32_t>(font_result);
            result->error_stage = PROGPU_NATIVE_TEXT_PARAGRAPH_STAGE_LAYOUT;
            return status_from_error(font_result);
        }
        for (std::uint32_t index = 0U; index < positioned_count; ++index) {
            const auto& source = positioned[index];
            const auto source_glyph_index = source.glyph_index == UINT32_MAX
                ? UINT32_MAX : source.glyph_index + continuation_glyph_start;
            // Layout owns the synthetic ellipsis, so it has no source-glyph
            // index. The public paragraph contract resolves that caller-
            // supplied glyph against the primary face.
            const auto font_index = source.glyph_index ==
                    std::numeric_limits<std::uint32_t>::max()
                ? 0U
                : glyph_font_indices[source_glyph_index];
            glyphs[index] = progpu_native_positioned_text_glyph{
                source_glyph_index,
                source.glyph_id,
                font_index,
                source.cluster,
                source.x,
                source.y,
                source.advance_x,
                source.advance_y};
        }
        for (std::uint32_t index = 0U; index < written_lines; ++index) {
            const auto& source = native_lines[index];
            lines[index] = progpu_native_positioned_text_line{
                source.glyph_start,
                source.glyph_count,
                source.input_start,
                source.input_end,
                source.width,
                source.baseline_y,
                source.height,
                static_cast<std::uint8_t>(source.clipped ? 1U : 0U),
                source.flags,
                0U,
                0U};
        }
        if (excluded_flow != nullptr)
            for (std::uint32_t index = 0; index < written_lines; ++index) {
                const auto& source = placements[index];
                excluded_flow->output[index] = {source.top, source.row_index, source.left, source.width, 0U};
            }
        result->glyph_count = positioned_count;
        result->line_count = written_lines;
        result->cached_plan_count =
            static_cast<std::uint32_t>(context->plans.size());
        result->plan_build_count = context->plan_build_count;
        result->content_width = metrics.content_width;
        result->content_height = metrics.content_height;
        result->measured_width = metrics.measured_width;
        result->measured_height = metrics.measured_height;
        result->scratch_bytes_used = arena.used();
        if (hinted != nullptr) {
            auto& retained = *hinted->generation;
            retained.glyphs.assign(positioned.begin(), positioned.begin() + positioned_count);
            retained.lines.assign(native_lines.begin(), native_lines.begin() + written_lines);
            retained.bidi_levels.resize(positioned_count);
            retained.line_origins.resize(written_lines);
            retained.line_frames.resize(written_lines);
            if (retained.has_source_geometry) {
                retained.source_glyphs.resize(positioned_count);
                retained.source_lines.resize(written_lines);
            }
            retained.metrics = metrics;
            retained.paragraph_result = *result;
        }
        if (widths != nullptr) { widths->minimum = intrinsic.minimum; widths->maximum = intrinsic.maximum; }
        if (floating_flow != nullptr) publish_floating_flow(floating_result, floating_placements, *floating_flow);
        return PROGPU_NATIVE_STATUS_SUCCESS;
    } catch (const std::bad_alloc&) {
        result->error_stage = PROGPU_NATIVE_TEXT_PARAGRAPH_STAGE_SHAPING;
        return PROGPU_NATIVE_STATUS_OUT_OF_MEMORY;
    } catch (...) {
        result->error_stage = PROGPU_NATIVE_TEXT_PARAGRAPH_STAGE_SHAPING;
        return PROGPU_NATIVE_STATUS_INTERNAL_ERROR;
    }
}

progpu_native_status progpu_native_text_context_get_paragraph_requirements(
    progpu_native_text_context* context, const progpu_native_text_shape_request* shaping,
    const progpu_native_text_layout_options* layout, progpu_native_text_paragraph_requirements* requirements) {
    return paragraph_requirements_core(context, shaping, layout, nullptr, 0U, nullptr, requirements);
}

progpu_native_status progpu_native_text_context_get_styled_paragraph_requirements(
    progpu_native_text_context* context, const progpu_native_text_shape_request* shaping,
    const progpu_native_text_layout_options* layout, const progpu_native_text_style_run* styles,
    std::uint32_t style_count, progpu_native_text_paragraph_requirements* requirements) {
    return paragraph_requirements_core(context, shaping, layout, styles, style_count, nullptr, requirements);
}

progpu_native_status progpu_native_text_context_layout_paragraph(
    progpu_native_text_context* context, const progpu_native_text_shape_request* shaping,
    const progpu_native_text_layout_options* layout, progpu_native_positioned_text_glyph* glyphs,
    std::uint32_t glyph_capacity, progpu_native_positioned_text_line* lines, std::uint32_t line_capacity,
    void* scratch, std::size_t scratch_size, progpu_native_text_paragraph_result* result) {
    return paragraph_layout_core(context, shaping, layout, nullptr, 0U, nullptr, glyphs, glyph_capacity, lines, line_capacity, scratch, scratch_size, result);
}

progpu_native_status progpu_native_text_context_layout_styled_paragraph(
    progpu_native_text_context* context, const progpu_native_text_shape_request* shaping,
    const progpu_native_text_layout_options* layout, const progpu_native_text_style_run* styles,
    std::uint32_t style_count, progpu_native_positioned_text_glyph* glyphs, std::uint32_t glyph_capacity,
    progpu_native_positioned_text_line* lines, std::uint32_t line_capacity,
    void* scratch, std::size_t scratch_size, progpu_native_text_paragraph_result* result) {
    return paragraph_layout_core(context, shaping, layout, styles, style_count, nullptr, glyphs, glyph_capacity, lines, line_capacity, scratch, scratch_size, result);
}

progpu_native_status progpu_native_text_context_get_flow_paragraph_requirements(
    progpu_native_text_context* context, const progpu_native_text_shape_request* shaping,
    const progpu_native_text_layout_options* layout, const progpu_native_text_style_run* styles,
    std::uint32_t style_count, const progpu_native_text_flow_options* flow,
    progpu_native_text_paragraph_requirements* requirements) {
    return paragraph_requirements_core(context, shaping, layout, styles, style_count, flow, requirements);
}

progpu_native_status progpu_native_text_context_layout_flow_paragraph(
    progpu_native_text_context* context, const progpu_native_text_shape_request* shaping,
    const progpu_native_text_layout_options* layout, const progpu_native_text_style_run* styles,
    std::uint32_t style_count, const progpu_native_text_flow_options* flow,
    progpu_native_positioned_text_glyph* glyphs, std::uint32_t glyph_capacity,
    progpu_native_positioned_text_line* lines, std::uint32_t line_capacity,
    void* scratch, std::size_t scratch_size, progpu_native_text_paragraph_result* result) {
    return paragraph_layout_core(context, shaping, layout, styles, style_count, flow,
        glyphs, glyph_capacity, lines, line_capacity, scratch, scratch_size, result);
}

progpu_native_status progpu_native_text_context_layout_configured_flow_paragraph(
    progpu_native_text_context* context, const progpu_native_text_shape_request* shaping,
    const progpu_native_text_layout_options* layout, const progpu_native_text_style_run* styles,
    std::uint32_t style_count, const progpu_native_text_flow_options* flow,
    progpu_native_positioned_text_glyph* glyphs, std::uint32_t glyph_capacity,
    progpu_native_positioned_text_line* lines, std::uint32_t line_capacity,
    void* scratch, std::size_t scratch_size, progpu_native_text_paragraph_result* result,
    std::uint32_t wrapping, progpu_native_text_intrinsic_widths* widths) {
    return paragraph_layout_core(context, shaping, layout, styles, style_count, flow,
        glyphs, glyph_capacity, lines, line_capacity, scratch, scratch_size, result, widths,
        wrapping);
}

progpu_native_status progpu_native_text_context_layout_continued_flow_paragraph(
    progpu_native_text_context* context, const progpu_native_text_shape_request* shaping,
    const progpu_native_text_layout_options* layout, const progpu_native_text_style_run* styles,
    std::uint32_t style_count, const progpu_native_text_flow_options* flow,
    const progpu_native_text_style_metrics* style_metrics,
    const progpu_native_text_inline_object* objects, std::uint32_t object_count,
    progpu_native_positioned_text_glyph* glyphs, std::uint32_t glyph_capacity,
    progpu_native_positioned_text_line* lines, std::uint32_t line_capacity,
    void* scratch, std::size_t scratch_size, progpu_native_text_paragraph_result* result,
    std::uint32_t wrapping, std::int32_t input_start, float collapse_width) {
    const inline_flow_policy policy{style_metrics, objects, object_count};
    return paragraph_layout_core(context, shaping, layout, styles, style_count, flow,
        glyphs, glyph_capacity, lines, line_capacity, scratch, scratch_size, result, nullptr,
        wrapping, collapse_width, style_metrics != nullptr || object_count != 0U ? &policy : nullptr,
        nullptr, nullptr, input_start < 0 ? -2 : input_start);
}

progpu_native_status progpu_native_text_context_get_inline_flow_paragraph_requirements(
    progpu_native_text_context* context, const progpu_native_text_shape_request* shaping,
    const progpu_native_text_layout_options* layout, const progpu_native_text_style_run* styles,
    std::uint32_t style_count, const progpu_native_text_flow_options* flow,
    const progpu_native_text_style_metrics* style_metrics,
    const progpu_native_text_inline_object* objects, std::uint32_t object_count,
    progpu_native_text_paragraph_requirements* requirements) {
    const inline_flow_policy policy{style_metrics, objects, object_count};
    return paragraph_requirements_core(context, shaping, layout, styles, style_count, flow, requirements, &policy);
}

progpu_native_status progpu_native_text_context_layout_inline_flow_paragraph(
    progpu_native_text_context* context, const progpu_native_text_shape_request* shaping,
    const progpu_native_text_layout_options* layout, const progpu_native_text_style_run* styles,
    std::uint32_t style_count, const progpu_native_text_flow_options* flow,
    const progpu_native_text_style_metrics* style_metrics,
    const progpu_native_text_inline_object* objects, std::uint32_t object_count,
    progpu_native_positioned_text_glyph* glyphs, std::uint32_t glyph_capacity,
    progpu_native_positioned_text_line* lines, std::uint32_t line_capacity,
    void* scratch, std::size_t scratch_size, progpu_native_text_paragraph_result* result,
    std::uint32_t wrapping, progpu_native_text_intrinsic_widths* widths) {
    const inline_flow_policy policy{style_metrics, objects, object_count};
    return paragraph_layout_core(context, shaping, layout, styles, style_count, flow,
        glyphs, glyph_capacity, lines, line_capacity, scratch, scratch_size, result, widths,
        wrapping, -1.0F, &policy);
}

progpu_native_status progpu_native_text_context_get_excluded_flow_paragraph_requirements(
    progpu_native_text_context* context, const progpu_native_text_shape_request* shaping,
    const progpu_native_text_layout_options* layout, const progpu_native_text_style_run* styles,
    std::uint32_t style_count, const progpu_native_text_flow_options* flow,
    const progpu_native_text_style_metrics* style_metrics,
    const progpu_native_text_inline_object* objects, std::uint32_t object_count,
    const progpu_native_text_exclusion_options* exclusion_options,
    const progpu_native_text_exclusion_rectangle* exclusions, std::uint32_t exclusion_count,
    progpu_native_text_paragraph_requirements* requirements) {
    const inline_flow_policy policy{style_metrics, objects, object_count};
    const excluded_flow_policy excluded{exclusion_options, exclusions, exclusion_count, nullptr, 0U};
    return paragraph_requirements_core(context, shaping, layout, styles, style_count, flow,
        requirements, &policy, &excluded);
}

progpu_native_status progpu_native_text_context_layout_excluded_flow_paragraph(
    progpu_native_text_context* context, const progpu_native_text_shape_request* shaping,
    const progpu_native_text_layout_options* layout, const progpu_native_text_style_run* styles,
    std::uint32_t style_count, const progpu_native_text_flow_options* flow,
    const progpu_native_text_style_metrics* style_metrics,
    const progpu_native_text_inline_object* objects, std::uint32_t object_count,
    const progpu_native_text_exclusion_options* exclusion_options,
    const progpu_native_text_exclusion_rectangle* exclusions, std::uint32_t exclusion_count,
    progpu_native_positioned_text_glyph* glyphs, std::uint32_t glyph_capacity,
    progpu_native_positioned_text_line* lines, std::uint32_t line_capacity,
    progpu_native_text_fragment_placement* fragments, std::uint32_t fragment_capacity,
    void* scratch, std::size_t scratch_size, progpu_native_text_paragraph_result* result,
    std::uint32_t wrapping, progpu_native_text_intrinsic_widths* widths) {
    const inline_flow_policy policy{style_metrics, objects, object_count};
    const excluded_flow_policy excluded{exclusion_options, exclusions, exclusion_count, fragments, fragment_capacity};
    return paragraph_layout_core(context, shaping, layout, styles, style_count, flow,
        glyphs, glyph_capacity, lines, line_capacity, scratch, scratch_size, result, widths,
        wrapping, -1.0F, &policy, &excluded);
}

progpu_native_status progpu_native_text_context_get_excluded_flow_paragraph_requirements_at(
    progpu_native_text_context* context, const progpu_native_text_shape_request* shaping,
    const progpu_native_text_layout_options* layout, const progpu_native_text_style_run* styles,
    std::uint32_t style_count, const progpu_native_text_flow_options* flow,
    const progpu_native_text_style_metrics* style_metrics,
    const progpu_native_text_inline_object* objects, std::uint32_t object_count,
    const progpu_native_text_exclusion_options* exclusion_options,
    const progpu_native_text_exclusion_rectangle* exclusions, std::uint32_t exclusion_count, double origin_y,
    progpu_native_text_paragraph_requirements* requirements) {
    const inline_flow_policy policy{style_metrics, objects, object_count};
    const excluded_flow_policy excluded{exclusion_options, exclusions, exclusion_count, nullptr, 0U, origin_y};
    return paragraph_requirements_core(context, shaping, layout, styles, style_count, flow,
        requirements, &policy, &excluded);
}

progpu_native_status progpu_native_text_context_layout_excluded_flow_paragraph_at(
    progpu_native_text_context* context, const progpu_native_text_shape_request* shaping,
    const progpu_native_text_layout_options* layout, const progpu_native_text_style_run* styles,
    std::uint32_t style_count, const progpu_native_text_flow_options* flow,
    const progpu_native_text_style_metrics* style_metrics,
    const progpu_native_text_inline_object* objects, std::uint32_t object_count,
    const progpu_native_text_exclusion_options* exclusion_options,
    const progpu_native_text_exclusion_rectangle* exclusions, std::uint32_t exclusion_count, double origin_y,
    progpu_native_positioned_text_glyph* glyphs, std::uint32_t glyph_capacity,
    progpu_native_positioned_text_line* lines, std::uint32_t line_capacity,
    progpu_native_text_fragment_placement* fragments, std::uint32_t fragment_capacity,
    void* scratch, std::size_t scratch_size, progpu_native_text_paragraph_result* result,
    std::uint32_t wrapping, progpu_native_text_intrinsic_widths* widths) {
    const inline_flow_policy policy{style_metrics, objects, object_count};
    const excluded_flow_policy excluded{exclusion_options, exclusions, exclusion_count, fragments, fragment_capacity, origin_y};
    return paragraph_layout_core(context, shaping, layout, styles, style_count, flow,
        glyphs, glyph_capacity, lines, line_capacity, scratch, scratch_size, result, widths,
        wrapping, -1.0F, &policy, &excluded);
}

progpu_native_status progpu_native_text_context_get_floating_flow_paragraph_requirements(
    progpu_native_text_context* context, const progpu_native_text_shape_request* shaping,
    const progpu_native_text_layout_options* layout, const progpu_native_text_style_run* styles,
    std::uint32_t style_count, const progpu_native_text_flow_options* flow,
    const progpu_native_text_style_metrics* style_metrics,
    const progpu_native_text_inline_object* objects, std::uint32_t object_count,
    const progpu_native_text_floating_options* options,
    const progpu_native_text_floating_item* events, std::uint32_t event_count,
    const progpu_native_text_exclusion_rectangle* exclusions, std::uint32_t exclusion_count,
    progpu_native_text_paragraph_requirements* requirements) {
    const progpu_native_text_exclusion_options fitting{sizeof(fitting), options == nullptr ? 0U : options->maximum_attempts, 0, 0};
    const inline_flow_policy inline_policy{style_metrics, objects, object_count};
    const excluded_flow_policy excluded{&fitting, exclusions, exclusion_count, nullptr, 0U,
        options == nullptr ? 0.0 : options->origin_y};
    const floating_flow_policy floating{options, events, event_count, nullptr, 0U, nullptr};
    return paragraph_requirements_core(context, shaping, layout, styles, style_count, flow,
        requirements, &inline_policy, &excluded, &floating);
}

progpu_native_status progpu_native_text_context_layout_floating_flow_paragraph(
    progpu_native_text_context* context, const progpu_native_text_shape_request* shaping,
    const progpu_native_text_layout_options* layout, const progpu_native_text_style_run* styles,
    std::uint32_t style_count, const progpu_native_text_flow_options* flow,
    const progpu_native_text_style_metrics* style_metrics,
    const progpu_native_text_inline_object* objects, std::uint32_t object_count,
    const progpu_native_text_floating_options* options,
    const progpu_native_text_floating_item* events, std::uint32_t event_count,
    const progpu_native_text_exclusion_rectangle* exclusions, std::uint32_t exclusion_count,
    progpu_native_positioned_text_glyph* glyphs, std::uint32_t glyph_capacity,
    progpu_native_positioned_text_line* lines, std::uint32_t line_capacity,
    progpu_native_text_fragment_placement* fragments, std::uint32_t fragment_capacity,
    progpu_native_text_floating_placement* floats, std::uint32_t float_capacity,
    void* scratch, std::size_t scratch_size, progpu_native_text_paragraph_result* result,
    progpu_native_text_floating_result* floating_result, std::uint32_t wrapping) {
    const progpu_native_text_exclusion_options fitting{sizeof(fitting), options == nullptr ? 0U : options->maximum_attempts, 0, 0};
    const inline_flow_policy inline_policy{style_metrics, objects, object_count};
    const excluded_flow_policy excluded{&fitting, exclusions, exclusion_count, fragments, fragment_capacity,
        options == nullptr ? 0.0 : options->origin_y};
    const floating_flow_policy floating{options, events, event_count, floats, float_capacity, floating_result};
    return paragraph_layout_core(context, shaping, layout, styles, style_count, flow,
        glyphs, glyph_capacity, lines, line_capacity, scratch, scratch_size, result, nullptr,
        wrapping, -1.0F, &inline_policy, &excluded, &floating);
}

progpu_native_status progpu_native_text_context_layout_collapsed_flow_paragraph(
    progpu_native_text_context* context, const progpu_native_text_shape_request* shaping,
    const progpu_native_text_layout_options* layout, const progpu_native_text_style_run* styles,
    std::uint32_t style_count, const progpu_native_text_flow_options* flow,
    progpu_native_positioned_text_glyph* glyphs, std::uint32_t glyph_capacity,
    progpu_native_positioned_text_line* lines, std::uint32_t line_capacity,
    void* scratch, std::size_t scratch_size, progpu_native_text_paragraph_result* result,
    std::uint32_t wrapping, float collapse_width) {
    if (!std::isfinite(collapse_width) || collapse_width < 0.0F)
        collapse_width = -2.0F; // Core initializes result before rejecting the request.
    return paragraph_layout_core(context, shaping, layout, styles, style_count, flow,
        glyphs, glyph_capacity, lines, line_capacity, scratch, scratch_size, result, nullptr,
        wrapping, collapse_width);
}

progpu_native_status progpu_native_text_context_capture_hinted_batch(
    progpu_native_text_context* context, const progpu_native_hinted_font_request* request,
    const std::int32_t* variation_coordinates_16_16, const std::uint32_t* glyph_indices,
    std::uint32_t glyph_count, progpu_native_hinted_batch** batch) {
    if (!valid_hinted_buffer(context, 1U) || !valid_hinted_buffer(request, 1U) ||
        !valid_hinted_buffer(batch, 1U) || request->abi_version != PROGPU_NATIVE_ABI_VERSION ||
        request->struct_size != sizeof(*request) || request->reserved != 0U ||
        request->x_pixels_per_em_26_6 == 0U || request->y_pixels_per_em_26_6 == 0U ||
        request->x_pixels_per_em_26_6 > static_cast<std::uint32_t>(std::numeric_limits<std::int32_t>::max()) ||
        request->y_pixels_per_em_26_6 > static_cast<std::uint32_t>(std::numeric_limits<std::int32_t>::max()) ||
        request->x_phase_26_6 >= 64U || request->y_phase_26_6 >= 64U ||
        (request->interpreter != 35U && request->interpreter != 40U) || request->variation_count > 65535U ||
        !valid_hinted_buffer(variation_coordinates_16_16, request->variation_count) ||
        !valid_hinted_buffer(glyph_indices, glyph_count) ||
        byte_ranges_overlap(batch, sizeof(*batch), request, sizeof(*request)) ||
        byte_ranges_overlap(batch, sizeof(*batch), variation_coordinates_16_16,
            static_cast<std::uint64_t>(request->variation_count) * sizeof(std::int32_t)) ||
        byte_ranges_overlap(batch, sizeof(*batch), glyph_indices,
            static_cast<std::uint64_t>(glyph_count) * sizeof(std::uint32_t)) ||
        hinted_publication_aliases_context(batch, *context)) return PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
    const progpu::native::text::hinted_font_configuration configuration{
        request->x_pixels_per_em_26_6, request->y_pixels_per_em_26_6,
        static_cast<progpu::native::text::font_hint_policy>(request->interpreter),
        request->x_phase_26_6, request->y_phase_26_6,
        {variation_coordinates_16_16, request->variation_count}};
    try {
        auto candidate = std::make_unique<progpu_native_hinted_batch>();
        progpu::native::text::hinted_font_error error{};
        if (!progpu::native::text::capture_context_hinted(context, request->font_index,
            configuration, {glyph_indices, glyph_count}, candidate->generation, error)) return hinted_status(error);
        *batch = candidate.release();
        return PROGPU_NATIVE_STATUS_SUCCESS;
    } catch (const std::bad_alloc&) {
        return PROGPU_NATIVE_STATUS_OUT_OF_MEMORY;
    } catch (...) {
        return PROGPU_NATIVE_STATUS_INTERNAL_ERROR;
    }
}

progpu_native_status progpu_native_hinted_batch_get_counts(
    const progpu_native_hinted_batch* batch, progpu_native_hinted_batch_counts* counts) {
    if (!valid_hinted_buffer(batch, 1U) || !valid_hinted_buffer(counts, 1U) ||
        batch->generation == nullptr || byte_ranges_overlap(batch, sizeof(*batch), counts, sizeof(*counts)))
        return PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
    return progpu::native::text::get_hinted_batch_counts(*batch->generation, *counts) ==
        progpu::native::text::hinted_transport_error::none ? PROGPU_NATIVE_STATUS_SUCCESS : PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
}

progpu_native_status progpu_native_hinted_batch_copy(
    const progpu_native_hinted_batch* batch, progpu_native_hinted_glyph* glyphs, std::uint32_t glyph_capacity,
    progpu_native_hinted_point* points, std::uint32_t point_capacity, std::uint8_t* tags, std::uint32_t tag_capacity,
    std::int32_t* contour_ends, std::uint32_t contour_capacity) {
    if (!valid_hinted_buffer(batch, 1U) || batch->generation == nullptr ||
        !valid_hinted_buffer(glyphs, glyph_capacity) || !valid_hinted_buffer(points, point_capacity) ||
        !valid_hinted_buffer(tags, tag_capacity) || !valid_hinted_buffer(contour_ends, contour_capacity) ||
        byte_ranges_overlap(batch, sizeof(*batch), glyphs, static_cast<std::uint64_t>(glyph_capacity) * sizeof(*glyphs)) ||
        byte_ranges_overlap(batch, sizeof(*batch), points, static_cast<std::uint64_t>(point_capacity) * sizeof(*points)) ||
        byte_ranges_overlap(batch, sizeof(*batch), tags, tag_capacity) ||
        byte_ranges_overlap(batch, sizeof(*batch), contour_ends, static_cast<std::uint64_t>(contour_capacity) * sizeof(*contour_ends)))
        return PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
    const auto copied = progpu::native::text::copy_hinted_batch(*batch->generation,
        {glyphs, glyph_capacity}, {points, point_capacity}, {tags, tag_capacity}, {contour_ends, contour_capacity},
        progpu::native::text::hinted_transport_policy::automatic);
    return copied ? PROGPU_NATIVE_STATUS_SUCCESS : PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
}

void progpu_native_hinted_batch_destroy(progpu_native_hinted_batch* batch) { delete batch; }

progpu_native_status progpu_native_text_context_shape_hinted_run(
    progpu_native_text_context* context, const progpu_native_hinted_font_request* hint_request,
    const std::int32_t* variation_coordinates_16_16, const progpu_native_text_shape_request* shape_request,
    progpu_native_hinted_run** run) {
    if (!valid_hinted_buffer(context, 1U) || !valid_hinted_buffer(run, 1U) ||
        !valid_hinted_shape_inputs(hint_request, variation_coordinates_16_16, shape_request) ||
        context->font_at(hint_request->font_index) == nullptr ||
        hinted_run_publication_aliases_inputs(run, sizeof(*run), *hint_request, variation_coordinates_16_16, *shape_request) ||
        hinted_run_publication_aliases_context(run, sizeof(*run), *context)) return PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
#if !defined(PROGPU_NATIVE_FONT_HINTING)
    return PROGPU_NATIVE_STATUS_UNSUPPORTED;
#else
    try {
        auto candidate = std::make_unique<progpu_native_hinted_run>();
        const auto status = shape_hinted_request_generation(context, *hint_request,
            variation_coordinates_16_16, *shape_request, candidate->generation);
        if (status != PROGPU_NATIVE_STATUS_SUCCESS) return status;
        *run = candidate.release();
        return PROGPU_NATIVE_STATUS_SUCCESS;
    } catch (const std::bad_alloc&) {
        return PROGPU_NATIVE_STATUS_OUT_OF_MEMORY;
    } catch (...) {
        return PROGPU_NATIVE_STATUS_INTERNAL_ERROR;
    }
#endif
}

progpu_native_status progpu_native_hinted_run_get_counts(const progpu_native_hinted_run* run,
    std::uint32_t* shaped_glyph_count, progpu_native_hinted_batch_counts* outline_counts) {
    if (!valid_hinted_run(run) || !valid_hinted_buffer(shaped_glyph_count, 1U) || !valid_hinted_buffer(outline_counts, 1U) ||
        byte_ranges_overlap(shaped_glyph_count, sizeof(*shaped_glyph_count), outline_counts, sizeof(*outline_counts)) ||
        hinted_run_output_aliases(*run, shaped_glyph_count, sizeof(*shaped_glyph_count)) ||
        hinted_run_output_aliases(*run, outline_counts, sizeof(*outline_counts))) return PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
    progpu_native_hinted_batch_counts counts{};
    if (get_hinted_batch_counts(*run->generation->batch, counts) != hinted_transport_error::none) return PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
    *shaped_glyph_count = static_cast<std::uint32_t>(run->generation->glyphs.size());
    *outline_counts = counts;
    return PROGPU_NATIVE_STATUS_SUCCESS;
}

progpu_native_status progpu_native_hinted_run_copy_glyphs(const progpu_native_hinted_run* run,
    progpu_native_text_shaping_glyph* glyphs, std::uint32_t glyph_capacity,
    std::uint32_t* descriptor_indices, std::uint32_t descriptor_capacity) {
    const auto glyph_bytes = static_cast<std::uint64_t>(glyph_capacity) * sizeof(*glyphs);
    const auto descriptor_bytes = static_cast<std::uint64_t>(descriptor_capacity) * sizeof(*descriptor_indices);
    if (!valid_hinted_run(run) || !valid_hinted_buffer(glyphs, glyph_capacity) ||
        !valid_hinted_buffer(descriptor_indices, descriptor_capacity) ||
        glyph_capacity < run->generation->glyphs.size() || descriptor_capacity < run->generation->glyphs.size() ||
        byte_ranges_overlap(glyphs, glyph_bytes, descriptor_indices, descriptor_bytes) ||
        hinted_run_output_aliases(*run, glyphs, glyph_bytes) || hinted_run_output_aliases(*run, descriptor_indices, descriptor_bytes))
        return PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
    if (!hinted_run_c_metrics_representable(*run->generation)) return PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
    for (std::size_t index = 0U; index < run->generation->glyphs.size(); ++index) {
        const auto& glyph = run->generation->glyphs[index];
        copy_hinted_run_glyph(glyph, glyphs[index]);
        descriptor_indices[index] = run->generation->descriptor_indices[index];
    }
    return PROGPU_NATIVE_STATUS_SUCCESS;
}

progpu_native_status progpu_native_hinted_run_copy_outlines(const progpu_native_hinted_run* run,
    progpu_native_hinted_glyph* glyphs, std::uint32_t glyph_capacity,
    progpu_native_hinted_point* points, std::uint32_t point_capacity, std::uint8_t* tags, std::uint32_t tag_capacity,
    std::int32_t* contour_ends, std::uint32_t contour_capacity) {
    if (!valid_hinted_run(run) || !valid_hinted_buffer(glyphs, glyph_capacity) || !valid_hinted_buffer(points, point_capacity) ||
        !valid_hinted_buffer(tags, tag_capacity) || !valid_hinted_buffer(contour_ends, contour_capacity) ||
        hinted_run_output_aliases(*run, glyphs, static_cast<std::uint64_t>(glyph_capacity) * sizeof(*glyphs)) ||
        hinted_run_output_aliases(*run, points, static_cast<std::uint64_t>(point_capacity) * sizeof(*points)) ||
        hinted_run_output_aliases(*run, tags, tag_capacity) ||
        hinted_run_output_aliases(*run, contour_ends, static_cast<std::uint64_t>(contour_capacity) * sizeof(*contour_ends)))
        return PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
    // The canonical transport preflights every pair of full-capacity outputs,
    // topology, fixed-width conversions and borrowed capture aliases atomically.
    const auto copied = copy_hinted_batch(*run->generation->batch, {glyphs, glyph_capacity}, {points, point_capacity},
        {tags, tag_capacity}, {contour_ends, contour_capacity}, hinted_transport_policy::automatic);
    return copied ? PROGPU_NATIVE_STATUS_SUCCESS : PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
}

void progpu_native_hinted_run_destroy(progpu_native_hinted_run* run) { delete run; }

} // extern "C"

namespace progpu::native::text {
namespace {

bool hinted_paragraph_aliases(const hinted_paragraph_generation& value,
    const void* output, std::uint64_t bytes) noexcept {
    const auto overlaps = [=](const void* data, std::size_t size) noexcept {
        return byte_ranges_overlap(output, bytes, data, size);
    };
    const auto vector_aliases = [&](const auto& values) noexcept {
        return overlaps(values.data(), values.capacity() * sizeof(values[0]));
    };
    if (overlaps(&value, sizeof(value)) || vector_aliases(value.source_input) ||
        vector_aliases(value.pre_context) || vector_aliases(value.post_context) || vector_aliases(value.features) ||
        vector_aliases(value.normalized_coordinates) || vector_aliases(value.shaping_input) ||
        vector_aliases(value.scalar_levels) || vector_aliases(value.script_runs) || vector_aliases(value.graphemes) ||
        vector_aliases(value.fallback_runs) || vector_aliases(value.font_sources) || vector_aliases(value.styles) ||
        vector_aliases(value.source_metrics) || vector_aliases(value.device_styles) || vector_aliases(value.source_styles) ||
        vector_aliases(value.source_style_metrics) || vector_aliases(value.source_item_metrics) ||
        vector_aliases(value.source_logical_metrics) || vector_aliases(value.source_glyphs) || vector_aliases(value.source_lines) ||
        (value.source_fitting != nullptr && value.source_fitting->allocation_aliases(output, static_cast<std::size_t>(bytes))) ||
        vector_aliases(value.runs) ||
        vector_aliases(value.logical_glyphs) || vector_aliases(value.logical_bidi_levels) ||
        vector_aliases(value.logical_font_indices) || vector_aliases(value.logical_source_scales) ||
        vector_aliases(value.glyph_scales) || vector_aliases(value.logical_owners) ||
        vector_aliases(value.line_break_classes) || vector_aliases(value.scalar_breaks) || vector_aliases(value.breaks_after) ||
        vector_aliases(value.justification_classes) || vector_aliases(value.item_metrics) ||
        vector_aliases(value.logical_cluster_ends) || vector_aliases(value.glyphs) || vector_aliases(value.lines) ||
        vector_aliases(value.positioned_owners) || vector_aliases(value.bidi_levels) ||
        vector_aliases(value.cluster_ends) || vector_aliases(value.line_origins) || vector_aliases(value.line_frames)) return true;
    for (const auto& style : value.device_styles)
        if (vector_aliases(style.variation_coordinates_16_16)) return true;
    for (const auto& source : value.font_sources)
        if (source != nullptr && (overlaps(source.get(), sizeof(*source)) || vector_aliases(source->bytes))) return true;
    for (const auto& run : value.runs) {
        if (run.generation == nullptr) continue;
        const progpu_native_hinted_run handle{run.generation};
        if (hinted_run_output_aliases(handle, output, bytes)) return true;
    }
    return false;
}

#if defined(PROGPU_NATIVE_FONT_HINTING)
bool retain_paragraph_source_coverage(hinted_paragraph_generation& value) {
    // New explicit source-map contract, not another shaping/bidi producer. Only
    // original scalar ranges define ends; admitted scalars define hard-break
    // semantics. Missing hard rows do not create carets or relabel source text.
    const auto& source = value.source_input;
    const auto& admitted = value.shaping_input;
    if (admitted.size() != source.size() || value.line_break_classes.size() != source.size()) return false;
    for (std::size_t i = 0U; i < source.size(); ++i)
        if (admitted[i].input_index != source[i].input_index || admitted[i].input_length != source[i].input_length) return false;
    const auto hard_boundary = [&](std::size_t index) noexcept {
        const auto kind = value.line_break_classes[index];
        return kind == unicode_line_break_class::mandatory || kind == unicode_line_break_class::next_line ||
            kind == unicode_line_break_class::carriage_return || kind == unicode_line_break_class::line_feed;
    };
    auto& ends = value.logical_cluster_ends;
    ends.resize(value.logical_glyphs.size());
    std::size_t scalar = 0U, glyph = 0U;
    while (glyph < value.logical_glyphs.size()) {
        const auto cluster = value.logical_glyphs[glyph].cluster;
        if (cluster < 0) return false;
        const auto start = static_cast<std::uint32_t>(cluster);
        while (scalar < source.size() && source[scalar].input_index < start) ++scalar;
        if (scalar == source.size() || source[scalar].input_index != start) return false;
        std::size_t next = glyph + 1U;
        while (next < value.logical_glyphs.size() && value.logical_glyphs[next].cluster == cluster) ++next;
        const auto limit = next == value.logical_glyphs.size() ? std::numeric_limits<std::int32_t>::max()
            : value.logical_glyphs[next].cluster;
        if (limit <= cluster) return false;
        const auto first_code_point = admitted[scalar].code_point;
        const bool starts_break = hard_boundary(scalar);
        std::int32_t end = cluster;
        for (std::size_t item = scalar; item < source.size() && source[item].input_index <
                static_cast<std::uint32_t>(limit); ++item) {
            const auto& record = source[item];
            const auto code_point = admitted[item].code_point;
            if (!starts_break && hard_boundary(item)) break;
            if (starts_break && item != scalar && !(first_code_point == 13U && item == scalar + 1U && code_point == 10U)) break;
            const auto record_end = static_cast<std::uint64_t>(record.input_index) + record.input_length;
            if (record_end > static_cast<std::uint32_t>(limit) || record_end >
                static_cast<std::uint32_t>(std::numeric_limits<std::int32_t>::max())) return false;
            end = static_cast<std::int32_t>(record_end);
        }
        if (end <= cluster) return false;
        std::fill(ends.begin() + glyph, ends.begin() + next, end);
        glyph = next;
    }
    value.positioned_owners.resize(value.glyphs.size());
    if (value.bidi_levels.size() != value.glyphs.size() || value.line_origins.size() != value.lines.size()) return false;
    value.cluster_ends.resize(value.glyphs.size());
    for (std::size_t i = 0U; i < value.glyphs.size(); ++i) {
        const auto index = value.glyphs[i].glyph_index;
        if (index >= value.logical_glyphs.size()) return false;
        value.positioned_owners[i] = value.logical_owners[index];
        value.cluster_ends[i] = ends[index];
    }
    return true;
}

#endif

} // namespace

bool hinted_paragraph_publication_disjoint(
    const std::shared_ptr<const hinted_paragraph_generation>* result,
    const progpu_native_text_paragraph_result* diagnostic) noexcept {
    return valid_hinted_buffer(result, 1U) && valid_hinted_buffer(diagnostic, 1U) &&
        !byte_ranges_overlap(result, sizeof(*result), diagnostic, sizeof(*diagnostic));
}

progpu_native_status try_layout_context_hinted_paragraph(
    progpu_native_text_context* context,
    const progpu_native_text_shape_request& shaping,
    const progpu_native_text_layout_options& layout,
    std::span<const progpu_native_text_style_run> styles,
    std::span<const progpu_native_text_style_metrics> source_metrics,
    std::span<const hinted_paragraph_style_configuration> device_styles,
    std::shared_ptr<const hinted_paragraph_generation>& result,
    progpu_native_text_paragraph_result& diagnostic,
    std::span<const hinted_source_style> source_styles,
    const hinted_source_paragraph_layout* source_layout) noexcept {
    // Validate address arithmetic before following any borrowed arrays. An alias
    // failure cannot write even the diagnostic or publication shared_ptr object.
    const auto valid_bytes = [](const void* data, std::size_t count) noexcept {
        return (count == 0U || data != nullptr) && count <=
            std::numeric_limits<std::uintptr_t>::max() - reinterpret_cast<std::uintptr_t>(data);
    };
    if (!valid_hinted_buffer(context, 1U) || !hinted_paragraph_publication_disjoint(&result, &diagnostic) ||
        !valid_hinted_buffer(&shaping, 1U) ||
        !valid_hinted_buffer(&layout, 1U) || !valid_bytes(&shaping, shaping.struct_size) ||
        !valid_bytes(&layout, layout.struct_size) || !valid_bytes(shaping.font_data, shaping.font_size) ||
        !valid_bytes(shaping.normalization_data, shaping.normalization_data_size) || styles.size() > UINT32_MAX ||
        source_metrics.size() > UINT32_MAX || device_styles.size() > UINT32_MAX || source_styles.size() > UINT32_MAX ||
        !valid_hinted_buffer(styles.data(), static_cast<std::uint32_t>(styles.size())) ||
        !valid_hinted_buffer(source_metrics.data(), static_cast<std::uint32_t>(source_metrics.size())) ||
        !valid_hinted_buffer(device_styles.data(), static_cast<std::uint32_t>(device_styles.size())) ||
        !valid_hinted_buffer(source_styles.data(), static_cast<std::uint32_t>(source_styles.size())) ||
        !valid_hinted_buffer(shaping.input, shaping.input_count) ||
        !valid_hinted_buffer(shaping.pre_context, shaping.pre_context_count) ||
        !valid_hinted_buffer(shaping.post_context, shaping.post_context_count) ||
        !valid_hinted_buffer(shaping.features, shaping.feature_count) ||
        !valid_hinted_buffer(shaping.normalized_coordinates, shaping.normalized_coordinate_count)) return PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
    if (source_layout != nullptr && (!valid_hinted_buffer(source_layout, 1U) ||
        source_layout->style_metrics.size() > UINT32_MAX ||
        !valid_hinted_buffer(source_layout->style_metrics.data(), static_cast<std::uint32_t>(source_layout->style_metrics.size()))))
        return PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
    for (const auto& device : device_styles)
        if (device.hinting.variation_coordinates_16_16.size() > 65535U ||
            !valid_hinted_buffer(device.hinting.variation_coordinates_16_16.data(),
                static_cast<std::uint32_t>(device.hinting.variation_coordinates_16_16.size()))) return PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
    const auto aliases_inputs = [&](const void* output, std::size_t bytes) noexcept {
        const auto overlaps = [=](const void* data, std::uint64_t size) noexcept { return byte_ranges_overlap(output, bytes, data, size); };
        if (overlaps(&shaping, std::max<std::size_t>(sizeof(shaping), shaping.struct_size)) ||
            overlaps(&layout, std::max<std::size_t>(sizeof(layout), layout.struct_size)) ||
            overlaps(styles.data(), styles.size_bytes()) || overlaps(source_metrics.data(), source_metrics.size_bytes()) ||
            overlaps(device_styles.data(), device_styles.size_bytes()) ||
            overlaps(source_styles.data(), source_styles.size_bytes()) ||
            (source_layout != nullptr && (overlaps(source_layout, sizeof(*source_layout)) ||
                overlaps(source_layout->style_metrics.data(), source_layout->style_metrics.size_bytes()))) ||
            overlaps(shaping.input, static_cast<std::uint64_t>(shaping.input_count) * sizeof(*shaping.input)) ||
            overlaps(shaping.pre_context, static_cast<std::uint64_t>(shaping.pre_context_count) * sizeof(*shaping.pre_context)) ||
            overlaps(shaping.post_context, static_cast<std::uint64_t>(shaping.post_context_count) * sizeof(*shaping.post_context)) ||
            overlaps(shaping.features, static_cast<std::uint64_t>(shaping.feature_count) * sizeof(*shaping.features)) ||
            overlaps(shaping.normalized_coordinates, static_cast<std::uint64_t>(shaping.normalized_coordinate_count) * sizeof(*shaping.normalized_coordinates)) ||
            overlaps(shaping.font_data, shaping.font_size) || overlaps(shaping.normalization_data, shaping.normalization_data_size) ||
            hinted_run_publication_aliases_context(output, bytes, *context)) return true;
        for (const auto& device : device_styles)
            if (overlaps(device.hinting.variation_coordinates_16_16.data(), device.hinting.variation_coordinates_16_16.size_bytes())) return true;
        return result != nullptr && hinted_paragraph_aliases(*result, output, bytes);
    };
    if (aliases_inputs(&result, sizeof(result)) || aliases_inputs(&diagnostic, sizeof(diagnostic))) return PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
    progpu_native_text_paragraph_result local{};
    local.struct_size = sizeof(local);
    const auto fail = [&](progpu_native_status status, font_error error = font_error::invalid_argument,
        std::uint32_t stage = PROGPU_NATIVE_TEXT_PARAGRAPH_STAGE_SHAPING) noexcept {
        local.error_code = static_cast<std::uint32_t>(error); local.error_stage = stage;
        diagnostic = local; return status;
    };
    if (shaping.struct_size != sizeof(shaping) || layout.struct_size != sizeof(layout) ||
        !valid_request(&shaping, false) || !valid_paragraph_layout_options(&layout) ||
        shaping.font_data != nullptr || shaping.font_size != 0U || shaping.face_index != 0U ||
        shaping.normalization_data != nullptr || shaping.normalization_data_size != 0U ||
        shaping.direction > PROGPU_NATIVE_TEXT_DIRECTION_RIGHT_TO_LEFT || layout.trimming != 0U ||
        styles.size() != source_metrics.size() || styles.size() != device_styles.size() ||
        (!source_styles.empty() && source_styles.size() != styles.size()) ||
        (shaping.input_count != 0U && styles.empty()) || (shaping.input_count == 0U && !styles.empty()) ||
        !valid_style_runs(*context, shaping, styles.data(), static_cast<std::uint32_t>(styles.size()))) return fail(PROGPU_NATIVE_STATUS_INVALID_ARGUMENT);
    if (source_layout != nullptr && (source_styles.size() != styles.size() ||
        source_layout->style_metrics.size() != styles.size() ||
        !std::isfinite(source_layout->maximum_width) || source_layout->maximum_width < 0.0 ||
        !std::isfinite(source_layout->line_height) || source_layout->line_height < 0.0 ||
        static_cast<float>(source_layout->maximum_width) != layout.maximum_width ||
        static_cast<float>(source_layout->line_height) != layout.line_height ||
        layout.alignment == PROGPU_NATIVE_TEXT_ALIGNMENT_JUSTIFY)) return fail(PROGPU_NATIVE_STATUS_INVALID_ARGUMENT);
    const inline_flow_policy source_policy{source_metrics.data(), nullptr, 0U};
    if (!valid_inline_flow(shaping, layout, static_cast<std::uint32_t>(styles.size()), &source_policy)) return fail(PROGPU_NATIVE_STATUS_INVALID_ARGUMENT);
    std::uint64_t previous_end = 0U;
    for (std::size_t i = 0U; i < shaping.input_count; ++i) {
        const auto& scalar = shaping.input[i];
        const auto end = static_cast<std::uint64_t>(scalar.input_index) + scalar.input_length;
        if (scalar.input_length == 0U || scalar.input_index < previous_end || end > INT32_MAX ||
            scalar.code_point == 9U || scalar.code_point == 0xFFFCU) return fail(PROGPU_NATIVE_STATUS_INVALID_ARGUMENT);
        previous_end = end;
    }
    for (std::size_t i = 0U; i < styles.size(); ++i) {
        if (source_layout != nullptr) {
            const auto metric = source_layout->style_metrics[i];
            if (!std::isfinite(metric.ascent) || metric.ascent < 0.0 ||
                !std::isfinite(metric.descent) || metric.descent < 0.0 ||
                static_cast<float>(metric.ascent) != source_metrics[i].ascent ||
                static_cast<float>(metric.descent) != source_metrics[i].descent) return fail(PROGPU_NATIVE_STATUS_INVALID_ARGUMENT);
        }
        const auto& device = device_styles[i];
        const auto& hint = device.hinting;
        const progpu_native_hinted_font_request request{PROGPU_NATIVE_ABI_VERSION, sizeof(progpu_native_hinted_font_request),
            device.font_index, hint.x_pixels_per_em_26_6, hint.y_pixels_per_em_26_6,
            static_cast<std::uint32_t>(hint.policy), hint.x_phase_26_6, hint.y_phase_26_6,
            static_cast<std::uint32_t>(hint.variation_coordinates_16_16.size()), 0U};
        const float scale = device.logical_units_per_physical_pixel / 64.0F;
        if (device.font_index != styles[i].font_index || device.source_scale != styles[i].scale ||
            !std::isfinite(device.logical_units_per_physical_pixel) || device.logical_units_per_physical_pixel <= 0.0F ||
            !std::isfinite(scale) || scale <= 0.0F ||
            !valid_hinted_shape_inputs(&request, hint.variation_coordinates_16_16.data(), &shaping)) return fail(PROGPU_NATIVE_STATUS_INVALID_ARGUMENT);
        if (!source_styles.empty()) {
            hinted_source_device_selection selected{};
            const auto* font = context->font_at(styles[i].font_index);
            sfnt_header_metrics header{};
            if (!resolve_hinted_source_device(source_styles[i], selected) || font == nullptr ||
                !font->try_get_header_metrics(header) || header.units_per_em == 0U ||
                static_cast<float>(source_styles[i].em_size) / static_cast<float>(header.units_per_em) != styles[i].scale ||
                selected.pixels_per_em_26_6 != hint.x_pixels_per_em_26_6 ||
                selected.pixels_per_em_26_6 != hint.y_pixels_per_em_26_6 ||
                selected.logical_units_per_physical_pixel != device.logical_units_per_physical_pixel)
                return fail(PROGPU_NATIVE_STATUS_INVALID_ARGUMENT);
        }
    }
#if !defined(PROGPU_NATIVE_FONT_HINTING)
    return fail(PROGPU_NATIVE_STATUS_UNSUPPORTED, font_error::none);
#else
    try {
        auto candidate = std::make_shared<hinted_paragraph_generation>();
        const auto copy = [](auto& output, const auto* input, std::uint32_t count) {
            if (count != 0U) output.assign(input, input + count);
        };
        copy(candidate->source_input, shaping.input, shaping.input_count);
        copy(candidate->pre_context, shaping.pre_context, shaping.pre_context_count);
        copy(candidate->post_context, shaping.post_context, shaping.post_context_count);
        copy(candidate->features, shaping.features, shaping.feature_count);
        copy(candidate->normalized_coordinates, shaping.normalized_coordinates, shaping.normalized_coordinate_count);
        candidate->shaping = shaping;
        candidate->shaping.input = candidate->source_input.data();
        candidate->shaping.pre_context = candidate->pre_context.data();
        candidate->shaping.post_context = candidate->post_context.data();
        candidate->shaping.features = candidate->features.data();
        candidate->shaping.normalized_coordinates = candidate->normalized_coordinates.data();
        candidate->layout = layout;
        candidate->styles.assign(styles.begin(), styles.end());
        candidate->source_metrics.assign(source_metrics.begin(), source_metrics.end());
        candidate->source_styles.assign(source_styles.begin(), source_styles.end());
        if (source_layout != nullptr) {
            candidate->has_source_geometry = true;
            candidate->source_maximum_width = source_layout->maximum_width;
            candidate->source_line_height = source_layout->line_height;
            candidate->source_allow_emergency_break = source_layout->allow_emergency_break;
            candidate->has_source_intrinsic_widths = source_layout->measure_intrinsic_widths;
            candidate->source_style_metrics.assign(source_layout->style_metrics.begin(), source_layout->style_metrics.end());
        }
        for (std::size_t i = 0U; i < context->font_count(); ++i) candidate->font_sources.push_back(context->source_at(i));
        for (const auto& device : device_styles) {
            const auto& hint = device.hinting;
            hinted_paragraph_owned_style_configuration owned{device.font_index, device.source_scale,
                hint.x_pixels_per_em_26_6, hint.y_pixels_per_em_26_6, hint.policy, hint.x_phase_26_6, hint.y_phase_26_6,
                device.logical_units_per_physical_pixel, {}};
            owned.variation_coordinates_16_16.assign(hint.variation_coordinates_16_16.begin(), hint.variation_coordinates_16_16.end());
            candidate->device_styles.push_back(std::move(owned));
        }
        const inline_flow_policy owned_source_policy{candidate->source_metrics.data(), nullptr, 0U};
        paragraph_capacities capacities{};
        font_error error = font_error::none;
        if (!build_flow_capacities(candidate->shaping, *context, capacities, error,
            has_number_substitution(candidate->styles.data(), static_cast<std::uint32_t>(candidate->styles.size())),
            &owned_source_policy, nullptr, nullptr)) return fail(status_from_error(error), error);
        std::vector<progpu_native_positioned_text_glyph> wire_glyphs(capacities.shaping.glyphs);
        std::vector<progpu_native_positioned_text_line> wire_lines(capacities.shaping.glyphs);
        std::vector<std::byte> scratch(capacities.scratch_bytes);
        const hinted_paragraph_policy policy{candidate.get()};
        const auto status = paragraph_layout_core(context, &candidate->shaping, &candidate->layout,
            candidate->styles.data(), static_cast<std::uint32_t>(candidate->styles.size()), nullptr,
            wire_glyphs.data(), static_cast<std::uint32_t>(wire_glyphs.size()),
            wire_lines.data(), static_cast<std::uint32_t>(wire_lines.size()), scratch.data(), scratch.size(), &local,
            nullptr, PROGPU_NATIVE_TEXT_WRAPPING_EMERGENCY, -1.0F, &owned_source_policy, nullptr, nullptr, -1, &policy);
        if (status != PROGPU_NATIVE_STATUS_SUCCESS) { diagnostic = local; return status; }
        // Empty input retains the original empty writer result, not invented rows.
        candidate->paragraph_level = static_cast<std::int8_t>(local.paragraph_level);
        candidate->paragraph_result = local;
        if (!retain_paragraph_source_coverage(*candidate))
            return fail(PROGPU_NATIVE_STATUS_INVALID_ARGUMENT, font_error::invalid_argument, PROGPU_NATIVE_TEXT_PARAGRAPH_STAGE_CLUSTER_MAP);
        diagnostic = local;
        result = std::move(candidate);
        return PROGPU_NATIVE_STATUS_SUCCESS;
    } catch (const std::bad_alloc&) {
        return fail(PROGPU_NATIVE_STATUS_OUT_OF_MEMORY, font_error::none);
    } catch (...) {
        return fail(PROGPU_NATIVE_STATUS_INTERNAL_ERROR, font_error::none);
    }
#endif
}

static hinted_paragraph_reflow_result reflow_hinted_paragraph_core(
    const hinted_paragraph_generation& source, std::int32_t input_start,
    double maximum_width, bool source_geometry) noexcept {
    hinted_paragraph_reflow_result result{};
    const auto logical_count = source.logical_glyphs.size();
    if (!std::isfinite(maximum_width) || maximum_width < 0.0 || maximum_width > std::numeric_limits<float>::max() || input_start < 0 ||
        source.has_source_geometry != source_geometry ||
        (source_geometry && (source.source_logical_metrics.size() != logical_count ||
            source.source_item_metrics.size() != logical_count || source.source_style_metrics.size() != source.styles.size())) ||
        source.lines.empty() || input_start < source.lines.front().input_start ||
        logical_count == 0U || logical_count > UINT32_MAX ||
        source.logical_bidi_levels.size() != logical_count || source.glyph_scales.size() != logical_count ||
        source.breaks_after.size() != logical_count || source.item_metrics.size() != logical_count ||
        source.logical_owners.size() != logical_count || source.logical_cluster_ends.size() != logical_count ||
        (!source.justification_classes.empty() && source.justification_classes.size() != logical_count) ||
        source.layout.trimming != PROGPU_NATIVE_TEXT_TRIMMING_NONE) return result;
    // The original producer's logical cluster order is retained, including
    // ligatures, marks, digit substitution and full-paragraph bidi context.
    const auto first = std::lower_bound(source.logical_glyphs.begin(), source.logical_glyphs.end(), input_start,
        [](const shaping_glyph& glyph, std::int32_t start) { return glyph.cluster < start; });
    if (first == source.logical_glyphs.end() || first->cluster != input_start) return result;
    const auto start = static_cast<std::uint32_t>(first - source.logical_glyphs.begin());
    try {
        auto candidate = std::make_shared<hinted_paragraph_generation>();
        // Explicit owned copies rebind the request's pointers. Font and hinted
        // run allocations retain their exact original shared identities; no
        // mutable source/context or earlier positioned view is borrowed.
        candidate->source_input = source.source_input;
        candidate->pre_context = source.pre_context;
        candidate->post_context = source.post_context;
        candidate->features = source.features;
        candidate->normalized_coordinates = source.normalized_coordinates;
        candidate->shaping = source.shaping;
        candidate->shaping.input = candidate->source_input.data();
        candidate->shaping.pre_context = candidate->pre_context.data();
        candidate->shaping.post_context = candidate->post_context.data();
        candidate->shaping.features = candidate->features.data();
        candidate->shaping.normalized_coordinates = candidate->normalized_coordinates.data();
        candidate->layout = source.layout;
        candidate->layout.maximum_width = static_cast<float>(maximum_width);
        candidate->shaping_input = source.shaping_input;
        candidate->source_digit_bidi = source.source_digit_bidi;
        candidate->paragraph_level = source.paragraph_level;
        candidate->scalar_levels = source.scalar_levels;
        candidate->script_runs = source.script_runs;
        candidate->graphemes = source.graphemes;
        candidate->fallback_runs = source.fallback_runs;
        candidate->font_sources = source.font_sources;
        candidate->styles = source.styles;
        candidate->source_metrics = source.source_metrics;
        candidate->device_styles = source.device_styles;
        candidate->source_styles = source.source_styles;
        candidate->has_source_geometry = source.has_source_geometry;
        candidate->source_allow_emergency_break = source.source_allow_emergency_break;
        candidate->source_maximum_width = source_geometry ? maximum_width : source.source_maximum_width;
        candidate->source_line_height = source.source_line_height;
        candidate->has_source_intrinsic_widths = source.has_source_intrinsic_widths;
        candidate->source_minimum_intrinsic_width = source.source_minimum_intrinsic_width;
        candidate->source_maximum_intrinsic_width = source.source_maximum_intrinsic_width;
        candidate->source_style_metrics = source.source_style_metrics;
        candidate->source_item_metrics = source.source_item_metrics;
        candidate->source_logical_metrics = source.source_logical_metrics;
        candidate->runs = source.runs;
        candidate->logical_glyphs = source.logical_glyphs;
        candidate->logical_bidi_levels = source.logical_bidi_levels;
        candidate->logical_font_indices = source.logical_font_indices;
        candidate->logical_source_scales = source.logical_source_scales;
        candidate->glyph_scales = source.glyph_scales;
        candidate->logical_owners = source.logical_owners;
        candidate->line_break_classes = source.line_break_classes;
        candidate->scalar_breaks = source.scalar_breaks;
        candidate->breaks_after = source.breaks_after;
        candidate->justification_classes = source.justification_classes;
        candidate->item_metrics = source.item_metrics;
        candidate->logical_cluster_ends = source.logical_cluster_ends;

        if (source_geometry) {
            const auto fitted = fit_hinted_source_paragraph(*candidate, start, maximum_width);
            if (fitted.status != PROGPU_NATIVE_STATUS_SUCCESS) { result.status = fitted.status; return result; }
            candidate->source_fitting = fitted.generation;
            candidate->source_logical_metrics = fitted.generation->metrics;
        }

        const auto logical = std::span(candidate->logical_glyphs).subspan(start);
        const auto breaks = std::span(candidate->breaks_after).subspan(start);
        const auto scales = std::span(candidate->glyph_scales).subspan(start);
        const auto options = convert_paragraph_layout_options(candidate->layout, candidate->paragraph_level);
        text_layout_requirements required{};
        font_error error = font_error::none;
        if (source_geometry) {
            // One glyph/line per remaining logical occurrence is the original
            // producer bound; the common double scanner chooses actual counts.
            required = {static_cast<std::uint32_t>(logical.size()), static_cast<std::uint32_t>(logical.size())};
        } else if (!try_get_scaled_text_layout_requirements(logical, breaks, scales, options, required, &error)) {
            result.status = status_from_error(error); return result;
        }
        candidate->glyphs.resize(required.glyph_capacity);
        candidate->bidi_levels.resize(required.glyph_capacity);
        candidate->lines.resize(required.line_capacity);
        candidate->line_origins.resize(required.line_capacity);
        candidate->line_frames.resize(required.line_capacity);
        if (source_geometry) {
            candidate->source_glyphs.resize(required.glyph_capacity);
            candidate->source_lines.resize(required.line_capacity);
        }
        std::vector<text_visual_cluster_group> groups(required.glyph_capacity);
        std::vector<std::uint32_t> indices(required.glyph_capacity);
        std::uint32_t glyph_count = 0U, line_count = 0U;
        const bool laid_out = source_geometry
            ? try_layout_source_measured_logical_shaped_text_retained(logical, breaks,
                std::span(candidate->logical_bidi_levels).subspan(start), scales, candidate->paragraph_level,
                options, {0.0F, 0.0F, candidate->source_allow_emergency_break}, {groups, indices},
                candidate->glyphs, candidate->lines, glyph_count, line_count,
                {candidate->bidi_levels, candidate->line_origins, candidate->line_frames},
                {candidate->source_maximum_width, candidate->source_line_height,
                    std::span(candidate->source_logical_metrics).subspan(start),
                    std::span(candidate->source_item_metrics).subspan(start),
                    candidate->source_glyphs, candidate->source_lines, candidate->source_fitting->lines}, &error)
            : try_layout_measured_logical_shaped_text_retained(logical, breaks,
                std::span(candidate->logical_bidi_levels).subspan(start), scales, candidate->paragraph_level,
                options, {}, {}, {groups, indices}, candidate->glyphs, candidate->lines, glyph_count, line_count,
                candidate->justification_classes.empty() ? std::span<const text_justification_class>{}
                    : std::span<const text_justification_class>(candidate->justification_classes).subspan(start),
                std::span(candidate->item_metrics).subspan(start),
                {candidate->bidi_levels, candidate->line_origins, candidate->line_frames}, &error);
        if (!laid_out) {
            result.status = status_from_error(error); return result;
        }
        candidate->glyphs.resize(glyph_count);
        candidate->bidi_levels.resize(glyph_count);
        candidate->lines.resize(line_count);
        candidate->line_origins.resize(line_count);
        candidate->line_frames.resize(line_count);
        if (source_geometry) {
            candidate->source_glyphs.resize(glyph_count);
            candidate->source_lines.resize(line_count);
        }
        candidate->positioned_owners.reserve(glyph_count);
        candidate->cluster_ends.reserve(glyph_count);
        for (auto& glyph : candidate->glyphs) {
            if (glyph.glyph_index >= logical.size()) return result;
            glyph.glyph_index += start;
            candidate->positioned_owners.push_back(candidate->logical_owners[glyph.glyph_index]);
            candidate->cluster_ends.push_back(candidate->logical_cluster_ends[glyph.glyph_index]);
        }
        if (candidate->lines.empty() || candidate->lines.front().input_start != input_start) return result;
        if (!try_measure_measured_text_lines(candidate->lines, static_cast<float>(maximum_width), candidate->metrics, &error)) {
            result.status = status_from_error(error); return result;
        }
        // Full shaping diagnostics still describe the same original producer.
        // Only placement counts/extents change. No shaping/scratch work is
        // invented for this retained operation.
        candidate->paragraph_result = source.paragraph_result;
        auto& diagnostic = candidate->paragraph_result;
        diagnostic.glyph_count = glyph_count;
        diagnostic.line_count = line_count;
        diagnostic.content_width = candidate->metrics.content_width;
        diagnostic.content_height = candidate->metrics.content_height;
        diagnostic.measured_width = candidate->metrics.measured_width;
        diagnostic.measured_height = candidate->metrics.measured_height;
        diagnostic.scratch_bytes_used = 0U;
        result.generation = std::move(candidate);
        result.status = PROGPU_NATIVE_STATUS_SUCCESS;
    } catch (const std::bad_alloc&) {
        result.status = PROGPU_NATIVE_STATUS_OUT_OF_MEMORY;
    } catch (...) {
        result.status = PROGPU_NATIVE_STATUS_INTERNAL_ERROR;
    }
    return result;
}

hinted_paragraph_reflow_result reflow_hinted_paragraph(
    const hinted_paragraph_generation& source, std::int32_t input_start, float maximum_width) noexcept {
    return reflow_hinted_paragraph_core(source, input_start, maximum_width, false);
}

hinted_paragraph_reflow_result reflow_hinted_source_paragraph(
    const hinted_paragraph_generation& source, std::int32_t input_start, double maximum_width) noexcept {
    return reflow_hinted_paragraph_core(source, input_start, maximum_width, true);
}

} // namespace progpu::native::text

namespace {

// Fixed records describe whole caller capacities, never merely the used prefix.
struct hinted_paragraph_output_range final {
    const void* data = nullptr;
    std::uint64_t bytes = 0U;
};

bool valid_hinted_paragraph(const progpu_native_hinted_paragraph* handle) noexcept {
    if (!valid_hinted_buffer(handle, 1U) || handle->generation == nullptr || handle->interaction == nullptr ||
        handle->interaction->paragraph() != handle->generation) return false;
    const auto& value = *handle->generation;
    const auto logical = value.logical_glyphs.size();
    const auto positioned = value.glyphs.size();
    const auto bounded = [](std::size_t size) noexcept { return size <= UINT32_MAX; };
    if (!bounded(value.source_input.size()) || !bounded(value.shaping_input.size()) || !bounded(value.styles.size()) ||
        !bounded(value.runs.size()) || !bounded(logical) || !bounded(positioned) || !bounded(value.lines.size()) ||
        !bounded(handle->interaction->boxes().size()) || !bounded(handle->interaction->carets().size()) ||
        value.shaping_input.size() != value.source_input.size() || value.scalar_levels.size() != value.source_input.size() ||
        value.source_metrics.size() != value.styles.size() || value.device_styles.size() != value.styles.size() ||
        value.logical_owners.size() != logical || value.logical_cluster_ends.size() != logical ||
        value.logical_bidi_levels.size() != logical || value.glyph_scales.size() != logical ||
        value.logical_font_indices.size() != logical || value.positioned_owners.size() != positioned ||
        value.cluster_ends.size() != positioned || value.bidi_levels.size() != positioned ||
        value.line_origins.size() != value.lines.size()) return false;
    for (const auto& run : value.runs)
        if (run.generation == nullptr || run.generation->batch == nullptr ||
            run.generation->source_descriptor_count > UINT32_MAX ||
            run.generation->source_descriptor_count > run.generation->batch->glyphs.size()) return false;
    for (const auto& glyph : value.glyphs)
        if (glyph.glyph_index >= logical) return false;
    return true;
}

bool hinted_paragraph_handle_aliases(const progpu_native_hinted_paragraph& handle,
    const void* output, std::uint64_t bytes) noexcept {
    return byte_ranges_overlap(output, bytes, &handle, sizeof(handle)) ||
        progpu::native::text::hinted_paragraph_aliases(*handle.generation, output, bytes) ||
        handle.interaction->allocation_aliases(output, static_cast<std::size_t>(bytes));
}

bool valid_hinted_paragraph_frame(const progpu_native_hinted_paragraph_frame* handle) noexcept {
    return valid_hinted_buffer(handle, 1U) && handle->generation != nullptr && handle->interaction != nullptr &&
        handle->generation->paragraph() != nullptr &&
        handle->generation->paragraph() == handle->interaction->paragraph();
}

bool hinted_paragraph_frame_aliases(const progpu_native_hinted_paragraph_frame& handle,
    const void* output, std::uint64_t bytes) noexcept {
    return byte_ranges_overlap(output, bytes, &handle, sizeof(handle)) ||
        handle.generation->allocation_aliases(output, static_cast<std::size_t>(bytes)) ||
        handle.interaction->allocation_aliases(output, static_cast<std::size_t>(bytes)) ||
        progpu::native::text::hinted_paragraph_aliases(*handle.generation->paragraph(), output, bytes);
}

bool hinted_paragraph_shape_inputs_valid(const progpu_native_text_shape_request* shaping) noexcept {
    return valid_hinted_buffer(shaping, 1U) && shaping->struct_size == sizeof(*shaping) &&
        shaping->font_data == nullptr && shaping->font_size == 0U && shaping->face_index == 0U &&
        shaping->normalization_data == nullptr && shaping->normalization_data_size == 0U &&
        valid_hinted_buffer(shaping->input, shaping->input_count) &&
        valid_hinted_buffer(shaping->pre_context, shaping->pre_context_count) &&
        valid_hinted_buffer(shaping->post_context, shaping->post_context_count) &&
        valid_hinted_buffer(shaping->features, shaping->feature_count) &&
        valid_hinted_buffer(shaping->normalized_coordinates, shaping->normalized_coordinate_count);
}

bool hinted_paragraph_factory_aliases_inputs(const void* output, std::uint64_t bytes,
    const progpu_native_text_context& context, const progpu_native_text_shape_request& shaping,
    const progpu_native_text_layout_options& layout,
    const progpu_native_text_style_run* styles, std::uint32_t style_count,
    const progpu_native_text_style_metrics* metrics,
    const progpu_native_hinted_paragraph_device_style* devices, std::uint32_t device_count,
    const std::int32_t* axes, std::uint32_t axis_count) noexcept {
    const auto overlaps = [=](const void* data, std::uint64_t size) noexcept {
        return byte_ranges_overlap(output, bytes, data, size);
    };
    return overlaps(&shaping, sizeof(shaping)) || overlaps(&layout, sizeof(layout)) ||
        overlaps(shaping.input, static_cast<std::uint64_t>(shaping.input_count) * sizeof(*shaping.input)) ||
        overlaps(shaping.pre_context, static_cast<std::uint64_t>(shaping.pre_context_count) * sizeof(*shaping.pre_context)) ||
        overlaps(shaping.post_context, static_cast<std::uint64_t>(shaping.post_context_count) * sizeof(*shaping.post_context)) ||
        overlaps(shaping.features, static_cast<std::uint64_t>(shaping.feature_count) * sizeof(*shaping.features)) ||
        overlaps(shaping.normalized_coordinates, static_cast<std::uint64_t>(shaping.normalized_coordinate_count) * sizeof(*shaping.normalized_coordinates)) ||
        overlaps(styles, static_cast<std::uint64_t>(style_count) * sizeof(*styles)) ||
        overlaps(metrics, static_cast<std::uint64_t>(style_count) * sizeof(*metrics)) ||
        overlaps(devices, static_cast<std::uint64_t>(device_count) * sizeof(*devices)) ||
        overlaps(axes, static_cast<std::uint64_t>(axis_count) * sizeof(*axes)) ||
        hinted_run_publication_aliases_context(output, bytes, context);
}

template<class T>
hinted_paragraph_output_range hinted_paragraph_range(const T* output, std::uint32_t capacity) noexcept {
    return {output, static_cast<std::uint64_t>(capacity) * sizeof(T)};
}

progpu_native_hinted_paragraph_glyph_owner copy_paragraph_owner(const hinted_paragraph_glyph_owner& source) noexcept {
    return {source.run_index, source.run_glyph_index, source.descriptor_index};
}

template<class T>
void copy_paragraph_vector(const std::vector<T>& source, T* output) noexcept {
    if (!source.empty()) std::copy(source.begin(), source.end(), output);
}

bool hinted_glyph_resource_format_bounded(const hinted_paragraph_generation& paragraph) noexcept {
    const auto bounded = [](std::size_t size) noexcept { return size <= UINT32_MAX; };
    if (!bounded(paragraph.font_sources.size()) || !bounded(paragraph.pre_context.size()) ||
        !bounded(paragraph.post_context.size()) || !bounded(paragraph.features.size()) ||
        !bounded(paragraph.normalized_coordinates.size())) return false;
    std::size_t byte_count = 0U, axis_count = 0U;
    for (const auto& font : paragraph.font_sources) {
        if (font == nullptr || font->bytes.empty() || font->bytes.size() > UINT32_MAX - byte_count) return false;
        byte_count += font->bytes.size();
    }
    for (const auto& style : paragraph.device_styles) {
        if (style.variation_coordinates_16_16.size() > 65535U ||
            style.variation_coordinates_16_16.size() > UINT32_MAX - axis_count) return false;
        axis_count += style.variation_coordinates_16_16.size();
    }
    return true;
}

template<class Container>
auto hinted_glyph_wire_data(const Container& values) noexcept {
    return values.empty() ? nullptr : values.data();
}

bool cache_hinted_glyph_resource_view(progpu_native_hinted_glyph_resource& handle) {
    const auto& resource = *handle.generation;
    const auto& paragraph = *resource.paragraph();
    const auto binding = resource.binding_view();
    const auto bounded = [](std::size_t size) noexcept { return size <= UINT32_MAX; };
    if (!bounded(binding.fonts.size()) || !bounded(binding.font_bytes.size()) ||
        !bounded(resource.outlines().size()) || !bounded(resource.segments().size()) ||
        !bounded(resource.source_outline_indices().size()) || !bounded(resource.run_outline_indices().size()) ||
        binding.fonts.size() != paragraph.font_sources.size() || binding.runs.size() != paragraph.runs.size() ||
        binding.glyphs.size() != paragraph.glyphs.size() || resource.run_slices().size() != paragraph.runs.size() ||
        resource.outline_owners().size() != resource.outlines().size() ||
        resource.positioned_outline_indices().size() != paragraph.glyphs.size()) return false;

    handle.device_styles.reserve(paragraph.device_styles.size());
    for (const auto& style : paragraph.device_styles) {
        const auto start = static_cast<std::uint32_t>(handle.variation_coordinates_16_16.size());
        const auto count = static_cast<std::uint32_t>(style.variation_coordinates_16_16.size());
        handle.device_styles.push_back({style.font_index, style.source_scale, style.logical_units_per_physical_pixel,
            style.x_pixels_per_em_26_6, style.y_pixels_per_em_26_6, static_cast<std::uint32_t>(style.policy),
            style.x_phase_26_6, style.y_phase_26_6, start, count, 0U});
        handle.variation_coordinates_16_16.insert(handle.variation_coordinates_16_16.end(),
            style.variation_coordinates_16_16.begin(), style.variation_coordinates_16_16.end());
    }
    handle.run_slices.reserve(resource.run_slices().size());
    for (const auto& slice : resource.run_slices())
        handle.run_slices.push_back({slice.source_start, slice.source_count, slice.run_start, slice.run_count,
            slice.outline_start, slice.outline_count, slice.segment_start, slice.segment_count});
    handle.outline_owners.reserve(resource.outline_owners().size());
    for (const auto& owner : resource.outline_owners())
        handle.outline_owners.push_back({owner.run_index, owner.descriptor_index});
    handle.admitted_scalars.reserve(paragraph.shaping_input.size());
    handle.scalar_levels.reserve(paragraph.scalar_levels.size());
    for (std::size_t index = 0U; index < paragraph.shaping_input.size(); ++index) {
        const auto& scalar = paragraph.shaping_input[index];
        handle.admitted_scalars.push_back({scalar.code_point, scalar.input_index, scalar.input_length,
            scalar.canonical_combining_class, scalar.reserved, scalar.script.value});
        const auto& level = paragraph.scalar_levels[index];
        handle.scalar_levels.push_back({level.input_index, level.input_length, level.level, level.reserved});
    }
    handle.logical_glyphs.reserve(paragraph.logical_glyphs.size());
    handle.logical_owners.reserve(paragraph.logical_owners.size());
    for (std::size_t index = 0U; index < paragraph.logical_glyphs.size(); ++index) {
        const auto& glyph = paragraph.logical_glyphs[index];
        // The original logical writer has already selected C Y-down metrics.
        handle.logical_glyphs.push_back({glyph.glyph_id, glyph.code_point, glyph.cluster,
            static_cast<std::uint32_t>(glyph.flags), glyph.advance_x, glyph.advance_y, glyph.offset_x, glyph.offset_y});
        handle.logical_owners.push_back(copy_paragraph_owner(paragraph.logical_owners[index]));
    }
    handle.positioned_owners.reserve(paragraph.positioned_owners.size());
    for (const auto& owner : paragraph.positioned_owners)
        handle.positioned_owners.push_back(copy_paragraph_owner(owner));
    handle.lines.reserve(paragraph.lines.size());
    for (const auto& line : paragraph.lines)
        handle.lines.push_back({line.glyph_start, line.glyph_count, line.input_start, line.input_end, line.width,
            line.baseline_y, line.height, static_cast<std::uint8_t>(line.clipped ? 1U : 0U), line.flags, 0U, 0U});
    if (paragraph.line_frames.size() != paragraph.lines.size()) return false;
    handle.line_frames.reserve(paragraph.line_frames.size());
    for (const auto& frame : paragraph.line_frames)
        handle.line_frames.push_back({frame.top, frame.baseline_offset, frame.measured ? 1U : 0U});
    handle.boxes.reserve(handle.interaction->boxes().size());
    for (const auto& box : handle.interaction->boxes())
        handle.boxes.push_back({box.input_start, box.input_end, box.line_index, box.bidi_level,
            0U, 0U, 0U, box.x, box.y, box.width, box.height});
    handle.carets.reserve(handle.interaction->carets().size());
    for (const auto& caret : handle.interaction->carets())
        handle.carets.push_back({caret.input_position, caret.line_index, caret.x, caret.y, caret.height,
            caret.bidi_level, static_cast<std::uint8_t>(caret.trailing ? 1U : 0U), 0U, 0U});

    // Publish pointers only after every cache allocation is complete. All other
    // spans are already C records in owners retained by this exact resource.
    auto& view = handle.view;
    view.abi_version = PROGPU_NATIVE_ABI_VERSION; view.struct_size = sizeof(view);
    view.dpi_scale = resource.dpi_scale(); view.projection_policy = static_cast<std::uint32_t>(resource.projection_policy());
    view.coverage = static_cast<std::uint32_t>(resource.coverage());
    view.source_digit_bidi = paragraph.source_digit_bidi ? 1U : 0U; view.paragraph_level = paragraph.paragraph_level;
    view.shaping_direction = paragraph.shaping.direction; view.shaping_flags = paragraph.shaping.flags;
    view.counts = {static_cast<std::uint32_t>(paragraph.source_input.size()),
        static_cast<std::uint32_t>(paragraph.shaping_input.size()), static_cast<std::uint32_t>(paragraph.styles.size()),
        static_cast<std::uint32_t>(paragraph.runs.size()), static_cast<std::uint32_t>(paragraph.logical_glyphs.size()),
        static_cast<std::uint32_t>(paragraph.glyphs.size()), static_cast<std::uint32_t>(paragraph.lines.size()),
        static_cast<std::uint32_t>(handle.boxes.size()), static_cast<std::uint32_t>(handle.carets.size())};
    view.result = paragraph.paragraph_result; view.layout = paragraph.layout;
    view.font_sources = hinted_glyph_wire_data(binding.fonts); view.font_source_count = static_cast<std::uint32_t>(binding.fonts.size());
    view.font_bytes = hinted_glyph_wire_data(binding.font_bytes); view.font_byte_count = static_cast<std::uint32_t>(binding.font_bytes.size());
    view.device_styles = hinted_glyph_wire_data(handle.device_styles);
    view.variation_coordinates_16_16 = hinted_glyph_wire_data(handle.variation_coordinates_16_16);
    view.variation_coordinate_count = static_cast<std::uint32_t>(handle.variation_coordinates_16_16.size());
    view.normalized_coordinates = hinted_glyph_wire_data(paragraph.normalized_coordinates);
    view.normalized_coordinate_count = static_cast<std::uint32_t>(paragraph.normalized_coordinates.size());
    view.outlines = hinted_glyph_wire_data(resource.outlines()); view.outline_count = static_cast<std::uint32_t>(resource.outlines().size());
    view.segments = hinted_glyph_wire_data(resource.segments()); view.segment_count = static_cast<std::uint32_t>(resource.segments().size());
    view.run_slices = hinted_glyph_wire_data(handle.run_slices);
    view.source_outline_indices = hinted_glyph_wire_data(resource.source_outline_indices());
    view.source_outline_count = static_cast<std::uint32_t>(resource.source_outline_indices().size());
    view.run_outline_indices = hinted_glyph_wire_data(resource.run_outline_indices());
    view.run_outline_count = static_cast<std::uint32_t>(resource.run_outline_indices().size());
    view.outline_owners = hinted_glyph_wire_data(handle.outline_owners);
    view.positioned_outline_indices = hinted_glyph_wire_data(resource.positioned_outline_indices());
    view.source_scalars = hinted_glyph_wire_data(paragraph.source_input);
    view.admitted_scalars = hinted_glyph_wire_data(handle.admitted_scalars);
    view.scalar_levels = hinted_glyph_wire_data(handle.scalar_levels);
    view.styles = hinted_glyph_wire_data(paragraph.styles); view.source_metrics = hinted_glyph_wire_data(paragraph.source_metrics);
    view.runs = hinted_glyph_wire_data(binding.runs); view.logical_glyphs = hinted_glyph_wire_data(handle.logical_glyphs);
    view.logical_owners = hinted_glyph_wire_data(handle.logical_owners);
    view.logical_cluster_ends = hinted_glyph_wire_data(paragraph.logical_cluster_ends);
    view.logical_bidi_levels = hinted_glyph_wire_data(paragraph.logical_bidi_levels);
    view.glyph_scales = hinted_glyph_wire_data(paragraph.glyph_scales);
    view.positioned_glyphs = hinted_glyph_wire_data(binding.glyphs); view.positioned_owners = hinted_glyph_wire_data(handle.positioned_owners);
    view.positioned_cluster_ends = hinted_glyph_wire_data(paragraph.cluster_ends);
    view.positioned_bidi_levels = hinted_glyph_wire_data(paragraph.bidi_levels);
    view.lines = hinted_glyph_wire_data(handle.lines); view.line_origins = hinted_glyph_wire_data(paragraph.line_origins);
    view.boxes = hinted_glyph_wire_data(handle.boxes); view.carets = hinted_glyph_wire_data(handle.carets);
    view.pre_context = hinted_glyph_wire_data(paragraph.pre_context); view.pre_context_count = static_cast<std::uint32_t>(paragraph.pre_context.size());
    view.post_context = hinted_glyph_wire_data(paragraph.post_context); view.post_context_count = static_cast<std::uint32_t>(paragraph.post_context.size());
    view.features = hinted_glyph_wire_data(paragraph.features); view.feature_count = static_cast<std::uint32_t>(paragraph.features.size());
    return true;
}

bool valid_hinted_glyph_resource(const progpu_native_hinted_glyph_resource* handle) noexcept {
    return valid_hinted_buffer(handle, 1U) && handle->generation != nullptr && handle->interaction != nullptr &&
        handle->generation->paragraph() != nullptr && handle->generation->paragraph() == handle->interaction->paragraph() &&
        handle->view.abi_version == PROGPU_NATIVE_ABI_VERSION && handle->view.struct_size == sizeof(handle->view);
}

bool hinted_glyph_resource_aliases(const progpu_native_hinted_glyph_resource& handle,
    const void* output, std::size_t bytes) noexcept {
    const owned_output_range range{output, bytes};
    return range.overlaps(&handle, sizeof(handle)) || range.overlaps(handle.device_styles) ||
        range.overlaps(handle.variation_coordinates_16_16) || range.overlaps(handle.run_slices) ||
        range.overlaps(handle.outline_owners) || range.overlaps(handle.admitted_scalars) ||
        range.overlaps(handle.scalar_levels) || range.overlaps(handle.logical_glyphs) ||
        range.overlaps(handle.logical_owners) || range.overlaps(handle.positioned_owners) ||
        range.overlaps(handle.lines) || range.overlaps(handle.boxes) || range.overlaps(handle.carets) ||
        range.overlaps(handle.nominal_metrics) || range.overlaps(handle.line_frames) ||
        handle.generation->allocation_aliases(output, bytes) || handle.interaction->allocation_aliases(output, bytes) ||
        progpu::native::text::hinted_paragraph_aliases(*handle.generation->paragraph(), output, bytes);
}

} // namespace

namespace progpu::native::text {

std::shared_ptr<const hinted_paragraph_generation> select_hinted_paragraph_generation(
    const progpu_native_hinted_paragraph* paragraph) noexcept {
    return valid_hinted_paragraph(paragraph) ? paragraph->generation : nullptr;
}

std::shared_ptr<const hinted_paragraph_interaction> select_hinted_paragraph_interaction(
    const progpu_native_hinted_paragraph* paragraph) noexcept {
    return valid_hinted_paragraph(paragraph) ? paragraph->interaction : nullptr;
}

std::shared_ptr<const hinted_paragraph_glyph_frame> select_hinted_paragraph_frame_generation(
    const progpu_native_hinted_paragraph_frame* frame) noexcept {
    return valid_hinted_paragraph_frame(frame) ? frame->generation : nullptr;
}

std::shared_ptr<const hinted_paragraph_glyph_resource> select_hinted_glyph_resource_generation(
    const progpu_native_hinted_glyph_resource* resource) noexcept {
    return valid_hinted_glyph_resource(resource) ? resource->generation : nullptr;
}

} // namespace progpu::native::text

extern "C" {

progpu_native_status progpu_native_text_context_layout_hinted_paragraph(
    progpu_native_text_context* context, const progpu_native_text_shape_request* shaping,
    const progpu_native_text_layout_options* layout,
    const progpu_native_text_style_run* styles, std::uint32_t style_count,
    const progpu_native_text_style_metrics* source_metrics,
    const progpu_native_hinted_paragraph_device_style* device_styles, std::uint32_t device_style_count,
    const std::int32_t* variation_coordinates_16_16, std::uint32_t variation_count,
    progpu_native_hinted_paragraph** paragraph, progpu_native_text_paragraph_result* paragraph_result) {
    if (!valid_hinted_buffer(context, 1U) || !hinted_paragraph_shape_inputs_valid(shaping) ||
        !valid_hinted_buffer(layout, 1U) || layout->struct_size != sizeof(*layout) ||
        !valid_hinted_buffer(styles, style_count) || !valid_hinted_buffer(source_metrics, style_count) ||
        !valid_hinted_buffer(device_styles, device_style_count) || device_style_count != style_count ||
        !valid_hinted_buffer(variation_coordinates_16_16, variation_count) ||
        !valid_hinted_buffer(paragraph, 1U) || !valid_hinted_buffer(paragraph_result, 1U) ||
        byte_ranges_overlap(paragraph, sizeof(*paragraph), paragraph_result, sizeof(*paragraph_result)))
        return PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
    const auto aliases = [&](const void* output, std::uint64_t bytes) noexcept {
        return hinted_paragraph_factory_aliases_inputs(output, bytes, *context, *shaping, *layout,
            styles, style_count, source_metrics, device_styles, device_style_count,
            variation_coordinates_16_16, variation_count);
    };
    if (aliases(paragraph, sizeof(*paragraph)) || aliases(paragraph_result, sizeof(*paragraph_result)))
        return PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
    for (std::uint32_t index = 0U; index < device_style_count; ++index) {
        const auto& device = device_styles[index];
        if (device.reserved != 0U || device.variation_count > 65535U ||
            device.variation_start > variation_count || device.variation_count > variation_count - device.variation_start)
            return PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
    }
    try {
        std::vector<hinted_paragraph_style_configuration> configurations;
        configurations.reserve(device_style_count);
        for (std::uint32_t index = 0U; index < device_style_count; ++index) {
            const auto& device = device_styles[index];
            const auto* axes = device.variation_count == 0U ? nullptr : variation_coordinates_16_16 + device.variation_start;
            configurations.push_back({device.font_index, device.source_scale,
                {device.x_pixels_per_em_26_6, device.y_pixels_per_em_26_6,
                    static_cast<font_hint_policy>(device.interpreter), device.x_phase_26_6, device.y_phase_26_6,
                    {axes, device.variation_count}}, device.logical_units_per_physical_pixel});
        }
        auto candidate = std::make_unique<progpu_native_hinted_paragraph>();
        progpu_native_text_paragraph_result diagnostic{};
        const auto status = try_layout_context_hinted_paragraph(context, *shaping, *layout,
            {styles, style_count}, {source_metrics, style_count}, configurations, candidate->generation, diagnostic);
        if (status != PROGPU_NATIVE_STATUS_SUCCESS) return status;
        const auto interaction = create_hinted_paragraph_interaction(candidate->generation);
        if (interaction.status != PROGPU_NATIVE_STATUS_SUCCESS) return interaction.status;
        candidate->interaction = interaction.generation;
        if (!valid_hinted_paragraph(candidate.get())) return PROGPU_NATIVE_STATUS_INTERNAL_ERROR;
        // Never inspect the caller's previous slot. Both independent owners and
        // all input/context guards are complete before either output is written.
        *paragraph_result = diagnostic;
        *paragraph = candidate.release();
        return PROGPU_NATIVE_STATUS_SUCCESS;
    } catch (const std::bad_alloc&) {
        return PROGPU_NATIVE_STATUS_OUT_OF_MEMORY;
    } catch (...) {
        return PROGPU_NATIVE_STATUS_INTERNAL_ERROR;
    }
}

progpu_native_status progpu_native_hinted_paragraph_get_counts(const progpu_native_hinted_paragraph* paragraph,
    progpu_native_hinted_paragraph_counts* counts, progpu_native_text_paragraph_result* paragraph_result) {
    if (!valid_hinted_paragraph(paragraph) || !valid_hinted_buffer(counts, 1U) || !valid_hinted_buffer(paragraph_result, 1U) ||
        byte_ranges_overlap(counts, sizeof(*counts), paragraph_result, sizeof(*paragraph_result)) ||
        hinted_paragraph_handle_aliases(*paragraph, counts, sizeof(*counts)) ||
        hinted_paragraph_handle_aliases(*paragraph, paragraph_result, sizeof(*paragraph_result)))
        return PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
    const auto& value = *paragraph->generation;
    const progpu_native_hinted_paragraph_counts result{
        static_cast<std::uint32_t>(value.source_input.size()), static_cast<std::uint32_t>(value.shaping_input.size()),
        static_cast<std::uint32_t>(value.styles.size()), static_cast<std::uint32_t>(value.runs.size()),
        static_cast<std::uint32_t>(value.logical_glyphs.size()), static_cast<std::uint32_t>(value.glyphs.size()),
        static_cast<std::uint32_t>(value.lines.size()), static_cast<std::uint32_t>(paragraph->interaction->boxes().size()),
        static_cast<std::uint32_t>(paragraph->interaction->carets().size())};
    *counts = result;
    *paragraph_result = value.paragraph_result;
    return PROGPU_NATIVE_STATUS_SUCCESS;
}

progpu_native_status progpu_native_hinted_paragraph_copy_format(const progpu_native_hinted_paragraph* paragraph,
    const progpu_native_hinted_paragraph_format_buffers* buffers) {
    if (!valid_hinted_paragraph(paragraph) || !valid_hinted_buffer(buffers, 1U) ||
        hinted_paragraph_handle_aliases(*paragraph, buffers, sizeof(*buffers)) ||
        buffers->struct_size != sizeof(*buffers) || buffers->reserved != 0U) return PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
    const auto& value = *paragraph->generation;
    const auto& b = *buffers;
    if (!valid_hinted_buffer(b.source_scalars, b.source_scalar_capacity) || b.source_scalar_capacity < value.source_input.size() ||
        !valid_hinted_buffer(b.admitted_scalars, b.admitted_scalar_capacity) || b.admitted_scalar_capacity < value.shaping_input.size() ||
        !valid_hinted_buffer(b.scalar_levels, b.scalar_level_capacity) || b.scalar_level_capacity < value.scalar_levels.size() ||
        !valid_hinted_buffer(b.styles, b.style_capacity) || b.style_capacity < value.styles.size() ||
        !valid_hinted_buffer(b.source_metrics, b.source_metric_capacity) || b.source_metric_capacity < value.source_metrics.size() ||
        !valid_hinted_buffer(b.runs, b.run_capacity) || b.run_capacity < value.runs.size() ||
        !valid_hinted_buffer(b.logical_glyphs, b.logical_glyph_capacity) || b.logical_glyph_capacity < value.logical_glyphs.size() ||
        !valid_hinted_buffer(b.logical_owners, b.logical_owner_capacity) || b.logical_owner_capacity < value.logical_owners.size() ||
        !valid_hinted_buffer(b.logical_cluster_ends, b.logical_cluster_end_capacity) || b.logical_cluster_end_capacity < value.logical_cluster_ends.size() ||
        !valid_hinted_buffer(b.logical_bidi_levels, b.logical_bidi_level_capacity) || b.logical_bidi_level_capacity < value.logical_bidi_levels.size() ||
        !valid_hinted_buffer(b.glyph_scales, b.glyph_scale_capacity) || b.glyph_scale_capacity < value.glyph_scales.size() ||
        !valid_hinted_buffer(b.positioned_glyphs, b.positioned_glyph_capacity) || b.positioned_glyph_capacity < value.glyphs.size() ||
        !valid_hinted_buffer(b.positioned_owners, b.positioned_owner_capacity) || b.positioned_owner_capacity < value.positioned_owners.size() ||
        !valid_hinted_buffer(b.positioned_cluster_ends, b.positioned_cluster_end_capacity) || b.positioned_cluster_end_capacity < value.cluster_ends.size() ||
        !valid_hinted_buffer(b.positioned_bidi_levels, b.positioned_bidi_level_capacity) || b.positioned_bidi_level_capacity < value.bidi_levels.size() ||
        !valid_hinted_buffer(b.lines, b.line_capacity) || b.line_capacity < value.lines.size() ||
        !valid_hinted_buffer(b.line_origins, b.line_origin_capacity) || b.line_origin_capacity < value.line_origins.size())
        return PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
    const std::array ranges{
        hinted_paragraph_range(b.source_scalars, b.source_scalar_capacity), hinted_paragraph_range(b.admitted_scalars, b.admitted_scalar_capacity),
        hinted_paragraph_range(b.scalar_levels, b.scalar_level_capacity), hinted_paragraph_range(b.styles, b.style_capacity),
        hinted_paragraph_range(b.source_metrics, b.source_metric_capacity), hinted_paragraph_range(b.runs, b.run_capacity),
        hinted_paragraph_range(b.logical_glyphs, b.logical_glyph_capacity), hinted_paragraph_range(b.logical_owners, b.logical_owner_capacity),
        hinted_paragraph_range(b.logical_cluster_ends, b.logical_cluster_end_capacity), hinted_paragraph_range(b.logical_bidi_levels, b.logical_bidi_level_capacity),
        hinted_paragraph_range(b.glyph_scales, b.glyph_scale_capacity), hinted_paragraph_range(b.positioned_glyphs, b.positioned_glyph_capacity),
        hinted_paragraph_range(b.positioned_owners, b.positioned_owner_capacity), hinted_paragraph_range(b.positioned_cluster_ends, b.positioned_cluster_end_capacity),
        hinted_paragraph_range(b.positioned_bidi_levels, b.positioned_bidi_level_capacity), hinted_paragraph_range(b.lines, b.line_capacity),
        hinted_paragraph_range(b.line_origins, b.line_origin_capacity)};
    for (std::size_t index = 0U; index < ranges.size(); ++index) {
        const auto range = ranges[index];
        if (byte_ranges_overlap(range.data, range.bytes, buffers, sizeof(*buffers)) ||
            hinted_paragraph_handle_aliases(*paragraph, range.data, range.bytes)) return PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
        for (std::size_t previous = 0U; previous < index; ++previous)
            if (byte_ranges_overlap(range.data, range.bytes, ranges[previous].data, ranges[previous].bytes))
                return PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
    }
    // Whole-generation preflight above; these fixed-record copies cannot fail.
    copy_paragraph_vector(value.source_input, b.source_scalars);
    for (std::size_t index = 0U; index < value.shaping_input.size(); ++index) {
        const auto& source = value.shaping_input[index];
        b.admitted_scalars[index] = {source.code_point, source.input_index, source.input_length,
            source.canonical_combining_class, source.reserved, source.script.value};
        const auto& level = value.scalar_levels[index];
        b.scalar_levels[index] = {level.input_index, level.input_length, level.level, level.reserved};
    }
    copy_paragraph_vector(value.styles, b.styles);
    copy_paragraph_vector(value.source_metrics, b.source_metrics);
    for (std::size_t index = 0U; index < value.runs.size(); ++index) {
        const auto& run = value.runs[index];
        b.runs[index] = {run.scalar_start, run.scalar_count, run.logical_start, run.logical_count,
            run.font_index, run.style_index, run.bidi_level, run.source_scale, run.logical_units_per_physical_pixel,
            static_cast<std::uint32_t>(run.generation->source_descriptor_count)};
    }
    for (std::size_t index = 0U; index < value.logical_glyphs.size(); ++index) {
        const auto& glyph = value.logical_glyphs[index];
        // Already C Y-down from the actual producer; never negate/project again.
        b.logical_glyphs[index] = {glyph.glyph_id, glyph.code_point, glyph.cluster, static_cast<std::uint32_t>(glyph.flags),
            glyph.advance_x, glyph.advance_y, glyph.offset_x, glyph.offset_y};
        b.logical_owners[index] = copy_paragraph_owner(value.logical_owners[index]);
    }
    copy_paragraph_vector(value.logical_cluster_ends, b.logical_cluster_ends);
    copy_paragraph_vector(value.logical_bidi_levels, b.logical_bidi_levels);
    copy_paragraph_vector(value.glyph_scales, b.glyph_scales);
    for (std::size_t index = 0U; index < value.glyphs.size(); ++index) {
        const auto& glyph = value.glyphs[index];
        b.positioned_glyphs[index] = {glyph.glyph_index, glyph.glyph_id, value.logical_font_indices[glyph.glyph_index],
            glyph.cluster, glyph.x, glyph.y, glyph.advance_x, glyph.advance_y};
        b.positioned_owners[index] = copy_paragraph_owner(value.positioned_owners[index]);
    }
    copy_paragraph_vector(value.cluster_ends, b.positioned_cluster_ends);
    copy_paragraph_vector(value.bidi_levels, b.positioned_bidi_levels);
    for (std::size_t index = 0U; index < value.lines.size(); ++index) {
        const auto& line = value.lines[index];
        b.lines[index] = {line.glyph_start, line.glyph_count, line.input_start, line.input_end, line.width,
            line.baseline_y, line.height, static_cast<std::uint8_t>(line.clipped ? 1U : 0U), line.flags, 0U, 0U};
    }
    copy_paragraph_vector(value.line_origins, b.line_origins);
    return PROGPU_NATIVE_STATUS_SUCCESS;
}

progpu_native_status progpu_native_hinted_paragraph_copy_interaction(const progpu_native_hinted_paragraph* paragraph,
    progpu_native_text_cluster_box* boxes, std::uint32_t box_capacity,
    progpu_native_text_caret_stop* carets, std::uint32_t caret_capacity) {
    if (!valid_hinted_paragraph(paragraph) || !valid_hinted_buffer(boxes, box_capacity) || !valid_hinted_buffer(carets, caret_capacity))
        return PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
    const auto source_boxes = paragraph->interaction->boxes();
    const auto source_carets = paragraph->interaction->carets();
    const auto box_range = hinted_paragraph_range(boxes, box_capacity);
    const auto caret_range = hinted_paragraph_range(carets, caret_capacity);
    if (box_capacity < source_boxes.size() || caret_capacity < source_carets.size() ||
        byte_ranges_overlap(box_range.data, box_range.bytes, caret_range.data, caret_range.bytes) ||
        hinted_paragraph_handle_aliases(*paragraph, box_range.data, box_range.bytes) ||
        hinted_paragraph_handle_aliases(*paragraph, caret_range.data, caret_range.bytes)) return PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
    for (std::size_t index = 0U; index < source_boxes.size(); ++index) {
        const auto& source = source_boxes[index];
        boxes[index] = {source.input_start, source.input_end, source.line_index, source.bidi_level,
            0U, 0U, 0U, source.x, source.y, source.width, source.height};
    }
    for (std::size_t index = 0U; index < source_carets.size(); ++index) {
        const auto& source = source_carets[index];
        carets[index] = {source.input_position, source.line_index, source.x, source.y, source.height,
            source.bidi_level, static_cast<std::uint8_t>(source.trailing ? 1U : 0U), 0U, 0U};
    }
    return PROGPU_NATIVE_STATUS_SUCCESS;
}

void progpu_native_hinted_paragraph_destroy(progpu_native_hinted_paragraph* paragraph) { delete paragraph; }

static progpu_native_status prepare_hinted_glyph_resource(
    const progpu_native_hinted_paragraph* paragraph,
    const progpu_native_hinted_glyph_resource_request* request,
    progpu_native_hinted_glyph_resource** resource, bool nominal_metrics) {
    static_assert(static_cast<std::uint32_t>(hinted_projection_policy::scalar_reference) == PROGPU_NATIVE_HINTED_PROJECTION_SCALAR_REFERENCE);
    static_assert(static_cast<std::uint32_t>(hinted_outline_coverage::nonzero_vector) == PROGPU_NATIVE_HINTED_COVERAGE_NONZERO_VECTOR);
    static_assert(static_cast<std::uint32_t>(hinted_outline_coverage::antialiased_vector) == PROGPU_NATIVE_HINTED_COVERAGE_ANTIALIASED_VECTOR);
    if (!valid_hinted_paragraph(paragraph) || !valid_hinted_buffer(request, 1U) || !valid_hinted_buffer(resource, 1U) ||
        hinted_paragraph_handle_aliases(*paragraph, request, sizeof(*request)) ||
        byte_ranges_overlap(resource, sizeof(*resource), request, sizeof(*request)) ||
        hinted_paragraph_handle_aliases(*paragraph, resource, sizeof(*resource)))
        return PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
    if (request->abi_version != PROGPU_NATIVE_ABI_VERSION || request->struct_size != sizeof(*request) || request->reserved != 0U ||
        !std::isfinite(request->dpi_scale) || request->dpi_scale <= 0.0F ||
        request->projection_policy > PROGPU_NATIVE_HINTED_PROJECTION_SCALAR_REFERENCE ||
        request->coverage > PROGPU_NATIVE_HINTED_COVERAGE_ANTIALIASED_VECTOR ||
        !hinted_glyph_resource_format_bounded(*paragraph->generation)) return PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
    try {
        auto candidate = std::make_unique<progpu_native_hinted_glyph_resource>();
        const auto prepared = create_hinted_paragraph_glyph_resource(paragraph->generation, request->dpi_scale,
            static_cast<hinted_projection_policy>(request->projection_policy), static_cast<hinted_outline_coverage>(request->coverage));
        if (prepared.status != PROGPU_NATIVE_STATUS_SUCCESS) return prepared.status;
        candidate->generation = prepared.generation;
        candidate->interaction = paragraph->interaction;
        if (candidate->generation == nullptr || candidate->generation->paragraph() != paragraph->generation ||
            !cache_hinted_glyph_resource_view(*candidate) || !valid_hinted_glyph_resource(candidate.get()))
            return PROGPU_NATIVE_STATUS_INTERNAL_ERROR;
        if (nominal_metrics) {
            const auto status = capture_hinted_nominal_metrics(*paragraph->generation, candidate->nominal_metrics);
            if (status != PROGPU_NATIVE_STATUS_SUCCESS) return status;
            candidate->has_nominal_metrics = true;
        }
        // Never read the old caller slot. Original geometry, interaction and
        // every immutable wire cache exist before the sole publication write.
        *resource = candidate.release();
        return PROGPU_NATIVE_STATUS_SUCCESS;
    } catch (const std::bad_alloc&) {
        return PROGPU_NATIVE_STATUS_OUT_OF_MEMORY;
    } catch (...) {
        return PROGPU_NATIVE_STATUS_INTERNAL_ERROR;
    }
}

progpu_native_status progpu_native_hinted_paragraph_prepare_glyph_resource(
    const progpu_native_hinted_paragraph* paragraph,
    const progpu_native_hinted_glyph_resource_request* request,
    progpu_native_hinted_glyph_resource** resource) {
    return prepare_hinted_glyph_resource(paragraph, request, resource, false);
}

progpu_native_status progpu_native_hinted_paragraph_prepare_glyph_resource_with_nominal_metrics(
    const progpu_native_hinted_paragraph* paragraph,
    const progpu_native_hinted_glyph_resource_request* request,
    progpu_native_hinted_glyph_resource** resource) {
    return prepare_hinted_glyph_resource(paragraph, request, resource, true);
}

progpu_native_status progpu_native_hinted_glyph_resource_borrow(
    const progpu_native_hinted_glyph_resource* resource, progpu_native_hinted_glyph_resource_view* view) {
    if (!valid_hinted_glyph_resource(resource) || !valid_hinted_buffer(view, 1U) ||
        hinted_glyph_resource_aliases(*resource, view, sizeof(*view))) return PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
    *view = resource->view; // No allocation or new projection under the caller's producer-library lease.
    return PROGPU_NATIVE_STATUS_SUCCESS;
}

progpu_native_status progpu_native_hinted_glyph_resource_reflow(
    const progpu_native_hinted_glyph_resource* resource,
    std::int32_t input_start, float maximum_width,
    progpu_native_hinted_glyph_resource** continuation) {
    if (!valid_hinted_glyph_resource(resource) || !valid_hinted_buffer(continuation, 1U) ||
        hinted_glyph_resource_aliases(*resource, continuation, sizeof(*continuation)))
        return PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
    const auto reflow = reflow_hinted_paragraph(*resource->generation->paragraph(), input_start, maximum_width);
    if (reflow.status != PROGPU_NATIVE_STATUS_SUCCESS) return reflow.status;
    const auto interaction = create_hinted_paragraph_interaction(reflow.generation);
    if (interaction.status != PROGPU_NATIVE_STATUS_SUCCESS) return interaction.status;
    const progpu_native_hinted_paragraph paragraph{reflow.generation, interaction.generation};
    const progpu_native_hinted_glyph_resource_request request{PROGPU_NATIVE_ABI_VERSION, sizeof(request),
        resource->view.dpi_scale, resource->view.projection_policy, resource->view.coverage, 0U};
    // Existing preparation owns all caches before its sole publication write.
    // Original resource storage is excluded above, including full capacities.
    return prepare_hinted_glyph_resource(&paragraph, &request, continuation, resource->has_nominal_metrics);
}

progpu_native_status progpu_native_hinted_glyph_resource_borrow_nominal_metrics(
    const progpu_native_hinted_glyph_resource* resource, progpu_native_hinted_glyph_nominal_metrics_view* view) {
    if (!valid_hinted_glyph_resource(resource) || !valid_hinted_buffer(view, 1U) ||
        hinted_glyph_resource_aliases(*resource, view, sizeof(*view))) return PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
    if (!resource->has_nominal_metrics || (resource->generation->paragraph() != nullptr &&
        resource->generation->paragraph()->has_source_geometry)) return PROGPU_NATIVE_STATUS_UNSUPPORTED;
    const progpu_native_hinted_glyph_nominal_metrics_view candidate{PROGPU_NATIVE_ABI_VERSION,
        sizeof(*view), static_cast<std::uint32_t>(resource->nominal_metrics.size()), 0U,
        hinted_glyph_wire_data(resource->nominal_metrics)};
    *view = candidate;
    return PROGPU_NATIVE_STATUS_SUCCESS;
}

void progpu_native_hinted_glyph_resource_destroy(progpu_native_hinted_glyph_resource* resource) {
    static_assert(std::is_nothrow_destructible_v<progpu_native_hinted_glyph_resource>);
    delete resource;
}

progpu_native_status progpu_native_hinted_glyph_resource_borrow_line_frames(
    const progpu_native_hinted_glyph_resource* resource, progpu_native_hinted_text_line_frames_view* view) {
    if (!valid_hinted_glyph_resource(resource) || !valid_hinted_buffer(view, 1U) ||
        hinted_glyph_resource_aliases(*resource, view, sizeof(*view))) return PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
    const progpu_native_hinted_text_line_frames_view candidate{PROGPU_NATIVE_ABI_VERSION, sizeof(*view),
        static_cast<std::uint32_t>(resource->line_frames.size()), 0U, hinted_glyph_wire_data(resource->line_frames)};
    *view = candidate;
    return PROGPU_NATIVE_STATUS_SUCCESS;
}

progpu_native_status progpu_native_hinted_glyph_resource_validate_source_frame(
    const progpu_native_hinted_glyph_resource* resource,
    const std::uint32_t* positioned_indices, std::uint32_t glyph_count,
    float source_em_size, progpu_native_point source_baseline_origin,
    const double* source_advances, const progpu_native_hinted_source_glyph_offset* source_offsets,
    progpu_native_hinted_source_glyph_frame* frame) {
    if (!valid_hinted_glyph_resource(resource) || !valid_hinted_buffer(frame, 1U) ||
        !valid_hinted_buffer(positioned_indices, glyph_count) || !valid_hinted_buffer(source_advances, glyph_count) ||
        !valid_hinted_buffer(source_offsets, glyph_count) ||
        hinted_glyph_resource_aliases(*resource, frame, sizeof(*frame)) ||
        byte_ranges_overlap(frame, sizeof(*frame), positioned_indices, static_cast<std::uint64_t>(glyph_count) * sizeof(*positioned_indices)) ||
        byte_ranges_overlap(frame, sizeof(*frame), source_advances, static_cast<std::uint64_t>(glyph_count) * sizeof(*source_advances)) ||
        byte_ranges_overlap(frame, sizeof(*frame), source_offsets, static_cast<std::uint64_t>(glyph_count) * sizeof(*source_offsets)))
        return PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
    if (!resource->has_nominal_metrics || (resource->generation->paragraph() != nullptr &&
        resource->generation->paragraph()->has_source_geometry)) return PROGPU_NATIVE_STATUS_UNSUPPORTED;
    return progpu::native::text::validate_hinted_source_frame(resource->view, resource->nominal_metrics,
        {positioned_indices, glyph_count}, source_em_size, source_baseline_origin,
        {source_advances, glyph_count}, {source_offsets, glyph_count}, *frame);
}

progpu_native_status progpu_native_hinted_glyph_resource_copy_source_offsets(
    const progpu_native_hinted_glyph_resource* resource, const std::uint32_t* positioned_indices,
    std::uint32_t glyph_count, float source_em_size,
    progpu_native_hinted_source_glyph_offset* offsets, std::uint32_t offset_capacity) {
    const auto bytes = static_cast<std::uint64_t>(offset_capacity) * sizeof(*offsets);
    if (!valid_hinted_glyph_resource(resource) || !valid_hinted_buffer(positioned_indices, glyph_count) ||
        !valid_hinted_buffer(offsets, offset_capacity) || offset_capacity < glyph_count ||
        hinted_glyph_resource_aliases(*resource, offsets, static_cast<std::size_t>(bytes)) ||
        byte_ranges_overlap(offsets, bytes, positioned_indices, static_cast<std::uint64_t>(glyph_count) * sizeof(*positioned_indices)))
        return PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
    if (!resource->has_nominal_metrics) return PROGPU_NATIVE_STATUS_UNSUPPORTED;
    return progpu::native::text::copy_hinted_source_offsets(resource->view, resource->nominal_metrics,
        {positioned_indices, glyph_count}, source_em_size, {offsets, offset_capacity});
}

progpu_native_status progpu_native_hinted_paragraph_prepare_frame(const progpu_native_hinted_paragraph* paragraph,
    const progpu_native_hinted_paragraph_frame_request* request,
    const progpu_native_color* style_colors, std::uint32_t style_color_count,
    progpu_native_hinted_paragraph_frame** frame) {
    static_assert(static_cast<std::uint32_t>(hinted_projection_policy::scalar_reference) == PROGPU_NATIVE_HINTED_PROJECTION_SCALAR_REFERENCE);
    static_assert(static_cast<std::uint32_t>(hinted_outline_coverage::nonzero_vector) == PROGPU_NATIVE_HINTED_COVERAGE_NONZERO_VECTOR);
    static_assert(static_cast<std::uint32_t>(hinted_outline_coverage::antialiased_vector) == PROGPU_NATIVE_HINTED_COVERAGE_ANTIALIASED_VECTOR);
    if (!valid_hinted_paragraph(paragraph) || !valid_hinted_buffer(request, 1U) || !valid_hinted_buffer(frame, 1U) ||
        hinted_paragraph_handle_aliases(*paragraph, request, sizeof(*request)) ||
        !valid_hinted_buffer(style_colors, style_color_count) || style_color_count != paragraph->generation->styles.size() ||
        byte_ranges_overlap(frame, sizeof(*frame), request, sizeof(*request)) ||
        byte_ranges_overlap(frame, sizeof(*frame), style_colors, static_cast<std::uint64_t>(style_color_count) * sizeof(*style_colors)) ||
        hinted_paragraph_handle_aliases(*paragraph, frame, sizeof(*frame))) return PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
    if (request->abi_version != PROGPU_NATIVE_ABI_VERSION || request->struct_size != sizeof(*request) || request->reserved != 0U ||
        request->projection_policy > PROGPU_NATIVE_HINTED_PROJECTION_SCALAR_REFERENCE || request->coverage > PROGPU_NATIVE_HINTED_COVERAGE_ANTIALIASED_VECTOR)
        return PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
    try {
        auto candidate = std::make_unique<progpu_native_hinted_paragraph_frame>();
        const hinted_paragraph_glyph_target target{request->width, request->height, request->dpi_scale,
            request->target_view, request->logical_origin, request->clear_color};
        const auto prepared = create_hinted_paragraph_glyph_frame(paragraph->generation, target, {style_colors, style_color_count},
            static_cast<hinted_projection_policy>(request->projection_policy), static_cast<hinted_outline_coverage>(request->coverage));
        if (prepared.status != PROGPU_NATIVE_STATUS_SUCCESS) return prepared.status;
        candidate->generation = prepared.generation;
        candidate->interaction = paragraph->interaction;
        if (!valid_hinted_paragraph_frame(candidate.get())) return PROGPU_NATIVE_STATUS_INTERNAL_ERROR;
        *frame = candidate.release();
        return PROGPU_NATIVE_STATUS_SUCCESS;
    } catch (const std::bad_alloc&) {
        return PROGPU_NATIVE_STATUS_OUT_OF_MEMORY;
    } catch (...) {
        return PROGPU_NATIVE_STATUS_INTERNAL_ERROR;
    }
}

progpu_native_status progpu_native_hinted_paragraph_frame_borrow(const progpu_native_hinted_paragraph_frame* frame,
    progpu_native_glyph_frame* wire_frame) {
    if (!valid_hinted_paragraph_frame(frame) || !valid_hinted_buffer(wire_frame, 1U) ||
        hinted_paragraph_frame_aliases(*frame, wire_frame, sizeof(*wire_frame))) return PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
    *wire_frame = frame->generation->frame();
    return PROGPU_NATIVE_STATUS_SUCCESS;
}

void progpu_native_hinted_paragraph_frame_destroy(progpu_native_hinted_paragraph_frame* frame) { delete frame; }

} // extern "C"
