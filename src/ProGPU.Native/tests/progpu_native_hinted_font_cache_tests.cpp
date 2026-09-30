#include "progpu_native_hinted_font_cache.hpp"
#include "progpu_native_hinted_transport_controls.hpp"
#include "progpu_native_hint_fault_fixture.hpp"

#include <algorithm>
#include <array>
#include <iostream>
#include <thread>

namespace {
using namespace progpu::native::text;
using progpu::native::text::tests::transport_require;

struct thread_join final {
    std::thread& value;
    ~thread_join() { if (value.joinable()) value.join(); }
};

void verify_cache(font_hint_policy policy)
{
    auto input = progpu::native::tests::make_hint_fault_font();
    auto source = std::make_shared<const owned_font_source>(input, 0U);
    const auto other_source = std::make_shared<const owned_font_source>(input, 0U);
    std::fill(input.begin(), input.end(), std::byte{0});
    hinted_font_configuration configuration{13U * 64U, 13U * 64U, policy, 0U, 0U, {}};
    const std::array<std::uint32_t, 3> ids{1U, 1U, 0U};
    std::shared_ptr<const hinted_glyph_batch> retained;
    {
        hinted_font_cache cache;
        hinted_font_error error = hinted_font_error::hinting_failed;
        std::shared_ptr<const hinted_glyph_batch> batch;
        transport_require(cache.try_capture(source, configuration, ids, batch, error));
        transport_require(error == hinted_font_error::none && batch->identity->source == source);
        retained = batch;
        for (unsigned int repeat = 0U; repeat < 4U; ++repeat) {
            transport_require(cache.try_capture(source, configuration, ids, batch, error));
            transport_require(batch == retained);
        }
        auto statistics = cache.statistics();
        transport_require(statistics.live_entries == 1U && statistics.face_creations == 1U &&
            statistics.glyph_captures == 1U && statistics.batch_hits == 4U);
        // One warm generation remains safe under actual concurrent calls. Cache
        // serialization does not let a slot load or key replacement interleave.
        std::array<bool, 2> success{true, true};
        const auto capture = [&](std::size_t worker) {
            hinted_font_error local_error = hinted_font_error::none;
            std::shared_ptr<const hinted_glyph_batch> value;
            for (unsigned int repeat = 0U; repeat < 4U; ++repeat) {
                if (!cache.try_capture(source, configuration, ids, value, local_error) ||
                    local_error != hinted_font_error::none || value != retained) success[worker] = false;
            }
        };
        std::thread first(capture, 0U);
        const thread_join first_join{first};
        std::thread second(capture, 1U);
        const thread_join second_join{second};
        first.join(); second.join();
        transport_require(success[0] && success[1]);
        statistics = cache.statistics();
        transport_require(statistics.face_creations == 1U && statistics.glyph_captures == 1U && statistics.batch_hits == 12U);
        // Same bytes in another immutable source are not this context owner.
        transport_require(cache.try_capture(other_source, configuration, ids, batch, error));
        transport_require(batch != retained && batch->identity->source == other_source &&
            batch->glyphs == retained->glyphs && cache.statistics().live_entries == 2U);
        transport_require(cache.try_capture(source, configuration, ids, batch, error) && batch == retained);
        // Native instruction failure occurs after two valid glyph loads. Neither
        // caller publication nor the latest exact cached key/batch may change.
        const std::array<std::uint32_t, 3> faulty{1U, 1U, 2U};
        for (unsigned int attempt = 0U; attempt < 2U; ++attempt) {
            transport_require(!cache.try_capture(source, configuration, faulty, batch, error));
            transport_require(error == hinted_font_error::hinting_failed && batch == retained);
            transport_require(cache.try_capture(source, configuration, ids, batch, error) && batch == retained);
        }
        const std::array<std::uint32_t, 3> invalid{1U, 1U, 0xFFFFFFFFU};
        transport_require(!cache.try_capture(source, configuration, invalid, batch, error) && batch == retained);
        const std::array<std::uint32_t, 3> reordered{0U, 1U, 1U};
        transport_require(cache.try_capture(source, configuration, reordered, batch, error) && batch != retained);
        transport_require(batch->identity == retained->identity && batch->glyphs[0].glyph_index == 0U);
        transport_require(cache.try_capture(source, configuration, ids, batch, error) && batch != retained &&
            batch->identity == retained->identity && batch->glyphs == retained->glyphs);
        transport_require(cache.try_capture(source, configuration, {}, batch, error) && batch->glyphs.empty());
        const auto empty = batch;
        transport_require(cache.try_capture(source, configuration, {}, batch, error) && batch == empty);
        transport_require(cache.try_capture(source, configuration, ids, batch, error) && batch->glyphs == retained->glyphs);
        const auto saved = batch;
        const auto live_before_failure = cache.statistics().live_entries;
        auto bad = configuration;
        bad.policy = static_cast<font_hint_policy>(38U);
        transport_require(!cache.try_capture(source, bad, ids, batch, error) && batch == saved);
        const std::array<std::int32_t, 1> axes{0};
        bad = configuration; bad.variation_coordinates_16_16 = axes;
        transport_require(!cache.try_capture(source, bad, ids, batch, error) && batch == saved);
        const auto wrong_face = std::make_shared<const owned_font_source>(source->bytes, 1U);
        transport_require(!cache.try_capture(wrong_face, configuration, ids, batch, error) && batch == saved);
        transport_require(cache.statistics().live_entries == live_before_failure);
        transport_require(!cache.try_capture(nullptr, configuration, ids, batch, error) && batch == saved);
        // A failed cold candidate also must not consume a replacement slot.
        auto cold_fault = configuration; cold_fault.x_phase_26_6 = 3U;
        transport_require(!cache.try_capture(source, cold_fault, faulty, batch, error) && batch == saved &&
            cache.statistics().live_entries == live_before_failure);
        // Every device/policy/phase component owns a distinct native face, while
        // every successful configuration shares the original immutable font copy.
        for (unsigned int component = 0U; component < 5U; ++component) {
            auto changed = configuration;
            if (component == 0U) ++changed.x_pixels_per_em_26_6;
            if (component == 1U) ++changed.y_pixels_per_em_26_6;
            if (component == 2U) changed.x_phase_26_6 = 1U;
            if (component == 3U) changed.y_phase_26_6 = 1U;
            if (component == 4U) changed.policy = policy == font_hint_policy::truetype_35 ?
                font_hint_policy::truetype_40 : font_hint_policy::truetype_35;
            transport_require(cache.try_capture(source, changed, ids, batch, error) && batch->identity != saved->identity &&
                batch->identity->source == source);
        }
        for (std::uint32_t phase = 2U; phase <= 20U; ++phase) {
            auto changed = configuration; changed.x_phase_26_6 = phase;
            transport_require(cache.try_capture(source, changed, ids, batch, error));
            transport_require(batch->identity->source == source && batch->identity->x_phase_26_6 == phase);
            transport_require(cache.statistics().live_entries <= hinted_font_cache::capacity);
        }
        transport_require(cache.statistics().live_entries == hinted_font_cache::capacity);
        const auto faces_before = cache.statistics().face_creations;
        transport_require(cache.try_capture(source, configuration, ids, batch, error) &&
            cache.statistics().face_creations == faces_before + 1U && batch->identity != saved->identity &&
            batch->identity->source == source && batch->glyphs == saved->glyphs);
        tests::verify_hinted_transport(*retained); // evicted generations remain exact
    }
    source.reset(); // cache and original caller references have both ended
    transport_require(retained->identity->source != nullptr && retained->identity->source->face_index == 0U);
    tests::verify_hinted_transport(*retained);
}
} // namespace

int main()
{
    try {
        verify_cache(font_hint_policy::truetype_35);
        verify_cache(font_hint_policy::truetype_40);
        std::cout << "{\"boundedOwnedCache\":true,\"exactGenerationReuse\":true,\"nativeFaultAtomicity\":true}\n";
        return 0;
    } catch (const std::exception& error) {
        std::cerr << error.what() << '\n';
        return 1;
    }
}
