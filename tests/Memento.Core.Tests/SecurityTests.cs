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

    [Fact]
    public void Windows_admin_authorizer_rejects_an_empty_actor_without_identity_access()
    {
        var authorizer = new WindowsAdministratorAuthorizer();
        Assert.False(authorizer.IsAuthorized(""));
    }

    [Fact]
    public void Application_lock_secret_round_trips_without_storing_the_passcode()
    {
        var encoded = ApplicationLockSecret.Create("correct horse battery");

        Assert.DoesNotContain("correct horse battery", encoded, StringComparison.Ordinal);
        Assert.True(ApplicationLockSecret.Verify(encoded, "correct horse battery"));
        Assert.False(ApplicationLockSecret.Verify(encoded, "wrong passcode"));
        Assert.False(ApplicationLockSecret.Verify("v1|bad|secret", "correct horse battery"));
        Assert.Throws<ArgumentException>(() => ApplicationLockSecret.Create("short"));
    }

    [Fact]
    public void Windows_application_lock_round_trips_through_credential_manager()
    {
        var target = "MEMENTO/TestLock/" + Guid.NewGuid().ToString("N");
        var applicationLock = new WindowsApplicationLock(target);
        try
        {
            applicationLock.Clear();
            Assert.False(applicationLock.IsConfigured);
            applicationLock.Configure("test lock passcode");

            Assert.True(applicationLock.IsConfigured);
            Assert.True(applicationLock.Verify("test lock passcode"));
            Assert.False(applicationLock.Verify("wrong passcode"));
        }
        finally
        {
            applicationLock.Clear();
        }

        Assert.False(applicationLock.IsConfigured);
    }
}
