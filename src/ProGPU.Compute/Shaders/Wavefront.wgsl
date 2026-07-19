// Algorithm: Retain flattened vector curves and transform-indexed shape instances, patch stable spatial transforms independently, build deterministic 16x16-cell coverage bitmaps instance-first, count/scan/scatter exact painter-ordered cell lists, compact non-empty cells stably, conservatively classify solid/outside cell-shape pairs, and indirectly dispatch only active cells to a sparse output texture.
// Time complexity: O(dC*S) for newly appended curves (O(C*S) only after an arena grow replay), O(dT) CPU upload for changed retained transforms, O(O + G*ceil(I/32) + G + O*log L) for sparse binning/active-cell compaction/coarse classification, and O(Pa*Ke*log L + Pa*Ks) for the fine path, where dC is new curves, C retained curves, S is the CPU-selected adaptive subdivision count bounded by 256, dT changed transforms, O instance/cell overlaps, G cells, I instances, Pa pixels in active cells, Ke edge candidates, Ks solid candidates, and L primitives per shape.
// Space complexity: O(C*S + I + T + G*ceil(I/32) + O + G + W*H), for retained geometry/transform/bin arenas and one sparse ping-pong texture; there is no fixed per-cell overlap cap and no full-window texture copy.
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
    transform_index: u32,
    transform_pad: u32,
    color: vec4<f32>,
    is_text: u32,
    pad0: u32,
    pad1: u32,
    pad2: u32,
};

struct ShapeTransform {
    transform: mat4x4<f32>,
    inv_transform: mat4x4<f32>,
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
    coverageWordCount: u32,
    wordsPerCell: u32,
    cellCount: u32,
    pairCount: u32,
    curveStart: u32,
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

struct DispatchIndirectArgs {
    x: u32,
    y: u32,
    z: u32,
};

struct DrawIndirectArgs {
    vertex_count: u32,
    instance_count: u32,
    first_vertex: u32,
    first_instance: u32,
};

@group(0) @binding(0) var<uniform> uniforms: Uniforms;
@group(0) @binding(3) var<storage, read> bvh_nodes: array<BvhNode>;
@group(0) @binding(4) var<storage, read> shape_instances: array<ShapeInstance>;
@group(0) @binding(5) var<storage, read_write> grid_cells: array<GridCell>;
@group(0) @binding(6) var<storage, read_write> cell_shape_indices: array<u32>;
@group(0) @binding(7) var<storage, read_write> cell_coverage_words: array<atomic<u32>>;
@group(0) @binding(8) var screen_texture: texture_2d<f32>;
@group(0) @binding(9) var<storage, read_write> output_lines: array<LineSegment>;
@group(0) @binding(11) var screen_texture_write: texture_storage_2d<bgra8unorm, write>;
@group(0) @binding(12) var<storage, read> raw_curves: array<BezierCurve>;
@group(0) @binding(13) var<storage, read_write> bin_word_counts: array<u32>;
@group(0) @binding(14) var<storage, read> bin_word_offsets: array<u32>;
@group(0) @binding(15) var<storage, read_write> active_cell_flags: array<u32>;
@group(0) @binding(16) var<storage, read> active_cell_offsets: array<u32>;
@group(0) @binding(17) var<storage, read_write> active_cell_indices: array<u32>;
@group(0) @binding(18) var<storage, read_write> active_dispatch_args: array<DispatchIndirectArgs>;
@group(0) @binding(19) var<storage, read_write> cell_shape_classes: array<u32>;
@group(0) @binding(20) var<storage, read_write> active_draw_args: array<DrawIndirectArgs>;
@group(0) @binding(21) var<storage, read> shape_transforms: array<ShapeTransform>;


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
    let local_curve_idx = global_id.x;
    if (local_curve_idx >= uniforms.curveCount) {
        return;
    }
    let curve_idx = uniforms.curveStart + local_curve_idx;
    
    let curve = raw_curves[curve_idx];
    let count = curve.subdivisions;
    let start_offset = curve.line_offset;
    
    // CPU preparation proves the max(|B''|) * h^2 / 8 chord-error bound at the
    // quantized upper device scale and rejects curves requiring more than 256 iterations.
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

struct ShapeEvaluation {
    winding: i32,
    min_distance: f32,
};

fn evaluate_shape(local_pos: vec2<f32>, root_node: u32) -> ShapeEvaluation {
    var winding = 0;
    var min_dist = 99999.0;
    var stack: array<u32, 16>;
    var stack_ptr = 0u;
    var current_node = root_node;

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
                    min_dist = min(min_dist, distance(local_pos, closest_point));
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

    return ShapeEvaluation(winding, min_dist);
}

fn minimum_device_scale(transform: mat4x4<f32>) -> f32 {
    let x_basis = transform[0].xy;
    let y_basis = transform[1].xy;
    let a = dot(x_basis, x_basis);
    let b = dot(x_basis, y_basis);
    let d = dot(y_basis, y_basis);
    let discriminant = sqrt(max(0.0, (a - d) * (a - d) + 4.0 * b * b));
    return sqrt(max(0.0, 0.5 * (a + d - discriminant)));
}

fn instance_transform(inst: ShapeInstance) -> mat4x4<f32> {
    // C# composes row-vector transforms as local * retained. The uploaded matrices are consumed
    // as WGSL column-vector matrices, so the equivalent order is retained * local here.
    return shape_transforms[inst.transform_index].transform * inst.transform;
}

fn instance_inverse_transform(inst: ShapeInstance) -> mat4x4<f32> {
    return inst.inv_transform * shape_transforms[inst.transform_index].inv_transform;
}

// Fixed workgroup size: 256. One invocation clears one 32-instance coverage word.
// Bandwidth: one 32-bit store per coverage word; no scene reads.
@compute @workgroup_size(256)
fn clear_bin_words(@builtin(global_invocation_id) global_id: vec3<u32>) {
    let word_idx = global_id.x;
    if (word_idx >= uniforms.coverageWordCount) {
        return;
    }
    atomicStore(&cell_coverage_words[word_idx], 0u);
}

fn transformed_device_bounds(inst: ShapeInstance) -> vec4<f32> {
    let transform = instance_transform(inst);
    let p0 = (transform * vec4<f32>(inst.min_bounds, 0.0, 1.0)).xy * uniforms.dpiScale;
    let p1 = (transform * vec4<f32>(inst.max_bounds.x, inst.min_bounds.y, 0.0, 1.0)).xy * uniforms.dpiScale;
    let p2 = (transform * vec4<f32>(inst.min_bounds.x, inst.max_bounds.y, 0.0, 1.0)).xy * uniforms.dpiScale;
    let p3 = (transform * vec4<f32>(inst.max_bounds, 0.0, 1.0)).xy * uniforms.dpiScale;
    let bounds_min = min(min(p0, p1), min(p2, p3));
    let bounds_max = max(max(p0, p1), max(p2, p3));
    return vec4<f32>(bounds_min, bounds_max);
}

// Fixed workgroup size: 64. One invocation transforms one instance and atomically sets its
// painter-order bit in every covered cell. Work is O(I + O), not O(G*I). Atomic OR is used only
// to build a set: the later word/bit traversal defines deterministic painter order.
@compute @workgroup_size(64)
fn build_bin_coverage(@builtin(global_invocation_id) global_id: vec3<u32>) {
    let instance_idx = global_id.x;
    if (instance_idx >= uniforms.instanceCount) {
        return;
    }

    let bounds = transformed_device_bounds(shape_instances[instance_idx]);
    let screen_max = vec2<f32>(f32(uniforms.screenWidth), f32(uniforms.screenHeight));
    if (any(bounds.zw < vec2<f32>(0.0)) || any(bounds.xy > screen_max)) {
        return;
    }

    let grid_rows = (uniforms.screenHeight + 15u) / 16u;
    let clipped_min = clamp(bounds.xy, vec2<f32>(0.0), max(screen_max - vec2<f32>(1.0), vec2<f32>(0.0)));
    let clipped_max = clamp(bounds.zw, vec2<f32>(0.0), max(screen_max - vec2<f32>(1.0), vec2<f32>(0.0)));
    let min_cell = vec2<u32>(clipped_min) / 16u;
    let max_cell = vec2<u32>(clipped_max) / 16u;
    let instance_word = instance_idx / 32u;
    let instance_bit = 1u << (instance_idx & 31u);

    for (var cell_y = min_cell.y; cell_y <= max_cell.y && cell_y < grid_rows; cell_y = cell_y + 1u) {
        for (var cell_x = min_cell.x; cell_x <= max_cell.x && cell_x < uniforms.gridStride; cell_x = cell_x + 1u) {
            let cell_idx = cell_y * uniforms.gridStride + cell_x;
            let word_idx = cell_idx * uniforms.wordsPerCell + instance_word;
            atomicOr(&cell_coverage_words[word_idx], instance_bit);
        }
    }
}

// Fixed workgroup size: 256. Each invocation popcounts one 32-instance word and writes the
// reusable exclusive-scan input. The following scan reserves exact output without a hard cap.
@compute @workgroup_size(256)
fn count_bin_words(@builtin(global_invocation_id) global_id: vec3<u32>) {
    let word_idx = global_id.x;
    if (word_idx >= uniforms.coverageWordCount) {
        return;
    }
    bin_word_counts[word_idx] = countOneBits(atomicLoad(&cell_coverage_words[word_idx]));
}

// Fixed workgroup size: 256. Words are laid out cell-major and instance-word-minor. The scan
// offset plus ascending bit enumeration therefore produces exact, stable painter-order lists.
// Bandwidth is O(W + O): one word/count/offset read and one index write per overlap.
@compute @workgroup_size(256)
fn scatter_bin_words(@builtin(global_invocation_id) global_id: vec3<u32>) {
    let word_idx = global_id.x;
    if (word_idx >= uniforms.coverageWordCount) {
        return;
    }

    var bits = atomicLoad(&cell_coverage_words[word_idx]);
    var output_idx = bin_word_offsets[word_idx];
    let instance_base = (word_idx % uniforms.wordsPerCell) * 32u;
    while (bits != 0u) {
        let bit = firstTrailingBit(bits);
        let instance_idx = instance_base + bit;
        if (instance_idx < uniforms.instanceCount && output_idx < uniforms.pairCount) {
            cell_shape_indices[output_idx] = instance_idx;
        }
        output_idx = output_idx + 1u;
        bits = bits & (bits - 1u);
    }

    if ((word_idx % uniforms.wordsPerCell) == 0u) {
        let cell_idx = word_idx / uniforms.wordsPerCell;
        let cell_start = bin_word_offsets[word_idx];
        var cell_end = uniforms.pairCount;
        if (cell_idx + 1u < uniforms.cellCount) {
            cell_end = bin_word_offsets[word_idx + uniforms.wordsPerCell];
        }
        grid_cells[cell_idx].shape_start_offset = cell_start;
        grid_cells[cell_idx].shape_count = cell_end - cell_start;
    }
}

// Fixed workgroup size: 256. One invocation classifies one cell after exact bin scatter.
// Bandwidth: one GridCell read and one u32 flag write per screen cell.
@compute @workgroup_size(256)
fn mark_active_cells(@builtin(global_invocation_id) global_id: vec3<u32>) {
    let cell_idx = global_id.x;
    if (cell_idx >= uniforms.cellCount) {
        return;
    }
    active_cell_flags[cell_idx] = select(0u, 1u, grid_cells[cell_idx].shape_count != 0u);
}

// Fixed workgroup size: 256. The exclusive scan offset preserves row-major cell order.
// Bandwidth: one flag/offset read and one active-index write per non-empty cell.
@compute @workgroup_size(256)
fn scatter_active_cells(@builtin(global_invocation_id) global_id: vec3<u32>) {
    let cell_idx = global_id.x;
    if (cell_idx >= uniforms.cellCount || active_cell_flags[cell_idx] == 0u) {
        return;
    }
    active_cell_indices[active_cell_offsets[cell_idx]] = cell_idx;
}

// Fixed workgroup size: 1. Derive the exact active count from the final flag/scan pair and
// split it over the WebGPU-guaranteed 65,535 workgroups-per-dimension limit. The count is stored
// in the sentinel slot active_cell_indices[cellCount] for final-row bounds checks.
@compute @workgroup_size(1)
fn finalize_active_dispatch(@builtin(global_invocation_id) global_id: vec3<u32>) {
    if (global_id.x != 0u) {
        return;
    }
    let last_cell = uniforms.cellCount - 1u;
    let active_count = active_cell_offsets[last_cell] + active_cell_flags[last_cell];
    active_cell_indices[uniforms.cellCount] = active_count;
    active_draw_args[0] = DrawIndirectArgs(6u, active_count, 0u, 0u);
    if (active_count == 0u) {
        active_dispatch_args[0] = DispatchIndirectArgs(0u, 1u, 1u);
        return;
    }
    let dispatch_width = min(active_count, 65535u);
    let dispatch_height = ((active_count - 1u) / dispatch_width) + 1u;
    active_dispatch_args[0] = DispatchIndirectArgs(dispatch_width, dispatch_height, 1u);
}

// One 64-lane workgroup classifies the painter-ordered candidates of one active cell. A candidate
// is solid or outside only when its center-to-outline lower bound in device pixels exceeds the
// farthest cell corner plus the half-pixel AA support. All uncertain pairs remain edge work, so the
// optimization cannot lower coverage quality or change painter order.
@compute @workgroup_size(64)
fn classify_cell_shapes(
    @builtin(workgroup_id) workgroup_id: vec3<u32>,
    @builtin(local_invocation_id) local_id: vec3<u32>) {
    let active_count = active_cell_indices[uniforms.cellCount];
    let dispatch_width = min(active_count, 65535u);
    let active_idx = workgroup_id.y * dispatch_width + workgroup_id.x;
    if (active_idx >= active_count) {
        return;
    }

    let cell_idx = active_cell_indices[active_idx];
    let cell_coord = vec2<u32>(cell_idx % uniforms.gridStride, cell_idx / uniforms.gridStride);
    let cell_origin = vec2<f32>(cell_coord * 16u);
    let screen_size = vec2<f32>(f32(uniforms.screenWidth), f32(uniforms.screenHeight));
    let cell_size = min(vec2<f32>(16.0), screen_size - cell_origin);
    let logical_center = (cell_origin + cell_size * 0.5) / uniforms.dpiScale;
    let safe_radius = length(cell_size * 0.5) + 0.5;
    let cell = grid_cells[cell_idx];

    for (var candidate = local_id.x; candidate < cell.shape_count; candidate = candidate + 64u) {
        let pair_idx = cell.shape_start_offset + candidate;
        let instance = shape_instances[cell_shape_indices[pair_idx]];
        let inverse_transform = instance_inverse_transform(instance);
        let transform = instance_transform(instance);
        let local_center = (inverse_transform * vec4<f32>(logical_center, 0.0, 1.0)).xy;
        let evaluation = evaluate_shape(local_center, instance.bvh_root_idx);
        let lower_device_distance = evaluation.min_distance *
            minimum_device_scale(transform) * uniforms.dpiScale;

        var cell_class = 0u; // edge/uncertain
        if (lower_device_distance > safe_radius) {
            cell_class = select(1u, 2u, evaluation.winding != 0); // outside / solid
        }
        cell_shape_classes[pair_idx] = cell_class;
    }
}

@compute @workgroup_size(16, 16)
fn wavefront_render(
    @builtin(workgroup_id) workgroup_id: vec3<u32>,
    @builtin(local_invocation_id) local_id: vec3<u32>) {
    let active_count = active_cell_indices[uniforms.cellCount];
    let dispatch_width = min(active_count, 65535u);
    let active_idx = workgroup_id.y * dispatch_width + workgroup_id.x;
    if (active_idx >= active_count) {
        return;
    }
    let cell_idx = active_cell_indices[active_idx];
    let cell_coord = vec2<u32>(cell_idx % uniforms.gridStride, cell_idx / uniforms.gridStride);
    let pixel_coord = cell_coord * 16u + local_id.xy;
    if (pixel_coord.x >= uniforms.screenWidth || pixel_coord.y >= uniforms.screenHeight) {
        return;
    }

    var current_color = textureLoad(screen_texture, vec2<i32>(pixel_coord), 0);

    let cell = grid_cells[cell_idx];
    let logical_pos = (vec2<f32>(pixel_coord) + vec2<f32>(0.5)) / uniforms.dpiScale;

    for (var i = 0u; i < cell.shape_count; i = i + 1u) {
        let pair_idx = cell.shape_start_offset + i;
        let cell_class = cell_shape_classes[pair_idx];
        if (cell_class == 1u) {
            continue;
        }
        let instance_idx = cell_shape_indices[pair_idx];
        let instance = shape_instances[instance_idx];
        let inverse_transform = instance_inverse_transform(instance);
        let transform = instance_transform(instance);

        let local_pos_3d = inverse_transform * vec4<f32>(logical_pos, 0.0, 1.0);
        let local_pos = local_pos_3d.xy;

        if (cell_class == 2u || point_in_aabb(local_pos, instance.min_bounds, instance.max_bounds)) {
            var coverage = 1.0;
            if (cell_class == 0u) {
                let evaluation = evaluate_shape(local_pos, instance.bvh_root_idx);
                var sd = evaluation.min_distance;
                if (evaluation.winding != 0) {
                    sd = -evaluation.min_distance;
                }

                let scale_factor = length(transform[0].xy);
                let screen_dist = sd * scale_factor;
                let physical_dist = screen_dist * uniforms.dpiScale;

                let adjusted_dist = physical_dist - uniforms.fontWeightOffset;
                coverage = 1.0 - smoothstep(-0.5, 0.5, adjusted_dist);
            }

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
