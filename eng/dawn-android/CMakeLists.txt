cmake_minimum_required(VERSION 3.22)

project(ProGpuDawnAndroid LANGUAGES C CXX)

if(NOT DEFINED DAWN_SOURCE_DIR OR
   NOT EXISTS "${DAWN_SOURCE_DIR}/CMakeLists.txt")
  message(FATAL_ERROR "DAWN_SOURCE_DIR must identify the pinned Dawn checkout.")
endif()

# ProGPU packages one exact WebGPU C ABI shared object. Dawn and Tint remain
# statically linked implementation details so Android never has to resolve a
# private Dawn DSO graph or libc++_shared beside libwebgpu_dawn.so.
set(BUILD_SHARED_LIBS OFF CACHE BOOL "" FORCE)
set(DAWN_ENABLE_D3D11 OFF CACHE BOOL "" FORCE)
set(DAWN_ENABLE_D3D12 OFF CACHE BOOL "" FORCE)
set(DAWN_ENABLE_DESKTOP_GL OFF CACHE BOOL "" FORCE)
set(DAWN_ENABLE_INSTALL OFF CACHE BOOL "" FORCE)
set(DAWN_ENABLE_METAL OFF CACHE BOOL "" FORCE)
set(DAWN_ENABLE_NULL OFF CACHE BOOL "" FORCE)
set(DAWN_ENABLE_OPENGLES OFF CACHE BOOL "" FORCE)
set(DAWN_ENABLE_PIC ON CACHE BOOL "" FORCE)
set(DAWN_ENABLE_VULKAN ON CACHE BOOL "" FORCE)
set(DAWN_FETCH_DEPENDENCIES OFF CACHE BOOL "" FORCE)
set(DAWN_BUILD_ANDROID_SAMPLES OFF CACHE BOOL "" FORCE)
set(DAWN_BUILD_BENCHMARKS OFF CACHE BOOL "" FORCE)
set(DAWN_BUILD_NODE_BINDINGS OFF CACHE BOOL "" FORCE)
set(DAWN_BUILD_PROTOBUF OFF CACHE BOOL "" FORCE)
set(DAWN_BUILD_SAMPLES OFF CACHE BOOL "" FORCE)
set(DAWN_USE_GLFW OFF CACHE BOOL "" FORCE)
set(TINT_BUILD_BENCHMARKS OFF CACHE BOOL "" FORCE)
set(TINT_BUILD_CMD_TOOLS OFF CACHE BOOL "" FORCE)
set(TINT_BUILD_FUZZERS OFF CACHE BOOL "" FORCE)
set(TINT_BUILD_IR_BINARY OFF CACHE BOOL "" FORCE)
set(TINT_BUILD_TESTS OFF CACHE BOOL "" FORCE)

add_subdirectory("${DAWN_SOURCE_DIR}" dawn)

# webgpu_dawn is static in this build. Mark its generated C entry points as
# public before they are absorbed into the final shared object.
target_compile_definitions(webgpu_dawn PRIVATE WGPU_SHARED_LIBRARY)

add_library(progpu_webgpu_dawn SHARED WebGpuDawnLinkAnchor.cpp)
add_dependencies(progpu_webgpu_dawn webgpu_dawn)

# The whole archive supplies every generated wgpu* entry point. CMake then
# follows webgpu_dawn's static dependency graph to link only the required Dawn,
# Tint, SPIR-V, Vulkan-loader, and utility objects into the same DSO.
target_link_libraries(
  progpu_webgpu_dawn
  PRIVATE
    "-Wl,--whole-archive"
    webgpu_dawn
    "-Wl,--no-whole-archive"
    android
    log)

target_link_options(
  progpu_webgpu_dawn
  PRIVATE
    "-Wl,--version-script=${CMAKE_CURRENT_SOURCE_DIR}/WebGpuDawn.exports"
    "-Wl,-z,max-page-size=16384"
    "-Wl,-z,common-page-size=16384"
    "-Wl,-soname,libwebgpu_dawn.so")

set_target_properties(
  progpu_webgpu_dawn
  PROPERTIES
    OUTPUT_NAME webgpu_dawn
    LIBRARY_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/package")
