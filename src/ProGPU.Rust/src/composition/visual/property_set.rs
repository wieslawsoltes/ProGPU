use super::*;
use crate::geometry::Affine;
use std::collections::HashMap;

/// Original strongly distinguished composition property kinds.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum CompositionPropertyKind {
    Boolean,
    Color,
    Matrix3x2,
    Matrix4x4,
    Quaternion,
    Scalar,
    Vector2,
    Vector3,
    Vector4,
}
/// Retained original property payload. Insertion preserves raw numeric values;
/// this neutral property bag does not change visual fields or start animations.
#[derive(Clone, Copy, Debug, PartialEq)]
pub enum CompositionPropertyValue {
    /// Boolean source value.
    Boolean(bool),
    /// Original byte RGBA color.
    Color([u8; 4]),
    /// Original six source affine coefficients.
    Matrix3x2(Affine),
    /// Original complete source matrix.
    Matrix4x4(Matrix4),
    /// Original x/y/z/w, without wrapper normalization.
    Quaternion([f32; 4]),
    /// Original scalar.
    Scalar(f32),
    /// Original two-axis value.
    Vector2([f32; 2]),
    /// Original three-axis value.
    Vector3([f32; 3]),
    /// Original four-component value, distinct from color/quaternion.
    Vector4([f32; 4]),
}
impl CompositionPropertyValue {
    /// Exact typed discriminant used by original TryGet methods.
    pub fn kind(self) -> CompositionPropertyKind {
        match self {
            Self::Boolean(_) => CompositionPropertyKind::Boolean,
            Self::Color(_) => CompositionPropertyKind::Color,
            Self::Matrix3x2(_) => CompositionPropertyKind::Matrix3x2,
            Self::Matrix4x4(_) => CompositionPropertyKind::Matrix4x4,
            Self::Quaternion(_) => CompositionPropertyKind::Quaternion,
            Self::Scalar(_) => CompositionPropertyKind::Scalar,
            Self::Vector2(_) => CompositionPropertyKind::Vector2,
            Self::Vector3(_) => CompositionPropertyKind::Vector3,
            Self::Vector4(_) => CompositionPropertyKind::Vector4,
        }
    }
}
/// Original property lookup outcome; missing and wrong-type are distinct.
#[derive(Clone, Copy, Debug, PartialEq)]
pub enum CompositionPropertyLookup {
    /// Actual stored value of the requested kind.
    Succeeded(CompositionPropertyValue),
    /// No original ordinal name entry.
    NotFound,
    /// An entry exists under that exact name with another payload kind.
    TypeMismatch,
}
/// Owned thread-local original ordinal property dictionary. Clones share actual
/// mutable property identity; it has no hidden global compositor or callbacks.
#[derive(Clone, Debug, Default)]
pub struct CompositionPropertySet(Rc<RefCell<HashMap<String, CompositionPropertyValue>>>);
impl CompositionPropertySet {
    /// Insert or replace one exact case-sensitive source name.
    pub fn insert(&self, name: &str, value: CompositionPropertyValue) -> Result<(), Error> {
        name_valid(name)?;
        let mut values = self.0.borrow_mut();
        if !values.contains_key(name) {
            values.try_reserve(1).map_err(|_| Error::Capacity)?;
        }
        values.insert(name.to_owned(), value);
        Ok(())
    }
    /// Original typed lookup without coercion or numeric sanitization.
    pub fn get(
        &self,
        name: &str,
        kind: CompositionPropertyKind,
    ) -> Result<CompositionPropertyLookup, Error> {
        name_valid(name)?;
        Ok(match self.0.borrow().get(name).copied() {
            None => CompositionPropertyLookup::NotFound,
            Some(v) if v.kind() == kind => CompositionPropertyLookup::Succeeded(v),
            Some(_) => CompositionPropertyLookup::TypeMismatch,
        })
    }
    /// Copy the complete current property generation for explicit expression use.
    pub fn snapshot(&self) -> HashMap<String, CompositionPropertyValue> {
        self.0.borrow().clone()
    }
}
fn name_valid(name: &str) -> Result<(), Error> {
    if name.is_empty() || name.chars().all(char::is_whitespace) {
        Err(Error::InvalidInput)
    } else {
        Ok(())
    }
}
impl Visual {
    /// Actual lazy property-set owner, independent from typed visual properties.
    pub fn property_set(&self) -> CompositionPropertySet {
        self.0
            .borrow_mut()
            .property_set
            .get_or_insert_with(CompositionPropertySet::default)
            .clone()
    }
}
