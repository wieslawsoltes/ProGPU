# System.Drawing type-scoped bitmap resource contract

## Source contract

The .NET 10 `Bitmap(Type, string)` constructor loads a bitmap resource from the
assembly containing the supplied type. The resource name is case-sensitive and
is scoped by that type's namespace. For example, a type in `Example.Controls`
and the name `Button.png` resolve `Example.Controls.Button.png`.

Primary contract sources:

- [Bitmap constructors](https://learn.microsoft.com/en-us/dotnet/api/system.drawing.bitmap.-ctor?view=windowsdesktop-10.0)
- [Assembly.GetManifestResourceStream(Type, String)](https://learn.microsoft.com/en-us/dotnet/api/system.reflection.assembly.getmanifestresourcestream?view=net-10.0)

## Portable implementation

ProGPU uses the public, typed `Type.Assembly` and namespace-scoped manifest
resource operation required by the constructor contract. It does not enumerate
assemblies, scan fields, inspect private state, or accept lookalike objects.
The returned stream is decoded by the same managed image path used by the
stream constructor. Decoded pixels are owned by the `Bitmap`, so the manifest
stream is closed before construction returns and no assembly resource remains
leased for the bitmap lifetime.

Null type and resource arguments are rejected before lookup. An empty resource
name is rejected by the manifest-resource contract, a missing resource produces
an `ArgumentException`, and invalid image bytes follow the existing stream
decoder's explicit `ArgumentException` boundary.

## Quality and compatibility gate

The test assembly embeds the existing ProGPU 256 by 256 PNG fixture with an
explicit logical resource name. Focused tests verify namespace-relative lookup,
decoded dimensions and representative pixels, resource-stream independence,
and null, empty, and missing-name validation. Strict ApiCompat removes only the
`Bitmap(Type, string)` missing-member suppression, reducing measured debt from
18 to 17 missing members and from 33 to 32 total diagnostics with no new
incompatibilities or stale suppressions.

This constructor is intentionally a managed resource-loading feature. Native
HBITMAP/HICON imports and shell resource extraction remain separate typed
local-OS adapter work.
