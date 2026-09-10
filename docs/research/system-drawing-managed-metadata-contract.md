# System.Drawing managed metadata contract

## Source contract

The public shapes and observable collection behavior in this slice match the .NET 10 `System.Drawing.Common` contract for `BitmapSuffixInSameAssemblyAttribute`, `BitmapSuffixInSatelliteAssemblyAttribute`, `System.Drawing.Design.CategoryNameCollection`, and `System.Drawing.Imaging.ColorMode`.

Both bitmap-suffix attributes apply only to assemblies and remain inheritable. `CategoryNameCollection` is a sealed `ReadOnlyCollectionBase` snapshot with the official array/copy constructors, indexer, lookup, and copy operations. `ColorMode` retains the official 32-bit and 64-bit component-mode values.

## Portable implementation

These are managed metadata and collection identities, so they require no renderer, GDI+, reflection-based product path, or local-OS adapter. The collection owns its list snapshot and delegates validation and copying to the base collection contract.

## Quality gate

Focused tests verify attribute inheritance and assembly-only usage, collection ownership and validation, lookup/copy behavior, and exact enum values. This slice adds no rendering or repeated hot-path work, so it does not require a performance benchmark.
