# Optional private dependency of the product EDIT classifier. There is no system
# find_package, loader fallback or network download. The capability fails closed
# for dependency-requiring source when this exact archive was not selected.
option(PROGPU_NATIVE_EDIT_WORD_ICU "Require the reviewed private EDIT ICU dependency" OFF)
set(PROGPU_NATIVE_EDIT_WORD_ICU_SOURCE_ARCHIVE "" CACHE FILEPATH
    "Exact official ICU78.3 source archive from eng/native-edit-word-icu.json")
if(NOT PROGPU_NATIVE_EDIT_WORD_ICU_SOURCE_ARCHIVE)
    if(PROGPU_NATIVE_EDIT_WORD_ICU)
        message(FATAL_ERROR "EDIT ICU was requested without its reviewed source archive")
    endif()
    return()
endif()
set(_edit_processor "${CMAKE_SYSTEM_PROCESSOR}")
if(CMAKE_CXX_COMPILER_TARGET MATCHES "^aarch64")
    set(_edit_processor arm64)
elseif(CMAKE_CXX_COMPILER_TARGET MATCHES "^x86_64")
    set(_edit_processor x64)
elseif(CMAKE_GENERATOR_PLATFORM)
    set(_edit_processor "${CMAKE_GENERATOR_PLATFORM}")
endif()
if(APPLE AND CMAKE_OSX_ARCHITECTURES)
    set(_edit_processor "${CMAKE_OSX_ARCHITECTURES}")
endif()
string(TOLOWER "${_edit_processor}" _edit_processor)
if(_edit_processor MATCHES "^(arm64|aarch64)$")
    set(_edit_arch arm64)
elseif(_edit_processor MATCHES "^(x64|amd64|x86_64)$")
    set(_edit_arch x64)
else()
    message(FATAL_ERROR "EDIT ICU requires one admitted x64 or ARM64 target")
endif()
if(WIN32)
    set(_edit_os win)
elseif(APPLE)
    set(_edit_os osx)
else()
    set(_edit_os linux)
endif()
set(PROGPU_NATIVE_EDIT_WORD_ICU_RID "${_edit_os}-${_edit_arch}")
if(EMSCRIPTEN OR NOT CMAKE_SIZEOF_VOID_P EQUAL 8 OR
   NOT CMAKE_SYSTEM_NAME MATCHES "^(Darwin|Linux|Windows)$" OR
   NOT CMAKE_CXX_BYTE_ORDER STREQUAL "LITTLE_ENDIAN")
    message(FATAL_ERROR "Private EDIT ICU requires a little-endian 64-bit desktop target")
endif()
get_filename_component(_edit_repository "${CMAKE_CURRENT_LIST_DIR}/../../.." ABSOLUTE)
set(_edit_pin_path "${_edit_repository}/eng/native-edit-word-icu.json")
file(READ "${_edit_pin_path}" _edit_pin)
string(JSON _edit_archive_hash GET "${_edit_pin}" sourceSha256)
file(SHA256 "${PROGPU_NATIVE_EDIT_WORD_ICU_SOURCE_ARCHIVE}" _edit_actual_hash)
if(NOT _edit_actual_hash STREQUAL _edit_archive_hash)
    message(FATAL_ERROR "EDIT ICU source is not the reviewed official release archive")
endif()
set(_edit_extract "${CMAKE_CURRENT_BINARY_DIR}/edit-word-icu-source")
file(MAKE_DIRECTORY "${_edit_extract}")
file(ARCHIVE_EXTRACT INPUT "${PROGPU_NATIVE_EDIT_WORD_ICU_SOURCE_ARCHIVE}"
    DESTINATION "${_edit_extract}")
set(_edit_source "${_edit_extract}/icu/source")
string(JSON _edit_license_hash GET "${_edit_pin}" licenseSha256)
file(SHA256 "${_edit_extract}/icu/LICENSE" _edit_actual_license_hash)
if(NOT _edit_actual_license_hash STREQUAL _edit_license_hash)
    message(FATAL_ERROR "EDIT ICU original redistribution notices changed")
endif()
find_package(Python3 REQUIRED COMPONENTS Interpreter)
set(_edit_embedded_data "${CMAKE_CURRENT_BINARY_DIR}/progpu_edit_icu_data.cpp")
execute_process(COMMAND "${Python3_EXECUTABLE}"
    "${_edit_repository}/eng/progpu-embed-edit-word-icu-data.py"
    --pin "${_edit_pin_path}" --data "${_edit_source}/data/in/icudt78l.dat"
    --output "${_edit_embedded_data}"
    RESULT_VARIABLE _edit_embed_result)
if(NOT _edit_embed_result EQUAL 0)
    message(FATAL_ERROR "EDIT ICU original release data failed admission")
endif()

# Use the upstream release's own common-library compilation inventory, not a
# hand-copied algorithm or an ambient ICU binary. BreakIterator lives in common;
# i18n, formatting, collation and transliteration libraries are unnecessary.
file(STRINGS "${_edit_source}/common/sources.txt" _edit_common_names)
set(_edit_common_sources)
foreach(_edit_name IN LISTS _edit_common_names)
    if(NOT _edit_name MATCHES "^[a-zA-Z0-9_]+[.]cpp$")
        message(FATAL_ERROR "Unexpected ICU common-library source inventory")
    endif()
    list(APPEND _edit_common_sources "${_edit_source}/common/${_edit_name}")
endforeach()
add_library(progpu_native_edit_icu STATIC ${_edit_common_sources}
    "${_edit_source}/stubdata/stubdata.cpp" "${_edit_embedded_data}")
target_compile_features(progpu_native_edit_icu PRIVATE cxx_std_20)
target_include_directories(progpu_native_edit_icu SYSTEM PUBLIC
    "${_edit_source}/common")
target_compile_definitions(progpu_native_edit_icu PUBLIC
    U_STATIC_IMPLEMENTATION=1 U_LIB_SUFFIX_C_NAME=_progpu_edit
    UCONFIG_NO_FILE_IO=1 UCONFIG_NO_FORMATTING=1 UCONFIG_NO_COLLATION=1
    UCONFIG_NO_TRANSLITERATION=1 UCONFIG_NO_REGULAR_EXPRESSIONS=1
    UCONFIG_NO_IDNA=1 UCONFIG_NO_LEGACY_CONVERSION=1
    UCONFIG_NO_FILTERED_BREAK_ITERATION=1)
target_compile_definitions(progpu_native_edit_icu PRIVATE U_COMMON_IMPLEMENTATION=1)
set_target_properties(progpu_native_edit_icu PROPERTIES
    POSITION_INDEPENDENT_CODE ON CXX_VISIBILITY_PRESET hidden
    VISIBILITY_INLINES_HIDDEN YES INTERPROCEDURAL_OPTIMIZATION OFF)
find_package(Threads REQUIRED)
target_link_libraries(progpu_native_edit_icu PRIVATE Threads::Threads ${CMAKE_DL_LIBS})
if(UNIX AND NOT APPLE)
    set_property(TARGET progpu_native_edit_icu PROPERTY INTERFACE_LINK_OPTIONS
        "LINKER:--exclude-libs,libprogpu_native_edit_icu.a")
elseif(APPLE)
    set_property(TARGET progpu_native_edit_icu PROPERTY INTERFACE_LINK_OPTIONS
        "LINKER:-load_hidden,$<TARGET_FILE:progpu_native_edit_icu>")
endif()
# Record only an actual successfully archived target; the verifier checks every
# object architecture and original compilation input before publishing a receipt.
add_custom_command(TARGET progpu_native_edit_icu POST_BUILD
    COMMAND "${Python3_EXECUTABLE}" "${_edit_repository}/eng/progpu-edit-word-icu-dependency.py" record
        --source-archive "${PROGPU_NATIVE_EDIT_WORD_ICU_SOURCE_ARCHIVE}"
        --build-directory "${CMAKE_CURRENT_BINARY_DIR}"
        --library "$<TARGET_FILE:progpu_native_edit_icu>"
        --rid "${PROGPU_NATIVE_EDIT_WORD_ICU_RID}" --configuration "$<CONFIG>"
        --compiler-id "${CMAKE_CXX_COMPILER_ID}" --compiler-version "${CMAKE_CXX_COMPILER_VERSION}"
        "--compiler-target=${CMAKE_CXX_COMPILER_TARGET}"
    VERBATIM)
set(PROGPU_NATIVE_EDIT_WORD_ICU_NOTICES "${_edit_extract}/icu/LICENSE")
