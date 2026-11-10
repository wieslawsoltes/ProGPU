//! Same-device retained query resources; canonical GPU geometry is unchanged.
use super::*;
use crate::{Error, picture_archive::ArchiveHitIndex};
use std::{
    collections::HashMap,
    sync::{Arc, Mutex},
};
use wgpu::util::DeviceExt;
mod pipeline;
mod readback;
pub use readback::{HitQueryOutput, HitQueryWork};

#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct HitQueryMetrics {
    pub index_upload_bytes: u64,
    pub index_buffer_bytes: u64,
    pub query_upload_bytes: u64,
    pub readback_bytes: u64,
    pub compute_passes: u64,
    pub submissions: u64,
    pub pipeline_creations: u32,
}

/// One immutable original index and its actual creating device. Query parameter,
/// result and staged scratch buffers are reused in submission order. Distinct
/// pending readbacks own distinct buffers and retain the complete source index.
pub struct GpuHitIndex {
    device: wgpu::Device,
    queue: wgpu::Queue,
    resources: Arc<Resources>,
    path: HitQueryExecution,
    families: u32,
    pipeline_layout: wgpu::PipelineLayout,
    point_group: wgpu::BindGroup,
    list_group: wgpu::BindGroup,
    shader: Option<wgpu::ShaderModule>,
    pipelines: HashMap<&'static str, wgpu::ComputePipeline>,
    metrics: HitQueryMetrics,
}
struct Resources {
    index: Arc<ArchiveHitIndex>,
    query: wgpu::Buffer,
    nodes: wgpu::Buffer,
    indices: wgpu::Buffer,
    primitives: wgpu::Buffer,
    segments: wgpu::Buffer,
    point: wgpu::Buffer,
    list: wgpu::Buffer,
    candidates: Option<wgpu::Buffer>,
    arguments: Option<wgpu::Buffer>,
    counts: [u32; 4],
}
impl GpuHitIndex {
    pub fn new(
        device: &wgpu::Device,
        queue: &wgpu::Queue,
        index: Arc<ArchiveHitIndex>,
        preference: HitQueryExecution,
        identity: HitQueryDeviceIdentity,
    ) -> Result<Self> {
        let path = preference.resolve(identity);
        let counts = [
            index.primitives().len(),
            index.nodes().len(),
            index.primitive_indices().len(),
            index.path_segments().len(),
        ]
        .map(|n| u32::try_from(n).map_err(|_| Error::Capacity));
        let [primitives, nodes, indices, segments] = counts;
        let counts = [primitives?, nodes?, indices?, segments?];
        if counts[..3].contains(&0) {
            return Err(Error::InvalidInput.into());
        }
        let limits = device.limits();
        let compute = HitQueryComputeLimits {
            max_storage_buffer_binding_size: u64::from(limits.max_storage_buffer_binding_size),
            max_storage_buffers_per_shader_stage: limits.max_storage_buffers_per_shader_stage,
            max_compute_invocations_per_workgroup: limits.max_compute_invocations_per_workgroup,
            max_compute_workgroup_size_x: limits.max_compute_workgroup_size_x,
            max_compute_workgroups_per_dimension: limits.max_compute_workgroups_per_dimension,
        };
        let ordered = path == HitQueryExecution::OrderedStages;
        if (ordered && !compute.admits_ordered(counts[2], limits.max_buffer_size))
            || (!ordered
                && (limits.max_storage_buffers_per_shader_stage < 6
                    || limits.max_compute_invocations_per_workgroup == 0
                    || limits.max_compute_workgroup_size_x == 0
                    || limits.max_compute_workgroups_per_dimension == 0))
        {
            return Err(Error::UnsupportedExecution.into());
        }
        let sizes = [
            40,
            u64::from(counts[1]) * 32,
            u64::from(counts[2]) * 4,
            u64::from(counts[0]) * 128,
            u64::from(counts[3].max(1)) * 48,
            32,
            (MAX_HIT_RESULTS as u64 + 1) * 32,
        ];
        if sizes.iter().any(|&bytes| {
            bytes > limits.max_buffer_size
                || bytes > u64::from(limits.max_storage_buffer_binding_size)
                || bytes > u64::from(u32::MAX)
        }) {
            return Err(Error::Capacity.into());
        }
        let families = index
            .primitives()
            .iter()
            .fold(0u32, |bits, p| bits | (1 << p.kind.min(8)));
        let storage = wgpu::BufferUsages::STORAGE | wgpu::BufferUsages::COPY_DST;
        let query = buffer(device, "ProGPU hit query", sizes[0], storage);
        let nodes = uploaded(
            device,
            "ProGPU hit nodes",
            bytemuck::cast_slice(index.nodes()),
        );
        let indices = uploaded(
            device,
            "ProGPU hit primitive references",
            bytemuck::cast_slice(index.primitive_indices()),
        );
        let primitives = uploaded(
            device,
            "ProGPU hit primitives",
            bytemuck::cast_slice(index.primitives()),
        );
        let segments = if index.path_segments().is_empty() {
            uploaded(device, "ProGPU empty hit path record", &[0; 48])
        } else {
            uploaded(
                device,
                "ProGPU hit paths",
                bytemuck::cast_slice(index.path_segments()),
            )
        };
        let point = buffer(
            device,
            "ProGPU hit result",
            sizes[5],
            storage | wgpu::BufferUsages::COPY_SRC,
        );
        let list = buffer(
            device,
            "ProGPU ordered hit results",
            sizes[6],
            storage | wgpu::BufferUsages::COPY_SRC,
        );
        let candidates = ordered.then(|| {
            buffer(
                device,
                "ProGPU ordered hit candidates",
                32 + 8 * u64::from(counts[2]),
                wgpu::BufferUsages::STORAGE | wgpu::BufferUsages::COPY_SRC,
            )
        });
        let arguments = ordered.then(|| {
            buffer(
                device,
                "ProGPU hit indirect dispatch",
                12,
                wgpu::BufferUsages::INDIRECT | wgpu::BufferUsages::COPY_DST,
            )
        });
        let index_buffer_bytes = sizes.iter().sum::<u64>()
            + candidates.as_ref().map_or(0, wgpu::Buffer::size)
            + arguments.as_ref().map_or(0, wgpu::Buffer::size);
        let resources = Arc::new(Resources {
            index,
            query,
            nodes,
            indices,
            primitives,
            segments,
            point,
            list,
            candidates,
            arguments,
            counts,
        });
        let layout = pipeline::layout(device, ordered);
        let pipeline_layout = device.create_pipeline_layout(&wgpu::PipelineLayoutDescriptor {
            label: Some("ProGPU retained hit query layout"),
            bind_group_layouts: &[Some(&layout)],
            immediate_size: 0,
        });
        let point_group = pipeline::bindings(device, &layout, &resources, &resources.point);
        let list_group = pipeline::bindings(device, &layout, &resources, &resources.list);
        Ok(Self {
            device: device.clone(),
            queue: queue.clone(),
            resources,
            path,
            families,
            pipeline_layout,
            point_group,
            list_group,
            shader: None,
            pipelines: HashMap::new(),
            metrics: HitQueryMetrics {
                index_buffer_bytes,
                index_upload_bytes: sizes[1..5].iter().sum(),
                ..Default::default()
            },
        })
    }
    pub fn index(&self) -> &Arc<ArchiveHitIndex> {
        &self.resources.index
    }
    pub fn execution(&self) -> HitQueryExecution {
        self.path
    }
    pub fn metrics(&self) -> HitQueryMetrics {
        self.metrics
    }
    pub fn is_owned_by(&self, device: &wgpu::Device) -> bool {
        self.device == *device
    }

    /// Original best-point query (flags zero), distinct from a one-slot list.
    pub async fn query_point(&mut self, point: [f32; 2]) -> Result<HitQueryWork> {
        self.begin(HitQueryShape::Point(point), None).await
    }
    /// Original bounded list query, retaining all raw output slots and counters.
    /// Zero capacity returns the source's default summary without dispatch.
    pub async fn query_all(
        &mut self,
        shape: HitQueryShape,
        capacity: usize,
    ) -> Result<HitQueryWork> {
        let capacity = capacity.min(MAX_HIT_RESULTS);
        if capacity == 0 {
            return Ok(HitQueryWork::empty(self.resources.index.clone(), self.path));
        }
        self.begin(shape, Some(capacity)).await
    }
    async fn begin(
        &mut self,
        shape: HitQueryShape,
        capacity: Option<usize>,
    ) -> Result<HitQueryWork> {
        let (point, region_max, flags, mode) = shape.parameters();
        let mut entries = [("", false); 12];
        let mut entry_count = 0;
        if self.path == HitQueryExecution::OrderedStages {
            entries[entry_count] = ("cs_collect", false);
            entry_count += 1;
            for family in 0..10 {
                if family == 0 || self.families & (1 << (family - 1)) != 0 {
                    entries[entry_count] = (pipeline::STAGES[mode][family], true);
                    entry_count += 1;
                }
            }
            entries[entry_count] = ("cs_merge", false);
            entry_count += 1;
        } else {
            entries[entry_count] = (["cs_point", "cs_bounds", "cs_ellipse"][mode], false);
            entry_count += 1;
        }
        let entries = &entries[..entry_count];
        for &(entry, _) in entries {
            self.prepare_pipeline(entry).await?;
        }
        let r = &self.resources;
        let query = Query {
            point,
            region_max,
            root_node_index: 0,
            primitive_count: r.counts[0],
            node_count: r.counts[1],
            primitive_index_count: r.counts[2],
            flags: flags | capacity.unwrap_or(0) as u32,
            path_segment_count: r.counts[3],
        };
        let elements = capacity.map_or(1, |n| n + 1);
        let bytes = elements as u64 * 32;
        let output = if capacity.is_some() {
            &r.list
        } else {
            &r.point
        };
        let group = if capacity.is_some() {
            &self.list_group
        } else {
            &self.point_group
        };
        let initial = [HitResult::MISS; MAX_HIT_RESULTS + 1];
        self.queue
            .write_buffer(&r.query, 0, bytemuck::bytes_of(&query));
        self.queue
            .write_buffer(output, 0, bytemuck::cast_slice(&initial[..elements]));
        let staging = buffer(
            &self.device,
            "ProGPU hit readback",
            bytes,
            wgpu::BufferUsages::MAP_READ | wgpu::BufferUsages::COPY_DST,
        );
        let mut encoder = self
            .device
            .create_command_encoder(&wgpu::CommandEncoderDescriptor {
                label: Some("ProGPU original ordered hit query"),
            });
        for &(entry, indirect) in entries {
            {
                let mut pass = encoder.begin_compute_pass(&wgpu::ComputePassDescriptor {
                    label: Some(entry),
                    timestamp_writes: None,
                });
                pass.set_pipeline(self.pipelines.get(entry).ok_or(Error::ResourceDomain)?);
                pass.set_bind_group(0, group, &[]);
                if indirect {
                    pass.dispatch_workgroups_indirect(
                        r.arguments.as_ref().ok_or(Error::ResourceDomain)?,
                        0,
                    );
                } else {
                    pass.dispatch_workgroups(1, 1, 1);
                }
            }
            if entry == "cs_collect" {
                encoder.copy_buffer_to_buffer(
                    r.candidates.as_ref().ok_or(Error::ResourceDomain)?,
                    16,
                    r.arguments.as_ref().ok_or(Error::ResourceDomain)?,
                    0,
                    12,
                );
            }
        }
        encoder.copy_buffer_to_buffer(output, 0, &staging, 0, bytes);
        let submission = self.queue.submit([encoder.finish()]);
        let submitted_owners = self.resources.clone();
        self.queue
            .on_submitted_work_done(move || drop(submitted_owners));
        self.metrics.submissions += 1;
        self.metrics.compute_passes += entries.len() as u64;
        self.metrics.query_upload_bytes += 40 + bytes;
        self.metrics.readback_bytes += bytes;
        Ok(HitQueryWork::pending(
            self.device.clone(),
            staging,
            submission,
            self.resources.clone(),
            self.path,
            capacity,
        ))
    }
}
fn uploaded(device: &wgpu::Device, label: &str, contents: &[u8]) -> wgpu::Buffer {
    device.create_buffer_init(&wgpu::util::BufferInitDescriptor {
        label: Some(label),
        contents,
        usage: wgpu::BufferUsages::STORAGE | wgpu::BufferUsages::COPY_DST,
    })
}
fn buffer(
    device: &wgpu::Device,
    label: &str,
    size: u64,
    usage: wgpu::BufferUsages,
) -> wgpu::Buffer {
    device.create_buffer(&wgpu::BufferDescriptor {
        label: Some(label),
        size,
        usage,
        mapped_at_creation: false,
    })
}
