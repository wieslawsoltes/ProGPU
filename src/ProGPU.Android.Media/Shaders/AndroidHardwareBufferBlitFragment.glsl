// Algorithm: Sample one EGLImage-backed RGBA AHardwareBuffer texture and write it to the MediaCodec encoder surface.
// Time complexity: O(1) per fragment and O(P) over P encoder-surface pixels.
// Space complexity: O(1) private storage with one texture sample and one output write per fragment.
// Color processing is deliberately absent: the preceding WebGPU pass owns
// effects, scaling, and composition, so this is one terminal GPU blit.
precision mediump float;

uniform sampler2D u_source;
varying vec2 v_tex_coord;

void main()
{
    gl_FragColor = texture2D(u_source, v_tex_coord);
}
