#include "progpu_native_hinted_shaper.hpp"
#include "progpu_native_hinted_run_metrics.hpp"
#include "../Interop/progpu_native_text_font_source.hpp"
#include "../Shaping/progpu_native_open_type_device_shaper_internal.hpp"

#include <algorithm>
#include <array>
#include <limits>
#include <new>
#include <numeric>

namespace progpu::native::text {
namespace {
constexpr std::size_t maximum_glyph_count = 1'048'576U;

template<class T>
bool valid_span(std::span<T> values) noexcept {
    const auto start = reinterpret_cast<std::uintptr_t>(values.data());
    constexpr auto maximum = std::numeric_limits<std::uintptr_t>::max();
    return values.size() <= maximum / sizeof(T) &&
        (values.empty() || (values.data() != nullptr && start % alignof(T) == 0U)) &&
        values.size_bytes() <= maximum - start;
}

bool valid_borrows(std::span<const unicode_scalar> input, const open_type_shape_run_options& options,
    const hinted_font_configuration& configuration) noexcept {
    return valid_span(input) && valid_span(options.normalized_coordinates) &&
        valid_span(configuration.variation_coordinates_16_16) && valid_span(options.requested_features) &&
        valid_span(options.explicit_features) && valid_span(options.feature_settings) &&
        valid_span(options.pre_context) && valid_span(options.post_context);
}

bool try_capacity(const sfnt_font_view& font, const open_type_shape_run_options& options,
    const open_type_shape_run_requirements& needs, std::size_t& capacity, font_error* error) {
    capacity = needs.glyph_capacity;
    if (capacity > maximum_glyph_count) {
        if (error != nullptr) *error = font_error::insufficient_buffer;
        return false;
    }
    if (!detail::device_shape_uses_arabic_stretch(options)) return true;
    sfnt_table_view table{};
    if (!font.try_get_table(open_type_tag::from_chars('G', 'S', 'U', 'B'), table)) return true;
    open_type_layout_table_view gsub{};
    if (!open_type_layout_table_view::try_create(table.bytes, gsub, error)) return false;
    std::vector<std::uint16_t> lookups(needs.gsub_lookup_capacity);
    std::uint32_t count = 0U;
    if (!gsub.try_select_feature_lookups(options.script, options.language,
            open_type_tag::from_chars('s', 't', 'c', 'h'), options.normalized_coordinates,
            lookups, count, error)) return false;
    if (count != 0U) capacity = static_cast<std::size_t>(std::min<std::uint64_t>(
        maximum_glyph_count, static_cast<std::uint64_t>(needs.glyph_capacity) * 257U));
    return true;
}

bool i32(long value, std::int32_t& output) noexcept {
    if (value < std::numeric_limits<std::int32_t>::min() ||
        value > std::numeric_limits<std::int32_t>::max()) return false;
    output = static_cast<std::int32_t>(value);
    return true;
}

struct run_owner final {
    progpu_native_text_context* context = nullptr;
    std::uint32_t font_index = 0U;
    hinted_font_configuration configuration{};
    hinted_projection_policy policy = hinted_projection_policy::automatic;
    std::shared_ptr<const owned_font_source> source{};
    std::shared_ptr<hinted_shaped_run> run{};
    std::vector<std::int32_t> widths{};
    hinted_shape_error failure{};
};

const hinted_glyph* descriptor(const run_owner& owner, std::size_t index,
    std::uint32_t glyph_id) noexcept {
    if (owner.run->batch == nullptr || index >= owner.run->descriptor_indices.size()) return nullptr;
    const auto original = owner.run->descriptor_indices[index];
    if (original >= owner.run->source_descriptor_count || original >= owner.run->batch->glyphs.size()) return nullptr;
    const auto& glyph = owner.run->batch->glyphs[original];
    return glyph.glyph_index == glyph_id ? &glyph : nullptr;
}

bool mark_extents(const void* value, std::size_t index, std::uint32_t glyph_id,
    detail::device_mark_bounds& output, bool& found) noexcept {
    const auto* glyph = descriptor(*static_cast<const run_owner*>(value), index, glyph_id);
    if (glyph == nullptr) return false;
    detail::device_mark_bounds candidate{};
    std::int32_t height = 0;
    if (!i32(glyph->horizontal_bearing_x_26_6, candidate.x_bearing) ||
        !i32(glyph->horizontal_bearing_y_26_6, candidate.y_bearing) ||
        !i32(glyph->width_26_6, candidate.width) || !i32(glyph->height_26_6, height) ||
        height == std::numeric_limits<std::int32_t>::min() || candidate.width < 0 || height < 0 ||
        !i32(glyph->horizontal_advance_26_6, candidate.horizontal_advance_26_6)) return false;
    candidate.negative_height = -height;
    output = candidate;
    found = !glyph->points.empty();
    return true;
}

bool stretch_advance(const void* value, std::size_t index, std::uint32_t glyph_id,
    std::int32_t& output) noexcept {
    const auto* glyph = descriptor(*static_cast<const run_owner*>(value), index, glyph_id);
    return glyph != nullptr && i32(glyph->horizontal_advance_26_6, output);
}

bool space_advances(const void* value, detail::device_space_advance_kind role,
    std::span<const std::uint16_t> ids, std::span<detail::gpos_metric_vector> output) noexcept {
    const auto& owner = *static_cast<const run_owner*>(value);
    const auto& run = *owner.run;
    std::size_t start = 0U, count = 0U;
    switch (role) {
    case detail::device_space_advance_kind::figure:
        start = run.figure_descriptor_start; count = run.figure_descriptor_count; break;
    case detail::device_space_advance_kind::punctuation:
        start = run.punctuation_descriptor_start; count = run.punctuation_descriptor_count; break;
    default: return false;
    }
    if (run.batch == nullptr || count != ids.size() || output.size() < count || count > 1U ||
        start > run.batch->glyphs.size() || count > run.batch->glyphs.size() - start) return false;
    std::array<detail::gpos_metric_vector, 1U> candidate{};
    for (std::size_t index = 0U; index < count; ++index) {
        const auto& glyph = run.batch->glyphs[start + index];
        if (glyph.glyph_index != ids[index] || !i32(glyph.horizontal_advance_26_6, candidate[index].x) ||
            !i32(glyph.vertical_advance_26_6, candidate[index].y)) return false;
    }
    std::copy_n(candidate.begin(), count, output.begin());
    return true;
}

bool append_auxiliary(const sfnt_font_view& font, std::span<const shaping_glyph> glyphs,
    std::uint32_t code_point, std::vector<std::uint32_t>& ids, std::size_t& start,
    std::size_t& count, font_error* error) {
    start = ids.size(); count = 0U;
    if (std::none_of(glyphs.begin(), glyphs.end(), [=](const auto& glyph) { return glyph.code_point == code_point; })) return true;
    std::uint16_t original = 0U, space = 0U;
    if (!font.try_get_glyph_index(code_point, original) || !font.try_get_glyph_index(0x20U, space)) {
        if (error != nullptr) *error = font_error::invalid_face;
        return false;
    }
    if (original != 0U || space == 0U) return true;
    std::uint16_t selected = 0U;
    if (code_point == 0x2007U) {
        for (std::uint32_t digit = 0x30U; digit <= 0x39U; ++digit) {
            if (!font.try_get_glyph_index(digit, selected)) {
                if (error != nullptr) *error = font_error::invalid_face;
                return false;
            }
            if (selected != 0U) break;
        }
    } else if (!font.try_get_glyph_index(0x2EU, selected) ||
        (selected == 0U && !font.try_get_glyph_index(0x2CU, selected))) {
        if (error != nullptr) *error = font_error::invalid_face;
        return false;
    }
    if (selected != 0U) { ids.push_back(selected); count = 1U; }
    return true;
}

bool prepare(void* value, const sfnt_font_view& font, const open_type_shape_run_options& options,
    std::span<shaping_glyph> glyphs, detail::device_shape_run_frame& frame, font_error* error) noexcept {
    auto& owner = *static_cast<run_owner*>(value);
    try {
        auto& run = *owner.run;
        if (run.batch != nullptr || font.data().data() != owner.source->bytes.data() ||
            font.data().size() != owner.source->bytes.size() || font.face_index() != owner.source->face_index ||
            glyphs.size() > run.descriptor_indices.size()) {
            if (error != nullptr) *error = font_error::invalid_argument;
            return false;
        }
        run.source_descriptor_count = glyphs.size();
        std::vector<std::uint32_t> ids;
        ids.reserve(glyphs.size() + 2U);
        for (const auto& glyph : glyphs) ids.push_back(glyph.glyph_id);
        if (!append_auxiliary(font, glyphs, 0x2007U, ids, run.figure_descriptor_start,
                run.figure_descriptor_count, error) ||
            !append_auxiliary(font, glyphs, 0x2008U, ids, run.punctuation_descriptor_start,
                run.punctuation_descriptor_count, error)) return false;
        if (!capture_context_hinted(owner.context, owner.font_index, owner.configuration, ids, run.batch,
                owner.failure.capture)) {
            if (error != nullptr) *error = owner.failure.capture == hinted_font_error::resource_exhausted
                ? font_error::insufficient_buffer : font_error::invalid_face;
            return false;
        }
        if (run.batch->identity == nullptr || run.batch->identity->source != owner.source) {
            if (error != nullptr) *error = font_error::invalid_argument;
            return false;
        }
        const auto initialized = initialize_hinted_run_metrics(*run.batch, font, options.direction,
            glyphs, run.source_descriptor_count, owner.policy, run.normalized_coordinates);
        owner.failure.projection = initialized.error;
        if (initialized.error != hinted_projection_error::none) {
            if (error != nullptr) *error = font_error::invalid_argument;
            return false;
        }
        std::iota(run.descriptor_indices.begin(), run.descriptor_indices.begin() + glyphs.size(), std::uint32_t{0U});
        frame.gpos = initialized.frame;
        frame.spaces = {&owner, &space_advances};
        frame.marks = {&owner, &mark_extents};
        frame.stretch = {&owner, &stretch_advance};
        frame.descriptor_mapping = run.descriptor_indices;
        frame.stretch_widths = owner.widths;
        return true;
    } catch (const std::bad_alloc&) {
        owner.failure.resource_exhausted = true;
        owner.failure.capture = hinted_font_error::resource_exhausted;
        if (error != nullptr) *error = font_error::insufficient_buffer;
    } catch (...) {
        if (error != nullptr) *error = font_error::invalid_face;
    }
    return false;
}

bool capture_positioning(void* value, const sfnt_font_view& font, const open_type_shape_run_options& options,
    std::span<const shaping_glyph> prepared, font_error* error) noexcept {
    auto& owner = *static_cast<run_owner*>(value);
    if (owner.run->positioning != nullptr) { if (error != nullptr) *error = font_error::invalid_argument; return false; }
    return try_capture_hinted_positioning(font, options, prepared, owner.policy, owner.run->positioning, error);
}

bool shape_fragment(void* value, const sfnt_font_view& font, std::span<const unicode_scalar> input,
    const open_type_shape_run_options& options, std::span<shaping_glyph> glyphs,
    open_type_shape_run_scratch scratch, std::uint32_t& count, font_error* error,
    const open_type_shape_plan* plan) noexcept {
    auto& parent = *static_cast<run_owner*>(value);
    try {
        run_owner child;
        child.context = parent.context; child.font_index = parent.font_index;
        child.configuration = parent.configuration; child.policy = parent.policy; child.source = parent.source;
        child.run = std::make_shared<hinted_shaped_run>();
        child.run->direction = options.direction;
        if (!input.empty()) child.run->shaping_input.assign(input.begin(), input.end());
        if (!options.normalized_coordinates.empty()) child.run->normalized_coordinates.assign(
            options.normalized_coordinates.begin(), options.normalized_coordinates.end());
        child.run->descriptor_indices.resize(glyphs.size());
        child.widths.resize(glyphs.size());
        auto child_options = options;
        child_options.normalized_coordinates = child.run->normalized_coordinates;
        const detail::device_shape_run_services services{&child, &prepare, &shape_fragment};
        const bool success = detail::try_shape_device_open_type_run(font, child.run->shaping_input, child_options,
            glyphs, scratch, services, count, error, plan);
        if (!success) {
            parent.failure.capture = child.failure.capture;
            parent.failure.projection = child.failure.projection;
            parent.failure.resource_exhausted = parent.failure.resource_exhausted || child.failure.resource_exhausted;
        }
        return success;
    } catch (const std::bad_alloc&) {
        parent.failure.resource_exhausted = true;
        if (error != nullptr) *error = font_error::insufficient_buffer;
    } catch (...) {
        if (error != nullptr) *error = font_error::invalid_face;
    }
    return false;
}

struct scratch_owner final {
    std::vector<unicode_grapheme_cluster> graphemes;
    std::vector<std::uint16_t> gsub, gpos;
    std::vector<shaping_attachment> attachments;
    std::vector<std::uint8_t> states, categories, syllables;
    std::vector<open_type_arabic_action> actions;
    std::vector<shaping_glyph_flags> flags;
    std::vector<std::uint32_t> indices;
    std::vector<arabic_stretch_run> runs;
    std::vector<unicode_scalar> normalization;
    std::vector<shaping_glyph> verification_glyphs;
    open_type_shape_verification_scratch verification{};

    open_type_shape_run_scratch initialize(const open_type_shape_run_requirements& needs, std::size_t capacity) {
        graphemes.resize(needs.grapheme_capacity); gsub.resize(needs.gsub_lookup_capacity); gpos.resize(needs.gpos_lookup_capacity);
        attachments.resize(capacity); states.resize(capacity);
        actions.resize(needs.script_action_capacity); flags.resize(needs.script_action_capacity);
        categories.resize(needs.complex_script_capacity); syllables.resize(needs.complex_script_capacity);
        indices.resize(needs.complex_script_index_capacity); runs.resize(capacity);
        normalization.resize(needs.normalization_scalar_capacity);
        verification_glyphs.resize(needs.verification_glyph_capacity == 0U ? 0U : capacity);
        verification.glyphs = verification_glyphs;
        open_type_shape_run_scratch result{};
        result.grapheme_clusters = graphemes; result.gsub_lookups = gsub; result.gpos_lookups = gpos;
        result.attachments = attachments; result.attachment_states = states;
        result.arabic_actions = actions; result.arabic_flags = flags;
        result.script_categories = categories; result.script_syllables = syllables; result.script_indices = indices;
        result.arabic_stretch_runs = runs; result.normalization_scalars = normalization;
        result.verification = verification_glyphs.empty() ? nullptr : &verification;
        return result;
    }
};
} // namespace

bool try_shape_context_hinted(progpu_native_text_context* context, std::uint32_t font_index,
    const hinted_font_configuration& configuration, std::span<const unicode_scalar> input,
    const open_type_shape_run_options& options, std::shared_ptr<const hinted_shaped_run>& result,
    hinted_shape_error& error, hinted_projection_policy policy, const open_type_shape_plan* plan, bool retain_positioning) noexcept {
    error = {};
    try {
        if (!valid_borrows(input, options, configuration)) { error.shaping = font_error::invalid_argument; return false; }
        run_owner owner;
        owner.context = context; owner.font_index = font_index; owner.configuration = configuration; owner.policy = policy;
        owner.source = select_context_font_source(context, font_index);
        if (owner.source == nullptr) { error.shaping = font_error::invalid_argument; return false; }
        sfnt_font_view font{};
        if (!sfnt_font_view::try_create(owner.source->bytes, owner.source->face_index, font, &error.shaping)) return false;
        owner.run = std::make_shared<hinted_shaped_run>();
        owner.run->direction = options.direction;
        if (!options.normalized_coordinates.empty()) owner.run->normalized_coordinates.assign(
            options.normalized_coordinates.begin(), options.normalized_coordinates.end());
        auto owned_options = options;
        owned_options.normalized_coordinates = owner.run->normalized_coordinates;
        open_type_shape_run_requirements needs{};
        if (!try_get_open_type_shape_run_requirements(font, input, owned_options, needs, &error.shaping)) return false;
        std::size_t capacity = 0U;
        if (!try_capacity(font, owned_options, needs, capacity, &error.shaping)) return false;
        // Preserve original input/capacity admission before allocating the
        // exact scalar owner; every actual shaping stage uses that owner.
        if (!input.empty()) owner.run->shaping_input.assign(input.begin(), input.end());
        owner.run->glyphs.resize(capacity); owner.run->descriptor_indices.resize(capacity); owner.widths.resize(capacity);
        scratch_owner storage;
        const auto scratch = storage.initialize(needs, capacity);
        const detail::device_shape_run_services services{&owner, &prepare, &shape_fragment, retain_positioning ? &capture_positioning : nullptr};
        std::uint32_t count = 0U;
        const bool success = detail::try_shape_device_open_type_run(font, owner.run->shaping_input, owned_options,
            owner.run->glyphs, scratch, services, count, &owner.failure.shaping, plan);
        error = owner.failure;
        if (!success) return false;
        if (owner.run->batch == nullptr) { error.shaping = font_error::invalid_face; return false; }
        if (count > owner.run->glyphs.size() || count > owner.run->descriptor_indices.size()) {
            error.shaping = font_error::invalid_argument; return false;
        }
        for (std::size_t index = 0U; index < count; ++index)
            if (descriptor(owner, index, owner.run->glyphs[index].glyph_id) == nullptr) {
                error.shaping = font_error::invalid_argument; return false;
            }
        owner.run->glyphs.resize(count); owner.run->descriptor_indices.resize(count);
        if (owner.run->positioning != nullptr && count != 0U) {
            std::shared_ptr<const hinted_positioned_slice> verified;
            if (!try_recompose_hinted_positioning(owner.run, 0U, count, verified, &error.shaping)) return false;
        }
        result = std::move(owner.run);
        error = {};
        return true;
    } catch (const std::bad_alloc&) {
        error.resource_exhausted = true;
        error.shaping = font_error::insufficient_buffer;
    } catch (...) {
        error.shaping = font_error::invalid_face;
    }
    return false;
}
} // namespace progpu::native::text
