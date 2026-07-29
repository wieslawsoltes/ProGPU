// Algorithm: select one decoder-owned external OES sample or a uniform solid color, apply fused Rec.709 saturation then grayscale transforms, and write encoder-surface RGBA.
// Time complexity: O(1) per output pixel with zero or one texture sample, two dot products, and two linear interpolations.
// Space complexity: O(1) private shader storage, at most one texture sample per pixel, and one output color; total bandwidth is O(W*H).
// OpenGL ES 2.0 fragment module. Saturation and grayscale are clamped by the
// host to [0,1]. The uniform branch is constant for the whole draw. Decoder/
// encoder surfaces own format conversion; no CPU pixel readback or upload is
// performed.
#extension GL_OES_EGL_image_external : require
precision mediump float;

uniform samplerExternalOES u_source;
uniform float u_saturation;
uniform float u_grayscale;
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
    const vec3 luminance_weights =
        vec3(0.2126, 0.7152, 0.0722);
    float source_luminance =
        dot(sampled.rgb, luminance_weights);
    vec3 saturated =
        mix(
            vec3(source_luminance),
            sampled.rgb,
            u_saturation);
    float result_luminance =
        dot(saturated, luminance_weights);
    vec3 processed =
        mix(
            saturated,
            vec3(result_luminance),
            u_grayscale);
    gl_FragColor = vec4(processed, sampled.a);
}
