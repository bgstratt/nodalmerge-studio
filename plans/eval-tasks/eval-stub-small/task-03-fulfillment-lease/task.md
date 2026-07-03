# Task: prevent double-fulfillment under concurrency

Repo: `eval-stub-small`
Type: Underspecified requirement (M)

## Goal (as given to the agent)

We're planning to run multiple instances of this API for redundancy. QA flagged a
scenario: with two instances both polling for `Placed` orders, the same order could get
picked up and fulfilled twice (any future side effect like a shipping-label API call
would fire twice).

Add a method to `OrderService`:

```csharp
bool TryClaimForFulfillment(string orderId)
```

It should atomically transition a `Placed` order to `Fulfilling` and return `true`, or
return `false` (leaving state unchanged) if the order isn't currently `Placed` — whether
because it's still `Draft`, or because another caller already claimed it. Under
concurrent callers racing for the same order, exactly one must receive `true`.

How you implement the safeguard (a lock, compare-and-swap, something else) is your
call — it's a genuine judgment call given this app's scale, not a single obviously
correct answer. Leave a short note on the reasoning.

## Grading

`hidden-tests/FulfillmentClaimTests.cs` copied into `tests/MiniLedger.Tests/` before
running `dotnet test`. Note this task specifies a concrete method contract (unlike a
fully open-ended version of this task) precisely so grading stays objective — the
*locking strategy* is the actual open judgment call, not the method shape.
