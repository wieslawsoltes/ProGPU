//! Frozen original per-cluster feature values and script-record admission.
use super::{FontError, ManagedFeatureSetting};
use crate::text::ManagedShapingGlyph;
mod planner;
/// Original half-open UTF input feature range, with an independent unsigned value.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct ManagedFeatureRange {
    tag: [u8; 4],
    value: u32,
    start: u32,
    end: u32,
}
impl ManagedFeatureRange {
    /// Preserve the original byte tag and range; reversed ranges reject atomically.
    pub fn new(tag: [u8; 4], value: u32, start: u32, end: u32) -> Result<Self, FontError> {
        if start > end {
            return Err(FontError::InvalidArgument);
        }
        Ok(Self {
            tag,
            value,
            start,
            end,
        })
    }
    /// Original tag bytes, decoded to four Latin-1 code units for matching.
    pub fn tag(self) -> [u8; 4] {
        self.tag
    }
    /// Original unsigned value, narrowed only during feature-value resolution.
    pub fn value(self) -> u32 {
        self.value
    }
    /// Original inclusive start and exclusive end.
    pub fn range(self) -> std::ops::Range<u32> {
        self.start..self.end
    }
    fn matches(self, tag: [u16; 4]) -> bool {
        self.tag.map(u16::from) == tag
    }
    fn applies(self, position: u32) -> bool {
        position >= self.start && position < self.end
    }
}
/// Immutable current managed feature options. Null and empty base/explicit
/// inventories remain distinct. Script, language and coordinates belong to the
/// owning lookup plan; this value contains the original per-cluster policy only.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct ManagedFeatureOptions {
    features: Vec<ManagedFeatureSetting>,
    baseline: Option<Vec<ManagedFeatureSetting>>,
    explicit: Option<Vec<[u16; 4]>>,
    ranges: Vec<ManagedFeatureRange>,
    source_defaults: bool,
}
fn copy<T: Copy>(source: &[T]) -> Result<Vec<T>, FontError> {
    let mut out = Vec::new();
    out.try_reserve_exact(source.len())
        .map_err(|_| FontError::Capacity)?;
    out.extend_from_slice(source);
    Ok(out)
}
impl Default for ManagedFeatureOptions {
    fn default() -> Self {
        let tags = [
            *b"rvrn", *b"frac", *b"numr", *b"dnom", *b"ccmp", *b"locl", *b"isol", *b"fina",
            *b"fin2", *b"fin3", *b"medi", *b"med2", *b"init", *b"rlig", *b"mark", *b"mkmk",
            *b"calt", *b"clig", *b"curs", *b"dist", *b"abvm", *b"blwm", *b"kern", *b"liga",
            *b"rclt", *b"rand",
        ];
        Self {
            features: tags
                .into_iter()
                .map(|tag| {
                    ManagedFeatureSetting::new(
                        tag.map(u16::from),
                        if tag == *b"rand" { 65535 } else { 1 },
                    )
                    .unwrap()
                })
                .collect(),
            baseline: None,
            explicit: None,
            ranges: vec![],
            source_defaults: true,
        }
    }
}
impl ManagedFeatureOptions {
    /// Freeze original ordered settings, optional baseline, explicit set and ranges.
    pub fn new(
        features: &[ManagedFeatureSetting],
        baseline: Option<&[ManagedFeatureSetting]>,
        explicit: Option<&[[u16; 4]]>,
        ranges: &[ManagedFeatureRange],
    ) -> Result<Self, FontError> {
        Ok(Self {
            features: copy(features)?,
            baseline: baseline.map(copy).transpose()?,
            explicit: explicit.map(copy).transpose()?,
            ranges: copy(ranges)?,
            source_defaults: false,
        })
    }
    /// Original WithFeatures defaults plus last-wins overrides and explicit tags.
    pub fn with_overrides(overrides: &[ManagedFeatureSetting]) -> Result<Self, FontError> {
        let mut out = Self::default();
        let mut tags = Vec::new();
        tags.try_reserve_exact(overrides.len())
            .map_err(|_| FontError::Capacity)?;
        for &setting in overrides {
            if let Some(old) = out.features.iter_mut().find(|v| v.tag() == setting.tag()) {
                *old = setting;
            } else {
                out.features
                    .try_reserve(1)
                    .map_err(|_| FontError::Capacity)?;
                out.features.push(setting);
            }
            if !tags.contains(&setting.tag()) {
                tags.push(setting.tag());
            }
        }
        out.explicit = Some(tags);
        out.source_defaults = false;
        Ok(out)
    }
    /// Complete ordered feature inventory used by plan selection.
    pub fn features(&self) -> &[ManagedFeatureSetting] {
        &self.features
    }
    /// Original optional per-position baseline.
    pub fn baseline(&self) -> Option<&[ManagedFeatureSetting]> {
        self.baseline.as_deref()
    }
    /// Original optional explicit set; null does not enable IsFeatureExplicitAt.
    pub fn explicit_tags(&self) -> Option<&[[u16; 4]]> {
        self.explicit.as_deref()
    }
    /// Original ordered input ranges.
    pub fn ranges(&self) -> &[ManagedFeatureRange] {
        &self.ranges
    }
    /// Whether ResolveExplicitFeatureTags includes this tag, retaining the source
    /// distinction between default-list identity and a custom list with null tags.
    pub fn resolves_explicit(&self, tag: [u16; 4]) -> bool {
        self.explicit.as_ref().map_or_else(
            || !self.source_defaults && self.features.iter().any(|f| f.tag() == tag),
            |tags| tags.contains(&tag),
        )
    }
    /// Resolve the original value at a signed cluster. Negative clusters use zero;
    /// last applicable ranges win and unsigned values clamp to i32::MAX.
    pub fn value_at(&self, tag: [u16; 4], cluster: i32) -> i32 {
        let baseline = self.baseline.as_deref().unwrap_or(&self.features);
        let found = baseline.iter().rev().find(|f| f.tag() == tag);
        let mut value = found.map_or(0, |f| f.value());
        if found.is_none() && !self.ranges.iter().any(|r| r.matches(tag)) {
            value = self
                .features
                .iter()
                .rev()
                .find(|f| f.tag() == tag)
                .map_or(0, |f| f.value());
        }
        let position = cluster.max(0) as u32;
        for &range in &self.ranges {
            if range.matches(tag) && range.applies(position) {
                value = range.value.min(i32::MAX as u32) as i32;
            }
        }
        value
    }
    /// Original explicit admission: the tag must be in the explicit set, and any
    /// range for that tag restricts explicitness to the union of its input ranges.
    pub fn explicit_at(&self, tag: [u16; 4], cluster: i32) -> bool {
        if !self
            .explicit
            .as_ref()
            .is_some_and(|tags| tags.contains(&tag))
        {
            return false;
        }
        let mut has_range = false;
        let position = cluster.max(0) as u32;
        for &range in &self.ranges {
            if range.matches(tag) {
                has_range = true;
                if range.applies(position) {
                    return true;
                }
            }
        }
        !has_range
    }
    /// Original per-record feature admission; only zero disables the first test.
    /// Independent Hangul/Indic branches precede Arabic/fraction/Khmer/USE policy.
    pub fn enabled(&self, glyph: ManagedShapingGlyph, tag: [u16; 4]) -> bool {
        if self.value_at(tag, glyph.cluster) == 0 {
            return false;
        }
        let key = |s: &[u8; 4]| tag == s.map(u16::from);
        let mask = |v: u8| glyph.script_feature_mask & v != 0;
        if glyph.script_shaper == 4 {
            if key(b"calt") {
                return self.explicit_at(tag, glyph.cluster);
            }
            return if key(b"ljmo") {
                mask(1)
            } else if key(b"vjmo") {
                mask(2)
            } else if key(b"tjmo") {
                mask(4)
            } else {
                true
            };
        }
        if glyph.script_shaper == 3 {
            for (feature, bit) in [
                (b"rphf", 1),
                (b"pref", 2),
                (b"blwf", 4),
                (b"abvf", 8),
                (b"half", 16),
                (b"pstf", 32),
                (b"init", 64),
            ] {
                if key(feature) {
                    return mask(bit);
                }
            }
            return true;
        }
        if key(b"frac") {
            return self.explicit_at(tag, glyph.cluster) || glyph.fraction_action != 0;
        }
        if key(b"numr") {
            return self.explicit_at(tag, glyph.cluster) || glyph.fraction_action == 1;
        }
        if key(b"dnom") {
            return self.explicit_at(tag, glyph.cluster) || glyph.fraction_action == 2;
        }
        if key(b"pref") {
            return glyph.script_shaper != 1 || mask(1);
        }
        if key(b"blwf") || key(b"abvf") || key(b"pstf") {
            return glyph.script_shaper != 1 || mask(2);
        }
        if key(b"cfar") {
            return glyph.script_shaper == 1 && mask(4);
        }
        if key(b"rphf") {
            return glyph.script_shaper != 2 || glyph.use_rphf_eligible != 0;
        }
        for (feature, action) in [
            (b"isol", 0),
            (b"fina", 1),
            (b"fin2", 2),
            (b"fin3", 3),
            (b"medi", 4),
            (b"med2", 5),
            (b"init", 6),
        ] {
            if key(feature) {
                return glyph.arabic_action == action;
            }
        }
        true
    }
    pub(super) fn may_enable(&self, tag: [u16; 4], fractions: bool) -> bool {
        ![b"frac", b"numr", b"dnom"]
            .iter()
            .any(|t| tag == t.map(u16::from))
            || fractions
            || self
                .explicit
                .as_ref()
                .is_some_and(|tags| tags.contains(&tag))
    }
}
