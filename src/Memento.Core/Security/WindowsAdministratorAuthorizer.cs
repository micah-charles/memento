using System.Runtime.Versioning;
using System.Security.Principal;
using Memento.Core.Admin;

namespace Memento.Core.Security;

/// <summary>Authorizes Family Admin operations for the current Windows account when it is an administrator.</summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsAdministratorAuthorizer : IAdminAuthorizer
{
    public string GetCurrentActorId()
        => WindowsIdentity.GetCurrent().Name ?? throw new InvalidOperationException("The current Windows identity has no account name.");

    public bool IsAuthorized(string actorId)
    {
        if (string.IsNullOrWhiteSpace(actorId)) return false;
        using var identity = WindowsIdentity.GetCurrent();
        if (!string.Equals(identity.Name, actorId, StringComparison.OrdinalIgnoreCase)) return false;
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }
}
