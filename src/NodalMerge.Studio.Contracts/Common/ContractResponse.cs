namespace NodalMerge.Studio.Contracts.Common;

public sealed record ContractError(
    string Code,
    string Message,
    string? Tool = null);

public sealed record ContractEnvelope<T>(
    string ContractVersion,
    T? Data,
    ContractError? Error)
{
    public static ContractEnvelope<T> Ok(T data, string contractVersion) =>
        new(contractVersion, data, null);

    public static ContractEnvelope<T> Fail(string code, string message, string contractVersion, string? tool = null) =>
        new(contractVersion, default, new ContractError(code, message, tool));
}
