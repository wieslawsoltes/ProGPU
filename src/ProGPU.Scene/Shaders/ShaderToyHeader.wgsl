// Algorithm: Transform the ShaderToy quad, expose time/resolution/frame/mouse inputs, and combine user output with either texture-mask or affine analytic rounded-mask coverage.
// Time complexity: O(1) per vertex; total fragment cost is defined by the appended user shader.
// Space complexity: O(1) header-local storage; analytic rounded and uniform-opacity masks add no texture bandwidth.
struct VSUniforms {
    projection: mat4x4<f32>,
    mvp: mat4x4<f32>,
    view: mat4x4<f32>,
};

@group(0) @binding(0) var<uniform> uniforms: VSUniforms;

struct ShaderToyUniforms {
    iResolution: vec3<f32>,
    iTime: f32,
    iTimeDelta: f32,
    iFrame: i32,
    iFrameRate: f32,
    _pad0: f32,
    iMouse: vec4<f32>,
    iDate: vec4<f32>,
};

@group(1) @binding(0) var<uniform> inputs: ShaderToyUniforms;

@group(2) @binding(0) var activeMaskSampler: sampler;
@group(2) @binding(1) var activeMaskTexture: texture_2d<f32>;

struct MaskSamplingUniforms {
    coordinate0: vec4<f32>,
    coordinate1: vec4<f32>,
    bounds: vec4<f32>,
    cornerRadiiX: vec4<f32>,
    cornerRadiiY: vec4<f32>,
    options: vec4<f32>,
};

@group(2) @binding(2) var<uniform> activeMaskSampling: MaskSamplingUniforms;

fn active_rounded_mask_alpha_local(
    local: vec2<f32>,
    bounds: vec4<f32>,
    radiiX: vec4<f32>,
    radiiY: vec4<f32>) -> f32 {
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

fn analytic_active_rounded_mask_alpha(position: vec2<f32>) -> f32 {
    let local = vec2<f32>(
        dot(vec3<f32>(position, 1.0), activeMaskSampling.coordinate0.xyz),
        dot(vec3<f32>(position, 1.0), activeMaskSampling.coordinate1.xyz));
    let outerAlpha = active_rounded_mask_alpha_local(
        local,
        activeMaskSampling.bounds,
        activeMaskSampling.cornerRadiiX,
        activeMaskSampling.cornerRadiiY);
    if (activeMaskSampling.options.x < 2.5) {
        return outerAlpha;
    }
    if (activeMaskSampling.options.x > 3.5) {
        let innerAlpha = active_rounded_mask_alpha_local(
            local,
            vec4<f32>(activeMaskSampling.coordinate0.w, activeMaskSampling.coordinate1.w, activeMaskSampling.options.z, activeMaskSampling.options.w),
            vec4<f32>(0.0),
            vec4<f32>(0.0));
        return outerAlpha * (1.0 - innerAlpha);
    }
    let inset = activeMaskSampling.options.z;
    let innerAlpha = active_rounded_mask_alpha_local(
        local,
        activeMaskSampling.bounds + vec4<f32>(inset, inset, -inset, -inset),
        max(activeMaskSampling.cornerRadiiX - vec4<f32>(inset), vec4<f32>(0.0)),
        max(activeMaskSampling.cornerRadiiY - vec4<f32>(inset), vec4<f32>(0.0)));
    return outerAlpha * (1.0 - innerAlpha);
}

fn sample_active_mask_alpha(position: vec2<f32>) -> f32 {
    if (activeMaskSampling.options.x < 0.5) {
        return 1.0;
    }
    if (activeMaskSampling.options.x > 1.5) {
        return analytic_active_rounded_mask_alpha(position) *
            activeMaskSampling.options.y;
    }
    let uv = (position - activeMaskSampling.coordinate0.xy) * activeMaskSampling.coordinate1.xy;
    let sampled = textureSample(
        activeMaskTexture,
        activeMaskSampler,
        clamp(uv, vec2<f32>(0.0), vec2<f32>(1.0))).r;
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
