//! Original native state and guideline producers; finite and non-finite static
//! guideline policies remain separate from explicitly resolved offsets.
use super::*;

impl NativeSceneBuilder {
    pub fn add_state(&mut self, source: wire::State) -> BuildResult<u32> {
        let has = |flag| source.flags & flag != 0;
        let kind_is = |index: u32, kind| {
            self.resources
                .get(index as usize)
                .is_some_and(|r| r.record.kind == kind)
        };
        if !finite_transform(source.transform)
            || !source.opacity.is_finite()
            || source.opacity < 0.0
            || source.opacity > 1.0
            || source.flags
                & !(SCENE_STATE_CLIP_RECT
                    | SCENE_STATE_MASK
                    | SCENE_STATE_GUIDELINE_SET
                    | SCENE_STATE_CLIP_PIXEL_CENTERS)
                != 0
            || (has(SCENE_STATE_CLIP_PIXEL_CENTERS) && !has(SCENE_STATE_CLIP_RECT))
            || (has(SCENE_STATE_CLIP_RECT) && !finite_rect(source.clip_rect))
            || (has(SCENE_STATE_MASK)
                && !kind_is(source.mask_resource_index, SCENE_RESOURCE_LAYER_MASK))
            || (has(SCENE_STATE_GUIDELINE_SET)
                && !kind_is(
                    source.guideline_resource_index,
                    SCENE_RESOURCE_GUIDELINE_SET,
                ))
            || self.resources.len() >= MAX_RESOURCES as usize
        {
            return Err(B::InvalidArgument);
        }
        reserve_append(&mut self.resources, 1)?;
        let state = wire::State {
            struct_size: wire::State::WIRE_SIZE as u32,
            reserved: 0,
            mask_resource_index: if has(SCENE_STATE_MASK) {
                source.mask_resource_index
            } else {
                0
            },
            guideline_resource_index: if has(SCENE_STATE_GUIDELINE_SET) {
                source.guideline_resource_index
            } else {
                0
            },
            ..source
        };
        let mut resource = self.new_resource(SCENE_RESOURCE_STATE);
        resource.payload = encode(&[state])?;
        let index = self.resources.len() as u32;
        self.resources.push(resource);
        Ok(index)
    }

    pub fn add_guideline_set(
        &mut self,
        x: &[f64],
        y: &[f64],
        composite_only: bool,
        per_point: bool,
    ) -> BuildResult<u32> {
        self.add_guidelines(x, y, None, composite_only, per_point)
    }
    pub fn add_guideline_set_with_offsets(
        &mut self,
        x: &[f64],
        y: &[f64],
        offsets_x: &[f64],
        offsets_y: &[f64],
        composite_only: bool,
        per_point: bool,
    ) -> BuildResult<u32> {
        self.add_guidelines(
            x,
            y,
            Some((offsets_x, offsets_y)),
            composite_only,
            per_point,
        )
    }
    fn add_guidelines(
        &mut self,
        x: &[f64],
        y: &[f64],
        offsets: Option<(&[f64], &[f64])>,
        composite_only: bool,
        per_point: bool,
    ) -> BuildResult<u32> {
        let multiple = x.len() > 1 || y.len() > 1;
        if (composite_only && per_point)
            || multiple != (composite_only || per_point)
            || x.len() > SCENE_MAX_GUIDELINES_PER_AXIS as usize
            || y.len() > SCENE_MAX_GUIDELINES_PER_AXIS as usize
            || self.resources.len() >= MAX_RESOURCES as usize
            || !x.windows(2).all(|p| p[0] <= p[1])
            || !y.windows(2).all(|p| p[0] <= p[1])
            || x.iter().chain(y).any(|value| {
                if offsets.is_some() {
                    !value.is_finite()
                } else {
                    value.is_nan()
                }
            })
        {
            return Err(B::InvalidArgument);
        }
        if let Some((ox, oy)) = offsets {
            if ox.len() != x.len()
                || oy.len() != y.len()
                || !ox.iter().chain(oy).all(|v| v.is_finite() && v.abs() <= 1.0)
            {
                return Err(B::InvalidArgument);
            }
        }
        // The original static branch grows resource storage before copying;
        // explicit-offset publication reserves only after its candidate exists.
        if offsets.is_none() {
            reserve_append(&mut self.resources, 1)?;
        }
        let header = wire::GuidelineSet {
            struct_size: wire::GuidelineSet::WIRE_SIZE as u32,
            flags: if composite_only {
                SCENE_GUIDELINE_COMPOSITE_ONLY
            } else if per_point {
                SCENE_GUIDELINE_PER_POINT
            } else {
                0
            } | if offsets.is_some() {
                SCENE_GUIDELINE_EXPLICIT_OFFSETS
            } else {
                0
            },
            guideline_x_count: x.len() as u32,
            guideline_y_count: y.len() as u32,
        };
        let mut resource = self.new_resource(SCENE_RESOURCE_GUIDELINE_SET);
        resource.payload = encode(&[header])?;
        append_encoded(&mut resource.payload, x)?;
        append_encoded(&mut resource.payload, y)?;
        if let Some((ox, oy)) = offsets {
            append_encoded(&mut resource.payload, ox)?;
            append_encoded(&mut resource.payload, oy)?;
            reserve_append(&mut self.resources, 1)?;
        }
        let index = self.resources.len() as u32;
        self.resources.push(resource);
        Ok(index)
    }

    /// Copies a previously admitted guideline into this builder's generation.
    pub fn copy_guideline_set_from(
        &mut self,
        source: &Self,
        source_index: u32,
    ) -> BuildResult<u32> {
        let resource = source
            .resources
            .get(source_index as usize)
            .filter(|r| r.record.kind == SCENE_RESOURCE_GUIDELINE_SET)
            .ok_or(B::InvalidArgument)?;
        self.copy_guideline_bytes(&resource.payload, &resource.auxiliary, resource.record)
    }
    /// Explicit self-copy avoids aliasing an exclusive mutable Rust borrow.
    pub fn duplicate_guideline_set(&mut self, source_index: u32) -> BuildResult<u32> {
        let source = self
            .resources
            .get(source_index as usize)
            .filter(|r| r.record.kind == SCENE_RESOURCE_GUIDELINE_SET)
            .ok_or(B::InvalidArgument)?;
        if self.resources.len() >= MAX_RESOURCES as usize {
            return Err(B::CapacityExceeded);
        }
        let payload = copy_bytes(&source.payload)?;
        let auxiliary = copy_bytes(&source.auxiliary)?;
        let record = source.record;
        self.publish_guideline_copy(record, payload, auxiliary)
    }
    fn copy_guideline_bytes(
        &mut self,
        payload: &[u8],
        auxiliary: &[u8],
        record: wire::Resource,
    ) -> BuildResult<u32> {
        if self.resources.len() >= MAX_RESOURCES as usize {
            return Err(B::CapacityExceeded);
        }
        self.publish_guideline_copy(record, copy_bytes(payload)?, copy_bytes(auxiliary)?)
    }
    fn publish_guideline_copy(
        &mut self,
        mut record: wire::Resource,
        payload: Vec<u8>,
        auxiliary: Vec<u8>,
    ) -> BuildResult<u32> {
        reserve_append(&mut self.resources, 1)?;
        let index = self.resources.len() as u32;
        record.resource_id = u64::from(index) + 1;
        record.generation = self.generation;
        self.resources.push(ResourceEntry {
            record,
            payload,
            auxiliary,
            ..Default::default()
        });
        Ok(index)
    }

    pub fn glyph_guideline_offset(&self, index: u32, origin_y: f32, dpi: f32) -> Option<f32> {
        if !origin_y.is_finite() || !dpi.is_finite() || dpi <= 0.0 || !(origin_y * dpi).is_finite()
        {
            return None;
        }
        let resource = self
            .resources
            .get(index as usize)
            .filter(|r| r.record.kind == SCENE_RESOURCE_GUIDELINE_SET)?;
        let header = wire::GuidelineSet::read(&resource.payload)?;
        if header.guideline_y_count == 0 {
            return Some(0.0);
        }
        let read = |index: u32| {
            f64::read(&resource.payload[wire::GuidelineSet::WIRE_SIZE + index as usize * 8..])
                .map(|v| v as f32)
        };
        // All coordinates belong to a builder-produced fixed, complete span.
        let selected = nearest_guideline_index(header.guideline_y_count, origin_y * dpi, |i| {
            read(header.guideline_x_count + i).expect("owned guideline span") * dpi
        });
        let index = header.guideline_x_count + selected;
        let offset = if header.flags & SCENE_GUIDELINE_EXPLICIT_OFFSETS != 0 {
            read(index + header.guideline_x_count + header.guideline_y_count)?
        } else {
            wpf_guideline_offset(read(index)? * dpi)
        };
        offset.is_finite().then_some(offset)
    }

    pub fn uniform_guideline_translation(&self, index: u32, dpi: f32) -> Option<wire::Point> {
        let resource = self
            .resources
            .get(index as usize)
            .filter(|r| r.record.kind == SCENE_RESOURCE_GUIDELINE_SET)?;
        uniform_guideline_translation(&resource.payload, dpi, dpi)
    }
}

fn wpf_guideline_offset(value: f32) -> f32 {
    if !(value.abs() < 8388608.0) {
        return 0.0;
    }
    let mut offset = (value + 0.5).floor() - value;
    if offset <= -0.5 {
        offset += 1.0;
    }
    offset
}
fn nearest_guideline_index(count: u32, coordinate: f32, read: impl Fn(u32) -> f32) -> u32 {
    if count <= 1 || coordinate <= read(0) {
        return 0;
    }
    let (mut lower, mut upper) = (0, count - 1);
    let (mut low, mut high) = (read(lower), read(upper));
    if coordinate > high {
        return upper;
    }
    while upper - lower > 1 {
        let middle = (lower + upper) >> 1;
        let value = read(middle);
        if coordinate > value {
            lower = middle;
            low = value;
        } else {
            upper = middle;
            high = value;
        }
    }
    if high - coordinate < coordinate - low {
        upper
    } else {
        lower
    }
}
fn uniform_guideline_translation(payload: &[u8], dpi_x: f32, dpi_y: f32) -> Option<wire::Point> {
    if !dpi_x.is_finite() || dpi_x <= 0.0 || !dpi_y.is_finite() || dpi_y <= 0.0 {
        return None;
    }
    let header = wire::GuidelineSet::read(payload)?;
    if header.struct_size != wire::GuidelineSet::WIRE_SIZE as u32
        || header.guideline_x_count > 1
        || header.guideline_y_count > 1
        || header.flags & !SCENE_GUIDELINE_EXPLICIT_OFFSETS != 0
    {
        return None;
    }
    let explicit = header.flags & SCENE_GUIDELINE_EXPLICIT_OFFSETS != 0;
    let count = header.guideline_x_count + header.guideline_y_count;
    if payload.len()
        != wire::GuidelineSet::WIRE_SIZE + count as usize * 8 * if explicit { 2 } else { 1 }
    {
        return None;
    }
    let axis = |index: u32, dpi: f32| {
        let start =
            wire::GuidelineSet::WIRE_SIZE + (index + if explicit { count } else { 0 }) as usize * 8;
        let value = f64::read(&payload[start..])? as f32;
        Some(
            if explicit {
                value
            } else {
                wpf_guideline_offset(value * dpi)
            } / dpi,
        )
    };
    let result = wire::Point {
        x: if header.guideline_x_count == 0 {
            0.0
        } else {
            axis(0, dpi_x)?
        },
        y: if header.guideline_y_count == 0 {
            0.0
        } else {
            axis(header.guideline_x_count, dpi_y)?
        },
    };
    (result.x.is_finite() && result.y.is_finite()).then_some(result)
}
