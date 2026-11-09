//! Original compute shaders with immutable same-device resources and no CPU pixels.
use super::*;
use crate::{
    image::{GpuImage, ImageAlpha},
    path::{ExecutionPath, ExecutionPreference},
    scene::LayerBlend,
};
use wgpu::util::DeviceExt;

const TABLE: &str = include_str!("../../../../ProGPU.Compute/Shaders/ColorTable.wgsl");
const NONLINEAR: &str =
    include_str!("../../../../ProGPU.Compute/Shaders/NonlinearColorFilter.wgsl");
const OVERDRAW: &str = include_str!("../../../../ProGPU.Compute/Shaders/OverdrawColorFilter.wgsl");
const BLEND: &str = include_str!("../../../../ProGPU.Compute/Shaders/ImageBlend.wgsl");
const ARITHMETIC: &str =
    include_str!("../../../../ProGPU.Compute/Shaders/ArithmeticComposite.wgsl");

#[derive(Clone, Copy)]
enum Kernel {
    Table,
    Nonlinear,
    Overdraw,
    Blend,
    Arithmetic,
}
impl Kernel {
    fn index(self) -> usize {
        self as usize
    }
    fn source(self) -> &'static str {
        match self {
            Self::Table => TABLE,
            Self::Nonlinear => NONLINEAR,
            Self::Overdraw => OVERDRAW,
            Self::Blend => BLEND,
            Self::Arithmetic => ARITHMETIC,
        }
    }
    fn binary(self) -> bool {
        matches!(self, Self::Blend | Self::Arithmetic)
    }
    fn parameter_size(self) -> u64 {
        match self {
            Self::Table => 4096,
            Self::Blend => 16,
            Self::Arithmetic => 32,
            _ => 96,
        }
    }
}
#[derive(Clone)]
struct Pipeline {
    pipeline: wgpu::ComputePipeline,
    layout: wgpu::BindGroupLayout,
}

/// Actual storage and work encoded for one original color-filter operation.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct ColorFilterComputeMetrics {
    /// Original compute passes per encode, always one for a prepared operation.
    pub compute_passes: u32,
    /// Actual parameter payload, including the expanded 4096-byte table when used.
    pub parameter_bytes: u64,
    /// Actual newly allocated output textures.
    pub texture_allocations: u32,
    /// Actual output RGBA8 texel storage.
    pub texture_bytes: u64,
    /// Total original 16-by-16 workgroups.
    pub workgroups: u64,
}
/// Frozen parameters and actual retained source/output resources. Re-encoding
/// reads the sources' current contents and rewrites this private output; retaining
/// a texture handle does not freeze an external producer's pixel mutations.
pub struct PreparedColorFilterCompute {
    owner: Arc<()>,
    pipeline: wgpu::ComputePipeline,
    binding: wgpu::BindGroup,
    inputs: Vec<GpuImage>,
    output: GpuImage,
    workgroups: [u32; 2],
    metrics: ColorFilterComputeMetrics,
}
impl PreparedColorFilterCompute {
    /// Original inputs, in background then foreground order for binary operations.
    pub fn inputs(&self) -> &[GpuImage] {
        &self.inputs
    }
    /// Actual premultiplied RGBA8 output. Encode and submit before consuming pixels.
    pub fn output(&self) -> &GpuImage {
        &self.output
    }
    /// Actual resource and dispatch inventory, independent of renderer counters.
    pub fn metrics(&self) -> ColorFilterComputeMetrics {
        self.metrics
    }
    /// Original selected GPU execution route.
    pub fn execution_path(&self) -> ExecutionPath {
        ExecutionPath::NativeCompute
    }
}
/// Device-bound original color-filter kernels. Construction does not compile
/// shaders; each pipeline is initialized only when its operation is prepared.
/// Matrix/luma draws retain their independent Texture.wgsl renderer path.
pub struct ColorFilterComputeEngine {
    owner: Arc<()>,
    device: wgpu::Device,
    pipelines: [Option<Pipeline>; 5],
}
impl ColorFilterComputeEngine {
    /// Retain the actual device and requested execution policy. Unsupported forced
    /// routes reject; there is no hidden per-pixel CPU fallback.
    pub fn new(device: &wgpu::Device, preference: ExecutionPreference) -> Result<Self, Error> {
        let limits = device.limits();
        if !matches!(
            preference,
            ExecutionPreference::Automatic | ExecutionPreference::NativeCompute
        ) || limits.max_compute_invocations_per_workgroup < 256
            || limits.max_compute_workgroup_size_x < 16
            || limits.max_compute_workgroup_size_y < 16
            || limits.max_storage_textures_per_shader_stage < 1
            || limits.max_sampled_textures_per_shader_stage < 2
            || limits.max_uniform_buffers_per_shader_stage < 1
            || limits.max_storage_buffers_per_shader_stage < 1
            || limits.max_bindings_per_bind_group < 4
        {
            return Err(Error::UnsupportedExecution);
        }
        Ok(Self {
            owner: Arc::new(()),
            device: device.clone(),
            pipelines: std::array::from_fn(|_| None),
        })
    }
    /// Actual selected route; this is not a performance qualification.
    pub fn execution_path(&self) -> ExecutionPath {
        ExecutionPath::NativeCompute
    }
    /// Number of original kernels initialized by actual preparation requests.
    pub fn initialized_pipeline_count(&self) -> usize {
        self.pipelines.iter().filter(|p| p.is_some()).count()
    }
    /// Prepare one original dependency-plan operation. Unary operations require
    /// no foreground; lerp requires its actual second filtered result. Blend
    /// requires the caller's full-size premultiplied capture of the operation's
    /// constant foreground color, as in the original retained brush renderer.
    /// Matrix/luma use the separate image renderer and reject here.
    pub async fn prepare(
        &mut self,
        operation: &ColorFilterOperation,
        background: &GpuImage,
        foreground: Option<&GpuImage>,
    ) -> Result<PreparedColorFilterCompute, Error> {
        match operation {
            ColorFilterOperation::Matrix(_) => Err(Error::UnsupportedExecution),
            ColorFilterOperation::Table(t) => {
                if foreground.is_some() {
                    return Err(Error::InvalidInput);
                }
                self.prepare_kernel(
                    Kernel::Table,
                    background,
                    None,
                    bytemuck::cast_slice(&t.gpu_values()),
                )
                .await
            }
            ColorFilterOperation::Nonlinear(p) => {
                if foreground.is_some()
                    || bytemuck::cast_slice::<_, f32>(std::slice::from_ref(p))
                        .iter()
                        .any(|v| !v.is_finite())
                    || !matches!(p.configuration[0], 0.0 | 1.0)
                    || !matches!(p.configuration[1], 0.0 | 1.0)
                    || !matches!(p.configuration[2], 0.0 | 1.0 | 2.0)
                    || p.configuration[3] < 0.0
                {
                    return Err(Error::InvalidInput);
                }
                self.prepare_kernel(Kernel::Nonlinear, background, None, bytemuck::bytes_of(p))
                    .await
            }
            ColorFilterOperation::Overdraw(p) => {
                if foreground.is_some()
                    || p.colors
                        .iter()
                        .flatten()
                        .any(|v| !v.is_finite() || !(0.0..=1.0).contains(v))
                {
                    return Err(Error::InvalidInput);
                }
                self.prepare_kernel(Kernel::Overdraw, background, None, bytemuck::bytes_of(p))
                    .await
            }
            ColorFilterOperation::Blend { mode, .. } => {
                let foreground = foreground.ok_or(Error::InvalidInput)?;
                if foreground.size() != background.size() {
                    return Err(Error::InvalidInput);
                }
                self.prepare_blend(background, foreground, *mode, false)
                    .await
            }
            ColorFilterOperation::Lerp { weight } => {
                if !weight.is_finite() || !(0.0..=1.0).contains(weight) {
                    return Err(Error::InvalidInput);
                }
                self.prepare_arithmetic(
                    background,
                    foreground.ok_or(Error::InvalidInput)?,
                    [0.0, *weight, 1.0 - *weight, 0.0],
                    true,
                )
                .await
            }
        }
    }
    /// Original ImageBlend compute, retaining its optional linear-light blend
    /// policy. Output covers the union of input extents; out-of-bounds reads are
    /// transparent in the unchanged canonical shader.
    pub async fn prepare_blend(
        &mut self,
        background: &GpuImage,
        foreground: &GpuImage,
        mode: LayerBlend,
        linear_rgb: bool,
    ) -> Result<PreparedColorFilterCompute, Error> {
        let parameters = [mode as u32, u32::from(linear_rgb), 0, 0];
        self.prepare_kernel(
            Kernel::Blend,
            background,
            Some(foreground),
            bytemuck::cast_slice(&parameters),
        )
        .await
    }
    /// Original SVG arithmetic-composite operation, with coefficients in k1,
    /// k2, k3, k4 order and its independent premultiplied-color enforcement.
    pub async fn prepare_arithmetic(
        &mut self,
        background: &GpuImage,
        foreground: &GpuImage,
        coefficients: [f32; 4],
        enforce_premultiplied: bool,
    ) -> Result<PreparedColorFilterCompute, Error> {
        if coefficients.iter().any(|v| !v.is_finite()) {
            return Err(Error::InvalidInput);
        }
        #[repr(C)]
        #[derive(Clone, Copy, Pod, Zeroable)]
        struct Parameters {
            coefficients: [f32; 4],
            enforce: u32,
            padding: [u32; 3],
        }
        let parameters = Parameters {
            coefficients,
            enforce: u32::from(enforce_premultiplied),
            padding: [0; 3],
        };
        self.prepare_kernel(
            Kernel::Arithmetic,
            background,
            Some(foreground),
            bytemuck::bytes_of(&parameters),
        )
        .await
    }
    async fn prepare_kernel(
        &mut self,
        kernel: Kernel,
        background: &GpuImage,
        foreground: Option<&GpuImage>,
        parameters: &[u8],
    ) -> Result<PreparedColorFilterCompute, Error> {
        for image in std::iter::once(background).chain(foreground) {
            if image.device() != &self.device {
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
        if kernel.binary() != foreground.is_some()
            || parameters.len() as u64 != kernel.parameter_size()
        {
            return Err(Error::InvalidInput);
        }
        let b = background.size();
        let f = foreground.map_or(b, GpuImage::size);
        let size = [b[0].max(f[0]), b[1].max(f[1])];
        let workgroups = size.map(|v| v.div_ceil(16));
        let limits = self.device.limits();
        if size
            .iter()
            .any(|v| *v == 0 || *v > limits.max_texture_dimension_2d)
            || workgroups
                .iter()
                .any(|v| *v > limits.max_compute_workgroups_per_dimension)
            || kernel.parameter_size() > limits.max_buffer_size
            || (matches!(kernel, Kernel::Table)
                && kernel.parameter_size() > u64::from(limits.max_storage_buffer_binding_size))
            || (!matches!(kernel, Kernel::Table)
                && kernel.parameter_size() > u64::from(limits.max_uniform_buffer_binding_size))
        {
            return Err(Error::Capacity);
        }
        if self.pipelines[kernel.index()].is_none() {
            let scope = self.device.push_error_scope(wgpu::ErrorFilter::Validation);
            let pipeline = create_pipeline(&self.device, kernel);
            if let Some(error) = scope.pop().await {
                return Err(error.into());
            }
            self.pipelines[kernel.index()] = Some(pipeline);
        }
        let scope = self.device.push_error_scope(wgpu::ErrorFilter::Validation);
        let result = self.allocate(kernel, background, foreground, parameters, size, workgroups);
        if let Some(error) = scope.pop().await {
            return Err(error.into());
        }
        result
    }
    fn allocate(
        &self,
        kernel: Kernel,
        background: &GpuImage,
        foreground: Option<&GpuImage>,
        parameters: &[u8],
        size: [u32; 2],
        workgroups: [u32; 2],
    ) -> Result<PreparedColorFilterCompute, Error> {
        let pipeline = self.pipelines[kernel.index()]
            .as_ref()
            .ok_or(Error::InvalidInput)?;
        let texture = self.device.create_texture(&wgpu::TextureDescriptor {
            label: Some("ProGPU original color-filter output"),
            size: wgpu::Extent3d {
                width: size[0],
                height: size[1],
                depth_or_array_layers: 1,
            },
            mip_level_count: 1,
            sample_count: 1,
            dimension: wgpu::TextureDimension::D2,
            format: wgpu::TextureFormat::Rgba8Unorm,
            usage: wgpu::TextureUsages::TEXTURE_BINDING
                | wgpu::TextureUsages::STORAGE_BINDING
                | wgpu::TextureUsages::COPY_SRC
                | wgpu::TextureUsages::RENDER_ATTACHMENT,
            view_formats: &[],
        });
        let input_views: Vec<_> = std::iter::once(background)
            .chain(foreground)
            .map(|image| {
                image.texture().create_view(&wgpu::TextureViewDescriptor {
                    base_mip_level: 0,
                    mip_level_count: Some(1),
                    ..Default::default()
                })
            })
            .collect();
        let output_view = texture.create_view(&Default::default());
        let buffer = self
            .device
            .create_buffer_init(&wgpu::util::BufferInitDescriptor {
                label: Some("ProGPU immutable original color-filter parameters"),
                contents: parameters,
                usage: if matches!(kernel, Kernel::Table) {
                    wgpu::BufferUsages::STORAGE
                } else {
                    wgpu::BufferUsages::UNIFORM
                },
            });
        let mut entries: Vec<_> = input_views
            .iter()
            .enumerate()
            .map(|(i, view)| wgpu::BindGroupEntry {
                binding: i as u32,
                resource: wgpu::BindingResource::TextureView(view),
            })
            .collect();
        entries.push(wgpu::BindGroupEntry {
            binding: input_views.len() as u32,
            resource: wgpu::BindingResource::TextureView(&output_view),
        });
        entries.push(wgpu::BindGroupEntry {
            binding: input_views.len() as u32 + 1,
            resource: buffer.as_entire_binding(),
        });
        let binding = self.device.create_bind_group(&wgpu::BindGroupDescriptor {
            label: Some("ProGPU original color-filter dependencies"),
            layout: &pipeline.layout,
            entries: &entries,
        });
        let output = GpuImage::from_texture(&self.device, &texture, ImageAlpha::Premultiplied)?;
        Ok(PreparedColorFilterCompute {
            owner: self.owner.clone(),
            pipeline: pipeline.pipeline.clone(),
            binding,
            inputs: std::iter::once(background)
                .chain(foreground)
                .cloned()
                .collect(),
            output,
            workgroups,
            metrics: ColorFilterComputeMetrics {
                compute_passes: 1,
                parameter_bytes: kernel.parameter_size(),
                texture_allocations: 1,
                texture_bytes: u64::from(size[0]) * u64::from(size[1]) * 4,
                workgroups: u64::from(workgroups[0]) * u64::from(workgroups[1]),
            },
        })
    }
    /// Append the original compute pass to the caller's same-device encoder.
    /// No submission, polling, readback or source mutation is performed here.
    pub fn encode(
        &self,
        encoder: &mut wgpu::CommandEncoder,
        prepared: &PreparedColorFilterCompute,
    ) -> Result<(), Error> {
        if !Arc::ptr_eq(&self.owner, &prepared.owner) {
            return Err(Error::ResourceDomain);
        }
        let mut pass = encoder.begin_compute_pass(&wgpu::ComputePassDescriptor {
            label: Some("ProGPU original color-filter compute"),
            timestamp_writes: None,
        });
        pass.set_pipeline(&prepared.pipeline);
        pass.set_bind_group(0, &prepared.binding, &[]);
        pass.dispatch_workgroups(prepared.workgroups[0], prepared.workgroups[1], 1);
        Ok(())
    }
}
fn create_pipeline(device: &wgpu::Device, kernel: Kernel) -> Pipeline {
    let input_count = if kernel.binary() { 2 } else { 1 };
    let mut entries: Vec<_> = (0..input_count)
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
        binding: input_count,
        visibility: wgpu::ShaderStages::COMPUTE,
        ty: wgpu::BindingType::StorageTexture {
            access: wgpu::StorageTextureAccess::WriteOnly,
            format: wgpu::TextureFormat::Rgba8Unorm,
            view_dimension: wgpu::TextureViewDimension::D2,
        },
        count: None,
    });
    entries.push(wgpu::BindGroupLayoutEntry {
        binding: input_count + 1,
        visibility: wgpu::ShaderStages::COMPUTE,
        ty: wgpu::BindingType::Buffer {
            ty: if matches!(kernel, Kernel::Table) {
                wgpu::BufferBindingType::Storage { read_only: true }
            } else {
                wgpu::BufferBindingType::Uniform
            },
            has_dynamic_offset: false,
            min_binding_size: std::num::NonZeroU64::new(kernel.parameter_size()),
        },
        count: None,
    });
    let layout = device.create_bind_group_layout(&wgpu::BindGroupLayoutDescriptor {
        label: Some("ProGPU original color-filter bindings"),
        entries: &entries,
    });
    let pipeline_layout = device.create_pipeline_layout(&wgpu::PipelineLayoutDescriptor {
        label: Some("ProGPU original color-filter layout"),
        bind_group_layouts: &[Some(&layout)],
        immediate_size: 0,
    });
    let module = device.create_shader_module(wgpu::ShaderModuleDescriptor {
        label: Some("ProGPU canonical color-filter WGSL"),
        source: wgpu::ShaderSource::Wgsl(kernel.source().into()),
    });
    let pipeline = device.create_compute_pipeline(&wgpu::ComputePipelineDescriptor {
        label: Some("ProGPU canonical color-filter kernel"),
        layout: Some(&pipeline_layout),
        module: &module,
        entry_point: Some("main"),
        compilation_options: Default::default(),
        cache: None,
    });
    Pipeline { pipeline, layout }
}
