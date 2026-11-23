//! Original variation factories, including distinct same-face and cross-face
//! duplicate-coordinate policies and the one-entry immutable selection cache.
use super::{Typeface, TypefaceError, TypefaceResult, style::float_key};
use crate::font::{ManagedFont, ManagedVariationSetting, Tag};
use std::{
    hash::{Hash, Hasher},
    sync::Arc,
};
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq, Hash)]
pub struct FontPaletteOverride {
    pub index: u16,
    pub color: u32,
}
#[derive(Clone, Copy, Debug, Default)]
pub struct FontVariationAxis {
    pub tag: Tag,
    pub minimum: f32,
    pub default: f32,
    pub maximum: f32,
    pub hidden: bool,
}
#[derive(Clone, Copy, Debug, Default)]
pub struct FontVariationCoordinate {
    pub axis: Tag,
    pub value: f32,
}
impl PartialEq for FontVariationCoordinate {
    fn eq(&self, other: &Self) -> bool {
        self.axis == other.axis && float_key(self.value) == float_key(other.value)
    }
}
impl Eq for FontVariationCoordinate {}
impl Hash for FontVariationCoordinate {
    fn hash<H: Hasher>(&self, state: &mut H) {
        self.axis.hash(state);
        float_key(self.value).hash(state);
    }
}
impl PartialEq for FontVariationAxis {
    fn eq(&self, other: &Self) -> bool {
        self.tag == other.tag
            && self.hidden == other.hidden
            && float_key(self.minimum) == float_key(other.minimum)
            && float_key(self.default) == float_key(other.default)
            && float_key(self.maximum) == float_key(other.maximum)
    }
}
impl Eq for FontVariationAxis {}
impl Hash for FontVariationAxis {
    fn hash<H: Hasher>(&self, state: &mut H) {
        self.tag.hash(state);
        self.hidden.hash(state);
        float_key(self.minimum).hash(state);
        float_key(self.default).hash(state);
        float_key(self.maximum).hash(state);
    }
}
/// Borrowed call parameters; a successful clone owns its selected source and
/// copies all retained coordinate/palette data before returning.
#[derive(Clone, Copy, Debug, Default)]
pub struct FontArguments<'a> {
    pub collection_index: i32,
    pub variation_position: &'a [FontVariationCoordinate],
    pub palette_index: i32,
    pub palette_overrides: &'a [FontPaletteOverride],
}
impl Typeface {
    pub fn variation_parameter_count(&self) -> TypefaceResult<usize> {
        Ok(if self.is_empty() {
            0
        } else {
            self.font().variation_axes()?.len()
        })
    }
    pub fn write_variation_parameters(
        &self,
        output: &mut [FontVariationAxis],
    ) -> TypefaceResult<usize> {
        if self.is_empty() || output.is_empty() {
            return Ok(0);
        }
        let axes = self.font().variation_axes()?;
        let count = axes.len().min(output.len());
        for (target, source) in output.iter_mut().zip(axes) {
            *target = FontVariationAxis {
                tag: Tag::new(source.tag),
                minimum: source.minimum,
                default: source.default,
                maximum: source.maximum,
                hidden: source.hidden,
            };
        }
        Ok(count)
    }
    pub fn variation_parameters(&self) -> TypefaceResult<Vec<FontVariationAxis>> {
        let mut result = vec![FontVariationAxis::default(); self.variation_parameter_count()?];
        self.write_variation_parameters(&mut result)?;
        Ok(result)
    }
    pub fn write_variation_position(
        &self,
        output: &mut [FontVariationCoordinate],
    ) -> TypefaceResult<usize> {
        if self.is_empty() || output.is_empty() {
            return Ok(0);
        }
        let axes = self.font().variation_axes()?;
        let settings = self.font().variation_settings();
        let count = axes.len().min(output.len());
        for index in 0..count {
            output[index] = FontVariationCoordinate {
                axis: Tag::new(axes[index].tag),
                value: if axes.len() == settings.len() {
                    settings[index].value
                } else {
                    axes[index].default
                },
            };
        }
        Ok(count)
    }
    pub fn variation_position(&self) -> TypefaceResult<Vec<FontVariationCoordinate>> {
        let mut result =
            vec![FontVariationCoordinate::default(); self.variation_parameter_count()?];
        self.write_variation_position(&mut result)?;
        Ok(result)
    }
    fn variation_font(&self, position: &[FontVariationCoordinate]) -> TypefaceResult<ManagedFont> {
        if let Some((previous, font)) = &*self.0.variation.lock().unwrap_or_else(|e| e.into_inner())
        {
            if previous.as_ref() == position {
                return Ok(font.clone());
            }
        }
        let varied = apply(self.font(), position, true)?;
        if position.len() <= self.font().variation_axes()?.len() {
            *self.0.variation.lock().unwrap_or_else(|e| e.into_inner()) =
                Some((Arc::from(position), varied.clone()));
        }
        Ok(varied)
    }
    pub fn clone_position(&self, position: &[FontVariationCoordinate]) -> TypefaceResult<Self> {
        if self.is_empty() || self.font().variation_axes()?.is_empty() {
            return Ok(Self::with_options(self.font().clone(), self.options()));
        }
        let font = self.variation_font(position)?;
        let metadata = font.metadata();
        let (bold, italic) = (metadata.weight_class >= 600, metadata.italic);
        Ok(Self::create(
            font,
            self.0.family.clone(),
            bold,
            italic,
            None,
            false,
        ))
    }
    /// Original palette convenience overload also selects collection face zero.
    pub fn clone_palette(&self, palette_index: i32) -> TypefaceResult<Self> {
        self.clone_arguments(FontArguments {
            palette_index,
            ..Default::default()
        })
    }
    pub fn clone_arguments(&self, args: FontArguments<'_>) -> TypefaceResult<Self> {
        let index =
            u32::try_from(args.collection_index).map_err(|_| TypefaceError::InvalidArgument)?;
        let mut selected = if index == self.font().source().face_index() {
            self.font().clone()
        } else {
            ManagedFont::new(Arc::<[u8]>::from(self.font().source().data()), index)?
        };
        if !args.variation_position.is_empty() {
            selected = if selected.same_instance(self.font()) {
                self.variation_font(args.variation_position)?
            } else {
                apply(&selected, args.variation_position, false)?
            };
        }
        if args.palette_index != 0 || !args.palette_overrides.is_empty() {
            let overrides: Vec<_> = args
                .palette_overrides
                .iter()
                .map(|value| (value.index, value.color))
                .collect();
            selected = selected.with_color_palette(args.palette_index, &overrides)?;
        }
        let m = selected.metadata();
        let (family, bold, italic) = (m.family.clone(), m.weight_class >= 600, m.italic);
        Ok(Self::create(selected, family, bold, italic, None, false))
    }
}
fn apply(
    font: &ManagedFont,
    position: &[FontVariationCoordinate],
    deduplicate: bool,
) -> TypefaceResult<ManagedFont> {
    let axes = font.variation_axes()?;
    let capacity = axes.len().min(position.len());
    let mut settings: Vec<ManagedVariationSetting> = Vec::with_capacity(capacity);
    for coordinate in position {
        let Some(axis) = axes
            .iter()
            .find(|axis| Tag::new(axis.tag) == coordinate.axis)
        else {
            continue;
        };
        let tag: Arc<[u16]> = Arc::from(axis.tag.map(u16::from));
        if deduplicate {
            if let Some(previous) = settings.iter_mut().find(|setting| setting.tag == tag) {
                previous.value = coordinate.value;
                continue;
            }
        }
        // The original cross-face overload has a fixed min(position,axis) buffer;
        // repeated coordinates exceeding it fail before publishing a clone.
        if settings.len() == capacity {
            return Err(TypefaceError::InvalidArgument);
        }
        settings.push(ManagedVariationSetting::new(tag, coordinate.value));
    }
    Ok(font.with_variations(&settings)?)
}
