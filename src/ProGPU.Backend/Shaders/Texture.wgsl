// Algorithm: Transform batched image/lattice/atlas quads, emit fixed-color cells without sampling, or sample nearest, linear, or Mitchell-Netravali cubic kernels; atlas sprites optionally combine sampled source and per-sprite destination colors with a Skia blend mode.
// Time complexity: O(1) per invocation; fixed-color cells perform no image sample, cubic filtering performs a fixed 4x4 sample footprint, and atlas color blending uses bounded scalar work.
// Space complexity: O(1) local storage and O(1) bounded texture bandwidth per fragment; one indexed batch stores four vertices and six indices per visible lattice cell or atlas sprite.
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

fn sample_bicubic(uv: vec2<f32>, resampler: vec2<f32>) -> vec4<f32> {
    let size = textureDimensions(texTexture);
    let sizef = vec2<f32>(f32(size.x), f32(size.y));
    let texel = uv * sizef - vec2<f32>(0.5, 0.5);
    let base = floor(texel);
    let f = texel - base;
    let maxCoord = vec2<i32>(i32(size.x) - 1, i32(size.y) - 1);
    var color = vec4<f32>(0.0);
    var total = 0.0;

    for (var y: i32 = -1; y <= 2; y = y + 1) {
        let wy = cubic_weight(f.y - f32(y), resampler.x, resampler.y);
        for (var x: i32 = -1; x <= 2; x = x + 1) {
            let wx = cubic_weight(f.x - f32(x), resampler.x, resampler.y);
            let weight = wx * wy;
            let coord = clamp(
                vec2<i32>(i32(base.x) + x, i32(base.y) + y),
                vec2<i32>(0, 0),
                maxCoord);
            color = color + textureLoad(texTexture, coord, 0) * weight;
            total = total + weight;
        }
    }

    return color / max(total, 0.0001);
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

fn texture_fs_main(input: VertexOutput) -> vec4<f32> {
    let textureCoordDx = dpdx(input.texCoord);
    let textureCoordDy = dpdy(input.texCoord);
    let screen_uv = input.position.xy / uniforms.canvasSize;
    let maskAlpha = textureSample(maskTexture, maskSampler, screen_uv).r;
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

    var texColor = textureSampleGrad(texTexture, texSampler, input.texCoord, textureCoordDx, textureCoordDy);
    if (input.color.a < 0.0 || (input.patchKind > 2.5 && input.patchOpacity < 0.0)) {
        texColor = sample_bicubic(input.texCoord, input.cubicResampler);
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
    let sourceIsPremultiplied = input.color.g > 0.5;
    let rgbScale = input.color.r;
    let coverage = opacity * maskAlpha;
    if (sourceIsPremultiplied) {
        return vec4<f32>(texColor.rgb * rgbScale * maskAlpha, texColor.a * coverage);
    }

    return vec4<f32>(texColor.rgb * rgbScale, texColor.a * coverage);
}

@fragment
fn fs_main(input: VertexOutput) -> @location(0) vec4<f32> {
    return texture_fs_main(input);
}

@fragment
fn fs_main_premultiplied(input: VertexOutput) -> @location(0) vec4<f32> {
    let color = texture_fs_main(input);
    return vec4<f32>(color.rgb * color.a, color.a);
}

@fragment
fn fs_mask(input: VertexOutput) -> @location(0) vec4<f32> {
    let color = texture_fs_main(input);
    return vec4<f32>(color.a, 0.0, 0.0, 1.0);
}
