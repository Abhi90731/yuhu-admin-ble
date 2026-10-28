using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;

namespace YuhuAdminBle;

/// <summary>
/// Admin identity (A.3): the laptop's Ed25519 keypair + its root-signed cert.
///
/// Key storage:
///   - The admin PRIVATE key is stored encrypted at rest using Windows DPAPI
///     (ProtectedData, CurrentUser scope), so it is bound to this Windows user
///     account and never sits on disk in plaintext.
///   - On non-Windows (dev/test on mac), falls back to a plain file with a loud
///     warning. Production is Windows-only per the project decision.
///
/// The admin CERTIFICATE (root-signed) is public and stored as plain JSON.
/// </summary>
public sealed class AdminIdentity
{
    private readonly byte[] _privateSeed; // 32-byte Ed25519 seed
    public byte[] PublicKey { get; }       // 32-byte Ed25519 public
    public JsonDocument Cert { get; }      // root-signed admin cert
    public string CertJson { get; }

    private AdminIdentity(byte[] privateSeed, byte[] publicKey, string certJson)
    {
        _privateSeed = privateSeed;
        PublicKey = publicKey;
        CertJson = certJson;
        Cert = JsonDocument.Parse(certJson);
    }

    /// <summary>
    /// Load the admin identity from a directory containing:
    ///   admin-key.dpapi   (DPAPI-encrypted 32-byte seed; created on first run)
    ///   admin-cert.json   (root-signed cert — copied in by the operator)
    /// If the key file is absent, a new keypair is generated and persisted.
    /// </summary>
    public static AdminIdentity Load(string dir)
    {
        Directory.CreateDirectory(dir);
        var keyPath = Path.Combine(dir, "admin-key.dpapi");
        var certPath = Path.Combine(dir, "admin-cert.json");

        byte[] seed;
        if (File.Exists(keyPath))
        {
            seed = Unprotect(File.ReadAllBytes(keyPath));
        }
        else
        {
            seed = RandomNumberGenerator.GetBytes(32);
            File.WriteAllBytes(keyPath, Protect(seed));
            Console.WriteLine($"[identity] generated new admin key -> {keyPath}");
        }

        var pub = PublicFromSeed(seed);
        if (!File.Exists(certPath))
        {
            Console.WriteLine();
            Console.WriteLine("  ADMIN PUBLIC KEY (give this to the offline minting tool):");
            Console.WriteLine("    " + Convert.ToBase64String(pub));
            Console.WriteLine($"  Then place the signed cert at: {certPath}");
            Console.WriteLine();
            throw new FileNotFoundException(
                "admin-cert.json not found. Mint it offline with tools/mint-admin-cert.js " +
                "using the admin public key printed above, then re-run.");
        }

        var certJson = File.ReadAllText(certPath);
        return new AdminIdentity(seed, pub, certJson);
    }

    /// <summary>Ed25519-sign a message with the admin private key (64-byte sig).</summary>
    public byte[] Sign(byte[] message)
    {
        var priv = new Ed25519PrivateKeyParameters(_privateSeed, 0);
        var signer = new Ed25519Signer();
        signer.Init(true, priv);
        signer.BlockUpdate(message, 0, message.Length);
        return signer.GenerateSignature();
    }

    private static byte[] PublicFromSeed(byte[] seed)
    {
        var priv = new Ed25519PrivateKeyParameters(seed, 0);
        return priv.GeneratePublicKey().GetEncoded();
    }

    // ---- DPAPI (Windows) with a plaintext fallback elsewhere ----
    private static byte[] Protect(byte[] data)
    {
        if (OperatingSystem.IsWindows())
            return ProtectedData.Protect(data, null, DataProtectionScope.CurrentUser);
        Console.WriteLine("[identity] WARNING: not on Windows — admin key stored WITHOUT DPAPI (dev only).");
        return data;
    }

    private static byte[] Unprotect(byte[] blob)
    {
        if (OperatingSystem.IsWindows())
            return ProtectedData.Unprotect(blob, null, DataProtectionScope.CurrentUser);
        return blob;
    }
}
