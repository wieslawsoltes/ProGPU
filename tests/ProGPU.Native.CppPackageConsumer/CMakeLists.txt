cmake_minimum_required(VERSION 3.28)
project(ProGPUNativeCppPackageConsumer LANGUAGES CXX)

find_package(ProGPUNative CONFIG REQUIRED)

add_executable(progpu_native_cpp_package_consumer main.cpp)
target_compile_features(progpu_native_cpp_package_consumer PRIVATE cxx_std_20)
target_link_libraries(progpu_native_cpp_package_consumer
    PRIVATE
        ProGPU::native_hit_testing
        ProGPU::native_scene_builder)
