# Original composition animation implementation

`scene::animation` ports the complete original `src/ProGPU.Scene/CompositionAnimations.cs` (SHA-256 `9e4669a01c1514768b3cd76c45c755332267dfea1114e788fc9286d7eed574d7`). Linear/cubic/step easing, scalar/Vector2/Vector3 keyframes, delay/duration/loop timing, spring motion and typed expression callbacks retain the original control flow and binary32 state. Scalar recurrence remains sequential; independent vector lanes use `wide` SIMD. Expressions execute only during explicit ticks, never GPU replay.

Original duplicate-progress keys and NaNs remain stored, using the original numeric comparer. Equal-key unstable-sort ordering, TimeSpan conversion, vector fused arithmetic and transcendental runtime behavior require original-source qualification; no matching result is claimed. Visual property binding is a separate ongoing implementation. Formatting only; no compilation, tests or runtime validation and no build artifacts.
