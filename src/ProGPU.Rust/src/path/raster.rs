use super::{ExecutionPath, ExecutionPreference, Path, RasterRequest};
use crate::vector::GpuPathRecord as Record;
use crate::{Error, shaders};
use bytemuck::{Pod, Zeroable};
use wgpu::util::DeviceExt;

/// Submission-owned R8 coverage, ready for GPU sampling without CPU readback.
pub struct CoverageMask {
    texture: wgpu::Texture,
    request: RasterRequest,
}

impl CoverageMask {
    /// Retained coverage texture. Callers must not explicitly destroy it while in use.
    pub fn texture(&self) -> &wgpu::Texture {
        &self.texture
    }
    /// Actual source-to-raster request used for this mask.
    pub fn request(&self) -> RasterRequest {
        self.request
    }
    /// Execution route used to produce the mask.
    pub fn execution_path(&self) -> ExecutionPath {
        ExecutionPath::NativeCompute
    }
}

/// One original request and its immutable position in a shared coverage atlas.
#[derive(Clone, Copy, Debug)]
pub struct AtlasTile {
    /// Physical texel origin within the atlas.
    pub origin: [u32; 2],
    /// Original source-to-raster request.
    pub request: RasterRequest,
}

/// One device-owned R8 atlas generation, populated by a single batched submission.
pub struct CoverageAtlas {
    pub(crate) device: wgpu::Device,
    texture: wgpu::Texture,
    tiles: Vec<AtlasTile>,
}

impl CoverageAtlas {
    /// Original request order and exact atlas placement.
    pub fn tiles(&self) -> &[AtlasTile] {
        &self.tiles
    }
    /// Texture retained by this generation; do not explicitly destroy it in use.
    pub fn texture(&self) -> &wgpu::Texture {
        &self.texture
    }
}

#[repr(C)]
#[derive(Clone, Copy, Pod, Zeroable)]
struct Uniforms {
    origin: [f32; 2],
    scale: [f32; 2],
    path_index: u32,
    output_offset_words: u32,
    output_row_words: u32,
    width: u32,
    height: u32,
    sample_grid: u32,
    path_index_b: u32,
    path_op_kind: u32,
}

const _: () = {
    assert!(size_of::<Uniforms>() == 48);
    assert!(size_of::<Record>() == 32);
};

struct Dispatch {
    offset: u64,
    bytes: u64,
    size: [u32; 3],
}

/// Native-compute path rasterization. Optional and created only on demand.
///
/// Uses the canonical operation-zero kernel for all six segment kinds. Boolean
/// batches use the separate original ordinary evaluator, prepared explicitly
/// on demand. No segment inventory is relabeled linear to select another kernel.
pub struct PathRasterizer {
    device: wgpu::Device,
    queue: wgpu::Queue,
    pipeline: wgpu::ComputePipeline,
    boolean_pipeline: Option<wgpu::ComputePipeline>,
}

impl PathRasterizer {
    /// Create the path kernel. Unsupported forced preferences return an error;
    /// no CPU fallback, alternate shader approximation or compiler flag is used.
    pub async fn new(
        device: &wgpu::Device,
        queue: &wgpu::Queue,
        preference: ExecutionPreference,
    ) -> Result<Self, Error> {
        let limits = device.limits();
        if !matches!(
            preference,
            ExecutionPreference::Automatic | ExecutionPreference::NativeCompute
        ) || limits.max_compute_invocations_per_workgroup < 256
            || limits.max_compute_workgroup_size_x < 16
            || limits.max_compute_workgroup_size_y < 16
            || limits.max_storage_buffers_per_shader_stage < 4
        {
            return Err(Error::UnsupportedExecution);
        }
        let scope = device.push_error_scope(wgpu::ErrorFilter::Validation);
        let module = device.create_shader_module(wgpu::ShaderModuleDescriptor {
            label: Some("ProGPU canonical PathRasterizer.wgsl"),
            source: wgpu::ShaderSource::Wgsl(shaders::PATH.into()),
        });
        let pipeline = device.create_compute_pipeline(&wgpu::ComputePipelineDescriptor {
            label: Some("ProGPU single path compute"),
            layout: None,
            module: &module,
            entry_point: Some("cs_main_single_path"),
            compilation_options: Default::default(),
            cache: None,
        });
        if let Some(error) = scope.pop().await {
            return Err(error.into());
        }
        Ok(Self {
            device: device.clone(),
            queue: queue.clone(),
            pipeline,
            boolean_pipeline: None,
        })
    }

    /// Lazily prepare the original general path/Boolean evaluator. Call before
    /// rasterizing any input with Path::is_boolean(); ordinary-only users keep
    /// their original single-path compilation and allocation behavior.
    pub async fn prepare_boolean(&mut self) -> Result<(), Error> {
        if self.boolean_pipeline.is_some() {
            return Ok(());
        }
        let scope = self.device.push_error_scope(wgpu::ErrorFilter::Validation);
        let module = self
            .device
            .create_shader_module(wgpu::ShaderModuleDescriptor {
                label: Some("ProGPU canonical Boolean path rasterizer"),
                source: wgpu::ShaderSource::Wgsl(shaders::PATH.into()),
            });
        let pipeline = self
            .device
            .create_compute_pipeline(&wgpu::ComputePipelineDescriptor {
                label: Some("ProGPU ordinary Boolean path compute"),
                layout: None,
                module: &module,
                entry_point: Some("cs_main_ordinary"),
                compilation_options: Default::default(),
                cache: None,
            });
        if let Some(error) = scope.pop().await {
            return Err(error.into());
        }
        self.boolean_pipeline = Some(pipeline);
        Ok(())
    }

    /// Resolved implementation for performance and quality receipts.
    pub fn execution_path(&self) -> ExecutionPath {
        ExecutionPath::NativeCompute
    }

    /// Rasterize a nonempty independent path batch into one shared atlas.
    /// The native shelf packing, two-texel margins and 1024..8192 growth remain
    /// unchanged. Device limits are checked before encoding. No temporary
    /// per-path texture, extra copy submission or CPU readback is introduced.
    pub fn rasterize_atlas(
        &self,
        paths: &[(&Path, RasterRequest)],
    ) -> Result<CoverageAtlas, Error> {
        let layout = crate::scene::AtlasLayout::new(paths.iter().map(|(_, r)| r.size))?;
        if layout.size == 0 {
            return Err(Error::InvalidInput);
        }
        if layout.size > self.device.limits().max_texture_dimension_2d {
            return Err(Error::Capacity);
        }
        let (mut encoder, output, uniforms) = self.encode_batch(paths)?;
        let texture = self.device.create_texture(&wgpu::TextureDescriptor {
            label: Some("ProGPU immutable path atlas"),
            size: wgpu::Extent3d {
                width: layout.size,
                height: layout.size,
                depth_or_array_layers: 1,
            },
            mip_level_count: 1,
            sample_count: 1,
            dimension: wgpu::TextureDimension::D2,
            format: wgpu::TextureFormat::R8Unorm,
            usage: wgpu::TextureUsages::TEXTURE_BINDING
                | wgpu::TextureUsages::COPY_DST
                | wgpu::TextureUsages::COPY_SRC,
            view_formats: &[],
        });
        let mut tiles = Vec::with_capacity(paths.len());
        for ((&(_, request), uniform), origin) in paths.iter().zip(&uniforms).zip(layout.origins) {
            let mut destination = texture.as_image_copy();
            destination.origin = wgpu::Origin3d {
                x: origin[0],
                y: origin[1],
                z: 0,
            };
            encoder.copy_buffer_to_texture(
                wgpu::TexelCopyBufferInfo {
                    buffer: &output,
                    layout: wgpu::TexelCopyBufferLayout {
                        offset: u64::from(uniform.output_offset_words) * 4,
                        bytes_per_row: Some(uniform.output_row_words * 4),
                        rows_per_image: Some(request.size[1]),
                    },
                },
                destination,
                wgpu::Extent3d {
                    width: request.size[0],
                    height: request.size[1],
                    depth_or_array_layers: 1,
                },
            );
            tiles.push(AtlasTile { origin, request });
        }
        self.queue.submit([encoder.finish()]);
        Ok(CoverageAtlas {
            device: self.device.clone(),
            texture,
            tiles,
        })
    }

    /// Rasterize independent paths in one compute pass and one submission.
    ///
    /// The complete batch is validated before any GPU mutation. Dispatches group
    /// actual extents with exact 48-byte records and 256-byte binding alignment,
    /// matching `progpu_native_path_dispatch.hpp`. Coverage remains in original
    /// request order. Compute writes four R8 pixels per word; GPU copies preserve
    /// 256-byte row pitch and 512-byte source offsets. No CPU coverage is computed.
    pub fn rasterize_batch(
        &self,
        paths: &[(&Path, RasterRequest)],
    ) -> Result<Vec<CoverageMask>, Error> {
        if paths.is_empty() {
            return Ok(Vec::new());
        }
        let (mut encoder, output, uniforms) = self.encode_batch(paths)?;
        let masks: Vec<_> = paths
            .iter()
            .zip(&uniforms)
            .map(|(&(_, request), uniform)| {
                let texture = self.device.create_texture(&wgpu::TextureDescriptor {
                    label: Some("ProGPU retained R8 path mask"),
                    size: wgpu::Extent3d {
                        width: request.size[0],
                        height: request.size[1],
                        depth_or_array_layers: 1,
                    },
                    mip_level_count: 1,
                    sample_count: 1,
                    dimension: wgpu::TextureDimension::D2,
                    format: wgpu::TextureFormat::R8Unorm,
                    usage: wgpu::TextureUsages::TEXTURE_BINDING
                        | wgpu::TextureUsages::COPY_DST
                        | wgpu::TextureUsages::COPY_SRC,
                    view_formats: &[],
                });
                encoder.copy_buffer_to_texture(
                    wgpu::TexelCopyBufferInfo {
                        buffer: &output,
                        layout: wgpu::TexelCopyBufferLayout {
                            offset: u64::from(uniform.output_offset_words) * 4,
                            bytes_per_row: Some(uniform.output_row_words * 4),
                            rows_per_image: Some(request.size[1]),
                        },
                    },
                    texture.as_image_copy(),
                    texture.size(),
                );
                CoverageMask { texture, request }
            })
            .collect();
        self.queue.submit([encoder.finish()]);
        Ok(masks)
    }

    fn encode_batch(
        &self,
        paths: &[(&Path, RasterRequest)],
    ) -> Result<(wgpu::CommandEncoder, wgpu::Buffer, Vec<Uniforms>), Error> {
        if paths.len() > 1 << 20 {
            return Err(Error::Capacity);
        }
        let pipeline = if paths.iter().any(|(path, _)| path.is_boolean()) {
            self.boolean_pipeline
                .as_ref()
                .ok_or(Error::UnsupportedExecution)?
        } else {
            &self.pipeline
        };
        let limits = self.device.limits();
        let binding_limit = limits
            .max_storage_buffer_binding_size
            .min(limits.max_buffer_size);
        let total_segments = paths.iter().try_fold(0usize, |count, (path, _)| {
            count
                .checked_add(path.segments.len())
                .ok_or(Error::Capacity)
        })?;
        let total_records = paths.iter().try_fold(0usize, |count, (path, _)| {
            let added = path
                .program
                .as_ref()
                .map_or(1, |p| p.records.len() + p.tokens.len());
            count.checked_add(added).ok_or(Error::Capacity)
        })?;
        if total_segments as u64 > binding_limit / size_of::<super::Segment>() as u64
            || total_records as u64 > binding_limit / size_of::<Record>() as u64
        {
            return Err(Error::Capacity);
        }
        let mut records = Vec::with_capacity(total_records);
        let mut segments = Vec::with_capacity(total_segments);
        let mut uniforms = Vec::with_capacity(paths.len());
        let mut output_size = 0u64;
        for &(path, request) in paths {
            let [width, height] = request.size;
            if width == 0
                || height == 0
                || width > limits.max_texture_dimension_2d
                || height > limits.max_texture_dimension_2d
                || !(1..=8).contains(&request.sample_grid)
                || request.origin.iter().any(|v| !v.is_finite())
                || request.scale.iter().any(|v| !v.is_finite() || *v <= 0.0)
                || width.div_ceil(64) > limits.max_compute_workgroups_per_dimension
                || height.div_ceil(16) > limits.max_compute_workgroups_per_dimension
            {
                return Err(Error::InvalidInput);
            }
            for axis in 0..2 {
                let end = request.origin[axis] + request.size[axis] as f32;
                if !end.is_finite()
                    || !(end / request.scale[axis]).is_finite()
                    || !(request.origin[axis] / request.scale[axis]).is_finite()
                {
                    return Err(Error::InvalidInput);
                }
            }
            let row_bytes = u64::from(width).div_ceil(256) * 256;
            u32::try_from(row_bytes).map_err(|_| Error::Capacity)?;
            output_size = output_size.checked_add(511).ok_or(Error::Capacity)? & !511;
            let offset = output_size;
            output_size = output_size
                .checked_add(row_bytes * u64::from(height))
                .ok_or(Error::Capacity)?;
            if output_size > binding_limit || output_size / 4 > u64::from(u32::MAX) {
                return Err(Error::Capacity);
            }
            let segment_count = u32::try_from(path.segments.len()).map_err(|_| Error::Capacity)?;
            let start_segment = u32::try_from(segments.len()).map_err(|_| Error::Capacity)?;
            start_segment
                .checked_add(segment_count)
                .ok_or(Error::Capacity)?;
            let path_index = u32::try_from(records.len()).map_err(|_| Error::Capacity)?;
            let (path_index_b, path_op_kind) = if let Some(program) = &path.program {
                for record in &program.records {
                    records.push(Record {
                        start_segment: record
                            .start_segment
                            .checked_add(start_segment)
                            .ok_or(Error::Capacity)?,
                        ..*record
                    });
                }
                // Tokens are relative leaf record indices or tagged operation/
                // empty values. Only the program's base record is rebased.
                records.extend_from_slice(&program.tokens);
                (
                    path_index
                        .checked_add(program.index_b)
                        .ok_or(Error::Capacity)?,
                    program.operation,
                )
            } else {
                records.push(Record {
                    start_segment,
                    segment_count,
                    bounds: path.bounds,
                    fill_rule: path.fill_rule as u32,
                    pad: 0,
                });
                (0, 0)
            };
            segments.extend_from_slice(&path.segments);
            uniforms.push(Uniforms {
                origin: request.origin,
                scale: request.scale,
                path_index,
                output_offset_words: u32::try_from(offset / 4).map_err(|_| Error::Capacity)?,
                output_row_words: u32::try_from(row_bytes / 4).map_err(|_| Error::Capacity)?,
                width,
                height,
                sample_grid: request.sample_grid,
                path_index_b,
                path_op_kind,
            });
        }
        if output_size > binding_limit
            || std::mem::size_of_val(records.as_slice()) as u64 > binding_limit
            || std::mem::size_of_val(segments.as_slice()) as u64 > binding_limit
        {
            return Err(Error::Capacity);
        }
        let (uniform_bytes, dispatches) = group_dispatches(
            &uniforms,
            limits.max_compute_workgroups_per_dimension,
            u64::from(limits.min_storage_buffer_offset_alignment).max(256),
            limits.max_buffer_size,
        )?;
        if uniform_bytes.len() as u64 > limits.max_buffer_size
            || dispatches.iter().any(|d| d.bytes > binding_limit)
        {
            return Err(Error::Capacity);
        }
        // All structural validation is complete. Candidate resources are owned
        // locally until the single command buffer has been submitted.
        let storage = |label, bytes| {
            self.device
                .create_buffer_init(&wgpu::util::BufferInitDescriptor {
                    label: Some(label),
                    contents: bytes,
                    usage: wgpu::BufferUsages::STORAGE,
                })
        };
        let uniform_buffer = storage("ProGPU grouped path uniforms", &uniform_bytes);
        let record_buffer = storage("ProGPU path records", bytemuck::cast_slice(&records));
        let segment_buffer = storage("ProGPU path segments", bytemuck::cast_slice(&segments));
        let output = self.device.create_buffer(&wgpu::BufferDescriptor {
            label: Some("ProGPU packed path coverage"),
            size: output_size,
            usage: wgpu::BufferUsages::STORAGE | wgpu::BufferUsages::COPY_SRC,
            mapped_at_creation: false,
        });
        let layout = pipeline.get_bind_group_layout(0);
        let bindings: Vec<_> = dispatches
            .iter()
            .map(|dispatch| {
                self.device.create_bind_group(&wgpu::BindGroupDescriptor {
                    label: Some("ProGPU path extent group"),
                    layout: &layout,
                    entries: &[
                        wgpu::BindGroupEntry {
                            binding: 0,
                            resource: wgpu::BindingResource::Buffer(wgpu::BufferBinding {
                                buffer: &uniform_buffer,
                                offset: dispatch.offset,
                                size: std::num::NonZeroU64::new(dispatch.bytes),
                            }),
                        },
                        wgpu::BindGroupEntry {
                            binding: 1,
                            resource: record_buffer.as_entire_binding(),
                        },
                        wgpu::BindGroupEntry {
                            binding: 2,
                            resource: segment_buffer.as_entire_binding(),
                        },
                        wgpu::BindGroupEntry {
                            binding: 3,
                            resource: output.as_entire_binding(),
                        },
                    ],
                })
            })
            .collect();
        let mut encoder = self
            .device
            .create_command_encoder(&wgpu::CommandEncoderDescriptor {
                label: Some("ProGPU batched path rasterization"),
            });
        {
            let mut pass = encoder.begin_compute_pass(&wgpu::ComputePassDescriptor {
                label: Some("ProGPU paths"),
                timestamp_writes: None,
            });
            pass.set_pipeline(pipeline);
            for (dispatch, binding) in dispatches.iter().zip(&bindings) {
                pass.set_bind_group(0, binding, &[]);
                pass.dispatch_workgroups(dispatch.size[0], dispatch.size[1], dispatch.size[2]);
            }
        }
        Ok((encoder, output, uniforms))
    }
}

fn group_dispatches(
    uniforms: &[Uniforms],
    max_z: u32,
    alignment: u64,
    max_bytes: u64,
) -> Result<(Vec<u8>, Vec<Dispatch>), Error> {
    if max_z == 0 {
        return Err(Error::UnsupportedExecution);
    }
    let key = |index: usize| {
        (
            uniforms[index].width.div_ceil(64),
            uniforms[index].height.div_ceil(16),
        )
    };
    let mut order: Vec<_> = (0..uniforms.len()).collect();
    order.sort_unstable_by_key(|&index| (key(index), index));
    let mut bytes = Vec::new();
    let mut groups = Vec::new();
    let mut first = 0;
    while first < order.len() {
        let mut end = first + 1;
        while end < order.len()
            && end - first < max_z.min(65535) as usize
            && key(order[first]) == key(order[end])
        {
            end += 1;
        }
        let offset = (bytes.len() as u64).div_ceil(alignment) * alignment;
        let count = (end - first) as u32;
        let size = u64::from(count) * size_of::<Uniforms>() as u64;
        let new_len = usize::try_from(offset.checked_add(size).ok_or(Error::Capacity)?)
            .map_err(|_| Error::Capacity)?;
        if new_len as u64 > max_bytes {
            return Err(Error::Capacity);
        }
        bytes.resize(new_len, 0);
        for (target, &source) in bytes[offset as usize..]
            .chunks_exact_mut(48)
            .zip(&order[first..end])
        {
            target.copy_from_slice(bytemuck::bytes_of(&uniforms[source]));
        }
        let (x, y) = key(order[first]);
        groups.push(Dispatch {
            offset,
            bytes: size,
            size: [x, y, count],
        });
        first = end;
    }
    Ok((bytes, groups))
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    #[ignore = "requires C++20 and the source checkout; mandatory native parity lane"]
    fn original_native_dispatch_plan_matches() {
        use std::process::{Command, Stdio};
        let root = std::path::Path::new(env!("CARGO_MANIFEST_DIR"));
        let directory =
            std::env::temp_dir().join(format!("progpu-path-oracle-{}", std::process::id()));
        std::fs::create_dir_all(&directory).unwrap();
        let executable = directory.join("path-oracle");
        assert!(
            Command::new(std::env::var_os("CXX").unwrap_or_else(|| "clang++".into()))
                .args([
                    "-std=c++20",
                    "-O2",
                    "-ffp-contract=off",
                    "-Wall",
                    "-Wextra",
                    "-Werror"
                ])
                .arg(format!(
                    "-I{}",
                    root.join("src/ProGPU.Native/include").display()
                ))
                .arg(format!(
                    "-I{}",
                    root.join("src/ProGPU.Native/src/Backend").display()
                ))
                .arg(root.join("src/ProGPU.Rust/tests/native_geometry.cpp"))
                .arg("-o")
                .arg(&executable)
                .status()
                .unwrap()
                .success()
        );
        // Include more than one native Z-limit of identical extents, several
        // different workgroup dimensions, signed zero and fractional origins.
        let source: Vec<_> = (0..65_540u32)
            .map(|index| Uniforms {
                width: if index < 65_536 {
                    64
                } else {
                    index % 3 * 64 + 1
                },
                height: if index < 65_536 {
                    16
                } else {
                    index % 2 * 16 + 1
                },
                origin: [-0.0, index as f32 * 0.125],
                scale: [1.25, 0.75],
                path_index: index,
                output_offset_words: index * 256,
                output_row_words: 64,
                sample_grid: 8,
                path_index_b: 0,
                path_op_kind: 0,
            })
            .collect();
        let (bytes, groups) = group_dispatches(&source, 65_535, 256, 16 * 1024 * 1024).unwrap();
        let mut expected = (groups.len() as u64).to_ne_bytes().to_vec();
        for group in groups {
            for value in [
                group.offset,
                group.bytes,
                u64::from(group.size[0]),
                u64::from(group.size[1]),
                u64::from(group.size[2]),
            ] {
                expected.extend_from_slice(&value.to_ne_bytes());
            }
        }
        expected.extend_from_slice(&bytes);
        let input_path = directory.join("input.bin");
        std::fs::write(&input_path, bytemuck::cast_slice(&source)).unwrap();
        let output = Command::new(executable)
            .arg("dispatch")
            .stdin(Stdio::from(std::fs::File::open(input_path).unwrap()))
            .output()
            .unwrap();
        assert!(output.status.success());
        assert_eq!(output.stdout.len(), expected.len());
        assert!(
            output.stdout == expected,
            "original native dispatch plan bytes differ"
        );
        eprintln!(
            "native_path_dispatch: {} records, {} exact bytes",
            source.len(),
            expected.len()
        );
        std::fs::remove_dir_all(directory).unwrap();
    }
    #[test]
    fn dispatches_retain_exact_uniform_bytes_and_binding_ranges() {
        let source: Vec<_> = [(129, 16), (64, 17), (63, 17), (64, 17)]
            .into_iter()
            .enumerate()
            .map(|(index, (width, height))| Uniforms {
                width,
                height,
                path_index: index as u32,
                origin: [-0.0, 0.125],
                ..Uniforms::zeroed()
            })
            .collect();
        let (bytes, groups) = group_dispatches(&source, 2, 256, 4096).unwrap();
        assert!(group_dispatches(&source, 2, 256, 512).is_err());
        assert_eq!(groups.len(), 3);
        for (group, expected) in groups.iter().zip([&[1usize, 2][..], &[3][..], &[0][..]]) {
            assert_eq!(group.offset % 256, 0);
            assert_eq!(group.bytes, expected.len() as u64 * 48);
            for (slot, &index) in expected.iter().enumerate() {
                let start = group.offset as usize + slot * 48;
                assert_eq!(
                    &bytes[start..start + 48],
                    bytemuck::bytes_of(&source[index])
                );
            }
        }
    }
}
