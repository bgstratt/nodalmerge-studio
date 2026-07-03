# Task: prioritize fulfillment by customer tier

Repo: `eval-stub-medium`
Type: Underspecified requirement (M)

## Goal (as given to the agent)

Support flagged that Vip and Gold customers expect faster fulfillment, but today
`FulfillmentCoordinator`/`FulfillmentWorker` process every order strictly
first-in-first-out with no notion of customer tier at all.

Extend `IFulfillmentService.Enqueue` to accept a tier hint:

```csharp
FulfillmentTask Enqueue(string orderId, CustomerTier customerTier)
```

`ListPending` should return tasks ordered with higher-tier customers first (`Vip`, then
`Gold`, then `Silver`, then `Standard`), ties within the same tier broken by creation
order. `FulfillmentTask` doesn't currently record a tier — you'll need to add that field
since the task itself doesn't otherwise know the customer's tier. Update
`FulfillmentWorker`'s call site to look up and pass the order's customer's tier.

The tier-ordering *contract* above is fixed so grading stays objective. What isn't
specified — and is a real judgment call given this app's scale — is how much further to
take it: is a simple sort enough, or does fairness/starvation for Standard-tier orders
under sustained load matter here? Note your reasoning briefly; you are not required to
solve starvation, just not to make it worse than the current FIFO baseline.

## Grading

`hidden-tests/MiniLedger.Core.Tests/FulfillmentPriorityTests.cs` copied into
`tests/MiniLedger.Core.Tests/` before running `dotnet test`.
