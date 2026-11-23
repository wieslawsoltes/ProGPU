//! Original managed compositor material registration and exact shader records.
//! Evaluation stays in the canonical GPU shader; these tables are retained data.
use super::*;
use bytemuck::{Pod, Zeroable};
use std::{collections::HashMap, sync::Arc};

/// Exact 256-byte original registered-material wire record.
#[repr(C)]
#[derive(Clone, Copy, Debug, Default, PartialEq, Pod, Zeroable)]
pub struct GpuBrush {
    /// Original kind: solid 0, linear 1, radial 2, hatch 3/4, conical 5,
    /// sweep 6, noise 7, hatch set 8, one-bit tile 9, path gradient 10.
    pub kind: u32,
    /// Original brush opacity multiplied once by semantic opacity.
    pub opacity: f32,
    /// Original first point or kind-specific frequency/angle payload.
    pub start: [f32; 2],
    /// Original second point or kind-specific stitch/focus payload.
    pub end: [f32; 2],
    /// Original center or kind-specific dimensions.
    pub center: [f32; 2],
    /// Original radius or kind-specific scalar/count.
    pub radius: f32,
    /// Original record count, octave count or low tile mask word.
    pub stop_count: u32,
    /// Original second radius or path curve count.
    pub radius_y: f32,
    /// Original spread plus independent flags, or kind-specific count.
    pub spread: u32,
    /// Original color interpolation or noise-table selection.
    pub interpolation: u32,
    /// Original auxiliary offset, or high tile mask word.
    pub stop_offset: u32,
    /// Explicit zero padding for the original 16-byte vector alignment.
    pub padding: [u32; 2],
    /// Original eight inline colors (also kind-specific payload).
    pub colors: [[f32; 4]; 8],
    /// Original eight inline offsets and source missing-stop defaults.
    pub offsets: [[f32; 4]; 2],
    /// Original two XY affine rows, without an implicit inverse.
    pub coordinate_transform: [[f32; 4]; 2],
}
/// Exact 32-byte original auxiliary record; stops, hatches, paths and noise
/// each retain their distinct source interpretation of color/offset.
#[repr(C)]
#[derive(Clone, Copy, Debug, Default, PartialEq, Pod, Zeroable)]
pub struct GpuGradientStop {
    /// Original four-component payload.
    pub color: [f32; 4],
    /// Original scalar payload.
    pub offset: f32,
    /// Explicit original zero tail padding.
    pub padding: [u32; 3],
}
const _: () = {
    assert!(size_of::<GpuBrush>() == 256);
    assert!(std::mem::offset_of!(GpuBrush, colors) == 64);
    assert!(std::mem::offset_of!(GpuBrush, offsets) == 192);
    assert!(std::mem::offset_of!(GpuBrush, coordinate_transform) == 224);
    assert!(size_of::<GpuGradientStop>() == 32);
    assert!(std::mem::offset_of!(GpuGradientStop, offset) == 16);
};
impl GpuBrush {
    fn transform(&mut self, matrix: Matrix4) {
        let m = matrix.0;
        self.coordinate_transform = [
            [m[0][0], m[1][0], m[3][0], 0.0],
            [m[0][1], m[1][1], m[3][1], 0.0],
        ];
    }
    fn gradient_options(&mut self, options: GradientOptions) {
        self.transform(options.transform);
        self.spread = options.spread as u32;
        self.interpolation = options.interpolation as u32;
    }
    fn equal_fields(self, other: Self) -> bool {
        // Source equality ignores storage offsets, except the tile's high word.
        // NaNs remain unequal; signed zeros retain ordinary float equality.
        let mut a = self;
        let mut b = other;
        if a.kind != 9 {
            a.stop_offset = 0;
            b.stop_offset = 0;
        }
        a == b
    }
}
/// Immutable material generation shared by CPU compilation and GPU preparation.
#[derive(Clone, Debug, Default)]
pub struct MaterialPage {
    brushes: Arc<[GpuBrush]>,
    records: Arc<[GpuGradientStop]>,
}
impl MaterialPage {
    /// Complete original ordered brush records.
    pub fn brushes(&self) -> &[GpuBrush] {
        &self.brushes
    }
    /// Complete original ordered auxiliary records.
    pub fn records(&self) -> &[GpuGradientStop] {
        &self.records
    }
    /// Original logical byte payload, excluding binding placeholders.
    pub fn payload_bytes(&self) -> usize {
        self.brushes.len() * size_of::<GpuBrush>()
            + self.records.len() * size_of::<GpuGradientStop>()
    }
}
/// Original per-frame registration with retained bounded noise-table cache.
/// Registration preserves the original first-equal linear scan and fallback
/// index zero at capacity. It does not silently sort or normalize gradient stops.
#[derive(Debug, Default)]
pub struct MaterialTable {
    brushes: Vec<GpuBrush>,
    records: Vec<GpuGradientStop>,
    noise_tables: HashMap<i32, Arc<[GpuGradientStop]>>,
}
impl MaterialTable {
    /// Original compositor brush limit; indices are exactly representable f32.
    pub const MAX_BRUSHES: usize = 8192;
    /// Original shared auxiliary-record limit.
    pub const MAX_RECORDS: usize = 65536;
    /// Original maximum retained seed tables before whole-cache clearing.
    pub const MAX_NOISE_TABLES: usize = 16;
    /// Clear frame records while retaining allocations and selected seed tables.
    pub fn clear(&mut self) {
        self.brushes.clear();
        self.records.clear();
    }
    /// Complete current original brush inventory.
    pub fn brushes(&self) -> &[GpuBrush] {
        &self.brushes
    }
    /// Complete current original auxiliary inventory.
    pub fn records(&self) -> &[GpuGradientStop] {
        &self.records
    }
    /// Actual retained normalized-seed table count.
    pub fn cached_noise_tables(&self) -> usize {
        self.noise_tables.len()
    }
    /// Copy one immutable generation; later clear/registration cannot mutate it.
    pub fn snapshot(&self) -> Result<MaterialPage, Error> {
        Ok(MaterialPage {
            brushes: copy_owned(&self.brushes)?,
            records: copy_owned(&self.records)?,
        })
    }
    /// Register one original brush with exact semantic opacity. None returns the
    /// source index zero without creating a record. Source capacity fallback and
    /// invalid path-gradient transparency remain observable; allocation errors
    /// roll back this request's auxiliary tail before returning an error.
    pub fn register(&mut self, brush: Option<&Brush>, semantic_opacity: f32) -> Result<u32, Error> {
        let Some(brush) = brush else { return Ok(0) };
        let start = self.records.len();
        let result = self.register_inner(brush, semantic_opacity, start);
        if result.is_err() {
            self.records.truncate(start);
        }
        result
    }
    fn register_inner(
        &mut self,
        brush: &Brush,
        semantic_opacity: f32,
        record_start: usize,
    ) -> Result<u32, Error> {
        let mut gpu = GpuBrush {
            opacity: brush.opacity * semantic_opacity,
            ..Default::default()
        };
        gpu.transform(Matrix4::IDENTITY);
        let mut noise_seed = None;
        match &brush.kind {
            BrushKind::Solid(color) => {
                gpu.colors[0] = *color;
            }
            BrushKind::Linear(gradient) => {
                gpu.kind = 1;
                gpu.start = gradient.start;
                gpu.end = gradient.end;
                gpu.gradient_options(gradient.options);
                self.gradient_stops(&mut gpu, &gradient.stops)?;
            }
            BrushKind::Radial(gradient) => {
                gpu.kind = 2;
                gpu.start = gradient.origin;
                gpu.center = gradient.center;
                gpu.radius = gradient.radii[0];
                gpu.radius_y = gradient.radii[1];
                gpu.gradient_options(gradient.options);
                self.gradient_stops(&mut gpu, &gradient.stops)?;
            }
            BrushKind::Path(gradient) => {
                gpu.kind = 10;
                gpu.center = gradient.center;
                gpu.end = gradient.focus_scales;
                gpu.colors[0] = gradient.center_color;
                gpu.colors[1][0] = f32::from(gradient.uses_preset_colors());
                gpu.gradient_options(gradient.options);
                if !self.path_gradient(&mut gpu, gradient)? {
                    gpu = GpuBrush::default();
                    gpu.transform(Matrix4::IDENTITY);
                }
            }
            BrushKind::TwoPointConical(gradient) => {
                gpu.kind = 5;
                gpu.start = gradient.start_center;
                gpu.center = gradient.end_center;
                gpu.radius = gradient.start_radius;
                gpu.radius_y = gradient.end_radius;
                gpu.gradient_options(gradient.options);
                self.gradient_stops(&mut gpu, &gradient.stops)?;
                if let Some(color) = gradient.outside_color {
                    gpu.spread |= 0x8000_0000;
                    gpu.colors[0] = color;
                }
            }
            BrushKind::Sweep(gradient) => {
                gpu.kind = 6;
                gpu.center = gradient.center;
                gpu.start = [gradient.start_angle, gradient.end_angle];
                gpu.gradient_options(gradient.options);
                self.gradient_stops(&mut gpu, &gradient.stops)?;
            }
            BrushKind::PerlinNoise(noise) => {
                noise_seed = Some(noise.seed);
                gpu.kind = 7;
                gpu.start = noise_frequency(noise.base_frequency, noise.tile_size);
                gpu.end = noise_stitch(gpu.start, noise.tile_size);
                gpu.center = noise.tile_size;
                gpu.radius = normalize_seed(noise.seed) as f32;
                gpu.stop_count = noise.octaves.clamp(0, 255) as u32;
                gpu.spread = u32::from(noise.turbulence);
                gpu.interpolation = 1;
                gpu.transform(noise.transform);
            }
            BrushKind::Hatch(hatch) | BrushKind::CrossHatch(hatch) => {
                gpu.kind = if matches!(brush.kind, BrushKind::Hatch(_)) {
                    3
                } else {
                    4
                };
                gpu.radius = hatch.angle;
                gpu.center = [hatch.spacing, hatch.thickness];
                gpu.colors[0] = hatch.color;
                gpu.stop_count = 1;
                gpu.transform(hatch.transform);
            }
            BrushKind::HatchSet(hatch) => {
                gpu.kind = 8;
                gpu.radius = hatch.thickness;
                gpu.colors[0] = hatch.color;
                gpu.spread = u32::try_from(hatch.families().len()).map_err(|_| Error::Capacity)?;
                gpu.transform(hatch.transform);
                if !self.hatch_set(&mut gpu, hatch)? {
                    self.records.truncate(record_start);
                    return Ok(0);
                }
            }
            BrushKind::Tile(tile) => {
                gpu.kind = 9;
                gpu.stop_count = tile.pattern() as u32;
                gpu.stop_offset = (tile.pattern() >> 32) as u32;
                gpu.colors[0] = tile.foreground();
                gpu.colors[1] = tile.background();
                let [x, y] = tile.origin();
                gpu.coordinate_transform = [[1.0, 0.0, -x, 0.0], [0.0, 1.0, -y, 0.0]];
            }
        }
        for (index, existing) in self.brushes.iter().enumerate() {
            if existing.equal_fields(gpu)
                && (!matches!(gpu.kind, 1 | 2 | 5 | 6 | 8 | 10)
                    || self.equal_records(*existing, gpu))
            {
                self.records.truncate(record_start);
                return Ok(index as u32);
            }
        }
        if let Some(seed) = noise_seed
            && !self.noise_table(&mut gpu, seed)?
        {
            gpu.interpolation = 0;
        }
        if self.brushes.len() < Self::MAX_BRUSHES {
            self.brushes.try_reserve(1).map_err(|_| Error::Capacity)?;
            let index = self.brushes.len() as u32;
            self.brushes.push(gpu);
            Ok(index)
        } else {
            self.records.truncate(record_start);
            Ok(0)
        }
    }
    fn equal_records(&self, a: GpuBrush, b: GpuBrush) -> bool {
        let count = a.stop_count as usize;
        self.records[a.stop_offset as usize..a.stop_offset as usize + count]
            == self.records[b.stop_offset as usize..b.stop_offset as usize + count]
    }
    fn reserve_records(&mut self, count: usize) -> Result<(), Error> {
        self.records.try_reserve(count).map_err(|_| Error::Capacity)
    }
    fn record(&mut self, color: [f32; 4], offset: f32) {
        self.records.push(GpuGradientStop {
            color,
            offset,
            padding: [0; 3],
        });
    }
    fn gradient_stops(&mut self, gpu: &mut GpuBrush, stops: &[GradientStop]) -> Result<(), Error> {
        let count = stops.len().min(Self::MAX_RECORDS - self.records.len());
        self.reserve_records(count)?;
        gpu.stop_offset = self.records.len() as u32;
        gpu.stop_count = count as u32;
        for stop in &stops[..count] {
            self.record(stop.color, stop.offset);
        }
        gpu.offsets = [[0.0, 1.0, 1.0, 1.0], [1.0; 4]];
        for (index, stop) in stops[..count.min(8)].iter().enumerate() {
            gpu.colors[index] = stop.color;
            gpu.offsets[index / 4][index % 4] = stop.offset;
        }
        Ok(())
    }
    fn hatch_set(&mut self, gpu: &mut GpuBrush, hatch: &HatchPatternSet) -> Result<bool, Error> {
        let count = hatch
            .families()
            .len()
            .checked_mul(4)
            .ok_or(Error::Capacity)?;
        if count > Self::MAX_RECORDS - self.records.len() {
            return Ok(false);
        }
        self.reserve_records(count)?;
        gpu.stop_offset = self.records.len() as u32;
        gpu.stop_count = count as u32;
        for family in hatch.families() {
            let mut dashes = [0.0; 6];
            let start = family.dash_offset as usize;
            let count = family.dash_count as usize;
            dashes[..count].copy_from_slice(&hatch.dashes()[start..start + count]);
            self.record(
                [
                    family.base[0],
                    family.base[1],
                    family.direction[0],
                    family.direction[1],
                ],
                family.spacing,
            );
            self.record(
                [
                    family.tangent_shift,
                    family.dash_period,
                    family.dash_count as f32,
                    0.0,
                ],
                0.0,
            );
            self.record([dashes[0], dashes[1], dashes[2], dashes[3]], dashes[4]);
            self.record([dashes[5], 0.0, 0.0, 0.0], 0.0);
        }
        Ok(true)
    }
    fn path_gradient(
        &mut self,
        gpu: &mut GpuBrush,
        gradient: &PathGradient,
    ) -> Result<bool, Error> {
        let points = gradient.boundary();
        let colors = gradient.surround_colors();
        let curve_count = if gradient.uses_preset_colors() {
            gradient.preset_stops().len()
        } else {
            gradient.blend_stops().len()
        };
        let count = points
            .len()
            .checked_mul(2)
            .and_then(|n| n.checked_add(curve_count))
            .ok_or(Error::Capacity)?;
        if !finite(gradient.center)
            || !finite4(gradient.center_color)
            || !finite(gradient.focus_scales)
            || !invertible_affine(gradient.options.transform)
            || !(2..=PathGradient::MAX_BOUNDARY_POINTS).contains(&points.len())
            || colors.len() != points.len()
            || curve_count == 0
            || count > Self::MAX_RECORDS - self.records.len()
            || !points.iter().copied().all(finite)
            || !colors.iter().copied().all(finite4)
        {
            return Ok(false);
        }
        let mut prior = f32::NEG_INFINITY;
        if gradient.uses_preset_colors() {
            for stop in gradient.preset_stops() {
                if !finite4(stop.color) || !curve_offset(stop.offset, prior) {
                    return Ok(false);
                }
                prior = stop.offset;
            }
        } else {
            for stop in gradient.blend_stops() {
                if !stop.factor.is_finite()
                    || !(0.0..=1.0).contains(&stop.factor)
                    || !curve_offset(stop.offset, prior)
                {
                    return Ok(false);
                }
                prior = stop.offset;
            }
        }
        self.reserve_records(count)?;
        gpu.stop_offset = self.records.len() as u32;
        gpu.stop_count = count as u32;
        gpu.radius = points.len() as f32;
        gpu.radius_y = curve_count as f32;
        for (point, color) in points.iter().zip(colors) {
            self.record([point[0], point[1], 0.0, 0.0], 0.0);
            self.record(*color, 0.0);
        }
        if gradient.uses_preset_colors() {
            for stop in gradient.preset_stops() {
                self.record(stop.color, stop.offset);
            }
        } else {
            for stop in gradient.blend_stops() {
                self.record([stop.factor, 0.0, 0.0, 0.0], stop.offset);
            }
        }
        Ok(true)
    }
    fn noise_table(&mut self, gpu: &mut GpuBrush, seed: f32) -> Result<bool, Error> {
        const COUNT: usize = 512;
        if Self::MAX_RECORDS - self.records.len() < COUNT {
            return Ok(false);
        }
        let seed = normalize_seed(seed);
        if !self.noise_tables.contains_key(&seed) {
            if self.noise_tables.len() >= Self::MAX_NOISE_TABLES {
                self.noise_tables.clear();
            }
            let table = create_noise_table(seed)?;
            self.noise_tables
                .try_reserve(1)
                .map_err(|_| Error::Capacity)?;
            self.noise_tables.insert(seed, table);
        }
        self.reserve_records(COUNT)?;
        gpu.stop_offset = self.records.len() as u32;
        self.records.extend_from_slice(&self.noise_tables[&seed]);
        Ok(true)
    }
}
fn copy_owned<T: Copy>(source: &[T]) -> Result<Arc<[T]>, Error> {
    let mut values = Vec::new();
    values
        .try_reserve_exact(source.len())
        .map_err(|_| Error::Capacity)?;
    values.extend_from_slice(source);
    Ok(values.into())
}
fn finite4(value: [f32; 4]) -> bool {
    value.iter().all(|v| v.is_finite())
}
fn curve_offset(value: f32, previous: f32) -> bool {
    value.is_finite() && (0.0..=1.0).contains(&value) && value >= previous
}
fn invertible_affine(matrix: Matrix4) -> bool {
    let m = matrix.0;
    let epsilon = 0.0001;
    // Original admission deliberately does not inspect M33 or M43.
    if ![m[0][0], m[0][1], m[1][0], m[1][1], m[3][0], m[3][1]]
        .iter()
        .all(|v| v.is_finite())
        || ![
            m[0][2],
            m[0][3],
            m[1][2],
            m[1][3],
            m[2][0],
            m[2][1],
            m[2][3],
            m[3][3] - 1.0,
        ]
        .iter()
        .all(|v| v.abs() <= epsilon)
    {
        return false;
    }
    let determinant = m[0][0] * m[1][1] - m[0][1] * m[1][0];
    determinant.is_finite() && determinant != 0.0
}
fn normalize_seed(value: f32) -> i32 {
    const MAXIMUM: i64 = i32::MAX as i64 - 1;
    let truncated = f64::from(value).trunc();
    let mut seed = if truncated >= f64::from(i32::MAX) {
        i64::from(i32::MAX)
    } else if truncated <= f64::from(i32::MIN) {
        i64::from(i32::MIN)
    } else {
        truncated as i64
    };
    if seed <= 0 {
        seed = -(seed % MAXIMUM) + 1;
    }
    if seed > MAXIMUM {
        seed = MAXIMUM;
    }
    seed as i32
}
fn next_random(seed: &mut i32) -> i32 {
    let mut result = 16807 * (*seed % 127773) - 2836 * (*seed / 127773);
    if result <= 0 {
        result += i32::MAX;
    }
    *seed = result;
    result
}
fn create_noise_table(mut seed: i32) -> Result<Arc<[GpuGradientStop]>, Error> {
    let mut lattice = [0u8; 256];
    let mut noise = [[0u16; 512]; 4];
    for channel in &mut noise {
        for (index, selector) in lattice.iter_mut().enumerate() {
            *selector = index as u8;
            channel[index * 2] = (next_random(&mut seed) % 512) as u16;
            channel[index * 2 + 1] = (next_random(&mut seed) % 512) as u16;
        }
    }
    for index in (1..256).rev() {
        let selected = (next_random(&mut seed) % 256) as usize;
        lattice.swap(index, selected);
    }
    for channel in &mut noise {
        let source = *channel;
        for (index, selector) in lattice.iter().enumerate() {
            let source_index = usize::from(*selector) * 2;
            channel[index * 2] = source[source_index];
            channel[index * 2 + 1] = source[source_index + 1];
        }
    }
    let mut table = Vec::new();
    table.try_reserve_exact(512).map_err(|_| Error::Capacity)?;
    for (index, selector) in lattice.iter().enumerate() {
        let g = noise
            .each_ref()
            .map(|channel| noise_gradient(channel[index * 2], channel[index * 2 + 1]));
        table.push(GpuGradientStop {
            color: [g[0][0], g[0][1], g[1][0], g[1][1]],
            offset: f32::from(*selector),
            padding: [0; 3],
        });
        table.push(GpuGradientStop {
            color: [g[2][0], g[2][1], g[3][0], g[3][1]],
            ..Default::default()
        });
    }
    Ok(table.into())
}
fn noise_gradient(x: u16, y: u16) -> [f32; 2] {
    let mut gradient = [
        (f32::from(x) - 256.0) / 256.0,
        (f32::from(y) - 256.0) / 256.0,
    ];
    let size = length(gradient);
    if size > f32::from_bits(1) {
        gradient = [gradient[0] / size, gradient[1] / size];
    }
    gradient.map(|v| {
        let encoded = (((v + 1.0) * 32767.5 + 0.5).floor() as i32).clamp(0, i32::from(u16::MAX));
        encoded as f32 * (2.0 / f32::from(u16::MAX)) - 1.0
    })
}
fn noise_frequency(frequency: [f32; 2], tile: [f32; 2]) -> [f32; 2] {
    if tile[0] <= 0.0 || tile[1] <= 0.0 {
        return frequency;
    }
    std::array::from_fn(|axis| {
        let frequency = frequency[axis];
        if frequency == 0.0 {
            return 0.0;
        }
        let low = (tile[axis] * frequency).floor() / tile[axis];
        let high = (tile[axis] * frequency).ceil() / tile[axis];
        if frequency / low < high / frequency {
            low
        } else {
            high
        }
    })
}
fn noise_stitch(frequency: [f32; 2], tile: [f32; 2]) -> [f32; 2] {
    if tile[0] <= 0.0 || tile[1] <= 0.0 {
        return [0.0; 2];
    }
    [
        (tile[0] * frequency[0] + 0.5).floor(),
        (tile[1] * frequency[1] + 0.5).floor(),
    ]
}
