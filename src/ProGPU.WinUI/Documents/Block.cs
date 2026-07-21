using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Text;
using System;
using System.Collections.Generic;
using System.Numerics;
using System.Text;
using Silk.NET.Input;
using ProGPU.Layout;
using ProGPU.Vector;
using ProGPU.Scene;
using ProGPU.Text;
using ProGPU.Text.Shaping;

namespace Microsoft.UI.Xaml.Documents {

public abstract class Block
{
    private float _marginBottom = 12f;
    internal event Action? Changed;
    internal int LogicalTextSeparatorLength { get; set; } = 1;

    protected void OnChanged() => Changed?.Invoke();

    public float MarginBottom
    {
        get => _marginBottom;
        set
        {
            if (_marginBottom == value) return;
            _marginBottom = value;
            OnChanged();
        }
    }

    internal void SetMarginBottomWithoutNotification(float value) => _marginBottom = value;

    // Presenter-local caches no longer live on the semantic node. These values are
    // compatibility diagnostics published by the most recent layout session only.
    public float CachedHeight { get; internal set; } = -1f;
    public float CachedYOffset { get; internal set; }
    public bool IsLayoutValid { get; internal set; }
}

} // namespace Microsoft.UI.Xaml.Documents
