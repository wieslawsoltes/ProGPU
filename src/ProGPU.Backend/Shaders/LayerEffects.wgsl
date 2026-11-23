// Algorithm: Evaluate one layer effect per target pixel from premultiplied input textures, read
// texel for texel: a colour matrix, a matrix convolution, a composite of two sources, scene
// lighting, a multiply by a factor image and material noise.
// Time complexity: O(P) for P pixels, times the kernel elements of a convolution (at most 25) and
// the lights of a lighting pass (at most 8).
// Space complexity: O(1) per invocation; the parameters are one bounded uniform block.

// The parameters of the pass, laid out by progpu_native_layer_effects.cpp. In every pass
// `block[0].xy` is the size in pixels of the area at the top left of the target.
const BLOCK_VECTORS: u32 = 88u;
@group(0) @binding(0) var<uniform> block: array<vec4<f32>, BLOCK_VECTORS>;
@group(0) @binding(1) var firstInput: texture_2d<f32>;
@group(0) @binding(2) var secondInput: texture_2d<f32>;

@vertex
fn vs_layer_effect(@builtin(vertex_index) index: u32) -> @builtin(position) vec4<f32> {
    var corners = array<vec2<f32>, 3>(vec2<f32>(-1.0, -1.0), vec2<f32>(3.0, -1.0), vec2<f32>(-1.0, 3.0));
    return vec4<f32>(corners[index], 0.0, 1.0);
}

fn area_size() -> vec2<i32> {
    return vec2<i32>(block[0].xy);
}

fn in_area(pixel: vec2<i32>) -> bool {
    return all(pixel >= vec2<i32>(0)) && all(pixel < area_size());
}

fn pixel_of(position: vec4<f32>) -> vec2<i32> {
    return vec2<i32>(floor(position.xy));
}

// A premultiplied colour as straight channels, none above one.
fn unpremultiplied(color: vec4<f32>) -> vec3<f32> {
    if (color.a <= 0.0) {
        return vec3<f32>(0.0);
    }
    return min(color.rgb / color.a, vec3<f32>(1.0));
}

// Two rows of an affine transform applied to a point.
fn through_rows(x: vec4<f32>, y: vec4<f32>, point: vec2<f32>) -> vec2<f32> {
    let row = vec3<f32>(point, 1.0);
    return vec2<f32>(dot(x.xyz, row), dot(y.xyz, row));
}

// --- colour matrix: block[0].z is 1 for a matrix over premultiplied colours, block[1..6] the rows.

fn through_matrix(color: vec4<f32>) -> vec4<f32> {
    let sum = color.r * block[1] + color.g * block[2] + color.b * block[3] + color.a * block[4] + block[5];
    return clamp(sum, vec4<f32>(0.0), vec4<f32>(1.0));
}

@fragment
fn fs_color_matrix(@builtin(position) position: vec4<f32>) -> @location(0) vec4<f32> {
    let pixel = pixel_of(position);
    if (!in_area(pixel)) {
        return vec4<f32>(0.0);
    }
    let texel = textureLoad(firstInput, pixel, 0);
    if (block[0].z > 0.5) {
        let out = through_matrix(texel);
        return vec4<f32>(min(out.rgb, vec3<f32>(out.a)), out.a);
    }
    let out = through_matrix(vec4<f32>(unpremultiplied(texel), texel.a));
    return vec4<f32>(out.rgb * out.a, out.a);
}

// --- convolution: block[0].zw columns and rows; block[1] gain, bias, keep alpha, repeat edges;
// block[2..9] the elements row by row, four to a vector.

fn kernel_element(index: i32) -> f32 {
    return block[2 + index / 4][index % 4];
}

fn kernel_source(pixel: vec2<i32>) -> vec4<f32> {
    if (block[1].w > 0.5) {
        return textureLoad(firstInput, clamp(pixel, vec2<i32>(0), area_size() - vec2<i32>(1)), 0);
    }
    if (!in_area(pixel)) {
        return vec4<f32>(0.0);
    }
    return textureLoad(firstInput, pixel, 0);
}

@fragment
fn fs_convolution(@builtin(position) position: vec4<f32>) -> @location(0) vec4<f32> {
    let pixel = pixel_of(position);
    if (!in_area(pixel)) {
        return vec4<f32>(0.0);
    }
    let columns = i32(block[0].z);
    let rows = i32(block[0].w);
    let keep_alpha = block[1].z > 0.5;
    let middle = vec2<i32>(columns / 2, rows / 2);
    var total = vec4<f32>(0.0);
    for (var row = 0; row < rows; row++) {
        for (var column = 0; column < columns; column++) {
            // The kernel is applied turned by half a turn.
            let weight = kernel_element(columns * rows - 1 - (row * columns + column));
            var texel = kernel_source(pixel + vec2<i32>(column, row) - middle);
            if (keep_alpha) {
                var straight = vec3<f32>(0.0);
                if (texel.a > 0.0) {
                    straight = texel.rgb / texel.a;
                }
                texel = vec4<f32>(straight, texel.a);
            }
            total += texel * weight;
        }
    }
    var out = total * block[1].x + vec4<f32>(block[1].y);
    if (keep_alpha) {
        let alpha = textureLoad(firstInput, pixel, 0).a;
        out = vec4<f32>(out.rgb * alpha, alpha);
    } else {
        out.a = clamp(out.a, 0.0, 1.0);
    }
    return vec4<f32>(clamp(out.rgb, vec3<f32>(0.0), vec3<f32>(out.a)), out.a);
}

// --- composite of two sources: block[0].z operation (0 blend, 1 arithmetic, 2 mask invert),
// block[0].w the blend mode; block[1] the arithmetic amounts; block[2] the Dissolve grid (x, y,
// width, height) and block[3].xy its cells; block[4], block[5] pixel to drawing.

fn perceived(color: vec3<f32>) -> f32 {
    return dot(color, vec3<f32>(0.3, 0.59, 0.11));
}

fn burned(backdrop: f32, source: f32) -> f32 {
    if (backdrop >= 1.0) {
        return 1.0;
    }
    if (source <= 0.0) {
        return 0.0;
    }
    return 1.0 - min((1.0 - backdrop) / source, 1.0);
}

fn dodged(backdrop: f32, source: f32) -> f32 {
    if (backdrop <= 0.0) {
        return 0.0;
    }
    if (source >= 1.0) {
        return 1.0;
    }
    return min(backdrop / (1.0 - source), 1.0);
}

// One channel of the separable modes the layers of the engine do not have (the values of
// D2D1_BLEND_MODE).
fn blended_channel(mode: u32, backdrop: f32, source: f32) -> f32 {
    switch mode {
        case 6u { return max(backdrop + source - 1.0, 0.0); }
        case 14u {
            if (source <= 0.5) {
                return burned(backdrop, 2.0 * source);
            }
            return dodged(backdrop, 2.0 * source - 1.0);
        }
        case 15u { return clamp(backdrop + 2.0 * source - 1.0, 0.0, 1.0); }
        case 16u {
            if (source <= 0.5) {
                return min(backdrop, 2.0 * source);
            }
            return max(backdrop, 2.0 * source - 1.0);
        }
        case 17u { return select(0.0, 1.0, backdrop + source >= 1.0); }
        case 24u { return max(backdrop - source, 0.0); }
        case 25u {
            if (source <= 0.0) {
                return select(1.0, 0.0, backdrop <= 0.0);
            }
            return min(backdrop / source, 1.0);
        }
        default { return source; }
    }
}

fn blended(mode: u32, backdrop: vec3<f32>, source: vec3<f32>) -> vec3<f32> {
    if (mode == 7u) {
        return select(backdrop, source, perceived(source) < perceived(backdrop));
    }
    if (mode == 8u) {
        return select(backdrop, source, perceived(source) > perceived(backdrop));
    }
    return vec3<f32>(
        blended_channel(mode, backdrop.r, source.r),
        blended_channel(mode, backdrop.g, source.g),
        blended_channel(mode, backdrop.b, source.b));
}

// The same random number, 0 to 1, for a cell in every frame.
fn cell_noise(index: u32) -> f32 {
    var value = index ^ 0x9E3779B9u;
    value = value * 0x85EBCA6Bu;
    value = value ^ (value >> 13u);
    value = value * 0xC2B2AE35u;
    value = value ^ (value >> 16u);
    return f32(value & 0xFFFFu) / 65535.0;
}

fn dissolve_cell(position: vec2<f32>) -> u32 {
    let grid = block[2];
    let cells = block[3].xy;
    let local = through_rows(block[4], block[5], position);
    let at = clamp(floor((local - grid.xy) / grid.zw * cells), vec2<f32>(0.0), cells - vec2<f32>(1.0));
    return u32(at.y) * u32(cells.x) + u32(at.x);
}

@fragment
fn fs_composite(@builtin(position) position: vec4<f32>) -> @location(0) vec4<f32> {
    let pixel = pixel_of(position);
    if (!in_area(pixel)) {
        return vec4<f32>(0.0);
    }
    let first = textureLoad(firstInput, pixel, 0);
    let second = textureLoad(secondInput, pixel, 0);
    let operation = u32(block[0].z);
    if (operation == 1u) {
        let k = block[1];
        let sum = k.x * first * second + k.y * first + k.z * second + vec4<f32>(k.w);
        let out = clamp(sum, vec4<f32>(0.0), vec4<f32>(1.0));
        return vec4<f32>(min(out.rgb, vec3<f32>(out.a)), out.a);
    }
    if (operation == 2u) {
        let inverted = second.a * (vec3<f32>(first.a) - first.rgb) + (1.0 - second.a) * first.rgb;
        return vec4<f32>(clamp(inverted, vec3<f32>(0.0), vec3<f32>(first.a)), first.a);
    }
    let mode = u32(block[0].w);
    let backdrop = unpremultiplied(first);
    let source = unpremultiplied(second);
    if (mode == 4u) {
        if (second.a > 0.0 && cell_noise(dissolve_cell(position.xy)) < second.a) {
            return vec4<f32>(source, 1.0);
        }
        return first;
    }
    let alpha = second.a + first.a - second.a * first.a;
    if (alpha <= 0.0) {
        return vec4<f32>(0.0);
    }
    let color = (1.0 - second.a) * first.a * backdrop + (1.0 - first.a) * second.a * source +
        second.a * first.a * blended(mode, backdrop, source);
    return vec4<f32>(clamp(color, vec3<f32>(0.0), vec3<f32>(alpha)), alpha);
}

// --- lighting: block[0].z 1 to multiply the input by the light, block[0].w the lights; block[1]
// the constant light and the largest factor; block[2] the ambient, diffuse, specular amounts and
// the shine; block[3], block[4] pixel to drawing; block[5].xy the pixel of the top left of the
// target. From block[6], ten vectors a light:
//   0 kind (1 point, 2 spot), cosines of the inner and outer cones
//   1 colour, the inner colour of a spot   2 outer colour   3 position   4 direction
//   5 attenuation: constant, linear, quadratic, minimum cutoff
//   6 x maximum cutoff, y 1 when there is one
//   7, 8, 9 the rows of the transform of a point of the drawing into the space of the light

const LIGHTS_START: u32 = 6u;
const LIGHT_VECTORS: u32 = 10u;

fn light_field(light: u32, field: u32) -> vec4<f32> {
    return block[LIGHTS_START + light * LIGHT_VECTORS + field];
}

fn falloff(light: u32, distance: f32) -> f32 {
    let limit = light_field(light, 6u);
    if (limit.y > 0.5 && distance > limit.x) {
        return 0.0;
    }
    let terms = light_field(light, 5u);
    let d = max(max(distance, terms.w), 0.0);
    let divisor = terms.x + terms.y * d + terms.z * d * d;
    return select(1.0, 1.0 / divisor, divisor > 0.0);
}

// Where a point is between the cones of a spot: 1 inside the inner, 0 outside the outer.
fn cone_factor(light: u32, point: vec2<f32>) -> f32 {
    let origin = light_field(light, 3u).xyz;
    let aim = light_field(light, 4u).xyz;
    let to_point = vec3<f32>(point - origin.xy, -origin.z);
    let lengths = length(to_point) * length(aim);
    if (lengths <= 0.0) {
        return 0.0;
    }
    let cosine = dot(to_point, aim) / lengths;
    let kind = light_field(light, 0u);
    if (cosine >= kind.y) {
        return 1.0;
    }
    if (cosine <= kind.z || kind.y <= kind.z) {
        return 0.0;
    }
    let t = clamp((cosine - kind.z) / (kind.y - kind.z), 0.0, 1.0);
    return t * t * (3.0 - 2.0 * t);
}

// (N.H)^shine of a flat surface facing +Z, seen from +Z.
fn highlight(to_light: vec3<f32>, shine: f32) -> f32 {
    let distance = length(to_light);
    if (distance <= 0.0 || to_light.z <= 0.0) {
        return 0.0;
    }
    let half_vector = to_light / distance + vec3<f32>(0.0, 0.0, 1.0);
    let half_length = length(half_vector);
    if (half_length <= 0.0) {
        return 0.0;
    }
    return pow(max(half_vector.z / half_length, 0.0), max(shine, 1.0));
}

fn reflected_light(light: u32, drawing: vec2<f32>) -> vec3<f32> {
    let row = vec3<f32>(drawing, 1.0);
    let w = dot(light_field(light, 9u).xyz, row);
    let point = vec2<f32>(dot(light_field(light, 7u).xyz, row), dot(light_field(light, 8u).xyz, row)) / w;
    let origin = light_field(light, 3u).xyz;
    let to_light = vec3<f32>(origin.xy - point, origin.z);
    let distance = length(to_light);
    var color = light_field(light, 1u).rgb;
    var strength = 1.0;
    if (light_field(light, 0u).x > 1.5) {
        strength = cone_factor(light, point);
        if (strength <= 0.0) {
            return vec3<f32>(0.0);
        }
        color = mix(light_field(light, 2u).rgb, color, strength);
    }
    let reach = strength * falloff(light, distance);
    var incidence = 0.0;
    if (distance > 0.0 && origin.z > 0.0) {
        incidence = origin.z / distance;
    }
    let surface = block[2];
    let diffuse = color * reach * incidence * surface.y;
    if (surface.z == 0.0) {
        return diffuse;
    }
    return diffuse + color * reach * highlight(to_light, surface.w) * surface.z;
}

fn light_at(position: vec2<f32>) -> vec3<f32> {
    let drawing = through_rows(block[3], block[4], position + block[5].xy);
    var light = block[1].rgb;
    let count = u32(block[0].w);
    for (var index = 0u; index < count; index++) {
        light += reflected_light(index, drawing);
    }
    return light;
}

@fragment
fn fs_lighting(@builtin(position) position: vec4<f32>) -> @location(0) vec4<f32> {
    let pixel = pixel_of(position);
    if (!in_area(pixel)) {
        return vec4<f32>(0.0);
    }
    let light = light_at(position.xy);
    if (block[0].z < 0.5) {
        return vec4<f32>(clamp(light, vec3<f32>(0.0), vec3<f32>(1.0)), 1.0);
    }
    let content = textureLoad(firstInput, pixel, 0);
    let factors = clamp(light, vec3<f32>(0.0), vec3<f32>(block[1].w));
    return vec4<f32>(min(content.rgb * factors, vec3<f32>(content.a)), content.a);
}

// --- multiply by a factor image (the second input): block[0].z what a sample of one stands for;
// block[1], block[2] pixel to the unit square of the factors. Outside the square nothing is kept.

fn factors_at(unit: vec2<f32>) -> vec3<f32> {
    let size = vec2<i32>(textureDimensions(secondInput));
    let last = size - vec2<i32>(1);
    let at = clamp(unit * vec2<f32>(size) - vec2<f32>(0.5), vec2<f32>(0.0), vec2<f32>(last));
    let low = vec2<i32>(floor(at));
    let high = min(low + vec2<i32>(1), last);
    let part = at - vec2<f32>(low);
    let upper = mix(
        textureLoad(secondInput, low, 0).rgb,
        textureLoad(secondInput, vec2<i32>(high.x, low.y), 0).rgb,
        part.x);
    let lower = mix(
        textureLoad(secondInput, vec2<i32>(low.x, high.y), 0).rgb,
        textureLoad(secondInput, high, 0).rgb,
        part.x);
    return mix(upper, lower, part.y) * block[0].z;
}

@fragment
fn fs_factor_multiply(@builtin(position) position: vec4<f32>) -> @location(0) vec4<f32> {
    let pixel = pixel_of(position);
    if (!in_area(pixel)) {
        return vec4<f32>(0.0);
    }
    let unit = through_rows(block[1], block[2], position.xy);
    if (any(unit < vec2<f32>(0.0)) || any(unit > vec2<f32>(1.0))) {
        return vec4<f32>(0.0);
    }
    let content = textureLoad(firstInput, pixel, 0);
    return vec4<f32>(min(content.rgb * factors_at(unit), vec3<f32>(content.a)), content.a);
}

// --- material noise, the noise of BackdropMaterial.wgsl as a layer of its own: block[0].z the
// opacity; block[1] the colour; block[2].xy the pixel of the top left of the target.

fn material_noise(position: vec2<f32>) -> f32 {
    let value = dot(floor(position), vec2<f32>(12.9898, 78.233));
    return fract(sin(value) * 43758.5453);
}

@fragment
fn fs_material_noise(@builtin(position) position: vec4<f32>) -> @location(0) vec4<f32> {
    if (!in_area(pixel_of(position))) {
        return vec4<f32>(0.0);
    }
    let opacity = block[0].z;
    return vec4<f32>(block[1].rgb * material_noise(position.xy + block[2].xy) * opacity, opacity);
}
