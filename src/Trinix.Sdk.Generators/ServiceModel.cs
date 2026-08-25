using System.Collections.Generic;

namespace Trinix.Sdk.Generators;

/// <summary>One generated file: what to call it and what is in it.</summary>
readonly struct SourceFile {
    public SourceFile(string hintName, string text) {
        HintName = hintName;
        Text = text;
    }

    /// <summary>The hint name. ⚠ Ends in <c>.g.cs</c> so that the CA and IDE analysers treat it as generated.</summary>
    public string HintName { get; }

    /// <summary>The C#.</summary>
    public string Text { get; }
}

/// <summary>A method on a service interface, resolved to its wire shape.</summary>
sealed class ServiceMethod {
    public ServiceMethod(
        string csharpName,
        string memberName,
        IReadOnlyList<ServiceParameter> parameters,
        WireType? result
    ) {
        CSharpName = csharpName;
        MemberName = memberName;
        Parameters = parameters;
        Result = result;
    }

    /// <summary>The C# method name, e.g. <c>PostAsync</c>.</summary>
    public string CSharpName { get; }

    /// <summary>The D-Bus member name, e.g. <c>Post</c>.</summary>
    public string MemberName { get; }

    /// <summary>The arguments, without the trailing <see cref="System.Threading.CancellationToken" />.</summary>
    public IReadOnlyList<ServiceParameter> Parameters { get; }

    /// <summary>What the reply carries, or <see langword="null" /> for a bare <c>Task</c>.</summary>
    public WireType? Result { get; }

    /// <summary>The D-Bus signature of the arguments.</summary>
    public string InSignature {
        get {
            var signature = "";
            foreach (var parameter in Parameters) {
                signature += parameter.Type.Signature;
            }

            return signature;
        }
    }

    /// <summary>The D-Bus signature of the reply.</summary>
    public string OutSignature => Result?.Signature ?? "";
}

/// <summary>One argument.</summary>
sealed class ServiceParameter {
    public ServiceParameter(string name, WireType type) {
        Name = name;
        Type = type;
    }

    /// <summary>The C# parameter name, reused as the introspection <c>arg name</c>.</summary>
    public string Name { get; }

    /// <summary>How it crosses the bus.</summary>
    public WireType Type { get; }
}

/// <summary>A signal, and the one method that subscribes to it.</summary>
sealed class ServiceSignal {
    public ServiceSignal(string csharpName, string memberName, WireType payload) {
        CSharpName = csharpName;
        MemberName = memberName;
        Payload = payload;
    }

    /// <summary>The C# method name, e.g. <c>WatchActivatedAsync</c>.</summary>
    public string CSharpName { get; }

    /// <summary>The D-Bus signal name, e.g. <c>Activated</c>.</summary>
    public string MemberName { get; }

    /// <summary>What the signal carries.</summary>
    public WireType Payload { get; }
}

/// <summary>One <c>[TrinixService]</c> interface, resolved.</summary>
sealed class ServiceContract {
    public ServiceContract(
        string interfaceName,
        string busName,
        string objectPath,
        int version,
        string csharpNamespace,
        string csharpInterface,
        string shortName,
        IReadOnlyList<ServiceMethod> methods,
        IReadOnlyList<ServiceSignal> signals
    ) {
        InterfaceName = interfaceName;
        BusName = busName;
        ObjectPath = objectPath;
        Version = version;
        Namespace = csharpNamespace;
        CSharpInterface = csharpInterface;
        ShortName = shortName;
        Methods = methods;
        Signals = signals;
    }

    /// <summary>The D-Bus interface name.</summary>
    public string InterfaceName { get; }

    /// <summary>The well-known bus name that serves it.</summary>
    public string BusName { get; }

    /// <summary>The object path.</summary>
    public string ObjectPath { get; }

    /// <summary>The contract version.</summary>
    public int Version { get; }

    /// <summary>The namespace the generated types go in — the interface's own.</summary>
    public string Namespace { get; }

    /// <summary>The fully-qualified C# interface.</summary>
    public string CSharpInterface { get; }

    /// <summary>
    ///     The interface's name with the leading <c>I</c> removed, which is what the
    ///     generated types are named after: <c>NotificationsProxy</c>,
    ///     <c>NotificationsHandler</c>.
    /// </summary>
    public string ShortName { get; }

    /// <summary>The methods.</summary>
    public IReadOnlyList<ServiceMethod> Methods { get; }

    /// <summary>The signals.</summary>
    public IReadOnlyList<ServiceSignal> Signals { get; }
}
