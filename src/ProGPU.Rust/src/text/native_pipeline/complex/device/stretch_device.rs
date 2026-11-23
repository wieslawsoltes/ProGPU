//! Original frozen-width device stretch preflight and descriptor-preserving write.
use super::super::stretch;
use super::*;
/// Caller-owned stretch run and frozen-width inventories for captured device runs.
#[derive(Clone, Debug, Default)]
pub struct NativeDeviceStretchScratch {
    runs: Vec<stretch::Run>,
    widths: Vec<i32>,
}
impl NativeDeviceStretchScratch {
    /// Allocate one run and width slot per original glyph capacity. No storage
    /// grows during callbacks, preflight or output publication.
    pub fn new(glyph_capacity: usize) -> Result<Self, FontError> {
        if glyph_capacity > 1_048_576 {
            return Err(FontError::Capacity);
        }
        let mut runs = Vec::new();
        let mut widths = Vec::new();
        runs.try_reserve_exact(glyph_capacity)
            .map_err(|_| FontError::Capacity)?;
        runs.resize(glyph_capacity, stretch::Run::default());
        widths
            .try_reserve_exact(glyph_capacity)
            .map_err(|_| FontError::Capacity)?;
        widths.resize(glyph_capacity, 0);
        Ok(Self { runs, widths })
    }
    /// Original inventory retained across synchronous runs.
    pub fn glyph_capacity(&self) -> usize {
        self.widths.len()
    }
}
fn source_index(index: usize, count: usize, rtl: bool) -> usize {
    if rtl { index } else { count - index - 1 }
}
/// Expand native Arabic stretch glyphs with exact retained descriptor identity.
/// All advance callbacks finish before original output reversal/mutation. Every
/// signed-32 offset is preflighted; on failure the original live count is retained.
pub fn apply_native_device_arabic_stretch(
    font: &SfntFace,
    coordinates: &[i16],
    storage: &mut [ShapingGlyph],
    glyph_count: &mut u32,
    right_to_left: bool,
    descriptor_mapping: &mut [u32],
    scratch: &mut NativeDeviceStretchScratch,
    frame: NativeDeviceFrame<'_>,
) -> Result<(), FontError> {
    let direction = if right_to_left {
        GposDirection::RightToLeft
    } else {
        GposDirection::LeftToRight
    };
    frame.validate(font, coordinates, direction)?;
    let count = *glyph_count as usize;
    if count > storage.len() || count > 1_048_576 {
        return Err(FontError::InvalidArgument);
    }
    if scratch.widths.len() < count || descriptor_mapping.len() < count {
        return Err(FontError::Capacity);
    }
    for (index, glyph) in storage[..count].iter().enumerate() {
        if !stretch::stretch(*glyph) {
            continue;
        }
        if glyph.glyph_id > u32::from(u16::MAX) {
            return Err(FontError::InvalidGlyph);
        }
        scratch.widths[index] = frame
            .source
            .stretch_advance(index, descriptor_mapping[index], glyph.glyph_id)
            .map_err(|_| FontError::InvalidArgument)?;
    }
    let (capacity, run_count) = stretch::runs(
        |index, _| Ok(scratch.widths[index]),
        &storage[..count],
        right_to_left,
        &mut scratch.runs,
    )?;
    if storage.len() < capacity || descriptor_mapping.len() < capacity {
        return Err(FontError::Capacity);
    }
    if run_count == 0 {
        return Ok(());
    }
    for run in &scratch.runs[..run_count] {
        let mut x = i64::from(run.remaining / 2);
        for index in (run.start..run.end).rev() {
            let source = source_index(index, count, right_to_left);
            let repeat = if joining::action(storage[source]) == 9 {
                run.copies + 1
            } else {
                1
            };
            for copy in 0..repeat {
                if right_to_left {
                    x -= i64::from(scratch.widths[source]);
                    if copy > 0 {
                        x += i64::from(run.overlap);
                    }
                }
                if i32::try_from(x).is_err() {
                    return Err(FontError::InvalidArgument);
                }
                if !right_to_left {
                    x += i64::from(scratch.widths[source]);
                    if copy > 0 {
                        x -= i64::from(run.overlap);
                    }
                }
            }
        }
    }
    if !right_to_left {
        storage[..count].reverse();
        descriptor_mapping[..count].reverse();
    }
    for run in &scratch.runs[..run_count] {
        let mut context = run.start;
        while context > 0
            && !stretch::stretch(storage[context - 1])
            && stretch::word(storage[context - 1].code_point)
        {
            context -= 1;
        }
        for glyph in &mut storage[context..run.end] {
            glyph.flags |= 3;
        }
    }
    let mut write = capacity;
    let mut source = count;
    let mut run_index = 0;
    while source > 0 {
        if !stretch::stretch(storage[source - 1]) {
            write -= 1;
            source -= 1;
            storage[write] = storage[source];
            descriptor_mapping[write] = descriptor_mapping[source];
            continue;
        }
        let run = scratch.runs[run_index];
        run_index += 1;
        source = run.start;
        let mut x = i64::from(run.remaining / 2);
        for index in (run.start..run.end).rev() {
            let mut glyph = storage[index];
            let descriptor = descriptor_mapping[index];
            let width = i64::from(scratch.widths[source_index(index, count, right_to_left)]);
            let repeat = if joining::action(glyph) == 9 {
                run.copies + 1
            } else {
                1
            };
            glyph.advance_x = 0;
            for copy in 0..repeat {
                if right_to_left {
                    x -= width;
                    if copy > 0 {
                        x += i64::from(run.overlap);
                    }
                }
                glyph.offset_x = x as i32;
                write -= 1;
                storage[write] = glyph;
                descriptor_mapping[write] = descriptor;
                if !right_to_left {
                    x += width;
                    if copy > 0 {
                        x -= i64::from(run.overlap);
                    }
                }
            }
        }
    }
    *glyph_count = capacity as u32;
    if !right_to_left {
        storage[..capacity].reverse();
        descriptor_mapping[..capacity].reverse();
    }
    Ok(())
}
