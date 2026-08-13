// Algorithm: Composite one full-size premultiplied group texture over a uniform premultiplied backdrop using a runtime-selected W3C blend function.
// Time complexity: O(1) per vertex and fragment across one full-target triangle.
// Space complexity: O(1) private storage, one texture read and one render-target write per fragment; no auxiliary storage.
// The source texture is already resolved through group opacity, masks, clips,
// and effects. Color blend functions operate on straight RGB, then the result
// is converted back to premultiplied source-over form. The switch is bounded
// to the fifteen blend modes that cannot be represented exactly by WebGPU's
// fixed-function blend factors for translucent inputs.
@group(0) @binding(0) var sourceTexture: texture_2d<f32>;

struct GroupBlendUniforms {
    backdrop: vec4<f32>,
    blendMode: u32,
    padding0: u32,
    padding1: u32,
    padding2: u32,
};

@group(0) @binding(1) var<uniform> uniforms: GroupBlendUniforms;

@vertex
fn vs_main(@builtin(vertex_index) vertexIndex: u32) -> @builtin(position) vec4<f32> {
    var positions = array<vec2<f32>, 3>(
        vec2<f32>(-1.0, -1.0),
        vec2<f32>(3.0, -1.0),
        vec2<f32>(-1.0, 3.0));
    return vec4<f32>(positions[vertexIndex], 0.0, 1.0);
}

fn screen(backdrop: vec3<f32>, source: vec3<f32>) -> vec3<f32> {
    return backdrop + source - backdrop * source;
}

fn hardLightComponent(backdrop: f32, source: f32) -> f32 {
    if (source <= 0.5) {
        return backdrop * (2.0 * source);
    }
    let scaledSource = 2.0 * source - 1.0;
    return backdrop + scaledSource - backdrop * scaledSource;
}

fn hardLight(backdrop: vec3<f32>, source: vec3<f32>) -> vec3<f32> {
    return vec3<f32>(
        hardLightComponent(backdrop.r, source.r),
        hardLightComponent(backdrop.g, source.g),
        hardLightComponent(backdrop.b, source.b));
}

fn colorDodgeComponent(backdrop: f32, source: f32) -> f32 {
    if (backdrop <= 0.0) {
        return 0.0;
    }
    if (source >= 1.0) {
        return 1.0;
    }
    return min(1.0, backdrop / (1.0 - source));
}

fn colorDodge(backdrop: vec3<f32>, source: vec3<f32>) -> vec3<f32> {
    return vec3<f32>(
        colorDodgeComponent(backdrop.r, source.r),
        colorDodgeComponent(backdrop.g, source.g),
        colorDodgeComponent(backdrop.b, source.b));
}

fn colorBurnComponent(backdrop: f32, source: f32) -> f32 {
    if (backdrop >= 1.0) {
        return 1.0;
    }
    if (source <= 0.0) {
        return 0.0;
    }
    return 1.0 - min(1.0, (1.0 - backdrop) / source);
}

fn colorBurn(backdrop: vec3<f32>, source: vec3<f32>) -> vec3<f32> {
    return vec3<f32>(
        colorBurnComponent(backdrop.r, source.r),
        colorBurnComponent(backdrop.g, source.g),
        colorBurnComponent(backdrop.b, source.b));
}

fn softLightComponent(backdrop: f32, source: f32) -> f32 {
    if (source <= 0.5) {
        return backdrop -
            (1.0 - 2.0 * source) * backdrop * (1.0 - backdrop);
    }
    var curve = sqrt(backdrop);
    if (backdrop <= 0.25) {
        curve = ((16.0 * backdrop - 12.0) * backdrop + 4.0) * backdrop;
    }
    return backdrop + (2.0 * source - 1.0) * (curve - backdrop);
}

fn softLight(backdrop: vec3<f32>, source: vec3<f32>) -> vec3<f32> {
    return vec3<f32>(
        softLightComponent(backdrop.r, source.r),
        softLightComponent(backdrop.g, source.g),
        softLightComponent(backdrop.b, source.b));
}

fn luminosity(color: vec3<f32>) -> f32 {
    return dot(color, vec3<f32>(0.3, 0.59, 0.11));
}

fn saturation(color: vec3<f32>) -> f32 {
    return max(max(color.r, color.g), color.b) -
        min(min(color.r, color.g), color.b);
}

fn clipColor(input: vec3<f32>) -> vec3<f32> {
    var color = input;
    let lightness = luminosity(color);
    let minimum = min(min(color.r, color.g), color.b);
    let maximum = max(max(color.r, color.g), color.b);
    if (minimum < 0.0 && lightness > minimum) {
        color = vec3<f32>(lightness) +
            (color - vec3<f32>(lightness)) * lightness /
                (lightness - minimum);
    }
    if (maximum > 1.0 && maximum > lightness) {
        color = vec3<f32>(lightness) +
            (color - vec3<f32>(lightness)) * (1.0 - lightness) /
                (maximum - lightness);
    }
    return color;
}

fn setLuminosity(color: vec3<f32>, lightness: f32) -> vec3<f32> {
    return clipColor(color + vec3<f32>(lightness - luminosity(color)));
}

fn setSaturation(color: vec3<f32>, targetSaturation: f32) -> vec3<f32> {
    let minimum = min(min(color.r, color.g), color.b);
    let maximum = max(max(color.r, color.g), color.b);
    if (maximum <= minimum) {
        return vec3<f32>(0.0);
    }
    return (color - vec3<f32>(minimum)) * targetSaturation /
        (maximum - minimum);
}

fn evaluateBlend(backdrop: vec3<f32>, source: vec3<f32>) -> vec3<f32> {
    switch uniforms.blendMode {
        case 11u: { return backdrop * source; }
        case 12u: { return screen(backdrop, source); }
        case 13u: { return min(backdrop, source); }
        case 14u: { return max(backdrop, source); }
        case 15u: { return backdrop + source - 2.0 * backdrop * source; }
        case 18u: { return hardLight(source, backdrop); }
        case 19u: { return colorDodge(backdrop, source); }
        case 20u: { return colorBurn(backdrop, source); }
        case 21u: { return hardLight(backdrop, source); }
        case 22u: { return softLight(backdrop, source); }
        case 23u: { return abs(backdrop - source); }
        case 24u: {
            return setLuminosity(
                setSaturation(source, saturation(backdrop)),
                luminosity(backdrop));
        }
        case 25u: {
            return setLuminosity(
                setSaturation(backdrop, saturation(source)),
                luminosity(backdrop));
        }
        case 26u: { return setLuminosity(source, luminosity(backdrop)); }
        case 27u: { return setLuminosity(backdrop, luminosity(source)); }
        default: { return source; }
    }
}

fn unpremultiply(color: vec3<f32>, alpha: f32) -> vec3<f32> {
    return select(vec3<f32>(0.0), color / alpha, alpha > 0.0);
}

@fragment
fn fs_main(@builtin(position) position: vec4<f32>) -> @location(0) vec4<f32> {
    let source = clamp(
        textureLoad(sourceTexture, vec2<i32>(position.xy), 0),
        vec4<f32>(0.0),
        vec4<f32>(1.0));
    let backdrop = clamp(uniforms.backdrop, vec4<f32>(0.0), vec4<f32>(1.0));
    let sourceAlpha = source.a;
    let backdropAlpha = backdrop.a;
    let mixed = clamp(
        evaluateBlend(
            unpremultiply(backdrop.rgb, backdropAlpha),
            unpremultiply(source.rgb, sourceAlpha)),
        vec3<f32>(0.0),
        vec3<f32>(1.0));
    return clamp(
        vec4<f32>(
            source.rgb * (1.0 - backdropAlpha) +
                backdrop.rgb * (1.0 - sourceAlpha) +
                mixed * sourceAlpha * backdropAlpha,
            sourceAlpha + backdropAlpha - sourceAlpha * backdropAlpha),
        vec4<f32>(0.0),
        vec4<f32>(1.0));
}
