using Monica.ApplicationModel.Abstractions;
using Monica.Core.Domains.Abstractions;

namespace Monica.ApplicationModel.Services;

internal sealed class ApplicationDomainCatalogSource(IApplicationModel model) : IDomainCatalogSource
{
    public IEnumerable<string> GetDomainIds() => model.Domains.Keys;
}
