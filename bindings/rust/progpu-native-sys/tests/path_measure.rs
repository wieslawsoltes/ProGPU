//! The path measure of the engine (no device): the length, positions and pieces of a closed
//! rectangle and of a cubic quarter circle.

use progpu_native_sys::progpu::*;
use progpu_native_sys::Runtime;
use std::ptr;

fn point(x: f32, y: f32) -> progpu_native_point {
    progpu_native_point { x, y }
}

fn line(from: progpu_native_point, to: progpu_native_point) -> progpu_native_path_segment {
    progpu_native_path_segment {
        p0: from,
        p1: to,
        p2: point(0.0, 0.0),
        p3: point(0.0, 0.0),
        kind: PROGPU_NATIVE_PATH_SEGMENT_LINE as u32,
        pad0: 0,
        pad1: 0,
        pad2: 0,
    }
}

#[test]
fn the_measure_answers_lengths_positions_and_pieces() {
    let runtime = Runtime::load().expect("the ProGPU native runtime");
    let p = &runtime.progpu;
    let sides = [
        line(point(0.0, 0.0), point(100.0, 0.0)),
        line(point(100.0, 0.0), point(100.0, 50.0)),
        line(point(100.0, 50.0), point(0.0, 50.0)),
    ];
    let figure = progpu_native_geometry_query_figure { start: point(0.0, 0.0), first_segment: 0, segment_count: 3, flags: 1 };
    let k = 55.228_475_f32;
    let quarter = progpu_native_path_segment {
        p0: point(100.0, 0.0),
        p1: point(100.0, k),
        p2: point(k, 100.0),
        p3: point(0.0, 100.0),
        kind: PROGPU_NATIVE_PATH_SEGMENT_CUBIC as u32,
        pad0: 0,
        pad1: 0,
        pad2: 0,
    };
    let quarter_figure = progpu_native_geometry_query_figure { start: quarter.p0, first_segment: 0, segment_count: 1, flags: 0 };
    // SAFETY: the inputs are borrowed for each call; the measures are destroyed once.
    unsafe {
        let mut measure = ptr::null_mut();
        assert_eq!(
            p.progpu_native_path_measure_create(&figure, 1, sides.as_ptr(), 3, 0, 1.0, &mut measure),
            PROGPU_NATIVE_STATUS_SUCCESS
        );
        assert_eq!(p.progpu_native_path_measure_contour_count(measure), 1);
        let (mut length, mut closed) = (0.0_f32, 0_u32);
        assert_eq!(p.progpu_native_path_measure_contour(measure, 0, &mut length, &mut closed), PROGPU_NATIVE_STATUS_SUCCESS);
        assert_eq!((length, closed), (300.0, 1));
        let (mut position, mut tangent) = (point(0.0, 0.0), point(0.0, 0.0));
        assert_eq!(
            p.progpu_native_path_measure_position_tangent(measure, 0, 125.0, &mut position, &mut tangent),
            PROGPU_NATIVE_STATUS_SUCCESS
        );
        assert!((position.x - 100.0).abs() < 1e-4 && (position.y - 25.0).abs() < 1e-4);
        assert!(tangent.x.abs() < 1e-6 && (tangent.y - 1.0).abs() < 1e-6);
        let mut count = 0_u32;
        assert_eq!(
            p.progpu_native_path_measure_segment(measure, 0, 50.0, 250.0, ptr::null_mut(), 0, &mut count),
            PROGPU_NATIVE_STATUS_SUCCESS
        );
        assert_eq!(count, 3);
        let mut pieces = vec![line(point(0.0, 0.0), point(0.0, 0.0)); count as usize];
        assert_eq!(
            p.progpu_native_path_measure_segment(measure, 0, 50.0, 250.0, pieces.as_mut_ptr(), count, &mut count),
            PROGPU_NATIVE_STATUS_SUCCESS
        );
        assert_eq!((pieces[0].p0.x, pieces[2].p1.x, pieces[2].p1.y), (50.0, 0.0, 50.0));
        p.progpu_native_path_measure_destroy(measure);

        let mut curve = ptr::null_mut();
        assert_eq!(
            p.progpu_native_path_measure_create(&quarter_figure, 1, &quarter, 1, 0, 1.0, &mut curve),
            PROGPU_NATIVE_STATUS_SUCCESS
        );
        assert_eq!(p.progpu_native_path_measure_contour(curve, 0, &mut length, ptr::null_mut()), PROGPU_NATIVE_STATUS_SUCCESS);
        assert!((length - 50.0 * std::f32::consts::PI).abs() < 0.3, "quarter circle length {length}");
        let mut piece = quarter;
        assert_eq!(
            p.progpu_native_path_measure_segment(curve, 0, 0.0, length * 0.5, &mut piece, 1, &mut count),
            PROGPU_NATIVE_STATUS_SUCCESS
        );
        assert_eq!((count, piece.kind), (1, PROGPU_NATIVE_PATH_SEGMENT_CUBIC as u32));
        assert!((piece.p3.x - 70.71).abs() < 0.1 && (piece.p3.y - 70.71).abs() < 0.1);
        p.progpu_native_path_measure_destroy(curve);
    }
}
