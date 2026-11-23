//! Exact original little-endian scalar, nullable-array and string transport.
use super::{ArchiveError, MAX_ARCHIVE_BYTES, MAX_ARRAY_ELEMENTS, MAX_STRING_BYTES, Result};
use crate::vector::Matrix4;

pub(super) trait Wire: Copy {
    const WIDTH: usize;
    fn zero() -> Self;
    fn read(reader: &mut Reader<'_>) -> Result<Self>;
    fn write(self, writer: &mut Writer) -> Result<()>;
}
pub(super) struct Reader<'a> {
    input: &'a [u8],
    offset: usize,
    pub version: i32,
}
impl<'a> Reader<'a> {
    pub fn new(input: &'a [u8]) -> Self {
        Self {
            input,
            offset: 0,
            version: 0,
        }
    }
    pub fn finished(&self) -> bool {
        self.offset == self.input.len()
    }
    pub fn remaining(&self) -> usize {
        self.input.len() - self.offset
    }
    fn take(&mut self, count: usize) -> Result<&'a [u8]> {
        let end = self
            .offset
            .checked_add(count)
            .ok_or(ArchiveError::Capacity)?;
        let result = self
            .input
            .get(self.offset..end)
            .ok_or(ArchiveError::Truncated)?;
        self.offset = end;
        Ok(result)
    }
    pub fn read<T: Wire>(&mut self) -> Result<T> {
        T::read(self)
    }
    pub fn count(&mut self, maximum: usize) -> Result<usize> {
        let count = self.read::<i32>()?;
        if count < 0 || count as usize > maximum {
            return Err(ArchiveError::InvalidData);
        }
        Ok(count as usize)
    }
    pub fn nullable_count(&mut self) -> Result<Option<usize>> {
        let count = self.read::<i32>()?;
        if count == -1 {
            return Ok(None);
        }
        if count < 0 || count as usize > MAX_ARRAY_ELEMENTS {
            return Err(ArchiveError::InvalidData);
        }
        Ok(Some(count as usize))
    }
    pub fn enumeration(&mut self, maximum: i32) -> Result<i32> {
        let value = self.read::<i32>()?;
        if !(0..=maximum).contains(&value) {
            return Err(ArchiveError::InvalidData);
        }
        Ok(value)
    }
    pub fn array<T: Wire + bytemuck::Pod>(&mut self) -> Result<Vec<T>> {
        let count = self.count(MAX_ARRAY_ELEMENTS)?;
        self.values(count)
    }
    pub fn nullable_array<T: Wire + bytemuck::Pod>(&mut self) -> Result<Option<Vec<T>>> {
        self.nullable_count()?
            .map(|count| self.values(count))
            .transpose()
    }
    fn values<T: Wire + bytemuck::Pod>(&mut self, count: usize) -> Result<Vec<T>> {
        let bytes = count.checked_mul(T::WIDTH).ok_or(ArchiveError::Capacity)?;
        if bytes > self.remaining() {
            return Err(ArchiveError::Truncated);
        }
        let mut result = super::reserve(count)?;
        if cfg!(target_endian = "little") && T::WIDTH != 0 {
            // Safe unaligned reads retain all original float payload/sign bits.
            // Original array elements are POD scalars or fixed scalar vectors.
            result.extend(
                self.take(bytes)?
                    .chunks_exact(T::WIDTH)
                    .map(bytemuck::pod_read_unaligned::<T>),
            );
        } else {
            for _ in 0..count {
                result.push(self.read()?);
            }
        }
        Ok(result)
    }
    pub fn string(&mut self) -> Result<Option<String>> {
        let count = self.read::<i32>()?;
        if count == -1 {
            return Ok(None);
        }
        if count < 0 || count as usize > MAX_STRING_BYTES {
            return Err(ArchiveError::InvalidData);
        }
        // Original Encoding.UTF8 replacement decoding, separate from strict
        // encoded glyph APIs. Valid source strings retain their exact scalars.
        Ok(Some(
            String::from_utf8_lossy(self.take(count as usize)?).into_owned(),
        ))
    }
}
pub(super) struct Writer {
    pub bytes: Vec<u8>,
    pub version: i32,
}
impl Writer {
    pub fn new(version: i32) -> Self {
        Self {
            bytes: Vec::new(),
            version,
        }
    }
    fn append(&mut self, data: &[u8]) -> Result<()> {
        let end = self
            .bytes
            .len()
            .checked_add(data.len())
            .ok_or(ArchiveError::Capacity)?;
        if end > MAX_ARCHIVE_BYTES {
            return Err(ArchiveError::Capacity);
        }
        self.bytes
            .try_reserve(data.len())
            .map_err(|_| ArchiveError::Capacity)?;
        self.bytes.extend_from_slice(data);
        Ok(())
    }
    pub fn write<T: Wire>(&mut self, value: T) -> Result<()> {
        value.write(self)
    }
    pub fn count(&mut self, count: usize, maximum: usize) -> Result<()> {
        if count > maximum || count > i32::MAX as usize {
            return Err(ArchiveError::InvalidData);
        }
        self.write(count as i32)
    }
    pub fn nullable_count(&mut self, count: Option<usize>) -> Result<()> {
        if let Some(count) = count {
            self.count(count, MAX_ARRAY_ELEMENTS)
        } else {
            self.write(-1i32)
        }
    }
    pub fn array<T: Wire + bytemuck::Pod>(&mut self, values: &[T]) -> Result<()> {
        self.count(values.len(), MAX_ARRAY_ELEMENTS)?;
        self.values(values)
    }
    pub fn nullable_array<T: Wire + bytemuck::Pod>(&mut self, values: Option<&[T]>) -> Result<()> {
        self.nullable_count(values.map(<[T]>::len))?;
        if let Some(values) = values {
            self.values(values)?;
        }
        Ok(())
    }
    fn values<T: Wire + bytemuck::Pod>(&mut self, values: &[T]) -> Result<()> {
        let size = values
            .len()
            .checked_mul(T::WIDTH)
            .ok_or(ArchiveError::Capacity)?;
        let end = self
            .bytes
            .len()
            .checked_add(size)
            .ok_or(ArchiveError::Capacity)?;
        if end > MAX_ARCHIVE_BYTES {
            return Err(ArchiveError::Capacity);
        }
        if cfg!(target_endian = "little") {
            // One bounded copy for actual scalar/vector arrays, without typed
            // pointer casts, per-element allocation checks or float arithmetic.
            return self.append(bytemuck::cast_slice(values));
        }
        self.bytes
            .try_reserve(size)
            .map_err(|_| ArchiveError::Capacity)?;
        for value in values {
            self.write(*value)?;
        }
        Ok(())
    }
    pub fn string(&mut self, value: Option<&str>) -> Result<()> {
        let Some(value) = value else {
            return self.write(-1i32);
        };
        self.count(value.len(), MAX_STRING_BYTES)?;
        self.append(value.as_bytes())
    }
}
macro_rules! scalar {
    ($($ty:ty),* $(,)?) => { $(impl Wire for $ty {
        const WIDTH: usize = std::mem::size_of::<Self>();
        fn zero() -> Self { 0 as Self }
        fn read(reader: &mut Reader<'_>) -> Result<Self> {
            let mut bytes = [0u8; std::mem::size_of::<Self>()];
            bytes.copy_from_slice(reader.take(Self::WIDTH)?);
            Ok(Self::from_le_bytes(bytes))
        }
        fn write(self, writer: &mut Writer) -> Result<()> { writer.append(&self.to_le_bytes()) }
    })* };
}
scalar!(u8, u16, i32, u32, u64, f32, f64);
impl Wire for bool {
    const WIDTH: usize = 1;
    fn zero() -> Self {
        false
    }
    fn read(reader: &mut Reader<'_>) -> Result<Self> {
        Ok(reader.read::<u8>()? != 0)
    }
    fn write(self, writer: &mut Writer) -> Result<()> {
        writer.write(u8::from(self))
    }
}
impl<T: Wire, const N: usize> Wire for [T; N] {
    const WIDTH: usize = T::WIDTH * N;
    fn zero() -> Self {
        [T::zero(); N]
    }
    fn read(reader: &mut Reader<'_>) -> Result<Self> {
        let mut result = Self::zero();
        for value in &mut result {
            *value = reader.read()?;
        }
        Ok(result)
    }
    fn write(self, writer: &mut Writer) -> Result<()> {
        for value in self {
            writer.write(value)?;
        }
        Ok(())
    }
}
impl Wire for Matrix4 {
    const WIDTH: usize = 64;
    fn zero() -> Self {
        Self([[0.0; 4]; 4])
    }
    fn read(reader: &mut Reader<'_>) -> Result<Self> {
        Ok(Self(reader.read()?))
    }
    fn write(self, writer: &mut Writer) -> Result<()> {
        writer.write(self.0)
    }
}
