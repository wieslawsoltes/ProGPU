// Algorithm: Draw a fullscreen triangle, reconstruct one P010 chroma sample with four clamped integer loads per luma location, decode normalized YUV, and evaluate one horizontal Gaussian axis with explicit symmetric integer taps.
// Time complexity: O(R) per fragment and O(P * R) over P destination texels, where R = ceil(3 * sigma) and 0 <= R <= 96; each source tap performs one R16 luma load and four RG16 chroma loads.
// Space complexity: O(1) fragment-local storage, one output write, two unfilterable sampled-texture bindings, and a fixed 912-byte uniform block.
// R16Unorm and RG16Unorm are unfilterable WebGPU Tier-1 formats. Chroma
// interpolation is therefore reconstructed explicitly before range/matrix
// conversion. The loop is fixed at 48 adjacent pairs (96 positive taps),
// clamps every integer coordinate, assumes MSB-aligned normalized P010 input,
// and writes straight alpha.
struct VertexOutput {
    @builtin(position) position: vec4<f32>,
};

@vertex
fn vs_main(@builtin(vertex_index) vertexIndex: u32) -> VertexOutput {
    var positions = array<vec2<f32>, 3>(
        vec2<f32>(-1.0, -1.0),
        vec2<f32>(3.0, -1.0),
        vec2<f32>(-1.0, 3.0));
    var output: VertexOutput;
    output.position = vec4<f32>(
        positions[vertexIndex],
        0.0,
        1.0);
    return output;
}

@group(0) @binding(0) var unusedSampler: sampler;
@group(0) @binding(1) var sourceTexture: texture_2d<f32>;
@group(0) @binding(2) var chromaTexture: texture_2d<f32>;

struct BlurEffects {
    // xy: one-texel UV direction, z: normalized center weight,
    // w: active adjacent-tap pair count.
    axis: vec4<f32>,
    red: vec4<f32>,
    green: vec4<f32>,
    blue: vec4<f32>,
    // x: decode planar YUV when non-zero.
    options: vec4<f32>,
    yuvRange: vec4<f32>,
    yuvRed: vec4<f32>,
    yuvGreen: vec4<f32>,
    yuvBlue: vec4<f32>,
    // x/y: filterable-path reduced offset/weight,
    // z/w: explicit weights for the two integer taps.
    taps: array<vec4<f32>, 48>,
};

@group(0) @binding(3) var<uniform> effects: BlurEffects;

fn clampCoordinate(
    coordinate: vec2<i32>,
    dimensions: vec2<u32>) -> vec2<i32> {
    return clamp(
        coordinate,
        vec2<i32>(0),
        vec2<i32>(dimensions) - vec2<i32>(1));
}

fn loadChroma(
    coordinate: vec2<i32>,
    dimensions: vec2<u32>) -> vec2<f32> {
    return textureLoad(
        chromaTexture,
        clampCoordinate(coordinate, dimensions),
        0).rg;
}

fn sampleSource(pixel: vec2<i32>) -> vec4<f32> {
    let lumaDimensions = textureDimensions(sourceTexture);
    let chromaDimensions = textureDimensions(chromaTexture);
    let clampedPixel =
        clampCoordinate(pixel, lumaDimensions);
    let rawY = textureLoad(
        sourceTexture,
        clampedPixel,
        0).r;

    let chromaPosition =
        (vec2<f32>(clampedPixel) + vec2<f32>(0.5)) *
        vec2<f32>(chromaDimensions) /
        vec2<f32>(lumaDimensions) -
        vec2<f32>(0.5);
    let chromaBase =
        vec2<i32>(floor(chromaPosition));
    let chromaFraction = fract(chromaPosition);
    let upperLeft =
        loadChroma(chromaBase, chromaDimensions);
    let upperRight =
        loadChroma(
            chromaBase + vec2<i32>(1, 0),
            chromaDimensions);
    let lowerLeft =
        loadChroma(
            chromaBase + vec2<i32>(0, 1),
            chromaDimensions);
    let lowerRight =
        loadChroma(
            chromaBase + vec2<i32>(1, 1),
            chromaDimensions);
    let rawChroma = mix(
        mix(
            upperLeft,
            upperRight,
            chromaFraction.x),
        mix(
            lowerLeft,
            lowerRight,
            chromaFraction.x),
        chromaFraction.y);
    let components = vec3<f32>(
        (rawY - effects.yuvRange.x) *
            effects.yuvRange.y,
        (rawChroma.x - effects.yuvRange.z) *
            effects.yuvRange.w,
        (rawChroma.y - effects.yuvRange.z) *
            effects.yuvRange.w);
    return vec4<f32>(
        dot(components, effects.yuvRed.xyz),
        dot(components, effects.yuvGreen.xyz),
        dot(components, effects.yuvBlue.xyz),
        1.0);
}

@fragment
fn fs_main(input: VertexOutput) -> @location(0) vec4<f32> {
    let center = vec2<i32>(input.position.xy);
    let direction = select(
        vec2<i32>(0, 1),
        vec2<i32>(1, 0),
        abs(effects.axis.x) > abs(effects.axis.y));
    var blurred =
        sampleSource(center) *
        effects.axis.z;

    for (var index = 0u; index < 48u; index = index + 1u) {
        if (f32(index) < effects.axis.w) {
            let tap = effects.taps[index];
            let firstOffset =
                i32(index * 2u + 1u);
            let secondOffset = firstOffset + 1;
            let firstDelta =
                direction * firstOffset;
            blurred +=
                (sampleSource(center + firstDelta) +
                 sampleSource(center - firstDelta)) *
                tap.z;
            if (tap.w > 0.0) {
                let secondDelta =
                    direction * secondOffset;
                blurred +=
                    (sampleSource(center + secondDelta) +
                     sampleSource(center - secondDelta)) *
                    tap.w;
            }
        }
    }

    let source = vec4<f32>(blurred.rgb, 1.0);
    let processed = vec3<f32>(
        dot(source, effects.red),
        dot(source, effects.green),
        dot(source, effects.blue));
    return vec4<f32>(processed, blurred.a);
}
