# Fragment caret navigation transport

ShowcaseApp's anchored-document consumer needs physical caret movement across
same-row text fragments and cleared rows. The existing ProGPU C++ fragment
navigation now serves both C++ callers and the public C transport through one
templated internal implementation. No composer, layout repacking, allocation,
per-caret interop loop or alternate navigation algorithm was added.

The C function takes the same retained caret/fragment generation, a current
index, physical direction (left/right/up/down), paragraph level 0/1 and finite
preferred X in paragraph-local DIPs. It returns an existing index, unchanged at
an outer boundary. Invalid pointers/counts, direction, metadata or index return
InvalidArgument and clear the output. C byte affinities and reserved fields are
validated before the generic implementation. C++ enum values are compile-time
checked against the C mapping. Caller buffers are borrowed and must not overlap.

The managed `MoveFragmentCaret` span binding holds all pins for the call.
`NativeTextParagraphSnapshot.MoveFragmentCaret` selects its own retained buffers,
rejecting ordinary snapshots instead of using ordinary row-index navigation.
Physical X and fragment position own horizontal motion, while paragraph level
owns row wrapping. Native preferred-X selection and affinity tie handling are
unchanged. The caller must keep indices within the same snapshot generation.

Both provider libraries build and their sorted export allowlists include the
new symbol. Native text and C interaction suites pass. The ABI differential
compares all six current carets, four directions and both paragraph levels with
the C++ API, and rejects invalid index, wide direction value, NaN preferred X,
reserved placement and invalid byte affinity. The managed consumer covers
vertical clearance, LTR/RTL crossing of an exclusion on the same row and cleared
invalid-index output. Backend/consumer compilation has zero warnings/errors.

Evidence: `artifacts/fragment-navigation-transport-build.log`,
`fragment-navigation-native-tests.log`, `fragment-navigation-consumer-build.log`,
`fragment-navigation-consumer.log` and `fragment-navigation-contract.log`.
These local native component results do not qualify source Figure/Floater,
empty excluded paragraphs, native height-fit convergence, fresh packages or
Windows/Linux/VM application behavior. Source anchor placement and the typed WPF
consumer remain the next concrete core dependencies; broader API expansion stays
deferred behind application closure.
