// Algorithm: Transform retained 3D lines and indexed meshes on the GPU; expand lines in physical screen space and shade mesh fragments with a bounded WPF/MIL light range and derivative wire coverage.
// Time complexity: O(L + I * K) shader work for L expanded line vertices, I referenced mesh indices, and at most K=16 lights per mesh.
// Space complexity: O(C + E + M + V + I + K) read-only storage for cameras, edges, meshes, vertices, indices, and lights; O(1) private storage and no auxiliary output storage per invocation.
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

@group(0) @binding(0) var<storage, read> cameras: array<Camera3D>;
@group(0) @binding(1) var<storage, read> lines: array<Line3D>;
@group(0) @binding(2) var<storage, read> meshes: array<Mesh3D>;
@group(0) @binding(3) var<storage, read> vertices: array<MeshVertex3D>;
@group(0) @binding(4) var<storage, read> indices: array<u32>;
@group(0) @binding(5) var<storage, read> lights: array<Light3D>;

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
    return output;
}

@fragment
fn fs_mesh_3d(input: MeshOutput) -> @location(0) vec4<f32> {
    let mesh = meshes[input.material];
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
    var rgb = input.color.rgb * (ambient + diffuse) +
        mesh.specular_color.rgb * specular;
    if (mesh.shading_mode == 0u) {
        rgb = input.color.rgb;
    }
    let derivative = fwidth(input.barycentric);
    let edge = 1.0 - min(min(smoothstep(vec3<f32>(0.0), derivative * 1.5, input.barycentric).x,
                              smoothstep(vec3<f32>(0.0), derivative * 1.5, input.barycentric).y),
                         smoothstep(vec3<f32>(0.0), derivative * 1.5, input.barycentric).z);
    if (mesh.render_mode == 1u) {
        return vec4<f32>(input.color.rgb, input.color.a * edge);
    }
    if (mesh.render_mode == 2u) {
        rgb = mix(rgb, input.color.rgb, edge);
    }
    return vec4<f32>(rgb, input.color.a);
}
