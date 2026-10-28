using System.Text;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;
using Xunit;

namespace YuhuAdminBle.Tests;

/// <summary>
/// Cross-platform interop: proves the C# admin signing (BouncyCastle Ed25519)
/// produces EXACTLY the proof that the Kotlin AdminService/AdminTrust already
/// verified (same golden fixtures used in the Android AdminTrustTest).
///
/// This closes the loop without a phone: if the C# sign and the Kotlin verify
/// agree on these vectors, the laptop ↔ phone admin handshake will interoperate.
/// </summary>
public class AdminInteropTests
{
    // Same fixtures as the Android tests (minted/offline-generated).
    private const string AdminPrivB64 = "mNqcn/wWDV8dZjVRCUlp1L9Shgmp2fwccMjuwSf3cFE=";
    private const string AdminPubB64 = "sYJ77/s9x0JBfIhrjnQIJQg0HvVS7aEdzDU7Z0Mf2p0=";
    // Golden proof that Kotlin verifyProof() accepted:
    private const string PhoneId = "46b3a5aa11223344";
    private const string NonceHex = "000102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f";
    private const string ExpectedProofB64 =
        "9MbVudVsgu1ku6TiyZgbL1cY5siTQzIFOSoJzMY+dhA3u9t4BmldTLtrE48YMO9L+wOuvWQJItS93atihSroBQ==";

    private static byte[] Sign(byte[] seed, byte[] message)
    {
        var priv = new Ed25519PrivateKeyParameters(seed, 0);
        var signer = new Ed25519Signer();
        signer.Init(true, priv);
        signer.BlockUpdate(message, 0, message.Length);
        return signer.GenerateSignature();
    }

    private static byte[] Hex(string s)
    {
        var b = new byte[s.Length / 2];
        for (int i = 0; i < b.Length; i++)
            b[i] = Convert.ToByte(s.Substring(i * 2, 2), 16);
        return b;
    }

    [Fact]
    public void CSharpSign_Matches_GoldenProof_VerifiedByKotlin()
    {
        var seed = Convert.FromBase64String(AdminPrivB64);
        var nonce = Hex(NonceHex);
        var message = nonce.Concat(Encoding.UTF8.GetBytes(PhoneId)).ToArray();

        var sig = Sign(seed, message);

        // Ed25519 is deterministic (RFC 8032) -> must equal the exact golden proof.
        Assert.Equal(ExpectedProofB64, Convert.ToBase64String(sig));
    }

    [Fact]
    public void DerivedPublicKey_Matches_Fixture()
    {
        var seed = Convert.FromBase64String(AdminPrivB64);
        var priv = new Ed25519PrivateKeyParameters(seed, 0);
        var pub = priv.GeneratePublicKey().GetEncoded();
        Assert.Equal(AdminPubB64, Convert.ToBase64String(pub));
    }

    [Fact]
    public void LogicalIdDerivation_Matches_Kotlin()
    {
        // Real identity captured from a physical phone during the A.3.0 spike.
        // Kotlin NearbyConnectionPolicy.idFromAdvertisement yields this same id.
        const string adv = "muudKuRBat6A8RCXcOJjJwfOHpWog9eZAme2nvEpeFQ=|XIOMI";
        var id = Protocol.LogicalIdFromAdvertisement(adv);
        Assert.Equal("1dc2a5c107fc6e0b", id);
    }

    [Fact]
    public void LogicalIdDerivation_RejectsMalformed()
    {
        Assert.Null(Protocol.LogicalIdFromAdvertisement(""));
        Assert.Null(Protocol.LogicalIdFromAdvertisement("not-base64!!|x"));
    }
}
