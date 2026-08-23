// Algorithm: Sample backdrop and optional noise plus either texture-mask or affine analytic rounded-mask coverage, then evaluate tint, luminosity, and material compositing.
// Time complexity: O(1) per vertex and fragment with a fixed sample footprint.
// Space complexity: O(1) local storage and bounded texture bandwidth; analytic rounded and uniform-opacity masks add no mask texture sample.
struct VSUniforms {
    projection: mat4x4<f32>,
    mvp: mat4x4<f32>,
    view: mat4x4<f32>,
};

@group(0) @binding(0) var<uniform> uniforms: VSUniforms;

struct MaterialUniforms {
    tintColor: vec4<f32>,
    luminosityColor: vec4<f32>,
    fallbackColor: vec4<f32>,
    noiseColor: vec4<f32>,
    material0: vec4<f32>,
    material1: vec4<f32>,
    geometry0: vec4<f32>,
    radiiX: vec4<f32>,
    radiiY: vec4<f32>,
    flags0: vec4<f32>,
    sourceUvRect: vec4<f32>,
};

@group(1) @binding(0) var<uniform> material: MaterialUniforms;

@group(2) @binding(0) var sourceSampler: sampler;
@group(2) @binding(1) var sourceTexture: texture_2d<f32>;

@group(3) @binding(0) var maskSampler: sampler;
@group(3) @binding(1) var maskTexture: texture_2d<f32>;

struct MaskSamplingUniforms {
    coordinate0: vec4<f32>,
    coordinate1: vec4<f32>,
    bounds: vec4<f32>,
    cornerRadiiX: vec4<f32>,
    cornerRadiiY: vec4<f32>,
    options: vec4<f32>,
};

@group(3) @binding(2) var<uniform> maskSampling: MaskSamplingUniforms;

fn rounded_mask_alpha_local(local: vec2<f32>, bounds: vec4<f32>, radiiX: vec4<f32>, radiiY: vec4<f32>) -> f32 {
	let edge = max(max(bounds.x - local.x, local.x - bounds.z), max(bounds.y - local.y, local.y - bounds.w));
	var center = vec2<f32>(0.0);
	var radius = vec2<f32>(0.0);
	var usesCorner = false;
	if (local.x < bounds.x + radiiX.x && local.y < bounds.y + radiiY.x) {
		radius = vec2<f32>(radiiX.x, radiiY.x);
		center = vec2<f32>(bounds.x + radius.x, bounds.y + radius.y);
		usesCorner = all(radius > vec2<f32>(0.0));
	} else if (local.x > bounds.z - radiiX.y && local.y < bounds.y + radiiY.y) {
		radius = vec2<f32>(radiiX.y, radiiY.y);
		center = vec2<f32>(bounds.z - radius.x, bounds.y + radius.y);
		usesCorner = all(radius > vec2<f32>(0.0));
	} else if (local.x > bounds.z - radiiX.z && local.y > bounds.w - radiiY.z) {
		radius = vec2<f32>(radiiX.z, radiiY.z);
		center = vec2<f32>(bounds.z - radius.x, bounds.w - radius.y);
		usesCorner = all(radius > vec2<f32>(0.0));
	} else if (local.x < bounds.x + radiiX.w && local.y > bounds.w - radiiY.w) {
		radius = vec2<f32>(radiiX.w, radiiY.w);
		center = vec2<f32>(bounds.x + radius.x, bounds.w - radius.y);
		usesCorner = all(radius > vec2<f32>(0.0));
	}
	let safeRadius = max(radius, vec2<f32>(0.000001));
	let ellipsePoint = (local - center) / safeRadius;
	let ellipse = dot(ellipsePoint, ellipsePoint) - 1.0;
	let implicit = select(edge, ellipse, usesCorner);
	let antialiasWidth = max(fwidth(implicit), 0.0001);
	return clamp(0.5 - implicit / antialiasWidth, 0.0, 1.0);
}

fn analytic_rounded_mask_alpha(position: vec2<f32>) -> f32 {
    let local = vec2<f32>(
        dot(vec3<f32>(position, 1.0), maskSampling.coordinate0.xyz),
        dot(vec3<f32>(position, 1.0), maskSampling.coordinate1.xyz));
	let outerAlpha = rounded_mask_alpha_local(local, maskSampling.bounds, maskSampling.cornerRadiiX, maskSampling.cornerRadiiY);
	if (maskSampling.options.x < 2.5) {
		return outerAlpha;
	}
	if (maskSampling.options.x > 3.5) {
		let innerAlpha = rounded_mask_alpha_local(
			local,
			vec4<f32>(maskSampling.coordinate0.w, maskSampling.coordinate1.w, maskSampling.options.z, maskSampling.options.w),
			vec4<f32>(0.0),
			vec4<f32>(0.0));
		return outerAlpha * (1.0 - innerAlpha);
	}
	let inset = maskSampling.options.z;
	let innerAlpha = rounded_mask_alpha_local(
		local,
		maskSampling.bounds + vec4<f32>(inset, inset, -inset, -inset),
		max(maskSampling.cornerRadiiX - vec4<f32>(inset), vec4<f32>(0.0)),
		max(maskSampling.cornerRadiiY - vec4<f32>(inset), vec4<f32>(0.0)));
	return outerAlpha * (1.0 - innerAlpha);
}

fn sample_mask_alpha(position: vec2<f32>) -> f32 {
    if (maskSampling.options.x < 0.5) {
        return 1.0;
    }
    if (maskSampling.options.x > 1.5) {
        return analytic_rounded_mask_alpha(position) *
            maskSampling.options.y;
    }
    let uv = (position - maskSampling.coordinate0.xy) * maskSampling.coordinate1.xy;
    let sampled = textureSample(maskTexture, maskSampler, clamp(uv, vec2<f32>(0.0), vec2<f32>(1.0))).r;
    let inside = all(uv >= vec2<f32>(0.0)) && all(uv <= vec2<f32>(1.0));
    return select(0.0, sampled, inside);
}

struct VertexInput {
    @location(0) position: vec2<f32>,
    @location(1) color: vec4<f32>,
    @location(2) texCoord: vec2<f32>,
};

struct VertexOutput {
    @builtin(position) position: vec4<f32>,
    @location(0) color: vec4<f32>,
    @location(1) texCoord: vec2<f32>,
};

@vertex
fn vs_main(input: VertexInput) -> VertexOutput {
    var output: VertexOutput;
    output.position = uniforms.projection * vec4<f32>(input.position, 0.0, 1.0);
    output.color = input.color;
    output.texCoord = input.texCoord;
    return output;
}

fn premultiply(color: vec4<f32>) -> vec4<f32> {
    return vec4<f32>(color.rgb * color.a, color.a);
}

fn source_over(destination: vec4<f32>, source: vec4<f32>) -> vec4<f32> {
    return source + destination * (1.0 - source.a);
}

fn blend_luminosity(color: vec3<f32>) -> f32 {
    return dot(color, vec3<f32>(0.3, 0.59, 0.11));
}

fn clip_blend_color(inputColor: vec3<f32>) -> vec3<f32> {
    var color = inputColor;
    let lightness = blend_luminosity(color);
    let minimum = min(min(color.r, color.g), color.b);
    let maximum = max(max(color.r, color.g), color.b);
    if (minimum < 0.0 && lightness > minimum) {
        color = vec3<f32>(lightness) +
            (color - vec3<f32>(lightness)) * lightness / (lightness - minimum);
    }
    if (maximum > 1.0 && maximum > lightness) {
        color = vec3<f32>(lightness) +
            (color - vec3<f32>(lightness)) * (1.0 - lightness) /
                (maximum - lightness);
    }
    return color;
}

fn set_blend_luminosity(color: vec3<f32>, lightness: f32) -> vec3<f32> {
    return clip_blend_color(
        color + vec3<f32>(lightness - blend_luminosity(color)));
}

fn blend_nonseparable_source_over(
    destination: vec4<f32>,
    sourceColor: vec4<f32>,
    useColorMode: bool) -> vec4<f32> {
    let source = premultiply(sourceColor);
    let sourceAlpha = source.a;
    let destinationAlpha = destination.a;
    let straightDestination = select(
        vec3<f32>(0.0),
        destination.rgb / destinationAlpha,
        destinationAlpha > 0.00001);
    var mixed = set_blend_luminosity(
        straightDestination,
        blend_luminosity(sourceColor.rgb));
    if (useColorMode) {
        mixed = set_blend_luminosity(
            sourceColor.rgb,
            blend_luminosity(straightDestination));
    }
    return vec4<f32>(
        source.rgb * (1.0 - destinationAlpha) +
            destination.rgb * (1.0 - sourceAlpha) +
            mixed * sourceAlpha * destinationAlpha,
        sourceAlpha + destinationAlpha - sourceAlpha * destinationAlpha);
}

fn sample_backdrop(uv: vec2<f32>) -> vec4<f32> {
    let blurRadius = max(material.material1.x, 0.0);
    if (blurRadius <= 0.01) {
        return textureSample(sourceTexture, sourceSampler, uv);
    }

    let textureSize = vec2<f32>(textureDimensions(sourceTexture));
    let texel = vec2<f32>(1.0) / max(textureSize, vec2<f32>(1.0));
    let inner = texel * blurRadius * 0.35;
    let outer = texel * blurRadius * 0.75;

    var color = textureSample(sourceTexture, sourceSampler, uv) * 0.20;
    color += textureSample(sourceTexture, sourceSampler, uv + vec2<f32>(inner.x, 0.0)) * 0.10;
    color += textureSample(sourceTexture, sourceSampler, uv - vec2<f32>(inner.x, 0.0)) * 0.10;
    color += textureSample(sourceTexture, sourceSampler, uv + vec2<f32>(0.0, inner.y)) * 0.10;
    color += textureSample(sourceTexture, sourceSampler, uv - vec2<f32>(0.0, inner.y)) * 0.10;
    color += textureSample(sourceTexture, sourceSampler, uv + inner) * 0.075;
    color += textureSample(sourceTexture, sourceSampler, uv - inner) * 0.075;
    color += textureSample(sourceTexture, sourceSampler, uv + vec2<f32>(inner.x, -inner.y)) * 0.075;
    color += textureSample(sourceTexture, sourceSampler, uv + vec2<f32>(-inner.x, inner.y)) * 0.075;
    color += textureSample(sourceTexture, sourceSampler, uv + vec2<f32>(outer.x, 0.0)) * 0.025;
    color += textureSample(sourceTexture, sourceSampler, uv - vec2<f32>(outer.x, 0.0)) * 0.025;
    color += textureSample(sourceTexture, sourceSampler, uv + vec2<f32>(0.0, outer.y)) * 0.025;
    color += textureSample(sourceTexture, sourceSampler, uv - vec2<f32>(0.0, outer.y)) * 0.025;
    return color;
}

fn ellipse_distance(point: vec2<f32>, center: vec2<f32>, radii: vec2<f32>) -> f32 {
    let safeRadii = max(radii, vec2<f32>(0.0001));
    return length((point - center) / safeRadii);
}

fn rounded_rect_distance(uv: vec2<f32>) -> f32 {
    let size = max(material.geometry0.xy, vec2<f32>(0.0001));
    let point = uv * size;
    let halfSize = size * 0.5;
    let radiiX = clamp(material.radiiX, vec4<f32>(0.0), vec4<f32>(halfSize.x));
    let radiiY = clamp(material.radiiY, vec4<f32>(0.0), vec4<f32>(halfSize.y));

    if (radiiX.x > 0.0 && radiiY.x > 0.0 && point.x < radiiX.x && point.y < radiiY.x) {
        return ellipse_distance(point, vec2<f32>(radiiX.x, radiiY.x), vec2<f32>(radiiX.x, radiiY.x));
    }
    if (radiiX.y > 0.0 && radiiY.y > 0.0 && point.x > size.x - radiiX.y && point.y < radiiY.y) {
        return ellipse_distance(point, vec2<f32>(size.x - radiiX.y, radiiY.y), vec2<f32>(radiiX.y, radiiY.y));
    }
    if (radiiX.z > 0.0 && radiiY.z > 0.0 && point.x > size.x - radiiX.z && point.y > size.y - radiiY.z) {
        return ellipse_distance(point, vec2<f32>(size.x - radiiX.z, size.y - radiiY.z), vec2<f32>(radiiX.z, radiiY.z));
    }
    if (radiiX.w > 0.0 && radiiY.w > 0.0 && point.x < radiiX.w && point.y > size.y - radiiY.w) {
        return ellipse_distance(point, vec2<f32>(radiiX.w, size.y - radiiY.w), vec2<f32>(radiiX.w, radiiY.w));
    }

    return 0.0;
}

fn random_noise(position: vec2<f32>) -> f32 {
    let value = dot(floor(position), vec2<f32>(12.9898, 78.233));
    return fract(sin(value) * 43758.5453);
}

@fragment
fn fs_main(input: VertexOutput) -> @location(0) vec4<f32> {
    let hasSource = material.flags0.x > 0.5;
    let hasMask = material.flags0.y > 0.5;
    let sourceIsPremultiplied = material.flags0.z > 0.5;
    let sourceIsCapturedHostBackdrop = material.flags0.w > 0.5;
    let useFallback = material.material1.w > 0.5;
    let kind = material.material1.z;

    var backdrop = vec4<f32>(0.0);
    var capturedDestination = vec4<f32>(0.0);
    if (hasSource) {
        let backdropUv = select(
            input.texCoord,
            input.position.xy / max(material.geometry0.zw, vec2<f32>(1.0)),
            sourceIsCapturedHostBackdrop);
        if (sourceIsCapturedHostBackdrop) {
            // Captured host textures are premultiplied. Preserve the exact
            // pre-material destination for coverage mixing below; the blurred
            // sample is the material input, not the uncovered output.
            capturedDestination = textureSample(
                sourceTexture,
                sourceSampler,
                backdropUv);
        }
        backdrop = sample_backdrop(backdropUv);
        if (sourceIsPremultiplied && backdrop.a > 0.00001) {
            backdrop = vec4<f32>(backdrop.rgb / backdrop.a, backdrop.a);
        }

        let luminance = dot(backdrop.rgb, vec3<f32>(0.2126, 0.7152, 0.0722));
        backdrop = vec4<f32>(
            mix(vec3<f32>(luminance), backdrop.rgb, max(material.material1.y, 0.0)),
            backdrop.a);
    }

    var result = premultiply(backdrop);
    if (useFallback || kind >= 3.5) {
        result = premultiply(material.fallbackColor);
    } else if (kind >= 2.5) {
        result = source_over(result, premultiply(vec4<f32>(
            material.tintColor.rgb,
            clamp(material.tintColor.a * material.material0.x, 0.0, 1.0))));
    } else if (kind < 1.5) {
        let luminosity = vec4<f32>(
            material.luminosityColor.rgb,
            clamp(material.luminosityColor.a * material.material0.y, 0.0, 1.0));
        let tint = vec4<f32>(
            material.tintColor.rgb,
            clamp(material.tintColor.a * material.material0.x, 0.0, 1.0));
        result = blend_nonseparable_source_over(result, luminosity, false);
        result = blend_nonseparable_source_over(result, tint, true);
    }

    let noiseOpacity = clamp(material.material0.w, 0.0, 1.0);
    if (noiseOpacity > 0.0001 && kind < 2.0) {
        let noise = random_noise(input.position.xy);
        let noiseSource = vec4<f32>(
            material.noiseColor.rgb * noise * noiseOpacity,
            noiseOpacity);
        result = source_over(result, noiseSource);
    }

    var maskAlpha = 1.0;
    if (hasMask) {
        maskAlpha = sample_mask_alpha(input.position.xy);
    }

    let sourceUvSize = max(material.sourceUvRect.zw - material.sourceUvRect.xy, vec2<f32>(0.0001));
    let localUv = (input.texCoord - material.sourceUvRect.xy) / sourceUvSize;
    let roundedDistance = rounded_rect_distance(localUv);
    // Derivatives must execute in uniform control flow. Interior pixels return
    // zero distance, while corner pixels carry the normalized ellipse distance.
    let antialias = max(fwidth(roundedDistance), 0.001);
    let roundedCoverage =
        1.0 - smoothstep(1.0 - antialias, 1.0 + antialias, roundedDistance);
    let coverage = roundedCoverage *
        input.color.a *
        maskAlpha *
        clamp(material.material0.z, 0.0, 1.0);
    if (sourceIsCapturedHostBackdrop) {
        // The render pipeline uses Src for captured host backdrops. Mix the
        // complete material result with the captured destination here so a
        // rounded or geometry-mask edge is preserved without source-over
        // blending the destination into itself a second time.
        return clamp(
            mix(capturedDestination, result, coverage),
            vec4<f32>(0.0),
            vec4<f32>(1.0));
    }
    return clamp(result * coverage, vec4<f32>(0.0), vec4<f32>(1.0));
}
