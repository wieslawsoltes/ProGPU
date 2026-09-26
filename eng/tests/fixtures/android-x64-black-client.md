# Rejected Android screenshot

`android-x64-black-client.png.base64` losslessly stores the original, unedited
`screenshot.png` from [run 36236752156](https://github.com/wieslawsoltes/ProGPU/actions/runs/36236752156),
artifact `progpu-android-x64-4c1a43248f628bbc00a6c715a8dbab5ce4d574d7`.
The PNG SHA-256 is `a7e182491946f15a4f02297b99987cd0d25108d2c887ec45f4a9d82e003f882a`.

The API 35 AOSP x86_64 sample was resumed and focused, and reported a Vulkan
first frame, but its entire client area was black. The status/navigation bars
are Android output, not evidence of gallery rendering. This is a **negative**
artifact regression, not a passing image baseline. Base64 keeps the fixture
text-reviewable and introduces no image conversion or rendering dependency.
