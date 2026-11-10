//! Original scalar polynomial roots and endpoint admission, without FMA.
#[derive(Clone, Copy, Default)]
pub(super) struct Roots {
    pub values: [f32; 3],
    pub count: usize,
}
pub(super) fn quadratic(a: f32, b: f32, c: f32) -> Roots {
    let mut result = Roots::default();
    if a.abs() < 0.00001 {
        if b.abs() > 0.00001 {
            result.values[0] = -c / b;
            result.count = 1;
        }
        return result;
    }
    let discriminant = b * b - 4.0 * a * c;
    if discriminant == 0.0 {
        result.values[0] = -b / (2.0 * a);
        result.count = 1;
    } else if discriminant > 0.0 {
        let root = discriminant.sqrt();
        result.values[0] = (-b - root) / (2.0 * a);
        result.values[1] = (-b + root) / (2.0 * a);
        result.count = 2;
    }
    result
}
fn shader_cbrt(value: f32) -> f32 {
    if value < 0.0 {
        -(-value).powf(1.0 / 3.0)
    } else {
        value.powf(1.0 / 3.0)
    }
}
pub(super) fn cubic(a_in: f32, b_in: f32, c_in: f32, d_in: f32) -> Roots {
    if a_in.abs() < 0.00001 {
        return quadratic(b_in, c_in, d_in);
    }
    let mut result = Roots::default();
    let a = b_in / a_in;
    let b = c_in / a_in;
    let c = d_in / a_in;
    let p = b - a * a / 3.0;
    let q = c - a * b / 3.0 + 2.0 * a * a * a / 27.0;
    let discriminant = q * q / 4.0 + p * p * p / 27.0;
    if discriminant > 0.0 {
        let root = discriminant.sqrt();
        let u = shader_cbrt(-q / 2.0 + root);
        let v = shader_cbrt(-q / 2.0 - root);
        result.values[0] = u + v - a / 3.0;
        result.count = 1;
    } else if p < 0.0 {
        let pi = 3.14159265359_f32;
        let radius = 2.0 * (-p / 3.0).sqrt();
        let ratio = (-q / (2.0 * (-p * p * p / 27.0).sqrt())).clamp(-1.0, 1.0);
        let theta = ratio.acos();
        result.values[0] = radius * (theta / 3.0).cos() - a / 3.0;
        result.values[1] = radius * ((theta + 2.0 * pi) / 3.0).cos() - a / 3.0;
        result.values[2] = radius * ((theta + 4.0 * pi) / 3.0).cos() - a / 3.0;
        result.count = 3;
    } else {
        result.values[0] = -a / 3.0;
        result.count = 1;
    }
    result
}
pub(super) fn valid(t: f32, derivative_y: f32, sample_y: f32, start_y: f32, end_y: f32) -> bool {
    if t < 0.005 {
        if derivative_y > 0.0 {
            sample_y >= start_y
        } else {
            derivative_y < 0.0 && sample_y < start_y
        }
    } else if t > 0.995 {
        if derivative_y > 0.0 {
            sample_y < end_y
        } else {
            derivative_y < 0.0 && sample_y >= end_y
        }
    } else {
        true
    }
}
