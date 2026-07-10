using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Silk.NET.WebGPU;
using Silk.NET.Core.Native;
using ProGPU.Vector;
using ProGPU.Backend;

namespace ProGPU.Scene.Extensions
{
    public class HatchExtensionPipeline : ICompositorExtension
    {
        private const string HatchShaderCode = @"
struct Brush {
    brushType: u32,
    opacity: f32,
    gradientStart: vec2<f32>,
    gradientEnd: vec2<f32>,
    gradientCenter: vec2<f32>,
    gradientRadius: f32,
    stopCount: u32,
    gradientRadiusY: f32,
    spreadMethod: u32,
    colorInterpolationMode: u32,
    stopOffset: u32,
    stopColors0: vec4<f32>,
    stopColors1: vec4<f32>,
    stopColors2: vec4<f32>,
    stopColors3: vec4<f32>,
    stopColors4: vec4<f32>,
    stopColors5: vec4<f32>,
    stopColors6: vec4<f32>,
    stopColors7: vec4<f32>,
    stopOffsets0: vec4<f32>,
    stopOffsets1: vec4<f32>,
    coordinateTransform0: vec4<f32>,
    coordinateTransform1: vec4<f32>,
};

struct GradientStop {
    color: vec4<f32>,
    offset: f32,
};

struct VSUniforms {
    projection: mat4x4<f32>,
    mvp: mat4x4<f32>,
    view: mat4x4<f32>,
};

struct GpuHatchRecord {
    startSegment: u32,
    segmentCount: u32,
    minX: f32,
    minY: f32,
    maxX: f32,
    maxY: f32,
    _pad0: u32,
    _pad1: u32,
};

struct GpuHatchSegment {
    p0: vec2<f32>,
    p1: vec2<f32>,
    p2: vec2<f32>,
    p3: vec2<f32>,
    segmentType: u32,
    _pad0: u32,
    _pad1: u32,
    _pad2: u32,
};

@group(0) @binding(0) var<uniform> uniforms: VSUniforms;
@group(0) @binding(1) var<storage, read> brushes: array<Brush>;
@group(0) @binding(2) var<storage, read> hatchRecords: array<GpuHatchRecord>;
@group(0) @binding(3) var<storage, read> hatchSegments: array<GpuHatchSegment>;
@group(0) @binding(4) var<storage, read> gradientStops: array<GradientStop>;

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

fn apply_gradient_spread(t: f32, spreadMethod: u32) -> f32 {
    if (spreadMethod == 1u) {
        let period = fract(t * 0.5) * 2.0;
        return select(period, 2.0 - period, period > 1.0);
    }

    if (spreadMethod == 2u) {
        return fract(t);
    }

    return clamp(t, 0.0, 1.0);
}

fn get_gradient_stop_color(brush: Brush, index: u32) -> vec4<f32> {
    return gradientStops[brush.stopOffset + index].color;
}

fn get_gradient_stop_offset(brush: Brush, index: u32) -> f32 {
    return gradientStops[brush.stopOffset + index].offset;
}

fn srgb_to_linear_component(value: f32) -> f32 {
    if (value <= 0.04045) {
        return value / 12.92;
    }

    return pow((value + 0.055) / 1.055, 2.4);
}

fn linear_to_srgb_component(value: f32) -> f32 {
    let clamped = max(value, 0.0);
    if (clamped <= 0.0031308) {
        return clamped * 12.92;
    }

    return (1.055 * pow(clamped, 1.0 / 2.4)) - 0.055;
}

fn srgb_to_linear_color(color: vec4<f32>) -> vec3<f32> {
    return vec3<f32>(
        srgb_to_linear_component(color.r),
        srgb_to_linear_component(color.g),
        srgb_to_linear_component(color.b));
}

fn linear_to_srgb_color(color: vec3<f32>) -> vec3<f32> {
    return vec3<f32>(
        linear_to_srgb_component(color.r),
        linear_to_srgb_component(color.g),
        linear_to_srgb_component(color.b));
}

fn interpolate_gradient_color(brush: Brush, startColor: vec4<f32>, endColor: vec4<f32>, factor: f32) -> vec4<f32> {
    if (brush.colorInterpolationMode == 1u) {
        let linearColor = mix(srgb_to_linear_color(startColor), srgb_to_linear_color(endColor), factor);
        return vec4<f32>(linear_to_srgb_color(linearColor), mix(startColor.a, endColor.a, factor));
    }

    return mix(startColor, endColor, factor);
}

fn sample_gradient_color(brush: Brush, t: f32) -> vec4<f32> {
    let stopCount = brush.stopCount;
    if (stopCount == 0u) {
        return vec4<f32>(0.0, 0.0, 0.0, 0.0);
    }

    var previousColor = get_gradient_stop_color(brush, 0u);
    var previousOffset = get_gradient_stop_offset(brush, 0u);
    var i = 1u;
    loop {
        if (i >= stopCount) {
            break;
        }

        let currentColor = get_gradient_stop_color(brush, i);
        let currentOffset = get_gradient_stop_offset(brush, i);
        if (t <= currentOffset) {
            let factor = (t - previousOffset) / max(currentOffset - previousOffset, 0.0001);
            return interpolate_gradient_color(brush, previousColor, currentColor, clamp(factor, 0.0, 1.0));
        }

        previousColor = currentColor;
        previousOffset = currentOffset;
        i = i + 1u;
    }

    return previousColor;
}

fn transform_brush_coordinate(brush: Brush, coord: vec2<f32>) -> vec2<f32> {
    let p = vec3<f32>(coord, 1.0);
    return vec2<f32>(
        dot(p, brush.coordinateTransform0.xyz),
        dot(p, brush.coordinateTransform1.xyz));
}

fn solve_two_point_conical_gradient(brush: Brush, coord: vec2<f32>) -> vec2<f32> {
    let centerDelta = brush.gradientCenter - brush.gradientStart;
    let radiusDelta = brush.gradientRadiusY - brush.gradientRadius;
    let point = coord - brush.gradientStart;
    let a = dot(centerDelta, centerDelta) - radiusDelta * radiusDelta;
    let b = -2.0 * (dot(point, centerDelta) + brush.gradientRadius * radiusDelta);
    let c = dot(point, point) - brush.gradientRadius * brush.gradientRadius;

    if (abs(a) < 0.00001) {
        if (abs(b) > 0.00001) {
            let root = -c / b;
            let radius = brush.gradientRadius + root * radiusDelta;
            if (radius >= -0.00001) {
                return vec2<f32>(root, 1.0);
            }
        }

        return vec2<f32>(0.0, 0.0);
    }

    let discriminant = (b * b) - (4.0 * a * c);
    if (discriminant < 0.0) {
        return vec2<f32>(0.0, 0.0);
    }

    let sqrtDiscriminant = sqrt(discriminant);
    let denominator = 2.0 * a;
    let root0 = (-b - sqrtDiscriminant) / denominator;
    let root1 = (-b + sqrtDiscriminant) / denominator;
    let root0Radius = brush.gradientRadius + root0 * radiusDelta;
    let root1Radius = brush.gradientRadius + root1 * radiusDelta;
    let root0Valid = root0Radius >= -0.00001;
    let root1Valid = root1Radius >= -0.00001;

    if (root0Valid && root1Valid) {
        return vec2<f32>(max(root0, root1), 1.0);
    }

    if (root0Valid) {
        return vec2<f32>(root0, 1.0);
    }

    if (root1Valid) {
        return vec2<f32>(root1, 1.0);
    }

    return vec2<f32>(0.0, 0.0);
}

@vertex
fn vs_main(input: VertexInput) -> VertexOutput {
    var output: VertexOutput;
    
    var sType = u32(round(input.shapeType));
    var isStatic = false;
    var useGpuTransforms = false;
    if (input.shapeType >= 195.0) {
        isStatic = true;
        sType = u32(round(input.shapeType - 200.0));
    } else if (input.shapeType >= 95.0) {
        useGpuTransforms = true;
        sType = u32(round(input.shapeType - 100.0));
    }

    var inPos = input.position;
    var inTexCoord = input.texCoord;
    var inShapeSize = input.shapeSize;
    var inColor = input.color;

    if (isStatic || useGpuTransforms) {
        if (useGpuTransforms) {
            inPos = (uniforms.view * vec4<f32>(input.position, 0.0, 1.0)).xy;
            inTexCoord = (uniforms.view * vec4<f32>(input.texCoord, 0.0, 1.0)).xy;
            inShapeSize = (uniforms.view * vec4<f32>(input.shapeSize, 0.0, 1.0)).xy;
        } else {
            inPos = (uniforms.mvp * vec4<f32>(input.position, 0.0, 1.0)).xy;
            inTexCoord = (uniforms.mvp * vec4<f32>(input.texCoord, 0.0, 1.0)).xy;
            inShapeSize = (uniforms.mvp * vec4<f32>(input.shapeSize, 0.0, 1.0)).xy;
        }
    }

    output.position = uniforms.projection * vec4<f32>(inPos, 0.0, 1.0);
    output.color = inColor;
    output.texCoord = inTexCoord;
    output.brushIndex = input.brushIndex;
    output.shapeSize = inShapeSize;
    output.cornerRadius = input.cornerRadius;
    output.strokeThickness = input.strokeThickness;
    output.shapeType = f32(sType);
    output.gridIndex = 0.0;
    return output;
}

" + Shaders.SharedWgpuMathCode + @"

fn is_hatch_point_inside(p: vec2<f32>, record: GpuHatchRecord) -> bool {
    var winding: i32 = 0;
    let endIdx = record.startSegment + record.segmentCount;
    for (var i: u32 = record.startSegment; i < endIdx; i = i + 1u) {
        let seg = hatchSegments[i];
        if (seg.segmentType == 0u) {
            let A = seg.p0;
            let B = seg.p1;
            if (A.y == B.y) {
                continue;
            }
            if (A.y <= p.y) {
                if (B.y > p.y) {
                    let t = (p.y - A.y) / (B.y - A.y);
                    let intersectX = A.x + t * (B.x - A.x);
                    if (p.x < intersectX) {
                        winding = winding + 1;
                    }
                }
            } else {
                if (B.y <= p.y) {
                    let t = (p.y - A.y) / (B.y - A.y);
                    let intersectX = A.x + t * (B.x - A.x);
                    if (p.x < intersectX) {
                        winding = winding - 1;
                    }
                }
            }
        } else if (seg.segmentType == 1u) {
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
                if (t >= -0.01 && t <= 1.01) {
                    let t_eval = clamp(t, 0.00001, 0.99999);
                    let omt_eval = 1.0 - t_eval;
                    let deriv_y = 2.0 * omt_eval * (B.y - A.y) + 2.0 * t_eval * (C.y - B.y);
                    
                    var is_valid = false;
                    if (t < 0.005) {
                        if (deriv_y > 0.0) {
                            is_valid = (p.y >= A.y);
                        } else if (deriv_y < 0.0) {
                            is_valid = (p.y < A.y);
                        }
                    } else if (t > 0.995) {
                        if (deriv_y > 0.0) {
                            is_valid = (p.y < C.y);
                        } else if (deriv_y < 0.0) {
                            is_valid = (p.y >= C.y);
                        }
                    } else {
                        is_valid = true;
                    }
                    
                    if (is_valid) {
                        let tc = clamp(t, 0.0, 1.0);
                        let omt = 1.0 - tc;
                        let intersectX = omt * omt * A.x + 2.0 * omt * tc * B.x + tc * tc * C.x;
                        if (p.x < intersectX) {
                            if (deriv_y > 0.0) {
                                winding = winding + 1;
                            } else if (deriv_y < 0.0) {
                                winding = winding - 1;
                            }
                        }
                    }
                }
            }
        } else if (seg.segmentType == 2u) {
            let A = seg.p0;
            let B = seg.p1;
            let C = seg.p2;
            let D = seg.p3;
            
            let a = -A.y + 3.0 * B.y - 3.0 * C.y + D.y;
            let b = 3.0 * A.y - 6.0 * B.y + 3.0 * C.y;
            let c = -3.0 * A.y + 3.0 * B.y;
            let d_coeff = A.y - p.y;
            
            var roots = array<f32, 3>(0.0, 0.0, 0.0);
            var root_count: u32 = 0u;
            solve_cubic(a, b, c, d_coeff, &roots, &root_count);
            
            for (var r: u32 = 0u; r < root_count; r = r + 1u) {
                let t = roots[r];
                if (t >= -0.01 && t <= 1.01) {
                    let t_eval = clamp(t, 0.00001, 0.99999);
                    let deriv_y = 3.0 * a * t_eval * t_eval + 2.0 * b * t_eval + c;
                    
                    var is_valid = false;
                    if (t < 0.005) {
                        if (deriv_y > 0.0) {
                            is_valid = (p.y >= A.y);
                        } else if (deriv_y < 0.0) {
                            is_valid = (p.y < A.y);
                        }
                    } else if (t > 0.995) {
                        if (deriv_y > 0.0) {
                            is_valid = (p.y < D.y);
                        } else if (deriv_y < 0.0) {
                            is_valid = (p.y >= D.y);
                        }
                    } else {
                        is_valid = true;
                    }
                    
                    if (is_valid) {
                        let tc = clamp(t, 0.0, 1.0);
                        let omt = 1.0 - tc;
                        let intersectX = omt * omt * omt * A.x + 3.0 * omt * omt * tc * B.x + 3.0 * omt * tc * tc * C.x + tc * tc * tc * D.x;
                        if (p.x < intersectX) {
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
    }
    return winding != 0;
}

@fragment
fn fs_main(input: VertexOutput) -> @location(0) vec4<f32> {
    var shapeAlpha = 0.0;
    
    let hatchRecordIndex = u32(round(input.color.z));
    let record = hatchRecords[hatchRecordIndex];
    let p = input.color.xy;
    
    if (is_hatch_point_inside(p, record)) {
        shapeAlpha = 1.0;
    } else {
        shapeAlpha = 0.0;
    }

    if (shapeAlpha <= 0.0) {
        discard;
    }

    let bIdx = u32(round(input.brushIndex));
    let brush = brushes[bIdx];

    var finalColor = input.color;
    let evalCoord = input.color.xy;

    if (brush.brushType == 0u) {
        finalColor = vec4<f32>(brush.stopColors0.rgb, brush.stopColors0.a * brush.opacity);
    } else if (brush.brushType == 3u) {
        let theta = brush.gradientRadius;
        let spacing = brush.gradientCenter.x;
        let thickness = brush.gradientCenter.y;
        
        let dir = vec2<f32>(cos(theta), sin(theta));
        let dist = dot(evalCoord, dir);
        
        let modDist = abs(fract(dist / spacing) * spacing - spacing * 0.5);
        if (modDist < thickness * 0.5) {
            finalColor = vec4<f32>(brush.stopColors0.rgb, brush.stopColors0.a * brush.opacity);
            shapeAlpha = brush.opacity;
        } else {
            discard;
        }
    } else if (brush.brushType == 4u) {
        let theta = brush.gradientRadius;
        let spacing = brush.gradientCenter.x;
        let thickness = brush.gradientCenter.y;
        
        let dir1 = vec2<f32>(cos(theta), sin(theta));
        let dist1 = dot(evalCoord, dir1);
        let modDist1 = abs(fract(dist1 / spacing) * spacing - spacing * 0.5);
        
        let theta2 = theta + 1.57079632679;
        let dir2 = vec2<f32>(cos(theta2), sin(theta2));
        let dist2 = dot(evalCoord, dir2);
        let modDist2 = abs(fract(dist2 / spacing) * spacing - spacing * 0.5);
        
        if (modDist1 < thickness * 0.5 || modDist2 < thickness * 0.5) {
            finalColor = vec4<f32>(brush.stopColors0.rgb, brush.stopColors0.a * brush.opacity);
            shapeAlpha = brush.opacity;
        } else {
            discard;
        }
    } else {
        let brushCoord = transform_brush_coordinate(brush, evalCoord);
        var t: f32 = 0.0;
        var gradientCoverage: f32 = 1.0;
        if (brush.brushType == 1u) {
            let gradVec = brush.gradientEnd - brush.gradientStart;
            let lenSq = dot(gradVec, gradVec);
            if (lenSq > 0.0001) {
                t = dot(brushCoord - brush.gradientStart, gradVec) / lenSq;
            }
        } else if (brush.brushType == 2u) {
            let rx = brush.gradientRadius;
            let ry = brush.gradientRadiusY;
            if (rx > 0.0001 || ry > 0.0001) {
                let radii = vec2<f32>(max(rx, 0.0001), max(ry, 0.0001));
                let point = (brushCoord - brush.gradientCenter) / radii;
                let origin = (brush.gradientStart - brush.gradientCenter) / radii;
                let direction = point - origin;
                let a = dot(direction, direction);
                if (a > 0.0001) {
                    let b = 2.0 * dot(origin, direction);
                    let c = dot(origin, origin) - 1.0;
                    let discriminant = max((b * b) - (4.0 * a * c), 0.0);
                    let boundary = (-b + sqrt(discriminant)) / (2.0 * a);
                    if (boundary > 0.0001) {
                        t = 1.0 / boundary;
                    }
                }
            }
        } else if (brush.brushType == 5u) {
            let solution = solve_two_point_conical_gradient(brush, brushCoord);
            t = solution.x;
            gradientCoverage = solution.y;
        } else if (brush.brushType == 6u) {
            let direction = brushCoord - brush.gradientCenter;
            t = atan2(direction.y, direction.x) / (2.0 * 3.141592653589793);
            if (t < 0.0) {
                t = t + 1.0;
            }
        }
        if (gradientCoverage <= 0.0) {
            if ((brush.spreadMethod & 0x80000000u) != 0u) {
                finalColor = vec4<f32>(brush.stopColors0.rgb, brush.stopColors0.a * brush.opacity);
            } else {
                finalColor = vec4<f32>(0.0);
            }
        } else {
            t = apply_gradient_spread(t, brush.spreadMethod & 0x7fffffffu);
            let gradColor = sample_gradient_color(brush, t);
            finalColor = vec4<f32>(gradColor.rgb, gradColor.a * brush.opacity);
        }
    }

    return vec4<f32>(finalColor.rgb, finalColor.a * shapeAlpha);
}
";

        private unsafe RenderPipeline* _cachedPipeline;
        private unsafe RenderPipeline* _cachedPipelineOffscreen;
        private unsafe BindGroup* _cachedBindGroup;
        private unsafe BindGroup* _cachedBindGroupOffscreen;
        private int _cachedRecordGen = -1;

        private readonly List<GpuHatchRecord> _dynamicRecords = new();
        private readonly List<GpuHatchSegment> _dynamicSegments = new();
        private GpuBuffer? _dynamicRecordsBuffer;
        private GpuBuffer? _dynamicSegmentsBuffer;

        public void BeginFrame(Compositor compositor)
        {
            _dynamicRecords.Clear();
            _dynamicSegments.Clear();
        }

        private class HatchStaticBuilder
        {
            public readonly List<GpuHatchRecord> Records = new();
            public readonly List<GpuHatchSegment> Segments = new();
        }

        public void BeginStaticCompile(Compositor compositor, StaticCompilationContext context)
        {
            context.SetBuilder(3, new HatchStaticBuilder());
        }

        public void EndStaticCompile(Compositor compositor, StaticCompilationContext context, DxfStaticBuffer staticBuffer)
        {
            if (context.GetBuilder(3) is HatchStaticBuilder builder && builder.Records.Count > 0)
            {
                var state = new HatchStaticState(compositor.Context, builder.Records.ToArray(), builder.Segments.ToArray());
                staticBuffer.SetExtensionState(3, state);
            }
        }

        private class HatchStaticState : IDisposable
        {
            public GpuBuffer RecordsBuffer { get; }
            public GpuBuffer SegmentsBuffer { get; }

            public HatchStaticState(WgpuContext context, GpuHatchRecord[] records, GpuHatchSegment[] segments)
            {
                uint recordsSize = (uint)Math.Max(1, records.Length) * (uint)Marshal.SizeOf<GpuHatchRecord>();
                RecordsBuffer = new GpuBuffer(context, recordsSize, BufferUsage.Storage | BufferUsage.CopyDst, "Static Hatch Records Buffer");
                if (records.Length > 0) RecordsBuffer.Write(new ReadOnlySpan<GpuHatchRecord>(records));
                else RecordsBuffer.WriteSingle(new GpuHatchRecord());

                uint segmentsSize = (uint)Math.Max(1, segments.Length) * (uint)Marshal.SizeOf<GpuHatchSegment>();
                SegmentsBuffer = new GpuBuffer(context, segmentsSize, BufferUsage.Storage | BufferUsage.CopyDst, "Static Hatch Segments Buffer");
                if (segments.Length > 0) SegmentsBuffer.Write(new ReadOnlySpan<GpuHatchSegment>(segments));
                else SegmentsBuffer.WriteSingle(new GpuHatchSegment());
            }

            public void Dispose()
            {
                RecordsBuffer.Dispose();
                SegmentsBuffer.Dispose();
            }
        }

        private void EnsureDynamicBuffers(WgpuContext context)
        {
            uint reqRecordsSize = (uint)Math.Max(1, _dynamicRecords.Count) * (uint)Marshal.SizeOf<GpuHatchRecord>();
            if (_dynamicRecordsBuffer == null || _dynamicRecordsBuffer.Size < reqRecordsSize)
            {
                _dynamicRecordsBuffer?.Dispose();
                _dynamicRecordsBuffer = new GpuBuffer(context, reqRecordsSize * 2, BufferUsage.Storage | BufferUsage.CopyDst, "Dynamic Hatch Records Buffer");
            }

            uint reqSegmentsSize = (uint)Math.Max(1, _dynamicSegments.Count) * (uint)Marshal.SizeOf<GpuHatchSegment>();
            if (_dynamicSegmentsBuffer == null || _dynamicSegmentsBuffer.Size < reqSegmentsSize)
            {
                _dynamicSegmentsBuffer?.Dispose();
                _dynamicSegmentsBuffer = new GpuBuffer(context, reqSegmentsSize * 2, BufferUsage.Storage | BufferUsage.CopyDst, "Dynamic Hatch Segments Buffer");
            }
        }

        public void Dispose()
        {
            _dynamicRecordsBuffer?.Dispose();
            _dynamicSegmentsBuffer?.Dispose();
        }

        public void Compile(
            Compositor compositor,
            IRenderDataProvider? provider,
            Matrix4x4 transform,
            ref RenderCommand cmd)
        {
            if (cmd.Path == null) return;
            
            List<GpuHatchSegment> segmentsList;
            List<GpuHatchRecord> recordsList;

            if (compositor.ActiveCompilationContext != null && compositor.ActiveCompilationContext.GetBuilder(3) is HatchStaticBuilder builder)
            {
                segmentsList = builder.Segments;
                recordsList = builder.Records;
            }
            else
            {
                segmentsList = _dynamicSegments;
                recordsList = _dynamicRecords;
            }

            int startIndex = compositor.VectorIndices.Count;

            if (cmd.Brush != null)
            {
                float bIdx = compositor.RegisterBrush(cmd.Brush);

                uint startSegment = (uint)segmentsList.Count;
                float minX = float.MaxValue;
                float minY = float.MaxValue;
                float maxX = float.MinValue;
                float maxY = float.MinValue;

                void UpdateBounds(Vector2 p)
                {
                    minX = Math.Min(minX, p.X);
                    minY = Math.Min(minY, p.Y);
                    maxX = Math.Max(maxX, p.X);
                    maxY = Math.Max(maxY, p.Y);
                }

                var pathFigures = cmd.Path.Figures;
                for (int figureIndex = 0; figureIndex < pathFigures.Count; figureIndex++)
                {
                    var figure = pathFigures[figureIndex];
                    if (figure.Segments.Count == 0) continue;

                    Vector2 currentPoint = figure.StartPoint;
                    UpdateBounds(currentPoint);

                    var figureSegments = figure.Segments;
                    for (int segmentIndex = 0; segmentIndex < figureSegments.Count; segmentIndex++)
                    {
                        var segment = figureSegments[segmentIndex];
                        if (segment is LineSegment line)
                        {
                            segmentsList.Add(new GpuHatchSegment
                            {
                                P0 = currentPoint,
                                P1 = line.Point,
                                SegmentType = 0
                            });
                            UpdateBounds(line.Point);
                            currentPoint = line.Point;
                        }
                        else if (segment is QuadraticBezierSegment quad)
                        {
                            segmentsList.Add(new GpuHatchSegment
                            {
                                P0 = currentPoint,
                                P1 = quad.ControlPoint,
                                P2 = quad.Point,
                                SegmentType = 1
                            });
                            UpdateBounds(quad.ControlPoint);
                            UpdateBounds(quad.Point);
                            currentPoint = quad.Point;
                        }
                        else if (segment is CubicBezierSegment cubic)
                        {
                            segmentsList.Add(new GpuHatchSegment
                            {
                                P0 = currentPoint,
                                P1 = cubic.ControlPoint1,
                                P2 = cubic.ControlPoint2,
                                P3 = cubic.Point,
                                SegmentType = 2
                            });
                            UpdateBounds(cubic.ControlPoint1);
                            UpdateBounds(cubic.ControlPoint2);
                            UpdateBounds(cubic.Point);
                            currentPoint = cubic.Point;
                        }
                    }

                    if (figure.IsClosed && currentPoint != figure.StartPoint)
                    {
                        segmentsList.Add(new GpuHatchSegment
                        {
                            P0 = currentPoint,
                            P1 = figure.StartPoint,
                            SegmentType = 0
                        });
                        UpdateBounds(figure.StartPoint);
                    }
                }

                uint segmentCount = (uint)segmentsList.Count - startSegment;
                if (segmentCount == 0) return;

                uint hatchRecordIndex = (uint)recordsList.Count;
                recordsList.Add(new GpuHatchRecord
                {
                    StartSegment = startSegment,
                    SegmentCount = segmentCount,
                    MinX = minX,
                    MinY = minY,
                    MaxX = maxX,
                    MaxY = maxY
                });

                var v0 = Vector2.Transform(new Vector2(minX, minY), transform);
                var v1 = Vector2.Transform(new Vector2(maxX, minY), transform);
                var v2 = Vector2.Transform(new Vector2(maxX, maxY), transform);
                var v3 = Vector2.Transform(new Vector2(minX, maxY), transform);

                var c0 = new Vector4(minX, minY, hatchRecordIndex, 0f);
                var c1 = new Vector4(maxX, minY, hatchRecordIndex, 0f);
                var c2 = new Vector4(maxX, maxY, hatchRecordIndex, 0f);
                var c3 = new Vector4(minX, maxY, hatchRecordIndex, 0f);

                int originalVertexCount = compositor.VectorVertices.Count;
                CollectionsMarshal.SetCount(compositor.VectorVertices, originalVertexCount + 4);
                var vertexSpan = CollectionsMarshal.AsSpan(compositor.VectorVertices).Slice(originalVertexCount, 4);

                vertexSpan[0] = new VectorVertex(v0, c0, new Vector2(0f, 0f), bIdx, shapeSize: new Vector2(maxX - minX, maxY - minY), shapeType: 9f);
                vertexSpan[1] = new VectorVertex(v1, c1, new Vector2(1f, 0f), bIdx, shapeSize: new Vector2(maxX - minX, maxY - minY), shapeType: 9f);
                vertexSpan[2] = new VectorVertex(v2, c2, new Vector2(1f, 1f), bIdx, shapeSize: new Vector2(maxX - minX, maxY - minY), shapeType: 9f);
                vertexSpan[3] = new VectorVertex(v3, c3, new Vector2(0f, 1f), bIdx, shapeSize: new Vector2(maxX - minX, maxY - minY), shapeType: 9f);

                int originalIndexCount = compositor.VectorIndices.Count;
                CollectionsMarshal.SetCount(compositor.VectorIndices, originalIndexCount + 6);
                var indexSpan = CollectionsMarshal.AsSpan(compositor.VectorIndices).Slice(originalIndexCount, 6);

                indexSpan[0] = (uint)originalVertexCount;
                indexSpan[1] = (uint)(originalVertexCount + 1);
                indexSpan[2] = (uint)(originalVertexCount + 2);
                indexSpan[3] = (uint)originalVertexCount;
                indexSpan[4] = (uint)(originalVertexCount + 2);
                indexSpan[5] = (uint)(originalVertexCount + 3);

                int indexCount = compositor.VectorIndices.Count - startIndex;
                cmd.PointBufferOffset = startIndex;
                cmd.PointBufferCount = indexCount;
            }
        }

        public unsafe void Render(
            Compositor compositor,
            void* renderPassEncoder,
            bool isOffscreen,
            in Compositor.CompositorDrawCall dc)
        {
            if (dc.PointBufferCount <= 0) return;

            var wgpu = compositor.Context.Wgpu;
            var device = compositor.Context.Device;
            var pass = (RenderPassEncoder*)renderPassEncoder;

            var activePipeline = isOffscreen ? _cachedPipelineOffscreen : _cachedPipeline;
            if (activePipeline == null)
            {
                var shaderModule = compositor.PipelineCache.GetOrCreateShader("HatchShader", HatchShaderCode, "Hatch WGSL Shader");
                
                Span<VertexAttribute> attrs = stackalloc VertexAttribute[8];
                attrs[0] = new VertexAttribute { Format = VertexFormat.Float32x2, Offset = 0, ShaderLocation = 0 }; // Position
                attrs[1] = new VertexAttribute { Format = VertexFormat.Float32x4, Offset = 8, ShaderLocation = 1 }; // Color
                attrs[2] = new VertexAttribute { Format = VertexFormat.Float32x2, Offset = 24, ShaderLocation = 2 }; // TexCoord
                attrs[3] = new VertexAttribute { Format = VertexFormat.Float32, Offset = 32, ShaderLocation = 3 };   // BrushIndex
                attrs[4] = new VertexAttribute { Format = VertexFormat.Float32x2, Offset = 36, ShaderLocation = 4 }; // ShapeSize
                attrs[5] = new VertexAttribute { Format = VertexFormat.Float32, Offset = 44, ShaderLocation = 5 };   // CornerRadius
                attrs[6] = new VertexAttribute { Format = VertexFormat.Float32, Offset = 48, ShaderLocation = 6 };   // StrokeThickness
                attrs[7] = new VertexAttribute { Format = VertexFormat.Float32, Offset = 52, ShaderLocation = 7 };   // ShapeType

                Span<VertexBufferLayout> layouts = stackalloc VertexBufferLayout[1];
                fixed (VertexAttribute* attrsPtr = attrs)
                {
                    layouts[0] = new VertexBufferLayout
                    {
                        ArrayStride = (uint)Unsafe.SizeOf<VectorVertex>(),
                        StepMode = VertexStepMode.Vertex,
                        AttributeCount = 8,
                        Attributes = attrsPtr
                    };

                    var pipeline = compositor.PipelineCache.GetOrCreateRenderPipeline(
                        isOffscreen ? "HatchPipeline_Offscreen" : "HatchPipeline",
                        shaderModule,
                        layouts,
                        topology: PrimitiveTopology.TriangleList,
                        targetFormat: compositor.RenderFormat,
                        sampleCount: isOffscreen ? 1u : 4u
                    );

                    if (isOffscreen)
                    {
                        _cachedPipelineOffscreen = pipeline;
                        activePipeline = _cachedPipelineOffscreen;
                    }
                    else
                    {
                        _cachedPipeline = pipeline;
                        activePipeline = _cachedPipeline;
                    }
                }
            }

            GpuBuffer vertexBuffer;
            GpuBuffer indexBuffer;
            GpuBuffer uniformBuffer;
            GpuBuffer brushesBuf;
            GpuBuffer gradientStopsBuf;
            GpuBuffer hatchRecordsBuf;
            GpuBuffer hatchSegmentsBuf;

            if (dc.StaticBuffer is DxfStaticBuffer sb && sb.GetExtensionState(3) is HatchStaticState staticState)
            {
                vertexBuffer = sb.VertexBuffer!;
                indexBuffer = sb.IndexBuffer!;
                uniformBuffer = sb.UniformBuffer!;
                brushesBuf = sb.BrushesBuffer!;
                gradientStopsBuf = sb.GradientStopsBuffer!;
                hatchRecordsBuf = staticState.RecordsBuffer;
                hatchSegmentsBuf = staticState.SegmentsBuffer;
            }
            else
            {
                vertexBuffer = compositor.VectorVertexBuffer;
                indexBuffer = compositor.VectorIndexBuffer;
                uniformBuffer = compositor.VectorUniformBuffer;
                brushesBuf = compositor.BrushesStorageBuffer;
                gradientStopsBuf = compositor.GradientStopsStorageBuffer;

                EnsureDynamicBuffers(compositor.Context);

                if (_dynamicRecords.Count > 0)
                {
                    _dynamicRecordsBuffer!.Write(CollectionsMarshal.AsSpan(_dynamicRecords));
                }
                else
                {
                    var dummy = new GpuHatchRecord();
                    _dynamicRecordsBuffer!.WriteSingle(dummy);
                }

                if (_dynamicSegments.Count > 0)
                {
                    _dynamicSegmentsBuffer!.Write(CollectionsMarshal.AsSpan(_dynamicSegments));
                }
                else
                {
                    var dummy = new GpuHatchSegment();
                    _dynamicSegmentsBuffer!.WriteSingle(dummy);
                }

                hatchRecordsBuf = _dynamicRecordsBuffer!;
                hatchSegmentsBuf = _dynamicSegmentsBuffer!;
            }

            int currentGen = HashCode.Combine(hatchRecordsBuf.GetHashCode(), brushesBuf.GetHashCode(), gradientStopsBuf.GetHashCode());
            var activeBg = isOffscreen ? _cachedBindGroupOffscreen : _cachedBindGroup;
            if (activeBg == null || currentGen != _cachedRecordGen)
            {
                _cachedRecordGen = currentGen;

                var bgEntries = stackalloc BindGroupEntry[5];
                bgEntries[0] = new BindGroupEntry
                {
                    Binding = 0,
                    Buffer = uniformBuffer.BufferPtr,
                    Offset = 0,
                    Size = 192
                };
                bgEntries[1] = new BindGroupEntry
                {
                    Binding = 1,
                    Buffer = brushesBuf.BufferPtr,
                    Offset = 0,
                    Size = brushesBuf.Size
                };
                bgEntries[2] = new BindGroupEntry
                {
                    Binding = 2,
                    Buffer = hatchRecordsBuf.BufferPtr,
                    Offset = 0,
                    Size = hatchRecordsBuf.Size
                };
                bgEntries[3] = new BindGroupEntry
                {
                    Binding = 3,
                    Buffer = hatchSegmentsBuf.BufferPtr,
                    Offset = 0,
                    Size = hatchSegmentsBuf.Size
                };
                bgEntries[4] = new BindGroupEntry
                {
                    Binding = 4,
                    Buffer = gradientStopsBuf.BufferPtr,
                    Offset = 0,
                    Size = gradientStopsBuf.Size
                };

                var pipelineLayout = wgpu.RenderPipelineGetBindGroupLayout(activePipeline, 0);

                var bgDesc = new BindGroupDescriptor
                {
                    Layout = pipelineLayout,
                    EntryCount = 5,
                    Entries = bgEntries,
                    Label = (byte*)SilkMarshal.StringToPtr("Hatch BindGroup")
                };

                if (isOffscreen)
                {
                    if (_cachedBindGroupOffscreen != null) wgpu.BindGroupRelease(_cachedBindGroupOffscreen);
                    _cachedBindGroupOffscreen = wgpu.DeviceCreateBindGroup(device, &bgDesc);
                    activeBg = _cachedBindGroupOffscreen;
                }
                else
                {
                    if (_cachedBindGroup != null) wgpu.BindGroupRelease(_cachedBindGroup);
                    _cachedBindGroup = wgpu.DeviceCreateBindGroup(device, &bgDesc);
                    activeBg = _cachedBindGroup;
                }
                SilkMarshal.Free((nint)bgDesc.Label);
            }

            wgpu.RenderPassEncoderSetVertexBuffer(pass, 0, vertexBuffer.BufferPtr, 0, vertexBuffer.Size);
            wgpu.RenderPassEncoderSetIndexBuffer(pass, indexBuffer.BufferPtr, IndexFormat.Uint32, 0, indexBuffer.Size);

            wgpu.RenderPassEncoderSetBindGroup(pass, 0, activeBg, 0, null);
            wgpu.RenderPassEncoderSetPipeline(pass, activePipeline);
            wgpu.RenderPassEncoderDrawIndexed(pass, (uint)dc.PointBufferCount, 1, (uint)dc.PointBufferOffset, 0, 0);
        }
    }
}
