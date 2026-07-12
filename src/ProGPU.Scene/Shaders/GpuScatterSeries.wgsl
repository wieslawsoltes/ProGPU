// Algorithm: Expand chart-point instances into marker quads and shade anti-aliased circular coverage.
// Time complexity: O(1) per vertex and fragment.
// Space complexity: O(1) local storage.
struct VSUniforms {
  transform: mat4x4<f32>,
  viewportPx: vec2<f32>,
  _pad0: vec2<f32>,
  scale: vec2<f32>,
  translate: vec2<f32>,
};

@group(0) @binding(0) var<uniform> vsUniforms: VSUniforms;

struct FSUniforms {
  color: vec4<f32>,
};

@group(0) @binding(1) var<uniform> fsUniforms: FSUniforms;

struct VSIn {
  @location(0) center: vec2<f32>,
  @location(1) radiusPx: f32,
};

struct VSOut {
  @builtin(position) clipPosition: vec4<f32>,
  @location(0) localPx: vec2<f32>,
  @location(1) radiusPx: f32,
};

fn quadCorner(vid : u32) -> vec2<f32> {
  switch (vid) {
    case 0u: { return vec2<f32>(-1.0, -1.0); }
    case 1u: { return vec2<f32>( 1.0, -1.0); }
    case 2u: { return vec2<f32>(-1.0,  1.0); }
    case 3u: { return vec2<f32>(-1.0,  1.0); }
    case 4u: { return vec2<f32>( 1.0, -1.0); }
    default: { return vec2<f32>( 1.0,  1.0); }
  }
}

@vertex
fn vs_main(in: VSIn, @builtin(vertex_index) vertexIndex: u32) -> VSOut {
  let corner = quadCorner(vertexIndex);
  let localPx = corner * in.radiusPx;

  let localClip = localPx * (2.0 / vsUniforms.viewportPx);
  let centerScaled = in.center * vsUniforms.scale + vsUniforms.translate;
  let centerClip = (vsUniforms.transform * vec4<f32>(centerScaled, 0.0, 1.0)).xy;

  var out: VSOut;
  out.clipPosition = vec4<f32>(centerClip + localClip, 0.0, 1.0);
  out.localPx = localPx;
  out.radiusPx = in.radiusPx;
  return out;
}

@fragment
fn fs_main(in: VSOut) -> @location(0) vec4<f32> {
  let dist = length(in.localPx) - in.radiusPx;
  let w = fwidth(dist);
  let a = 1.0 - smoothstep(0.0, w, dist);

  if (a <= 0.0) {
    discard;
  }

  return vec4<f32>(fsUniforms.color.rgb, fsUniforms.color.a * a);
}
