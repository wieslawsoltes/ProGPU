# Optional floating text provider contract

The LibreWPF acceptance application's first-window measurement requires native
Figure/Floater layout. `IPortableFloatingTextFormatting` extends the existing
segmented/inline service explicitly; ordinary providers cannot silently ignore
floating events. Source measures original children and supplies ordered UTF-16
boundaries in shaping text, not original document symbol offsets. Source hidden
edge mapping and child lifetime remain source responsibilities.

The typed alignment enum, explicit origin/retry/empty-row metrics and measured
outer boxes feed the existing native floating snapshot. Output retains source
positions and native physical row identities. Existing parent fragment content
extents remain separate from float-inclusive occupied extents. The contract
transfers no document objects or child ownership across assemblies.

The paired LibreWPF WpfPortableTextFormatting adapter uses its existing styled
physical-face/context pipeline and shared fragment interaction, with a floating
snapshot branch and owned neutral placements. It explicitly rejects requested
intrinsic widths until that floating contract exists, rather than returning null
after silently dropping the request. Both rendering modes share this CPU provider.

The linked original WPF adapter builds in Release with zero warnings/errors
against this interop project and native snapshot 93c4efdd. A local native probe
passes sibling/terminal placement, original input identity after caller mutation,
inline interaction, separate extents, the anchor-only native row, and explicit
intrinsic-width rejection. This is not source-WPF package or application evidence.
Existing snapshot and transport tests remain the lower-level coverage.

Next: connect original WPF hidden-edge event mapping and paragraph child placement,
hard-segment continuation and application drawing/input. Automatic anchor admission,
final packages, platform/CI gates and deferred broader compatibility remain open.
Provenance is the original ProGPU optional excluded/inline contract and
[retained native floating snapshot](native-mil-floating-snapshots-2026-09-11.md),
with the same documented text ownership research; no foreign implementation.
