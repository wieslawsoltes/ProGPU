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
string(REGEX MATCHALL ".." binary_bytes "${binary_hex}")
set(binary_initializer "")
set(column 0)
foreach(byte IN LISTS binary_bytes)
    string(APPEND binary_initializer "0x${byte},")
    math(EXPR column "${column} + 1")
    if(column EQUAL 16)
        string(APPEND binary_initializer "\n")
        set(column 0)
    endif()
endforeach()

get_filename_component(output_directory "${OUTPUT}" DIRECTORY)
file(MAKE_DIRECTORY "${output_directory}")
file(WRITE "${OUTPUT}"
"#include <cstddef>\n"
"namespace progpu::native::generated {\n"
"alignas(4) extern const unsigned char ${SYMBOL}[] = {\n"
"${binary_initializer}\n};\n"
"extern const std::size_t ${SYMBOL}_size = sizeof(${SYMBOL});\n"
"}\n")
