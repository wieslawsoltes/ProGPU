cmake_minimum_required(VERSION 3.28)
project(ProGPUFontDependencyProbe LANGUAGES CXX)

# Never search an ambient FreeType installation or select a shared substitute.
if(NOT IS_ABSOLUTE "${PROGPU_FREETYPE_LIBRARY}" OR
   NOT EXISTS "${PROGPU_FREETYPE_LIBRARY}" OR
   NOT IS_ABSOLUTE "${PROGPU_FREETYPE_INCLUDE}" OR
   NOT EXISTS "${PROGPU_FREETYPE_INCLUDE}/ft2build.h")
    message(FATAL_ERROR "The probe requires the exact prepared static library and headers")
endif()
add_executable(progpu_font_dependency_probe probe.cpp)
target_compile_features(progpu_font_dependency_probe PRIVATE cxx_std_20)
set_target_properties(progpu_font_dependency_probe PROPERTIES
    CXX_EXTENSIONS OFF
    INTERPROCEDURAL_OPTIMIZATION OFF)
target_include_directories(progpu_font_dependency_probe PRIVATE "${PROGPU_FREETYPE_INCLUDE}")
target_link_libraries(progpu_font_dependency_probe PRIVATE "${PROGPU_FREETYPE_LIBRARY}")
if(MSVC)
    target_compile_options(progpu_font_dependency_probe PRIVATE /W4 /WX)
else()
    target_compile_options(progpu_font_dependency_probe PRIVATE -Wall -Wextra -Wpedantic -Werror)
endif()
