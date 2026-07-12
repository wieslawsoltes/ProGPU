// Algorithm: Transform instanced mesh vertices, resolve material/normal data, and apply bounded multi-light solid shading.
// Time complexity: O(L) per fragment for L active lights, bounded by the fixed light array.
// Space complexity: O(1) local storage with O(L) uniform reads.
struct VSUniforms {
    projection: mat4x4<f32>,
    view: mat4x4<f32>,
    cameraPosition: vec3<f32>,
    _pad: f32,
};

struct GpuMesh3DRecord {
    modelTransform: mat4x4<f32>,
    normalTransform: mat4x4<f32>,
    color: vec4<f32>,
    lightDirection: vec4<f32>,
    ambientColor: vec4<f32>,
    specularColor: vec4<f32>,
    materialAmbient: vec4<f32>,
    opacity: f32,
    renderMode: f32,
    shadingMode: f32,
    _pad2: f32,
};

@group(0) @binding(0) var<uniform> uniforms: VSUniforms;
@group(0) @binding(1) var<storage, read> meshRecords: array<GpuMesh3DRecord>;

struct VertexInput {
    @location(0) position: vec3<f32>,
    @location(1) normal: vec3<f32>,
};

struct VertexOutput {
    @builtin(position) position: vec4<f32>,
    @location(0) worldPosition: vec3<f32>,
    @location(1) worldNormal: vec3<f32>,
    @location(2) @interpolate(flat) instanceIdx: u32,
};

struct VertexOutputWireframe {
    @builtin(position) position: vec4<f32>,
    @location(0) worldPosition: vec3<f32>,
    @location(1) worldNormal: vec3<f32>,
    @location(2) barycentric: vec3<f32>,
    @location(3) renderMode: f32,
    @location(4) @interpolate(flat) instanceIdx: u32,
};

fn DistributionGGX(N: vec3<f32>, H: vec3<f32>, roughness: f32) -> f32 {
    let alpha = roughness * roughness;
    let alpha2 = alpha * alpha;
    let NdotH = max(dot(N, H), 0.0);
    let NdotH2 = NdotH * NdotH;

    let denom = (NdotH2 * (alpha2 - 1.0) + 1.0);
    return alpha2 / (3.1415926535 * denom * denom);
}

fn VisibilitySchlickGGX(NdotV: f32, NdotL: f32, roughness: f32) -> f32 {
    let r = (roughness + 1.0);
    let k = (r * r) / 8.0;
    let denom = (NdotV * (1.0 - k) + k) * (NdotL * (1.0 - k) + k) * 4.0;
    return 1.0 / max(denom, 0.0001);
}

fn FresnelSchlick(cosTheta: f32, F0: vec3<f32>) -> vec3<f32> {
    return F0 + (1.0 - F0) * pow(clamp(1.0 - cosTheta, 0.0, 1.0), 5.0);
}

fn GoochShading(N: vec3<f32>, L: vec3<f32>, diffuseColor: vec3<f32>) -> vec3<f32> {
    let NdotL = dot(N, L);
    let t = NdotL * 0.5 + 0.5;
    let coolCol = vec3<f32>(0.0, 0.0, 0.55) + 0.25 * diffuseColor;
    let warmCol = vec3<f32>(0.3, 0.3, 0.0) + 0.25 * diffuseColor;
    return mix(coolCol, warmCol, t);
}

fn ComputeLighting(
    instanceIdx: u32,
    worldPos: vec3<f32>,
    worldNormal: vec3<f32>
) -> vec4<f32> {
    let record = meshRecords[instanceIdx];
    let shading = u32(record.shadingMode + 0.5);

    let N = normalize(worldNormal);

    if (shading == 6u) { // Normals Diagnostic
        let normalColor = N * 0.5 + 0.5;
        return vec4<f32>(normalColor, record.opacity);
    }

    if (shading == 2u) { // Flat / Unlit
        return vec4<f32>(record.color.rgb, record.opacity);
    }

    if (shading == 3u) { // Hidden Line
        return vec4<f32>(0.05, 0.05, 0.06, record.opacity); // background solid fill
    }

    let V = normalize(uniforms.cameraPosition - worldPos);

    let shininess = record.specularColor.w;
    let roughness = clamp(sqrt(2.0 / (max(shininess, 0.001) + 2.0)), 0.04, 1.0);
    let F0 = mix(vec3<f32>(0.04), record.color.rgb, 0.1);

    let keyDir = normalize(record.lightDirection.xyz);
    let keyIntensity = record.lightDirection.w;

    let fillDir = normalize(vec3<f32>(-keyDir.x, 0.5, -keyDir.z));
    let fillIntensity = keyIntensity * 0.35;
    let fillCol = vec3<f32>(0.8, 0.88, 1.0);

    let backDir = normalize(vec3<f32>(-keyDir.x, -keyDir.y, -keyDir.z));
    let backIntensity = keyIntensity * 0.45;
    let backCol = vec3<f32>(1.0, 0.95, 0.9);

    var diffuseOut = vec3<f32>(0.0);
    var specularOut = vec3<f32>(0.0);

    if (shading == 1u) { // Conceptual (Gooch Shading)
        diffuseOut += GoochShading(N, keyDir, record.color.rgb) * keyIntensity;
        diffuseOut += GoochShading(N, fillDir, record.color.rgb) * fillIntensity * fillCol;
        diffuseOut += GoochShading(N, backDir, record.color.rgb) * backIntensity * backCol;

        let H = normalize(keyDir + V);
        let NdotL = max(dot(N, keyDir), 0.0);
        let NdotV = max(dot(N, V), 0.0);
        if (NdotL > 0.0) {
            let D = DistributionGGX(N, H, roughness);
            let V_joint = VisibilitySchlickGGX(NdotV, NdotL, roughness);
            let F = FresnelSchlick(max(dot(H, V), 0.0), F0);
            specularOut += D * V_joint * F * NdotL * keyIntensity;
        }
    } else { // Realistic (PBR GGX) or ShadesOfGray or XRay
        // 1. KEY LIGHT
        {
            let L = keyDir;
            let H = normalize(L + V);
            let NdotL = max(dot(N, L), 0.0);
            let NdotV = max(dot(N, V), 0.0);
            if (NdotL > 0.0) {
                let D = DistributionGGX(N, H, roughness);
                let V_joint = VisibilitySchlickGGX(NdotV, NdotL, roughness);
                let F = FresnelSchlick(max(dot(H, V), 0.0), F0);
                let spec = D * V_joint * F;
                let kS = F;
                let kD = (vec3<f32>(1.0) - kS);
                diffuseOut += (kD * record.color.rgb / 3.1415926535) * NdotL * keyIntensity;
                specularOut += spec * NdotL * keyIntensity;
            }
        }

        // 2. FILL LIGHT
        {
            let L = fillDir;
            let H = normalize(L + V);
            let NdotL = max(dot(N, L), 0.0);
            let NdotV = max(dot(N, V), 0.0);
            if (NdotL > 0.0) {
                let D = DistributionGGX(N, H, roughness);
                let V_joint = VisibilitySchlickGGX(NdotV, NdotL, roughness);
                let F = FresnelSchlick(max(dot(H, V), 0.0), F0);
                let spec = D * V_joint * F;
                let kS = F;
                let kD = (vec3<f32>(1.0) - kS);
                diffuseOut += (kD * record.color.rgb / 3.1415926535) * NdotL * fillIntensity * fillCol;
                specularOut += spec * NdotL * fillIntensity * fillCol;
            }
        }

        // 3. BACK LIGHT
        {
            let L = backDir;
            let H = normalize(L + V);
            let NdotL = max(dot(N, L), 0.0);
            let NdotV = max(dot(N, V), 0.0);
            if (NdotL > 0.0) {
                let D = DistributionGGX(N, H, roughness);
                let V_joint = VisibilitySchlickGGX(NdotV, NdotL, roughness);
                let F = FresnelSchlick(max(dot(H, V), 0.0), F0);
                let spec = D * V_joint * F;
                let kS = F;
                let kD = (vec3<f32>(1.0) - kS);
                diffuseOut += (kD * record.color.rgb / 3.1415926535) * NdotL * backIntensity * backCol;
                specularOut += spec * NdotL * backIntensity * backCol;
            }
        }
    }

    let skyFactor = N.y * 0.5 + 0.5;
    let skyAmbient = record.ambientColor.rgb * record.ambientColor.w;
    let groundAmbient = record.ambientColor.rgb * record.ambientColor.w * 0.4;
    let ambient = mix(groundAmbient, skyAmbient, skyFactor) * record.materialAmbient.rgb;

    let F_rim = pow(1.0 - max(dot(N, V), 0.0), 4.0);
    let rimColor = vec3<f32>(0.85, 0.90, 1.0) * F_rim * 0.25 * keyIntensity;

    var resultColor = ambient + diffuseOut + specularOut + rimColor;

    if (shading == 4u) { // Shades of Gray
        let gray = dot(resultColor, vec3<f32>(0.2126, 0.7152, 0.0722));
        resultColor = vec3<f32>(gray);
    }

    var opacity = record.opacity;
    if (shading == 5u) { // X-Ray Mode
        opacity = clamp(0.15 + 0.55 * pow(1.0 - max(dot(N, V), 0.0), 3.0), 0.0, 1.0) * record.opacity;
    }

    return vec4<f32>(resultColor, opacity);
}

@vertex
fn vs_main(input: VertexInput, @builtin(instance_index) instanceIdx: u32) -> VertexOutput {
    var output: VertexOutput;
    let record = meshRecords[instanceIdx];

    let worldPos = record.modelTransform * vec4<f32>(input.position, 1.0);
    let worldNormal = normalize((record.normalTransform * vec4<f32>(input.normal, 0.0)).xyz);

    output.position = uniforms.projection * uniforms.view * worldPos;
    output.worldPosition = worldPos.xyz;
    output.worldNormal = worldNormal;
    output.instanceIdx = instanceIdx;

    return output;
}

@fragment
fn fs_main(input: VertexOutput, @builtin(front_facing) is_front: bool) -> @location(0) vec4<f32> {
    var normal = input.worldNormal;
    if (!is_front) {
        normal = -input.worldNormal;
    }
    return ComputeLighting(input.instanceIdx, input.worldPosition, normal);
}
