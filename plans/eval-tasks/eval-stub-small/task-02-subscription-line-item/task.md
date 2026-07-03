# Task: add a Subscription line-item kind

Repo: `eval-stub-small`
Type: Multi-file feature add (M)

## Goal (as given to the agent)

Add a new `Subscription` line-item kind to the order model, alongside the existing
`Physical` and `Digital` kinds. A subscription line item represents one recurring seat,
so it comes with a business rule: a `Subscription` line item must always have
`Quantity == 1` — attempting to add one with any other quantity should be rejected
(`AddLineItem` returns `null` and the order is left unchanged), the same way an
already-placed order already rejects new line items.

## Grading

`hidden-tests/SubscriptionLineItemTests.cs` copied into `tests/MiniLedger.Tests/` before
running `dotnet test`.
