using System.Collections.Concurrent;
using System.Reflection;
using Microsoft.AspNetCore.Mvc.ApiExplorer;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using Microsoft.OpenApi;
using Monica.ApplicationModel.Abstractions;
using Monica.ApplicationModel.Annotations;
using Monica.Core;
using Monica.Core.Modularity.Models;
using Monica.Modules;
using Monica.WebApi.Swagger.Models;

namespace Monica.WebApi.Swagger.Services.Support;

/// <summary>
/// Computes the Swagger document set and routes endpoint descriptions into the correct document.
/// </summary>
internal sealed class SwaggerDocumentCatalog
{
    private const string DefaultDocumentVersion = "v1";
    private const string DefaultMonicaDocumentTitle = "Monica API";
    private const string DefaultBusinessDocumentTitle = "Business API";

    private readonly ModuleSwaggerOption _option;
    private readonly IMonicaApplicationOptions _application;
    private readonly IApplicationModel? _model;
    private readonly IReadOnlyDictionary<string, string> _roleDocumentNames;
    private readonly ILogger? _logger;
    private readonly ConcurrentDictionary<string, byte> _unknownResolverDocumentNames = new(StringComparer.OrdinalIgnoreCase);
    private readonly SwaggerDocumentDefinition _businessDocument;
    private readonly IReadOnlyList<SwaggerDocumentDefinition> _documents;
    private readonly IReadOnlyDictionary<string, SwaggerDocumentDefinition> _documentsByName;

    public SwaggerDocumentCatalog(
        ModuleSwaggerOption option,
        IMonicaApplicationOptions application,
        ILogger? logger = null,
        IApplicationModel? model = null)
    {
        _option = option;
        _application = application;
        _logger = logger;
        _model = model;
        _roleDocumentNames = model is null ? new Dictionary<string, string>(StringComparer.Ordinal) : model.Roles.Values
            .Where(role => model.IsRoleAvailable(role.Id) && model.IsRoleActive(role.Id))
            .OrderBy(role => role.Id, StringComparer.Ordinal)
            .ToDictionary(role => role.Id, role => $"role.{role.Id}", StringComparer.Ordinal);
        (_businessDocument, _documents) = BuildDocuments();
        _documentsByName = _documents.ToDictionary(document => document.Name, StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyList<SwaggerDocumentDefinition> Documents => _documents;

    public string DocumentVersion => NormalizeOrDefault(_option.ApiVersion, DefaultDocumentVersion);

    public OpenApiInfo GetOpenApiInfo(SwaggerDocumentDefinition document)
    {
        return new OpenApiInfo
        {
            Title = document.Title,
            Version = DocumentVersion,
            Description = document.Description ?? string.Empty
        };
    }

    public string ResolveDocumentName(ApiDescription description)
    {
        var overriddenDocumentName = _option.DocumentNameResolver?.Invoke(description);
        if (overriddenDocumentName is not null)
        {
            if (TryGetRegisteredDocumentName(overriddenDocumentName, out var resolvedDocumentName))
            {
                return resolvedDocumentName;
            }

            LogUnknownDocumentName(overriddenDocumentName);
        }

        if (TryGetRegisteredDocumentName(description.GroupName, out var groupedDocumentName))
        {
            return groupedDocumentName;
        }

        if (IsMonicaEndpoint(description))
        {
            return _option.Monica.Enabled && TryGetRegisteredDocumentName(_option.Monica.Name, out var monicaDocumentName)
                ? monicaDocumentName : _businessDocument.Name;
        }

        if (ResolveRole(description) is { } roleId && _roleDocumentNames.TryGetValue(roleId, out var roleDocumentName))
            return roleDocumentName;

        return _businessDocument.Name;
    }

    private string? ResolveRole(ApiDescription description)
    {
        if (_model is null) return null;
        var controller = description.ActionDescriptor as ControllerActionDescriptor;
        var receiver = description.ActionDescriptor.EndpointMetadata.OfType<ApplicationApiReceiverAttribute>().SingleOrDefault()
            ?? controller?.MethodInfo.GetCustomAttribute<ApplicationApiReceiverAttribute>();
        if (receiver is not null) return _model.GetOperation(receiver.RequestType).RoleId;

        // Inherited controller and hub methods belong to their concrete receiver's role.
        // Minimal handlers use their actual method metadata when available.
        var assembly = controller?.ControllerTypeInfo.Assembly
            ?? description.ActionDescriptor.EndpointMetadata.OfType<HubMetadata>().LastOrDefault()?.HubType.Assembly
            ?? description.ActionDescriptor.EndpointMetadata.OfType<MethodInfo>().LastOrDefault()?.DeclaringType?.Assembly;
        return assembly is null ? null : _model.FindRole(assembly)?.Id;
    }

    public bool ShouldInclude(string docName, ApiDescription description)
    {
        return !_documentsByName.ContainsKey(docName)
            || string.Equals(ResolveDocumentName(description), docName, StringComparison.OrdinalIgnoreCase);
    }

    public string GetSwaggerEndpointDisplayName(SwaggerDocumentDefinition document)
    {
        return $"{document.Title} {DocumentVersion}";
    }

    private (SwaggerDocumentDefinition BusinessDocument, IReadOnlyList<SwaggerDocumentDefinition> Documents) BuildDocuments()
    {
        var businessDocument = new SwaggerDocumentDefinition(
            NormalizeOrDefault(_option.BusinessDocumentName, DocumentVersion),
            ResolveBusinessDocumentTitle(),
            NormalizeOptional(_option.Description));

        List<SwaggerDocumentDefinition> documents =
        [
            businessDocument
        ];

        if (_option.Monica.Enabled)
        {
            documents.Add(new SwaggerDocumentDefinition(
                NormalizeRequiredName(_option.Monica.Name, "Monica document"),
                NormalizeOrDefault(_option.Monica.Title, DefaultMonicaDocumentTitle),
                NormalizeOptional(_option.Monica.Description)));
        }

        foreach (var (roleId, documentName) in _roleDocumentNames)
            documents.Add(new SwaggerDocumentDefinition(documentName, roleId, null));

        foreach (var descriptor in _option.AdditionalDocuments)
        {
            ArgumentNullException.ThrowIfNull(descriptor);

            var name = NormalizeRequiredName(descriptor.Name, "Additional Swagger document");
            documents.Add(new SwaggerDocumentDefinition(
                name,
                NormalizeOrDefault(descriptor.Title, name),
                NormalizeOptional(descriptor.Description)));
        }

        ValidateDocumentNames(documents.Select(document => document.Name));
        return (businessDocument, documents);
    }

    private string ResolveBusinessDocumentTitle()
    {
        if (!string.IsNullOrWhiteSpace(_option.BusinessDocumentTitle))
        {
            return _option.BusinessDocumentTitle.Trim();
        }

        var appName = _application.ResolveAppName(_option.AppName);
        return string.IsNullOrWhiteSpace(appName)
            ? DefaultBusinessDocumentTitle
            : $"{appName} API";
    }

    private static string NormalizeOrDefault(string? value, string fallback)
    {
        return string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
    }

    private static string? NormalizeOptional(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static string NormalizeRequiredName(
        string? value,
        string documentLabel)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException($"{documentLabel} name must not be empty.");
        }

        return value.Trim();
    }

    private bool TryGetRegisteredDocumentName(
        string? requestedName,
        out string resolvedName)
    {
        resolvedName = string.Empty;
        if (string.IsNullOrWhiteSpace(requestedName))
        {
            return false;
        }

        if (!_documentsByName.TryGetValue(requestedName.Trim(), out var document))
        {
            return false;
        }

        resolvedName = document.Name;
        return true;
    }

    private void LogUnknownDocumentName(string documentName)
    {
        if (_logger is null)
        {
            return;
        }

        var normalizedDocumentName = string.IsNullOrWhiteSpace(documentName)
            ? "<empty>"
            : documentName.Trim();

        if (!_unknownResolverDocumentNames.TryAdd(normalizedDocumentName, 0))
        {
            return;
        }

        _logger.LogWarning(
            "Swagger DocumentNameResolver returned unknown document name '{DocumentName}'. Registered documents: {RegisteredDocuments}. Falling back to ApiDescription.GroupName and default routing.",
            normalizedDocumentName,
            string.Join(", ", _documents.Select(document => document.Name)));
    }

    private static bool IsMonicaEndpoint(ApiDescription description)
    {
        return description.ActionDescriptor.EndpointMetadata.Any(static metadata =>
            metadata is MonicaMinimalApiMetadata or MonicaEndpointMetadata);
    }

    internal static void ValidateDocumentNames(IEnumerable<string> documentNames)
    {
        var duplicateNames = documentNames
            .GroupBy(name => name, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToList();

        if (duplicateNames.Count == 0)
        {
            return;
        }

        throw new InvalidOperationException(
            $"Swagger document names must be unique. Duplicates: {string.Join(", ", duplicateNames)}");
    }
}

internal sealed record SwaggerDocumentDefinition(
    string Name,
    string Title,
    string? Description);
