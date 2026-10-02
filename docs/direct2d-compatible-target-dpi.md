# Compatible render target fractional-pixel DPI

When only a logical size is supplied to `CreateCompatibleRenderTarget`, each
fractional physical extent rounds upward and only that axis's child DPI increases
to map the requested logical corner to the actual pixel corner. An integral axis
retains its exact parent DPI. This follows the original Microsoft
[four-combination contract](https://learn.microsoft.com/en-us/windows/win32/api/d2d1/nf-d2d1-id2d1rendertarget-createcompatiblerendertarget%28constd2d1_size_f_constd2d1_size_u_constd2d1_pixel_format_d2d1_compatible_render_target_options_id2d1bitmaprendertarget%29).
The original size-and-pixels calculation, pixels-only/neither inheritance,
format/alpha/options gates, finite and size limits, output publication and owned
bitmap lifetime remain unchanged. No requested-DIP metadata replaces the actual
floating-point `GetSize` calculation.

The fixture owns six independent parent-DPI/request pairs and all four size-input
combinations: 24 cases include X-only, Y-only, both fractional and both integral
axes, asymmetric density and 96 DPI. It captures actual target and `GetBitmap`
logical/pixel size and DPI. The original Windows D2D/WIC execution must match
every metric exactly, printing hexadecimal floats on disagreement; this is a
qualification gate, not an asserted observation of Windows behavior.

Each case draws the caller's original three-quarter-size blue rectangle over a
red compatible target. A 32-DIP parent image has an independent full-byte oracle
with a blue 24-by-24 corner, red remainder and black exterior. After recording,
the child changes DPI/content and all child handles retire. Both native providers
must retain the original pixels on cold/warm/independent engines with 2/1/2
submissions. Original Windows independently compares all pixels with the absolute
oracle and native result, without tolerance or excluded borders. The new Windows
batch reuses one real renderer engine; every existing deadline and original case
remains unchanged. Existing source and provider suites select the shared fixture.

This is an original portable implementation over the existing compatible-target
factory and picture snapshot contracts. It introduces no ABI, default, CPU
rendering, native crossing, additional readback or layer/clip policy. Base is
main `e444b383d72ecc4252d1554a35096ecf4bb24be7`, independent of #278 and #275.
The implementation and authored controls are committed before bounded source
checks. Original Windows, both GPU providers and complete package qualification
remain pending hosted execution; no local native/full build or GPU/VM was run.

Post-commit checks: Apple Clang C++20 `-fsyntax-only -Wall -Wextra -Wpedantic
-Wshadow -Werror` passed the complete changed portable target, existing portable
compatibility test TU and an explicitly instantiated shared pixel fixture, with
a 45-second timeout per process. `git diff --check` passed. No objects/libraries
were produced and no test executable, GPU pipeline or Windows oracle was run.
The Windows-only differential TU and provider entrypoints still require hosted
type compilation/execution; the shared fixture's syntax check is not that proof.
