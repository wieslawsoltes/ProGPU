// Algorithm: Linearly resample limited-range BT.709 NV12 in normalized coordinates, apply one fused affine straight-RGB transform, and render RGBA or output-sized luma/chroma planes; the reverse lane encodes sampled RGBA into NV12 planes.
// Time complexity: O(P) for P output texels; the NV12-to-RGBA/luma passes use one Y/UV sample pair and three four-term dot products, NV12 chroma uses four Y/UV pairs and twelve dot products, RGBA luma uses one sample, and RGBA chroma uses four samples.
// Space complexity: O(1) private storage per fragment, two sampled textures, one 64-byte uniform block, and one output write per fragment.
// The two render passes are encoded into one command buffer. Chroma is the
// average of a 2x2 reconstructed block so subsampling remains centered. RGB
// is not clamped between folded effect stages; destination encoding performs
// the terminal representable-format clamp.
struct VertexOutput {
    @builtin(position) position: vec4<f32>,
    @location(0) uv: vec2<f32>,
};

@vertex
fn vs_main(@builtin(vertex_index) vertexIndex: u32) -> VertexOutput {
    var positions = array<vec2<f32>, 3>(
        vec2<f32>(-1.0, -1.0),
        vec2<f32>(3.0, -1.0),
        vec2<f32>(-1.0, 3.0));
    var uvs = array<vec2<f32>, 3>(
        vec2<f32>(0.0, 1.0),
        vec2<f32>(2.0, 1.0),
        vec2<f32>(0.0, -1.0));
    var output: VertexOutput;
    output.position = vec4<f32>(positions[vertexIndex], 0.0, 1.0);
    output.uv = uvs[vertexIndex];
    return output;
}

@group(0) @binding(0) var planeSampler: sampler;
@group(0) @binding(1) var lumaTexture: texture_2d<f32>;
@group(0) @binding(2) var chromaTexture: texture_2d<f32>;

struct ProcessorUniforms {
    inverseLumaSize: vec2<f32>,
    redTransform: vec4<f32>,
    greenTransform: vec4<f32>,
    blueTransform: vec4<f32>,
};

@group(0) @binding(3) var<uniform> uniforms: ProcessorUniforms;

fn decodeBt709(uv: vec2<f32>) -> vec3<f32> {
    let encodedY = textureSampleLevel(
        lumaTexture,
        planeSampler,
        uv,
        0.0).r;
    let encodedUv = textureSampleLevel(
        chromaTexture,
        planeSampler,
        uv,
        0.0).rg;
    let y = (encodedY * 255.0 - 16.0) / 219.0;
    let cbcr = (encodedUv * 255.0 - vec2<f32>(128.0)) / 224.0;
    return clamp(
        vec3<f32>(
            y + 1.5748 * cbcr.y,
            y - 0.187324 * cbcr.x - 0.468124 * cbcr.y,
            y + 1.8556 * cbcr.x),
        vec3<f32>(0.0),
        vec3<f32>(1.0));
}

fn applyEffects(source: vec3<f32>) -> vec3<f32> {
    let affineInput = vec4<f32>(source, 1.0);
    return vec3<f32>(
        dot(uniforms.redTransform, affineInput),
        dot(uniforms.greenTransform, affineInput),
        dot(uniforms.blueTransform, affineInput));
}

fn encodeBt709(rgb: vec3<f32>) -> vec3<f32> {
    let y = dot(rgb, vec3<f32>(0.2126, 0.7152, 0.0722));
    let cb = (rgb.b - y) / 1.8556;
    let cr = (rgb.r - y) / 1.5748;
    return clamp(
        vec3<f32>(
            (16.0 + 219.0 * y) / 255.0,
            (128.0 + 224.0 * cb) / 255.0,
            (128.0 + 224.0 * cr) / 255.0),
        vec3<f32>(0.0),
        vec3<f32>(1.0));
}

@fragment
fn fs_luma(input: VertexOutput) -> @location(0) vec4<f32> {
    let encoded = encodeBt709(
        applyEffects(
            decodeBt709(input.uv)));
    return vec4<f32>(encoded.x, 0.0, 0.0, 1.0);
}

@fragment
fn fs_rgba(input: VertexOutput) -> @location(0) vec4<f32> {
    return vec4<f32>(
        applyEffects(
            decodeBt709(input.uv)),
        1.0);
}

@fragment
fn fs_chroma(input: VertexOutput) -> @location(0) vec4<f32> {
    let halfTexel = uniforms.inverseLumaSize * 0.5;
    let encoded0 = encodeBt709(applyEffects(
        decodeBt709(input.uv + vec2<f32>(-halfTexel.x, -halfTexel.y))));
    let encoded1 = encodeBt709(applyEffects(
        decodeBt709(input.uv + vec2<f32>(halfTexel.x, -halfTexel.y))));
    let encoded2 = encodeBt709(applyEffects(
        decodeBt709(input.uv + vec2<f32>(-halfTexel.x, halfTexel.y))));
    let encoded3 = encodeBt709(applyEffects(
        decodeBt709(input.uv + vec2<f32>(halfTexel.x, halfTexel.y))));
    let chroma =
        (encoded0.yz + encoded1.yz + encoded2.yz + encoded3.yz) *
        0.25;
    return vec4<f32>(chroma, 0.0, 1.0);
}

fn sampleRgba(uv: vec2<f32>) -> vec3<f32> {
    return textureSampleLevel(
        lumaTexture,
        planeSampler,
        uv,
        0.0).rgb;
}

@fragment
fn fs_rgba_luma(input: VertexOutput) -> @location(0) vec4<f32> {
    let encoded = encodeBt709(sampleRgba(input.uv));
    return vec4<f32>(encoded.x, 0.0, 0.0, 1.0);
}

@fragment
fn fs_rgba_chroma(input: VertexOutput) -> @location(0) vec4<f32> {
    let halfTexel = uniforms.inverseLumaSize * 0.5;
    let encoded0 = encodeBt709(
        sampleRgba(
            input.uv +
            vec2<f32>(-halfTexel.x, -halfTexel.y)));
    let encoded1 = encodeBt709(
        sampleRgba(
            input.uv +
            vec2<f32>(halfTexel.x, -halfTexel.y)));
    let encoded2 = encodeBt709(
        sampleRgba(
            input.uv +
            vec2<f32>(-halfTexel.x, halfTexel.y)));
    let encoded3 = encodeBt709(
        sampleRgba(
            input.uv +
            vec2<f32>(halfTexel.x, halfTexel.y)));
    let chroma =
        (encoded0.yz +
         encoded1.yz +
         encoded2.yz +
         encoded3.yz) *
        0.25;
    return vec4<f32>(chroma, 0.0, 1.0);
}
