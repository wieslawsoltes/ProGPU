#include "progpu_native_direct2d_vertical_metrics.hpp"
#include "progpu_native_text.hpp"
#include "../Text/progpu_native_font_bytes.hpp"

#include <array>
#include <cstddef>
#include <utility>

namespace progpu::native::direct2d {
namespace {
using text::detail::can_read;
using text::detail::read_i16;
using text::detail::read_u16;
using text::detail::read_u32;

enum table_index : std::size_t { glyf_index, loca_index, cff1_index, cff2_index, vhea_index, vmtx_index, vorg_index, table_count };
constexpr std::array<std::uint32_t, table_count> tags{
    text::open_type_tag::from_chars('g', 'l', 'y', 'f').value,
    text::open_type_tag::from_chars('l', 'o', 'c', 'a').value,
    text::open_type_tag::from_chars('C', 'F', 'F', ' ').value,
    text::open_type_tag::from_chars('C', 'F', 'F', '2').value,
    text::open_type_tag::from_chars('v', 'h', 'e', 'a').value,
    text::open_type_tag::from_chars('v', 'm', 't', 'x').value,
    text::open_type_tag::from_chars('V', 'O', 'R', 'G').value};

struct original_table final {
    std::uint32_t count = 0U, offset = 0U, length = 0U;
};

bool read_directory(std::span<const std::byte> bytes, std::uint32_t face,
    std::array<original_table, table_count>& output) noexcept
{
    if (!can_read(bytes, 0U, 12U)) return false;
    std::size_t offset = 0U;
    if (read_u32(bytes, 0U) == text::open_type_tag::from_chars('t', 't', 'c', 'f').value) {
        if (face >= read_u32(bytes, 8U) || !can_read(bytes, 12U, (static_cast<std::size_t>(face) + 1U) * 4U)) return false;
        offset = read_u32(bytes, 12U + static_cast<std::size_t>(face) * 4U);
    } else if (face != 0U) return false;
    if (!can_read(bytes, offset, 12U)) return false;
    const auto count = read_u16(bytes, offset + 4U);
    offset += 12U;
    if (!can_read(bytes, offset, static_cast<std::size_t>(count) * 16U)) return false;
    for (std::uint16_t index = 0U; index < count; ++index, offset += 16U) {
        const auto tag = read_u32(bytes, offset);
        for (std::size_t selected = 0U; selected < tags.size(); ++selected) {
            if (tag != tags[selected]) continue;
            auto& table = output[selected];
            ++table.count; table.offset = read_u32(bytes, offset + 8U); table.length = read_u32(bytes, offset + 12U);
        }
    }
    return true;
}
} // namespace

struct retained_original_vertical_metrics::state final {
    std::shared_ptr<const original_font_capture> source;
    text::sfnt_font_view font;
    std::span<const std::byte> vmtx, vorg;
    std::uint16_t glyph_count = 0U, metric_count = 0U;
    bool true_type = false, available = false;
};

retained_original_vertical_metrics::retained_original_vertical_metrics(std::unique_ptr<state> value) noexcept
    : state_(std::move(value)) {}
retained_original_vertical_metrics::~retained_original_vertical_metrics() = default;

const std::shared_ptr<const original_font_capture>& retained_original_vertical_metrics::source() const noexcept
{
    return state_->source;
}

bool retained_original_vertical_metrics::has_metrics() const noexcept { return state_->available; }

com::result retained_original_vertical_metrics::create(std::shared_ptr<const original_font_capture> source,
    std::shared_ptr<const retained_original_vertical_metrics>& output) noexcept
{
    if (!source || !source->face || source->files.empty()) return com::invalid_argument;
    if (source->files.size() != 1U || source->simulations != 0U || source->face_type > 2U)
        return compat::not_implemented;
    try {
        auto candidate = std::make_unique<state>();
        candidate->source = std::move(source);
        const auto& original = *candidate->source;
        const std::span<const std::byte> bytes = original.files[0];
        std::array<original_table, table_count> tables{};
        if (!text::sfnt_font_view::try_create(bytes, original.face_index, candidate->font) ||
            !candidate->font.try_get_glyph_count(candidate->glyph_count) ||
            candidate->glyph_count != original.glyph_count || candidate->glyph_count == 0U ||
            !read_directory(bytes, original.face_index, tables)) return com::invalid_argument;
        const bool glyf = tables[glyf_index].count != 0U, loca = tables[loca_index].count != 0U;
        const bool cff1 = tables[cff1_index].count != 0U, cff2 = tables[cff2_index].count != 0U;
        const auto families = static_cast<unsigned>(glyf || loca) + static_cast<unsigned>(cff1) + static_cast<unsigned>(cff2);
        if (families == 0U) return compat::not_implemented;
        if (families != 1U || glyf != loca || (original.face_type == 0U && glyf) ||
            (original.face_type == 1U && !glyf)) return com::invalid_argument;
        candidate->true_type = glyf;
        for (std::size_t index = 0U; index < tables.size(); ++index) {
            // OpenType explicitly requires ignoring VORG for TrueType. Even a
            // malformed VORG cannot override its actual glyf/vmtx origin.
            if (index == vorg_index && glyf) continue;
            const auto& table = tables[index];
            if (table.count > 1U || (table.count != 0U && !can_read(bytes, table.offset, table.length)))
                return com::invalid_argument;
        }
        if ((tables[vhea_index].count != 0U) != (tables[vmtx_index].count != 0U)) return com::invalid_argument;
        if (tables[vhea_index].count != 0U) {
            const auto vhea = bytes.subspan(tables[vhea_index].offset, tables[vhea_index].length);
            const auto vmtx = bytes.subspan(tables[vmtx_index].offset, tables[vmtx_index].length);
            if (vhea.size() < 36U) return com::invalid_argument;
            const auto version = read_u32(vhea, 0U);
            if ((version != 0x00010000U && version != 0x00011000U) ||
                (version == 0x00010000U && read_i16(vhea, 8U) != 0)) return com::invalid_argument;
            for (std::size_t reserved = 24U; reserved <= 32U; reserved += 2U)
                if (read_u16(vhea, reserved) != 0U) return com::invalid_argument;
            candidate->metric_count = read_u16(vhea, 34U);
            if (candidate->metric_count == 0U || candidate->metric_count > candidate->glyph_count) return com::invalid_argument;
            const auto metric_bytes = static_cast<std::size_t>(candidate->metric_count) * 4U;
            const auto bearing_bytes = static_cast<std::size_t>(candidate->glyph_count - candidate->metric_count) * 2U;
            if (!can_read(vmtx, 0U, metric_bytes + bearing_bytes)) return com::invalid_argument;
            candidate->vmtx = vmtx;
            candidate->available = true;
        }
        if (!glyf && tables[vorg_index].count != 0U) {
            const auto vorg = bytes.subspan(tables[vorg_index].offset, tables[vorg_index].length);
            if (vorg.size() < 8U || read_u32(vorg, 0U) != 0x00010000U) return com::invalid_argument;
            const auto count = read_u16(vorg, 6U);
            if (!can_read(vorg, 8U, static_cast<std::size_t>(count) * 4U)) return com::invalid_argument;
            std::uint16_t previous = 0U;
            for (std::uint16_t index = 0U; index < count; ++index) {
                const auto glyph = read_u16(vorg, 8U + static_cast<std::size_t>(index) * 4U);
                if (glyph >= candidate->glyph_count || (index != 0U && glyph <= previous)) return com::invalid_argument;
                previous = glyph;
            }
            candidate->vorg = vorg;
        }
        output = std::shared_ptr<const retained_original_vertical_metrics>(
            new retained_original_vertical_metrics(std::move(candidate)));
        return com::ok;
    } catch (const std::bad_alloc&) { return com::out_of_memory; }
    catch (...) { return com::invalid_argument; }
}

com::result retained_original_vertical_metrics::read_base(std::uint16_t glyph,
    original_vertical_glyph_metrics& output) const noexcept
{
    if (glyph >= state_->glyph_count) return com::invalid_argument;
    if (!state_->available) return compat::not_implemented;
    original_vertical_glyph_metrics candidate{};
    const auto count = state_->metric_count;
    const auto advance_offset = static_cast<std::size_t>(glyph < count ? glyph : count - 1U) * 4U;
    const auto bearing_offset = glyph < count ? advance_offset + 2U
        : static_cast<std::size_t>(count) * 4U + static_cast<std::size_t>(glyph - count) * 2U;
    candidate.advance_height = read_u16(state_->vmtx, advance_offset);
    candidate.top_side_bearing = read_i16(state_->vmtx, bearing_offset);
    if (state_->true_type) {
        text::sfnt_glyph_data_view original{};
        if (!state_->font.try_get_glyph_data(glyph, original)) return com::invalid_argument;
        if (!original.empty()) {
            if (original.y_min > original.y_max || original.x_min > original.x_max) return com::invalid_argument;
            candidate.top_origin = static_cast<std::int32_t>(original.y_max) + candidate.top_side_bearing;
            candidate.has_origin = true;
            candidate.origin_kind = original_vertical_origin_kind::true_type_bounds;
        }
    } else if (!state_->vorg.empty()) {
        const auto vorg = state_->vorg;
        candidate.top_origin = read_i16(vorg, 4U);
        std::size_t low = 0U, high = read_u16(vorg, 6U);
        while (low < high) {
            const auto middle = low + (high - low) / 2U;
            const auto offset = 8U + middle * 4U;
            const auto selected = read_u16(vorg, offset);
            if (selected < glyph) low = middle + 1U;
            else if (selected > glyph) high = middle;
            else { candidate.top_origin = read_i16(vorg, offset + 2U); break; }
        }
        candidate.has_origin = true;
        candidate.origin_kind = original_vertical_origin_kind::cff_vorg;
    }
    if (candidate.has_origin) candidate.bottom_origin = candidate.top_origin - candidate.advance_height;
    output = candidate;
    return com::ok;
}

} // namespace progpu::native::direct2d
