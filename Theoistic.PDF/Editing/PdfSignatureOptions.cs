using System.Security.Cryptography.X509Certificates;

namespace Theoistic.PDF.Editing;

/// <summary>What <see cref="PdfSigner"/> signs with, and what it records alongside the signature.</summary>
public sealed class PdfSignatureOptions
{
    /// <summary>The signer's certificate. It must carry its private key, RSA or ECDSA.</summary>
    public required X509Certificate2 Certificate { get; init; }

    /// <summary>
    /// Intermediate certificates to embed, so a verifier can build the chain to a root it trusts
    /// without fetching anything. The signer's own certificate is always embedded.
    /// </summary>
    public IReadOnlyList<X509Certificate2> ExtraCertificates { get; init; } = [];

    /// <summary>The signer's name as shown by a viewer (/Name).</summary>
    public string? Name { get; init; }

    /// <summary>Why the document was signed (/Reason).</summary>
    public string? Reason { get; init; }

    /// <summary>Where it was signed (/Location).</summary>
    public string? Location { get; init; }

    /// <summary>How to reach the signer (/ContactInfo).</summary>
    public string? ContactInfo { get; init; }

    /// <summary>
    /// The claimed signing time, written as /M. PAdES keeps the time here rather than in the
    /// signature, where a signing-time attribute is not allowed; a trusted time comes from
    /// <see cref="TimestampAuthority"/>.
    /// </summary>
    public DateTimeOffset SigningTime { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// An RFC 3161 time-stamping service. When set, the signature carries a timestamp over its
    /// own value from that service (PAdES B-T), which proves it existed at that time.
    /// </summary>
    public Uri? TimestampAuthority { get; init; }

    /// <summary>The client for <see cref="TimestampAuthority"/>; a shared private one when null.</summary>
    public HttpClient? HttpClient { get; init; }

    /// <summary>
    /// The signature field's name. When null, the first of <c>Signature1</c>, <c>Signature2</c>, ...
    /// that the form does not already use.
    /// </summary>
    public string? FieldName { get; init; }
}
