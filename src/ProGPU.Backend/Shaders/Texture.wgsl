// Algorithm: Transform batched image/lattice/atlas quads, sample occupied zero-origin tile pages with per-tap clamp/repeat/mirror addressing independent of pooled allocation size, emit fixed-color cells without sampling, or sample nearest, linear, Mitchell-Netravali cubic, or a retained-cache Fant-style bounded area footprint; atlas sprites optionally combine sampled source and per-sprite destination colors with a Skia blend mode; semantic color processing optionally applies Skia-compatible post-transform luminance-to-alpha.
// Time complexity: O(1) per invocation; fixed-color cells perform no image sample, native nearest/linear uses one sampler operation, explicit nearest/linear uses 1/4 base-level texel loads, cubic and Fant filtering perform fixed 4x4 sample footprints (occupied-page Fant uses 4 bilinear loads per stratum, at most 64 loads), optional semantic color processing performs five fixed dot products plus one luminance dot product, atlas color blending uses bounded scalar work, and semantic mask chains evaluate at most four analytic rounded masks.
// Space complexity: O(1) local storage and bounded texture bandwidth per fragment; texture masks add one sample plus a fixed axis-aligned or affine UV transform, color matrices add one 96-byte uniform record containing 80 bytes of coefficients, and nested analytic masks use one primary 96-byte record plus one fixed 288-byte continuation record without another texture.
struct VertexInput {
    @location(0) position: vec2<f32>,
    @location(1) color: vec4<f32>,
    @location(2) texCoord: vec2<f32>,
    @location(3) patchKind: f32,
    @location(4) cubicResampler: vec2<f32>,
    @location(5) colorBlendMode: f32,
    @location(6) patchOpacity: f32,
};

struct VertexOutput {
    @builtin(position) position: vec4<f32>,
    @location(0) color: vec4<f32>,
    @location(1) texCoord: vec2<f32>,
    @location(2) @interpolate(flat) cubicResampler: vec2<f32>,
    @location(3) @interpolate(flat) patchKind: f32,
    @location(4) @interpolate(flat) colorBlendMode: f32,
    @location(5) @interpolate(flat) patchOpacity: f32,
};

struct Uniforms {
    projection: mat4x4<f32>,
    mvp: mat4x4<f32>,
    view: mat4x4<f32>,
    canvasSize: vec2<f32>,
    dpiScale: f32,
    boundedSourcePass: f32,
    renderOrigin: vec2<f32>,
    pad1: vec2<f32>,
};

@group(0) @binding(0) var<uniform> uniforms: Uniforms;

@vertex
fn vs_main(input: VertexInput) -> VertexOutput {
    var output: VertexOutput;
    var pos = input.position;
    output.position = uniforms.projection * vec4<f32>(pos, 0.0, 1.0);
    output.color = input.color;
    output.texCoord = input.texCoord;
    output.cubicResampler = input.cubicResampler;
    output.patchKind = input.patchKind;
    output.colorBlendMode = input.colorBlendMode;
    output.patchOpacity = input.patchOpacity;
    return output;
}

    @group(1) @binding(0) var texSampler: sampler;
@group(1) @binding(1) var texTexture: texture_2d<f32>;
@group(2) @binding(0) var maskSampler: sampler;
@group(2) @binding(1) var maskTexture: texture_2d<f32>;

struct MaskSamplingUniforms {
    coordinate0: vec4<f32>,
    coordinate1: vec4<f32>,
    bounds: vec4<f32>,
    cornerRadiiX: vec4<f32>,
    cornerRadiiY: vec4<f32>,
    options: vec4<f32>,
};

@group(2) @binding(2) var<uniform> maskSampling: MaskSamplingUniforms;
@group(3) @binding(2) var<uniform> colorMatrixSampling: MaskSamplingUniforms;

struct MaskChainUniforms {
    masks: array<MaskSamplingUniforms, 3>,
};

@group(2) @binding(3) var<uniform> maskChain: MaskChainUniforms;

fn rounded_mask_alpha_local(local: vec2<f32>, bounds: vec4<f32>, radiiX: vec4<f32>, radiiY: vec4<f32>) -> f32 {
    let edge = max(max(bounds.x - local.x, local.x - bounds.z), max(bounds.y - local.y, local.y - bounds.w));
    var center = vec2<f32>(0.0);
    var radius = vec2<f32>(0.0);
    var usesCorner = false;
    if (local.x < bounds.x + radiiX.x && local.y < bounds.y + radiiY.x) {
        radius = vec2<f32>(radiiX.x, radiiY.x);
        center = vec2<f32>(bounds.x + radius.x, bounds.y + radius.y);
        usesCorner = all(radius > vec2<f32>(0.0));
    } else if (local.x > bounds.z - radiiX.y && local.y < bounds.y + radiiY.y) {
        radius = vec2<f32>(radiiX.y, radiiY.y);
        center = vec2<f32>(bounds.z - radius.x, bounds.y + radius.y);
        usesCorner = all(radius > vec2<f32>(0.0));
    } else if (local.x > bounds.z - radiiX.z && local.y > bounds.w - radiiY.z) {
        radius = vec2<f32>(radiiX.z, radiiY.z);
        center = vec2<f32>(bounds.z - radius.x, bounds.w - radius.y);
        usesCorner = all(radius > vec2<f32>(0.0));
    } else if (local.x < bounds.x + radiiX.w && local.y > bounds.w - radiiY.w) {
        radius = vec2<f32>(radiiX.w, radiiY.w);
        center = vec2<f32>(bounds.x + radius.x, bounds.w - radius.y);
        usesCorner = all(radius > vec2<f32>(0.0));
    }
    let safeRadius = max(radius, vec2<f32>(0.000001));
    let ellipsePoint = (local - center) / safeRadius;
    let ellipse = dot(ellipsePoint, ellipsePoint) - 1.0;
    let implicit = select(edge, ellipse, usesCorner);
    let antialiasWidth = max(fwidth(implicit), 0.0001);
    return clamp(0.5 - implicit / antialiasWidth, 0.0, 1.0);
}

fn analytic_rounded_mask_alpha_for(position: vec2<f32>, sampling: MaskSamplingUniforms) -> f32 {
    let local = vec2<f32>(
        dot(vec3<f32>(position, 1.0), sampling.coordinate0.xyz),
        dot(vec3<f32>(position, 1.0), sampling.coordinate1.xyz));
    let outerAlpha = rounded_mask_alpha_local(local, sampling.bounds, sampling.cornerRadiiX, sampling.cornerRadiiY);
    if (sampling.options.x < 2.5) {
        return outerAlpha;
    }
    if (sampling.options.x > 3.5) {
        let innerAlpha = rounded_mask_alpha_local(
            local,
            vec4<f32>(sampling.coordinate0.w, sampling.coordinate1.w, sampling.options.z, sampling.options.w),
            vec4<f32>(0.0),
            vec4<f32>(0.0));
        return outerAlpha * (1.0 - innerAlpha);
    }
    let inset = sampling.options.z;
    let innerAlpha = rounded_mask_alpha_local(
        local,
        sampling.bounds + vec4<f32>(inset, inset, -inset, -inset),
        max(sampling.cornerRadiiX - vec4<f32>(inset), vec4<f32>(0.0)),
        max(sampling.cornerRadiiY - vec4<f32>(inset), vec4<f32>(0.0)));
    return outerAlpha * (1.0 - innerAlpha);
}

fn analytic_rounded_mask_alpha(position: vec2<f32>) -> f32 {
    return analytic_rounded_mask_alpha_for(position, maskSampling);
}

fn sample_mask_chain_alpha(position: vec2<f32>) -> f32 {
    let targetPosition = position + uniforms.renderOrigin;
    var alpha = 1.0;
    for (var index = 0u; index < 3u; index++) {
        let sampling = maskChain.masks[index];
        if (sampling.options.x > 1.5) {
            alpha *= analytic_rounded_mask_alpha_for(targetPosition, sampling) * sampling.options.y;
        }
    }
    return alpha;
}

fn sample_mask_alpha(position: vec2<f32>) -> f32 {
    if (maskSampling.options.x < 0.5) {
        return 1.0;
    }

    let targetPosition = position + uniforms.renderOrigin;
    if (maskSampling.options.x > 1.5) {
        return analytic_rounded_mask_alpha(targetPosition) *
            maskSampling.options.y;
    }
    var uv = (targetPosition - maskSampling.coordinate0.xy) * maskSampling.coordinate1.xy;
    if (maskSampling.options.z > 0.5) {
        uv = vec2<f32>(
            dot(vec3<f32>(targetPosition, 1.0), maskSampling.coordinate0.xyz),
            dot(vec3<f32>(targetPosition, 1.0), maskSampling.coordinate1.xyz));
    }
    let sample = textureSample(maskTexture, maskSampler, clamp(uv, vec2<f32>(0.0), vec2<f32>(1.0)));
    let sampled = select(sample.r, sample.a, maskSampling.options.w > 1.5);
    let inside = all(uv >= vec2<f32>(0.0)) && all(uv <= vec2<f32>(1.0));
    let textureOpacity = select(1.0, maskSampling.options.y,
        maskSampling.options.w > 0.5);
    return select(0.0, sampled * textureOpacity, inside);
}

fn cubic_weight(x: f32, b: f32, c: f32) -> f32 {
    let ax = abs(x);
    let ax2 = ax * ax;
    let ax3 = ax2 * ax;

    if (b == 0.0 && c == 0.5) {
        let a = -0.5;
        if (ax <= 1.0) {
            return ((a + 2.0) * ax3) - ((a + 3.0) * ax2) + 1.0;
        }
        if (ax < 2.0) {
            return (a * ax3) - (5.0 * a * ax2) + (8.0 * a * ax) - (4.0 * a);
        }
        return 0.0;
    }

    if (ax <= 1.0) {
        return ((12.0 - 9.0 * b - 6.0 * c) * ax3
            + (-18.0 + 12.0 * b + 6.0 * c) * ax2
            + (6.0 - 2.0 * b)) / 6.0;
    }

    if (ax < 2.0) {
        return ((-b - 6.0 * c) * ax3
            + (6.0 * b + 30.0 * c) * ax2
            + (-12.0 * b - 48.0 * c) * ax
            + (8.0 * b + 24.0 * c)) / 6.0;
    }

    return 0.0;
}

fn address_texture_coordinate(value: f32, mode: f32) -> f32 {
    if (mode < 0.5) {
        return value;
    }
    if (mode < 1.5) {
        return fract(value);
    }
    let mirrored = fract(value * 0.5) * 2.0;
    return select(mirrored, 2.0 - mirrored, mirrored > 1.0);
}

fn address_texture_coordinates(uv: vec2<f32>, modes: vec2<f32>) -> vec2<f32> {
    return vec2<f32>(
        address_texture_coordinate(uv.x, modes.x),
        address_texture_coordinate(uv.y, modes.y));
}

fn address_texture_index(coordinate: i32, size: i32, mode: f32) -> i32 {
    if (mode < 0.5) {
        return clamp(coordinate, 0, size - 1);
    }
    if (mode < 1.5) {
        return ((coordinate % size) + size) % size;
    }
    let period = size * 2;
    let wrapped = ((coordinate % period) + period) % period;
    return select(wrapped, period - 1 - wrapped, wrapped >= size);
}

fn sample_bicubic(
    uv: vec2<f32>,
    resampler: vec2<f32>,
    addressModes: vec2<f32>) -> vec4<f32> {
    let size = textureDimensions(texTexture);
    let sizef = vec2<f32>(f32(size.x), f32(size.y));
    let texel = uv * sizef - vec2<f32>(0.5, 0.5);
    let base = floor(texel);
    let f = texel - base;
    let sizei = vec2<i32>(i32(size.x), i32(size.y));
    var color = vec4<f32>(0.0);
    var total = 0.0;

    for (var y: i32 = -1; y <= 2; y = y + 1) {
        let wy = cubic_weight(f.y - f32(y), resampler.x, resampler.y);
        for (var x: i32 = -1; x <= 2; x = x + 1) {
            let wx = cubic_weight(f.x - f32(x), resampler.x, resampler.y);
            let weight = wx * wy;
            let coord = vec2<i32>(
                address_texture_index(
                    i32(base.x) + x,
                    sizei.x,
                    addressModes.x),
                address_texture_index(
                    i32(base.y) + y,
                    sizei.y,
                    addressModes.y));
            color = color + textureLoad(texTexture, coord, 0) * weight;
            total = total + weight;
        }
    }

    return color / max(total, 0.0001);
}

// Same base-level texel-center and per-tap clamp/repeat/mirror addressing as
// bicubic sampling. Mix in the texture's existing alpha representation; never
// unpremultiply individual taps. No CPU work, intermediate image, or sampler.
const explicit_linear_coefficient: f32 = -64.0;
const explicit_nearest_coefficient: f32 = -128.0;

fn sample_bilinear_extent(uv: vec2<f32>, modes: vec2<f32>, size: vec2<i32>) -> vec4<f32> {
    let texel = uv * vec2<f32>(size) - vec2<f32>(0.5);
    let base = vec2<i32>(floor(texel));
    let f = fract(texel);
    let x0 = address_texture_index(base.x, size.x, modes.x);
    let x1 = address_texture_index(base.x + 1, size.x, modes.x);
    let y0 = address_texture_index(base.y, size.y, modes.y);
    let y1 = address_texture_index(base.y + 1, size.y, modes.y);
    let top = mix(textureLoad(texTexture, vec2<i32>(x0, y0), 0),
                  textureLoad(texTexture, vec2<i32>(x1, y0), 0), f.x);
    let bottom = mix(textureLoad(texTexture, vec2<i32>(x0, y1), 0),
                     textureLoad(texTexture, vec2<i32>(x1, y1), 0), f.x);
    return mix(top, bottom, f.y);
}

fn sample_nearest_extent(uv: vec2<f32>, modes: vec2<f32>, size: vec2<i32>) -> vec4<f32> {
    let texel = vec2<i32>(floor(uv * vec2<f32>(size)));
    let coordinate = vec2<i32>(
        address_texture_index(texel.x, size.x, modes.x),
        address_texture_index(texel.y, size.y, modes.y));
    return textureLoad(texTexture, coordinate, 0);
}

fn sample_bilinear_explicit(uv: vec2<f32>, modes: vec2<f32>) -> vec4<f32> {
    return sample_bilinear_extent(uv, modes, vec2<i32>(textureDimensions(texTexture)));
}

fn sample_nearest_explicit(uv: vec2<f32>, modes: vec2<f32>) -> vec4<f32> {
    return sample_nearest_extent(uv, modes, vec2<i32>(textureDimensions(texTexture)));
}

// patchKind -2 is a premultiplied, zero-origin retained tile page. Color RG
// carries its occupied integer texel extent, A its once-only output opacity.
// UVs are normalized to that extent, not to the pooled allocation. Sampling
// each tap inside the tile preserves bilinear repeat seams and mirror edges.
fn is_tile_page(input: VertexOutput) -> bool {
    return input.patchKind == -2.0;
}

// WPF maps BitmapScalingMode.Fant/HighQuality to a prefilter only after either
// source axis shrinks beyond the sqrt(2) threshold. The native image/cache path
// keeps the same threshold and integrates one destination-pixel
// parallelogram with a fixed stratified 4x4 footprint. This is stable under
// rotation/shear, bounded on every backend, and retains ordinary bilinear
// reconstruction for magnification and small minification.
fn sample_fant_footprint(
    uv: vec2<f32>,
    uvDx: vec2<f32>,
    uvDy: vec2<f32>,
    size: vec2<i32>,
    modes: vec2<f32>,
    occupiedPage: bool) -> vec4<f32> {
    let sizef = vec2<f32>(size);
    let texelDx = uvDx * sizef;
    let texelDy = uvDy * sizef;
    let sourceFootprintX = length(vec2<f32>(texelDx.x, texelDy.x));
    let sourceFootprintY = length(vec2<f32>(texelDx.y, texelDy.y));
    if (max(sourceFootprintX, sourceFootprintY) <= 1.41421356237) {
        if (occupiedPage) {
            return sample_bilinear_extent(address_texture_coordinates(uv, modes), modes, size);
        }
        return textureSampleGrad(texTexture, texSampler, uv, uvDx, uvDy);
    }

    var color = vec4<f32>(0.0);
    for (var y: i32 = 0; y < 4; y = y + 1) {
        let offsetY = (f32(y) + 0.5) * 0.25 - 0.5;
        for (var x: i32 = 0; x < 4; x = x + 1) {
            let offsetX = (f32(x) + 0.5) * 0.25 - 0.5;
            let sampleUv = uv + uvDx * offsetX + uvDy * offsetY;
            if (occupiedPage) {
                color = color + sample_bilinear_extent(
                    address_texture_coordinates(sampleUv, modes), modes, size);
            } else {
                color = color + textureSampleLevel(texTexture, texSampler, sampleUv, 0.0);
            }
        }
    }
    return color * 0.0625;
}

fn sample_fant_prefilter(uv: vec2<f32>, uvDx: vec2<f32>, uvDy: vec2<f32>) -> vec4<f32> {
    return sample_fant_footprint(uv, uvDx, uvDy,
        vec2<i32>(textureDimensions(texTexture)), vec2<f32>(0.0), false);
}

fn sample_image(
    input: VertexOutput, uv: vec2<f32>, modes: vec2<f32>,
    uvDx: vec2<f32>, uvDy: vec2<f32>) -> vec4<f32> {
    if (is_tile_page(input)) {
        let size = clamp(vec2<i32>(input.color.rg), vec2<i32>(1),
            vec2<i32>(textureDimensions(texTexture)));
        if (input.cubicResampler.x == explicit_nearest_coefficient) {
            return sample_nearest_extent(uv, modes, size);
        }
        if (input.cubicResampler.x == -32.0) {
            // Address each stratum from unwrapped UVs. Addressing the center
            // first would lose mirror parity for a rotated/sheared footprint.
            return sample_fant_footprint(input.texCoord, uvDx, uvDy, size, modes, true);
        }
        return sample_bilinear_extent(uv, modes, size);
    }
    // Fant/cubic are distinct algorithms, not downgraded by the base-level
    // policy. Derivatives are evaluated by callers before divergent control.
    if (input.patchKind < -0.5 || input.cubicResampler.x == -32.0) {
        return sample_fant_prefilter(uv, uvDx, uvDy);
    }
    if (input.cubicResampler.x == explicit_nearest_coefficient) {
        return sample_nearest_explicit(uv, modes);
    }
    if (input.cubicResampler.x == explicit_linear_coefficient) {
        return sample_bilinear_explicit(uv, modes);
    }
    if (input.color.a < 0.0 || (input.patchKind > 2.5 && input.patchOpacity < 0.0)) {
        return sample_bicubic(uv, input.cubicResampler, modes);
    }
    return textureSampleGrad(texTexture, texSampler, uv, uvDx, uvDy);
}

fn atlas_unpremultiply(color: vec4<f32>) -> vec4<f32> {
    if (color.a <= 0.0) {
        return vec4<f32>(0.0);
    }
    return vec4<f32>(color.rgb / color.a, color.a);
}

fn atlas_screen(backdrop: vec3<f32>, source: vec3<f32>) -> vec3<f32> {
    return backdrop + source - backdrop * source;
}

fn atlas_hard_light_component(backdrop: f32, source: f32) -> f32 {
    if (source <= 0.5) {
        return backdrop * (2.0 * source);
    }
    return backdrop + (2.0 * source - 1.0) - backdrop * (2.0 * source - 1.0);
}

fn atlas_hard_light(backdrop: vec3<f32>, source: vec3<f32>) -> vec3<f32> {
    return vec3<f32>(
        atlas_hard_light_component(backdrop.r, source.r),
        atlas_hard_light_component(backdrop.g, source.g),
        atlas_hard_light_component(backdrop.b, source.b));
}

fn atlas_color_dodge_component(backdrop: f32, source: f32) -> f32 {
    if (backdrop <= 0.0) { return 0.0; }
    if (source >= 1.0) { return 1.0; }
    return min(1.0, backdrop / (1.0 - source));
}

fn atlas_color_dodge(backdrop: vec3<f32>, source: vec3<f32>) -> vec3<f32> {
    return vec3<f32>(
        atlas_color_dodge_component(backdrop.r, source.r),
        atlas_color_dodge_component(backdrop.g, source.g),
        atlas_color_dodge_component(backdrop.b, source.b));
}

fn atlas_color_burn_component(backdrop: f32, source: f32) -> f32 {
    if (backdrop >= 1.0) { return 1.0; }
    if (source <= 0.0) { return 0.0; }
    return 1.0 - min(1.0, (1.0 - backdrop) / source);
}

fn atlas_color_burn(backdrop: vec3<f32>, source: vec3<f32>) -> vec3<f32> {
    return vec3<f32>(
        atlas_color_burn_component(backdrop.r, source.r),
        atlas_color_burn_component(backdrop.g, source.g),
        atlas_color_burn_component(backdrop.b, source.b));
}

fn atlas_soft_light_component(backdrop: f32, source: f32) -> f32 {
    if (source <= 0.5) {
        return backdrop - (1.0 - 2.0 * source) * backdrop * (1.0 - backdrop);
    }
    var curve = sqrt(backdrop);
    if (backdrop <= 0.25) {
        curve = ((16.0 * backdrop - 12.0) * backdrop + 4.0) * backdrop;
    }
    return backdrop + (2.0 * source - 1.0) * (curve - backdrop);
}

fn atlas_soft_light(backdrop: vec3<f32>, source: vec3<f32>) -> vec3<f32> {
    return vec3<f32>(
        atlas_soft_light_component(backdrop.r, source.r),
        atlas_soft_light_component(backdrop.g, source.g),
        atlas_soft_light_component(backdrop.b, source.b));
}

fn atlas_luminosity(color: vec3<f32>) -> f32 {
    return dot(color, vec3<f32>(0.3, 0.59, 0.11));
}

fn atlas_saturation(color: vec3<f32>) -> f32 {
    return max(max(color.r, color.g), color.b) - min(min(color.r, color.g), color.b);
}

fn atlas_clip_color(input: vec3<f32>) -> vec3<f32> {
    var color = input;
    let lightness = atlas_luminosity(color);
    let minimum = min(min(color.r, color.g), color.b);
    let maximum = max(max(color.r, color.g), color.b);
    if (minimum < 0.0 && lightness > minimum) {
        color = vec3<f32>(lightness) +
            (color - vec3<f32>(lightness)) * lightness / (lightness - minimum);
    }
    if (maximum > 1.0 && maximum > lightness) {
        color = vec3<f32>(lightness) +
            (color - vec3<f32>(lightness)) * (1.0 - lightness) / (maximum - lightness);
    }
    return color;
}

fn atlas_set_luminosity(color: vec3<f32>, lightness: f32) -> vec3<f32> {
    return atlas_clip_color(color + vec3<f32>(lightness - atlas_luminosity(color)));
}

fn atlas_set_saturation(color: vec3<f32>, targetSaturation: f32) -> vec3<f32> {
    let minimum = min(min(color.r, color.g), color.b);
    let maximum = max(max(color.r, color.g), color.b);
    if (maximum <= minimum) {
        return vec3<f32>(0.0);
    }
    return (color - vec3<f32>(minimum)) * targetSaturation / (maximum - minimum);
}

fn atlas_advanced_blend(backdrop: vec3<f32>, source: vec3<f32>, mode: u32) -> vec3<f32> {
    switch mode {
        case 14u: { return atlas_screen(backdrop, source); }
        case 15u: { return atlas_hard_light(source, backdrop); }
        case 16u: { return min(backdrop, source); }
        case 17u: { return max(backdrop, source); }
        case 18u: { return atlas_color_dodge(backdrop, source); }
        case 19u: { return atlas_color_burn(backdrop, source); }
        case 20u: { return atlas_hard_light(backdrop, source); }
        case 21u: { return atlas_soft_light(backdrop, source); }
        case 22u: { return abs(backdrop - source); }
        case 23u: { return backdrop + source - 2.0 * backdrop * source; }
        case 24u: { return backdrop * source; }
        case 25u: {
            return atlas_set_luminosity(
                atlas_set_saturation(source, atlas_saturation(backdrop)),
                atlas_luminosity(backdrop));
        }
        case 26u: {
            return atlas_set_luminosity(
                atlas_set_saturation(backdrop, atlas_saturation(source)),
                atlas_luminosity(backdrop));
        }
        case 27u: { return atlas_set_luminosity(source, atlas_luminosity(backdrop)); }
        case 28u: { return atlas_set_luminosity(backdrop, atlas_luminosity(source)); }
        default: { return source; }
    }
}

// Sprite is the blend source and the optional per-sprite color is the destination, matching SkCanvas.drawAtlas.
fn blend_atlas_color(source: vec4<f32>, destinationPremultiplied: vec4<f32>, mode: u32) -> vec4<f32> {
    let sourcePremultiplied = vec4<f32>(source.rgb * source.a, source.a);
    let destination = atlas_unpremultiply(destinationPremultiplied);
    var result = vec4<f32>(0.0);
    switch mode {
        case 0u: { result = vec4<f32>(0.0); }
        case 1u: { result = sourcePremultiplied; }
        case 2u: { result = destinationPremultiplied; }
        case 3u: { result = sourcePremultiplied + destinationPremultiplied * (1.0 - source.a); }
        case 4u: { result = destinationPremultiplied + sourcePremultiplied * (1.0 - destination.a); }
        case 5u: { result = sourcePremultiplied * destination.a; }
        case 6u: { result = destinationPremultiplied * source.a; }
        case 7u: { result = sourcePremultiplied * (1.0 - destination.a); }
        case 8u: { result = destinationPremultiplied * (1.0 - source.a); }
        case 9u: {
            result = sourcePremultiplied * destination.a +
                destinationPremultiplied * (1.0 - source.a);
        }
        case 10u: {
            result = destinationPremultiplied * source.a +
                sourcePremultiplied * (1.0 - destination.a);
        }
        case 11u: {
            result = sourcePremultiplied * (1.0 - destination.a) +
                destinationPremultiplied * (1.0 - source.a);
        }
        case 12u: { result = min(sourcePremultiplied + destinationPremultiplied, vec4<f32>(1.0)); }
        case 13u: { result = sourcePremultiplied * destinationPremultiplied; }
        default: {
            let mixed = clamp(
                atlas_advanced_blend(destination.rgb, source.rgb, mode),
                vec3<f32>(0.0),
                vec3<f32>(1.0));
            result = vec4<f32>(
                sourcePremultiplied.rgb * (1.0 - destination.a) +
                    destinationPremultiplied.rgb * (1.0 - source.a) +
                    mixed * source.a * destination.a,
                source.a + destination.a - source.a * destination.a);
        }
    }
    return atlas_unpremultiply(clamp(result, vec4<f32>(0.0), vec4<f32>(1.0)));
}

fn texture_fs_main_with_mask(input: VertexOutput, maskAlpha: f32) -> vec4<f32> {
    let textureCoordDx = dpdx(input.texCoord);
    let textureCoordDy = dpdy(input.texCoord);
    let addressModes = select(
        vec2<f32>(0.0),
        vec2<f32>(input.colorBlendMode, input.patchOpacity),
        input.patchKind < 0.5);
    let addressedTexCoord = address_texture_coordinates(
        input.texCoord,
        addressModes);
    if (maskAlpha <= 0.0) {
        discard;
    }

    // patchKind 1 carries straight fixed color; 2 carries premultiplied fixed color.
    if (input.patchKind > 0.5 && input.patchKind < 2.5) {
        if (input.patchKind > 1.5) {
            return vec4<f32>(input.color.rgb * maskAlpha, input.color.a * maskAlpha);
        }
        return vec4<f32>(input.color.rgb, input.color.a * maskAlpha);
    }

    var texColor = sample_image(input, addressedTexCoord, addressModes,
        textureCoordDx, textureCoordDy);
    if (input.color.b < -0.5) {
        texColor.a = 1.0;
    }

    // patchKind 3 carries straight atlas samples; 4 carries premultiplied atlas samples.
    if (input.patchKind > 2.5) {
        var source = texColor;
        if (input.patchKind > 3.5) {
            source = atlas_unpremultiply(source);
        }
        let blended = blend_atlas_color(source, input.color, u32(round(input.colorBlendMode)));
        let coverage = abs(input.patchOpacity) * maskAlpha;
        if (input.patchKind > 3.5) {
            return vec4<f32>(blended.rgb * blended.a * coverage, blended.a * coverage);
        }
        return vec4<f32>(blended.rgb, blended.a * coverage);
    }

    let opacity = abs(input.color.a);
    let sourceIsPremultiplied = is_tile_page(input) || input.color.g > 0.5;
    let rgbScale = select(input.color.r, opacity, is_tile_page(input));
    let coverage = opacity * maskAlpha;
    if (sourceIsPremultiplied) {
        return vec4<f32>(texColor.rgb * rgbScale * maskAlpha, texColor.a * coverage);
    }

    return vec4<f32>(texColor.rgb * rgbScale, texColor.a * coverage);
}

fn texture_fs_main(input: VertexOutput) -> vec4<f32> {
    let fragmentOrigin = select(
        vec2<f32>(0.0),
        uniforms.canvasSize,
        uniforms.boundedSourcePass > 0.5);
    let maskAlpha = sample_mask_alpha(input.position.xy + fragmentOrigin);
    return texture_fs_main_with_mask(input, maskAlpha);
}

@fragment
fn fs_main(input: VertexOutput) -> @location(0) vec4<f32> {
    return texture_fs_main(input);
}

@fragment
fn fs_main_chain(input: VertexOutput) -> @location(0) vec4<f32> {
    let fragmentOrigin = select(
        vec2<f32>(0.0),
        uniforms.canvasSize,
        uniforms.boundedSourcePass > 0.5);
    let position = input.position.xy + fragmentOrigin;
    let maskAlpha = sample_mask_alpha(position) *
        sample_mask_chain_alpha(position);
    return texture_fs_main_with_mask(input, maskAlpha);
}

@fragment
fn fs_main_unmasked(input: VertexOutput) -> @location(0) vec4<f32> {
    return texture_fs_main_with_mask(input, 1.0);
}

// Semantic retained images lower all fused affine color operations to this
// straight-RGBA 4x5 matrix. A 96-byte mask-shaped record stores the five vec4
// rows; the independent group-three record lets a state mask remain bound at
// group two without materializing an intermediate texture.
fn color_matrix_fs_main_with_mask(
    input: VertexOutput,
    matrix: MaskSamplingUniforms,
    maskAlpha: f32) -> vec4<f32> {
    let textureCoordDx = dpdx(input.texCoord);
    let textureCoordDy = dpdy(input.texCoord);
    let addressModes = select(
        vec2<f32>(0.0),
        vec2<f32>(input.colorBlendMode, input.patchOpacity),
        input.patchKind < 0.5);
    let addressedTexCoord = address_texture_coordinates(
        input.texCoord,
        addressModes);
    var source = sample_image(input, addressedTexCoord, addressModes,
        textureCoordDx, textureCoordDy);
    if (input.color.b < -0.5) {
        source.a = 1.0;
    }
    if (is_tile_page(input) || input.color.g > 0.5) {
        source = atlas_unpremultiply(source);
    }
    var transformed = vec4<f32>(
        dot(source, matrix.coordinate0) + matrix.cornerRadiiY.x,
        dot(source, matrix.coordinate1) + matrix.cornerRadiiY.y,
        dot(source, matrix.bounds) + matrix.cornerRadiiY.z,
        dot(source, matrix.cornerRadiiX) + matrix.cornerRadiiY.w);
    if (matrix.options.x > 0.5) {
        let luminance = dot(
            transformed.rgb,
            vec3<f32>(0.2126, 0.7152, 0.0722));
        transformed = vec4<f32>(
            0.0,
            0.0,
            0.0,
            luminance * transformed.a);
    }
    transformed = clamp(
        transformed,
        vec4<f32>(0.0),
        vec4<f32>(1.0));
    return vec4<f32>(
        transformed.rgb * select(input.color.r, abs(input.color.a), is_tile_page(input)),
        transformed.a * abs(input.color.a) * maskAlpha);
}

@fragment
fn fs_main_color_matrix_unmasked(input: VertexOutput) -> @location(0) vec4<f32> {
    return color_matrix_fs_main_with_mask(input, maskSampling, 1.0);
}

@fragment
fn fs_main_color_matrix(input: VertexOutput) -> @location(0) vec4<f32> {
    let fragmentOrigin = select(
        vec2<f32>(0.0),
        uniforms.canvasSize,
        uniforms.boundedSourcePass > 0.5);
    let maskAlpha = sample_mask_alpha(input.position.xy + fragmentOrigin);
    return color_matrix_fs_main_with_mask(
        input,
        colorMatrixSampling,
        maskAlpha);
}

@fragment
fn fs_main_color_matrix_chain(input: VertexOutput) -> @location(0) vec4<f32> {
    let fragmentOrigin = select(
        vec2<f32>(0.0),
        uniforms.canvasSize,
        uniforms.boundedSourcePass > 0.5);
    let position = input.position.xy + fragmentOrigin;
    let maskAlpha = sample_mask_alpha(position) *
        sample_mask_chain_alpha(position);
    return color_matrix_fs_main_with_mask(
        input,
        colorMatrixSampling,
        maskAlpha);
}

@fragment
fn fs_main_premultiplied(input: VertexOutput) -> @location(0) vec4<f32> {
    let color = texture_fs_main(input);
    return vec4<f32>(color.rgb * color.a, color.a);
}

@fragment
fn fs_main_premultiplied_unmasked(input: VertexOutput) -> @location(0) vec4<f32> {
    let color = texture_fs_main_with_mask(input, 1.0);
    return vec4<f32>(color.rgb * color.a, color.a);
}

@fragment
fn fs_mask(input: VertexOutput) -> @location(0) vec4<f32> {
    let color = texture_fs_main(input);
    return vec4<f32>(color.a, 0.0, 0.0, 1.0);
}

@fragment
fn fs_mask_unmasked(input: VertexOutput) -> @location(0) vec4<f32> {
    let color = texture_fs_main_with_mask(input, 1.0);
    return vec4<f32>(color.a, 0.0, 0.0, 1.0);
}
