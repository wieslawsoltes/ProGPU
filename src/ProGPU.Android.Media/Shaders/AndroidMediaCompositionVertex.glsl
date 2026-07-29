// Algorithm: transform one retained full-screen quad and the decoder SurfaceTexture coordinates into encoder-surface clip space.
// Time complexity: O(1) per vertex with exactly four vertices and one 4x4 texture-coordinate transform.
// Space complexity: O(1) private shader storage and no output storage beyond four interpolated UV pairs.
// OpenGL ES 2.0 vertex module. The fixed draw is a triangle strip with four
// vertices; SurfaceTexture supplies the crop/rotation transform.
attribute vec2 a_position;
attribute vec2 a_tex_coord;
uniform mat4 u_tex_transform;
varying vec2 v_tex_coord;

void main()
{
    gl_Position = vec4(a_position, 0.0, 1.0);
    vec4 transformed =
        u_tex_transform * vec4(a_tex_coord, 0.0, 1.0);
    v_tex_coord = transformed.xy;
}
