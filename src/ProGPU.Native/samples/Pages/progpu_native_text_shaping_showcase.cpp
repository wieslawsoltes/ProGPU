#include "progpu_native_text_shaping_showcase.hpp"

#include <algorithm>
#include <array>
#include <cmath>
#include <limits>
#include <new>
#include <string_view>
#include <unordered_map>

namespace progpu::native::samples {
namespace {

constexpr std::uint32_t tag(char a, char b, char c, char d) noexcept {
    return (static_cast<std::uint32_t>(static_cast<unsigned char>(a)) << 24U) |
        (static_cast<std::uint32_t>(static_cast<unsigned char>(b)) << 16U) |
        (static_cast<std::uint32_t>(static_cast<unsigned char>(c)) << 8U) |
        static_cast<std::uint32_t>(static_cast<unsigned char>(d));
}

struct feature_preset final {
    std::string_view feature;
    std::string_view name;
    std::string_view text;
    std::uint32_t language = 0U;
    bool disable_composition = false;
};

// Exact feature-wall specimens from TextShapingShowcasePage.CreateFeatureWall.
constexpr std::array<feature_preset, 8U> presets{{
    {"liga", "Standard ligatures", "office affinity"},
    {"kern", "Pair positioning", "AVATAR \xC2\xB7 To Wa Yo"},
    {"frac", "Automatic fractions", "1/2  12/25"},
    {"zero", "Slashed zero", "O0  1002003"},
    {"ss01", "Stylistic set 01", "1234567890"},
    {"calt", "Contextual alternates", "->  -->  <->  =>"},
    {"locl", "Localized Romanian", "\xC5\x9E \xC5\x9F  \xC5\xA2 \xC5\xA3",
        tag('R', 'O', 'M', ' ')},
    {"mkmk", "Mark-to-mark", "a\xCC\x88\xCC\x81  o\xCC\x82\xCC\x81",
        0U, true}
}};

struct shaped_run final {
    std::vector<progpu_native_text_shaping_glyph> glyphs{};
    std::vector<progpu_native_positioned_glyph> positioned{};
    float x = 0.0F;
    float baseline = 0.0F;
    float font_size = 16.0F;
    progpu_native_color color{1.0F, 1.0F, 1.0F, 1.0F};
    float advance_x = 0.0F;
};

bool shape(
    std::span<const std::byte> font_bytes,
    std::string_view utf8,
    std::span<const progpu_native_text_feature> features,
    std::uint32_t language,
    shaped_run& run) {
    const auto input_bytes = std::span<const std::byte>(
        reinterpret_cast<const std::byte*>(utf8.data()), utf8.size());
    text::unicode_decode_requirements decode_requirements{};
    if (!text::try_get_utf8_decode_requirements(
            input_bytes, decode_requirements)) {
        return false;
    }
    std::vector<text::unicode_scalar> decoded(
        decode_requirements.scalar_count);
    std::uint32_t decoded_count = 0U;
    if (!text::try_decode_utf8(
            input_bytes, decoded, decoded_count) ||
        decoded_count != decoded.size()) {
        return false;
    }
    std::vector<progpu_native_text_scalar> input;
    input.reserve(decoded.size());
    for (const auto& scalar : decoded) {
        input.push_back({
            scalar.code_point,
            scalar.input_index,
            scalar.input_length,
            scalar.canonical_combining_class,
            0U,
            scalar.script.value});
    }

    progpu_native_text_shape_request request{};
    request.struct_size = sizeof(request);
    request.abi_version = PROGPU_NATIVE_ABI_VERSION;
    request.font_data = reinterpret_cast<const std::uint8_t*>(
        font_bytes.data());
    request.font_size = font_bytes.size();
    request.flags = PROGPU_NATIVE_TEXT_SHAPE_ZERO_MARK_ADVANCES;
    request.input = input.data();
    request.input_count = static_cast<std::uint32_t>(input.size());
    request.features = features.data();
    request.feature_count = static_cast<std::uint32_t>(features.size());
    request.unicode_script = tag('l', 'a', 't', 'n');
    request.language = language;
    request.direction = PROGPU_NATIVE_TEXT_DIRECTION_LEFT_TO_RIGHT;
    request.cluster_level = PROGPU_NATIVE_TEXT_CLUSTER_MONOTONE_GRAPHEMES;
    request.alternate_value = 1U;

    progpu_native_text_shape_requirements requirements{};
    requirements.struct_size = sizeof(requirements);
    if (progpu_native_text_get_shape_requirements(
            &request, &requirements) != PROGPU_NATIVE_STATUS_SUCCESS ||
        requirements.glyph_capacity == 0U) {
        return false;
    }
    run.glyphs.resize(requirements.glyph_capacity);
    std::vector<std::uint8_t> scratch(
        static_cast<std::size_t>(requirements.scratch_bytes));
    progpu_native_text_shape_result result{};
    result.struct_size = sizeof(result);
    if (progpu_native_text_shape(
            &request,
            run.glyphs.data(),
            static_cast<std::uint32_t>(run.glyphs.size()),
            scratch.data(),
            scratch.size(),
            &result) != PROGPU_NATIVE_STATUS_SUCCESS ||
        result.glyph_count == 0U ||
        result.glyph_count > run.glyphs.size()) {
        return false;
    }
    run.glyphs.resize(result.glyph_count);
    return true;
}

progpu_native_analytic_primitive rectangle(
    float x,
    float y,
    float width,
    float height,
    float radius,
    progpu_native_color color) noexcept {
    return {
        PROGPU_NATIVE_PRIMITIVE_ROUNDED_RECTANGLE,
        0U,
        x,
        y,
        width,
        height,
        radius,
        0.0F,
        color,
        semantic_scene_builder::identity_transform()};
}

} // namespace

text_shaping_showcase_scene::text_shaping_showcase_scene() {
    font_bytes_.reserve(512U * 1024U);
}

bool text_shaping_showcase_scene::load_font(
    std::span<const std::byte> font_bytes) noexcept {
    if (font_bytes.empty()) {
        return false;
    }
    try {
        font_bytes_.assign(font_bytes.begin(), font_bytes.end());
    } catch (...) {
        return false;
    }
    text::font_error error = text::font_error::none;
    text::sfnt_font_view parsed;
    if (!text::sfnt_font_view::try_create(
            font_bytes_, 0U, parsed, &error)) {
        font_bytes_.clear();
        return false;
    }
    font_ = parsed;
    ready_ = true;
    mark_dirty();
    return true;
}

bool text_shaping_showcase_scene::resize(
    float width,
    float height,
    float dpi_scale) noexcept {
    if (!std::isfinite(width) || !std::isfinite(height) ||
        !std::isfinite(dpi_scale) || width <= 0.0F || height <= 0.0F ||
        dpi_scale < 1.0F || dpi_scale > 4.0F) {
        return false;
    }
    if (width_ == width && height_ == height && dpi_scale_ == dpi_scale) {
        return false;
    }
    width_ = width;
    height_ = height;
    dpi_scale_ = dpi_scale;
    mark_dirty();
    return true;
}

bool text_shaping_showcase_scene::set_preset(
    std::uint32_t preset_index) noexcept {
    preset_index = std::min(
        preset_index,
        static_cast<std::uint32_t>(presets.size() - 1U));
    if (preset_index_ == preset_index) {
        return false;
    }
    preset_index_ = preset_index;
    mark_dirty();
    return true;
}

void text_shaping_showcase_scene::invalidate() noexcept {
    mark_dirty();
}

bool text_shaping_showcase_scene::compile(
    std::vector<std::byte>& stream,
    text_shaping_showcase_metrics& metrics) noexcept {
    if (!ready_ || !dirty_) {
        return false;
    }
    try {
        const feature_preset& preset = presets[preset_index_];
        const std::uint32_t feature_tag = tag(
            preset.feature[0], preset.feature[1],
            preset.feature[2], preset.feature[3]);
        std::array<progpu_native_text_feature, 2U> enabled_features{{
            {feature_tag, 1U, 0U, std::numeric_limits<std::uint32_t>::max()},
            {tag('c', 'c', 'm', 'p'), 0U, 0U,
                std::numeric_limits<std::uint32_t>::max()}}};
        std::array<progpu_native_text_feature, 2U> disabled_features{{
            {feature_tag, 0U, 0U, std::numeric_limits<std::uint32_t>::max()},
            {tag('c', 'c', 'm', 'p'), 0U, 0U,
                std::numeric_limits<std::uint32_t>::max()}}};
        const std::size_t feature_count = preset.disable_composition ? 2U : 1U;

        const float compact = std::clamp(width_ / 900.0F, 0.72F, 1.0F);
        const float margin = std::clamp(width_ * 0.045F, 16.0F, 42.0F);
        const float preview_size = std::clamp(width_ / 20.0F, 25.0F, 46.0F);
        const float title_size = 34.0F * compact;
        const float body_size = 14.0F * compact;
        const float card_top = 126.0F * compact;
        const float row_gap = std::clamp(height_ * 0.14F, 62.0F, 92.0F);

        std::vector<shaped_run> runs(10U);
        runs[0U] = {{}, {}, margin, 48.0F * compact, title_size,
            {0.94F, 0.96F, 1.0F, 1.0F}};
        runs[1U] = {{}, {}, margin, 78.0F * compact, body_size,
            {0.57F, 0.62F, 0.73F, 1.0F}};
        runs[2U] = {{}, {}, margin, 105.0F * compact, body_size * 0.86F,
            {0.35F, 0.68F, 1.0F, 1.0F}};
        runs[3U] = {{}, {}, margin + 18.0F, card_top + 35.0F, body_size,
            {0.43F, 0.72F, 1.0F, 1.0F}};
        runs[4U] = {{}, {}, margin + 18.0F, card_top + 68.0F, body_size * 0.8F,
            {0.52F, 0.56F, 0.67F, 1.0F}};
        runs[5U] = {{}, {}, margin + 30.0F, card_top + 106.0F,
            body_size * 0.78F, {0.52F, 0.56F, 0.67F, 1.0F}};
        runs[6U] = {{}, {}, margin + 62.0F, card_top + 68.0F + row_gap,
            preview_size, {0.65F, 0.69F, 0.78F, 1.0F}};
        runs[7U] = {{}, {}, margin + 30.0F, card_top + 106.0F + row_gap,
            body_size * 0.78F, {0.43F, 0.72F, 1.0F, 1.0F}};
        runs[8U] = {{}, {}, margin + 62.0F, card_top + 68.0F + row_gap * 2.0F,
            preview_size, {0.95F, 0.97F, 1.0F, 1.0F}};
        runs[9U] = {{}, {}, margin, height_ - 24.0F, body_size * 0.8F,
            {0.47F, 0.52F, 0.63F, 1.0F}};

        const std::string_view labels[10U]{
            "From Unicode to positioned glyphs.",
            "Native C++ OpenType shaping and retained WebGPU glyph runs",
            "GSUB 1-8  /  GPOS 1-9  /  pooled buffers  /  one retained scene",
            preset.name,
            preset.feature,
            "OFF",
            preset.text,
            "ON",
            preset.text,
            "Glyph IDs, clusters, advances and offsets feed the same rendered run."
        };
        for (std::size_t index = 0U; index < runs.size(); ++index) {
            std::span<const progpu_native_text_feature> features{};
            std::uint32_t language = 0U;
            if (index == 6U) {
                features = std::span(disabled_features).first(feature_count);
                language = preset.language;
            } else if (index == 8U) {
                features = std::span(enabled_features).first(feature_count);
                language = preset.language;
            }
            if (!shape(font_bytes_, labels[index], features, language, runs[index])) {
                return false;
            }
        }

        std::vector<std::uint32_t> glyph_ids;
        for (const auto& run : runs) {
            for (const auto& glyph : run.glyphs) {
                if (glyph.glyph_id != 0U &&
                    glyph.glyph_id <= std::numeric_limits<std::uint16_t>::max()) {
                    glyph_ids.push_back(glyph.glyph_id);
                }
            }
        }
        std::sort(glyph_ids.begin(), glyph_ids.end());
        glyph_ids.erase(
            std::unique(glyph_ids.begin(), glyph_ids.end()), glyph_ids.end());

        text::sfnt_header_metrics header{};
        if (!font_.try_get_header_metrics(header) || header.units_per_em == 0U) {
            return false;
        }
        const float raster_font_size = std::max(56.0F, preview_size);
        const float raster_scale = raster_font_size * dpi_scale_ /
            static_cast<float>(header.units_per_em);
        std::vector<progpu_native_scene_glyph_outline> outlines;
        std::vector<progpu_native_path_segment> segments;
        std::unordered_map<std::uint32_t, std::uint32_t> outline_indices;
        outlines.reserve(glyph_ids.size());
        outline_indices.reserve(glyph_ids.size());
        for (const std::uint32_t glyph_id : glyph_ids) {
            const auto glyph_index = static_cast<std::uint16_t>(glyph_id);
            text::sfnt_glyph_data_view data{};
            text::sfnt_expanded_glyph_requirements requirements{};
            text::font_error error = text::font_error::none;
            if (!font_.try_get_glyph_data(glyph_index, data) || data.empty() ||
                !font_.try_get_expanded_glyph_requirements(
                    glyph_index, requirements, &error) ||
                requirements.path_segment_count == 0U) {
                continue;
            }
            std::vector<std::uint16_t> contour_scratch(
                requirements.simple_contour_scratch_count);
            std::vector<text::sfnt_outline_point> point_scratch(
                requirements.simple_point_scratch_count);
            std::vector<progpu_native_point> points(requirements.point_count);
            const std::size_t segment_offset = segments.size();
            segments.resize(segment_offset + requirements.path_segment_count);
            std::uint32_t points_written = 0U;
            std::uint32_t segments_written = 0U;
            if (!font_.try_decode_glyph_outline(
                    glyph_index,
                    contour_scratch,
                    point_scratch,
                    points,
                    std::span(segments).subspan(
                        segment_offset, requirements.path_segment_count),
                    points_written,
                    segments_written,
                    &error) ||
                segments_written != requirements.path_segment_count) {
                segments.resize(segment_offset);
                return false;
            }
            const std::uint32_t outline_index = static_cast<std::uint32_t>(
                outlines.size());
            outline_indices.emplace(glyph_id, outline_index);
            outlines.push_back({
                segment_offset,
                segments_written,
                static_cast<float>(data.x_min),
                static_cast<float>(data.y_min),
                static_cast<float>(data.x_max),
                static_cast<float>(data.y_max),
                raster_scale,
                0.0F});
        }
        if (outlines.empty() || segments.empty()) {
            return false;
        }

        std::uint32_t total_glyphs = 0U;
        std::uint32_t visible_glyphs = 0U;
        for (auto& run : runs) {
            total_glyphs += static_cast<std::uint32_t>(run.glyphs.size());
            float cursor_x = run.x;
            float cursor_y = run.baseline;
            const float design_scale = run.font_size /
                static_cast<float>(header.units_per_em);
            run.positioned.reserve(run.glyphs.size());
            for (const auto& glyph : run.glyphs) {
                const auto found = outline_indices.find(glyph.glyph_id);
                if (found != outline_indices.end()) {
                    run.positioned.push_back({
                        found->second,
                        0U,
                        {cursor_x + glyph.offset_x * design_scale,
                            cursor_y + glyph.offset_y * design_scale},
                        {1.0F, 0.0F},
                        {0.0F, 1.0F},
                        run.color,
                        run.font_size / raster_font_size,
                        0.0F,
                        0.0F,
                        0.0F});
                    ++visible_glyphs;
                }
                cursor_x += glyph.advance_x * design_scale;
                cursor_y += glyph.advance_y * design_scale;
            }
            run.advance_x = cursor_x - run.x;
        }

        if (!builder_.reset(scene_id_, generation_) ||
            !builder_.reserve(
                static_cast<std::uint32_t>(runs.size() + 1U),
                6U,
                static_cast<std::uint64_t>(segments.size()) *
                    sizeof(progpu_native_path_segment) +
                    visible_glyphs * sizeof(progpu_native_positioned_glyph))) {
            return false;
        }
        constexpr progpu_native_color page{0.075F, 0.078F, 0.105F, 1.0F};
        constexpr progpu_native_color card{0.105F, 0.112F, 0.148F, 1.0F};
        constexpr progpu_native_color off_row{0.125F, 0.132F, 0.17F, 1.0F};
        constexpr progpu_native_color on_row{0.075F, 0.20F, 0.35F, 1.0F};
        const float card_height = std::max(
            230.0F, std::min(height_ - card_top - 48.0F, row_gap * 2.55F));
        const std::array backgrounds{
            rectangle(0.0F, 0.0F, width_, height_, 0.0F, page),
            rectangle(margin, card_top, width_ - margin * 2.0F,
                card_height, 14.0F, card),
            rectangle(margin + 14.0F, card_top + 82.0F,
                width_ - margin * 2.0F - 28.0F, row_gap - 12.0F,
                9.0F, off_row),
            rectangle(margin + 14.0F, card_top + 82.0F + row_gap,
                width_ - margin * 2.0F - 28.0F, row_gap - 12.0F,
                9.0F, on_row)};
        std::array<std::uint32_t, backgrounds.size()> background_brushes{};
        for (std::size_t index = 0U; index < backgrounds.size(); ++index) {
            if (!builder_.add_solid_brush(
                    backgrounds[index].color,
                    1.0F,
                    background_brushes[index])) {
                return false;
            }
        }
        if (!builder_.draw_analytic(
                backgrounds,
                background_brushes,
                {0.0F, 0.0F, width_, height_})) {
            return false;
        }
        std::uint32_t glyph_resource = PROGPU_NATIVE_SCENE_NO_INDEX;
        if (!builder_.add_glyph_outlines(
                outlines, segments, glyph_resource)) {
            return false;
        }
        for (const auto& run : runs) {
            if (!run.positioned.empty() &&
                !builder_.draw_glyph_run(
                    glyph_resource,
                    run.positioned,
                    {0.0F, 0.0F, width_, height_})) {
                return false;
            }
        }

        const std::size_t required = builder_.required_stream_size();
        if (required == 0U) {
            return false;
        }
        if (stream.capacity() < required) {
            stream.reserve(required);
        }
        stream.resize(required);
        std::size_t written = 0U;
        scene_build_metrics build_metrics{};
        if (!builder_.build_into(stream, written, &build_metrics) ||
            written != required) {
            return false;
        }
        metrics.preset_index = preset_index_;
        metrics.shaped_glyph_count = total_glyphs;
        metrics.visible_glyph_count = visible_glyphs;
        metrics.unique_outline_count = static_cast<std::uint32_t>(outlines.size());
        metrics.feature_off_glyph_count = static_cast<std::uint32_t>(
            runs[6U].glyphs.size());
        metrics.feature_on_glyph_count = static_cast<std::uint32_t>(
            runs[8U].glyphs.size());
        metrics.feature_off_advance = runs[6U].advance_x;
        metrics.feature_on_advance = runs[8U].advance_x;
        metrics.command_count = build_metrics.command_count;
        metrics.resource_count = build_metrics.resource_count;
        metrics.stream_bytes = build_metrics.stream_bytes;
        metrics.generation = generation_;
        dirty_ = false;
        return true;
    } catch (const std::bad_alloc&) {
        return false;
    } catch (...) {
        return false;
    }
}

bool text_shaping_showcase_scene::ready() const noexcept {
    return ready_;
}

bool text_shaping_showcase_scene::dirty() const noexcept {
    return dirty_;
}

std::uint64_t text_shaping_showcase_scene::generation() const noexcept {
    return generation_;
}

std::uint32_t text_shaping_showcase_scene::preset_index() const noexcept {
    return preset_index_;
}

std::uint32_t text_shaping_showcase_scene::preset_count() noexcept {
    return static_cast<std::uint32_t>(presets.size());
}

void text_shaping_showcase_scene::mark_dirty() noexcept {
    ++generation_;
    if (generation_ == 0U) {
        generation_ = 1U;
    }
    dirty_ = true;
}

} // namespace progpu::native::samples
