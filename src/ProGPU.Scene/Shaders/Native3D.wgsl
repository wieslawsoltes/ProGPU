// Algorithm: Transform retained 3D lines and indexed meshes on the GPU; expand lines in physical screen space and shade mesh fragments with bounded directional lighting and derivative wire coverage.
// Time complexity: O(L + I) shader invocations for L expanded line vertices and I referenced mesh indices; every invocation performs fixed matrix, lighting, and at most one derivative wire evaluation.
// Space complexity: O(C + E + M + V + I) read-only storage for cameras, edges, meshes, vertices, and indices; O(1) private storage and no auxiliary output storage per invocation.
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
    reserved0: u32,
    reserved1: u32,
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
    let light = normalize(-mesh.light_direction.xyz);
    let view = normalize(camera.camera_position.xyz - input.world_position);
    let light_intensity = max(mesh.light_direction.w, 0.0);
    let ambient_intensity = max(mesh.ambient_color.w, 0.0);
    let shininess = max(mesh.specular_color.w, 0.001);
    let diffuse = max(dot(n, light), 0.0) * light_intensity;
    let reflected = reflect(-light, n);
    let specular = pow(
        max(dot(view, reflected), 0.0),
        shininess) * light_intensity;
    let ambient = mesh.ambient_color.rgb * ambient_intensity *
        mesh.material_ambient.rgb;
    var rgb = input.color.rgb * (ambient + vec3<f32>(diffuse)) + mesh.specular_color.rgb * specular;
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
