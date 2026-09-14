using Memento.Core.Domain;
using Memento.Core.Storage;

namespace Memento.Core.Memory;

public sealed record PersonMatchSuggestion(PersonEntity Person, string MatchedText, double Score, bool ExactMatch, bool SpeakerConfirmed);

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

    /// <summary>
    /// Returns conservative, review-only identity suggestions. Suggestions do
    /// not create a person, alias, or Evidence link; a participant or Family
    /// Admin must explicitly confirm the identity before any link is written.
    /// Exact matching is allowed for short names, while fuzzy matching is
    /// deliberately disabled for names of three characters or fewer.
    /// </summary>
    public IReadOnlyList<PersonMatchSuggestion> SuggestMatches(string mention, double minimumScore = 0.72, int limit = 5)
    {
        if (string.IsNullOrWhiteSpace(mention)) throw new ArgumentException("A name or mention is required.", nameof(mention));
        if (minimumScore is < 0 or > 1) throw new ArgumentOutOfRangeException(nameof(minimumScore), "The minimum score must be between zero and one.");
        if (limit is < 1 or > 50) throw new ArgumentOutOfRangeException(nameof(limit), "The result limit must be between one and fifty.");

        var normalizedMention = Normalize(mention);
        if (normalizedMention.Length == 0) throw new ArgumentException("A name or mention is required.", nameof(mention));
        var people = _repository.ListPersonEntities().ToDictionary(person => person.PersonEntityId, StringComparer.Ordinal);
        var best = new Dictionary<string, PersonMatchSuggestion>(StringComparer.Ordinal);

        foreach (var person in people.Values)
        {
            Consider(person, person.DisplayName, speakerConfirmed: false);
        }

        foreach (var alias in _repository.ListEntityAliases())
        {
            if (people.TryGetValue(alias.PersonEntityId, out var person))
                Consider(person, alias.Alias, alias.SpeakerConfirmed);
        }

        return best.Values
            .OrderByDescending(item => item.Score)
            .ThenByDescending(item => item.ExactMatch)
            .ThenBy(item => item.Person.CreatedAt)
            .ThenBy(item => item.Person.PersonEntityId, StringComparer.Ordinal)
            .Take(limit)
            .ToArray();

        void Consider(PersonEntity person, string candidateText, bool speakerConfirmed)
        {
            var normalizedCandidate = Normalize(candidateText);
            if (normalizedCandidate.Length == 0) return;
            var exact = string.Equals(normalizedMention, normalizedCandidate, StringComparison.Ordinal);
            if (!exact && (normalizedMention.Length <= 3 || normalizedCandidate.Length <= 3)) return;
            var score = exact ? 1d : Similarity(normalizedMention, normalizedCandidate);
            if (score < minimumScore) return;

            var suggestion = new PersonMatchSuggestion(person, candidateText, score, exact, speakerConfirmed);
            if (!best.TryGetValue(person.PersonEntityId, out var existing)
                || suggestion.Score > existing.Score
                || (suggestion.ExactMatch && !existing.ExactMatch)
                || (suggestion.SpeakerConfirmed && !existing.SpeakerConfirmed && suggestion.Score == existing.Score))
            {
                best[person.PersonEntityId] = suggestion;
            }
        }
    }

    private static string Normalize(string value)
        => string.Concat(value.Normalize(System.Text.NormalizationForm.FormKC)
            .Where(character => char.IsLetterOrDigit(character))
            .Select(char.ToLowerInvariant));

    private static double Similarity(string left, string right)
    {
        var previous = Enumerable.Range(0, right.Length + 1).ToArray();
        for (var i = 1; i <= left.Length; i++)
        {
            var current = new int[right.Length + 1];
            current[0] = i;
            for (var j = 1; j <= right.Length; j++)
            {
                var substitution = previous[j - 1] + (left[i - 1] == right[j - 1] ? 0 : 1);
                current[j] = Math.Min(Math.Min(previous[j] + 1, current[j - 1] + 1), substitution);
            }

            previous = current;
        }

        var distance = previous[right.Length];
        return 1d - (double)distance / Math.Max(left.Length, right.Length);
    }
}
