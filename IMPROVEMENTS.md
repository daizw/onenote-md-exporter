# OneNote-Md-Exporter — Incremental Export & Conversion-Quality Improvements

This branch adds four improvements to `alxnbl/onenote-md-exporter`, all driven through
the existing CLI with no change to the default behaviour, plus two cherry-picked upstream
correctness fixes (§5) and a note on math/equation export (§6):

1. **Incremental export** (`--incremental`) — skip pages unchanged in OneNote since the
   last run, and resume an interrupted export instead of restarting from scratch.
2. **Cleaner image references** — stop emitting the opaque cache GUID as image alt text.
3. **Complex-table rendering fix** — guarantee raw HTML tables are recognised as HTML
   blocks by strict GFM/CommonMark renderers (GitHub, Obsidian).
4. **Broken-image self-check** — after writing each page, verify every local image
   reference resolves to a file on disk, and surface a count + remediation hint instead of
   letting silent image loss go unnoticed.

All four are opt-in-safe: the default (no `--incremental`) export is unchanged except for
the rendering-quality fixes and the diagnostic warning, which only make output cleaner and
loss more visible.

---

## 1. Incremental export — `--incremental`

### Problem
A full notebook export re-converts **every** page every run and writes to a new
timestamped folder. For large notebooks this is slow, and if a batch run fails partway
through, the whole export has to start again.

### What changed
A new `--incremental` flag changes three things:

- **Stable output folder.** Output goes to `md/<notebook>` (no timestamp suffix), so
  successive runs write to the same place.
- **A manifest** (`onenote-export-manifest.json`) is written at the root of that folder.
  It records, per page (keyed by the stable OneNote page id), the OneNote
  *last-modified* time, the relative output path, and the last export status.
- **Skip + resume.** A page is skipped when **all** of these hold:
  - it is present in the manifest with status `ok`,
  - its OneNote last-modified time is unchanged (±1s tolerance for serialisation drift),
  - its output `.md` file still exists on disk.

  The manifest is saved after **each** page, so a crash mid-run leaves a valid manifest and
  the next run resumes from where it stopped. The export folder is **not** wiped at the
  start of an incremental run.

When `--incremental` is **not** supplied, behaviour is exactly as before (timestamped
folder, full re-export, folder cleaned at start).

### How to use
```bash
# First run — full export into a stable folder, writes the manifest
OneNoteMdExporter.exe --notebook "My Notebook" --format 1 --incremental --no-input

# Later runs — only pages changed in OneNote since last time are re-converted
OneNoteMdExporter.exe --notebook "My Notebook" --format 1 --incremental --no-input
```

Typical second-run log:
```
Incremental: 142 page(s) skipped, 6 processed, 0 error(s).
```

### Design notes
- The skip key is the OneNote page id (a stable GUID), **not** the title or path, so renames
  and re-orderings don't cause spurious re-exports or stale duplicates.
- The manifest loader **never throws**: a corrupt or schema-mismatched manifest, or one
  belonging to a different notebook, is discarded and treated as an empty manifest (a full
  export), with a warning. Failing safe here means a bad manifest can never block an export.
- Atomic save (temp file + move) so an interrupted write can't corrupt the manifest.

### Files
| File | Change |
|---|---|
| `Services/Export/IncrementalManifest.cs` | **New.** Manifest model + load/skip/record/save logic. |
| `Infrastructure/AppSettings.cs` | New `IncrementalExport` setting. |
| `Program.cs` | New `--incremental` CLI option, wired to the setting. |
| `Services/Export/ExportServiceBase.cs` | Stable-folder selection, manifest lifecycle, `CanSkipPage` / `RecordPageExport` helpers. |
| `Services/Export/MdExportService.cs` | Phase-2 loop skips unchanged pages and records each result. |
| `Models/NotebookExportResult.cs` | New `PagesSkipped` counter. |

---

## 2. Image references — drop the GUID alt text

### Problem
Images were emitted with the OneNote-cache / PanDoc temp **file name** as alt text — an
opaque GUID, e.g.:
```markdown
![a3f9c2e1bd6f4a0e9c1d](_resources/a3f9c2e1bd6f4a0e9c1d.png)
```
That GUID is meaningless: it shows as gibberish when an image fails to load and is read
aloud verbatim by screen readers — worse than no alt text at all.

### What changed
Images now use an **empty** alt text, yielding a clean reference that renders identically
and degrades gracefully:
```markdown
![](_resources/a3f9c2e1bd6f4a0e9c1d.png)
```
Images nested inside an HTML table cell still use a raw `<img src="..." alt="" />` (GFM
cannot place a `![]()` reference inside a `<td>`), now also with an empty alt.

### Why not use the real OneNote alt text?
OneNote's page XML *does* carry a real image description — `<one:Image>` has an `alt`
attribute (OneNote 2013 schema). The problem is the export pipeline severs that attribute
from the image's identity before the two ever meet:

1. The page XML (with `alt`) is fetched first (`GetPageContent`).
2. The page is then **re-rendered to DocX** by OneNote (`Publish(..., pfWord)`). This render
   does **not** propagate the OneNote `alt` into the Word drawing's description
   (`wp:docPr/@descr`), and emits no OneNote object id or original path into the Word XML.
3. Pandoc converts that DocX to Markdown, extracting each image to a temp file. Because the
   DocX had no description, the emitted reference has no alt either — and the only handle is
   a content-hashed temp path.
4. Images are matched back **by that Pandoc temp path** (`processImgTag`); the `<one:Image>`
   XML node is never parsed (the XML attachment pass reads only `InsertedFile`/`MediaFile`).

So there is **no stable key** linking an XML `<one:Image>` to its DocX/Pandoc counterpart.
Recovering the real `alt` would require positionally zipping "XML image N ↔ DocX image N" by
relying on OneNote's internal image ordering — which desyncs on merged-cell tables, floating
images, and background images, producing **mislabelled** alt text. A wrong description read
aloud by a screen reader is worse than none, so the honest fix is an **empty** alt. (A robust
implementation would need OneNote to emit the description into the DocX, or a documented
object-id join key; neither exists today.)

### Files
| File | Change |
|---|---|
| `Services/Export/ExportServiceBase.cs` | `processImgTag` emits empty alt instead of the cache GUID, for both the `![]()` and the table-cell `<img>` paths. |

---

## 3. Complex tables — make the HTML fallback render correctly

### Problem
GFM pipe tables cannot represent merged cells (`colspan`/`rowspan`) or block content inside
cells. For such tables Pandoc correctly falls back to a raw HTML `<table>` block. But that
block was emitted **without** the blank-line separation that strict GFM/CommonMark
renderers (GitHub, Obsidian) require — so the table got absorbed into the adjacent
paragraph and rendered as literal `<table>...` markup.

### What changed
A new content-preserving post-processing pass, `NormalizeHtmlTableBlocks`, guarantees a
blank line **before** every `<table ...>` and **after** every `</table>`, then collapses any
accidental 3+ newline runs back to a single blank line.

It only adjusts whitespace **around** the table block — it never parses or rewrites the
table markup, so `colspan` / `rowspan` and nested cell content are preserved exactly.
Simple tables are unaffected (Pandoc already emits them as clean GFM pipe tables;
`--to=gfm` is the existing default).

Before (absorbed into the paragraph, renders as literal markup):
```markdown
Some lead-in text.<table><tr><td colspan="2">merged</td></tr></table>Trailing text.
```
After (recognised as an HTML block, renders as a real table):
```markdown
Some lead-in text.

<table><tr><td colspan="2">merged</td></tr></table>

Trailing text.
```

### Files
| File | Change |
|---|---|
| `Services/ConverterService.cs` | New `NormalizeHtmlTableBlocks`, called at the end of `PageMdPostConversion`. |

---

## 4. Broken-image self-check — make silent image loss observable

### Problem
OneNote images can fail to download during export (a known sync failure mode — the
project's own FAQ admits "Some of my images are lost / broken during export"). When that
happens the markdown still contains an `![](_resources/<guid>.png)` reference, but the
target file is missing on disk. Nothing warns the user: the export reports success and the
broken image is only discovered later, by eye, in a viewer.

### What changed
After each page's markdown is written, a new pass scans it for **local** image references
(both `![](path)` and raw `<img src="path">` forms) and checks each target exists on disk.
Every missing file is logged as a per-page warning naming the page and the path, and the
misses are tallied. At the end of the notebook a summary warning reports the total and the
fix:

```
Broken image in 'Q3 Planning': referenced file '_resources/8c1f….png' was not found on disk.
...
3 broken image reference(s) detected across the export - the referenced files are missing
on disk. Try enabling 'Download all files and images' in OneNote sync options, then re-export.
```

The count is also exposed on `NotebookExportResult.BrokenImageCount` for callers/automation.

This is **diagnostic only** — it never alters the exported markdown, and it does not change
the page's success/failure status (a page with a broken image still exports). It simply
turns a silent data-loss path into an observable signal (fail-loudly, not silently).

### Design notes
- **Local-only.** Remote (`http(s)://`, protocol-relative `//`), `data:` and `mailto:`
  references are ignored — they aren't files this exporter is responsible for.
- **Path-faithful.** The reference is `#`-fragment-stripped and percent-decoded before the
  existence check, so `_resources/a%20b.png` correctly matches the file `a b.png`.
- **De-duplicated.** The same broken path referenced twice on a page is reported once.
- **Unparseable path = broken.** A reference that can't be resolved to a filesystem path is
  treated as a broken reference rather than silently swallowed.

### Files
| File | Change |
|---|---|
| `Services/Export/ExportServiceBase.cs` | New `VerifyPageImages` + static `FindBrokenImageReferences`, called after `WritePageMdFile`; per-notebook `BrokenImagesInNotebook` counter (reset at the start of each notebook). |
| `Services/Export/MdExportService.cs` | Assigns the count to the result and logs the end-of-export summary warning with a remediation hint. |
| `Models/NotebookExportResult.cs` | New `BrokenImageCount` counter. |

---

## 5. Adopted upstream correctness fixes (cherry-picked, original authorship preserved)

Two small, single-purpose bug-fixes were open as upstream PRs against `alxnbl/onenote-md-exporter`
but unmerged (upstream `main` has not moved since 2025-12-15). Both are pure correctness fixes with
no behavioural surprise, so they were cherry-picked onto this branch with `git cherry-pick -x`
(provenance trailer retained) and their **original authors preserved** — only the committer is the
fork owner.

| # | Upstream PR | Author | File | Fix |
|---|---|---|---|---|
| 5a | [#142](https://github.com/alxnbl/onenote-md-exporter/pull/142) | Nic Jansma | `Services/Export/ExportServiceBase.cs` | `ConvertOnenoteTags` called `.First()` on a `<one:Tag>` whose parent had no `<one:T>` text element, throwing a LINQ "sequence contains no elements" exception and aborting the page. Now guarded: a tag with no associated text element is skipped. (Fail-loudly-compatible: it degrades one orphan tag instead of crashing the export.) |
| 5b | [#110](https://github.com/alxnbl/onenote-md-exporter/pull/110) | Rob Bernstein | `Helpers/OneNoteExtensions.cs` | The OneNote COM API mis-encodes certain characters in section-title XML, returning the literal two-char strings `^M` for `+` and `^J` for `,`. `FillNodebookSections` now restores them, so section/folder names with `+` or `,` export correctly instead of as `^M` / `^J`. |

Neither fix is exercised by the COM-free harness (both sit on XML/LINQ paths that need OneNote
interop), but each is a minimal, upstream-reviewed change applied verbatim. They are kept as
**separate commits** from the original work so they can be dropped or re-based independently.

---

## 6. Math / equations — already handled by Pandoc (no code, verification-gated)

### The question
OneNote pages can contain equations. Does the export carry them as editable LaTeX
(`$...$` / `$$...$$`), or are they lost / rasterised?

### What the pipeline already does
**No new code is needed for the common case** — the existing `OneNote → DocX → Pandoc → MD`
pipeline converts math for free, *provided the equation was authored as structured math*:

1. OneNote stores typed/Ink-to-Math equations as **OMML** (Office Math Markup), and a
   `Publish(..., pfWord)` carries that OMML into the Word `.docx` as editable math (not a picture).
2. Pandoc's DocX reader **natively parses OMML into its internal math AST**, and the Markdown/GFM
   writer emits it as TeX between `$` (inline) / `$$` (display) delimiters **by default**. No
   `--mathjax` / `--webtex` flag is required — those are HTML-writer options; the Markdown writer
   always uses raw `$` delimiters. The current Pandoc invocation (`--to=gfm --wrap=none
   --extract-media`) therefore already produces `$...$` for any OMML it receives.

So for equations authored with the OneNote equation editor (or Ink-to-Math), LaTeX output is
expected to *already work* on the current branch with zero changes.

### Why no `$`-escaping guard was added
A naive worry is that prose containing literal dollar amounts (`$20,000`) collides with math
delimiters. Two reasons this is left alone:

- **Pandoc already excludes it.** Pandoc's TeX-math rule does not open math when a `$` is followed
  by whitespace, and does not treat `$20,000` … `$30,000` as a math span (a closing `$` immediately
  before a digit is rejected). The upstream issue raised about this (#113) was closed by the owner
  as *not reproducible* on v1.6.
- **The post-processing passes don't touch `$` blocks.** I traced every transform in
  `PageMdPostConversion`: `DeduplicateLinebreaks` / `MaxTwoLineBreaksInARow` only collapse **3+**
  consecutive newlines (a normal `$$…$$` block uses single newlines, so it is untouched), and
  `RemoveQuotationBlocks` only rewrites lines beginning with `>` (math lines don't). None of them
  can corrupt a Pandoc-emitted `$$` block.

Adding a speculative guard would be a fix for a failure mode that doesn't reproduce — KISS says
don't write it.

### The one unverifiable-here fact (needs David's machine)
Everything above about Pandoc and the post-processing is verifiable statically and holds. The
**single** thing that cannot be confirmed in this headless environment is whether OneNote's
`Publish(pfWord)` on a *live* page actually emits OMML versus rasterising the equation to a PNG —
that depends on desktop OneNote + COM, which aren't available here. That distinction decides
between two honest outcomes:

- **OMML survives** → equations export as `$...$` LaTeX automatically; document it as a supported
  feature.
- **OneNote rasterises** → equations come out as images (no LaTeX recoverable); document that
  limitation plainly. **Do not fake LaTeX** by OCR-ing the picture.

This is recorded as a one-step smoke test below; no math claim is finalised until that test runs.

### Files
*No source change.* Math is an emergent property of the existing Pandoc step; this section exists
to document the behaviour and the single open verification.

---

## Verification

The full project requires desktop OneNote + Word via COM interop and builds only with
.NET-Framework MSBuild, so it can't be compiled or end-to-end tested in a headless
environment. The four changes above are, however, **pure string/data transforms with no
COM dependency**, so their logic was extracted into a standalone harness and unit-tested:

- Incremental manifest: skip/record/persist/reload, sub-second-drift tolerance,
  error-status retry, stale-notebook rejection, corrupt-manifest safety — **10 assertions**.
- Image alt-text emission (table and non-table paths) — **3 assertions**.
- HTML table normalization: blank-line insertion both sides, content/`colspan`/`rowspan`
  preservation, idempotency, no-op when no table — **6 assertions**.
- Broken-image detection: present-vs-missing discrimination, remote/data ignored, raw
  `<img src>` form checked, percent-decoding before existence check, de-duplication, no-op
  when no images — **7 assertions**.

**Result: 26/26 assertions pass** (built and run with the .NET 9 SDK).

### Remaining manual checks (require David's machine: desktop OneNote + Word)
1. **Full build** of the COM-linked project with Visual Studio / .NET-Framework MSBuild
   (`net10.0-windows7.0`, win-x86).
2. **Live incremental smoke test:** run with `--incremental` once (full export), run again
   unchanged (expect all pages skipped), edit one page in OneNote, run again (expect exactly
   that page re-exported), and a mid-run interruption + re-run (expect resume).
3. **Visual spot-check** of a page with images and a merged-cell table in a GFM viewer
   (GitHub / Obsidian).
4. **Math export check (§6):** create a OneNote page with one equation typed via the equation
   editor (e.g. `E = mc^2` or a fraction), export it, and inspect the `.md`. Expected: a `$...$`
   or `$$...$$` LaTeX span. If instead an image (`![](_resources/….png)`) appears, OneNote
   rasterised the equation — update §6 to record math as image-only (no LaTeX), and do **not**
   attempt OCR-to-LaTeX.
