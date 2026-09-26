# System font ownership and role identity

Each `SystemFonts` property now creates a caller-owned `Font`. Requests for the
same role have equal font values but independent disposal state. A request for
one role cannot dispose another role's font or corrupt later requests.

The owned font retains the requested property name in `SystemFontName` and
reports `IsSystemFont`. Ordinary constructors, prototype/style copies and clones
retain their existing untagged identity. Font value equality does not include
the system role. Unknown, empty, differently cased and null `GetFontByName`
requests return null rather than selecting an unrelated default font.

This is an original ProGPU implementation of observable API and ownership
contracts. The current portable generic-sans selection, 8.25-point size, style,
charset and font metrics are unchanged. These role names do not claim native OS
font-settings discovery, Windows font-name parity, accessibility text scaling,
or identical cross-platform form autoscaling. Those are separate contracts.

## Validation

The same 13 new ownership/identity cases fail on unchanged main
`1fca0bcad00e7359b9aeb78427d37b6794045d20`. With the implementation change,
the complete source Drawing suite passes 662/662 with no skips on macOS ARM64
(.NET 10.0.5). The original allocation assertion is unchanged.

The pinned upstream corpus already records six independent-instance failures
and three unknown-name failures on each Linux architecture. Those inventories
must only be tightened from actual complete Linux corpus results. Host source
tests alone do not qualify those inventories, package consumers or native font
settings. No upstream test is excluded or assertion relaxed by this change.
