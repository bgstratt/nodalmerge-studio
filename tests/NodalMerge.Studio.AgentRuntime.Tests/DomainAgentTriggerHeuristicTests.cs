using NodalMerge.Studio.AgentRuntime;

namespace NodalMerge.Studio.AgentRuntime.Tests;

public class DomainAgentTriggerHeuristicTests
{
    [Theory]
    [InlineData("Add JWT validation", null, true)]
    [InlineData(null, "Stores the session token in a secure cookie", true)]
    [InlineData("AUTH refactor", null, true)]
    [InlineData("Add caching layer", "Speeds up repeated reads via an in-memory dictionary", false)]
    [InlineData(null, null, false)]
    public void IsRelevant_matches_Security_keywords_in_title_or_body_case_insensitively(
        string? title, string? body, bool expected)
    {
        Assert.Equal(expected, DomainAgentTriggerHeuristic.IsRelevant(DomainAgentRegistry.Security, title, body));
    }

    [Theory]
    [InlineData("Switch to microservice deployment", null, true)]
    [InlineData(null, "Introduces a new module boundary between billing and auth", true)]
    [InlineData("MONOLITH split plan", null, true)]
    [InlineData("Add JWT validation", "OAuth token refresh", false)]
    [InlineData(null, null, false)]
    public void IsRelevant_matches_Architecture_keywords_in_title_or_body_case_insensitively(
        string? title, string? body, bool expected)
    {
        Assert.Equal(expected, DomainAgentTriggerHeuristic.IsRelevant(DomainAgentRegistry.Architecture, title, body));
    }
}
