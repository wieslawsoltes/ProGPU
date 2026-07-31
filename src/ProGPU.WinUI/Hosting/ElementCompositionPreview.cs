using System.Runtime.CompilerServices;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Windows.Foundation.Metadata;

namespace Microsoft.UI.Xaml.Hosting;

[ContractVersion("Microsoft.UI.Xaml.WinUIContract", 0x00010000)]
public sealed class ElementCompositionPreview
{
    private static readonly ConditionalWeakTable<
        UIElement,
        ElementCompositionState> States = new();

    private ElementCompositionPreview()
    {
    }

    public static Visual? GetElementChildVisual(UIElement element)
    {
        ArgumentNullException.ThrowIfNull(element);
        if (!States.TryGetValue(element, out ElementCompositionState? state) ||
            state.ChildVisual is null)
        {
            return null;
        }

        if (!state.ChildVisual.IsAttachedTo(element))
        {
            state.ChildVisual = null;
            return null;
        }

        return state.ChildVisual;
    }

    public static Visual GetElementVisual(UIElement element)
    {
        ArgumentNullException.ThrowIfNull(element);
        return States.GetValue(element, CreateState).ElementVisual;
    }

    public static void SetElementChildVisual(
        UIElement element,
        Visual? visual)
    {
        ArgumentNullException.ThrowIfNull(element);
        ElementCompositionState state =
            States.GetValue(element, CreateState);
        if (ReferenceEquals(state.ChildVisual, visual))
            return;

        if (visual is not null &&
            !ReferenceEquals(
                state.ElementVisual.Compositor,
                visual.Compositor))
        {
            throw new InvalidOperationException(
                "The child visual must belong to the element visual's Compositor.");
        }

        if (state.ChildVisual is not null &&
            state.ChildVisual.IsAttachedTo(element))
        {
            state.ChildVisual.DetachFromCurrentParent();
        }

        state.ChildVisual = null;
        if (visual is null)
            return;

        visual.AttachToExternalHost(element);
        state.ChildVisual = visual;
    }

    private static ElementCompositionState CreateState(UIElement element)
    {
        Compositor compositor = Compositor.GetSharedForCurrentThread();
        return new ElementCompositionState(
            new Visual(compositor, element));
    }

    private sealed class ElementCompositionState
    {
        internal ElementCompositionState(Visual elementVisual)
        {
            ElementVisual = elementVisual;
        }

        internal Visual ElementVisual { get; }

        internal Visual? ChildVisual { get; set; }
    }
}
