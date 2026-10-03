#pragma once

#include "progpu_native_direct2d_compat.hpp"

#include <array>
#include <cstddef>
#include <cstdint>
#include <vector>

namespace progpu::native::direct2d::tests {

// These are deliberately broken ProGPU private-interface responses, not an
// original Microsoft COM behavior oracle. Return real owned interfaces from the
// source while changing only the HRESULT/null-output contract. This avoids a
// counterfeit private vtable and allows exact reference-balance observations.
class shared_bitmap_query_source final : public compat::bitmap {
public:
    struct response {
        com::result result;
        bool returns_pointer;
    };

    shared_bitmap_query_source(compat::bitmap* source,
        compat::scene_render_target_native* scene, response storage_response,
        response scene_response) noexcept
        : source_(source), scene_(scene), storage_response_(storage_response),
          scene_response_(scene_response) {}

    com::result PROGPU_NATIVE_COM_CALL QueryInterface(com::guid_ref id,
        void** value) noexcept override {
        if (value == nullptr) return com::pointer_error;
        *value = nullptr;
        if (com::guid_equal(id, com::unknown_interface_id()) ||
            com::guid_equal(id, compat::resource_interface_id) ||
            com::guid_equal(id, compat::bitmap_interface_id)) {
            *value = static_cast<compat::bitmap*>(this);
            AddRef();
            return com::ok;
        }
        if (com::guid_equal(id, compat::scene_render_target_native_interface_id)) {
            ++scene_queries;
            if (scene_response_.returns_pointer) {
                *value = scene_.get();
                scene_->AddRef();
            }
            return scene_response_.result;
        }
        ++storage_queries;
        if (storage_response_.returns_pointer) {
            // Do not duplicate the private IID or interface layout in a test.
            // Ask the actual owned bitmap for the exact requested interface.
            if (source_->QueryInterface(id, value) != com::ok || *value == nullptr) {
                interface_acquisition_failed = true;
                return compat::failure;
            }
        }
        return storage_response_.result;
    }

    com::reference_count_value PROGPU_NATIVE_COM_CALL AddRef() noexcept override {
        ++add_refs;
        return references_.add_ref();
    }
    com::reference_count_value PROGPU_NATIVE_COM_CALL Release() noexcept override {
        ++releases;
        return references_.release(this);
    }
    void PROGPU_NATIVE_COM_CALL GetFactory(compat::factory** value) const noexcept override {
        ++factory_reads;
        source_->GetFactory(value);
    }
    compat::size_f PROGPU_NATIVE_COM_CALL GetSize() const noexcept override {
        ++metadata_reads;
        return source_->GetSize();
    }
    compat::size_u PROGPU_NATIVE_COM_CALL GetPixelSize() const noexcept override {
        ++metadata_reads;
        return source_->GetPixelSize();
    }
    compat::pixel_format PROGPU_NATIVE_COM_CALL GetPixelFormat() const noexcept override {
        ++metadata_reads;
        return source_->GetPixelFormat();
    }
    void PROGPU_NATIVE_COM_CALL GetDpi(float* x, float* y) const noexcept override {
        ++metadata_reads;
        source_->GetDpi(x, y);
    }
    com::result PROGPU_NATIVE_COM_CALL CopyFromBitmap(const compat::point_2u* point,
        compat::bitmap* source, const compat::rectangle_u* rectangle) noexcept override {
        ++copies;
        return source_->CopyFromBitmap(point, source, rectangle);
    }
    com::result PROGPU_NATIVE_COM_CALL CopyFromRenderTarget(const compat::point_2u* point,
        compat::render_target* source, const compat::rectangle_u* rectangle) noexcept override {
        ++copies;
        return source_->CopyFromRenderTarget(point, source, rectangle);
    }
    com::result PROGPU_NATIVE_COM_CALL CopyFromMemory(const compat::rectangle_u* rectangle,
        const void* data, std::uint32_t pitch) noexcept override {
        ++copies;
        return source_->CopyFromMemory(rectangle, data, pitch);
    }

    std::uint32_t storage_queries = 0U;
    std::uint32_t scene_queries = 0U;
    std::uint32_t add_refs = 0U;
    std::uint32_t releases = 0U;
    mutable std::uint32_t factory_reads = 0U;
    mutable std::uint32_t metadata_reads = 0U;
    std::uint32_t copies = 0U;
    bool interface_acquisition_failed = false;

private:
    friend class com::atomic_reference_count<shared_bitmap_query_source>;
    ~shared_bitmap_query_source() = default;
    com::atomic_reference_count<shared_bitmap_query_source> references_;
    com::pointer<compat::bitmap> source_;
    com::pointer<compat::scene_render_target_native> scene_;
    response storage_response_;
    response scene_response_;
};

inline bool shared_bitmap_query_failure_contract(compat::render_target* target,
    compat::bitmap* source, compat::scene_render_target_native* scene) {
    using response = shared_bitmap_query_source::response;
    struct fault_case {
        response storage;
        response scene;
        com::result expected;
        std::uint32_t expected_scene_queries;
    };
    // The unused scene response would succeed with an owned interface. A
    // first-query error/null success must therefore stop, not accidentally
    // publish a valid-looking alternate source.
    constexpr response available_scene{com::ok, true};
    constexpr response absent_storage{com::no_interface, false};
    constexpr std::array cases{
        fault_case{{com::ok, false}, available_scene, compat::not_implemented, 0U},
        fault_case{{com::false_result, false}, available_scene, compat::not_implemented, 0U},
        fault_case{{com::no_interface, true}, available_scene, com::no_interface, 0U},
        fault_case{{com::out_of_memory, false}, available_scene, com::out_of_memory, 0U},
        fault_case{{com::out_of_memory, true}, available_scene, com::out_of_memory, 0U},
        fault_case{{compat::failure, false}, available_scene, compat::failure, 0U},
        fault_case{{compat::failure, true}, available_scene, compat::failure, 0U},
        fault_case{absent_storage, {com::ok, false}, compat::not_implemented, 1U},
        fault_case{absent_storage, {com::false_result, false}, compat::not_implemented, 1U},
        fault_case{absent_storage, {com::no_interface, false}, com::no_interface, 1U},
        fault_case{absent_storage, {com::no_interface, true}, com::no_interface, 1U},
        fault_case{absent_storage, {com::out_of_memory, false}, com::out_of_memory, 1U},
        fault_case{absent_storage, {com::out_of_memory, true}, com::out_of_memory, 1U}};

    const auto snapshot = [](compat::scene_render_target_native* value,
        std::vector<std::byte>& bytes) {
        bytes.resize(static_cast<std::size_t>(value->GetRequiredSceneSize()));
        std::uint64_t written = 0U;
        return !bytes.empty() && value->BuildScene(bytes.data(), bytes.size(), &written) == com::ok &&
            written == bytes.size();
    };
    // These are owned ProGPU objects with observable shared atomic COM counts,
    // not assumptions about arbitrary third-party AddRef return values.
    const auto reference_count = [](com::unknown* value) {
        const auto result = value->AddRef();
        value->Release();
        return result;
    };
    com::pointer<compat::factory> factory;
    target->GetFactory(factory.put());
    if (!factory) return false;
    std::vector<std::byte> before;
    if (!snapshot(scene, before)) return false;
    for (const auto& test : cases) {
        auto* faulty = new shared_bitmap_query_source(source, scene, test.storage, test.scene);
        com::pointer<compat::bitmap> faulty_owner;
        faulty_owner.attach(faulty);
        const auto source_references = reference_count(source);
        const auto scene_references = reference_count(scene);
        const auto factory_references = reference_count(factory.get());
        auto* output = reinterpret_cast<compat::bitmap*>(static_cast<std::uintptr_t>(1U));
        const auto result = target->CreateSharedBitmap(compat::bitmap_interface_id,
            faulty_owner.get(), nullptr, &output);
        // Only attach an actual returned pointer; a preserved sentinel is a
        // failure, not a COM object that may safely be released.
        com::pointer<compat::bitmap> unexpected_output;
        if (output != reinterpret_cast<compat::bitmap*>(static_cast<std::uintptr_t>(1U))) {
            unexpected_output.attach(output);
        }
        if (result != test.expected || output != nullptr || faulty->storage_queries != 1U ||
            faulty->scene_queries != test.expected_scene_queries || faulty->factory_reads != 1U ||
            faulty->metadata_reads != 0U || faulty->copies != 0U ||
            faulty->interface_acquisition_failed || faulty->add_refs != 0U || faulty->releases != 0U ||
            reference_count(source) != source_references || reference_count(scene) != scene_references ||
            reference_count(factory.get()) != factory_references) return false;
        std::vector<std::byte> after;
        if (!snapshot(scene, after) || after != before) return false;
    }
    return true;
}

} // namespace progpu::native::direct2d::tests
