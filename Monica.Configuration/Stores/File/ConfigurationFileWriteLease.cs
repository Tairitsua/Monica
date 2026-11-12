using Monica.Configuration.Exceptions;

namespace Monica.Configuration.Stores.File;

/// <summary>Serializes native writers across store instances and processes sharing the same directory.</summary>
internal static class ConfigurationFileWriteLease
{
    internal const string FileName = ".writer.lock";

    internal static async Task<FileStream> AcquireAsync(string rootDirectory, bool requireExistingDirectory,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var root = Path.GetFullPath(rootDirectory);
        if (requireExistingDirectory && !Directory.Exists(root))
            throw new ConfigurationConcurrencyConflictException("The existing managed store disappeared before persistence.");
        if (!requireExistingDirectory) Directory.CreateDirectory(root);
        var path = Path.Combine(root, FileName);
        var delayMilliseconds = 25;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                // Keep this metadata-only file after release: deleting it permits two processes to lock
                // different file identities for the same path. The exclusive handle owns the lease.
                return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException exception) when (IsContention(exception))
            {
                await Task.Delay(delayMilliseconds, cancellationToken);
                delayMilliseconds = Math.Min(delayMilliseconds * 2, 250);
            }
        }
    }

    private static bool IsContention(IOException exception)
    {
        var code = exception.HResult & 0xffff;
        // Windows sharing/lock violations; Unix EAGAIN/EWOULDBLOCK from the exclusive file handle.
        return OperatingSystem.IsWindows() ? code is 32 or 33 : code is 11 or 35;
    }
}
