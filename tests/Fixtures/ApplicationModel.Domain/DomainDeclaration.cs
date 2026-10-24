using Monica.ApplicationModel.Annotations;
using Monica.Core.TypeDiscovery.Annotations;

[assembly: ApplicationDomain(ApplicationModel.Domain.DomainId.Ordering)]

namespace ApplicationModel.Domain;

public enum DomainId { Ordering }
public sealed class DomainDeclaration;
[ProcessLocal]
public sealed class DomainLocalInitialization;
