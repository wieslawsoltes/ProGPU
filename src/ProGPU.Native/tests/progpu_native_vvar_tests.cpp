#include "progpu_native_text.hpp"

#include <algorithm>
#include <array>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <limits>
#include <source_location>
#include <vector>

namespace {
using namespace progpu::native::text;
using bytes = std::vector<std::byte>;

void require(bool condition, std::source_location location = std::source_location::current()) {
    if (!condition) {
        std::fprintf(stderr, "VVAR requirement failed at line %u\n", location.line());
        std::abort();
    }
}
void put16(bytes& data, std::size_t offset, std::uint16_t value) {
    data[offset] = static_cast<std::byte>(value >> 8U);
    data[offset + 1U] = static_cast<std::byte>(value & 255U);
}
void put32(bytes& data, std::size_t offset, std::uint32_t value) {
    put16(data, offset, static_cast<std::uint16_t>(value >> 16U));
    put16(data, offset + 2U, static_cast<std::uint16_t>(value & 65535U));
}
template<class T> auto snapshot(const T& value) {
    std::array<std::byte, sizeof(T)> result{};
    std::memcpy(result.data(), &value, sizeof(T));
    return result;
}

constexpr std::size_t store_offset = 24U;
constexpr std::size_t regions_offset = 48U;
constexpr std::size_t rows_offset = 64U;
constexpr std::size_t row_table_size = 22U;
constexpr std::size_t maps_offset = 152U;
constexpr std::array<std::int16_t, 1> positive_half{8192};

bytes variation_table() {
    // Independent original three-glyph, one-axis font metadata. Four unrelated
    // metric families, two regions and signed literal deltas make wrong map,
    // region, row or metric selection observable without an outline engine.
    bytes table(173U);
    put32(table, 0U, 0x00010000U);
    put32(table, 4U, store_offset);
    put16(table, store_offset, 1U);
    put32(table, store_offset + 2U, regions_offset - store_offset);
    put16(table, store_offset + 6U, 4U);
    put16(table, regions_offset, 1U);
    put16(table, regions_offset + 2U, 2U);
    put16(table, regions_offset + 4U, 0U);
    put16(table, regions_offset + 6U, 16384U);
    put16(table, regions_offset + 8U, 16384U);
    put16(table, regions_offset + 10U, static_cast<std::uint16_t>(-16384));
    put16(table, regions_offset + 12U, static_cast<std::uint16_t>(-16384));
    put16(table, regions_offset + 14U, 0U);
    constexpr std::int16_t deltas[4][6]{
        {10, -20, 30, -40, 50, -60},
        {2, -4, 6, -8, 10, -12},
        {-3, 6, -9, 12, -15, 18},
        {7, -14, 21, -28, 35, -42}};
    for (std::size_t kind = 0U; kind < 4U; ++kind) {
        const auto offset = rows_offset + kind * row_table_size;
        put32(table, store_offset + 8U + kind * 4U,
            static_cast<std::uint32_t>(offset - store_offset));
        put16(table, offset, 3U);
        put16(table, offset + 2U, 2U);
        put16(table, offset + 4U, 2U);
        put16(table, offset + 6U, 0U);
        put16(table, offset + 8U, 1U);
        for (std::size_t index = 0U; index < 6U; ++index)
            put16(table, offset + 10U + index * 2U, static_cast<std::uint16_t>(deltas[kind][index]));
        if (kind == 0U) continue; // Advance intentionally uses implicit glyph IDs.
        const auto map = maps_offset + (kind - 1U) * 7U;
        put32(table, 8U + kind * 4U, static_cast<std::uint32_t>(map));
        table[map + 1U] = std::byte{1}; // One byte, two inner bits.
        put16(table, map + 2U, 3U);
        for (std::size_t glyph = 0U; glyph < 3U; ++glyph)
            table[map + 4U + glyph] = static_cast<std::byte>(kind * 4U + glyph);
    }
    return table;
}

bytes font_bytes(std::span<const std::byte> table, bool has_vvar = true) {
    // Minimal SFNT required by the neutral metadata reader, not a drawable
    // source-font fixture. No VORG/vmtx or guessed source vertical metrics.
    constexpr std::size_t maxp = 60U;
    constexpr std::size_t fvar = 92U;
    constexpr std::size_t vvar = 128U;
    bytes font(vvar + table.size());
    put32(font, 0U, 0x00010000U);
    put16(font, 4U, 3U);
    constexpr std::array<std::uint32_t, 3> tags{0x6D617870U, 0x66766172U, 0x56564152U};
    const std::array<std::size_t, 3> offsets{maxp, fvar, vvar};
    const std::array<std::size_t, 3> lengths{32U, 36U, table.size()};
    for (std::size_t index = 0U; index < 3U; ++index) {
        put32(font, 12U + index * 16U, index == 2U && !has_vvar ? 0x7A7A7A7AU : tags[index]);
        put32(font, 20U + index * 16U, static_cast<std::uint32_t>(offsets[index]));
        put32(font, 24U + index * 16U, static_cast<std::uint32_t>(lengths[index]));
    }
    put32(font, maxp, 0x00010000U);
    put16(font, maxp + 4U, 3U);
    put32(font, fvar, 0x00010000U);
    put16(font, fvar + 4U, 16U);
    put16(font, fvar + 6U, 2U);
    put16(font, fvar + 8U, 1U);
    put16(font, fvar + 10U, 20U);
    put16(font, fvar + 14U, 8U);
    put32(font, fvar + 16U, 0x77676874U);
    put32(font, fvar + 20U, 0xFFFF0000U);
    put32(font, fvar + 28U, 0x00010000U);
    put16(font, fvar + 34U, 256U);
    std::copy(table.begin(), table.end(), font.begin() + vvar);
    return font;
}
sfnt_font_view view(const bytes& data, std::uint32_t face = 0U) {
    sfnt_font_view result{};
    require(sfnt_font_view::try_create(data, face, result));
    return result;
}
sfnt_vertical_metrics_variation sentinel() {
    return {91.0F, 92.0F, 93.0F, 94.0F, true, true, true, true};
}
void metric(const sfnt_vertical_metrics_variation& value, float advance, float top,
    float bottom, float origin, bool has_top = true, bool has_bottom = true, bool has_origin = true) {
    require(value.uses_vvar && value.advance_height == advance && value.top_side_bearing == top &&
        value.bottom_side_bearing == bottom && value.vertical_origin_y == origin &&
        value.has_top_side_bearing == has_top && value.has_bottom_side_bearing == has_bottom &&
        value.has_vertical_origin_y == has_origin);
}
sfnt_vertical_metrics_variation evaluate(const bytes& table, std::uint16_t glyph = 1U) {
    const auto data = font_bytes(table);
    const auto font = view(data);
    std::array<float, 3> scalars{-9.0F, -8.0F, -7.0F};
    sfnt_vertical_metrics_variation_instance instance{};
    require(font.try_prepare_vertical_metrics_variation(positive_half, scalars, instance));
    require(scalars[0] == 0.5F && scalars[1] == 0.0F && scalars[2] == -7.0F);
    auto result = sentinel();
    require(font.try_get_vertical_metrics_variation(glyph, instance, result));
    return result;
}

void invalid_font(const bytes& bad_data) {
    const auto good_data = font_bytes(variation_table());
    const auto good_font = view(good_data);
    std::array<float, 3> original_scalars{};
    sfnt_vertical_metrics_variation_instance original{};
    require(good_font.try_prepare_vertical_metrics_variation(positive_half, original_scalars, original));
    const auto before_instance = snapshot(original);
    const auto bad_font = view(bad_data);
    std::array<float, 3> scalars{-91.0F, -92.0F, -93.0F};
    const auto before_scalars = scalars;
    auto error = font_error::none;
    require(!bad_font.try_prepare_vertical_metrics_variation(positive_half, scalars, original, &error));
    require(error == font_error::invalid_face && snapshot(original) == before_instance && scalars == before_scalars);
    std::uint16_t count = 99U;
    bool uses = false;
    require(!bad_font.try_get_vertical_metrics_variation_region_count(positive_half, count, uses, &error));
    require(count == 99U && !uses && error == font_error::invalid_face);
    sfnt_vertical_metrics_variation retained{};
    require(good_font.try_get_vertical_metrics_variation(1U, original, retained));
    metric(retained, 15.0F, 3.0F, -4.5F, 10.5F);
}

void invalid_table(const bytes& table) { invalid_font(font_bytes(table)); }

void malformed_directories() {
    const auto original = font_bytes(variation_table());
    for (const auto offset : {0xFFFFFFF0U, static_cast<std::uint32_t>(original.size())}) {
        auto damaged = original;
        put32(damaged, 52U, offset);
        invalid_font(damaged);
    }
    auto damaged = original;
    put32(damaged, 56U, 0xFFFFFFFFU);
    invalid_font(damaged);
    // Add a complete fourth record without changing either required table.
    // Identical duplicate bytes are still ambiguous original source metadata.
    bytes duplicate(original.size() + 16U);
    std::copy_n(original.begin(), 60U, duplicate.begin());
    std::copy(original.begin() + 60U, original.end(), duplicate.begin() + 76U);
    put16(duplicate, 4U, 4U);
    put32(duplicate, 20U, 76U);
    put32(duplicate, 36U, 108U);
    put32(duplicate, 52U, 144U);
    std::copy_n(duplicate.begin() + 44U, 16U, duplicate.begin() + 60U);
    invalid_font(duplicate);
    // A malformed duplicate may precede or follow the valid record. Neither
    // the legacy reverse lookup nor a first-match lookup can select it away.
    for (const auto record : {44U, 60U}) {
        damaged = duplicate;
        put32(damaged, record + 8U, 0xFFFFFFF0U);
        invalid_font(damaged);
    }
}

void valid_maps_and_instances() {
    const auto table = variation_table();
    const auto data = font_bytes(table);
    const auto font = view(data);
    std::uint16_t count = 99U;
    bool uses = false;
    require(font.try_get_vertical_metrics_variation_region_count(positive_half, count, uses));
    require(uses && count == 2U);
    std::array<float, 3> scalars{};
    sfnt_vertical_metrics_variation_instance instance{};
    require(font.try_prepare_vertical_metrics_variation(positive_half, scalars, instance));
    require(instance.uses_vvar() && instance.region_count() == 2U);
    const std::array<std::uint16_t, 4> glyphs{2U, 0U, 1U, 2U};
    std::array<sfnt_vertical_metrics_variation, 5> outputs{};
    outputs.back() = sentinel();
    const auto tail = snapshot(outputs.back());
    require(font.try_get_vertical_metrics_variation(glyphs, instance, outputs));
    metric(outputs[0], 25.0F, 5.0F, -7.5F, 17.5F);
    metric(outputs[1], 5.0F, 1.0F, -1.5F, 3.5F);
    metric(outputs[2], 15.0F, 3.0F, -4.5F, 10.5F);
    metric(outputs[3], 25.0F, 5.0F, -7.5F, 17.5F);
    require(snapshot(outputs.back()) == tail);
    const auto copied_instance = instance;
    const auto copied_view = font;
    require(copied_view.try_get_vertical_metrics_variation(1U, copied_instance, outputs[0]));
    constexpr std::array<std::int16_t, 1> negative_half{-8192};
    std::array<float, 2> negative_scalars{};
    sfnt_vertical_metrics_variation_instance negative{};
    require(font.try_prepare_vertical_metrics_variation(negative_half, negative_scalars, negative));
    require(font.try_get_vertical_metrics_variation(1U, negative, outputs[0]));
    metric(outputs[0], -20.0F, -4.0F, 6.0F, -14.0F);
    // Preparing another instance cannot mutate the first scalar generation.
    require(font.try_get_vertical_metrics_variation(1U, instance, outputs[0]));
    metric(outputs[0], 15.0F, 3.0F, -4.5F, 10.5F);
    constexpr std::array<std::int16_t, 1> default_coordinate{0};
    require(font.try_prepare_vertical_metrics_variation(default_coordinate, negative_scalars, negative));
    require(font.try_get_vertical_metrics_variation(1U, negative, outputs[0]));
    metric(outputs[0], 0.0F, 0.0F, 0.0F, 0.0F);

    auto changed = table;
    // Explicit advance permutation is not the implicit row order.
    put32(changed, 8U, maps_offset);
    changed[maps_offset + 4U] = std::byte{2};
    changed[maps_offset + 5U] = std::byte{0};
    changed[maps_offset + 6U] = std::byte{1};
    metric(evaluate(changed), 5.0F, 5.0F, -4.5F, 10.5F);
    changed = table;
    put16(changed, maps_offset + 2U, 1U); // Last entry repeats.
    metric(evaluate(changed, 2U), 25.0F, 1.0F, -7.5F, 17.5F);
    changed = table;
    put32(changed, 12U, 0U);
    put32(changed, 16U, 0U);
    put32(changed, 20U, 0U);
    metric(evaluate(changed), 15.0F, 0.0F, 0.0F, 0.0F, false, false, false);
    changed = table;
    put32(changed, store_offset + 12U, 0U); // Null optional row table.
    changed[maps_offset + 5U] = std::byte{7}; // Inner3 is ignored for null.
    metric(evaluate(changed), 15.0F, 0.0F, -4.5F, 10.5F);
    changed = table;
    put32(changed, store_offset + 8U, 0U); // Null implicit advance table.
    metric(evaluate(changed), 0.0F, 3.0F, -4.5F, 10.5F);
    changed = table;
    const auto sentinel_map = changed.size();
    changed.resize(sentinel_map + 8U);
    put32(changed, 12U, static_cast<std::uint32_t>(sentinel_map));
    changed[sentinel_map + 1U] = std::byte{0x3F};
    put16(changed, sentinel_map + 2U, 1U);
    put32(changed, sentinel_map + 4U, 0xFFFFFFFFU);
    metric(evaluate(changed), 15.0F, 0.0F, -4.5F, 10.5F);
    // Mixed int16/int8 deltas retain signed arithmetic, not unsigned narrowing.
    changed = table;
    put16(changed, rows_offset + 2U, 1U);
    constexpr std::int16_t first_words[3]{10, 30, 50};
    constexpr std::int8_t second_bytes[3]{-20, -40, -60};
    for (std::size_t row = 0U; row < 3U; ++row) {
        put16(changed, rows_offset + 10U + row * 3U, static_cast<std::uint16_t>(first_words[row]));
        changed[rows_offset + 12U + row * 3U] = static_cast<std::byte>(second_bytes[row]);
    }
    metric(evaluate(changed), 15.0F, 3.0F, -4.5F, 10.5F);
}

void malformed_tables() {
    const auto table = variation_table();
    for (std::size_t length = 0U; length < table.size(); ++length)
        invalid_table(bytes(table.begin(), table.begin() + static_cast<std::ptrdiff_t>(length)));
    const auto bad16 = [&](std::size_t offset, std::uint16_t value) {
        auto changed = table; put16(changed, offset, value); invalid_table(changed);
    };
    const auto bad32 = [&](std::size_t offset, std::uint32_t value) {
        auto changed = table; put32(changed, offset, value); invalid_table(changed);
    };
    bad32(0U, 0x00010001U);
    bad32(0U, 0x00020000U);
    bad32(4U, 0U);
    bad32(4U, 0xFFFFFFFFU);
    bad32(12U, 0xFFFFFFFFU);
    bad32(20U, 23U);
    bad16(store_offset, 2U);
    bad32(store_offset + 2U, 0U);
    bad32(store_offset + 2U, 0xFFFFFFFFU);
    bad32(store_offset + 20U, 0xFFFFFFFFU);
    bad16(regions_offset, 2U);
    bad16(regions_offset + 2U, 0x8002U);
    bad16(regions_offset + 4U, static_cast<std::uint16_t>(-16385));
    bad16(regions_offset + 6U, 16385U);
    bad16(regions_offset + 8U, 16385U);
    // A legal start8192 alone is valid; use a reversed interval explicitly.
    auto changed = table;
    put16(changed, regions_offset + 4U, 8192U);
    put16(changed, regions_offset + 6U, 4096U);
    invalid_table(changed);
    changed = table;
    put16(changed, regions_offset + 4U, static_cast<std::uint16_t>(-1));
    invalid_table(changed); // Cross-origin support with nonzero peak.
    bad16(rows_offset + 4U, 1U); // Word count greater than region count.
    bad16(rows_offset + 8U, 2U); // Invalid region even when its scalar is zero.
    bad16(rows_offset, 2U); // Implicit advance map lacks final glyph row.
    bad16(maps_offset + 2U, 0U);
    for (const auto reserved : {0x40U, 0x80U, 0xC0U}) {
        changed = table;
        changed[maps_offset + 1U] = static_cast<std::byte>(reserved | 1U);
        invalid_table(changed);
    }
    changed = table; changed[maps_offset] = std::byte{1}; invalid_table(changed);
    changed = table; changed[maps_offset + 4U] = std::byte{7}; invalid_table(changed); // Inner3.
    changed = table; changed[maps_offset + 4U] = std::byte{16}; invalid_table(changed); // Outer4.
    const auto overflow_map = table.size();
    changed = table; changed.resize(overflow_map + 8U);
    put32(changed, 20U, static_cast<std::uint32_t>(overflow_map));
    changed[overflow_map + 1U] = std::byte{0x30}; // Four bytes, one inner bit.
    put16(changed, overflow_map + 2U, 1U);
    put32(changed, overflow_map + 4U, 0x00020000U); // Outer65536 must not wrap0.
    invalid_table(changed);
    changed = table; changed.resize(overflow_map + 8U);
    put32(changed, 20U, static_cast<std::uint32_t>(overflow_map));
    changed[overflow_map + 1U] = std::byte{0x3F};
    put16(changed, overflow_map + 2U, 1U);
    put32(changed, overflow_map + 4U, 0xFFFF0000U); // Not the no-variation pair.
    invalid_table(changed);
    changed = table;
    put16(changed, maps_offset + 2U, 4U); // Unused entry3 lies in next map's zero byte.
    changed[maps_offset + 7U] = std::byte{16};
    put32(changed, 16U, 0U); // Remove next map so only unused bad entry rejects.
    invalid_table(changed);
}

void atomic_ownership_and_aliases() {
    const auto data = font_bytes(variation_table());
    const auto font = view(data);
    std::array<float, 3> scalars{-91.0F, -92.0F, -93.0F};
    sfnt_vertical_metrics_variation_instance instance{};
    auto before = snapshot(instance);
    auto before_scalars = scalars;
    auto error = font_error::none;
    require(!font.try_prepare_vertical_metrics_variation(positive_half,
        std::span<float>(scalars).first(1U), instance, &error));
    require(error == font_error::insufficient_buffer && before == snapshot(instance) && scalars == before_scalars);
    const std::array<std::int16_t, 2> extra{8192, 0};
    const std::array<std::int16_t, 1> outside{16385};
    for (const auto coordinates : {std::span<const std::int16_t>{},
        std::span<const std::int16_t>(extra), std::span<const std::int16_t>(outside)}) {
        require(!font.try_prepare_vertical_metrics_variation(coordinates, scalars, instance, &error));
        require(error == font_error::invalid_argument && before == snapshot(instance) && scalars == before_scalars);
    }
    require(font.try_prepare_vertical_metrics_variation(positive_half, scalars, instance));
    auto output = sentinel();
    const auto output_before = snapshot(output);
    const sfnt_vertical_metrics_variation_instance unprepared{};
    require(!font.try_get_vertical_metrics_variation(0U, unprepared, output, &error));
    require(snapshot(output) == output_before && error == font_error::invalid_argument);
    const auto foreign_data = data;
    const auto foreign = view(foreign_data);
    require(!foreign.try_get_vertical_metrics_variation(0U, instance, output, &error));
    require(snapshot(output) == output_before && error == font_error::invalid_argument);
    const std::array<std::uint16_t, 2> late_invalid{0U, 3U};
    std::array<sfnt_vertical_metrics_variation, 3> outputs{sentinel(), sentinel(), sentinel()};
    const auto outputs_before = snapshot(outputs);
    require(!font.try_get_vertical_metrics_variation(late_invalid, instance, outputs, &error));
    require(snapshot(outputs) == outputs_before && error == font_error::invalid_argument);
    const std::array<std::uint16_t, 2> valid{0U, 2U};
    require(!font.try_get_vertical_metrics_variation(valid, instance,
        std::span<sfnt_vertical_metrics_variation>(outputs).first(1U), &error));
    require(snapshot(outputs) == outputs_before && error == font_error::insufficient_buffer);
    for (const auto bad_scalar : {std::numeric_limits<float>::quiet_NaN(),
        std::numeric_limits<float>::infinity(), -0.25F, 1.25F}) {
        scalars[1] = bad_scalar;
        require(!font.try_get_vertical_metrics_variation(valid, instance, outputs, &error));
        require(snapshot(outputs) == outputs_before && error == font_error::invalid_argument);
    }
    scalars[1] = 0.0F;
    // Every alias below is rejected by ranges before a typed store/load through
    // the intentionally overlapping span; no invalid object is dereferenced.
    before = snapshot(instance);
    require(!font.try_prepare_vertical_metrics_variation(positive_half,
        std::span<float>(reinterpret_cast<float*>(&instance), 2U), instance, &error));
    require(snapshot(instance) == before && error == font_error::invalid_argument);
    require(!font.try_prepare_vertical_metrics_variation(
        std::span<const std::int16_t>(reinterpret_cast<const std::int16_t*>(scalars.data()), 1U),
        scalars, instance, &error));
    require(snapshot(instance) == before);
    const auto instance_error_before = snapshot(instance);
    require(!font.try_get_vertical_metrics_variation(0U, instance, output,
        reinterpret_cast<font_error*>(&instance)));
    require(snapshot(instance) == instance_error_before && snapshot(output) == output_before);
    require(!font.try_get_vertical_metrics_variation(valid, instance,
        std::span<sfnt_vertical_metrics_variation>(reinterpret_cast<sfnt_vertical_metrics_variation*>(scalars.data()), 1U),
        &error));
    require(error == font_error::invalid_argument && scalars[0] == 0.5F && scalars[1] == 0.0F);
    require(!font.try_get_vertical_metrics_variation(
        std::span<const std::uint16_t>(reinterpret_cast<const std::uint16_t*>(outputs.data()), 1U),
        instance, outputs, &error));
    require(snapshot(outputs) == outputs_before);
    std::uint16_t count = 99U;
    bool uses = false;
    require(!font.try_get_vertical_metrics_variation_region_count(
        std::span<const std::int16_t>(reinterpret_cast<const std::int16_t*>(&count), 1U), count, uses, &error));
    require(count == 99U && !uses && error == font_error::invalid_argument);

    // Same byte owner and same directory offset, but different original TTC
    // face indices, must not exchange prepared instances.
    bytes collection(data.size() + 20U);
    put32(collection, 0U, 0x74746366U);
    put32(collection, 4U, 0x00010000U);
    put32(collection, 8U, 2U);
    put32(collection, 12U, 20U);
    put32(collection, 16U, 20U);
    std::copy(data.begin(), data.end(), collection.begin() + 20U);
    put32(collection, 40U, 80U);
    put32(collection, 56U, 112U);
    put32(collection, 72U, 148U);
    const auto face0 = view(collection, 0U);
    const auto face1 = view(collection, 1U);
    sfnt_vertical_metrics_variation_instance face_instance{};
    std::array<float, 2> face_scalars{};
    require(face0.try_prepare_vertical_metrics_variation(positive_half, face_scalars, face_instance));
    require(!face1.try_get_vertical_metrics_variation(0U, face_instance, output, &error));
    require(error == font_error::invalid_argument && snapshot(output) == output_before);

    const auto absent_data = font_bytes(variation_table(), false);
    const auto absent_font = view(absent_data);
    before_scalars = scalars;
    require(absent_font.try_prepare_vertical_metrics_variation(positive_half, scalars, instance));
    require(!instance.uses_vvar() && instance.region_count() == 0U && scalars == before_scalars);
    require(absent_font.try_get_vertical_metrics_variation(1U, instance, output));
    require(!output.uses_vvar && !output.has_top_side_bearing && !output.has_bottom_side_bearing &&
        !output.has_vertical_origin_y && output.advance_height == 0.0F && output.top_side_bearing == 0.0F &&
        output.bottom_side_bearing == 0.0F && output.vertical_origin_y == 0.0F);
}
} // namespace

int main() {
    valid_maps_and_instances();
    malformed_tables();
    malformed_directories();
    atomic_ownership_and_aliases();
    std::puts("strict retained VVAR controls passed");
}
