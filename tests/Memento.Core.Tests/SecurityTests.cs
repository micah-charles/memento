using Memento.Core.Security;
using System.Runtime.Versioning;

namespace Memento.Core.Tests;

[SupportedOSPlatform("windows")]
public sealed class SecurityTests
{
    [Fact]
    public void Credential_provider_requires_a_nonempty_target_without_touching_archive()
    {
        Assert.Throws<ArgumentException>(() => new WindowsCredentialProvider(" "));
        var provider = new WindowsCredentialProvider("MEMENTO/test");
        Assert.Equal("MEMENTO/test", provider.TargetName);
    }
}
