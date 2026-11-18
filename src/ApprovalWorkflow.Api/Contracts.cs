namespace ApprovalWorkflow.Api;

public enum Role
{
    Requester,
    Approver
}

public enum RequestStatus
{
    Pending,
    Approved,
    Rejected
}

public sealed record CreateRequest(string Title, decimal Amount);

public sealed record DecisionRequest(string Decision, string? Comment, int ExpectedVersion);

public sealed record AuditEntry(DateTimeOffset At, string Actor, string Action, RequestStatus? From, RequestStatus? To, string? Comment);

public sealed record ApprovalRequest(
    Guid Id,
    string Title,
    decimal Amount,
    string Requester,
    RequestStatus Status,
    int Version,
    DateTimeOffset CreatedAt,
    IReadOnlyList<AuditEntry> History);

public sealed record ApiError(string ErrorCode, string Message);
