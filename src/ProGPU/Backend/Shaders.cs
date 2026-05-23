namespace ProGPU.Backend;

public static class Shaders
{
    public const string VectorShader = @"
struct Brush {
    brushType: u32,
    opacity: f32,
    gradientStart: vec2<f32>,
    gradientEnd: vec2<f32>,
    gradientCenter: vec2<f32>,
    gradientRadius: f32,
    stopCount: u32,
    _pad: u32,
    stopColors0: vec4<f32>,
    stopColors1: vec4<f32>,
    stopColors2: vec4<f32>,
    stopColors3: vec4<f32>,
    stopOffsets: vec4<f32>,
};

struct Uniforms {
    projection: mat4x4<f32>,
    brushes: array<Brush, 64>,
};

@group(0) @binding(0) var<uniform> uniforms: Uniforms;

struct VertexInput {
    @location(0) position: vec2<f32>,
    @location(1) color: vec4<f32>,
    @location(2) texCoord: vec2<f32>,
    @location(3) brushIndex: f32,
    @location(4) shapeSize: vec2<f32>,
    @location(5) cornerRadius: f32,
    @location(6) strokeThickness: f32,
    @location(7) shapeType: f32,
};

struct VertexOutput {
    @builtin(position) position: vec4<f32>,
    @location(0) color: vec4<f32>,
    @location(1) texCoord: vec2<f32>,
    @location(2) brushIndex: f32,
    @location(3) shapeSize: vec2<f32>,
    @location(4) cornerRadius: f32,
    @location(5) strokeThickness: f32,
    @location(6) shapeType: f32,
    @location(7) gridIndex: f32,
};

@vertex
fn vs_main(input: VertexInput, @builtin(vertex_index) vertexIndex: u32) -> VertexOutput {
    var output: VertexOutput;
    let sType = u32(round(input.shapeType));
    var worldPos = input.position;
    var texCoord = input.texCoord;

    if (sType == 3u) {
        // GPU Stroke Expansion
        var miterN = vec2<f32>(0.0, 0.0);
        var miterScale: f32 = 1.0;
        
        let p0 = input.texCoord;
        let p1 = input.shapeSize;
        
        let isStart = length(input.position - input.texCoord) < 0.001;
        worldPos = select(p1, p0, isStart);

        let len1 = length(worldPos - p0);
        let len2 = length(p1 - worldPos);

        if (len1 < 0.001) {
            if (len2 > 0.001) {
                let dir = normalize(p1 - p0);
                miterN = vec2<f32>(-dir.y, dir.x);
            }
        } else if (len2 < 0.001) {
            let dir = normalize(worldPos - p0);
            miterN = vec2<f32>(-dir.y, dir.x);
        } else {
            let dir1 = normalize(worldPos - p0);
            let dir2 = normalize(p1 - worldPos);
            let n1 = vec2<f32>(-dir1.y, dir1.x);
            let n2 = vec2<f32>(-dir2.y, dir2.x);
            miterN = normalize(n1 + n2);
            miterScale = clamp(1.0 / max(dot(miterN, n1), 0.0001), 0.5, 4.0);
        }
        let offset = miterN * (input.strokeThickness * 0.5) * miterScale * input.cornerRadius;
        worldPos = worldPos + offset;
    } else if (sType == 5u) {
        // GPU Quadratic Bezier Curve Evaluation
        let p0 = input.position;
        let p1 = input.texCoord;
        let p2 = input.shapeSize;
        
        let idxStart = u32(round(input.cornerRadius));
        let localIndex = vertexIndex - idxStart;
        let N = clamp(u32(round(input.strokeThickness * 1.5 + 8.0)), 8u, 24u);
        let t = f32(localIndex / 2u) / f32(N);
        let signVal = select(-1.0, 1.0, (localIndex % 2u) == 0u);
        
        let oneMinusT = 1.0 - t;
        let pos = oneMinusT * oneMinusT * p0 + 2.0 * oneMinusT * t * p1 + t * t * p2;
        let tangent = 2.0 * oneMinusT * (p1 - p0) + 2.0 * t * (p2 - p1);
        let len = length(tangent);
        var normal = vec2<f32>(0.0, 0.0);
        if (len > 0.0001) {
            normal = vec2<f32>(-tangent.y, tangent.x) / len;
        }
        let offset = normal * (input.strokeThickness * 0.5) * signVal;
        worldPos = pos + offset;
        texCoord = pos;
    } else if (sType == 6u) {
        // GPU Cubic Bezier Curve Evaluation
        let p0 = input.position;
        let p1 = input.texCoord;
        let p2 = input.shapeSize;
        let p3 = input.color.rg;
        
        let idxStart = u32(round(input.cornerRadius));
        let localIndex = vertexIndex - idxStart;
        let N = clamp(u32(round(input.strokeThickness * 1.5 + 8.0)), 8u, 24u);
        let t = f32(localIndex / 2u) / f32(N);
        let signVal = select(-1.0, 1.0, (localIndex % 2u) == 0u);
        
        let oneMinusT = 1.0 - t;
        
        let pos = oneMinusT * oneMinusT * oneMinusT * p0 
                + 3.0 * oneMinusT * oneMinusT * t * p1 
                + 3.0 * oneMinusT * t * t * p2 
                + t * t * t * p3;
                
        let tangent = 3.0 * oneMinusT * oneMinusT * (p1 - p0) 
                    + 6.0 * oneMinusT * t * (p2 - p1) 
                    + 3.0 * t * t * (p3 - p2);
                    
        let len = length(tangent);
        var normal = vec2<f32>(0.0, 0.0);
        if (len > 0.0001) {
            normal = vec2<f32>(-tangent.y, tangent.x) / len;
        }
        let offset = normal * (input.strokeThickness * 0.5) * signVal;
        worldPos = pos + offset;
        texCoord = pos;
    }

    output.position = uniforms.projection * vec4<f32>(worldPos, 0.0, 1.0);
    output.color = input.color;
    output.texCoord = texCoord;
    output.brushIndex = input.brushIndex;
    output.shapeSize = input.shapeSize;
    output.cornerRadius = input.cornerRadius;
    output.strokeThickness = input.strokeThickness;
    output.shapeType = input.shapeType;
    output.gridIndex = 0.0;
    return output;
}

@fragment
fn fs_main(input: VertexOutput) -> @location(0) vec4<f32> {
    let sType = u32(round(input.shapeType));
    var d: f32 = -1.0;

    if (sType == 0u) {
        // Rectangle SDF
        let d_vec = abs(input.texCoord) - input.shapeSize * 0.5;
        d = length(max(d_vec, vec2<f32>(0.0))) + min(max(d_vec.x, d_vec.y), 0.0);
    } else if (sType == 1u) {
        // Ellipse SDF
        let rx = input.shapeSize.x * 0.5;
        let ry = input.shapeSize.y * 0.5;
        if (rx > 0.0001 && ry > 0.0001) {
            let v = (input.texCoord.x * input.texCoord.x) / (rx * rx) + (input.texCoord.y * input.texCoord.y) / (ry * ry);
            let grad = vec2<f32>((2.0 * input.texCoord.x) / (rx * rx), (2.0 * input.texCoord.y) / (ry * ry));
            d = (v - 1.0) / max(length(grad), 0.0001);
        }
    } else if (sType == 2u) {
        // Rounded Rectangle SDF
        let r = input.cornerRadius;
        let d_vec = abs(input.texCoord) - (input.shapeSize * 0.5 - vec2<f32>(r));
        d = length(max(d_vec, vec2<f32>(0.0))) + min(max(d_vec.x, d_vec.y), 0.0) - r;
    }

    var shapeAlpha: f32 = 1.0;
    if (sType < 3u) {
        var d_shape: f32 = 0.0;
        if (input.strokeThickness > 0.0) {
            d_shape = abs(d) - input.strokeThickness * 0.5;
        } else {
            d_shape = d;
        }
        let fw = max(fwidth(d_shape), 0.0001);
        shapeAlpha = 1.0 - smoothstep(-0.5 * fw, 0.5 * fw, d_shape);
    }

    if (shapeAlpha <= 0.0) {
        discard;
    }

    // Process Color Brush
    let bIdx = u32(round(input.brushIndex));
    let brush = uniforms.brushes[bIdx];

    var finalColor = input.color;
    if (brush.brushType == 0u) {
        let sType = u32(round(input.shapeType));
        if (sType == 5u || sType == 6u) {
            finalColor = vec4<f32>(brush.stopColors0.rgb, brush.stopColors0.a * brush.opacity);
        } else {
            finalColor = vec4<f32>(input.color.rgb, input.color.a * brush.opacity);
        }
    } else {
        var evalCoord = input.texCoord;
        if (sType < 3u) {
            evalCoord = input.color.xy + input.texCoord;
        }

        var t: f32 = 0.0;
        if (brush.brushType == 1u) {
            // Linear Gradient
            let gradVec = brush.gradientEnd - brush.gradientStart;
            let lenSq = dot(gradVec, gradVec);
            if (lenSq > 0.0001) {
                t = dot(evalCoord - brush.gradientStart, gradVec) / lenSq;
            }
        } else if (brush.brushType == 2u) {
            // Radial Gradient
            let dist = distance(evalCoord, brush.gradientCenter);
            if (brush.gradientRadius > 0.0001) {
                t = dist / brush.gradientRadius;
            }
        }
        t = clamp(t, 0.0, 1.0);

        // Interpolate colors based on stops
        var gradColor = brush.stopColors0;
        if (brush.stopCount > 1u) {
            if (t <= brush.stopOffsets.y) {
                let factor = (t - brush.stopOffsets.x) / max(brush.stopOffsets.y - brush.stopOffsets.x, 0.0001);
                gradColor = mix(brush.stopColors0, brush.stopColors1, clamp(factor, 0.0, 1.0));
            } else if (t <= brush.stopOffsets.z) {
                let factor = (t - brush.stopOffsets.y) / max(brush.stopOffsets.z - brush.stopOffsets.y, 0.0001);
                gradColor = mix(brush.stopColors1, brush.stopColors2, clamp(factor, 0.0, 1.0));
            } else {
                let factor = (t - brush.stopOffsets.z) / max(brush.stopOffsets.w - brush.stopOffsets.z, 0.0001);
                gradColor = mix(brush.stopColors2, brush.stopColors3, clamp(factor, 0.0, 1.0));
            }
        }
        finalColor = vec4<f32>(gradColor.rgb, gradColor.a * brush.opacity);
    }

    return vec4<f32>(finalColor.rgb, finalColor.a * shapeAlpha);
}
";

    public const string TextShader = @"
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

struct Uniforms {
    projection: mat4x4<f32>,
};

@group(0) @binding(0) var<uniform> uniforms: Uniforms;

@vertex
fn vs_main(input: VertexInput) -> VertexOutput {
    var output: VertexOutput;
    output.position = uniforms.projection * vec4<f32>(input.position, 0.0, 1.0);
    output.color = input.color;
    output.texCoord = input.texCoord;
    return output;
}

@group(1) @binding(0) var atlasSampler: sampler;
@group(1) @binding(1) var atlasTexture: texture_2d<f32>;

@fragment
fn fs_main(input: VertexOutput) -> @location(0) vec4<f32> {
    let alpha = textureSample(atlasTexture, atlasSampler, input.texCoord).r;
    return vec4<f32>(input.color.rgb, input.color.a * alpha);
}
";

    public const string TextureShader = @"
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

struct Uniforms {
    projection: mat4x4<f32>,
};

@group(0) @binding(0) var<uniform> uniforms: Uniforms;

@vertex
fn vs_main(input: VertexInput) -> VertexOutput {
    var output: VertexOutput;
    output.position = uniforms.projection * vec4<f32>(input.position, 0.0, 1.0);
    output.color = input.color;
    output.texCoord = input.texCoord;
    return output;
}

    @group(1) @binding(0) var texSampler: sampler;
@group(1) @binding(1) var texTexture: texture_2d<f32>;

@fragment
fn fs_main(input: VertexOutput) -> @location(0) vec4<f32> {
    let texColor = textureSample(texTexture, texSampler, input.texCoord);
    return texColor * input.color;
}
";

    public const string GlyphRasterizerShader = @"
struct GlyphUniforms {
    xStart: f32,
    yStart: f32,
    scale: f32,
    glyphIndex: u32,
    atlasX: u32,
    atlasY: u32,
    width: u32,
    height: u32,
};

struct GlyphRecord {
    startSegment: u32,
    segmentCount: u32,
    minX: f32,
    minY: f32,
    maxX: f32,
    maxY: f32,
    _pad0: u32,
    _pad1: u32,
};

struct Segment {
    p0: vec2<f32>,
    p1: vec2<f32>,
    p2: vec2<f32>,
    segmentType: u32,
    _pad: u32,
};

@group(0) @binding(0) var<uniform> uniforms: GlyphUniforms;
@group(0) @binding(1) var<storage, read> glyphRecords: array<GlyphRecord>;
@group(0) @binding(2) var<storage, read> segments: array<Segment>;
@group(0) @binding(3) var atlasTexture: texture_storage_2d<r8unorm, write>;

fn solve_quadratic(a: f32, b: f32, c: f32, roots: ptr<function, array<f32, 2>>, root_count: ptr<function, u32>) {
    if (abs(a) < 0.00001) {
        if (abs(b) > 0.00001) {
            (*roots)[0] = -c / b;
            *root_count = 1u;
        } else {
            *root_count = 0u;
        }
    } else {
        let d = b * b - 4.0 * a * c;
        if (d < 0.0) {
            *root_count = 0u;
        } else if (d == 0.0) {
            (*roots)[0] = -b / (2.0 * a);
            *root_count = 1u;
        } else {
            let sqrt_d = sqrt(d);
            (*roots)[0] = (-b - sqrt_d) / (2.0 * a);
            (*roots)[1] = (-b + sqrt_d) / (2.0 * a);
            *root_count = 2u;
        }
    }
}

fn is_point_inside(p: vec2<f32>, record: GlyphRecord) -> bool {
    var winding: i32 = 0;
    let endIdx = record.startSegment + record.segmentCount;
    for (var i: u32 = record.startSegment; i < endIdx; i = i + 1u) {
        let seg = segments[i];
        if (seg.segmentType == 0u) {
            // Line Segment from A to B
            let A = seg.p0;
            let B = seg.p1;
            if (A.y == B.y) {
                continue;
            }
            if (A.y <= p.y) {
                if (B.y > p.y) { // Upward crossing
                    let t = (p.y - A.y) / (B.y - A.y);
                    let intersectX = A.x + t * (B.x - A.x);
                    if (p.x < intersectX) {
                        winding = winding + 1;
                    }
                }
            } else {
                if (B.y <= p.y) { // Downward crossing
                    let t = (p.y - A.y) / (B.y - A.y);
                    let intersectX = A.x + t * (B.x - A.x);
                    if (p.x < intersectX) {
                        winding = winding - 1;
                    }
                }
            }
        } else if (seg.segmentType == 1u) {
            // Quadratic Bezier from A to C with control point B
            let A = seg.p0;
            let B = seg.p1;
            let C = seg.p2;
            
            let a = A.y - 2.0 * B.y + C.y;
            let b = 2.0 * (B.y - A.y);
            let c = A.y - p.y;
            
            var roots = array<f32, 2>(0.0, 0.0);
            var root_count: u32 = 0u;
            solve_quadratic(a, b, c, &roots, &root_count);
            
            for (var r: u32 = 0u; r < root_count; r = r + 1u) {
                let t = roots[r];
                if (t >= 0.0 && t <= 1.0) {
                    let one_minus_t = 1.0 - t;
                    let x_t = one_minus_t * one_minus_t * A.x + 2.0 * one_minus_t * t * B.x + t * t * C.x;
                    if (p.x < x_t) {
                        let deriv_y = 2.0 * one_minus_t * (B.y - A.y) + 2.0 * t * (C.y - B.y);
                        if (deriv_y > 0.0) {
                            winding = winding + 1;
                        } else if (deriv_y < 0.0) {
                            winding = winding - 1;
                        }
                    }
                }
            }
        }
    }
    return winding != 0;
}

@compute @workgroup_size(16, 16)
fn cs_main(@builtin(global_invocation_id) global_id: vec3<u32>) {
    let x = global_id.x;
    let y = global_id.y;
    
    if (x >= uniforms.width || y >= uniforms.height) {
        return;
    }
    
    let glyphIndex = uniforms.glyphIndex;
    let record = glyphRecords[glyphIndex];
    
    let px = uniforms.xStart + f32(x);
    let py = uniforms.yStart + f32(y);
    
    var coverage: f32 = 0.0;
    
    // Sample 0: +0.25, +0.25
    let sp0 = vec2<f32>(px + 0.25, py + 0.25);
    let fp0 = vec2<f32>(sp0.x / uniforms.scale, -sp0.y / uniforms.scale);
    if (is_point_inside(fp0, record)) {
        coverage = coverage + 0.25;
    }
    
    // Sample 1: +0.75, +0.25
    let sp1 = vec2<f32>(px + 0.75, py + 0.25);
    let fp1 = vec2<f32>(sp1.x / uniforms.scale, -sp1.y / uniforms.scale);
    if (is_point_inside(fp1, record)) {
        coverage = coverage + 0.25;
    }
    
    // Sample 2: +0.25, +0.75
    let sp2 = vec2<f32>(px + 0.25, py + 0.75);
    let fp2 = vec2<f32>(sp2.x / uniforms.scale, -sp2.y / uniforms.scale);
    if (is_point_inside(fp2, record)) {
        coverage = coverage + 0.25;
    }
    
    // Sample 3: +0.75, +0.75
    let sp3 = vec2<f32>(px + 0.75, py + 0.75);
    let fp3 = vec2<f32>(sp3.x / uniforms.scale, -sp3.y / uniforms.scale);
    if (is_point_inside(fp3, record)) {
        coverage = coverage + 0.25;
    }
    
    let writeCoord = vec2<u32>(uniforms.atlasX + x, uniforms.atlasY + y);
    textureStore(atlasTexture, writeCoord, vec4<f32>(coverage, 0.0, 0.0, 0.0));
}
";
}
