using Silk.NET.WebGPU;

namespace ProGPU.Backend;

internal readonly record struct LinuxWebGpuAdapterCandidate(
    AdapterType AdapterType,
    BackendType BackendType);

internal static class LinuxWebGpuBackendPreference
{
    internal static BackendType Choose(
        ReadOnlySpan<LinuxWebGpuAdapterCandidate> candidates)
    {
        int bestScore = int.MinValue;
        BackendType bestBackend = BackendType.Undefined;
        foreach (LinuxWebGpuAdapterCandidate candidate in candidates)
        {
            if (candidate.BackendType is not (
                BackendType.Vulkan or BackendType.OpenGL))
            {
                continue;
            }

            int score = candidate.AdapterType switch
            {
                AdapterType.DiscreteGpu => 400,
                AdapterType.IntegratedGpu => 300,
                AdapterType.Unknown => 200,
                AdapterType.Cpu => 100,
                _ => 0
            };
            if (candidate.BackendType == BackendType.Vulkan)
            {
                score += 10;
            }
            if (score > bestScore)
            {
                bestScore = score;
                bestBackend = candidate.BackendType;
            }
        }

        return bestBackend;
    }
}
