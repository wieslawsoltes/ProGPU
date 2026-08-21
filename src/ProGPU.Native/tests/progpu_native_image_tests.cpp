#include "progpu_native_image.hpp"

#include <algorithm>
#include <array>
#include <cstddef>
#include <cstdint>
#include <cstdlib>
#include <span>
#include <vector>

namespace {

using progpu::native::image::image_error;
using progpu::native::image::png_decode_requirements;
using progpu::native::image::try_decode_png_rgba;
using progpu::native::image::try_get_png_decode_requirements;

void require(bool condition) {
    if (!condition) {
        std::abort();
    }
}

void append_u32(std::vector<std::byte>& bytes, std::uint32_t value) {
    bytes.push_back(static_cast<std::byte>(value >> 24U));
    bytes.push_back(static_cast<std::byte>(value >> 16U));
    bytes.push_back(static_cast<std::byte>(value >> 8U));
    bytes.push_back(static_cast<std::byte>(value));
}

std::uint32_t crc32(std::span<const std::byte> bytes) {
    auto crc = 0xFFFFFFFFU;
    for (const auto value : bytes) {
        crc ^= std::to_integer<std::uint8_t>(value);
        for (std::uint32_t bit = 0U; bit < 8U; ++bit) {
            const auto mask = 0U - (crc & 1U);
            crc = (crc >> 1U) ^ (0xEDB88320U & mask);
        }
    }
    return crc ^ 0xFFFFFFFFU;
}

void append_chunk(
    std::vector<std::byte>& png,
    const std::array<char, 4U>& type,
    std::span<const std::byte> data) {
    append_u32(png, static_cast<std::uint32_t>(data.size()));
    const auto crc_start = png.size();
    for (const auto character : type) {
        png.push_back(static_cast<std::byte>(
            static_cast<unsigned char>(character)));
    }
    png.insert(png.end(), data.begin(), data.end());
    append_u32(png, crc32(std::span<const std::byte>(png).subspan(
        crc_start, 4U + data.size())));
}

std::vector<std::byte> zlib_stored(std::span<const std::byte> input) {
    require(input.size() <= 65535U);
    std::vector<std::byte> result{
        std::byte{0x78U}, std::byte{0x01U}, std::byte{0x01U}};
    const auto length = static_cast<std::uint16_t>(input.size());
    const auto inverse = static_cast<std::uint16_t>(~length);
    result.push_back(static_cast<std::byte>(length));
    result.push_back(static_cast<std::byte>(length >> 8U));
    result.push_back(static_cast<std::byte>(inverse));
    result.push_back(static_cast<std::byte>(inverse >> 8U));
    result.insert(result.end(), input.begin(), input.end());
    auto first = 1U;
    auto second = 0U;
    for (const auto value : input) {
        first = (first + std::to_integer<std::uint8_t>(value)) % 65521U;
        second = (second + first) % 65521U;
    }
    append_u32(result, (second << 16U) | first);
    return result;
}

std::vector<std::byte> make_png(
    std::uint32_t width,
    std::uint32_t height,
    std::uint8_t color_type,
    std::span<const std::byte> filtered,
    std::span<const std::byte> palette = {},
    std::span<const std::byte> transparency = {},
    std::uint8_t interlace = 0U,
    std::uint8_t bit_depth = 8U) {
    std::vector<std::byte> png{
        std::byte{0x89U}, std::byte{'P'}, std::byte{'N'}, std::byte{'G'},
        std::byte{0x0DU}, std::byte{0x0AU}, std::byte{0x1AU}, std::byte{0x0AU}};
    std::array<std::byte, 13U> header{};
    header[0U] = static_cast<std::byte>(width >> 24U);
    header[1U] = static_cast<std::byte>(width >> 16U);
    header[2U] = static_cast<std::byte>(width >> 8U);
    header[3U] = static_cast<std::byte>(width);
    header[4U] = static_cast<std::byte>(height >> 24U);
    header[5U] = static_cast<std::byte>(height >> 16U);
    header[6U] = static_cast<std::byte>(height >> 8U);
    header[7U] = static_cast<std::byte>(height);
    header[8U] = static_cast<std::byte>(bit_depth);
    header[9U] = static_cast<std::byte>(color_type);
    header[12U] = static_cast<std::byte>(interlace);
    append_chunk(png, {'I', 'H', 'D', 'R'}, header);
    if (!palette.empty()) {
        append_chunk(png, {'P', 'L', 'T', 'E'}, palette);
    }
    if (!transparency.empty()) {
        append_chunk(png, {'t', 'R', 'N', 'S'}, transparency);
    }
    const auto compressed = zlib_stored(filtered);
    const auto split = compressed.size() / 2U;
    append_chunk(png, {'I', 'D', 'A', 'T'},
        std::span<const std::byte>(compressed).first(split));
    append_chunk(png, {'I', 'D', 'A', 'T'},
        std::span<const std::byte>(compressed).subspan(split));
    append_chunk(png, {'I', 'E', 'N', 'D'}, {});
    return png;
}

std::vector<std::byte> encode_filters(
    std::span<const std::byte> pixels,
    std::size_t width,
    std::size_t height,
    std::size_t bytes_per_pixel) {
    const auto row_bytes = width * bytes_per_pixel;
    std::vector<std::byte> filtered(height * (row_bytes + 1U));
    for (std::size_t row = 0U; row < height; ++row) {
        const auto filter = static_cast<std::uint8_t>(row % 5U);
        filtered[row * (row_bytes + 1U)] = static_cast<std::byte>(filter);
        for (std::size_t column = 0U; column < row_bytes; ++column) {
            const auto source = row * row_bytes + column;
            const auto value = std::to_integer<std::uint8_t>(pixels[source]);
            const auto left = column >= bytes_per_pixel
                ? std::to_integer<std::uint8_t>(
                    pixels[source - bytes_per_pixel])
                : 0U;
            const auto above = row != 0U
                ? std::to_integer<std::uint8_t>(pixels[source - row_bytes])
                : 0U;
            const auto upper_left = row != 0U && column >= bytes_per_pixel
                ? std::to_integer<std::uint8_t>(
                    pixels[source - row_bytes - bytes_per_pixel])
                : 0U;
            std::uint8_t predictor = 0U;
            if (filter == 1U) {
                predictor = static_cast<std::uint8_t>(left);
            } else if (filter == 2U) {
                predictor = static_cast<std::uint8_t>(above);
            } else if (filter == 3U) {
                predictor = static_cast<std::uint8_t>((left + above) / 2U);
            } else if (filter == 4U) {
                const auto prediction = static_cast<int>(left + above) -
                    static_cast<int>(upper_left);
                const auto da = std::abs(prediction - static_cast<int>(left));
                const auto db = std::abs(prediction - static_cast<int>(above));
                const auto dc =
                    std::abs(prediction - static_cast<int>(upper_left));
                predictor = static_cast<std::uint8_t>(
                    da <= db && da <= dc ? left : (db <= dc ? above : upper_left));
            }
            filtered[row * (row_bytes + 1U) + 1U + column] =
                static_cast<std::byte>(
                    static_cast<std::uint8_t>(value - predictor));
        }
    }
    return filtered;
}

std::vector<std::byte> pack_samples(
    std::span<const std::uint16_t> samples,
    std::uint8_t bit_depth) {
    const auto byte_count =
        (samples.size() * static_cast<std::size_t>(bit_depth) + 7U) / 8U;
    std::vector<std::byte> result(byte_count);
    if (bit_depth == 16U) {
        for (std::size_t index = 0U; index < samples.size(); ++index) {
            result[index * 2U] =
                static_cast<std::byte>(samples[index] >> 8U);
            result[index * 2U + 1U] =
                static_cast<std::byte>(samples[index]);
        }
        return result;
    }
    if (bit_depth == 8U) {
        for (std::size_t index = 0U; index < samples.size(); ++index) {
            result[index] = static_cast<std::byte>(samples[index]);
        }
        return result;
    }
    const auto mask = static_cast<std::uint16_t>((1U << bit_depth) - 1U);
    for (std::size_t index = 0U; index < samples.size(); ++index) {
        const auto bit_offset = index * bit_depth;
        const auto shift = 8U - bit_depth - (bit_offset & 7U);
        result[bit_offset / 8U] |= static_cast<std::byte>(
            (samples[index] & mask) << shift);
    }
    return result;
}

std::vector<std::byte> make_adam7_rgba_filtered(
    std::span<const std::byte> rgba,
    std::uint32_t width,
    std::uint32_t height) {
    struct pass final {
        std::uint32_t start_x;
        std::uint32_t start_y;
        std::uint32_t step_x;
        std::uint32_t step_y;
    };
    constexpr std::array<pass, 7U> passes{{
        {0U, 0U, 8U, 8U}, {4U, 0U, 8U, 8U},
        {0U, 4U, 4U, 8U}, {2U, 0U, 4U, 4U},
        {0U, 2U, 2U, 4U}, {1U, 0U, 2U, 2U},
        {0U, 1U, 1U, 2U}}};
    std::vector<std::byte> filtered;
    for (const auto& descriptor : passes) {
        std::vector<std::byte> pass_pixels;
        std::size_t pass_width = 0U;
        std::size_t pass_height = 0U;
        for (auto y = descriptor.start_y; y < height; y += descriptor.step_y) {
            if (descriptor.start_x >= width) {
                break;
            }
            std::size_t row_width = 0U;
            for (auto x = descriptor.start_x;
                 x < width;
                 x += descriptor.step_x) {
                const auto source =
                    (static_cast<std::size_t>(y) * width + x) * 4U;
                pass_pixels.insert(pass_pixels.end(),
                    rgba.begin() + static_cast<std::ptrdiff_t>(source),
                    rgba.begin() + static_cast<std::ptrdiff_t>(source + 4U));
                ++row_width;
            }
            pass_width = row_width;
            ++pass_height;
        }
        if (pass_width != 0U) {
            const auto pass_filtered =
                encode_filters(pass_pixels, pass_width, pass_height, 4U);
            filtered.insert(
                filtered.end(), pass_filtered.begin(), pass_filtered.end());
        }
    }
    return filtered;
}

std::vector<std::byte> decode(std::span<const std::byte> png) {
    png_decode_requirements requirements{};
    image_error error = image_error::invalid_argument;
    require(try_get_png_decode_requirements(png, requirements, &error));
    require(error == image_error::none);
    std::vector<std::byte> compressed(requirements.compressed_bytes);
    std::vector<std::byte> filtered(requirements.filtered_bytes);
    std::vector<std::byte> rgba(requirements.rgba_bytes);
    png_decode_requirements decoded{};
    require(try_decode_png_rgba(
        png, compressed, filtered, rgba, decoded, &error));
    require(error == image_error::none);
    require(decoded.width == requirements.width &&
        decoded.height == requirements.height &&
        decoded.rgba_bytes == requirements.rgba_bytes);
    return rgba;
}

void all_filters_decode_exact_rgba() {
    constexpr std::size_t width = 3U;
    constexpr std::size_t height = 5U;
    std::vector<std::byte> rgba(width * height * 4U);
    for (std::size_t row = 0U; row < height; ++row) {
        for (std::size_t column = 0U; column < width; ++column) {
            const auto pixel = (row * width + column) * 4U;
            rgba[pixel] = static_cast<std::byte>(row * 31U + column * 7U);
            rgba[pixel + 1U] =
                static_cast<std::byte>(row * 17U + column * 13U);
            rgba[pixel + 2U] =
                static_cast<std::byte>(row * 11U + column * 23U);
            rgba[pixel + 3U] = static_cast<std::byte>(255U - row * 19U);
        }
    }
    const auto filtered = encode_filters(rgba, width, height, 4U);
    const auto png = make_png(width, height, 6U, filtered);
    require(decode(png) == rgba);
}

void palette_and_other_color_types_decode() {
    const std::array<std::byte, 2U> indexes{
        std::byte{0U}, std::byte{1U}};
    const auto filtered_indexes = encode_filters(indexes, 2U, 1U, 1U);
    const std::array<std::byte, 6U> palette{
        std::byte{10U}, std::byte{20U}, std::byte{30U},
        std::byte{200U}, std::byte{150U}, std::byte{100U}};
    const std::array<std::byte, 2U> alpha{
        std::byte{255U}, std::byte{40U}};
    const auto indexed_png =
        make_png(2U, 1U, 3U, filtered_indexes, palette, alpha);
    const std::vector<std::byte> indexed_expected{
        std::byte{10U}, std::byte{20U}, std::byte{30U}, std::byte{255U},
        std::byte{200U}, std::byte{150U}, std::byte{100U}, std::byte{40U}};
    require(decode(indexed_png) == indexed_expected);

    const std::array<std::byte, 1U> gray{std::byte{81U}};
    const auto gray_png = make_png(1U, 1U, 0U,
        encode_filters(gray, 1U, 1U, 1U));
    require(decode(gray_png) == std::vector<std::byte>{
        std::byte{81U}, std::byte{81U}, std::byte{81U}, std::byte{255U}});
    const std::array<std::byte, 2U> transparent_gray{
        std::byte{0U}, std::byte{81U}};
    const auto transparent_gray_png = make_png(1U, 1U, 0U,
        encode_filters(gray, 1U, 1U, 1U), {}, transparent_gray);
    require(decode(transparent_gray_png) == std::vector<std::byte>{
        std::byte{81U}, std::byte{81U}, std::byte{81U}, std::byte{0U}});

    const std::array<std::byte, 3U> rgb{
        std::byte{9U}, std::byte{8U}, std::byte{7U}};
    const auto rgb_png = make_png(1U, 1U, 2U,
        encode_filters(rgb, 1U, 1U, 3U));
    require(decode(rgb_png) == std::vector<std::byte>{
        std::byte{9U}, std::byte{8U}, std::byte{7U}, std::byte{255U}});
    const std::array<std::byte, 6U> transparent_rgb{
        std::byte{0U}, std::byte{9U}, std::byte{0U},
        std::byte{8U}, std::byte{0U}, std::byte{7U}};
    const auto transparent_rgb_png = make_png(1U, 1U, 2U,
        encode_filters(rgb, 1U, 1U, 3U), {}, transparent_rgb);
    require(decode(transparent_rgb_png) == std::vector<std::byte>{
        std::byte{9U}, std::byte{8U}, std::byte{7U}, std::byte{0U}});

    const std::array<std::byte, 2U> gray_alpha{
        std::byte{61U}, std::byte{31U}};
    const auto gray_alpha_png = make_png(1U, 1U, 4U,
        encode_filters(gray_alpha, 1U, 1U, 2U));
    require(decode(gray_alpha_png) == std::vector<std::byte>{
        std::byte{61U}, std::byte{61U}, std::byte{61U}, std::byte{31U}});
}

void packed_and_sixteen_bit_samples_decode() {
    for (const auto bit_depth : {1U, 2U, 4U}) {
        const auto maximum = (1U << bit_depth) - 1U;
        const std::array<std::uint16_t, 5U> samples{
            0U,
            static_cast<std::uint16_t>(maximum),
            static_cast<std::uint16_t>(maximum / 2U),
            1U,
            static_cast<std::uint16_t>(maximum)};
        auto packed = pack_samples(samples, static_cast<std::uint8_t>(bit_depth));
        packed.insert(packed.begin(), std::byte{0U});
        const auto png = make_png(
            5U,
            1U,
            0U,
            packed,
            {},
            {},
            0U,
            static_cast<std::uint8_t>(bit_depth));
        const auto decoded = decode(png);
        for (std::size_t index = 0U; index < samples.size(); ++index) {
            const auto expected = static_cast<std::uint8_t>(
                (samples[index] * 255U + maximum / 2U) / maximum);
            require(decoded[index * 4U] == static_cast<std::byte>(expected));
            require(decoded[index * 4U + 1U] ==
                static_cast<std::byte>(expected));
            require(decoded[index * 4U + 2U] ==
                static_cast<std::byte>(expected));
            require(decoded[index * 4U + 3U] == std::byte{255U});
        }
    }

    const std::array<std::uint16_t, 8U> rgba16{
        0x0000U, 0x8000U, 0xFFFFU, 0x4000U,
        0x1234U, 0xABCDU, 0xFEDCU, 0xFFFFU};
    auto rgba16_filtered = pack_samples(rgba16, 16U);
    rgba16_filtered.insert(rgba16_filtered.begin(), std::byte{0U});
    const auto rgba16_png = make_png(
        2U, 1U, 6U, rgba16_filtered, {}, {}, 0U, 16U);
    const auto rgba16_decoded = decode(rgba16_png);
    for (std::size_t index = 0U; index < rgba16.size(); ++index) {
        const auto expected = static_cast<std::uint8_t>(
            (static_cast<std::uint32_t>(rgba16[index]) * 255U + 32767U) /
            65535U);
        require(rgba16_decoded[index] == static_cast<std::byte>(expected));
    }

    const std::array<std::uint16_t, 3U> indexed_samples{0U, 3U, 1U};
    auto packed_indexes = pack_samples(indexed_samples, 2U);
    packed_indexes.insert(packed_indexes.begin(), std::byte{0U});
    const std::array<std::byte, 12U> palette{
        std::byte{1U}, std::byte{2U}, std::byte{3U},
        std::byte{10U}, std::byte{20U}, std::byte{30U},
        std::byte{40U}, std::byte{50U}, std::byte{60U},
        std::byte{70U}, std::byte{80U}, std::byte{90U}};
    const auto indexed_png = make_png(
        3U, 1U, 3U, packed_indexes, palette, {}, 0U, 2U);
    require(decode(indexed_png) == std::vector<std::byte>{
        std::byte{1U}, std::byte{2U}, std::byte{3U}, std::byte{255U},
        std::byte{70U}, std::byte{80U}, std::byte{90U}, std::byte{255U},
        std::byte{10U}, std::byte{20U}, std::byte{30U}, std::byte{255U}});
}

void adam7_scatter_decodes_exact_rgba() {
    constexpr std::uint32_t width = 9U;
    constexpr std::uint32_t height = 7U;
    std::vector<std::byte> rgba(
        static_cast<std::size_t>(width) * height * 4U);
    for (std::uint32_t y = 0U; y < height; ++y) {
        for (std::uint32_t x = 0U; x < width; ++x) {
            const auto offset =
                (static_cast<std::size_t>(y) * width + x) * 4U;
            rgba[offset] = static_cast<std::byte>(x * 19U);
            rgba[offset + 1U] = static_cast<std::byte>(y * 31U);
            rgba[offset + 2U] = static_cast<std::byte>((x + y) * 13U);
            rgba[offset + 3U] = static_cast<std::byte>(255U - x * 7U);
        }
    }
    const auto filtered = make_adam7_rgba_filtered(rgba, width, height);
    const auto png = make_png(width, height, 6U, filtered, {}, {}, 1U);
    png_decode_requirements requirements{};
    require(try_get_png_decode_requirements(png, requirements));
    require(requirements.interlace_method == 1U);
    require(decode(png) == rgba);
}

void failures_are_transactional_and_bounded() {
    const std::array<std::byte, 1U> invalid_index{std::byte{2U}};
    const auto filtered = encode_filters(invalid_index, 1U, 1U, 1U);
    const std::array<std::byte, 6U> palette{
        std::byte{1U}, std::byte{2U}, std::byte{3U},
        std::byte{4U}, std::byte{5U}, std::byte{6U}};
    auto png = make_png(1U, 1U, 3U, filtered, palette);
    png_decode_requirements requirements{};
    image_error error = image_error::none;
    require(try_get_png_decode_requirements(png, requirements, &error));
    std::vector<std::byte> compressed(requirements.compressed_bytes);
    std::vector<std::byte> decoded(requirements.filtered_bytes);
    std::vector<std::byte> output(requirements.rgba_bytes, std::byte{0xA5U});
    png_decode_requirements result{};
    require(!try_decode_png_rgba(
        png, compressed, decoded, output, result, &error));
    require(error == image_error::invalid_compressed_data);
    require(std::all_of(output.begin(), output.end(), [](std::byte value) {
        return value == std::byte{0xA5U};
    }));

    require(!try_decode_png_rgba(
        png,
        std::span<std::byte>(compressed).first(compressed.size() - 1U),
        decoded,
        output,
        result,
        &error));
    require(error == image_error::insufficient_buffer);

    png[29U] ^= std::byte{1U};
    require(!try_get_png_decode_requirements(png, result, &error));
    require(error == image_error::checksum_mismatch);

    const auto invalid_depth = make_png(
        1U, 1U, 2U, filtered, palette, {}, 0U, 4U);
    require(!try_get_png_decode_requirements(invalid_depth, result, &error));
    require(error == image_error::unsupported_format);
}

} // namespace

int main() {
    all_filters_decode_exact_rgba();
    palette_and_other_color_types_decode();
    packed_and_sixteen_bit_samples_decode();
    adam7_scatter_decodes_exact_rgba();
    failures_are_transactional_and_bounded();
    return 0;
}
