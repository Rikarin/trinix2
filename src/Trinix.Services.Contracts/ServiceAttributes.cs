namespace Trinix.Services.Contracts;

/// <summary>
///     Marks a C# interface as the native face of a Trinix service.
/// </summary>
/// <remarks>
///     <para>
///         Doc 02 § The two-faces rule gives every service two front doors, and this
///         attribute is the one that faces Trinix. The C# interface is the source of
///         truth: <c>Trinix.Sdk.Generators</c> reads it and emits the client proxy, the
///         server dispatcher and the D-Bus introspection XML. Nothing transcribes XML
///         into C# in this direction — the compat faces go the other way round and are
///         hand-written precisely because their XML is somebody else's normative
///         document.
///     </para>
///     <para>
///         ⚠ <b>The interface is a wire format.</b> Adding a member is compatible;
///         renaming one, reordering a record's members, or changing a parameter's type
///         is a protocol break that no compiler on either side will report, because the
///         daemon and the application are built at different times from different
///         copies. <see cref="Version" /> exists so that the break can at least be
///         detected at run time rather than being decoded as garbage.
///     </para>
/// </remarks>
/// <example>
///     <code>
///     [TrinixService(
///         "io.trinix.Clipboard",
///         BusName = "io.trinix.Shell",
///         ObjectPath = "/io/trinix/Clipboard")]
///     public interface IClipboard { … }
///     </code>
/// </example>
[AttributeUsage(AttributeTargets.Interface)]
public sealed class TrinixServiceAttribute : Attribute {
    /// <summary>Declare the native face of a service.</summary>
    /// <param name="interfaceName">
    ///     The D-Bus interface name, which by doc 02 is always <c>io.trinix.&lt;Service&gt;</c>.
    /// </param>
    public TrinixServiceAttribute(string interfaceName) {
        InterfaceName = interfaceName;
    }

    /// <summary>The D-Bus interface name — <c>io.trinix.Notifications</c> and friends.</summary>
    public string InterfaceName { get; }

    /// <summary>
    ///     The well-known bus name that owns this interface.
    /// </summary>
    /// <remarks>
    ///     ⚠ Not derivable from <see cref="InterfaceName" />, and doc 02 § The daemon set
    ///     is why: notifications and the clipboard are both <c>io.trinix.Shell</c>,
    ///     because their state <i>is</i> shell state and a second process would mean two
    ///     owners of one notification centre. The interface names the contract; this
    ///     names the process that happens to serve it today, and the two are allowed to
    ///     be re-partitioned without the contract changing.
    /// </remarks>
    public string BusName { get; set; } = "";

    /// <summary>The object path the service is published at.</summary>
    public string ObjectPath { get; set; } = "";

    /// <summary>
    ///     The contract version, bumped whenever a member is added.
    /// </summary>
    /// <remarks>
    ///     Doc 02 § Failure, restart and versioning: "the SDK's proxy refuses a service
    ///     older than it was built against with a message naming the system version
    ///     required". This number is what makes that refusal expressible; it is emitted
    ///     into the introspection XML as an annotation so that a running system can be
    ///     asked what it implements without a Trinix-specific method.
    /// </remarks>
    public int Version { get; set; } = 1;
}

/// <summary>
///     Marks a method on a service interface as the <i>subscription</i> to a D-Bus signal
///     rather than as a method call.
/// </summary>
/// <remarks>
///     <para>
///         A signal is not a C# <c>event</c> here, and the reason is doc 01's rule that
///         everything on the service surface is <c>async</c>. Subscribing to a D-Bus
///         signal means an <c>AddMatch</c> round trip to the bus, so an
///         <c>event … { add { … } }</c> would be a synchronous accessor hiding IPC — the
///         exact shape doc 01 forbids, and one that cannot report a failure either.
///     </para>
///     <para>
///         ⚠ The generator requires the annotated method to be spelled exactly:
///     </para>
///     <code>
///     [ServiceSignal("Activated")]
///     Task&lt;IDisposable&gt; WatchActivatedAsync(
///         Action&lt;NotificationActivation&gt; handler,
///         CancellationToken cancellationToken);
///     </code>
///     <para>
///         One payload parameter, always — a signal that wants to carry two things
///         carries a <see cref="ServiceRecordAttribute" /> record with two members, so
///         that adding a third is a change to one type rather than to a signature the
///         generator would have to widen.
///     </para>
///     <para>
///         The returned <see cref="IDisposable" /> unsubscribes. It is only on the
///         <i>client</i> side: the dispatcher the generator emits for the server carries
///         a matching <c>Emit…</c> method instead, because a service raises a signal and
///         never watches its own.
///     </para>
/// </remarks>
[AttributeUsage(AttributeTargets.Method)]
public sealed class ServiceSignalAttribute : Attribute {
    /// <summary>Declare a signal subscription.</summary>
    /// <param name="name">The D-Bus signal name, e.g. <c>Activated</c>.</param>
    public ServiceSignalAttribute(string name) {
        Name = name;
    }

    /// <summary>The D-Bus signal name.</summary>
    /// <remarks>
    ///     Stated rather than derived from the method name. <c>WatchActivatedAsync</c>
    ///     could be de-affixed mechanically, but a rule that strips <c>Watch</c> and
    ///     <c>Async</c> would silently rename the signal the day somebody writes
    ///     <c>WatchAsyncOperationAsync</c>, and the failure would be a signal nobody
    ///     receives.
    /// </remarks>
    public string Name { get; }
}

/// <summary>
///     Marks a record as one that crosses the service boundary, marshalled as a D-Bus
///     structure.
/// </summary>
/// <remarks>
///     <para>
///         The members are the record's <b>primary constructor parameters, in
///         declaration order</b>, and that order is the wire format. ⚠ Reordering them
///         is a silent protocol break: <c>(string Title, string Body)</c> and
///         <c>(string Body, string Title)</c> have the same D-Bus signature
///         <c>(ss)</c>, so a mismatched pair of builds does not fail — it swaps the
///         fields and shows the body as the title.
///     </para>
///     <para>
///         Why a record and not a property bag. Doc 02 § Notifications insists a
///         notification is "a record with an identity, not a string", and the same
///         argument decides the encoding: an <c>a{sv}</c> dictionary — which is what
///         <c>org.freedesktop.Notifications</c> hints are — makes every field optional,
///         untyped and unversioned, so a typo in a key is indistinguishable from a field
///         the peer does not implement. A structure makes the shape a compile-time fact
///         on both sides.
///     </para>
///     <para>
///         ⚠ <b>A record with a sequence member does not have value equality.</b> C#
///         generates <c>Equals</c> from <c>EqualityComparer&lt;T&gt;.Default</c> per member,
///         and for an <c>IReadOnlyList&lt;T&gt;</c> that is reference equality — so two
///         notifications with identical contents compare <i>unequal</i> if their action
///         lists are different objects, which they always are after a trip over the bus. An
///         application writing <c>if (next == current) return;</c> to avoid a redundant
///         update will never take that branch. It is left this way rather than papered over
///         with a value-equal collection type, because the alternative hides a second
///         problem: a record that compares equal by contents invites being used as a
///         dictionary key, and doc 02's identity for a notification is
///         <c>NotificationId</c>, not its text.
///     </para>
///     <para>
///         ⚠ The generator emits the read/write helpers for a record into the assembly
///         that <i>declares</i> it. A <see cref="ServiceRecordAttribute" /> record used
///         by a service in another assembly therefore needs that assembly to run the
///         generator too; in practice everything lives in
///         <c>Trinix.Services.Contracts</c>, which is the point of there being one
///         contracts assembly.
///     </para>
/// </remarks>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct)]
public sealed class ServiceRecordAttribute : Attribute;
