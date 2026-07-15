namespace ProGPU.Compute;

public static class WavefrontShaders
{
    public const string ShadersSource = @"
struct RayState {
    pixel_coord: vec2<u32>,
    leaf_node_id: u32,
    accumulated_color: vec4<f32>,
    accumulated_alpha: f32,
};

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
    pad: u32,
};

struct LineSegment {
    start: vec2<f32>,
    end: vec2<f32>,
};

@group(0) @binding(0) var<uniform> uniforms: Uniforms;
@group(0) @binding(1) var<storage, read_write> ray_queue: array<RayState>;
@group(0) @binding(2) var<atomic, read_write> queue_counter: u32;
@group(0) @binding(3) var<storage, read> bvh_nodes: array<BvhNode>;
@group(0) @binding(4) var<storage, read> shape_instances: array<ShapeInstance>;
@group(0) @binding(5) var<storage, read_write> grid_cells: array<GridCell>;
@group(0) @binding(6) var<storage, read_write> cell_shape_indices: array<u32>;
@group(0) @binding(7) var mask_texture: texture_2d<u32>;
@group(0) @binding(8) var screen_texture: texture_2d<f32>;

// Group bindings for Pass 3
@group(0) @binding(9) var<storage, read> output_lines: array<LineSegment>;
@group(0) @binding(10) var mask_texture_write: texture_storage_2d<r32u, write>;
@group(0) @binding(11) var screen_texture_write: texture_storage_2d<rgba8unorm, write>;

// Binding for sorting parameters
struct SortParams {
    stage: u32,
    step: u32,
};
@group(0) @binding(12) var<uniform> sort_params: SortParams;

// Workgroup shared memory for aggregation (Pass 1)
var<workgroup> local_counter: atomic<u32>;
var<workgroup> local_offset: u32;
var<workgroup> local_slots: array<RayState, 64>;

fn point_in_aabb(p: vec2<f32>, min_b: vec2<f32>, max_b: vec2<f32>) -> bool {
    return p.x >= min_b.x && p.x <= max_b.x && p.y >= min_b.y && p.y <= max_b.y;
}

fn decode_morton_2d(linear_index: u32) -> vec2<u32> {
    var x = 0u;
    var y = 0u;

    x = (linear_index & 0x01u) | 
        ((linear_index & 0x04u) >> 1u) | 
        ((linear_index & 0x10u) >> 2u) | 
        ((linear_index & 0x40u) >> 3u);

    y = ((linear_index & 0x02u) >> 1u) | 
        ((linear_index & 0x08u) >> 2u) | 
        ((linear_index & 0x20u) >> 3u) | 
        ((linear_index & 0x80u) >> 4u);

    return vec2<u32>(x, y);
}

@compute @workgroup_size(256, 1, 1)
fn init_queue(@builtin(global_invocation_id) global_id: vec3<u32>) {
    if (global_id.x < uniforms.maxQueueSize) {
        ray_queue[global_id.x].leaf_node_id = 0xffffffffu;
        ray_queue[global_id.x].pixel_coord = vec2<u32>(0xffffffffu, 0xffffffffu);
        ray_queue[global_id.x].accumulated_color = vec4<f32>(0.0);
        ray_queue[global_id.x].accumulated_alpha = 0.0;
    }
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
    let max_cell_instances = 64u; // Max shapes per grid cell (pre-allocated layout offset)
    
    for (var i = 0u; i < uniforms.instanceCount; i = i + 1u) {
        let inst = shape_instances[i];
        
        let corners = array<vec2<f32>, 4>(
            (inst.transform * vec4<f32>(inst.min_bounds, 0.0, 1.0)).xy,
            (inst.transform * vec4<f32>(inst.max_bounds.x, inst.min_bounds.y, 0.0, 1.0)).xy,
            (inst.transform * vec4<f32>(inst.min_bounds.x, inst.max_bounds.y, 0.0, 1.0)).xy,
            (inst.transform * vec4<f32>(inst.max_bounds, 0.0, 1.0)).xy
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

@compute @workgroup_size(64, 1, 1)
fn wavefront_traverse(
    @builtin(workgroup_id) workgroup_id: vec3<u32>,
    @builtin(local_invocation_index) local_idx: u32
) {
    if (local_idx == 0u) {
        atomicStore(&local_counter, 0u);
    }
    workgroupBarrier();

    let block_offset = decode_morton_2d(local_idx);
    let pixel_coord = (workgroup_id.xy * 8u) + block_offset;

    if (pixel_coord.x >= uniforms.screenWidth || pixel_coord.y >= uniforms.screenHeight) {
        return;
    }

    let is_active = textureLoad(mask_texture, vec2<i32>(pixel_coord), 0).r;
    if (is_active == 0u) {
        return;
    }

    var current_color = textureLoad(screen_texture, vec2<i32>(pixel_coord), 0);
    var current_alpha = current_color.a;

    let cell_idx = (pixel_coord.y / 16u) * uniforms.gridStride + (pixel_coord.x / 16u);
    let cell = grid_cells[cell_idx];

    for (var i = 0u; i < cell.shape_count; i = i + 1u) {
        let instance_idx = cell_shape_indices[cell.shape_start_offset + i];
        let instance = shape_instances[instance_idx];

        let local_pos_3d = instance.inv_transform * vec4<f32>(vec2<f32>(pixel_coord) + vec2<f32>(0.5), 0.0, 1.0);
        let local_pos = local_pos_3d.xy;

        if (point_in_aabb(local_pos, instance.min_bounds, instance.max_bounds)) {
            var stack: array<u32, 16>;
            var stack_ptr = 0u;
            var current_node = instance.bvh_root_idx;

            while (true) {
                let node = bvh_nodes[current_node];
                if (point_in_aabb(local_pos, node.min_bounds, node.max_bounds)) {
                    if (node.primitive_count > 0u) {
                        let slot = atomicAdd(&local_counter, 1u);
                        if (slot < 64u) {
                            local_slots[slot] = RayState(
                                pixel_coord,
                                current_node,
                                instance.color,
                                current_alpha
                            );
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
        }
    }

    workgroupBarrier();

    if (local_idx == 0u) {
        let count = min(atomicLoad(&local_counter), 64u);
        if (count > 0u) {
            local_offset = atomicAdd(&queue_counter, count);
        }
    }
    workgroupBarrier();

    let local_count = min(atomicLoad(&local_counter), 64u);
    if (local_idx < local_count) {
        let global_slot = local_offset + local_idx;
        if (global_slot < uniforms.maxQueueSize) {
            ray_queue[global_slot] = local_slots[local_idx];
        }
    }
}

@compute @workgroup_size(256, 1, 1)
fn wavefront_sort(@builtin(global_invocation_id) global_id: vec3<u32>) {
    let i = global_id.x;
    let stage = sort_params.stage;
    let step = sort_params.step;

    let ixj = i ^ step;
    if (ixj > i && ixj < uniforms.maxQueueSize) {
        if ((i & stage) == 0u) {
            if (ray_queue[i].leaf_node_id > ray_queue[ixj].leaf_node_id) {
                let temp = ray_queue[i];
                ray_queue[i] = ray_queue[ixj];
                ray_queue[ixj] = temp;
            }
        } else {
            if (ray_queue[i].leaf_node_id < ray_queue[ixj].leaf_node_id) {
                let temp = ray_queue[i];
                ray_queue[i] = ray_queue[ixj];
                ray_queue[ixj] = temp;
            }
        }
    }
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

@compute @workgroup_size(256, 1, 1)
fn wavefront_intersect(@builtin(global_invocation_id) global_id: vec3<u32>) {
    if (global_id.x >= uniforms.maxQueueSize) {
        return;
    }

    let ray = ray_queue[global_id.x];
    if (ray.leaf_node_id == 0xffffffffu) {
        return;
    }

    let node = bvh_nodes[ray.leaf_node_id];
    let start_line = node.left_child_or_first_line;
    let end_line = start_line + node.primitive_count;
    let pixel_pos = vec2<f32>(ray.pixel_coord) + vec2<f32>(0.5);

    var winding = 0;
    var min_dist = 99999.0;

    for (var i = start_line; i < end_line; i = i + 1u) {
        let line = output_lines[i];
        winding += check_line_intersection(pixel_pos, line);

        let ab = line.end - line.start;
        let ap = pixel_pos - line.start;
        let t = clamp(dot(ap, ab) / dot(ab, ab), 0.0, 1.0);
        let closest_point = line.start + t * ab;
        let d = distance(pixel_pos, closest_point);
        if (d < min_dist) {
            min_dist = d;
        }
    }

    var sd = min_dist;
    if (winding != 0) {
        sd = -min_dist;
    }

    let adjusted_dist = sd - uniforms.fontWeightOffset;
    let coverage = 1.0 - smoothstep(-0.5, 0.5, adjusted_dist);

    if (coverage > 0.0) {
        // Linear blending
        let text_color = ray.accumulated_color;
        let bg_color = textureLoad(screen_texture, vec2<i32>(ray.pixel_coord), 0);

        let linear_text = pow(text_color.rgb, vec3<f32>(2.2));
        let linear_bg = pow(bg_color.rgb, vec3<f32>(2.2));

        let linear_blend = mix(linear_bg, linear_text, coverage);
        let srgb_output = pow(linear_blend, vec3<f32>(1.0 / 2.2));

        let blended_alpha = bg_color.a + coverage * (1.0 - bg_color.a);
        
        textureStore(screen_texture_write, vec2<i32>(ray.pixel_coord), vec4<f32>(srgb_output, blended_alpha));
    }

    // Mark pixel completed
    textureStore(mask_texture_write, vec2<i32>(ray.pixel_coord), vec4<u32>(0u, 0u, 0u, 0u));
}

@compute @workgroup_size(16, 16)
fn clear_mask(@builtin(global_invocation_id) global_id: vec3<u32>) {
    if (global_id.x < uniforms.screenWidth && global_id.y < uniforms.screenHeight) {
        textureStore(mask_texture_write, vec2<i32>(global_id.xy), vec4<u32>(1u, 0u, 0u, 0u));
    }
}
";
}
