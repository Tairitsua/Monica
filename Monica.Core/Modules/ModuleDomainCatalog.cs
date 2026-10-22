using System.ComponentModel;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Monica.Core.Domains.Abstractions;
using Monica.Core.Domains.Models;
using Monica.Core.Domains.Services;
using Monica.Core.Modularity;
using Monica.Core.Modularity.Abstractions;

namespace Monica.Modules;

/// <summary>Registers shared business-domain metadata without requiring an application model, registry, or UI.</summary>
public static class ModuleDomainCatalogBuilderExtensions
{
    extension(IMonicaBuilder builder)
    {
        /// <summary>Configures the host's domain catalog. The default catalog has no explicitly configured domains.</summary>
        public ModuleRegistration<ModuleDomainCatalog, ModuleDomainCatalogOption> AddDomainCatalog(
            Action<ModuleDomainCatalogOption>? configure = null) =>
            builder.AddModule<ModuleDomainCatalog, ModuleDomainCatalogOption>(configure);
    }
}

/// <summary>Owns the shared immutable catalog consumed by framework modules.</summary>
public sealed class ModuleDomainCatalog : MonicaModule<ModuleDomainCatalogOption>
{
    /// <inheritdoc />
    public override void ValidateOptions(ModuleDomainCatalogOption options, string? profileName) =>
        _ = new DomainCatalog(Microsoft.Extensions.Options.Options.Create(options), []);

    /// <inheritdoc />
    public override void ConfigureServices(ModuleContext<ModuleDomainCatalogOption> context) =>
        context.Services.AddSingleton<IDomainCatalog, DomainCatalog>();
}

/// <summary>Startup-only domain metadata. Changes require a new host.</summary>
public sealed class ModuleDomainCatalogOption : ModuleOptions<ModuleDomainCatalog>
{
    /// <summary>Gets explicit metadata declarations. Duplicate identifiers fail catalog construction; defaults to empty.</summary>
    public List<DomainInfo> Domains { get; } = [];

    /// <summary>Imports existing enum identities and Description labels, without inventing another domain registry.</summary>
    /// <remarks>For flags enums, zero and composite values are excluded. Ordinary enums retain their zero member.
    /// Duplicate aliases are rejected by catalog validation. Configure a string-based DomainInfo directly when no enum exists.</remarks>
    public void AddEnum<TEnum>() where TEnum : struct, Enum
    {
        var flags = typeof(TEnum).IsDefined(typeof(FlagsAttribute));
        var values = new HashSet<TEnum>();
        foreach (var field in typeof(TEnum).GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            var value = field.GetValue(null)!;
            if (flags)
            {
                var bits = Type.GetTypeCode(Enum.GetUnderlyingType(typeof(TEnum))) switch
                {
                    TypeCode.SByte => unchecked((byte)Convert.ToSByte(value)),
                    TypeCode.Int16 => unchecked((ushort)Convert.ToInt16(value)),
                    TypeCode.Int32 => unchecked((uint)Convert.ToInt32(value)),
                    TypeCode.Int64 => unchecked((ulong)Convert.ToInt64(value)),
                    _ => Convert.ToUInt64(value)
                };
                if (bits == 0 || (bits & (bits - 1)) != 0) continue;
            }
            if (!values.Add((TEnum)value))
                throw new InvalidOperationException($"Domain enum '{typeof(TEnum).Name}' contains an ambiguous alias '{field.Name}'.");
            Domains.Add(new DomainInfo
            {
                Id = field.Name,
                DisplayName = field.GetCustomAttribute<DescriptionAttribute>()?.Description ?? field.Name
            });
        }
    }
}
