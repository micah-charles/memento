[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'High')]
param()

$ErrorActionPreference = 'Stop'
$targetName = 'MEMENTO/OpenAI'

if (-not $PSCmdlet.ShouldProcess($targetName, 'Save an API credential in the current Windows user Credential Manager')) {
    return
}

$secret = Read-Host 'OpenAI API key (input is hidden)' -AsSecureString
if ($null -eq $secret -or $secret.Length -eq 0) {
    throw 'An OpenAI API key is required.'
}

if (-not ('MementoCredentialWriter' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;

public static class MementoCredentialWriter
{
    private const int GenericCredentialType = 1;
    private const uint LocalMachinePersistence = 2;

    public static void Write(string targetName, IntPtr secret, uint secretBytes)
    {
        var targetPointer = Marshal.StringToCoTaskMemUni(targetName);
        var userPointer = Marshal.StringToCoTaskMemUni("MEMENTO");
        try
        {
            var credential = new NativeCredential
            {
                Type = GenericCredentialType,
                TargetName = targetPointer,
                CredentialBlobSize = secretBytes,
                CredentialBlob = secret,
                Persist = LocalMachinePersistence,
                UserName = userPointer
            };
            if (!CredWrite(ref credential, 0))
            {
                var error = Marshal.GetLastWin32Error();
                throw new System.ComponentModel.Win32Exception(error, "Windows Credential Manager could not save the MEMENTO/OpenAI credential.");
            }
        }
        finally
        {
            Marshal.FreeCoTaskMem(targetPointer);
            Marshal.FreeCoTaskMem(userPointer);
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
'@
}

$secretPointer = [Runtime.InteropServices.Marshal]::SecureStringToCoTaskMemUnicode($secret)
try {
    $secretBytes = [uint32]($secret.Length * 2)
    [MementoCredentialWriter]::Write($targetName, $secretPointer, $secretBytes)
    Write-Output "Saved '$targetName' in the current Windows user's Credential Manager."
}
finally {
    [Runtime.InteropServices.Marshal]::ZeroFreeCoTaskMemUnicode($secretPointer)
    $secret.Dispose()
}
