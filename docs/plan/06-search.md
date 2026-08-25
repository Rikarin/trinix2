# 06 — Search

Called **Beacon**, and it is a system service rather than an application — the brief is right about
that and it is the decision everything else follows from. ⌘Space, one field, and the first result is
correct.

## The shape

```
trinix-beacon              the index, the query planner, the ranker
 ├── Index                 SQLite + FTS5, per-user, in ~/Library/Beacon/
 ├── Watchers              inotify over the indexable roots, coalesced
 ├── Extractors            one per content type, sandboxed, out of process
 ├── LiveProviders         asked at query time, 50 ms or dropped
 └── Actions               what a result *does*
trinix-shell               the field, the results list, the preview  (doc 03)
```

## Two kinds of provider, and why the distinction is load-bearing

| | **Index contributor** | **Live provider** |
|---|---|---|
| Example | Notes writing its note titles; the file watcher; the app catalogue | A calculator; unit conversion; a running application's open documents; a web suggestion |
| When it runs | Whenever its data changes | On every keystroke, in parallel with the index query |
| Budget | Unbounded, in the background, `nice`d | **50 ms.** Late answers are discarded, not waited for |
| Failure | Stale results | The provider is dropped from this query and, after three timeouts, from the session |
| Who may register | Any application, scoped to its own identity | Any application, but the shell shows the app's name beside the result |

Everything the brief lists sorts into one of these, and the sorting is the whole design. Applications,
files, settings, documentation, packages, recent documents, contacts, calendar, messages and browser
history are **indexed**. Arithmetic, conversion, actions, and "what does this running app have open"
are **live**. Getting one of these wrong is how a search field ends up feeling slow: a browser-history
provider that is asked live will one day take 400 ms, and every keystroke will wait for it.

⚠ **Nothing is asked live that touches a disk or a network.** That is the rule; a "search the web"
provider is a *deferred* result — the row appears immediately as an action, and it does not query
anything until it is chosen.

## The index

| Decision | Reason |
|---|---|
| **SQLite with FTS5** | ⚠ Adds a base recipe Trinix does not have. It buys a proven full-text engine, transactional updates, `WITHOUT ROWID` compactness and no server, and the alternative is writing an inverted index — which is a month and a class of bugs for something that will still be worse. It is also what doc 02's settings store and doc 03's notification history want anyway, so one recipe serves three |
| One index per user, in the user's own directory, never shared | A shared index is a cross-user information leak and a permissions problem with no good answer |
| Indexed roots are the user's own: `~/Documents`, `~/Desktop`, `~/Downloads`, `~/Pictures`, `~/Library/Notes`… | Not `/usr`, not `/nix`-shaped stores, not other users. A default that indexes the whole filesystem is how a laptop spends its first afternoon at 100 % CPU |
| Per-app containers are indexed **only** via that app's contributions | The container is the app's; Beacon does not walk into it |
| Exclusions in Settings, and honoured immediately including retroactive deletion | An exclusion that only affects future indexing is not a privacy control |
| Content extraction is **out of process and sandboxed** | An extractor parses hostile input by definition. It runs in a transient unit with no network, no filesystem beyond the one fd, and a 10 s timeout. This is the same machinery doc 04 built and it is the second-best reason to have built it |
| Encrypted volumes and network mounts are not indexed by default | Indexing a mounted share means the index becomes a copy of somebody else's data |

**Extractors, 1.0 set:** plain text and code · PDF · Markdown · HTML · images (EXIF, and file name;
not contents — no OCR in 1.0) · audio and video metadata · archives (names only, not contents) ·
`.app` bundles (identity, category) · Office formats via a bundled parser or not at all.

The index holds name, path, type, size, dates, the extracted text, the tags, and **the use signal** —
how recently and how often the user opened it, which matters more for ranking than any text score.

## Ranking

The part that decides whether the feature is loved or ignored. In order of weight:

1. **Exactness of the match on a name** — a prefix match on a file name beats a body-text hit, always.
2. **Recency and frequency of the user's own use**, decayed. The application you launch fifty times a
   day is the first result for its first letter by the second day.
3. **Type priority for the query shape** — a single word that names an application is an application.
4. **Body text**, BM25 from FTS5.
5. **Provider-declared confidence**, clamped so no provider can promote itself to the top.

Two rules keep it honest: **the top result must be stable while more results arrive** — a list that
reorders under the cursor causes wrong launches, so late results append and never displace the
selection — and **the ranking is learnable but resettable**, with a "reset search learning" button,
because a mis-trained ranker is otherwise permanent.

## Actions

A result is a thing *and* a verb. `⌘Space` is a launcher, a file opener, a settings jump, a
calculator and a command line, and the last is what the brief was reaching for.

| Typed | First result |
|---|---|
| `notes` | the Notes application, launched |
| `tax.pdf` | the file, opened — with ⌘↵ to reveal in Files and Space to Quick Look it (doc 07) |
| `wifi` | Settings ▸ Network ▸ Wi-Fi, opened at that pane |
| `2 + 2 * 8` | `18`, copyable with ↵ |
| `100 usd in eur` | ⚠ a conversion, **only** if the user has enabled the rates source. Otherwise absent — a currency converter is a network call and doc 00 § No telemetry means the system does not make one silently |
| `12 dec 1994` | that date, its weekday, and days elapsed |
| `restart`, `sleep`, `lock`, `log out` | the system verb, with a confirmation |
| `shutdown in 30 minutes` | a scheduled action, handed to doc 14's automation engine |
| `#work` | everything tagged `work` (doc 07 § Tags) |
| `> git status` | run in a terminal — the `>` prefix is the explicit escape into a command, so that no bare word is ever executed |

⚠ **No bare query ever executes anything.** A search field that runs `rm` because it happened to
match a binary in `$PATH` is a search field that will eventually delete something. The `>` prefix, or
an explicit "Run" row that must be selected, is the only path to execution.

Applications contribute actions by declaring **verbs** in `Info.json` (doc 14 defines the verb
vocabulary, and one declaration serves both search and automation — this is why they share it).
A verb declares its name, its parameters and whether invoking it needs the app running.

## The interface

- ⌘Space anywhere, including over a fullscreen window and on the lock screen — where it searches
  nothing and says so, rather than being absent and feeling broken.
- Results grouped by kind, top result pre-selected, ↑↓ to move, ↵ to act, ⌘↵ for the secondary action,
  Space for Quick Look. Typing continues to refine; the field never loses focus to the list.
- A preview pane on the right for the selected result, which is Glance (doc 07) doing the work.
- **120 ms from keystroke to a redrawn list**, at the 95th percentile, on the reference machine, with
  the index warm — a measured gate in doc 16, not a target.
- The field is `Trinix.Sdk.Controls.SearchField` so an application can host the same thing (Files'
  search field *is* Beacon, scoped to a directory).

## Effort

| Piece | EM |
|---|---|
| SQLite/FTS5 recipe + the index schema, writer, watcher, coalescing | 2.0 |
| Extractor host, sandboxing, and the nine extractors | 2.0 |
| Query planner, ranker, use-signal store, learning + reset | 1.5 |
| Live provider protocol, timeouts, the calculator and date/unit providers | 1.0 |
| Actions, the verb vocabulary's search half, system verbs | 1.0 |
| The shell's search UI, preview pane, keyboard model | 1.5 |
| **Total** | **9.0** |
