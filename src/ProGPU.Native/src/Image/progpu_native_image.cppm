module;
#include "progpu_native_image.hpp"

export module progpu.native.image;

export namespace progpu::native::image {
using ::progpu::native::image::image_error;
using ::progpu::native::image::png_decode_requirements;
using ::progpu::native::image::try_decode_png_rgba;
using ::progpu::native::image::try_get_png_decode_requirements;
}
