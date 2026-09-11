# Floating transport test MSVC compatibility

PR139 run 34551944182, MSVC job 103116502958 fails at
progpu_native_text_interop_tests.cpp:447: C4456 reports the new floating
requirements lambda shadows the existing paragraph requirements local. /WX
correctly makes that warning an error. Rename the lambda and its five call sites
to get_floating_requirements; no production behavior, assertions or compiler
policies change. Local native interop rebuild and CTest pass (0.55 seconds).
New-head MSVC CI remains required.

The same run's browser job 103116502968 separately times out after 120 seconds at
native stage evidence-readback, with no browser errors reported. It is not fixed
by this identifier change. Preserve timeout/readback/quality gates and investigate
completion evidence; do not treat a rerun or longer timeout as a demonstrated fix.
