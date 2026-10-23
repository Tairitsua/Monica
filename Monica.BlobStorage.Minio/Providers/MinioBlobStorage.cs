using Microsoft.Extensions.Options;
using Minio;
using Minio.DataModel.Args;
using Minio.Exceptions;
using Monica.BlobStorage.Abstractions;
using Monica.BlobStorage.Exceptions;
using Monica.Modules;

namespace Monica.BlobStorage.Minio.Providers;

internal sealed class MinioBlobStorage(IMinioClient client, IOptions<ModuleMinioBlobStorageOption> options)
    : IBlobStorage
{
    private readonly ModuleMinioBlobStorageOption _options = options.Value;

    public async Task SaveAsync(string name, Stream content, bool overrideExisting = false,
        CancellationToken cancellationToken = default)
    {
        var objectName = GetObjectName(name);
        ArgumentNullException.ThrowIfNull(content);
        if (!content.CanRead) throw new ArgumentException("The blob stream must be readable.", nameof(content));
        cancellationToken.ThrowIfCancellationRequested();

        // This preserves check-then-write semantics; it is not an atomic conditional write.
        if (!overrideExisting && await ExistsAsync(name, cancellationToken))
            throw new BlobAlreadyExistsException(name);

        if (_options.CreateBucketIfNotExists &&
            !await client.BucketExistsAsync(new BucketExistsArgs().WithBucket(_options.BucketName), cancellationToken))
        {
            try
            {
                await client.MakeBucketAsync(new MakeBucketArgs().WithBucket(_options.BucketName), cancellationToken);
            }
            catch (ArgumentException exception) when (exception.ParamName == "response" &&
                exception.Message.StartsWith("Bucket already owned by you: ", StringComparison.Ordinal))
            {
                // Minio 7 maps BucketAlreadyOwnedByYou to this ArgumentException instead of a typed SDK error.
                // Another writer may create the bucket after our check; only confirmed existence permits retrying.
                cancellationToken.ThrowIfCancellationRequested();
                if (!await client.BucketExistsAsync(new BucketExistsArgs().WithBucket(_options.BucketName), cancellationToken))
                    throw;
                cancellationToken.ThrowIfCancellationRequested();
            }
        }

        // Minio accepts -1 for an unknown-length stream. Seekable streams upload their remaining bytes.
        var length = content.CanSeek ? content.Length - content.Position : -1;
        await client.PutObjectAsync(new PutObjectArgs().WithBucket(_options.BucketName).WithObject(objectName)
            .WithStreamData(content).WithObjectSize(length), cancellationToken);
    }

    public async Task<bool> DeleteAsync(string name, CancellationToken cancellationToken = default)
    {
        var objectName = GetObjectName(name);
        if (!await ExistsAsync(name, cancellationToken)) return false;
        try
        {
            await client.RemoveObjectAsync(new RemoveObjectArgs().WithBucket(_options.BucketName).WithObject(objectName),
                cancellationToken);
            return true;
        }
        catch (ObjectNotFoundException)
        {
            return false;
        }
        catch (BucketNotFoundException)
        {
            return false;
        }
    }

    public async Task<bool> ExistsAsync(string name, CancellationToken cancellationToken = default)
    {
        var objectName = GetObjectName(name);
        cancellationToken.ThrowIfCancellationRequested();
        if (!await client.BucketExistsAsync(new BucketExistsArgs().WithBucket(_options.BucketName), cancellationToken))
            return false;
        try
        {
            await client.StatObjectAsync(new StatObjectArgs().WithBucket(_options.BucketName).WithObject(objectName),
                cancellationToken);
            return true;
        }
        catch (ObjectNotFoundException)
        {
            return false;
        }
        catch (BucketNotFoundException)
        {
            return false;
        }
    }

    public async Task<Stream?> GetOrNullAsync(string name, CancellationToken cancellationToken = default)
    {
        var objectName = GetObjectName(name);
        if (!await ExistsAsync(name, cancellationToken)) return null;
        var buffer = new MemoryStream();
        try
        {
            await client.GetObjectAsync(new GetObjectArgs().WithBucket(_options.BucketName).WithObject(objectName)
                .WithCallbackStream((stream, token) => stream.CopyToAsync(buffer, token)), cancellationToken);
            buffer.Position = 0;
            return buffer;
        }
        catch (ObjectNotFoundException)
        {
            await buffer.DisposeAsync();
            return null;
        }
        catch (BucketNotFoundException)
        {
            await buffer.DisposeAsync();
            return null;
        }
        catch
        {
            await buffer.DisposeAsync();
            throw;
        }
    }

    private string GetObjectName(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return _options.ObjectKeyPrefix + name;
    }
}
