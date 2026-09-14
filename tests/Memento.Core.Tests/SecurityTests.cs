using Memento.Core.Security;
using System.Runtime.InteropServices;
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
        Assert.False(ApplicationLockSecret.Verify(new string('x', 1025), "correct horse battery"));
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

    [Fact]
    public void Windows_application_lock_fails_closed_for_a_malformed_credential_blob()
    {
        var target = "MEMENTO/TestMalformedLock/" + Guid.NewGuid().ToString("N");
        var applicationLock = new WindowsApplicationLock(target);
        try
        {
            WriteMalformedCredential(target);

            Assert.Throws<InvalidDataException>(() => _ = applicationLock.IsConfigured);
            Assert.Throws<InvalidDataException>(() => applicationLock.Verify("any passcode"));
        }
        finally
        {
            applicationLock.Clear();
        }
    }

    private static void WriteMalformedCredential(string target)
    {
        var targetPointer = Marshal.StringToCoTaskMemUni(target);
        var userNamePointer = Marshal.StringToCoTaskMemUni("MEMENTO");
        var blob = new byte[] { 0x41 };
        var blobPointer = Marshal.AllocCoTaskMem(blob.Length);
        try
        {
            Marshal.Copy(blob, 0, blobPointer, blob.Length);
            var credential = new NativeCredential
            {
                Type = 1,
                TargetName = targetPointer,
                CredentialBlobSize = (uint)blob.Length,
                CredentialBlob = blobPointer,
                Persist = 2,
                UserName = userNamePointer
            };
            Assert.True(CredWrite(ref credential, 0), $"CredWrite failed: {Marshal.GetLastWin32Error()}");
        }
        finally
        {
            Marshal.FreeCoTaskMem(targetPointer);
            Marshal.FreeCoTaskMem(userNamePointer);
            Marshal.FreeCoTaskMem(blobPointer);
        }
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredWrite(ref NativeCredential userCredential, uint flags);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NativeCredential
    {
        public uint Flags;
        public int Type;
        public IntPtr TargetName;
        public IntPtr Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        public IntPtr TargetAlias;
        public IntPtr UserName;
    }
}
