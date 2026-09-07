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

[[nodiscard]] bool valid_description(
    const drawing_state_description1& value) noexcept
{
    return value.antialias <= antialias_mode::aliased &&
        value.text_antialias <= text_antialias_mode::aliased &&
        value.blend <= primitive_blend::maximum &&
        value.units <= unit_mode::pixels &&
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

[[nodiscard]] constexpr drawing_state_description1 default_description1()
    noexcept
{
    const drawing_state_description base = default_description();
    return {
        base.antialias,
        base.text_antialias,
        base.tag1,
        base.tag2,
        base.transform,
        primitive_blend::source_over,
        unit_mode::dips};
}

[[nodiscard]] constexpr drawing_state_description1 extend_description(
    const drawing_state_description& value) noexcept
{
    return {
        value.antialias,
        value.text_antialias,
        value.tag1,
        value.tag2,
        value.transform,
        primitive_blend::source_over,
        unit_mode::dips};
}

class portable_drawing_state_block final : public drawing_state_block1 {
public:
    portable_drawing_state_block(
        factory* owner,
        const drawing_state_description1& description,
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
                interface_id, drawing_state_block_interface_id) ||
            com::guid_equal(
                interface_id, drawing_state_block1_interface_id)) {
            *value = com::guid_equal(
                    interface_id, drawing_state_block1_interface_id)
                ? static_cast<void*>(static_cast<drawing_state_block1*>(this))
                : static_cast<void*>(static_cast<drawing_state_block*>(this));
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
        *description = {
            description_.antialias,
            description_.text_antialias,
            description_.tag1,
            description_.tag2,
            description_.transform};
    }

    void PROGPU_NATIVE_COM_CALL SetDescription(
        const drawing_state_description* description) noexcept override
    {
        if (description == nullptr || !valid_description(*description)) {
            return;
        }
        const std::lock_guard lock(mutex_);
        description_.antialias = description->antialias;
        description_.text_antialias = description->text_antialias;
        description_.tag1 = description->tag1;
        description_.tag2 = description->tag2;
        description_.transform = description->transform;
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

    void PROGPU_NATIVE_COM_CALL GetDescription1(
        drawing_state_description1* description) const noexcept override
    {
        if (description == nullptr) {
            return;
        }
        const std::lock_guard lock(mutex_);
        *description = description_;
    }

    void PROGPU_NATIVE_COM_CALL SetDescription1(
        const drawing_state_description1* description) noexcept override
    {
        if (description == nullptr || !valid_description(*description)) {
            return;
        }
        const std::lock_guard lock(mutex_);
        description_ = *description;
    }

private:
    friend class com::atomic_reference_count<portable_drawing_state_block>;
    ~portable_drawing_state_block() = default;

    com::atomic_reference_count<portable_drawing_state_block>
        reference_count_;
    com::pointer<factory> owner_;
    mutable std::mutex mutex_;
    drawing_state_description1 description_{};
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
            description == nullptr
                ? default_description1()
                : extend_description(*description),
            text_rendering_parameters);
        *value = created;
        return com::ok;
    } catch (const std::bad_alloc&) {
        return com::out_of_memory;
    } catch (...) {
        return failure;
    }
}

com::result create_drawing_state_block1(
    factory* owner,
    const drawing_state_description1* description,
    rendering_parameters* text_rendering_parameters,
    drawing_state_block1** value) noexcept
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
            description == nullptr ? default_description1() : *description,
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
