// Algorithm: Convert a physical framebuffer position into either bounded texture UVs or affine rounded-rectangle local coordinates and evaluate anti-aliased coverage.
// Time complexity: O(1), with one texture sample for texture masks or fixed derivative arithmetic for analytic rounded and uniform-opacity masks.
// Space complexity: O(1) shader-local storage.
fn wpf_analytic_rounded_mask_alpha(position: vec2<f32>) -> f32 {
    let local = vec2<f32>(
        dot(vec3<f32>(position, 1.0), activeMaskSampling.coordinate0.xyz),
        dot(vec3<f32>(position, 1.0), activeMaskSampling.coordinate1.xyz));
    let bounds = activeMaskSampling.bounds;
    let edge = max(max(bounds.x - local.x, local.x - bounds.z), max(bounds.y - local.y, local.y - bounds.w));
    var center = vec2<f32>(0.0);
    var radius = vec2<f32>(0.0);
    var usesCorner = false;
    if (local.x < bounds.x + activeMaskSampling.cornerRadiiX.x && local.y < bounds.y + activeMaskSampling.cornerRadiiY.x) {
        radius = vec2<f32>(activeMaskSampling.cornerRadiiX.x, activeMaskSampling.cornerRadiiY.x);
        center = vec2<f32>(bounds.x + radius.x, bounds.y + radius.y);
        usesCorner = all(radius > vec2<f32>(0.0));
    } else if (local.x > bounds.z - activeMaskSampling.cornerRadiiX.y && local.y < bounds.y + activeMaskSampling.cornerRadiiY.y) {
        radius = vec2<f32>(activeMaskSampling.cornerRadiiX.y, activeMaskSampling.cornerRadiiY.y);
        center = vec2<f32>(bounds.z - radius.x, bounds.y + radius.y);
        usesCorner = all(radius > vec2<f32>(0.0));
    } else if (local.x > bounds.z - activeMaskSampling.cornerRadiiX.z && local.y > bounds.w - activeMaskSampling.cornerRadiiY.z) {
        radius = vec2<f32>(activeMaskSampling.cornerRadiiX.z, activeMaskSampling.cornerRadiiY.z);
        center = vec2<f32>(bounds.z - radius.x, bounds.w - radius.y);
        usesCorner = all(radius > vec2<f32>(0.0));
    } else if (local.x < bounds.x + activeMaskSampling.cornerRadiiX.w && local.y > bounds.w - activeMaskSampling.cornerRadiiY.w) {
        radius = vec2<f32>(activeMaskSampling.cornerRadiiX.w, activeMaskSampling.cornerRadiiY.w);
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
