# Native MIL SDK static dependencies

Producer 38b6a7a4 Build 34510947244 passed the native RID builds and dynamic
managed consumers, then failed the Linux source-independent C++ package link.
The unresolved symbols included Direct2D compat create_factory and native
fill/stroke geometry helpers called by libprogpu_native_mil.a.

The source CMake target already links Direct2D core, but the hand-authored SDK
import graph omitted MIL's dependencies and the NuGet staging lists omitted
libprogpu_native_direct2d_core.a (and the Windows .lib equivalent).
This is a packaging graph failure, not evidence of missing system Direct2D
on Linux or permission to remove those MIL geometry calls.

The fix imports Direct2D core, publishes MIL's scene/text/Direct2D dependencies
and Direct2D core's scene dependency, and preserves Windows compile definitions.
Normal Unix staging, explicit Unix build-only preflight and Windows staging now
require the archive. Package verification requires it for all six desktop RIDs.
Regular CMake install already included the archive and is unchanged.

The source-independent C++ consumer continues exercising its existing interfaces,
but links only the Dawn and MIL public targets. Scene/text/hit dependencies must
therefore arrive through the SDK graph. No source, assertion or runtime action
was removed, and no test tolerance or CI bypass was introduced.

The installed macOS ARM64 SDK consumer configures, links and executes locally
against the same exported config and current built archives. Logs are
artifacts/release-hour/sdk-link-install.log, sdk-link-configure.log,
sdk-link-build.log and sdk-link-tests.log. This does not qualify the fresh
all-RID NuGet package: exact-head CI must rebuild/stage/pack and run its full
consumer again. Downstream WPF pins to the failed producer cannot be treated
as qualified or satisfied by another commit's artifacts.
