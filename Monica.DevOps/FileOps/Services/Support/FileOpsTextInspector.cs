using System.Text;
using Monica.DevOps.FileOps.Exceptions;
using Monica.DevOps.FileOps.Models;
using Monica.Tool.Text;

namespace Monica.DevOps.FileOps.Services.Support;

public sealed class FileOpsTextReadResult
{
    public string Content { get; init; } = string.Empty;

    public string EncodingName { get; init; } = "utf-8";

    public int LineCount { get; init; }
}

public class FileOpsTextInspector
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private const int SAMPLE_SIZE = 4096;

    internal FileOpsTextPreviewKind Classify(string fullPath, long size, long maxBytes)
    {
        if (size == 0) return FileOpsTextPreviewKind.Text;

        try
        {
            using var stream = File.Open(fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            Span<byte> sample = stackalloc byte[SAMPLE_SIZE];
            var count = stream.ReadAtLeast(sample, SAMPLE_SIZE, false);
            if (LooksBinary(sample[..count])) return FileOpsTextPreviewKind.Binary;
            return size > maxBytes ? FileOpsTextPreviewKind.Oversized : FileOpsTextPreviewKind.Text;
        }
        catch (IOException)
        {
            return FileOpsTextPreviewKind.Unavailable;
        }
        catch (UnauthorizedAccessException)
        {
            return FileOpsTextPreviewKind.Unavailable;
        }
    }

    public async Task<FileOpsTextReadResult> ReadAsync(string fullPath, long maxBytes, CancellationToken cancellationToken = default)
    {
        var fileInfo = new FileInfo(fullPath);
        if (fileInfo.Length > maxBytes)
        {
            throw FileOpsOperationException.TextFileTooLarge(fullPath, maxBytes.FormatByteSize());
        }

        var buffer = new byte[fileInfo.Length];
        await using var stream = File.OpenRead(fullPath);
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset, buffer.Length - offset), cancellationToken);
            if (read == 0)
            {
                break;
            }

            offset += read;
        }

        if (LooksBinary(buffer.AsSpan(0, Math.Min(offset, SAMPLE_SIZE))))
        {
            throw FileOpsOperationException.BinaryFilePreviewUnsupported(fullPath);
        }

        var encoding = ResolveDetectedEncoding(buffer.AsSpan(0, offset), out var preambleLength);
        var content = encoding.GetString(buffer, preambleLength, offset - preambleLength);
        return new FileOpsTextReadResult
        {
            Content = content,
            EncodingName = encoding.WebName,
            LineCount = string.IsNullOrEmpty(content) ? 0 : content.Count(static ch => ch == '\n') + 1
        };
    }

    public Encoding ResolveEncoding(string? encodingName)
    {
        if (string.IsNullOrWhiteSpace(encodingName))
        {
            return Utf8NoBom;
        }

        try
        {
            return Encoding.GetEncoding(encodingName);
        }
        catch
        {
            return Utf8NoBom;
        }
    }

    private static bool LooksBinary(ReadOnlySpan<byte> buffer)
    {
        ResolveDetectedEncoding(buffer, out var preambleLength);
        if (preambleLength == 2) return false;
        buffer = buffer[preambleLength..];

        var suspiciousBytes = 0;
        foreach (var value in buffer)
        {
            if (value == 0)
            {
                return true;
            }

            if (value < 8 || (value > 13 && value < 32))
            {
                suspiciousBytes++;
            }
        }

        return suspiciousBytes > buffer.Length / 20;
    }

    private static Encoding ResolveDetectedEncoding(ReadOnlySpan<byte> buffer, out int preambleLength)
    {
        if (buffer.Length >= 3 &&
            buffer[0] == 0xEF &&
            buffer[1] == 0xBB &&
            buffer[2] == 0xBF)
        {
            preambleLength = 3;
            return Encoding.UTF8;
        }

        if (buffer.Length >= 2 && buffer[0] == 0xFF && buffer[1] == 0xFE)
        {
            preambleLength = 2;
            return Encoding.Unicode;
        }

        if (buffer.Length >= 2 && buffer[0] == 0xFE && buffer[1] == 0xFF)
        {
            preambleLength = 2;
            return Encoding.BigEndianUnicode;
        }

        preambleLength = 0;
        return Utf8NoBom;
    }
}
