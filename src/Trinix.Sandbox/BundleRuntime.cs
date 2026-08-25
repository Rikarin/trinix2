using Trinix.Bundle;

namespace Trinix.Sandbox;

/// <summary>
///     What kind of code the bundle's entry point is, which decides exactly one
///     property: <c>MemoryDenyWriteExecute=</c>.
/// </summary>
/// <remarks>
///     <para>
///         Doc 04 puts this in the choices table: <c>MemoryDenyWriteExecute</c> is off
///         for .NET applications, because the JIT maps a page writable, writes machine
///         code into it, and then maps it executable — which is precisely what the
///         property forbids — and it is on for NativeAOT bundles, "recorded per-bundle
///         rather than being a system-wide surrender". <c>trinixd.service</c> already
///         carries the same note in its own comments.
///     </para>
///     <para>
///         ⚠ <b><c>Info.json</c> cannot express this today, and this file does not
///         invent a field for it.</b> The manifest has <c>schema</c>, <c>identifier</c>,
///         <c>name</c>, <c>version</c>, <c>shortVersion</c>, <c>entryPoint</c>,
///         <c>minimumSystemVersion</c>, <c>permissions</c> and <c>categories</c>, and
///         none of them says how the entry point executes. Adding a <c>runtime</c>
///         field would be cheap — the signature covers file bytes, so an old bundle
///         whose <c>Info.json</c> simply lacks the field still verifies, and it would
///         arrive here as <see cref="Unknown" />, which is already the safe answer —
///         but it is a format change and belongs in the same commit as the schema note
///         that describes it, not smuggled in under a sandbox.
///     </para>
///     <para>
///         Until then <see cref="BundleRuntimeDetection.DetectFrom(IEnumerable{string})" /> reads it out of evidence that is
///         <i>already signed</i>: the manifest's file list. That is a weaker fact than
///         a declaration, and the whole design of the enum is built around which way
///         it is allowed to be wrong.
///     </para>
/// </remarks>
public enum BundleRuntime {
    /// <summary>
    ///     Nothing in the bundle says. Treated as <see cref="Managed" /> for the
    ///     purposes of <c>MemoryDenyWriteExecute=</c>.
    /// </summary>
    /// <remarks>
    ///     ⚠ Kept distinct from <see cref="Managed" /> even though it produces the
    ///     same property, because the two are different statements and audit mode has
    ///     to be able to tell them apart. "This is a JIT and W^X is off for a reason"
    ///     is a decision; "we could not tell, so we assumed the permissive one" is a
    ///     gap, and <see cref="SandboxUnit.Gaps" /> records it as one.
    /// </remarks>
    Unknown = 0,

    /// <summary>
    ///     A framework-dependent .NET application. The JIT needs W^X transitions.
    /// </summary>
    Managed,

    /// <summary>
    ///     A NativeAOT bundle, or any other executable that generates no code at
    ///     runtime. <c>MemoryDenyWriteExecute=yes</c>.
    /// </summary>
    Native
}

/// <summary>
///     Working out a bundle's <see cref="BundleRuntime" /> from what is in it.
/// </summary>
public static class BundleRuntimeDetection {
    /// <summary>The suffix .NET's build gives the file that configures the runtime host.</summary>
    public const string RuntimeConfigSuffix = ".runtimeconfig.json";

    /// <summary>
    ///     Read the runtime kind out of a bundle's signed file list.
    /// </summary>
    /// <param name="bundleRelativePaths">
    ///     Every path the signed manifest covers — <c>Contents/Bin/hello.dll</c> and
    ///     the rest. Taking the manifest's list rather than a directory listing is the
    ///     point: the manifest is what the developer signed, so the conclusion drawn
    ///     from it is as trustworthy as the signature, whereas a directory listing is
    ///     whatever is on disk right now.
    /// </param>
    /// <returns>
    ///     <see cref="BundleRuntime.Managed" /> when the bundle carries a
    ///     <c>*.runtimeconfig.json</c>, and <see cref="BundleRuntime.Unknown" />
    ///     otherwise.
    /// </returns>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>This never returns <see cref="BundleRuntime.Native" />, and that is
    ///         not an oversight.</b> The only signal available is the presence of a
    ///         runtime config, which is positive evidence of a JIT and no evidence at
    ///         all of the opposite: a bundle without one might be NativeAOT, or a Rust
    ///         binary, or a shell script, or a browser with its own JIT. Turning
    ///         <c>MemoryDenyWriteExecute=yes</c> on from an absence would mean the
    ///         failure mode is "the application segfaults the first time it compiles a
    ///         method", which is a crash at an arbitrary later moment rather than at
    ///         startup — the worst shape a sandbox bug can have.
    ///     </para>
    ///     <para>
    ///         So the detector only ever moves in the safe direction, and the strict
    ///         answer has to be asserted by whoever knows it: the bundle tool at seal
    ///         time, once <c>Info.json</c> can carry it. Until then a NativeAOT
    ///         application runs without W^X enforcement, which is the same protection
    ///         every application has today and no less.
    ///     </para>
    /// </remarks>
    public static BundleRuntime DetectFrom(IEnumerable<string> bundleRelativePaths) {
        foreach (var path in bundleRelativePaths) {
            if (path.EndsWith(RuntimeConfigSuffix, StringComparison.Ordinal)) {
                return BundleRuntime.Managed;
            }
        }

        return BundleRuntime.Unknown;
    }

    /// <summary>Read the runtime kind out of a verified bundle's manifest.</summary>
    /// <param name="manifest">The signed manifest, after verification.</param>
    public static BundleRuntime DetectFrom(BundleManifest manifest) =>
        DetectFrom(manifest.Entries.Select(entry => entry.Path));
}
