#include "progpu_native_text.hpp"
#include "progpu_native_embedded_data.hpp"

#include <cstddef>
#include <span>

namespace progpu::native::text {

namespace {

class default_normalization_holder final {
public:
    default_normalization_holder() noexcept {
        const auto bytes = std::span<const std::byte>{
            reinterpret_cast<const std::byte*>(
                generated::unicode_normalization_data),
            generated::unicode_normalization_data_size};
        unicode_error error = unicode_error::none;
        valid_ = unicode_normalization_data::try_create(
            bytes, data_, &error);
    }

    const unicode_normalization_data* get() const noexcept {
        return valid_ ? &data_ : nullptr;
    }

private:
    unicode_normalization_data data_{};
    bool valid_ = false;
};

}

const unicode_normalization_data*
get_default_unicode_normalization_data() noexcept {
    static const default_normalization_holder holder{};
    return holder.get();
}

}
