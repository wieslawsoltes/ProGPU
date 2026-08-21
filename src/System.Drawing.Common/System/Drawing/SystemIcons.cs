namespace System.Drawing;

public enum StockIconId : uint
{
    Application = 2,
    Error = 80,
    Warning = 78,
    Info = 79,
    Shield = 77
}

public static class SystemIcons
{
    private static readonly Lazy<Icon> s_application = new(() => CreateStockIcon(StockIconId.Application, 32));
    private static readonly Lazy<Icon> s_error = new(() => CreateStockIcon(StockIconId.Error, 32));
    private static readonly Lazy<Icon> s_information = new(() => CreateStockIcon(StockIconId.Info, 32));
    private static readonly Lazy<Icon> s_warning = new(() => CreateStockIcon(StockIconId.Warning, 32));

    public static Icon Application => s_application.Value;
    public static Icon Asterisk => Information;
    public static Icon Error => s_error.Value;
    public static Icon Exclamation => Warning;
    public static Icon Hand => Error;
    public static Icon Information => s_information.Value;
    public static Icon Question => Information;
    public static Icon Shield => Warning;
    public static Icon Warning => s_warning.Value;
    public static Icon WinLogo => Application;

    public static Icon GetStockIcon(StockIconId stockIcon, int size) => CreateStockIcon(stockIcon, size);

    private static Icon CreateStockIcon(StockIconId stockIcon, int size)
    {
        if (size <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(size));
        }

        using var bitmap = new Bitmap(size, size);
        using (Graphics graphics = Graphics.FromImage(bitmap))
        {
            graphics.Clear(Color.Transparent);
            Color background = stockIcon switch
            {
                StockIconId.Error => Color.FromArgb(210, 50, 45),
                StockIconId.Warning => Color.FromArgb(245, 180, 30),
                StockIconId.Info => Color.FromArgb(40, 120, 210),
                StockIconId.Shield => Color.FromArgb(45, 110, 180),
                _ => Color.FromArgb(90, 100, 115)
            };
            using var fill = new SolidBrush(background);
            graphics.FillEllipse(fill, 1, 1, size - 2, size - 2);
            using var mark = new Pen(Color.White, MathF.Max(2f, size / 8f));
            if (stockIcon == StockIconId.Error)
            {
                graphics.DrawLine(mark, size * 0.3f, size * 0.3f, size * 0.7f, size * 0.7f);
                graphics.DrawLine(mark, size * 0.7f, size * 0.3f, size * 0.3f, size * 0.7f);
            }
            else
            {
                graphics.DrawLine(mark, size * 0.5f, size * 0.28f, size * 0.5f, size * 0.6f);
                graphics.DrawLine(mark, size * 0.5f, size * 0.72f, size * 0.5f, size * 0.73f);
            }
        }

        using var stream = new MemoryStream();
        bitmap.Save(stream, Imaging.ImageFormat.Png);
        stream.Position = 0;
        return new Icon(stream);
    }
}
