use super::*;
impl Canvas {
    /// Original independent conservative device clip query.
    pub fn device_clip_bounds(&self) -> Option<[i32; 4]> {
        (!self.clip_state.empty()).then_some(self.clip_state.bounds)
    }
    /// Original local query outsets the device bounds by one before inversion.
    pub fn local_clip_bounds(&self) -> Option<[f32; 4]> {
        let b = self.device_clip_bounds()?.map(|v| v as f32);
        let bounds =
            self.matrix
                .invert_full()?
                .map_rect([b[0] - 1.0, b[1] - 1.0, b[2] + 1.0, b[3] + 1.0]);
        valid(bounds).then_some(bounds)
    }
    /// Original empty-clip state; this is not a pixel readback.
    pub fn is_clip_empty(&self) -> bool {
        self.clip_state.empty()
    }
    /// Original proven rectangular clip state, independent of its envelope.
    pub fn is_clip_rect(&self) -> bool {
        !self.clip_state.empty() && self.clip_state.rect
    }
    /// Original conservative rectangle rejection in actual device coordinates.
    pub fn quick_reject(&self, bounds: [f32; 4]) -> bool {
        if bounds[2] <= bounds[0] || bounds[3] <= bounds[1] || self.clip_state.empty() {
            return true;
        }
        let b = self.matrix.map_rect(bounds);
        let c = self.clip_state.bounds.map(|v| v as f32);
        b[2] <= c[0] || b[3] <= c[1] || b[0] >= c[2] || b[1] >= c[3]
    }
    /// Original clip rectangles keep their source transform and use binary pixel
    /// centers; the compatibility antialias argument is deliberately ignored.
    pub fn clip_rect(
        &mut self,
        bounds: [f32; 4],
        operation: CanvasClipOperation,
        _antialias: bool,
    ) -> Result<(), Error> {
        let rect = axis(self.matrix);
        let mapped = self.matrix.map_rect(bounds);
        let clip = Clip {
            geometry: Arc::new(PathGeometry::rectangle(
                bounds[0],
                bounds[1],
                bounds[2] - bounds[0],
                bounds[3] - bounds[1],
            )),
            matrix: self.matrix,
            operation,
            rect: (operation == CanvasClipOperation::Intersect).then_some(bounds),
        };
        self.add_clip(clip, mapped, rect)
    }
    /// Original inverse-fill toggles the clip operation without changing source
    /// geometry; generic path coverage keeps the source's eight-sample contract.
    pub fn clip_path(
        &mut self,
        geometry: Arc<PathGeometry>,
        inverse: bool,
        operation: CanvasClipOperation,
        _antialias: bool,
    ) -> Result<(), Error> {
        let operation = if inverse {
            match operation {
                CanvasClipOperation::Intersect => CanvasClipOperation::Difference,
                CanvasClipOperation::Difference => CanvasClipOperation::Intersect,
            }
        } else {
            operation
        };
        let rectangle = if axis(self.matrix) {
            geometry.axis_aligned_rectangle_bounds()
        } else {
            None
        };
        let bounds = if let Some(bounds) = rectangle {
            bounds
        } else {
            geometry.tight_bounds()?
        };
        let mapped = if valid(bounds) {
            self.matrix.map_rect(bounds)
        } else {
            [0.0; 4]
        };
        let clip = Clip {
            geometry,
            matrix: self.matrix,
            operation,
            rect: if operation == CanvasClipOperation::Intersect {
                rectangle
            } else {
                None
            },
        };
        self.add_clip(clip, mapped, rectangle.is_some())
    }
    /// Original rounded clip is the actual four-corner geometry.
    pub fn clip_round_rect(
        &mut self,
        rect: RoundRect,
        operation: CanvasClipOperation,
        antialias: bool,
    ) -> Result<(), Error> {
        self.clip_path(
            Arc::new(rect.to_geometry(crate::vector::SweepDirection::Clockwise, 0)?),
            false,
            operation,
            antialias,
        )
    }
    /// Exact integer region paths remain source geometry. Only a first intersect
    /// selects original CPU readback regions; region scan is never used as paint.
    pub fn clip_region(
        &mut self,
        region: &Region,
        operation: CanvasClipOperation,
    ) -> Result<(), Error> {
        let regions = if operation == CanvasClipOperation::Intersect
            && self.commands.is_empty()
            && self.cpu_readback_regions.is_none()
        {
            Some(
                region
                    .rectangles()?
                    .map(|r| self.matrix.map_rect(r.0.map(|v| v as f32)))
                    .collect::<Vec<_>>(),
            )
        } else {
            None
        };
        self.clip_path(Arc::new(region.boundary_path()?), false, operation, false)?;
        if let Some(regions) = regions {
            self.cpu_readback_regions = Some(regions);
        }
        Ok(())
    }
    /// Explicit original initial clip metadata, admitted only before any save or
    /// push; it cannot invent actual clip drawing commands for caller storage.
    pub fn initialize_device_clip_bounds(
        &mut self,
        bounds: [i32; 4],
        is_rect: bool,
    ) -> Result<(), Error> {
        if !self.saved.is_empty() || !self.clips.is_empty() {
            return Err(Error::InvalidInput);
        }
        let c = [0, 0, self.size[0].ceil() as i32, self.size[1].ceil() as i32];
        let b = [
            c[0].max(bounds[0]),
            c[1].max(bounds[1]),
            c[2].min(bounds[2]),
            c[3].min(bounds[3]),
        ];
        self.clip_state = state(b, is_rect);
        Ok(())
    }
    fn add_clip(&mut self, clip: Clip, mapped: [f32; 4], is_rect: bool) -> Result<(), Error> {
        self.clips.try_reserve(1).map_err(|_| Error::Capacity)?;
        let index = self.commands.len();
        self.push(Command::Clip(clip.clone()))?;
        self.clip_state = update(self.clip_state, mapped, is_rect, clip.operation);
        self.clips.push(ActiveClip { clip, index });
        Ok(())
    }
}
fn state(bounds: [i32; 4], rect: bool) -> ClipState {
    if bounds[2] > bounds[0] && bounds[3] > bounds[1] {
        ClipState { bounds, rect }
    } else {
        ClipState {
            bounds: [0; 4],
            rect: false,
        }
    }
}
fn axis(matrix: CanvasMatrix) -> bool {
    matrix.0[3].abs() <= 0.0001 && matrix.0[1].abs() <= 0.0001
}
fn device(bounds: [f32; 4], nearest: bool) -> [i32; 4] {
    if !valid(bounds) {
        return [0; 4];
    }
    std::array::from_fn(|i| {
        if nearest {
            bounds[i].round() as i32
        } else if i < 2 {
            bounds[i].floor() as i32
        } else {
            bounds[i].ceil() as i32
        }
    })
}
fn update(
    current: ClipState,
    bounds: [f32; 4],
    rect: bool,
    operation: CanvasClipOperation,
) -> ClipState {
    if current.empty() {
        return current;
    }
    let e = device(bounds, rect);
    let c = current.bounds;
    if operation == CanvasClipOperation::Intersect {
        return state(
            [
                c[0].max(e[0]),
                c[1].max(e[1]),
                c[2].min(e[2]),
                c[3].min(e[3]),
            ],
            current.rect && rect,
        );
    }
    if e[2] <= c[0] || e[3] <= c[1] || e[0] >= c[2] || e[1] >= c[3] {
        return current;
    }
    if !current.rect || !rect {
        return ClipState {
            bounds: c,
            rect: false,
        };
    }
    if e[0] <= c[0] && e[2] >= c[2] && e[1] <= c[1] && e[3] >= c[3] {
        return state([0; 4], false);
    }
    if e[1] <= c[1] && e[3] >= c[3] {
        if e[0] <= c[0] {
            return state([e[2], c[1], c[2], c[3]], true);
        }
        if e[2] >= c[2] {
            return state([c[0], c[1], e[0], c[3]], true);
        }
    }
    if e[0] <= c[0] && e[2] >= c[2] {
        if e[1] <= c[1] {
            return state([c[0], e[3], c[2], c[3]], true);
        }
        if e[3] >= c[3] {
            return state([c[0], c[1], c[2], e[1]], true);
        }
    }
    ClipState {
        bounds: c,
        rect: false,
    }
}
