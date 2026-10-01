cmake_minimum_required(VERSION 3.28)
project(ProGPUEditWordBoundaryContracts LANGUAGES CXX)
enable_testing()
find_package(Threads REQUIRED)
get_filename_component(_repository "${CMAKE_CURRENT_LIST_DIR}/../.." ABSOLUTE)
set(_native "${_repository}/src/ProGPU.Native")
include("${_native}/cmake/ProGPUEditWordIcu.cmake")
add_executable(progpu_native_edit_word_boundary_tests
    "${_native}/tests/progpu_native_edit_word_boundary_tests.cpp"
    "${_native}/tests/progpu_native_edit_word_interop_tests.cpp"
    "${_native}/tests/progpu_native_edit_myanmar_tests.cpp"
    "${_native}/src/Text/Interop/progpu_native_edit_word_interop.cpp"
    "${_native}/src/Text/progpu_native_edit_word_boundaries.cpp"
    "${_native}/src/Text/progpu_native_unicode.cpp"
    "${_native}/src/Text/Shaping/progpu_native_unicode_shaping.cpp"
    "${_native}/src/Text/Shaping/progpu_native_unicode_syllables.cpp"
    "${_native}/src/Text/Unicode/progpu_native_unicode_category.cpp"
    "${_native}/src/Text/Unicode/progpu_native_unicode_directional.cpp"
    "${_native}/src/Text/Bidi/progpu_native_unicode_bidi.cpp"
    "${_native}/src/Text/progpu_native_unicode_grapheme.cpp"
    "${_native}/src/Text/progpu_native_unicode_line_break.cpp")
target_compile_features(progpu_native_edit_word_boundary_tests PRIVATE cxx_std_20)
set_target_properties(progpu_native_edit_word_boundary_tests PROPERTIES CXX_EXTENSIONS OFF)
target_compile_definitions(progpu_native_edit_word_boundary_tests PRIVATE PROGPU_NATIVE_BUILD=1)
target_include_directories(progpu_native_edit_word_boundary_tests PRIVATE
    "${_native}/include" "${_native}/src/Text")
target_link_libraries(progpu_native_edit_word_boundary_tests PRIVATE Threads::Threads)
if(MSVC)
    target_compile_options(progpu_native_edit_word_boundary_tests PRIVATE /W4 /WX)
else()
    target_compile_options(progpu_native_edit_word_boundary_tests PRIVATE
        -Wall -Wextra -Wpedantic -Werror)
endif()
if(TARGET progpu_native_edit_icu)
    target_compile_definitions(progpu_native_edit_word_boundary_tests PRIVATE PROGPU_NATIVE_EDIT_WORD_ICU=1)
    target_link_libraries(progpu_native_edit_word_boundary_tests PRIVATE
        progpu_native_edit_icu ${CMAKE_DL_LIBS})
endif()
add_test(NAME progpu_native_edit_word_boundary_tests COMMAND progpu_native_edit_word_boundary_tests)
set_tests_properties(progpu_native_edit_word_boundary_tests PROPERTIES TIMEOUT 60)
