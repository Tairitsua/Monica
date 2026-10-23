namespace Monica.BlobStorage.Exceptions;

/// <summary>Indicates that a save rejected an already existing object.</summary>
public sealed class BlobAlreadyExistsException(string name)
    : IOException($"The blob '{name}' already exists. Enable overwrite to replace it.");
