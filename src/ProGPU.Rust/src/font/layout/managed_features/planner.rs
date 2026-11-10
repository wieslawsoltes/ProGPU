//! Original AddScriptFeatures / AddDirectionalFeatures from OpenTypeTextShaper.
//! Ordered values use an index map: expected O(F), retaining first insertion
//! order, final values, explicit identity and independent range/baseline owners.
use super::*;
use crate::text::script_actions::ManagedShapingDirection;
use std::collections::{HashMap, HashSet};

impl ManagedFeatureOptions {
    /// Freeze the original script feature order followed by direction policy.
    /// Script is the selected layout tag, not the original Unicode script.
    pub fn for_shaping(
        &self,
        script: &str,
        use_shaper: bool,
        indic_shaper: bool,
        direction: ManagedShapingDirection,
    ) -> Result<Self, FontError> {
        let arabic = crate::text::managed_pipeline::uses_arabic_joining(script);
        let mut out = self.clone();
        // Resolve before adding implicit features. Default-list identity matters.
        let mut explicit = self.explicit.clone().unwrap_or_default();
        if self.explicit.is_none() && !self.source_defaults {
            let mut seen = HashSet::new();
            seen.try_reserve(self.features.len())
                .map_err(|_| FontError::Capacity)?;
            explicit
                .try_reserve(self.features.len())
                .map_err(|_| FontError::Capacity)?;
            for f in &self.features {
                if seen.insert(f.tag()) {
                    explicit.push(f.tag());
                }
            }
        }
        if matches!(script, "khmr" | "hang" | "mymr" | "mym2")
            || use_shaper
            || indic_shaper
            || arabic
        {
            let mut indices = HashMap::new();
            indices
                .try_reserve(self.features.len() + 32)
                .map_err(|_| FontError::Capacity)?;
            let mut values: Vec<Option<ManagedFeatureSetting>> = Vec::new();
            values
                .try_reserve(self.features.len() + 32)
                .map_err(|_| FontError::Capacity)?;
            let mut put = |tag: [u16; 4], value: i32, replace: bool| -> Result<(), FontError> {
                if let Some(&index) = indices.get(&tag) {
                    if replace {
                        values[index] = Some(ManagedFeatureSetting::new(tag, value)?);
                    }
                } else {
                    indices.insert(tag, values.len());
                    values.push(Some(ManagedFeatureSetting::new(tag, value)?));
                }
                Ok(())
            };
            for f in &self.features {
                put(f.tag(), f.value(), true)?;
            }
            let additions: &[[u8; 4]] = match script {
                "khmr" => &[
                    *b"pref", *b"blwf", *b"abvf", *b"pstf", *b"cfar", *b"pres", *b"abvs", *b"blws",
                    *b"psts",
                ],
                "mymr" | "mym2" => &[
                    *b"rphf", *b"pref", *b"blwf", *b"pstf", *b"pres", *b"abvs", *b"blws", *b"psts",
                ],
                "hang" => &[*b"ljmo", *b"vjmo", *b"tjmo"],
                _ if indic_shaper => &[
                    *b"nukt", *b"akhn", *b"rphf", *b"rkrf", *b"pref", *b"blwf", *b"abvf", *b"half",
                    *b"pstf", *b"vatu", *b"cjct", *b"init", *b"pres", *b"abvs", *b"blws", *b"psts",
                    *b"haln",
                ],
                _ => &[
                    *b"nukt", *b"akhn", *b"rphf", *b"pref", *b"rkrf", *b"abvf", *b"blwf", *b"half",
                    *b"pstf", *b"vatu", *b"cjct", *b"isol", *b"init", *b"medi", *b"fina", *b"abvs",
                    *b"blws", *b"haln", *b"pres", *b"psts",
                ],
            };
            for tag in additions {
                put(tag.map(u16::from), 1, false)?;
            }
            if script == "khmr" {
                put(b"clig".map(u16::from), 1, false)?;
                if !explicit.contains(&b"liga".map(u16::from)) {
                    put(b"liga".map(u16::from), 0, true)?;
                }
            } else if indic_shaper {
                put(b"liga".map(u16::from), 0, true)?;
            } else if arabic {
                put(b"stch".map(u16::from), 1, false)?;
                put(b"mset".map(u16::from), 1, false)?;
            }
            let order: &[[u8; 4]] = if script == "khmr" {
                &[
                    *b"rvrn", *b"frac", *b"numr", *b"dnom", *b"locl", *b"ccmp", *b"pref", *b"blwf",
                    *b"abvf", *b"pstf", *b"cfar", *b"pres", *b"abvs", *b"blws", *b"psts",
                ]
            } else if arabic {
                &[
                    *b"rvrn", *b"frac", *b"numr", *b"dnom", *b"stch", *b"ccmp", *b"locl", *b"isol",
                    *b"fina", *b"fin2", *b"fin3", *b"medi", *b"med2", *b"init", *b"rlig", *b"calt",
                    *b"rclt", *b"liga", *b"clig", *b"mset",
                ]
            } else {
                &[
                    *b"rvrn", *b"frac", *b"numr", *b"dnom", *b"locl", *b"ccmp", *b"nukt", *b"akhn",
                    *b"rphf", *b"pref", *b"rkrf", *b"abvf", *b"blwf", *b"half", *b"pstf", *b"vatu",
                    *b"cjct", *b"isol", *b"init", *b"medi", *b"fina", *b"abvs", *b"blws", *b"haln",
                    *b"pres", *b"psts", *b"ljmo", *b"vjmo", *b"tjmo",
                ]
            };
            let mut features = Vec::new();
            features
                .try_reserve_exact(values.len())
                .map_err(|_| FontError::Capacity)?;
            for tag in order
                .iter()
                .map(|t| t.map(u16::from))
                .chain(self.features.iter().map(|f| f.tag()))
            {
                if let Some(&index) = indices.get(&tag)
                    && let Some(value) = values[index].take()
                {
                    features.push(value);
                }
            }
            features.extend(values.into_iter().flatten());
            out.features = features;
        }
        use ManagedShapingDirection::*;
        let vertical = matches!(direction, TopToBottom | BottomToTop);
        let required: &[[u8; 4]] = match direction {
            TopToBottom | BottomToTop => &[*b"vert", *b"vrt2", *b"vkrn"],
            RightToLeft => &[*b"rtla", *b"rtlm"],
            LeftToRight => &[*b"ltra", *b"ltrm"],
        };
        let mut features = Vec::new();
        features
            .try_reserve_exact(out.features.len() + required.len())
            .map_err(|_| FontError::Capacity)?;
        for tag in required {
            let tag = tag.map(u16::from);
            if !out.features.iter().any(|f| f.tag() == tag) {
                features.push(ManagedFeatureSetting::new(tag, 1)?);
            }
        }
        features.extend(
            out.features
                .into_iter()
                .filter(|f| !vertical || f.tag() != b"kern".map(u16::from)),
        );
        out.features = features;
        out.explicit = Some(explicit);
        out.source_defaults = false;
        Ok(out)
    }
}

impl ManagedFeatureOptions {
    pub(crate) fn for_fragment(&self, range: std::ops::Range<u32>) -> Result<Self, FontError> {
        let mut out = self.clone();
        out.ranges.clear();
        for feature in &self.ranges {
            let start = feature.start.max(range.start);
            let end = if feature.end == u32::MAX {
                range.end
            } else {
                feature.end.min(range.end)
            };
            if start < end {
                out.ranges.push(ManagedFeatureRange::new(
                    feature.tag,
                    feature.value,
                    start - range.start,
                    end - range.start,
                )?);
            }
        }
        Ok(out)
    }
}
