# Task: discount code removes too much

Repo: `eval-stub-small`
Type: Localized bug fix (S)

## Goal (as given to the agent)

Customers report that applying a discount code removes way more than the advertised
percentage — a `WELCOME10` code marketed as "10% off" is emptying the order down to
almost nothing. Investigate `DiscountRules` and fix the calculation so each code takes
off exactly its advertised percentage (e.g. `WELCOME10` on a $100 subtotal should leave
$90, not near-$0).

## Grading

`hidden-tests/DiscountRulesFixTests.cs` copied into `tests/MiniLedger.Tests/` before
running `dotnet test`.
