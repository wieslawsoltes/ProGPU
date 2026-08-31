#include "progpu_native_com.hpp"

#include <cstdint>
#include <type_traits>

namespace com = progpu::native::com;

namespace {

constexpr com::guid probe_interface_id{
    0x6C3AC7D4U,
    0x8A4DU,
    0x40EEU,
    {0xA1U, 0x5EU, 0x68U, 0x3BU, 0xD2U, 0x3EU, 0xC8U, 0x91U}};

struct probe_interface : com::unknown {
    virtual std::uint32_t PROGPU_NATIVE_COM_CALL Value() noexcept = 0;
};

class probe final : public probe_interface {
public:
    explicit probe(bool& destroyed) noexcept : destroyed_(destroyed)
    {
    }

    com::result PROGPU_NATIVE_COM_CALL QueryInterface(
        com::guid_ref interface_id,
        void** value) noexcept override
    {
        if (value == nullptr) {
            return com::pointer_error;
        }
        *value = nullptr;
        if (com::guid_equal(interface_id, com::unknown_interface_id()) ||
            com::guid_equal(interface_id, probe_interface_id)) {
            *value = static_cast<probe_interface*>(this);
            AddRef();
            return com::ok;
        }
        return com::no_interface;
    }

    com::reference_count_value PROGPU_NATIVE_COM_CALL AddRef()
        noexcept override
    {
        return reference_count_.add_ref();
    }

    com::reference_count_value PROGPU_NATIVE_COM_CALL Release()
        noexcept override
    {
        return reference_count_.release(this);
    }

    std::uint32_t PROGPU_NATIVE_COM_CALL Value() noexcept override
    {
        return 42U;
    }

private:
    friend class com::atomic_reference_count<probe>;

    ~probe()
    {
        destroyed_ = true;
    }

    bool& destroyed_;
    com::atomic_reference_count<probe> reference_count_;
};

} // namespace

static_assert(std::is_standard_layout_v<com::guid>);
static_assert(sizeof(com::guid) == 16U);
static_assert(sizeof(com::result) == 4U);
static_assert(sizeof(com::reference_count_value) == 4U);
static_assert(com::succeeded(com::ok));
static_assert(com::succeeded(com::false_result));
static_assert(com::failed(com::no_interface));
static_assert(com::failed(com::pointer_error));

int main()
{
    bool destroyed = false;
    auto* raw = new probe(destroyed);
    {
        com::pointer<probe_interface> owner;
        owner.attach(raw);
        if (!owner || owner->Value() != 42U) {
            return 1;
        }

        com::pointer<com::unknown> identity;
        if (com::failed(owner.as(com::unknown_interface_id(), identity)) ||
            !identity || identity.get() != static_cast<com::unknown*>(raw)) {
            return 2;
        }

        com::pointer<probe_interface> queried;
        if (com::failed(owner.as(probe_interface_id, queried)) ||
            queried->Value() != 42U) {
            return 3;
        }

        constexpr com::guid unsupported{
            0xFFFFFFFFU,
            0xFFFFU,
            0xFFFFU,
            {0xFFU, 0xFFU, 0xFFU, 0xFFU, 0xFFU, 0xFFU, 0xFFU, 0xFFU}};
        com::pointer<com::unknown> missing;
        if (owner.as(unsupported, missing) != com::no_interface || missing) {
            return 4;
        }

        com::pointer<probe_interface> copied(owner);
        com::pointer<probe_interface> moved(std::move(copied));
        if (copied || !moved || moved->Value() != 42U) {
            return 5;
        }
    }
    return destroyed ? 0 : 6;
}
