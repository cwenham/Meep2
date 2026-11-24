using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Meep2.SDK.Security;

namespace Meep2.Runtime.Verifying;

/// <summary>
/// The checks of an X.509 certificate: a chain from it to a certificate in the trust store's <c>roots/</c>, built by
/// .NET's <see cref="X509Chain"/> with only those roots trusted, every certificate on it in date and allowed to do what
/// it does, and none revoked by the store's own list or CRLs (Q274, Q275).
/// </summary>
/// <remarks>
/// Every certificate in <c>roots/</c> is an anchor, including one that isn't self-signed, such as a department's CA
/// trusted without its parent. Where .NET's custom root trust won't end a chain at such a certificate, the chain it
/// built is cut at the first certificate that is in <c>roots/</c>, and only what's on it up to there counts. Revocation
/// is checked against the store alone, never online, so it works the same on every system.
/// </remarks>
internal static class CertificateCheck
{
    private const string ClientAuthentication = "1.3.6.1.5.5.7.3.2";
    private const string ServerAuthentication = "1.3.6.1.5.5.7.3.1";

    // What only says the chain didn't reach a trusted root, which is checked separately, or that revocation wasn't
    // checked online, which it never is.
    private const X509ChainStatusFlags NotAboutTheChain = X509ChainStatusFlags.UntrustedRoot | X509ChainStatusFlags.PartialChain
        | X509ChainStatusFlags.RevocationStatusUnknown | X509ChainStatusFlags.OfflineRevocation | X509ChainStatusFlags.NotTimeNested
        | X509ChainStatusFlags.CtlNotTimeValid | X509ChainStatusFlags.CtlNotSignatureValid | X509ChainStatusFlags.CtlNotValidForUsage;

    /// <summary>Checks <paramref name="certificate"/>, adding what it finds to <paramref name="verification"/>.</summary>
    public static void Check(Verification verification, Certificate certificate, CertificateUsage usage)
    {
        var store = verification.Store;
        using var leaf = certificate.ToX509();
        var intermediates = new List<X509Certificate2>();
        try
        {
            foreach (var der in certificate.Intermediates)
            {
                try
                {
                    intermediates.Add(X509CertificateLoader.LoadCertificate(der.Span));
                }
                catch (CryptographicException exception)
                {
                    verification.Fail(VerificationFailureCode.Malformed, $"An intermediate sent with {certificate.Subject} can't be read: {exception.Message.TrimEnd('.')}.");
                }
            }
            using var chain = new X509Chain();
            var policy = chain.ChainPolicy;
            policy.TrustMode = X509ChainTrustMode.CustomRootTrust;
            policy.CustomTrustStore.AddRange(store.RootCollection());
            policy.ExtraStore.AddRange(intermediates.ToArray());
            policy.RevocationMode = X509RevocationMode.NoCheck;
            policy.DisableCertificateDownloads = true;
            policy.VerificationTime = verification.Now.ToLocalTime();
            policy.VerificationTimeIgnored = false;
            if (usage != CertificateUsage.Any)
                policy.ApplicationPolicy.Add(new Oid(usage == CertificateUsage.Client ? ClientAuthentication : ServerAuthentication));
            chain.Build(leaf);
            try
            {
                CheckChain(verification, chain, usage);
            }
            finally
            {
                chain.Reset(); // disposes the certificates on it
            }
        }
        finally
        {
            foreach (var intermediate in intermediates)
                intermediate.Dispose();
        }
    }

    private static void CheckChain(Verification verification, X509Chain chain, CertificateUsage usage)
    {
        var store = verification.Store;
        var roots = store.Roots.Select(root => root.Certificate.GetCertHashString(HashAlgorithmName.SHA256)).ToHashSet(StringComparer.Ordinal);
        var elements = chain.ChainElements;
        var anchor = -1;
        for (var i = 0; i < elements.Count; i++)
        {
            if (roots.Contains(elements[i].Certificate.GetCertHashString(HashAlgorithmName.SHA256)))
            {
                anchor = i;
                break;
            }
        }

        if (anchor < 0)
        {
            var status = chain.ChainStatus.Aggregate(X509ChainStatusFlags.NoError, (all, s) => all | s.Status);
            var top = elements.Count > 0 ? elements[^1].Certificate : null;
            if ((status & X509ChainStatusFlags.PartialChain) != 0 || top is null || top.SubjectName.RawData.AsSpan().SequenceEqual(top.IssuerName.RawData) is false)
                verification.Fail(VerificationFailureCode.ChainIncomplete, $"The chain from {Leaf(elements)} stops at {top?.Subject ?? "it"}, whose issuer, {top?.Issuer}, wasn't sent and isn't trusted.");
            else
                verification.Fail(VerificationFailureCode.UntrustedRoot, $"The chain from {Leaf(elements)} ends at {top.Subject}, which isn't among the trust store's roots.");
        }

        // Only the chain up to the anchor counts: anything .NET found beyond it is ignored.
        var last = anchor < 0 ? elements.Count - 1 : anchor;
        for (var i = 0; i <= last; i++)
        {
            var element = elements[i];
            var certificate = element.Certificate;
            foreach (var status in element.ChainElementStatus)
            {
                if ((status.Status & ~NotAboutTheChain) != 0)
                    Translate(verification, status.Status & ~NotAboutTheChain, certificate, usage, status.StatusInformation);
            }
            verification.ReliesOnUntil(certificate.NotAfter.ToUniversalTime());
            if (store.Revocations.WhyRevoked(certificate) is { } revoked)
                verification.Fail(VerificationFailureCode.Revoked, revoked);
            if (i < last || anchor < 0)
                CheckRevocationList(verification, certificate);
        }
    }

    // A certificate's issuer's CRL must be in date, or nobody can know it isn't revoked (Q274). Only issuers with a CRL
    // in the store are checked.
    private static void CheckRevocationList(Verification verification, X509Certificate2 certificate)
    {
        if (verification.Store.Revocations.NextUpdate(certificate.IssuerName) is not { } due)
            return;
        due = due.ToUniversalTime();
        if (due <= verification.Now)
        {
            verification.Fail(VerificationFailureCode.RevocationUnknown,
                $"The revocation list from {certificate.Issuer} was due to be replaced at {CredentialCheck.Time(due)}, so it can't be known whether {certificate.Subject} is revoked. Add a new one with: meep trust revoke <file.crl>");
        }
        verification.ReliesOnUntil(due);
    }

    private static void Translate(Verification verification, X509ChainStatusFlags status, X509Certificate2 certificate, CertificateUsage usage, string information)
    {
        var subject = certificate.Subject;
        var now = verification.Now;
        if ((status & X509ChainStatusFlags.NotTimeValid) != 0)
        {
            if (certificate.NotBefore.ToUniversalTime() > now)
                verification.Fail(VerificationFailureCode.NotYetValid, $"The certificate {subject} isn't valid until {CredentialCheck.Time(certificate.NotBefore)}.");
            else
                verification.Fail(VerificationFailureCode.Expired, $"The certificate {subject} expired at {CredentialCheck.Time(certificate.NotAfter)}.");
            status &= ~X509ChainStatusFlags.NotTimeValid;
        }
        if ((status & X509ChainStatusFlags.Revoked) != 0)
        {
            verification.Fail(VerificationFailureCode.Revoked, $"The certificate {subject} is revoked.");
            status &= ~X509ChainStatusFlags.Revoked;
        }
        if ((status & (X509ChainStatusFlags.NotSignatureValid | X509ChainStatusFlags.HasWeakSignature)) != 0)
        {
            verification.Fail(VerificationFailureCode.BadSignature, $"The certificate {subject}'s signature isn't good{((status & X509ChainStatusFlags.HasWeakSignature) != 0 ? ", or is made with a weak algorithm" : "")}.");
            status &= ~(X509ChainStatusFlags.NotSignatureValid | X509ChainStatusFlags.HasWeakSignature);
        }
        if ((status & X509ChainStatusFlags.NotValidForUsage) != 0)
        {
            verification.Fail(VerificationFailureCode.WrongUsage, usage == CertificateUsage.Any
                ? $"The certificate {subject} isn't allowed to be used this way."
                : $"The certificate {subject} isn't allowed to identify a {(usage == CertificateUsage.Client ? "client" : "server")}. Use usage=any if it needn't.");
            status &= ~X509ChainStatusFlags.NotValidForUsage;
        }
        const X509ChainStatusFlags constraints = X509ChainStatusFlags.InvalidBasicConstraints | X509ChainStatusFlags.InvalidNameConstraints
            | X509ChainStatusFlags.HasNotSupportedNameConstraint | X509ChainStatusFlags.HasNotDefinedNameConstraint
            | X509ChainStatusFlags.HasNotPermittedNameConstraint | X509ChainStatusFlags.HasExcludedNameConstraint
            | X509ChainStatusFlags.InvalidPolicyConstraints | X509ChainStatusFlags.NoIssuanceChainPolicy;
        if ((status & constraints) != 0)
        {
            verification.Fail(VerificationFailureCode.WrongUsage, $"The certificate {subject} breaks a constraint of the chain: {information.Trim()}");
            status &= ~constraints;
        }
        if ((status & X509ChainStatusFlags.ExplicitDistrust) != 0)
        {
            verification.Fail(VerificationFailureCode.UntrustedRoot, $"The certificate {subject} is explicitly distrusted.");
            status &= ~X509ChainStatusFlags.ExplicitDistrust;
        }
        if (status != 0)
            verification.Fail(VerificationFailureCode.Malformed, $"The certificate {subject} can't be used: {information.Trim()}");
    }

    private static string Leaf(X509ChainElementCollection elements) => elements.Count > 0 ? elements[0].Certificate.Subject : "the certificate";
}
