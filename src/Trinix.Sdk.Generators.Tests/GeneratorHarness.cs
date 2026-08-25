using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Trinix.Sdk.Generators.Tests;

/// <summary>
///     Runs <see cref="TrinixServiceGenerator" /> over a snippet and hands back what came
///     out.
/// </summary>
/// <remarks>
///     <para>
///         The references are the ones the compiler is already running with, taken from
///         <c>TRUSTED_PLATFORM_ASSEMBLIES</c>. ⚠ That is deliberately not a curated list:
///         a curated list drifts from the framework the contracts assembly actually builds
///         against, and the drift shows up as a test that passes because a type it needed
///         to resolve was missing and the compilation quietly had an error nobody asserted
///         on. <see cref="Run" /> fails on any compilation error for the same reason.
///     </para>
///     <para>
///         The snippets under test compile against the real
///         <c>Trinix.Services.Contracts</c> and the real <c>Tmds.DBus.Protocol</c>, so a
///         change to either that would break generated code breaks these tests too. A
///         harness with stub attributes would keep passing.
///     </para>
/// </remarks>
static class GeneratorHarness {
    static readonly ImmutableArray<MetadataReference> References = ImmutableArray.CreateRange(
        ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
        .Split(Path.PathSeparator)
        .Where(path => path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
        .Select(path => (MetadataReference)MetadataReference.CreateFromFile(path))
    );

    /// <summary>Compile <paramref name="source" />, run the service generator, return everything.</summary>
    /// <param name="source">The contract under test.</param>
    /// <param name="expectFailure">
    ///     When the snippet is <i>meant</i> to be refused, so that the resulting compilation
    ///     is allowed to be broken and only the diagnostics matter.
    /// </param>
    public static GeneratorResult Run(string source, bool expectFailure = false) =>
        Run(new TrinixServiceGenerator(), source, expectFailure);

    /// <summary>Compile <paramref name="source" />, run the settings generator, return everything.</summary>
    /// <remarks>
    ///     ⚠ One generator per run rather than both at once. They ignore each other's
    ///     attributes, so running the pair would work — and would mean that a failure in a
    ///     settings test could be caused by the service generator, which is the sort of
    ///     coupling a suite acquires without noticing and cannot then remove.
    /// </remarks>
    /// <param name="source">The schema under test.</param>
    /// <param name="expectFailure">As above.</param>
    public static GeneratorResult RunSettings(string source, bool expectFailure = false) =>
        Run(new TrinixSettingsGenerator(), source, expectFailure);

    static GeneratorResult Run(IIncrementalGenerator generator, string source, bool expectFailure) {
        // ⚠ The assembly name is not decoration: TrinixServiceGenerator derives the
        // marshalling helper's namespace from it, so every generated proxy in these runs
        // says `using static Trinix.Sdk.Generators.UnderTest.Generated.ServiceWire`. It was
        // Trinix.Services.Contracts.UnderTest while this harness ran one generator; a
        // settings schema compiled into an assembly named after the contracts is a name
        // that would mislead the next person reading a failure.
        var compilation = CSharpCompilation.Create(
            "Trinix.Sdk.Generators.UnderTest",
            [CSharpSyntaxTree.ParseText(source)],
            References,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable)
        );

        var driver = CSharpGeneratorDriver
            .Create(generator)
            .RunGeneratorsAndUpdateCompilation(compilation, out var updated, out var diagnostics);

        var run = driver.GetRunResult();
        var files = run.Results
            .SelectMany(result => result.GeneratedSources)
            .ToDictionary(
                generated => generated.HintName,
                generated => generated.SourceText.ToString(),
                StringComparer.Ordinal
            );

        var errors = diagnostics
            .Concat(updated.GetDiagnostics())
            .Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .ToList();

        // ⚠ The generated code is compiled, not just produced. A generator test that only
        // string-matches its own output cannot tell the difference between emitting the
        // right thing and emitting something that looks right and does not build — which
        // is the failure mode this whole suite exists to catch, given that the generated
        // code is what stands between the daemon and every application.
        if (!expectFailure && errors.Count > 0) {
            throw new InvalidOperationException(
                "the generated compilation did not build:\n"
                + string.Join("\n", errors.Select(error => error.ToString()))
            );
        }

        return new GeneratorResult(files, [.. errors]);
    }
}

/// <summary>What one generator run produced.</summary>
/// <param name="Files">Generated sources, by hint name.</param>
/// <param name="Errors">Every error, the generator's own and the compiler's.</param>
sealed record GeneratorResult(IReadOnlyDictionary<string, string> Files, ImmutableArray<Diagnostic> Errors) {
    /// <summary>The generated file with this hint name.</summary>
    public string File(string hintName) {
        Assert.True(Files.ContainsKey(hintName), $"no generated file called '{hintName}'; got: {string.Join(", ", Files.Keys)}");
        return Files[hintName];
    }

    /// <summary>The distinct Trinix diagnostic ids that were reported.</summary>
    public IReadOnlyList<string> Ids => [.. Errors.Select(error => error.Id).Distinct().Order(StringComparer.Ordinal)];
}
