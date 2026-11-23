// `progpu_native_layer_effects.h`, in the form of the other generated bindings.

use crate::progpu::*;

#[repr(C)]
#[derive(Debug)]
pub struct progpu_native_layer_effects {
    _unused: [u8; 0],
}

#[repr(C)]
#[derive(Debug)]
pub struct progpu_native_layer_effect_program {
    _unused: [u8; 0],
}

pub const PROGPU_NATIVE_LAYER_EFFECT_MAX_LIGHTS: usize = 8;
pub const PROGPU_NATIVE_LAYER_EFFECT_MAX_KERNEL_ELEMENTS: usize = 25;
pub const PROGPU_NATIVE_LAYER_EFFECT_MAX_KERNEL_SIDE: u32 = 5;
pub const PROGPU_NATIVE_LAYER_EFFECT_MAX_PROGRAM_IMAGES: usize = 8;

pub const PROGPU_NATIVE_LAYER_COLOR_MATRIX_PREMULTIPLIED_INPUT: u32 = 1;
pub const PROGPU_NATIVE_LAYER_CONVOLUTION_KEEP_ALPHA: u32 = 1;
pub const PROGPU_NATIVE_LAYER_CONVOLUTION_REPEAT_EDGES: u32 = 2;
pub const PROGPU_NATIVE_LAYER_COMPOSITE_BLEND: u32 = 0;
pub const PROGPU_NATIVE_LAYER_COMPOSITE_ARITHMETIC: u32 = 1;
pub const PROGPU_NATIVE_LAYER_COMPOSITE_MASK_INVERT: u32 = 2;
pub const PROGPU_NATIVE_LAYER_BLEND_DISSOLVE: u32 = 4;
pub const PROGPU_NATIVE_LAYER_BLEND_LINEAR_BURN: u32 = 6;
pub const PROGPU_NATIVE_LAYER_BLEND_DARKER_COLOR: u32 = 7;
pub const PROGPU_NATIVE_LAYER_BLEND_LIGHTER_COLOR: u32 = 8;
pub const PROGPU_NATIVE_LAYER_BLEND_VIVID_LIGHT: u32 = 14;
pub const PROGPU_NATIVE_LAYER_BLEND_LINEAR_LIGHT: u32 = 15;
pub const PROGPU_NATIVE_LAYER_BLEND_PIN_LIGHT: u32 = 16;
pub const PROGPU_NATIVE_LAYER_BLEND_HARD_MIX: u32 = 17;
pub const PROGPU_NATIVE_LAYER_BLEND_SUBTRACT: u32 = 24;
pub const PROGPU_NATIVE_LAYER_BLEND_DIVISION: u32 = 25;
pub const PROGPU_NATIVE_LAYER_LIGHT_POINT: u32 = 1;
pub const PROGPU_NATIVE_LAYER_LIGHT_SPOT: u32 = 2;
pub const PROGPU_NATIVE_LAYER_LIGHTING_FILL: u32 = 0;
pub const PROGPU_NATIVE_LAYER_LIGHTING_MULTIPLY: u32 = 1;

#[repr(C)]
#[derive(Debug, Copy, Clone)]
pub struct progpu_native_layer_effect_target {
    pub struct_size: u32,
    pub width: u32,
    pub height: u32,
    pub reserved: u32,
    pub output_view: usize,
    pub first_view: usize,
    pub second_view: usize,
}

#[repr(C)]
#[derive(Debug, Copy, Clone)]
pub struct progpu_native_layer_color_matrix {
    pub struct_size: u32,
    pub flags: u32,
    pub rows: [[f32; 4]; 5],
}

#[repr(C)]
#[derive(Debug, Copy, Clone)]
pub struct progpu_native_layer_convolution {
    pub struct_size: u32,
    pub flags: u32,
    pub columns: u32,
    pub rows: u32,
    pub gain: f32,
    pub bias: f32,
    pub elements: [f32; PROGPU_NATIVE_LAYER_EFFECT_MAX_KERNEL_ELEMENTS],
    pub reserved: u32,
}

#[repr(C)]
#[derive(Debug, Copy, Clone)]
pub struct progpu_native_layer_composite {
    pub struct_size: u32,
    pub operation: u32,
    pub blend_mode: u32,
    pub grid_columns: u32,
    pub grid_rows: u32,
    pub reserved: u32,
    pub k: [f32; 4],
    pub grid: progpu_native_image_rect,
    pub pixel_to_drawing: progpu_native_affine_2d,
}

#[repr(C)]
#[derive(Debug, Copy, Clone)]
pub struct progpu_native_layer_light {
    pub kind: u32,
    pub has_max_cutoff: u32,
    pub cos_inner_cone: f32,
    pub cos_outer_cone: f32,
    pub color: [f32; 3],
    pub outer_color: [f32; 3],
    pub position: [f32; 3],
    pub direction: [f32; 3],
    pub attenuation_constant: f32,
    pub attenuation_linear: f32,
    pub attenuation_quadratic: f32,
    pub min_cutoff: f32,
    pub max_cutoff: f32,
    pub to_light: [[f32; 3]; 3],
}

#[repr(C)]
#[derive(Debug, Copy, Clone)]
pub struct progpu_native_layer_lighting {
    pub struct_size: u32,
    pub output: u32,
    pub light_count: u32,
    pub origin_x: u32,
    pub origin_y: u32,
    pub constant_light: [f32; 3],
    pub diffuse: f32,
    pub specular: f32,
    pub shine: f32,
    pub max_factor: f32,
    pub pixel_to_drawing: progpu_native_affine_2d,
    pub lights: [progpu_native_layer_light; PROGPU_NATIVE_LAYER_EFFECT_MAX_LIGHTS],
}

#[repr(C)]
#[derive(Debug, Copy, Clone)]
pub struct progpu_native_layer_factor_multiply {
    pub struct_size: u32,
    pub scale: f32,
    pub pixel_to_unit: progpu_native_affine_2d,
}

#[repr(C)]
#[derive(Debug, Copy, Clone)]
pub struct progpu_native_layer_material_noise {
    pub struct_size: u32,
    pub opacity: f32,
    pub color: [f32; 3],
    pub origin_x: f32,
    pub origin_y: f32,
}

const _: () = {
    assert!(core::mem::size_of::<progpu_native_layer_color_matrix>() == 88);
    assert!(core::mem::size_of::<progpu_native_layer_convolution>() == 128);
    assert!(core::mem::size_of::<progpu_native_layer_composite>() == 80);
    assert!(core::mem::size_of::<progpu_native_layer_light>() == 120);
    assert!(core::mem::size_of::<progpu_native_layer_lighting>() == 1032);
    assert!(core::mem::size_of::<progpu_native_layer_factor_multiply>() == 32);
    assert!(core::mem::size_of::<progpu_native_layer_material_noise>() == 28);
};

unsafe extern "C" {
    pub fn progpu_native_layer_effects_create(
        engine: *mut progpu_native_engine,
        effects: *mut *mut progpu_native_layer_effects,
    ) -> progpu_native_status;
    pub fn progpu_native_layer_effects_destroy(effects: *mut progpu_native_layer_effects);
    pub fn progpu_native_layer_effects_color_matrix(
        effects: *mut progpu_native_layer_effects,
        target: *const progpu_native_layer_effect_target,
        matrix: *const progpu_native_layer_color_matrix,
    ) -> progpu_native_status;
    pub fn progpu_native_layer_effects_convolution(
        effects: *mut progpu_native_layer_effects,
        target: *const progpu_native_layer_effect_target,
        convolution: *const progpu_native_layer_convolution,
    ) -> progpu_native_status;
    pub fn progpu_native_layer_effects_composite(
        effects: *mut progpu_native_layer_effects,
        target: *const progpu_native_layer_effect_target,
        composite: *const progpu_native_layer_composite,
    ) -> progpu_native_status;
    pub fn progpu_native_layer_effects_lighting(
        effects: *mut progpu_native_layer_effects,
        target: *const progpu_native_layer_effect_target,
        lighting: *const progpu_native_layer_lighting,
    ) -> progpu_native_status;
    pub fn progpu_native_layer_effects_factor_multiply(
        effects: *mut progpu_native_layer_effects,
        target: *const progpu_native_layer_effect_target,
        factors: *const progpu_native_layer_factor_multiply,
    ) -> progpu_native_status;
    pub fn progpu_native_layer_effects_material_noise(
        effects: *mut progpu_native_layer_effects,
        target: *const progpu_native_layer_effect_target,
        noise: *const progpu_native_layer_material_noise,
    ) -> progpu_native_status;
    pub fn progpu_native_layer_effects_create_program(
        effects: *mut progpu_native_layer_effects,
        wgsl: *const ::core::ffi::c_char,
        wgsl_size: usize,
        uniform_size: u32,
        image_count: u32,
        program: *mut *mut progpu_native_layer_effect_program,
    ) -> progpu_native_status;
    pub fn progpu_native_layer_effect_program_destroy(program: *mut progpu_native_layer_effect_program);
    pub fn progpu_native_layer_effects_run_program(
        effects: *mut progpu_native_layer_effects,
        program: *mut progpu_native_layer_effect_program,
        target: *const progpu_native_layer_effect_target,
        uniforms: *const ::core::ffi::c_void,
        uniform_size: usize,
        pass_block: *const f32,
        image_views: *const usize,
        image_count: u32,
    ) -> progpu_native_status;
}
