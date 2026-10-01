// Algorithm: Reuse original image sampling with per-tap address modes, original nearest/linear/cubic/Fant coefficients and untouched source-alpha representation.
// Time complexity: O(1); original fixed footprints use at most 64 texel loads.
// Space complexity: O(1), no CPU sampling or resampled intermediate image.
struct TextureImageSampleInput {
    color: vec4<f32>,
    texCoord: vec2<f32>,
    cubicResampler: vec2<f32>,
    patchKind: f32,
    patchOpacity: f32,
};

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
const explicit_fant_coefficient: f32 = -256.0;

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
fn texture_is_tile_page(patchKind: f32) -> bool {
    return patchKind == -2.0;
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
    explicitSampling: bool) -> vec4<f32> {
    let sizef = vec2<f32>(size);
    let texelDx = uvDx * sizef;
    let texelDy = uvDy * sizef;
    let sourceFootprintX = length(vec2<f32>(texelDx.x, texelDy.x));
    let sourceFootprintY = length(vec2<f32>(texelDx.y, texelDy.y));
    if (max(sourceFootprintX, sourceFootprintY) <= 1.41421356237) {
        if (explicitSampling) {
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
            if (explicitSampling) {
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
    input: TextureImageSampleInput, uv: vec2<f32>, modes: vec2<f32>,
    uvDx: vec2<f32>, uvDy: vec2<f32>) -> vec4<f32> {
    if (texture_is_tile_page(input.patchKind)) {
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
    // Full-image explicit Fant keeps the same footprint/threshold, replacing
    // only each bilinear sampler operation. Preserve unwrapped mirror phase.
    if (input.cubicResampler.x == explicit_fant_coefficient) {
        return sample_fant_footprint(input.texCoord, uvDx, uvDy,
            vec2<i32>(textureDimensions(texTexture)), modes, true);
    }
    // Derivatives are evaluated by callers before divergent control.
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
