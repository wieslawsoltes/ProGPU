// Algorithm: Generate one fullscreen-adjacent triangle from vertex indices and shade it with an interpolated premultiplied color gradient.
// Time complexity: O(1) per vertex and O(1) per covered fragment; three vertex invocations and one color write per covered sample.
// Space complexity: O(1) private storage with no buffers, textures, or auxiliary output storage; framebuffer bandwidth is one RGBA write per covered sample.
struct VertexOutput {
    @builtin(position) position: vec4f,
    @location(0) color: vec3f,
};

@vertex
fn vsMain(@builtin(vertex_index) vertexIndex: u32) -> VertexOutput {
    var positions = array<vec2f, 3>(
        vec2f(-0.68, -0.58),
        vec2f(0.68, -0.58),
        vec2f(0.0, 0.68)
    );
    var colors = array<vec3f, 3>(
        vec3f(0.22, 0.48, 1.0),
        vec3f(0.58, 0.25, 1.0),
        vec3f(0.10, 0.91, 0.78)
    );

    var output: VertexOutput;
    output.position = vec4f(positions[vertexIndex], 0.0, 1.0);
    output.color = colors[vertexIndex];
    return output;
}

@fragment
fn fsMain(input: VertexOutput) -> @location(0) vec4f {
    return vec4f(input.color, 1.0);
}
