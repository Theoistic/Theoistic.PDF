using System.Globalization;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Theoistic.PDF.Writing;

namespace Theoistic.PDF.Editing;

/// <summary>
/// Applies a PAdES digital signature to an existing PDF.
/// <para>
/// The signature is appended as an incremental update and covers the whole file as it stands,
/// so anything signed earlier keeps verifying for its own revision. It is invisible - a signature
/// field with no appearance on the first page - which is what a seal applied by a system rather
/// than a hand calls for.
/// </para>
/// </summary>
public static class PdfSigner
{
    // Room for "[0 a b c]" with three ten-digit numbers - files up to 9.99 GB - padded with spaces
    // when the numbers are shorter. The width has to be fixed before the offsets are known.
    private const int ByteRangeWidth = 36;

    /// <summary>
    /// Appends an incremental update holding an invisible signature field and a PAdES signature
    /// (<c>/Filter /Adobe.PPKLite</c>, <c>/SubFilter /ETSI.CAdES.detached</c>) over the whole
    /// file, and returns the signed bytes.
    /// </summary>
    public static Task<byte[]> SignAsync(byte[] pdf, PdfSignatureOptions options, CancellationToken cancellationToken = default) =>
        SignAsync(pdf, options, initialReserve: null, cancellationToken);

    /// <param name="initialReserve">
    /// Bytes to reserve for the signature on the first attempt, instead of the estimate; lets a
    /// test force the placeholder to be too small.
    /// </param>
    internal static async Task<byte[]> SignAsync(
        byte[] pdf,
        PdfSignatureOptions options,
        int? initialReserve,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(pdf);
        ArgumentNullException.ThrowIfNull(options);

        X509Certificate2 certificate = options.Certificate ?? throw new ArgumentException(
            "A signing certificate is required.", nameof(options));

        CheckKey(certificate);

        IReadOnlyList<X509Certificate2> extraCertificates = options.ExtraCertificates ?? [];
        PdfFile file = PdfFile.Open(pdf);
        List<PdfPageNode> pages = PageTree.Read(file);

        if (pages.Count == 0)
        {
            throw new PdfFormatException("The PDF has no pages, so there is nowhere to put a signature field.");
        }

        PdfPageNode firstPage = pages[0];
        PdfReference pageReference = firstPage.Reference ?? throw new PdfFormatException(
            "The first page is not an indirect object, so a signature field cannot be attached to it.");

        string fieldName = ChooseFieldName(file, options.FieldName);
        int reserve = initialReserve ?? EstimateSize(certificate, extraCertificates, options.TimestampAuthority is not null);

        for (int attempt = 1; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            Prepared prepared = Prepare(file, options, firstPage, pageReference, fieldName, reserve);
            SignedCms cms = CadesSignature.Sign(prepared.SignedContent(), certificate, extraCertificates);

            if (options.TimestampAuthority is Uri authority)
            {
                await CadesSignature.AddTimestampAsync(
                    cms,
                    authority,
                    options.HttpClient ?? CadesSignature.DefaultHttpClient,
                    cancellationToken).ConfigureAwait(false);
            }

            byte[] signature = cms.Encode();

            if (signature.Length <= reserve)
            {
                prepared.Embed(signature);

                return prepared.Bytes;
            }

            if (attempt == 3)
            {
                throw new InvalidOperationException(
                    $"The signature ({signature.Length} bytes) still did not fit the space reserved for it ({reserve} bytes) after {attempt} attempts.");
            }

            // The space has to be reserved before the digest is taken, so a signature that does
            // not fit - an unusually long chain, a large timestamp token - means writing the
            // update again with room for what was actually produced, plus some slack for the
            // few bytes an ECDSA signature or a new token can vary by.
            reserve = signature.Length + 2048;
        }
    }

    /// <summary>
    /// Writes the update with placeholders where the byte range and the signature go, and fills
    /// in the byte range, which can only be known once everything around it has been written.
    /// </summary>
    private static Prepared Prepare(
        PdfFile file,
        PdfSignatureOptions options,
        PdfPageNode page,
        PdfReference pageReference,
        string fieldName,
        int reserve)
    {
        var update = new IncrementalUpdate(file);

        // Hex digits of zeros, two per byte of signature, inside the angle brackets that the byte
        // range excludes along with them.
        byte[] contentsPlaceholder = new byte[(reserve * 2) + 2];

        contentsPlaceholder.AsSpan().Fill((byte)'0');
        contentsPlaceholder[0] = (byte)'<';
        contentsPlaceholder[^1] = (byte)'>';

        var contents = new PdfPlaceholder(contentsPlaceholder);
        var byteRange = new PdfPlaceholder(Encoding.ASCII.GetBytes(ByteRangeText(0, 0, 0)));

        // Written as an ordinary, uncompressed object, never inside an object stream: the byte
        // range has to point into the file itself, and verifiers look for /Contents there.
        var signature = new PdfDictionary()
            .SetName("Type", "Sig")
            .SetName("Filter", "Adobe.PPKLite")
            .SetName("SubFilter", "ETSI.CAdES.detached")
            .Set("ByteRange", byteRange)
            .Set("Contents", contents)
            .Set("M", PdfString.Latin1(PdfText.FormatDate(options.SigningTime)));

        SetText(signature, "Name", options.Name);
        SetText(signature, "Reason", options.Reason);
        SetText(signature, "Location", options.Location);
        SetText(signature, "ContactInfo", options.ContactInfo);

        PdfReference signatureReference = update.Add(signature);

        // A field and its widget annotation merged into one dictionary, the usual form for a
        // field with a single widget. A zero-size rectangle makes it invisible; flags 132 are
        // Print and Locked, so the field cannot be moved or deleted in a viewer.
        var field = new PdfDictionary()
            .SetName("Type", "Annot")
            .SetName("Subtype", "Widget")
            .SetName("FT", "Sig")
            .Set("T", PdfText.Encode(fieldName))
            .Set("V", signatureReference)
            .Set("Rect", new PdfArray(new PdfObject[] { new PdfInteger(0), new PdfInteger(0), new PdfInteger(0), new PdfInteger(0) }))
            .Set("F", 132)
            .Set("P", pageReference);

        PdfReference fieldReference = update.Add(field);

        // The page lists the widget among its annotations. The array is copied rather than
        // changed: an indirect /Annots array could be shared, and the original must not change.
        PdfDictionary rewrittenPage = PdfEditor.CopyOf(page.Dictionary);
        var annotations = new PdfArray(file.Resolve(rewrittenPage["Annots"]) as PdfArray ?? new PdfArray());

        annotations.Add(fieldReference);
        rewrittenPage.Set("Annots", annotations);
        update.Replace(pageReference, rewrittenPage);

        // The form lists the field, keeping every field and setting it already had. SigFlags 3
        // says the document has signatures and is to be changed only by appending.
        PdfDictionary catalog = file.Catalog;
        PdfObject? formValue = catalog["AcroForm"];
        PdfDictionary form = PdfEditor.CopyOf(file.Resolve(formValue) as PdfDictionary);
        var fields = new PdfArray(file.Resolve(form["Fields"]) as PdfArray ?? new PdfArray());

        fields.Add(fieldReference);
        form.Set("Fields", fields);
        form.Set("SigFlags", 3);

        if (formValue is PdfReference formReference && file.Resolve(formReference) is PdfDictionary)
        {
            update.Replace(formReference, form);
        }
        else
        {
            PdfDictionary rewrittenCatalog = PdfEditor.CopyOf(catalog);

            rewrittenCatalog.Set("AcroForm", form);
            update.Replace(file.RootReference, rewrittenCatalog);
        }

        byte[] bytes = update.Write();

        long gapStart = contents.Offset;
        long gapEnd = gapStart + contents.Length;

        byte[] range = Encoding.ASCII.GetBytes(ByteRangeText(gapStart, gapEnd, bytes.Length - gapEnd));

        range.CopyTo(bytes, byteRange.Offset);

        return new Prepared(bytes, gapStart, gapEnd);
    }

    private static string ByteRangeText(long gapStart, long gapEnd, long tail) =>
        string.Create(CultureInfo.InvariantCulture, $"[0 {gapStart} {gapEnd} {tail}]").PadRight(ByteRangeWidth);

    private static void SetText(PdfDictionary dictionary, string key, string? value)
    {
        if (!string.IsNullOrEmpty(value))
        {
            dictionary.Set(key, PdfText.Encode(value));
        }
    }

    private static void CheckKey(X509Certificate2 certificate)
    {
        if (!certificate.HasPrivateKey)
        {
            throw new ArgumentException(
                "The signing certificate has no private key. Load it from a PFX or a certificate store entry that includes the key.",
                "options");
        }

        using RSA? rsa = certificate.GetRSAPrivateKey();
        using ECDsa? ecdsa = rsa is null ? certificate.GetECDsaPrivateKey() : null;

        if (rsa is null && ecdsa is null)
        {
            throw new NotSupportedException("Only RSA and ECDSA signing keys are supported.");
        }
    }

    /// <summary>
    /// The space to reserve for the encoded signature. The certificates dominate; the signature
    /// value, attributes and structure fit comfortably in 2 KB even for a 4096-bit RSA key, and a
    /// timestamp token carries the authority's own certificates, which commonly run to several KB.
    /// </summary>
    private static int EstimateSize(X509Certificate2 certificate, IReadOnlyList<X509Certificate2> extraCertificates, bool timestamped)
    {
        int size = certificate.RawData.Length + 2048;

        foreach (X509Certificate2 extra in extraCertificates)
        {
            size += extra.RawData.Length;
        }

        return timestamped ? size + 10240 : size;
    }

    private static string ChooseFieldName(PdfFile file, string? requested)
    {
        var existing = new HashSet<string>(
            FormFields.Read(file).Where(field => field.HasOwnName).Select(field => field.FullName),
            StringComparer.Ordinal);

        if (requested is not null)
        {
            if (requested.Length == 0 || requested.Contains('.'))
            {
                throw new ArgumentException(
                    "A signature field name cannot be empty or contain a period, which separates the parts of a qualified field name.",
                    "options");
            }

            if (existing.Contains(requested))
            {
                throw new ArgumentException($"The form already has a field named '{requested}'.", "options");
            }

            return requested;
        }

        for (int n = 1; ; n++)
        {
            string name = "Signature" + n.ToString(CultureInfo.InvariantCulture);

            if (!existing.Contains(name))
            {
                return name;
            }
        }
    }

    /// <summary>A written update waiting for its signature.</summary>
    private sealed class Prepared
    {
        private readonly long gapStart;
        private readonly long gapEnd;

        public Prepared(byte[] bytes, long gapStart, long gapEnd)
        {
            Bytes = bytes;
            this.gapStart = gapStart;
            this.gapEnd = gapEnd;
        }

        public byte[] Bytes { get; }

        /// <summary>The bytes the signature covers: everything except the /Contents value.</summary>
        public byte[] SignedContent()
        {
            byte[] content = new byte[gapStart + (Bytes.Length - gapEnd)];

            Array.Copy(Bytes, 0, content, 0, gapStart);
            Array.Copy(Bytes, gapEnd, content, gapStart, Bytes.Length - gapEnd);

            return content;
        }

        /// <summary>Writes the signature into the placeholder; the zeros after it are padding.</summary>
        public void Embed(byte[] signature)
        {
            byte[] hex = Encoding.ASCII.GetBytes(Convert.ToHexString(signature));

            hex.CopyTo(Bytes, gapStart + 1);
        }
    }
}
