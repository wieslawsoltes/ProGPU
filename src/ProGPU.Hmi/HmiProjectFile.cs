using System.Text;

namespace ProGPU.Hmi;

/// <summary>Desktop persistence with a same-directory temporary file and replace-after-validation.</summary>
public static class HmiProjectFile
{
    public static async Task<HmiProject> LoadAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous);
        if (stream.Length > HmiProjectSerializer.MaximumDocumentBytes) throw new InvalidDataException("HMI document exceeds 8 MiB.");
        using var memory = new MemoryStream();
        var buffer = new byte[8192];
        int read;
        while ((read = await stream.ReadAsync(buffer, cancellationToken)) != 0)
        {
            if (memory.Length + read > HmiProjectSerializer.MaximumDocumentBytes) throw new InvalidDataException("HMI document exceeds 8 MiB.");
            memory.Write(buffer, 0, read);
        }
        return HmiProjectSerializer.Deserialize(new UTF8Encoding(false, true).GetString(memory.ToArray()));
    }
    public static async Task SaveAsync(string path, HmiProject project, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string json = HmiProjectSerializer.Serialize(project);
        string fullPath = Path.GetFullPath(path);
        string temporary = Path.Combine(Path.GetDirectoryName(fullPath)!, $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await File.WriteAllTextAsync(temporary, json, new UTF8Encoding(false), cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, fullPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}
