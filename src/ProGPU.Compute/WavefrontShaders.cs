namespace ProGPU.Compute;

public static class WavefrontShaders
{
    public const string ShadersSource = @"
struct BvhNode {
    min_bounds: vec2<f32>,
    max_bounds: vec2<f32>,
    left_child_or_first_line: u32,
    primitive_count: u32,
    right_child: u32,
    pad1: u32,
};

struct ShapeInstance {
    transform: mat4x4<f32>,
    inv_transform: mat4x4<f32>,
    min_bounds: vec2<f32>,
    max_bounds: vec2<f32>,
    bvh_root_idx: u32,
    shape_id: u32,
    color: vec4<f32>,
    is_text: u32,
    pad0: u32,
    pad1: u32,
    pad2: u32,
};

struct GridCell {
    shape_start_offset: u32,
    shape_count: u32,
};

struct Uniforms {
    screenWidth: u32,
    screenHeight: u32,
    gridStride: u32,
    instanceCount: u32,
    maxQueueSize: u32,
    currentFrameIndex: u32,
    fontWeightOffset: f32,
    dpiScale: f32,
    curveCount: u32,
    pad0: u32,
    pad1: u32,
    pad2: u32,
};

struct LineSegment {
    start: vec2<f32>,
    end: vec2<f32>,
};

struct BezierCurve {
    p0: vec2<f32>,
    p1: vec2<f32>,
    p2: vec2<f32>,
    p3: vec2<f32>,
    curve_type: u32,
    subdivisions: u32,
    line_offset: u32,
    pad: u32,
};

@group(0) @binding(0) var<uniform> uniforms: Uniforms;
@group(0) @binding(3) var<storage, read> bvh_nodes: array<BvhNode>;
@group(0) @binding(4) var<storage, read> shape_instances: array<ShapeInstance>;
@group(0) @binding(5) var<storage, read_write> grid_cells: array<GridCell>;
@group(0) @binding(6) var<storage, read_write> cell_shape_indices: array<u32>;
@group(0) @binding(8) var screen_texture: texture_2d<f32>;
@group(0) @binding(9) var<storage, read_write> output_lines: array<LineSegment>;
@group(0) @binding(11) var screen_texture_write: texture_storage_2d<bgra8unorm, write>;
@group(0) @binding(12) var<storage, read> raw_curves: array<BezierCurve>;


fn evaluate_curve(curve: BezierCurve, t: f32) -> vec2<f32> {
    if (curve.curve_type == 0u) {
        return (1.0 - t) * curve.p0 + t * curve.p1;
    } else if (curve.curve_type == 1u) {
        let oneMinusT = 1.0 - t;
        return oneMinusT * oneMinusT * curve.p0 + 2.0 * oneMinusT * t * curve.p1 + t * t * curve.p2;
    } else {
        let oneMinusT = 1.0 - t;
        return oneMinusT * oneMinusT * oneMinusT * curve.p0 
             + 3.0 * oneMinusT * oneMinusT * t * curve.p1 
             + 3.0 * oneMinusT * t * t * curve.p2 
             + t * t * t * curve.p3;
    }
}

@compute @workgroup_size(64)
fn flatten_curves(@builtin(global_invocation_id) global_id: vec3<u32>) {
    let curve_idx = global_id.x;
    if (curve_idx >= uniforms.curveCount) {
        return;
    }
    
    let curve = raw_curves[curve_idx];
    let count = curve.subdivisions;
    let start_offset = curve.line_offset;
    
    for (var i = 0u; i < count; i = i + 1u) {
        let t0 = f32(i) / f32(count);
        let t1 = f32(i + 1u) / f32(count);
        
        let p0 = evaluate_curve(curve, t0);
        let p1 = evaluate_curve(curve, t1);
        
        output_lines[start_offset + i] = LineSegment(p0, p1);
    }
}


fn ray_intersects_aabb(p: vec2<f32>, min_b: vec2<f32>, max_b: vec2<f32>) -> bool {
    return p.y >= min_b.y && p.y <= max_b.y && p.x <= max_b.x;
}

fn point_in_aabb(p: vec2<f32>, min_b: vec2<f32>, max_b: vec2<f32>) -> bool {
    return p.x >= min_b.x && p.x <= max_b.x && p.y >= min_b.y && p.y <= max_b.y;
}

fn bvh_node_overlap(p: vec2<f32>, min_b: vec2<f32>, max_b: vec2<f32>, radius: f32) -> bool {
    let ray_overlap = p.y >= min_b.y && p.y <= max_b.y && p.x <= max_b.x;
    if (ray_overlap) {
        return true;
    }
    let dx = max(0.0, max(min_b.x - p.x, p.x - max_b.x));
    let dy = max(0.0, max(min_b.y - p.y, p.y - max_b.y));
    return (dx * dx + dy * dy) <= (radius * radius);
}

fn check_line_intersection(pixel_pos: vec2<f32>, line: LineSegment) -> i32 {
    let A = line.start;
    let B = line.end;
    let deriv_y = B.y - A.y;

    if (deriv_y == 0.0) {
        return 0;
    }

    let spans_y = (A.y <= pixel_pos.y && B.y > pixel_pos.y) || 
                  (B.y <= pixel_pos.y && A.y > pixel_pos.y);

    if (!spans_y) {
        return 0;
    }

    let t = (pixel_pos.y - A.y) / deriv_y;
    var is_valid = false;
    if (deriv_y > 0.0) {
        is_valid = (t >= 0.0 && t < 1.0);
    } else {
        is_valid = (t > 0.0 && t <= 1.0);
    }

    if (is_valid) {
        let intersect_x = A.x + t * (B.x - A.x);
        if (pixel_pos.x < intersect_x) {
            if (deriv_y > 0.0) {
                return 1;
            } else {
                return -1;
            }
        }
    }

    return 0;
}

@compute @workgroup_size(16, 16)
fn bin_shapes(@builtin(global_invocation_id) global_id: vec3<u32>) {
    let cell_x = global_id.x;
    let cell_y = global_id.y;
    
    let grid_stride = uniforms.gridStride;
    let grid_rows = (uniforms.screenHeight + 15u) / 16u;
    if (cell_x >= grid_stride || cell_y >= grid_rows) {
        return;
    }
    
    let cell_idx = cell_y * grid_stride + cell_x;
    let cell_min = vec2<f32>(f32(cell_x * 16u), f32(cell_y * 16u));
    let cell_max = cell_min + vec2<f32>(16.0);
    
    var count = 0u;
    let max_cell_instances = 64u; // Max shapes per grid cell
    
    for (var i = 0u; i < uniforms.instanceCount; i = i + 1u) {
        let inst = shape_instances[i];
        
        var corners = array<vec2<f32>, 4>(
            (inst.transform * vec4<f32>(inst.min_bounds, 0.0, 1.0)).xy * uniforms.dpiScale,
            (inst.transform * vec4<f32>(inst.max_bounds.x, inst.min_bounds.y, 0.0, 1.0)).xy * uniforms.dpiScale,
            (inst.transform * vec4<f32>(inst.min_bounds.x, inst.max_bounds.y, 0.0, 1.0)).xy * uniforms.dpiScale,
            (inst.transform * vec4<f32>(inst.max_bounds, 0.0, 1.0)).xy * uniforms.dpiScale
        );
        
        var shape_min = vec2<f32>(99999.0);
        var shape_max = vec2<f32>(-99999.0);
        for (var c = 0u; c < 4u; c = c + 1u) {
            shape_min.x = min(shape_min.x, corners[c].x);
            shape_min.y = min(shape_min.y, corners[c].y);
            shape_max.x = max(shape_max.x, corners[c].x);
            shape_max.y = max(shape_max.y, corners[c].y);
        }
        
        let overlap = shape_min.x <= cell_max.x && shape_max.x >= cell_min.x &&
                      shape_min.y <= cell_max.y && shape_max.y >= cell_min.y;
                      
        if (overlap) {
            let write_offset = cell_idx * max_cell_instances + count;
            if (count < max_cell_instances) {
                cell_shape_indices[write_offset] = i;
                count = count + 1u;
            }
        }
    }
    
    grid_cells[cell_idx].shape_start_offset = cell_idx * max_cell_instances;
    grid_cells[cell_idx].shape_count = count;
}

@compute @workgroup_size(16, 16)
fn wavefront_render(@builtin(global_invocation_id) global_id: vec3<u32>) {
    let pixel_coord = global_id.xy;
    if (pixel_coord.x >= uniforms.screenWidth || pixel_coord.y >= uniforms.screenHeight) {
        return;
    }

    var current_color = textureLoad(screen_texture, vec2<i32>(pixel_coord), 0);

    let cell_idx = (pixel_coord.y / 16u) * uniforms.gridStride + (pixel_coord.x / 16u);
    let cell = grid_cells[cell_idx];

    for (var i = 0u; i < cell.shape_count; i = i + 1u) {
        let instance_idx = cell_shape_indices[cell.shape_start_offset + i];
        let instance = shape_instances[instance_idx];

        let logical_pos = (vec2<f32>(pixel_coord) + vec2<f32>(0.5)) / uniforms.dpiScale;
        let local_pos_3d = instance.inv_transform * vec4<f32>(logical_pos, 0.0, 1.0);
        let local_pos = local_pos_3d.xy;

        if (point_in_aabb(local_pos, instance.min_bounds, instance.max_bounds)) {
            var winding = 0;
            var min_dist = 99999.0;

            var stack: array<u32, 16>;
            var stack_ptr = 0u;
            var current_node = instance.bvh_root_idx;

            while (true) {
                let node = bvh_nodes[current_node];
                if (bvh_node_overlap(local_pos, node.min_bounds, node.max_bounds, min_dist)) {
                    if (node.primitive_count > 0u) {
                        let start_line = node.left_child_or_first_line;
                        let end_line = start_line + node.primitive_count;
                        for (var line_idx = start_line; line_idx < end_line; line_idx = line_idx + 1u) {
                            let line = output_lines[line_idx];
                            winding += check_line_intersection(local_pos, line);

                            let ab = line.end - line.start;
                            let ap = local_pos - line.start;
                            let t = clamp(dot(ap, ab) / dot(ab, ab), 0.0, 1.0);
                            let closest_point = line.start + t * ab;
                            let d = distance(local_pos, closest_point);
                            if (d < min_dist) {
                                min_dist = d;
                            }
                        }
                    } else {
                        if (stack_ptr < 16u) {
                            stack[stack_ptr] = node.right_child;
                            stack_ptr = stack_ptr + 1u;
                        }
                        current_node = node.left_child_or_first_line;
                        continue;
                    }
                }

                if (stack_ptr == 0u) {
                    break;
                }
                stack_ptr = stack_ptr - 1u;
                current_node = stack[stack_ptr];
            }

            var sd = min_dist;
            if (winding != 0) {
                sd = -min_dist;
            }

            let scale_factor = length(instance.transform[0].xy);
            let screen_dist = sd * scale_factor;
            let physical_dist = screen_dist * uniforms.dpiScale;

            let adjusted_dist = physical_dist - uniforms.fontWeightOffset;
            let coverage = 1.0 - smoothstep(-0.5, 0.5, adjusted_dist);

            if (coverage > 0.0) {
                let text_color = instance.color;
                let bg_color = current_color;

                let linear_text = pow(text_color.rgb, vec3<f32>(2.2));
                let linear_bg = pow(bg_color.rgb, vec3<f32>(2.2));

                let linear_blend = mix(linear_bg, linear_text, coverage);
                let srgb_output = pow(linear_blend, vec3<f32>(1.0 / 2.2));

                let blended_alpha = bg_color.a + coverage * (1.0 - bg_color.a);
                current_color = vec4<f32>(srgb_output, blended_alpha);
            }
        }
    }

    textureStore(screen_texture_write, vec2<i32>(pixel_coord), current_color);
}
";
}
