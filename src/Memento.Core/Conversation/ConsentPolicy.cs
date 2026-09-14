using Memento.Core.Domain;

namespace Memento.Core.Conversation;

/// <summary>Centralizes the participant's cloud-consent boundary for a session.</summary>
public static class ConsentPolicy
{
    public static bool CloudConsentGranted(PrivacyMode privacyMode, bool requested)
        => requested && !CloudNotPermittedException.IsBlocked(privacyMode);
}
