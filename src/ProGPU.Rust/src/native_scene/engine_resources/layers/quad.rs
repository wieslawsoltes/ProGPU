//! Original physical source/target extents and independent source DPI arithmetic.
use super::*;
use wide::f32x4 as V;
fn vertices(x: [f32; 4], y: [f32; 4], u: f32, v: f32, opacity: f32) -> [VectorVertex; 4] {
    std::array::from_fn(|i| VectorVertex {
        position: [x[i], y[i]],
        color: [opacity, 1.0, 0.0, opacity],
        texture_coordinate: [[0.0, 0.0], [u, 0.0], [u, v], [0.0, v]][i],
        brush_index: 0.0,
        shape_size: [0.0; 2],
        corner_radius: 0.0,
        stroke_thickness: 1.0,
        shape_type: 0.0,
    })
}
fn admit(size: [u32; 2], dpi: f32, opacity: f32) -> Result<(), Error> {
    if size.contains(&0) || !dpi.is_finite() || dpi <= 0.0 || !opacity.is_finite() {
        Err(Error::InvalidInput)
    } else {
        Ok(())
    }
}
pub fn native_layer_quad(
    source: NativeScissor,
    target: NativeScissor,
    texture_size: [u32; 2],
    dpi: f32,
    opacity: f32,
) -> Result<[VectorVertex; 4], Error> {
    admit(texture_size, dpi, opacity)?;
    // Original scissor coordinates are unsigned. Preserve the source subtraction
    // before conversion, including the original wrapping representation.
    let x0 = source.x.wrapping_sub(target.x) as f32 / dpi;
    let y0 = source.y.wrapping_sub(target.y) as f32 / dpi;
    let x1 = x0 + source.width as f32 / dpi;
    let y1 = y0 + source.height as f32 / dpi;
    Ok(vertices(
        [x0, x1, x1, x0],
        [y0, y0, y1, y1],
        source.width as f32 / texture_size[0] as f32,
        source.height as f32 / texture_size[1] as f32,
        opacity,
    ))
}
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum NativeLayerQuadPath {
    CpuSimd,
    ScalarReference,
}
pub fn native_transformed_layer_quad(
    source: NativeScissor,
    target: NativeScissor,
    texture_size: [u32; 2],
    dpi: f32,
    opacity: f32,
    transform: wire::Affine2d,
    source_dpi: [f32; 2],
) -> Result<[VectorVertex; 4], Error> {
    native_transformed_layer_quad_with_preference(
        source,
        target,
        texture_size,
        dpi,
        opacity,
        transform,
        source_dpi,
        crate::path::ExecutionPreference::Automatic,
    )
    .map(|result| result.0)
}
pub fn native_transformed_layer_quad_with_preference(
    source: NativeScissor,
    target: NativeScissor,
    texture_size: [u32; 2],
    dpi: f32,
    opacity: f32,
    transform: wire::Affine2d,
    source_dpi: [f32; 2],
    preference: crate::path::ExecutionPreference,
) -> Result<([VectorVertex; 4], NativeLayerQuadPath), Error> {
    use crate::path::ExecutionPreference as P;
    let simd = cfg!(any(
        target_arch = "x86_64",
        target_arch = "aarch64",
        all(target_arch = "wasm32", target_feature = "simd128")
    ));
    let path = match preference {
        P::Automatic => {
            if simd {
                NativeLayerQuadPath::CpuSimd
            } else {
                NativeLayerQuadPath::ScalarReference
            }
        }
        P::CpuSimd if simd => NativeLayerQuadPath::CpuSimd,
        P::ScalarReference => NativeLayerQuadPath::ScalarReference,
        _ => return Err(Error::UnsupportedExecution),
    };
    admit(texture_size, dpi, opacity)?;
    if ![
        transform.m11,
        transform.m12,
        transform.m21,
        transform.m22,
        transform.m31,
        transform.m32,
    ]
    .into_iter()
    .all(f32::is_finite)
    {
        return Err(Error::InvalidInput);
    }
    let sx = if source_dpi[0] > 0.0 {
        source_dpi[0]
    } else {
        dpi
    };
    let sy = if source_dpi[1] > 0.0 {
        source_dpi[1]
    } else {
        dpi
    };
    let x0 = source.x as f32 / sx;
    let y0 = source.y as f32 / sy;
    let x1 = x0 + source.width as f32 / sx;
    let y1 = y0 + source.height as f32 / sy;
    if path == NativeLayerQuadPath::ScalarReference {
        let x = [x0, x1, x1, x0];
        let y = [y0, y0, y1, y1];
        let px = std::array::from_fn(|i| {
            x[i] * transform.m11 + y[i] * transform.m21 + transform.m31 - target.x as f32 / dpi
        });
        let py = std::array::from_fn(|i| {
            x[i] * transform.m12 + y[i] * transform.m22 + transform.m32 - target.y as f32 / dpi
        });
        return Ok((
            vertices(
                px,
                py,
                source.width as f32 / texture_size[0] as f32,
                source.height as f32 / texture_size[1] as f32,
                opacity,
            ),
            path,
        ));
    }
    let x = V::new([x0, x1, x1, x0]);
    let y = V::new([y0, y0, y1, y1]);
    let px = ((x * V::splat(transform.m11) + y * V::splat(transform.m21))
        + V::splat(transform.m31))
        - V::splat(target.x as f32 / dpi);
    let py = ((x * V::splat(transform.m12) + y * V::splat(transform.m22))
        + V::splat(transform.m32))
        - V::splat(target.y as f32 / dpi);
    Ok((
        vertices(
            px.to_array(),
            py.to_array(),
            source.width as f32 / texture_size[0] as f32,
            source.height as f32 / texture_size[1] as f32,
            opacity,
        ),
        path,
    ))
}
