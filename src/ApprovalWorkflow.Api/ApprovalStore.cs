using System.Collections.Concurrent;

namespace ApprovalWorkflow.Api;

public sealed record Outcome(ApprovalRequest? Request, ApiError? Error, int StatusCode, string? Location = null);

/// <summary>Single-node in-memory store. Transitions are serialized so the state machine cannot race.</summary>
public sealed class ApprovalStore
{
    private readonly ConcurrentDictionary<Guid, ApprovalRequest> _requests = new();
    private readonly object _gate = new();

    public Outcome Create(CreateRequest input, string requester)
    {
        var now = DateTimeOffset.UtcNow;
        var request = new ApprovalRequest(
            Guid.NewGuid(), input.Title.Trim(), input.Amount, requester,
            RequestStatus.Pending, Version: 1, now,
            [new AuditEntry(now, requester, "submitted", null, RequestStatus.Pending, null)]);

        _requests[request.Id] = request;
        return new Outcome(request, null, 201, $"/api/v1/requests/{request.Id}");
    }

    public ApprovalRequest? Find(Guid id) => _requests.GetValueOrDefault(id);

    public IReadOnlyList<ApprovalRequest> List(RequestStatus? status) =>
        _requests.Values
            .Where(request => status is null || request.Status == status)
            .OrderByDescending(request => request.CreatedAt)
            .ToList();

    public Outcome Decide(Guid id, string actor, DecisionRequest decision)
    {
        lock (_gate)
        {
            if (!_requests.TryGetValue(id, out var request))
            {
                return new Outcome(null, new ApiError("REQUEST_NOT_FOUND", "Approval request not found"), 404);
            }

            if (request.Status != RequestStatus.Pending)
            {
                return new Outcome(null,
                    new ApiError("INVALID_STATE", $"Request is already {request.Status} and cannot be decided again"), 409);
            }

            if (request.Version != decision.ExpectedVersion)
            {
                return new Outcome(null,
                    new ApiError("VERSION_CONFLICT", $"Request has moved to version {request.Version}"), 409);
            }

            if (string.Equals(request.Requester, actor, StringComparison.OrdinalIgnoreCase))
            {
                return new Outcome(null,
                    new ApiError("SELF_APPROVAL_FORBIDDEN", "The requester cannot approve or reject their own request"), 403);
            }

            var target = decision.Decision?.Trim().ToLowerInvariant() switch
            {
                "approve" => RequestStatus.Approved,
                "reject" => RequestStatus.Rejected,
                _ => (RequestStatus?)null
            };

            if (target is null)
            {
                return new Outcome(null, new ApiError("INVALID_DECISION", "Decision must be 'approve' or 'reject'"), 400);
            }

            if (target == RequestStatus.Rejected && string.IsNullOrWhiteSpace(decision.Comment))
            {
                return new Outcome(null, new ApiError("COMMENT_REQUIRED", "A rejection requires a comment"), 400);
            }

            var status = target.GetValueOrDefault();
            var now = DateTimeOffset.UtcNow;
            var updated = request with
            {
                Status = status,
                Version = request.Version + 1,
                History = [.. request.History, new AuditEntry(now, actor, "decided", request.Status, status, decision.Comment)]
            };

            _requests[id] = updated;
            return new Outcome(updated, null, 200);
        }
    }
}
