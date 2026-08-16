#include "progpu_native_sfnt_subset_internal.hpp"

#include <algorithm>
#include <deque>
#include <limits>

namespace progpu::native::text::sfnt_subset_detail {
namespace {

constexpr std::uint16_t composite_more_components = 0x0020U;
constexpr std::uint16_t composite_args_are_words = 0x0001U;
constexpr std::uint16_t composite_has_scale = 0x0008U;
constexpr std::uint16_t composite_has_xy_scale = 0x0040U;
constexpr std::uint16_t composite_has_two_by_two = 0x0080U;

std::vector<std::uint32_t> read_loca(
    std::span<const std::byte> loca,
    std::uint16_t glyph_count,
    std::int16_t format) {
    std::vector<std::uint32_t> offsets(
        static_cast<std::size_t>(glyph_count) + 1U);
    if (format == 0) {
        if (!can_read(loca, 0U, offsets.size() * 2U)) {
            throw subset_failure{};
        }
        for (std::size_t index = 0U; index < offsets.size(); ++index) {
            offsets[index] = static_cast<std::uint32_t>(
                read_u16(loca, index * 2U)) * 2U;
        }
    } else if (format == 1) {
        if (!can_read(loca, 0U, offsets.size() * 4U)) {
            throw subset_failure{};
        }
        for (std::size_t index = 0U; index < offsets.size(); ++index) {
            offsets[index] = read_u32(loca, index * 4U);
        }
    } else {
        throw subset_failure{};
    }
    return offsets;
}

void include_composite_dependencies(
    std::span<const std::byte> glyf,
    std::span<const std::uint32_t> offsets,
    std::vector<bool>& included) {
    std::deque<std::uint16_t> queue;
    for (std::size_t index = 0U; index < included.size(); ++index) {
        if (included[index]) {
            queue.push_back(static_cast<std::uint16_t>(index));
        }
    }
    while (!queue.empty()) {
        const auto glyph = queue.front();
        queue.pop_front();
        const auto start = static_cast<std::size_t>(offsets[glyph]);
        const auto end = static_cast<std::size_t>(offsets[glyph + 1U]);
        if (start == end) {
            continue;
        }
        if (start > end || !can_read(glyf, start, end - start)) {
            throw subset_failure{};
        }
        const auto glyph_data = glyf.subspan(start, end - start);
        if (!can_read(glyph_data, 0U, 10U) || read_i16(glyph_data, 0U) >= 0) {
            continue;
        }
        std::size_t offset = 10U;
        std::uint16_t flags = 0U;
        do {
            if (!can_read(glyph_data, offset, 4U)) {
                throw subset_failure{};
            }
            flags = read_u16(glyph_data, offset);
            const auto component = read_u16(glyph_data, offset + 2U);
            if (component < included.size() && !included[component]) {
                included[component] = true;
                queue.push_back(component);
            }
            offset += 4U;
            offset += (flags & composite_args_are_words) != 0U ? 4U : 2U;
            if ((flags & composite_has_scale) != 0U) {
                offset += 2U;
            } else if ((flags & composite_has_xy_scale) != 0U) {
                offset += 4U;
            } else if ((flags & composite_has_two_by_two) != 0U) {
                offset += 8U;
            }
            if (offset > glyph_data.size()) {
                throw subset_failure{};
            }
        } while ((flags & composite_more_components) != 0U);
    }
}

glyph_table_subset build_glyph_subset(
    std::span<const std::byte> source_glyf,
    std::span<const std::uint32_t> source_offsets,
    const std::vector<bool>& included) {
    std::vector<std::uint32_t> offsets(source_offsets.size());
    std::vector<std::byte> glyf;
    for (std::size_t glyph = 0U; glyph < included.size(); ++glyph) {
        if (glyf.size() > std::numeric_limits<std::uint32_t>::max()) {
            throw subset_failure{};
        }
        offsets[glyph] = static_cast<std::uint32_t>(glyf.size());
        if (!included[glyph]) {
            continue;
        }
        const auto start = static_cast<std::size_t>(source_offsets[glyph]);
        const auto end = static_cast<std::size_t>(source_offsets[glyph + 1U]);
        if (start > end || !can_read(source_glyf, start, end - start)) {
            throw subset_failure{};
        }
        glyf.insert(glyf.end(),
            source_glyf.begin() + static_cast<std::ptrdiff_t>(start),
            source_glyf.begin() + static_cast<std::ptrdiff_t>(end));
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

} // namespace

std::vector<std::byte> build_glyph_id_preserving_subset(
    std::span<const std::byte> font_data,
    std::size_t directory_offset,
    std::span<const std::uint16_t> glyphs) {
    auto face = parse_face(font_data, directory_offset);
    const auto* head = find_table(face, tag('h', 'e', 'a', 'd'));
    const auto* maxp = find_table(face, tag('m', 'a', 'x', 'p'));
    const auto* loca = find_table(face, tag('l', 'o', 'c', 'a'));
    const auto* glyf = find_table(face, tag('g', 'l', 'y', 'f'));
    if (head == nullptr || maxp == nullptr || loca == nullptr || glyf == nullptr) {
        std::vector<table_data> copied;
        copied.reserve(face.tables.size());
        for (auto& table : face.tables) {
            if (table.tag != tag('D', 'S', 'I', 'G')) {
                copied.push_back(std::move(table));
            }
        }
        return build_sfnt(face.sfnt_version, copied);
    }
    if (head->bytes.size() < 54U || maxp->bytes.size() < 6U) {
        throw subset_failure{};
    }
    const auto glyph_count = read_u16(maxp->bytes, 4U);
    if (glyph_count == 0U) {
        std::vector<table_data> copied;
        for (auto& table : face.tables) {
            if (table.tag != tag('D', 'S', 'I', 'G')) {
                copied.push_back(std::move(table));
            }
        }
        return build_sfnt(face.sfnt_version, copied);
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
    auto subset = build_glyph_subset(glyf->bytes, source_offsets, included);

    std::vector<table_data> output_tables;
    output_tables.reserve(face.tables.size());
    for (auto& table : face.tables) {
        if (table.tag != tag('D', 'S', 'I', 'G') &&
            table.tag != tag('h', 'e', 'a', 'd') &&
            table.tag != tag('l', 'o', 'c', 'a') &&
            table.tag != tag('g', 'l', 'y', 'f')) {
            output_tables.push_back(std::move(table));
        }
    }
    auto subset_head = head->bytes;
    write_u32(subset_head, 8U, 0U);
    write_i16(subset_head, 50U, 1);
    output_tables.push_back({tag('h', 'e', 'a', 'd'), std::move(subset_head)});
    output_tables.push_back({tag('l', 'o', 'c', 'a'), std::move(subset.loca)});
    output_tables.push_back({tag('g', 'l', 'y', 'f'), std::move(subset.glyf)});
    return build_sfnt(face.sfnt_version, output_tables);
}

} // namespace progpu::native::text::sfnt_subset_detail
