import progpu.native.text;

int main() {
    constexpr auto tag =
        progpu::native::text::open_type_tag::from_chars('c', 'm', 'a', 'p');
    return tag.value == 0x636D6170U ? 0 : 1;
}
