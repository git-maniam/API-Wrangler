using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ApiWrangler.Application;

namespace ApiWrangler.Storage;

public sealed class LocalSecretStore : ISecretStore
{
    private readonly string storageDirectory;
    private readonly string secretsFile;
    private readonly SemaphoreSlim fileGate = new(1, 1);

    public LocalSecretStore(string? baseDirectory = null)
    {
        storageDirectory = baseDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ApiWrangler", "secrets");
        secretsFile = Path.Combine(storageDirectory, "secrets.dat");
    }

    public async Task<string?> GetSecretAsync(string name, CancellationToken token = default)
    {
        await fileGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var map = await ReadSecretsMapAsync(token).ConfigureAwait(false);
            return map.TryGetValue(name, out var val) ? val : null;
        }
        finally
        {
            fileGate.Release();
        }
    }

    public async Task SetSecretAsync(string name, string value, CancellationToken token = default)
    {
        await fileGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var map = await ReadSecretsMapAsync(token).ConfigureAwait(false);
            map[name] = value;
            await WriteSecretsMapAsync(map, token).ConfigureAwait(false);
        }
        finally
        {
            fileGate.Release();
        }
    }

    public async Task<IReadOnlyList<string>> ListSecretNamesAsync(CancellationToken token = default)
    {
        await fileGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var map = await ReadSecretsMapAsync(token).ConfigureAwait(false);
            return map.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToList();
        }
        finally
        {
            fileGate.Release();
        }
    }

    private async Task<Dictionary<string, string>> ReadSecretsMapAsync(CancellationToken token)
    {
        if (!File.Exists(secretsFile)) return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var rawBytes = await File.ReadAllBytesAsync(secretsFile, token).ConfigureAwait(false);
        if (rawBytes.Length == 0) return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        byte[] plainBytes;
        if (OperatingSystem.IsWindows())
        {
            try { plainBytes = DpapiDecrypt(rawBytes); }
            catch { return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); }
        }
        else
        {
            plainBytes = rawBytes;
        }

        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string>>(plainBytes)
                   ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private async Task WriteSecretsMapAsync(Dictionary<string, string> map, CancellationToken token)
    {
        Directory.CreateDirectory(storageDirectory);
        var plainBytes = JsonSerializer.SerializeToUtf8Bytes(map);
        byte[] payload = OperatingSystem.IsWindows() ? DpapiEncrypt(plainBytes) : plainBytes;

        var temp = Path.Combine(storageDirectory, "secrets.tmp." + Guid.NewGuid().ToString("N"));
        await File.WriteAllBytesAsync(temp, payload, token).ConfigureAwait(false);
        File.Move(temp, secretsFile, overwrite: true);
    }

    #region Windows DPAPI P/Invoke

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DataBlob
    {
        public int CbData;
        public IntPtr PbData;
    }

    [DllImport("Crypt32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern bool CryptProtectData(
        ref DataBlob pDataIn,
        string szDataDescr,
        ref DataBlob pOptionalEntropy,
        IntPtr pvReserved,
        IntPtr pPromptStruct,
        int dwFlags,
        ref DataBlob pDataOut);

    [DllImport("Crypt32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern bool CryptUnprotectData(
        ref DataBlob pDataIn,
        IntPtr ppszDataDescr,
        ref DataBlob pOptionalEntropy,
        IntPtr pvReserved,
        IntPtr pPromptStruct,
        int dwFlags,
        ref DataBlob pDataOut);

    [DllImport("Kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr hMem);

    private static byte[] DpapiEncrypt(byte[] data)
    {
        var inBlob = new DataBlob();
        var outBlob = new DataBlob();
        var emptyBlob = new DataBlob();
        var handle = GCHandle.Alloc(data, GCHandleType.Pinned);
        try
        {
            inBlob.PbData = handle.AddrOfPinnedObject();
            inBlob.CbData = data.Length;
            if (!CryptProtectData(ref inBlob, "ApiWrangler.Secret", ref emptyBlob, IntPtr.Zero, IntPtr.Zero, 0, ref outBlob))
                throw new InvalidOperationException("DPAPI encryption failed.");

            var result = new byte[outBlob.CbData];
            Marshal.Copy(outBlob.PbData, result, 0, outBlob.CbData);
            return result;
        }
        finally
        {
            handle.Free();
            if (outBlob.PbData != IntPtr.Zero) LocalFree(outBlob.PbData);
        }
    }

    private static byte[] DpapiDecrypt(byte[] cipher)
    {
        var inBlob = new DataBlob();
        var outBlob = new DataBlob();
        var emptyBlob = new DataBlob();
        var handle = GCHandle.Alloc(cipher, GCHandleType.Pinned);
        try
        {
            inBlob.PbData = handle.AddrOfPinnedObject();
            inBlob.CbData = cipher.Length;
            if (!CryptUnprotectData(ref inBlob, IntPtr.Zero, ref emptyBlob, IntPtr.Zero, IntPtr.Zero, 0, ref outBlob))
                throw new InvalidOperationException("DPAPI decryption failed.");

            var result = new byte[outBlob.CbData];
            Marshal.Copy(outBlob.PbData, result, 0, outBlob.CbData);
            return result;
        }
        finally
        {
            handle.Free();
            if (outBlob.PbData != IntPtr.Zero) LocalFree(outBlob.PbData);
        }
    }

    #endregion
}

