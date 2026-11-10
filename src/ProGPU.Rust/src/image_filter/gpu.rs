//! Original image-filter kernels, exact pass ordering and retained GPU ownership.
//! Preparation copies parameters, never image pixels. Encoding does not submit.
//!
//! Direct original ProGPU source provenance (SHA-256, 2026-11-09):
//! - `ProGPU.Compute/ComputeAccelerator.cs` structs 215–493, methods 838–1318:
//!   `7e3cd2be4a1f02cfe44f9b784759cf5ae5677f089c5f7c5c2db7e2a4b28c8ea1`.
//! - `ProGPU.Backend/GpuTexture.cs` original complete `CopyFrom` contract:
//!   `0b5e89412acd16e86ae2f86c7e85e8bfa880b85dea196a79c2014cc1928bdb25`.
//! - `ProGPU.Compute/Shaders/Morphology.wgsl`:
//!   `08739b716dc7aed05a5be079882b0812ca84122266bd46700c4d2cb51967787e`.
//! - `ProGPU.Compute/Shaders/MatrixConvolution.wgsl`:
//!   `9efcbdcfbbd74e48859d8d0b015bc23c5cfd211fd1fcd83586f306fcff25726b`.
//! - `ProGPU.Compute/Shaders/DisplacementMap.wgsl`:
//!   `b771c27cf1ab9d9e07b51b86108f96cae53b9b8414e9e46a58b555aa3bcd1018`.
//! - `ProGPU.Compute/Shaders/Magnifier.wgsl`:
//!   `3f7b37719e19940b70bb535cde3fd6da72f849d7d4f33fd20b3a1005fdf52080`.
//! - `ProGPU.Compute/Shaders/ImageLighting.wgsl`:
//!   `1632a4803ee654cc4943720d4e08716c7e856409861f5a74543585dc1badefe2`.
//!
//! Kernels remain unchanged. Their work is GPU-parallel: morphology O(W*H*R)
//! over two passes, convolution O(W*H*Kw*Kh), and other kernels O(W*H), with
//! bounded per-pixel shader-local storage. CPU preparation owns fixed metadata
//! and copies the original convolution prefix in O(Kw*Kh); it evaluates no pixels.
//! Validation/benchmarks remain deferred until the mandated port-first phase ends.
use super::{
    DisplacementMapParams, ImageLightingParams, MagnifierParams, MatrixConvolutionParams,
    MorphologyParams,
};
use crate::{
    Error,
    image::{GpuImage, ImageAlpha},
    path::{ExecutionPath, ExecutionPreference},
};
use std::sync::Arc;
use wgpu::util::DeviceExt;

const MORPHOLOGY: &str = include_str!("../../../ProGPU.Compute/Shaders/Morphology.wgsl");
const CONVOLUTION: &str = include_str!("../../../ProGPU.Compute/Shaders/MatrixConvolution.wgsl");
const DISPLACEMENT: &str = include_str!("../../../ProGPU.Compute/Shaders/DisplacementMap.wgsl");
const MAGNIFIER: &str = include_str!("../../../ProGPU.Compute/Shaders/Magnifier.wgsl");
const LIGHTING: &str = include_str!("../../../ProGPU.Compute/Shaders/ImageLighting.wgsl");

/// One complete original compute request. Extents are actual physical texels;
/// graph bounds/placement and parameter construction remain separate policies.
pub enum ImageFilterComputeRequest<'a> {
    /// Original horizontal then vertical morphology. Both passes execute unless
    /// both radii are zero, when the original complete texture copy is used.
    Morphology {
        /// Actual same-device premultiplied source.
        source: &'a GpuImage,
        /// Exact horizontal [1,0] and vertical [0,1] parameter blocks.
        parameters: [MorphologyParams; 2],
    },
    /// Original 1x1 through 64x64 convolution. Excess kernel values are ignored.
    Convolution {
        /// Actual same-device premultiplied source.
        source: &'a GpuImage,
        /// Original tile rectangle, offsets, gain/bias and alpha policy.
        parameters: &'a MatrixConvolutionParams,
        /// Complete original kernel; only the declared prefix is uploaded.
        kernel: &'a [f32],
    },
    /// Original two-input displacement; displacement dimensions do not enlarge output.
    Displacement {
        /// Actual same-device premultiplied color source.
        source: &'a GpuImage,
        /// Independently sized same-device premultiplied displacement map.
        displacement: &'a GpuImage,
        /// Original XY transform and selected straight-color channels.
        parameters: &'a DisplacementMapParams,
        /// Original preexisting destination extent, used as a minimum. Zero
        /// lanes request the corresponding source extent without enlargement.
        requested_size: [u32; 2],
    },
    /// Original bounded nearest/linear/cubic magnifier shader.
    Magnifier {
        /// Actual same-device premultiplied source.
        source: &'a GpuImage,
        /// Original lens/output rectangles, zoom transform and inverse inset.
        parameters: &'a MagnifierParams,
    },
    /// Original distant/point/spot diffuse or specular height-field lighting.
    Lighting {
        /// Actual same-device premultiplied height source.
        source: &'a GpuImage,
        /// Original light, surface and mode parameter block.
        parameters: &'a ImageLightingParams,
    },
}

/// Actual allocations, parameter payload and work per encoding. Zero-radius
/// morphology reports copies separately and does not claim compute dispatches.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct ImageFilterComputeMetrics {
    /// Original ordered compute passes, zero for a complete-copy operation.
    pub compute_passes: u32,
    /// Actual immutable uniform payload bytes across all passes.
    pub parameter_bytes: u64,
    /// Actual uploaded original convolution prefix bytes.
    pub kernel_bytes: u64,
    /// Private output/intermediate texture generations.
    pub texture_allocations: u32,
    /// Actual texel storage, including every mip of a complete texture copy.
    pub texture_bytes: u64,
    /// Total original 16-by-16 workgroups across compute passes.
    pub workgroups: u64,
    /// Exact GPU copy bytes for the original zero-radius morphology route.
    pub texture_copy_bytes: u64,
}
impl ImageFilterComputeMetrics {
    /// Combine complete graph inventories with atomic overflow rejection.
    pub(crate) fn merge(&mut self, value: Self) -> Result<(), Error> {
        let merged = Self {
            compute_passes: self
                .compute_passes
                .checked_add(value.compute_passes)
                .ok_or(Error::Capacity)?,
            parameter_bytes: self
                .parameter_bytes
                .checked_add(value.parameter_bytes)
                .ok_or(Error::Capacity)?,
            kernel_bytes: self
                .kernel_bytes
                .checked_add(value.kernel_bytes)
                .ok_or(Error::Capacity)?,
            texture_allocations: self
                .texture_allocations
                .checked_add(value.texture_allocations)
                .ok_or(Error::Capacity)?,
            texture_bytes: self
                .texture_bytes
                .checked_add(value.texture_bytes)
                .ok_or(Error::Capacity)?,
            workgroups: self
                .workgroups
                .checked_add(value.workgroups)
                .ok_or(Error::Capacity)?,
            texture_copy_bytes: self
                .texture_copy_bytes
                .checked_add(value.texture_copy_bytes)
                .ok_or(Error::Capacity)?,
        };
        *self = merged;
        Ok(())
    }
}
#[derive(Clone, Copy)]
enum Kernel {
    Morphology,
    Convolution,
    Displacement,
    Magnifier,
    Lighting,
}
impl Kernel {
    fn index(self) -> usize {
        self as usize
    }
    fn source(self) -> &'static str {
        match self {
            Self::Morphology => MORPHOLOGY,
            Self::Convolution => CONVOLUTION,
            Self::Displacement => DISPLACEMENT,
            Self::Magnifier => MAGNIFIER,
            Self::Lighting => LIGHTING,
        }
    }
    fn input_count(self) -> u32 {
        if matches!(self, Self::Displacement) {
            2
        } else {
            1
        }
    }
    fn parameter_size(self) -> u64 {
        match self {
            Self::Morphology => 16,
            Self::Convolution => 48,
            Self::Displacement => 32,
            _ => 80,
        }
    }
    fn has_kernel(self) -> bool {
        matches!(self, Self::Convolution)
    }
}
#[derive(Clone)]
struct Pipeline {
    pipeline: wgpu::ComputePipeline,
    layout: wgpu::BindGroupLayout,
}
struct Dispatch {
    pipeline: wgpu::ComputePipeline,
    binding: wgpu::BindGroup,
}
struct Plan<'a> {
    source: &'a GpuImage,
    displacement: Option<&'a GpuImage>,
    kernel: Option<Kernel>,
    parameters: Vec<Vec<u8>>,
    weights: &'a [f32],
    size: [u32; 2],
    workgroups: [u32; 2],
    format: wgpu::TextureFormat,
    mip_levels: u32,
    metrics: ImageFilterComputeMetrics,
}

/// Immutable original parameters and owned source/output resources. Encoding
/// reads current input texels and rewrites this private output; an externally
/// mutable source is not silently turned into an immutable pixel snapshot.
pub struct PreparedImageFilterCompute {
    owner: Arc<()>,
    dispatches: Vec<Dispatch>,
    inputs: Vec<GpuImage>,
    intermediate: Option<GpuImage>,
    output: GpuImage,
    workgroups: [u32; 2],
    copy_mips: u32,
    metrics: ImageFilterComputeMetrics,
}
impl PreparedImageFilterCompute {
    /// Original source followed by displacement input, when present.
    pub fn inputs(&self) -> &[GpuImage] {
        &self.inputs
    }
    /// Actual private intermediate, present only for nonzero morphology.
    pub fn intermediate(&self) -> Option<&GpuImage> {
        self.intermediate.as_ref()
    }
    /// Actual output resource. Encode and submit before consuming its texels.
    /// Compute operations output RGBA8; complete-copy operations retain source format.
    pub fn output(&self) -> &GpuImage {
        &self.output
    }
    /// Exact preparation allocation and per-encode work inventory.
    pub fn metrics(&self) -> ImageFilterComputeMetrics {
        self.metrics
    }
    /// Selected original kernel route; zero-radius morphology uses its explicit copy branch.
    pub fn execution_path(&self) -> ExecutionPath {
        ExecutionPath::NativeCompute
    }
    /// Replay independently of the renderer's replaceable engine cache.
    pub(crate) fn encode_owned(&self, encoder: &mut wgpu::CommandEncoder) {
        if self.copy_mips != 0 {
            let source = self.inputs[0].texture();
            for mip in 0..self.copy_mips {
                encoder.copy_texture_to_texture(
                    wgpu::TexelCopyTextureInfo {
                        texture: source,
                        mip_level: mip,
                        origin: wgpu::Origin3d::ZERO,
                        aspect: wgpu::TextureAspect::All,
                    },
                    wgpu::TexelCopyTextureInfo {
                        texture: self.output.texture(),
                        mip_level: mip,
                        origin: wgpu::Origin3d::ZERO,
                        aspect: wgpu::TextureAspect::All,
                    },
                    wgpu::Extent3d {
                        width: (source.width() >> mip).max(1),
                        height: (source.height() >> mip).max(1),
                        depth_or_array_layers: 1,
                    },
                );
            }
        }
        for dispatch in &self.dispatches {
            let mut pass = encoder.begin_compute_pass(&wgpu::ComputePassDescriptor {
                label: Some("ProGPU original retained image-filter pass"),
                timestamp_writes: None,
            });
            pass.set_pipeline(&dispatch.pipeline);
            pass.set_bind_group(0, &dispatch.binding, &[]);
            pass.dispatch_workgroups(self.workgroups[0], self.workgroups[1], 1);
        }
    }
}

/// Lazy same-device execution of the five original image-filter kernels.
/// Construction selects a typed GPU-first route; unsupported forced execution
/// rejects rather than selecting an implicit scalar or CPU pixel evaluator.
pub struct ImageFilterComputeEngine {
    owner: Arc<()>,
    device: wgpu::Device,
    pipelines: [Option<Pipeline>; 5],
}
impl ImageFilterComputeEngine {
    /// Retain actual device ownership without allocating textures or pipelines.
    pub fn new(device: &wgpu::Device, preference: ExecutionPreference) -> Result<Self, Error> {
        if !matches!(
            preference,
            ExecutionPreference::Automatic | ExecutionPreference::NativeCompute
        ) {
            return Err(Error::UnsupportedExecution);
        }
        Ok(Self {
            owner: Arc::new(()),
            device: device.clone(),
            pipelines: std::array::from_fn(|_| None),
        })
    }
    /// Selected original compute route, separate from adapter performance evidence.
    pub fn execution_path(&self) -> ExecutionPath {
        ExecutionPath::NativeCompute
    }
    /// Only actually requested non-copy kernels initialize a pipeline.
    pub fn initialized_pipeline_count(&self) -> usize {
        self.pipelines.iter().filter(|p| p.is_some()).count()
    }
    /// Complete original parameter, dependency, output-size and actual-device
    /// capacity admission, with no GPU mutation. Useful for whole-graph preflight.
    pub fn preflight(
        device: &wgpu::Device,
        request: &ImageFilterComputeRequest<'_>,
    ) -> Result<ImageFilterComputeMetrics, Error> {
        Ok(plan(device, request)?.metrics)
    }
    /// Prepare the complete typed operation with private output, parameters,
    /// optional intermediate and exact original dependency ordering.
    pub async fn prepare(
        &mut self,
        request: ImageFilterComputeRequest<'_>,
    ) -> Result<PreparedImageFilterCompute, Error> {
        let plan = plan(&self.device, &request)?;
        if let Some(kernel) = plan.kernel
            && self.pipelines[kernel.index()].is_none()
        {
            let scope = self.device.push_error_scope(wgpu::ErrorFilter::Validation);
            let pipeline = create_pipeline(&self.device, kernel);
            if let Some(error) = scope.pop().await {
                return Err(error.into());
            }
            self.pipelines[kernel.index()] = Some(pipeline);
        }
        let scope = self.device.push_error_scope(wgpu::ErrorFilter::Validation);
        let prepared = self.allocate(plan);
        if let Some(error) = scope.pop().await {
            return Err(error.into());
        }
        prepared
    }
    /// Original two-pass morphology. Parameter directions must be horizontal
    /// then vertical, with matching dilation policy. No-op is a real full copy.
    pub async fn prepare_morphology(
        &mut self,
        source: &GpuImage,
        parameters: [MorphologyParams; 2],
    ) -> Result<PreparedImageFilterCompute, Error> {
        self.prepare(ImageFilterComputeRequest::Morphology { source, parameters })
            .await
    }
    /// Original convolution; uploads only the declared prefix, preserving raw
    /// weight bits, original gain/bias and independent tiling rectangle.
    pub async fn prepare_convolution(
        &mut self,
        source: &GpuImage,
        parameters: &MatrixConvolutionParams,
        kernel: &[f32],
    ) -> Result<PreparedImageFilterCompute, Error> {
        self.prepare(ImageFilterComputeRequest::Convolution {
            source,
            parameters,
            kernel,
        })
        .await
    }
    /// Original displacement with output=max(requested destination, source).
    /// The displacement texture can be a different size and never enlarges output.
    pub async fn prepare_displacement(
        &mut self,
        source: &GpuImage,
        displacement: &GpuImage,
        parameters: &DisplacementMapParams,
        requested_size: [u32; 2],
    ) -> Result<PreparedImageFilterCompute, Error> {
        self.prepare(ImageFilterComputeRequest::Displacement {
            source,
            displacement,
            parameters,
            requested_size,
        })
        .await
    }
    /// Original magnifier, always producing the source's complete extent.
    pub async fn prepare_magnifier(
        &mut self,
        source: &GpuImage,
        parameters: &MagnifierParams,
    ) -> Result<PreparedImageFilterCompute, Error> {
        self.prepare(ImageFilterComputeRequest::Magnifier { source, parameters })
            .await
    }
    /// Original lighting, preserving all five vec4 parameter records.
    pub async fn prepare_lighting(
        &mut self,
        source: &GpuImage,
        parameters: &ImageLightingParams,
    ) -> Result<PreparedImageFilterCompute, Error> {
        self.prepare(ImageFilterComputeRequest::Lighting { source, parameters })
            .await
    }
    fn allocate(&self, plan: Plan<'_>) -> Result<PreparedImageFilterCompute, Error> {
        let output = allocate_texture(
            &self.device,
            plan.size,
            plan.format,
            plan.mip_levels,
            plan.kernel.is_some(),
        )?;
        let intermediate = if plan.parameters.len() == 2 {
            Some(allocate_texture(
                &self.device,
                plan.size,
                wgpu::TextureFormat::Rgba8Unorm,
                1,
                true,
            )?)
        } else {
            None
        };
        let mut dispatches = Vec::with_capacity(plan.parameters.len());
        if let Some(kernel) = plan.kernel {
            let pipeline = self.pipelines[kernel.index()]
                .as_ref()
                .ok_or(Error::ResourceDomain)?;
            for (index, bytes) in plan.parameters.iter().enumerate() {
                let input = if index == 0 {
                    plan.source
                } else {
                    intermediate.as_ref().ok_or(Error::ResourceDomain)?
                };
                let destination = if index + 1 == plan.parameters.len() {
                    &output
                } else {
                    intermediate.as_ref().ok_or(Error::ResourceDomain)?
                };
                let binding = binding(
                    &self.device,
                    pipeline,
                    kernel,
                    input,
                    plan.displacement,
                    destination,
                    bytes,
                    plan.weights,
                );
                dispatches.push(Dispatch {
                    pipeline: pipeline.pipeline.clone(),
                    binding,
                });
            }
        }
        Ok(PreparedImageFilterCompute {
            owner: self.owner.clone(),
            dispatches,
            inputs: std::iter::once(plan.source)
                .chain(plan.displacement)
                .cloned()
                .collect(),
            intermediate,
            output,
            workgroups: plan.workgroups,
            copy_mips: if plan.kernel.is_none() {
                plan.mip_levels
            } else {
                0
            },
            metrics: plan.metrics,
        })
    }
    /// Append all original passes or complete-copy commands in caller order.
    /// No submission, readback, polling, source mutation or uniform rewrite occurs.
    pub fn encode(
        &self,
        encoder: &mut wgpu::CommandEncoder,
        prepared: &PreparedImageFilterCompute,
    ) -> Result<(), Error> {
        if !Arc::ptr_eq(&self.owner, &prepared.owner) {
            return Err(Error::ResourceDomain);
        }
        prepared.encode_owned(encoder);
        Ok(())
    }
}

fn plan<'a>(
    device: &wgpu::Device,
    request: &'a ImageFilterComputeRequest<'a>,
) -> Result<Plan<'a>, Error> {
    let (source, displacement, kernel, parameters, weights, size) = match request {
        ImageFilterComputeRequest::Morphology { source, parameters } => {
            parameters[0].validate()?;
            parameters[1].validate()?;
            if parameters[0].direction != [1, 0]
                || parameters[1].direction != [0, 1]
                || parameters[0].dilate != parameters[1].dilate
            {
                return Err(Error::InvalidInput);
            }
            let no_op = parameters[0].radius == 0 && parameters[1].radius == 0;
            (
                *source,
                None,
                if no_op {
                    None
                } else {
                    Some(Kernel::Morphology)
                },
                if no_op {
                    Vec::new()
                } else {
                    parameters
                        .iter()
                        .map(|p| bytemuck::bytes_of(p).to_vec())
                        .collect()
                },
                &[][..],
                source.size(),
            )
        }
        ImageFilterComputeRequest::Convolution {
            source,
            parameters,
            kernel,
        } => {
            parameters.validate()?;
            let [width, height] = parameters.kernel_size;
            // Original raw method rejects dimensions before kernel publication.
            if !(1..=64).contains(&width) || !(1..=64).contains(&height) {
                return Err(Error::InvalidInput);
            }
            let count = (width as usize)
                .checked_mul(height as usize)
                .ok_or(Error::Capacity)?;
            if kernel.len() < count {
                return Err(Error::InvalidInput);
            }
            (
                *source,
                None,
                Some(Kernel::Convolution),
                vec![bytemuck::bytes_of(*parameters).to_vec()],
                &kernel[..count],
                source.size(),
            )
        }
        ImageFilterComputeRequest::Displacement {
            source,
            displacement,
            parameters,
            requested_size,
        } => {
            parameters.validate()?;
            let size = source.size();
            (
                *source,
                Some(*displacement),
                Some(Kernel::Displacement),
                vec![bytemuck::bytes_of(*parameters).to_vec()],
                &[][..],
                [
                    size[0].max(requested_size[0]),
                    size[1].max(requested_size[1]),
                ],
            )
        }
        ImageFilterComputeRequest::Magnifier { source, parameters } => {
            parameters.validate()?;
            (
                *source,
                None,
                Some(Kernel::Magnifier),
                vec![bytemuck::bytes_of(*parameters).to_vec()],
                &[][..],
                source.size(),
            )
        }
        ImageFilterComputeRequest::Lighting { source, parameters } => {
            parameters.validate()?;
            (
                *source,
                None,
                Some(Kernel::Lighting),
                vec![bytemuck::bytes_of(*parameters).to_vec()],
                &[][..],
                source.size(),
            )
        }
    };
    for image in std::iter::once(source).chain(displacement) {
        if image.device() != device {
            return Err(Error::ResourceDomain);
        }
        if image.alpha() != ImageAlpha::Premultiplied
            || !matches!(
                image.texture().format(),
                wgpu::TextureFormat::Rgba8Unorm
                    | wgpu::TextureFormat::Bgra8Unorm
                    | wgpu::TextureFormat::Rgba8UnormSrgb
                    | wgpu::TextureFormat::Bgra8UnormSrgb
            )
        {
            return Err(Error::InvalidInput);
        }
    }
    let limits = device.limits();
    if size
        .iter()
        .any(|v| *v == 0 || *v > limits.max_texture_dimension_2d)
    {
        return Err(Error::Capacity);
    }
    let mip_levels = if kernel.is_none() {
        source.mip_level_count()
    } else {
        1
    };
    let format = if kernel.is_none() {
        source.texture().format()
    } else {
        wgpu::TextureFormat::Rgba8Unorm
    };
    let mut bytes = 0_u64;
    for mip in 0..mip_levels {
        let level_bytes = u64::from((size[0] >> mip).max(1))
            .checked_mul(u64::from((size[1] >> mip).max(1)))
            .and_then(|value| value.checked_mul(4))
            .ok_or(Error::Capacity)?;
        bytes = bytes.checked_add(level_bytes).ok_or(Error::Capacity)?;
    }
    let workgroups = size.map(|v| v.div_ceil(16));
    let parameter_bytes = parameters.iter().map(|p| p.len() as u64).sum();
    let kernel_bytes = std::mem::size_of_val(weights) as u64;
    let metrics = if let Some(kernel) = kernel {
        if limits.max_compute_invocations_per_workgroup < 256
            || limits.max_compute_workgroup_size_x < 16
            || limits.max_compute_workgroup_size_y < 16
            || limits.max_storage_textures_per_shader_stage < 1
            || limits.max_sampled_textures_per_shader_stage < kernel.input_count()
            || limits.max_uniform_buffers_per_shader_stage < 1
            || limits.max_bind_groups < 1
            || limits.max_bindings_per_bind_group
                < if kernel.has_kernel() {
                    4
                } else {
                    kernel.input_count() + 2
                }
            || kernel.parameter_size() > limits.max_buffer_size
            || kernel.parameter_size() > u64::from(limits.max_uniform_buffer_binding_size)
            || workgroups
                .iter()
                .any(|v| *v > limits.max_compute_workgroups_per_dimension)
            || kernel.has_kernel()
                && (limits.max_storage_buffers_per_shader_stage < 1
                    || kernel_bytes > u64::from(limits.max_storage_buffer_binding_size)
                    || kernel_bytes > limits.max_buffer_size)
        {
            return Err(Error::Capacity);
        }
        let count = parameters.len() as u32;
        ImageFilterComputeMetrics {
            compute_passes: count,
            parameter_bytes,
            kernel_bytes,
            texture_allocations: count,
            texture_bytes: bytes.checked_mul(u64::from(count)).ok_or(Error::Capacity)?,
            workgroups: u64::from(workgroups[0]) * u64::from(workgroups[1]) * u64::from(count),
            texture_copy_bytes: 0,
        }
    } else {
        // The original no-op branch uses GpuTexture.CopyFrom, not a passthrough
        // shader. It needs CopySrc, matching dimensions/format and every mip.
        if !source
            .texture()
            .usage()
            .contains(wgpu::TextureUsages::COPY_SRC)
        {
            return Err(Error::InvalidInput);
        }
        ImageFilterComputeMetrics {
            texture_allocations: 1,
            texture_bytes: bytes,
            texture_copy_bytes: bytes,
            ..Default::default()
        }
    };
    Ok(Plan {
        source,
        displacement,
        kernel,
        parameters,
        weights,
        size,
        workgroups,
        format,
        mip_levels,
        metrics,
    })
}

fn allocate_texture(
    device: &wgpu::Device,
    size: [u32; 2],
    format: wgpu::TextureFormat,
    mip_level_count: u32,
    storage: bool,
) -> Result<GpuImage, Error> {
    let mut usage = wgpu::TextureUsages::TEXTURE_BINDING
        | wgpu::TextureUsages::COPY_SRC
        | wgpu::TextureUsages::COPY_DST
        | wgpu::TextureUsages::RENDER_ATTACHMENT;
    if storage {
        usage |= wgpu::TextureUsages::STORAGE_BINDING;
    }
    let texture = device.create_texture(&wgpu::TextureDescriptor {
        label: Some("ProGPU owned original image-filter target"),
        size: wgpu::Extent3d {
            width: size[0],
            height: size[1],
            depth_or_array_layers: 1,
        },
        mip_level_count,
        sample_count: 1,
        dimension: wgpu::TextureDimension::D2,
        format,
        usage,
        view_formats: &[],
    });
    GpuImage::from_texture(device, &texture, ImageAlpha::Premultiplied)
}
fn binding(
    device: &wgpu::Device,
    pipeline: &Pipeline,
    kernel: Kernel,
    source: &GpuImage,
    displacement: Option<&GpuImage>,
    output: &GpuImage,
    parameters: &[u8],
    weights: &[f32],
) -> wgpu::BindGroup {
    let views: Vec<_> = std::iter::once(source)
        .chain(displacement)
        .map(|image| {
            image.texture().create_view(&wgpu::TextureViewDescriptor {
                base_mip_level: 0,
                mip_level_count: Some(1),
                ..Default::default()
            })
        })
        .collect();
    let output = output.texture().create_view(&wgpu::TextureViewDescriptor {
        base_mip_level: 0,
        mip_level_count: Some(1),
        ..Default::default()
    });
    let buffer = device.create_buffer_init(&wgpu::util::BufferInitDescriptor {
        label: Some("ProGPU immutable original image-filter parameters"),
        contents: parameters,
        usage: wgpu::BufferUsages::UNIFORM,
    });
    let weights = kernel.has_kernel().then(|| {
        device.create_buffer_init(&wgpu::util::BufferInitDescriptor {
            label: Some("ProGPU original convolution kernel prefix"),
            contents: bytemuck::cast_slice(weights),
            usage: wgpu::BufferUsages::STORAGE,
        })
    });
    let mut entries: Vec<_> = views
        .iter()
        .enumerate()
        .map(|(i, view)| wgpu::BindGroupEntry {
            binding: i as u32,
            resource: wgpu::BindingResource::TextureView(view),
        })
        .collect();
    entries.push(wgpu::BindGroupEntry {
        binding: kernel.input_count(),
        resource: wgpu::BindingResource::TextureView(&output),
    });
    entries.push(wgpu::BindGroupEntry {
        binding: kernel.input_count() + 1,
        resource: buffer.as_entire_binding(),
    });
    if let Some(weights) = &weights {
        entries.push(wgpu::BindGroupEntry {
            binding: 3,
            resource: weights.as_entire_binding(),
        });
    }
    device.create_bind_group(&wgpu::BindGroupDescriptor {
        label: Some("ProGPU retained image-filter dependencies"),
        layout: &pipeline.layout,
        entries: &entries,
    })
}
fn create_pipeline(device: &wgpu::Device, kernel: Kernel) -> Pipeline {
    let inputs = kernel.input_count();
    let mut entries: Vec<_> = (0..inputs)
        .map(|binding| wgpu::BindGroupLayoutEntry {
            binding,
            visibility: wgpu::ShaderStages::COMPUTE,
            ty: wgpu::BindingType::Texture {
                sample_type: wgpu::TextureSampleType::Float { filterable: false },
                view_dimension: wgpu::TextureViewDimension::D2,
                multisampled: false,
            },
            count: None,
        })
        .collect();
    entries.push(wgpu::BindGroupLayoutEntry {
        binding: inputs,
        visibility: wgpu::ShaderStages::COMPUTE,
        ty: wgpu::BindingType::StorageTexture {
            access: wgpu::StorageTextureAccess::WriteOnly,
            format: wgpu::TextureFormat::Rgba8Unorm,
            view_dimension: wgpu::TextureViewDimension::D2,
        },
        count: None,
    });
    entries.push(wgpu::BindGroupLayoutEntry {
        binding: inputs + 1,
        visibility: wgpu::ShaderStages::COMPUTE,
        ty: wgpu::BindingType::Buffer {
            ty: wgpu::BufferBindingType::Uniform,
            has_dynamic_offset: false,
            min_binding_size: std::num::NonZeroU64::new(kernel.parameter_size()),
        },
        count: None,
    });
    if kernel.has_kernel() {
        entries.push(wgpu::BindGroupLayoutEntry {
            binding: 3,
            visibility: wgpu::ShaderStages::COMPUTE,
            ty: wgpu::BindingType::Buffer {
                ty: wgpu::BufferBindingType::Storage { read_only: true },
                has_dynamic_offset: false,
                min_binding_size: std::num::NonZeroU64::new(4),
            },
            count: None,
        });
    }
    let layout = device.create_bind_group_layout(&wgpu::BindGroupLayoutDescriptor {
        label: Some("ProGPU original image-filter bindings"),
        entries: &entries,
    });
    let pipeline_layout = device.create_pipeline_layout(&wgpu::PipelineLayoutDescriptor {
        label: Some("ProGPU original image-filter pipeline layout"),
        bind_group_layouts: &[Some(&layout)],
        immediate_size: 0,
    });
    let module = device.create_shader_module(wgpu::ShaderModuleDescriptor {
        label: Some("ProGPU canonical image-filter WGSL"),
        source: wgpu::ShaderSource::Wgsl(kernel.source().into()),
    });
    let pipeline = device.create_compute_pipeline(&wgpu::ComputePipelineDescriptor {
        label: Some("ProGPU original image-filter compute kernel"),
        layout: Some(&pipeline_layout),
        module: &module,
        entry_point: Some("main"),
        compilation_options: Default::default(),
        cache: None,
    });
    Pipeline { pipeline, layout }
}

impl PreparedImageFilterCompute {
    /// Keep original image producers alive through all previously submitted work.
    /// Call immediately after an external queue submission of this program.
    pub fn retain_submitted(&self, queue: &wgpu::Queue) {
        for input in &self.inputs {
            input.retain_submitted(queue);
        }
    }
}
