using System.Security.Cryptography;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// The signature methods of the EU trusted lists for the XML signature code of the .NET libraries (test project only): it lacks some of them
/// (RSA with SHA-512, RSA-PSS, ECDSA), so the tests give them to it — to sign test lists and to cross-check the own verification. Signing and
/// verifying go to the .NET key classes directly.
/// </summary>
public static class XmlSignatureMethods
{
    private static int _registered;

    /// <summary>Registers the methods once per test run.</summary>
    public static void Register()
    {
        if (Interlocked.Exchange(ref _registered, 1) == 1) return;
        CryptoConfig.AddAlgorithm(typeof(RsaSha256), "http://www.w3.org/2001/04/xmldsig-more#rsa-sha256");
        CryptoConfig.AddAlgorithm(typeof(RsaSha512), "http://www.w3.org/2001/04/xmldsig-more#rsa-sha512");
        CryptoConfig.AddAlgorithm(typeof(PssSha256), "http://www.w3.org/2007/05/xmldsig-more#sha256-rsa-MGF1");
        CryptoConfig.AddAlgorithm(typeof(EcdsaSha256), "http://www.w3.org/2001/04/xmldsig-more#ecdsa-sha256");
        CryptoConfig.AddAlgorithm(typeof(EcdsaSha512), "http://www.w3.org/2001/04/xmldsig-more#ecdsa-sha512");
    }

    public class Description : SignatureDescription
    {
        private readonly HashAlgorithmName _hash;
        private readonly string _kind;

        protected Description(HashAlgorithmName hash, string kind)
        {
            _hash = hash;
            _kind = kind;
            KeyAlgorithm = (kind == "ecdsa" ? typeof(ECDsa) : typeof(RSA)).AssemblyQualifiedName;
        }

        public override AsymmetricSignatureDeformatter CreateDeformatter(AsymmetricAlgorithm key) => new Deformatter(key, _hash, _kind);

        public override AsymmetricSignatureFormatter CreateFormatter(AsymmetricAlgorithm key) => new Formatter(key, _hash, _kind);

        public override HashAlgorithm CreateDigest() =>
            _hash == HashAlgorithmName.SHA256 ? SHA256.Create() : _hash == HashAlgorithmName.SHA384 ? SHA384.Create() : SHA512.Create();
    }

    private sealed class Deformatter(AsymmetricAlgorithm key, HashAlgorithmName hash, string kind) : AsymmetricSignatureDeformatter
    {
        public override void SetKey(AsymmetricAlgorithm key) { }

        public override void SetHashAlgorithm(string strName) { }

        public override bool VerifySignature(byte[] rgbHash, byte[] rgbSignature) => kind switch
        {
            "ecdsa" => ((ECDsa)key).VerifyHash(rgbHash, rgbSignature, DSASignatureFormat.IeeeP1363FixedFieldConcatenation),
            "pss" => ((RSA)key).VerifyHash(rgbHash, rgbSignature, hash, RSASignaturePadding.Pss),
            _ => ((RSA)key).VerifyHash(rgbHash, rgbSignature, hash, RSASignaturePadding.Pkcs1),
        };
    }

    private sealed class Formatter(AsymmetricAlgorithm key, HashAlgorithmName hash, string kind) : AsymmetricSignatureFormatter
    {
        public override void SetKey(AsymmetricAlgorithm key) { }

        public override void SetHashAlgorithm(string strName) { }

        public override byte[] CreateSignature(byte[] rgbHash) => kind switch
        {
            "ecdsa" => ((ECDsa)key).SignHash(rgbHash, DSASignatureFormat.IeeeP1363FixedFieldConcatenation),
            "pss" => ((RSA)key).SignHash(rgbHash, hash, RSASignaturePadding.Pss),
            _ => ((RSA)key).SignHash(rgbHash, hash, RSASignaturePadding.Pkcs1),
        };
    }

    public sealed class RsaSha256() : Description(HashAlgorithmName.SHA256, "rsa");
    public sealed class RsaSha512() : Description(HashAlgorithmName.SHA512, "rsa");
    public sealed class PssSha256() : Description(HashAlgorithmName.SHA256, "pss");
    public sealed class EcdsaSha256() : Description(HashAlgorithmName.SHA256, "ecdsa");
    public sealed class EcdsaSha512() : Description(HashAlgorithmName.SHA512, "ecdsa");
}
