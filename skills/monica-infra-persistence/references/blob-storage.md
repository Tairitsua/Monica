# Blob storage

Reference `Monica.BlobStorage` for `IBlobStorage` and its byte-array extensions. Reference the optional `Monica.BlobStorage.Minio` package only when selecting Minio. Register one provider inside the host's `AddMonica` callback; no conventional type discovery is required.

```csharp
using Monica.Modules;

builder.AddMonica(monica =>
{
    monica.AddBlobStorage().UseMinio(options =>
    {
        options.Endpoint = builder.Configuration["Storage:Endpoint"]!;
        options.AccessKey = builder.Configuration["Storage:AccessKey"]!;
        options.SecretKey = builder.Configuration["Storage:SecretKey"]!;
        options.BucketName = "documents";
        options.UseSsl = true;
    });
});
```

Supply credentials through the host's configuration sources. Endpoint and bucket are required during composition; credentials are required when storage is first resolved. A host that never uses storage can therefore compose without credentials. Registration performs no network I/O. The endpoint uses `host[:port]` form. TLS defaults to `false`, automatic bucket creation defaults to `false`, and `ObjectKeyPrefix` defaults to empty. The prefix is literal: use `"files/"` when a slash separator is intended. Bucket names pass unchanged to the SDK; the framework does not lowercase, sanitize or migrate them. Changing a bucket or prefix changes the storage namespace. Explicitly preserve existing naming rules when migrating an application.

`IBlobStorage` lives in `Monica.BlobStorage.Abstractions`. `SaveAsync` reads from the caller's current stream position and does not dispose the input. The Minio provider supports seekable and unknown-length readable streams. `overrideExisting` defaults to `false`; an existing object produces `BlobAlreadyExistsException`. Minio checks then writes, so this is not atomic create-only concurrency control. `CreateBucketIfNotExists` creates a missing bucket only during saves.

`ExistsAsync` and `DeleteAsync` return `false` for absence; `GetOrNullAsync` returns `null`. Authentication and transport failures propagate instead of becoming missing objects. The Minio reader buffers the complete object in memory and returns it rewound; the caller must dispose the stream. Byte-array helpers in `Monica.BlobStorage.Extensions` own their temporary streams. Pass cancellation tokens for network and copy operations.

The host owns one singleton provider and Minio client and disposes the client with the service provider. Consumers borrow `IBlobStorage`; do not dispose or replace its client during use. Options are startup configuration, not a live reload contract. A custom provider can use `AddBlobStorage().UseProvider<TProvider>()`; it must be thread-safe and accept singleton-compatible dependencies.

Keep business file records, domain IDs, temporary-file indexes, authorization and retention workflows in the application. Uploading an object and committing a database record are separate operations; blob storage does not enlist in a UnitOfWork or provide a cross-resource transaction. This capability deliberately supplies one configured storage namespace, without a typed-container or tenant-routing hierarchy.
