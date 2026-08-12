if(NOT DEFINED INPUT OR NOT EXISTS "${INPUT}")
    message(FATAL_ERROR "EmbedShader requires an existing INPUT file")
endif()
if(NOT DEFINED OUTPUT)
    message(FATAL_ERROR "EmbedShader requires OUTPUT")
endif()
if(NOT DEFINED SYMBOL)
    set(SYMBOL "vector_wgsl")
endif()

file(READ "${INPUT}" shader_hex HEX)
string(REGEX MATCHALL ".." shader_bytes "${shader_hex}")
set(shader_initializer "")
set(column 0)
foreach(byte IN LISTS shader_bytes)
    string(APPEND shader_initializer "0x${byte},")
    math(EXPR column "${column} + 1")
    if(column EQUAL 16)
        string(APPEND shader_initializer "\n")
        set(column 0)
    endif()
endforeach()
string(APPEND shader_initializer "0x00")

get_filename_component(output_directory "${OUTPUT}" DIRECTORY)
file(MAKE_DIRECTORY "${output_directory}")
file(WRITE "${OUTPUT}"
"#pragma once\n"
"#include <cstddef>\n"
"namespace progpu::native::generated {\n"
"inline constexpr unsigned char ${SYMBOL}[] = {\n${shader_initializer}\n};\n"
"inline constexpr std::size_t ${SYMBOL}_size = sizeof(${SYMBOL}) - 1U;\n"
"}\n")
