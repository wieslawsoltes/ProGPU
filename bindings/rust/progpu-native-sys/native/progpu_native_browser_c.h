/*
 * A C ABI for hosts that link the engine into a module of their own on
 * wasm32-unknown-emscripten and cannot call C++: the WebGPU device the page requested before the
 * module started, and the swap chain of a canvas.
 *
 * It sits here, over the WebGPU header of the Emscripten port, so that a host does not depend on
 * the layout of the port's surface structures. Every call belongs to the thread of the page: the
 * device of a page is not shared with its workers.
 *
 * A function that returns int returns nonzero on success.
 */
#ifndef PROGPU_NATIVE_BROWSER_C_H
#define PROGPU_NATIVE_BROWSER_C_H

#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

typedef struct progpu_native_browser_canvas progpu_native_browser_canvas;

/* The device the page gave the module (Module.preinitializedWebGPUDevice), or 0. */
uintptr_t progpu_native_browser_imported_device(void);
/* The queue of a device, with a reference of its own, or 0. */
uintptr_t progpu_native_browser_device_queue(uintptr_t device);

/*
 * The swap chain of the canvas that the CSS selector names, in a format of
 * progpu_native_texture_format (RGBA8 or BGRA8 unorm). The surface is configured at the first
 * frame.
 */
int progpu_native_browser_canvas_create(
    uintptr_t device,
    const char* selector,
    uint32_t native_format,
    progpu_native_browser_canvas** canvas);
void progpu_native_browser_canvas_destroy(progpu_native_browser_canvas* canvas);

/*
 * The view of the texture of the next frame, at width by height pixels. The page shows the frame
 * when the task that draws it returns; there is no present call. Every successful begin is
 * followed by an end.
 */
int progpu_native_browser_canvas_begin_frame(
    progpu_native_browser_canvas* canvas,
    uint32_t width,
    uint32_t height,
    uintptr_t* view);
void progpu_native_browser_canvas_end_frame(progpu_native_browser_canvas* canvas);

#ifdef __cplusplus
}
#endif

#endif
