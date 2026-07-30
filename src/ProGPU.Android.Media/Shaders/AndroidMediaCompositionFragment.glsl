// Algorithm: select one decoder-owned external OES sample or a uniform solid color, apply one fused affine straight-RGB transform, preserve alpha, and write encoder-surface RGBA.
// Time complexity: O(1) per output pixel with zero or one texture sample and three four-term dot products.
// Space complexity: O(1) private shader storage, at most one texture sample per pixel, and one output color; total bandwidth is O(W*H).
// OpenGL ES 2.0 fragment module. The uniform branch and three transform rows
// are constant for the whole draw. The terminal encoder surface clamps the
// result to its representable format; no intermediate effect clamp is added.
// Decoder/encoder surfaces own format conversion, so no CPU pixel readback or
// upload is performed.
#extension GL_OES_EGL_image_external : require
precision mediump float;

uniform samplerExternalOES u_source;
uniform vec4 u_red_transform;
uniform vec4 u_green_transform;
uniform vec4 u_blue_transform;
uniform float u_use_solid_color;
uniform vec4 u_solid_color;
varying vec2 v_tex_coord;

void main()
{
    vec4 sampled;
    if (u_use_solid_color > 0.5)
    {
        sampled = u_solid_color;
    }
    else
    {
        sampled = texture2D(u_source, v_tex_coord);
    }
    vec4 affine_input =
        vec4(sampled.rgb, 1.0);
    vec3 processed =
        vec3(
            dot(u_red_transform, affine_input),
            dot(u_green_transform, affine_input),
            dot(u_blue_transform, affine_input));
    gl_FragColor = vec4(processed, sampled.a);
}
