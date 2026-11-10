//! Original independent XY and homogeneous de Casteljau expressions.
use super::*;
#[derive(Clone, Copy)]
pub(super) struct Curve {
    points: [[f32; 2]; 4],
    kind: u8,
    weight: f32,
}
fn lerp(a: V, b: V, t: f32) -> V {
    a * V::splat(1.0 - t) + b * V::splat(t)
}
fn split_quad(p: [V; 3], t: f32) -> ([V; 3], [V; 3]) {
    let a = lerp(p[0], p[1], t);
    let b = lerp(p[1], p[2], t);
    let c = lerp(a, b, t);
    ([p[0], a, c], [c, b, p[2]])
}
fn split_cubic(p: [V; 4], t: f32) -> ([V; 4], [V; 4]) {
    let a = lerp(p[0], p[1], t);
    let b = lerp(p[1], p[2], t);
    let c = lerp(p[2], p[3], t);
    let d = lerp(a, b, t);
    let e = lerp(b, c, t);
    let f = lerp(d, e, t);
    ([p[0], a, d, f], [f, e, c, p[3]])
}
impl Curve {
    pub fn line(a: [f32; 2], b: [f32; 2]) -> Self {
        Self {
            points: [a, b, [0.0; 2], [0.0; 2]],
            kind: 0,
            weight: 1.0,
        }
    }
    pub fn from_command(start: [f32; 2], command: MeasureCommand) -> Option<Self> {
        Some(match command {
            MeasureCommand::Line(end) => Self::line(start, end),
            MeasureCommand::Quadratic([a, b]) => Self {
                points: [start, a, b, [0.0; 2]],
                kind: 1,
                weight: 1.0,
            },
            MeasureCommand::Conic {
                points: [a, b],
                weight,
            } => Self {
                points: [start, a, b, [0.0; 2]],
                kind: 2,
                weight,
            },
            MeasureCommand::Cubic([a, b, c]) => Self {
                points: [start, a, b, c],
                kind: 3,
                weight: 1.0,
            },
            _ => return None,
        })
    }
    pub fn start(self) -> [f32; 2] {
        self.points[0]
    }
    pub fn end(self) -> [f32; 2] {
        self.points[match self.kind {
            0 => 1,
            1 | 2 => 2,
            _ => 3,
        }]
    }
    pub fn evaluate(self, t: f32) -> [f32; 2] {
        let t = t.clamp(0.0, 1.0);
        let u = 1.0 - t;
        let [a, b, c, d] = self.points.map(vector);
        xy(match self.kind {
            0 => lerp(a, b, t),
            1 => V::splat(u * u) * a + V::splat(2.0 * u * t) * b + V::splat(t * t) * c,
            2 => {
                let b0 = u * u;
                let b1 = 2.0 * self.weight * u * t;
                let b2 = t * t;
                let denominator = b0 + b1 + b2;
                if denominator.abs() <= EPS {
                    c
                } else {
                    (V::splat(b0) * a + V::splat(b1) * b + V::splat(b2) * c) / V::splat(denominator)
                }
            }
            _ => {
                V::splat(u * u * u) * a
                    + V::splat(3.0 * u * u * t) * b
                    + V::splat(3.0 * u * t * t) * c
                    + V::splat(t * t * t) * d
            }
        })
    }
    pub fn tangent(self, t: f32) -> [f32; 2] {
        let t = t.clamp(0.0, 1.0);
        let u = 1.0 - t;
        let [a, b, c, d] = self.points.map(vector);
        xy(match self.kind {
            0 => b - a,
            1 => V::splat(2.0) * (V::splat(u) * (b - a) + V::splat(t) * (c - b)),
            2 => {
                let w = self.weight;
                let n = V::splat(u * u) * a + V::splat(2.0 * w * u * t) * b + V::splat(t * t) * c;
                let denominator = u * u + 2.0 * w * u * t + t * t;
                if denominator.abs() <= EPS {
                    V::ZERO
                } else {
                    let derivative = V::splat(2.0)
                        * (V::splat(u) * (V::splat(w) * b - a)
                            + V::splat(t) * (c - V::splat(w) * b));
                    let d = 2.0 * (u * (w - 1.0) + t * (1.0 - w));
                    (derivative * V::splat(denominator) - n * V::splat(d))
                        / V::splat(denominator * denominator)
                }
            }
            _ => {
                V::splat(3.0 * u * u) * (b - a)
                    + V::splat(6.0 * u * t) * (c - b)
                    + V::splat(3.0 * t * t) * (d - c)
            }
        })
    }
    fn full(self) -> MeasureCommand {
        match self.kind {
            0 => MeasureCommand::Line(self.points[1]),
            1 => MeasureCommand::Quadratic([self.points[1], self.points[2]]),
            2 => MeasureCommand::Conic {
                points: [self.points[1], self.points[2]],
                weight: self.weight,
            },
            _ => MeasureCommand::Cubic([self.points[1], self.points[2], self.points[3]]),
        }
    }
    pub fn slice(self, start: f32, stop: f32) -> MeasureCommand {
        let start = start.clamp(0.0, 1.0);
        let stop = stop.clamp(0.0, 1.0);
        if start <= EPS && stop >= 1.0 - EPS {
            return self.full();
        }
        match self.kind {
            0 => MeasureCommand::Line(self.evaluate(stop)),
            1 => {
                let left = split_quad(
                    [
                        vector(self.points[0]),
                        vector(self.points[1]),
                        vector(self.points[2]),
                    ],
                    stop,
                )
                .0;
                let selected = if start <= EPS {
                    left
                } else {
                    split_quad(left, start / stop).1
                };
                MeasureCommand::Quadratic([xy(selected[1]), xy(selected[2])])
            }
            2 => {
                let p = self.points;
                let w = self.weight;
                let h0 = V::from([p[0][0], p[0][1], 1.0, 0.0]);
                let h1 = V::from([p[1][0] * w, p[1][1] * w, w, 0.0]);
                let h2 = V::from([p[2][0], p[2][1], 1.0, 0.0]);
                let left = split_quad([h0, h1, h2], stop).0;
                let selected = if start <= EPS {
                    left
                } else {
                    split_quad(left, start / stop).1
                };
                let [a, b, c] = selected.map(V::to_array);
                if a[2].abs() <= EPS || b[2].abs() <= EPS || c[2].abs() <= EPS || a[2] * c[2] <= 0.0
                {
                    return MeasureCommand::Line(self.evaluate(stop));
                }
                let q0 = xy(selected[0] / V::splat(a[2]));
                let q1 = xy(selected[1] / V::splat(b[2]));
                let q2 = xy(selected[2] / V::splat(c[2]));
                let weight = b[2] / (a[2] * c[2]).sqrt();
                if !weight.is_finite() || q0.iter().chain(&q1).chain(&q2).any(|v| !v.is_finite()) {
                    return MeasureCommand::Line(self.evaluate(stop));
                }
                MeasureCommand::Conic {
                    points: [q1, q2],
                    weight,
                }
            }
            _ => {
                let left = split_cubic(self.points.map(vector), stop).0;
                let selected = if start <= EPS {
                    left
                } else {
                    split_cubic(left, start / stop).1
                };
                MeasureCommand::Cubic([xy(selected[1]), xy(selected[2]), xy(selected[3])])
            }
        }
    }
}
