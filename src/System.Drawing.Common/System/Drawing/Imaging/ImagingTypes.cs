using System;

namespace System.Drawing.Imaging;

public enum PixelFormat
{
    Format32bppArgb = 2498570,
    Format24bppRgb = 137224,
    Format8bppIndexed = 198658,
    Format32bppRgb = 139273,
    Format32bppPArgb = 925707,
    Format16bppRgb565 = 135173
}

public enum ImageLockMode
{
    ReadOnly = 1,
    WriteOnly = 2,
    ReadWrite = 3,
    UserInputBuffer = 4
}

public enum ColorMatrixFlag
{
    Default = 0,
    SkipGrays = 1,
    AltGrays = 2
}

public enum ColorAdjustType
{
    Default = 0,
    Bitmap = 1,
    Brush = 2,
    Pen = 3,
    Text = 4,
    Count = 5,
    Any = 6
}

public sealed class ColorMatrix
{
    private readonly float[][] _matrix;

    public ColorMatrix()
    {
        _matrix = CreateMatrix();
    }

    public ColorMatrix(float[][] newColorMatrix)
    {
        ArgumentNullException.ThrowIfNull(newColorMatrix);

        _matrix = CreateMatrix();
        for (int row = 0; row < Math.Min(5, newColorMatrix.Length); row++)
        {
            var sourceRow = newColorMatrix[row];
            if (sourceRow == null)
            {
                continue;
            }

            for (int column = 0; column < Math.Min(5, sourceRow.Length); column++)
            {
                _matrix[row][column] = sourceRow[column];
            }
        }
    }

    public float[][] Matrix => _matrix;

    public float this[int row, int column]
    {
        get => _matrix[row][column];
        set => _matrix[row][column] = value;
    }

    public float Matrix00 { get => this[0, 0]; set => this[0, 0] = value; }
    public float Matrix01 { get => this[0, 1]; set => this[0, 1] = value; }
    public float Matrix02 { get => this[0, 2]; set => this[0, 2] = value; }
    public float Matrix03 { get => this[0, 3]; set => this[0, 3] = value; }
    public float Matrix04 { get => this[0, 4]; set => this[0, 4] = value; }
    public float Matrix10 { get => this[1, 0]; set => this[1, 0] = value; }
    public float Matrix11 { get => this[1, 1]; set => this[1, 1] = value; }
    public float Matrix12 { get => this[1, 2]; set => this[1, 2] = value; }
    public float Matrix13 { get => this[1, 3]; set => this[1, 3] = value; }
    public float Matrix14 { get => this[1, 4]; set => this[1, 4] = value; }
    public float Matrix20 { get => this[2, 0]; set => this[2, 0] = value; }
    public float Matrix21 { get => this[2, 1]; set => this[2, 1] = value; }
    public float Matrix22 { get => this[2, 2]; set => this[2, 2] = value; }
    public float Matrix23 { get => this[2, 3]; set => this[2, 3] = value; }
    public float Matrix24 { get => this[2, 4]; set => this[2, 4] = value; }
    public float Matrix30 { get => this[3, 0]; set => this[3, 0] = value; }
    public float Matrix31 { get => this[3, 1]; set => this[3, 1] = value; }
    public float Matrix32 { get => this[3, 2]; set => this[3, 2] = value; }
    public float Matrix33 { get => this[3, 3]; set => this[3, 3] = value; }
    public float Matrix34 { get => this[3, 4]; set => this[3, 4] = value; }
    public float Matrix40 { get => this[4, 0]; set => this[4, 0] = value; }
    public float Matrix41 { get => this[4, 1]; set => this[4, 1] = value; }
    public float Matrix42 { get => this[4, 2]; set => this[4, 2] = value; }
    public float Matrix43 { get => this[4, 3]; set => this[4, 3] = value; }
    public float Matrix44 { get => this[4, 4]; set => this[4, 4] = value; }

    private static float[][] CreateMatrix()
    {
        var matrix = new[]
        {
            new float[5],
            new float[5],
            new float[5],
            new float[5],
            new float[5]
        };
        matrix[0][0] = 1f;
        matrix[1][1] = 1f;
        matrix[2][2] = 1f;
        matrix[3][3] = 1f;
        matrix[4][4] = 1f;
        return matrix;
    }
}

public sealed class ImageAttributes : IDisposable
{
    public ColorMatrix? ColorMatrix { get; private set; }

    public void SetColorMatrix(ColorMatrix newColorMatrix)
    {
        SetColorMatrix(newColorMatrix, ColorMatrixFlag.Default, ColorAdjustType.Default);
    }

    public void SetColorMatrix(ColorMatrix newColorMatrix, ColorMatrixFlag mode, ColorAdjustType type)
    {
        ArgumentNullException.ThrowIfNull(newColorMatrix);
        ColorMatrix = newColorMatrix;
    }

    public void Dispose()
    {
    }
}

public sealed class BitmapData
{
    public int Width { get; set; }
    public int Height { get; set; }
    public int Stride { get; set; }
    public PixelFormat PixelFormat { get; set; }
    public IntPtr Scan0 { get; set; }
    public int Reserved { get; set; }
}

public sealed class ImageFormat
{
    public Guid Guid { get; }

    public ImageFormat(Guid guid)
    {
        Guid = guid;
    }

    public static ImageFormat Bmp { get; } = new ImageFormat(new Guid("b96b3cab-0728-11d3-9d7b-0000f81ef32e"));
    public static ImageFormat Jpeg { get; } = new ImageFormat(new Guid("b96b3cae-0728-11d3-9d7b-0000f81ef32e"));
    public static ImageFormat Png { get; } = new ImageFormat(new Guid("b96b3caf-0728-11d3-9d7b-0000f81ef32e"));
    public static ImageFormat Gif { get; } = new ImageFormat(new Guid("b96b3cb0-0728-11d3-9d7b-0000f81ef32e"));
}
