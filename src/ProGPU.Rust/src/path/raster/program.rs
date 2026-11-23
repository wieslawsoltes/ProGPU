//! Original native ordered split leaves, signed rows and final coverage phases.
use super::*;
use crate::path::SignedWindingPolicy;
use std::collections::BTreeMap;

#[derive(Clone, Copy, Debug, PartialEq, Eq, PartialOrd, Ord)]
pub(super) enum Kernel {
    Ordinary,
    Signed,
    LinearSigned,
    Leaf,
    Boolean,
    SignedLeaf,
    SignedRows,
    SignedPack,
}
impl Kernel {
    fn shader(self) -> (&'static str, &'static str) {
        match self {
            Self::Ordinary => (shaders::PATH, "cs_main_ordinary"),
            Self::Signed => (shaders::PATH, "cs_main"),
            Self::LinearSigned => (shaders::PATH, "cs_main_linear_winding"),
            Self::Leaf => (shaders::PATH, "cs_split_leaf"),
            Self::Boolean => (shaders::PATH, "cs_split_boolean_combine"),
            Self::SignedLeaf => (shaders::PATH_SIGNED_LEAF, "cs_main"),
            Self::SignedRows => (shaders::PATH_SIGNED_ROWS, "cs_main"),
            Self::SignedPack => (shaders::PATH_SIGNED_PACK, "cs_main"),
        }
    }
}
#[repr(C)]
#[derive(Clone, Copy, Pod, Zeroable)]
struct Combine {
    source_offset: u32,
    source_stride: u32,
    source_count: u32,
    program_index: u32,
    program_count: u32,
    destination_offset: u32,
    destination_row: u32,
    width: u32,
    height: u32,
    sample_grid: u32,
}
const _: () = assert!(size_of::<Combine>() == 40);

pub(super) struct Plan {
    inline: Vec<Uniforms>,
    boolean_leaves: Vec<Vec<Uniforms>>,
    signed_leaves: Vec<Vec<Uniforms>>,
    boolean_combine: Vec<Combine>,
    signed_combine: Vec<Combine>,
    inline_kernel: Kernel,
    policy: SignedWindingPolicy,
}
impl Plan {
    pub fn needed(paths: &[(&Path, RasterRequest)]) -> bool {
        paths
            .iter()
            .any(|(p, _)| p.has_signed_winding() || p.requires_split_program())
    }
    pub fn new(paths: &[(&Path, RasterRequest)], policy: SignedWindingPolicy) -> Self {
        let inline_signed = policy == SignedWindingPolicy::Inline
            && paths.iter().any(|(p, _)| p.has_signed_winding());
        // Whole original inventory, including otherwise unused segments. Never
        // infer all-line admission from an emitted subset or bounding shape.
        let all_lines = paths
            .iter()
            .all(|(p, _)| p.segments.iter().all(|s| s.kind == 0));
        Self {
            inline: Vec::new(),
            boolean_leaves: Vec::new(),
            signed_leaves: Vec::new(),
            boolean_combine: Vec::new(),
            signed_combine: Vec::new(),
            policy,
            inline_kernel: if inline_signed {
                if all_lines {
                    Kernel::LinearSigned
                } else {
                    Kernel::Signed
                }
            } else {
                Kernel::Ordinary
            },
        }
    }
    pub fn append(&mut self, path: &Path, uniform: Uniforms, size: &mut u64) -> Result<(), Error> {
        let signed = path.has_signed_winding();
        if !path.requires_split_program() || signed && self.policy == SignedWindingPolicy::Inline {
            self.inline.push(uniform);
            return Ok(());
        }
        let program = path.program.as_ref().ok_or(Error::InvalidInput)?;
        let words = if signed { 64u64 } else { 2 };
        let row = u64::from(uniform.width) * words;
        let stride = row * u64::from(uniform.height) * 4;
        let source = size.checked_add(255).ok_or(Error::Capacity)? & !255;
        let count = program.records.len() as u64;
        let result = if signed {
            u64::from(uniform.width) * u64::from(uniform.height) * 8
        } else {
            0
        };
        let next = source
            .checked_add(stride.checked_mul(count).ok_or(Error::Capacity)?)
            .and_then(|n| n.checked_add(result))
            .ok_or(Error::Capacity)?;
        // Original native four-GiB byte bound, independently of device limits.
        if next > u64::from(u32::MAX) {
            return Err(Error::Capacity);
        }
        let leaves = if signed {
            &mut self.signed_leaves
        } else {
            &mut self.boolean_leaves
        };
        leaves.resize_with(leaves.len().max(count as usize), Vec::new);
        for (index, phase) in leaves.iter_mut().take(count as usize).enumerate() {
            phase.push(Uniforms {
                path_index: uniform.path_index + index as u32,
                output_offset_words: ((source + stride * index as u64) / 4) as u32,
                output_row_words: row as u32,
                path_index_b: 0,
                path_op_kind: if signed { 0x4000_0000 } else { 0 },
                ..uniform
            });
        }
        let combine = Combine {
            source_offset: (source / 4) as u32,
            source_stride: (stride / 4) as u32,
            source_count: count as u32,
            program_index: uniform.path_index_b,
            program_count: uniform.path_op_kind & !0x8000_0000,
            destination_offset: uniform.output_offset_words,
            destination_row: uniform.output_row_words,
            width: uniform.width,
            height: uniform.height,
            sample_grid: uniform.sample_grid,
        };
        if signed {
            self.signed_combine.push(combine);
        } else {
            self.boolean_combine.push(combine);
        }
        *size = next;
        Ok(())
    }
    fn phases(&self) -> Vec<Phase<'_>> {
        let mut phases = Vec::new();
        let split_size = self
            .boolean_leaves
            .iter()
            .flatten()
            .fold([0u32; 2], |[w, h], u| {
                [w.max(u.width.div_ceil(16)), h.max(u.height.div_ceil(16))]
            });
        let signed_size = self
            .signed_leaves
            .iter()
            .flatten()
            .fold([0u32; 2], |[w, h], u| {
                [
                    w.max(u.width.div_ceil(16)),
                    h.max((u.height * 8).div_ceil(16)),
                ]
            });
        if !self.inline.is_empty() {
            let size = self.inline.iter().fold([0u32; 2], |[w, h], u| {
                [w.max(u.width.div_ceil(64)), h.max(u.height.div_ceil(16))]
            });
            phases.push(Phase {
                kernel: self.inline_kernel,
                uniforms: bytemuck::cast_slice(&self.inline),
                combine: false,
                size,
                count: self.inline.len(),
                submit: false,
            });
        }
        for (leaves, kernel, size) in [
            (&self.boolean_leaves, Kernel::Leaf, split_size),
            (&self.signed_leaves, Kernel::SignedLeaf, signed_size),
        ] {
            for leaf in leaves {
                if !leaf.is_empty() {
                    phases.push(Phase {
                        kernel,
                        uniforms: bytemuck::cast_slice(leaf),
                        combine: false,
                        size,
                        count: leaf.len(),
                        submit: true,
                    });
                }
            }
        }
        // Match the original row-evaluate, Boolean-pack, signed-pack pass order.
        for (records, kernel, width) in [
            (&self.signed_combine, Kernel::SignedRows, 16),
            (&self.boolean_combine, Kernel::Boolean, 64),
            (&self.signed_combine, Kernel::SignedPack, 64),
        ] {
            if !records.is_empty() {
                let size = records.iter().fold([0u32; 2], |[w, h], u| {
                    [w.max(u.width.div_ceil(width)), h.max(u.height.div_ceil(16))]
                });
                phases.push(Phase {
                    kernel,
                    uniforms: bytemuck::cast_slice(records),
                    combine: true,
                    size,
                    count: records.len(),
                    submit: false,
                });
            }
        }
        phases
    }
}
struct Phase<'a> {
    kernel: Kernel,
    uniforms: &'a [u8],
    combine: bool,
    size: [u32; 2],
    count: usize,
    submit: bool,
}

#[derive(Default)]
pub(super) struct Pipelines {
    layout: Option<wgpu::BindGroupLayout>,
    pipelines: BTreeMap<Kernel, wgpu::ComputePipeline>,
}
impl Pipelines {
    pub async fn prepare(
        &mut self,
        device: &wgpu::Device,
        paths: &[(&Path, RasterRequest)],
        policy: SignedWindingPolicy,
    ) -> Result<(), Error> {
        if !Plan::needed(paths) {
            return Ok(());
        }
        if device.limits().max_storage_buffers_per_shader_stage < 5 {
            return Err(Error::UnsupportedExecution);
        }
        let signed = paths.iter().any(|(p, _)| p.has_signed_winding());
        let split_boolean = paths
            .iter()
            .any(|(p, _)| p.requires_split_program() && !p.has_signed_winding());
        let mut required = Vec::new();
        if paths.iter().any(|(p, _)| {
            !p.requires_split_program()
                || p.has_signed_winding() && policy == SignedWindingPolicy::Inline
        }) {
            required.push(Plan::new(paths, policy).inline_kernel);
        }
        if split_boolean {
            required.extend([Kernel::Leaf, Kernel::Boolean]);
        }
        if signed && policy == SignedWindingPolicy::Staged {
            required.extend([Kernel::SignedLeaf, Kernel::SignedRows, Kernel::SignedPack]);
        }
        for kernel in required {
            if self.pipelines.contains_key(&kernel) {
                continue;
            }
            let scope = device.push_error_scope(wgpu::ErrorFilter::Validation);
            let layout = match &self.layout {
                Some(layout) => layout.clone(),
                None => device.create_bind_group_layout(&wgpu::BindGroupLayoutDescriptor {
                    label: Some("ProGPU original five-buffer path program layout"),
                    entries: &std::array::from_fn::<_, 5, _>(|binding| {
                        wgpu::BindGroupLayoutEntry {
                            binding: binding as u32,
                            visibility: wgpu::ShaderStages::COMPUTE,
                            ty: wgpu::BindingType::Buffer {
                                ty: wgpu::BufferBindingType::Storage {
                                    read_only: binding != 3,
                                },
                                has_dynamic_offset: false,
                                min_binding_size: None,
                            },
                            count: None,
                        }
                    }),
                }),
            };
            let pipeline_layout = device.create_pipeline_layout(&wgpu::PipelineLayoutDescriptor {
                label: Some("ProGPU path program pipeline layout"),
                bind_group_layouts: &[Some(&layout)],
                immediate_size: 0,
            });
            let (source, entry) = kernel.shader();
            let module = device.create_shader_module(wgpu::ShaderModuleDescriptor {
                label: Some("ProGPU canonical path program shader"),
                source: wgpu::ShaderSource::Wgsl(source.into()),
            });
            let pipeline = device.create_compute_pipeline(&wgpu::ComputePipelineDescriptor {
                label: Some(entry),
                layout: Some(&pipeline_layout),
                module: &module,
                entry_point: Some(entry),
                compilation_options: Default::default(),
                cache: None,
            });
            if let Some(error) = scope.pop().await {
                return Err(error.into());
            }
            self.layout = Some(layout);
            self.pipelines.insert(kernel, pipeline);
        }
        Ok(())
    }
    pub fn encode(
        &self,
        device: &wgpu::Device,
        plan: &Plan,
        records: &[Record],
        segments: &[super::super::Segment],
        size: u64,
    ) -> Result<EncodedBatch, Error> {
        let limits = device.limits();
        let maximum = limits
            .max_storage_buffer_binding_size
            .min(limits.max_buffer_size);
        if size == 0 || size > maximum {
            return Err(Error::Capacity);
        }
        let layout = self.layout.as_ref().ok_or(Error::UnsupportedExecution)?;
        let phases = plan.phases();
        // Reject the entire plan before allocating or submitting any phase.
        for phase in &phases {
            if !self.pipelines.contains_key(&phase.kernel) {
                return Err(Error::UnsupportedExecution);
            }
            if phase.count > limits.max_compute_workgroups_per_dimension as usize
                || phase
                    .size
                    .iter()
                    .any(|n| *n == 0 || *n > limits.max_compute_workgroups_per_dimension)
                || phase.uniforms.len() as u64 > maximum
            {
                return Err(Error::Capacity);
            }
        }
        let storage = |label, contents: &[u8]| {
            device.create_buffer_init(&wgpu::util::BufferInitDescriptor {
                label: Some(label),
                contents,
                usage: wgpu::BufferUsages::STORAGE,
            })
        };
        let records = storage(
            "ProGPU native program records",
            bytemuck::cast_slice(records),
        );
        let segments = storage(
            "ProGPU native program segments",
            bytemuck::cast_slice(segments),
        );
        let dummy_path = storage("ProGPU unused path record", &[0; 48]);
        let dummy_combine = storage("ProGPU unused combine record", &[0; 40]);
        let output = device.create_buffer(&wgpu::BufferDescriptor {
            label: Some("ProGPU original shared program staging"),
            size,
            usage: wgpu::BufferUsages::STORAGE | wgpu::BufferUsages::COPY_SRC,
            mapped_at_creation: false,
        });
        let mut owned = Vec::new();
        for phase in &phases {
            let buffer = storage("ProGPU original program phase records", phase.uniforms);
            let buffers = [
                if phase.combine { &dummy_path } else { &buffer },
                &records,
                &segments,
                &output,
                if phase.combine {
                    &buffer
                } else {
                    &dummy_combine
                },
            ];
            let binding = device.create_bind_group(&wgpu::BindGroupDescriptor {
                label: Some("ProGPU immutable program phase bindings"),
                layout,
                entries: &std::array::from_fn::<_, 5, _>(|i| wgpu::BindGroupEntry {
                    binding: i as u32,
                    resource: buffers[i].as_entire_binding(),
                }),
            });
            owned.push(binding);
        }
        let encoder = || {
            device.create_command_encoder(&wgpu::CommandEncoderDescriptor {
                label: Some("ProGPU native program phase commands"),
            })
        };
        let mut current = encoder();
        let mut preceding = Vec::new();
        for (phase, binding) in phases.iter().zip(&owned) {
            {
                let mut pass = current.begin_compute_pass(&wgpu::ComputePassDescriptor {
                    label: Some("ProGPU native program phase"),
                    timestamp_writes: None,
                });
                pass.set_pipeline(
                    self.pipelines
                        .get(&phase.kernel)
                        .ok_or(Error::UnsupportedExecution)?,
                );
                pass.set_bind_group(0, binding, &[]);
                pass.dispatch_workgroups(phase.size[0], phase.size[1], phase.count as u32);
            }
            if phase.submit {
                preceding.push(current.finish());
                current = encoder();
            }
        }
        Ok(EncodedBatch {
            encoder: current,
            output,
            preceding,
        })
    }
}
