//! The layer effects of the engine, checked in the pixels that come back.

mod common;

use progpu_native_sys::layer_effects::*;
use progpu_native_sys::progpu::*;
use progpu_native_sys::webgpu::*;
use std::ffi::c_void;
use std::ptr;

const IDENTITY: progpu_native_affine_2d =
    progpu_native_affine_2d { m11: 1.0, m12: 0.0, m21: 0.0, m22: 1.0, m31: 0.0, m32: 0.0 };

struct Fixture {
    gpu: common::Gpu,
    effects: *mut progpu_native_layer_effects,
}

impl Fixture {
    fn new() -> Option<Self> {
        let gpu = common::Gpu::new()?;
        let mut effects = ptr::null_mut();
        // SAFETY: the engine is alive and the slot is written.
        let status = unsafe { progpu_native_layer_effects_create(gpu.engine, &mut effects) };
        assert_eq!(PROGPU_NATIVE_STATUS_SUCCESS, status);
        Some(Fixture { gpu, effects })
    }

    /// A view of a texture with `pixels` (premultiplied RGBA, `width` to a row).
    fn image(&self, width: u32, height: u32, pixels: &[u8]) -> usize {
        // SAFETY: the device is alive; the records and the pixels are borrowed for the calls.
        unsafe {
            let texture = wgpuDeviceCreateTexture(
                self.gpu.device,
                &WGPUTextureDescriptor {
                    nextInChain: ptr::null_mut(),
                    label: WGPUStringView { data: ptr::null(), length: 0 },
                    usage: WGPUTextureUsage_TextureBinding | WGPUTextureUsage_CopyDst,
                    dimension: WGPUTextureDimension_2D,
                    size: WGPUExtent3D { width, height, depthOrArrayLayers: 1 },
                    format: WGPUTextureFormat_RGBA8Unorm,
                    mipLevelCount: 1,
                    sampleCount: 1,
                    viewFormatCount: 0,
                    viewFormats: ptr::null(),
                },
            );
            wgpuQueueWriteTexture(
                self.gpu.queue,
                &WGPUTexelCopyTextureInfo {
                    texture,
                    mipLevel: 0,
                    origin: WGPUOrigin3D { x: 0, y: 0, z: 0 },
                    aspect: WGPUTextureAspect_All,
                },
                pixels.as_ptr() as *const c_void,
                pixels.len(),
                &WGPUTexelCopyBufferLayout { offset: 0, bytesPerRow: width * 4, rowsPerImage: height },
                &WGPUExtent3D { width, height, depthOrArrayLayers: 1 },
            );
            wgpuTextureCreateView(texture, ptr::null()) as usize
        }
    }

    /// Runs `pass` into a new target of `width` by `height` and returns its pixels.
    fn output(
        &self,
        width: u32,
        height: u32,
        first: usize,
        second: usize,
        pass: impl FnOnce(&progpu_native_layer_effect_target) -> progpu_native_status,
    ) -> Vec<u8> {
        let (texture, view) = self.gpu.target(width, height);
        let target = progpu_native_layer_effect_target {
            struct_size: std::mem::size_of::<progpu_native_layer_effect_target>() as u32,
            width,
            height,
            reserved: 0,
            output_view: view as usize,
            first_view: first,
            second_view: second,
        };
        assert_eq!(PROGPU_NATIVE_STATUS_SUCCESS, pass(&target), "the pass");
        self.gpu.read_rgba8(texture, width, height)
    }
}

impl Drop for Fixture {
    fn drop(&mut self) {
        // SAFETY: made in `new`; the engine outlives it.
        unsafe { progpu_native_layer_effects_destroy(self.effects) };
    }
}

fn near(expected: [u8; 4], actual: &[u8]) {
    let close = expected.iter().zip(actual).all(|(a, b)| a.abs_diff(*b) <= 1);
    assert!(close, "expected {expected:?}, got {actual:?}");
}

#[test]
fn a_colour_matrix_over_straight_and_premultiplied_colours() {
    let Some(fixture) = Fixture::new() else { return };
    // Half-transparent red, premultiplied.
    let input = fixture.image(1, 1, &[128, 0, 0, 128]);
    let mut matrix = progpu_native_layer_color_matrix {
        struct_size: std::mem::size_of::<progpu_native_layer_color_matrix>() as u32,
        flags: 0,
        rows: [[0.0, 0.0, 1.0, 0.0], [0.0, 1.0, 0.0, 0.0], [1.0, 0.0, 0.0, 0.0], [0.0, 0.0, 0.0, 1.0], [0.0; 4]],
    };
    let straight = fixture.output(1, 1, input, 0, |target| unsafe {
        progpu_native_layer_effects_color_matrix(fixture.effects, target, &matrix)
    });
    near([0, 0, 128, 128], &straight);
    // Over premultiplied colours an offset is not scaled by the alpha.
    matrix.flags = PROGPU_NATIVE_LAYER_COLOR_MATRIX_PREMULTIPLIED_INPUT;
    matrix.rows[4] = [0.0, 0.25, 0.0, 0.0];
    let premultiplied = fixture.output(1, 1, input, 0, |target| unsafe {
        progpu_native_layer_effects_color_matrix(fixture.effects, target, &matrix)
    });
    near([0, 64, 128, 128], &premultiplied);
}

#[test]
fn a_kernel_is_turned_by_half_a_turn_and_edges_are_transparent_or_repeated() {
    let Some(fixture) = Fixture::new() else { return };
    let row = [0, 0, 0, 255, 50, 50, 50, 255, 100, 100, 100, 255, 150, 150, 150, 255];
    let input = fixture.image(4, 1, &row);
    let mut elements = [0.0; PROGPU_NATIVE_LAYER_EFFECT_MAX_KERNEL_ELEMENTS];
    elements[2] = 1.0;
    let mut kernel = progpu_native_layer_convolution {
        struct_size: std::mem::size_of::<progpu_native_layer_convolution>() as u32,
        flags: 0,
        columns: 3,
        rows: 1,
        gain: 1.0,
        bias: 0.0,
        elements,
        reserved: 0,
    };
    // The last element of the kernel takes the pixel to the left.
    let shifted = fixture.output(4, 1, input, 0, |target| unsafe {
        progpu_native_layer_effects_convolution(fixture.effects, target, &kernel)
    });
    near([0, 0, 0, 0], &shifted[0..4]);
    near([0, 0, 0, 255], &shifted[4..8]);
    near([100, 100, 100, 255], &shifted[12..16]);
    kernel.flags = PROGPU_NATIVE_LAYER_CONVOLUTION_REPEAT_EDGES;
    let repeated = fixture.output(4, 1, input, 0, |target| unsafe {
        progpu_native_layer_effects_convolution(fixture.effects, target, &kernel)
    });
    near([0, 0, 0, 255], &repeated[0..4]);
}

fn composite(operation: u32, blend_mode: u32, k: [f32; 4]) -> progpu_native_layer_composite {
    progpu_native_layer_composite {
        struct_size: std::mem::size_of::<progpu_native_layer_composite>() as u32,
        operation,
        blend_mode,
        grid_columns: 1,
        grid_rows: 1,
        reserved: 0,
        k,
        grid: progpu_native_image_rect { x: 0.0, y: 0.0, width: 1.0, height: 1.0 },
        pixel_to_drawing: IDENTITY,
    }
}

#[test]
fn composites_of_two_sources_follow_their_formulas() {
    let Some(fixture) = Fixture::new() else { return };
    let run = |first: [u8; 4], second: [u8; 4], record: progpu_native_layer_composite| {
        let (a, b) = (fixture.image(1, 1, &first), fixture.image(1, 1, &second));
        fixture.output(1, 1, a, b, |target| unsafe {
            progpu_native_layer_effects_composite(fixture.effects, target, &record)
        })
    };
    let blend = |mode| composite(PROGPU_NATIVE_LAYER_COMPOSITE_BLEND, mode, [0.0; 4]);
    near(
        [100, 0, 25, 255],
        &run([200, 100, 50, 255], [100, 150, 25, 255], blend(PROGPU_NATIVE_LAYER_BLEND_SUBTRACT)),
    );
    near(
        [45, 100, 0, 255],
        &run([200, 100, 50, 255], [100, 255, 0, 255], blend(PROGPU_NATIVE_LAYER_BLEND_LINEAR_BURN)),
    );
    near(
        [255, 0, 0, 255],
        &run([100, 100, 100, 255], [200, 100, 0, 255], blend(PROGPU_NATIVE_LAYER_BLEND_HARD_MIX)),
    );
    near(
        [200, 0, 0, 255],
        &run([10, 10, 10, 255], [200, 0, 0, 255], blend(PROGPU_NATIVE_LAYER_BLEND_LIGHTER_COLOR)),
    );
    near([100, 50, 0, 255], &run([0; 4], [100, 50, 0, 255], blend(PROGPU_NATIVE_LAYER_BLEND_DIVISION)));
    near([0, 0, 255, 255], &run([255, 0, 0, 255], [0, 0, 255, 255], blend(PROGPU_NATIVE_LAYER_BLEND_DISSOLVE)));
    let arithmetic = composite(PROGPU_NATIVE_LAYER_COMPOSITE_ARITHMETIC, 0, [0.0, 0.5, 0.5, 0.0]);
    near([128, 128, 128, 255], &run([255, 0, 128, 255], [0, 255, 128, 255], arithmetic));
    let invert = composite(PROGPU_NATIVE_LAYER_COMPOSITE_MASK_INVERT, 0, [0.0; 4]);
    near([0, 255, 255, 255], &run([255, 0, 0, 255], [0, 0, 0, 255], invert));
}

fn lighting(output: u32) -> progpu_native_layer_lighting {
    // SAFETY: all-zero is a valid record of numbers.
    let mut record: progpu_native_layer_lighting = unsafe { std::mem::zeroed() };
    record.struct_size = std::mem::size_of::<progpu_native_layer_lighting>() as u32;
    record.output = output;
    record.diffuse = 1.0;
    record.shine = 16.0;
    record.max_factor = 4.0;
    record.pixel_to_drawing = IDENTITY;
    record
}

#[test]
fn a_point_light_above_a_pixel_lights_it_fully_and_falls_off_sideways() {
    let Some(fixture) = Fixture::new() else { return };
    let mut record = lighting(PROGPU_NATIVE_LAYER_LIGHTING_FILL);
    record.light_count = 1;
    record.lights[0].kind = PROGPU_NATIVE_LAYER_LIGHT_POINT;
    record.lights[0].color = [1.0, 0.5, 0.0];
    record.lights[0].position = [0.5, 0.5, 10.0];
    record.lights[0].attenuation_constant = 1.0;
    record.lights[0].to_light = [[1.0, 0.0, 0.0], [0.0, 1.0, 0.0], [0.0, 0.0, 1.0]];
    let pixels = fixture.output(11, 1, 0, 0, |target| unsafe {
        progpu_native_layer_effects_lighting(fixture.effects, target, &record)
    });
    near([255, 128, 0, 255], &pixels[0..4]);
    // N.L at 10 pixels sideways: 10 / sqrt(200).
    near([180, 90, 0, 255], &pixels[40..44]);
}

#[test]
fn a_spot_lights_inside_its_cone_only() {
    let Some(fixture) = Fixture::new() else { return };
    let mut record = lighting(PROGPU_NATIVE_LAYER_LIGHTING_FILL);
    record.light_count = 1;
    let light = &mut record.lights[0];
    light.kind = PROGPU_NATIVE_LAYER_LIGHT_SPOT;
    light.color = [1.0, 1.0, 1.0];
    light.outer_color = [1.0, 1.0, 1.0];
    light.position = [0.5, 0.5, 10.0];
    light.direction = [0.0, 0.0, -1.0];
    light.cos_inner_cone = 0.999;
    light.cos_outer_cone = 0.99;
    light.attenuation_constant = 1.0;
    light.to_light = [[1.0, 0.0, 0.0], [0.0, 1.0, 0.0], [0.0, 0.0, 1.0]];
    let pixels = fixture.output(16, 1, 0, 0, |target| unsafe {
        progpu_native_layer_effects_lighting(fixture.effects, target, &record)
    });
    near([255, 255, 255, 255], &pixels[0..4]);
    near([0, 0, 0, 255], &pixels[60..64]);
}

#[test]
fn a_multiply_by_the_light_keeps_the_alpha_and_the_colour_under_it() {
    let Some(fixture) = Fixture::new() else { return };
    let input = fixture.image(1, 1, &[100, 100, 100, 200]);
    let mut record = lighting(PROGPU_NATIVE_LAYER_LIGHTING_MULTIPLY);
    record.constant_light = [0.5, 3.0, 0.0];
    let pixels = fixture.output(1, 1, input, 0, |target| unsafe {
        progpu_native_layer_effects_lighting(fixture.effects, target, &record)
    });
    near([50, 200, 0, 200], &pixels);
}

#[test]
fn a_factor_image_multiplies_inside_its_square() {
    let Some(fixture) = Fixture::new() else { return };
    let input = fixture.image(2, 1, &[255, 255, 0, 255, 255, 255, 0, 255]);
    let factors = fixture.image(1, 1, &[255, 0, 255, 255]);
    // The square of the factors is the first pixel.
    let record = progpu_native_layer_factor_multiply {
        struct_size: std::mem::size_of::<progpu_native_layer_factor_multiply>() as u32,
        scale: 1.0,
        pixel_to_unit: IDENTITY,
    };
    let pixels = fixture.output(2, 1, input, factors, |target| unsafe {
        progpu_native_layer_effects_factor_multiply(fixture.effects, target, &record)
    });
    near([255, 0, 0, 255], &pixels[0..4]);
    near([0, 0, 0, 0], &pixels[4..8]);
}

#[test]
fn material_noise_is_faint_and_stays_with_its_pixels() {
    let Some(fixture) = Fixture::new() else { return };
    let record = progpu_native_layer_material_noise {
        struct_size: std::mem::size_of::<progpu_native_layer_material_noise>() as u32,
        opacity: 0.0225,
        color: [1.0, 1.0, 1.0],
        origin_x: 0.0,
        origin_y: 0.0,
    };
    let run = |record: &progpu_native_layer_material_noise| {
        fixture.output(8, 8, 0, 0, |target| unsafe {
            progpu_native_layer_effects_material_noise(fixture.effects, target, record)
        })
    };
    let pixels = run(&record);
    assert!(pixels.chunks_exact(4).all(|pixel| pixel[3] == 6 && pixel[0] <= 6));
    assert!(pixels.chunks_exact(4).any(|pixel| pixel[0] != pixels[0]), "it varies");
    assert_eq!(pixels, run(&record), "the same in every frame");
}

const SWAP_PROGRAM: &str = "
struct Pass { area: vec4<f32> }
@group(0) @binding(0) var<uniform> values: vec4<f32>;
@group(0) @binding(1) var<uniform> pass_block: Pass;
@group(0) @binding(2) var content: texture_2d<f32>;
@group(0) @binding(3) var image: texture_2d<f32>;
@vertex fn vs_main(@builtin(vertex_index) index: u32) -> @builtin(position) vec4<f32> {
    var corners = array<vec2<f32>, 3>(vec2<f32>(-1.0, -1.0), vec2<f32>(3.0, -1.0), vec2<f32>(-1.0, 3.0));
    return vec4<f32>(corners[index], 0.0, 1.0);
}
@fragment fn fs_main(@builtin(position) position: vec4<f32>) -> @location(0) vec4<f32> {
    let pixel = vec2<i32>(position.xy);
    let color = textureLoad(content, pixel, 0).bgra * values.x;
    return vec4<f32>(color.rgb + textureLoad(image, pixel, 0).rgb, color.a + pass_block.area.x);
}";

#[test]
fn a_program_of_the_host_reads_its_uniforms_content_and_images() {
    let Some(fixture) = Fixture::new() else { return };
    let content = fixture.image(1, 1, &[255, 0, 0, 255]);
    let image = fixture.image(1, 1, &[0, 64, 0, 255]);
    let mut program = ptr::null_mut();
    // SAFETY: the source and the slot outlive the call.
    let status = unsafe {
        progpu_native_layer_effects_create_program(
            fixture.effects,
            SWAP_PROGRAM.as_ptr() as *const _,
            SWAP_PROGRAM.len(),
            16,
            1,
            &mut program,
        )
    };
    assert_eq!(PROGPU_NATIVE_STATUS_SUCCESS, status);
    let (values, pass, images) = ([1.0f32, 0.0, 0.0, 0.0], [0.0f32; 4], [image]);
    let pixels = fixture.output(1, 1, content, 0, |target| unsafe {
        progpu_native_layer_effects_run_program(
            fixture.effects,
            program,
            target,
            values.as_ptr() as *const c_void,
            16,
            pass.as_ptr(),
            images.as_ptr(),
            1,
        )
    });
    near([0, 64, 255, 255], &pixels);
    // SAFETY: made above.
    unsafe { progpu_native_layer_effect_program_destroy(program) };
}

#[test]
fn what_a_pass_refuses() {
    let Some(fixture) = Fixture::new() else { return };
    let (_, view) = fixture.gpu.target(1, 1);
    let target = progpu_native_layer_effect_target {
        struct_size: std::mem::size_of::<progpu_native_layer_effect_target>() as u32,
        width: 1,
        height: 1,
        reserved: 0,
        output_view: view as usize,
        first_view: 0,
        second_view: 0,
    };
    let mut record = lighting(PROGPU_NATIVE_LAYER_LIGHTING_FILL);
    record.light_count = 9;
    // SAFETY: the records outlive the calls.
    unsafe {
        assert_ne!(
            PROGPU_NATIVE_STATUS_SUCCESS,
            progpu_native_layer_effects_lighting(fixture.effects, &target, &record),
            "more lights than the block holds"
        );
        record.light_count = 0;
        record.output = PROGPU_NATIVE_LAYER_LIGHTING_MULTIPLY;
        assert_ne!(
            PROGPU_NATIVE_STATUS_SUCCESS,
            progpu_native_layer_effects_lighting(fixture.effects, &target, &record),
            "a multiply without its content"
        );
    }
}
