//! Original raw allocation overloads exposed as safe borrowed typed slices.
use super::*;
impl TextBlobBuilder {
    /// Safe raw nominal run with an empty position span.
    pub fn allocate_raw_run(
        &mut self,
        font: &CanvasFont,
        count: usize,
        origin: [f32; 2],
        bounds: Option<[f32; 4]>,
    ) -> Result<TextBlobRunBuffer<'_, f32>> {
        self.allocate_run(font, count, origin, bounds)
    }
    /// Safe raw nominal text run, retaining full text and cluster spans.
    pub fn allocate_raw_text_run(
        &mut self,
        font: &CanvasFont,
        count: usize,
        origin: [f32; 2],
        text_bytes: usize,
        bounds: Option<[f32; 4]>,
    ) -> Result<TextBlobRunBuffer<'_, f32>> {
        self.allocate_text_run(font, count, origin, text_bytes, bounds)
    }
    /// Safe raw horizontal run with original prefix-copy behavior.
    pub fn allocate_raw_horizontal_run(
        &mut self,
        font: &CanvasFont,
        count: usize,
        y: f32,
        bounds: Option<[f32; 4]>,
    ) -> Result<TextBlobRunBuffer<'_, f32>> {
        self.allocate_horizontal_run(font, count, y, bounds)
    }
    /// Safe raw horizontal text run.
    pub fn allocate_raw_horizontal_text_run(
        &mut self,
        font: &CanvasFont,
        count: usize,
        y: f32,
        text_bytes: usize,
        bounds: Option<[f32; 4]>,
    ) -> Result<TextBlobRunBuffer<'_, f32>> {
        self.allocate_horizontal_text_run(font, count, y, text_bytes, bounds)
    }
    /// Safe raw positioned text run, retaining the original copied Build policy.
    pub fn allocate_raw_positioned_text_run(
        &mut self,
        font: &CanvasFont,
        count: usize,
        text_bytes: usize,
        bounds: Option<[f32; 4]>,
    ) -> Result<TextBlobRunBuffer<'_, [f32; 2]>> {
        self.allocate_positioned_text_run(font, count, text_bytes, bounds)
    }
    /// Safe raw rotation/scale run with explicit zero default frames.
    pub fn allocate_raw_rotation_scale_run(
        &mut self,
        font: &CanvasFont,
        count: usize,
        bounds: Option<[f32; 4]>,
    ) -> Result<TextBlobRunBuffer<'_, RotationScaleMatrix>> {
        self.allocate_rotation_scale_run(font, count, bounds)
    }
    /// Safe raw rotation/scale text run.
    pub fn allocate_raw_rotation_scale_text_run(
        &mut self,
        font: &CanvasFont,
        count: usize,
        text_bytes: usize,
        bounds: Option<[f32; 4]>,
    ) -> Result<TextBlobRunBuffer<'_, RotationScaleMatrix>> {
        self.allocate_rotation_scale_text_run(font, count, text_bytes, bounds)
    }
}
