//! Original SKLatticeLayout: alternating fixed/scalable spans and source cells.
use super::*;
/// Original cell tag. Unrecognized source values retain default texture behavior.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct ImageLatticeRectType(pub i32);
impl ImageLatticeRectType {
    /// Sample the source image.
    pub const DEFAULT: Self = Self(0);
    /// Omit this cell entirely.
    pub const TRANSPARENT: Self = Self(1);
    /// Use its original optional fixed RGBA color.
    pub const FIXED_COLOR: Self = Self(2);
}
/// Frozen original lattice inventory. Missing trailing cell metadata defaults
/// independently; the skipped first row/column retains its original array index.
#[derive(Clone, Debug, Default)]
pub struct ImageLattice {
    /// Strictly increasing source X divisions, including an optional first edge.
    pub x_divisions: Arc<[i32]>,
    /// Strictly increasing source Y divisions.
    pub y_divisions: Arc<[i32]>,
    /// Original optional row-major cell tags.
    pub rect_types: Option<Arc<[ImageLatticeRectType]>>,
    /// Optional source left/top/right/bottom extent.
    pub bounds: Option<[i32; 4]>,
    /// Original optional row-major straight RGBA colors.
    pub colors: Option<Arc<[[u8; 4]]>>,
}
impl PartialEq for ImageLattice {
    fn eq(&self, other: &Self) -> bool {
        fn same<T>(a: &Option<Arc<[T]>>, b: &Option<Arc<[T]>>) -> bool {
            match (a, b) {
                (None, None) => true,
                (Some(a), Some(b)) => Arc::ptr_eq(a, b),
                _ => false,
            }
        }
        Arc::ptr_eq(&self.x_divisions, &other.x_divisions)
            && Arc::ptr_eq(&self.y_divisions, &other.y_divisions)
            && same(&self.rect_types, &other.rect_types)
            && same(&self.colors, &other.colors)
            && self.bounds == other.bounds
    }
}
impl Eq for ImageLattice {}
impl ImageLattice {
    /// Freeze both original division arrays without normalizing their contents.
    pub fn new(x: impl Into<Arc<[i32]>>, y: impl Into<Arc<[i32]>>) -> Self {
        Self {
            x_divisions: x.into(),
            y_divisions: y.into(),
            ..Self::default()
        }
    }
    /// Produce the original ordered patch generation. Invalid source divisions or
    /// the original all-fixed degenerate lattice return None without a draw.
    pub fn layout(
        &self,
        size: [u32; 2],
        destination: [f32; 4],
        filter: Option<&ColorFilter>,
    ) -> Result<Option<ImagePatchBatch>, Error> {
        let width = i32::try_from(size[0]).map_err(|_| Error::Capacity)?;
        let height = i32::try_from(size[1]).map_err(|_| Error::Capacity)?;
        let b = self.bounds.unwrap_or([0, 0, width, height]);
        let x = &self.x_divisions;
        let y = &self.y_divisions;
        if b[0] < 0
            || b[1] < 0
            || b[2] > width
            || b[3] > height
            || b[0] > b[2]
            || b[1] > b[3]
            || !divisions(x, b[0], b[2])
            || !divisions(y, b[1], b[3])
        {
            return Ok(None);
        }
        let zero_x = x.is_empty() || (x.len() == 1 && x[0] == b[0]);
        let zero_y = y.is_empty() || (y.len() == 1 && y[0] == b[1]);
        if zero_x && zero_y {
            return Ok(None);
        }
        let x = dimension(x, b[0], b[2], destination[0], destination[2])?;
        let y = dimension(y, b[1], b[3], destination[1], destination[3])?;
        Ok(Some(patches(
            &x,
            &y,
            self.rect_types.as_deref(),
            self.colors.as_deref(),
            self.x_divisions
                .len()
                .checked_add(1)
                .ok_or(Error::Capacity)?,
            filter,
            destination,
        )?))
    }
}
impl ImagePatchBatch {
    /// Original nine-patch center and independently compressed fixed borders.
    /// Out-of-image centers are errors; empty/reversed centers produce no draw.
    pub fn nine_patch(
        size: [u32; 2],
        center: [i32; 4],
        destination: [f32; 4],
    ) -> Result<Option<Self>, Error> {
        let w = i32::try_from(size[0]).map_err(|_| Error::Capacity)?;
        let h = i32::try_from(size[1]).map_err(|_| Error::Capacity)?;
        if center[0] < 0 || center[1] < 0 || center[2] > w || center[3] > h {
            return Err(Error::InvalidInput);
        }
        if center[0] >= center[2] || center[1] >= center[3] {
            return Ok(None);
        }
        let x = Dimension {
            source: vec![0, center[0], center[2], w],
            destination: nine_dimension(w, center[0], center[2], destination[0], destination[2])
                .to_vec(),
            skipped: false,
        };
        let y = Dimension {
            source: vec![0, center[1], center[3], h],
            destination: nine_dimension(h, center[1], center[3], destination[1], destination[3])
                .to_vec(),
            skipped: false,
        };
        Ok(Some(patches(&x, &y, None, None, 3, None, destination)?))
    }
}
fn divisions(values: &[i32], start: i32, end: i32) -> bool {
    let mut previous = start - 1;
    for &value in values {
        if previous >= value || value >= end {
            return false;
        }
        previous = value;
    }
    true
}
struct Dimension {
    source: Vec<i32>,
    destination: Vec<f32>,
    skipped: bool,
}
fn dimension(divs: &[i32], start: i32, end: i32, near: f32, far: f32) -> Result<Dimension, Error> {
    let skipped = divs.first() == Some(&start);
    let offset = usize::from(skipped);
    let count = divs.len() - offset;
    let scalable = if count == 0 {
        if skipped { end - start } else { 0 }
    } else {
        let mut sum = if skipped { divs[offset] - start } else { 0 };
        for i in (usize::from(skipped)..count).step_by(2) {
            let right = if i + 1 < count {
                divs[offset + i + 1]
            } else {
                end
            };
            sum += right - divs[offset + i];
        }
        sum
    };
    let fixed = end - start - scalable;
    let length = far - near;
    let normal = fixed as f32 <= length;
    let scale = if normal {
        if scalable == 0 {
            0.0
        } else {
            (length - fixed as f32) / scalable as f32
        }
    } else if fixed == 0 {
        0.0
    } else {
        length / fixed as f32
    };
    let capacity = count.checked_add(2).ok_or(Error::Capacity)?;
    if capacity > 65_538 {
        return Err(Error::Capacity);
    }
    let mut source = Vec::new();
    let mut destination = Vec::new();
    source
        .try_reserve_exact(capacity)
        .map_err(|_| Error::Capacity)?;
    destination
        .try_reserve_exact(capacity)
        .map_err(|_| Error::Capacity)?;
    source.push(start);
    destination.push(near);
    let mut stretch = skipped;
    // Original cumulative endpoints depend on the preceding rounded endpoint.
    for i in 0..count {
        source.push(divs[offset + i]);
        let delta = (source[i + 1] - source[i]) as f32;
        let delta = if normal {
            if stretch { scale * delta } else { delta }
        } else if stretch {
            0.0
        } else {
            scale * delta
        };
        destination.push(destination[i] + delta);
        stretch = !stretch;
    }
    source.push(end);
    destination.push(far);
    Ok(Dimension {
        source,
        destination,
        skipped,
    })
}
fn nine_dimension(length: i32, first: i32, last: i32, near: f32, far: f32) -> [f32; 4] {
    let mut result = [near, near + first as f32, far - (length - last) as f32, far];
    if result[1] > result[2] {
        result[1] = near + (far - near) * first as f32 / (length - (last - first)) as f32;
        result[2] = result[1];
    }
    result
}
fn patches(
    x: &Dimension,
    y: &Dimension,
    tags: Option<&[ImageLatticeRectType]>,
    colors: Option<&[[u8; 4]]>,
    original_columns: usize,
    filter: Option<&ColorFilter>,
    bounds: [f32; 4],
) -> Result<ImagePatchBatch, Error> {
    let columns = x.source.len() - 1;
    let rows = y.source.len() - 1;
    let cells = columns.checked_mul(rows).ok_or(Error::Capacity)?;
    if cells > 1_048_576 {
        return Err(Error::Capacity);
    }
    let index = |ix: usize, iy: usize| -> Result<usize, Error> {
        (iy + usize::from(y.skipped))
            .checked_mul(original_columns)
            .and_then(|v| v.checked_add(ix + usize::from(x.skipped)))
            .ok_or(Error::Capacity)
    };
    let mut count = 0;
    for iy in 0..rows {
        for ix in 0..columns {
            if x.destination[ix] == x.destination[ix + 1]
                || y.destination[iy] == y.destination[iy + 1]
            {
                continue;
            }
            let i = index(ix, iy)?;
            if tags.and_then(|t| t.get(i)).copied() != Some(ImageLatticeRectType::TRANSPARENT) {
                count += 1;
            }
        }
    }
    if count > 65_536 {
        return Err(Error::Capacity);
    }
    let mut output = Vec::new();
    output
        .try_reserve_exact(count)
        .map_err(|_| Error::Capacity)?;
    for iy in 0..rows {
        for ix in 0..columns {
            if x.destination[ix] == x.destination[ix + 1]
                || y.destination[iy] == y.destination[iy + 1]
            {
                continue;
            }
            let i = index(ix, iy)?;
            let tag = tags.and_then(|t| t.get(i)).copied().unwrap_or_default();
            if tag == ImageLatticeRectType::TRANSPARENT {
                continue;
            }
            let destination = [
                x.destination[ix],
                y.destination[iy],
                x.destination[ix + 1] - x.destination[ix],
                y.destination[iy + 1] - y.destination[iy],
            ];
            let (source, kind) = if tag == ImageLatticeRectType::FIXED_COLOR {
                let color = colors.and_then(|c| c.get(i)).copied().unwrap_or([0; 4]);
                let color = filter.map_or(color, |f| f.apply_color(color));
                (
                    [0.0; 4],
                    ImagePatchKind::FixedColor(super::patch_batch::color_components(color)),
                )
            } else {
                (
                    [
                        x.source[ix] as f32,
                        y.source[iy] as f32,
                        (x.source[ix + 1] - x.source[ix]) as f32,
                        (y.source[iy + 1] - y.source[iy]) as f32,
                    ],
                    ImagePatchKind::Texture,
                )
            };
            output.push(ImagePatch {
                source,
                destination,
                transform: Affine::IDENTITY,
                kind,
            });
        }
    }
    Ok(ImagePatchBatch::new(output, bounds))
}
