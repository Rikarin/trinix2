using System.Reflection;
using Trinix.Services.Contracts.Generated;

namespace Trinix.Services.Contracts.Tests;

/// <summary>
///     The shape of the three contracts, asserted against the signatures they produce.
/// </summary>
/// <remarks>
///     <para>
///         These are change-detector tests and they are meant to be. Doc 02 § Failure,
///         restart and versioning makes an interface a versioned wire format shared between
///         a daemon and applications that outlive it, so a change to one of these signatures
///         is a compatibility event, not a refactor. A failing assertion here is not a test
///         to update — it is a prompt to bump
///         <see cref="TrinixServiceAttribute.Version" /> and to think about what is already
///         deployed.
///     </para>
///     <para>
///         ⚠ The two clipboard members that carry a file descriptor are asserted here and
///         nowhere else. <c>h</c> travels in <c>SCM_RIGHTS</c> on a Unix socket, and the
///         round-trip harness has no socket, so the signature is the only thing this suite
///         can check about them. Doc 18 R4 has the evidence that the transport carries them.
///     </para>
/// </remarks>
public sealed class ContractSurfaceTests {
    /// <summary>Doc 02's notification record, as a D-Bus signature.</summary>
    [Fact]
    public void TheNotificationRecordHasTheSignatureDocTwoDescribes() {
        Assert.Equal("(su)", ServiceWire.NotificationIdSignature);
        Assert.Equal("(ss)", ServiceWire.NotificationActionSignature);
        Assert.Equal("(ss)", ServiceWire.NotificationReplySignature);
        // title, body, urgency, actions[], reply?, group, expiry — in that order.
        Assert.Equal("(ssua(ss)a(ss)sx)", ServiceWire.NotificationRequestSignature);
    }

    /// <summary>An urgency is a <c>u</c>, not the <c>i</c> a bare C# enum would give.</summary>
    [Fact]
    public void UrgencyIsUnsignedOnTheWire() =>
        Assert.Contains("u", ServiceWire.NotificationRequestSignature, StringComparison.Ordinal);

    /// <summary>
    ///     ⚠ Two contract records with identical contents are <b>not</b> equal when one of
    ///     their members is a sequence.
    /// </summary>
    /// <remarks>
    ///     This is asserted rather than merely documented because it is a trap with a
    ///     plausible-looking wrong answer: an application avoiding a redundant
    ///     <see cref="INotifications.UpdateAsync" /> with <c>if (next == current) return;</c>
    ///     will never take that branch, and nothing will ever tell it so. C# generates
    ///     <c>Equals</c> from <c>EqualityComparer&lt;T&gt;.Default</c> per member, which for
    ///     an <c>IReadOnlyList&lt;T&gt;</c> is reference equality — and after a trip over the
    ///     bus the list is always a different object.
    ///     <para>
    ///         The day somebody makes these value-equal, this test fails and the change gets
    ///         the thought it deserves rather than arriving as a side effect.
    ///     </para>
    /// </remarks>
    [Fact]
    public void RecordsWithSequenceMembersAreNotValueEqual() {
        var left = new NotificationRequest(
            "t", "b", NotificationUrgency.Normal, [new NotificationAction("k", "K")], null, "", TimeSpan.Zero
        );
        var right = left with { Actions = [new NotificationAction("k", "K")] };

        Assert.NotEqual(left, right);

        // The members themselves, having no sequences, do compare by value.
        Assert.Equal(new NotificationAction("k", "K"), left.Actions[0]);
        Assert.Equal(new NotificationId("io.example.notes", 1), new NotificationId("io.example.notes", 1));
    }

    /// <summary>
    ///     A clipboard representation carries a descriptor, and that is visible in the
    ///     signature.
    /// </summary>
    [Fact]
    public void AClipboardRepresentationCarriesAFileDescriptor() =>
        Assert.Equal("(sh)", ServiceWire.ClipboardRepresentationSignature);

    /// <summary>The two members that pass descriptors say so in the introspection XML.</summary>
    [Fact]
    public void TheClipboardsDescriptorMembersAreDeclaredAsSuch() {
        Assert.Contains(
            "<arg name=\"result\" type=\"h\" direction=\"out\"/>",
            ClipboardIntrospection.NodeXml,
            StringComparison.Ordinal
        );
        Assert.Contains(
            "<arg name=\"representations\" type=\"a(sh)\" direction=\"in\"/>",
            ClipboardIntrospection.NodeXml,
            StringComparison.Ordinal
        );
    }

    /// <summary>The generated XML is a whole introspection document, not a fragment.</summary>
    [Fact]
    public void TheNodeDocumentIsWellFormedAndNamesItsPath() {
        var document = System.Xml.Linq.XDocument.Parse(NotificationsIntrospection.NodeXml);

        Assert.Equal("node", document.Root!.Name.LocalName);
        Assert.Equal(NotificationsProxy.DefaultObjectPath, document.Root.Attribute("name")!.Value);
        Assert.Equal(
            NotificationsProxy.InterfaceName,
            document.Root.Element("interface")!.Attribute("name")!.Value
        );
    }

    /// <summary>
    ///     Doc 01's rule, checked against the contracts rather than against the generator.
    /// </summary>
    /// <remarks>
    ///     The generator refuses a synchronous member, so in principle this cannot fail. It
    ///     is here anyway because the generator's own tests prove the <i>rule</i> is enforced
    ///     on a sample interface, and this proves it was enforced on the interfaces Trinix
    ///     actually ships — which would stop being true the moment somebody adds a service
    ///     surface that does not carry <c>[TrinixService]</c>.
    /// </remarks>
    [Theory]
    [InlineData(typeof(INotifications))]
    [InlineData(typeof(IClipboard))]
    [InlineData(typeof(ISystem))]
    public void EveryContractMemberIsAsynchronousAndCancellable(Type contract) {
        foreach (var member in contract.GetMethods(BindingFlags.Public | BindingFlags.Instance)) {
            Assert.True(
                member.ReturnType == typeof(Task) || member.ReturnType.IsGenericType
                && member.ReturnType.GetGenericTypeDefinition() == typeof(Task<>),
                $"{contract.Name}.{member.Name} does not return a Task."
            );

            var parameters = member.GetParameters();
            Assert.True(
                parameters.Length > 0 && parameters[^1].ParameterType == typeof(CancellationToken),
                $"{contract.Name}.{member.Name} does not take a CancellationToken last."
            );
        }
    }

    /// <summary>
    ///     Nothing on the service surface offers a way to ask whether a permission is held.
    /// </summary>
    /// <remarks>
    ///     ⚠ Doc 01's rule, and the reason it is worth a test rather than a comment: a
    ///     permission query is not wrong in a way that breaks anything, so nothing else would
    ///     ever catch it. It is wrong because it teaches applications to branch on authority
    ///     and produces two code paths of which one is never tested.
    /// </remarks>
    [Theory]
    [InlineData(typeof(INotifications))]
    [InlineData(typeof(IClipboard))]
    [InlineData(typeof(ISystem))]
    public void NoContractOffersAPermissionQuery(Type contract) {
        foreach (var member in contract.GetMethods(BindingFlags.Public | BindingFlags.Instance)) {
            Assert.DoesNotContain("Permission", member.Name, StringComparison.Ordinal);
        }
    }

    /// <summary>
    ///     The clipboard has no way to read the history, and that absence is the enforcement.
    /// </summary>
    /// <remarks>
    ///     Doc 02: "An application may read the current clipboard when focused and may never
    ///     read the history — the history is the shell's, and pasting from it is the user's
    ///     action." There is no permission in doc 04's vocabulary that grants it, because it
    ///     is not a capability Trinix offers.
    /// </remarks>
    [Fact]
    public void TheClipboardExposesNoHistory() {
        foreach (var member in typeof(IClipboard).GetMethods(BindingFlags.Public | BindingFlags.Instance)) {
            Assert.DoesNotContain("History", member.Name, StringComparison.Ordinal);
        }
    }
}
