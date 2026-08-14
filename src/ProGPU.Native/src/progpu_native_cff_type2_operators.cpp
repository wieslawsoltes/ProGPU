#include "progpu_native_cff_type2_internal.hpp"

#include <algorithm>
#include <cmath>
#include <cstddef>
#include <cstdint>
#include <limits>

// Direct native port provenance: ProGPU-owned Type 2 escaped, flex, and
// alternating-curve operators at checkpoint 281a9078. All operators mutate
// fixed evaluator storage; none allocate or dispatch through std::function.
namespace progpu::native::text::detail {

bool cff_type2_evaluator::execute_escaped(
    std::uint8_t operation) noexcept {
    switch (operation) {
    case 0U:
        clear();
        return true;
    case 3U:
        return binary(cff_binary_operation::logical_and);
    case 4U:
        return binary(cff_binary_operation::logical_or);
    case 5U: {
        double value = 0.0;
        return pop(value) && push(value == 0.0 ? 1.0 : 0.0);
    }
    case 9U: {
        double value = 0.0;
        return pop(value) && push(std::abs(value));
    }
    case 10U:
        return binary(cff_binary_operation::add);
    case 11U:
        return binary(cff_binary_operation::subtract);
    case 12U:
        return binary(cff_binary_operation::divide);
    case 14U: {
        double value = 0.0;
        return pop(value) && push(-value);
    }
    case 15U:
        return binary(cff_binary_operation::equal);
    case 18U: {
        double discarded = 0.0;
        return pop(discarded);
    }
    case 20U: {
        double encoded_index = 0.0;
        double value = 0.0;
        std::int32_t index = 0;
        if (!pop(encoded_index) || !pop(value) ||
            !try_to_i32(encoded_index, index) || index < 0 ||
            static_cast<std::size_t>(index) >= transient_.size()) {
            return false;
        }
        transient_[static_cast<std::size_t>(index)] = value;
        return true;
    }
    case 21U: {
        double encoded_index = 0.0;
        std::int32_t index = 0;
        return pop(encoded_index) && try_to_i32(encoded_index, index) &&
            index >= 0 &&
            static_cast<std::size_t>(index) < transient_.size() &&
            push(transient_[static_cast<std::size_t>(index)]);
    }
    case 22U: {
        if (operand_count_ < 4U) {
            return false;
        }
        const auto second_limit = operands_[--operand_count_];
        const auto first_limit = operands_[--operand_count_];
        const auto second_value = operands_[--operand_count_];
        const auto first_value = operands_[--operand_count_];
        return push(first_limit <= second_limit ? first_value : second_value);
    }
    case 23U:
        random_state_ ^= random_state_ << 13U;
        random_state_ ^= random_state_ >> 17U;
        random_state_ ^= random_state_ << 5U;
        return push((static_cast<double>(random_state_) + 1.0) /
            (static_cast<double>(
                std::numeric_limits<std::uint32_t>::max()) + 2.0));
    case 24U:
        return binary(cff_binary_operation::multiply);
    case 26U: {
        double value = 0.0;
        return pop(value) && push(std::sqrt(std::max(0.0, value)));
    }
    case 27U:
        return operand_count_ != 0U && push(operands_[operand_count_ - 1U]);
    case 28U:
        if (operand_count_ < 2U) {
            return false;
        }
        std::swap(
            operands_[operand_count_ - 1U],
            operands_[operand_count_ - 2U]);
        return true;
    case 29U: {
        double encoded_index = 0.0;
        std::int32_t index = 0;
        if (!pop(encoded_index) || operand_count_ == 0U ||
            !try_to_i32(encoded_index, index)) {
            return false;
        }
        index = std::clamp(
            index,
            0,
            static_cast<std::int32_t>(operand_count_ - 1U));
        return push(operands_[operand_count_ - 1U -
            static_cast<std::size_t>(index)]);
    }
    case 30U:
        return roll();
    case 34U:
        return flex(cff_flex_kind::horizontal);
    case 35U:
        return flex(cff_flex_kind::full);
    case 36U:
        return flex(cff_flex_kind::horizontal_one);
    case 37U:
        return flex(cff_flex_kind::one);
    default:
        return false;
    }
}

bool cff_type2_evaluator::execute_vv_or_hh_curve(
    bool horizontal) noexcept {
    if (operand_count_ < 4U) {
        return false;
    }
    std::size_t cursor = 0U;
    double first_cross_delta = 0.0;
    if ((operand_count_ & 1U) != 0U) {
        first_cross_delta = operands_[cursor++];
    }
    while (cursor <= operand_count_ - 4U) {
        const auto success = horizontal
            ? curve(
                operands_[cursor], first_cross_delta,
                operands_[cursor + 1U], operands_[cursor + 2U],
                operands_[cursor + 3U], 0.0)
            : curve(
                first_cross_delta, operands_[cursor],
                operands_[cursor + 1U], operands_[cursor + 2U],
                0.0, operands_[cursor + 3U]);
        if (!success) {
            return false;
        }
        first_cross_delta = 0.0;
        cursor += 4U;
    }
    const auto valid = cursor == operand_count_;
    clear();
    return valid;
}

bool cff_type2_evaluator::execute_alternating_curve(
    bool horizontal) noexcept {
    if (operand_count_ < 4U) {
        return false;
    }
    std::size_t cursor = 0U;
    while (cursor <= operand_count_ - 4U) {
        const auto remaining = operand_count_ - cursor;
        const auto final_with_extra = remaining == 5U;
        const auto success = horizontal
            ? curve(
                operands_[cursor], 0.0,
                operands_[cursor + 1U], operands_[cursor + 2U],
                final_with_extra ? operands_[cursor + 4U] : 0.0,
                operands_[cursor + 3U])
            : curve(
                0.0, operands_[cursor],
                operands_[cursor + 1U], operands_[cursor + 2U],
                operands_[cursor + 3U],
                final_with_extra ? operands_[cursor + 4U] : 0.0);
        if (!success) {
            return false;
        }
        cursor += final_with_extra ? 5U : 4U;
        horizontal = !horizontal;
    }
    const auto valid = cursor == operand_count_;
    clear();
    return valid;
}

bool cff_type2_evaluator::flex(cff_flex_kind kind) noexcept {
    switch (kind) {
    case cff_flex_kind::horizontal:
        if (operand_count_ == 7U) {
            const auto dy = operands_[2U];
            const auto success =
                curve(
                    operands_[0U], 0.0, operands_[1U], dy,
                    operands_[3U], 0.0) &&
                curve(
                    operands_[4U], 0.0, operands_[5U], -dy,
                    operands_[6U], 0.0);
            clear();
            return success;
        }
        return false;
    case cff_flex_kind::full:
        if (operand_count_ == 13U) {
            const auto success =
                curve(
                    operands_[0U], operands_[1U],
                    operands_[2U], operands_[3U],
                    operands_[4U], operands_[5U]) &&
                curve(
                    operands_[6U], operands_[7U],
                    operands_[8U], operands_[9U],
                    operands_[10U], operands_[11U]);
            clear();
            return success;
        }
        return false;
    case cff_flex_kind::horizontal_one:
        if (operand_count_ == 9U) {
            const auto final_y =
                -(operands_[1U] + operands_[3U] + operands_[7U]);
            const auto success =
                curve(
                    operands_[0U], operands_[1U],
                    operands_[2U], operands_[3U],
                    operands_[4U], 0.0) &&
                curve(
                    operands_[5U], 0.0,
                    operands_[6U], operands_[7U],
                    operands_[8U], final_y);
            clear();
            return success;
        }
        return false;
    case cff_flex_kind::one:
        if (operand_count_ == 11U) {
            const auto sum_x = operands_[0U] + operands_[2U] +
                operands_[4U] + operands_[6U] + operands_[8U];
            const auto sum_y = operands_[1U] + operands_[3U] +
                operands_[5U] + operands_[7U] + operands_[9U];
            const auto dx6 = std::abs(sum_x) > std::abs(sum_y)
                ? operands_[10U]
                : -sum_x;
            const auto dy6 = std::abs(sum_x) > std::abs(sum_y)
                ? -sum_y
                : operands_[10U];
            const auto success =
                curve(
                    operands_[0U], operands_[1U],
                    operands_[2U], operands_[3U],
                    operands_[4U], operands_[5U]) &&
                curve(
                    operands_[6U], operands_[7U],
                    operands_[8U], operands_[9U], dx6, dy6);
            clear();
            return success;
        }
        return false;
    }
    return false;
}

bool cff_type2_evaluator::curve(
    double dx1,
    double dy1,
    double dx2,
    double dy2,
    double dx3,
    double dy3) noexcept {
    const auto start_x = x_;
    const auto start_y = y_;
    const auto x1 = x_ + dx1;
    const auto y1 = y_ + dy1;
    const auto x2 = x1 + dx2;
    const auto y2 = y1 + dy2;
    x_ = x2 + dx3;
    y_ = y2 + dy3;
    return writer_.curve_to(
        start_x, start_y, x1, y1, x2, y2, x_, y_);
}

bool cff_type2_evaluator::roll() noexcept {
    double shift_value = 0.0;
    double count_value = 0.0;
    std::int32_t shift = 0;
    std::int32_t count = 0;
    if (!pop(shift_value) || !pop(count_value) ||
        !try_to_i32(shift_value, shift) ||
        !try_to_i32(count_value, count) || count < 0 ||
        static_cast<std::size_t>(count) > operand_count_) {
        return false;
    }
    if (count < 2) {
        return true;
    }
    shift %= count;
    if (shift < 0) {
        shift += count;
    }
    if (shift == 0) {
        return true;
    }
    const auto count_size = static_cast<std::size_t>(count);
    const auto shift_size = static_cast<std::size_t>(shift);
    const auto start = operand_count_ - count_size;
    reverse(start, operand_count_ - 1U);
    reverse(start, start + shift_size - 1U);
    reverse(start + shift_size, operand_count_ - 1U);
    return true;
}

void cff_type2_evaluator::reverse(
    std::size_t start,
    std::size_t end) noexcept {
    while (start < end) {
        std::swap(operands_[start++], operands_[end--]);
    }
}

bool cff_type2_evaluator::binary(
    cff_binary_operation operation) noexcept {
    double right = 0.0;
    double left = 0.0;
    if (!pop(right) || !pop(left)) {
        return false;
    }
    switch (operation) {
    case cff_binary_operation::logical_and:
        return push(left != 0.0 && right != 0.0 ? 1.0 : 0.0);
    case cff_binary_operation::logical_or:
        return push(left != 0.0 || right != 0.0 ? 1.0 : 0.0);
    case cff_binary_operation::add:
        return push(left + right);
    case cff_binary_operation::subtract:
        return push(left - right);
    case cff_binary_operation::divide:
        return push(right == 0.0 ? 0.0 : left / right);
    case cff_binary_operation::equal:
        return push(left == right ? 1.0 : 0.0);
    case cff_binary_operation::multiply:
        return push(left * right);
    }
    return false;
}

} // namespace progpu::native::text::detail
