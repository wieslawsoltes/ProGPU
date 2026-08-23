// Algorithm: Transform an image-effect quad, optionally map each perspective ray into an equirectangular source, sample RGB or planar YUV with an optional bounded 2D Gaussian footprint, apply fused color operations, and combine texture-mask or up to four affine analytic rounded-mask coverages.
// Time complexity: O(1) per vertex and fragment because spherical mapping and the maximum four-mask chain are fixed work and blur radius R is clamped to 5; RGB performs 1 source sample without blur or at most 121 samples, planar YUV performs 2 or at most 242 samples, and a texture mask adds at most one sample.
// Space complexity: O(1) local/private storage per invocation and bounded uniform storage; spherical mapping, planar conversion, fused effects, and analytic mask chains use no intermediate texture, while blur has a fixed bounded source-texture bandwidth cost.
struct VSUniforms {
    projection: mat4x4<f32>,
    mvp: mat4x4<f32>,
    view: mat4x4<f32>,
    canvasSize: vec2<f32>,
    dpiScale: f32,
    boundedSourcePass: f32,
    renderOrigin: vec2<f32>,
    pad1: vec2<f32>,
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
    yuvRange: vec4<f32>,
    yuvRed: vec4<f32>,
    yuvGreen: vec4<f32>,
    yuvBlue: vec4<f32>,
    spherical0: vec4<f32>,
    sphericalUvRect: vec4<f32>,
    sphericalRotation0: vec4<f32>,
    sphericalRotation1: vec4<f32>,
    sphericalRotation2: vec4<f32>,
};

@group(1) @binding(0) var<uniform> effect: EffectUniforms;

@group(2) @binding(0) var texSampler: sampler;
@group(2) @binding(1) var texTexture: texture_2d<f32>;
@group(2) @binding(2) var chromaTexture: texture_2d<f32>;

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

struct MaskChainUniforms {
    masks: array<MaskSamplingUniforms, 3>,
};

@group(3) @binding(3) var<uniform> maskChain: MaskChainUniforms;

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

fn analytic_rounded_mask_alpha_for(
    position: vec2<f32>,
    sampling: MaskSamplingUniforms) -> f32 {
    let local = vec2<f32>(
        dot(vec3<f32>(position, 1.0), sampling.coordinate0.xyz),
        dot(vec3<f32>(position, 1.0), sampling.coordinate1.xyz));
    let outerAlpha = rounded_mask_alpha_local(local, sampling.bounds, sampling.cornerRadiiX, sampling.cornerRadiiY);
    if (sampling.options.x < 2.5) {
        return outerAlpha;
    }
    let inset = sampling.options.z;
    let innerAlpha = rounded_mask_alpha_local(
        local,
        sampling.bounds + vec4<f32>(inset, inset, -inset, -inset),
        max(sampling.cornerRadiiX - vec4<f32>(inset), vec4<f32>(0.0)),
        max(sampling.cornerRadiiY - vec4<f32>(inset), vec4<f32>(0.0)));
    return outerAlpha * (1.0 - innerAlpha);
}

fn sample_mask_alpha(position: vec2<f32>) -> f32 {
    if (maskSampling.options.x < 0.5) {
        return 1.0;
    }
    let targetPosition = position + uniforms.renderOrigin;
    if (maskSampling.options.x > 1.5) {
        return analytic_rounded_mask_alpha_for(targetPosition, maskSampling) *
            maskSampling.options.y;
    }
    let uv = (targetPosition - maskSampling.coordinate0.xy) * maskSampling.coordinate1.xy;
    let sampled = textureSample(maskTexture, maskSampler, clamp(uv, vec2<f32>(0.0), vec2<f32>(1.0))).r;
    let inside = all(uv >= vec2<f32>(0.0)) && all(uv <= vec2<f32>(1.0));
    return select(0.0, sampled, inside);
}

fn sample_mask_chain_alpha(position: vec2<f32>) -> f32 {
    let targetPosition = position + uniforms.renderOrigin;
    var alpha = 1.0;
    for (var index = 0u; index < 3u; index++) {
        let sampling = maskChain.masks[index];
        if (sampling.options.x > 1.5) {
            alpha *= analytic_rounded_mask_alpha_for(
                targetPosition,
                sampling) * sampling.options.y;
        }
    }
    return alpha;
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

fn sample_source(texCoord: vec2<f32>) -> vec4<f32> {
    if (effect.flags0.x > 0.5) {
        let rawY = textureSample(
            texTexture,
            texSampler,
            texCoord).r;
        let rawChroma = textureSample(
            chromaTexture,
            texSampler,
            texCoord).rg;
        let components = vec3<f32>(
            (rawY - effect.yuvRange.x) *
                effect.yuvRange.y,
            (rawChroma.x - effect.yuvRange.z) *
                effect.yuvRange.w,
            (rawChroma.y - effect.yuvRange.z) *
                effect.yuvRange.w);
        return vec4<f32>(
            dot(components, effect.yuvRed.xyz),
            dot(components, effect.yuvGreen.xyz),
            dot(components, effect.yuvBlue.xyz),
            1.0);
    }
    return textureSample(texTexture, texSampler, texCoord);
}

fn project_source_coordinate(texCoord: vec2<f32>) -> vec2<f32> {
    if (effect.spherical0.x < 0.5) {
        return texCoord;
    }

    let localCoordinate =
        (texCoord - effect.sphericalUvRect.xy) /
        max(
            effect.sphericalUvRect.zw,
            vec2<f32>(0.000001));
    let output = vec2<f32>(
        localCoordinate.x * 2.0 - 1.0,
        1.0 - localCoordinate.y * 2.0);
    let tanHalfHorizontal =
        tan(effect.spherical0.y * 0.5);
    let tanHalfVertical =
        tanHalfHorizontal /
        max(effect.spherical0.z, 0.000001);
    let viewRay = normalize(vec3<f32>(
        output.x * tanHalfHorizontal,
        output.y * tanHalfVertical,
        1.0));
    let direction = normalize(vec3<f32>(
        dot(viewRay, effect.sphericalRotation0.xyz),
        dot(viewRay, effect.sphericalRotation1.xyz),
        dot(viewRay, effect.sphericalRotation2.xyz)));
    let longitude = atan2(direction.x, direction.z);
    let latitude = asin(clamp(direction.y, -1.0, 1.0));
    let equirectangular = vec2<f32>(
        0.5 + longitude * 0.15915494309189535,
        0.5 - latitude * 0.3183098861837907);
    return effect.sphericalUvRect.xy +
        equirectangular * effect.sphericalUvRect.zw;
}

fn render_image_effect(
    input: VertexOutput,
    maskAlpha: f32) -> vec4<f32> {
    var color = vec4<f32>(0.0);
    let sourceCoordinate =
        project_source_coordinate(input.texCoord);

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
                color = color +
                    sample_source(sourceCoordinate + offset) *
                    weight;
                totalWeight = totalWeight + weight;
            }
        }
        color = color / totalWeight;
    } else {
        color = sample_source(sourceCoordinate);
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

    let coverage = input.color.a * maskAlpha;
    if (effect.texture0.w > 0.5) {
        return vec4<f32>(straightColor.rgb * straightColor.a * input.color.rgb * coverage, straightColor.a * coverage);
    }

    return vec4<f32>(straightColor.rgb * input.color.rgb, straightColor.a * coverage);
}

@fragment
fn fs_main(input: VertexOutput) -> @location(0) vec4<f32> {
    var maskAlpha = 1.0;
    if (effect.effects1.w > 0.5) {
        maskAlpha = sample_mask_alpha(input.position.xy);
    }
    return render_image_effect(input, maskAlpha);
}

@fragment
fn fs_main_chain(input: VertexOutput) -> @location(0) vec4<f32> {
    return render_image_effect(
        input,
        sample_mask_chain_alpha(input.position.xy));
}
