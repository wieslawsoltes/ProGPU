module;

#include "progpu_native_scene_builder.hpp"

export module progpu.native.scene_builder;

export namespace progpu::native {

using ::progpu::native::scene_build_error;
using ::progpu::native::scene_build_metrics;
using ::progpu::native::shaped_text_scene_options;
using ::progpu::native::semantic_scene_builder;

using ::progpu_native_point_3d;
using ::progpu_native_float_4;
using ::progpu_native_matrix_4x4;
using ::progpu_native_scene_camera_3d;
using ::progpu_native_scene_line_3d;
using ::progpu_native_scene_mesh_3d_vertex;
using ::progpu_native_scene_mesh_3d;

} // namespace progpu::native
