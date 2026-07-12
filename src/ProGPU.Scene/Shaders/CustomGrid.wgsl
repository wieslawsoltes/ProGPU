// Algorithm: Transform a fullscreen grid quad and shade major/minor procedural grid lines from world coordinates.
// Time complexity: O(1) per vertex and fragment.
// Space complexity: O(1) local storage.
struct VertexOutput {
    @builtin(position) position: vec4<f32>,
    @location(0) texCoord: vec2<f32>,
    @location(1) color: vec4<f32>,
};

@vertex
fn vs_main(
    @location(0) position: vec2<f32>,
    @location(1) color: vec4<f32>,
    @location(2) texCoord: vec2<f32>,
) -> VertexOutput {
    var output: VertexOutput;
    output.position = vec4<f32>(position, 0.0, 1.0);
    output.texCoord = texCoord;
    output.color = color;
    return output;
}

@fragment
fn fs_main(input: VertexOutput) -> @location(0) vec4<f32> {
    // Modern glassmorphic background
    let bgColor = vec4<f32>(0.05, 0.05, 0.08, 0.95);

    // Grid coordinates
    let gridCount = 25.0;
    let grid = abs(fract(input.texCoord * gridCount - 0.5) - 0.5) / fwidth(input.texCoord * gridCount);
    let lineVal = min(grid.x, grid.y);

    // Smooth grid lines
    let lineAlpha = 1.0 - min(lineVal, 1.0);
    let lineColor = vec4<f32>(0.0, 0.6, 0.8, 0.15 * lineAlpha);

    // Glowing intersections
    let distToIntersection = length(fract(input.texCoord * gridCount - 0.5) - 0.5);
    let glow = exp(-distToIntersection * 12.0) * 0.4;
    let glowColor = vec4<f32>(0.0, 0.8, 1.0, glow);

    // Subtle pulsing center radial glow
    let centerDist = distance(input.texCoord, vec2<f32>(0.5, 0.5));
    let centerGlow = exp(-centerDist * 2.5) * 0.2;
    let centerGlowColor = vec4<f32>(0.0, 0.5, 0.8, centerGlow);

    var finalColor = mix(bgColor, lineColor, lineAlpha);
    finalColor = finalColor + glowColor + centerGlowColor;

    return finalColor;
}
