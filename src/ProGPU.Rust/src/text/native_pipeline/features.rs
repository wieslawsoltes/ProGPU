//! Original native request normalization, script/directional feature planning,
//! and half-open input value resolution. This does not choose a script route.
use super::{FRACTIONS, FontError, GposDirection, Tag, copy_vec, push};

/// Original native feature value and half-open source-input range.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct NativeFeature {
    /// Exact four-byte OpenType tag.
    pub tag: Tag,
    /// Original unsigned alternate/enable value.
    pub value: u32,
    /// Inclusive original UTF-16 input offset.
    pub start: u32,
    /// Exclusive input offset; `u32::MAX` denotes the original whole-run end.
    pub end: u32,
}
impl NativeFeature {
    /// A complete original run setting.
    pub const fn global(tag: Tag, value: u32) -> Self {
        Self {
            tag,
            value,
            start: 0,
            end: u32::MAX,
        }
    }
    fn is_global(self) -> bool {
        self.start == 0 && self.end == u32::MAX
    }
}
/// Native default-overridden baseline entry, prior to script planning.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct NativeFeatureSetting {
    /// Original feature tag.
    pub tag: Tag,
    /// Original complete-run baseline value.
    pub value: u32,
}
/// Original script feature-policy inputs, independent of font script discovery.
/// The booleans retain the native route's separate script decisions.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct NativeFeatureRoute {
    /// Actual selected layout script, including second/third generation tags.
    pub layout_script: Tag,
    /// Original requested direction after route resolution.
    pub direction: GposDirection,
    /// Original Universal Shaping Engine feature policy.
    pub use_shaper: bool,
    /// Original Indic feature policy.
    pub indic_shaper: bool,
    /// Original Arabic feature policy.
    pub arabic_shaper: bool,
}
impl NativeFeatureRoute {
    /// Original simple Latin feature policy, without inferring Unicode scripts.
    pub const fn latin() -> Self {
        Self {
            layout_script: Tag::new(*b"latn"),
            direction: GposDirection::LeftToRight,
            use_shaper: false,
            indic_shaper: false,
            arabic_shaper: false,
        }
    }
    fn vertical(self) -> bool {
        matches!(
            self.direction,
            GposDirection::TopToBottom | GposDirection::BottomToTop
        )
    }
    fn policy(self) -> bool {
        [
            Tag::new(*b"khmr"),
            Tag::new(*b"hang"),
            Tag::new(*b"mymr"),
            Tag::new(*b"mym2"),
        ]
        .contains(&self.layout_script)
            || self.use_shaper
            || self.indic_shaper
            || self.arabic_shaper
    }
    fn script_features(self) -> &'static [Tag] {
        if self.layout_script == Tag::new(*b"khmr") {
            KHMER
        } else if [Tag::new(*b"mymr"), Tag::new(*b"mym2")].contains(&self.layout_script) {
            MYANMAR
        } else if self.layout_script == Tag::new(*b"hang") {
            HANGUL
        } else if self.indic_shaper {
            INDIC
        } else {
            USE
        }
    }
    fn order(self) -> &'static [Tag] {
        if self.layout_script == Tag::new(*b"khmr") {
            KHMER_ORDER
        } else if self.arabic_shaper {
            ARABIC_ORDER
        } else {
            COMMON_ORDER
        }
    }
    fn directional(self) -> &'static [Tag] {
        if self.vertical() {
            VERTICAL
        } else if self.direction == GposDirection::RightToLeft {
            RTL
        } else {
            LTR
        }
    }
}

/// Immutable original native request/configuration result. Construction preserves
/// duplicate baseline tags, first explicit-tag order, full-run overrides, ranges,
/// script ordering and the source's independent zero-valued feature inventory.
#[derive(Clone, Debug)]
pub struct NativeFeaturePlan {
    route: NativeFeatureRoute,
    base: Vec<NativeFeatureSetting>,
    explicit: Vec<Tag>,
    requested: Vec<Tag>,
    settings: Vec<NativeFeature>,
}
impl NativeFeaturePlan {
    /// Normalize public native requests and apply the original script policy.
    /// Global unsigned values clamp to `i32::MAX`; ranged values remain exact.
    pub fn new(route: NativeFeatureRoute, features: &[NativeFeature]) -> Result<Self, FontError> {
        if features.len() > u32::MAX as usize || features.iter().any(|f| f.start > f.end) {
            return Err(FontError::InvalidArgument);
        }
        let mut base = copy_vec(native_default_features())?;
        let mut explicit = Vec::new();
        for feature in features.iter().filter(|f| f.is_global()) {
            let setting = NativeFeatureSetting {
                tag: feature.tag,
                value: feature.value.min(i32::MAX as u32),
            };
            if let Some(old) = base.iter_mut().find(|f| f.tag == feature.tag) {
                *old = setting;
            } else {
                push(&mut base, setting)?;
            }
        }
        for feature in features {
            if feature.value != 0 && !base.iter().any(|f| f.tag == feature.tag && f.value != 0) {
                push(
                    &mut base,
                    NativeFeatureSetting {
                        tag: feature.tag,
                        value: 1,
                    },
                )?;
            }
            if !explicit.contains(&feature.tag) {
                push(&mut explicit, feature.tag)?;
            }
        }
        let mut plan = Self::from_baseline(route, &base, &explicit)?;
        for &range in features.iter().filter(|f| !f.is_global()) {
            if plan.requested.contains(&range.tag) {
                push(&mut plan.settings, range)?;
            }
        }
        Ok(plan)
    }
    /// Original lower-level feature-plan boundary, accepting a caller baseline.
    /// It does not inject request defaults or repair a caller's duplicate values.
    pub fn from_baseline(
        route: NativeFeatureRoute,
        base: &[NativeFeatureSetting],
        explicit: &[Tag],
    ) -> Result<Self, FontError> {
        if base.len() > u32::MAX as usize {
            return Err(FontError::InvalidArgument);
        }
        let mut requested = Vec::new();
        if !route.policy() {
            for &tag in route.directional() {
                if !base.iter().any(|f| f.tag == tag) {
                    push(&mut requested, tag)?;
                }
            }
            for feature in base {
                if !(route.vertical() && feature.tag == super::KERN) {
                    push(&mut requested, feature.tag)?;
                }
            }
        } else {
            let mut dictionary: Vec<NativeFeatureSetting> = Vec::new();
            let put =
                |dictionary: &mut Vec<NativeFeatureSetting>, tag, value| -> Result<(), FontError> {
                    if let Some(old) = dictionary.iter_mut().find(|f| f.tag == tag) {
                        old.value = value;
                        Ok(())
                    } else {
                        push(dictionary, NativeFeatureSetting { tag, value })
                    }
                };
            for feature in base {
                put(&mut dictionary, feature.tag, feature.value)?;
            }
            for &tag in route.script_features() {
                if !base.iter().any(|f| f.tag == tag) {
                    put(&mut dictionary, tag, 1)?;
                }
            }
            if route.layout_script == Tag::new(*b"khmr") {
                if !base.iter().any(|f| f.tag == Tag::new(*b"clig")) {
                    put(&mut dictionary, Tag::new(*b"clig"), 1)?;
                }
                if !explicit.contains(&Tag::new(*b"liga")) {
                    put(&mut dictionary, Tag::new(*b"liga"), 0)?;
                }
            } else if route.indic_shaper {
                put(&mut dictionary, Tag::new(*b"liga"), 0)?;
            } else if route.arabic_shaper {
                for tag in [Tag::new(*b"stch"), Tag::new(*b"mset")] {
                    if !base.iter().any(|f| f.tag == tag) {
                        put(&mut dictionary, tag, 1)?;
                    }
                }
            }
            for &tag in route.directional() {
                if !dictionary.iter().any(|f| f.tag == tag) {
                    push(&mut requested, tag)?;
                }
            }
            for tag in route
                .order()
                .iter()
                .copied()
                .chain(base.iter().map(|f| f.tag))
                .chain(dictionary.iter().map(|f| f.tag))
            {
                if dictionary.iter().any(|f| f.tag == tag) && !requested.contains(&tag) {
                    push(&mut requested, tag)?;
                }
            }
            if route.vertical() {
                requested.retain(|&tag| tag != super::KERN);
            }
        }
        let mut settings = Vec::new();
        let filtered_base_count = base
            .iter()
            .filter(|f| !(route.vertical() && f.tag == super::KERN))
            .count();
        let inserted = requested.len().saturating_sub(filtered_base_count);
        let mut original = base
            .iter()
            .filter(|f| !(route.vertical() && f.tag == super::KERN));
        for (index, &tag) in requested.iter().enumerate() {
            let mut value = if route.policy() {
                base.iter()
                    .rev()
                    .find(|f| f.tag == tag)
                    .map_or(1, |f| f.value)
            } else if index >= inserted {
                original.next().map_or(1, |f| f.value)
            } else {
                1
            };
            if tag == Tag::new(*b"liga")
                && (route.indic_shaper
                    || route.layout_script == Tag::new(*b"khmr") && !explicit.contains(&tag))
            {
                value = 0;
            }
            if value != 1 {
                push(&mut settings, NativeFeature::global(tag, value))?;
            }
        }
        Ok(Self {
            route,
            base: copy_vec(base)?,
            explicit: copy_vec(explicit)?,
            requested,
            settings,
        })
    }
    /// Exact route used to construct this immutable plan.
    pub fn route(&self) -> NativeFeatureRoute {
        self.route
    }
    /// Complete default-overridden baseline, including source duplicates.
    pub fn baseline(&self) -> &[NativeFeatureSetting] {
        &self.base
    }
    /// Explicit tags in their original first-occurrence order.
    pub fn explicit(&self) -> &[Tag] {
        &self.explicit
    }
    /// Original ordered lookup selection inventory, including disabled tags.
    pub fn requested(&self) -> &[Tag] {
        &self.requested
    }
    /// Nondefault global settings followed by active original partial ranges.
    pub fn settings(&self) -> &[NativeFeature] {
        &self.settings
    }
    /// Whole-run enablement applies global overrides only, in original order.
    pub fn run_enabled(&self, tag: Tag) -> bool {
        self.settings
            .iter()
            .filter(|f| f.tag == tag && f.is_global())
            .fold(self.requested.contains(&tag), |_, f| f.value != 0)
    }
    /// Original native value resolution. Any record for a tag sets its initial
    /// value to zero; matching later records overwrite it, including zero ranges.
    pub fn value(&self, tag: Tag, cluster: i32) -> u32 {
        let mut value = if self.has_settings(tag) {
            0
        } else {
            u32::from(self.requested.contains(&tag))
        };
        let index = cluster.max(0) as u32;
        for feature in &self.settings {
            if feature.tag == tag && feature.start <= index && index < feature.end {
                value = feature.value;
            }
        }
        value
    }
    pub(super) fn has_settings(&self, tag: Tag) -> bool {
        self.settings.iter().any(|f| f.tag == tag)
    }
    pub(super) fn excluded_fractions(&self) -> impl Iterator<Item = Tag> + '_ {
        FRACTIONS
            .into_iter()
            .filter(|tag| !self.explicit.contains(tag))
    }
}

const fn tags<const N: usize>(bytes: [[u8; 4]; N]) -> [Tag; N] {
    let mut result = [Tag(0); N];
    let mut i = 0;
    while i < N {
        result[i] = Tag::new(bytes[i]);
        i += 1;
    }
    result
}
const DEFAULT_TAGS: [Tag; 26] = tags([
    *b"rvrn", *b"frac", *b"numr", *b"dnom", *b"ccmp", *b"locl", *b"isol", *b"fina", *b"fin2",
    *b"fin3", *b"medi", *b"med2", *b"init", *b"rlig", *b"mark", *b"mkmk", *b"calt", *b"clig",
    *b"curs", *b"dist", *b"abvm", *b"blwm", *b"kern", *b"liga", *b"rclt", *b"rand",
]);
const DEFAULTS: [NativeFeatureSetting; 26] = {
    let mut result = [NativeFeatureSetting {
        tag: Tag(0),
        value: 1,
    }; 26];
    let mut i = 0;
    while i < 26 {
        result[i].tag = DEFAULT_TAGS[i];
        i += 1;
    }
    result[25].value = 65535;
    result
};
/// Exact original native baseline, including `rand = 65535` and conditional
/// fraction tags. Exclusions are applied separately during lookup selection.
pub fn native_default_features() -> &'static [NativeFeatureSetting] {
    &DEFAULTS
}
const KHMER: &[Tag] = &tags([
    *b"pref", *b"blwf", *b"abvf", *b"pstf", *b"cfar", *b"pres", *b"abvs", *b"blws", *b"psts",
]);
const MYANMAR: &[Tag] = &tags([
    *b"rphf", *b"pref", *b"blwf", *b"pstf", *b"pres", *b"abvs", *b"blws", *b"psts",
]);
const HANGUL: &[Tag] = &tags([*b"ljmo", *b"vjmo", *b"tjmo"]);
const INDIC: &[Tag] = &tags([
    *b"nukt", *b"akhn", *b"rphf", *b"rkrf", *b"pref", *b"blwf", *b"abvf", *b"half", *b"pstf",
    *b"vatu", *b"cjct", *b"init", *b"pres", *b"abvs", *b"blws", *b"psts", *b"haln",
]);
const USE: &[Tag] = &tags([
    *b"nukt", *b"akhn", *b"rphf", *b"pref", *b"rkrf", *b"abvf", *b"blwf", *b"half", *b"pstf",
    *b"vatu", *b"cjct", *b"isol", *b"init", *b"medi", *b"fina", *b"abvs", *b"blws", *b"haln",
    *b"pres", *b"psts",
]);
const KHMER_ORDER: &[Tag] = &tags([
    *b"rvrn", *b"frac", *b"numr", *b"dnom", *b"locl", *b"ccmp", *b"pref", *b"blwf", *b"abvf",
    *b"pstf", *b"cfar", *b"pres", *b"abvs", *b"blws", *b"psts",
]);
const ARABIC_ORDER: &[Tag] = &tags([
    *b"rvrn", *b"frac", *b"numr", *b"dnom", *b"stch", *b"ccmp", *b"locl", *b"isol", *b"fina",
    *b"fin2", *b"fin3", *b"medi", *b"med2", *b"init", *b"rlig", *b"calt", *b"rclt", *b"liga",
    *b"clig", *b"mset",
]);
const COMMON_ORDER: &[Tag] = &tags([
    *b"rvrn", *b"frac", *b"numr", *b"dnom", *b"locl", *b"ccmp", *b"nukt", *b"akhn", *b"rphf",
    *b"pref", *b"rkrf", *b"abvf", *b"blwf", *b"half", *b"pstf", *b"vatu", *b"cjct", *b"isol",
    *b"init", *b"medi", *b"fina", *b"abvs", *b"blws", *b"haln", *b"pres", *b"psts", *b"ljmo",
    *b"vjmo", *b"tjmo",
]);
const VERTICAL: &[Tag] = &tags([*b"vert", *b"vrt2", *b"vkrn"]);
const RTL: &[Tag] = &tags([*b"rtla", *b"rtlm"]);
const LTR: &[Tag] = &tags([*b"ltra", *b"ltrm"]);
