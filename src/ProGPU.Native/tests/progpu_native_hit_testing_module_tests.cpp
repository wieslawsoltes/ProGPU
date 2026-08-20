import progpu.native.hit_testing;

int main() {
    progpu::native::hit_testing::hit_test_index index;
    return index.nodes().empty() ? 0 : 1;
}
