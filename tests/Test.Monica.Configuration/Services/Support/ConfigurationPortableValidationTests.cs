using System.ComponentModel;
using System.Globalization;
using AwesomeAssertions;
using Monica.Configuration.Annotations;
using Monica.Configuration.Models;
using Monica.Configuration.Serialization;
using Monica.Configuration.Services;
using Monica.Configuration.Services.Support;
using Xunit;

namespace Test.Monica.Configuration.Services.Support;

public sealed class ConfigurationPortableValidationTests
{
    [Fact]
    public void ValidateCompleteValue_WhenPublishedPortableEnumMetadataHasNoLocalRegistry_ShouldValidateWithoutExecutingOwnerTypes()
    {
        var local = new ConfigurationDefinitionScanner(new ConfigurationSchemaHasher()).Scan(typeof(PortableOptions));
        var remote = ConfigurationDefinitionSchemaCodec.DeserializeDefinition(local.DefinitionKey, local.SectionPath,
            local.DisplayName, null, "Remote.PortableOptions", local.FromProject, null, local.SchemaVersion, local.SchemaHash,
            local.ReloadBehavior, ConfigurationDefinitionSchemaCodec.SerializeSchema(local), ConfigurationDefinitionOrigin.PublishedMetadata);
        var coordinator = ConfigurationValidationTestServices.CreateCoordinator();

        var valid = coordinator.ValidateCompleteValue(remote, """{"Mode":"One","Map":{"One":1}}""");
        valid.Coverage.Should().Be(ConfigurationValidationCoverage.Complete);
        valid.IsValid.Should().BeTrue();
        coordinator.ValidateCompleteValue(remote, """{"Mode":"One","Map":{"Unknown":1}}""").IsValid.Should().BeFalse();
    }

    [Fact]
    public void ValidateCompleteValue_WhenRemoteTypeIdentityHasACustomConverter_ShouldNeverExecuteTheConverter()
    {
        var definition = TestConfigurationFactory.Definition() with
        {
            Origin = ConfigurationDefinitionOrigin.PublishedMetadata,
            Root = TestConfigurationFactory.ScalarNode("Value", typeof(PoisonScalar), ConfigurationValueKind.String,
                LogicalPath.Root, "Poison")
        };
        var result = ConfigurationValidationTestServices.CreateCoordinator().ValidateCompleteValue(definition, "\"candidate\"");
        result.IsValid.Should().BeFalse();
        PoisonConverter.Attempts.Should().Be(0);
    }

    [Fact]
    public void SerializeSchema_WhenCodeRevisionChanges_ShouldPublishItWithoutChangingTheStructuralHash()
    {
        var definition = new ConfigurationDefinitionScanner(new ConfigurationSchemaHasher()).Scan(typeof(PortableOptions));
        var updated = definition with { ValidationContract = definition.ValidationContract with { Revision = "sha256:new-code-revision" } };
        ConfigurationDefinitionSchemaCodec.SerializeSchema(updated).Should().NotBe(ConfigurationDefinitionSchemaCodec.SerializeSchema(definition));
        ConfigurationDefinitionSchemaCodec.ComputeSchemaHash(updated.DefinitionKey, updated.SectionPath, updated.Root)
            .Should().Be(definition.SchemaHash);
    }

    [Fact]
    public void MetadataFormatting_WhenTheNamedAssemblyIsUnavailable_ShouldFormatWithoutResolvingIt()
    {
        ConfigurationDefinitionSchemaCodec.ToCompactClrTypeName(
            "Remote.Pair`2[[Remote.Value, Unavailable.Owner],[System.Int32[], System.Private.CoreLib]], Unavailable.Owner")
            .Should().Be("Remote.Pair<Remote.Value,System.Int32[]>");
        var node = TestConfigurationFactory.ScalarNode("Mode", typeof(PortableMode), ConfigurationValueKind.Enum,
            LogicalPath.Root, "Remote") with { ClrTypeName = "Remote.Mode, Unavailable.Owner", EnumValues = [] };
        node.TryNormalizeEnumDisplayValue("One", out _).Should().BeFalse();
    }

    [Configuration("Portable")]
    private sealed class PortableOptions
    {
        public PortableMode Mode { get; set; }
        public Dictionary<PortableMode, int> Map { get; set; } = [];
    }
    private enum PortableMode { One = 1, Two = 2 }
    [TypeConverter(typeof(PoisonConverter))]
    private sealed class PoisonScalar;
    private sealed class PoisonConverter : TypeConverter
    {
        internal static int Attempts { get; private set; }
        public override bool CanConvertFrom(ITypeDescriptorContext? context, Type sourceType)
        {
            Attempts++;
            return true;
        }
        public override object? ConvertFrom(ITypeDescriptorContext? context, CultureInfo? culture, object value)
        {
            Attempts++;
            throw new InvalidOperationException("Synthetic remote converter must never run.");
        }
    }
}
