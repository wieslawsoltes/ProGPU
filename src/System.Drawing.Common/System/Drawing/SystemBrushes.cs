namespace System.Drawing;

public static class SystemBrushes
{
    public static Brush Control { get; } = new SolidBrush(Color.LightGray);
    public static Brush ControlText { get; } = new SolidBrush(Color.Black);
    public static Brush Window { get; } = new SolidBrush(Color.White);
    public static Brush WindowText { get; } = new SolidBrush(Color.Black);
    public static Brush Highlight { get; } = new SolidBrush(Color.Blue);
    public static Brush HighlightText { get; } = new SolidBrush(Color.White);
}
