#pragma once

#include <cstddef>
#include <cstdint>
#include <limits>
#include <vector>

namespace progpu::native::text {

// Private publication guard. Inspect owned allocation addresses/capacities,
// never their unused elements; unsafe address arithmetic fails closed.
class owned_output_range final {
public:
    owned_output_range(const void* output, std::size_t bytes) noexcept
        : begin_(reinterpret_cast<std::uintptr_t>(output)), bytes_(bytes),
          valid_((bytes == 0U || output != nullptr) && bytes <= maximum - begin_) {}

    bool overlaps(const void* storage, std::size_t bytes) const noexcept {
        if (bytes_ == 0U || bytes == 0U) return false;
        const auto begin = reinterpret_cast<std::uintptr_t>(storage);
        if (!valid_ || storage == nullptr || bytes > maximum - begin) return true;
        return begin_ < begin + bytes && begin < begin_ + bytes_;
    }

    template<class T, class Allocator>
    bool overlaps(const std::vector<T, Allocator>& storage) const noexcept {
        if (storage.capacity() > maximum / sizeof(T)) return true;
        return overlaps(storage.data(), storage.capacity() * sizeof(T));
    }

private:
    static constexpr auto maximum = std::numeric_limits<std::uintptr_t>::max();
    std::uintptr_t begin_;
    std::size_t bytes_;
    bool valid_;
};

} // namespace progpu::native::text
