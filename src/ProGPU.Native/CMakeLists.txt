cmake_minimum_required(VERSION 3.22)
project(ProGPUNative LANGUAGES CXX)

include(CTest)

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
set(PROGPU_NATIVE_CLIP_COMPOSE_SHADER
    "${CMAKE_CURRENT_SOURCE_DIR}/../ProGPU.Backend/Shaders/ClipCompose.wgsl"
    CACHE FILEPATH "Shared ProGPU retained clip-composition shader source")
set(PROGPU_NATIVE_GAUSSIAN_BLUR_HORIZONTAL_SHADER
    "${CMAKE_CURRENT_SOURCE_DIR}/../ProGPU.Compute/Shaders/GaussianBlurHorizontal.wgsl"
    CACHE FILEPATH "Shared ProGPU horizontal Gaussian-blur shader source")
set(PROGPU_NATIVE_GAUSSIAN_BLUR_VERTICAL_SHADER
    "${CMAKE_CURRENT_SOURCE_DIR}/../ProGPU.Compute/Shaders/GaussianBlurVertical.wgsl"
    CACHE FILEPATH "Shared ProGPU vertical Gaussian-blur shader source")
option(PROGPU_NATIVE_BUILD_SAMPLE "Build the live headless WebGPU sample" ON)
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
if(NOT EXISTS "${PROGPU_NATIVE_CLIP_COMPOSE_SHADER}")
    message(FATAL_ERROR "The shared ProGPU ClipCompose.wgsl source is missing")
endif()
if(NOT EXISTS "${PROGPU_NATIVE_GAUSSIAN_BLUR_HORIZONTAL_SHADER}")
    message(FATAL_ERROR "The shared ProGPU horizontal Gaussian-blur shader source is missing")
endif()
if(NOT EXISTS "${PROGPU_NATIVE_GAUSSIAN_BLUR_VERTICAL_SHADER}")
    message(FATAL_ERROR "The shared ProGPU vertical Gaussian-blur shader source is missing")
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

add_library(progpu_native SHARED
    src/progpu_native.cpp
    src/progpu_native_geometry.hpp
    src/progpu_webgpu_compat.hpp
    include/progpu_native.h
    "${generated_vector_shader}"
    "${generated_path_rasterizer_shader}"
    "${generated_glyph_rasterizer_shader}"
    "${generated_text_shader}"
    "${generated_texture_shader}"
    "${generated_clip_compose_shader}"
    "${generated_gaussian_blur_horizontal_shader}"
    "${generated_gaussian_blur_vertical_shader}")
target_compile_features(progpu_native PRIVATE cxx_std_20)
target_compile_definitions(progpu_native PRIVATE PROGPU_NATIVE_BUILD=1)
target_include_directories(progpu_native
    PUBLIC "${CMAKE_CURRENT_SOURCE_DIR}/include"
    PRIVATE
        "${CMAKE_CURRENT_SOURCE_DIR}/src"
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

if(PROGPU_NATIVE_DAWN_WEBGPU_INCLUDE_DIR)
    if(NOT EXISTS "${PROGPU_NATIVE_DAWN_WEBGPU_INCLUDE_DIR}/webgpu.h")
        message(FATAL_ERROR
            "PROGPU_NATIVE_DAWN_WEBGPU_INCLUDE_DIR must contain webgpu.h")
    endif()
    add_library(progpu_native_dawn SHARED
        src/progpu_native.cpp
        src/progpu_native_geometry.hpp
        src/progpu_webgpu_compat.hpp
        include/progpu_native.h
        include/progpu_native_dawn.h
        "${generated_vector_shader}"
        "${generated_path_rasterizer_shader}"
        "${generated_glyph_rasterizer_shader}"
        "${generated_text_shader}"
        "${generated_texture_shader}"
        "${generated_clip_compose_shader}"
        "${generated_gaussian_blur_horizontal_shader}"
        "${generated_gaussian_blur_vertical_shader}")
    target_compile_features(progpu_native_dawn
        PRIVATE cxx_std_20)
    target_compile_definitions(progpu_native_dawn PRIVATE
        PROGPU_NATIVE_BUILD=1
        PROGPU_NATIVE_DAWN_ABI=1)
    target_include_directories(progpu_native_dawn
        PUBLIC "${CMAKE_CURRENT_SOURCE_DIR}/include"
        PRIVATE
        "${CMAKE_CURRENT_SOURCE_DIR}/src"
        "${generated_directory}")
    target_include_directories(progpu_native_dawn SYSTEM PRIVATE
        "${PROGPU_NATIVE_DAWN_WEBGPU_INCLUDE_DIR}")
    progpu_native_enable_warnings(progpu_native_dawn)
    progpu_native_enable_sanitizers(progpu_native_dawn)
    set_target_properties(progpu_native_dawn PROPERTIES
        CXX_VISIBILITY_PRESET hidden
        VISIBILITY_INLINES_HIDDEN YES)
    if(UNIX AND NOT APPLE)
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
                tests/progpu_native_webscene_provider_tests.cpp)
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
                "${PROGPU_NATIVE_IOSURFACE_FRAMEWORK}"
                "${CMAKE_DL_LIBS}")
            progpu_native_enable_warnings(
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

if(BUILD_TESTING)
    add_executable(progpu_native_tests
        tests/progpu_native_tests.cpp)
    target_compile_features(progpu_native_tests PRIVATE cxx_std_20)
    target_include_directories(progpu_native_tests PRIVATE
        "${CMAKE_CURRENT_SOURCE_DIR}/include"
        "${CMAKE_CURRENT_SOURCE_DIR}/src")
    target_link_libraries(progpu_native_tests PRIVATE progpu_native)
    progpu_native_enable_warnings(progpu_native_tests)
    progpu_native_enable_sanitizers(progpu_native_tests)
    add_test(NAME progpu_native_tests COMMAND progpu_native_tests)
    set_tests_properties(progpu_native_tests PROPERTIES TIMEOUT 60)
endif()

if(PROGPU_NATIVE_BUILD_SAMPLE)
    add_executable(progpu_native_sample
        samples/progpu_native_sample.cpp)
    target_compile_features(progpu_native_sample PRIVATE cxx_std_20)
    target_include_directories(progpu_native_sample PRIVATE
        "${CMAKE_CURRENT_SOURCE_DIR}/include")
    target_include_directories(progpu_native_sample SYSTEM PRIVATE
        "${PROGPU_NATIVE_WEBGPU_INCLUDE_DIR}")
    target_link_libraries(progpu_native_sample PRIVATE
        progpu_native
        "${PROGPU_NATIVE_WEBGPU_LIBRARY}")
    progpu_native_enable_warnings(progpu_native_sample)
    if(APPLE OR UNIX)
        set_target_properties(progpu_native_sample PROPERTIES
            BUILD_RPATH "${webgpu_library_directory}")
    endif()
endif()
