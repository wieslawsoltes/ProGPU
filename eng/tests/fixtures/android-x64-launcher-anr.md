# Rejected Android launcher boot state

`android-x64-launcher-anr.txt` contains unchanged lines 1–8 and 140–146 of
`window-initial.txt` from [run 36238216481](https://github.com/wieslawsoltes/ProGPU/actions/runs/36238216481),
head `96b72f94f9e093bdfe593c707b76a5bfc79a82d8`.
The full original dump SHA-256 is
`a1c61c9c7f0df6779493544da0dabd97faecfc90d7488412208b78dc2e20241a`.
The joined excerpts are a negative parser fixture, not a complete window dump.

The action sent input after observing boot completion, before Quickstep's first
draw completed. The launcher ANR preceded APK installation. Its window already
had a surface and a focus entry, but remained hidden in `DRAW_PENDING`.
The gate must not admit this state or dismiss its ANR dialog.

Positive HOME parser fixtures are synthetic. Their field layout follows the
same run's ordinary visible sample activity/window records; unlike the verbose
last-ANR snapshot, the ordinary window record does not include `mDrawState`.
Actual activity draw acknowledgements plus the shown, focused window remain
required. Neither fixture qualifies a healthy emulator or sample rendering.
