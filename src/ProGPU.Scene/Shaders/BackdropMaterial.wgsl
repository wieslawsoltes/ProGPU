// Algorithm: Sample backdrop and optional noise/mask inputs, then evaluate tint, luminosity, and material compositing.
// Time complexity: O(1) per vertex and fragment with a fixed sample footprint.
// Space complexity: O(1) local storage and bounded texture bandwidth.
struct VSUniforms {
    projection: mat4x4<f32>,
    mvp: mat4x4<f32>,
    view: mat4x4<f32>,
};

@group(0) @binding(0) var<uniform> uniforms: VSUniforms;

struct MaterialUniforms {
    tintColor: vec4<f32>,
    luminosityColor: vec4<f32>,
    fallbackColor: vec4<f32>,
    noiseColor: vec4<f32>,
    material0: vec4<f32>,
    material1: vec4<f32>,
    geometry0: vec4<f32>,
    radiiX: vec4<f32>,
    radiiY: vec4<f32>,
    flags0: vec4<f32>,
    sourceUvRect: vec4<f32>,
};

@group(1) @binding(0) var<uniform> material: MaterialUniforms;

@group(2) @binding(0) var sourceSampler: sampler;
@group(2) @binding(1) var sourceTexture: texture_2d<f32>;

@group(3) @binding(0) var maskSampler: sampler;
@group(3) @binding(1) var maskTexture: texture_2d<f32>;

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

fn premultiply(color: vec4<f32>) -> vec4<f32> {
    return vec4<f32>(color.rgb * color.a, color.a);
}

fn source_over(destination: vec4<f32>, source: vec4<f32>) -> vec4<f32> {
    return source + destination * (1.0 - source.a);
}

fn sample_backdrop(uv: vec2<f32>) -> vec4<f32> {
    let blurRadius = max(material.material1.x, 0.0);
    if (blurRadius <= 0.01) {
        return textureSample(sourceTexture, sourceSampler, uv);
    }

    let textureSize = vec2<f32>(textureDimensions(sourceTexture));
    let texel = vec2<f32>(1.0) / max(textureSize, vec2<f32>(1.0));
    let inner = texel * blurRadius * 0.35;
    let outer = texel * blurRadius * 0.75;

    var color = textureSample(sourceTexture, sourceSampler, uv) * 0.20;
    color += textureSample(sourceTexture, sourceSampler, uv + vec2<f32>(inner.x, 0.0)) * 0.10;
    color += textureSample(sourceTexture, sourceSampler, uv - vec2<f32>(inner.x, 0.0)) * 0.10;
    color += textureSample(sourceTexture, sourceSampler, uv + vec2<f32>(0.0, inner.y)) * 0.10;
    color += textureSample(sourceTexture, sourceSampler, uv - vec2<f32>(0.0, inner.y)) * 0.10;
    color += textureSample(sourceTexture, sourceSampler, uv + inner) * 0.075;
    color += textureSample(sourceTexture, sourceSampler, uv - inner) * 0.075;
    color += textureSample(sourceTexture, sourceSampler, uv + vec2<f32>(inner.x, -inner.y)) * 0.075;
    color += textureSample(sourceTexture, sourceSampler, uv + vec2<f32>(-inner.x, inner.y)) * 0.075;
    color += textureSample(sourceTexture, sourceSampler, uv + vec2<f32>(outer.x, 0.0)) * 0.025;
    color += textureSample(sourceTexture, sourceSampler, uv - vec2<f32>(outer.x, 0.0)) * 0.025;
    color += textureSample(sourceTexture, sourceSampler, uv + vec2<f32>(0.0, outer.y)) * 0.025;
    color += textureSample(sourceTexture, sourceSampler, uv - vec2<f32>(0.0, outer.y)) * 0.025;
    return color;
}

fn ellipse_coverage(point: vec2<f32>, center: vec2<f32>, radii: vec2<f32>) -> f32 {
    let safeRadii = max(radii, vec2<f32>(0.0001));
    let distance = length((point - center) / safeRadii);
    let antialias = max(fwidth(distance), 0.001);
    return 1.0 - smoothstep(1.0 - antialias, 1.0 + antialias, distance);
}

fn rounded_rect_coverage(uv: vec2<f32>) -> f32 {
    let size = max(material.geometry0.xy, vec2<f32>(0.0001));
    let point = uv * size;
    let halfSize = size * 0.5;
    let radiiX = clamp(material.radiiX, vec4<f32>(0.0), vec4<f32>(halfSize.x));
    let radiiY = clamp(material.radiiY, vec4<f32>(0.0), vec4<f32>(halfSize.y));

    if (radiiX.x > 0.0 && radiiY.x > 0.0 && point.x < radiiX.x && point.y < radiiY.x) {
        return ellipse_coverage(point, vec2<f32>(radiiX.x, radiiY.x), vec2<f32>(radiiX.x, radiiY.x));
    }
    if (radiiX.y > 0.0 && radiiY.y > 0.0 && point.x > size.x - radiiX.y && point.y < radiiY.y) {
        return ellipse_coverage(point, vec2<f32>(size.x - radiiX.y, radiiY.y), vec2<f32>(radiiX.y, radiiY.y));
    }
    if (radiiX.z > 0.0 && radiiY.z > 0.0 && point.x > size.x - radiiX.z && point.y > size.y - radiiY.z) {
        return ellipse_coverage(point, vec2<f32>(size.x - radiiX.z, size.y - radiiY.z), vec2<f32>(radiiX.z, radiiY.z));
    }
    if (radiiX.w > 0.0 && radiiY.w > 0.0 && point.x < radiiX.w && point.y > size.y - radiiY.w) {
        return ellipse_coverage(point, vec2<f32>(radiiX.w, size.y - radiiY.w), vec2<f32>(radiiX.w, radiiY.w));
    }

    return 1.0;
}

fn random_noise(position: vec2<f32>) -> f32 {
    let value = dot(floor(position), vec2<f32>(12.9898, 78.233));
    return fract(sin(value) * 43758.5453);
}

@fragment
fn fs_main(input: VertexOutput) -> @location(0) vec4<f32> {
    let hasSource = material.flags0.x > 0.5;
    let hasMask = material.flags0.y > 0.5;
    let sourceIsPremultiplied = material.flags0.z > 0.5;
    let useFallback = material.material1.w > 0.5;
    let kind = material.material1.z;

    var backdrop = vec4<f32>(0.0);
    if (hasSource) {
        backdrop = sample_backdrop(input.texCoord);
        if (sourceIsPremultiplied && backdrop.a > 0.00001) {
            backdrop = vec4<f32>(backdrop.rgb / backdrop.a, backdrop.a);
        }

        let luminance = dot(backdrop.rgb, vec3<f32>(0.2126, 0.7152, 0.0722));
        backdrop = vec4<f32>(
            mix(vec3<f32>(luminance), backdrop.rgb, max(material.material1.y, 0.0)),
            backdrop.a);
    }

    var result = premultiply(backdrop);
    if (useFallback || kind >= 3.5) {
        result = premultiply(material.fallbackColor);
    } else if (kind >= 2.5) {
        result = source_over(result, premultiply(vec4<f32>(
            material.tintColor.rgb,
            clamp(material.tintColor.a * material.material0.x, 0.0, 1.0))));
    } else if (kind < 1.5) {
        let luminosity = vec4<f32>(
            material.luminosityColor.rgb,
            clamp(material.luminosityColor.a * material.material0.y, 0.0, 1.0));
        let tint = vec4<f32>(
            material.tintColor.rgb,
            clamp(material.tintColor.a * material.material0.x, 0.0, 1.0));
        result = source_over(result, premultiply(luminosity));
        result = source_over(result, premultiply(tint));
    }

    let noiseOpacity = clamp(material.material0.w, 0.0, 1.0);
    if (noiseOpacity > 0.0001 && kind < 2.0) {
        let noise = random_noise(input.position.xy);
        let noiseSource = vec4<f32>(
            material.noiseColor.rgb * noise * noiseOpacity,
            noiseOpacity);
        result = source_over(result, noiseSource);
    }

    var maskAlpha = 1.0;
    if (hasMask) {
        let maskSize = max(material.geometry0.zw, vec2<f32>(1.0));
        let screenUv = input.position.xy / maskSize;
        maskAlpha = textureSample(maskTexture, maskSampler, screenUv).r;
    }

    let sourceUvSize = max(material.sourceUvRect.zw - material.sourceUvRect.xy, vec2<f32>(0.0001));
    let localUv = (input.texCoord - material.sourceUvRect.xy) / sourceUvSize;
    let coverage = rounded_rect_coverage(localUv) *
        input.color.a *
        maskAlpha *
        clamp(material.material0.z, 0.0, 1.0);
    return clamp(result * coverage, vec4<f32>(0.0), vec4<f32>(1.0));
}
