using System.Collections.Generic;
using System.Linq;

namespace Trinix.Sdk.Generators;

/// <summary>
///     Turns a resolved <see cref="ServiceContract" /> into the three things doc 01 says a
///     service declaration is worth: the client proxy, the server dispatcher, and the
///     introspection XML.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>Every method here that touches a <c>MessageWriter</c> or a <c>Reader</c>
///         emits a <c>static</c>, non-<c>async</c> method, and that is the single hardest
///         constraint on this file.</b> Both types are <c>ref struct</c>s: they live on the
///         stack, they cannot be a field of a state machine, and therefore they cannot be
///         in scope across an <c>await</c>. The generated code is shaped around it in four
///         places —
///     </para>
///     <list type="bullet">
///         <item><description>
///             <c>Build&lt;Member&gt;</c> fills a writer and returns a finished
///             <c>MessageBuffer</c>, so the proxy's <c>async</c> surface never sees the
///             writer. The proxy method is not even <c>async</c> itself: it builds, sends,
///             and returns the transport's task to
///             <c>ServiceErrors.TranslateAsync</c>, which is where the <c>await</c> lives.
///         </description></item>
///         <item><description>
///             <c>Read&lt;Member&gt;Reply</c> is a <c>MessageValueReader&lt;T&gt;</c>,
///             which the transport calls synchronously on the reader loop for exactly this
///             reason.
///         </description></item>
///         <item><description>
///             <c>Read&lt;Member&gt;Arguments</c> exists because the constraint is
///             <i>symmetric</i> and that is easy to miss: <c>Reader</c> is a
///             <c>ref struct</c> too, so the dispatcher cannot decode its arguments inline
///             in an <c>async</c> handler. It decodes first, into ordinary values, and only
///             then awaits the implementation.
///         </description></item>
///         <item><description>
///             <c>Build&lt;Member&gt;Reply</c> runs after the <c>await</c> has already
///             completed, inside its own non-<c>async</c> frame.
///         </description></item>
///     </list>
///     <para>
///         Hand-written, that is ten to twenty lines per member forever, per doc 01. Here it
///         is one shape in one file that nobody downstream ever sees.
///     </para>
/// </remarks>
static class ServiceEmitter {
    const string Tmds = "global::Tmds.DBus.Protocol";
    const string Errors = "global::Trinix.Services.Contracts.ServiceErrors";
    const string Task = "global::System.Threading.Tasks.Task";
    const string Token = "global::System.Threading.CancellationToken";

    // CA1861: hoisted rather than written inline at both call sites, which is what the
    // rule is for — the array is the same every time and the emitter runs once per member
    // per compile.
    static readonly string[] CancellationParameter = { Token + " cancellationToken" };

    // The three fields every proxy builder is handed, so that the builder can stay static
    // and therefore stay out of any async frame.
    static readonly string[] ProxyState = { "_connection", "_destination", "_path" };

    /// <summary>
    ///     Open a <c>MessageWriter</c> scope in a way the nested marshallers can use.
    /// </summary>
    /// <remarks>
    ///     ⚠ <c>try</c>/<c>finally</c> rather than <c>using var</c>, and it is not a style
    ///     choice: C# forbids passing a <c>using</c> variable as a <c>ref</c> argument
    ///     (CS1657), because disposal has to be able to see the variable it declared. Every
    ///     nested record marshals through <c>Write&lt;Record&gt;(ref writer, …)</c>, so a
    ///     <c>using var</c> here makes any contract with a record in it fail to compile —
    ///     which is a third consequence of <c>MessageWriter</c> being a <c>ref struct</c>,
    ///     and the one that is not in doc 01 because nobody hits it until they nest.
    /// </remarks>
    static void OpenWriter(CodeWriter code, string initialiser) {
        code.Line($"var writer = {initialiser};");
        code.OpenBrace("try");
    }

    /// <summary>Close the scope <see cref="OpenWriter" /> opened, returning the message.</summary>
    static void CloseWriter(CodeWriter code) {
        code.Line("return writer.CreateMessage();");
        code.CloseBrace();
        code.OpenBrace("finally");
        code.Line("writer.Dispose();");
        code.CloseBrace();
    }

    /// <summary>Emit the proxy, the handler and the introspection for one service.</summary>
    /// <param name="contract">The resolved contract.</param>
    /// <param name="wireClass">The fully-qualified marshalling helper class to <c>using static</c>.</param>
    public static IEnumerable<SourceFile> Emit(ServiceContract contract, string wireClass) {
        yield return new SourceFile(
            $"{contract.ShortName}Proxy.g.cs",
            EmitProxy(contract, wireClass)
        );
        yield return new SourceFile(
            $"{contract.ShortName}Handler.g.cs",
            EmitHandler(contract, wireClass)
        );
        yield return new SourceFile(
            $"{contract.ShortName}Introspection.g.cs",
            EmitIntrospection(contract)
        );
    }

    static void Preamble(CodeWriter code, ServiceContract contract, string wireClass) {
        code.Line("// <auto-generated/>");
        code.Line("#nullable enable");
        code.Line("#pragma warning disable CS1591 // generated members carry their reason on the interface");
        code.Line();
        code.Line($"using static {wireClass};");
        code.Line();
        if (contract.Namespace.Length > 0) {
            code.Line($"namespace {contract.Namespace};");
            code.Line();
        }
    }

    static void Constants(CodeWriter code, ServiceContract contract) {
        code.Line($"public const string InterfaceName = \"{contract.InterfaceName}\";");
        code.Line($"public const string DefaultBusName = \"{contract.BusName}\";");
        code.Line($"public const string DefaultObjectPath = \"{contract.ObjectPath}\";");
        code.Line();
        code.Line("/// <summary>The contract version this build was generated from.</summary>");
        code.Line($"public const int ContractVersion = {contract.Version};");
        code.Line();
    }

    // ---------------------------------------------------------------- proxy

    static string EmitProxy(ServiceContract contract, string wireClass) {
        var code = new CodeWriter();
        Preamble(code, contract, wireClass);

        code.Line($"/// <summary>Calls <c>{contract.InterfaceName}</c>. Generated from <see cref=\"{contract.CSharpInterface}\" />.</summary>");
        code.OpenBrace($"public sealed class {contract.ShortName}Proxy : {contract.CSharpInterface}");
        Constants(code, contract);

        code.Line($"readonly {Tmds}.DBusConnection _connection;");
        code.Line("readonly string _destination;");
        code.Line("readonly string _path;");
        code.Line();

        code.Line("/// <summary>Talk to the service where doc 02 puts it.</summary>");
        code.Line($"public {contract.ShortName}Proxy({Tmds}.DBusConnection connection)");
        code.Line("    : this(connection, DefaultBusName, DefaultObjectPath) { }");
        code.Line();
        code.Line("/// <summary>Talk to a service somewhere else — a test host, or a second instance.</summary>");
        code.OpenBrace($"public {contract.ShortName}Proxy({Tmds}.DBusConnection connection, string destination, string path)");
        code.Line("_connection = connection ?? throw new global::System.ArgumentNullException(nameof(connection));");
        code.Line("_destination = destination ?? throw new global::System.ArgumentNullException(nameof(destination));");
        code.Line("_path = path ?? throw new global::System.ArgumentNullException(nameof(path));");
        code.CloseBrace();
        code.Line();

        foreach (var method in contract.Methods) {
            EmitProxyMethod(code, contract, method);
        }

        foreach (var signal in contract.Signals) {
            EmitProxySignal(code, contract, signal);
        }

        code.CloseBrace();
        return code.ToString();
    }

    static void EmitProxyMethod(CodeWriter code, ServiceContract contract, ServiceMethod method) {
        var parameters = method.Parameters
            .Select(p => $"{p.Type.CSharpType} {p.Name}")
            .ToList();

        // ⚠ Non-async and static: the writer is a ref struct and may not be in scope across
        // an await, so the whole message is built and finished before any task exists.
        var buildParameters = new List<string> {
            $"{Tmds}.DBusConnection connection",
            "string destination",
            "string path"
        };
        buildParameters.AddRange(parameters);

        code.OpenBrace($"static {Tmds}.MessageBuffer Build{method.MemberName}({string.Join(", ", buildParameters)})");
        OpenWriter(code, "connection.GetMessageWriter()");
        code.Line(
            $"writer.WriteMethodCallHeader(destination, path, InterfaceName, \"{method.MemberName}\", "
            + $"{Literal(method.InSignature)});"
        );
        foreach (var parameter in method.Parameters) {
            parameter.Type.EmitWrite(code, parameter.Name);
        }

        CloseWriter(code);
        code.CloseBrace();
        code.Line();

        if (method.Result is not null) {
            var resultType = method.Result.CSharpType;
            code.Line(
                $"static readonly {Tmds}.MessageValueReader<{resultType}> {method.MemberName}ReplyReader "
                + $"= Read{method.MemberName}Reply;"
            );
            code.Line();
            code.OpenBrace($"static {resultType} Read{method.MemberName}Reply({Tmds}.Message message, object? state)");
            code.Line("var reader = message.GetBodyReader();");
            method.Result.EmitRead(code, "result");
            code.Line("return result;");
            code.CloseBrace();
            code.Line();
        }

        var signature = string.Join(", ", parameters.Concat(CancellationParameter));
        var returns = method.Result is null ? Task : $"{Task}<{method.Result.CSharpType}>";
        var arguments = string.Join(", ", ProxyState.Concat(method.Parameters.Select(p => p.Name)));

        code.Line("/// <inheritdoc />");
        code.OpenBrace($"public {returns} {method.CSharpName}({signature})");
        code.Line($"var message = Build{method.MemberName}({arguments});");
        code.Line(method.Result is null
            ? $"return {Errors}.TranslateAsync(_connection.CallMethodAsync(message), cancellationToken);"
            : $"return {Errors}.TranslateAsync(_connection.CallMethodAsync(message, {method.MemberName}ReplyReader, null!), cancellationToken);");
        code.CloseBrace();
        code.Line();
    }

    static void EmitProxySignal(CodeWriter code, ServiceContract contract, ServiceSignal signal) {
        var payload = signal.Payload.CSharpType;

        code.Line(
            $"static readonly {Tmds}.MessageValueReader<{payload}> {signal.MemberName}SignalReader "
            + $"= Read{signal.MemberName}Signal;"
        );
        code.Line();
        code.OpenBrace($"static {payload} Read{signal.MemberName}Signal({Tmds}.Message message, object? state)");
        code.Line("var reader = message.GetBodyReader();");
        signal.Payload.EmitRead(code, "value");
        code.Line("return value;");
        code.CloseBrace();
        code.Line();

        code.Line("/// <inheritdoc />");
        code.OpenBrace(
            $"public {Task}<global::System.IDisposable> {signal.CSharpName}("
            + $"global::System.Action<{payload}> handler, {Token} cancellationToken)"
        );
        code.Line("global::System.ArgumentNullException.ThrowIfNull(handler);");
        // ⚠ Sender is pinned to the destination. Without it any peer on the bus could
        // synthesise this signal, and for a notification activation that means any
        // application could tell another that the user pressed one of its buttons.
        code.OpenBrace($"var rule = new {Tmds}.MatchRule");
        code.Line($"Type = {Tmds}.MessageType.Signal,");
        code.Line("Sender = _destination,");
        code.Line("Path = _path,");
        code.Line("Interface = InterfaceName,");
        code.Line($"Member = \"{signal.MemberName}\"");
        code.CloseBrace(";");
        code.Line();
        code.Line("return _connection.AddMatchAsync(");
        code.Indent();
        code.Line("rule,");
        code.Line($"{signal.MemberName}SignalReader,");
        // ⚠ The Notification<T> overload, not the four-argument one — that is [Obsolete]
        // as of 0.95.0 and TreatWarningsAsErrors makes an obsolete call a build failure.
        // With ObserverFlags.None the only notification that arrives is a value, so the
        // guard is about the shape of the type rather than about a case we expect.
        //
        // The handler is passed as the observer's state rather than captured, so the
        // lambda stays static: a capturing lambda here would allocate a closure per
        // subscription and, worse, keep the proxy alive for as long as the bus holds the
        // match rule.
        code.OpenBrace("static notification =>");
        code.OpenBrace("if (notification.HasValue)");
        code.Line($"((global::System.Action<{payload}>)notification.State!).Invoke(notification.Value);");
        code.CloseBrace();
        code.CloseBrace(",");
        code.Line("false,");
        code.Line($"{Tmds}.ObserverFlags.None,");
        code.Line("handler).AsTask().WaitAsync(cancellationToken);");
        code.Outdent();
        code.CloseBrace();
        code.Line();
    }

    // -------------------------------------------------------------- handler

    static string EmitHandler(ServiceContract contract, string wireClass) {
        var code = new CodeWriter();
        Preamble(code, contract, wireClass);

        code.Line($"/// <summary>Serves <c>{contract.InterfaceName}</c>. Derive and implement the members.</summary>");
        code.Line("/// <remarks>");
        code.Line("///     An abstract class rather than a second interface, because the emit helpers for");
        code.Line("///     the signals have to live somewhere and a service raises its own signals rather");
        code.Line($"///     than watching them — which is why this does not implement <see cref=\"{contract.CSharpInterface}\" />.");
        code.Line("/// </remarks>");
        code.OpenBrace($"public abstract class {contract.ShortName}Handler : {Tmds}.IPathMethodHandler");
        Constants(code, contract);

        code.Line($"readonly {Tmds}.DBusConnection _connection;");
        code.Line("readonly string _path;");
        code.Line();
        code.Line("/// <summary>Publish at the path doc 02 gives this service.</summary>");
        code.Line($"protected {contract.ShortName}Handler({Tmds}.DBusConnection connection)");
        code.Line("    : this(connection, DefaultObjectPath) { }");
        code.Line();
        code.Line("/// <summary>Publish somewhere else.</summary>");
        code.OpenBrace($"protected {contract.ShortName}Handler({Tmds}.DBusConnection connection, string path)");
        code.Line("_connection = connection ?? throw new global::System.ArgumentNullException(nameof(connection));");
        code.Line("_path = path ?? throw new global::System.ArgumentNullException(nameof(path));");
        code.CloseBrace();
        code.Line();
        code.Line("/// <inheritdoc />");
        code.Line("public string Path => _path;");
        code.Line();
        code.Line("/// <inheritdoc />");
        code.Line("public virtual bool HandlesChildPaths => false;");
        code.Line();
        code.Line("/// <summary>The connection this handler was published on.</summary>");
        code.Line($"protected {Tmds}.DBusConnection Connection => _connection;");
        code.Line();

        foreach (var method in contract.Methods) {
            var parameters = method.Parameters
                .Select(p => $"{p.Type.CSharpType} {p.Name}")
                .Concat(CancellationParameter);
            var returns = method.Result is null ? Task : $"{Task}<{method.Result.CSharpType}>";
            code.Line($"/// <summary>Implements <c>{contract.InterfaceName}.{method.MemberName}</c>.</summary>");
            code.Line($"protected abstract {returns} {method.CSharpName}({string.Join(", ", parameters)});");
            code.Line();
        }

        foreach (var method in contract.Methods) {
            EmitHandlerCodecs(code, method);
        }

        foreach (var signal in contract.Signals) {
            EmitHandlerSignal(code, signal);
        }

        EmitDispatch(code, contract);

        code.CloseBrace();
        return code.ToString();
    }

    static void EmitHandlerCodecs(CodeWriter code, ServiceMethod method) {
        if (method.Parameters.Count > 0) {
            var tuple = method.Parameters.Count == 1
                ? method.Parameters[0].Type.CSharpType
                : "(" + string.Join(", ", method.Parameters.Select(p => $"{p.Type.CSharpType} {p.Name}")) + ")";

            // ⚠ Non-async and static, for the same reason as the proxy's builders: Reader
            // is a ref struct. Decoding has to finish before the implementation is awaited.
            code.OpenBrace($"static {tuple} Read{method.MemberName}Arguments({Tmds}.Message message)");
            code.Line("var reader = message.GetBodyReader();");
            var locals = new List<string>();
            foreach (var parameter in method.Parameters) {
                var local = code.NextName(parameter.Name + "_");
                parameter.Type.EmitRead(code, local);
                locals.Add(local);
            }

            code.Line(method.Parameters.Count == 1
                ? $"return {locals[0]};"
                : $"return ({string.Join(", ", locals)});");
            code.CloseBrace();
            code.Line();
        }

        var replyValue = method.Result is null ? "" : $", {method.Result.CSharpType} value";
        code.OpenBrace($"static {Tmds}.MessageBuffer Build{method.MemberName}Reply({Tmds}.MethodContext context{replyValue})");
        OpenWriter(code, $"context.CreateReplyWriter({Literal(method.OutSignature)})");
        method.Result?.EmitWrite(code, "value");
        CloseWriter(code);
        code.CloseBrace();
        code.Line();
    }

    static void EmitHandlerSignal(CodeWriter code, ServiceSignal signal) {
        var payload = signal.Payload.CSharpType;

        code.OpenBrace($"static {Tmds}.MessageBuffer Build{signal.MemberName}Signal(" +
            $"{Tmds}.DBusConnection connection, string? destination, string path, {payload} value)");
        OpenWriter(code, "connection.GetMessageWriter()");
        code.Line($"writer.WriteSignalHeader(destination!, path, InterfaceName, \"{signal.MemberName}\", {Literal(signal.Payload.Signature)});");
        signal.Payload.EmitWrite(code, "value");
        CloseWriter(code);
        code.CloseBrace();
        code.Line();

        code.Line($"/// <summary>Broadcast <c>{signal.MemberName}</c> to every peer that has matched it.</summary>");
        code.Line("/// <remarks>");
        code.Line("///     ⚠ A broadcast signal is readable by anything on the bus that adds a match rule.");
        code.Line("///     Anything scoped to one caller — a notification's activation, a per-application");
        code.Line("///     result — must use the overload that takes a destination instead, or the service");
        code.Line("///     has told every application what the user just did in another.");
        code.Line("/// </remarks>");
        code.OpenBrace($"protected void Emit{signal.MemberName}({payload} value)");
        code.Line($"_ = _connection.TrySendMessage(Build{signal.MemberName}Signal(_connection, null, _path, value));");
        code.CloseBrace();
        code.Line();

        code.Line($"/// <summary>Send <c>{signal.MemberName}</c> to one peer.</summary>");
        code.Line("/// <param name=\"destination\">The recipient's unique bus name.</param>");
        code.Line("/// <param name=\"value\">The payload.</param>");
        code.OpenBrace($"protected void Emit{signal.MemberName}(string destination, {payload} value)");
        code.Line("global::System.ArgumentNullException.ThrowIfNull(destination);");
        code.Line($"_ = _connection.TrySendMessage(Build{signal.MemberName}Signal(_connection, destination, _path, value));");
        code.CloseBrace();
        code.Line();
    }

    static void EmitDispatch(CodeWriter code, ServiceContract contract) {
        code.Line("/// <inheritdoc />");
        code.OpenBrace($"public async global::System.Threading.Tasks.ValueTask HandleMethodAsync({Tmds}.MethodContext context)");
        code.Line("global::System.ArgumentNullException.ThrowIfNull(context);");
        code.Line();
        code.OpenBrace("if (context.IsDBusIntrospectRequest)");
        code.Line("context.ReplyIntrospectXml(");
        code.Line("    new global::System.ReadOnlyMemory<byte>[] {");
        // ⚠ Introspectable and nothing else. Tmds also offers ready-made XML for
        // org.freedesktop.DBus.Peer and .Properties, and advertising either would be a
        // lie: the dispatcher below refuses every interface but its own, so a Ping would
        // be answered with UnknownMethod by a service whose introspection said it was
        // supported. An interface that appears in introspection is a promise to whatever
        // read it, including tools that are not ours.
        code.Line($"        {contract.ShortName}Introspection.InterfaceXmlUtf8,");
        code.Line($"        {Tmds}.IntrospectionXml.DBusIntrospectable");
        // ⚠ The cast is not decoration. ReplyIntrospectXml is overloaded on
        // ReadOnlySpan<string> and IList<string>, and a bare Array.Empty<string>() converts
        // to both — which resolves under C# 14's first-class-span rules and is CS0121 under
        // anything older. Generated code is compiled by whatever LangVersion the consuming
        // project sets, so it must not depend on the newest overload-resolution rules.
        code.Line("    },");
        code.Line("    (global::System.ReadOnlySpan<string>)global::System.Array.Empty<string>());");
        code.Line("return;");
        code.CloseBrace();
        code.Line();
        code.Line("var member = context.Request.MemberIsSet ? context.Request.MemberAsString : string.Empty;");
        code.Line("var signature = context.Request.SignatureIsSet ? context.Request.SignatureAsString : string.Empty;");
        code.Line("var target = context.Request.InterfaceIsSet ? context.Request.InterfaceAsString : string.Empty;");
        code.Line();
        // ⚠ The interface header field is optional in the specification. Trinix's own
        // services require it: a message that names a member without naming the interface
        // is ambiguous the moment one object carries two interfaces, and doc 02's
        // trinix-shell carries at least Notifications and Clipboard.
        code.OpenBrace("if (!string.Equals(target, InterfaceName, global::System.StringComparison.Ordinal))");
        code.Line("context.ReplyUnknownMethodError();");
        code.Line("return;");
        code.CloseBrace();
        code.Line();
        code.OpenBrace("try");
        code.OpenBrace("switch (member)");

        foreach (var method in contract.Methods) {
            code.OpenBrace($"case \"{method.MemberName}\":");
            // ⚠ Quoted, not Literal. A method with no arguments sends *no* signature field
            // and Literal spells that as a null for the writer, but the dispatcher is
            // comparing against the empty string it read out of the header — comparing
            // against null there would make every no-argument member unreachable.
            code.OpenBrace($"if (!string.Equals(signature, {Quoted(method.InSignature)}, global::System.StringComparison.Ordinal))");
            code.Line("break;");
            code.CloseBrace();
            code.Line();

            var call = new List<string>();
            if (method.Parameters.Count == 1) {
                code.Line($"var argument = Read{method.MemberName}Arguments(context.Request);");
                call.Add("argument");
            } else if (method.Parameters.Count > 1) {
                code.Line($"var arguments = Read{method.MemberName}Arguments(context.Request);");
                call.AddRange(method.Parameters.Select(p => "arguments." + p.Name));
            }

            call.Add("context.RequestAborted");
            var invocation = $"{method.CSharpName}({string.Join(", ", call)})";
            if (method.Result is null) {
                code.Line($"await {invocation}.ConfigureAwait(false);");
                code.Line($"context.Reply(Build{method.MemberName}Reply(context));");
            } else {
                code.Line($"var result = await {invocation}.ConfigureAwait(false);");
                code.Line($"context.Reply(Build{method.MemberName}Reply(context, result));");
            }

            code.Line("return;");
            code.CloseBrace();
        }

        code.OpenBrace("default:");
        code.Line("break;");
        code.CloseBrace();
        code.CloseBrace();
        code.CloseBrace();
        // ⚠ Catching everything is right here and nowhere else. This is the boundary
        // between one application's request and the service's own process: an exception
        // that escapes takes down the connection and with it every other application's
        // subscriptions. ServiceErrors.ToErrorReply decides what the caller is told, and
        // deliberately does not tell it the type or the stack.
        code.OpenBrace("catch (global::System.Exception exception)");
        code.OpenBrace("if (!context.ReplySent)");
        code.Line($"var failure = {Errors}.ToErrorReply(exception);");
        code.Line("context.ReplyError(failure.Name, failure.Message);");
        code.CloseBrace();
        code.Line();
        code.Line("return;");
        code.CloseBrace();
        code.Line();
        code.Line("context.ReplyUnknownMethodError();");
        code.CloseBrace();
    }

    // -------------------------------------------------------- introspection

    static string EmitIntrospection(ServiceContract contract) {
        var xml = new System.Text.StringBuilder();
        xml.Append("<interface name=\"").Append(contract.InterfaceName).Append("\">\n");
        // The contract version, as an annotation rather than as a method, so that
        // `busctl introspect` answers doc 02's versioning question without Trinix
        // having to define a Version() member every service would then have to implement.
        xml.Append("  <annotation name=\"io.trinix.Version\" value=\"")
            .Append(contract.Version.ToString(System.Globalization.CultureInfo.InvariantCulture))
            .Append("\"/>\n");

        foreach (var method in contract.Methods) {
            xml.Append("  <method name=\"").Append(method.MemberName).Append("\">\n");
            foreach (var parameter in method.Parameters) {
                xml.Append("    <arg name=\"").Append(parameter.Name).Append("\" type=\"")
                    .Append(parameter.Type.Signature).Append("\" direction=\"in\"/>\n");
            }

            if (method.Result is not null) {
                xml.Append("    <arg name=\"result\" type=\"").Append(method.Result.Signature)
                    .Append("\" direction=\"out\"/>\n");
            }

            xml.Append("  </method>\n");
        }

        foreach (var signal in contract.Signals) {
            xml.Append("  <signal name=\"").Append(signal.MemberName).Append("\">\n");
            xml.Append("    <arg name=\"value\" type=\"").Append(signal.Payload.Signature).Append("\"/>\n");
            xml.Append("  </signal>\n");
        }

        xml.Append("</interface>\n");
        var interfaceXml = xml.ToString();

        var node = "<!DOCTYPE node PUBLIC \"-//freedesktop//DTD D-BUS Object Introspection 1.0//EN\"\n"
            + " \"http://www.freedesktop.org/standards/dbus/1.0/introspect.dtd\">\n"
            + "<node name=\"" + contract.ObjectPath + "\">\n"
            + interfaceXml
            + "</node>\n";

        var code = new CodeWriter();
        code.Line("// <auto-generated/>");
        code.Line("#nullable enable");
        code.Line("#pragma warning disable CS1591");
        code.Line();
        if (contract.Namespace.Length > 0) {
            code.Line($"namespace {contract.Namespace};");
            code.Line();
        }

        code.Line($"/// <summary>The D-Bus introspection XML for <c>{contract.InterfaceName}</c>, generated from the C#.</summary>");
        code.Line("/// <remarks>");
        code.Line("///     Doc 01: the C# interface is the source of truth and the XML is derived from it.");
        code.Line("///     The compat faces go the other way round and are hand-written, because their XML");
        code.Line("///     is somebody else's normative document and transcribing it by hand is what pins it.");
        code.Line("/// </remarks>");
        code.OpenBrace($"public static class {contract.ShortName}Introspection");
        code.Line("/// <summary>The <c>&lt;interface&gt;</c> element, which is what a reply to Introspect() carries.</summary>");
        code.Line($"public const string InterfaceXml = {Literal(interfaceXml)};");
        code.Line();
        code.Line("/// <summary>The whole document, for a tool that wants to diff it against a running service.</summary>");
        code.Line($"public const string NodeXml = {Literal(node)};");
        code.Line();
        code.Line("/// <summary>The interface element as UTF-8, for <c>MethodContext.ReplyIntrospectXml</c>.</summary>");
        code.Line("public static global::System.ReadOnlyMemory<byte> InterfaceXmlUtf8 => Utf8;");
        code.Line();
        code.Line("static readonly byte[] Utf8 = global::System.Text.Encoding.UTF8.GetBytes(InterfaceXml);");
        code.CloseBrace();
        return code.ToString();
    }

    /// <summary>A C# string literal, or <c>null!</c> for the empty signature.</summary>
    /// <remarks>
    ///     ⚠ An empty D-Bus signature and an absent one are different things on the wire.
    ///     A method with no arguments must send <b>no</b> SIGNATURE header field, which
    ///     Tmds spells as a null, not as <c>""</c> — a message carrying an empty signature
    ///     field is malformed and is dropped by the daemon rather than delivered.
    /// </remarks>
    static string Literal(string value) => value.Length == 0 ? "null!" : Quoted(value);

    /// <summary>A C# string literal, always quoted.</summary>
    static string Quoted(string value) =>
        "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n") + "\"";
}
