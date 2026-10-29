using System.Formats.Asn1;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.RegularExpressions;
using Theoistic.PDF.Editing;
using Theoistic.PDF.Layout;
using Theoistic.PDF.Writing;

namespace Theoistic.PDF.Tests;

/// <summary>
/// Covers PAdES signing and verification: the structure the signature update adds, the CMS
/// attributes PAdES requires, tamper detection, and a document signed twice with an edit in
/// between, where the first signature has to keep verifying for the revision it signed.
/// </summary>
[TestClass]
public sealed class PdfSigningTests
{
    private static readonly DateTimeOffset SigningTime = new DateTimeOffset(2026, 10, 29, 12, 30, 0, TimeSpan.FromHours(1));

    private static string Latin1(byte[] bytes) => Encoding.Latin1.GetString(bytes);

    private static X509Certificate2 RsaCertificate(string name = "Theoistic Test Signer")
    {
        using RSA key = RSA.Create(2048);
        var request = new CertificateRequest($"CN={name}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.NonRepudiation, critical: true));

        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
    }

    private static X509Certificate2 EcdsaCertificate()
    {
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=Theoistic Test ECDSA Signer", key, HashAlgorithmName.SHA256);

        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, critical: true));

        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
    }

    private static byte[] Document()
    {
        var document = new PdfDocument();

        document.AddPage(PdfPaperSize.A4).Content.DrawText("Power of attorney", StandardFont.Helvetica, 14, 72, 760);

        return document.ToArray();
    }

    private static PdfSignatureOptions Options(X509Certificate2 certificate) => new PdfSignatureOptions
    {
        Certificate = certificate,
        Name = "Jóhanna Á. Hansen",
        Reason = "I accept the terms",
        Location = "Tórshavn",
        ContactInfo = "hr@example.com",
        SigningTime = SigningTime,
    };

    /// <summary>The CMS of the newest signature, read straight from the gap its byte range leaves.</summary>
    private static SignedCms NewestCms(byte[] signed)
    {
        MatchCollection ranges = Regex.Matches(Latin1(signed), @"/ByteRange \[0 (\d+) (\d+) (\d+)\]");
        Match newest = ranges[^1];
        int gapStart = int.Parse(newest.Groups[1].Value);
        int gapEnd = int.Parse(newest.Groups[2].Value);

        byte[] padded = Convert.FromHexString(Latin1(signed[(gapStart + 1)..(gapEnd - 1)]));

        AsnDecoder.ReadEncodedValue(padded, AsnEncodingRules.BER, out _, out _, out int length);

        var cms = new SignedCms();

        cms.Decode(padded.AsSpan(0, length));

        return cms;
    }

    // ---------------------------------------------------------------- signing

    [TestMethod]
    public async Task Sign_Rsa_VerifiesAndCoversTheWholeDocument()
    {
        using X509Certificate2 certificate = RsaCertificate();
        byte[] original = Document();

        byte[] signed = await PdfSigner.SignAsync(original, Options(certificate));

        CollectionAssert.AreEqual(original, signed.Take(original.Length).ToArray(), "the original is not a prefix of the signed file");

        PdfSignatureInfo signature = PdfSignatureVerifier.Verify(signed).Single();

        Assert.IsTrue(signature.IntegrityValid);
        Assert.IsTrue(signature.CoversWholeDocument);
        Assert.AreEqual(signed.Length, signature.SignedRevisionLength);
        Assert.AreEqual("Signature1", signature.FieldName);
        Assert.AreEqual("Jóhanna Á. Hansen", signature.Name);
        Assert.AreEqual("I accept the terms", signature.Reason);
        Assert.AreEqual("Tórshavn", signature.Location);
        Assert.AreEqual(SigningTime, signature.SigningTime);
        Assert.AreEqual("ETSI.CAdES.detached", signature.SubFilter);
        Assert.AreEqual(certificate.Thumbprint, signature.Certificate!.Thumbprint);
        Assert.IsNull(signature.TimestampTime);
    }

    [TestMethod]
    public async Task Sign_Ecdsa_Verifies()
    {
        using X509Certificate2 certificate = EcdsaCertificate();

        byte[] signed = await PdfSigner.SignAsync(Document(), Options(certificate));
        PdfSignatureInfo signature = PdfSignatureVerifier.Verify(signed).Single();

        Assert.IsTrue(signature.IntegrityValid);
        Assert.IsTrue(signature.CoversWholeDocument);
    }

    [TestMethod]
    public async Task Sign_WritesAnInvisibleFieldAndTheSignatureDictionary()
    {
        using X509Certificate2 certificate = RsaCertificate();
        byte[] original = Document();

        byte[] signed = await PdfSigner.SignAsync(original, Options(certificate));
        string update = Latin1(signed[original.Length..]);

        StringAssert.Contains(update, "/Filter /Adobe.PPKLite");
        StringAssert.Contains(update, "/SubFilter /ETSI.CAdES.detached");
        StringAssert.Contains(update, "/FT /Sig");
        StringAssert.Contains(update, "/Rect [0 0 0 0]");
        StringAssert.Contains(update, "/F 132");
        StringAssert.Contains(update, "/SigFlags 3");
        StringAssert.Contains(update, "/M (D:20261029123000+01'00)");

        PdfFile file = PdfFile.Open(signed);
        PdfPageNode page = PageTree.Read(file)[0];
        var annotations = (PdfArray)file.Resolve(page.Dictionary["Annots"])!;
        var widget = (PdfDictionary)file.Resolve(annotations[0])!;

        Assert.AreEqual(page.Reference!.ToString(), widget["P"]!.ToString());
    }

    [TestMethod]
    public async Task Sign_CmsCarriesTheSigningCertificateAndNoSigningTime()
    {
        using X509Certificate2 certificate = RsaCertificate();

        byte[] signed = await PdfSigner.SignAsync(Document(), Options(certificate));
        SignerInfo signer = NewestCms(signed).SignerInfos[0];

        string[] attributes = signer.SignedAttributes.Cast<CryptographicAttributeObject>().Select(a => a.Oid.Value!).ToArray();

        CollectionAssert.AreEquivalent(
            new[] { CadesSignature.ContentTypeOid, CadesSignature.MessageDigestOid, CadesSignature.SigningCertificateV2Oid },
            attributes);

        Assert.AreEqual(CadesSignature.Sha256Oid, signer.DigestAlgorithm.Value);
        Assert.AreEqual(SubjectIdentifierType.IssuerAndSerialNumber, signer.SignerIdentifier.Type);

        // The attribute names this certificate by its SHA-256 hash.
        CryptographicAttributeObject essAttribute = signer.SignedAttributes
            .Cast<CryptographicAttributeObject>()
            .Single(a => a.Oid.Value == CadesSignature.SigningCertificateV2Oid);

        var reader = new AsnReader(essAttribute.Values[0].RawData, AsnEncodingRules.DER);
        AsnReader certificateId = reader.ReadSequence().ReadSequence().ReadSequence();

        CollectionAssert.AreEqual(SHA256.HashData(certificate.RawData), certificateId.ReadOctetString());
    }

    [TestMethod]
    public async Task Sign_ThenTamperOneByte_IntegrityFails()
    {
        using X509Certificate2 certificate = RsaCertificate();

        byte[] signed = await PdfSigner.SignAsync(Document(), Options(certificate));

        // A byte of the binary-marker comment: the file still parses, but it is not what was signed.
        signed[10] ^= 0x01;

        PdfSignatureInfo signature = PdfSignatureVerifier.Verify(signed).Single();

        Assert.IsFalse(signature.IntegrityValid);
    }

    [TestMethod]
    public async Task Sign_EditAndSignAgain_BothSignaturesVerifyForTheirRevisions()
    {
        using X509Certificate2 applicant = RsaCertificate("Applicant");
        using X509Certificate2 company = EcdsaCertificate();

        byte[] once = await PdfSigner.SignAsync(Document(), Options(applicant));

        PdfEditor editor = PdfEditor.Open(once);

        editor.DrawOn(0).Content.DrawText("Approved by HR", StandardFont.HelveticaBold, 12, 72, 100);
        editor.AppendPages(new FlowDocument().Paragraph("Audit trail").ToArray());

        byte[] edited = editor.Save();
        byte[] twice = await PdfSigner.SignAsync(edited, Options(company));

        IReadOnlyList<PdfSignatureInfo> signatures = PdfSignatureVerifier.Verify(twice);

        Assert.AreEqual(2, signatures.Count);

        Assert.AreEqual("Signature1", signatures[0].FieldName);
        Assert.IsTrue(signatures[0].IntegrityValid, "the first signature broke when the document was extended");
        Assert.IsFalse(signatures[0].CoversWholeDocument);
        Assert.AreEqual(once.Length, signatures[0].SignedRevisionLength);
        Assert.AreEqual(applicant.Thumbprint, signatures[0].Certificate!.Thumbprint);

        Assert.AreEqual("Signature2", signatures[1].FieldName);
        Assert.IsTrue(signatures[1].IntegrityValid);
        Assert.IsTrue(signatures[1].CoversWholeDocument);
        Assert.AreEqual(company.Thumbprint, signatures[1].Certificate!.Thumbprint);

        Assert.AreEqual(2, PdfEditor.Open(twice).Pages.Count);
    }

    [TestMethod]
    public async Task Sign_XrefStreamFile_VerifiesAndKeepsTheSectionKind()
    {
        using X509Certificate2 certificate = RsaCertificate();
        byte[] original = PdfFixtures.ObjectStreams();

        byte[] signed = await PdfSigner.SignAsync(original, Options(certificate));

        StringAssert.Contains(Latin1(signed[original.Length..]), "/Type /XRef");
        Assert.IsTrue(PdfSignatureVerifier.Verify(signed).Single().IntegrityValid);
    }

    [TestMethod]
    public async Task Sign_ExistingIndirectForm_KeepsItsFieldsAndSettings()
    {
        using X509Certificate2 certificate = RsaCertificate();

        byte[] original = PdfFixtures.Classic(new[]
        {
            "<< /Type /Catalog /Pages 2 0 R /AcroForm 4 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] /Annots 6 0 R >>",
            "<< /Fields [5 0 R] /DA (/Helv 0 Tf 0 g) >>",
            "<< /FT /Tx /T (Signature1) /Type /Annot /Subtype /Widget /Rect [10 10 100 30] /P 3 0 R >>",
            "[5 0 R]",
        });

        byte[] signed = await PdfSigner.SignAsync(original, Options(certificate));
        string update = Latin1(signed[original.Length..]);

        // The form dictionary is rewritten under its own number; the catalog and the shared
        // annotation array are left alone.
        StringAssert.Matches(update, new Regex(@"(?m)^4 0 obj"));
        Assert.IsFalse(Regex.IsMatch(update, @"(?m)^1 0 obj"), "the catalog was rewritten although the form is indirect");
        Assert.IsFalse(Regex.IsMatch(update, @"(?m)^6 0 obj"), "the shared /Annots array was modified in place");

        PdfFile file = PdfFile.Open(signed);
        var form = (PdfDictionary)file.Resolve(file.Catalog["AcroForm"])!;

        Assert.AreEqual(2, ((PdfArray)file.Resolve(form["Fields"])!).Count);
        Assert.IsNotNull(form["DA"]);
        Assert.AreEqual(2, ((PdfArray)file.Resolve(PageTree.Read(file)[0].Dictionary["Annots"])!).Count);

        // Signature1 is taken by the text field, so the signature gets the next name.
        Assert.AreEqual("Signature2", PdfSignatureVerifier.Verify(signed).Single().FieldName);
    }

    [TestMethod]
    public async Task Sign_FieldNames_AreValidated()
    {
        using X509Certificate2 certificate = RsaCertificate();

        byte[] signed = await PdfSigner.SignAsync(Document(), new PdfSignatureOptions { Certificate = certificate, FieldName = "Applicant" });

        Assert.AreEqual("Applicant", PdfSignatureVerifier.Verify(signed).Single().FieldName);

        await Assert.ThrowsExceptionAsync<ArgumentException>(() =>
            PdfSigner.SignAsync(signed, new PdfSignatureOptions { Certificate = certificate, FieldName = "Applicant" }));
        await Assert.ThrowsExceptionAsync<ArgumentException>(() =>
            PdfSigner.SignAsync(signed, new PdfSignatureOptions { Certificate = certificate, FieldName = "a.b" }));
    }

    [TestMethod]
    public async Task Sign_WithoutPrivateKey_Throws()
    {
        using X509Certificate2 withKey = RsaCertificate();
        using X509Certificate2 publicOnly = X509CertificateLoader.LoadCertificate(withKey.RawData);

        await Assert.ThrowsExceptionAsync<ArgumentException>(() =>
            PdfSigner.SignAsync(Document(), new PdfSignatureOptions { Certificate = publicOnly }));
    }

    [TestMethod]
    public async Task Sign_PlaceholderTooSmall_WritesTheUpdateAgainWithRoom()
    {
        using X509Certificate2 certificate = RsaCertificate();

        byte[] signed = await PdfSigner.SignAsync(Document(), Options(certificate), initialReserve: 64, CancellationToken.None);

        Assert.IsTrue(PdfSignatureVerifier.Verify(signed).Single().IntegrityValid);
    }

    [TestMethod]
    public async Task Sign_SameInputsWithRsa_ProduceTheSameBytes()
    {
        using X509Certificate2 certificate = RsaCertificate();
        byte[] document = Document();

        byte[] first = await PdfSigner.SignAsync(document, Options(certificate));
        byte[] second = await PdfSigner.SignAsync(document, Options(certificate));

        // PKCS#1 v1.5 signatures are deterministic and nothing else in the update is random, so
        // with a fixed signing time the whole signed file is reproducible.
        CollectionAssert.AreEqual(first, second);
    }

    [TestMethod]
    public void Verify_UnsignedDocument_FindsNothing()
    {
        Assert.AreEqual(0, PdfSignatureVerifier.Verify(Document()).Count);
    }

    // ---------------------------------------------------------------- timestamps

    [TestMethod]
    public async Task Sign_WithTimestampAuthority_EmbedsASignatureTimestamp()
    {
        using X509Certificate2 certificate = RsaCertificate();
        using X509Certificate2 authority = TimestampAuthorityCertificate();
        var stampedAt = new DateTimeOffset(2026, 10, 29, 11, 31, 5, TimeSpan.Zero);
        var handler = new FakeTimestampAuthority(authority, stampedAt);
        using var client = new HttpClient(handler);

        PdfSignatureOptions options = Options(certificate);

        byte[] signed = await PdfSigner.SignAsync(Document(), new PdfSignatureOptions
        {
            Certificate = options.Certificate,
            SigningTime = options.SigningTime,
            TimestampAuthority = new Uri("https://tsa.example.test/"),
            HttpClient = client,
        });

        Assert.AreEqual(1, handler.Requests);

        PdfSignatureInfo signature = PdfSignatureVerifier.Verify(signed).Single();

        Assert.IsTrue(signature.IntegrityValid);
        Assert.AreEqual(stampedAt, signature.TimestampTime);

        CryptographicAttributeObject token = NewestCms(signed).SignerInfos[0].UnsignedAttributes
            .Cast<CryptographicAttributeObject>()
            .Single();

        Assert.AreEqual(CadesSignature.SignatureTimestampOid, token.Oid.Value);
    }

    [TestMethod]
    public async Task Sign_TimestampAuthorityFails_Throws()
    {
        using X509Certificate2 certificate = RsaCertificate();
        using var client = new HttpClient(new FailingHandler());

        await Assert.ThrowsExceptionAsync<HttpRequestException>(() => PdfSigner.SignAsync(Document(), new PdfSignatureOptions
        {
            Certificate = certificate,
            TimestampAuthority = new Uri("https://tsa.example.test/"),
            HttpClient = client,
        }));
    }

    private static X509Certificate2 TimestampAuthorityCertificate()
    {
        using RSA key = RSA.Create(2048);
        var request = new CertificateRequest("CN=Theoistic Test TSA", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, critical: true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new Oid("1.3.6.1.5.5.7.3.8") }, critical: true));

        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
    }

    /// <summary>An RFC 3161 service in a handler: answers each request with a token it signs itself.</summary>
    private sealed class FakeTimestampAuthority : HttpMessageHandler
    {
        private readonly X509Certificate2 certificate;
        private readonly DateTimeOffset time;

        public FakeTimestampAuthority(X509Certificate2 certificate, DateTimeOffset time)
        {
            this.certificate = certificate;
            this.time = time;
        }

        public int Requests { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;

            Assert.AreEqual("application/timestamp-query", request.Content!.Headers.ContentType!.MediaType);

            byte[] body = await request.Content.ReadAsByteArrayAsync(cancellationToken);

            Assert.IsTrue(Rfc3161TimestampRequest.TryDecode(body, out Rfc3161TimestampRequest? query, out _));

            var info = new Rfc3161TimestampTokenInfo(
                new Oid("1.2.3.4.1"),
                query!.HashAlgorithmId,
                query.GetMessageHash(),
                serialNumber: new byte[] { 0x01, 0x02 },
                timestamp: time,
                nonce: query.GetNonce());

            var token = new SignedCms(new ContentInfo(new Oid("1.2.840.113549.1.9.16.1.4"), info.Encode()));
            var signer = new CmsSigner(SubjectIdentifierType.IssuerAndSerialNumber, certificate)
            {
                DigestAlgorithm = new Oid(CadesSignature.Sha256Oid),
                IncludeOption = X509IncludeOption.EndCertOnly,
            };

            signer.SignedAttributes.Add(new AsnEncodedData(CadesSignature.SigningCertificateV2Oid, CadesSignature.EncodeSigningCertificateV2(certificate)));
            token.ComputeSignature(signer);

            // TimeStampResp ::= SEQUENCE { status PKIStatusInfo, timeStampToken ContentInfo }
            var writer = new AsnWriter(AsnEncodingRules.DER);

            using (writer.PushSequence())
            {
                using (writer.PushSequence())
                {
                    writer.WriteInteger(0);
                }

                writer.WriteEncodedValue(token.Encode());
            }

            var content = new ByteArrayContent(writer.Encode());

            content.Headers.ContentType = new MediaTypeHeaderValue("application/timestamp-reply");

            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        }
    }

    private sealed class FailingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
    }
}
