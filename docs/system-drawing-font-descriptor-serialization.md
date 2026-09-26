# Font constructor serialization

`FontConverter` emits the shortest complete constructor-prefix descriptor for
its serialization contract. A nondefault value retains the preceding
arguments even when those arguments themselves have their default values:

| Last required value | Constructor argument count |
| --- | --- |
| Name and size only | 2 |
| Non-regular style | 3 |
| Non-point unit | 4 |
| Charset other than 1 | 5 |
| Vertical font | 6 |

This preserves the canonical style-before-unit prefix. It does not choose the
separate three-argument `Font(string, float, GraphicsUnit)` overload for a
non-point regular font.

The descriptor retains `OriginalFontName ?? Name`, size, style, unit, charset and
vertical state. Invoking it creates an independently owned font, including after
the source font is disposed. A requested unavailable family name remains the
constructor argument; this change does not replace it with the resolved fallback
name. Descriptor completeness is unchanged.

This is a bounded component-model serialization operation: at most six arguments
and a matching existing public constructor. Reflection is confined to the existing
`InstanceDescriptor` compatibility boundary. No renderer, native ABI, font parser,
catalog, metrics, font defaults or property-grid ordering changes. There is no
applicable C++ renderer counterpart because neither renderer implements or consumes
.NET component-model constructor descriptors.

## Contract and validation

The implementation is original ProGPU code over its existing converter. Its
behavioral reference is the eight original descriptor cases in
[the pinned upstream test file](https://github.com/dotnet/winforms/blob/b8acee9d29af0ed4c9049cea5f05f80570ecf3b0/src/System.Drawing.Common/tests/System/Drawing/FontConverterTests.cs).
Those tests compare argument counts, invoke the descriptor and compare all font
values. No upstream converter implementation was copied or translated. The public
[InstanceDescriptor contract](https://learn.microsoft.com/en-us/dotnet/api/system.componentmodel.design.serialization.instancedescriptor?view=net-10.0)
provides constructor metadata, arguments and invocation for design-time serialization.

Twelve new ProGPU cases check exact constructor parameter types and arguments,
all restored font values, independent repeated invocation, source disposal and
unavailable-family identity. Against parent `1a3f292f4`, eight fail solely on the
unnecessarily long argument list and four full-argument controls pass. With the
fix, the complete host Drawing suite passes 674/674 with zero skips on macOS ARM64;
the existing allocation assertion and runtime/test policy are unchanged.
API verification reports zero missing types, zero missing members and the same
13 previously recorded other differences, with zero build warnings or errors.

Paired complete Linux ARM64 corpus runs compare parent `1a3f292f4` with product
commit `5c4a16718`, using SDK 10.0.400/runtime 10.0.11. Both retain all 4,453 cases,
1,753 discovered methods, two original method exclusions, 82 included source files
and seven excluded source files. The baseline has 3,272 passes, 1,096 known
failures and 85 skips. The fixed run has 3,279 passes, 1,089 known failures and
the same 85 skips. Exactly seven descriptor cases improve; 4,440 case identities
and outcomes remain identical, and six other passing cases differ only in their
generated GUID or checkout-path display labels. Discovery and skip inventories
are byte-identical. The unchanged strict verifier rejects the fixed run solely
because the seven established descriptor failures disappeared.

The ARM64 expectation files match those actual results. The x64 expectation
requires the same seven repaired contracts: 3,295 passes, 1,124 known failures and
34 unchanged skips. That x64 expectation awaits its independent full CI corpus;
ARM64 is not evidence that x64 ran. The complete Build is also required before
merge. No original test, exclusion, assertion or skip is removed. Text parsing
and unavailable platform font names remain separate compatibility gaps.
