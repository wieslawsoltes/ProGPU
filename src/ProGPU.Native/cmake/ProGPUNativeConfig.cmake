# ProGPU native C++20 SDK imported targets.
# Supports both a regular CMake install and the ProGPU.Backend.Native NuGet
# layout. Module interface sources are shipped separately because compiled C++
# module artifacts are intentionally compiler-specific.

if(TARGET ProGPU::native_scene_builder)
    return()
endif()

get_filename_component(_progpu_native_root
    "${CMAKE_CURRENT_LIST_DIR}/../../.." ABSOLUTE)

if(EXISTS "${_progpu_native_root}/build/native/include")
    set(_progpu_native_include
        "${_progpu_native_root}/build/native/include")
    if(CMAKE_SYSTEM_PROCESSOR MATCHES "^(arm64|aarch64|ARM64)$")
        set(_progpu_native_arch "arm64")
    else()
        set(_progpu_native_arch "x64")
    endif()
    if(WIN32)
        set(_progpu_native_rid "win-${_progpu_native_arch}")
        set(_progpu_native_prefix "")
        set(_progpu_native_suffix ".lib")
    elseif(APPLE)
        set(_progpu_native_rid "osx-${_progpu_native_arch}")
        set(_progpu_native_prefix "lib")
        set(_progpu_native_suffix ".a")
    elseif(UNIX)
        set(_progpu_native_rid "linux-${_progpu_native_arch}")
        set(_progpu_native_prefix "lib")
        set(_progpu_native_suffix ".a")
    else()
        message(FATAL_ERROR "The ProGPU native C++ SDK does not support this platform.")
    endif()
    set(_progpu_native_library_dir
        "${_progpu_native_root}/runtimes/${_progpu_native_rid}/native/sdk")
else()
    set(_progpu_native_include "${_progpu_native_root}/include")
    set(_progpu_native_library_dir "${_progpu_native_root}/lib")
    if(WIN32)
        set(_progpu_native_prefix "")
        set(_progpu_native_suffix ".lib")
    else()
        set(_progpu_native_prefix "lib")
        set(_progpu_native_suffix ".a")
    endif()
endif()

function(_progpu_native_import component)
    set(_location
        "${_progpu_native_library_dir}/${_progpu_native_prefix}progpu_native_${component}${_progpu_native_suffix}")
    if(NOT EXISTS "${_location}")
        message(FATAL_ERROR
            "The ProGPU native C++ SDK library is missing: ${_location}")
    endif()
    add_library("ProGPU::native_${component}" STATIC IMPORTED)
    set_target_properties("ProGPU::native_${component}" PROPERTIES
        IMPORTED_LOCATION "${_location}"
        INTERFACE_INCLUDE_DIRECTORIES "${_progpu_native_include}"
        INTERFACE_COMPILE_FEATURES cxx_std_20)
endfunction()

_progpu_native_import(compression)
_progpu_native_import(image)
_progpu_native_import(text)
_progpu_native_import(scene_builder)

set_property(TARGET ProGPU::native_image PROPERTY
    INTERFACE_LINK_LIBRARIES ProGPU::native_compression)
set_property(TARGET ProGPU::native_text PROPERTY
    INTERFACE_LINK_LIBRARIES ProGPU::native_compression)
set_property(TARGET ProGPU::native_scene_builder PROPERTY
    INTERFACE_LINK_LIBRARIES ProGPU::native_text)

unset(_progpu_native_root)
unset(_progpu_native_include)
unset(_progpu_native_library_dir)
unset(_progpu_native_arch)
unset(_progpu_native_rid)
unset(_progpu_native_prefix)
unset(_progpu_native_suffix)
