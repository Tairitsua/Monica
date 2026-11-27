using Microsoft.AspNetCore.WebUtilities;
using Monica.Repository.UI.Pages;

namespace Monica.Repository.UI.UIRepository.Support;

internal static class RepositoryRoute
{
    internal static string For(string path, params (string Key, string? Value)[] query) =>
        QueryHelpers.AddQueryString(path, query.Where(item => !string.IsNullOrEmpty(item.Value))
            .ToDictionary(item => item.Key, item => item.Value));

    internal static string Overview(params (string Key, string? Value)[] query) => For(UIRepositoryDashboardPage.PAGE_URL, query);

    internal static string Activity(string? contextId) => For(UIRepositoryActivityPage.PAGE_URL, ("context", contextId));

    internal static string Migrations(string? contextId) => For(UIRepositoryMigrationsPage.PAGE_URL, ("context", contextId));
}
