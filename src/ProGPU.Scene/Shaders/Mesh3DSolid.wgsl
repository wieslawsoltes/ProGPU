// Algorithm: Transform instanced UV meshes, apply a normalized crop plus quarter-turn/mirror presentation transform, sample a leased filterable RGB/NV12 texture or manually reconstruct an unfilterable P010 plane pair, apply the bounded fallback kernel and fused color effects, then evaluate bounded multi-light shading.
// Time complexity: O(L + S + G) per fragment for L fixed lights, S source taps, and G bounded material-gradient stops; S is exactly 1 or 9, filterable RGB uses S samples, filterable NV12 uses 2S samples, and unfilterable P010 uses 2S nearest or 8S bilinear texel loads.
// Space complexity: O(1) local/private storage, O(1) material records per mesh, and at most 72 unfilterable texel loads per fragment for the fixed nine-tap fallback.
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
    textureSamplingMode: f32,
    textureEffects0: vec4<f32>,
    textureEffects1: vec4<f32>,
    textureInfo: vec4<f32>,
    colorMatrixRed: vec4<f32>,
    colorMatrixGreen: vec4<f32>,
    colorMatrixBlue: vec4<f32>,
    colorMatrixAlpha: vec4<f32>,
    colorMatrixOffset: vec4<f32>,
    textureFlags: vec4<f32>,
    yuvRange: vec4<f32>,
    yuvRed: vec4<f32>,
    yuvGreen: vec4<f32>,
    yuvBlue: vec4<f32>,
    textureSourceRect: vec4<f32>,
    lightOffset: u32,
    lightCount: u32,
    lightPadding: vec2<u32>,
    materialGradientPoints: vec4<f32>,
    materialGradientEllipse: vec4<f32>,
    materialBrushTransform0: vec4<f32>,
    materialBrushTransform1: vec4<f32>,
    materialBrushMetadata: vec4<f32>,
    materialStopMetadata: vec4<f32>,
};

struct GpuGradientStop {
    color: vec4<f32>,
    offset: f32,
    padding0: f32,
    padding1: f32,
    padding2: f32,
};

struct GpuLight3DRecord {
    metadata: vec4<f32>,
    color: vec4<f32>,
    positionRange: vec4<f32>,
    directionInnerCos: vec4<f32>,
    attenuationOuterCos: vec4<f32>,
};

@group(0) @binding(0) var<uniform> uniforms: VSUniforms;
@group(0) @binding(1) var<storage, read> meshRecords: array<GpuMesh3DRecord>;
@group(0) @binding(2) var<storage, read> lightRecords: array<GpuLight3DRecord>;
@group(0) @binding(3) var<storage, read> materialGradientStops: array<GpuGradientStop>;
@group(1) @binding(0) var materialSampler: sampler;
@group(1) @binding(1) var materialTexture: texture_2d<f32>;
@group(1) @binding(2) var materialChromaTexture: texture_2d<f32>;
@group(1) @binding(3) var unfilterableMaterialTexture: texture_2d<f32>;
@group(1) @binding(4) var unfilterableMaterialChromaTexture: texture_2d<f32>;

struct VertexInput {
    @location(0) position: vec3<f32>,
    @location(1) normal: vec3<f32>,
    @location(2) textureCoordinate: vec2<f32>,
    @location(3) recordIndex: u32,
};

struct VertexOutput {
    @builtin(position) position: vec4<f32>,
    @location(0) worldPosition: vec3<f32>,
    @location(1) worldNormal: vec3<f32>,
    @location(2) textureCoordinate: vec2<f32>,
    @location(3) @interpolate(flat) instanceIdx: u32,
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

fn ComputeWpfLighting(
    record: GpuMesh3DRecord,
    N: vec3<f32>,
    V: vec3<f32>,
    worldPos: vec3<f32>,
    albedo: vec3<f32>
) -> vec3<f32> {
    var ambient = vec3<f32>(0.0);
    var diffuse = vec3<f32>(0.0);
    var specular = vec3<f32>(0.0);
    let shininess = max(record.specularColor.w, 0.001);
    for (var lightIndex = 0u; lightIndex < 16u; lightIndex++) {
        if (lightIndex >= record.lightCount) {
            break;
        }
        let source = lightRecords[record.lightOffset + lightIndex];
        let kind = u32(source.metadata.x + 0.5);
        if (kind == 0u) {
            ambient += source.color.rgb;
            continue;
        }
        var L = normalize(-source.directionInnerCos.xyz);
        var attenuation = 1.0;
        if (kind >= 2u) {
            let toLight = source.positionRange.xyz - worldPos;
            let distance = length(toLight);
            if (distance <= 0.000001 || distance > source.positionRange.w) {
                continue;
            }
            L = toLight / distance;
            let terms = source.attenuationOuterCos.xyz;
            attenuation = 1.0 / max(
                terms.x + terms.y * distance +
                    terms.z * distance * distance,
                1.0);
            if (kind == 3u) {
                let rho = max(dot(
                    normalize(-source.directionInnerCos.xyz), L), 0.0);
                let outerCos = source.attenuationOuterCos.w;
                attenuation *= clamp(
                    (rho - outerCos) /
                        max(source.directionInnerCos.w - outerCos, 0.000001),
                    0.0,
                    1.0);
            }
        }
        let NdotL = max(dot(N, L), 0.0);
        if (NdotL <= 0.0) {
            continue;
        }
        diffuse += source.color.rgb * NdotL * attenuation;
        let H = normalize(V + L);
        specular += source.color.rgb *
            pow(max(dot(N, H), 0.0), shininess) * attenuation;
    }
    return albedo * (ambient * record.materialAmbient.rgb + diffuse) +
        record.specularColor.rgb * specular;
}

fn SampleMaterialSource(
    record: GpuMesh3DRecord,
    textureCoordinate: vec2<f32>
) -> vec4<f32> {
    if (record.textureFlags.y > 0.5) {
        let rawY = textureSampleLevel(
            materialTexture,
            materialSampler,
            textureCoordinate,
            0.0).r;
        let rawChroma = textureSampleLevel(
            materialChromaTexture,
            materialSampler,
            textureCoordinate,
            0.0).rg;
        let components = vec3<f32>(
            (rawY - record.yuvRange.x) *
                record.yuvRange.y,
            (rawChroma.x - record.yuvRange.z) *
                record.yuvRange.w,
            (rawChroma.y - record.yuvRange.z) *
                record.yuvRange.w);
        return vec4<f32>(
            dot(components, record.yuvRed.xyz),
            dot(components, record.yuvGreen.xyz),
            dot(components, record.yuvBlue.xyz),
            1.0);
    }
    return textureSampleLevel(
        materialTexture,
        materialSampler,
        textureCoordinate,
        0.0);
}

fn ClampMaterialCoordinate(
    coordinate: vec2<i32>,
    dimensions: vec2<u32>
) -> vec2<i32> {
    return clamp(
        coordinate,
        vec2<i32>(0),
        vec2<i32>(dimensions) - vec2<i32>(1));
}

fn LoadUnfilterableLuma(
    coordinate: vec2<i32>,
    dimensions: vec2<u32>
) -> f32 {
    return textureLoad(
        unfilterableMaterialTexture,
        ClampMaterialCoordinate(
            coordinate,
            dimensions),
        0).r;
}

fn LoadUnfilterableChroma(
    coordinate: vec2<i32>,
    dimensions: vec2<u32>
) -> vec2<f32> {
    return textureLoad(
        unfilterableMaterialChromaTexture,
        ClampMaterialCoordinate(
            coordinate,
            dimensions),
        0).rg;
}

fn SampleUnfilterableLuma(
    textureCoordinate: vec2<f32>,
    linear: bool
) -> f32 {
    let dimensions =
        textureDimensions(
            unfilterableMaterialTexture);
    if (!linear) {
        return LoadUnfilterableLuma(
            vec2<i32>(
                floor(
                    textureCoordinate *
                    vec2<f32>(dimensions))),
            dimensions);
    }

    let position =
        textureCoordinate *
            vec2<f32>(dimensions) -
        vec2<f32>(0.5);
    let base = vec2<i32>(floor(position));
    let fraction = fract(position);
    return mix(
        mix(
            LoadUnfilterableLuma(
                base,
                dimensions),
            LoadUnfilterableLuma(
                base + vec2<i32>(1, 0),
                dimensions),
            fraction.x),
        mix(
            LoadUnfilterableLuma(
                base + vec2<i32>(0, 1),
                dimensions),
            LoadUnfilterableLuma(
                base + vec2<i32>(1, 1),
                dimensions),
            fraction.x),
        fraction.y);
}

fn SampleUnfilterableChroma(
    textureCoordinate: vec2<f32>,
    linear: bool
) -> vec2<f32> {
    let dimensions =
        textureDimensions(
            unfilterableMaterialChromaTexture);
    if (!linear) {
        return LoadUnfilterableChroma(
            vec2<i32>(
                floor(
                    textureCoordinate *
                    vec2<f32>(dimensions))),
            dimensions);
    }

    let position =
        textureCoordinate *
            vec2<f32>(dimensions) -
        vec2<f32>(0.5);
    let base = vec2<i32>(floor(position));
    let fraction = fract(position);
    return mix(
        mix(
            LoadUnfilterableChroma(
                base,
                dimensions),
            LoadUnfilterableChroma(
                base + vec2<i32>(1, 0),
                dimensions),
            fraction.x),
        mix(
            LoadUnfilterableChroma(
                base + vec2<i32>(0, 1),
                dimensions),
            LoadUnfilterableChroma(
                base + vec2<i32>(1, 1),
                dimensions),
            fraction.x),
        fraction.y);
}

fn SampleMaterialSourceUnfilterable(
    record: GpuMesh3DRecord,
    textureCoordinate: vec2<f32>
) -> vec4<f32> {
    let linear =
        record.textureSamplingMode > 0.5;
    let rawY = SampleUnfilterableLuma(
        textureCoordinate,
        linear);
    let rawChroma = SampleUnfilterableChroma(
        textureCoordinate,
        linear);
    let components = vec3<f32>(
        (rawY - record.yuvRange.x) *
            record.yuvRange.y,
        (rawChroma.x - record.yuvRange.z) *
            record.yuvRange.w,
        (rawChroma.y - record.yuvRange.z) *
            record.yuvRange.w);
    return vec4<f32>(
        dot(components, record.yuvRed.xyz),
        dot(components, record.yuvGreen.xyz),
        dot(components, record.yuvBlue.xyz),
        1.0);
}

fn TransformMaterialCoordinate(
    record: GpuMesh3DRecord,
    textureCoordinate: vec2<f32>
) -> vec2<f32> {
    var localCoordinate = textureCoordinate;
    if (record.textureFlags.w > 0.5) {
        localCoordinate.x = 1.0 - localCoordinate.x;
    }

    let quarterTurns =
        i32(round(record.textureFlags.z)) & 3;
    if (quarterTurns == 1) {
        localCoordinate = vec2<f32>(
            localCoordinate.y,
            1.0 - localCoordinate.x);
    } else if (quarterTurns == 2) {
        localCoordinate = vec2<f32>(
            1.0 - localCoordinate.x,
            1.0 - localCoordinate.y);
    } else if (quarterTurns == 3) {
        localCoordinate = vec2<f32>(
            1.0 - localCoordinate.y,
            localCoordinate.x);
    }

    return record.textureSourceRect.xy +
        localCoordinate * record.textureSourceRect.zw;
}

fn TransformMaterialBrushCoordinate(
    record: GpuMesh3DRecord,
    coordinate: vec2<f32>
) -> vec2<f32> {
    let point = vec3<f32>(coordinate, 1.0);
    return vec2<f32>(
        dot(point, record.materialBrushTransform0.xyz),
        dot(point, record.materialBrushTransform1.xyz));
}

fn ApplyMaterialGradientSpread(value: f32, method: u32) -> f32 {
    if (method == 1u) {
        let period = fract(value * 0.5) * 2.0;
        return select(period, 2.0 - period, period > 1.0);
    }
    if (method == 2u) {
        return fract(value);
    }
    return value;
}

fn SrgbToLinearMaterialComponent(value: f32) -> f32 {
    if (value <= 0.04045) {
        return value / 12.92;
    }
    return pow((value + 0.055) / 1.055, 2.4);
}

fn LinearToSrgbMaterialComponent(value: f32) -> f32 {
    let clamped = max(value, 0.0);
    if (clamped <= 0.0031308) {
        return clamped * 12.92;
    }
    return 1.055 * pow(clamped, 1.0 / 2.4) - 0.055;
}

fn InterpolateMaterialGradient(
    startColor: vec4<f32>,
    endColor: vec4<f32>,
    factor: f32,
    interpolationMode: u32
) -> vec4<f32> {
    if (interpolationMode == 1u) {
        let startLinear = vec3<f32>(
            SrgbToLinearMaterialComponent(startColor.r),
            SrgbToLinearMaterialComponent(startColor.g),
            SrgbToLinearMaterialComponent(startColor.b));
        let endLinear = vec3<f32>(
            SrgbToLinearMaterialComponent(endColor.r),
            SrgbToLinearMaterialComponent(endColor.g),
            SrgbToLinearMaterialComponent(endColor.b));
        let mixed = mix(startLinear, endLinear, factor);
        return vec4<f32>(
            LinearToSrgbMaterialComponent(mixed.r),
            LinearToSrgbMaterialComponent(mixed.g),
            LinearToSrgbMaterialComponent(mixed.b),
            mix(startColor.a, endColor.a, factor));
    }
    return mix(startColor, endColor, factor);
}

fn SampleMaterialGradientStops(
    record: GpuMesh3DRecord,
    value: f32
) -> vec4<f32> {
    let stopOffset = u32(record.materialStopMetadata.x + 0.5);
    let stopCount = u32(record.materialStopMetadata.y + 0.5);
    var previous = materialGradientStops[stopOffset];
    if (value < previous.offset) {
        return previous.color;
    }
    for (var index = 1u; index < stopCount; index++) {
        let current = materialGradientStops[stopOffset + index];
        if (value < current.offset) {
            let factor = clamp(
                (value - previous.offset) /
                    max(current.offset - previous.offset, 0.0001),
                0.0,
                1.0);
            return InterpolateMaterialGradient(
                previous.color,
                current.color,
                factor,
                u32(record.materialBrushMetadata.w + 0.5));
        }
        previous = current;
    }
    return previous.color;
}

fn SampleMaterialGradient(
    record: GpuMesh3DRecord,
    textureCoordinate: vec2<f32>
) -> vec4<f32> {
    let coordinate = TransformMaterialBrushCoordinate(
        record,
        textureCoordinate);
    let kind = u32(record.materialBrushMetadata.x + 0.5);
    var value = 0.0;
    if (kind == 1u) {
        let start = record.materialGradientPoints.xy;
        let direction = record.materialGradientPoints.zw - start;
        let lengthSquared = dot(direction, direction);
        if (lengthSquared > 0.0001) {
            value = dot(coordinate - start, direction) /
                lengthSquared;
        }
    } else {
        let center = record.materialGradientEllipse.xy;
        let radii = max(
            record.materialGradientEllipse.zw,
            vec2<f32>(0.0001));
        let point = (coordinate - center) / radii;
        let origin =
            (record.materialGradientPoints.xy - center) /
            radii;
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
    let spread = u32(record.materialBrushMetadata.z + 0.5);
    if (spread == 3u && (value < 0.0 || value > 1.0)) {
        return vec4<f32>(0.0);
    }
    let color = SampleMaterialGradientStops(
        record,
        ApplyMaterialGradientSpread(value, spread));
    return vec4<f32>(
        color.rgb,
        color.a * record.materialBrushMetadata.y);
}

fn ApplyMaterialEffects(
    record: GpuMesh3DRecord,
    sampledColor: vec4<f32>
) -> vec4<f32> {
    var straightColor = sampledColor;
    if (record.textureInfo.z > 0.5) {
        if (straightColor.a > 0.00001) {
            straightColor = vec4<f32>(
                straightColor.rgb / straightColor.a,
                straightColor.a);
        } else {
            straightColor = vec4<f32>(0.0);
        }
    }

    straightColor = vec4<f32>(
        straightColor.rgb +
            vec3<f32>(record.textureEffects0.x),
        straightColor.a);
    straightColor = vec4<f32>(
        (straightColor.rgb - vec3<f32>(0.5)) *
                record.textureEffects0.y +
            vec3<f32>(0.5),
        straightColor.a);
    let luminance = dot(
        straightColor.rgb,
        vec3<f32>(0.2126, 0.7152, 0.0722));
    straightColor = vec4<f32>(
        mix(
            vec3<f32>(luminance),
            straightColor.rgb,
            record.textureEffects0.z),
        straightColor.a);
    straightColor = vec4<f32>(
        mix(
            straightColor.rgb,
            vec3<f32>(luminance),
            record.textureEffects0.w),
        straightColor.a);

    let sepia = vec3<f32>(
        dot(
            straightColor.rgb,
            vec3<f32>(0.393, 0.769, 0.189)),
        dot(
            straightColor.rgb,
            vec3<f32>(0.349, 0.686, 0.168)),
        dot(
            straightColor.rgb,
            vec3<f32>(0.272, 0.534, 0.131)));
    straightColor = vec4<f32>(
        mix(
            straightColor.rgb,
            sepia,
            record.textureEffects1.x),
        straightColor.a);
    straightColor = vec4<f32>(
        mix(
            straightColor.rgb,
            vec3<f32>(1.0) - straightColor.rgb,
            record.textureEffects1.y),
        straightColor.a);

    if (record.textureInfo.w > 0.5) {
        let alpha = dot(
            straightColor.rgb,
            vec3<f32>(0.2126, 0.7152, 0.0722)) *
            straightColor.a;
        straightColor = vec4<f32>(0.0, 0.0, 0.0, alpha);
    }
    if (record.textureFlags.x > 0.5) {
        let matrixSource = straightColor;
        straightColor = vec4<f32>(
            dot(matrixSource, record.colorMatrixRed) +
                record.colorMatrixOffset.r,
            dot(matrixSource, record.colorMatrixGreen) +
                record.colorMatrixOffset.g,
            dot(matrixSource, record.colorMatrixBlue) +
                record.colorMatrixOffset.b,
            dot(matrixSource, record.colorMatrixAlpha) +
                record.colorMatrixOffset.a);
    }
    return clamp(
        straightColor,
        vec4<f32>(0.0),
        vec4<f32>(1.0));
}

fn SampleMaterial(
    record: GpuMesh3DRecord,
    textureCoordinate: vec2<f32>
) -> vec4<f32> {
    if (record.materialBrushMetadata.x > 0.5) {
        return SampleMaterialGradient(
            record,
            textureCoordinate);
    }
    let sourceCoordinate = TransformMaterialCoordinate(
        record,
        textureCoordinate);
    var color: vec4<f32>;
    if (record.textureEffects1.z > 0.01) {
        let texel = vec2<f32>(1.0) /
            max(record.textureInfo.xy, vec2<f32>(1.0));
        let radius = clamp(
            record.textureEffects1.z,
            0.0,
            8.0);
        let step = texel * radius;
        color =
            SampleMaterialSource(
                record,
                sourceCoordinate) * 0.25;
        color += SampleMaterialSource(
            record,
            sourceCoordinate +
                vec2<f32>(step.x, 0.0)) *
            0.125;
        color += SampleMaterialSource(
            record,
            sourceCoordinate -
                vec2<f32>(step.x, 0.0)) *
            0.125;
        color += SampleMaterialSource(
            record,
            sourceCoordinate +
                vec2<f32>(0.0, step.y)) *
            0.125;
        color += SampleMaterialSource(
            record,
            sourceCoordinate -
                vec2<f32>(0.0, step.y)) *
            0.125;
        color += SampleMaterialSource(
            record,
            sourceCoordinate + step) * 0.0625;
        color += SampleMaterialSource(
            record,
            sourceCoordinate - step) * 0.0625;
        color += SampleMaterialSource(
            record,
            sourceCoordinate +
                vec2<f32>(step.x, -step.y)) *
            0.0625;
        color += SampleMaterialSource(
            record,
            sourceCoordinate +
                vec2<f32>(-step.x, step.y)) *
            0.0625;
    } else {
        color = SampleMaterialSource(
            record,
            sourceCoordinate);
    }
    return ApplyMaterialEffects(record, color);
}

fn SampleMaterialUnfilterable(
    record: GpuMesh3DRecord,
    textureCoordinate: vec2<f32>
) -> vec4<f32> {
    if (record.materialBrushMetadata.x > 0.5) {
        return SampleMaterialGradient(
            record,
            textureCoordinate);
    }
    let sourceCoordinate = TransformMaterialCoordinate(
        record,
        textureCoordinate);
    var color: vec4<f32>;
    if (record.textureEffects1.z > 0.01) {
        let texel = vec2<f32>(1.0) /
            max(record.textureInfo.xy, vec2<f32>(1.0));
        let radius = clamp(
            record.textureEffects1.z,
            0.0,
            8.0);
        let step = texel * radius;
        color =
            SampleMaterialSourceUnfilterable(
                record,
                sourceCoordinate) * 0.25;
        color += SampleMaterialSourceUnfilterable(
            record,
            sourceCoordinate +
                vec2<f32>(step.x, 0.0)) *
            0.125;
        color += SampleMaterialSourceUnfilterable(
            record,
            sourceCoordinate -
                vec2<f32>(step.x, 0.0)) *
            0.125;
        color += SampleMaterialSourceUnfilterable(
            record,
            sourceCoordinate +
                vec2<f32>(0.0, step.y)) *
            0.125;
        color += SampleMaterialSourceUnfilterable(
            record,
            sourceCoordinate -
                vec2<f32>(0.0, step.y)) *
            0.125;
        color += SampleMaterialSourceUnfilterable(
            record,
            sourceCoordinate + step) * 0.0625;
        color += SampleMaterialSourceUnfilterable(
            record,
            sourceCoordinate - step) * 0.0625;
        color += SampleMaterialSourceUnfilterable(
            record,
            sourceCoordinate +
                vec2<f32>(step.x, -step.y)) *
            0.0625;
        color += SampleMaterialSourceUnfilterable(
            record,
            sourceCoordinate +
                vec2<f32>(-step.x, step.y)) *
            0.0625;
    } else {
        color =
            SampleMaterialSourceUnfilterable(
                record,
                sourceCoordinate);
    }
    return ApplyMaterialEffects(record, color);
}

fn ComputeLighting(
    instanceIdx: u32,
    worldPos: vec3<f32>,
    worldNormal: vec3<f32>,
    albedo: vec4<f32>
) -> vec4<f32> {
    let record = meshRecords[instanceIdx];
    let shading = u32(record.shadingMode + 0.5);

    let N = normalize(worldNormal);

    if (shading == 6u) { // Normals Diagnostic
        let normalColor = N * 0.5 + 0.5;
        return vec4<f32>(
            normalColor,
            record.opacity * albedo.a);
    }

    if (shading == 2u) { // Flat / Unlit
        return vec4<f32>(
            albedo.rgb,
            record.opacity * albedo.a);
    }

    if (shading == 3u) { // Hidden Line
        return vec4<f32>(
            0.05,
            0.05,
            0.06,
            record.opacity * albedo.a); // background solid fill
    }

    let V = normalize(uniforms.cameraPosition - worldPos);

    let shininess = record.specularColor.w;
    if (record.lightCount != 0u) {
        var resultColor = ComputeWpfLighting(
            record, N, V, worldPos, albedo.rgb);
        if (shading == 4u) {
            let gray = dot(
                resultColor,
                vec3<f32>(0.2126, 0.7152, 0.0722));
            resultColor = vec3<f32>(gray);
        }
        var explicitOpacity = record.opacity * albedo.a;
        if (shading == 5u) {
            explicitOpacity = clamp(
                0.15 + 0.55 *
                    pow(1.0 - max(dot(N, V), 0.0), 3.0),
                0.0,
                1.0) * record.opacity * albedo.a;
        }
        return vec4<f32>(resultColor, explicitOpacity);
    }
    let roughness = clamp(sqrt(2.0 / (max(shininess, 0.001) + 2.0)), 0.04, 1.0);
    let F0 = mix(vec3<f32>(0.04), albedo.rgb, 0.1);

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
        diffuseOut += GoochShading(N, keyDir, albedo.rgb) * keyIntensity;
        diffuseOut += GoochShading(N, fillDir, albedo.rgb) * fillIntensity * fillCol;
        diffuseOut += GoochShading(N, backDir, albedo.rgb) * backIntensity * backCol;

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
                diffuseOut += (kD * albedo.rgb / 3.1415926535) * NdotL * keyIntensity;
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
                diffuseOut += (kD * albedo.rgb / 3.1415926535) * NdotL * fillIntensity * fillCol;
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
                diffuseOut += (kD * albedo.rgb / 3.1415926535) * NdotL * backIntensity * backCol;
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

    var opacity = record.opacity * albedo.a;
    if (shading == 5u) { // X-Ray Mode
        opacity = clamp(
            0.15 + 0.55 *
                pow(1.0 - max(dot(N, V), 0.0), 3.0),
            0.0,
            1.0) * record.opacity * albedo.a;
    }

    return vec4<f32>(resultColor, opacity);
}

@vertex
fn vs_main(input: VertexInput) -> VertexOutput {
    var output: VertexOutput;
    let instanceIdx = input.recordIndex;
    let record = meshRecords[instanceIdx];

    let worldPos = record.modelTransform * vec4<f32>(input.position, 1.0);
    let worldNormal = normalize((record.normalTransform * vec4<f32>(input.normal, 0.0)).xyz);

    output.position = uniforms.projection * uniforms.view * worldPos;
    output.worldPosition = worldPos.xyz;
    output.worldNormal = worldNormal;
    output.textureCoordinate = input.textureCoordinate;
    output.instanceIdx = instanceIdx;

    return output;
}

@fragment
fn fs_main(input: VertexOutput, @builtin(front_facing) is_front: bool) -> @location(0) vec4<f32> {
    var normal = input.worldNormal;
    if (!is_front) {
        normal = -input.worldNormal;
    }
    let record = meshRecords[input.instanceIdx];
    let materialColor =
        SampleMaterial(record, input.textureCoordinate) *
        record.color;
    return ComputeLighting(
        input.instanceIdx,
        input.worldPosition,
        normal,
        materialColor);
}

@fragment
fn fs_unfilterable(
    input: VertexOutput,
    @builtin(front_facing) is_front: bool
) -> @location(0) vec4<f32> {
    var normal = input.worldNormal;
    if (!is_front) {
        normal = -input.worldNormal;
    }
    let record = meshRecords[input.instanceIdx];
    let materialColor =
        SampleMaterialUnfilterable(
            record,
            input.textureCoordinate) *
        record.color;
    return ComputeLighting(
        input.instanceIdx,
        input.worldPosition,
        normal,
        materialColor);
}
