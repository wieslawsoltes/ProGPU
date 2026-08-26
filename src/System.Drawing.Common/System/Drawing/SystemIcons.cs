using System.Drawing.Drawing2D;

namespace System.Drawing;

public enum StockIconId : uint
{
    DocumentNoAssociation = 0,
    DocumentWithAssociation = 1,
    Application = 2,
    Folder = 3,
    FolderOpen = 4,
    Drive525 = 5,
    Drive35 = 6,
    DriveRemovable = 7,
    DriveFixed = 8,
    DriveNet = 9,
    DriveNetDisabled = 10,
    DriveCD = 11,
    DriveRam = 12,
    World = 13,
    Server = 15,
    Printer = 16,
    MyNetwork = 17,
    Find = 22,
    Help = 23,
    Share = 28,
    Link = 29,
    SlowFile = 30,
    Recycler = 31,
    RecyclerFull = 32,
    MediaCDAudio = 40,
    Lock = 47,
    AutoList = 49,
    PrinterNet = 50,
    ServerShare = 51,
    PrinterFax = 52,
    PrinterFaxNet = 53,
    PrinterFile = 54,
    Stack = 55,
    MediaSVCD = 56,
    StuffedFolder = 57,
    DriveUnknown = 58,
    DriveDVD = 59,
    MediaDVD = 60,
    MediaDVDRAM = 61,
    MediaDVDRW = 62,
    MediaDVDR = 63,
    MediaDVDROM = 64,
    MediaCDAudioPlus = 65,
    MediaCDRW = 66,
    MediaCDR = 67,
    MediaCDBurn = 68,
    MediaBlankCD = 69,
    MediaCDROM = 70,
    AudioFiles = 71,
    ImageFiles = 72,
    VideoFiles = 73,
    MixedFiles = 74,
    FolderBack = 75,
    FolderFront = 76,
    Shield = 77,
    Warning = 78,
    Info = 79,
    Error = 80,
    Key = 81,
    Software = 82,
    Rename = 83,
    Delete = 84,
    MediaAudioDVD = 85,
    MediaMovieDVD = 86,
    MediaEnhancedCD = 87,
    MediaEnhancedDVD = 88,
    MediaHDDVD = 89,
    MediaBluRay = 90,
    MediaVCD = 91,
    MediaDVDPlusR = 92,
    MediaDVDPlusRW = 93,
    DesktopPC = 94,
    MobilePC = 95,
    Users = 96,
    MediaSmartMedia = 97,
    MediaCompactFlash = 98,
    DeviceCellPhone = 99,
    DeviceCamera = 100,
    DeviceVideoCamera = 101,
    DeviceAudioPlayer = 102,
    NetworkConnect = 103,
    Internet = 104,
    ZipFile = 105,
    Settings = 106,
    DriveHDDVD = 132,
    DriveBD = 133,
    MediaHDDVDROM = 134,
    MediaHDDVDR = 135,
    MediaHDDVDRAM = 136,
    MediaBDROM = 137,
    MediaBDR = 138,
    MediaBDRE = 139,
    ClusteredDrive = 140
}

[Flags]
public enum StockIconOptions
{
    Default = 0,
    SmallIcon = 1,
    ShellIconSize = 4,
    LinkOverlay = 0x8000,
    Selected = 0x10000
}

public static class SystemIcons
{
    private const StockIconOptions ValidOptions = StockIconOptions.SmallIcon
        | StockIconOptions.ShellIconSize
        | StockIconOptions.LinkOverlay
        | StockIconOptions.Selected;

    private static readonly Lazy<Icon> s_application = new(() => CreateStockIcon(StockIconId.Application, 32));
    private static readonly Lazy<Icon> s_error = new(() => CreateStockIcon(StockIconId.Error, 32));
    private static readonly Lazy<Icon> s_information = new(() => CreateStockIcon(StockIconId.Info, 32));
    private static readonly Lazy<Icon> s_shield = new(() => CreateStockIcon(StockIconId.Shield, 32));
    private static readonly Lazy<Icon> s_warning = new(() => CreateStockIcon(StockIconId.Warning, 32));

    public static Icon Application => s_application.Value;
    public static Icon Asterisk => Information;
    public static Icon Error => s_error.Value;
    public static Icon Exclamation => Warning;
    public static Icon Hand => Error;
    public static Icon Information => s_information.Value;
    public static Icon Question => Information;
    public static Icon Shield => s_shield.Value;
    public static Icon Warning => s_warning.Value;
    public static Icon WinLogo => Application;

    public static Icon GetStockIcon(
        StockIconId stockIcon,
        StockIconOptions options = StockIconOptions.Default)
    {
        ValidateStockIcon(stockIcon);
        ValidateOptions(options);

        int size = (options & StockIconOptions.SmallIcon) != 0 ? 16 : 32;
        return CreateStockIcon(stockIcon, size, options);
    }

    public static Icon GetStockIcon(StockIconId stockIcon, int size)
    {
        ValidateStockIcon(stockIcon);
        if (size <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(size));
        }

        return CreateStockIcon(stockIcon, size);
    }

    private static Icon CreateStockIcon(
        StockIconId stockIcon,
        int size,
        StockIconOptions options = StockIconOptions.Default)
    {
        var bitmap = new Bitmap(size, size);
        try
        {
            using (Graphics graphics = Graphics.FromImage(bitmap))
            {
                graphics.Clear(Color.Transparent);
                graphics.SmoothingMode = SmoothingMode.AntiAlias;
                DrawSemanticIcon(graphics, stockIcon, size);

                if ((options & StockIconOptions.LinkOverlay) != 0)
                {
                    DrawLinkOverlay(graphics, size);
                }

                if ((options & StockIconOptions.Selected) != 0)
                {
                    using var selection = new SolidBrush(Color.FromArgb(80, 55, 125, 230));
                    graphics.FillRectangle(selection, 0, 0, size, size);
                }
            }

            return Icon.CreateOwned(bitmap);
        }
        catch
        {
            bitmap.Dispose();
            throw;
        }
    }

    private static void DrawSemanticIcon(Graphics graphics, StockIconId stockIcon, int size)
    {
        if (stockIcon is StockIconId.Error or StockIconId.Warning or StockIconId.Info)
        {
            DrawNotification(graphics, stockIcon, size);
        }
        else if (stockIcon is StockIconId.Folder
            or StockIconId.FolderOpen
            or StockIconId.FolderBack
            or StockIconId.FolderFront
            or StockIconId.StuffedFolder)
        {
            DrawFolder(graphics, stockIcon, size);
        }
        else if (IsDrive(stockIcon))
        {
            DrawDrive(graphics, stockIcon, size);
        }
        else if (IsMedia(stockIcon))
        {
            DrawMedia(graphics, stockIcon, size);
        }
        else if (stockIcon is StockIconId.DocumentNoAssociation
            or StockIconId.DocumentWithAssociation
            or StockIconId.AudioFiles
            or StockIconId.ImageFiles
            or StockIconId.VideoFiles
            or StockIconId.MixedFiles
            or StockIconId.ZipFile)
        {
            DrawDocument(graphics, stockIcon, size);
        }
        else if (stockIcon is StockIconId.Printer
            or StockIconId.PrinterNet
            or StockIconId.PrinterFax
            or StockIconId.PrinterFaxNet
            or StockIconId.PrinterFile)
        {
            DrawPrinter(graphics, size);
        }
        else if (stockIcon is StockIconId.World
            or StockIconId.MyNetwork
            or StockIconId.NetworkConnect
            or StockIconId.Internet
            or StockIconId.Server
            or StockIconId.ServerShare)
        {
            DrawNetwork(graphics, stockIcon, size);
        }
        else if (stockIcon is StockIconId.Shield or StockIconId.Lock or StockIconId.Key)
        {
            DrawSecurity(graphics, stockIcon, size);
        }
        else if (stockIcon is StockIconId.DesktopPC
            or StockIconId.MobilePC
            or StockIconId.DeviceCellPhone
            or StockIconId.DeviceCamera
            or StockIconId.DeviceVideoCamera
            or StockIconId.DeviceAudioPlayer)
        {
            DrawDevice(graphics, stockIcon, size);
        }
        else if (stockIcon is StockIconId.Find
            or StockIconId.Help
            or StockIconId.Delete
            or StockIconId.Settings
            or StockIconId.Recycler
            or StockIconId.RecyclerFull)
        {
            DrawAction(graphics, stockIcon, size);
        }
        else
        {
            DrawApplication(graphics, stockIcon, size);
        }
    }

    private static bool IsDrive(StockIconId value) => value is
        StockIconId.Drive525 or StockIconId.Drive35 or StockIconId.DriveRemovable
        or StockIconId.DriveFixed or StockIconId.DriveNet or StockIconId.DriveNetDisabled
        or StockIconId.DriveCD or StockIconId.DriveRam or StockIconId.DriveUnknown
        or StockIconId.DriveDVD or StockIconId.DriveHDDVD or StockIconId.DriveBD
        or StockIconId.ClusteredDrive;

    private static bool IsMedia(StockIconId value) => value is
        StockIconId.MediaCDAudio or StockIconId.MediaSVCD or StockIconId.MediaDVD
        or StockIconId.MediaDVDRAM or StockIconId.MediaDVDRW or StockIconId.MediaDVDR
        or StockIconId.MediaDVDROM or StockIconId.MediaCDAudioPlus or StockIconId.MediaCDRW
        or StockIconId.MediaCDR or StockIconId.MediaCDBurn or StockIconId.MediaBlankCD
        or StockIconId.MediaCDROM or StockIconId.MediaAudioDVD or StockIconId.MediaMovieDVD
        or StockIconId.MediaEnhancedCD or StockIconId.MediaEnhancedDVD or StockIconId.MediaHDDVD
        or StockIconId.MediaBluRay or StockIconId.MediaVCD or StockIconId.MediaDVDPlusR
        or StockIconId.MediaDVDPlusRW or StockIconId.MediaSmartMedia
        or StockIconId.MediaCompactFlash or StockIconId.MediaHDDVDROM
        or StockIconId.MediaHDDVDR or StockIconId.MediaHDDVDRAM or StockIconId.MediaBDROM
        or StockIconId.MediaBDR or StockIconId.MediaBDRE;

    private static void DrawNotification(Graphics graphics, StockIconId id, int size)
    {
        Color color = id switch
        {
            StockIconId.Error => Color.FromArgb(210, 50, 45),
            StockIconId.Warning => Color.FromArgb(245, 180, 30),
            _ => Color.FromArgb(40, 120, 210)
        };
        using var fill = new SolidBrush(color);
        using var mark = new Pen(Color.White, Scale(size, 2.8f));
        graphics.FillEllipse(fill, Scale(size, 2f), Scale(size, 2f), Scale(size, 28f), Scale(size, 28f));

        if (id == StockIconId.Error)
        {
            graphics.DrawLine(mark, Scale(size, 10f), Scale(size, 10f), Scale(size, 22f), Scale(size, 22f));
            graphics.DrawLine(mark, Scale(size, 22f), Scale(size, 10f), Scale(size, 10f), Scale(size, 22f));
        }
        else
        {
            graphics.DrawLine(mark, Scale(size, 16f), Scale(size, 8f), Scale(size, 16f), Scale(size, 19f));
            graphics.DrawLine(mark, Scale(size, 16f), Scale(size, 24f), Scale(size, 16f), Scale(size, 24.2f));
        }
    }

    private static void DrawFolder(Graphics graphics, StockIconId id, int size)
    {
        Color color = id == StockIconId.FolderOpen
            ? Color.FromArgb(255, 185, 45)
            : Color.FromArgb(240, 165, 35);
        using var fill = new SolidBrush(color);
        using var outline = new Pen(Color.FromArgb(160, 100, 25), Scale(size, 1.4f));
        PointF[] points =
        [
            P(size, 3, 9), P(size, 12, 9), P(size, 15, 12),
            P(size, 29, 12), P(size, 27, 27), P(size, 3, 27)
        ];
        graphics.FillPolygon(fill, points);
        graphics.DrawPolygon(outline, points);
    }

    private static void DrawDocument(Graphics graphics, StockIconId id, int size)
    {
        using var paper = new SolidBrush(Color.FromArgb(245, 247, 250));
        using var accent = new SolidBrush(ColorFor(id));
        using var outline = new Pen(Color.FromArgb(80, 90, 105), Scale(size, 1.3f));
        PointF[] page = [P(size, 7, 3), P(size, 20, 3), P(size, 27, 10), P(size, 27, 29), P(size, 7, 29)];
        graphics.FillPolygon(paper, page);
        graphics.DrawPolygon(outline, page);
        graphics.FillRectangle(accent, Scale(size, 10), Scale(size, 17), Scale(size, 14), Scale(size, 7));
        graphics.DrawLine(outline, Scale(size, 20), Scale(size, 3), Scale(size, 20), Scale(size, 10));
        graphics.DrawLine(outline, Scale(size, 20), Scale(size, 10), Scale(size, 27), Scale(size, 10));
    }

    private static void DrawDrive(Graphics graphics, StockIconId id, int size)
    {
        using var fill = new SolidBrush(Color.FromArgb(115, 130, 145));
        using var face = new SolidBrush(Color.FromArgb(195, 205, 215));
        using var accent = new SolidBrush(ColorFor(id));
        graphics.FillRectangle(fill, Scale(size, 4), Scale(size, 6), Scale(size, 24), Scale(size, 20));
        graphics.FillRectangle(face, Scale(size, 6), Scale(size, 8), Scale(size, 20), Scale(size, 11));
        graphics.FillEllipse(accent, Scale(size, 21), Scale(size, 21), Scale(size, 3), Scale(size, 3));
    }

    private static void DrawMedia(Graphics graphics, StockIconId id, int size)
    {
        using var disc = new SolidBrush(Color.FromArgb(180, 195, 220));
        using var accent = new SolidBrush(ColorFor(id));
        using var outline = new Pen(Color.FromArgb(75, 90, 120), Scale(size, 1.2f));
        graphics.FillEllipse(disc, Scale(size, 3), Scale(size, 3), Scale(size, 26), Scale(size, 26));
        graphics.DrawEllipse(outline, Scale(size, 3), Scale(size, 3), Scale(size, 26), Scale(size, 26));
        graphics.FillEllipse(accent, Scale(size, 10), Scale(size, 10), Scale(size, 12), Scale(size, 12));
        graphics.FillEllipse(disc, Scale(size, 14), Scale(size, 14), Scale(size, 4), Scale(size, 4));
    }

    private static void DrawPrinter(Graphics graphics, int size)
    {
        using var body = new SolidBrush(Color.FromArgb(95, 110, 125));
        using var paper = new SolidBrush(Color.FromArgb(245, 247, 250));
        graphics.FillRectangle(paper, Scale(size, 8), Scale(size, 3), Scale(size, 16), Scale(size, 10));
        graphics.FillRectangle(body, Scale(size, 4), Scale(size, 10), Scale(size, 24), Scale(size, 14));
        graphics.FillRectangle(paper, Scale(size, 8), Scale(size, 18), Scale(size, 16), Scale(size, 11));
    }

    private static void DrawNetwork(Graphics graphics, StockIconId id, int size)
    {
        using var fill = new SolidBrush(ColorFor(id));
        using var line = new Pen(Color.White, Scale(size, 1.4f));
        graphics.FillEllipse(fill, Scale(size, 3), Scale(size, 3), Scale(size, 26), Scale(size, 26));
        graphics.DrawEllipse(line, Scale(size, 9), Scale(size, 3), Scale(size, 14), Scale(size, 26));
        graphics.DrawLine(line, Scale(size, 4), Scale(size, 12), Scale(size, 28), Scale(size, 12));
        graphics.DrawLine(line, Scale(size, 4), Scale(size, 20), Scale(size, 28), Scale(size, 20));
    }

    private static void DrawSecurity(Graphics graphics, StockIconId id, int size)
    {
        using var fill = new SolidBrush(id == StockIconId.Shield
            ? Color.FromArgb(45, 110, 180)
            : Color.FromArgb(225, 165, 35));
        using var line = new Pen(Color.White, Scale(size, 2f));
        PointF[] shield = [P(size, 16, 2), P(size, 28, 7), P(size, 25, 23), P(size, 16, 30), P(size, 7, 23), P(size, 4, 7)];
        graphics.FillPolygon(fill, shield);
        if (id == StockIconId.Key)
        {
            graphics.DrawEllipse(line, Scale(size, 9), Scale(size, 9), Scale(size, 8), Scale(size, 8));
            graphics.DrawLine(line, Scale(size, 16), Scale(size, 16), Scale(size, 24), Scale(size, 24));
        }
        else
        {
            graphics.DrawLine(line, Scale(size, 11), Scale(size, 16), Scale(size, 15), Scale(size, 21));
            graphics.DrawLine(line, Scale(size, 15), Scale(size, 21), Scale(size, 22), Scale(size, 11));
        }
    }

    private static void DrawDevice(Graphics graphics, StockIconId id, int size)
    {
        using var body = new SolidBrush(Color.FromArgb(65, 75, 90));
        using var screen = new SolidBrush(ColorFor(id));
        graphics.FillRectangle(body, Scale(size, 4), Scale(size, 4), Scale(size, 24), Scale(size, 21));
        graphics.FillRectangle(screen, Scale(size, 7), Scale(size, 7), Scale(size, 18), Scale(size, 14));
        graphics.FillRectangle(body, Scale(size, 12), Scale(size, 25), Scale(size, 8), Scale(size, 4));
    }

    private static void DrawAction(Graphics graphics, StockIconId id, int size)
    {
        using var fill = new SolidBrush(ColorFor(id));
        using var mark = new Pen(Color.White, Scale(size, 2.5f));
        graphics.FillEllipse(fill, Scale(size, 3), Scale(size, 3), Scale(size, 26), Scale(size, 26));
        if (id == StockIconId.Find)
        {
            graphics.DrawEllipse(mark, Scale(size, 8), Scale(size, 8), Scale(size, 10), Scale(size, 10));
            graphics.DrawLine(mark, Scale(size, 17), Scale(size, 17), Scale(size, 24), Scale(size, 24));
        }
        else if (id == StockIconId.Help)
        {
            graphics.DrawLine(mark, Scale(size, 12), Scale(size, 11), Scale(size, 16), Scale(size, 8));
            graphics.DrawLine(mark, Scale(size, 16), Scale(size, 8), Scale(size, 20), Scale(size, 11));
            graphics.DrawLine(mark, Scale(size, 20), Scale(size, 11), Scale(size, 16), Scale(size, 17));
            graphics.DrawLine(mark, Scale(size, 16), Scale(size, 17), Scale(size, 16), Scale(size, 20));
        }
        else
        {
            graphics.DrawLine(mark, Scale(size, 10), Scale(size, 10), Scale(size, 22), Scale(size, 22));
            graphics.DrawLine(mark, Scale(size, 22), Scale(size, 10), Scale(size, 10), Scale(size, 22));
        }
    }

    private static void DrawApplication(Graphics graphics, StockIconId id, int size)
    {
        using var fill = new SolidBrush(ColorFor(id));
        using var tile = new SolidBrush(Color.FromArgb(220, 235, 250));
        graphics.FillRectangle(fill, Scale(size, 3), Scale(size, 3), Scale(size, 26), Scale(size, 26));
        graphics.FillRectangle(tile, Scale(size, 7), Scale(size, 7), Scale(size, 7), Scale(size, 7));
        graphics.FillRectangle(tile, Scale(size, 18), Scale(size, 7), Scale(size, 7), Scale(size, 7));
        graphics.FillRectangle(tile, Scale(size, 7), Scale(size, 18), Scale(size, 7), Scale(size, 7));
        graphics.FillRectangle(tile, Scale(size, 18), Scale(size, 18), Scale(size, 7), Scale(size, 7));
    }

    private static void DrawLinkOverlay(Graphics graphics, int size)
    {
        using var fill = new SolidBrush(Color.FromArgb(35, 105, 220));
        using var arrow = new Pen(Color.White, Scale(size, 1.8f));
        graphics.FillEllipse(fill, Scale(size, 17), Scale(size, 17), Scale(size, 14), Scale(size, 14));
        graphics.DrawLine(arrow, Scale(size, 21), Scale(size, 25), Scale(size, 27), Scale(size, 19));
        graphics.DrawLine(arrow, Scale(size, 22), Scale(size, 19), Scale(size, 27), Scale(size, 19));
        graphics.DrawLine(arrow, Scale(size, 27), Scale(size, 19), Scale(size, 27), Scale(size, 24));
    }

    private static Color ColorFor(StockIconId id)
    {
        uint value = (uint)id;
        return Color.FromArgb(
            55 + (int)((value * 47u) % 150u),
            70 + (int)((value * 71u) % 135u),
            85 + (int)((value * 29u) % 120u));
    }

    private static PointF P(int size, float x, float y) => new(Scale(size, x), Scale(size, y));

    private static float Scale(int size, float value) => value * size / 32f;

    private static void ValidateStockIcon(StockIconId stockIcon)
    {
        if (!Enum.IsDefined(stockIcon))
        {
            throw new ArgumentException("The stock icon identifier is invalid.", nameof(stockIcon));
        }
    }

    private static void ValidateOptions(StockIconOptions options)
    {
        if ((options & ~ValidOptions) != 0)
        {
            throw new ArgumentException("The stock icon options contain unsupported flags.", nameof(options));
        }
    }
}
