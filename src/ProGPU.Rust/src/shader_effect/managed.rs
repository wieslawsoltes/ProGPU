//! Original managed WGSL parameters, sampler precedence and retained output quads.
use super::*;
#[cfg(feature = "gpu")]
use crate::image::{GpuImage, ImageSampling};
use crate::{geometry::Affine, image::ImageAlpha, shaders};
use std::fmt::Write;

/// Immutable original WGSL effect body. The body defines `wpf_effect_main` and
/// uses the canonical constant/source/register helpers supplied by this module.
#[derive(Clone, Debug)]
pub struct ManagedShaderProgram {
    source: Arc<str>,
    key: Arc<str>,
    source_key: u64,
}
impl ManagedShaderProgram {
    /// Retain exact source and optional caller key. Blank source selects the
    /// original pass-through; blank keys use the original UTF-16 FNV-1a identity.
    /// Shader syntax is admitted by the actual device during GPU preparation.
    pub fn new(source: impl Into<Arc<str>>, key: impl Into<Arc<str>>) -> Self {
        let source = source.into();
        let source = if source.trim().is_empty() {
            Arc::from(shaders::WPF_EFFECT_PASS_THROUGH)
        } else {
            source
        };
        let source_key = source
            .encode_utf16()
            .fold(14695981039346656037_u64, |hash, c| {
                (hash ^ u64::from(c)).wrapping_mul(1099511628211)
            });
        let key = key.into();
        let key = if key.trim().is_empty() {
            Arc::from(format!("wpf_shader_{source_key:016x}"))
        } else {
            key
        };
        Self {
            source,
            key,
            source_key,
        }
    }
    /// Original default effect with no source replacement or hidden transform.
    pub fn pass_through() -> Self {
        Self::new("", "")
    }
    /// Exact source generation used for pipeline identity, even with a custom key.
    pub fn source(&self) -> &str {
        &self.source
    }
    /// Original explicit or derived stable shader key.
    pub fn key(&self) -> &str {
        &self.key
    }
    /// Original FNV-1a hash over UTF-16 code units, not UTF-8 bytes.
    pub fn source_key(&self) -> u64 {
        self.source_key
    }

    /// Compose the unchanged managed header/body/wrapper and original ordered
    /// active-register declarations. Missing registers sample transparent black.
    pub fn assemble(
        &self,
        registers: &[u32],
        include_mask: bool,
        source_alpha: ImageAlpha,
        pipeline_alpha: ImageAlpha,
    ) -> Result<String, Error> {
        if registers.len() > 16
            || registers.iter().any(|r| *r >= 16)
            || registers.windows(2).any(|r| r[0] >= r[1])
        {
            return Err(Error::InvalidInput);
        }
        let mut text = String::from(shaders::WPF_EFFECT_HEADER);
        for (slot, register) in registers.iter().enumerate() {
            writeln!(
                text,
                "@group(2) @binding({}) var sourceSampler{register}: sampler;",
                slot * 2
            )
            .unwrap();
            writeln!(
                text,
                "@group(2) @binding({}) var sourceTexture{register}: texture_2d<f32>;",
                slot * 2 + 1
            )
            .unwrap();
        }
        if include_mask {
            text.push_str(shaders::WPF_EFFECT_MASK_DECLARATIONS);
        }
        text.push_str("\nfn wpf_sample_register(index: u32, uv: vec2<f32>) -> vec4<f32> {\n");
        for register in registers {
            writeln!(text, "    if (index == {register}u) {{ return textureSample(sourceTexture{register}, sourceSampler{register}, uv); }}").unwrap();
        }
        text.push_str("    return vec4<f32>(0.0);\n}\n\nfn wpf_source_register() -> u32 {\n    return u32(round(effect.metadata.x));\n}\n\nfn wpf_sample_source(uv: vec2<f32>) -> vec4<f32> {\n    return wpf_sample_register(wpf_source_register(), uv);\n}\n\nfn wpf_has_active_mask() -> bool {\n");
        text.push_str(if include_mask {
            "    return effect.metadata.w > 0.5;\n}\n"
        } else {
            "    return false;\n}\n"
        });
        text.push_str(if include_mask {
            shaders::WPF_EFFECT_MASK_HELPER
        } else {
            shaders::WPF_EFFECT_NO_MASK_HELPER
        });
        text.push('\n');
        text.push_str(&self.source);
        text.push('\n');
        text.push_str(if pipeline_alpha != ImageAlpha::Premultiplied {
            shaders::WPF_EFFECT_STRAIGHT_WRAPPER
        } else if source_alpha == ImageAlpha::Premultiplied {
            shaders::WPF_EFFECT_PREMULTIPLIED_WRAPPER
        } else {
            shaders::WPF_EFFECT_STRAIGHT_TO_PREMULTIPLIED_WRAPPER
        });
        Ok(text)
    }
}

/// Consumed prefix of the original vector vertex; unused primitive attributes
/// are omitted from this dedicated effect buffer without changing shader inputs.
#[repr(C)]
#[derive(Clone, Copy, Debug, Pod, Zeroable)]
pub struct ManagedShaderVertex {
    /// Original transformed logical position.
    pub position: [f32; 2],
    /// Original white RGB and active output opacity.
    pub color: [f32; 4],
    /// Original full-texture or source-capture subset UV.
    pub uv: [f32; 2],
}
/// Original effect output edges/UVs, transformed before optional axis clipping.
/// The effect is evaluated at the actual final raster samples of this quad.
#[derive(Clone, Copy, Debug)]
pub struct ManagedShaderQuad {
    /// Original local x, y, width, height, also copied into effect uniforms.
    pub bounds: [f32; 4],
    /// Original left, top, right, bottom texture coordinates.
    pub uv: [f32; 4],
    /// Independent source output edges; None derives them from bounds.
    pub output_edges: Option<[f32; 4]>,
    /// Original local-to-target logical transform.
    pub transform: Affine,
    /// Active output opacity, separate from captured source-root opacity.
    pub opacity: f32,
    /// Original active logical axis clip, x/y/width/height. Non-axis quads
    /// remain unchanged and require their actual raster scissor/mask as well.
    pub clip: Option<[f32; 4]>,
}
impl ManagedShaderQuad {
    /// Check complete quad metadata without changing renderer or device state.
    pub fn validate(&self) -> Result<(), Error> {
        self.vertices().map(|_| ())
    }
    /// Full texture output with identity transform and unit opacity.
    pub fn new(bounds: [f32; 4]) -> Self {
        Self {
            bounds,
            uv: [0.0, 0.0, 1.0, 1.0],
            output_edges: None,
            transform: Affine::IDENTITY,
            opacity: 1.0,
            clip: None,
        }
    }
    /// Original four-vertex expansion and bounded axis-quad clipping. None means
    /// fully clipped; a non-axis-aligned quad retains original positions and UVs.
    pub fn vertices(&self) -> Result<Option<[ManagedShaderVertex; 4]>, Error> {
        let [x, y, w, h] = self.bounds;
        let edges = self.output_edges.unwrap_or([x, y, x + w, y + h]);
        if self
            .bounds
            .iter()
            .chain(&self.uv)
            .chain(&edges)
            .chain(&self.transform.0)
            .any(|v| !v.is_finite())
            || !self.opacity.is_finite()
            || !(0.0..=1.0).contains(&self.opacity)
            || w < 0.0
            || h < 0.0
        {
            return Err(Error::InvalidInput);
        }
        let [l, t, r, b] = edges;
        let [ul, ut, ur, ub] = self.uv;
        let positions = [[l, t], [r, t], [r, b], [l, b]].map(|p| self.transform.transform_point(p));
        if positions.iter().flatten().any(|v| !v.is_finite()) {
            return Err(Error::InvalidInput);
        }
        let uv = [[ul, ut], [ur, ut], [ur, ub], [ul, ub]];
        let mut vertices = std::array::from_fn(|i| ManagedShaderVertex {
            position: positions[i],
            color: [1.0, 1.0, 1.0, self.opacity],
            uv: uv[i],
        });
        if let Some([cx, cy, cw, ch]) = self.clip {
            if [cx, cy, cw, ch, cx + cw, cy + ch]
                .iter()
                .any(|v| !v.is_finite())
                || cw < 0.0
                || ch < 0.0
            {
                return Err(Error::InvalidInput);
            }
            let [v0, v1, v2, v3] = positions;
            const EPSILON: f32 = 0.0001;
            if (v0[1] - v1[1]).abs() <= EPSILON
                && (v2[1] - v3[1]).abs() <= EPSILON
                && (v0[0] - v3[0]).abs() <= EPSILON
                && (v1[0] - v2[0]).abs() <= EPSILON
            {
                let left = v0[0].min(v1[0]).min(v2[0].min(v3[0])).max(cx);
                let right = v0[0].max(v1[0]).max(v2[0].max(v3[0])).min(cx + cw);
                let top = v0[1].min(v1[1]).min(v2[1].min(v3[1])).max(cy);
                let bottom = v0[1].max(v1[1]).max(v2[1].max(v3[1])).min(cy + ch);
                let dx = v1[0] - v0[0];
                let dy = v3[1] - v0[1];
                if right <= left || bottom <= top || dx.abs() <= EPSILON || dy.abs() <= EPSILON {
                    return Ok(None);
                }
                let nx0 = v0[0].clamp(left, right);
                let nx1 = v1[0].clamp(left, right);
                let ny0 = v0[1].clamp(top, bottom);
                let ny1 = v3[1].clamp(top, bottom);
                let u0 = (nx0 - v0[0]) / dx;
                let u1 = (nx1 - v0[0]) / dx;
                let t0 = (ny0 - v0[1]) / dy;
                let t1 = (ny1 - v0[1]) / dy;
                let points = [[nx0, ny0], [nx1, ny0], [nx1, ny1], [nx0, ny1]];
                let factors = [[u0, t0], [u1, t0], [u1, t1], [u0, t1]];
                for i in 0..4 {
                    vertices[i].position = points[i];
                    vertices[i].uv = std::array::from_fn(|axis| {
                        // Original Vector2.Lerp uses (1-t)*value1 + t*value2.
                        let top = uv[0][axis] * (1.0 - factors[i][0]) + uv[1][axis] * factors[i][0];
                        let bottom =
                            uv[3][axis] * (1.0 - factors[i][0]) + uv[2][axis] * factors[i][0];
                        top * (1.0 - factors[i][1]) + bottom * factors[i][1]
                    });
                }
            }
        }
        Ok(Some(vertices))
    }
}

#[cfg(feature = "gpu")]
fn sampling(mode: ImageSampling) -> Result<(), Error> {
    // The original managed enum has ten modes; native-only Fant is independent.
    if mode == ImageSampling::Fant {
        Err(Error::InvalidInput)
    } else {
        Ok(())
    }
}
/// One immutable original sampler descriptor. Null is an implicit-input slot;
/// duplicate registers preserve first nonnull descriptor selection.
#[cfg(feature = "gpu")]
#[derive(Clone, Debug)]
pub struct ManagedShaderSampler {
    register: u32,
    image: Option<GpuImage>,
    sampling: ImageSampling,
}
#[cfg(feature = "gpu")]
impl ManagedShaderSampler {
    /// Retain the supplied exact resource handle and original filtering policy.
    /// A borrowed external texture remains externally mutable; this does not
    /// claim source snapshot ownership for future owned-source factories.
    pub fn new(register: u32, image: Option<GpuImage>, mode: ImageSampling) -> Result<Self, Error> {
        if register >= 16 {
            return Err(Error::InvalidInput);
        }
        sampling(mode)?;
        Ok(Self {
            register,
            image,
            sampling: mode,
        })
    }
    /// Original sampler register.
    pub fn register(&self) -> u32 {
        self.register
    }
    /// Exact retained source resource, if present.
    pub fn image(&self) -> Option<&GpuImage> {
        self.image.as_ref()
    }
    /// Original filtering; cubic retains the managed sampler's linear behavior.
    pub fn sampling(&self) -> ImageSampling {
        self.sampling
    }
}
/// Construction-time original parameters. Moving this descriptor into
/// `ManagedShaderParameters` freezes metadata and retains all source handles.
#[cfg(feature = "gpu")]
#[derive(Clone, Debug)]
pub struct ManagedShaderDescriptor {
    /// Exact WGSL source/key generation.
    pub program: Arc<ManagedShaderProgram>,
    /// Original floats; uniforms copy at most 128, while identity retains all.
    pub constants: Arc<[f32]>,
    /// Original ordered sampler inventory, including null and duplicate slots.
    pub samplers: Arc<[ManagedShaderSampler]>,
    /// Original implicit input. None is allowed when explicit samplers exist.
    pub input: Option<GpuImage>,
    /// Original source register in 0..16.
    pub source_register: u32,
    /// Original implicit-input filter.
    pub sampling: ImageSampling,
    /// Explicit source-capture override; ordinary direct parameters keep false.
    pub source_overrides_sampler: bool,
}
#[cfg(feature = "gpu")]
impl ManagedShaderDescriptor {
    /// Original default parameter snapshot with zero registers and linear input.
    pub fn new(program: impl Into<Arc<ManagedShaderProgram>>) -> Self {
        Self {
            program: program.into(),
            constants: Arc::from([]),
            samplers: Arc::from([]),
            input: None,
            source_register: 0,
            sampling: ImageSampling::Linear,
            source_overrides_sampler: false,
        }
    }
}
/// Exact immutable managed parameter generation. Sampler selection and primary
/// alpha/dimension selection follow the original C# order independently.
#[cfg(feature = "gpu")]
#[derive(Clone, Debug)]
pub struct ManagedShaderParameters(Arc<ManagedShaderDescriptor>);
#[cfg(feature = "gpu")]
impl ManagedShaderParameters {
    /// Original whole-parameter admission; constants retain original raw bits.
    pub fn validate(&self) -> Result<(), Error> {
        if self.0.source_register >= 16 {
            return Err(Error::InvalidInput);
        }
        sampling(self.0.sampling)
    }
    /// Sources actually selected by original register precedence, in register order.
    pub fn sources(&self) -> impl Iterator<Item = &GpuImage> {
        (0..16).filter_map(|r| self.sampler(r).map(|s| s.0))
    }
    /// Freeze a complete parameter descriptor without shader compilation or GPU work.
    pub fn new(descriptor: ManagedShaderDescriptor) -> Result<Self, Error> {
        if descriptor.source_register >= 16 {
            return Err(Error::InvalidInput);
        }
        sampling(descriptor.sampling)?;
        Ok(Self(Arc::new(descriptor)))
    }
    /// Original complete descriptor, exposed read-only.
    pub fn descriptor(&self) -> &ManagedShaderDescriptor {
        &self.0
    }
    /// Clone the original parameter snapshot for an actual implicit-input capture.
    /// Source override follows Visual.UpdateDrawParameters; geometry/UVs are
    /// provided independently by this target's fresh ManagedShaderQuad.
    pub fn with_captured_input(&self, input: GpuImage) -> Self {
        let mut descriptor = (*self.0).clone();
        descriptor.input = Some(input);
        descriptor.source_overrides_sampler = true;
        Self(Arc::new(descriptor))
    }
    /// Original source override, then first matching explicit sampler, then
    /// source-register fallback and finally register-zero input alias.
    pub fn sampler(&self, register: u32) -> Option<(&GpuImage, ImageSampling)> {
        if register >= 16 {
            return None;
        }
        let p = &self.0;
        if register == p.source_register
            && p.source_overrides_sampler
            && let Some(input) = &p.input
        {
            return Some((input, p.sampling));
        }
        for sampler in p.samplers.iter() {
            if sampler.register == register
                && let Some(image) = &sampler.image
            {
                return Some((image, sampler.sampling));
            }
        }
        if (register == p.source_register || register == 0)
            && let Some(input) = &p.input
        {
            return Some((input, p.sampling));
        }
        None
    }
    /// Original primary texture determines uniform size and shader alpha wrapper.
    pub fn primary(&self) -> Option<&GpuImage> {
        self.sampler(self.0.source_register)
            .map(|s| s.0)
            .or_else(|| {
                if self.0.source_register != 0 {
                    self.sampler(0).map(|s| s.0)
                } else {
                    None
                }
            })
            .or_else(|| self.0.samplers.iter().find_map(|s| s.image.as_ref()))
            .or(self.0.input.as_ref())
    }
    /// Exact ascending original active-register set, at most sixteen entries.
    pub fn active_registers(&self) -> Vec<u32> {
        (0..16).filter(|r| self.sampler(*r).is_some()).collect()
    }
    /// Original 140-float / 560-byte block. Constants are copied without
    /// sanitizing their bits; unused components stay zero. Canvas/mask metadata
    /// is populated from the actual target in GPU preparation.
    pub fn uniform_floats(&self, bounds: [f32; 4], texture_size: [u32; 2]) -> [f32; 140] {
        let mut values = [0.0; 140];
        let count = self.0.constants.len().min(128);
        values[..count].copy_from_slice(&self.0.constants[..count]);
        values[128..132].copy_from_slice(&bounds);
        let [w, h] = texture_size;
        values[132..136].copy_from_slice(&[
            w as f32,
            h as f32,
            if w > 0 { 1.0 / w as f32 } else { 0.0 },
            if h > 0 { 1.0 / h as f32 } else { 0.0 },
        ]);
        values[136] = self.0.source_register as f32;
        values
    }
}
