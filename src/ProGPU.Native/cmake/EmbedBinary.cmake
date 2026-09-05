if(NOT DEFINED INPUT OR NOT EXISTS "${INPUT}")
    message(FATAL_ERROR "EmbedBinary requires an existing INPUT file")
endif()
if(NOT DEFINED OUTPUT)
    message(FATAL_ERROR "EmbedBinary requires OUTPUT")
endif()
if(NOT DEFINED SYMBOL)
    message(FATAL_ERROR "EmbedBinary requires SYMBOL")
endif()

file(READ "${INPUT}" binary_hex HEX)
string(REGEX REPLACE
    "([0-9a-f][0-9a-f])" "0x\\1," binary_initializer "${binary_hex}")
set(binary_row_pattern "")
foreach(index RANGE 1 16)
    string(APPEND binary_row_pattern "0x[0-9a-f][0-9a-f],")
endforeach()
string(REGEX REPLACE
    "(${binary_row_pattern})" "\\1\n" binary_initializer
    "${binary_initializer}")

get_filename_component(output_directory "${OUTPUT}" DIRECTORY)
file(MAKE_DIRECTORY "${output_directory}")
file(WRITE "${OUTPUT}"
"#include <cstddef>\n"
"namespace progpu::native::generated {\n"
"alignas(4) extern const unsigned char ${SYMBOL}[] = {\n"
"${binary_initializer}\n};\n"
"extern const std::size_t ${SYMBOL}_size = sizeof(${SYMBOL});\n"
"}\n")
