import progpu.native.compression;

int main() {
    const auto error = progpu::native::compression::compression_error::none;
    return error == progpu::native::compression::compression_error::none
        ? 0
        : 1;
}
