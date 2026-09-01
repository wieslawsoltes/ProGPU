// Algorithm: Sample a full-size destination and a bounded source region, then evaluate one runtime-selected advanced Porter-Duff blend function.
// Time complexity: O(1) per vertex and fragment.
// Space complexity: O(1) local storage with two texture reads per fragment.
@group(0) @binding(0) var destinationTexture: texture_2d<f32>;
@group(0) @binding(1) var sourceTexture: texture_2d<f32>;

struct SamplingUniforms {
    sourceOrigin: vec2<f32>,
    sourceExtent: vec2<f32>,
    blendMode: u32,
    operationKind: u32,
    rasterOperationCode: u32,
    pad0: u32,
    patternColor: vec4<f32>,
};

@group(0) @binding(2) var<uniform> sampling: SamplingUniforms;

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

fn hard_light_component(backdrop: f32, source: f32) -> f32 {
    if (source <= 0.5) {
        return backdrop * (2.0 * source);
    }
    return backdrop + (2.0 * source - 1.0) -
        backdrop * (2.0 * source - 1.0);
}

fn hard_light(backdrop: vec3<f32>, source: vec3<f32>) -> vec3<f32> {
    return vec3<f32>(
        hard_light_component(backdrop.r, source.r),
        hard_light_component(backdrop.g, source.g),
        hard_light_component(backdrop.b, source.b));
}

fn color_dodge_component(backdrop: f32, source: f32) -> f32 {
    if (backdrop <= 0.0) {
        return 0.0;
    }
    if (source >= 1.0) {
        return 1.0;
    }
    return min(1.0, backdrop / (1.0 - source));
}

fn color_dodge(backdrop: vec3<f32>, source: vec3<f32>) -> vec3<f32> {
    return vec3<f32>(
        color_dodge_component(backdrop.r, source.r),
        color_dodge_component(backdrop.g, source.g),
        color_dodge_component(backdrop.b, source.b));
}

fn color_burn_component(backdrop: f32, source: f32) -> f32 {
    if (backdrop >= 1.0) {
        return 1.0;
    }
    if (source <= 0.0) {
        return 0.0;
    }
    return 1.0 - min(1.0, (1.0 - backdrop) / source);
}

fn color_burn(backdrop: vec3<f32>, source: vec3<f32>) -> vec3<f32> {
    return vec3<f32>(
        color_burn_component(backdrop.r, source.r),
        color_burn_component(backdrop.g, source.g),
        color_burn_component(backdrop.b, source.b));
}

fn soft_light_component(backdrop: f32, source: f32) -> f32 {
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

fn soft_light(backdrop: vec3<f32>, source: vec3<f32>) -> vec3<f32> {
    return vec3<f32>(
        soft_light_component(backdrop.r, source.r),
        soft_light_component(backdrop.g, source.g),
        soft_light_component(backdrop.b, source.b));
}

fn luminosity(color: vec3<f32>) -> f32 {
    return dot(color, vec3<f32>(0.3, 0.59, 0.11));
}

fn saturation(color: vec3<f32>) -> f32 {
    return max(max(color.r, color.g), color.b) -
        min(min(color.r, color.g), color.b);
}

fn clip_color(input: vec3<f32>) -> vec3<f32> {
    var color = input;
    let lightness = luminosity(color);
    let minimum = min(min(color.r, color.g), color.b);
    let maximum = max(max(color.r, color.g), color.b);
    if (minimum < 0.0 && lightness > minimum) {
        color = vec3<f32>(lightness) +
            (color - vec3<f32>(lightness)) * lightness / (lightness - minimum);
    }
    if (maximum > 1.0 && maximum > lightness) {
        color = vec3<f32>(lightness) +
            (color - vec3<f32>(lightness)) * (1.0 - lightness) /
                (maximum - lightness);
    }
    return color;
}

fn set_luminosity(color: vec3<f32>, lightness: f32) -> vec3<f32> {
    return clip_color(color + vec3<f32>(lightness - luminosity(color)));
}

fn set_saturation(color: vec3<f32>, targetSaturation: f32) -> vec3<f32> {
    let minimum = min(min(color.r, color.g), color.b);
    let maximum = max(max(color.r, color.g), color.b);
    if (maximum <= minimum) {
        return vec3<f32>(0.0);
    }
    return (color - vec3<f32>(minimum)) * targetSaturation / (maximum - minimum);
}

fn blend(backdrop: vec3<f32>, source: vec3<f32>) -> vec3<f32> {
    switch sampling.blendMode {
        case 11u: {
            return backdrop * source;
        }
        case 12u: {
            return screen(backdrop, source);
        }
        case 13u: {
            return min(backdrop, source);
        }
        case 14u: {
            return max(backdrop, source);
        }
        case 15u: {
            return backdrop + source - 2.0 * backdrop * source;
        }
        case 18u: {
            return hard_light(source, backdrop);
        }
        case 19u: {
            return color_dodge(backdrop, source);
        }
        case 20u: {
            return color_burn(backdrop, source);
        }
        case 21u: {
            return hard_light(backdrop, source);
        }
        case 22u: {
            return soft_light(backdrop, source);
        }
        case 23u: {
            return abs(backdrop - source);
        }
        case 24u: {
            return set_luminosity(
                set_saturation(source, saturation(backdrop)),
                luminosity(backdrop));
        }
        case 25u: {
            return set_luminosity(
                set_saturation(backdrop, saturation(source)),
                luminosity(backdrop));
        }
        case 26u: {
            return set_luminosity(source, luminosity(backdrop));
        }
        case 27u: {
            return set_luminosity(backdrop, luminosity(source));
        }
        default: {
            return source;
        }
    }
}

fn unpremultiply(color: vec3<f32>, alpha: f32) -> vec3<f32> {
    if (alpha <= 0.0) {
        return vec3<f32>(0.0);
    }

    return color / alpha;
}

fn normalized_to_byte(color: vec3<f32>) -> vec3<u32> {
    return vec3<u32>(floor(clamp(color, vec3<f32>(0.0), vec3<f32>(1.0)) * 255.0 + 0.5));
}

fn evaluate_rop3(
    code: u32,
    pattern: vec3<u32>,
    source: vec3<u32>,
    destination: vec3<u32>) -> vec3<u32> {
    var result = vec3<u32>(0u);
    for (var bit = 0u; bit < 8u; bit = bit + 1u) {
        let patternBit = (pattern >> vec3<u32>(bit)) & vec3<u32>(1u);
        let sourceBit = (source >> vec3<u32>(bit)) & vec3<u32>(1u);
        let destinationBit = (destination >> vec3<u32>(bit)) & vec3<u32>(1u);
        let truthTableIndex = (patternBit << vec3<u32>(2u)) |
            (sourceBit << vec3<u32>(1u)) |
            destinationBit;
        let outputBit = (vec3<u32>(code) >> truthTableIndex) & vec3<u32>(1u);
        result = result | (outputBit << vec3<u32>(bit));
    }
    return result;
}

@fragment
fn fs_main(@builtin(position) position: vec4<f32>) -> @location(0) vec4<f32> {
    let pixel = vec2<i32>(position.xy);
    let destination = clamp(
        textureLoad(destinationTexture, pixel, 0),
        vec4<f32>(0.0),
        vec4<f32>(1.0));
    let sourceCoordinate = position.xy - sampling.sourceOrigin;
    let sourceIsInside = all(sourceCoordinate >= vec2<f32>(0.0)) &&
        all(sourceCoordinate < sampling.sourceExtent);
    var source = vec4<f32>(0.0);
    if (sourceIsInside) {
        source = clamp(
            textureLoad(sourceTexture, vec2<i32>(sourceCoordinate), 0),
            vec4<f32>(0.0),
            vec4<f32>(1.0));
    }


    if (sampling.operationKind == 1u) {
        if (!sourceIsInside || source.a <= 0.0) {
            return destination;
        }
        let sourceAlpha = source.a;
        let destinationAlpha = destination.a;
        let straightSource = unpremultiply(source.rgb, sourceAlpha);
        let straightDestination = unpremultiply(destination.rgb, destinationAlpha);
        let result = evaluate_rop3(
            sampling.rasterOperationCode,
            normalized_to_byte(sampling.patternColor.rgb),
            normalized_to_byte(straightSource),
            normalized_to_byte(straightDestination));
        return vec4<f32>(vec3<f32>(result) / 255.0, 1.0);
    }

    if (sampling.blendMode == 1u) {
        return source;
    }

    let sourceAlpha = source.a;
    let destinationAlpha = destination.a;
    let straightSource = unpremultiply(source.rgb, sourceAlpha);
    let straightDestination = unpremultiply(destination.rgb, destinationAlpha);
    let mixed = clamp(
        blend(straightDestination, straightSource),
        vec3<f32>(0.0),
        vec3<f32>(1.0));
    let result = vec4<f32>(
        source.rgb * (1.0 - destinationAlpha) +
            destination.rgb * (1.0 - sourceAlpha) +
            mixed * sourceAlpha * destinationAlpha,
        sourceAlpha + destinationAlpha - sourceAlpha * destinationAlpha);
    return clamp(result, vec4<f32>(0.0), vec4<f32>(1.0));
}
