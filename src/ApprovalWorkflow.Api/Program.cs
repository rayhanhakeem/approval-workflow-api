using System.Text.Json.Serialization;
using ApprovalWorkflow.Api;

var builder = WebApplication.CreateBuilder(args);
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddSingleton<ApprovalStore>();

var app = builder.Build();

app.UseExceptionHandler(errorApp => errorApp.Run(async context =>
{
    context.Response.StatusCode = StatusCodes.Status500InternalServerError;
    await context.Response.WriteAsJsonAsync(new ApiError("INTERNAL_ERROR", "Unexpected error"));
}));

// Demo identity: the gateway is expected to authenticate the caller in production (JWT/OIDC).
app.Use(async (context, next) =>
{
    if (context.Request.Path.StartsWithSegments("/health"))
    {
        await next();
        return;
    }

    var user = context.Request.Headers["X-User"].ToString();
    if (string.IsNullOrWhiteSpace(user))
    {
        await Results.Json(new ApiError("UNAUTHENTICATED", "X-User header is required"), statusCode: 401)
            .ExecuteAsync(context);
        return;
    }

    if (!Enum.TryParse<Role>(context.Request.Headers["X-Role"].ToString(), ignoreCase: true, out var role))
    {
        await Results.Json(new ApiError("UNAUTHENTICATED", "X-Role must be Requester or Approver"), statusCode: 401)
            .ExecuteAsync(context);
        return;
    }

    context.Items["user"] = user.Trim();
    context.Items["role"] = role;
    await next();
});

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

app.MapPost("/api/v1/requests", (CreateRequest input, HttpContext http, ApprovalStore store) =>
{
    if (CurrentRole(http) != Role.Requester)
    {
        return Forbidden("Only a Requester can submit an approval request");
    }

    var errors = Validate(input);
    if (errors is not null)
    {
        return Results.Json(new ApiError("VALIDATION_ERROR", errors), statusCode: 400);
    }

    var outcome = store.Create(input, CurrentUser(http));
    return Results.Created(outcome.Location!, outcome.Request);
});

app.MapGet("/api/v1/requests", (string? status, HttpContext http, ApprovalStore store) =>
{
    RequestStatus? filter = null;
    if (!string.IsNullOrWhiteSpace(status))
    {
        if (!Enum.TryParse<RequestStatus>(status, ignoreCase: true, out var parsed))
        {
            return Results.Json(new ApiError("INVALID_FILTER", "status must be Pending, Approved, or Rejected"), statusCode: 400);
        }

        filter = parsed;
    }

    return Results.Ok(store.List(filter));
});

app.MapGet("/api/v1/requests/{id:guid}", (Guid id, ApprovalStore store) =>
{
    var request = store.Find(id);
    return request is null
        ? Results.NotFound(new ApiError("REQUEST_NOT_FOUND", "Approval request not found"))
        : Results.Ok(request);
});

app.MapPost("/api/v1/requests/{id:guid}/decisions", (Guid id, DecisionRequest decision, HttpContext http, ApprovalStore store) =>
{
    if (CurrentRole(http) != Role.Approver)
    {
        return Forbidden("Only an Approver can decide a request");
    }

    var outcome = store.Decide(id, CurrentUser(http), decision);
    return outcome.Error is null
        ? Results.Ok(outcome.Request)
        : Results.Json(outcome.Error, statusCode: outcome.StatusCode);
});

app.Run();

static string CurrentUser(HttpContext http) => (string)http.Items["user"]!;

static Role CurrentRole(HttpContext http) => (Role)http.Items["role"]!;

static IResult Forbidden(string message) =>
    Results.Json(new ApiError("ROLE_FORBIDDEN", message), statusCode: 403);

static string? Validate(CreateRequest input)
{
    if (string.IsNullOrWhiteSpace(input.Title) || input.Title.Trim().Length < 3)
    {
        return "title must be at least 3 characters";
    }

    if (input.Title.Trim().Length > 120)
    {
        return "title must be at most 120 characters";
    }

    return input.Amount is <= 0 or > 1_000_000_000_000m
        ? "amount must be greater than zero and at most 1,000,000,000,000"
        : null;
}

public partial class Program;
