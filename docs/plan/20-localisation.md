# 20 — Localisation

⚠️ **Extends [01](01-system-sdk.md) § Open, [11](11-core-applications.md) § What every one of these
must do, and [15](15-accessibility.md); and corrects [08](08-settings-and-system-services.md) § Time,
language, region.** It answers [18](18-risks-and-open-questions.md) R7, which is a decision and not a
work item.

R7 states the problem correctly and then names the wrong thing as the decision. "Which library" is not
the question, and neither, really, is "English-only or not" — because **the choices that are expensive
to reverse are not the translations.** They are the shape of a call site, the shape of a stylesheet,
and one line in a lookup function. All three cost nothing to get right today and cannot be swept later
by any tool.

So this document is mostly about what has to be true *before* the first `.vxml` is written, and it
recommends shipping 1.0 in English.

## What is at stake, sorted by whether you can change your mind

The retrofit cost R7 warns about is real but it is not evenly spread. Some of it is a sweep a person
can do in an afternoon with a compiler error to guide them; some of it is invisible and unfindable.

| Choice | Reversible? | What changing it later costs |
|---|---|---|
| `Label = "Save"` at a call site | Expensive but **findable** | A compiler-guided sweep. This is the cost R7 counts, and it is the *smallest* of the three real ones — an analyser can point at every one |
| A sentence built by concatenation — `"Deleted " + n + " files"` | **One-way** | There is no analyser for it. The fragments are grammatical in English and meaningless anywhere else, and finding them means reading every window. [11](11-core-applications.md) already forbids it; this is why |
| `$"{n} item(s)"` — a call site with no plural *category* | **One-way** | The call site does not carry the number in a form a rule can be applied to. Czech needs four forms, Arabic six. Rewriting the call site is the same work as writing it right, done twice, in forty windows |
| `value.ToString()`, `size.ToString()`, `date.ToString()` | **One-way, and the worst of them** | Invisible. It compiles, it renders, it is correct in English, and no grep finds it because the string never appears in the source. Every one of these is a locale bug that ships |
| Physical edges in the theme and in markup — `pl-4`, `ml-2`, `text-left`, `rounded-l` | **One-way in practice** | It is a per-element judgement, not a rename: `ml-2` between a checkbox and its label must flip, `ml-2` on a resize handle must not. Forty windows of that is the RTL retrofit everyone tells the same story about |
| `Info.json`'s single, signed `name` | One-way **after the first third-party bundle exists** | Today it is a schema addition to a format nothing outside this repository has written. Later it is a revision of a signed manifest and a change to the verification path |
| The catalogue's file format | **Reversible** | A converter and an afternoon |
| Which translation platform, or whether there is one | **Reversible** | Nothing in the code knows |
| `InvariantGlobalization` per project | **Reversible** | One MSBuild property. ICU is already in the image |
| Adding a language | **Reversible** | Translation and review. Not engineering |
| Adding a `Loc:` directive to VXML | **Reversible** | A change in Vixen, in one file. Not a change in any application |

Read down the "one-way" rows: **not one of them is about translation.** They are about how a call site
is written and how a stylesheet is written. That is the finding this document turns on, and it is why
"English-only" is a defensible answer and "English-only, so we will think about this later" is not.

## What the code says today

Everything in this section was read rather than assumed. Where a conclusion is mine rather than the
code's, it says so.

### Vixen's markup cannot carry a translatable string as markup — and does not need to

| Fact | Where |
|---|---|
| There is **no markup-extension syntax**. `{` and `}` are ordinary text inside an attribute value; the lexer's attribute-value mode recognises exactly the closing quote, `@@` and `@` | `Core/Vixen.Ui.Markup/Parsing/VxmlLexer.cs`, `StepAttributeValue` |
| There is **no extension point** for one. The attribute directives are a hardcoded `if`-chain — `key`, `ref`, `refs`, `tag`, `use`, `bind:`, `change:`, `on:` — and any other `prefix:` is diagnostic **VXML2006** | `Core/Vixen.Ui.Markup/Binding/Binder.cs`, `Classify` |
| A literal attribute compiles to a **verbatim C# string literal**, through exactly three shapes: `ctx.Text(parent, "…")`, `ctx.Attribute(n, "name", "…")`, `Literals.Of(n.Prop, "…")`. No table, no indirection, **no interception hook** | `Core/Vixen.Ui.Markup/Emit/ComponentEmitter.cs` |
| There is **no `x:Uid` and no element identity of any kind**. Elements are locals `n0`, `n1`, … from a per-file counter, and the markup README says the absence of `id` is a decision rather than an omission | same file; `Core/Vixen.Ui.Markup/README.md` |
| ⚠ **But `@` interpolation already does the whole job.** `Label="@Strings.Save"` lexes, binds and compiles today with no change to the markup compiler, and a C# string literal survives inside the same quotes, so `Text="@Loc("app.save")"` also works | `VxmlLexer.LexInterpolation` / `SkipCSharp`, pinned by `Vixen.Ui.Markup.Tests/ParserTests.cs` |
| Vixen's own editor **already localises its markup this way** — `Label="@EditorStrings.TasksCancel.Text"` | `Editor/Vixen.Editor.Ui/Tasks/TaskCenter.vxml` |
| Every `@expr` becomes a region-scoped `Effect`, so a bound string **re-evaluates whenever a signal it read changes** | `Core/Vixen.Ui/Composition/BuildContext.cs`, `Bind` / `Text` |

The absence of element identity settles the keying question, and settles it the right way. **A key
cannot be derived from where a string sits in the markup**, and it should not be: a positional key
survives a reorder of a stack panel by silently attaching every translation to the wrong control. The
id must be authored.

### Vixen already has the catalogue — in the wrong assembly

`Editor/Vixen.Editor.Ui/Localisation/` is three files and 123 declared strings, and it is the design
this document would otherwise have had to invent:

```csharp
public readonly record struct StringId(string Id, string Source) {
    public string Text => Strings.Get(this);
    public override string ToString() => Text;
}

public static StringId MenuFile { get; } = new("editor.menu.file", "File");
```

Its own doc comment makes the argument: *"`item.Label = EditorStrings.Save.Text` is no more work at
the call site than `item.Label = "Save"`, so there is never a reason to write the literal"*. The source
text lives at the declaration rather than in an `en` catalogue, so a missing catalogue shows English
rather than `editor.menu.file`. `StringCatalog` is a flat BCP-47-tagged id→text map, `Strings.Missing`
is the translator's worklist, and `Strings.Template(language)` exports one.

Four things about it matter to Trinix:

- **It is in `Vixen.Editor.Ui`**, which is not in Trinix's dependency closure —
  [`vendor/vixen/`](../../vendor/vixen/) is the 41-package closure of `Vixen.Ui.Desktop` and this is not
  in it. Neither, incidentally, is `Vixen.Ui.Markup`: **no Trinix project references it and there is not
  one `.vxml` in this repository yet.**
- **`Strings.Resource`, the generator that would emit those declarations from a catalogue, is planned
  and not built** — Vixen's doc 11 asks for it, `EditorStrings.cs` says it is *"written by hand until it
  does"*. So "an id used nowhere and an id declared nowhere are both build errors" is a promise on both
  sides of the fence.
- ⚠ **`Strings` is a plain static with a plain `event`, so changing the language does not re-label what
  is on screen.** The class says so itself, and its answer is to rebuild the menu bar and ask for a
  restart for everything else. Reading `Strings.Get` inside a Vixen binding registers **no dependency**,
  because the catalogue is a field and not a `Signal<T>`.
- Vixen's own controls carry about a dozen hardcoded English strings — `"Clear"`, `"Close"` twice,
  `"Dismiss"`, `"Show suggestions"`, `"Previous tab"`, `"Next tab"`, `"Reset"`, `"Search"`,
  `"Intensity"`, `"Pick a colour from the screen"`. A dozen, not a thousand: the upstream ask is small.

### Text, layout and direction: better than [01](01-system-sdk.md) claims, with two named seams

Doc 01's table says Vixen's text has "bidi, line breaking". That undersells it, and doc 12's claim that
"font fallback, combining marks and bidi" come free is true right up to the two seams named at the
bottom of this table.

| Fact | Where |
|---|---|
| **UAX #9 bidi**, hand-written, no ICU and no fribidi, gated against the Consortium's conformance cases | `Core/Vixen.Ui.Text/BidiAlgorithm.cs`, `BidiState.cs`, `BidiRunSequence.cs`, `BidiConformanceTests.cs` |
| **UAX #24 script itemisation**, cutting runs on bidi level *and* script; HarfBuzz's direction and script are **set explicitly** rather than guessed, and the language is deliberately left unset so shaping is not machine-dependent | `TextItemizer.cs`, `TextShaper.ShapeRun` |
| **UAX #14 line breaking** over generated Unicode 17 tables, with East Asian width and `KeepAll`/`BreakAll` | `LineBreaker.cs`, `Generated/LineBreakTable.g.cs` |
| **The layout engine is genuinely RTL-aware**, and the flip lives in essentially one place: `FlexAxis.Resolve` swaps `Row`↔`RowReverse` under `Direction.Rtl`, and `StyleResolution` resolves logical `Start`/`End` edges over physical `Left`/`Right`. Threaded through block, float, inline and grid layout, pinned by Taffy RTL fixtures | `Core/Vixen.Ui.Layout/FlexAxis.cs`, `LayoutEnums.cs`, `StyleResolution.cs` |
| **VCSS has logical utilities**: `ps-*`/`pe-*`, `ms-*`/`me-*`, `start-*`/`end-*`, `border-s`/`border-e`, `rounded-ss/se/ee/es`, `float-start`/`float-end`, and `text-align: start/end` resolved against `direction` at paint | `Core/Vixen.Ui.Styling.Utilities/UtilityFamilies.cs`, `Core/Vixen.Ui/DrawListBuilder.cs` |
| ⚠ **Seam 1: `direction` never reaches the shaper.** `ParagraphDirection` has zero references outside `Vixen.Ui.Text`; `UiElement` shapes without it, so every paragraph is `Auto` — first-strong-character. An element styled `direction: rtl` whose text starts with a Latin word still gets base level 0 | `Core/Vixen.Ui/UiElement.cs` |
| ⚠ **Seam 2: bidi reordering does not cross a font-fallback boundary.** `FontRegistry.Cover` splits by coverage *before* shaping, each span resolves bidi independently, and `TextRun` carries no level — so Hebrew and Latin on two different faces lay out in logical order | `Core/Vixen.Ui/TextLine.cs`, `FontRegistry.Cover` |
| **No `.vcss` in Vixen declares `direction:`**, and no Vixen control sets it. RTL is implemented and unexercised | grepped all 17 stylesheets |
| **All 53 `CultureInfo` uses in the UI assemblies are `InvariantCulture`**, including `BuildContext.Format`, which formats every `@expr` that is not already a string | `Core/Vixen.Ui/Composition/BuildContext.cs` |

That last row is doc 08's boundary appearing in a place doc 08 does not mention: **`@count` and
`@modified` in a `.vxml` render invariantly no matter what `CurrentCulture` says**, because Vixen
formats them itself. Turning ICU on in a Trinix application does not fix a number that Vixen formatted.

### Trinix, today

| Fact | Where |
|---|---|
| **`InvariantGlobalization=true` applies to every Trinix project**, applications included — and is *additionally* forced on the publish command line for applications | [`src/Directory.Build.props`](../../src/Directory.Build.props), [`src/pack-apps.sh`](../../src/pack-apps.sh), [`src/publish.sh`](../../src/publish.sh) |
| **ICU 77.1 is in the base image**, full data, described in its own recipe as *"the largest single library in the base by some margin"* | [`base/recipes/icu/recipe.sh`](../../base/recipes/icu/recipe.sh) |
| ⚠ .NET dlopens ICU **by soname**, and the recipe's own check says a version bump *"will make the runtime fall back to invariant mode silently rather than fail loudly"* | same file |
| **The only font is DejaVu**, seven faces, and there is **no fontconfig and no freetype in the base by design** | [`base/recipes/dejavu-fonts/recipe.sh`](../../base/recipes/dejavu-fonts/recipe.sh) |
| **Vixen registers no fallback face by default** — `AddFallback` is called only in its own tests — so out of the box the chain is one face | `Core/Vixen.Ui/FontRegistry.cs`, `Platform/Vixen.Ui.Desktop/SystemFonts.cs` |
| **There is no input method.** `TrinixTextInput` is a stub: `HasOnScreenKeyboard => false`, `SetCandidateArea` empty, and its own remark names `text-input-v3` as what is missing. No IME recipe exists | [`src/Trinix.Platform/TrinixServices.cs`](../../src/Trinix.Platform/TrinixServices.cs) |
| **The keyboard layout is whatever xkb's defaults are** — `xkb_keymap_new_from_names(context, NULL, …)`, no layout selection and no switching | `base/recipes/trinix-wlr/src/trinix-wlr.c` |
| `Info.json`'s `name` is a **`required string`** in the signed manifest — one name, no language | [`src/Trinix.Bundle/BundleInfo.cs`](../../src/Trinix.Bundle/BundleInfo.cs) |
| The word "localisation" appears in this repository **twice**: as a promise in doc 01's `trinix doctor` and as a directory comment in `app-bundles.md` | grepped |

## The decision

> **Trinix 1.0 ships in English. Every user-visible string is in a catalogue from the day it is
> written, every number and date goes through a formatter, and the theme is written in logical edges.
> The first translation is a data change and a language selector, not an engineering project.**

| Decision | Reason |
|---|---|
| **English-only for 1.0** | Not because translation is expensive — because the things Trinix cannot yet *do* for a non-English user are larger than the strings. It cannot type Japanese (no `text-input-v3`, no IME), cannot draw Chinese (one font, no fallback face registered), and cannot switch keyboard layout. Shipping a Japanese Settings pane on a machine that renders Japanese as boxes is worse than shipping English |
| **The catalogue exists from the first string, not from the first translation** | The one-way doors above. A catalogue with exactly one language in it costs the id next to the text and buys every later language for the price of translation |
| **The id is authored, never derived** | Vixen's markup emits no element identity and the generated locals are positional (`n0`, `n1`). A positional key would silently re-point every translation when somebody reorders a panel |
| **`StringId(Id, Source)` — the source text lives at the declaration** | Vixen's editor already made this argument and it is right: an application whose fallback is a file shows `settings.network.title` to anyone whose install is missing that file, and a missing file is exactly what a localisation bug looks like. Here the worst case is English |
| **The catalogue is a `Signal<StringCatalog>`, not a static field** | ⚠ This is the one line Vixen's editor got wrong and says so. Every `@expr` in a `.vxml` is already an `Effect`; if the lookup reads a signal, changing the language re-labels the running interface for free. If it reads a field, every application needs a restart forever, and by then there are forty of them |
| **The mechanism goes upstream into Vixen; the catalogues and the tooling stay in Trinix** | [01](01-system-sdk.md)'s scope rule: a string a *Vixen control* shows (`"Dismiss"`, `"Previous tab"`) can only be localised by Vixen. A dozen strings and three files is a small pull request. ⚠ If it is refused, `Trinix.Sdk` carries its own copy and those Vixen controls stay English — which is a visible seam and the reason to ask first |
| **No language ships until it is complete and owned** | Below, § Who translates |

⚠ **This does not amend [18](18-risks-and-open-questions.md) R7's recommendation — it substantiates
it.** R7 already says "English-only, strings extractable, decided *now* rather than by default". What
was missing was what "extractable" costs and what it actually consists of, and the answer is longer
than the phrase suggests.

## The string catalogue

### Where a string lives

```
Trinix.Apps.Notes/
  Strings.yaml                      the source: id, text, and a note for the translator
  Strings.g.cs                      generated: one StringId property per id, plus All
  bundle/Resources/Strings/
    cs.json  de.json  …             one per language, verified by the Merkle manifest

/usr/share/trinix/strings/<bcp47>.json     the shell's, the SDK's and the services'
```

| Decision | Reason |
|---|---|
| The **source of truth is `Strings.yaml`**, and the C# declarations are generated from it | This is Vixen's owed `Strings.Resource` in Trinix's build. It is what makes "an id used nowhere and an id declared nowhere are both build errors" true rather than aspirational, and `Trinix.Sdk.Generators` already exists to hold it |
| Shipped catalogues are **JSON**, not the YAML the source is written in | [`app-bundles.md`](../app-bundles.md) § Why JSON and not TOML applies unchanged: `System.Text.Json` has a source generator, so a catalogue parses with no reflection and survives NativeAOT. ⚠ `Vixen.Core.Yaml` is a real assembly and is **not** in Trinix's vendored closure, so YAML at runtime would add a package to the pin for no gain |
| Catalogues live **inside the signed bundle** | A translation is content, so it is covered by the manifest for free and nobody can drop a hostile catalogue into an installed application. ⚠ The price is that a translation fix is an application update; that is the right side of the trade and it should be said out loud |
| Ids are **dotted paths that say where the string is used**, not what it says — `notes.editor.menu.pin` | Vixen's `StringCatalog` gives the reason: two places that both say "Open" can be translated differently where a language needs them to be, and a diff of a translator's file is readable |
| The **system's** strings are in the image, not in each bundle | The SDK's standard menu, the permission dialog, the file chooser and the error surfaces are the system speaking, and an application must not be able to change or fail to ship them |

### How it reaches a call site

```csharp
// C#
menu.Item(Strings.NotePin, Pin, Key.P | Key.Command);
label.Text = Strings.ItemsSelected.With(count);          // never $"{count} selected"
```

```html
<!-- .vxml — this compiles today, unchanged, against the pinned Vixen -->
<Button Label="@Strings.NoteExport" on:click="@Export" />
<heading>@Strings.NotesTitle</heading>
```

Both forms already work. `Label="@expr"` emits `ctx.Bind(() => n.Label = expr)` — a plain C# assignment,
so an `implicit operator string(StringId)` is all it needs — and `@Strings.NotesTitle` as element
content goes through `BuildContext.Text(parent, Func<object?>)`, whose formatter falls through to
`ToString()`, which `StringId` defines as `Text`. **No change to Vixen's markup compiler is required
for any of this**, which is the single most useful fact in this document.

⚠ **What is *not* free is the interpolation.** `BuildContext.Format` renders any non-string with
`InvariantCulture`, so `@file.Modified` in markup is invariant regardless of the application's culture.
The rule that follows is a rule about markup, not about C#: **a `.vxml` interpolates strings, never
values.** Formatting happens in the view model, through the SDK's formatter, and the markup binds the
result. An analyser in `Trinix.Sdk.Generators` can see this — the bound expression's type is Roslyn's
to know — and `trinix doctor` should fail on it.

### Should VXML get a `Loc:` directive?

| Option | Verdict |
|---|---|
| `Text="@Strings.Save"` — a member reference | **Yes, now.** Zero framework change, statically typed, an id that does not exist is a Roslyn error on the characters between the quotes, and it is what Vixen's own editor does |
| `Loc:Text="app.save"` — a new directive | **No, and it is not urgent.** It is a change in `Binder.Classify` and nothing else, it is reversible at any time, and its only advantage — a shorter attribute — is bought by giving up the compile-time check that the id exists. Revisit if and when Vixen's `Strings.Resource` generator makes the id the primary artefact |
| `Text="{Loc app.save}"` — a markup extension | **Not possible.** There is no extension syntax and no extension point; the braces would be literal text and the string would render as `{Loc app.save}` |

## Plural, gender and ordering

### The invariant boundary is in the wrong place

⚠ [08](08-settings-and-system-services.md) § Time, language, region says *"services invariant,
applications ICU"* and calls it "a decision worth restating here because it will otherwise be
discovered as a bug". Two things are wrong with it.

**First, it is not implemented.** `InvariantGlobalization=true` is in
[`Directory.Build.props`](../../src/Directory.Build.props), which every project inherits, and
[`pack-apps.sh`](../../src/pack-apps.sh) passes `-p:InvariantGlobalization=true` on the application
publish line as well. There is no application in this repository that is not invariant.

**Second, the line is not between services and applications.** It is between **machine-facing and
human-facing text**, and it cuts through both.
[`BundleScannerTests.OrdersOrdinallyRatherThanByCulture`](../../src/Trinix.Bundle.Tests/BundleScannerTests.cs)
already has the argument in it: the signed manifest's file list must sort ordinally, and its comment
spells out that ICU's collation would answer differently. That is a *library* used by an application
(Files verifies bundles) and by a system component (Gatekeeper) and it must be invariant in both. Files'
list view, in the same process, must not be.

| Where | Setting | Reason |
|---|---|---|
| `trinixd`, `trinix-broker`, the compositor, the boot path | **Invariant** | Doc 08's reason stands: no locale database on the boot path, and nothing there shows a person a word |
| `Trinix.Bundle`, `Trinix.Gatekeeper`, anything that hashes, signs, compares versions or orders a manifest | **Invariant, and it is a correctness requirement rather than a size one** | A signature that depends on the user's locale is a signature that fails on somebody else's machine |
| Applications and the shell | **ICU** | Sorting a file list, formatting a date, casing a heading |
| Anything a `.vxml` interpolates | **Neither — it is Vixen's, and it is invariant** | See above. Format before you bind |

**Doing it:** move `InvariantGlobalization` out of `Directory.Build.props`'s shared block into the
projects that need it, drop it from `pack-apps.sh`'s publish line, and ⚠ add a **startup assertion** in
`TrinixApplication.Run` that a known culture-sensitive comparison is not ordinal. The ICU recipe's own
check says the runtime *"will fall back to invariant mode silently"* when the soname moves; one
comparison at startup turns a silent, locale-shaped bug into a crash with a cause.

⚠ **Measure the cost before assuming it is free.** [00](00-vision-and-principles.md) budgets 400 ms
from click to window and 400 MB for an idle session, both as CI gates. Loading full ICU data is not
nothing, and the recipe already flags trimming it as an open exercise. If the launch gate moves, the
answer is trimmed ICU data or app-local ICU — not going back to invariant, which does not have a
correct configuration.

### Plural rules are not something ICU gives you

The most important fact in this section, and the one most likely to be assumed away: **.NET has no
plural API, with or without ICU.** ICU's `PluralRules` is not projected into the BCL, `IStringLocalizer`
has no pluralisation, and turning `InvariantGlobalization` off buys collation, formatting and casing
and no plural categories at all. Whatever else is decided, **plural selection has to be written.**

What it needs, per CLDR: the operands of a number (`n`, `i`, `v`, `w`, `f`, `t`, `e`) and a per-language
rule table selecting from `zero`, `one`, `two`, `few`, `many`, `other`. English uses two categories,
Czech and Polish four, Russian four, Arabic six, Japanese one.

| Decision | Reason |
|---|---|
| **The call site names a plural string and passes the number** — `Strings.ItemsSelected.With(count)` | This is the one-way door. A call site that has already formatted the number into a sentence cannot be given a rule later, and there is no analyser that finds it |
| A catalogue entry for a plural string is **a map of category to text**, and English's map has two keys | So adding Czech adds two keys to that entry, and nothing at any call site changes |
| The message syntax is an **ICU MessageFormat subset**: named placeholders, `plural`, `select`. Not the whole of it | Translators and every translation platform already know MessageFormat, so it is not a dialect we have to teach. The parts left out — `selectordinal`, `spellout`, nested numbers with skeletons — cost nothing to add later and each one is a maintenance surface now. ⚠ Implementing it ourselves rather than taking a package is the [00](00-vision-and-principles.md) rule about wrappers plus the fact that the AOT-clean options are thin |
| **English is selected through the same rule engine as everything else** | An engine exercised only by translations is an engine first exercised by translators. If English goes through `plural`, the path is tested by every screenshot golden in CI |

### Gender

Two different problems that get one name.

- **Agreement with a runtime value.** "Soubor byl smazán" / "Složka byla smazána" — Czech's past
  participle agrees with the gender of the noun. Where the noun is one the application knows about,
  `select` handles it and the *catalogue* carries which form goes with which case. ⚠ Where the noun is
  runtime data — a filename, a device name, a person's name — **it cannot be solved**, and the answer is
  a sentence shaped so that agreement does not arise. That is a rule for whoever writes the English, not
  a feature: prefer "Deleted: {name}" to "{name} was deleted".
- **The user's own gender**, which some languages need in order to address them. Trinix does not ask,
  should not ask, and the strings should not need it. Address the user in a form that does not decline.

### Ordering

Collation is the concrete thing doc 08 was right about: a Czech file list sorted ordinally puts `Čapek`
after `Zeman`, and a German one puts `Ärger` after `Zürich`. The fix is that every list a person reads
sorts through `CompareInfo` for the current culture, and every list a machine reads sorts ordinally —
which is the boundary above, restated as a code review rule. ⚠ Numeric-aware ordering (`file2` before
`file10`) is a *separate* requirement that ICU does not give by default and that Files needs regardless
of language; it belongs in [07](07-files-and-quick-look.md) and is named here so it is not discovered
inside a collation bug.

## Layout: expansion, and direction

### Expansion

Translated interface text is longer. The usual guidance is that a string under ten characters can
double, and that thirty per cent is a floor for anything longer; German, Finnish and Russian are the
ones that bite. (This is received wisdom rather than something measured here.)

The consequences are all decisions about the *English* interface:

- No fixed width on anything containing a word. A `w-24` on a label is a bug even in English, because
  the text size is an accessibility setting ([15](15-accessibility.md)) and already varies.
- A label that can truncate must have a tooltip or an accessible name that does not.
- ⚠ **Pseudolocalisation is the highest-value item in this document for its cost.** A generated
  pseudo-locale that expands every catalogue entry by 40 % and accents its letters — `[Ŝàvé Ćĥàñĝéŝ‥‥]`
  — finds two classes of bug at once, in an English-only 1.0, with no translator involved: anything that
  stays plain English **was never extracted**, and anything clipped **will clip in German**. Wire it into
  [16](16-build-ci-and-testing.md)'s screenshot goldens as a second variant of every window and it runs
  on every pull request forever.

### Right to left

The good news is that this is largely already paid for. Vixen's layout engine resolves logical edges
against `Direction.Rtl`, `FlexAxis.Resolve` flips row axes, VCSS has the full logical utility set, and
Taffy's RTL fixtures pin it. Trinix does not have to build RTL layout; it has to **not preclude it**.

| Decision | Reason |
|---|---|
| **`Trinix.Sdk.Theme`'s `trinix.vcss` uses logical utilities only** — `ps-*`, `ms-*`, `start-*`, `border-s`, `rounded-s`, `text-align: start` | This is the decision with a deadline, and the deadline is **Phase 7**, when the theme is written — not the day an RTL language is proposed. A stylesheet written in physical edges is one file to fix; forty windows written against it are not |
| The **eight `Trinix.Sdk.Controls`** are written the same way | They are the ones every application inherits its geometry from |
| ⚠ The **icon set marks which glyphs are directional** | [01](01-system-sdk.md) budgets 400 glyphs. Back/forward, indentation chevrons, undo/redo, list bullets and progress mirror; a clock, a play triangle and a checkmark do not. Recording this per glyph while they are drawn is free; deciding it afterwards means opening 400 files |
| **Shell mirroring is deferred, explicitly** | Traffic lights, the dock, the menu bar and the sidebar side are [03](03-shell-and-window-management.md)'s, they are compositor and shell policy rather than application layout, and no application is affected by when they are decided. (Judgement: about 1 EM when it happens) |
| ⚠ Vixen's **two seams must be closed before an RTL language is announced**, not before 1.0 | `direction` reaching the shaper, and bidi crossing font-fallback spans. Both are upstream, both are small, and neither blocks an English 1.0 — but an RTL language shipped over them produces text that is subtly wrong in exactly the cases a reviewer cannot read |

## The languages Trinix can actually show

⚠ **This is the section that decides which languages are even candidates, and it is not about strings.**

| Requirement | State | Consequence |
|---|---|---|
| Glyph coverage | DejaVu only, and Vixen registers **no fallback face** | Latin, Greek, Cyrillic and DejaVu's Hebrew and basic Arabic render. **CJK, Devanagari, Thai and Korean render as `.notdef` boxes.** A fallback face is a font recipe plus one `AddFallback` call — cheap, and nobody will notice it is missing until they see the boxes |
| Font selection by language | No fontconfig and no freetype in the base, **by design** | Han unification means a Japanese and a Chinese reader want different glyphs from the same code points. Vixen's `FontRegistry` chain can express this; nothing chooses per language today |
| Typing | **No `text-input-v3`, no input-method protocol, no IME recipe.** `TrinixTextInput` is a stub | Any language needing candidate selection — Chinese, Japanese, Korean, Vietnamese, Thai — **cannot be typed at all.** Not typed badly: not typed |
| Keyboard layout | `xkb_keymap_new_from_names(…, NULL, …)`; no selection, no switching | Even French and Czech need this, and [11](11-core-applications.md)'s Setup Assistant already promises to ask on first boot |

The honest reading: **the languages a translated Trinix could serve on day one are the ones a keyboard
layout alone can type and DejaVu can draw** — the European Latin and Cyrillic set. That is not a small
set and it is not the set most people mean by "localisation". It is the strongest argument in this
document for English-only 1.0, and it is a reason that has nothing to do with strings.

The IME work is real and belongs to other documents: `text-input-v3` and `input-method-v2` in the
compositor ([03](03-shell-and-window-management.md)), an engine packaged in the Desktop runtime
([13](13-compatibility.md)), Vixen's `ITextInput` implemented for Trinix and pre-edit rendering in
`TextEditor` — which Vixen's own README lists as owed. Judgement: 2–3 EM, none of it counted here.

## Who translates, and how strings get out and back

| Decision | Reason |
|---|---|
| The interchange format is **gettext PO**, generated from the catalogue and merged back into it | Not because it is the runtime format — it is not; JSON is. PO is what Weblate, Poedit and every translator already handle, its plural-forms header carries exactly the CLDR categories we need, and `msgmerge` solves the one hard problem in the workflow: what happens to a translation when the English changes. ⚠ XLIFF 2.0 is the better-specified format and has worse tooling; if a platform is chosen that prefers it, the converter is a day |
| The catalogue is **public in the repository from the first commit**, not exported when someone asks | A translator who has to ask for a file is a translator who does not appear. `Strings.Template(language)` is already the shape of this in Vixen's editor |
| A translation platform is **Weblate**, hosted for open source, if and only if someone wants it | ⚠ It is a service to operate or a dependency on someone else's. Until there is a second person, PO files in pull requests are the whole workflow and that is not embarrassing |
| ⚠ **No language ships until it is ≥ 95 % complete and has a named owner** | This is the honest constraint and it is a policy, not a technical one. A half-translated Settings is worse than an English one: it looks broken rather than untranslated, and the support burden lands on the one person who cannot read the bug report. A language with no owner rots at the first release that adds strings |
| A missing entry falls back to the **source text**, never to an id | Vixen's `StringCatalog` already argues this: a catalogue holds no fallbacks because the English lives at the declaration. The worst thing a user ever sees is English |
| `Strings.Missing` is **asserted empty in CI** for any language claiming completeness | It exists already, in Vixen's editor, for this purpose |

**Can this project sustain it?** [18](18-risks-and-open-questions.md) R1 says the plan is four years
for four people and there is one. So: **no, not as a commitment.** What it can sustain is the machinery
— which is engineering, is bounded, and is in the effort table below — plus a public catalogue that
makes a contributed translation a pull request rather than a project. Anything beyond that is a
statement about people who do not exist yet, and this plan does not make those.

## What "English-only but extractable" specifically requires

It is only cheap if it is all of this. Any one of them omitted turns the 1.1 translation back into the
retrofit R7 warns about.

1. **`StringId` and a catalogue**, in Vixen if it will take them and in `Trinix.Sdk` if it will not,
   with the source text at the declaration.
2. **A generator** in `Trinix.Sdk.Generators`: `Strings.yaml` → declarations, an `All` list that is data
   rather than reflection, and both "declared and unused" and "used and undeclared" as build errors.
3. **The catalogue read through a `Signal<T>`**, so a language change re-labels a running interface.
   One line, and the difference between that and a restart-only design is permanent.
4. **Plural selection implemented and used by English**, so no call site ever formats a count into a
   sentence.
5. **No concatenation** — [11](11-core-applications.md)'s rule — enforced by there being no API that
   makes it convenient.
6. **One formatter for dates, numbers, byte sizes, durations and relative times**, in `Trinix.Sdk`, used
   by every application. This is the invisible one-way door: `bytes.ToString()` leaves no trace to find.
7. **`.vxml` binds strings, never values**, with an analyser, because Vixen formats interpolations
   invariantly.
8. **The theme and the SDK controls in logical edges**, decided in Phase 7.
9. **`trinix doctor --strings`** — the check [01](01-system-sdk.md) already promised and
   [16](16-build-ci-and-testing.md) already runs — failing on a user-visible literal, on a concatenated
   sentence, on a count without a category, and on a missing catalogue entry.
10. **A pseudo-locale in the screenshot goldens**, which is what makes 9 honest, because a checker that
    is not itself checked drifts.
11. **`Info.json` reserves an optional `names` map now**, while the schema is `trinix.bundle/1` and no
    third-party bundle exists. Reserving it is a nullable property; adding it later is a revision of a
    signed format.
12. **The language preference is a settings-store key with a change event**, which
    [02](02-services-architecture.md)'s store already provides, surfaced by
    [08](08-settings-and-system-services.md)'s Time & Language pane and asked once by
    [11](11-core-applications.md)'s Setup Assistant.

## Gaps, written down as gaps

- **Vixen must be asked for a short list**, none of it large, none of it scheduled by this plan, and
  [18](18-risks-and-open-questions.md) R5 already names localisation as an exposure: promote
  `StringId`/`Strings`/`StringCatalog` out of `Vixen.Editor.Ui`; make the catalogue a signal; plumb
  `direction` into shaping; carry the bidi level across font-fallback spans; put the dozen control
  literals through the catalogue; and build the `Strings.Resource` generator Vixen's own plan already
  owes itself. ⚠ If they are not asked for by Phase 7 they will not
  be there for Phase 9, which is the same failure mode [15](15-accessibility.md) and R6 already have.
- **This document cannot add the back-references its own convention requires.** [01](01-system-sdk.md)
  § Open, [11](11-core-applications.md), [15](15-accessibility.md) and [08](08-settings-and-system-services.md)
  § Time, language, region should each point here, and 08's sentence about the invariant boundary should
  be corrected rather than merely contradicted from a later document.
- **No font fallback face is chosen.** Noto Sans CJK is 20 MB per weight and the base is already carrying
  a 30 MB ICU; whether it goes in the image, in a runtime bundle, or is downloaded on demand is a real
  decision that nobody has made, and until it is made a Chinese filename in Files renders as boxes.
- **Accessible names are strings too.** [15](15-accessibility.md)'s gate checks that every element *has*
  an accessible name; nothing checks that the name is in the catalogue. An interface that is fully
  localised visually and English to a screen reader is a specific and humiliating failure, and the fix is
  one more rule in `trinix doctor --a11y`.
- **Nothing here covers content**, only interface: sort order inside Beacon's index, the collation of a
  search query against indexed text, and word segmentation for languages without spaces are
  [06](06-search.md)'s and are not costed.
- ⚠ **The ICU-load cost against [00](00-vision-and-principles.md)'s 400 ms launch budget is unmeasured**,
  and the measurement should happen in Phase 7 rather than being discovered by a latency gate in Phase 12.

## Effort

| Piece | EM |
|---|---|
| `StringId`, `StringCatalog`, the signal-backed lookup — in `Trinix.Sdk` or as the upstream port | 0.5 |
| `Strings.yaml` → declarations generator, with unused/undeclared as errors | 0.5 |
| Message formatting: named placeholders, an ICU MessageFormat subset, CLDR plural rules and their table | 0.75 |
| The formatter — dates, numbers, sizes, durations, relative times — and the collation helpers | 0.5 |
| Fixing the invariant boundary: the props split, the publish lines, the startup assertion, the launch measurement | 0.25 |
| `trinix doctor --strings`, the `.vxml`-binds-values analyser, and the CI gate | 0.25 |
| Pseudo-locale generation and a second golden per window | 0.25 |
| Logical edges in the theme and the eight controls | — (inside [01](01-system-sdk.md)'s 2.0 + 1.5, if written that way from the start) |
| **1.0 total, English-only and extractable** | **3.0** |
| | |
| Vixen upstream: the promotion, the signal, the two text seams, the dozen control strings | (1.5) |
| Later — PO export and merge, and whatever a translation platform needs | 0.25 |
| Later — the **first actual language**, once all of the above exists | 0.25 + translation |
| Later — the first **RTL** language: Vixen's seams closed, shell mirroring, directional icons, and a review by somebody who reads it | 2.0 |
| Later — input methods, so CJK is typable at all: [03](03-shell-and-window-management.md) and [13](13-compatibility.md), not counted here | (2.5) |

**Against doing none of it.** [11](11-core-applications.md)'s applications plus the shell are on the
order of sixty engineer-months of interface. A retrofit across that surface — extraction, the
concatenated sentences that no tool finds, every `ToString()`, and the physical-edge sweep — is
conventionally a tenth to a fifth of the interface it touches. Call it **8 EM**, and it is my estimate
rather than a measurement. Against 3 EM now, that is R7's "several times" made specific: the ratio is
not the interesting part, though. **The interesting part is that half the retrofit is work no compiler
can point at**, and that is what makes it the kind of task that gets started and then quietly stops at
70 %.

The 3.0 EM belongs in **Phase 7**, beside the SDK and before the first application, for the same reason
[16](16-build-ci-and-testing.md)'s first 1.5 EM does: an SDK whose localisation arrives after its first
three applications is an SDK with three applications to fix.
