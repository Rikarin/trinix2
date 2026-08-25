using Tmds.DBus.Protocol;
using Trinix.Bundle;

namespace Trinix.Services.Contracts.Tests;

/// <summary>
///     How a refusal is spelled on the wire, in both directions.
/// </summary>
/// <remarks>
///     A D-Bus error is a name and one string, so the permission a caller lacked has to be
///     encoded into that string. ⚠ The encoding is unambiguous only because a Trinix
///     permission name contains no colon and no space — a property of
///     <see cref="BundlePermissions" /> that this suite depends on and that
///     <see cref="EveryPermissionSurvivesTheEncoding" /> checks rather than assumes.
/// </remarks>
public sealed class ServiceErrorTests {
    /// <summary>Every permission in the vocabulary round-trips through the error message.</summary>
    /// <remarks>
    ///     The whole vocabulary rather than a sample, because the encoding's correctness is a
    ///     property of the <i>names</i> — the day somebody adds a permission with a space in
    ///     it, this is what says so.
    /// </remarks>
    [Fact]
    public void EveryPermissionSurvivesTheEncoding() {
        foreach (var name in BundlePermissions.Known) {
            Assert.True(BundlePermissions.TryParse(name, out var permission));

            var original = new PermissionDeniedException(permission, "doing the thing: with a colon in it");
            var restored = PermissionDeniedException.FromErrorMessage(original.ToErrorMessage());

            Assert.Equal(permission, restored.Permission);
            Assert.Equal(name, restored.PermissionName);
            Assert.Equal(original.Explanation, restored.Explanation);
        }
    }

    /// <summary>
    ///     A permission this build has never heard of still arrives as a refusal, with the
    ///     word.
    /// </summary>
    /// <remarks>
    ///     ⚠ The case that decides whether an application built against an older SDK can say
    ///     anything useful when a newer shell refuses it. <see cref="Permissions.None" />
    ///     alone reads as a bug; the name reads as "this needs a newer Trinix".
    /// </remarks>
    [Fact]
    public void AnUnknownPermissionKeepsItsName() {
        var restored = PermissionDeniedException.FromErrorMessage("devices.neural: running a model on the NPU");

        Assert.Equal(Permissions.None, restored.Permission);
        Assert.Equal("devices.neural", restored.PermissionName);
        Assert.Equal("running a model on the NPU", restored.Explanation);
    }

    /// <summary>A message that does not parse is still a refusal.</summary>
    [Fact]
    public void AMalformedMessageIsStillARefusal() {
        var restored = PermissionDeniedException.FromErrorMessage("no, and I shall not say why");

        Assert.Equal(Permissions.None, restored.Permission);
        Assert.Equal("", restored.PermissionName);
        Assert.Equal("no, and I shall not say why", restored.Explanation);
    }

    /// <summary>
    ///     An exception the service did not mean to raise tells the caller nothing about
    ///     itself.
    /// </summary>
    /// <remarks>
    ///     ⚠ Doc 04's threat model has the caller as the untrusted party. A reply carrying a
    ///     type name and a stack tells an application which code path it reached inside a
    ///     service running with more authority than it has; the full exception belongs in the
    ///     journal, where the person debugging it can see it and the application cannot.
    /// </remarks>
    [Fact]
    public void AnUnexpectedFailureLeaksNeitherTypeNorStack() {
        var (name, message) = ServiceErrors.ToErrorReply(
            new InvalidOperationException("the index is corrupt")
        );

        Assert.Equal("org.freedesktop.DBus.Error.Failed", name);
        Assert.Equal("the index is corrupt", message);
        Assert.DoesNotContain("InvalidOperationException", message, StringComparison.Ordinal);
    }

    /// <summary>The classifications a service gets for free.</summary>
    [Theory]
    [InlineData(typeof(ArgumentException), ServiceErrors.InvalidArgument)]
    [InlineData(typeof(NotSupportedException), ServiceErrors.Unsupported)]
    public void CommonFailuresGetTheRightErrorName(Type failure, string expected) {
        var (name, _) = ServiceErrors.ToErrorReply((Exception)Activator.CreateInstance(failure)!);
        Assert.Equal(expected, name);
    }

    /// <summary>An error name outside Trinix's table survives unchanged.</summary>
    /// <remarks>
    ///     Inventing a Trinix meaning for <c>org.freedesktop.DBus.Error.ServiceUnknown</c>
    ///     would lose the one word that tells a developer their daemon is not running.
    /// </remarks>
    [Fact]
    public void AForeignErrorNameIsNotReinterpreted() {
        var reply = new DBusErrorReplyException("org.freedesktop.DBus.Error.ServiceUnknown", "no such name");

        var translated = Assert.IsType<TrinixServiceException>(ServiceErrors.FromErrorReply(reply));

        Assert.Equal("org.freedesktop.DBus.Error.ServiceUnknown", translated.ErrorName);
        Assert.Equal("no such name", translated.Message);
    }
}
