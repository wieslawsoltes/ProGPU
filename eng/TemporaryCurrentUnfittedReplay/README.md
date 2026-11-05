# Original Windows source replay

Producer 2e9e434959ddb43922061c6a1e03efc390739adb passed all 73 checks and
63 original Build jobs in 41974319569. The exact four-package closure from
artifact 13392892634 (0.1.0-preview.3711.ci) is independently hash/source/runtime
verified. Complete producer evidence is in producer-checks.json.

This retains the original 64 descriptors, font collector, expected frames,
source geometry, both native providers and 45-second/16-MiB workload limits.
The original failed 72-case application gate remains independently required.
No local VM, desktop window or input is used. Run each provider once on the
reviewed disposable Windows x64 runner; retain failures without retry-to-pass.
