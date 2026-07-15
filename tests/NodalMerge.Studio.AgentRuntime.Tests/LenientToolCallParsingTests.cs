using System.Text.Json;
using NodalMerge.Studio.AgentRuntime;
using Xunit;

namespace NodalMerge.Studio.AgentRuntime.Tests;

// Pins down LlmClient.TryRecoverTextToolCalls — the opt-in lenient fallback that recovers a tool
// call a small local model wrote as message text instead of the structured tool_calls field. The
// false-positive cases (content that merely CONTAINS tool-call-shaped JSON) are the point: lenient
// mode must not hijack a documentation/plan/proposal worker's legitimate output.
public sealed class LenientToolCallParsingTests
{
    private static readonly IReadOnlyList<LlmToolDef> Tools =
    [
        new("nm_v1_workunit_get", "", new { }),
        new("nm_v1_task_update", "", new { }),
    ];

    [Fact]
    public void Recovers_a_bare_json_object_tool_call()
    {
        var ok = LlmClient.TryRecoverTextToolCalls(
            "{\"name\": \"nm_v1_workunit_get\", \"arguments\": {\"workUnitId\": \"wu1\"}}",
            Tools, out var calls);

        Assert.True(ok);
        var call = Assert.Single(calls);
        Assert.Equal("nm_v1_workunit_get", call.Name);
        Assert.Equal("wu1", call.Input.GetProperty("workUnitId").GetString());
    }

    [Fact]
    public void Recovers_a_fenced_json_tool_call()
    {
        var ok = LlmClient.TryRecoverTextToolCalls(
            "```json\n{\"name\": \"nm_v1_task_update\", \"arguments\": {\"status\": \"InProgress\"}}\n```",
            Tools, out var calls);

        Assert.True(ok);
        Assert.Equal("nm_v1_task_update", Assert.Single(calls).Name);
    }

    [Fact]
    public void Recovers_multiple_objects_in_one_fence()
    {
        var ok = LlmClient.TryRecoverTextToolCalls(
            "```json\n{\"name\": \"nm_v1_task_update\", \"arguments\": {}}\n" +
            "{\"name\": \"nm_v1_workunit_get\", \"arguments\": {}}\n```",
            Tools, out var calls);

        Assert.True(ok);
        Assert.Equal(2, calls.Count);
        Assert.Equal("nm_v1_task_update", calls[0].Name);
        Assert.Equal("nm_v1_workunit_get", calls[1].Name);
    }

    [Fact]
    public void Accepts_stringified_arguments()
    {
        var ok = LlmClient.TryRecoverTextToolCalls(
            "{\"name\": \"nm_v1_workunit_get\", \"arguments\": \"{\\\"workUnitId\\\": \\\"wu2\\\"}\"}",
            Tools, out var calls);

        Assert.True(ok);
        Assert.Equal("wu2", Assert.Single(calls).Input.GetProperty("workUnitId").GetString());
    }

    [Fact]
    public void Defaults_missing_arguments_to_empty_object()
    {
        var ok = LlmClient.TryRecoverTextToolCalls(
            "{\"name\": \"nm_v1_workunit_get\"}", Tools, out var calls);

        Assert.True(ok);
        Assert.Equal(JsonValueKind.Object, Assert.Single(calls).Input.ValueKind);
    }

    // Real captures from qwen2.5-coder:7b-instruct-q6_K (2026-07-15) — the exact strings that
    // dead-lettered under strict parsing. These are the payoff cases the feature exists for.
    [Fact]
    public void Recovers_real_capture_pretty_printed_planner_call()
    {
        const string raw =
            "```json\n{\n  \"name\": \"nm_v1_workunit_get\",\n  \"arguments\": {\n" +
            "    \"workUnitId\": \"a7b5ec70dda54c9f9f051055b5dee26d\"\n  }\n}\n```";

        var ok = LlmClient.TryRecoverTextToolCalls(raw, Tools, out var calls);

        Assert.True(ok);
        Assert.Equal("nm_v1_workunit_get", Assert.Single(calls).Name);
        Assert.Equal("a7b5ec70dda54c9f9f051055b5dee26d", calls[0].Input.GetProperty("workUnitId").GetString());
    }

    [Fact]
    public void Recovers_real_capture_single_line_worker_call()
    {
        const string raw =
            "```json\n{\"name\": \"nm_v1_task_update\", \"arguments\": " +
            "{\"taskId\": null, \"status\": \"InProgress\", \"workUnitId\": \"a7b5ec70dda54c9f9f051055b5dee26d\"}}\n```";

        var ok = LlmClient.TryRecoverTextToolCalls(raw, Tools, out var calls);

        Assert.True(ok);
        Assert.Equal("nm_v1_task_update", Assert.Single(calls).Name);
        Assert.Equal("InProgress", calls[0].Input.GetProperty("status").GetString());
    }

    // ── False-positive guards: these must all be REJECTED (treated as plain text) ──────────────

    [Fact]
    public void Rejects_prose_that_merely_contains_a_tool_call()
    {
        // A worker documenting the API — legitimate content, not a call.
        var ok = LlmClient.TryRecoverTextToolCalls(
            "To fetch a work unit, call {\"name\": \"nm_v1_workunit_get\", \"arguments\": {}} like so.",
            Tools, out var calls);

        Assert.False(ok);
        Assert.Empty(calls);
    }

    [Fact]
    public void Rejects_trailing_prose_after_a_tool_call()
    {
        var ok = LlmClient.TryRecoverTextToolCalls(
            "{\"name\": \"nm_v1_workunit_get\", \"arguments\": {}}\nThis fetches the work unit.",
            Tools, out var calls);

        Assert.False(ok);
    }

    [Fact]
    public void Rejects_an_object_whose_name_is_not_a_registered_tool()
    {
        var ok = LlmClient.TryRecoverTextToolCalls(
            "{\"name\": \"delete_everything\", \"arguments\": {}}", Tools, out var calls);

        Assert.False(ok);
    }

    [Fact]
    public void Rejects_a_batch_if_any_object_name_is_unknown()
    {
        // All-or-nothing: one unknown name poisons the whole recovery so we never half-execute.
        var ok = LlmClient.TryRecoverTextToolCalls(
            "{\"name\": \"nm_v1_task_update\", \"arguments\": {}}\n{\"name\": \"rm_rf\", \"arguments\": {}}",
            Tools, out var calls);

        Assert.False(ok);
    }

    [Fact]
    public void Rejects_a_fenced_markdown_document_that_is_not_json()
    {
        var ok = LlmClient.TryRecoverTextToolCalls(
            "# Title\n\nSome documentation with a `{\"name\": \"nm_v1_workunit_get\"}` example inline.",
            Tools, out var calls);

        Assert.False(ok);
    }

    [Fact]
    public void Rejects_a_json_array_rather_than_bare_objects()
    {
        // Not the shape we recover; an array is likely legitimate content (e.g. a plan.json body).
        var ok = LlmClient.TryRecoverTextToolCalls(
            "[{\"name\": \"nm_v1_workunit_get\", \"arguments\": {}}]", Tools, out var calls);

        Assert.False(ok);
    }

    [Fact]
    public void Rejects_object_with_braces_inside_string_values_that_is_actually_prose()
    {
        // Ensures the brace scanner respects string literals; here a stray trailing char is prose.
        var ok = LlmClient.TryRecoverTextToolCalls(
            "{\"name\": \"nm_v1_workunit_get\", \"arguments\": {\"note\": \"use {curly} braces\"}} done",
            Tools, out var calls);

        Assert.False(ok);
    }
}
