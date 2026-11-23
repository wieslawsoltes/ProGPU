#ifndef PROGPU_NATIVE_IMAGE_HPP
#define PROGPU_NATIVE_IMAGE_HPP

#include <cstddef>
#include <cstdint>
#include <span>
#include <vector>

namespace progpu::native::image {

enum class image_error : std::uint32_t {
    none = 0U,
    invalid_argument,
    invalid_signature,
    invalid_chunk,
    checksum_mismatch,
    unsupported_format,
    insufficient_buffer,
    invalid_compressed_data,
    out_of_memory
};

// The file formats of the image module, told from the first bytes of a file.
enum class image_format : std::uint32_t {
    unknown = 0U,
    png,
    jpeg,
    gif,
    bmp
};

// The size of a decoded raster image: eight bits a channel in the order red,
// green, blue, alpha, alpha not premultiplied, rows from the top.
struct raster_image_size final {
    std::uint32_t width = 0U;
    std::uint32_t height = 0U;
    std::size_t rgba_bytes = 0U;
    image_format format = image_format::unknown;
};

struct png_decode_requirements final {
    std::uint32_t width = 0U;
    std::uint32_t height = 0U;
    std::size_t compressed_bytes = 0U;
    std::size_t filtered_bytes = 0U;
    std::size_t rgba_bytes = 0U;
    std::uint8_t bit_depth = 0U;
    std::uint8_t color_type = 0U;
    std::uint8_t channel_count = 0U;
    std::uint8_t interlace_method = 0U;
};

bool try_get_png_decode_requirements(
    std::span<const std::byte> input,
    png_decode_requirements& result,
    image_error* error = nullptr) noexcept;

bool try_decode_png_rgba(
    std::span<const std::byte> input,
    std::span<std::byte> compressed_scratch,
    std::span<std::byte> filtered_scratch,
    std::span<std::byte> rgba_output,
    png_decode_requirements& result,
    image_error* error = nullptr) noexcept;

// How hard the PNG writer looks for matches.
enum class png_compression : std::uint32_t {
    none = 0U,
    fastest,
    optimal,
    smallest
};

// Writes `rgba` (width x height pixels, eight bits a channel, alpha not
// premultiplied, rows from the top) as a PNG into `output`.
bool try_encode_png_rgba(
    std::span<const std::byte> rgba,
    std::uint32_t width,
    std::uint32_t height,
    png_compression level,
    std::vector<std::byte>& output,
    image_error* error = nullptr) noexcept;

// Writes the colours of `rgba` as a baseline JPEG of `quality` (1 to 100, on the
// scale of the IJG tables) into `output`. Alpha is ignored. Chroma is subsampled
// 4:2:0 below a quality of 90.
bool try_encode_jpeg_rgba(
    std::span<const std::byte> rgba,
    std::uint32_t width,
    std::uint32_t height,
    std::uint32_t quality,
    std::vector<std::byte>& output,
    image_error* error = nullptr) noexcept;

// The format of a file by its signature; unknown for anything else.
image_format detect_image_format(std::span<const std::byte> input) noexcept;

// The size of a JPEG, GIF or BMP file. Reads the headers and decodes nothing.
bool try_get_raster_image_size(
    std::span<const std::byte> input,
    raster_image_size& result,
    image_error* error = nullptr) noexcept;

// Decodes a JPEG, GIF or BMP file into rgba_output, which holds at least the
// rgba_bytes of try_get_raster_image_size.
//
// JPEG: baseline, extended sequential and progressive Huffman at eight bits, in
// grayscale, YCbCr, RGB, CMYK and YCCK. The Exif orientation is not applied.
// GIF: the first frame on a transparent canvas of the logical screen.
// BMP: 1, 4, 8, 16, 24 and 32 bits, with bit fields and RLE.
bool try_decode_raster_image_rgba(
    std::span<const std::byte> input,
    std::span<std::byte> rgba_output,
    image_error* error = nullptr) noexcept;

} // namespace progpu::native::image

#endif
