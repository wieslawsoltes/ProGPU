/*
 * A C ABI over the engine's own image module (progpu_native_image.hpp), for hosts that cannot
 * call C++: reading the size of a PNG, JPEG, GIF or BMP file and decoding it to RGBA. No platform
 * image API is involved: the readers and the inflate are those of the engine.
 *
 * A function returns nonzero on success. On failure the error, when a place for it is given, is
 * one of PROGPU_NATIVE_IMAGE_ERROR_*. The functions keep no state and may be called from any
 * thread.
 */
#ifndef PROGPU_NATIVE_IMAGE_C_H
#define PROGPU_NATIVE_IMAGE_C_H

#include <stddef.h>
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

/* The errors of the image module, and the one of this layer (its scratch memory). */
#define PROGPU_NATIVE_IMAGE_ERROR_NONE 0u
#define PROGPU_NATIVE_IMAGE_ERROR_INVALID_ARGUMENT 1u
#define PROGPU_NATIVE_IMAGE_ERROR_INVALID_SIGNATURE 2u
#define PROGPU_NATIVE_IMAGE_ERROR_INVALID_CHUNK 3u
#define PROGPU_NATIVE_IMAGE_ERROR_CHECKSUM_MISMATCH 4u
#define PROGPU_NATIVE_IMAGE_ERROR_UNSUPPORTED_FORMAT 5u
#define PROGPU_NATIVE_IMAGE_ERROR_INSUFFICIENT_BUFFER 6u
#define PROGPU_NATIVE_IMAGE_ERROR_INVALID_COMPRESSED_DATA 7u
#define PROGPU_NATIVE_IMAGE_ERROR_OUT_OF_MEMORY 8u

/* The file formats, told from the first bytes of a file. */
#define PROGPU_NATIVE_IMAGE_FORMAT_UNKNOWN 0u
#define PROGPU_NATIVE_IMAGE_FORMAT_PNG 1u
#define PROGPU_NATIVE_IMAGE_FORMAT_JPEG 2u
#define PROGPU_NATIVE_IMAGE_FORMAT_GIF 3u
#define PROGPU_NATIVE_IMAGE_FORMAT_BMP 4u

/* The size of a PNG: its width and height in pixels and the bytes its RGBA pixels take (four a
 * pixel, rows without padding). Reads the header and the layout of the chunks; decodes nothing. */
int progpu_native_image_png_read_size(
    const uint8_t* data,
    size_t size,
    uint32_t* width,
    uint32_t* height,
    size_t* rgba_size,
    uint32_t* error);

/* Decodes a PNG into `rgba`: eight bits a channel in the order red, green, blue, alpha, alpha not
 * premultiplied, rows from the top without padding. `rgba_size` is at least the size
 * progpu_native_image_png_read_size answered. Every colour type, bit depth and interlace method of
 * PNG is read; samples of other depths are scaled to eight bits with rounding. Gamma and colour
 * profile chunks are not applied: the samples are handed out as the file has them. */
int progpu_native_image_png_decode_rgba(
    const uint8_t* data,
    size_t size,
    uint8_t* rgba,
    size_t rgba_size,
    uint32_t* error);

/* The format and size of a PNG, JPEG, GIF or BMP file, as progpu_native_image_png_read_size for a
 * PNG. Reads the headers; decodes nothing. */
int progpu_native_image_read_size(
    const uint8_t* data,
    size_t size,
    uint32_t* format,
    uint32_t* width,
    uint32_t* height,
    size_t* rgba_size,
    uint32_t* error);

/* Decodes a PNG, JPEG, GIF or BMP file into `rgba`, as progpu_native_image_png_decode_rgba for a
 * PNG. JPEG: baseline, extended sequential and progressive Huffman at eight bits, in grayscale,
 * YCbCr, RGB, CMYK and YCCK; the Exif orientation is not applied. GIF: the first frame on a
 * transparent canvas of the logical screen. BMP: 1 to 32 bits, bit fields and RLE. */
int progpu_native_image_decode_rgba(
    const uint8_t* data,
    size_t size,
    uint8_t* rgba,
    size_t rgba_size,
    uint32_t* error);

/* The compression levels of progpu_native_image_encode_png. */
#define PROGPU_NATIVE_IMAGE_PNG_NO_COMPRESSION 0u
#define PROGPU_NATIVE_IMAGE_PNG_FASTEST 1u
#define PROGPU_NATIVE_IMAGE_PNG_OPTIMAL 2u
#define PROGPU_NATIVE_IMAGE_PNG_SMALLEST 3u

/* Writes `rgba` (`width` x `height` pixels, eight bits a channel, alpha not premultiplied, rows
 * from the top without padding) as a PNG. On success `*output` is a buffer of `*output_size`
 * bytes, released with progpu_native_image_free. */
int progpu_native_image_encode_png(
    const uint8_t* rgba,
    size_t rgba_size,
    uint32_t width,
    uint32_t height,
    uint32_t level,
    uint8_t** output,
    size_t* output_size,
    uint32_t* error);

/* Writes the colours of `rgba` as a baseline JPEG of `quality` (1 to 100; alpha is ignored; chroma
 * is subsampled 4:2:0 below 90). The output is as for progpu_native_image_encode_png. */
int progpu_native_image_encode_jpeg(
    const uint8_t* rgba,
    size_t rgba_size,
    uint32_t width,
    uint32_t height,
    uint32_t quality,
    uint8_t** output,
    size_t* output_size,
    uint32_t* error);

/* Releases a buffer of an encoder. A null pointer is ignored. */
void progpu_native_image_free(uint8_t* output);

#ifdef __cplusplus
}
#endif

#endif
