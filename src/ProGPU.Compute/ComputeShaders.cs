namespace ProGPU.Compute;

public static class ComputeShaders
{
    public const string GaussianBlurHorizontal = @"
@group(0) @binding(0) var inputTex: texture_2d<f32>;
@group(0) @binding(1) var outputTex: texture_storage_2d<rgba8unorm, write>;

@compute @workgroup_size(16, 16)
fn main(@builtin(global_invocation_id) id: vec3<u32>) {
    let size = textureDimensions(inputTex);
    let x = i32(id.x);
    let y = i32(id.y);

    if (x >= i32(size.x) || y >= i32(size.y)) {
        return;
    }

    // 5-tap Gaussian Blur unrolled (weights: 0.0625, 0.25, 0.375, 0.25, 0.0625)
    var color = vec4<f32>(0.0);
    
    color += textureLoad(inputTex, vec2<i32>(clamp(x - 2, 0, i32(size.x) - 1), y), 0) * 0.0625;
    color += textureLoad(inputTex, vec2<i32>(clamp(x - 1, 0, i32(size.x) - 1), y), 0) * 0.25;
    color += textureLoad(inputTex, vec2<i32>(clamp(x + 0, 0, i32(size.x) - 1), y), 0) * 0.375;
    color += textureLoad(inputTex, vec2<i32>(clamp(x + 1, 0, i32(size.x) - 1), y), 0) * 0.25;
    color += textureLoad(inputTex, vec2<i32>(clamp(x + 2, 0, i32(size.x) - 1), y), 0) * 0.0625;

    textureStore(outputTex, vec2<i32>(x, y), color);
}
";

    public const string GaussianBlurVertical = @"
@group(0) @binding(0) var inputTex: texture_2d<f32>;
@group(0) @binding(1) var outputTex: texture_storage_2d<rgba8unorm, write>;

@compute @workgroup_size(16, 16)
fn main(@builtin(global_invocation_id) id: vec3<u32>) {
    let size = textureDimensions(inputTex);
    let x = i32(id.x);
    let y = i32(id.y);

    if (x >= i32(size.x) || y >= i32(size.y)) {
        return;
    }

    // 5-tap Gaussian Blur unrolled (weights: 0.0625, 0.25, 0.375, 0.25, 0.0625)
    var color = vec4<f32>(0.0);
    
    color += textureLoad(inputTex, vec2<i32>(x, clamp(y - 2, 0, i32(size.y) - 1)), 0) * 0.0625;
    color += textureLoad(inputTex, vec2<i32>(x, clamp(y - 1, 0, i32(size.y) - 1)), 0) * 0.25;
    color += textureLoad(inputTex, vec2<i32>(x, clamp(y + 0, 0, i32(size.y) - 1)), 0) * 0.375;
    color += textureLoad(inputTex, vec2<i32>(x, clamp(y + 1, 0, i32(size.y) - 1)), 0) * 0.25;
    color += textureLoad(inputTex, vec2<i32>(x, clamp(y + 2, 0, i32(size.y) - 1)), 0) * 0.0625;

    textureStore(outputTex, vec2<i32>(x, y), color);
}
";

    public const string DropShadow = @"
struct Params {
    offset: vec2<f32>,
    color: vec4<f32>,
    blurRadius: f32,
    padding: f32,
    pad0: f32,
    pad1: f32,
    pad2: f32,
    pad3: f32,
    pad4: f32,
    pad5: f32,
};

@group(0) @binding(0) var inputTex: texture_2d<f32>;
@group(0) @binding(1) var outputTex: texture_storage_2d<rgba8unorm, write>;
@group(0) @binding(2) var<uniform> params: Params;

@compute @workgroup_size(16, 16)
fn main(@builtin(global_invocation_id) id: vec3<u32>) {
    let size = textureDimensions(inputTex);
    let x = i32(id.x);
    let y = i32(id.y);

    if (x >= i32(size.x) || y >= i32(size.y)) {
        return;
    }

    var alphaSum: f32 = 0.0;
    let r = i32(params.blurRadius);
    var count: f32 = 0.0;

    for (var dy = -r; dy <= r; dy++) {
        for (var dx = -r; dx <= r; dx++) {
            let srcX = clamp(x - dx, 0, i32(size.x) - 1);
            let srcY = clamp(y - dy, 0, i32(size.y) - 1);
            
            let pixel = textureLoad(inputTex, vec2<i32>(srcX, srcY), 0);
            alphaSum += pixel.a;
            count += 1.0;
        }
    }

    let avgAlpha = alphaSum / count;
    let shadowAlpha = params.color.a * avgAlpha;
    let shadowColor = vec4<f32>(params.color.rgb * shadowAlpha, shadowAlpha);

    textureStore(outputTex, vec2<i32>(x, y), shadowColor);
}
";

    public const string ShadowBlurHorizontal = @"
struct Params {
    offset: vec2<f32>,
    color: vec4<f32>,
    blurRadius: f32,
    padding: f32,
    pad0: f32,
    pad1: f32,
    pad2: f32,
    pad3: f32,
    pad4: f32,
    pad5: f32,
};

@group(0) @binding(0) var inputTex: texture_2d<f32>;
@group(0) @binding(1) var outputTex: texture_storage_2d<rgba8unorm, write>;
@group(0) @binding(2) var<uniform> params: Params;

@compute @workgroup_size(16, 16)
fn main(@builtin(global_invocation_id) id: vec3<u32>) {
    let size = textureDimensions(inputTex);
    let x = i32(id.x);
    let y = i32(id.y);

    if (x >= i32(size.x) || y >= i32(size.y)) {
        return;
    }

    let sigma = max(params.blurRadius, 0.5);
    let radius = min(i32(ceil(sigma * 3.0)), 64);
    var alphaSum: f32 = 0.0;
    var weightSum: f32 = 0.0;
    for (var dx = -radius; dx <= radius; dx = dx + 1) {
        let sampleX = clamp(x + dx, 0, i32(size.x) - 1);
        let distance = f32(dx);
        let weight = exp(-0.5 * distance * distance / (sigma * sigma));
        alphaSum += textureLoad(inputTex, vec2<i32>(sampleX, y), 0).a * weight;
        weightSum += weight;
    }

    let shadowAlpha = params.color.a * alphaSum / max(weightSum, 0.0001);
    let shadowColor = vec4<f32>(params.color.rgb * shadowAlpha, shadowAlpha);
    textureStore(outputTex, vec2<i32>(x, y), shadowColor);
}
";

    public const string ShadowBlurVertical = @"
struct Params {
    offset: vec2<f32>,
    color: vec4<f32>,
    blurRadius: f32,
    padding: f32,
    pad0: f32,
    pad1: f32,
    pad2: f32,
    pad3: f32,
    pad4: f32,
    pad5: f32,
};

@group(0) @binding(0) var inputTex: texture_2d<f32>;
@group(0) @binding(1) var outputTex: texture_storage_2d<rgba8unorm, write>;
@group(0) @binding(2) var<uniform> params: Params;

@compute @workgroup_size(16, 16)
fn main(@builtin(global_invocation_id) id: vec3<u32>) {
    let size = textureDimensions(inputTex);
    let x = i32(id.x);
    let y = i32(id.y);

    if (x >= i32(size.x) || y >= i32(size.y)) {
        return;
    }

    let sigma = max(params.blurRadius, 0.5);
    let radius = min(i32(ceil(sigma * 3.0)), 64);
    var colorSum = vec4<f32>(0.0);
    var weightSum: f32 = 0.0;
    for (var dy = -radius; dy <= radius; dy = dy + 1) {
        let sampleY = clamp(y + dy, 0, i32(size.y) - 1);
        let distance = f32(dy);
        let weight = exp(-0.5 * distance * distance / (sigma * sigma));
        colorSum += textureLoad(inputTex, vec2<i32>(x, sampleY), 0) * weight;
        weightSum += weight;
    }

    textureStore(outputTex, vec2<i32>(x, y), colorSum / max(weightSum, 0.0001));
}
";


}
