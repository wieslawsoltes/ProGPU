#include "progpu_native_cff_type2_internal.hpp"

#include "progpu_native_font_bytes.hpp"

#include <algorithm>
#include <bit>
#include <cmath>
#include <cstddef>
#include <cstdint>
#include <limits>

// Direct native port provenance: ProGPU-owned Cff1OutlineSource.Type2Evaluator
// at checkpoint 281a9078. Evaluation is O(B + S) for executed bytes B and
// emitted segments S, with fixed operand/transient stacks and depth <= 10.
namespace progpu::native::text::detail {

cff_type2_evaluator::cff_type2_evaluator(
    cff_path_writer& writer,
    sfnt_cff_index_view global_subroutines,
    sfnt_cff_index_view local_subroutines,
    std::span<double> operands,
    std::span<double> transient,
    std::uint32_t random_seed) noexcept
    : writer_(writer),
      global_subroutines_(global_subroutines),
      local_subroutines_(local_subroutines),
      operands_(operands),
      transient_(transient),
      random_state_(random_seed) {
    std::fill(transient_.begin(), transient_.end(), 0.0);
}

bool cff_type2_evaluator::try_evaluate(
    std::span<const std::byte> char_string) noexcept {
    return execute(char_string, 0U, false) ==
            cff_execution_result::end_glyph &&
        writer_.end_glyph() && writer_.valid();
}

cff_execution_result cff_type2_evaluator::execute(
    std::span<const std::byte> program,
    std::uint32_t depth,
    bool is_subroutine) noexcept {
    if (depth > maximum_subroutine_depth) {
        return cff_execution_result::failed;
    }
    std::size_t cursor = 0U;
    while (cursor < program.size()) {
        const auto operation =
            std::to_integer<std::uint8_t>(program[cursor++]);
        double value = 0.0;
        if (try_read_operand(program, cursor, operation, value)) {
            if (!push(value)) {
                return cff_execution_result::failed;
            }
            continue;
        }

        switch (operation) {
        case 1U:
        case 3U:
        case 18U:
        case 23U:
            if (!consume_stems()) {
                return cff_execution_result::failed;
            }
            break;
        case 19U:
        case 20U: {
            if (!consume_stems()) {
                return cff_execution_result::failed;
            }
            const auto mask_bytes = (stem_count_ + 7U) >> 3U;
            if (cursor > program.size() ||
                mask_bytes > program.size() - cursor) {
                return cff_execution_result::failed;
            }
            cursor += mask_bytes;
            break;
        }
        case 4U:
            if (!consume_width(1U) || operand_count_ != 1U) {
                return cff_execution_result::failed;
            }
            y_ += operands_[0];
            if (!writer_.move_to(x_, y_)) {
                return cff_execution_result::failed;
            }
            clear();
            break;
        case 5U:
            if (operand_count_ < 2U || (operand_count_ & 1U) != 0U) {
                return cff_execution_result::failed;
            }
            for (std::size_t index = 0U;
                index < operand_count_;
                index += 2U) {
                const auto start_x = x_;
                const auto start_y = y_;
                x_ += operands_[index];
                y_ += operands_[index + 1U];
                if (!writer_.line_to(start_x, start_y, x_, y_)) {
                    return cff_execution_result::failed;
                }
            }
            clear();
            break;
        case 6U:
        case 7U: {
            if (operand_count_ == 0U) {
                return cff_execution_result::failed;
            }
            auto horizontal = operation == 6U;
            for (std::size_t index = 0U;
                index < operand_count_;
                ++index) {
                const auto start_x = x_;
                const auto start_y = y_;
                if (horizontal) {
                    x_ += operands_[index];
                } else {
                    y_ += operands_[index];
                }
                if (!writer_.line_to(start_x, start_y, x_, y_)) {
                    return cff_execution_result::failed;
                }
                horizontal = !horizontal;
            }
            clear();
            break;
        }
        case 8U:
            if (operand_count_ == 0U || operand_count_ % 6U != 0U) {
                return cff_execution_result::failed;
            }
            for (std::size_t index = 0U;
                index < operand_count_;
                index += 6U) {
                if (!curve(
                        operands_[index], operands_[index + 1U],
                        operands_[index + 2U], operands_[index + 3U],
                        operands_[index + 4U], operands_[index + 5U])) {
                    return cff_execution_result::failed;
                }
            }
            clear();
            break;
        case 10U:
        case 29U: {
            double encoded_index = 0.0;
            std::int32_t requested = 0;
            if (!pop(encoded_index) || !try_to_i32(encoded_index, requested)) {
                return cff_execution_result::failed;
            }
            const auto subroutines = operation == 10U
                ? local_subroutines_
                : global_subroutines_;
            const auto biased = static_cast<std::int64_t>(requested) +
                get_subroutine_bias(subroutines.count);
            if (biased < 0 ||
                biased >= static_cast<std::int64_t>(subroutines.count)) {
                return cff_execution_result::failed;
            }
            std::span<const std::byte> subroutine{};
            if (!sfnt_cff_data::try_get_index_item(
                    subroutines,
                    static_cast<std::uint32_t>(biased),
                    subroutine)) {
                return cff_execution_result::failed;
            }
            const auto nested = execute(
                subroutine, depth + 1U, true);
            if (nested == cff_execution_result::failed ||
                nested == cff_execution_result::end_glyph) {
                return nested;
            }
            break;
        }
        case 11U:
            return is_subroutine
                ? cff_execution_result::returned
                : cff_execution_result::failed;
        case 12U:
            if (cursor >= program.size() ||
                !execute_escaped(std::to_integer<std::uint8_t>(
                    program[cursor++]))) {
                return cff_execution_result::failed;
            }
            break;
        case 14U: {
            const auto expected = operand_count_ == 1U || operand_count_ == 5U
                ? operand_count_ - 1U
                : operand_count_;
            if (!consume_width(expected) || operand_count_ != 0U) {
                return cff_execution_result::failed;
            }
            return cff_execution_result::end_glyph;
        }
        case 21U:
            if (!consume_width(2U) || operand_count_ != 2U) {
                return cff_execution_result::failed;
            }
            x_ += operands_[0];
            y_ += operands_[1];
            if (!writer_.move_to(x_, y_)) {
                return cff_execution_result::failed;
            }
            clear();
            break;
        case 22U:
            if (!consume_width(1U) || operand_count_ != 1U) {
                return cff_execution_result::failed;
            }
            x_ += operands_[0];
            if (!writer_.move_to(x_, y_)) {
                return cff_execution_result::failed;
            }
            clear();
            break;
        case 24U: {
            if (operand_count_ < 8U ||
                (operand_count_ - 2U) % 6U != 0U) {
                return cff_execution_result::failed;
            }
            const auto curve_limit = operand_count_ - 2U;
            for (std::size_t index = 0U; index < curve_limit; index += 6U) {
                if (!curve(
                        operands_[index], operands_[index + 1U],
                        operands_[index + 2U], operands_[index + 3U],
                        operands_[index + 4U], operands_[index + 5U])) {
                    return cff_execution_result::failed;
                }
            }
            const auto start_x = x_;
            const auto start_y = y_;
            x_ += operands_[curve_limit];
            y_ += operands_[curve_limit + 1U];
            if (!writer_.line_to(start_x, start_y, x_, y_)) {
                return cff_execution_result::failed;
            }
            clear();
            break;
        }
        case 25U: {
            if (operand_count_ < 8U ||
                (operand_count_ - 6U) % 2U != 0U) {
                return cff_execution_result::failed;
            }
            const auto line_limit = operand_count_ - 6U;
            for (std::size_t index = 0U; index < line_limit; index += 2U) {
                const auto start_x = x_;
                const auto start_y = y_;
                x_ += operands_[index];
                y_ += operands_[index + 1U];
                if (!writer_.line_to(start_x, start_y, x_, y_)) {
                    return cff_execution_result::failed;
                }
            }
            if (!curve(
                    operands_[line_limit], operands_[line_limit + 1U],
                    operands_[line_limit + 2U], operands_[line_limit + 3U],
                    operands_[line_limit + 4U], operands_[line_limit + 5U])) {
                return cff_execution_result::failed;
            }
            clear();
            break;
        }
        case 26U:
        case 27U:
            if (!execute_vv_or_hh_curve(operation == 27U)) {
                return cff_execution_result::failed;
            }
            break;
        case 30U:
        case 31U:
            if (!execute_alternating_curve(operation == 31U)) {
                return cff_execution_result::failed;
            }
            break;
        default:
            return cff_execution_result::failed;
        }
    }
    return is_subroutine
        ? cff_execution_result::returned
        : cff_execution_result::failed;
}

bool cff_type2_evaluator::consume_stems() noexcept {
    if (!width_seen_ && (operand_count_ & 1U) != 0U) {
        remove_first_operand();
        width_seen_ = true;
    }
    if ((operand_count_ & 1U) != 0U) {
        return false;
    }
    stem_count_ += static_cast<std::uint32_t>(operand_count_ >> 1U);
    clear();
    return stem_count_ <= 96U;
}

bool cff_type2_evaluator::consume_width(
    std::size_t expected_operands) noexcept {
    if (!width_seen_ && operand_count_ == expected_operands + 1U) {
        remove_first_operand();
        width_seen_ = true;
    } else if (!width_seen_) {
        width_seen_ = true;
    }
    return operand_count_ == expected_operands;
}

void cff_type2_evaluator::remove_first_operand() noexcept {
    std::move(
        operands_.begin() + 1,
        operands_.begin() + static_cast<std::ptrdiff_t>(operand_count_),
        operands_.begin());
    --operand_count_;
}

bool cff_type2_evaluator::push(double value) noexcept {
    if (operand_count_ >= operands_.size() || !std::isfinite(value)) {
        return false;
    }
    operands_[operand_count_++] = value;
    return true;
}

bool cff_type2_evaluator::pop(double& value) noexcept {
    if (operand_count_ == 0U) {
        value = 0.0;
        return false;
    }
    value = operands_[--operand_count_];
    return true;
}

void cff_type2_evaluator::clear() noexcept {
    operand_count_ = 0U;
}

std::int32_t cff_type2_evaluator::get_subroutine_bias(
    std::uint32_t count) noexcept {
    return count < 1240U ? 107 : count < 33900U ? 1131 : 32768;
}

bool cff_type2_evaluator::try_read_operand(
    std::span<const std::byte> bytes,
    std::size_t& cursor,
    std::uint8_t first,
    double& result) noexcept {
    result = 0.0;
    if (first >= 32U && first <= 246U) {
        result = static_cast<double>(first) - 139.0;
        return true;
    }
    if (first >= 247U && first <= 250U) {
        if (cursor >= bytes.size()) {
            return false;
        }
        result = static_cast<double>((first - 247U) * 256U +
            std::to_integer<std::uint8_t>(bytes[cursor++]) + 108U);
        return true;
    }
    if (first >= 251U && first <= 254U) {
        if (cursor >= bytes.size()) {
            return false;
        }
        result = -static_cast<double>((first - 251U) * 256U +
            std::to_integer<std::uint8_t>(bytes[cursor++]) + 108U);
        return true;
    }
    if (first == 28U) {
        if (cursor > bytes.size() || bytes.size() - cursor < 2U) {
            return false;
        }
        result = detail::read_i16(bytes, cursor);
        cursor += 2U;
        return true;
    }
    if (first != 255U || cursor > bytes.size() ||
        bytes.size() - cursor < 4U) {
        return false;
    }
    result = static_cast<double>(
        std::bit_cast<std::int32_t>(detail::read_u32(bytes, cursor))) /
        65536.0;
    cursor += 4U;
    return true;
}

bool cff_type2_evaluator::try_to_i32(
    double value,
    std::int32_t& result) noexcept {
    if (!std::isfinite(value) ||
        value < static_cast<double>(std::numeric_limits<std::int32_t>::min()) ||
        value > static_cast<double>(std::numeric_limits<std::int32_t>::max())) {
        result = 0;
        return false;
    }
    result = static_cast<std::int32_t>(value);
    return true;
}

} // namespace progpu::native::text::detail
