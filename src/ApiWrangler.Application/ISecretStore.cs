namespace ApiWrangler.Application;

public interface ISecretStore
{
    Task<string?> GetSecretAsync(string name, CancellationToken token = default);
    Task SetSecretAsync(string name, string value, CancellationToken token = default);
    Task<IReadOnlyList<string>> ListSecretNamesAsync(CancellationToken token = default);
}

