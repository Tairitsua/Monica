namespace Monica.Invocation.Models;

/// <summary>Separates logical service identity from its private HTTP destination.</summary>
/// <param name="Name">Stable logical name.</param>
/// <param name="AppId">Private destination used by registration and transport only.</param>
public sealed record ApiServiceDescriptor(string Name, string AppId);
