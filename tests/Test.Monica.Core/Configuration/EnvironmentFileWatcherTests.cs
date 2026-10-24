using System.Text;
using AwesomeAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.Json;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Primitives;
using Monica.Core.Configuration;
using Xunit;

namespace Test.Monica.Core.Configuration;

public sealed class EnvironmentFileWatcherTests
{
    [Fact]
    public void Watch_WhenAFileIsDeletedOrUnreadable_ShouldKeepTheAcceptedContributionAndReportSafely()
    {
        using var files = new ControlledFiles { Json = """{"Connection":{"Name":"initial","Items":[]}}""" };
        var errors = new List<string>();
        var state = new EnvironmentFileConfiguration.EnvironmentFileState("site.json", errors.Add);
        var source = new EnvironmentFileConfiguration.EnvironmentFileConfigurationSource(state)
        {
            Path = "site.json", FileProvider = files, Optional = false, ReloadOnChange = true, ReloadDelay = 0
        };
        using var provider = (FileConfigurationProvider)source.Build(new ConfigurationBuilder());
        provider.Should().BeAssignableTo<JsonConfigurationProvider>();
        provider.Should().BeAssignableTo<IDeploymentConfigurationProvider>();
        provider.Source.ReloadOnChange.Should().BeTrue();
        files.WatchCount.Should().Be(0);
        provider.Load();
        files.WatchCount.Should().Be(1);
        var accepted = state.GetDiagnostic().AcceptedRevision;
        var changes = 0;
        using var subscription = ChangeToken.OnChange(provider.GetReloadToken, () => changes++);

        files.Exists = false;
        Action deleted = files.SignalChange;
        deleted.Should().NotThrow();
        provider.TryGet("Connection:Name", out var afterDelete).Should().BeTrue();
        afterDelete.Should().Be("initial");
        state.GetDiagnostic().AcceptedRevision.Should().Be(accepted);
        state.GetDiagnostic().Error.Should().NotBeNull();
        state.GetDiagnostic().LastRejectedAt.Should().NotBeNull();
        changes.Should().Be(0);

        files.Exists = true;
        files.DenyRead = true;
        Action unreadable = files.SignalChange;
        unreadable.Should().NotThrow();
        provider.TryGet("Connection:Name", out var afterUnreadable).Should().BeTrue();
        afterUnreadable.Should().Be("initial");
        state.GetDiagnostic().Error.Should().NotContain("SECRET");
        ((IConfigurationValueShapeProvider)provider).TryGetShape("Connection:Items", out var shape).Should().BeTrue();
        shape.Should().Be(ConfigurationValueShape.List);
        provider.GetChildKeys([], "Connection:Items").Should().BeEmpty();

        files.DenyRead = false;
        files.Json = """{"Connection":{"Name":"accepted","Items":["one"]}}""";
        files.SignalChange();
        provider.TryGet("Connection:Name", out var afterAccepted).Should().BeTrue();
        afterAccepted.Should().Be("accepted");
        state.GetDiagnostic().Error.Should().BeNull();
        state.GetDiagnostic().AcceptedRevision.Should().BeGreaterThan(accepted);
        changes.Should().Be(1);
        errors.Should().HaveCount(2);

        provider.Dispose();
        files.Json = """{"Connection":{"Name":"after-disposal"}}""";
        files.SignalChange();
        state.CaptureProvider().GetSection("Connection:Name").Value.Should().Be("accepted");
        changes.Should().Be(1);
    }

    [Fact]
    public void Load_WhenTheInitialFileIsInvalid_ShouldFailSynchronouslyBeforeWatching()
    {
        using var files = new ControlledFiles { Json = "invalid-json" };
        var state = new EnvironmentFileConfiguration.EnvironmentFileState("site.json", _ => { });
        var source = new EnvironmentFileConfiguration.EnvironmentFileConfigurationSource(state)
        {
            Path = "site.json", FileProvider = files, Optional = false, ReloadOnChange = true, ReloadDelay = 0
        };
        using var provider = (FileConfigurationProvider)source.Build(new ConfigurationBuilder());
        Action load = provider.Load;
        load.Should().Throw<InvalidDataException>();
        files.WatchCount.Should().Be(0);
        state.GetDiagnostic().AcceptedRevision.Should().Be(0);
    }

    [Fact]
    public void Watch_WhenAValidationCallbackIncludesValuesInItsException_ShouldConcealThem()
    {
        using var files = new ControlledFiles { Json = """{"Connection":"initial"}""" };
        var state = new EnvironmentFileConfiguration.EnvironmentFileState("site.json", _ => { });
        var source = new EnvironmentFileConfiguration.EnvironmentFileConfigurationSource(state)
        {
            Path = "site.json", FileProvider = files, Optional = false, ReloadOnChange = true, ReloadDelay = 0
        };
        using var provider = (FileConfigurationProvider)source.Build(new ConfigurationBuilder());
        provider.Load();
        state.Validate(snapshot =>
        {
            if (snapshot.Contains("Rejected")) throw new InvalidOperationException("SECRET-CANDIDATE");
        });
        files.Json = """{"Connection":"candidate","Rejected":true}""";
        files.SignalChange();
        provider.TryGet("Connection", out var value).Should().BeTrue();
        value.Should().Be("initial");
        state.GetDiagnostic().Error.Should().Contain("failed source validation").And.NotContain("SECRET-CANDIDATE");
    }

    private sealed class ControlledFiles : IFileProvider, IDisposable
    {
        private CancellationTokenSource _changed = new();
        internal string Json { get; set; } = "{}";
        internal bool Exists { get; set; } = true;
        internal bool DenyRead { get; set; }
        internal int WatchCount { get; private set; }
        public IFileInfo GetFileInfo(string subpath) => new ControlledFile(this);
        public IDirectoryContents GetDirectoryContents(string subpath) => NotFoundDirectoryContents.Singleton;
        public IChangeToken Watch(string filter)
        {
            WatchCount++;
            return new CancellationChangeToken(_changed.Token);
        }
        internal void SignalChange()
        {
            var previous = _changed;
            _changed = new CancellationTokenSource();
            previous.Cancel();
            previous.Dispose();
        }
        public void Dispose() => _changed.Dispose();

        private sealed class ControlledFile(ControlledFiles owner) : IFileInfo
        {
            public bool Exists => owner.Exists;
            public long Length => Encoding.UTF8.GetByteCount(owner.Json);
            public string? PhysicalPath => null;
            public string Name => "site.json";
            public DateTimeOffset LastModified => DateTimeOffset.UnixEpoch;
            public bool IsDirectory => false;
            public Stream CreateReadStream() => owner.DenyRead
                ? throw new UnauthorizedAccessException("SECRET-ACCESS-DETAIL")
                : new MemoryStream(Encoding.UTF8.GetBytes(owner.Json));
        }
    }
}
