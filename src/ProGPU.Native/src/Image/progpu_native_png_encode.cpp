// The PNG writer: eight-bit RGBA, each row with the filter of the smallest sum of
// absolute differences, and a zlib stream of the engine's own deflate (hash chains
// with lazy matching, and for each block the cheapest of stored, fixed and dynamic
// Huffman codes).

#include "progpu_native_png_internal.hpp"
#include "progpu_native_raster_internal.hpp"

#include <algorithm>
#include <array>
#include <cstdlib>
#include <limits>
#include <new>
#include <utility>
#include <vector>

namespace progpu::native::image {

namespace {

constexpr std::size_t window_size = 32768U;
constexpr std::size_t hash_size = 1U << 15U;
constexpr std::size_t min_match = 3U;
constexpr std::size_t max_match = 258U;
constexpr std::size_t block_symbols = 1U << 16U;
constexpr std::uint32_t literal_codes = 286U;
constexpr std::uint32_t distance_codes = 30U;
constexpr std::uint32_t length_code_codes = 19U;
constexpr std::uint32_t end_of_block = 256U;

constexpr std::array<std::uint16_t, 29U> length_base{
    3U, 4U, 5U, 6U, 7U, 8U, 9U, 10U, 11U, 13U, 15U, 17U, 19U, 23U, 27U, 31U,
    35U, 43U, 51U, 59U, 67U, 83U, 99U, 115U, 131U, 163U, 195U, 227U, 258U};
constexpr std::array<std::uint8_t, 29U> length_extra{
    0U, 0U, 0U, 0U, 0U, 0U, 0U, 0U, 1U, 1U, 1U, 1U, 2U, 2U, 2U, 2U,
    3U, 3U, 3U, 3U, 4U, 4U, 4U, 4U, 5U, 5U, 5U, 5U, 0U};
constexpr std::array<std::uint16_t, 30U> distance_base{
    1U, 2U, 3U, 4U, 5U, 7U, 9U, 13U, 17U, 25U, 33U, 49U, 65U, 97U, 129U, 193U,
    257U, 385U, 513U, 769U, 1025U, 1537U, 2049U, 3073U, 4097U, 6145U, 8193U,
    12289U, 16385U, 24577U};
constexpr std::array<std::uint8_t, 30U> distance_extra{
    0U, 0U, 0U, 0U, 1U, 1U, 2U, 2U, 3U, 3U, 4U, 4U, 5U, 5U, 6U, 6U,
    7U, 7U, 8U, 8U, 9U, 9U, 10U, 10U, 11U, 11U, 12U, 12U, 13U, 13U};
constexpr std::array<std::uint8_t, 19U> length_code_order{
    16U, 17U, 18U, 0U, 8U, 7U, 9U, 6U, 10U, 5U, 11U, 4U, 12U, 3U, 13U, 2U, 14U, 1U, 15U};

struct search_effort final {
    std::uint32_t chain = 0U;
    std::uint32_t good_length = 0U;
    bool lazy = false;
};

search_effort effort_of(png_compression level) noexcept {
    switch (level) {
    case png_compression::fastest:
        return {8U, 16U, false};
    case png_compression::smallest:
        return {1024U, 258U, true};
    case png_compression::optimal:
    case png_compression::none:
        break;
    }
    return {128U, 64U, true};
}

// Bits least significant first, as deflate writes them.
class bit_writer final {
public:
    explicit bit_writer(std::vector<std::byte>& output) noexcept : output_(output) {}

    void put(std::uint32_t value, std::uint32_t count) {
        bits_ |= static_cast<std::uint64_t>(value) << count_;
        count_ += count;
        while (count_ >= 8U) {
            output_.push_back(static_cast<std::byte>(bits_ & 0xFFU));
            bits_ >>= 8U;
            count_ -= 8U;
        }
    }

    void align() {
        if (count_ > 0U) {
            output_.push_back(static_cast<std::byte>(bits_ & 0xFFU));
            bits_ = 0U;
            count_ = 0U;
        }
    }

    std::vector<std::byte>& output() noexcept {
        return output_;
    }

private:
    std::vector<std::byte>& output_;
    std::uint64_t bits_ = 0U;
    std::uint32_t count_ = 0U;
};

std::uint32_t reverse_bits(std::uint32_t code, std::uint32_t length) noexcept {
    std::uint32_t reversed = 0U;
    for (std::uint32_t bit = 0U; bit < length; ++bit) {
        reversed = (reversed << 1U) | ((code >> bit) & 1U);
    }
    return reversed;
}

// Canonical codes for code lengths, bit-reversed for writing.
void assign_codes(
    std::span<const std::uint8_t> lengths,
    std::span<std::uint16_t> codes) noexcept {
    std::array<std::uint32_t, 16U> count{};
    for (const std::uint8_t length : lengths) {
        ++count[length];
    }
    count[0] = 0U;
    std::array<std::uint32_t, 16U> next{};
    std::uint32_t code = 0U;
    for (std::size_t bits = 1U; bits < next.size(); ++bits) {
        code = (code + count[bits - 1U]) << 1U;
        next[bits] = code;
    }
    for (std::size_t symbol = 0U; symbol < lengths.size(); ++symbol) {
        const std::uint32_t length = lengths[symbol];
        codes[symbol] = length == 0U
            ? std::uint16_t{0U}
            : static_cast<std::uint16_t>(reverse_bits(next[length]++, length));
    }
}

// Huffman code lengths of `frequencies`, none longer than `limit`.
void build_lengths(
    std::span<const std::uint32_t> frequencies,
    std::span<std::uint8_t> lengths,
    std::uint32_t limit) {
    struct node final {
        std::uint64_t weight = 0U;
        std::int32_t left = -1;
        std::int32_t right = -1;
    };
    std::fill(lengths.begin(), lengths.end(), std::uint8_t{0U});
    std::vector<node> nodes;
    nodes.reserve(frequencies.size() * 2U);
    std::vector<std::int32_t> heap;
    std::vector<std::uint32_t> symbols;
    for (std::size_t symbol = 0U; symbol < frequencies.size(); ++symbol) {
        if (frequencies[symbol] != 0U) {
            heap.push_back(static_cast<std::int32_t>(nodes.size()));
            symbols.push_back(static_cast<std::uint32_t>(symbol));
            nodes.push_back({frequencies[symbol], -1, -1});
        }
    }
    if (symbols.empty()) {
        return;
    }
    if (symbols.size() == 1U) {
        lengths[symbols[0]] = 1U;
        return;
    }
    const auto heavier = [&nodes](std::int32_t a, std::int32_t b) noexcept {
        const auto& first = nodes[static_cast<std::size_t>(a)];
        const auto& second = nodes[static_cast<std::size_t>(b)];
        return first.weight != second.weight ? first.weight > second.weight : a > b;
    };
    std::make_heap(heap.begin(), heap.end(), heavier);
    while (heap.size() > 1U) {
        std::pop_heap(heap.begin(), heap.end(), heavier);
        const std::int32_t a = heap.back();
        heap.pop_back();
        std::pop_heap(heap.begin(), heap.end(), heavier);
        const std::int32_t b = heap.back();
        heap.pop_back();
        const std::uint64_t weight =
            nodes[static_cast<std::size_t>(a)].weight + nodes[static_cast<std::size_t>(b)].weight;
        nodes.push_back({weight, a, b});
        heap.push_back(static_cast<std::int32_t>(nodes.size() - 1U));
        std::push_heap(heap.begin(), heap.end(), heavier);
    }
    std::vector<std::pair<std::int32_t, std::uint32_t>> stack;
    stack.emplace_back(heap.front(), 0U);
    std::vector<std::uint32_t> depths(symbols.size(), 0U);
    while (!stack.empty()) {
        const auto [index, depth] = stack.back();
        stack.pop_back();
        const node& current = nodes[static_cast<std::size_t>(index)];
        if (current.left < 0) {
            depths[static_cast<std::size_t>(index)] = depth;
        } else {
            stack.emplace_back(current.left, depth + 1U);
            stack.emplace_back(current.right, depth + 1U);
        }
    }
    // Lengths over the limit are cut, and the Kraft sum is repaired by lengthening the
    // cheapest codes that still have room.
    std::vector<std::size_t> order(symbols.size());
    for (std::size_t index = 0U; index < order.size(); ++index) {
        order[index] = index;
    }
    std::sort(order.begin(), order.end(), [&](std::size_t a, std::size_t b) noexcept {
        return frequencies[symbols[a]] != frequencies[symbols[b]]
            ? frequencies[symbols[a]] < frequencies[symbols[b]]
            : symbols[a] < symbols[b];
    });
    const std::uint64_t full = std::uint64_t{1U} << limit;
    std::uint64_t kraft = 0U;
    for (auto& depth : depths) {
        depth = std::min(depth, limit);
        kraft += full >> depth;
    }
    for (std::size_t at = 0U; kraft > full; at = (at + 1U) % order.size()) {
        auto& depth = depths[order[at]];
        if (depth < limit) {
            kraft -= full >> (depth + 1U);
            ++depth;
        }
    }
    for (auto at = order.rbegin(); at != order.rend(); ++at) {
        auto& depth = depths[*at];
        while (depth > 1U && kraft + (full >> depth) <= full) {
            kraft += full >> depth;
            --depth;
        }
    }
    for (std::size_t index = 0U; index < symbols.size(); ++index) {
        lengths[symbols[index]] = static_cast<std::uint8_t>(depths[index]);
    }
}

struct token final {
    std::uint16_t length = 0U; // 0: a literal in `distance`.
    std::uint16_t distance = 0U;
};

std::uint32_t length_symbol(std::uint32_t length) noexcept {
    const auto at = std::upper_bound(length_base.begin(), length_base.end(), length);
    return static_cast<std::uint32_t>(at - length_base.begin()) - 1U;
}

std::uint32_t distance_symbol(std::uint32_t distance) noexcept {
    const auto at = std::upper_bound(distance_base.begin(), distance_base.end(), distance);
    return static_cast<std::uint32_t>(at - distance_base.begin()) - 1U;
}

struct run_code final {
    std::uint8_t symbol = 0U;
    std::uint8_t extra = 0U;
};

// The code lengths in the run-length alphabet of deflate.
std::vector<run_code> encode_lengths(std::span<const std::uint8_t> lengths) {
    std::vector<run_code> runs;
    for (std::size_t at = 0U; at < lengths.size();) {
        const std::uint8_t value = lengths[at];
        std::size_t run = 1U;
        while (at + run < lengths.size() && lengths[at + run] == value) {
            ++run;
        }
        at += run;
        if (value == 0U) {
            while (run >= 11U) {
                const std::size_t take = std::min<std::size_t>(run, 138U);
                runs.push_back({18U, static_cast<std::uint8_t>(take - 11U)});
                run -= take;
            }
            if (run >= 3U) {
                runs.push_back({17U, static_cast<std::uint8_t>(run - 3U)});
                run = 0U;
            }
        } else {
            runs.push_back({value, 0U});
            --run;
            while (run >= 3U) {
                const std::size_t take = std::min<std::size_t>(run, 6U);
                runs.push_back({16U, static_cast<std::uint8_t>(take - 3U)});
                run -= take;
            }
        }
        for (; run > 0U; --run) {
            runs.push_back({value, 0U});
        }
    }
    return runs;
}

void write_tokens(
    bit_writer& bits,
    std::span<const token> tokens,
    std::span<const std::uint16_t> literal_bits,
    std::span<const std::uint8_t> literal_lengths,
    std::span<const std::uint16_t> distance_bits,
    std::span<const std::uint8_t> distance_lengths) {
    for (const token& item : tokens) {
        if (item.length == 0U) {
            bits.put(literal_bits[item.distance], literal_lengths[item.distance]);
            continue;
        }
        const std::uint32_t symbol = length_symbol(item.length);
        bits.put(literal_bits[257U + symbol], literal_lengths[257U + symbol]);
        bits.put(item.length - length_base[symbol], length_extra[symbol]);
        const std::uint32_t distance = distance_symbol(item.distance);
        bits.put(distance_bits[distance], distance_lengths[distance]);
        bits.put(item.distance - distance_base[distance], distance_extra[distance]);
    }
    bits.put(literal_bits[end_of_block], literal_lengths[end_of_block]);
}

void write_stored(bit_writer& bits, std::span<const std::byte> data, bool last) {
    std::size_t at = 0U;
    do {
        const std::size_t size = std::min<std::size_t>(data.size() - at, 65535U);
        const bool final_block = last && at + size == data.size();
        bits.put(final_block ? 1U : 0U, 3U);
        bits.align();
        const auto length = static_cast<std::uint32_t>(size);
        bits.put(length & 0xFFFFU, 16U);
        bits.put(~length & 0xFFFFU, 16U);
        auto& output = bits.output();
        output.insert(output.end(), data.begin() + static_cast<std::ptrdiff_t>(at),
            data.begin() + static_cast<std::ptrdiff_t>(at + size));
        at += size;
    } while (at < data.size());
}

// One block: the cheapest of stored, fixed and dynamic codes.
void write_block(
    bit_writer& bits,
    std::span<const token> tokens,
    std::span<const std::byte> raw,
    bool last) {
    std::array<std::uint32_t, literal_codes> literal_count{};
    std::array<std::uint32_t, distance_codes> distance_count{};
    std::uint64_t extra_bits = 0U;
    for (const token& item : tokens) {
        if (item.length == 0U) {
            ++literal_count[item.distance];
        } else {
            const std::uint32_t symbol = length_symbol(item.length);
            ++literal_count[257U + symbol];
            const std::uint32_t distance = distance_symbol(item.distance);
            ++distance_count[distance];
            extra_bits += length_extra[symbol];
            extra_bits += distance_extra[distance];
        }
    }
    literal_count[end_of_block] = 1U;

    std::array<std::uint8_t, literal_codes> literal_lengths{};
    std::array<std::uint8_t, distance_codes> distance_lengths{};
    build_lengths(literal_count, literal_lengths, 15U);
    build_lengths(distance_count, distance_lengths, 15U);
    std::uint32_t literal_used = literal_codes;
    while (literal_used > 257U && literal_lengths[literal_used - 1U] == 0U) {
        --literal_used;
    }
    std::uint32_t distance_used = distance_codes;
    while (distance_used > 1U && distance_lengths[distance_used - 1U] == 0U) {
        --distance_used;
    }
    std::vector<std::uint8_t> joined(literal_lengths.begin(), literal_lengths.begin() + literal_used);
    joined.insert(joined.end(), distance_lengths.begin(), distance_lengths.begin() + distance_used);
    const auto runs = encode_lengths(joined);
    std::array<std::uint32_t, length_code_codes> code_count{};
    for (const run_code& run : runs) {
        ++code_count[run.symbol];
    }
    std::array<std::uint8_t, length_code_codes> code_lengths{};
    build_lengths(code_count, code_lengths, 7U);
    std::uint32_t code_used = length_code_codes;
    while (code_used > 4U && code_lengths[length_code_order[code_used - 1U]] == 0U) {
        --code_used;
    }

    std::uint64_t dynamic_bits = 14U + std::uint64_t{code_used} * 3U + extra_bits;
    for (const run_code& run : runs) {
        dynamic_bits += code_lengths[run.symbol];
        dynamic_bits += run.symbol == 16U ? 2U : run.symbol == 17U ? 3U : run.symbol == 18U ? 7U : 0U;
    }
    std::array<std::uint8_t, 288U> fixed_literal{};
    for (std::size_t symbol = 0U; symbol < fixed_literal.size(); ++symbol) {
        fixed_literal[symbol] = symbol < 144U ? 8U : symbol < 256U ? 9U : symbol < 280U ? 7U : 8U;
    }
    std::uint64_t fixed_bits = extra_bits;
    for (std::size_t symbol = 0U; symbol < literal_codes; ++symbol) {
        dynamic_bits += std::uint64_t{literal_count[symbol]} * literal_lengths[symbol];
        fixed_bits += std::uint64_t{literal_count[symbol]} * fixed_literal[symbol];
    }
    for (std::size_t symbol = 0U; symbol < distance_codes; ++symbol) {
        dynamic_bits += std::uint64_t{distance_count[symbol]} * distance_lengths[symbol];
        fixed_bits += std::uint64_t{distance_count[symbol]} * 5U;
    }
    const std::uint64_t stored_bits = raw.size() * 8U + (raw.size() / 65535U + 1U) * 40U;
    if (stored_bits <= dynamic_bits && stored_bits <= fixed_bits) {
        write_stored(bits, raw, last);
        return;
    }
    if (fixed_bits <= dynamic_bits) {
        bits.put((last ? 1U : 0U) | (1U << 1U), 3U);
        std::array<std::uint16_t, 288U> literal_codes_fixed{};
        assign_codes(fixed_literal, literal_codes_fixed);
        std::array<std::uint8_t, 30U> distance_fixed{};
        distance_fixed.fill(5U);
        std::array<std::uint16_t, 30U> distance_codes_fixed{};
        assign_codes(distance_fixed, distance_codes_fixed);
        write_tokens(bits, tokens, literal_codes_fixed, fixed_literal, distance_codes_fixed, distance_fixed);
        return;
    }
    bits.put((last ? 1U : 0U) | (2U << 1U), 3U);
    bits.put(literal_used - 257U, 5U);
    bits.put(distance_used - 1U, 5U);
    bits.put(code_used - 4U, 4U);
    for (std::uint32_t index = 0U; index < code_used; ++index) {
        bits.put(code_lengths[length_code_order[index]], 3U);
    }
    std::array<std::uint16_t, length_code_codes> code_bits{};
    assign_codes(code_lengths, code_bits);
    for (const run_code& run : runs) {
        bits.put(code_bits[run.symbol], code_lengths[run.symbol]);
        if (run.symbol == 16U) {
            bits.put(run.extra, 2U);
        } else if (run.symbol == 17U) {
            bits.put(run.extra, 3U);
        } else if (run.symbol == 18U) {
            bits.put(run.extra, 7U);
        }
    }
    std::array<std::uint16_t, literal_codes> literal_bits{};
    std::array<std::uint16_t, distance_codes> distance_bits{};
    assign_codes(literal_lengths, literal_bits);
    assign_codes(distance_lengths, distance_bits);
    write_tokens(bits, tokens, literal_bits, literal_lengths, distance_bits, distance_lengths);
}

std::uint32_t hash_at(std::span<const std::byte> data, std::size_t at) noexcept {
    const auto a = static_cast<std::uint32_t>(data[at]);
    const auto b = static_cast<std::uint32_t>(data[at + 1U]);
    const auto c = static_cast<std::uint32_t>(data[at + 2U]);
    return (((a << 16U) | (b << 8U) | c) * 2654435761U) >> 17U;
}

void deflate(std::span<const std::byte> data, png_compression level, std::vector<std::byte>& output) {
    bit_writer bits(output);
    if (level == png_compression::none || data.empty()) {
        write_stored(bits, data, true);
        bits.align();
        return;
    }
    const search_effort effort = effort_of(level);
    constexpr std::uint32_t none = std::numeric_limits<std::uint32_t>::max();
    std::vector<std::uint32_t> head(hash_size, none);
    std::vector<std::uint32_t> previous(window_size, none);
    const auto insert = [&](std::size_t at) noexcept {
        if (at + min_match <= data.size()) {
            const std::uint32_t hash = hash_at(data, at);
            previous[at & (window_size - 1U)] = head[hash];
            head[hash] = static_cast<std::uint32_t>(at);
        }
    };
    const auto longest = [&](std::size_t at, std::size_t& distance) noexcept {
        if (at + min_match > data.size()) {
            return std::size_t{0U};
        }
        const std::size_t limit = std::min(max_match, data.size() - at);
        std::size_t best = 0U;
        std::uint32_t candidate = head[hash_at(data, at)];
        for (std::uint32_t steps = 0U; candidate != none && steps < effort.chain; ++steps) {
            const std::size_t from = candidate;
            if (from >= at || at - from > window_size - 1U) {
                break;
            }
            std::size_t length = 0U;
            while (length < limit && data[from + length] == data[at + length]) {
                ++length;
            }
            if (length > best) {
                best = length;
                distance = at - from;
                if (length >= effort.good_length || length == limit) {
                    break;
                }
            }
            candidate = previous[from & (window_size - 1U)];
        }
        return best >= min_match ? best : std::size_t{0U};
    };

    std::vector<token> tokens;
    tokens.reserve(block_symbols);
    std::size_t block_start = 0U;
    std::size_t at = 0U;
    while (at < data.size()) {
        std::size_t distance = 0U;
        const std::size_t length = longest(at, distance);
        if (length > 0U && effort.lazy && length < effort.good_length && at + 1U < data.size()) {
            insert(at);
            std::size_t next_distance = 0U;
            if (longest(at + 1U, next_distance) > length) {
                // A longer match starts at the next byte: this one goes out as a literal.
                tokens.push_back({0U, static_cast<std::uint16_t>(data[at])});
                ++at;
            } else {
                for (std::size_t step = 1U; step < length; ++step) {
                    insert(at + step);
                }
                tokens.push_back({static_cast<std::uint16_t>(length), static_cast<std::uint16_t>(distance)});
                at += length;
            }
        } else if (length > 0U) {
            for (std::size_t step = 0U; step < length; ++step) {
                insert(at + step);
            }
            tokens.push_back({static_cast<std::uint16_t>(length), static_cast<std::uint16_t>(distance)});
            at += length;
        } else {
            insert(at);
            tokens.push_back({0U, static_cast<std::uint16_t>(data[at])});
            ++at;
        }
        if (tokens.size() >= block_symbols - 1U) {
            write_block(bits, tokens, data.subspan(block_start, at - block_start), at == data.size());
            tokens.clear();
            block_start = at;
        }
    }
    if (!tokens.empty() || block_start == 0U) {
        write_block(bits, tokens, data.subspan(block_start, at - block_start), true);
    }
    bits.align();
}

void put_u32(std::vector<std::byte>& out, std::uint32_t value) {
    for (std::uint32_t shift = 24U;; shift -= 8U) {
        out.push_back(static_cast<std::byte>((value >> shift) & 0xFFU));
        if (shift == 0U) {
            break;
        }
    }
}

void put_chunk(std::vector<std::byte>& out, const char (&kind)[5], std::span<const std::byte> data) {
    put_u32(out, static_cast<std::uint32_t>(data.size()));
    const std::size_t start = out.size();
    for (std::size_t index = 0U; index < 4U; ++index) {
        out.push_back(static_cast<std::byte>(kind[index]));
    }
    out.insert(out.end(), data.begin(), data.end());
    put_u32(out, ~detail::update_crc32(0xFFFFFFFFU, {out.data() + start, out.size() - start}));
}

std::uint32_t adler32(std::span<const std::byte> data) noexcept {
    std::uint32_t a = 1U;
    std::uint32_t b = 0U;
    std::size_t at = 0U;
    while (at < data.size()) {
        const std::size_t end = std::min(at + 5552U, data.size());
        for (; at < end; ++at) {
            a += static_cast<std::uint32_t>(data[at]);
            b += a;
        }
        a %= 65521U;
        b %= 65521U;
    }
    return (b << 16U) | a;
}

std::uint8_t paeth(std::uint8_t left, std::uint8_t up, std::uint8_t corner) noexcept {
    const int estimate = int{left} + int{up} - int{corner};
    const int to_left = std::abs(estimate - int{left});
    const int to_up = std::abs(estimate - int{up});
    const int to_corner = std::abs(estimate - int{corner});
    if (to_left <= to_up && to_left <= to_corner) {
        return left;
    }
    return to_up <= to_corner ? up : corner;
}

// The rows, each with its filter byte: the filter of the smallest sum of absolute
// differences, or none when nothing is compressed.
std::vector<std::byte> filter_rows(
    std::span<const std::byte> rgba,
    std::uint32_t width,
    std::uint32_t height,
    png_compression level) {
    const std::size_t row_bytes = std::size_t{width} * 4U;
    std::vector<std::byte> filtered((row_bytes + 1U) * height);
    std::array<std::vector<std::uint8_t>, 5U> candidates;
    for (auto& candidate : candidates) {
        candidate.resize(row_bytes);
    }
    for (std::uint32_t y = 0U; y < height; ++y) {
        const std::byte* row = rgba.data() + row_bytes * y;
        const std::byte* above = y == 0U ? nullptr : row - row_bytes;
        std::uint64_t best_sum = std::numeric_limits<std::uint64_t>::max();
        std::size_t best = 0U;
        const std::size_t kinds = level == png_compression::none ? 1U : candidates.size();
        for (std::size_t kind = 0U; kind < kinds; ++kind) {
            std::uint64_t sum = 0U;
            for (std::size_t at = 0U; at < row_bytes; ++at) {
                const auto value = static_cast<std::uint8_t>(row[at]);
                const auto left = at >= 4U ? static_cast<std::uint8_t>(row[at - 4U]) : std::uint8_t{0U};
                const auto up = above != nullptr ? static_cast<std::uint8_t>(above[at]) : std::uint8_t{0U};
                const auto corner = above != nullptr && at >= 4U
                    ? static_cast<std::uint8_t>(above[at - 4U])
                    : std::uint8_t{0U};
                std::uint32_t predicted = 0U;
                switch (kind) {
                case 1U:
                    predicted = left;
                    break;
                case 2U:
                    predicted = up;
                    break;
                case 3U:
                    predicted = (std::uint32_t{left} + up) / 2U;
                    break;
                case 4U:
                    predicted = paeth(left, up, corner);
                    break;
                default:
                    break;
                }
                const auto residual = static_cast<std::uint8_t>(value - predicted);
                candidates[kind][at] = residual;
                sum += residual < 128U ? residual : 256U - residual;
            }
            if (sum < best_sum) {
                best_sum = sum;
                best = kind;
            }
        }
        const std::size_t target = (row_bytes + 1U) * y;
        filtered[target] = static_cast<std::byte>(best);
        for (std::size_t at = 0U; at < row_bytes; ++at) {
            filtered[target + 1U + at] = static_cast<std::byte>(candidates[best][at]);
        }
    }
    return filtered;
}

} // namespace

bool try_encode_png_rgba(
    std::span<const std::byte> rgba,
    std::uint32_t width,
    std::uint32_t height,
    png_compression level,
    std::vector<std::byte>& output,
    image_error* error) noexcept {
    const auto fail = [&](image_error value) noexcept {
        if (error != nullptr) {
            *error = value;
        }
        return false;
    };
    std::size_t bytes = 0U;
    if (!detail::raster_bytes(width, height, bytes) || rgba.size() < bytes) {
        return fail(image_error::invalid_argument);
    }
    try {
        output.clear();
        static constexpr std::array<std::uint8_t, 8U> signature{
            0x89U, 0x50U, 0x4EU, 0x47U, 0x0DU, 0x0AU, 0x1AU, 0x0AU};
        for (const std::uint8_t value : signature) {
            output.push_back(static_cast<std::byte>(value));
        }
        std::vector<std::byte> header;
        put_u32(header, width);
        put_u32(header, height);
        for (const std::uint8_t value : {std::uint8_t{8U}, std::uint8_t{6U}, std::uint8_t{0U},
                 std::uint8_t{0U}, std::uint8_t{0U}}) {
            header.push_back(static_cast<std::byte>(value));
        }
        put_chunk(output, "IHDR", header);
        const auto filtered = filter_rows(rgba.first(bytes), width, height, level);
        std::vector<std::byte> stream;
        stream.reserve(filtered.size() / 2U + 64U);
        stream.push_back(std::byte{0x78U});
        stream.push_back(static_cast<std::byte>(level == png_compression::none ? 0x01U : 0x9CU));
        deflate(filtered, level, stream);
        put_u32(stream, adler32(filtered));
        put_chunk(output, "IDAT", stream);
        put_chunk(output, "IEND", {});
    } catch (const std::bad_alloc&) {
        return fail(image_error::out_of_memory);
    } catch (...) {
        return fail(image_error::out_of_memory);
    }
    if (error != nullptr) {
        *error = image_error::none;
    }
    return true;
}

} // namespace progpu::native::image
