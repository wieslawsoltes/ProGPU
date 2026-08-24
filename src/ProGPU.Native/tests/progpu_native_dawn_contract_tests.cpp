#include "progpu_native_dawn.h"

#include <cstddef>
#include <cstdint>
#include <cstdlib>
#include <cstring>

namespace {

struct resolver_state final {
    const char* missing_name;
    std::size_t call_count;
    bool invalid_name;
};

void require(bool condition) {
    if (!condition) {
        std::abort();
    }
}

void* resolve_except_one(void* context, const char* name) {
    auto* state = static_cast<resolver_state*>(context);
    ++state->call_count;
    if (name == nullptr || std::strncmp(name, "wgpu", 4U) != 0) {
        state->invalid_name = true;
    }
    if (name != nullptr &&
        std::strcmp(name, state->missing_name) == 0) {
        return nullptr;
    }
    return context;
}

progpu_native_dawn_engine_options valid_options(
    resolver_state& state) {
    progpu_native_dawn_engine_options options{};
    options.struct_size = sizeof(options);
    options.native_abi_version = PROGPU_NATIVE_ABI_VERSION;
    options.adapter_abi_version =
        PROGPU_NATIVE_DAWN_ADAPTER_ABI_VERSION;
    options.provider_abi_version =
        PROGPU_NATIVE_DAWN_REQUIRED_PROVIDER_ABI_VERSION;
    options.target_format = PROGPU_NATIVE_TEXTURE_FORMAT_BGRA8_UNORM;
    options.resolver_context = &state;
    options.resolve_proc = resolve_except_one;
    options.instance = 1U;
    options.device = 2U;
    options.queue = 3U;
    return options;
}

} // namespace

int main() {
    require(progpu_native_get_abi_version() == PROGPU_NATIVE_ABI_VERSION);
    require(progpu_native_dawn_get_adapter_abi_version() ==
        PROGPU_NATIVE_DAWN_ADAPTER_ABI_VERSION);

    progpu_native_engine_info info{};
    info.struct_size = sizeof(info);
    require(progpu_native_get_info(&info) != 0U);
    require(info.backend_abi ==
        PROGPU_NATIVE_BACKEND_ABI_DAWN_WEBSCENE_2026_07);
    require((info.capabilities &
        PROGPU_NATIVE_CAPABILITY_SEMANTIC_SCENE_SNAPSHOTS) != 0U);
    require((info.capabilities &
        PROGPU_NATIVE_CAPABILITY_DEVICE_LOSS_RECREATION) != 0U);
    require(std::strstr(info.name, "Dawn provider") != nullptr);

    progpu_native_engine* engine = nullptr;
    progpu_native_engine_options generic{};
    generic.struct_size = sizeof(generic);
    generic.abi_version = PROGPU_NATIVE_ABI_VERSION;
    generic.backend_abi =
        PROGPU_NATIVE_BACKEND_ABI_WGPU_NATIVE_2024_05;
    generic.target_format = PROGPU_NATIVE_TEXTURE_FORMAT_BGRA8_UNORM;
    generic.device = 1U;
    generic.queue = 2U;
    require(progpu_native_engine_create(&generic, &engine) ==
        PROGPU_NATIVE_STATUS_UNSUPPORTED);
    require(engine == nullptr);

    resolver_state state{"wgpuDeviceCreateBuffer", 0U, false};
    auto options = valid_options(state);
    options.provider_abi_version = 1U;
    require(progpu_native_dawn_engine_create(&options, &engine) ==
        PROGPU_NATIVE_STATUS_INVALID_ARGUMENT);
    require(state.call_count == 0U);
    require(engine == nullptr);

    options = valid_options(state);
    options.flags = 1ULL << 1U;
    require(progpu_native_dawn_engine_create(&options, &engine) ==
        PROGPU_NATIVE_STATUS_INVALID_ARGUMENT);
    require(state.call_count == 0U);
    require(engine == nullptr);

    options = valid_options(state);
    options.flags = PROGPU_NATIVE_ENGINE_GLYPH_COMPUTE_FALLBACK;
    require(progpu_native_dawn_engine_create(&options, &engine) ==
        PROGPU_NATIVE_STATUS_UNSUPPORTED);
    require(state.call_count > 1U);
    require(!state.invalid_name);
    require(engine == nullptr);

    state.call_count = 0U;
    options = valid_options(state);
    require(progpu_native_dawn_engine_create(&options, &engine) ==
        PROGPU_NATIVE_STATUS_UNSUPPORTED);
    require(state.call_count > 1U);
    require(!state.invalid_name);
    require(engine == nullptr);

    return 0;
}
