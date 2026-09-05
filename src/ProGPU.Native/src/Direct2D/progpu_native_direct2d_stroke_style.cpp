#include "progpu_native_direct2d_stroke_style.hpp"

#include <algorithm>
#include <new>
#include <vector>

namespace progpu::native::direct2d::compat {
namespace {

class portable_stroke_style final : public stroke_style1 {
public:
    portable_stroke_style(
        factory* owner,
        const stroke_style_properties& properties,
        stroke_transform_type transform_type,
        const float* dashes,
        std::uint32_t dash_count)
        : owner_(owner), properties_(properties), transform_type_(transform_type)
    {
        if (dash_count != 0U) {
            dashes_.assign(dashes, dashes + dash_count);
        }
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
            com::guid_equal(interface_id, stroke_style_interface_id) ||
            com::guid_equal(interface_id, stroke_style1_interface_id)) {
            *value = static_cast<stroke_style1*>(this);
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

    cap_style PROGPU_NATIVE_COM_CALL GetStartCap() const
        noexcept override
    {
        return properties_.start_cap;
    }

    cap_style PROGPU_NATIVE_COM_CALL GetEndCap() const
        noexcept override
    {
        return properties_.end_cap;
    }

    cap_style PROGPU_NATIVE_COM_CALL GetDashCap() const
        noexcept override
    {
        return properties_.dash_cap;
    }

    float PROGPU_NATIVE_COM_CALL GetMiterLimit() const
        noexcept override
    {
        return properties_.miter_limit;
    }

    line_join PROGPU_NATIVE_COM_CALL GetLineJoin() const
        noexcept override
    {
        return properties_.join;
    }

    float PROGPU_NATIVE_COM_CALL GetDashOffset() const
        noexcept override
    {
        return properties_.dash_offset;
    }

    dash_style PROGPU_NATIVE_COM_CALL GetDashStyle() const
        noexcept override
    {
        return properties_.dash;
    }

    std::uint32_t PROGPU_NATIVE_COM_CALL GetDashesCount() const
        noexcept override
    {
        return static_cast<std::uint32_t>(dashes_.size());
    }

    void PROGPU_NATIVE_COM_CALL GetDashes(
        float* dashes,
        std::uint32_t dash_count) const noexcept override
    {
        if (dashes == nullptr || dash_count == 0U) {
            return;
        }
        std::copy_n(
            dashes_.data(),
            std::min<std::size_t>(dashes_.size(), dash_count),
            dashes);
    }

    stroke_transform_type PROGPU_NATIVE_COM_CALL GetStrokeTransformType()
        const noexcept override
    {
        return transform_type_;
    }

private:
    friend class com::atomic_reference_count<portable_stroke_style>;
    ~portable_stroke_style() = default;

    com::atomic_reference_count<portable_stroke_style> reference_count_;
    com::pointer<factory> owner_;
    stroke_style_properties properties_{};
    stroke_transform_type transform_type_{};
    std::vector<float> dashes_;
};

} // namespace

com::result create_stroke_style1(
    factory* owner,
    const stroke_style_properties* properties,
    stroke_transform_type transform_type,
    const float* dashes,
    std::uint32_t dash_count,
    stroke_style1** value) noexcept
{
    if (value == nullptr) {
        return com::pointer_error;
    }
    *value = nullptr;
    if (owner == nullptr || properties == nullptr ||
        transform_type > stroke_transform_type::hairline ||
        !core::valid_stroke_style(*properties, dashes, dash_count)) {
        return com::invalid_argument;
    }
    try {
        auto* created = new portable_stroke_style(
            owner, *properties, transform_type, dashes, dash_count);
        *value = created;
        return com::ok;
    } catch (const std::bad_alloc&) {
        return com::out_of_memory;
    } catch (...) {
        return failure;
    }
}

com::result detail::create_stroke_style(
    factory* owner,
    const stroke_style_properties* properties,
    const float* dashes,
    std::uint32_t dash_count,
    stroke_style** value) noexcept
{
    if (value == nullptr) {
        return com::pointer_error;
    }
    *value = nullptr;
    stroke_style1* created = nullptr;
    const auto result = create_stroke_style1(
        owner, properties, stroke_transform_type::normal,
        dashes, dash_count, &created);
    *value = created;
    return result;
}

} // namespace progpu::native::direct2d::compat
