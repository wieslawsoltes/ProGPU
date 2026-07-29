// The final shared library links Dawn's complete generated WebGPU C-procedure
// archive. This translation unit gives CMake a concrete shared-library source;
// it intentionally has no runtime initialization or exported ProGPU ABI.
namespace
{
[[maybe_unused]] constexpr unsigned int ProGpuDawnAndroidBundleVersion = 1;
}
