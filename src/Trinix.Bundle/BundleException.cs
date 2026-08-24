namespace Trinix.Bundle;

/// <summary>
///     Why a bundle was refused.
/// </summary>
/// <remarks>
///     Enumerated rather than left to the exception message, because the launcher
///     has to tell a person what went wrong in one line and the difference between
///     "this was tampered with" and "this was signed by someone we do not know" is
///     the entire content of that line. It is also the difference between an
///     incident and a configuration mistake.
/// </remarks>
public enum BundleFailure {
    /// <summary>Nothing is wrong.</summary>
    None = 0,

    /// <summary>The path is not a bundle: no <c>Contents</c>, or no <c>Info.json</c>.</summary>
    NotABundle,

    /// <summary><c>Info.json</c> is unparseable or says something impossible.</summary>
    MalformedInfo,

    /// <summary>The bundle carries no signature at all.</summary>
    NotSigned,

    /// <summary>The signature material is present but unreadable.</summary>
    MalformedSignature,

    /// <summary>There is no trust store to check against.</summary>
    NoTrustStore,

    /// <summary>The signer's certificate does not chain to a root Trinix trusts.</summary>
    UntrustedSigner,

    /// <summary>The chain is trusted, but the signature over the manifest is not valid.</summary>
    BadSignature,

    /// <summary>The signature is valid, but the bundle's contents are not what it covers.</summary>
    ContentMismatch,

    /// <summary>The manifest names a file that is not there, or the bundle has one it does not name.</summary>
    FileSetMismatch,

    /// <summary>The bundle contains something that is not a regular file.</summary>
    UnsupportedEntry,

    /// <summary>The distribution image is not a <c>.tdi</c>, or its footer is damaged.</summary>
    MalformedImage,

    /// <summary>The operation needs privileges the caller does not have.</summary>
    NotPermitted
}

/// <summary>An error with a reason the caller can act on.</summary>
public sealed class BundleException : Exception {
    /// <summary>Why the bundle was refused.</summary>
    public BundleFailure Failure { get; } = BundleFailure.None;

    /// <summary>Create one with a reason.</summary>
    public BundleException(BundleFailure failure, string message) : base(message) {
        Failure = failure;
    }

    /// <summary>Create one with a reason and an underlying cause.</summary>
    public BundleException(BundleFailure failure, string message, Exception innerException)
        : base(message, innerException) {
        Failure = failure;
    }

    /// <summary>Required by the exception guidelines; never thrown by Trinix itself.</summary>
    public BundleException() { }

    /// <summary>Required by the exception guidelines; never thrown by Trinix itself.</summary>
    public BundleException(string message) : base(message) { }

    /// <summary>Required by the exception guidelines; never thrown by Trinix itself.</summary>
    public BundleException(string message, Exception innerException) : base(message, innerException) { }
}
