use super::*;
use wgpu::util::DeviceExt;

/// Exact original 224-byte GPU frame, independent of pointer-sized frame ABI.
/// The explicit reserved words keep bounded Texture/ROP tags separate from the
/// negative original root glyph coverage certificate.
#[repr(C)]
#[derive(Clone, Copy, Debug, PartialEq)]
pub struct NativeGpuUniforms {
    pub projection: [f32; 16],
    pub model_view_projection: [f32; 16],
    pub view: [f32; 16],
    pub canvas_size: [f32; 2],
    pub dpi_scale: f32,
    pub pad0: f32,
    pub render_origin: [f32; 2],
    pub pad1: [f32; 2],
}
const _: () = {
    assert!(size_of::<NativeGpuUniforms>() == 224);
    assert!(std::mem::offset_of!(NativeGpuUniforms, pad0) == 204);
};
fn identity() -> [f32; 16] {
    [
        1.0, 0.0, 0.0, 0.0, 0.0, 1.0, 0.0, 0.0, 0.0, 0.0, 1.0, 0.0, 0.0, 0.0, 0.0, 1.0,
    ]
}
impl NativeGpuUniforms {
    pub fn new(size: [u32; 2], dpi_scale: f32) -> Result<Self, Error> {
        if size.contains(&0) || !dpi_scale.is_finite() || dpi_scale <= 0.0 {
            return Err(Error::InvalidInput);
        }
        let [width, height] = size.map(|n| n as f32 / dpi_scale);
        let mut projection = identity();
        projection[0] = 2.0 / width;
        projection[5] = -2.0 / height;
        projection[10] = -1.0;
        projection[12] = -1.0;
        projection[13] = 1.0;
        if !width.is_finite() || !height.is_finite() || !projection.iter().all(|v| v.is_finite()) {
            return Err(Error::InvalidInput);
        }
        Ok(Self {
            projection,
            model_view_projection: identity(),
            view: identity(),
            canvas_size: [width, height],
            dpi_scale,
            pad0: 0.0,
            render_origin: [0.0; 2],
            pad1: [-(size[0] as f32), -(size[1] as f32)],
        })
    }
    /// Only the actual unshifted full-target pass may request this certificate.
    /// Failure clears the certificate; it never rewrites projection or coverage.
    pub fn certify_root_glyph_coverage(&mut self, target: [u32; 2], viewport: [f32; 4]) -> bool {
        self.pad0 = 0.0;
        let [physical_width, physical_height] = target.map(|n| n as f32);
        let dpi = self.dpi_scale;
        if target.contains(&0)
            || physical_width as f64 != target[0] as f64
            || physical_height as f64 != target[1] as f64
            || !dpi.is_finite()
            || dpi <= 0.0
            || viewport != [0.0, 0.0, physical_width, physical_height]
            || self.render_origin != [0.0; 2]
        {
            return false;
        }
        let width = physical_width / dpi;
        let height = physical_height / dpi;
        if !width.is_finite()
            || !height.is_finite()
            || width <= 0.0
            || height <= 0.0
            || width * dpi != physical_width
            || height * dpi != physical_height
            || self.canvas_size != [width, height]
        {
            return false;
        }
        let mut projection = [0.0; 16];
        projection[0] = 2.0 / width;
        projection[5] = -2.0 / height;
        projection[10] = -1.0;
        projection[12] = -1.0;
        projection[13] = 1.0;
        projection[15] = 1.0;
        if !projection[0].is_finite()
            || !projection[5].is_finite()
            || projection[0] <= 0.0
            || projection[5] >= 0.0
            || self.projection != projection
            || self.model_view_projection != identity()
            || self.view != identity()
        {
            return false;
        }
        self.pad0 = -1.0;
        true
    }
    pub fn to_bytes(self) -> [u8; 224] {
        let mut bytes = [0; 224];
        let tail = [
            self.canvas_size[0],
            self.canvas_size[1],
            self.dpi_scale,
            self.pad0,
            self.render_origin[0],
            self.render_origin[1],
            self.pad1[0],
            self.pad1[1],
        ];
        for (target, value) in bytes.chunks_exact_mut(4).zip(
            self.projection
                .into_iter()
                .chain(self.model_view_projection)
                .chain(self.view)
                .chain(tail),
        ) {
            target.copy_from_slice(&value.to_le_bytes());
        }
        bytes
    }
}
/// Immutable bindings for one actual physical target. Keeping each frame's
/// uniform buffer prevents a later target from mutating earlier encoded draws.
pub struct NativeFrameBindings {
    uniforms: NativeGpuUniforms,
    material: Arc<PreparedNativeMaterials>,
    pub buffer: wgpu::Buffer,
    pub frame: wgpu::BindGroup,
    pub vector: wgpu::BindGroup,
    pub text: wgpu::BindGroup,
}
impl NativeFrameBindings {
    pub fn uniforms(&self) -> NativeGpuUniforms {
        self.uniforms
    }
    pub fn material(&self) -> &Arc<PreparedNativeMaterials> {
        &self.material
    }
    pub fn upload_bytes(&self) -> u64 {
        224
    }
    pub async fn prepare(
        pipelines: &NativePipelineCache,
        material: Arc<PreparedNativeMaterials>,
        uniforms: NativeGpuUniforms,
    ) -> Result<Self, Error> {
        let device = pipelines.device();
        if device != material.device() {
            return Err(Error::ResourceDomain);
        }
        let scope = device.push_error_scope(wgpu::ErrorFilter::Validation);
        let buffer = device.create_buffer_init(&wgpu::util::BufferInitDescriptor {
            label: Some("Native immutable physical-target frame"),
            contents: &uniforms.to_bytes(),
            usage: wgpu::BufferUsages::UNIFORM,
        });
        let layouts = pipelines.layouts();
        let frame = layout::bind(device, &layouts.frame, &[layout::buffer_entry(0, &buffer)]);
        let vector = layout::bind(
            device,
            &layouts.vector,
            &[
                layout::buffer_entry(0, &buffer),
                layout::buffer_entry(1, &material.brush_buffer),
                layout::buffer_entry(2, &material.gradient_buffer),
            ],
        );
        let text = layout::bind(
            device,
            &layouts.text,
            &[
                layout::buffer_entry(0, &buffer),
                layout::buffer_entry(1, &material.style_buffer),
            ],
        );
        if let Some(error) = scope.pop().await {
            return Err(error.into());
        }
        Ok(Self {
            uniforms,
            material,
            buffer,
            frame,
            vector,
            text,
        })
    }
}
