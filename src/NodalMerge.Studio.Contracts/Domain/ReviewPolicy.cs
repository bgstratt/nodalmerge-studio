namespace NodalMerge.Studio.Contracts.Domain;

public enum ReviewPolicy
{
    HumanRequired,  // default — proposal waits for human apply
    AgentApproval,  // reviewer agent runs; merge proceeds automatically on approval
    Hybrid,         // agent approves; human can override for N minutes; then auto-merge
}
