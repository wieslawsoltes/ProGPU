namespace System.Drawing;

public static class Pens
{
    public static Pen Transparent { get; } = new Pen(Color.Transparent);
    public static Pen Black { get; } = new Pen(Color.Black);
    public static Pen White { get; } = new Pen(Color.White);
    public static Pen Red { get; } = new Pen(Color.Red);
    public static Pen Green { get; } = new Pen(Color.Green);
    public static Pen Blue { get; } = new Pen(Color.Blue);
    public static Pen Yellow { get; } = new Pen(Color.Yellow);
    public static Pen Gray { get; } = new Pen(Color.Gray);
    public static Pen DarkGray { get; } = new Pen(Color.DarkGray);
    public static Pen LightGray { get; } = new Pen(Color.LightGray);
    public static Pen DarkGreen { get; } = new Pen(Color.FromArgb(255, 0, 100, 0));
}
