/*
 * A C ABI over the engine's own image module (progpu_native_image.hpp), for hosts that cannot
 * call C++: reading the size of a PNG and decoding it to RGBA. No platform image API is involved:
 * the PNG reader and its inflate are those of the engine.
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

#ifdef __cplusplus
}
#endif

#endif
