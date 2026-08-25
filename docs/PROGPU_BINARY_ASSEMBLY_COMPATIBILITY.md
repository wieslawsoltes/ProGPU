# ProGPU binary assembly compatibility

## Scope

This design adds an explicitly opt-in modern .NET compatibility profile for
precompiled libraries whose metadata references the official strong-named
`SkiaSharp` and `Avalonia.Skia` assemblies. It changes assembly identity and
build/publish asset selection only. Rendering, shaping, rasterization, scene
compilation, shaders, native interop, and managed/native renderer behavior are
unchanged, so the native rendering parity rule is not applicable.

The replacement assemblies use these ceiling identities:

| Contract | Assembly version | Public-key token | Runtime |
| --- | ---: | --- | --- |
| SkiaSharp 4.151.1 package | `4.151.0.0` | `0738eb9f132ed756` | `net10.0` |
| Avalonia.Skia 12.1.1 package | `12.1.1.0` | `c8d484a7012f9a8b` | `net10.0` |

CoreCLR can use an already selected assembly to satisfy a request for the
same simple name, culture, and public-key token when the selected version is
equal to or higher than the requested version. The profile therefore accepts
stable official package releases in these bounded ranges without a per-patch
setting:

- SkiaSharp 2.80.0 through 4.151.1, covering the released stable 2.x, 3.x,
  and 4.x package bands through August 25, 2026.
- Avalonia.Skia 11.0.0 through 12.1.1, covering the released stable 11.x and
  12.x package bands through August 25, 2026.

This is a binary identity and shared-contract promise, not a claim that every
historical SkiaSharp API removed between major versions has been re-created.
An unchanged dependency still requires every API it calls to exist in the
current ProGPU SkiaSharp compatibility surface. Prereleases, future versions
above either ceiling, and .NET Framework are not covered.

## Primary sources and decisions

| Source | Finding | Decision |
| --- | --- | --- |
| [C# `PublicSign` compiler option](https://learn.microsoft.com/en-us/dotnet/csharp/language-reference/compiler-options/security#publicsign) | Public signing embeds the supplied public key and marks the assembly as signed without possessing or applying the private key. Microsoft identifies compatibility with fully signed OSS releases as its intended use. | Public-sign both replacement identities from public-only 160-byte key blobs. Do not describe the outputs as authentically signed by SkiaSharp or Avalonia. |
| [.NET 6 strong-name API compatibility note](https://learn.microsoft.com/en-us/dotnet/core/compatibility/core-libraries/6.0/strong-name-signing-exceptions) | .NET / CoreCLR does not validate strong-name signatures at runtime; .NET Framework has different enforcement behavior. | Limit the profile to modern .NET and keep the feature opt-in. |
| [.NET assembly names](https://learn.microsoft.com/en-us/dotnet/standard/assembly/names) | CLR identity consists of name, version, culture, and strong name rather than the file name alone. | Match all four identity dimensions and test the consumer's original `AssemblyRef` metadata before execution. |
| [AssemblyLoadContext versioning](https://learn.microsoft.com/en-us/dotnet/core/dependency-loading/understanding-assemblyloadcontext#versioning-rules) | One load context loads one assembly per simple name and can satisfy a request from an equal-or-higher loaded version. | Use one tested ceiling identity per assembly, avoiding a patch-specific asset matrix while rejecting requests above the ceiling. |
| [NuGet MSBuild assets](https://learn.microsoft.com/en-us/nuget/concepts/msbuild-props-and-targets) | `buildTransitive` targets flow build logic to consuming PackageReference projects. | Put bounded output and publish replacement in `ProGPU.BinaryCompatibility.targets`; do not rewrite package restore metadata or consumer IL. |
| [Avalonia 12.1.1 public key](https://github.com/AvaloniaUI/Avalonia/blob/12.1.1/build/AvaloniaPublicKey.props) | Avalonia publishes the full public key corresponding to token `c8d484a7012f9a8b`. | Generate `eng/Avalonia.public.snk` from the official 12.1.1 assembly and independently verify it against the published key and token. |
| [Avalonia 12.1.1 lease contract](https://github.com/AvaloniaUI/Avalonia/blob/12.1.1/src/Skia/Avalonia.Skia/ISkiaSharpApiLeaseFeature.cs) | Existing custom drawing binaries reference three public lease interfaces scoped to the `Avalonia.Skia` assembly. | Public-sign a minimal facade and forward exactly those three types to the existing clean-room contracts in `Avalonia.ProGpu`. |
| Official SkiaSharp 4.151.0/4.151.1 reference assemblies from the pinned NuGet packages | Both report `SkiaSharp, Version=4.151.0.0, PublicKeyToken=0738eb9f132ed756`; 4.151.0 is the repository's existing 4,222-entry API-parity baseline. | Generate `eng/SkiaSharp.public.snk` from the signed reference assembly, public-sign the existing ProGPU-owned implementation, and preserve its API/behavioral tests. |

No source text from SkiaSharp or Avalonia was copied. The public key blobs are
cryptographic identity data extracted from signed public binaries, and the
facade contains only ProGPU-authored type-forward declarations over contracts
already implemented in this repository.

## Architecture

`ProGPU.SkiaSharp` now emits:

```text
SkiaSharp, Version=4.151.0.0, Culture=neutral,
PublicKeyToken=0738eb9f132ed756
```

`ProGPU.Avalonia.Skia.BinaryCompatibility` emits the non-shipping payload:

```text
Avalonia.Skia, Version=12.1.1.0, Culture=neutral,
PublicKeyToken=c8d484a7012f9a8b
```

That facade forwards:

```text
Avalonia.Skia.ISkiaSharpApiLeaseFeature
Avalonia.Skia.ISkiaSharpApiLease
Avalonia.Skia.ISkiaSharpPlatformGraphicsApiLease
```

to their defining `Avalonia.ProGpu` assembly. There is no adapter allocation,
reflection, IL rewriting, rendering call, or per-frame work.

The facade identity is deliberately the tested 12.1.1 ceiling. The same
forward-only source is built against both the Avalonia 11 and Avalonia 12
ProGPU rendering lanes. Older 11.x assemblies reference the two original lease
interfaces; newer releases may also use the platform-graphics lease. All
shared member references resolve to the current ProGPU definitions.

The `ProGPU.BinaryCompatibility` package contains both identity-compatible
payloads under `tools/net10.0`. When the consuming application sets:

```xml
<PropertyGroup>
  <ProGpuBinaryCompatibility>true</ProGpuBinaryCompatibility>
</PropertyGroup>
```

its transitive target overwrites the two final build outputs after normal copy
resolution and replaces the same two `ResolvedFileToPublish` items before
publish bundling. Compile-time references remain official package reference
assemblies, and third-party binaries remain byte-for-byte unchanged.

The package intentionally does not attempt to satisfy NuGet package identity.
Applications still reference the normal ProGPU Avalonia packages and may keep
official `SkiaSharp` / `Avalonia.Skia` dependencies required by third-party
packages. The compatibility package controls only the final CLR assets.

## Validation

`integration/ProGpuBinaryCompatibility/run.sh` provides the end-to-end gate:

1. Compile consumers solely against the first and last stable package in every
   released SkiaSharp 2.x/3.x/4.x and Avalonia.Skia 11.x/12.x minor band.
2. Record every SHA-256 and verify the original strong-named `AssemblyRef`
   token and bounded version before loading the consumer.
3. Execute the consumers against direct Avalonia 11 and Avalonia 12 ProGPU
   compatibility projects.
4. Pack `ProGPU.BinaryCompatibility`, restore hosts through the local package,
   and execute both ordinary build and publish outputs for both Avalonia lanes.
5. Verify all precompiled consumer SHA-256 values remain unchanged.

Focused unit tests separately pin both replacement identities and the complete
three-type forwarding set.
