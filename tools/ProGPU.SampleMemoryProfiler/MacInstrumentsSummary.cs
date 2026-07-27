using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;

internal static class MacInstrumentsSummary
{
    public static int Run(string[] args)
    {
        if (args.Length != 2)
        {
            Console.Error.WriteLine(
                "Usage: progpu-memory instruments-summary <capture-directory>");
            return 2;
        }

        string directory = Path.GetFullPath(args[1]);
        if (!Directory.Exists(directory))
        {
            Console.Error.WriteLine(
                $"Instruments capture directory does not exist: {directory}");
            return 3;
        }

        Write(directory);
        return 0;
    }

    public static InstrumentsCaptureSummary Write(string directory)
    {
        string fullDirectory = Path.GetFullPath(directory);
        MetalResourceSummary resources = SummarizeResources(
            FindExport(fullDirectory, "metal-resource-allocations"));
        NativeAllocationSummary nativeAllocations =
            SummarizeNativeAllocations(
                FindExport(fullDirectory, "allocations-statistics"),
                FindExport(fullDirectory, "allocations-list"));
        NumericSeriesSummary currentAllocated = SummarizeSeries(
            FindExport(fullDirectory, "metal-current-allocated-size"),
            valueColumn: 4);
        NumericSeriesSummary drawableWaits = SummarizeSeries(
            FindExport(fullDirectory, "ca-client-buffer-wait-interval"),
            valueColumn: 1);
        NumericSeriesSummary spills = SummarizeSeries(
            FindExport(fullDirectory, "graphics-compiler-spill-events"),
            valueColumn: 3);
        SpillGroupSummary[] spillGroups = SummarizeSpillGroups(
            FindExport(fullDirectory, "graphics-compiler-spill-events"),
            FindExport(
                fullDirectory,
                "metal-application-command-buffer-submissions"));
        HangSummary hangs = SummarizeHangs(
            FindExport(fullDirectory, "potential-hangs"),
            FindExport(fullDirectory, "time-profile"));
        int hangRiskCount = CountRows(
            FindExport(fullDirectory, "hang-risks"));
        int commandBufferErrorCount = CountRows(
            FindExport(fullDirectory, "metal-command-buffer-error"));
        int submissionCount = CountRows(
            FindExport(
                fullDirectory,
                "metal-application-command-buffer-submissions"));
        int completionCount = CountRows(
            FindExport(
                fullDirectory,
                "metal-command-buffer-completed"));

        var summary = new InstrumentsCaptureSummary(
            SchemaVersion: 2,
            GeneratedUtc: DateTimeOffset.UtcNow,
            Resources: resources,
            NativeAllocations: nativeAllocations,
            CurrentAllocatedSize: currentAllocated,
            DrawableWaits: drawableWaits,
            CompilerSpills: spills,
            CompilerSpillGroups: spillGroups,
            PotentialHangs: hangs,
            HangRiskCount: hangRiskCount,
            CommandBufferErrorCount: commandBufferErrorCount,
            CommandBufferSubmissionCount: submissionCount,
            CommandBufferCompletionCount: completionCount);
        string jsonPath = Path.Combine(
            fullDirectory,
            "instruments-summary.json");
        string markdownPath = Path.Combine(
            fullDirectory,
            "instruments-summary.md");
        File.WriteAllText(
            jsonPath,
            JsonSerializer.Serialize(
                summary,
                new JsonSerializerOptions { WriteIndented = true }));
        File.WriteAllText(markdownPath, FormatMarkdown(summary));
        Console.WriteLine($"[Instruments] summary={markdownPath}");
        return summary;
    }

    private static NativeAllocationSummary SummarizeNativeAllocations(
        string? path,
        string? detailsPath)
    {
        XmlTable? table = XmlTable.TryLoad(path);
        if (table is null)
        {
            return NativeAllocationSummary.Empty;
        }

        var categories = new List<NativeAllocationCategorySummary>(
            table.Rows.Count);
        foreach (XElement row in table.Rows)
        {
            string? category = row.Attribute("category")?.Value;
            if (string.IsNullOrWhiteSpace(category))
            {
                continue;
            }

            categories.Add(
                new NativeAllocationCategorySummary(
                    category,
                    ReadAttributeInt64(row, "persistent-bytes"),
                    ReadAttributeInt64(row, "total-bytes"),
                    ReadAttributeInt64(row, "transient-bytes"),
                    ReadAttributeInt64(row, "count-persistent"),
                    ReadAttributeInt64(row, "count-total")));
        }

        NativeAllocationCategorySummary heapAndAnonymous =
            FindNativeCategory(categories, "All Heap & Anonymous VM");
        NativeAllocationCategorySummary heap =
            FindNativeCategory(categories, "All Heap Allocations");
        NativeAllocationCategorySummary anonymous =
            FindNativeCategory(categories, "All Anonymous VM");
        NativeAllocationCategorySummary allRegions =
            FindNativeCategory(categories, "All VM Regions");
        NativeAllocationCategorySummary[] largest =
            categories
                .Where(category =>
                    !category.Category.StartsWith(
                        "All ",
                        StringComparison.Ordinal) &&
                    category.PersistentBytes > 0)
                .OrderByDescending(category => category.PersistentBytes)
                .ThenBy(category => category.Category, StringComparer.Ordinal)
                .Take(24)
                .ToArray();
        return new NativeAllocationSummary(
            heapAndAnonymous.PersistentBytes,
            heap.PersistentBytes,
            anonymous.PersistentBytes,
            allRegions.PersistentBytes,
            heapAndAnonymous.TotalBytes,
            heapAndAnonymous.TransientBytes,
            largest,
            SummarizeNativeAllocationDetails(detailsPath));
    }

    private static NativeAllocationDetailSummary
        SummarizeNativeAllocationDetails(string? path)
    {
        XmlTable? table = XmlTable.TryLoad(path);
        if (table is null)
        {
            return NativeAllocationDetailSummary.Empty;
        }

        var groups = new Dictionary<
            (string Category, string Caller, string Library),
            MutableNativeAllocationDetail>();
        long liveCount = 0;
        long liveBytes = 0;
        foreach (XElement row in table.Rows)
        {
            if (!string.Equals(
                    row.Attribute("live")?.Value,
                    "true",
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string category =
                row.Attribute("category")?.Value ?? "Unknown";
            string caller =
                row.Attribute("responsible-caller")?.Value ?? "Unavailable";
            string library =
                row.Attribute("responsible-library")?.Value ?? "Unavailable";
            long size = ReadAttributeInt64(row, "size");
            string timestamp =
                row.Attribute("timestamp")?.Value ?? string.Empty;
            var key = (category, caller, library);
            if (!groups.TryGetValue(
                    key,
                    out MutableNativeAllocationDetail? group))
            {
                group = new MutableNativeAllocationDetail(
                    category,
                    caller,
                    library);
                groups.Add(key, group);
            }

            group.Add(size, timestamp);
            checked
            {
                liveCount++;
                liveBytes += size;
            }
        }

        return new NativeAllocationDetailSummary(
            liveCount,
            liveBytes,
            groups.Values
                .Select(group => group.ToSummary())
                .OrderByDescending(group => group.LiveBytes)
                .ThenByDescending(group => group.LiveCount)
                .ThenBy(group => group.Category, StringComparer.Ordinal)
                .Take(24)
                .ToArray());
    }

    private static NativeAllocationCategorySummary FindNativeCategory(
        IReadOnlyList<NativeAllocationCategorySummary> categories,
        string name)
    {
        for (int index = 0; index < categories.Count; index++)
        {
            if (categories[index].Category.Equals(
                    name,
                    StringComparison.Ordinal))
            {
                return categories[index];
            }
        }

        return NativeAllocationCategorySummary.Empty(name);
    }

    private static long ReadAttributeInt64(
        XElement element,
        string name)
        => long.TryParse(
               element.Attribute(name)?.Value,
               NumberStyles.Integer,
               CultureInfo.InvariantCulture,
               out long value)
            ? value
            : 0;

    private static MetalResourceSummary SummarizeResources(string? path)
    {
        XmlTable? table = XmlTable.TryLoad(path);
        if (table is null)
        {
            return MetalResourceSummary.Empty;
        }

        var deallocations = new Dictionary<long, long?>();
        foreach (XElement row in table.Rows)
        {
            XElement[] fields = row.Elements().ToArray();
            if (fields.Length < 15 ||
                !table.ReadFormatted(fields[13], string.Empty).Equals(
                    "Deallocation",
                    StringComparison.OrdinalIgnoreCase) ||
                table.ReadInt64(fields[4]) is not { } resourceId)
            {
                continue;
            }

            deallocations[resourceId] = table.ReadInt64(fields[1]);
        }

        var groups = new Dictionary<(string Owner, string Type), MutableResourceGroup>();
        var records = new List<MetalResourceRecord>(table.Rows.Count / 2);
        long totalBytes = 0;
        long liveBytes = 0;
        int liveCount = 0;
        foreach (XElement row in table.Rows)
        {
            XElement[] fields = row.Elements().ToArray();
            if (fields.Length < 15 ||
                !table.ReadFormatted(fields[13], string.Empty).Equals(
                    "Allocation",
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            long resourceId = table.ReadInt64(fields[4]) ?? 0;
            long size = table.ReadInt64(fields[9]) ??
                        table.ReadInt64(fields[8]) ??
                        table.ReadInt64(fields[7]) ??
                        0;
            string resourceType =
                table.ReadFormatted(fields[12], "Unknown");
            XElement backtrace = table.Resolve(fields[14]);
            string owner = ClassifyOwner(backtrace);
            string label = table.ReadFormatted(fields[6], "Unlabelled");
            bool liveAtEnd = !deallocations.ContainsKey(resourceId);
            long? lifetime = deallocations.GetValueOrDefault(resourceId);
            checked
            {
                totalBytes += size;
                if (liveAtEnd)
                {
                    liveBytes += size;
                    liveCount++;
                }
            }

            var key = (owner, resourceType);
            if (!groups.TryGetValue(key, out MutableResourceGroup? group))
            {
                group = new MutableResourceGroup(owner, resourceType);
                groups.Add(key, group);
            }
            group.Add(size, liveAtEnd);
            records.Add(
                new MetalResourceRecord(
                    owner,
                    resourceType,
                    label,
                    size,
                    liveAtEnd,
                    table.ReadInt64(fields[0]) ?? 0,
                    lifetime,
                    FindRelevantFrame(backtrace, owner)));
        }

        ResourceGroupSummary[] resourceGroups = groups.Values
            .Select(group => group.ToSummary())
            .OrderByDescending(group => group.TotalBytes)
            .ThenBy(group => group.Owner, StringComparer.Ordinal)
            .ThenBy(group => group.ResourceType, StringComparer.Ordinal)
            .ToArray();
        return new MetalResourceSummary(
            records.Count,
            totalBytes,
            liveCount,
            liveBytes,
            resourceGroups,
            records
                .OrderByDescending(record => record.SizeBytes)
                .ThenBy(record => record.CreationNanoseconds)
                .Take(24)
                .ToArray());
    }

    private static NumericSeriesSummary SummarizeSeries(
        string? path,
        int valueColumn)
    {
        XmlTable? table = XmlTable.TryLoad(path);
        if (table is null)
        {
            return NumericSeriesSummary.Empty;
        }

        long total = 0;
        long maximum = 0;
        long last = 0;
        int count = 0;
        foreach (XElement row in table.Rows)
        {
            XElement[] fields = row.Elements().ToArray();
            if (valueColumn >= fields.Length ||
                table.ReadInt64(fields[valueColumn]) is not { } value)
            {
                continue;
            }

            checked
            {
                total += value;
            }
            maximum = Math.Max(maximum, value);
            last = value;
            count++;
        }
        return new NumericSeriesSummary(count, total, maximum, last);
    }

    private static HangSummary SummarizeHangs(
        string? path,
        string? timeProfilePath)
    {
        XmlTable? table = XmlTable.TryLoad(path);
        if (table is null)
        {
            return HangSummary.Empty;
        }

        var types = new Dictionary<string, MutableHangGroup>(
            StringComparer.Ordinal);
        var intervals = new List<MutableHangInterval>(table.Rows.Count);
        long totalDuration = 0;
        long maximumDuration = 0;
        foreach (XElement row in table.Rows)
        {
            XElement[] fields = row.Elements().ToArray();
            if (fields.Length < 3)
            {
                continue;
            }

            long duration = table.ReadInt64(fields[1]) ?? 0;
            string type = table.ReadFormatted(fields[2], "Unknown");
            long start = table.ReadInt64(fields[0]) ?? 0;
            checked
            {
                totalDuration += duration;
            }
            maximumDuration = Math.Max(maximumDuration, duration);
            if (!types.TryGetValue(type, out MutableHangGroup? group))
            {
                group = new MutableHangGroup(type);
                types.Add(type, group);
            }
            group.Add(duration);
            intervals.Add(
                new MutableHangInterval(type, start, duration));
        }

        AttachTimeProfileSamples(
            intervals,
            XmlTable.TryLoad(timeProfilePath));
        return new HangSummary(
            table.Rows.Count,
            totalDuration,
            maximumDuration,
            types.Values
                .Select(group => group.ToSummary())
                .OrderByDescending(group => group.TotalDurationNanoseconds)
                .ToArray(),
            intervals
                .Select(interval => interval.ToSummary())
                .ToArray());
    }

    private static SpillGroupSummary[] SummarizeSpillGroups(
        string? spillPath,
        string? submissionPath)
    {
        XmlTable? spills = XmlTable.TryLoad(spillPath);
        XmlTable? submissions = XmlTable.TryLoad(submissionPath);
        if (spills is null || submissions is null)
        {
            return [];
        }

        var labels = new Dictionary<long, string>();
        foreach (XElement row in submissions.Rows)
        {
            XElement[] fields = row.Elements().ToArray();
            if (fields.Length < 15 ||
                submissions.ReadInt64(fields[14]) is not { } commandBufferId)
            {
                continue;
            }
            labels[commandBufferId] = ExtractCommandBufferLabel(
                submissions.ReadFormatted(fields[10], "Unlabelled"));
        }

        var groups = new Dictionary<string, MutableSpillGroup>(
            StringComparer.Ordinal);
        foreach (XElement row in spills.Rows)
        {
            XElement[] fields = row.Elements().ToArray();
            if (fields.Length < 4)
            {
                continue;
            }
            long commandBufferId = spills.ReadInt64(fields[1]) ?? 0;
            long bytes = spills.ReadInt64(fields[3]) ?? 0;
            string label = labels.GetValueOrDefault(
                commandBufferId,
                "Unresolved command buffer");
            if (!groups.TryGetValue(label, out MutableSpillGroup? group))
            {
                group = new MutableSpillGroup(label);
                groups.Add(label, group);
            }
            group.Add(bytes);
        }
        return groups.Values
            .Select(group => group.ToSummary())
            .OrderByDescending(group => group.TotalBytes)
            .ThenBy(group => group.CommandBuffer, StringComparer.Ordinal)
            .ToArray();
    }

    private static string ExtractCommandBufferLabel(string narrative)
    {
        int firstQuote = narrative.IndexOf('"');
        if (firstQuote >= 0)
        {
            int secondQuote = narrative.IndexOf('"', firstQuote + 1);
            if (secondQuote > firstQuote)
            {
                return narrative[(firstQuote + 1)..secondQuote].Trim();
            }
        }
        return narrative;
    }

    private static void AttachTimeProfileSamples(
        IReadOnlyList<MutableHangInterval> intervals,
        XmlTable? timeProfile)
    {
        if (timeProfile is null || intervals.Count == 0)
        {
            return;
        }

        foreach (XElement row in timeProfile.Rows)
        {
            XElement[] fields = row.Elements().ToArray();
            if (fields.Length < 7 ||
                !timeProfile.ReadFormatted(fields[1], string.Empty)
                    .Contains(
                        "Main Thread",
                        StringComparison.Ordinal) ||
                timeProfile.ReadInt64(fields[0]) is not { } sampleTime)
            {
                continue;
            }

            MutableHangInterval? interval = intervals.FirstOrDefault(
                candidate =>
                    sampleTime >= candidate.StartNanoseconds &&
                    sampleTime <
                    candidate.StartNanoseconds +
                    candidate.DurationNanoseconds);
            if (interval is null)
            {
                continue;
            }

            long weight = timeProfile.ReadInt64(fields[5]) ?? 0;
            string frame = FindSampleFrame(
                timeProfile,
                timeProfile.Resolve(fields[6]));
            interval.AddSample(frame, weight);
        }
    }

    private static string FindSampleFrame(
        XmlTable table,
        XElement backtrace)
    {
        string? fallback = null;
        foreach (XElement frame in backtrace.Descendants("frame"))
        {
            XElement resolved = table.Resolve(frame);
            string? name = resolved.Attribute("name")?.Value;
            if (string.IsNullOrWhiteSpace(name) ||
                name.StartsWith("0x", StringComparison.Ordinal))
            {
                continue;
            }

            fallback ??= name;
            if (name.Contains("wgpu", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("Shader", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("Pipeline", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("Device", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("glfw", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("NSWindow", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("Monitor", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("JIT_", StringComparison.Ordinal))
            {
                return name;
            }
        }
        return fallback ?? "Unresolved managed frame";
    }

    private static int CountRows(string? path)
        => XmlTable.TryLoad(path)?.Rows.Count ?? 0;

    private static string? FindExport(
        string directory,
        string schema)
    {
        string exactPath = Path.Combine(directory, $"{schema}.xml");
        if (File.Exists(exactPath))
        {
            return exactPath;
        }

        return Directory.EnumerateFiles(
                    directory,
                    $"*-{schema}.xml",
                    SearchOption.TopDirectoryOnly)
                .OrderBy(path => path, StringComparer.Ordinal)
                .FirstOrDefault();
    }

    private static string ClassifyOwner(XElement backtrace)
    {
        string text = backtrace.ToString(SaveOptions.DisableFormatting);
        if (text.Contains("libwgpu_native", StringComparison.Ordinal) ||
            text.Contains("wgpu_core::", StringComparison.Ordinal) ||
            text.Contains("wgpu_hal::", StringComparison.Ordinal))
        {
            return "wgpu-native";
        }
        if (text.Contains("AppKit", StringComparison.Ordinal) ||
            text.Contains("libglfw", StringComparison.Ordinal) ||
            text.Contains("QuartzCore", StringComparison.Ordinal))
        {
            return "window-system";
        }
        if (text.Contains("AGX", StringComparison.Ordinal) ||
            text.Contains("IOGPU", StringComparison.Ordinal) ||
            text.Contains("Metal.framework", StringComparison.Ordinal))
        {
            return "metal-driver";
        }
        return "other";
    }

    private static string FindRelevantFrame(
        XElement backtrace,
        string owner)
    {
        IEnumerable<string> names = backtrace
            .Descendants("frame")
            .Select(frame => frame.Attribute("name")?.Value)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name!);
        string[] markers = owner switch
        {
            "wgpu-native" => ["wgpu", "WGPU"],
            "window-system" => ["NSWindow", "NSCGS", "glfw", "Quartz"],
            "metal-driver" => ["AGX", "IOGPU", "MTL"],
            _ => []
        };
        foreach (string name in names)
        {
            if (markers.Any(
                    marker => name.Contains(
                        marker,
                        StringComparison.OrdinalIgnoreCase)))
            {
                return name;
            }
        }
        return names.FirstOrDefault() ?? "Unavailable";
    }

    private static string FormatMarkdown(InstrumentsCaptureSummary summary)
    {
        var text = new StringBuilder();
        text.AppendLine("# Xcode Instruments compact summary");
        text.AppendLine();
        text.AppendLine(
            $"Generated: {summary.GeneratedUtc:O}");
        text.AppendLine();
        text.AppendLine("| Signal | Count | Total | Maximum | Last/live |");
        text.AppendLine("| --- | ---: | ---: | ---: | ---: |");
        AppendSeries(
            text,
            "Metal current allocated size",
            summary.CurrentAllocatedSize,
            isDuration: false);
        AppendSeries(
            text,
            "Drawable waits",
            summary.DrawableWaits,
            isDuration: true);
        AppendSeries(
            text,
            "Graphics compiler spills",
            summary.CompilerSpills,
            isDuration: false);
        text.AppendLine(
            $"| Potential hangs | {FormatCount(summary.PotentialHangs.Count)} | " +
            $"{FormatDuration(summary.PotentialHangs.TotalDurationNanoseconds)} | " +
            $"{FormatDuration(summary.PotentialHangs.MaximumDurationNanoseconds)} | — |");
        text.AppendLine(
            $"| Hang risks | {FormatCount(summary.HangRiskCount)} | — | — | — |");
        text.AppendLine(
            $"| Command-buffer errors | {FormatCount(summary.CommandBufferErrorCount)} | — | — | — |");
        text.AppendLine();
        text.AppendLine(
            $"Metal submissions: {FormatCount(summary.CommandBufferSubmissionCount)}; " +
            $"completions: {FormatCount(summary.CommandBufferCompletionCount)}.");
        if (summary.CompilerSpillGroups.Count > 0)
        {
            text.AppendLine();
            text.AppendLine("Compiler spills by command buffer:");
            foreach (SpillGroupSummary group in summary.CompilerSpillGroups)
            {
                text.AppendLine(
                    $"- {group.CommandBuffer}: {FormatCount(group.Count)} events, " +
                    $"{FormatBytes(group.TotalBytes)}.");
            }
        }
        if (summary.PotentialHangs.Intervals.Count > 0)
        {
            text.AppendLine();
            text.AppendLine("## Potential-hang intervals");
            text.AppendLine();
            foreach (HangIntervalSummary interval in
                     summary.PotentialHangs.Intervals)
            {
                text.AppendLine(
                    $"- {interval.Type} at " +
                    $"{FormatDuration(interval.StartNanoseconds)} for " +
                    $"{FormatDuration(interval.DurationNanoseconds)}: " +
                    $"{FormatCount(interval.SampleCount)} main-thread samples.");
                foreach (HangFrameSummary frame in interval.TopFrames)
                {
                    text.AppendLine(
                        $"  - {frame.Frame}: {FormatCount(frame.SampleCount)} samples, " +
                        $"{FormatDuration(frame.WeightNanoseconds)} weight.");
                }
            }
        }
        text.AppendLine();
        text.AppendLine("## Native heap and anonymous VM");
        text.AppendLine();
        text.AppendLine(
            "The Allocations instrument reports allocator payload and " +
            "anonymous virtual-memory reservations. Managed-object attribution " +
            "remains the responsibility of the paired .NET EventPipe capture.");
        NativeAllocationCategorySummary? dispatchContinuations =
            summary.NativeAllocations.LargestPersistentCategories.FirstOrDefault(
                category => string.Equals(
                    category.Category,
                    "VM: Dispatch continuations",
                    StringComparison.Ordinal));
        if (dispatchContinuations is not null)
        {
            text.AppendLine(
                $"The {FormatBytes(dispatchContinuations.PersistentBytes)} " +
                "`VM: Dispatch continuations` row is a per-process libdispatch " +
                "virtual-address reservation, not that many resident bytes. " +
                "Use the paired `vmmap` resident and dirty columns before " +
                "attributing it to physical footprint.");
        }
        text.AppendLine();
        text.AppendLine("| Aggregate | Persistent | Total allocated | Transient |");
        text.AppendLine("| --- | ---: | ---: | ---: |");
        text.AppendLine(
            $"| Heap and anonymous VM | " +
            $"{FormatBytes(summary.NativeAllocations.HeapAndAnonymousVmPersistentBytes)} | " +
            $"{FormatBytes(summary.NativeAllocations.HeapAndAnonymousVmTotalBytes)} | " +
            $"{FormatBytes(summary.NativeAllocations.HeapAndAnonymousVmTransientBytes)} |");
        text.AppendLine(
            $"| Heap allocations | " +
            $"{FormatBytes(summary.NativeAllocations.HeapPersistentBytes)} | — | — |");
        text.AppendLine(
            $"| Anonymous VM | " +
            $"{FormatBytes(summary.NativeAllocations.AnonymousVmPersistentBytes)} | — | — |");
        text.AppendLine(
            $"| All VM regions | " +
            $"{FormatBytes(summary.NativeAllocations.AllVmRegionsPersistentBytes)} | — | — |");
        text.AppendLine();
        text.AppendLine("### Largest persistent native/VM categories");
        text.AppendLine();
        text.AppendLine("| Category | Persistent | Count | Total allocated |");
        text.AppendLine("| --- | ---: | ---: | ---: |");
        foreach (NativeAllocationCategorySummary category in
                 summary.NativeAllocations.LargestPersistentCategories)
        {
            text.AppendLine(
                $"| {EscapeTable(category.Category)} | " +
                $"{FormatBytes(category.PersistentBytes)} | " +
                $"{FormatCount(category.PersistentCount)} | " +
                $"{FormatBytes(category.TotalBytes)} |");
        }
        if (summary.NativeAllocations.Details.LiveCount > 0)
        {
            text.AppendLine();
            text.AppendLine("### Largest attributed live native/VM groups");
            text.AppendLine();
            text.AppendLine(
                $"The opt-in allocation list attributed " +
                $"{FormatCount(summary.NativeAllocations.Details.LiveCount)} live rows " +
                $"totaling {FormatBytes(summary.NativeAllocations.Details.LiveBytes)}.");
            text.AppendLine();
            text.AppendLine(
                "| Category | Caller | Library | Live count | Live bytes | First | Last |");
            text.AppendLine(
                "| --- | --- | --- | ---: | ---: | --- | --- |");
            foreach (NativeAllocationDetailGroup group in
                     summary.NativeAllocations.Details.LargestLiveGroups)
            {
                text.AppendLine(
                    $"| {EscapeTable(group.Category)} | " +
                    $"{EscapeTable(group.ResponsibleCaller)} | " +
                    $"{EscapeTable(group.ResponsibleLibrary)} | " +
                    $"{FormatCount(group.LiveCount)} | " +
                    $"{FormatBytes(group.LiveBytes)} | " +
                    $"{EscapeTable(group.FirstTimestamp)} | " +
                    $"{EscapeTable(group.LastTimestamp)} |");
            }
        }
        text.AppendLine();
        text.AppendLine("## Metal resource allocations");
        text.AppendLine();
        text.AppendLine(
            $"Observed {FormatCount(summary.Resources.Count)} resources totaling " +
            $"{FormatBytes(summary.Resources.TotalBytes)} across the capture. " +
            $"{FormatCount(summary.Resources.LiveAtCaptureEndCount)} resources totaling " +
            $"{FormatBytes(summary.Resources.LiveAtCaptureEndBytes)} had no " +
            "recorded deallocation before capture end.");
        text.AppendLine();
        text.AppendLine("| Owner | Type | Count | Bytes | Live count | Live bytes |");
        text.AppendLine("| --- | --- | ---: | ---: | ---: | ---: |");
        foreach (ResourceGroupSummary group in summary.Resources.Groups)
        {
            text.AppendLine(
                $"| {group.Owner} | {group.ResourceType} | {FormatCount(group.Count)} | " +
                $"{FormatBytes(group.TotalBytes)} | {FormatCount(group.LiveAtCaptureEndCount)} | " +
                $"{FormatBytes(group.LiveAtCaptureEndBytes)} |");
        }
        text.AppendLine();
        text.AppendLine("### Largest observed resources");
        text.AppendLine();
        text.AppendLine("| Owner | Type | Size | Live at end | Relevant frame |");
        text.AppendLine("| --- | --- | ---: | --- | --- |");
        foreach (MetalResourceRecord resource in summary.Resources.LargestResources)
        {
            text.AppendLine(
                $"| {resource.Owner} | {resource.ResourceType} | " +
                $"{FormatBytes(resource.SizeBytes)} | " +
                $"{(resource.LiveAtCaptureEnd ? "yes" : "no")} | " +
                $"{EscapeTable(resource.RelevantFrame)} |");
        }
        return text.ToString();
    }

    private static string EscapeTable(string value)
        => value.Replace("|", "\\|", StringComparison.Ordinal);

    private static void AppendSeries(
        StringBuilder text,
        string name,
        NumericSeriesSummary series,
        bool isDuration)
    {
        string Format(long value) =>
            isDuration ? FormatDuration(value) : FormatBytes(value);
        text.AppendLine(
            $"| {name} | {FormatCount(series.Count)} | {Format(series.Total)} | " +
            $"{Format(series.Maximum)} | {Format(series.Last)} |");
    }

    private static string FormatBytes(long value)
        => value.ToString("N0", CultureInfo.InvariantCulture) + " B";

    private static string FormatCount(long value)
        => value.ToString("N0", CultureInfo.InvariantCulture);

    private static string FormatDuration(long nanoseconds)
        => (nanoseconds / 1_000_000d)
            .ToString("N3", CultureInfo.InvariantCulture) + " ms";

    private sealed class XmlTable
    {
        private readonly Dictionary<string, XElement> _identities;

        private XmlTable(XDocument document)
        {
            Rows = document.Descendants("row").ToArray();
            _identities = document
                .Descendants()
                .Where(element => element.Attribute("id") is not null)
                .ToDictionary(
                    element => element.Attribute("id")!.Value,
                    StringComparer.Ordinal);
        }

        public IReadOnlyList<XElement> Rows { get; }

        public static XmlTable? TryLoad(string? path)
            => path is null || !File.Exists(path)
                ? null
                : new XmlTable(XDocument.Load(path));

        public XElement Resolve(XElement element)
        {
            var visited = new HashSet<string>(StringComparer.Ordinal);
            while (element.Attribute("ref") is { } reference &&
                   visited.Add(reference.Value) &&
                   _identities.TryGetValue(
                       reference.Value,
                       out XElement? resolved))
            {
                element = resolved;
            }
            return element;
        }

        public long? ReadInt64(XElement element)
        {
            XElement resolved = Resolve(element);
            return long.TryParse(
                resolved.Value.Trim(),
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out long value)
                ? value
                : null;
        }

        public string ReadFormatted(
            XElement element,
            string fallback)
        {
            XElement resolved = Resolve(element);
            string? formatted = resolved.Attribute("fmt")?.Value;
            if (!string.IsNullOrWhiteSpace(formatted))
            {
                return formatted;
            }
            string value = resolved.Value.Trim();
            return string.IsNullOrWhiteSpace(value) ? fallback : value;
        }
    }

    private sealed class MutableResourceGroup(
        string owner,
        string resourceType)
    {
        private int _count;
        private long _totalBytes;
        private int _liveCount;
        private long _liveBytes;

        public void Add(long bytes, bool liveAtEnd)
        {
            _count++;
            checked
            {
                _totalBytes += bytes;
            }
            if (liveAtEnd)
            {
                _liveCount++;
                checked
                {
                    _liveBytes += bytes;
                }
            }
        }

        public ResourceGroupSummary ToSummary()
            => new(
                owner,
                resourceType,
                _count,
                _totalBytes,
                _liveCount,
                _liveBytes);
    }

    private sealed class MutableHangGroup(string type)
    {
        private int _count;
        private long _total;
        private long _maximum;

        public void Add(long duration)
        {
            _count++;
            checked
            {
                _total += duration;
            }
            _maximum = Math.Max(_maximum, duration);
        }

        public HangGroupSummary ToSummary()
            => new(type, _count, _total, _maximum);
    }

    private sealed class MutableHangInterval(
        string type,
        long startNanoseconds,
        long durationNanoseconds)
    {
        private readonly Dictionary<string, MutableHangFrame> _frames =
            new(StringComparer.Ordinal);
        private int _sampleCount;

        public long StartNanoseconds { get; } = startNanoseconds;
        public long DurationNanoseconds { get; } = durationNanoseconds;

        public void AddSample(string frame, long weight)
        {
            _sampleCount++;
            if (!_frames.TryGetValue(frame, out MutableHangFrame? value))
            {
                value = new MutableHangFrame(frame);
                _frames.Add(frame, value);
            }
            value.Add(weight);
        }

        public HangIntervalSummary ToSummary()
            => new(
                type,
                StartNanoseconds,
                DurationNanoseconds,
                _sampleCount,
                _frames.Values
                    .Select(frame => frame.ToSummary())
                    .OrderByDescending(frame => frame.WeightNanoseconds)
                    .ThenByDescending(frame => frame.SampleCount)
                    .Take(8)
                    .ToArray());
    }

    private sealed class MutableHangFrame(string frame)
    {
        private int _sampleCount;
        private long _weight;

        public void Add(long weight)
        {
            _sampleCount++;
            checked
            {
                _weight += weight;
            }
        }

        public HangFrameSummary ToSummary()
            => new(frame, _sampleCount, _weight);
    }

    private sealed class MutableSpillGroup(string commandBuffer)
    {
        private int _count;
        private long _bytes;

        public void Add(long bytes)
        {
            _count++;
            checked
            {
                _bytes += bytes;
            }
        }

        public SpillGroupSummary ToSummary()
            => new(commandBuffer, _count, _bytes);
    }

    private sealed class MutableNativeAllocationDetail(
        string category,
        string responsibleCaller,
        string responsibleLibrary)
    {
        private long _liveCount;
        private long _liveBytes;
        private string _firstTimestamp = string.Empty;
        private string _lastTimestamp = string.Empty;

        public void Add(long bytes, string timestamp)
        {
            checked
            {
                _liveCount++;
                _liveBytes += bytes;
            }

            if (!string.IsNullOrWhiteSpace(timestamp))
            {
                if (_firstTimestamp.Length == 0 ||
                    string.CompareOrdinal(timestamp, _firstTimestamp) < 0)
                {
                    _firstTimestamp = timestamp;
                }
                if (_lastTimestamp.Length == 0 ||
                    string.CompareOrdinal(timestamp, _lastTimestamp) > 0)
                {
                    _lastTimestamp = timestamp;
                }
            }
        }

        public NativeAllocationDetailGroup ToSummary() =>
            new(
                category,
                responsibleCaller,
                responsibleLibrary,
                _liveCount,
                _liveBytes,
                _firstTimestamp,
                _lastTimestamp);
    }
}

internal sealed record InstrumentsCaptureSummary(
    int SchemaVersion,
    DateTimeOffset GeneratedUtc,
    MetalResourceSummary Resources,
    NativeAllocationSummary NativeAllocations,
    NumericSeriesSummary CurrentAllocatedSize,
    NumericSeriesSummary DrawableWaits,
    NumericSeriesSummary CompilerSpills,
    IReadOnlyList<SpillGroupSummary> CompilerSpillGroups,
    HangSummary PotentialHangs,
    int HangRiskCount,
    int CommandBufferErrorCount,
    int CommandBufferSubmissionCount,
    int CommandBufferCompletionCount);

internal sealed record NativeAllocationSummary(
    long HeapAndAnonymousVmPersistentBytes,
    long HeapPersistentBytes,
    long AnonymousVmPersistentBytes,
    long AllVmRegionsPersistentBytes,
    long HeapAndAnonymousVmTotalBytes,
    long HeapAndAnonymousVmTransientBytes,
    IReadOnlyList<NativeAllocationCategorySummary>
        LargestPersistentCategories,
    NativeAllocationDetailSummary Details)
{
    public static NativeAllocationSummary Empty { get; } =
        new(0, 0, 0, 0, 0, 0, [], NativeAllocationDetailSummary.Empty);
}

internal sealed record NativeAllocationDetailSummary(
    long LiveCount,
    long LiveBytes,
    IReadOnlyList<NativeAllocationDetailGroup> LargestLiveGroups)
{
    public static NativeAllocationDetailSummary Empty { get; } =
        new(0, 0, []);
}

internal sealed record NativeAllocationDetailGroup(
    string Category,
    string ResponsibleCaller,
    string ResponsibleLibrary,
    long LiveCount,
    long LiveBytes,
    string FirstTimestamp,
    string LastTimestamp);

internal sealed record NativeAllocationCategorySummary(
    string Category,
    long PersistentBytes,
    long TotalBytes,
    long TransientBytes,
    long PersistentCount,
    long TotalCount)
{
    public static NativeAllocationCategorySummary Empty(string category) =>
        new(category, 0, 0, 0, 0, 0);
}

internal sealed record MetalResourceSummary(
    int Count,
    long TotalBytes,
    int LiveAtCaptureEndCount,
    long LiveAtCaptureEndBytes,
    IReadOnlyList<ResourceGroupSummary> Groups,
    IReadOnlyList<MetalResourceRecord> LargestResources)
{
    public static MetalResourceSummary Empty { get; } =
        new(0, 0, 0, 0, [], []);
}

internal sealed record ResourceGroupSummary(
    string Owner,
    string ResourceType,
    int Count,
    long TotalBytes,
    int LiveAtCaptureEndCount,
    long LiveAtCaptureEndBytes);

internal sealed record MetalResourceRecord(
    string Owner,
    string ResourceType,
    string Label,
    long SizeBytes,
    bool LiveAtCaptureEnd,
    long CreationNanoseconds,
    long? LifetimeNanoseconds,
    string RelevantFrame);

internal sealed record NumericSeriesSummary(
    int Count,
    long Total,
    long Maximum,
    long Last)
{
    public static NumericSeriesSummary Empty { get; } =
        new(0, 0, 0, 0);
}

internal sealed record HangSummary(
    int Count,
    long TotalDurationNanoseconds,
    long MaximumDurationNanoseconds,
    IReadOnlyList<HangGroupSummary> Groups,
    IReadOnlyList<HangIntervalSummary> Intervals)
{
    public static HangSummary Empty { get; } =
        new(0, 0, 0, [], []);
}

internal sealed record HangGroupSummary(
    string Type,
    int Count,
    long TotalDurationNanoseconds,
    long MaximumDurationNanoseconds);

internal sealed record HangIntervalSummary(
    string Type,
    long StartNanoseconds,
    long DurationNanoseconds,
    int SampleCount,
    IReadOnlyList<HangFrameSummary> TopFrames);

internal sealed record HangFrameSummary(
    string Frame,
    int SampleCount,
    long WeightNanoseconds);

internal sealed record SpillGroupSummary(
    string CommandBuffer,
    int Count,
    long TotalBytes);
