using System.Security.Cryptography;

namespace PdfKnead.Core.Platform.Pkcs11;

/// <summary>A logged-in session on a token with one private key: signs with the card, the key never leaves it.</summary>
internal sealed class TokenSession(Pkcs11Module module, ulong session, ulong key, bool alwaysAuthenticate, string? pin) : IDisposable
{
    private string? _pin = pin;
    private bool _closed;

    /// <summary>Signs data with the key (see <see cref="Pkcs11Module.Sign"/>).</summary>
    public byte[] Sign(ulong mechanism, byte[] data, Pkcs11Module.PssParameters? pss = null)
    {
        ObjectDisposedException.ThrowIf(_closed, this);
        try
        {
            return module.Sign(session, key, mechanism, data, alwaysAuthenticate, _pin, pss);
        }
        catch (Pkcs11Exception e) when (e.Code == Pkcs11Module.CkrMechanismInvalid)
        {
            throw new MechanismUnavailableException(e.Message, e);
        }
        catch (Pkcs11Exception e)
        {
            throw new CryptographicException(e.Message, e);
        }
    }

    public void Dispose()
    {
        if (_closed) return;
        _closed = true;
        _pin = null;
        module.Logout(session);
        module.CloseSession(session);
    }
}

/// <summary>
/// An RSA key on a token for <see cref="System.Security.Cryptography.Pkcs.SignedCms"/> and the own CMS code: the public part from the
/// certificate, signing (PKCS #1 v1.5) on the card with CKM_RSA_PKCS over the DigestInfo of the hash (RFC 8017, 9.2).
/// </summary>
/// <summary>The card does not offer the mechanism (CKR_MECHANISM_INVALID): another one may be tried.</summary>
internal sealed class MechanismUnavailableException(string message, Exception inner) : CryptographicException(message, inner);

internal sealed class TokenRsa : RSA, IRemoteKey
{
    private readonly TokenSession _session;
    private readonly RSA _public;

    public TokenRsa(TokenSession session, RSA publicKey)
    {
        _session = session;
        _public = publicKey;
        KeySizeValue = publicKey.KeySize;
        LegalKeySizesValue = [new KeySizes(publicKey.KeySize, publicKey.KeySize, 0)];
    }

    public override RSAParameters ExportParameters(bool includePrivateParameters) =>
        includePrivateParameters ? throw new CryptographicException(Tr.T("Der Schlüssel bleibt auf der Karte.")) : _public.ExportParameters(false);

    public override void ImportParameters(RSAParameters parameters) => throw new NotSupportedException();

    public override byte[] SignHash(byte[] hash, HashAlgorithmName hashAlgorithm, RSASignaturePadding padding)
    {
        // RSASSA-PSS (CKM_RSA_PKCS_PSS over the finished hash): MGF1 with the same hash, salt as long as the hash (PKCS #11 3.1, 2.1.11;
        // docs/FEHLERGEDAECHTNIS.md, No. 537)
        if (padding == RSASignaturePadding.Pss)
            return _session.Sign(Pkcs11Module.CkmRsaPkcsPss, hash, Pkcs11Module.PssParameters.For(hashAlgorithm));
        if (padding != RSASignaturePadding.Pkcs1) throw new CryptographicException(Tr.T("Dieses Signaturverfahren unterstützt die Karte hier nicht."));
        return _session.Sign(Pkcs11Module.CkmRsaPkcs, [.. DigestInfo(hashAlgorithm), .. hash]);
    }

    public override bool VerifyHash(byte[] hash, byte[] signature, HashAlgorithmName hashAlgorithm, RSASignaturePadding padding) =>
        _public.VerifyHash(hash, signature, hashAlgorithm, padding);

    /// <summary>The DER prefix of DigestInfo { AlgorithmIdentifier, OCTET STRING } for a hash (RFC 8017, 9.2, note 1).</summary>
    private static byte[] DigestInfo(HashAlgorithmName hash)
    {
        if (hash == HashAlgorithmName.SHA256) return [0x30, 0x31, 0x30, 0x0d, 0x06, 0x09, 0x60, 0x86, 0x48, 0x01, 0x65, 0x03, 0x04, 0x02, 0x01, 0x05, 0x00, 0x04, 0x20];
        if (hash == HashAlgorithmName.SHA384) return [0x30, 0x41, 0x30, 0x0d, 0x06, 0x09, 0x60, 0x86, 0x48, 0x01, 0x65, 0x03, 0x04, 0x02, 0x02, 0x05, 0x00, 0x04, 0x30];
        if (hash == HashAlgorithmName.SHA512) return [0x30, 0x51, 0x30, 0x0d, 0x06, 0x09, 0x60, 0x86, 0x48, 0x01, 0x65, 0x03, 0x04, 0x02, 0x03, 0x05, 0x00, 0x04, 0x40];
        throw new CryptographicException(Tr.T("Dieses Signaturverfahren unterstützt die Karte hier nicht."));
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _session.Dispose();
            _public.Dispose();
        }
        base.Dispose(disposing);
    }
}

/// <summary>An ECDSA key on a token: signing on the card with CKM_ECDSA over the hash (the card returns r‖s, as <see cref="ECDsa.SignHash(byte[])"/> does).</summary>
internal sealed class TokenEcdsa : ECDsa, IRemoteKey
{
    private readonly TokenSession _session;
    private readonly ECDsa _public;

    public TokenEcdsa(TokenSession session, ECDsa publicKey)
    {
        _session = session;
        _public = publicKey;
        KeySizeValue = publicKey.KeySize;
        LegalKeySizesValue = [new KeySizes(publicKey.KeySize, publicKey.KeySize, 0)];
    }

    public override byte[] SignHash(byte[] hash) => _session.Sign(Pkcs11Module.CkmEcdsa, hash);

    public override bool VerifyHash(byte[] hash, byte[] signature) => _public.VerifyHash(hash, signature);

    public override ECParameters ExportParameters(bool includePrivateParameters) =>
        includePrivateParameters ? throw new CryptographicException(Tr.T("Der Schlüssel bleibt auf der Karte.")) : _public.ExportParameters(false);

    public override ECParameters ExportExplicitParameters(bool includePrivateParameters) => _public.ExportExplicitParameters(false);

    public override void ImportParameters(ECParameters parameters) => throw new NotSupportedException();

    public override void GenerateKey(ECCurve curve) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _session.Dispose();
            _public.Dispose();
        }
        base.Dispose(disposing);
    }
}
