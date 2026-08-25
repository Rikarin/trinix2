using System.Globalization;

namespace Trinix.Bundle.Tests;

/// <summary>
///     A scratch directory that deletes itself when the test finishes.
/// </summary>
/// <remarks>
///     <para>
///         Every bundle test works on real files, because the code under test works on
///         real files: <see cref="BundleScanner" /> asks the filesystem about link
///         targets and Unix mode bits, and an abstraction over that would be a second
///         implementation of the thing being tested. The cost is that each test needs
///         somewhere to make a mess, and that somewhere has to be unique — xunit runs
///         separate test classes in parallel.
///     </para>
///     <para>
///         Disposal is best-effort. A test that leaves a directory behind because the
///         machine held a handle open is not a failure worth reporting; a test that
///         fails during cleanup and hides the assertion that already failed is.
///     </para>
/// </remarks>
public sealed class TempDirectory : IDisposable {
    TempDirectory(string path) {
        Path = path;
    }

    /// <summary>The directory itself. It exists by the time the constructor returns.</summary>
    public string Path { get; }

    /// <inheritdoc />
    public void Dispose() {
        try {
            if (Directory.Exists(Path)) {
                Directory.Delete(Path, true);
            }
        } catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
            // See the remarks: cleanup must never be the reason a test fails.
        }
    }

    /// <summary>Make one, named after whatever is being tested.</summary>
    public static TempDirectory Create(string label) {
        var path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            string.Create(CultureInfo.InvariantCulture, $"trinix-tests-{label}-{Guid.NewGuid():n}")
        );
        Directory.CreateDirectory(path);
        return new(path);
    }

    /// <summary>A path inside this directory. Nothing is created.</summary>
    public string Combine(params string[] parts) => System.IO.Path.Combine([Path, .. parts]);
}

/// <summary>
///     One-shot temporary files, for the tests that need a path to read rather than a
///     tree to walk.
/// </summary>
/// <remarks>
///     These are not cleaned up, deliberately: they are a few hundred bytes each, the
///     operating system owns the lifetime of its own temporary directory, and wiring
///     an <see cref="IDisposable" /> through a <c>[Theory]</c> to delete one of them
///     would be more machinery than the problem.
/// </remarks>
public static class TempFile {
    /// <summary>Write <paramref name="content" /> somewhere and return where.</summary>
    public static string WithContent(string content) {
        var path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            string.Create(CultureInfo.InvariantCulture, $"trinix-tests-{Guid.NewGuid():n}")
        );
        File.WriteAllText(path, content);
        return path;
    }
}
