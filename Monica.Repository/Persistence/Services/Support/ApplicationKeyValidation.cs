using Microsoft.EntityFrameworkCore.Metadata;

namespace Monica.Repository.Persistence.Services.Support;

/// <summary>Rejects missing application-owned UUID keys without generating or replacing identities.</summary>
internal static class ApplicationKeyValidation
{
    public static void Validate(IProperty property, object? value, bool isTemporary = false)
    {
        if (property.ClrType == typeof(Guid) && property.ValueGenerated == ValueGenerated.Never
            && (value is not Guid id || id == Guid.Empty || isTemporary))
        {
            throw new InvalidOperationException(
                $"Assign a permanent, non-empty UUID to '{property.DeclaringType.Name}.{property.Name}' before inserting it.");
        }
    }
}
