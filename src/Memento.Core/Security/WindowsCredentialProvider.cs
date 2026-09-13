using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Memento.Core.Conversation;

namespace Memento.Core.Security;

/// <summary>Reads an API credential from Windows Credential Manager without exposing it to logs or the archive.</summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsCredentialProvider : IApiCredentialProvider
{
    private const int GenericCredentialType = 1;
    private const int ErrorNotFound = 1168;

    public WindowsCredentialProvider(string targetName = "MEMENTO/OpenAI")
        => TargetName = string.IsNullOrWhiteSpace(targetName) ? throw new ArgumentException("A credential target is required.", nameof(targetName)) : targetName;

    public string TargetName { get; }

    public string? GetApiKey()
    {
        if (!CredRead(TargetName, GenericCredentialType, 0, out var credentialPointer))
        {
            var error = Marshal.GetLastWin32Error();
            if (error == ErrorNotFound) return null;
            throw new Win32Exception(error, "Windows Credential Manager could not read the MEMENTO credential.");
        }

        try
        {
            var credential = Marshal.PtrToStructure<Credential>(credentialPointer);
            if (credential.CredentialBlob == IntPtr.Zero || credential.CredentialBlobSize == 0) return null;
            return Marshal.PtrToStringUni(credential.CredentialBlob, checked((int)credential.CredentialBlobSize / 2))?.TrimEnd('\0');
        }
        finally
        {
            CredFree(credentialPointer);
        }
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredRead(string targetName, int type, int flags, out IntPtr credential);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool CredFree(IntPtr credential);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Credential
    {
        public uint Flags;
        public uint Type;
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
