using System.Reflection;
using AwesomeAssertions;
using Monica.Core.Modularity.Diagnostics.Models;
using Monica.Core.Modularity.Diagnostics.Services.Support;
using Monica.Core.Modularity.Models;
using Monica.Tool.Extensions;
using Xunit;

namespace Test.Monica.Core.Modularity;

public sealed class ModuleOptionDiagnosticsProjectorTests
{
    [Fact]
    public void Project_WhenRootEntryBudgetIsExhausted_ShouldPublishExplicitTruncation()
    {
        var projector = new ModuleOptionDiagnosticsProjector();
        var options = new ProjectionBudgetProbe();

        var diagnostics = projector.Project(
            ModuleKey.FromModuleType(typeof(DiagnosticsConsumerModule)),
            requestedProfileName: null,
            effectiveProfileName: null,
            ModuleOptionProfileResolution.Default,
            typeof(ProjectionBudgetProbe),
            options,
            ModuleOptionDiagnosticsExposureMode.Redacted,
            ModuleOptionDiagnosticsPolicyDefinition.Empty);

        diagnostics.IsTruncated.Should().BeTrue();
        CountEntries(diagnostics.Entries).Should().Be(200);
        diagnostics.Entries.Should().Contain(entry => entry.IsTruncated);
    }

    [Fact]
    public void Project_WhenExposureModeIsInvalid_ShouldRejectProjection()
    {
        var projector = new ModuleOptionDiagnosticsProjector();
        var options = new ProjectionBudgetProbe();

        Action project = () => projector.Project(
            ModuleKey.FromModuleType(typeof(DiagnosticsConsumerModule)),
            requestedProfileName: null,
            effectiveProfileName: null,
            ModuleOptionProfileResolution.Default,
            typeof(ProjectionBudgetProbe),
            options,
            (ModuleOptionDiagnosticsExposureMode)int.MaxValue,
            ModuleOptionDiagnosticsPolicyDefinition.Empty);

        project.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Project_WhenAddressContainsFragmentOrScpCredentials_ShouldProtectEveryCredentialForm()
    {
        var diagnostics = ProjectAddresses(ModuleOptionDiagnosticsExposureMode.Redacted);

        diagnostics.Entries.Should().Contain(entry =>
            entry.Name == nameof(ProjectionAddressProbe.FragmentEndpoint)
            && entry.IsSensitive
            && entry.Value == "https://example.test/path");
        diagnostics.Entries.Should().Contain(entry =>
            entry.Name == nameof(ProjectionAddressProbe.GitRemoteUrl)
            && entry.IsSensitive
            && entry.Value == "git-user:redacted@example.test:org/repository.git");
        diagnostics.Entries.Should().Contain(entry =>
            entry.Name == nameof(ProjectionAddressProbe.RelativeEndpoint)
            && entry.IsSensitive
            && entry.Value == "/callback");
        diagnostics.Entries.Should().Contain(entry =>
            entry.Name == nameof(ProjectionAddressProbe.SafeRemoteUrl)
            && !entry.IsSensitive
            && entry.Value == "git@example.test:org/repository.git");
        diagnostics.Entries.Select(static entry => entry.Value).Should().NotContain(value =>
            value != null
            && (value.Contains("fragment-secret", StringComparison.Ordinal)
                || value.Contains("scp-secret", StringComparison.Ordinal)));
    }

    [Fact]
    public void Project_WhenSensitiveValueRevealIsEnabled_ShouldRevealBoundedAddressCredentials()
    {
        var diagnostics = ProjectAddresses(ModuleOptionDiagnosticsExposureMode.RevealSensitive);

        diagnostics.Entries.Should().Contain(entry =>
            entry.Name == nameof(ProjectionAddressProbe.FragmentEndpoint)
            && entry.IsSensitive
            && entry.Value!.Contains("fragment-secret", StringComparison.Ordinal));
        diagnostics.Entries.Should().Contain(entry =>
            entry.Name == nameof(ProjectionAddressProbe.GitRemoteUrl)
            && entry.IsSensitive
            && entry.Value!.Contains("scp-secret", StringComparison.Ordinal));
        diagnostics.Entries.Should().Contain(entry =>
            entry.Name == nameof(ProjectionAddressProbe.RelativeEndpoint)
            && entry.IsSensitive
            && entry.Value!.Contains("relative-fragment-secret", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(ModuleOptionDiagnosticsExposureMode.Redacted)]
    [InlineData(ModuleOptionDiagnosticsExposureMode.RevealSensitive)]
    public void Project_WhenConfigurationContainsRuntimeBoundaries_ShouldKeepNamesWithoutReturningObjects(
        ModuleOptionDiagnosticsExposureMode exposureMode)
    {
        var projector = new ModuleOptionDiagnosticsProjector();

        var diagnostics = projector.Project(
            ModuleKey.FromModuleType(typeof(DiagnosticsConsumerModule)),
            requestedProfileName: null,
            effectiveProfileName: null,
            ModuleOptionProfileResolution.Default,
            typeof(ProjectionRuntimeBoundaryProbe),
            new ProjectionRuntimeBoundaryProbe(),
            exposureMode,
            ModuleOptionDiagnosticsPolicyDefinition.Empty);

        var expectedNames = typeof(ProjectionRuntimeBoundaryProbe)
            .GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Where(static property => property.GetIndexParameters().Length == 0)
            .Select(static property => property.Name);
        diagnostics.Entries.Select(static entry => entry.Name)
            .Should().BeEquivalentTo(expectedNames);
        diagnostics.Entries.Should().Contain(entry =>
            entry.Name == nameof(ProjectionRuntimeBoundaryProbe.ExtendAction)
            && entry.Kind == ModuleOptionDiagnosticValueKind.Unsupported
            && entry.IsPresent == true
            && entry.Value == null
            && entry.Children.IsEmpty);
        diagnostics.Entries.Should().Contain(entry =>
            entry.Name == nameof(ProjectionRuntimeBoundaryProbe.SigningKey)
            && entry.IsSensitive
            && entry.Kind == (exposureMode == ModuleOptionDiagnosticsExposureMode.Redacted
                ? ModuleOptionDiagnosticValueKind.Presence
                : ModuleOptionDiagnosticValueKind.Unsupported));
        diagnostics.Entries.Should().Contain(entry =>
            entry.Name == nameof(ProjectionRuntimeBoundaryProbe.ComputedValue)
            && entry.Kind == ModuleOptionDiagnosticValueKind.Unsupported
            && entry.Value == null
            && entry.IsPresent == null
            && entry.Children.IsEmpty);
        diagnostics.Entries.Should().Contain(entry =>
            entry.Name == nameof(ProjectionRuntimeBoundaryProbe.CustomBackedValue)
            && entry.Kind == ModuleOptionDiagnosticValueKind.Unsupported
            && entry.Value == null
            && entry.IsPresent == null
            && entry.Children.IsEmpty);
        diagnostics.Entries.Should().Contain(entry =>
            entry.Name == nameof(ProjectionRuntimeBoundaryProbe.InheritedLabel)
            && entry.Kind == ModuleOptionDiagnosticValueKind.Value
            && entry.Value == "inherited");
        diagnostics.Entries.Should().Contain(entry =>
            entry.Name == nameof(ProjectionRuntimeBoundaryProbe.OptionalLimit)
            && entry.TypeName == typeof(int?).GetCleanFullName());
        diagnostics.Entries.Should().Contain(entry =>
            entry.Name == nameof(ProjectionRuntimeBoundaryProbe.MappingType)
            && entry.Value == typeof(Dictionary<string, List<int?>>).GetCleanFullName());
        diagnostics.OptionTypeName.Should().Be(typeof(ProjectionRuntimeBoundaryProbe).GetCleanFullName());
        System.Text.Json.JsonSerializer.Serialize(diagnostics).Should().NotContain("1,2,3");
    }

    [Fact]
    public void Project_WhenDictionaryKeysAreRead_ShouldRevealThemOnlyInSensitiveValueReveal()
    {
        const string secretKey = "opaque-secret-used-as-a-dictionary-key";
        var projector = new ModuleOptionDiagnosticsProjector();
        var options = new ProjectionDictionaryProbe
        {
            Headers = new Dictionary<string, string> { [secretKey] = "ordinary-value" }
        };

        var redacted = projector.Project(
            ModuleKey.FromModuleType(typeof(DiagnosticsConsumerModule)),
            requestedProfileName: null,
            effectiveProfileName: null,
            ModuleOptionProfileResolution.Default,
            typeof(ProjectionDictionaryProbe),
            options,
            ModuleOptionDiagnosticsExposureMode.Redacted,
            ModuleOptionDiagnosticsPolicyDefinition.Empty);
        var revealed = projector.Project(
            ModuleKey.FromModuleType(typeof(DiagnosticsConsumerModule)),
            requestedProfileName: null,
            effectiveProfileName: null,
            ModuleOptionProfileResolution.Default,
            typeof(ProjectionDictionaryProbe),
            options,
            ModuleOptionDiagnosticsExposureMode.RevealSensitive,
            ModuleOptionDiagnosticsPolicyDefinition.Empty);

        var redactedDictionary = redacted.Entries.Single();
        redactedDictionary.TypeName.Should().Be(typeof(Dictionary<string, string>).GetCleanFullName());
        redactedDictionary.TypeName.Should().NotContain("`").And.NotContain("[[");
        redactedDictionary.Children.Should().ContainSingle(entry => entry.Name == "[0]");
        System.Text.Json.JsonSerializer.Serialize(redacted).Should().NotContain(secretKey);
        revealed.Entries.Single().Children.Should().ContainSingle(entry => entry.Name == $"[{secretKey}]");
    }

    [Fact]
    public void Project_WhenARevealedSensitiveStringIsEmpty_ShouldNotReportVisibleSensitiveContent()
    {
        var projector = new ModuleOptionDiagnosticsProjector();

        var diagnostics = projector.Project(
            ModuleKey.FromModuleType(typeof(DiagnosticsConsumerModule)),
            requestedProfileName: null,
            effectiveProfileName: null,
            ModuleOptionProfileResolution.Default,
            typeof(ProjectionEmptySecretProbe),
            new ProjectionEmptySecretProbe(),
            ModuleOptionDiagnosticsExposureMode.RevealSensitive,
            ModuleOptionDiagnosticsPolicyDefinition.Empty);

        diagnostics.ContainsRevealedSensitiveValues.Should().BeFalse();
    }

    private static ModuleOptionDiagnostics ProjectAddresses(ModuleOptionDiagnosticsExposureMode exposureMode)
    {
        var projector = new ModuleOptionDiagnosticsProjector();
        return projector.Project(
            ModuleKey.FromModuleType(typeof(DiagnosticsConsumerModule)),
            requestedProfileName: null,
            effectiveProfileName: null,
            ModuleOptionProfileResolution.Default,
            typeof(ProjectionAddressProbe),
            new ProjectionAddressProbe(),
            exposureMode,
            ModuleOptionDiagnosticsPolicyDefinition.Empty);
    }

    private static int CountEntries(IEnumerable<ModuleOptionDiagnosticEntry> entries) =>
        entries.Sum(entry => 1 + CountEntries(entry.Children));
}

internal sealed class ProjectionBudgetProbe
{
    public List<int> Collection01 { get; set; } = CreateCollection();

    public List<int> Collection02 { get; set; } = CreateCollection();

    public List<int> Collection03 { get; set; } = CreateCollection();

    public List<int> Collection04 { get; set; } = CreateCollection();

    public List<int> Collection05 { get; set; } = CreateCollection();

    public List<int> Collection06 { get; set; } = CreateCollection();

    public List<int> Collection07 { get; set; } = CreateCollection();

    public List<int> Collection08 { get; set; } = CreateCollection();

    public List<int> Collection09 { get; set; } = CreateCollection();

    public List<int> Collection10 { get; set; } = CreateCollection();

    private static List<int> CreateCollection() => Enumerable.Range(0, 20).ToList();
}

internal sealed class ProjectionAddressProbe
{
    public string FragmentEndpoint { get; set; } =
        "https://example.test/path#access_token=fragment-secret";

    public string GitRemoteUrl { get; set; } =
        "git-user:scp-secret@example.test:org/repository.git";

    public string RelativeEndpoint { get; set; } =
        "/callback#access_token=relative-fragment-secret";

    public string SafeRemoteUrl { get; set; } =
        "git@example.test:org/repository.git";
}

internal abstract class ProjectionRuntimeBoundaryProbeBase
{
    public string InheritedLabel { get; set; } = "inherited";
}

internal sealed class ProjectionRuntimeBoundaryProbe : ProjectionRuntimeBoundaryProbeBase
{
    public Action ExtendAction { get; set; } = static () => { };

    public byte[] SigningKey { get; set; } = [1, 2, 3];

    public int? OptionalLimit { get; set; }

    public Type MappingType { get; set; } = typeof(Dictionary<string, List<int?>>);

    public string ComputedValue => throw new InvalidOperationException("Computed getters must not be invoked.");

    public string CustomBackedValue
    {
        get => throw new InvalidOperationException("Custom getters must not be invoked.");
        set { }
    }
}

internal sealed class ProjectionDictionaryProbe
{
    public Dictionary<string, string> Headers { get; set; } = [];
}

internal sealed class ProjectionEmptySecretProbe
{
    public string ApiKey { get; set; } = string.Empty;
}
