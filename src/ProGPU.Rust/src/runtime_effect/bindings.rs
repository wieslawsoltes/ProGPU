//! Original tightly packed native-endian uniforms and snapshot-owned children.
use super::{RuntimeEffect, RuntimeEffectChild, RuntimeEffectError as E, RuntimeLocalMatrix};
use std::{borrow::Cow, sync::Arc};

/// Original uniform input families. Borrowed arrays avoid allocation; inline
/// point/color/matrix values retain their exact original component order.
#[derive(Clone, Debug, Default)]
pub enum RuntimeUniformValue<'a> {
    /// Original default value with zero bytes.
    #[default]
    Empty,
    /// One native-endian 32-bit float, with no finite-value restriction.
    Float(f32),
    /// One native-endian signed 32-bit integer.
    Int(i32),
    /// Original float span or once-flattened ragged array.
    Floats(Cow<'a, [f32]>),
    /// Original integer span.
    Ints(Cow<'a, [i32]>),
    /// Two-coordinate original point/size.
    Point([f32; 2]),
    /// Three-coordinate original point.
    Point3([f32; 3]),
    /// Original straight RGBA float components.
    Color([f32; 4]),
    /// Original integer point/size, retained inline.
    IntPoint([i32; 2]),
    /// Original nine-float SKMatrix.Values order.
    Matrix(RuntimeLocalMatrix),
}
impl<'a> RuntimeUniformValue<'a> {
    /// Original default-value discriminator, distinct from an empty float span.
    pub fn is_empty(&self) -> bool {
        matches!(self, Self::Empty)
    }
    /// Original signed byte-count overflow admission.
    pub fn size(&self) -> Result<usize, E> {
        let count = match self {
            Self::Empty => 0,
            Self::Float(_) | Self::Int(_) => 1,
            Self::Floats(v) => v.len(),
            Self::Ints(v) => v.len(),
            Self::Point(_) | Self::IntPoint(_) => 2,
            Self::Point3(_) => 3,
            Self::Color(_) => 4,
            Self::Matrix(_) => 9,
        };
        count
            .checked_mul(4)
            .filter(|&v| v <= i32::MAX as usize)
            .ok_or(E::Capacity)
    }
    /// Copy exactly the value's native-endian bytes after whole-capacity
    /// admission. Any longer destination tail remains unchanged.
    pub fn write_to(&self, destination: &mut [u8]) -> Result<(), E> {
        let size = self.size()?;
        let output = destination.get_mut(..size).ok_or(E::DestinationTooSmall)?;
        let bytes: &[u8] = match self {
            Self::Empty => &[],
            Self::Float(v) => bytemuck::bytes_of(v),
            Self::Int(v) => bytemuck::bytes_of(v),
            Self::Floats(v) => bytemuck::cast_slice(v.as_ref()),
            Self::Ints(v) => bytemuck::cast_slice(v.as_ref()),
            Self::Point(v) => bytemuck::cast_slice(v),
            Self::Point3(v) => bytemuck::cast_slice(v),
            Self::Color(v) => bytemuck::cast_slice(v),
            Self::IntPoint(v) => bytemuck::cast_slice(v),
            Self::Matrix(v) => bytemuck::cast_slice(&v.0),
        };
        output.copy_from_slice(bytes);
        Ok(())
    }
    /// Original RGBA8-to-straight-float conversion with four independent SIMD
    /// division lanes; this is metadata packing, not pixel evaluation.
    pub fn color_bytes(value: [u8; 4]) -> Self {
        use wide::f32x4 as V;
        Self::Color((V::from(value.map(f32::from)) / V::splat(255.0)).to_array())
    }
    /// Flatten the original ragged float rows in original order once. No matrix
    /// padding, dimension inference or column/row transposition is introduced.
    pub fn from_rows(rows: &[&[f32]]) -> Result<Self, E> {
        let length = rows
            .iter()
            .try_fold(0usize, |n, row| n.checked_add(row.len()).ok_or(E::Capacity))?;
        length
            .checked_mul(4)
            .filter(|&v| v <= i32::MAX as usize)
            .ok_or(E::Capacity)?;
        let mut values = Vec::new();
        values.try_reserve_exact(length).map_err(|_| E::Capacity)?;
        for row in rows {
            values.extend_from_slice(row);
        }
        Ok(Self::Floats(Cow::Owned(values)))
    }
}
impl From<f32> for RuntimeUniformValue<'_> {
    fn from(value: f32) -> Self {
        Self::Float(value)
    }
}
impl From<i32> for RuntimeUniformValue<'_> {
    fn from(value: i32) -> Self {
        Self::Int(value)
    }
}
impl<'a> From<&'a [f32]> for RuntimeUniformValue<'a> {
    fn from(value: &'a [f32]) -> Self {
        Self::Floats(Cow::Borrowed(value))
    }
}
impl<'a> From<&'a [i32]> for RuntimeUniformValue<'a> {
    fn from(value: &'a [i32]) -> Self {
        Self::Ints(Cow::Borrowed(value))
    }
}
impl<'a, const N: usize> From<&'a [f32; N]> for RuntimeUniformValue<'a> {
    fn from(value: &'a [f32; N]) -> Self {
        Self::Floats(Cow::Borrowed(value))
    }
}
impl<'a, const N: usize> From<&'a [i32; N]> for RuntimeUniformValue<'a> {
    fn from(value: &'a [i32; N]) -> Self {
        Self::Ints(Cow::Borrowed(value))
    }
}
impl From<Vec<f32>> for RuntimeUniformValue<'_> {
    fn from(value: Vec<f32>) -> Self {
        Self::Floats(Cow::Owned(value))
    }
}
impl From<Vec<i32>> for RuntimeUniformValue<'_> {
    fn from(value: Vec<i32>) -> Self {
        Self::Ints(Cow::Owned(value))
    }
}
impl From<[f32; 2]> for RuntimeUniformValue<'_> {
    fn from(value: [f32; 2]) -> Self {
        Self::Point(value)
    }
}
impl From<[f32; 3]> for RuntimeUniformValue<'_> {
    fn from(value: [f32; 3]) -> Self {
        Self::Point3(value)
    }
}
impl From<[f32; 4]> for RuntimeUniformValue<'_> {
    fn from(value: [f32; 4]) -> Self {
        Self::Color(value)
    }
}
impl From<[i32; 2]> for RuntimeUniformValue<'_> {
    fn from(value: [i32; 2]) -> Self {
        Self::IntPoint(value)
    }
}
impl From<RuntimeLocalMatrix> for RuntimeUniformValue<'_> {
    fn from(value: RuntimeLocalMatrix) -> Self {
        Self::Matrix(value)
    }
}

/// Original zero-initialized mutable uniform storage. Snapshot sharing is O(1);
/// the first write after sharing copies the complete original buffer once.
#[derive(Clone, Debug)]
pub struct RuntimeEffectUniforms {
    effect: RuntimeEffect,
    data: Arc<[u8]>,
}
impl RuntimeEffectUniforms {
    /// Allocate exactly the original complete packed byte count and clear it.
    pub fn new(effect: RuntimeEffect) -> Result<Self, E> {
        let mut data = Vec::new();
        data.try_reserve_exact(effect.uniform_size())
            .map_err(|_| E::Capacity)?;
        data.resize(effect.uniform_size(), 0);
        Ok(Self {
            effect,
            data: data.into(),
        })
    }
    /// Number of original declarations, including repeated names.
    pub fn len(&self) -> usize {
        self.effect.program().uniforms().len()
    }
    /// Whether the producer reflected no numeric uniforms.
    pub fn is_empty(&self) -> bool {
        self.len() == 0
    }
    /// Complete original storage size in bytes.
    pub fn size(&self) -> usize {
        self.data.len()
    }
    /// Original ordered names.
    pub fn names(&self) -> impl ExactSizeIterator<Item = &str> {
        self.effect.uniforms()
    }
    /// Original case-sensitive first-match name lookup.
    pub fn contains(&self, name: &str) -> bool {
        self.effect
            .program()
            .uniforms()
            .iter()
            .any(|v| v.name.as_ref() == name)
    }
    /// Write only the first declaration with this name, validating complete size
    /// before copy-on-write. Numeric source type is not a setter restriction.
    pub fn set<'a>(
        &mut self,
        name: &str,
        value: impl Into<RuntimeUniformValue<'a>>,
    ) -> Result<(), E> {
        let value = value.into();
        let descriptor = self
            .effect
            .program()
            .uniforms()
            .iter()
            .find(|v| v.name.as_ref() == name)
            .ok_or_else(|| E::UnknownUniform(name.into()))?;
        let actual = value.size()?;
        if actual != descriptor.size {
            return Err(E::UniformSize {
                name: name.into(),
                required: descriptor.size,
                actual,
            });
        }
        let range = descriptor.offset..descriptor.offset + descriptor.size;
        value.write_to(&mut self.writable()?[range])
    }
    /// Original all-byte reset; existing snapshots retain their old contents.
    pub fn reset(&mut self) -> Result<(), E> {
        self.writable()?.fill(0);
        Ok(())
    }
    /// Original independent byte copy, suitable for transport without bindings.
    pub fn to_data(&self) -> Result<Vec<u8>, E> {
        let mut bytes = Vec::new();
        bytes
            .try_reserve_exact(self.data.len())
            .map_err(|_| E::Capacity)?;
        bytes.extend_from_slice(&self.data);
        Ok(bytes)
    }
    /// Read-only current generation; this cannot mutate retained snapshots.
    pub fn data(&self) -> &[u8] {
        &self.data
    }
    pub(super) fn snapshot(&self) -> Arc<[u8]> {
        self.data.clone()
    }
    pub(super) fn ensure_effect(&self, effect: &RuntimeEffect) -> Result<(), E> {
        if self.effect.same_owner(effect) {
            Ok(())
        } else {
            Err(E::UniformOwner)
        }
    }
    fn writable(&mut self) -> Result<&mut [u8], E> {
        if Arc::get_mut(&mut self.data).is_none() {
            self.data = self.to_data()?.into();
        }
        // Only this builder owns the replacement; immutable snapshots cannot
        // acquire it through any shared reference while this borrow is active.
        Arc::get_mut(&mut self.data).ok_or(E::UniformOwner)
    }
}

/// Original ordered nullable child storage. Actual owners are retained strongly
/// and each instance captures a separate immutable generation.
#[derive(Clone, Debug)]
pub struct RuntimeEffectChildren {
    effect: RuntimeEffect,
    values: Arc<[Option<RuntimeEffectChild>]>,
}
impl RuntimeEffectChildren {
    /// Allocate exactly the original reflected child count, initially null.
    pub fn new(effect: RuntimeEffect) -> Result<Self, E> {
        let mut values = Vec::new();
        values
            .try_reserve_exact(effect.program().children().len())
            .map_err(|_| E::Capacity)?;
        values.resize_with(effect.program().children().len(), || None);
        Ok(Self {
            effect,
            values: values.into(),
        })
    }
    /// Original slot count, including duplicate declaration names.
    pub fn len(&self) -> usize {
        self.values.len()
    }
    /// Whether the producer reflected no children.
    pub fn is_empty(&self) -> bool {
        self.values.is_empty()
    }
    /// Original ordered declaration names.
    pub fn names(&self) -> impl ExactSizeIterator<Item = &str> {
        self.effect.children()
    }
    /// Original case-sensitive first-match lookup.
    pub fn contains(&self, name: &str) -> bool {
        self.index(name).is_some()
    }
    /// Set the first declaration with this name. As in the original, actual
    /// shader/filter/blender kinds are not rejected against declaration metadata.
    pub fn set(&mut self, name: &str, value: Option<RuntimeEffectChild>) -> Result<(), E> {
        let index = self
            .index(name)
            .ok_or_else(|| E::UnknownChild(name.into()))?;
        self.writable()?[index] = value;
        Ok(())
    }
    /// Clear all original slots while retaining existing instance generations.
    pub fn reset(&mut self) -> Result<(), E> {
        self.writable()?.fill(None);
        Ok(())
    }
    /// Original independent slot-array copy; actual source objects remain shared.
    pub fn to_array(&self) -> Result<Vec<Option<RuntimeEffectChild>>, E> {
        let mut values = Vec::new();
        values
            .try_reserve_exact(self.values.len())
            .map_err(|_| E::Capacity)?;
        values.extend_from_slice(&self.values);
        Ok(values)
    }
    /// Read-only complete current child generation.
    pub fn values(&self) -> &[Option<RuntimeEffectChild>] {
        &self.values
    }
    pub(super) fn snapshot(&self) -> Arc<[Option<RuntimeEffectChild>]> {
        self.values.clone()
    }
    pub(super) fn ensure_effect(&self, effect: &RuntimeEffect) -> Result<(), E> {
        if self.effect.same_owner(effect) {
            Ok(())
        } else {
            Err(E::ChildOwner)
        }
    }
    fn index(&self, name: &str) -> Option<usize> {
        self.effect
            .program()
            .children()
            .iter()
            .position(|v| v.name.as_ref() == name)
    }
    fn writable(&mut self) -> Result<&mut [Option<RuntimeEffectChild>], E> {
        if Arc::get_mut(&mut self.values).is_none() {
            self.values = self.to_array()?.into();
        }
        Arc::get_mut(&mut self.values).ok_or(E::ChildOwner)
    }
}
