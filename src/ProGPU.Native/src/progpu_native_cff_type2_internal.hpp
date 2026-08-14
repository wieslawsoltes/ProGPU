#ifndef PROGPU_NATIVE_CFF_TYPE2_INTERNAL_HPP
#define PROGPU_NATIVE_CFF_TYPE2_INTERNAL_HPP

#include "progpu_native_text.hpp"

#include <cstddef>
#include <cstdint>
#include <span>

namespace progpu::native::text::detail {

enum class cff_execution_result : std::uint8_t {
    failed = 0U,
    returned,
    end_glyph
};

enum class cff_flex_kind : std::uint8_t {
    horizontal = 0U,
    full,
    horizontal_one,
    one
};

enum class cff_binary_operation : std::uint8_t {
    logical_and = 0U,
    logical_or,
    add,
    subtract,
    divide,
    equal,
    multiply
};

class cff_path_writer final {
public:
    cff_path_writer(
        std::span<progpu_native_path_segment> segments,
        bool count_only) noexcept;

    bool move_to(double x, double y) noexcept;
    bool line_to(double x0, double y0, double x1, double y1) noexcept;
    bool curve_to(
        double x0,
        double y0,
        double x1,
        double y1,
        double x2,
        double y2,
        double x3,
        double y3) noexcept;
    bool end_glyph() noexcept;
    std::uint32_t count() const noexcept;
    bool valid() const noexcept;

private:
    bool close_figure() noexcept;
    bool emit(progpu_native_path_segment segment) noexcept;
    bool begin_if_needed(double x, double y) noexcept;

    std::span<progpu_native_path_segment> segments_{};
    std::uint32_t count_ = 0U;
    double start_x_ = 0.0;
    double start_y_ = 0.0;
    double current_x_ = 0.0;
    double current_y_ = 0.0;
    bool count_only_ = false;
    bool figure_active_ = false;
    bool valid_ = true;
};

class cff_type2_evaluator final {
public:
    cff_type2_evaluator(
        cff_path_writer& writer,
        sfnt_cff_index_view global_subroutines,
        sfnt_cff_index_view local_subroutines,
        std::span<double> operands,
        std::span<double> transient,
        std::uint32_t random_seed) noexcept;

    bool try_evaluate(std::span<const std::byte> char_string) noexcept;

private:
    static constexpr std::uint32_t maximum_subroutine_depth = 10U;

    cff_execution_result execute(
        std::span<const std::byte> program,
        std::uint32_t depth,
        bool is_subroutine) noexcept;
    bool execute_escaped(std::uint8_t operation) noexcept;
    bool consume_stems() noexcept;
    bool consume_width(std::size_t expected_operands) noexcept;
    void remove_first_operand() noexcept;
    bool execute_vv_or_hh_curve(bool horizontal) noexcept;
    bool execute_alternating_curve(bool horizontal) noexcept;
    bool flex(cff_flex_kind kind) noexcept;
    bool curve(
        double dx1,
        double dy1,
        double dx2,
        double dy2,
        double dx3,
        double dy3) noexcept;
    bool roll() noexcept;
    void reverse(std::size_t start, std::size_t end) noexcept;
    bool binary(cff_binary_operation operation) noexcept;
    bool push(double value) noexcept;
    bool pop(double& value) noexcept;
    void clear() noexcept;
    static std::int32_t get_subroutine_bias(std::uint32_t count) noexcept;
    static bool try_read_operand(
        std::span<const std::byte> bytes,
        std::size_t& cursor,
        std::uint8_t first,
        double& result) noexcept;
    static bool try_to_i32(double value, std::int32_t& result) noexcept;

    cff_path_writer& writer_;
    sfnt_cff_index_view global_subroutines_{};
    sfnt_cff_index_view local_subroutines_{};
    std::span<double> operands_{};
    std::span<double> transient_{};
    std::size_t operand_count_ = 0U;
    std::uint32_t stem_count_ = 0U;
    bool width_seen_ = false;
    double x_ = 0.0;
    double y_ = 0.0;
    std::uint32_t random_state_ = 0U;
};

bool try_evaluate_cff1_outline(
    sfnt_cff1_font_view font,
    std::uint32_t glyph_index,
    std::span<progpu_native_path_segment> segments,
    bool count_only,
    std::uint32_t& written,
    font_error* error) noexcept;

} // namespace progpu::native::text::detail

#endif
