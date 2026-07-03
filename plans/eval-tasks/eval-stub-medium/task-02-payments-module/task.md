# Task: add a Payments module

Repo: `eval-stub-medium`
Type: Multi-file feature add (L) — spans all four layers, the biggest task in this set.

## Goal (as given to the agent)

Add a Payments module, following this repo's existing conventions (contracts in
`MiniLedger.Contracts.<Module>`, service/repository interfaces in
`MiniLedger.Core.Abstractions`, implementation in `MiniLedger.Core.<Module>`, in-memory
storage in `MiniLedger.Storage.InMemory`):

- `MiniLedger.Contracts.Payments.PaymentStatus` — enum: `Pending`, `Captured`, `Failed`.
- `MiniLedger.Contracts.Payments.PaymentRecord` — record with at least `PaymentId`,
  `OrderId`, `Amount`, `Status`.
- `MiniLedger.Core.Abstractions.IPaymentService` with:
  - `PaymentRecord Capture(string orderId, decimal amount)`
  - `PaymentRecord? Get(string orderId)`
- `MiniLedger.Core.Abstractions.IPaymentRepository`, backed by an in-memory
  implementation (`MiniLedger.Core.Payments.PaymentService` for the service,
  `MiniLedger.Storage.InMemory.InMemoryPaymentRepository` for storage).
- Wire both into `Program.cs` DI, and add a `POST /payments/capture` endpoint.

Then use it: `OrderService.Place` should require a successful payment capture first.
`OrderService` will need `IPaymentService` added as a constructor dependency — capture
the order's current total (via the existing discount/total logic) and only transition
the order to `Placed` if the capture succeeds (`PaymentStatus.Captured`); otherwise leave
the order as-is and return `null`, same as the other rejection paths already there.

## Grading

`hidden-tests/MiniLedger.Core.Tests/PaymentCaptureTests.cs` copied into
`tests/MiniLedger.Core.Tests/` before running `dotnet test`. Note this task specifies
exact type/method names and namespaces (unlike a fully open-ended version) precisely so
grading stays objective — the interesting judgment call here is module boundaries and
wiring, not naming.
