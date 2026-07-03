# Task: rename CustomerId to BuyerId

Repo: `eval-stub-small`
Type: Cross-cutting refactor (M)

## Goal (as given to the agent)

Rename the `CustomerId` concept to `BuyerId` throughout the codebase — the domain
property, the create-order request field, and any other references — so the public API
request/response shape uses `buyerId` instead of `customerId` consistently. This is a
pure rename: no behavior change.

## Grading

`hidden-tests/BuyerIdRenameTests.cs` copied into `tests/MiniLedger.Tests/` before running
`dotnet test`. Note: this file references `CreateOrderRequest(BuyerId: ...)` and
`order.BuyerId` by name — it will fail to *compile* against an incomplete rename, which
is an intended and correct failure mode here (a partial rename should fail grading, not
just fail an assertion).
