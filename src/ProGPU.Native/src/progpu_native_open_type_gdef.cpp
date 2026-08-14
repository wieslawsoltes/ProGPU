#include "progpu_native_text.hpp"

#include <array>
#include <cstddef>
#include <cstdint>
#include <span>

// Direct native port of ProGPU-owned OpenType GDEF classification and
// OpenTypeGdefPolicy.cs at checkpoint 3cc418aa. The binary layout follows the
// OpenType GDEF 1.0/1.2/1.3 public specification.

namespace progpu::native::text {
namespace {

void set_error(font_error* error, font_error value) noexcept {
    if (error != nullptr) {
        *error = value;
    }
}

bool can_read(
    std::span<const std::byte> bytes,
    std::size_t offset,
    std::size_t length) noexcept {
    return offset <= bytes.size() && length <= bytes.size() - offset;
}

std::uint16_t read_u16(
    std::span<const std::byte> bytes,
    std::size_t offset) noexcept {
    return static_cast<std::uint16_t>(
        (std::to_integer<std::uint16_t>(bytes[offset]) << 8U) |
        std::to_integer<std::uint16_t>(bytes[offset + 1U]));
}

std::uint32_t read_u32(
    std::span<const std::byte> bytes,
    std::size_t offset) noexcept {
    return (static_cast<std::uint32_t>(read_u16(bytes, offset)) << 16U) |
        read_u16(bytes, offset + 2U);
}

struct gdef_blocklist_record final {
    std::size_t gdef;
    std::size_t gsub;
    std::size_t gpos;
};

constexpr std::array<gdef_blocklist_record, 40U> gdef_blocklist{{
    {442U, 2874U, 42038U}, {430U, 2874U, 40662U},
    {442U, 2874U, 39116U}, {430U, 2874U, 39374U},
    {490U, 3046U, 41638U}, {478U, 3046U, 41902U},
    {898U, 12554U, 46470U}, {910U, 12566U, 47732U},
    {928U, 23298U, 59332U}, {940U, 23310U, 60732U},
    {964U, 23836U, 60072U}, {976U, 23832U, 61456U},
    {994U, 24474U, 60336U}, {1006U, 24470U, 61740U},
    {1006U, 24576U, 61346U}, {1018U, 24572U, 62828U},
    {1006U, 24576U, 61352U}, {1018U, 24572U, 62834U},
    {832U, 7324U, 47162U}, {844U, 7302U, 45474U},
    {180U, 13054U, 7254U}, {192U, 12638U, 7254U},
    {192U, 12690U, 7254U}, {188U, 248U, 3852U},
    {188U, 264U, 3426U}, {1058U, 47032U, 11818U},
    {1046U, 47030U, 12600U}, {1058U, 71796U, 16770U},
    {1046U, 71790U, 17862U}, {1046U, 71788U, 17112U},
    {1058U, 71794U, 17514U}, {1330U, 109904U, 57938U},
    {1330U, 109904U, 58972U}, {1004U, 59092U, 14836U},
    {588U, 5078U, 14418U}, {588U, 5078U, 14238U},
    {894U, 17162U, 33960U}, {894U, 17154U, 34472U},
    {816U, 7868U, 17052U}, {816U, 7868U, 17138U}
}};

} // namespace

bool open_type_gdef_view::try_create(
    std::span<const std::byte> table,
    open_type_gdef_view& result,
    font_error* error) noexcept {
    result = {};
    if (!can_read(table, 0U, 12U) || read_u16(table, 0U) != 1U) {
        set_error(error, font_error::invalid_face);
        return false;
    }
    const std::uint16_t minor = read_u16(table, 2U);
    if ((minor != 0U && minor != 2U && minor != 3U) ||
        (minor >= 2U && !can_read(table, 0U, 14U)) ||
        (minor >= 3U && !can_read(table, 0U, 18U))) {
        set_error(error, font_error::invalid_face);
        return false;
    }

    open_type_class_definition_view glyph_classes{};
    open_type_class_definition_view mark_attachment_classes{};
    const std::size_t glyph_classes_offset = read_u16(table, 4U);
    const std::size_t mark_attachment_offset = read_u16(table, 10U);
    if ((glyph_classes_offset != 0U &&
            !open_type_class_definition_view::try_create(
                table, glyph_classes_offset, glyph_classes, error)) ||
        (mark_attachment_offset != 0U &&
            !open_type_class_definition_view::try_create(
                table,
                mark_attachment_offset,
                mark_attachment_classes,
                error))) {
        return false;
    }

    std::size_t mark_sets_offset = 0U;
    std::uint16_t mark_set_count = 0U;
    if (minor >= 2U) {
        mark_sets_offset = read_u16(table, 12U);
        if (mark_sets_offset != 0U) {
            if (!can_read(table, mark_sets_offset, 4U) ||
                read_u16(table, mark_sets_offset) != 1U) {
                set_error(error, font_error::invalid_face);
                return false;
            }
            mark_set_count = read_u16(table, mark_sets_offset + 2U);
            if (!can_read(
                    table,
                    mark_sets_offset + 4U,
                    static_cast<std::size_t>(mark_set_count) * 4U)) {
                set_error(error, font_error::invalid_face);
                return false;
            }
            for (std::uint16_t index = 0U; index < mark_set_count; ++index) {
                const std::uint32_t relative = read_u32(
                    table,
                    mark_sets_offset + 4U + index * 4U);
                if (relative == 0U || relative > table.size() - mark_sets_offset) {
                    set_error(error, font_error::invalid_face);
                    return false;
                }
                open_type_coverage_view coverage{};
                if (!open_type_coverage_view::try_create(
                        table,
                        mark_sets_offset + relative,
                        coverage,
                        error)) {
                    return false;
                }
            }
        }
    }
    if (minor >= 3U) {
        const std::uint32_t item_variation_store = read_u32(table, 14U);
        if (item_variation_store != 0U &&
            !can_read(table, item_variation_store, 8U)) {
            set_error(error, font_error::invalid_face);
            return false;
        }
    }

    result.table_ = table;
    result.glyph_classes_ = glyph_classes;
    result.mark_attachment_classes_ = mark_attachment_classes;
    result.mark_sets_offset_ = mark_sets_offset;
    result.mark_set_count_ = mark_set_count;
    result.has_glyph_classes_ = glyph_classes_offset != 0U;
    result.has_mark_attachment_classes_ = mark_attachment_offset != 0U;
    set_error(error, font_error::none);
    return true;
}

open_type_glyph_class open_type_gdef_view::glyph_class(
    std::uint16_t glyph_id) const noexcept {
    if (!has_glyph_classes_) {
        return open_type_glyph_class::unclassified;
    }
    const std::uint16_t value = glyph_classes_.get(glyph_id);
    return value <= static_cast<std::uint16_t>(open_type_glyph_class::component)
        ? static_cast<open_type_glyph_class>(value)
        : open_type_glyph_class::unclassified;
}

std::uint16_t open_type_gdef_view::mark_attachment_class(
    std::uint16_t glyph_id) const noexcept {
    return has_mark_attachment_classes_
        ? mark_attachment_classes_.get(glyph_id)
        : 0U;
}

bool open_type_gdef_view::is_in_mark_set(
    std::uint16_t set_index,
    std::uint16_t glyph_id) const noexcept {
    if (set_index >= mark_set_count_) {
        return false;
    }
    const std::uint32_t relative = read_u32(
        table_,
        mark_sets_offset_ + 4U + set_index * 4U);
    open_type_coverage_view coverage{};
    return open_type_coverage_view::try_create(
               table_, mark_sets_offset_ + relative, coverage) &&
        coverage.find(glyph_id) >= 0;
}

bool is_open_type_gdef_blocklisted(
    std::size_t gdef_length,
    std::size_t gsub_length,
    std::size_t gpos_length) noexcept {
    for (const auto& record : gdef_blocklist) {
        if (record.gdef == gdef_length && record.gsub == gsub_length &&
            record.gpos == gpos_length) {
            return true;
        }
    }
    return false;
}

} // namespace progpu::native::text
