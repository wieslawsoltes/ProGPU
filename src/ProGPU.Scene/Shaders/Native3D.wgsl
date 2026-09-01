// Algorithm: Transform retained 3D lines and indexed meshes on the GPU; expand lines in physical screen space, evaluate the canonical bounded three-light CAD visual-style model, and apply derivative wire coverage.
// Time complexity: O(L + I) shader invocations for L expanded line vertices and I referenced mesh indices; every mesh fragment evaluates at most three fixed lights and one derivative wire test.
// Space complexity: O(C + E + M + V + I) read-only storage for cameras, edges, meshes, vertices, and indices; O(1) private storage and no auxiliary output storage per invocation.
// Lines use six vertices per retained edge. Meshes fetch uint32 indices from
// storage so one pointer-free scene ABI works in native WebGPU and wasm32.

struct Camera3D {
    projection: mat4x4<f32>,
    view: mat4x4<f32>,
    camera_position: vec4<f32>,
    viewport: vec4<f32>,
};

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
    let corner = array<vec2<f32>, 6>(
        vec2<f32>(0.0, -1.0), vec2<f32>(0.0, 1.0),
        vec2<f32>(1.0, -1.0), vec2<f32>(1.0, -1.0),
        vec2<f32>(0.0, 1.0), vec2<f32>(1.0, 1.0))[vertex_index];
    let local = select(line.start, line.end, corner.x > 0.5);
    var start_clip = camera.projection * camera.view * line.transform * line.start;
    var end_clip = camera.projection * camera.view * line.transform * line.end;
    let safe_start_w = select(start_clip.w, 0.000001, abs(start_clip.w) < 0.000001);
    let safe_end_w = select(end_clip.w, 0.000001, abs(end_clip.w) < 0.000001);
    let start_screen = (start_clip.xy / safe_start_w) * camera.viewport.xy;
    let end_screen = (end_clip.xy / safe_end_w) * camera.viewport.xy;
    let delta = end_screen - start_screen;
    let length = max(length(delta), 0.000001);
    let normal = vec2<f32>(-delta.y, delta.x) / length;
    var clip = camera.projection * camera.view * line.transform * local;
    let expanded_xy = clip.xy +
        normal * corner.y * line.thickness * clip.w / camera.viewport.xy;
    clip = vec4<f32>(expanded_xy, clip.zw);
    var output: LineOutput;
    output.position = clip;
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
    let world = mesh.model_transform * vertex.position;
    var output: MeshOutput;
    output.position = camera.projection * camera.view * world;
    output.color = vec4<f32>(mesh.color.rgb, mesh.color.a * mesh.opacity);
    output.normal = normalize((mesh.normal_transform * vec4<f32>(vertex.normal.xyz, 0.0)).xyz);
    output.world_position = world.xyz;
    output.material = instance_index;
    let corner = vertex_index % 3u;
    output.barycentric = vec3<f32>(select(0.0, 1.0, corner == 0u), select(0.0, 1.0, corner == 1u), select(0.0, 1.0, corner == 2u));
    return output;
}

fn distribution_ggx(
    normal: vec3<f32>,
    half_vector: vec3<f32>,
    roughness: f32
) -> f32 {
    let alpha = roughness * roughness;
    let alpha2 = alpha * alpha;
    let normal_dot_half = max(dot(normal, half_vector), 0.0);
    let normal_dot_half2 = normal_dot_half * normal_dot_half;
    let denominator =
        normal_dot_half2 * (alpha2 - 1.0) + 1.0;
    return alpha2 /
        (3.1415926535 * denominator * denominator);
}

fn visibility_schlick_ggx(
    normal_dot_view: f32,
    normal_dot_light: f32,
    roughness: f32
) -> f32 {
    let radius = roughness + 1.0;
    let k = radius * radius / 8.0;
    let denominator =
        (normal_dot_view * (1.0 - k) + k) *
        (normal_dot_light * (1.0 - k) + k) * 4.0;
    return 1.0 / max(denominator, 0.0001);
}

fn fresnel_schlick(
    cosine: f32,
    base_reflectance: vec3<f32>
) -> vec3<f32> {
    return base_reflectance +
        (vec3<f32>(1.0) - base_reflectance) *
        pow(clamp(1.0 - cosine, 0.0, 1.0), 5.0);
}

fn gooch_shading(
    normal: vec3<f32>,
    light: vec3<f32>,
    diffuse_color: vec3<f32>
) -> vec3<f32> {
    let blend = dot(normal, light) * 0.5 + 0.5;
    let cool = vec3<f32>(0.0, 0.0, 0.55) +
        0.25 * diffuse_color;
    let warm = vec3<f32>(0.3, 0.3, 0.0) +
        0.25 * diffuse_color;
    return mix(cool, warm, blend);
}

fn accumulate_pbr_light(
    normal: vec3<f32>,
    view: vec3<f32>,
    light: vec3<f32>,
    light_color: vec3<f32>,
    intensity: f32,
    roughness: f32,
    base_reflectance: vec3<f32>,
    diffuse_color: vec3<f32>
) -> vec4<f32> {
    let half_vector = normalize(light + view);
    let normal_dot_light = max(dot(normal, light), 0.0);
    let normal_dot_view = max(dot(normal, view), 0.0);
    if (normal_dot_light <= 0.0) {
        return vec4<f32>(0.0);
    }
    let distribution =
        distribution_ggx(normal, half_vector, roughness);
    let visibility = visibility_schlick_ggx(
        normal_dot_view,
        normal_dot_light,
        roughness);
    let fresnel = fresnel_schlick(
        max(dot(half_vector, view), 0.0),
        base_reflectance);
    let specular = distribution * visibility * fresnel;
    let diffuse_weight = vec3<f32>(1.0) - fresnel;
    let diffuse =
        diffuse_weight * diffuse_color / 3.1415926535;
    return vec4<f32>(
        (diffuse + specular) * normal_dot_light *
            intensity * light_color,
        0.0);
}

// Exact ProGPU-owned algorithm provenance: the managed Mesh3DSolid.wgsl and
// Mesh3DWireframe.wgsl ComputeLighting contract. Native3D uses a distinct
// storage ABI because it also batches retained 3D lines and indexed meshes in
// one pointer-free scene resource; the visual-style math remains equivalent.
fn compute_mesh_lighting(
    mesh: Mesh3D,
    camera: Camera3D,
    world_position: vec3<f32>,
    world_normal: vec3<f32>
) -> vec4<f32> {
    let shading = mesh.shading_mode;
    let normal = normalize(world_normal);

    if (shading == 6u) {
        return vec4<f32>(
            normal * 0.5 + vec3<f32>(0.5),
            mesh.color.a * mesh.opacity);
    }
    if (shading == 2u) {
        return vec4<f32>(
            mesh.color.rgb,
            mesh.color.a * mesh.opacity);
    }
    if (shading == 3u) {
        return vec4<f32>(
            0.05,
            0.05,
            0.06,
            mesh.color.a * mesh.opacity);
    }

    let view = normalize(
        camera.camera_position.xyz - world_position);
    let shininess = mesh.specular_color.w;
    let roughness = clamp(
        sqrt(2.0 / (max(shininess, 0.001) + 2.0)),
        0.04,
        1.0);
    let base_reflectance = mix(
        vec3<f32>(0.04),
        mesh.color.rgb,
        0.1);
    let key = normalize(mesh.light_direction.xyz);
    let key_intensity = mesh.light_direction.w;
    let fill = normalize(vec3<f32>(-key.x, 0.5, -key.z));
    let fill_intensity = key_intensity * 0.35;
    let fill_color = vec3<f32>(0.8, 0.88, 1.0);
    let back = normalize(-key);
    let back_intensity = key_intensity * 0.45;
    let back_color = vec3<f32>(1.0, 0.95, 0.9);

    var illuminated = vec3<f32>(0.0);
    if (shading == 1u) {
        illuminated += gooch_shading(
            normal,
            key,
            mesh.color.rgb) * key_intensity;
        illuminated += gooch_shading(
            normal,
            fill,
            mesh.color.rgb) * fill_intensity * fill_color;
        illuminated += gooch_shading(
            normal,
            back,
            mesh.color.rgb) * back_intensity * back_color;

        let half_vector = normalize(key + view);
        let normal_dot_light = max(dot(normal, key), 0.0);
        let normal_dot_view = max(dot(normal, view), 0.0);
        if (normal_dot_light > 0.0) {
            let distribution = distribution_ggx(
                normal,
                half_vector,
                roughness);
            let visibility = visibility_schlick_ggx(
                normal_dot_view,
                normal_dot_light,
                roughness);
            let fresnel = fresnel_schlick(
                max(dot(half_vector, view), 0.0),
                base_reflectance);
            illuminated += distribution * visibility * fresnel *
                normal_dot_light * key_intensity;
        }
    } else {
        illuminated += accumulate_pbr_light(
            normal,
            view,
            key,
            vec3<f32>(1.0),
            key_intensity,
            roughness,
            base_reflectance,
            mesh.color.rgb).rgb;
        illuminated += accumulate_pbr_light(
            normal,
            view,
            fill,
            fill_color,
            fill_intensity,
            roughness,
            base_reflectance,
            mesh.color.rgb).rgb;
        illuminated += accumulate_pbr_light(
            normal,
            view,
            back,
            back_color,
            back_intensity,
            roughness,
            base_reflectance,
            mesh.color.rgb).rgb;
    }

    let sky_factor = normal.y * 0.5 + 0.5;
    let sky_ambient =
        mesh.ambient_color.rgb * mesh.ambient_color.w;
    let ground_ambient = sky_ambient * 0.4;
    let ambient = mix(
        ground_ambient,
        sky_ambient,
        sky_factor) * mesh.material_ambient.rgb;
    let rim_factor = pow(
        1.0 - max(dot(normal, view), 0.0),
        4.0);
    let rim = vec3<f32>(0.85, 0.90, 1.0) *
        rim_factor * 0.25 * key_intensity;
    var rgb = ambient + illuminated + rim;
    if (shading == 4u) {
        rgb = vec3<f32>(
            dot(rgb, vec3<f32>(0.2126, 0.7152, 0.0722)));
    }

    var opacity = mesh.color.a * mesh.opacity;
    if (shading == 5u) {
        opacity = clamp(
            0.15 + 0.55 * pow(
                1.0 - max(dot(normal, view), 0.0),
                3.0),
            0.0,
            1.0) * mesh.color.a * mesh.opacity;
    }
    return vec4<f32>(rgb, opacity);
}

@fragment
fn fs_mesh_3d(
    input: MeshOutput,
    @builtin(front_facing) is_front: bool
) -> @location(0) vec4<f32> {
    let mesh = meshes[input.material];
    let camera = cameras[mesh.camera_index];
    var normal = input.normal;
    if (!is_front) {
        normal = -normal;
    }
    let solid = compute_mesh_lighting(
        mesh,
        camera,
        input.world_position,
        normal);
    let derivative_x = dpdx(input.barycentric);
    let derivative_y = dpdy(input.barycentric);
    let gradient = max(
        sqrt(
            derivative_x * derivative_x +
            derivative_y * derivative_y),
        vec3<f32>(0.00001));
    let distance = input.barycentric / gradient;
    let minimum_distance = min(
        distance.x,
        min(distance.y, distance.z));
    let edge_mix = smoothstep(
        0.5,
        1.5,
        minimum_distance);
    let wire_color = vec3<f32>(0.85, 0.85, 0.9);
    if (mesh.render_mode == 1u) {
        let alpha = (1.0 - edge_mix) * solid.a;
        if (alpha < 0.01) {
            discard;
        }
        return vec4<f32>(wire_color, alpha);
    }
    if (mesh.render_mode == 2u) {
        return vec4<f32>(
            mix(wire_color, solid.rgb, edge_mix),
            solid.a);
    }
    return solid;
}
