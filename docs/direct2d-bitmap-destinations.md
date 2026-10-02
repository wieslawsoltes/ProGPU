# Direct2D bitmap destination semantics

The portable `ID2D1RenderTarget::DrawBitmap` recorder now resolves an omitted
destination from the selected source rectangle's DIP width/height at target
origin zero. Previously a crop was stretched to the complete bitmap DIP size.
Source bitmap DPI converts only the source texture coordinates; target DPI and
the current drawing transform remain separate unchanged rendering inputs.

A finite destination whose X or Y edges are inverted emits no draw and does not
latch a target error. It is not a request to mirror the image. Existing bitmap
identity/factory/provider, opacity/interpolation, source-bounds and nonfinite
validation still precede that no-op. In particular this change does not admit
out-of-bitmap source rectangles or hide existing source/resource errors.

These two behaviors are explicit in Microsoft's original
[DrawBitmap parameter contract](https://learn.microsoft.com/en-us/windows/win32/api/d2d1/nf-d2d1-id2d1rendertarget-drawbitmap%28id2d1bitmap_constd2d1_rect_f_float_d2d1_bitmap_interpolation_mode_constd2d1_rect_f%29).
No foreign implementation was copied. This is a bounded correction of the
existing original ProGPU recorder, not a new renderer or sampling architecture.
The same native scene/image path serves wgpu-native and Dawn. Managed Scene has
no portable Direct2D COM recorder; raw managed native submission consumes the
same shared native stream. The genuine Windows provider already delegates
ordinary drawing to system Direct2D; its command-sink bitmap import remains a
distinct explicit unsupported-resource boundary, unchanged here.

The independent branch starts at main `9994c19a73fb032bdb093702dfa212c1c35e271f`
and does not depend on the pending Clear or scoped-copy PRs. No C ABI, COM slot,
wire layout, source clip/layer policy, default or generated binding changes.

## Authored controls

Eight actual public-vtable sequences cover independent source and compatible
target DPI axes, both existing interpolation modes, nonzero source crops,
translated clipped/unclipped placement, X/Y/both inverted destinations and a
subsequent visible draw. Original Windows Direct2D/WIC executes the same calls
independently; every BGRA pixel is compared with the native implementation and
an independent binary-color expected image. Both native providers additionally
require exact cold/warm/independent pixels and 2/1/2 submissions. Resources retire
before replay. Existing independent controls/deadlines remain unchanged.

Raw recorded commands require exact source physical coordinates and destination
DIP origin/extents for three crops plus null-source full-bitmap control. Portable
tests preserve exact foreign-factory/source-invalid errors, all four nonfinite
destination controls and following-draw usability. Original Windows separately
compares and reports ordered versus inverted destination HRESULT/tag precedence
with real foreign-factory bitmap, nonfinite source, inverted source and
out-of-bounds source inputs. That original observation is not yet available:
the implementation deliberately preserves the previous strict validation order;
any original precedence difference is a blocking qualification result, not
permission to swallow errors or broaden source bounds silently.

Implementation and all fixtures are committed before focused checks. No local
native library, GPU, Windows/VM execution or runtime staging has occurred.
Hosted source/Windows/provider/package qualification remains required.

Post-commit source checks passed with Apple Clang 21.0.0, C++20,
`-Wall -Wextra -Wpedantic -Wshadow -Werror -fsyntax-only` and a 45-second bound
per process: the complete Direct2D target implementation, complete portable
compatibility test translation unit and an explicit instantiation of the shared
pixel fixture. The memory ownership source guard passed with 93 owned fields and
seven non-owning identities excluded; `git diff --check` passed. These checks
produce no object/library and execute no native provider or GPU. The Windows-only
translation unit, original HRESULT/tag observations, recorded native contracts
and pixel/submission assertions remain pending hosted execution.
