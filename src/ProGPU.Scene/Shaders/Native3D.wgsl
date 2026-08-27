// Algorithm: Transform retained 3D lines and indexed meshes on the GPU; expand lines in physical screen space and shade mesh fragments with a bounded WPF/MIL light range and derivative wire coverage.
// Time complexity: O(L + I * (K + S)) shader work for L expanded line vertices, I referenced mesh indices, at most K=16 lights per mesh, and S material stops sampled for each covered mesh fragment.
// Space complexity: O(C + E + M + V + I + K + B + S) read-only storage for cameras, edges, meshes, vertices, indices, lights, material brushes, and gradient stops; O(1) private storage and no auxiliary output storage per invocation.
// Lines use six vertices per retained edge. Meshes fetch uint32 indices from
// storage so one pointer-free scene ABI works in native WebGPU and wasm32.

struct Camera3D {
    projection: mat4x4<f32>,
    view: mat4x4<f32>,
    camera_position: vec4<f32>,
    viewport: vec4<f32>,
    viewport_rect: vec4<f32>,
};

fn map_clip_to_viewport(clip: vec4<f32>, camera: Camera3D) -> vec4<f32> {
    let target_size = max(camera.viewport.xy, vec2<f32>(1.0));
    let viewport_size = max(camera.viewport_rect.zw, vec2<f32>(1.0));
    let scale = viewport_size / target_size;
    let center = vec2<f32>(
        ((camera.viewport_rect.x + viewport_size.x * 0.5) /
            target_size.x) * 2.0 - 1.0,
        1.0 - ((camera.viewport_rect.y + viewport_size.y * 0.5) /
            target_size.y) * 2.0);
    return vec4<f32>(
        clip.x * scale.x + clip.w * center.x,
        clip.y * scale.y + clip.w * center.y,
        clip.z,
        clip.w);
}

struct Line3D {
    start: vec4<f32>,
    end: vec4<f32>,
    color: vec4<f32>,
    thickness: f32,
    opacity: f32,
    camera_index: u32,
    flags: u32,
    transform: mat4x4<f32>,
};

struct Mesh3D {
    flags: u32,
    topology: u32,
    render_mode: u32,
    camera_index: u32,
    vertex_offset: u32,
    vertex_count: u32,
    index_offset: u32,
    index_count: u32,
    model_transform: mat4x4<f32>,
    normal_transform: mat4x4<f32>,
    color: vec4<f32>,
    light_direction: vec4<f32>,
    ambient_color: vec4<f32>,
    specular_color: vec4<f32>,
    material_ambient: vec4<f32>,
    opacity: f32,
    shading_mode: u32,
    light_offset: u32,
    light_count: u32,
};

struct Light3D {
    struct_size: u32,
    kind: u32,
    flags: u32,
    reserved: u32,
    color: vec4<f32>,
    position_range: vec4<f32>,
    direction_inner_cos: vec4<f32>,
    attenuation_outer_cos: vec4<f32>,
};

struct MeshVertex3D {
    position: vec4<f32>,
    normal: vec4<f32>,
    texture_coordinate: vec2<f32>,
    reserved: vec2<u32>,
};

struct MaterialBrush3D {
    brush_type: u32,
    opacity: f32,
    gradient_start: vec2<f32>,
    gradient_end: vec2<f32>,
    gradient_center: vec2<f32>,
    gradient_radius: f32,
    stop_count: u32,
    gradient_radius_y: f32,
    spread_method: u32,
    color_interpolation_mode: u32,
    stop_offset: u32,
    stop_colors0: vec4<f32>,
    stop_colors1: vec4<f32>,
    stop_colors2: vec4<f32>,
    stop_colors3: vec4<f32>,
    stop_colors4: vec4<f32>,
    stop_colors5: vec4<f32>,
    stop_colors6: vec4<f32>,
    stop_colors7: vec4<f32>,
    stop_offsets0: vec4<f32>,
    stop_offsets1: vec4<f32>,
    coordinate_transform0: vec4<f32>,
    coordinate_transform1: vec4<f32>,
};

struct MaterialGradientStop3D {
    color: vec4<f32>,
    offset: f32,
};

@group(0) @binding(0) var<storage, read> cameras: array<Camera3D>;
@group(0) @binding(1) var<storage, read> lines: array<Line3D>;
@group(0) @binding(2) var<storage, read> meshes: array<Mesh3D>;
@group(0) @binding(3) var<storage, read> vertices: array<MeshVertex3D>;
@group(0) @binding(4) var<storage, read> indices: array<u32>;
@group(0) @binding(5) var<storage, read> lights: array<Light3D>;
@group(0) @binding(6) var<storage, read> materials: array<MaterialBrush3D>;
@group(0) @binding(7) var<storage, read> material_gradient_stops: array<MaterialGradientStop3D>;

struct LineOutput {
    @builtin(position) position: vec4<f32>,
    @location(0) color: vec4<f32>,
    @location(1) edge_coordinate: f32,
};

@vertex
fn vs_line_3d(
    @builtin(vertex_index) vertex_index: u32,
    @builtin(instance_index) instance_index: u32) -> LineOutput {
    let line = lines[instance_index];
    let camera = cameras[line.camera_index];
    let is_end = vertex_index == 2u || vertex_index == 3u ||
        vertex_index == 5u;
    let is_positive_side = vertex_index == 1u || vertex_index == 4u ||
        vertex_index == 5u;
    let corner = vec2<f32>(
        select(0.0, 1.0, is_end),
        select(-1.0, 1.0, is_positive_side));
    let local = select(line.start, line.end, corner.x > 0.5);
    var start_clip = camera.projection * camera.view * line.transform * line.start;
    var end_clip = camera.projection * camera.view * line.transform * line.end;
    let safe_start_w = select(start_clip.w, 0.000001, abs(start_clip.w) < 0.000001);
    let safe_end_w = select(end_clip.w, 0.000001, abs(end_clip.w) < 0.000001);
    let viewport_size = max(camera.viewport_rect.zw, vec2<f32>(1.0));
    let start_screen = (start_clip.xy / safe_start_w) * viewport_size;
    let end_screen = (end_clip.xy / safe_end_w) * viewport_size;
    let delta = end_screen - start_screen;
    let length = max(length(delta), 0.000001);
    let normal = vec2<f32>(-delta.y, delta.x) / length;
    var clip = camera.projection * camera.view * line.transform * local;
    let expanded_xy = clip.xy +
        normal * corner.y * line.thickness * clip.w / viewport_size;
    clip = vec4<f32>(expanded_xy, clip.zw);
    var output: LineOutput;
    output.position = map_clip_to_viewport(clip, camera);
    output.color = vec4<f32>(line.color.rgb, line.color.a * line.opacity);
    output.edge_coordinate = corner.y;
    return output;
}

@fragment
fn fs_line_3d(input: LineOutput) -> @location(0) vec4<f32> {
    let coverage = 1.0 - smoothstep(0.80, 1.0, abs(input.edge_coordinate));
    return vec4<f32>(input.color.rgb, input.color.a * coverage);
}

struct MeshOutput {
    @builtin(position) position: vec4<f32>,
    @location(0) color: vec4<f32>,
    @location(1) normal: vec3<f32>,
    @location(2) world_position: vec3<f32>,
    @location(3) @interpolate(flat) material: u32,
    @location(4) barycentric: vec3<f32>,
    @location(5) texture_coordinate: vec2<f32>,
};

@vertex
fn vs_mesh_3d(
    @builtin(vertex_index) vertex_index: u32,
    @builtin(instance_index) instance_index: u32) -> MeshOutput {
    let mesh = meshes[instance_index];
    let camera = cameras[mesh.camera_index];
    let source_index = indices[mesh.index_offset + vertex_index];
    let vertex = vertices[mesh.vertex_offset + source_index];
    let world = mesh.model_transform *
        vec4<f32>(vertex.position.xyz, 1.0);
    var output: MeshOutput;
    output.position = map_clip_to_viewport(
        camera.projection * camera.view * world,
        camera);
    output.color = vec4<f32>(mesh.color.rgb, mesh.color.a * mesh.opacity);
    output.normal = normalize((mesh.normal_transform * vec4<f32>(vertex.normal.xyz, 0.0)).xyz);
    output.world_position = world.xyz;
    output.material = instance_index;
    let corner = vertex_index % 3u;
    output.barycentric = vec3<f32>(select(0.0, 1.0, corner == 0u), select(0.0, 1.0, corner == 1u), select(0.0, 1.0, corner == 2u));
    output.texture_coordinate = vertex.texture_coordinate;
    return output;
}

fn transform_material_coordinate(
    brush: MaterialBrush3D,
    coordinate: vec2<f32>
) -> vec2<f32> {
    let point = vec3<f32>(coordinate, 1.0);
    return vec2<f32>(
        dot(point, brush.coordinate_transform0.xyz),
        dot(point, brush.coordinate_transform1.xyz));
}

fn apply_material_spread(value: f32, method: u32) -> f32 {
    if (method == 1u) {
        let period = fract(value * 0.5) * 2.0;
        return select(period, 2.0 - period, period > 1.0);
    }
    if (method == 2u) {
        return fract(value);
    }
    return value;
}

fn material_srgb_to_linear_component(value: f32) -> f32 {
    if (value <= 0.04045) {
        return value / 12.92;
    }
    return pow((value + 0.055) / 1.055, 2.4);
}

fn material_linear_to_srgb_component(value: f32) -> f32 {
    let clamped = max(value, 0.0);
    if (clamped <= 0.0031308) {
        return clamped * 12.92;
    }
    return 1.055 * pow(clamped, 1.0 / 2.4) - 0.055;
}

fn interpolate_material_gradient(
    brush: MaterialBrush3D,
    first: vec4<f32>,
    second: vec4<f32>,
    factor: f32
) -> vec4<f32> {
    if (brush.color_interpolation_mode == 1u) {
        let linear = mix(
            vec3<f32>(
                material_srgb_to_linear_component(first.r),
                material_srgb_to_linear_component(first.g),
                material_srgb_to_linear_component(first.b)),
            vec3<f32>(
                material_srgb_to_linear_component(second.r),
                material_srgb_to_linear_component(second.g),
                material_srgb_to_linear_component(second.b)),
            factor);
        return vec4<f32>(
            material_linear_to_srgb_component(linear.r),
            material_linear_to_srgb_component(linear.g),
            material_linear_to_srgb_component(linear.b),
            mix(first.a, second.a, factor));
    }
    return mix(first, second, factor);
}

fn sample_material_stops(
    brush: MaterialBrush3D,
    value: f32
) -> vec4<f32> {
    var previous = material_gradient_stops[brush.stop_offset];
    if (value < previous.offset) {
        return previous.color;
    }
    for (var index = 1u; index < brush.stop_count; index++) {
        let current = material_gradient_stops[brush.stop_offset + index];
        if (value < current.offset) {
            let factor = clamp(
                (value - previous.offset) /
                    max(current.offset - previous.offset, 0.0001),
                0.0,
                1.0);
            return interpolate_material_gradient(
                brush, previous.color, current.color, factor);
        }
        previous = current;
    }
    return previous.color;
}

fn sample_mesh_material(
    brush: MaterialBrush3D,
    texture_coordinate: vec2<f32>
) -> vec4<f32> {
    if (brush.brush_type == 0u) {
        return vec4<f32>(
            brush.stop_colors0.rgb,
            brush.stop_colors0.a * brush.opacity);
    }
    let coordinate = transform_material_coordinate(
        brush, texture_coordinate);
    var value = 0.0;
    if (brush.brush_type == 1u) {
        let direction = brush.gradient_end - brush.gradient_start;
        let length_squared = dot(direction, direction);
        if (length_squared > 0.0001) {
            value = dot(
                coordinate - brush.gradient_start,
                direction) / length_squared;
        }
    } else {
        let radii = max(
            vec2<f32>(brush.gradient_radius, brush.gradient_radius_y),
            vec2<f32>(0.0001));
        let point = (coordinate - brush.gradient_center) / radii;
        let origin =
            (brush.gradient_start - brush.gradient_center) / radii;
        let direction = point - origin;
        let a = dot(direction, direction);
        if (a > 0.0001) {
            let b = 2.0 * dot(origin, direction);
            let c = dot(origin, origin) - 1.0;
            let discriminant = max(b * b - 4.0 * a * c, 0.0);
            let boundary = (-b + sqrt(discriminant)) / (2.0 * a);
            if (boundary > 0.0001) {
                value = 1.0 / boundary;
            }
        }
    }
    if (brush.spread_method == 3u &&
        (value < 0.0 || value > 1.0)) {
        return vec4<f32>(0.0);
    }
    let color = sample_material_stops(
        brush,
        apply_material_spread(value, brush.spread_method));
    return vec4<f32>(color.rgb, color.a * brush.opacity);
}

@fragment
fn fs_mesh_3d(input: MeshOutput) -> @location(0) vec4<f32> {
    let mesh = meshes[input.material];
    let material_color = input.color * sample_mesh_material(
        materials[input.material], input.texture_coordinate);
    let camera = cameras[mesh.camera_index];
    let n = normalize(input.normal);
    let view = normalize(camera.camera_position.xyz - input.world_position);
    let shininess = max(mesh.specular_color.w, 0.001);
    var diffuse = vec3<f32>(0.0);
    var ambient = vec3<f32>(0.0);
    var specular = vec3<f32>(0.0);
    if (mesh.light_count == 0u) {
        let light = normalize(-mesh.light_direction.xyz);
        let light_intensity = max(mesh.light_direction.w, 0.0);
        let ambient_intensity = max(mesh.ambient_color.w, 0.0);
        let amount = max(dot(n, light), 0.0) * light_intensity;
        let reflected = reflect(-light, n);
        diffuse = vec3<f32>(amount);
        specular = vec3<f32>(pow(
            max(dot(view, reflected), 0.0),
            shininess) * light_intensity);
        ambient = mesh.ambient_color.rgb * ambient_intensity;
    } else {
        for (var light_index = 0u; light_index < 16u; light_index++) {
            if (light_index >= mesh.light_count) {
                break;
            }
            let source = lights[mesh.light_offset + light_index];
            if (source.kind == 0u) {
                ambient += source.color.rgb;
                continue;
            }
            var light = normalize(-source.direction_inner_cos.xyz);
            var attenuation = 1.0;
            if (source.kind >= 2u) {
                let to_light = source.position_range.xyz - input.world_position;
                let distance = length(to_light);
                light = to_light / max(distance, 0.000001);
                let terms = source.attenuation_outer_cos.xyz;
                attenuation = 1.0 / max(
                    terms.x + terms.y * distance +
                        terms.z * distance * distance,
                    1.0);
                attenuation *= select(
                    0.0, 1.0, distance <= source.position_range.w);
                attenuation *= select(
                    0.0, 1.0, distance > 0.000001);
                if (source.kind == 3u) {
                    let rho = max(dot(
                        normalize(-source.direction_inner_cos.xyz),
                        light), 0.0);
                    let outer_cos = source.attenuation_outer_cos.w;
                    let cone_width = max(
                        source.direction_inner_cos.w - outer_cos,
                        0.000001);
                    attenuation *= clamp(
                        (rho - outer_cos) / cone_width,
                        0.0,
                        1.0);
                }
            }
            let amount = max(dot(n, light), 0.0) * attenuation;
            let half_vector = normalize(view + light);
            diffuse += source.color.rgb * amount;
            specular += source.color.rgb * pow(
                max(dot(n, half_vector), 0.0), shininess) * attenuation;
        }
    }
    ambient *= mesh.material_ambient.rgb;
    var rgb = material_color.rgb * (ambient + diffuse) +
        mesh.specular_color.rgb * specular;
    if (mesh.shading_mode == 0u) {
        rgb = material_color.rgb;
    }
    let derivative = fwidth(input.barycentric);
    let edge = 1.0 - min(min(smoothstep(vec3<f32>(0.0), derivative * 1.5, input.barycentric).x,
                              smoothstep(vec3<f32>(0.0), derivative * 1.5, input.barycentric).y),
                         smoothstep(vec3<f32>(0.0), derivative * 1.5, input.barycentric).z);
    if (mesh.render_mode == 1u) {
        return vec4<f32>(material_color.rgb, material_color.a * edge);
    }
    if (mesh.render_mode == 2u) {
        rgb = mix(rgb, material_color.rgb, edge);
    }
    return vec4<f32>(rgb, material_color.a);
}
