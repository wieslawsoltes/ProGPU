#include "progpu_native_sfnt_subset_internal.hpp"

#include <algorithm>
#include <iterator>
#include <limits>

namespace progpu::native::text::sfnt_subset_detail {
namespace {

constexpr std::uint16_t composite_more_components = 0x0020U;
constexpr std::uint16_t composite_args_are_words = 0x0001U;
constexpr std::uint16_t composite_has_scale = 0x0008U;
constexpr std::uint16_t composite_has_xy_scale = 0x0040U;
constexpr std::uint16_t composite_has_two_by_two = 0x0080U;
constexpr std::uint16_t composite_has_instructions = 0x0100U;
constexpr std::uint16_t no_glyph =
    std::numeric_limits<std::uint16_t>::max();

bool skip_copied_table(std::uint32_t value) noexcept {
    constexpr std::uint32_t skipped[]{
        tag('D', 'S', 'I', 'G'), tag('h', 'e', 'a', 'd'),
        tag('m', 'a', 'x', 'p'), tag('l', 'o', 'c', 'a'),
        tag('g', 'l', 'y', 'f'), tag('h', 'h', 'e', 'a'),
        tag('h', 'm', 't', 'x'), tag('c', 'm', 'a', 'p'),
        tag('G', 'S', 'U', 'B'), tag('G', 'P', 'O', 'S'),
        tag('G', 'D', 'E', 'F'), tag('k', 'e', 'r', 'n'),
        tag('v', 'h', 'e', 'a'), tag('v', 'm', 't', 'x'),
        tag('V', 'O', 'R', 'G'), tag('B', 'A', 'S', 'E'),
        tag('J', 'S', 'T', 'F'), tag('M', 'A', 'T', 'H'),
        tag('C', 'O', 'L', 'R'), tag('C', 'P', 'A', 'L'),
        tag('p', 'o', 's', 't')};
    return std::find(std::begin(skipped), std::end(skipped), value) !=
        std::end(skipped);
}

std::vector<std::byte> remap_composite(
    std::span<const std::byte> source,
    std::span<const std::uint16_t> source_to_subset) {
    std::vector<std::byte> result(source.begin(), source.end());
    if (source.empty() || !can_read(source, 0U, 10U) ||
        read_i16(source, 0U) >= 0) {
        return result;
    }
    std::size_t offset = 10U;
    std::uint16_t flags = 0U;
    do {
        if (!can_read(source, offset, 4U)) {
            throw subset_failure{};
        }
        flags = read_u16(source, offset);
        const auto source_glyph = read_u16(source, offset + 2U);
        if (source_glyph >= source_to_subset.size() ||
            source_to_subset[source_glyph] == no_glyph) {
            throw subset_failure{};
        }
        write_u16(result, offset + 2U, source_to_subset[source_glyph]);
        offset += 4U;
        offset += (flags & composite_args_are_words) != 0U ? 4U : 2U;
        if ((flags & composite_has_scale) != 0U) {
            offset += 2U;
        } else if ((flags & composite_has_xy_scale) != 0U) {
            offset += 4U;
        } else if ((flags & composite_has_two_by_two) != 0U) {
            offset += 8U;
        }
        if (offset > source.size()) {
            throw subset_failure{};
        }
    } while ((flags & composite_more_components) != 0U);
    if ((flags & composite_has_instructions) != 0U) {
        const auto length = read_u16(source, offset);
        if (!can_read(source, offset + 2U, length)) {
            throw subset_failure{};
        }
    }
    return result;
}

glyph_table_subset build_compact_glyphs(
    std::span<const std::byte> source_glyf,
    std::span<const std::uint32_t> source_offsets,
    std::span<const std::uint16_t> glyph_order,
    std::span<const std::uint16_t> source_to_subset) {
    std::vector<std::uint32_t> offsets(glyph_order.size() + 1U);
    std::vector<std::byte> glyf;
    for (std::size_t subset_glyph = 0U;
         subset_glyph < glyph_order.size(); ++subset_glyph) {
        if (glyf.size() > std::numeric_limits<std::uint32_t>::max()) {
            throw subset_failure{};
        }
        offsets[subset_glyph] = static_cast<std::uint32_t>(glyf.size());
        const auto source_glyph = glyph_order[subset_glyph];
        const auto start = static_cast<std::size_t>(
            source_offsets[source_glyph]);
        const auto end = static_cast<std::size_t>(
            source_offsets[source_glyph + 1U]);
        if (start > end || !can_read(source_glyf, start, end - start)) {
            throw subset_failure{};
        }
        const auto remapped = remap_composite(
            source_glyf.subspan(start, end - start), source_to_subset);
        glyf.insert(glyf.end(), remapped.begin(), remapped.end());
        glyf.resize(align4(glyf.size()));
    }
    if (glyf.size() > std::numeric_limits<std::uint32_t>::max()) {
        throw subset_failure{};
    }
    offsets.back() = static_cast<std::uint32_t>(glyf.size());
    std::vector<std::byte> loca(offsets.size() * 4U);
    for (std::size_t index = 0U; index < offsets.size(); ++index) {
        write_u32(loca, index * 4U, offsets[index]);
    }
    return {std::move(glyf), std::move(loca)};
}

std::vector<std::byte> build_compact_hmtx(
    std::span<const std::byte> hhea,
    std::span<const std::byte> hmtx,
    std::span<const std::uint16_t> glyph_order) {
    const auto metric_count = read_u16(hhea, 34U);
    if (metric_count == 0U) {
        throw subset_failure{};
    }
    std::vector<std::byte> result(glyph_order.size() * 4U);
    for (std::size_t index = 0U; index < glyph_order.size(); ++index) {
        const auto glyph = glyph_order[index];
        const std::size_t advance_offset = glyph < metric_count
            ? static_cast<std::size_t>(glyph) * 4U
            : static_cast<std::size_t>(metric_count - 1U) * 4U;
        const std::size_t bearing_offset = glyph < metric_count
            ? advance_offset + 2U
            : static_cast<std::size_t>(metric_count) * 4U +
                static_cast<std::size_t>(glyph - metric_count) * 2U;
        const auto advance = read_u16(hmtx, advance_offset);
        const auto bearing = can_read(hmtx, bearing_offset, 2U)
            ? read_i16(hmtx, bearing_offset)
            : 0;
        write_u16(result, index * 4U, advance);
        write_i16(result, index * 4U + 2U, bearing);
    }
    return result;
}

} // namespace

compact_subset_result build_compact_subset(
    std::span<const std::byte> font_data,
    std::size_t directory_offset,
    std::span<const std::uint16_t> glyphs) {
    auto face = parse_face(font_data, directory_offset);
    const auto* head = find_table(face, tag('h', 'e', 'a', 'd'));
    const auto* maxp = find_table(face, tag('m', 'a', 'x', 'p'));
    const auto* loca = find_table(face, tag('l', 'o', 'c', 'a'));
    const auto* glyf = find_table(face, tag('g', 'l', 'y', 'f'));
    if (head == nullptr || maxp == nullptr || loca == nullptr || glyf == nullptr ||
        head->bytes.size() < 54U || maxp->bytes.size() < 6U) {
        throw subset_failure{};
    }
    const auto glyph_count = read_u16(maxp->bytes, 4U);
    if (glyph_count == 0U) {
        throw subset_failure{};
    }
    const auto source_offsets = read_loca(
        loca->bytes, glyph_count, read_i16(head->bytes, 50U));
    std::vector<bool> included(glyph_count, false);
    included[0U] = true;
    for (const auto glyph : glyphs) {
        if (glyph < glyph_count) {
            included[glyph] = true;
        }
    }
    include_composite_dependencies(glyf->bytes, source_offsets, included);

    std::vector<std::uint16_t> glyph_order;
    for (std::size_t glyph = 0U; glyph < included.size(); ++glyph) {
        if (included[glyph]) {
            glyph_order.push_back(static_cast<std::uint16_t>(glyph));
        }
    }
    std::vector<std::uint16_t> source_to_subset(glyph_count, no_glyph);
    compact_subset_result result{};
    result.glyph_map.resize(glyph_order.size());
    for (std::size_t subset = 0U; subset < glyph_order.size(); ++subset) {
        const auto compact = static_cast<std::uint16_t>(subset);
        source_to_subset[glyph_order[subset]] = compact;
        result.glyph_map[subset] = {glyph_order[subset], compact};
    }
    auto glyph_subset = build_compact_glyphs(
        glyf->bytes, source_offsets, glyph_order, source_to_subset);
    auto subset_head = head->bytes;
    write_u32(subset_head, 8U, 0U);
    write_i16(subset_head, 50U, 1);
    auto subset_maxp = maxp->bytes;
    write_u16(subset_maxp, 4U,
        static_cast<std::uint16_t>(glyph_order.size()));

    std::vector<table_data> output_tables;
    output_tables.reserve(face.tables.size());
    for (auto& table : face.tables) {
        if (!skip_copied_table(table.tag)) {
            output_tables.push_back(std::move(table));
        }
    }
    output_tables.push_back({tag('h', 'e', 'a', 'd'), std::move(subset_head)});
    output_tables.push_back({tag('m', 'a', 'x', 'p'), std::move(subset_maxp)});
    output_tables.push_back({tag('l', 'o', 'c', 'a'),
        std::move(glyph_subset.loca)});
    output_tables.push_back({tag('g', 'l', 'y', 'f'),
        std::move(glyph_subset.glyf)});
    const auto* hhea = find_table(face, tag('h', 'h', 'e', 'a'));
    const auto* hmtx = find_table(face, tag('h', 'm', 't', 'x'));
    if (hhea != nullptr && hmtx != nullptr) {
        if (hhea->bytes.size() < 36U) {
            throw subset_failure{};
        }
        auto subset_hhea = hhea->bytes;
        write_u16(subset_hhea, 34U,
            static_cast<std::uint16_t>(glyph_order.size()));
        output_tables.push_back({tag('h', 'h', 'e', 'a'),
            std::move(subset_hhea)});
        output_tables.push_back({tag('h', 'm', 't', 'x'),
            build_compact_hmtx(hhea->bytes, hmtx->bytes, glyph_order)});
    }
    result.font = build_sfnt(face.sfnt_version, output_tables);
    return result;
}

} // namespace progpu::native::text::sfnt_subset_detail
