using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Trinix.Bundle.Tests;

/// <summary>
///     The <c>.tdi</c> footer parser, which is the first thing to touch an untrusted
///     distribution image.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ Only the read side is covered. <see cref="DistributionImage.PackAsync" />
///         shells out to <c>mkfs.erofs</c>, which is not on a developer's Mac and is
///         not worth installing on a CI runner for a unit test — packing belongs to the
///         container tier. Reading is the half that matters here anyway: every offset
///         in this footer is used to seek into a file somebody else wrote, and it is
///         read <i>before</i> the image is mounted, precisely so that a hostile
///         filesystem never reaches the kernel's EROFS driver.
///     </para>
///     <para>
///         So the footers below are written by hand, byte by byte, rather than produced
///         by the packer. That is the point: a hand-written footer is what an attacker
///         supplies, and a test that could only construct well-formed input would never
///         exercise the bounds checks at all.
///     </para>
/// </remarks>
public class DistributionImageFooterTests {
    /// <summary>
    ///     Assemble a <c>.tdi</c>-shaped file: payload, signature, certificates, footer.
    /// </summary>
    /// <remarks>
    ///     The layout mirrors what <c>PackAsync</c> writes. Each parameter is nullable
    ///     so that one field at a time can be made to lie while the rest stays
    ///     coherent — a footer that is wrong in several ways at once tells you nothing
    ///     about which check caught it.
    /// </remarks>
    static string WriteImage(
        int payloadLength = 4096,
        int signatureLength = 71,
        int certificatesLength = 512,
        long? declaredPayloadLength = null,
        long? signatureOffset = null,
        long? certificatesOffset = null,
        int? declaredSignatureLength = null,
        int? declaredCertificatesLength = null,
        uint version = DistributionImage.FormatVersion,
        ReadOnlySpan<byte> magic = default
    ) {
        var payload = new byte[payloadLength];
        RandomNumberGenerator.Fill(payload);
        var signature = new byte[signatureLength];
        var certificates = new byte[certificatesLength];

        var body = new byte[payload.Length + signature.Length + certificates.Length];
        payload.CopyTo(body, 0);
        signature.CopyTo(body, payload.Length);
        certificates.CopyTo(body, payload.Length + signature.Length);

        var footer = new byte[DistributionImage.FooterLength];
        BinaryPrimitives.WriteInt64LittleEndian(footer, declaredPayloadLength ?? payload.Length);
        SHA256.HashData(payload).CopyTo(footer.AsSpan(8));
        BinaryPrimitives.WriteInt64LittleEndian(footer.AsSpan(40), signatureOffset ?? payload.Length);
        BinaryPrimitives.WriteInt32LittleEndian(footer.AsSpan(48), declaredSignatureLength ?? signature.Length);
        BinaryPrimitives.WriteInt32LittleEndian(footer.AsSpan(52), declaredCertificatesLength ?? certificates.Length);
        BinaryPrimitives.WriteInt64LittleEndian(
            footer.AsSpan(56),
            certificatesOffset ?? payload.Length + signature.Length
        );
        BinaryPrimitives.WriteUInt32LittleEndian(footer.AsSpan(80), version);
        (magic.IsEmpty ? DistributionImage.Magic : magic).CopyTo(footer.AsSpan(88));

        var path = TempFile.WithContent(string.Empty);
        using (var stream = File.Create(path)) {
            stream.Write(body);
            stream.Write(footer);
        }

        return path;
    }

    [Fact]
    public void ReadsAWellFormedFooter() {
        var footer = DistributionImage.ReadFooter(WriteImage());

        Assert.Equal(4096, footer.PayloadLength);
        Assert.Equal(32, footer.PayloadSha256.Length);
        Assert.Equal(4096, footer.SignatureOffset);
        Assert.Equal(71, footer.SignatureLength);
        Assert.Equal(4096 + 71, footer.CertificatesOffset);
        Assert.Equal(512, footer.CertificatesLength);
    }

    [Fact]
    public void TheFooterIsExactlyNinetySixBytesAndEndsInTheMagic() {
        // Both are format constants: the reader seeks backwards by the length
        // and then checks the last eight bytes, so either one changing is a
        // format change rather than an implementation detail.
        Assert.Equal(96, DistributionImage.FooterLength);
        Assert.Equal("TRINIXDI", Encoding.UTF8.GetString(DistributionImage.Magic));
        Assert.Equal(8, DistributionImage.Magic.Length);
    }

    [Fact]
    public void AFileTooShortToHoldAFooterIsRefused() {
        var path = TempFile.WithContent("not a distribution image");

        var e = Assert.Throws<BundleException>(() => DistributionImage.ReadFooter(path));
        Assert.Equal(BundleFailure.MalformedImage, e.Failure);
        Assert.Contains("too short", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AFileWithoutTheMagicIsRefused() {
        var path = WriteImage(magic: "NOTATDI!"u8);

        var e = Assert.Throws<BundleException>(() => DistributionImage.ReadFooter(path));
        Assert.Equal(BundleFailure.MalformedImage, e.Failure);
        Assert.Contains("does not end in a Trinix distribution-image footer", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AFutureFormatVersionIsRefusedWithBothVersionsNamed() {
        // The marker exists so that a newer .tdi is refused as "newer" rather
        // than as "corrupt", which is the difference between "update your
        // system" and "redownload this file".
        var path = WriteImage(version: DistributionImage.FormatVersion + 1);

        var e = Assert.Throws<BundleException>(() => DistributionImage.ReadFooter(path));
        Assert.Contains("format version 2", e.Message, StringComparison.Ordinal);
        Assert.Contains("this system understands 1", e.Message, StringComparison.Ordinal);
    }

    // ⚠ Every case below breaks exactly one offset or length in an otherwise
    // coherent footer. Each of those values is used to seek into a file the
    // caller did not write, and is read before the image is mounted — so a
    // footer whose numbers do not fit inside the file has to be refused before
    // anything acts on them.

    [Fact]
    public void APayloadLongerThanTheFileIsRefused() {
        var path = WriteImage(declaredPayloadLength: 1 << 30);

        var e = Assert.Throws<BundleException>(() => DistributionImage.ReadFooter(path));
        Assert.Contains("offsets do not fit the file", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AZeroLengthPayloadIsRefused() {
        // An image with no filesystem in it is not an image, and zero would
        // otherwise pass every subsequent bound.
        var path = WriteImage(declaredPayloadLength: 0);

        Assert.Throws<BundleException>(() => DistributionImage.ReadFooter(path));
    }

    [Fact]
    public void ANegativePayloadLengthIsRefused() {
        var path = WriteImage(declaredPayloadLength: -1);

        Assert.Throws<BundleException>(() => DistributionImage.ReadFooter(path));
    }

    [Fact]
    public void ASignatureThatOverlapsThePayloadIsRefused() {
        // Overlapping would let bytes covered by the payload hash be reread as
        // the signature over that hash.
        var path = WriteImage(signatureOffset: 2048);

        Assert.Throws<BundleException>(() => DistributionImage.ReadFooter(path));
    }

    [Fact]
    public void ASignatureRunningPastTheEndIsRefused() {
        var path = WriteImage(declaredSignatureLength: 60 * 1024);

        Assert.Throws<BundleException>(() => DistributionImage.ReadFooter(path));
    }

    [Fact]
    public void AnAbsurdlyLongSignatureIsRefusedOnItsFaceValue() {
        // 64 KiB is far more than any ECDSA signature and the bound is there so
        // that a length is never turned into an allocation.
        var path = WriteImage(declaredSignatureLength: 64 * 1024);

        Assert.Throws<BundleException>(() => DistributionImage.ReadFooter(path));
    }

    [Fact]
    public void AZeroLengthSignatureIsRefused() {
        var path = WriteImage(declaredSignatureLength: 0);

        Assert.Throws<BundleException>(() => DistributionImage.ReadFooter(path));
    }

    [Fact]
    public void CertificatesOverlappingThePayloadAreRefused() {
        var path = WriteImage(certificatesOffset: 100);

        Assert.Throws<BundleException>(() => DistributionImage.ReadFooter(path));
    }

    [Fact]
    public void CertificatesRunningPastTheEndAreRefused() {
        var path = WriteImage(declaredCertificatesLength: 900 * 1024);

        Assert.Throws<BundleException>(() => DistributionImage.ReadFooter(path));
    }

    [Fact]
    public void AZeroLengthCertificateChainIsRefused() {
        var path = WriteImage(declaredCertificatesLength: 0);

        Assert.Throws<BundleException>(() => DistributionImage.ReadFooter(path));
    }

    [Fact]
    public void ReadFooterRejectsANullPath() {
        Assert.Throws<ArgumentNullException>(() => DistributionImage.ReadFooter(null!));
    }

    // ---------------------------------------------------------------------
    // The signed descriptor.
    // ---------------------------------------------------------------------

    [Fact]
    public void TheSignedDescriptorCarriesThePrefixTheLengthAndTheHash() {
        var hash = SHA256.HashData("payload"u8);

        var descriptor = DistributionImage.SignedDescriptor(4096, hash);

        Assert.Equal("trinix-tdi/1\n"u8.ToArray(), descriptor[..13]);
        Assert.Equal(4096, BinaryPrimitives.ReadInt64LittleEndian(descriptor.AsSpan(13)));
        Assert.Equal(hash, descriptor[21..]);
        Assert.Equal(13 + 8 + 32, descriptor.Length);
    }

    [Fact]
    public void TheSignedDescriptorBindsTheLengthAsWellAsTheHash() {
        // ⚠ Without the length, an image whose payload could be re-declared as
        // longer would let signed bytes be reinterpreted as a different
        // filesystem — the hash still matches the prefix it covers.
        var hash = SHA256.HashData("payload"u8);

        Assert.NotEqual(
            DistributionImage.SignedDescriptor(4096, hash),
            DistributionImage.SignedDescriptor(8192, hash)
        );
    }

    [Fact]
    public void TheBlockSizeIsPinnedRatherThanTakenFromTheBuildMachine() {
        // ⚠ EROFS refuses to mount an image whose block size exceeds the
        // kernel's page size. A container on an Apple Silicon Mac has 16 KiB
        // pages, and an image built there with the default would be unmountable
        // on every Trinix machine.
        Assert.Equal(4096, DistributionImage.BlockSize);
    }

    [Fact]
    public void TheImageExtensionIsTdi() {
        Assert.Equal(".tdi", BundleLayout.ImageExtension);
    }
}
