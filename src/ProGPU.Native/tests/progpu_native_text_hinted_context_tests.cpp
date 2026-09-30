#include "../src/Text/Interop/progpu_native_text_font_source.hpp"
#include "progpu_native_hint_fault_fixture.hpp"
#include "progpu_native_hinted_transport_controls.hpp"

#include <array>
#include <iostream>

int main()
{
    using namespace progpu::native::text;
    using tests::transport_require;
    struct context_owner final {
        progpu_native_text_context* value = nullptr;
        ~context_owner() { progpu_native_text_context_destroy(value); }
    };
    try {
        const auto original = progpu::native::tests::make_hint_fault_font();
        std::shared_ptr<const hinted_glyph_batch> retained;
        const std::array<std::uint32_t, 3> ids{1U, 1U, 0U};
        hinted_font_error error = hinted_font_error::none;
        for (const auto policy : {font_hint_policy::truetype_35, font_hint_policy::truetype_40}) {
            context_owner context;
            transport_require(progpu_native_text_context_create(PROGPU_NATIVE_ABI_VERSION,
                reinterpret_cast<const std::uint8_t*>(original.data()), original.size(), 0U,
                nullptr, 0U, &context.value) == PROGPU_NATIVE_STATUS_SUCCESS);
            const auto source = select_context_font_source(context.value, 0U);
            const hinted_font_configuration configuration{13U * 64U, 13U * 64U, policy, 0U, 0U, {}};
#if defined(PROGPU_NATIVE_FONT_HINTING)
            transport_require(capture_context_hinted(context.value, 0U, configuration, ids, retained, error));
            transport_require(retained->identity->source == source && retained->identity->policy == policy);
            const auto saved = retained;
            const std::array<std::uint32_t, 3> faulty{1U, 1U, 2U};
            transport_require(!capture_context_hinted(context.value, 0U, configuration, faulty, retained, error) &&
                error == hinted_font_error::hinting_failed && retained == saved);
            transport_require(capture_context_hinted(context.value, 0U, configuration, ids, retained, error) && retained == saved);
            transport_require(!capture_context_hinted(context.value, 99U, configuration, ids, retained, error) &&
                error == hinted_font_error::invalid_argument && retained == saved);
            for (std::uint32_t index = 1U; index <= 20U; ++index) {
                std::uint32_t palette = 0U;
                transport_require(progpu_native_text_context_add_fallback_font(context.value,
                    reinterpret_cast<const std::uint8_t*>(original.data()), original.size(), 0U, index,
                    &palette) == PROGPU_NATIVE_STATUS_SUCCESS && palette == index);
                std::shared_ptr<const hinted_glyph_batch> fallback;
                transport_require(capture_context_hinted(context.value, palette, configuration, ids, fallback, error));
                transport_require(fallback->identity->source == select_context_font_source(context.value, palette) &&
                    fallback->identity->source != source && fallback->glyphs == saved->glyphs);
            }
            transport_require(retained == saved && retained->identity->source == source);
            tests::verify_hinted_transport(*retained);
#else
            transport_require(source != nullptr && !capture_context_hinted(context.value, 0U, configuration, ids, retained, error) &&
                error == hinted_font_error::dependency_unavailable && retained == nullptr);
#endif
        }
#if defined(PROGPU_NATIVE_FONT_HINTING)
        tests::verify_hinted_transport(*retained); // both contexts and native faces have retired
#endif
        std::cout << "native hinted context ownership controls passed\n";
        return 0;
    } catch (const std::exception& failure) {
        std::cerr << failure.what() << '\n';
        return 1;
    }
}
