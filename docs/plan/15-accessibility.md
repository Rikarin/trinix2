# 15 — Accessibility

The brief says "don't bolt this on later", which is correct and is usually said by plans that bolt it
on later. The way to not bolt it on is to make the accessible path the *only* path for a small number
of things, so that inaccessible code does not compile or does not pass a test.

## Where it actually has to live

Accessibility for Trinix is three layers, and only the middle one is ours to invent.

```
Vixen.Ui             every control exposes a role, a name, a value, a state, and its relations
Trinix.Sdk           the AT-SPI bridge, the system settings, the checker
Assistive tools      screen reader, magnifier, switch control, voice control
```

⚠ **The bottom layer is Vixen's and it is not finished.** Vixen's controls have accessibility in the
design (doc 09 of its plan lists it as part of every control's base API), and what exists is not
audited. The honest sequencing is that a Trinix accessibility gate cannot be built before Vixen's
accessibility tree is real, and getting it real is a contribution to Vixen, not work in this
repository. Doc 18 R6 carries it.

## The bridge

Trinix implements **AT-SPI2** over D-Bus — the Linux accessibility interface — as the compat face
(doc 02) over Vixen's accessibility tree.

| Choice | Reason |
|---|---|
| AT-SPI2, not a Trinix protocol | It is what every existing assistive tool on Linux speaks, including Orca. A native protocol means writing every assistive tool ourselves, which is not a thing a small project does |
| The bridge is **in the SDK**, per-application, not a central daemon crawling processes | An application owns its own tree; a central crawler would need `system.input`-class authority over every application, which doc 04 will not grant |
| ⚠ AT-SPI's shape is GTK's, and it is chatty | A naïve bridge makes a round trip per node. The bridge caches the tree and pushes changes, which is what every non-GTK implementation ends up doing, and it should be built that way from the start |
| The compositor exposes the **window** tree; applications expose their contents | So a screen reader can say "Files, window 2 of 3" without every application knowing about windows |

## What ships in 1.0

| Feature | Shape | Note |
|---|---|---|
| **Full keyboard operability** | Every control reachable and operable; a visible focus ring that meets contrast; no keyboard trap | Not a feature — a gate. Doc 16 walks every first-party window with a headless keyboard driver |
| **Text scaling** | A system text-size setting that every application follows through the design tokens (doc 01), plus full-UI scaling through doc 03 | Because it is a token, an application cannot fail to follow it |
| **Reduce motion** | The `Motion.cs` durations collapse; no parallax, no zoom transitions | Doc 01 § design language explains why this is a token and not a branch |
| **Increase contrast, reduce transparency** | Alternative token sets | Same mechanism again — three accessibility settings for the price of a token file is the argument for having one |
| **Colour filters** | Protanopia/deuteranopia/tritanopia and greyscale, as a compositor output LUT | System-wide including XWayland and Flatpak, because it is below every client |
| **Zoom / magnifier** | Compositor-level, follows the pointer and the keyboard focus | Must be the compositor's: a magnifier as a client cannot magnify the shell |
| **Sticky keys, slow keys, key repeat, mouse keys** | In the compositor's input layer | Also below every client |
| **Captions styling, mono audio, balance** | Settings + PipeWire | Cheap |
| **Screen reader** | ⚠ **Orca, packaged against the Desktop runtime** (doc 13), not a Trinix screen reader | Below |
| **Voice control, switch control, dwell** | Cut from 1.0 | Below |

### Two honest decisions

**The screen reader is Orca.** Writing one means speech synthesis, braille display support, a script
system, and years of accumulated knowledge about how blind users actually navigate. Orca is that
knowledge. It is Python and GTK, it will look like a foreign object on this desktop, and it will work
— which is the trade a person who needs a screen reader would choose. ⚠ It needs a speech synthesiser
(`speech-dispatcher` + a voice) in the runtime, and that is a recipe cost of about 0.5 EM that is easy
to forget until the day it does not talk.

**Voice control and switch control are cut**, and not because they matter less. Voice control done
badly is worse than absent, it needs a speech recogniser and a whole-system command grammar, and the
grammar is doc 14's verb vocabulary — so the *right* time to build it is after automation exists,
where it is a front end rather than a subsystem. That is a real plan for later rather than a
euphemism.

## The gate

Accessibility survives only if it is checked mechanically. Three checks, all in doc 16's CI:

1. **`trinix doctor --a11y`** on every first-party bundle: every interactive element has a role and an
   accessible name; every image has a label or is marked decorative; no control is keyboard-unreachable;
   contrast of the theme's token pairs meets 4.5:1 for text and 3:1 for UI.
2. **A headless keyboard walk** of every first-party window through `Vixen.Ui.Testing`: Tab from the
   first element returns to it, every control is reached, and every control reports a name.
3. **An AT-SPI conformance probe**: the bridge's tree for a reference window matches an expected shape,
   so a refactor that drops the tree fails the build rather than being noticed by a user.

⚠ These three catch mechanical failures and cannot catch an interface that is technically labelled and
practically unusable. That needs a person who uses a screen reader to try it, and the plan should say
out loud that no amount of CI substitutes for that.

## Effort

| Piece | EM |
|---|---|
| AT-SPI2 bridge in the SDK, with caching | 2.0 |
| Compositor: magnifier, colour filters, input accessibility | 1.5 |
| Token sets: contrast, transparency, motion, text size, and their plumbing | 1.0 |
| Settings pane | 0.5 |
| The three gates | 1.0 |
| Orca + speech-dispatcher packaging | 0.5 |
| Vixen-side accessibility tree work (⚠ upstream, not counted in Trinix's total) | (2.0) |
| **Total** | **6.5** |
