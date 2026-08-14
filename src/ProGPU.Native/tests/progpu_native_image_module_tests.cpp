import progpu.native.image;

int main() {
    const auto error = progpu::native::image::image_error::none;
    progpu::native::image::png_decode_requirements requirements{};
    return error == progpu::native::image::image_error::none &&
        requirements.rgba_bytes == 0U ? 0 : 1;
}
