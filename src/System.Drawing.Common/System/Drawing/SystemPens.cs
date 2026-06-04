namespace System.Drawing;

public static class SystemPens
{
    public static Pen Control { get; } = new Pen(Color.LightGray);
    public static Pen ControlText { get; } = new Pen(Color.Black);
    public static Pen Window { get; } = new Pen(Color.White);
    public static Pen WindowText { get; } = new Pen(Color.Black);
    public static Pen Highlight { get; } = new Pen(Color.Blue);
    public static Pen HighlightText { get; } = new Pen(Color.White);
}
