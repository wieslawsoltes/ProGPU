namespace System.Drawing.Drawing2D;

public sealed class Blend
{
    public Blend()
        : this(1)
    {
    }

    public Blend(int count)
    {
        if (count < 0)
        {
            throw new OverflowException();
        }

        Factors = new float[count];
        Positions = new float[count];
    }

    public float[] Factors { get; set; }

    public float[] Positions { get; set; }

    internal Blend CloneBlend()
        => new(Factors.Length)
        {
            Factors = (float[])Factors.Clone(),
            Positions = (float[])Positions.Clone()
        };
}

public sealed class ColorBlend
{
    public ColorBlend()
        : this(1)
    {
    }

    public ColorBlend(int count)
    {
        if (count < 0)
        {
            throw new OverflowException();
        }

        Colors = new Color[count];
        Positions = new float[count];
    }

    public Color[] Colors { get; set; }

    public float[] Positions { get; set; }

    internal ColorBlend CloneBlend()
        => new(Colors.Length)
        {
            Colors = (Color[])Colors.Clone(),
            Positions = (float[])Positions.Clone()
        };
}
