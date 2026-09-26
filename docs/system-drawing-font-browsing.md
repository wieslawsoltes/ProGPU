# Font browsing metadata

`FontConverter` presents these nine design-time properties in order:

1. `Name`
2. `Size`
3. `Unit`
4. `Bold`
5. `GdiCharSet`
6. `GdiVerticalFont`
7. `Italic`
8. `Strikeout`
9. `Underline`

The inherited `GetProperties(value)` and `GetProperties(context, value)`
overloads request browsable properties. The three-argument overload still honors
the caller's filter: `BrowsableAttribute.Yes` selects those nine properties,
`BrowsableAttribute.No` selects the eight hidden properties, and a null or empty
filter retains all 17 public properties. Hidden properties remain public and
readable; this change does not remove API members.

The seven official hidden properties are `FontFamily`, `Height`, `IsSystemFont`,
`OriginalFontName`, `SizeInPoints`, `Style`, and `SystemFontName`. The existing
ProGPU-specific `OriginalUnit` property is also hidden from default designer
browsing while remaining available to ordinary callers and unfiltered queries.
`Name` and `Unit` retain their existing type converters.

## Contract provenance

This is an original metadata change to ProGPU's existing implementation, not a
port of another converter. The exact nine-property ordering is observable in
[`GetFontPropsSorted` in the pinned upstream tests](https://github.com/dotnet/winforms/blob/b8acee9d29af0ed4c9049cea5f05f80570ecf3b0/src/System.Drawing.Common/tests/System/Drawing/FontConverterTests.cs#L83).
The public [browsability contract](https://learn.microsoft.com/en-us/dotnet/api/system.componentmodel.browsableattribute?view=net-10.0)
defines visibility separately from API accessibility, and
[`TypeConverter.GetProperties`](https://learn.microsoft.com/en-us/dotnet/api/system.componentmodel.typeconverter.getproperties?view=net-10.0)
preserves explicit attribute filtering.

Read-only PE metadata inspection independently confirmed `Browsable(false)` on
the seven official properties in Microsoft `System.Drawing.Common` 10.0.12,
`lib/net10.0/System.Drawing.Common.dll` (assembly version 10.0.0.0, SHA-256
`e09d9cbd11b375098dfebe93fa10905cce7c03d55369452da12194d63ae0964d`).
Only property names and custom-attribute values were inspected; no upstream
implementation or method body was read, copied, or translated. The additional
`OriginalUnit` annotation preserves the existing ProGPU API without adding an
extra designer property to the nine-property contract.

The product change is eight metadata annotations and two entries in the existing
ordering array. The existing attribute query and remaining-descriptor append
algorithm are unchanged. No parser, constructor descriptor, font selection,
metrics, disposal, native ABI, or rendering path changes. Neither renderer has a
corresponding .NET component-model browsing operation, so a C++ renderer change
is not applicable. Descriptor work remains bounded by the existing public
property count and does not enter rendering or text-layout hot paths.

## Validation and remaining gates

Sixteen independent ProGPU cases cover each hidden descriptor, exact default
ordering through both inherited overloads and an explicit browsable filter,
null/empty filters, hidden-only filtering, and direct `TypeDescriptor` queries
that do not use `FontConverter`. Against unchanged parent `6bed216d6`, 14 cases
fail and the two unfiltered-query controls pass, with zero skips. After the
metadata and ordering change, all 16 cases pass with zero skips.

The first complete host Drawing suite run on macOS ARM64 records 689 passes,
one failure and zero skips (690 total). The sole failure is the unchanged
`WarmedPrivateMetricReadsAreAllocationFree` assertion: expected zero, observed
3,400 bytes. The full result is preserved; it was not rerun to obtain a pass,
and it is not a successful whole-suite qualification. API verification passes
with zero missing types, zero missing members and the same 13 existing shape
differences. The isolated focused build reports no warnings or errors.

The complete pinned upstream Linux corpus, independent architecture lanes, and
the whole Build remain required before qualification. No known-failure baseline,
test exclusion, assertion, skip, runtime policy, or allocation gate is changed.
Font string parsing, platform font discovery and unavailable family names remain
separate compatibility gaps. These component-model tests do not qualify a live
property-grid application, native font settings, or rendered text layout.
