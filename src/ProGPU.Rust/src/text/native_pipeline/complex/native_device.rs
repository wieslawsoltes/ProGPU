//! Original owned hinted-run services around a required actual capture provider.
//! Provider execution is separate from native GSUB/GPOS and never replaced with
//! unhinted outlines, managed shaping or inferred device metrics.
use super::*;
use std::cell::Cell;
use std::sync::Arc;
#[path = "native_device/binding.rs"]
mod binding;
#[path = "native_device/positioning.rs"]
mod positioning;
#[path = "native_device/transport.rs"]
mod transport;
use binding::{Auxiliary, BoundBatch};
pub use positioning::{NativeDevicePositionedSlice, NativeDevicePositioningRecipe};
pub use transport::*;
const MAXIMUM_GLYPHS: usize = 1_048_576;

/// Published native device run retaining the actual whole capture independently
/// of provider/context lifetime and every final occurrence's original descriptor.
#[derive(Debug)]
pub struct NativeDeviceRun {
    batch: Arc<NativeHintedBatch>,
    source: Vec<UnicodeScalar>,
    coordinates: Vec<i16>,
    glyphs: Vec<ShapingGlyph>,
    mapping: Vec<u32>,
    source_count: usize,
    auxiliary: Auxiliary,
    direction: GposDirection,
    projection: NativeHintedProjection,
    positioning: Option<NativeDevicePositioningRecipe>,
    boundaries: Vec<ShapingGlyph>,
}
impl NativeDeviceRun {
    /// Complete actual captured generation, including auxiliary/no-ink slots.
    pub fn batch(&self) -> &Arc<NativeHintedBatch> {
        &self.batch
    }
    /// Original decoded scalars before normalization, preprocessing and GSUB.
    pub fn source(&self) -> &[UnicodeScalar] {
        &self.source
    }
    /// Frozen original normalized variation coordinates.
    pub fn normalized_coordinates(&self) -> &[i16] {
        &self.coordinates
    }
    /// Final native device positions in 26.6 units, with private metadata retired.
    pub fn glyphs(&self) -> &[ShapingGlyph] {
        &self.glyphs
    }
    /// One original capture descriptor per final occurrence; repeats are retained.
    pub fn descriptor_indices(&self) -> &[u32] {
        &self.mapping
    }
    /// Exact post-GSUB prefix before auxiliary descriptors were appended.
    pub fn source_descriptor_count(&self) -> usize {
        self.source_count
    }
    /// Actual figure-space auxiliary range in the complete batch.
    pub fn figure_descriptors(&self) -> std::ops::Range<usize> {
        self.auxiliary.figure.0..self.auxiliary.figure.0 + self.auxiliary.figure.1
    }
    /// Actual punctuation-space auxiliary range in the complete batch.
    pub fn punctuation_descriptors(&self) -> std::ops::Range<usize> {
        self.auxiliary.punctuation.0..self.auxiliary.punctuation.0 + self.auxiliary.punctuation.1
    }
    /// Exact final direction.
    pub fn direction(&self) -> GposDirection {
        self.direction
    }
    /// Selected CPU-visible original projection policy.
    pub fn projection(&self) -> NativeHintedProjection {
        self.projection
    }
    /// Original bounded single/pair placement recipe, when fully admitted.
    pub fn positioning_recipe(&self) -> Option<&NativeDevicePositioningRecipe> {
        self.positioning.as_ref()
    }
    /// Original pre-positioning dependencies. Empty means no retained recipe or
    /// boundary inventory was admitted; final glyphs remain independently valid.
    pub fn prepared_glyphs(&self) -> &[ShapingGlyph] {
        self.positioning
            .as_ref()
            .map_or(&self.boundaries, |r| r.prepared_glyphs())
    }
}

pub(super) struct DeviceOwner<'a> {
    provider: &'a mut dyn NativeHintedProvider,
    request: &'a NativeHintedRequest,
    projection: NativeHintedProjection,
    batch: Option<Arc<NativeHintedBatch>>,
    mapping: Vec<u32>,
    source_count: usize,
    auxiliary: Auxiliary,
    stretch: NativeDeviceStretchScratch,
    capture_recipes: bool,
    positioning: Option<NativeDevicePositioningRecipe>,
    boundaries: Vec<ShapingGlyph>,
    capture_failure: Option<NativeHintedCaptureError>,
    projection_failure: Cell<Option<NativeHintedProjectionError>>,
}
impl<'a> DeviceOwner<'a> {
    fn new(
        provider: &'a mut dyn NativeHintedProvider,
        request: &'a NativeHintedRequest,
        projection: NativeHintedProjection,
        capacity: usize,
        capture_recipes: bool,
    ) -> Result<Self, FontError> {
        Ok(Self {
            provider,
            request,
            projection,
            batch: None,
            mapping: filled(capacity, 0)?,
            source_count: 0,
            auxiliary: Auxiliary::default(),
            stretch: NativeDeviceStretchScratch::new(capacity)?,
            capture_recipes,
            positioning: None,
            boundaries: Vec::new(),
            capture_failure: None,
            projection_failure: Cell::new(None),
        })
    }
    fn error(&self, shaping: FontError) -> NativeDeviceError {
        NativeDeviceError {
            shaping,
            capture: self.capture_failure,
            projection: self.projection_failure.get(),
        }
    }
    fn prepare(
        &mut self,
        shaper: &NativeShaper,
        glyphs: &mut [ShapingGlyph],
    ) -> Result<(), FontError> {
        if self.batch.is_some() || glyphs.len() > self.mapping.len() {
            return Err(FontError::InvalidArgument);
        }
        self.source_count = glyphs.len();
        let mut ids = Vec::new();
        ids.try_reserve_exact(glyphs.len().checked_add(2).ok_or(FontError::Capacity)?)
            .map_err(|_| FontError::Capacity)?;
        ids.extend(glyphs.iter().map(|g| g.glyph_id));
        self.auxiliary.figure = append_auxiliary(&shaper.font, glyphs, 0x2007, &mut ids)?;
        self.auxiliary.punctuation = append_auxiliary(&shaper.font, glyphs, 0x2008, &mut ids)?;
        let batch = self
            .provider
            .capture(&shaper.font, self.request, &ids)
            .map_err(|error| {
                self.capture_failure = Some(error);
                if error == NativeHintedCaptureError::ResourceExhausted {
                    FontError::Capacity
                } else {
                    FontError::InvalidFace
                }
            })?;
        if !batch.matches(&shaper.font, self.request, &ids) {
            return Err(FontError::InvalidArgument);
        }
        let bound = BoundBatch {
            font: &shaper.font,
            coordinates: shaper.normalized_coordinates(),
            batch: &batch,
            policy: self.projection,
            source_count: self.source_count,
            auxiliary: self.auxiliary,
            failure: &self.projection_failure,
        };
        bound.initialize(glyphs, shaper.route.direction)?;
        for (index, descriptor) in self.mapping[..self.source_count].iter_mut().enumerate() {
            *descriptor = index as u32;
        }
        self.batch = Some(batch);
        Ok(())
    }
    pub(super) fn position(
        &mut self,
        shaper: &NativeShaper,
        b: &mut Buffer<'_>,
        attachments: &mut [GposAttachment],
        states: &mut [u8],
        gdef: Option<Gdef<'_>>,
        flags: NativeBufferFlags,
    ) -> Result<(), FontError> {
        let count = b.count;
        // Freeze original ignorable sentinels before actual captured metrics replace them.
        for (g, state) in b.glyphs().iter().zip(states.iter_mut()) {
            *state = u8::from(
                input::default_ignorable(g.code_point)
                    && g.advance_x != i32::MIN
                    && !flags.contains(NativeBufferFlags::PRESERVE_DEFAULT_IGNORABLES),
            );
        }
        self.prepare(shaper, b.glyphs_mut())?;
        let batch = self.batch.as_ref().ok_or(FontError::InvalidArgument)?;
        let source = BoundBatch {
            font: &shaper.font,
            coordinates: shaper.normalized_coordinates(),
            batch,
            policy: self.projection,
            source_count: self.source_count,
            auxiliary: self.auxiliary,
            failure: &self.projection_failure,
        };
        let frame = NativeDeviceFrame {
            source: &source,
            arithmetic: source.bind()?,
        };
        let direction = shaper.route.direction;
        let vertical = matches!(
            direction,
            GposDirection::TopToBottom | GposDirection::BottomToTop
        );
        let adjust = shaper.gpos.is_none()
            && matches!(
                direction,
                GposDirection::LeftToRight | GposDirection::TopToBottom
            );
        let early_zero = shaper.zero_marks
            && matches!(
                shaper.route.complex,
                NativeComplexKind::Use | NativeComplexKind::Myanmar
            );
        let glyphs = b.glyphs_mut();
        for (index, (g, attachment)) in glyphs.iter_mut().zip(attachments.iter_mut()).enumerate() {
            *attachment = GposAttachment::default();
            if shaper.fallback_mark_positioning() {
                attachment.component_count = ((g.flags & 0x7f8) >> 3) as u8;
                let encoded = ((g.flags & 0x7f800) >> 11) as u8;
                attachment.component = if encoded == 0 { 255 } else { encoded - 1 };
                g.flags &= !0x7fff8;
            }
            if g.glyph_id > u32::from(u16::MAX) {
                return Err(FontError::InvalidGlyph);
            }
            apply_native_device_space(
                &shaper.font,
                shaper.normalized_coordinates(),
                direction,
                g,
                frame,
            )?;
            if early_zero && positioning_mark(*g, gdef) {
                zero_mark(g, adjust)?;
            }
            if states[index] != 0 {
                g.advance_x = 0;
                g.advance_y = 0;
            }
        }
        let capture = self.capture_recipes
            && !shaper.route.arabic_joining
            && shaper.route.complex == NativeComplexKind::None
            && !vertical
            && shaper.level == NativeClusterLevel::MonotoneGraphemes;
        if capture && !glyphs.iter().any(|&g| positioning_mark(g, gdef)) {
            self.positioning =
                NativeDevicePositioningRecipe::capture(shaper, glyphs, self.projection)?;
        }
        if capture && self.positioning.is_none() && prepared_identity(glyphs) {
            self.boundaries = copy_vec(glyphs)?;
        }
        if let Some(stage) = &shaper.gpos {
            stage.position_with_device(
                &shaper.features,
                glyphs,
                attachments,
                GposOptions {
                    gdef,
                    direction,
                    font: Some(&shaper.font),
                    normalized_coordinates: shaper.normalized_coordinates(),
                    ..Default::default()
                },
                Some(frame.gpos()),
            )?;
        }
        if !shaper.gpos_kerning
            && shaper.features.run_enabled(KERN)
            && shaper.route.complex != NativeComplexKind::Indic
            && matches!(
                direction,
                GposDirection::LeftToRight | GposDirection::RightToLeft
            )
        {
            apply_native_device_legacy_kern(&shaper.font, glyphs, gdef, frame.gpos())?;
        }
        let late_zero = shaper.zero_marks
            && (shaper.fallback_mark_positioning()
                || [*b"thai", *b"lao "].contains(&shaper.route.unicode_script.bytes()));
        if late_zero {
            for g in glyphs.iter_mut() {
                if positioning_mark(*g, gdef) {
                    zero_mark(g, adjust)?;
                }
            }
        }
        if shaper
            .gpos
            .as_ref()
            .is_some_and(|s| s.selection.table().view().lookup_count() != 0)
        {
            resolve_gpos_attachments(glyphs, attachments, direction, states, true)?;
        }
        if shaper.fallback_mark_positioning() {
            apply_native_device_fallback_marks(
                &shaper.font,
                shaper.normalized_coordinates(),
                glyphs,
                direction,
                attachments,
                &self.mapping[..count],
                frame,
            )?;
        }
        if matches!(
            direction,
            GposDirection::RightToLeft | GposDirection::BottomToTop
        ) {
            glyphs.reverse();
            self.mapping[..count].reverse();
            if shaper.level == NativeClusterLevel::MonotoneCharacters {
                reverse_mark_clusters(glyphs);
            }
        }
        if shaper.route.arabic_joining {
            let mut live = b.count as u32;
            let result = apply_native_device_arabic_stretch(
                &shaper.font,
                shaper.normalized_coordinates(),
                b.storage,
                &mut live,
                direction == GposDirection::RightToLeft,
                &mut self.mapping,
                &mut self.stretch,
                frame,
            );
            b.count = live as usize;
            joining::clear(b.glyphs_mut());
            result?;
        }
        Ok(())
    }
    fn valid_final(&self, glyphs: &[ShapingGlyph]) -> bool {
        let Some(batch) = &self.batch else {
            return false;
        };
        glyphs.len() <= self.mapping.len()
            && glyphs.iter().zip(&self.mapping).all(|(g, &descriptor)| {
                (descriptor as usize) < self.source_count
                    && batch
                        .glyphs()
                        .get(descriptor as usize)
                        .is_some_and(|original| original.glyph_id == g.glyph_id)
            })
    }
    fn validate_boundaries(&mut self, glyphs: &[ShapingGlyph]) -> Result<(), FontError> {
        if self.boundaries.is_empty() {
            return Ok(());
        }
        let mut valid = self.boundaries.len() == glyphs.len() && glyphs.len() == self.source_count;
        let mut seen = filled(self.source_count, false)?;
        if valid {
            for (after, &index) in glyphs.iter().zip(&self.mapping) {
                let index = index as usize;
                let Some(before) = self.boundaries.get(index) else {
                    valid = false;
                    break;
                };
                if seen[index]
                    || before.glyph_id != after.glyph_id
                    || before.code_point != after.code_point
                    || before.cluster != after.cluster
                    || before.flags & !after.flags != 0
                {
                    valid = false;
                    break;
                }
                seen[index] = true;
            }
        }
        if !valid {
            self.boundaries.clear();
        }
        Ok(())
    }
}
fn append_auxiliary(
    font: &SfntFace,
    glyphs: &[ShapingGlyph],
    cp: u32,
    ids: &mut Vec<u32>,
) -> Result<(usize, usize), FontError> {
    let start = ids.len();
    if !glyphs.iter().any(|g| g.code_point == cp) {
        return Ok((start, 0));
    }
    let original = font.glyph_index(cp).ok_or(FontError::InvalidFace)?;
    let space = font.glyph_index(0x20).ok_or(FontError::InvalidFace)?;
    if original != 0 || space == 0 {
        return Ok((start, 0));
    }
    let mut selected = 0;
    if cp == 0x2007 {
        for digit in 0x30..=0x39 {
            selected = font.glyph_index(digit).ok_or(FontError::InvalidFace)?;
            if selected != 0 {
                break;
            }
        }
    } else {
        selected = font.glyph_index(0x2e).ok_or(FontError::InvalidFace)?;
        if selected == 0 {
            selected = font.glyph_index(0x2c).ok_or(FontError::InvalidFace)?;
        }
    }
    if selected == 0 {
        Ok((start, 0))
    } else {
        ids.push(u32::from(selected));
        Ok((start, 1))
    }
}
fn positioning_mark(glyph: ShapingGlyph, gdef: Option<Gdef<'_>>) -> bool {
    let class = gdef.map_or(GlyphClass::Unclassified, |d| {
        d.glyph_class(glyph.glyph_id as u16)
    });
    if class != GlyphClass::Unclassified {
        class == GlyphClass::Mark
    } else {
        !input::default_ignorable(glyph.code_point) && unicode_mark(glyph.code_point)
    }
}
fn zero_mark(g: &mut ShapingGlyph, adjust: bool) -> Result<(), FontError> {
    if adjust {
        let x = i32::try_from(i64::from(g.offset_x) - i64::from(g.advance_x))
            .map_err(|_| FontError::InvalidFace)?;
        let y = i32::try_from(i64::from(g.offset_y) - i64::from(g.advance_y))
            .map_err(|_| FontError::InvalidFace)?;
        g.offset_x = x;
        g.offset_y = y;
    }
    g.advance_x = 0;
    g.advance_y = 0;
    Ok(())
}
fn reverse_mark_clusters(glyphs: &mut [ShapingGlyph]) {
    let mut start = 0;
    while start < glyphs.len() {
        while start < glyphs.len() && input::modified_class(glyphs[start].code_point) == 0 {
            start += 1;
        }
        let mut end = start;
        while end < glyphs.len() && input::modified_class(glyphs[end].code_point) != 0 {
            end += 1;
        }
        let mut left = start;
        let mut right = end.saturating_sub(1);
        while left < right {
            let value = glyphs[left].cluster;
            glyphs[left].cluster = glyphs[right].cluster;
            glyphs[right].cluster = value;
            left += 1;
            right -= 1;
        }
        start = end;
    }
}
fn prepared_identity(glyphs: &[ShapingGlyph]) -> bool {
    glyphs
        .iter()
        .all(|g| g.glyph_id <= u32::from(u16::MAX) && g.cluster >= 0)
        && glyphs.windows(2).all(|g| g[0].cluster <= g[1].cluster)
}
impl NativeShaper {
    /// Original owned-device capacity including bounded 257-fold stch expansion
    /// only when this font actually selects a native stch lookup. Device fallback
    /// uses actual captured bounds and allocates no unhinted outline cache.
    pub fn device_requirements(
        &self,
        input: &[UnicodeScalar],
    ) -> Result<NativeShapeRequirements, FontError> {
        let mut required = self.requirements(input)?;
        if required.glyph_capacity > MAXIMUM_GLYPHS {
            return Err(FontError::Capacity);
        }
        if self.route.arabic_joining
            && self.features.run_enabled(Tag::new(*b"stch"))
            && let Some(stage) = &self.gsub
        {
            let mut lookups = filled(required.lookup_capacity, 0)?;
            if stage.selection.table().view().select_feature_into(
                self.route.layout_script,
                self.language,
                Tag::new(*b"stch"),
                self.normalized_coordinates(),
                &mut lookups,
            )? != 0
            {
                required.glyph_capacity = (required.glyph_capacity * 257).min(MAXIMUM_GLYPHS);
            }
        }
        required.bounds_capacity = 0;
        if required.verification_glyph_capacity != 0 {
            required.verification_glyph_capacity = required.glyph_capacity;
        }
        Ok(required)
    }
    /// Shape one complete actual device generation and publish only after all
    /// capture, descriptor, fragment and supported recipe checks succeed.
    /// The caller must supply an actual hinted producer; none is inferred.
    pub fn shape_device(
        &self,
        source: &[UnicodeScalar],
        request: &NativeHintedRequest,
        projection: NativeHintedProjection,
        provider: &mut dyn NativeHintedProvider,
    ) -> Result<Arc<NativeDeviceRun>, NativeDeviceError> {
        let requirements = self.device_requirements(source)?;
        let mut scratch = NativeShapeScratch::new(requirements)?;
        self.shape_device_with_scratch(source, request, projection, provider, &mut scratch)
    }
    /// Use explicit reusable caller scratch while publishing an independent owned
    /// run. Original live/partial scratch state remains inspectable on failure;
    /// no candidate or fragment capture escapes an unsuccessful operation.
    pub fn shape_device_with_scratch(
        &self,
        source: &[UnicodeScalar],
        request: &NativeHintedRequest,
        projection: NativeHintedProjection,
        provider: &mut dyn NativeHintedProvider,
        scratch: &mut NativeShapeScratch,
    ) -> Result<Arc<NativeDeviceRun>, NativeDeviceError> {
        scratch.glyph_count = 0;
        request.validate()?;
        let required = self.device_requirements(source)?;
        let capacity = scratch.glyphs.len();
        if capacity < required.glyph_capacity
            || capacity > MAXIMUM_GLYPHS
            || required.verification_glyph_capacity != 0
                && scratch.verification_glyphs.len() < capacity
        {
            return Err(FontError::Capacity.into());
        }
        // Freeze all external request/source inputs before the first provider callback.
        let request = request.try_copy()?;
        let input = copy_vec(source)?;
        let coordinates = copy_vec(self.normalized_coordinates())?;
        let mut owner = DeviceOwner::new(provider, &request, projection, capacity, true)?;
        if let Err(error) = self.shape_core(
            &input,
            scratch,
            self.flags,
            &self.pre_context,
            &self.post_context,
            capacity,
            Some(&mut owner),
        ) {
            return Err(owner.error(error));
        }
        if required.verification_glyph_capacity != 0 {
            let count = scratch.glyph_count;
            let expected = std::mem::replace(
                &mut scratch.glyphs,
                std::mem::take(&mut scratch.verification_glyphs),
            );
            let result = self.verify_device(&input, &expected[..count], scratch, &mut owner);
            scratch.verification_glyphs = std::mem::replace(&mut scratch.glyphs, expected);
            scratch.glyph_count = if result.is_ok() { count } else { 0 };
            if let Err(error) = result {
                return Err(owner.error(error));
            }
        }
        let glyphs = scratch.glyphs();
        if !owner.valid_final(glyphs) {
            return Err(FontError::InvalidArgument.into());
        }
        owner.validate_boundaries(glyphs)?;
        let run = Arc::new(NativeDeviceRun {
            batch: owner.batch.take().ok_or(FontError::InvalidArgument)?,
            source: input,
            coordinates,
            glyphs: copy_vec(glyphs)?,
            mapping: copy_vec(&owner.mapping[..glyphs.len()])?,
            source_count: owner.source_count,
            auxiliary: owner.auxiliary,
            direction: self.route.direction,
            projection,
            positioning: owner.positioning.take(),
            boundaries: std::mem::take(&mut owner.boundaries),
        });
        if run.positioning.is_some() && !run.glyphs.is_empty() {
            run.recompose_positioning(0, run.glyphs.len() as u32)?;
        }
        Ok(run)
    }
}
impl NativeDeviceFragmentSource for DeviceOwner<'_> {
    fn shape_fragment(
        &mut self,
        shaper: &NativeShaper,
        input: &[UnicodeScalar],
        context: NativeFragmentContext<'_>,
        output: &mut [ShapingGlyph],
    ) -> Result<usize, FontError> {
        let mut required = shaper.requirements(input)?;
        required.bounds_capacity = 0;
        required.verification_glyph_capacity = 0;
        let mut scratch = NativeShapeScratch::with_glyph_capacity(required, output.len())?;
        let mut child = DeviceOwner::new(
            self.provider,
            self.request,
            self.projection,
            output.len(),
            false,
        )?;
        let result = shaper.shape_core(
            input,
            &mut scratch,
            context.flags,
            context.pre_context,
            context.post_context,
            output.len(),
            Some(&mut child),
        );
        if let Err(error) = result {
            self.capture_failure = child.capture_failure;
            self.projection_failure.set(child.projection_failure.get());
            return Err(error);
        }
        if !child.valid_final(scratch.glyphs()) {
            return Err(FontError::InvalidArgument);
        }
        let count = scratch.glyph_count;
        output[..count].copy_from_slice(scratch.glyphs());
        // The entire child generation retires before the caller publishes output.
        drop(child);
        Ok(count)
    }
}
