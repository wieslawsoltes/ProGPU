// Algorithm: Draw one retained fullscreen triangle strip and forward normalized AHardwareBuffer texture coordinates.
// Time complexity: O(1) per vertex with exactly four vertices.
// Space complexity: O(1) private storage and four interpolated UV pairs.
// The WebGPU pass already applies media effects. This terminal EGL pass only
// transfers the completed RGBA target into the timestamped encoder surface.
attribute vec2 a_position;
attribute vec2 a_tex_coord;
varying vec2 v_tex_coord;

void main()
{
    gl_Position = vec4(a_position, 0.0, 1.0);
    v_tex_coord = a_tex_coord;
}
