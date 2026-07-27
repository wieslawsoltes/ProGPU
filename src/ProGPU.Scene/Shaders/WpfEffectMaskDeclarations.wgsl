// Algorithm: Declare the optional active-mask texture or affine analytic rounded-rectangle contract.
// Time complexity: O(1) per shader invocation.
// Space complexity: O(1) uniform state and one bound texture/sampler pair; analytic masks do not sample it.
@group(3) @binding(0) var activeMaskSampler: sampler;
@group(3) @binding(1) var activeMaskTexture: texture_2d<f32>;

struct ActiveMaskSamplingUniforms {
    coordinate0: vec4<f32>,
    coordinate1: vec4<f32>,
    bounds: vec4<f32>,
    cornerRadiiX: vec4<f32>,
    cornerRadiiY: vec4<f32>,
    options: vec4<f32>,
};

@group(3) @binding(2) var<uniform> activeMaskSampling: ActiveMaskSamplingUniforms;
