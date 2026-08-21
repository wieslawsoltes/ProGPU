#include "../progpu_native_font_bytes.hpp"
#include "progpu_native_text.hpp"

#include <algorithm>
#include <array>
#include <cctype>
#include <cstdlib>
#include <deque>
#include <filesystem>
#include <fstream>
#include <iterator>
#include <limits>
#include <memory>
#include <mutex>
#include <string>
#include <system_error>
#include <unordered_map>
#include <unordered_set>
#include <utility>
#include <vector>

// Direct native port provenance: ProGPU-owned FontApi.ScanSystemFonts,
// SfntFontMetadataReader, and FontManager matching policy at checkpoint
// 497afbb3. The native catalog keeps the same metadata-only discovery and
// lazy-cmap/full-face ownership boundaries without using a platform text API.

namespace progpu::native::text {
namespace {

using detail::read_u16;
using detail::read_u32;

constexpr std::size_t sfnt_header_size = 12U;
constexpr std::size_t table_record_size = 16U;
constexpr std::uint32_t maximum_face_count = 4096U;
constexpr std::uint16_t maximum_table_count = 4096U;
constexpr std::uint64_t maximum_table_size = 64U * 1024U * 1024U;
constexpr std::size_t maximum_character_cache_entries = 4096U;
constexpr auto collection_tag = open_type_tag::from_chars('t', 't', 'c', 'f');
constexpr auto name_tag = open_type_tag::from_chars('n', 'a', 'm', 'e');
constexpr auto os2_tag = open_type_tag::from_chars('O', 'S', '/', '2');
constexpr auto head_tag = open_type_tag::from_chars('h', 'e', 'a', 'd');
constexpr auto cmap_tag = open_type_tag::from_chars('c', 'm', 'a', 'p');

void set_error(font_catalog_error* destination, font_catalog_error value) noexcept {
    if (destination != nullptr) *destination = value;
}

std::uint64_t hash_normalized(std::string_view value) noexcept {
    std::uint64_t result = 1469598103934665603ULL;
    for (const unsigned char byte : value) {
        const auto normalized =
            byte >= 'A' && byte <= 'Z' ? static_cast<unsigned char>(byte + ('a' - 'A')) : byte;
        result ^= normalized;
        result *= 1099511628211ULL;
    }
    return result == 0U ? 1U : result;
}

bool equal_normalized(std::string_view left, std::string_view right) noexcept {
    if (left.size() != right.size()) return false;
    for (std::size_t index = 0U; index < left.size(); ++index) {
        const auto a = static_cast<unsigned char>(left[index]);
        const auto b = static_cast<unsigned char>(right[index]);
        const auto folded_a = a >= 'A' && a <= 'Z' ? a + ('a' - 'A') : a;
        const auto folded_b = b >= 'A' && b <= 'Z' ? b + ('a' - 'A') : b;
        if (folded_a != folded_b) return false;
    }
    return true;
}

bool less_normalized(std::string_view left, std::string_view right) noexcept {
    const auto count = std::min(left.size(), right.size());
    for (std::size_t index = 0U; index < count; ++index) {
        const auto a = static_cast<unsigned char>(left[index]);
        const auto b = static_cast<unsigned char>(right[index]);
        const auto folded_a = a >= 'A' && a <= 'Z' ? a + ('a' - 'A') : a;
        const auto folded_b = b >= 'A' && b <= 'Z' ? b + ('a' - 'A') : b;
        if (folded_a != folded_b) return folded_a < folded_b;
    }
    return left.size() < right.size();
}

std::uint64_t hash_face_identity(std::string_view path, std::uint32_t face_index) noexcept {
    auto result = hash_normalized(path);
    for (unsigned shift = 0U; shift < 32U; shift += 8U) {
        result ^= static_cast<std::uint8_t>(face_index >> shift);
        result *= 1099511628211ULL;
    }
    return result == 0U ? 1U : result;
}

std::size_t align4(std::size_t value) noexcept { return (value + 3U) & ~std::size_t{3U}; }

void write_u16(std::span<std::byte> data, std::size_t offset, std::uint16_t value) noexcept {
    data[offset] = static_cast<std::byte>(value >> 8U);
    data[offset + 1U] = static_cast<std::byte>(value);
}

void write_u32(std::span<std::byte> data, std::size_t offset, std::uint32_t value) noexcept {
    data[offset] = static_cast<std::byte>(value >> 24U);
    data[offset + 1U] = static_cast<std::byte>(value >> 16U);
    data[offset + 2U] = static_cast<std::byte>(value >> 8U);
    data[offset + 3U] = static_cast<std::byte>(value);
}

class file_reader final {
  public:
    explicit file_reader(const std::filesystem::path& path, std::uint64_t* bytes_read = nullptr)
        : stream_(path, std::ios::binary), bytes_read_(bytes_read) {
        if (!stream_) return;
        stream_.seekg(0, std::ios::end);
        const auto end = stream_.tellg();
        if (end < 0) {
            stream_.setstate(std::ios::failbit);
            return;
        }
        size_ = static_cast<std::uint64_t>(end);
    }

    bool valid() const noexcept { return stream_.is_open() && !stream_.fail(); }
    std::uint64_t size() const noexcept { return size_; }

    bool read(std::uint64_t offset, std::span<std::byte> output) noexcept {
        if (offset > size_ || output.size() > size_ - offset ||
            offset > static_cast<std::uint64_t>(std::numeric_limits<std::streamoff>::max()) ||
            output.size() > static_cast<std::size_t>(std::numeric_limits<std::streamsize>::max())) {
            return false;
        }
        stream_.clear();
        stream_.seekg(static_cast<std::streamoff>(offset), std::ios::beg);
        if (!stream_) return false;
        stream_.read(reinterpret_cast<char*>(output.data()),
                     static_cast<std::streamsize>(output.size()));
        if (stream_.gcount() != static_cast<std::streamsize>(output.size())) {
            return false;
        }
        if (bytes_read_ != nullptr) *bytes_read_ += output.size();
        return true;
    }

  private:
    std::ifstream stream_{};
    std::uint64_t size_ = 0U;
    std::uint64_t* bytes_read_ = nullptr;
};

struct source_table final {
    open_type_tag tag{};
    std::uint32_t offset = 0U;
    std::uint32_t length = 0U;
};

struct table_payload final {
    open_type_tag tag{};
    std::vector<std::byte> bytes{};
};

bool read_face_offsets(file_reader& reader, std::vector<std::uint32_t>& offsets) {
    offsets.clear();
    std::array<std::byte, sfnt_header_size> header{};
    if (!reader.read(0U, header)) return false;
    if (read_u32(header, 0U) != collection_tag.value) {
        try {
            offsets.push_back(0U);
            return true;
        } catch (const std::bad_alloc&) {
            throw;
        } catch (...) {
            return false;
        }
    }
    const auto count = read_u32(header, 8U);
    if (count == 0U || count > maximum_face_count ||
        12U + static_cast<std::uint64_t>(count) * 4U > reader.size()) {
        return false;
    }
    try {
        std::vector<std::byte> bytes(static_cast<std::size_t>(count) * 4U);
        if (!reader.read(12U, bytes)) return false;
        offsets.resize(count);
        for (std::uint32_t index = 0U; index < count; ++index) {
            offsets[index] = read_u32(bytes, static_cast<std::size_t>(index) * 4U);
        }
        return true;
    } catch (const std::bad_alloc&) {
        throw;
    } catch (...) {
        offsets.clear();
        return false;
    }
}

bool read_face_directory(file_reader& reader, std::uint32_t face_offset,
                         std::uint32_t& sfnt_version, std::vector<source_table>& tables) {
    tables.clear();
    std::array<std::byte, sfnt_header_size> header{};
    if (!reader.read(face_offset, header)) return false;
    sfnt_version = read_u32(header, 0U);
    const auto count = read_u16(header, 4U);
    if (count == 0U || count > maximum_table_count ||
        static_cast<std::uint64_t>(face_offset) + sfnt_header_size +
                static_cast<std::uint64_t>(count) * table_record_size >
            reader.size()) {
        return false;
    }
    try {
        std::vector<std::byte> directory(static_cast<std::size_t>(count) * table_record_size);
        if (!reader.read(static_cast<std::uint64_t>(face_offset) + sfnt_header_size, directory)) {
            return false;
        }
        tables.reserve(count);
        for (std::uint16_t index = 0U; index < count; ++index) {
            const auto record = static_cast<std::size_t>(index) * table_record_size;
            const auto offset = read_u32(directory, record + 8U);
            const auto length = read_u32(directory, record + 12U);
            if (offset > reader.size() || length > reader.size() - offset) {
                return false;
            }
            tables.push_back(
                source_table{open_type_tag{read_u32(directory, record)}, offset, length});
        }
        return true;
    } catch (const std::bad_alloc&) {
        throw;
    } catch (...) {
        tables.clear();
        return false;
    }
}

bool read_selected_tables(file_reader& reader, std::span<const source_table> directory,
                          std::span<const open_type_tag> selected,
                          std::vector<table_payload>& tables) {
    tables.clear();
    try {
        for (const auto tag : selected) {
            const auto found =
                std::find_if(directory.begin(), directory.end(),
                             [tag](const source_table& value) { return value.tag == tag; });
            if (found == directory.end() || found->length == 0U) continue;
            if (found->length > maximum_table_size) return false;
            table_payload payload{tag,
                                  std::vector<std::byte>(static_cast<std::size_t>(found->length))};
            if (!reader.read(found->offset, payload.bytes)) return false;
            tables.push_back(std::move(payload));
        }
        return true;
    } catch (const std::bad_alloc&) {
        throw;
    } catch (...) {
        tables.clear();
        return false;
    }
}

bool build_compact_face(std::uint32_t sfnt_version, std::vector<table_payload> tables,
                        std::vector<std::byte>& output) {
    output.clear();
    if (tables.empty() || tables.size() > maximum_table_count) return false;
    std::sort(tables.begin(), tables.end(),
              [](const auto& left, const auto& right) { return left.tag.value < right.tag.value; });
    try {
        const auto directory_size = sfnt_header_size + tables.size() * table_record_size;
        auto size = align4(directory_size);
        for (const auto& table : tables) {
            if (table.bytes.size() > std::numeric_limits<std::uint32_t>::max() ||
                size > std::numeric_limits<std::uint32_t>::max() - table.bytes.size()) {
                return false;
            }
            size = align4(size + table.bytes.size());
        }
        output.assign(size, std::byte{});
        write_u32(output, 0U, sfnt_version);
        write_u16(output, 4U, static_cast<std::uint16_t>(tables.size()));
        std::uint16_t maximum_power = 1U;
        std::uint16_t selector = 0U;
        while (maximum_power <= tables.size() / 2U) {
            maximum_power = static_cast<std::uint16_t>(maximum_power * 2U);
            ++selector;
        }
        write_u16(output, 6U, static_cast<std::uint16_t>(maximum_power * table_record_size));
        write_u16(output, 8U, selector);
        write_u16(output, 10U,
                  static_cast<std::uint16_t>(tables.size() * table_record_size -
                                             maximum_power * table_record_size));
        auto target = align4(directory_size);
        for (std::size_t index = 0U; index < tables.size(); ++index) {
            const auto record = sfnt_header_size + index * table_record_size;
            write_u32(output, record, tables[index].tag.value);
            write_u32(output, record + 8U, static_cast<std::uint32_t>(target));
            write_u32(output, record + 12U, static_cast<std::uint32_t>(tables[index].bytes.size()));
            std::copy(tables[index].bytes.begin(), tables[index].bytes.end(),
                      output.begin() + static_cast<std::ptrdiff_t>(target));
            target = align4(target + tables[index].bytes.size());
        }
        return true;
    } catch (const std::bad_alloc&) {
        throw;
    } catch (...) {
        output.clear();
        return false;
    }
}

bool decode_name(const sfnt_font_view& font, std::uint16_t name_id, std::string& result) {
    result.clear();
    sfnt_name_requirements requirements{};
    if (!font.try_get_name_requirements(name_id, requirements) || requirements.utf8_bytes == 0U) {
        return false;
    }
    try {
        result.resize(requirements.utf8_bytes);
    } catch (const std::bad_alloc&) {
        throw;
    } catch (...) {
        return false;
    }
    std::size_t written = 0U;
    if (!font.try_decode_name(name_id, result, written) || written != requirements.utf8_bytes) {
        result.clear();
        return false;
    }
    return true;
}

bool has_font_extension(const std::filesystem::path& path) {
    auto extension = path.extension().string();
    std::transform(extension.begin(), extension.end(), extension.begin(),
                   [](unsigned char value) { return static_cast<char>(std::tolower(value)); });
    return extension == ".ttf" || extension == ".ttc" || extension == ".otf";
}

font_style_request normalize_style(font_style_request value) noexcept {
    if (value.weight <= 0) value.weight = 400;
    value.weight = std::clamp(value.weight, 1, 1000);
    if (value.width <= 0) value.width = 5;
    value.width = std::clamp(value.width, 1, 9);
    if (static_cast<std::uint8_t>(value.slant) >
        static_cast<std::uint8_t>(font_provider_slant::oblique)) {
        value.slant = font_provider_slant::normal;
    }
    return value;
}

std::uint32_t style_distance(const font_catalog_face_info& actual,
                             font_style_request requested) noexcept {
    const bool actual_italic = actual.slant != font_provider_slant::normal;
    const bool requested_italic = requested.slant != font_provider_slant::normal;
    const auto slant = actual_italic == requested_italic ? 0U : 10000U;
    const auto width =
        static_cast<std::uint32_t>(std::abs(static_cast<int>(actual.width) - requested.width)) *
        1000U;
    const auto weight =
        static_cast<std::uint32_t>(std::abs(static_cast<int>(actual.weight) - requested.weight));
    return slant + width + weight;
}

#if defined(__APPLE__) || defined(_WIN32) || defined(__ANDROID__) || defined(__linux__)
void add_if_missing(std::vector<std::string>& values, std::string value) {
    if (value.empty()) return;
    if (std::find(values.begin(), values.end(), value) == values.end()) {
        values.push_back(std::move(value));
    }
}
#endif

#if defined(__APPLE__) || defined(_WIN32) || (defined(__linux__) && !defined(__ANDROID__))
void add_environment_path(std::vector<std::string>& values, const char* variable,
                          std::string_view suffix) {
    std::string root;
#if defined(_WIN32)
    char* environment_value = nullptr;
    std::size_t environment_length = 0U;
    if (_dupenv_s(&environment_value, &environment_length, variable) != 0) return;
    const std::unique_ptr<char, decltype(&std::free)> owner(environment_value, &std::free);
    if (environment_value == nullptr || *environment_value == '\0') return;
    root = environment_value;
#else
    const auto* environment_value = std::getenv(variable);
    if (environment_value == nullptr || *environment_value == '\0') return;
    root = environment_value;
#endif
    auto path = std::filesystem::path(root);
    if (!suffix.empty()) path /= suffix;
    add_if_missing(values, path.string());
}
#endif

} // namespace

class system_font_catalog::implementation final {
  public:
    struct entry final {
        std::string full_name{};
        std::string family_name{};
        std::string file_path{};
        std::uint64_t identity = 0U;
        std::uint64_t family_identity = 0U;
        std::uint32_t face_index = 0U;
        std::uint16_t weight = 400U;
        std::uint8_t width = 5U;
        font_provider_slant slant = font_provider_slant::normal;
        mutable bool cmap_attempted = false;
        mutable std::shared_ptr<const std::vector<std::byte>> cmap_face{};

        font_catalog_face_info info() const noexcept {
            return font_catalog_face_info{full_name, family_name,     file_path,
                                          identity,  family_identity, face_index,
                                          weight,    width,           slant};
        }
    };

    struct character_key final {
        std::uint64_t family = 0U;
        std::uint64_t excluded = 0U;
        std::uint32_t code_point = 0U;
        std::uint16_t weight = 0U;
        std::uint8_t width = 0U;
        font_provider_slant slant = font_provider_slant::normal;

        friend bool operator==(character_key, character_key) noexcept = default;
    };

    struct character_key_hash final {
        std::size_t operator()(character_key value) const noexcept {
            auto result = static_cast<std::size_t>(value.family);
            result ^= static_cast<std::size_t>(value.excluded) + 0x9e3779b9U + (result << 6U) +
                      (result >> 2U);
            result ^= static_cast<std::size_t>(value.code_point) + 0x9e3779b9U + (result << 6U) +
                      (result >> 2U);
            result ^= static_cast<std::size_t>(value.weight) << 9U;
            result ^= static_cast<std::size_t>(value.width) << 3U;
            result ^= static_cast<std::size_t>(value.slant);
            return result;
        }
    };

    struct character_result final {
        std::uint32_t catalog_index = 0U;
        std::uint16_t glyph_index = 0U;
        bool found = false;
    };

    std::vector<entry> entries{};
    mutable std::mutex cache_mutex{};
    mutable std::unordered_map<std::string, std::weak_ptr<const std::vector<std::byte>>>
        loaded_files{};
    mutable std::unordered_map<character_key, character_result, character_key_hash>
        character_cache{};
    mutable std::deque<character_key> character_order{};

    static bool read_font_entries(const std::filesystem::path& path,
                                  font_catalog_scan_metrics& metrics, std::vector<entry>& output,
                                  font_catalog_error& error) noexcept;

    bool try_glyph(std::uint32_t index, std::uint32_t code_point, std::uint16_t& glyph,
                   font_catalog_error& error) const noexcept {
        glyph = 0U;
        error = font_catalog_error::none;
        if (index >= entries.size()) return false;
        std::lock_guard lock(cache_mutex);
        auto& value = entries[index];
        if (!value.cmap_attempted) {
            value.cmap_attempted = true;
            try {
                file_reader reader(value.file_path);
                std::vector<std::uint32_t> offsets;
                std::vector<source_table> directory;
                std::vector<table_payload> tables;
                std::uint32_t version = 0U;
                auto compact = std::make_shared<std::vector<std::byte>>();
                if (reader.valid() && read_face_offsets(reader, offsets) &&
                    value.face_index < offsets.size() &&
                    read_face_directory(reader, offsets[value.face_index], version, directory) &&
                    read_selected_tables(reader, directory, std::array{cmap_tag}, tables) &&
                    build_compact_face(version, std::move(tables), *compact)) {
                    sfnt_font_view font{};
                    if (sfnt_font_view::try_create(*compact, 0U, font)) {
                        value.cmap_face = std::move(compact);
                    }
                }
            } catch (const std::bad_alloc&) {
                error = font_catalog_error::out_of_memory;
                value.cmap_face.reset();
            } catch (...) {
                value.cmap_face.reset();
            }
        }
        if (!value.cmap_face) return false;
        sfnt_font_view font{};
        return sfnt_font_view::try_create(*value.cmap_face, 0U, font) &&
               font.try_get_glyph_index(code_point, glyph) && glyph != 0U;
    }
};

namespace {

template <typename Entry>
bool entry_matches_family(const Entry& value, std::string_view family) noexcept {
    return equal_normalized(value.family_name, family) || equal_normalized(value.full_name, family);
}

} // namespace

bool system_font_catalog::implementation::read_font_entries(const std::filesystem::path& path,
                                                            font_catalog_scan_metrics& metrics,
                                                            std::vector<entry>& output,
                                                            font_catalog_error& error) noexcept {
    error = font_catalog_error::none;
    try {
        std::error_code absolute_error;
        const auto absolute_path =
            std::filesystem::absolute(path, absolute_error).lexically_normal();
        if (absolute_error) return false;
        file_reader reader(absolute_path, &metrics.bytes_read);
        if (!reader.valid()) return false;
        std::vector<std::uint32_t> offsets;
        if (!read_face_offsets(reader, offsets)) return false;
        const auto file_path = absolute_path.string();
        const auto fallback_name = absolute_path.stem().string();
        std::vector<entry> parsed;
        parsed.reserve(offsets.size());
        for (std::uint32_t face_index = 0U; face_index < offsets.size(); ++face_index) {
            std::uint32_t version = 0U;
            std::vector<source_table> directory;
            std::vector<table_payload> tables;
            std::vector<std::byte> compact;
            if (!read_face_directory(reader, offsets[face_index], version, directory) ||
                !read_selected_tables(reader, directory, std::array{name_tag, os2_tag, head_tag},
                                      tables) ||
                !build_compact_face(version, std::move(tables), compact)) {
                return false;
            }
            sfnt_font_view font{};
            if (!sfnt_font_view::try_create(compact, 0U, font)) return false;
            std::string family;
            if (!decode_name(font, sfnt_name_ids::preferred_family_name, family) &&
                !decode_name(font, sfnt_name_ids::family_name, family)) {
                family = fallback_name;
            }
            std::string full;
            if (!decode_name(font, sfnt_name_ids::full_name, full)) full = family;
            sfnt_face_style style{};
            font.try_get_face_style(style);
            const auto slant =
                style.italic ? font_provider_slant::italic : font_provider_slant::normal;
            entry value{};
            value.full_name = std::move(full);
            value.family_name = std::move(family);
            value.file_path = file_path;
            value.identity = hash_face_identity(file_path, face_index);
            parsed.push_back(std::move(value));
            auto& added = parsed.back();
            added.family_identity = hash_normalized(added.family_name);
            added.face_index = face_index;
            added.weight = style.weight;
            added.width = static_cast<std::uint8_t>(style.width);
            added.slant = slant;
        }
        output.insert(output.end(), std::make_move_iterator(parsed.begin()),
                      std::make_move_iterator(parsed.end()));
        return true;
    } catch (const std::bad_alloc&) {
        error = font_catalog_error::out_of_memory;
        return false;
    } catch (...) {
        return false;
    }
}

bool loaded_font_face::valid() const noexcept {
    return storage_ != nullptr && !storage_->empty() && !font_.data().empty();
}

std::span<const std::byte> loaded_font_face::data() const noexcept {
    return storage_ == nullptr ? std::span<const std::byte>{} : *storage_;
}

const sfnt_font_view& loaded_font_face::font() const noexcept { return font_; }

std::uint32_t loaded_font_face::catalog_index() const noexcept { return catalog_index_; }

std::uint64_t loaded_font_face::identity() const noexcept { return identity_; }

system_font_catalog::system_font_catalog() : implementation_(std::make_unique<implementation>()) {}

system_font_catalog::~system_font_catalog() = default;
system_font_catalog::system_font_catalog(system_font_catalog&&) noexcept = default;
system_font_catalog& system_font_catalog::operator=(system_font_catalog&&) noexcept = default;

std::vector<std::string> system_font_catalog::system_font_directories() {
    std::vector<std::string> result;
#if defined(__APPLE__)
    add_if_missing(result, "/System/Library/Fonts");
    add_if_missing(result, "/System/Library/Fonts/Supplemental");
    add_if_missing(result, "/Library/Fonts");
    add_environment_path(result, "HOME", "Library/Fonts");
#elif defined(_WIN32)
    add_environment_path(result, "WINDIR", "Fonts");
    add_environment_path(result, "LOCALAPPDATA", "Microsoft/Windows/Fonts");
#elif defined(__ANDROID__)
    add_if_missing(result, "/system/fonts");
    add_if_missing(result, "/product/fonts");
    add_if_missing(result, "/vendor/fonts");
#elif defined(__linux__)
    add_if_missing(result, "/usr/share/fonts");
    add_if_missing(result, "/usr/local/share/fonts");
    add_environment_path(result, "HOME", ".fonts");
    add_environment_path(result, "HOME", ".local/share/fonts");
    add_environment_path(result, "XDG_DATA_HOME", "fonts");
#endif
    return result;
}

bool system_font_catalog::try_discover_system_fonts(font_catalog_scan_metrics* metrics,
                                                    font_catalog_error* error) noexcept {
    try {
        const auto owned = system_font_directories();
        std::vector<std::string_view> views;
        views.reserve(owned.size());
        for (const auto& value : owned)
            views.push_back(value);
        return try_discover_fonts(views, metrics, error);
    } catch (...) {
        if (metrics != nullptr) *metrics = {};
        set_error(error, font_catalog_error::out_of_memory);
        return false;
    }
}

bool system_font_catalog::try_discover_fonts(std::span<const std::string_view> directories,
                                             font_catalog_scan_metrics* metrics,
                                             font_catalog_error* error) noexcept {
    if (metrics != nullptr) *metrics = {};
    set_error(error, font_catalog_error::none);
    if (implementation_ == nullptr) {
        set_error(error, font_catalog_error::invalid_argument);
        return false;
    }
    font_catalog_scan_metrics measured{};
    try {
        std::vector<implementation::entry> discovered;
        std::unordered_set<std::string> visited;
        for (const auto directory_value : directories) {
            if (directory_value.empty()) continue;
            const std::filesystem::path directory{directory_value};
            std::error_code status_error;
            if (!std::filesystem::is_directory(directory, status_error)) continue;
            ++measured.directory_count;
            std::error_code iterator_error;
            std::filesystem::recursive_directory_iterator iterator(
                directory, std::filesystem::directory_options::skip_permission_denied,
                iterator_error);
            const std::filesystem::recursive_directory_iterator end;
            while (!iterator_error && iterator != end) {
                std::error_code entry_error;
                const auto regular = iterator->is_regular_file(entry_error);
                if (!entry_error && regular && has_font_extension(iterator->path())) {
                    std::error_code absolute_error;
                    auto path = std::filesystem::absolute(iterator->path(), absolute_error)
                                    .lexically_normal()
                                    .string();
                    if (absolute_error) {
                        ++measured.skipped_file_count;
                        iterator.increment(iterator_error);
                        continue;
                    }
#if defined(__APPLE__) || defined(_WIN32)
                    std::transform(path.begin(), path.end(), path.begin(), [](unsigned char value) {
                        return value >= 'A' && value <= 'Z' ? static_cast<char>(value + ('a' - 'A'))
                                                            : static_cast<char>(value);
                    });
#endif
                    if (visited.insert(path).second) {
                        ++measured.file_count;
                        font_catalog_error file_error{};
                        if (!implementation::read_font_entries(iterator->path(), measured,
                                                               discovered, file_error)) {
                            if (file_error == font_catalog_error::out_of_memory) {
                                set_error(error, file_error);
                                if (metrics != nullptr) *metrics = measured;
                                return false;
                            }
                            ++measured.skipped_file_count;
                        }
                    }
                }
                iterator.increment(iterator_error);
                if (iterator_error == std::errc::permission_denied) {
                    iterator_error.clear();
                }
            }
        }
        std::sort(discovered.begin(), discovered.end(), [](const auto& left, const auto& right) {
            if (less_normalized(left.full_name, right.full_name)) return true;
            if (less_normalized(right.full_name, left.full_name)) return false;
            if (left.file_path != right.file_path) {
                return left.file_path < right.file_path;
            }
            return left.face_index < right.face_index;
        });
        if (discovered.size() > std::numeric_limits<std::uint32_t>::max()) {
            set_error(error, font_catalog_error::out_of_memory);
            if (metrics != nullptr) *metrics = measured;
            return false;
        }
        measured.face_count = static_cast<std::uint32_t>(discovered.size());
        {
            std::lock_guard lock(implementation_->cache_mutex);
            implementation_->entries = std::move(discovered);
            implementation_->loaded_files.clear();
            implementation_->character_cache.clear();
            implementation_->character_order.clear();
        }
        if (metrics != nullptr) *metrics = measured;
        return true;
    } catch (const std::bad_alloc&) {
        set_error(error, font_catalog_error::out_of_memory);
    } catch (...) {
        set_error(error, font_catalog_error::filesystem_error);
    }
    if (metrics != nullptr) *metrics = measured;
    return false;
}

std::uint32_t system_font_catalog::face_count() const noexcept {
    if (implementation_ == nullptr ||
        implementation_->entries.size() > std::numeric_limits<std::uint32_t>::max()) {
        return 0U;
    }
    return static_cast<std::uint32_t>(implementation_->entries.size());
}

bool system_font_catalog::try_get_face_info(std::uint32_t catalog_index,
                                            font_catalog_face_info& result) const noexcept {
    result = {};
    if (implementation_ == nullptr || catalog_index >= implementation_->entries.size()) {
        return false;
    }
    result = implementation_->entries[catalog_index].info();
    return true;
}

bool system_font_catalog::try_match_family(std::string_view family_name, font_style_request style,
                                           std::uint32_t& catalog_index,
                                           font_catalog_error* error) const noexcept {
    catalog_index = 0U;
    set_error(error, font_catalog_error::none);
    if (implementation_ == nullptr || family_name.empty()) {
        set_error(error, font_catalog_error::invalid_argument);
        return false;
    }
    style = normalize_style(style);
    auto best_distance = std::numeric_limits<std::uint32_t>::max();
    bool found = false;
    for (std::uint32_t index = 0U; index < implementation_->entries.size(); ++index) {
        const auto& value = implementation_->entries[index];
        if (!entry_matches_family(value, family_name)) continue;
        const auto distance = style_distance(value.info(), style);
        if (!found || distance < best_distance) {
            found = true;
            best_distance = distance;
            catalog_index = index;
        }
    }
    return found;
}

bool system_font_catalog::try_match_character(
    std::string_view family_name, font_style_request style,
    std::span<const std::string_view> language_tags, std::uint32_t code_point,
    std::uint64_t excluded_identity, std::uint32_t& catalog_index, std::uint16_t& glyph_index,
    font_catalog_error* error) const noexcept {
    catalog_index = 0U;
    glyph_index = 0U;
    set_error(error, font_catalog_error::none);
    if (implementation_ == nullptr || code_point > 0x10FFFFU) {
        set_error(error, font_catalog_error::invalid_argument);
        return false;
    }
    style = normalize_style(style);
    const implementation::character_key key{family_name.empty() ? 0U : hash_normalized(family_name),
                                            excluded_identity,
                                            code_point,
                                            static_cast<std::uint16_t>(style.weight),
                                            static_cast<std::uint8_t>(style.width),
                                            style.slant};
    if (language_tags.empty()) {
        std::lock_guard lock(implementation_->cache_mutex);
        const auto cached = implementation_->character_cache.find(key);
        if (cached != implementation_->character_cache.end()) {
            catalog_index = cached->second.catalog_index;
            glyph_index = cached->second.glyph_index;
            return cached->second.found;
        }
    }

    std::vector<std::uint8_t> visited;
    try {
        visited.assign(implementation_->entries.size(), 0U);
    } catch (...) {
        set_error(error, font_catalog_error::out_of_memory);
        return false;
    }
    bool fatal_allocation = false;

    const auto try_family = [&](std::string_view family) {
        auto best_distance = std::numeric_limits<std::uint32_t>::max();
        std::uint32_t best = 0U;
        std::uint16_t best_glyph = 0U;
        bool found = false;
        for (std::uint32_t index = 0U; index < implementation_->entries.size(); ++index) {
            const auto& value = implementation_->entries[index];
            if (visited[index] != 0U || value.identity == excluded_identity ||
                !entry_matches_family(value, family)) {
                continue;
            }
            std::uint16_t glyph = 0U;
            font_catalog_error glyph_error{};
            if (!implementation_->try_glyph(index, code_point, glyph, glyph_error)) {
                if (glyph_error == font_catalog_error::out_of_memory) fatal_allocation = true;
                continue;
            }
            const auto distance = style_distance(value.info(), style);
            if (!found || distance < best_distance) {
                found = true;
                best_distance = distance;
                best = index;
                best_glyph = glyph;
            }
        }
        if (!found) return false;
        visited[best] = 1U;
        catalog_index = best;
        glyph_index = best_glyph;
        return true;
    };

    bool found = !family_name.empty() && try_family(family_name);
    if (fatal_allocation) {
        set_error(error, font_catalog_error::out_of_memory);
        return false;
    }
    if (!found) {
        std::uint32_t count = 0U;
        font_error preference_error{};
        if (try_get_font_fallback_family_preference_count(language_tags, code_point, count,
                                                          &preference_error) &&
            count != 0U) {
            try {
                std::vector<std::string_view> preferences(count);
                std::uint32_t written = 0U;
                if (try_get_font_fallback_family_preferences(language_tags, code_point, preferences,
                                                             written, &preference_error)) {
                    for (std::uint32_t index = 0U; index < written && !found; ++index) {
                        found = try_family(preferences[index]);
                        if (fatal_allocation) {
                            set_error(error, font_catalog_error::out_of_memory);
                            return false;
                        }
                    }
                }
            } catch (...) {
                set_error(error, font_catalog_error::out_of_memory);
                return false;
            }
        }
    }
    for (unsigned pass = 0U; pass < 2U && !found; ++pass) {
        for (std::uint32_t index = 0U; index < implementation_->entries.size() && !found; ++index) {
            const auto& value = implementation_->entries[index];
            if (visited[index] != 0U || value.identity == excluded_identity ||
                (pass == 0U && style_distance(value.info(), style) != 0U)) {
                continue;
            }
            visited[index] = 1U;
            std::uint16_t glyph = 0U;
            font_catalog_error glyph_error{};
            if (implementation_->try_glyph(index, code_point, glyph, glyph_error)) {
                catalog_index = index;
                glyph_index = glyph;
                found = true;
            } else if (glyph_error == font_catalog_error::out_of_memory) {
                set_error(error, glyph_error);
                return false;
            }
        }
    }

    if (language_tags.empty()) {
        try {
            std::lock_guard lock(implementation_->cache_mutex);
            if (implementation_->character_cache
                    .emplace(key,
                             implementation::character_result{catalog_index, glyph_index, found})
                    .second) {
                implementation_->character_order.push_back(key);
            }
            while (implementation_->character_cache.size() > maximum_character_cache_entries &&
                   !implementation_->character_order.empty()) {
                implementation_->character_cache.erase(implementation_->character_order.front());
                implementation_->character_order.pop_front();
            }
        } catch (...) {
            // Matching succeeded without the optional bounded cache.
        }
    }
    return found;
}

bool system_font_catalog::try_load_face(std::uint32_t catalog_index, loaded_font_face& result,
                                        font_catalog_error* error) const noexcept {
    result = {};
    set_error(error, font_catalog_error::none);
    if (implementation_ == nullptr || catalog_index >= implementation_->entries.size()) {
        set_error(error, font_catalog_error::invalid_argument);
        return false;
    }
    const auto& entry = implementation_->entries[catalog_index];
    std::shared_ptr<const std::vector<std::byte>> storage;
    {
        std::lock_guard lock(implementation_->cache_mutex);
        const auto cached = implementation_->loaded_files.find(entry.file_path);
        if (cached != implementation_->loaded_files.end()) {
            storage = cached->second.lock();
        }
    }
    if (!storage) {
        try {
            file_reader reader(entry.file_path);
            if (!reader.valid() || reader.size() > std::numeric_limits<std::size_t>::max()) {
                set_error(error, font_catalog_error::filesystem_error);
                return false;
            }
            auto loaded =
                std::make_shared<std::vector<std::byte>>(static_cast<std::size_t>(reader.size()));
            if (!reader.read(0U, *loaded)) {
                set_error(error, font_catalog_error::filesystem_error);
                return false;
            }
            storage = std::move(loaded);
            std::lock_guard lock(implementation_->cache_mutex);
            auto& slot = implementation_->loaded_files[entry.file_path];
            if (auto concurrent = slot.lock())
                storage = std::move(concurrent);
            else
                slot = storage;
        } catch (const std::bad_alloc&) {
            set_error(error, font_catalog_error::out_of_memory);
            return false;
        } catch (...) {
            set_error(error, font_catalog_error::filesystem_error);
            return false;
        }
    }
    sfnt_font_view font{};
    if (!sfnt_font_view::try_create(*storage, entry.face_index, font)) {
        set_error(error, font_catalog_error::invalid_font);
        return false;
    }
    result.storage_ = std::move(storage);
    result.font_ = font;
    result.catalog_index_ = catalog_index;
    result.identity_ = entry.identity;
    return true;
}

} // namespace progpu::native::text
