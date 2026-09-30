#include "../src/Text/Interop/progpu_native_text_font_source.hpp"
#include "progpu_native_hint_fault_fixture.hpp"
#include "progpu_native_hinted_transport_controls.hpp"

#include <array>
#include <cstring>
#include <iostream>
#include <vector>

namespace {
struct batch_owner final {
    progpu_native_hinted_batch* value = nullptr;
    ~batch_owner() { progpu_native_hinted_batch_destroy(value); }
};

#if defined(PROGPU_NATIVE_FONT_HINTING)
void verify_public_batch(const progpu_native_hinted_batch* batch,
    const progpu::native::text::hinted_glyph_batch& original) {
    using namespace progpu::native::text;
    using tests::transport_require;
    progpu_native_hinted_batch_counts counts{91U, 92U, 93U};
    transport_require(progpu_native_hinted_batch_get_counts(batch, &counts) == PROGPU_NATIVE_STATUS_SUCCESS);
    progpu_native_hinted_batch_counts expected{};
    transport_require(get_hinted_batch_counts(original, expected) == hinted_transport_error::none &&
        counts.glyphs == expected.glyphs && counts.points == expected.points && counts.contours == expected.contours);
    std::vector<progpu_native_hinted_glyph> glyphs(counts.glyphs + 1U), reference_glyphs(glyphs.size());
    std::vector<progpu_native_hinted_point> points(counts.points + 1U), reference_points(points.size());
    std::vector<std::uint8_t> tags(points.size(), 0xA5U), reference_tags(tags);
    std::vector<std::int32_t> contours(counts.contours + 1U, -77), reference_contours(contours);
    std::memset(glyphs.data(), 0xA5, glyphs.size() * sizeof(glyphs[0]));
    std::memset(points.data(), 0xA5, points.size() * sizeof(points[0]));
    reference_glyphs = glyphs;
    reference_points = points;
    const auto same = [&] {
        return std::memcmp(glyphs.data(), reference_glyphs.data(), glyphs.size() * sizeof(glyphs[0])) == 0 &&
            std::memcmp(points.data(), reference_points.data(), points.size() * sizeof(points[0])) == 0 &&
            tags == reference_tags && contours == reference_contours;
    };
    const auto copy = [&](std::uint32_t capacity) {
        return progpu_native_hinted_batch_copy(batch, glyphs.data(), static_cast<std::uint32_t>(glyphs.size()),
            points.data(), static_cast<std::uint32_t>(points.size()), tags.data(), capacity,
            contours.data(), static_cast<std::uint32_t>(contours.size()));
    };
    transport_require(counts.points != 0U && copy(counts.points - 1U) == PROGPU_NATIVE_STATUS_INVALID_ARGUMENT && same());
    transport_require(progpu_native_hinted_batch_copy(batch, glyphs.data(), static_cast<std::uint32_t>(glyphs.size()),
        points.data(), static_cast<std::uint32_t>(points.size()), reinterpret_cast<std::uint8_t*>(points.data()),
        static_cast<std::uint32_t>(points.size()), contours.data(), static_cast<std::uint32_t>(contours.size())) ==
        PROGPU_NATIVE_STATUS_INVALID_ARGUMENT && same());
    transport_require(copy(static_cast<std::uint32_t>(tags.size())) == PROGPU_NATIVE_STATUS_SUCCESS);
    transport_require(static_cast<bool>(copy_hinted_batch(original, reference_glyphs, reference_points,
        reference_tags, reference_contours, hinted_transport_policy::scalar_reference)) && same());
    transport_require(progpu_native_hinted_batch_get_counts(batch,
        reinterpret_cast<progpu_native_hinted_batch_counts*>(const_cast<progpu_native_hinted_batch*>(batch))) ==
        PROGPU_NATIVE_STATUS_INVALID_ARGUMENT);
    transport_require(copy(static_cast<std::uint32_t>(tags.size())) == PROGPU_NATIVE_STATUS_SUCCESS && same());
}
#endif
}

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
        batch_owner public_batch;
        for (const auto policy : {font_hint_policy::truetype_35, font_hint_policy::truetype_40}) {
            context_owner context;
            transport_require(progpu_native_text_context_create(PROGPU_NATIVE_ABI_VERSION,
                reinterpret_cast<const std::uint8_t*>(original.data()), original.size(), 0U,
                nullptr, 0U, &context.value) == PROGPU_NATIVE_STATUS_SUCCESS);
            const auto source = select_context_font_source(context.value, 0U);
            const hinted_font_configuration configuration{13U * 64U, 13U * 64U, policy, 0U, 0U, {}};
            alignas(std::max_align_t) progpu_native_hinted_font_request request{PROGPU_NATIVE_ABI_VERSION,
                sizeof(progpu_native_hinted_font_request), 0U, 13U * 64U, 13U * 64U,
                static_cast<std::uint32_t>(policy), 0U, 0U, 0U, 0U};
            progpu_native_hinted_batch* candidate = nullptr;
            request.reserved = 1U;
            transport_require(progpu_native_text_context_capture_hinted_batch(context.value, &request, nullptr,
                ids.data(), static_cast<std::uint32_t>(ids.size()), &candidate) == PROGPU_NATIVE_STATUS_INVALID_ARGUMENT &&
                candidate == nullptr);
            request.reserved = 0U;
            request.x_phase_26_6 = 64U;
            transport_require(progpu_native_text_context_capture_hinted_batch(context.value, &request, nullptr,
                ids.data(), static_cast<std::uint32_t>(ids.size()), &candidate) == PROGPU_NATIVE_STATUS_INVALID_ARGUMENT &&
                candidate == nullptr);
            request.x_phase_26_6 = 0U;
            transport_require(progpu_native_text_context_capture_hinted_batch(context.value, &request, nullptr,
                ids.data(), static_cast<std::uint32_t>(ids.size()), reinterpret_cast<progpu_native_hinted_batch**>(&request)) ==
                PROGPU_NATIVE_STATUS_INVALID_ARGUMENT && request.abi_version == PROGPU_NATIVE_ABI_VERSION);
#if defined(PROGPU_NATIVE_FONT_HINTING)
            transport_require(capture_context_hinted(context.value, 0U, configuration, ids, retained, error));
            transport_require(retained->identity->source == source && retained->identity->policy == policy);
            const auto saved = retained;
            transport_require(progpu_native_text_context_capture_hinted_batch(context.value, &request, nullptr,
                ids.data(), static_cast<std::uint32_t>(ids.size()), &candidate) == PROGPU_NATIVE_STATUS_SUCCESS);
            progpu_native_hinted_batch_destroy(public_batch.value);
            public_batch.value = candidate;
            verify_public_batch(public_batch.value, *saved);
            const std::array<std::uint32_t, 3> faulty{1U, 1U, 2U};
            transport_require(progpu_native_text_context_capture_hinted_batch(context.value, &request, nullptr,
                faulty.data(), static_cast<std::uint32_t>(faulty.size()), &candidate) == PROGPU_NATIVE_STATUS_INTERNAL_ERROR &&
                candidate == public_batch.value);
            verify_public_batch(public_batch.value, *saved);
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
                batch_owner published_fallback;
                request.font_index = palette;
                transport_require(progpu_native_text_context_capture_hinted_batch(context.value, &request, nullptr,
                    ids.data(), static_cast<std::uint32_t>(ids.size()), &published_fallback.value) == PROGPU_NATIVE_STATUS_SUCCESS);
                verify_public_batch(published_fallback.value, *fallback);
            }
            request.font_index = 0U;
            transport_require(retained == saved && retained->identity->source == source);
            tests::verify_hinted_transport(*retained);
#else
            transport_require(progpu_native_text_context_capture_hinted_batch(context.value, &request, nullptr,
                ids.data(), static_cast<std::uint32_t>(ids.size()), &candidate) == PROGPU_NATIVE_STATUS_UNSUPPORTED &&
                candidate == nullptr);
            transport_require(source != nullptr && !capture_context_hinted(context.value, 0U, configuration, ids, retained, error) &&
                error == hinted_font_error::dependency_unavailable && retained == nullptr);
#endif
        }
#if defined(PROGPU_NATIVE_FONT_HINTING)
        tests::verify_hinted_transport(*retained); // both contexts and native faces have retired
        verify_public_batch(public_batch.value, *retained);
#endif
        std::cout << "native hinted context ownership controls passed\n";
        return 0;
    } catch (const std::exception& failure) {
        std::cerr << failure.what() << '\n';
        return 1;
    }
}
