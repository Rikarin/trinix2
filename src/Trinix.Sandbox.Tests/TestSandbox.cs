using Trinix.Bundle;
using Trinix.Sandbox;

namespace Trinix.Sandbox.Tests;

/// <summary>
///     One application, one user, one set of paths — so that every test below
///     differs from every other one in exactly the thing it is about.
/// </summary>
/// <remarks>
///     ⚠ The user's real home is <c>/home/jiu</c> and it appears nowhere in the
///     expected output. Several tests assert that, and they only mean something
///     because the fixture puts a real home in the layout for them to fail to find:
///     a fixture whose home directory was never mentioned would make
///     "no mount of the user's home" true by construction.
/// </remarks>
static class TestSandbox {
    public const string UserHome = "/home/jiu";
    public const string UserName = "jiu";
    public const uint UserId = 1000;
    public const string BundlePath = "/Applications/Hello.app";
    public const string Identifier = "io.trinix.hello";

    public static BundleInfo Info(params string[] permissions) => new() {
        Identifier = Identifier,
        Name = "Hello",
        Version = "1.0.0",
        EntryPoint = "Contents/Bin/hello",
        Permissions = permissions
    };

    public static SandboxLayout Layout(BundleInfo? info = null) =>
        SandboxLayout.For(info ?? Info(), BundlePath, UserName, UserId, UserHome);

    /// <summary>Build a unit for an application declaring exactly these permissions.</summary>
    public static SandboxUnit Unit(params string[] permissions) {
        var info = Info(permissions);
        return SandboxUnitBuilder.Build(info, Layout(info));
    }

    /// <summary>Build a unit with something other than the default options.</summary>
    public static SandboxUnit Unit(SandboxOptions options, params string[] permissions) {
        var info = Info(permissions);
        return SandboxUnitBuilder.Build(info, Layout(info), options);
    }

    /// <summary>Every property value the unit sets, whatever the property.</summary>
    public static IEnumerable<string> AllValues(this SandboxUnit unit) =>
        unit.Properties.Select(property => property.Value);

    /// <summary>
    ///     The host side of every bind mount — the half that decides what the
    ///     container can reach.
    /// </summary>
    /// <remarks>
    ///     ⚠ systemd spells a bind as <c>source:destination</c>, or as a bare path
    ///     when the two are the same. Several tests care only about the source,
    ///     because a destination inside the container is by definition not something
    ///     escaping it.
    /// </remarks>
    public static IEnumerable<string> BindSources(this SandboxUnit unit) =>
        unit.ValuesOf("BindPaths")
            .Concat(unit.ValuesOf("BindReadOnlyPaths"))
            .Select(value => value.Split(':')[0]);
}
