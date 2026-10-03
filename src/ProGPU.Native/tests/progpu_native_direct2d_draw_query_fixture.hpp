#pragma once

#include "progpu_native_direct2d_shared_bitmap_query_fixture.hpp"

#include <algorithm>
#include <array>
#include <cstddef>
#include <cstdint>
#include <utility>
#include <vector>

namespace progpu::native::direct2d::tests {

// Deliberately malformed portable-provider responses, not a Microsoft oracle.
// The returned pointers are actual owned interfaces; no private IID or vtable
// is copied into this fixture. Public geometry/brush calls drive classification.
class draw_query_brush final : public compat::brush {
public:
    using response = shared_bitmap_query_source::response;
    draw_query_brush(std::array<compat::brush*, 4U> sources,
        std::array<response, 4U> responses) noexcept : responses_(responses) {
        for (std::size_t i = 0U; i < sources.size(); ++i)
            sources_[i] = com::pointer<compat::brush>(sources[i]);
    }

    com::result PROGPU_NATIVE_COM_CALL QueryInterface(com::guid_ref id,
        void** value) noexcept override {
        if (value == nullptr) return com::pointer_error;
        *value = nullptr;
        if (com::guid_equal(id, com::unknown_interface_id()) ||
            com::guid_equal(id, compat::resource_interface_id) ||
            com::guid_equal(id, compat::brush_interface_id)) {
            *value = static_cast<compat::brush*>(this);
            AddRef();
            return com::ok;
        }
        const std::size_t family = com::guid_equal(id, compat::linear_gradient_brush_interface_id) ? 1U
            : com::guid_equal(id, compat::radial_gradient_brush_interface_id) ? 2U
            : com::guid_equal(id, compat::solid_color_brush_interface_id) ? 3U : 0U;
        ++queries[family];
        if (responses_[family].returns_pointer &&
            (sources_[family]->QueryInterface(id, value) != com::ok || *value == nullptr)) {
            acquisition_failed = true;
            return compat::failure;
        }
        return responses_[family].result;
    }
    com::reference_count_value PROGPU_NATIVE_COM_CALL AddRef() noexcept override {
        return references_.add_ref();
    }
    com::reference_count_value PROGPU_NATIVE_COM_CALL Release() noexcept override {
        return references_.release(this);
    }
    void PROGPU_NATIVE_COM_CALL GetFactory(compat::factory** value) const noexcept override {
        sources_[3U]->GetFactory(value);
    }
    void PROGPU_NATIVE_COM_CALL SetOpacity(float value) noexcept override { sources_[3U]->SetOpacity(value); }
    void PROGPU_NATIVE_COM_CALL SetTransform(const compat::matrix_3x2_f* value) noexcept override {
        sources_[3U]->SetTransform(value);
    }
    float PROGPU_NATIVE_COM_CALL GetOpacity() const noexcept override { return sources_[3U]->GetOpacity(); }
    void PROGPU_NATIVE_COM_CALL GetTransform(compat::matrix_3x2_f* value) const noexcept override {
        sources_[3U]->GetTransform(value);
    }

    std::array<std::uint32_t, 4U> queries{};
    bool acquisition_failed = false;

private:
    friend class com::atomic_reference_count<draw_query_brush>;
    ~draw_query_brush() = default;
    com::atomic_reference_count<draw_query_brush> references_;
    std::array<com::pointer<compat::brush>, 4U> sources_;
    std::array<response, 4U> responses_;
};

template<class CompareScenes>
bool draw_resource_query_contract(compat::scene_factory_native* scene_factory, CompareScenes compare_scenes) {
    using response = shared_bitmap_query_source::response;
    constexpr response absent{com::no_interface, false};
    constexpr response available{com::ok, true};
    constexpr std::array faults{
        response{com::ok, false}, response{com::false_result, false},
        response{com::no_interface, true}, response{com::out_of_memory, false},
        response{com::out_of_memory, true}, response{compat::failure, false},
        response{compat::failure, true}, response{com::invalid_argument, false}};
    const auto failure_result = [](response value) {
        return com::failed(value.result) ? value.result : compat::failure;
    };
    const auto reference_count = [](com::unknown* value) {
        const auto count = value->AddRef();
        value->Release();
        return count;
    };
    const compat::scene_render_target_properties properties{32U, 32U, 96, 96, 0xD2D515U, 1U};
    com::pointer<compat::render_target> target;
    com::pointer<compat::scene_render_target_native> scene;
    com::pointer<compat::factory> factory;
    if (scene_factory->CreateSceneRenderTarget(&properties, target.put()) != com::ok ||
        target.as(compat::scene_render_target_native_interface_id, scene) != com::ok) return false;
    target->GetFactory(factory.put());
    if (!factory) return false;

    const compat::color_f green{0, 1, 0, 1};
    com::pointer<compat::solid_color_brush> solid;
    com::pointer<compat::linear_gradient_brush> linear;
    com::pointer<compat::radial_gradient_brush> radial;
    com::pointer<compat::gradient_stop_collection> stops;
    com::pointer<compat::bitmap> bitmap;
    com::pointer<compat::bitmap_brush> bitmap_brush;
    const std::array<compat::gradient_stop, 2U> stop_values{{{0, {1, 0, 0, 1}}, {1, {0, 0, 1, 1}}}};
    const compat::linear_gradient_brush_properties linear_properties{{0, 0}, {16, 16}};
    const compat::radial_gradient_brush_properties radial_properties{{8, 8}, {0, 0}, 8, 8};
    const compat::bitmap_properties bitmap_properties{{28U, compat::alpha_mode::premultiplied}, 96, 96};
    const std::array<std::uint8_t, 16U> pixels{255, 0, 0, 255, 0, 255, 0, 255,
        0, 0, 255, 255, 255, 255, 255, 255};
    if (target->CreateSolidColorBrush(&green, nullptr, solid.put()) != com::ok ||
        target->CreateGradientStopCollection(stop_values.data(), 2U, compat::gamma::gamma_2_2,
            compat::extend_mode::clamp, stops.put()) != com::ok ||
        target->CreateLinearGradientBrush(&linear_properties, nullptr, stops.get(), linear.put()) != com::ok ||
        target->CreateRadialGradientBrush(&radial_properties, nullptr, stops.get(), radial.put()) != com::ok ||
        target->CreateBitmap({2U, 2U}, pixels.data(), 8U, &bitmap_properties, bitmap.put()) != com::ok ||
        target->CreateBitmapBrush(bitmap.get(), nullptr, nullptr, bitmap_brush.put()) != com::ok) return false;
    const std::array<compat::brush*, 4U> sources{bitmap_brush.get(), linear.get(), radial.get(), solid.get()};
    const compat::rectangle_f rectangle{2, 3, 18, 19};
    const compat::rounded_rectangle rounded{rectangle, 2, 3};
    com::pointer<compat::rectangle_geometry> geometry;
    if (factory->CreateRectangleGeometry(&rectangle, geometry.put()) != com::ok) return false;
    target->SetAntialiasMode(compat::antialias_mode::aliased);

    const auto snapshot = [&](std::vector<std::byte>& bytes) {
        bytes.resize(static_cast<std::size_t>(scene->GetRequiredSceneSize()));
        std::uint64_t written = 0U;
        return !bytes.empty() && scene->BuildScene(bytes.data(), bytes.size(), &written) == com::ok &&
            written == bytes.size();
    };
    const auto prefix = [&] {
        target->BeginDraw();
        target->SetTags(37U, 41U);
        target->FillRectangle(&rectangle, solid.get());
    };
    std::vector<std::byte> original;
    prefix();
    if (target->EndDraw(nullptr, nullptr) != com::ok || !snapshot(original)) return false;
    const auto rejected = [&](com::result expected) {
        // Subsequent invalid and valid draws cannot overwrite the first error
        // or append another draw. No failed transaction can publish a stream.
        target->DrawBitmap(nullptr, nullptr, 1, compat::bitmap_interpolation_mode::nearest_neighbor, nullptr);
        target->FillRectangle(&rectangle, solid.get());
        std::uint64_t first = 0U, second = 0U;
        if (target->EndDraw(&first, &second) != expected || first != 37U || second != 41U) return false;
        compat::scene_render_target_summary summary{};
        scene->GetSummary(&summary);
        if (summary.draw_count != 1U || scene->GetRequiredSceneSize() != 0U) return false;
        std::array<std::byte, 128U> output;
        output.fill(std::byte{0xA5});
        const auto untouched = output;
        std::uint64_t written = 99U;
        if (scene->BuildScene(output.data(), output.size(), &written) != compat::wrong_state ||
            written != 0U || output != untouched) return false;
        prefix();
        std::vector<std::byte> recovered;
        return target->EndDraw(nullptr, nullptr) == com::ok && snapshot(recovered) &&
            compare_scenes(original, std::move(recovered));
    };
    const auto record_brush = [&](std::uint32_t route, compat::brush* value) {
        switch (route) {
        case 0U: target->FillRectangle(&rectangle, value); break;
        case 1U: target->FillRoundedRectangle(&rounded, value); break;
        case 2U: target->FillGeometry(geometry.get(), value, nullptr); break;
        case 3U: target->DrawGeometry(geometry.get(), value, 1, nullptr); break;
        case 4U: {
            const compat::layer_parameters layer{rectangle, nullptr, compat::antialias_mode::aliased,
                {1, 0, 0, 1, 0, 0}, 1, value, compat::layer_options::none};
            target->PushLayer(&layer, nullptr);
            target->FillRectangle(&rectangle, solid.get());
            target->PopLayer();
            break;
        }
        case 5U: target->FillOpacityMask(bitmap.get(), value, compat::opacity_mask_content::graphics,
            &rectangle, nullptr); break;
        default: break;
        }
    };

    // Six public routes reach all four family probes. Later families would
    // succeed if reached: a genuine failure cannot be concealed by fallback.
    for (std::uint32_t route = 0U; route < 6U; ++route) {
        for (std::size_t family = 0U; family < sources.size(); ++family) {
            for (const auto fault : faults) {
                std::array<response, 4U> responses;
                responses.fill(available);
                for (std::size_t prior = 0U; prior < family; ++prior) responses[prior] = absent;
                responses[family] = fault;
                com::pointer<draw_query_brush> wrapper;
                wrapper.attach(new draw_query_brush(sources, responses));
                const auto count = reference_count(sources[family]);
                prefix();
                record_brush(route, wrapper.get());
                if (wrapper->acquisition_failed || reference_count(sources[family]) != count) return false;
                for (std::size_t probe = 0U; probe < sources.size(); ++probe)
                    if (wrapper->queries[probe] != (probe <= family ? 1U : 0U)) return false;
                if (!rejected(failure_result(fault))) return false;
            }
        }
        std::array<response, 4U> responses;
        responses.fill(absent);
        com::pointer<draw_query_brush> unsupported;
        unsupported.attach(new draw_query_brush(sources, responses));
        prefix();
        record_brush(route, unsupported.get());
        if (unsupported->queries != std::array<std::uint32_t, 4U>{1U, 1U, 1U, 1U} ||
            !rejected(compat::not_implemented)) return false;
    }

    // Required bitmap interface: absence stays unsupported; other errors are
    // not rewritten. The nested bitmap-brush snapshot has the same policy.
    for (std::uint32_t route = 0U; route < 3U; ++route) {
        for (std::size_t i = 0U; i <= faults.size(); ++i) {
            const response fault = i == faults.size() ? absent : faults[i];
            com::pointer<shared_bitmap_query_source> wrapper;
            wrapper.attach(new shared_bitmap_query_source(bitmap.get(), scene.get(), fault, absent));
            com::pointer<compat::bitmap_brush> nested;
            if (route == 1U && target->CreateBitmapBrush(wrapper.get(), nullptr, nullptr, nested.put()) != com::ok)
                return false;
            const auto count = reference_count(bitmap.get());
            prefix();
            if (route == 0U) target->DrawBitmap(wrapper.get(), &rectangle, 1,
                compat::bitmap_interpolation_mode::nearest_neighbor, nullptr);
            else if (route == 1U) target->FillRectangle(&rectangle, nested.get());
            else target->FillOpacityMask(wrapper.get(), solid.get(), compat::opacity_mask_content::graphics,
                &rectangle, nullptr);
            const auto expected = i == faults.size()
                ? (route == 2U ? com::no_interface : compat::not_implemented) : failure_result(fault);
            if (wrapper->storage_queries != 1U || wrapper->scene_queries != (route == 2U ? 1U : 0U) ||
                wrapper->metadata_reads != 0U || wrapper->copies != 0U || wrapper->interface_acquisition_failed ||
                reference_count(bitmap.get()) != count || !rejected(expected)) return false;
        }
    }

    // FillOpacityMask probes a scene alternative first. A malformed result
    // cannot reach the (otherwise valid) bitmap alternative or self-use check.
    for (const auto fault : faults) {
        com::pointer<shared_bitmap_query_source> wrapper;
        wrapper.attach(new shared_bitmap_query_source(bitmap.get(), scene.get(), available, fault));
        const auto count = reference_count(scene.get());
        prefix();
        target->FillOpacityMask(wrapper.get(), solid.get(), compat::opacity_mask_content::graphics,
            &rectangle, nullptr);
        if (wrapper->scene_queries != 1U || wrapper->storage_queries != 0U || wrapper->metadata_reads != 0U ||
            reference_count(scene.get()) != count || !rejected(failure_result(fault))) return false;
    }

    // A target already in error must not even query a later malformed source.
    {
        std::array<response, 4U> responses;
        responses.fill({com::out_of_memory, true});
        com::pointer<draw_query_brush> wrapper;
        wrapper.attach(new draw_query_brush(sources, responses));
        prefix();
        target->DrawBitmap(nullptr, nullptr, 1, compat::bitmap_interpolation_mode::nearest_neighbor, nullptr);
        target->FillRectangle(&rectangle, wrapper.get());
        if (wrapper->queries != std::array<std::uint32_t, 4U>{} || !rejected(com::invalid_argument)) return false;
    }

    // Legitimate absence still selects each supported family, and a positive
    // nonzero success HRESULT with an owned interface is not misclassified.
    // Compare complete bytes to the same genuine brush, not just draw counts.
    for (std::uint32_t route = 0U; route < 6U; ++route) {
        for (std::size_t family = 0U; family < sources.size(); ++family) {
            target->BeginDraw();
            record_brush(route, sources[family]);
            std::vector<std::byte> expected;
            if (target->EndDraw(nullptr, nullptr) != com::ok || !snapshot(expected)) return false;
            for (const auto success : {com::ok, com::false_result}) {
                std::array<response, 4U> responses;
                responses.fill(absent);
                responses[family] = {success, true};
                com::pointer<draw_query_brush> wrapper;
                wrapper.attach(new draw_query_brush(sources, responses));
                target->BeginDraw();
                record_brush(route, wrapper.get());
                std::vector<std::byte> actual;
                if (target->EndDraw(nullptr, nullptr) != com::ok || !snapshot(actual) ||
                    wrapper->acquisition_failed || !compare_scenes(expected, std::move(actual))) return false;
            }
        }
    }
    for (std::uint32_t route = 0U; route < 3U; ++route) {
        const auto record_bitmap = [&](compat::bitmap* source) {
            if (route == 0U) target->DrawBitmap(source, &rectangle, 1,
                compat::bitmap_interpolation_mode::nearest_neighbor, nullptr);
            else if (route == 1U) {
                bitmap_brush->SetBitmap(source);
                target->FillRectangle(&rectangle, bitmap_brush.get());
                bitmap_brush->SetBitmap(bitmap.get());
            } else target->FillOpacityMask(source, solid.get(), compat::opacity_mask_content::graphics,
                &rectangle, nullptr);
        };
        target->BeginDraw();
        record_bitmap(bitmap.get());
        std::vector<std::byte> expected;
        if (target->EndDraw(nullptr, nullptr) != com::ok || !snapshot(expected)) return false;
        for (const auto success : {com::ok, com::false_result}) {
            com::pointer<shared_bitmap_query_source> wrapper;
            wrapper.attach(new shared_bitmap_query_source(bitmap.get(), scene.get(), {success, true}, absent));
            target->BeginDraw();
            record_bitmap(wrapper.get());
            std::vector<std::byte> actual;
            if (target->EndDraw(nullptr, nullptr) != com::ok || !snapshot(actual) ||
                wrapper->interface_acquisition_failed || wrapper->storage_queries != 1U ||
                !compare_scenes(expected, std::move(actual))) return false;
        }
    }
    return true;
}

} // namespace progpu::native::direct2d::tests
