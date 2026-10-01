cmake_minimum_required(VERSION 3.20)
project(ProGpuGlyphCoverageFrameContracts LANGUAGES CXX)

# Device-free private pass-certificate controls; no renderer/SDK/GPU linkage.
enable_testing()
add_executable(progpu_glyph_coverage_frame_tests
    ../../src/ProGPU.Native/tests/progpu_native_glyph_coverage_frame_tests.cpp)
target_compile_features(progpu_glyph_coverage_frame_tests PRIVATE cxx_std_20)
target_include_directories(progpu_glyph_coverage_frame_tests PRIVATE
    ../../src/ProGPU.Native/include
    ../../src/ProGPU.Native/src/Backend)
if(MSVC)
    target_compile_options(progpu_glyph_coverage_frame_tests PRIVATE /W4 /WX)
else()
    target_compile_options(progpu_glyph_coverage_frame_tests PRIVATE
        -Wall -Wextra -Wpedantic -Werror)
endif()
add_test(NAME progpu_glyph_coverage_frame_tests
    COMMAND progpu_glyph_coverage_frame_tests)
set_tests_properties(progpu_glyph_coverage_frame_tests PROPERTIES TIMEOUT 15)
