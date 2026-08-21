#include "progpu_native_text_shaping_showcase.hpp"

#include <cstddef>
#include <cstdint>
#include <fstream>
#include <iterator>
#include <stdexcept>
#include <string>
#include <vector>

namespace {

using progpu::native::samples::text_shaping_showcase_metrics;
using progpu::native::samples::text_shaping_showcase_scene;

void require_impl(bool condition, int line) {
    if (!condition) {
        throw std::runtime_error(
            "native text shaping showcase assertion failed at line " +
            std::to_string(line));
    }
}

#define require(condition) require_impl((condition), __LINE__)

std::vector<std::byte> read_font() {
    std::ifstream input(PROGPU_NATIVE_TEST_INTER_FONT, std::ios::binary);
    require(input.good());
    const std::vector<char> chars{
        std::istreambuf_iterator<char>(input),
        std::istreambuf_iterator<char>()};
    std::vector<std::byte> bytes(chars.size());
    for (std::size_t index = 0U; index < chars.size(); ++index) {
        bytes[index] = static_cast<std::byte>(
            static_cast<unsigned char>(chars[index]));
    }
    return bytes;
}

void managed_feature_wall_port_is_retained_and_dpi_sensitive() {
    const auto font = read_font();
    text_shaping_showcase_scene scene;
    require(scene.load_font(font));
    require(scene.ready());
    require(scene.resize(960.0F, 640.0F, 1.0F));

    std::vector<std::byte> stream;
    text_shaping_showcase_metrics metrics{};
    require(scene.compile(stream, metrics));
    require(metrics.preset_index == 0U);
    require(metrics.shaped_glyph_count > metrics.visible_glyph_count);
    require(metrics.visible_glyph_count > 0U);
    require(metrics.unique_outline_count > 16U);
    require(metrics.feature_off_glyph_count > 0U);
    require(metrics.feature_on_glyph_count > 0U);
    require(metrics.command_count == 11U);
    require(metrics.resource_count >= 2U);
    require(metrics.stream_bytes == stream.size());
    const auto first_stream = stream;
    const auto first_generation = metrics.generation;

    require(!scene.compile(stream, metrics));
    require(scene.generation() == first_generation);

    require(scene.set_preset(1U));
    require(scene.compile(stream, metrics));
    require(metrics.preset_index == 1U);
    require(metrics.feature_on_glyph_count == metrics.feature_off_glyph_count);
    require(metrics.feature_on_advance != metrics.feature_off_advance);
    require(stream != first_stream);

    for (std::uint32_t preset = 2U;
         preset < text_shaping_showcase_scene::preset_count();
         ++preset) {
        require(scene.set_preset(preset));
        require(scene.compile(stream, metrics));
        require(metrics.preset_index == preset);
        require(metrics.visible_glyph_count > 0U);
        require(metrics.unique_outline_count > 0U);
    }

    const std::uint32_t glyph_count = metrics.shaped_glyph_count;
    const std::uint32_t outline_count = metrics.unique_outline_count;
    require(scene.resize(960.0F, 640.0F, 2.0F));
    require(scene.compile(stream, metrics));
    require(metrics.shaped_glyph_count == glyph_count);
    require(metrics.unique_outline_count == outline_count);
}

} // namespace

int main() {
    managed_feature_wall_port_is_retained_and_dpi_sensitive();
    return 0;
}
