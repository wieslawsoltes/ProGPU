namespace Microsoft.UI.Xaml.Automation.Peers;

public abstract class AutomationPeer
{
    public virtual object? GetPattern(PatternInterface patternInterface) => null;
    public virtual string GetClassName() => GetType().Name;
    public virtual AutomationControlType GetAutomationControlType() => AutomationControlType.Custom;
    public virtual string GetName() => string.Empty;
    public virtual bool IsKeyboardFocusable() => false;
    public virtual bool HasKeyboardFocus() => false;
}
