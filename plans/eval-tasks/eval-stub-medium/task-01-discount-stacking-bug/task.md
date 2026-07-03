# Task: multiple discount codes stack instead of taking the best one

Repo: `eval-stub-medium`
Type: Localized bug fix (S) — logic bug, not a typo; requires reading intent from the
existing doc comment/tests, not just spotting an obviously wrong literal.

## Goal (as given to the agent)

Policy is supposed to be "best single discount wins" when a customer applies more than
one discount code to an order — take the maximum applicable percent-off, not a stack of
all of them. Right now `DiscountService.ApplyDiscounts` sums every applicable rule's
percentage instead, so two 20%+ codes together can discount far more than either code
alone. Fix it so only the single best applicable discount applies.

## Grading

`hidden-tests/MiniLedger.Core.Tests/DiscountStackingFixTests.cs` copied into
`tests/MiniLedger.Core.Tests/` before running `dotnet test`.
