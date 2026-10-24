using Monica.Core.TypeDiscovery.Abstractions;

namespace Monica.DataChannel.Services;

/// <summary>Retains one setup's declaring resource owner without constructing the setup.</summary>
internal sealed record DataChannelSetupRegistration(Type ImplementationType, Type? RegistrationOwner, ActivationMode Mode);
