using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

namespace Memento.Core.Security;

public interface IApplicationLock
{
    bool IsConfigured { get; }
    bool Verify(string passcode);
    void Configure(string passcode);
    void Clear();
}

/// <summary>Creates and verifies a salted, iterated application-lock secret without storing a PIN in the archive.</summary>
public static class ApplicationLockSecret
{
    private const int SaltLength = 16;
    private const int HashLength = 32;
    private const int Iterations = 100_000;
    private const string Version = "v1";

    public static string Create(string passcode)
    {
        ValidatePasscode(passcode);
        var salt = RandomNumberGenerator.GetBytes(SaltLength);
        var hash = Derive(passcode, salt);
        return string.Join('|', Version, Convert.ToBase64String(salt), Convert.ToBase64String(hash));
    }

    public static bool Verify(string? encoded, string passcode)
    {
        if (string.IsNullOrEmpty(encoded) || string.IsNullOrEmpty(passcode)) return false;
        var parts = encoded.Split('|');
        if (parts.Length != 3 || !string.Equals(parts[0], Version, StringComparison.Ordinal)) return false;
        byte[] salt;
        byte[] expected;
        try
        {
            salt = Convert.FromBase64String(parts[1]);
            expected = Convert.FromBase64String(parts[2]);
        }
        catch (FormatException)
        {
            return false;
        }

        if (salt.Length != SaltLength || expected.Length != HashLength) return false;
        var actual = Derive(passcode, salt);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    public static void ValidatePasscode(string passcode)
    {
        if (string.IsNullOrWhiteSpace(passcode) || passcode.Length < 6)
            throw new ArgumentException("The application-lock passcode must contain at least six characters.", nameof(passcode));
    }

    private static byte[] Derive(string passcode, byte[] salt)
        => Rfc2898DeriveBytes.Pbkdf2(passcode, salt, Iterations, HashAlgorithmName.SHA256, HashLength);
}

/// <summary>Stores the application-lock verifier in the current Windows user's Credential Manager.</summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsApplicationLock : IApplicationLock
{
    private const int GenericCredentialType = 1;
    private const int LocalMachinePersistence = 2;
    private const int ErrorNotFound = 1168;

    public WindowsApplicationLock(string targetName = "MEMENTO/AppLock")
        => TargetName = string.IsNullOrWhiteSpace(targetName) ? throw new ArgumentException("A credential target is required.", nameof(targetName)) : targetName;

    public string TargetName { get; }

    public bool IsConfigured => ReadSecret() is not null;

    public bool Verify(string passcode)
        => ApplicationLockSecret.Verify(ReadSecret(), passcode);

    public void Configure(string passcode)
        => WriteSecret(ApplicationLockSecret.Create(passcode));

    public void Clear()
    {
        if (CredDelete(TargetName, GenericCredentialType, 0)) return;
        var error = Marshal.GetLastWin32Error();
        if (error != ErrorNotFound)
            throw new Win32Exception(error, "Windows Credential Manager could not remove the MEMENTO application lock.");
    }

    private string? ReadSecret()
    {
        if (!CredRead(TargetName, GenericCredentialType, 0, out var credentialPointer))
        {
            var error = Marshal.GetLastWin32Error();
            if (error == ErrorNotFound) return null;
            throw new Win32Exception(error, "Windows Credential Manager could not read the MEMENTO application lock.");
        }

        try
        {
            var credential = Marshal.PtrToStructure<Credential>(credentialPointer);
            if (credential.CredentialBlob == IntPtr.Zero || credential.CredentialBlobSize == 0 || credential.CredentialBlobSize % 2 != 0)
                return null;
            return Marshal.PtrToStringUni(credential.CredentialBlob, checked((int)credential.CredentialBlobSize / 2))?.TrimEnd('\0');
        }
        finally
        {
            CredFree(credentialPointer);
        }
    }

    private void WriteSecret(string secret)
    {
        var targetPointer = Marshal.StringToCoTaskMemUni(TargetName);
        var userNamePointer = Marshal.StringToCoTaskMemUni("MEMENTO");
        var blob = Encoding.Unicode.GetBytes(secret + "\0");
        var blobPointer = Marshal.AllocCoTaskMem(blob.Length);
        try
        {
            Marshal.Copy(blob, 0, blobPointer, blob.Length);
            var credential = new Credential
            {
                Type = GenericCredentialType,
                TargetName = targetPointer,
                CredentialBlobSize = checked((uint)blob.Length),
                CredentialBlob = blobPointer,
                Persist = LocalMachinePersistence,
                UserName = userNamePointer
            };
            if (!CredWrite(ref credential, 0))
            {
                var error = Marshal.GetLastWin32Error();
                throw new Win32Exception(error, "Windows Credential Manager could not save the MEMENTO application lock.");
            }
        }
        finally
        {
            Marshal.FreeCoTaskMem(targetPointer);
            Marshal.FreeCoTaskMem(userNamePointer);
            Marshal.FreeCoTaskMem(blobPointer);
        }
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredRead(string targetName, int type, int flags, out IntPtr credential);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredWrite(ref Credential userCredential, uint flags);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredDelete(string targetName, int type, int flags);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool CredFree(IntPtr credential);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Credential
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
