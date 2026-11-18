using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ApprovalWorkflow.Api;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace ApprovalWorkflow.Tests;

public class ApprovalApiTests
{
    private static HttpClient As(WebApplicationFactory<Program> factory, string user, string role)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-User", user);
        client.DefaultRequestHeaders.Add("X-Role", role);
        return client;
    }

    private static async Task<JsonElement> Body(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

    private static async Task<(Guid Id, int Version)> Submit(HttpClient client, decimal amount = 150_000m)
    {
        var response = await client.PostAsJsonAsync("/api/v1/requests",
            new CreateRequest("Overtime for November", amount));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await Body(response);
        return (body.GetProperty("id").GetGuid(), body.GetProperty("version").GetInt32());
    }

    private static Task<HttpResponseMessage> Decide(
        HttpClient client, Guid id, int version, string decision = "approve", string? comment = null) =>
        client.PostAsJsonAsync($"/api/v1/requests/{id}/decisions",
            new DecisionRequest(decision, comment, version));

    [Fact]
    public async Task Health_is_anonymous()
    {
        using var factory = new WebApplicationFactory<Program>();
        var response = await factory.CreateClient().GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Missing_identity_headers_return_401()
    {
        using var factory = new WebApplicationFactory<Program>();
        var response = await factory.CreateClient().GetAsync("/api/v1/requests");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Requester_submits_request_and_gets_pending_with_history()
    {
        using var factory = new WebApplicationFactory<Program>();
        var requester = As(factory, "rina", "Requester");

        var (id, version) = await Submit(requester);

        var body = await Body(await requester.GetAsync($"/api/v1/requests/{id}"));
        Assert.Equal("Pending", body.GetProperty("status").GetString());
        Assert.Equal(1, version);
        Assert.Equal("submitted", body.GetProperty("history")[0].GetProperty("action").GetString());
    }

    [Fact]
    public async Task Approver_decision_moves_request_and_appends_audit()
    {
        using var factory = new WebApplicationFactory<Program>();
        var requester = As(factory, "rina", "Requester");
        var approver = As(factory, "budi", "Approver");
        var (id, version) = await Submit(requester);

        var response = await Decide(approver, id, version, "approve", "ok");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await Body(response);
        Assert.Equal("Approved", body.GetProperty("status").GetString());
        Assert.Equal(2, body.GetProperty("version").GetInt32());
        var history = body.GetProperty("history");
        Assert.Equal(2, history.GetArrayLength());
        Assert.Equal("budi", history[1].GetProperty("actor").GetString());
        Assert.Equal("Approved", history[1].GetProperty("to").GetString());
    }

    [Fact]
    public async Task Requester_cannot_decide_own_request()
    {
        using var factory = new WebApplicationFactory<Program>();
        var requester = As(factory, "rina", "Requester");
        var (id, version) = await Submit(requester);

        var response = await Decide(As(factory, "rina", "Approver"), id, version);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("SELF_APPROVAL_FORBIDDEN", (await Body(response)).GetProperty("errorCode").GetString());
    }

    [Fact]
    public async Task Approver_cannot_submit_request()
    {
        using var factory = new WebApplicationFactory<Program>();
        var approver = As(factory, "budi", "Approver");

        var response = await approver.PostAsJsonAsync("/api/v1/requests", new CreateRequest("Broken rule", 10m));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Deciding_twice_is_rejected_as_invalid_state()
    {
        using var factory = new WebApplicationFactory<Program>();
        var requester = As(factory, "rina", "Requester");
        var approver = As(factory, "budi", "Approver");
        var (id, version) = await Submit(requester);
        await Decide(approver, id, version);

        var second = await Decide(approver, id, version + 1);

        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        Assert.Equal("INVALID_STATE", (await Body(second)).GetProperty("errorCode").GetString());
    }

    [Fact]
    public async Task Stale_expected_version_conflicts()
    {
        using var factory = new WebApplicationFactory<Program>();
        var requester = As(factory, "rina", "Requester");
        var approver = As(factory, "budi", "Approver");
        var (id, version) = await Submit(requester);
        await Decide(approver, id, version);
        var (secondId, _) = await Submit(requester, 99m);

        var response = await Decide(approver, secondId, version: 7);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("VERSION_CONFLICT", (await Body(response)).GetProperty("errorCode").GetString());
    }

    [Fact]
    public async Task Rejection_requires_comment()
    {
        using var factory = new WebApplicationFactory<Program>();
        var requester = As(factory, "rina", "Requester");
        var approver = As(factory, "budi", "Approver");
        var (id, version) = await Submit(requester);

        var response = await Decide(approver, id, version, "reject");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("COMMENT_REQUIRED", (await Body(response)).GetProperty("errorCode").GetString());
    }

    [Fact]
    public async Task Rejection_with_comment_is_recorded()
    {
        using var factory = new WebApplicationFactory<Program>();
        var requester = As(factory, "rina", "Requester");
        var approver = As(factory, "budi", "Approver");
        var (id, version) = await Submit(requester);

        var response = await Decide(approver, id, version, "reject", "Budget exhausted");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await Body(response);
        Assert.Equal("Rejected", body.GetProperty("status").GetString());
        Assert.Equal("Budget exhausted", body.GetProperty("history")[1].GetProperty("comment").GetString());
    }

    [Fact]
    public async Task List_filters_by_status_and_validates_filter()
    {
        using var factory = new WebApplicationFactory<Program>();
        var requester = As(factory, "rina", "Requester");
        var (id, version) = await Submit(requester);
        await Decide(As(factory, "budi", "Approver"), id, version);

        var approved = await Body(await requester.GetAsync("/api/v1/requests?status=approved"));
        var pending = await Body(await requester.GetAsync("/api/v1/requests?status=pending"));
        var invalid = await requester.GetAsync("/api/v1/requests?status=lost");

        Assert.Equal(1, approved.GetArrayLength());
        Assert.Equal(0, pending.GetArrayLength());
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
    }

    [Fact]
    public async Task Unknown_request_returns_404()
    {
        using var factory = new WebApplicationFactory<Program>();
        var requester = As(factory, "rina", "Requester");

        var response = await requester.GetAsync($"/api/v1/requests/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
