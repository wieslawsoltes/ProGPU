namespace Windows.ApplicationModel.DataTransfer;

[Flags]
public enum DataPackageOperation : uint
{
    None = 0,
    Copy = 1,
    Move = 2,
    Link = 4
}

public static class StandardDataFormats
{
    public static string Text => "Text";
    public static string Bitmap => "Bitmap";
    public static string StorageItems =>
        "StorageItems";
    public static string Html => "Html";
    public static string Rtf => "Rtf";
    public static string Uri => "Uri";
    public static string WebLink => "WebLink";
    public static string ApplicationLink =>
        "ApplicationLink";
}

public class DataPackage
{
    private readonly System.Collections.Concurrent
        .ConcurrentDictionary<string, object>
        _properties =
            new(StringComparer.OrdinalIgnoreCase);
    private DataPackageView? _view;

    public void SetText(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        SetData(StandardDataFormats.Text, value);
    }

    public void SetData(
        string formatId,
        object value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(
            formatId);
        ArgumentNullException.ThrowIfNull(value);
        _properties[formatId] = value;
    }

    public DataPackageView GetView()
    {
        DataPackageView? current =
            Volatile.Read(ref _view);
        if (current is not null)
            return current;

        var created = new DataPackageView(this);
        return Interlocked.CompareExchange(
                ref _view,
                created,
                null) ??
            created;
    }

    internal bool Contains(string formatId) =>
        _properties.ContainsKey(formatId);

    internal object? GetData(string formatId) =>
        _properties.TryGetValue(
            formatId,
            out object? value)
            ? value
            : null;

    internal string[] GetAvailableFormats() =>
        _properties.Keys.ToArray();
}

public sealed class DataPackageView
{
    private readonly DataPackage _package;

    internal DataPackageView(
        DataPackage package)
    {
        _package = package;
    }

    public IReadOnlyList<string>
        AvailableFormats =>
        _package.GetAvailableFormats();

    public bool Contains(string formatId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(
            formatId);
        return _package.Contains(formatId);
    }

    public Task<object?> GetDataAsync(
        string formatId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(
            formatId);
        return Task.FromResult(
            _package.GetData(formatId));
    }

    public Task<string> GetTextAsync()
    {
        return Task.FromResult(
            _package.GetData(
                StandardDataFormats.Text)
            as string ?? string.Empty);
    }
}
