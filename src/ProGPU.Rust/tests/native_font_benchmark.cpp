// Matched benchmark of the original native API; not a Rust runtime dependency.
#include "progpu_native_text.hpp"
#include <algorithm>
#include <chrono>
#include <cstdio>
#include <cstdlib>
#include <fstream>
#include <vector>
using namespace progpu::native::text;
using clock_type = std::chrono::steady_clock;
std::uint64_t query(const sfnt_font_view& face) {
    std::uint64_t checksum{};
    for (std::uint32_t index = 0; index < 66560U; ++index) {
        const auto code = index < 65536U ? index : 0x1f600U + index - 65536U;
        std::uint16_t glyph{};
        if (face.try_get_glyph_index(code, glyph)) {
            checksum += static_cast<std::uint64_t>(glyph) + (1ULL << 48U);
            sfnt_horizontal_glyph_metrics metrics{};
            if (face.try_get_horizontal_glyph_metrics(glyph, metrics)) {
                checksum += (static_cast<std::uint64_t>(metrics.advance_width) << 16U) +
                    (static_cast<std::uint64_t>(static_cast<std::uint16_t>(metrics.left_side_bearing)) << 32U);
            }
        }
    }
    return checksum;
}
double elapsed(clock_type::time_point start) {
    return std::chrono::duration<double, std::micro>(clock_type::now() - start).count();
}
int main(int argc, char** argv) {
    if (argc < 2 || argc > 3) return 1;
    const auto index = argc == 3 ? static_cast<std::uint32_t>(std::stoul(argv[2])) : 0U;
    std::ifstream input(argv[1], std::ios::binary | std::ios::ate);
    if (!input) return 2;
    std::vector<std::byte> bytes(static_cast<std::size_t>(input.tellg()));
    input.seekg(0);
    if (!input.read(reinterpret_cast<char*>(bytes.data()), static_cast<std::streamsize>(bytes.size()))) return 3;
    sfnt_font_view face{};
    const auto start = clock_type::now();
    if (!sfnt_font_view::try_create(bytes, index, face)) return 4;
    const auto first = elapsed(start);
    const auto expected = query(face);
    for (unsigned i = 0; i < 5; ++i) if (query(face) != expected) return 5;
    std::vector<double> samples;
    for (unsigned i = 0; i < 40; ++i) {
        const auto begin = clock_type::now();
        if (query(face) != expected) return 6;
        samples.push_back(elapsed(begin));
    }
    std::sort(samples.begin(), samples.end());
    std::printf("bytes=%zu face=%u tables=%u first_parse_us=%.3f checksum=%llu\n", bytes.size(), index,
        face.table_count(), first, static_cast<unsigned long long>(expected));
    std::printf("queries_per_round=66560 warmup=5 rounds=40 round_us p50=%.3f p95=%.3f p99=%.3f\n", samples[20], samples[38], samples[39]);
}
