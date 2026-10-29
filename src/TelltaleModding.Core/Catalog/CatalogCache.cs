using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using TelltaleModding.Core.Archives;

namespace TelltaleModding.Core.Catalog;

/// <summary>
/// The built index of one install, as stored on disk: archive priorities, the winning
/// entry for every file name, and the characters. Saved as a small Brotli-compressed
/// binary file per install (entry names front-coded) (<c>&lt;sha of archives folder&gt;.catalog</c>), tagged with a
/// fingerprint of every archive's and description's name, size and modification time so
/// any game update or mod change rebuilds it.
/// </summary>
internal sealed class CatalogCache
{
    /// <summary>Bump when the build logic or file layout changes.</summary>
    public const int FormatVersion = 1;

    private const uint Magic = 0x43534D54; // "TMSC"

    /// <summary>Archives with their resolved priorities.</summary>
    public required IReadOnlyList<CatalogArchive> Archives { get; init; }

    /// <summary>Winning entry per file name, sorted by name (ordinal, case-insensitive).</summary>
    public required IReadOnlyList<ArchiveEntryRef> Entries { get; init; }

    /// <summary>Characters (their parts and skeletons reference <see cref="Entries"/>).</summary>
    public required IReadOnlyList<CharacterEntry> Characters { get; init; }

    /// <summary>The cache file for an install inside <paramref name="cacheFolder"/>.</summary>
    public static string PathFor(GameInstall install, string cacheFolder)
    {
        var key = $"{install.Game.GameNum}|{Path.GetFullPath(install.ArchivesFolder).ToLowerInvariant()}";
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..24].ToLowerInvariant();
        return Path.Combine(cacheFolder, hash + ".catalog");
    }

    /// <summary>
    /// Fingerprint of everything the index depends on: format version, game, language,
    /// and each archive's and resource description's file name, size and last-write time.
    /// </summary>
    public static string ComputeFingerprint(GameInstall install, string language)
    {
        var sb = new StringBuilder();
        sb.Append(FormatVersion).Append('|').Append(install.Game.GameNum).Append('|').Append(language).Append('\n');
        foreach (var path in install.ArchivePaths.Concat(ResourceDescriptionLoader.FindDescriptionFiles(install.ArchivesFolder)))
        {
            var info = new FileInfo(path);
            sb.Append(info.Name.ToLowerInvariant()).Append('|');
            if (info.Exists)
                sb.Append(info.Length).Append('|').Append(info.LastWriteTimeUtc.Ticks);
            sb.Append('\n');
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())));
    }

    /// <summary>Writes the cache atomically (temp file + move).</summary>
    public void Save(string path, string fingerprint)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var file = File.Create(temp))
            using (var brotli = new BrotliStream(file, CompressionLevel.Fastest))
            using (var buffered = new BufferedStream(brotli, 1 << 16))
            using (var w = new BinaryWriter(buffered, Encoding.UTF8))
                Write(w, fingerprint);
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    /// <summary>Loads the cache if it exists and matches <paramref name="fingerprint"/>; null otherwise.</summary>
    public static CatalogCache? TryLoad(string path, string fingerprint)
    {
        if (!File.Exists(path)) return null;
        try
        {
            using var file = File.OpenRead(path);
            using var brotli = new BrotliStream(file, CompressionMode.Decompress);
            using var r = new BinaryReader(new BufferedStream(brotli, 1 << 16), Encoding.UTF8);
            return Read(r, fingerprint);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or EndOfStreamException or
                                       ArgumentException or IndexOutOfRangeException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private void Write(BinaryWriter w, string fingerprint)
    {
        w.Write(Magic);
        w.Write(FormatVersion);
        w.Write(fingerprint);

        w.Write(Archives.Count);
        var archiveIndex = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var a in Archives)
        {
            archiveIndex[a.Path] = archiveIndex.Count;
            w.Write(a.Path);
            w.Write(a.Priority);
            w.Write(a.SetName ?? "");
            w.Write(a.Enabled);
            w.Write(a.FromDescription);
        }

        w.Write(Entries.Count);
        var entryIndex = new Dictionary<ArchiveEntryRef, int>(Entries.Count);
        // Names are sorted, so store each as (length shared with the previous name, rest).
        string previous = "";
        foreach (var e in Entries)
        {
            entryIndex[e] = entryIndex.Count;
            int shared = 0;
            int max = Math.Min(previous.Length, e.Name.Length);
            while (shared < max && previous[shared] == e.Name[shared]) shared++;
            w.Write7BitEncodedInt(archiveIndex[e.ArchivePath]);
            w.Write7BitEncodedInt(shared);
            w.Write(e.Name[shared..]);
            w.Write7BitEncodedInt64(e.Size);
            previous = e.Name;
        }

        int IndexOf(ArchiveEntryRef e) =>
            entryIndex.TryGetValue(e, out var i) ? i : throw new InvalidOperationException($"{e.Name} is not in the index.");

        w.Write(Characters.Count);
        foreach (var c in Characters)
        {
            w.Write(c.Id);
            w.Write(c.DisplayName);
            w.Write(c.Variant is not null);
            if (c.Variant is not null) w.Write(c.Variant);
            w.Write(c.Skeleton is null ? -1 : IndexOf(c.Skeleton));
            w.Write(c.DefaultsFromGameData);
            w.Write(c.Parts.Count);
            foreach (var p in c.Parts)
            {
                w.Write(IndexOf(p.Mesh));
                w.Write(p.PartName);
                w.Write(p.Slot);
                w.Write(p.DefaultOn);
            }
        }
    }

    private static CatalogCache? Read(BinaryReader r, string fingerprint)
    {
        if (r.ReadUInt32() != Magic || r.ReadInt32() != FormatVersion || r.ReadString() != fingerprint)
            return null;

        int archiveCount = r.ReadInt32();
        var archives = new CatalogArchive[archiveCount];
        for (int i = 0; i < archiveCount; i++)
        {
            var path = r.ReadString();
            int priority = r.ReadInt32();
            var set = r.ReadString();
            bool enabled = r.ReadBoolean();
            bool fromDescription = r.ReadBoolean();
            archives[i] = new CatalogArchive(path, priority, set.Length == 0 ? null : set, enabled, fromDescription);
        }

        int entryCount = r.ReadInt32();
        var entries = new ArchiveEntryRef[entryCount];
        string previous = "";
        for (int i = 0; i < entryCount; i++)
        {
            int archive = r.Read7BitEncodedInt();
            int shared = r.Read7BitEncodedInt();
            var name = string.Concat(previous.AsSpan(0, shared), r.ReadString());
            entries[i] = new ArchiveEntryRef(archives[archive].Path, name, r.Read7BitEncodedInt64());
            previous = name;
        }

        int characterCount = r.ReadInt32();
        var characters = new CharacterEntry[characterCount];
        for (int i = 0; i < characterCount; i++)
        {
            var id = r.ReadString();
            var display = r.ReadString();
            string? variant = r.ReadBoolean() ? r.ReadString() : null;
            int skeleton = r.ReadInt32();
            bool fromGame = r.ReadBoolean();
            int partCount = r.ReadInt32();
            var parts = new CharacterPart[partCount];
            for (int p = 0; p < partCount; p++)
                parts[p] = new CharacterPart(entries[r.ReadInt32()], r.ReadString(), r.ReadString(), r.ReadBoolean());
            characters[i] = new CharacterEntry(id, display, variant, skeleton < 0 ? null : entries[skeleton], parts)
            {
                DefaultsFromGameData = fromGame,
            };
        }

        return new CatalogCache { Archives = archives, Entries = entries, Characters = characters };
    }
}
