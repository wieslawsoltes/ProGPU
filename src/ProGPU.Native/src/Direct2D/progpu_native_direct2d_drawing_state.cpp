#include "progpu_native_direct2d_drawing_state.hpp"

#include <mutex>
#include <new>
#include <utility>

namespace progpu::native::direct2d::compat::detail {
namespace {

[[nodiscard]] bool valid_description(
    const drawing_state_description& value) noexcept
{
    return value.antialias <= antialias_mode::aliased &&
        value.text_antialias <= text_antialias_mode::aliased &&
        core::valid_transform(&value.transform);
}

[[nodiscard]] constexpr drawing_state_description default_description()
    noexcept
{
    return {
        antialias_mode::per_primitive,
        text_antialias_mode::default_value,
        0U,
        0U,
        {1.0F, 0.0F, 0.0F, 1.0F, 0.0F, 0.0F}};
}

class portable_drawing_state_block final : public drawing_state_block {
public:
    portable_drawing_state_block(
        factory* owner,
        const drawing_state_description& description,
        rendering_parameters* text_rendering_parameters) noexcept
        : owner_(owner),
          description_(description),
          text_rendering_parameters_(text_rendering_parameters)
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
            com::guid_equal(interface_id, resource_interface_id) ||
            com::guid_equal(
                interface_id, drawing_state_block_interface_id)) {
            *value = static_cast<drawing_state_block*>(this);
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

    void PROGPU_NATIVE_COM_CALL GetFactory(factory** value) const
        noexcept override
    {
        if (value == nullptr) {
            return;
        }
        *value = owner_.get();
        if (*value != nullptr) {
            (*value)->AddRef();
        }
    }

    void PROGPU_NATIVE_COM_CALL GetDescription(
        drawing_state_description* description) const noexcept override
    {
        if (description == nullptr) {
            return;
        }
        const std::lock_guard lock(mutex_);
        *description = description_;
    }

    void PROGPU_NATIVE_COM_CALL SetDescription(
        const drawing_state_description* description) noexcept override
    {
        if (description == nullptr || !valid_description(*description)) {
            return;
        }
        const std::lock_guard lock(mutex_);
        description_ = *description;
    }

    void PROGPU_NATIVE_COM_CALL SetTextRenderingParams(
        rendering_parameters* parameters) noexcept override
    {
        com::pointer<rendering_parameters> replacement(parameters);
        const std::lock_guard lock(mutex_);
        text_rendering_parameters_ = std::move(replacement);
    }

    void PROGPU_NATIVE_COM_CALL GetTextRenderingParams(
        rendering_parameters** parameters) const noexcept override
    {
        if (parameters == nullptr) {
            return;
        }
        const std::lock_guard lock(mutex_);
        *parameters = text_rendering_parameters_.get();
        if (*parameters != nullptr) {
            (*parameters)->AddRef();
        }
    }

private:
    friend class com::atomic_reference_count<portable_drawing_state_block>;
    ~portable_drawing_state_block() = default;

    com::atomic_reference_count<portable_drawing_state_block>
        reference_count_;
    com::pointer<factory> owner_;
    mutable std::mutex mutex_;
    drawing_state_description description_{};
    com::pointer<rendering_parameters> text_rendering_parameters_;
};

} // namespace

com::result create_drawing_state_block(
    factory* owner,
    const drawing_state_description* description,
    rendering_parameters* text_rendering_parameters,
    drawing_state_block** value) noexcept
{
    if (value == nullptr) {
        return com::pointer_error;
    }
    *value = nullptr;
    if (owner == nullptr ||
        (description != nullptr && !valid_description(*description))) {
        return com::invalid_argument;
    }
    try {
        auto* created = new portable_drawing_state_block(
            owner,
            description == nullptr ? default_description() : *description,
            text_rendering_parameters);
        *value = created;
        return com::ok;
    } catch (const std::bad_alloc&) {
        return com::out_of_memory;
    } catch (...) {
        return failure;
    }
}

} // namespace progpu::native::direct2d::compat::detail
