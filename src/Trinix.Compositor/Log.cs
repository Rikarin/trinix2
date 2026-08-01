namespace Trinix.Compositor;

/// <summary>
/// The compositor's own log output.
/// </summary>
/// <remarks>
/// Plain writes to standard output, not <c>ILogger</c>, and for once that is
/// the considered choice rather than the lazy one. systemd captures stdout into
/// the journal with the unit's identity already attached, so a logging
/// framework here would add a dependency, a startup cost and a second
/// formatting layer in front of a stream that is already structured by the
/// thing reading it. wlroots' own messages arrive on the same stream from C,
/// and the two interleave in the order they happened.
/// </remarks>
internal static class Log
{
    private const string Prefix = "trinix-compositor: ";

    /// <summary>Writes one line to the journal.</summary>
    /// <param name="message">The message, without a trailing newline.</param>
    internal static void Line(string message) => Console.Out.WriteLine(Prefix + message);

    /// <summary>Writes one line to standard error.</summary>
    /// <param name="message">The message, without a trailing newline.</param>
    internal static void Error(string message) => Console.Error.WriteLine(Prefix + message);
}
