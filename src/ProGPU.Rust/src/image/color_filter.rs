//! Original retained color filters, native image-matrix transport and GPU lowering.
//! Constructors retain immutable data; no factory initializes WebGPU.
use crate::Error;
use bytemuck::{Pod, Zeroable};
use std::sync::Arc;
mod cpu;
mod graph;
mod plan;
pub use graph::{
    ColorFilter, ColorFilterKind, ColorTableLayout, ColorTables, HighContrast, InvertStyle,
};
pub use plan::{ColorFilterOperation, ColorFilterPlan, ColorFilterStep};
#[cfg(feature = "gpu")]
mod gpu;
#[cfg(feature = "gpu")]
pub use gpu::{ColorFilterComputeEngine, ColorFilterComputeMetrics, PreparedColorFilterCompute};

/// Original semantic image matrix. Texture rendering applies its matrix first,
/// then optional luminance-to-alpha, then clamps; source alpha decoding precedes it.
#[derive(Clone, Copy, Debug, PartialEq)]
pub struct ImageColorMatrix {
    /// Original straight-RGBA red row.
    pub red: [f32; 4],
    /// Original green row.
    pub green: [f32; 4],
    /// Original blue row.
    pub blue: [f32; 4],
    /// Original alpha row.
    pub alpha: [f32; 4],
    /// Original normalized RGBA offsets.
    pub offset: [f32; 4],
    /// Explicit luminance-to-alpha after the original matrix.
    pub luminance_to_alpha: bool,
}
impl Default for ImageColorMatrix {
    fn default() -> Self {
        Self {
            red: [1.0, 0.0, 0.0, 0.0],
            green: [0.0, 1.0, 0.0, 0.0],
            blue: [0.0, 0.0, 1.0, 0.0],
            alpha: [0.0, 0.0, 0.0, 1.0],
            offset: [0.0; 4],
            luminance_to_alpha: false,
        }
    }
}
impl ImageColorMatrix {
    /// Original managed 4-by-5 row-major matrix, with already-normalized offsets.
    /// Managed finite admission remains independent of the native magnitude bound.
    pub fn from_managed(values: [f32; 20]) -> Result<Self, Error> {
        if values.iter().any(|v| !v.is_finite()) {
            return Err(Error::InvalidInput);
        }
        Ok(Self {
            red: [values[0], values[1], values[2], values[3]],
            green: [values[5], values[6], values[7], values[8]],
            blue: [values[10], values[11], values[12], values[13]],
            alpha: [values[15], values[16], values[17], values[18]],
            offset: [values[4], values[9], values[14], values[19]],
            luminance_to_alpha: false,
        })
    }
    /// Exact original source matrix layout without scaling, flattening or fusion.
    pub fn managed_values(self) -> [f32; 20] {
        let rows = [self.red, self.green, self.blue, self.alpha];
        std::array::from_fn(|i| {
            if i % 5 == 4 {
                self.offset[i / 5]
            } else {
                rows[i / 5][i % 5]
            }
        })
    }
    /// Native semantic transport admits only finite components with magnitude <=1024.
    pub fn validate_native(self) -> Result<(), Error> {
        if self
            .managed_values()
            .iter()
            .any(|v| !v.is_finite() || v.abs() > 1024.0)
        {
            Err(Error::InvalidInput)
        } else {
            Ok(())
        }
    }
    /// Native 96-byte shader data; this is distinct from the 96-byte wire packet.
    pub fn uniforms(self) -> ImageColorUniforms {
        ImageColorUniforms {
            red: self.red,
            green: self.green,
            blue: self.blue,
            alpha: self.alpha,
            offset: self.offset,
            options: [
                if self.luminance_to_alpha { 1.0 } else { 0.0 },
                0.0,
                0.0,
                0.0,
            ],
        }
    }
    /// Preserve complete original packet admission before exposing draw metadata.
    pub fn from_native(packet: NativeImageColorMatrix) -> Result<Self, Error> {
        if packet.struct_size != 96 || packet.flags & !1 != 0 || packet.reserved != [0; 2] {
            return Err(Error::InvalidInput);
        }
        let matrix = Self {
            red: packet.red,
            green: packet.green,
            blue: packet.blue,
            alpha: packet.alpha,
            offset: packet.offset,
            luminance_to_alpha: packet.flags & 1 != 0,
        };
        matrix.validate_native()?;
        Ok(matrix)
    }
    /// Create the original typed packet with exact component bits and zero reserved words.
    pub fn to_native(self) -> Result<NativeImageColorMatrix, Error> {
        self.validate_native()?;
        Ok(NativeImageColorMatrix {
            struct_size: 96,
            flags: u32::from(self.luminance_to_alpha),
            red: self.red,
            green: self.green,
            blue: self.blue,
            alpha: self.alpha,
            offset: self.offset,
            reserved: [0; 2],
        })
    }
}
/// Exact original 96-byte native semantic packet; header/footer are not GPU rows.
#[repr(C)]
#[derive(Clone, Copy, Debug, Pod, Zeroable)]
pub struct NativeImageColorMatrix {
    /// Original packet byte size, exactly 96.
    pub struct_size: u32,
    /// Bit zero selects luminance-to-alpha; other bits reject.
    pub flags: u32,
    /// Original red row.
    pub red: [f32; 4],
    /// Original green row.
    pub green: [f32; 4],
    /// Original blue row.
    pub blue: [f32; 4],
    /// Original alpha row.
    pub alpha: [f32; 4],
    /// Original normalized offsets.
    pub offset: [f32; 4],
    /// Original reserved zeros.
    pub reserved: [u32; 2],
}
/// Exact Texture.wgsl 96-byte matrix record, using the original mask-layout slots.
#[repr(C)]
#[derive(Clone, Copy, Debug, PartialEq, Pod, Zeroable)]
pub struct ImageColorUniforms {
    /// Original coordinate0/red row.
    pub red: [f32; 4],
    /// Original coordinate1/green row.
    pub green: [f32; 4],
    /// Original bounds/blue row.
    pub blue: [f32; 4],
    /// Original cornerRadiiX/alpha row.
    pub alpha: [f32; 4],
    /// Original cornerRadiiY/offset.
    pub offset: [f32; 4],
    /// Luminance flag and three original zeros.
    pub options: [f32; 4],
}
/// Exact original HSLA/high-contrast 96-byte compute record.
#[repr(C)]
#[derive(Clone, Copy, Debug, PartialEq, Pod, Zeroable)]
pub struct NonlinearColorUniforms {
    /// Original transformed hue/red row.
    pub red: [f32; 4],
    /// Original saturation/green row.
    pub green: [f32; 4],
    /// Original lightness/blue row.
    pub blue: [f32; 4],
    /// Original alpha row.
    pub alpha: [f32; 4],
    /// Original offsets.
    pub offset: [f32; 4],
    /// Kind (0 HSLA, 1 high contrast), grayscale, invert style and contrast scale.
    pub configuration: [f32; 4],
}
impl NonlinearColorUniforms {
    /// Retain the original exact matrix and HSLA configuration.
    pub fn hsla(matrix: ImageColorMatrix) -> Self {
        Self {
            red: matrix.red,
            green: matrix.green,
            blue: matrix.blue,
            alpha: matrix.alpha,
            offset: matrix.offset,
            configuration: [0.0, 0.0, 0.0, 1.0],
        }
    }
    /// Original managed parameter builder, independently of stricter public filter admission.
    pub fn high_contrast(grayscale: bool, invert_style: u32, contrast: f32) -> Self {
        let contrast = if contrast.is_finite() { contrast } else { 0.0 };
        let contrast = contrast.clamp(-1.0 + f32::EPSILON, 1.0 - f32::EPSILON);
        Self {
            configuration: [
                1.0,
                if grayscale { 1.0 } else { 0.0 },
                invert_style.min(2) as f32,
                (1.0 + contrast) / (1.0 - contrast),
            ],
            ..Zeroable::zeroed()
        }
    }
}
/// Original six straight RGBA colors occupying 96 bytes.
#[repr(C)]
#[derive(Clone, Copy, Debug, PartialEq, Pod, Zeroable)]
pub struct OverdrawColorUniforms {
    /// Counts 1..5 select entries 0..4; count >=6 selects entry 5; zero is transparent.
    pub colors: [[f32; 4]; 6],
}
const _: () = {
    assert!(size_of::<NativeImageColorMatrix>() == 96);
    assert!(size_of::<ImageColorUniforms>() == 96);
    assert!(size_of::<NonlinearColorUniforms>() == 96);
    assert!(size_of::<OverdrawColorUniforms>() == 96);
};
