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
    let shadowColor = vec4<f32>(params.color.rgb, params.color.a * avgAlpha);

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

    // 5-tap Gaussian Blur of alpha channel, colorized by params.color
    var alphaSum: f32 = 0.0;
    
    alphaSum += textureLoad(inputTex, vec2<i32>(clamp(x - 2, 0, i32(size.x) - 1), y), 0).a * 0.0625;
    alphaSum += textureLoad(inputTex, vec2<i32>(clamp(x - 1, 0, i32(size.x) - 1), y), 0).a * 0.25;
    alphaSum += textureLoad(inputTex, vec2<i32>(clamp(x + 0, 0, i32(size.x) - 1), y), 0).a * 0.375;
    alphaSum += textureLoad(inputTex, vec2<i32>(clamp(x + 1, 0, i32(size.x) - 1), y), 0).a * 0.25;
    alphaSum += textureLoad(inputTex, vec2<i32>(clamp(x + 2, 0, i32(size.x) - 1), y), 0).a * 0.0625;

    let shadowColor = vec4<f32>(params.color.rgb, params.color.a * alphaSum);
    textureStore(outputTex, vec2<i32>(x, y), shadowColor);
}
";

    public const string LiquidGlass = @"
struct Params {
    glassColor: vec4<f32>,
    fluidColor: vec4<f32>,
    progress: f32,
    time: f32,
    refraction: f32,
    shininess: f32,
    width: f32,
    height: f32,
};

@group(0) @binding(0) var inputTex: texture_2d<f32>;
@group(0) @binding(1) var outputTex: texture_storage_2d<rgba8unorm, write>;
@group(0) @binding(2) var<uniform> params: Params;

fn hash(p: vec2<f32>) -> f32 {
    return fract(sin(dot(p, vec2<f32>(127.1, 311.7))) * 43758.5453123);
}

fn noise(p: vec2<f32>) -> f32 {
    let i = floor(p);
    let f = fract(p);
    let u = f * f * (3.0 - 2.0 * f);
    
    let a = hash(i + vec2<f32>(0.0, 0.0));
    let b = hash(i + vec2<f32>(1.0, 0.0));
    let c = hash(i + vec2<f32>(0.0, 1.0));
    let d = hash(i + vec2<f32>(1.0, 1.0));
    
    return mix(mix(a, b, u.x), mix(c, d, u.x), u.y);
}

@compute @workgroup_size(16, 16)
fn main(@builtin(global_invocation_id) id: vec3<u32>) {
    let size = textureDimensions(inputTex);
    let x = i32(id.x);
    let y = i32(id.y);

    if (x >= i32(size.x) || y >= i32(size.y)) {
        return;
    }

    let currentPixel = textureLoad(inputTex, vec2<i32>(x, y), 0);
    let alpha = currentPixel.a;

    if (alpha <= 0.01) {
        textureStore(outputTex, vec2<i32>(x, y), vec4<f32>(0.0));
        return;
    }

    let w = i32(size.x);
    let h = i32(size.y);
    
    let aLeft   = textureLoad(inputTex, vec2<i32>(clamp(x - 1, 0, w - 1), y), 0).a;
    let aRight  = textureLoad(inputTex, vec2<i32>(clamp(x + 1, 0, w - 1), y), 0).a;
    let aTop    = textureLoad(inputTex, vec2<i32>(x, clamp(y - 1, 0, h - 1)), 0).a;
    let aBottom = textureLoad(inputTex, vec2<i32>(x, clamp(y + 1, 0, h - 1)), 0).a;

    let nx = (aLeft - aRight) * 2.0;
    let ny = (aTop - aBottom) * 2.0;
    
    var normal = normalize(vec3<f32>(nx, ny, 1.0 - clamp(length(vec2<f32>(nx, ny)), 0.0, 0.95)));

    let uv = vec2<f32>(f32(x) / f32(w), f32(y) / f32(h));
    
    let t = params.time * 2.0;
    let wave1 = sin(uv.x * 12.0 + t) * 0.03;
    let wave2 = cos(uv.x * 24.0 - t * 1.5) * 0.015;
    let noiseVal = noise(uv * 8.0 + vec2<f32>(t * 0.1, -t * 0.2)) * 0.02;
    let wave = wave1 + wave2 + noiseVal;

    let isFluid = uv.x < (params.progress + wave);
    
    var baseColor = params.glassColor;
    if (isFluid) {
        let depthHighlight = smoothstep(0.0, 1.0, uv.y) * 0.25;
        let flowAnim = sin(uv.x * 30.0 + t * 4.0) * 0.05;
        baseColor = vec4<f32>(params.fluidColor.rgb + vec3<f32>(depthHighlight + flowAnim), params.fluidColor.a);
    }

    let lightDir = normalize(vec3<f32>(-0.8, 0.8, 1.2));
    let viewDir = vec3<f32>(0.0, 0.0, 1.0);
    
    let halfDir = normalize(lightDir + viewDir);
    let specIntensity = pow(max(dot(normal, halfDir), 0.0), params.shininess);
    let specularColor = vec3<f32>(1.0, 1.0, 1.0) * specIntensity * 0.7;

    let rimIntensity = pow(1.0 - max(dot(normal, viewDir), 0.0), 3.0);
    let rimColor = vec3<f32>(1.0, 1.0, 1.0) * rimIntensity * 0.45;

    var waveSpec: f32 = 0.0;
    let borderDist = abs(uv.x - (params.progress + wave));
    if (isFluid && borderDist < 0.02) {
        let waveNormal = normalize(vec3<f32>(cos(uv.x * 12.0 + t), -1.0, 0.5));
        waveSpec = pow(max(dot(waveNormal, halfDir), 0.0), 16.0) * 0.6 * (1.0 - borderDist / 0.02);
    }

    var finalColor = vec4<f32>(baseColor.rgb + specularColor + rimColor + vec3<f32>(waveSpec), baseColor.a * alpha);
    
    let distFromEdge = 1.0 - alpha;
    finalColor.r = finalColor.r + distFromEdge * 0.1;
    finalColor.g = finalColor.g + distFromEdge * 0.15;
    finalColor.b = finalColor.b + distFromEdge * 0.2;

    textureStore(outputTex, vec2<i32>(x, y), finalColor);
}
";
}

