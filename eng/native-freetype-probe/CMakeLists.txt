cmake_minimum_required(VERSION 3.28)
project(ProGPUFontDependencyProbe LANGUAGES CXX)
find_package(Threads REQUIRED)

# Never search an ambient FreeType installation or select a shared substitute.
if(NOT IS_ABSOLUTE "${PROGPU_FREETYPE_LIBRARY}" OR
   NOT EXISTS "${PROGPU_FREETYPE_LIBRARY}" OR
   NOT IS_ABSOLUTE "${PROGPU_FREETYPE_INCLUDE}" OR
   NOT EXISTS "${PROGPU_FREETYPE_INCLUDE}/ft2build.h")
    message(FATAL_ERROR "The probe requires the exact prepared static library and headers")
endif()
function(configure_font_target target)
    target_compile_features(${target} PRIVATE cxx_std_20)
    set_target_properties(${target} PROPERTIES CXX_EXTENSIONS OFF INTERPROCEDURAL_OPTIMIZATION OFF)
    target_include_directories(${target} PRIVATE "${PROGPU_FREETYPE_INCLUDE}")
    target_link_libraries(${target} PRIVATE "${PROGPU_FREETYPE_LIBRARY}")
    if(MSVC)
        target_compile_options(${target} PRIVATE /W4 /WX)
    else()
        target_compile_options(${target} PRIVATE -Wall -Wextra -Wpedantic -Werror)
    endif()
endfunction()

add_executable(progpu_font_dependency_probe probe.cpp)
configure_font_target(progpu_font_dependency_probe)

set(native_root "${CMAKE_CURRENT_LIST_DIR}/../../src/ProGPU.Native")
add_library(progpu_native_hinted_font STATIC
    "${native_root}/src/Text/Font/progpu_native_hinted_font.cpp"
    "${native_root}/src/Text/Font/progpu_native_hinted_font_cache.cpp"
    "${native_root}/src/Text/Font/progpu_native_hinted_transport.cpp")
set_target_properties(progpu_native_hinted_font PROPERTIES POSITION_INDEPENDENT_CODE ON)
target_include_directories(progpu_native_hinted_font PUBLIC "${native_root}/src/Text/Font" "${native_root}/include")
target_link_libraries(progpu_native_hinted_font PRIVATE Threads::Threads ${CMAKE_DL_LIBS})
configure_font_target(progpu_native_hinted_font)
add_executable(progpu_native_hinted_font_tests
    "${native_root}/tests/progpu_native_hinted_font_tests.cpp")
target_link_libraries(progpu_native_hinted_font_tests PRIVATE progpu_native_hinted_font)
configure_font_target(progpu_native_hinted_font_tests)
add_executable(progpu_native_hinted_transport_tests
    "${native_root}/tests/progpu_native_hinted_transport_tests.cpp")
target_link_libraries(progpu_native_hinted_transport_tests PRIVATE progpu_native_hinted_font)
configure_font_target(progpu_native_hinted_transport_tests)
add_executable(progpu_native_hinted_font_cache_tests
    "${native_root}/tests/progpu_native_hinted_font_cache_tests.cpp")
target_link_libraries(progpu_native_hinted_font_cache_tests PRIVATE progpu_native_hinted_font)
configure_font_target(progpu_native_hinted_font_cache_tests)
