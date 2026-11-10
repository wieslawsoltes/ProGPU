//! Safe SIMD ownership of the original independent four-lane masks. No unsafe
//! access or fused arithmetic; sample lanes retain their scalar expressions.
use super::*;
use wide::{CmpEq, CmpLt, f32x4, i32x4};
pub(super) struct Winding<const GROUPS: usize> {
    samples: [f32x4; GROUPS],
    winding: [i32x4; GROUPS],
}
fn samples(x: f32, step: f32) -> [f32x4; 2] {
    [
        f32x4::from([x, x + step, x + step * 2.0, x + step * 3.0]),
        f32x4::from([
            x + step * 4.0,
            x + step * 5.0,
            x + step * 6.0,
            x + step * 7.0,
        ]),
    ]
}
impl Winding<4> {
    pub fn pair(first: f32, second: f32, step: f32) -> Self {
        let a = samples(first, step);
        let b = samples(second, step);
        Self {
            samples: [a[0], a[1], b[0], b[1]],
            winding: [i32x4::ZERO; 4],
        }
    }
    pub fn covered_pair(&self) -> [u32; 2] {
        [
            count(self.winding[0], self.winding[1]),
            count(self.winding[2], self.winding[3]),
        ]
    }
}
impl Winding<2> {
    pub fn single(x: f32, step: f32) -> Self {
        Self {
            samples: samples(x, step),
            winding: [i32x4::ZERO; 2],
        }
    }
    pub fn covered(&self) -> u32 {
        count(self.winding[0], self.winding[1])
    }
}
impl<const GROUPS: usize> Winding<GROUPS> {
    pub fn reset(&mut self) {
        self.winding.fill(i32x4::ZERO);
    }
    pub fn add(&mut self, crossing_x: f32, direction: i32) {
        let crossing = f32x4::splat(crossing_x);
        for index in 0..GROUPS {
            let mask: i32x4 = bytemuck::cast(self.samples[index].cmp_lt(crossing));
            self.winding[index] = if direction > 0 {
                self.winding[index] - mask
            } else {
                self.winding[index] + mask
            };
        }
    }
}
fn count(low: i32x4, high: i32x4) -> u32 {
    if cfg!(any(
        target_feature = "neon",
        all(target_arch = "aarch64", target_os = "windows")
    )) {
        // Original NEON low+high, low-half+high-half, pairwise sum order.
        let low = low
            .cmp_eq(i32x4::ZERO)
            .to_array()
            .map(|v| ((!v) as u32) >> 31);
        let high = high
            .cmp_eq(i32x4::ZERO)
            .to_array()
            .map(|v| ((!v) as u32) >> 31);
        let combined = std::array::from_fn::<_, 4, _>(|i| low[i] + high[i]);
        (combined[0] + combined[2]) + (combined[1] + combined[3])
    } else {
        let zero_mask =
            low.cmp_eq(i32x4::ZERO).move_mask() | (high.cmp_eq(i32x4::ZERO).move_mask() << 4);
        8 - (zero_mask as u32).count_ones()
    }
}
/// The original compile-time neither-ISA branch deliberately keeps its own
/// first sample expression `base + sample_index * step`, including sample zero.
pub(super) fn cached_scalar(
    first: f32,
    second: f32,
    step: f32,
    crossings: &[Crossing],
) -> [u32; 2] {
    let mut covered = [0u32; 2];
    for pixel in 0..2 {
        for sample in 0..8 {
            let position = (if pixel == 0 { first } else { second }) + sample as f32 * step;
            let mut winding = 0i32;
            for crossing in crossings {
                winding += if position < crossing.x {
                    crossing.direction
                } else {
                    0
                };
            }
            covered[pixel] += u32::from(winding != 0);
        }
    }
    covered
}
