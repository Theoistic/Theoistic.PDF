using System.Formats.Asn1;
using System.Net.Http.Headers;
using System.Numerics;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;

namespace Theoistic.PDF.Editing;

/// <summary>
/// The CMS signature a PAdES baseline signature carries (ETSI EN 319 142-1, built on CAdES): a
/// detached SignedData over the signed byte ranges, SHA-256, the signer identified by issuer and
/// serial number, and exactly the signed attributes PAdES asks for.
/// </summary>
internal static class CadesSignature
{
    public const string Sha256Oid = "2.16.840.1.101.3.4.2.1";
    public const string ContentTypeOid = "1.2.840.113549.1.9.3";
    public const string MessageDigestOid = "1.2.840.113549.1.9.4";
    public const string SigningTimeOid = "1.2.840.113549.1.9.5";
    public const string SigningCertificateV2Oid = "1.2.840.113549.1.9.16.2.47";
    public const string SignatureTimestampOid = "1.2.840.113549.1.9.16.2.14";

    private static readonly Lazy<HttpClient> SharedHttpClient = new Lazy<HttpClient>(() =>
        new HttpClient(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) }));

    public static HttpClient DefaultHttpClient => SharedHttpClient.Value;

    /// <summary>
    /// Signs <paramref name="content"/>. The content-type and message-digest attributes are added
    /// by <see cref="SignedCms"/>; the signing certificate reference is added here, because PAdES
    /// requires it - it binds the signature to one certificate so it cannot be passed off as made
    /// with another carrying the same key. No signing-time attribute is added: PAdES forbids it,
    /// and the time goes in the signature dictionary's /M instead.
    /// </summary>
    public static SignedCms Sign(byte[] content, X509Certificate2 certificate, IReadOnlyList<X509Certificate2> extraCertificates)
    {
        var cms = new SignedCms(new ContentInfo(content), detached: true);

        var signer = new CmsSigner(SubjectIdentifierType.IssuerAndSerialNumber, certificate)
        {
            DigestAlgorithm = new Oid(Sha256Oid),

            // Only the signer's certificate is taken from the certificate itself. Building the
            // whole chain would consult the machine's stores and, for missing intermediates, the
            // network; the caller names the intermediates to embed instead.
            IncludeOption = X509IncludeOption.EndCertOnly,
        };

        foreach (X509Certificate2 extra in extraCertificates)
        {
            signer.Certificates.Add(extra);
        }

        signer.SignedAttributes.Add(new AsnEncodedData(SigningCertificateV2Oid, EncodeSigningCertificateV2(certificate)));

        cms.ComputeSignature(signer);

        return cms;
    }

    /// <summary>
    /// The ESS signing-certificate-v2 attribute value (RFC 5035):
    /// <code>
    /// SigningCertificateV2 ::= SEQUENCE { certs SEQUENCE OF ESSCertIDv2 }
    /// ESSCertIDv2 ::= SEQUENCE { certHash OCTET STRING, issuerSerial IssuerSerial }
    /// IssuerSerial ::= SEQUENCE { issuer GeneralNames, serialNumber INTEGER }
    /// </code>
    /// </summary>
    public static byte[] EncodeSigningCertificateV2(X509Certificate2 certificate)
    {
        var writer = new AsnWriter(AsnEncodingRules.DER);

        using (writer.PushSequence())
        {
            using (writer.PushSequence())
            {
                using (writer.PushSequence())
                {
                    // hashAlgorithm is left out: SHA-256 is its default, and DER forbids encoding
                    // a value equal to the default.
                    writer.WriteOctetString(SHA256.HashData(certificate.RawData));

                    using (writer.PushSequence())
                    {
                        using (writer.PushSequence())
                        {
                            // GeneralName's directoryName is [4], explicitly tagged because Name is
                            // itself a CHOICE.
                            using (writer.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 4, isConstructed: true)))
                            {
                                writer.WriteEncodedValue(certificate.IssuerName.RawData);
                            }
                        }

                        writer.WriteInteger(new BigInteger(certificate.SerialNumberBytes.Span, isUnsigned: false, isBigEndian: true));
                    }
                }
            }
        }

        return writer.Encode();
    }

    /// <summary>
    /// Asks an RFC 3161 service to timestamp the signature value and attaches the token as the
    /// signature-time-stamp unsigned attribute, which is what makes a signature PAdES B-T.
    /// </summary>
    public static async Task AddTimestampAsync(SignedCms cms, Uri authority, HttpClient client, CancellationToken cancellationToken)
    {
        SignerInfo signerInfo = cms.SignerInfos[0];

        byte[] nonce = RandomNumberGenerator.GetBytes(8);

        // A positive nonce with no leading zero byte encodes the same however a TSA normalises it.
        nonce[0] = (byte)((nonce[0] & 0x7F) | 0x01);

        Rfc3161TimestampRequest request = Rfc3161TimestampRequest.CreateFromData(
            signerInfo.GetSignature(),
            HashAlgorithmName.SHA256,
            requestedPolicyId: null,
            nonce: nonce,
            requestSignerCertificates: true);

        using var body = new ByteArrayContent(request.Encode());

        body.Headers.ContentType = new MediaTypeHeaderValue("application/timestamp-query");

        using HttpResponseMessage response = await client.PostAsync(authority, body, cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"The timestamp authority at {authority} answered {(int)response.StatusCode} {response.ReasonPhrase}.",
                inner: null,
                response.StatusCode);
        }

        byte[] reply = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        Rfc3161TimestampToken token;

        try
        {
            // Checks the status, that the token covers this signature, and that the nonce matches.
            token = request.ProcessResponse(reply, out _);
        }
        catch (CryptographicException exception)
        {
            throw new CryptographicException(
                $"The timestamp authority at {authority} returned a response that does not answer the request: {exception.Message}",
                exception);
        }

        signerInfo.AddUnsignedAttribute(new AsnEncodedData(SignatureTimestampOid, token.AsSignedCms().Encode()));
    }
}
