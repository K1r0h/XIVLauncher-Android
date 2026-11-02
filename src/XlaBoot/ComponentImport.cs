using System;
using System.IO;
using System.IO.Compression;
using System.Linq;

namespace XlaBoot;

public static class ComponentImporter
{
    private static string DxvkRoot(string filesDir) => Path.Combine(filesDir, "dxvk-import");
    private static string SelectedFile(string filesDir) => Path.Combine(DxvkRoot(filesDir), "selected.txt");

    public static void ImportDxvk(string archivePath, string filesDir)
    {
    var root = DxvkRoot(filesDir);
    Directory.CreateDirectory(root);

    var version = Path.GetFileNameWithoutExtension(archivePath);
    if (version.EndsWith(".tar", StringComparison.OrdinalIgnoreCase))
        version = Path.GetFileNameWithoutExtension(version);
    var dest = Path.Combine(root, version);
    if (Directory.Exists(dest)) Directory.Delete(dest, true);
    Directory.CreateDirectory(dest);

    byte[] header = new byte[2];
    using (var fs = File.OpenRead(archivePath))
        fs.Read(header, 0, 2);

    bool isGzip = header[0] == 0x1f && header[1] == 0x8b;

    if (!isGzip)
    {
        using var archive = ZipFile.OpenRead(archivePath);
        foreach (var entry in archive.Entries)
            ExtractDllEntry(entry, dest);
    }
    else
    {
        using var stream = File.OpenRead(archivePath);
        using var gzip = new System.IO.Compression.GZipStream(stream, System.IO.Compression.CompressionMode.Decompress);
        ExtractTar(gzip, dest);
    }

    File.WriteAllText(SelectedFile(filesDir), version);
    }

    private static void ExtractDllEntry(ZipArchiveEntry entry, string destRoot)
    {
        var name = entry.FullName.Replace('\\', '/');
if (!name.StartsWith("x32/") && !name.StartsWith("x64/"))
            return;
        if (!name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            return;

        var subdir = name.StartsWith("x32/") ? "x32" : "x64";
        var destDir = Path.Combine(destRoot, subdir);
        Directory.CreateDirectory(destDir);
        var destPath = Path.Combine(destDir, Path.GetFileName(name));
        entry.ExtractToFile(destPath, overwrite: true);
    }

    private static void ExtractTar(Stream tarStream, string destRoot)
    {
        int extracted = 0, skipped = 0;
        var buffer = new byte[8192];
        while (true)
        {
            int read = ReadExact(tarStream, buffer, 0, 512);
            if (read == 0) break;

            var name = System.Text.Encoding.ASCII.GetString(buffer, 0, 100).TrimEnd('\0');
            if (string.IsNullOrEmpty(name)) continue;

            var type = System.Text.Encoding.ASCII.GetString(buffer, 156, 12).TrimEnd('\0');
            long size = ParseOctal(buffer, 124, 12);

            bool isDll = name.Replace('\\', '/').EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
                      && !type.Equals("5", StringComparison.Ordinal);
            bool isSymlink = type.Equals("2", StringComparison.Ordinal) || type.Equals("l", StringComparison.Ordinal);
            long dataRemaining = size;

            if ((isDll && size > 0) || (isSymlink && size == 0))
            {
                var relative = name.Replace('\\', '/').TrimStart('/');
                var parts = relative.Split('/');
                int idx = Array.FindLastIndex(parts, p => p == "x32" || p == "x64");
                if (idx >= 0 && idx < parts.Length - 1)
                {
                    var subdir = parts[idx];
                    var fileName = parts[^1];
                    var destDir = Path.Combine(destRoot, subdir);
                    Directory.CreateDirectory(destDir);
                    var destPath = Path.Combine(destDir, fileName);
                    using var file = File.Create(destPath);
                    while (dataRemaining > 0)
                    {
                        int toRead = (int)Math.Min(dataRemaining, buffer.Length);
                        read = tarStream.Read(buffer, 0, toRead);
                        if (read <= 0) break;
                        file.Write(buffer, 0, read);
                        dataRemaining -= read;
                    }
                    extracted++;
                }
                else
                {
                    Skip(tarStream, dataRemaining);
                    skipped++;
                }
            }
            else
            {
                Skip(tarStream, dataRemaining);
                skipped++;
            }

            long pad = (512 - (size % 512)) % 512;
            if (pad > 0) Skip(tarStream, pad);
        }
    }

    private static int ReadExact(Stream stream, byte[] buffer, int offset, int count)
    {
        int total = 0;
        while (total < count)
        {
            int read = stream.Read(buffer, offset + total, count - total);
            if (read <= 0) break;
            total += read;
        }
        return total;
    }

    private static long ParseOctal(byte[] buffer, int offset, int count)
    {
        var s = System.Text.Encoding.ASCII.GetString(buffer, offset, count).TrimEnd('\0', ' ');
        if (string.IsNullOrEmpty(s)) return 0;
        return Convert.ToInt64(s, 8);
    }
    private static void Skip(this Stream stream, long bytes)
    {
        while (bytes > 0)
        {
            int read = stream.Read(new byte[Math.Min((int)bytes, 8192)], 0, (int)Math.Min(bytes, 8192));
            if (read <= 0) break;
            bytes -= read;
        }
    }

    public static string[] DxvkVersions(string filesDir)
    {
        var root = DxvkRoot(filesDir);
        if (!Directory.Exists(root)) return Array.Empty<string>();
        return Directory.GetDirectories(root)
            .Select(Path.GetFileName)
            .OrderBy(n => n)
            .ToArray();
    }

    public static string SelectedVersion(string filesDir)
    {
        var sel = SelectedFile(filesDir);
        if (File.Exists(sel))
        {
            var v = File.ReadAllText(sel).Trim();
            if (Directory.Exists(Path.Combine(DxvkRoot(filesDir), v)))
                return v;
        }
        return "stock";
    }

    public static void SelectVersion(string filesDir, string version)
    {
        File.WriteAllText(SelectedFile(filesDir), version);
    }
}