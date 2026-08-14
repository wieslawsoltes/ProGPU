#ifndef PROGPU_NATIVE_TEXT_HPP
#define PROGPU_NATIVE_TEXT_HPP

#include <cstddef>
#include <cstdint>
#include <span>

namespace progpu::native::text {

enum class font_error : std::uint32_t {
    none = 0U,
    invalid_argument,
    unsupported_container,
    invalid_collection,
    invalid_face,
    truncated_directory
};

struct open_type_tag final {
    std::uint32_t value = 0U;

    static constexpr open_type_tag from_chars(
        char a,
        char b,
        char c,
        char d) noexcept {
        return open_type_tag{
            (static_cast<std::uint32_t>(
                static_cast<unsigned char>(a)) << 24U) |
            (static_cast<std::uint32_t>(
                static_cast<unsigned char>(b)) << 16U) |
            (static_cast<std::uint32_t>(
                static_cast<unsigned char>(c)) << 8U) |
            static_cast<std::uint32_t>(
                static_cast<unsigned char>(d))};
    }

    friend constexpr bool operator==(
        open_type_tag,
        open_type_tag) noexcept = default;
};

struct sfnt_table_view final {
    open_type_tag tag{};
    std::uint32_t checksum = 0U;
    std::span<const std::byte> bytes{};
};

struct sfnt_header_metrics final {
    std::uint16_t units_per_em = 0U;
    std::int16_t x_min = 0;
    std::int16_t y_min = 0;
    std::int16_t x_max = 0;
    std::int16_t y_max = 0;
    std::int16_t index_to_loc_format = 0;
};

struct sfnt_horizontal_header_metrics final {
    std::int16_t ascender = 0;
    std::int16_t descender = 0;
    std::int16_t line_gap = 0;
    std::uint16_t advance_width_max = 0U;
    std::uint16_t number_of_horizontal_metrics = 0U;
};

struct sfnt_horizontal_glyph_metrics final {
    std::uint16_t advance_width = 0U;
    std::int16_t left_side_bearing = 0;
};

/*
 * Allocation-free borrowed view over one SFNT or TrueType Collection face.
 * The caller owns the byte span and must keep it alive for the view lifetime.
 * Construction and table lookup are O(T) for T directory records with O(1)
 * storage. Character lookup is O(log G) for format 12/13 groups and O(S) for
 * format 4 segments, with no heap allocation or WebGPU initialization.
 */
class sfnt_font_view final {
public:
    static bool try_create(
        std::span<const std::byte> data,
        std::uint32_t face_index,
        sfnt_font_view& result,
        font_error* error = nullptr) noexcept;

    static bool try_get_face_count(
        std::span<const std::byte> data,
        std::uint32_t& face_count,
        font_error* error = nullptr) noexcept;

    bool try_get_table(
        open_type_tag tag,
        sfnt_table_view& result) const noexcept;
    bool try_get_header_metrics(
        sfnt_header_metrics& result) const noexcept;
    bool try_get_horizontal_header_metrics(
        sfnt_horizontal_header_metrics& result) const noexcept;
    bool try_get_horizontal_glyph_metrics(
        std::uint16_t glyph_index,
        sfnt_horizontal_glyph_metrics& result) const noexcept;
    bool try_get_glyph_count(std::uint16_t& result) const noexcept;
    bool try_get_glyph_index(
        std::uint32_t code_point,
        std::uint16_t& result) const noexcept;

    std::span<const std::byte> data() const noexcept;
    std::uint32_t face_index() const noexcept;
    std::uint32_t face_offset() const noexcept;
    std::uint16_t table_count() const noexcept;
    bool uses_symbol_character_map() const noexcept;

private:
    std::span<const std::byte> data_{};
    std::span<const std::byte> cmap_format4_{};
    std::span<const std::byte> cmap_format12_{};
    std::span<const std::byte> cmap_format13_{};
    std::uint32_t face_index_ = 0U;
    std::uint32_t face_offset_ = 0U;
    std::size_t directory_offset_ = 0U;
    std::uint16_t table_count_ = 0U;
    bool uses_symbol_character_map_ = false;
};

} // namespace progpu::native::text

#endif
