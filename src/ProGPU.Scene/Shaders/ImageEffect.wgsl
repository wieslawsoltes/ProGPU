// Algorithm: Transform an image-effect quad, sample the source and either texture-mask or affine analytic rounded-mask coverage, then apply color/effect parameters.
// Time complexity: O(1) per vertex and fragment.
// Space complexity: O(1) local storage; texture masks add one sample while analytic rounded and uniform-opacity masks add fixed derivative arithmetic and no mask texture bandwidth.
struct VSUniforms {
    projection: mat4x4<f32>,
    mvp: mat4x4<f32>,
    view: mat4x4<f32>,
};

@group(0) @binding(0) var<uniform> uniforms: VSUniforms;

struct EffectUniforms {
    colorMatrixRed: vec4<f32>,
    colorMatrixGreen: vec4<f32>,
    colorMatrixBlue: vec4<f32>,
    colorMatrixAlpha: vec4<f32>,
    colorMatrixOffset: vec4<f32>,
    effects0: vec4<f32>,
    effects1: vec4<f32>,
    texture0: vec4<f32>,
    flags0: vec4<f32>,
};

@group(1) @binding(0) var<uniform> effect: EffectUniforms;

@group(2) @binding(0) var texSampler: sampler;
@group(2) @binding(1) var texTexture: texture_2d<f32>;

@group(3) @binding(0) var maskSampler: sampler;
@group(3) @binding(1) var maskTexture: texture_2d<f32>;

struct MaskSamplingUniforms {
    coordinate0: vec4<f32>,
    coordinate1: vec4<f32>,
    bounds: vec4<f32>,
    cornerRadiiX: vec4<f32>,
    cornerRadiiY: vec4<f32>,
    options: vec4<f32>,
};

@group(3) @binding(2) var<uniform> maskSampling: MaskSamplingUniforms;

fn analytic_rounded_mask_alpha(position: vec2<f32>) -> f32 {
    let local = vec2<f32>(
        dot(vec3<f32>(position, 1.0), maskSampling.coordinate0.xyz),
        dot(vec3<f32>(position, 1.0), maskSampling.coordinate1.xyz));
    let bounds = maskSampling.bounds;
    let edge = max(max(bounds.x - local.x, local.x - bounds.z), max(bounds.y - local.y, local.y - bounds.w));
    var center = vec2<f32>(0.0);
    var radius = vec2<f32>(0.0);
    var usesCorner = false;
    if (local.x < bounds.x + maskSampling.cornerRadiiX.x && local.y < bounds.y + maskSampling.cornerRadiiY.x) {
        radius = vec2<f32>(maskSampling.cornerRadiiX.x, maskSampling.cornerRadiiY.x);
        center = vec2<f32>(bounds.x + radius.x, bounds.y + radius.y);
        usesCorner = all(radius > vec2<f32>(0.0));
    } else if (local.x > bounds.z - maskSampling.cornerRadiiX.y && local.y < bounds.y + maskSampling.cornerRadiiY.y) {
        radius = vec2<f32>(maskSampling.cornerRadiiX.y, maskSampling.cornerRadiiY.y);
        center = vec2<f32>(bounds.z - radius.x, bounds.y + radius.y);
        usesCorner = all(radius > vec2<f32>(0.0));
    } else if (local.x > bounds.z - maskSampling.cornerRadiiX.z && local.y > bounds.w - maskSampling.cornerRadiiY.z) {
        radius = vec2<f32>(maskSampling.cornerRadiiX.z, maskSampling.cornerRadiiY.z);
        center = vec2<f32>(bounds.z - radius.x, bounds.w - radius.y);
        usesCorner = all(radius > vec2<f32>(0.0));
    } else if (local.x < bounds.x + maskSampling.cornerRadiiX.w && local.y > bounds.w - maskSampling.cornerRadiiY.w) {
        radius = vec2<f32>(maskSampling.cornerRadiiX.w, maskSampling.cornerRadiiY.w);
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

fn sample_mask_alpha(position: vec2<f32>) -> f32 {
    if (maskSampling.options.x < 0.5) {
        return 1.0;
    }
    if (maskSampling.options.x > 1.5) {
        return analytic_rounded_mask_alpha(position) *
            maskSampling.options.y;
    }
    let uv = (position - maskSampling.coordinate0.xy) * maskSampling.coordinate1.xy;
    let sampled = textureSample(maskTexture, maskSampler, clamp(uv, vec2<f32>(0.0), vec2<f32>(1.0))).r;
    let inside = all(uv >= vec2<f32>(0.0)) && all(uv <= vec2<f32>(1.0));
    return select(0.0, sampled, inside);
}

struct VertexInput {
    @location(0) position: vec2<f32>,
    @location(1) color: vec4<f32>,
    @location(2) texCoord: vec2<f32>,
};

struct VertexOutput {
    @builtin(position) position: vec4<f32>,
    @location(0) color: vec4<f32>,
    @location(1) texCoord: vec2<f32>,
};

@vertex
fn vs_main(input: VertexInput) -> VertexOutput {
    var output: VertexOutput;
    output.position = uniforms.projection * vec4<f32>(input.position, 0.0, 1.0);
    output.color = input.color;
    output.texCoord = input.texCoord;
    return output;
}

@fragment
fn fs_main(input: VertexOutput) -> @location(0) vec4<f32> {
    var color = vec4<f32>(0.0);

    let sigma = effect.effects1.z;
    if (sigma > 0.01) {
        let texSize = vec2<f32>(textureDimensions(texTexture));
        let texel = vec2<f32>(1.0) / texSize;

        var totalWeight = 0.0;
        let radius = i32(clamp(sigma * 2.0, 1.0, 5.0));

        for (var dy = -radius; dy <= radius; dy = dy + 1) {
            for (var dx = -radius; dx <= radius; dx = dx + 1) {
                let offset = vec2<f32>(f32(dx), f32(dy)) * texel;
                let weight = exp(-f32(dx * dx + dy * dy) / (2.0 * sigma * sigma));
                color = color + textureSample(texTexture, texSampler, input.texCoord + offset) * weight;
                totalWeight = totalWeight + weight;
            }
        }
        color = color / totalWeight;
    } else {
        color = textureSample(texTexture, texSampler, input.texCoord);
    }

    var straightColor = color;
    if (effect.texture0.z > 0.5) {
        if (straightColor.a > 0.00001) {
            straightColor = vec4<f32>(straightColor.rgb / straightColor.a, straightColor.a);
        } else {
            straightColor = vec4<f32>(0.0);
        }
    }

    // Apply brightness
    straightColor.r = straightColor.r + effect.effects0.x;
    straightColor.g = straightColor.g + effect.effects0.x;
    straightColor.b = straightColor.b + effect.effects0.x;

    // Apply contrast
    straightColor.r = (straightColor.r - 0.5) * effect.effects0.y + 0.5;
    straightColor.g = (straightColor.g - 0.5) * effect.effects0.y + 0.5;
    straightColor.b = (straightColor.b - 0.5) * effect.effects0.y + 0.5;

    // Apply saturation
    let luminance = dot(straightColor.rgb, vec3<f32>(0.2126, 0.7152, 0.0722));
    straightColor.r = mix(luminance, straightColor.r, effect.effects0.z);
    straightColor.g = mix(luminance, straightColor.g, effect.effects0.z);
    straightColor.b = mix(luminance, straightColor.b, effect.effects0.z);

    // Apply grayscale
    let gray = vec3<f32>(luminance);
    straightColor = vec4<f32>(mix(straightColor.rgb, gray, effect.effects0.w), straightColor.a);

    // Apply sepia
    let sepiaColor = vec3<f32>(
        straightColor.r * 0.393 + straightColor.g * 0.769 + straightColor.b * 0.189,
        straightColor.r * 0.349 + straightColor.g * 0.686 + straightColor.b * 0.168,
        straightColor.r * 0.272 + straightColor.g * 0.534 + straightColor.b * 0.131
    );
    straightColor = vec4<f32>(mix(straightColor.rgb, sepiaColor, effect.effects1.x), straightColor.a);

    // Apply invert
    let inverted = vec3<f32>(1.0) - straightColor.rgb;
    straightColor = vec4<f32>(mix(straightColor.rgb, inverted, effect.effects1.y), straightColor.a);

    if (effect.flags0.w > 0.5) {
        let maskLuminance = dot(straightColor.rgb, vec3<f32>(0.2126, 0.7152, 0.0722));
        // Skia's luma color filter emits transparent black with luminance in alpha.
        straightColor = vec4<f32>(0.0, 0.0, 0.0, maskLuminance * straightColor.a);
    }

    if (effect.flags0.z > 0.5) {
        let matrixSource = straightColor;
        straightColor = vec4<f32>(
            dot(matrixSource, effect.colorMatrixRed) + effect.colorMatrixOffset.r,
            dot(matrixSource, effect.colorMatrixGreen) + effect.colorMatrixOffset.g,
            dot(matrixSource, effect.colorMatrixBlue) + effect.colorMatrixOffset.b,
            dot(matrixSource, effect.colorMatrixAlpha) + effect.colorMatrixOffset.a
        );
    }

    straightColor = clamp(straightColor, vec4<f32>(0.0), vec4<f32>(1.0));

    var maskAlpha = 1.0;
    if (effect.effects1.w > 0.5) {
        maskAlpha = sample_mask_alpha(input.position.xy);
    }

    let coverage = input.color.a * maskAlpha;
    if (effect.texture0.w > 0.5) {
        return vec4<f32>(straightColor.rgb * straightColor.a * input.color.rgb * coverage, straightColor.a * coverage);
    }

    return vec4<f32>(straightColor.rgb * input.color.rgb, straightColor.a * coverage);
}
