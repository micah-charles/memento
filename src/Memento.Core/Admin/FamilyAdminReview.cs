using Memento.Core.Domain;
using Memento.Core.Storage;

namespace Memento.Core.Admin;

public interface IAdminAuthorizer
{
    bool IsAuthorized(string actorId);
}

public sealed class FixedTestAdminAuthorizer(string allowedActorId) : IAdminAuthorizer
{
    public bool IsAuthorized(string actorId) => string.Equals(actorId, allowedActorId, StringComparison.Ordinal);
}

public sealed class FamilyAdminReviewService
{
    private readonly ArchiveRepository _repository;
    private readonly IAdminAuthorizer _authorizer;

    public FamilyAdminReviewService(ArchiveRepository repository, IAdminAuthorizer authorizer)
    {
        _repository = repository;
        _authorizer = authorizer;
    }

    public IReadOnlyList<MemoryClaim> ListCandidates(string actorId)
    {
        DemandAuthorization(actorId);
        return _repository.ListCandidateClaims();
    }

    public ReviewAnnotation AnnotateClaim(string actorId, MemoryClaim claim, string annotationType, string body, string? assessment = null)
    {
        DemandAuthorization(actorId);
        if (claim.Status != ClaimStatus.Candidate) throw new InvalidOperationException("Only candidate claims can be reviewed through this operation.");
        if (!string.Equals(annotationType, "family_assessment", StringComparison.Ordinal)
            && !string.Equals(annotationType, "admin_annotation", StringComparison.Ordinal))
            throw new ArgumentException("Family Admin review can only create family_assessment or admin_annotation records.", nameof(annotationType));
        if (string.IsNullOrWhiteSpace(body)) throw new ArgumentException("A review body is required.", nameof(body));
        if (!_repository.ListCandidateClaims().Any(candidate => string.Equals(candidate.MemoryClaimId, claim.MemoryClaimId, StringComparison.Ordinal)))
            throw new InvalidOperationException("The candidate claim is no longer available for review.");
        var annotation = _repository.AddReviewAnnotation(new ReviewAnnotation(Guid.NewGuid().ToString("N"), "memory_claim", claim.MemoryClaimId, actorId, annotationType, body, assessment, DateTimeOffset.UtcNow));
        if (string.Equals(annotationType, "family_assessment", StringComparison.Ordinal) && string.Equals(assessment, "supported", StringComparison.OrdinalIgnoreCase))
            _repository.UpdateMemoryClaimStatus(claim.MemoryClaimId, ClaimStatus.Reviewed);
        if (string.Equals(annotationType, "admin_annotation", StringComparison.Ordinal) && string.Equals(assessment, "rejected", StringComparison.OrdinalIgnoreCase))
            _repository.UpdateMemoryClaimStatus(claim.MemoryClaimId, ClaimStatus.Rejected);
        return annotation;
    }

    private void DemandAuthorization(string actorId)
    {
        if (string.IsNullOrWhiteSpace(actorId) || !_authorizer.IsAuthorized(actorId))
            throw new UnauthorizedAccessException("Family Admin authorization is required.");
    }
}
