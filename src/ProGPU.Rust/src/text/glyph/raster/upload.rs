//! Original sparse record slots, contiguous staging and geometric GPU growth.
use super::*;

impl GlyphRasterizer {
    /// Reuse the exact selected font instance/glyph slot, or compile/upload only
    /// that requested outline. Coordinate-equivalent font instances do not alias.
    pub async fn upload_font_glyph(
        &mut self,
        font: &ManagedFont,
        glyph: u16,
    ) -> Result<GlyphGpuSlot, GlyphGpuError> {
        let (root, instance) = font.instance_identity();
        let key = (root, instance);
        if let Some(cached) = self
            .font_slots
            .get(&key)
            .and_then(|data| data.slots.get(&glyph))
        {
            return Ok(cached.clone());
        }
        self.font_slots
            .try_reserve(1)
            .map_err(|_| Error::Capacity)?;
        self.font_slots
            .entry(key)
            .or_insert_with(|| FontGpuData {
                _font: font.clone(),
                slots: HashMap::new(),
            })
            .slots
            .try_reserve(1)
            .map_err(|_| Error::Capacity)?;
        let mut scratch = std::mem::take(&mut self.glyph_scratch);
        scratch.clear();
        let result = match font.append_gpu_outline_data(glyph, &mut scratch) {
            Ok(record) => self
                .upload_outline(record, &scratch)
                .await
                .map_err(GlyphGpuError::from),
            Err(error) => Err(error.into()),
        };
        scratch.clear();
        release_oversized(&mut scratch, INITIAL_SEGMENTS as usize);
        self.glyph_scratch = scratch;
        let slot = result?;
        self.font_slots
            .get_mut(&key)
            .expect("selected font data retained across upload")
            .slots
            .insert(glyph, slot.clone());
        Ok(slot)
    }
    /// Upload the exact selected original segment range, rebasing only its
    /// descriptor offset. Coordinates, record bounds and all segment metadata
    /// words are retained; this does not execute a font or infer raster policy.
    /// Empty records retain an actual slot and are rejected only at raster use.
    pub async fn upload_outline(
        &mut self,
        mut record: GpuGlyphRecord,
        source: &[Segment],
    ) -> Result<GlyphGpuSlot, Error> {
        let start = record.start_segment as usize;
        let count = record.segment_count as usize;
        let selected = source
            .get(start..start.checked_add(count).ok_or(Error::Capacity)?)
            .ok_or(Error::InvalidInput)?;
        if selected.iter().any(|s| s.kind > 2) {
            return Err(Error::InvalidInput);
        }
        let required_records = self.record_count.checked_add(1).ok_or(Error::Capacity)?;
        let required_segments = self
            .segment_count
            .checked_add(record.segment_count)
            .ok_or(Error::Capacity)?;
        let record_capacity = grow_capacity(self.record_capacity, required_records)?;
        let segment_capacity = grow_capacity(self.segment_capacity, required_segments)?;
        check_buffer(&self.device, u64::from(record_capacity) * 32)?;
        check_buffer(&self.device, u64::from(segment_capacity) * 48)?;
        let growth =
            record_capacity != self.record_capacity || segment_capacity != self.segment_capacity;
        let record_chunk_full = !self.pending_records.is_empty()
            && (self.pending_records.len() + 1) * 32 > OUTLINE_CHUNK;
        let segment_chunk_full = !self.pending_segments.is_empty()
            && (self.pending_segments.len() + count) * 48 > OUTLINE_CHUNK;
        if growth || record_chunk_full || segment_chunk_full {
            self.flush_outline_uploads();
        }
        self.pending_records
            .try_reserve(1)
            .map_err(|_| Error::Capacity)?;
        self.pending_segments
            .try_reserve(count)
            .map_err(|_| Error::Capacity)?;
        let scope = self.device.push_error_scope(wgpu::ErrorFilter::Validation);
        // Source copy submissions follow pending writes and precede the next
        // coverage submission. Existing commands retain their own old buffers.
        let new_records = (record_capacity != self.record_capacity)
            .then(|| outline_buffer(&self.device, record_capacity, 32));
        let new_segments = (segment_capacity != self.segment_capacity)
            .then(|| outline_buffer(&self.device, segment_capacity, 48));
        if let Some(error) = scope.pop().await {
            return Err(error.into());
        }
        if let Some(replacement) = new_records {
            let previous = self.records.clone();
            self.copy_outline_buffer(&previous, &replacement, u64::from(self.record_count) * 32);
            self.records = replacement;
            self.record_capacity = record_capacity;
        }
        if let Some(replacement) = new_segments {
            let previous = self.segments.clone();
            self.copy_outline_buffer(&previous, &replacement, u64::from(self.segment_count) * 48);
            self.segments = replacement;
            self.segment_capacity = segment_capacity;
        }
        if growth {
            self.ring_bindings = None;
            self.binary_bindings = None;
        }
        record.start_segment = self.segment_count;
        let slot = GlyphGpuSlot {
            owner: self.owner.clone(),
            generation: self.generation,
            index: self.record_count,
            record,
        };
        if self.pending_records.is_empty() {
            self.pending_record_start = self.record_count;
        }
        self.pending_records.push(record);
        if !selected.is_empty() {
            if self.pending_segments.is_empty() {
                self.pending_segment_start = self.segment_count;
            }
            self.pending_segments.extend_from_slice(selected);
        }
        self.record_count = required_records;
        self.segment_count = required_segments;
        Ok(slot)
    }
    /// Materialize pending contiguous original payloads. Called automatically
    /// before growth copies and coverage submissions; no per-glyph queue submit.
    pub fn flush_outline_uploads(&mut self) {
        if !self.pending_records.is_empty() {
            let bytes = bytemuck::cast_slice(&self.pending_records);
            self.queue.write_buffer(
                &self.records,
                u64::from(self.pending_record_start) * 32,
                bytes,
            );
            self.metrics.outline_upload_writes += 1;
            self.metrics.outline_upload_bytes += bytes.len() as u64;
            self.pending_records.clear();
            release_oversized(&mut self.pending_records, INITIAL_RECORDS as usize);
        }
        if !self.pending_segments.is_empty() {
            let bytes = bytemuck::cast_slice(&self.pending_segments);
            self.queue.write_buffer(
                &self.segments,
                u64::from(self.pending_segment_start) * 48,
                bytes,
            );
            self.metrics.outline_upload_writes += 1;
            self.metrics.outline_upload_bytes += bytes.len() as u64;
            self.pending_segments.clear();
            release_oversized(&mut self.pending_segments, INITIAL_SEGMENTS as usize);
        }
    }
    /// Original frame-boundary budget trim. Call before collecting new requests:
    /// a successful reset invalidates old record slots, while existing atlas
    /// coverage and previously submitted commands keep their actual GPU owners.
    pub async fn trim_outline_cache(&mut self) -> Result<bool, Error> {
        if self.allocated_outline_bytes() <= OUTLINE_BUDGET {
            return Ok(false);
        }
        let generation = self.generation.checked_add(1).ok_or(Error::Capacity)?;
        let scope = self.device.push_error_scope(wgpu::ErrorFilter::Validation);
        let records = outline_buffer(&self.device, INITIAL_RECORDS, 32);
        let segments = outline_buffer(&self.device, INITIAL_SEGMENTS, 48);
        if let Some(error) = scope.pop().await {
            return Err(error.into());
        }
        self.ring_bindings = None;
        self.binary_bindings = None;
        self.records = records;
        self.segments = segments;
        self.record_count = 0;
        self.segment_count = 0;
        self.record_capacity = INITIAL_RECORDS;
        self.segment_capacity = INITIAL_SEGMENTS;
        self.pending_records.clear();
        self.pending_segments.clear();
        self.font_slots.clear();
        release_oversized(&mut self.pending_records, INITIAL_RECORDS as usize);
        release_oversized(&mut self.pending_segments, INITIAL_SEGMENTS as usize);
        self.generation = generation;
        self.metrics.outline_cache_resets += 1;
        Ok(true)
    }
    fn copy_outline_buffer(
        &mut self,
        source: &wgpu::Buffer,
        destination: &wgpu::Buffer,
        bytes: u64,
    ) {
        if bytes == 0 {
            return;
        }
        let mut encoder = self
            .device
            .create_command_encoder(&wgpu::CommandEncoderDescriptor {
                label: Some("ProGPU glyph outline growth"),
            });
        encoder.copy_buffer_to_buffer(source, 0, destination, 0, bytes);
        self.queue.submit([encoder.finish()]);
        self.metrics.outline_copy_submissions += 1;
    }
}
fn grow_capacity(current: u32, required: u32) -> Result<u32, Error> {
    let mut capacity = current;
    while capacity < required {
        capacity = capacity
            .checked_add((capacity / 2).max(1))
            .ok_or(Error::Capacity)?;
    }
    if capacity > i32::MAX as u32 {
        return Err(Error::Capacity);
    }
    Ok(capacity)
}
fn release_oversized<T>(values: &mut Vec<T>, retained: usize) {
    if values.is_empty() && values.capacity().saturating_mul(size_of::<T>()) > OUTLINE_CHUNK {
        values.shrink_to(retained);
    }
}
