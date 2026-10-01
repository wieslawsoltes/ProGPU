#include "progpu_native_hinted_transport_controls.hpp"

#include <iostream>
#include <limits>

namespace {
using namespace progpu::native::text;
using progpu::native::text::tests::transport_require;

hinted_glyph_batch make_batch(std::size_t length)
{
    hinted_glyph_batch batch;
    auto identity = std::make_shared<hinted_font_identity>();
    const std::vector<std::byte> original(128U, std::byte{0x51});
    identity->source = std::make_shared<const owned_font_source>(original, 0U);
    identity->variation_coordinates_16_16 = {-65536, 65536, 0, 1};
    batch.identity = identity;
    hinted_glyph glyph;
    glyph.glyph_index = 17U;
    glyph.outline_flags = -1;
    glyph.advance_x_26_6 = std::numeric_limits<long>::min();
    glyph.advance_y_26_6 = std::numeric_limits<long>::max();
    glyph.horizontal_bearing_x_26_6 = -31;
    glyph.horizontal_bearing_y_26_6 = 37;
    glyph.width_26_6 = 43; glyph.height_26_6 = 47;
    glyph.horizontal_advance_26_6 = 53; glyph.vertical_bearing_x_26_6 = -59;
    glyph.vertical_bearing_y_26_6 = 61; glyph.vertical_advance_26_6 = 67;
    glyph.linear_horizontal_advance_16_16 = 65537; glyph.linear_vertical_advance_16_16 = -65539;
    glyph.left_side_bearing_delta_26_6 = -23; glyph.right_side_bearing_delta_26_6 = 29;
    for (std::size_t index = 0U; index < length; ++index) {
        glyph.points.push_back({index % 2U == 0U ? std::numeric_limits<long>::min() : -17L,
            index % 2U == 0U ? std::numeric_limits<long>::max() : 19L});
        glyph.tags.push_back(static_cast<std::uint8_t>(0xA0U | (index % 3U)));
        glyph.contour_ends.push_back(static_cast<std::int16_t>(index));
    }
    batch.glyphs = {glyph, hinted_glyph{}, glyph}; // repeats and a real empty descriptor
    return batch;
}

void failures_are_atomic()
{
    auto batch = make_batch(17U);
    std::array<progpu_native_hinted_glyph, 6> glyphs{};
    std::array<progpu_native_hinted_point, 40> points{};
    std::array<std::uint8_t, 40> tags{};
    std::array<std::int32_t, 40> contours{};
    std::memset(glyphs.data(), 0x5A, sizeof(glyphs));
    std::memset(points.data(), 0x5A, sizeof(points));
    tags.fill(0x5AU); contours.fill(-91);
    const auto saved_glyphs = glyphs;
    const auto saved_points = points;
    const auto saved_tags = tags;
    const auto saved_contours = contours;
    const auto unchanged = [&] {
        transport_require(std::memcmp(glyphs.data(), saved_glyphs.data(), sizeof(glyphs)) == 0 &&
            std::memcmp(points.data(), saved_points.data(), sizeof(points)) == 0 &&
            tags == saved_tags && contours == saved_contours);
    };
    const auto copy = [&](std::span<progpu_native_hinted_glyph> g, std::span<progpu_native_hinted_point> p,
        std::span<std::uint8_t> t, std::span<std::int32_t> c,
        hinted_transport_policy policy = hinted_transport_policy::automatic) {
        return copy_hinted_batch(batch, g, p, t, c, policy).error;
    };
    for (unsigned int missing = 0U; missing < 4U; ++missing) {
        transport_require(copy(std::span(glyphs).first(missing == 0U ? 2U : 6U),
            std::span(points).first(missing == 1U ? 33U : 40U),
            std::span(tags).first(missing == 2U ? 33U : 40U),
            std::span(contours).first(missing == 3U ? 33U : 40U)) == hinted_transport_error::insufficient_capacity);
        unchanged();
    }
    transport_require(copy(glyphs, points, tags, contours, static_cast<hinted_transport_policy>(713)) ==
        hinted_transport_error::invalid_argument);
    unchanged();
    // Entire capacities, not only written prefixes, must be disjoint.
    auto* point_tail = reinterpret_cast<std::uint8_t*>(points.data() + 34U);
    transport_require(copy(glyphs, points, std::span(point_tail, 40U), contours) == hinted_transport_error::invalid_argument);
    unchanged();
    for (auto* output_alias : {reinterpret_cast<std::uint8_t*>(glyphs.data() + 3U),
            reinterpret_cast<std::uint8_t*>(contours.data() + 30U)}) {
        transport_require(copy(glyphs, points, std::span(output_alias, 40U), contours) ==
            hinted_transport_error::invalid_argument);
        unchanged();
    }
    const auto original_tags = batch.glyphs[0].tags;
    transport_require(copy(glyphs, points, std::span(batch.glyphs[0].tags.data(), 17U), contours) ==
        hinted_transport_error::insufficient_capacity);
    // Alias a retained tag buffer with sufficient capacity by using one descriptor.
    auto single = batch;
    single.glyphs.resize(1U);
    transport_require(copy_hinted_batch(single, glyphs, points, single.glyphs[0].tags, contours,
        hinted_transport_policy::automatic).error == hinted_transport_error::invalid_argument);
    transport_require(single.glyphs[0].tags == original_tags);
    auto* font_bytes = reinterpret_cast<std::uint8_t*>(const_cast<std::byte*>(batch.identity->source->bytes.data()));
    transport_require(copy(glyphs, points, std::span(font_bytes, 128U), contours) == hinted_transport_error::invalid_argument);
    transport_require(batch.identity->source->bytes == std::vector<std::byte>(128U, std::byte{0x51}));
    auto& alias_counts = *reinterpret_cast<progpu_native_hinted_batch_counts*>(font_bytes);
    transport_require(get_hinted_batch_counts(batch, alias_counts) == hinted_transport_error::invalid_argument);
    transport_require(batch.identity->source->bytes == std::vector<std::byte>(128U, std::byte{0x51}));
    unchanged();
    // Failure in the final glyph must not publish earlier valid descriptors.
    for (unsigned int invalid = 0U; invalid < 5U; ++invalid) {
        auto bad = batch;
        auto& last = bad.glyphs.back();
        if (invalid == 0U) last.tags.pop_back();
        if (invalid == 1U) last.contour_ends[8] = last.contour_ends[7];
        if (invalid == 2U) last.contour_ends.back() = 15;
        if (invalid == 3U) last.contour_ends[0] = -1;
        if (invalid == 4U) last.contour_ends.clear();
        progpu_native_hinted_batch_counts counts{71U, 73U, 79U};
        transport_require(get_hinted_batch_counts(bad, counts) == hinted_transport_error::invalid_batch &&
            counts.glyphs == 71U && counts.points == 73U && counts.contours == 79U);
        transport_require(copy_hinted_batch(bad, glyphs, points, tags, contours,
            hinted_transport_policy::automatic).error == hinted_transport_error::invalid_batch);
        unchanged();
    }
    auto absent = batch;
    absent.identity.reset();
    transport_require(copy_hinted_batch(absent, glyphs, points, tags, contours,
        hinted_transport_policy::automatic).error == hinted_transport_error::invalid_batch);
    unchanged();
    absent.identity = std::make_shared<hinted_font_identity>();
    transport_require(copy_hinted_batch(absent, glyphs, points, tags, contours,
        hinted_transport_policy::automatic).error == hinted_transport_error::invalid_batch);
    unchanged();
}
} // namespace

int main()
{
    try {
        for (std::size_t length = 0U; length <= 17U; ++length)
            progpu::native::text::tests::verify_hinted_transport(make_batch(length));
        auto empty = make_batch(0U);
        empty.glyphs.clear();
        progpu::native::text::tests::verify_hinted_transport(empty);
        transport_require(static_cast<bool>(copy_hinted_batch(empty, {}, {}, {}, {}, hinted_transport_policy::automatic)));
        failures_are_atomic();
        std::cout << "{\"fixedWidthTransport\":true,\"exactIntegerDifferential\":true,\"atomicTailControls\":true}\n";
        return 0;
    } catch (const std::exception& error) {
        std::cerr << error.what() << '\n';
        return 1;
    }
}
