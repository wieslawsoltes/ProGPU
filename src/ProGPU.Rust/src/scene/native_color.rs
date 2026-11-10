//! Typed already-prepared native color-layer path into canonical text coverage.
use super::*;
impl Scene {
    /// Record a complete selected-font layer generation without executing font
    /// parsers or raster decoders during scene compilation/GPU encoding.
    pub fn draw_native_color_path(
        &mut self,
        layer: crate::text::native_color::NativeColorVectorPath,
    ) -> Result<(), Error> {
        let policy = layer.policy;
        if !policy.coverage_gamma.is_finite()
            || !policy.raster_scale.is_finite()
            || policy.raster_scale <= 0.0
            || !layer.semantic_opacity.is_finite()
            || policy.aliased && policy.coverage_transfer.encoding() != 0
        {
            return Err(Error::InvalidInput);
        }
        let m = layer.transform.0;
        if m.iter().flatten().any(|v| !v.is_finite())
            || m[0][2] != 0.0
            || m[0][3] != 0.0
            || m[1][2] != 0.0
            || m[1][3] != 0.0
            || m[2] != [0.0, 0.0, 1.0, 0.0]
            || m[3][2] != 0.0
            || m[3][3] != 1.0
        {
            return Err(Error::UnsupportedExecution);
        }
        let sampling = PathSampling::new(if policy.aliased {
            1
        } else if policy.sample_grid >= 8 {
            8
        } else if policy.sample_grid == 1 {
            1
        } else {
            4
        })?;
        let mut path = layer.path;
        path.retain_source_owner(layer.source);
        let draw = PathDraw {
            path,
            transform: Affine([m[0][0], m[0][1], m[1][0], m[1][1], m[3][0], m[3][1]]),
            color: match layer.brush.kind {
                BrushKind::Solid(color) => color,
                _ => [1.0; 4],
            },
            sampling,
        };
        validate_path_draw(&draw)?;
        self.commands.try_reserve(1).map_err(|_| Error::Capacity)?;
        self.commands.push(Command::TextPath(
            draw,
            MaterialPaint {
                brush: layer.brush,
                semantic_opacity: layer.semantic_opacity,
            },
            policy,
        ));
        Ok(())
    }
}
