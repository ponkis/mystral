using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Mystral.Configuration;

namespace Mystral.Services;

/// <summary>
/// File-backed credential storage protected for the current Windows user with
/// DPAPI. Development and production builds naturally use separate stores via
/// <see cref="AppMetadata.LocalApplicationDataDirectory"/>.
/// </summary>
public sealed class DpapiCredentialStore : ISecureCredentialStore
{
    private const int CryptProtectUiForbidden = 0x1;
    private readonly string _directory;

    public DpapiCredentialStore()
        : this(Path.Combine(AppMetadata.LocalApplicationDataDirectory, "credentials"))
    {
    }

    internal DpapiCredentialStore(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        _directory = Path.GetFullPath(directory);
    }

    public string? Read(string key)
    {
        ValidateKey(key);
        var path = GetCredentialPath(key);
        if (!File.Exists(path))
        {
            return null;
        }

        var protectedBytes = File.ReadAllBytes(path);
        if (protectedBytes.Length == 0)
        {
            throw new InvalidDataException("The protected credential is empty.");
        }

        var clearBytes = Unprotect(protectedBytes, CreateEntropy(key));
        try
        {
            return Encoding.UTF8.GetString(clearBytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(clearBytes);
            CryptographicOperations.ZeroMemory(protectedBytes);
        }
    }

    public void Write(string key, string value)
    {
        ValidateKey(key);
        ArgumentNullException.ThrowIfNull(value);

        var clearBytes = Encoding.UTF8.GetBytes(value);
        byte[]? protectedBytes = null;
        try
        {
            protectedBytes = Protect(clearBytes, CreateEntropy(key));
            Directory.CreateDirectory(_directory);
            var path = GetCredentialPath(key);
            var temporaryPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllBytes(temporaryPath, protectedBytes);
                File.Move(temporaryPath, path, overwrite: true);
            }
            finally
            {
                try
                {
                    if (File.Exists(temporaryPath))
                    {
                        File.Delete(temporaryPath);
                    }
                }
                catch
                {
                }
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(clearBytes);
            if (protectedBytes is not null)
            {
                CryptographicOperations.ZeroMemory(protectedBytes);
            }
        }
    }

    public void Delete(string key)
    {
        ValidateKey(key);
        var path = GetCredentialPath(key);
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private string GetCredentialPath(string key)
    {
        var keyBytes = Encoding.UTF8.GetBytes(key);
        try
        {
            return Path.Combine(_directory, Convert.ToHexString(SHA256.HashData(keyBytes)) + ".credential");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(keyBytes);
        }
    }

    private static byte[] CreateEntropy(string key)
    {
        return Encoding.UTF8.GetBytes($"{AppMetadata.Name}|{AppMetadata.EnvironmentName}|{key}|v1");
    }

    private static void ValidateKey(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
    }

    private static byte[] Protect(byte[] clearBytes, byte[] entropy)
    {
        EnsureWindows();
        using var input = NativeBlob.From(clearBytes);
        using var optionalEntropy = NativeBlob.From(entropy);
        if (!CryptProtectData(
                ref input.Value,
                null,
                ref optionalEntropy.Value,
                IntPtr.Zero,
                IntPtr.Zero,
                CryptProtectUiForbidden,
                out var output))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows could not protect the credential.");
        }

        return CopyAndFree(output);
    }

    private static byte[] Unprotect(byte[] protectedBytes, byte[] entropy)
    {
        EnsureWindows();
        using var input = NativeBlob.From(protectedBytes);
        using var optionalEntropy = NativeBlob.From(entropy);
        if (!CryptUnprotectData(
                ref input.Value,
                IntPtr.Zero,
                ref optionalEntropy.Value,
                IntPtr.Zero,
                IntPtr.Zero,
                CryptProtectUiForbidden,
                out var output))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows could not decrypt the credential.");
        }

        return CopyAndFree(output);
    }

    private static byte[] CopyAndFree(DataBlob blob)
    {
        try
        {
            if (blob.Length <= 0 || blob.Data == IntPtr.Zero)
            {
                return [];
            }

            var result = new byte[blob.Length];
            Marshal.Copy(blob.Data, result, 0, result.Length);
            return result;
        }
        finally
        {
            if (blob.Data != IntPtr.Zero)
            {
                if (blob.Length > 0)
                {
                    Marshal.Copy(new byte[blob.Length], 0, blob.Data, blob.Length);
                }
                _ = LocalFree(blob.Data);
            }
        }
    }

    private static void EnsureWindows()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Windows DPAPI credential storage is only available on Windows.");
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int Length;
        public IntPtr Data;
    }

    private sealed class NativeBlob : IDisposable
    {
        private readonly int _length;

        private NativeBlob(byte[] bytes)
        {
            _length = bytes.Length;
            Value = new DataBlob
            {
                Length = bytes.Length,
                Data = bytes.Length == 0 ? IntPtr.Zero : Marshal.AllocHGlobal(bytes.Length)
            };
            if (bytes.Length > 0)
            {
                Marshal.Copy(bytes, 0, Value.Data, bytes.Length);
            }
        }

        public DataBlob Value;

        public static NativeBlob From(byte[] bytes) => new(bytes);

        public void Dispose()
        {
            if (Value.Data == IntPtr.Zero)
            {
                return;
            }

            var zeroes = new byte[_length];
            Marshal.Copy(zeroes, 0, Value.Data, zeroes.Length);
            Marshal.FreeHGlobal(Value.Data);
            Value = default;
        }
    }

    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(
        ref DataBlob dataIn,
        string? description,
        ref DataBlob optionalEntropy,
        IntPtr reserved,
        IntPtr prompt,
        int flags,
        out DataBlob dataOut);

    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(
        ref DataBlob dataIn,
        IntPtr description,
        ref DataBlob optionalEntropy,
        IntPtr reserved,
        IntPtr prompt,
        int flags,
        out DataBlob dataOut);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr LocalFree(IntPtr memory);
}
