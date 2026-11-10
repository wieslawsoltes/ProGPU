# Original Canvas primitive adapters

Implementation only; validation is deferred.

`draw_arc` ports the original complete finite-input/no-op admission, signed 360-degree sweep clamp, original midpoint and separate trigonometric expressions, explicit two half arcs for a centered full turn and independent closed-oval route for an uncentered full turn. It records the actual endpoint arc path and retains the full Paint path-effect/stroke/shader flow. A zero rectangle means all four edges equal zero; negative extents are not relabeled empty.

Point/color-point overloads preserve private stroke selection and the independent Src blend default. The center/radii oval overload reconstructs original edges before midpoint arithmetic. Rounded-rectangle difference retains both original default-index paths and the actual ordered Boolean operator without assuming containment. The XYWH rectangle entry point retains supplied dimensions independently of reconstructed far endpoints.

Explicit matrix/position picture entry points compose the original previous matrix with the supplied matrix and record it directly, preserving caller Canvas state even on failure. No saved clip/layer scope is introduced by this transform-only overload.

Source: `src/SkiaSharp/SKCanvas.cs`, SHA-256 `08b8d871bccfefdda7021e533c41641eaf22299932d1c32f8e7d9b49a2741e64`. Original default-runtime trigonometry/float behavior, source errors, pixel coverage, native/browser lifetime/performance and package qualification remain open.
