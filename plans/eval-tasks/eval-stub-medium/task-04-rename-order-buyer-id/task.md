# Task: rename Order's buyer-reference field to BuyerId

Repo: `eval-stub-medium`
Type: Cross-cutting refactor (M) — spans Contracts, Core, and Host, plus a subtlety that
rewards actually reading the code rather than a blind find-and-replace.

## Goal (as given to the agent)

Rename the buyer-reference field on `Order` — `Order.CustomerId` and the corresponding
`CreateOrderRequest.CustomerId` in the Host endpoints — to `BuyerId`. Update every
reference across `Contracts`, `Core`, and `Host`.

**Do not** rename `Customer.CustomerId` — that's the customer's own primary key, a
separate and unrelated field that happens to share the old name. Only `Order`'s foreign
key reference to a buyer is being renamed here.

## Grading

Two hidden-test files, copied in before running `dotnet test`:
- `hidden-tests/MiniLedger.Core.Tests/BuyerIdRenameTests.cs` → `tests/MiniLedger.Core.Tests/`
- `hidden-tests/MiniLedger.Host.Tests/CreateOrderRequestRenameTests.cs` → `tests/MiniLedger.Host.Tests/`

Both reference the renamed members by name (`order.BuyerId`, `CreateOrderRequest(BuyerId:
...)`), so an incomplete rename fails to *compile*, not just fails an assertion — an
intended and correct failure mode. `BuyerIdRenameTests` also asserts `Customer.CustomerId`
is untouched, as a regression guard against an over-broad rename.
