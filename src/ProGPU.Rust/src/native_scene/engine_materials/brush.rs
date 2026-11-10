//! Original material variants, source-opacity placement and shared stop ranges.
use super::*;
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct NativeBrushDraw {
    pub first_index: u32,
    pub index_count: u32,
}
#[derive(Clone, Debug)]
pub struct NativeBrushPage {
    scene: Arc<NativeScene>,
    scene_hash: u64,
    brushes: Arc<[wire::Brush]>,
    gradient_stops: Arc<[wire::GradientStop]>,
    remapped_indices: Arc<[u32]>,
    command_draws: Arc<[NativeBrushDraw]>,
}
impl NativeBrushPage {
    pub fn compile(scene: Arc<NativeScene>, scene_hash: u64) -> Result<Self, Error> {
        let mut brushes = allocate(64)?;
        let mut stops = allocate(64)?;
        let mut draws = allocate(scene.envelope().header().command_count as usize)?;
        draws.resize(
            scene.envelope().header().command_count as usize,
            NativeBrushDraw::default(),
        );
        let mut sentinel = wire::Brush {
            kind: SCENE_BRUSH_SOLID,
            opacity: 1.0,
            ..Default::default()
        };
        sentinel.coordinate_transform0[0] = 1.0;
        sentinel.coordinate_transform1[1] = 1.0;
        brushes.push(sentinel);
        stops.push(wire::GradientStop::default());
        let mut remapped = Vec::new();
        let mut variants = variants(64)?;
        let mut stop_ranges = super::variants(32)?;
        let mut cursor = NativeStateCursor::new(scene.clone(), [1.0, 1.0]);
        for (command_index, command) in scene.commands().enumerate() {
            let state = cursor.advance(command.record)?;
            let kind = command.record.kind;
            let painted = kind == SCENE_COMMAND_DRAW_PAINTED_GLYPH_RUN;
            let painted_draw = if painted {
                let draw: wire::PaintedGlyphDraw = read(command.payload, 0)?;
                let paint = wire::GlyphPaint::read(
                    command
                        .payload
                        .get(wire::PaintedGlyphDraw::WIRE_SIZE..)
                        .ok_or(Error::InvalidInput)?,
                )
                .ok_or(Error::InvalidInput)?;
                if paint.kind != SCENE_GLYPH_PAINT_MATERIAL {
                    continue;
                }
                Some(draw)
            } else {
                None
            };
            if (!painted
                && !matches!(
                    kind,
                    SCENE_COMMAND_DRAW_ANALYTIC
                        | SCENE_COMMAND_DRAW_PATH
                        | SCENE_COMMAND_DRAW_SOURCE_PATH
                        | SCENE_COMMAND_DRAW_GEOMETRY
                        | SCENE_COMMAND_DRAW_POINT_BATCH
                        | SCENE_COMMAND_DRAW_VERTEX_MESH
                        | SCENE_COMMAND_DRAW_SOURCE_COVERAGE
                        | SCENE_COMMAND_DRAW_STROKE_BATCH
                ))
                || command.payload.is_empty()
            {
                continue;
            }
            let draw = if let Some(p) = painted_draw {
                wire::DrawBrushes {
                    struct_size: wire::DrawBrushes::WIRE_SIZE as u32,
                    brush_resource_index: p.paint_resource_index,
                    brush_count: 1,
                    reserved: 0,
                }
            } else {
                read(command.payload, 0)?
            };
            let resource = scene
                .envelope()
                .resource(draw.brush_resource_index)
                .ok_or(Error::InvalidInput)?;
            draws[command_index] = NativeBrushDraw {
                first_index: u32::try_from(remapped.len()).map_err(|_| Error::Capacity)?,
                index_count: draw.brush_count,
            };
            remapped
                .try_reserve(draw.brush_count as usize)
                .map_err(|_| Error::Capacity)?;
            for index in 0..draw.brush_count {
                let local = if let Some(p) = painted_draw {
                    p.paint_index
                } else {
                    read::<u32>(
                        command
                            .payload
                            .get(wire::DrawBrushes::WIRE_SIZE..)
                            .ok_or(Error::InvalidInput)?,
                        index as usize,
                    )?
                };
                let opacity = if matches!(
                    kind,
                    SCENE_COMMAND_DRAW_VERTEX_MESH | SCENE_COMMAND_DRAW_SOURCE_COVERAGE
                ) {
                    1.0
                } else {
                    state.opacity
                };
                let key = Key(draw.brush_resource_index, local, opacity.to_bits());
                if let Some(&packed) = variants.get(&key) {
                    remapped.push(packed);
                    continue;
                }
                if brushes.len() > SCENE_MAX_BRUSHES as usize || brushes.len() >= (1 << 24) {
                    return Err(Error::Capacity);
                }
                let mut brush: wire::Brush = read(resource.payload, local as usize)?;
                brush.opacity *= opacity;
                let count = crate::native_scene::semantic::stored_stop_count(&brush);
                if count != 0 {
                    let key = Key(draw.brush_resource_index, brush.stop_offset, count);
                    if let Some(&packed) = stop_ranges.get(&key) {
                        brush.stop_offset = packed;
                    } else {
                        let retained = stops.len() - 1;
                        if count as usize > SCENE_MAX_GRADIENT_STOPS as usize - retained {
                            return Err(Error::Capacity);
                        }
                        let packed = u32::try_from(stops.len()).map_err(|_| Error::Capacity)?;
                        stops
                            .try_reserve(count as usize)
                            .map_err(|_| Error::Capacity)?;
                        for index in 0..count {
                            stops.push(read(
                                resource.auxiliary,
                                (brush.stop_offset + index) as usize,
                            )?);
                        }
                        stop_ranges.try_reserve(1).map_err(|_| Error::Capacity)?;
                        stop_ranges.insert(key, packed);
                        brush.stop_offset = packed;
                    }
                    if brush.stop_offset == 0 {
                        return Err(Error::InvalidInput);
                    }
                }
                let packed = brushes.len() as u32;
                push(&mut brushes, brush)?;
                variants.try_reserve(1).map_err(|_| Error::Capacity)?;
                variants.insert(key, packed);
                remapped.push(packed);
            }
        }
        Ok(Self {
            scene,
            scene_hash,
            brushes: brushes.into(),
            gradient_stops: stops.into(),
            remapped_indices: remapped.into(),
            command_draws: draws.into(),
        })
    }
    pub fn scene(&self) -> &Arc<NativeScene> {
        &self.scene
    }
    pub fn scene_hash(&self) -> u64 {
        self.scene_hash
    }
    pub fn brushes(&self) -> &[wire::Brush] {
        &self.brushes
    }
    pub fn gradient_stops(&self) -> &[wire::GradientStop] {
        &self.gradient_stops
    }
    pub fn remapped_indices(&self) -> &[u32] {
        &self.remapped_indices
    }
    pub fn command_draws(&self) -> &[NativeBrushDraw] {
        &self.command_draws
    }
    pub fn packed_brush(&self, index: u32) -> Option<&wire::Brush> {
        self.brushes.get(index as usize)
    }
    pub fn draw_brush_index(&self, command: u32, record: u32) -> Option<u32> {
        let draw = self.command_draws.get(command as usize)?;
        if draw.index_count == 0 {
            return Some(0);
        }
        if record >= draw.index_count
            || draw.first_index as usize > self.remapped_indices.len()
            || draw.index_count as usize > self.remapped_indices.len() - draw.first_index as usize
        {
            return None;
        }
        let index = *self
            .remapped_indices
            .get(draw.first_index as usize + record as usize)?;
        ((index as usize) < self.brushes.len()).then_some(index)
    }
    pub fn draw_brush(
        &self,
        command: u32,
        record: u32,
    ) -> Result<Option<(u32, &wire::Brush)>, Error> {
        let view = self
            .scene
            .envelope()
            .command(command)
            .ok_or(Error::InvalidInput)?;
        if view.payload.is_empty() {
            return Ok(None);
        }
        let index = self
            .draw_brush_index(command, record)
            .ok_or(Error::InvalidInput)?;
        Ok(Some((
            index,
            self.packed_brush(index).ok_or(Error::InvalidInput)?,
        )))
    }
}
