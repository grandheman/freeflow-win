using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace FreeFlow.App.Platform.Host;

/// <summary>
/// Stores API keys encrypted to the current Windows user account.
/// </summary>
/// <remarks>
/// <para>
/// Windows replacement for <c>Sources/KeychainStorage.swift</c>. DPAPI with
/// <see cref="DataProtectionScope.CurrentUser"/> ties the ciphertext to the logged-in
/// user, so another account on the same machine cannot read it, and a copied file is
/// useless elsewhere. That is the same protection level the macOS Keychain gives here.
/// </para>
/// <para>
/// This is not protection against malware already running as the user. Nothing
/// available to a user-level desktop app is, and pretending otherwise would be worse
/// than stating the limit plainly.
/// </para>
/// </remarks>
public sealed class DpapiSecretStore
{
    /// <summary>Bound into the encryption so ciphertext from another app cannot be swapped in.</summary>
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("FreeFlow.Windows.Credentials.v1");

    private readonly string _filePath;
    private readonly object _gate = new();
    private Dictionary<string, string> _secrets;

    public DpapiSecretStore(string? filePath = null)
    {
        _filePath = filePath ?? AppPaths.CredentialsFile;
        _secrets = Load();
    }

    public string? Get(string key)
    {
        lock (_gate) return _secrets.TryGetValue(key, out var value) ? value : null;
    }

    public void Set(string key, string value)
    {
        lock (_gate)
        {
            if (string.IsNullOrEmpty(value)) _secrets.Remove(key);
            else _secrets[key] = value;
            Save();
        }
    }

    public void Remove(string key)
    {
        lock (_gate)
        {
            if (_secrets.Remove(key)) Save();
        }
    }

    private Dictionary<string, string> Load()
    {
        try
        {
            if (!File.Exists(_filePath)) return new Dictionary<string, string>(StringComparer.Ordinal);

            var protectedBytes = File.ReadAllBytes(_filePath);
            var plainBytes = ProtectedData.Unprotect(protectedBytes, Entropy, DataProtectionScope.CurrentUser);
            var json = Encoding.UTF8.GetString(plainBytes);

            return JsonSerializer.Deserialize<Dictionary<string, string>>(json)
                ?? new Dictionary<string, string>(StringComparer.Ordinal);
        }
        catch (CryptographicException)
        {
            // Written by a different user account, or the profile's master key
            // changed. The key is unrecoverable, so start clean and let the user
            // re-enter it rather than failing to launch.
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }
        catch (Exception)
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }
    }

    private void Save()
    {
        try
        {
            AppPaths.EnsureCreated();

            var json = JsonSerializer.Serialize(_secrets);
            var plainBytes = Encoding.UTF8.GetBytes(json);
            var protectedBytes = ProtectedData.Protect(plainBytes, Entropy, DataProtectionScope.CurrentUser);

            var temporaryPath = _filePath + ".tmp";
            File.WriteAllBytes(temporaryPath, protectedBytes);

            if (File.Exists(_filePath)) File.Replace(temporaryPath, _filePath, null);
            else File.Move(temporaryPath, _filePath);

            // Clear the plaintext buffer promptly; it is no defense against a memory
            // dump but it shortens the window.
            Array.Clear(plainBytes);
        }
        catch (Exception)
        {
            // A failed credential write surfaces as an empty key on next launch,
            // which the setup flow already handles.
        }
    }
}
