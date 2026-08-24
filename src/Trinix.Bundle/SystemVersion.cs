using System.Globalization;

namespace Trinix.Bundle;

/// <summary>
///     The system's own version, and whether an application can run on it.
/// </summary>
/// <remarks>
///     <para>
///         This is a compatibility question, not a trust one, and the distinction is
///         why it lives here rather than in <see cref="BundleVerifier" />. An
///         application that needs a newer system is not suspicious — it is simply in
///         the wrong place, and it should be told so in different words and with a
///         different exit code than one whose signature does not check out.
///     </para>
///     <para>
///         The comparison is coarse and honestly so: <c>minimumSystemVersion</c> says
///         "at least this system", not "this particular protocol at this version". That
///         is the right granularity while everything ships from one tree; it is not the
///         right granularity once a third-party application depends on
///         <c>trinix_shell_v1</c>, and the answer then is to freeze the protocol rather
///         than to make this field cleverer.
///     </para>
/// </remarks>
public static class SystemVersion {
    /// <summary>Where the running system records its identity.</summary>
    public const string OsReleasePath = "/usr/lib/os-release";

    /// <summary>
    ///     Read <c>VERSION_ID</c>, or <see langword="null" /> when there is nothing
    ///     to read — which is the normal case on a build machine.
    /// </summary>
    public static string? Current(string? osReleasePath = null) {
        var path = osReleasePath ?? OsReleasePath;
        if (!File.Exists(path)) {
            return null;
        }

        foreach (var line in File.ReadLines(path)) {
            if (!line.StartsWith("VERSION_ID=", StringComparison.Ordinal)) {
                continue;
            }

            return line["VERSION_ID=".Length..].Trim().Trim('"');
        }

        return null;
    }

    /// <summary>
    ///     Is <paramref name="system" /> at least <paramref name="required" />?
    /// </summary>
    /// <remarks>
    ///     Unknown on either side is "yes". A launcher that refused to start
    ///     anything because it could not find <c>/usr/lib/os-release</c> would be
    ///     enforcing a version policy by accident, and the failure would look
    ///     nothing like its cause.
    /// </remarks>
    public static bool Satisfies(string? system, string? required) {
        if (string.IsNullOrEmpty(required) || string.IsNullOrEmpty(system)) {
            return true;
        }

        var left = Parse(system);
        var right = Parse(required);
        if (left.Length == 0 || right.Length == 0) {
            return true;
        }

        for (var i = 0; i < Math.Max(left.Length, right.Length); i++) {
            // A missing component is zero: 0.3 and 0.3.0 are the same version.
            var a = i < left.Length ? left[i] : 0;
            var b = i < right.Length ? right[i] : 0;
            if (a != b) {
                return a > b;
            }
        }

        return true;
    }

    static int[] Parse(string value) {
        var parts = value.Split('.');
        var numbers = new List<int>(parts.Length);
        foreach (var part in parts) {
            if (!int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out var number)) {
                // Anything non-numeric ends the comparison rather than failing
                // it: "0.3-rc1" is at least "0.3", and pretending to know more
                // than that would be inventing a version scheme.
                break;
            }

            numbers.Add(number);
        }

        return [.. numbers];
    }
}
