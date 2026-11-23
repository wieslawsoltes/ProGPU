//! Original save/layer state cursor and independent guideline selection.
use super::*;
pub fn wpf_guideline_offset(value: f32) -> f32 {
    if !(value.abs() < 8388608.0) {
        return 0.0;
    }
    let mut offset = (value + 0.5).floor() - value;
    if offset <= -0.5 {
        offset += 1.0;
    }
    offset
}
/// Original ordered search over an admitted bounded axis. Ties select its lower coordinate.
pub fn nearest_guideline_index(count: u32, coordinate: f32, read: impl Fn(u32) -> f32) -> u32 {
    if count <= 1 || coordinate <= read(0) {
        return 0;
    }
    let (mut lower, mut upper) = (0, count - 1);
    let (mut lower_value, mut upper_value) = (read(lower), read(upper));
    if coordinate > upper_value {
        return upper;
    }
    while upper - lower > 1 {
        let middle = (lower + upper) >> 1;
        let value = read(middle);
        if coordinate > value {
            lower = middle;
            lower_value = value;
        } else {
            upper = middle;
            upper_value = value;
        }
    }
    if upper_value - coordinate < coordinate - lower_value {
        upper
    } else {
        lower
    }
}
pub fn uniform_guideline_translation(payload: &[u8], dpi: [f32; 2]) -> Option<wire::Point> {
    if payload.len() < wire::GuidelineSet::WIRE_SIZE
        || dpi.into_iter().any(|v| !v.is_finite() || v <= 0.0)
    {
        return None;
    }
    let h = wire::GuidelineSet::read(payload)?;
    if h.struct_size != wire::GuidelineSet::WIRE_SIZE as u32
        || h.guideline_x_count > 1
        || h.guideline_y_count > 1
        || h.flags & !SCENE_GUIDELINE_EXPLICIT_OFFSETS != 0
    {
        return None;
    }
    let explicit = h.flags & SCENE_GUIDELINE_EXPLICIT_OFFSETS != 0;
    let count = h.guideline_x_count + h.guideline_y_count;
    if payload.len()
        != wire::GuidelineSet::WIRE_SIZE + count as usize * 8 * if explicit { 2 } else { 1 }
    {
        return None;
    }
    let axis = |index: u32, dpi: f32| -> Option<f32> {
        let offset =
            wire::GuidelineSet::WIRE_SIZE + (index + if explicit { count } else { 0 }) as usize * 8;
        let value = <f64 as WireRecord>::read(&payload[offset..])?;
        Some(
            if explicit {
                value as f32
            } else {
                wpf_guideline_offset(value as f32 * dpi)
            } / dpi,
        )
    };
    let result = wire::Point {
        x: if h.guideline_x_count == 0 {
            0.0
        } else {
            axis(0, dpi[0])?
        },
        y: if h.guideline_y_count == 0 {
            0.0
        } else {
            axis(h.guideline_x_count, dpi[1])?
        },
    };
    (result.x.is_finite() && result.y.is_finite()).then_some(result)
}
pub struct NativeStateCursor {
    scene: Arc<NativeScene>,
    dpi: [f32; 2],
    stack: [wire::State; SCENE_MAX_STACK_DEPTH as usize],
    depth: usize,
    current: wire::State,
}
impl NativeStateCursor {
    pub fn new(scene: Arc<NativeScene>, dpi: [f32; 2]) -> Self {
        Self {
            scene,
            dpi,
            stack: [wire::State::default(); SCENE_MAX_STACK_DEPTH as usize],
            depth: 0,
            current: identity_state(),
        }
    }
    pub fn advance(&mut self, command: wire::Command) -> Result<wire::State, Error> {
        if matches!(command.kind, SCENE_COMMAND_SAVE | SCENE_COMMAND_PUSH_LAYER) {
            if self.depth == self.stack.len() {
                return Err(Error::InvalidInput);
            }
            let next = if command.state_index != SCENE_NO_INDEX {
                self.resolve_state(command.state_index)?
            } else {
                self.current
            };
            self.stack[self.depth] = self.current;
            self.depth += 1;
            self.current = next;
            return Ok(next);
        }
        if matches!(
            command.kind,
            SCENE_COMMAND_RESTORE | SCENE_COMMAND_POP_LAYER
        ) {
            if self.depth == 0 {
                return Err(Error::InvalidInput);
            }
            self.depth -= 1;
            self.current = self.stack[self.depth];
            return Ok(self.current);
        }
        if command.state_index != SCENE_NO_INDEX {
            self.resolve_state(command.state_index)
        } else {
            Ok(self.current)
        }
    }
    pub fn resolve_state(&self, index: u32) -> Result<wire::State, Error> {
        self.resolve_guidelines(self.read_composite_state(index)?)
    }
    pub fn read_composite_state(&self, index: u32) -> Result<wire::State, Error> {
        let resource = self
            .scene
            .envelope()
            .resource(index)
            .ok_or(Error::InvalidInput)?;
        if resource.record.kind != SCENE_RESOURCE_STATE {
            return Err(Error::InvalidInput);
        }
        wire::State::read(resource.payload).ok_or(Error::InvalidInput)
    }
    fn guidelines(
        &self,
        state: &wire::State,
    ) -> Result<Option<(wire::GuidelineSet, &[u8])>, Error> {
        if state.flags & SCENE_STATE_GUIDELINE_SET == 0 {
            return Ok(None);
        }
        let resource = self
            .scene
            .envelope()
            .resource(state.guideline_resource_index)
            .ok_or(Error::InvalidInput)?;
        if resource.record.kind != SCENE_RESOURCE_GUIDELINE_SET {
            return Err(Error::InvalidInput);
        }
        Ok(Some((
            wire::GuidelineSet::read(resource.payload).ok_or(Error::InvalidInput)?,
            resource.payload,
        )))
    }
    fn guideline_flags(&self, state: &wire::State) -> Result<u32, Error> {
        if state.flags & SCENE_STATE_GUIDELINE_SET == 0
            || state.guideline_resource_index >= self.scene.envelope().header().resource_count
        {
            return Ok(0);
        }
        Ok(self
            .guidelines(state)?
            .map_or(0, |(header, _)| header.flags))
    }
    pub fn has_per_point_guidelines(&self, state: &wire::State) -> Result<bool, Error> {
        Ok(self.guideline_flags(state)? & SCENE_GUIDELINE_PER_POINT != 0)
    }
    pub fn snap_composite_point(
        &self,
        state: &wire::State,
        point: &mut wire::Point,
    ) -> Result<(), Error> {
        if self.guideline_flags(state)? & SCENE_GUIDELINE_PER_POINT != 0 {
            return Ok(());
        }
        self.snap_point(state, point)
    }
    pub fn snap_draw_point(
        &self,
        state: &wire::State,
        point: &mut wire::Point,
    ) -> Result<(), Error> {
        if !self.has_per_point_guidelines(state)? {
            return Ok(());
        }
        self.snap_point(state, point)
    }
    fn snap_point(&self, state: &wire::State, point: &mut wire::Point) -> Result<(), Error> {
        if state.flags & SCENE_STATE_GUIDELINE_SET == 0
            || self.dpi.into_iter().any(|v| !v.is_finite() || v <= 0.0)
        {
            return Ok(());
        }
        let Some((h, payload)) = self.guidelines(state)? else {
            return Ok(());
        };
        let explicit = h.flags & SCENE_GUIDELINE_EXPLICIT_OFFSETS != 0;
        let count = h.guideline_x_count as usize + h.guideline_y_count as usize;
        let offset_base = wire::GuidelineSet::WIRE_SIZE + count * 8;
        let snap = |coordinate: f32,
                    offset: usize,
                    count: u32,
                    base: usize,
                    dpi: f32|
         -> Result<f32, Error> {
            if count == 0 {
                return Ok(coordinate);
            }
            // These spans belong to the completely admitted immutable resource.
            let read = |index: u32| read_double(payload, offset + index as usize * 8) as f32 * dpi;
            let selected = nearest_guideline_index(count, coordinate * dpi, read);
            let mut displacement = wpf_guideline_offset(read(selected));
            if explicit {
                displacement =
                    read_double(payload, offset_base + (base + selected as usize) * 8) as f32;
            }
            Ok(coordinate + displacement / dpi)
        };
        let x = snap(
            point.x,
            wire::GuidelineSet::WIRE_SIZE,
            h.guideline_x_count,
            0,
            self.dpi[0],
        )?;
        let y = snap(
            point.y,
            wire::GuidelineSet::WIRE_SIZE + h.guideline_x_count as usize * 8,
            h.guideline_y_count,
            h.guideline_x_count as usize,
            self.dpi[1],
        )?;
        point.x = x;
        point.y = y;
        Ok(())
    }
    fn resolve_guidelines(&self, mut state: wire::State) -> Result<wire::State, Error> {
        if state.flags & SCENE_STATE_GUIDELINE_SET == 0
            || self.dpi.into_iter().any(|v| !v.is_finite() || v <= 0.0)
        {
            return Ok(state);
        }
        let Some((h, payload)) = self.guidelines(&state)? else {
            return Ok(state);
        };
        if h.flags & SCENE_GUIDELINE_PER_POINT != 0 {
            return Ok(state);
        }
        if let Some(translation) = uniform_guideline_translation(payload, self.dpi) {
            state.transform.m31 += translation.x;
            state.transform.m32 += translation.y;
            return Ok(state);
        }
        let explicit = h.flags & SCENE_GUIDELINE_EXPLICIT_OFFSETS != 0;
        let base = wire::GuidelineSet::WIRE_SIZE
            + (h.guideline_x_count as usize + h.guideline_y_count as usize) * 8;
        let mut offset = wire::GuidelineSet::WIRE_SIZE;
        if h.guideline_x_count != 0 {
            let physical = read_double(payload, offset) as f32 * self.dpi[0];
            let displacement = if explicit {
                read_double(payload, base) as f32
            } else {
                wpf_guideline_offset(physical)
            };
            state.transform.m31 += displacement / self.dpi[0];
            offset += h.guideline_x_count as usize * 8;
        }
        if h.guideline_y_count != 0 {
            let physical = read_double(payload, offset) as f32 * self.dpi[1];
            let displacement = if explicit {
                read_double(payload, base + h.guideline_x_count as usize * 8) as f32
            } else {
                wpf_guideline_offset(physical)
            };
            state.transform.m32 += displacement / self.dpi[1];
        }
        Ok(state)
    }
    /// Returns the original inverse and whether the snapped rectangle has area.
    pub fn composite_rectangle_inverse(
        &self,
        state: &wire::State,
        bounds: wire::ImageRect,
    ) -> Result<Option<(wire::Affine2d, bool)>, Error> {
        if ![bounds.x, bounds.y, bounds.width, bounds.height]
            .into_iter()
            .all(f32::is_finite)
            || bounds.width <= 0.0
            || bounds.height <= 0.0
        {
            return Ok(None);
        }
        let right = bounds.x + bounds.width;
        let bottom = bounds.y + bounds.height;
        if !right.is_finite() || !bottom.is_finite() {
            return Ok(None);
        }
        let mut first = wire::Point {
            x: bounds.x,
            y: bounds.y,
        };
        let mut last = wire::Point {
            x: right,
            y: bottom,
        };
        self.snap_composite_point(state, &mut first)?;
        self.snap_composite_point(state, &mut last)?;
        if ![first.x, first.y, last.x, last.y]
            .into_iter()
            .all(f32::is_finite)
        {
            return Ok(None);
        }
        let width = f64::from(last.x) - f64::from(first.x);
        let height = f64::from(last.y) - f64::from(first.y);
        if width <= 0.0 || height <= 0.0 {
            return Ok(Some((identity_transform(), false)));
        }
        let sx = f64::from(right - bounds.x) / width;
        let sy = f64::from(bottom - bounds.y) / height;
        let tx = f64::from(bounds.x) - f64::from(first.x) * sx;
        let ty = f64::from(bounds.y) - f64::from(first.y) * sy;
        if [sx, sy, tx, ty]
            .into_iter()
            .any(|v| !v.is_finite() || v.abs() > f64::from(f32::MAX))
        {
            return Ok(None);
        }
        Ok(Some((
            wire::Affine2d {
                m11: sx as f32,
                m12: 0.0,
                m21: 0.0,
                m22: sy as f32,
                m31: tx as f32,
                m32: ty as f32,
            },
            true,
        )))
    }
}
fn read_double(payload: &[u8], offset: usize) -> f64 {
    <f64 as WireRecord>::read(&payload[offset..]).expect("admitted guideline scalar")
}
