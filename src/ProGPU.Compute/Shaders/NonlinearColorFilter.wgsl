// Algorithm: Apply either an RGBA-to-HSLA matrix round trip or Skia's linear-light high-contrast transform to unpremultiplied texels.
// Time complexity: O(1) per output texel with one texture read, fixed color-space arithmetic, and one texture write.
// Space complexity: O(1) local storage; the 4x5 matrix and high-contrast controls occupy a fixed 96-byte uniform block.
struct NonlinearColorFilterParams {
    matrixRed: vec4<f32>,
    matrixGreen: vec4<f32>,
    matrixBlue: vec4<f32>,
    matrixAlpha: vec4<f32>,
    matrixOffset: vec4<f32>,
    // x: 0 for HSLA matrix, 1 for high contrast; y: grayscale; z: invert style; w: contrast scale.
    configuration: vec4<f32>,
};

@group(0) @binding(0) var inputTex: texture_2d<f32>;
@group(0) @binding(1) var outputTex: texture_storage_2d<rgba8unorm, write>;
@group(0) @binding(2) var<uniform> params: NonlinearColorFilterParams;

fn rgb_to_hsl(color: vec3<f32>) -> vec3<f32> {
    let maximum = max(color.r, max(color.g, color.b));
    let minimum = min(color.r, min(color.g, color.b));
    let delta = maximum - minimum;
    let lightness = (maximum + minimum) * 0.5;
    if (delta <= 0.000001) {
        return vec3<f32>(0.0, 0.0, lightness);
    }

    let saturation = delta / max(0.000001, 1.0 - abs(2.0 * lightness - 1.0));
    var hue: f32;
    // Long-form comparisons avoid GPU max() equality drift on tied channels.
    if (color.r >= color.g && color.r >= color.b) {
        hue = (color.g - color.b) / delta + select(0.0, 6.0, color.g < color.b);
    } else if (color.g >= color.b) {
        hue = (color.b - color.r) / delta + 2.0;
    } else {
        hue = (color.r - color.g) / delta + 4.0;
    }
    return vec3<f32>(hue / 6.0, saturation, lightness);
}

fn hue_to_rgb(p: f32, q: f32, sourceHue: f32) -> f32 {
    let hue = fract(sourceHue);
    if (hue < 1.0 / 6.0) {
        return p + (q - p) * 6.0 * hue;
    }
    if (hue < 0.5) {
        return q;
    }
    if (hue < 2.0 / 3.0) {
        return p + (q - p) * (2.0 / 3.0 - hue) * 6.0;
    }
    return p;
}

fn hsl_to_rgb(hsl: vec3<f32>) -> vec3<f32> {
    if (abs(hsl.y) <= 0.000001) {
        return vec3<f32>(hsl.z);
    }

    let q = select(
        hsl.z + hsl.y - hsl.z * hsl.y,
        hsl.z * (1.0 + hsl.y),
        hsl.z < 0.5);
    let p = 2.0 * hsl.z - q;
    return vec3<f32>(
        hue_to_rgb(p, q, hsl.x + 1.0 / 3.0),
        hue_to_rgb(p, q, hsl.x),
        hue_to_rgb(p, q, hsl.x - 1.0 / 3.0));
}

fn srgb_to_linear_component(value: f32) -> f32 {
    return select(
        pow((value + 0.055) / 1.055, 2.4),
        value / 12.92,
        value <= 0.04045);
}

fn linear_to_srgb_component(value: f32) -> f32 {
    return select(
        1.055 * pow(value, 1.0 / 2.4) - 0.055,
        value * 12.92,
        value <= 0.0031308);
}

fn apply_hsla_matrix(straight: vec4<f32>) -> vec4<f32> {
    let hsla = vec4<f32>(rgb_to_hsl(straight.rgb), straight.a);
    let transformed = vec4<f32>(
        dot(hsla, params.matrixRed) + params.matrixOffset.r,
        dot(hsla, params.matrixGreen) + params.matrixOffset.g,
        dot(hsla, params.matrixBlue) + params.matrixOffset.b,
        dot(hsla, params.matrixAlpha) + params.matrixOffset.a);
    return clamp(
        vec4<f32>(hsl_to_rgb(transformed.rgb), transformed.a),
        vec4<f32>(0.0),
        vec4<f32>(1.0));
}

fn apply_high_contrast(straight: vec4<f32>) -> vec4<f32> {
    var rgb = vec3<f32>(
        srgb_to_linear_component(straight.r),
        srgb_to_linear_component(straight.g),
        srgb_to_linear_component(straight.b));
    if (params.configuration.y > 0.5) {
        rgb = vec3<f32>(dot(rgb, vec3<f32>(0.2126, 0.7152, 0.0722)));
    }

    if (params.configuration.z > 0.5 && params.configuration.z < 1.5) {
        rgb = vec3<f32>(1.0) - rgb;
    } else if (params.configuration.z >= 1.5) {
        var hsl = rgb_to_hsl(rgb);
        hsl.z = 1.0 - hsl.z;
        rgb = hsl_to_rgb(hsl);
    }

    rgb = clamp(
        vec3<f32>(0.5) + (rgb - vec3<f32>(0.5)) * params.configuration.w,
        vec3<f32>(0.0),
        vec3<f32>(1.0));
    rgb = vec3<f32>(
        linear_to_srgb_component(rgb.r),
        linear_to_srgb_component(rgb.g),
        linear_to_srgb_component(rgb.b));
    return vec4<f32>(rgb, straight.a);
}

@compute @workgroup_size(16, 16)
fn main(@builtin(global_invocation_id) id: vec3<u32>) {
    let size = textureDimensions(inputTex);
    if (id.x >= size.x || id.y >= size.y) {
        return;
    }

    let pixelPosition = vec2<i32>(id.xy);
    let input = clamp(textureLoad(inputTex, pixelPosition, 0), vec4<f32>(0.0), vec4<f32>(1.0));
    var straight = vec4<f32>(0.0);
    if (input.a > 0.0) {
        straight = vec4<f32>(input.rgb / input.a, input.a);
    }

    var filtered: vec4<f32>;
    if (params.configuration.x > 0.5) {
        filtered = apply_high_contrast(straight);
    } else {
        filtered = apply_hsla_matrix(straight);
    }
    textureStore(
        outputTex,
        pixelPosition,
        vec4<f32>(filtered.rgb * filtered.a, filtered.a));
}
