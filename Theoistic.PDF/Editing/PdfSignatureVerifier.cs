using System.Formats.Asn1;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using Theoistic.PDF.Writing;

namespace Theoistic.PDF.Editing;

/// <summary>
/// Finds the signatures in a PDF and checks that each still verifies.
/// <para>
/// This answers whether the signed bytes are intact and were signed by the key of the embedded
/// certificate. Whether that certificate is one to trust - its chain, its validity period, its
/// revocation status - is the caller's decision, made with <see cref="PdfSignatureInfo.Certificate"/>.
/// </para>
/// </summary>
public static class PdfSignatureVerifier
{
    private const string Sha1Oid = "1.3.14.3.2.26";
    private const string Sha384Oid = "2.16.840.1.101.3.4.2.2";
    private const string Sha512Oid = "2.16.840.1.101.3.4.2.3";

    /// <summary>Every signature in the file, oldest first, with whether each one still verifies.</summary>
    public static IReadOnlyList<PdfSignatureInfo> Verify(byte[] pdf)
    {
        ArgumentNullException.ThrowIfNull(pdf);

        PdfFile file = PdfFile.Open(pdf);
        var signatures = new List<PdfSignatureInfo>();
        var seen = new HashSet<int>();

        foreach (FormField field in FormFields.Read(file))
        {
            if (field.Type != "Sig" || field.Dictionary["V"] is not PdfObject value)
            {
                continue;
            }

            // A field and its merged widget can both lead to the same signature dictionary.
            if (value is PdfReference reference && !seen.Add(reference.ObjectNumber))
            {
                continue;
            }

            if (file.Resolve(value) is PdfDictionary signature)
            {
                signatures.Add(Check(file, field.FullName, signature));
            }
        }

        // A later signature covers more of the file, so the length it signed orders them.
        return signatures.OrderBy(signature => signature.SignedRevisionLength).ToList();
    }

    private static PdfSignatureInfo Check(PdfFile file, string fieldName, PdfDictionary signature)
    {
        byte[] data = file.Data;
        string subFilter = (file.Resolve(signature["SubFilter"]) as PdfName)?.Value ?? string.Empty;
        long[]? range = ReadByteRange(file, signature["ByteRange"], data.Length);
        long signedLength = range is null ? 0 : range[2] + range[3];

        X509Certificate2? certificate = null;
        bool valid = false;
        DateTimeOffset? timestamp = null;

        if (range is not null && TryReadGap(data, range, file.Resolve(signature["Contents"]) as PdfString, out byte[] encoded))
        {
            // Everything in the two ranges, which is the signed revision less the gap.
            byte[] signedContent = new byte[range[1] + range[3]];

            Array.Copy(data, range[0], signedContent, 0, range[1]);
            Array.Copy(data, range[2], signedContent, range[1], range[3]);

            try
            {
                (valid, certificate, timestamp) = subFilter switch
                {
                    "ETSI.RFC3161" => CheckDocumentTimestamp(encoded, signedContent),
                    "adbe.pkcs7.sha1" => CheckEncapsulatedDigest(encoded, signedContent),
                    _ => CheckDetached(encoded, signedContent)
                };
            }
            catch (Exception exception) when (exception is CryptographicException or AsnContentException or ArgumentException or InvalidOperationException)
            {
                valid = false;
            }
        }

        return new PdfSignatureInfo(
            fieldName,
            PdfText.Decode(file.Resolve(signature["Name"])),
            PdfText.Decode(file.Resolve(signature["Reason"])),
            PdfText.Decode(file.Resolve(signature["Location"])),
            PdfText.ParseDate(PdfText.Decode(file.Resolve(signature["M"]))),
            certificate,
            valid,
            range is not null && signedLength == data.Length,
            signedLength,
            timestamp,
            subFilter);
    }

    /// <summary>
    /// The byte range, if it is the one shape a signature may have: from the start of the file to
    /// the /Contents value, and from after it to the end of the signed revision.
    /// </summary>
    private static long[]? ReadByteRange(PdfFile file, PdfObject? value, long fileLength)
    {
        if (file.Resolve(value) is not PdfArray { Count: 4 } array)
        {
            return null;
        }

        var range = new long[4];

        for (int i = 0; i < 4; i++)
        {
            if (file.Resolve(array[i]) is not PdfInteger { Value: >= 0 } number)
            {
                return null;
            }

            range[i] = number.Value;
        }

        bool sane = range[0] == 0 &&
            range[1] > 0 &&
            range[2] >= range[1] + 2 &&
            range[2] + range[3] <= fileLength;

        return sane ? range : null;
    }

    /// <summary>
    /// Reads the signature from the gap the byte range leaves. It has to be exactly the hex string
    /// that /Contents holds - otherwise the range was drawn around something else, and what it
    /// claims to protect is not what was signed.
    /// </summary>
    private static bool TryReadGap(byte[] data, long[] range, PdfString? contents, out byte[] encoded)
    {
        encoded = Array.Empty<byte>();

        if (contents is null || data[range[1]] != (byte)'<' || data[range[2] - 1] != (byte)'>')
        {
            return false;
        }

        try
        {
            var parser = new PdfParser(data, (int)range[1], (int)(range[2] - range[1]));

            if (parser.ReadObject() is not PdfString gap || parser.Position != range[2] || !gap.Bytes.AsSpan().SequenceEqual(contents.Bytes))
            {
                return false;
            }

            // The placeholder is longer than the signature, and the rest is zero padding that is
            // not part of the encoding.
            AsnDecoder.ReadEncodedValue(gap.Bytes, AsnEncodingRules.BER, out _, out _, out int length);

            encoded = gap.Bytes.AsSpan(0, length).ToArray();

            return true;
        }
        catch (Exception exception) when (exception is PdfFormatException or AsnContentException)
        {
            return false;
        }
    }

    private static (bool Valid, X509Certificate2? Certificate, DateTimeOffset? Timestamp) CheckDetached(byte[] encoded, byte[] signedContent)
    {
        var cms = new SignedCms(new ContentInfo(signedContent), detached: true);

        cms.Decode(encoded);

        if (cms.SignerInfos.Count == 0)
        {
            return (false, null, null);
        }

        SignerInfo signer = cms.SignerInfos[0];
        X509Certificate2? certificate = signer.Certificate;

        // Checks the signature over the signed attributes, and that their message digest matches
        // the content. Only the signature: building a chain to a trusted root is the caller's call.
        cms.CheckSignature(verifySignatureOnly: true);

        bool digestMatches = DigestMatches(signer, signedContent);

        return (digestMatches, certificate, ReadSignatureTimestamp(signer));
    }

    /// <summary>
    /// The old adbe.pkcs7.sha1 form: the SHA-1 of the byte ranges is the signed content itself,
    /// encapsulated rather than detached.
    /// </summary>
    private static (bool Valid, X509Certificate2? Certificate, DateTimeOffset? Timestamp) CheckEncapsulatedDigest(byte[] encoded, byte[] signedContent)
    {
        var cms = new SignedCms();

        cms.Decode(encoded);

        if (cms.SignerInfos.Count == 0)
        {
            return (false, null, null);
        }

        SignerInfo signer = cms.SignerInfos[0];

        cms.CheckSignature(verifySignatureOnly: true);

        byte[] expected = SHA1.HashData(signedContent);
        byte[] content = cms.ContentInfo.Content;

        // The content is the digest itself, sometimes wrapped as an OCTET STRING.
        bool matches = content.AsSpan().SequenceEqual(expected) ||
            (TryReadOctetString(content, out byte[] inner) && inner.AsSpan().SequenceEqual(expected));

        return (matches, signer.Certificate, ReadSignatureTimestamp(signer));
    }

    /// <summary>A document timestamp: the token's own imprint covers the byte ranges.</summary>
    private static (bool Valid, X509Certificate2? Certificate, DateTimeOffset? Timestamp) CheckDocumentTimestamp(byte[] encoded, byte[] signedContent)
    {
        if (!Rfc3161TimestampToken.TryDecode(encoded, out Rfc3161TimestampToken? token, out _) || token is null)
        {
            return (false, null, null);
        }

        bool valid = token.VerifySignatureForData(signedContent, out X509Certificate2? signer);

        return (valid, signer, token.TokenInfo.Timestamp);
    }

    private static bool DigestMatches(SignerInfo signer, byte[] content)
    {
        HashAlgorithmName? algorithm = signer.DigestAlgorithm.Value switch
        {
            CadesSignature.Sha256Oid => HashAlgorithmName.SHA256,
            Sha384Oid => HashAlgorithmName.SHA384,
            Sha512Oid => HashAlgorithmName.SHA512,
            Sha1Oid => HashAlgorithmName.SHA1,
            _ => null
        };

        if (algorithm is not HashAlgorithmName name)
        {
            return false;
        }

        byte[]? declared = null;

        foreach (CryptographicAttributeObject attribute in signer.SignedAttributes)
        {
            if (attribute.Oid.Value == CadesSignature.MessageDigestOid && attribute.Values.Count > 0 &&
                TryReadOctetString(attribute.Values[0].RawData, out byte[] digest))
            {
                declared = digest;
            }
        }

        // Without signed attributes the signature is over the content's digest directly, and
        // CheckSignature has already compared it.
        if (signer.SignedAttributes.Count == 0)
        {
            return true;
        }

        if (declared is null)
        {
            return false;
        }

        using IncrementalHash hash = IncrementalHash.CreateHash(name);

        hash.AppendData(content);

        return CryptographicOperations.FixedTimeEquals(hash.GetHashAndReset(), declared);
    }

    /// <summary>
    /// The time from a signature-time-stamp attribute, if the token in it is genuine and covers
    /// this signature's value; null otherwise.
    /// </summary>
    private static DateTimeOffset? ReadSignatureTimestamp(SignerInfo signer)
    {
        foreach (CryptographicAttributeObject attribute in signer.UnsignedAttributes)
        {
            if (attribute.Oid.Value != CadesSignature.SignatureTimestampOid)
            {
                continue;
            }

            foreach (AsnEncodedData value in attribute.Values)
            {
                if (Rfc3161TimestampToken.TryDecode(value.RawData, out Rfc3161TimestampToken? token, out _) &&
                    token is not null &&
                    token.VerifySignatureForSignerInfo(signer, out _))
                {
                    return token.TokenInfo.Timestamp;
                }
            }
        }

        return null;
    }

    private static bool TryReadOctetString(byte[] encoded, out byte[] value)
    {
        try
        {
            value = AsnDecoder.ReadOctetString(encoded, AsnEncodingRules.BER, out _);

            return true;
        }
        catch (AsnContentException)
        {
            value = Array.Empty<byte>();

            return false;
        }
    }
}

/// <summary>One signature found in a PDF, and whether it still verifies.</summary>
/// <param name="FieldName">The signature field's fully qualified name.</param>
/// <param name="Name">The signer's name as the signature dictionary records it (/Name).</param>
/// <param name="Reason">/Reason.</param>
/// <param name="Location">/Location.</param>
/// <param name="SigningTime">The claimed signing time (/M), as the signer's clock had it.</param>
/// <param name="Certificate">The signer's certificate, taken from the signature.</param>
/// <param name="IntegrityValid">
/// Whether the signature verifies over the signed byte ranges with the signer certificate's key
/// and the message digest it carries matches those bytes. Says nothing about whether the
/// certificate is trusted.
/// </param>
/// <param name="CoversWholeDocument">
/// Whether the signed range runs to the end of the file. False for a signature followed by later
/// incremental updates, such as another signature.
/// </param>
/// <param name="SignedRevisionLength">How many bytes from the start of the file the signature covers.</param>
/// <param name="TimestampTime">
/// The time from an embedded RFC 3161 signature timestamp, when one is present and genuine for
/// this signature; for a document timestamp, the time it attests.
/// </param>
/// <param name="SubFilter">The signature format, such as <c>ETSI.CAdES.detached</c>.</param>
public sealed record PdfSignatureInfo(
    string FieldName,
    string? Name,
    string? Reason,
    string? Location,
    DateTimeOffset? SigningTime,
    X509Certificate2? Certificate,
    bool IntegrityValid,
    bool CoversWholeDocument,
    long SignedRevisionLength,
    DateTimeOffset? TimestampTime,
    string SubFilter);
