using System;

namespace ProGPU.Backend;

/// <summary>
/// A GPU operation cannot continue on its terminally lost device domain.
/// Hosts may rebuild the domain after unwinding the active frame. This is
/// distinct from validation, allocation and application callback failures.
/// </summary>
public sealed class WgpuDeviceLostException : InvalidOperationException
{
    public WgpuDeviceLostException(string message) : base(message)
    {
    }
}
