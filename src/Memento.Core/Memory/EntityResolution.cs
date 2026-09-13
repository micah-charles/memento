using Memento.Core.Domain;
using Memento.Core.Storage;

namespace Memento.Core.Memory;

public sealed class EntityResolutionService
{
    private readonly ArchiveRepository _repository;

    public EntityResolutionService(ArchiveRepository repository) => _repository = repository;

    public PersonEntity CreatePerson(string displayName, string? relationship = null)
    {
        if (string.IsNullOrWhiteSpace(displayName)) throw new ArgumentException("A display name is required.", nameof(displayName));
        return _repository.AddPersonEntity(new PersonEntity(Guid.NewGuid().ToString("N"), displayName, relationship, DateTimeOffset.UtcNow));
    }

    public EntityAlias AddSpeakerConfirmedAlias(PersonEntity person, string alias, string? clarificationEventId = null)
    {
        if (string.IsNullOrWhiteSpace(alias)) throw new ArgumentException("An alias is required.", nameof(alias));
        if (string.IsNullOrWhiteSpace(clarificationEventId)) throw new InvalidOperationException("A speaker-confirmed alias requires a clarification event.");
        return _repository.AddEntityAlias(new EntityAlias(Guid.NewGuid().ToString("N"), person.PersonEntityId, alias, true, clarificationEventId, DateTimeOffset.UtcNow));
    }

    public EvidenceEntityLink LinkEvidence(EvidenceRecord evidence, PersonEntity person, string role)
        => _repository.AddEvidenceEntityLink(new EvidenceEntityLink(evidence.EvidenceId, person.PersonEntityId, role, DateTimeOffset.UtcNow));
}
