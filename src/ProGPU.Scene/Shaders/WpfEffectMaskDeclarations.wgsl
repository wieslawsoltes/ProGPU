// Algorithm: Declare the optional active-mask texture and its bounded screen-space sampling transform.
// Time complexity: O(1) per shader invocation.
// Space complexity: O(1) uniform state and one bound texture/sampler pair.
@group(3) @binding(0) var activeMaskSampler: sampler;
@group(3) @binding(1) var activeMaskTexture: texture_2d<f32>;

struct ActiveMaskSamplingUniforms {
    origin: vec2<f32>,
    inverseSize: vec2<f32>,
    options: vec4<f32>,
};

@group(3) @binding(2) var<uniform> activeMaskSampling: ActiveMaskSamplingUniforms;
