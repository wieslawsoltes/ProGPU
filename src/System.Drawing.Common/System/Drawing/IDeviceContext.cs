using System;

namespace System.Drawing;

/// <summary>
/// Provides access to a native device context when a platform adapter exposes one.
/// </summary>
public interface IDeviceContext : IDisposable
{
    IntPtr GetHdc();

    void ReleaseHdc();
}
