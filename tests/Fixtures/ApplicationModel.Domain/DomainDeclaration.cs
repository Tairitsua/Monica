using Monica.ApplicationModel.Annotations;

[assembly: ApplicationDomain(ApplicationModel.Domain.DomainId.Ordering)]

namespace ApplicationModel.Domain;

public enum DomainId { Ordering }
public sealed class DomainDeclaration;
