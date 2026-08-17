cmake_minimum_required(VERSION 3.28)
project(ProGPUNative LANGUAGES CXX)

include(CTest)
include(GNUInstallDirs)

set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_CXX_EXTENSIONS OFF)

set(_progpu_native_modules_default OFF)
if(NOT EMSCRIPTEN
    AND CMAKE_CXX_COMPILER_ID STREQUAL "Clang"
    AND CMAKE_GENERATOR MATCHES "^Ninja"
    AND CMAKE_CXX_COMPILER_CLANG_SCAN_DEPS)
    set(_progpu_native_modules_default ON)
endif()
option(PROGPU_NATIVE_ENABLE_CPP_MODULES
    "Build and test the standard C++20 module import surface"
    ${_progpu_native_modules_default})
if(PROGPU_NATIVE_ENABLE_CPP_MODULES
    AND (EMSCRIPTEN
        OR NOT CMAKE_CXX_COMPILER_ID STREQUAL "Clang"
        OR NOT CMAKE_GENERATOR MATCHES "^Ninja"
        OR NOT CMAKE_CXX_COMPILER_CLANG_SCAN_DEPS))
    message(FATAL_ERROR
        "PROGPU_NATIVE_ENABLE_CPP_MODULES requires LLVM Clang and a supported "
        "Ninja generator; Emscripten and Visual Studio builds remain on the "
        "shared header compatibility path until module scanning is available.")
endif()
if(NOT PROGPU_NATIVE_ENABLE_CPP_MODULES)
    # CMake 4 may otherwise scan every C++20 translation unit when a toolchain
    # advertises a scanner, even though this build has no module file set.
    # Emscripten installations do not consistently ship clang-scan-deps.
    set(CMAKE_CXX_SCAN_FOR_MODULES OFF)
endif()

set(PROGPU_NATIVE_WEBGPU_INCLUDE_DIR "" CACHE PATH
    "Directory containing the pinned wgpu-native webgpu.h and wgpu.h headers")
set(PROGPU_NATIVE_WEBGPU_LIBRARY "" CACHE FILEPATH
    "Exact wgpu-native library consumed by Silk.NET.WebGPU 2.23.0")
set(PROGPU_NATIVE_DAWN_WEBGPU_INCLUDE_DIR "" CACHE PATH
    "Optional directory containing WebScene's exact modern Dawn webgpu.h")
set(PROGPU_NATIVE_WEBSCENE_PROVIDER_INCLUDE_DIR "" CACHE PATH
    "Optional directory containing webscene_gpu_provider.h")
set(PROGPU_NATIVE_WEBSCENE_PROVIDER_LIBRARY "" CACHE FILEPATH
    "Optional exact WebScene provider library for hardware integration tests")
set(PROGPU_NATIVE_VECTOR_SHADER
    "${CMAKE_CURRENT_SOURCE_DIR}/../ProGPU.Backend/Shaders/Vector.wgsl"
    CACHE FILEPATH "Shared ProGPU vector shader source")
set(PROGPU_NATIVE_PATH_RASTERIZER_SHADER
    "${CMAKE_CURRENT_SOURCE_DIR}/../ProGPU.Backend/Shaders/PathRasterizer.wgsl"
    CACHE FILEPATH "Shared ProGPU path-rasterizer shader source")
set(PROGPU_NATIVE_GLYPH_RASTERIZER_SHADER
    "${CMAKE_CURRENT_SOURCE_DIR}/../ProGPU.Backend/Shaders/GlyphRasterizer.wgsl"
    CACHE FILEPATH "Shared ProGPU glyph-rasterizer shader source")
set(PROGPU_NATIVE_TEXT_SHADER
    "${CMAKE_CURRENT_SOURCE_DIR}/../ProGPU.Backend/Shaders/Text.wgsl"
    CACHE FILEPATH "Shared ProGPU text shader source")
set(PROGPU_NATIVE_TEXTURE_SHADER
    "${CMAKE_CURRENT_SOURCE_DIR}/../ProGPU.Backend/Shaders/Texture.wgsl"
    CACHE FILEPATH "Shared ProGPU texture shader source")
set(PROGPU_NATIVE_IMAGE_EFFECT_SHADER
    "${CMAKE_CURRENT_SOURCE_DIR}/../ProGPU.Scene/Shaders/ImageEffect.wgsl"
    CACHE FILEPATH "Shared ProGPU image-effect shader source")
set(PROGPU_NATIVE_TEXTURE_GAUSSIAN_BLUR_SHADER
    "${CMAKE_CURRENT_SOURCE_DIR}/../ProGPU.Backend/Shaders/TextureGaussianBlur.wgsl"
    CACHE FILEPATH "Shared ProGPU texture Gaussian-blur shader source")
set(PROGPU_NATIVE_TEXTURE_GAUSSIAN_BLUR_UNFILTERABLE_SHADER
    "${CMAKE_CURRENT_SOURCE_DIR}/../ProGPU.Backend/Shaders/TextureGaussianBlurUnfilterable.wgsl"
    CACHE FILEPATH "Shared ProGPU unfilterable texture Gaussian-blur shader source")
set(PROGPU_NATIVE_CLIP_COMPOSE_SHADER
    "${CMAKE_CURRENT_SOURCE_DIR}/../ProGPU.Backend/Shaders/ClipCompose.wgsl"
    CACHE FILEPATH "Shared ProGPU retained clip-composition shader source")
set(PROGPU_NATIVE_GAUSSIAN_BLUR_HORIZONTAL_SHADER
    "${CMAKE_CURRENT_SOURCE_DIR}/../ProGPU.Compute/Shaders/GaussianBlurHorizontal.wgsl"
    CACHE FILEPATH "Shared ProGPU horizontal Gaussian-blur shader source")
set(PROGPU_NATIVE_GAUSSIAN_BLUR_VERTICAL_SHADER
    "${CMAKE_CURRENT_SOURCE_DIR}/../ProGPU.Compute/Shaders/GaussianBlurVertical.wgsl"
    CACHE FILEPATH "Shared ProGPU vertical Gaussian-blur shader source")
set(PROGPU_NATIVE_GROUP_DROP_SHADOW_COMPOSE_SHADER
    "${CMAKE_CURRENT_SOURCE_DIR}/../ProGPU.Compute/Shaders/GroupDropShadowCompose.wgsl"
    CACHE FILEPATH "Shared ProGPU group drop-shadow composition shader source")
set(PROGPU_NATIVE_GROUP_BLEND_SHADER
    "${CMAKE_CURRENT_SOURCE_DIR}/../ProGPU.Backend/Shaders/GroupBlend.wgsl"
    CACHE FILEPATH "Shared ProGPU group blend shader source")
set(PROGPU_NATIVE_ADVANCED_BLEND_SHADER
    "${CMAKE_CURRENT_SOURCE_DIR}/../ProGPU.Backend/Shaders/AdvancedBlend.wgsl"
    CACHE FILEPATH "Shared ProGPU destination-aware blend shader source")
set(PROGPU_NATIVE_3D_SHADER
    "${CMAKE_CURRENT_SOURCE_DIR}/../ProGPU.Scene/Shaders/Native3D.wgsl"
    CACHE FILEPATH "Shared ProGPU native retained 3D shader source")
option(PROGPU_NATIVE_BUILD_SAMPLE "Build the live headless WebGPU sample" ON)
option(PROGPU_NATIVE_BUILD_WGPU_TARGET
    "Build the directly linked pinned wgpu-native renderer" ON)
set(PROGPU_NATIVE_DAWN_LIBRARY_TYPE "SHARED" CACHE STRING
    "Library kind for the provider-resolved Dawn adapter (SHARED or STATIC)")
set_property(CACHE PROGPU_NATIVE_DAWN_LIBRARY_TYPE PROPERTY STRINGS SHARED STATIC)
option(PROGPU_NATIVE_ENABLE_SANITIZERS
    "Enable AddressSanitizer and UndefinedBehaviorSanitizer" OFF)

function(progpu_native_enable_warnings target_name)
    if(MSVC)
        target_compile_options(${target_name} PRIVATE /W4 /WX)
    else()
        target_compile_options(${target_name} PRIVATE
            -Wall -Wextra -Wpedantic -Werror)
    endif()
endfunction()

function(progpu_native_enable_sanitizers target_name)
    if(PROGPU_NATIVE_ENABLE_SANITIZERS)
        if(MSVC)
            message(FATAL_ERROR
                "PROGPU_NATIVE_ENABLE_SANITIZERS requires Clang or GCC")
        endif()
        target_compile_options(${target_name} PRIVATE
            -fsanitize=address,undefined
            -fno-omit-frame-pointer)
        target_link_options(${target_name} PRIVATE
            -fsanitize=address,undefined)
    endif()
endfunction()

if(NOT EMSCRIPTEN AND PROGPU_NATIVE_BUILD_WGPU_TARGET)
    if(NOT EXISTS "${PROGPU_NATIVE_WEBGPU_INCLUDE_DIR}/webgpu.h"
        OR NOT EXISTS "${PROGPU_NATIVE_WEBGPU_INCLUDE_DIR}/wgpu.h")
        message(FATAL_ERROR
            "PROGPU_NATIVE_WEBGPU_INCLUDE_DIR must contain the exact pinned "
            "webgpu.h and wgpu.h headers")
    endif()
    if(NOT EXISTS "${PROGPU_NATIVE_WEBGPU_LIBRARY}")
        message(FATAL_ERROR
            "PROGPU_NATIVE_WEBGPU_LIBRARY must name the exact native library")
    endif()
endif()
if(NOT EXISTS "${PROGPU_NATIVE_VECTOR_SHADER}")
    message(FATAL_ERROR "The shared ProGPU Vector.wgsl source is missing")
endif()
if(NOT EXISTS "${PROGPU_NATIVE_PATH_RASTERIZER_SHADER}")
    message(FATAL_ERROR "The shared ProGPU PathRasterizer.wgsl source is missing")
endif()
if(NOT EXISTS "${PROGPU_NATIVE_GLYPH_RASTERIZER_SHADER}")
    message(FATAL_ERROR "The shared ProGPU GlyphRasterizer.wgsl source is missing")
endif()
if(NOT EXISTS "${PROGPU_NATIVE_TEXT_SHADER}")
    message(FATAL_ERROR "The shared ProGPU Text.wgsl source is missing")
endif()
if(NOT EXISTS "${PROGPU_NATIVE_TEXTURE_SHADER}")
    message(FATAL_ERROR "The shared ProGPU Texture.wgsl source is missing")
endif()
if(NOT EXISTS "${PROGPU_NATIVE_IMAGE_EFFECT_SHADER}")
    message(FATAL_ERROR "The shared ProGPU ImageEffect.wgsl source is missing")
endif()
if(NOT EXISTS "${PROGPU_NATIVE_TEXTURE_GAUSSIAN_BLUR_SHADER}")
    message(FATAL_ERROR "The shared ProGPU texture Gaussian-blur shader source is missing")
endif()
if(NOT EXISTS "${PROGPU_NATIVE_TEXTURE_GAUSSIAN_BLUR_UNFILTERABLE_SHADER}")
    message(FATAL_ERROR "The shared ProGPU unfilterable texture Gaussian-blur shader source is missing")
endif()
if(NOT EXISTS "${PROGPU_NATIVE_CLIP_COMPOSE_SHADER}")
    message(FATAL_ERROR "The shared ProGPU ClipCompose.wgsl source is missing")
endif()
if(NOT EXISTS "${PROGPU_NATIVE_GAUSSIAN_BLUR_HORIZONTAL_SHADER}")
    message(FATAL_ERROR "The shared ProGPU horizontal Gaussian-blur shader source is missing")
endif()
if(NOT EXISTS "${PROGPU_NATIVE_GAUSSIAN_BLUR_VERTICAL_SHADER}")
    message(FATAL_ERROR "The shared ProGPU vertical Gaussian-blur shader source is missing")
endif()
if(NOT EXISTS "${PROGPU_NATIVE_GROUP_DROP_SHADOW_COMPOSE_SHADER}")
    message(FATAL_ERROR "The shared ProGPU group drop-shadow composition shader source is missing")
endif()
if(NOT EXISTS "${PROGPU_NATIVE_GROUP_BLEND_SHADER}")
    message(FATAL_ERROR "The shared ProGPU group-blend shader source is missing")
endif()
if(NOT EXISTS "${PROGPU_NATIVE_ADVANCED_BLEND_SHADER}")
    message(FATAL_ERROR "The shared ProGPU advanced-blend shader source is missing")
endif()
if(NOT EXISTS "${PROGPU_NATIVE_3D_SHADER}")
    message(FATAL_ERROR "The shared ProGPU Native3D.wgsl source is missing")
endif()

set(generated_directory "${CMAKE_CURRENT_BINARY_DIR}/generated")
set(generated_vector_shader
    "${generated_directory}/VectorWgsl.generated.hpp")
add_custom_command(
    OUTPUT "${generated_vector_shader}"
    COMMAND "${CMAKE_COMMAND}"
        "-DINPUT=${PROGPU_NATIVE_VECTOR_SHADER}"
        "-DOUTPUT=${generated_vector_shader}"
        "-DSYMBOL=vector_wgsl"
        -P "${CMAKE_CURRENT_SOURCE_DIR}/cmake/EmbedShader.cmake"
    DEPENDS
        "${PROGPU_NATIVE_VECTOR_SHADER}"
        "${CMAKE_CURRENT_SOURCE_DIR}/cmake/EmbedShader.cmake"
    COMMENT "Embedding the shared ProGPU Vector.wgsl shader"
    VERBATIM)
set(generated_glyph_rasterizer_shader
    "${generated_directory}/GlyphRasterizerWgsl.generated.hpp")
add_custom_command(
    OUTPUT "${generated_glyph_rasterizer_shader}"
    COMMAND "${CMAKE_COMMAND}"
        "-DINPUT=${PROGPU_NATIVE_GLYPH_RASTERIZER_SHADER}"
        "-DOUTPUT=${generated_glyph_rasterizer_shader}"
        "-DSYMBOL=glyph_rasterizer_wgsl"
        -P "${CMAKE_CURRENT_SOURCE_DIR}/cmake/EmbedShader.cmake"
    DEPENDS
        "${PROGPU_NATIVE_GLYPH_RASTERIZER_SHADER}"
        "${CMAKE_CURRENT_SOURCE_DIR}/cmake/EmbedShader.cmake"
    COMMENT "Embedding the shared ProGPU GlyphRasterizer.wgsl shader"
    VERBATIM)
set(generated_text_shader
    "${generated_directory}/TextWgsl.generated.hpp")
add_custom_command(
    OUTPUT "${generated_text_shader}"
    COMMAND "${CMAKE_COMMAND}"
        "-DINPUT=${PROGPU_NATIVE_TEXT_SHADER}"
        "-DOUTPUT=${generated_text_shader}"
        "-DSYMBOL=text_wgsl"
        -P "${CMAKE_CURRENT_SOURCE_DIR}/cmake/EmbedShader.cmake"
    DEPENDS
        "${PROGPU_NATIVE_TEXT_SHADER}"
        "${CMAKE_CURRENT_SOURCE_DIR}/cmake/EmbedShader.cmake"
    COMMENT "Embedding the shared ProGPU Text.wgsl shader"
    VERBATIM)
set(generated_texture_shader
    "${generated_directory}/TextureWgsl.generated.hpp")
add_custom_command(
    OUTPUT "${generated_texture_shader}"
    COMMAND "${CMAKE_COMMAND}"
        "-DINPUT=${PROGPU_NATIVE_TEXTURE_SHADER}"
        "-DOUTPUT=${generated_texture_shader}"
        "-DSYMBOL=texture_wgsl"
        -P "${CMAKE_CURRENT_SOURCE_DIR}/cmake/EmbedShader.cmake"
    DEPENDS
        "${PROGPU_NATIVE_TEXTURE_SHADER}"
        "${CMAKE_CURRENT_SOURCE_DIR}/cmake/EmbedShader.cmake"
    COMMENT "Embedding the shared ProGPU Texture.wgsl shader"
    VERBATIM)
set(generated_image_effect_shader
    "${generated_directory}/ImageEffectWgsl.generated.hpp")
add_custom_command(
    OUTPUT "${generated_image_effect_shader}"
    COMMAND "${CMAKE_COMMAND}"
        "-DINPUT=${PROGPU_NATIVE_IMAGE_EFFECT_SHADER}"
        "-DOUTPUT=${generated_image_effect_shader}"
        "-DSYMBOL=image_effect_wgsl"
        -P "${CMAKE_CURRENT_SOURCE_DIR}/cmake/EmbedShader.cmake"
    DEPENDS
        "${PROGPU_NATIVE_IMAGE_EFFECT_SHADER}"
        "${CMAKE_CURRENT_SOURCE_DIR}/cmake/EmbedShader.cmake"
    COMMENT "Embedding the shared ProGPU ImageEffect.wgsl shader"
    VERBATIM)
set(generated_texture_gaussian_blur_shader
    "${generated_directory}/TextureGaussianBlurWgsl.generated.hpp")
add_custom_command(
    OUTPUT "${generated_texture_gaussian_blur_shader}"
    COMMAND "${CMAKE_COMMAND}"
        "-DINPUT=${PROGPU_NATIVE_TEXTURE_GAUSSIAN_BLUR_SHADER}"
        "-DOUTPUT=${generated_texture_gaussian_blur_shader}"
        "-DSYMBOL=texture_gaussian_blur_wgsl"
        -P "${CMAKE_CURRENT_SOURCE_DIR}/cmake/EmbedShader.cmake"
    DEPENDS
        "${PROGPU_NATIVE_TEXTURE_GAUSSIAN_BLUR_SHADER}"
        "${CMAKE_CURRENT_SOURCE_DIR}/cmake/EmbedShader.cmake"
    COMMENT "Embedding the shared ProGPU texture Gaussian-blur shader"
    VERBATIM)
set(generated_texture_gaussian_blur_unfilterable_shader
    "${generated_directory}/TextureGaussianBlurUnfilterableWgsl.generated.hpp")
add_custom_command(
    OUTPUT "${generated_texture_gaussian_blur_unfilterable_shader}"
    COMMAND "${CMAKE_COMMAND}"
        "-DINPUT=${PROGPU_NATIVE_TEXTURE_GAUSSIAN_BLUR_UNFILTERABLE_SHADER}"
        "-DOUTPUT=${generated_texture_gaussian_blur_unfilterable_shader}"
        "-DSYMBOL=texture_gaussian_blur_unfilterable_wgsl"
        -P "${CMAKE_CURRENT_SOURCE_DIR}/cmake/EmbedShader.cmake"
    DEPENDS
        "${PROGPU_NATIVE_TEXTURE_GAUSSIAN_BLUR_UNFILTERABLE_SHADER}"
        "${CMAKE_CURRENT_SOURCE_DIR}/cmake/EmbedShader.cmake"
    COMMENT "Embedding the shared ProGPU unfilterable texture Gaussian-blur shader"
    VERBATIM)
set(generated_path_rasterizer_shader
    "${generated_directory}/PathRasterizerWgsl.generated.hpp")
add_custom_command(
    OUTPUT "${generated_path_rasterizer_shader}"
    COMMAND "${CMAKE_COMMAND}"
        "-DINPUT=${PROGPU_NATIVE_PATH_RASTERIZER_SHADER}"
        "-DOUTPUT=${generated_path_rasterizer_shader}"
        "-DSYMBOL=path_rasterizer_wgsl"
        -P "${CMAKE_CURRENT_SOURCE_DIR}/cmake/EmbedShader.cmake"
    DEPENDS
        "${PROGPU_NATIVE_PATH_RASTERIZER_SHADER}"
        "${CMAKE_CURRENT_SOURCE_DIR}/cmake/EmbedShader.cmake"
    COMMENT "Embedding the shared ProGPU PathRasterizer.wgsl shader"
    VERBATIM)
set(generated_clip_compose_shader
    "${generated_directory}/ClipComposeWgsl.generated.hpp")
add_custom_command(
    OUTPUT "${generated_clip_compose_shader}"
    COMMAND "${CMAKE_COMMAND}"
        "-DINPUT=${PROGPU_NATIVE_CLIP_COMPOSE_SHADER}"
        "-DOUTPUT=${generated_clip_compose_shader}"
        "-DSYMBOL=clip_compose_wgsl"
        -P "${CMAKE_CURRENT_SOURCE_DIR}/cmake/EmbedShader.cmake"
    DEPENDS
        "${PROGPU_NATIVE_CLIP_COMPOSE_SHADER}"
        "${CMAKE_CURRENT_SOURCE_DIR}/cmake/EmbedShader.cmake"
    COMMENT "Embedding the shared ProGPU ClipCompose.wgsl shader"
    VERBATIM)
set(generated_gaussian_blur_horizontal_shader
    "${generated_directory}/GaussianBlurHorizontalWgsl.generated.hpp")
add_custom_command(
    OUTPUT "${generated_gaussian_blur_horizontal_shader}"
    COMMAND "${CMAKE_COMMAND}"
        "-DINPUT=${PROGPU_NATIVE_GAUSSIAN_BLUR_HORIZONTAL_SHADER}"
        "-DOUTPUT=${generated_gaussian_blur_horizontal_shader}"
        "-DSYMBOL=gaussian_blur_horizontal_wgsl"
        -P "${CMAKE_CURRENT_SOURCE_DIR}/cmake/EmbedShader.cmake"
    DEPENDS
        "${PROGPU_NATIVE_GAUSSIAN_BLUR_HORIZONTAL_SHADER}"
        "${CMAKE_CURRENT_SOURCE_DIR}/cmake/EmbedShader.cmake"
    COMMENT "Embedding the shared ProGPU horizontal Gaussian-blur shader"
    VERBATIM)
set(generated_gaussian_blur_vertical_shader
    "${generated_directory}/GaussianBlurVerticalWgsl.generated.hpp")
add_custom_command(
    OUTPUT "${generated_gaussian_blur_vertical_shader}"
    COMMAND "${CMAKE_COMMAND}"
        "-DINPUT=${PROGPU_NATIVE_GAUSSIAN_BLUR_VERTICAL_SHADER}"
        "-DOUTPUT=${generated_gaussian_blur_vertical_shader}"
        "-DSYMBOL=gaussian_blur_vertical_wgsl"
        -P "${CMAKE_CURRENT_SOURCE_DIR}/cmake/EmbedShader.cmake"
    DEPENDS
        "${PROGPU_NATIVE_GAUSSIAN_BLUR_VERTICAL_SHADER}"
        "${CMAKE_CURRENT_SOURCE_DIR}/cmake/EmbedShader.cmake"
    COMMENT "Embedding the shared ProGPU vertical Gaussian-blur shader"
    VERBATIM)
set(generated_group_drop_shadow_compose_shader
    "${generated_directory}/GroupDropShadowComposeWgsl.generated.hpp")
add_custom_command(
    OUTPUT "${generated_group_drop_shadow_compose_shader}"
    COMMAND "${CMAKE_COMMAND}"
        "-DINPUT=${PROGPU_NATIVE_GROUP_DROP_SHADOW_COMPOSE_SHADER}"
        "-DOUTPUT=${generated_group_drop_shadow_compose_shader}"
        "-DSYMBOL=group_drop_shadow_compose_wgsl"
        -P "${CMAKE_CURRENT_SOURCE_DIR}/cmake/EmbedShader.cmake"
    DEPENDS
        "${PROGPU_NATIVE_GROUP_DROP_SHADOW_COMPOSE_SHADER}"
        "${CMAKE_CURRENT_SOURCE_DIR}/cmake/EmbedShader.cmake"
    COMMENT "Embedding the shared ProGPU group drop-shadow composition shader"
    VERBATIM)
set(generated_group_blend_shader
    "${generated_directory}/GroupBlendWgsl.generated.hpp")
add_custom_command(
    OUTPUT "${generated_group_blend_shader}"
    COMMAND "${CMAKE_COMMAND}"
        "-DINPUT=${PROGPU_NATIVE_GROUP_BLEND_SHADER}"
        "-DOUTPUT=${generated_group_blend_shader}"
        "-DSYMBOL=group_blend_wgsl"
        -P "${CMAKE_CURRENT_SOURCE_DIR}/cmake/EmbedShader.cmake"
    DEPENDS
        "${PROGPU_NATIVE_GROUP_BLEND_SHADER}"
        "${CMAKE_CURRENT_SOURCE_DIR}/cmake/EmbedShader.cmake"
    COMMENT "Embedding the shared ProGPU group-blend shader"
    VERBATIM)
set(generated_advanced_blend_shader
    "${generated_directory}/AdvancedBlendWgsl.generated.hpp")
add_custom_command(
    OUTPUT "${generated_advanced_blend_shader}"
    COMMAND "${CMAKE_COMMAND}"
        "-DINPUT=${PROGPU_NATIVE_ADVANCED_BLEND_SHADER}"
        "-DOUTPUT=${generated_advanced_blend_shader}"
        "-DSYMBOL=advanced_blend_wgsl"
        -P "${CMAKE_CURRENT_SOURCE_DIR}/cmake/EmbedShader.cmake"
    DEPENDS
        "${PROGPU_NATIVE_ADVANCED_BLEND_SHADER}"
        "${CMAKE_CURRENT_SOURCE_DIR}/cmake/EmbedShader.cmake"
    COMMENT "Embedding the shared ProGPU advanced-blend shader"
    VERBATIM)
set(generated_native_3d_shader
    "${generated_directory}/Native3DWgsl.generated.hpp")
add_custom_command(
    OUTPUT "${generated_native_3d_shader}"
    COMMAND "${CMAKE_COMMAND}"
        "-DINPUT=${PROGPU_NATIVE_3D_SHADER}"
        "-DOUTPUT=${generated_native_3d_shader}"
        "-DSYMBOL=native_3d_wgsl"
        -P "${CMAKE_CURRENT_SOURCE_DIR}/cmake/EmbedShader.cmake"
    DEPENDS
        "${PROGPU_NATIVE_3D_SHADER}"
        "${CMAKE_CURRENT_SOURCE_DIR}/cmake/EmbedShader.cmake"
    COMMENT "Embedding the shared ProGPU Native3D.wgsl shader"
    VERBATIM)

# All renderer variants include the same generated headers. Model generation
# as one target so multi-config generators cannot race two custom commands
# writing the same output files.
add_custom_target(progpu_native_generated_shaders
    DEPENDS
        "${generated_vector_shader}"
        "${generated_path_rasterizer_shader}"
        "${generated_glyph_rasterizer_shader}"
        "${generated_text_shader}"
        "${generated_texture_shader}"
        "${generated_image_effect_shader}"
        "${generated_texture_gaussian_blur_shader}"
        "${generated_texture_gaussian_blur_unfilterable_shader}"
        "${generated_clip_compose_shader}"
        "${generated_gaussian_blur_horizontal_shader}"
        "${generated_gaussian_blur_vertical_shader}"
        "${generated_group_drop_shadow_compose_shader}"
        "${generated_group_blend_shader}"
        "${generated_advanced_blend_shader}"
        "${generated_native_3d_shader}")

set(PROGPU_NATIVE_BACKEND_SOURCES
    src/Backend/progpu_native_advanced_blend_execution.cpp
    src/Backend/progpu_native_backdrop_execution.cpp
    src/Backend/progpu_native.cpp
    src/Backend/progpu_native_draw_state.cpp
    src/Backend/progpu_native_draw_state.hpp
    src/Backend/progpu_native_device_recovery.cpp
    src/Backend/progpu_native_device_recovery.hpp
    src/Backend/progpu_native_engine.hpp
    src/Backend/progpu_native_effect_plan.cpp
    src/Backend/progpu_native_effect_plan.hpp
    src/Backend/progpu_native_frame_execution.hpp
    src/Backend/progpu_native_frame_execution_common.hpp
    src/Backend/progpu_native_geometry.hpp
    src/Backend/progpu_native_geometry_analytic.hpp
    src/Backend/progpu_native_geometry_base.hpp
    src/Backend/progpu_native_geometry_dash.hpp
    src/Backend/progpu_native_geometry_spline.hpp
    src/Backend/progpu_native_geometry_stroke.hpp
    src/Backend/progpu_native_gpu_records.hpp
    src/Backend/progpu_native_clip_execution.cpp
    src/Backend/progpu_native_clip_resources.cpp
    src/Backend/progpu_native_color_glyph_resources.cpp
    src/Backend/progpu_native_image_execution.cpp
    src/Backend/progpu_native_image_layer_resources.cpp
    src/Backend/progpu_native_layer_resource_execution.cpp
    src/Backend/progpu_native_3d_execution.cpp
    src/Backend/progpu_native_3d_execution.hpp
    src/Backend/progpu_native_effect_execution.cpp
    src/Backend/progpu_native_layer_composite_execution.cpp
    src/Backend/progpu_native_path_text_resources.cpp
    src/Backend/progpu_native_path_execution.cpp
    src/Backend/progpu_native_glyph_execution.cpp
    src/Backend/progpu_native_texture_execution.cpp
    src/Backend/progpu_native_pipeline.cpp
    src/Backend/progpu_native_pipeline.hpp
    src/Backend/progpu_native_replay_execution.hpp
    src/Backend/progpu_native_webgpu_resources.hpp
    src/Backend/progpu_native_vector_execution.cpp
    src/Backend/progpu_webgpu_compat.hpp)

set(PROGPU_NATIVE_SCENE_SOURCES
    src/Scene/progpu_native_semantic_masked_text_pipeline.cpp
    src/Scene/progpu_native_semantic_masked_vector_pipeline.cpp
    src/Scene/progpu_native_semantic_mask_chain_pipeline.cpp
    src/Scene/progpu_native_scene.cpp
    src/Scene/progpu_native_scene.hpp
    src/Scene/progpu_native_semantic_budget.hpp
    src/Scene/progpu_native_semantic_brush.cpp
    src/Scene/progpu_native_semantic_brush.hpp
    src/Scene/progpu_native_semantic_color_glyph.cpp
    src/Scene/progpu_native_semantic_color_glyph.hpp
    src/Scene/progpu_native_semantic_image.cpp
    src/Scene/progpu_native_semantic_image.hpp
    src/Scene/progpu_native_semantic_image_resources.cpp
    src/Scene/progpu_native_semantic_image_effect_resources.cpp
    src/Scene/progpu_native_semantic_image_blur_resources.cpp
    src/Scene/progpu_native_semantic_image_resources.hpp
    src/Scene/progpu_native_semantic_identity.cpp
    src/Scene/progpu_native_semantic_identity.hpp
    src/Scene/progpu_native_semantic_layer_mask.cpp
    src/Scene/progpu_native_semantic_layer_mask.hpp
    src/Scene/progpu_native_semantic_layer_mask_resources.cpp
    src/Scene/progpu_native_semantic_layer_mask_resources.hpp
    src/Scene/progpu_native_semantic_text_style.cpp
    src/Scene/progpu_native_semantic_text_style.hpp
    src/Scene/progpu_native_semantic_effect_cache.cpp
    src/Scene/progpu_native_semantic_effect_cache.hpp
    src/Scene/progpu_native_semantic_draw_execution.cpp
    src/Scene/progpu_native_semantic_draw_execution.hpp
    src/Scene/progpu_native_semantic_update_execution.cpp
    src/Scene/progpu_native_semantic_render_execution.cpp
    src/Scene/progpu_native_semantic_replay.hpp
    src/Scene/progpu_native_semantic_state.cpp
    src/Scene/progpu_native_semantic_state.hpp
    src/Scene/progpu_native_semantic_validation.cpp
    src/Scene/progpu_native_semantic_validation.hpp)

set(PROGPU_NATIVE_RENDERER_SOURCES
    ${PROGPU_NATIVE_BACKEND_SOURCES}
    ${PROGPU_NATIVE_SCENE_SOURCES}
    include/progpu_native.h)

# Mirror the managed ProGPU project boundaries in the native source tree while
# keeping internal includes independent of a translation unit's physical folder.
# Public consumers continue to see only the stable headers under include/.
set(PROGPU_NATIVE_INTERNAL_INCLUDE_DIRECTORIES
    "${CMAKE_CURRENT_SOURCE_DIR}/src/Backend"
    "${CMAKE_CURRENT_SOURCE_DIR}/src/Scene"
    "${CMAKE_CURRENT_SOURCE_DIR}/src/Scene/Builder"
    "${CMAKE_CURRENT_SOURCE_DIR}/src/Text"
    "${CMAKE_CURRENT_SOURCE_DIR}/src/Text/Bidi"
    "${CMAKE_CURRENT_SOURCE_DIR}/src/Text/Shaping"
    "${CMAKE_CURRENT_SOURCE_DIR}/src/Image"
    "${CMAKE_CURRENT_SOURCE_DIR}/src/Compression")

add_library(progpu_native_compression STATIC
    src/Compression/progpu_native_deflate.cpp
    src/Compression/progpu_native_deflate_huffman.cpp
    src/Compression/progpu_native_deflate_internal.hpp
    src/Compression/progpu_native_gzip.cpp
    src/Compression/progpu_native_zlib.cpp
    include/progpu_native_compression.hpp)
target_compile_features(progpu_native_compression PUBLIC cxx_std_20)
target_include_directories(progpu_native_compression
    PUBLIC "${CMAKE_CURRENT_SOURCE_DIR}/include"
    PRIVATE "${CMAKE_CURRENT_SOURCE_DIR}/src/Compression")
if(PROGPU_NATIVE_ENABLE_CPP_MODULES)
    target_sources(progpu_native_compression
        PUBLIC
        FILE_SET CXX_MODULES
        FILES src/Compression/progpu_native_compression.cppm)
endif()
progpu_native_enable_warnings(progpu_native_compression)
progpu_native_enable_sanitizers(progpu_native_compression)

add_library(progpu_native_image STATIC
    src/Image/progpu_native_png.cpp
    src/Image/progpu_native_png_crc.cpp
    src/Image/progpu_native_png_filter.cpp
    src/Image/progpu_native_png_layout.cpp
    src/Image/progpu_native_png_pixels.cpp
    src/Image/progpu_native_png_internal.hpp
    include/progpu_native_image.hpp)
target_compile_features(progpu_native_image PUBLIC cxx_std_20)
target_include_directories(progpu_native_image
    PUBLIC "${CMAKE_CURRENT_SOURCE_DIR}/include"
    PRIVATE
        "${CMAKE_CURRENT_SOURCE_DIR}/src/Image"
        "${CMAKE_CURRENT_SOURCE_DIR}/src/Compression")
target_link_libraries(progpu_native_image PUBLIC progpu_native_compression)
if(PROGPU_NATIVE_ENABLE_CPP_MODULES)
    target_sources(progpu_native_image
        PUBLIC
        FILE_SET CXX_MODULES
        FILES src/Image/progpu_native_image.cppm)
endif()
progpu_native_enable_warnings(progpu_native_image)
progpu_native_enable_sanitizers(progpu_native_image)

add_library(progpu_native_text STATIC
    src/Text/progpu_native_unicode.cpp
    src/Text/Unicode/progpu_native_unicode_category.cpp
    src/Text/Unicode/progpu_native_unicode_directional.cpp
    src/Text/Bidi/progpu_native_unicode_bidi.cpp
    src/Text/Bidi/progpu_native_text_visual_order.cpp
    src/Text/progpu_native_unicode_grapheme.cpp
    src/Text/Shaping/progpu_native_unicode_shaping.cpp
    src/Text/Shaping/progpu_native_unicode_syllables.cpp
    src/Text/Shaping/progpu_native_unicode_arabic.cpp
    src/Text/Shaping/progpu_native_arabic_stretch.cpp
    src/Text/Shaping/progpu_native_arabic_fallback.cpp
    src/Text/Shaping/progpu_native_arabic_stretch_internal.hpp
    src/Text/progpu_native_unicode_normalization.cpp
    src/Text/Shaping/progpu_native_open_type_layout.cpp
    src/Text/Shaping/progpu_native_open_type_features.cpp
    src/Text/Shaping/progpu_native_open_type_feature_tags.cpp
    src/Text/Shaping/progpu_native_open_type_feature_values.cpp
    src/Text/Shaping/progpu_native_open_type_feature_values_internal.hpp
    src/Text/Shaping/progpu_native_open_type_gdef.cpp
    src/Text/Shaping/progpu_native_open_type_gsub.cpp
    src/Text/Shaping/progpu_native_open_type_gsub_internal.hpp
    src/Text/Shaping/progpu_native_open_type_gpos.cpp
    src/Text/Shaping/progpu_native_open_type_gpos_context.cpp
    src/Text/Shaping/progpu_native_open_type_gpos_internal.hpp
    src/Text/Shaping/progpu_native_legacy_kern.cpp
    src/Text/Shaping/progpu_native_legacy_kern_internal.hpp
    src/Text/Shaping/progpu_native_fallback_marks.cpp
    src/Text/Shaping/progpu_native_fallback_marks_internal.hpp
    src/Text/Shaping/progpu_native_open_type_plan.cpp
    src/Text/Shaping/progpu_native_open_type_shaper.cpp
    src/Text/Shaping/progpu_native_initial_mapping.cpp
    src/Text/Shaping/progpu_native_initial_mapping_internal.hpp
    src/Text/Shaping/progpu_native_shaping_route.cpp
    src/Text/Shaping/progpu_native_shaping_features.cpp
    src/Text/Shaping/progpu_native_shaping_request_features.cpp
    src/Text/Shaping/progpu_native_shaping_configuration.cpp
    src/Text/Shaping/progpu_native_shaping_options_internal.hpp
    src/Text/Shaping/progpu_native_space_fallback.cpp
    src/Text/Shaping/progpu_native_use_diacritics.cpp
    src/Text/Shaping/progpu_native_vowel_constraints.cpp
    src/Text/Shaping/progpu_native_open_type_complex_internal.hpp
    src/Text/Shaping/progpu_native_open_type_khmer.cpp
    src/Text/Shaping/progpu_native_open_type_indic.cpp
    src/Text/Shaping/progpu_native_open_type_myanmar.cpp
    src/Text/Shaping/progpu_native_open_type_use.cpp
    src/Text/Shaping/progpu_native_open_type_hangul.cpp
    src/Text/Shaping/progpu_native_open_type_preprocess.cpp
    src/Text/Shaping/progpu_native_font_fallback.cpp
    src/Text/Shaping/progpu_native_font_fallback_preferences.cpp
    src/Text/Shaping/progpu_native_font_provider.cpp
    src/Text/Font/progpu_native_font_kerning.cpp
    src/Text/Font/progpu_native_font_horizontal_metrics.cpp
    src/Text/Font/progpu_native_font_bounds.cpp
    src/Text/Font/progpu_native_font_style.cpp
    src/Text/Font/progpu_native_font_vertical_metrics.cpp
    src/Text/progpu_native_unicode_line_break.cpp
    src/Text/progpu_native_text_layout.cpp
    src/Text/progpu_native_text_metrics.cpp
    src/Text/progpu_native_vertical_text_layout.cpp
    src/Text/progpu_native_vertical_text_interaction.cpp
    src/Text/progpu_native_text_interaction.cpp
    src/Text/progpu_native_unicode_data.generated.hpp
    src/Text/progpu_native_font_variations.cpp
    src/Text/progpu_native_font_bytes.hpp
    src/Text/progpu_native_gvar.cpp
    src/Text/progpu_native_gvar_apply.cpp
    src/Text/progpu_native_gvar_composite.cpp
    src/Text/progpu_native_gvar_payload.cpp
    src/Text/progpu_native_gvar_phantom.cpp
    src/Text/progpu_native_gvar_payload_internal.hpp
    src/Text/progpu_native_gvar_packed.cpp
    src/Text/progpu_native_gvar_interpolate.cpp
    src/Text/progpu_native_gvar_tuples.cpp
    src/Text/progpu_native_item_variation_store.cpp
    src/Text/progpu_native_hvar.cpp
    src/Text/progpu_native_mvar.cpp
    src/Text/progpu_native_gdef_variations.cpp
    src/Text/progpu_native_cff_index.cpp
    src/Text/progpu_native_cff_dictionary.cpp
    src/Text/progpu_native_cff_private.cpp
    src/Text/progpu_native_cff_fd_select.cpp
    src/Text/progpu_native_cff1.cpp
    src/Text/progpu_native_cff2.cpp
    src/Text/progpu_native_cff_type2.cpp
    src/Text/progpu_native_cff_type2_operators.cpp
    src/Text/progpu_native_cff_outline.cpp
    src/Text/progpu_native_cff_type2_internal.hpp
    src/Text/progpu_native_sbix.cpp
    src/Text/progpu_native_cblc.cpp
    src/Text/progpu_native_cblc_index.cpp
    src/Text/progpu_native_cbdt.cpp
    src/Text/progpu_native_cbdt_internal.hpp
    src/Text/progpu_native_colr.cpp
    src/Text/progpu_native_cpal.cpp
    src/Text/progpu_native_colr_internal.hpp
    src/Text/progpu_native_svg_glyph.cpp
    src/Text/progpu_native_svg_glyph_decode.cpp
    src/Text/progpu_native_svg_glyph_layers.cpp
    src/Text/progpu_native_svg_arc.cpp
    src/Text/progpu_native_svg_document_internal.hpp
    src/Text/progpu_native_svg_number.hpp
    src/Text/progpu_native_svg_path.cpp
    src/Text/progpu_native_svg_path_internal.hpp
    src/Text/progpu_native_svg_values.cpp
    src/Text/progpu_native_svg_xml.cpp
    src/Text/Metadata/progpu_native_sfnt_metadata.cpp
    src/Text/Metadata/progpu_native_sfnt_names.cpp
    src/Text/Metadata/progpu_native_sfnt_resident.cpp
    src/Text/Metadata/progpu_native_sfnt_standalone.cpp
    src/Text/Unicode/progpu_native_unicode_categories.generated.hpp
    src/Text/Subset/progpu_native_sfnt_subset.cpp
    src/Text/Subset/progpu_native_sfnt_subset_compact.cpp
    src/Text/Subset/progpu_native_sfnt_subset_glyphs.cpp
    src/Text/Subset/progpu_native_sfnt_subset_internal.hpp
    src/Text/Subset/progpu_native_sfnt_subset_tables.cpp
    src/Text/progpu_native_woff1.cpp
    src/Text/progpu_native_sfnt.cpp
    src/Text/progpu_native_true_type_composite.cpp
    src/Text/progpu_native_true_type_composite_internal.hpp
    src/Text/progpu_native_true_type_glyph.cpp
    src/Text/progpu_native_true_type_outline.cpp
    src/Text/progpu_native_true_type_varied_outline.cpp
    src/Text/progpu_native_true_type_varied_requirements.cpp
    src/Text/progpu_native_true_type_path.cpp
    include/progpu_native_text.hpp)
target_compile_features(progpu_native_text PUBLIC cxx_std_20)
target_include_directories(progpu_native_text
    PUBLIC "${CMAKE_CURRENT_SOURCE_DIR}/include"
    PRIVATE
        "${CMAKE_CURRENT_SOURCE_DIR}/src/Text"
        "${CMAKE_CURRENT_SOURCE_DIR}/src/Text/Bidi"
        "${CMAKE_CURRENT_SOURCE_DIR}/src/Text/Shaping"
        "${CMAKE_CURRENT_SOURCE_DIR}/src/Compression")
target_link_libraries(progpu_native_text PUBLIC progpu_native_compression)
if(PROGPU_NATIVE_ENABLE_CPP_MODULES)
    target_sources(progpu_native_text
        PUBLIC
        FILE_SET CXX_MODULES
        FILES src/Text/progpu_native_text.cppm)
endif()
progpu_native_enable_warnings(progpu_native_text)
progpu_native_enable_sanitizers(progpu_native_text)

add_library(progpu_native_scene_builder STATIC
    src/Scene/Builder/progpu_native_scene_builder.cpp
    src/Scene/Builder/progpu_native_scene_builder_3d.cpp
    src/Scene/Builder/progpu_native_scene_builder_brush.cpp
    src/Scene/Builder/progpu_native_scene_builder_geometry.cpp
    src/Scene/Builder/progpu_native_scene_builder_glyph.cpp
    src/Scene/Builder/progpu_native_scene_builder_image.cpp
    src/Scene/Builder/progpu_native_scene_builder_layer.cpp
    src/Scene/Builder/progpu_native_scene_builder_path.cpp
    src/Scene/Builder/progpu_native_scene_builder_text.cpp
    src/Scene/Builder/progpu_native_scene_builder_internal.hpp
    src/Scene/progpu_native_semantic_layer_mask.cpp
    src/Scene/progpu_native_semantic_layer_mask.hpp
    src/Scene/progpu_native_semantic_validation.cpp
    src/Scene/progpu_native_semantic_validation.hpp
    include/progpu_native_scene_builder.hpp)
target_compile_features(progpu_native_scene_builder PUBLIC cxx_std_20)
target_include_directories(progpu_native_scene_builder
    PUBLIC "${CMAKE_CURRENT_SOURCE_DIR}/include"
    PRIVATE ${PROGPU_NATIVE_INTERNAL_INCLUDE_DIRECTORIES})
if(PROGPU_NATIVE_ENABLE_CPP_MODULES)
    target_sources(progpu_native_scene_builder
        PUBLIC
        FILE_SET CXX_MODULES
        FILES src/Scene/Builder/progpu_native_scene_builder.cppm)
endif()
progpu_native_enable_warnings(progpu_native_scene_builder)
progpu_native_enable_sanitizers(progpu_native_scene_builder)

if(NOT EMSCRIPTEN AND PROGPU_NATIVE_BUILD_WGPU_TARGET)
add_library(progpu_native SHARED
    ${PROGPU_NATIVE_RENDERER_SOURCES})
add_dependencies(progpu_native progpu_native_generated_shaders)
target_compile_features(progpu_native PRIVATE cxx_std_20)
target_compile_definitions(progpu_native PRIVATE PROGPU_NATIVE_BUILD=1)
target_include_directories(progpu_native
    PUBLIC "${CMAKE_CURRENT_SOURCE_DIR}/include"
    PRIVATE
        ${PROGPU_NATIVE_INTERNAL_INCLUDE_DIRECTORIES}
        "${generated_directory}")
target_include_directories(progpu_native SYSTEM PRIVATE
    "${PROGPU_NATIVE_WEBGPU_INCLUDE_DIR}")
target_link_libraries(progpu_native PRIVATE
    "${PROGPU_NATIVE_WEBGPU_LIBRARY}")
progpu_native_enable_warnings(progpu_native)
progpu_native_enable_sanitizers(progpu_native)
set_target_properties(progpu_native PROPERTIES
    CXX_VISIBILITY_PRESET hidden
    VISIBILITY_INLINES_HIDDEN YES)
endif()

if(NOT EMSCRIPTEN AND PROGPU_NATIVE_DAWN_WEBGPU_INCLUDE_DIR)
    if(NOT EXISTS "${PROGPU_NATIVE_DAWN_WEBGPU_INCLUDE_DIR}/webgpu.h")
        message(FATAL_ERROR
            "PROGPU_NATIVE_DAWN_WEBGPU_INCLUDE_DIR must contain webgpu.h")
    endif()
    if(NOT PROGPU_NATIVE_DAWN_LIBRARY_TYPE STREQUAL "SHARED" AND
       NOT PROGPU_NATIVE_DAWN_LIBRARY_TYPE STREQUAL "STATIC")
        message(FATAL_ERROR
            "PROGPU_NATIVE_DAWN_LIBRARY_TYPE must be SHARED or STATIC")
    endif()
    add_library(progpu_native_dawn ${PROGPU_NATIVE_DAWN_LIBRARY_TYPE}
        ${PROGPU_NATIVE_RENDERER_SOURCES}
        include/progpu_native_dawn.h
        )
    add_dependencies(progpu_native_dawn progpu_native_generated_shaders)
    target_compile_features(progpu_native_dawn
        PRIVATE cxx_std_20)
    target_compile_definitions(progpu_native_dawn PRIVATE
        PROGPU_NATIVE_BUILD=1
        PROGPU_NATIVE_DAWN_ABI=1)
    target_include_directories(progpu_native_dawn
        PUBLIC "${CMAKE_CURRENT_SOURCE_DIR}/include"
        PRIVATE
            ${PROGPU_NATIVE_INTERNAL_INCLUDE_DIRECTORIES}
            "${generated_directory}")
    target_include_directories(progpu_native_dawn SYSTEM PRIVATE
        "${PROGPU_NATIVE_DAWN_WEBGPU_INCLUDE_DIR}")
    progpu_native_enable_warnings(progpu_native_dawn)
    progpu_native_enable_sanitizers(progpu_native_dawn)
    set_target_properties(progpu_native_dawn PROPERTIES
        CXX_VISIBILITY_PRESET hidden
        VISIBILITY_INLINES_HIDDEN YES)
    # A sanitizer-instrumented shared library deliberately resolves the ASan/
    # UBSan runtime from its instrumented test executable. --no-undefined is
    # still mandatory for every production configuration.
    if(UNIX AND NOT APPLE AND NOT PROGPU_NATIVE_ENABLE_SANITIZERS)
        target_link_options(progpu_native_dawn PRIVATE
            "-Wl,--no-undefined")
    endif()

    if(BUILD_TESTING)
        add_executable(progpu_native_dawn_contract_tests
            tests/progpu_native_dawn_contract_tests.cpp)
        target_compile_features(progpu_native_dawn_contract_tests
            PRIVATE cxx_std_20)
        target_include_directories(progpu_native_dawn_contract_tests PRIVATE
            "${CMAKE_CURRENT_SOURCE_DIR}/include")
        target_link_libraries(progpu_native_dawn_contract_tests PRIVATE
            progpu_native_dawn)
        progpu_native_enable_warnings(progpu_native_dawn_contract_tests)
        progpu_native_enable_sanitizers(progpu_native_dawn_contract_tests)
        add_test(
            NAME progpu_native_dawn_contract_tests
            COMMAND progpu_native_dawn_contract_tests)
        set_tests_properties(
            progpu_native_dawn_contract_tests
            PROPERTIES TIMEOUT 60)

        if(APPLE
            AND PROGPU_NATIVE_WEBSCENE_PROVIDER_INCLUDE_DIR
            AND PROGPU_NATIVE_WEBSCENE_PROVIDER_LIBRARY)
            if(NOT EXISTS
                "${PROGPU_NATIVE_WEBSCENE_PROVIDER_INCLUDE_DIR}/webscene_gpu_provider.h")
                message(FATAL_ERROR
                    "PROGPU_NATIVE_WEBSCENE_PROVIDER_INCLUDE_DIR must contain "
                    "webscene_gpu_provider.h")
            endif()
            if(NOT EXISTS "${PROGPU_NATIVE_WEBSCENE_PROVIDER_LIBRARY}")
                message(FATAL_ERROR
                    "PROGPU_NATIVE_WEBSCENE_PROVIDER_LIBRARY does not exist")
            endif()
            add_executable(progpu_native_webscene_provider_tests
                tests/progpu_native_webscene_provider_tests.cpp
                tests/progpu_native_semantic_advanced_blend_scene.cpp
                tests/progpu_native_semantic_advanced_blend_scene.hpp
                tests/progpu_native_semantic_backdrop_scene.cpp
                tests/progpu_native_semantic_backdrop_scene.hpp
                tests/progpu_native_semantic_color_glyph_scene.cpp
                tests/progpu_native_semantic_color_glyph_scene.hpp
                tests/progpu_native_semantic_coverage_mask_scene.cpp
                tests/progpu_native_semantic_coverage_mask_scene.hpp
                tests/progpu_native_semantic_state_mask_scene.cpp
                tests/progpu_native_semantic_state_mask_scene.hpp
                tests/progpu_native_webscene_advanced_blend_fixture.cpp
                tests/progpu_native_webscene_advanced_blend_fixture.hpp
                tests/progpu_native_webscene_semantic_effect_fixture.cpp
                tests/progpu_native_webscene_semantic_effect_fixture.hpp
                tests/progpu_native_webscene_state_mask_fixture.cpp
                tests/progpu_native_webscene_state_mask_fixture.hpp
                tests/progpu_native_webscene_state_mask_media_fixture.cpp
                tests/progpu_native_webscene_state_mask_media_fixture.hpp)
            target_compile_features(progpu_native_webscene_provider_tests
                PRIVATE cxx_std_20)
            target_include_directories(
                progpu_native_webscene_provider_tests PRIVATE
                "${CMAKE_CURRENT_SOURCE_DIR}/include"
                "${PROGPU_NATIVE_WEBSCENE_PROVIDER_INCLUDE_DIR}")
            target_include_directories(
                progpu_native_webscene_provider_tests SYSTEM PRIVATE
                "${PROGPU_NATIVE_DAWN_WEBGPU_INCLUDE_DIR}")
            find_library(PROGPU_NATIVE_IOSURFACE_FRAMEWORK IOSurface REQUIRED)
            target_link_libraries(progpu_native_webscene_provider_tests PRIVATE
                progpu_native_dawn
                progpu_native_scene_builder
                "${PROGPU_NATIVE_IOSURFACE_FRAMEWORK}"
                "${CMAKE_DL_LIBS}")
            progpu_native_enable_warnings(
                progpu_native_webscene_provider_tests)
            progpu_native_enable_sanitizers(
                progpu_native_webscene_provider_tests)
            add_test(
                NAME progpu_native_webscene_provider_tests
                COMMAND progpu_native_webscene_provider_tests
                    "${PROGPU_NATIVE_WEBSCENE_PROVIDER_LIBRARY}"
                    "${CMAKE_CURRENT_BINARY_DIR}/progpu-native-webscene-provider.ppm")
            set_tests_properties(
                progpu_native_webscene_provider_tests
                PROPERTIES TIMEOUT 60)
        endif()
    endif()
endif()

if(EMSCRIPTEN)
    add_executable(progpu_native_browser_smoke
        ${PROGPU_NATIVE_RENDERER_SOURCES}
        include/progpu_native_browser.h
        include/progpu_native_dawn.h
        browser/progpu_native_browser_evidence.cpp
        browser/progpu_native_browser_evidence.hpp
        tests/progpu_native_semantic_advanced_blend_scene.cpp
        tests/progpu_native_semantic_advanced_blend_scene.hpp
        tests/progpu_native_semantic_backdrop_scene.cpp
        tests/progpu_native_semantic_backdrop_scene.hpp
        tests/progpu_native_semantic_color_glyph_scene.cpp
        tests/progpu_native_semantic_color_glyph_scene.hpp
        tests/progpu_native_semantic_coverage_mask_scene.cpp
        tests/progpu_native_semantic_coverage_mask_scene.hpp
        tests/progpu_native_semantic_geometry_scene.cpp
        tests/progpu_native_semantic_geometry_scene.hpp
        tests/progpu_native_semantic_image_scene.cpp
        tests/progpu_native_semantic_image_scene.hpp
        tests/progpu_native_semantic_rounded_mask_scene.cpp
        tests/progpu_native_semantic_rounded_mask_scene.hpp
        tests/progpu_native_semantic_state_mask_scene.cpp
        tests/progpu_native_semantic_state_mask_scene.hpp
        tests/progpu_native_semantic_text_scene.cpp
        tests/progpu_native_semantic_text_scene.hpp
        browser/progpu_native_browser_smoke.cpp)
    add_dependencies(
        progpu_native_browser_smoke
        progpu_native_generated_shaders)
    target_compile_features(progpu_native_browser_smoke PRIVATE cxx_std_20)
    target_compile_definitions(progpu_native_browser_smoke PRIVATE
        PROGPU_NATIVE_BUILD=1
        PROGPU_NATIVE_DAWN_ABI=1
        PROGPU_NATIVE_BROWSER=1)
    target_include_directories(progpu_native_browser_smoke PRIVATE
        "${CMAKE_CURRENT_SOURCE_DIR}/include"
        ${PROGPU_NATIVE_INTERNAL_INCLUDE_DIRECTORIES}
        "${CMAKE_CURRENT_SOURCE_DIR}/browser/include"
        "${CMAKE_CURRENT_SOURCE_DIR}/tests"
        "${generated_directory}")
    target_link_libraries(progpu_native_browser_smoke PRIVATE
        progpu_native_image
        progpu_native_scene_builder
        progpu_native_text)
    target_compile_options(progpu_native_browser_smoke PRIVATE
        --use-port=emdawnwebgpu:cpp_bindings=false)
    target_link_options(progpu_native_browser_smoke PRIVATE
        --use-port=emdawnwebgpu:cpp_bindings=false
        --pre-js=${CMAKE_CURRENT_SOURCE_DIR}/browser/pre.js
        --shell-file=${CMAKE_CURRENT_SOURCE_DIR}/browser/shell.html
        -sALLOW_MEMORY_GROWTH=1
        -sEXIT_RUNTIME=0
        -sENVIRONMENT=web
        -sASSERTIONS=1)
    progpu_native_enable_warnings(progpu_native_browser_smoke)
    set_target_properties(progpu_native_browser_smoke PROPERTIES
        SUFFIX ".html"
        LINK_DEPENDS
            "${CMAKE_CURRENT_SOURCE_DIR}/browser/pre.js;${CMAKE_CURRENT_SOURCE_DIR}/browser/shell.html")
endif()

if(NOT EMSCRIPTEN AND PROGPU_NATIVE_BUILD_WGPU_TARGET)
get_filename_component(webgpu_library_directory
    "${PROGPU_NATIVE_WEBGPU_LIBRARY}" DIRECTORY)
if(APPLE)
    set_target_properties(progpu_native PROPERTIES
        BUILD_RPATH "@loader_path;${webgpu_library_directory}"
        INSTALL_RPATH "@loader_path")
elseif(UNIX)
    set_target_properties(progpu_native PROPERTIES
        BUILD_RPATH "$ORIGIN;${webgpu_library_directory}"
        INSTALL_RPATH "$ORIGIN")
endif()
endif()

if(BUILD_TESTING AND NOT EMSCRIPTEN)
    if(PROGPU_NATIVE_ENABLE_CPP_MODULES)
        add_executable(progpu_native_compression_module_tests
            tests/progpu_native_compression_module_tests.cpp)
        target_link_libraries(
            progpu_native_compression_module_tests
            PRIVATE progpu_native_compression)
        progpu_native_enable_warnings(
            progpu_native_compression_module_tests)
        progpu_native_enable_sanitizers(
            progpu_native_compression_module_tests)
        add_test(
            NAME progpu_native_compression_module_tests
            COMMAND progpu_native_compression_module_tests)
        set_tests_properties(
            progpu_native_compression_module_tests
            PROPERTIES TIMEOUT 60)

        add_executable(progpu_native_image_module_tests
            tests/progpu_native_image_module_tests.cpp)
        target_link_libraries(
            progpu_native_image_module_tests
            PRIVATE progpu_native_image)
        progpu_native_enable_warnings(
            progpu_native_image_module_tests)
        progpu_native_enable_sanitizers(
            progpu_native_image_module_tests)
        add_test(
            NAME progpu_native_image_module_tests
            COMMAND progpu_native_image_module_tests)
        set_tests_properties(
            progpu_native_image_module_tests
            PROPERTIES TIMEOUT 60)

        add_executable(progpu_native_scene_builder_module_tests
            tests/progpu_native_scene_builder_module_tests.cpp)
        target_link_libraries(
            progpu_native_scene_builder_module_tests
            PRIVATE progpu_native_scene_builder)
        progpu_native_enable_warnings(
            progpu_native_scene_builder_module_tests)
        progpu_native_enable_sanitizers(
            progpu_native_scene_builder_module_tests)
        add_test(
            NAME progpu_native_scene_builder_module_tests
            COMMAND progpu_native_scene_builder_module_tests)
        set_tests_properties(
            progpu_native_scene_builder_module_tests
            PROPERTIES TIMEOUT 60)

        add_executable(progpu_native_text_module_tests
            tests/progpu_native_text_module_tests.cpp)
        target_link_libraries(
            progpu_native_text_module_tests
            PRIVATE progpu_native_text)
        progpu_native_enable_warnings(
            progpu_native_text_module_tests)
        progpu_native_enable_sanitizers(
            progpu_native_text_module_tests)
        add_test(
            NAME progpu_native_text_module_tests
            COMMAND progpu_native_text_module_tests)
        set_tests_properties(
            progpu_native_text_module_tests
            PROPERTIES TIMEOUT 60)
    endif()

    add_executable(progpu_native_compression_tests
        tests/progpu_native_compression_tests.cpp)
    target_link_libraries(progpu_native_compression_tests PRIVATE
        progpu_native_compression)
    progpu_native_enable_warnings(progpu_native_compression_tests)
    progpu_native_enable_sanitizers(progpu_native_compression_tests)
    add_test(
        NAME progpu_native_compression_tests
        COMMAND progpu_native_compression_tests)
    set_tests_properties(
        progpu_native_compression_tests PROPERTIES TIMEOUT 60)

    add_executable(progpu_native_image_tests
        tests/progpu_native_image_tests.cpp)
    target_link_libraries(progpu_native_image_tests PRIVATE
        progpu_native_image)
    progpu_native_enable_warnings(progpu_native_image_tests)
    progpu_native_enable_sanitizers(progpu_native_image_tests)
    add_test(
        NAME progpu_native_image_tests
        COMMAND progpu_native_image_tests)
    set_tests_properties(
        progpu_native_image_tests PROPERTIES TIMEOUT 60)

    add_executable(progpu_native_text_tests
        tests/progpu_native_text_tests.cpp)
    target_link_libraries(progpu_native_text_tests PRIVATE
        progpu_native_text)
    target_compile_definitions(progpu_native_text_tests PRIVATE
        "PROGPU_NATIVE_TEST_INTER_FONT=\"${CMAKE_CURRENT_SOURCE_DIR}/../ProGPU.Fonts.Inter/Fonts/Inter-Medium.ttf\""
        "PROGPU_NATIVE_TEST_INTER_VARIABLE_FONT=\"${CMAKE_CURRENT_SOURCE_DIR}/../ProGPU.Fonts.Inter/Fonts/InterVariable.ttf\""
        "PROGPU_NATIVE_TEST_NOTO_CFF_FONT=\"${CMAKE_CURRENT_SOURCE_DIR}/../ProGPU.Fonts.Noto/Fonts/NotoSansCJKjp-Regular.otf\""
        "PROGPU_NATIVE_TEST_UNICODE_NORMALIZATION_DATA=\"${CMAKE_CURRENT_SOURCE_DIR}/../ProGPU.Text/UnicodeNormalizationData.bin\"")
    progpu_native_enable_warnings(progpu_native_text_tests)
    progpu_native_enable_sanitizers(progpu_native_text_tests)
    add_test(NAME progpu_native_text_tests COMMAND progpu_native_text_tests)
    set_tests_properties(progpu_native_text_tests PROPERTIES TIMEOUT 60)

    add_executable(progpu_native_internal_tests
        tests/progpu_native_internal_tests.cpp
        tests/progpu_native_scene_builder_tests.cpp
        tests/progpu_native_scene_builder_tests.hpp
        tests/progpu_native_semantic_brush_tests.cpp
        tests/progpu_native_semantic_brush_tests.hpp
        tests/progpu_native_semantic_image_tests.cpp
        tests/progpu_native_semantic_image_tests.hpp
        tests/progpu_native_semantic_layer_mask_tests.cpp
        tests/progpu_native_semantic_layer_mask_tests.hpp
        src/Backend/progpu_native_draw_state.cpp
        src/Backend/progpu_native_draw_state.hpp
        src/Backend/progpu_native_effect_plan.cpp
        src/Backend/progpu_native_effect_plan.hpp
        src/Backend/progpu_native_geometry_analytic.hpp
        src/Backend/progpu_native_geometry_base.hpp
        src/Backend/progpu_native_geometry_dash.hpp
        src/Backend/progpu_native_geometry_spline.hpp
        src/Backend/progpu_native_geometry_stroke.hpp
        src/Backend/progpu_native_gpu_records.hpp
        src/Scene/progpu_native_scene.cpp
        src/Scene/progpu_native_scene.hpp
        src/Scene/progpu_native_semantic_budget.hpp
        src/Scene/progpu_native_semantic_brush.cpp
        src/Scene/progpu_native_semantic_brush.hpp
        src/Scene/progpu_native_semantic_color_glyph.cpp
        src/Scene/progpu_native_semantic_color_glyph.hpp
        src/Scene/progpu_native_semantic_image.cpp
        src/Scene/progpu_native_semantic_image.hpp
        src/Scene/progpu_native_semantic_identity.cpp
        src/Scene/progpu_native_semantic_identity.hpp
        src/Scene/progpu_native_semantic_layer_mask.hpp
        src/Scene/progpu_native_semantic_text_style.cpp
        src/Scene/progpu_native_semantic_text_style.hpp
        src/Scene/progpu_native_semantic_effect_cache.cpp
        src/Scene/progpu_native_semantic_effect_cache.hpp
        src/Scene/progpu_native_semantic_replay.hpp
        src/Scene/progpu_native_semantic_state.cpp
        src/Scene/progpu_native_semantic_state.hpp
        src/Scene/progpu_native_semantic_validation.hpp)
    target_compile_features(progpu_native_internal_tests PRIVATE cxx_std_20)
    target_include_directories(progpu_native_internal_tests PRIVATE
        "${CMAKE_CURRENT_SOURCE_DIR}/include"
        ${PROGPU_NATIVE_INTERNAL_INCLUDE_DIRECTORIES})
    target_link_libraries(progpu_native_internal_tests PRIVATE
        progpu_native_scene_builder
        progpu_native_text)
    progpu_native_enable_warnings(progpu_native_internal_tests)
    progpu_native_enable_sanitizers(progpu_native_internal_tests)
    add_test(
        NAME progpu_native_internal_tests
        COMMAND progpu_native_internal_tests)
    set_tests_properties(
        progpu_native_internal_tests PROPERTIES TIMEOUT 60)

    if(PROGPU_NATIVE_BUILD_WGPU_TARGET)
    add_executable(progpu_native_tests
        tests/progpu_native_tests.cpp
        src/Scene/progpu_native_scene.cpp
        src/Scene/progpu_native_scene.hpp
        src/Scene/progpu_native_semantic_brush.cpp
        src/Scene/progpu_native_semantic_brush.hpp
        src/Scene/progpu_native_semantic_color_glyph.cpp
        src/Scene/progpu_native_semantic_color_glyph.hpp
        src/Scene/progpu_native_semantic_layer_mask.cpp
        src/Scene/progpu_native_semantic_layer_mask.hpp
        src/Scene/progpu_native_semantic_text_style.cpp
        src/Scene/progpu_native_semantic_text_style.hpp
        src/Scene/progpu_native_semantic_state.cpp
        src/Scene/progpu_native_semantic_state.hpp
        src/Scene/progpu_native_semantic_validation.cpp
        src/Scene/progpu_native_semantic_validation.hpp)
    target_compile_features(progpu_native_tests PRIVATE cxx_std_20)
    target_include_directories(progpu_native_tests PRIVATE
        "${CMAKE_CURRENT_SOURCE_DIR}/include"
        ${PROGPU_NATIVE_INTERNAL_INCLUDE_DIRECTORIES})
    target_link_libraries(progpu_native_tests PRIVATE progpu_native)
    progpu_native_enable_warnings(progpu_native_tests)
    progpu_native_enable_sanitizers(progpu_native_tests)
    add_test(NAME progpu_native_tests COMMAND progpu_native_tests)
    set_tests_properties(progpu_native_tests PROPERTIES TIMEOUT 60)
    endif()
endif()

if(PROGPU_NATIVE_BUILD_SAMPLE AND NOT EMSCRIPTEN AND PROGPU_NATIVE_BUILD_WGPU_TARGET)
    add_executable(progpu_native_sample
        samples/progpu_native_sample.cpp
        samples/progpu_native_sample_font.cpp)
    target_compile_features(progpu_native_sample PRIVATE cxx_std_20)
    target_include_directories(progpu_native_sample PRIVATE
        "${CMAKE_CURRENT_SOURCE_DIR}/include")
    target_include_directories(progpu_native_sample SYSTEM PRIVATE
        "${PROGPU_NATIVE_WEBGPU_INCLUDE_DIR}")
    target_link_libraries(progpu_native_sample PRIVATE
        progpu_native
        progpu_native_scene_builder
        progpu_native_text
        "${PROGPU_NATIVE_WEBGPU_LIBRARY}")
    progpu_native_enable_warnings(progpu_native_sample)
    if(APPLE OR UNIX)
        set_target_properties(progpu_native_sample PROPERTIES
            BUILD_RPATH "${webgpu_library_directory}")
    endif()
endif()

if(NOT EMSCRIPTEN)
    install(TARGETS
        progpu_native_compression
        progpu_native_image
        progpu_native_text
        progpu_native_scene_builder
        ARCHIVE DESTINATION "${CMAKE_INSTALL_LIBDIR}")
    install(FILES
        include/progpu_native.h
        include/progpu_native_compression.hpp
        include/progpu_native_image.hpp
        include/progpu_native_scene_builder.hpp
        include/progpu_native_text.hpp
        DESTINATION "${CMAKE_INSTALL_INCLUDEDIR}")
    install(FILES
        src/Compression/progpu_native_compression.cppm
        src/Image/progpu_native_image.cppm
        src/Scene/Builder/progpu_native_scene_builder.cppm
        src/Text/progpu_native_text.cppm
        DESTINATION "${CMAKE_INSTALL_DATADIR}/progpu/modules")
    install(FILES cmake/ProGPUNativeConfig.cmake
        DESTINATION "${CMAKE_INSTALL_LIBDIR}/cmake/ProGPUNative")
endif()
