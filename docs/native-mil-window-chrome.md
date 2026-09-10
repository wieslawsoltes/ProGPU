# Native MIL source window-chrome ownership

The existing LibreWPF package SDK gate attaches, replaces and removes real WPF
WindowChrome. Source inspection found that its worker selected WPF HWND hooks
by OS alone, treating a ProGPU-owned Windows handle as a WPF HwndSource. The Showcase
also contains chrome metadata, but that metadata-only check is not evidence of
an attached custom-chrome window; the SDK application is the concrete consumer.

Source WindowChromeWorker now uses active portable ownership, registered portable
activation, or the immutable portable media selection before querying a handle.
That ordering covers chrome attached before the first source and attached after
activation. An already-created native WPF source keeps its ownership even if a
portable service is subsequently registered; pre-source policy cannot reclassify
it. Portable chrome continues through source SetPortableCustomChrome,
neutral PortableWindowState and the registered border callback. The host's
existing window-border implementation owns the platform mutation; no WPF
HwndSource lookup, WPF chrome hook, DWM frame restoration or WPF client-area query
is valid merely because the host exposes a real Windows HWND.

Removing chrome restores the source WindowStyle through the same typed callback.
Windows-MIL windows retain their original native WPF chrome path. Both ProGPU
renderer modes share this source/host contract; no native renderer, GPU shader,
pixel fallback, C ABI or CPU-heavy algorithm changes are required.

Authored source fixtures cover attachment before/after hidden activation, live
chrome-property changes, border callbacks and removal without changing the public
WindowStyle. The existing package application checks typed border state after
attachment/removal. These are not executed results. Interactive caption drag,
resize borders, non-client hit testing, DPI transitions and exact-head Windows
application/CI qualification remain required after feature freeze. SDK Windows
admission is not enabled by this source ownership connection.
