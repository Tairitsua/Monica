using System.Security.Claims;
using Microsoft.Extensions.DependencyInjection;
using Monica.Authority.Authorization.Abstractions;
using Monica.Core.Modularity.Abstractions;
using Monica.Modules;
using Monica.Testing.Hosting;
using Xunit;

namespace Test.Monica.Authority.Authorization;

public sealed class AuthorizationRegistrationTests
{
    [Fact]
    public async Task AddAuthorization_WithPermissionEnum_ShouldComposeAndEnforceDeclaredBits()
    {
        await using var application = await new PermissionFactory()
            .CreateAsync(cancellationToken: TestContext.Current.CancellationToken);
        var checker = application.Services.GetRequiredService<IPermissionBitChecker<Permission>>();
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim("permissions", checker.ToPermissionBits([Permission.Read]))], "test"));

        Assert.True(checker.IsGranted(principal, Permission.Read));
        Assert.False(checker.IsGranted(principal, Permission.Write));
        Assert.NotNull(application.Services.GetRequiredService<IPermissionChecker>());
    }

    private sealed class PermissionFactory : MonicaTestApplicationFactory<AuthorizationRegistrationTests>
    {
        protected override void ConfigureMonica(IMonicaBuilder builder) =>
            builder.AddAuthorization<Permission>("permissions");
    }

    public enum Permission
    {
        None,
        Read,
        Write
    }
}
