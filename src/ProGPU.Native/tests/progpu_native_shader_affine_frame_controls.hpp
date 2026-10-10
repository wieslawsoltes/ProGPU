#pragma once

// Authored controls; execution belongs to the final integrated tip.
inline bool source_affine_frame_arithmetic() {
    using namespace progpu::native::shader_effect;
    static_assert(sizeof(progpu_native_scene_shader_affine_frame) == 144U);
    static_assert(sizeof(progpu_native_scene_shader_effect_affine) == 720U);
    static_assert(offsetof(progpu_native_scene_shader_effect_affine, program) == 176U);
    sample_frame_request request{0,0,16,8,{0,0,1,1,32,16,1,-1}};
    sample_frame result{};
    UV_REQUIRE(create_affine_sample_frame(request,result));
    UV_REQUIRE(result.affine && (result.capture == sample_lattice{0,0,16,8}));
    UV_REQUIRE((result.output == sample_lattice{24,16,8,16}));
    UV_REQUIRE(result.unit_to_device.xy == 16 && result.unit_to_device.yx == -8);
    UV_REQUIRE(result.device_to_unit.xy == -.125F && result.device_to_unit.yx == .0625F);
    sample_projection projection{};
    UV_REQUIRE(project_affine_sample_frame(result,{8,8,64,32},projection));
    UV_REQUIRE(projection.unit_to_clip.xy == -1 && projection.unit_to_clip.yx == -.25F);
    request.source_to_device = {-1,1,1,1,32,16};
    UV_REQUIRE(create_affine_sample_frame(request,result));
    UV_REQUIRE((result.output == sample_lattice{16,16,16,8}));
    UV_REQUIRE(result.device_to_unit.x == -.0625F && result.device_to_unit.y == .125F);
    progpu_native_scene_shader_affine_frame wire{};
    wire.placement.local_right=16; wire.placement.local_bottom=8;
    wire.placement.source_scale_x=-1; wire.placement.source_scale_y=1;
    wire.placement.source_offset_x=32; wire.placement.source_offset_y=16;
    wire.placement.source_dpi_x=wire.placement.source_dpi_y=1;
    wire.placement.clip_right=wire.placement.clip_bottom=64;
    UV_REQUIRE(complete_affine_frame(wire) && validate_affine_frame(wire));
    UV_REQUIRE(!validate_sample_frame(wire.placement));
    const auto original=wire;
    wire.quad_m12=1;
    UV_REQUIRE(!validate_affine_frame(wire));
    wire=original; wire.source_m21=std::numeric_limits<float>::infinity();
    UV_REQUIRE(!validate_affine_frame(wire));
    wire=original; wire.placement.reserved=1;
    UV_REQUIRE(!validate_affine_frame(wire));
    const auto prior=result;
    request.source_to_device={1,1,1,1,0,0,1,1};
    UV_REQUIRE(!create_affine_sample_frame(request,result));
    UV_REQUIRE(std::memcmp(&prior,&result,sizeof(result))==0);
    return true;
}
