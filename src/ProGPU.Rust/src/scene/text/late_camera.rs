//! Original ordinary UseGpuTransforms text compilation and final flag rewrite.
//! StaticCompilationContext stays absent: shared styles are chosen before the
//! original post-command ForceTextUseMvp pass, not replaced by legacy colors.
use super::*;

pub(in crate::scene) fn context(
    mut context: TextPaintContext,
    camera: Option<Matrix4>,
    dpi: f32,
) -> Result<TextPaintContext, Error> {
    context.dpi_scale = dpi;
    validate_context(context)?;
    if let Some(camera) = camera {
        if camera.0.iter().flatten().any(|value| !value.is_finite()) {
            return Err(Error::InvalidInput);
        }
        context.gpu_transforms_active = true;
        context.camera_view = camera;
        // Ordinary late camera is independent of ActiveCompilationContext.
        // Keeping use_mvp false retains original shared style registration.
    }
    Ok(context)
}

pub(in crate::scene) fn inspect_context(
    mut context: TextPaintContext,
    camera: Option<Matrix4>,
) -> TextPaintContext {
    if let Some(camera) = camera {
        context.gpu_transforms_active = true;
        context.camera_view = camera;
    }
    context
}

/// Original scalar decode/re-encode order, including independent color and
/// explicit grayscale contrast flags. It is applied after instance generation.
pub(in crate::scene) fn force_use_mvp(encoded: f32) -> f32 {
    let transfer = 16.0 * (encoded.max(0.0) / 16.0).floor();
    let stripped = encoded - transfer;
    let color = stripped > 5.5;
    let base = if color { stripped - 8.0 } else { stripped };
    let mode = if base < -0.5 {
        -2.0
    } else if base > 1.5 {
        3.0
    } else {
        1.0
    };
    (if color { mode + 8.0 } else { mode }) + transfer
}

pub(in crate::scene) fn wrap(command: Command, camera: Option<Matrix4>) -> Command {
    if let Some(camera) = camera {
        Command::LateCamera(Box::new(command), camera)
    } else {
        command
    }
}

impl Scene {
    /// Retain ordinary original text with a distinct model frame and late camera.
    /// The final marked scene command selects the shared GPU camera; each text
    /// command's own camera still supplies its coverage-scale policy. Prepared
    /// hinted geometry and static DXF retain their independent source admission.
    pub fn draw_text_late_camera(
        &mut self,
        draw: TextDraw,
        context: TextPaintContext,
        camera: Matrix4,
    ) -> Result<(), Error> {
        validate_context(context)?;
        let command = Command::Text(Box::new((draw, context)));
        super::super::late_camera::validate(&command, camera)?;
        self.commands.try_reserve(1).map_err(|_| Error::Capacity)?;
        self.commands.push(wrap(command, Some(camera)));
        self.tree_text = true;
        Ok(())
    }
}
