//! A frame that fits the capacity of the frames before it allocates nothing.

use progpu_native::SceneWriter;
use progpu_native_sys::progpu::*;
use std::alloc::{GlobalAlloc, Layout, System};
use std::cell::Cell;

thread_local! {
    static ALLOCATIONS: Cell<u64> = const { Cell::new(0) };
}

struct Counting;

// SAFETY: every call is forwarded to the system allocator.
unsafe impl GlobalAlloc for Counting {
    unsafe fn alloc(&self, layout: Layout) -> *mut u8 {
        let _ = ALLOCATIONS.try_with(|count| count.set(count.get() + 1));
        // SAFETY: the contract of the caller.
        unsafe { System.alloc(layout) }
    }

    unsafe fn dealloc(&self, pointer: *mut u8, layout: Layout) {
        // SAFETY: the contract of the caller.
        unsafe { System.dealloc(pointer, layout) }
    }

    unsafe fn realloc(&self, pointer: *mut u8, layout: Layout, size: usize) -> *mut u8 {
        let _ = ALLOCATIONS.try_with(|count| count.set(count.get() + 1));
        // SAFETY: the contract of the caller.
        unsafe { System.realloc(pointer, layout, size) }
    }
}

#[global_allocator]
static ALLOCATOR: Counting = Counting;

fn frame(writer: &mut SceneWriter, generation: u64) -> usize {
    let color = progpu_native_color { r: 1.0, g: 0.0, b: 0.0, a: 1.0 };
    let bounds = progpu_native_image_rect { x: 0.0, y: 0.0, width: 64.0, height: 64.0 };
    writer.reset(11, generation).expect("reset");
    writer.clear_target(color).expect("clear");
    let brush = writer.add_solid_brush(color, 1.0).expect("brush");
    let primitive = progpu_native_analytic_primitive {
        kind: PROGPU_NATIVE_PRIMITIVE_RECTANGLE as u32,
        flags: 0,
        x: 1.0,
        y: 1.0,
        width: 8.0,
        height: 8.0,
        corner_radius: 0.0,
        stroke_thickness: 0.0,
        color,
        transform: progpu_native_affine_2d { m11: 1.0, m12: 0.0, m21: 0.0, m22: 1.0, m31: 0.0, m32: 0.0 },
    };
    for _ in 0..32 {
        writer.draw_analytic(&[primitive], &[brush], bounds, PROGPU_NATIVE_SCENE_NO_INDEX as u32).expect("draw");
    }
    writer.build().expect("build").len()
}

#[test]
fn a_stable_frame_allocates_nothing() {
    let mut writer = SceneWriter::new(11, 1).expect("a writer");
    let first = frame(&mut writer, 1);
    let before = ALLOCATIONS.with(Cell::get);
    let second = frame(&mut writer, 2);
    let allocated = ALLOCATIONS.with(Cell::get) - before;
    assert_eq!(first, second);
    assert_eq!(0, allocated, "allocations in a frame the same as the one before");
}
