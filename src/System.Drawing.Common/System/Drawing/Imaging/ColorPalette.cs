namespace System.Drawing.Imaging;

[Flags]
public enum PaletteFlags
{
    HasAlpha = 1,
    GrayScale = 2,
    Halftone = 4
}

public enum PaletteType
{
    Custom = 0,
    FixedBlackAndWhite = 2,
    FixedHalftone8 = 3,
    FixedHalftone27 = 4,
    FixedHalftone64 = 5,
    FixedHalftone125 = 6,
    FixedHalftone216 = 7,
    FixedHalftone252 = 8,
    FixedHalftone256 = 9
}

public sealed class ColorPalette
{
    private static readonly Color[] s_systemPaletteColors =
    [
        Color.FromArgb(0, 0, 0),
        Color.FromArgb(128, 0, 0),
        Color.FromArgb(0, 128, 0),
        Color.FromArgb(128, 128, 0),
        Color.FromArgb(0, 0, 128),
        Color.FromArgb(128, 0, 128),
        Color.FromArgb(0, 128, 128),
        Color.FromArgb(192, 192, 192),
        Color.FromArgb(128, 128, 128),
        Color.FromArgb(255, 0, 0),
        Color.FromArgb(0, 255, 0),
        Color.FromArgb(255, 255, 0),
        Color.FromArgb(0, 0, 255),
        Color.FromArgb(255, 0, 255),
        Color.FromArgb(0, 255, 255),
        Color.FromArgb(255, 255, 255)
    ];

    private readonly int _flags;
    private readonly Color[] _entries;

    private ColorPalette()
        : this(0, [])
    {
    }

    private ColorPalette(int flags, Color[] entries)
    {
        _flags = flags;
        _entries = entries;
    }

    public ColorPalette(params Color[] customColors)
        : this(0, customColors ?? throw new ArgumentNullException(nameof(customColors)))
    {
    }

    public ColorPalette(PaletteType fixedPaletteType)
    {
        (_flags, _entries) = CreateFixedPalette(fixedPaletteType);
    }

    public int Flags => _flags;

    public Color[] Entries => _entries;

    public static ColorPalette CreateOptimalPalette(
        int colors,
        bool useTransparentColor,
        Bitmap bitmap)
    {
        ArgumentNullException.ThrowIfNull(bitmap);
        if (colors is < 1 or > 256)
        {
            throw new ArgumentOutOfRangeException(nameof(colors), "Palette size must be between 1 and 256 colors.");
        }

        int opaqueCapacity = colors - (useTransparentColor ? 1 : 0);
        if (opaqueCapacity == 0)
        {
            return new ColorPalette((int)PaletteFlags.HasAlpha, [Color.Transparent]);
        }

        byte[] pixels = bitmap.CopyStraightPixelsForPalette();
        var histogram = new Dictionary<int, int>();
        for (int offset = 0; offset < pixels.Length; offset += 4)
        {
            int alpha = pixels[offset + 3];
            if (useTransparentColor && alpha == 0)
            {
                continue;
            }

            int argb = (alpha << 24)
                | (pixels[offset] << 16)
                | (pixels[offset + 1] << 8)
                | pixels[offset + 2];
            histogram[argb] = histogram.TryGetValue(argb, out int count) ? count + 1 : 1;
        }

        var samples = new List<ColorSample>(histogram.Count);
        foreach ((int argb, int count) in histogram)
        {
            samples.Add(new ColorSample(argb, count));
        }

        List<Color> quantized = Quantize(samples, opaqueCapacity);
        int outputCount = checked(quantized.Count + (useTransparentColor ? 1 : 0));
        var entries = new Color[outputCount];
        int destinationIndex = 0;
        if (useTransparentColor)
        {
            entries[destinationIndex++] = Color.Transparent;
        }

        quantized.CopyTo(entries, destinationIndex);
        return new ColorPalette(useTransparentColor ? (int)PaletteFlags.HasAlpha : 0, entries);
    }

    internal ColorPalette ClonePalette() =>
        new(_flags, (Color[])_entries.Clone());

    private static (int Flags, Color[] Entries) CreateFixedPalette(PaletteType paletteType)
    {
        return paletteType switch
        {
            PaletteType.Custom => (0, []),
            PaletteType.FixedBlackAndWhite =>
                ((int)(PaletteFlags.GrayScale | PaletteFlags.Halftone), [Color.Black, Color.White]),
            PaletteType.FixedHalftone8 => CreateSystemHalftone(2),
            PaletteType.FixedHalftone27 => CreateSystemHalftone(3),
            PaletteType.FixedHalftone64 => CreateSystemHalftone(4),
            PaletteType.FixedHalftone125 => CreateSystemHalftone(5),
            PaletteType.FixedHalftone216 => CreateSystemHalftone(6),
            PaletteType.FixedHalftone252 => CreateColorCube(6, 7, 6),
            PaletteType.FixedHalftone256 => CreateColorCube(8, 8, 4),
            _ => throw new ArgumentException("Invalid fixed palette type.", nameof(paletteType))
        };
    }

    private static (int Flags, Color[] Entries) CreateSystemHalftone(int levels)
    {
        (_, Color[] cube) = CreateColorCube(levels, levels, levels);
        var entries = new List<Color>(checked(cube.Length + 8));
        entries.AddRange(cube);

        foreach (Color systemColor in s_systemPaletteColors)
        {
            if (!entries.Contains(systemColor))
            {
                entries.Add(systemColor);
            }
        }

        return ((int)PaletteFlags.Halftone, entries.ToArray());
    }

    private static (int Flags, Color[] Entries) CreateColorCube(
        int redLevels,
        int greenLevels,
        int blueLevels)
    {
        var entries = new Color[checked(redLevels * greenLevels * blueLevels)];
        int index = 0;
        for (int red = 0; red < redLevels; red++)
        {
            for (int green = 0; green < greenLevels; green++)
            {
                for (int blue = 0; blue < blueLevels; blue++)
                {
                    entries[index++] = Color.FromArgb(
                        ScaleIntensity(red, redLevels),
                        ScaleIntensity(green, greenLevels),
                        ScaleIntensity(blue, blueLevels));
                }
            }
        }

        return ((int)PaletteFlags.Halftone, entries);
    }

    private static int ScaleIntensity(int index, int levels) =>
        levels <= 1 ? 0 : index * 255 / (levels - 1);

    private static List<Color> Quantize(List<ColorSample> samples, int capacity)
    {
        if (samples.Count == 0)
        {
            return [];
        }

        if (samples.Count <= capacity)
        {
            samples.Sort(ColorSampleFrequencyComparer.Instance);
            return samples.Select(static sample => Color.FromArgb(sample.Argb)).ToList();
        }

        var boxes = new List<PaletteBox>(capacity) { new(samples) };
        while (boxes.Count < capacity)
        {
            int splitIndex = -1;
            long splitScore = -1;
            for (int index = 0; index < boxes.Count; index++)
            {
                PaletteBox box = boxes[index];
                if (box.Samples.Count < 2)
                {
                    continue;
                }

                long score = (long)box.MaxRange * box.TotalWeight;
                if (score > splitScore)
                {
                    splitScore = score;
                    splitIndex = index;
                }
            }

            if (splitIndex < 0)
            {
                break;
            }

            PaletteBox selected = boxes[splitIndex];
            (PaletteBox first, PaletteBox second) = selected.Split();
            boxes[splitIndex] = first;
            boxes.Add(second);
        }

        boxes.Sort(PaletteBoxWeightComparer.Instance);
        var result = new List<Color>(boxes.Count);
        foreach (PaletteBox box in boxes)
        {
            result.Add(box.GetWeightedAverage());
        }

        return result;
    }

    private readonly record struct ColorSample(int Argb, int Count)
    {
        public byte Alpha => (byte)(Argb >> 24);
        public byte Red => (byte)(Argb >> 16);
        public byte Green => (byte)(Argb >> 8);
        public byte Blue => (byte)Argb;
    }

    private sealed class PaletteBox
    {
        public PaletteBox(List<ColorSample> samples)
        {
            Samples = samples;
            int minimumAlpha = byte.MaxValue;
            int minimumRed = byte.MaxValue;
            int minimumGreen = byte.MaxValue;
            int minimumBlue = byte.MaxValue;
            int maximumAlpha = byte.MinValue;
            int maximumRed = byte.MinValue;
            int maximumGreen = byte.MinValue;
            int maximumBlue = byte.MinValue;
            int totalWeight = 0;

            foreach (ColorSample sample in samples)
            {
                minimumAlpha = Math.Min(minimumAlpha, sample.Alpha);
                minimumRed = Math.Min(minimumRed, sample.Red);
                minimumGreen = Math.Min(minimumGreen, sample.Green);
                minimumBlue = Math.Min(minimumBlue, sample.Blue);
                maximumAlpha = Math.Max(maximumAlpha, sample.Alpha);
                maximumRed = Math.Max(maximumRed, sample.Red);
                maximumGreen = Math.Max(maximumGreen, sample.Green);
                maximumBlue = Math.Max(maximumBlue, sample.Blue);
                totalWeight = checked(totalWeight + sample.Count);
            }

            AlphaRange = maximumAlpha - minimumAlpha;
            RedRange = maximumRed - minimumRed;
            GreenRange = maximumGreen - minimumGreen;
            BlueRange = maximumBlue - minimumBlue;
            TotalWeight = totalWeight;
        }

        public List<ColorSample> Samples { get; }
        public int AlphaRange { get; }
        public int RedRange { get; }
        public int GreenRange { get; }
        public int BlueRange { get; }
        public int TotalWeight { get; }
        public int MaxRange => Math.Max(Math.Max(AlphaRange, RedRange), Math.Max(GreenRange, BlueRange));

        public (PaletteBox First, PaletteBox Second) Split()
        {
            int channel = GetSplitChannel();
            Samples.Sort((left, right) =>
            {
                int comparison = GetChannel(left, channel).CompareTo(GetChannel(right, channel));
                return comparison != 0 ? comparison : left.Argb.CompareTo(right.Argb);
            });

            int halfWeight = (TotalWeight + 1) / 2;
            int accumulatedWeight = 0;
            int splitPosition = 1;
            for (; splitPosition < Samples.Count; splitPosition++)
            {
                accumulatedWeight += Samples[splitPosition - 1].Count;
                if (accumulatedWeight >= halfWeight)
                {
                    break;
                }
            }

            splitPosition = Math.Clamp(splitPosition, 1, Samples.Count - 1);
            return (
                new PaletteBox(Samples.GetRange(0, splitPosition)),
                new PaletteBox(Samples.GetRange(splitPosition, Samples.Count - splitPosition)));
        }

        public Color GetWeightedAverage()
        {
            long alpha = 0;
            long red = 0;
            long green = 0;
            long blue = 0;
            foreach (ColorSample sample in Samples)
            {
                alpha += (long)sample.Alpha * sample.Count;
                red += (long)sample.Red * sample.Count;
                green += (long)sample.Green * sample.Count;
                blue += (long)sample.Blue * sample.Count;
            }

            return Color.FromArgb(
                (int)((alpha + TotalWeight / 2L) / TotalWeight),
                (int)((red + TotalWeight / 2L) / TotalWeight),
                (int)((green + TotalWeight / 2L) / TotalWeight),
                (int)((blue + TotalWeight / 2L) / TotalWeight));
        }

        private int GetSplitChannel()
        {
            int channel = 0;
            int range = AlphaRange;
            if (RedRange > range)
            {
                channel = 1;
                range = RedRange;
            }
            if (GreenRange > range)
            {
                channel = 2;
                range = GreenRange;
            }
            if (BlueRange > range)
            {
                channel = 3;
            }
            return channel;
        }

        private static byte GetChannel(ColorSample sample, int channel) =>
            channel switch
            {
                0 => sample.Alpha,
                1 => sample.Red,
                2 => sample.Green,
                _ => sample.Blue
            };
    }

    private sealed class ColorSampleFrequencyComparer : IComparer<ColorSample>
    {
        public static ColorSampleFrequencyComparer Instance { get; } = new();

        public int Compare(ColorSample left, ColorSample right)
        {
            int comparison = right.Count.CompareTo(left.Count);
            return comparison != 0 ? comparison : left.Argb.CompareTo(right.Argb);
        }
    }

    private sealed class PaletteBoxWeightComparer : IComparer<PaletteBox>
    {
        public static PaletteBoxWeightComparer Instance { get; } = new();

        public int Compare(PaletteBox? left, PaletteBox? right)
        {
            if (ReferenceEquals(left, right))
            {
                return 0;
            }
            if (left is null)
            {
                return 1;
            }
            if (right is null)
            {
                return -1;
            }

            int comparison = right.TotalWeight.CompareTo(left.TotalWeight);
            return comparison != 0 ? comparison : left.GetWeightedAverage().ToArgb().CompareTo(right.GetWeightedAverage().ToArgb());
        }
    }

}
