#pragma once

#include <atomic>
#include <cstddef>
#include <cstdint>
#include <utility>

#if defined(_WIN32)
#  include <unknwn.h>
#  define PROGPU_NATIVE_COM_CALL STDMETHODCALLTYPE
#else
#  define PROGPU_NATIVE_COM_CALL
#endif

namespace progpu::native::com {

#if defined(_WIN32)
using guid = GUID;
using guid_ref = REFIID;
using result = HRESULT;
using reference_count_value = ULONG;
using unknown = IUnknown;
#else
struct guid final {
    std::uint32_t data1;
    std::uint16_t data2;
    std::uint16_t data3;
    std::uint8_t data4[8];
};

using guid_ref = const guid&;
using result = std::int32_t;
using reference_count_value = std::uint32_t;

struct unknown {
    virtual result PROGPU_NATIVE_COM_CALL QueryInterface(
        guid_ref interface_id,
        void** value) noexcept = 0;
    virtual reference_count_value PROGPU_NATIVE_COM_CALL AddRef()
        noexcept = 0;
    virtual reference_count_value PROGPU_NATIVE_COM_CALL Release()
        noexcept = 0;
};
#endif

inline constexpr result ok = 0;
inline constexpr result false_result = 1;
inline constexpr result no_interface = -2147467262;
inline constexpr result pointer_error = -2147467261;
inline constexpr result out_of_memory = -2147024882;
inline constexpr result invalid_argument = -2147024809;

[[nodiscard]] constexpr bool succeeded(result value) noexcept
{
    return value >= 0;
}

[[nodiscard]] constexpr bool failed(result value) noexcept
{
    return value < 0;
}

[[nodiscard]] constexpr bool guid_equal(
    guid_ref left,
    guid_ref right) noexcept
{
#if defined(_WIN32)
    if (left.Data1 != right.Data1 ||
        left.Data2 != right.Data2 ||
        left.Data3 != right.Data3) {
        return false;
    }
    for (std::uint32_t index = 0U; index < 8U; ++index) {
        if (left.Data4[index] != right.Data4[index]) {
            return false;
        }
    }
#else
    if (left.data1 != right.data1 ||
        left.data2 != right.data2 ||
        left.data3 != right.data3) {
        return false;
    }
    for (std::uint32_t index = 0U; index < 8U; ++index) {
        if (left.data4[index] != right.data4[index]) {
            return false;
        }
    }
#endif
    return true;
}

[[nodiscard]] inline guid_ref unknown_interface_id() noexcept
{
#if defined(_WIN32)
    return IID_IUnknown;
#else
    static constexpr guid value{
        0x00000000U,
        0x0000U,
        0x0000U,
        {0xC0U, 0x00U, 0x00U, 0x00U, 0x00U, 0x00U, 0x00U, 0x46U}};
    return value;
#endif
}

template<typename Owner>
class atomic_reference_count final {
public:
    [[nodiscard]] reference_count_value add_ref() noexcept
    {
        return value_.fetch_add(1U, std::memory_order_relaxed) + 1U;
    }

    reference_count_value release(Owner* owner) noexcept
    {
        const reference_count_value remaining = value_.fetch_sub(
            1U,
            std::memory_order_acq_rel) - 1U;
        if (remaining == 0U) {
            delete owner;
        }
        return remaining;
    }

    [[nodiscard]] reference_count_value value() const noexcept
    {
        return value_.load(std::memory_order_relaxed);
    }

private:
    std::atomic<reference_count_value> value_{1U};
};

template<typename Interface>
class pointer final {
public:
    constexpr pointer() noexcept = default;
    constexpr pointer(std::nullptr_t) noexcept
    {
    }

    explicit pointer(Interface* value) noexcept : value_(value)
    {
        internal_add_ref();
    }

    pointer(const pointer& other) noexcept : value_(other.value_)
    {
        internal_add_ref();
    }

    template<typename Other>
    explicit pointer(const pointer<Other>& other) noexcept
        : value_(other.get())
    {
        internal_add_ref();
    }

    pointer(pointer&& other) noexcept
        : value_(std::exchange(other.value_, nullptr))
    {
    }

    ~pointer()
    {
        internal_release();
    }

    pointer& operator=(std::nullptr_t) noexcept
    {
        reset();
        return *this;
    }

    pointer& operator=(const pointer& other) noexcept
    {
        if (this != &other) {
            Interface* replacement = other.value_;
            if (replacement != nullptr) {
                replacement->AddRef();
            }
            internal_release();
            value_ = replacement;
        }
        return *this;
    }

    pointer& operator=(pointer&& other) noexcept
    {
        if (this != &other) {
            internal_release();
            value_ = std::exchange(other.value_, nullptr);
        }
        return *this;
    }

    [[nodiscard]] Interface* get() const noexcept
    {
        return value_;
    }

    [[nodiscard]] Interface* operator->() const noexcept
    {
        return value_;
    }

    [[nodiscard]] explicit operator bool() const noexcept
    {
        return value_ != nullptr;
    }

    void reset() noexcept
    {
        internal_release();
    }

    void attach(Interface* value) noexcept
    {
        if (value_ != value) {
            internal_release();
            value_ = value;
        }
    }

    [[nodiscard]] Interface* detach() noexcept
    {
        return std::exchange(value_, nullptr);
    }

    [[nodiscard]] Interface** put() noexcept
    {
        reset();
        return &value_;
    }

    template<typename Other>
    result as(guid_ref interface_id, pointer<Other>& destination) const noexcept
    {
        destination.reset();
        if (value_ == nullptr) {
            return pointer_error;
        }
        void* queried = nullptr;
        const result query_result = value_->QueryInterface(
            interface_id,
            &queried);
        if (succeeded(query_result)) {
            destination.attach(static_cast<Other*>(queried));
        }
        return query_result;
    }

private:
    template<typename Other>
    friend class pointer;

    void internal_add_ref() noexcept
    {
        if (value_ != nullptr) {
            value_->AddRef();
        }
    }

    void internal_release() noexcept
    {
        Interface* released = std::exchange(value_, nullptr);
        if (released != nullptr) {
            released->Release();
        }
    }

    Interface* value_ = nullptr;
};

} // namespace progpu::native::com
