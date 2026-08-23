// Algorithm: Convert a physical framebuffer position into either bounded texture UVs or affine rounded-rectangle local coordinates and evaluate anti-aliased coverage.
// Time complexity: O(1), with one texture sample for texture masks or fixed derivative arithmetic for analytic rounded and uniform-opacity masks.
// Space complexity: O(1) shader-local storage.
fn wpf_rounded_mask_alpha_local(local: vec2<f32>, bounds: vec4<f32>, radiiX: vec4<f32>, radiiY: vec4<f32>) -> f32 {
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

fn wpf_analytic_rounded_mask_alpha(position: vec2<f32>) -> f32 {
    let local = vec2<f32>(
        dot(vec3<f32>(position, 1.0), activeMaskSampling.coordinate0.xyz),
        dot(vec3<f32>(position, 1.0), activeMaskSampling.coordinate1.xyz));
	let outerAlpha = wpf_rounded_mask_alpha_local(local, activeMaskSampling.bounds, activeMaskSampling.cornerRadiiX, activeMaskSampling.cornerRadiiY);
	if (activeMaskSampling.options.x < 2.5) {
		return outerAlpha;
	}
	if (activeMaskSampling.options.x > 3.5) {
		let innerAlpha = wpf_rounded_mask_alpha_local(
			local,
			vec4<f32>(activeMaskSampling.coordinate0.w, activeMaskSampling.coordinate1.w, activeMaskSampling.options.z, activeMaskSampling.options.w),
			vec4<f32>(0.0),
			vec4<f32>(0.0));
		return outerAlpha * (1.0 - innerAlpha);
	}
	let inset = activeMaskSampling.options.z;
	let innerAlpha = wpf_rounded_mask_alpha_local(
		local,
		activeMaskSampling.bounds + vec4<f32>(inset, inset, -inset, -inset),
		max(activeMaskSampling.cornerRadiiX - vec4<f32>(inset), vec4<f32>(0.0)),
		max(activeMaskSampling.cornerRadiiY - vec4<f32>(inset), vec4<f32>(0.0)));
	return outerAlpha * (1.0 - innerAlpha);
}

fn wpf_active_mask_alpha(screenPosition: vec4<f32>) -> f32 {
    if (activeMaskSampling.options.x < 0.5) {
        return 1.0;
    }

    if (activeMaskSampling.options.x > 1.5) {
        return wpf_analytic_rounded_mask_alpha(screenPosition.xy) *
            activeMaskSampling.options.y;
    }
    let screenUv = (screenPosition.xy - activeMaskSampling.coordinate0.xy) *
        activeMaskSampling.coordinate1.xy;
    let sampled = textureSample(
        activeMaskTexture,
        activeMaskSampler,
        clamp(screenUv, vec2<f32>(0.0), vec2<f32>(1.0))).r;
    let inside = all(screenUv >= vec2<f32>(0.0)) && all(screenUv <= vec2<f32>(1.0));
    return select(0.0, sampled, inside);
}
