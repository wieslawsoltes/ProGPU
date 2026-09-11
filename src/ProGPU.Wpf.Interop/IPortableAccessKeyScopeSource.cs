namespace ProGPU.Wpf.Interop;

/// <summary>
/// Source-owned admission for a portable root's default keyboard access-key scope.
/// This is not a request to activate a native window or transfer keyboard focus.
/// </summary>
public interface IPortableAccessKeyScopeSource
{
    /// <summary>
    /// True only while the root belongs to a live, visible, active portable window
    /// that admits input. Read on the owning UI thread; do not discover handles,
    /// allocate a window-state snapshot, or infer activation from keyboard focus.
    /// Custom hosts must publish their actual host activation and lifetime policy.
    /// </summary>
    bool IsPortableAccessKeyScopeActive { get; }
}
