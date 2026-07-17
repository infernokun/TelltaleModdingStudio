namespace TelltaleModding.Core.Tests;

/// <summary>A throwaway temp file that deletes itself on dispose.</summary>
internal sealed class TempFile : IDisposable
{
    public string Path { get; }

    public TempFile(byte[]? content = null)
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ttmod-test-" + Guid.NewGuid().ToString("N"));
        File.WriteAllBytes(Path, content ?? Array.Empty<byte>());
    }

    public void Dispose()
    {
        try { File.Delete(Path); } catch { /* ignore */ }
    }
}

/// <summary>A throwaway temp directory that recursively deletes itself on dispose.</summary>
internal sealed class TempDir : IDisposable
{
    public string Path { get; }

    public TempDir()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ttmod-testdir-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); } catch { /* ignore */ }
    }
}
